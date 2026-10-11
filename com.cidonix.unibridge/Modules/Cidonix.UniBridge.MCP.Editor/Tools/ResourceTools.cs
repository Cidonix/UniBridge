using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry; // For McpTool attribute
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Resource wrapper tools so clients that do not expose MCP resources primitives
    /// can still list and read files via normal tools. These call into the same
    /// safe path logic to ensure security.
    /// </summary>
    public static class ResourceTools
    {
        #region Tool Descriptions

        /// <summary>
        /// Description text for the UniBridge_ListResources MCP tool.
        /// Explains how to list project files and URIs under a specified folder.
        /// </summary>
        public const string ListResourcesTitle = "List project resources";

        public const string ListResourcesDescription = @"List project files as unity://path/... resource URIs.

Use this to discover scripts, assets, and other files before reading or editing them. Results are limited to the Unity project, normally under Assets/.

Args:
    Pattern: Glob pattern such as *.cs, *.prefab, or *.
    Under: Folder under the project root to search; default is Assets.
    Limit: Maximum number of results; default is 200.
    ProjectRoot: Optional override for advanced clients.

Returns:
    success, message, and data containing uris and count.";

        /// <summary>
        /// Description text for the UniBridge_ReadResource MCP tool.
        /// Explains how to read file contents with optional line-based or byte-based slicing.
        /// </summary>
        public const string ReadResourceTitle = "Read a project resource";

        public const string ReadResourceDescription = @"Read text from a project resource by unity://path/... URI.

Use this before editing code so exact line contents and context are known. Supports line, head, tail, and simple natural-language slicing.

Args:
    Uri: Resource URI under Assets/.
    StartLine: 1-based starting line.
    LineCount: Number of lines to read.
    HeadBytes: Read bytes from the beginning when set.
    TailLines: Read lines from the end when set.
    Request: Optional request such as 'last 120 lines' or 'show 40 lines around MethodName'.
    ProjectRoot: Optional override for advanced clients.

Returns:
    success, message, and data containing text plus metadata about the returned slice.";

        /// <summary>
        /// Description text for the UniBridge_FindInFile MCP tool.
        /// Explains how to search files using regex patterns for methods, variables, or any text.
        /// </summary>
        public const string FindInFileTitle = "Search within a file";

        public const string FindInFileDescription = @"Search inside one project file with a regular expression.

Use this to locate methods, classes, fields, serialized names, or stable anchors before reading or editing.

Args:
    Uri: Resource URI under Assets/.
    Pattern: Regex pattern to search for.
    IgnoreCase: Case-insensitive search; default is true.
    MaxResults: Maximum matches to return; default is 200.
    ProjectRoot: Optional override for advanced clients.

Returns:
    success, message, and data containing matches with line numbers and excerpts.";

        #endregion


        [McpTool("UniBridge_ListResources", ListResourcesDescription, ListResourcesTitle, Groups = new[] {"core", "resources"}, EnabledByDefault = true)]
        public static object ListResources(ListResourcesParams parameters)
        {
            parameters ??= new ListResourcesParams();
            try
            {
                var root = ResourceUriHelper.ResolveProjectRoot(parameters.ProjectRoot);
                var folder = Path.GetFullPath(Path.Combine(root, parameters.Under ?? "Assets"));
                var assets = Path.Combine(root, "Assets");
                if (!ResourceUriHelper.IsPathUnderProject(folder, root)) return Response.Error("Base path must be under project root");
                if (!ResourceUriHelper.IsPathUnderProject(folder, assets)) return Response.Error("Listing is restricted to Assets/");
                var result = ResourceBudgetIO.List(root, folder, parameters, ToolExecutionScheduler.CurrentCancellationToken);
                // Preserve the existing built-in specification URI outside the requested file-result cap.
                if (!result.Uris.Contains("unity://spec/script-edits")) result.Uris.Add("unity://spec/script-edits");
                result.Count = result.Uris.Count;
                return Result(result, result.Outcome, "Resources listed successfully");
            }
            catch (Exception error) { return Response.Error("Error listing resources: " + error.Message); }
        }

        [McpTool("UniBridge_ReadResource", ReadResourceDescription, ReadResourceTitle, Groups = new[] {"core", "resources"}, EnabledByDefault = true)]
        public static object ReadResource(ReadResourceParams parameters)
        {
            if (parameters == null) return Response.Error("Parameters cannot be null.");
            try
            {
                if (ResourceUriHelper.IsSpecialScriptEditsUri(parameters.Uri))
                {
                    var specification = ResourceUriHelper.GetScriptEditsSpecification();
                    if (!string.IsNullOrEmpty(specification))
                    {
                        var generated = ResourceBudgetIO.Generated(specification, parameters, ToolExecutionScheduler.CurrentCancellationToken);
                        return Result(generated, generated.Outcome, "Dynamic script-edits specification retrieved");
                    }
                }
                var root = ResourceUriHelper.ResolveProjectRoot(parameters.ProjectRoot);
                var file = ResourceUriHelper.ResolveSafePathFromUri(parameters.Uri, root);
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return Response.Error("Resource not found: " + parameters.Uri);
                if (!ResourceUriHelper.IsPathUnderProject(file, Path.Combine(root, "Assets"))) return Response.Error("Read restricted to Assets/");
                var result = ResourceBudgetIO.Read(file, parameters, ToolExecutionScheduler.CurrentCancellationToken);
                return Result(result, result.Outcome, "Resource read successfully");
            }
            catch (Exception error) { return Response.Error("Error reading resource: " + error.Message); }
        }

        [McpTool("UniBridge_FindInFile", FindInFileDescription, FindInFileTitle, Groups = new[] {"core", "resources"}, EnabledByDefault = true)]
        public static object FindInFile(FindInFileParams parameters)
        {
            if (parameters == null) return Response.Error("Parameters cannot be null.");
            try
            {
                var root = ResourceUriHelper.ResolveProjectRoot(parameters.ProjectRoot);
                var file = ResourceUriHelper.ResolveSafePathFromUri(parameters.Uri, root);
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return Response.Error("Resource not found: " + parameters.Uri);
                var result = ResourceBudgetIO.Find(file, parameters, ToolExecutionScheduler.CurrentCancellationToken);
                return Result(result, result.Outcome, "File search completed successfully");
            }
            catch (Exception error) { return Response.Error("Error searching in file: " + error.Message); }
        }

        static object Result(object data, ResourceOperationOutcome outcome, string message)
        {
            if (outcome.Complete) return Response.Success(message, data);
            // An explicit match/result cap remains a successful bounded query, with
            // incomplete coverage declared. Operational cutoffs are errors with retained data.
            if (outcome.Status == "partial" && outcome.Reason == "result_limit")
                return Response.Success("Resource query reached its result limit; coverage is partial.", data);
            return Response.Error("RESOURCE_" + (outcome.Reason ?? outcome.Status).ToUpperInvariant(), data);
        }
    }
}
