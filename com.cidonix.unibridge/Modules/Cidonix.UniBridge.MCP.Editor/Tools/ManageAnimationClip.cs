#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static class ManageAnimationClip
    {
        const string ToolName = "UniBridge_ManageAnimationClip";
        const int DefaultLimit = 50;
        const int MaxLimit = 500;

        public const string Title = "Manage AnimationClip assets";

        public const string Description = @"Create, inspect, and edit Unity AnimationClip assets.

Use this when an agent needs actual .anim authoring, not only Animator Controller states/transitions. Supports clip settings, float curves, object-reference curves, and AnimationEvents for MCP workflows.

Args:
    Action: Search, Inspect, Create, CreateOrUpdate, SetCurves, SetEvents, ClearCurves, or ClearEvents.
    Path: Assets/... .anim path.
    FrameRate, Legacy, WrapMode, LoopTime: common clip settings.
    Curves: float curve bindings: { RelativePath, Type, Property, Keys:[{Time, Value, InTangent, OutTangent}] }.
    ObjectReferenceCurves: object reference bindings, useful for sprite animation.
    Events: Animation events: { Time, FunctionName, StringParameter, FloatParameter, IntParameter, ObjectReferencePath }.

Returns:
    success, message, and AnimationClip snapshot data.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new
                    {
                        type = "string",
                        description = "AnimationClip operation",
                        @enum = new[] { "Search", "Inspect", "Create", "CreateOrUpdate", "SetCurves", "SetEvents", "ClearCurves", "ClearEvents" }
                    },
                    Path = new { type = "string", description = "AnimationClip asset path, e.g. Assets/Animations/Walk.anim" },
                    Query = new { type = "string", description = "Search text for Search" },
                    Limit = new { type = "integer", description = "Maximum search results", @default = DefaultLimit },
                    FrameRate = new { type = "number", description = "Clip frame rate" },
                    Legacy = new { type = "boolean", description = "Set clip.legacy" },
                    WrapMode = new { type = "string", description = "WrapMode enum value, e.g. Loop, Once, ClampForever" },
                    LoopTime = new { type = "boolean", description = "AnimationClipSettings.loopTime" },
                    ClearExistingCurves = new { type = "boolean", description = "Clear existing float and object-reference curves before applying new curves", @default = false },
                    Curves = new { type = "array", description = "Float curve bindings", items = new { type = "object", additionalProperties = true } },
                    ObjectReferenceCurves = new { type = "array", description = "Object reference curve bindings", items = new { type = "object", additionalProperties = true } },
                    Events = new { type = "array", description = "Animation events", items = new { type = "object", additionalProperties = true } }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "assets", "animation" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = NormalizeKey(GetString(parameters, "Action", "action") ?? "Inspect");

            try
            {
                return action switch
                {
                    "search" or "list" => Search(parameters),
                    "inspect" or "getinfo" => Inspect(parameters),
                    "create" => CreateOrUpdate(parameters, createIfMissing: true, requireExisting: false),
                    "createorupdate" or "upsert" => CreateOrUpdate(parameters, createIfMissing: true, requireExisting: false),
                    "setcurves" or "setcurve" => CreateOrUpdate(parameters, createIfMissing: false, requireExisting: true),
                    "setevents" or "setevent" => CreateOrUpdate(parameters, createIfMissing: false, requireExisting: true, curvesOptional: true),
                    "clearcurves" => Clear(parameters, clearCurves: true, clearEvents: false),
                    "clearevents" => Clear(parameters, clearCurves: false, clearEvents: true),
                    _ => Response.Error($"Unknown Action '{GetString(parameters, "Action", "action")}'. Supported: Search, Inspect, Create, CreateOrUpdate, SetCurves, SetEvents, ClearCurves, ClearEvents.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageAnimationClip] Action '{action}' failed: {ex}");
                return Response.Error($"AnimationClip action '{action}' failed: {ex.Message}");
            }
        }

        static object Search(JObject parameters)
        {
            var query = GetString(parameters, "Query", "query", "Search", "search");
            var limit = Mathf.Clamp(GetInt(parameters, DefaultLimit, "Limit", "limit"), 1, MaxLimit);
            var paths = AssetDatabase.FindAssets("t:AnimationClip")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(path => string.IsNullOrWhiteSpace(query) ||
                               path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               Path.GetFileNameWithoutExtension(path).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(path =>
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    return BuildClipSummary(path, clip);
                })
                .ToArray();

            return Response.Success("Found AnimationClip assets.", new { query, count = paths.Length, clips = paths });
        }

        static object Inspect(JObject parameters)
        {
            var path = ResolveClipPath(parameters);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return Response.Error($"AnimationClip was not found at '{path}'.");

            return Response.Success($"Inspected AnimationClip '{path}'.", BuildClipSnapshot(path, clip));
        }

        static object CreateOrUpdate(JObject parameters, bool createIfMissing, bool requireExisting, bool curvesOptional = false)
        {
            var path = ResolveClipPath(parameters);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            var created = false;

            if (clip == null)
            {
                if (requireExisting || !createIfMissing)
                    return Response.Error($"AnimationClip was not found at '{path}'.");

                EnsureParentDirectoryExists(path);
                clip = new AnimationClip();
                AssetDatabase.CreateAsset(clip, path);
                created = true;
            }
            else
            {
                VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true);
            }

            Undo.RecordObject(clip, "Manage AnimationClip");

            ApplyBasicSettings(clip, parameters);
            ApplyClipSettings(clip, parameters);

            if (GetBool(parameters, false, "ClearExistingCurves", "clearExistingCurves", "clear_existing_curves"))
                ClearCurvesInternal(clip);

            var curves = GetArray(parameters, "Curves", "curves");
            var objectReferenceCurves = GetArray(parameters, "ObjectReferenceCurves", "objectReferenceCurves", "object_reference_curves");
            var events = GetArray(parameters, "Events", "events");

            if (!curvesOptional && curves == null && objectReferenceCurves == null && events == null && !HasSetting(parameters))
                return Response.Error("No clip settings, Curves, ObjectReferenceCurves, or Events were provided.");

            var appliedCurves = curves == null ? 0 : ApplyFloatCurves(clip, curves);
            var appliedObjectCurves = objectReferenceCurves == null ? 0 : ApplyObjectReferenceCurves(clip, objectReferenceCurves);
            var appliedEvents = events == null ? (int?)null : ApplyEvents(clip, events);

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return Response.Success(
                created ? $"AnimationClip '{path}' created." : $"AnimationClip '{path}' updated.",
                new
                {
                    path,
                    created,
                    appliedCurves,
                    appliedObjectReferenceCurves = appliedObjectCurves,
                    appliedEvents,
                    clip = BuildClipSnapshot(path, clip)
                });
        }

        static object Clear(JObject parameters, bool clearCurves, bool clearEvents)
        {
            var path = ResolveClipPath(parameters);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                return Response.Error($"AnimationClip was not found at '{path}'.");

            VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true);
            Undo.RecordObject(clip, "Clear AnimationClip Data");

            if (clearCurves)
                ClearCurvesInternal(clip);
            if (clearEvents)
                AnimationUtility.SetAnimationEvents(clip, Array.Empty<AnimationEvent>());

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return Response.Success($"AnimationClip '{path}' cleared.", BuildClipSnapshot(path, clip));
        }

        static void ApplyBasicSettings(AnimationClip clip, JObject parameters)
        {
            if (TryGetFloat(parameters, out var frameRate, "FrameRate", "frameRate", "frame_rate"))
                clip.frameRate = Mathf.Max(1f, frameRate);

            if (TryGetBool(parameters, out var legacy, "Legacy", "legacy"))
                clip.legacy = legacy;

            var wrapMode = GetString(parameters, "WrapMode", "wrapMode", "wrap_mode");
            if (!string.IsNullOrWhiteSpace(wrapMode))
            {
                if (!Enum.TryParse(wrapMode, true, out WrapMode parsed))
                    throw new InvalidOperationException($"Unsupported WrapMode '{wrapMode}'.");
                clip.wrapMode = parsed;
            }
        }

        static void ApplyClipSettings(AnimationClip clip, JObject parameters)
        {
            var settingsToken = parameters["Settings"] as JObject ??
                                parameters["settings"] as JObject ??
                                new JObject();

            if (parameters.TryGetValue("LoopTime", StringComparison.OrdinalIgnoreCase, out var loopTime) ||
                parameters.TryGetValue("loop_time", StringComparison.OrdinalIgnoreCase, out loopTime) ||
                parameters.TryGetValue("loopTime", StringComparison.OrdinalIgnoreCase, out loopTime))
            {
                settingsToken["loopTime"] = loopTime.DeepClone();
            }

            if (!settingsToken.HasValues)
                return;

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            ApplySetting(settingsToken, "loopTime", value => settings.loopTime = value);
            ApplySetting(settingsToken, "loopBlend", value => settings.loopBlend = value);
            ApplySetting(settingsToken, "loopBlendOrientation", value => settings.loopBlendOrientation = value);
            ApplySetting(settingsToken, "loopBlendPositionY", value => settings.loopBlendPositionY = value);
            ApplySetting(settingsToken, "loopBlendPositionXZ", value => settings.loopBlendPositionXZ = value);
            ApplySetting(settingsToken, "keepOriginalOrientation", value => settings.keepOriginalOrientation = value);
            ApplySetting(settingsToken, "keepOriginalPositionY", value => settings.keepOriginalPositionY = value);
            ApplySetting(settingsToken, "keepOriginalPositionXZ", value => settings.keepOriginalPositionXZ = value);
            AnimationUtility.SetAnimationClipSettings(clip, settings);
        }

        static int ApplyFloatCurves(AnimationClip clip, JArray curves)
        {
            var count = 0;
            foreach (var token in curves)
            {
                if (token is not JObject spec)
                    throw new InvalidOperationException("Each curve spec must be an object.");

                var binding = BuildBinding(spec);
                var curve = BuildAnimationCurve(spec, clip.frameRate);
                AnimationUtility.SetEditorCurve(clip, binding, curve);
                count++;
            }

            return count;
        }

        static int ApplyObjectReferenceCurves(AnimationClip clip, JArray curves)
        {
            var count = 0;
            foreach (var token in curves)
            {
                if (token is not JObject spec)
                    throw new InvalidOperationException("Each object reference curve spec must be an object.");

                var binding = BuildBinding(spec);
                var keys = GetArray(spec, "Keys", "keys");
                if (keys == null)
                    throw new InvalidOperationException($"Object reference curve '{binding.propertyName}' requires Keys.");

                var frames = keys.OfType<JObject>().Select(key =>
                {
                    var frame = new ObjectReferenceKeyframe
                    {
                        time = GetKeyTime(key, clip.frameRate),
                        value = LoadObjectReference(key)
                    };
                    return frame;
                }).ToArray();

                AnimationUtility.SetObjectReferenceCurve(clip, binding, frames);
                count++;
            }

            return count;
        }

        static int ApplyEvents(AnimationClip clip, JArray events)
        {
            var result = new List<AnimationEvent>();
            foreach (var token in events)
            {
                if (token is not JObject spec)
                    throw new InvalidOperationException("Each event spec must be an object.");

                var functionName = GetString(spec, "FunctionName", "functionName", "function_name", "function");
                if (string.IsNullOrWhiteSpace(functionName))
                    throw new InvalidOperationException("AnimationEvent requires FunctionName.");

                result.Add(new AnimationEvent
                {
                    time = GetFloat(spec, 0f, "Time", "time"),
                    functionName = functionName,
                    stringParameter = GetString(spec, "StringParameter", "stringParameter", "string_parameter"),
                    floatParameter = GetFloat(spec, 0f, "FloatParameter", "floatParameter", "float_parameter"),
                    intParameter = GetInt(spec, 0, "IntParameter", "intParameter", "int_parameter"),
                    objectReferenceParameter = LoadOptionalObjectReference(spec)
                });
            }

            AnimationUtility.SetAnimationEvents(clip, result.ToArray());
            return result.Count;
        }

        static EditorCurveBinding BuildBinding(JObject spec)
        {
            var property = GetString(spec, "Property", "property", "propertyName", "property_name");
            if (string.IsNullOrWhiteSpace(property))
                throw new InvalidOperationException("Curve binding requires Property.");

            return new EditorCurveBinding
            {
                path = GetString(spec, "RelativePath", "relativePath", "relative_path", "Path", "path") ?? string.Empty,
                type = ResolveBindingType(GetString(spec, "Type", "type", "ComponentType", "componentType", "component_type")),
                propertyName = property
            };
        }

        static AnimationCurve BuildAnimationCurve(JObject spec, float frameRate)
        {
            var keys = GetArray(spec, "Keys", "keys");
            if (keys == null || keys.Count == 0)
                throw new InvalidOperationException("Float curve requires at least one key.");

            var keyframes = keys.OfType<JObject>().Select(key =>
            {
                var frame = new Keyframe(
                    GetKeyTime(key, frameRate),
                    GetFloat(key, 0f, "Value", "value"),
                    GetFloat(key, 0f, "InTangent", "inTangent", "in_tangent"),
                    GetFloat(key, 0f, "OutTangent", "outTangent", "out_tangent"));

                if (TryGetFloat(key, out var inWeight, "InWeight", "inWeight", "in_weight"))
                    frame.inWeight = inWeight;
                if (TryGetFloat(key, out var outWeight, "OutWeight", "outWeight", "out_weight"))
                    frame.outWeight = outWeight;

                var weightedMode = GetString(key, "WeightedMode", "weightedMode", "weighted_mode");
                if (!string.IsNullOrWhiteSpace(weightedMode) && Enum.TryParse(weightedMode, true, out WeightedMode parsed))
                    frame.weightedMode = parsed;

                return frame;
            }).ToArray();

            var curve = new AnimationCurve(keyframes);
            var preWrap = GetString(spec, "PreWrapMode", "preWrapMode", "pre_wrap_mode");
            if (!string.IsNullOrWhiteSpace(preWrap) && Enum.TryParse(preWrap, true, out WrapMode pre))
                curve.preWrapMode = pre;
            var postWrap = GetString(spec, "PostWrapMode", "postWrapMode", "post_wrap_mode");
            if (!string.IsNullOrWhiteSpace(postWrap) && Enum.TryParse(postWrap, true, out WrapMode post))
                curve.postWrapMode = post;
            return curve;
        }

        static void ClearCurvesInternal(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                AnimationUtility.SetEditorCurve(clip, binding, null);
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
        }

        static object BuildClipSummary(string path, AnimationClip clip)
        {
            return new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                name = clip == null ? Path.GetFileNameWithoutExtension(path) : clip.name,
                length = clip?.length,
                frameRate = clip?.frameRate,
                legacy = clip?.legacy
            };
        }

        static object BuildClipSnapshot(string path, AnimationClip clip)
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            return new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                name = clip.name,
                length = clip.length,
                frameRate = clip.frameRate,
                legacy = clip.legacy,
                wrapMode = clip.wrapMode.ToString(),
                empty = clip.empty,
                settings = new
                {
                    settings.loopTime,
                    settings.loopBlend,
                    settings.loopBlendOrientation,
                    settings.loopBlendPositionY,
                    settings.loopBlendPositionXZ,
                    settings.keepOriginalOrientation,
                    settings.keepOriginalPositionY,
                    settings.keepOriginalPositionXZ
                },
                curveBindings = AnimationUtility.GetCurveBindings(clip).Select(binding =>
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    return new
                    {
                        relativePath = binding.path,
                        type = binding.type?.FullName,
                        property = binding.propertyName,
                        keyCount = curve?.keys.Length ?? 0
                    };
                }).ToArray(),
                objectReferenceCurveBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip).Select(binding =>
                {
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    return new
                    {
                        relativePath = binding.path,
                        type = binding.type?.FullName,
                        property = binding.propertyName,
                        keyCount = curve?.Length ?? 0
                    };
                }).ToArray(),
                events = AnimationUtility.GetAnimationEvents(clip).Select(evt => new
                {
                    evt.time,
                    functionName = evt.functionName,
                    evt.stringParameter,
                    evt.floatParameter,
                    evt.intParameter,
                    objectReferencePath = evt.objectReferenceParameter == null ? null : AssetDatabase.GetAssetPath(evt.objectReferenceParameter)
                }).ToArray()
            };
        }

        static UnityEngine.Object LoadObjectReference(JObject key)
        {
            var path = GetString(key, "AssetPath", "assetPath", "asset_path", "ObjectPath", "objectPath", "object_reference_path");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = NormalizeAssetPath(path);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalized);
            if (asset == null)
                throw new InvalidOperationException($"Object reference asset was not found at '{normalized}'.");
            return asset;
        }

        static UnityEngine.Object LoadOptionalObjectReference(JObject spec)
        {
            var path = GetString(spec, "ObjectReferencePath", "objectReferencePath", "object_reference_path", "AssetPath", "assetPath");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = NormalizeAssetPath(path);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalized);
            if (asset == null)
                throw new InvalidOperationException($"AnimationEvent object reference was not found at '{normalized}'.");
            return asset;
        }

        static Type ResolveBindingType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName) ||
                string.Equals(typeName, "Transform", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "UnityEngine.Transform", StringComparison.OrdinalIgnoreCase))
                return typeof(Transform);

            if (string.Equals(typeName, "RectTransform", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "UnityEngine.RectTransform", StringComparison.OrdinalIgnoreCase))
                return typeof(RectTransform);

            if (ComponentResolver.TryResolve(typeName, out var componentType, out _))
                return componentType;

            var resolved = Type.GetType(typeName, throwOnError: false) ??
                           AppDomain.CurrentDomain.GetAssemblies()
                               .Select(assembly => assembly.GetType(typeName, throwOnError: false))
                               .FirstOrDefault(type => type != null);

            if (resolved != null && typeof(Component).IsAssignableFrom(resolved))
                return resolved;

            throw new InvalidOperationException($"Could not resolve AnimationCurve binding component type '{typeName}'.");
        }

        static string ResolveClipPath(JObject parameters)
        {
            var path = NormalizeAssetPath(GetString(parameters, "Path", "path", "AssetPath", "assetPath", "asset_path"));
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Path is required.");
            if (!path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AnimationClip path must end with .anim.");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AnimationClip authoring is only allowed under Assets/.");
            return path;
        }

        static string NormalizeAssetPath(string path)
        {
            return VersionControlUtility.NormalizeAssetPath(path);
        }

        static void EnsureParentDirectoryExists(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || AssetDatabase.IsValidFolder(directory))
                return;

            Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), directory));
            AssetDatabase.Refresh();
        }

        static bool HasSetting(JObject parameters)
        {
            return GetToken(parameters, "FrameRate", "frameRate", "frame_rate") != null ||
                   GetToken(parameters, "Legacy", "legacy") != null ||
                   GetToken(parameters, "WrapMode", "wrapMode", "wrap_mode") != null ||
                   GetToken(parameters, "LoopTime", "loopTime", "loop_time") != null ||
                   GetToken(parameters, "Settings", "settings") != null;
        }

        static float GetKeyTime(JObject key, float frameRate)
        {
            if (TryGetFloat(key, out var time, "Time", "time"))
                return time;
            if (TryGetFloat(key, out var frame, "Frame", "frame"))
                return frame / Mathf.Max(1f, frameRate);
            return 0f;
        }

        static void ApplySetting(JObject settings, string key, Action<bool> setter)
        {
            if (TryGetBool(settings, out var value, key))
                setter(value);
        }

        static JArray GetArray(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token is JArray array)
                    return array;
            }

            return null;
        }

        static JToken GetToken(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                    return token;
            }

            return null;
        }

        static string GetString(JObject obj, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token?.ToString();
        }

        static int GetInt(JObject obj, int fallback, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && int.TryParse(token.ToString(), out var value) ? value : fallback;
        }

        static float GetFloat(JObject obj, float fallback, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && float.TryParse(token.ToString(), out var value) ? value : fallback;
        }

        static bool GetBool(JObject obj, bool fallback, params string[] keys)
        {
            return TryGetBool(obj, out var value, keys) ? value : fallback;
        }

        static bool TryGetFloat(JObject obj, out float value, params string[] keys)
        {
            var token = GetToken(obj, keys);
            if (token != null && float.TryParse(token.ToString(), out value))
                return true;
            value = 0f;
            return false;
        }

        static bool TryGetBool(JObject obj, out bool value, params string[] keys)
        {
            var token = GetToken(obj, keys);
            if (token != null && bool.TryParse(token.ToString(), out value))
                return true;
            value = false;
            return false;
        }

        static string NormalizeKey(string value)
        {
            return (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }
    }
}
