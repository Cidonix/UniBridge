using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using static Cidonix.UniBridge.MCP.Editor.ToolRegistry.ActionOutputSchema;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    /// <summary>Discovery producers and expanded recipe plans, including the registered nested batch delivery.</summary>
    internal static class DiscoveryWorkflowActionOutputRules
    {
        static JObject S() => Type("string");
        static JObject B() => Type("boolean");
        static JObject I() => Type("integer");
        static JObject NS() => Nullable(S());
        static JObject SS() => Arr(S());

        internal static Dictionary<string, JObject> Build(string owner)
        {
            var result = new Dictionary<string, JObject>(StringComparer.Ordinal);
            if (owner == "UniBridge_Discover")
            {
                result["ping"] = Reply(Ping());
                result["aliases"] = Reply(Obj(("query", NS()), ("returned", I()),
                    ("aliases", Arr(Obj(("alias", S()), ("tool", S()))))));
                result["tools"] = Reply(Tools());
                result["workflows"] = Reply(Workflows());
                result["status"] = Reply(Obj(("package", Package()),
                    ("unity", Obj(("version", S()), ("projectPath", S()), ("isPlaying", B()))),
                    ("toolRegistry", Obj(("total", I()), ("enabled", I()), ("disabled", I()), ("discoverToolPresent", B()))),
                    ("tools", Nullable(Tools()))));
            }
            else if (owner == "UniBridge_WorkflowRecipes")
            {
                var definition = Definition();
                var missingInput = Error(Obj(("recipe", S()), ("errors", SS()), ("warnings", SS()), ("definition", definition)));
                result["List"] = Reply(Obj(("recipes", Arr(definition))));
                result["Describe"] = Reply(definition);
                result["BuildBatch"] = Compose(new[] { Success(Obj(("recipe", S()), ("warnings", SS()), ("batch", BatchPlan()))), missingInput, Error() });
                var executed = Obj(("recipe", S()), ("dryRun", B()), ("warnings", SS()),
                    ("batch", BatchPlan()), ("batchResult", RegisteredBatchResult()));
                var execution = Compose(new[] { Success(executed), Error(executed), missingInput, Error() });
                result["DryRun"] = (JObject)execution.DeepClone();
                result["Execute"] = execution;
            }
            else throw new ArgumentException("Unknown discovery/workflow owner: " + owner, nameof(owner));
            return result;
        }

        static JObject Reply(JObject data) => Compose(new[] { Success(data), Error() });
        static JObject Package() => Obj(("name", S()), ("displayName", S()), ("version", S()), ("relayConfigHint", S()));
        static JObject Workflows() => Arr(Obj(("key", S()), ("summary", S()), ("calls", SS())));
        static JObject Tools() => Obj(("query", NS()), ("returned", I()), ("tools", Arr(Obj(
            ("name", NS()), ("title", NS()), ("enabled", B()), ("enabledByDefault", B()),
            ("groups", Arr(NS())), ("batchAllowed", B()), ("aliases", SS())))));
        static JObject Ping() => Obj(("tool", S()), ("package", Package()),
            ("unity", Obj(("version", S()), ("projectPath", S()))), ("connected", Const(true)),
            ("discoverability", Obj(("primaryTool", S()), ("guideTool", S()), ("catalogTool", S()), ("note", S()))),
            ("coreWorkflows", Workflows()), ("tools", Nullable(Tools())));
        static JObject Definition() => Obj(("name", S()), ("aliases", SS()), ("when", S()),
            ("required", SS()), ("optional", SS()), ("outputs", SS()), ("underlyingTools", SS()));

        // BuildRecipeBatch writes this exact payload. Only the submitted parameter
        // bag carries arbitrary recipe options; the plan and step shells are closed.
        static JObject BatchPlan() => Obj(("DryRun", B()), ("ValidateBeforeExecute", Const(true)),
            ("StopOnError", Const(true)), ("UseUndoGroup", Const(true)), ("RollbackOnFailure", Const(true)),
            ("RollbackAssets", Const(true)), ("Name", S()), ("Steps", Arr(Obj(
                ("id", S()), ("tool", S()), ("description", S()), ("optional", B()), ("parameters", Map(JsonValue()))))));

        static JObject RegisteredBatchResult()
        {
            var business = SceneWorkflowActionOutputRules.Build("UniBridge_BatchActions")["$call"];
            var contextual = CoreActionOutputContracts.WithContext(business, true);
            var successfulDelivery = Mirror(contextual);
            var projectionFailure = OptionalObj(
                new[] { "status", "code", "error", "command", "executionEvidence", "projectionFailure" },
                ("status", Const("error")), ("code", Const("RESULT_PROJECTION_FAILED")), ("error", S()),
                ("command", Const("UniBridge_BatchActions")),
                ("executionEvidence", Obj(("handlerCompleted", Const(true)), ("retryOriginal", Const(false)),
                    ("operationOutcome", Enum("unavailable", "serialized_result_retained")))),
                ("projectionFailure", Obj(("exceptionType", S()))),
                ("projectContext", CoreActionOutputContracts.ContextSchema()),
                ("originalResponse", JsonValue()));
            var contractFailure = Obj(("status", Const("error")), ("code", Const("RESULT_CONTRACT_MISMATCH")),
                ("error", S()), ("command", Const("UniBridge_BatchActions")), ("action", NS()),
                ("executionEvidence", Obj(("handlerCompleted", Const(true)), ("retryOriginal", Const(false)),
                    ("operationOutcome", Const("serialized_result_retained")))),
                ("validationErrors", Arr(Obj(("path", S()), ("message", S())))),
                // A failed delivery retains already serialized evidence, even if
                // that evidence is precisely what violated the business contract.
                ("originalResponse", JsonValue()), ("projectContext", CoreActionOutputContracts.ContextSchema()));
            return Compose(new[] { successfulDelivery, Mirror(projectionFailure), Mirror(contractFailure) });
        }

        // The registry service mirror is one complete business reply, not another
        // response envelope and not an unconstrained substitute for the batch.
        static JObject Mirror(JObject business)
        {
            var schema = Compose(new[] { business });
            AddMirror(schema, (JObject)schema["$defs"]);
            return schema;
        }

        static void AddMirror(JObject schema, JObject definitions)
        {
            if (schema["$ref"]?.Value<string>() is string reference)
            {
                if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal) || definitions[reference.Substring(8)] is not JObject target)
                    throw new InvalidOperationException("Missing nested batch response definition: " + reference);
                schema.Remove("$ref");
                foreach (var field in ((JObject)target.DeepClone()).Properties()) schema[field.Name] = field.Value;
            }
            if (schema["properties"] is JObject properties && schema.Value<string>("type") == "object")
            {
                var mirrored = (JObject)schema.DeepClone();
                mirrored.Remove("$defs");
                mirrored.Remove("$schema");
                properties["structuredContent"] = mirrored;
                var required = schema["required"] as JArray ?? new JArray();
                required.Add("structuredContent");
                schema["required"] = required;
                return;
            }
            if (schema["anyOf"] is not JArray alternatives)
                throw new InvalidOperationException("A registered batch result needs explicit closed response roots.");
            foreach (var branch in alternatives.OfType<JObject>()) AddMirror(branch, definitions);
        }
    }
}
