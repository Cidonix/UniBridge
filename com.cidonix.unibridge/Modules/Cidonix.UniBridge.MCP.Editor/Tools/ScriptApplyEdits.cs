using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Structured C# script editing with safer boundaries and comprehensive validation.
    /// This tool provides advanced script editing capabilities including method/class operations
    /// and anchor-based pattern matching with improved heuristics.
    /// </summary>
    public static class ScriptApplyEdits
    {
        /// <summary>
        /// Description of the ScriptApplyEdits tool functionality and parameters.
        /// </summary>
        public const string Title = "Apply structured C# edits";

        public const string Description = @"Apply structured C# edits to methods, classes, or anchored code regions.

Use this instead of raw text edits when changing whole methods, adding methods to a class, or inserting/replacing text near a stable regex anchor. It helps keep braces and using/header structure safe.

Args:
    name: Script name without .cs extension.
    path: Folder under Assets/ containing the script.
    edits: List of structured edits.
    options: Optional validate, refresh, and applyMode settings.
    preconditionSha256: Optional SHA256 guard. The edit fails with stale_file if the script changed.
    preview: When true, return a diff without modifying the script.

Supported edit ops:
    replace_method, insert_method, delete_method, anchor_insert, anchor_delete, anchor_replace.

Common fields:
    className: Target class name; defaults to name when omitted.
    methodName: Required for replace_method and delete_method.
    replacement: New method/class text for replace_method or insert_method.
    position: start, end, after, or before for insert_method.
    afterMethodName / beforeMethodName: Anchor method names for positional insertions.
    anchor: Regex anchor for anchor_* operations.
    text: Text inserted or used by anchor_insert/anchor_replace.
    matchIndex: Optional zero-based match index when the regex has multiple matches.
    preferLast: Explicitly select the last (true) or first (false) match.
    allowNoop: Explicitly allow a missing anchor to produce no changes; defaults to false.

Anchor safety:
    Missing anchors fail by default. Ambiguous anchors fail unless matchIndex, preferLast,
    or allowAmbiguous is supplied explicitly.

Structured Preview safety:
    Preview=true computes method/class edits in memory, returns diff plus current/predicted SHA,
    applies zero edits, and never schedules an AssetDatabase refresh.

Examples:
1) Replace a method:
{
  ""name"": ""SmartReach"",
  ""path"": ""Assets/Scripts/Interaction"",
  ""edits"": [{
    ""op"": ""replace_method"",
    ""className"": ""SmartReach"",
    ""methodName"": ""HasTarget"",
    ""replacement"": ""public bool HasTarget(){ return currentTarget!=null; }""
  }],
  ""options"": {""validate"": ""standard"", ""refresh"": ""immediate""}
}

2) Insert a method after another:
{
  ""name"": ""SmartReach"",
  ""path"": ""Assets/Scripts/Interaction"",
  ""edits"": [{
    ""op"": ""insert_method"",
    ""className"": ""SmartReach"",
    ""replacement"": ""public void PrintSeries(){ Debug.Log(seriesName); }"",
    ""position"": ""after"",
    ""afterMethodName"": ""GetCurrentTarget""
  }]
}";

        /// <summary>
        /// Returns the output schema for this tool.
        /// </summary>
        /// <returns>The JSON schema object describing the tool's output structure.</returns>
        [McpOutputSchema("UniBridge_ScriptApplyEdits")]
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
                        description = "Script edit results",
                        properties = new
                        {
                            uri = new { type = "string", description = "Unity URI of the edited script" },
                            path = new { type = "string", description = "Relative path of the edited script" },
                            editsApplied = new { type = "integer", description = "Number of edits applied" },
                            sha256 = new { type = "string", description = "SHA256 hash of the modified script" },
                            scheduledRefresh = new { type = "boolean", description = "Whether a refresh was scheduled" },
                            no_op = new { type = "boolean", description = "Whether this was a no-op (no changes made)" },
                            normalizedEdits = new { type = "array", description = "Normalized edit operations that were applied" },
                            routing = new { type = "string", description = "Edit routing method used (structured/text/mixed)" },
                            warnings = new { type = "array", description = "Any warnings generated during processing" }
                        }
                    }
                },
                required = new[] { "success", "message" }
            };
        }

        /// <summary>
        /// Main handler for structured script edits.
        /// </summary>
        /// <param name="parameters">The parameters specifying the script edits to apply.</param>
        /// <returns>A response object containing success status, message, and optional data.</returns>
        [McpTool("UniBridge_ScriptApplyEdits", Description, Title, Groups = new string[] { "core", "scripting" }, EnabledByDefault = true)]
        public static object HandleCommand(ScriptApplyEditsParams parameters)
        {
            if (parameters == null)
            {
                return Response.Error("Parameters cannot be null.");
            }

            string name = parameters.Name?.Trim();
            string path = parameters.Path?.Trim();

            if (string.IsNullOrEmpty(name))
            {
                return Response.Error("Name parameter is required.");
            }

            // Normalize script locator
            var (normalizedName, normalizedPath) = NormalizeScriptLocator(name, path);

            // Validate script name
            if (!Regex.IsMatch(normalizedName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            {
                return Response.Error($"Invalid script name: '{normalizedName}'. Use only letters, numbers, underscores, and don't start with a number.");
            }

            try
            {
                // Normalize edits and handle aliases
                var (normalizedEdits, structuredError) = EditNormalizer.NormalizeEdits(
                    parameters.Edits ?? new List<Dictionary<string, object>>(),
                    normalizedName);

                if (structuredError != null)
                {
                    return structuredError;
                }

                // Validate tool availability
                var availabilityErrors = EditNormalizer.ValidateToolAvailability(normalizedEdits);
                if (availabilityErrors.Any())
                {
                    return Response.Error($"Unsupported operations: {string.Join(", ", availabilityErrors)}", new
                    {
                        normalizedEdits = normalizedEdits,
                        errors = availabilityErrors
                    });
                }

                // Determine routing strategy
                string routing = EditNormalizer.DetermineRouting(normalizedEdits);

                // Add top-level Preview parameter to options if not already present
                var optionsToUse = parameters.Options ?? new Dictionary<string, object>();
                if (parameters.Preview && !optionsToUse.ContainsKey("preview"))
                {
                    optionsToUse = new Dictionary<string, object>(optionsToUse);
                    optionsToUse["preview"] = true;
                }
                if (!string.IsNullOrWhiteSpace(parameters.PreconditionSha256) &&
                    !optionsToUse.ContainsKey("preconditionSha256"))
                {
                    if (ReferenceEquals(optionsToUse, parameters.Options))
                        optionsToUse = new Dictionary<string, object>(optionsToUse);
                    optionsToUse["preconditionSha256"] = parameters.PreconditionSha256.Trim();
                }

                // Execute edits based on routing
                switch (routing)
                {
                    case "structured":
                        return ExecuteStructuredEdits(normalizedName, normalizedPath, normalizedEdits, optionsToUse, parameters.ScriptType, parameters.Namespace);

                    case "text":
                        return ExecuteTextEdits(normalizedName, normalizedPath, normalizedEdits, optionsToUse, parameters.ScriptType, parameters.Namespace);

                    case "mixed":
                        return ExecuteMixedEdits(normalizedName, normalizedPath, normalizedEdits, optionsToUse, parameters.ScriptType, parameters.Namespace);

                    default:
                        return Response.Error($"Unknown routing strategy: {routing}", new
                        {
                            normalizedEdits = normalizedEdits,
                            routing = routing
                        });
                }
            }
            catch (Exception ex)
            {
                return Response.Error($"Script edit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Execute purely structured edits (method/class operations)
        /// </summary>
        static object ExecuteStructuredEdits(string name, string path, List<Dictionary<string, object>> edits,
            Dictionary<string, object> options, string scriptType, string namespaceName)
        {
            var opts = new Dictionary<string, object>(options ?? new Dictionary<string, object>());
            opts.TryAdd("refresh", "immediate"); // Prefer immediate refresh for structured edits

            var managementParams = new JObject
            {
                ["action"] = "edit",
                ["name"] = name,
                ["path"] = path,
                ["namespace"] = namespaceName,
                ["scriptType"] = scriptType,
                ["edits"] = JArray.FromObject(edits),
                ["options"] = JObject.FromObject(opts)
            };

            var precondition = GetPreconditionSha256(opts);
            if (!string.IsNullOrEmpty(precondition))
                managementParams["precondition_sha256"] = precondition;

            var result = ManageScript.HandleCommand(managementParams);

            // Enhance result with routing information
            if (result is object resultObj)
            {
                var resultDict = GetObjectProperties(resultObj);
                var data = resultDict.GetValueOrDefault("data") as Dictionary<string, object> ?? new Dictionary<string, object>();
                data["normalizedEdits"] = edits;
                data["routing"] = "structured";
                resultDict["data"] = data;
            }

            return result;
        }

        /// <summary>
        /// Execute text-based edits (anchor operations, regex, ranges)
        /// </summary>
        static object ExecuteTextEdits(string name, string path, List<Dictionary<string, object>> edits,
            Dictionary<string, object> options, string scriptType, string namespaceName)
        {
            // First read the current script content
            var readParams = new JObject
            {
                ["action"] = "read",
                ["name"] = name,
                ["path"] = path,
                ["namespace"] = namespaceName,
                ["scriptType"] = scriptType
            };

            var readResult = ManageScript.HandleCommand(readParams);
            if (!IsSuccessResponse(readResult))
            {
                return readResult;
            }

            string contents = ExtractContentsFromReadResult(readResult);
            if (contents == null)
            {
                return Response.Error("Failed to read script contents for text editing.");
            }

            var preconditionError = ValidatePrecondition(contents, options);
            if (preconditionError != null)
                return preconditionError;

            // Try to convert and apply text edits directly
            var result = ConvertAndApplyTextEdits(name, path, namespaceName, scriptType, edits, contents, options);
            if (result != null)
            {
                return result;
            }

            // Handle preview logic for regex_replace
            bool preview = GetBoolValue(options, "preview");
            bool confirm = GetBoolValue(options, "confirm", false);
            var textOps = edits.Select(e => GetStringValue(e, "op")?.ToLowerInvariant() ?? "").ToHashSet();
            var hasRegexReplace = textOps.Contains("regex_replace");

            if (hasRegexReplace && (preview || !confirm))
            {
                try
                {
                    // Apply edits locally to generate preview
                    string previewText = ApplyEditsLocally(contents, edits);
                    string diff = GenerateUnifiedDiff(contents, previewText);

                    if (preview)
                    {
                        return Response.Success("Preview only (no write)", new
                        {
                            diff = diff,
                            normalizedEdits = edits,
                            routing = "text"
                        });
                    }

                    // For regex_replace without confirm, show preview and require confirmation
                    return Response.Error("Preview diff; set options.confirm=true to apply.", new
                    {
                        diff = diff,
                        normalizedEdits = edits,
                        routing = "text"
                    });
                }
                catch (Exception ex)
                {
                    return Response.Error($"Preview failed: {ex.Message}", new
                    {
                        normalizedEdits = edits,
                        routing = "text"
                    });
                }
            }

            // Apply edits locally
            string newContents;
            try
            {
                newContents = ApplyEditsLocally(contents, edits);
            }
            catch (Exception ex)
            {
                return Response.Error($"Edit application failed: {ex.Message}");
            }

            // Short-circuit no-op edits
            if (newContents == contents)
            {
                return Response.Success("No-op: contents unchanged", new
                {
                    no_op = true,
                    evidence = new { reason = "identical_content" },
                    normalizedEdits = edits,
                    routing = "text"
                });
            }

            // Handle general preview mode
            if (preview)
            {
                try
                {
                    string diff = GenerateUnifiedDiff(contents, newContents);
                    return Response.Success("Preview only (no write)", new
                    {
                        diff = diff,
                        normalizedEdits = edits,
                        routing = "text"
                    });
                }
                catch (Exception ex)
                {
                    return Response.Error($"Preview diff generation failed: {ex.Message}", new
                    {
                        normalizedEdits = edits,
                        routing = "text"
                    });
                }
            }

            // Fallback: send as whole-file replacement
            try
            {

                var lines = contents.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                int endLine = lines.Length + 1; // 1-based exclusive end
                string sha = ComputeSha256(contents);

                var fallbackParams = new JObject
                {
                    ["action"] = "apply_text_edits",
                    ["name"] = name,
                    ["path"] = path,
                    ["namespace"] = namespaceName,
                    ["scriptType"] = scriptType,
                    ["edits"] = new JArray
                    {
                        new JObject
                        {
                            ["startLine"] = 1,
                            ["startCol"] = 1,
                            ["endLine"] = endLine,
                            ["endCol"] = 1,
                            ["newText"] = newContents
                        }
                    },
                    ["precondition_sha256"] = sha,
                    ["options"] = JObject.FromObject(new Dictionary<string, object>
                    {
                        ["validate"] = GetStringValue(options, "validate") ?? "standard",
                        ["refresh"] = GetStringValue(options, "refresh") ?? "debounced"
                    })
                };

                var fallbackResult = ManageScript.HandleCommand(fallbackParams);

                // Enhance result with routing information
                if (fallbackResult is object fallbackResultObj)
                {
                    var resultDict = GetObjectProperties(fallbackResultObj);
                    var data = resultDict.GetValueOrDefault("data") as Dictionary<string, object> ?? new Dictionary<string, object>();
                    data["normalizedEdits"] = edits;
                    data["routing"] = "text/fallback";
                    resultDict["data"] = data;
                }

                return fallbackResult;
            }
            catch (Exception ex)
            {
                return Response.Error($"Fallback edit application failed: {ex.Message}", new
                {
                    normalizedEdits = edits,
                    routing = "text/fallback"
                });
            }
        }

        /// <summary>
        /// Execute mixed edits (combination of structured and text operations)
        /// </summary>
        static object ExecuteMixedEdits(string name, string path, List<Dictionary<string, object>> edits,
            Dictionary<string, object> options, string scriptType, string namespaceName)
        {
            // Mixed edits are order-dependent. Run the complete normalized list through
            // one in-memory EditScript pass so Preview and Apply share identical semantics.
            var opts = new Dictionary<string, object>(options ?? new Dictionary<string, object>())
            {
                ["applyMode"] = "sequential"
            };
            opts.TryAdd("refresh", "debounced");

            var managementParams = new JObject
            {
                ["action"] = "edit",
                ["name"] = name,
                ["path"] = path,
                ["namespace"] = namespaceName,
                ["scriptType"] = scriptType,
                ["edits"] = JArray.FromObject(edits),
                ["options"] = JObject.FromObject(opts)
            };

            var precondition = GetPreconditionSha256(opts);
            if (!string.IsNullOrEmpty(precondition))
                managementParams["precondition_sha256"] = precondition;

            var result = ManageScript.HandleCommand(managementParams);
            if (!IsSuccessResponse(result))
                return result;

            var resultProperties = GetObjectProperties(result);
            var data = GetObjectProperties(resultProperties.GetValueOrDefault("data"));
            var preview = GetBoolValue(opts, "preview");
            data["normalizedEdits"] = edits;
            data["routing"] = preview ? "mixed/preview" : "mixed/sequential";
            data["executionModel"] = "single_in_memory_pipeline";

            var message = preview
                ? $"Previewed {edits.Count} mixed edit(s) for '{path}/{name}.cs' (no write)."
                : $"Applied {edits.Count} mixed edit(s) to '{path}/{name}.cs'.";
            return Response.Success(message, data);
        }

        static List<Dictionary<string, object>> BuildComputedTextEdits(
            IEnumerable<Dictionary<string, object>> edits,
            string contents)
        {
            var computed = new List<Dictionary<string, object>>();
            foreach (var edit in edits ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                var op = (GetStringValue(edit, "op") ?? string.Empty).Trim().ToLowerInvariant();
                var text = GetStringValue(edit, "text") ??
                           GetStringValue(edit, "insert") ??
                           GetStringValue(edit, "content") ??
                           GetStringValue(edit, "replacement") ?? string.Empty;
                Dictionary<string, object> converted = op switch
                {
                    "anchor_insert" => ProcessAnchorInsert(edit, contents),
                    "anchor_delete" => ProcessAnchorDelete(edit, contents),
                    "anchor_replace" => ProcessAnchorReplace(edit, contents),
                    "replace_range" => ProcessReplaceRange(edit),
                    "regex_replace" => ProcessRegexReplace(edit, contents),
                    "prepend" => new Dictionary<string, object>
                    {
                        ["startLine"] = 1,
                        ["startCol"] = 1,
                        ["endLine"] = 1,
                        ["endCol"] = 1,
                        ["newText"] = text
                    },
                    "append" => BuildAppendTextEdit(contents, text),
                    _ => throw new InvalidOperationException($"Unsupported text edit op: {op}")
                };

                if (converted != null)
                    computed.Add(converted);
            }

            return computed;
        }

        static Dictionary<string, object> BuildAppendTextEdit(string contents, string text)
        {
            var (line, col) = GetEndOfFilePosition(contents);
            return new Dictionary<string, object>
            {
                ["startLine"] = line,
                ["startCol"] = col,
                ["endLine"] = line,
                ["endCol"] = col,
                ["newText"] = (!contents.EndsWith("\n") ? "\n" : string.Empty) + text
            };
        }

        static string ApplyComputedTextEditsLocally(
            string contents,
            IEnumerable<Dictionary<string, object>> edits)
        {
            var ranges = (edits ?? Enumerable.Empty<Dictionary<string, object>>())
                .Select(edit => new
                {
                    start = GetIndexFromLineCol(contents, GetIntValue(edit, "startLine"), GetIntValue(edit, "startCol")),
                    end = GetIndexFromLineCol(contents, GetIntValue(edit, "endLine"), GetIntValue(edit, "endCol")),
                    text = GetStringValue(edit, "newText") ?? string.Empty
                })
                .OrderByDescending(range => range.start)
                .ToArray();

            for (var index = 1; index < ranges.Length; index++)
            {
                if (ranges[index].end > ranges[index - 1].start)
                    throw new InvalidOperationException("Computed text edits overlap; use sequential calls or more specific anchors.");
            }

            var result = contents;
            foreach (var range in ranges)
                result = result.Remove(range.start, range.end - range.start).Insert(range.start, range.text);
            return result;
        }

        static int GetIndexFromLineCol(string text, int targetLine, int targetCol)
        {
            if (targetLine < 1 || targetCol < 1)
                throw new ArgumentOutOfRangeException(nameof(targetLine), "Text edit line and column are 1-based and must be positive.");

            var line = 1;
            var col = 1;
            for (var index = 0; index <= text.Length; index++)
            {
                if (line == targetLine && col == targetCol)
                    return index;
                if (index == text.Length)
                    break;

                if (text[index] == '\n')
                {
                    line++;
                    col = 1;
                }
                else if (text[index] != '\r')
                {
                    col++;
                }
            }

            throw new ArgumentOutOfRangeException(
                nameof(targetLine),
                $"Text edit position {targetLine}:{targetCol} is outside the current file.");
        }

        /// <summary>
        /// Convert structured operations to apply_text_edits format
        /// </summary>
        static object ConvertAndApplyTextEdits(string name, string path, string namespaceName, string scriptType,
            List<Dictionary<string, object>> edits, string contents, Dictionary<string, object> options)
        {
            try
            {
                var atEdits = BuildComputedTextEdits(edits, contents);

                if (!atEdits.Any())
                {
                    return Response.Error("No applicable text edit spans computed (anchor not found or zero-length).", new
                    {
                        routing = "text"
                    });
                }

                var preview = GetBoolValue(options, "preview");
                var confirm = GetBoolValue(options, "confirm", false);
                var hasRegexReplace = edits.Any(e => string.Equals(
                    GetStringValue(e, "op"),
                    "regex_replace",
                    StringComparison.OrdinalIgnoreCase));

                if (preview || (hasRegexReplace && !confirm))
                {
                    try
                    {
                        var previewText = ApplyComputedTextEditsLocally(contents, atEdits);
                        var diff = GenerateUnifiedDiff(contents, previewText);
                        var data = new
                        {
                            diff,
                            normalizedEdits = edits,
                            computedTextEdits = atEdits,
                            routing = "text"
                        };

                        return preview
                            ? Response.Success("Preview only (no write)", data)
                            : Response.Error("Preview diff; set options.confirm=true to apply.", data);
                    }
                    catch (Exception ex)
                    {
                        return Response.Error($"Preview failed: {ex.Message}", new
                        {
                            normalizedEdits = edits,
                            computedTextEdits = atEdits,
                            routing = "text"
                        });
                    }
                }

                string sha = ComputeSha256(contents);
                var managementParams = new JObject
                {
                    ["action"] = "apply_text_edits",
                    ["name"] = name,
                    ["path"] = path,
                    ["namespace"] = namespaceName,
                    ["scriptType"] = scriptType,
                    ["edits"] = JArray.FromObject(atEdits),
                    ["precondition_sha256"] = sha,
                    ["options"] = JObject.FromObject(new Dictionary<string, object>
                    {
                        ["refresh"] = GetStringValue(options, "refresh") ?? "debounced",
                        ["validate"] = GetStringValue(options, "validate") ?? "standard",
                        ["applyMode"] = atEdits.Count > 1 ? "atomic" : GetStringValue(options, "applyMode") ?? "sequential"
                    })
                };

                var result = ManageScript.HandleCommand(managementParams);

                // Enhance result with routing information
                if (result is object resultObj)
                {
                    var resultDict = GetObjectProperties(resultObj);
                    var data = resultDict.GetValueOrDefault("data") as Dictionary<string, object> ?? new Dictionary<string, object>();
                    data["normalizedEdits"] = edits;
                    data["routing"] = "text";
                    resultDict["data"] = data;
                }

                return result;
            }
            catch (Exception ex)
            {
                return Response.Error($"Text edit conversion and application failed: {ex.Message}", new
                {
                    routing = "text"
                });
            }
        }

        /// <summary>
        /// Process anchor_insert operation
        /// </summary>
        internal static Match ResolveAnchorMatch(
            Dictionary<string, object> edit,
            string contents,
            string operation)
        {
            var anchor = GetStringValue(edit, "anchor");
            if (string.IsNullOrWhiteSpace(anchor))
                throw new InvalidOperationException($"{operation} requires a non-empty anchor regex.");

            var options = RegexOptions.Multiline;
            if (GetBoolValue(edit, "ignoreCase") || GetBoolValue(edit, "ignore_case"))
                options |= RegexOptions.IgnoreCase;

            Match[] matches;
            try
            {
                matches = new Regex(anchor, options, TimeSpan.FromSeconds(2))
                    .Matches(contents)
                    .Cast<Match>()
                    .ToArray();
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InvalidOperationException($"{operation}: anchor regex timed out: {anchor}");
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"{operation}: invalid anchor regex '{anchor}': {ex.Message}");
            }

            if (matches.Length == 0)
            {
                if (GetBoolValue(edit, "allowNoop") || GetBoolValue(edit, "allow_noop"))
                    return null;
                throw new InvalidOperationException($"{operation}: anchor not found: {anchor}");
            }

            if (TryGetIntValue(edit, "matchIndex", out var matchIndex) ||
                TryGetIntValue(edit, "match_index", out matchIndex))
            {
                if (matchIndex < 0 || matchIndex >= matches.Length)
                    throw new InvalidOperationException(
                        $"{operation}: matchIndex {matchIndex} is outside 0..{matches.Length - 1} for anchor '{anchor}'.");
                return matches[matchIndex];
            }

            if (matches.Length > 1)
            {
                var hasExplicitPreference = edit.ContainsKey("preferLast") || edit.ContainsKey("prefer_last");
                var allowAmbiguous = GetBoolValue(edit, "allowAmbiguous") || GetBoolValue(edit, "allow_ambiguous");
                if (!hasExplicitPreference && !allowAmbiguous)
                {
                    throw new InvalidOperationException(
                        $"{operation}: anchor is ambiguous ({matches.Length} matches): {anchor}. " +
                        "Use a more specific regex, matchIndex, or explicit preferLast=true/false.");
                }

                var preferLast = GetBoolValue(edit, "preferLast", GetBoolValue(edit, "prefer_last", true));
                return preferLast ? matches[^1] : matches[0];
            }

            return matches[0];
        }

        static Dictionary<string, object> ProcessAnchorInsert(Dictionary<string, object> edit, string contents)
        {
            var position = GetStringValue(edit, "position")?.ToLowerInvariant() ?? "before";
            var text = GetStringValue(edit, "text") ?? GetStringValue(edit, "replacement") ?? "";
            var match = ResolveAnchorMatch(edit, contents, "anchor_insert");
            if (match == null) return null;

            int index = position == "before" ? match.Index : match.Index + match.Length;
            var (line, col) = GetLineColFromIndex(contents, index);

            // Normalize text with newlines
            if (!string.IsNullOrEmpty(text))
            {
                if (!text.StartsWith("\n"))
                    text = "\n" + text;
                if (!text.EndsWith("\n"))
                    text = text + "\n";
            }

            return new Dictionary<string, object>
            {
                ["startLine"] = line,
                ["startCol"] = col,
                ["endLine"] = line,
                ["endCol"] = col,
                ["newText"] = text
            };
        }

        /// <summary>
        /// Process anchor_delete operation
        /// </summary>
        static Dictionary<string, object> ProcessAnchorDelete(Dictionary<string, object> edit, string contents)
        {
            var match = ResolveAnchorMatch(edit, contents, "anchor_delete");
            if (match == null) return null;

            var (startLine, startCol) = GetLineColFromIndex(contents, match.Index);
            var (endLine, endCol) = GetLineColFromIndex(contents, match.Index + match.Length);

            return new Dictionary<string, object>
            {
                ["startLine"] = startLine,
                ["startCol"] = startCol,
                ["endLine"] = endLine,
                ["endCol"] = endCol,
                ["newText"] = ""
            };
        }

        /// <summary>
        /// Process anchor_replace operation
        /// </summary>
        static Dictionary<string, object> ProcessAnchorReplace(Dictionary<string, object> edit, string contents)
        {
            var replacement = GetStringValue(edit, "text") ?? GetStringValue(edit, "replacement") ?? "";
            var match = ResolveAnchorMatch(edit, contents, "anchor_replace");
            if (match == null) return null;

            var (startLine, startCol) = GetLineColFromIndex(contents, match.Index);
            var (endLine, endCol) = GetLineColFromIndex(contents, match.Index + match.Length);

            return new Dictionary<string, object>
            {
                ["startLine"] = startLine,
                ["startCol"] = startCol,
                ["endLine"] = endLine,
                ["endCol"] = endCol,
                ["newText"] = replacement
            };
        }

        /// <summary>
        /// Process replace_range operation
        /// </summary>
        static Dictionary<string, object> ProcessReplaceRange(Dictionary<string, object> edit)
        {
            return new Dictionary<string, object>
            {
                ["startLine"] = GetIntValue(edit, "startLine"),
                ["startCol"] = GetIntValue(edit, "startCol"),
                ["endLine"] = GetIntValue(edit, "endLine"),
                ["endCol"] = GetIntValue(edit, "endCol"),
                ["newText"] = GetStringValue(edit, "text") ?? ""
            };
        }

        /// <summary>
        /// Process regex_replace operation
        /// </summary>
        static Dictionary<string, object> ProcessRegexReplace(Dictionary<string, object> edit, string contents)
        {
            var pattern = GetStringValue(edit, "pattern") ?? GetStringValue(edit, "anchor");
            var replacement = GetStringValue(edit, "replacement") ?? GetStringValue(edit, "text") ?? "";
            var ignoreCase = GetBoolValue(edit, "ignoreCase");

            if (string.IsNullOrEmpty(pattern))
                return null;

            var options = RegexOptions.Multiline;
            if (ignoreCase)
                options |= RegexOptions.IgnoreCase;

            var match = AnchorMatcher.FindBestAnchorMatch(pattern, contents, options, true);
            if (match == null)
                return null;

            // Expand $1, $2... backreferences using the match groups
            var expandedReplacement = Regex.Replace(replacement, @"\$(\d+)", m =>
            {
                int groupNum = int.Parse(m.Groups[1].Value);
                return groupNum < match.Groups.Count ? (match.Groups[groupNum]?.Value ?? "") : "";
            });

            var (startLine, startCol) = GetLineColFromIndex(contents, match.Index);
            var (endLine, endCol) = GetLineColFromIndex(contents, match.Index + match.Length);

            return new Dictionary<string, object>
            {
                ["startLine"] = startLine,
                ["startCol"] = startCol,
                ["endLine"] = endLine,
                ["endCol"] = endCol,
                ["newText"] = expandedReplacement
            };
        }

        /// <summary>
        /// Best-effort normalization of script "name" and "path".
        ///
        /// Accepts any of:
        /// - name = "SmartReach", path = "Assets/Scripts/Interaction"
        /// - name = "SmartReach.cs", path = "Assets/Scripts/Interaction"
        /// - name = "Assets/Scripts/Interaction/SmartReach.cs", path = ""
        /// - path = "Assets/Scripts/Interaction/SmartReach.cs" (name empty)
        /// - name or path using uri prefixes: unity://path/..., file://...
        /// - accidental duplicates like "Assets/.../SmartReach.cs/SmartReach.cs"
        ///
        /// Returns (name_without_extension, directory_path_under_Assets).
        /// </summary>
        /// <param name="name">Script name or full path</param>
        /// <param name="path">Directory path or full path</param>
        /// <returns>Tuple of (normalized_name, normalized_path)</returns>
        static (string name, string path) NormalizeScriptLocator(string name, string path)
        {
            string n = (name ?? "").Trim();
            string p = (path ?? "").Trim();

            string StripPrefix(string s)
            {
                if (s.StartsWith("unity://path/"))
                    return s.Substring("unity://path/".Length);
                if (s.StartsWith("file://"))
                    return s.Substring("file://".Length);
                return s;
            }

            string CollapseDuplicateTail(string inputPath)
            {
                if (string.IsNullOrEmpty(inputPath))
                    return inputPath;
                var parts = inputPath.Split('/');
                if (parts.Length >= 2 && parts[parts.Length - 1] == parts[parts.Length - 2])
                    return string.Join("/", parts.Take(parts.Length - 1));
                return inputPath;
            }

            // Prefer a full path if provided in either field
            string candidate = "";
            foreach (var v in new[] { n, p })
            {
                var v2 = StripPrefix(v);
                if (v2.EndsWith(".cs") || v2.StartsWith("Assets/"))
                {
                    candidate = v2;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(candidate))
            {
                candidate = CollapseDuplicateTail(candidate);
                // If a directory was passed in path and file in name, join them
                if (!candidate.EndsWith(".cs") && n.EndsWith(".cs"))
                {
                    var v2 = StripPrefix(n);
                    candidate = candidate.TrimEnd('/') + "/" + v2.Split('/').Last();
                }
                if (candidate.EndsWith(".cs"))
                {
                    var parts = candidate.Split('/');
                    var fileName = parts[parts.Length - 1];
                    var dirPath = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : "Assets";
                    var baseName = fileName.Length > 3 && fileName.ToLowerInvariant().EndsWith(".cs") ?
                        fileName.Substring(0, fileName.Length - 3) : fileName;
                    return (baseName, dirPath);
                }
            }

            // Fall back: remove extension from name if present and return given path
            var baseName2 = n.ToLowerInvariant().EndsWith(".cs") ? n.Substring(0, n.Length - 3) : n;
            return (baseName2, string.IsNullOrEmpty(p) ? "Assets" : p);
        }

        /// <summary>
        /// Get line and column from character index (1-based)
        /// </summary>
        static (int line, int col) GetLineColFromIndex(string text, int index)
        {
            int line = 1;
            int col = 1;

            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    col = 1;
                }
                else if (text[i] != '\r') // Don't count \r in CRLF
                {
                    col++;
                }
            }

            return (line, col);
        }

        /// <summary>
        /// Get end of file position
        /// </summary>
        static (int line, int col) GetEndOfFilePosition(string contents)
        {
            return GetLineColFromIndex(contents, contents.Length);
        }

        /// <summary>
        /// Compute SHA256 hash of content
        /// </summary>
        static string ComputeSha256(string content)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                var hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        static string GetPreconditionSha256(Dictionary<string, object> options)
        {
            return GetStringValue(options, "preconditionSha256") ??
                   GetStringValue(options, "precondition_sha256");
        }

        static object ValidatePrecondition(string contents, Dictionary<string, object> options)
        {
            var expected = GetPreconditionSha256(options);
            if (string.IsNullOrWhiteSpace(expected))
                return null;

            var current = ComputeSha256(contents);
            return string.Equals(expected.Trim(), current, StringComparison.OrdinalIgnoreCase)
                ? null
                : Response.Error("stale_file", new
                {
                    status = "stale_file",
                    expected_sha256 = expected.Trim(),
                    current_sha256 = current,
                    noChangesApplied = true
                });
        }

        /// <summary>
        /// Extract string value from dictionary
        /// </summary>
        static string GetStringValue(Dictionary<string, object> dict, string key)
        {
            return dict?.GetValueOrDefault(key)?.ToString();
        }

        /// <summary>
        /// Extract integer value from dictionary
        /// </summary>
        static int GetIntValue(Dictionary<string, object> dict, string key)
        {
            if (dict?.GetValueOrDefault(key) is int intValue)
                return intValue;

            if (int.TryParse(dict?.GetValueOrDefault(key)?.ToString(), out int parsed))
                return parsed;

            return 0;
        }

        static bool TryGetIntValue(Dictionary<string, object> dict, string key, out int value)
        {
            value = 0;
            if (dict == null || !dict.TryGetValue(key, out var raw) || raw == null)
                return false;
            if (raw is int intValue)
            {
                value = intValue;
                return true;
            }
            return int.TryParse(raw.ToString(), out value);
        }

        /// <summary>
        /// Extract boolean value from dictionary
        /// </summary>
        static bool GetBoolValue(Dictionary<string, object> dict, string key, bool defaultValue = false)
        {
            if (dict?.GetValueOrDefault(key) is bool boolValue)
                return boolValue;

            if (bool.TryParse(dict?.GetValueOrDefault(key)?.ToString(), out bool parsed))
                return parsed;

            return defaultValue;
        }

        /// <summary>
        /// Check if response indicates success
        /// </summary>
        static bool IsSuccessResponse(object response)
        {
            if (response == null)
                return false;

            var props = GetObjectProperties(response);
            return props.GetValueOrDefault("success") as bool? == true;
        }

        /// <summary>
        /// Extract contents from ManageScript read result
        /// </summary>
        static string ExtractContentsFromReadResult(object result)
        {
            try
            {
                var resultDict = GetObjectProperties(result);
                var data = resultDict.GetValueOrDefault("data");

                if (data != null)
                {
                    var dataDict = GetObjectProperties(data);
                    var contents = dataDict.GetValueOrDefault("contents") as string;

                    if (!string.IsNullOrEmpty(contents))
                        return contents;

                    // Try encoded contents
                    var encodedContents = dataDict.GetValueOrDefault("encoded_contents") as string;
                    if (!string.IsNullOrEmpty(encodedContents))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(encodedContents);
                            return Encoding.UTF8.GetString(bytes);
                        }
                        catch
                        {
                            // Fall through to return null
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get properties from anonymous object or dictionary
        /// </summary>
        static Dictionary<string, object> GetObjectProperties(object obj)
        {
            if (obj is Dictionary<string, object> dict)
                return dict;

            if (obj == null)
                return new Dictionary<string, object>();

            var result = new Dictionary<string, object>();
            var type = obj.GetType();
            var properties = type.GetProperties();

            foreach (var prop in properties)
            {
                try
                {
                    result[prop.Name] = prop.GetValue(obj);
                }
                catch
                {
                    // Skip properties that can't be read
                }
            }

            return result;
        }

        /// <summary>
        /// Minimal local edit application for preview
        /// </summary>
        static string ApplyEditsLocally(string originalText, List<Dictionary<string, object>> edits)
        {
            var computed = BuildComputedTextEdits(edits, originalText);
            return ApplyComputedTextEditsLocally(originalText, computed);
        }

        /// <summary>
        /// Process anchor_insert operation for mixed processing
        /// </summary>
        static Dictionary<string, object> ProcessMixedAnchorInsert(Dictionary<string, object> edit, string baseText)
        {
            var anchor = GetStringValue(edit, "anchor") ?? "";
            var position = GetStringValue(edit, "position")?.ToLowerInvariant() ?? "after";
            var textField = GetStringValue(edit, "text") ?? GetStringValue(edit, "insert") ??
                           GetStringValue(edit, "content") ?? GetStringValue(edit, "replacement") ?? "";
            var ignoreCase = GetBoolValue(edit, "ignore_case");

            if (string.IsNullOrEmpty(anchor))
                return null;

            var flags = RegexOptions.Multiline;
            if (ignoreCase) flags |= RegexOptions.IgnoreCase;

            try
            {
                var match = AnchorMatcher.FindBestAnchorMatch(anchor, baseText, flags, true);
                if (match == null)
                    return null; // Continue processing

                int idx = position == "before" ? match.Index : match.Index + match.Length;

                // Normalize insertion to avoid jammed methods
                var textFieldNorm = textField;
                if (!string.IsNullOrEmpty(textFieldNorm))
                {
                    if (!textFieldNorm.StartsWith("\n"))
                        textFieldNorm = "\n" + textFieldNorm;
                    if (!textFieldNorm.EndsWith("\n"))
                        textFieldNorm = textFieldNorm + "\n";
                }

                var (sl, sc) = GetLineColFromIndex(baseText, idx);
                return new Dictionary<string, object>
                {
                    ["startLine"] = sl,
                    ["startCol"] = sc,
                    ["endLine"] = sl,
                    ["endCol"] = sc,
                    ["newText"] = textFieldNorm
                };
            }
            catch
            {
                return null; // Continue processing on error
            }
        }

        /// <summary>
        /// Process replace_range operation for mixed processing
        /// </summary>
        static Dictionary<string, object> ProcessMixedReplaceRange(Dictionary<string, object> edit, string textField)
        {
            var requiredKeys = new[] { "startLine", "startCol", "endLine", "endCol" };
            if (!requiredKeys.All(k => edit.ContainsKey(k)))
                return null; // Skip if missing required fields

            return new Dictionary<string, object>
            {
                ["startLine"] = GetIntValue(edit, "startLine"),
                ["startCol"] = GetIntValue(edit, "startCol"),
                ["endLine"] = GetIntValue(edit, "endLine"),
                ["endCol"] = GetIntValue(edit, "endCol"),
                ["newText"] = textField
            };
        }

        /// <summary>
        /// Process regex_replace operation for mixed processing
        /// NO confirmation logic
        /// </summary>
        static Dictionary<string, object> ProcessMixedRegexReplace(Dictionary<string, object> edit, string baseText, string textField)
        {
            var pattern = GetStringValue(edit, "pattern") ?? "";
            var ignoreCase = GetBoolValue(edit, "ignore_case");

            if (string.IsNullOrEmpty(pattern))
                return null;

            try
            {
                var flags = RegexOptions.Multiline;
                if (ignoreCase) flags |= RegexOptions.IgnoreCase;

                var regexObj = new Regex(pattern, flags);
                var match = regexObj.Match(baseText);
                if (!match.Success)
                    return null; // Continue processing if no match

                // Expand $1, $2... in replacement using this match
                var expandedReplacement = Regex.Replace(textField, @"\$(\d+)", m =>
                {
                    if (int.TryParse(m.Groups[1].Value, out int groupNum) && groupNum < match.Groups.Count)
                        return match.Groups[groupNum]?.Value ?? "";
                    return "";
                });

                var (sl, sc) = GetLineColFromIndex(baseText, match.Index);
                var (el, ec) = GetLineColFromIndex(baseText, match.Index + match.Length);

                return new Dictionary<string, object>
                {
                    ["startLine"] = sl,
                    ["startCol"] = sc,
                    ["endLine"] = el,
                    ["endCol"] = ec,
                    ["newText"] = expandedReplacement
                };
            }
            catch
            {
                return null; // Continue processing on error
            }
        }

        const int DiffContextLines = 3;
        const int MaxDiffOutputLines = 800;
        const long MaxExactDiffCells = 4_000_000;
        const int LargeDiffLookAhead = 200;

        /// <summary>
        /// Generate a compact unified diff while preserving line alignment after insertions and deletions.
        /// </summary>
        internal static string GenerateUnifiedDiff(string before, string after)
        {
            if (before == after)
            {
                return "--- before\n+++ after\n(no changes)";
            }

            var beforeLines = before.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var afterLines = after.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            var edits = (long)(beforeLines.Length + 1) * (afterLines.Length + 1) <= MaxExactDiffCells
                ? BuildExactLineEdits(beforeLines, afterLines)
                : BuildAlignedLineEdits(beforeLines, afterLines);
            var hunks = BuildDiffHunks(edits, DiffContextLines);
            if (hunks.Count == 0)
                return "--- before\n+++ after\n(no changes)";

            var diff = new List<string> { "--- before", "+++ after" };
            var truncated = false;
            foreach (var hunk in hunks)
            {
                var hunkLines = hunk.Select(edit => edit.Prefix + edit.Text).ToArray();
                var availableLines = MaxDiffOutputLines - diff.Count - 2;
                if (availableLines <= 0)
                {
                    truncated = true;
                    break;
                }

                var beforeStart = Math.Max(1, hunk[0].BeforePosition);
                var afterStart = Math.Max(1, hunk[0].AfterPosition);
                var beforeCount = hunk.Count(edit => edit.Prefix != '+');
                var afterCount = hunk.Count(edit => edit.Prefix != '-');
                diff.Add($"@@ -{beforeStart},{beforeCount} +{afterStart},{afterCount} @@");
                if (hunkLines.Length > availableLines)
                {
                    diff.AddRange(hunkLines.Take(availableLines));
                    truncated = true;
                    break;
                }
                diff.AddRange(hunkLines);
            }

            if (truncated)
                diff.Add($"... (diff truncated to {MaxDiffOutputLines} lines) ...");
            return string.Join("\n", diff);
        }

        static List<DiffLine> BuildExactLineEdits(string[] before, string[] after)
        {
            var lcs = new int[before.Length + 1, after.Length + 1];
            for (var i = before.Length - 1; i >= 0; i--)
            {
                for (var j = after.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = string.Equals(before[i], after[j], StringComparison.Ordinal)
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            var edits = new List<DiffLine>(before.Length + after.Length);
            var beforeIndex = 0;
            var afterIndex = 0;
            while (beforeIndex < before.Length && afterIndex < after.Length)
            {
                if (string.Equals(before[beforeIndex], after[afterIndex], StringComparison.Ordinal))
                {
                    edits.Add(DiffLine.Equal(before[beforeIndex], beforeIndex++, afterIndex++));
                }
                else if (lcs[beforeIndex + 1, afterIndex] >= lcs[beforeIndex, afterIndex + 1])
                {
                    edits.Add(DiffLine.Delete(before[beforeIndex], beforeIndex++, afterIndex));
                }
                else
                {
                    edits.Add(DiffLine.Add(after[afterIndex], beforeIndex, afterIndex++));
                }
            }

            while (beforeIndex < before.Length)
                edits.Add(DiffLine.Delete(before[beforeIndex], beforeIndex++, afterIndex));
            while (afterIndex < after.Length)
                edits.Add(DiffLine.Add(after[afterIndex], beforeIndex, afterIndex++));
            return edits;
        }

        static List<DiffLine> BuildAlignedLineEdits(string[] before, string[] after)
        {
            var edits = new List<DiffLine>(before.Length + after.Length);
            var beforeIndex = 0;
            var afterIndex = 0;
            while (beforeIndex < before.Length && afterIndex < after.Length)
            {
                if (string.Equals(before[beforeIndex], after[afterIndex], StringComparison.Ordinal))
                {
                    edits.Add(DiffLine.Equal(before[beforeIndex], beforeIndex++, afterIndex++));
                    continue;
                }

                if (!TryFindNextAlignedLine(before, after, beforeIndex, afterIndex, out var beforeOffset, out var afterOffset))
                    break;

                for (var i = 0; i < beforeOffset; i++)
                    edits.Add(DiffLine.Delete(before[beforeIndex], beforeIndex++, afterIndex));
                for (var i = 0; i < afterOffset; i++)
                    edits.Add(DiffLine.Add(after[afterIndex], beforeIndex, afterIndex++));
            }

            while (beforeIndex < before.Length)
                edits.Add(DiffLine.Delete(before[beforeIndex], beforeIndex++, afterIndex));
            while (afterIndex < after.Length)
                edits.Add(DiffLine.Add(after[afterIndex], beforeIndex, afterIndex++));
            return edits;
        }

        static bool TryFindNextAlignedLine(
            string[] before,
            string[] after,
            int beforeIndex,
            int afterIndex,
            out int beforeOffset,
            out int afterOffset)
        {
            beforeOffset = 0;
            afterOffset = 0;
            var beforeLimit = Math.Min(before.Length - beforeIndex - 1, LargeDiffLookAhead);
            var afterLimit = Math.Min(after.Length - afterIndex - 1, LargeDiffLookAhead);

            for (var allowBlank = 0; allowBlank <= 1; allowBlank++)
            {
                var bestDistance = int.MaxValue;
                for (var left = 0; left <= beforeLimit; left++)
                {
                    var candidate = before[beforeIndex + left];
                    if (allowBlank == 0 && string.IsNullOrWhiteSpace(candidate))
                        continue;
                    for (var right = 0; right <= afterLimit; right++)
                    {
                        if (left == 0 && right == 0)
                            continue;
                        var distance = left + right;
                        if (distance >= bestDistance || !string.Equals(candidate, after[afterIndex + right], StringComparison.Ordinal))
                            continue;
                        beforeOffset = left;
                        afterOffset = right;
                        bestDistance = distance;
                    }
                }

                if (bestDistance != int.MaxValue)
                    return true;
            }

            return false;
        }

        static List<List<DiffLine>> BuildDiffHunks(List<DiffLine> edits, int contextLines)
        {
            var hunks = new List<List<DiffLine>>();
            var index = 0;
            while (index < edits.Count)
            {
                while (index < edits.Count && edits[index].Prefix == ' ')
                    index++;
                if (index >= edits.Count)
                    break;

                var start = Math.Max(0, index - contextLines);
                var lastChange = index;
                var scan = index + 1;
                while (scan < edits.Count)
                {
                    if (edits[scan].Prefix != ' ')
                    {
                        if (scan - lastChange > (contextLines * 2) + 1)
                            break;
                        lastChange = scan;
                    }
                    scan++;
                }

                var end = Math.Min(edits.Count, lastChange + contextLines + 1);
                hunks.Add(edits.GetRange(start, end - start));
                index = end;
            }
            return hunks;
        }

        sealed class DiffLine
        {
            DiffLine(char prefix, string text, int beforePosition, int afterPosition)
            {
                Prefix = prefix;
                Text = text ?? string.Empty;
                BeforePosition = beforePosition;
                AfterPosition = afterPosition;
            }

            public char Prefix { get; }
            public string Text { get; }
            public int BeforePosition { get; }
            public int AfterPosition { get; }

            public static DiffLine Equal(string text, int beforeIndex, int afterIndex) =>
                new DiffLine(' ', text, beforeIndex + 1, afterIndex + 1);

            public static DiffLine Delete(string text, int beforeIndex, int afterIndex) =>
                new DiffLine('-', text, beforeIndex + 1, afterIndex + 1);

            public static DiffLine Add(string text, int beforeIndex, int afterIndex) =>
                new DiffLine('+', text, beforeIndex + 1, afterIndex + 1);
        }
    }
}
