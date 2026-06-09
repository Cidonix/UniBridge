#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    /// <summary>
    /// Parameters for UniBridge_Discover.
    /// </summary>
    public record DiscoverParams
    {
        [McpDescription("Discovery action: Ping, Workflows, Aliases, Tools, or Status.", Required = false, Default = "Ping")]
        public string Action { get; set; } = "Ping";

        [McpDescription("Optional search text for Action=Tools or Action=Aliases.", Required = false)]
        public string Query { get; set; }

        [McpDescription("Include the enabled registered tool list. Defaults to false for compact ping responses.", Required = false, Default = false)]
        public bool IncludeTools { get; set; } = false;

        [McpDescription("Maximum tools or aliases to return for list-style actions.", Required = false, Default = 48)]
        public int? Limit { get; set; } = 48;
    }
}
