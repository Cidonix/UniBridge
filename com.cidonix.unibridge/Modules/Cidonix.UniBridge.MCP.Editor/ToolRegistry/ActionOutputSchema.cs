using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    /// <summary>Serialized action shapes. Unknown CLR/JToken data is rejected, never inferred as an open schema.</summary>
    internal static class ActionOutputSchema
    {
        internal static JObject Type(string type) => new JObject { ["type"] = type };
        internal static JObject Nullable(JObject schema) => AnyOf(schema, Type("null"));
        internal static JObject Const(object value) => new JObject { ["const"] = value == null ? JValue.CreateNull() : JToken.FromObject(value) };
        internal static JObject Enum(params string[] values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
        internal static JObject Number() => AnyOf(Type("number"), new JObject { ["enum"] = new JArray("NaN", "Infinity", "-Infinity") });
        internal static JObject Arr(JObject item) => new JObject { ["type"] = "array", ["items"] = item ?? throw new ArgumentNullException(nameof(item)) };
        internal static JObject Map(JObject value) => new JObject { ["type"] = "object", ["additionalProperties"] = value ?? throw new ArgumentNullException(nameof(value)) };
        internal static JObject Obj(params (string Name, JObject Schema)[] fields) => OptionalObj(fields.Select(x => x.Name).ToArray(), fields);
        internal static JObject OptionalObj(string[] required, params (string Name, JObject Schema)[] fields)
        {
            var properties = new JObject();
            foreach (var field in fields)
            {
                if (properties.ContainsKey(field.Name)) throw new ArgumentException("Duplicate serialized property: " + field.Name);
                properties[field.Name] = field.Schema ?? throw new ArgumentNullException(field.Name);
            }
            var schema = new JObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
            if (required != null && required.Length != 0) schema["required"] = new JArray(required);
            return schema;
        }
        internal static JObject AnyOf(params JObject[] schemas)
        {
            if (schemas == null || schemas.Length == 0 || schemas.Any(x => x == null)) throw new ArgumentException("A union requires known schemas.");
            return schemas.Length == 1 ? (JObject)schemas[0].DeepClone() : new JObject { ["anyOf"] = new JArray(schemas.Select(x => x.DeepClone())) };
        }
        internal static JObject Success(JObject data = null, JObject meta = null)
        {
            var fields = new List<(string, JObject)> { ("success", Const(true)), ("message", Nullable(Type("string"))) };
            if (data != null) fields.Add(("data", data));
            if (meta != null) fields.Add(("_meta", meta));
            return Obj(fields.ToArray());
        }
        internal static JObject Error(JObject data = null)
        {
            var fields = new List<(string, JObject)> { ("success", Const(false)), ("code", Nullable(Type("string"))), ("error", Nullable(Type("string"))) };
            if (data != null) fields.Add(("data", data));
            return Obj(fields.ToArray());
        }

        // This is the complete JSON value domain for deliberately serialized user
        // property bags, e.g. a SerializedProperty's value. It is not a substitute
        // for the shape of a business response or an unresolved CLR object.
        internal static JObject JsonValue() => new JObject { ["$ref"] = "#/$defs/AuthoredJsonValue" };
        internal static JObject JsonValueDefinition() => new JObject { ["anyOf"] = new JArray(
            Type("null"), Type("boolean"), Type("number"), Type("string"),
            Arr(JsonValue()), Map(JsonValue())) };

        internal static JObject DTO(Type type) => new DtoBuilder().Build(type);

        // Hoist local definition tables when composing families. Definition names
        // are structural/type identities; a collision with different bytes fails.
        internal static JObject Compose(IEnumerable<JObject> alternatives)
        {
            var definitions = new JObject { ["AuthoredJsonValue"] = JsonValueDefinition() };
            var schemas = alternatives.Select(x => (JObject)x.DeepClone()).ToArray();
            foreach (var schema in schemas) Hoist(schema, definitions);
            var result = AnyOf(schemas);
            result["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            result["type"] = "object";
            result["$defs"] = definitions;
            return result;
        }

        static void Hoist(JToken node, JObject definitions)
        {
            if (node is JObject obj)
            {
                if (obj["$defs"] is JObject local)
                {
                    foreach (var property in local.Properties())
                    {
                        Hoist(property.Value, definitions);
                        if (definitions.TryGetValue(property.Name, out var prior) && !JToken.DeepEquals(prior, property.Value))
                            throw new InvalidOperationException("Output definition collision: " + property.Name);
                        definitions[property.Name] = property.Value.DeepClone();
                    }
                    obj.Remove("$defs");
                }
                foreach (var property in obj.Properties().ToArray()) Hoist(property.Value, definitions);
            }
            else if (node is JArray array) foreach (var item in array) Hoist(item, definitions);
        }

        // Schema representation only: intern equal, nontrivial schemas inside one
        // tool document. Action roots remain named and retain the same constraints.
        internal static JObject Compact(JObject schema)
        {
            var result = (JObject)schema.DeepClone();
            var definitions = result["$defs"] as JObject ?? new JObject();
            result["$defs"] = definitions;
            foreach (var definition in definitions.Properties().ToArray())
                definition.Value = Intern((JObject)definition.Value, definitions, false);
            return Intern(result, definitions, false);
        }

        static JObject Intern(JObject schema, JObject definitions, bool replace)
        {
            foreach (var keyword in new[] { "properties", "patternProperties", "dependentSchemas" })
                if (schema[keyword] is JObject entries)
                    foreach (var entry in entries.Properties().ToArray())
                        if (entry.Value is JObject child) entry.Value = Intern(child, definitions, true);
            foreach (var keyword in new[] { "additionalProperties", "items", "contains", "propertyNames", "not", "if", "then", "else" })
                if (schema[keyword] is JObject child) schema[keyword] = Intern(child, definitions, true);
            foreach (var keyword in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
                if (schema[keyword] is JArray entries)
                    for (var i = 0; i < entries.Count; i++)
                        if (entries[i] is JObject child) entries[i] = Intern(child, definitions, true);
            if (!replace || schema.ContainsKey("$ref") || schema.ContainsKey("$defs")) return schema;
            var bytes = Canonical(schema).ToString(Formatting.None);
            if (bytes.Length < 160) return schema;
            string name;
            using (var hash = SHA256.Create()) name = "Shape_" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(bytes))).Replace("-", "").ToLowerInvariant();
            if (definitions.TryGetValue(name, out var previous))
            {
                if (Canonical(previous).ToString(Formatting.None) != bytes) throw new InvalidOperationException("Structural output schema hash collision.");
            }
            else definitions[name] = schema.DeepClone();
            return new JObject { ["$ref"] = "#/$defs/" + name };
        }

        static JToken Canonical(JToken value)
        {
            if (value is JObject obj) return new JObject(obj.Properties().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new JProperty(x.Name, Canonical(x.Value))));
            if (value is JArray array) return new JArray(array.Select(Canonical));
            return value.DeepClone();
        }

        sealed class DtoBuilder
        {
            readonly JsonSerializer serializer = McpJson.CreateSerializer();
            readonly Dictionary<Type, string> names = new Dictionary<Type, string>();
            readonly JObject definitions = new JObject();
            internal JObject Build(Type type)
            {
                var schema = Value(type, false);
                if (definitions.Count != 0) schema["$defs"] = definitions;
                return schema;
            }
            JObject Value(Type declared, bool nullable)
            {
                if (declared == null || declared == typeof(object) || typeof(JToken).IsAssignableFrom(declared))
                    throw new NotSupportedException("Dynamic serialized type requires an explicit action producer schema: " + declared);
                var underlying = System.Nullable.GetUnderlyingType(declared);
                var type = underlying ?? declared;
                nullable |= underlying != null;
                var contract = serializer.ContractResolver.ResolveContract(type);
                if (contract.Converter != null || serializer.Converters.Count != 0 || contract.IsReference == true)
                    throw new NotSupportedException("Converted/reference-preserving serialized type needs a producer schema: " + type);
                JObject schema;
                if (type == typeof(bool)) schema = Type("boolean");
                else if (type.IsEnum || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)) schema = Type("integer");
                else if (type == typeof(float) || type == typeof(double)) schema = Number();
                else if (type == typeof(decimal)) schema = Type("number");
                else if (type == typeof(byte[]) || contract is JsonStringContract || type == typeof(string) || type == typeof(char) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri)) schema = Type("string");
                else if (contract is JsonArrayContract array)
                {
                    var itemType = array.CollectionItemType ?? throw new NotSupportedException("Unknown collection element type: " + type);
                    schema = Arr(Value(itemType, !itemType.IsValueType));
                    for (var rank = type.IsArray ? type.GetArrayRank() : 1; rank > 1; rank--) schema = Arr(schema);
                }
                else if (contract is JsonDictionaryContract dictionary)
                {
                    var value = dictionary.DictionaryValueType ?? throw new NotSupportedException("Unknown dictionary value type: " + type);
                    schema = Map(Value(value, !value.IsValueType));
                }
                else if (contract is JsonObjectContract objectContract)
                {
                    if (!names.TryGetValue(type, out var name))
                    {
                        using (var sha = SHA256.Create()) name = "DTO_" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(type.AssemblyQualifiedName))).Replace("-", "").Substring(0, 24);
                        names[type] = name;
                        definitions[name] = new JObject();
                        if (objectContract.ExtensionDataGetter != null || objectContract.OnSerializingCallbacks.Count != 0 || objectContract.OnSerializedCallbacks.Count != 0)
                            throw new NotSupportedException("Dynamic/callback serialized DTO requires explicit source rules: " + type);
                        var fields = new List<(string, JObject)>();
                        var required = new List<string>();
                        foreach (var property in objectContract.Properties.Where(x => !x.Ignored && x.Readable && x.PropertyType != null))
                        {
                            if (property.Converter != null || property.ItemConverter != null || property.ShouldSerialize != null || property.GetIsSpecified != null || property.IsReference == true || property.ItemIsReference == true)
                                throw new NotSupportedException("Conditional/converted serialized property needs producer rules: " + type + "." + property.PropertyName);
                            fields.Add((property.PropertyName, Value(property.PropertyType, !property.PropertyType.IsValueType)));
                            var nulls = property.NullValueHandling ?? objectContract.ItemNullValueHandling ?? serializer.NullValueHandling;
                            var defaults = property.DefaultValueHandling ?? serializer.DefaultValueHandling;
                            if (nulls == NullValueHandling.Include && (defaults & DefaultValueHandling.Ignore) == 0) required.Add(property.PropertyName);
                        }
                        if (fields.Count == 0) throw new NotSupportedException("No known serialized DTO members: " + type);
                        definitions[name] = OptionalObj(required.ToArray(), fields.ToArray());
                    }
                    schema = new JObject { ["$ref"] = "#/$defs/" + name };
                }
                else throw new NotSupportedException("Unsupported serialized type: " + type);
                return nullable ? Nullable(schema) : schema;
            }
        }
    }
}
