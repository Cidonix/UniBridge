using System;
using System.IO;
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
    uri: unity://path/..., file://..., Assets/..., Packages/..., or an absolute path to a .cs file.
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

            var resolved = ProjectPathResolver.Resolve(uri, assumeAssetRelative: true);
            var displayPath = resolved.DisplayPath ?? uri;
            if (string.IsNullOrWhiteSpace(resolved.AbsolutePath))
            {
                return Response.Error($"invalid_uri: URI must resolve to a project file. error={resolved.Error ?? "unknown"}");
            }

            if (!string.Equals(Path.GetExtension(resolved.AbsolutePath), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                return Response.Error("invalid_uri: URI must point to a .cs file.");
            }

            if (!File.Exists(resolved.AbsolutePath))
            {
                return Response.Error($"script_not_found: '{displayPath}' does not exist.");
            }

            try
            {
                var validation = ManageScript.ValidateScriptSource(File.ReadAllText(resolved.AbsolutePath), level);
                var diagnostics = JArray.FromObject(validation.Diagnostics ?? Array.Empty<ManageScript.ScriptDiagnostic>());
                var warnings = diagnostics.Count(diag => string.Equals(diag["severity"]?.ToString(), "warning", StringComparison.OrdinalIgnoreCase));
                var errors = diagnostics.Count(diag =>
                {
                    var severity = diag["severity"]?.ToString();
                    return string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(severity, "fatal", StringComparison.OrdinalIgnoreCase);
                });

                var data = new JObject
                {
                    ["warnings"] = warnings,
                    ["errors"] = errors,
                    ["summary"] = JObject.FromObject(new { warnings, errors }),
                    ["path"] = resolved.AssetPath ?? resolved.ProjectRelativePath,
                    ["absolutePath"] = resolved.AbsolutePath.Replace('\\', '/'),
                    ["pathResolution"] = JObject.FromObject(new
                    {
                        requested = resolved.Input,
                        displayPath = resolved.DisplayPath,
                        assetPath = resolved.AssetPath,
                        projectRelativePath = resolved.ProjectRelativePath,
                        isPackage = resolved.IsPackage,
                        isExternalPackage = resolved.IsExternalPackage,
                        exists = resolved.Exists
                    })
                };

                if (includeDiagnostics)
                    data["diagnostics"] = diagnostics;

                var message = $"Script validation completed. Found {errors} error(s) and {warnings} warning(s).";
                return validation.Ok ? Response.Success(message, data) : Response.Error(message, data);
            }
            catch (Exception e)
            {
                return Response.Error($"validation_failed, Failed to validate script: {e.Message}");
            }
        }
    }
}
