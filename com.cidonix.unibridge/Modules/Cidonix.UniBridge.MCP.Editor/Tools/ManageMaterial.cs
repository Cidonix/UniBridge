#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Creates, inspects, validates, and updates Unity Material assets with shader-aware property handling.
    /// </summary>
    public static class ManageMaterial
    {
        const int DefaultPropertyLimit = 200;
        const int MaxPropertyLimit = 2000;

        public const string Title = "Manage materials";

        public const string Description = @"Inspect, validate, create, or update Unity Material assets with shader-aware property handling.

Use this for material authoring after importing textures/sprites/models, or when an agent needs to inspect valid shader properties before changing a material. Shader properties are applied by actual shader property name, for example _Color, _BaseColor, _MainTex, _BaseMap, _Metallic, or _BaseMap_ST.

Args:
    Action: Inspect, Validate, CreateOrUpdate, SetShader, SetProperties, or ApplyPreset.
    Path/Guid: Material asset to inspect or update.
    Shader: Optional shader path or shader name. Asset paths are loaded through AssetDatabase; other values use Shader.Find.
    Properties: Shader property patch plus optional material settings. structured { ""shader"": { ""shaderPath"": ""..."", ""props"": { ... } } } is supported.
    Preset: URPLit, URPUnlit, Standard, UnlitColor, SpriteDefault, UIDefault, Transparent, or Cutout.
    TexturePath/Color: Convenience preset inputs for common main texture and color properties.
    DryRun: Preview mutating actions without changing assets.

Returns:
    success, message, and material snapshots with shader metadata, values, planned/applied changes, warnings, and dry-run status.";

        [McpTool("UniBridge_ManageMaterial", Description, Title, Groups = new[] { "core", "assets" }, EnabledByDefault = true)]
        public static object HandleCommand(ManageMaterialParams parameters)
        {
            parameters ??= new ManageMaterialParams();

            try
            {
                switch (parameters.Action)
                {
                    case MaterialAction.Inspect:
                        return InspectMaterial(parameters);
                    case MaterialAction.Validate:
                        return ValidateMaterial(parameters);
                    case MaterialAction.CreateOrUpdate:
                        return CreateOrUpdateMaterial(parameters);
                    case MaterialAction.SetShader:
                        return SetMaterialShader(parameters);
                    case MaterialAction.SetProperties:
                        return SetMaterialProperties(parameters);
                    case MaterialAction.ApplyPreset:
                        return ApplyMaterialPreset(parameters);
                    default:
                        return Response.Error($"Unsupported Material action '{parameters.Action}'.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageMaterial] {parameters.Action} failed for '{parameters.Path ?? parameters.Guid}': {ex}");
                return Response.Error($"Material action '{parameters.Action}' failed: {ex.Message}");
            }
        }

        static object InspectMaterial(ManageMaterialParams parameters)
        {
            var resolved = ResolveMaterial(parameters, allowMissing: false);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            return Response.Success(
                $"Inspected material '{resolved.AssetPath}'.",
                BuildResponseData(resolved.AssetPath, resolved.Material, parameters, Array.Empty<ChangeRecord>(), Array.Empty<string>(), false, null));
        }

        static object ValidateMaterial(ManageMaterialParams parameters)
        {
            var resolved = ResolveMaterial(parameters, allowMissing: true);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            var tempMaterial = (Material)null;
            try
            {
                var currentMaterial = resolved.Material;
                var currentShader = currentMaterial != null ? currentMaterial.shader : null;
                var plan = BuildPlan(parameters, currentMaterial, currentShader);
                if (!plan.Success)
                    return Response.Error(plan.Error, new { warnings = plan.Warnings });

                var validation = ValidatePlan(plan);
                var materialForSnapshot = currentMaterial;
                if (materialForSnapshot == null && plan.TargetShader != null)
                {
                    tempMaterial = new Material(plan.TargetShader);
                    materialForSnapshot = tempMaterial;
                }

                var data = BuildResponseData(
                    resolved.AssetPath,
                    materialForSnapshot,
                    parameters,
                    validation.Changes,
                    validation.Warnings.Concat(plan.Warnings).ToArray(),
                    true,
                    plan.TargetShader);

                var success = validation.Errors.Count == 0;
                if (!success)
                {
                    return Response.Error($"Material validation found {validation.Errors.Count} issue(s).", new
                    {
                        path = resolved.AssetPath,
                        errors = validation.Errors,
                        warnings = validation.Warnings.Concat(plan.Warnings).ToArray(),
                        dryRun = true
                    });
                }

                return Response.Success($"Material validation passed for '{resolved.AssetPath ?? plan.TargetShader.name}'.", data);
            }
            finally
            {
                if (tempMaterial != null)
                    Object.DestroyImmediate(tempMaterial);
            }
        }

        static object CreateOrUpdateMaterial(ManageMaterialParams parameters)
        {
            var resolved = ResolveMaterial(parameters, allowMissing: true);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            if (!CanMutate(resolved.AssetPath, parameters, out var mutationError))
                return Response.Error(mutationError);

            var currentMaterial = resolved.Material;
            var plan = BuildPlan(parameters, currentMaterial, currentMaterial != null ? currentMaterial.shader : null);
            if (!plan.Success)
                return Response.Error(plan.Error, new { warnings = plan.Warnings });

            if (currentMaterial == null && plan.TargetShader == null)
                return Response.Error("CreateOrUpdate requires a Shader or a preset that resolves to a shader when the material does not exist.");

            return ApplyPlan(resolved.AssetPath, currentMaterial, parameters, plan, allowCreate: true);
        }

        static object SetMaterialShader(ManageMaterialParams parameters)
        {
            var resolved = ResolveMaterial(parameters, allowMissing: false);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            if (!CanMutate(resolved.AssetPath, parameters, out var mutationError))
                return Response.Error(mutationError);

            if (string.IsNullOrWhiteSpace(parameters.Shader) && !TryGetShaderPathFromProperties(parameters.Properties, out _))
                return Response.Error("SetShader requires Shader or Properties.shader.shaderPath.");

            var plan = BuildPlan(parameters, resolved.Material, resolved.Material.shader);
            if (!plan.Success)
                return Response.Error(plan.Error, new { warnings = plan.Warnings });

            if (plan.TargetShader == null)
                return Response.Error("SetShader could not resolve a target shader.");

            return ApplyPlan(resolved.AssetPath, resolved.Material, parameters, plan, allowCreate: false);
        }

        static object SetMaterialProperties(ManageMaterialParams parameters)
        {
            var resolved = ResolveMaterial(parameters, allowMissing: false);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            if (!CanMutate(resolved.AssetPath, parameters, out var mutationError))
                return Response.Error(mutationError);

            var plan = BuildPlan(parameters, resolved.Material, resolved.Material.shader);
            if (!plan.Success)
                return Response.Error(plan.Error, new { warnings = plan.Warnings });

            if (!plan.HasMaterialChanges)
                return Response.Error("SetProperties requires non-empty Properties, material setting parameters, TexturePath, or Color.");

            return ApplyPlan(resolved.AssetPath, resolved.Material, parameters, plan, allowCreate: false);
        }

        static object ApplyMaterialPreset(ManageMaterialParams parameters)
        {
            var resolved = ResolveMaterial(parameters, allowMissing: false);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            if (!CanMutate(resolved.AssetPath, parameters, out var mutationError))
                return Response.Error(mutationError);

            if (parameters.Preset == MaterialPreset.None)
                return Response.Error("ApplyPreset requires a non-None Preset.");

            var plan = BuildPlan(parameters, resolved.Material, resolved.Material.shader);
            if (!plan.Success)
                return Response.Error(plan.Error, new { warnings = plan.Warnings });

            return ApplyPlan(resolved.AssetPath, resolved.Material, parameters, plan, allowCreate: false);
        }

        static object ApplyPlan(string assetPath, Material material, ManageMaterialParams parameters, MaterialPlan plan, bool allowCreate)
        {
            var validation = ValidatePlan(plan);
            if (validation.Errors.Count > 0)
            {
                return Response.Error($"Material validation failed for '{assetPath}'.", new
                {
                    path = assetPath,
                    errors = validation.Errors,
                    warnings = validation.Warnings.Concat(plan.Warnings).ToArray(),
                    dryRun = parameters.DryRun
                });
            }

            var changes = new List<ChangeRecord>();
            var warnings = validation.Warnings.Concat(plan.Warnings).ToList();

            var created = false;
            var tempMaterial = (Material)null;
            try
            {
                if (material == null)
                {
                    if (!allowCreate)
                        return Response.Error($"Material not found at '{assetPath}'.");

                    if (parameters.DryRun)
                    {
                        tempMaterial = new Material(plan.TargetShader);
                        material = tempMaterial;
                        changes.Add(ChangeRecord.Pending("asset", null, assetPath, "would create material asset"));
                    }
                    else
                    {
                        EnsureParentDirectory(assetPath);
                        material = new Material(plan.TargetShader);
                        AssetDatabase.CreateAsset(material, assetPath);
                        created = true;
                        changes.Add(ChangeRecord.Changed("asset", null, assetPath, "created material asset"));
                    }
                }

                if (material == null)
                    return Response.Error($"Could not create or load material at '{assetPath}'.");

                if (!parameters.DryRun)
                    Undo.RecordObject(material, created ? "Create Material" : "Update Material");

                ApplyShaderChange(material, plan, parameters.DryRun, changes);
                ApplySettings(material, plan.Settings, parameters.DryRun, changes);
                ApplyShaderProperties(material, plan, parameters.DryRun, changes);

                if (!parameters.DryRun)
                {
                    EditorUtility.SetDirty(material);
                    AssetDatabase.SaveAssets();
                    if (parameters.Select)
                    {
                        Selection.activeObject = material;
                        EditorGUIUtility.PingObject(material);
                    }
                }

                var changedCount = changes.Count(change => change.status == "changed" || change.status == "would_change");
                var message = parameters.DryRun
                    ? $"Dry-run: {changedCount} material change(s) checked for '{assetPath}'."
                    : created
                        ? $"Created material '{assetPath}'."
                        : changedCount > 0
                            ? $"Applied {changes.Count(change => change.status == "changed")} material change(s) to '{assetPath}'."
                            : $"Material '{assetPath}' already matched the requested state.";

                return Response.Success(
                    message,
                    BuildResponseData(assetPath, material, parameters, changes, warnings, parameters.DryRun, plan.TargetShader));
            }
            finally
            {
                if (tempMaterial != null)
                    Object.DestroyImmediate(tempMaterial);
            }
        }

        static MaterialPlan BuildPlan(ManageMaterialParams parameters, Material material, Shader currentShader)
        {
            var plan = new MaterialPlan();
            var shaderPath = parameters.Shader;
            var shaderRequested = !string.IsNullOrWhiteSpace(shaderPath);

            if (TryGetShaderPathFromProperties(parameters.Properties, out var nestedShaderPath) && string.IsNullOrWhiteSpace(shaderPath))
            {
                shaderPath = nestedShaderPath;
                shaderRequested = true;
            }

            if (string.IsNullOrWhiteSpace(shaderPath))
            {
                shaderPath = GetPresetShaderPath(parameters.Preset, currentShader);
                shaderRequested = parameters.Preset != MaterialPreset.None && !string.IsNullOrWhiteSpace(shaderPath);
            }

            if (!string.IsNullOrWhiteSpace(shaderPath))
            {
                plan.TargetShader = LoadShaderByPathOrName(shaderPath);
                plan.TargetShaderSource = shaderPath;
                plan.ShaderRequested = shaderRequested;
                if (plan.TargetShader == null)
                {
                    plan.Success = false;
                    plan.Error = $"Could not find shader by path or name: '{shaderPath}'.";
                    return plan;
                }
            }
            else
            {
                plan.TargetShader = currentShader;
                plan.TargetShaderSource = currentShader != null ? GetShaderPathOrName(currentShader) : null;
                plan.ShaderRequested = false;
            }

            if (plan.TargetShader == null)
            {
                plan.Success = false;
                plan.Error = "No shader could be resolved. Provide Shader, use a shader preset, or target an existing material with a shader.";
                return plan;
            }

            AddPresetPatch(plan, parameters);
            AddConveniencePatch(plan, parameters);
            AddPropertiesPatch(plan, parameters.Properties);
            AddExplicitSettings(plan, parameters);

            plan.Success = true;
            return plan;
        }

        static void AddPresetPatch(MaterialPlan plan, ManageMaterialParams parameters)
        {
            switch (parameters.Preset)
            {
                case MaterialPreset.Transparent:
                    AddIfShaderHasProperty(plan, "_Surface", 1f);
                    AddIfShaderHasProperty(plan, "_Blend", 0f);
                    AddIfShaderHasProperty(plan, "_AlphaClip", 0f);
                    AddIfShaderHasProperty(plan, "_Mode", 3f);
                    AddIfShaderHasProperty(plan, "_SrcBlend", (int)BlendMode.SrcAlpha);
                    AddIfShaderHasProperty(plan, "_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    AddIfShaderHasProperty(plan, "_ZWrite", 0f);
                    plan.Settings.RenderQueue = 3000;
                    plan.Settings.EnableKeywords.Add("_SURFACE_TYPE_TRANSPARENT");
                    plan.Settings.EnableKeywords.Add("_ALPHABLEND_ON");
                    break;

                case MaterialPreset.Cutout:
                    AddIfShaderHasProperty(plan, "_Surface", 0f);
                    AddIfShaderHasProperty(plan, "_AlphaClip", 1f);
                    AddIfShaderHasProperty(plan, "_Cutoff", 0.5f);
                    AddIfShaderHasProperty(plan, "_Mode", 1f);
                    AddIfShaderHasProperty(plan, "_SrcBlend", (int)BlendMode.One);
                    AddIfShaderHasProperty(plan, "_DstBlend", (int)BlendMode.Zero);
                    AddIfShaderHasProperty(plan, "_ZWrite", 1f);
                    plan.Settings.RenderQueue = 2450;
                    plan.Settings.EnableKeywords.Add("_ALPHATEST_ON");
                    plan.Settings.DisableKeywords.Add("_ALPHABLEND_ON");
                    break;
            }
        }

        static void AddConveniencePatch(MaterialPlan plan, ManageMaterialParams parameters)
        {
            if (parameters.Color != null && parameters.Color.Type != JTokenType.Null)
            {
                var colorProp = FindFirstVisibleProperty(plan.TargetShader, "_BaseColor", "_Color", "_TintColor", "_ColorTint");
                if (colorProp != null)
                    plan.ShaderProperties[colorProp] = parameters.Color.DeepClone();
                else
                    plan.Warnings.Add($"Shader '{plan.TargetShader.name}' has no common color property for Color.");
            }

            if (!string.IsNullOrWhiteSpace(parameters.TexturePath))
            {
                var textureProp = FindFirstVisibleProperty(plan.TargetShader, "_BaseMap", "_MainTex", "_MainTex2D", "_BaseColorMap", "_UnlitColorMap");
                if (textureProp != null)
                    plan.ShaderProperties[textureProp] = parameters.TexturePath;
                else
                    plan.Warnings.Add($"Shader '{plan.TargetShader.name}' has no common main texture property for TexturePath.");
            }
        }

        static void AddPropertiesPatch(MaterialPlan plan, JObject properties)
        {
            if (properties == null)
                return;

            AddSettingsFromObject(plan.Settings, properties);

            if (properties["settings"] is JObject settings)
                AddSettingsFromObject(plan.Settings, settings);
            if (properties["material"] is JObject materialSettings)
                AddSettingsFromObject(plan.Settings, materialSettings);

            if (properties["shader"] is JObject shaderObject)
            {
                AddPropertiesFromNamedObject(plan, shaderObject["props"] as JObject);
                AddPropertiesFromNamedObject(plan, shaderObject["properties"] as JObject);
            }

            AddPropertiesFromNamedObject(plan, properties["props"] as JObject);
            AddPropertiesFromNamedObject(plan, properties["shaderProps"] as JObject);
            AddPropertiesFromNamedObject(plan, properties["shader_properties"] as JObject);

            if (properties["color"] != null && properties["color"].Type != JTokenType.Null)
            {
                var colorProp = FindFirstVisibleProperty(plan.TargetShader, "_BaseColor", "_Color", "_TintColor", "_ColorTint");
                if (colorProp != null)
                    plan.ShaderProperties[colorProp] = properties["color"].DeepClone();
            }

            if (properties["texture"] is JObject textureObject)
            {
                var propertyName = textureObject.Value<string>("name") ?? textureObject.Value<string>("property") ?? "_MainTex";
                var path = textureObject["path"] ?? textureObject["value"] ?? textureObject["texturePath"];
                if (!string.IsNullOrWhiteSpace(propertyName) && path != null)
                    plan.ShaderProperties[propertyName] = path.DeepClone();
            }
            else if (properties["texture"] is JToken textureToken && textureToken.Type == JTokenType.String)
            {
                var textureProp = FindFirstVisibleProperty(plan.TargetShader, "_BaseMap", "_MainTex", "_MainTex2D", "_BaseColorMap", "_UnlitColorMap");
                if (textureProp != null)
                    plan.ShaderProperties[textureProp] = textureToken.DeepClone();
            }

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "shader", "shaderPath", "shaderName", "props", "shaderProps", "shader_properties",
                "settings", "material", "enableInstancing", "doubleSidedGI", "renderQueue",
                "keywords", "enableKeywords", "disableKeywords", "enabledKeywords", "disabledKeywords",
                "color", "texture"
            };

            foreach (var property in properties.Properties())
            {
                if (reserved.Contains(property.Name))
                    continue;

                if (IsShaderPropertyLike(plan.TargetShader, property.Name))
                    plan.ShaderProperties[property.Name] = property.Value.DeepClone();
            }
        }

        static void AddPropertiesFromNamedObject(MaterialPlan plan, JObject properties)
        {
            if (properties == null)
                return;

            foreach (var property in properties.Properties())
            {
                plan.ShaderProperties[property.Name] = property.Value.DeepClone();
            }
        }

        static void AddExplicitSettings(MaterialPlan plan, ManageMaterialParams parameters)
        {
            if (parameters.EnableInstancing.HasValue)
                plan.Settings.EnableInstancing = parameters.EnableInstancing.Value;
            if (parameters.DoubleSidedGI.HasValue)
                plan.Settings.DoubleSidedGI = parameters.DoubleSidedGI.Value;
            if (parameters.RenderQueue.HasValue)
                plan.Settings.RenderQueue = parameters.RenderQueue.Value;

            if (parameters.Keywords != null)
            {
                plan.Settings.ReplaceKeywords = true;
                plan.Settings.Keywords.Clear();
                plan.Settings.Keywords.AddRange(parameters.Keywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword)));
            }

            if (parameters.EnableKeywords != null)
                plan.Settings.EnableKeywords.AddRange(parameters.EnableKeywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword)));
            if (parameters.DisableKeywords != null)
                plan.Settings.DisableKeywords.AddRange(parameters.DisableKeywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword)));
        }

        static ValidationResult ValidatePlan(MaterialPlan plan)
        {
            var result = new ValidationResult();
            var shader = plan.TargetShader;

            if (shader == null)
            {
                result.Errors.Add("Target shader is null.");
                return result;
            }

            foreach (var property in plan.ShaderProperties.Properties())
            {
                ValidateShaderProperty(shader, property.Name, property.Value, result);
            }

            foreach (var keyword in plan.Settings.Keywords.Concat(plan.Settings.EnableKeywords).Concat(plan.Settings.DisableKeywords))
            {
                if (string.IsNullOrWhiteSpace(keyword))
                    result.Errors.Add("Shader keyword entries cannot be empty.");
            }

            return result;
        }

        static void ValidateShaderProperty(Shader shader, string propertyName, JToken value, ValidationResult result)
        {
            if (propertyName.EndsWith("_ST", StringComparison.Ordinal))
            {
                var texturePropertyName = propertyName.Substring(0, propertyName.Length - 3);
                var textureIndex = shader.FindPropertyIndex(texturePropertyName);
                if (textureIndex < 0)
                {
                    result.Errors.Add($"Texture property '{texturePropertyName}' not found for scale/offset '{propertyName}'.");
                    return;
                }

                if (GetShaderPropertyTypeId(shader, textureIndex) != 4)
                {
                    result.Errors.Add($"Scale/offset property '{propertyName}' targets '{texturePropertyName}', but it is not a Texture property.");
                    return;
                }

                if (HasShaderFlag(shader, textureIndex, 4))
                {
                    result.Errors.Add($"Texture property '{texturePropertyName}' does not support scale/offset.");
                    return;
                }

                if (!TryParseTextureScaleOffset(value, out _, out var error))
                    result.Errors.Add(error);

                result.Changes.Add(ChangeRecord.Pending(propertyName, null, NormalizeValueForResponse(value), "validated texture scale/offset"));
                return;
            }

            var index = shader.FindPropertyIndex(propertyName);
            if (index < 0)
            {
                result.Errors.Add($"Property '{propertyName}' not found in shader '{shader.name}'.");
                return;
            }

            if (HasShaderFlag(shader, index, 1))
            {
                result.Errors.Add($"Property '{propertyName}' is hidden in shader '{shader.name}' and cannot be set.");
                return;
            }

            switch (GetShaderPropertyTypeId(shader, index))
            {
                case 0:
                    if (!TryParseColor(value, out _, out var colorError))
                        result.Errors.Add($"Invalid value for Color property '{propertyName}': {colorError}");
                    break;
                case 1:
                    if (!TryParseVector4(value, out _, out var vectorError))
                        result.Errors.Add($"Invalid value for Vector property '{propertyName}': {vectorError}");
                    break;
                case 2:
                case 3:
                    if (!TryParseFloat(value, out var floatValue, out var floatError))
                    {
                        result.Errors.Add($"Invalid value for Float/Range property '{propertyName}': {floatError}");
                    }
                    else if (GetShaderPropertyTypeId(shader, index) == 3)
                    {
                        var limits = shader.GetPropertyRangeLimits(index);
                        if (floatValue < limits.x || floatValue > limits.y)
                            result.Warnings.Add($"Value {floatValue.ToString(CultureInfo.InvariantCulture)} for property '{propertyName}' is outside range [{limits.x}, {limits.y}].");
                    }
                    break;
                case 4:
                    if (!TryParseTexture(value, out var texture, out var texturePath, out var textureError))
                    {
                        result.Errors.Add($"Invalid value for Texture property '{propertyName}': {textureError}");
                    }
                    else if (!string.IsNullOrWhiteSpace(texturePath) && texture == null)
                    {
                        result.Errors.Add($"Could not load texture at path '{texturePath}' for property '{propertyName}'.");
                    }
                    break;
                case 5:
                    if (!TryParseInt(value, out _, out var intError))
                        result.Errors.Add($"Invalid value for Int property '{propertyName}': {intError}");
                    break;
                default:
                    result.Errors.Add($"Property '{propertyName}' has unsupported shader property type '{shader.GetPropertyType(index)}'.");
                    break;
            }

            result.Changes.Add(ChangeRecord.Pending(propertyName, null, NormalizeValueForResponse(value), "validated shader property"));
        }

        static void ApplyShaderChange(Material material, MaterialPlan plan, bool dryRun, List<ChangeRecord> changes)
        {
            if (plan.TargetShader == null || material.shader == plan.TargetShader)
                return;

            var before = GetShaderPathOrName(material.shader);
            var after = GetShaderPathOrName(plan.TargetShader);
            if (!dryRun)
                material.shader = plan.TargetShader;

            changes.Add(dryRun
                ? ChangeRecord.WouldChange("shader", before, after, "would change material shader")
                : ChangeRecord.Changed("shader", before, after, "changed material shader"));
        }

        static void ApplySettings(Material material, MaterialSettingsPatch settings, bool dryRun, List<ChangeRecord> changes)
        {
            if (settings.EnableInstancing.HasValue)
            {
                var before = material.enableInstancing;
                var after = settings.EnableInstancing.Value;
                AddSettingChange(changes, "enableInstancing", before, after, dryRun, () => material.enableInstancing = after);
            }

            if (settings.DoubleSidedGI.HasValue)
            {
                var before = material.doubleSidedGI;
                var after = settings.DoubleSidedGI.Value;
                AddSettingChange(changes, "doubleSidedGI", before, after, dryRun, () => material.doubleSidedGI = after);
            }

            if (settings.RenderQueue.HasValue)
            {
                var before = material.renderQueue;
                var after = settings.RenderQueue.Value;
                AddSettingChange(changes, "renderQueue", before, after, dryRun, () => material.renderQueue = after);
            }

            if (settings.ReplaceKeywords)
            {
                var before = material.shaderKeywords ?? Array.Empty<string>();
                var after = settings.Keywords.Distinct(StringComparer.Ordinal).ToArray();
                var same = before.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(after.OrderBy(x => x, StringComparer.Ordinal));
                if (!same)
                {
                    if (!dryRun)
                        material.shaderKeywords = after;

                    changes.Add(dryRun
                        ? ChangeRecord.WouldChange("keywords", before, after, "would replace shader keywords")
                        : ChangeRecord.Changed("keywords", before, after, "replaced shader keywords"));
                }
            }

            foreach (var keyword in settings.EnableKeywords.Distinct(StringComparer.Ordinal))
            {
                var before = material.IsKeywordEnabled(keyword);
                if (!before)
                {
                    if (!dryRun)
                        material.EnableKeyword(keyword);

                    changes.Add(dryRun
                        ? ChangeRecord.WouldChange($"keyword:{keyword}", false, true, "would enable shader keyword")
                        : ChangeRecord.Changed($"keyword:{keyword}", false, true, "enabled shader keyword"));
                }
            }

            foreach (var keyword in settings.DisableKeywords.Distinct(StringComparer.Ordinal))
            {
                var before = material.IsKeywordEnabled(keyword);
                if (before)
                {
                    if (!dryRun)
                        material.DisableKeyword(keyword);

                    changes.Add(dryRun
                        ? ChangeRecord.WouldChange($"keyword:{keyword}", true, false, "would disable shader keyword")
                        : ChangeRecord.Changed($"keyword:{keyword}", true, false, "disabled shader keyword"));
                }
            }
        }

        static void ApplyShaderProperties(Material material, MaterialPlan plan, bool dryRun, List<ChangeRecord> changes)
        {
            foreach (var property in plan.ShaderProperties.Properties())
            {
                ApplyShaderProperty(material, property.Name, property.Value, dryRun, changes);
            }
        }

        static void ApplyShaderProperty(Material material, string propertyName, JToken value, bool dryRun, List<ChangeRecord> changes)
        {
            var shader = material.shader;

            if (propertyName.EndsWith("_ST", StringComparison.Ordinal))
            {
                var texturePropertyName = propertyName.Substring(0, propertyName.Length - 3);
                var beforeScale = material.GetTextureScale(texturePropertyName);
                var beforeOffset = material.GetTextureOffset(texturePropertyName);
                var before = ToVector4Token(new Vector4(beforeScale.x, beforeScale.y, beforeOffset.x, beforeOffset.y));
                TryParseTextureScaleOffset(value, out var st, out _);
                var after = ToVector4Token(st);
                var same = JToken.DeepEquals(before, after);
                if (!same && !dryRun)
                {
                    material.SetTextureScale(texturePropertyName, new Vector2(st.x, st.y));
                    material.SetTextureOffset(texturePropertyName, new Vector2(st.z, st.w));
                }

                AddPropertyChange(changes, propertyName, before, after, dryRun, same, "texture scale/offset");
                return;
            }

            var index = shader.FindPropertyIndex(propertyName);
            switch (GetShaderPropertyTypeId(shader, index))
            {
                case 0:
                    TryParseColor(value, out var color, out _);
                    var colorBefore = ToColorToken(material.GetColor(propertyName));
                    var colorAfter = ToColorToken(color);
                    var colorSame = JToken.DeepEquals(colorBefore, colorAfter);
                    if (!colorSame && !dryRun)
                        material.SetColor(propertyName, color);
                    AddPropertyChange(changes, propertyName, colorBefore, colorAfter, dryRun, colorSame, "color");
                    break;

                case 1:
                    TryParseVector4(value, out var vector, out _);
                    var vectorBefore = ToVector4Token(material.GetVector(propertyName));
                    var vectorAfter = ToVector4Token(vector);
                    var vectorSame = JToken.DeepEquals(vectorBefore, vectorAfter);
                    if (!vectorSame && !dryRun)
                        material.SetVector(propertyName, vector);
                    AddPropertyChange(changes, propertyName, vectorBefore, vectorAfter, dryRun, vectorSame, "vector");
                    break;

                case 2:
                case 3:
                    TryParseFloat(value, out var floatValue, out _);
                    var floatBefore = material.GetFloat(propertyName);
                    var floatSame = Mathf.Approximately(floatBefore, floatValue);
                    if (!floatSame && !dryRun)
                        material.SetFloat(propertyName, floatValue);
                    AddPropertyChange(changes, propertyName, floatBefore, floatValue, dryRun, floatSame, "float");
                    break;

                case 4:
                    TryParseTexture(value, out var texture, out var texturePath, out _);
                    var textureBefore = material.GetTexture(propertyName);
                    var textureBeforePath = textureBefore != null ? AssetDatabase.GetAssetPath(textureBefore) : null;
                    var textureAfterPath = texture != null ? AssetDatabase.GetAssetPath(texture) : texturePath;
                    var textureSame = textureBefore == texture;
                    if (!textureSame && !dryRun)
                        material.SetTexture(propertyName, texture);
                    AddPropertyChange(changes, propertyName, textureBeforePath, textureAfterPath, dryRun, textureSame, "texture");
                    break;

                case 5:
                    TryParseInt(value, out var intValue, out _);
                    var intBefore = material.GetInt(propertyName);
                    var intSame = intBefore == intValue;
                    if (!intSame && !dryRun)
                        material.SetInt(propertyName, intValue);
                    AddPropertyChange(changes, propertyName, intBefore, intValue, dryRun, intSame, "int");
                    break;
            }
        }

        static void AddSettingChange<T>(List<ChangeRecord> changes, string name, T before, T after, bool dryRun, Action apply)
        {
            if (EqualityComparer<T>.Default.Equals(before, after))
                return;

            if (!dryRun)
                apply();

            changes.Add(dryRun
                ? ChangeRecord.WouldChange(name, before, after, "would update material setting")
                : ChangeRecord.Changed(name, before, after, "updated material setting"));
        }

        static void AddPropertyChange(List<ChangeRecord> changes, string name, object before, object after, bool dryRun, bool same, string label)
        {
            if (same)
                changes.Add(ChangeRecord.Unchanged(name, before, after, $"{label} already matched"));
            else
                changes.Add(dryRun
                    ? ChangeRecord.WouldChange(name, before, after, $"would update {label} property")
                    : ChangeRecord.Changed(name, before, after, $"updated {label} property"));
        }

        static object BuildResponseData(
            string assetPath,
            Material material,
            ManageMaterialParams parameters,
            IEnumerable<ChangeRecord> changes,
            IEnumerable<string> warnings,
            bool dryRun,
            Shader plannedShader)
        {
            var shader = plannedShader != null ? plannedShader : material != null ? material.shader : null;
            var data = new
            {
                path = assetPath,
                guid = !string.IsNullOrWhiteSpace(assetPath) ? AssetDatabase.AssetPathToGUID(assetPath) : null,
                exists = material != null && !string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(material)),
                dryRun,
                material = material != null ? BuildMaterialSnapshot(assetPath, material, parameters) : null,
                plannedShader = shader != null ? BuildShaderSnapshot(shader, null, parameters, includeValues: false) : null,
                changes = changes?.ToArray() ?? Array.Empty<ChangeRecord>(),
                warnings = warnings?.Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct().ToArray() ?? Array.Empty<string>()
            };

            return data;
        }

        static object BuildMaterialSnapshot(string assetPath, Material material, ManageMaterialParams parameters)
        {
            if (material == null)
                return null;

            return new
            {
                name = material.name,
                path = assetPath,
                shader = BuildShaderSnapshot(material.shader, material, parameters, parameters.IncludeValues),
                enableInstancing = material.enableInstancing,
                doubleSidedGI = material.doubleSidedGI,
                renderQueue = material.renderQueue,
                globalIlluminationFlags = material.globalIlluminationFlags.ToString(),
                keywords = material.shaderKeywords ?? Array.Empty<string>()
            };
        }

        static object BuildShaderSnapshot(Shader shader, Material material, ManageMaterialParams parameters, bool includeValues)
        {
            if (shader == null)
                return null;

            var shaderAssetPath = AssetDatabase.GetAssetPath(shader);
            return new
            {
                name = shader.name,
                pathOrName = GetShaderPathOrName(shader),
                assetPath = string.IsNullOrEmpty(shaderAssetPath) ? null : shaderAssetPath,
                propertyCount = shader.GetPropertyCount(),
                properties = parameters.IncludeShaderProperties
                    ? BuildShaderPropertySnapshots(shader, material, parameters, includeValues)
                    : Array.Empty<object>()
            };
        }

        static object[] BuildShaderPropertySnapshots(Shader shader, Material material, ManageMaterialParams parameters, bool includeValues)
        {
            var max = Mathf.Clamp(parameters.MaxShaderProperties <= 0 ? DefaultPropertyLimit : parameters.MaxShaderProperties, 1, MaxPropertyLimit);
            var items = new List<object>();
            var count = shader.GetPropertyCount();
            for (var i = 0; i < count && items.Count < max; i++)
            {
                var hidden = HasShaderFlag(shader, i, 1);
                if (hidden && !parameters.IncludeHiddenProperties)
                    continue;

                var typeId = GetShaderPropertyTypeId(shader, i);
                var name = shader.GetPropertyName(i);
                var supportsScaleOffset = typeId == 4 && !HasShaderFlag(shader, i, 4);

                items.Add(new
                {
                    index = i,
                    name,
                    displayName = shader.GetPropertyDescription(i),
                    type = GetShaderPropertyTypeName(typeId),
                    flags = shader.GetPropertyFlags(i).ToString(),
                    hidden,
                    supportsScaleOffset,
                    range = typeId == 3 ? ToVector2Token(shader.GetPropertyRangeLimits(i)) : null,
                    textureDimension = typeId == 4 ? shader.GetPropertyTextureDimension(i).ToString() : null,
                    value = includeValues && material != null ? GetMaterialPropertyValue(material, name, typeId, supportsScaleOffset) : null
                });
            }

            return items.ToArray();
        }

        static object GetMaterialPropertyValue(Material material, string propertyName, int typeId, bool supportsScaleOffset)
        {
            switch (typeId)
            {
                case 0:
                    return ToColorToken(material.GetColor(propertyName));
                case 1:
                    return ToVector4Token(material.GetVector(propertyName));
                case 2:
                case 3:
                    return material.GetFloat(propertyName);
                case 4:
                    var texture = material.GetTexture(propertyName);
                    var scale = material.GetTextureScale(propertyName);
                    var offset = material.GetTextureOffset(propertyName);
                    return new
                    {
                        path = texture != null ? AssetDatabase.GetAssetPath(texture) : null,
                        name = texture != null ? texture.name : null,
                        scaleOffset = supportsScaleOffset ? ToVector4Token(new Vector4(scale.x, scale.y, offset.x, offset.y)) : null
                    };
                case 5:
                    return material.GetInt(propertyName);
                default:
                    return null;
            }
        }

        static ResolvedMaterial ResolveMaterial(ManageMaterialParams parameters, bool allowMissing)
        {
            var path = parameters.Path;
            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(parameters.Guid))
                path = AssetDatabase.GUIDToAssetPath(parameters.Guid);

            if (string.IsNullOrWhiteSpace(path))
            {
                if (allowMissing && parameters.Action == MaterialAction.Validate && !string.IsNullOrWhiteSpace(parameters.Shader))
                    return ResolvedMaterial.Ok(null, null);

                return ResolvedMaterial.Fail("Path or Guid is required.");
            }

            if (!TryNormalizeMaterialPath(path, out var assetPath, out var error))
                return ResolvedMaterial.Fail(error);

            var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material != null)
                return ResolvedMaterial.Ok(assetPath, material);

            var existing = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
            if (existing != null)
                return ResolvedMaterial.Fail($"Asset at '{assetPath}' is '{existing.GetType().FullName}', not a Material.");

            if (allowMissing)
                return ResolvedMaterial.Ok(assetPath, null);

            return ResolvedMaterial.Fail($"Material not found at '{assetPath}'.");
        }

        static bool CanMutate(string assetPath, ManageMaterialParams parameters, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                error = "Material Path is required for mutating actions.";
                return false;
            }

            if (assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) && !parameters.AllowPackages)
            {
                error = $"Refusing to mutate package material '{assetPath}'. Set AllowPackages=true if this is intentional.";
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

        static bool TryNormalizeMaterialPath(string path, out string normalizedPath, out string error)
        {
            normalizedPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Material path cannot be empty.";
                return false;
            }

            var value = path.Trim().Trim('"').Replace('\\', '/');
            if (value.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("unity://path/".Length);

            if (Path.IsPathRooted(value) || value.Contains(":"))
            {
                error = $"Material path must be a Unity project path under Assets/ or Packages/: '{path}'.";
                return false;
            }

            while (value.Contains("//"))
                value = value.Replace("//", "/");

            if (value.Contains("../") || value.EndsWith("/..", StringComparison.Ordinal) || value == "..")
            {
                error = $"Material path cannot contain '..': '{path}'.";
                return false;
            }

            if (!value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                value = "Assets/" + value.TrimStart('/');
            }

            if (string.IsNullOrWhiteSpace(Path.GetExtension(value)))
                value += ".mat";

            if (!string.Equals(Path.GetExtension(value), ".mat", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Material path must use .mat extension: '{value}'.";
                return false;
            }

            normalizedPath = value;
            return true;
        }

        static void EnsureParentDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || AssetDatabase.IsValidFolder(directory))
                return;

            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), directory);
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
                AssetDatabase.Refresh();
            }
        }

        static Shader LoadShaderByPathOrName(string pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName))
                return null;

            var value = pathOrName.Trim().Trim('"').Replace('\\', '/');
            if ((value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) &&
                AssetDatabase.LoadAssetAtPath<Shader>(value) is Shader shaderFromPath)
            {
                return shaderFromPath;
            }

            var shader = Shader.Find(value);
            if (shader != null)
                return shader;

            var fileName = Path.GetFileNameWithoutExtension(value);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                foreach (var guid in AssetDatabase.FindAssets($"{fileName} t:Shader"))
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    var assetShader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                    if (assetShader != null && (assetShader.name == value || assetShader.name == fileName || assetPath == value))
                        return assetShader;
                }
            }

            return null;
        }

        static string GetShaderPathOrName(Shader shader)
        {
            if (shader == null)
                return null;

            var assetPath = AssetDatabase.GetAssetPath(shader);
            return !string.IsNullOrWhiteSpace(assetPath) ? assetPath : shader.name;
        }

        static string GetPresetShaderPath(MaterialPreset preset, Shader currentShader)
        {
            switch (preset)
            {
                case MaterialPreset.URPLit:
                    return "Universal Render Pipeline/Lit";
                case MaterialPreset.URPUnlit:
                    return "Universal Render Pipeline/Unlit";
                case MaterialPreset.Standard:
                    return "Standard";
                case MaterialPreset.UnlitColor:
                    return "Unlit/Color";
                case MaterialPreset.SpriteDefault:
                    return "Sprites/Default";
                case MaterialPreset.UIDefault:
                    return "UI/Default";
                case MaterialPreset.Transparent:
                case MaterialPreset.Cutout:
                    return currentShader != null ? GetShaderPathOrName(currentShader) : "Universal Render Pipeline/Lit";
                default:
                    return null;
            }
        }

        static bool TryGetShaderPathFromProperties(JObject properties, out string shaderPath)
        {
            shaderPath = null;
            if (properties == null)
                return false;

            shaderPath = properties.Value<string>("shaderPath") ?? properties.Value<string>("shaderName");
            if (!string.IsNullOrWhiteSpace(shaderPath))
                return true;

            if (properties["shader"] is JObject shaderObject)
            {
                shaderPath = shaderObject.Value<string>("shaderPath") ?? shaderObject.Value<string>("name") ?? shaderObject.Value<string>("path");
                return !string.IsNullOrWhiteSpace(shaderPath);
            }

            if (properties["shader"] is JToken shaderToken && shaderToken.Type == JTokenType.String)
            {
                shaderPath = shaderToken.Value<string>();
                return !string.IsNullOrWhiteSpace(shaderPath);
            }

            return false;
        }

        static void AddIfShaderHasProperty(MaterialPlan plan, string propertyName, JToken value)
        {
            if (plan.TargetShader != null && plan.TargetShader.FindPropertyIndex(propertyName) >= 0)
                plan.ShaderProperties[propertyName] = value;
        }

        static string FindFirstVisibleProperty(Shader shader, params string[] propertyNames)
        {
            if (shader == null)
                return null;

            foreach (var propertyName in propertyNames)
            {
                var index = shader.FindPropertyIndex(propertyName);
                if (index >= 0 && !HasShaderFlag(shader, index, 1))
                    return propertyName;
            }

            return null;
        }

        static bool IsShaderPropertyLike(Shader shader, string propertyName)
        {
            if (shader == null || string.IsNullOrWhiteSpace(propertyName))
                return false;

            if (shader.FindPropertyIndex(propertyName) >= 0)
                return true;

            if (propertyName.EndsWith("_ST", StringComparison.Ordinal))
            {
                var baseName = propertyName.Substring(0, propertyName.Length - 3);
                return shader.FindPropertyIndex(baseName) >= 0;
            }

            return propertyName.StartsWith("_", StringComparison.Ordinal);
        }

        static void AddSettingsFromObject(MaterialSettingsPatch settings, JObject obj)
        {
            if (obj == null)
                return;

            if (TryGetBool(obj, out var enableInstancing, "enableInstancing", "EnableInstancing"))
                settings.EnableInstancing = enableInstancing;
            if (TryGetBool(obj, out var doubleSidedGI, "doubleSidedGI", "DoubleSidedGI"))
                settings.DoubleSidedGI = doubleSidedGI;
            if (TryGetInt(obj, out var renderQueue, "renderQueue", "RenderQueue"))
                settings.RenderQueue = renderQueue;

            if (TryGetStringArray(obj, out var keywords, "keywords", "Keywords"))
            {
                settings.ReplaceKeywords = true;
                settings.Keywords.Clear();
                settings.Keywords.AddRange(keywords);
            }

            if (TryGetStringArray(obj, out var enableKeywords, "enableKeywords", "enabledKeywords", "EnableKeywords"))
                settings.EnableKeywords.AddRange(enableKeywords);
            if (TryGetStringArray(obj, out var disableKeywords, "disableKeywords", "disabledKeywords", "DisableKeywords"))
                settings.DisableKeywords.AddRange(disableKeywords);
        }

        static bool TryGetBool(JObject obj, out bool value, params string[] names)
        {
            value = false;
            foreach (var name in names)
            {
                var token = obj[name];
                if (token == null || token.Type == JTokenType.Null)
                    continue;

                if (token.Type == JTokenType.Boolean)
                {
                    value = token.Value<bool>();
                    return true;
                }

                if (bool.TryParse(token.ToString(), out value))
                    return true;
            }

            return false;
        }

        static bool TryGetInt(JObject obj, out int value, params string[] names)
        {
            value = 0;
            foreach (var name in names)
            {
                var token = obj[name];
                if (token == null || token.Type == JTokenType.Null)
                    continue;

                if (token.Type == JTokenType.Integer || int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    if (token.Type == JTokenType.Integer)
                        value = token.Value<int>();
                    return true;
                }
            }

            return false;
        }

        static bool TryGetStringArray(JObject obj, out string[] values, params string[] names)
        {
            values = null;
            foreach (var name in names)
            {
                var token = obj[name];
                if (token == null || token.Type == JTokenType.Null)
                    continue;

                if (token is JArray array)
                {
                    values = array
                        .Select(item => item.Type == JTokenType.Null ? null : item.ToString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToArray();
                    return true;
                }

                values = token.ToString()
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray();
                return true;
            }

            return false;
        }

        static int GetShaderPropertyTypeId(Shader shader, int index) => (int)shader.GetPropertyType(index);

        static string GetShaderPropertyTypeName(int typeId)
        {
            switch (typeId)
            {
                case 0:
                    return "Color";
                case 1:
                    return "Vector";
                case 2:
                    return "Float";
                case 3:
                    return "Range";
                case 4:
                    return "Texture";
                case 5:
                    return "Int";
                default:
                    return $"Unknown({typeId})";
            }
        }

        static bool HasShaderFlag(Shader shader, int propertyIndex, int flagMask)
        {
            return (((int)shader.GetPropertyFlags(propertyIndex)) & flagMask) != 0;
        }

        static bool TryParseColor(JToken value, out Color color, out string error)
        {
            color = Color.white;
            error = null;

            if (value == null || value.Type == JTokenType.Null)
            {
                error = "Expected color object, array, or HTML color string.";
                return false;
            }

            if (value.Type == JTokenType.String)
            {
                if (ColorUtility.TryParseHtmlString(value.Value<string>(), out color))
                    return true;

                error = "Expected #RGB, #RRGGBB, #RGBA, or #RRGGBBAA.";
                return false;
            }

            if (value is JArray array)
            {
                if (array.Count < 3)
                {
                    error = "Expected at least [r,g,b].";
                    return false;
                }

                var a = 1f;
                if (!TryReadFloat(array[0], out var r) ||
                    !TryReadFloat(array[1], out var g) ||
                    !TryReadFloat(array[2], out var b) ||
                    (array.Count > 3 && !TryReadFloat(array[3], out a)))
                {
                    error = "Color array values must be numbers.";
                    return false;
                }

                color = new Color(r, g, b, a);
                return true;
            }

            if (value is JObject obj)
            {
                if (!TryReadFloat(obj["r"] ?? obj["x"], out var r) ||
                    !TryReadFloat(obj["g"] ?? obj["y"], out var g) ||
                    !TryReadFloat(obj["b"] ?? obj["z"], out var b))
                {
                    error = "Expected r/g/b or x/y/z numeric fields.";
                    return false;
                }

                var alphaToken = obj["a"] ?? obj["w"];
                var a = 1f;
                if (alphaToken != null && !TryReadFloat(alphaToken, out a))
                {
                    error = "Alpha must be a number.";
                    return false;
                }

                color = new Color(r, g, b, a);
                return true;
            }

            error = "Expected color object, array, or HTML color string.";
            return false;
        }

        static bool TryParseVector4(JToken value, out Vector4 vector, out string error)
        {
            vector = Vector4.zero;
            error = null;

            if (value is JArray array)
            {
                if (array.Count < 2 || array.Count > 4)
                {
                    error = "Expected vector array with 2 to 4 numbers.";
                    return false;
                }

                var values = new[] { 0f, 0f, 0f, 0f };
                for (var i = 0; i < array.Count; i++)
                {
                    if (!TryReadFloat(array[i], out values[i]))
                    {
                        error = "Vector array values must be numbers.";
                        return false;
                    }
                }

                vector = new Vector4(values[0], values[1], values[2], values[3]);
                return true;
            }

            if (value is JObject obj)
            {
                if (!TryReadFloat(obj["x"] ?? obj["r"], out var x))
                {
                    error = "Expected x numeric field.";
                    return false;
                }

                var y = ReadOptionalFloat(obj["y"] ?? obj["g"], 0f);
                var z = ReadOptionalFloat(obj["z"] ?? obj["b"], 0f);
                var w = ReadOptionalFloat(obj["w"] ?? obj["a"], 0f);
                vector = new Vector4(x, y, z, w);
                return true;
            }

            error = "Expected vector object or array.";
            return false;
        }

        static bool TryParseTextureScaleOffset(JToken value, out Vector4 vector, out string error)
        {
            vector = Vector4.zero;
            error = null;

            if (TryParseVector4(value, out vector, out error))
                return true;

            if (value is JObject obj)
            {
                var scale = obj["scale"] as JArray ?? obj["tiling"] as JArray;
                var offset = obj["offset"] as JArray;
                if (scale != null && offset != null && scale.Count >= 2 && offset.Count >= 2)
                {
                    if (TryReadFloat(scale[0], out var sx) &&
                        TryReadFloat(scale[1], out var sy) &&
                        TryReadFloat(offset[0], out var ox) &&
                        TryReadFloat(offset[1], out var oy))
                    {
                        vector = new Vector4(sx, sy, ox, oy);
                        return true;
                    }
                }
            }

            error = "Expected {x,y,z,w}, [x,y,z,w], or {scale:[x,y], offset:[x,y]}.";
            return false;
        }

        static bool TryParseFloat(JToken value, out float number, out string error)
        {
            error = null;
            if (TryReadFloat(value, out number))
                return true;

            error = "Expected number.";
            return false;
        }

        static bool TryParseInt(JToken value, out int number, out string error)
        {
            number = 0;
            error = null;
            if (value == null || value.Type == JTokenType.Null)
            {
                error = "Expected integer.";
                return false;
            }

            if (value.Type == JTokenType.Integer)
            {
                number = value.Value<int>();
                return true;
            }

            if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return true;

            error = "Expected integer.";
            return false;
        }

        static bool TryParseTexture(JToken value, out Texture texture, out string texturePath, out string error)
        {
            texture = null;
            texturePath = null;
            error = null;

            if (value == null || value.Type == JTokenType.Null)
                return true;

            if (value is JObject obj)
                value = obj["path"] ?? obj["assetPath"] ?? obj["value"];

            if (value == null || value.Type == JTokenType.Null)
                return true;

            if (value.Type != JTokenType.String)
            {
                error = "Expected texture asset path string or null.";
                return false;
            }

            texturePath = NormalizeAssetReferencePath(value.Value<string>());
            texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            return true;
        }

        static string NormalizeAssetReferencePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            var value = path.Trim().Trim('"').Replace('\\', '/');
            if (!value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                value = "Assets/" + value.TrimStart('/');
            }

            return value;
        }

        static bool TryReadFloat(JToken token, out float value)
        {
            value = 0f;
            if (token == null || token.Type == JTokenType.Null)
                return false;

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = token.Value<float>();
                return true;
            }

            return float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        static float ReadOptionalFloat(JToken token, float fallback)
        {
            return TryReadFloat(token, out var value) ? value : fallback;
        }

        static JToken NormalizeValueForResponse(JToken value)
        {
            return value == null ? JValue.CreateNull() : value.DeepClone();
        }

        static JObject ToColorToken(Color color)
        {
            return new JObject
            {
                ["r"] = color.r,
                ["g"] = color.g,
                ["b"] = color.b,
                ["a"] = color.a
            };
        }

        static JObject ToVector2Token(Vector2 vector)
        {
            return new JObject
            {
                ["x"] = vector.x,
                ["y"] = vector.y
            };
        }

        static JObject ToVector4Token(Vector4 vector)
        {
            return new JObject
            {
                ["x"] = vector.x,
                ["y"] = vector.y,
                ["z"] = vector.z,
                ["w"] = vector.w
            };
        }

        sealed class ResolvedMaterial
        {
            public bool Success;
            public string Error;
            public string AssetPath;
            public Material Material;

            public static ResolvedMaterial Ok(string path, Material material) => new ResolvedMaterial
            {
                Success = true,
                AssetPath = path,
                Material = material
            };

            public static ResolvedMaterial Fail(string error) => new ResolvedMaterial
            {
                Success = false,
                Error = error
            };
        }

        sealed class MaterialPlan
        {
            public bool Success;
            public string Error;
            public Shader TargetShader;
            public string TargetShaderSource;
            public bool ShaderRequested;
            public JObject ShaderProperties = new JObject();
            public MaterialSettingsPatch Settings = new MaterialSettingsPatch();
            public List<string> Warnings = new List<string>();

            public bool HasMaterialChanges =>
                ShaderProperties.HasValues ||
                Settings.HasValues ||
                ShaderRequested;
        }

        sealed class MaterialSettingsPatch
        {
            public bool? EnableInstancing;
            public bool? DoubleSidedGI;
            public int? RenderQueue;
            public bool ReplaceKeywords;
            public List<string> Keywords = new List<string>();
            public List<string> EnableKeywords = new List<string>();
            public List<string> DisableKeywords = new List<string>();

            public bool HasValues =>
                EnableInstancing.HasValue ||
                DoubleSidedGI.HasValue ||
                RenderQueue.HasValue ||
                ReplaceKeywords ||
                EnableKeywords.Count > 0 ||
                DisableKeywords.Count > 0;
        }

        sealed class ValidationResult
        {
            public List<string> Errors = new List<string>();
            public List<string> Warnings = new List<string>();
            public List<ChangeRecord> Changes = new List<ChangeRecord>();
        }

        sealed class ChangeRecord
        {
            public string property;
            public object before;
            public object after;
            public string status;
            public string message;

            public static ChangeRecord Pending(string property, object before, object after, string message) => new ChangeRecord
            {
                property = property,
                before = before,
                after = after,
                status = "validated",
                message = message
            };

            public static ChangeRecord WouldChange(string property, object before, object after, string message) => new ChangeRecord
            {
                property = property,
                before = before,
                after = after,
                status = "would_change",
                message = message
            };

            public static ChangeRecord Changed(string property, object before, object after, string message) => new ChangeRecord
            {
                property = property,
                before = before,
                after = after,
                status = "changed",
                message = message
            };

            public static ChangeRecord Unchanged(string property, object before, object after, string message) => new ChangeRecord
            {
                property = property,
                before = before,
                after = after,
                status = "unchanged",
                message = message
            };
        }
    }
}
