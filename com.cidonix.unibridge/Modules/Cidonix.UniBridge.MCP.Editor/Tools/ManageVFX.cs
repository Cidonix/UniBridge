#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// VFX and animated visual component authoring helpers.
    /// </summary>
    public static class ManageVFX
    {
        public const string Title = "Manage VFX scene components";

        public const string Description = @"Create, inspect, and configure scene VFX components with practical presets.

Actions:
    Inspect: Summarize ParticleSystem, TrailRenderer, LineRenderer, VisualEffect, and VideoPlayer components.
    AddParticleSystem: Add/update a ParticleSystem and ParticleSystemRenderer.
    AddTrailRenderer: Add/update a TrailRenderer.
    AddLineRenderer: Add/update a LineRenderer.
    AddVisualEffect: Add/update UnityEngine.VFX.VisualEffect when the VFX package/module is available.
    AddVideoPlayer: Add/update UnityEngine.Video.VideoPlayer when the Video module is available.
    ApplyPreset: SimpleBurst, LoopingAura, Trail, Line, or VideoBillboard.

Args:
    Target/SearchMethod: GameObject to configure. If missing and Name is supplied, a GameObject is created.
    Preset, MaterialPath, VisualEffectAssetPath, VideoClipPath, Url, Duration, StartLifetime, StartSpeed, StartSize, Color, Loop, EmissionRate, Width, Positions, Properties.

Returns:
    success, message, and a VFX-focused component summary.";

        [McpTool("UniBridge_ManageVFX", Description, Title, Groups = new[] { "core", "scene", "vfx" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            try
            {
                var action = GetString(parameters, "Action", "action") ?? "Inspect";
                return Normalize(action) switch
                {
                    "inspect" => Inspect(parameters),
                    "addparticlesystem" => ConfigureParticleSystem(parameters, preset: null),
                    "addtrailrenderer" => ConfigureTrail(parameters, preset: null),
                    "addlinerenderer" => ConfigureLine(parameters, preset: null),
                    "addvisualeffect" => ConfigureVisualEffect(parameters),
                    "addvideoplayer" => ConfigureVideoPlayer(parameters),
                    "applypreset" => ApplyPreset(parameters),
                    _ => Response.Error($"Unsupported VFX action '{action}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageVFX] failed: {ex}");
                return Response.Error($"ManageVFX failed: {ex.Message}");
            }
        }

        static object Inspect(JObject parameters)
        {
            var go = ResolveTarget(parameters, createIfMissing: false);
            return go == null
                ? Response.Error("Inspect requires Target or selected GameObject.")
                : Response.Success($"Inspected VFX setup on '{go.name}'.", BuildVfxSummary(go));
        }

        static object ApplyPreset(JObject parameters)
        {
            var preset = Normalize(GetString(parameters, "Preset", "preset") ?? "SimpleBurst");
            return preset switch
            {
                "simpleburst" => ConfigureParticleSystem(parameters, "SimpleBurst"),
                "loopingaura" => ConfigureParticleSystem(parameters, "LoopingAura"),
                "trail" => ConfigureTrail(parameters, "Trail"),
                "line" => ConfigureLine(parameters, "Line"),
                "videobillboard" => ConfigureVideoPlayer(parameters),
                _ => Response.Error($"Unsupported VFX preset '{GetString(parameters, "Preset", "preset")}'.")
            };
        }

        static object ConfigureParticleSystem(JObject parameters, string preset)
        {
            var go = ResolveTarget(parameters, createIfMissing: true);
            if (go == null)
                return Response.Error("AddParticleSystem/ApplyPreset requires Target, selected GameObject, or Name for creation.");

            var particleSystem = GetOrAddComponent<ParticleSystem>(go);
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
                renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
                renderer = Undo.AddComponent<ParticleSystemRenderer>(go);
            Undo.RecordObject(particleSystem, "Configure ParticleSystem");
            Undo.RecordObject(renderer, "Configure ParticleSystemRenderer");

            if (particleSystem.isPlaying || particleSystem.isEmitting || particleSystem.particleCount > 0)
                particleSystem.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = particleSystem.main;
            var emission = particleSystem.emission;
            var shape = particleSystem.shape;
            var mainSerializedPatch = new JObject();

            if (preset == "SimpleBurst")
            {
                main.loop = false;
                main.duration = 1f;
                main.startLifetime = 0.75f;
                main.startSpeed = 2.5f;
                main.startSize = 0.18f;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 18) });
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.12f;
            }
            else if (preset == "LoopingAura")
            {
                main.loop = true;
                main.duration = 2f;
                main.startLifetime = 1.4f;
                main.startSpeed = 0.35f;
                main.startSize = 0.22f;
                emission.rateOverTime = 18f;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.radius = 0.8f;
            }

            if (TryGetFloat(parameters, out var duration, "Duration", "duration"))
            {
                main.duration = Mathf.Max(0.01f, duration);
                mainSerializedPatch["lengthInSec"] = Mathf.Max(0.01f, duration);
            }
            if (TryGetBool(parameters, out var loop, "Loop", "loop"))
            {
                main.loop = loop;
                mainSerializedPatch["InitialModule.loop"] = loop;
            }
            if (TryGetFloat(parameters, out var lifetime, "StartLifetime", "startLifetime", "start_lifetime"))
            {
                main.startLifetime = Mathf.Max(0.01f, lifetime);
                mainSerializedPatch["InitialModule.startLifetime.scalar"] = Mathf.Max(0.01f, lifetime);
            }
            if (TryGetFloat(parameters, out var speed, "StartSpeed", "startSpeed", "start_speed"))
            {
                main.startSpeed = speed;
                mainSerializedPatch["InitialModule.startSpeed.scalar"] = speed;
            }
            if (TryGetFloat(parameters, out var size, "StartSize", "startSize", "start_size"))
            {
                main.startSize = Mathf.Max(0.001f, size);
                mainSerializedPatch["InitialModule.startSize.scalar"] = Mathf.Max(0.001f, size);
            }
            if (TryGetFloat(parameters, out var rate, "EmissionRate", "emissionRate", "emission_rate"))
                emission.rateOverTime = Mathf.Max(0f, rate);
            if (TryReadColor(parameters["Color"] ?? parameters["color"], out var color))
            {
                main.startColor = color;
                mainSerializedPatch["InitialModule.startColor.maxColor"] = JArray.FromObject(new[] { color.r, color.g, color.b, color.a });
                mainSerializedPatch["InitialModule.startColor.minColor"] = JArray.FromObject(new[] { color.r, color.g, color.b, color.a });
            }

            ApplyMaterial(renderer, parameters);
            ApplySerializedProperties(particleSystem, mainSerializedPatch);
            ApplySerializedProperties(particleSystem, parameters["Properties"] as JObject ?? parameters["properties"] as JObject);
            EditorUtility.SetDirty(particleSystem);
            EditorUtility.SetDirty(renderer);
            MarkSceneDirty(go);

            return Response.Success($"Configured ParticleSystem on '{go.name}'.", BuildVfxSummary(go));
        }

        static object ConfigureTrail(JObject parameters, string preset)
        {
            var go = ResolveTarget(parameters, createIfMissing: true);
            if (go == null)
                return Response.Error("AddTrailRenderer/ApplyPreset requires Target, selected GameObject, or Name for creation.");

            var trail = GetOrAddComponent<TrailRenderer>(go);
            Undo.RecordObject(trail, "Configure TrailRenderer");
            if (preset == "Trail")
            {
                trail.time = 0.75f;
                trail.minVertexDistance = 0.05f;
                trail.widthMultiplier = 0.18f;
                trail.emitting = true;
            }

            if (TryGetFloat(parameters, out var width, "Width", "width"))
                trail.widthMultiplier = Mathf.Max(0.001f, width);
            if (TryGetFloat(parameters, out var time, "Duration", "duration", "Time", "time"))
                trail.time = Mathf.Max(0.001f, time);
            if (TryReadColor(parameters["Color"] ?? parameters["color"], out var color))
            {
                trail.startColor = color;
                trail.endColor = new Color(color.r, color.g, color.b, 0f);
            }

            ApplyMaterial(trail, parameters);
            ApplySerializedProperties(trail, parameters["Properties"] as JObject ?? parameters["properties"] as JObject);
            EditorUtility.SetDirty(trail);
            MarkSceneDirty(go);
            return Response.Success($"Configured TrailRenderer on '{go.name}'.", BuildVfxSummary(go));
        }

        static object ConfigureLine(JObject parameters, string preset)
        {
            var go = ResolveTarget(parameters, createIfMissing: true);
            if (go == null)
                return Response.Error("AddLineRenderer/ApplyPreset requires Target, selected GameObject, or Name for creation.");

            var line = GetOrAddComponent<LineRenderer>(go);
            Undo.RecordObject(line, "Configure LineRenderer");
            if (preset == "Line" && line.positionCount < 2)
            {
                line.positionCount = 2;
                line.SetPosition(0, new Vector3(-0.5f, 0f, 0f));
                line.SetPosition(1, new Vector3(0.5f, 0f, 0f));
                line.widthMultiplier = 0.06f;
                line.useWorldSpace = false;
            }

            if (TryReadPositions(parameters["Positions"] ?? parameters["positions"], out var positions) && positions.Length > 0)
            {
                line.positionCount = positions.Length;
                for (var i = 0; i < positions.Length; i++)
                    line.SetPosition(i, positions[i]);
            }

            if (TryGetFloat(parameters, out var width, "Width", "width"))
                line.widthMultiplier = Mathf.Max(0.001f, width);
            if (TryReadColor(parameters["Color"] ?? parameters["color"], out var color))
            {
                line.startColor = color;
                line.endColor = color;
            }

            ApplyMaterial(line, parameters);
            ApplySerializedProperties(line, parameters["Properties"] as JObject ?? parameters["properties"] as JObject);
            EditorUtility.SetDirty(line);
            MarkSceneDirty(go);
            return Response.Success($"Configured LineRenderer on '{go.name}'.", BuildVfxSummary(go));
        }

        static object ConfigureVisualEffect(JObject parameters)
        {
            var visualEffectType = FindType("UnityEngine.VFX.VisualEffect");
            if (visualEffectType == null)
                return Response.Error("UnityEngine.VFX.VisualEffect is not available in this project.");

            var go = ResolveTarget(parameters, createIfMissing: true);
            if (go == null)
                return Response.Error("AddVisualEffect requires Target, selected GameObject, or Name for creation.");

            var component = GetOrAddComponent(go, visualEffectType);
            var assetPath = GetString(parameters, "VisualEffectAssetPath", "visualEffectAssetPath", "vfxAssetPath", "AssetPath", "assetPath");
            if (!string.IsNullOrWhiteSpace(assetPath))
                SetMemberValue(component, "visualEffectAsset", LoadAsset<Object>(assetPath));

            ApplySerializedProperties(component, parameters["Properties"] as JObject ?? parameters["properties"] as JObject);
            EditorUtility.SetDirty(component);
            MarkSceneDirty(go);
            return Response.Success($"Configured VisualEffect on '{go.name}'.", BuildVfxSummary(go));
        }

        static object ConfigureVideoPlayer(JObject parameters)
        {
            var videoPlayerType = FindType("UnityEngine.Video.VideoPlayer");
            if (videoPlayerType == null)
                return Response.Error("UnityEngine.Video.VideoPlayer is not available in this project.");

            var go = ResolveTarget(parameters, createIfMissing: true);
            if (go == null)
                return Response.Error("AddVideoPlayer requires Target, selected GameObject, or Name for creation.");

            var component = GetOrAddComponent(go, videoPlayerType);
            var clipPath = GetString(parameters, "VideoClipPath", "videoClipPath", "clipPath");
            if (!string.IsNullOrWhiteSpace(clipPath))
                SetMemberValue(component, "clip", LoadAsset<Object>(clipPath));
            var url = GetString(parameters, "Url", "URL", "url");
            if (!string.IsNullOrWhiteSpace(url))
                SetMemberValue(component, "url", url);
            if (TryGetBool(parameters, out var playOnAwake, "PlayOnAwake", "playOnAwake", "play_on_awake"))
                SetMemberValue(component, "playOnAwake", playOnAwake);
            if (TryGetBool(parameters, out var loop, "Loop", "loop", "isLooping"))
                SetMemberValue(component, "isLooping", loop);

            ApplySerializedProperties(component, parameters["Properties"] as JObject ?? parameters["properties"] as JObject);
            EditorUtility.SetDirty(component);
            MarkSceneDirty(go);
            return Response.Success($"Configured VideoPlayer on '{go.name}'.", BuildVfxSummary(go));
        }

        static object BuildVfxSummary(GameObject go)
        {
            return new
            {
                target = ToGameObjectInfo(go),
                particleSystems = go.GetComponents<ParticleSystem>().Select(ps =>
                {
                    var main = ps.main;
                    var emission = ps.emission;
                    return new
                    {
                        enabled = ps.isEmitting || ps.isPlaying || ps.isStopped,
                        isPlaying = ps.isPlaying,
                        particleCount = ps.particleCount,
                        duration = main.duration,
                        loop = main.loop,
                        startLifetime = main.startLifetime.constant,
                        startSpeed = main.startSpeed.constant,
                        startSize = main.startSize.constant,
                        emissionRate = emission.rateOverTime.constant
                    };
                }).ToArray(),
                trails = go.GetComponents<TrailRenderer>().Select(trail => new
                {
                    enabled = trail.enabled,
                    emitting = trail.emitting,
                    time = trail.time,
                    width = trail.widthMultiplier,
                    material = ToAssetInfo(trail.sharedMaterial)
                }).ToArray(),
                lines = go.GetComponents<LineRenderer>().Select(line => new
                {
                    enabled = line.enabled,
                    positionCount = line.positionCount,
                    width = line.widthMultiplier,
                    loop = line.loop,
                    material = ToAssetInfo(line.sharedMaterial)
                }).ToArray(),
                visualEffects = GetComponentsByTypeName(go, "UnityEngine.VFX.VisualEffect").Select(SummarizeReflectedComponent).ToArray(),
                videoPlayers = GetComponentsByTypeName(go, "UnityEngine.Video.VideoPlayer").Select(SummarizeReflectedComponent).ToArray()
            };
        }

        static void ApplyMaterial(Renderer renderer, JObject parameters)
        {
            var materialPath = GetString(parameters, "MaterialPath", "materialPath", "material");
            if (!string.IsNullOrWhiteSpace(materialPath))
                renderer.sharedMaterial = LoadAsset<Material>(materialPath);
        }

        static void ApplySerializedProperties(Component component, JObject properties)
        {
            if (component == null || properties == null || !properties.HasValues)
                return;

            Undo.RecordObject(component, "Patch VFX Component");
            using var serializedObject = new SerializedObject(component);
            serializedObject.Update();
            foreach (var prop in properties.Properties())
                SerializedPropertyPatcher.TryApplyProperty(component, serializedObject, prop.Name, prop.Value, dryRun: false);
            serializedObject.ApplyModifiedProperties();
        }

        static GameObject ResolveTarget(JObject parameters, bool createIfMissing)
        {
            var target = GetString(parameters, "Target", "target");
            var method = GetString(parameters, "SearchMethod", "searchMethod", "search_method");
            if (!string.IsNullOrWhiteSpace(target))
            {
                return SceneObjectLocator.FindObject(target, method, new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            }

            var name = GetString(parameters, "Name", "name");
            if (createIfMissing && !string.IsNullOrWhiteSpace(name))
            {
                var named = SceneObjectLocator.FindObject(name.Trim(), "by_name", new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
                if (named != null)
                {
                    return named;
                }

                var created = new GameObject(name.Trim());
                Undo.RegisterCreatedObjectUndo(created, "Create VFX GameObject");
                return created;
            }

            var go = Selection.activeGameObject;
            if (go != null || !createIfMissing)
                return go;

            if (string.IsNullOrWhiteSpace(name))
                return null;

            go = new GameObject(name.Trim());
            Undo.RegisterCreatedObjectUndo(go, "Create VFX GameObject");
            return go;
        }

        static T GetOrAddComponent<T>(GameObject go) where T : Component
        {
            var component = go.GetComponent<T>();
            if (component == null)
                component = Undo.AddComponent<T>(go);
            return component;
        }

        static Component GetOrAddComponent(GameObject go, Type type)
        {
            var component = go.GetComponent(type);
            if (component == null)
                component = Undo.AddComponent(go, type);
            return component;
        }

        static Component[] GetComponentsByTypeName(GameObject go, string fullName)
        {
            return go.GetComponents<Component>()
                .Where(component => component != null && string.Equals(component.GetType().FullName, fullName, StringComparison.Ordinal))
                .ToArray();
        }

        static object SummarizeReflectedComponent(Component component)
        {
            return new
            {
                type = component.GetType().FullName,
                enabled = GetMemberValue(component, "enabled"),
                asset = ToAssetInfo(GetMemberValue(component, "visualEffectAsset") as Object ?? GetMemberValue(component, "clip") as Object),
                url = GetMemberValue(component, "url"),
                playOnAwake = GetMemberValue(component, "playOnAwake"),
                isLooping = GetMemberValue(component, "isLooping")
            };
        }

        static bool TryReadPositions(JToken token, out Vector3[] positions)
        {
            positions = Array.Empty<Vector3>();
            if (token is not JArray array)
                return false;

            var list = new List<Vector3>();
            foreach (var item in array)
            {
                if (TryReadVector3(item, out var position))
                    list.Add(position);
            }

            positions = list.ToArray();
            return positions.Length > 0;
        }

        static bool TryReadVector3(JToken token, out Vector3 value)
        {
            value = default;
            if (token is JArray array && array.Count >= 3)
            {
                value = new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
                return true;
            }

            if (token is JObject obj)
            {
                value = new Vector3(obj.Value<float?>("x") ?? 0f, obj.Value<float?>("y") ?? 0f, obj.Value<float?>("z") ?? 0f);
                return true;
            }

            return false;
        }

        static bool TryReadColor(JToken token, out Color color)
        {
            color = Color.white;
            if (token is JArray array && array.Count >= 3)
            {
                var max = array.Take(Math.Min(array.Count, 4)).Max(item => item.Value<float>());
                var scale = max > 1f ? 255f : 1f;
                color = new Color(array[0].Value<float>() / scale, array[1].Value<float>() / scale, array[2].Value<float>() / scale, array.Count >= 4 ? array[3].Value<float>() / scale : 1f);
                return true;
            }

            if (token is JObject obj)
            {
                color = new Color(obj.Value<float?>("r") ?? 1f, obj.Value<float?>("g") ?? 1f, obj.Value<float?>("b") ?? 1f, obj.Value<float?>("a") ?? 1f);
                return true;
            }

            return false;
        }

        static T LoadAsset<T>(string pathOrGuid) where T : Object
        {
            var path = ResolveAssetPath(pathOrGuid);
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException($"Could not load {typeof(T).Name} at '{pathOrGuid}'.");
            return asset;
        }

        static string ResolveAssetPath(string pathOrGuid)
        {
            var value = pathOrGuid.Trim().Replace('\\', '/');
            var guidPath = AssetDatabase.GUIDToAssetPath(value);
            if (!string.IsNullOrWhiteSpace(guidPath))
                return guidPath;
            return value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                ? value
                : "Assets/" + value.TrimStart('/');
        }

        static Type FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(GetTypesSafe)
                .FirstOrDefault(type => string.Equals(type.FullName, fullName, StringComparison.Ordinal));
        }

        static IEnumerable<Type> GetTypesSafe(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type != null); }
            catch { return Array.Empty<Type>(); }
        }

        static bool SetMemberValue(object target, string memberName, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName))
                return false;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = target.GetType().GetProperty(memberName, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value);
                return true;
            }

            var field = target.GetType().GetField(memberName, flags);
            if (field != null)
            {
                field.SetValue(target, value);
                return true;
            }

            return false;
        }

        static object GetMemberValue(object target, string memberName)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return target?.GetType().GetProperty(memberName, flags)?.GetValue(target)
                   ?? target?.GetType().GetField(memberName, flags)?.GetValue(target);
        }

        static object ToGameObjectInfo(GameObject go) => new
        {
            name = go.name,
            id = UnityApiAdapter.GetObjectId(go),
            path = GetHierarchyPath(go),
            scene = go.scene.IsValid() ? go.scene.name : null
        };

        static object ToAssetInfo(Object obj)
        {
            if (obj == null)
                return null;
            var path = AssetDatabase.GetAssetPath(obj);
            return new { name = obj.name, type = obj.GetType().Name, path, guid = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.AssetPathToGUID(path) };
        }

        static string GetHierarchyPath(GameObject go)
        {
            var parts = new Stack<string>();
            for (var current = go.transform; current != null; current = current.parent)
                parts.Push(current.name);
            return "/" + string.Join("/", parts);
        }

        static void MarkSceneDirty(GameObject go)
        {
            if (go != null && go.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
        }

        static string Normalize(string value) => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

        static string GetString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
                    return token.ToString();
            return null;
        }

        static bool TryGetFloat(JObject obj, out float value, params string[] keys)
        {
            value = 0f;
            foreach (var key in keys)
            {
                if (!obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) || token.Type == JTokenType.Null)
                    continue;

                if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                {
                    value = token.ToObject<float>();
                    return true;
                }

                var text = token.ToString();
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                    float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                    return true;
            }

            return false;
        }

        static bool TryGetBool(JObject obj, out bool value, params string[] keys)
        {
            value = false;
            var text = GetString(obj, keys);
            return bool.TryParse(text, out value);
        }
    }
}
