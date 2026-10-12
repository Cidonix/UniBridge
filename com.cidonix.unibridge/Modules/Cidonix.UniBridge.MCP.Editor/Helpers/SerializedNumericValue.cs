using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // SerializedPropertyType groups all integer widths and both real widths.
    // Keep the declared numeric type and its native accessor throughout the route.
    internal static class SerializedNumericValue
    {
        internal static object Read(SerializedProperty property)
        {
            switch (property.numericType)
            {
                case SerializedPropertyNumericType.Int8: return checked((sbyte)property.intValue);
                case SerializedPropertyNumericType.UInt8: return checked((byte)property.uintValue);
                case SerializedPropertyNumericType.Int16: return checked((short)property.intValue);
                case SerializedPropertyNumericType.UInt16: return checked((ushort)property.uintValue);
                case SerializedPropertyNumericType.Int32: return property.intValue;
                case SerializedPropertyNumericType.UInt32: return property.uintValue;
                case SerializedPropertyNumericType.Int64: return property.longValue;
                case SerializedPropertyNumericType.UInt64: return property.ulongValue;
                case SerializedPropertyNumericType.Float: return property.floatValue;
                case SerializedPropertyNumericType.Double: return property.doubleValue;
                default: throw new NotSupportedException("Unsupported serialized numeric type.");
            }
        }

        internal static object ConvertValue(SerializedProperty property, JToken token)
        {
            switch (property.numericType)
            {
                // Preserve the established int/float input conversion for existing callers.
                case SerializedPropertyNumericType.Int32: return token.ToObjectIndependent<int>();
                case SerializedPropertyNumericType.Float: return token.ToObjectIndependent<float>();
                case SerializedPropertyNumericType.Double:
                    var real = token.ToObjectIndependent<double>();
                    if (double.IsNaN(real) || double.IsInfinity(real)) throw new ArgumentException("A finite Double value is required.");
                    return real;
                case SerializedPropertyNumericType.Int8: return checked((sbyte)Signed(token));
                case SerializedPropertyNumericType.Int16: return checked((short)Signed(token));
                case SerializedPropertyNumericType.Int64: return Signed(token);
                case SerializedPropertyNumericType.UInt8: return checked((byte)Unsigned(token));
                case SerializedPropertyNumericType.UInt16: return checked((ushort)Unsigned(token));
                case SerializedPropertyNumericType.UInt32: return checked((uint)Unsigned(token));
                case SerializedPropertyNumericType.UInt64: return Unsigned(token);
                default: throw new NotSupportedException("Unsupported serialized numeric type.");
            }
        }

        internal static void Write(SerializedProperty property, object value)
        {
            switch (property.numericType)
            {
                case SerializedPropertyNumericType.Int8: property.intValue = (sbyte)value; break;
                case SerializedPropertyNumericType.Int16: property.intValue = (short)value; break;
                case SerializedPropertyNumericType.Int32: property.intValue = (int)value; break;
                case SerializedPropertyNumericType.UInt8: property.uintValue = (byte)value; break;
                case SerializedPropertyNumericType.UInt16: property.uintValue = (ushort)value; break;
                case SerializedPropertyNumericType.UInt32: property.uintValue = (uint)value; break;
                case SerializedPropertyNumericType.Int64: property.longValue = (long)value; break;
                case SerializedPropertyNumericType.UInt64: property.ulongValue = (ulong)value; break;
                case SerializedPropertyNumericType.Float: property.floatValue = (float)value; break;
                case SerializedPropertyNumericType.Double: property.doubleValue = (double)value; break;
                default: throw new NotSupportedException("Unsupported serialized numeric type.");
            }
        }

        static void RequireInteger(JToken token)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.String))
                throw new ArgumentException("An integer JSON number or exact decimal integer string is required.");
        }

        static long Signed(JToken token)
        {
            RequireInteger(token);
            if (token.Type == JTokenType.String)
                return long.Parse(token.Value<string>(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return token.ToObjectIndependent<long>();
        }

        static ulong Unsigned(JToken token)
        {
            RequireInteger(token);
            if (token.Type == JTokenType.String)
                return ulong.Parse(token.Value<string>(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return token.ToObjectIndependent<ulong>();
        }
    }
}
