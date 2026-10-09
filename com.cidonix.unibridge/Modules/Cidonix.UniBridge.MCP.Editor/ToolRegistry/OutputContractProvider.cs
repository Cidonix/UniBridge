using System;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    public sealed class OutputContract
    {
        public JObject Schema;
        public string Fidelity;
        public string Reason;
        public bool HasSchema => Schema != null;
        public bool WholeActionDataQualified => false;
    }

    interface IOutputContractHandler
    {
        OutputContract OutputContract { get; }
    }

    public sealed class OutputContractProvider
    {
        public static Type UnwrapAsync(Type type)
        {
            if (type == null) return null;
            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
                return type.GetGenericArguments()[0];
            return type == typeof(Task) || type == typeof(ValueTask) ? typeof(void) : type;
        }

        public static OutputContract Resolve(string owner, MethodInfo executeMethod, MethodInfo explicitMethod = null,
            object instance = null, Func<object> instanceProvider = null, string registrationIssue = null)
        {
            if (!string.IsNullOrEmpty(registrationIssue)) return Missing(registrationIssue);
            if (explicitMethod != null && (!explicitMethod.IsStatic || explicitMethod.GetParameters().Length != 0))
                return Missing("Explicit output provider must be static and parameterless.");
            Func<object> provider = explicitMethod != null ? () => explicitMethod.Invoke(null, null) : instanceProvider;
            var core = executeMethod.DeclaringType?.Assembly == typeof(OutputContractProvider).Assembly &&
                executeMethod.DeclaringType?.Namespace == "Cidonix.UniBridge.MCP.Editor.Tools";
            if (provider != null)
            {
                try
                {
                    var value = provider();
                    if (value == null) return Missing("Explicit output provider returned null.");
                    var schema = value is JObject jo ? (JObject)jo.DeepClone() : McpJson.ObjectFromObject(value);
                    if (schema.Value<string>("type") != "object") return Missing("Structured output requires an object root schema.");
                    if (core)
                        return ReviewedCoreManual(owner, schema);
                    return new OutputContract { Schema = WithDeliveryFailure(schema), Fidelity = "explicit_unqualified", Reason = "Explicit author contract with delivery/error alternatives; action payload conformance must be independently qualified." };
                }
                catch (Exception ex) { return Missing("Explicit output provider failed: " + (ex is TargetInvocationException invocation ? invocation.InnerException ?? invocation : ex).GetType().Name); }
            }
            if (core)
            {
                // Core assembly ownership is not proof that every dynamic branch
                // returns Response. Keep descriptive envelope fields, without an
                // unqualified success/error requirement on opaque result producers.
                var envelope = CommonResponseSchema();
                envelope.Remove("anyOf");
                return new OutputContract { Schema = WithDeliveryFailure(envelope), Fidelity = "envelope_only",
                    Reason = "Envelope field descriptions only; opaque action payloads and their root fields remain unconstrained." };
            }
            var output = UnwrapAsync(executeMethod.ReturnType);
            if (output == null || output == typeof(object) || output == typeof(void) || typeof(JToken).IsAssignableFrom(output))
                return Missing("Dynamic/void output requires an explicit serialized payload contract.");
            var inferred = new SerializerOutputSchema().Generate(output);
            if (inferred.HasSchema) inferred.Schema = WithDeliveryFailure(inferred.Schema);
            return inferred;
        }

        static OutputContract ReviewedCoreManual(string owner, JObject declared)
        {
            // Preserve the explicit registration route, but amend only source-reviewed current core owners.
            // These data families intentionally leave helper-owned/dynamic nested payloads open.
            try
            {
                return new OutputContract { Schema = WithDeliveryFailure(CoreManualOutputRules.BuildBusinessResponseSchema(owner)),
                    Fidelity = "source_family_partial",
                    Reason = "Reviewed success/error and serialized data families for " + owner + "; detailed action producers are not wholly qualified." };
            }
            catch (ArgumentException)
            {
                return new OutputContract { Schema = WithDeliveryFailure(declared), Fidelity = "explicit_unqualified",
                    Reason = "Explicit core provider is outside the reviewed manual owner map." };
            }
        }

        static OutputContract Missing(string reason) => new OutputContract { Fidelity = "unavailable", Reason = reason };

        public static JObject CommonResponseSchema()
        {
            return JObject.Parse(@"{
                '$schema':'https://json-schema.org/draft/2020-12/schema','type':'object',
                'properties':{'success':{'type':'boolean'},'message':{'type':['string','null']},
                    'code':{'type':['string','null']},'error':{'type':['string','null']},
                    'projectContext':{'type':'object'},'_meta':{},'data':{},'status':{'type':'string'}},
                'anyOf':[
                    {'properties':{'success':{'const':true}},'required':['success','message']},
                    {'properties':{'success':{'const':false}},'required':['success','code','error']},
                    {'properties':{'status':{'enum':['error','failed']}},'required':['status','error']}
                ],'additionalProperties':true
            }");
        }

        public static JObject DeliveryFailureSchema() => JObject.Parse(@"{
            'type':'object','properties':{'status':{'const':'error'},'code':{'const':'RESULT_PROJECTION_FAILED'},
                'error':{'type':'string'},'executionEvidence':{'type':'object','properties':{
                    'handlerCompleted':{'const':true},'retryOriginal':{'const':false},
                    'operationOutcome':{'enum':['unavailable','serialized_result_retained']}},
                    'required':['handlerCompleted','retryOriginal','operationOutcome']},
                'projectContext':{'type':'object'},'originalResponse':{}},
            'required':['status','code','error','executionEvidence'],'additionalProperties':true
        }");

        public static JObject WithDeliveryFailure(JObject inferred)
        {
            // Moving an arbitrary resource/anchor/reference changes its meaning. Only the
            // locally rooted definitions produced here (or supplied in that form) are safe.
            ValidatePortableReferences(inferred);
            var payload = (JObject)inferred.DeepClone();
            var result = new JObject { ["$schema"] = payload["$schema"] ?? "https://json-schema.org/draft/2020-12/schema", ["type"] = "object" };
            payload.Remove("$schema");
            if (payload.TryGetValue("$defs", out var definitions))
            {
                result["$defs"] = definitions.DeepClone();
                payload.Remove("$defs");
            }
            // This reserved delivery error must always carry known-completion evidence,
            // including when an explicit author schema otherwise accepts every object.
            var reservedCode = JObject.Parse("{'required':['code'],'properties':{'code':{'const':'RESULT_PROJECTION_FAILED'}}}");
            result["anyOf"] = new JArray(
                new JObject { ["allOf"] = new JArray(payload, new JObject { ["not"] = reservedCode.DeepClone() }) },
                DeliveryFailureSchema(),
                new JObject { ["type"] = "object", ["anyOf"] = new JArray(
                    JObject.Parse("{'properties':{'status':{'enum':['error','failed']},'error':{'type':['string','null']}},'required':['status','error']}"),
                    JObject.Parse("{'properties':{'success':{'const':false},'code':{'type':['string','null']},'error':{'type':['string','null']}},'required':['success','code','error']}")),
                    ["not"] = reservedCode.DeepClone(), ["additionalProperties"] = true });
            return result;
        }

        static void ValidatePortableReferences(JToken schema)
        {
            if (schema is JObject obj)
                foreach (var property in obj.Properties())
                {
                    if (property.Name == "$id" || property.Name == "$anchor" || property.Name == "$dynamicAnchor" ||
                        property.Name == "$dynamicRef" || property.Name == "$recursiveRef" || property.Name == "$recursiveAnchor")
                        throw new NotSupportedException("Output schema resources/anchors need an explicitly qualified delivery union.");
                    if (property.Name == "$ref" && (property.Value.Type != JTokenType.String ||
                        !property.Value.Value<string>().StartsWith("#/$defs/", StringComparison.Ordinal)))
                        throw new NotSupportedException("Output schema references must use local #/$defs definitions.");
                    if (property.Name == "properties" || property.Name == "patternProperties" || property.Name == "$defs" || property.Name == "dependentSchemas")
                    {
                        if (property.Value is JObject definitions)
                            foreach (var child in definitions.Properties()) ValidatePortableReferences(child.Value);
                    }
                    else if (property.Name == "allOf" || property.Name == "anyOf" || property.Name == "oneOf" || property.Name == "prefixItems" ||
                        property.Name == "items" || property.Name == "additionalProperties" || property.Name == "unevaluatedProperties" ||
                        property.Name == "unevaluatedItems" || property.Name == "contains" || property.Name == "propertyNames" ||
                        property.Name == "not" || property.Name == "if" || property.Name == "then" || property.Name == "else")
                        ValidatePortableReferences(property.Value);
                }
            else if (schema is JArray array)
                foreach (var item in array) ValidatePortableReferences(item);
        }

        internal static object Annotation(IToolHandler handler)
        {
            var contract = (handler as IOutputContractHandler)?.OutputContract;
            return new { fidelity = contract?.Fidelity ?? "unavailable", reason = contract?.Reason,
                schemaAvailable = contract?.HasSchema == true, wholeActionDataQualified = false };
        }
    }
}
