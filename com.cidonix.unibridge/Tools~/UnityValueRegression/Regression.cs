using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Runtime.Serialization;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MathRegression
{
    static partial class Regression
    {
        static readonly List<object> Cases = new List<object>();
        static int failures;
        static void Check(string name, Func<bool> check)
        {
            bool passed = false; string error = null;
            try { passed = check(); } catch (Exception e) { error = e.ToString(); }
            Cases.Add(new { name, passed, error }); if (!passed) failures++;
            Console.WriteLine((passed ? "PASS " : "FAIL ") + name + (error == null ? "" : ": " + error));
        }
        static bool Rejected(Action action)
        {
            try { action(); return false; } catch (JsonSerializationException) { return true; }
        }
        static Matrix4x4 Matrix()
        {
            var value = new Matrix4x4();
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) value[r, c] = r * 4 + c + 1;
            return value;
        }
        static bool SameMatrix(Matrix4x4 left, Matrix4x4 right)
        {
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) if (left[r, c] != right[r, c]) return false;
            return true;
        }
        static JArray Flat()
        {
            var values = new JArray(); for (int i = 1; i <= 16; i++) values.Add(i); return values;
        }
        static JArray Rows()
        {
            var rows = new JArray();
            for (int r = 0; r < 4; r++)
            {
                var row = new JArray(); for (int c = 0; c < 4; c++) row.Add(r * 4 + c + 1); rows.Add(row);
            }
            return rows;
        }
        public static int Main(string[] args)
        {
            CheckOutputSchemas();
            var matrix = Matrix();
            Check("matrix-output-only-16-fields-row-order", () =>
            {
                var token = McpJson.TokenFromObject(matrix) as JObject;
                return token != null && token.Count == 16 && token.Value<float>("m01") == 2 && token.Value<float>("m10") == 5 && token.Value<float>("m33") == 16;
            });
            Check("matrix-flat-row-major", () => SameMatrix(matrix, Flat().ToObjectIndependent<Matrix4x4>()));
            Check("matrix-four-rows", () => SameMatrix(matrix, Rows().ToObjectIndependent<Matrix4x4>()));
            Check("matrix-snapshot-rows-input", () => SameMatrix(matrix, new JObject { ["rows"] = Rows() }.ToObjectIndependent<Matrix4x4>()));
            Check("matrix-object-roundtrip", () => SameMatrix(matrix, McpJson.TokenFromObject(matrix).ToObjectIndependent<Matrix4x4>()));
            Check("matrix-fifteen-rejected", () => { var value = Flat(); value.RemoveAt(15); return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-seventeen-rejected", () => { var value = Flat(); value.Add(17); return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-missing-field-rejected", () => { var value = (JObject)McpJson.TokenFromObject(matrix); value.Remove("m21"); return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-extra-field-rejected", () => { var value = (JObject)McpJson.TokenFromObject(matrix); value["extra"] = 1; return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-short-row-rejected", () => { var value = Rows(); ((JArray)value[2]).RemoveAt(3); return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-numeric-string-rejected", () => { var value = Flat(); value[3] = "4"; return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-bool-rejected", () => { var value = Flat(); value[3] = true; return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-null-rejected", () => { var value = Flat(); value[3] = JValue.CreateNull(); return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-nan-rejected", () => { var value = Flat(); value[3] = double.NaN; return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-infinity-rejected", () => { var value = Flat(); value[3] = double.PositiveInfinity; return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-float-overflow-rejected", () => { var value = Flat(); value[3] = 1e100; return Rejected(() => value.ToObjectIndependent<Matrix4x4>()); });
            Check("matrix-nonfinite-output-rejected", () => { var value = matrix; value.m23 = float.NaN; return Rejected(() => McpJson.SerializeObject(value)); });
            Check("vector2-array-and-canonical-output", () => { var value = JArray.Parse("[0.1,2]").ToObjectIndependent<Vector2>(); return ((JObject)McpJson.TokenFromObject(value)).Count == 2 && value.y == 2; });
            Check("vector3-array-and-canonical-output", () => { var value = JArray.Parse("[0.1,2,3]").ToObjectIndependent<Vector3>(); return ((JObject)McpJson.TokenFromObject(value)).Count == 3 && value.z == 3; });
            Check("vector4-array-and-canonical-output", () => { var value = JArray.Parse("[0.1,2,3,4]").ToObjectIndependent<Vector4>(); return ((JObject)McpJson.TokenFromObject(value)).Count == 4 && value.w == 4; });
            Check("quaternion-preserves-existing-shape", () => ((JObject)McpJson.TokenFromObject(JArray.Parse("[0,0,0,1]").ToObjectIndependent<Quaternion>())).Count == 4);
            Check("color-preserves-existing-shape", () => ((JObject)McpJson.TokenFromObject(JArray.Parse("[0.1,0.2,0.3,1]").ToObjectIndependent<Color>())).Count == 4);
            Check("rect-preserves-existing-shape", () => McpJson.TokenFromObject(JArray.Parse("[1,2,3,4]").ToObjectIndependent<Rect>()).Value<float>("width") == 3);
            Check("bounds-mixed-children", () =>
            {
                var value = JObject.Parse("{\"center\":[1,2,3],\"size\":{\"x\":4,\"y\":5,\"z\":6}}").ToObjectIndependent<Bounds>();
                var token = (JObject)McpJson.TokenFromObject(value);
                return token.Count == 2 && ((JObject)token["center"]).Count == 3 && value.size.z == 6;
            });
            Check("vector-extra-elements-rejected", () => Rejected(() => JArray.Parse("[1,2,3,4]").ToObjectIndependent<Vector3>()));
            Check("vector-missing-field-rejected", () => Rejected(() => JObject.Parse("{\"x\":1,\"y\":2}").ToObjectIndependent<Vector3>()));
            Check("vector-nonfinite-output-rejected", () => Rejected(() => McpJson.SerializeObject(new Vector3(1, float.PositiveInfinity, 3))));
            Check("bounds-nonfinite-child-rejected", () => Rejected(() => McpJson.SerializeObject(new Bounds(new Vector3(1, 2, 3), new Vector3(1, 2, float.NaN)))));
            Check("actual-reflection-setter-array-matrix-readback", () =>
            {
                var target = new Properties(); object actual;
                return ManageGameObject.Set(target, "Matrix", Flat()) && ManageGameObject.Read(target, "Matrix", out actual) && SameMatrix((Matrix4x4)actual, matrix) && ManageGameObject.Equal(Flat(), actual);
            });
            Check("actual-reflection-setter-invalid-matrix-unchanged", () =>
            {
                var target = new Properties { Matrix = matrix }; var value = Flat(); value.RemoveAt(15);
                return !ManageGameObject.Set(target, "Matrix", value) && SameMatrix(target.Matrix, matrix);
            });
            Check("actual-reflection-vector-array-readback", () =>
            {
                var target = new Properties(); object actual; var value = JArray.Parse("[0.1,2,3]");
                return ManageGameObject.Set(target, "Vector", value) && ManageGameObject.Read(target, "Vector", out actual) && ManageGameObject.Equal(value, actual);
            });
            Check("readback-different-matrix-is-false", () => { var other = matrix; other.m21++; return !ManageGameObject.Equal(Flat(), other); });
            Check("readback-float-rounding-tolerance", () => ManageGameObject.Equal(JArray.Parse("[0.1,0.2,0.3]"), new Vector3(.1f, .2f, .3f)));
            Check("readback-vector-mismatch-is-false", () => !ManageGameObject.Equal(JArray.Parse("[1,2,3]"), new Vector3(1, 2, 4)));
            Check("readback-bounds-array-children", () => ManageGameObject.Equal(JObject.Parse("{\"center\":[1,2,3],\"size\":[4,5,6]}"), new Bounds(new Vector3(1, 2, 3), new Vector3(4, 5, 6))));
            Check("reflection-report-canonical-safe", () =>
            {
                var response = new { status = "applied", actualValue = matrix, readbackVerified = true };
                var value = JObject.Parse(McpJson.SerializeObject(response));
                return value.Value<bool>("readbackVerified") && ((JObject)value["actualValue"]).Count == 16;
            });
            Check("unchanged-ordinary-json-contract", () => McpJson.SerializeObject(new { Count = 3, Label = "Зразок", Mode = 2 }) == "{\"Count\":3,\"Label\":\"Зразок\",\"Mode\":2}");
            Check("caller-matrix-converter-has-priority", () => McpJson.SerializeObject(matrix, new JsonSerializerSettings { Converters = new List<JsonConverter> { new CustomMatrix() } }) == "\"custom-matrix\"");
            Check("caller-formatting-retained", () => McpJson.SerializeObject(new { a = 1 }, new JsonSerializerSettings { Formatting = Formatting.Indented }).Contains("\n"));
            Check("global-poison-factory-never-called", () =>
            {
                var before = JsonConvert.DefaultSettings;
                try
                {
                    JsonConvert.DefaultSettings = () => { throw new Exception("Unexpected global JSON factory"); };
                    return SameMatrix(Flat().ToObjectIndependent<Matrix4x4>(), matrix) && ((JObject)McpJson.TokenFromObject(matrix)).Count == 16 && ManageGameObject.Equal(Flat(), matrix);
                }
                finally { JsonConvert.DefaultSettings = before; }
            });
            Check("parallel-serializers-no-shared-state", () =>
            {
                var tasks = new Task<bool>[8];
                for (int i = 0; i < tasks.Length; i++) tasks[i] = Task.Run(() => { for (int n = 0; n < 100; n++) if (!SameMatrix(Flat().ToObjectIndependent<Matrix4x4>(), matrix)) return false; return true; });
                Task.WaitAll(tasks); foreach (var task in tasks) if (!task.Result) return false; return true;
            });
            var report = new { passed = failures == 0, failures, cases = Cases, proof = "Exact draft converters/McpJson and unchanged candidate reflection method bodies; real Unity6.6 SDK math types outside Editor; object resolution, nested setters and Unity logging are adapters, not qualified here." };
            File.WriteAllText(args[0], JsonConvert.SerializeObject(report, Formatting.Indented));
            return failures == 0 ? 0 : 1;
        }
        sealed class CustomMatrix : JsonConverter
        {
            public override bool CanConvert(Type type) { return type == typeof(Matrix4x4); }
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) { writer.WriteValue("custom-matrix"); }
            public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer) { throw new NotSupportedException(); }
        }
        sealed class Properties { public Matrix4x4 Matrix { get; set; } public Vector3 Vector { get; set; } }
    }
}
