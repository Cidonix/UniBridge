using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    /// <summary>
    /// Parameters for the UniBridge_ValidateScript tool.
    /// </summary>
    public record ValidateScriptParams
    {
        /// <summary>
        /// Gets or sets the URI or Assets-relative path to the C# script to validate.
        /// </summary>
        [McpDescription("URI or Assets-relative path to the script (e.g., 'unity://path/Assets/Scripts/MyScript.cs', 'file://...', or 'Assets/Scripts/MyScript.cs')", Required = true)]
        public string Uri { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the validation level. Non-basic levels also include advisory semantic obsolete API hints.
        /// </summary>
        [McpDescription("Validation level: basic (fast structural checks; semantic hints skipped), standard (Unity checks and semantic obsolete API hints), comprehensive, or strict. Hints never change validation success/counts.", Required = false)]
        public string Level { get; set; } = "basic";

        /// <summary>
        /// Gets or sets whether to include full validation diagnostic details. Semantic obsolete API hints remain available separately.
        /// </summary>
        [McpDescription("When true, returns full validation diagnostics; otherwise counts. Advisory obsoleteApiHints are returned independently for non-basic levels.", Required = false)]
        public bool IncludeDiagnostics { get; set; } = false;
    }
}
