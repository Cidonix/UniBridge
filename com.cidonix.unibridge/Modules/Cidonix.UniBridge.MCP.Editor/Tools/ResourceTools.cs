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

        #region Public Tool Methods

        /// <summary>
        /// Lists Unity project resources (URIs) under a specified folder.
        /// </summary>
        /// <param name="parameters">Parameters controlling which resources to list, including pattern filter, base folder, and result limit.</param>
        /// <returns>A response dictionary containing success status, list of resource URIs, count, and any error messages.</returns>
        [McpTool("UniBridge_ListResources", ListResourcesDescription, ListResourcesTitle, Groups = new[] {"core", "resources"}, EnabledByDefault = true)]
        public static object ListResources(ListResourcesParams parameters)
        {
            // Provide defaults when parameters is null
            parameters ??= new ListResourcesParams();

            try
            {
                var projectRoot = ResourceUriHelper.ResolveProjectRoot(parameters.ProjectRoot);
                var basePath = Path.Combine(projectRoot, parameters.Under ?? "Assets");

                // Validate base path is under project root
                if (!ResourceUriHelper.IsPathUnderProject(basePath, projectRoot))
                {
                    return Response.Error("Base path must be under project root");
                }

                // Enforce listing only under Assets
                var assetsPath = Path.Combine(projectRoot, "Assets");
                if (!ResourceUriHelper.IsPathUnderProject(basePath, assetsPath))
                {
                    return Response.Error("Listing is restricted to Assets/");
                }

                var matches = new List<string>();
                var limit = Math.Max(1, parameters.Limit);
                var pattern = parameters.Pattern ?? "*";

                if (Directory.Exists(basePath))
                {
                    var files = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories);

                    foreach (var file in files)
                    {
                        try
                        {
                            var realPath = Path.GetFullPath(file);

                            // Ensure real path stays under project/Assets
                            if (!ResourceUriHelper.IsPathUnderProject(realPath, assetsPath))
                                continue;

                            // Apply pattern matching
                            if (!string.IsNullOrEmpty(pattern) && !ResourceUriHelper.MatchesGlobPattern(Path.GetFileName(file), pattern))
                                continue;

                            var relativePath = Path.GetRelativePath(projectRoot, realPath).Replace('\\', '/');
                            matches.Add($"unity://path/{relativePath}");

                            if (matches.Count >= limit)
                                break;
                        }
                        catch (Exception ex)
                        {
                            // Skip files that can't be processed (e.g., permission issues, path resolution failures)
                            Debug.LogWarning($"Skipping file '{file}' during resource listing: {ex.Message}");
                            continue;
                        }
                    }
                }

                // Always include the script-edits specification resource at the end
                if (!matches.Contains("unity://spec/script-edits"))
                {
                    matches.Add("unity://spec/script-edits");
                }

                var responseData = new ListResourcesResponse {Uris = matches, Count = matches.Count};

                return Response.Success("Resources listed successfully", responseData);
            }
            catch (Exception ex)
            {
                return Response.Error($"Error listing resources: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads a Unity project resource by URI with optional content slicing.
        /// </summary>
        /// <param name="parameters">Parameters specifying the URI to read and optional slicing options (line ranges, head bytes, tail lines, or natural language requests).</param>
        /// <returns>A response dictionary containing success status, file text content, metadata (SHA-256 and length), and any error messages.</returns>
        [McpTool("UniBridge_ReadResource", ReadResourceDescription, ReadResourceTitle, Groups = new[] {"core", "resources"}, EnabledByDefault = true)]
        public static object ReadResource(ReadResourceParams parameters)
        {
            if (parameters == null)
            {
                return Response.Error("Parameters cannot be null.");
            }

            try
            {
                // Handle special script-edits specification resource
                if (ResourceUriHelper.IsSpecialScriptEditsUri(parameters.Uri))
                {
                    var specJson = ResourceUriHelper.GetScriptEditsSpecification();
                    if (!string.IsNullOrEmpty(specJson))
                    {
                        var specSha = ResourceUriHelper.ComputeSha256(Encoding.UTF8.GetBytes(specJson));

                        var specResponse = new ReadResourceResponse {Text = specJson, Metadata = new ResourceMetadata {Sha256 = specSha, LengthBytes = Encoding.UTF8.GetByteCount(specJson)}};

                        return Response.Success("Dynamic script-edits specification retrieved", specResponse);
                    }
                }

                var projectRoot = ResourceUriHelper.ResolveProjectRoot(parameters.ProjectRoot);
                var filePath = ResourceUriHelper.ResolveSafePathFromUri(parameters.Uri, projectRoot);

                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    return Response.Error($"Resource not found: {parameters.Uri}");
                }

                // Ensure file is under Assets/
                var assetsPath = Path.Combine(projectRoot, "Assets");
                if (!ResourceUriHelper.IsPathUnderProject(filePath, assetsPath))
                {
                    return Response.Error("Read restricted to Assets/");
                }

                // Process natural language request and apply if no explicit values provided
                ResourceUriHelper.ProcessNaturalLanguageRequest(parameters.Request, filePath,
                    out var startLineFromRequest, out var lineCountFromRequest,
                    out var headBytesFromRequest, out var tailLinesFromRequest);

                var isDefaultSlicing = parameters.StartLine == 1 && parameters.LineCount == -1;

                var effectiveStartLine = isDefaultSlicing ? (startLineFromRequest ?? 1) : parameters.StartLine;
                var effectiveLineCount = isDefaultSlicing ? (lineCountFromRequest ?? -1) : parameters.LineCount;
                var effectiveHeadBytes = parameters.HeadBytes > 0 ? parameters.HeadBytes : (headBytesFromRequest ?? 0);
                var effectiveTailLines = parameters.TailLines > 0 ? parameters.TailLines : (tailLinesFromRequest ?? 0);

                // Read file and compute metadata
                var fileBytes = File.ReadAllBytes(filePath);
                var fullSha = ResourceUriHelper.ComputeSha256(fileBytes);

                // Determine if selection is requested
                bool selectionRequested = effectiveHeadBytes > 0 ||
                    effectiveTailLines > 0 ||
                    (effectiveStartLine != 1 || (effectiveLineCount != -1 && effectiveLineCount > 0)) ||
                    !string.IsNullOrEmpty(parameters.Request);

                var response = new ReadResourceResponse {Metadata = new ResourceMetadata {Sha256 = fullSha, LengthBytes = fileBytes.Length}};

                if (selectionRequested)
                {
                    // Apply windowing based on precedence: head_bytes > tail_lines > start_line+line_count
                    if (effectiveHeadBytes > 0)
                    {
                        var headBytes = Math.Min(effectiveHeadBytes, fileBytes.Length);
                        var headData = new byte[headBytes];
                        Array.Copy(fileBytes, headData, headBytes);
                        response.Text = Encoding.UTF8.GetString(headData);
                    }
                    else
                    {
                        var fileText = Encoding.UTF8.GetString(fileBytes);

                        if (effectiveTailLines > 0)
                        {
                            var lines = fileText.Split('\n');
                            var tailCount = Math.Min(effectiveTailLines, lines.Length);
                            var tailLines = lines.Skip(lines.Length - tailCount).ToArray();
                            response.Text = string.Join("\n", tailLines);
                        }
                        else if (effectiveStartLine != 1 || (effectiveLineCount != -1 && effectiveLineCount > 0))
                        {
                            var lines = fileText.Split('\n');
                            var startIdx = Math.Max(0, effectiveStartLine - 1);

                            if (effectiveLineCount == -1)
                            {
                                // Read from startLine to end of file
                                var selectedLines = lines.Skip(startIdx).ToArray();
                                response.Text = string.Join("\n", selectedLines);
                            }
                            else
                            {
                                // Read specific number of lines from startLine
                                var endIdx = Math.Min(lines.Length, startIdx + effectiveLineCount);
                                var selectedLines = lines.Skip(startIdx).Take(endIdx - startIdx).ToArray();
                                response.Text = string.Join("\n", selectedLines);
                            }
                        }
                        else
                        {
                            response.Text = fileText;
                        }
                    }
                }
                else
                {
                    // No selection requested - return full content
                    response.Text = Encoding.UTF8.GetString(fileBytes);
                }

                return Response.Success("Resource read successfully", response);
            }
            catch (Exception ex)
            {
                return Response.Error($"Error reading resource: {ex.Message}");
            }
        }

        /// <summary>
        /// Searches for regex patterns within a Unity project file.
        /// </summary>
        /// <param name="parameters">Parameters specifying the URI to search, regex pattern, case sensitivity, and maximum results.</param>
        /// <returns>A response dictionary containing success status, list of matches with line/column positions, match count, file SHA-256, and any error messages.</returns>
        [McpTool("UniBridge_FindInFile", FindInFileDescription, FindInFileTitle, Groups = new[] {"core", "resources"}, EnabledByDefault = true)]
        public static object FindInFile(FindInFileParams parameters)
        {
            if (parameters == null)
            {
                return Response.Error("Parameters cannot be null.");
            }

            try
            {
                var projectRoot = ResourceUriHelper.ResolveProjectRoot(parameters.ProjectRoot);
                var filePath = ResourceUriHelper.ResolveSafePathFromUri(parameters.Uri, projectRoot);

                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    return Response.Error($"Resource not found: {parameters.Uri}");
                }

                var fileBytes = File.ReadAllBytes(filePath);
                var fileText = Encoding.UTF8.GetString(fileBytes);
                var fileSha = ResourceUriHelper.ComputeSha256(fileBytes);
                var regexOptions = RegexOptions.Multiline;
                if (parameters.IgnoreCase)
                {
                    regexOptions |= RegexOptions.IgnoreCase;
                }

                var regex = new Regex(parameters.Pattern, regexOptions);
                var matches = new List<Dictionary<string, object>>();
                var maxResults = Math.Max(1, parameters.MaxResults);
                var lines = fileText.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var match = regex.Match(line);

                    if (match.Success)
                    {
                        matches.Add(new Dictionary<string, object>
                        {
                            ["startLine"] = i + 1, // 1-based line numbers
                            ["startCol"] = match.Index + 1, // 1-based column
                            ["endLine"] = i + 1,
                            ["endCol"] = match.Index + match.Length + 1 // End exclusive, 1-based
                        });

                        if (matches.Count >= maxResults)
                            break;
                    }
                }

                var responseData = new FindInFileResponse {Matches = matches, Count = matches.Count, Sha256 = fileSha};

                return Response.Success("File search completed successfully", responseData);
            }
            catch (Exception ex)
            {
                return Response.Error($"Error searching in file: {ex.Message}");
            }
        }

        #endregion
    }
}
