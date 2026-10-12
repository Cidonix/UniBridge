using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    /// <summary>Action producers have individual serialized contracts; transport failures do not open their data shapes.</summary>
    internal static class CoreActionOutputContracts
    {
        internal static bool TryBuild(string owner, MethodInfo executeMethod, out OutputContract contract)
        {
            Dictionary<string, JObject> actions;
            string defaultAction;
            switch (owner)
            {
                case "UniBridge_ProjectGuidelines": actions = ProjectGuidelinesActionOutputRules.Build(); defaultAction = "Status"; break;
                case "UniBridge_ManagePackages": actions = PackageActionOutputRules.Build(); defaultAction = "List"; break;
                case "UniBridge_RuntimeProfiler": actions = RuntimeProfilerActionOutputRules.Build(); defaultAction = "Snapshot"; break;
                case "UniBridge_CaptureView": actions = CaptureViewActionOutputRules.Build(); defaultAction = "CaptureSceneView"; break;
                case "UniBridge_ManageEditor": actions = ManageEditorActionOutputRules.Build(); defaultAction = "GetState"; break;
                case "UniBridge_ReadConsole": actions = ReadConsoleActionOutputRules.Build(); defaultAction = "Get"; break;
                case "UniBridge_ManageMaterial":
                case "UniBridge_ManageAssetImporter":
                case "UniBridge_ManageScriptableObject": actions = AssetActionOutputRules.Build(owner); defaultAction = "Inspect"; break;
                case "UniBridge_ManageUnityEvent": actions = SceneActionOutputRules.Build(owner); defaultAction = "Inspect"; break;
                case "UniBridge_ManageRendering":
                case "UniBridge_ManageNavigation": actions = SceneActionOutputRules.Build(owner); defaultAction = "inspect"; break;
                case "UniBridge_EditorEvents": actions = EditorObservationActionOutputRules.Build(owner); defaultAction = "Snapshot"; break;
                case "UniBridge_WaitForEvent": actions = EditorObservationActionOutputRules.Build(owner); defaultAction = "AnyEvent"; break;
                case "UniBridge_ExecutionStatus": actions = EditorObservationActionOutputRules.Build(owner); defaultAction = "Snapshot"; break;
                case "UniBridge_BehaviourContext":
                case "UniBridge_ContextSnapshot": actions = EditorObservationActionOutputRules.Build(owner); defaultAction = "Default"; break;
                case "UniBridge_WorkSession": actions = RecoveryActionOutputRules.Build(owner); defaultAction = "status"; break;
                case "UniBridge_EditorSnapshot": actions = RecoveryActionOutputRules.Build(owner); defaultAction = "Capture"; break;
                case "UniBridge_Script": actions = ScriptActionOutputRules.Build(owner); defaultAction = "$default"; break;
                case "UniBridge_ApplyTextEdits":
                case "UniBridge_CreateScript":
                case "UniBridge_DeleteScript":
                case "UniBridge_GetSha":
                case "UniBridge_ScriptApplyEdits":
                case "UniBridge_ScriptCapabilities":
                case "UniBridge_ValidateScript":
                case "UniBridge_FindInFile":
                case "UniBridge_ListResources":
                case "UniBridge_ReadResource": actions = ScriptActionOutputRules.Build(owner); defaultAction = actions.Keys.Single(); break;
                case "UniBridge_ToolGuide": actions = OtherEditorActionOutputRules.Build(owner); defaultAction = "Overview"; break;
                case "UniBridge_VersionControl": actions = OtherEditorActionOutputRules.Build(owner); defaultAction = "GetStatus"; break;
                case "UniBridge_ValidateAdditiveSceneRegistration": actions = OtherEditorActionOutputRules.Build(owner); defaultAction = "Default"; break;
                case "UniBridge_DomainCatalog": actions = OtherEditorActionOutputRules.Build(owner); defaultAction = "Overview"; break;
                case "UniBridge_ManageAnimationClip":
                case "UniBridge_ManageInputActions":
                case "UniBridge_ManageVFX":
                case "UniBridge_ManageUIToolkit": actions = AuthoringActionOutputRules.Build(owner); defaultAction = "inspect"; break;
                case "UniBridge_ManageUI": actions = AuthoringActionOutputRules.Build(owner); defaultAction = "Inspect"; break;
                case "UniBridge_ManageAnimatorController": actions = AuthoringActionOutputRules.Build(owner); defaultAction = null; break;
                case "UniBridge_ManageScene": actions = SceneWorkflowActionOutputRules.Build(owner); defaultAction = "GetActive"; break;
                case "UniBridge_ManageSceneHierarchy": actions = SceneWorkflowActionOutputRules.Build(owner); defaultAction = "Reparent"; break;
                case "UniBridge_SceneHierarchyExport": actions = SceneWorkflowActionOutputRules.Build(owner); defaultAction = "Export"; break;
                case "UniBridge_SceneObjectView": actions = SceneWorkflowActionOutputRules.Build(owner); defaultAction = "Hierarchy"; break;
                case "UniBridge_VisualSceneAudit": actions = SceneWorkflowActionOutputRules.Build(owner); defaultAction = "AuditCapture"; break;
                case "UniBridge_BatchActions":
                case "UniBridge_ScopedEdit": actions = SceneWorkflowActionOutputRules.Build(owner); defaultAction = "$call"; break;
                case "UniBridge_ImportExternalModel": actions = GeneralAuthoringActionOutputRules.Build(owner); defaultAction = "$call"; break;
                case "UniBridge_ManageAsset": actions = GeneralAuthoringActionOutputRules.Build(owner); defaultAction = "GetInfo"; break;
                case "UniBridge_ManageAudio":
                case "UniBridge_ManageConstraints":
                case "UniBridge_ManagePhysics2D":
                case "UniBridge_ManagePhysics3D":
                case "UniBridge_ManageTilemap2D":
                case "UniBridge_ManageTimeline": actions = GeneralAuthoringActionOutputRules.Build(owner); defaultAction = "inspect"; break;
                case "UniBridge_ManageMenuItem": actions = GeneralAuthoringActionOutputRules.Build(owner); defaultAction = "Execute"; break;
                case "UniBridge_ManageShader": actions = GeneralAuthoringActionOutputRules.Build(owner); defaultAction = "Create"; break;
                case "UniBridge_UnitySearch": actions = GeneralAuthoringActionOutputRules.Build(owner); defaultAction = "Search"; break;
                case "UniBridge_AssetIntelligence": actions = IntelligenceActionOutputRules.Build(owner); defaultAction = "Search"; break;
                case "UniBridge_ScriptIntelligence": actions = IntelligenceActionOutputRules.Build(owner); defaultAction = "Catalog"; break;
                case "UniBridge_CaptureAsset": actions = InspectionActionOutputRules.Build(owner); defaultAction = "Capture"; break;
                case "UniBridge_CaptureUIToolkit": actions = InspectionActionOutputRules.Build(owner); defaultAction = "Capture"; break;
                case "UniBridge_RuntimeStateProbe": actions = InspectionActionOutputRules.Build(owner); defaultAction = "Snapshot"; break;
                case "UniBridge_TypeSchema": actions = InspectionActionOutputRules.Build(owner); defaultAction = "Inspect"; break;
                case "UniBridge_ManageGameObject":
                case "UniBridge_ManagePrefab": actions = InspectionActionOutputRules.Build(owner); defaultAction = null; break;
                case "UniBridge_Discover": actions = DiscoveryWorkflowActionOutputRules.Build(owner); defaultAction = "ping"; break;
                case "UniBridge_WorkflowRecipes": actions = DiscoveryWorkflowActionOutputRules.Build(owner); defaultAction = "List"; break;
                default: contract = null; return false;
            }
            if (actions.Count == 0 || defaultAction != null && !actions.ContainsKey(defaultAction)) throw new InvalidOperationException("Missing action/default output contract: " + owner);
            var definitions = new JObject();
            var actionReferences = new JObject();
            var alternatives = new List<JObject>();
            foreach (var action in actions.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var schema = WithContext(action.Value, true);
                var key = "Action_" + action.Key;
                definitions[key] = schema;
                var reference = new JObject { ["$ref"] = "#/$defs/" + key };
                actionReferences[action.Key] = reference.DeepClone();
                alternatives.Add(reference);
            }
            if (owner == "UniBridge_ProjectGuidelines")
            {
                definitions["UnknownGuidelinesAction"] = WithContext(ProjectGuidelinesActionOutputRules.UnknownAction(), true);
                alternatives.Add(new JObject { ["$ref"] = "#/$defs/UnknownGuidelinesAction" });
            }
            if (owner == "UniBridge_ManagePackages")
            {
                definitions["UnknownPackageAction"] = WithContext(PackageActionOutputRules.UnknownAction(), true);
                alternatives.Add(new JObject { ["$ref"] = "#/$defs/UnknownPackageAction" });
            }
            definitions["KnownCompletionProjectionFailure"] = ProjectionFailure(owner);
            alternatives.Add(new JObject { ["$ref"] = "#/$defs/KnownCompletionProjectionFailure" });
            definitions["KnownCompletionContractFailure"] = ContractFailure(owner);
            alternatives.Add(new JObject { ["$ref"] = "#/$defs/KnownCompletionContractFailure" });
            definitions["FrameworkInvocationFailure"] = ActionOutputSchema.Obj(
                ("status", ActionOutputSchema.Const("error")), ("error", ActionOutputSchema.Type("string")),
                ("command", ActionOutputSchema.Const(owner)), ("projectContext", ContextSchema()));
            alternatives.Add(new JObject { ["$ref"] = "#/$defs/FrameworkInvocationFailure" });
            var composed = ActionOutputSchema.Compose(alternatives);
            foreach (var definition in definitions.Properties()) composed["$defs"][definition.Name] = definition.Value;
            // Family DTO builders can carry definitions in nested branches. Hoist
            // all of them after action definitions are attached, preserving names.
            composed = ActionOutputSchema.Compose(new[] { composed });
            composed["x-unibridge-actions"] = actionReferences;
            composed["x-unibridge-default-action"] = defaultAction;
            var aliases = ActionSelectorRules.Aliases(owner, actions);
            composed["x-unibridge-action-aliases"] = new JObject(aliases.Select(x => new JProperty(x.Key, x.Value)));
            var selector = actions.Count == 1 && (defaultAction == "$call" || defaultAction == "Default") ? null : owner == "UniBridge_WaitForEvent" ? "WaitFor" : owner == "UniBridge_Script" ? "action" : "Action";
            composed["x-unibridge-action-selector"] = selector == null ? JValue.CreateNull() : new JValue(selector);
            var enumType = selector == null ? null : executeMethod?.GetParameters().FirstOrDefault()?.ParameterType.GetProperty(selector, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.PropertyType;
            // CaptureView deliberately parses this enum itself from JObject.
            if (owner == "UniBridge_CaptureView") enumType = typeof(Tools.Parameters.CaptureViewAction);
            var enumNames = new Dictionary<string, string>();
            if (enumType?.IsEnum == true)
                foreach (var name in Enum.GetNames(enumType))
                {
                    var canonical = actions.ContainsKey(name) ? name : aliases.TryGetValue(name, out var alias) ? alias : null;
                    if (canonical != null)
                        enumNames[Convert.ToInt64(Enum.Parse(enumType, name)).ToString(System.Globalization.CultureInfo.InvariantCulture)] = canonical;
                }
            var normalization = ActionSelectorRules.Normalization(owner, enumType?.IsEnum == true);
            composed["x-unibridge-action-normalization"] = normalization;
            var typedParameters = executeMethod?.GetParameters().FirstOrDefault()?.ParameterType != typeof(JObject);
            var parameterNames = selector == null ? Array.Empty<string>() : owner == "UniBridge_WaitForEvent" ? new[] { "WaitFor", "waitFor", "wait_for" } : owner == "UniBridge_Script" || owner == "UniBridge_ManagePrefab" ? new[] { "action" } : owner == "UniBridge_ManageAnimatorController" ? new[] { "action", "Action" } : owner == "UniBridge_ManageGameObject" ? new[] { "action", "Action", "operation", "Operation" } : typedParameters ? new[] { selector } : new[] { "Action", "action" };
            composed["x-unibridge-action-parameter-names"] = new JArray(parameterNames);
            var caseInsensitive = typedParameters || owner != "UniBridge_Script" && owner != "UniBridge_ManageAnimatorController" && owner != "UniBridge_VisualSceneAudit";
            composed["x-unibridge-action-parameter-case-insensitive"] = caseInsensitive;
            var lookupPolicy = typedParameters ? "last-non-null" : ActionSelectorRules.LookupPolicy(owner);
            composed["x-unibridge-action-lookup-policy"] = lookupPolicy;
            composed["x-unibridge-numeric-actions"] = new JObject(enumNames.Select(x => new JProperty(x.Key, x.Value)));
            composed = ActionOutputSchema.Compact(composed);
            contract = new OutputContract
            {
                Schema = composed,
                Fidelity = "source_action_contract",
                Reason = "Named serialized success/error/partial producer contracts for every declared action in " + owner + "; runtime action coverage is qualified separately.",
                ActionSchemas = actions.ToDictionary(x => x.Key, x => WithContext(x.Value, true), StringComparer.OrdinalIgnoreCase),
                DefaultAction = defaultAction,
                ActionSelector = selector,
                NormalizeAction = enumType?.IsEnum != true,
                ActionNormalization = normalization,
                ActionParameterNames = parameterNames,
                ActionParameterCaseInsensitive = caseInsensitive,
                ActionLookupPolicy = lookupPolicy,
                ActionEnumType = enumType?.IsEnum == true ? enumType : null,
                ActionAliases = aliases,
                NumericActions = enumNames,
                UnknownActionUsesDefault = owner == "UniBridge_RuntimeProfiler" || owner == "UniBridge_RuntimeStateProbe" || owner == "UniBridge_WorkSession" || owner == "UniBridge_ToolGuide" || owner == "UniBridge_Discover" || owner == "UniBridge_VisualSceneAudit" || owner == "UniBridge_WorkflowRecipes",
                // Workflow's unrecognised numeric enum follows its final Execute
                // branch; missing Action still uses the declared List default.
                UnknownActionDefault = owner == "UniBridge_WorkflowRecipes" ? "Execute" : null,
                UnknownActionSchema = WithContext(owner == "UniBridge_ProjectGuidelines"
                    ? ProjectGuidelinesActionOutputRules.UnknownAction() : owner == "UniBridge_ManagePackages"
                    ? PackageActionOutputRules.UnknownAction() : ActionOutputSchema.Error(), true)
            };
            return true;
        }

        internal static JObject ContextSchema() => ActionOutputSchema.Obj(
            ("id", ActionOutputSchema.Type("string")),
            ("name", ActionOutputSchema.Type("string")),
            ("root", ActionOutputSchema.Type("string")),
            ("assetsPath", ActionOutputSchema.Type("string")),
            ("settingsPath", ActionOutputSchema.Type("string")));

        internal static JObject WithContext(JObject business, bool required)
        {
            var schema = (JObject)business.DeepClone();
            var portable = ActionOutputSchema.Compose(new[] { schema });
            Attach(portable, required, (JObject)portable["$defs"]);
            return portable;
        }

        static void Attach(JObject schema, bool required, JObject definitions)
        {
            if (schema["$ref"]?.Value<string>() is string reference)
            {
                if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal) || definitions[reference.Substring(8)] is not JObject target)
                    throw new InvalidOperationException("Missing portable root action definition: " + reference);
                schema.Remove("$ref");
                foreach (var property in ((JObject)target.DeepClone()).Properties()) schema[property.Name] = property.Value;
            }
            if (schema["properties"] is JObject properties && schema.Value<string>("type") == "object")
            {
                if (properties["projectContext"] == null) properties["projectContext"] = ContextSchema();
                if (required)
                {
                    var fields = schema["required"] as JArray ?? new JArray();
                    if (!fields.Any(x => x.Value<string>() == "projectContext")) fields.Add("projectContext");
                    schema["required"] = fields;
                }
                return;
            }
            var found = false;
            foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
                if (schema[keyword] is JArray alternatives)
                    foreach (var branch in alternatives.OfType<JObject>()) { Attach(branch, required, definitions); found = true; }
            if (!found) throw new InvalidOperationException("A business action root needs explicit object properties.");
        }

        static JObject ProjectionFailure(string owner) => ActionOutputSchema.OptionalObj(
            new[] { "status", "code", "error", "command", "executionEvidence", "projectionFailure" },
            ("status", ActionOutputSchema.Const("error")),
            ("code", ActionOutputSchema.Const("RESULT_PROJECTION_FAILED")),
            ("error", ActionOutputSchema.Type("string")),
            ("command", ActionOutputSchema.Const(owner)),
            ("executionEvidence", ActionOutputSchema.Obj(
                ("handlerCompleted", ActionOutputSchema.Const(true)),
                ("retryOriginal", ActionOutputSchema.Const(false)),
                ("operationOutcome", ActionOutputSchema.Enum("unavailable", "serialized_result_retained")))),
            ("projectionFailure", ActionOutputSchema.Obj(("exceptionType", ActionOutputSchema.Type("string")))),
            ("projectContext", ContextSchema()),
            // Original serialized bytes are retained only when projection failed.
            // A JSON value is legitimate evidence here, never the action's data.
            ("originalResponse", ActionOutputSchema.JsonValue()));

        static JObject ContractFailure(string owner) => ActionOutputSchema.Obj(
            ("status", ActionOutputSchema.Const("error")), ("code", ActionOutputSchema.Const("RESULT_CONTRACT_MISMATCH")),
            ("error", ActionOutputSchema.Type("string")), ("command", ActionOutputSchema.Const(owner)),
            ("action", ActionOutputSchema.Nullable(ActionOutputSchema.Type("string"))),
            ("executionEvidence", ActionOutputSchema.Obj(("handlerCompleted", ActionOutputSchema.Const(true)),
                ("retryOriginal", ActionOutputSchema.Const(false)), ("operationOutcome", ActionOutputSchema.Const("serialized_result_retained")))),
            ("validationErrors", ActionOutputSchema.Arr(ActionOutputSchema.Obj(("path", ActionOutputSchema.Type("string")), ("message", ActionOutputSchema.Type("string"))))),
            ("originalResponse", ActionOutputSchema.JsonValue()), ("projectContext", ContextSchema()));
    }
}
