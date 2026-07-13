using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    /// <summary>
    /// Generates JSON schemas from C# types for MCP tool parameters.
    /// </summary>
    static class SchemaGenerator
    {
        /// <summary>
        /// Generate a JSON schema object from a C# type.
        /// Returns null for basic types that don't warrant schemas.
        /// </summary>
        /// <param name="type">The type to generate schema for</param>
        /// <returns>JSON schema object or null if no schema is needed</returns>
        public static object GenerateSchema(Type type)
        {
            if (type == null || !SchemaGenerationHelper.ShouldGenerateSchemaForType(type))
            {
                return null;
            }

            return GenerateObjectSchema(type);
        }

        static object GenerateObjectSchema(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // Skip properties that can't be set
                if (!prop.CanWrite)
                    continue;

                var propName = prop.Name;
                var propSchema = GeneratePropertySchema(prop);

                properties[propName] = propSchema;

                // Determine if property is required
                if (IsRequiredProperty(prop))
                {
                    required.Add(propName);
                }
            }

            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties
            };

            if (required.Count > 0)
            {
                schema["required"] = required.ToArray();
            }

            return schema;
        }

        static object GeneratePropertySchema(PropertyInfo property)
        {
            var type = property.PropertyType;
            var schema = new Dictionary<string, object>();

            // Handle nullable types
            if (IsNullableType(type))
            {
                type = Nullable.GetUnderlyingType(type);
            }

            // Newtonsoft's JArray is not a generic collection, so it needs an
            // explicit array schema instead of falling through as JToken/object.
            schema["type"] = GetJsonType(type);

            // Add description from DisplayName or Description attributes
            var description = GetPropertyDescription(property);
            if (!string.IsNullOrEmpty(description))
            {
                schema["description"] = description;
            }

            // Handle enum constraints
            var enumValues = GetEnumValues(property, type);
            if (enumValues != null && enumValues.Length > 0)
            {
                schema["enum"] = enumValues;
            }

            // Handle arrays/collections
            if (IsJsonArrayType(type))
            {
                schema["type"] = "array";
                schema["items"] = CreateFlexibleObjectSchema();
            }
            else if (type.IsArray || (type.IsGenericType && IsCollectionType(type)))
            {
                schema["type"] = "array";
                var itemType = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];
                schema["items"] = GenerateTypeSchema(itemType);
            }

            // Add default value if available
            var defaultValue = GetDefaultValue(property);
            if (defaultValue != null)
            {
                schema["default"] = defaultValue;
            }

            return schema;
        }

        static object GenerateTypeSchema(Type type)
        {
            if (IsJsonArrayType(type))
            {
                return new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = CreateFlexibleObjectSchema()
                };
            }

            if (type == typeof(object) || IsJsonObjectType(type) || IsDictionaryType(type))
            {
                return CreateFlexibleObjectSchema();
            }

            if (type.IsEnum)
            {
                return new
                {
                    type = "string",
                    @enum = GetEnumNames(type)
                };
            }

            if (type.IsPrimitive || type == typeof(string) || type == typeof(DateTime) || type == typeof(Guid))
            {
                return new { type = GetJsonType(type) };
            }

            // For complex types, generate full object schema
            return GenerateObjectSchema(type);
        }

        static string GetJsonType(Type type)
        {
            if (type == null)
                return "null";

            // Handle nullable types
            if (IsNullableType(type))
            {
                type = Nullable.GetUnderlyingType(type);
            }

            return type switch
            {
                var t when t == typeof(bool) => "boolean",
                var t when t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
                          t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong) => "integer",
                var t when t == typeof(float) || t == typeof(double) || t == typeof(decimal) => "number",
                var t when t == typeof(string) || t == typeof(char) || t == typeof(Guid) => "string",
                var t when t == typeof(DateTime) || t == typeof(DateTimeOffset) => "string", // ISO 8601
                var t when t.IsEnum => "string",
                var t when t.IsArray || IsCollectionType(t) || IsJsonArrayType(t) => "array",
                var t when IsDictionaryType(t) || IsJsonObjectType(t) => "object",
                _ => "object"
            };
        }

        static Dictionary<string, object> CreateFlexibleObjectSchema() => new()
        {
            ["type"] = "object",
            ["additionalProperties"] = true
        };

        static bool IsNullableType(Type type) =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);

        static bool IsCollectionType(Type type)
        {
            if (!type.IsGenericType)
                return false;

            var genericDef = type.GetGenericTypeDefinition();
            return genericDef == typeof(List<>) ||
                   genericDef == typeof(IList<>) ||
                   genericDef == typeof(ICollection<>) ||
                   genericDef == typeof(IEnumerable<>);
        }

        static bool IsDictionaryType(Type type)
        {
            if (type == null)
                return false;

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                return true;

            return type.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        }

        static bool IsJsonArrayType(Type type) =>
            type != null && typeof(JArray).IsAssignableFrom(type);

        static bool IsJsonObjectType(Type type) =>
            type != null && typeof(JToken).IsAssignableFrom(type) && !IsJsonArrayType(type);

        static bool IsRequiredProperty(PropertyInfo property)
        {
            // Check for our custom McpDescription attribute with Required = true
            var mcpDesc = property.GetCustomAttribute<McpDescriptionAttribute>();
            if (mcpDesc?.Required == true)
                return true;

            // Properties with no default value and non-nullable types are typically required
            var type = property.PropertyType;
            if (!IsNullableType(type) && !type.IsClass && type != typeof(string))
            {
                // Value types without default values are usually required
                var defaultValue = GetDefaultValue(property);
                return defaultValue == null;
            }

            return false;
        }

        static string GetPropertyName(PropertyInfo property) => property.Name;

        static string GetPropertyDescription(PropertyInfo property) =>
            property.GetCustomAttribute<McpDescriptionAttribute>()?.Description;

        static object GetDefaultValue(PropertyInfo property)
        {
            try
            {
                // Check for explicit default in McpDescription attribute first (mixed approach)
                var mcpDesc = property.GetCustomAttribute<McpDescriptionAttribute>();
                if (mcpDesc?.HasDefault == true)
                {
                    return ConvertDefaultValueForSchema(mcpDesc.Default, property.PropertyType);
                }

                // Fallback to property initializer default detection
                var type = property.PropertyType;

                // Try to detect property initializer by creating an instance and checking the property value
                var declaringType = property.DeclaringType;
                if (declaringType != null && !declaringType.IsAbstract)
                {
                    try
                    {
                        var instance = Activator.CreateInstance(declaringType);
                        var propertyValue = property.GetValue(instance);

                        // For value types, check if the value differs from the type's default
                        if (type.IsValueType && !IsNullableType(type))
                        {
                            var typeDefault = Activator.CreateInstance(type);
                            if (!Equals(propertyValue, typeDefault))
                            {
                                // Property has a non-default initializer
                                return ConvertDefaultValueForSchema(propertyValue, type);
                            }
                        }
                        else if (propertyValue != null)
                        {
                            // Reference type with non-null initializer
                            return ConvertDefaultValueForSchema(propertyValue, type);
                        }
                    }
                    catch
                    {
                        // If we can't create an instance, fall back to type default for value types only
                        if (type.IsValueType && !IsNullableType(type))
                        {
                            var defaultValue = Activator.CreateInstance(type);
                            return ConvertDefaultValueForSchema(defaultValue, type);
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors getting default values
            }

            return null;
        }

        /// <summary>
        /// Convert a default value to its JSON schema representation.
        /// </summary>
        static object ConvertDefaultValueForSchema(object value, Type type)
        {
            if (value == null) return null;

            // For enums, convert to string representation for JSON schema
            if (type.IsEnum)
            {
                var enumName = value.ToString();
                var convertedNames = GetEnumNames(type);
                var enumNames = Enum.GetNames(type);

                // Find the index of the enum value and return the converted name
                for (int i = 0; i < enumNames.Length; i++)
                {
                    if (enumNames[i] == enumName)
                    {
                        return convertedNames?[i] ?? enumName;
                    }
                }

                // Fallback to first converted value
                return convertedNames?[0] ?? enumName;
            }

            return value;
        }

        /// <summary>
        /// Get enum values for a property, checking McpDescription attribute and property type.
        /// </summary>
        static string[] GetEnumValues(PropertyInfo property, Type type)
        {
            var mcpDesc = property.GetCustomAttribute<McpDescriptionAttribute>();

            return mcpDesc?.EnumType?.IsEnum == true ? GetEnumNames(mcpDesc.EnumType) :
                   type.IsEnum ? GetEnumNames(type) :
                   null;
        }

        /// <summary>
        /// Get enum names
        /// </summary>
        static string[] GetEnumNames(Type enumType) =>
            !enumType.IsEnum ? null : Enum.GetNames(enumType);

        /// <summary>
        /// Attempts to generate an output schema from a method's return type using reflection.
        /// Handles all the reflection error handling and type checking internally.
        /// </summary>
        /// <param name="instance">The object instance to inspect</param>
        /// <param name="methodName">Name of the method to inspect (default: "Execute")</param>
        /// <param name="parameterTypes">Parameter types to match the method signature (optional)</param>
        /// <returns>JSON schema object or null if no schema can be generated</returns>
        public static object GenerateOutputSchemaFromMethod(object instance, string methodName = "Execute", Type[] parameterTypes = null)
        {
            if (instance == null) return null;

            try
            {
                MethodInfo method;
                if (parameterTypes != null)
                    method = instance.GetType().GetMethod(methodName, parameterTypes);
                else
                    method = instance.GetType().GetMethod(methodName);

                if (method != null)
                    return GenerateSchema(method.ReturnType);
            }
            catch (Exception)
            {
                // Ignore reflection errors
            }

            return null;
        }

    }
}
