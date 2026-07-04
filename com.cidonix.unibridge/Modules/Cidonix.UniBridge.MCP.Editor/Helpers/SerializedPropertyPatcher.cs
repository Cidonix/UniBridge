#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Shared SerializedObject patching for Unity objects. This is intentionally small and
    /// conservative: it sets exact or discoverable SerializedProperty paths and reports
    /// unsupported Unity property kinds instead of guessing.
    /// </summary>
    public static class SerializedPropertyPatcher
    {
        public sealed class PropertyPatchResult
        {
            public string RequestedName { get; set; }
            public bool Found { get; set; }
            public bool Success { get; set; }
            public string Error { get; set; }
            public List<PropertyPatchChange> Changes { get; } = new();

            public static PropertyPatchResult NotFound(string requestedName)
            {
                return new PropertyPatchResult
                {
                    RequestedName = requestedName,
                    Found = false,
                    Success = false
                };
            }

            public static PropertyPatchResult Failure(string requestedName, string error)
            {
                return new PropertyPatchResult
                {
                    RequestedName = requestedName,
                    Found = true,
                    Success = false,
                    Error = error
                };
            }
        }

        public sealed class PropertyPatchChange
        {
            public string requestedName;
            public string propertyPath;
            public string propertyType;
            public object before;
            public object after;
            public bool dryRun;
        }

        public static PropertyPatchResult TryApplyProperty(
            Object owner,
            SerializedObject serializedObject,
            string nameOrPath,
            JToken value,
            bool dryRun)
        {
            if (owner == null || serializedObject == null || string.IsNullOrWhiteSpace(nameOrPath))
            {
                return PropertyPatchResult.NotFound(nameOrPath);
            }

            var property = FindProperty(serializedObject, nameOrPath);
            if (property == null)
            {
                return PropertyPatchResult.NotFound(nameOrPath);
            }

            if (property.propertyPath == "m_Script")
            {
                return PropertyPatchResult.Failure(nameOrPath, "Property 'm_Script' is read-only and cannot be changed.");
            }

            if (!property.editable)
            {
                return PropertyPatchResult.Failure(nameOrPath, $"SerializedProperty '{property.propertyPath}' is not editable.");
            }

            var result = new PropertyPatchResult
            {
                RequestedName = nameOrPath,
                Found = true,
                Success = true
            };

            try
            {
                ApplyValue(owner, serializedObject, property, value, dryRun, nameOrPath, result.Changes);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = $"SerializedProperty '{property.propertyPath}' could not be set: {ex.Message}";
            }

            return result;
        }

        public static SerializedProperty FindProperty(SerializedObject serializedObject, string nameOrPath)
        {
            if (serializedObject == null || string.IsNullOrWhiteSpace(nameOrPath))
            {
                return null;
            }

            var exact = serializedObject.FindProperty(nameOrPath);
            if (exact != null)
            {
                return exact;
            }

            var normalized = NormalizeName(nameOrPath);
            var iterator = serializedObject.GetIterator();
            if (!iterator.NextVisible(true))
            {
                return null;
            }

            do
            {
                if (Matches(iterator, nameOrPath, normalized))
                {
                    return iterator.Copy();
                }
            }
            while (iterator.NextVisible(false));

            return null;
        }

        static bool Matches(SerializedProperty property, string requested, string normalizedRequested)
        {
            return string.Equals(property.name, requested, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(property.displayName, requested, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(property.propertyPath, requested, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(NormalizeName(property.name), normalizedRequested, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(NormalizeName(property.displayName), normalizedRequested, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(NormalizeName(property.propertyPath), normalizedRequested, StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var normalized = name.Trim().Replace(" ", string.Empty).Replace("_", string.Empty);
            if (normalized.StartsWith("m", StringComparison.OrdinalIgnoreCase) && normalized.Length > 1)
            {
                normalized = normalized.Substring(1);
            }

            return normalized.ToLowerInvariant();
        }

        static void ApplyValue(
            Object owner,
            SerializedObject serializedObject,
            SerializedProperty property,
            JToken value,
            bool dryRun,
            string requestedName,
            List<PropertyPatchChange> changes)
        {
            if (property.isFixedBuffer && value is JArray fixedBufferArray)
            {
                ApplyFixedBuffer(owner, serializedObject, property, fixedBufferArray, dryRun, requestedName, changes);
                return;
            }

            if (property.isArray && property.propertyType == SerializedPropertyType.Generic && value is JArray array)
            {
                ApplyArray(owner, serializedObject, property, array, dryRun, requestedName, changes);
                return;
            }

            if (property.propertyType == SerializedPropertyType.Generic && value is JObject obj)
            {
                ApplyGenericObject(owner, serializedObject, property, obj, dryRun, requestedName, changes);
                return;
            }

            var before = SerializePropertyValue(property);
            object after;

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.FixedBufferSize:
                    after = IsRenderingLayerMaskProperty(property)
                        ? ReadRenderingLayerMask(value)
                        : ReadInt(value);
                    if (!dryRun)
                    {
                        property.intValue = Convert.ToInt32(after, CultureInfo.InvariantCulture);
                    }
                    break;
                case SerializedPropertyType.LayerMask:
                    after = ReadLayerMask(value);
                    if (!dryRun)
                    {
                        property.intValue = Convert.ToInt32(after, CultureInfo.InvariantCulture);
                    }
                    break;
                case SerializedPropertyType.Boolean:
                    after = ReadBool(value);
                    if (!dryRun)
                    {
                        property.boolValue = (bool)after;
                    }
                    break;
                case SerializedPropertyType.Float:
                    after = ReadFloat(value);
                    if (!dryRun)
                    {
                        property.floatValue = Convert.ToSingle(after, CultureInfo.InvariantCulture);
                    }
                    break;
                case SerializedPropertyType.String:
                    after = value == null || value.Type == JTokenType.Null ? null : value.ToString();
                    if (!dryRun)
                    {
                        property.stringValue = (string)after;
                    }
                    break;
                case SerializedPropertyType.Color:
                    var color = ReadColor(value);
                    after = SerializeColor(color);
                    if (!dryRun)
                    {
                        property.colorValue = color;
                    }
                    break;
                case SerializedPropertyType.Vector2:
                    var vector2 = ReadVector2(value);
                    after = SerializeVector2(vector2);
                    if (!dryRun)
                    {
                        property.vector2Value = vector2;
                    }
                    break;
                case SerializedPropertyType.Vector3:
                    var vector3 = ReadVector3(value);
                    after = SerializeVector3(vector3);
                    if (!dryRun)
                    {
                        property.vector3Value = vector3;
                    }
                    break;
                case SerializedPropertyType.Vector4:
                    var vector4 = ReadVector4(value);
                    after = SerializeVector4(vector4);
                    if (!dryRun)
                    {
                        property.vector4Value = vector4;
                    }
                    break;
                case SerializedPropertyType.Vector2Int:
                    var vector2Int = ReadVector2Int(value);
                    after = new { x = vector2Int.x, y = vector2Int.y };
                    if (!dryRun)
                    {
                        property.vector2IntValue = vector2Int;
                    }
                    break;
                case SerializedPropertyType.Vector3Int:
                    var vector3Int = ReadVector3Int(value);
                    after = new { x = vector3Int.x, y = vector3Int.y, z = vector3Int.z };
                    if (!dryRun)
                    {
                        property.vector3IntValue = vector3Int;
                    }
                    break;
                case SerializedPropertyType.Rect:
                    var rect = ReadRect(value);
                    after = new { rect.x, rect.y, rect.width, rect.height };
                    if (!dryRun)
                    {
                        property.rectValue = rect;
                    }
                    break;
                case SerializedPropertyType.RectInt:
                    var rectInt = ReadRectInt(value);
                    after = new { rectInt.x, rectInt.y, rectInt.width, rectInt.height };
                    if (!dryRun)
                    {
                        property.rectIntValue = rectInt;
                    }
                    break;
                case SerializedPropertyType.Bounds:
                    var bounds = ReadBounds(value);
                    after = new { center = SerializeVector3(bounds.center), size = SerializeVector3(bounds.size) };
                    if (!dryRun)
                    {
                        property.boundsValue = bounds;
                    }
                    break;
                case SerializedPropertyType.BoundsInt:
                    var boundsInt = ReadBoundsInt(value);
                    after = new
                    {
                        position = new { boundsInt.position.x, boundsInt.position.y, boundsInt.position.z },
                        size = new { boundsInt.size.x, boundsInt.size.y, boundsInt.size.z }
                    };
                    if (!dryRun)
                    {
                        property.boundsIntValue = boundsInt;
                    }
                    break;
                case SerializedPropertyType.Quaternion:
                    var quaternion = ReadQuaternion(value);
                    after = SerializeQuaternion(quaternion);
                    if (!dryRun)
                    {
                        property.quaternionValue = quaternion;
                    }
                    break;
                case SerializedPropertyType.Enum:
                    var enumIndex = ReadEnumIndex(property, value);
                    after = new
                    {
                        index = enumIndex,
                        name = enumIndex >= 0 && enumIndex < property.enumNames.Length ? property.enumNames[enumIndex] : null,
                        displayName = enumIndex >= 0 && enumIndex < property.enumDisplayNames.Length ? property.enumDisplayNames[enumIndex] : null
                    };
                    if (!dryRun)
                    {
                        property.enumValueIndex = enumIndex;
                    }
                    break;
                case SerializedPropertyType.ObjectReference:
                    var reference = ResolveObjectReference(property, value);
                    after = SerializeObjectReference(reference);
                    if (!dryRun)
                    {
                        property.objectReferenceValue = reference;
                    }
                    break;
                case SerializedPropertyType.ExposedReference:
                    var exposedReference = ResolveObjectReference(property, value);
                    after = SerializeObjectReference(exposedReference);
                    if (!dryRun)
                    {
                        property.exposedReferenceValue = exposedReference;
                    }
                    break;
                case SerializedPropertyType.Character:
                    var ch = ReadCharacter(value);
                    after = ch.ToString();
                    if (!dryRun)
                    {
                        property.intValue = ch;
                    }
                    break;
                case SerializedPropertyType.AnimationCurve:
                    var curve = ReadAnimationCurve(value);
                    after = SerializeAnimationCurve(curve);
                    if (!dryRun)
                    {
                        property.animationCurveValue = curve;
                    }
                    break;
                case SerializedPropertyType.Gradient:
                    var gradient = ReadGradient(value);
                    after = SerializeGradient(gradient);
                    if (!dryRun)
                    {
                        property.gradientValue = gradient;
                    }
                    break;
                case SerializedPropertyType.ManagedReference:
                    after = ApplyManagedReference(owner, serializedObject, property, value, dryRun, requestedName, changes);
                    break;
                case SerializedPropertyType.Hash128:
                    var hash = ReadHash128(value);
                    after = hash.ToString();
                    if (!dryRun)
                    {
                        property.hash128Value = hash;
                    }
                    break;
                default:
                    throw new NotSupportedException($"SerializedProperty type '{property.propertyType}' is not supported by the generic patcher yet.");
            }

            changes.Add(new PropertyPatchChange
            {
                requestedName = requestedName,
                propertyPath = property.propertyPath,
                propertyType = property.propertyType.ToString(),
                before = before,
                after = after,
                dryRun = dryRun
            });
        }

        static void ApplyFixedBuffer(
            Object owner,
            SerializedObject serializedObject,
            SerializedProperty property,
            JArray array,
            bool dryRun,
            string requestedName,
            List<PropertyPatchChange> changes)
        {
            var size = property.fixedBufferSize;
            if (array.Count > size)
            {
                throw new ArgumentException($"Fixed buffer '{property.propertyPath}' has size {size}, but {array.Count} value(s) were supplied.");
            }

            changes.Add(new PropertyPatchChange
            {
                requestedName = requestedName,
                propertyPath = property.propertyPath,
                propertyType = "FixedBuffer",
                before = SerializeFixedBufferProperty(property),
                after = new { fixedBuffer = true, fixedBufferSize = size, supplied = array.Count },
                dryRun = dryRun
            });

            if (dryRun)
                return;

            for (var i = 0; i < array.Count; i++)
            {
                var element = property.GetFixedBufferElementAtIndex(i);
                ApplyValue(owner, serializedObject, element, array[i], false, $"{requestedName}[{i}]", changes);
            }
        }

        static void ApplyArray(
            Object owner,
            SerializedObject serializedObject,
            SerializedProperty property,
            JArray array,
            bool dryRun,
            string requestedName,
            List<PropertyPatchChange> changes)
        {
            var beforeSize = property.arraySize;
            if (dryRun)
            {
                changes.Add(new PropertyPatchChange
                {
                    requestedName = requestedName,
                    propertyPath = property.propertyPath,
                    propertyType = property.propertyType.ToString(),
                    before = beforeSize,
                    after = array.Count,
                    dryRun = true
                });
                return;
            }

            property.arraySize = array.Count;
            for (var i = 0; i < array.Count; i++)
            {
                var element = property.GetArrayElementAtIndex(i);
                ApplyValue(owner, serializedObject, element, array[i], false, $"{requestedName}[{i}]", changes);
            }

            changes.Insert(0, new PropertyPatchChange
            {
                requestedName = requestedName,
                propertyPath = property.propertyPath,
                propertyType = property.propertyType.ToString(),
                before = beforeSize,
                after = array.Count,
                dryRun = false
            });
        }

        static void ApplyGenericObject(
            Object owner,
            SerializedObject serializedObject,
            SerializedProperty property,
            JObject obj,
            bool dryRun,
            string requestedName,
            List<PropertyPatchChange> changes)
        {
            foreach (var childToken in obj.Properties())
            {
                var child = FindChildProperty(property, childToken.Name);
                if (child == null)
                {
                    throw new InvalidOperationException($"Child property '{childToken.Name}' was not found under '{property.propertyPath}'.");
                }

                ApplyValue(owner, serializedObject, child, childToken.Value, dryRun, $"{requestedName}.{childToken.Name}", changes);
            }
        }

        static SerializedProperty FindChildProperty(SerializedProperty parent, string childName)
        {
            var normalized = NormalizeName(childName);
            var copy = parent.Copy();
            var end = copy.GetEndProperty();
            var enterChildren = true;
            while (copy.NextVisible(enterChildren) && !SerializedProperty.EqualContents(copy, end))
            {
                enterChildren = false;
                if (Matches(copy, childName, normalized))
                {
                    return copy.Copy();
                }
            }

            return null;
        }

        static int ReadInt(JToken token)
        {
            return token.ToObject<int>();
        }

        static int ReadLayerMask(JToken token)
        {
            return ReadNamedMask(
                token,
                "LayerMask",
                name => LayerMask.NameToLayer(name),
                GetValidLayerNames());
        }

        static int ReadRenderingLayerMask(JToken token)
        {
            return RenderingLayerUtility.ReadRenderingLayerMaskToken(token);
        }

        static int ReadNamedMask(JToken token, string label, Func<string, int> resolveIndex, IReadOnlyCollection<string> validNames)
        {
            if (token == null || token.Type == JTokenType.Null)
                return 0;

            if (token.Type == JTokenType.Integer)
                return token.ToObject<int>();

            if (token.Type == JTokenType.String)
            {
                var text = token.ToString().Trim();
                if (string.Equals(text, "Everything", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "All", StringComparison.OrdinalIgnoreCase))
                    return -1;
                if (string.Equals(text, "Nothing", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "None", StringComparison.OrdinalIgnoreCase))
                    return 0;

                var index = resolveIndex(text);
                if (index < 0)
                    throw new ArgumentException($"Unknown {label} name '{text}'. Valid names: [{string.Join(", ", validNames)}].");
                return 1 << index;
            }

            if (token is JObject obj)
            {
                var nested = obj["layers"] ?? obj["Layers"] ?? obj["names"] ?? obj["Names"] ?? obj["value"] ?? obj["Value"];
                if (nested != null)
                    return ReadNamedMask(nested, label, resolveIndex, validNames);
            }

            if (token is JArray array)
            {
                var mask = 0;
                foreach (var item in array)
                {
                    if (item == null || item.Type == JTokenType.Null)
                        continue;

                    var value = ReadNamedMask(item, label, resolveIndex, validNames);
                    if (value == -1)
                        return -1;
                    mask |= value;
                }

                return mask;
            }

            throw new ArgumentException($"{label} value must be an integer, 'Everything', 'Nothing', a layer name, or an array of names.");
        }

        static bool IsRenderingLayerMaskProperty(SerializedProperty property)
        {
            if (property == null)
                return false;

            var normalizedName = NormalizeName(property.name);
            var normalizedPath = NormalizeName(property.propertyPath);
            return normalizedName == "renderinglayermask" || normalizedPath.EndsWith("renderinglayermask", StringComparison.Ordinal);
        }

        static string[] GetValidLayerNames()
        {
            var names = new List<string>();
            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }

            return names.ToArray();
        }

        static string[] BuildLayerNameArray()
        {
            var names = new string[32];
            for (var i = 0; i < names.Length; i++)
                names[i] = LayerMask.LayerToName(i);
            return names;
        }

        static string[] GetRenderingLayerNames()
        {
            return RenderingLayerUtility.GetRenderingLayerNames();
        }

        static object SerializeNamedMask(int value, string[] names)
        {
            if (value == -1)
                return "Everything";
            if (value == 0)
                return "Nothing";

            var selected = new List<string>();
            for (var i = 0; i < names.Length && i < 32; i++)
            {
                if ((value & (1 << i)) == 0)
                    continue;

                var name = names[i];
                selected.Add(string.IsNullOrWhiteSpace(name) ? i.ToString(CultureInfo.InvariantCulture) : name);
            }

            return selected.ToArray();
        }

        static bool ReadBool(JToken token)
        {
            return token.ToObject<bool>();
        }

        static float ReadFloat(JToken token)
        {
            return token.ToObject<float>();
        }

        static char ReadCharacter(JToken token)
        {
            if (token.Type == JTokenType.Integer)
            {
                return Convert.ToChar(token.ToObject<int>());
            }

            var text = token.ToString();
            if (string.IsNullOrEmpty(text))
            {
                throw new ArgumentException("Character value cannot be empty.");
            }

            return text[0];
        }

        static Color ReadColor(JToken token)
        {
            if (token.Type == JTokenType.String && ColorUtility.TryParseHtmlString(token.ToString(), out var htmlColor))
            {
                return htmlColor;
            }

            return new Color(
                ReadFloatMember(token, "r", 0),
                ReadFloatMember(token, "g", 1),
                ReadFloatMember(token, "b", 2),
                ReadFloatMember(token, "a", 3, 1f));
        }

        static Vector2 ReadVector2(JToken token)
        {
            return new Vector2(ReadFloatMember(token, "x", 0), ReadFloatMember(token, "y", 1));
        }

        static Vector3 ReadVector3(JToken token)
        {
            return new Vector3(ReadFloatMember(token, "x", 0), ReadFloatMember(token, "y", 1), ReadFloatMember(token, "z", 2));
        }

        static Vector4 ReadVector4(JToken token)
        {
            return new Vector4(ReadFloatMember(token, "x", 0), ReadFloatMember(token, "y", 1), ReadFloatMember(token, "z", 2), ReadFloatMember(token, "w", 3));
        }

        static Vector2Int ReadVector2Int(JToken token)
        {
            return new Vector2Int(ReadIntMember(token, "x", 0), ReadIntMember(token, "y", 1));
        }

        static Vector3Int ReadVector3Int(JToken token)
        {
            return new Vector3Int(ReadIntMember(token, "x", 0), ReadIntMember(token, "y", 1), ReadIntMember(token, "z", 2));
        }

        static Quaternion ReadQuaternion(JToken token)
        {
            return new Quaternion(ReadFloatMember(token, "x", 0), ReadFloatMember(token, "y", 1), ReadFloatMember(token, "z", 2), ReadFloatMember(token, "w", 3, 1f));
        }

        static Rect ReadRect(JToken token)
        {
            return new Rect(ReadFloatMember(token, "x", 0), ReadFloatMember(token, "y", 1), ReadFloatMember(token, "width", 2), ReadFloatMember(token, "height", 3));
        }

        static RectInt ReadRectInt(JToken token)
        {
            return new RectInt(ReadIntMember(token, "x", 0), ReadIntMember(token, "y", 1), ReadIntMember(token, "width", 2), ReadIntMember(token, "height", 3));
        }

        static Bounds ReadBounds(JToken token)
        {
            if (token is JObject obj && obj["center"] != null && obj["size"] != null)
            {
                return new Bounds(ReadVector3(obj["center"]), ReadVector3(obj["size"]));
            }

            throw new ArgumentException("Bounds value must be an object with center and size.");
        }

        static BoundsInt ReadBoundsInt(JToken token)
        {
            if (token is JObject obj && obj["position"] != null && obj["size"] != null)
            {
                return new BoundsInt(ReadVector3Int(obj["position"]), ReadVector3Int(obj["size"]));
            }

            throw new ArgumentException("BoundsInt value must be an object with position and size.");
        }

        static Hash128 ReadHash128(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return default;
            }

            var text = token.Type == JTokenType.String
                ? token.ToString()
                : token is JObject obj
                    ? ReadString(obj, "hash128", "hash", "value", "Value")
                    : token.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                return default;
            }

            try
            {
                return Hash128.Parse(text.Trim());
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Hash128 value '{text}' could not be parsed: {ex.Message}");
            }
        }

        static int ReadEnumIndex(SerializedProperty property, JToken token)
        {
            if (token.Type == JTokenType.Integer)
            {
                var index = token.ToObject<int>();
                if (index >= 0 && index < property.enumNames.Length)
                {
                    return index;
                }

                throw new ArgumentOutOfRangeException(nameof(token), $"Enum index {index} is outside 0..{property.enumNames.Length - 1}.");
            }

            var normalized = NormalizeEnumName(token.ToString());
            for (var i = 0; i < property.enumNames.Length; i++)
            {
                if (NormalizeEnumName(property.enumNames[i]) == normalized ||
                    NormalizeEnumName(property.enumDisplayNames[i]) == normalized)
                {
                    return i;
                }
            }

            throw new ArgumentException($"Enum value '{token}' was not found. Valid values: {string.Join(", ", property.enumNames)}.");
        }

        static string NormalizeEnumName(string value)
        {
            return (value ?? string.Empty).Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        }

        static Object ResolveObjectReference(SerializedProperty property, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            var expectedType = ResolveObjectReferenceType(property);
            var resolved = ResolveObjectReferenceValue(expectedType, token);
            if (resolved == null)
            {
                throw new ArgumentException($"Object reference value for '{property.propertyPath}' resolved to null. Provide a valid objectId/objectIdString, asset path/GUID, or a find/component payload.");
            }

            return resolved;
        }

        public static Object ResolveObjectReferenceValue(Type expectedType, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                return CastObjectReference(UnityApiAdapter.GetObjectFromId(token.ToObject<long>()), expectedType);
            }

            if (token.Type == JTokenType.String)
            {
                var text = token.ToString().Trim();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
                {
                    return CastObjectReference(UnityApiAdapter.GetObjectFromId(numericId), expectedType);
                }

                return ResolveObjectReferenceFromPath(text, expectedType);
            }

            if (token is JObject obj)
            {
                var id = ReadLong(obj, "objectId", "object_id", "objectIdString", "object_id_string", "instanceId", "instance_id", "instanceIdString", "instance_id_string", "id", "idString");
                if (id.HasValue)
                {
                    return CastObjectReference(UnityApiAdapter.GetObjectFromId(id.Value), expectedType);
                }

                var find = obj.Value<string>("find") ?? obj.Value<string>("target") ?? obj.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(find))
                {
                    var method = obj.Value<string>("method") ?? obj.Value<string>("searchMethod") ?? obj.Value<string>("search_method");
                    var go = SceneObjectLocator.FindObject(find, method, new SceneObjectLocator.Options
                    {
                        IncludeInactive = true,
                        IncludePrefabStage = true
                    });
                    if (go == null)
                    {
                        throw new ArgumentException($"Scene object '{find}' was not found for object reference payload.");
                    }

                    var componentName =
                        obj.Value<string>("component") ??
                        obj.Value<string>("componentName") ??
                        obj.Value<string>("component_name") ??
                        obj.Value<string>("type") ??
                        obj.Value<string>("typeName") ??
                        obj.Value<string>("type_name");
                    if (!string.IsNullOrWhiteSpace(componentName))
                    {
                        var componentType = FindType(componentName);
                        if (componentType == null || !typeof(Component).IsAssignableFrom(componentType))
                        {
                            throw new ArgumentException($"Component type '{componentName}' could not be resolved for object reference payload.");
                        }

                        var component = go.GetComponent(componentType);
                        if (component == null)
                        {
                            throw new ArgumentException($"Scene object '{find}' was found, but it does not have component '{componentName}'.");
                        }

                        return CastObjectReference(component, expectedType);
                    }

                    if (expectedType == typeof(Component))
                    {
                        var components = go.GetComponents<Component>().Where(component => component != null).ToArray();
                        if (components.Length == 1)
                        {
                            return components[0];
                        }

                        throw new ArgumentException($"Object reference for '{find}' targets a UnityEngine.Component field. Add component/componentName/type to disambiguate which component to assign.");
                    }

                    return CastObjectReference(go, expectedType);
                }

                var path = obj.Value<string>("assetPath") ?? obj.Value<string>("path") ?? obj.Value<string>("guid");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var fileId = ReadLong(obj, "fileID", "fileId", "localId", "localIdentifierInFile", "localIdentifier");
                    return ResolveObjectReferenceFromPath(path, expectedType, fileId);
                }
            }

            throw new ArgumentException("Object reference value must be null, an instance id/objectIdString, asset path/GUID, or an object with assetPath/path/guid/fileID/objectId/find/component.");
        }

        static object ApplyManagedReference(
            Object owner,
            SerializedObject serializedObject,
            SerializedProperty property,
            JToken token,
            bool dryRun,
            string requestedName,
            List<PropertyPatchChange> changes)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                if (!dryRun)
                {
                    property.managedReferenceValue = null;
                }

                return new
                {
                    managedReference = true,
                    type = (string)null,
                    operation = "set-null"
                };
            }

            if (token is not JObject obj)
            {
                throw new ArgumentException("ManagedReference value must be null or an object with type/fullTypeName/assemblyQualifiedName and optional properties.");
            }

            var currentTypeName = property.managedReferenceFullTypename;
            var requestedTypeName =
                ReadString(obj, "type") ??
                ReadString(obj, "fullTypeName") ??
                ReadString(obj, "full_type_name") ??
                ReadString(obj, "assemblyQualifiedName") ??
                ReadString(obj, "assembly_qualified_name") ??
                currentTypeName;

            var targetType = ResolveManagedReferenceType(requestedTypeName);
            if (targetType == null)
                throw new ArgumentException($"ManagedReference type '{requestedTypeName}' could not be resolved.");

            var fieldType = ResolveManagedReferenceType(property.managedReferenceFieldTypename);
            if (fieldType != null && !fieldType.IsAssignableFrom(targetType))
                throw new ArgumentException($"ManagedReference type '{targetType.FullName}' is not assignable to field type '{fieldType.FullName}'.");

            if (targetType.IsAbstract || targetType.IsInterface)
                throw new ArgumentException($"ManagedReference type '{targetType.FullName}' cannot be instantiated because it is abstract or an interface.");

            var propertyValues = ExtractManagedReferenceProperties(obj);
            if (!dryRun)
            {
                var instance = property.managedReferenceValue;
                if (instance == null || instance.GetType() != targetType)
                {
                    instance = Activator.CreateInstance(targetType);
                    property.managedReferenceValue = instance;
                    serializedObject.ApplyModifiedProperties();
                    serializedObject.Update();
                    property = serializedObject.FindProperty(property.propertyPath);
                    if (property == null)
                        throw new InvalidOperationException($"ManagedReference property '{requestedName}' could not be reacquired after assigning '{targetType.FullName}'.");
                }

                if (propertyValues.Count > 0)
                {
                    ApplyGenericObject(owner, serializedObject, property, propertyValues, false, requestedName, changes);
                }
            }

            return new
            {
                managedReference = true,
                type = targetType.FullName,
                fieldType = fieldType?.FullName,
                operation = propertyValues.Count > 0 ? "set-type-and-properties" : "set-type"
            };
        }

        static JObject ExtractManagedReferenceProperties(JObject obj)
        {
            if (obj["properties"] is JObject properties)
                return properties;
            if (obj["value"] is JObject value)
                return value;

            var result = new JObject();
            foreach (var prop in obj.Properties())
            {
                var name = NormalizeName(prop.Name);
                if (name is "type" or "fulltypename" or "assemblyqualifiedname" or "fulltypename" or "assemblyqualifiedname")
                    continue;
                result[prop.Name] = prop.Value.DeepClone();
            }

            return result;
        }

        static string ReadString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                    return token.ToString();
            }

            return null;
        }

        static Type ResolveManagedReferenceType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            var trimmed = typeName.Trim();
            var direct = Type.GetType(trimmed, false);
            if (direct != null)
                return direct;

            var separator = trimmed.IndexOf(' ');
            if (separator > 0 && separator < trimmed.Length - 1)
            {
                var assemblyName = trimmed.Substring(0, separator).Trim();
                var fullName = trimmed.Substring(separator + 1).Trim();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var assemblyType = assembly.GetType(fullName, false);
                    if (assemblyType != null)
                        return assemblyType;
                }

                trimmed = fullName;
            }

            return FindType(trimmed);
        }

        static long? ReadLong(JObject obj, params string[] names)
        {
            if (obj == null || names == null)
            {
                return null;
            }

            foreach (var name in names)
            {
                if (!obj.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var token) ||
                    token == null ||
                    token.Type == JTokenType.Null)
                {
                    continue;
                }

                if (token.Type == JTokenType.Integer)
                {
                    return token.Value<long>();
                }

                if (long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        static Object ResolveObjectReferenceFromPath(string pathOrGuid, Type expectedType, long? localFileId = null)
        {
            if (string.IsNullOrWhiteSpace(pathOrGuid))
            {
                return null;
            }

            var path = pathOrGuid.Replace('\\', '/').Trim();
            if (path.Length == 32 && !path.Contains("/"))
            {
                path = AssetDatabase.GUIDToAssetPath(path);
            }

            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                path = "Assets/" + path.TrimStart('/');
            }

            var targetType = expectedType ?? typeof(Object);
            if (localFileId.HasValue)
            {
                var allAssets = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (var candidate in allAssets)
                {
                    if (candidate == null)
                    {
                        continue;
                    }

                    if (!long.TryParse(GetLocalIdentifier(candidate), NumberStyles.Integer, CultureInfo.InvariantCulture, out var candidateId) ||
                        candidateId != localFileId.Value)
                    {
                        continue;
                    }

                    return CastObjectReference(candidate, targetType);
                }

                throw new ArgumentException($"Could not load {targetType.Name} at '{path}' with local fileID '{localFileId.Value}'.");
            }

            var asset = AssetDatabase.LoadAssetAtPath(path, targetType);
            if (asset == null && targetType != typeof(Object))
            {
                asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                asset = CastObjectReference(asset, targetType);
            }

            if (asset == null)
            {
                throw new ArgumentException($"Could not load {targetType.Name} at '{path}'.");
            }

            return asset;
        }

        static string GetLocalIdentifier(Object obj)
        {
            if (obj == null)
            {
                return null;
            }

            try
            {
                var method = typeof(Unsupported).GetMethod(
                    "GetLocalIdentifierInFileForPersistentObject",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var value = method?.Invoke(null, new object[] { obj });
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        static Object CastObjectReference(Object obj, Type expectedType)
        {
            if (obj == null || expectedType == null || expectedType == typeof(Object))
            {
                return obj;
            }

            if (expectedType.IsInstanceOfType(obj))
            {
                return obj;
            }

            if (obj is GameObject gameObject)
            {
                if (expectedType == typeof(GameObject))
                {
                    return gameObject;
                }

                if (typeof(Component).IsAssignableFrom(expectedType))
                {
                    return gameObject.GetComponent(expectedType);
                }
            }

            if (obj is Component component)
            {
                if (expectedType == typeof(GameObject))
                {
                    return component.gameObject;
                }

                if (typeof(Component).IsAssignableFrom(expectedType))
                {
                    return component.GetComponent(expectedType);
                }
            }

            throw new ArgumentException($"Object '{obj.name}' ({obj.GetType().Name}) cannot be assigned to {expectedType.Name}.");
        }

        static Type ResolveObjectReferenceType(SerializedProperty property)
        {
            if (property.objectReferenceValue != null)
            {
                return property.objectReferenceValue.GetType();
            }

            var typeName = property.type;
            if (string.IsNullOrWhiteSpace(typeName) || !typeName.StartsWith("PPtr<", StringComparison.Ordinal))
            {
                return null;
            }

            var inner = typeName.Substring(5).TrimEnd('>').TrimStart('$');
            return FindType(inner);
        }

        public static string GetObjectReferenceTypeName(SerializedProperty property)
        {
            return ResolveObjectReferenceType(property)?.FullName;
        }

        static Type FindType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            var direct = Type.GetType(typeName, false);
            if (direct != null)
            {
                return direct;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in GetTypesSafe(assembly))
                {
                    if (string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        static IEnumerable<Type> GetTypesSafe(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        static float ReadFloatMember(JToken token, string property, int index, float defaultValue = 0f)
        {
            if (token is JArray array)
            {
                return index < array.Count && TryReadFloatToken(array[index], out var arrayValue) ? arrayValue : defaultValue;
            }

            if (token is JObject obj)
            {
                return obj.TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var value) &&
                       TryReadFloatToken(value, out var objectValue)
                    ? objectValue
                    : defaultValue;
            }

            throw new ArgumentException($"Expected array or object with '{property}'.");
        }

        static bool TryReadFloatMember(JToken token, string property, out float value)
        {
            value = 0f;
            if (token is JObject obj && obj.TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var propertyToken))
            {
                return TryReadFloatToken(propertyToken, out value);
            }

            return false;
        }

        static bool TryReadFloatToken(JToken token, out float value)
        {
            value = 0f;
            if (token == null || token.Type == JTokenType.Null)
                return false;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = token.Value<float>();
                return true;
            }
            var text = token.ToString();
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        static bool TryReadStringMember(JToken token, string property, out string value)
        {
            value = null;
            if (token is JObject obj && obj.TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var propertyToken) && propertyToken.Type != JTokenType.Null)
            {
                value = propertyToken.ToString();
                return true;
            }

            return false;
        }

        static int ReadIntMember(JToken token, string property, int index, int defaultValue = 0)
        {
            if (token is JArray array)
            {
                return index < array.Count ? array[index].ToObject<int>() : defaultValue;
            }

            if (token is JObject obj)
            {
                return obj.TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var value)
                    ? value.ToObject<int>()
                    : defaultValue;
            }

            throw new ArgumentException($"Expected array or object with '{property}'.");
        }

        public static object SerializePropertyValue(SerializedProperty property)
        {
            return property.propertyType switch
            {
                SerializedPropertyType.Generic => property.isFixedBuffer
                    ? SerializeFixedBufferProperty(property)
                    : property.isArray
                        ? SerializeArrayProperty(property)
                        : SerializeGenericProperty(property),
                SerializedPropertyType.Integer => IsRenderingLayerMaskProperty(property)
                    ? SerializeNamedMask(property.intValue, GetRenderingLayerNames())
                    : property.intValue,
                SerializedPropertyType.Boolean => property.boolValue,
                SerializedPropertyType.Float => property.floatValue,
                SerializedPropertyType.String => property.stringValue,
                SerializedPropertyType.Color => SerializeColor(property.colorValue),
                SerializedPropertyType.ObjectReference => SerializeObjectReference(property.objectReferenceValue),
                SerializedPropertyType.ExposedReference => SerializeObjectReference(property.exposedReferenceValue),
                SerializedPropertyType.LayerMask => SerializeNamedMask(property.intValue, BuildLayerNameArray()),
                SerializedPropertyType.Enum => new
                {
                    index = property.enumValueIndex,
                    name = property.enumValueIndex >= 0 && property.enumValueIndex < property.enumNames.Length ? property.enumNames[property.enumValueIndex] : null,
                    displayName = property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length ? property.enumDisplayNames[property.enumValueIndex] : null
                },
                SerializedPropertyType.Vector2 => SerializeVector2(property.vector2Value),
                SerializedPropertyType.Vector3 => SerializeVector3(property.vector3Value),
                SerializedPropertyType.Vector4 => SerializeVector4(property.vector4Value),
                SerializedPropertyType.Rect => new { property.rectValue.x, property.rectValue.y, property.rectValue.width, property.rectValue.height },
                SerializedPropertyType.ArraySize => property.intValue,
                SerializedPropertyType.FixedBufferSize => property.fixedBufferSize,
                SerializedPropertyType.Character => Convert.ToChar(property.intValue).ToString(),
                SerializedPropertyType.AnimationCurve => SerializeAnimationCurve(property.animationCurveValue),
                SerializedPropertyType.Gradient => SerializeGradient(property.gradientValue),
                SerializedPropertyType.Bounds => new { center = SerializeVector3(property.boundsValue.center), size = SerializeVector3(property.boundsValue.size) },
                SerializedPropertyType.Quaternion => SerializeQuaternion(property.quaternionValue),
                SerializedPropertyType.Vector2Int => new { property.vector2IntValue.x, property.vector2IntValue.y },
                SerializedPropertyType.Vector3Int => new { property.vector3IntValue.x, property.vector3IntValue.y, property.vector3IntValue.z },
                SerializedPropertyType.RectInt => new { property.rectIntValue.x, property.rectIntValue.y, property.rectIntValue.width, property.rectIntValue.height },
                SerializedPropertyType.BoundsInt => new
                {
                    position = new { property.boundsIntValue.position.x, property.boundsIntValue.position.y, property.boundsIntValue.position.z },
                    size = new { property.boundsIntValue.size.x, property.boundsIntValue.size.y, property.boundsIntValue.size.z }
                },
                SerializedPropertyType.ManagedReference => new
                {
                    managedReference = true,
                    fieldType = property.managedReferenceFieldTypename,
                    type = property.managedReferenceFullTypename,
                    hasValue = property.managedReferenceValue != null,
                    id = property.managedReferenceId
                },
                SerializedPropertyType.Hash128 => property.hash128Value.ToString(),
                _ => property.propertyType.ToString()
            };
        }

        static object SerializeArrayProperty(SerializedProperty property)
        {
            const int inlineLimit = 12;
            var count = property.arraySize;
            var items = new List<object>();
            var limit = Math.Min(count, inlineLimit);
            for (var i = 0; i < limit; i++)
            {
                var element = property.GetArrayElementAtIndex(i);
                items.Add(new
                {
                    index = i,
                    path = element.propertyPath,
                    type = element.propertyType.ToString(),
                    value = SerializePropertyValue(element)
                });
            }

            return new
            {
                array = true,
                arraySize = count,
                returned = items.Count,
                truncated = count > items.Count,
                items
            };
        }

        static object SerializeFixedBufferProperty(SerializedProperty property)
        {
            const int inlineLimit = 16;
            var count = property.fixedBufferSize;
            var items = new List<object>();
            var limit = Math.Min(count, inlineLimit);
            for (var i = 0; i < limit; i++)
            {
                var element = property.GetFixedBufferElementAtIndex(i);
                items.Add(new
                {
                    index = i,
                    path = element.propertyPath,
                    type = element.propertyType.ToString(),
                    value = SerializePropertyValue(element)
                });
            }

            return new
            {
                fixedBuffer = true,
                fixedBufferSize = count,
                returned = items.Count,
                truncated = count > items.Count,
                items
            };
        }

        static object SerializeGenericProperty(SerializedProperty property)
        {
            const int childLimit = 24;
            var children = new List<object>();
            var copy = property.Copy();
            var end = copy.GetEndProperty();
            var enterChildren = true;

            while (copy.NextVisible(enterChildren) && !SerializedProperty.EqualContents(copy, end))
            {
                enterChildren = false;
                children.Add(new
                {
                    path = copy.propertyPath,
                    name = copy.name,
                    displayName = copy.displayName,
                    type = copy.propertyType.ToString(),
                    value = copy.propertyType == SerializedPropertyType.Generic
                        ? new { generic = true, copy.hasVisibleChildren }
                        : SerializePropertyValue(copy)
                });

                if (children.Count >= childLimit)
                    break;
            }

            return new
            {
                generic = true,
                returned = children.Count,
                truncated = children.Count >= childLimit,
                children
            };
        }

        static AnimationCurve ReadAnimationCurve(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return new AnimationCurve();
            }

            var obj = token as JObject;
            var keysToken = obj?["keys"] ?? obj?["Keys"] ?? token as JArray;
            if (keysToken is not JArray keysArray)
            {
                throw new ArgumentException("AnimationCurve value must be an object with a keys array or a keys array directly.");
            }

            var keys = new List<Keyframe>();
            foreach (var keyToken in keysArray)
            {
                var key = new Keyframe(
                    ReadFloatMember(keyToken, "time", 0),
                    ReadFloatMember(keyToken, "value", 1),
                    ReadFloatMember(keyToken, "inTangent", 2),
                    ReadFloatMember(keyToken, "outTangent", 3));

                if (TryReadFloatMember(keyToken, "inWeight", out var inWeight))
                {
                    key.inWeight = inWeight;
                }

                if (TryReadFloatMember(keyToken, "outWeight", out var outWeight))
                {
                    key.outWeight = outWeight;
                }

                if (TryReadStringMember(keyToken, "weightedMode", out var weightedMode) &&
                    Enum.TryParse(weightedMode, true, out WeightedMode parsedWeightedMode))
                {
                    key.weightedMode = parsedWeightedMode;
                }

                keys.Add(key);
            }

            var curve = new AnimationCurve(keys.ToArray());
            if (obj != null)
            {
                curve.preWrapMode = ReadWrapMode(obj["preWrapMode"] ?? obj["PreWrapMode"], curve.preWrapMode);
                curve.postWrapMode = ReadWrapMode(obj["postWrapMode"] ?? obj["PostWrapMode"], curve.postWrapMode);
            }

            return curve;
        }

        static Gradient ReadGradient(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                var empty = new Gradient();
                empty.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
                return empty;
            }

            if (token is not JObject obj)
            {
                throw new ArgumentException("Gradient value must be an object with colorKeys and alphaKeys arrays.");
            }

            var colorKeysToken = obj["colorKeys"] ?? obj["ColorKeys"];
            var alphaKeysToken = obj["alphaKeys"] ?? obj["AlphaKeys"];
            if (colorKeysToken is not JArray colorArray || colorArray.Count == 0)
            {
                throw new ArgumentException("Gradient value requires a non-empty colorKeys array.");
            }

            var colorKeys = colorArray.Select(ReadGradientColorKey).ToArray();
            var alphaKeys = alphaKeysToken is JArray alphaArray && alphaArray.Count > 0
                ? alphaArray.Select(ReadGradientAlphaKey).ToArray()
                : colorKeys.Select(key => new GradientAlphaKey(key.color.a, key.time)).ToArray();

            var gradient = new Gradient();
            gradient.SetKeys(colorKeys, alphaKeys);
            if (TryReadStringMember(obj, "mode", out var mode) && Enum.TryParse(mode, true, out GradientMode parsedMode))
            {
                gradient.mode = parsedMode;
            }

            SetGradientColorSpaceIfPossible(gradient, obj["colorSpace"] ?? obj["ColorSpace"]);
            return gradient;
        }

        static GradientColorKey ReadGradientColorKey(JToken token)
        {
            var colorToken = token is JObject obj ? obj["color"] ?? obj["Color"] : null;
            var color = colorToken != null
                ? ReadColor(colorToken)
                : new Color(
                    ReadFloatMember(token, "r", 0),
                    ReadFloatMember(token, "g", 1),
                    ReadFloatMember(token, "b", 2),
                    ReadFloatMember(token, "a", 3, 1f));
            return new GradientColorKey(color, ReadFloatMember(token, "time", 4));
        }

        static GradientAlphaKey ReadGradientAlphaKey(JToken token)
        {
            return new GradientAlphaKey(
                ReadFloatMember(token, "alpha", 0, 1f),
                ReadFloatMember(token, "time", 1));
        }

        static WrapMode ReadWrapMode(JToken token, WrapMode fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            if (token.Type == JTokenType.Integer)
            {
                return (WrapMode)token.ToObject<int>();
            }

            return Enum.TryParse(token.ToString(), true, out WrapMode value) ? value : fallback;
        }

        static void SetGradientColorSpaceIfPossible(Gradient gradient, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return;
            }

            var property = typeof(Gradient).GetProperty("colorSpace", BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite || !property.PropertyType.IsEnum)
            {
                return;
            }

            try
            {
                var value = token.Type == JTokenType.Integer
                    ? Enum.ToObject(property.PropertyType, token.ToObject<int>())
                    : Enum.Parse(property.PropertyType, token.ToString(), true);
                property.SetValue(gradient, value);
            }
            catch
            {
                // Color space is informational on older Unity versions.
            }
        }

        static object SerializeAnimationCurve(AnimationCurve curve)
        {
            if (curve == null)
            {
                return null;
            }

            return new
            {
                keys = curve.keys.Select(key => new
                {
                    key.time,
                    key.value,
                    key.inTangent,
                    key.outTangent,
                    key.inWeight,
                    key.outWeight,
                    weightedMode = key.weightedMode.ToString()
                }).ToArray(),
                preWrapMode = curve.preWrapMode.ToString(),
                postWrapMode = curve.postWrapMode.ToString()
            };
        }

        static object SerializeGradient(Gradient gradient)
        {
            if (gradient == null)
            {
                return null;
            }

            return new
            {
                mode = gradient.mode.ToString(),
                colorSpace = GetGradientColorSpace(gradient),
                colorKeys = gradient.colorKeys.Select(key => new
                {
                    time = key.time,
                    r = key.color.r,
                    g = key.color.g,
                    b = key.color.b,
                    a = key.color.a
                }).ToArray(),
                alphaKeys = gradient.alphaKeys.Select(key => new
                {
                    time = key.time,
                    alpha = key.alpha
                }).ToArray()
            };
        }

        static object GetGradientColorSpace(Gradient gradient)
        {
            var property = typeof(Gradient).GetProperty("colorSpace", BindingFlags.Instance | BindingFlags.Public);
            return property != null ? property.GetValue(gradient)?.ToString() : null;
        }

        static object SerializeObjectReference(Object value)
        {
            if (value == null)
            {
                return null;
            }

            var assetPath = AssetDatabase.GetAssetPath(value);
            return new
            {
                name = value.name,
                type = value.GetType().FullName,
                id = UnityApiAdapter.GetObjectId(value),
                assetPath = string.IsNullOrWhiteSpace(assetPath) ? null : assetPath,
                guid = string.IsNullOrWhiteSpace(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath)
            };
        }

        static object SerializeVector2(Vector2 value) => new { x = value.x, y = value.y };
        static object SerializeVector3(Vector3 value) => new { x = value.x, y = value.y, z = value.z };
        static object SerializeVector4(Vector4 value) => new { x = value.x, y = value.y, z = value.z, w = value.w };
        static object SerializeQuaternion(Quaternion value) => new { x = value.x, y = value.y, z = value.z, w = value.w };
        static object SerializeColor(Color value) => new { r = value.r, g = value.g, b = value.b, a = value.a };
    }

    internal static class RenderingLayerUtility
    {
        const string TagManagerPath = "ProjectSettings/TagManager.asset";

        public static string[] GetRenderingLayerNames()
        {
            try
            {
                var tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath(TagManagerPath);
                var tagManager = tagManagerAssets.FirstOrDefault();
                if (tagManager != null)
                {
                    using var serializedObject = new SerializedObject(tagManager);
                    var renderingLayers = serializedObject.FindProperty("m_RenderingLayers");
                    if (renderingLayers != null && renderingLayers.isArray)
                    {
                        var result = new string[renderingLayers.arraySize];
                        for (var i = 0; i < renderingLayers.arraySize; i++)
                        {
                            result[i] = renderingLayers.GetArrayElementAtIndex(i)?.stringValue;
                        }

                        if (result.Length > 0)
                        {
                            if (string.IsNullOrWhiteSpace(result[0]))
                                result[0] = "Default";
                            return result;
                        }
                    }
                }
            }
            catch
            {
                // Keep inspection calls resilient while Unity is importing or settings are incomplete.
            }

            return new[] { "Default" };
        }

        public static object[] GetRenderingLayerEntries()
        {
            return GetRenderingLayerNames()
                .Select((name, index) => new
                {
                    index,
                    name,
                    bit = index < 32 ? 1L << index : 0L
                })
                .Cast<object>()
                .ToArray();
        }

        public static object SerializeRenderingLayerMask(Object obj)
        {
            if (obj == null)
                return null;

            try
            {
                using var serializedObject = new SerializedObject(obj);
                return SerializeRenderingLayerMask(serializedObject);
            }
            catch
            {
                return null;
            }
        }

        public static object SerializeRenderingLayerMask(SerializedObject serializedObject)
        {
            if (serializedObject == null)
                return null;

            var property = serializedObject.FindProperty("m_RenderingLayerMask");
            return property == null ? null : SerializeRenderingLayerMask(property.intValue);
        }

        public static object SerializeRenderingLayerMask(int mask)
        {
            var names = GetRenderingLayerNames();
            var selected = new List<string>();
            var knownBits = 0u;
            var unsignedMask = unchecked((uint)mask);
            var limit = Math.Min(names.Length, 32);

            for (var i = 0; i < limit; i++)
            {
                var bit = 1u << i;
                knownBits |= bit;
                if ((unsignedMask & bit) != 0 && !string.IsNullOrWhiteSpace(names[i]))
                    selected.Add(names[i]);
            }

            var unknownBits = unsignedMask & ~knownBits;
            var mode = mask switch
            {
                0 => "Nothing",
                -1 => "Everything",
                _ => "Named"
            };

            return new
            {
                mask,
                mode,
                names = mode == "Everything"
                    ? names.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray()
                    : selected.ToArray(),
                unknownBits = unknownBits == 0 ? 0L : unknownBits
            };
        }

        public static int ReadRenderingLayerMaskToken(JToken token)
        {
            var names = GetRenderingLayerNames();
            return ReadNamedMaskToken(
                token,
                "renderingLayerMask",
                name => Array.FindIndex(names, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)),
                names.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray(),
                BuildEverythingMask(names));
        }

        static int BuildEverythingMask(string[] names)
        {
            var mask = 0;
            for (var i = 0; i < names.Length && i < 32; i++)
            {
                if (!string.IsNullOrWhiteSpace(names[i]))
                    mask |= 1 << i;
            }

            return mask;
        }

        static int ReadNamedMaskToken(JToken token, string label, Func<string, int> resolveIndex, IReadOnlyCollection<string> validNames, int everythingMask)
        {
            if (token == null || token.Type == JTokenType.Null)
                return 0;

            if (token.Type == JTokenType.Integer)
                return token.ToObject<int>();

            if (token.Type == JTokenType.String)
            {
                var text = token.ToString().Trim();
                if (string.Equals(text, "Everything", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "All", StringComparison.OrdinalIgnoreCase))
                    return everythingMask;
                if (string.Equals(text, "Nothing", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "None", StringComparison.OrdinalIgnoreCase))
                    return 0;

                var index = resolveIndex(text);
                if (index < 0)
                    throw new ArgumentException($"Unknown {label} name '{text}'. Valid names: [{string.Join(", ", validNames)}].");
                return 1 << index;
            }

            if (token is JObject obj)
            {
                var nested = obj["layers"] ?? obj["Layers"] ?? obj["names"] ?? obj["Names"] ?? obj["value"] ?? obj["Value"];
                if (nested != null)
                    return ReadNamedMaskToken(nested, label, resolveIndex, validNames, everythingMask);
            }

            if (token is JArray array)
            {
                var mask = 0;
                foreach (var item in array)
                {
                    if (item == null || item.Type == JTokenType.Null)
                        continue;

                    var value = ReadNamedMaskToken(item, label, resolveIndex, validNames, everythingMask);
                    mask |= value;
                }

                return mask;
            }

            throw new ArgumentException($"{label} value must be an integer, 'Everything', 'Nothing', a rendering layer name, or an array of names.");
        }
    }
}
