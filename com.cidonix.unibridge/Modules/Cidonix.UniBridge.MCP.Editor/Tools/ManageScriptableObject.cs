#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Creates, inspects, validates, and updates ScriptableObject assets using SerializedObject-aware patches.
    /// </summary>
    public static class ManageScriptableObject
    {
        const int DefaultSerializedPropertyLimit = 200;
        const int MaxSerializedPropertyLimit = 2000;
        const int DefaultTypeLimit = 100;
        const int MaxTypeLimit = 1000;

        public const string Title = "Manage ScriptableObjects";

        public const string Description = @"Inspect, validate, create, or update Unity ScriptableObject assets.

Use this when an agent needs to create project data/config assets after compiling a ScriptableObject type, or update serialized fields on an existing .asset file. This is inspired by the createOrUpdateScriptableObject action, but uses UniBridge MCP-native dry-run validation and bounded serialized snapshots.

Args:
    Action: Inspect, Validate, CreateOrUpdate, SetProperties, or ListTypes.
    Path/Guid: ScriptableObject asset to inspect or update.
    ScriptableObjectType: Short, full, or assembly-qualified type name. Required when creating a missing asset.
    Properties: Field/property patch. Public fields/properties and Unity serialized fields are supported.
    ScriptableObject: Optional structured shape { ""scriptableObjectType"": ""Namespace.Type"", ""props"": { ... } }.
    DryRun: Preview mutating actions without changing assets.

Returns:
    success, message, and ScriptableObject snapshots with type info, serialized properties, planned/applied changes, warnings, errors, and dry-run status.";

        [McpTool("UniBridge_ManageScriptableObject", Description, Title, Groups = new[] { "core", "assets" }, EnabledByDefault = true)]
        public static object HandleCommand(ManageScriptableObjectParams parameters)
        {
            parameters ??= new ManageScriptableObjectParams();

            try
            {
                switch (parameters.Action)
                {
                    case ScriptableObjectAction.Inspect:
                        return Inspect(parameters);
                    case ScriptableObjectAction.Validate:
                        return Validate(parameters);
                    case ScriptableObjectAction.CreateOrUpdate:
                        return CreateOrUpdate(parameters);
                    case ScriptableObjectAction.SetProperties:
                        return SetProperties(parameters);
                    case ScriptableObjectAction.ListTypes:
                        return ListTypes(parameters);
                    default:
                        return Response.Error($"Unsupported ScriptableObject action '{parameters.Action}'.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageScriptableObject] {parameters.Action} failed for '{parameters.Path ?? parameters.Guid}': {ex}");
                return Response.Error($"ScriptableObject action '{parameters.Action}' failed: {ex.Message}");
            }
        }

        static object Inspect(ManageScriptableObjectParams parameters)
        {
            var resolved = ResolveAsset(parameters, allowMissing: false, appendExtension: false);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            return Response.Success(
                $"Inspected ScriptableObject '{resolved.AssetPath}'.",
                BuildResponseData(resolved.AssetPath, resolved.Asset, parameters, Array.Empty<PropertyChange>(), Array.Empty<string>(), Array.Empty<string>(), false, false));
        }

        static object Validate(ManageScriptableObjectParams parameters)
        {
            var resolved = ResolveAsset(parameters, allowMissing: true, appendExtension: false);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            var props = ExtractProperties(parameters);
            var requestedTypeName = ExtractTypeName(parameters);
            var typeResult = ResolveTargetType(requestedTypeName, resolved.Asset);
            if (!typeResult.Success)
                return Response.Error(typeResult.Error);

            if (resolved.Asset == null && typeResult.Type == null && props != null && props.HasValues)
                return Response.Error("Validate with Properties requires an existing ScriptableObject asset or ScriptableObjectType.");

            if (resolved.Asset != null && typeResult.Type != null && resolved.Asset.GetType() != typeResult.Type)
            {
                return Response.Error(
                    $"Type mismatch: existing asset is '{resolved.Asset.GetType().FullName}', requested '{typeResult.Type.FullName}'.");
            }

            ScriptableObject temp = null;
            ScriptableObject target = resolved.Asset;
            try
            {
                if (target == null && typeResult.Type != null)
                {
                    temp = ScriptableObject.CreateInstance(typeResult.Type);
                    target = temp;
                }

                var validation = ValidateProperties(target, props);
                var success = validation.Errors.Count == 0;
                var data = BuildResponseData(
                    resolved.AssetPath,
                    target,
                    parameters,
                    validation.Changes,
                    validation.Warnings.Concat(typeResult.Warnings).ToArray(),
                    validation.Errors.ToArray(),
                    true,
                    false);

                return success
                    ? Response.Success("ScriptableObject validation passed.", data)
                    : Response.Error($"ScriptableObject validation found {validation.Errors.Count} issue(s).", data);
            }
            finally
            {
                if (temp != null)
                    Object.DestroyImmediate(temp);
            }
        }

        static object CreateOrUpdate(ManageScriptableObjectParams parameters)
        {
            var resolved = ResolveAsset(parameters, allowMissing: true, appendExtension: true);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            if (string.IsNullOrWhiteSpace(resolved.AssetPath))
                return Response.Error("CreateOrUpdate requires Path or Guid.");

            if (!CanMutate(resolved.AssetPath, parameters, out var mutationError))
                return Response.Error(mutationError);

            var props = ExtractProperties(parameters);
            var requestedTypeName = ExtractTypeName(parameters);
            var typeResult = ResolveTargetType(requestedTypeName, resolved.Asset);
            if (!typeResult.Success)
                return Response.Error(typeResult.Error);

            if (resolved.Asset == null && typeResult.Type == null)
                return Response.Error("CreateOrUpdate requires ScriptableObjectType when the asset does not exist.");

            if (resolved.Asset != null && typeResult.Type != null && resolved.Asset.GetType() != typeResult.Type)
            {
                return Response.Error(
                    $"Cannot update '{resolved.AssetPath}': existing asset is '{resolved.Asset.GetType().FullName}', requested '{typeResult.Type.FullName}'.");
            }

            var validationTarget = resolved.Asset;
            ScriptableObject temp = null;
            try
            {
                if (validationTarget == null)
                {
                    temp = ScriptableObject.CreateInstance(typeResult.Type);
                    validationTarget = temp;
                }

                var validation = ValidateProperties(validationTarget, props);
                if (validation.Errors.Count > 0)
                {
                    return Response.Error($"ScriptableObject validation found {validation.Errors.Count} issue(s).", new
                    {
                        path = resolved.AssetPath,
                        type = validationTarget.GetType().FullName,
                        dryRun = true,
                        changes = validation.Changes,
                        warnings = validation.Warnings.Concat(typeResult.Warnings).ToArray(),
                        errors = validation.Errors
                    });
                }

                if (parameters.DryRun)
                {
                    var dryData = BuildResponseData(
                        resolved.AssetPath,
                        validationTarget,
                        parameters,
                        validation.Changes,
                        validation.Warnings.Concat(typeResult.Warnings).ToArray(),
                        Array.Empty<string>(),
                        true,
                        resolved.Asset == null);
                    return Response.Success("Dry run: ScriptableObject would be created or updated.", dryData);
                }
            }
            finally
            {
                if (temp != null)
                    Object.DestroyImmediate(temp);
            }

            var created = false;
            var asset = resolved.Asset;
            if (asset == null)
            {
                EnsureDirectoryExists(Path.GetDirectoryName(resolved.AssetPath));
                asset = ScriptableObject.CreateInstance(typeResult.Type);
                if (asset == null)
                    return Response.Error($"Failed to create ScriptableObject instance of type '{typeResult.Type.FullName}'.");

                AssetDatabase.CreateAsset(asset, resolved.AssetPath);
                created = true;
            }
            else
            {
                Undo.RecordObject(asset, "Update ScriptableObject");
            }

            var applied = ApplyProperties(asset, props, dryRun: false);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(resolved.AssetPath, ImportAssetOptions.ForceUpdate);

            if (parameters.Select)
                Selection.activeObject = asset;

            return Response.Success(
                created ? $"Created ScriptableObject '{resolved.AssetPath}'." : $"Updated ScriptableObject '{resolved.AssetPath}'.",
                BuildResponseData(
                    resolved.AssetPath,
                    asset,
                    parameters,
                    applied.Changes,
                    applied.Warnings.Concat(typeResult.Warnings).ToArray(),
                    applied.Errors.ToArray(),
                    false,
                    created));
        }

        static object SetProperties(ManageScriptableObjectParams parameters)
        {
            var resolved = ResolveAsset(parameters, allowMissing: false, appendExtension: false);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            if (!CanMutate(resolved.AssetPath, parameters, out var mutationError))
                return Response.Error(mutationError);

            var props = ExtractProperties(parameters);
            if (props == null || !props.HasValues)
                return Response.Error("SetProperties requires non-empty Properties or ScriptableObject.props.");

            var validation = ValidateProperties(resolved.Asset, props);
            if (validation.Errors.Count > 0)
            {
                return Response.Error($"ScriptableObject validation found {validation.Errors.Count} issue(s).", new
                {
                    path = resolved.AssetPath,
                    type = resolved.Asset.GetType().FullName,
                    dryRun = true,
                    changes = validation.Changes,
                    warnings = validation.Warnings,
                    errors = validation.Errors
                });
            }

            if (parameters.DryRun)
            {
                return Response.Success(
                    "Dry run: ScriptableObject properties would be updated.",
                    BuildResponseData(resolved.AssetPath, resolved.Asset, parameters, validation.Changes, validation.Warnings.ToArray(), Array.Empty<string>(), true, false));
            }

            Undo.RecordObject(resolved.Asset, "Update ScriptableObject");
            var applied = ApplyProperties(resolved.Asset, props, dryRun: false);
            EditorUtility.SetDirty(resolved.Asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(resolved.AssetPath, ImportAssetOptions.ForceUpdate);

            if (parameters.Select)
                Selection.activeObject = resolved.Asset;

            return Response.Success(
                $"Updated ScriptableObject '{resolved.AssetPath}'.",
                BuildResponseData(resolved.AssetPath, resolved.Asset, parameters, applied.Changes, applied.Warnings.ToArray(), applied.Errors.ToArray(), false, false));
        }

        static object ListTypes(ManageScriptableObjectParams parameters)
        {
            var query = parameters.Query?.Trim();
            var limit = Mathf.Clamp(parameters.Limit <= 0 ? DefaultTypeLimit : parameters.Limit, 1, MaxTypeLimit);

            var types = GetScriptableObjectTypes()
                .Select(BuildTypeInfo)
                .Where(info => MatchesTypeQuery(info, query))
                .OrderBy(info => info.fullName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray();

            return Response.Success($"Found {types.Length} ScriptableObject type(s).", new
            {
                query,
                limit,
                types,
                truncated = types.Length >= limit
            });
        }

        static PropertyValidation ValidateProperties(ScriptableObject target, JObject properties)
        {
            if (target == null)
            {
                return new PropertyValidation
                {
                    Errors = { "No ScriptableObject target is available for property validation." }
                };
            }

            return ApplyProperties(target, properties, dryRun: true);
        }

        static PropertyValidation ApplyProperties(ScriptableObject target, JObject properties, bool dryRun)
        {
            var result = new PropertyValidation();
            if (properties == null || !properties.HasValues)
                return result;

            using var serializedObject = new SerializedObject(target);
            serializedObject.Update();

            foreach (var property in properties.Properties())
            {
                if (IsControlProperty(property.Name))
                    continue;

                if (TryApplySerializedProperty(target, serializedObject, property.Name, property.Value, dryRun, result))
                    continue;

                if (TryApplyReflectionMember(target, property.Name, property.Value, dryRun, result))
                    continue;

                result.Errors.Add($"Property '{property.Name}' was not found on '{target.GetType().FullName}'. Use Inspect/ListTypes to verify serialized field names.");
            }

            if (!dryRun)
                serializedObject.ApplyModifiedProperties();

            return result;
        }

        static bool TryApplySerializedProperty(ScriptableObject target, SerializedObject serializedObject, string nameOrPath, JToken value, bool dryRun, PropertyValidation result)
        {
            var patch = SerializedPropertyPatcher.TryApplyProperty(target, serializedObject, nameOrPath, value, dryRun);
            if (!patch.Found)
            {
                return false;
            }

            if (!patch.Success)
            {
                result.Errors.Add(patch.Error);
                return true;
            }

            foreach (var change in patch.Changes)
            {
                result.Changes.Add(new PropertyChange(
                    change.propertyPath,
                    change.propertyType,
                    change.before,
                    change.after,
                    change.dryRun));
            }

            return true;
        }

        static bool TryApplyReflectionMember(ScriptableObject target, string memberName, JToken value, bool dryRun, PropertyValidation result)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = target.GetType();

            var property = type.GetProperty(memberName, flags);
            if (property != null)
            {
                if (!property.CanWrite)
                {
                    result.Errors.Add($"Property '{memberName}' exists on '{type.FullName}' but is read-only.");
                    return true;
                }

                var before = property.GetValue(target);
                var converted = ConvertToken(value, property.PropertyType);
                if (!dryRun)
                    property.SetValue(target, converted);

                result.Changes.Add(new PropertyChange(property.Name, property.PropertyType.FullName ?? property.PropertyType.Name, SerializeValue(before), SerializeValue(converted), dryRun));
                return true;
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                if (field.IsInitOnly || field.IsLiteral)
                {
                    result.Errors.Add($"Field '{memberName}' exists on '{type.FullName}' but is read-only.");
                    return true;
                }

                var before = field.GetValue(target);
                var converted = ConvertToken(value, field.FieldType);
                if (!dryRun)
                    field.SetValue(target, converted);

                result.Changes.Add(new PropertyChange(field.Name, field.FieldType.FullName ?? field.FieldType.Name, SerializeValue(before), SerializeValue(converted), dryRun));
                return true;
            }

            return false;
        }

        static object BuildResponseData(string assetPath, ScriptableObject asset, ManageScriptableObjectParams parameters, IReadOnlyList<PropertyChange> changes, IReadOnlyList<string> warnings, IReadOnlyList<string> errors, bool dryRun, bool created)
        {
            return new
            {
                path = assetPath,
                guid = !string.IsNullOrWhiteSpace(assetPath) ? AssetDatabase.AssetPathToGUID(assetPath) : null,
                dryRun,
                created,
                scriptableObject = asset != null ? BuildScriptableObjectInfo(asset, assetPath, parameters) : null,
                changes,
                warnings,
                errors,
                summary = new
                {
                    changeCount = changes?.Count ?? 0,
                    warningCount = warnings?.Count ?? 0,
                    errorCount = errors?.Count ?? 0
                }
            };
        }

        static object BuildScriptableObjectInfo(ScriptableObject asset, string assetPath, ManageScriptableObjectParams parameters)
        {
            var type = asset.GetType();
            return new
            {
                name = asset.name,
                path = assetPath,
                guid = !string.IsNullOrWhiteSpace(assetPath) ? AssetDatabase.AssetPathToGUID(assetPath) : null,
                type = BuildTypeInfo(type),
                entityId = UnityApiAdapter.GetObjectId(asset),
                serializedProperties = parameters.IncludeSerializedProperties ? SerializeProperties(asset, parameters) : null
            };
        }

        static TypeInfoDto BuildTypeInfo(Type type)
        {
            var createMenu = type.GetCustomAttribute<CreateAssetMenuAttribute>();
            return new TypeInfoDto
            {
                name = type.Name,
                fullName = type.FullName ?? type.Name,
                @namespace = type.Namespace,
                assembly = type.Assembly.GetName().Name,
                isAbstract = type.IsAbstract,
                createAssetMenu = createMenu != null
                    ? new CreateAssetMenuDto
                    {
                        menuName = createMenu.menuName,
                        fileName = createMenu.fileName,
                        order = createMenu.order
                    }
                    : null
            };
        }

        static object SerializeProperties(ScriptableObject asset, ManageScriptableObjectParams parameters)
        {
            var limit = Mathf.Clamp(
                parameters.MaxSerializedProperties <= 0 ? DefaultSerializedPropertyLimit : parameters.MaxSerializedProperties,
                1,
                MaxSerializedPropertyLimit);
            var properties = new List<object>();
            var truncated = false;

            using var serializedObject = new SerializedObject(asset);
            var iterator = serializedObject.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    properties.Add(new
                    {
                        path = iterator.propertyPath,
                        name = iterator.name,
                        displayName = iterator.displayName,
                        type = iterator.propertyType.ToString(),
                        value = SerializePropertyValue(iterator)
                    });

                    if (properties.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }
                }
                while (iterator.NextVisible(false));
            }

            return new
            {
                count = properties.Count,
                truncated,
                properties
            };
        }

        static AssetResolution ResolveAsset(ManageScriptableObjectParams parameters, bool allowMissing, bool appendExtension)
        {
            var path = parameters.Path;
            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(parameters.Guid))
            {
                path = AssetDatabase.GUIDToAssetPath(parameters.Guid);
                if (string.IsNullOrWhiteSpace(path))
                    return AssetResolution.Fail($"Asset GUID '{parameters.Guid}' was not found.");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                if (allowMissing)
                    return AssetResolution.Ok(null, null);

                return AssetResolution.Fail("Path or Guid is required.");
            }

            if (!TryNormalizeAssetPath(path, appendExtension, out var assetPath, out var error))
                return AssetResolution.Fail(error);

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            var existing = AssetDatabase.LoadMainAssetAtPath(assetPath);

            if (existing != null && asset == null)
                return AssetResolution.Fail($"Asset at '{assetPath}' is '{existing.GetType().FullName}', not a ScriptableObject.");

            if (asset == null && !allowMissing)
                return AssetResolution.Fail($"ScriptableObject asset not found at '{assetPath}'.");

            return AssetResolution.Ok(assetPath, asset);
        }

        static TypeResolution ResolveTargetType(string requestedTypeName, ScriptableObject existingAsset)
        {
            if (!string.IsNullOrWhiteSpace(requestedTypeName))
            {
                if (TryResolveScriptableObjectType(requestedTypeName, out var resolved, out var error))
                    return TypeResolution.Ok(resolved);

                return TypeResolution.Fail(error);
            }

            return TypeResolution.Ok(existingAsset != null ? existingAsset.GetType() : null);
        }

        static bool TryResolveScriptableObjectType(string nameOrFullName, out Type type, out string error)
        {
            type = null;
            error = null;

            if (string.IsNullOrWhiteSpace(nameOrFullName))
            {
                error = "ScriptableObject type name is empty.";
                return false;
            }

            var direct = Type.GetType(nameOrFullName, throwOnError: false);
            if (IsValidScriptableObjectType(direct))
            {
                type = direct;
                return true;
            }

            var candidates = GetScriptableObjectTypes()
                .Where(t => string.Equals(t.FullName, nameOrFullName, StringComparison.Ordinal) ||
                            string.Equals(t.Name, nameOrFullName, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 1)
            {
                type = candidates[0];
                return true;
            }

            if (candidates.Count > 1)
            {
                error = $"Multiple ScriptableObject types matched '{nameOrFullName}': " +
                        string.Join(", ", candidates.Select(t => $"{t.FullName} ({t.Assembly.GetName().Name})")) +
                        ". Use a fully-qualified type name.";
                return false;
            }

            error = $"ScriptableObject type '{nameOrFullName}' was not found. Ensure the script compiled, then use ListTypes or ScriptIntelligence.";
            return false;
        }

        static IEnumerable<Type> GetScriptableObjectTypes()
        {
#if UNITY_EDITOR
            return TypeCache.GetTypesDerivedFrom<ScriptableObject>()
                .Where(IsValidScriptableObjectType);
#else
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .Where(IsValidScriptableObjectType);
#endif
        }

        static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
        }

        static bool IsValidScriptableObjectType(Type type)
        {
            return type != null &&
                   type.IsClass &&
                   !type.IsAbstract &&
                   !type.ContainsGenericParameters &&
                   typeof(ScriptableObject).IsAssignableFrom(type);
        }

        static bool TryNormalizeAssetPath(string path, bool appendExtension, out string normalizedPath, out string error)
        {
            normalizedPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Asset path is empty.";
                return false;
            }

            var candidate = path.Replace('\\', '/').Trim();
            if (candidate.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                candidate = candidate.Substring("unity://path/".Length);

            if (candidate.Contains("../", StringComparison.Ordinal) ||
                candidate.Contains("/..", StringComparison.Ordinal) ||
                candidate.Contains(":", StringComparison.Ordinal) ||
                Path.IsPathRooted(candidate))
            {
                error = $"Asset path must not contain traversal or absolute roots: '{path}'.";
                return false;
            }

            if (!candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "Assets/" + candidate.TrimStart('/');
            }

            if (appendExtension && string.IsNullOrEmpty(Path.GetExtension(candidate)))
                candidate += ".asset";

            normalizedPath = candidate;
            return true;
        }

        static bool CanMutate(string assetPath, ManageScriptableObjectParams parameters, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                error = "A ScriptableObject asset path is required for mutation.";
                return false;
            }

            if (assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) && !parameters.AllowPackages)
            {
                error = $"Refusing to mutate package asset '{assetPath}'. Pass AllowPackages=true only for embedded package assets you intentionally own.";
                return false;
            }

            if (!parameters.DryRun)
            {
                try
                {
                    VersionControlUtility.EnsureAssetEditable(assetPath, checkout: true, throwOnBlocked: true);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            return true;
        }

        static void EnsureDirectoryExists(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                return;

            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), directoryPath);
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
                AssetDatabase.Refresh();
            }
        }

        static string ExtractTypeName(ManageScriptableObjectParams parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameters.ScriptableObjectType))
                return parameters.ScriptableObjectType.Trim();

            var referenceShape = parameters.ScriptableObject;
            var fromReferenceShape = referenceShape?["scriptableObjectType"]?.ToString()
                ?? referenceShape?["type"]?.ToString()
                ?? referenceShape?["scriptClass"]?.ToString();
            if (!string.IsNullOrWhiteSpace(fromReferenceShape))
                return fromReferenceShape.Trim();

            var props = parameters.Properties;
            var fromProps = props?["scriptableObjectType"]?.ToString()
                ?? props?["type"]?.ToString()
                ?? props?["scriptClass"]?.ToString();
            return string.IsNullOrWhiteSpace(fromProps) ? null : fromProps.Trim();
        }

        static JObject ExtractProperties(ManageScriptableObjectParams parameters)
        {
            if (parameters.ScriptableObject?["props"] is JObject referenceShapeProps)
                return referenceShapeProps;

            if (parameters.Properties?["props"] is JObject nestedProps)
                return nestedProps;

            return parameters.Properties;
        }

        static bool IsControlProperty(string name)
        {
            return string.Equals(name, "scriptableObjectType", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "scriptClass", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "type", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "props", StringComparison.OrdinalIgnoreCase);
        }

        static bool MatchesTypeQuery(TypeInfoDto info, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;

            return Contains(info.name, query) ||
                   Contains(info.fullName, query) ||
                   Contains(info.@namespace, query) ||
                   Contains(info.assembly, query) ||
                   Contains(info.createAssetMenu?.menuName, query) ||
                   Contains(info.createAssetMenu?.fileName, query);
        }

        static bool Contains(string value, string query)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static int ReadInt(JToken token)
        {
            if (token.Type == JTokenType.String && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return token.ToObject<int>();
        }

        static float ReadFloat(JToken token)
        {
            if (token.Type == JTokenType.String && float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return token.ToObject<float>();
        }

        static bool ReadBool(JToken token)
        {
            if (token.Type == JTokenType.String && bool.TryParse(token.ToString(), out var parsed))
                return parsed;
            return token.ToObject<bool>();
        }

        static char ReadCharacter(JToken token)
        {
            var text = token.ToString();
            return string.IsNullOrEmpty(text) ? '\0' : text[0];
        }

        static int ReadEnumIndex(SerializedProperty property, JToken token)
        {
            if (token.Type == JTokenType.Integer)
            {
                var index = token.ToObject<int>();
                if (index < 0 || index >= property.enumNames.Length)
                    throw new ArgumentOutOfRangeException(nameof(token), $"Enum index {index} is outside 0..{property.enumNames.Length - 1}.");
                return index;
            }

            var text = token.ToString();
            for (var i = 0; i < property.enumNames.Length; i++)
            {
                if (string.Equals(property.enumNames[i], text, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.enumDisplayNames[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            throw new ArgumentException($"Enum value '{text}' was not found. Valid values: {string.Join(", ", property.enumNames)}.");
        }

        static Color ReadColor(JToken token)
        {
            if (token.Type == JTokenType.String)
            {
                var text = token.ToString();
                if (ColorUtility.TryParseHtmlString(text, out var parsed))
                    return parsed;
                throw new ArgumentException($"Color string '{text}' must be #RGB, #RRGGBB, or #RRGGBBAA.");
            }

            if (token is JArray array)
            {
                if (array.Count < 3)
                    throw new ArgumentException("Color array requires at least [r,g,b].");
                return new Color(ReadFloat(array[0]), ReadFloat(array[1]), ReadFloat(array[2]), array.Count > 3 ? ReadFloat(array[3]) : 1f);
            }

            return new Color(
                ReadFloat(token["r"] ?? token["R"] ?? throw new ArgumentException("Color object requires r/g/b.")),
                ReadFloat(token["g"] ?? token["G"]),
                ReadFloat(token["b"] ?? token["B"]),
                token["a"] != null || token["A"] != null ? ReadFloat(token["a"] ?? token["A"]) : 1f);
        }

        static Vector2 ReadVector2(JToken token)
        {
            if (token is JArray array)
                return new Vector2(ReadFloat(array[0]), ReadFloat(array[1]));
            return new Vector2(ReadFloat(token["x"] ?? token["X"]), ReadFloat(token["y"] ?? token["Y"]));
        }

        static Vector3 ReadVector3(JToken token)
        {
            if (token is JArray array)
                return new Vector3(ReadFloat(array[0]), ReadFloat(array[1]), ReadFloat(array[2]));
            return new Vector3(ReadFloat(token["x"] ?? token["X"]), ReadFloat(token["y"] ?? token["Y"]), ReadFloat(token["z"] ?? token["Z"]));
        }

        static Vector4 ReadVector4(JToken token)
        {
            if (token is JArray array)
                return new Vector4(ReadFloat(array[0]), ReadFloat(array[1]), ReadFloat(array[2]), ReadFloat(array[3]));
            return new Vector4(ReadFloat(token["x"] ?? token["X"]), ReadFloat(token["y"] ?? token["Y"]), ReadFloat(token["z"] ?? token["Z"]), ReadFloat(token["w"] ?? token["W"]));
        }

        static Vector2Int ReadVector2Int(JToken token)
        {
            if (token is JArray array)
                return new Vector2Int(ReadInt(array[0]), ReadInt(array[1]));
            return new Vector2Int(ReadInt(token["x"] ?? token["X"]), ReadInt(token["y"] ?? token["Y"]));
        }

        static Vector3Int ReadVector3Int(JToken token)
        {
            if (token is JArray array)
                return new Vector3Int(ReadInt(array[0]), ReadInt(array[1]), ReadInt(array[2]));
            return new Vector3Int(ReadInt(token["x"] ?? token["X"]), ReadInt(token["y"] ?? token["Y"]), ReadInt(token["z"] ?? token["Z"]));
        }

        static Quaternion ReadQuaternion(JToken token)
        {
            var vector = ReadVector4(token);
            return new Quaternion(vector.x, vector.y, vector.z, vector.w);
        }

        static Rect ReadRect(JToken token)
        {
            if (token is JArray array)
                return new Rect(ReadFloat(array[0]), ReadFloat(array[1]), ReadFloat(array[2]), ReadFloat(array[3]));
            return new Rect(ReadFloat(token["x"]), ReadFloat(token["y"]), ReadFloat(token["width"]), ReadFloat(token["height"]));
        }

        static RectInt ReadRectInt(JToken token)
        {
            if (token is JArray array)
                return new RectInt(ReadInt(array[0]), ReadInt(array[1]), ReadInt(array[2]), ReadInt(array[3]));
            return new RectInt(ReadInt(token["x"]), ReadInt(token["y"]), ReadInt(token["width"]), ReadInt(token["height"]));
        }

        static Bounds ReadBounds(JToken token)
        {
            var center = token["center"] != null ? ReadVector3(token["center"]) : Vector3.zero;
            var size = token["size"] != null ? ReadVector3(token["size"]) : Vector3.zero;
            return new Bounds(center, size);
        }

        static BoundsInt ReadBoundsInt(JToken token)
        {
            var position = token["position"] != null ? ReadVector3Int(token["position"]) : Vector3Int.zero;
            var size = token["size"] != null ? ReadVector3Int(token["size"]) : Vector3Int.zero;
            return new BoundsInt(position, size);
        }

        static Object ResolveObjectReference(JToken token, Type expectedType)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            var text = token.Type == JTokenType.String
                ? token.ToString()
                : token["path"]?.ToString() ?? token["assetPath"]?.ToString() ?? token["guid"]?.ToString();

            if (string.IsNullOrWhiteSpace(text) ||
                string.Equals(text, "null", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var path = text;
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                path = AssetDatabase.GUIDToAssetPath(text);
            }

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException($"Object reference '{text}' is not an asset path or GUID.");

            return AssetDatabase.LoadAssetAtPath(path, expectedType ?? typeof(Object));
        }

        static object ConvertToken(JToken token, Type targetType)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (targetType == typeof(string))
                return token.ToString();
            if (targetType == typeof(int))
                return ReadInt(token);
            if (targetType == typeof(float))
                return ReadFloat(token);
            if (targetType == typeof(double))
                return token.ToObject<double>();
            if (targetType == typeof(bool))
                return ReadBool(token);
            if (targetType == typeof(Color))
                return ReadColor(token);
            if (targetType == typeof(Vector2))
                return ReadVector2(token);
            if (targetType == typeof(Vector3))
                return ReadVector3(token);
            if (targetType == typeof(Vector4))
                return ReadVector4(token);
            if (targetType == typeof(Vector2Int))
                return ReadVector2Int(token);
            if (targetType == typeof(Vector3Int))
                return ReadVector3Int(token);
            if (targetType == typeof(Quaternion))
                return ReadQuaternion(token);
            if (targetType == typeof(Rect))
                return ReadRect(token);
            if (targetType == typeof(RectInt))
                return ReadRectInt(token);
            if (targetType == typeof(Bounds))
                return ReadBounds(token);
            if (targetType == typeof(BoundsInt))
                return ReadBoundsInt(token);
            if (targetType.IsEnum)
                return Enum.Parse(targetType, token.ToString(), ignoreCase: true);
            if (typeof(Object).IsAssignableFrom(targetType))
                return ResolveObjectReference(token, targetType);
            if (targetType.IsArray && token is JArray array)
            {
                var elementType = targetType.GetElementType();
                var result = Array.CreateInstance(elementType, array.Count);
                for (var i = 0; i < array.Count; i++)
                    result.SetValue(ConvertToken(array[i], elementType), i);
                return result;
            }

            return token.ToObject(targetType);
        }

        static object SerializePropertyValue(SerializedProperty property)
        {
            try
            {
                return SerializedPropertyPatcher.SerializePropertyValue(property);
            }
            catch (Exception ex)
            {
                return new
                {
                    error = "SerializedProperty value serialization failed.",
                    propertyType = property.propertyType.ToString(),
                    ex.Message
                };
            }
        }

        static object SerializeValue(object value)
        {
            return value switch
            {
                null => null,
                Color color => SerializeColor(color),
                Vector2 vector2 => SerializeVector2(vector2),
                Vector3 vector3 => SerializeVector3(vector3),
                Vector4 vector4 => SerializeVector4(vector4),
                Quaternion quaternion => SerializeQuaternion(quaternion),
                Object obj => SerializeObjectReference(obj),
                _ => value
            };
        }

        static object SerializeColor(Color value) => new { value.r, value.g, value.b, value.a };
        static object SerializeVector2(Vector2 value) => new { value.x, value.y };
        static object SerializeVector3(Vector3 value) => new { value.x, value.y, value.z };
        static object SerializeVector4(Vector4 value) => new { value.x, value.y, value.z, value.w };
        static object SerializeQuaternion(Quaternion value) => new { value.x, value.y, value.z, value.w };

        static object SerializeObjectReference(Object value)
        {
            return value == null
                ? null
                : new
                {
                    name = value.name,
                    type = value.GetType().FullName,
                    path = AssetDatabase.GetAssetPath(value),
                    guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value)),
                    entityId = UnityApiAdapter.GetObjectId(value)
                };
        }

        sealed class PropertyValidation
        {
            public List<PropertyChange> Changes { get; } = new();
            public List<string> Warnings { get; } = new();
            public List<string> Errors { get; } = new();
        }

        sealed class PropertyChange
        {
            public PropertyChange(string propertyPath, string propertyType, object before, object after, bool dryRun)
            {
                this.propertyPath = propertyPath;
                this.propertyType = propertyType;
                this.before = before;
                this.after = after;
                this.dryRun = dryRun;
            }

            public string propertyPath { get; }
            public string propertyType { get; }
            public object before { get; }
            public object after { get; }
            public bool dryRun { get; }
        }

        sealed class AssetResolution
        {
            public bool Success { get; private set; }
            public string Error { get; private set; }
            public string AssetPath { get; private set; }
            public ScriptableObject Asset { get; private set; }

            public static AssetResolution Ok(string path, ScriptableObject asset) => new()
            {
                Success = true,
                AssetPath = path,
                Asset = asset
            };

            public static AssetResolution Fail(string error) => new()
            {
                Success = false,
                Error = error
            };
        }

        sealed class TypeResolution
        {
            public bool Success { get; private set; }
            public string Error { get; private set; }
            public Type Type { get; private set; }
            public List<string> Warnings { get; } = new();

            public static TypeResolution Ok(Type type) => new()
            {
                Success = true,
                Type = type
            };

            public static TypeResolution Fail(string error) => new()
            {
                Success = false,
                Error = error
            };
        }

        sealed class TypeInfoDto
        {
            public string name { get; set; }
            public string fullName { get; set; }
            public string @namespace { get; set; }
            public string assembly { get; set; }
            public bool isAbstract { get; set; }
            public CreateAssetMenuDto createAssetMenu { get; set; }
        }

        sealed class CreateAssetMenuDto
        {
            public string menuName { get; set; }
            public string fileName { get; set; }
            public int order { get; set; }
        }
    }
}
