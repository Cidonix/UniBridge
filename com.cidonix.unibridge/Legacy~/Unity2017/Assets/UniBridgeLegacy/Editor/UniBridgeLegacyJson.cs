using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Cidonix.UniBridge.Legacy
{
    // Small dependency-free JSON codec for Unity versions that predate UPM's
    // Newtonsoft.Json dependency. It intentionally supports only JSON values.
    internal static class UniBridgeLegacyJson
    {
        public static object Deserialize(string json)
        {
            if (String.IsNullOrEmpty(json))
                return null;

            return new Parser(json).ParseValue();
        }

        public static string Serialize(object value)
        {
            StringBuilder builder = new StringBuilder(256);
            WriteValue(builder, value);
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            string text = value as string;
            if (text != null)
            {
                WriteString(builder, text);
                return;
            }

            if (value is bool)
            {
                builder.Append((bool)value ? "true" : "false");
                return;
            }

            IDictionary dictionary = value as IDictionary;
            if (dictionary != null)
            {
                WriteObject(builder, dictionary);
                return;
            }

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                WriteArray(builder, enumerable);
                return;
            }

            if (value is Enum)
            {
                WriteString(builder, value.ToString());
                return;
            }

            if (value is float || value is double || value is decimal)
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong)
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            WriteString(builder, value.ToString());
        }

        private static void WriteObject(StringBuilder builder, IDictionary dictionary)
        {
            builder.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!first)
                    builder.Append(',');
                first = false;
                WriteString(builder, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                builder.Append(':');
                WriteValue(builder, entry.Value);
            }
            builder.Append('}');
        }

        private static void WriteArray(StringBuilder builder, IEnumerable values)
        {
            builder.Append('[');
            bool first = true;
            foreach (object value in values)
            {
                if (!first)
                    builder.Append(',');
                first = false;
                WriteValue(builder, value);
            }
            builder.Append(']');
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 32)
                            builder.Append("\\u").Append(((int)character).ToString("x4"));
                        else
                            builder.Append(character);
                        break;
                }
            }
            builder.Append('"');
        }

        private sealed class Parser
        {
            private readonly string source;
            private int index;

            public Parser(string source)
            {
                this.source = source;
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (index >= source.Length)
                    return null;

                char character = source[index];
                if (character == '{') return ParseObject();
                if (character == '[') return ParseArray();
                if (character == '"') return ParseString();
                if (character == '-' || Char.IsDigit(character)) return ParseNumber();
                if (Match("true")) return true;
                if (Match("false")) return false;
                if (Match("null")) return null;
                throw new FormatException("Invalid JSON token at position " + index + ".");
            }

            private Dictionary<string, object> ParseObject()
            {
                Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                index++;
                SkipWhitespace();
                if (TryConsume('}'))
                    return result;

                while (index < source.Length)
                {
                    SkipWhitespace();
                    if (index >= source.Length || source[index] != '"')
                        throw new FormatException("Expected a JSON object key at position " + index + ".");
                    string key = ParseString();
                    SkipWhitespace();
                    if (!TryConsume(':'))
                        throw new FormatException("Expected ':' after JSON object key at position " + index + ".");
                    result[key] = ParseValue();
                    SkipWhitespace();
                    if (TryConsume('}'))
                        return result;
                    if (!TryConsume(','))
                        throw new FormatException("Expected ',' or '}' at position " + index + ".");
                }

                throw new FormatException("Unterminated JSON object.");
            }

            private List<object> ParseArray()
            {
                List<object> result = new List<object>();
                index++;
                SkipWhitespace();
                if (TryConsume(']'))
                    return result;

                while (index < source.Length)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (TryConsume(']'))
                        return result;
                    if (!TryConsume(','))
                        throw new FormatException("Expected ',' or ']' at position " + index + ".");
                }

                throw new FormatException("Unterminated JSON array.");
            }

            private string ParseString()
            {
                StringBuilder builder = new StringBuilder();
                index++;
                while (index < source.Length)
                {
                    char character = source[index++];
                    if (character == '"')
                        return builder.ToString();
                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }

                    if (index >= source.Length)
                        throw new FormatException("Unterminated JSON escape sequence.");
                    char escape = source[index++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (index + 4 > source.Length)
                                throw new FormatException("Incomplete Unicode escape sequence.");
                            string hex = source.Substring(index, 4);
                            builder.Append((char)Int32.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            index += 4;
                            break;
                        default:
                            throw new FormatException("Invalid JSON escape sequence '\\" + escape + "'.");
                    }
                }

                throw new FormatException("Unterminated JSON string.");
            }

            private object ParseNumber()
            {
                int start = index;
                if (source[index] == '-') index++;
                while (index < source.Length && Char.IsDigit(source[index])) index++;
                bool floatingPoint = false;
                if (index < source.Length && source[index] == '.')
                {
                    floatingPoint = true;
                    index++;
                    while (index < source.Length && Char.IsDigit(source[index])) index++;
                }
                if (index < source.Length && (source[index] == 'e' || source[index] == 'E'))
                {
                    floatingPoint = true;
                    index++;
                    if (index < source.Length && (source[index] == '+' || source[index] == '-')) index++;
                    while (index < source.Length && Char.IsDigit(source[index])) index++;
                }

                string token = source.Substring(start, index - start);
                if (!floatingPoint)
                {
                    long integer;
                    if (Int64.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                        return integer;
                }

                double number;
                if (Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    return number;
                throw new FormatException("Invalid JSON number '" + token + "'.");
            }

            private bool Match(string token)
            {
                if (index + token.Length > source.Length)
                    return false;
                if (String.Compare(source, index, token, 0, token.Length, StringComparison.Ordinal) != 0)
                    return false;
                index += token.Length;
                return true;
            }

            private bool TryConsume(char character)
            {
                SkipWhitespace();
                if (index >= source.Length || source[index] != character)
                    return false;
                index++;
                return true;
            }

            private void SkipWhitespace()
            {
                while (index < source.Length && Char.IsWhiteSpace(source[index]))
                    index++;
            }
        }
    }
}
