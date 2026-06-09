using System;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Handles validation of C# scripts and returns diagnostics.
    /// </summary>
    public static class ValidateScript
    {
        /// <summary>
        /// Description of the UniBridge_ValidateScript tool functionality and parameters.
        /// </summary>
        public const string Title = "Validate a C# script";

        public const string Description = @"Validate a C# script and report syntax or structural diagnostics.

Use this after creating or editing scripts, or before asking Unity to compile/reload a changed script.

Args:
    uri: unity://path/..., file://..., or Assets/... path to a .cs file.
    level: basic or standard.
    include_diagnostics: When true, returns detailed diagnostics; otherwise returns summary counts.

Validation levels:
    basic: Fast syntax and structural checks.
    standard: Adds common Unity/C# checks when available.

Returns:
    success, message, and data with warning/error counts plus optional diagnostics.";

        /// <summary>
        /// Gets the JSON schema describing the output format of the UniBridge_ValidateScript tool.
        /// </summary>
        /// <returns>An object representing the JSON schema for the tool's output.</returns>
        [McpOutputSchema("UniBridge_ValidateScript")]
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
                        description = "Validation results",
                        properties = new
                        {
                            warnings = new { type = "integer", description = "Number of warnings found" },
                            errors = new { type = "integer", description = "Number of errors found" },
                            diagnostics = new
                            {
                                type = "array",
                                description = "Full diagnostic information (when include_diagnostics=true)",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        severity = new { type = "string", description = "Diagnostic severity (error, warning, info)" },
                                        message = new { type = "string", description = "Diagnostic message" },
                                        line = new { type = "integer", description = "Line number" },
                                        column = new { type = "integer", description = "Column number" }
                                    }
                                }
                            },
                            summary = new
                            {
                                type = "object",
                                description = "Summary of validation results",
                                properties = new
                                {
                                    warnings = new { type = "integer", description = "Total warnings" },
                                    errors = new { type = "integer", description = "Total errors" }
                                }
                            }
                        }
                    }
                },
                required = new[] { "success", "message" }
            };
        }

        /// <summary>
        /// Handles script validation commands by analyzing C# scripts for errors and warnings.
        /// </summary>
        /// <param name="parameters">The validation parameters including URI, validation level, and diagnostic options.</param>
        /// <returns>A response object containing validation results with error and warning counts, and optionally full diagnostics.</returns>
        [McpTool("UniBridge_ValidateScript", Description, Title, Groups = new string[] { "core", "scripting" }, EnabledByDefault = true)]
        public static object HandleCommand(ValidateScriptParams parameters)
        {
            string uri = parameters?.Uri;
            if (string.IsNullOrEmpty(uri))
            {
                return Response.Error("uri parameter is required.");
            }

            string level = parameters.Level ?? "basic";
            if (level != "basic" && level != "standard")
            {
                return Response.Error("bad_level: level must be 'basic' or 'standard'.");
            }

            bool includeDiagnostics = parameters.IncludeDiagnostics;

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
                scriptParams["action"] = "validate";
                scriptParams["name"] = name;
                scriptParams["path"] = directory;
                scriptParams["level"] = level;

                // Call ManageScript.HandleCommand with the prepared parameters
                var result = ManageScript.HandleCommand(scriptParams);

                // Process the result to extract diagnostic counts and optionally full diagnostics
                if (result is JObject resultObj && resultObj["success"]?.Value<bool>() == true)
                {
                    var data = resultObj["data"] as JObject;
                    var diagnostics = data?["diagnostics"] as JArray;

                    if (diagnostics != null)
                    {
                        // Count warnings and errors
                        int warnings = 0;
                        int errors = 0;

                        foreach (var diag in diagnostics)
                        {
                            string severity = diag["severity"]?.ToString()?.ToLowerInvariant() ?? "";
                            if (severity == "warning")
                            {
                                warnings++;
                            }
                            else if (severity == "error" || severity == "fatal")
                            {
                                errors++;
                            }
                        }

                        // Return response based on includeDiagnostics flag
                        if (includeDiagnostics)
                        {
                            return Response.Success(
                                $"Script validation completed. Found {errors} error(s) and {warnings} warning(s).",
                                new
                                {
                                    diagnostics = diagnostics.ToObject<object[]>(),
                                    summary = new { warnings, errors }
                                }
                            );
                        }
                        else
                        {
                            return Response.Success(
                                $"Script validation completed. Found {errors} error(s) and {warnings} warning(s).",
                                new { warnings, errors }
                            );
                        }
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                return Response.Error($"validation_failed, Failed to validate script: {e.Message}");
            }
        }
    }
}
