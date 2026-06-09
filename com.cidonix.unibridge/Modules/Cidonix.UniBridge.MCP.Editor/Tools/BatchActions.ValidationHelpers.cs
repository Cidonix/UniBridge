#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class BatchActions
    {
        static void ValidateComponentList(JToken token, ValidationReport report, string fieldName)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return;
            }

            if (token is not JArray array)
            {
                report.Error($"{fieldName} must be an array of component type names.");
                return;
            }

            foreach (var item in array)
            {
                var componentName = item is JObject componentObject
                    ? GetString(componentObject, "typeName", "TypeName", "type", "Type", "componentName", "ComponentName", "componentType", "ComponentType")
                    : item?.ToString();
                if (string.IsNullOrWhiteSpace(componentName))
                    report.Error($"{fieldName} contains an empty component type name.");
                else
                    ValidateComponentType(componentName, report);
            }
        }

        static void ValidateComponentType(string componentName, ValidationReport report)
        {
            if (string.Equals(componentName, "Transform", StringComparison.OrdinalIgnoreCase))
            {
                report.Warning("Transform is always present on GameObjects and cannot be added or removed.");
                return;
            }

            var type = FindComponentType(componentName);
            if (type == null)
            {
                report.Error($"Component type '{componentName}' was not found in loaded assemblies.");
            }
            else if (type.IsAbstract)
            {
                report.Error($"Component type '{componentName}' is abstract and cannot be added.");
            }
        }

        static Type FindComponentType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            var trimmed = typeName.Trim();
            return TypeCache.GetTypesDerivedFrom<Component>()
                .FirstOrDefault(type =>
                    string.Equals(type.Name, trimmed, StringComparison.Ordinal) ||
                    string.Equals(type.FullName, trimmed, StringComparison.Ordinal));
        }

        static void ValidatePrefabCreate(JObject parameters, ValidationReport report)
        {
            ValidateRequiredGameObjectTarget(parameters, report, "target/game_object");
            ValidatePrefabDestination(GetString(parameters, "prefab_path", "prefabPath", "PrefabPath"), report, required: true);
        }

        static void ValidatePrefabStatusTarget(JObject parameters, ValidationReport report)
        {
            var assetPath = GetPrefabSourcePath(parameters);
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                ValidateExistingAssetPath(assetPath, report, "prefab_path/asset_path");
                return;
            }

            ValidateRequiredGameObjectTarget(parameters, report, "target/game_object");
        }

        static void ValidateAnimatorParameter(JObject parameters, AnimatorController controller, ValidationReport report, bool mustExist)
        {
            var parameterName = GetString(parameters, "parameter", "Parameter", "name", "Name");
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                report.Error("Animator parameter action requires 'parameter' or 'name'.");
                return;
            }

            var exists = controller.parameters.Any(parameter => string.Equals(parameter.name, parameterName, StringComparison.Ordinal));
            if (mustExist && !exists)
                report.Error($"Animator parameter '{parameterName}' does not exist.");
            else if (!mustExist && exists)
                report.Error($"Animator parameter '{parameterName}' already exists.");
        }

        static bool IsKnownAnimatorParameterType(string type)
        {
            return !string.IsNullOrWhiteSpace(type) &&
                   Enum.TryParse<AnimatorControllerParameterType>(type, true, out _);
        }

        static void ValidateAnimatorLayerReference(JObject parameters, AnimatorController controller, ValidationReport report, bool allowNameAsLayer = false)
        {
            if (!TryResolveLayerIndex(parameters, controller, out _, allowNameAsLayer))
            {
                var layer = GetString(parameters, "layer", "Layer", "layer_name", "layerName", "name", "Name");
                if (HasAnyToken(parameters, "layer_index", "layerIndex", "LayerIndex"))
                    report.Error("Animator layer_index is not a valid layer index.");
                else
                    report.Error($"Animator layer '{layer}' does not exist.");
            }
        }

        static bool TryResolveLayerIndex(JObject parameters, AnimatorController controller, out int index, bool allowNameAsLayer = false)
        {
            index = 0;
            var indexToken = parameters["layer_index"] ?? parameters["layerIndex"] ?? parameters["LayerIndex"];
            if (indexToken != null && indexToken.Type != JTokenType.Null)
            {
                if (!int.TryParse(indexToken.ToString(), out index))
                    return false;

                return index >= 0 && index < controller.layers.Length;
            }

            var layerName = allowNameAsLayer
                ? GetString(parameters, "layer", "Layer", "layer_name", "layerName", "name", "Name")
                : GetString(parameters, "layer", "Layer", "layer_name", "layerName");
            if (string.IsNullOrWhiteSpace(layerName))
                return controller.layers.Length > 0;

            index = Array.FindIndex(controller.layers, layer => string.Equals(layer.name, layerName, StringComparison.Ordinal));
            return index >= 0;
        }

        static void ValidateExistingAnimatorState(JObject parameters, AnimatorController controller, ValidationReport report, string stateName)
        {
            if (string.IsNullOrWhiteSpace(stateName))
            {
                report.Error("Animator state action requires 'state' or 'name'.");
                return;
            }

            if (TryResolveLayerIndex(parameters, controller, out var layerIndex) &&
                !AnimatorStateExists(controller.layers[layerIndex].stateMachine, stateName))
            {
                report.Error($"Animator state '{stateName}' does not exist in layer '{controller.layers[layerIndex].name}'.");
            }
        }

        static bool AnimatorStateExists(AnimatorStateMachine stateMachine, string stateName)
        {
            return FindAnimatorState(stateMachine, stateName) != null;
        }

        static AnimatorState FindAnimatorState(AnimatorStateMachine stateMachine, string stateName)
        {
            return FindAnimatorState(stateMachine, stateName, stateMachine?.name);
        }

        static AnimatorState FindAnimatorState(AnimatorStateMachine stateMachine, string stateName, string path)
        {
            if (stateMachine == null || string.IsNullOrWhiteSpace(stateName))
                return null;

            var normalizedName = stateName.Replace('\\', '/').Trim('/');
            foreach (var child in stateMachine.states)
            {
                if (child.state == null)
                    continue;

                var childPath = string.IsNullOrWhiteSpace(path)
                    ? child.state.name
                    : $"{path}/{child.state.name}";

                if (string.Equals(child.state.name, normalizedName, StringComparison.Ordinal) ||
                    string.Equals(childPath, normalizedName, StringComparison.Ordinal))
                {
                    return child.state;
                }
            }

            foreach (var childMachine in stateMachine.stateMachines)
            {
                var childPath = string.IsNullOrWhiteSpace(path)
                    ? childMachine.stateMachine?.name
                    : $"{path}/{childMachine.stateMachine?.name}";
                var found = FindAnimatorState(childMachine.stateMachine, normalizedName, childPath);
                if (found != null)
                    return found;
            }

            return null;
        }

        static void ValidateOptionalMotion(JObject parameters, ValidationReport report)
        {
            if (HasAnyToken(parameters, "motion_path", "motionPath", "clip_path", "clipPath"))
                ValidateRequiredMotion(parameters, report);
        }

        static void ValidateRequiredMotion(JObject parameters, ValidationReport report)
        {
            var motionPath = GetString(parameters, "motion_path", "motionPath", "clip_path", "clipPath");
            if (string.IsNullOrWhiteSpace(motionPath))
            {
                report.Error("Animator state motion action requires 'motion_path'.");
                return;
            }

            if (!TryNormalizeAssetPath(motionPath, out var normalizedPath, out var pathError))
            {
                report.Error(pathError);
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<Motion>(normalizedPath) == null)
                report.Error($"Motion asset was not found at '{normalizedPath}'.");
        }

        static void ValidateAnimatorBlendTreeCreate(JObject parameters, AnimatorController controller, ValidationReport report)
        {
            var stateName = GetString(parameters, "state", "State", "name", "Name");
            if (string.IsNullOrWhiteSpace(stateName))
            {
                report.Error("Create BlendTree requires 'state' or 'name'.");
                return;
            }

            if (TryResolveLayerIndex(parameters, controller, out var layerIndex))
            {
                var state = FindAnimatorState(controller.layers[layerIndex].stateMachine, stateName);
                var replaceExisting = GetBool(parameters, false, "replace_existing", "replaceExisting");
                if (state?.motion != null && state.motion is not BlendTree && !replaceExisting)
                    report.Error($"Animator state '{stateName}' already has a non-BlendTree motion. Set replace_existing=true to replace the state motion.");
            }

            ValidateAnimatorBlendTreeSettings(parameters, controller, report);
            ValidateAnimatorBlendChildren(parameters["children"] ?? parameters["Children"], controller, report, required: false);
        }

        static void ValidateExistingAnimatorBlendTree(JObject parameters, AnimatorController controller, ValidationReport report)
        {
            var stateName = GetString(parameters, "state", "State", "name", "Name");
            if (string.IsNullOrWhiteSpace(stateName))
            {
                report.Error("BlendTree action requires 'state' or 'name'.");
                return;
            }

            if (!TryResolveLayerIndex(parameters, controller, out var layerIndex))
                return;

            var state = FindAnimatorState(controller.layers[layerIndex].stateMachine, stateName);
            if (state == null)
            {
                report.Error($"Animator state '{stateName}' does not exist in layer '{controller.layers[layerIndex].name}'.");
                return;
            }

            if (state.motion is not BlendTree)
                report.Error($"Animator state '{stateName}' does not have a BlendTree motion.");
        }

        static void ValidateAnimatorBlendTreeSettings(JObject parameters, AnimatorController controller, ValidationReport report)
        {
            var blendType = GetString(parameters, "blend_type", "blendType");
            if (!string.IsNullOrWhiteSpace(blendType) && !IsKnownBlendTreeType(blendType))
                report.Error($"BlendTree type must be Simple1D, SimpleDirectional2D, FreeformDirectional2D, FreeformCartesian2D, or Direct. Received '{blendType}'.");

            ValidateAnimatorParameterReference(parameters, controller, report, "blend_parameter", "blendParameter", "parameter", "Parameter");
            ValidateAnimatorParameterReference(parameters, controller, report, "blend_parameter_y", "blendParameterY", "parameter_y", "parameterY");
        }

        static void ValidateAnimatorBlendChildren(JToken token, AnimatorController controller, ValidationReport report, bool required)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                if (required)
                    report.Error("BlendTree children array is required.");
                return;
            }

            if (token is not JArray array)
            {
                report.Error("BlendTree children must be an array.");
                return;
            }

            foreach (var item in array)
            {
                if (item is JObject child)
                    ValidateAnimatorBlendChild(child, controller, report);
                else
                    report.Error("Each BlendTree child must be an object.");
            }
        }

        static void ValidateAnimatorBlendChild(JObject parameters, AnimatorController controller, ValidationReport report)
        {
            var motionPath = GetString(parameters, "motion_path", "motionPath", "clip_path", "clipPath");
            if (!string.IsNullOrWhiteSpace(motionPath))
            {
                if (!TryNormalizeAssetPath(motionPath, out var normalizedPath, out var pathError))
                {
                    report.Error(pathError);
                }
                else if (AssetDatabase.LoadAssetAtPath<Motion>(normalizedPath) == null)
                {
                    report.Error($"BlendTree child motion asset was not found at '{normalizedPath}'.");
                }
            }
            else
            {
                report.Warning("BlendTree child has no motion_path; execution will create an empty placeholder child.");
            }

            var position = parameters["position"] ?? parameters["Position"];
            if (position != null && position.Type != JTokenType.Null)
            {
                if (position is not JArray array || array.Count != 2)
                    report.Error("BlendTree child position must be [x,y].");
            }

            ValidateAnimatorParameterReference(parameters, controller, report, "direct_blend_parameter", "directBlendParameter");
        }

        static void ValidateAnimatorParameterReference(JObject parameters, AnimatorController controller, ValidationReport report, params string[] names)
        {
            var parameter = GetString(parameters, names);
            if (string.IsNullOrWhiteSpace(parameter))
                return;

            if (!controller.parameters.Any(item => string.Equals(item.name, parameter, StringComparison.Ordinal)))
                report.Error($"Animator parameter '{parameter}' does not exist.");
        }

        static bool IsKnownBlendTreeType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return false;

            var normalized = type.Trim();
            if (string.Equals(normalized, "1d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "simple_1d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "simple1d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "directional_2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "simple_directional_2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "simpledirectional2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "freeform_directional_2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "freeformdirectional2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "freeform_cartesian_2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "freeformcartesian2d", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return Enum.TryParse<BlendTreeType>(normalized, true, out _);
        }

        static void ValidateAnimatorTransition(JObject parameters, AnimatorController controller, ValidationReport report, bool adding)
        {
            if (!TryResolveLayerIndex(parameters, controller, out var layerIndex))
                return;

            var anyState = GetBool(parameters, false, "any_state", "anyState");
            var entry = GetBool(parameters, false, "entry", "entry_transition", "entryTransition") ||
                        string.Equals(GetString(parameters, "action", "Action"), "add_entry_transition", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(GetString(parameters, "action", "Action"), "remove_entry_transition", StringComparison.OrdinalIgnoreCase);
            var toExit = GetBool(parameters, false, "to_exit", "toExit", "exit");
            var fromState = GetString(parameters, "from_state", "fromState", "state");
            var toState = GetString(parameters, "to_state", "toState", "destination_state", "destinationState");
            var destinationStateMachine = GetString(parameters, "destination_state_machine", "destinationStateMachine", "to_state_machine", "toStateMachine");

            if (entry)
            {
                if (toExit || !string.IsNullOrWhiteSpace(destinationStateMachine))
                    report.Error("Entry transitions must target a destination state.");

                if (string.IsNullOrWhiteSpace(toState))
                    report.Error("Entry transition requires 'to_state'.");
                else
                    ValidateExistingAnimatorState(parameters, controller, report, toState);
            }
            else if (anyState)
            {
                if (toExit)
                    report.Error("Any State transitions cannot target Exit.");
                if (!string.IsNullOrWhiteSpace(destinationStateMachine))
                    report.Error("Any State transitions must target a destination state in the selected state machine.");

                if (string.IsNullOrWhiteSpace(toState))
                    report.Error("Any State transition requires 'to_state'.");
                else
                    ValidateExistingAnimatorState(parameters, controller, report, toState);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(fromState))
                    report.Error("State transition requires 'from_state' or 'state'.");
                else
                    ValidateExistingAnimatorState(parameters, controller, report, fromState);

                if (!toExit)
                {
                    if (string.IsNullOrWhiteSpace(toState) && string.IsNullOrWhiteSpace(destinationStateMachine))
                        report.Error("State transition requires 'to_state' or 'destination_state_machine' unless 'to_exit' is true.");
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(toState))
                            ValidateExistingAnimatorState(parameters, controller, report, toState);
                    }
                }
            }

            if (adding)
                ValidateAnimatorTransitionConditions(parameters, controller, report);
        }

        static void ValidateAnimatorTransitionConditions(JObject parameters, AnimatorController controller, ValidationReport report)
        {
            var conditions = parameters["conditions"] ?? parameters["Conditions"];
            if (conditions == null || conditions.Type == JTokenType.Null)
                return;

            if (conditions is not JArray array)
            {
                report.Error("Animator transition conditions must be an array.");
                return;
            }

            var parameterNames = new HashSet<string>(controller.parameters.Select(parameter => parameter.name), StringComparer.Ordinal);
            foreach (var item in array)
            {
                if (item is not JObject condition)
                {
                    report.Error("Each Animator transition condition must be an object.");
                    continue;
                }

                var parameter = GetString(condition, "parameter", "Parameter");
                if (string.IsNullOrWhiteSpace(parameter))
                    report.Error("Animator transition condition requires 'parameter'.");
                else if (!parameterNames.Contains(parameter))
                    report.Error($"Animator transition condition uses missing parameter '{parameter}'.");

                var mode = GetString(condition, "mode", "Mode");
                if (string.IsNullOrWhiteSpace(mode) || !Enum.TryParse<AnimatorConditionMode>(mode, true, out _))
                    report.Error($"Animator transition condition mode must be a valid AnimatorConditionMode. Received '{mode}'.");
            }
        }

        static bool IsApplyAnimatorGraphAction(string action)
        {
            return string.Equals(action, "apply_graph", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(action, "applygraph", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(action, "create_or_update_graph", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(action, "createorupdategraph", StringComparison.OrdinalIgnoreCase);
        }

        static void ValidateAnimatorGraphSpec(JObject parameters, AnimatorController controller, ValidationReport report)
        {
            var graph = GetAnimatorGraphSpec(parameters);
            if (graph == null)
            {
                report.Error("apply_graph requires a graph object or top-level graph fields.");
                return;
            }

            if (!HasAnyToken(graph, "parameters", "Parameters") && !HasAnyToken(graph, "layers", "Layers"))
                report.Warning("Animator graph has no parameters or layers; execution will only create/update the controller asset.");

            var declaredParameters = new HashSet<string>(
                controller?.parameters?.Select(parameter => parameter.name) ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);

            var parameterSpecs = GetGraphArray(graph, report, "graph.parameters", required: false, "parameters", "Parameters");
            if (parameterSpecs != null)
            {
                foreach (var token in parameterSpecs)
                {
                    if (token is not JObject parameterSpec)
                    {
                        report.Error("Each graph parameter must be an object.");
                        continue;
                    }

                    var name = GetString(parameterSpec, "name", "Name", "parameter", "Parameter");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        report.Error("Graph parameter requires 'name' or 'parameter'.");
                        continue;
                    }

                    declaredParameters.Add(name);
                    var type = GetString(parameterSpec, "type", "Type", "parameter_type", "parameterType");
                    var exists = controller?.parameters?.Any(parameter => string.Equals(parameter.name, name, StringComparison.Ordinal)) ?? false;
                    if (string.IsNullOrWhiteSpace(type) && !exists)
                    {
                        report.Error($"Graph parameter '{name}' requires 'type' because it does not exist yet.");
                    }
                    else if (!string.IsNullOrWhiteSpace(type) && !IsKnownAnimatorParameterType(type))
                    {
                        report.Error($"Animator parameter '{name}' type must be Float, Int, Bool, or Trigger. Received '{type}'.");
                    }
                }
            }

            var layerSpecs = GetGraphArray(graph, report, "graph.layers", required: false, "layers", "Layers");
            if (layerSpecs == null)
                return;

            for (var layerSpecIndex = 0; layerSpecIndex < layerSpecs.Count; layerSpecIndex++)
            {
                if (layerSpecs[layerSpecIndex] is not JObject layerSpec)
                {
                    report.Error("Each graph layer must be an object.");
                    continue;
                }

                var layerName = GetGraphLayerName(layerSpec, layerSpecIndex);
                if (string.IsNullOrWhiteSpace(layerName))
                {
                    report.Error($"Graph layer at index {layerSpecIndex} requires 'name' or 'layer'.");
                    continue;
                }

                var existingLayerIndex = controller == null
                    ? -1
                    : Array.FindIndex(controller.layers, layer => string.Equals(layer.name, layerName, StringComparison.Ordinal));
                var knownStates = new HashSet<string>(StringComparer.Ordinal);
                if (existingLayerIndex >= 0)
                    AddExistingAnimatorStateNames(controller.layers[existingLayerIndex].stateMachine, knownStates);

                var stateSpecs = GetGraphArray(layerSpec, report, $"graph.layers[{layerSpecIndex}].states", required: false, "states", "States");
                if (stateSpecs != null)
                {
                    foreach (var stateToken in stateSpecs)
                    {
                        if (stateToken is not JObject stateSpec)
                        {
                            report.Error($"Each state in graph layer '{layerName}' must be an object.");
                            continue;
                        }

                        var stateName = GetString(stateSpec, "name", "Name", "state", "State");
                        if (string.IsNullOrWhiteSpace(stateName))
                        {
                            report.Error($"A state in graph layer '{layerName}' requires 'name' or 'state'.");
                            continue;
                        }

                        knownStates.Add(stateName);
                        ValidateGraphPosition(stateSpec["position"] ?? stateSpec["Position"], report, $"state '{stateName}' position", allowZ: true);
                        ValidateOptionalGraphMotion(stateSpec, report);

                        var blendTreeSpec = stateSpec["blend_tree"] as JObject ?? stateSpec["blendTree"] as JObject;
                        if (blendTreeSpec != null)
                            ValidateGraphBlendTree(blendTreeSpec, declaredParameters, report, $"state '{stateName}' BlendTree");
                    }
                }

                var defaultState = GetString(layerSpec, "default_state", "defaultState");
                if (!string.IsNullOrWhiteSpace(defaultState) && !knownStates.Contains(defaultState))
                    report.Warning($"Default state '{defaultState}' in graph layer '{layerName}' is not declared in the graph and was not found in the existing layer.");

                foreach (var transition in CollectAnimatorGraphTransitions(layerSpec, report, layerName))
                    ValidateAnimatorGraphTransition(transition, knownStates, declaredParameters, report, layerName);
            }
        }

        static JObject GetAnimatorGraphSpec(JObject parameters)
        {
            return parameters["graph"] as JObject ?? parameters["Graph"] as JObject ?? parameters;
        }

        static JArray GetGraphArray(JObject obj, ValidationReport report, string label, bool required, params string[] names)
        {
            foreach (var name in names)
            {
                var token = obj[name];
                if (token == null || token.Type == JTokenType.Null)
                    continue;

                if (token is JArray array)
                    return array;

                report.Error($"{label} must be an array.");
                return null;
            }

            if (required)
                report.Error($"{label} is required.");
            return null;
        }

        static string GetGraphLayerName(JObject layerSpec, int index)
        {
            var layerName = GetString(layerSpec, "name", "Name", "layer", "Layer", "layer_name", "layerName");
            if (!string.IsNullOrWhiteSpace(layerName))
                return layerName;

            return index == 0 ? "Base Layer" : null;
        }

        static void AddExistingAnimatorStateNames(AnimatorStateMachine stateMachine, HashSet<string> names)
        {
            if (stateMachine == null)
                return;

            foreach (var child in stateMachine.states)
            {
                if (child.state != null)
                    names.Add(child.state.name);
            }

            foreach (var childMachine in stateMachine.stateMachines)
                AddExistingAnimatorStateNames(childMachine.stateMachine, names);
        }

        static void ValidateGraphBlendTree(JObject blendTreeSpec, HashSet<string> declaredParameters, ValidationReport report, string owner)
        {
            var blendType = GetString(blendTreeSpec, "blend_type", "blendType");
            if (!string.IsNullOrWhiteSpace(blendType) && !IsKnownBlendTreeType(blendType))
                report.Error($"{owner} has unsupported blend_type '{blendType}'.");

            ValidateGraphParameterReference(blendTreeSpec, declaredParameters, report, $"{owner} blend_parameter", "blend_parameter", "blendParameter", "parameter", "Parameter");
            ValidateGraphParameterReference(blendTreeSpec, declaredParameters, report, $"{owner} blend_parameter_y", "blend_parameter_y", "blendParameterY", "parameter_y", "parameterY");

            var children = GetGraphArray(blendTreeSpec, report, $"{owner} children", required: false, "children", "Children");
            if (children == null)
                return;

            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is not JObject child)
                {
                    report.Error($"{owner} child {i} must be an object.");
                    continue;
                }

                ValidateOptionalGraphMotion(child, report);
                ValidateGraphPosition(child["position"] ?? child["Position"], report, $"{owner} child {i} position", allowZ: false);
                ValidateGraphParameterReference(child, declaredParameters, report, $"{owner} child {i} direct_blend_parameter", "direct_blend_parameter", "directBlendParameter");
            }
        }

        static void ValidateOptionalGraphMotion(JObject spec, ValidationReport report)
        {
            var motionPath = GetString(spec, "motion_path", "motionPath", "clip_path", "clipPath");
            if (string.IsNullOrWhiteSpace(motionPath))
                return;

            if (!TryNormalizeAssetPath(motionPath, out var normalizedPath, out var pathError))
            {
                report.Error(pathError);
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<Motion>(normalizedPath) == null)
                report.Error($"Motion asset was not found at '{normalizedPath}'.");
        }

        static void ValidateGraphPosition(JToken token, ValidationReport report, string label, bool allowZ)
        {
            if (token == null || token.Type == JTokenType.Null)
                return;

            if (token is not JArray array)
            {
                report.Error($"{label} must be an array.");
                return;
            }

            var min = 2;
            var max = allowZ ? 3 : 2;
            if (array.Count < min || array.Count > max)
                report.Error($"{label} must contain {min}{(allowZ ? " or 3" : "")} number values.");
        }

        static List<JObject> CollectAnimatorGraphTransitions(JObject layerSpec, ValidationReport report, string layerName)
        {
            var transitions = new List<JObject>();
            AddAnimatorGraphTransitionArray(transitions, layerSpec, report, $"graph layer '{layerName}' transitions", "transitions", "Transitions");

            var anyStateTransitions = GetGraphArray(layerSpec, report, $"graph layer '{layerName}' any_state_transitions", required: false, "any_state_transitions", "anyStateTransitions", "AnyStateTransitions");
            if (anyStateTransitions != null)
            {
                foreach (var token in anyStateTransitions)
                {
                    if (token is JObject transition)
                    {
                        var clone = (JObject)transition.DeepClone();
                        clone["any_state"] = true;
                        transitions.Add(clone);
                    }
                    else
                    {
                        report.Error($"Each Any State transition in graph layer '{layerName}' must be an object.");
                    }
                }
            }

            var states = GetGraphArray(layerSpec, report, $"graph layer '{layerName}' states", required: false, "states", "States");
            if (states != null)
            {
                foreach (var stateToken in states.OfType<JObject>())
                {
                    var stateName = GetString(stateToken, "name", "Name", "state", "State");
                    var stateTransitions = GetGraphArray(stateToken, report, $"state '{stateName}' transitions", required: false, "transitions", "Transitions");
                    if (stateTransitions == null)
                        continue;

                    foreach (var transitionToken in stateTransitions)
                    {
                        if (transitionToken is JObject transition)
                        {
                            var clone = (JObject)transition.DeepClone();
                            if (clone["from_state"] == null && clone["fromState"] == null)
                                clone["from_state"] = stateName;
                            transitions.Add(clone);
                        }
                        else
                        {
                            report.Error($"Each transition in state '{stateName}' must be an object.");
                        }
                    }
                }
            }

            return transitions;
        }

        static void AddAnimatorGraphTransitionArray(List<JObject> transitions, JObject owner, ValidationReport report, string label, params string[] names)
        {
            var array = GetGraphArray(owner, report, label, false, names);
            if (array == null)
                return;

            foreach (var token in array)
            {
                if (token is JObject transition)
                    transitions.Add((JObject)transition.DeepClone());
                else
                    report.Error($"Each transition in {label} must be an object.");
            }
        }

        static void ValidateAnimatorGraphTransition(JObject transition, HashSet<string> knownStates, HashSet<string> declaredParameters, ValidationReport report, string layerName)
        {
            var anyState = GetBool(transition, false, "any_state", "anyState");
            var toExit = GetBool(transition, false, "to_exit", "toExit", "exit");
            var fromState = GetString(transition, "from_state", "fromState", "state", "State");
            var toState = GetString(transition, "to_state", "toState", "destination_state", "destinationState");

            if (anyState)
            {
                if (toExit)
                    report.Error($"Graph layer '{layerName}' has an Any State transition targeting Exit, which Unity does not support.");

                if (string.IsNullOrWhiteSpace(toState))
                    report.Error($"Graph layer '{layerName}' Any State transition requires 'to_state'.");
                else if (!knownStates.Contains(toState))
                    report.Warning($"Graph layer '{layerName}' Any State transition targets '{toState}', which is not declared in the graph and was not found in the existing layer.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(fromState))
                    report.Error($"Graph layer '{layerName}' transition requires 'from_state' or a state-level transition.");
                else if (!knownStates.Contains(fromState))
                    report.Warning($"Graph layer '{layerName}' transition source '{fromState}' is not declared in the graph and was not found in the existing layer.");

                if (!toExit)
                {
                    if (string.IsNullOrWhiteSpace(toState))
                        report.Error($"Graph layer '{layerName}' transition requires 'to_state' unless 'to_exit' is true.");
                    else if (!knownStates.Contains(toState))
                        report.Warning($"Graph layer '{layerName}' transition target '{toState}' is not declared in the graph and was not found in the existing layer.");
                }
            }

            var conditions = GetGraphArray(transition, report, $"graph layer '{layerName}' transition conditions", required: false, "conditions", "Conditions");
            if (!GetBool(transition, false, "has_exit_time", "hasExitTime") &&
                (conditions == null || conditions.Count == 0))
            {
                report.Error($"Graph layer '{layerName}' transition '{(anyState ? "Any State" : fromState)} -> {(toExit ? "Exit" : toState)}' requires has_exit_time=true or at least one condition. Unity ignores transitions with neither.");
            }

            if (conditions == null)
                return;

            foreach (var token in conditions)
            {
                if (token is not JObject condition)
                {
                    report.Error($"Each transition condition in graph layer '{layerName}' must be an object.");
                    continue;
                }

                ValidateGraphParameterReference(condition, declaredParameters, report, $"graph layer '{layerName}' transition condition", "parameter", "Parameter");
                var mode = GetString(condition, "mode", "Mode");
                if (string.IsNullOrWhiteSpace(mode) || !IsKnownAnimatorConditionMode(mode))
                    report.Error($"Animator transition condition mode must be If, IfNot, Greater, Less, Equals, or NotEqual. Received '{mode}'.");
            }
        }

        static void ValidateGraphParameterReference(JObject spec, HashSet<string> declaredParameters, ValidationReport report, string label, params string[] names)
        {
            var parameter = GetString(spec, names);
            if (!string.IsNullOrWhiteSpace(parameter) && !declaredParameters.Contains(parameter))
                report.Error($"{label} references missing Animator parameter '{parameter}'.");
        }

        static bool IsKnownAnimatorConditionMode(string mode)
        {
            var normalized = mode?.Trim();
            if (string.Equals(normalized, "if_not", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ifnot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "not_equal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "notequal", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return Enum.TryParse<AnimatorConditionMode>(normalized, true, out _);
        }

        static void ValidateRequiredGameObjectTarget(JObject parameters, ValidationReport report, string fieldName)
        {
            var target = parameters["target"] ?? parameters["Target"] ?? parameters["game_object"] ?? parameters["gameObject"] ?? parameters["GameObject"];
            if (target == null || target.Type == JTokenType.Null)
            {
                report.Error($"Prefab action requires '{fieldName}'.");
                return;
            }

            var matches = FindGameObjects(target, GetString(parameters, "search_method", "searchMethod", "SearchMethod"));
            if (matches.Count == 0)
            {
                report.Error($"Target GameObject '{target}' was not found.");
            }
            else if (matches.Count > 1)
            {
                report.Warning($"Target '{target}' matches {matches.Count} GameObjects; execution may use the first match.");
            }
        }

        static string GetPrefabSourcePath(JObject parameters) =>
            GetString(parameters, "prefab_path", "prefabPath", "PrefabPath", "asset_path", "assetPath", "AssetPath");

        static void ValidateExistingAssetPath(string path, ValidationReport report, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                report.Error($"'{fieldName}' is required.");
                return;
            }

            if (!TryNormalizeAssetPath(path, out var normalizedPath, out var error))
            {
                report.Error(error);
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(normalizedPath) == null)
            {
                report.Error($"Asset not found at '{normalizedPath}'.");
            }
        }

        static void ValidatePrefabDestination(string path, ValidationReport report, bool required)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (required)
                    report.Error("Prefab destination path is required.");
                return;
            }

            if (!TryNormalizeAssetPath(path, out var normalizedPath, out var error))
            {
                report.Error(error);
                return;
            }

            if (!normalizedPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                report.Error($"Prefab destination must end with .prefab: '{normalizedPath}'.");
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(normalizedPath) != null)
            {
                report.Error($"Destination prefab already exists at '{normalizedPath}'.");
            }
        }

        static void ValidateDestinationPath(string destination, ValidationReport report)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                report.Error("Destination path is required.");
                return;
            }

            if (!TryNormalizeAssetPath(destination, out var normalizedDestination, out var error))
            {
                report.Error(error);
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(normalizedDestination) != null || AssetDatabase.IsValidFolder(normalizedDestination))
            {
                report.Error($"Destination already exists at '{normalizedDestination}'.");
            }
        }

        static bool TryNormalizeAssetPath(string path, out string normalizedPath, out string error, bool allowFolder = false)
        {
            normalizedPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Asset path is empty.";
                return false;
            }

            var candidate = path.Replace('\\', '/').Trim();
            if (candidate.StartsWith("project:/", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Asset path must be an Assets/... path, not a project URI: '{path}'.";
                return false;
            }

            if (!candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Asset path must stay under Assets/: '{path}'.";
                return false;
            }

            if (candidate.Contains("../", StringComparison.Ordinal) ||
                candidate.Contains("/..", StringComparison.Ordinal) ||
                Path.IsPathRooted(candidate))
            {
                error = $"Asset path must not contain traversal or absolute roots: '{path}'.";
                return false;
            }

            if (!allowFolder && string.Equals(candidate, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                error = "Asset file path cannot be the Assets root.";
                return false;
            }

            normalizedPath = candidate.TrimEnd('/');
            return true;
        }

        static bool TryNormalizeValidateScriptUri(string uri, out string scriptPath, out string error)
        {
            scriptPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(uri))
            {
                error = "ValidateScript Uri is empty.";
                return false;
            }

            var (name, directory) = ScriptRefreshHelpers.SplitUri(uri);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(directory))
            {
                error = $"ValidateScript Uri '{uri}' must include a script filename and directory.";
                return false;
            }

            var fileName = name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? name
                : name + ".cs";
            var candidate = $"{directory.TrimEnd('/')}/{fileName}".Replace('\\', '/');

            if (candidate.Contains("../", StringComparison.Ordinal) ||
                candidate.Contains("/..", StringComparison.Ordinal) ||
                candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                error = $"ValidateScript Uri '{uri}' resolved to an unsafe path.";
                return false;
            }

            scriptPath = candidate.TrimStart('/').TrimEnd('/');
            return true;
        }

        static string ToProjectAbsolutePath(string assetPath)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        static string BuildScenePath(string name, string path)
        {
            var candidate = NormalizeProjectPathCandidate(path);
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? candidate
                    : $"Assets/{candidate.TrimStart('/')}";
            }

            var folder = string.IsNullOrWhiteSpace(candidate)
                ? "Assets/Scenes"
                : candidate.Trim('/');
            if (!folder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                folder = $"Assets/{folder}";
            }

            return $"{folder}/{name}.unity";
        }

        static string NormalizeProjectPathCandidate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var text = value.Trim().Trim('"').Replace('\\', '/');
            if (text.StartsWith("project://", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring("project://".Length);
            }
            else if (text.StartsWith("project:/", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring("project:/".Length);
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                .Replace('\\', '/')
                .TrimEnd('/');
            if (Path.IsPathRooted(text))
            {
                var full = Path.GetFullPath(text).Replace('\\', '/');
                if (full.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    text = full.Substring(projectRoot.Length + 1);
                }
            }

            while (text.Contains("//"))
            {
                text = text.Replace("//", "/");
            }

            if (text.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/Packages/", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/ProjectSettings/", StringComparison.OrdinalIgnoreCase))
            {
                text = text.TrimStart('/');
            }

            return text.TrimEnd('/');
        }

        static List<GameObject> FindGameObjects(JToken targetToken, string searchMethod)
        {
            return SceneObjectLocator.FindObjects(
                targetToken,
                string.IsNullOrWhiteSpace(searchMethod) ? "by_id_or_name_or_path" : searchMethod,
                findAll: true,
                new JObject { ["search_inactive"] = true });
        }

        static List<GameObject> FindGameObjects(string target, string searchMethod)
        {
            return SceneObjectLocator.FindObjects(target, searchMethod, new SceneObjectLocator.Options
            {
                IncludeInactive = true,
                IncludePrefabStage = true
            });
        }

        static string GetString(JObject obj, params string[] names)
        {
            if (obj == null)
            {
                return null;
            }

            foreach (var name in names)
            {
                var token = obj[name];
                if (token != null && token.Type != JTokenType.Null)
                {
                    return token.ToString();
                }
            }

            return null;
        }

        static bool HasAnyToken(JObject obj, params string[] names)
        {
            if (obj == null)
            {
                return false;
            }

            foreach (var name in names)
            {
                var token = obj[name];
                if (token != null && token.Type != JTokenType.Null)
                {
                    return true;
                }
            }

            return false;
        }

        static bool GetBool(JObject obj, bool defaultValue, params string[] names)
        {
            foreach (var name in names)
            {
                var token = obj[name];
                if (token == null || token.Type == JTokenType.Null)
                {
                    continue;
                }

                if (token.Type == JTokenType.Boolean)
                {
                    return token.Value<bool>();
                }

                if (bool.TryParse(token.ToString(), out var parsed))
                {
                    return parsed;
                }
            }

            return defaultValue;
        }

        static JObject ToJObjectSafe(object value)
        {
            try
            {
                return value as JObject ?? JObject.FromObject(value ?? new { });
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = $"Failed to serialize tool result: {ex.Message}"
                };
            }
        }
    }
}
