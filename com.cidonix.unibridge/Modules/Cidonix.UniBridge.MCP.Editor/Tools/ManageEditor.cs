using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement; // Required for PrefabStage
using UnityEditorInternal; // Required for tag management
using UnityEngine;
using UnityEngine.SceneManagement;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry; // For Response class
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Handles operations related to controlling and querying the Unity Editor state,
    /// including managing Tags and Layers.
    /// </summary>
    public static class ManageEditor
    {
        /// <summary>
        /// Tool description for MCP tool registration, explaining the UniBridge_ManageEditor tool's capabilities
        /// </summary>
        public const string Title = "Control the Unity Editor";

        public const string Description = @"Query or control Unity Editor state for the open project.

Use this for play mode, pause/stop, selection, open editor windows, active tools, prefab stage information, tags, layers, project root discovery, editor readiness, saving, asset refresh, structured reload/checkpoint refresh, and script/solution regeneration.

Args:
    Action: Play, RequestPlayModeNoWait, WaitForPlayMode, WaitForEditMode, Pause, Stop, ExitPlayMode, GetState, GetPlayModeState, GetCompilationDiagnostics, GetProjectRoot, GetWindows, GetActiveTool, GetSelection, GetPrefabStage, SetActiveTool, AddTag, RemoveTag, GetTags, AddLayer, RemoveLayer, GetLayers, SelectAsset, SelectGameObject, ClearSelection, PingSelection, FrameSelection, WaitForReady, WaitForReadyAfterReload, WaitIdle, RefreshAssets, RequestScriptCompilation, RequestScriptCompilationNoWait, SaveAll, SaveAssets, GenerateSolutionFiles, GenerateSolutionFile, or ReloadCheckpoint.
    WaitForCompletion: Optional wait flag. Play/Stop and script compilation use reload-safe deferred waiting because Unity domain reload may recreate the Unity-side bridge.
    AssetPath/GameObjectPath/Target/InstanceID: Selection targets.
    ToolName: Tool name for SetActiveTool.
    TagName: Tag name for AddTag or RemoveTag.
    LayerName: Layer name for AddLayer or RemoveLayer.
    TimeoutMs/PollIntervalMs/RequireNotPlaying: Readiness wait controls.
    ModifiedAssetPaths/RestoreScenes/RestorePrefabStage/AllowDirtySceneReload: ReloadCheckpoint controls.
    Force: Force refresh or clean script compilation when supported.

Returns:
    success, message, and action-specific editor state data.";
        // Constant for starting user layer index
        const int FirstUserLayerIndex = 8;

        // Constant for total layer count
        const int TotalLayerCount = 32;
        static bool? s_PendingPlayModeTarget;
        static string s_PendingPlayModeRequestId;
        static DateTime s_PendingPlayModeQueuedAtUtc;

        /// <summary>
        /// Returns the output schema for this tool.
        /// </summary>
        /// <returns>The JSON schema object describing the tool's output structure.</returns>
        [McpOutputSchema("UniBridge_ManageEditor")]
        public static object GetOutputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    success = new { type = "boolean", description = "Whether the operation succeeded" },
                    message = new { type = "string", description = "Human-readable message about the operation" },
                    data = new
                    {
                        type = "object",
                        description = "Editor-specific operation data",
                        properties = new
                        {
                            // Editor state properties
                            isPlaying = new { type = "boolean", description = "Whether the editor is in play mode" },
                            isPlayingOrWillChangePlaymode = new { type = "boolean", description = "Whether the editor is in play mode or about to change play mode" },
                            isPaused = new { type = "boolean", description = "Whether the game is paused" },
                            isCompiling = new { type = "boolean", description = "Whether the editor is compiling" },
                            isUpdating = new { type = "boolean", description = "Whether the editor is updating" },
                            isReady = new { type = "boolean", description = "Whether the editor is ready for the next agent action" },
                            applicationPath = new { type = "string", description = "Path to Unity application" },
                            applicationContentsPath = new { type = "string", description = "Path to Unity application contents" },
                            timeSinceStartup = new { type = "number", description = "Time since Unity startup" },

                            // Project root
                            projectRoot = new { type = "string", description = "Full path to the project root directory" },

                            // Windows array
                            windows = new
                            {
                                type = "array",
                                description = "List of open editor windows",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        title = new { type = "string", description = "Window title" },
                                        typeName = new { type = "string", description = "Full type name of the window" },
                                        isFocused = new { type = "boolean", description = "Whether the window is currently focused" },
                                        instanceID = new { type = "integer", description = "Unity instance ID of the window" },
                                        position = new
                                        {
                                            type = "object",
                                            properties = new
                                            {
                                                x = new { type = "number", description = "X coordinate" },
                                                y = new { type = "number", description = "Y coordinate" },
                                                width = new { type = "number", description = "Width" },
                                                height = new { type = "number", description = "Height" }
                                            }
                                        }
                                    }
                                }
                            },

                            // Active tool
                            activeTool = new { type = "string", description = "Name of the active tool" },
                            isCustom = new { type = "boolean", description = "Whether a custom tool is active" },
                            pivotMode = new { type = "string", description = "Pivot mode setting" },
                            pivotRotation = new { type = "string", description = "Pivot rotation setting" },
                            handleRotation = new { type = "array", items = new { type = "number" }, description = "Handle rotation as euler angles" },
                            handlePosition = new { type = "array", items = new { type = "number" }, description = "Handle position" },

                            // Selection
                            activeObject = new { type = "string", description = "Name of active selected object" },
                            activeGameObject = new { type = "string", description = "Name of active selected GameObject" },
                            activeTransform = new { type = "string", description = "Name of active selected Transform" },
                            activeInstanceID = new { type = "integer", description = "Instance ID of active selection" },
                            count = new { type = "integer", description = "Total count of selected objects" },
                            objects = new
                            {
                                type = "array",
                                description = "List of all selected objects",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        name = new { type = "string", description = "Object name" },
                                        type = new { type = "string", description = "Full type name" },
                                        instanceID = new { type = "integer", description = "Unity instance ID" },
                                        isPersistent = new { type = "boolean", description = "Whether this selection item is a project asset" },
                                        path = new { type = "string", description = "Project asset path or scene hierarchy path" },
                                        guid = new { type = "string", description = "Project asset GUID when available" },
                                        scenePath = new { type = "string", description = "Scene path when available" }
                                    }
                                }
                            },
                            gameObjects = new
                            {
                                type = "array",
                                description = "List of all selected GameObjects",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        name = new { type = "string", description = "GameObject name" },
                                        instanceID = new { type = "integer", description = "Unity instance ID" },
                                        path = new { type = "string", description = "GameObject hierarchy path" },
                                        scenePath = new { type = "string", description = "Scene path" },
                                        isActiveInHierarchy = new { type = "boolean", description = "Whether the GameObject is active in hierarchy" }
                                    }
                                }
                            },
                            assetGUIDs = new { type = "array", items = new { type = "string" }, description = "Asset GUIDs of selected assets in Project view" },
                            selected = new { type = "object", description = "Selected object summary for selection actions" },
                            readiness = new { type = "object", description = "Editor readiness state for wait/compile/refresh actions" },
                            savedScenes = new { type = "array", description = "Scenes saved by SaveAll" },
                            skippedScenes = new { type = "array", description = "Dirty scenes skipped by SaveAll because they have no path" },
                            savedPrefabStage = new { type = "object", description = "Prefab stage save result" },

                            // Tags and layers
                            tags = new { type = "array", items = new { type = "string" }, description = "List of tags" },
                            layers = new
                            {
                                type = "object",
                                description = "Dictionary of layer indices and names",
                                additionalProperties = new { type = "string" }
                            },

                            // Prefab stage info
                            isOpen = new { type = "boolean", description = "Whether prefab stage is currently open" },
                            assetPath = new { type = "string", description = "Asset path of the prefab being edited" },
                            prefabRootName = new { type = "string", description = "Name of the prefab root GameObject" },
                            mode = new { type = "string", description = "Prefab stage mode (InContext or InIsolation)" },
                            isDirty = new { type = "boolean", description = "Whether the prefab has unsaved changes" }
                        }
                    }
                },
                required = new[] { "success", "message" }
            };
        }




        /// <summary>
        /// Main handler for editor management actions.
        /// </summary>
        /// <param name="parameters">The parameters specifying the action and related settings.</param>
        /// <returns>A response object containing success status, message, and optional data.</returns>
        [McpTool("UniBridge_ManageEditor", Description, Title, Groups = new string[] { "core", "editor" }, EnabledByDefault = true)]
        public static async Task<object> HandleCommand(ManageEditorParams parameters)
        {
            var @params = parameters ?? new ManageEditorParams();

            // Parameters for specific actions
            string tagName = @params.TagName;
            string layerName = @params.LayerName;
            bool waitForCompletion = @params.WaitForCompletion ?? false;

            // Route action
            switch (@params.Action)
            {
                // Play Mode Control
                case EditorAction.Play:
                case EditorAction.RequestPlayModeNoWait:
                    try
                    {
                        if (EditorApplication.isPlaying)
                            return Response.Success("Already in play mode.", BuildPlayModeStateData());

                        var queued = QueuePlayModeChange(targetPlaying: true, @params, waitForCompletionRequested: waitForCompletion);
                        return Response.Success(
                            "Play mode entry queued. Inline waiting is deferred because entering Play Mode can recreate the MCP bridge.",
                            queued);
                    }
                    catch (Exception e)
                    {
                        return Response.Error($"Error entering play mode: {e.Message}");
                    }
                case EditorAction.WaitForPlayMode:
                    return await WaitForPlayModeState(true, @params);
                case EditorAction.WaitForEditMode:
                    return await WaitForPlayModeState(false, @params);
                case EditorAction.Pause:
                    try
                    {
                        if (EditorApplication.isPlaying)
                        {
                            EditorApplication.isPaused = !EditorApplication.isPaused;
                            return Response.Success(
                                EditorApplication.isPaused ? "Game paused." : "Game resumed."
                            );
                        }
                        return Response.Error("Cannot pause/resume: Not in play mode.");
                    }
                    catch (Exception e)
                    {
                        return Response.Error($"Error pausing/resuming game: {e.Message}");
                    }
                case EditorAction.Stop:
                case EditorAction.ExitPlayMode:
                    try
                    {
                        if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                            return Response.Success("Already stopped (not in play mode).", BuildPlayModeStateData());

                        var queued = QueuePlayModeChange(targetPlaying: false, @params, waitForCompletionRequested: waitForCompletion);
                        return Response.Success(
                            "Play mode exit queued. Inline waiting is deferred because leaving Play Mode can recreate the MCP bridge.",
                            queued);
                    }
                    catch (Exception e)
                    {
                        return Response.Error($"Error stopping play mode: {e.Message}");
                    }

                // Editor State/Info
                case EditorAction.GetState:
                    return GetEditorState();
                case EditorAction.GetPlayModeState:
                    return GetPlayModeState();
                case EditorAction.GetCompilationDiagnostics:
                    return GetCompilationDiagnostics();
                case EditorAction.GetProjectRoot:
                    return GetProjectRoot();
                case EditorAction.GetWindows:
                    return GetEditorWindows();
                case EditorAction.GetActiveTool:
                    return GetActiveTool();
                case EditorAction.GetSelection:
                    return GetSelection();
                case EditorAction.GetPrefabStage:
                    return GetPrefabStageInfo();
                case EditorAction.SelectAsset:
                    return SelectAsset(@params);
                case EditorAction.SelectGameObject:
                    return SelectGameObject(@params);
                case EditorAction.ClearSelection:
                    return ClearSelection();
                case EditorAction.PingSelection:
                    return PingSelection(@params);
                case EditorAction.FrameSelection:
                    return FrameSelection();
                case EditorAction.WaitForReady:
                case EditorAction.WaitIdle:
                    return await WaitForReady(@params);
                case EditorAction.WaitForReadyAfterReload:
                    return await WaitForReadyAfterReload(@params);
                case EditorAction.RefreshAssets:
                    return await RefreshAssets(@params);
                case EditorAction.RequestScriptCompilation:
                    return await RequestScriptCompilation(@params);
                case EditorAction.RequestScriptCompilationNoWait:
                    return RequestScriptCompilationNoWait(@params);
                case EditorAction.SaveAll:
                    return SaveAll(@params);
                case EditorAction.SaveAssets:
                    return SaveAssetsOnly(@params);
                case EditorAction.GenerateSolutionFiles:
                case EditorAction.GenerateSolutionFile:
                    return GenerateSolutionFiles(@params);
                case EditorAction.ReloadCheckpoint:
                    return await ReloadCheckpoint(@params);
                case EditorAction.SetActiveTool:
                    string toolName = @params.ToolName;
                    if (string.IsNullOrEmpty(toolName))
                        return Response.Error("'ToolName' parameter required for SetActiveTool.");
                    return SetActiveTool(toolName);

                // Tag Management
                case EditorAction.AddTag:
                    if (string.IsNullOrEmpty(tagName))
                        return Response.Error("'tagName' parameter required for add_tag.");
                    return AddTag(tagName);
                case EditorAction.RemoveTag:
                    if (string.IsNullOrEmpty(tagName))
                        return Response.Error("'tagName' parameter required for remove_tag.");
                    return RemoveTag(tagName);
                case EditorAction.GetTags:
                    return GetTags(); // Helper to list current tags

                // Layer Management
                case EditorAction.AddLayer:
                    if (string.IsNullOrEmpty(layerName))
                        return Response.Error("'layerName' parameter required for add_layer.");
                    return AddLayer(layerName);
                case EditorAction.RemoveLayer:
                    if (string.IsNullOrEmpty(layerName))
                        return Response.Error("'layerName' parameter required for remove_layer.");
                    return RemoveLayer(layerName);
                case EditorAction.GetLayers:
                    return GetLayers(); // Helper to list current layers

                // --- Settings (Example) ---
                // case "set_resolution":
                //     int? width = @params["width"]?.ToObject<int?>();
                //     int? height = @params["height"]?.ToObject<int?>();
                //     if (!width.HasValue || !height.HasValue) return Response.Error("'width' and 'height' parameters required.");
                //     return SetGameViewResolution(width.Value, height.Value);
                // case "set_quality":
                //     // Handle string name or int index
                //     return SetQualityLevel(@params["qualityLevel"]);

                default:
                    return Response.Error(
                        $"Unknown action: '{@params.Action}'. Supported actions include Play, RequestPlayModeNoWait, WaitForPlayMode, WaitForEditMode, Pause, Stop, ExitPlayMode, GetState, GetPlayModeState, GetCompilationDiagnostics, GetProjectRoot, GetWindows, GetActiveTool, GetSelection, GetPrefabStage, SetActiveTool, AddTag, RemoveTag, GetTags, AddLayer, RemoveLayer, GetLayers, SelectAsset, SelectGameObject, ClearSelection, PingSelection, FrameSelection, WaitForReady, WaitForReadyAfterReload, WaitIdle, RefreshAssets, RequestScriptCompilation, RequestScriptCompilationNoWait, SaveAll, SaveAssets, GenerateSolutionFiles, GenerateSolutionFile, ReloadCheckpoint."
                    );
            }
        }

        // --- Editor State/Info Methods ---
        static object GetEditorState()
        {
            try
            {
                var state = new EditorStateData
                {
                    IsPlaying = EditorApplication.isPlaying,
                    IsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                    IsPaused = EditorApplication.isPaused,
                    IsCompiling = EditorApplication.isCompiling,
                    IsUpdating = EditorApplication.isUpdating,
                    ApplicationPath = EditorApplication.applicationPath,
                    ApplicationContentsPath = EditorApplication.applicationContentsPath,
                    TimeSinceStartup = EditorApplication.timeSinceStartup,
                    IsReady = IsEditorReady(requireNotPlaying: false),
                };

                return Response.Success("Retrieved editor state.", state);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting editor state: {e.Message}");
            }
        }

        static object GetPlayModeState()
        {
            try
            {
                return Response.Success("Retrieved play mode state.", new
                {
                    state = BuildPlayModeStateData(),
                    isPlaying = EditorApplication.isPlaying,
                    isPaused = EditorApplication.isPaused,
                    isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                    canRequestScriptCompilation = !EditorApplication.isPlaying,
                    readiness = BuildReadinessData(requireNotPlaying: true, DateTime.UtcNow)
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting play mode state: {e.Message}");
            }
        }

        static object BuildPlayModeStateData() => new
        {
            isPlaying = EditorApplication.isPlaying,
            isPaused = EditorApplication.isPaused,
            isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
            canRequestScriptCompilation = !EditorApplication.isPlaying
        };

        static object GetCompilationDiagnostics()
        {
            try
            {
                var diagnostics = EditorEventHistory.Snapshot(0, 1, includeSelection: false, includeDiagnostics: true, includeAssetChanges: false);
                var data = JObject.FromObject(diagnostics);
                var buildSystemHealth = ReadConsole.BuildBuildSystemHealth(maxIssues: 5, includeStacktrace: true);
                var buildSystemHealthToken = JToken.FromObject(buildSystemHealth);
                var hasBuildSystemIssues = buildSystemHealthToken["hasCriticalIssues"]?.Value<bool>() == true;
                data["buildSystemHealth"] = buildSystemHealthToken;
                data["assemblyFreshness"] = JToken.FromObject(BuildScriptAssemblyFreshness());
                data["compileHealth"] = JToken.FromObject(new
                {
                    healthy = !hasBuildSystemIssues,
                    note = "CompilationPipeline diagnostics can be clean while Unity Bee/BuildProgram fails. buildSystemHealth inspects the Editor Console for those lower-level failures, and assemblyFreshness helps spot stale runtime assemblies."
                });

                return Response.Success(
                    "Retrieved retained compilation diagnostics and build-system health.",
                    data);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting compilation diagnostics: {e.Message}");
            }
        }

        static object GetProjectRoot()
        {
            try
            {
                // Application.dataPath points to <Project>/Assets
                string assetsPath = Application.dataPath.Replace('\\', '/');
                string projectRoot = Directory.GetParent(assetsPath)?.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(projectRoot))
                {
                    return Response.Error("Could not determine project root from Application.dataPath");
                }

                var data = new { projectRoot };

                return Response.Success("Project root resolved.", data);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting project root: {e.Message}");
            }
        }

        static object GetEditorWindows()
        {
            try
            {
                // Get all types deriving from EditorWindow
                var windowTypes = AppDomain
                    .CurrentDomain.GetAssemblies()
                    .SelectMany(assembly => assembly.GetTypes())
                    .Where(type => type.IsSubclassOf(typeof(EditorWindow)))
                    .ToList();

                var openWindows = new List<EditorWindowInfo>();

                // Find currently open instances
                // Resources.FindObjectsOfTypeAll seems more reliable than GetWindow for finding *all* open windows
                EditorWindow[] allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();

                foreach (EditorWindow window in allWindows)
                {
                    if (window == null)
                        continue; // Skip potentially destroyed windows

                    try
                    {
                        openWindows.Add(
                            new EditorWindowInfo
                            {
                                Title = window.titleContent.text,
                                TypeName = window.GetType().FullName,
                                IsFocused = EditorWindow.focusedWindow == window,
                                Position = new WindowPosition
                                {
                                    X = window.position.x,
                                    Y = window.position.y,
                                    Width = window.position.width,
                                    Height = window.position.height,
                                },
                                InstanceID = UnityApiAdapter.GetObjectId(window),
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"Could not get info for window {window.GetType().Name}: {ex.Message}"
                        );
                    }
                }

                return Response.Success("Retrieved list of open editor windows.", openWindows);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting editor windows: {e.Message}");
            }
        }

        static object GetActiveTool()
        {
            try
            {
                Tool currentTool = UnityEditor.Tools.current;
                string toolName = currentTool.ToString(); // Enum to string
                bool customToolActive = UnityEditor.Tools.current == Tool.Custom; // Check if a custom tool is active
                string activeToolName = customToolActive
                    ? EditorTools.GetActiveToolName()
                    : toolName; // Get custom name if needed

                var toolInfo = new ActiveToolData
                {
                    ActiveTool = activeToolName,
                    IsCustom = customToolActive,
                    PivotMode = UnityEditor.Tools.pivotMode.ToString(),
                    PivotRotation = UnityEditor.Tools.pivotRotation.ToString(),
                    HandleRotation = new float[] { UnityEditor.Tools.handleRotation.eulerAngles.x, UnityEditor.Tools.handleRotation.eulerAngles.y, UnityEditor.Tools.handleRotation.eulerAngles.z },
                    HandlePosition = new float[] { UnityEditor.Tools.handlePosition.x, UnityEditor.Tools.handlePosition.y, UnityEditor.Tools.handlePosition.z },
                };

                return Response.Success("Retrieved active tool information.", toolInfo);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting active tool: {e.Message}");
            }
        }

        static object SetActiveTool(string toolName)
        {
            try
            {
                Tool targetTool;
                if (Enum.TryParse<Tool>(toolName, true, out targetTool)) // Case-insensitive parse
                {
                    // Check if it's a valid built-in tool
                    if (targetTool != Tool.None && targetTool <= Tool.Custom) // Tool.Custom is the last standard tool
                    {
                        UnityEditor.Tools.current = targetTool;
                        return Response.Success($"Set active tool to '{targetTool}'.");
                    }
                    else
                    {
                        return Response.Error(
                            $"Cannot directly set tool to '{toolName}'. It might be None, Custom, or invalid."
                        );
                    }
                }
                else
                {
                    // Potentially try activating a custom tool by name here if needed
                    // This often requires specific editor scripting knowledge for that tool.
                    return Response.Error(
                        $"Could not parse '{toolName}' as a standard Unity Tool (View, Move, Rotate, Scale, Rect, Transform, Custom)."
                    );
                }
            }
            catch (Exception e)
            {
                return Response.Error($"Error setting active tool: {e.Message}");
            }
        }

        static object GetSelection()
        {
            try
            {
                var selectionInfo = new SelectionData
                {
                    ActiveObject = Selection.activeObject?.name,
                    ActiveGameObject = Selection.activeGameObject?.name,
                    ActiveTransform = Selection.activeTransform?.name,
                    ActiveInstanceID = UnityApiAdapter.GetActiveSelectionId(),
                    Count = Selection.count,
                    Objects = Selection
                        .objects.Select(ToSelectionObjectInfo)
                        .ToList(),
                    GameObjects = Selection
                        .gameObjects.Select(ToGameObjectSelectionInfo)
                        .ToList(),
                    AssetGUIDs = Selection.assetGUIDs, // GUIDs for selected assets in Project view
                };

                return Response.Success("Retrieved current selection details.", selectionInfo);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting selection: {e.Message}");
            }
        }

        static object GetPrefabStageInfo()
        {
            try
            {
                PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage == null)
                {
                    var closedStageInfo = new PrefabStageData
                    {
                        IsOpen = false,
                        AssetPath = null,
                        PrefabRootName = null,
                        Mode = null,
                        IsDirty = false
                    };
                    return Response.Success("No prefab stage is currently open.", closedStageInfo);
                }

                var stageInfo = new PrefabStageData
                {
                    IsOpen = true,
                    AssetPath = stage.assetPath,
                    PrefabRootName = stage.prefabContentsRoot?.name,
                    Mode = stage.mode.ToString(),
                    IsDirty = stage.scene.isDirty
                };

                return Response.Success("Prefab stage info retrieved.", stageInfo);
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting prefab stage info: {e.Message}");
            }
        }

        static object SelectAsset(ManageEditorParams parameters)
        {
            try
            {
                var assetPath = NormalizeAssetPath(parameters.AssetPath ?? parameters.Target);
                if (string.IsNullOrWhiteSpace(assetPath))
                    return Response.Error("'AssetPath' or 'Target' is required for SelectAsset.");

                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset == null)
                    return Response.Error($"Failed to load asset at path '{assetPath}'.");

                Selection.activeObject = asset;
                if (parameters.Focus != false)
                    EditorUtility.FocusProjectWindow();
                if (parameters.PingObject != false)
                    EditorGUIUtility.PingObject(asset);

                return Response.Success($"Selected asset '{assetPath}'.", new
                {
                    selected = ToSelectionObjectInfo(asset),
                    selection = CaptureSelectionData()
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error selecting asset: {e.Message}");
            }
        }

        static object SelectGameObject(ManageEditorParams parameters)
        {
            try
            {
                var target = ResolveGameObject(parameters);
                if (target == null)
                    return Response.Error("Failed to resolve GameObject. Provide GameObjectPath, Target, or InstanceID.");

                Selection.activeGameObject = target;
                if (parameters.Focus != false)
                    FocusSceneView();
                if (parameters.PingObject != false)
                    EditorGUIUtility.PingObject(target);
                if (parameters.FrameSceneView == true)
                    TryFrameSelection();

                return Response.Success($"Selected GameObject '{BuildGameObjectPath(target)}'.", new
                {
                    selected = ToGameObjectSelectionInfo(target),
                    selection = CaptureSelectionData()
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error selecting GameObject: {e.Message}");
            }
        }

        static object ClearSelection()
        {
            Selection.objects = Array.Empty<UnityEngine.Object>();
            return Response.Success("Cleared Unity editor selection.", new
            {
                selection = CaptureSelectionData()
            });
        }

        static object PingSelection(ManageEditorParams parameters)
        {
            try
            {
                UnityEngine.Object target = null;
                var assetPath = NormalizeAssetPath(parameters.AssetPath);
                if (!string.IsNullOrWhiteSpace(assetPath))
                    target = AssetDatabase.LoadMainAssetAtPath(assetPath);

                if (target == null)
                    target = ResolveGameObject(parameters);

                if (target == null && !string.IsNullOrWhiteSpace(parameters.Target))
                {
                    var targetPath = NormalizeAssetPath(parameters.Target);
                    target = AssetDatabase.LoadMainAssetAtPath(targetPath);
                }

                target ??= Selection.activeObject;
                if (target == null)
                    return Response.Error("No active selection or resolvable target to ping.");

                if (EditorUtility.IsPersistent(target))
                    EditorUtility.FocusProjectWindow();
                else if (parameters.Focus != false)
                    FocusSceneView();

                EditorGUIUtility.PingObject(target);
                return Response.Success($"Pinged '{target.name}'.", new
                {
                    selected = ToSelectionObjectInfo(target),
                    selection = CaptureSelectionData()
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error pinging selection: {e.Message}");
            }
        }

        static object FrameSelection()
        {
            if (Selection.gameObjects == null || Selection.gameObjects.Length == 0)
                return Response.Error("FrameSelection requires at least one selected scene GameObject.");

            var framed = TryFrameSelection();
            return framed
                ? Response.Success("Framed selected GameObject(s) in Scene View.", new { selection = CaptureSelectionData() })
                : Response.Error("No Scene View is available to frame the selection.");
        }

        static async Task<object> WaitForPlayModeState(bool targetPlaying, ManageEditorParams parameters)
        {
            var timeoutMs = Clamp(parameters.TimeoutMs ?? 30000, 100, 300000);
            var pollIntervalMs = Clamp(parameters.PollIntervalMs ?? 100, 25, 5000);
            var start = DateTime.UtcNow;

            while ((DateTime.UtcNow - start).TotalMilliseconds <= timeoutMs)
            {
                var reachedTarget = targetPlaying
                    ? EditorApplication.isPlaying
                    : !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode;
                if (reachedTarget)
                {
                    ClearPendingPlayModeState(targetPlaying);
                    return Response.Success(
                        targetPlaying ? "Entered play mode." : "Exited play mode.",
                        new
                        {
                            waitedMs = (int)(DateTime.UtcNow - start).TotalMilliseconds,
                            readiness = BuildReadinessData(requireNotPlaying: !targetPlaying, start)
                        });
                }

                try
                {
                    await WaitForEditorUpdateAsync(pollIntervalMs);
                }
                catch (OperationCanceledException ex)
                {
                    return Response.Success(
                        targetPlaying
                            ? "Play mode wait crossed a Unity reload boundary. Reconnect, then call WaitForPlayMode again."
                            : "Edit mode wait crossed a Unity reload boundary. Reconnect, then call WaitForEditMode again.",
                        BuildPlayModeBoundaryData(targetPlaying, start, ex.Message));
                }
            }

            return Response.Error(
                targetPlaying ? "Timed out entering play mode." : "Timed out exiting play mode.",
                new
                {
                    timeoutMs,
                    readiness = BuildReadinessData(requireNotPlaying: !targetPlaying, start)
                });
        }

        static object BuildPlayModeBoundaryData(bool targetPlaying, DateTime start, string boundaryReason = null)
        {
            return new
            {
                status = targetPlaying ? "play_mode_transition" : "edit_mode_transition",
                targetPlaying,
                reloadBoundary = true,
                reconnectRequired = true,
                reloadSafe = true,
                changedProject = false,
                requestId = s_PendingPlayModeRequestId,
                queuedAtUtc = s_PendingPlayModeQueuedAtUtc == default ? (DateTime?)null : s_PendingPlayModeQueuedAtUtc,
                boundaryReason,
                waitedMs = (int)(DateTime.UtcNow - start).TotalMilliseconds,
                state = BuildPlayModeStateData(),
                readiness = BuildReadinessData(requireNotPlaying: !targetPlaying, start),
                nextSuggestedCalls = targetPlaying
                    ? new[]
                    {
                        "Reconnect to UniBridge",
                        "UniBridge_ManageEditor Action=WaitForPlayMode",
                        "UniBridge_ManageEditor Action=WaitForReady RequireNotPlaying=false",
                        "UniBridge_ReadConsole Action=DiagnosticSummary"
                    }
                    : new[]
                    {
                        "Reconnect to UniBridge",
                        "UniBridge_ManageEditor Action=WaitForEditMode",
                        "UniBridge_ManageEditor Action=WaitForReady RequireNotPlaying=true",
                        "UniBridge_ReadConsole Action=DiagnosticSummary"
                    }
            };
        }

        static void ClearPendingPlayModeState(bool reachedTarget)
        {
            if (s_PendingPlayModeTarget == reachedTarget)
            {
                s_PendingPlayModeTarget = null;
                s_PendingPlayModeRequestId = null;
                s_PendingPlayModeQueuedAtUtc = default;
            }
        }

        static object QueuePlayModeChange(bool targetPlaying, ManageEditorParams parameters, bool waitForCompletionRequested)
        {
            var requestId = $"{(targetPlaying ? "play" : "edit")}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var requireNotPlaying = parameters.RequireNotPlaying == true;
            var readinessBefore = BuildReadinessData(requireNotPlaying, DateTime.UtcNow);
            var stateBefore = BuildPlayModeStateData();
            s_PendingPlayModeTarget = targetPlaying;
            s_PendingPlayModeRequestId = requestId;
            s_PendingPlayModeQueuedAtUtc = DateTime.UtcNow;

            var applied = false;
            void Apply()
            {
                if (applied)
                    return;

                applied = true;
                try
                {
                    if (targetPlaying)
                    {
                        if (!EditorApplication.isPlaying)
                            EditorApplication.isPlaying = true;
                    }
                    else
                    {
                        if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                            EditorApplication.isPlaying = false;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[UniBridge] Deferred play mode {(targetPlaying ? "entry" : "exit")} request failed: {e.Message}");
                }
            }

            void ApplyOnUpdate()
            {
                EditorApplication.update -= ApplyOnUpdate;
                Apply();
            }

            EditorApplication.update += ApplyOnUpdate;
            EditorApplication.delayCall += Apply;
            EditorApplication.QueuePlayerLoopUpdate();

            return new
            {
                requestId,
                status = "queued",
                targetPlaying,
                waitForCompletionRequested,
                inlineWaitSkipped = waitForCompletionRequested,
                reconnectRequired = true,
                reloadSafe = true,
                batchBoundary = true,
                changedProject = false,
                reason = targetPlaying
                    ? "Entering Play Mode can reload Unity domain and close the current bridge connection; wait with WaitForPlayMode and WaitForReady after reconnect."
                    : "Leaving Play Mode can reload Unity domain and close the current bridge connection; wait with WaitForEditMode and WaitForReady after reconnect.",
                stateBefore,
                readinessBefore,
                nextSuggestedCalls = targetPlaying
                    ? new[]
                    {
                        "UniBridge_ManageEditor Action=WaitForPlayMode",
                        "UniBridge_ManageEditor Action=WaitForReady RequireNotPlaying=false",
                        "UniBridge_ReadConsole Action=DiagnosticSummary"
                    }
                    : new[]
                    {
                        "UniBridge_ManageEditor Action=WaitForEditMode",
                        "UniBridge_ManageEditor Action=WaitForReady RequireNotPlaying=true",
                        "UniBridge_ReadConsole Action=DiagnosticSummary"
                    }
            };
        }

        static async Task<object> WaitForReady(ManageEditorParams parameters)
        {
            var timeoutMs = Clamp(parameters.TimeoutMs ?? 30000, 100, 300000);
            var pollIntervalMs = Clamp(parameters.PollIntervalMs ?? 100, 25, 5000);
            var requireNotPlaying = parameters.RequireNotPlaying == true;
            var start = DateTime.UtcNow;
            object lastReadiness = null;

            while ((DateTime.UtcNow - start).TotalMilliseconds <= timeoutMs)
            {
                var readiness = BuildReadinessData(requireNotPlaying, start);
                lastReadiness = readiness;
                if (IsEditorReady(requireNotPlaying))
                {
                    return Response.Success("Unity editor is ready.", new
                    {
                        readiness,
                        waitedMs = (int)(DateTime.UtcNow - start).TotalMilliseconds
                    });
                }

                await WaitForEditorUpdateAsync(pollIntervalMs);
            }

            return Response.Error("Timed out waiting for Unity editor readiness.", new
            {
                readiness = lastReadiness,
                timeoutMs,
                requireNotPlaying
            });
        }

        static async Task<object> WaitForReadyAfterReload(ManageEditorParams parameters)
        {
            var waitResult = await WaitForReady(parameters);
            var diagnostics = GetCompilationDiagnostics();
            var buildSystemHealth = ReadConsole.BuildBuildSystemHealth(maxIssues: 5, includeStacktrace: true);
            var assemblyFreshness = BuildScriptAssemblyFreshness();
            return Response.Success("Unity editor is ready after reload/compilation checkpoint.", new
            {
                waitResult,
                compilationDiagnostics = diagnostics,
                buildSystemHealth,
                assemblyFreshness,
                readiness = BuildReadinessData(parameters.RequireNotPlaying == true, DateTime.UtcNow)
            });
        }

        static object BuildScriptAssemblyFreshness()
        {
            try
            {
                var assetsPath = Application.dataPath.Replace('\\', '/');
                var projectRoot = Directory.GetParent(assetsPath)?.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(projectRoot))
                {
                    return new
                    {
                        available = false,
                        reason = "Could not determine project root from Application.dataPath."
                    };
                }

                var assemblyPath = Path.Combine(projectRoot, "Library", "ScriptAssemblies", "Assembly-CSharp.dll").Replace('\\', '/');
                var assemblyInfo = new FileInfo(assemblyPath);
                var latestScriptInfo = Directory
                    .EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories)
                    .Select(path => SafeFileInfo(path))
                    .Where(file => file != null && file.Exists)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();

                var assemblyExists = assemblyInfo.Exists;
                var staleLikely = assemblyExists
                    && latestScriptInfo != null
                    && latestScriptInfo.LastWriteTimeUtc > assemblyInfo.LastWriteTimeUtc.AddSeconds(1);

                return new
                {
                    available = true,
                    assemblyPath,
                    assemblyExists,
                    assemblyLastWriteTimeUtc = assemblyExists ? assemblyInfo.LastWriteTimeUtc.ToString("o") : null,
                    latestAssetScriptPath = latestScriptInfo != null ? ToProjectRelativePath(latestScriptInfo.FullName, projectRoot) : null,
                    latestAssetScriptLastWriteTimeUtc = latestScriptInfo != null ? latestScriptInfo.LastWriteTimeUtc.ToString("o") : null,
                    staleLikely,
                    note = "staleLikely means an Assets/*.cs file is newer than Library/ScriptAssemblies/Assembly-CSharp.dll, which can happen when Bee/BuildProgram failed before producing a new runtime assembly."
                };
            }
            catch (Exception e)
            {
                return new
                {
                    available = false,
                    reason = $"Could not inspect script assembly freshness: {e.Message}"
                };
            }
        }

        static FileInfo SafeFileInfo(string path)
        {
            try
            {
                return new FileInfo(path);
            }
            catch
            {
                return null;
            }
        }

        static string ToProjectRelativePath(string path, string projectRoot)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(projectRoot))
                return path;

            var normalizedPath = path.Replace('\\', '/');
            var normalizedRoot = projectRoot.Replace('\\', '/').TrimEnd('/');
            if (!normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
                return normalizedPath;

            return normalizedPath.Substring(normalizedRoot.Length + 1);
        }

        static async Task<object> RefreshAssets(ManageEditorParams parameters)
        {
            try
            {
                var options = (ImportAssetOptions)(parameters.Force == true ? (int)ImportAssetOptions.ForceUpdate : 0);
                AssetDatabase.Refresh(options);

                if (parameters.WaitForCompletion == true)
                    return await WaitForReady(parameters);

                return Response.Success("AssetDatabase refresh requested.", new
                {
                    forced = parameters.Force == true,
                    readiness = BuildReadinessData(parameters.RequireNotPlaying == true, DateTime.UtcNow)
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error refreshing assets: {e.Message}");
            }
        }

        static async Task<object> RequestScriptCompilation(ManageEditorParams parameters)
        {
            try
            {
                if (EditorApplication.isPlaying)
                    return Response.Error("Editor is in Play mode and cannot recompile now. Exit Play mode first.");

                if (parameters.WaitForCompletion == true)
                {
                    var queued = QueueScriptCompilation(parameters, waitForCompletionRequested: true);
                    return Response.Success("Script compilation queued. Inline waiting is deferred because Unity assembly reload can recreate the MCP bridge.", queued);
                }

                AssetDatabase.Refresh();
                var options = ReadRequestScriptCompilationOptions(parameters);
                CompilationPipeline.RequestScriptCompilation(options);
                await WaitForEditorUpdateAsync(parameters.PollIntervalMs ?? 100);

                return Response.Success("Script compilation requested.", new
                {
                    forced = parameters.Force == true,
                    status = "requested",
                    waitForCompletionRequested = false,
                    reconnectRequired = true,
                    readiness = BuildReadinessData(parameters.RequireNotPlaying == true, DateTime.UtcNow)
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error requesting script compilation: {e.Message}");
            }
        }

        static object RequestScriptCompilationNoWait(ManageEditorParams parameters)
        {
            try
            {
                if (EditorApplication.isPlaying)
                    return Response.Error("Editor is in Play mode and cannot recompile now. Exit Play mode first.");

                var queued = QueueScriptCompilation(parameters, waitForCompletionRequested: false);
                return Response.Success("Script compilation queued without waiting through assembly reload.", queued);
            }
            catch (Exception e)
            {
                return Response.Error($"Error queueing script compilation: {e.Message}");
            }
        }

        static object QueueScriptCompilation(ManageEditorParams parameters, bool waitForCompletionRequested)
        {
            var requestId = $"compile-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var options = ReadRequestScriptCompilationOptions(parameters);
            var forced = parameters.Force == true;
            var requireNotPlaying = parameters.RequireNotPlaying == true;
            var readinessBefore = BuildReadinessData(requireNotPlaying, DateTime.UtcNow);

            EditorApplication.delayCall += () =>
            {
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        AssetDatabase.Refresh();
                        CompilationPipeline.RequestScriptCompilation(options);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[UniBridge] Delayed script compilation request failed: {e.Message}");
                    }
                };
            };

            return new
            {
                requestId,
                status = "queued",
                forced,
                waitForCompletionRequested,
                inlineWaitSkipped = waitForCompletionRequested,
                reconnectRequired = true,
                reloadSafe = true,
                reason = "Unity assembly reload can close the current bridge connection; wait with WaitForReadyAfterReload after reconnect.",
                readinessBefore,
                nextSuggestedCalls = new[]
                {
                    "UniBridge_ManageEditor Action=WaitForReadyAfterReload",
                    "UniBridge_ManageEditor Action=GetCompilationDiagnostics",
                    "UniBridge_ReadConsole Action=DiagnosticSummary"
                }
            };
        }

        static RequestScriptCompilationOptions ReadRequestScriptCompilationOptions(ManageEditorParams parameters)
        {
            return (RequestScriptCompilationOptions)(parameters.Force == true ? 1 : 0);
        }

        static object SaveAll(ManageEditorParams parameters)
        {
            try
            {
                var savedScenes = new List<object>();
                var skippedScenes = new List<object>();
                var warnings = new List<string>();

                if (parameters.SaveScenes != false)
                {
                    for (var i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (!scene.IsValid() || !scene.isLoaded || !scene.isDirty)
                            continue;

                        if (string.IsNullOrWhiteSpace(scene.path))
                        {
                            skippedScenes.Add(new { scene.name, reason = "Scene has no asset path." });
                            continue;
                        }

                        var saved = EditorSceneManager.SaveScene(scene);
                        savedScenes.Add(new
                        {
                            scene.name,
                            path = NormalizePath(scene.path),
                            saved
                        });
                    }
                }

                object savedPrefabStage = null;
                var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                if (prefabStage != null && prefabStage.scene.IsValid() && prefabStage.scene.isDirty)
                {
                    if (prefabStage.prefabContentsRoot != null && !string.IsNullOrWhiteSpace(prefabStage.assetPath))
                    {
                        var savedAsset = PrefabUtility.SaveAsPrefabAsset(prefabStage.prefabContentsRoot, prefabStage.assetPath);
                        savedPrefabStage = new
                        {
                            path = NormalizePath(prefabStage.assetPath),
                            saved = savedAsset != null
                        };
                    }
                    else
                    {
                        warnings.Add("Active Prefab Stage is dirty but has no prefab root/path to save.");
                    }
                }

                if (parameters.SaveAssets != false)
                    AssetDatabase.SaveAssets();

                return Response.Success("Saved editor scenes, prefab stage, and assets.", new
                {
                    savedScenes = savedScenes.ToArray(),
                    skippedScenes = skippedScenes.ToArray(),
                    savedPrefabStage,
                    savedAssets = parameters.SaveAssets != false,
                    warnings = warnings.ToArray(),
                    readiness = BuildReadinessData(parameters.RequireNotPlaying == true, DateTime.UtcNow)
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error saving editor state: {e.Message}");
            }
        }

        static object SaveAssetsOnly(ManageEditorParams parameters)
        {
            var saveParams = parameters with
            {
                SaveScenes = false,
                SaveAssets = true
            };
            return SaveAll(saveParams);
        }

        static object GenerateSolutionFiles(ManageEditorParams parameters)
        {
            try
            {
                AssetDatabase.Refresh();
                var editorAssembly = typeof(EditorApplication).Assembly;
                var syncVsType = editorAssembly.GetType("UnityEditor.SyncVS");
                var method = syncVsType?
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "SyncSolution" && m.GetParameters().Length == 0);

                if (method == null)
                    return Response.Error("UnityEditor.SyncVS.SyncSolution() is not available in this Unity Editor version.");

                method.Invoke(null, null);
                return Response.Success("Unity solution/project file generation requested.", new
                {
                    method = $"{syncVsType.FullName}.{method.Name}",
                    readiness = BuildReadinessData(parameters.RequireNotPlaying == true, DateTime.UtcNow)
                });
            }
            catch (TargetInvocationException e)
            {
                return Response.Error($"Error generating solution files: {e.InnerException?.Message ?? e.Message}");
            }
            catch (Exception e)
            {
                return Response.Error($"Error generating solution files: {e.Message}");
            }
        }

        static async Task<object> ReloadCheckpoint(ManageEditorParams parameters)
        {
            try
            {
                var modified = new HashSet<string>(
                    (parameters.ModifiedAssetPaths ?? Array.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(NormalizeReloadAssetPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
                    StringComparer.OrdinalIgnoreCase);

                var loadedScenes = GetLoadedSceneInfos();
                var activeScenePath = NormalizePath(SceneManager.GetActiveScene().path);
                var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                var prefabStagePath = NormalizePath(prefabStage?.assetPath);
                var restoreScenes = parameters.RestoreScenes != false;
                var restorePrefabStage = parameters.RestorePrefabStage != false;
                var force = parameters.Force == true;
                var changedLoadedScene = loadedScenes.Any(scene => modified.Contains(scene.path));
                var changedPrefabStage = !string.IsNullOrWhiteSpace(prefabStagePath) && modified.Contains(prefabStagePath);
                var shouldReopenScenes = restoreScenes && (force || changedLoadedScene);
                var shouldReopenPrefabStage = restorePrefabStage && !string.IsNullOrWhiteSpace(prefabStagePath) && (force || changedPrefabStage);
                var savedScenes = new List<object>();
                var warnings = new List<string>();

                if (shouldReopenScenes)
                {
                    var blockedDirtyScenes = loadedScenes
                        .Where(scene => scene.isDirty && modified.Contains(scene.path) && parameters.AllowDirtySceneReload != true)
                        .Select(scene => scene.path)
                        .ToArray();

                    if (blockedDirtyScenes.Length > 0)
                    {
                        return Response.Error("ReloadCheckpoint refused to reopen dirty modified scene(s). Save them first or set AllowDirtySceneReload=true.", new
                        {
                            modifiedAssetPaths = modified.ToArray(),
                            blockedDirtyScenes
                        });
                    }

                    if (parameters.SaveUnmodifiedScenes != false)
                    {
                        foreach (var sceneInfo in loadedScenes)
                        {
                            if (!sceneInfo.isDirty || modified.Contains(sceneInfo.path))
                                continue;

                            var scene = SceneManager.GetSceneByPath(sceneInfo.path);
                            if (scene.IsValid() && scene.isLoaded)
                            {
                                var saved = EditorSceneManager.SaveScene(scene);
                                savedScenes.Add(new { sceneInfo.name, sceneInfo.path, saved });
                            }
                        }
                    }
                }

                if (shouldReopenPrefabStage)
                    StageUtility.GoToMainStage();

                var refreshOptions = (ImportAssetOptions)(parameters.Force == true ? (int)ImportAssetOptions.ForceUpdate : 0);
                AssetDatabase.Refresh(refreshOptions);

                if (shouldReopenScenes && loadedScenes.Count > 0)
                    ReopenLoadedScenes(loadedScenes.Select(scene => scene.path).ToList(), activeScenePath);

                if (shouldReopenPrefabStage)
                    PrefabStageUtility.OpenPrefab(prefabStagePath);

                if (parameters.RepaintEditor != false)
                    RepaintEditorViews();

                if (parameters.WaitForCompletion == true)
                    await WaitForReady(parameters);

                return Response.Success("ReloadCheckpoint completed.", new
                {
                    modifiedAssetPaths = modified.ToArray(),
                    refreshed = true,
                    forced = force,
                    reopenedScenes = shouldReopenScenes,
                    reopenedPrefabStage = shouldReopenPrefabStage,
                    activeScenePath,
                    prefabStagePath,
                    savedScenes = savedScenes.ToArray(),
                    warnings = warnings.ToArray(),
                    readiness = BuildReadinessData(parameters.RequireNotPlaying == true, DateTime.UtcNow)
                });
            }
            catch (Exception e)
            {
                return Response.Error($"ReloadCheckpoint failed: {e.Message}");
            }
        }

        static List<LoadedSceneInfo> GetLoadedSceneInfos()
        {
            var scenes = new List<LoadedSceneInfo>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrWhiteSpace(scene.path))
                    continue;

                scenes.Add(new LoadedSceneInfo
                {
                    name = scene.name,
                    path = NormalizePath(scene.path),
                    isDirty = scene.isDirty
                });
            }

            return scenes;
        }

        static void ReopenLoadedScenes(List<string> loadedScenePaths, string activeScenePath)
        {
            if (loadedScenePaths == null || loadedScenePaths.Count == 0)
                return;

            EditorSceneManager.OpenScene(loadedScenePaths[0], OpenSceneMode.Single);
            for (var i = 1; i < loadedScenePaths.Count; i++)
                EditorSceneManager.OpenScene(loadedScenePaths[i], OpenSceneMode.Additive);

            if (!string.IsNullOrWhiteSpace(activeScenePath))
            {
                var activeScene = SceneManager.GetSceneByPath(activeScenePath);
                if (activeScene.IsValid() && activeScene.isLoaded)
                    SceneManager.SetActiveScene(activeScene);
            }
        }

        static void RepaintEditorViews()
        {
            SceneView.RepaintAll();
            InternalEditorUtility.RepaintAllViews();
            EditorApplication.QueuePlayerLoopUpdate();
        }

        static string NormalizeReloadAssetPath(string path)
        {
            var normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
                return normalized;

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(projectRoot) &&
                normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(projectRoot.Length).TrimStart('/');
            }

            if (normalized.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("unity://path/".Length);

            return normalized;
        }

        sealed class LoadedSceneInfo
        {
            public string name;
            public string path;
            public bool isDirty;
        }

        // --- Tag Management Methods ---

        static object AddTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return Response.Error("Tag name cannot be empty or whitespace.");

            // Check if tag already exists
            if (InternalEditorUtility.tags.Contains(tagName))
            {
                return Response.Error($"Tag '{tagName}' already exists.");
            }

            try
            {
                EnsureTagManagerEditable();
                // Add the tag using the internal utility
                InternalEditorUtility.AddTag(tagName);
                // Force save assets to ensure the change persists in the TagManager asset
                AssetDatabase.SaveAssets();
                return Response.Success($"Tag '{tagName}' added successfully.");
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to add tag '{tagName}': {e.Message}");
            }
        }

        static object RemoveTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return Response.Error("Tag name cannot be empty or whitespace.");
            if (tagName.Equals("Untagged", StringComparison.OrdinalIgnoreCase))
                return Response.Error("Cannot remove the built-in 'Untagged' tag.");

            // Check if tag exists before attempting removal
            if (!InternalEditorUtility.tags.Contains(tagName))
            {
                return Response.Error($"Tag '{tagName}' does not exist.");
            }

            try
            {
                EnsureTagManagerEditable();
                // Remove the tag using the internal utility
                InternalEditorUtility.RemoveTag(tagName);
                // Force save assets
                AssetDatabase.SaveAssets();
                return Response.Success($"Tag '{tagName}' removed successfully.");
            }
            catch (Exception e)
            {
                // Catch potential issues if the tag is somehow in use or removal fails
                return Response.Error($"Failed to remove tag '{tagName}': {e.Message}");
            }
        }

        static object GetTags()
        {
            try
            {
                string[] tags = InternalEditorUtility.tags;
                return Response.Success("Retrieved current tags.", tags);
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to retrieve tags: {e.Message}");
            }
        }

        // --- Layer Management Methods ---

        static object AddLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return Response.Error("Layer name cannot be empty or whitespace.");

            try
            {
                EnsureTagManagerEditable();
            }
            catch (Exception e)
            {
                return Response.Error($"TagManager is not editable: {e.Message}");
            }

            // Access the TagManager asset
            SerializedObject tagManager = GetTagManager();
            if (tagManager == null)
                return Response.Error("Could not access TagManager asset.");

            SerializedProperty layersProp = tagManager.FindProperty("layers");
            if (layersProp == null || !layersProp.isArray)
                return Response.Error("Could not find 'layers' property in TagManager.");

            // Check if layer name already exists (case-insensitive check recommended)
            for (int i = 0; i < TotalLayerCount; i++)
            {
                SerializedProperty layerSP = layersProp.GetArrayElementAtIndex(i);
                if (
                    layerSP != null
                    && layerName.Equals(layerSP.stringValue, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return Response.Error($"Layer '{layerName}' already exists at index {i}.");
                }
            }

            // Find the first empty user layer slot (indices 8 to 31)
            int firstEmptyUserLayer = -1;
            for (int i = FirstUserLayerIndex; i < TotalLayerCount; i++)
            {
                SerializedProperty layerSP = layersProp.GetArrayElementAtIndex(i);
                if (layerSP != null && string.IsNullOrEmpty(layerSP.stringValue))
                {
                    firstEmptyUserLayer = i;
                    break;
                }
            }

            if (firstEmptyUserLayer == -1)
            {
                return Response.Error("No empty User Layer slots available (8-31 are full).");
            }

            // Assign the name to the found slot
            try
            {
                SerializedProperty targetLayerSP = layersProp.GetArrayElementAtIndex(
                    firstEmptyUserLayer
                );
                targetLayerSP.stringValue = layerName;
                // Apply the changes to the TagManager asset
                tagManager.ApplyModifiedProperties();
                // Save assets to make sure it's written to disk
                AssetDatabase.SaveAssets();
                return Response.Success(
                    $"Layer '{layerName}' added successfully to slot {firstEmptyUserLayer}."
                );
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to add layer '{layerName}': {e.Message}");
            }
        }

        static object RemoveLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return Response.Error("Layer name cannot be empty or whitespace.");

            try
            {
                EnsureTagManagerEditable();
            }
            catch (Exception e)
            {
                return Response.Error($"TagManager is not editable: {e.Message}");
            }

            // Access the TagManager asset
            SerializedObject tagManager = GetTagManager();
            if (tagManager == null)
                return Response.Error("Could not access TagManager asset.");

            SerializedProperty layersProp = tagManager.FindProperty("layers");
            if (layersProp == null || !layersProp.isArray)
                return Response.Error("Could not find 'layers' property in TagManager.");

            // Find the layer by name (must be user layer)
            int layerIndexToRemove = -1;
            for (int i = FirstUserLayerIndex; i < TotalLayerCount; i++) // Start from user layers
            {
                SerializedProperty layerSP = layersProp.GetArrayElementAtIndex(i);
                // Case-insensitive comparison is safer
                if (
                    layerSP != null
                    && layerName.Equals(layerSP.stringValue, StringComparison.OrdinalIgnoreCase)
                )
                {
                    layerIndexToRemove = i;
                    break;
                }
            }

            if (layerIndexToRemove == -1)
            {
                return Response.Error($"User layer '{layerName}' not found.");
            }

            // Clear the name for that index
            try
            {
                SerializedProperty targetLayerSP = layersProp.GetArrayElementAtIndex(
                    layerIndexToRemove
                );
                targetLayerSP.stringValue = string.Empty; // Set to empty string to remove
                // Apply the changes
                tagManager.ApplyModifiedProperties();
                // Save assets
                AssetDatabase.SaveAssets();
                return Response.Success(
                    $"Layer '{layerName}' (slot {layerIndexToRemove}) removed successfully."
                );
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to remove layer '{layerName}': {e.Message}");
            }
        }

        static object GetLayers()
        {
            try
            {
                var layers = new Dictionary<int, string>();
                for (int i = 0; i < TotalLayerCount; i++)
                {
                    string layerName = LayerMask.LayerToName(i);
                    if (!string.IsNullOrEmpty(layerName)) // Only include layers that have names
                    {
                        layers.Add(i, layerName);
                    }
                }

                return Response.Success("Retrieved current named layers.", layers);
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to retrieve layers: {e.Message}");
            }
        }

        static SelectionData CaptureSelectionData()
        {
            return new SelectionData
            {
                ActiveObject = Selection.activeObject?.name,
                ActiveGameObject = Selection.activeGameObject?.name,
                ActiveTransform = Selection.activeTransform?.name,
                ActiveInstanceID = UnityApiAdapter.GetActiveSelectionId(),
                Count = Selection.count,
                Objects = Selection.objects.Select(ToSelectionObjectInfo).ToList(),
                GameObjects = Selection.gameObjects.Select(ToGameObjectSelectionInfo).ToList(),
                AssetGUIDs = Selection.assetGUIDs,
            };
        }

        static SelectionObjectInfo ToSelectionObjectInfo(UnityEngine.Object obj)
        {
            if (obj == null)
                return null;

            var isPersistent = EditorUtility.IsPersistent(obj);
            var assetPath = isPersistent ? AssetDatabase.GetAssetPath(obj) : null;
            var gameObject = obj as GameObject;
            if (gameObject == null && obj is Component component)
                gameObject = component.gameObject;

            var scenePath = gameObject != null && gameObject.scene.IsValid()
                ? NormalizePath(gameObject.scene.path)
                : null;

            return new SelectionObjectInfo
            {
                Name = obj.name,
                Type = obj.GetType().FullName,
                InstanceID = UnityApiAdapter.GetObjectId(obj),
                IsPersistent = isPersistent,
                Path = isPersistent
                    ? NormalizePath(assetPath)
                    : gameObject != null ? BuildGameObjectPath(gameObject) : null,
                Guid = !string.IsNullOrWhiteSpace(assetPath) ? AssetDatabase.AssetPathToGUID(assetPath) : null,
                ScenePath = scenePath
            };
        }

        static GameObjectSelectionInfo ToGameObjectSelectionInfo(GameObject gameObject)
        {
            if (gameObject == null)
                return null;

            return new GameObjectSelectionInfo
            {
                Name = gameObject.name,
                InstanceID = UnityApiAdapter.GetObjectId(gameObject),
                Path = BuildGameObjectPath(gameObject),
                ScenePath = gameObject.scene.IsValid() ? NormalizePath(gameObject.scene.path) : null,
                IsActiveInHierarchy = gameObject.activeInHierarchy
            };
        }

        static GameObject ResolveGameObject(ManageEditorParams parameters)
        {
            if (parameters.InstanceID.HasValue &&
                parameters.InstanceID.Value >= int.MinValue &&
                parameters.InstanceID.Value <= int.MaxValue)
            {
                var obj = ResolveObjectByInstanceId((int)parameters.InstanceID.Value);
                if (obj is GameObject go)
                    return go;
                if (obj is Component component)
                    return component.gameObject;
            }

            var query = FirstNonEmpty(parameters.GameObjectPath, parameters.Target);
            if (string.IsNullOrWhiteSpace(query))
                return null;

            query = NormalizePath(query);
            var direct = GameObject.Find(query);
            if (direct != null)
                return direct;

            var allObjects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(IsSceneObject)
                .ToArray();

            return allObjects.FirstOrDefault(go =>
                    string.Equals(BuildGameObjectPath(go), query, StringComparison.OrdinalIgnoreCase))
                ?? allObjects.FirstOrDefault(go =>
                    string.Equals(go.name, query, StringComparison.OrdinalIgnoreCase));
        }

        static bool IsSceneObject(GameObject gameObject)
        {
            if (gameObject == null || EditorUtility.IsPersistent(gameObject))
                return false;
            return gameObject.scene.IsValid();
        }

        static UnityEngine.Object ResolveObjectByInstanceId(int instanceId)
        {
            var editorUtilityType = typeof(EditorUtility);
            var entityMethod = editorUtilityType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(method =>
                    method.Name == "EntityIdToObject" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));

            if (entityMethod != null)
                return entityMethod.Invoke(null, new object[] { instanceId }) as UnityEngine.Object;

            var legacyMethod = editorUtilityType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(method =>
                    method.Name == "InstanceIDToObject" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));

            return legacyMethod?.Invoke(null, new object[] { instanceId }) as UnityEngine.Object;
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

        static string NormalizeAssetPath(string path)
        {
            path = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var projectRoot = NormalizePath(Directory.GetParent(Application.dataPath)?.FullName);
            if (!string.IsNullOrWhiteSpace(projectRoot) &&
                path.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(projectRoot.Length + 1);
            }

            return path;
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/').Trim();
        }

        static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        static void FocusSceneView()
        {
            var sceneView = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
            sceneView?.Focus();
            sceneView?.Repaint();
        }

        static bool TryFrameSelection()
        {
            var sceneView = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
            if (sceneView == null)
                return false;

            sceneView.Focus();
            sceneView.FrameSelected();
            sceneView.Repaint();
            return true;
        }

        static bool IsEditorReady(bool requireNotPlaying)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return false;
            if (requireNotPlaying && EditorApplication.isPlayingOrWillChangePlaymode)
                return false;
            return true;
        }

        static object BuildReadinessData(bool requireNotPlaying, DateTime startedAtUtc)
        {
            var isReady = IsEditorReady(requireNotPlaying);
            return new
            {
                isReady,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                requireNotPlaying,
                elapsedMs = (int)(DateTime.UtcNow - startedAtUtc).TotalMilliseconds,
                timeSinceStartup = EditorApplication.timeSinceStartup
            };
        }

        static async Task WaitForEditorUpdateAsync(int minimumDelayMs)
        {
            minimumDelayMs = Clamp(minimumDelayMs, 0, 5000);
            var tcs = new TaskCompletionSource<bool>();
            var dueTime = EditorApplication.timeSinceStartup + minimumDelayMs / 1000.0;

            void Tick()
            {
                if (EditorApplication.timeSinceStartup < dueTime)
                    return;

                EditorApplication.update -= Tick;
                tcs.TrySetResult(true);
            }

            EditorApplication.update += Tick;
            SceneView.RepaintAll();
            InternalEditorUtility.RepaintAllViews();
            EditorApplication.QueuePlayerLoopUpdate();
            await tcs.Task;
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        // --- Helper Methods ---

        static void EnsureTagManagerEditable()
        {
            VersionControlUtility.EnsureAssetEditable("ProjectSettings/TagManager.asset", checkout: true, throwOnBlocked: true);
        }

        /// <summary>
        /// Gets the SerializedObject for the TagManager asset.
        /// </summary>
        static SerializedObject GetTagManager()
        {
            try
            {
                // Load the TagManager asset from the ProjectSettings folder
                UnityEngine.Object[] tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath(
                    "ProjectSettings/TagManager.asset"
                );
                if (tagManagerAssets == null || tagManagerAssets.Length == 0)
                {
                    Debug.LogError("[ManageEditor] TagManager.asset not found in ProjectSettings.");
                    return null;
                }
                // The first object in the asset file should be the TagManager
                return new SerializedObject(tagManagerAssets[0]);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ManageEditor] Error accessing TagManager.asset: {e.Message}");
                return null;
            }
        }

        // --- Example Implementations for Settings ---
        /*
        private static object SetGameViewResolution(int width, int height) { ... }
        private static object SetQualityLevel(JToken qualityLevelToken) { ... }
        */
    }

    // Helper class to get custom tool names (remains the same)
    static class EditorTools
    {
        public static string GetActiveToolName()
        {
            // This is a placeholder. Real implementation depends on how custom tools
            // are registered and tracked in the specific Unity project setup.
            // It might involve checking static variables, calling methods on specific tool managers, etc.
            if (UnityEditor.Tools.current == Tool.Custom)
            {
                // Example: Check a known custom tool manager
                // if (MyCustomToolManager.IsActive) return MyCustomToolManager.ActiveToolName;
                return "Unknown Custom Tool";
            }
            return UnityEditor.Tools.current.ToString();
        }
    }
}
