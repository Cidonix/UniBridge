#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Manages Unity Animator Controller assets through UnityEditor.Animations APIs.
    /// </summary>
    public static class ManageAnimatorController
    {
        const string ToolName = "UniBridge_ManageAnimatorController";
        const int DefaultLimit = 50;
        const int MaxLimit = 500;

        public const string Title = "Manage animator controllers";

        public const string Description = @"Inspect and edit Unity Animator Controller assets.

Use this for Mecanim controller workflows: list controllers, inspect layers/parameters/states/transitions, create a controller, add/remove parameters, add/remove layers, add/remove states and sub-state machines, set state motion, create/configure BlendTrees, add/replace BlendTree children, set default state, add state/Any State/Entry transitions, validate controller structure, or apply a full controller graph from one structured request.

This tool uses UnityEditor.Animations APIs instead of editing controller YAML directly.

Args:
    action: search, inspect, validate, create, apply_graph, add_parameter, remove_parameter, add_layer, remove_layer, add_state_machine, remove_state_machine, add_state, remove_state, set_state_motion, create_blend_tree, configure_blend_tree, add_blend_child, set_blend_children, clear_blend_children, set_default_state, add_transition, add_entry_transition, remove_transition, or remove_entry_transition.
    path/controller_path: Assets/... .controller path.
    query: Optional search/filter text for search.
    layer/layer_index: Layer name or index. Defaults to Base Layer / 0 where applicable.
    state_machine/parent_state_machine, state, from_state, to_state, destination_state_machine: Animator state machine and state names. Nested names can use Path/Like/This.
    motion_path: Assets/... path to an AnimationClip or BlendTree-backed Motion.
    parameter/type/default_*: Animator parameter data.
    graph: Full graph definition for apply_graph. Supports parameters, layers, states, blend trees, default states, and transitions.
    blend_type/blend_parameter/blend_parameter_y: BlendTree configuration.
    children: Array of BlendTree child definitions: { motion_path, threshold, position, time_scale, cycle_offset, mirror, direct_blend_parameter }.
    conditions: Array of transition conditions: { parameter, mode, threshold }.

Returns:
    success, message, and controller-specific data such as controller snapshots, created paths, modified layers/states/transitions, or validation issues.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    action = new
                    {
                        type = "string",
                        description = "Animator Controller operation",
                        @enum = new[]
                        {
                            "search", "inspect", "validate", "create", "apply_graph",
                            "add_parameter", "remove_parameter",
                            "add_layer", "remove_layer",
                            "add_state_machine", "remove_state_machine",
                            "add_state", "remove_state",
                            "set_state_motion",
                            "create_blend_tree", "configure_blend_tree",
                            "add_blend_child", "set_blend_children", "clear_blend_children",
                            "set_default_state",
                            "add_transition", "add_entry_transition",
                            "remove_transition", "remove_entry_transition"
                        }
                    },
                    path = new { type = "string", description = "Animator Controller path, usually Assets/... .controller" },
                    controller_path = new { type = "string", description = "Alias for path" },
                    query = new { type = "string", description = "Search/filter text" },
                    limit = new { type = "integer", description = "Maximum search results", @default = DefaultLimit },
                    graph = new { type = "object", description = "Full Animator Controller graph definition for apply_graph", additionalProperties = true },
                    create_if_missing = new { type = "boolean", description = "For apply_graph, create the controller asset if it does not exist", @default = true },
                    dry_run = new { type = "boolean", description = "For apply_graph, validate and report planned changes without modifying assets", @default = false },
                    replace_transitions = new { type = "boolean", description = "For apply_graph, remove matching existing transitions before applying transition specs", @default = true },
                    remove_missing_parameters = new { type = "boolean", description = "For apply_graph, remove parameters not present in the graph spec", @default = false },
                    remove_missing_layers = new { type = "boolean", description = "For apply_graph, remove non-base layers not present in the graph spec", @default = false },
                    remove_missing_states = new { type = "boolean", description = "For apply_graph, remove states not present in each layer spec", @default = false },
                    name = new { type = "string", description = "Controller, layer, state, or parameter name depending on action" },
                    layer = new { type = "string", description = "Layer name. Defaults to Base Layer when omitted" },
                    layer_index = new { type = "integer", description = "Layer index. Takes precedence over layer when provided" },
                    weight = new { type = "number", description = "Layer default weight" },
                    blending_mode = new { type = "string", description = "Layer blending mode: Override or Additive" },
                    avatar_mask = new { type = "string", description = "AvatarMask asset path for the layer" },
                    ik_pass = new { type = "boolean", description = "Layer IK pass setting" },
                    synced_layer_index = new { type = "integer", description = "Layer synced layer index" },
                    synced_layer_affects_timing = new { type = "boolean", description = "Layer syncedLayerAffectsTiming setting" },
                    state_machine = new { type = "string", description = "State machine name or path" },
                    parent_state_machine = new { type = "string", description = "Parent state machine name/path for sub-state-machine operations" },
                    destination_state_machine = new { type = "string", description = "Destination state machine name/path for transitions" },
                    state = new { type = "string", description = "State name or state path" },
                    from_state = new { type = "string", description = "Source state for transitions" },
                    to_state = new { type = "string", description = "Destination state for transitions" },
                    motion_path = new { type = "string", description = "AnimationClip or Motion asset path" },
                    blend_type = new { type = "string", description = "BlendTree type: Simple1D, SimpleDirectional2D, FreeformDirectional2D, FreeformCartesian2D, or Direct" },
                    blend_parameter = new { type = "string", description = "Primary Animator parameter used by the BlendTree" },
                    blend_parameter_y = new { type = "string", description = "Secondary Animator parameter used by 2D BlendTrees" },
                    use_automatic_thresholds = new { type = "boolean", description = "BlendTree automatic threshold mode" },
                    min_threshold = new { type = "number", description = "BlendTree minimum threshold" },
                    max_threshold = new { type = "number", description = "BlendTree maximum threshold" },
                    replace_existing = new { type = "boolean", description = "For create_blend_tree, reuse or replace an existing state motion when possible" },
                    children = new
                    {
                        type = "array",
                        description = "BlendTree child motions. motion_path is optional for placeholder children.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                motion_path = new { type = "string" },
                                threshold = new { type = "number" },
                                position = new { type = "array", items = new { type = "number" }, minItems = 2, maxItems = 2 },
                                time_scale = new { type = "number" },
                                cycle_offset = new { type = "number" },
                                mirror = new { type = "boolean" },
                                direct_blend_parameter = new { type = "string" }
                            }
                        }
                    },
                    position = new { type = "array", description = "Graph position [x,y,z] for new states", items = new { type = "number" }, minItems = 2, maxItems = 3 },
                    speed = new { type = "number", description = "State playback speed" },
                    cycle_offset = new { type = "number", description = "State cycle offset" },
                    mirror = new { type = "boolean", description = "State mirror setting" },
                    ik_on_feet = new { type = "boolean", description = "State foot IK setting" },
                    write_default_values = new { type = "boolean", description = "State writeDefaultValues setting" },
                    tag = new { type = "string", description = "State tag" },
                    parameter = new { type = "string", description = "Animator parameter name" },
                    type = new { type = "string", description = "Parameter type: Float, Int, Bool, or Trigger" },
                    default_float = new { type = "number", description = "Default float parameter value" },
                    default_int = new { type = "integer", description = "Default int parameter value" },
                    default_bool = new { type = "boolean", description = "Default bool parameter value" },
                    has_exit_time = new { type = "boolean", description = "Transition hasExitTime" },
                    exit_time = new { type = "number", description = "Transition exitTime" },
                    duration = new { type = "number", description = "Transition duration" },
                    offset = new { type = "number", description = "Transition offset" },
                    has_fixed_duration = new { type = "boolean", description = "Transition fixed duration setting" },
                    interruption_source = new { type = "string", description = "Transition interruption source: None, Source, Destination, SourceThenDestination, or DestinationThenSource" },
                    ordered_interruption = new { type = "boolean", description = "Transition orderedInterruption setting" },
                    can_transition_to_self = new { type = "boolean", description = "Any State transition canTransitionToSelf setting" },
                    mute = new { type = "boolean", description = "Transition mute setting" },
                    solo = new { type = "boolean", description = "Transition solo setting" },
                    entry = new { type = "boolean", description = "Create/remove Entry transition in transition flows" },
                    any_state = new { type = "boolean", description = "Create/remove Any State transition" },
                    to_exit = new { type = "boolean", description = "Create transition from state to Exit" },
                    conditions = new
                    {
                        type = "array",
                        description = "Transition conditions",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                parameter = new { type = "string" },
                                mode = new { type = "string" },
                                threshold = new { type = "number" }
                            }
                        }
                    }
                },
                required = new[] { "action" },
                additionalProperties = false
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "assets", "animation" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return Response.Error("Parameters cannot be null.");

            var action = GetString(@params, "action", "Action")?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
                return Response.Error("'action' parameter is required.");

            try
            {
                return action switch
                {
                    "search" or "list" => Search(@params),
                    "inspect" or "get_info" or "getinfo" => Inspect(@params),
                    "validate" => Validate(@params),
                    "create" => Create(@params),
                    "apply_graph" or "applygraph" or "create_or_update_graph" or "createorupdategraph" => ApplyGraph(@params),
                    "add_parameter" or "addparameter" => AddParameter(@params),
                    "remove_parameter" or "removeparameter" => RemoveParameter(@params),
                    "add_layer" or "addlayer" => AddLayer(@params),
                    "remove_layer" or "removelayer" => RemoveLayer(@params),
                    "add_state_machine" or "addstatemachine" or "add_sub_state_machine" or "addsubstatemachine" => AddStateMachine(@params),
                    "remove_state_machine" or "removestatemachine" or "remove_sub_state_machine" or "removesubstatemachine" => RemoveStateMachine(@params),
                    "add_state" or "addstate" => AddState(@params),
                    "remove_state" or "removestate" => RemoveState(@params),
                    "set_state_motion" or "setstatemotion" => SetStateMotion(@params),
                    "create_blend_tree" or "createblendtree" => CreateBlendTree(@params),
                    "configure_blend_tree" or "configureblendtree" => ConfigureBlendTree(@params),
                    "add_blend_child" or "addblendchild" => AddBlendChild(@params),
                    "set_blend_children" or "setblendchildren" => SetBlendChildren(@params),
                    "clear_blend_children" or "clearblendchildren" => ClearBlendChildren(@params),
                    "set_default_state" or "setdefaultstate" => SetDefaultState(@params),
                    "add_entry_transition" or "addentrytransition" => AddEntryTransition(@params),
                    "add_transition" or "addtransition" => AddTransition(@params),
                    "remove_entry_transition" or "removeentrytransition" => RemoveEntryTransition(@params),
                    "remove_transition" or "removetransition" => RemoveTransition(@params),
                    _ => Response.Error($"Unknown action: '{action}'. Supported actions: search, inspect, validate, create, apply_graph, add_parameter, remove_parameter, add_layer, remove_layer, add_state_machine, remove_state_machine, add_state, remove_state, set_state_motion, create_blend_tree, configure_blend_tree, add_blend_child, set_blend_children, clear_blend_children, set_default_state, add_transition, add_entry_transition, remove_transition, remove_entry_transition.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageAnimatorController] Action '{action}' failed: {ex}");
                return Response.Error($"Animator Controller action '{action}' failed: {ex.Message}");
            }
        }

        static object Search(JObject @params)
        {
            var query = GetString(@params, "query", "Query", "search", "Search");
            var limit = Clamp(GetInt(@params, DefaultLimit, "limit", "Limit"), 1, MaxLimit);
            var paths = AssetDatabase.FindAssets("t:AnimatorController")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(path => string.IsNullOrWhiteSpace(query) ||
                               path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               Path.GetFileNameWithoutExtension(path).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(path => BuildControllerSearchResult(path))
                .ToList();

            return Response.Success($"Found {paths.Count} Animator Controller asset(s).", new
            {
                action = "search",
                query,
                returned = paths.Count,
                controllers = paths
            });
        }

        static object Inspect(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            return Response.Success($"Inspected Animator Controller '{path}'.", new
            {
                action = "inspect",
                controller = BuildControllerSnapshot(controller, path)
            });
        }

        static object Validate(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var issues = BuildValidationIssues(controller, path);
            return Response.Success($"Validated Animator Controller '{path}'.", new
            {
                action = "validate",
                valid = issues.All(issue => GetAnonymousString(issue, "severity") != "error"),
                issueCount = issues.Count,
                issues,
                controller = BuildControllerSummary(controller, path)
            });
        }

        static object Create(JObject @params)
        {
            var path = NormalizeControllerPath(GetString(@params, "path", "controller_path", "controllerPath"));
            EnsureWritableControllerPath(path);
            EnsureParentDirectoryExists(path);

            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
                return Response.Error($"Animator Controller already exists at '{path}'.");

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            if (controller == null)
                return Response.Error($"Failed to create Animator Controller at '{path}'.");

            ApplyLayerSettings(controller, 0, @params);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);

            return Response.Success($"Created Animator Controller '{path}'.", new
            {
                action = "create",
                controller = BuildControllerSnapshot(controller, path)
            });
        }

        static object ApplyGraph(JObject @params)
        {
            var path = NormalizeControllerPath(GetString(@params, "path", "controller_path", "controllerPath"));
            EnsureWritableControllerPath(path);

            var graph = GetGraphSpec(@params);
            var options = GraphApplyOptions.From(@params);
            var report = new GraphApplyReport();
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

            if (controller == null && !options.CreateIfMissing)
                return Response.Error($"Animator Controller was not found at '{path}' and create_if_missing is false.");

            var preflightErrors = ValidateGraphPreflight(controller, graph);
            if (preflightErrors.Count > 0)
            {
                return Response.Error("Animator graph validation failed.", new
                {
                    action = "apply_graph",
                    path,
                    errors = preflightErrors
                });
            }

            if (options.DryRun)
            {
                PlanGraphChanges(controller, graph, path, options, report);
                return Response.Success($"Animator graph dry-run completed for '{path}'.", new
                {
                    action = "apply_graph",
                    dryRun = true,
                    path,
                    report = report.ToObject(),
                    controllerExists = controller != null
                });
            }

            if (controller == null)
            {
                EnsureParentDirectoryExists(path);
                controller = AnimatorController.CreateAnimatorControllerAtPath(path);
                if (controller == null)
                    return Response.Error($"Failed to create Animator Controller at '{path}'.");

                report.Add("created", "controller", path, Path.GetFileNameWithoutExtension(path), null);
            }
            else
            {
                Undo.RegisterCompleteObjectUndo(controller, "Apply Animator Graph");
                report.Add("updated", "controller", path, controller.name, null);
            }

            ApplyGraphParameters(controller, graph, options, report);
            ApplyGraphLayers(controller, graph, options, report);

            SaveController(controller, path);
            var issues = BuildValidationIssues(controller, path);
            var valid = issues.All(issue => GetAnonymousString(issue, "severity") != "error");

            return Response.Success($"Applied Animator graph to '{path}'.", new
            {
                action = "apply_graph",
                dryRun = false,
                valid,
                issueCount = issues.Count,
                issues,
                report = report.ToObject(),
                controller = BuildControllerSnapshot(controller, path)
            });
        }

        static object AddParameter(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var name = RequireString(@params, "parameter", "name");
            var type = ParseParameterType(RequireString(@params, "type", "parameter_type", "parameterType"));

            if (controller.parameters.Any(parameter => string.Equals(parameter.name, name, StringComparison.Ordinal)))
                return Response.Error($"Animator parameter '{name}' already exists in '{path}'.");

            Undo.RegisterCompleteObjectUndo(controller, "Add Animator Parameter");
            controller.AddParameter(name, type);
            ApplyParameterDefaults(controller, name, @params);
            SaveController(controller, path);

            return Response.Success($"Added Animator parameter '{name}'.", new
            {
                action = "add_parameter",
                controller = BuildControllerSummary(controller, path),
                parameter = BuildParameterSnapshot(controller.parameters.First(parameter => parameter.name == name))
            });
        }

        static object RemoveParameter(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var name = RequireString(@params, "parameter", "name");
            var parameter = controller.parameters.FirstOrDefault(item => string.Equals(item.name, name, StringComparison.Ordinal));
            if (parameter == null)
                return Response.Error($"Animator parameter '{name}' was not found in '{path}'.");

            Undo.RegisterCompleteObjectUndo(controller, "Remove Animator Parameter");
            controller.RemoveParameter(parameter);
            SaveController(controller, path);

            return Response.Success($"Removed Animator parameter '{name}'.", new
            {
                action = "remove_parameter",
                controller = BuildControllerSummary(controller, path)
            });
        }

        static object AddLayer(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var name = RequireString(@params, "layer", "name");
            if (FindLayerIndex(controller, name) >= 0)
                return Response.Error($"Animator layer '{name}' already exists in '{path}'.");

            Undo.RegisterCompleteObjectUndo(controller, "Add Animator Layer");
            controller.AddLayer(name);
            ApplyLayerSettings(controller, controller.layers.Length - 1, @params);
            SaveController(controller, path);

            return Response.Success($"Added Animator layer '{name}'.", new
            {
                action = "add_layer",
                controller = BuildControllerSummary(controller, path),
                layer = BuildLayerSnapshot(controller.layers[controller.layers.Length - 1], controller.layers.Length - 1, true)
            });
        }

        static object RemoveLayer(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            if (@params["layer"] == null &&
                @params["layer_name"] == null &&
                @params["layerName"] == null &&
                @params["layer_index"] == null &&
                @params["layerIndex"] == null &&
                @params["name"] != null)
            {
                @params["layer"] = @params["name"].DeepClone();
            }

            var index = ResolveLayerIndex(controller, @params);
            if (index == 0)
                return Response.Error("Cannot remove Base Layer from an Animator Controller.");

            var name = controller.layers[index].name;
            Undo.RegisterCompleteObjectUndo(controller, "Remove Animator Layer");
            controller.RemoveLayer(index);
            SaveController(controller, path);

            return Response.Success($"Removed Animator layer '{name}'.", new
            {
                action = "remove_layer",
                controller = BuildControllerSummary(controller, path),
                removedLayer = name
            });
        }

        static object AddStateMachine(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var layer = controller.layers[layerIndex];
            var rootStateMachine = layer.stateMachine;
            var parentName = GetString(@params, "parent_state_machine", "parentStateMachine", "state_machine_parent", "stateMachineParent");
            var parent = string.IsNullOrWhiteSpace(parentName) ? rootStateMachine : ResolveStateMachine(rootStateMachine, parentName);
            var name = RequireString(@params, "state_machine", "stateMachine", "name", "Name");

            if (FindDirectStateMachine(parent, name) != null)
                return Response.Error($"Animator state machine '{name}' already exists under '{parent.name}'.");

            Undo.RegisterCompleteObjectUndo(controller, "Add Animator State Machine");
            var child = new AnimatorStateMachine
            {
                name = name,
                hideFlags = HideFlags.HideInHierarchy
            };
            AssetDatabase.AddObjectToAsset(child, controller);
            Undo.RegisterCreatedObjectUndo(child, "Add Animator State Machine");
            parent.AddStateMachine(child, ParsePosition(@params["position"] as JArray) ?? NewStateMachinePosition(parent));
            ApplyStateMachineSettings(child, @params);

            SaveController(controller, path, child);
            return Response.Success($"Added Animator state machine '{name}'.", new
            {
                action = "add_state_machine",
                controller = BuildControllerSummary(controller, path),
                layer = layer.name,
                stateMachine = BuildStateMachineSnapshot(child, CombinePath(parent.name, child.name), 0)
            });
        }

        static object RemoveStateMachine(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var layer = controller.layers[layerIndex];
            var rootStateMachine = layer.stateMachine;
            var name = RequireString(@params, "state_machine", "stateMachine", "name", "Name");
            var found = FindStateMachineWithOwner(rootStateMachine, name);
            if (found.StateMachine == null)
                return Response.Error($"Animator state machine '{name}' was not found in layer '{layer.name}'.");
            if (found.Owner == null)
                return Response.Error("Cannot remove a layer root Animator state machine.");

            Undo.RegisterCompleteObjectUndo(controller, "Remove Animator State Machine");
            found.Owner.RemoveStateMachine(found.StateMachine);
            SaveController(controller, path);

            return Response.Success($"Removed Animator state machine '{name}'.", new
            {
                action = "remove_state_machine",
                controller = BuildControllerSummary(controller, path),
                layer = layer.name,
                removedStateMachine = name
            });
        }

        static object AddState(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var stateName = RequireString(@params, "state", "name");
            var layerRoot = controller.layers[layerIndex].stateMachine;
            var stateMachine = ResolveOptionalStateMachine(layerRoot, @params) ?? layerRoot;
            if (FindState(stateMachine, stateName) != null)
                return Response.Error($"Animator state '{stateName}' already exists in layer '{controller.layers[layerIndex].name}'.");

            Undo.RegisterCompleteObjectUndo(controller, "Add Animator State");
            var state = stateMachine.AddState(stateName, ParsePosition(@params["position"] as JArray) ?? NewStatePosition(stateMachine));
            ApplyStateSettings(state, @params);
            var motionPath = GetString(@params, "motion_path", "motionPath", "clip_path", "clipPath");
            if (!string.IsNullOrWhiteSpace(motionPath))
                state.motion = LoadMotion(motionPath);

            SaveController(controller, path);
            return Response.Success($"Added Animator state '{stateName}'.", new
            {
                action = "add_state",
                controller = BuildControllerSummary(controller, path),
                state = BuildStateSnapshot(state, stateMachine, stateName)
            });
        }

        static object RemoveState(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var stateName = RequireString(@params, "state", "name");
            var stateMachine = controller.layers[layerIndex].stateMachine;
            var found = FindStateWithOwner(stateMachine, stateName);
            if (found.State == null)
                return Response.Error($"Animator state '{stateName}' was not found in layer '{controller.layers[layerIndex].name}'.");

            Undo.RegisterCompleteObjectUndo(controller, "Remove Animator State");
            found.Owner.RemoveState(found.State);
            SaveController(controller, path);

            return Response.Success($"Removed Animator state '{stateName}'.", new
            {
                action = "remove_state",
                controller = BuildControllerSummary(controller, path),
                removedState = stateName
            });
        }

        static object SetStateMotion(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var stateName = RequireString(@params, "state", "name");
            var motionPath = RequireString(@params, "motion_path", "motionPath", "clip_path", "clipPath");
            var motion = LoadMotion(motionPath);
            var state = ResolveState(controller.layers[layerIndex].stateMachine, stateName);

            Undo.RegisterCompleteObjectUndo(controller, "Set Animator State Motion");
            state.motion = motion;
            ApplyStateSettings(state, @params);
            SaveController(controller, path);

            return Response.Success($"Set motion for Animator state '{stateName}'.", new
            {
                action = "set_state_motion",
                controller = BuildControllerSummary(controller, path),
                state = BuildStateSnapshot(state, controller.layers[layerIndex].stateMachine, stateName)
            });
        }

        static object CreateBlendTree(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var stateName = RequireString(@params, "state", "name");
            var stateMachine = controller.layers[layerIndex].stateMachine;
            var replaceExisting = GetBool(@params, false, "replace_existing", "replaceExisting");
            var found = FindStateWithOwner(stateMachine, stateName);
            AnimatorState state;

            Undo.RegisterCompleteObjectUndo(controller, "Create Animator BlendTree");
            if (found.State == null)
            {
                state = stateMachine.AddState(stateName, ParsePosition(@params["position"] as JArray) ?? NewStatePosition(stateMachine));
            }
            else
            {
                state = found.State;
                if (state.motion != null && state.motion is not BlendTree && !replaceExisting)
                    return Response.Error($"Animator state '{stateName}' already has a non-BlendTree motion. Set replace_existing=true to replace the state motion.");
            }

            var blendTree = state.motion as BlendTree;
            if (blendTree == null)
            {
                blendTree = new BlendTree
                {
                    name = GetString(@params, "blend_tree_name", "blendTreeName") ?? $"{stateName}_BlendTree"
                };
                AssetDatabase.AddObjectToAsset(blendTree, controller);
                state.motion = blendTree;
                Undo.RegisterCreatedObjectUndo(blendTree, "Create Animator BlendTree");
            }
            else if (GetString(@params, "blend_tree_name", "blendTreeName") is { } blendTreeName && !string.IsNullOrWhiteSpace(blendTreeName))
            {
                blendTree.name = blendTreeName;
            }

            ApplyBlendTreeSettings(blendTree, @params);
            if (@params["children"] is JArray children)
                blendTree.children = BuildBlendChildren(children);

            ApplyStateSettings(state, @params);
            SaveController(controller, path, blendTree);

            return Response.Success($"Created BlendTree for Animator state '{stateName}'.", new
            {
                action = "create_blend_tree",
                controller = BuildControllerSummary(controller, path),
                layer = controller.layers[layerIndex].name,
                state = BuildStateSnapshot(state, stateMachine, stateName)
            });
        }

        static object ConfigureBlendTree(JObject @params)
        {
            var resolved = ResolveBlendTree(@params);
            Undo.RegisterCompleteObjectUndo(resolved.Controller, "Configure Animator BlendTree");
            Undo.RegisterCompleteObjectUndo(resolved.BlendTree, "Configure Animator BlendTree");

            ApplyBlendTreeSettings(resolved.BlendTree, @params);
            SaveController(resolved.Controller, resolved.Path, resolved.BlendTree);

            return Response.Success($"Configured BlendTree on Animator state '{resolved.State.name}'.", new
            {
                action = "configure_blend_tree",
                controller = BuildControllerSummary(resolved.Controller, resolved.Path),
                layer = resolved.Controller.layers[resolved.LayerIndex].name,
                state = BuildStateSnapshot(resolved.State, resolved.Owner, resolved.State.name)
            });
        }

        static object AddBlendChild(JObject @params)
        {
            var resolved = ResolveBlendTree(@params);
            Undo.RegisterCompleteObjectUndo(resolved.Controller, "Add Animator BlendTree Child");
            Undo.RegisterCompleteObjectUndo(resolved.BlendTree, "Add Animator BlendTree Child");

            var children = resolved.BlendTree.children.ToList();
            children.Add(BuildBlendChild(@params));
            resolved.BlendTree.children = children.ToArray();
            SaveController(resolved.Controller, resolved.Path, resolved.BlendTree);

            return Response.Success($"Added BlendTree child to Animator state '{resolved.State.name}'.", new
            {
                action = "add_blend_child",
                controller = BuildControllerSummary(resolved.Controller, resolved.Path),
                layer = resolved.Controller.layers[resolved.LayerIndex].name,
                state = BuildStateSnapshot(resolved.State, resolved.Owner, resolved.State.name)
            });
        }

        static object SetBlendChildren(JObject @params)
        {
            var resolved = ResolveBlendTree(@params);
            var children = @params["children"] as JArray ?? @params["Children"] as JArray;
            if (children == null)
                return Response.Error("set_blend_children requires a children array.");

            Undo.RegisterCompleteObjectUndo(resolved.Controller, "Set Animator BlendTree Children");
            Undo.RegisterCompleteObjectUndo(resolved.BlendTree, "Set Animator BlendTree Children");
            resolved.BlendTree.children = BuildBlendChildren(children);
            SaveController(resolved.Controller, resolved.Path, resolved.BlendTree);

            return Response.Success($"Set BlendTree children for Animator state '{resolved.State.name}'.", new
            {
                action = "set_blend_children",
                controller = BuildControllerSummary(resolved.Controller, resolved.Path),
                layer = resolved.Controller.layers[resolved.LayerIndex].name,
                state = BuildStateSnapshot(resolved.State, resolved.Owner, resolved.State.name)
            });
        }

        static object ClearBlendChildren(JObject @params)
        {
            var resolved = ResolveBlendTree(@params);
            Undo.RegisterCompleteObjectUndo(resolved.Controller, "Clear Animator BlendTree Children");
            Undo.RegisterCompleteObjectUndo(resolved.BlendTree, "Clear Animator BlendTree Children");
            resolved.BlendTree.children = Array.Empty<ChildMotion>();
            SaveController(resolved.Controller, resolved.Path, resolved.BlendTree);

            return Response.Success($"Cleared BlendTree children for Animator state '{resolved.State.name}'.", new
            {
                action = "clear_blend_children",
                controller = BuildControllerSummary(resolved.Controller, resolved.Path),
                layer = resolved.Controller.layers[resolved.LayerIndex].name,
                state = BuildStateSnapshot(resolved.State, resolved.Owner, resolved.State.name)
            });
        }

        static object SetDefaultState(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var stateName = RequireString(@params, "state", "name");
            var layerRoot = controller.layers[layerIndex].stateMachine;
            var stateMachine = ResolveOptionalStateMachine(layerRoot, @params) ?? layerRoot;
            var state = ResolveState(stateMachine, stateName);

            Undo.RegisterCompleteObjectUndo(controller, "Set Animator Default State");
            stateMachine.defaultState = state;
            SaveController(controller, path);

            return Response.Success($"Set default state to '{stateName}'.", new
            {
                action = "set_default_state",
                controller = BuildControllerSummary(controller, path),
                layer = controller.layers[layerIndex].name,
                defaultState = state.name
            });
        }

        static object AddEntryTransition(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var layer = controller.layers[layerIndex];
            var rootStateMachine = layer.stateMachine;
            var stateMachine = ResolveOptionalStateMachine(rootStateMachine, @params) ?? rootStateMachine;
            var toStateName = RequireString(@params, "to_state", "toState", "destination_state", "destinationState", "state");
            var toState = ResolveState(stateMachine, toStateName);

            Undo.RegisterCompleteObjectUndo(controller, "Add Animator Entry Transition");
            var transition = stateMachine.AddEntryTransition(toState);
            ApplyTransitionSettings(transition, @params);
            SaveController(controller, path);

            return Response.Success("Added Animator Entry transition.", new
            {
                action = "add_entry_transition",
                controller = BuildControllerSummary(controller, path),
                layer = layer.name,
                stateMachine = stateMachine.name,
                transition = BuildTransitionSnapshot(transition)
            });
        }

        static object AddTransition(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var layer = controller.layers[layerIndex];
            var rootStateMachine = layer.stateMachine;
            var stateMachine = ResolveOptionalStateMachine(rootStateMachine, @params) ?? rootStateMachine;
            var toExit = GetBool(@params, false, "to_exit", "toExit", "exit");
            var entry = GetBool(@params, false, "entry", "entry_transition", "entryTransition");
            var anyState = GetBool(@params, false, "any_state", "anyState");
            var toStateName = GetString(@params, "to_state", "toState", "destination_state", "destinationState");
            var destinationStateMachineName = GetString(@params, "destination_state_machine", "destinationStateMachine", "to_state_machine", "toStateMachine");
            var fromStateName = GetString(@params, "from_state", "fromState", "state");

            Undo.RegisterCompleteObjectUndo(controller, "Add Animator Transition");
            AnimatorTransitionBase transition;
            if (entry)
            {
                if (toExit || !string.IsNullOrWhiteSpace(destinationStateMachineName))
                    return Response.Error("Entry transitions must target a destination state.");

                transition = stateMachine.AddEntryTransition(ResolveState(stateMachine, toStateName));
            }
            else if (anyState)
            {
                if (toExit)
                    return Response.Error("Any State transitions cannot target Exit.");
                if (!string.IsNullOrWhiteSpace(destinationStateMachineName))
                    return Response.Error("Any State transitions must target a destination state in the selected state machine.");

                var toState = ResolveState(stateMachine, toStateName);
                transition = stateMachine.AddAnyStateTransition(toState);
            }
            else
            {
                var fromState = ResolveState(rootStateMachine, fromStateName);
                transition = toExit
                    ? fromState.AddExitTransition()
                    : !string.IsNullOrWhiteSpace(destinationStateMachineName) && string.IsNullOrWhiteSpace(toStateName)
                        ? fromState.AddTransition(ResolveStateMachine(rootStateMachine, destinationStateMachineName))
                        : fromState.AddTransition(ResolveTransitionDestinationState(rootStateMachine, stateMachine, toStateName, destinationStateMachineName));
            }

            ApplyTransitionSettings(transition, @params);
            SaveController(controller, path);

            return Response.Success("Added Animator transition.", new
            {
                action = "add_transition",
                controller = BuildControllerSummary(controller, path),
                layer = layer.name,
                transition = BuildTransitionSnapshot(transition)
            });
        }

        static object RemoveEntryTransition(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var layer = controller.layers[layerIndex];
            var rootStateMachine = layer.stateMachine;
            var stateMachine = ResolveOptionalStateMachine(rootStateMachine, @params) ?? rootStateMachine;
            var toStateName = GetString(@params, "to_state", "toState", "destination_state", "destinationState", "state");

            Undo.RegisterCompleteObjectUndo(controller, "Remove Animator Entry Transition");
            if (!RemoveEntryTransition(stateMachine, toStateName))
                return Response.Error("No matching Animator Entry transition was found.");

            SaveController(controller, path);
            return Response.Success("Removed Animator Entry transition.", new
            {
                action = "remove_entry_transition",
                controller = BuildControllerSummary(controller, path),
                layer = layer.name,
                stateMachine = stateMachine.name
            });
        }

        static object RemoveTransition(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var layer = controller.layers[layerIndex];
            var rootStateMachine = layer.stateMachine;
            var stateMachine = ResolveOptionalStateMachine(rootStateMachine, @params) ?? rootStateMachine;
            var entry = GetBool(@params, false, "entry", "entry_transition", "entryTransition");
            var anyState = GetBool(@params, false, "any_state", "anyState");
            var fromStateName = GetString(@params, "from_state", "fromState", "state");
            var toStateName = GetString(@params, "to_state", "toState", "destination_state", "destinationState");
            var destinationStateMachineName = GetString(@params, "destination_state_machine", "destinationStateMachine", "to_state_machine", "toStateMachine");
            var toExit = GetBool(@params, false, "to_exit", "toExit", "exit");

            Undo.RegisterCompleteObjectUndo(controller, "Remove Animator Transition");
            var removed = entry
                ? RemoveEntryTransition(stateMachine, toStateName)
                : anyState
                ? RemoveAnyStateTransition(stateMachine, toStateName)
                : RemoveStateTransition(rootStateMachine, fromStateName, toStateName, destinationStateMachineName, toExit);

            if (!removed)
                return Response.Error("No matching Animator transition was found.");

            SaveController(controller, path);
            return Response.Success("Removed Animator transition.", new
            {
                action = "remove_transition",
                controller = BuildControllerSummary(controller, path),
                layer = layer.name
            });
        }

        static object BuildControllerSearchResult(string path)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            return new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                name = Path.GetFileNameWithoutExtension(path),
                layerCount = controller?.layers?.Length ?? 0,
                parameterCount = controller?.parameters?.Length ?? 0
            };
        }

        static object BuildControllerSummary(AnimatorController controller, string path)
        {
            return new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                name = controller.name,
                layerCount = controller.layers?.Length ?? 0,
                parameterCount = controller.parameters?.Length ?? 0,
                animationClipCount = controller.animationClips?.Length ?? 0
            };
        }

        static object BuildControllerSnapshot(AnimatorController controller, string path)
        {
            return new
            {
                summary = BuildControllerSummary(controller, path),
                parameters = controller.parameters.Select(BuildParameterSnapshot).ToArray(),
                layers = controller.layers.Select((layer, index) => BuildLayerSnapshot(layer, index, true)).ToArray(),
                clips = controller.animationClips
                    .Where(clip => clip != null)
                    .Select(clip => new
                    {
                        name = clip.name,
                        path = AssetDatabase.GetAssetPath(clip),
                        guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(clip)),
                        length = clip.length,
                        frameRate = clip.frameRate
                    })
                    .ToArray()
            };
        }

        static object BuildParameterSnapshot(AnimatorControllerParameter parameter)
        {
            return new
            {
                name = parameter.name,
                type = parameter.type.ToString(),
                defaultFloat = parameter.defaultFloat,
                defaultInt = parameter.defaultInt,
                defaultBool = parameter.defaultBool
            };
        }

        static object BuildLayerSnapshot(AnimatorControllerLayer layer, int index, bool includeStateMachine)
        {
            return new
            {
                index,
                name = layer.name,
                defaultWeight = layer.defaultWeight,
                blendingMode = layer.blendingMode.ToString(),
                iKPass = layer.iKPass,
                syncedLayerIndex = layer.syncedLayerIndex,
                syncedLayerAffectsTiming = layer.syncedLayerAffectsTiming,
                avatarMaskPath = layer.avatarMask == null ? null : AssetDatabase.GetAssetPath(layer.avatarMask),
                stateMachine = includeStateMachine ? BuildStateMachineSnapshot(layer.stateMachine, layer.stateMachine?.name ?? layer.name, 0) : null
            };
        }

        static object BuildStateMachineSnapshot(AnimatorStateMachine stateMachine, string path, int depth)
        {
            if (stateMachine == null)
                return null;

            if (depth > 8)
                return new { name = stateMachine.name, path, truncated = true };

            return new
            {
                name = stateMachine.name,
                path,
                defaultState = stateMachine.defaultState == null ? null : stateMachine.defaultState.name,
                anyStatePosition = ToArray(stateMachine.anyStatePosition),
                entryPosition = ToArray(stateMachine.entryPosition),
                exitPosition = ToArray(stateMachine.exitPosition),
                states = stateMachine.states
                    .OrderBy(child => child.state?.name, StringComparer.OrdinalIgnoreCase)
                    .Select(child => BuildStateSnapshot(child.state, stateMachine, CombinePath(path, child.state?.name), child.position))
                    .ToArray(),
                anyStateTransitions = stateMachine.anyStateTransitions.Select(BuildTransitionSnapshot).ToArray(),
                entryTransitions = stateMachine.entryTransitions.Select(BuildTransitionSnapshot).ToArray(),
                stateMachines = stateMachine.stateMachines
                    .OrderBy(child => child.stateMachine?.name, StringComparer.OrdinalIgnoreCase)
                    .Select(child => BuildStateMachineSnapshot(child.stateMachine, CombinePath(path, child.stateMachine?.name), depth + 1))
                    .ToArray()
            };
        }

        static object BuildStateSnapshot(AnimatorState state, AnimatorStateMachine owner, string path, Vector3? graphPosition = null)
        {
            if (state == null)
                return null;

            return new
            {
                name = state.name,
                path,
                motion = BuildMotionSnapshot(state.motion),
                speed = state.speed,
                speedParameterActive = state.speedParameterActive,
                speedParameter = state.speedParameter,
                cycleOffset = state.cycleOffset,
                cycleOffsetParameterActive = state.cycleOffsetParameterActive,
                cycleOffsetParameter = state.cycleOffsetParameter,
                mirror = state.mirror,
                mirrorParameterActive = state.mirrorParameterActive,
                mirrorParameter = state.mirrorParameter,
                iKOnFeet = state.iKOnFeet,
                timeParameterActive = state.timeParameterActive,
                timeParameter = state.timeParameter,
                tag = state.tag,
                writeDefaultValues = state.writeDefaultValues,
                isDefault = owner != null && owner.defaultState == state,
                position = graphPosition.HasValue ? ToArray(graphPosition.Value) : null,
                transitions = state.transitions.Select(BuildTransitionSnapshot).ToArray()
            };
        }

        static object BuildMotionSnapshot(Motion motion, int depth = 0)
        {
            if (motion == null)
                return null;

            var path = AssetDatabase.GetAssetPath(motion);
            if (motion is BlendTree blendTree)
                return BuildBlendTreeSnapshot(blendTree, path, depth);

            return new
            {
                name = motion.name,
                type = motion.GetType().Name,
                path,
                guid = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.AssetPathToGUID(path)
            };
        }

        static object BuildBlendTreeSnapshot(BlendTree blendTree, string path, int depth)
        {
            if (depth > 4)
            {
                return new
                {
                    name = blendTree.name,
                    type = nameof(BlendTree),
                    path,
                    truncated = true
                };
            }

            var children = blendTree.children ?? Array.Empty<ChildMotion>();
            return new
            {
                name = blendTree.name,
                type = nameof(BlendTree),
                path,
                guid = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.AssetPathToGUID(path),
                blendType = blendTree.blendType.ToString(),
                blendParameter = blendTree.blendParameter,
                blendParameterY = blendTree.blendParameterY,
                useAutomaticThresholds = blendTree.useAutomaticThresholds,
                minThreshold = blendTree.minThreshold,
                maxThreshold = blendTree.maxThreshold,
                childCount = children.Length,
                children = children.Select((child, index) => new
                {
                    index,
                    threshold = child.threshold,
                    position = new[] { child.position.x, child.position.y },
                    timeScale = child.timeScale,
                    cycleOffset = child.cycleOffset,
                    mirror = child.mirror,
                    directBlendParameter = child.directBlendParameter,
                    motion = BuildMotionSnapshot(child.motion, depth + 1)
                }).ToArray()
            };
        }

        static object BuildTransitionSnapshot(AnimatorTransitionBase transition)
        {
            if (transition == null)
                return null;

            var stateTransition = transition as AnimatorStateTransition;
            return new
            {
                name = transition.name,
                isExit = transition.isExit,
                mute = transition.mute,
                solo = transition.solo,
                destinationState = transition.destinationState == null ? null : transition.destinationState.name,
                destinationStateMachine = transition.destinationStateMachine == null ? null : transition.destinationStateMachine.name,
                hasExitTime = stateTransition?.hasExitTime,
                exitTime = stateTransition?.exitTime,
                duration = stateTransition?.duration,
                offset = stateTransition?.offset,
                hasFixedDuration = stateTransition?.hasFixedDuration,
                interruptionSource = stateTransition?.interruptionSource.ToString(),
                orderedInterruption = stateTransition?.orderedInterruption,
                canTransitionToSelf = stateTransition?.canTransitionToSelf,
                conditions = transition.conditions.Select(condition => new
                {
                    mode = condition.mode.ToString(),
                    parameter = condition.parameter,
                    threshold = condition.threshold
                }).ToArray()
            };
        }

        static List<object> BuildValidationIssues(AnimatorController controller, string path)
        {
            var issues = new List<object>();
            if (controller.layers == null || controller.layers.Length == 0)
            {
                issues.Add(BuildIssue("error", "noLayers", path, "Animator Controller has no layers."));
                return issues;
            }

            var parameterNames = new HashSet<string>(controller.parameters.Select(parameter => parameter.name), StringComparer.Ordinal);
            foreach (var group in controller.parameters.GroupBy(parameter => parameter.name, StringComparer.Ordinal).Where(group => group.Count() > 1))
                issues.Add(BuildIssue("error", "duplicateParameter", path, $"Duplicate parameter '{group.Key}'."));

            for (var i = 0; i < controller.layers.Length; i++)
            {
                var layer = controller.layers[i];
                if (layer.stateMachine == null)
                {
                    issues.Add(BuildIssue("error", "missingStateMachine", path, $"Layer '{layer.name}' has no state machine."));
                    continue;
                }

                ValidateStateMachine(layer.stateMachine, layer.name, parameterNames, issues, path);
            }

            return issues;
        }

        static void ValidateStateMachine(AnimatorStateMachine stateMachine, string pathInController, HashSet<string> parameterNames, List<object> issues, string controllerPath)
        {
            var states = stateMachine.states.Select(child => child.state).Where(state => state != null).ToList();
            foreach (var group in states.GroupBy(state => state.name, StringComparer.Ordinal).Where(group => group.Count() > 1))
                issues.Add(BuildIssue("warning", "duplicateStateName", controllerPath, $"State machine '{pathInController}' has duplicate state name '{group.Key}'."));

            if (states.Count > 0 && stateMachine.defaultState == null)
                issues.Add(BuildIssue("warning", "missingDefaultState", controllerPath, $"State machine '{pathInController}' has states but no default state."));

            foreach (var state in states)
            {
                if (state.motion is BlendTree blendTree)
                    ValidateBlendTree(blendTree, parameterNames, issues, controllerPath, $"{pathInController}/{state.name}", 0);

                foreach (var transition in state.transitions)
                    ValidateTransition(transition, parameterNames, issues, controllerPath, $"{pathInController}/{state.name}");
            }

            foreach (var transition in stateMachine.anyStateTransitions)
                ValidateTransition(transition, parameterNames, issues, controllerPath, $"{pathInController}/Any State");

            foreach (var transition in stateMachine.entryTransitions)
                ValidateTransition(transition, parameterNames, issues, controllerPath, $"{pathInController}/Entry");

            foreach (var child in stateMachine.stateMachines)
            {
                if (child.stateMachine != null)
                    ValidateStateMachine(child.stateMachine, CombinePath(pathInController, child.stateMachine.name), parameterNames, issues, controllerPath);
            }
        }

        static void ValidateTransition(AnimatorTransitionBase transition, HashSet<string> parameterNames, List<object> issues, string controllerPath, string owner)
        {
            if (transition is AnimatorStateTransition stateTransition &&
                !stateTransition.hasExitTime &&
                transition.conditions.Length == 0)
            {
                issues.Add(BuildIssue("warning", "ignoredTransition", controllerPath, $"Transition in '{owner}' has no exit time and no conditions, so Unity will ignore it."));
            }

            foreach (var condition in transition.conditions)
            {
                if (!parameterNames.Contains(condition.parameter))
                    issues.Add(BuildIssue("error", "missingConditionParameter", controllerPath, $"Transition in '{owner}' uses missing parameter '{condition.parameter}'."));
            }
        }

        static void ValidateBlendTree(BlendTree blendTree, HashSet<string> parameterNames, List<object> issues, string controllerPath, string owner, int depth)
        {
            if (blendTree == null || depth > 8)
                return;

            if (NeedsBlendParameter(blendTree.blendType) && string.IsNullOrWhiteSpace(blendTree.blendParameter))
                issues.Add(BuildIssue("error", "missingBlendParameter", controllerPath, $"BlendTree in '{owner}' has no blend parameter."));
            else if (!string.IsNullOrWhiteSpace(blendTree.blendParameter) && !parameterNames.Contains(blendTree.blendParameter))
                issues.Add(BuildIssue("error", "missingBlendParameter", controllerPath, $"BlendTree in '{owner}' uses missing parameter '{blendTree.blendParameter}'."));

            if (NeedsBlendParameterY(blendTree.blendType) && string.IsNullOrWhiteSpace(blendTree.blendParameterY))
                issues.Add(BuildIssue("error", "missingBlendParameterY", controllerPath, $"2D BlendTree in '{owner}' has no Y blend parameter."));
            else if (!string.IsNullOrWhiteSpace(blendTree.blendParameterY) && !parameterNames.Contains(blendTree.blendParameterY))
                issues.Add(BuildIssue("error", "missingBlendParameterY", controllerPath, $"BlendTree in '{owner}' uses missing Y parameter '{blendTree.blendParameterY}'."));

            var children = blendTree.children ?? Array.Empty<ChildMotion>();
            if (children.Length == 0)
                issues.Add(BuildIssue("warning", "emptyBlendTree", controllerPath, $"BlendTree in '{owner}' has no child motions."));

            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i];
                if (blendTree.blendType == BlendTreeType.Direct &&
                    !string.IsNullOrWhiteSpace(child.directBlendParameter) &&
                    !parameterNames.Contains(child.directBlendParameter))
                {
                    issues.Add(BuildIssue("error", "missingDirectBlendParameter", controllerPath, $"BlendTree child {i} in '{owner}' uses missing direct blend parameter '{child.directBlendParameter}'."));
                }

                if (child.motion is BlendTree childTree)
                    ValidateBlendTree(childTree, parameterNames, issues, controllerPath, $"{owner}/child[{i}]", depth + 1);
            }
        }

        static bool NeedsBlendParameter(BlendTreeType blendType)
        {
            return blendType != BlendTreeType.Direct;
        }

        static bool NeedsBlendParameterY(BlendTreeType blendType)
        {
            return blendType == BlendTreeType.SimpleDirectional2D ||
                   blendType == BlendTreeType.FreeformDirectional2D ||
                   blendType == BlendTreeType.FreeformCartesian2D;
        }

        static object BuildIssue(string severity, string code, string path, string message)
        {
            return new { severity, code, path, message };
        }

        static JObject GetGraphSpec(JObject @params)
        {
            var graph = @params["graph"] as JObject ?? @params["Graph"] as JObject;
            return graph ?? @params;
        }

        static List<string> ValidateGraphPreflight(AnimatorController controller, JObject graph)
        {
            var errors = new List<string>();
            var parameterNames = new HashSet<string>(
                controller?.parameters?.Select(parameter => parameter.name) ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);

            foreach (var parameterSpec in GetArray(graph, "parameters", "Parameters").OfType<JObject>())
            {
                var name = GetString(parameterSpec, "name", "Name", "parameter", "Parameter");
                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add("Graph parameter requires 'name' or 'parameter'.");
                    continue;
                }

                var existing = controller?.parameters?.FirstOrDefault(parameter => string.Equals(parameter.name, name, StringComparison.Ordinal));
                var typeText = GetString(parameterSpec, "type", "Type", "parameter_type", "parameterType");
                if (string.IsNullOrWhiteSpace(typeText) && existing == null)
                    errors.Add($"Animator parameter '{name}' requires 'type' because it does not exist yet.");
                else if (!string.IsNullOrWhiteSpace(typeText))
                    TryPreflight(errors, () => ParseParameterType(typeText), $"Animator parameter '{name}' has unsupported type '{typeText}'.");

                parameterNames.Add(name);
            }

            foreach (var layerItem in GetArray(graph, "layers", "Layers").OfType<JObject>().Select((spec, index) => new { spec, index }))
            {
                var layerName = (string)null;
                TryPreflight(errors, () => layerName = ResolveGraphLayerName(layerItem.spec, layerItem.index), $"Graph layer at index {layerItem.index} requires 'name' or 'layer'.");
                if (string.IsNullOrWhiteSpace(layerName))
                    continue;

                var stateNames = new HashSet<string>(StringComparer.Ordinal);
                var layerIndex = controller == null ? -1 : FindLayerIndex(controller, layerName);
                if (layerIndex >= 0)
                    CollectStateNames(controller.layers[layerIndex].stateMachine, stateNames);

                CollectGraphDeclaredStateNames(layerItem.spec, layerName, stateNames, errors);
                ValidateGraphStateMachinePreflight(layerItem.spec, layerName, stateNames, parameterNames, errors);
            }

            return errors;
        }

        static void CollectGraphDeclaredStateNames(JObject stateMachineSpec, string layerName, HashSet<string> stateNames, List<string> errors)
        {
            foreach (var stateSpec in GetArray(stateMachineSpec, "states", "States").OfType<JObject>())
            {
                var stateName = GetString(stateSpec, "name", "Name", "state", "State");
                if (string.IsNullOrWhiteSpace(stateName))
                {
                    errors.Add($"A state in graph layer '{layerName}' requires 'name' or 'state'.");
                    continue;
                }

                stateNames.Add(stateName);
            }

            foreach (var childSpec in GetArray(stateMachineSpec, "state_machines", "stateMachines", "StateMachines").OfType<JObject>())
            {
                if (string.IsNullOrWhiteSpace(GetString(childSpec, "name", "Name", "state_machine", "stateMachine")))
                    errors.Add($"A sub-state machine in graph layer '{layerName}' requires 'name'.");
                CollectGraphDeclaredStateNames(childSpec, layerName, stateNames, errors);
            }
        }

        static void ValidateGraphStateMachinePreflight(JObject stateMachineSpec, string layerName, HashSet<string> stateNames, HashSet<string> parameterNames, List<string> errors)
        {
            foreach (var stateSpec in GetArray(stateMachineSpec, "states", "States").OfType<JObject>())
            {
                var stateName = GetString(stateSpec, "name", "Name", "state", "State");
                if (string.IsNullOrWhiteSpace(stateName))
                    continue;

                ValidateGraphMotionReference(stateSpec, errors);

                var blendTreeSpec = stateSpec["blend_tree"] as JObject ?? stateSpec["blendTree"] as JObject;
                if (blendTreeSpec != null)
                    ValidateGraphBlendTreePreflight(blendTreeSpec, parameterNames, errors, $"state '{stateName}' BlendTree");
            }

            var defaultState = GetString(stateMachineSpec, "default_state", "defaultState");
            if (!string.IsNullOrWhiteSpace(defaultState) && !stateNames.Contains(defaultState))
                errors.Add($"Default state '{defaultState}' in graph layer '{layerName}' is not declared in the graph and does not exist in the layer.");

            foreach (var transitionSpec in CollectGraphTransitions(stateMachineSpec))
                ValidateGraphTransitionPreflight(transitionSpec, layerName, stateNames, parameterNames, errors);

            foreach (var childSpec in GetArray(stateMachineSpec, "state_machines", "stateMachines", "StateMachines").OfType<JObject>())
                ValidateGraphStateMachinePreflight(childSpec, layerName, stateNames, parameterNames, errors);
        }

        static void ValidateGraphBlendTreePreflight(JObject blendTreeSpec, HashSet<string> parameterNames, List<string> errors, string owner)
        {
            var blendType = GetString(blendTreeSpec, "blend_type", "blendType");
            if (!string.IsNullOrWhiteSpace(blendType))
                TryPreflight(errors, () => ParseBlendTreeType(blendType), $"{owner} has unsupported blend_type '{blendType}'.");

            ValidateGraphParameterReference(blendTreeSpec, parameterNames, errors, $"{owner} blend_parameter", "blend_parameter", "blendParameter", "parameter", "Parameter");
            ValidateGraphParameterReference(blendTreeSpec, parameterNames, errors, $"{owner} blend_parameter_y", "blend_parameter_y", "blendParameterY", "parameter_y", "parameterY");

            foreach (var child in GetArray(blendTreeSpec, "children", "Children").OfType<JObject>())
            {
                ValidateGraphMotionReference(child, errors);
                ValidateGraphParameterReference(child, parameterNames, errors, $"{owner} direct_blend_parameter", "direct_blend_parameter", "directBlendParameter");
            }
        }

        static void ValidateGraphMotionReference(JObject spec, List<string> errors)
        {
            var motionPath = GetString(spec, "motion_path", "motionPath", "clip_path", "clipPath");
            if (string.IsNullOrWhiteSpace(motionPath))
                return;

            var normalized = NormalizeAssetPath(motionPath);
            if (AssetDatabase.LoadAssetAtPath<Motion>(normalized) == null)
                errors.Add($"Motion asset was not found at '{normalized}'.");
        }

        static void ValidateGraphTransitionPreflight(JObject transitionSpec, string layerName, HashSet<string> stateNames, HashSet<string> parameterNames, List<string> errors)
        {
            var entry = GetBool(transitionSpec, false, "entry", "entry_transition", "entryTransition");
            var anyState = GetBool(transitionSpec, false, "any_state", "anyState");
            var toExit = GetBool(transitionSpec, false, "to_exit", "toExit", "exit");
            var fromStateName = GetString(transitionSpec, "from_state", "fromState", "state", "State");
            var toStateName = GetString(transitionSpec, "to_state", "toState", "destination_state", "destinationState");
            var destinationStateMachineName = GetString(transitionSpec, "destination_state_machine", "destinationStateMachine", "to_state_machine", "toStateMachine");

            var hasExitTime = GetBool(transitionSpec, false, "has_exit_time", "hasExitTime");
            var conditions = transitionSpec["conditions"] as JArray ?? transitionSpec["Conditions"] as JArray;
            if (!entry && !hasExitTime && (conditions == null || conditions.Count == 0))
                errors.Add($"Transition '{(entry ? "Entry" : anyState ? "Any State" : fromStateName)} -> {(toExit ? "Exit" : !string.IsNullOrWhiteSpace(destinationStateMachineName) && string.IsNullOrWhiteSpace(toStateName) ? destinationStateMachineName : toStateName)}' in layer '{layerName}' requires has_exit_time=true or at least one condition.");

            if (entry)
            {
                if (toExit || !string.IsNullOrWhiteSpace(destinationStateMachineName))
                    errors.Add($"Entry transition in layer '{layerName}' must target a destination state.");
                if (string.IsNullOrWhiteSpace(toStateName))
                    errors.Add($"Entry transition in layer '{layerName}' requires 'to_state'.");
                else if (!stateNames.Contains(toStateName))
                    errors.Add($"Entry transition in layer '{layerName}' targets missing state '{toStateName}'.");
            }
            else if (anyState)
            {
                if (toExit)
                    errors.Add($"Any State transition in layer '{layerName}' cannot target Exit.");
                if (!string.IsNullOrWhiteSpace(destinationStateMachineName))
                    errors.Add($"Any State transition in layer '{layerName}' must target a destination state in the selected state machine.");
                if (string.IsNullOrWhiteSpace(toStateName))
                    errors.Add($"Any State transition in layer '{layerName}' requires 'to_state'.");
                else if (!stateNames.Contains(toStateName))
                    errors.Add($"Any State transition in layer '{layerName}' targets missing state '{toStateName}'.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(fromStateName))
                    errors.Add($"Transition in layer '{layerName}' requires 'from_state' or a state-level transition.");
                else if (!stateNames.Contains(fromStateName))
                    errors.Add($"Transition in layer '{layerName}' starts from missing state '{fromStateName}'.");

                if (!toExit)
                {
                    if (string.IsNullOrWhiteSpace(toStateName) && string.IsNullOrWhiteSpace(destinationStateMachineName))
                        errors.Add($"Transition in layer '{layerName}' requires 'to_state' or 'destination_state_machine' unless 'to_exit' is true.");
                    else if (!stateNames.Contains(toStateName))
                    {
                        if (string.IsNullOrWhiteSpace(destinationStateMachineName))
                            errors.Add($"Transition in layer '{layerName}' targets missing state '{toStateName}'.");
                    }
                }
            }

            var interruptionSource = GetString(transitionSpec, "interruption_source", "interruptionSource");
            if (!string.IsNullOrWhiteSpace(interruptionSource))
                TryPreflight(errors, () => ParseInterruptionSource(interruptionSource), $"Transition in layer '{layerName}' has unsupported interruption_source '{interruptionSource}'.");

            if (conditions == null)
                return;

            foreach (var condition in conditions.OfType<JObject>())
            {
                var parameter = GetString(condition, "parameter", "Parameter");
                if (string.IsNullOrWhiteSpace(parameter))
                    errors.Add($"Transition condition in layer '{layerName}' requires 'parameter'.");
                else
                    ValidateGraphParameterReference(condition, parameterNames, errors, $"transition condition in layer '{layerName}'", "parameter", "Parameter");

                var mode = GetString(condition, "mode", "Mode");
                if (string.IsNullOrWhiteSpace(mode))
                    errors.Add($"Transition condition in layer '{layerName}' requires 'mode'.");
                else
                    TryPreflight(errors, () => ParseConditionMode(mode), $"Transition condition in layer '{layerName}' has unsupported mode '{mode}'.");
            }
        }

        static void ValidateGraphParameterReference(JObject spec, HashSet<string> parameterNames, List<string> errors, string owner, params string[] names)
        {
            var parameter = GetString(spec, names);
            if (!string.IsNullOrWhiteSpace(parameter) && !parameterNames.Contains(parameter))
                errors.Add($"{owner} references missing Animator parameter '{parameter}'.");
        }

        static void CollectStateNames(AnimatorStateMachine stateMachine, HashSet<string> names)
        {
            if (stateMachine == null)
                return;

            foreach (var child in stateMachine.states)
            {
                if (child.state != null)
                    names.Add(child.state.name);
            }

            foreach (var childMachine in stateMachine.stateMachines)
                CollectStateNames(childMachine.stateMachine, names);
        }

        static void TryPreflight<T>(List<string> errors, Func<T> action, string error)
        {
            try
            {
                action();
            }
            catch
            {
                errors.Add(error);
            }
        }

        static void PlanGraphChanges(AnimatorController controller, JObject graph, string path, GraphApplyOptions options, GraphApplyReport report)
        {
            report.Add(controller == null ? "would_create" : "would_update", "controller", path, controller?.name ?? Path.GetFileNameWithoutExtension(path), null);

            foreach (var parameterSpec in GetArray(graph, "parameters", "Parameters").OfType<JObject>())
            {
                var name = GetString(parameterSpec, "name", "Name", "parameter", "Parameter");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var exists = controller?.parameters?.Any(parameter => string.Equals(parameter.name, name, StringComparison.Ordinal)) ?? false;
                report.Add(exists ? "would_update" : "would_create", "parameter", path, name, null);
            }

            foreach (var layerSpec in GetArray(graph, "layers", "Layers").OfType<JObject>().Select((spec, index) => new { spec, index }))
            {
                var layerName = ResolveGraphLayerName(layerSpec.spec, layerSpec.index);
                var layerIndex = controller == null ? -1 : FindLayerIndex(controller, layerName);
                report.Add(layerIndex >= 0 ? "would_update" : "would_create", "layer", path, layerName, null);

                foreach (var stateSpec in GetArray(layerSpec.spec, "states", "States").OfType<JObject>())
                {
                    var stateName = GetString(stateSpec, "name", "Name", "state", "State");
                    if (string.IsNullOrWhiteSpace(stateName))
                        continue;

                    var exists = layerIndex >= 0 && AnimatorStateExists(controller.layers[layerIndex].stateMachine, stateName);
                    report.Add(exists ? "would_update" : "would_create", "state", path, stateName, layerName);

                    if (stateSpec["blend_tree"] is JObject || stateSpec["blendTree"] is JObject)
                        report.Add("would_upsert", "blend_tree", path, stateName, layerName);
                }

                var transitionCount = CountGraphTransitions(layerSpec.spec);
                if (transitionCount > 0)
                    report.Add("would_upsert", "transition", path, $"{transitionCount} transition(s)", layerName);
            }
        }

        static void ApplyGraphParameters(AnimatorController controller, JObject graph, GraphApplyOptions options, GraphApplyReport report)
        {
            var parameterSpecs = GetArray(graph, "parameters", "Parameters").OfType<JObject>().ToList();
            var declared = new HashSet<string>(StringComparer.Ordinal);

            foreach (var spec in parameterSpecs)
            {
                var name = RequireString(spec, "name", "Name", "parameter", "Parameter");
                declared.Add(name);
                var existing = controller.parameters.FirstOrDefault(parameter => string.Equals(parameter.name, name, StringComparison.Ordinal));
                var typeText = GetString(spec, "type", "Type", "parameter_type", "parameterType");
                if (string.IsNullOrWhiteSpace(typeText) && existing == null)
                    throw new InvalidOperationException($"Animator parameter '{name}' requires 'type' because it does not exist yet.");

                var type = string.IsNullOrWhiteSpace(typeText) ? existing.type : ParseParameterType(typeText);

                if (existing == null)
                {
                    controller.AddParameter(name, type);
                    ApplyParameterDefaults(controller, name, spec);
                    report.Add("created", "parameter", null, name, type.ToString());
                    continue;
                }

                if (existing.type != type)
                {
                    controller.RemoveParameter(existing);
                    controller.AddParameter(name, type);
                    ApplyParameterDefaults(controller, name, spec);
                    report.Add("updated", "parameter", null, name, $"type={type}");
                    continue;
                }

                ApplyParameterDefaults(controller, name, spec);
                report.Add("updated", "parameter", null, name, null);
            }

            if (options.RemoveMissingParameters && declared.Count > 0)
            {
                foreach (var parameter in controller.parameters.ToArray())
                {
                    if (declared.Contains(parameter.name))
                        continue;

                    controller.RemoveParameter(parameter);
                    report.Add("removed", "parameter", null, parameter.name, null);
                }
            }
        }

        static void ApplyGraphLayers(AnimatorController controller, JObject graph, GraphApplyOptions options, GraphApplyReport report)
        {
            var layerSpecs = GetArray(graph, "layers", "Layers").OfType<JObject>().ToList();
            if (layerSpecs.Count == 0)
                return;

            var declaredLayerNames = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < layerSpecs.Count; i++)
            {
                var layerSpec = layerSpecs[i];
                var layerName = ResolveGraphLayerName(layerSpec, i);
                declaredLayerNames.Add(layerName);
                var layerIndex = FindLayerIndex(controller, layerName);

                if (layerIndex < 0)
                {
                    controller.AddLayer(layerName);
                    layerIndex = FindLayerIndex(controller, layerName);
                    report.Add("created", "layer", null, layerName, null);
                }
                else
                {
                    report.Add("updated", "layer", null, layerName, null);
                }

                ApplyLayerSettings(controller, layerIndex, layerSpec);
                ApplyGraphStates(controller, layerIndex, layerSpec, options, report);
                ApplyGraphTransitions(controller, layerIndex, layerSpec, options, report);
            }

            if (options.RemoveMissingLayers && declaredLayerNames.Count > 0)
            {
                for (var i = controller.layers.Length - 1; i >= 1; i--)
                {
                    var layerName = controller.layers[i].name;
                    if (declaredLayerNames.Contains(layerName))
                        continue;

                    controller.RemoveLayer(i);
                    report.Add("removed", "layer", null, layerName, null);
                }
            }
        }

        static void ApplyGraphStates(AnimatorController controller, int layerIndex, JObject layerSpec, GraphApplyOptions options, GraphApplyReport report)
        {
            var layer = controller.layers[layerIndex];
            ApplyGraphStateMachineStructure(controller, layer.stateMachine, layer.stateMachine, layerSpec, options, report, layer.name, layer.stateMachine.name);
        }

        static void ApplyGraphStateMachineStructure(
            AnimatorController controller,
            AnimatorStateMachine rootStateMachine,
            AnimatorStateMachine stateMachine,
            JObject stateMachineSpec,
            GraphApplyOptions options,
            GraphApplyReport report,
            string layerName,
            string pathInLayer)
        {
            ApplyStateMachineSettings(stateMachine, stateMachineSpec);
            var stateSpecs = GetArray(stateMachineSpec, "states", "States").OfType<JObject>().ToList();
            var declaredStateNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var stateSpec in stateSpecs)
            {
                var stateName = RequireString(stateSpec, "name", "Name", "state", "State");
                declaredStateNames.Add(stateName);
                var found = FindDirectStateWithPosition(stateMachine, stateName);
                AnimatorState state;

                if (found.State == null)
                {
                    state = stateMachine.AddState(stateName, ParsePosition(stateSpec["position"] as JArray) ?? NewStatePosition(stateMachine));
                    report.Add("created", "state", null, CombinePath(pathInLayer, stateName), layerName);
                }
                else
                {
                    state = found.State;
                    if (stateSpec["position"] is JArray position)
                        SetStateGraphPosition(stateMachine, state, ParsePosition(position).GetValueOrDefault());
                    report.Add("updated", "state", null, CombinePath(pathInLayer, stateName), layerName);
                }

                ApplyStateSettings(state, stateSpec);

                var blendTreeSpec = stateSpec["blend_tree"] as JObject ?? stateSpec["blendTree"] as JObject;
                if (blendTreeSpec != null)
                {
                    var replaceExisting = GetBool(blendTreeSpec, GetBool(stateSpec, true, "replace_existing", "replaceExisting"), "replace_existing", "replaceExisting");
                    EnsureBlendTreeOnState(controller, state, stateName, blendTreeSpec, replaceExisting, report, layerName);
                }
                else
                {
                    var motionPath = GetString(stateSpec, "motion_path", "motionPath", "clip_path", "clipPath");
                    if (!string.IsNullOrWhiteSpace(motionPath))
                    {
                        state.motion = LoadMotion(motionPath);
                        report.Add("updated", "motion", null, stateName, motionPath);
                    }
                }
            }

            foreach (var childSpec in GetArray(stateMachineSpec, "state_machines", "stateMachines", "StateMachines").OfType<JObject>())
            {
                var childName = RequireString(childSpec, "name", "Name", "state_machine", "stateMachine");
                var existing = FindDirectStateMachine(stateMachine, childName);
                if (GetBool(childSpec, false, "remove", "Remove"))
                {
                    if (existing != null)
                    {
                        stateMachine.RemoveStateMachine(existing);
                        report.Add("removed", "state_machine", null, CombinePath(pathInLayer, childName), layerName);
                    }
                    continue;
                }

                AnimatorStateMachine childMachine;
                if (existing == null)
                {
                    childMachine = new AnimatorStateMachine
                    {
                        name = childName,
                        hideFlags = HideFlags.HideInHierarchy
                    };
                    AssetDatabase.AddObjectToAsset(childMachine, controller);
                    Undo.RegisterCreatedObjectUndo(childMachine, "Apply Animator Graph State Machine");
                    stateMachine.AddStateMachine(childMachine, ParsePosition(childSpec["position"] as JArray) ?? NewStateMachinePosition(stateMachine));
                    report.Add("created", "state_machine", null, CombinePath(pathInLayer, childName), layerName);
                }
                else
                {
                    childMachine = existing;
                    if (childSpec["position"] is JArray position)
                        SetStateMachineGraphPosition(stateMachine, childMachine, ParsePosition(position).GetValueOrDefault());
                    report.Add("updated", "state_machine", null, CombinePath(pathInLayer, childName), layerName);
                }

                ApplyGraphStateMachineStructure(controller, rootStateMachine, childMachine, childSpec, options, report, layerName, CombinePath(pathInLayer, childName));
            }

            var defaultStateName = GetString(stateMachineSpec, "default_state", "defaultState");
            if (!string.IsNullOrWhiteSpace(defaultStateName))
            {
                var defaultState = ResolveState(stateMachine, defaultStateName);
                stateMachine.defaultState = defaultState;
                report.Add("updated", "default_state", null, CombinePath(pathInLayer, defaultStateName), layerName);
            }

            if (options.RemoveMissingStates && declaredStateNames.Count > 0)
            {
                foreach (var child in stateMachine.states.ToArray())
                {
                    if (child.state == null || declaredStateNames.Contains(child.state.name))
                        continue;

                    stateMachine.RemoveState(child.state);
                    report.Add("removed", "state", null, CombinePath(pathInLayer, child.state.name), layerName);
                }
            }
        }

        static void ApplyGraphTransitions(AnimatorController controller, int layerIndex, JObject layerSpec, GraphApplyOptions options, GraphApplyReport report)
        {
            var layer = controller.layers[layerIndex];
            ApplyGraphStateMachineTransitions(controller, layer.stateMachine, layer.stateMachine, layerSpec, options, report, layer.name);
        }

        static void ApplyGraphStateMachineTransitions(
            AnimatorController controller,
            AnimatorStateMachine rootStateMachine,
            AnimatorStateMachine stateMachine,
            JObject stateMachineSpec,
            GraphApplyOptions options,
            GraphApplyReport report,
            string layerName)
        {
            foreach (var transitionSpec in CollectGraphTransitions(stateMachineSpec))
                ApplyGraphTransition(controller, rootStateMachine, stateMachine, layerName, transitionSpec, options, report);

            foreach (var childSpec in GetArray(stateMachineSpec, "state_machines", "stateMachines", "StateMachines").OfType<JObject>())
            {
                if (GetBool(childSpec, false, "remove", "Remove"))
                    continue;

                var childName = GetString(childSpec, "name", "Name", "state_machine", "stateMachine");
                var child = FindDirectStateMachine(stateMachine, childName);
                if (child != null)
                    ApplyGraphStateMachineTransitions(controller, rootStateMachine, child, childSpec, options, report, layerName);
            }
        }

        static List<JObject> CollectGraphTransitions(JObject stateMachineSpec)
        {
            var transitions = new List<JObject>();
            foreach (var transition in GetArray(stateMachineSpec, "transitions", "Transitions").OfType<JObject>())
                transitions.Add((JObject)transition.DeepClone());

            foreach (var transition in GetArray(stateMachineSpec, "entry_transitions", "entryTransitions", "EntryTransitions").OfType<JObject>())
            {
                var clone = (JObject)transition.DeepClone();
                clone["entry"] = true;
                transitions.Add(clone);
            }

            foreach (var transition in GetArray(stateMachineSpec, "any_state_transitions", "anyStateTransitions", "AnyStateTransitions").OfType<JObject>())
            {
                var clone = (JObject)transition.DeepClone();
                clone["any_state"] = true;
                transitions.Add(clone);
            }

            foreach (var stateSpec in GetArray(stateMachineSpec, "states", "States").OfType<JObject>())
            {
                var stateName = GetString(stateSpec, "name", "Name", "state", "State");
                if (string.IsNullOrWhiteSpace(stateName))
                    continue;

                foreach (var transition in GetArray(stateSpec, "transitions", "Transitions").OfType<JObject>())
                {
                    var clone = (JObject)transition.DeepClone();
                    if (clone["from_state"] == null && clone["fromState"] == null)
                        clone["from_state"] = stateName;
                    transitions.Add(clone);
                }
            }

            return transitions;
        }

        static int CountGraphTransitions(JObject layerSpec)
        {
            return CollectGraphTransitions(layerSpec).Count;
        }

        static void ApplyGraphTransition(
            AnimatorController controller,
            AnimatorStateMachine rootStateMachine,
            AnimatorStateMachine stateMachine,
            string layerName,
            JObject transitionSpec,
            GraphApplyOptions options,
            GraphApplyReport report)
        {
            var entry = GetBool(transitionSpec, false, "entry", "entry_transition", "entryTransition");
            var anyState = GetBool(transitionSpec, false, "any_state", "anyState");
            var toExit = GetBool(transitionSpec, false, "to_exit", "toExit", "exit");
            var fromStateName = GetString(transitionSpec, "from_state", "fromState", "state", "State");
            var toStateName = GetString(transitionSpec, "to_state", "toState", "destination_state", "destinationState");
            var destinationStateMachineName = GetString(transitionSpec, "destination_state_machine", "destinationStateMachine", "to_state_machine", "toStateMachine");

            EnsureGraphTransitionCanRun(transitionSpec, layerName, entry ? "Entry" : anyState ? "Any State" : fromStateName, toExit ? "Exit" : !string.IsNullOrWhiteSpace(destinationStateMachineName) && string.IsNullOrWhiteSpace(toStateName) ? destinationStateMachineName : toStateName);

            if (entry)
            {
                if (toExit || !string.IsNullOrWhiteSpace(destinationStateMachineName))
                    throw new InvalidOperationException("Entry transitions must target a destination state.");

                if (options.ReplaceTransitions)
                    RemoveEntryTransition(stateMachine, toStateName);

                var transition = stateMachine.AddEntryTransition(ResolveState(stateMachine, toStateName));
                ApplyTransitionSettings(transition, transitionSpec);
                report.Add("upserted", "transition", null, $"Entry -> {toStateName}", layerName);
                return;
            }

            if (anyState)
            {
                if (toExit)
                    throw new InvalidOperationException("Any State transitions cannot target Exit.");
                if (!string.IsNullOrWhiteSpace(destinationStateMachineName))
                    throw new InvalidOperationException("Any State transitions must target a destination state in the selected state machine.");

                if (options.ReplaceTransitions)
                    RemoveAnyStateTransition(stateMachine, toStateName);
                else if (FindAnyStateTransition(stateMachine, toStateName) != null)
                {
                    report.Add("skipped", "transition", null, $"Any State -> {toStateName}", layerName);
                    return;
                }

                var transition = stateMachine.AddAnyStateTransition(ResolveState(stateMachine, toStateName));
                ApplyTransitionSettings(transition, transitionSpec);
                report.Add("upserted", "transition", null, $"Any State -> {toStateName}", layerName);
                return;
            }

            var fromState = ResolveState(rootStateMachine, fromStateName);
            var label = toExit ? $"{fromStateName} -> Exit" :
                !string.IsNullOrWhiteSpace(destinationStateMachineName) && string.IsNullOrWhiteSpace(toStateName)
                    ? $"{fromStateName} -> {destinationStateMachineName}"
                    : $"{fromStateName} -> {toStateName}";
            if (options.ReplaceTransitions)
                RemoveStateTransition(rootStateMachine, fromStateName, toStateName, destinationStateMachineName, toExit);
            else if (FindStateTransition(rootStateMachine, fromStateName, toStateName, destinationStateMachineName, toExit) != null)
            {
                report.Add("skipped", "transition", null, label, layerName);
                return;
            }

            var newTransition = toExit
                ? fromState.AddExitTransition()
                : !string.IsNullOrWhiteSpace(destinationStateMachineName) && string.IsNullOrWhiteSpace(toStateName)
                    ? fromState.AddTransition(ResolveStateMachine(rootStateMachine, destinationStateMachineName))
                    : fromState.AddTransition(ResolveTransitionDestinationState(rootStateMachine, stateMachine, toStateName, destinationStateMachineName));
            ApplyTransitionSettings(newTransition, transitionSpec);
            report.Add("upserted", "transition", null, label, layerName);
        }

        static void EnsureGraphTransitionCanRun(JObject transitionSpec, string layerName, string from, string to)
        {
            if (string.Equals(from, "Entry", StringComparison.Ordinal))
                return;

            var hasExitTime = GetBool(transitionSpec, false, "has_exit_time", "hasExitTime");
            var conditions = transitionSpec["conditions"] as JArray ?? transitionSpec["Conditions"] as JArray;
            if (hasExitTime || (conditions != null && conditions.Count > 0))
                return;

            throw new InvalidOperationException($"Transition '{from} -> {to}' in layer '{layerName}' requires has_exit_time=true or at least one condition. Unity ignores transitions with neither.");
        }

        static string ResolveGraphLayerName(JObject layerSpec, int index)
        {
            var layerName = GetString(layerSpec, "name", "Name", "layer", "Layer", "layer_name", "layerName");
            if (!string.IsNullOrWhiteSpace(layerName))
                return layerName;

            if (index == 0)
                return "Base Layer";

            throw new InvalidOperationException($"Graph layer at index {index} requires 'name' or 'layer'.");
        }

        static void EnsureBlendTreeOnState(AnimatorController controller, AnimatorState state, string stateName, JObject blendTreeSpec, bool replaceExisting, GraphApplyReport report, string layerName)
        {
            var blendTree = state.motion as BlendTree;
            if (blendTree == null)
            {
                if (state.motion != null && !replaceExisting)
                    throw new InvalidOperationException($"Animator state '{stateName}' already has a non-BlendTree motion. Set replace_existing=true to replace it.");

                blendTree = new BlendTree
                {
                    name = GetString(blendTreeSpec, "name", "Name", "blend_tree_name", "blendTreeName") ?? $"{stateName}_BlendTree"
                };
                AssetDatabase.AddObjectToAsset(blendTree, controller);
                state.motion = blendTree;
                Undo.RegisterCreatedObjectUndo(blendTree, "Apply Animator Graph BlendTree");
                report.Add("created", "blend_tree", null, stateName, layerName);
            }
            else
            {
                var name = GetString(blendTreeSpec, "name", "Name", "blend_tree_name", "blendTreeName");
                if (!string.IsNullOrWhiteSpace(name))
                    blendTree.name = name;
                report.Add("updated", "blend_tree", null, stateName, layerName);
            }

            ApplyBlendTreeSettings(blendTree, blendTreeSpec);
            var children = blendTreeSpec["children"] as JArray ?? blendTreeSpec["Children"] as JArray;
            if (children != null)
            {
                blendTree.children = BuildBlendChildren(children);
                report.Add("updated", "blend_tree_children", null, stateName, $"{children.Count} child(ren)");
            }

            EditorUtility.SetDirty(blendTree);
        }

        static void SetStateGraphPosition(AnimatorStateMachine stateMachine, AnimatorState state, Vector3 position)
        {
            if (stateMachine == null || state == null)
                return;

            var children = stateMachine.states;
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i].state != state)
                    continue;

                var child = children[i];
                child.position = position;
                children[i] = child;
                stateMachine.states = children;
                return;
            }
        }

        static void SetStateMachineGraphPosition(AnimatorStateMachine parent, AnimatorStateMachine child, Vector3 position)
        {
            if (parent == null || child == null)
                return;

            var children = parent.stateMachines;
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i].stateMachine != child)
                    continue;

                var entry = children[i];
                entry.position = position;
                children[i] = entry;
                parent.stateMachines = children;
                return;
            }
        }

        static void ApplyStateMachineSettings(AnimatorStateMachine stateMachine, JObject @params)
        {
            if (stateMachine == null || @params == null)
                return;

            var entryPosition = GetArrayToken(@params, "entry_position", "entryPosition");
            if (entryPosition != null)
                stateMachine.entryPosition = ParsePosition(entryPosition).GetValueOrDefault(stateMachine.entryPosition);

            var exitPosition = GetArrayToken(@params, "exit_position", "exitPosition");
            if (exitPosition != null)
                stateMachine.exitPosition = ParsePosition(exitPosition).GetValueOrDefault(stateMachine.exitPosition);

            var anyStatePosition = GetArrayToken(@params, "any_state_position", "anyStatePosition");
            if (anyStatePosition != null)
                stateMachine.anyStatePosition = ParsePosition(anyStatePosition).GetValueOrDefault(stateMachine.anyStatePosition);

            var parentPosition = GetArrayToken(@params, "parent_state_machine_position", "parentStateMachinePosition");
            if (parentPosition != null)
                stateMachine.parentStateMachinePosition = ParsePosition(parentPosition).GetValueOrDefault(stateMachine.parentStateMachinePosition);
        }

        static void ApplyLayerSettings(AnimatorController controller, int layerIndex, JObject @params)
        {
            var layers = controller.layers;
            if (layerIndex < 0 || layerIndex >= layers.Length)
                throw new InvalidOperationException($"Layer index {layerIndex} is out of range.");

            var layer = layers[layerIndex];
            var weight = GetFloat(@params, float.NaN, "weight", "default_weight", "defaultWeight");
            if (!float.IsNaN(weight))
                layer.defaultWeight = weight;

            var blendingMode = GetString(@params, "blending_mode", "blendingMode");
            if (!string.IsNullOrWhiteSpace(blendingMode))
                layer.blendingMode = ParseBlendingMode(blendingMode);

            var avatarMaskPath = GetString(@params, "avatar_mask", "avatarMask", "avatar_mask_path", "avatarMaskPath");
            if (!string.IsNullOrWhiteSpace(avatarMaskPath))
            {
                var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(NormalizeAssetPath(avatarMaskPath));
                if (mask == null)
                    throw new InvalidOperationException($"AvatarMask asset was not found at '{NormalizeAssetPath(avatarMaskPath)}'.");
                layer.avatarMask = mask;
            }

            if (GetToken(@params, "ik_pass", "iKPass", "ikPass") != null)
                layer.iKPass = GetBool(@params, layer.iKPass, "ik_pass", "iKPass", "ikPass");

            var syncedLayerIndex = GetNullableInt(@params, "synced_layer_index", "syncedLayerIndex");
            if (syncedLayerIndex.HasValue)
                layer.syncedLayerIndex = syncedLayerIndex.Value;

            if (GetToken(@params, "synced_layer_affects_timing", "syncedLayerAffectsTiming") != null)
                layer.syncedLayerAffectsTiming = GetBool(@params, layer.syncedLayerAffectsTiming, "synced_layer_affects_timing", "syncedLayerAffectsTiming");

            layers[layerIndex] = layer;
            controller.layers = layers;
        }

        static void ApplyParameterDefaults(AnimatorController controller, string parameterName, JObject @params)
        {
            var parameters = controller.parameters;
            var index = Array.FindIndex(parameters, parameter => parameter.name == parameterName);
            if (index < 0)
                return;

            var parameter = parameters[index];
            if (@params["default_float"] != null || @params["defaultFloat"] != null)
                parameter.defaultFloat = GetFloat(@params, parameter.defaultFloat, "default_float", "defaultFloat");
            if (@params["default_int"] != null || @params["defaultInt"] != null)
                parameter.defaultInt = GetInt(@params, parameter.defaultInt, "default_int", "defaultInt");
            if (@params["default_bool"] != null || @params["defaultBool"] != null)
                parameter.defaultBool = GetBool(@params, parameter.defaultBool, "default_bool", "defaultBool");

            parameters[index] = parameter;
            controller.parameters = parameters;
        }

        static void ApplyStateSettings(AnimatorState state, JObject @params)
        {
            var speed = GetFloat(@params, float.NaN, "speed", "Speed");
            if (!float.IsNaN(speed))
                state.speed = speed;

            if (GetToken(@params, "speed_parameter_active", "speedParameterActive") != null)
                state.speedParameterActive = GetBool(@params, state.speedParameterActive, "speed_parameter_active", "speedParameterActive");
            var speedParameter = GetString(@params, "speed_parameter", "speedParameter");
            if (!string.IsNullOrWhiteSpace(speedParameter))
                state.speedParameter = speedParameter;

            var cycleOffset = GetFloat(@params, float.NaN, "cycle_offset", "cycleOffset");
            if (!float.IsNaN(cycleOffset))
                state.cycleOffset = cycleOffset;
            if (GetToken(@params, "cycle_offset_parameter_active", "cycleOffsetParameterActive") != null)
                state.cycleOffsetParameterActive = GetBool(@params, state.cycleOffsetParameterActive, "cycle_offset_parameter_active", "cycleOffsetParameterActive");
            var cycleOffsetParameter = GetString(@params, "cycle_offset_parameter", "cycleOffsetParameter");
            if (!string.IsNullOrWhiteSpace(cycleOffsetParameter))
                state.cycleOffsetParameter = cycleOffsetParameter;

            if (GetToken(@params, "mirror", "Mirror") != null)
                state.mirror = GetBool(@params, state.mirror, "mirror", "Mirror");
            if (GetToken(@params, "mirror_parameter_active", "mirrorParameterActive") != null)
                state.mirrorParameterActive = GetBool(@params, state.mirrorParameterActive, "mirror_parameter_active", "mirrorParameterActive");
            var mirrorParameter = GetString(@params, "mirror_parameter", "mirrorParameter");
            if (!string.IsNullOrWhiteSpace(mirrorParameter))
                state.mirrorParameter = mirrorParameter;

            if (GetToken(@params, "ik_on_feet", "iKOnFeet", "ikOnFeet") != null)
                state.iKOnFeet = GetBool(@params, state.iKOnFeet, "ik_on_feet", "iKOnFeet", "ikOnFeet");
            if (GetToken(@params, "time_parameter_active", "timeParameterActive") != null)
                state.timeParameterActive = GetBool(@params, state.timeParameterActive, "time_parameter_active", "timeParameterActive");
            var timeParameter = GetString(@params, "time_parameter", "timeParameter");
            if (!string.IsNullOrWhiteSpace(timeParameter))
                state.timeParameter = timeParameter;

            if (@params["write_default_values"] != null || @params["writeDefaultValues"] != null)
                state.writeDefaultValues = GetBool(@params, state.writeDefaultValues, "write_default_values", "writeDefaultValues");

            var tag = GetString(@params, "tag", "Tag");
            if (!string.IsNullOrWhiteSpace(tag))
                state.tag = tag;
        }

        static void ApplyTransitionSettings(AnimatorTransitionBase transition, JObject @params)
        {
            if (GetToken(@params, "mute", "Mute") != null)
                transition.mute = GetBool(@params, transition.mute, "mute", "Mute");
            if (GetToken(@params, "solo", "Solo") != null)
                transition.solo = GetBool(@params, transition.solo, "solo", "Solo");

            if (transition is AnimatorStateTransition stateTransition)
            {
                if (@params["has_exit_time"] != null || @params["hasExitTime"] != null)
                    stateTransition.hasExitTime = GetBool(@params, stateTransition.hasExitTime, "has_exit_time", "hasExitTime");
                stateTransition.exitTime = GetFloat(@params, stateTransition.exitTime, "exit_time", "exitTime");
                stateTransition.duration = GetFloat(@params, stateTransition.duration, "duration", "Duration");
                stateTransition.offset = GetFloat(@params, stateTransition.offset, "offset", "Offset");

                if (GetToken(@params, "has_fixed_duration", "hasFixedDuration") != null)
                    stateTransition.hasFixedDuration = GetBool(@params, stateTransition.hasFixedDuration, "has_fixed_duration", "hasFixedDuration");

                var interruptionSource = GetString(@params, "interruption_source", "interruptionSource");
                if (!string.IsNullOrWhiteSpace(interruptionSource))
                    stateTransition.interruptionSource = ParseInterruptionSource(interruptionSource);

                if (GetToken(@params, "ordered_interruption", "orderedInterruption") != null)
                    stateTransition.orderedInterruption = GetBool(@params, stateTransition.orderedInterruption, "ordered_interruption", "orderedInterruption");

                if (GetToken(@params, "can_transition_to_self", "canTransitionToSelf") != null)
                    stateTransition.canTransitionToSelf = GetBool(@params, stateTransition.canTransitionToSelf, "can_transition_to_self", "canTransitionToSelf");
            }

            var conditions = @params["conditions"] ?? @params["Conditions"];
            if (conditions is JArray array)
            {
                foreach (var item in array.OfType<JObject>())
                {
                    var parameter = RequireString(item, "parameter", "Parameter");
                    var mode = ParseConditionMode(RequireString(item, "mode", "Mode"));
                    var threshold = GetFloat(item, 0f, "threshold", "Threshold");
                    transition.AddCondition(mode, threshold, parameter);
                }
            }
        }

        static bool RemoveStateTransition(AnimatorStateMachine stateMachine, string fromStateName, string toStateName, string destinationStateMachineName, bool toExit)
        {
            var fromState = ResolveState(stateMachine, fromStateName);
            foreach (var transition in fromState.transitions)
            {
                if (toExit && transition.isExit)
                {
                    fromState.RemoveTransition(transition);
                    return true;
                }

                if (!toExit && transition.destinationStateMachine != null && !string.IsNullOrWhiteSpace(destinationStateMachineName) &&
                    string.Equals(transition.destinationStateMachine.name, destinationStateMachineName, StringComparison.Ordinal))
                {
                    fromState.RemoveTransition(transition);
                    return true;
                }

                if (!toExit && transition.destinationState != null &&
                    string.Equals(transition.destinationState.name, toStateName, StringComparison.Ordinal))
                {
                    fromState.RemoveTransition(transition);
                    return true;
                }
            }

            return false;
        }

        static bool RemoveAnyStateTransition(AnimatorStateMachine stateMachine, string toStateName)
        {
            foreach (var transition in stateMachine.anyStateTransitions)
            {
                if (transition.destinationState != null &&
                    string.Equals(transition.destinationState.name, toStateName, StringComparison.Ordinal))
                {
                    stateMachine.RemoveAnyStateTransition(transition);
                    return true;
                }
            }

            return false;
        }

        static bool RemoveEntryTransition(AnimatorStateMachine stateMachine, string toStateName)
        {
            foreach (var transition in stateMachine.entryTransitions)
            {
                if (string.IsNullOrWhiteSpace(toStateName) ||
                    (transition.destinationState != null &&
                     string.Equals(transition.destinationState.name, toStateName, StringComparison.Ordinal)))
                {
                    stateMachine.RemoveEntryTransition(transition);
                    return true;
                }
            }

            return false;
        }

        static AnimatorStateTransition FindStateTransition(AnimatorStateMachine stateMachine, string fromStateName, string toStateName, string destinationStateMachineName, bool toExit)
        {
            var fromState = FindState(stateMachine, fromStateName);
            if (fromState == null)
                return null;

            foreach (var transition in fromState.transitions)
            {
                if (toExit && transition.isExit)
                    return transition;

                if (!toExit && transition.destinationStateMachine != null && !string.IsNullOrWhiteSpace(destinationStateMachineName) &&
                    string.Equals(transition.destinationStateMachine.name, destinationStateMachineName, StringComparison.Ordinal))
                {
                    return transition;
                }

                if (!toExit && transition.destinationState != null &&
                    string.Equals(transition.destinationState.name, toStateName, StringComparison.Ordinal))
                {
                    return transition;
                }
            }

            return null;
        }

        static AnimatorStateTransition FindAnyStateTransition(AnimatorStateMachine stateMachine, string toStateName)
        {
            foreach (var transition in stateMachine.anyStateTransitions)
            {
                if (transition.destinationState != null &&
                    string.Equals(transition.destinationState.name, toStateName, StringComparison.Ordinal))
                {
                    return transition;
                }
            }

            return null;
        }

        static AnimatorController LoadController(string path)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                throw new InvalidOperationException($"Animator Controller was not found at '{path}'.");

            return controller;
        }

        static string ResolveControllerPath(JObject @params)
        {
            return NormalizeControllerPath(GetString(@params, "path", "controller_path", "controllerPath", "asset_path", "assetPath"));
        }

        static string NormalizeControllerPath(string path)
        {
            var normalized = NormalizeAssetPath(path);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new InvalidOperationException("Animator Controller path is required.");

            if (!normalized.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Animator Controller path must end with .controller.");

            return normalized;
        }

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = path.Trim().Replace('\\', '/');
            if (normalized.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("unity://path/".Length);

            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return normalized;

            if (normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Packages", StringComparison.OrdinalIgnoreCase))
                return normalized;

            return "Assets/" + normalized.TrimStart('/');
        }

        static void EnsureWritableControllerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Animator Controller changes are only allowed under Assets/.");
        }

        static void EnsureParentDirectoryExists(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || AssetDatabase.IsValidFolder(directory))
                return;

            var current = "Assets";
            foreach (var part in directory.Substring("Assets".Length).Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, part);
                current = next;
            }
        }

        static int ResolveLayerIndex(AnimatorController controller, JObject @params)
        {
            var explicitIndex = GetNullableInt(@params, "layer_index", "layerIndex");
            if (explicitIndex.HasValue)
            {
                if (explicitIndex.Value < 0 || explicitIndex.Value >= controller.layers.Length)
                    throw new InvalidOperationException($"Layer index {explicitIndex.Value} is out of range.");
                return explicitIndex.Value;
            }

            var layerName = GetString(@params, "layer", "layer_name", "layerName");
            if (string.IsNullOrWhiteSpace(layerName))
                return 0;

            var index = FindLayerIndex(controller, layerName);
            if (index < 0)
                throw new InvalidOperationException($"Animator layer '{layerName}' was not found.");

            return index;
        }

        static int FindLayerIndex(AnimatorController controller, string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return -1;

            for (var i = 0; i < controller.layers.Length; i++)
            {
                if (string.Equals(controller.layers[i].name, layerName, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        static AnimatorState ResolveState(AnimatorStateMachine stateMachine, string stateName)
        {
            var state = FindState(stateMachine, stateName);
            if (state == null)
                throw new InvalidOperationException($"Animator state '{stateName}' was not found.");
            return state;
        }

        static AnimatorState ResolveTransitionDestinationState(
            AnimatorStateMachine rootStateMachine,
            AnimatorStateMachine currentStateMachine,
            string toStateName,
            string destinationStateMachineName)
        {
            if (!string.IsNullOrWhiteSpace(destinationStateMachineName))
                return ResolveState(ResolveStateMachine(rootStateMachine, destinationStateMachineName), toStateName);

            var direct = FindDirectStateWithPosition(currentStateMachine, toStateName).State;
            return direct != null ? direct : ResolveState(rootStateMachine, toStateName);
        }

        static AnimatorState FindState(AnimatorStateMachine stateMachine, string stateName)
        {
            return FindStateWithOwner(stateMachine, stateName).State;
        }

        static bool AnimatorStateExists(AnimatorStateMachine stateMachine, string stateName)
        {
            return FindState(stateMachine, stateName) != null;
        }

        static (AnimatorState State, AnimatorStateMachine Owner) FindStateWithOwner(AnimatorStateMachine stateMachine, string stateName)
        {
            if (stateMachine == null || string.IsNullOrWhiteSpace(stateName))
                return (null, null);

            foreach (var child in stateMachine.states)
            {
                if (child.state == null)
                    continue;

                if (string.Equals(child.state.name, stateName, StringComparison.Ordinal) ||
                    string.Equals(CombinePath(stateMachine.name, child.state.name), stateName, StringComparison.Ordinal) ||
                    stateName.EndsWith("/" + child.state.name, StringComparison.Ordinal))
                {
                    return (child.state, stateMachine);
                }
            }

            foreach (var childMachine in stateMachine.stateMachines)
            {
                var found = FindStateWithOwner(childMachine.stateMachine, stateName);
                if (found.State != null)
                    return found;
            }

            return (null, null);
        }

        static AnimatorStateMachine ResolveOptionalStateMachine(AnimatorStateMachine rootStateMachine, JObject @params)
        {
            var name = GetString(@params, "state_machine", "stateMachine");
            return string.IsNullOrWhiteSpace(name) ? null : ResolveStateMachine(rootStateMachine, name);
        }

        static AnimatorStateMachine ResolveStateMachine(AnimatorStateMachine rootStateMachine, string stateMachineName)
        {
            var found = FindStateMachine(rootStateMachine, stateMachineName);
            if (found == null)
                throw new InvalidOperationException($"Animator state machine '{stateMachineName}' was not found.");
            return found;
        }

        static AnimatorStateMachine FindStateMachine(AnimatorStateMachine rootStateMachine, string stateMachineName)
        {
            return FindStateMachineWithOwner(rootStateMachine, stateMachineName).StateMachine;
        }

        static (AnimatorStateMachine StateMachine, AnimatorStateMachine Owner) FindStateMachineWithOwner(AnimatorStateMachine stateMachine, string stateMachineName)
        {
            if (stateMachine == null || string.IsNullOrWhiteSpace(stateMachineName))
                return (null, null);

            if (string.Equals(stateMachine.name, stateMachineName, StringComparison.Ordinal) ||
                stateMachineName.EndsWith("/" + stateMachine.name, StringComparison.Ordinal))
            {
                return (stateMachine, null);
            }

            foreach (var child in stateMachine.stateMachines)
            {
                if (child.stateMachine == null)
                    continue;

                if (string.Equals(child.stateMachine.name, stateMachineName, StringComparison.Ordinal) ||
                    string.Equals(CombinePath(stateMachine.name, child.stateMachine.name), stateMachineName, StringComparison.Ordinal) ||
                    stateMachineName.EndsWith("/" + child.stateMachine.name, StringComparison.Ordinal))
                {
                    return (child.stateMachine, stateMachine);
                }

                var found = FindStateMachineWithOwner(child.stateMachine, stateMachineName);
                if (found.StateMachine != null)
                {
                    return (found.StateMachine, found.Owner ?? child.stateMachine);
                }
            }

            return (null, null);
        }

        static AnimatorStateMachine FindDirectStateMachine(AnimatorStateMachine parent, string stateMachineName)
        {
            if (parent == null || string.IsNullOrWhiteSpace(stateMachineName))
                return null;

            foreach (var child in parent.stateMachines)
            {
                if (child.stateMachine != null &&
                    string.Equals(child.stateMachine.name, stateMachineName, StringComparison.Ordinal))
                    return child.stateMachine;
            }

            return null;
        }

        static (AnimatorState State, Vector3 Position) FindDirectStateWithPosition(AnimatorStateMachine stateMachine, string stateName)
        {
            if (stateMachine == null || string.IsNullOrWhiteSpace(stateName))
                return (null, default);

            foreach (var child in stateMachine.states)
            {
                if (child.state != null && string.Equals(child.state.name, stateName, StringComparison.Ordinal))
                    return (child.state, child.position);
            }

            return (null, default);
        }

        static BlendTreeContext ResolveBlendTree(JObject @params)
        {
            var path = ResolveControllerPath(@params);
            var controller = LoadController(path);
            var layerIndex = ResolveLayerIndex(controller, @params);
            var stateName = RequireString(@params, "state", "name");
            var stateMachine = controller.layers[layerIndex].stateMachine;
            var found = FindStateWithOwner(stateMachine, stateName);
            if (found.State == null)
                throw new InvalidOperationException($"Animator state '{stateName}' was not found in layer '{controller.layers[layerIndex].name}'.");

            if (found.State.motion is not BlendTree blendTree)
                throw new InvalidOperationException($"Animator state '{stateName}' does not have a BlendTree motion.");

            return new BlendTreeContext(path, controller, layerIndex, found.Owner, found.State, blendTree);
        }

        static void ApplyBlendTreeSettings(BlendTree blendTree, JObject @params)
        {
            var blendType = GetString(@params, "blend_type", "blendType");
            if (!string.IsNullOrWhiteSpace(blendType))
                blendTree.blendType = ParseBlendTreeType(blendType);

            var blendParameter = GetString(@params, "blend_parameter", "blendParameter", "parameter", "Parameter");
            if (!string.IsNullOrWhiteSpace(blendParameter))
                blendTree.blendParameter = blendParameter;

            var blendParameterY = GetString(@params, "blend_parameter_y", "blendParameterY", "parameter_y", "parameterY");
            if (!string.IsNullOrWhiteSpace(blendParameterY))
                blendTree.blendParameterY = blendParameterY;

            if (GetToken(@params, "use_automatic_thresholds", "useAutomaticThresholds") != null)
                blendTree.useAutomaticThresholds = GetBool(@params, blendTree.useAutomaticThresholds, "use_automatic_thresholds", "useAutomaticThresholds");

            if (GetToken(@params, "min_threshold", "minThreshold") != null)
                blendTree.minThreshold = GetFloat(@params, blendTree.minThreshold, "min_threshold", "minThreshold");

            if (GetToken(@params, "max_threshold", "maxThreshold") != null)
                blendTree.maxThreshold = GetFloat(@params, blendTree.maxThreshold, "max_threshold", "maxThreshold");
        }

        static ChildMotion[] BuildBlendChildren(JArray children)
        {
            if (children == null)
                return Array.Empty<ChildMotion>();

            var result = new List<ChildMotion>();
            foreach (var childToken in children)
            {
                if (childToken is not JObject childObject)
                    throw new InvalidOperationException("Each BlendTree child must be an object.");

                result.Add(BuildBlendChild(childObject));
            }

            return result.ToArray();
        }

        static ChildMotion BuildBlendChild(JObject @params)
        {
            var motionPath = GetString(@params, "motion_path", "motionPath", "clip_path", "clipPath");
            var child = new ChildMotion
            {
                motion = string.IsNullOrWhiteSpace(motionPath) ? null : LoadMotion(motionPath),
                threshold = GetFloat(@params, 0f, "threshold", "Threshold"),
                position = ParseVector2(@params["position"] as JArray) ?? new Vector2(
                    GetFloat(@params, 0f, "x", "X"),
                    GetFloat(@params, 0f, "y", "Y")),
                timeScale = GetFloat(@params, 1f, "time_scale", "timeScale"),
                cycleOffset = GetFloat(@params, 0f, "cycle_offset", "cycleOffset"),
                mirror = GetBool(@params, false, "mirror", "Mirror"),
                directBlendParameter = GetString(@params, "direct_blend_parameter", "directBlendParameter")
            };

            return child;
        }

        static Motion LoadMotion(string path)
        {
            var normalized = NormalizeAssetPath(path);
            var motion = AssetDatabase.LoadAssetAtPath<Motion>(normalized);
            if (motion == null)
                throw new InvalidOperationException($"Motion asset was not found at '{normalized}'. Expected AnimationClip or BlendTree-backed Motion.");
            return motion;
        }

        static Vector3 NewStatePosition(AnimatorStateMachine stateMachine)
        {
            var count = stateMachine.states?.Length ?? 0;
            return new Vector3(300 + (count % 4) * 220, 100 + (count / 4) * 80, 0);
        }

        static Vector3 NewStateMachinePosition(AnimatorStateMachine stateMachine)
        {
            var count = stateMachine.stateMachines?.Length ?? 0;
            return new Vector3(320 + (count % 3) * 260, 260 + (count / 3) * 120, 0);
        }

        static Vector3? ParsePosition(JArray array)
        {
            if (array == null)
                return null;
            if (array.Count < 2 || array.Count > 3)
                throw new InvalidOperationException("Position must be [x,y] or [x,y,z].");
            return new Vector3(array[0].ToObject<float>(), array[1].ToObject<float>(), array.Count >= 3 ? array[2].ToObject<float>() : 0f);
        }

        static Vector2? ParseVector2(JArray array)
        {
            if (array == null)
                return null;
            if (array.Count != 2)
                throw new InvalidOperationException("BlendTree child position must be [x,y].");
            return new Vector2(array[0].ToObject<float>(), array[1].ToObject<float>());
        }

        static AnimatorControllerParameterType ParseParameterType(string type)
        {
            if (Enum.TryParse(type, true, out AnimatorControllerParameterType parsed))
                return parsed;
            throw new InvalidOperationException($"Unsupported Animator parameter type '{type}'. Use Float, Int, Bool, or Trigger.");
        }

        static AnimatorLayerBlendingMode ParseBlendingMode(string mode)
        {
            if (Enum.TryParse(mode, true, out AnimatorLayerBlendingMode parsed))
                return parsed;
            throw new InvalidOperationException($"Unsupported Animator layer blending mode '{mode}'. Use Override or Additive.");
        }

        static BlendTreeType ParseBlendTreeType(string type)
        {
            var normalized = type?.Trim();
            if (string.Equals(normalized, "1d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "simple_1d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "simple1d", StringComparison.OrdinalIgnoreCase))
                return BlendTreeType.Simple1D;
            if (string.Equals(normalized, "directional_2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "simple_directional_2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "simpledirectional2d", StringComparison.OrdinalIgnoreCase))
                return BlendTreeType.SimpleDirectional2D;
            if (string.Equals(normalized, "freeform_directional_2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "freeformdirectional2d", StringComparison.OrdinalIgnoreCase))
                return BlendTreeType.FreeformDirectional2D;
            if (string.Equals(normalized, "freeform_cartesian_2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "freeformcartesian2d", StringComparison.OrdinalIgnoreCase))
                return BlendTreeType.FreeformCartesian2D;

            if (Enum.TryParse(normalized, true, out BlendTreeType parsed))
                return parsed;
            throw new InvalidOperationException($"Unsupported BlendTree type '{type}'. Use Simple1D, SimpleDirectional2D, FreeformDirectional2D, FreeformCartesian2D, or Direct.");
        }

        static AnimatorConditionMode ParseConditionMode(string mode)
        {
            var normalized = mode?.Trim();
            if (string.Equals(normalized, "if_not", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ifnot", StringComparison.OrdinalIgnoreCase))
                return AnimatorConditionMode.IfNot;
            if (string.Equals(normalized, "not_equal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "notequal", StringComparison.OrdinalIgnoreCase))
                return AnimatorConditionMode.NotEqual;

            if (Enum.TryParse(normalized, true, out AnimatorConditionMode parsed))
                return parsed;
            throw new InvalidOperationException($"Unsupported Animator condition mode '{mode}'. Use If, IfNot, Greater, Less, Equals, or NotEqual.");
        }

        static TransitionInterruptionSource ParseInterruptionSource(string mode)
        {
            var normalized = mode?.Trim();
            if (string.Equals(normalized, "source_then_destination", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "sourcethendestination", StringComparison.OrdinalIgnoreCase))
                return TransitionInterruptionSource.SourceThenDestination;
            if (string.Equals(normalized, "destination_then_source", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "destinationthensource", StringComparison.OrdinalIgnoreCase))
                return TransitionInterruptionSource.DestinationThenSource;

            if (Enum.TryParse(normalized, true, out TransitionInterruptionSource parsed))
                return parsed;
            throw new InvalidOperationException($"Unsupported transition interruption source '{mode}'. Use None, Source, Destination, SourceThenDestination, or DestinationThenSource.");
        }

        static void SaveController(AnimatorController controller, string path, params UnityEngine.Object[] extraDirtyObjects)
        {
            EditorUtility.SetDirty(controller);
            if (extraDirtyObjects != null)
            {
                foreach (var item in extraDirtyObjects)
                {
                    if (item != null)
                        EditorUtility.SetDirty(item);
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
        }

        sealed class BlendTreeContext
        {
            public BlendTreeContext(string path, AnimatorController controller, int layerIndex, AnimatorStateMachine owner, AnimatorState state, BlendTree blendTree)
            {
                Path = path;
                Controller = controller;
                LayerIndex = layerIndex;
                Owner = owner;
                State = state;
                BlendTree = blendTree;
            }

            public string Path { get; }
            public AnimatorController Controller { get; }
            public int LayerIndex { get; }
            public AnimatorStateMachine Owner { get; }
            public AnimatorState State { get; }
            public BlendTree BlendTree { get; }
        }

        sealed class GraphApplyOptions
        {
            public bool CreateIfMissing { get; private set; }
            public bool DryRun { get; private set; }
            public bool ReplaceTransitions { get; private set; }
            public bool RemoveMissingParameters { get; private set; }
            public bool RemoveMissingLayers { get; private set; }
            public bool RemoveMissingStates { get; private set; }

            public static GraphApplyOptions From(JObject @params)
            {
                return new GraphApplyOptions
                {
                    CreateIfMissing = GetBool(@params, true, "create_if_missing", "createIfMissing"),
                    DryRun = GetBool(@params, false, "dry_run", "dryRun"),
                    ReplaceTransitions = GetBool(@params, true, "replace_transitions", "replaceTransitions"),
                    RemoveMissingParameters = GetBool(@params, false, "remove_missing_parameters", "removeMissingParameters"),
                    RemoveMissingLayers = GetBool(@params, false, "remove_missing_layers", "removeMissingLayers"),
                    RemoveMissingStates = GetBool(@params, false, "remove_missing_states", "removeMissingStates")
                };
            }
        }

        sealed class GraphApplyReport
        {
            readonly List<GraphChange> m_Changes = new();

            public int Created { get; private set; }
            public int Updated { get; private set; }
            public int Removed { get; private set; }
            public int Skipped { get; private set; }
            public int Planned { get; private set; }

            public void Add(string operation, string subject, string path, string name, string detail)
            {
                operation ??= "updated";
                switch (operation.ToLowerInvariant())
                {
                    case "created":
                        Created++;
                        break;
                    case "updated":
                    case "upserted":
                        Updated++;
                        break;
                    case "removed":
                        Removed++;
                        break;
                    case "skipped":
                        Skipped++;
                        break;
                    default:
                        if (operation.StartsWith("would_", StringComparison.OrdinalIgnoreCase))
                            Planned++;
                        break;
                }

                m_Changes.Add(new GraphChange
                {
                    operation = operation,
                    subject = subject,
                    path = path,
                    name = name,
                    detail = detail
                });
            }

            public object ToObject() => new
            {
                created = Created,
                updated = Updated,
                removed = Removed,
                skipped = Skipped,
                planned = Planned,
                total = m_Changes.Count,
                changes = m_Changes
            };
        }

        sealed class GraphChange
        {
            public string operation;
            public string subject;
            public string path;
            public string name;
            public string detail;
        }

        static string CombinePath(string parent, string child)
        {
            if (string.IsNullOrWhiteSpace(child))
                return parent;
            return string.IsNullOrWhiteSpace(parent) ? child : parent + "/" + child;
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        static float[] ToArray(Vector3 vector)
        {
            return new[] { vector.x, vector.y, vector.z };
        }

        static string GetAnonymousString(object item, string propertyName)
        {
            return item?.GetType().GetProperty(propertyName)?.GetValue(item) as string;
        }

        static string RequireString(JObject obj, params string[] names)
        {
            var value = GetString(obj, names);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Required parameter missing: {string.Join("/", names)}.");
            return value;
        }

        static string GetString(JObject obj, params string[] names)
        {
            if (obj == null)
                return null;

            foreach (var name in names)
            {
                var token = obj[name];
                if (token != null && token.Type != JTokenType.Null)
                    return token.ToString();
            }

            return null;
        }

        static int GetInt(JObject obj, int defaultValue, params string[] names)
        {
            var token = GetToken(obj, names);
            return token == null ? defaultValue : token.ToObject<int>();
        }

        static int? GetNullableInt(JObject obj, params string[] names)
        {
            var token = GetToken(obj, names);
            return token == null ? null : token.ToObject<int>();
        }

        static float GetFloat(JObject obj, float defaultValue, params string[] names)
        {
            var token = GetToken(obj, names);
            return token == null ? defaultValue : token.ToObject<float>();
        }

        static bool GetBool(JObject obj, bool defaultValue, params string[] names)
        {
            var token = GetToken(obj, names);
            return token == null ? defaultValue : token.ToObject<bool>();
        }

        static JToken GetToken(JObject obj, params string[] names)
        {
            if (obj == null)
                return null;

            foreach (var name in names)
            {
                var token = obj[name];
                if (token != null && token.Type != JTokenType.Null)
                    return token;
            }

            return null;
        }

        static JArray GetArray(JObject obj, params string[] names)
        {
            var token = GetToken(obj, names);
            return token as JArray ?? new JArray();
        }

        static JArray GetArrayToken(JObject obj, params string[] names)
        {
            return GetToken(obj, names) as JArray;
        }
    }
}
