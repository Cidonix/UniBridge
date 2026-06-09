using System;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Handles getting UniBridge_Script capabilities including supported operations, limits, and guards.
    /// </summary>
    public static class ManageScriptCapabilities
    {
        public const string ToolName = "UniBridge_ScriptCapabilities";

        /// <summary>
        /// Description of the UniBridge_ScriptCapabilities tool for MCP clients.
        /// Returns information about supported operations, payload limits, and guard settings for script management.
        /// </summary>
        public const string Title = "Get script router capabilities";

        public const string Description = @"Report supported structured script-edit operations and server-side edit limits.

Use this when a client needs to discover which UniBridge_Script edit routes and safeguards are available before constructing edit requests.

Returns:
    ops: Structured operations such as replace_method, insert_method, delete_method, and anchor_*.
    text_ops: Text operations such as replace_range, regex_replace, prepend, and append.
    max_edit_payload_bytes: Maximum edit payload size accepted by the bridge.
    guards: Enabled edit safeguards.
    extras: Additional feature flags such as get_sha support.";

        /// <summary>
        /// Returns the output schema for this tool.
        /// </summary>
        /// <returns>The JSON schema object describing the tool's output structure.</returns>
        [McpOutputSchema(ToolName)]
        public static object GetOutputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    success = new {type = "boolean", description = "Whether the operation succeeded"},
                    message = new {type = "string", description = "Human-readable message about the operation"},
                    data = new
                    {
                        type = "object",
                        description = "Script capabilities information",
                        properties = new
                        {
                            ops = new {type = "array", description = "List of supported structured operations", items = new {type = "string"}},
                            text_ops = new {type = "array", description = "List of supported text operations", items = new {type = "string"}},
                            max_edit_payload_bytes = new {type = "integer", description = "Maximum edit payload size in bytes"},
                            guards = new {type = "object", description = "Guard settings", properties = new {using_guard = new {type = "boolean", description = "Whether using guard is enabled"}}},
                            extras = new {type = "object", description = "Extra capabilities", properties = new {get_sha = new {type = "boolean", description = "Whether get_sha is supported"}}}
                        }
                    }
                },
                required = new[] {"success", "message"}
            };
        }

        /// <summary>
        /// Main handler for getting script capabilities.
        /// </summary>
        /// <param name="parameters">The parameters for retrieving script capabilities.</param>
        /// <returns>A response object containing supported operations, limits, and guards.</returns>
        [McpTool(ToolName, Description, Title, Groups = new string[] {"core", "scripting"}, EnabledByDefault = true)]
        public static object HandleCommand(ManageScriptCapabilitiesParams parameters)
        {
            try
            {
                // Keep in sync with server/Editor script router implementation.
                var ops = new[] {"replace_class", "delete_class", "replace_method", "delete_method", "insert_method", "anchor_insert", "anchor_delete", "anchor_replace"};

                var textOps = new[] {"replace_range", "regex_replace", "prepend", "append"};

                // Match the script router payload limit if exposed; hardcode a sensible default fallback.
                int maxEditPayloadBytes = 256 * 1024;

                var guards = new {using_guard = true};
                var extras = new {get_sha = true};

                return Response.Success("Retrieved UniBridge_Script capabilities successfully", new
                {
                    ops,
                    text_ops = textOps,
                    max_edit_payload_bytes = maxEditPayloadBytes,
                    guards,
                    extras
                });
            }
            catch (Exception e)
            {
                return Response.Error($"capabilities error: {e.Message}");
            }
        }
    }
}
