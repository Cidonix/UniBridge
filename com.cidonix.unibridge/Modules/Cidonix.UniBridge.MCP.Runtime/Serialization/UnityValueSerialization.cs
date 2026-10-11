using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Runtime.Serialization
{
    /// <summary>Owns finite, explicit JSON values without reflecting Unity's derived properties.</summary>
    static class UnityValueSerialization
    {
        static readonly JsonConverter MathConverter = new UnityMathValueConverter();

        public static List<JsonConverter> CreateConverters(bool includeObjectReferences)
        {
            var converters = new List<JsonConverter> { MathConverter };
            if (includeObjectReferences)
                converters.Add(new UnityEngineObjectConverter());
            return converters;
        }

        public static void AddMathConverter(IList<JsonConverter> converters)
        {
            // Caller converters keep priority. Converters here have no mutable per-call state.
            if (!converters.Contains(MathConverter))
                converters.Add(MathConverter);
        }

        public static bool TryCompareMathValues(object expected, object actual, out bool equal)
        {
            equal = false;
            if (actual == null || !MathConverter.CanConvert(actual.GetType()))
                return false;
            try
            {
                var serializer = JsonSerializer.Create();
                serializer.Converters.Add(MathConverter);
                var token = expected as JToken ?? (expected == null
                    ? JValue.CreateNull() : JToken.FromObject(expected, serializer));
                var converted = token.ToObject(actual.GetType(), serializer);
                equal = ValuesEqual(JToken.FromObject(converted, serializer), JToken.FromObject(actual, serializer));
            }
            catch (JsonException) { }
            catch (ArgumentException) { }
            catch (OverflowException) { }
            return true;
        }

        static bool ValuesEqual(JToken expected, JToken actual)
        {
            if (expected == null || actual == null)
                return expected == null && actual == null;
            if (IsNumber(expected) && IsNumber(actual))
            {
                double a = expected.Value<double>(), b = actual.Value<double>();
                return Math.Abs(a - b) <= 0.000001d * Math.Max(1d, Math.Max(Math.Abs(a), Math.Abs(b)));
            }
            var expectedObject = expected as JObject;
            var actualObject = actual as JObject;
            if (expectedObject != null && actualObject != null)
            {
                if (expectedObject.Count != actualObject.Count) return false;
                foreach (var property in expectedObject.Properties())
                {
                    JToken value;
                    if (!actualObject.TryGetValue(property.Name, out value) || !ValuesEqual(property.Value, value))
                        return false;
                }
                return true;
            }
            var expectedArray = expected as JArray;
            var actualArray = actual as JArray;
            if (expectedArray != null && actualArray != null)
            {
                if (expectedArray.Count != actualArray.Count) return false;
                for (int i = 0; i < expectedArray.Count; i++)
                    if (!ValuesEqual(expectedArray[i], actualArray[i])) return false;
                return true;
            }
            return JToken.DeepEquals(expected, actual);
        }

        static bool IsNumber(JToken value)
        {
            return value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
        }
    }

    /// <summary>
    /// Matrix arrays use row-major order. Matrix output uses m00..m33; explicit snapshot DTOs
    /// keep their own schema. Vector and color output retains the existing named-field format.
    /// </summary>
    sealed class UnityMathValueConverter : JsonConverter
    {
        public override bool CanConvert(Type type)
        {
            return type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) ||
                   type == typeof(Quaternion) || type == typeof(Color) || type == typeof(Rect) ||
                   type == typeof(Bounds) || type == typeof(Matrix4x4);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            string[] names;
            float[] numbers;
            if (value is Vector2)
            {
                var v = (Vector2)value; names = new[] { "x", "y" }; numbers = new[] { v.x, v.y };
            }
            else if (value is Vector3)
            {
                var v = (Vector3)value; names = new[] { "x", "y", "z" }; numbers = new[] { v.x, v.y, v.z };
            }
            else if (value is Vector4)
            {
                var v = (Vector4)value; names = new[] { "x", "y", "z", "w" }; numbers = new[] { v.x, v.y, v.z, v.w };
            }
            else if (value is Quaternion)
            {
                var v = (Quaternion)value; names = new[] { "x", "y", "z", "w" }; numbers = new[] { v.x, v.y, v.z, v.w };
            }
            else if (value is Color)
            {
                var v = (Color)value; names = new[] { "r", "g", "b", "a" }; numbers = new[] { v.r, v.g, v.b, v.a };
            }
            else if (value is Rect)
            {
                var v = (Rect)value; names = new[] { "x", "y", "width", "height" }; numbers = new[] { v.x, v.y, v.width, v.height };
            }
            else if (value is Bounds)
            {
                var v = (Bounds)value;
                // Validate both children before starting the output object.
                RequireFinite(new[] { v.center.x, v.center.y, v.center.z, v.size.x, v.size.y, v.size.z });
                writer.WriteStartObject();
                writer.WritePropertyName("center"); serializer.Serialize(writer, v.center);
                writer.WritePropertyName("size"); serializer.Serialize(writer, v.size);
                writer.WriteEndObject();
                return;
            }
            else if (value is Matrix4x4)
            {
                var v = (Matrix4x4)value;
                names = new string[16]; numbers = new float[16];
                for (int row = 0; row < 4; row++)
                    for (int column = 0; column < 4; column++)
                    {
                        int index = row * 4 + column;
                        names[index] = "m" + row + column;
                        numbers[index] = v[row, column];
                    }
            }
            else throw new JsonSerializationException("Unsupported Unity mathematical value.");

            RequireFinite(numbers);
            writer.WriteStartObject();
            for (int i = 0; i < names.Length; i++)
            {
                writer.WritePropertyName(names[i]); writer.WriteValue(numbers[i]);
            }
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type type, object existingValue, JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            if (type == typeof(Bounds))
            {
                var fields = RequireObject(token, new[] { "center", "size" });
                Vector3 center = fields["center"].ToObject<Vector3>(serializer);
                Vector3 size = fields["size"].ToObject<Vector3>(serializer);
                RequireFinite(new[] { center.x, center.y, center.z, size.x, size.y, size.z });
                return new Bounds(center, size);
            }
            if (type == typeof(Matrix4x4)) return ReadMatrix(token);
            string[] names = type == typeof(Vector2) ? new[] { "x", "y" } :
                type == typeof(Vector3) ? new[] { "x", "y", "z" } :
                type == typeof(Color) ? new[] { "r", "g", "b", "a" } :
                type == typeof(Rect) ? new[] { "x", "y", "width", "height" } : new[] { "x", "y", "z", "w" };
            float[] values = ReadNumbers(token, names);
            if (type == typeof(Vector2)) return new Vector2(values[0], values[1]);
            if (type == typeof(Vector3)) return new Vector3(values[0], values[1], values[2]);
            if (type == typeof(Vector4)) return new Vector4(values[0], values[1], values[2], values[3]);
            if (type == typeof(Quaternion)) return new Quaternion(values[0], values[1], values[2], values[3]);
            if (type == typeof(Color)) return new Color(values[0], values[1], values[2], values[3]);
            if (type == typeof(Rect)) return new Rect(values[0], values[1], values[2], values[3]);
            throw new JsonSerializationException("Unsupported Unity mathematical value.");
        }

        static Matrix4x4 ReadMatrix(JToken token)
        {
            var rowsObject = token as JObject;
            JToken rows;
            if (rowsObject != null && rowsObject.Count == 1 && rowsObject.TryGetValue("rows", out rows))
                token = rows;
            var array = token as JArray;
            var matrix = new Matrix4x4();
            if (array != null && array.Count == 4)
            {
                for (int row = 0; row < 4; row++)
                {
                    var cells = array[row] as JArray;
                    if (cells == null || cells.Count != 4)
                        throw new JsonSerializationException("Matrix4x4 rows must each contain exactly four finite numbers.");
                    for (int column = 0; column < 4; column++) matrix[row, column] = ReadNumber(cells[column]);
                }
                return matrix;
            }
            var names = new string[16];
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++) names[row * 4 + column] = "m" + row + column;
            float[] values = ReadNumbers(token, names);
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++) matrix[row, column] = values[row * 4 + column];
            return matrix;
        }

        static float[] ReadNumbers(JToken token, string[] names)
        {
            var values = new float[names.Length];
            var array = token as JArray;
            if (array != null)
            {
                if (array.Count != names.Length)
                    throw new JsonSerializationException("Unity value requires exactly " + names.Length + " finite numbers.");
                for (int i = 0; i < values.Length; i++) values[i] = ReadNumber(array[i]);
                return values;
            }
            var fields = RequireObject(token, names);
            for (int i = 0; i < values.Length; i++) values[i] = ReadNumber(fields[names[i]]);
            return values;
        }

        static JObject RequireObject(JToken token, string[] names)
        {
            var fields = token as JObject;
            if (fields == null || fields.Count != names.Length)
                throw new JsonSerializationException("Unity value requires a complete object containing " + String.Join(", ", names) + ".");
            for (int i = 0; i < names.Length; i++)
                if (fields[names[i]] == null)
                    throw new JsonSerializationException("Unity value is missing required field '" + names[i] + "'.");
            return fields;
        }

        static float ReadNumber(JToken value)
        {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float))
                throw new JsonSerializationException("Unity mathematical values require finite numeric JSON values.");
            float result;
            try { result = (float)value.Value<double>(); }
            catch (Exception error) when (error is OverflowException || error is FormatException)
            { throw new JsonSerializationException("Unity mathematical value is outside the finite float range.", error); }
            if (float.IsNaN(result) || float.IsInfinity(result))
                throw new JsonSerializationException("Unity mathematical value is outside the finite float range.");
            return result;
        }

        static void RequireFinite(float[] values)
        {
            for (int i = 0; i < values.Length; i++)
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                    throw new JsonSerializationException("Unity mathematical values must be finite.");
        }
    }
}
