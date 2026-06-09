using System;
using Newtonsoft.Json;

namespace Cidonix.UniBridge.MCP.Editor.Models
{
    /// <summary>
    /// Represents a collection of MCP server configurations.
    /// This class is used to serialize/deserialize MCP client configuration files.
    /// </summary>
    [Serializable]
    class McpConfigServers
    {
        /// <summary>
        /// Gets or sets the UniBridge MCP server configuration.
        /// Contains the command, arguments, and connection settings for the UniBridge MCP server.
        /// </summary>
        [JsonProperty("unityMCP")]
        public McpConfigServer unityMCP;
    }
}
