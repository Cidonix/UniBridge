using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Reflection fallback only. SerializedObject and dedicated component routes keep precedence.
    internal static class ReflectionPropertyPath
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;

        internal sealed class WriteResult
        {
            public bool Attempted, Written, ReadbackAvailable;
            public object ExpectedValue, ActualValue;
            public string Error;
        }

        sealed class Endpoint
        {
            public Type ValueType;
            public Func<object> Read;
            public Action<object> Write;
            public string ShaderKind;
        }

        internal static WriteResult Write(object root, string path, JToken token, JsonSerializer serializer,
            Func<JToken, Type, JsonSerializer, object> convert)
        {
            var result = new WriteResult();
            try
            {
                var endpoint = Resolve(root, path, true);
                result.ExpectedValue = ConvertValue(endpoint, token, serializer, convert);
                if (result.ExpectedValue == null && token != null && token.Type != JTokenType.Null)
                    throw new InvalidOperationException("Property payload resolved to null.");
                result.Attempted = true;
                endpoint.Write(result.ExpectedValue);
                result.Written = true;
            }
            catch (Exception error)
            {
                result.Error = (result.Attempted ? "Setter was attempted and may have changed the target: " : "Write was rejected before setter: ") + Message(error);
            }
            if (result.Attempted)
            {
                // Resolve again: a setter/callback may replace a nested parent or collection.
                string readError;
                result.ReadbackAvailable = TryRead(root, path, out result.ActualValue, out readError);
                if (!result.ReadbackAvailable)
                    result.Error = (result.Error == null ? "" : result.Error + " ") + "Current route readback failed: " + readError;
            }
            return result;
        }

        internal static bool TryRead(object root, string path, out object value, out string error)
        {
            value = null; error = null;
            try { value = Resolve(root, path, false).Read(); return true; }
            catch (Exception failure) { error = Message(failure); return false; }
        }

        static Endpoint Resolve(object root, string path, bool writing)
        {
            if (root == null || string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A target and property path are required.");
            var parts = path.Split('.');
            object owner = root;
            for (int segment = 0; segment < parts.Length; segment++)
            {
                var part = parts[segment];
                if (part.Length == 0) throw new ArgumentException("Empty property path segment.");
                string name = part;
                int index = -1;
                int bracket = part.IndexOf('[');
                if (bracket >= 0)
                {
                    if (bracket == 0 || part[part.Length - 1] != ']' || part.IndexOf('[', bracket + 1) >= 0 ||
                        part.IndexOf(']') != part.Length - 1 ||
                        !int.TryParse(part.Substring(bracket + 1, part.Length - bracket - 2), NumberStyles.None, CultureInfo.InvariantCulture, out index))
                        throw new ArgumentException("Expected one nonnegative array/list index after a member name.");
                    name = part.Substring(0, bracket);
                }
                else if (part.IndexOf(']') >= 0) throw new ArgumentException("Unmatched property index bracket.");
                if (owner == null) throw new InvalidOperationException("Nested target is null at '" + name + "'.");
                if (writing && owner.GetType().IsValueType)
                    throw new InvalidOperationException("Nested value-type writes require replacing the containing value; boxed copies are not writable routes.");
                bool last = segment == parts.Length - 1;
                Endpoint endpoint;
                var material = owner as Material;
                if (last && index < 0 && material != null && name.StartsWith("_", StringComparison.Ordinal))
                    endpoint = ShaderEndpoint(material, name);
                else
                    endpoint = MemberEndpoint(owner, name);
                if (index >= 0)
                {
                    if (endpoint.Read == null) throw new InvalidOperationException("Indexed member is not readable.");
                    var collection = endpoint.Read() as IList;
                    if (collection == null) throw new InvalidOperationException("Indexed member must be an array or IList; dictionary keys are not property routes.");
                    if (index >= collection.Count) throw new ArgumentOutOfRangeException("index", "Property index exceeds the existing collection.");
                    var elementType = ElementType(collection.GetType());
                    var capturedIndex = index;
                    endpoint = new Endpoint { ValueType = elementType, Read = () => collection[capturedIndex],
                        Write = collection.IsReadOnly ? null : (Action<object>)(value => collection[capturedIndex] = value) };
                }
                if (last)
                {
                    if (writing ? endpoint.Write == null : endpoint.Read == null)
                        throw new InvalidOperationException(writing ? "Property route is not writable." : "Property route is not readable.");
                    return endpoint;
                }
                if (endpoint.Read == null) throw new InvalidOperationException("Intermediate member is not readable.");
                owner = endpoint.Read();
            }
            throw new InvalidOperationException("Property route did not resolve.");
        }

        static Endpoint MemberEndpoint(object owner, string name)
        {
            var type = owner.GetType();
            var property = type.GetProperty(name, Flags);
            if (property != null)
            {
                if (property.GetIndexParameters().Length != 0) throw new InvalidOperationException("Custom indexer properties are not supported.");
                return new Endpoint { ValueType = property.PropertyType,
                    Read = property.CanRead ? (Func<object>)(() => property.GetValue(owner, null)) : null,
                    Write = property.CanWrite ? (Action<object>)(value => property.SetValue(owner, value, null)) : null };
            }
            var field = type.GetField(name, Flags);
            if (field == null) throw new MissingMemberException(type.FullName, name);
            return new Endpoint { ValueType = field.FieldType, Read = () => field.GetValue(owner),
                Write = field.IsInitOnly || field.IsLiteral ? null : (Action<object>)(value => field.SetValue(owner, value)) };
        }

        static Type ElementType(Type collection)
        {
            if (collection.IsArray) return collection.GetElementType();
            foreach (var type in collection.GetInterfaces())
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IList<>))
                    return type.GetGenericArguments()[0];
            return typeof(object);
        }

        static Endpoint ShaderEndpoint(Material material, string name)
        {
            if (!material.HasProperty(name) || material.shader == null) throw new MissingMemberException("Material shader property was not found: " + name);
            string kind = null;
            for (int i = 0; i < ShaderUtil.GetPropertyCount(material.shader); i++)
                if (ShaderUtil.GetPropertyName(material.shader, i) == name) { kind = ShaderUtil.GetPropertyType(material.shader, i).ToString(); break; }
            switch (kind)
            {
                case "Color": return new Endpoint { ValueType = typeof(Color), ShaderKind = kind, Read = () => material.GetColor(name), Write = value => material.SetColor(name, (Color)value) };
                case "Vector": return new Endpoint { ValueType = typeof(Vector4), ShaderKind = kind, Read = () => material.GetVector(name), Write = value => material.SetVector(name, (Vector4)value) };
                case "Float": case "Range": return new Endpoint { ValueType = typeof(float), ShaderKind = kind, Read = () => material.GetFloat(name), Write = value => material.SetFloat(name, (float)value) };
                case "Int": return new Endpoint { ValueType = typeof(int), ShaderKind = kind, Read = () => material.GetInt(name), Write = value => material.SetInt(name, (int)value) };
                case "TexEnv": return new Endpoint { ValueType = typeof(Texture), ShaderKind = kind, Read = () => material.GetTexture(name), Write = value => material.SetTexture(name, (Texture)value) };
                default: throw new InvalidOperationException("Unsupported shader property kind: " + kind);
            }
        }

        static object ConvertValue(Endpoint endpoint, JToken token, JsonSerializer serializer,
            Func<JToken, Type, JsonSerializer, object> convert)
        {
            if ((endpoint.ShaderKind == "Float" || endpoint.ShaderKind == "Range") && token != null && token.Type == JTokenType.Boolean)
                return token.Value<bool>() ? 1f : 0f;
            if (endpoint.ShaderKind == "Vector" && token is JArray)
            {
                var array = (JArray)token;
                if (array.Count == 2) { var value = (Vector2)convert(token, typeof(Vector2), serializer); return new Vector4(value.x, value.y, 0f, 0f); }
                if (array.Count == 3) { var value = (Vector3)convert(token, typeof(Vector3), serializer); return new Vector4(value.x, value.y, value.z, 0f); }
            }
            return convert(token, endpoint.ValueType, serializer);
        }

        static string Message(Exception error)
        {
            var invocation = error as TargetInvocationException;
            return (invocation != null && invocation.InnerException != null ? invocation.InnerException : error).Message;
        }
    }
}
