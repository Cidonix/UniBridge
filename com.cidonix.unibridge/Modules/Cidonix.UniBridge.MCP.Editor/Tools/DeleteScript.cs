using System;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Handles deletion of C# scripts by URI or Assets-relative path.
    /// </summary>
    public static class DeleteScript
    {
        /// <summary>
        /// Human-readable description of the UniBridge_DeleteScript tool functionality and usage.
        /// </summary>
        public const string Title = "Delete a C# script";

        public const string Description = @"Delete a C# script under Assets/.

Use this only when the file is intentionally being removed from the Unity project.

Args:
    uri: unity://path/..., file://..., or Assets/... path to a .cs file.

Rules:
    The target must resolve under Assets/.

Returns:
    success, message, and data with deleted status and path.";

        /// <summary>
        /// Returns the output schema for this tool.
        /// </summary>
        /// <returns>The output schema object defining the structure of successful responses.</returns>
        [McpOutputSchema("UniBridge_DeleteScript")]
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
                        description = "Script deletion data",
                        properties = new
                        {
                            deleted = new { type = "boolean", description = "Whether the script was deleted" },
                            path = new { type = "string", description = "Relative path of the deleted script" }
                        }
                    }
                },
                required = new[] { "success", "message" }
            };
        }


        /// <summary>
        /// Main handler for script deletion.
        /// </summary>
        /// <param name="parameters">Parameters containing the URI or path of the script to delete.</param>
        /// <returns>A response object indicating success or failure with relevant details.</returns>
        [McpTool("UniBridge_DeleteScript", Description, Title, Groups = new string[] { "core", "scripting" }, EnabledByDefault = true)]
        public static object HandleCommand(DeleteScriptParams parameters)
        {
            string uri = parameters?.Uri;
            if (string.IsNullOrEmpty(uri))
            {
                return Response.Error("uri parameter is required.");
            }

            // Split URI into name and directory using ScriptRefreshHelpers
            var (name, directory) = ScriptRefreshHelpers.SplitUri(uri);

            // Validate the split result
            if (string.IsNullOrEmpty(name))
            {
                return Response.Error("invalid_uri: URI must include a script file name.");
            }

            if (string.IsNullOrEmpty(directory))
            {
                return Response.Error("invalid_uri: URI must include a valid directory path.");
            }

            // Ensure directory is under Assets/
            if (!directory.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                return Response.Error("path_outside_assets: URI must resolve under 'Assets/'.");
            }

            try
            {
                // Create JObject parameters for ManageScript.HandleCommand
                var scriptParams = new JObject();
                scriptParams["action"] = "delete";
                scriptParams["name"] = name;
                scriptParams["path"] = directory;

                // Call ManageScript.HandleCommand with the prepared parameters
                var result = ManageScript.HandleCommand(scriptParams);

                return result;
            }
            catch (Exception e)
            {
                return Response.Error($"delete_failed, Failed to delete script: {e.Message}");
            }
        }
    }
}
