#nullable disable
using System;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read-only diagnostics for UniBridge tool scheduling and recent tool execution.
    /// </summary>
    public static class ExecutionStatus
    {
        const string ToolName = "UniBridge_ExecutionStatus";

        public const string Title = "Inspect UniBridge tool execution state";

        public const string Description = @"Inspect UniBridge MCP execution scheduling without changing the project.

Use this when a tool appears to wait, timeout, or collide with another active operation. It exposes active read/exclusive work, pending scheduler slots, recent tool calls, and per-tool execution policy annotations.

Args:
    Action: Snapshot, Recent, or Policies.
    RecentLimit: Maximum recent operations to return.
    IncludeDisabled: For Policies, include tools disabled in the user's UniBridge settings.
    IncludeWorkSession: For Snapshot/Recent, include active UniBridge_WorkSession review summary.
    WorkSessionMaxChanged: Maximum changed files returned in the active WorkSession summary.

Returns:
    success, message, and scheduler/policy diagnostics.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new
                    {
                        type = "string",
                        description = "Execution diagnostic operation.",
                        @enum = new[] { "Snapshot", "Recent", "Policies" },
                        @default = "Snapshot"
                    },
                    RecentLimit = new { type = "integer", description = "Maximum recent operations to return.", @default = 20 },
                    IncludeDisabled = new { type = "boolean", description = "For Policies, include tools disabled in settings.", @default = false },
                    IncludeWorkSession = new { type = "boolean", description = "For Snapshot/Recent, include active UniBridge_WorkSession review summary.", @default = true },
                    WorkSessionMaxChanged = new { type = "integer", description = "Maximum changed files returned in the active WorkSession summary.", @default = 20 }
                },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "diagnostics", "editor" }, EnabledByDefault = true, ExecutionPolicy = ToolExecutionPolicy.Observer)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = Normalize(ReadString(parameters, "Action", "action") ?? "Snapshot");
            var recentLimit = ReadInt(parameters, 20, "RecentLimit", "recentLimit", "recent_limit", "Limit", "limit");
            var includeWorkSession = ReadBool(parameters, true, "IncludeWorkSession", "includeWorkSession", "include_work_session");
            var workSessionMaxChanged = Math.Max(1, ReadInt(parameters, 20, "WorkSessionMaxChanged", "workSessionMaxChanged", "work_session_max_changed"));

            try
            {
                return action switch
                {
                    "recent" or "history" => Response.Success("Built UniBridge execution history.", new
                    {
                        action = "Recent",
                        recent = ToolExecutionScheduler.Recent(recentLimit),
                        workSession = includeWorkSession
                            ? WorkSession.BuildCompactActiveReview(workSessionMaxChanged, includeChangedFiles: true)
                            : null
                    }),
                    "policies" or "policy" => Response.Success("Built UniBridge tool execution policy summary.", new
                    {
                        action = "Policies",
                        policies = BuildPolicies(parameters)
                    }),
                    _ => Response.Success("Built UniBridge execution scheduler snapshot.", new
                    {
                        action = "Snapshot",
                        scheduler = ToolExecutionScheduler.Snapshot(recentLimit),
                        workSession = includeWorkSession
                            ? WorkSession.BuildCompactActiveReview(workSessionMaxChanged, includeChangedFiles: true)
                            : null
                    })
                };
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to build execution status: {ex.Message}");
            }
        }

        static object BuildPolicies(JObject parameters)
        {
            var includeDisabled = ReadBool(parameters, false, "IncludeDisabled", "includeDisabled", "include_disabled");
            var entries = McpToolRegistry.GetAllToolsForSettings()
                .Where(entry => includeDisabled || entry.IsEnabled)
                .OrderBy(entry => entry.Info?.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var policies = entries.Select(entry =>
            {
                var toolName = entry.Info?.name;
                var handler = McpToolRegistry.GetTool(toolName);
                var annotation = ToolExecutionScheduler.BuildAnnotation(toolName, handler);
                return new
                {
                    name = toolName,
                    title = entry.Info?.title,
                    enabled = entry.IsEnabled,
                    enabledByDefault = entry.IsDefault,
                    groups = entry.Groups,
                    execution = annotation
                };
            }).ToArray();

            return new
            {
                total = policies.Length,
                includeDisabled,
                tools = policies
            };
        }

        static string ReadString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                    return token.ToString();
            }

            return null;
        }

        static int ReadInt(JObject obj, int fallback, params string[] keys)
        {
            var raw = ReadString(obj, keys);
            return int.TryParse(raw, out var value) ? value : fallback;
        }

        static bool ReadBool(JObject obj, bool fallback, params string[] keys)
        {
            var raw = ReadString(obj, keys);
            return bool.TryParse(raw, out var value) ? value : fallback;
        }

        static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();
        }
    }
}
