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
UniBridge, Unity, MCP, Unity Editor, ValidateScript, RefreshAssets, RequestScriptCompilationNoWait, WaitForReadyAfterReload, GetCompilationDiagnostics, ReadConsole, DiagnosticSummary, ClearConsole, console delta, post action diagnostics, batch self check, PlayMode, WaitForPlayMode, WaitForEditMode, RuntimeProfiler, RuntimeStateProbe, runtime state, state probe, runtime assert, watch assert, watch variables, component fields, MonoBehaviour state, profiler, performance, FPS, GC, memory, spikes, TypeSchema, TypeIndex, type map, type fingerprint, component schema, ScriptableObject schema, asset structure, prefab structure, serialized asset search, asset reference search, asset_ref_search, reference locations, script usages, code usages, caller scan, member callers, code member usages, member usages, serialized member usages, UnityEvent usages, AnimationEvent usages, serialized field usages, BatchActions, ToolGuide, DomainCatalog, ContextSnapshot, WorkSession, checkpoint, review changes, diff, revert, ValidateAdditiveSceneRegistration, additive scene validation.

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
                    key = "batch_self_check",
                    summary = "Run a validated multi-step batch and ask UniBridge to return only console/editor events emitted during that batch.",
                    calls = new[]
                    {
                        "UniBridge_BatchActions DryRun=true IncludeImpact=true Steps=[...]",
                        "UniBridge_BatchActions DryRun=false IncludeConsoleDelta=true IncludeEditorEventDelta=true Steps=[...]",
                        "Inspect data.postActionDiagnostics.consoleDelta totals/criticalIssues/warningIssues",
                        "Inspect data.postActionDiagnostics.editorEventDelta for project/hierarchy/compile/play-mode events"
                    }
                },
                new
                {
                    key = "type_index",
                    summary = "Build a cacheable loaded Unity/C# type map so agents can resolve component, ScriptableObject, importer, asset, and shader type names before patching.",
                    calls = new[]
                    {
                        "UniBridge_TypeSchema Action=TypeFingerprint",
                        "UniBridge_TypeSchema Action=TypeIndex Kind=MonoBehaviour Query=<name> Limit=40",
                        "UniBridge_TypeSchema Action=TypeIndex Kind=Any WriteToFile=true Limit=80",
                        "UniBridge_TypeSchema Action=Inspect TypeName=<fullName> IncludePatchExamples=true"
                    }
                },
                new
                {
                    key = "asset_reference_locations",
                    summary = "Find exact YAML locations where an asset/script GUID or a serialized script member is referenced, plus C# caller sites before script API renames.",
                    calls = new[]
                    {
                        "UniBridge_AssetIntelligence Action=ReferenceGraph Path=Assets/... IncludeReferenceLocations=true MaxReferenceLocations=20",
                        "UniBridge_AssetIntelligence Action=Impact Path=Assets/... ImpactOperation=Delete IncludeReferenceLocations=true",
                        "UniBridge_ScriptIntelligence Action=Usages Path=Assets/.../<script>.cs IncludeUsageLocations=true MaxUsageLocations=20",
                        "UniBridge_ScriptIntelligence Action=MemberUsages Path=Assets/.../<script>.cs Member=<methodOrField> MaxUsageLocations=20",
                        "UniBridge_ScriptIntelligence Action=CodeUsages Path=Assets/.../<script>.cs Member=<methodOrField> MaxReferences=80"
                    }
                },
                new
                {
                    key = "asset_structure",
                    summary = "List, search, or drill into prefab and already-loaded scene hierarchy assets with indexed paths, components, and optional serialized field matching.",
                    calls = new[]
                    {
                        "UniBridge_AssetIntelligence Action=Structure StructureMode=List Path=Assets/.../<prefab>.prefab MaxStructureDepth=4",
                        "UniBridge_AssetIntelligence Action=Structure StructureMode=Search Path=Assets/.../<prefab>.prefab Query=<nameOrField> MatchFields=all Limit=20",
                        "UniBridge_AssetIntelligence Action=Structure StructureMode=Read Path=Assets/.../<prefab>.prefab ObjectPath=<indexedPath> IncludeSerializedProperties=true"
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
                },
                new
                {
                    key = "runtime_profiler",
                    summary = "Inspect live runtime/editor state and capture a bounded ProfilerRecorder sample for frame time, GC, memory, rendering, physics, and spike hints.",
                    calls = new[]
                    {
                        "UniBridge_ManageEditor Action=GetPlayModeState",
                        "UniBridge_RuntimeProfiler Action=Snapshot",
                        "UniBridge_RuntimeProfiler Action=Metrics",
                        "UniBridge_RuntimeProfiler Action=Sample SampleFrames=120 Metrics=[main_thread_ms,gc_alloc_bytes,batches_count]",
                        "UniBridge_ReadConsole Action=DiagnosticSummary"
                    }
                },
                new
                {
                    key = "runtime_state_probe",
                    summary = "Read, sample, or assert live GameObject/component members over several frames without arbitrary C# execution.",
                    calls = new[]
                    {
                        "UniBridge_ManageEditor Action=GetPlayModeState",
                        "UniBridge_RuntimeStateProbe Action=ListMembers Component=<MonoBehaviourOrComponent>",
                        "UniBridge_RuntimeStateProbe Action=Snapshot Target=<objectPathOrId> Component=<component> Members=[fieldOrProperty]",
                        "UniBridge_RuntimeStateProbe Action=Sample Target=<objectPathOrId> Component=<component> Members=[fieldOrProperty] SampleFrames=30",
                        "UniBridge_RuntimeStateProbe Action=Assert Target=<objectPathOrId> Component=<component> Assertions=[{member:'field',operator:'==',value:true}]",
                        "UniBridge_ReadConsole Action=DiagnosticSummary"
                    }
                }
            };
        }

        static object BuildAliases(string query, int limit)
        {
            var normalizedQuery = query?.Trim();
            var aliases = BatchActionToolCatalog.ToolAliases
                .Where(pair => string.IsNullOrWhiteSpace(normalizedQuery) ||
                               QueryMatches(normalizedQuery, pair.Key, pair.Value))
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
                                QueryMatches(
                                    normalizedQuery,
                                    entry.Info?.name,
                                    entry.Info?.title,
                                    entry.Info?.description,
                                    string.Join(" ", BatchActionToolCatalog.GetAliasesForTool(entry.Info?.name))))
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

        static bool QueryMatches(string query, params string[] fields)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;

            var haystack = string.Join(" ", fields.Where(field => !string.IsNullOrWhiteSpace(field)));
            if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            var tokens = query
                .Split(new[] { ' ', '\t', '\r', '\n', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim())
                .Where(token => token.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (tokens.Length == 0)
                return true;

            return tokens.All(token => haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
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
