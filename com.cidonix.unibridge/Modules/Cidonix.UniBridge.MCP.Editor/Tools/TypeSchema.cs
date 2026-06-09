#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Reports write-safe schemas for Unity types, components, importers, assets, and shaders.
    /// </summary>
    public static class TypeSchema
    {
        const int DefaultLimit = 80;
        const int MaxLimit = 500;
        const int DefaultSerializedPropertyLimit = 160;
        const int MaxSerializedPropertyLimit = 1000;

        public const string Title = "Inspect Unity type schemas";

        public const string Description = @"Inspect Unity types and return MCP-native JSON schemas for AI-safe property editing.

Use this before setting component, ScriptableObject, AssetImporter, or Material properties. It returns JSON schema data for writable fields/properties, serialized property paths, enum values, object-reference hints, UnityEvent hints, and shader property metadata.

Args:
    Action: Inspect, ListTypes, InspectShader, InspectAsset, InspectGameObject, or PatchExamples.
    Kind: Any, Component, MonoBehaviour, ScriptableObject, AssetImporter, Asset, or Shader.
    TypeName/TypeNames: Type names to inspect.
    Path/Guid: Asset path for InspectAsset or InspectShader.
    Shader: Shader path or Shader.Find name for InspectShader.
    Target/SearchMethod: Scene GameObject target for InspectGameObject.
    IncludeInactive: Include inactive scene and Prefab Stage objects for InspectGameObject target lookup.
    ComponentTypes: Optional component type filters for InspectGameObject.
    IncludeSerializedProperties/IncludeValues: Include SerializedObject property paths and optional current values.

Returns:
    success, message, and schema data with exact member names/property paths that UniBridge tools can use for property patches.";

        [McpTool("UniBridge_TypeSchema", Description, Title, Groups = new[] { "core", "schema", "assets", "scene" }, EnabledByDefault = true)]
        public static object HandleCommand(TypeSchemaParams parameters)
        {
            parameters ??= new TypeSchemaParams();

            try
            {
                return parameters.Action switch
                {
                    TypeSchemaAction.ListTypes => ListTypes(parameters),
                    TypeSchemaAction.InspectShader => InspectShader(parameters),
                    TypeSchemaAction.InspectAsset => InspectAsset(parameters),
                    TypeSchemaAction.InspectGameObject => InspectGameObject(parameters),
                    TypeSchemaAction.PatchExamples => PatchExamples(parameters),
                    TypeSchemaAction.Inspect => Inspect(parameters),
                    _ => Response.Error($"Unsupported TypeSchema action '{parameters.Action}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TypeSchema] {parameters.Action} failed: {ex}");
                return Response.Error($"TypeSchema action '{parameters.Action}' failed: {ex.Message}");
            }
        }

        static object Inspect(TypeSchemaParams parameters)
        {
            var names = CollectTypeNames(parameters);

            if (!string.IsNullOrWhiteSpace(parameters.Shader) || (parameters.Kind == TypeSchemaKind.Shader && names.Count == 0))
                return InspectShader(parameters);

            if (!string.IsNullOrWhiteSpace(parameters.Path) || !string.IsNullOrWhiteSpace(parameters.Guid))
                return InspectAsset(parameters);

            if (!string.IsNullOrWhiteSpace(parameters.Target))
                return InspectGameObject(parameters);

            if (names.Count == 0)
                return Response.Error("Inspect requires TypeName/TypeNames, Shader, Path/Guid, or Target.");

            var schemas = new List<object>();
            var errors = new List<object>();
            foreach (var name in names)
            {
                var type = ResolveType(name, parameters.Kind);
                if (type == null)
                {
                    errors.Add(new { typeName = name, error = $"Type '{name}' was not found for kind '{parameters.Kind}'." });
                    continue;
                }

                schemas.Add(BuildTypeSchema(type, parameters, null, null));
            }

            var data = new
            {
                action = "Inspect",
                kind = parameters.Kind.ToString(),
                requested = names.ToArray(),
                returned = schemas.Count,
                errors = errors.ToArray(),
                schemas = schemas.ToArray()
            };

            return schemas.Count > 0
                ? Response.Success($"Built {schemas.Count} type schema(s).", data)
                : Response.Error("No requested type schemas could be built.", data);
        }

        static object PatchExamples(TypeSchemaParams parameters)
        {
            parameters.IncludePatchExamples = true;
            return Inspect(parameters);
        }

        static object ListTypes(TypeSchemaParams parameters)
        {
            var limit = Clamp(parameters.Limit <= 0 ? DefaultLimit : parameters.Limit, 1, MaxLimit);
            var query = (parameters.Query ?? parameters.TypeName ?? string.Empty).Trim();
            var types = EnumerateTypes(parameters.Kind)
                .Where(type => parameters.IncludeAbstract || !type.IsAbstract)
                .Where(type => MatchesTypeQuery(type, query))
                .OrderBy(type => type.FullName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(type => BuildTypeSummary(type))
                .ToArray();

            return Response.Success($"Found {types.Length} type(s) for kind '{parameters.Kind}'.", new
            {
                action = "ListTypes",
                kind = parameters.Kind.ToString(),
                query,
                limit,
                returned = types.Length,
                types
            });
        }

        static object InspectShader(TypeSchemaParams parameters)
        {
            var shader = ResolveShader(parameters, out var source);
            if (shader == null)
                return Response.Error("InspectShader requires a valid Shader, Path, or Guid.");

            return Response.Success($"Built shader schema for '{shader.name}'.", new
            {
                action = "InspectShader",
                source,
                shader = BuildShaderSchema(shader)
            });
        }

        static object InspectAsset(TypeSchemaParams parameters)
        {
            if (!TryResolveAssetPath(parameters, out var assetPath, out var error))
                return Response.Error(error);

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            var importer = AssetImporter.GetAtPath(assetPath);
            var material = asset as Material;
            var shader = asset as Shader;

            var assetType = asset == null ? AssetDatabase.GetMainAssetTypeAtPath(assetPath) : asset.GetType();
            var data = new
            {
                action = "InspectAsset",
                path = assetPath,
                guid = AssetDatabase.AssetPathToGUID(assetPath),
                exists = asset != null || importer != null,
                mainAsset = asset == null ? null : new
                {
                    name = asset.name,
                    type = BuildTypeSummary(asset.GetType()),
                    schema = assetType != null ? BuildTypeSchema(assetType, parameters, parameters.IncludeSerializedProperties ? asset : null, null) : null
                },
                importer = importer == null ? null : new
                {
                    type = BuildTypeSummary(importer.GetType()),
                    schema = BuildTypeSchema(importer.GetType(), parameters, parameters.IncludeSerializedProperties ? importer : null, null)
                },
                materialShader = material != null && material.shader != null ? BuildShaderSchema(material.shader, material) : null,
                shader = shader != null ? BuildShaderSchema(shader) : null
            };

            return Response.Success($"Built asset schema for '{assetPath}'.", data);
        }

        static object InspectGameObject(TypeSchemaParams parameters)
        {
            var targetToken = string.IsNullOrWhiteSpace(parameters.Target) ? null : JToken.FromObject(parameters.Target);
            if (targetToken == null)
                return Response.Error("InspectGameObject requires Target.");

            var searchMethod = string.IsNullOrWhiteSpace(parameters.SearchMethod)
                ? "by_id_or_name_or_path"
                : parameters.SearchMethod;
            var findParams = new JObject
            {
                ["search_inactive"] = parameters.IncludeInactive
            };
            var gameObject = ObjectsHelper.FindObject(targetToken, searchMethod, findParams);
            if (gameObject == null)
                return Response.Error($"GameObject target '{parameters.Target}' was not found using search method '{searchMethod}' (IncludeInactive={parameters.IncludeInactive}).");

            var filters = NormalizeStrings(parameters.ComponentTypes);
            var components = gameObject.GetComponents<Component>()
                .Where(component => component != null)
                .Where(component => filters.Count == 0 || filters.Any(filter => MatchesComponentFilter(component, filter)))
                .Select(component => new
                {
                    name = component.GetType().Name,
                    type = component.GetType().FullName,
                    scriptIdentity = ComponentIdentity.BuildScriptIdentity(component),
                    namespaceMigration = ComponentIdentity.BuildNamespaceMigrationDiagnostic(component),
                    schema = BuildTypeSchema(component.GetType(), parameters, parameters.IncludeSerializedProperties ? component : null, component)
                })
                .ToArray();

            return Response.Success($"Built schemas for {components.Length} component(s) on '{gameObject.name}'.", new
            {
                action = "InspectGameObject",
                target = new
                {
                    name = gameObject.name,
                    instanceId = GetObjectInstanceId(gameObject),
                    path = GetGameObjectPath(gameObject),
                    activeSelf = gameObject.activeSelf,
                    activeInHierarchy = gameObject.activeInHierarchy,
                    layer = LayerMask.LayerToName(gameObject.layer),
                    tag = gameObject.tag
                },
                includeInactive = parameters.IncludeInactive,
                componentFilters = filters.ToArray(),
                components
            });
        }

        static object BuildTypeSchema(Type type, TypeSchemaParams parameters, Object serializedObjectTarget, Object valueSource)
        {
            var fields = parameters.IncludeFields
                ? BuildFieldSchemas(type, parameters).ToArray()
                : Array.Empty<object>();
            var properties = parameters.IncludeProperties
                ? BuildPropertySchemas(type, parameters).ToArray()
                : Array.Empty<object>();
            var serializedProperties = parameters.IncludeSerializedProperties && serializedObjectTarget != null
                ? BuildSerializedPropertySchemas(serializedObjectTarget, parameters).ToArray()
                : Array.Empty<object>();

            return new
            {
                type = BuildTypeSummary(type),
                usage = BuildUsageHints(type),
                contextProfile = BuildContextProfile(type, serializedObjectTarget),
                fields,
                properties,
                serializedProperties,
                writableNames = fields.Concat(properties)
                    .Select(item => JObject.FromObject(item))
                    .Where(item => item.Value<bool?>("writable") ?? false)
                    .Select(item => item.Value<string>("name"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                serializedPropertyPaths = serializedProperties
                    .Select(item => JObject.FromObject(item))
                    .Where(item => item.Value<bool?>("editable") ?? false)
                    .Select(item => item.Value<string>("path"))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray(),
                patchExamples = parameters.IncludePatchExamples
                    ? BuildPatchExamples(type, serializedObjectTarget, serializedProperties, parameters).ToArray()
                    : Array.Empty<object>()
            };
        }

        static IEnumerable<object> BuildFieldSchemas(Type type, TypeSchemaParams parameters)
        {
            foreach (var field in EnumerateFields(type, parameters.IncludeInherited))
            {
                if (field.IsStatic)
                    continue;
                if (field.IsDefined(typeof(NonSerializedAttribute), true))
                    continue;
                if (!parameters.IncludeObsolete && field.IsDefined(typeof(ObsoleteAttribute), true))
                    continue;

                var isPublic = field.IsPublic;
                var isSerializeField = field.IsDefined(typeof(SerializeField), true);
                if (!isPublic && (!parameters.IncludePrivateSerialized || !isSerializeField))
                    continue;

                var readOnly = field.IsInitOnly || field.IsLiteral;
                var writable = !readOnly;
                if (!parameters.IncludeReadOnly && !writable)
                    continue;

                yield return new
                {
                    name = field.Name,
                    source = "field",
                    declaringType = field.DeclaringType?.FullName,
                    type = BuildValueTypeSchema(field.FieldType),
                    writable,
                    serializedByUnity = isPublic || isSerializeField,
                    privateSerialized = !isPublic && isSerializeField,
                    readOnly,
                    hidden = field.IsDefined(typeof(HideInInspector), true),
                    attributes = BuildMemberAttributes(field)
                };
            }
        }

        static IEnumerable<object> BuildPropertySchemas(Type type, TypeSchemaParams parameters)
        {
            foreach (var property in EnumerateProperties(type, parameters.IncludeInherited))
            {
                if (property.GetIndexParameters().Length > 0)
                    continue;
                if (!parameters.IncludeObsolete && property.IsDefined(typeof(ObsoleteAttribute), true))
                    continue;

                var setter = property.SetMethod;
                var getter = property.GetMethod;
                var writable = setter != null && setter.IsPublic && !setter.IsStatic;
                var readable = getter != null && getter.IsPublic && !getter.IsStatic;
                if (!parameters.IncludeReadOnly && !writable)
                    continue;

                yield return new
                {
                    name = property.Name,
                    source = "property",
                    declaringType = property.DeclaringType?.FullName,
                    type = BuildValueTypeSchema(property.PropertyType),
                    writable,
                    readable,
                    serializedByUnity = false,
                    readOnly = !writable,
                    attributes = BuildMemberAttributes(property)
                };
            }
        }

        static IEnumerable<object> BuildSerializedPropertySchemas(Object target, TypeSchemaParams parameters)
        {
            var limit = Clamp(parameters.MaxSerializedProperties <= 0 ? DefaultSerializedPropertyLimit : parameters.MaxSerializedProperties, 1, MaxSerializedPropertyLimit);
            var serializedObject = new SerializedObject(target);
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            var count = 0;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (count++ >= limit)
                    yield break;

                yield return BuildSerializedPropertySchema(iterator, parameters.IncludeValues);
            }
        }

        static object BuildSerializedPropertySchema(SerializedProperty property, bool includeValue)
        {
            var isArray = property.isArray && property.propertyType != SerializedPropertyType.String;
            var isFixedBuffer = property.isFixedBuffer;
            return new
            {
                path = property.propertyPath,
                name = property.name,
                displayName = property.displayName,
                aliases = BuildSerializedPropertyAliases(property),
                tooltip = string.IsNullOrWhiteSpace(property.tooltip) ? null : property.tooltip,
                type = property.propertyType.ToString(),
                jsonShape = GetSerializedJsonShape(property),
                editable = property.editable,
                visible = property.hasVisibleChildren || property.propertyType != SerializedPropertyType.Generic,
                depth = property.depth,
                isArray,
                arraySize = isArray ? property.arraySize : (int?)null,
                isFixedBuffer,
                fixedBufferSize = isFixedBuffer ? property.fixedBufferSize : (int?)null,
                enumNames = property.propertyType == SerializedPropertyType.Enum ? property.enumNames : null,
                objectReferenceType = property.propertyType == SerializedPropertyType.ObjectReference || property.propertyType == SerializedPropertyType.ExposedReference
                    ? SerializedPropertyPatcher.GetObjectReferenceTypeName(property)
                    : null,
                managedReference = property.propertyType == SerializedPropertyType.ManagedReference ? new
                {
                    fieldType = property.managedReferenceFieldTypename,
                    currentType = property.managedReferenceFullTypename,
                    hasValue = property.managedReferenceValue != null,
                    id = property.managedReferenceId
                } : null,
                exposedReference = property.propertyType == SerializedPropertyType.ExposedReference,
                patchHint = BuildSerializedPropertyPatchHint(property),
                value = includeValue ? SerializeSerializedPropertyValue(property) : null
            };
        }

        static string[] BuildSerializedPropertyAliases(SerializedProperty property)
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddAlias(property.propertyPath);
            AddAlias(property.name);
            AddAlias(property.displayName);
            AddAlias(CleanSerializedPropertyName(property.name));
            AddAlias(CleanSerializedPropertyName(property.displayName));
            return aliases.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();

            void AddAlias(string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    aliases.Add(value);
            }
        }

        static string CleanSerializedPropertyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;

            var cleaned = name.Trim();
            return cleaned.StartsWith("m_", StringComparison.Ordinal) && cleaned.Length > 2
                ? cleaned.Substring(2)
                : cleaned;
        }

        static object BuildSerializedPropertyPatchHint(SerializedProperty property)
        {
            return new
            {
                preferredPath = property.propertyPath,
                acceptedShapes = GetSerializedAcceptedValueShapes(property),
                sampleValue = SampleValueForSerializedProperty(property.propertyType.ToString(), GetSerializedJsonShape(property), property)
            };
        }

        static string[] GetSerializedAcceptedValueShapes(SerializedProperty property)
        {
            if (property.isFixedBuffer)
                return new[] { "array up to fixedBufferSize elements" };
            if (property.isArray && property.propertyType == SerializedPropertyType.Generic)
                return new[] { "array" };

            return property.propertyType switch
            {
                SerializedPropertyType.Color => new[] { "{r,g,b,a}", "#RRGGBB", "#RRGGBBAA" },
                SerializedPropertyType.Vector2 => new[] { "{x,y}", "[x,y]" },
                SerializedPropertyType.Vector3 => new[] { "{x,y,z}", "[x,y,z]" },
                SerializedPropertyType.Vector4 => new[] { "{x,y,z,w}", "[x,y,z,w]" },
                SerializedPropertyType.Quaternion => new[] { "{x,y,z,w}", "[x,y,z,w]" },
                SerializedPropertyType.Rect or SerializedPropertyType.RectInt => new[] { "{x,y,width,height}", "[x,y,width,height]" },
                SerializedPropertyType.Bounds => new[] { "{center:{x,y,z},size:{x,y,z}}" },
                SerializedPropertyType.BoundsInt => new[] { "{position:{x,y,z},size:{x,y,z}}" },
                SerializedPropertyType.LayerMask => new[] { "integer mask", "Everything", "Nothing", "layer name", "array of layer names" },
                SerializedPropertyType.Enum => new[] { "enum name", "enum display name", "enum index" },
                SerializedPropertyType.ObjectReference or SerializedPropertyType.ExposedReference => new[] { "null", "asset path", "GUID", "instance id", "{assetPath|guid|objectId|find}" },
                SerializedPropertyType.AnimationCurve => new[] { "{keys:[{time,value,inTangent,outTangent,inWeight,outWeight,weightedMode}],preWrapMode,postWrapMode}" },
                SerializedPropertyType.Gradient => new[] { "{mode,colorSpace,colorKeys:[{r,g,b,a,time}],alphaKeys:[{alpha,time}]}" },
                SerializedPropertyType.ManagedReference => new[] { "null", "{type|fullTypeName|assemblyQualifiedName, properties:{...}}" },
                SerializedPropertyType.Hash128 => new[] { "32-character hash string", "{hash|value}" },
                SerializedPropertyType.Character => new[] { "single-character string", "integer char code" },
                _ => new[] { GetSerializedJsonShape(property) }
            };
        }

        static object BuildValueTypeSchema(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            var isArray = type.IsArray;
            var elementType = isArray ? type.GetElementType() : GetEnumerableElementType(type);
            var isList = !isArray && elementType != null && type != typeof(string);

            return new
            {
                name = GetFriendlyTypeName(type),
                fullName = type.FullName,
                assembly = type.Assembly.GetName().Name,
                jsonShape = GetJsonShape(type),
                nullable = !type.IsValueType || Nullable.GetUnderlyingType(type) != null,
                isEnum = underlying.IsEnum,
                enumValues = underlying.IsEnum ? Enum.GetNames(underlying) : null,
                isArray,
                isList,
                elementType = elementType == null ? null : new
                {
                    name = GetFriendlyTypeName(elementType),
                    fullName = elementType.FullName,
                    jsonShape = GetJsonShape(elementType)
                },
                unityObjectReference = typeof(Object).IsAssignableFrom(underlying),
                componentReference = typeof(Component).IsAssignableFrom(underlying),
                gameObjectReference = typeof(GameObject).IsAssignableFrom(underlying),
                unityEvent = typeof(UnityEventBase).IsAssignableFrom(underlying)
            };
        }

        static object BuildShaderSchema(Shader shader, Material material = null)
        {
            var assetPath = AssetDatabase.GetAssetPath(shader);
            var propertyCount = shader.GetPropertyCount();
            var properties = new List<object>();
            for (var i = 0; i < propertyCount; i++)
            {
                var flags = shader.GetPropertyFlags(i);
                var hidden = HasShaderFlag(flags, ShaderPropertyFlags.HideInInspector);
                var type = shader.GetPropertyType(i);
                var name = shader.GetPropertyName(i);
                properties.Add(new
                {
                    index = i,
                    name,
                    displayName = shader.GetPropertyDescription(i),
                    type = type.ToString(),
                    writable = !hidden,
                    hidden,
                    flags = GetShaderFlags(flags),
                    attributes = shader.GetPropertyAttributes(i),
                    defaultValue = GetShaderDefaultValue(shader, i, type),
                    currentValue = material != null ? GetMaterialPropertyValue(material, name, i, type) : null,
                    range = type == ShaderPropertyType.Range ? ToArray(shader.GetPropertyRangeLimits(i)) : null,
                    textureDimension = type == ShaderPropertyType.Texture ? shader.GetPropertyTextureDimension(i).ToString() : null,
                    supportsScaleOffset = type == ShaderPropertyType.Texture && !HasShaderFlag(flags, ShaderPropertyFlags.NoScaleOffset),
                    scaleOffsetProperty = type == ShaderPropertyType.Texture && !HasShaderFlag(flags, ShaderPropertyFlags.NoScaleOffset)
                        ? $"{name}_ST"
                        : null
                });
            }

            return new
            {
                name = shader.name,
                assetPath = string.IsNullOrWhiteSpace(assetPath) ? null : assetPath,
                pathOrName = string.IsNullOrWhiteSpace(assetPath) ? shader.name : assetPath,
                propertyCount,
                properties = properties.ToArray(),
                writableProperties = properties
                    .Select(item => JObject.FromObject(item))
                    .Where(item => item.Value<bool?>("writable") ?? false)
                    .Select(item => item.Value<string>("name"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToArray()
            };
        }

        static object BuildTypeSummary(Type type)
        {
            return new
            {
                name = type.Name,
                friendlyName = GetFriendlyTypeName(type),
                fullName = type.FullName,
                namespaceName = type.Namespace,
                assembly = type.Assembly.GetName().Name,
                baseType = type.BaseType?.FullName,
                kind = ClassifyType(type),
                domainTags = DomainCatalog.GetDomainTagsForType(type),
                isAbstract = type.IsAbstract,
                isGeneric = type.IsGenericType,
                isNested = type.IsNested,
                obsolete = type.GetCustomAttribute<ObsoleteAttribute>()?.Message
            };
        }

        static object BuildUsageHints(Type type)
        {
            return new
            {
                componentPropertyPatch = typeof(Component).IsAssignableFrom(type) ? "UniBridge_ManageGameObject componentProperties" : null,
                scriptableObjectPatch = typeof(ScriptableObject).IsAssignableFrom(type) ? "UniBridge_ManageScriptableObject Properties" : null,
                importerPatch = typeof(AssetImporter).IsAssignableFrom(type) ? "UniBridge_ManageAssetImporter Properties" : null,
                materialShaderPatch = type == typeof(Shader) ? "UniBridge_ManageMaterial Properties.shader.props" : null,
                examples = "Set IncludePatchExamples=true or Action=PatchExamples to get ready-to-call patch payloads for this schema."
            };
        }

        static object BuildContextProfile(Type type, Object target)
        {
            var fullName = type.FullName ?? type.Name;
            var profile = fullName switch
            {
                "UnityEngine.AnimationClip" => "animationClip",
                "UnityEditor.Animations.AnimatorController" => "animatorController",
                "UnityEngine.Timeline.TimelineAsset" => "timelineAsset",
                "UnityEngine.ParticleSystem" => "particleSystem",
                "TMPro.TMP_FontAsset" => "tmpFontAsset",
                "UnityEngine.Audio.AudioMixer" => "audioMixer",
                "UnityEngine.Audio.AudioMixerGroup" => "audioMixerGroup",
                "UnityEngine.Audio.AudioMixerSnapshot" => "audioMixerSnapshot",
                "UnityEngine.Material" => "material",
                "UnityEngine.Texture2D" => "texture2D",
                "UnityEngine.RenderTexture" => "renderTexture",
                "UnityEngine.Sprite" => "sprite",
                "UnityEngine.TerrainLayer" => "terrainLayer",
                "UnityEngine.AvatarMask" => "avatarMask",
                "UnityEngine.ShaderVariantCollection" => "shaderVariantCollection",
                "UnityEngine.UIElements.VisualTreeAsset" => "uiToolkitVisualTreeAsset",
                "UnityEngine.UIElements.StyleSheet" => "uiToolkitStyleSheet",
                "UnityEngine.UIElements.ThemeStyleSheet" => "uiToolkitThemeStyleSheet",
                "UnityEngine.UIElements.PanelSettings" => "uiToolkitPanelSettings",
                "UnityEngine.InputSystem.InputActionAsset" => "inputActionAsset",
                "UnityEngine.U2D.SpriteAtlas" => "spriteAtlas",
                "UnityEngine.Tilemaps.Tile" => "tile",
                "UnityEngine.Video.VideoClip" => "videoClip",
                "UnityEngine.VFX.VisualEffectAsset" => "visualEffectAsset",
                "UnityEngine.Mesh" => "mesh",
                "UnityEngine.Shader" => "shader",
                "UnityEngine.ComputeShader" => "computeShader",
                _ => null
            };

            if (profile == null)
                return null;

            return new
            {
                profile,
                summary = BuildProfileSummary(profile, target),
                guidance = profile switch
                {
                    "animationClip" => "Prefer curves/events/bindings summaries over full serialized dumps.",
                    "animatorController" => "Prefer layers, parameters, states, and transitions over raw child-state serialization.",
                    "timelineAsset" => "Prefer tracks, clips, bindings, duration, and muted/locked flags.",
                    "particleSystem" => "Prefer main/emission/shape/renderer module summaries and capture with AdvanceMs.",
                    "tmpFontAsset" => "Prefer atlas metrics, fallback chain, material, and glyph counts.",
                    "audioMixer" => "Prefer groups, exposed parameters, snapshots, and routing.",
                    "audioMixerGroup" => "Prefer mixer/group routing and effect hints over raw mixer serialization.",
                    "audioMixerSnapshot" => "Prefer mixer/snapshot identity over raw mixer serialization.",
                    "material" => "Prefer shader property schema and assigned textures/colors/floats.",
                    "texture2D" => "Prefer dimensions, importer, alpha/sprite settings, and preview capture.",
                    "renderTexture" => "Prefer dimensions, graphics format, dynamic scale, mip, and random-write settings.",
                    "sprite" => "Prefer rect/pivot/border/ppu plus texture/importer context.",
                    "terrainLayer" => "Prefer texture slots, tile size/offset, metallic/smoothness, and remap ranges.",
                    "avatarMask" => "Prefer humanoid body-part flags and active transform paths.",
                    "shaderVariantCollection" => "Prefer shader/variant counts and author through ManageAsset allowlist.",
                    "uiToolkitVisualTreeAsset" => "Prefer UXML text stats, dependencies, and CaptureUIToolkit for visual inspection.",
                    "uiToolkitStyleSheet" => "Prefer USS text stats and dependencies.",
                    "uiToolkitThemeStyleSheet" => "Prefer theme stylesheet dependencies and linked style assets.",
                    "uiToolkitPanelSettings" => "Prefer scale mode, reference resolution, target texture, theme, and text settings.",
                    "inputActionAsset" => "Prefer maps, actions, bindings, and control schemes over raw JSON.",
                    "spriteAtlas" => "Prefer sprite count and packed sprite references.",
                    "tile" => "Prefer sprite, color, transform, flags, and collider type.",
                    "videoClip" => "Prefer dimensions, duration, frame count/rate, aspect, and audio tracks.",
                    "visualEffectAsset" => "Prefer dependency/exposed-property hints and capture with AdvanceMs.",
                    "mesh" => "Prefer vertex/submesh/bounds/blendshape summaries.",
                    "shader" => "Prefer shader property metadata and material schema.",
                    "computeShader" => "Prefer source/dependency stats and kernel authoring in code.",
                    _ => null
                }
            };
        }

        static object BuildProfileSummary(string profile, Object target)
        {
            if (target == null)
                return null;

            try
            {
                var assetPath = AssetDatabase.GetAssetPath(target);
                var smartProfile = AssetSnapshotSerializer.BuildSmartProfileForObject(target, null, assetPath, 80);
                if (smartProfile != null)
                    return smartProfile;

                switch (profile)
                {
                    case "animationClip" when target is AnimationClip clip:
                        return new
                        {
                            clip.length,
                            clip.frameRate,
                            clip.wrapMode,
                            clip.empty,
                            legacy = clip.legacy,
                            events = AnimationUtility.GetAnimationEvents(clip).Length,
                            curveBindings = AnimationUtility.GetCurveBindings(clip).Length,
                            objectReferenceCurveBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip).Length
                        };
                    case "particleSystem" when target is ParticleSystem particles:
                        return new
                        {
                            mainDuration = particles.main.duration,
                            mainLoop = particles.main.loop,
                            maxParticles = particles.main.maxParticles,
                            emissionEnabled = particles.emission.enabled,
                            shapeEnabled = particles.shape.enabled
                        };
                    case "material" when target is Material material:
                        return new
                        {
                            shader = material.shader != null ? material.shader.name : null,
                            renderQueue = material.renderQueue,
                            enableInstancing = material.enableInstancing,
                            textureProperties = material.GetTexturePropertyNames().Take(40).ToArray()
                        };
                    case "texture2D" when target is Texture2D texture:
                        return new
                        {
                            texture.width,
                            texture.height,
                            texture.format,
                            mipmapCount = texture.mipmapCount,
                            isReadable = texture.isReadable
                        };
                    case "sprite" when target is Sprite sprite:
                        return new
                        {
                            rect = ToObject(sprite.rect),
                            pivot = ToObject(sprite.pivot),
                            border = ToObject(sprite.border),
                            pixelsPerUnit = sprite.pixelsPerUnit,
                            texture = BuildObjectReference(sprite.texture)
                        };
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        static IEnumerable<object> BuildPatchExamples(Type type, Object target, object[] serializedProperties, TypeSchemaParams parameters)
        {
            var limit = Clamp(parameters.ExampleLimit <= 0 ? 6 : parameters.ExampleLimit, 1, 20);
            var toolContext = BuildPatchToolContext(type, target);
            if (toolContext == null)
            {
                yield return new
                {
                    note = "No concrete patch target was supplied. Inspect a GameObject, ScriptableObject asset, or AssetImporter to get executable examples.",
                    type = type.FullName
                };
                yield break;
            }

            var emitted = 0;
            foreach (var property in serializedProperties
                         .Select(item => JObject.FromObject(item))
                         .Where(item => item.Value<bool?>("editable") ?? false)
                         .Where(item => !string.Equals(item.Value<string>("path"), "m_Script", StringComparison.Ordinal))
                         .Where(item => SampleValueForSerializedProperty(item) != null))
            {
                var path = property.Value<string>("path");
                var value = SampleValueForSerializedProperty(property);
                yield return new
                {
                    description = $"Patch serialized property '{path}'.",
                    property = new
                    {
                        path,
                        type = property.Value<string>("type"),
                        jsonShape = property.Value<string>("jsonShape")
                    },
                    tool = toolContext.tool,
                    parameters = BuildPatchExampleParameters(toolContext, path, value)
                };

                emitted++;
                if (emitted >= limit)
                    yield break;
            }

            if (emitted == 0)
            {
                yield return new
                {
                    note = "No editable serialized properties with safe sample values were found for this target.",
                    tool = toolContext.tool
                };
            }
        }

        static PatchToolContext BuildPatchToolContext(Type type, Object target)
        {
            if (target is Component component && component.gameObject != null)
            {
                return new PatchToolContext
                {
                    tool = "UniBridge_ManageGameObject",
                    action = "SetComponentProperty",
                    target = GetGameObjectPath(component.gameObject),
                    componentType = component.GetType().FullName
                };
            }

            if (target is ScriptableObject)
            {
                var path = AssetDatabase.GetAssetPath(target);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return new PatchToolContext
                    {
                        tool = "UniBridge_ManageScriptableObject",
                        action = "SetProperties",
                        assetPath = path
                    };
                }
            }

            if (target is AssetImporter importer)
            {
                return new PatchToolContext
                {
                    tool = "UniBridge_ManageAssetImporter",
                    action = "SetProperties",
                    assetPath = importer.assetPath
                };
            }

            return null;
        }

        static JObject BuildPatchExampleParameters(PatchToolContext context, string path, object value)
        {
            var patch = new JObject { [path] = JToken.FromObject(value) };
            if (context.tool == "UniBridge_ManageGameObject")
            {
                return new JObject
                {
                    ["Action"] = context.action,
                    ["Target"] = context.target,
                    ["ComponentName"] = context.componentType,
                    ["ComponentProperties"] = new JObject
                    {
                        [context.componentType] = patch
                    }
                };
            }

            return new JObject
            {
                ["Action"] = context.action,
                ["Path"] = context.assetPath,
                ["Properties"] = patch,
                ["DryRun"] = true
            };
        }

        static object SampleValueForSerializedProperty(JObject property)
        {
            var type = property.Value<string>("type");
            var shape = property.Value<string>("jsonShape");
            return SampleValueForSerializedProperty(type, shape, null);
        }

        static object SampleValueForSerializedProperty(string type, string shape, SerializedProperty property)
        {
            return type switch
            {
                "Boolean" => true,
                "Integer" => 1,
                "ArraySize" => 1,
                "FixedBufferSize" => 1,
                "Float" => 1f,
                "String" => "New value",
                "Character" => "A",
                "Color" => new { r = 1f, g = 1f, b = 1f, a = 1f },
                "Vector2" => new { x = 0f, y = 0f },
                "Vector3" => new { x = 0f, y = 0f, z = 0f },
                "Vector4" => new { x = 0f, y = 0f, z = 0f, w = 0f },
                "Vector2Int" => new { x = 0, y = 0 },
                "Vector3Int" => new { x = 0, y = 0, z = 0 },
                "Rect" => new { x = 0f, y = 0f, width = 100f, height = 100f },
                "RectInt" => new { x = 0, y = 0, width = 100, height = 100 },
                "Bounds" => new { center = new { x = 0f, y = 0f, z = 0f }, size = new { x = 1f, y = 1f, z = 1f } },
                "BoundsInt" => new { position = new { x = 0, y = 0, z = 0 }, size = new { x = 1, y = 1, z = 1 } },
                "Quaternion" => new { x = 0f, y = 0f, z = 0f, w = 1f },
                "LayerMask" => "Default",
                "Enum" => property != null && property.enumNames != null && property.enumNames.Length > 0
                    ? property.enumNames[Mathf.Clamp(property.enumValueIndex, 0, property.enumNames.Length - 1)]
                    : null,
                "ObjectReference" => null,
                "ExposedReference" => null,
                "AnimationCurve" => new
                {
                    keys = new[]
                    {
                        new { time = 0f, value = 0f, inTangent = 0f, outTangent = 1f },
                        new { time = 1f, value = 1f, inTangent = 1f, outTangent = 0f }
                    },
                    preWrapMode = "Clamp",
                    postWrapMode = "Clamp"
                },
                "Gradient" => new
                {
                    mode = "Blend",
                    colorKeys = new[]
                    {
                        new { r = 1f, g = 1f, b = 1f, a = 1f, time = 0f },
                        new { r = 1f, g = 1f, b = 1f, a = 1f, time = 1f }
                    },
                    alphaKeys = new[]
                    {
                        new { alpha = 1f, time = 0f },
                        new { alpha = 1f, time = 1f }
                    }
                },
                "ManagedReference" => null,
                "Hash128" => "00000000000000000000000000000000",
                _ => shape == "array" ? new object[] { } : null
            };
        }

        sealed class PatchToolContext
        {
            public string tool;
            public string action;
            public string target;
            public string componentType;
            public string assetPath;
        }

        static IEnumerable<Type> EnumerateTypes(TypeSchemaKind kind)
        {
            IEnumerable<Type> types = kind switch
            {
                TypeSchemaKind.Component => TypeCache.GetTypesDerivedFrom<Component>().Concat(new[] { typeof(Transform), typeof(RectTransform) }),
                TypeSchemaKind.MonoBehaviour => TypeCache.GetTypesDerivedFrom<MonoBehaviour>(),
                TypeSchemaKind.ScriptableObject => TypeCache.GetTypesDerivedFrom<ScriptableObject>(),
                TypeSchemaKind.AssetImporter => TypeCache.GetTypesDerivedFrom<AssetImporter>(),
                TypeSchemaKind.Asset => GetAllLoadedTypes().Where(type => typeof(Object).IsAssignableFrom(type) &&
                                                                          !typeof(Component).IsAssignableFrom(type) &&
                                                                          !typeof(UnityEditor.Editor).IsAssignableFrom(type) &&
                                                                          !typeof(EditorWindow).IsAssignableFrom(type) &&
                                                                          type != typeof(GameObject)),
                TypeSchemaKind.Shader => new[] { typeof(Shader) },
                _ => GetAllLoadedTypes().Where(type => typeof(Object).IsAssignableFrom(type) ||
                                                       typeof(AssetImporter).IsAssignableFrom(type) ||
                                                       typeof(UnityEventBase).IsAssignableFrom(type))
            };

            return types
                .Where(type => type != null && type.IsClass)
                .Distinct();
        }

        static IEnumerable<Type> GetAllLoadedTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                    yield return type;
            }
        }

        static Type ResolveType(string name, TypeSchemaKind kind)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var trimmed = name.Trim();
            var direct = Type.GetType(trimmed) ?? Type.GetType(trimmed.Replace('.', '+'));
            if (direct != null && MatchesKind(direct, kind))
                return direct;

            return EnumerateTypes(kind)
                .FirstOrDefault(type =>
                    string.Equals(type.FullName, trimmed, StringComparison.Ordinal) ||
                    string.Equals(type.Name, trimmed, StringComparison.Ordinal) ||
                    string.Equals(type.FullName, trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.FullName?.Replace('+', '.'), trimmed, StringComparison.OrdinalIgnoreCase));
        }

        static bool MatchesKind(Type type, TypeSchemaKind kind)
        {
            return kind switch
            {
                TypeSchemaKind.Component => typeof(Component).IsAssignableFrom(type),
                TypeSchemaKind.MonoBehaviour => typeof(MonoBehaviour).IsAssignableFrom(type),
                TypeSchemaKind.ScriptableObject => typeof(ScriptableObject).IsAssignableFrom(type),
                TypeSchemaKind.AssetImporter => typeof(AssetImporter).IsAssignableFrom(type),
                TypeSchemaKind.Asset => typeof(Object).IsAssignableFrom(type) && !typeof(Component).IsAssignableFrom(type),
                TypeSchemaKind.Shader => type == typeof(Shader),
                _ => true
            };
        }

        static bool MatchesTypeQuery(Type type, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;

            return (type.Name?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                   (type.FullName?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                   (type.Namespace?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                   type.Assembly.GetName().Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static IEnumerable<FieldInfo> EnumerateFields(Type type, bool includeInherited)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = type;
            while (current != null && current != typeof(object))
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (seen.Add(field.Name))
                        yield return field;
                }

                if (!includeInherited)
                    yield break;

                current = current.BaseType;
            }
        }

        static IEnumerable<PropertyInfo> EnumerateProperties(Type type, bool includeInherited)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = type;
            while (current != null && current != typeof(object))
            {
                foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (seen.Add(property.Name))
                        yield return property;
                }

                if (!includeInherited)
                    yield break;

                current = current.BaseType;
            }
        }

        static object BuildMemberAttributes(MemberInfo member)
        {
            var tooltip = member.GetCustomAttribute<TooltipAttribute>();
            var range = member.GetCustomAttribute<RangeAttribute>();
            var min = member.GetCustomAttribute<MinAttribute>();
            var header = member.GetCustomAttribute<HeaderAttribute>();
            var textArea = member.GetCustomAttribute<TextAreaAttribute>();
            var multiline = member.GetCustomAttribute<MultilineAttribute>();

            return new
            {
                tooltip = tooltip?.tooltip,
                range = range == null ? null : new { min = range.min, max = range.max },
                min = min == null ? (float?)null : min.min,
                header = header?.header,
                textArea = textArea == null ? null : new { minLines = textArea.minLines, maxLines = textArea.maxLines },
                multiline = multiline == null ? (int?)null : multiline.lines,
                serializeReference = member.IsDefined(typeof(SerializeReference), true)
            };
        }

        static string ClassifyType(Type type)
        {
            if (type == typeof(Shader))
                return "Shader";
            if (typeof(AssetImporter).IsAssignableFrom(type))
                return "AssetImporter";
            if (typeof(MonoBehaviour).IsAssignableFrom(type))
                return "MonoBehaviour";
            if (typeof(Component).IsAssignableFrom(type))
                return "Component";
            if (typeof(ScriptableObject).IsAssignableFrom(type))
                return "ScriptableObject";
            if (typeof(Object).IsAssignableFrom(type))
                return "Asset";
            if (typeof(UnityEventBase).IsAssignableFrom(type))
                return "UnityEvent";
            return "PlainCSharp";
        }

        static string GetJsonShape(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(string) || underlying == typeof(char))
                return "string";
            if (underlying == typeof(bool))
                return "boolean";
            if (underlying.IsEnum)
                return "string";
            if (IsIntegerType(underlying))
                return "integer";
            if (IsNumberType(underlying))
                return "number";
            if (underlying.IsArray || (GetEnumerableElementType(underlying) != null && underlying != typeof(string)))
                return "array";
            if (typeof(Object).IsAssignableFrom(underlying))
                return "objectReference";
            if (underlying == typeof(Vector2) || underlying == typeof(Vector2Int) ||
                underlying == typeof(Vector3) || underlying == typeof(Vector3Int) ||
                underlying == typeof(Vector4) || underlying == typeof(Quaternion) ||
                underlying == typeof(Color) || underlying == typeof(Color32) ||
                underlying == typeof(Rect) || underlying == typeof(RectInt) ||
                underlying == typeof(Bounds) || underlying == typeof(BoundsInt) ||
                underlying == typeof(AnimationCurve) || underlying == typeof(Gradient))
                return "object";
            return "object";
        }

        static string GetSerializedJsonShape(SerializedProperty property)
        {
            if (property.isFixedBuffer)
                return "array";
            if (property.isArray && property.propertyType == SerializedPropertyType.Generic)
                return "array";

            return property.propertyType switch
            {
                SerializedPropertyType.Generic => "object",
                SerializedPropertyType.Integer => "integer",
                SerializedPropertyType.Boolean => "boolean",
                SerializedPropertyType.Float => "number",
                SerializedPropertyType.String => "string",
                SerializedPropertyType.Color => "object",
                SerializedPropertyType.ObjectReference => "objectReference",
                SerializedPropertyType.LayerMask => "namedMask",
                SerializedPropertyType.Enum => "string",
                SerializedPropertyType.Vector2 => "object",
                SerializedPropertyType.Vector3 => "object",
                SerializedPropertyType.Vector4 => "object",
                SerializedPropertyType.Rect => "object",
                SerializedPropertyType.ArraySize => "integer",
                SerializedPropertyType.Character => "string",
                SerializedPropertyType.AnimationCurve => "object",
                SerializedPropertyType.Bounds => "object",
                SerializedPropertyType.Gradient => "object",
                SerializedPropertyType.Quaternion => "object",
                SerializedPropertyType.ExposedReference => "objectReference",
                SerializedPropertyType.FixedBufferSize => "integer",
                SerializedPropertyType.ManagedReference => "managedReference",
                SerializedPropertyType.Vector2Int => "object",
                SerializedPropertyType.Vector3Int => "object",
                SerializedPropertyType.RectInt => "object",
                SerializedPropertyType.BoundsInt => "object",
                SerializedPropertyType.Hash128 => "string",
                _ => property.isArray ? "array" : "object"
            };
        }

        static object SerializeSerializedPropertyValue(SerializedProperty property)
        {
            try
            {
                return SerializedPropertyPatcher.SerializePropertyValue(property);
            }
            catch
            {
                return null;
            }
        }

        static object GetShaderDefaultValue(Shader shader, int index, ShaderPropertyType type)
        {
            return type switch
            {
                ShaderPropertyType.Color => ToObject((Color)shader.GetPropertyDefaultVectorValue(index)),
                ShaderPropertyType.Vector => ToObject(shader.GetPropertyDefaultVectorValue(index)),
                ShaderPropertyType.Float => shader.GetPropertyDefaultFloatValue(index),
                ShaderPropertyType.Range => shader.GetPropertyDefaultFloatValue(index),
                ShaderPropertyType.Texture => shader.GetPropertyTextureDefaultName(index),
                ShaderPropertyType.Int => shader.GetPropertyDefaultIntValue(index),
                _ => null
            };
        }

        static object GetMaterialPropertyValue(Material material, string propertyName, int index, ShaderPropertyType type)
        {
            if (material == null || !material.HasProperty(propertyName))
                return null;

            try
            {
                return type switch
                {
                    ShaderPropertyType.Color => ToObject(material.GetColor(propertyName)),
                    ShaderPropertyType.Vector => ToObject(material.GetVector(propertyName)),
                    ShaderPropertyType.Float => material.GetFloat(propertyName),
                    ShaderPropertyType.Range => material.GetFloat(propertyName),
                    ShaderPropertyType.Texture => BuildObjectReference(material.GetTexture(propertyName)),
                    ShaderPropertyType.Int => material.GetInt(propertyName),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        static List<string> GetShaderFlags(ShaderPropertyFlags flags)
        {
            var result = new List<string>();
            foreach (ShaderPropertyFlags value in Enum.GetValues(typeof(ShaderPropertyFlags)))
            {
                if (value == 0)
                    continue;
                if (HasShaderFlag(flags, value))
                    result.Add(value.ToString());
            }

            return result;
        }

        static bool HasShaderFlag(ShaderPropertyFlags flags, ShaderPropertyFlags flag)
        {
            return (((int)flags) & (int)flag) != 0;
        }

        static object BuildObjectReference(Object obj)
        {
            if (obj == null)
                return null;

            var path = AssetDatabase.GetAssetPath(obj);
            return new
            {
                name = obj.name,
                type = obj.GetType().FullName,
                instanceId = GetObjectInstanceId(obj),
                assetPath = string.IsNullOrWhiteSpace(path) ? null : path,
                guid = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.AssetPathToGUID(path)
            };
        }

#pragma warning disable CS0618
        static int GetObjectInstanceId(Object obj) => obj == null ? 0 : obj.GetInstanceID();
#pragma warning restore CS0618

        static Shader ResolveShader(TypeSchemaParams parameters, out object source)
        {
            source = null;
            var shaderInput = parameters.Shader;

            if (string.IsNullOrWhiteSpace(shaderInput) && TryResolveAssetPath(parameters, out var assetPath, out _))
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                if (shader != null)
                {
                    source = new { kind = "assetPath", value = assetPath };
                    return shader;
                }

                var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (material != null && material.shader != null)
                {
                    source = new { kind = "material", value = assetPath };
                    return material.shader;
                }
            }

            if (string.IsNullOrWhiteSpace(shaderInput))
                return null;

            shaderInput = NormalizeAssetPath(shaderInput);
            if ((shaderInput.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                 shaderInput.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) &&
                AssetDatabase.LoadAssetAtPath<Shader>(shaderInput) is Shader shaderFromPath)
            {
                source = new { kind = "assetPath", value = shaderInput };
                return shaderFromPath;
            }

            var found = Shader.Find(shaderInput);
            if (found != null)
                source = new { kind = "shaderName", value = shaderInput };
            return found;
        }

        static bool TryResolveAssetPath(TypeSchemaParams parameters, out string assetPath, out string error)
        {
            assetPath = null;
            error = null;

            if (!string.IsNullOrWhiteSpace(parameters.Guid))
            {
                assetPath = AssetDatabase.GUIDToAssetPath(parameters.Guid.Trim());
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    error = $"Asset GUID '{parameters.Guid}' was not found.";
                    return false;
                }

                return true;
            }

            if (string.IsNullOrWhiteSpace(parameters.Path))
            {
                error = "Asset path or GUID is required.";
                return false;
            }

            assetPath = NormalizeAssetPath(parameters.Path);
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Path '{parameters.Path}' must resolve under Assets/... or Packages/....";
                return false;
            }

            if (AssetImporter.GetAtPath(assetPath) == null && AssetDatabase.LoadMainAssetAtPath(assetPath) == null)
            {
                error = $"Asset was not found at '{assetPath}'.";
                return false;
            }

            return true;
        }

        static string NormalizeAssetPath(string path)
        {
            var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
            if (normalized.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(1);
            if (normalized.StartsWith("/Packages/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(1);

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(projectRoot) &&
                normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(projectRoot.Length).TrimStart('/');
            }

            return normalized;
        }

        static List<string> CollectTypeNames(TypeSchemaParams parameters)
        {
            var result = new List<string>();
            if (!string.IsNullOrWhiteSpace(parameters.TypeName))
                result.Add(parameters.TypeName.Trim());
            if (parameters.TypeNames != null)
            {
                foreach (var name in parameters.TypeNames)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        result.Add(name.Trim());
                }
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static List<string> NormalizeStrings(string[] values)
        {
            return values == null
                ? new List<string>()
                : values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        static bool MatchesComponentFilter(Component component, string filter)
        {
            var type = component.GetType();
            return string.Equals(type.Name, filter, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type.FullName, filter, StringComparison.OrdinalIgnoreCase) ||
                   ComponentIdentity.Matches(component, filter);
        }

        static Type GetEnumerableElementType(Type type)
        {
            if (type == typeof(string))
                return null;
            if (type.IsArray)
                return type.GetElementType();
            if (type.IsGenericType &&
                (type.GetGenericTypeDefinition() == typeof(List<>) ||
                 type.GetGenericTypeDefinition() == typeof(IList<>) ||
                 type.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                return type.GetGenericArguments()[0];
            var enumerable = type.GetInterfaces()
                .FirstOrDefault(interfaceType => interfaceType.IsGenericType &&
                                                 interfaceType.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return enumerable?.GetGenericArguments()[0];
        }

        static bool IsIntegerType(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) ||
                   type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) ||
                   type == typeof(long) || type == typeof(ulong);
        }

        static bool IsNumberType(Type type)
        {
            return IsIntegerType(type) || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }

        static string GetFriendlyTypeName(Type type)
        {
            if (type == null)
                return null;
            if (!type.IsGenericType)
                return type.Name;

            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
                name = name.Substring(0, tick);
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName))}>";
        }

        static string GetGameObjectPath(GameObject gameObject)
        {
            if (gameObject == null)
                return null;

            var names = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        static object ToArray(Vector2 value) => new[] { value.x, value.y };
        static object ToArray(Vector3 value) => new[] { value.x, value.y, value.z };
        static object ToArray(Vector4 value) => new[] { value.x, value.y, value.z, value.w };
        static object ToObject(Vector2 value) => new { x = value.x, y = value.y };
        static object ToObject(Vector2Int value) => new { x = value.x, y = value.y };
        static object ToObject(Vector3 value) => new { x = value.x, y = value.y, z = value.z };
        static object ToObject(Vector3Int value) => new { x = value.x, y = value.y, z = value.z };
        static object ToObject(Vector4 value) => new { x = value.x, y = value.y, z = value.z, w = value.w };
        static object ToObject(Quaternion value) => new { x = value.x, y = value.y, z = value.z, w = value.w };
        static object ToObject(Color value) => new { r = value.r, g = value.g, b = value.b, a = value.a };
        static object ToObject(Rect value) => new { x = value.x, y = value.y, width = value.width, height = value.height };
        static object ToObject(RectInt value) => new { x = value.x, y = value.y, width = value.width, height = value.height };
        static object ToObject(Bounds value) => new { center = ToObject(value.center), size = ToObject(value.size) };
        static object ToObject(BoundsInt value) => new { position = ToObject(value.position), size = ToObject(value.size) };
        static object ToObject(AnimationCurve value) => value == null ? null : new
        {
            keys = value.keys.Select(key => new
            {
                key.time,
                key.value,
                key.inTangent,
                key.outTangent,
                key.inWeight,
                key.outWeight,
                weightedMode = key.weightedMode.ToString()
            }).ToArray(),
            preWrapMode = value.preWrapMode.ToString(),
            postWrapMode = value.postWrapMode.ToString()
        };
        static object ToObject(Gradient value) => value == null ? null : new
        {
            mode = value.mode.ToString(),
            colorSpace = GetGradientColorSpace(value),
            colorKeys = value.colorKeys.Select(key => new
            {
                key.time,
                r = key.color.r,
                g = key.color.g,
                b = key.color.b,
                a = key.color.a
            }).ToArray(),
            alphaKeys = value.alphaKeys.Select(key => new
            {
                key.time,
                key.alpha
            }).ToArray()
        };

        static object GetGradientColorSpace(Gradient gradient)
        {
            var property = typeof(Gradient).GetProperty("colorSpace", BindingFlags.Instance | BindingFlags.Public);
            return property != null ? property.GetValue(gradient)?.ToString() : null;
        }

        static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }
}
