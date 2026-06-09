#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Captures and restores AI-relevant Unity Editor working state.
    /// </summary>
    public static class EditorSnapshotTool
    {
        const int DefaultListLimit = 50;
        const int MaxListLimit = 200;

        static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        public const string Title = "Capture or restore Editor state";

        public const string Description = @"Capture, inspect, and restore Unity Editor working state.

Use this when an AI agent needs to temporarily change the Editor context and then put the user's workspace back: loaded scenes, active scene, Scene View camera, selection, active tool, Prefab Mode, active dock tabs, Prefab Mode autosave settings, and focused window.

Actions:
    Capture: Capture current Editor state. By default the snapshot is persisted under Library/UniBridge/EditorSnapshots.
    Restore: Restore a persisted or inline snapshot. Supports DryRun and protects dirty scenes by default.
    List: List persisted snapshots.
    Inspect: Return one persisted snapshot by id.
    Delete: Delete one persisted snapshot by id.
    Clear: Delete persisted snapshots.

Restore safety:
    Scene restore refuses to reload or close dirty scenes unless SaveDirtyScenes or AllowDirtySceneReload is true.
    DryRun reports the exact restore plan without changing Unity.
    Window/layout restore is intentionally conservative: it focuses matching window types and active dock tabs, and optionally restores maximized state, but does not rewrite Unity layouts.";

        [McpTool("UniBridge_EditorSnapshot", Description, Title, Groups = new[] { "core", "editor", "scene" }, EnabledByDefault = true)]
        public static object HandleCommand(EditorSnapshotParams parameters)
        {
            parameters ??= new EditorSnapshotParams();

            try
            {
                switch (parameters.Action)
                {
                    case EditorSnapshotAction.Capture:
                        return Capture(parameters);
                    case EditorSnapshotAction.Restore:
                        return Restore(parameters);
                    case EditorSnapshotAction.List:
                        return ListSnapshots(parameters);
                    case EditorSnapshotAction.Inspect:
                        return Inspect(parameters);
                    case EditorSnapshotAction.Delete:
                        return Delete(parameters);
                    case EditorSnapshotAction.Clear:
                        return Clear(parameters);
                    default:
                        return Response.Error($"Unsupported EditorSnapshot action '{parameters.Action}'.");
                }
            }
            catch (Exception ex)
            {
                return Response.Error($"Editor snapshot action '{parameters.Action}' failed: {ex.Message}");
            }
        }

        static object Capture(EditorSnapshotParams parameters)
        {
            var identity = ProjectIdentity.GetOrCreate();
            var snapshot = CaptureSnapshot(identity, parameters);
            var persist = parameters.Persist ?? true;
            string filePath = null;

            if (persist)
            {
                filePath = SaveSnapshot(snapshot);
            }

            return Response.Success(
                persist ? $"Editor snapshot captured and saved as '{snapshot.snapshotId}'." : $"Editor snapshot captured as '{snapshot.snapshotId}'.",
                new
                {
                    snapshotId = snapshot.snapshotId,
                    name = snapshot.name,
                    persisted = persist,
                    filePath = NormalizePath(filePath),
                    snapshot
                });
        }

        static object Restore(EditorSnapshotParams parameters)
        {
            var snapshot = LoadSnapshot(parameters);
            if (snapshot == null)
            {
                return Response.Error("Restore requires SnapshotId or SnapshotJson.");
            }

            var options = RestoreOptions.From(parameters);
            var plan = BuildRestorePlan(snapshot, options);

            if (!plan.canRestore)
            {
                return Response.Error("Editor snapshot restore is blocked by safety checks.", new
                {
                    snapshotId = snapshot.snapshotId,
                    dryRun = options.DryRun,
                    plan
                });
            }

            if (options.DryRun)
            {
                return Response.Success("Editor snapshot restore dry-run completed.", new
                {
                    snapshotId = snapshot.snapshotId,
                    dryRun = true,
                    plan
                });
            }

            var applied = new List<string>();
            var warnings = new List<string>(plan.warnings ?? Array.Empty<string>());

            if (options.RestoreScenes)
            {
                RestoreScenes(snapshot, options, applied, warnings);
            }

            if (options.RestorePrefabStage)
            {
                RestorePrefabStage(snapshot, applied, warnings);
            }

            if (options.RestorePrefabAutoSave)
            {
                RestorePrefabAutoSave(snapshot, applied, warnings);
            }

            if (options.RestoreSceneView)
            {
                RestoreSceneView(snapshot, applied, warnings);
            }

            if (options.RestoreSelection)
            {
                RestoreSelection(snapshot, applied, warnings);
            }

            if (options.RestoreActiveTool)
            {
                RestoreActiveTool(snapshot, applied, warnings);
            }

            if (options.RestoreDockTabs)
            {
                RestoreDockTabs(snapshot, applied, warnings);
            }

            if (options.RestoreFocusedWindow)
            {
                RestoreFocusedWindow(snapshot, options, applied, warnings);
            }

            return Response.Success("Editor snapshot restored.", new
            {
                snapshotId = snapshot.snapshotId,
                restored = true,
                applied,
                warnings,
                current = BuildCurrentSummary()
            });
        }

        static object ListSnapshots(EditorSnapshotParams parameters)
        {
            var limit = Mathf.Clamp(parameters.Limit ?? DefaultListLimit, 1, MaxListLimit);
            var snapshots = Directory.Exists(SnapshotDirectory)
                ? Directory.GetFiles(SnapshotDirectory, "*.json")
                    .Select(ReadSnapshotSummary)
                    .Where(summary => summary != null)
                    .OrderByDescending(summary => summary.createdUtc)
                    .Take(limit)
                    .ToArray()
                : Array.Empty<SnapshotSummary>();

            return Response.Success("Editor snapshots listed.", new
            {
                directory = NormalizePath(SnapshotDirectory),
                count = snapshots.Length,
                snapshots
            });
        }

        static object Inspect(EditorSnapshotParams parameters)
        {
            var snapshot = LoadSnapshot(parameters);
            if (snapshot == null)
            {
                return Response.Error("Inspect requires SnapshotId or SnapshotJson.");
            }

            return Response.Success($"Editor snapshot '{snapshot.snapshotId}' loaded.", new
            {
                snapshotId = snapshot.snapshotId,
                snapshot
            });
        }

        static object Delete(EditorSnapshotParams parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters.SnapshotId))
            {
                return Response.Error("Delete requires SnapshotId.");
            }

            var path = GetSnapshotPath(parameters.SnapshotId);
            if (!File.Exists(path))
            {
                return Response.Error($"Snapshot '{parameters.SnapshotId}' was not found.");
            }

            File.Delete(path);
            return Response.Success($"Editor snapshot '{parameters.SnapshotId}' deleted.", new
            {
                snapshotId = parameters.SnapshotId,
                deleted = true
            });
        }

        static object Clear(EditorSnapshotParams parameters)
        {
            if (!Directory.Exists(SnapshotDirectory))
            {
                return Response.Success("No editor snapshots to clear.", new { deleted = 0 });
            }

            var files = Directory.GetFiles(SnapshotDirectory, "*.json");
            foreach (var file in files)
            {
                File.Delete(file);
            }

            return Response.Success("Editor snapshots cleared.", new
            {
                directory = NormalizePath(SnapshotDirectory),
                deleted = files.Length
            });
        }

        static EditorSnapshotData CaptureSnapshot(ProjectIdentity.Snapshot identity, EditorSnapshotParams parameters)
        {
            var now = DateTime.UtcNow;
            var snapshot = new EditorSnapshotData
            {
                snapshotId = CreateSnapshotId(now),
                name = string.IsNullOrWhiteSpace(parameters.Name) ? null : parameters.Name.Trim(),
                createdUtc = now.ToString("O"),
                project = new ProjectData
                {
                    id = identity.ProjectId,
                    name = identity.ProjectName,
                    root = NormalizePath(identity.ProjectRoot),
                    unityVersion = Application.unityVersion
                },
                scenes = CaptureScenes(),
                activeTool = CaptureActiveTool()
            };

            if (parameters.IncludeSceneView ?? true)
            {
                snapshot.sceneView = CaptureSceneView();
            }

            if (parameters.IncludeSelection ?? true)
            {
                snapshot.selection = CaptureSelection();
            }

            if (parameters.IncludeWindows ?? true)
            {
                snapshot.focusedWindow = CaptureFocusedWindow();
                snapshot.windows = CaptureWindows();
                if (parameters.IncludeDockTabs ?? true)
                {
                    snapshot.dockTabs = CaptureDockTabs();
                }
            }

            if (parameters.IncludePrefabStage ?? true)
            {
                snapshot.prefabStage = CapturePrefabStage();
            }

            if (parameters.IncludePrefabAutoSave ?? true)
            {
                snapshot.prefabAutoSave = CapturePrefabAutoSave();
            }

            return snapshot;
        }

        static ScenesData CaptureScenes()
        {
            var loaded = new List<SceneData>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid())
                {
                    continue;
                }

                loaded.Add(new SceneData
                {
                    path = NormalizePath(scene.path),
                    name = scene.name,
                    isLoaded = scene.isLoaded,
                    isDirty = scene.isDirty,
                    buildIndex = scene.buildIndex,
                    rootCount = scene.isLoaded ? scene.rootCount : 0
                });
            }

            var active = SceneManager.GetActiveScene();
            return new ScenesData
            {
                activeScenePath = NormalizePath(active.path),
                activeSceneName = active.name,
                loadedScenes = loaded.ToArray()
            };
        }

        static SceneViewData CaptureSceneView()
        {
            var sceneView = SceneView.lastActiveSceneView ?? SceneView.sceneViews.OfType<SceneView>().FirstOrDefault();
            if (sceneView == null)
            {
                return null;
            }

            return new SceneViewData
            {
                exists = true,
                pivot = ToVector3(sceneView.pivot),
                rotation = ToQuaternion(sceneView.rotation),
                size = sceneView.size,
                orthographic = sceneView.orthographic,
                in2DMode = sceneView.in2DMode,
                cameraMode = sceneView.cameraMode.drawMode.ToString()
            };
        }

        static SelectionData CaptureSelection()
        {
            var objects = Selection.objects ?? Array.Empty<Object>();
            return new SelectionData
            {
                activeGlobalId = ToGlobalIdString(Selection.activeObject),
                activeName = Selection.activeObject == null ? null : Selection.activeObject.name,
                objects = objects.Select(ToObjectRef).Where(item => item != null).ToArray()
            };
        }

        static ActiveToolData CaptureActiveTool()
        {
            try
            {
                return new ActiveToolData
                {
                    tool = UnityEditor.Tools.current.ToString(),
                    pivotMode = UnityEditor.Tools.pivotMode.ToString(),
                    pivotRotation = UnityEditor.Tools.pivotRotation.ToString()
                };
            }
            catch
            {
                return null;
            }
        }

        static PrefabStageData CapturePrefabStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                return new PrefabStageData { isOpen = false };
            }

            return new PrefabStageData
            {
                isOpen = true,
                assetPath = NormalizePath(stage.assetPath),
                prefabRootName = stage.prefabContentsRoot == null ? null : stage.prefabContentsRoot.name,
                isDirty = stage.scene.IsValid() && stage.scene.isDirty
            };
        }

        static WindowData CaptureFocusedWindow()
        {
            return ToWindowData(EditorWindow.focusedWindow, true);
        }

        static WindowData[] CaptureWindows()
        {
            return Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(window => window != null)
                .Select(window => ToWindowData(window, window == EditorWindow.focusedWindow))
                .Where(window => window != null)
                .OrderByDescending(window => window.isFocused)
                .ThenBy(window => window.typeName)
                .Take(100)
                .ToArray();
        }

        static DockTabData[] CaptureDockTabs()
        {
            var tabs = new List<DockTabData>();
            var seenDockAreas = new HashSet<long>();
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null)
                    continue;

                try
                {
                    if (GetMemberValue(window, "m_Parent") is not Object dockAreaObject || dockAreaObject == null)
                        continue;

                    var dockAreaType = dockAreaObject.GetType();
                    if (!string.Equals(dockAreaType.Name, "DockArea", StringComparison.Ordinal))
                        continue;

                    var dockAreaId = UnityApiAdapter.GetObjectId(dockAreaObject);
                    if (!seenDockAreas.Add(dockAreaId))
                        continue;

                    var panes = ReadEditorWindowList(GetMemberValue(dockAreaObject, "m_Panes"));
                    var selectedIndex = ReadIntMember(dockAreaObject, "m_Selected");
                    if (panes == null || selectedIndex < 0 || selectedIndex >= panes.Count)
                        continue;

                    var selected = panes[selectedIndex];
                    if (selected == null)
                        continue;

                    tabs.Add(new DockTabData
                    {
                        dockAreaInstanceId = dockAreaId,
                        selectedIndex = selectedIndex,
                        window = ToWindowData(selected, selected == EditorWindow.focusedWindow)
                    });
                }
                catch
                {
                    // DockArea internals differ between Unity versions; skip unreadable tabs.
                }
            }

            return tabs.ToArray();
        }

        static PrefabAutoSaveData CapturePrefabAutoSave()
        {
            return new PrefabAutoSaveData
            {
                prefabModeAllowAutoSave = EditorSettings.prefabModeAllowAutoSave,
                stageNavigationAutoSave = TryGetStageNavigationAutoSave(out var autoSave) ? autoSave : null
            };
        }

        static RestorePlan BuildRestorePlan(EditorSnapshotData snapshot, RestoreOptions options)
        {
            var actions = new List<string>();
            var warnings = new List<string>();
            var blockers = new List<string>();

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                blockers.Add("Unity is compiling or updating assets.");
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode && (options.RestoreScenes || options.RestorePrefabStage))
            {
                blockers.Add("Scene or Prefab Stage restore is blocked while Unity is in or entering play mode.");
            }

            if (options.RestoreScenes && snapshot.scenes?.loadedScenes != null)
            {
                var currentScenes = GetLoadedScenes().ToArray();
                var targetPaths = snapshot.scenes.loadedScenes
                    .Select(scene => scene.path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(NormalizePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var currentPaths = currentScenes
                    .Select(scene => NormalizePath(scene.path))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var missing = targetPaths.Except(currentPaths, StringComparer.OrdinalIgnoreCase).ToArray();
                var extra = currentPaths.Except(targetPaths, StringComparer.OrdinalIgnoreCase).ToArray();
                var dirty = currentScenes.Where(scene => scene.isDirty).Select(scene => new { scene.name, path = NormalizePath(scene.path) }).ToArray();

                if (missing.Length > 0 && options.OpenMissingScenes)
                {
                    actions.Add($"Open {missing.Length} missing scene(s).");
                }

                if (extra.Length > 0 && options.CloseExtraScenes)
                {
                    actions.Add($"Close/reload {extra.Length} extra scene(s).");
                }

                if (!string.IsNullOrWhiteSpace(snapshot.scenes.activeScenePath))
                {
                    actions.Add($"Set active scene to '{snapshot.scenes.activeScenePath}'.");
                }

                if (dirty.Length > 0 && (missing.Length > 0 || (extra.Length > 0 && options.CloseExtraScenes)))
                {
                    if (options.SaveDirtyScenes)
                    {
                        actions.Add($"Save {dirty.Length} dirty scene(s) before restore.");
                    }
                    else if (!options.AllowDirtySceneReload)
                    {
                        blockers.Add("Scene restore would reload/close dirty scenes. Set SaveDirtyScenes or AllowDirtySceneReload to proceed.");
                    }
                    else
                    {
                        warnings.Add("Dirty scenes may lose unsaved changes because AllowDirtySceneReload is true.");
                    }
                }
            }

            if (options.RestorePrefabStage && snapshot.prefabStage != null)
            {
                actions.Add(snapshot.prefabStage.isOpen
                    ? $"Open Prefab Stage '{snapshot.prefabStage.assetPath}'."
                    : "Return to Main Stage.");
            }

            if (options.RestorePrefabAutoSave && snapshot.prefabAutoSave != null)
            {
                actions.Add("Restore Prefab Mode autosave settings.");
            }

            if (options.RestoreSceneView && snapshot.sceneView?.exists == true)
            {
                actions.Add("Restore Scene View camera.");
            }

            if (options.RestoreSelection && snapshot.selection?.objects != null)
            {
                actions.Add($"Restore selection ({snapshot.selection.objects.Length} object(s)).");
            }

            if (options.RestoreActiveTool && snapshot.activeTool != null)
            {
                actions.Add($"Restore active tool '{snapshot.activeTool.tool}'.");
            }

            if (options.RestoreFocusedWindow && snapshot.focusedWindow != null)
            {
                actions.Add($"Focus window '{snapshot.focusedWindow.typeName}'.");
            }

            if (options.RestoreDockTabs && snapshot.dockTabs?.Length > 0)
            {
                actions.Add($"Restore active dock tabs ({snapshot.dockTabs.Length} tab(s)).");
            }

            return new RestorePlan
            {
                canRestore = blockers.Count == 0,
                dryRun = options.DryRun,
                actions = actions.ToArray(),
                warnings = warnings.ToArray(),
                blockers = blockers.ToArray(),
                current = BuildCurrentSummary(),
                target = BuildTargetSummary(snapshot)
            };
        }

        static void RestoreScenes(EditorSnapshotData snapshot, RestoreOptions options, List<string> applied, List<string> warnings)
        {
            var targetScenes = snapshot.scenes?.loadedScenes?
                .Where(scene => !string.IsNullOrWhiteSpace(scene.path))
                .ToArray();

            if (targetScenes == null || targetScenes.Length == 0)
            {
                warnings.Add("Snapshot has no saved scene paths to restore.");
                return;
            }

            var currentScenes = GetLoadedScenes().ToArray();
            var dirtyScenes = currentScenes.Where(scene => scene.isDirty).ToArray();
            if (dirtyScenes.Length > 0 && options.SaveDirtyScenes)
            {
                foreach (var scene in dirtyScenes)
                {
                    if (string.IsNullOrWhiteSpace(scene.path))
                    {
                        warnings.Add($"Dirty untitled scene '{scene.name}' could not be saved automatically.");
                        continue;
                    }

                    if (EditorSceneManager.SaveScene(scene))
                    {
                        applied.Add($"Saved dirty scene '{scene.path}'.");
                    }
                    else
                    {
                        warnings.Add($"Unity failed to save dirty scene '{scene.path}'.");
                    }
                }
            }

            if (options.CloseExtraScenes)
            {
                var firstPath = targetScenes[0].path;
                if (File.Exists(firstPath))
                {
                    EditorSceneManager.OpenScene(firstPath, OpenSceneMode.Single);
                    applied.Add($"Opened scene '{firstPath}' as Single.");
                }
                else
                {
                    warnings.Add($"Snapshot scene path not found: '{firstPath}'.");
                }

                for (var i = 1; i < targetScenes.Length; i++)
                {
                    OpenSceneIfExists(targetScenes[i].path, OpenSceneMode.Additive, applied, warnings);
                }
            }
            else if (options.OpenMissingScenes)
            {
                var currentPaths = new HashSet<string>(
                    GetLoadedScenes().Select(scene => NormalizePath(scene.path)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var scene in targetScenes)
                {
                    if (!currentPaths.Contains(NormalizePath(scene.path)))
                    {
                        OpenSceneIfExists(scene.path, OpenSceneMode.Additive, applied, warnings);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(snapshot.scenes.activeScenePath))
            {
                var active = GetLoadedScenes()
                    .FirstOrDefault(scene => string.Equals(NormalizePath(scene.path), NormalizePath(snapshot.scenes.activeScenePath), StringComparison.OrdinalIgnoreCase));
                if (active.IsValid() && active.isLoaded)
                {
                    SceneManager.SetActiveScene(active);
                    applied.Add($"Set active scene to '{snapshot.scenes.activeScenePath}'.");
                }
                else
                {
                    warnings.Add($"Could not restore active scene '{snapshot.scenes.activeScenePath}'.");
                }
            }
        }

        static void RestorePrefabStage(EditorSnapshotData snapshot, List<string> applied, List<string> warnings)
        {
            var currentStage = PrefabStageUtility.GetCurrentPrefabStage();
            var targetStage = snapshot.prefabStage;
            if (targetStage == null)
            {
                return;
            }

            if (!targetStage.isOpen)
            {
                if (currentStage != null)
                {
                    StageUtility.GoToMainStage();
                    applied.Add("Returned to Main Stage.");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(targetStage.assetPath))
            {
                warnings.Add("Snapshot Prefab Stage has no asset path.");
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(targetStage.assetPath);
            if (prefab == null)
            {
                warnings.Add($"Could not load prefab asset '{targetStage.assetPath}'.");
                return;
            }

            if (currentStage == null || !string.Equals(NormalizePath(currentStage.assetPath), NormalizePath(targetStage.assetPath), StringComparison.OrdinalIgnoreCase))
            {
                AssetDatabase.OpenAsset(prefab);
                applied.Add($"Opened Prefab Stage '{targetStage.assetPath}'.");
            }
        }

        static void RestorePrefabAutoSave(EditorSnapshotData snapshot, List<string> applied, List<string> warnings)
        {
            var data = snapshot.prefabAutoSave;
            if (data == null)
                return;

            try
            {
                EditorSettings.prefabModeAllowAutoSave = data.prefabModeAllowAutoSave;
                applied.Add($"Restored EditorSettings.prefabModeAllowAutoSave={data.prefabModeAllowAutoSave}.");
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to restore prefabModeAllowAutoSave: {ex.Message}");
            }

            if (data.stageNavigationAutoSave.HasValue)
            {
                if (TrySetStageNavigationAutoSave(data.stageNavigationAutoSave.Value))
                {
                    applied.Add($"Restored StageNavigationManager.autoSave={data.stageNavigationAutoSave.Value}.");
                }
                else
                {
                    warnings.Add("Could not restore StageNavigationManager.autoSave in this Unity version.");
                }
            }
        }

        static void RestoreSceneView(EditorSnapshotData snapshot, List<string> applied, List<string> warnings)
        {
            var data = snapshot.sceneView;
            if (data?.exists != true)
            {
                return;
            }

            var sceneView = SceneView.lastActiveSceneView ?? SceneView.sceneViews.OfType<SceneView>().FirstOrDefault();
            if (sceneView == null)
            {
                warnings.Add("No Scene View is available to restore.");
                return;
            }

            sceneView.pivot = FromVector3(data.pivot);
            sceneView.in2DMode = data.in2DMode;
            if (!data.in2DMode)
            {
                sceneView.rotation = FromQuaternion(data.rotation);
            }

            sceneView.size = Mathf.Max(0.0001f, data.size);
            sceneView.orthographic = data.orthographic;
            sceneView.Repaint();
            applied.Add("Restored Scene View camera.");
        }

        static void RestoreSelection(EditorSnapshotData snapshot, List<string> applied, List<string> warnings)
        {
            var refs = snapshot.selection?.objects ?? Array.Empty<ObjectRefData>();
            var objects = new List<Object>();

            foreach (var reference in refs)
            {
                var obj = ResolveObjectRef(reference);
                if (obj != null)
                {
                    objects.Add(obj);
                }
                else
                {
                    warnings.Add($"Could not resolve selected object '{reference.name}' ({reference.type}).");
                }
            }

            Selection.objects = objects.ToArray();
            if (!string.IsNullOrWhiteSpace(snapshot.selection?.activeGlobalId))
            {
                var active = ResolveGlobalId(snapshot.selection.activeGlobalId);
                if (active != null)
                {
                    Selection.activeObject = active;
                }
            }

            applied.Add($"Restored selection ({objects.Count} object(s)).");
        }

        static void RestoreActiveTool(EditorSnapshotData snapshot, List<string> applied, List<string> warnings)
        {
            var tool = snapshot.activeTool;
            if (tool == null)
            {
                return;
            }

            try
            {
                if (Enum.TryParse(tool.tool, out UnityEditor.Tool parsedTool))
                {
                    UnityEditor.Tools.current = parsedTool;
                }

                if (Enum.TryParse(tool.pivotMode, out UnityEditor.PivotMode pivotMode))
                {
                    UnityEditor.Tools.pivotMode = pivotMode;
                }

                if (Enum.TryParse(tool.pivotRotation, out UnityEditor.PivotRotation pivotRotation))
                {
                    UnityEditor.Tools.pivotRotation = pivotRotation;
                }

                applied.Add($"Restored active tool '{tool.tool}'.");
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to restore active tool: {ex.Message}");
            }
        }

        static void RestoreFocusedWindow(EditorSnapshotData snapshot, RestoreOptions options, List<string> applied, List<string> warnings)
        {
            var windowData = snapshot.focusedWindow;
            if (windowData == null)
            {
                return;
            }

            var type = ResolveType(windowData.assemblyQualifiedName, windowData.typeName);
            if (type == null || !typeof(EditorWindow).IsAssignableFrom(type))
            {
                warnings.Add($"Could not resolve EditorWindow type '{windowData.typeName}'.");
                return;
            }

            try
            {
                var window = EditorWindow.GetWindow(type);
                if (options.RestoreWindowMaximized)
                {
                    SetWindowMaximized(window, windowData.maximized);
                }

                window.Focus();
                window.Repaint();
                applied.Add($"Focused window '{windowData.typeName}'.");
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to focus window '{windowData.typeName}': {ex.Message}");
            }
        }

        static void RestoreDockTabs(EditorSnapshotData snapshot, List<string> applied, List<string> warnings)
        {
            var tabs = snapshot.dockTabs ?? Array.Empty<DockTabData>();
            var restored = 0;

            foreach (var tab in tabs)
            {
                var windowData = tab?.window;
                if (windowData == null)
                    continue;

                var type = ResolveType(windowData.assemblyQualifiedName, windowData.typeName);
                if (type == null || !typeof(EditorWindow).IsAssignableFrom(type))
                {
                    warnings.Add($"Could not resolve dock tab window type '{windowData.typeName}'.");
                    continue;
                }

                try
                {
                    var existing = Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().FirstOrDefault();
                    var window = existing ?? EditorWindow.GetWindow(type, false, windowData.title);
                    ShowWindowTab(window);
                    window.Repaint();
                    restored++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to restore dock tab '{windowData.typeName}': {ex.Message}");
                }
            }

            if (restored > 0)
                applied.Add($"Restored active dock tabs ({restored}/{tabs.Length}).");
        }

        static void OpenSceneIfExists(string path, OpenSceneMode mode, List<string> applied, List<string> warnings)
        {
            if (File.Exists(path))
            {
                EditorSceneManager.OpenScene(path, mode);
                applied.Add($"Opened scene '{path}' as {mode}.");
            }
            else
            {
                warnings.Add($"Snapshot scene path not found: '{path}'.");
            }
        }

        static EditorSnapshotData LoadSnapshot(EditorSnapshotParams parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameters.SnapshotJson))
            {
                return JsonConvert.DeserializeObject<EditorSnapshotData>(parameters.SnapshotJson, JsonSettings);
            }

            if (string.IsNullOrWhiteSpace(parameters.SnapshotId))
            {
                return null;
            }

            var path = GetSnapshotPath(parameters.SnapshotId);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<EditorSnapshotData>(File.ReadAllText(path), JsonSettings);
        }

        static string SaveSnapshot(EditorSnapshotData snapshot)
        {
            Directory.CreateDirectory(SnapshotDirectory);
            var path = GetSnapshotPath(snapshot.snapshotId);
            File.WriteAllText(path, JsonConvert.SerializeObject(snapshot, JsonSettings));
            return path;
        }

        static SnapshotSummary ReadSnapshotSummary(string file)
        {
            try
            {
                var json = JObject.Parse(File.ReadAllText(file));
                return new SnapshotSummary
                {
                    snapshotId = json.Value<string>("snapshotId"),
                    name = json.Value<string>("name"),
                    createdUtc = json.Value<string>("createdUtc"),
                    filePath = NormalizePath(file),
                    projectName = json["project"]?.Value<string>("name"),
                    activeScenePath = json["scenes"]?.Value<string>("activeScenePath"),
                    loadedSceneCount = json["scenes"]?["loadedScenes"]?.Count() ?? 0,
                    prefabStagePath = json["prefabStage"]?.Value<bool?>("isOpen") == true ? json["prefabStage"]?.Value<string>("assetPath") : null,
                    dockTabCount = json["dockTabs"]?.Count() ?? 0,
                    hasPrefabAutoSave = json["prefabAutoSave"] != null
                };
            }
            catch
            {
                return null;
            }
        }

        static Scene[] GetLoadedScenes()
        {
            var scenes = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded)
                {
                    scenes.Add(scene);
                }
            }

            return scenes.ToArray();
        }

        static object BuildCurrentSummary()
        {
            var activeScene = SceneManager.GetActiveScene();
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            return new
            {
                loadedScenePaths = GetLoadedScenes().Select(scene => NormalizePath(scene.path)).ToArray(),
                activeScenePath = NormalizePath(activeScene.path),
                selectionCount = Selection.objects?.Length ?? 0,
                prefabStagePath = prefabStage == null ? null : NormalizePath(prefabStage.assetPath),
                focusedWindow = EditorWindow.focusedWindow == null ? null : EditorWindow.focusedWindow.GetType().FullName,
                prefabAutoSave = CapturePrefabAutoSave(),
                dockTabCount = CaptureDockTabs().Length
            };
        }

        static object BuildTargetSummary(EditorSnapshotData snapshot)
        {
            return new
            {
                loadedScenePaths = snapshot.scenes?.loadedScenes?.Select(scene => scene.path).ToArray() ?? Array.Empty<string>(),
                activeScenePath = snapshot.scenes?.activeScenePath,
                selectionCount = snapshot.selection?.objects?.Length ?? 0,
                prefabStagePath = snapshot.prefabStage?.isOpen == true ? snapshot.prefabStage.assetPath : null,
                focusedWindow = snapshot.focusedWindow?.typeName,
                prefabAutoSave = snapshot.prefabAutoSave,
                dockTabCount = snapshot.dockTabs?.Length ?? 0
            };
        }

        static ObjectRefData ToObjectRef(Object obj)
        {
            if (obj == null)
            {
                return null;
            }

            var assetPath = AssetDatabase.GetAssetPath(obj);
            return new ObjectRefData
            {
                name = obj.name,
                type = obj.GetType().FullName,
                globalId = ToGlobalIdString(obj),
                assetPath = NormalizePath(assetPath),
                hierarchyPath = obj is GameObject go ? GetHierarchyPath(go) : obj is Component component ? GetHierarchyPath(component.gameObject) : null
            };
        }

        static Object ResolveObjectRef(ObjectRefData reference)
        {
            if (reference == null)
            {
                return null;
            }

            var obj = ResolveGlobalId(reference.globalId);
            if (obj != null)
            {
                return obj;
            }

            if (!string.IsNullOrWhiteSpace(reference.assetPath))
            {
                obj = AssetDatabase.LoadAssetAtPath<Object>(reference.assetPath);
                if (obj != null)
                {
                    return obj;
                }
            }

            if (!string.IsNullOrWhiteSpace(reference.hierarchyPath))
            {
                var go = ResolveGameObjectByPath(reference.hierarchyPath);
                if (go != null)
                {
                    return go;
                }
            }

            if (!string.IsNullOrWhiteSpace(reference.name))
            {
                return GameObject.Find(reference.name);
            }

            return null;
        }

        static Object ResolveGlobalId(string globalId)
        {
            if (string.IsNullOrWhiteSpace(globalId))
            {
                return null;
            }

            return GlobalObjectId.TryParse(globalId, out var parsed)
                ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed)
                : null;
        }

        static string ToGlobalIdString(Object obj)
        {
            if (obj == null)
            {
                return null;
            }

            try
            {
                return GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
            }
            catch
            {
                return null;
            }
        }

        static WindowData ToWindowData(EditorWindow window, bool isFocused)
        {
            if (window == null)
            {
                return null;
            }

            var type = window.GetType();
            return new WindowData
            {
                title = window.titleContent == null ? null : window.titleContent.text,
                typeName = type.FullName,
                assemblyQualifiedName = type.AssemblyQualifiedName,
                isFocused = isFocused,
                maximized = GetWindowMaximized(window),
                position = ToRect(window.position)
            };
        }

        static object GetMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName))
                return null;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();
            while (type != null)
            {
                var field = type.GetField(memberName, flags);
                if (field != null)
                    return field.GetValue(target);

                var property = type.GetProperty(memberName, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(target);

                type = type.BaseType;
            }

            return null;
        }

        static List<EditorWindow> ReadEditorWindowList(object value)
        {
            if (value is IEnumerable<EditorWindow> typed)
                return typed.Where(window => window != null).ToList();

            if (value is System.Collections.IEnumerable enumerable)
            {
                var result = new List<EditorWindow>();
                foreach (var item in enumerable)
                    if (item is EditorWindow window && window != null)
                        result.Add(window);
                return result;
            }

            return null;
        }

        static int ReadIntMember(object target, string memberName)
        {
            var value = GetMemberValue(target, memberName);
            if (value is int intValue)
                return intValue;
            if (value is short shortValue)
                return shortValue;
            if (value is long longValue)
                return longValue > int.MaxValue ? -1 : (int)longValue;
            return -1;
        }

        static bool TryGetStageNavigationAutoSave(out bool autoSave)
        {
            autoSave = false;
            var manager = GetStageNavigationManager();
            if (manager == null)
                return false;

            var value = GetMemberValue(manager, "autoSave");
            if (value is bool boolValue)
            {
                autoSave = boolValue;
                return true;
            }

            return false;
        }

        static bool TrySetStageNavigationAutoSave(bool autoSave)
        {
            var manager = GetStageNavigationManager();
            if (manager == null)
                return false;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = manager.GetType();
            while (type != null)
            {
                var field = type.GetField("autoSave", flags);
                if (field != null && field.FieldType == typeof(bool))
                {
                    field.SetValue(manager, autoSave);
                    return true;
                }

                var property = type.GetProperty("autoSave", flags);
                if (property != null && property.PropertyType == typeof(bool) && property.CanWrite)
                {
                    property.SetValue(manager, autoSave);
                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }

        static object GetStageNavigationManager()
        {
            var type = ResolveType(null, "UnityEditor.SceneManagement.StageNavigationManager") ??
                       ResolveType(null, "UnityEditor.StageNavigationManager");
            if (type == null)
                return null;

            const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            var instanceProperty = type.GetProperty("instance", staticFlags);
            if (instanceProperty != null)
            {
                try
                {
                    var instance = instanceProperty.GetValue(null);
                    if (instance != null)
                        return instance;
                }
                catch
                {
                    // Fallback below.
                }
            }

            return Resources.FindObjectsOfTypeAll(type).FirstOrDefault();
        }

        static void ShowWindowTab(EditorWindow window)
        {
            if (window == null)
                return;

            var showTab = typeof(EditorWindow).GetMethod("ShowTab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (showTab != null)
                showTab.Invoke(window, null);
            else
                window.Focus();
        }

        static bool GetWindowMaximized(EditorWindow window)
        {
            try
            {
                var property = typeof(EditorWindow).GetProperty("maximized");
                return property != null && (bool)property.GetValue(window);
            }
            catch
            {
                return false;
            }
        }

        static void SetWindowMaximized(EditorWindow window, bool value)
        {
            var property = typeof(EditorWindow).GetProperty("maximized");
            if (property != null && property.CanWrite)
            {
                property.SetValue(window, value);
            }
        }

        static Type ResolveType(string assemblyQualifiedName, string fullName)
        {
            if (!string.IsNullOrWhiteSpace(assemblyQualifiedName))
            {
                var type = Type.GetType(assemblyQualifiedName);
                if (type != null)
                {
                    return type;
                }
            }

            if (string.IsNullOrWhiteSpace(fullName))
            {
                return null;
            }

            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName))
                .FirstOrDefault(type => type != null);
        }

        static GameObject ResolveGameObjectByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Trim('/');
            foreach (var scene in GetLoadedScenes())
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (string.Equals(root.name, normalized, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(GetHierarchyPath(root).Trim('/'), normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return root;
                    }

                    var found = root.GetComponentsInChildren<Transform>(true)
                        .Select(transform => transform.gameObject)
                        .FirstOrDefault(go => string.Equals(GetHierarchyPath(go).Trim('/'), normalized, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        return found;
                    }
                }
            }

            return null;
        }

        static string GetHierarchyPath(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var stack = new Stack<string>();
            var current = go.transform;
            while (current != null)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            return "/" + string.Join("/", stack);
        }

        static string SnapshotDirectory
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(projectRoot, "Library", "UniBridge", "EditorSnapshots");
            }
        }

        static string GetSnapshotPath(string snapshotId)
        {
            return Path.Combine(SnapshotDirectory, SanitizeSnapshotId(snapshotId) + ".json");
        }

        static string SanitizeSnapshotId(string snapshotId)
        {
            var raw = string.IsNullOrWhiteSpace(snapshotId) ? "snapshot" : snapshotId.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                raw = raw.Replace(invalid, '_');
            }

            return raw;
        }

        static string CreateSnapshotId(DateTime utcNow)
        {
            return "editor_" + utcNow.ToString("yyyyMMdd_HHmmss_fff");
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/');
        }

        static Vector3Data ToVector3(Vector3 value) => new() { x = value.x, y = value.y, z = value.z };
        static Vector3 FromVector3(Vector3Data value) => value == null ? Vector3.zero : new Vector3(value.x, value.y, value.z);
        static QuaternionData ToQuaternion(Quaternion value) => new() { x = value.x, y = value.y, z = value.z, w = value.w };
        static Quaternion FromQuaternion(QuaternionData value) => value == null ? Quaternion.identity : new Quaternion(value.x, value.y, value.z, value.w);
        static RectData ToRect(Rect value) => new() { x = value.x, y = value.y, width = value.width, height = value.height };

        sealed class RestoreOptions
        {
            public bool DryRun;
            public bool RestoreScenes;
            public bool RestoreSceneView;
            public bool RestoreSelection;
            public bool RestorePrefabStage;
            public bool RestorePrefabAutoSave;
            public bool RestoreActiveTool;
            public bool RestoreFocusedWindow;
            public bool RestoreDockTabs;
            public bool RestoreWindowMaximized;
            public bool CloseExtraScenes;
            public bool OpenMissingScenes;
            public bool SaveDirtyScenes;
            public bool AllowDirtySceneReload;

            public static RestoreOptions From(EditorSnapshotParams parameters)
            {
                return new RestoreOptions
                {
                    DryRun = parameters.DryRun ?? false,
                    RestoreScenes = parameters.RestoreScenes ?? true,
                    RestoreSceneView = parameters.RestoreSceneView ?? true,
                    RestoreSelection = parameters.RestoreSelection ?? true,
                    RestorePrefabStage = parameters.RestorePrefabStage ?? true,
                    RestorePrefabAutoSave = parameters.RestorePrefabAutoSave ?? true,
                    RestoreActiveTool = parameters.RestoreActiveTool ?? true,
                    RestoreFocusedWindow = parameters.RestoreFocusedWindow ?? true,
                    RestoreDockTabs = parameters.RestoreDockTabs ?? true,
                    RestoreWindowMaximized = parameters.RestoreWindowMaximized ?? false,
                    CloseExtraScenes = parameters.CloseExtraScenes ?? true,
                    OpenMissingScenes = parameters.OpenMissingScenes ?? true,
                    SaveDirtyScenes = parameters.SaveDirtyScenes ?? false,
                    AllowDirtySceneReload = parameters.AllowDirtySceneReload ?? false
                };
            }
        }

        sealed class RestorePlan
        {
            public bool canRestore;
            public bool dryRun;
            public string[] actions;
            public string[] warnings;
            public string[] blockers;
            public object current;
            public object target;
        }

        sealed class SnapshotSummary
        {
            public string snapshotId;
            public string name;
            public string createdUtc;
            public string filePath;
            public string projectName;
            public string activeScenePath;
            public int loadedSceneCount;
            public string prefabStagePath;
            public int dockTabCount;
            public bool hasPrefabAutoSave;
        }

        sealed class EditorSnapshotData
        {
            public string snapshotId;
            public string name;
            public string createdUtc;
            public ProjectData project;
            public ScenesData scenes;
            public SceneViewData sceneView;
            public SelectionData selection;
            public ActiveToolData activeTool;
            public PrefabStageData prefabStage;
            public PrefabAutoSaveData prefabAutoSave;
            public WindowData focusedWindow;
            public WindowData[] windows;
            public DockTabData[] dockTabs;
        }

        sealed class ProjectData
        {
            public string id;
            public string name;
            public string root;
            public string unityVersion;
        }

        sealed class ScenesData
        {
            public string activeScenePath;
            public string activeSceneName;
            public SceneData[] loadedScenes;
        }

        sealed class SceneData
        {
            public string path;
            public string name;
            public bool isLoaded;
            public bool isDirty;
            public int buildIndex;
            public int rootCount;
        }

        sealed class SceneViewData
        {
            public bool exists;
            public Vector3Data pivot;
            public QuaternionData rotation;
            public float size;
            public bool orthographic;
            public bool in2DMode;
            public string cameraMode;
        }

        sealed class SelectionData
        {
            public string activeGlobalId;
            public string activeName;
            public ObjectRefData[] objects;
        }

        sealed class ObjectRefData
        {
            public string name;
            public string type;
            public string globalId;
            public string assetPath;
            public string hierarchyPath;
        }

        sealed class ActiveToolData
        {
            public string tool;
            public string pivotMode;
            public string pivotRotation;
        }

        sealed class PrefabStageData
        {
            public bool isOpen;
            public string assetPath;
            public string prefabRootName;
            public bool isDirty;
        }

        sealed class PrefabAutoSaveData
        {
            public bool prefabModeAllowAutoSave;
            public bool? stageNavigationAutoSave;
        }

        sealed class WindowData
        {
            public string title;
            public string typeName;
            public string assemblyQualifiedName;
            public bool isFocused;
            public bool maximized;
            public RectData position;
        }

        sealed class DockTabData
        {
            public long dockAreaInstanceId;
            public int selectedIndex;
            public WindowData window;
        }

        sealed class Vector3Data
        {
            public float x;
            public float y;
            public float z;
        }

        sealed class QuaternionData
        {
            public float x;
            public float y;
            public float z;
            public float w;
        }

        sealed class RectData
        {
            public float x;
            public float y;
            public float width;
            public float height;
        }
    }
}
