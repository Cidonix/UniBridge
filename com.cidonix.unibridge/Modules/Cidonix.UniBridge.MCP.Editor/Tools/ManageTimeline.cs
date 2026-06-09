#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Timeline/PlayableDirector authoring without a hard Timeline compile dependency.
    /// </summary>
    public static class ManageTimeline
    {
        const string ToolName = "UniBridge_ManageTimeline";

        public const string Title = "Manage Timeline assets and PlayableDirectors";

        public const string Description = @"Create and inspect TimelineAsset files, add tracks/default clips, and bind them to PlayableDirector components.

Use this after AnimationClip/AnimatorController work when an agent needs cinematic or sequenced playback. UniBridge uses reflection for Timeline types, so projects without com.unity.timeline still compile; Timeline actions return a clear error until the package is installed.

Args:
    Action: Inspect, CreateAsset, AddTrack, AddClip, CreateDirector, or BindDirector.
    Path/TimelinePath: Assets/... .playable path.
    TrackType: AnimationTrack, ActivationTrack, AudioTrack, ControlTrack, SignalTrack, PlayableTrack, GroupTrack, or full type name.
    TrackName: New or existing track name.
    ClipName, Start, Duration: Default clip metadata.
    Target: GameObject for PlayableDirector creation/binding.
    BindingTarget: Scene object bound to the track.

Returns:
    success, message, and data with timeline track/clip summaries and PlayableDirector binding details.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new { type = "string", description = "Timeline operation.", @enum = new[] { "Inspect", "CreateAsset", "AddTrack", "AddClip", "CreateDirector", "BindDirector" } },
                    Path = new { type = "string", description = "Assets/... .playable TimelineAsset path." },
                    TimelinePath = new { type = "string", description = "Alias for Path." },
                    Name = new { type = "string", description = "Timeline asset name." },
                    TrackType = new { type = "string", description = "Track type short/full name.", @default = "AnimationTrack" },
                    TrackName = new { type = "string", description = "Track name." },
                    ClipName = new { type = "string", description = "Clip display name." },
                    Start = new { type = "number", description = "Timeline clip start time." },
                    Duration = new { type = "number", description = "Timeline clip duration." },
                    Target = new { description = "GameObject for PlayableDirector.", anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    BindingTarget = new { description = "Scene object to bind to TrackName.", anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    SearchMethod = new { type = "string", description = "Scene object search method." }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "assets", "scene", "animation", "timeline" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = Normalize(GetString(parameters, "Action", "action") ?? "Inspect");
            try
            {
                EnsureTimelineAvailable();
                return action switch
                {
                    "inspect" => Inspect(parameters),
                    "createasset" or "create" or "createorupdate" => CreateAsset(parameters),
                    "addtrack" => AddTrack(parameters),
                    "addclip" => AddClip(parameters),
                    "createdirector" => CreateDirector(parameters),
                    "binddirector" or "bind" => BindDirector(parameters),
                    _ => Response.Error($"Unknown Timeline action '{GetString(parameters, "Action", "action")}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageTimeline] Action '{action}' failed: {ex}");
                return Response.Error($"Timeline action '{action}' failed: {ex.Message}");
            }
        }

        static object Inspect(JObject parameters)
        {
            var path = ResolveTimelinePath(parameters);
            var timeline = LoadTimeline(path, required: true);
            return Response.Success("Inspected TimelineAsset.", new { path, guid = AssetDatabase.AssetPathToGUID(path), timeline = BuildTimelineSummary(timeline) });
        }

        static object CreateAsset(JObject parameters)
        {
            var path = ResolveTimelinePath(parameters);
            var timeline = LoadTimeline(path, required: false);
            var created = false;
            if (timeline == null)
            {
                EnsureParentDirectory(path);
                timeline = ScriptableObject.CreateInstance(TimelineAssetType);
                timeline.name = GetString(parameters, "Name", "name") ?? Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(timeline, path);
                created = true;
            }
            else
            {
                VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true);
            }

            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            return Response.Success(created ? "TimelineAsset created." : "TimelineAsset already exists.", new { path, created, timeline = BuildTimelineSummary(timeline) });
        }

        static object AddTrack(JObject parameters)
        {
            var path = ResolveTimelinePath(parameters);
            var timeline = LoadTimeline(path, required: false);
            if (timeline == null)
            {
                CreateAsset(parameters);
                timeline = LoadTimeline(path, required: true);
            }
            timeline = LoadTimeline(path, required: true);
            VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true);

            var trackType = ResolveTrackType(GetString(parameters, "TrackType", "trackType", "track_type") ?? "AnimationTrack");
            var trackName = GetString(parameters, "TrackName", "trackName", "track_name", "Name", "name") ?? trackType.Name;
            var existing = FindTrack(timeline, trackName);
            var track = existing ?? CreateTrack(timeline, trackType, trackName);
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            return Response.Success(existing == null ? "Timeline track added." : "Timeline track already exists.", new { path, track = BuildTrackSummary(track), timeline = BuildTimelineSummary(timeline) });
        }

        static object AddClip(JObject parameters)
        {
            var path = ResolveTimelinePath(parameters);
            var timeline = LoadTimeline(path, required: true);
            VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true);

            var trackName = GetString(parameters, "TrackName", "trackName", "track_name") ?? "Track";
            var track = FindTrack(timeline, trackName);
            if (track == null)
            {
                var trackType = ResolveTrackType(GetString(parameters, "TrackType", "trackType", "track_type") ?? "AnimationTrack");
                track = CreateTrack(timeline, trackType, trackName);
            }

            var clip = CreateDefaultClip(track);
            if (clip == null)
                return Response.Error($"Track '{trackName}' does not support CreateDefaultClip.");

            SetProperty(clip, "displayName", GetString(parameters, "ClipName", "clipName", "clip_name", "Name", "name") ?? "Clip");
            SetProperty(clip, "start", GetDouble(parameters, 0, "Start", "start"));
            SetProperty(clip, "duration", GetDouble(parameters, 1, "Duration", "duration"));

            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            return Response.Success("Timeline clip added.", new { path, track = BuildTrackSummary(track), clip = BuildClipSummary(clip), timeline = BuildTimelineSummary(timeline) });
        }

        static object CreateDirector(JObject parameters)
        {
            var target = ResolveTarget(parameters, createIfMissingName: GetString(parameters, "TargetName", "targetName", "target_name") ?? "PlayableDirector");
            if (target == null)
                return Response.Error("Target GameObject was not found.");

            var director = target.GetComponent<PlayableDirector>();
            if (director == null)
                director = Undo.AddComponent<PlayableDirector>(target);
            if (director == null)
                return Response.Error($"PlayableDirector component could not be added to '{target.name}'.");
            var path = ResolveTimelinePath(parameters, required: false);
            if (!string.IsNullOrWhiteSpace(path))
            {
                var timeline = LoadTimeline(path, required: false);
                if (timeline == null)
                {
                    CreateAsset(parameters);
                    timeline = LoadTimeline(path, required: true);
                }
                timeline = LoadTimeline(path, required: true);
                director.playableAsset = timeline as PlayableAsset;
            }

            EditorUtility.SetDirty(director);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("PlayableDirector created or updated.", new { target = BuildObjectSummary(target), director = BuildDirectorSummary(director) });
        }

        static object BindDirector(JObject parameters)
        {
            var directorTarget = ResolveTarget(parameters, createIfMissingName: null);
            if (directorTarget == null)
                return Response.Error("Target GameObject was not found for PlayableDirector binding.");

            var director = directorTarget.GetComponent<PlayableDirector>();
            if (director == null)
                return Response.Error($"GameObject '{directorTarget.name}' does not have a PlayableDirector.");

            var timeline = director.playableAsset != null ? director.playableAsset : LoadTimeline(ResolveTimelinePath(parameters, required: false), required: false) as PlayableAsset;
            if (timeline == null)
                return Response.Error("PlayableDirector has no Timeline asset and no TimelinePath was provided.");

            if (director.playableAsset == null)
                director.playableAsset = timeline;

            var trackName = GetString(parameters, "TrackName", "trackName", "track_name");
            var track = FindTrack(timeline, trackName);
            if (track == null)
                return Response.Error($"Track '{trackName}' was not found.");

            var bindingTarget = ResolveBindingTarget(parameters);
            if (bindingTarget == null)
                return Response.Error("BindingTarget was not found.");

            Undo.RecordObject(director, "Bind Timeline Track");
            director.SetGenericBinding(track, bindingTarget);
            EditorUtility.SetDirty(director);
            EditorSceneManager.MarkSceneDirty(director.gameObject.scene);
            return Response.Success("Timeline track bound to PlayableDirector.", new
            {
                director = BuildDirectorSummary(director),
                track = BuildTrackSummary(track),
                bindingTarget = BuildObjectSummary(bindingTarget is Component c ? c.gameObject : bindingTarget as GameObject)
            });
        }

        static UnityEngine.Object LoadTimeline(string path, bool required)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (required)
                    throw new InvalidOperationException("Path/TimelinePath is required.");
                return null;
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null && TimelineAssetType.IsInstanceOfType(asset))
                return asset;
            if (required)
                throw new InvalidOperationException($"TimelineAsset was not found at '{path}'.");
            return null;
        }

        static UnityEngine.Object CreateTrack(UnityEngine.Object timeline, Type trackType, string trackName)
        {
            var methods = TimelineAssetType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "CreateTrack" && m.IsGenericMethodDefinition)
                .ToArray();

            foreach (var method in methods)
            {
                var generic = method.MakeGenericMethod(trackType);
                var args = generic.GetParameters().Length switch
                {
                    2 => new object[] { null, trackName },
                    1 => new object[] { trackName },
                    0 => Array.Empty<object>(),
                    _ => null
                };
                if (args == null)
                    continue;

                try
                {
                    var track = generic.Invoke(timeline, args) as UnityEngine.Object;
                    if (track != null)
                    {
                        track.name = trackName;
                        return track;
                    }
                }
                catch
                {
                    // Try the next overload.
                }
            }

            throw new InvalidOperationException($"Could not create track type '{trackType.FullName}'.");
        }

        static object CreateDefaultClip(UnityEngine.Object track)
        {
            var method = track.GetType().GetMethod("CreateDefaultClip", BindingFlags.Instance | BindingFlags.Public);
            return method?.Invoke(track, null);
        }

        static UnityEngine.Object FindTrack(UnityEngine.Object timeline, string trackName)
        {
            var tracks = GetTracks(timeline);
            if (string.IsNullOrWhiteSpace(trackName))
                return tracks.FirstOrDefault();
            return tracks.FirstOrDefault(track => string.Equals(track.name, trackName, StringComparison.OrdinalIgnoreCase));
        }

        static List<UnityEngine.Object> GetTracks(UnityEngine.Object timeline)
        {
            var method = TimelineAssetType.GetMethod("GetOutputTracks", BindingFlags.Instance | BindingFlags.Public)
                         ?? TimelineAssetType.GetMethod("GetRootTracks", BindingFlags.Instance | BindingFlags.Public);
            if (method?.Invoke(timeline, null) is not IEnumerable enumerable)
                return new List<UnityEngine.Object>();

            return enumerable.OfType<UnityEngine.Object>().ToList();
        }

        static object BuildTimelineSummary(UnityEngine.Object timeline)
        {
            var tracks = GetTracks(timeline);
            return new
            {
                name = timeline.name,
                type = timeline.GetType().FullName,
                trackCount = tracks.Count,
                tracks = tracks.Select(BuildTrackSummary).ToArray()
            };
        }

        static object BuildTrackSummary(UnityEngine.Object track)
        {
            if (track == null)
                return null;

            var clips = GetClips(track).Select(BuildClipSummary).ToArray();
            return new { name = track.name, type = track.GetType().FullName, clipCount = clips.Length, clips };
        }

        static IEnumerable<object> GetClips(UnityEngine.Object track)
        {
            var method = track.GetType().GetMethod("GetClips", BindingFlags.Instance | BindingFlags.Public);
            return method?.Invoke(track, null) is IEnumerable enumerable
                ? enumerable.Cast<object>().ToArray()
                : Array.Empty<object>();
        }

        static object BuildClipSummary(object clip)
        {
            if (clip == null)
                return null;
            return new
            {
                displayName = GetProperty<string>(clip, "displayName"),
                start = GetProperty<double>(clip, "start"),
                duration = GetProperty<double>(clip, "duration")
            };
        }

        static object BuildDirectorSummary(PlayableDirector director)
        {
            return new
            {
                gameObject = BuildObjectSummary(director.gameObject),
                playableAsset = director.playableAsset != null ? new { name = director.playableAsset.name, path = AssetDatabase.GetAssetPath(director.playableAsset) } : null,
                time = director.time,
                initialTime = director.initialTime,
                duration = director.duration,
                playOnAwake = director.playOnAwake,
                extrapolationMode = director.extrapolationMode.ToString()
            };
        }

        static object BuildObjectSummary(GameObject go)
        {
            return go == null ? null : new
            {
                name = go.name,
                instanceId = UnityApiAdapter.GetObjectId(go),
                path = SceneObjectLocator.GetHierarchyPath(go),
                scene = go.scene.IsValid() ? new { name = go.scene.name, path = go.scene.path } : null
            };
        }

        static GameObject ResolveTarget(JObject parameters, string createIfMissingName)
        {
            var target = GetToken(parameters, "Target", "target", "DirectorTarget", "directorTarget", "director_target", "GameObject", "gameObject");
            if (target != null && target.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(target.ToString()))
            {
                return SceneObjectLocator.FindObject(target.ToString(), GetString(parameters, "SearchMethod", "searchMethod", "search_method"), new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            }

            if (Selection.activeGameObject != null)
                return Selection.activeGameObject;

            if (string.IsNullOrWhiteSpace(createIfMissingName))
                return null;

            var go = new GameObject(createIfMissingName);
            Undo.RegisterCreatedObjectUndo(go, "Create PlayableDirector GameObject");
            return go;
        }

        static UnityEngine.Object ResolveBindingTarget(JObject parameters)
        {
            var target = GetToken(parameters, "BindingTarget", "bindingTarget", "binding_target", "BoundObject", "boundObject", "bound_object");
            if (target == null || target.Type == JTokenType.Null)
                return Selection.activeGameObject;
            var go = SceneObjectLocator.FindObject(target.ToString(), GetString(parameters, "BindingSearchMethod", "bindingSearchMethod", "binding_search_method", "SearchMethod", "searchMethod", "search_method"), new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            return go;
        }

        static Type ResolveTrackType(string name)
        {
            var normalized = (name ?? "AnimationTrack").Trim();
            var fullName = normalized.Contains(".") ? normalized : "UnityEngine.Timeline." + normalized;
            var type = FindType(fullName);
            if (type == null)
                throw new InvalidOperationException($"Timeline track type '{name}' was not found.");
            return type;
        }

        static void EnsureTimelineAvailable()
        {
            if (TimelineAssetType == null)
                throw new InvalidOperationException("Timeline package types were not found. Install/enable com.unity.timeline in this Unity project.");
        }

        static Type TimelineAssetType => FindType("UnityEngine.Timeline.TimelineAsset");

        static Type FindType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(fullName, false); } catch { }
                if (type != null)
                    return type;
            }

            return null;
        }

        static void SetProperty(object obj, string name, object value)
        {
            var property = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
                property.SetValue(obj, value);
        }

        static T GetProperty<T>(object obj, string name)
        {
            var property = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null)
                return default;
            var value = property.GetValue(obj);
            if (value is T typed)
                return typed;
            return value == null ? default : (T)Convert.ChangeType(value, typeof(T));
        }

        static string ResolveTimelinePath(JObject parameters, bool required = true)
        {
            var path = NormalizeAssetPath(GetString(parameters, "Path", "path", "TimelinePath", "timelinePath", "timeline_path", "AssetPath", "assetPath", "asset_path"));
            if (required && string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Path/TimelinePath is required.");
            if (!string.IsNullOrWhiteSpace(path) && !path.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Timeline path '{path}' must end with .playable.");
            return path;
        }

        static void EnsureParentDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || AssetDatabase.IsValidFolder(directory))
                return;

            var parts = directory.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var normalized = path.Trim().Replace('\\', '/').TrimStart('/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return normalized;
            return "Assets/" + normalized;
        }

        static string Normalize(string value) => (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

        static JToken GetToken(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
                    return token;
            return null;
        }

        static string GetString(JObject obj, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString().Trim();
        }

        static double GetDouble(JObject obj, double defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && double.TryParse(token.ToString(), out var value) ? value : defaultValue;
        }
    }
}
