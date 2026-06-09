#nullable disable
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Audio component authoring helpers for common scene audio setups.
    /// </summary>
    public static class ManageAudio
    {
        public const string Title = "Manage scene audio components";

        public const string Description = @"Create, inspect, and configure scene audio components with small presets.

Actions:
    Inspect: Summarize AudioSource, AudioListener, AudioReverbZone, and audio filter components on a GameObject.
    AddSource: Add or update an AudioSource.
    AddListener: Add an AudioListener.
    AddReverbZone: Add or update an AudioReverbZone.
    AddFilter: Add an audio filter component: LowPass, HighPass, Echo, Reverb, Chorus, or Distortion.
    ApplyPreset: Apply Sfx2D, SpatialOneShot, AmbientLoop3D, ReverbRoom, or ListenerRig.

Args:
    Target/SearchMethod: GameObject to configure. If missing and Name is supplied, a GameObject is created.
    Preset, FilterType, ClipPath, Volume, Pitch, Loop, PlayOnAwake, SpatialBlend, MinDistance, MaxDistance, Properties.

Returns:
    success, message, and an audio-focused component summary.";

        [McpTool("UniBridge_ManageAudio", Description, Title, Groups = new[] { "core", "scene", "audio" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            try
            {
                var action = GetString(parameters, "Action", "action") ?? "Inspect";
                return Normalize(action) switch
                {
                    "inspect" => Inspect(parameters),
                    "addsource" => ConfigureSource(parameters, preset: null),
                    "addlistener" => AddListener(parameters),
                    "addreverbzone" => AddReverbZone(parameters),
                    "addfilter" => AddFilter(parameters),
                    "applypreset" => ApplyPreset(parameters),
                    _ => Response.Error($"Unsupported audio action '{action}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageAudio] failed: {ex}");
                return Response.Error($"ManageAudio failed: {ex.Message}");
            }
        }

        static object Inspect(JObject parameters)
        {
            var go = ResolveTarget(parameters, createIfMissing: false);
            if (go == null)
                return Response.Error("Inspect requires Target or selected GameObject.");

            return Response.Success($"Inspected audio setup on '{go.name}'.", BuildAudioSummary(go));
        }

        static object ApplyPreset(JObject parameters)
        {
            var preset = Normalize(GetString(parameters, "Preset", "preset") ?? "Sfx2D");
            return preset switch
            {
                "sfx2d" => ConfigureSource(parameters, "Sfx2D"),
                "spatialoneshot" => ConfigureSource(parameters, "SpatialOneShot"),
                "ambientloop3d" => ConfigureSource(parameters, "AmbientLoop3D"),
                "reverbroom" => AddReverbZone(parameters),
                "listenerrig" => AddListener(parameters),
                _ => Response.Error($"Unsupported audio preset '{GetString(parameters, "Preset", "preset")}'.")
            };
        }

        static object ConfigureSource(JObject parameters, string preset)
        {
            var go = ResolveTarget(parameters, createIfMissing: true);
            if (go == null)
                return Response.Error("AddSource/ApplyPreset requires Target, selected GameObject, or Name for creation.");

            var source = GetOrAddComponent<AudioSource>(go);
            Undo.RecordObject(source, "Configure AudioSource");

            if (preset == "Sfx2D")
            {
                source.spatialBlend = 0f;
                source.loop = false;
                source.playOnAwake = false;
            }
            else if (preset == "SpatialOneShot")
            {
                source.spatialBlend = 1f;
                source.loop = false;
                source.playOnAwake = false;
                source.minDistance = 1f;
                source.maxDistance = 30f;
            }
            else if (preset == "AmbientLoop3D")
            {
                source.spatialBlend = 1f;
                source.loop = true;
                source.playOnAwake = true;
                source.minDistance = 2f;
                source.maxDistance = 80f;
            }

            var clipPath = GetString(parameters, "ClipPath", "clipPath", "clip");
            if (!string.IsNullOrWhiteSpace(clipPath))
                source.clip = LoadAsset<AudioClip>(clipPath);

            if (TryGetFloat(parameters, out var volume, "Volume", "volume"))
                source.volume = Mathf.Clamp01(volume);
            if (TryGetFloat(parameters, out var pitch, "Pitch", "pitch"))
                source.pitch = Mathf.Clamp(pitch, -3f, 3f);
            if (TryGetBool(parameters, out var loop, "Loop", "loop"))
                source.loop = loop;
            if (TryGetBool(parameters, out var playOnAwake, "PlayOnAwake", "playOnAwake", "play_on_awake"))
                source.playOnAwake = playOnAwake;
            if (TryGetFloat(parameters, out var spatialBlend, "SpatialBlend", "spatialBlend", "spatial_blend"))
                source.spatialBlend = Mathf.Clamp01(spatialBlend);
            if (TryGetFloat(parameters, out var minDistance, "MinDistance", "minDistance", "min_distance"))
                source.minDistance = Mathf.Max(0f, minDistance);
            if (TryGetFloat(parameters, out var maxDistance, "MaxDistance", "maxDistance", "max_distance"))
                source.maxDistance = Mathf.Max(source.minDistance, maxDistance);

            ApplySerializedProperties(source, parameters["Properties"] as JObject ?? parameters["properties"] as JObject);
            EditorUtility.SetDirty(source);
            MarkSceneDirty(go);

            return Response.Success($"Configured AudioSource on '{go.name}'.", BuildAudioSummary(go));
        }

        static object AddListener(JObject parameters)
        {
            var go = ResolveTarget(parameters, createIfMissing: true);
            if (go == null)
                return Response.Error("AddListener requires Target, selected GameObject, or Name for creation.");

            var listener = GetOrAddComponent<AudioListener>(go);
            ApplySerializedProperties(listener, parameters["Properties"] as JObject ?? parameters["properties"] as JObject);
            EditorUtility.SetDirty(listener);
            MarkSceneDirty(go);
            return Response.Success($"AudioListener is present on '{go.name}'.", BuildAudioSummary(go));
        }

        static object AddReverbZone(JObject parameters)
        {
            var go = ResolveTarget(parameters, createIfMissing: true);
            if (go == null)
                return Response.Error("AddReverbZone requires Target, selected GameObject, or Name for creation.");

            var zone = GetOrAddComponent<AudioReverbZone>(go);
            Undo.RecordObject(zone, "Configure AudioReverbZone");
            if (TryGetFloat(parameters, out var minDistance, "MinDistance", "minDistance", "min_distance"))
                zone.minDistance = Mathf.Max(0f, minDistance);
            else if (zone.minDistance <= 0f)
                zone.minDistance = 5f;
            if (TryGetFloat(parameters, out var maxDistance, "MaxDistance", "maxDistance", "max_distance"))
                zone.maxDistance = Mathf.Max(zone.minDistance, maxDistance);
            else if (zone.maxDistance <= zone.minDistance)
                zone.maxDistance = 20f;

            ApplySerializedProperties(zone, parameters["Properties"] as JObject ?? parameters["properties"] as JObject);
            EditorUtility.SetDirty(zone);
            MarkSceneDirty(go);
            return Response.Success($"Configured AudioReverbZone on '{go.name}'.", BuildAudioSummary(go));
        }

        static object AddFilter(JObject parameters)
        {
            var go = ResolveTarget(parameters, createIfMissing: true);
            if (go == null)
                return Response.Error("AddFilter requires Target, selected GameObject, or Name for creation.");

            var filterType = Normalize(GetString(parameters, "FilterType", "filterType", "filter") ?? "LowPass");
            var type = filterType switch
            {
                "lowpass" => typeof(AudioLowPassFilter),
                "highpass" => typeof(AudioHighPassFilter),
                "echo" => typeof(AudioEchoFilter),
                "reverb" => typeof(AudioReverbFilter),
                "chorus" => typeof(AudioChorusFilter),
                "distortion" => typeof(AudioDistortionFilter),
                _ => null
            };
            if (type == null)
                return Response.Error($"Unsupported audio filter '{filterType}'.");

            var component = GetOrAddComponent(go, type);
            ApplySerializedProperties(component, parameters["Properties"] as JObject ?? parameters["properties"] as JObject);
            EditorUtility.SetDirty(component);
            MarkSceneDirty(go);
            return Response.Success($"Configured {type.Name} on '{go.name}'.", BuildAudioSummary(go));
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
                Undo.RegisterCreatedObjectUndo(created, "Create Audio GameObject");
                return created;
            }

            var go = Selection.activeGameObject;
            if (go != null || !createIfMissing)
                return go;

            if (string.IsNullOrWhiteSpace(name))
                return null;

            go = new GameObject(name.Trim());
            Undo.RegisterCreatedObjectUndo(go, "Create Audio GameObject");
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

        static object BuildAudioSummary(GameObject go)
        {
            return new
            {
                target = ToGameObjectInfo(go),
                sources = go.GetComponents<AudioSource>().Select(source => new
                {
                    enabled = source.enabled,
                    clip = ToAssetInfo(source.clip),
                    volume = source.volume,
                    pitch = source.pitch,
                    loop = source.loop,
                    playOnAwake = source.playOnAwake,
                    spatialBlend = source.spatialBlend,
                    minDistance = source.minDistance,
                    maxDistance = source.maxDistance
                }).ToArray(),
                hasListener = go.GetComponent<AudioListener>() != null,
                reverbZones = go.GetComponents<AudioReverbZone>().Select(zone => new
                {
                    enabled = zone.enabled,
                    minDistance = zone.minDistance,
                    maxDistance = zone.maxDistance,
                    reverbPreset = zone.reverbPreset.ToString()
                }).ToArray(),
                filters = go.GetComponents<Component>()
                    .Where(component => component is AudioLowPassFilter || component is AudioHighPassFilter || component is AudioEchoFilter || component is AudioReverbFilter || component is AudioChorusFilter || component is AudioDistortionFilter)
                    .Select(component => component.GetType().Name)
                    .ToArray()
            };
        }

        static void ApplySerializedProperties(Component component, JObject properties)
        {
            if (component == null || properties == null || !properties.HasValues)
                return;

            Undo.RecordObject(component, "Patch Audio Component");
            using var serializedObject = new SerializedObject(component);
            serializedObject.Update();
            foreach (var prop in properties.Properties())
                SerializedPropertyPatcher.TryApplyProperty(component, serializedObject, prop.Name, prop.Value, dryRun: false);
            serializedObject.ApplyModifiedProperties();
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
            var parts = new System.Collections.Generic.Stack<string>();
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
            var text = GetString(obj, keys);
            return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        static bool TryGetBool(JObject obj, out bool value, params string[] keys)
        {
            value = false;
            var text = GetString(obj, keys);
            return bool.TryParse(text, out value);
        }
    }
}
