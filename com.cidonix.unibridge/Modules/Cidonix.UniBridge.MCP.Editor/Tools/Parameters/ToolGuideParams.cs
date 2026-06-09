#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    /// <summary>
    /// Parameters for UniBridge_ToolGuide.
    /// </summary>
    public record ToolGuideParams
    {
        [McpDescription("Guide action: Overview, Workflow, or Tool.", Required = false, Default = "Overview")]
        public string Action { get; set; } = "Overview";

        [McpDescription("Workflow/topic name for Action=Workflow. Examples: orientation, scene_objects, ui, assets_import, materials, scriptable_objects, unity_events, visual_capture, animator, scripts, batch, console, search.", Required = false)]
        public string Topic { get; set; }

        [McpDescription("Tool name or alias for Action=Tool. Examples: ui, UnitySearch, UniBridge_ManageUI, asset_importer.", Required = false)]
        public string Tool { get; set; }

        [McpDescription("Include a compact registered-tool list in the response.", Required = false, Default = false)]
        public bool? IncludeRegisteredTools { get; set; }
    }
}
