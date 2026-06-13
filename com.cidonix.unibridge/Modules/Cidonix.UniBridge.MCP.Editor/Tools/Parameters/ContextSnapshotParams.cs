using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    /// <summary>
    /// Controls how much project context is collected by UniBridge_ContextSnapshot.
    /// </summary>
    public enum ContextSnapshotDepth
    {
        /// <summary>
        /// Minimal editor, scene, and console state.
        /// </summary>
        Brief,

        /// <summary>
        /// Balanced project context suitable for most AI work.
        /// </summary>
        Standard,

        /// <summary>
        /// Broader context with more hierarchy, asset, tool, and window data.
        /// </summary>
        Detailed
    }

    public enum ContextSnapshotConsoleSummaryMode
    {
        Compact,
        Detailed
    }

    /// <summary>
    /// Parameters for the UniBridge_ContextSnapshot tool.
    /// </summary>
    public record ContextSnapshotParams
    {
        [McpDescription("Snapshot detail level: Brief, Standard, or Detailed.", Required = false, Default = ContextSnapshotDepth.Standard)]
        public ContextSnapshotDepth Depth { get; set; } = ContextSnapshotDepth.Standard;

        [McpDescription("Include a compact console diagnostic summary.", Required = false, Default = true)]
        public bool? IncludeConsole { get; set; }

        [McpDescription("Console summary mode for IncludeConsole: Compact returns totals and grouped issues only; Detailed also keeps samples/timeline highlights.", Required = false, Default = ContextSnapshotConsoleSummaryMode.Compact)]
        public ContextSnapshotConsoleSummaryMode ConsoleSummaryMode { get; set; } = ContextSnapshotConsoleSummaryMode.Compact;

        [McpDescription("Include a bounded scene hierarchy summary.", Required = false, Default = true)]
        public bool? IncludeHierarchy { get; set; }

        [McpDescription("Include current editor selection details.", Required = false, Default = true)]
        public bool? IncludeSelection { get; set; }

        [McpDescription("Include project asset counts and recent asset paths.", Required = false, Default = true)]
        public bool? IncludeAssets { get; set; }

        [McpDescription("Include available UniBridge tool names and enabled state.", Required = false, Default = true)]
        public bool? IncludeTools { get; set; }

        [McpDescription("Include open Editor window metadata. Defaults to true only in Detailed mode.", Required = false)]
        public bool? IncludeWindows { get; set; }

        [McpDescription("Include scene Build Settings. Defaults to true only in Detailed mode.", Required = false)]
        public bool? IncludeBuildSettings { get; set; }

        [McpDescription("Include project roots for Assets, ProjectSettings, Packages, and registered package count. Full registered package roots are included only for Detailed or IncludePackageDependencies=true.", Required = false, Default = true)]
        public bool? IncludeProjectRoots { get; set; }

        [McpDescription("Include Unity project settings such as render pipeline, 2D/3D mode, tags, layers, and sorting layers. Defaults to true.", Required = false, Default = true)]
        public bool? IncludeProjectSettings { get; set; }

        [McpDescription("Include package dependency overview from Packages/packages-lock.json or manifest.json. Defaults to Standard/Detailed only.", Required = false)]
        public bool? IncludePackageDependencies { get; set; }

        [McpDescription("Include a compact new-agent onboarding brief with project shape, risk flags, guardrails, and recommended next UniBridge calls. Defaults to true.", Required = false, Default = true)]
        public bool? IncludeAgentBrief { get; set; }

        [McpDescription("Hierarchy depth: 0 for roots only, 1+ for child levels. Values are clamped to 0..3.", Required = false)]
        public int? HierarchyDepth { get; set; }

        [McpDescription("Maximum scene GameObjects returned in hierarchy summaries.", Required = false)]
        public int? MaxSceneObjects { get; set; }

        [McpDescription("Maximum recent assets returned.", Required = false)]
        public int? MaxAssets { get; set; }

        [McpDescription("Maximum console issue groups returned in the embedded diagnostic summary.", Required = false)]
        public int? MaxConsoleIssues { get; set; }

        [McpDescription("Maximum tool entries returned when IncludeTools is enabled.", Required = false)]
        public int? MaxTools { get; set; }
    }
}
