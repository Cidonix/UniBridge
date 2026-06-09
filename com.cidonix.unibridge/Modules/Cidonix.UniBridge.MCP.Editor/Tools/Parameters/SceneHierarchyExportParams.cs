#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum SceneHierarchyExportAction
    {
        Export,
        CompareExports
    }

    public enum SceneHierarchyExportFormat
    {
        Json,
        Jsonl
    }

    /// <summary>
    /// Parameters for UniBridge_SceneHierarchyExport.
    /// </summary>
    public record SceneHierarchyExportParams
    {
        [McpDescription("Operation to perform: Export or CompareExports.", Required = false, Default = SceneHierarchyExportAction.Export)]
        public SceneHierarchyExportAction Action { get; set; } = SceneHierarchyExportAction.Export;

        [McpDescription("Optional scene path or scene name. When omitted, exports all currently loaded scenes.", Required = false)]
        public string ScenePath { get; set; }

        [McpDescription("Include inactive GameObjects. Defaults to true so complete scene exports do not miss disabled hierarchy branches.", Required = false, Default = true)]
        public bool IncludeInactive { get; set; } = true;

        [McpDescription("Include component type summaries and missing-script counts for each GameObject.", Required = false, Default = true)]
        public bool IncludeComponents { get; set; } = true;

        [McpDescription("Include renderer sorting/material details for Renderer, SpriteRenderer, ParticleSystemRenderer, Spine renderers, and related renderer types.", Required = false, Default = true)]
        public bool IncludeRenderers { get; set; } = true;

        [McpDescription("Include prefab asset path, GUID, local file id when available, and nearest prefab instance root.", Required = false, Default = true)]
        public bool IncludePrefabInfo { get; set; } = true;

        [McpDescription("Include URP Light2D sorting-layer masks when Light2D components are present.", Required = false, Default = true)]
        public bool IncludeLight2D { get; set; } = true;

        [McpDescription("Output format when writing a full export file: Json or Jsonl.", Required = false, Default = SceneHierarchyExportFormat.Json)]
        public SceneHierarchyExportFormat Format { get; set; } = SceneHierarchyExportFormat.Json;

        [McpDescription("Cursor returned by a previous export call. Numeric cursors are stable depth-first traversal offsets.", Required = false)]
        public string Cursor { get; set; }

        [McpDescription("Zero-based traversal offset. Ignored when Cursor is provided.", Required = false, Default = 0)]
        public int? Offset { get; set; }

        [McpDescription("Maximum number of objects to return inline from the stable depth-first traversal. The full export file still contains every object when WriteToFile or auto-file output is enabled.", Required = false)]
        public int? Limit { get; set; }

        [McpDescription("Write the full export to Library/UniBridge/SceneHierarchyExports even when it is small.", Required = false, Default = false)]
        public bool WriteToFile { get; set; }

        [McpDescription("Automatically write the full export to a file when total object count exceeds this threshold. Set 0 to always inline unless WriteToFile is true.", Required = false, Default = 500)]
        public int AutoFileThreshold { get; set; } = 500;

        [McpDescription("When ScenePath is not loaded and points to a scene asset, open it additively for export, then close it without saving. This temporarily changes editor scene state.", Required = false, Default = false)]
        public bool OpenIfNotLoaded { get; set; }

        [McpDescription("Path to the left export file for Action=CompareExports.", Required = false)]
        public string LeftExportPath { get; set; }

        [McpDescription("Path to the right export file for Action=CompareExports.", Required = false)]
        public string RightExportPath { get; set; }

        [McpDescription("Maximum diff rows to return for Action=CompareExports.", Required = false, Default = 200)]
        public int MaxDiffItems { get; set; } = 200;

        [McpDescription("Include verbose duplicate compare-key rows for Action=CompareExports. Default false keeps large UI clone hierarchies readable.", Required = false, Default = false)]
        public bool IncludeDuplicateKeys { get; set; }

        [McpDescription("Include bounded duplicate group examples in CompareExports summary. Disable for count-only compare responses.", Required = false, Default = true)]
        public bool IncludeDuplicateSummary { get; set; } = true;

        [McpDescription("Maximum duplicate groups/keys returned in compact summaries or verbose duplicate lists.", Required = false, Default = 10)]
        public int MaxDuplicateKeys { get; set; } = 10;

        [McpDescription("Maximum sample objectIds/indexedPaths returned per duplicate group.", Required = false, Default = 3)]
        public int MaxDuplicateSamples { get; set; } = 3;

        [McpDescription("Compare renderer sorting layer/order/material summaries for matching objects.", Required = false, Default = true)]
        public bool CompareRenderers { get; set; } = true;

        [McpDescription("Compare prefab source path/GUID summaries for matching objects.", Required = false, Default = true)]
        public bool ComparePrefabInfo { get; set; } = true;

        [McpDescription("Compare Light2D sorting-layer masks for matching objects.", Required = false, Default = true)]
        public bool CompareLight2D { get; set; } = true;
    }
}
