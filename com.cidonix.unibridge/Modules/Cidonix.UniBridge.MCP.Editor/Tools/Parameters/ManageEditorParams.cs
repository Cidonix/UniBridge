using System;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    /// <summary>
    /// Actions that can be performed by the UniBridge_ManageEditor tool.
    /// </summary>
    public enum EditorAction
    {
        /// <summary>
        /// Enter play mode.
        /// </summary>
        Play,

        /// <summary>
        /// Queue entering play mode without waiting through a possible domain reload.
        /// </summary>
        RequestPlayModeNoWait,

        /// <summary>
        /// Wait until Unity is in play mode.
        /// </summary>
        WaitForPlayMode,

        /// <summary>
        /// Wait until Unity is back in edit mode.
        /// </summary>
        WaitForEditMode,

        /// <summary>
        /// Pause play mode.
        /// </summary>
        Pause,

        /// <summary>
        /// Exit play mode.
        /// </summary>
        Stop,

        /// <summary>
        /// Explicit alias for exiting play mode.
        /// </summary>
        ExitPlayMode,

        /// <summary>
        /// Get the current editor state.
        /// </summary>
        GetState,

        /// <summary>
        /// Get only play-mode related state.
        /// </summary>
        GetPlayModeState,

        /// <summary>
        /// Get retained compilation diagnostics captured from Unity compilation callbacks.
        /// </summary>
        GetCompilationDiagnostics,

        /// <summary>
        /// Get the project root directory path.
        /// </summary>
        GetProjectRoot,

        /// <summary>
        /// Get information about open editor windows.
        /// </summary>
        GetWindows,

        /// <summary>
        /// Get the currently active editor tool.
        /// </summary>
        GetActiveTool,

        /// <summary>
        /// Get the current selection in the editor.
        /// </summary>
        GetSelection,

        /// <summary>
        /// Get prefab stage information if a prefab is open for editing.
        /// </summary>
        GetPrefabStage,

        /// <summary>
        /// Set the active editor tool.
        /// </summary>
        SetActiveTool,

        /// <summary>
        /// Add a new tag to the project.
        /// </summary>
        AddTag,

        /// <summary>
        /// Remove a tag from the project.
        /// </summary>
        RemoveTag,

        /// <summary>
        /// Get all tags in the project.
        /// </summary>
        GetTags,

        /// <summary>
        /// Add a new layer to the project.
        /// </summary>
        AddLayer,

        /// <summary>
        /// Remove a layer from the project.
        /// </summary>
        RemoveLayer,

        /// <summary>
        /// Get all layers in the project.
        /// </summary>
        GetLayers,

        /// <summary>
        /// Select a Project asset by path.
        /// </summary>
        SelectAsset,

        /// <summary>
        /// Select a scene GameObject by hierarchy path, name, or instance ID.
        /// </summary>
        SelectGameObject,

        /// <summary>
        /// Clear the current Unity editor selection.
        /// </summary>
        ClearSelection,

        /// <summary>
        /// Ping the selected or resolved object in the Unity editor.
        /// </summary>
        PingSelection,

        /// <summary>
        /// Frame the current scene selection in the Scene View.
        /// </summary>
        FrameSelection,

        /// <summary>
        /// Wait until the editor is ready after compile, import, or play-mode transitions.
        /// </summary>
        WaitForReady,

        /// <summary>
        /// Wait until the editor is ready after a script-compilation assembly reload and include compilation diagnostics.
        /// </summary>
        WaitForReadyAfterReload,

        /// <summary>
        /// Explicit alias for waiting until the editor is idle.
        /// </summary>
        WaitIdle,

        /// <summary>
        /// Refresh the Unity AssetDatabase.
        /// </summary>
        RefreshAssets,

        /// <summary>
        /// Request C# script compilation.
        /// </summary>
        RequestScriptCompilation,

        /// <summary>
        /// Request C# script compilation without waiting through an assembly reload.
        /// </summary>
        RequestScriptCompilationNoWait,

        /// <summary>
        /// Save open dirty scenes, active prefab stage, and assets.
        /// </summary>
        SaveAll,

        /// <summary>
        /// Save project assets without saving scenes.
        /// </summary>
        SaveAssets,

        /// <summary>
        /// Regenerate Unity solution/project files when supported by this editor version.
        /// </summary>
        GenerateSolutionFiles,

        /// <summary>
        /// structured singular alias for regenerating Unity solution/project files.
        /// </summary>
        GenerateSolutionFile,

        /// <summary>
        /// Refresh externally changed assets and reopen loaded scenes/prefab stage when scene-like assets changed.
        /// </summary>
        ReloadCheckpoint
    }

    /// <summary>
    /// Parameters for the UniBridge_ManageEditor tool.
    /// </summary>
    public record ManageEditorParams
    {
        /// <summary>
        /// Gets or sets the operation to perform.
        /// </summary>
        [McpDescription("Operation to perform", Required = true, Default = EditorAction.GetState)]
        public EditorAction Action { get; set; } = EditorAction.GetState;

        /// <summary>
        /// Gets or sets whether to wait for certain actions to complete.
        /// </summary>
        [McpDescription("If true, waits for certain actions", Required = false)]
        public bool? WaitForCompletion { get; set; }

        /// <summary>
        /// Gets or sets the tool name for the set_active_tool action.
        /// </summary>
        [McpDescription("Tool name for set_active_tool action", Required = false)]
        public string ToolName { get; set; }

        /// <summary>
        /// Gets or sets the tag name for add_tag/remove_tag actions.
        /// </summary>
        [McpDescription("Tag name for add_tag/remove_tag actions", Required = false)]
        public string TagName { get; set; }

        /// <summary>
        /// Gets or sets the layer name for add_layer/remove_layer actions.
        /// </summary>
        [McpDescription("Layer name for add_layer/remove_layer actions", Required = false)]
        public string LayerName { get; set; }

        /// <summary>
        /// Gets or sets an asset path for SelectAsset or PingSelection.
        /// </summary>
        [McpDescription("Project asset path for SelectAsset or PingSelection, e.g. Assets/Sprites/Icon.png", Required = false)]
        public string AssetPath { get; set; }

        /// <summary>
        /// Gets or sets a GameObject hierarchy path for SelectGameObject or PingSelection.
        /// </summary>
        [McpDescription("Scene GameObject hierarchy path or name for SelectGameObject/PingSelection", Required = false)]
        public string GameObjectPath { get; set; }

        /// <summary>
        /// Gets or sets a generic target for selection actions.
        /// </summary>
        [McpDescription("Generic target path/name used when AssetPath or GameObjectPath is not supplied", Required = false)]
        public string Target { get; set; }

        /// <summary>
        /// Gets or sets a Unity instance ID target for scene selection.
        /// </summary>
        [McpDescription("Unity instance ID for selecting/pinging a scene object", Required = false)]
        public long? InstanceID { get; set; }

        /// <summary>
        /// Gets or sets whether to ping the selected or resolved object.
        /// </summary>
        [McpDescription("Ping the selected/resolved object in Project or Hierarchy view", Required = false, Default = true)]
        public bool? PingObject { get; set; }

        /// <summary>
        /// Gets or sets whether to focus a relevant Unity window after selection.
        /// </summary>
        [McpDescription("Focus a relevant Unity window after selection or ping", Required = false, Default = true)]
        public bool? Focus { get; set; }

        /// <summary>
        /// Gets or sets whether to frame the selected GameObject in Scene View.
        /// </summary>
        [McpDescription("Frame selected scene object(s) in Scene View", Required = false, Default = false)]
        public bool? FrameSceneView { get; set; }

        /// <summary>
        /// Gets or sets whether to force refresh/compilation when supported.
        /// </summary>
        [McpDescription("Force refresh/compilation when supported", Required = false, Default = false)]
        public bool? Force { get; set; }

        /// <summary>
        /// Gets or sets the timeout for WaitForReady/WaitForCompletion.
        /// </summary>
        [McpDescription("Timeout in milliseconds for editor readiness waits", Required = false, Default = 30000)]
        public int? TimeoutMs { get; set; }

        /// <summary>
        /// Gets or sets the poll interval for editor readiness waits.
        /// </summary>
        [McpDescription("Poll interval in milliseconds for editor readiness waits", Required = false, Default = 100)]
        public int? PollIntervalMs { get; set; }

        /// <summary>
        /// Gets or sets whether WaitForReady also requires Unity not to be entering or in play mode.
        /// </summary>
        [McpDescription("Require the editor not to be in/entering play mode before reporting ready", Required = false, Default = false)]
        public bool? RequireNotPlaying { get; set; }

        /// <summary>
        /// Gets or sets whether SaveAll should save scenes.
        /// </summary>
        [McpDescription("Save dirty open scenes in SaveAll", Required = false, Default = true)]
        public bool? SaveScenes { get; set; }

        /// <summary>
        /// Gets or sets whether SaveAll should save assets.
        /// </summary>
        [McpDescription("Save dirty assets in SaveAll", Required = false, Default = true)]
        public bool? SaveAssets { get; set; }

        /// <summary>
        /// Gets or sets asset paths modified outside Unity before ReloadCheckpoint.
        /// </summary>
        [McpDescription("Asset paths modified outside Unity before ReloadCheckpoint. Scene/prefab paths trigger scene/prefab reopening after AssetDatabase refresh.", Required = false)]
        public string[] ModifiedAssetPaths { get; set; }

        /// <summary>
        /// Gets or sets whether ReloadCheckpoint should reopen scenes when scene-like assets changed.
        /// </summary>
        [McpDescription("Reopen loaded scenes when ReloadCheckpoint detects changed loaded scene assets.", Required = false, Default = true)]
        public bool? RestoreScenes { get; set; }

        /// <summary>
        /// Gets or sets whether ReloadCheckpoint should reopen the active prefab stage when the prefab asset changed.
        /// </summary>
        [McpDescription("Reopen active prefab stage when ReloadCheckpoint detects the prefab asset changed.", Required = false, Default = true)]
        public bool? RestorePrefabStage { get; set; }

        /// <summary>
        /// Gets or sets whether ReloadCheckpoint should repaint Unity editor windows after refresh.
        /// </summary>
        [McpDescription("Repaint editor windows after ReloadCheckpoint refresh/reopen.", Required = false, Default = true)]
        public bool? RepaintEditor { get; set; }

        /// <summary>
        /// Gets or sets whether ReloadCheckpoint should save loaded scenes that were not externally modified.
        /// </summary>
        [McpDescription("Save loaded dirty scenes that are not in ModifiedAssetPaths before scene reopen.", Required = false, Default = true)]
        public bool? SaveUnmodifiedScenes { get; set; }

        /// <summary>
        /// Gets or sets whether ReloadCheckpoint may reload dirty scenes listed in ModifiedAssetPaths.
        /// </summary>
        [McpDescription("Allow ReloadCheckpoint to reopen dirty scenes that are listed in ModifiedAssetPaths. Default false prevents accidental scene-data loss.", Required = false, Default = false)]
        public bool? AllowDirtySceneReload { get; set; }
    }
}
