using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    /// <summary>
    /// Infers declared outbound value shapes with the same independent serializer
    /// as the Editor. Dynamic output and custom conversion need explicit providers.
    /// The result describes the object after ProjectContextGuard, not a raw DTO.
    /// </summary>
    public sealed class SerializerOutputSchema
    {
        public OutputContract Generate(Type declared)
        {
            // A builder owns one serializer and one definition graph. Reusing the
            // public generator cannot leak another call's references or limitations.
            try { return new Builder().Generate(OutputContractProvider.UnwrapAsync(declared)); }
            catch (Exception ex)
            {
                return new OutputContract { Fidelity = "unavailable", Reason = "Output contract metadata could not be resolved: " + ex.GetType().Name + "." };
            }
        }

        sealed class Builder
        {
            const int MaxDepth = 48;
            const int MaxDefinitions = 128;
            const int MaxMembers = 2048;
            readonly JsonSerializer serializer = McpJson.CreateSerializer();
            readonly Dictionary<Type, string> names = new Dictionary<Type, string>();
            readonly JObject definitions = new JObject();
            readonly HashSet<string> limitations = new HashSet<string>(StringComparer.Ordinal);
            int members;

            public OutputContract Generate(Type type)
            {
                if (type == null || type == typeof(void) || IsDynamic(type))
                    return Missing("Dynamic/JToken/void root requires an explicit payload provider.");
                var hazard = ConversionAttributeHazard(type);
                if (hazard != null) return Missing(hazard + " Root needs an explicit payload provider.");
                var contract = serializer.ContractResolver.ResolveContract(type);
                if (HasConverter(contract) || !Supported(contract) || contract.IsReference == true)
                    return Missing("Converted, reference-preserving or unsupported root needs an explicit payload provider.");
                if (contract is JsonObjectContract rootObject)
                    foreach (var property in rootObject.Properties)
                        if (!property.Ignored && property.Readable && property.PropertyName == "structuredContent")
                            return Missing("A readable root structuredContent member supplies an authored projection; an explicit payload provider is required.");
                if (contract is JsonDictionaryContract rootDictionary && MayWriteObject(rootDictionary.DictionaryValueType))
                    return Missing("Root dictionary values can supply an authored structuredContent object; an explicit payload provider is required.");

                var valueSchema = Value(type, false, 0);
                var branches = new JArray();
                if (contract is JsonObjectContract || contract is JsonDictionaryContract)
                {
                    if (contract is JsonDictionaryContract && names.TryGetValue(type, out var dictionaryName))
                    {
                        // The service field is exempt only at the outer dictionary;
                        // nested dictionaries retain their declared value contract.
                        valueSchema = (JObject)definitions[dictionaryName].DeepClone();
                        valueSchema["properties"] = new JObject { ["projectContext"] = new JObject() };
                    }
                    branches.Add(new JObject {
                        ["allOf"] = new JArray(valueSchema, new JObject { ["required"] = new JArray("projectContext") })
                    });
                }
                else
                {
                    branches.Add(new JObject {
                        ["type"] = "object",
                        ["properties"] = new JObject { ["value"] = valueSchema, ["projectContext"] = new JObject { ["type"] = "object" } },
                        ["required"] = new JArray("value", "projectContext")
                    });
                }
                if (CanBeNull(type)) branches.Add(ContextOnly());
                var result = new JObject {
                    ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object", ["anyOf"] = branches
                };
                if (definitions.Count > 0) result["$defs"] = definitions;
                return new OutputContract {
                    Schema = result,
                    Fidelity = limitations.Count == 0 ? "typed_serializer_contract" : "typed_partial",
                    Reason = limitations.Count == 0
                        ? "Declared serialized value shapes after project context; this is not action or execution qualification."
                        : string.Join("; ", limitations)
                };
            }

            JObject Value(Type declared, bool nullable, int depth)
            {
                var underlying = Nullable.GetUnderlyingType(declared);
                var type = underlying ?? declared;
                nullable |= underlying != null;
                if (depth > MaxDepth) return Partial("Output graph exceeds the depth budget.");
                if (IsDynamic(type)) return Partial("Dynamic member " + type.Name + " is unconstrained.");
                var hazard = ConversionAttributeHazard(type);
                if (hazard != null) return Partial(hazard);
                var contract = serializer.ContractResolver.ResolveContract(type);
                if (HasConverter(contract)) return Partial("Converted member " + type.Name + " requires an explicit provider.");
                if (contract.IsReference == true) return Partial("Reference-preserving member " + type.Name + " has a runtime wire shape.");
                if (contract is JsonContainerContract metadata &&
                    (metadata.ItemConverter != null || metadata.ItemIsReference == true ||
                     (metadata.ItemTypeNameHandling != null && metadata.ItemTypeNameHandling != TypeNameHandling.None)))
                    return Partial("Container item conversion/reference/type metadata in " + type.Name + " requires an explicit provider.");

                JObject schema;
                if (type == typeof(byte[])) schema = new JObject { ["type"] = "string" };
                else if (type == typeof(DBNull)) schema = new JObject { ["type"] = "null" };
                else if (type.IsEnum || IsInteger(type)) schema = new JObject { ["type"] = "integer" };
                else if (type == typeof(float) || type == typeof(double))
                    schema = new JObject { ["anyOf"] = new JArray(new JObject { ["type"] = "number" }, new JObject { ["enum"] = new JArray("NaN", "Infinity", "-Infinity") }) };
                else if (type == typeof(decimal)) schema = new JObject { ["type"] = "number" };
                else if (type == typeof(bool)) schema = new JObject { ["type"] = "boolean" };
                else if (contract is JsonStringContract || IsStringPrimitive(type)) schema = new JObject { ["type"] = "string" };
                else if (contract is JsonPrimitiveContract) return Partial("Unknown primitive encoding for " + type.FullName + " is unconstrained.");
                else if (contract is JsonObjectContract || contract is JsonArrayContract || contract is JsonDictionaryContract)
                {
                    if (!names.TryGetValue(type, out var name))
                    {
                        if (names.Count >= MaxDefinitions) return Partial("Output graph exceeds the definition budget.");
                        name = "T" + names.Count;
                        names[type] = name; // Allocate every container before walking its items.
                        var definition = new JObject();
                        definitions[name] = definition;
                        if (contract is JsonArrayContract array)
                        {
                            var itemType = array.CollectionItemType ?? typeof(object);
                            var item = Value(itemType, CanBeNull(itemType), depth + 1);
                            // Rectangular CLR arrays serialize one JSON array per rank.
                            var rank = type.IsArray ? type.GetArrayRank() : 1;
                            for (var dimension = 1; dimension < rank; dimension++)
                                item = new JObject { ["type"] = "array", ["items"] = item };
                            definition["type"] = "array";
                            definition["items"] = item;
                        }
                        else if (contract is JsonDictionaryContract dictionary)
                        {
                            var itemType = dictionary.DictionaryValueType ?? typeof(object);
                            definition["type"] = "object";
                            definition["additionalProperties"] = Value(itemType, CanBeNull(itemType), depth + 1);
                        }
                        else BuildObject(type, (JsonObjectContract)contract, definition, depth);
                    }
                    schema = new JObject { ["$ref"] = "#/$defs/" + name };
                }
                else return Partial("Unsupported serialized contract " + contract.GetType().Name + " is unconstrained.");
                return nullable ? new JObject { ["anyOf"] = new JArray(schema, new JObject { ["type"] = "null" }) } : schema;
            }

            void BuildObject(Type type, JsonObjectContract obj, JObject definition, int depth)
            {
                var properties = new JObject();
                var required = new JArray();
                definition["type"] = "object";
                definition["properties"] = properties;
                definition["additionalProperties"] = true;
                if (obj.ExtensionDataGetter != null) limitations.Add("Extension data in " + type.Name + " is unconstrained.");
                if (obj.OnSerializingCallbacks.Count > 0 || obj.OnSerializedCallbacks.Count > 0)
                    limitations.Add("Serialization callbacks in " + type.Name + " were not executed during inference.");
                foreach (var property in obj.Properties)
                {
                    if (property.Ignored || !property.Readable || property.PropertyType == null) continue;
                    if (++members > MaxMembers) { limitations.Add("Output graph exceeds the member budget."); break; }
                    JObject propertySchema;
                    if (property.Converter != null || property.ItemConverter != null || property.IsReference == true ||
                        property.ItemIsReference == true ||
                        (property.TypeNameHandling != null && property.TypeNameHandling != TypeNameHandling.None) ||
                        (property.ItemTypeNameHandling != null && property.ItemTypeNameHandling != TypeNameHandling.None))
                        propertySchema = Partial("Property conversion/reference/type metadata for " + property.PropertyName + " requires an explicit provider.");
                    else propertySchema = Value(property.PropertyType, CanBeNull(property.PropertyType), depth + 1);
                    var presence = property.IsRequiredSpecified ? property.Required : obj.ItemRequired ?? Required.Default;
                    if (propertySchema.Count > 0 && (presence == Required.Always || presence == Required.DisallowNull))
                        propertySchema = new JObject { ["allOf"] = new JArray(propertySchema, new JObject { ["not"] = new JObject { ["type"] = "null" } }) };
                    properties[property.PropertyName] = propertySchema;
                    var nulls = property.NullValueHandling ?? obj.ItemNullValueHandling ?? serializer.NullValueHandling;
                    var defaults = property.DefaultValueHandling ?? serializer.DefaultValueHandling;
                    var loop = property.ReferenceLoopHandling ?? obj.ItemReferenceLoopHandling ?? serializer.ReferenceLoopHandling;
                    if (property.ShouldSerialize == null && property.GetIsSpecified == null && loop != ReferenceLoopHandling.Ignore &&
                        nulls == NullValueHandling.Include && (defaults & DefaultValueHandling.Ignore) == 0)
                        required.Add(property.PropertyName);
                    if (property.ShouldSerialize != null || property.GetIsSpecified != null)
                        limitations.Add("Conditional member presence in " + type.Name + " was not executed during inference.");
                }
                if (required.Count > 0) definition["required"] = required;
                if (properties.Count == 0) limitations.Add("No declared serialized members in " + type.Name + "; open object is partial.");
            }

            // DefaultContractResolver constructs converter attributes while creating
            // member contracts. Read metadata first so inference cannot execute an
            // arbitrary converter constructor, CanConvert, getter or writer.
            static string ConversionAttributeHazard(Type type)
            {
                for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                {
                    if (HasConversionAttributes(current.GetCustomAttributesData()))
                        return "Conversion attribute on " + type.Name + " was not executed during inference.";
                    foreach (var member in current.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if ((member is PropertyInfo || member is FieldInfo) && HasConversionAttributes(member.GetCustomAttributesData()))
                            return "Member/item conversion attribute in " + type.Name + " was not executed during inference.";
                }
                return null;
            }

            static bool HasConversionAttributes(IList<CustomAttributeData> attributes)
            {
                foreach (var attribute in attributes)
                {
                    if (attribute.AttributeType == typeof(JsonConverterAttribute)) return true;
                    if (!typeof(JsonContainerAttribute).IsAssignableFrom(attribute.AttributeType) && attribute.AttributeType != typeof(JsonPropertyAttribute)) continue;
                    foreach (var argument in attribute.NamedArguments)
                        if (argument.MemberName == "ItemConverterType" && argument.TypedValue.Value != null) return true;
                }
                return false;
            }

            static bool HasConverter(JsonContract contract)
            {
                if (contract.Converter != null) return true;
                // Newtonsoft's built-in converters also change DTO/collection shapes.
                // This is library contract metadata, never a DTO value or converter call.
                var internalConverter = typeof(JsonContract).GetProperty("InternalConverter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return internalConverter != null && internalConverter.GetValue(contract, null) != null;
            }

            bool MayWriteObject(Type type)
            {
                if (type == null || IsDynamic(type) || ConversionAttributeHazard(type) != null) return true;
                var contract = serializer.ContractResolver.ResolveContract(type);
                return HasConverter(contract) || contract is JsonObjectContract || contract is JsonDictionaryContract || !Supported(contract) || contract.IsReference == true;
            }

            static bool Supported(JsonContract contract) => contract is JsonObjectContract || contract is JsonDictionaryContract || contract is JsonArrayContract || contract is JsonPrimitiveContract || contract is JsonStringContract;
            static bool IsDynamic(Type type) => type == typeof(object) || typeof(JToken).IsAssignableFrom(type);
            static bool CanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
            static bool IsInteger(Type type) => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) || type.FullName == "System.Numerics.BigInteger";
            static bool IsStringPrimitive(Type type) => type == typeof(string) || type == typeof(char) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Uri);
            static JObject ContextOnly() => new JObject {
                ["type"] = "object", ["properties"] = new JObject { ["projectContext"] = new JObject { ["type"] = "object" } },
                ["required"] = new JArray("projectContext"), ["additionalProperties"] = false
            };
            JObject Partial(string reason) { limitations.Add(reason); return new JObject(); }
            static OutputContract Missing(string reason) => new OutputContract { Fidelity = "unavailable", Reason = reason };
        }
    }
}
