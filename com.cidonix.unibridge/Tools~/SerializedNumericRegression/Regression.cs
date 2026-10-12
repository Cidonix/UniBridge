using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace SerializedNumericRegression
{
    static class Regression
    {
        static readonly List<object> Cases = new List<object>();
        static int failures;
        static void Check(string name, Action body)
        {
            try { body(); Cases.Add(new { name, passed = true }); }
            catch (Exception error) { failures++; Cases.Add(new { name, passed = false, error = error.ToString() }); }
        }
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        static string Accessor(SerializedPropertyNumericType kind)
        {
            switch (kind)
            {
                case SerializedPropertyNumericType.Int8:
                case SerializedPropertyNumericType.Int16:
                case SerializedPropertyNumericType.Int32: return "intValue";
                case SerializedPropertyNumericType.UInt8:
                case SerializedPropertyNumericType.UInt16:
                case SerializedPropertyNumericType.UInt32: return "uintValue";
                case SerializedPropertyNumericType.Int64: return "longValue";
                case SerializedPropertyNumericType.UInt64: return "ulongValue";
                case SerializedPropertyNumericType.Float: return "floatValue";
                case SerializedPropertyNumericType.Double: return "doubleValue";
                default: return null;
            }
        }
        static void Roundtrip(string name, SerializedPropertyNumericType kind, JToken token, object expected)
        {
            Check(name, () => {
                var property = new SerializedProperty(kind);
                object value = SerializedNumericValue.ConvertValue(property, token);
                Assert(value.GetType() == expected.GetType() && value.Equals(expected), "Converted numeric type/value differs from the independent expectation.");
                SerializedNumericValue.Write(property, value);
                Assert(property.WriteAccessor == Accessor(kind) && property.WriteCount == 1, "Wrong native accessor or multiple setter calls.");
                object read = SerializedNumericValue.Read(property);
                Assert(read.GetType() == expected.GetType() && read.Equals(expected), "Readback type/value differs.");
                Assert(property.ReadAccessor == Accessor(kind), "Wrong native read accessor.");
                var json = McpJson.SerializeObject(read);
                var parsed = JToken.Parse(json);
                Assert(parsed.ToObject(expected.GetType(), JsonSerializer.Create()).Equals(expected), "Wire JSON did not roundtrip the full numeric value.");
            });
        }
        static void Reject(string name, SerializedPropertyNumericType kind, JToken token)
        {
            Check(name, () => {
                var property = new SerializedProperty(kind);
                bool rejected = false;
                try { SerializedNumericValue.Write(property, SerializedNumericValue.ConvertValue(property, token)); }
                catch (Exception error) when (error is OverflowException || error is ArgumentException || error is FormatException || error is NotSupportedException || error is JsonException) { rejected = true; }
                Assert(rejected && property.WriteCount == 0, "Invalid input must be rejected before a setter is reached.");
            });
        }
        static int Main(string[] args)
        {
            Roundtrip("sbyte_min", SerializedPropertyNumericType.Int8, new JValue(-128), (sbyte)-128);
            Roundtrip("sbyte_max", SerializedPropertyNumericType.Int8, new JValue(127), (sbyte)127);
            Roundtrip("byte_max", SerializedPropertyNumericType.UInt8, new JValue(255), (byte)255);
            Roundtrip("short_min", SerializedPropertyNumericType.Int16, new JValue(-32768), (short)-32768);
            Roundtrip("short_max", SerializedPropertyNumericType.Int16, new JValue(32767), (short)32767);
            Roundtrip("ushort_max", SerializedPropertyNumericType.UInt16, new JValue(65535), (ushort)65535);
            Roundtrip("int_min", SerializedPropertyNumericType.Int32, new JValue(int.MinValue), int.MinValue);
            Roundtrip("int_max", SerializedPropertyNumericType.Int32, new JValue(int.MaxValue), int.MaxValue);
            Roundtrip("uint_above_int", SerializedPropertyNumericType.UInt32, new JValue(2147483648L), 2147483648u);
            Roundtrip("uint_max", SerializedPropertyNumericType.UInt32, new JValue(uint.MaxValue), uint.MaxValue);
            Roundtrip("long_above_int", SerializedPropertyNumericType.Int64, new JValue(4294967296L), 4294967296L);
            Roundtrip("long_min", SerializedPropertyNumericType.Int64, JToken.Parse("-9223372036854775808"), long.MinValue);
            Roundtrip("long_max", SerializedPropertyNumericType.Int64, JToken.Parse("9223372036854775807"), long.MaxValue);
            Roundtrip("ulong_above_long", SerializedPropertyNumericType.UInt64, JToken.Parse("9223372036854775808"), 9223372036854775808UL);
            Roundtrip("ulong_max", SerializedPropertyNumericType.UInt64, JToken.Parse("18446744073709551615"), ulong.MaxValue);
            Roundtrip("long_decimal_string", SerializedPropertyNumericType.Int64, new JValue("9223372036854775807"), long.MaxValue);
            Roundtrip("ulong_decimal_string", SerializedPropertyNumericType.UInt64, new JValue("18446744073709551615"), ulong.MaxValue);
            Roundtrip("double_next_after_one", SerializedPropertyNumericType.Double, JToken.Parse("1.0000000000000002"), 1.0000000000000002d);
            Roundtrip("double_large_finite", SerializedPropertyNumericType.Double, JToken.Parse("1e200"), 1e200d);
            Roundtrip("double_negative", SerializedPropertyNumericType.Double, new JValue(-123456789.125d), -123456789.125d);
            Roundtrip("double_decimal_string", SerializedPropertyNumericType.Double, new JValue("1.0000000000000002"), 1.0000000000000002d);
            Roundtrip("float_regular", SerializedPropertyNumericType.Float, new JValue(0.125d), 0.125f);
            Roundtrip("int_existing_numeric_string", SerializedPropertyNumericType.Int32, new JValue("23"), 23);
            Roundtrip("float_existing_numeric_string", SerializedPropertyNumericType.Float, new JValue("0.125"), 0.125f);
            Check("ordinary_float_nonfinite_behavior_retained", () => {
                var property = new SerializedProperty(SerializedPropertyNumericType.Float);
                var value = SerializedNumericValue.ConvertValue(property, new JValue("NaN"));
                SerializedNumericValue.Write(property, value);
                Assert(float.IsNaN((float)SerializedNumericValue.Read(property)) && property.WriteCount == 1, "Existing Float behavior changed.");
            });
            Check("dry_run_conversion_does_not_write", () => {
                var property = new SerializedProperty(SerializedPropertyNumericType.UInt64);
                Assert((ulong)SerializedNumericValue.ConvertValue(property, new JValue(ulong.MaxValue)) == ulong.MaxValue && property.WriteCount == 0, "Convert stage mutated the property.");
            });
            Check("process_global_serializer_is_not_inherited", () => {
                JsonConvert.DefaultSettings = () => throw new Exception("Poison process global settings");
                try { Assert((long)SerializedNumericValue.ConvertValue(new SerializedProperty(SerializedPropertyNumericType.Int64), new JValue(4294967296L)) == 4294967296L, "Conversion changed."); }
                finally { JsonConvert.DefaultSettings = null; }
            });
            Reject("sbyte_overflow", SerializedPropertyNumericType.Int8, new JValue(128));
            Reject("byte_negative", SerializedPropertyNumericType.UInt8, new JValue(-1));
            Reject("byte_overflow", SerializedPropertyNumericType.UInt8, new JValue(256));
            Reject("short_overflow", SerializedPropertyNumericType.Int16, new JValue(32768));
            Reject("ushort_overflow", SerializedPropertyNumericType.UInt16, new JValue(65536));
            Reject("uint_negative", SerializedPropertyNumericType.UInt32, new JValue(-1));
            Reject("uint_overflow", SerializedPropertyNumericType.UInt32, new JValue(4294967296L));
            Reject("long_overflow", SerializedPropertyNumericType.Int64, JToken.Parse("9223372036854775808"));
            Reject("long_underflow", SerializedPropertyNumericType.Int64, JToken.Parse("-9223372036854775809"));
            Reject("ulong_negative", SerializedPropertyNumericType.UInt64, new JValue(-1));
            Reject("ulong_overflow", SerializedPropertyNumericType.UInt64, JToken.Parse("18446744073709551616"));
            Reject("ulong_fraction", SerializedPropertyNumericType.UInt64, new JValue(1.5));
            Reject("long_fraction", SerializedPropertyNumericType.Int64, new JValue(1.5));
            Reject("ulong_string_sign_overflow", SerializedPropertyNumericType.UInt64, new JValue("-1"));
            Reject("long_string_not_integer", SerializedPropertyNumericType.Int64, new JValue("1.5"));
            Reject("double_nan", SerializedPropertyNumericType.Double, new JValue("NaN"));
            Reject("double_infinity", SerializedPropertyNumericType.Double, new JValue("Infinity"));
            Reject("unknown_native_numeric_type", SerializedPropertyNumericType.Unknown, new JValue(1));
            var report = new { schema = "unibridge.serializedNumeric.source.v1", passed = failures == 0, total = Cases.Count, failures, nativeExecuted = false, cases = Cases };
            File.WriteAllText(args[0], JsonConvert.SerializeObject(report, Formatting.Indented));
            Console.WriteLine("Serialized numeric exact-source helper: " + (failures == 0 ? "PASS" : "FAIL") + " " + (Cases.Count - failures) + "/" + Cases.Count);
            return failures == 0 ? 0 : 1;
        }
    }
}

namespace UnityEditor
{
    // Named accessor adapter only: stores values and exposes which physical API
    // production selected. Native SerializedProperty behavior is qualified separately.
    public enum SerializedPropertyNumericType { Unknown, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double }
    public sealed class SerializedProperty
    {
        public readonly SerializedPropertyNumericType numericType;
        public string ReadAccessor, WriteAccessor;
        public int WriteCount;
        object stored;
        public SerializedProperty(SerializedPropertyNumericType kind) { numericType = kind; }
        T Read<T>(string name) { ReadAccessor = name; return (T)stored; }
        void Write<T>(string name, T value) { WriteAccessor = name; stored = value; WriteCount++; }
        public int intValue { get { return Read<int>("intValue"); } set { Write("intValue", value); } }
        public uint uintValue { get { return Read<uint>("uintValue"); } set { Write("uintValue", value); } }
        public long longValue { get { return Read<long>("longValue"); } set { Write("longValue", value); } }
        public ulong ulongValue { get { return Read<ulong>("ulongValue"); } set { Write("ulongValue", value); } }
        public float floatValue { get { return Read<float>("floatValue"); } set { Write("floatValue", value); } }
        public double doubleValue { get { return Read<double>("doubleValue"); } set { Write("doubleValue", value); } }
    }
}
