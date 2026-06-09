#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    [InitializeOnLoad]
    public static class EditorEventHistory
    {
        const int MaxEvents = 512;
        const int MaxDiagnostics = 256;
        const int MaxAssetChanges = 512;
        static readonly List<EditorEventRecord> Events = new();
        static readonly List<CompilationDiagnosticRecord> CompilationDiagnostics = new();
        static readonly List<AssetChangeRecord> AssetChanges = new();
        static long s_NextId = 1;
        static long s_NextAssetChangeId = 1;
        static string s_CurrentCompilationContext;

        static EditorEventHistory()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.projectChanged += OnProjectChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            UnityEditor.PackageManager.Events.registeredPackages -= OnRegisteredPackages;
            UnityEditor.PackageManager.Events.registeredPackages += OnRegisteredPackages;

#if UNITY_2020_1_OR_NEWER
            ObjectChangeEvents.changesPublished -= OnObjectChangesPublished;
            ObjectChangeEvents.changesPublished += OnObjectChangesPublished;
#endif
        }

        public static object Snapshot(
            long sinceId,
            int limit,
            bool includeSelection,
            bool includeDiagnostics = true,
            bool includeAssetChanges = true)
        {
            limit = Mathf.Clamp(limit <= 0 ? 100 : limit, 1, MaxEvents);
            var records = Events
                .Where(item => item.id > sinceId)
                .OrderBy(item => item.id)
                .Take(limit)
                .Select(item => item.ToDto())
                .ToArray();

            return new
            {
                latestId = Events.Count == 0 ? 0 : Events[Events.Count - 1].id,
                sinceId,
                count = records.Length,
                truncated = Events.Count(item => item.id > sinceId) > records.Length,
                events = records,
                selection = includeSelection ? BuildSelectionSnapshot() : null,
                diagnostics = includeDiagnostics ? BuildCompilationDiagnostics(limit) : null,
                assetChanges = includeAssetChanges ? BuildAssetChangeSnapshot(sinceId, limit) : null
            };
        }

        public static long LatestId()
        {
            return Events.Count == 0 ? 0 : Events[Events.Count - 1].id;
        }

        public static EventQueryResult Query(long sinceId, int limit, EventQuery query)
        {
            limit = Mathf.Clamp(limit <= 0 ? 25 : limit, 1, MaxEvents);
            query ??= new EventQuery();

            var afterSince = Events
                .Where(item => item.id > sinceId)
                .OrderBy(item => item.id)
                .ToArray();

            var matches = afterSince
                .Where(item => Matches(item, query))
                .Take(limit)
                .Select(item => item.ToDto())
                .ToArray();

            return new EventQueryResult
            {
                latestId = LatestId(),
                sinceId = sinceId,
                totalAfterSince = afterSince.Length,
                count = matches.Length,
                truncated = afterSince.Count(item => Matches(item, query)) > matches.Length,
                events = matches
            };
        }

        public static void Clear()
        {
            Events.Clear();
            CompilationDiagnostics.Clear();
            AssetChanges.Clear();
            Record("history", "Editor event history was cleared.", null);
        }

        public static void RecordAssetChanges(
            IEnumerable<string> imported,
            IEnumerable<string> deleted,
            IEnumerable<(string from, string to)> moved)
        {
            var changes = new List<object>();
            AddAssetChangeRecords(imported, "imported", changes);
            AddAssetChangeRecords(deleted, "deleted", changes);

            foreach (var move in moved ?? Enumerable.Empty<(string from, string to)>())
            {
                var from = NormalizePath(move.from);
                var to = NormalizePath(move.to);
                if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
                    continue;

                var record = new AssetChangeRecord
                {
                    id = s_NextAssetChangeId++,
                    kind = "moved",
                    path = to,
                    oldPath = from,
                    guid = !string.IsNullOrWhiteSpace(to) ? AssetDatabase.AssetPathToGUID(to) : null,
                    utc = DateTime.UtcNow.ToString("o")
                };
                AssetChanges.Add(record);
                changes.Add(record.ToDto());
            }

            TrimAssetChanges();
            if (changes.Count > 0)
            {
                Record("projectAssets", $"Project assets changed: {changes.Count} delta(s).", new
                {
                    changes = changes.ToArray(),
                    referenceGraphHint = "AssetIntelligence reference graph may be stale after moved/deleted/imported assets."
                });
            }
        }

        static void OnSelectionChanged()
        {
            Record("selection", "Selection changed.", new { selection = BuildSelectionSnapshot() });
        }

        static void OnHierarchyChanged()
        {
            Record("hierarchy", "Scene hierarchy changed.", new { sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount });
        }

        static void OnProjectChanged()
        {
            Record("project", "Project assets changed.", null);
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            Record("playMode", $"Play mode state changed to {state}.", new { state = state.ToString() });
        }

        static void OnCompilationStarted(object context)
        {
            s_CurrentCompilationContext = context?.ToString();
            CompilationDiagnostics.Clear();
            Record("compilation", "Compilation started.", new
            {
                context = s_CurrentCompilationContext,
                isCompiling = EditorApplication.isCompiling
            });
        }

        static void OnCompilationFinished(object context)
        {
            s_CurrentCompilationContext = context?.ToString();
            Record("compilation", "Compilation finished.", new
            {
                context = s_CurrentCompilationContext,
                isCompiling = EditorApplication.isCompiling,
                diagnostics = BuildCompilationDiagnostics(50)
            });
        }

        static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            var normalizedAssembly = NormalizePath(assemblyPath);
            var diagnostics = (messages ?? Array.Empty<CompilerMessage>())
                .Select(message => new CompilationDiagnosticRecord
                {
                    assemblyPath = normalizedAssembly,
                    type = message.type.ToString(),
                    message = message.message,
                    file = NormalizePath(message.file),
                    line = message.line,
                    column = message.column,
                    utc = DateTime.UtcNow.ToString("o")
                })
                .ToArray();

            CompilationDiagnostics.AddRange(diagnostics);
            while (CompilationDiagnostics.Count > MaxDiagnostics)
                CompilationDiagnostics.RemoveAt(0);

            var errors = diagnostics.Count(item => string.Equals(item.type, "Error", StringComparison.OrdinalIgnoreCase));
            var warnings = diagnostics.Count(item => string.Equals(item.type, "Warning", StringComparison.OrdinalIgnoreCase));
            Record("compilationAssembly", $"Assembly compilation finished: {assemblyPath}.", new
            {
                assemblyPath = normalizedAssembly,
                messageCount = diagnostics.Length,
                errors,
                warnings,
                diagnostics = diagnostics.Select(item => item.ToDto()).ToArray()
            });
        }

        static void OnBeforeAssemblyReload()
        {
            Record("assemblyReload", "Assembly reload will start.", new { phase = "before" });
        }

        static void OnAfterAssemblyReload()
        {
            Record("assemblyReload", "Assembly reload finished.", new { phase = "after" });
        }

#if UNITY_2020_1_OR_NEWER
        static void OnObjectChangesPublished(ref ObjectChangeEventStream stream)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < stream.length; i++)
            {
                var kind = stream.GetEventType(i).ToString();
                counts[kind] = counts.TryGetValue(kind, out var count) ? count + 1 : 1;
            }

            Record("objectChanges", $"Unity published {stream.length} object change event(s).", new
            {
                count = stream.length,
                counts
            });
        }
#endif

        static void OnRegisteredPackages(PackageRegistrationEventArgs args)
        {
            var added = ReadPackageDtos(args, "added");
            var changed = ReadPackageDtos(args, "changed", "updated");
            var removed = ReadPackageDtos(args, "removed");
            var total = added.Length + changed.Length + removed.Length;
            if (total == 0)
                return;

            Record("packages", $"Unity package registry changed: {total} package delta(s).", new
            {
                added,
                changed,
                removed
            });
        }

        static void Record(string kind, string message, object data)
        {
            Events.Add(new EditorEventRecord
            {
                id = s_NextId++,
                kind = kind,
                message = message,
                timeSinceStartup = EditorApplication.timeSinceStartup,
                utc = DateTime.UtcNow.ToString("o"),
                data = data
            });

            while (Events.Count > MaxEvents)
                Events.RemoveAt(0);
        }

        static bool Matches(EditorEventRecord item, EventQuery query)
        {
            if (item == null)
                return false;

            var normalizedKind = NormalizeKey(item.kind);
            if (query.kinds != null && query.kinds.Count > 0 && !query.kinds.Contains(normalizedKind))
                return false;

            if (!string.IsNullOrWhiteSpace(query.messageContains) &&
                (item.message == null || item.message.IndexOf(query.messageContains, StringComparison.OrdinalIgnoreCase) < 0))
                return false;

            var dto = JObject.FromObject(item.ToDto());

            if (!string.IsNullOrWhiteSpace(query.playModeState))
            {
                var state = dto.SelectToken("data.state")?.ToString();
                if (!string.Equals(state, query.playModeState, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(query.phase))
            {
                var phase = dto.SelectToken("data.phase")?.ToString();
                if (!string.Equals(phase, query.phase, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(query.textContains) &&
                dto.ToString(Newtonsoft.Json.Formatting.None).IndexOf(query.textContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (!string.IsNullOrWhiteSpace(query.assetPathContains) &&
                dto.ToString(Newtonsoft.Json.Formatting.None).IndexOf(query.assetPathContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return true;
        }

        static string NormalizeKey(string value)
        {
            return (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }

        static object BuildSelectionSnapshot()
        {
            var activeScene = SceneManager.GetActiveScene();
            return new
            {
                activeObject = Selection.activeObject == null ? null : BuildSelectionObject(Selection.activeObject),
                activeGameObject = Selection.activeGameObject == null ? null : BuildGameObjectSelection(Selection.activeGameObject),
                activeTransform = Selection.activeTransform == null ? null : BuildSelectionObject(Selection.activeTransform),
                activeInstanceID = UnityApiAdapter.GetActiveSelectionId(),
                count = Selection.objects?.Length ?? 0,
                assetGUIDs = Selection.assetGUIDs ?? Array.Empty<string>(),
                activeScene = new
                {
                    name = activeScene.IsValid() ? activeScene.name : null,
                    path = activeScene.IsValid() ? NormalizePath(activeScene.path) : null,
                    guid = activeScene.IsValid() && !string.IsNullOrWhiteSpace(activeScene.path)
                        ? AssetDatabase.AssetPathToGUID(activeScene.path)
                        : null,
                    isDirty = activeScene.IsValid() && activeScene.isDirty,
                    isLoaded = activeScene.IsValid() && activeScene.isLoaded
                },
                objects = (Selection.objects ?? Array.Empty<UnityEngine.Object>())
                    .Where(obj => obj != null)
                    .Select(BuildSelectionObject)
                    .ToArray(),
                gameObjects = (Selection.gameObjects ?? Array.Empty<GameObject>())
                    .Where(go => go != null)
                    .Select(BuildGameObjectSelection)
                    .ToArray()
            };
        }

        static object BuildCompilationDiagnostics(int limit)
        {
            limit = Mathf.Clamp(limit <= 0 ? 50 : limit, 1, MaxDiagnostics);
            var recent = CompilationDiagnostics
                .Skip(Math.Max(0, CompilationDiagnostics.Count - limit))
                .Select(item => item.ToDto())
                .ToArray();

            return new
            {
                isCompiling = EditorApplication.isCompiling,
                currentContext = s_CurrentCompilationContext,
                totalRetained = CompilationDiagnostics.Count,
                returned = recent.Length,
                errors = CompilationDiagnostics.Count(item => string.Equals(item.type, "Error", StringComparison.OrdinalIgnoreCase)),
                warnings = CompilationDiagnostics.Count(item => string.Equals(item.type, "Warning", StringComparison.OrdinalIgnoreCase)),
                messages = recent
            };
        }

        static object BuildAssetChangeSnapshot(long sinceId, int limit)
        {
            limit = Mathf.Clamp(limit <= 0 ? 100 : limit, 1, MaxAssetChanges);
            var latestId = AssetChanges.Count == 0 ? 0 : AssetChanges[AssetChanges.Count - 1].id;
            var records = AssetChanges
                .Where(item => item.id > sinceId)
                .OrderBy(item => item.id)
                .Take(limit)
                .Select(item => item.ToDto())
                .ToArray();

            return new
            {
                latestId,
                sinceId,
                count = records.Length,
                truncated = AssetChanges.Count(item => item.id > sinceId) > records.Length,
                changes = records,
                referenceGraphHint = records.Length > 0
                    ? "If these changes affect dependencies, rerun UniBridge_AssetIntelligence ReferenceGraph or Impact before destructive asset work."
                    : null
            };
        }

        static object BuildSelectionObject(UnityEngine.Object obj)
        {
            if (obj == null)
                return null;

            var isPersistent = EditorUtility.IsPersistent(obj);
            var assetPath = isPersistent ? NormalizePath(AssetDatabase.GetAssetPath(obj)) : null;
            var gameObject = obj as GameObject;
            if (gameObject == null && obj is Component component)
                gameObject = component.gameObject;

            var hierarchyPath = gameObject != null && !isPersistent ? BuildGameObjectPath(gameObject) : null;
            var scene = gameObject != null ? gameObject.scene : default;
            var scenePath = scene.IsValid() ? NormalizePath(scene.path) : null;

            return new
            {
                name = obj.name,
                type = obj.GetType().FullName,
                instanceID = UnityApiAdapter.GetObjectId(obj),
                isPersistent,
                assetPath,
                path = isPersistent ? assetPath : hierarchyPath,
                hierarchyPath,
                indexAtPath = gameObject != null && gameObject.transform != null ? gameObject.transform.GetSiblingIndex() : (int?)null,
                guid = !string.IsNullOrWhiteSpace(assetPath) ? AssetDatabase.AssetPathToGUID(assetPath) : null,
                scenePath,
                sceneGuid = !string.IsNullOrWhiteSpace(scenePath) ? AssetDatabase.AssetPathToGUID(scenePath) : null
            };
        }

        static object BuildGameObjectSelection(GameObject gameObject)
        {
            if (gameObject == null)
                return null;

            var scenePath = gameObject.scene.IsValid() ? NormalizePath(gameObject.scene.path) : null;
            return new
            {
                name = gameObject.name,
                instanceID = UnityApiAdapter.GetObjectId(gameObject),
                path = BuildGameObjectPath(gameObject),
                indexAtPath = gameObject.transform.GetSiblingIndex(),
                scenePath,
                sceneGuid = !string.IsNullOrWhiteSpace(scenePath) ? AssetDatabase.AssetPathToGUID(scenePath) : null,
                tag = gameObject.tag,
                layer = gameObject.layer,
                layerName = LayerMask.LayerToName(gameObject.layer),
                activeSelf = gameObject.activeSelf,
                isActiveInHierarchy = gameObject.activeInHierarchy
            };
        }

        static string BuildGameObjectPath(GameObject gameObject)
        {
            if (gameObject == null)
                return null;

            var names = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        static void AddAssetChangeRecords(IEnumerable<string> paths, string kind, List<object> changes)
        {
            foreach (var rawPath in paths ?? Enumerable.Empty<string>())
            {
                var path = NormalizePath(rawPath);
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var record = new AssetChangeRecord
                {
                    id = s_NextAssetChangeId++,
                    kind = kind,
                    path = path,
                    guid = kind == "deleted" ? null : AssetDatabase.AssetPathToGUID(path),
                    utc = DateTime.UtcNow.ToString("o")
                };
                AssetChanges.Add(record);
                changes.Add(record.ToDto());
            }
        }

        static void TrimAssetChanges()
        {
            while (AssetChanges.Count > MaxAssetChanges)
                AssetChanges.RemoveAt(0);
        }

        static object BuildPackageDto(UnityEditor.PackageManager.PackageInfo package)
        {
            if (package == null)
                return null;

            return new
            {
                name = package.name,
                displayName = package.displayName,
                version = package.version,
                source = package.source.ToString(),
                assetPath = NormalizePath(package.assetPath)
            };
        }

        static object[] ReadPackageDtos(PackageRegistrationEventArgs args, params string[] propertyNames)
        {
            if (args == null)
                return Array.Empty<object>();

            foreach (var propertyName in propertyNames)
            {
                var property = args.GetType().GetProperty(propertyName);
                if (property == null)
                    continue;

                if (property.GetValue(args) is IEnumerable<UnityEditor.PackageManager.PackageInfo> packages)
                    return packages.Select(BuildPackageDto).Where(item => item != null).ToArray();
            }

            return Array.Empty<object>();
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').Trim();
        }

        sealed class EditorEventRecord
        {
            public long id;
            public string kind;
            public string message;
            public double timeSinceStartup;
            public string utc;
            public object data;

            public object ToDto()
            {
                return new
                {
                    id,
                    kind,
                    message,
                    timeSinceStartup,
                    utc,
                    data
                };
            }
        }

        sealed class CompilationDiagnosticRecord
        {
            public string assemblyPath;
            public string type;
            public string message;
            public string file;
            public int line;
            public int column;
            public string utc;

            public object ToDto()
            {
                return new
                {
                    assemblyPath,
                    type,
                    message,
                    file,
                    line,
                    column,
                    utc
                };
            }
        }

        sealed class AssetChangeRecord
        {
            public long id;
            public string kind;
            public string path;
            public string oldPath;
            public string guid;
            public string utc;

            public object ToDto()
            {
                return new
                {
                    id,
                    kind,
                    path,
                    oldPath,
                    guid,
                    utc
                };
            }
        }

        public sealed class EventQuery
        {
            public HashSet<string> kinds;
            public string messageContains;
            public string textContains;
            public string playModeState;
            public string phase;
            public string assetPathContains;
        }

        public sealed class EventQueryResult
        {
            public long latestId;
            public long sinceId;
            public int totalAfterSince;
            public int count;
            public bool truncated;
            public object[] events = Array.Empty<object>();
        }
    }

    public sealed class EditorEventAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            var moved = Enumerable.Range(0, movedAssets?.Length ?? 0)
                .Select(index => (
                    from: movedFromAssetPaths != null && index < movedFromAssetPaths.Length ? movedFromAssetPaths[index] : null,
                    to: movedAssets[index]));
            EditorEventHistory.RecordAssetChanges(importedAssets, deletedAssets, moved);
        }
    }

    public static class EditorEvents
    {
        const string ToolName = "UniBridge_EditorEvents";

        public const string Title = "Read editor event deltas";

        public const string Description = @"Read a bounded delta history of Unity editor events.

Use this when an orchestrator or new agent wants structured awareness of selection, hierarchy, project, compilation, assembly reload, play mode, and object-change events without polling full context snapshots every time.

Args:
    Action: Snapshot, Get, or Clear.
    SinceId: Return events after this id.
    Limit: Maximum event count.
    IncludeSelection: Include current selection summary.
    IncludeDiagnostics: Include retained compiler diagnostics.
    IncludeAssetChanges: Include imported/deleted/moved asset deltas.

Returns:
    success, message, and event delta data with latestId.";

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
                        description = "Event history operation",
                        @enum = new[] { "Snapshot", "Get", "Clear" }
                    },
                    SinceId = new { type = "integer", description = "Return events after this id", @default = 0 },
                    Limit = new { type = "integer", description = "Maximum events to return", @default = 100 },
                    IncludeSelection = new { type = "boolean", description = "Include current selection summary", @default = true },
                    IncludeDiagnostics = new { type = "boolean", description = "Include retained compiler diagnostics", @default = true },
                    IncludeAssetChanges = new { type = "boolean", description = "Include imported/deleted/moved asset deltas", @default = true }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "editor" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = NormalizeKey(GetString(parameters, "Action", "action") ?? "Snapshot");
            if (action == "clear")
            {
                EditorEventHistory.Clear();
                return Response.Success("Editor event history cleared.", EditorEventHistory.Snapshot(0, 10, includeSelection: true));
            }

            if (action != "snapshot" && action != "get")
                return Response.Error($"Unknown Action '{GetString(parameters, "Action", "action")}'. Supported: Snapshot, Get, Clear.");

            var sinceId = GetLong(parameters, 0, "SinceId", "sinceId", "since_id");
            var limit = GetInt(parameters, 100, "Limit", "limit");
            var includeSelection = GetBool(parameters, true, "IncludeSelection", "includeSelection", "include_selection");
            var includeDiagnostics = GetBool(parameters, true, "IncludeDiagnostics", "includeDiagnostics", "include_diagnostics");
            var includeAssetChanges = GetBool(parameters, true, "IncludeAssetChanges", "includeAssetChanges", "include_asset_changes");
            return Response.Success(
                "Read Unity editor event deltas.",
                EditorEventHistory.Snapshot(sinceId, limit, includeSelection, includeDiagnostics, includeAssetChanges));
        }

        static string GetString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                    return token.ToString();
            }

            return null;
        }

        static int GetInt(JObject obj, int fallback, params string[] keys)
        {
            var value = GetString(obj, keys);
            return int.TryParse(value, out var parsed) ? parsed : fallback;
        }

        static long GetLong(JObject obj, long fallback, params string[] keys)
        {
            var value = GetString(obj, keys);
            return long.TryParse(value, out var parsed) ? parsed : fallback;
        }

        static bool GetBool(JObject obj, bool fallback, params string[] keys)
        {
            var value = GetString(obj, keys);
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }

        static string NormalizeKey(string value)
        {
            return (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }
    }

    public static class WaitForEvent
    {
        const string ToolName = "UniBridge_WaitForEvent";
        const int DefaultTimeoutMs = 10000;
        const int MaxTimeoutMs = 300000;
        const int DefaultLimit = 25;

        public const string Title = "Wait for editor event";

        public const string Description = @"Wait for a Unity editor event without blocking mutating UniBridge tools.

Use this after triggering asynchronous Unity work, or before a parallel operation, when an agent needs a concrete event instead of blind sleeps. It observes the same event history as UniBridge_EditorEvents and can wait for selection, hierarchy, project/assets, compilation, assembly reload, play mode, package, object-change, editor-ready, or next-update conditions.

Args:
    WaitFor: AnyEvent, Kind, SelectionChanged, HierarchyChanged, ProjectChanged, ProjectAssetsChanged, CompilationStarted, CompilationFinished, AssemblyReloadBefore, AssemblyReloadAfter, PlayModeState, PackagesChanged, ObjectChanges, EditorReady, or NextEditorUpdate.
    SinceId: Only consider events after this id. If omitted, StartFromLatest defaults to true so the call waits for a future event.
    StartFromLatest: When SinceId is omitted, start from the current latest event id.
    Kind/Kinds: Explicit event kind filter, for example selection, hierarchy, projectAssets, compilation, playMode.
    PlayModeState, MessageContains, TextContains, AssetPathContains: Optional extra filters.
    TimeoutMs: Maximum wait time.
    QuietMs: After a match, wait until no newer editor event arrives for this many milliseconds.
    Limit: Maximum matched events returned.

Returns:
    success, message, elapsedMs, latestId, sinceId, matched events, and timeout metadata.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    WaitFor = new
                    {
                        type = "string",
                        description = "Condition to wait for.",
                        @enum = new[]
                        {
                            "AnyEvent",
                            "Kind",
                            "SelectionChanged",
                            "HierarchyChanged",
                            "ProjectChanged",
                            "ProjectAssetsChanged",
                            "CompilationStarted",
                            "CompilationFinished",
                            "AssemblyReloadBefore",
                            "AssemblyReloadAfter",
                            "PlayModeState",
                            "PackagesChanged",
                            "ObjectChanges",
                            "EditorReady",
                            "NextEditorUpdate"
                        },
                        @default = "AnyEvent"
                    },
                    SinceId = new { type = "integer", description = "Only consider events after this id." },
                    StartFromLatest = new { type = "boolean", description = "When SinceId is omitted, wait for future events from the current latest id.", @default = true },
                    Kind = new { type = "string", description = "Single explicit event kind filter." },
                    Kinds = new { type = "array", items = new { type = "string" }, description = "Explicit event kind filters." },
                    PlayModeState = new { type = "string", description = "Expected play mode state for WaitFor=PlayModeState." },
                    MessageContains = new { type = "string", description = "Require event message to contain this text." },
                    TextContains = new { type = "string", description = "Require serialized event payload to contain this text." },
                    AssetPathContains = new { type = "string", description = "Require serialized event payload to contain this asset path/text." },
                    TimeoutMs = new { type = "integer", description = "Maximum wait time in milliseconds.", @default = DefaultTimeoutMs },
                    QuietMs = new { type = "integer", description = "After a match, wait for no newer editor event for this many milliseconds.", @default = 0 },
                    Limit = new { type = "integer", description = "Maximum matched events returned.", @default = DefaultLimit }
                },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "editor" }, EnabledByDefault = true)]
        public static async Task<object> HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();

            var timeoutMs = Clamp(GetInt(parameters, DefaultTimeoutMs, "TimeoutMs", "timeoutMs", "timeout_ms"), 1, MaxTimeoutMs);
            var quietMs = Clamp(GetInt(parameters, 0, "QuietMs", "quietMs", "quiet_ms"), 0, MaxTimeoutMs);
            var limit = Clamp(GetInt(parameters, DefaultLimit, "Limit", "limit"), 1, 100);
            var startFromLatest = GetBool(parameters, true, "StartFromLatest", "startFromLatest", "start_from_latest");
            var hasSinceId = TryGetLong(parameters, out var sinceId, "SinceId", "sinceId", "since_id");
            if (!hasSinceId && startFromLatest)
                sinceId = EditorEventHistory.LatestId();

            var waitFor = NormalizeKey(GetString(parameters, "WaitFor", "waitFor", "wait_for") ?? "AnyEvent");
            var query = BuildQuery(parameters, waitFor);
            var startedUtc = DateTime.UtcNow;
            var startLatestId = EditorEventHistory.LatestId();

            if (waitFor == "editorready" && IsEditorReady())
            {
                return Response.Success("Editor is already ready.", BuildResult(waitFor, sinceId, startLatestId, startedUtc, timedOut: false, quietMs, EditorEventHistory.Query(sinceId, limit, query)));
            }

            var initial = EditorEventHistory.Query(sinceId, limit, query);
            if (waitFor != "nexteditorupdate" && waitFor != "editorready" && initial.count > 0 && quietMs == 0)
            {
                return Response.Success("Editor event condition already matched.", BuildResult(waitFor, sinceId, startLatestId, startedUtc, timedOut: false, quietMs, initial));
            }

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var quietStartedUtc = initial.count > 0 ? DateTime.UtcNow : (DateTime?)null;
            var lastLatestId = EditorEventHistory.LatestId();
            var lastMatch = initial;
            var sawUpdate = false;

            void Tick()
            {
                if (tcs.Task.IsCompleted)
                {
                    EditorApplication.update -= Tick;
                    return;
                }

                var now = DateTime.UtcNow;
                var elapsedMs = (int)(now - startedUtc).TotalMilliseconds;
                if (elapsedMs >= timeoutMs)
                {
                    EditorApplication.update -= Tick;
                    var timeoutResult = BuildResult(waitFor, sinceId, startLatestId, startedUtc, timedOut: true, quietMs, lastMatch);
                    tcs.TrySetResult(Response.Error($"Timed out after {timeoutMs}ms waiting for editor event condition '{waitFor}'.", timeoutResult));
                    return;
                }

                if (waitFor == "nexteditorupdate")
                {
                    if (sawUpdate)
                    {
                        EditorApplication.update -= Tick;
                        var updateQuery = EditorEventHistory.Query(sinceId, limit, query);
                        tcs.TrySetResult(Response.Success("Observed next editor update.", BuildResult(waitFor, sinceId, startLatestId, startedUtc, timedOut: false, quietMs, updateQuery)));
                    }
                    sawUpdate = true;
                    return;
                }

                if (waitFor == "editorready")
                {
                    if (IsEditorReady())
                    {
                        EditorApplication.update -= Tick;
                        var readyQuery = EditorEventHistory.Query(sinceId, limit, query);
                        tcs.TrySetResult(Response.Success("Editor is ready.", BuildResult(waitFor, sinceId, startLatestId, startedUtc, timedOut: false, quietMs, readyQuery)));
                    }
                    return;
                }

                var currentLatestId = EditorEventHistory.LatestId();
                var current = EditorEventHistory.Query(sinceId, limit, query);
                if (current.count <= 0)
                    return;

                lastMatch = current;
                if (quietMs <= 0)
                {
                    EditorApplication.update -= Tick;
                    tcs.TrySetResult(Response.Success("Editor event condition matched.", BuildResult(waitFor, sinceId, startLatestId, startedUtc, timedOut: false, quietMs, current)));
                    return;
                }

                if (currentLatestId != lastLatestId || quietStartedUtc == null)
                {
                    lastLatestId = currentLatestId;
                    quietStartedUtc = now;
                    return;
                }

                if ((now - quietStartedUtc.Value).TotalMilliseconds >= quietMs && IsEditorReady())
                {
                    EditorApplication.update -= Tick;
                    tcs.TrySetResult(Response.Success("Editor event condition matched and quiet window elapsed.", BuildResult(waitFor, sinceId, startLatestId, startedUtc, timedOut: false, quietMs, current)));
                }
            }

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.QueuePlayerLoopUpdate();
            return await tcs.Task;
        }

        static object BuildResult(
            string waitFor,
            long sinceId,
            long startLatestId,
            DateTime startedUtc,
            bool timedOut,
            int quietMs,
            EditorEventHistory.EventQueryResult query)
        {
            query ??= new EditorEventHistory.EventQueryResult
            {
                latestId = EditorEventHistory.LatestId(),
                sinceId = sinceId,
                events = Array.Empty<object>()
            };

            return new
            {
                waitFor,
                timedOut,
                elapsedMs = (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds,
                quietMs,
                sinceId,
                startLatestId,
                latestId = query.latestId,
                totalAfterSince = query.totalAfterSince,
                matchCount = query.count,
                truncated = query.truncated,
                editorReady = IsEditorReady(),
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                isPlaying = EditorApplication.isPlaying,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                events = query.events
            };
        }

        static EditorEventHistory.EventQuery BuildQuery(JObject parameters, string waitFor)
        {
            var query = new EditorEventHistory.EventQuery
            {
                kinds = ResolveKinds(parameters, waitFor),
                messageContains = GetString(parameters, "MessageContains", "messageContains", "message_contains"),
                textContains = GetString(parameters, "TextContains", "textContains", "text_contains"),
                playModeState = GetString(parameters, "PlayModeState", "playModeState", "play_mode_state"),
                assetPathContains = GetString(parameters, "AssetPathContains", "assetPathContains", "asset_path_contains")
            };

            switch (waitFor)
            {
                case "compilationstarted":
                    query.kinds = SetOf("compilation");
                    query.messageContains ??= "started";
                    break;
                case "compilationfinished":
                    query.kinds = SetOf("compilation");
                    query.messageContains ??= "finished";
                    break;
                case "assemblyreloadbefore":
                    query.kinds = SetOf("assemblyreload");
                    query.phase = "before";
                    break;
                case "assemblyreloadafter":
                    query.kinds = SetOf("assemblyreload");
                    query.phase = "after";
                    break;
                case "playmodestate":
                    query.kinds = SetOf("playmode");
                    break;
            }

            return query;
        }

        static HashSet<string> ResolveKinds(JObject parameters, string waitFor)
        {
            var explicitKinds = new List<string>();
            var kind = GetString(parameters, "Kind", "kind");
            if (!string.IsNullOrWhiteSpace(kind))
                explicitKinds.Add(kind);

            if (parameters.TryGetValue("Kinds", StringComparison.OrdinalIgnoreCase, out var kindsToken) && kindsToken is JArray kindsArray)
                explicitKinds.AddRange(kindsArray.Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)));

            if (explicitKinds.Count > 0)
                return new HashSet<string>(explicitKinds.Select(NormalizeKey), StringComparer.Ordinal);

            return waitFor switch
            {
                "selectionchanged" => SetOf("selection"),
                "hierarchychanged" => SetOf("hierarchy", "objectchanges"),
                "projectchanged" => SetOf("project", "projectassets", "packages"),
                "projectassetschanged" => SetOf("projectassets"),
                "packageschanged" => SetOf("packages"),
                "objectchanges" => SetOf("objectchanges"),
                "kind" => new HashSet<string>(StringComparer.Ordinal),
                _ => new HashSet<string>(StringComparer.Ordinal)
            };
        }

        static HashSet<string> SetOf(params string[] values)
        {
            return new HashSet<string>((values ?? Array.Empty<string>()).Select(NormalizeKey), StringComparer.Ordinal);
        }

        static bool IsEditorReady()
        {
            return !EditorApplication.isCompiling &&
                   !EditorApplication.isUpdating &&
                   !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        static string GetString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                    return token.ToString();
            }

            return null;
        }

        static bool GetBool(JObject obj, bool fallback, params string[] keys)
        {
            var value = GetString(obj, keys);
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }

        static int GetInt(JObject obj, int fallback, params string[] keys)
        {
            var value = GetString(obj, keys);
            return int.TryParse(value, out var parsed) ? parsed : fallback;
        }

        static bool TryGetLong(JObject obj, out long value, params string[] keys)
        {
            var raw = GetString(obj, keys);
            return long.TryParse(raw, out value);
        }

        static int Clamp(int value, int min, int max)
        {
            return Mathf.Clamp(value, min, max);
        }

        static string NormalizeKey(string value)
        {
            return (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }
    }
}
