using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    /// <summary>Validates one serialized response against the requested action, without invoking a producer or getter again.</summary>
    internal static class ActionOutputValidation
    {
        internal static JObject Completed(JObject serialized, string owner, JObject parameters, OutputContract contract)
        {
            if (contract?.ActionSchemas == null) return serialized;
            var business = serialized["structuredContent"] as JObject ?? serialized;
            if (business.Value<string>("code") == "RESULT_PROJECTION_FAILED") return serialized;
            string action = null;
            List<JObject> errors;
            try
            {
                action = SelectAction(contract, parameters);
                var schema = action != null && contract.ActionSchemas.TryGetValue(action, out var selected)
                    ? selected : contract.UnknownActionSchema;
                var definitions = contract.Schema["$defs"] as JObject;
                errors = Check(business, schema, definitions);
            }
            catch (Exception exception)
            {
                // Validation runs after the handler and serialization have completed.
                // A validator failure must retain that outcome just like a mismatch.
                errors = new List<JObject>();
                Error(errors, "$", "Output contract validation could not complete: " + exception.GetType().Name + ".");
            }
            if (errors.Count == 0) return serialized;
            var failure = new JObject
            {
                ["status"] = "error", ["code"] = "RESULT_CONTRACT_MISMATCH",
                ["error"] = "The completed tool response did not conform to the requested action's serialized output contract.",
                ["command"] = owner, ["action"] = action == null ? JValue.CreateNull() : new JValue(action),
                ["executionEvidence"] = new JObject { ["handlerCompleted"] = true, ["retryOriginal"] = false, ["operationOutcome"] = "serialized_result_retained" },
                ["validationErrors"] = new JArray(errors),
                // Preserve the entire already serialized reply, including its
                // independent completion/mutation evidence and service mirror.
                ["originalResponse"] = serialized.DeepClone(),
                ["projectContext"] = business["projectContext"]?.DeepClone() ?? serialized["projectContext"]?.DeepClone() ?? ProjectContextGuard.BuildProjectContext()
            };
            return StructuredOutputProjection.Project(failure);
        }

        internal static string SelectAction(OutputContract contract, JObject parameters)
        {
            if (contract.ActionSelector == null) return contract.DefaultAction;
            JToken value = null;
            var parameterNames = contract.ActionParameterNames ?? new[] { contract.ActionSelector };
            if (contract.ActionLookupPolicy == "last-non-null")
            {
                // Json.NET assigns matching typed properties in document order;
                // NullValueHandling.Ignore preserves the previous/default value.
                foreach (var property in parameters?.Properties() ?? Enumerable.Empty<JProperty>())
                    if (property.Value.Type != JTokenType.Null && parameterNames.Any(key => string.Equals(key, property.Name, contract.ActionParameterCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                        value = property.Value;
            }
            else if (contract.ActionLookupPolicy == "gameobject-copy-alias" && parameters?["action"] != null)
                value = parameters["action"];
            else foreach (var key in parameterNames)
            {
                value = parameters?.GetValue(key, contract.ActionParameterCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (value != null && (value.Type != JTokenType.Null || contract.ActionLookupPolicy == "first-present")) break;
            }
            if (value == null || value.Type == JTokenType.Null) return contract.DefaultAction;
            if (value.Type == JTokenType.Integer)
            {
                var key = value.ToString(Formatting.None);
                if (contract.NumericActions != null && contract.NumericActions.TryGetValue(key, out var numeric)) return numeric;
                return contract.UnknownActionUsesDefault ? contract.UnknownActionDefault ?? contract.DefaultAction : null;
            }
            if (value.Type != JTokenType.String) return contract.UnknownActionUsesDefault ? contract.UnknownActionDefault ?? contract.DefaultAction : null;
            var requested = value.Value<string>();
            if (contract.ActionEnumType != null)
            {
                try
                {
                    var parsed = Enum.Parse(contract.ActionEnumType, requested, true);
                    var numericKey = Convert.ToInt64(parsed, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    if (contract.NumericActions.TryGetValue(numericKey, out var enumAction)) return enumAction;
                }
                catch (ArgumentException) { }
                catch (OverflowException) { }
                return contract.UnknownActionUsesDefault ? contract.UnknownActionDefault ?? contract.DefaultAction : null;
            }
            requested = Normalize(requested, contract.ActionNormalization);
            if (contract.NumericActions != null && contract.ActionNormalization == "enum-name-case-insensitive" && contract.NumericActions.TryGetValue(requested, out var numericString)) return numericString;
            if (contract.ActionAliases != null && contract.ActionAliases.TryGetValue(requested, out var alias)) return alias;
            foreach (var action in contract.ActionSchemas.Keys)
                if (string.Equals(Normalize(action, contract.ActionNormalization), requested, StringComparison.OrdinalIgnoreCase)) return action;
            return contract.UnknownActionUsesDefault ? contract.UnknownActionDefault ?? contract.DefaultAction : null;
        }

        internal static string Normalize(string value, string mode)
        {
            value ??= "";
            if (mode == "lower-only") return value.ToLowerInvariant();
            if (mode == "lower-trim") return value.Trim().ToLowerInvariant();
            if (mode == "guide-underscore") return value.Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
            if (mode == "enum-name-case-insensitive") return value.Trim();
            if (mode == "separators-trim") value = value.Trim();
            return value.Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();
        }

        internal static List<JObject> Check(JToken value, JObject schema, JObject definitions)
        {
            var errors = new List<JObject>();
            // JObject.FromObject retains CLR intrinsic JValue kinds (Date,
            // Bytes, Guid, Uri, TimeSpan and nonfinite Float). Their wire JSON
            // representation is a string. Validate that already serialized tree,
            // without reading the original CLR producer or its getters again.
            // Keep the original reply intact for delivery and mismatch evidence.
            var wireValue = WireValue(value);
            Validate(wireValue, schema, definitions, "$", errors);
            return errors;
        }

        static JToken WireValue(JToken value)
        {
            if (value is JObject obj)
                return new JObject(obj.Properties().Select(property => new JProperty(property.Name, WireValue(property.Value))));
            if (value is JArray array) return new JArray(array.Select(WireValue));
            if (value == null) return JValue.CreateNull();
            // These kinds already have their exact JSON wire representation.
            // Re-reading them would impose Json.NET's large-integer parse limit
            // and round decimal values through its default Double parser.
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.String || value.Type == JTokenType.Boolean || value.Type == JTokenType.Null)
                return value.DeepClone();
            if (value.Type == JTokenType.Float)
            {
                var number = ((JValue)value).Value;
                var nonfinite = number is double d && (double.IsNaN(d) || double.IsInfinity(d))
                    || number is float f && (float.IsNaN(f) || float.IsInfinity(f));
                if (!nonfinite) return value.DeepClone();
            }
            // Canonicalize only intrinsic/string/nonfinite or raw wire leaves;
            // the original already-serialized result and CLR getter stay intact.
            using var reader = new JsonTextReader(new StringReader(value.ToString(Formatting.None)))
            {
                DateParseHandling = DateParseHandling.None,
                MaxDepth = null
            };
            return JToken.ReadFrom(reader);
        }

        static void Error(List<JObject> errors, string path, string message) => errors.Add(new JObject { ["path"] = path, ["message"] = message });
        static bool IsType(JToken value, string type)
        {
            var kind = value?.Type ?? JTokenType.Null;
            return type switch
            {
                "null" => kind == JTokenType.Null,
                "object" => kind == JTokenType.Object,
                "array" => kind == JTokenType.Array,
                "string" => kind == JTokenType.String,
                "boolean" => kind == JTokenType.Boolean,
                "number" => kind == JTokenType.Float || kind == JTokenType.Integer,
                "integer" => kind == JTokenType.Integer || kind == JTokenType.Float && IsWholeNumber(value),
                _ => throw new InvalidOperationException("Unknown output schema type: " + type)
            };
        }
        static void Validate(JToken value, JObject schema, JObject definitions, string path, List<JObject> errors)
        {
            if (schema == null) throw new InvalidOperationException("Missing requested action output schema.");
            if (schema["$ref"] is JValue reference)
            {
                var text = reference.Value<string>();
                if (text == null || !text.StartsWith("#/$defs/", StringComparison.Ordinal) || definitions?[text.Substring(8)] is not JObject target)
                    throw new InvalidOperationException("Missing portable output definition: " + text);
                Validate(value, target, definitions, path, errors);
            }
            if (schema["const"] != null && !JToken.DeepEquals(value, schema["const"])) Error(errors, path, "Value differs from the declared constant.");
            if (schema["enum"] is JArray allowed && !allowed.Any(x => JToken.DeepEquals(x, value))) Error(errors, path, "Value is outside the declared enum.");
            if (schema["type"] != null)
            {
                var types = schema["type"] is JArray array ? array.Select(x => x.Value<string>()) : new[] { schema["type"].Value<string>() };
                if (!types.Any(x => IsType(value, x))) { Error(errors, path, "Serialized JSON type differs from the declared type."); return; }
            }
            foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
            {
                if (schema[keyword] is not JArray alternatives) continue;
                var results = alternatives.OfType<JObject>().Select(branch =>
                {
                    var candidate = new List<JObject>(); Validate(value, branch, definitions, path, candidate); return candidate;
                }).ToArray();
                if (keyword == "allOf") { foreach (var result in results) errors.AddRange(result); }
                else if (keyword == "anyOf" ? results.All(x => x.Count != 0) : results.Count(x => x.Count == 0) != 1)
                {
                    Error(errors, path, "Value does not satisfy " + keyword + " action alternatives.");
                    if (results.Length != 0) errors.AddRange(results.OrderBy(x => x.Count).First());
                }
            }
            if (schema["not"] is JObject forbidden)
            {
                var candidate = new List<JObject>(); Validate(value, forbidden, definitions, path, candidate);
                if (candidate.Count == 0) Error(errors, path, "Value matches a forbidden schema.");
            }
            if (value is JObject obj)
            {
                if (schema["required"] is JArray required)
                    foreach (var property in required.Values<string>()) if (!obj.ContainsKey(property)) Error(errors, path + "." + property, "Required serialized property is missing.");
                var properties = schema["properties"] as JObject;
                var patterns = schema["patternProperties"] as JObject;
                foreach (var property in obj.Properties())
                {
                    var covered = false;
                    if (properties?[property.Name] is JObject declared) { Validate(property.Value, declared, definitions, path + "." + property.Name, errors); covered = true; }
                    if (patterns != null)
                        foreach (var pattern in patterns.Properties()) if (Regex.IsMatch(property.Name, pattern.Name, RegexOptions.CultureInvariant)) { Validate(property.Value, (JObject)pattern.Value, definitions, path + "." + property.Name, errors); covered = true; }
                    if (covered) continue;
                    if (schema["additionalProperties"]?.Type == JTokenType.Boolean && schema["additionalProperties"].Value<bool>() == false) Error(errors, path + "." + property.Name, "Undeclared serialized property.");
                    else if (schema["additionalProperties"] is JObject additional) Validate(property.Value, additional, definitions, path + "." + property.Name, errors);
                }
                if (schema["minProperties"]?.Value<int>() is int minimum && obj.Count < minimum) Error(errors, path, "Too few serialized properties.");
                if (schema["maxProperties"]?.Value<int>() is int maximum && obj.Count > maximum) Error(errors, path, "Too many serialized properties.");
            }
            if (value is JArray values)
            {
                var prefixes = schema["prefixItems"] as JArray;
                for (var i = 0; i < values.Count; i++)
                {
                    if (prefixes != null && i < prefixes.Count) Validate(values[i], (JObject)prefixes[i], definitions, path + "[" + i + "]", errors);
                    else if (schema["items"] is JObject item) Validate(values[i], item, definitions, path + "[" + i + "]", errors);
                    else if (schema["items"]?.Type == JTokenType.Boolean && schema["items"].Value<bool>() == false) Error(errors, path + "[" + i + "]", "Undeclared array item.");
                }
                if (schema["minItems"]?.Value<int>() is int minimum && values.Count < minimum) Error(errors, path, "Too few array items.");
                if (schema["maxItems"]?.Value<int>() is int maximum && values.Count > maximum) Error(errors, path, "Too many array items.");
                if (schema.Value<bool?>("uniqueItems") == true && values.Select(x => x.ToString(Formatting.None)).Distinct(StringComparer.Ordinal).Count() != values.Count) Error(errors, path, "Array items are not unique.");
            }
            if (value?.Type == JTokenType.String)
            {
                var text = value.Value<string>();
                if (schema["minLength"]?.Value<int>() is int minimum && text.Length < minimum) Error(errors, path, "String is shorter than declared.");
                if (schema["maxLength"]?.Value<int>() is int maximum && text.Length > maximum) Error(errors, path, "String is longer than declared.");
                if (schema["pattern"]?.Value<string>() is string pattern && !Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant)) Error(errors, path, "String does not match the declared pattern.");
            }
            if (value?.Type == JTokenType.Integer || value?.Type == JTokenType.Float)
            {
                // Json.NET reads unsigned values above Int64.MaxValue as
                // BigInteger. Value<double>() rejects that representation and
                // double also loses integer boundary precision above 2^53.
                // Compare the invariant wire decimals only when a bound exists.
                if (schema["minimum"] is JToken minimum && CompareNumbers(value, minimum) < 0) Error(errors, path, "Number is below the declared minimum.");
                if (schema["maximum"] is JToken maximum && CompareNumbers(value, maximum) > 0) Error(errors, path, "Number is above the declared maximum.");
                if (schema["exclusiveMinimum"] is JToken exclusiveMinimum && CompareNumbers(value, exclusiveMinimum) <= 0) Error(errors, path, "Number is below the exclusive minimum.");
                if (schema["exclusiveMaximum"] is JToken exclusiveMaximum && CompareNumbers(value, exclusiveMaximum) >= 0) Error(errors, path, "Number is above the exclusive maximum.");
            }
        }

        struct WireNumber
        {
            internal string Digits;
            internal long Exponent;
            internal int Sign;
        }

        static WireNumber ReadNumber(JToken value)
        {
            if (value?.Type != JTokenType.Integer && value?.Type != JTokenType.Float)
                throw new InvalidOperationException("Output schema numeric bound must be a JSON number.");
            var text = value.ToString(Formatting.None);
            var exponentAt = text.IndexOfAny(new[] { 'e', 'E' });
            long exponent = 0;
            if (exponentAt >= 0)
            {
                exponent = long.Parse(text.Substring(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                text = text.Substring(0, exponentAt);
            }
            var sign = text.StartsWith("-", StringComparison.Ordinal) ? -1 : 1;
            if (sign < 0) text = text.Substring(1);
            var point = text.IndexOf('.');
            if (point >= 0)
            {
                exponent = checked(exponent - (text.Length - point - 1));
                text = text.Remove(point, 1);
            }
            var digits = text.TrimStart('0');
            if (digits.Length == 0) return new WireNumber { Digits = "0", Exponent = 0, Sign = 0 };
            var trimmed = digits.TrimEnd('0');
            exponent = checked(exponent + digits.Length - trimmed.Length);
            return new WireNumber { Digits = trimmed, Exponent = exponent, Sign = sign };
        }

        static bool IsWholeNumber(JToken value)
        {
            var number = ReadNumber(value);
            return number.Sign == 0 || number.Exponent >= 0;
        }

        static int CompareNumbers(JToken left, JToken right)
        {
            var a = ReadNumber(left);
            var b = ReadNumber(right);
            var comparison = a.Sign.CompareTo(b.Sign);
            if (comparison != 0 || a.Sign == 0) return comparison;
            var aMagnitude = checked(a.Exponent + a.Digits.Length);
            var bMagnitude = checked(b.Exponent + b.Digits.Length);
            comparison = aMagnitude.CompareTo(bMagnitude);
            if (comparison != 0) return comparison * a.Sign;
            // Equal decimal magnitude: compare digits with virtual trailing
            // zeros, without rounding or allocating exponent-sized integers.
            for (var i = 0; i < Math.Max(a.Digits.Length, b.Digits.Length); i++)
            {
                var aDigit = i < a.Digits.Length ? a.Digits[i] : '0';
                var bDigit = i < b.Digits.Length ? b.Digits[i] : '0';
                comparison = aDigit.CompareTo(bDigit);
                if (comparison != 0) return comparison * a.Sign;
            }
            return 0;
        }
    }
}
