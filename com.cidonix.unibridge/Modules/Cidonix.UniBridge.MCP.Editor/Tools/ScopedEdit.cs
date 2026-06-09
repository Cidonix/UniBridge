#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Opens a scene or prefab scope, executes a bounded batch, saves, and restores editor state.
    /// </summary>
    public static class ScopedEdit
    {
        const string ToolName = "UniBridge_ScopedEdit";

        public const string Title = "Run actions inside a scene or prefab scope";

        public const string Description = @"Open a .unity scene or .prefab asset as an editing scope, run UniBridge_BatchActions inside it, then optionally save and restore the previous editor state.

Use this when an agent needs to edit a specific scene or prefab asset without manually orchestrating EditorSnapshot, ManagePrefab, ManageScene, and ManageGameObject. The target scope is opened, made active, nested steps execute in that context, and the tool reports what was opened, saved, and restored.

Args:
    ScopePath: Required Assets/... .unity or .prefab path.
    Steps: Required UniBridge_BatchActions steps to run inside the scope.
    DryRun: Defaults true and is forwarded into the nested batch.
    SaveScope: Defaults true for executing batches, false for dry-runs.
    RestoreEditorState: Defaults true. Reopens the prior prefab stage or active scene and restores selection when possible.
    SaveCurrentPrefabStage: Defaults true before switching scopes.

Returns:
    success, message, and data with scope metadata, nested batch result, save/restore report, and selection/scene notes.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    ScopePath = new { type = "string", description = "Assets/... .unity or .prefab path to edit." },
                    scope = new { type = "string", description = "Alias for ScopePath." },
                    DryRun = new { type = "boolean", description = "Validate nested steps without applying changes. Defaults to true.", @default = true },
                    SaveScope = new { type = "boolean", description = "Save the target scene/prefab after successful execution. Defaults to true when DryRun=false." },
                    RestoreEditorState = new { type = "boolean", description = "Restore prior prefab stage/active scene and selection after the scoped edit. Defaults to true.", @default = true },
                    SaveCurrentPrefabStage = new { type = "boolean", description = "Save the currently open prefab stage before switching scope. Defaults to true.", @default = true },
                    Name = new { type = "string", description = "Optional batch/undo label." },
                    Steps = new
                    {
                        type = "array",
                        description = "Nested UniBridge_BatchActions steps to execute inside the scope.",
                        items = new { type = "object", additionalProperties = true }
                    }
                },
                required = new[] { "ScopePath", "Steps" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "assets", "workflow" }, EnabledByDefault = true)]
        public static async Task<object> HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var scopePath = NormalizeAssetPath(GetString(parameters, "ScopePath", "scope", "Scope", "path", "Path"));
            if (string.IsNullOrWhiteSpace(scopePath))
                return Response.Error("ScopePath is required and must point to an Assets/... .unity or .prefab asset.");

            var isPrefab = scopePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
            var isScene = scopePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
            if (!isPrefab && !isScene)
                return Response.Error($"ScopePath '{scopePath}' must end with .unity or .prefab.");

            var steps = GetArray(parameters, "Steps", "steps", "Actions", "actions");
            if (steps == null || steps.Count == 0)
                return Response.Error("Steps array is required.");

            var dryRun = GetBool(parameters, true, "DryRun", "dryRun", "dry_run");
            var saveScope = GetBool(parameters, !dryRun, "SaveScope", "saveScope", "save_scope");
            var restoreEditorState = GetBool(parameters, true, "RestoreEditorState", "restoreEditorState", "restore_editor_state");
            var saveCurrentPrefabStage = GetBool(parameters, true, "SaveCurrentPrefabStage", "saveCurrentPrefabStage", "save_current_prefab_stage");
            var name = GetString(parameters, "Name", "name") ?? $"Scoped edit {scopePath}";

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return Response.Error("Scoped scene/prefab editing is not available while entering or running Play Mode.");

            ScopedState state = null;
            try
            {
                state = OpenScope(scopePath, isPrefab, restoreEditorState, saveCurrentPrefabStage);

                var batch = BuildNestedBatch(parameters, steps, dryRun, name, scopePath, isScene);
                var batchResult = await BatchActions.HandleCommand(batch);
                var batchJson = JObject.FromObject(batchResult);
                var batchSuccess = batchJson.Value<bool?>("success") ?? true;

                object saveResult = null;
                if (batchSuccess && !dryRun && saveScope)
                {
                    saveResult = SaveScope(state);
                }

                object restoreResult = null;
                if (restoreEditorState)
                {
                    restoreResult = RestoreState(state);
                }

                var data = new
                {
                    scope = BuildScopeSnapshot(state),
                    dryRun,
                    saveScope,
                    batch = batchJson,
                    save = saveResult,
                    restore = restoreResult
                };

                return batchSuccess
                    ? Response.Success(dryRun ? "Scoped edit dry-run completed." : "Scoped edit completed.", data)
                    : Response.Error("Scoped edit batch failed.", data);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ScopedEdit] Failed for scope '{scopePath}': {ex}");
                object restoreResult = null;
                if (restoreEditorState && state != null)
                {
                    try { restoreResult = RestoreState(state); }
                    catch (Exception restoreEx) { restoreResult = new { restored = false, error = restoreEx.Message }; }
                }

                return Response.Error($"Scoped edit failed: {ex.Message}", new
                {
                    scopePath,
                    restoredAfterFailure = restoreResult
                });
            }
        }

        static ScopedState OpenScope(string scopePath, bool isPrefab, bool restoreEditorState, bool saveCurrentPrefabStage)
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(scopePath) == null)
                throw new InvalidOperationException($"Scope asset was not found at '{scopePath}'.");

            var state = new ScopedState
            {
                ScopePath = scopePath,
                IsPrefab = isPrefab,
                RestoreEditorState = restoreEditorState,
                PreviousSelectionIds = Selection.objects?.Where(o => o != null).Select(UnityApiAdapter.GetObjectId).ToArray() ?? Array.Empty<long>(),
                PreviousActiveScenePath = SceneManager.GetActiveScene().path,
                PreviouslyLoadedScenePaths = GetLoadedScenePaths(),
                PreviousPrefabStagePath = PrefabStageUtility.GetCurrentPrefabStage()?.assetPath
            };

            if (saveCurrentPrefabStage)
                SaveCurrentPrefabStageIfAny();

            if (isPrefab)
            {
                var current = PrefabStageUtility.GetCurrentPrefabStage();
                if (current == null || !string.Equals(current.assetPath, scopePath, StringComparison.OrdinalIgnoreCase))
                    current = PrefabStageUtility.OpenPrefab(scopePath);

                if (current == null || !current.scene.IsValid() || !current.scene.isLoaded)
                    throw new InvalidOperationException($"Prefab Stage failed to open for '{scopePath}'.");

                state.ScopeScene = current.scene;
                state.PrefabRoot = current.prefabContentsRoot;
                return state;
            }

            var scene = FindLoadedScene(scopePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                var currentStage = PrefabStageUtility.GetCurrentPrefabStage();
                if (currentStage != null)
                    StageUtility.GoToMainStage();

                scene = EditorSceneManager.OpenScene(scopePath, OpenSceneMode.Additive);
                state.OpenedSceneForScope = true;
            }

            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException($"Scene failed to open for '{scopePath}'.");

            SceneManager.SetActiveScene(scene);
            state.ScopeScene = scene;
            return state;
        }

        static object SaveScope(ScopedState state)
        {
            if (state == null)
                return new { saved = false, error = "No scope state." };

            if (state.IsPrefab)
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage == null || !string.Equals(stage.assetPath, state.ScopePath, StringComparison.OrdinalIgnoreCase))
                    stage = PrefabStageUtility.OpenPrefab(state.ScopePath);

                if (stage == null || stage.prefabContentsRoot == null)
                    return new { saved = false, path = state.ScopePath, error = "Prefab Stage is not open." };

                VersionControlUtility.EnsureAssetEditable(state.ScopePath, checkout: true, throwOnBlocked: true);
                var prefab = PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, state.ScopePath);
                AssetDatabase.SaveAssets();
                return new { saved = prefab != null, path = state.ScopePath, kind = "prefab" };
            }

            if (!state.ScopeScene.IsValid())
                return new { saved = false, path = state.ScopePath, error = "Scope scene is invalid." };

            VersionControlUtility.EnsureAssetEditable(state.ScopePath, checkout: true, throwOnBlocked: true);
            var saved = EditorSceneManager.SaveScene(state.ScopeScene, state.ScopePath);
            AssetDatabase.SaveAssets();
            return new { saved, path = state.ScopePath, kind = "scene" };
        }

        static object RestoreState(ScopedState state)
        {
            if (state == null)
                return new { restored = false, error = "No scope state." };

            var notes = new List<string>();

            if (state.OpenedSceneForScope && state.ScopeScene.IsValid() && state.ScopeScene.isLoaded)
            {
                var stillLoadedBefore = state.PreviouslyLoadedScenePaths.Contains(state.ScopePath, StringComparer.OrdinalIgnoreCase);
                if (!stillLoadedBefore)
                {
                    EditorSceneManager.CloseScene(state.ScopeScene, removeScene: true);
                    notes.Add($"Closed scoped scene '{state.ScopePath}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(state.PreviousPrefabStagePath))
            {
                var current = PrefabStageUtility.GetCurrentPrefabStage();
                if (current == null || !string.Equals(current.assetPath, state.PreviousPrefabStagePath, StringComparison.OrdinalIgnoreCase))
                {
                    PrefabStageUtility.OpenPrefab(state.PreviousPrefabStagePath);
                    notes.Add($"Restored prefab stage '{state.PreviousPrefabStagePath}'.");
                }
            }
            else
            {
                var current = PrefabStageUtility.GetCurrentPrefabStage();
                if (current != null)
                {
                    StageUtility.GoToMainStage();
                    notes.Add("Returned to main stage.");
                }
            }

            var activeScene = FindLoadedScene(state.PreviousActiveScenePath);
            if (activeScene.IsValid() && activeScene.isLoaded)
            {
                SceneManager.SetActiveScene(activeScene);
                notes.Add($"Restored active scene '{state.PreviousActiveScenePath}'.");
            }

            var restoredSelection = state.PreviousSelectionIds
                .Select(id => UnityApiAdapter.GetObjectFromId(id))
                .Where(o => o != null)
                .ToArray();
            Selection.objects = restoredSelection;

            return new
            {
                restored = true,
                activeScenePath = SceneManager.GetActiveScene().path,
                prefabStagePath = PrefabStageUtility.GetCurrentPrefabStage()?.assetPath,
                restoredSelectionCount = restoredSelection.Length,
                notes
            };
        }

        static JObject BuildNestedBatch(JObject original, JArray steps, bool dryRun, string name, string scopePath, bool isScene)
        {
            var batch = new JObject
            {
                ["DryRun"] = dryRun,
                ["ValidateBeforeExecute"] = GetBool(original, true, "ValidateBeforeExecute", "validateBeforeExecute", "validate_before_execute"),
                ["StopOnError"] = GetBool(original, true, "StopOnError", "stopOnError", "stop_on_error"),
                ["UseUndoGroup"] = GetBool(original, true, "UseUndoGroup", "useUndoGroup", "use_undo_group"),
                ["RollbackOnFailure"] = GetBool(original, true, "RollbackOnFailure", "rollbackOnFailure", "rollback_on_failure"),
                ["RollbackAssets"] = GetBool(original, true, "RollbackAssets", "rollbackAssets", "rollback_assets"),
                ["Name"] = name,
                ["Steps"] = InjectScopeHints(steps, scopePath, isScene)
            };

            return batch;
        }

        static JArray InjectScopeHints(JArray steps, string scopePath, bool isScene)
        {
            var copy = new JArray();
            foreach (var stepToken in steps)
            {
                var step = stepToken is JObject obj ? (JObject)obj.DeepClone() : new JObject();
                var tool = step.Value<string>("tool") ?? step.Value<string>("Tool");
                var parameters = step["parameters"] as JObject ?? step["Parameters"] as JObject;
                if (parameters == null)
                {
                    parameters = new JObject();
                    foreach (var property in step.Properties().ToArray())
                    {
                        if (IsStepMetadata(property.Name))
                            continue;
                        parameters[property.Name] = property.Value.DeepClone();
                        property.Remove();
                    }

                    step["parameters"] = parameters;
                }

                if (isScene)
                {
                    var resolved = BatchActionToolCatalog.ResolveToolName(tool);
                    if (string.Equals(resolved, "UniBridge_ManagePrefab", StringComparison.Ordinal) && parameters["scene_path"] == null)
                        parameters["scene_path"] = scopePath;
                    if (string.Equals(resolved, "UniBridge_SceneObjectView", StringComparison.Ordinal) && parameters["ScenePath"] == null && parameters["scene_path"] == null)
                        parameters["ScenePath"] = scopePath;
                    if (string.Equals(resolved, "UniBridge_UnitySearch", StringComparison.Ordinal) && parameters["ScenePath"] == null && parameters["scene_path"] == null)
                        parameters["ScenePath"] = scopePath;
                }

                copy.Add(step);
            }

            return copy;
        }

        static bool IsStepMetadata(string name)
        {
            return string.Equals(name, "id", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "tool", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "description", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "optional", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "skip", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "parameters", StringComparison.OrdinalIgnoreCase);
        }

        static object BuildScopeSnapshot(ScopedState state)
        {
            return new
            {
                path = state.ScopePath,
                kind = state.IsPrefab ? "prefab" : "scene",
                openedSceneForScope = state.OpenedSceneForScope,
                scene = state.ScopeScene.IsValid()
                    ? new { name = state.ScopeScene.name, path = state.ScopeScene.path, isLoaded = state.ScopeScene.isLoaded, isDirty = state.ScopeScene.isDirty }
                    : null,
                prefabRoot = state.PrefabRoot != null
                    ? new { name = state.PrefabRoot.name, instanceId = UnityApiAdapter.GetObjectId(state.PrefabRoot) }
                    : null
            };
        }

        static void SaveCurrentPrefabStageIfAny()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage?.prefabContentsRoot == null || string.IsNullOrWhiteSpace(stage.assetPath))
                return;

            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath);
        }

        static Scene FindLoadedScene(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return default;

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() &&
                    scene.isLoaded &&
                    (string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(scene.name, path, StringComparison.OrdinalIgnoreCase)))
                {
                    return scene;
                }
            }

            return default;
        }

        static HashSet<string> GetLoadedScenePaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded && !string.IsNullOrWhiteSpace(scene.path))
                    paths.Add(scene.path);
            }

            return paths;
        }

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = path.Trim().Replace('\\', '/').TrimStart('/');
            if (normalized.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("unity://path/".Length);
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return normalized;
            return "Assets/" + normalized;
        }

        static string GetString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                {
                    var value = token.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }
            }

            return null;
        }

        static bool GetBool(JObject obj, bool defaultValue, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null &&
                    bool.TryParse(token.ToString(), out var value))
                {
                    return value;
                }
            }

            return defaultValue;
        }

        static JArray GetArray(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token is JArray array)
                    return array;
            }

            return null;
        }

        sealed class ScopedState
        {
            public string ScopePath;
            public bool IsPrefab;
            public bool RestoreEditorState;
            public bool OpenedSceneForScope;
            public Scene ScopeScene;
            public GameObject PrefabRoot;
            public string PreviousActiveScenePath;
            public string PreviousPrefabStagePath;
            public HashSet<string> PreviouslyLoadedScenePaths;
            public long[] PreviousSelectionIds;
        }
    }
}
