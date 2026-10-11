using System;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MathRegression
{
    static partial class Regression
    {
        static JObject Definition(JObject document, JObject reference)
            => (JObject)document["$defs"][reference.Value<string>("$ref").Substring("#/$defs/".Length)];
        static bool SchemaRefuses(Type type)
        {
            try { ActionOutputSchema.DTO(type); return false; }
            catch (NotSupportedException) { return true; }
        }
        static void CheckOutputSchemas()
        {
            Check("output-schema-unrelated-math-converter-does-not-reject-plain-dto", () =>
            {
                if (McpJson.CreateSerializer().Converters.Count == 0) return false;
                var document = ActionOutputSchema.DTO(typeof(PlainSchemaDto));
                var shape = Definition(document, document);
                return shape.Value<string>("type") == "object" && !shape.Value<bool>("additionalProperties") &&
                    shape["properties"]["count"].Value<string>("type") == "integer" &&
                    ((JArray)shape["required"]).Values<string>().OrderBy(x => x).SequenceEqual(new[] { "count", "name" });
            });
            Check("output-schema-explicit-math-dto-and-nested-shapes-remain-closed", () =>
            {
                var document = ActionOutputSchema.DTO(typeof(ExplicitMathEnvelope));
                var root = Definition(document, document);
                var nestedReference = (JObject)root["properties"]["value"]["anyOf"][0];
                var nested = Definition(document, nestedReference);
                var fields = (JObject)nested["properties"];
                return !root.Value<bool>("additionalProperties") && !nested.Value<bool>("additionalProperties") &&
                    fields.Count == 3 && ((JArray)nested["required"]).Count == 3 &&
                    fields["x"]["anyOf"][0].Value<string>("type") == "number" &&
                    ((JArray)fields["x"]["anyOf"][1]["enum"]).Values<string>().SequenceEqual(new[] { "NaN", "Infinity", "-Infinity" }) &&
                    root["properties"]["rows"]["anyOf"][0]["items"]["anyOf"][0].Value<string>("$ref") == nestedReference.Value<string>("$ref");
            });
            Check("output-schema-unity-vector-applicable-converter-still-requires-producer", () => SchemaRefuses(typeof(Vector3)));
            Check("output-schema-nested-unity-matrix-applicable-converter-still-requires-producer", () => SchemaRefuses(typeof(ConvertedMathEnvelope)));
            Check("output-schema-custom-applicable-contract-converter-still-refused", () => SchemaRefuses(typeof(CustomConvertedDto)));
            Check("output-schema-nested-custom-applicable-converter-still-refused", () => SchemaRefuses(typeof(CustomConvertedEnvelope)));
            Check("output-schema-dynamic-object-still-refused", () => SchemaRefuses(typeof(DynamicEnvelope)));
            Check("output-schema-ignores-poison-global-serializer-factory", () =>
            {
                var original = JsonConvert.DefaultSettings;
                try
                {
                    JsonConvert.DefaultSettings = () => throw new Exception("Global factory must not be invoked by DTO schemas.");
                    return Definition(ActionOutputSchema.DTO(typeof(PlainSchemaDto)), ActionOutputSchema.DTO(typeof(PlainSchemaDto))).Value<string>("type") == "object";
                }
                finally { JsonConvert.DefaultSettings = original; }
            });
        }
        sealed class PlainSchemaDto { [JsonProperty("name")] public string Name; [JsonProperty("count")] public int Count; }
        sealed class ExplicitMathDto { [JsonProperty("x")] public float X; [JsonProperty("y")] public float Y; [JsonProperty("z")] public float Z; }
        sealed class ExplicitMathEnvelope { [JsonProperty("value")] public ExplicitMathDto Value; [JsonProperty("rows")] public ExplicitMathDto[] Rows; }
        sealed class ConvertedMathEnvelope { public Matrix4x4 Matrix; }
        sealed class CustomConvertedEnvelope { public CustomConvertedDto Value; }
        sealed class DynamicEnvelope { public object Value; }
        [JsonConverter(typeof(CustomSchemaConverter))]
        sealed class CustomConvertedDto { public int Value; }
        sealed class CustomSchemaConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type == typeof(CustomConvertedDto);
            public override object ReadJson(JsonReader reader, Type type, object value, JsonSerializer serializer) => throw new NotSupportedException();
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new NotSupportedException();
        }
    }
}
