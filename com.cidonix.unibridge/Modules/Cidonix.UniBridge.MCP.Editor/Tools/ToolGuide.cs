#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Gives agents a compact map of which UniBridge tools to use for common workflows.
    /// </summary>
    public static class ToolGuide
    {
        const string ToolName = "UniBridge_ToolGuide";

        public const string Title = "Guide UniBridge tool selection";

public const string Description = @"Return a compact, agent-facing guide for choosing UniBridge tools and workflow order.

Use this when an agent is new to a project or unsure which UniBridge tool should handle a Unity task. It summarizes the recommended first calls, edit tools, verification calls, batch aliases, and common workflows without changing the project.

Search aliases: UniBridge Unity MCP ToolGuide WorkSession checkpoint review changes diff revert rollback ValidateScript RefreshAssets RequestScriptCompilationNoWait WaitForReadyAfterReload GetCompilationDiagnostics ReadConsole DiagnosticSummary ClearConsole console delta post action diagnostics batch self check PlayMode WaitForPlayMode WaitForEditMode RuntimeProfiler RuntimeStateProbe runtime state state probe runtime assert watch assert watch variables component fields MonoBehaviour state profiler performance FPS GC memory spikes TypeSchema TypeIndex type map type fingerprint component schema ScriptableObject schema asset structure prefab structure serialized asset search ValidateAdditiveSceneRegistration additive scene validation.

Actions:
    Overview: Core orientation flow plus available workflow topics.
    Workflow: Focused guide for one topic such as ui, scene_objects, assets_import, unity_events, scripts, batch, or console.
    Tool: Resolve a tool name or alias and show its registry metadata plus related workflows.

This tool is read-only.";

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "guide", "editor" }, EnabledByDefault = true)]
        public static object HandleCommand(ToolGuideParams parameters)
        {
            parameters ??= new ToolGuideParams();

            try
            {
                var action = Normalize(parameters.Action, "Overview");
                var data = action switch
                {
                    "workflow" => BuildWorkflowResponse(parameters),
                    "tool" => BuildToolResponse(parameters),
                    _ => BuildOverviewResponse(parameters)
                };

                return Response.Success("Built UniBridge tool guide.", data);
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to build tool guide: {ex.Message}");
            }
        }

        static object BuildOverviewResponse(ToolGuideParams parameters)
        {
            var workflows = GetWorkflows();
            return new
            {
                action = "Overview",
                recommendedStart = new[]
                {
                    new { step = 1, tool = ToolName, why = "Ask for a workflow or tool map when the task is unclear." },
                    new { step = 2, tool = "UniBridge_Discover", why = "Ping UniBridge and get searchable workflow aliases when a Codex thread is newly connected." },
                    new { step = 3, tool = "UniBridge_DomainCatalog", why = "Pick the Unity work domain and see its authoring/inspection tools." },
                    new { step = 4, tool = "UniBridge_ContextSnapshot", why = "Get project roots, render settings, packages, scene, console, selection, asset, and tool context in one read-only call." },
                    new { step = 5, tool = "UniBridge_WorkSession", why = "Start a checkpoint before broad AI edits, then review/diff/revert selected changed files after the work." },
                    new { step = 6, tool = "UniBridge_UnitySearch", why = "Resolve vague user references across scene objects, assets, scripts, shaders, and menu items." },
                    new { step = 7, tool = "UniBridge_WorkflowRecipes", why = "Use a complete recipe when the task matches a common Unity workflow." },
                    new { step = 8, tool = "UniBridge_ReadConsole", why = "Check Unity diagnostics before and after edits." },
                    new { step = 9, tool = "UniBridge_ExecutionStatus", why = "Inspect active/pending UniBridge tool execution if something appears to wait or timeout." }
                },
                coreLoop = new[]
                {
                    "Orient with ContextSnapshot or ToolGuide.",
                    "Resolve targets with UnitySearch, SceneObjectView, TypeSchema/TypeIndex, AssetIntelligence, or ScriptIntelligence.",
                    "Use WorkflowRecipes for common full workflows, or dry-run custom multi-step changes with BatchActions.",
                    "Apply the smallest suitable Manage* tool.",
                    "Verify with ReadConsole and a capture/inspect tool.",
                    "Review the WorkSession changed-file report before reporting completion."
                },
                workflowTopics = workflows.Select(ToWorkflowSummary).ToArray(),
                batch = BuildBatchSummary(),
                registeredTools = parameters.IncludeRegisteredTools == true ? BuildRegisteredToolSummary() : null
            };
        }

        static object BuildWorkflowResponse(ToolGuideParams parameters)
        {
            var topic = parameters.Topic;
            if (string.IsNullOrWhiteSpace(topic))
            {
                topic = parameters.Tool;
            }

            var workflow = FindWorkflow(topic);
            if (workflow == null)
            {
                return new
                {
                    action = "Workflow",
                    found = false,
                    requestedTopic = topic,
                    availableTopics = GetWorkflows().Select(workflowInfo => workflowInfo.Key).ToArray(),
                    hint = "Call Action=Overview to see all workflow topics."
                };
            }

            return new
            {
                action = "Workflow",
                found = true,
                workflow = ToWorkflowDetail(workflow),
                registeredTools = parameters.IncludeRegisteredTools == true ? BuildRegisteredToolSummary() : null
            };
        }

        static object BuildToolResponse(ToolGuideParams parameters)
        {
            var rawTool = string.IsNullOrWhiteSpace(parameters.Tool) ? parameters.Topic : parameters.Tool;
            var resolved = BatchActionToolCatalog.ResolveToolName(rawTool);
            if (string.IsNullOrWhiteSpace(resolved) && !string.IsNullOrWhiteSpace(rawTool))
            {
                resolved = rawTool.Trim();
            }

            var entries = McpToolRegistry.GetAllToolsForSettings();
            var entry = entries.FirstOrDefault(item =>
                string.Equals(item.Info?.name, resolved, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Info?.name, rawTool, StringComparison.OrdinalIgnoreCase));

            var toolName = entry.Info?.name ?? resolved;
            var relatedWorkflows = string.IsNullOrWhiteSpace(toolName)
                ? Array.Empty<object>()
                : GetWorkflows()
                    .Where(workflow => workflow.Tools.Any(tool => string.Equals(tool, toolName, StringComparison.OrdinalIgnoreCase)))
                    .Select(ToWorkflowSummary)
                    .ToArray();

            return new
            {
                action = "Tool",
                requestedTool = rawTool,
                resolvedTool = toolName,
                found = entry.Info != null,
                enabled = entry.Info != null ? entry.IsEnabled : (bool?)null,
                enabledByDefault = entry.Info != null ? entry.IsDefault : (bool?)null,
                title = entry.Info?.title,
                description = entry.Info?.description,
                groups = entry.Groups,
                batchAllowed = BatchActionToolCatalog.IsAllowed(toolName),
                batchAliases = BatchActionToolCatalog.GetAliasesForTool(toolName),
                relatedWorkflows,
                registeredTools = parameters.IncludeRegisteredTools == true ? BuildRegisteredToolSummary() : null
            };
        }

        static object BuildBatchSummary()
        {
            return new
            {
                tool = "UniBridge_BatchActions",
                defaults = new
                {
                    dryRun = true,
                    validateBeforeExecute = true,
                    rollbackOnFailure = true,
                    rollbackAssets = true
                },
                allowedTools = BatchActionToolCatalog.AllowedTools.OrderBy(tool => tool).ToArray(),
                aliases = BatchActionToolCatalog.ToolAliases
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new { alias = pair.Key, tool = pair.Value })
                    .ToArray()
            };
        }

        static object BuildRegisteredToolSummary()
        {
            var entries = McpToolRegistry.GetAllToolsForSettings()
                .OrderBy(entry => entry.Info?.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new
            {
                total = entries.Length,
                enabled = entries.Count(entry => entry.IsEnabled),
                disabled = entries.Count(entry => !entry.IsEnabled),
                tools = entries.Select(entry => new
                {
                    name = entry.Info?.name,
                    title = entry.Info?.title,
                    enabled = entry.IsEnabled,
                    enabledByDefault = entry.IsDefault,
                    groups = entry.Groups,
                    batchAliases = BatchActionToolCatalog.GetAliasesForTool(entry.Info?.name)
                }).ToArray()
            };
        }

        static object ToWorkflowSummary(WorkflowGuide workflow)
        {
            return new
            {
                topic = workflow.Key,
                title = workflow.Title,
                when = workflow.When,
                tools = workflow.Tools,
                aliases = workflow.Aliases
            };
        }

        static object ToWorkflowDetail(WorkflowGuide workflow)
        {
            return new
            {
                topic = workflow.Key,
                title = workflow.Title,
                when = workflow.When,
                firstCalls = workflow.FirstCalls,
                editCalls = workflow.EditCalls,
                verifyCalls = workflow.VerifyCalls,
                tools = workflow.Tools,
                batchAliases = workflow.BatchAliases,
                notes = workflow.Notes,
                aliases = workflow.Aliases
            };
        }

        static WorkflowGuide FindWorkflow(string topic)
        {
            var normalized = Normalize(topic, null);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "orientation";
            }

            var workflows = GetWorkflows();
            var exact = workflows.FirstOrDefault(workflow =>
                string.Equals(workflow.Key, normalized, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            return workflows.FirstOrDefault(workflow =>
                workflow.Aliases.Any(alias => string.Equals(Normalize(alias, alias), normalized, StringComparison.OrdinalIgnoreCase)));
        }

        static string Normalize(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return value.Trim()
                .Replace('-', '_')
                .Replace(' ', '_')
                .ToLowerInvariant();
        }

        static WorkflowGuide[] GetWorkflows()
        {
            return new[]
            {
                new WorkflowGuide
                {
                    Key = "orientation",
                    Title = "Orient in a Unity project",
                    When = "Use at the start of an unfamiliar project or after a long pause.",
                    FirstCalls = new[] { "UniBridge_ToolGuide Action=Overview", "UniBridge_DomainCatalog Action=Overview", "UniBridge_ContextSnapshot Depth=Standard IncludeConsole=true IncludeTools=true IncludeProjectRoots=true IncludeProjectSettings=true", "UniBridge_WorkSession Action=Begin Name=<task> before broad edits", "UniBridge_EditorEvents Action=Snapshot IncludeSelection=true IncludeDiagnostics=true", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { ToolName, "UniBridge_Discover", "UniBridge_DomainCatalog", "UniBridge_ContextSnapshot", "UniBridge_WorkSession", "UniBridge_EditorEvents", "UniBridge_RuntimeProfiler", "UniBridge_RuntimeStateProbe", "UniBridge_UnitySearch", "UniBridge_WorkflowRecipes", "UniBridge_ReadConsole", "UniBridge_ExecutionStatus", "UniBridge_EditorSnapshot" },
                    BatchAliases = Array.Empty<string>(),
                    Notes = new[] { "Use this before planning broad scene, asset, UI, or script edits." },
                    Aliases = new[] { "start", "overview", "project" }
                },
                new WorkflowGuide
                {
                    Key = "work_session",
                    Title = "Review and protect an AI work session",
                    When = "Use before and after broad AI edits so the agent can explain changed files, inspect text diffs, and safely revert selected paths.",
                    FirstCalls = new[] { "UniBridge_WorkSession Action=Begin Name=<task>", "Run the normal domain-specific UniBridge tools for the task; executing BatchActions appends data.workSessionReview when this session is active" },
                    EditCalls = new[] { "UniBridge_WorkSession Action=Revert DryRun=true Paths=[Assets/...]", "UniBridge_WorkSession Action=Revert DryRun=false Paths=[Assets/...] only after checking the dry-run plan" },
                    VerifyCalls = new[] { "UniBridge_ExecutionStatus Action=Snapshot to see scheduler state plus active WorkSession summary", "UniBridge_WorkSession Action=Review", "UniBridge_WorkSession Action=Diff Paths=[Assets/...]", "UniBridge_ReadConsole Action=DiagnosticSummary", "UniBridge_WorkSession Action=End" },
                    Tools = new[] { "UniBridge_WorkSession", "UniBridge_ReadConsole", "UniBridge_ExecutionStatus", "UniBridge_EditorEvents" },
                    BatchAliases = Array.Empty<string>(),
                    Notes = new[] { "Snapshots live under Library/UniBridge/WorkSessions and are not meant for version control.", "Revert defaults to DryRun=true and only restores files that were captured at Begin, or deletes files added after Begin.", "Use this alongside BatchActions rollback: BatchActions protects one planned execution, WorkSession reviews the whole agent work window.", "Use IncludeWorkSessionReview=false on BatchActions or IncludeWorkSession=false on ExecutionStatus when a response must stay scheduler-only." },
                    Aliases = new[] { "checkpoint", "review", "changes", "diff", "revert", "rollback", "safety" }
                },
                new WorkflowGuide
                {
                    Key = "additive_scene_validation",
                    Title = "Validate additive scene registration",
                    When = "Use after cloning or wiring additive Unity scenes to verify scene asset, metadata, Build Settings, scenesManager, and boundary arrays without changing the project.",
                    FirstCalls = new[] { "UniBridge_ValidateAdditiveSceneRegistration ScenePath=Assets/.../<scene>.unity", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "UniBridge_ValidateAdditiveSceneRegistration SceneName=<sceneName> RequireBuildSettingsEntry=true", "UniBridge_BatchActions DryRun=true steps=[validate_additive_scene, editor GetCompilationDiagnostics, console DiagnosticSummary]" },
                    Tools = new[] { "UniBridge_ValidateAdditiveSceneRegistration", "UniBridge_BatchActions", "UniBridge_ManageEditor", "UniBridge_ReadConsole", "UniBridge_SceneHierarchyExport" },
                    BatchAliases = new[] { "validate_additive_scene", "additive_scene_validation", "scene_registration" },
                    Notes = new[] { "The validator is read-only and checks .unity/.meta GUIDs, metadata asset GUID, scene-to-metadata references, EditorBuildSettings, scenesManager runtime entries, boundary counts, and optional stale template names/GUIDs.", "Use this before Play Mode tests when a new additive scene was cloned or renamed." },
                    Aliases = new[] { "additive_scene", "scene_registration", "scene_metadata", "darkness12", "darkness13" }
                },
                new WorkflowGuide
                {
                    Key = "search",
                    Title = "Resolve vague user references",
                    When = "Use when the user names an object, asset, script, shader, menu item, or folder without an exact path.",
                    FirstCalls = new[] { "UniBridge_UnitySearch with Sources suited to the task", "UniBridge_AssetIntelligence Search/Context/ResolveMissing for asset-heavy tasks", "UniBridge_AssetIntelligence Action=Structure StructureMode=Search for prefab or loaded scene hierarchy/serialized-field lookup", "UniBridge_ScriptIntelligence Search for code/type tasks", "UniBridge_TypeSchema Action=TypeIndex for loaded Unity/C# type lookup" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "UniBridge_TypeSchema or SceneObjectView for selected scene targets", "UniBridge_AssetIntelligence Context or Action=Structure StructureMode=Read for selected assets" },
                    Tools = new[] { "UniBridge_UnitySearch", "UniBridge_AssetIntelligence", "UniBridge_ScriptIntelligence", "UniBridge_TypeSchema", "UniBridge_SceneObjectView" },
                    BatchAliases = new[] { "find", "lookup", "asset_search", "asset_structure", "prefab_structure", "script_search", "schema" },
                    Notes = new[] { "Prefer search before editing when names are ambiguous.", "Use TypeSchema TypeIndex/TypeFingerprint when a component or ScriptableObject short name may be ambiguous across namespaces/assemblies.", "Use AssetIntelligence Context when you want structured one-call asset summary/read/serialize/suggestion output.", "Use AssetIntelligence Structure when you need compact list/search/read over prefab or loaded scene hierarchy, including indexed paths and serialized field matching.", "Use AssetIntelligence ResolveMissing when a user-provided asset path is stale or mistyped." },
                    Aliases = new[] { "find", "lookup", "resolve", "asset_structure", "prefab_structure" }
                },
                new WorkflowGuide
                {
                    Key = "asset_structure",
                    Title = "Inspect prefab or loaded scene asset structure",
                    When = "Use when an agent needs a compact hierarchy map, component search, or serialized-field drill-down inside a prefab or already-loaded scene asset.",
                    FirstCalls = new[] { "UniBridge_AssetIntelligence Action=Structure StructureMode=List Path=Assets/.../<prefab>.prefab", "UniBridge_AssetIntelligence Action=Structure StructureMode=Search Path=Assets/... Query=<text> MatchFields=all Limit=20" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "UniBridge_AssetIntelligence Action=Structure StructureMode=Read ObjectPath=<indexedPath> IncludeSerializedProperties=true", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_AssetIntelligence", "UniBridge_UnitySearch", "UniBridge_SceneHierarchyExport", "UniBridge_SceneObjectView" },
                    BatchAliases = new[] { "asset_structure", "prefab_structure", "scene_asset_structure", "structure_search", "serialized_asset_search" },
                    Notes = new[] { "Structure is read-only and does not open unloaded scenes automatically.", "Use indexedPath from List/Search when duplicate sibling names make plain paths ambiguous.", "For unloaded scene assets, load/open the scene first or use AssetIntelligence Read for raw YAML text." },
                    Aliases = new[] { "asset_structure", "prefab_structure", "serialized_asset_search", "read_yaml" }
                },
                new WorkflowGuide
                {
                    Key = "type_schema",
                    Title = "Resolve and inspect Unity/C# types",
                    When = "Use before AddComponent, ScriptableObject creation, serialized property patching, or any task where a short type name may be ambiguous.",
                    FirstCalls = new[] { "UniBridge_TypeSchema Action=TypeFingerprint", "UniBridge_TypeSchema Action=TypeIndex Kind=Any Query=<name> Limit=50", "UniBridge_TypeSchema Action=TypeIndex Kind=Any WriteToFile=true Limit=80 for a cacheable project type map" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "UniBridge_TypeSchema Action=Inspect TypeName=<fullName> IncludePatchExamples=true", "UniBridge_TypeSchema Action=InspectGameObject Target=<object> IncludeValues=true" },
                    Tools = new[] { "UniBridge_TypeSchema", "UniBridge_DomainCatalog", "UniBridge_ScriptIntelligence", "UniBridge_UnitySearch" },
                    BatchAliases = new[] { "type_schema", "component_schema", "schema" },
                    Notes = new[] { "TypeFingerprint lets an agent decide whether a saved TypeIndex file is still current after assembly reloads.", "TypeIndex can write the full bounded map under Library/UniBridge/TypeIndex while keeping the MCP response compact.", "Use fullName from TypeIndex when simpleName is ambiguous." },
                    Aliases = new[] { "schema", "type_schema", "type_index", "type_map", "component_schema", "scriptableobject_schema" }
                },
                new WorkflowGuide
                {
                    Key = "editor_control",
                    Title = "Control Unity editor state",
                    When = "Use when the agent needs to select/ping targets, wait after refresh/compile, save dirty work, or regenerate project files.",
                    FirstCalls = new[] { "UniBridge_ManageEditor Action=GetState", "UniBridge_ManageEditor Action=GetSelection", "UniBridge_ManageEditor Action=WaitForReady" },
                    EditCalls = new[] { "UniBridge_ManageEditor Action=SelectAsset AssetPath=<path>", "UniBridge_ManageEditor Action=SelectGameObject GameObjectPath=<hierarchy path>", "UniBridge_ManageEditor Action=SaveAll or SaveAssets", "UniBridge_ManageEditor Action=RequestScriptCompilationNoWait Force=true", "UniBridge_ManageEditor Action=WaitForReadyAfterReload", "UniBridge_ManageEditor Action=RequestPlayModeNoWait", "UniBridge_ManageEditor Action=ExitPlayMode WaitForCompletion=true" },
                    VerifyCalls = new[] { "UniBridge_ManageEditor Action=WaitForPlayMode", "UniBridge_ManageEditor Action=WaitForEditMode", "UniBridge_ManageEditor Action=GetState", "UniBridge_EditorEvents Action=Snapshot IncludeDiagnostics=true IncludeAssetChanges=true", "UniBridge_ExecutionStatus Action=Snapshot", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManageEditor", "UniBridge_EditorSnapshot", "UniBridge_EditorEvents", "UniBridge_ExecutionStatus", "UniBridge_ContextSnapshot", "UniBridge_SceneObjectView", "UniBridge_AssetIntelligence", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "editor", "manage_editor", "console" },
                    Notes = new[] { "Use RequestScriptCompilationNoWait followed by WaitForReadyAfterReload for script compilation; Unity domain reload can recreate the bridge, so avoid relying on an inline wait inside the old connection.", "Entering or leaving Play Mode is also a reload boundary in many projects. BatchActions stops after queued Play/ExitPlayMode and returns nextSuggestedCalls; run WaitForPlayMode/WaitForEditMode and console reads as a follow-up call.", "Use WaitForReady/WaitIdle after asset refreshes, script compilation, or play-mode transitions before interpreting console or scene state.", "Use EditorSnapshot before temporarily changing scenes, Prefab Mode, selection, active dock tabs, or focused windows, then Restore with DryRun first when appropriate.", "Use EditorEvents after refresh/compile/move operations to get structured selection, compilation, package, and asset delta history without polling a full ContextSnapshot.", "Use ExecutionStatus when a tool appears queued, blocked by an exclusive lease, or near its scheduler timeout.", "GetPlayModeState, ExitPlayMode, SaveAssets, GenerateSolutionFile, and WaitIdle exist as explicit lifecycle names for discoverability." },
                    Aliases = new[] { "editor", "selection", "ready", "compile", "save" }
                },
                new WorkflowGuide
                {
                    Key = "recipes",
                    Title = "Use complete workflow recipes",
                    When = "Use when the user asks for a normal Unity result rather than a low-level tool operation.",
                    FirstCalls = new[] { "UniBridge_WorkflowRecipes Action=List", "UniBridge_WorkflowRecipes Action=Describe Recipe=<name>", "UniBridge_WorkflowRecipes Action=BuildBatch Recipe=<name>", "UniBridge_WorkflowRecipes Action=BuildBatch Recipe=RunCoreSmokeTest" },
                    EditCalls = new[] { "UniBridge_WorkflowRecipes Action=DryRun Recipe=<name>", "UniBridge_WorkflowRecipes Action=Execute Recipe=<name> after a clean dry-run" },
                    VerifyCalls = new[] { "UniBridge_ReadConsole Action=DiagnosticSummary", "Domain inspect/capture tools returned by the recipe batch" },
                    Tools = new[] { "UniBridge_WorkflowRecipes", "UniBridge_BatchActions", "UniBridge_ReadConsole" },
                    BatchAliases = Array.Empty<string>(),
                    Notes = new[] { "Recipes expand into BatchActions payloads, so the agent can inspect the generated steps before execution.", "Use RunCoreSmokeTest, RunUISmokeTest, and RunAssetSmokeTest to verify a fresh UniBridge connection before complex work." },
                    Aliases = new[] { "workflow_recipes", "recipe", "recipes", "common_workflows", "self_test", "smoke" }
                },
                new WorkflowGuide
                {
                    Key = "scene_objects",
                    Title = "Inspect or change scene GameObjects",
                    When = "Use for hierarchy, components, transforms, prefabs in scenes, and object-level state.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[SceneObjects]", "UniBridge_SceneObjectView Detail=Standard or Detailed Profile=Rendering/Physics2D/UI/Animation/VFX/Audio when relevant", "UniBridge_SceneHierarchyExport for complete large-scene exports and objectId pagination", "UniBridge_TypeSchema InspectGameObject IncludeValues=true" },
                    EditCalls = new[] { "UniBridge_ScopedEdit for scene/prefab asset changes", "UniBridge_ManageSceneHierarchy DryRun=true for objectId-based batch reparent/container work", "UniBridge_ManageGameObject Action=Create/Modify/Delete/AddComponent using PascalCase parameters", "UniBridge_ManagePrefab when prefab assets/instances are involved", "UniBridge_BatchActions DryRun=true for multi-object changes" },
                    VerifyCalls = new[] { "UniBridge_SceneHierarchyExport CompareExports or Export after hierarchy moves", "UniBridge_SceneObjectView", "UniBridge_BehaviourContext IncludeSource=true for script-backed objects", "UniBridge_CaptureView", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_UnitySearch", "UniBridge_SceneObjectView", "UniBridge_SceneHierarchyExport", "UniBridge_ManageSceneHierarchy", "UniBridge_BehaviourContext", "UniBridge_TypeSchema", "UniBridge_ScopedEdit", "UniBridge_ManageGameObject", "UniBridge_ManagePrefab", "UniBridge_CaptureView", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "game_object", "go", "prefab", "scene_view", "scene_hierarchy_export", "batch_reparent", "script_context", "scope", "scoped_edit", "capture" },
                    Notes = new[] { "Use objectId for duplicate names and large-scene edits; hierarchy paths are still useful for human review.", "For 100+ object scenes, export the full hierarchy to JSON/JSONL before reorganizing so no object is hidden by MaxObjects limits.", "Use ManageSceneHierarchy dry-run before any batch reparent; execution preserves world transforms, returns before/after diff, and validates object count.", "For asset-backed scene or prefab work, prefer ScopedEdit so the target scope is opened, edited, saved, and restored in one call.", "SceneObjectView Profile focuses component properties so agents can inspect rendering, physics, UI, animation, VFX, or audio state without dumping every component field.", "PascalCase fields are canonical for new agents; legacy snake_case still works through the normalizer." },
                    Aliases = new[] { "scene", "objects", "hierarchy", "gameobject" }
                },
                new WorkflowGuide
                {
                    Key = "scoped_editing",
                    Title = "Edit a specific scene or prefab asset",
                    When = "Use when the user names a .unity or .prefab asset and the agent must avoid accidentally editing the wrong open context.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[Assets] for the .unity/.prefab path", "UniBridge_ScopedEdit DryRun=true ScopePath=<asset> Steps=[...]" },
                    EditCalls = new[] { "UniBridge_ScopedEdit DryRun=false SaveScope=true RestoreEditorState=true after a clean dry-run" },
                    VerifyCalls = new[] { "UniBridge_SceneObjectView ScenePath=<scene> or prefab inspection", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ScopedEdit", "UniBridge_BatchActions", "UniBridge_ManageGameObject", "UniBridge_ManagePrefab", "UniBridge_SceneObjectView", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "scoped_edit", "scope", "game_object", "prefab", "scene_view" },
                    Notes = new[] { "ScopedEdit wraps BatchActions inside a scene or prefab scope and restores the previous editor state.", "Keep nested steps domain-specific and dry-run before saving scope assets." },
                    Aliases = new[] { "scope", "scoped", "scene_scope", "prefab_scope" }
                },
                new WorkflowGuide
                {
                    Key = "behaviour_context",
                    Title = "Inspect attached script context",
                    When = "Use when gameplay debugging needs the selected GameObject's MonoBehaviours, source snippets, and serialized field values in one compact response.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[SceneObjects]", "UniBridge_BehaviourContext Target=<object> IncludeSource=true IncludeSerializedFields=true" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "UniBridge_ScriptIntelligence Inspect for deeper source context", "UniBridge_TypeSchema InspectGameObject IncludeValues=true", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_BehaviourContext", "UniBridge_UnitySearch", "UniBridge_ScriptIntelligence", "UniBridge_TypeSchema", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "behaviour_context", "behavior_context", "script_context", "attached_scripts" },
                    Notes = new[] { "Use BehaviourContext before editing scripts when the target object has several custom components or serialized references." },
                    Aliases = new[] { "behaviour", "behavior", "monobehaviour", "attached_scripts", "script_context" }
                },
                new WorkflowGuide
                {
                    Key = "tilemap2d",
                    Title = "Author 2D Tilemaps",
                    When = "Use for Grid creation, Tilemap layers, Tile assets from sprites, paint/erase cells, bounds compression, and collider setup.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[Assets,SceneObjects] for sprites or existing tilemaps", "UniBridge_ManageTilemap2D Action=Inspect" },
                    EditCalls = new[] { "UniBridge_ManageTilemap2D CreateGrid/CreateLayer/CreateTileAsset", "UniBridge_ManageTilemap2D PaintCells or EraseCells", "UniBridge_BatchActions DryRun=true for multi-layer tilemap changes" },
                    VerifyCalls = new[] { "UniBridge_ManageTilemap2D Action=Inspect IncludeCells=true", "UniBridge_CaptureView", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManageTilemap2D", "UniBridge_UnitySearch", "UniBridge_CaptureView", "UniBridge_BatchActions", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "tilemap", "tilemap2d", "tiles", "tile", "grid2d" },
                    Notes = new[] { "Create tile assets from project sprites before painting cells.", "Use CompressBounds after painting sparse maps." },
                    Aliases = new[] { "tilemap", "tiles", "grid", "tilemap_2d" }
                },
                new WorkflowGuide
                {
                    Key = "input_actions",
                    Title = "Author Input System actions",
                    When = "Use for .inputactions files, action maps, actions, bindings, control schemes, PlayerInput, UI input modules, on-screen controls, virtual mouse, and multiplayer event systems.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[Assets,SceneObjects] for existing .inputactions or player objects", "UniBridge_ManageInputActions Action=Inspect Path=<asset>" },
                    EditCalls = new[] { "UniBridge_ManageInputActions CreateOrUpdate/AddMap/AddAction/AddBinding/AddControlScheme", "UniBridge_ManageInputActions WirePlayerInput/WirePlayerInputManager/WireUIInputModule/AddOnScreenButton/AddOnScreenStick when Input System is installed" },
                    VerifyCalls = new[] { "UniBridge_ManageInputActions Inspect IncludeJson=true", "UniBridge_BehaviourContext on the player object if custom scripts consume actions", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManageInputActions", "UniBridge_UnitySearch", "UniBridge_BehaviourContext", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "input_actions", "inputactions", "input", "player_input", "playerinput" },
                    Notes = new[] { "The asset authoring path writes .inputactions JSON without hard-compiling UniBridge against Input System assemblies.", "Scene wiring actions return a clear error if the Input System package is absent." },
                    Aliases = new[] { "input", "input_system", "inputactions", "controls" }
                },
                new WorkflowGuide
                {
                    Key = "constraints",
                    Title = "Author animation constraints",
                    When = "Use for Parent, Position, Rotation, Scale, Aim, and LookAt constraints that make one object follow or aim at another.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[SceneObjects]", "UniBridge_ManageConstraints Action=Inspect" },
                    EditCalls = new[] { "UniBridge_ManageConstraints AddConstraint/SetSources/ApplyPreset", "UniBridge_BatchActions DryRun=true for multi-object rig setup" },
                    VerifyCalls = new[] { "UniBridge_ManageConstraints Inspect", "UniBridge_SceneObjectView Profile=Animation", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManageConstraints", "UniBridge_UnitySearch", "UniBridge_SceneObjectView", "UniBridge_BatchActions", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "constraints", "constraint", "animation_constraints", "parent_constraint", "position_constraint", "rotation_constraint", "lookat_constraint" },
                    Notes = new[] { "Use Sources arrays when several transforms influence one target, with per-source weights." },
                    Aliases = new[] { "constraints", "constraint", "parent_constraint", "lookat", "aim" }
                },
                new WorkflowGuide
                {
                    Key = "timeline",
                    Title = "Author Timeline and PlayableDirector data",
                    When = "Use for TimelineAsset creation, tracks, default clips, PlayableDirector setup, and track bindings.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[Assets,SceneObjects]", "UniBridge_ManageTimeline Action=Inspect Path=<timeline>" },
                    EditCalls = new[] { "UniBridge_ManageTimeline CreateAsset/AddTrack/AddClip", "UniBridge_ManageTimeline CreateDirector/BindDirector", "UniBridge_BatchActions DryRun=true for sequenced setup" },
                    VerifyCalls = new[] { "UniBridge_ManageTimeline Inspect", "UniBridge_SceneObjectView Profile=VideoTimeline or Animation", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManageTimeline", "UniBridge_UnitySearch", "UniBridge_SceneObjectView", "UniBridge_BatchActions", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "timeline", "playable", "playable_director", "cinematic" },
                    Notes = new[] { "Timeline operations use reflection so projects without Timeline still compile and return a focused runtime error." },
                    Aliases = new[] { "timeline", "playable", "cinematic", "sequencer" }
                },
                new WorkflowGuide
                {
                    Key = "physics2d",
                    Title = "Author Physics2D prototypes",
                    When = "Use for Rigidbody2D, Collider2D, Joint2D, Effector2D, and PhysicsMaterial2D presets.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[SceneObjects,Assets]", "UniBridge_ManagePhysics2D Action=Inspect Target=<object>" },
                    EditCalls = new[] { "UniBridge_ManagePhysics2D ApplyPreset/AddRigidbody/AddCollider/AddJoint/AddEffector/CreateMaterial", "UniBridge_BatchActions DryRun=true for multi-object physics setup" },
                    VerifyCalls = new[] { "UniBridge_ManagePhysics2D Inspect", "UniBridge_SceneObjectView Profile=Physics2D", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManagePhysics2D", "UniBridge_UnitySearch", "UniBridge_SceneObjectView", "UniBridge_BatchActions", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "physics2d", "2d_physics", "collider2d", "rigidbody2d", "joint2d", "effector2d" },
                    Notes = new[] { "Use named presets for fast prototypes, then inspect exact component values before handing off gameplay logic." },
                    Aliases = new[] { "physics2d", "2d_physics", "collider", "rigidbody", "joint" }
                },
                new WorkflowGuide
                {
                    Key = "physics3d",
                    Title = "Author Physics3D prototypes",
                    When = "Use for Rigidbody, Collider, Joint, CharacterController, and PhysicsMaterial presets.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=Physics3D", "UniBridge_UnitySearch Sources=[SceneObjects,Assets]", "UniBridge_ManagePhysics3D Action=Inspect Target=<object>" },
                    EditCalls = new[] { "UniBridge_ManagePhysics3D ApplyPreset/AddRigidbody/AddCollider/AddJoint/AddCharacterController/CreateMaterial", "UniBridge_BatchActions DryRun=true for multi-object physics setup" },
                    VerifyCalls = new[] { "UniBridge_ManagePhysics3D Inspect", "UniBridge_SceneObjectView Profile=Physics3D", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_DomainCatalog", "UniBridge_ManagePhysics3D", "UniBridge_UnitySearch", "UniBridge_SceneObjectView", "UniBridge_BatchActions", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "physics3d", "3d_physics", "collider3d", "rigidbody3d", "joint3d" },
                    Notes = new[] { "Use named presets for first-pass gameplay feel, then inspect exact collider/material values.", "MeshCollider convex state matters before adding a dynamic Rigidbody." },
                    Aliases = new[] { "physics3d", "3d_physics", "rigidbody3d", "collider3d", "physic_material" }
                },
                new WorkflowGuide
                {
                    Key = "navigation",
                    Title = "Author Navigation and NavMesh data",
                    When = "Use for NavMeshAgent, NavMeshObstacle, OffMeshLink, and optional AI Navigation surface/modifier setup.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=Navigation", "UniBridge_UnitySearch Sources=[SceneObjects]", "UniBridge_ManageNavigation Action=Inspect" },
                    EditCalls = new[] { "UniBridge_ManageNavigation AddAgent/AddObstacle/AddOffMeshLink/AddSurface/AddModifier/BakeSurface", "UniBridge_BatchActions DryRun=true for multi-object navigation setup" },
                    VerifyCalls = new[] { "UniBridge_ManageNavigation Inspect", "UniBridge_SceneObjectView Profile=Navigation", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_DomainCatalog", "UniBridge_ManageNavigation", "UniBridge_UnitySearch", "UniBridge_SceneObjectView", "UniBridge_BatchActions", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "navigation", "nav", "navmesh", "navmesh_agent", "navmesh_surface" },
                    Notes = new[] { "Surface/modifier/link components are optional package types and return focused unavailable messages when the package is absent." },
                    Aliases = new[] { "navigation", "navmesh", "nav", "ai" }
                },
                new WorkflowGuide
                {
                    Key = "rendering",
                    Title = "Author cameras, lights, and render presets",
                    When = "Use when a scene needs a usable camera, readable lighting, 2D pixel-perfect setup, 2D lights/shadows, render settings, Volume, or preview rig before capture.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=Rendering", "UniBridge_SceneObjectView Profile=Rendering", "UniBridge_ManageRendering Action=Inspect" },
                    EditCalls = new[] { "UniBridge_ManageRendering ApplyPreset/CreateCamera/CreateLight/CreateVolume/CreateRig/SetupSceneLighting/Setup2DScene/AddLight2D/AddShadowCaster2D/AddDecalProjector/AddLensFlare/AddWindZone/AddProjector/AddLODGroup/AddReflectionProbe/AddLightProbeGroup/AddLightProbeProxyVolume", "UniBridge_ManageMaterial or ManageShader for material-specific work" },
                    VerifyCalls = new[] { "UniBridge_ManageRendering Inspect", "UniBridge_CaptureView", "UniBridge_VisualSceneAudit Action=AuditCapture", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_DomainCatalog", "UniBridge_ManageRendering", "UniBridge_ManageMaterial", "UniBridge_ManageShader", "UniBridge_SceneObjectView", "UniBridge_CaptureView", "UniBridge_VisualSceneAudit", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "rendering", "camera", "lighting", "lights", "volume", "render_settings" },
                    Notes = new[] { "Use rendering presets to make scene captures meaningful before tuning materials or gameplay composition.", "After visible scene, material, camera, or lighting work, run VisualSceneAudit before reporting success so obvious visual failures are surfaced while the agent can still fix them.", "ManageRendering accepts RenderingLayerMask as an integer, Everything/Nothing, a rendering layer name, or an array/object of names for light, renderer, decal, and reflection probe authoring.", "Use AddDecalProjector/AddLensFlare/AddFlareLayer/AddWindZone/AddProjector/AddLODGroup/AddReflectionProbe/AddLightProbeGroup/AddLightProbeProxyVolume for rendering extras instead of generic AddComponent plus hand-written SerializedProperty patches.", "SceneObjectView and AssetIntelligence expose renderer sorting/rendering layer masks as named context, not just raw bit fields.", "Optional package/module components are applied through reflection and return clear unavailable messages when absent." },
                    Aliases = new[] { "rendering", "render", "camera", "lighting", "postprocessing" }
                },
                new WorkflowGuide
                {
                    Key = "uitoolkit",
                    Title = "Author UI Toolkit UXML and USS",
                    When = "Use for VisualTreeAsset/UXML, USS, PanelSettings, UIDocument wiring, and UI Toolkit capture.",
                    FirstCalls = new[] { "UniBridge_DomainCatalog Domain=UIToolkit", "UniBridge_CaptureUIToolkit Action=ListUxml", "UniBridge_UnitySearch Sources=[Assets,SceneObjects]" },
                    EditCalls = new[] { "UniBridge_ManageUIToolkit CreateDocument/CreateStyleSheet/CreatePanelSettings/AttachDocument/AddElement/SetClasses/SetInlineStyle" },
                    VerifyCalls = new[] { "UniBridge_CaptureUIToolkit Inspect or Capture", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_DomainCatalog", "UniBridge_ManageUIToolkit", "UniBridge_CaptureUIToolkit", "UniBridge_AssetIntelligence", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "uitoolkit", "ui_toolkit", "uxml_authoring", "uss", "uidocument" },
                    Notes = new[] { "Create or patch UXML/USS assets, attach a UIDocument, then capture the resolved tree instead of guessing layout state." },
                    Aliases = new[] { "uitoolkit", "ui_toolkit", "uxml", "uss", "uidocument" }
                },
                new WorkflowGuide
                {
                    Key = "ui",
                    Title = "Build, inspect, or validate uGUI",
                    When = "Use for Canvas, RectTransform layout, panels, templates, buttons, text, and UI validation.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[SceneObjects] for existing UI roots", "UniBridge_ManageUI Inspect or ValidateUI", "UniBridge_TypeSchema for custom UI components" },
                    EditCalls = new[] { "UniBridge_ManageUI for templates/layout/widgets", "UniBridge_ManageUnityEvent for non-button events", "UniBridge_BatchActions DryRun=true for multi-step UI creation" },
                    VerifyCalls = new[] { "UniBridge_ManageUI ValidateUI", "UniBridge_CaptureView", "UniBridge_VisualSceneAudit Action=AuditCapture", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManageUI", "UniBridge_ManageUnityEvent", "UniBridge_WorkflowRecipes", "UniBridge_UnitySearch", "UniBridge_TypeSchema", "UniBridge_CaptureView", "UniBridge_VisualSceneAudit", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "ui", "layout", "canvas", "unity_event", "capture" },
                    Notes = new[] { "Prefer WorkflowRecipes or templates for whole screens and ValidateUI for overlaps, zero sizes, invisible elements, and text bounds." },
                    Aliases = new[] { "ugui", "canvas", "layout", "hud", "panel" }
                },
                new WorkflowGuide
                {
                    Key = "assets_import",
                    Title = "Inspect and tune asset import settings",
                    When = "Use for textures, sprites, models, audio, and importer serialized properties.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[Assets]", "UniBridge_AssetIntelligence Context for asset summary/read/serialize, Structure for prefab/loaded scene hierarchy, or ReferenceGraph for reference maps", "UniBridge_ManageAssetImporter Inspect IncludeSerializedProperties=true" },
                    EditCalls = new[] { "UniBridge_ManageAssetImporter SetProperties", "UniBridge_ManageAsset for folders/copy/move/delete/CreateOrUpdate allowlisted assets", "UniBridge_BatchActions DryRun=true for folder-wide edits" },
                    VerifyCalls = new[] { "UniBridge_ManageAssetImporter Inspect", "UniBridge_CaptureAsset", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_UnitySearch", "UniBridge_AssetIntelligence", "UniBridge_WorkflowRecipes", "UniBridge_ManageAssetImporter", "UniBridge_ManageAsset", "UniBridge_CaptureAsset", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "asset_importer", "importer", "import_settings", "asset_capture" },
                    Notes = new[] { "Use project context before deciding what optimal import settings mean.", "Use AssetIntelligence ContextProfile=Deep when an asset's importer, sub-assets, serialized fields, or text chunks are central to the task.", "Use AssetIntelligence Structure for prefab/scene hierarchy and serialized-field searches without reading a giant snapshot.", "Before move/rename/delete, call AssetIntelligence Impact or ReferenceGraph to see dependents and reference risk.", "ManageAsset CreateOrUpdate supports an allowlist: PhysicsMaterial2D, RenderTexture, TerrainLayer, AvatarMask, ShaderVariantCollection." },
                    Aliases = new[] { "import", "importer", "sprites", "textures", "assets", "asset_structure" }
                },
                new WorkflowGuide
                {
                    Key = "materials",
                    Title = "Create or tune materials and shaders",
                    When = "Use for material assets, shader assignment, colors, textures, and render previews.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[Assets,SceneObjects]", "UniBridge_AssetIntelligence Inspect", "UniBridge_TypeSchema for renderer/material-bearing components" },
                    EditCalls = new[] { "UniBridge_ManageMaterial", "UniBridge_ManageShader", "UniBridge_ManageGameObject for renderer assignment" },
                    VerifyCalls = new[] { "UniBridge_CaptureAsset for material previews", "UniBridge_CaptureView for scene result", "UniBridge_VisualSceneAudit Action=AuditCapture for placed scene result", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManageMaterial", "UniBridge_ManageShader", "UniBridge_WorkflowRecipes", "UniBridge_ManageGameObject", "UniBridge_AssetIntelligence", "UniBridge_CaptureAsset", "UniBridge_CaptureView", "UniBridge_VisualSceneAudit", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "material", "mat", "shader", "asset_capture", "capture" },
                    Notes = new[] { "Inspect shader property names before setting values." },
                    Aliases = new[] { "mat", "shader", "rendering" }
                },
                new WorkflowGuide
                {
                    Key = "scriptable_objects",
                    Title = "Create or patch ScriptableObject assets",
                    When = "Use for data/config assets and serialized-field editing.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[Assets,Scripts]", "UniBridge_TypeSchema for target type", "UniBridge_ManageScriptableObject Inspect" },
                    EditCalls = new[] { "UniBridge_ManageScriptableObject Create or SetProperties", "UniBridge_BatchActions DryRun=true for multiple data assets" },
                    VerifyCalls = new[] { "UniBridge_ManageScriptableObject Inspect IncludeValues=true", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManageScriptableObject", "UniBridge_WorkflowRecipes", "UniBridge_TypeSchema", "UniBridge_UnitySearch", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "scriptable_object", "so", "data_asset", "config_asset" },
                    Notes = new[] { "Serialized property paths from TypeSchema can be fed directly into SetProperties." },
                    Aliases = new[] { "scriptable", "so", "data", "config" }
                },
                new WorkflowGuide
                {
                    Key = "unity_events",
                    Title = "Inspect or bind UnityEvent persistent calls",
                    When = "Use for Button.onClick, Toggle/Slider events, custom UnityEvents, and serialized persistent listeners.",
                    FirstCalls = new[] { "UniBridge_TypeSchema or ManageUI Inspect to find event fields", "UniBridge_ManageUnityEvent Inspect" },
                    EditCalls = new[] { "UniBridge_ManageUnityEvent AddPersistentCall/SetPersistentCalls/ClearPersistentCalls", "UniBridge_ManageUI SetButtonEvent for simple Button.onClick" },
                    VerifyCalls = new[] { "UniBridge_ManageUnityEvent Inspect", "UniBridge_ManageUI Inspect for buttons", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManageUnityEvent", "UniBridge_ManageUI", "UniBridge_TypeSchema", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "unity_event", "event", "persistent_call", "ui" },
                    Notes = new[] { "For non-button UnityEvents use ManageUnityEvent rather than button-specific UI actions." },
                    Aliases = new[] { "events", "persistent_events", "button_events" }
                },
                new WorkflowGuide
                {
                    Key = "visual_capture",
                    Title = "Capture scene, Game view, asset, or UI Toolkit visuals",
                    When = "Use whenever the agent needs to look at Unity output rather than infer from data alone.",
                    FirstCalls = new[] { "UniBridge_CaptureView Action=CaptureGameView for exact Game View pixels, CaptureContactSheet for multi-angle scene inspection, or CaptureGameCamera for controlled camera render", "UniBridge_VisualSceneAudit Action=AuditCapture after visible work to catch obvious bad results before final reporting", "UniBridge_CaptureAsset Action=CaptureContactSheet for one asset from several angles, or CaptureGrid for several assets", "UniBridge_CaptureUIToolkit for UXML or UIDocument" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "Repeat capture after edits", "UniBridge_VisualSceneAudit Action=AuditCapture", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_CaptureView", "UniBridge_VisualSceneAudit", "UniBridge_CaptureAsset", "UniBridge_CaptureUIToolkit", "UniBridge_SceneObjectView", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "capture", "visual_audit", "self_check", "asset_capture", "uxml_capture", "scene_view" },
                    Notes = new[] { "Use CaptureGameView when the agent needs the exact post-render Game View, including overlays, post-processing, and Game tab presentation.", "Use VisualSceneAudit after visible work; it renders or analyzes a PNG, checks broad pixel failures, target framing, material/shader health, and console diagnostics.", "Use CaptureView CaptureContactSheet with Views=[Iso,Front,Top,Right] when one screenshot needs to expose 3D placement, scale, and occlusion from several angles.", "Use CaptureAsset CaptureContactSheet to inspect one prefab/material/mesh from multiple views before placing it in a scene.", "For UI Toolkit work, combine CaptureUIToolkit screenshots with returned layout issues such as zero-size elements, likely text overflow, and visible element overlap.", "For animated prefabs, particles, or VFX, pass AdvanceMs with SimulateParticles/SampleAnimations so the capture is not stuck on frame zero.", "For SRP-heavy camera or UI Toolkit renders where ReadPixels misses a result, pass ReadbackMode=GpuReadback and inspect readbackMode in the response." },
                    Aliases = new[] { "capture", "screenshot", "preview", "vision" }
                },
                new WorkflowGuide
                {
                    Key = "animator",
                    Title = "Inspect or edit Animator Controllers",
                    When = "Use for states, transitions, parameters, layers, blend trees, and controller assets.",
                    FirstCalls = new[] { "UniBridge_UnitySearch Sources=[Assets]", "UniBridge_ManageAnimatorController Inspect" },
                    EditCalls = new[] { "UniBridge_ManageAnimatorController", "UniBridge_BatchActions DryRun=true for graph changes" },
                    VerifyCalls = new[] { "UniBridge_ManageAnimatorController Inspect", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ManageAnimatorController", "UniBridge_UnitySearch", "UniBridge_BatchActions", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "animator", "animator_controller", "controller" },
                    Notes = new[] { "Dry-run graph edits before writing controller assets." },
                    Aliases = new[] { "animation", "controller", "animator_controller" }
                },
                new WorkflowGuide
                {
                    Key = "scripts",
                    Title = "Search, create, and safely edit scripts",
                    When = "Use for C# discovery and text changes that need SHA/precondition safety.",
                    FirstCalls = new[] { "UniBridge_ScriptIntelligence Search/Inspect", "UniBridge_GetSha before direct text edits" },
                    EditCalls = new[] { "UniBridge_CreateScript", "UniBridge_ApplyTextEdits", "UniBridge_DeleteScript" },
                    VerifyCalls = new[] { "UniBridge_ValidateScript IncludeDiagnostics=true", "UniBridge_ManageEditor Action=RefreshAssets WaitForCompletion=true", "UniBridge_ManageEditor Action=RequestScriptCompilationNoWait Force=true", "UniBridge_ManageEditor Action=WaitForReadyAfterReload", "UniBridge_ManageEditor Action=GetCompilationDiagnostics", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ScriptIntelligence", "UniBridge_ValidateScript", "UniBridge_GetSha", "UniBridge_ApplyTextEdits", "UniBridge_CreateScript", "UniBridge_DeleteScript", "UniBridge_ManageEditor", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "script_intelligence", "script_search", "validate_script", "script_validate", "cs_validation" },
                    Notes = new[] { "BatchActions supports read-only script validation; script text editing still uses dedicated SHA/precondition tools.", "For compile verification, prefer RequestScriptCompilationNoWait followed by WaitForReadyAfterReload, then read compilation diagnostics and console output." },
                    Aliases = new[] { "code", "csharp", "cs", "script" }
                },
                new WorkflowGuide
                {
                    Key = "batch",
                    Title = "Plan and execute multi-step Unity changes",
                    When = "Use when several related Unity tools should run as one validated workflow.",
                    FirstCalls = new[] { "UniBridge_BatchActions DryRun=true IncludeImpact=true", "Inspect validation errors/warnings and per-step impact plans before execution" },
                    EditCalls = new[] { "UniBridge_BatchActions DryRun=false IncludeConsoleDelta=true IncludeEditorEventDelta=true after a clean dry-run" },
                    VerifyCalls = new[] { "Inspect data.postActionDiagnostics.consoleDelta and editorEventDelta", "UniBridge_ReadConsole Action=DiagnosticSummary", "Specialized inspect/capture tools for the touched domain" },
                    Tools = new[] { "UniBridge_BatchActions", "UniBridge_WorkflowRecipes", "UniBridge_ReadConsole", ToolName },
                    BatchAliases = new[] { "game_object", "asset", "asset_importer", "material", "scriptable_object", "ui", "unity_event", "capture", "context", "editor", "runtime_profiler", "runtime_probe", "state_probe" },
                    Notes = new[] { "RollbackOnFailure defaults to true for execution; still keep batches small and domain-focused.", "Pass IncludeConsoleDelta=true to mark the console before execution and append only new compact console diagnostics.", "Pass IncludeEditorEventDelta=true to append bounded Unity editor event deltas for project/hierarchy/compile/play-mode changes.", "Impact includes per-step likely asset paths, project settings, scene object references, and validation provider names." },
                    Aliases = new[] { "transaction", "rollback", "dry_run", "multi_step" }
                },
                new WorkflowGuide
                {
                    Key = "runtime_profiler",
                    Title = "Inspect Play Mode runtime and profiler state",
                    When = "Use when runtime behavior, FPS, GC allocation, memory, rendering counters, physics spikes, or stutters need measured data instead of guesses.",
                    FirstCalls = new[] { "UniBridge_ManageEditor Action=GetPlayModeState", "UniBridge_RuntimeProfiler Action=Snapshot", "UniBridge_RuntimeProfiler Action=Metrics" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "UniBridge_RuntimeProfiler Action=Sample SampleFrames=120 Metrics=[main_thread_ms,gc_alloc_bytes,batches_count]", "UniBridge_ReadConsole Action=DiagnosticSummary", "UniBridge_CaptureView Action=CaptureGameView when visual context matters" },
                    Tools = new[] { "UniBridge_RuntimeProfiler", "UniBridge_ManageEditor", "UniBridge_ReadConsole", "UniBridge_CaptureView", "UniBridge_VisualSceneAudit" },
                    BatchAliases = new[] { "runtime_profiler", "runtime", "profiler", "performance", "fps", "gc_profile", "memory_profile" },
                    Notes = new[] { "RuntimeProfiler is read-only and uses bounded ProfilerRecorder sampling; it does not execute arbitrary C# in the project.", "Action=Sample requires Play Mode by default; pass RequirePlayMode=false only for editor-time sampling.", "Full raw samples are saved under Library/UniBridge/RuntimeProfiler when SaveToFile=true, while the MCP response stays compact." },
                    Aliases = new[] { "runtime", "profiler", "performance", "fps", "gc", "memory", "spikes", "stutter" }
                },
                new WorkflowGuide
                {
                    Key = "runtime_state_probe",
                    Title = "Probe live component state",
                    When = "Use when a gameplay bug depends on MonoBehaviour flags, counters, references, positions, trigger state, animation state fields, or other component values over several frames, or when a workflow needs pass/fail checks for those values.",
                    FirstCalls = new[] { "UniBridge_ManageEditor Action=GetPlayModeState", "UniBridge_RuntimeStateProbe Action=ListMembers Component=<ComponentOrMonoBehaviour>", "UniBridge_RuntimeStateProbe Action=Snapshot Target=<objectPathOrId> Component=<component> Members=[fieldOrProperty]" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "UniBridge_RuntimeStateProbe Action=Sample Target=<objectPathOrId> Component=<component> Members=[fieldOrProperty] SampleFrames=30", "UniBridge_RuntimeStateProbe Action=Assert Target=<objectPathOrId> Component=<component> Assertions=[{member:'field',operator:'==',value:true}]", "UniBridge_RuntimeProfiler Action=Sample SampleFrames=60 Metrics=[main_thread_ms,gc_alloc_bytes]", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_RuntimeStateProbe", "UniBridge_RuntimeProfiler", "UniBridge_SceneObjectView", "UniBridge_UnitySearch", "UniBridge_ManageEditor", "UniBridge_ReadConsole" },
                    BatchAliases = new[] { "runtime_probe", "runtime_state_probe", "state_probe", "watch_state", "watch_variables", "watch_assert", "runtime_assert", "component_state", "monobehaviour_state", "runtime_fields" },
                    Notes = new[] { "RuntimeStateProbe is read-only and samples SerializedProperty plus reflected fields/properties; it does not execute arbitrary C# in the project.", "Action=Assert evaluates simple rules such as equals, greater/less, between, contains, regex matches, changed, stable, isNull, and notNull. Required assertion failures return success=false by default so BatchActions can stop safely.", "Target lookup uses the shared scene resolver, so inactive objects, Prefab Stage objects, instance IDs, hierarchy paths, component short/full names, MonoScript GUIDs, and serialized editor class identifiers are supported.", "Action=Sample and Action=Assert require Play Mode by default; pass RequirePlayMode=false only for editor-time smoke tests. Full raw samples are saved under Library/UniBridge/RuntimeStateProbe when SaveToFile=true." },
                    Aliases = new[] { "state_probe", "runtime_state", "watch", "assert", "expect", "variables", "fields", "monobehaviour", "component_state" }
                },
                new WorkflowGuide
                {
                    Key = "editor_events",
                    Title = "Track live editor deltas",
                    When = "Use when the agent needs to know what changed since the last action: selection, asset imports/deletes/moves, package changes, compilation messages, hierarchy, play mode, or object changes.",
                    FirstCalls = new[] { "UniBridge_EditorEvents Action=Snapshot SinceId=0 IncludeSelection=true IncludeDiagnostics=true IncludeAssetChanges=true" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "UniBridge_WaitForEvent WaitFor=<condition> SinceId=<previous latestId>", "UniBridge_EditorEvents Action=Snapshot SinceId=<previous latestId>", "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_EditorEvents", "UniBridge_WaitForEvent", "UniBridge_ReadConsole", "UniBridge_ContextSnapshot", "UniBridge_AssetIntelligence" },
                    BatchAliases = Array.Empty<string>(),
                    Notes = new[] { "Use the returned latestId as SinceId for the next poll.", "Use WaitForEvent instead of blind sleeps; it observes events without holding the mutating execution gate.", "Asset move/delete/import deltas are a cue to refresh AssetIntelligence reference graphs before destructive asset work.", "Compiler diagnostics include file, line, column, assembly path, and severity when Unity reports them." },
                    Aliases = new[] { "events", "deltas", "wait", "selection_events", "asset_events", "compile_events" }
                },
                new WorkflowGuide
                {
                    Key = "console",
                    Title = "Monitor Unity diagnostics",
                    When = "Use before/after refreshes, scene edits, asset imports, script changes, and visual validation.",
                    FirstCalls = new[] { "UniBridge_ReadConsole Action=DiagnosticSummary", "UniBridge_ReadConsole Action=Read when detailed log lines are needed" },
                    EditCalls = Array.Empty<string>(),
                    VerifyCalls = new[] { "UniBridge_ReadConsole Action=DiagnosticSummary" },
                    Tools = new[] { "UniBridge_ReadConsole", "UniBridge_ContextSnapshot" },
                    BatchAliases = Array.Empty<string>(),
                    Notes = new[] { "A clean feature smoke should end with 0 warnings, errors, exceptions, and asserts unless intentionally testing failures." },
                    Aliases = new[] { "logs", "diagnostics", "errors", "warnings" }
                }
            };
        }

        sealed class WorkflowGuide
        {
            public string Key;
            public string Title;
            public string When;
            public string[] FirstCalls = Array.Empty<string>();
            public string[] EditCalls = Array.Empty<string>();
            public string[] VerifyCalls = Array.Empty<string>();
            public string[] Tools = Array.Empty<string>();
            public string[] BatchAliases = Array.Empty<string>();
            public string[] Notes = Array.Empty<string>();
            public string[] Aliases = Array.Empty<string>();
        }
    }
}
