#nullable disable
using System;
using System.IO;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Stable discovery/ping entry point for new agents.
    /// </summary>
    public static class Discover
    {
        const string ToolName = "UniBridge_Discover";

        public const string Title = "Discover UniBridge Unity MCP tools";

        public const string Description = @"Ping and discover UniBridge Unity MCP tools, workflows, aliases, and health.

Search aliases for Codex/tool_search discoverability:
UniBridge, Unity, MCP, Unity Editor, ValidateScript, RefreshAssets, RequestScriptCompilationNoWait, WaitForReadyAfterReload, GetCompilationDiagnostics, ReadConsole, DiagnosticSummary, ClearConsole, PlayMode, WaitForPlayMode, WaitForEditMode, BatchActions, ToolGuide, DomainCatalog, ContextSnapshot, WorkSession, checkpoint, review changes, diff, revert, ValidateAdditiveSceneRegistration, additive scene validation.

Use this first when a Codex agent is unsure whether UniBridge is connected or which Unity workflow to run. This tool is read-only.";

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "guide", "editor", "diagnostics" }, EnabledByDefault = true, ExecutionPolicy = ToolExecutionPolicy.ReadOnly)]
        public static object HandleCommand(DiscoverParams parameters)
        {
            parameters ??= new DiscoverParams();

            var action = Normalize(parameters.Action, "ping");
            var limit = Math.Max(1, Math.Min(300, parameters.Limit ?? 48));

            try
            {
                return action switch
                {
                    "aliases" or "alias" => Response.Success("Listed UniBridge searchable aliases.", BuildAliases(parameters.Query, limit)),
                    "tools" or "tool" => Response.Success("Listed UniBridge tools.", BuildTools(parameters.Query, limit)),
                    "workflows" or "workflow" => Response.Success("Listed UniBridge recommended workflows.", BuildWorkflows()),
                    "status" => Response.Success("Built UniBridge discovery status.", BuildStatus(parameters.IncludeTools, limit)),
                    _ => Response.Success("UniBridge is reachable from this Unity Editor session.", BuildPing(parameters.IncludeTools, limit))
                };
            }
            catch (Exception ex)
            {
                return Response.Error($"UniBridge discovery failed: {ex.Message}");
            }
        }

        static object BuildPing(bool includeTools, int limit)
        {
            return new
            {
                tool = ToolName,
                package = PackageInfo(),
                unity = new
                {
                    version = Application.unityVersion,
                    projectPath = ProjectRoot()
                },
                connected = true,
                discoverability = new
                {
                    primaryTool = ToolName,
                    guideTool = "UniBridge_ToolGuide",
                    catalogTool = "UniBridge_DomainCatalog",
                    note = "If Codex tool_search returns zero UniBridge tools, restart the Codex/AI agent or MCP client after adding the UniBridge MCP server configuration. Restarting Unity alone is not enough for the agent to index new tools."
                },
                coreWorkflows = BuildWorkflows(),
                tools = includeTools ? BuildTools(null, limit) : null
            };
        }

        static object BuildStatus(bool includeTools, int limit)
        {
            var entries = McpToolRegistry.GetAllToolsForSettings();
            return new
            {
                package = PackageInfo(),
                unity = new
                {
                    version = Application.unityVersion,
                    projectPath = ProjectRoot(),
                    isPlaying = Application.isPlaying
                },
                toolRegistry = new
                {
                    total = entries.Length,
                    enabled = entries.Count(entry => entry.IsEnabled),
                    disabled = entries.Count(entry => !entry.IsEnabled),
                    discoverToolPresent = entries.Any(entry => string.Equals(entry.Info?.name, ToolName, StringComparison.Ordinal))
                },
                tools = includeTools ? BuildTools(null, limit) : null
            };
        }

        static object BuildWorkflows()
        {
            return new[]
            {
                new
                {
                    key = "work_session_review",
                    summary = "Start a checkpoint before AI work, let mutating batches append active review data, inspect text diffs, and optionally revert selected paths.",
                    calls = new[]
                    {
                        "UniBridge_WorkSession Action=Begin Name=<task>",
                        "Run normal UniBridge tools for the task; UniBridge_BatchActions DryRun=false appends data.workSessionReview by default",
                        "UniBridge_ExecutionStatus Action=Snapshot to see scheduler state plus active WorkSession summary",
                        "UniBridge_WorkSession Action=Review",
                        "UniBridge_WorkSession Action=Diff Paths=[Assets/...]",
                        "UniBridge_WorkSession Action=Revert DryRun=true Paths=[Assets/...]",
                        "UniBridge_WorkSession Action=End"
                    }
                },
                new
                {
                    key = "compile_diagnostics",
                    summary = "Validate scripts, refresh assets, queue compilation without inline reload wait, then reconnect/wait and read diagnostics.",
                    calls = new[]
                    {
                        "UniBridge_ValidateScript IncludeDiagnostics=true",
                        "UniBridge_ManageEditor Action=RefreshAssets WaitForCompletion=true",
                        "UniBridge_ManageEditor Action=RequestScriptCompilationNoWait Force=true",
                        "UniBridge_ManageEditor Action=WaitForReadyAfterReload",
                        "UniBridge_ManageEditor Action=GetCompilationDiagnostics",
                        "UniBridge_ReadConsole Action=DiagnosticSummary"
                    }
                },
                new
                {
                    key = "play_mode_boundary",
                    summary = "Clear console, queue Play/ExitPlayMode as reload-safe boundaries, then wait after reconnect and read console diagnostics.",
                    calls = new[]
                    {
                        "UniBridge_ReadConsole Action=ClearConsole",
                        "UniBridge_ManageEditor Action=Play WaitForCompletion=true",
                        "UniBridge_ManageEditor Action=WaitForPlayMode",
                        "UniBridge_ManageEditor Action=WaitForReady RequireNotPlaying=false",
                        "UniBridge_ReadConsole Action=DiagnosticSummary",
                        "UniBridge_ManageEditor Action=ExitPlayMode WaitForCompletion=true",
                        "UniBridge_ManageEditor Action=WaitForEditMode",
                        "UniBridge_ManageEditor Action=WaitForReady RequireNotPlaying=true",
                        "UniBridge_ReadConsole Action=DiagnosticSummary"
                    }
                },
                new
                {
                    key = "additive_scene_validation",
                    summary = "Validate a cloned/additive scene registration without changing the project.",
                    calls = new[]
                    {
                        "UniBridge_ValidateAdditiveSceneRegistration ScenePath=Assets/.../darkness12.unity",
                        "UniBridge_BatchActions DryRun=true steps=[validate_additive_scene, editor RefreshAssets, editor GetCompilationDiagnostics, console DiagnosticSummary]"
                    }
                }
            };
        }

        static object BuildAliases(string query, int limit)
        {
            var normalizedQuery = query?.Trim();
            var aliases = BatchActionToolCatalog.ToolAliases
                .Where(pair => string.IsNullOrWhiteSpace(normalizedQuery) ||
                               pair.Key.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               pair.Value.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(pair => new { alias = pair.Key, tool = pair.Value })
                .ToArray();

            return new
            {
                query = normalizedQuery,
                returned = aliases.Length,
                aliases
            };
        }

        static object BuildTools(string query, int limit)
        {
            var normalizedQuery = query?.Trim();
            var tools = McpToolRegistry.GetAllToolsForSettings()
                .Where(entry => string.IsNullOrWhiteSpace(normalizedQuery) ||
                                (entry.Info?.name?.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                (entry.Info?.title?.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                (entry.Info?.description?.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                BatchActionToolCatalog.GetAliasesForTool(entry.Info?.name).Any(alias => alias.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(entry => entry.Info?.name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(entry => new
                {
                    name = entry.Info?.name,
                    title = entry.Info?.title,
                    enabled = entry.IsEnabled,
                    enabledByDefault = entry.IsDefault,
                    groups = entry.Groups,
                    batchAllowed = BatchActionToolCatalog.IsAllowed(entry.Info?.name),
                    aliases = BatchActionToolCatalog.GetAliasesForTool(entry.Info?.name)
                })
                .ToArray();

            return new
            {
                query = normalizedQuery,
                returned = tools.Length,
                tools
            };
        }

        static object PackageInfo()
        {
            var version = "unknown";
            var displayName = "UniBridge";
            try
            {
                var packagePath = Path.Combine(ProjectRoot(), "Packages", "com.cidonix.unibridge", "package.json");
                if (File.Exists(packagePath))
                {
                    var json = JObject.Parse(File.ReadAllText(packagePath));
                    version = json.Value<string>("version") ?? version;
                    displayName = json.Value<string>("displayName") ?? displayName;
                }
            }
            catch
            {
                // Keep discovery robust even if package metadata is temporarily unreadable.
            }

            return new
            {
                name = "com.cidonix.unibridge",
                displayName,
                version,
                relayConfigHint = "Use the UniBridge MCP server for this Unity project, then restart the AI agent/MCP client so tool_search can index the tools."
            };
        }

        static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
        }

        static string Normalize(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            return value.Trim().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }
    }
}
