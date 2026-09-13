using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.Legacy
{
    internal static class UniBridgeLegacySerialized
    {
        private const int MaximumArrayReadback = 128;

        internal static List<object> BuildComponentDetails(
            GameObject target,
            bool includeSerializedProperties,
            bool includeHidden,
            int maxProperties)
        {
            Component[] components = target.GetComponents<Component>();
            List<object> results = new List<object>();
            for (int index = 0; index < components.Length; index++)
            {
                Component component = components[index];
                if (component == null)
                {
                    Dictionary<string, object> missing = new Dictionary<string, object>();
                    missing["componentIndex"] = index;
                    missing["missingScript"] = true;
                    results.Add(missing);
                    continue;
                }

                Dictionary<string, object> detail = BuildComponentData(
                    component,
                    includeSerializedProperties,
                    includeHidden,
                    maxProperties);
                detail["componentIndex"] = index;
                results.Add(detail);
            }
            return results;
        }

        internal static Dictionary<string, object> BuildComponentData(
            Component component,
            bool includeSerializedProperties,
            bool includeHidden,
            int maxProperties)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["componentObjectId"] = component.GetInstanceID();
            result["type"] = component.GetType().FullName;
            result["assembly"] = component.GetType().Assembly.GetName().Name;
            result["gameObjectId"] = component.gameObject.GetInstanceID();

            Behaviour behaviour = component as Behaviour;
            if (behaviour != null)
                result["enabled"] = behaviour.enabled;

            if (includeSerializedProperties)
            {
                bool truncated;
                List<object> properties = ReadSerializedProperties(
                    component,
                    includeHidden,
                    Math.Max(1, Math.Min(maxProperties, 4096)),
                    out truncated);
                result["serializedProperties"] = properties;
                result["serializedPropertyCount"] = properties.Count;
                result["serializedPropertiesTruncated"] = truncated;
            }

            return result;
        }

        internal static Component ResolveComponent(
            GameObject target,
            Dictionary<string, object> parameters,
            bool requireUnique)
        {
            int componentObjectId = UniBridgeLegacyValue.GetInt(parameters, "ComponentObjectId", 0);
            if (componentObjectId == 0)
                componentObjectId = UniBridgeLegacyValue.GetInt(parameters, "ComponentInstanceId", 0);
            if (componentObjectId != 0)
            {
                Component byId = EditorUtility.InstanceIDToObject(componentObjectId) as Component;
                if (byId == null)
                    throw new InvalidOperationException("ComponentObjectId did not resolve to a live Component.");
                if (byId.gameObject != target)
                    throw new InvalidOperationException("ComponentObjectId belongs to a different GameObject.");
                return byId;
            }

            string componentName = UniBridgeLegacyValue.GetString(parameters, "Component", null);
            if (String.IsNullOrEmpty(componentName))
                componentName = UniBridgeLegacyValue.GetString(parameters, "ComponentName", null);
            if (String.IsNullOrEmpty(componentName))
                throw new InvalidOperationException("Component, ComponentName, or ComponentObjectId is required.");

            Component[] all = target.GetComponents<Component>();
            List<Component> matches = new List<Component>();
            for (int index = 0; index < all.Length; index++)
            {
                Component component = all[index];
                if (component == null)
                    continue;
                Type type = component.GetType();
                if (String.Equals(type.Name, componentName, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(type.FullName, componentName, StringComparison.OrdinalIgnoreCase))
                    matches.Add(component);
            }

            if (matches.Count == 0)
                throw new InvalidOperationException("Component was not found on target: " + componentName);

            int requestedIndex = UniBridgeLegacyValue.GetInt(parameters, "ComponentIndex", -1);
            if (requestedIndex >= 0)
            {
                if (requestedIndex >= matches.Count)
                    throw new InvalidOperationException(
                        "ComponentIndex " + requestedIndex + " is outside the " + matches.Count + " matching component(s).");
                return matches[requestedIndex];
            }

            if (requireUnique && matches.Count > 1)
                throw new InvalidOperationException(
                    "Component selector is ambiguous; " + matches.Count +
                    " components matched. Use ComponentObjectId or ComponentIndex.");
            return matches[0];
        }

        internal static Dictionary<string, object> ApplyProperties(
            Component component,
            Dictionary<string, object> properties,
            bool dryRun)
        {
            if (properties == null || properties.Count == 0)
                throw new InvalidOperationException("At least one serialized property is required.");

            SerializedObject serialized = new SerializedObject(component);
            serialized.Update();

            List<object> changes = new List<object>();
            List<string> propertyPaths = new List<string>();
            List<object> plannedValues = new List<object>();
            List<string> errors = new List<string>();

            foreach (KeyValuePair<string, object> entry in properties)
            {
                string propertyPath = entry.Key;
                if (String.IsNullOrEmpty(propertyPath))
                {
                    errors.Add("Serialized property path cannot be empty.");
                    continue;
                }
                if (String.Equals(propertyPath, "m_Script", StringComparison.Ordinal))
                {
                    errors.Add("m_Script cannot be changed through the legacy bridge.");
                    continue;
                }

                SerializedProperty property = serialized.FindProperty(propertyPath);
                if (property == null)
                {
                    errors.Add("Serialized property was not found: " + propertyPath);
                    continue;
                }
                if (!property.editable)
                {
                    errors.Add("Serialized property is not editable: " + propertyPath);
                    continue;
                }

                object before = ReadPropertyValue(property);
                string error;
                if (!TrySetPropertyValue(property, entry.Value, !dryRun, 0, out error))
                {
                    errors.Add(propertyPath + ": " + error);
                    continue;
                }

                object planned = dryRun ? entry.Value : ReadPropertyValue(property);
                Dictionary<string, object> change = new Dictionary<string, object>();
                change["propertyPath"] = propertyPath;
                change["propertyType"] = property.propertyType.ToString();
                change["before"] = before;
                change["requested"] = entry.Value;
                change["planned"] = planned;
                change["dryRun"] = dryRun;
                changes.Add(change);
                propertyPaths.Add(propertyPath);
                plannedValues.Add(planned);
            }

            if (errors.Count > 0)
                throw new InvalidOperationException("Serialized property validation failed: " + Join(errors));

            bool modified = false;
            bool allVerified = true;
            if (!dryRun)
            {
                Undo.RecordObject(component, "UniBridge Set Component Properties");
                modified = serialized.ApplyModifiedProperties();
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                EditorUtility.SetDirty(component);

                SerializedObject readback = new SerializedObject(component);
                readback.Update();
                for (int index = 0; index < propertyPaths.Count; index++)
                {
                    SerializedProperty actualProperty = readback.FindProperty(propertyPaths[index]);
                    object actual = actualProperty == null ? null : ReadPropertyValue(actualProperty);
                    bool verified = actualProperty != null && ValuesEquivalent(plannedValues[index], actual);
                    allVerified = allVerified && verified;
                    Dictionary<string, object> change = changes[index] as Dictionary<string, object>;
                    change["actual"] = actual;
                    change["readbackVerified"] = verified;
                }
                if (!allVerified)
                    throw new InvalidOperationException("One or more serialized property writes failed post-write readback.");
            }

            Dictionary<string, object> result = new Dictionary<string, object>();
            result["component"] = BuildComponentData(component, false, false, 1);
            result["dryRun"] = dryRun;
            result["wouldChange"] = changes.Count > 0;
            result["modified"] = modified;
            result["allVerified"] = dryRun ? (object)null : allVerified;
            result["changeCount"] = changes.Count;
            result["changes"] = changes;
            return result;
        }

        internal static Dictionary<string, object> BuildObjectReferenceInfo(UnityEngine.Object value)
        {
            if (value == null)
                return null;

            Dictionary<string, object> result = new Dictionary<string, object>();
            result["objectId"] = value.GetInstanceID();
            result["name"] = value.name;
            result["type"] = value.GetType().FullName;
            bool persistent = EditorUtility.IsPersistent(value);
            result["persistent"] = persistent;

            if (persistent)
            {
                string assetPath = AssetDatabase.GetAssetPath(value);
                result["assetPath"] = assetPath;
                result["guid"] = String.IsNullOrEmpty(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath);
                result["localFileId"] = Unsupported.GetLocalIdentifierInFile(value.GetInstanceID());
            }
            else
            {
                GameObject gameObject = value as GameObject;
                Component component = value as Component;
                if (gameObject == null && component != null)
                    gameObject = component.gameObject;
                if (gameObject != null && gameObject.scene.IsValid())
                {
                    result["sceneId"] = gameObject.scene.GetHashCode();
                    result["scenePath"] = gameObject.scene.path;
                    result["indexedHierarchyPath"] = BuildIndexedHierarchyPath(gameObject.transform);
                }
            }
            return result;
        }

        private static List<object> ReadSerializedProperties(
            Component component,
            bool includeHidden,
            int maximum,
            out bool truncated)
        {
            List<object> results = new List<object>();
            SerializedObject serialized = new SerializedObject(component);
            serialized.Update();
            SerializedProperty iterator = serialized.GetIterator();
            bool hasNext = includeHidden ? iterator.Next(true) : iterator.NextVisible(true);
            while (hasNext)
            {
                if (results.Count >= maximum)
                {
                    truncated = true;
                    return results;
                }
                results.Add(BuildPropertyData(iterator));
                hasNext = includeHidden ? iterator.Next(true) : iterator.NextVisible(true);
            }
            truncated = false;
            return results;
        }

        private static Dictionary<string, object> BuildPropertyData(SerializedProperty property)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["propertyPath"] = property.propertyPath;
            result["name"] = property.name;
            result["displayName"] = property.displayName;
            result["propertyType"] = property.propertyType.ToString();
            result["serializedType"] = property.type;
            result["depth"] = property.depth;
            result["editable"] = property.editable;
            result["isArray"] = property.isArray && property.propertyType != SerializedPropertyType.String;
            result["value"] = ReadPropertyValue(property);
            return result;
        }

        private static object ReadPropertyValue(SerializedProperty property)
        {
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                Dictionary<string, object> array = new Dictionary<string, object>();
                array["arraySize"] = property.arraySize;
                List<object> elements = new List<object>();
                int count = Math.Min(property.arraySize, MaximumArrayReadback);
                for (int index = 0; index < count; index++)
                    elements.Add(ReadPropertyValue(property.GetArrayElementAtIndex(index)));
                array["elements"] = elements;
                array["truncated"] = property.arraySize > count;
                return array;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.longValue;
                case SerializedPropertyType.Boolean: return property.boolValue;
                case SerializedPropertyType.Float: return property.doubleValue;
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Color: return Color(property.colorValue);
                case SerializedPropertyType.ObjectReference:
                    return BuildObjectReferenceInfo(property.objectReferenceValue);
                case SerializedPropertyType.LayerMask: return property.intValue;
                case SerializedPropertyType.Enum:
                {
                    Dictionary<string, object> value = new Dictionary<string, object>();
                    value["index"] = property.enumValueIndex;
                    string[] names = property.enumNames;
                    value["name"] = property.enumValueIndex >= 0 && property.enumValueIndex < names.Length
                        ? names[property.enumValueIndex]
                        : null;
                    return value;
                }
                case SerializedPropertyType.Vector2: return Vector2(property.vector2Value);
                case SerializedPropertyType.Vector3: return Vector3(property.vector3Value);
                case SerializedPropertyType.Vector4: return Vector4(property.vector4Value);
                case SerializedPropertyType.Rect: return Rect(property.rectValue);
                case SerializedPropertyType.ArraySize: return property.intValue;
                case SerializedPropertyType.Character: return property.intValue;
                case SerializedPropertyType.Bounds: return Bounds(property.boundsValue);
                case SerializedPropertyType.Quaternion: return Quaternion(property.quaternionValue);
                // Unity 5.3 has neither the enum member nor its value accessor.
#if UNITY_5_6_OR_NEWER
                case SerializedPropertyType.ExposedReference:
                    return BuildObjectReferenceInfo(property.exposedReferenceValue);
#endif
                case SerializedPropertyType.AnimationCurve:
                    return UniBridgeLegacyValue.Object("keyCount", property.animationCurveValue == null ? 0 : property.animationCurveValue.keys.Length);
                case SerializedPropertyType.Gradient:
                    return UniBridgeLegacyValue.Object("summary", "Gradient readback is not expanded by the legacy profile.");
                default:
                    return UniBridgeLegacyValue.Object("summary", "Composite property; address a serialized child property path for write access.");
            }
        }

        private static bool TrySetPropertyValue(
            SerializedProperty property,
            object value,
            bool apply,
            int depth,
            out string error)
        {
            if (depth > 32)
            {
                error = "Serialized value nesting exceeds the legacy bridge limit.";
                return false;
            }

            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                List<object> values = value as List<object>;
                if (values == null)
                {
                    error = "Array and List properties require a JSON array value.";
                    return false;
                }
                if (values.Count > 10000)
                {
                    error = "Array value exceeds the 10000-element safety limit.";
                    return false;
                }
                if (apply)
                    property.arraySize = values.Count;
                for (int index = 0; index < values.Count; index++)
                {
                    if (!apply && index >= property.arraySize)
                        continue;
                    SerializedProperty element = property.GetArrayElementAtIndex(index);
                    string elementError;
                    if (!TrySetPropertyValue(element, values[index], apply, depth + 1, out elementError))
                    {
                        error = "Element " + index + ": " + elementError;
                        return false;
                    }
                }
                error = null;
                return true;
            }

            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                    {
                        long converted = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                        if (apply) property.longValue = converted;
                        break;
                    }
                    case SerializedPropertyType.Boolean:
                    {
                        bool converted = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                        if (apply) property.boolValue = converted;
                        break;
                    }
                    case SerializedPropertyType.Float:
                    {
                        double converted = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                        if (apply) property.doubleValue = converted;
                        break;
                    }
                    case SerializedPropertyType.String:
                        if (apply) property.stringValue = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                        break;
                    case SerializedPropertyType.Color:
                    {
                        UnityEngine.Color converted = ReadColor(value, property.colorValue);
                        if (apply) property.colorValue = converted;
                        break;
                    }
                    case SerializedPropertyType.ObjectReference:
                    {
                        UnityEngine.Object converted = ResolveObjectReference(value);
                        if (apply) property.objectReferenceValue = converted;
                        break;
                    }
                    case SerializedPropertyType.LayerMask:
                    case SerializedPropertyType.ArraySize:
                    case SerializedPropertyType.Character:
                    {
                        int converted = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        if (apply) property.intValue = converted;
                        break;
                    }
                    case SerializedPropertyType.Enum:
                    {
                        int converted = ResolveEnumIndex(property, value);
                        if (apply) property.enumValueIndex = converted;
                        break;
                    }
                    case SerializedPropertyType.Vector2:
                    {
                        UnityEngine.Vector2 converted = ReadVector2(value, property.vector2Value);
                        if (apply) property.vector2Value = converted;
                        break;
                    }
                    case SerializedPropertyType.Vector3:
                    {
                        UnityEngine.Vector3 converted = ReadVector3(value, property.vector3Value);
                        if (apply) property.vector3Value = converted;
                        break;
                    }
                    case SerializedPropertyType.Vector4:
                    {
                        UnityEngine.Vector4 converted = ReadVector4(value, property.vector4Value);
                        if (apply) property.vector4Value = converted;
                        break;
                    }
                    case SerializedPropertyType.Rect:
                    {
                        UnityEngine.Rect converted = ReadRect(value, property.rectValue);
                        if (apply) property.rectValue = converted;
                        break;
                    }
                    case SerializedPropertyType.Bounds:
                    {
                        UnityEngine.Bounds converted = ReadBounds(value, property.boundsValue);
                        if (apply) property.boundsValue = converted;
                        break;
                    }
                    case SerializedPropertyType.Quaternion:
                    {
                        UnityEngine.Quaternion converted = ReadQuaternion(value, property.quaternionValue);
                        if (apply) property.quaternionValue = converted;
                        break;
                    }
#if UNITY_5_6_OR_NEWER
                    case SerializedPropertyType.ExposedReference:
                    {
                        UnityEngine.Object converted = ResolveObjectReference(value);
                        if (apply) property.exposedReferenceValue = converted;
                        break;
                    }
#endif
                    default:
                        error = "Property type " + property.propertyType +
                                " is read-only in this legacy profile. Address supported serialized leaf properties instead.";
                        return false;
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            error = null;
            return true;
        }

        private static UnityEngine.Object ResolveObjectReference(object value)
        {
            if (value == null)
                return null;

            Dictionary<string, object> reference = value as Dictionary<string, object>;
            if (reference == null)
            {
                int directId;
                try { directId = Convert.ToInt32(value, CultureInfo.InvariantCulture); }
                catch { throw new InvalidOperationException("Unity object references require ObjectId or GUID/localFileId identity."); }
                UnityEngine.Object direct = EditorUtility.InstanceIDToObject(directId);
                if (direct == null)
                    throw new InvalidOperationException("ObjectId did not resolve to a live Unity object: " + directId);
                return direct;
            }

            object idValue = Get(reference, "ObjectId", "objectId", "InstanceId", "instanceId", "id");
            if (idValue != null)
            {
                int objectId = Convert.ToInt32(idValue, CultureInfo.InvariantCulture);
                UnityEngine.Object byId = EditorUtility.InstanceIDToObject(objectId);
                if (byId == null)
                    throw new InvalidOperationException("ObjectId did not resolve to a live Unity object: " + objectId);
                return byId;
            }

            object guidValue = Get(reference, "Guid", "guid", "AssetGuid", "assetGuid");
            string guid = guidValue == null ? null : Convert.ToString(guidValue, CultureInfo.InvariantCulture);
            if (String.IsNullOrEmpty(guid))
                throw new InvalidOperationException("Unity object reference requires ObjectId or asset Guid identity.");

            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (String.IsNullOrEmpty(path))
                throw new InvalidOperationException("Asset GUID was not found: " + guid);

            object localIdValue = Get(reference, "LocalFileId", "localFileId", "FileId", "fileId");
            if (localIdValue == null)
            {
                UnityEngine.Object main = AssetDatabase.LoadMainAssetAtPath(path);
                if (main == null)
                    throw new InvalidOperationException("Asset GUID resolved to an unreadable main asset: " + guid);
                return main;
            }

            long requestedLocalId = Convert.ToInt64(localIdValue, CultureInfo.InvariantCulture);
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            for (int index = 0; index < assets.Length; index++)
            {
                UnityEngine.Object asset = assets[index];
                if (asset != null && Unsupported.GetLocalIdentifierInFile(asset.GetInstanceID()) == requestedLocalId)
                    return asset;
            }
            throw new InvalidOperationException(
                "Asset GUID resolved, but localFileId " + requestedLocalId + " was not found: " + guid);
        }

        private static int ResolveEnumIndex(SerializedProperty property, object value)
        {
            if (value is string)
            {
                string requested = (string)value;
                string[] names = property.enumNames;
                string[] displayNames = property.enumDisplayNames;
                for (int index = 0; index < names.Length; index++)
                {
                    if (String.Equals(names[index], requested, StringComparison.OrdinalIgnoreCase) ||
                        (index < displayNames.Length && String.Equals(displayNames[index], requested, StringComparison.OrdinalIgnoreCase)))
                        return index;
                }
                throw new InvalidOperationException("Enum value was not found: " + requested);
            }

            int numeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (numeric < 0 || numeric >= property.enumNames.Length)
                throw new InvalidOperationException("Enum index is outside the available range: " + numeric);
            return numeric;
        }

        private static bool ValuesEquivalent(object expected, object actual)
        {
            if (expected == null || actual == null)
                return expected == null && actual == null;

            Dictionary<string, object> expectedObject = expected as Dictionary<string, object>;
            Dictionary<string, object> actualObject = actual as Dictionary<string, object>;
            if (expectedObject != null || actualObject != null)
            {
                if (expectedObject == null || actualObject == null || expectedObject.Count != actualObject.Count)
                    return false;
                foreach (KeyValuePair<string, object> entry in expectedObject)
                {
                    object actualValue;
                    if (!actualObject.TryGetValue(entry.Key, out actualValue) || !ValuesEquivalent(entry.Value, actualValue))
                        return false;
                }
                return true;
            }

            List<object> expectedArray = expected as List<object>;
            List<object> actualArray = actual as List<object>;
            if (expectedArray != null || actualArray != null)
            {
                if (expectedArray == null || actualArray == null || expectedArray.Count != actualArray.Count)
                    return false;
                for (int index = 0; index < expectedArray.Count; index++)
                {
                    if (!ValuesEquivalent(expectedArray[index], actualArray[index]))
                        return false;
                }
                return true;
            }

            if (IsNumber(expected) && IsNumber(actual))
            {
                double left = Convert.ToDouble(expected, CultureInfo.InvariantCulture);
                double right = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
                double scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
                return Math.Abs(left - right) <= 0.000001 * scale;
            }
            return String.Equals(
                Convert.ToString(expected, CultureInfo.InvariantCulture),
                Convert.ToString(actual, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        private static bool IsNumber(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort ||
                   value is int || value is uint || value is long || value is ulong ||
                   value is float || value is double || value is decimal;
        }

        private static object Get(Dictionary<string, object> source, params string[] keys)
        {
            for (int index = 0; index < keys.Length; index++)
            {
                object value;
                if (source.TryGetValue(keys[index], out value))
                    return value;
            }
            return null;
        }

        private static float ReadNumber(Dictionary<string, object> source, string key, float fallback)
        {
            object value;
            if (!source.TryGetValue(key, out value) || value == null)
                return fallback;
            return Convert.ToSingle(value, CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, object> RequireObject(object value, string expected)
        {
            Dictionary<string, object> result = value as Dictionary<string, object>;
            if (result == null)
                throw new InvalidOperationException(expected + " requires a JSON object value.");
            return result;
        }

        private static UnityEngine.Color ReadColor(object value, UnityEngine.Color fallback)
        {
            Dictionary<string, object> source = RequireObject(value, "Color");
            return new UnityEngine.Color(
                ReadNumber(source, "r", fallback.r),
                ReadNumber(source, "g", fallback.g),
                ReadNumber(source, "b", fallback.b),
                ReadNumber(source, "a", fallback.a));
        }

        private static UnityEngine.Vector2 ReadVector2(object value, UnityEngine.Vector2 fallback)
        {
            Dictionary<string, object> source = RequireObject(value, "Vector2");
            return new UnityEngine.Vector2(ReadNumber(source, "x", fallback.x), ReadNumber(source, "y", fallback.y));
        }

        private static UnityEngine.Vector3 ReadVector3(object value, UnityEngine.Vector3 fallback)
        {
            Dictionary<string, object> source = RequireObject(value, "Vector3");
            return new UnityEngine.Vector3(
                ReadNumber(source, "x", fallback.x),
                ReadNumber(source, "y", fallback.y),
                ReadNumber(source, "z", fallback.z));
        }

        private static UnityEngine.Vector4 ReadVector4(object value, UnityEngine.Vector4 fallback)
        {
            Dictionary<string, object> source = RequireObject(value, "Vector4");
            return new UnityEngine.Vector4(
                ReadNumber(source, "x", fallback.x),
                ReadNumber(source, "y", fallback.y),
                ReadNumber(source, "z", fallback.z),
                ReadNumber(source, "w", fallback.w));
        }

        private static UnityEngine.Rect ReadRect(object value, UnityEngine.Rect fallback)
        {
            Dictionary<string, object> source = RequireObject(value, "Rect");
            return new UnityEngine.Rect(
                ReadNumber(source, "x", fallback.x),
                ReadNumber(source, "y", fallback.y),
                ReadNumber(source, "width", fallback.width),
                ReadNumber(source, "height", fallback.height));
        }

        private static UnityEngine.Bounds ReadBounds(object value, UnityEngine.Bounds fallback)
        {
            Dictionary<string, object> source = RequireObject(value, "Bounds");
            object centerValue = Get(source, "center", "Center");
            object extentsValue = Get(source, "extents", "Extents");
            UnityEngine.Vector3 center = centerValue == null ? fallback.center : ReadVector3(centerValue, fallback.center);
            UnityEngine.Vector3 extents = extentsValue == null ? fallback.extents : ReadVector3(extentsValue, fallback.extents);
            return new UnityEngine.Bounds(center, extents * 2.0f);
        }

        private static UnityEngine.Quaternion ReadQuaternion(object value, UnityEngine.Quaternion fallback)
        {
            Dictionary<string, object> source = RequireObject(value, "Quaternion");
            return new UnityEngine.Quaternion(
                ReadNumber(source, "x", fallback.x),
                ReadNumber(source, "y", fallback.y),
                ReadNumber(source, "z", fallback.z),
                ReadNumber(source, "w", fallback.w));
        }

        private static Dictionary<string, object> Color(UnityEngine.Color value)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["r"] = value.r; result["g"] = value.g; result["b"] = value.b; result["a"] = value.a;
            return result;
        }

        private static Dictionary<string, object> Vector2(UnityEngine.Vector2 value)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["x"] = value.x; result["y"] = value.y;
            return result;
        }

        private static Dictionary<string, object> Vector3(UnityEngine.Vector3 value)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["x"] = value.x; result["y"] = value.y; result["z"] = value.z;
            return result;
        }

        private static Dictionary<string, object> Vector4(UnityEngine.Vector4 value)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["x"] = value.x; result["y"] = value.y; result["z"] = value.z; result["w"] = value.w;
            return result;
        }

        private static Dictionary<string, object> Rect(UnityEngine.Rect value)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["x"] = value.x; result["y"] = value.y; result["width"] = value.width; result["height"] = value.height;
            return result;
        }

        private static Dictionary<string, object> Bounds(UnityEngine.Bounds value)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["center"] = Vector3(value.center);
            result["extents"] = Vector3(value.extents);
            return result;
        }

        private static Dictionary<string, object> Quaternion(UnityEngine.Quaternion value)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["x"] = value.x; result["y"] = value.y; result["z"] = value.z; result["w"] = value.w;
            return result;
        }

        private static string BuildIndexedHierarchyPath(Transform transform)
        {
            string path = "/" + transform.name + "[" + transform.GetSiblingIndex() + "]";
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = "/" + transform.name + "[" + transform.GetSiblingIndex() + "]" + path;
            }
            return path;
        }

        private static string Join(List<string> values)
        {
            return String.Join(" | ", values.ToArray());
        }
    }
}
