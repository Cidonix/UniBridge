#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
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
    /// Inspects and updates Unity AssetImporter settings through MCP-safe operations.
    /// </summary>
    public static class ManageAssetImporter
    {
        const int DefaultPropertyLimit = 200;
        const int MaxPropertyLimit = 2000;

        public const string Title = "Manage asset import settings";

        public const string Description = @"Inspect, patch, preset, or reimport Unity AssetImporter settings.

Use this when an agent needs to configure import settings for textures, sprites/icons, models, audio clips, or other imported assets.

Args:
    Action: Inspect, SetProperties, ApplyPreset, or Reimport.
    Path/Guid: Asset to inspect or update.
    ImporterType: Optional expected importer type such as TextureImporter, ModelImporter, AudioImporter, or UnityEditor.TextureImporter.
    Properties: Generic property patch. Keys can be public importer properties or SerializedProperty paths.
    Preset: TextureSprite2D, TextureUI, TextureReadable, TextureNormalMap, ModelStatic, ModelAnimated, Audio2D, or AudioStreaming.
    DryRun: Preview mutating actions without changing importer settings.
    Reimport: SaveAndReimport after changes, default true.

Returns:
    success, message, and importer snapshots, applied changes, warnings, and dry-run status.";

        [McpTool("UniBridge_ManageAssetImporter", Description, Title, Groups = new[] { "core", "assets" }, EnabledByDefault = true)]
        public static object HandleCommand(ManageAssetImporterParams parameters)
        {
            parameters ??= new ManageAssetImporterParams();

            var resolved = ResolveImporter(parameters);
            if (!resolved.Success)
            {
                return Response.Error(resolved.Error);
            }

            try
            {
                switch (parameters.Action)
                {
                    case AssetImporterAction.Inspect:
                        return Response.Success(
                            $"Inspected importer for '{resolved.AssetPath}'.",
                            BuildResponseData(resolved.AssetPath, resolved.Importer, parameters, Array.Empty<ChangeRecord>(), Array.Empty<string>(), false));

                    case AssetImporterAction.Reimport:
                        return ReimportImporter(resolved.AssetPath, resolved.Importer, parameters);

                    case AssetImporterAction.SetProperties:
                        return ApplyImporterChanges(resolved.AssetPath, resolved.Importer, parameters, usePreset: false);

                    case AssetImporterAction.ApplyPreset:
                        return ApplyImporterChanges(resolved.AssetPath, resolved.Importer, parameters, usePreset: true);

                    default:
                        return Response.Error($"Unsupported AssetImporter action '{parameters.Action}'.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageAssetImporter] {parameters.Action} failed for '{resolved.AssetPath}': {ex}");
                return Response.Error($"AssetImporter action '{parameters.Action}' failed for '{resolved.AssetPath}': {ex.Message}");
            }
        }

        static object ReimportImporter(string assetPath, AssetImporter importer, ManageAssetImporterParams parameters)
        {
            if (!CanMutate(assetPath, parameters, out var mutationError))
            {
                return Response.Error(mutationError);
            }

            var changes = new[]
            {
                new ChangeRecord("SaveAndReimport", null, null, null, "would call AssetImporter.SaveAndReimport", "pending")
            };

            if (!parameters.DryRun)
            {
                importer.SaveAndReimport();
            }

            return Response.Success(
                parameters.DryRun
                    ? $"Dry-run: importer for '{assetPath}' would be reimported."
                    : $"Importer for '{assetPath}' reimported.",
                BuildResponseData(assetPath, importer, parameters, changes, Array.Empty<string>(), parameters.DryRun));
        }

        static object ApplyImporterChanges(string assetPath, AssetImporter importer, ManageAssetImporterParams parameters, bool usePreset)
        {
            if (!CanMutate(assetPath, parameters, out var mutationError))
            {
                return Response.Error(mutationError);
            }

            var patch = new JObject();
            if (usePreset)
            {
                var presetPatch = BuildPresetPatch(importer, parameters, out var presetWarnings);
                foreach (var prop in presetPatch.Properties())
                {
                    patch[prop.Name] = prop.Value.DeepClone();
                }

                foreach (var warning in presetWarnings)
                {
                    Debug.LogWarning($"[ManageAssetImporter] {warning}");
                }
            }

            if (parameters.Properties != null)
            {
                foreach (var prop in parameters.Properties.Properties())
                {
                    patch[prop.Name] = prop.Value.DeepClone();
                }
            }

            if (!patch.HasValues)
            {
                return Response.Error(usePreset
                    ? "ApplyPreset requires a non-None Preset or Properties."
                    : "SetProperties requires non-empty Properties.");
            }

            var changes = new List<ChangeRecord>();
            var warnings = new List<string>();

            if (!parameters.DryRun)
            {
                Undo.RecordObject(importer, "UniBridge Asset Importer");
            }
            foreach (var prop in patch.Properties())
            {
                ApplyProperty(importer, prop.Name, prop.Value, parameters.DryRun, changes, warnings);
            }

            var hasEffectiveChanges = changes.Any(change => change.Status == "changed");
            if (warnings.Count > 0 && changes.Count == 0)
            {
                return Response.Error(
                    $"No importer properties were applied for '{assetPath}'.",
                    BuildResponseData(assetPath, importer, parameters, changes, warnings, parameters.DryRun));
            }

            if (!parameters.DryRun && hasEffectiveChanges)
            {
                EditorUtility.SetDirty(importer);
                if (parameters.Reimport)
                {
                    importer.SaveAndReimport();
                }
                else
                {
                    AssetDatabase.WriteImportSettingsIfDirty(assetPath);
                    AssetDatabase.SaveAssets();
                }
            }

            var message = parameters.DryRun
                ? $"Dry-run: {changes.Count(change => change.Status != "error")} importer setting(s) checked for '{assetPath}'."
                : hasEffectiveChanges
                    ? $"Applied {changes.Count(change => change.Status == "changed")} importer setting(s) to '{assetPath}'."
                    : $"Importer settings for '{assetPath}' already matched the requested values.";

            return Response.Success(
                message,
                BuildResponseData(assetPath, importer, parameters, changes, warnings, parameters.DryRun));
        }

        static JObject BuildPresetPatch(AssetImporter importer, ManageAssetImporterParams parameters, out List<string> warnings)
        {
            warnings = new List<string>();
            var patch = new JObject();
            var preset = parameters.Preset;

            if (preset == AssetImporterPreset.None)
            {
                return patch;
            }

            switch (preset)
            {
                case AssetImporterPreset.TextureSprite2D:
                case AssetImporterPreset.TextureUI:
                    if (!RequireImporter<TextureImporter>(importer, preset, warnings))
                        return patch;

                    patch["textureType"] = "Sprite";
                    patch["spriteImportMode"] = "Single";
                    patch["spritePixelsPerUnit"] = parameters.SpritePixelsPerUnit ?? 100f;
                    patch["alphaIsTransparency"] = true;
                    patch["mipmapEnabled"] = false;
                    patch["sRGBTexture"] = true;
                    if (parameters.IsReadable.HasValue)
                        patch["isReadable"] = parameters.IsReadable.Value;
                    if (parameters.MaxTextureSize.HasValue)
                        patch["maxTextureSize"] = parameters.MaxTextureSize.Value;
                    if (parameters.CompressionQuality.HasValue)
                        patch["compressionQuality"] = parameters.CompressionQuality.Value;

                    if (preset == AssetImporterPreset.TextureUI)
                    {
                        patch["textureCompression"] = "Uncompressed";
                        patch["filterMode"] = "Bilinear";
                    }
                    break;

                case AssetImporterPreset.TextureReadable:
                    if (!RequireImporter<TextureImporter>(importer, preset, warnings))
                        return patch;

                    patch["isReadable"] = true;
                    if (parameters.MaxTextureSize.HasValue)
                        patch["maxTextureSize"] = parameters.MaxTextureSize.Value;
                    break;

                case AssetImporterPreset.TextureNormalMap:
                    if (!RequireImporter<TextureImporter>(importer, preset, warnings))
                        return patch;

                    patch["textureType"] = "NormalMap";
                    patch["sRGBTexture"] = false;
                    patch["mipmapEnabled"] = true;
                    if (parameters.IsReadable.HasValue)
                        patch["isReadable"] = parameters.IsReadable.Value;
                    break;

                case AssetImporterPreset.ModelStatic:
                case AssetImporterPreset.ModelAnimated:
                    if (!RequireImporter<ModelImporter>(importer, preset, warnings))
                        return patch;

                    patch["globalScale"] = parameters.GlobalScale ?? 1f;
                    patch["isReadable"] = parameters.IsReadable ?? false;
                    patch["importAnimation"] = preset == AssetImporterPreset.ModelAnimated;
                    patch["importCameras"] = false;
                    patch["importLights"] = false;
                    patch["generateSecondaryUV"] = preset == AssetImporterPreset.ModelStatic;
                    break;

                case AssetImporterPreset.Audio2D:
                case AssetImporterPreset.AudioStreaming:
                    if (!RequireImporter<AudioImporter>(importer, preset, warnings))
                        return patch;

                    patch["forceToMono"] = false;
                    patch["ambisonic"] = false;
                    patch["loadInBackground"] = preset == AssetImporterPreset.AudioStreaming;
                    patch["defaultSampleSettings"] = BuildAudioSampleSettingsPatch(preset, parameters);
                    break;
            }

            return patch;
        }

        static JObject BuildAudioSampleSettingsPatch(AssetImporterPreset preset, ManageAssetImporterParams parameters)
        {
            return new JObject
            {
                ["loadType"] = preset == AssetImporterPreset.AudioStreaming ? "Streaming" : "DecompressOnLoad",
                ["compressionFormat"] = "Vorbis",
                ["quality"] = parameters.AudioQuality ?? (preset == AssetImporterPreset.AudioStreaming ? 0.65f : 0.8f),
                ["sampleRateSetting"] = "PreserveSampleRate"
            };
        }

        static bool RequireImporter<T>(AssetImporter importer, AssetImporterPreset preset, List<string> warnings)
            where T : AssetImporter
        {
            if (importer is T)
            {
                return true;
            }

            warnings.Add($"Preset {preset} requires {typeof(T).Name}, but asset uses {importer.GetType().Name}.");
            return false;
        }

        static void ApplyProperty(
            AssetImporter importer,
            string propertyName,
            JToken value,
            bool dryRun,
            List<ChangeRecord> changes,
            List<string> warnings)
        {
            if (TryApplyPublicMember(importer, propertyName, value, dryRun, changes, warnings))
            {
                return;
            }

            if (TryApplySerializedProperty(importer, propertyName, value, dryRun, changes, warnings))
            {
                return;
            }

            warnings.Add($"Property '{propertyName}' was not found on importer type '{importer.GetType().FullName}'.");
            changes.Add(new ChangeRecord(propertyName, null, JTokenToSimpleValue(value), null, "property not found", "error"));
        }

        static bool TryApplyPublicMember(
            AssetImporter importer,
            string propertyName,
            JToken value,
            bool dryRun,
            List<ChangeRecord> changes,
            List<string> warnings)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
            var type = importer.GetType();
            var property = type.GetProperty(propertyName, flags);
            if (property != null)
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                {
                    warnings.Add($"Property '{propertyName}' exists on {type.Name} but is not a writable simple property.");
                    changes.Add(new ChangeRecord(propertyName, null, JTokenToSimpleValue(value), property.PropertyType.FullName, "property is not writable", "error"));
                    return true;
                }

                if (!TryConvertToken(value, property.PropertyType, out var converted, out var convertError))
                {
                    warnings.Add($"Could not convert '{propertyName}' to {property.PropertyType.Name}: {convertError}");
                    changes.Add(new ChangeRecord(propertyName, null, JTokenToSimpleValue(value), property.PropertyType.FullName, convertError, "error"));
                    return true;
                }

                var before = property.GetValue(importer);
                var changed = !ValuesEqual(before, converted);
                if (!dryRun && changed)
                {
                    property.SetValue(importer, converted);
                }

                changes.Add(new ChangeRecord(property.Name, ToSimpleValue(before), ToSimpleValue(converted), property.PropertyType.FullName, null, changed ? "changed" : "unchanged"));
                return true;
            }

            var field = type.GetField(propertyName, flags);
            if (field == null)
            {
                return false;
            }

            if (field.IsInitOnly || field.IsLiteral)
            {
                warnings.Add($"Field '{propertyName}' exists on {type.Name} but is read-only.");
                changes.Add(new ChangeRecord(propertyName, null, JTokenToSimpleValue(value), field.FieldType.FullName, "field is read-only", "error"));
                return true;
            }

            if (!TryConvertToken(value, field.FieldType, out var fieldValue, out var fieldError))
            {
                warnings.Add($"Could not convert '{propertyName}' to {field.FieldType.Name}: {fieldError}");
                changes.Add(new ChangeRecord(propertyName, null, JTokenToSimpleValue(value), field.FieldType.FullName, fieldError, "error"));
                return true;
            }

            var fieldBefore = field.GetValue(importer);
            var fieldChanged = !ValuesEqual(fieldBefore, fieldValue);
            if (!dryRun && fieldChanged)
            {
                field.SetValue(importer, fieldValue);
            }

            changes.Add(new ChangeRecord(field.Name, ToSimpleValue(fieldBefore), ToSimpleValue(fieldValue), field.FieldType.FullName, null, fieldChanged ? "changed" : "unchanged"));
            return true;
        }

        static bool TryApplySerializedProperty(
            AssetImporter importer,
            string propertyPath,
            JToken value,
            bool dryRun,
            List<ChangeRecord> changes,
            List<string> warnings)
        {
            try
            {
                using var serializedObject = new SerializedObject(importer);
                serializedObject.Update();

                var patch = SerializedPropertyPatcher.TryApplyProperty(importer, serializedObject, propertyPath, value, dryRun);
                if (!patch.Found)
                {
                    return false;
                }

                if (!patch.Success)
                {
                    warnings.Add($"Could not set SerializedProperty '{propertyPath}': {patch.Error}");
                    changes.Add(new ChangeRecord(propertyPath, null, JTokenToSimpleValue(value), null, patch.Error, "error"));
                    return true;
                }

                if (!dryRun && patch.Changes.Count > 0)
                {
                    serializedObject.ApplyModifiedProperties();
                }

                foreach (var change in patch.Changes)
                {
                    changes.Add(new ChangeRecord(
                        change.propertyPath,
                        change.before,
                        change.after,
                        change.propertyType,
                        null,
                        dryRun ? "dryRun" : "changed"));
                }

                return true;
            }
            catch (Exception ex)
            {
                warnings.Add($"SerializedProperty '{propertyPath}' failed: {ex.Message}");
                changes.Add(new ChangeRecord(propertyPath, null, JTokenToSimpleValue(value), null, ex.Message, "error"));
                return true;
            }
        }

        static bool TryConvertToken(JToken token, Type targetType, out object value, out string error)
        {
            value = null;
            error = null;

            if (targetType == null)
            {
                error = "Target type is null.";
                return false;
            }

            if (token == null || token.Type == JTokenType.Null)
            {
                value = null;
                return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null;
            }

            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null)
            {
                targetType = nullableType;
            }

            try
            {
                if (targetType == typeof(string))
                {
                    value = token.ToString();
                    return true;
                }

                if (targetType == typeof(bool))
                {
                    value = token.ToObject<bool>();
                    return true;
                }

                if (targetType == typeof(int))
                {
                    value = token.ToObject<int>();
                    return true;
                }

                if (targetType == typeof(long))
                {
                    value = token.ToObject<long>();
                    return true;
                }

                if (targetType == typeof(float))
                {
                    value = token.ToObject<float>();
                    return true;
                }

                if (targetType == typeof(double))
                {
                    value = token.ToObject<double>();
                    return true;
                }

                if (targetType.IsEnum)
                {
                    if (token.Type == JTokenType.Integer)
                    {
                        value = Enum.ToObject(targetType, token.ToObject<int>());
                        return true;
                    }

                    var normalized = NormalizeEnumName(token.ToString());
                    foreach (var name in Enum.GetNames(targetType))
                    {
                        if (NormalizeEnumName(name) == normalized)
                        {
                            value = Enum.Parse(targetType, name);
                            return true;
                        }
                    }

                    error = $"Enum value '{token}' is not valid for {targetType.Name}. Valid values: {string.Join(", ", Enum.GetNames(targetType))}.";
                    return false;
                }

                if (targetType == typeof(Vector2))
                {
                    value = ReadVector2(token);
                    return true;
                }

                if (targetType == typeof(Vector3))
                {
                    value = ReadVector3(token);
                    return true;
                }

                if (targetType == typeof(Vector4))
                {
                    value = ReadVector4(token);
                    return true;
                }

                if (targetType == typeof(Color))
                {
                    value = ReadColor(token);
                    return true;
                }

                if (targetType == typeof(Rect))
                {
                    value = ReadRect(token);
                    return true;
                }

                if (targetType == typeof(Bounds))
                {
                    value = ReadBounds(token);
                    return true;
                }

                if (typeof(Object).IsAssignableFrom(targetType))
                {
                    if (!TryResolveObjectReference(token, targetType, out var objectValue, out error))
                        return false;

                    value = objectValue;
                    return true;
                }

                value = token.ToObject(targetType, JsonSerializer.CreateDefault());
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static bool TryResolveObjectReference(JToken token, Type targetType, out Object value, out string error)
        {
            value = null;
            error = null;

            string path = null;
            if (token.Type == JTokenType.String)
            {
                path = token.ToString();
            }
            else if (token is JObject obj)
            {
                path = obj.Value<string>("assetPath") ?? obj.Value<string>("path") ?? obj.Value<string>("guid");
                if (!string.IsNullOrWhiteSpace(path) && path.Length == 32 && !path.Contains("/") && !path.Contains("\\"))
                {
                    path = AssetDatabase.GUIDToAssetPath(path);
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Object reference value must be an asset path, GUID, or object with assetPath/path/guid.";
                return false;
            }

            var normalizedPath = NormalizeAssetPath(path);
            value = AssetDatabase.LoadAssetAtPath(normalizedPath, targetType);
            if (value == null)
            {
                error = $"Could not load {targetType.Name} at '{normalizedPath}'.";
                return false;
            }

            return true;
        }

        static object BuildResponseData(
            string assetPath,
            AssetImporter importer,
            ManageAssetImporterParams parameters,
            IReadOnlyList<ChangeRecord> changes,
            IReadOnlyList<string> warnings,
            bool dryRun)
        {
            return new
            {
                assetPath,
                guid = AssetDatabase.AssetPathToGUID(assetPath),
                dryRun,
                reimport = parameters.Reimport,
                importer = BuildImporterSnapshot(importer, parameters),
                changes = changes.Select(change => change.ToObject()).ToArray(),
                warnings = warnings.ToArray()
            };
        }

        static object BuildImporterSnapshot(AssetImporter importer, ManageAssetImporterParams parameters)
        {
            var snapshot = new Dictionary<string, object>
            {
                ["type"] = importer.GetType().FullName,
                ["typeName"] = importer.GetType().Name,
                ["assetPath"] = NormalizeAssetPath(importer.assetPath),
                ["userData"] = importer.userData,
                ["assetBundleName"] = importer.assetBundleName,
                ["assetBundleVariant"] = importer.assetBundleVariant
            };

            if (TryGetPublicProperty(importer, "importSettingsMissing", out var importSettingsMissing))
                snapshot["importSettingsMissing"] = importSettingsMissing;

            if (importer is TextureImporter textureImporter)
                snapshot["texture"] = BuildTextureImporterSnapshot(textureImporter);
            if (importer is ModelImporter modelImporter)
                snapshot["model"] = BuildModelImporterSnapshot(modelImporter);
            if (importer is AudioImporter audioImporter)
                snapshot["audio"] = BuildAudioImporterSnapshot(audioImporter);

            if (parameters.IncludeSerializedProperties)
            {
                snapshot["serializedProperties"] = SerializeProperties(importer, parameters.MaxSerializedProperties);
            }

            return snapshot;
        }

        static object BuildTextureImporterSnapshot(TextureImporter importer)
        {
            return new
            {
                textureType = importer.textureType.ToString(),
                textureShape = importer.textureShape.ToString(),
                sRGBTexture = importer.sRGBTexture,
                alphaSource = importer.alphaSource.ToString(),
                alphaIsTransparency = importer.alphaIsTransparency,
                mipmapEnabled = importer.mipmapEnabled,
                isReadable = importer.isReadable,
                spriteImportMode = importer.spriteImportMode.ToString(),
                spritePixelsPerUnit = importer.spritePixelsPerUnit,
                spriteMeshType = TryGetPublicProperty(importer, "spriteMeshType", out var spriteMeshType) ? spriteMeshType?.ToString() : null,
                spriteAlignment = TryGetPublicProperty(importer, "spriteAlignment", out var spriteAlignment) ? spriteAlignment : null,
                spritePivot = SerializeVector2(importer.spritePivot),
                spriteBorder = SerializeVector4(importer.spriteBorder),
                textureCompression = importer.textureCompression.ToString(),
                compressionQuality = importer.compressionQuality,
                maxTextureSize = TryGetPublicProperty(importer, "maxTextureSize", out var maxTextureSize) ? maxTextureSize : null,
                filterMode = TryGetPublicProperty(importer, "filterMode", out var filterMode) ? filterMode?.ToString() : null,
                wrapMode = TryGetPublicProperty(importer, "wrapMode", out var wrapMode) ? wrapMode?.ToString() : null
            };
        }

        static object BuildModelImporterSnapshot(ModelImporter importer)
        {
            return new
            {
                importer.globalScale,
                importer.useFileScale,
                importer.importCameras,
                importer.importLights,
                importer.importAnimation,
                animationType = importer.animationType.ToString(),
                materialImportMode = importer.materialImportMode.ToString(),
                materialLocation = importer.materialLocation.ToString(),
                importer.importBlendShapes,
                importer.isReadable,
                meshCompression = importer.meshCompression.ToString(),
                importNormals = importer.importNormals.ToString(),
                importTangents = importer.importTangents.ToString(),
                importer.generateSecondaryUV,
                importer.optimizeMeshPolygons,
                importer.optimizeMeshVertices
            };
        }

        static object BuildAudioImporterSnapshot(AudioImporter importer)
        {
            var settings = importer.defaultSampleSettings;
            return new
            {
                importer.forceToMono,
                importer.loadInBackground,
                importer.ambisonic,
                defaultSampleSettings = new
                {
                    loadType = settings.loadType.ToString(),
                    compressionFormat = settings.compressionFormat.ToString(),
                    quality = settings.quality,
                    sampleRateSetting = settings.sampleRateSetting.ToString(),
                    sampleRateOverride = settings.sampleRateOverride
                }
            };
        }

        static object SerializeProperties(Object obj, int requestedLimit)
        {
            var limit = Mathf.Clamp(requestedLimit <= 0 ? DefaultPropertyLimit : requestedLimit, 1, MaxPropertyLimit);
            var properties = new List<object>();
            var truncated = false;

            try
            {
                using var serializedObject = new SerializedObject(obj);
                var iterator = serializedObject.GetIterator();
                try
                {
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
                }
                finally
                {
                    iterator.Dispose();
                }
            }
            catch (Exception ex)
            {
                return new
                {
                    error = "SerializedObject traversal failed.",
                    exception = ex.GetType().FullName,
                    ex.Message
                };
            }

            return new
            {
                count = properties.Count,
                truncated,
                properties
            };
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

        static object SerializeObjectReference(Object value)
        {
            if (value == null)
                return null;

            var assetPath = AssetDatabase.GetAssetPath(value);
            return new
            {
                name = value.name,
                type = value.GetType().FullName ?? value.GetType().Name,
                entityId = UnityApiAdapter.GetObjectId(value),
                assetPath = NormalizeAssetPath(assetPath),
                guid = string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath)
            };
        }

        static ImporterResolution ResolveImporter(ManageAssetImporterParams parameters)
        {
            var assetPath = ResolveAssetPath(parameters.Path, parameters.Guid);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return ImporterResolution.Fail("Path or Guid is required.");
            }

            if (!AssetPathExists(assetPath))
            {
                return ImporterResolution.Fail($"Asset not found at '{assetPath}'.");
            }

            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
            {
                return ImporterResolution.Fail($"Asset at '{assetPath}' does not have an AssetImporter.");
            }

            if (!string.IsNullOrWhiteSpace(parameters.ImporterType))
            {
                if (!TryResolveImporterType(parameters.ImporterType, out var expectedType, out var typeError))
                {
                    return ImporterResolution.Fail(typeError);
                }

                if (importer.GetType() != expectedType)
                {
                    return ImporterResolution.Fail($"Importer type mismatch. Asset uses '{importer.GetType().FullName}', but '{parameters.ImporterType}' was requested.");
                }
            }

            return ImporterResolution.Ok(assetPath, importer);
        }

        static bool CanMutate(string assetPath, ManageAssetImporterParams parameters, out string error)
        {
            error = null;
            if (assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) && !parameters.AllowPackages)
            {
                error = $"Refusing to mutate package asset '{assetPath}'. Set AllowPackages=true only for embedded package assets you intentionally want to change.";
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

        static string ResolveAssetPath(string path, string guid)
        {
            if (!string.IsNullOrWhiteSpace(guid))
            {
                var guidPath = AssetDatabase.GUIDToAssetPath(guid.Trim());
                if (!string.IsNullOrWhiteSpace(guidPath))
                    return NormalizeAssetPath(guidPath);
            }

            return NormalizeAssetPath(path);
        }

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            path = path.Trim().Replace('\\', '/');
            if (path.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                path = path.Substring("unity://path/".Length);

            if (path.Contains("../", StringComparison.Ordinal) ||
                path.Contains("/..", StringComparison.Ordinal) ||
                Path.IsPathRooted(path))
            {
                return path;
            }

            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return "Assets/" + path.TrimStart('/');
        }

        static bool AssetPathExists(string path)
        {
#if UNITY_2022_0_OR_NEWER
            return AssetDatabase.AssetPathExists(path) || AssetDatabase.IsValidFolder(path);
#else
            return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)) ||
                   AssetDatabase.IsValidFolder(path) ||
                   File.Exists(Path.Combine(Directory.GetCurrentDirectory(), path));
#endif
        }

        static bool TryResolveImporterType(string typeName, out Type type, out string error)
        {
            type = null;
            error = null;

            var trimmed = typeName?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                error = "ImporterType cannot be empty.";
                return false;
            }

            type = Type.GetType(trimmed, throwOnError: false);
            if (IsValidImporterType(type))
                return true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                var matches = types
                    .Where(t => IsValidImporterType(t))
                    .Where(t => string.Equals(t.Name, trimmed, StringComparison.Ordinal) ||
                                string.Equals(t.FullName, trimmed, StringComparison.Ordinal))
                    .ToArray();

                if (matches.Length == 1)
                {
                    type = matches[0];
                    return true;
                }

                if (matches.Length > 1)
                {
                    error = $"ImporterType '{trimmed}' is ambiguous: {string.Join(", ", matches.Select(t => t.FullName))}.";
                    return false;
                }
            }

            error = $"ImporterType '{trimmed}' was not found or does not inherit UnityEditor.AssetImporter.";
            return false;
        }

        static bool IsValidImporterType(Type type)
        {
            return type != null && typeof(AssetImporter).IsAssignableFrom(type) && !type.IsAbstract;
        }

        static bool TryGetPublicProperty(object target, string propertyName, out object value)
        {
            value = null;
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property == null || !property.CanRead || property.GetIndexParameters().Length > 0)
            {
                return false;
            }

            value = property.GetValue(target);
            return true;
        }

        static string NormalizeEnumName(string value)
        {
            return (value ?? string.Empty)
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .ToLowerInvariant();
        }

        static bool ValuesEqual(object a, object b)
        {
            if (a == null || b == null)
                return a == null && b == null;

            return a.Equals(b);
        }

        static object ToSimpleValue(object value)
        {
            if (value == null)
                return null;

            return value switch
            {
                Vector2 vector => SerializeVector2(vector),
                Vector3 vector => SerializeVector3(vector),
                Vector4 vector => SerializeVector4(vector),
                Quaternion quaternion => SerializeQuaternion(quaternion),
                Color color => SerializeColor(color),
                Rect rect => SerializeRect(rect),
                Bounds bounds => SerializeBounds(bounds),
                Object obj => SerializeObjectReference(obj),
                Enum enumValue => enumValue.ToString(),
                AudioImporterSampleSettings settings => new
                {
                    loadType = settings.loadType.ToString(),
                    compressionFormat = settings.compressionFormat.ToString(),
                    quality = settings.quality,
                    sampleRateSetting = settings.sampleRateSetting.ToString(),
                    sampleRateOverride = settings.sampleRateOverride
                },
                _ => value
            };
        }

        static object JTokenToSimpleValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token is JValue value)
                return value.Value;

            return token.ToString(Formatting.None);
        }

        static Vector2 ReadVector2(JToken token)
        {
            if (token is JArray array)
                return new Vector2(array[0].ToObject<float>(), array[1].ToObject<float>());

            return new Vector2(token.Value<float>("x"), token.Value<float>("y"));
        }

        static Vector3 ReadVector3(JToken token)
        {
            if (token is JArray array)
                return new Vector3(array[0].ToObject<float>(), array[1].ToObject<float>(), array[2].ToObject<float>());

            return new Vector3(token.Value<float>("x"), token.Value<float>("y"), token.Value<float>("z"));
        }

        static Vector4 ReadVector4(JToken token)
        {
            if (token is JArray array)
                return new Vector4(array[0].ToObject<float>(), array[1].ToObject<float>(), array[2].ToObject<float>(), array[3].ToObject<float>());

            return new Vector4(token.Value<float>("x"), token.Value<float>("y"), token.Value<float>("z"), token.Value<float>("w"));
        }

        static Color ReadColor(JToken token)
        {
            if (token is JArray array)
            {
                return new Color(
                    array[0].ToObject<float>(),
                    array[1].ToObject<float>(),
                    array[2].ToObject<float>(),
                    array.Count > 3 ? array[3].ToObject<float>() : 1f);
            }

            return new Color(
                token.Value<float>("r"),
                token.Value<float>("g"),
                token.Value<float>("b"),
                token["a"] != null ? token.Value<float>("a") : 1f);
        }

        static Rect ReadRect(JToken token)
        {
            if (token is JArray array)
                return new Rect(array[0].ToObject<float>(), array[1].ToObject<float>(), array[2].ToObject<float>(), array[3].ToObject<float>());

            return new Rect(token.Value<float>("x"), token.Value<float>("y"), token.Value<float>("width"), token.Value<float>("height"));
        }

        static Bounds ReadBounds(JToken token)
        {
            var centerToken = token["center"];
            var sizeToken = token["size"];
            if (centerToken == null || sizeToken == null)
                throw new JsonSerializationException("Bounds requires center and size.");

            return new Bounds(ReadVector3(centerToken), ReadVector3(sizeToken));
        }

        static object SerializeVector2(Vector2 value) => new { value.x, value.y };
        static object SerializeVector3(Vector3 value) => new { value.x, value.y, value.z };
        static object SerializeVector4(Vector4 value) => new { value.x, value.y, value.z, value.w };
        static object SerializeQuaternion(Quaternion value) => new { value.x, value.y, value.z, value.w };
        static object SerializeColor(Color value) => new { value.r, value.g, value.b, value.a };
        static object SerializeRect(Rect value) => new { value.x, value.y, value.width, value.height };
        static object SerializeBounds(Bounds value) => new { center = SerializeVector3(value.center), size = SerializeVector3(value.size) };

        sealed record ImporterResolution(bool Success, string AssetPath, AssetImporter Importer, string Error)
        {
            public static ImporterResolution Ok(string assetPath, AssetImporter importer) => new(true, assetPath, importer, null);
            public static ImporterResolution Fail(string error) => new(false, null, null, error);
        }

        sealed record ChangeRecord(string Property, object Before, object After, string ValueType, string Error, string Status)
        {
            public object ToObject() => new
            {
                property = Property,
                before = Before,
                after = After,
                valueType = ValueType,
                error = Error,
                status = Status
            };
        }
    }
}
