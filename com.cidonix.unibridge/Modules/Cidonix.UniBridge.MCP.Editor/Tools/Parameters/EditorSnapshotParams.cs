#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum EditorSnapshotAction
    {
        Capture,
        Restore,
        List,
        Inspect,
        Delete,
        Clear
    }

    public record EditorSnapshotParams
    {
        [McpDescription("Operation to perform: Capture, Restore, List, Inspect, Delete, or Clear.", Required = false, Default = EditorSnapshotAction.Capture)]
        public EditorSnapshotAction Action { get; set; } = EditorSnapshotAction.Capture;

        [McpDescription("Optional human-readable snapshot name for Capture.", Required = false)]
        public string Name { get; set; }

        [McpDescription("Snapshot id for Restore, Inspect, or Delete.", Required = false)]
        public string SnapshotId { get; set; }

        [McpDescription("Inline snapshot JSON for Restore when SnapshotId is not used.", Required = false)]
        public string SnapshotJson { get; set; }

        [McpDescription("Persist captured snapshots under Library/UniBridge/EditorSnapshots. Default true.", Required = false, Default = true)]
        public bool? Persist { get; set; }

        [McpDescription("Include Scene View camera state in captured snapshots. Default true.", Required = false, Default = true)]
        public bool? IncludeSceneView { get; set; }

        [McpDescription("Include current selection in captured snapshots. Default true.", Required = false, Default = true)]
        public bool? IncludeSelection { get; set; }

        [McpDescription("Include focused/open window metadata in captured snapshots. Default true.", Required = false, Default = true)]
        public bool? IncludeWindows { get; set; }

        [McpDescription("Include active tab per Unity dock area when window metadata is captured. Default true.", Required = false, Default = true)]
        public bool? IncludeDockTabs { get; set; }

        [McpDescription("Include current Prefab Stage information in captured snapshots. Default true.", Required = false, Default = true)]
        public bool? IncludePrefabStage { get; set; }

        [McpDescription("Include Prefab Mode autosave settings in captured snapshots. Default true.", Required = false, Default = true)]
        public bool? IncludePrefabAutoSave { get; set; }

        [McpDescription("Preview restore without changing Editor state. Default false.", Required = false, Default = false)]
        public bool? DryRun { get; set; }

        [McpDescription("Restore loaded scenes and active scene from the snapshot. Default true.", Required = false, Default = true)]
        public bool? RestoreScenes { get; set; }

        [McpDescription("Restore the Scene View camera from the snapshot. Default true.", Required = false, Default = true)]
        public bool? RestoreSceneView { get; set; }

        [McpDescription("Restore selected objects/assets from the snapshot. Default true.", Required = false, Default = true)]
        public bool? RestoreSelection { get; set; }

        [McpDescription("Restore Prefab Mode to the captured prefab asset or main stage. Default true.", Required = false, Default = true)]
        public bool? RestorePrefabStage { get; set; }

        [McpDescription("Restore active tool, pivot mode, and pivot rotation from the snapshot. Default true.", Required = false, Default = true)]
        public bool? RestoreActiveTool { get; set; }

        [McpDescription("Focus the captured focused EditorWindow type when possible. Default true.", Required = false, Default = true)]
        public bool? RestoreFocusedWindow { get; set; }

        [McpDescription("Restore captured active dock tabs where possible. Default true.", Required = false, Default = true)]
        public bool? RestoreDockTabs { get; set; }

        [McpDescription("Also restore the captured focused window maximized state when possible. Default false.", Required = false, Default = false)]
        public bool? RestoreWindowMaximized { get; set; }

        [McpDescription("Restore captured Prefab Mode autosave settings. Default true.", Required = false, Default = true)]
        public bool? RestorePrefabAutoSave { get; set; }

        [McpDescription("Close currently loaded scenes that were not in the snapshot. Default true.", Required = false, Default = true)]
        public bool? CloseExtraScenes { get; set; }

        [McpDescription("Open scene paths from the snapshot that are not currently loaded. Default true.", Required = false, Default = true)]
        public bool? OpenMissingScenes { get; set; }

        [McpDescription("Save dirty loaded scenes before a restore that may reload/close scenes. Default false.", Required = false, Default = false)]
        public bool? SaveDirtyScenes { get; set; }

        [McpDescription("Allow scene restore even when dirty scenes would be reloaded/closed. Default false.", Required = false, Default = false)]
        public bool? AllowDirtySceneReload { get; set; }

        [McpDescription("Maximum snapshots returned by List. Default 50.", Required = false)]
        public int? Limit { get; set; }
    }
}
