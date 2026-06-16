#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Agent-facing catalog that maps Unity work domains to relevant UniBridge tools and Unity types.
    /// </summary>
    public static class DomainCatalog
    {
        const string ToolName = "UniBridge_DomainCatalog";
        const int DefaultLimit = 64;
        const int MaxLimit = 300;

        public const string Title = "Catalog Unity authoring domains";

public const string Description = @"Return an agent-facing catalog of Unity domains, common component/asset types, and the UniBridge tools that author or inspect each area.

Use this as a first call when a new agent knows the task domain but not the exact UniBridge tool. It is read-only and optimized for tool discovery, workflow order, and domain-specific type hints.

Search aliases: UniBridge Unity MCP DomainCatalog agent playbook read before modify verification ladder risk controls WorkSession checkpoint review changes diff revert rollback ValidateScript RefreshAssets RequestScriptCompilationNoWait WaitForReadyAfterReload GetCompilationDiagnostics ReadConsole DiagnosticSummary ClearConsole console delta post action diagnostics batch self check PlayMode WaitForPlayMode WaitForEditMode RuntimeProfiler RuntimeStateProbe runtime state state probe runtime assert watch assert watch variables component fields MonoBehaviour state profiler profiler hierarchy marker hierarchy frame export top markers performance FPS GC memory spikes TypeSchema TypeIndex type map type fingerprint component schema ScriptableObject schema asset structure prefab structure serialized asset search asset reference search asset_ref_search reference locations script usages code usages caller scan member callers code member usages member usages serialized member usages UnityEvent usages AnimationEvent usages serialized field usages ValidateAdditiveSceneRegistration additive scene validation scenesManager BuildSettings.

Args:
    Action: Overview, ListDomains, InspectDomain, ListTypes, or SuggestTools.
    Domain: Optional domain key such as RuntimeDebug, Physics3D, Navigation, Rendering, UIToolkit, UI, Physics2D, Animation, Timeline, Assets, Scripts.
    Query: Optional text filter for domains, types, or tools.
    Limit: Max types/domains returned.
    IncludeTypes: Include curated/runtime Unity types for each domain.
    IncludeTools: Include recommended UniBridge tools for each domain.

Returns:
    success, message, and data with domains, recommended first/edit/verify tools, type summaries, and available batch aliases.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new { type = "string", @enum = new[] { "Overview", "ListDomains", "InspectDomain", "ListTypes", "SuggestTools" } },
                    Domain = new { type = "string", description = "Domain key or alias, e.g. Physics3D, Navigation, Rendering, UIToolkit." },
                    Query = new { type = "string", description = "Filter domains, type names, or tools." },
                    Limit = new { type = "integer", minimum = 1, maximum = MaxLimit },
                    IncludeTypes = new { type = "boolean" },
                    IncludeTools = new { type = "boolean" }
                },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "guide", "schema", "editor" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = Normalize(GetString(parameters, "Action", "action") ?? "Overview");
            try
            {
                return action switch
                {
                    "overview" => Overview(parameters),
                    "listdomains" or "domains" or "list" => ListDomains(parameters),
                    "inspectdomain" or "inspect" or "domain" => InspectDomain(parameters),
                    "listtypes" or "types" => ListTypes(parameters),
                    "suggesttools" or "tools" or "suggest" => SuggestTools(parameters),
                    _ => Response.Error($"Unknown DomainCatalog action '{GetString(parameters, "Action", "action")}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DomainCatalog] Action '{action}' failed: {ex}");
                return Response.Error($"DomainCatalog action '{action}' failed: {ex.Message}");
            }
        }

        internal static string[] GetDomainTagsForType(Type type)
        {
            if (type == null)
                return Array.Empty<string>();

            return GetDomains()
                .Where(domain => domain.TypeNames.Any(typeName => MatchesType(type, typeName)))
                .Select(domain => domain.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static object Overview(JObject parameters)
        {
            var includeTypes = GetBool(parameters, false, "IncludeTypes", "includeTypes", "include_types");
            var includeTools = GetBool(parameters, true, "IncludeTools", "includeTools", "include_tools");
            var domains = FilterDomains(parameters)
                .Select(domain => ToDomainSummary(domain, includeTypes, includeTools, GetLimit(parameters)))
                .ToArray();

            return Response.Success("Built Unity domain catalog overview.", new
            {
                action = "Overview",
                recommendedStart = new[]
                {
                    new { step = 1, tool = ToolName, why = "Pick the Unity work domain and its recommended tool sequence." },
                    new { step = 2, tool = "UniBridge_ToolGuide", why = "Open the workflow guide for the selected topic." },
                    new { step = 3, tool = "UniBridge_ToolGuide Workflow=agent_playbook", why = "Read the shared AI operating protocol before broad or risky edits." },
                    new { step = 4, tool = "UniBridge_UnitySearch", why = "Resolve concrete scene objects, assets, scripts, or shaders before editing." },
                    new { step = 5, tool = "UniBridge_TypeSchema", why = "Inspect exact writable properties and use TypeIndex/TypeFingerprint for loaded type lookup before low-level patches." }
                },
                globalRiskControls = BuildGlobalRiskControls(),
                domainCount = domains.Length,
                domains,
                allDomains = GetDomains().Select(domain => domain.Key).ToArray()
            });
        }

        static object ListDomains(JObject parameters)
        {
            var includeTypes = GetBool(parameters, false, "IncludeTypes", "includeTypes", "include_types");
            var includeTools = GetBool(parameters, true, "IncludeTools", "includeTools", "include_tools");
            var limit = GetLimit(parameters);
            var domains = FilterDomains(parameters)
                .Take(limit)
                .Select(domain => ToDomainSummary(domain, includeTypes, includeTools, limit))
                .ToArray();

            return Response.Success($"Listed {domains.Length} Unity domain(s).", new
            {
                action = "ListDomains",
                query = GetString(parameters, "Query", "query"),
                returned = domains.Length,
                domains
            });
        }

        static object InspectDomain(JObject parameters)
        {
            var domain = ResolveDomain(parameters);
            if (domain == null)
            {
                return Response.Error("InspectDomain requires a valid Domain.", new
                {
                    availableDomains = GetDomains().Select(item => item.Key).ToArray()
                });
            }

            var limit = GetLimit(parameters);
            return Response.Success($"Inspected domain '{domain.Key}'.", ToDomainDetail(domain, limit));
        }

        static object ListTypes(JObject parameters)
        {
            var domain = ResolveDomain(parameters);
            var limit = GetLimit(parameters);
            var query = GetString(parameters, "Query", "query");
            var domains = domain != null ? new[] { domain } : FilterDomains(parameters).ToArray();
            var types = domains
                .SelectMany(item => item.TypeNames.Select(typeName => new { domain = item.Key, typeName }))
                .Select(item => new { item.domain, type = ResolveType(item.typeName), requestedType = item.typeName })
                .Where(item => item.type != null)
                .Where(item => string.IsNullOrWhiteSpace(query) || MatchesQuery(item.type, query))
                .GroupBy(item => item.type.FullName, StringComparer.Ordinal)
                .Select(group => new { type = group.First().type, domains = group.Select(item => item.domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() })
                .OrderBy(item => item.type.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(item => ToTypeSummary(item.type, item.domains))
                .ToArray();

            return Response.Success($"Listed {types.Length} type(s).", new
            {
                action = "ListTypes",
                domain = domain?.Key,
                query,
                returned = types.Length,
                types
            });
        }

        static object SuggestTools(JObject parameters)
        {
            var domain = ResolveDomain(parameters);
            var query = GetString(parameters, "Query", "query");
            var domains = domain != null ? new[] { domain } : FilterDomains(parameters).ToArray();
            var tools = domains
                .SelectMany(item => item.AuthoringTools.Concat(item.InspectionTools).Concat(item.VerificationTools).Concat(item.CaptureTools)
                    .Select(tool => new { domain = item.Key, tool }))
                .Where(item => string.IsNullOrWhiteSpace(query) || item.tool.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || item.domain.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(item => item.tool, StringComparer.Ordinal)
                .Select(group => new
                {
                    tool = group.Key,
                    domains = group.Select(item => item.domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    batchAllowed = BatchActionToolCatalog.IsAllowed(group.Key),
                    aliases = BatchActionToolCatalog.GetAliasesForTool(group.Key)
                })
                .OrderBy(item => item.tool, StringComparer.OrdinalIgnoreCase)
                .Take(GetLimit(parameters))
                .ToArray();

            return Response.Success($"Suggested {tools.Length} tool(s).", new
            {
                action = "SuggestTools",
                domain = domain?.Key,
                query,
                returned = tools.Length,
                tools
            });
        }

        static IEnumerable<DomainDefinition> FilterDomains(JObject parameters)
        {
            var query = GetString(parameters, "Query", "query") ?? GetString(parameters, "Domain", "domain");
            var domains = GetDomains();
            if (string.IsNullOrWhiteSpace(query))
                return domains;

            var normalized = Normalize(query);
            return domains.Where(domain =>
                Normalize(domain.Key).Contains(normalized) ||
                Normalize(domain.Title).Contains(normalized) ||
                domain.Aliases.Any(alias => Normalize(alias).Contains(normalized)) ||
                domain.AuthoringTools.Concat(domain.InspectionTools).Concat(domain.VerificationTools).Any(tool => Normalize(tool).Contains(normalized)) ||
                domain.TypeNames.Any(type => Normalize(type).Contains(normalized)));
        }

        static DomainDefinition ResolveDomain(JObject parameters)
        {
            var domainInput = GetString(parameters, "Domain", "domain", "Topic", "topic", "Profile", "profile");
            if (string.IsNullOrWhiteSpace(domainInput))
                return null;

            var normalized = Normalize(domainInput);
            return GetDomains().FirstOrDefault(domain =>
                Normalize(domain.Key) == normalized ||
                Normalize(domain.Title) == normalized ||
                domain.Aliases.Any(alias => Normalize(alias) == normalized));
        }

        static object ToDomainSummary(DomainDefinition domain, bool includeTypes, bool includeTools, int limit)
        {
            return new
            {
                domain = domain.Key,
                title = domain.Title,
                when = domain.When,
                primaryTool = domain.AuthoringTools.FirstOrDefault() ?? domain.InspectionTools.FirstOrDefault(),
                authoringTools = includeTools ? domain.AuthoringTools : null,
                inspectionTools = includeTools ? domain.InspectionTools : null,
                verificationTools = includeTools ? domain.VerificationTools : null,
                riskControls = BuildDomainRiskControls(domain, compact: true),
                aliases = domain.Aliases,
                typeHints = includeTypes ? BuildTypeSummaries(domain, limit) : null
            };
        }

        static object ToDomainDetail(DomainDefinition domain, int limit)
        {
            return new
            {
                domain = domain.Key,
                title = domain.Title,
                when = domain.When,
                firstCalls = domain.FirstCalls,
                authoringTools = domain.AuthoringTools.Select(ToToolHint).ToArray(),
                inspectionTools = domain.InspectionTools.Select(ToToolHint).ToArray(),
                verificationTools = domain.VerificationTools.Select(ToToolHint).ToArray(),
                captureTools = domain.CaptureTools.Select(ToToolHint).ToArray(),
                batchAliases = domain.AuthoringTools.Concat(domain.InspectionTools).Concat(domain.VerificationTools)
                    .SelectMany(BatchActionToolCatalog.GetAliasesForTool)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                riskControls = BuildDomainRiskControls(domain, compact: false),
                typeHints = BuildTypeSummaries(domain, limit),
                notes = domain.Notes,
                aliases = domain.Aliases
            };
        }

        static object ToToolHint(string tool)
        {
            return new
            {
                tool,
                batchAllowed = BatchActionToolCatalog.IsAllowed(tool),
                aliases = BatchActionToolCatalog.GetAliasesForTool(tool)
            };
        }

        static object BuildGlobalRiskControls()
        {
            return new
            {
                readBeforeModify = new[]
                {
                    "Resolve exact target identity before writes: objectId/indexedPath for scene objects, GUID/path for assets, full type names for components.",
                    "Inspect references before rename/move/delete or serialized API changes.",
                    "Check Prefab Stage, dirty scenes, Play Mode state, and console diagnostics before broad edits."
                },
                executeSafely = new[]
                {
                    "Use WorkSession for broad tasks and BatchActions DryRun=true for multi-step edits.",
                    "Use ScopedEdit for scene/prefab-specific work that should restore prior editor state.",
                    "Prefer reload-safe editor workflows around refresh, compilation, and Play Mode."
                },
                verify = new[]
                {
                    "Read compilation diagnostics and console summary.",
                    "Inspect the touched domain again.",
                    "Capture/audit visible work or probe runtime state when the result is visual or behavioral."
                }
            };
        }

        static object BuildDomainRiskControls(DomainDefinition domain, bool compact)
        {
            var commonBefore = new List<string>
            {
                "Resolve target identity and current state before editing.",
                "Use TypeSchema/DomainCatalog hints when component or asset type names may be ambiguous."
            };
            var commonExecute = new List<string>
            {
                "Use BatchActions DryRun=true for multi-step domain changes."
            };
            var commonVerify = new List<string>
            {
                "ReadConsole DiagnosticSummary after edits."
            };
            var redFlags = new List<string>();

            switch (domain.Key)
            {
                case "LargeScenes":
                    commonBefore.Add("Export full hierarchy to file and use objectId/indexedPath before reparenting or sorting.");
                    commonExecute.Add("Use ManageSceneHierarchy dry-run and object-count validation before execution.");
                    commonVerify.Add("Re-export or compare hierarchy after changes.");
                    redFlags.Add("Duplicate object names or truncated snapshots make name-only edits unsafe.");
                    break;
                case "Scripts":
                    commonBefore.Add("Run Usages/MemberUsages/CodeUsages/ChangeImpact before deleting, renaming, or changing public/serialized members.");
                    commonExecute.Add("Use SHA/precondition script edits instead of BatchActions for text mutation.");
                    commonVerify.Add("ValidateScript, RefreshAssets, RequestScriptCompilationNoWait, WaitForReadyAfterReload, GetCompilationDiagnostics.");
                    redFlags.Add("Serialized UnityEvent/AnimationEvent/field references can break without C# compile errors.");
                    break;
                case "Assets":
                    commonBefore.Add("Use ReferenceGraph/Impact with IncludeReferenceLocations=true before move, rename, delete, or importer changes.");
                    commonExecute.Add("Use asset snapshots or BatchActions rollback for importer/material/data changes.");
                    commonVerify.Add("Inspect importer/asset context and refresh reference graph when asset deltas occurred.");
                    redFlags.Add("Asset paths can be stale; resolve by GUID or ResolveMissing before changing files.");
                    break;
                case "Rendering":
                case "UI":
                case "UIToolkit":
                case "VFX":
                    commonBefore.Add("Inspect layout/render/component state before changing visible objects.");
                    commonExecute.Add("Keep visual edits scoped and capture after each meaningful batch.");
                    commonVerify.Add("Use CaptureView/CaptureAsset/CaptureUIToolkit and VisualSceneAudit.");
                    redFlags.Add("A clean console does not prove visual quality; capture or audit visible output.");
                    break;
                case "RuntimeDebug":
                    commonBefore.Add("Check Play Mode state and list readable members/metrics before sampling.");
                    commonExecute.Add("Use runtime probes as read-only gates; do not infer gameplay state from static scene data alone.");
                    commonVerify.Add("Use RuntimeStateProbe assertions or RuntimeProfiler samples plus console diagnostics.");
                    redFlags.Add("Runtime state disappears when exiting Play Mode; distinguish runtime-only changes from asset/scene changes.");
                    break;
                case "EditorOps":
                    commonBefore.Add("Check editor readiness, compilation/import state, and current selection before lifecycle actions.");
                    commonExecute.Add("Use reload-safe no-wait plus wait workflows across refresh, compile, and Play Mode boundaries.");
                    commonVerify.Add("Use EditorEvents deltas and GetCompilationDiagnostics after lifecycle actions.");
                    redFlags.Add("Domain reload may recreate the bridge; do not treat a reload boundary as a normal in-process wait.");
                    break;
                case "Safety":
                    commonBefore.Add("Start or inspect WorkSession before broad changes.");
                    commonExecute.Add("Dry-run selective reverts before executing them.");
                    commonVerify.Add("Review/Diff changed paths before final reporting.");
                    redFlags.Add("Rollback protects one batch; WorkSession explains the whole agent work window.");
                    break;
                default:
                    commonVerify.Add("Use the domain's inspection tool to verify serialized state.");
                    break;
            }

            if (compact)
            {
                return new
                {
                    beforeEditing = commonBefore.Take(3).ToArray(),
                    executeSafely = commonExecute.Take(2).ToArray(),
                    verify = commonVerify.Take(2).ToArray(),
                    redFlags = redFlags.Take(1).ToArray()
                };
            }

            return new
            {
                beforeEditing = commonBefore.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                executeSafely = commonExecute.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                verify = commonVerify.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                redFlags = redFlags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        static object[] BuildTypeSummaries(DomainDefinition domain, int limit)
        {
            return domain.TypeNames
                .Select(ResolveType)
                .Where(type => type != null)
                .Distinct()
                .OrderBy(type => type.FullName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(type => ToTypeSummary(type, new[] { domain.Key }))
                .ToArray();
        }

        static object ToTypeSummary(Type type, string[] domains)
        {
            return new
            {
                name = type.Name,
                fullName = type.FullName,
                assembly = type.Assembly.GetName().Name,
                baseType = type.BaseType?.FullName,
                domains,
                isComponent = typeof(Component).IsAssignableFrom(type),
                isScriptableObject = typeof(ScriptableObject).IsAssignableFrom(type),
                isAsset = typeof(UnityEngine.Object).IsAssignableFrom(type) && !typeof(Component).IsAssignableFrom(type),
                obsolete = type.GetCustomAttributes(typeof(ObsoleteAttribute), true).OfType<ObsoleteAttribute>().FirstOrDefault()?.Message
            };
        }

        static Type ResolveType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            var direct = Type.GetType(typeName, false);
            if (direct != null)
                return direct;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).ToArray();
                }
                catch
                {
                    continue;
                }

                var match = types.FirstOrDefault(type =>
                    string.Equals(type.FullName, typeName, StringComparison.Ordinal) ||
                    string.Equals(type.Name, typeName, StringComparison.Ordinal) ||
                    string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }

            return null;
        }

        static bool MatchesType(Type type, string typeName)
        {
            return string.Equals(type.FullName, typeName, StringComparison.Ordinal) ||
                   string.Equals(type.Name, typeName, StringComparison.Ordinal) ||
                   string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase);
        }

        static bool MatchesQuery(Type type, string query)
        {
            return string.IsNullOrWhiteSpace(query) ||
                   (type.Name?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                   (type.FullName?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                   type.Assembly.GetName().Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static DomainDefinition[] GetDomains()
        {
            return new[]
            {
                new DomainDefinition
                {
                    Key = "Physics3D",
                    Title = "3D physics authoring",
                    When = "Use for Rigidbody, Collider, Joint, CharacterController, and PhysicsMaterial setup.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=Physics3D", "UniBridge_UnitySearch Sources=[SceneObjects,Assets]", "UniBridge_SceneObjectView Profile=Physics3D" },
                    AuthoringTools = new[] { "UniBridge_ManagePhysics3D", "UniBridge_ManageGameObject", "UniBridge_ManageAsset" },
                    InspectionTools = new[] { "UniBridge_SceneObjectView", "UniBridge_TypeSchema" },
                    VerificationTools = new[] { "UniBridge_ManagePhysics3D", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureView" },
                    TypeNames = new[] { "UnityEngine.Rigidbody", "UnityEngine.BoxCollider", "UnityEngine.SphereCollider", "UnityEngine.CapsuleCollider", "UnityEngine.MeshCollider", "UnityEngine.CharacterController", "UnityEngine.FixedJoint", "UnityEngine.HingeJoint", "UnityEngine.SpringJoint", "UnityEngine.ConfigurableJoint", "UnityEngine.PhysicsMaterial", "UnityEngine.PhysicMaterial" },
                    Notes = new[] { "Use named presets for fast prototypes, then inspect exact mass/collider/joint values.", "MeshCollider convex state matters for dynamic bodies." },
                    Aliases = new[] { "physics3d", "3d_physics", "rigidbody", "collider3d", "joint3d" }
                },
                new DomainDefinition
                {
                    Key = "Navigation",
                    Title = "Navigation and NavMesh authoring",
                    When = "Use for NavMeshAgent, NavMeshObstacle, OffMeshLink, and optional AI Navigation surface/modifier components.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=Navigation", "UniBridge_UnitySearch Sources=[SceneObjects]", "UniBridge_SceneObjectView Profile=Navigation" },
                    AuthoringTools = new[] { "UniBridge_ManageNavigation", "UniBridge_ManageGameObject" },
                    InspectionTools = new[] { "UniBridge_SceneObjectView", "UniBridge_TypeSchema" },
                    VerificationTools = new[] { "UniBridge_ManageNavigation", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureView" },
                    TypeNames = new[] { "UnityEngine.AI.NavMeshAgent", "UnityEngine.AI.NavMeshObstacle", "UnityEngine.AI.OffMeshLink", "Unity.AI.Navigation.NavMeshSurface", "Unity.AI.Navigation.NavMeshModifier", "Unity.AI.Navigation.NavMeshModifierVolume", "Unity.AI.Navigation.NavMeshLink" },
                    Notes = new[] { "Optional AI Navigation package types are discovered reflectively so projects without the package still compile.", "Bake/clear surface actions return a clear unavailable result when the package is absent." },
                    Aliases = new[] { "nav", "navmesh", "navigation", "ai" }
                },
                new DomainDefinition
                {
                    Key = "EditorOps",
                    Title = "Editor lifecycle, diagnostics, and live deltas",
                    When = "Use for selection context, compilation/package diagnostics, asset import/delete/move deltas, play mode, save/refresh, and readiness checks.",
                    FirstCalls = new[] { "UniBridge_EditorEvents Action=Snapshot IncludeSelection=true IncludeDiagnostics=true IncludeAssetChanges=true", "UniBridge_ManageEditor Action=GetState", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    AuthoringTools = new[] { "UniBridge_ManageEditor", "UniBridge_EditorSnapshot" },
                    InspectionTools = new[] { "UniBridge_EditorEvents", "UniBridge_ContextSnapshot", "UniBridge_EditorSnapshot", "UniBridge_ReadConsole" },
                    VerificationTools = new[] { "UniBridge_EditorEvents", "UniBridge_ManageEditor", "UniBridge_EditorSnapshot", "UniBridge_ReadConsole" },
                    CaptureTools = Array.Empty<string>(),
                    TypeNames = new[] { "UnityEditor.EditorApplication", "UnityEditor.Compilation.CompilationPipeline", "UnityEditor.AssetPostprocessor", "UnityEditor.Selection" },
                    Notes = new[] { "Use RequestScriptCompilationNoWait followed by WaitForReadyAfterReload for compile workflows; Unity assembly reload can recreate the bridge during inline waits.", "Use RequestPlayModeNoWait or Play as a reload-safe boundary, then call WaitForPlayMode/WaitForReady/ReadConsole after reconnect; do not rely on one in-process batch spanning Play Mode domain reload.", "Use latestId from EditorEvents as SinceId for low-cost polling.", "Use EditorSnapshot before temporary scene/prefab/window/selection changes; it preserves active dock tabs and Prefab Mode autosave settings.", "Asset deltas are a cue to refresh AssetIntelligence reference graphs before moves/deletes.", "Compiler diagnostics include severity, assembly path, file, line, and column when Unity reports them." },
                    Aliases = new[] { "editor", "events", "diagnostics", "compile", "asset_events", "selection" }
                },
                new DomainDefinition
                {
                    Key = "RuntimeDebug",
                    Title = "Runtime state, profiler, and Play Mode diagnostics",
                    When = "Use for Play Mode runtime state, live component variables, runtime assertions/watch rules, frame time, GC/memory, rendering counters, physics/script markers, spikes, stutter, and performance triage.",
                    FirstCalls = new[] { "UniBridge_ManageEditor Action=GetPlayModeState", "UniBridge_RuntimeProfiler Action=Snapshot", "UniBridge_RuntimeProfiler Action=Metrics", "UniBridge_RuntimeProfiler Action=Hierarchy SampleFrames=1 MaxHierarchySamples=40", "UniBridge_RuntimeStateProbe Action=ListMembers Component=<ComponentOrMonoBehaviour>" },
                    AuthoringTools = new[] { "UniBridge_ManageEditor" },
                    InspectionTools = new[] { "UniBridge_RuntimeProfiler", "UniBridge_RuntimeStateProbe", "UniBridge_ContextSnapshot", "UniBridge_SceneObjectView", "UniBridge_EditorEvents", "UniBridge_ReadConsole" },
                    VerificationTools = new[] { "UniBridge_RuntimeStateProbe", "UniBridge_RuntimeProfiler", "UniBridge_ReadConsole", "UniBridge_CaptureView", "UniBridge_VisualSceneAudit" },
                    CaptureTools = new[] { "UniBridge_CaptureView", "UniBridge_VisualSceneAudit" },
                    TypeNames = new[] { "Unity.Profiling.ProfilerRecorder", "UnityEngine.Profiling.Profiler", "UnityEngine.MonoBehaviour", "UnityEngine.Renderer", "UnityEngine.Camera", "UnityEngine.ParticleSystem", "UnityEngine.EventSystems.EventSystem" },
                    Notes = new[] { "RuntimeProfiler is read-only and captures bounded ProfilerRecorder samples; RuntimeProfiler Action=Hierarchy exports top profiler marker paths for AI performance triage, but it is a marker hierarchy view rather than Unity ProfilerWindow's full call tree.", "RuntimeStateProbe is read-only and samples component SerializedProperty/reflection values and can evaluate simple assertions/watch rules. Neither tool executes arbitrary project code.", "RuntimeStateProbe Action=Assert can fail a batch on required failed assertions, giving AI workflows a structured pass/fail gate.", "Sample/Hierarchy/Assert actions require Play Mode by default so data comes from the running game; pass RequirePlayMode=false only for editor-time smoke tests.", "Full raw profiler/hierarchy samples are saved under Library/UniBridge/RuntimeProfiler and state samples under Library/UniBridge/RuntimeStateProbe when SaveToFile=true; MCP responses stay compact." },
                    Aliases = new[] { "runtime", "runtime_debug", "runtime_state", "state_probe", "watch_variables", "watch_assert", "runtime_assert", "component_state", "profiler", "performance", "fps", "gc", "memory", "spikes", "stutter" }
                },
                new DomainDefinition
                {
                    Key = "Safety",
                    Title = "AI work-session review and revert safety",
                    When = "Use before broad AI edits and after implementation to summarize changed files, inspect text diffs, and dry-run selective reverts.",
                    FirstCalls = new[] { "UniBridge_WorkSession Action=Begin Name=<task>", "UniBridge_WorkSession Action=Status" },
                    AuthoringTools = new[] { "UniBridge_WorkSession", "UniBridge_BatchActions" },
                    InspectionTools = new[] { "UniBridge_WorkSession", "UniBridge_EditorEvents", "UniBridge_ExecutionStatus" },
                    VerificationTools = new[] { "UniBridge_WorkSession", "UniBridge_ReadConsole", "UniBridge_BatchActions" },
                    CaptureTools = Array.Empty<string>(),
                    TypeNames = new[] { "UnityEditor.AssetDatabase" },
                    Notes = new[] { "WorkSession snapshots are stored in Library/UniBridge/WorkSessions and are not version-controlled.", "Executing BatchActions appends data.workSessionReview by default while a WorkSession is active.", "Pass IncludeConsoleDelta=true and IncludeEditorEventDelta=true on BatchActions when the agent wants a compact self-check for logs and editor events emitted by that batch.", "ExecutionStatus Snapshot/Recent include the active WorkSession summary by default.", "Use Review and Diff before final reporting so the agent can explain exactly what changed.", "Revert defaults to DryRun=true and only executes when the agent explicitly passes DryRun=false." },
                    Aliases = new[] { "safety", "checkpoint", "work_session", "review", "changes", "diff", "revert", "rollback", "self_check", "console_delta", "editor_event_delta" }
                },
                new DomainDefinition
                {
                    Key = "LargeScenes",
                    Title = "Large scene hierarchy export and safe reparenting",
                    When = "Use for 100+ object scenes, duplicate names, sorting-layer audits, prototype scene comparisons, and batch hierarchy organization.",
                    FirstCalls = new[] { "UniBridge_SceneHierarchyExport Action=Export WriteToFile=true IncludeInactive=true IncludeRenderers=true IncludePrefabInfo=true", "UniBridge_SceneHierarchyExport Action=CompareExports when two exported scenes must be compared" },
                    AuthoringTools = new[] { "UniBridge_ManageSceneHierarchy", "UniBridge_ScopedEdit", "UniBridge_BatchActions" },
                    InspectionTools = new[] { "UniBridge_SceneHierarchyExport", "UniBridge_SceneObjectView", "UniBridge_UnitySearch" },
                    VerificationTools = new[] { "UniBridge_SceneHierarchyExport", "UniBridge_VisualSceneAudit", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureView", "UniBridge_VisualSceneAudit" },
                    TypeNames = new[] { "UnityEngine.GameObject", "UnityEngine.Transform", "UnityEngine.Renderer", "UnityEngine.SpriteRenderer", "UnityEngine.ParticleSystemRenderer", "UnityEngine.Rendering.Universal.Light2D" },
                    Notes = new[] { "SceneHierarchyExport uses stable depth-first pagination and writes large full exports under Library/UniBridge/SceneHierarchyExports.", "ManageSceneHierarchy accepts objectId targets, preserves world transform, supports dry-run, uses one Undo group, and validates object count after execution.", "For duplicate object names, prefer objectId plus returned parentObjectId/siblingIndex instead of name-only edits." },
                    Aliases = new[] { "large_scene", "large_scenes", "hierarchy", "scene_hierarchy", "scene_sorting", "batch_reparent" }
                },
                new DomainDefinition
                {
                    Key = "Rendering",
                    Title = "Camera, light, and rendering presets",
                    When = "Use for gameplay cameras, 2D/isometric cameras, pixel-perfect cameras, 2D lights/shadows, light rigs, scene lighting, volumes, and URP additional data.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=Rendering", "UniBridge_SceneObjectView Profile=Rendering", "UniBridge_TypeSchema TypeName=Camera" },
                    AuthoringTools = new[] { "UniBridge_ManageRendering", "UniBridge_ManageMaterial", "UniBridge_ManageShader" },
                    InspectionTools = new[] { "UniBridge_SceneObjectView", "UniBridge_TypeSchema", "UniBridge_AssetIntelligence" },
                    VerificationTools = new[] { "UniBridge_ManageRendering", "UniBridge_CaptureView", "UniBridge_VisualSceneAudit", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureView", "UniBridge_VisualSceneAudit", "UniBridge_CaptureAsset" },
                    TypeNames = new[] { "UnityEngine.Camera", "UnityEngine.Light", "UnityEngine.ReflectionProbe", "UnityEngine.LightProbeGroup", "UnityEngine.LightProbeProxyVolume", "UnityEngine.Rendering.Volume", "UnityEngine.Rendering.VolumeProfile", "UnityEngine.RenderSettings", "UnityEngine.Material", "UnityEngine.Shader", "UnityEngine.U2D.PixelPerfectCamera", "UnityEngine.Rendering.Universal.Light2D", "UnityEngine.Rendering.Universal.ShadowCaster2D", "UnityEngine.U2D.SpriteShapeRenderer", "UnityEngine.Rendering.Universal.DecalProjector", "UnityEngine.Rendering.LensFlareComponentSRP", "UnityEngine.LensFlare", "UnityEngine.FlareLayer", "UnityEngine.WindZone", "UnityEngine.Projector", "UnityEngine.LODGroup" },
                    Notes = new[] { "Use rendering presets to create a usable scene view quickly, then tune materials/shaders separately.", "Run VisualSceneAudit after visible scene/camera/material work to catch fallback-magenta captures, blank framing, huge placeholder blocks, broken materials, and console issues before reporting success.", "ManageRendering can author light/renderer rendering layer masks from project layer names.", "ManageRendering also has focused actions for decals, lens flares, flare layers, wind zones, legacy projectors, LOD groups, and probe volumes so agents do not need to hand-patch rendering components.", "Scene inspection reports renderer sorting and rendering layer masks with project layer names so draw/lighting issues are easier to diagnose.", "Optional package/module types are applied through reflection where packages are optional." },
                    Aliases = new[] { "render", "rendering", "camera", "lighting", "lights", "postprocessing" }
                },
                new DomainDefinition
                {
                    Key = "Audio",
                    Title = "Scene audio authoring",
                    When = "Use for AudioSource, AudioListener, AudioReverbZone, and audio filter setup.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=Audio", "UniBridge_SceneObjectView Profile=Audio", "UniBridge_ManageAudio Action=Inspect" },
                    AuthoringTools = new[] { "UniBridge_ManageAudio", "UniBridge_ManageGameObject", "UniBridge_ManageAssetImporter" },
                    InspectionTools = new[] { "UniBridge_SceneObjectView", "UniBridge_TypeSchema" },
                    VerificationTools = new[] { "UniBridge_ManageAudio", "UniBridge_ReadConsole" },
                    CaptureTools = Array.Empty<string>(),
                    TypeNames = new[] { "UnityEngine.AudioSource", "UnityEngine.AudioListener", "UnityEngine.AudioReverbZone", "UnityEngine.AudioLowPassFilter", "UnityEngine.AudioHighPassFilter", "UnityEngine.AudioEchoFilter", "UnityEngine.AudioReverbFilter", "UnityEngine.AudioChorusFilter", "UnityEngine.AudioDistortionFilter", "UnityEngine.AudioClip" },
                    Notes = new[] { "Use presets for 2D SFX, 3D one-shots, ambient loops, and listener rigs." },
                    Aliases = new[] { "audio", "audiosource", "listener", "sound", "sfx" }
                },
                new DomainDefinition
                {
                    Key = "VFX",
                    Title = "Particles, trails, lines, video, and visual effects",
                    When = "Use for ParticleSystem, TrailRenderer, LineRenderer, VisualEffect, and VideoPlayer setup.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=VFX", "UniBridge_SceneObjectView Profile=VFX", "UniBridge_ManageVFX Action=Inspect" },
                    AuthoringTools = new[] { "UniBridge_ManageVFX", "UniBridge_ManageMaterial", "UniBridge_ManageGameObject" },
                    InspectionTools = new[] { "UniBridge_SceneObjectView", "UniBridge_TypeSchema" },
                    VerificationTools = new[] { "UniBridge_ManageVFX", "UniBridge_CaptureView", "UniBridge_VisualSceneAudit", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureView", "UniBridge_VisualSceneAudit" },
                    TypeNames = new[] { "UnityEngine.ParticleSystem", "UnityEngine.ParticleSystemRenderer", "UnityEngine.TrailRenderer", "UnityEngine.LineRenderer", "UnityEngine.VFX.VisualEffect", "UnityEngine.Video.VideoPlayer", "UnityEngine.Material" },
                    Notes = new[] { "Use simple presets to avoid blank first-frame captures, then tune exact modules or materials." },
                    Aliases = new[] { "vfx", "particles", "trail", "line", "visualeffect", "video" }
                },
                new DomainDefinition
                {
                    Key = "UIToolkit",
                    Title = "UI Toolkit UXML/USS authoring",
                    When = "Use for UXML documents, USS files, PanelSettings, UIDocument wiring, and UI Toolkit capture.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=UIToolkit", "UniBridge_CaptureUIToolkit Action=ListUxml", "UniBridge_UnitySearch Sources=[Assets,SceneObjects]" },
                    AuthoringTools = new[] { "UniBridge_ManageUIToolkit" },
                    InspectionTools = new[] { "UniBridge_CaptureUIToolkit", "UniBridge_TypeSchema", "UniBridge_AssetIntelligence" },
                    VerificationTools = new[] { "UniBridge_CaptureUIToolkit", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureUIToolkit" },
                    TypeNames = new[] { "UnityEngine.UIElements.UIDocument", "UnityEngine.UIElements.PanelSettings", "UnityEngine.UIElements.VisualTreeAsset", "UnityEngine.UIElements.StyleSheet", "UnityEngine.UIElements.ThemeStyleSheet", "UnityEngine.UIElements.VisualElement", "UnityEngine.UIElements.Button", "UnityEngine.UIElements.Label" },
                    Notes = new[] { "Create UXML/USS assets first, attach them with UIDocument, then capture or inspect the resolved tree.", "CaptureUIToolkit returns render/readback metadata plus layout hints for blank captures, zero-size elements, likely text overflow, and visible overlap.", "Use class/style patch actions for small structural changes instead of rewriting entire documents." },
                    Aliases = new[] { "uitoolkit", "ui_toolkit", "uxml", "uss", "uidocument" }
                },
                new DomainDefinition
                {
                    Key = "UI",
                    Title = "uGUI screen authoring and validation",
                    When = "Use for Canvas, RectTransform, templates, layout validation, and UnityEvent wiring.",
                    FirstCalls = new[] { "UniBridge_ToolGuide Workflow=ui", "UniBridge_ManageUI Action=Inspect", "UniBridge_ManageUI Action=Validate" },
                    AuthoringTools = new[] { "UniBridge_ManageUI", "UniBridge_ManageUnityEvent" },
                    InspectionTools = new[] { "UniBridge_ManageUI", "UniBridge_TypeSchema" },
                    VerificationTools = new[] { "UniBridge_ManageUI", "UniBridge_CaptureView", "UniBridge_VisualSceneAudit", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureView", "UniBridge_VisualSceneAudit" },
                    TypeNames = new[] { "UnityEngine.Canvas", "UnityEngine.RectTransform", "UnityEngine.UI.Button", "UnityEngine.UI.Text", "UnityEngine.UI.Image", "UnityEngine.UI.GridLayoutGroup", "UnityEngine.UI.VerticalLayoutGroup", "UnityEngine.UI.HorizontalLayoutGroup", "UnityEngine.UI.CanvasScaler" },
                    Notes = new[] { "Prefer templates for complete screens and ValidateUI after layout changes." },
                    Aliases = new[] { "ui", "ugui", "canvas", "hud", "layout" }
                },
                new DomainDefinition
                {
                    Key = "Input",
                    Title = "Input System assets and scene wiring",
                    When = "Use for .inputactions maps/bindings, PlayerInput, PlayerInputManager, UI input modules, on-screen controls, virtual mouse, and multiplayer event systems.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=Input", "UniBridge_ManageInputActions Action=Inspect", "UniBridge_SceneObjectView Profile=Input" },
                    AuthoringTools = new[] { "UniBridge_ManageInputActions", "UniBridge_ManageGameObject" },
                    InspectionTools = new[] { "UniBridge_SceneObjectView", "UniBridge_AssetIntelligence", "UniBridge_TypeSchema" },
                    VerificationTools = new[] { "UniBridge_ManageInputActions", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureView" },
                    TypeNames = new[] { "UnityEngine.InputSystem.InputActionAsset", "UnityEngine.InputSystem.PlayerInput", "UnityEngine.InputSystem.PlayerInputManager", "UnityEngine.InputSystem.UI.InputSystemUIInputModule", "UnityEngine.InputSystem.OnScreen.OnScreenButton", "UnityEngine.InputSystem.OnScreen.OnScreenStick", "UnityEngine.InputSystem.UI.VirtualMouseInput", "UnityEngine.InputSystem.UI.MultiplayerEventSystem" },
                    Notes = new[] { "The asset path works through JSON; scene components are wired reflectively when the Input System package is present." },
                    Aliases = new[] { "input", "inputsystem", "input_actions", "playerinput", "controls" }
                },
                new DomainDefinition
                {
                    Key = "Physics2D",
                    Title = "2D physics authoring",
                    When = "Use for Rigidbody2D, Collider2D, Joint2D, Effector2D, and PhysicsMaterial2D presets.",
                    FirstCalls = new[] { "UniBridge_ToolGuide Workflow=physics2d", "UniBridge_ManagePhysics2D Action=Inspect" },
                    AuthoringTools = new[] { "UniBridge_ManagePhysics2D", "UniBridge_ManageGameObject" },
                    InspectionTools = new[] { "UniBridge_SceneObjectView", "UniBridge_TypeSchema" },
                    VerificationTools = new[] { "UniBridge_ManagePhysics2D", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureView" },
                    TypeNames = new[] { "UnityEngine.Rigidbody2D", "UnityEngine.BoxCollider2D", "UnityEngine.CircleCollider2D", "UnityEngine.CapsuleCollider2D", "UnityEngine.PolygonCollider2D", "UnityEngine.PlatformEffector2D", "UnityEngine.PhysicsMaterial2D" },
                    Notes = new[] { "Use material and collider presets for prototypes, then inspect." },
                    Aliases = new[] { "physics2d", "2d_physics", "collider2d", "rigidbody2d" }
                },
                new DomainDefinition
                {
                    Key = "Animation",
                    Title = "Animation clips and animator controllers",
                    When = "Use for AnimationClip curves/events, AnimatorController graphs, and sampled visual capture.",
                    FirstCalls = new[] { "UniBridge_ToolGuide Workflow=animator", "UniBridge_ManageAnimatorController Action=Inspect", "UniBridge_ManageAnimationClip Action=Inspect" },
                    AuthoringTools = new[] { "UniBridge_ManageAnimationClip", "UniBridge_ManageAnimatorController", "UniBridge_ManageConstraints" },
                    InspectionTools = new[] { "UniBridge_TypeSchema", "UniBridge_AssetIntelligence", "UniBridge_SceneObjectView" },
                    VerificationTools = new[] { "UniBridge_CaptureAsset", "UniBridge_CaptureView", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureAsset", "UniBridge_CaptureView" },
                    TypeNames = new[] { "UnityEngine.AnimationClip", "UnityEngine.Animator", "UnityEditor.Animations.AnimatorController", "UnityEngine.RuntimeAnimatorController", "UnityEngine.AnimationCurve", "UnityEngine.Gradient", "UnityEngine.Animations.ParentConstraint", "UnityEngine.Animations.PositionConstraint", "UnityEngine.Animations.RotationConstraint", "UnityEngine.Animations.ScaleConstraint", "UnityEngine.Animations.AimConstraint", "UnityEngine.Animations.LookAtConstraint" },
                    Notes = new[] { "Generic property patching supports AnimationCurve and Gradient values.", "Use ManageConstraints for transform-follow relationships before authoring custom scripts." },
                    Aliases = new[] { "animation", "animator", "clip", "curves" }
                },
                new DomainDefinition
                {
                    Key = "Timeline",
                    Title = "Timeline and PlayableDirector authoring",
                    When = "Use for TimelineAsset tracks, clips, bindings, and director setup.",
                    FirstCalls = new[] { "UniBridge_ToolGuide Workflow=timeline", "UniBridge_ManageTimeline Action=Inspect" },
                    AuthoringTools = new[] { "UniBridge_ManageTimeline" },
                    InspectionTools = new[] { "UniBridge_SceneObjectView", "UniBridge_AssetIntelligence" },
                    VerificationTools = new[] { "UniBridge_ManageTimeline", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureView" },
                    TypeNames = new[] { "UnityEngine.Playables.PlayableDirector", "UnityEngine.Timeline.TimelineAsset", "UnityEngine.Timeline.TrackAsset" },
                    Notes = new[] { "Timeline package types are handled with reflection where needed." },
                    Aliases = new[] { "timeline", "playable", "sequencer", "cinematic" }
                },
                new DomainDefinition
                {
                    Key = "Assets",
                    Title = "Asset import, snapshots, and generic asset authoring",
                    When = "Use for importer settings, asset snapshots, material/shader assets, ScriptableObjects, and allowlisted generic assets.",
                    FirstCalls = new[] { "UniBridge_EditorEvents Action=Snapshot IncludeAssetChanges=true", "UniBridge_AssetIntelligence Context", "UniBridge_AssetIntelligence Action=Structure for prefab/loaded scene hierarchy search", "UniBridge_AssetIntelligence ReferenceGraph/Impact IncludeReferenceLocations=true for exact YAML reference sites", "UniBridge_ManageAssetImporter Inspect", "UniBridge_TypeSchema InspectAsset IncludePatchExamples=true" },
                    AuthoringTools = new[] { "UniBridge_ManageAsset", "UniBridge_ManageAssetImporter", "UniBridge_ManageMaterial", "UniBridge_ManageScriptableObject" },
                    InspectionTools = new[] { "UniBridge_AssetIntelligence", "UniBridge_TypeSchema", "UniBridge_UnitySearch", "UniBridge_EditorEvents" },
                    VerificationTools = new[] { "UniBridge_CaptureAsset", "UniBridge_EditorEvents", "UniBridge_ReadConsole" },
                    CaptureTools = new[] { "UniBridge_CaptureAsset" },
                    TypeNames = new[] { "UnityEngine.Texture2D", "UnityEngine.Sprite", "UnityEngine.Material", "UnityEngine.Shader", "UnityEngine.RenderTexture", "UnityEngine.TerrainLayer", "UnityEngine.AvatarMask", "UnityEngine.ShaderVariantCollection", "UnityEditor.AssetImporter" },
                    Notes = new[] { "Use AssetIntelligence Impact/ReferenceGraph before risky moves or deletes; pass IncludeReferenceLocations=true when exact YAML line/property/object context matters.", "Use AssetIntelligence Structure for compact prefab or loaded scene hierarchy list/search/read with indexed paths and serialized field matching.", "TypeSchema PatchExamples gives ready-to-call property patches for importers and ScriptableObjects.", "AssetSnapshotSerializer returns compact profiles for noisy materials, textures, clips, particles, TMP fonts, audio mixers, timelines, input actions, UI Toolkit assets, meshes, shaders, atlases, tiles, and related importer data." },
                    Aliases = new[] { "assets", "import", "importer", "materials", "sprites", "asset_structure", "prefab_structure", "asset_ref_search", "reference_locations" }
                },
                new DomainDefinition
                {
                    Key = "Scripts",
                    Title = "C# script discovery and source editing",
                    When = "Use for script search, attached behaviour context, safe text edits, validation, and compilation.",
                    FirstCalls = new[] { "UniBridge_ScriptIntelligence Search", "UniBridge_ScriptIntelligence Usages IncludeUsageLocations=true", "UniBridge_ScriptIntelligence MemberUsages Member=<methodOrField>", "UniBridge_ScriptIntelligence CodeUsages Member=<methodOrField>", "UniBridge_ScriptIntelligence ChangeImpact ProposedSource=<candidateSource>", "UniBridge_BehaviourContext IncludeSource=true", "UniBridge_GetSha" },
                    AuthoringTools = new[] { "UniBridge_CreateScript", "UniBridge_ApplyTextEdits", "UniBridge_DeleteScript" },
                    InspectionTools = new[] { "UniBridge_ScriptIntelligence", "UniBridge_BehaviourContext", "UniBridge_TypeSchema" },
                    VerificationTools = new[] { "UniBridge_ManageEditor", "UniBridge_EditorEvents", "UniBridge_ReadConsole" },
                    CaptureTools = Array.Empty<string>(),
                    TypeNames = new[] { "UnityEngine.MonoBehaviour", "UnityEngine.ScriptableObject", "UnityEditor.MonoScript" },
                    Notes = new[] { "After script edits, validate scripts, refresh assets, request compilation with RequestScriptCompilationNoWait, then wait with WaitForReadyAfterReload before scene work.", "Use ScriptIntelligence ChangeImpact before applying a large source edit to estimate API, serialized field, Unity callback, and reload risk from ProposedSource/ProposedPath.", "Use ScriptIntelligence Usages IncludeUsageLocations=true to see exact prefab/scene YAML references to a script before deletion or migration.", "Use ScriptIntelligence MemberUsages before renaming/removing UnityEvent handlers, AnimationEvent functions, or serialized fields.", "Use ScriptIntelligence CodeUsages before renaming/removing C# API; it returns syntax-based call sites with Exact, Possible, or RuntimeResolved confidence.", "Use EditorEvents IncludeDiagnostics=true to read retained compiler errors/warnings with file/line/column after compilation." },
                    Aliases = new[] { "scripts", "code", "csharp", "monobehaviour", "script_usages", "code_usages", "caller_scan", "member_callers", "change_impact", "script_preflight", "hot_diff", "member_usages", "serialized_member_usages" }
                }
            };
        }

        static string GetString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
                    return token.ToString().Trim();
            return null;
        }

        static bool GetBool(JObject obj, bool defaultValue, params string[] keys)
        {
            var raw = GetString(obj, keys);
            return bool.TryParse(raw, out var value) ? value : defaultValue;
        }

        static int GetLimit(JObject parameters)
        {
            var raw = GetString(parameters, "Limit", "limit", "MaxResults", "maxResults", "max_results");
            return int.TryParse(raw, out var value) ? Math.Max(1, Math.Min(MaxLimit, value)) : DefaultLimit;
        }

        static string Normalize(string value)
        {
            return (value ?? string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();
        }

        sealed class DomainDefinition
        {
            public string Key;
            public string Title;
            public string When;
            public string[] FirstCalls = Array.Empty<string>();
            public string[] AuthoringTools = Array.Empty<string>();
            public string[] InspectionTools = Array.Empty<string>();
            public string[] VerificationTools = Array.Empty<string>();
            public string[] CaptureTools = Array.Empty<string>();
            public string[] TypeNames = Array.Empty<string>();
            public string[] Notes = Array.Empty<string>();
            public string[] Aliases = Array.Empty<string>();
        }
    }
}
