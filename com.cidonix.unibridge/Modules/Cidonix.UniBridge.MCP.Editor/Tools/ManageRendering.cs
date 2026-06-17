#nullable disable
#pragma warning disable 0618 // LightProbeProxyVolume is deprecated in Unity 6000.5, but UniBridge still supports existing projects that use it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Camera, lighting, and rendering presets for making scenes immediately inspectable.
    /// </summary>
    public static class ManageRendering
    {
        const string ToolName = "UniBridge_ManageRendering";

        public const string Title = "Manage rendering presets";

        public const string Description = @"Author Camera, Light, RenderSettings, 2D rendering helpers, light rigs, optional Volume components, and rendering extras.

Use this when a scene needs a usable gameplay camera, 2D/isometric camera, pixel-perfect setup, 2D lights/shadows, product preview rig, simple three-point lighting, render settings, a Volume/VolumeProfile asset, decals, lens flares, projector effects, wind, LOD groups, or light/reflection probe setup before visual capture.

Args:
    Action: Inspect, ApplyPreset, CreateCamera, CreateLight, CreateVolume, CreateRig, SetupSceneLighting, AddPixelPerfectCamera, AddLight2D, AddShadowCaster2D, AddSpriteShapeRenderer, Setup2DScene, AddDecalProjector, AddLensFlare, AddFlareLayer, AddWindZone, AddProjector, AddLODGroup, AddReflectionProbe, AddLightProbeGroup, or AddLightProbeProxyVolume.
    Target: Optional GameObject to update; otherwise a new GameObject is created.
    Name, Parent, Position, Rotation, Orthographic, OrthographicSize, FieldOfView, ClearFlags, BackgroundColor, CullingMask: Camera controls.
    LightType, Color, Intensity, Range, SpotAngle, Shadows, RenderingLayerMask: Light controls.
    Preset: 2DGameCamera, IsometricCamera, Gameplay3DCamera, ProductPreviewRig, Bright2D, PixelPerfect2D, Cinematic3Point.
    AssetsPPU, ReferenceResolution, UpscaleRT, PixelSnapping, CropFrameX/Y, StretchFill: PixelPerfectCamera controls.
    Light2DType, OuterRadius, InnerRadius, BlendStyleIndex, CastsShadows, SelfShadows: 2D light/shadow controls.
    SortingLayerName, SortingOrder, RenderingLayerMask: renderer-based 2D controls.
    VolumeProfilePath, IsGlobal, Weight, Priority: Volume controls.
    Material/MaterialPath, DrawDistance, FadeScale, Size, Pivot, UvScale, UvBias: DecalProjector/projector controls.
    FlareAssetPath/LensFlareDataPath, Brightness, FadeSpeed, UseOcclusion, Scale, AllowOffScreen: lens flare controls.
    Mode, Radius, WindMain, WindTurbulence, WindPulseMagnitude, WindPulseFrequency: WindZone controls.
    Lods/LODLevels/Levels, Renderers, UseChildRenderers, LODSize, LocalReferencePoint, FadeMode, AnimateCrossFading, LastLODBillboard, RecalculateBounds: LODGroup controls.
    ProbePositions, ProbeLayout, Dering, Tetrahedralize: LightProbeGroup controls.
    ProbeDensity, GridResolutionX/Y/Z, QualityMode, DataFormat, BoundingBoxMode, OriginCustom, SizeCustom, ProbePositionMode: LightProbeProxyVolume controls.
    Mode, RefreshMode, TimeSlicingMode, RenderDynamicObjects, Importance, Intensity, BoxProjection, BlendDistance, Resolution, Hdr, ShadowDistance, ClearFlags, BackgroundColor, CullingMask, OcclusionCulling, NearClipPlane, FarClipPlane: ReflectionProbe controls.
    AmbientColor, Fog, FogColor, FogDensity: scene lighting controls.
    Properties: Optional extra SerializedProperty/public-property patches for the created/updated component.

Returns:
    success, message, and data with created/updated rendering component summaries.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new { type = "string", @enum = new[] { "Inspect", "ApplyPreset", "CreateCamera", "CreateLight", "CreateVolume", "CreateRig", "SetupSceneLighting", "AddPixelPerfectCamera", "AddLight2D", "AddShadowCaster2D", "AddSpriteShapeRenderer", "Setup2DScene", "AddDecalProjector", "AddLensFlare", "AddFlareLayer", "AddWindZone", "AddProjector", "AddLODGroup", "AddReflectionProbe", "AddLightProbeGroup", "AddLightProbeProxyVolume" } },
                    Target = new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    Name = new { type = "string" },
                    Parent = new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    Preset = new { type = "string" },
                    Position = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    Rotation = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    Orthographic = new { type = "boolean" },
                    OrthographicSize = new { type = "number" },
                    FieldOfView = new { type = "number" },
                    ClearFlags = new { type = "string" },
                    BackgroundColor = new { description = "Color object/array/html string." },
                    CullingMask = new { description = "Layer mask integer or array of layer names." },
                    LightType = new { type = "string" },
                    Color = new { description = "Color object/array/html string." },
                    Intensity = new { type = "number" },
                    Range = new { type = "number" },
                    SpotAngle = new { type = "number" },
                    Shadows = new { type = "string" },
                    RenderingLayerMask = new { description = "Rendering layer mask as integer, 'Everything', 'Nothing', a rendering layer name, or an array/object of names." },
                    VolumeProfilePath = new { type = "string" },
                    IsGlobal = new { type = "boolean" },
                    Weight = new { type = "number" },
                    Priority = new { type = "number" },
                    AmbientColor = new { description = "Color object/array/html string." },
                    Fog = new { type = "boolean" },
                    FogColor = new { description = "Color object/array/html string." },
                    FogDensity = new { type = "number" },
                    AssetsPPU = new { type = "integer" },
                    ReferenceResolution = new { type = "array", items = new { type = "integer" }, minItems = 2, maxItems = 2 },
                    UpscaleRT = new { type = "boolean" },
                    PixelSnapping = new { type = "boolean" },
                    CropFrameX = new { type = "boolean" },
                    CropFrameY = new { type = "boolean" },
                    StretchFill = new { type = "boolean" },
                    Light2DType = new { type = "string" },
                    OuterRadius = new { type = "number" },
                    InnerRadius = new { type = "number" },
                    BlendStyleIndex = new { type = "integer" },
                    CastsShadows = new { type = "boolean" },
                    SelfShadows = new { type = "boolean" },
                    SortingLayerName = new { type = "string" },
                    SortingOrder = new { type = "integer" },
                    Material = new { description = "Material asset reference path/guid/object id for DecalProjector or Projector." },
                    MaterialPath = new { type = "string" },
                    DrawDistance = new { type = "number" },
                    FadeScale = new { type = "number" },
                    StartAngleFade = new { type = "number" },
                    EndAngleFade = new { type = "number" },
                    UvScale = new { type = "array", items = new { type = "number" }, minItems = 2, maxItems = 2 },
                    UvBias = new { type = "array", items = new { type = "number" }, minItems = 2, maxItems = 2 },
                    ScaleMode = new { type = "string" },
                    Pivot = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    Size = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    LensFlareDataPath = new { type = "string" },
                    FlareAssetPath = new { type = "string" },
                    Brightness = new { type = "number" },
                    FadeSpeed = new { type = "number" },
                    UseOcclusion = new { type = "boolean" },
                    EnvironmentOcclusion = new { type = "boolean" },
                    OcclusionRadius = new { type = "number" },
                    SampleCount = new { type = "integer" },
                    MaxAttenuationDistance = new { type = "number" },
                    MaxAttenuationScale = new { type = "number" },
                    AllowOffScreen = new { type = "boolean" },
                    AspectRatio = new { type = "number" },
                    OrthographicSizeProjector = new { type = "number" },
                    IgnoreLayers = new { description = "Layer mask integer, Everything/Nothing, layer name, or array/object of layer names for legacy Projector." },
                    Mode = new { type = "string" },
                    Radius = new { type = "number" },
                    WindMain = new { type = "number" },
                    WindTurbulence = new { type = "number" },
                    WindPulseMagnitude = new { type = "number" },
                    WindPulseFrequency = new { type = "number" },
                    Lods = new { type = "array", description = "LOD levels. Each entry accepts ScreenRelativeTransitionHeight/Height, FadeTransitionWidth, Renderers, and IncludeChildren." },
                    LODLevels = new { type = "array", description = "Alias for Lods." },
                    Levels = new { type = "array", description = "Alias for Lods." },
                    Renderers = new { description = "Renderer/GameObject references for a single generated LOD or per-LOD entries." },
                    UseChildRenderers = new { type = "boolean" },
                    LODSize = new { type = "number" },
                    LocalReferencePoint = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    FadeMode = new { type = "string", @enum = new[] { "None", "CrossFade", "SpeedTree" } },
                    AnimateCrossFading = new { type = "boolean" },
                    LastLODBillboard = new { type = "boolean" },
                    RecalculateBounds = new { type = "boolean" },
                    ProbePositions = new { type = "array", description = "Light probe local positions as Vector3 arrays/objects." },
                    ProbeLayout = new { type = "string", @enum = new[] { "Box", "Tetrahedron" } },
                    Dering = new { type = "boolean" },
                    Tetrahedralize = new { type = "boolean" },
                    RenderDynamicObjects = new { type = "boolean" },
                    CustomBakedTexture = new { description = "Texture asset reference for ReflectionProbe custom baked texture." },
                    CustomBakedTexturePath = new { type = "string" },
                    Importance = new { type = "integer" },
                    BoxProjection = new { type = "boolean" },
                    BlendDistance = new { type = "number" },
                    Resolution = new { type = "integer" },
                    Hdr = new { type = "boolean" },
                    ShadowDistance = new { type = "number" },
                    OcclusionCulling = new { type = "boolean" },
                    NearClipPlane = new { type = "number" },
                    FarClipPlane = new { type = "number" },
                    TimeSlicingMode = new { type = "string" },
                    QualityMode = new { type = "string" },
                    DataFormat = new { type = "string" },
                    BoundingBoxMode = new { type = "string" },
                    SizeCustom = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    OriginCustom = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    ResolutionMode = new { type = "string" },
                    ProbeDensity = new { type = "number" },
                    GridResolutionX = new { type = "integer" },
                    GridResolutionY = new { type = "integer" },
                    GridResolutionZ = new { type = "integer" },
                    ProbePositionMode = new { type = "string" },
                    Properties = new { type = "object", additionalProperties = true }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "rendering", "camera", "lighting" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = Normalize(GetString(parameters, "Action", "action") ?? "Inspect");
            try
            {
                return action switch
                {
                    "inspect" => Inspect(parameters),
                    "applypreset" or "preset" => ApplyPreset(parameters),
                    "createcamera" or "camera" => CreateCamera(parameters),
                    "createlight" or "light" => CreateLight(parameters),
                    "createvolume" or "volume" => CreateVolume(parameters),
                    "createrig" or "rig" or "lightrig" => CreateRig(parameters),
                    "setupscenelighting" or "scenelighting" or "rendersettings" => SetupSceneLighting(parameters),
                    "addpixelperfectcamera" or "pixelperfectcamera" or "pixelperfect" => AddPixelPerfectCamera(parameters),
                    "addlight2d" or "light2d" => AddLight2D(parameters),
                    "addshadowcaster2d" or "shadowcaster2d" => AddShadowCaster2D(parameters),
                    "addspriteshaperenderer" or "spriteshaperenderer" => AddSpriteShapeRenderer(parameters),
                    "setup2dscene" or "setup2drendering" or "2drendering" => Setup2DScene(parameters),
                    "adddecalprojector" or "decalprojector" or "decal" => AddDecalProjector(parameters),
                    "addlensflare" or "lensflare" or "flare" => AddLensFlare(parameters),
                    "addflarelayer" or "flarelayer" => AddFlareLayer(parameters),
                    "addwindzone" or "windzone" or "wind" => AddWindZone(parameters),
                    "addprojector" or "projector" => AddProjector(parameters),
                    "addlodgroup" or "lodgroup" or "lod" => AddLODGroup(parameters),
                    "addreflectionprobe" or "reflectionprobe" or "reflprobe" => AddReflectionProbe(parameters),
                    "addlightprobegroup" or "lightprobegroup" or "probegrid" => AddLightProbeGroup(parameters),
                    "addlightprobeproxyvolume" or "lightprobeproxyvolume" or "probeproxyvolume" or "lppv" => AddLightProbeProxyVolume(parameters),
                    _ => Response.Error($"Unknown Rendering action '{GetString(parameters, "Action", "action")}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageRendering] Action '{action}' failed: {ex}");
                return Response.Error($"Rendering action '{action}' failed: {ex.Message}");
            }
        }

        static object Inspect(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: false);
            if (target == null)
            {
                var objects = SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true })
                    .Where(HasRenderingComponent)
                    .Select(BuildRenderingSummary)
                    .ToArray();
                return Response.Success("Listed rendering objects.", new { count = objects.Length, objects, renderSettings = BuildRenderSettingsSummary() });
            }

            return Response.Success("Inspected rendering object.", new { objectSummary = BuildRenderingSummary(target), renderSettings = BuildRenderSettingsSummary() });
        }

        static object ApplyPreset(JObject parameters)
        {
            var preset = Normalize(GetString(parameters, "Preset", "preset") ?? "2DGameCamera");
            return preset switch
            {
                "2dgamecamera" or "game2dcamera" or "orthographic2d" => Apply2DCameraPreset(parameters),
                "isometriccamera" or "iso" => ApplyIsometricCameraPreset(parameters),
                "gameplay3dcamera" or "3dgamecamera" => ApplyGameplay3DCameraPreset(parameters),
                "productpreviewrig" or "previewrig" => CreateProductPreviewRig(parameters),
                "bright2d" => ApplyBright2DPreset(parameters),
                "pixelperfect2d" or "pixelperfect" => Setup2DScene(parameters),
                "cinematic3point" or "threepoint" or "3point" => CreateThreePointLighting(parameters),
                _ => Response.Error($"Unknown Rendering preset '{GetString(parameters, "Preset", "preset")}'.")
            };
        }

        static object Apply2DCameraPreset(JObject parameters)
        {
            parameters["Orthographic"] ??= true;
            parameters["OrthographicSize"] ??= 5f;
            parameters["Position"] ??= new JArray(0f, 0f, -10f);
            parameters["Rotation"] ??= new JArray(0f, 0f, 0f);
            parameters["ClearFlags"] ??= "SolidColor";
            parameters["BackgroundColor"] ??= "#0B0F16";
            parameters["Name"] ??= "UniBridge 2D Camera";
            return CreateCamera(parameters);
        }

        static object ApplyIsometricCameraPreset(JObject parameters)
        {
            parameters["Orthographic"] ??= true;
            parameters["OrthographicSize"] ??= 8f;
            parameters["Position"] ??= new JArray(8f, 8f, -8f);
            parameters["Rotation"] ??= new JArray(35f, 45f, 0f);
            parameters["ClearFlags"] ??= "Skybox";
            parameters["Name"] ??= "UniBridge Isometric Camera";
            return CreateCamera(parameters);
        }

        static object ApplyGameplay3DCameraPreset(JObject parameters)
        {
            parameters["Orthographic"] ??= false;
            parameters["FieldOfView"] ??= 55f;
            parameters["Position"] ??= new JArray(0f, 3f, -8f);
            parameters["Rotation"] ??= new JArray(18f, 0f, 0f);
            parameters["ClearFlags"] ??= "Skybox";
            parameters["Name"] ??= "UniBridge Gameplay Camera";
            return CreateCamera(parameters);
        }

        static object ApplyBright2DPreset(JObject parameters)
        {
            var cameraResult = Apply2DCameraPreset(parameters);
            var lightingResult = SetupSceneLighting(new JObject
            {
                ["AmbientColor"] = "#FFFFFF",
                ["Fog"] = false
            });
            return Response.Success("Bright 2D rendering preset applied.", new { cameraResult, lightingResult });
        }

        static object CreateCamera(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "UniBridge Camera");
            var camera = target.GetComponent<Camera>();
            if (camera == null)
                camera = Undo.AddComponent<Camera>(target);

            Undo.RecordObject(target.transform, "Configure Camera Transform");
            target.transform.localPosition = ParseVector3(GetToken(parameters, "Position", "position")) ?? target.transform.localPosition;
            target.transform.localEulerAngles = ParseVector3(GetToken(parameters, "Rotation", "rotation", "Euler", "euler")) ?? target.transform.localEulerAngles;
            target.transform.localScale = ParseVector3(GetToken(parameters, "Scale", "scale")) ?? target.transform.localScale;
            AssignParent(target, parameters);

            Undo.RecordObject(camera, "Configure Camera");
            camera.orthographic = GetBool(parameters, camera.orthographic, "Orthographic", "orthographic", "ortho");
            camera.orthographicSize = GetFloat(parameters, camera.orthographicSize, "OrthographicSize", "orthographicSize", "orthographic_size");
            camera.fieldOfView = GetFloat(parameters, camera.fieldOfView, "FieldOfView", "fieldOfView", "field_of_view", "Fov", "fov");
            camera.nearClipPlane = GetFloat(parameters, camera.nearClipPlane, "NearClip", "nearClip", "near_clip", "NearClipPlane", "nearClipPlane");
            camera.farClipPlane = GetFloat(parameters, camera.farClipPlane, "FarClip", "farClip", "far_clip", "FarClipPlane", "farClipPlane");
            camera.depth = GetFloat(parameters, camera.depth, "Depth", "depth");
            camera.clearFlags = ParseEnum(GetString(parameters, "ClearFlags", "clearFlags", "clear_flags"), camera.clearFlags);
            camera.backgroundColor = ParseColor(GetToken(parameters, "BackgroundColor", "backgroundColor", "background_color"), camera.backgroundColor);
            var cullingMask = ReadLayerMask(GetToken(parameters, "CullingMask", "cullingMask", "culling_mask"), camera.cullingMask);
            camera.cullingMask = cullingMask;
            ApplyExtraProperties(camera, parameters);
            AddOptionalComponent(target, parameters, "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            EditorUtility.SetDirty(camera);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Camera created or updated.", BuildRenderingSummary(target));
        }

        static object CreateLight(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "UniBridge Light");
            var light = target.GetComponent<Light>();
            if (light == null)
                light = Undo.AddComponent<Light>(target);

            Undo.RecordObject(target.transform, "Configure Light Transform");
            target.transform.localPosition = ParseVector3(GetToken(parameters, "Position", "position")) ?? target.transform.localPosition;
            target.transform.localEulerAngles = ParseVector3(GetToken(parameters, "Rotation", "rotation", "Euler", "euler")) ?? target.transform.localEulerAngles;
            AssignParent(target, parameters);

            Undo.RecordObject(light, "Configure Light");
            light.type = ParseEnum(GetString(parameters, "LightType", "lightType", "light_type", "Type", "type"), light.type);
            light.color = ParseColor(GetToken(parameters, "Color", "color", "Tint", "tint"), light.color);
            light.intensity = GetFloat(parameters, light.intensity, "Intensity", "intensity");
            light.range = GetFloat(parameters, light.range, "Range", "range");
            light.spotAngle = GetFloat(parameters, light.spotAngle, "SpotAngle", "spotAngle", "spot_angle");
            light.shadows = ParseEnum(GetString(parameters, "Shadows", "shadows"), light.shadows);
            ApplyRenderingLayerMaskIfPresent(light, parameters);
            ApplyExtraProperties(light, parameters);
            AddOptionalComponent(target, parameters, "UnityEngine.Rendering.Universal.UniversalAdditionalLightData");
            EditorUtility.SetDirty(light);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Light created or updated.", BuildRenderingSummary(target));
        }

        static object CreateVolume(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "UniBridge Volume");
            var volumeType = FindType("UnityEngine.Rendering.Volume");
            if (volumeType == null)
                return Response.Error("UnityEngine.Rendering.Volume is unavailable in this Unity project.");

            var volume = target.GetComponent(volumeType);
            if (volume == null)
                volume = Undo.AddComponent(target, volumeType);

            Undo.RecordObject(volume, "Configure Volume");
            var profilePath = NormalizeAssetPath(GetString(parameters, "VolumeProfilePath", "volumeProfilePath", "volume_profile_path", "ProfilePath", "profilePath"));
            if (!string.IsNullOrWhiteSpace(profilePath))
                AssignVolumeProfile(volume, profilePath);
            ApplyExtraProperties(volume, parameters, BuildVolumeProperties(parameters));
            EditorUtility.SetDirty(volume);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Volume created or updated.", BuildRenderingSummary(target));
        }

        static object CreateRig(JObject parameters)
        {
            var preset = Normalize(GetString(parameters, "Preset", "preset") ?? "Cinematic3Point");
            return preset switch
            {
                "productpreviewrig" or "previewrig" => CreateProductPreviewRig(parameters),
                _ => CreateThreePointLighting(parameters)
            };
        }

        static object SetupSceneLighting(JObject parameters)
        {
            var ambient = ParseColor(GetToken(parameters, "AmbientColor", "ambientColor", "ambient_color"), RenderSettings.ambientLight);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = ambient;
            RenderSettings.fog = GetBool(parameters, RenderSettings.fog, "Fog", "fog");
            RenderSettings.fogColor = ParseColor(GetToken(parameters, "FogColor", "fogColor", "fog_color"), RenderSettings.fogColor);
            RenderSettings.fogDensity = GetFloat(parameters, RenderSettings.fogDensity, "FogDensity", "fogDensity", "fog_density");
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            return Response.Success("Scene lighting settings updated.", BuildRenderSettingsSummary());
        }

        static object Setup2DScene(JObject parameters)
        {
            parameters["Name"] ??= "UniBridge 2D Camera";
            parameters["Orthographic"] ??= true;
            parameters["OrthographicSize"] ??= 5f;
            parameters["Position"] ??= new JArray(0f, 0f, -10f);
            parameters["Rotation"] ??= new JArray(0f, 0f, 0f);
            parameters["ClearFlags"] ??= "SolidColor";
            parameters["BackgroundColor"] ??= "#0B0F16";

            var created = new List<object>();
            var cameraResult = JObject.FromObject(CreateCamera(parameters));
            created.Add(cameraResult["data"]);

            var cameraName = GetString(parameters, "Name", "name") ?? "UniBridge 2D Camera";
            var pixelParams = (JObject)parameters.DeepClone();
            pixelParams["Target"] = cameraName;
            var ppcResult = JObject.FromObject(AddPixelPerfectCamera(pixelParams));
            created.Add(ppcResult["data"]);

            if (FindType("UnityEngine.Rendering.Universal.Light2D", "Light2D") != null)
            {
                var lightResult = JObject.FromObject(AddLight2D(new JObject
                {
                    ["Name"] = "Global Light 2D",
                    ["Light2DType"] = "Global",
                    ["Intensity"] = GetFloat(parameters, 1f, "Intensity", "intensity"),
                    ["Color"] = GetToken(parameters, "Color", "color") ?? JToken.FromObject("#FFFFFF")
                }));
                created.Add(lightResult["data"]);
            }

            return Response.Success("2D rendering scene setup applied.", new { created });
        }

        static object AddPixelPerfectCamera(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "UniBridge 2D Camera");
            if (target.GetComponent<Camera>() == null)
                Undo.AddComponent<Camera>(target);

            var component = EnsureOptionalComponent(target, "PixelPerfectCamera", "UnityEngine.U2D.PixelPerfectCamera", "PixelPerfectCamera");
            if (component == null)
                return Response.Error("PixelPerfectCamera type is unavailable. Install/enable the 2D Pixel Perfect package or URP 2D support in this Unity project.");

            Undo.RecordObject(component, "Configure PixelPerfectCamera");
            var serializedObject = new SerializedObject(component);
            SetSerializedInt(serializedObject, GetInt(parameters, 100, "AssetsPPU", "assetsPPU", "assets_ppu"), "m_AssetsPPU", "m_AssetsPixelsPerUnit");
            var referenceResolution = ParseVector2Int(GetToken(parameters, "ReferenceResolution", "referenceResolution", "reference_resolution"));
            if (referenceResolution.HasValue)
            {
                SetSerializedInt(serializedObject, referenceResolution.Value.x, "m_RefResolutionX");
                SetSerializedInt(serializedObject, referenceResolution.Value.y, "m_RefResolutionY");
            }
            else
            {
                SetSerializedInt(serializedObject, GetInt(parameters, 320, "RefResolutionX", "refResolutionX", "ref_resolution_x"), "m_RefResolutionX");
                SetSerializedInt(serializedObject, GetInt(parameters, 180, "RefResolutionY", "refResolutionY", "ref_resolution_y"), "m_RefResolutionY");
            }

            SetSerializedBool(serializedObject, GetBool(parameters, false, "UpscaleRT", "upscaleRT", "upscale_rt"), "m_UpscaleRT");
            SetSerializedBool(serializedObject, GetBool(parameters, false, "PixelSnapping", "pixelSnapping", "pixel_snapping"), "m_PixelSnapping");
            SetSerializedBool(serializedObject, GetBool(parameters, false, "CropFrameX", "cropFrameX", "crop_frame_x"), "m_CropFrameX");
            SetSerializedBool(serializedObject, GetBool(parameters, false, "CropFrameY", "cropFrameY", "crop_frame_y"), "m_CropFrameY");
            SetSerializedBool(serializedObject, GetBool(parameters, false, "StretchFill", "stretchFill", "stretch_fill"), "m_StretchFill");
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            ApplyExtraProperties(component, parameters);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("PixelPerfectCamera added or updated.", BuildRenderingSummary(target));
        }

        static object AddLight2D(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Light 2D");
            var component = EnsureOptionalComponent(target, "Light2D", "UnityEngine.Rendering.Universal.Light2D", "Light2D");
            if (component == null)
                return Response.Error("Light2D type is unavailable. Install/enable URP 2D Renderer support in this Unity project.");

            Undo.RecordObject(component, "Configure Light2D");
            var implicitProperties = new JObject();
            CopyIfPresent(parameters, implicitProperties, "lightType", "Light2DType", "light2DType", "light_2d_type", "LightType", "lightType", "Type", "type");
            CopyIfPresent(parameters, implicitProperties, "intensity", "Intensity", "intensity");
            CopyIfPresent(parameters, implicitProperties, "color", "Color", "color", "Tint", "tint");
            CopyIfPresent(parameters, implicitProperties, "pointLightOuterRadius", "OuterRadius", "outerRadius", "outer_radius", "Range", "range");
            CopyIfPresent(parameters, implicitProperties, "pointLightInnerRadius", "InnerRadius", "innerRadius", "inner_radius");
            CopyIfPresent(parameters, implicitProperties, "blendStyleIndex", "BlendStyleIndex", "blendStyleIndex", "blend_style_index");
            ApplyExtraProperties(component, parameters, implicitProperties);

            var serializedObject = new SerializedObject(component);
            SetSerializedFloatIfPresent(serializedObject, parameters, "m_PointLightOuterRadius", "OuterRadius", "outerRadius", "outer_radius", "Range", "range");
            SetSerializedFloatIfPresent(serializedObject, parameters, "m_PointLightInnerRadius", "InnerRadius", "innerRadius", "inner_radius");
            if (GetToken(parameters, "Color", "color", "Tint", "tint") != null)
                SetSerializedColor(serializedObject, ParseColor(GetToken(parameters, "Color", "color", "Tint", "tint"), Color.white), "m_Color", "m_LightColor");
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Light2D added or updated.", BuildRenderingSummary(target));
        }

        static object AddShadowCaster2D(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Shadow Caster 2D");
            var component = EnsureOptionalComponent(target, "ShadowCaster2D", "UnityEngine.Rendering.Universal.ShadowCaster2D", "ShadowCaster2D");
            if (component == null)
                return Response.Error("ShadowCaster2D type is unavailable. Install/enable URP 2D Renderer support in this Unity project.");

            Undo.RecordObject(component, "Configure ShadowCaster2D");
            var serializedObject = new SerializedObject(component);
            SetSerializedBool(serializedObject, GetBool(parameters, true, "CastsShadows", "castsShadows", "casts_shadows"), "m_CastsShadows");
            SetSerializedBool(serializedObject, GetBool(parameters, false, "SelfShadows", "selfShadows", "self_shadows"), "m_SelfShadows");
            SetSerializedFloatIfPresent(serializedObject, parameters, "m_AlphaCutoff", "AlphaCutoff", "alphaCutoff", "alpha_cutoff");
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            ApplyExtraProperties(component, parameters);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("ShadowCaster2D added or updated.", BuildRenderingSummary(target));
        }

        static object AddSpriteShapeRenderer(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Sprite Shape");
            var component = EnsureOptionalComponent(target, "SpriteShapeRenderer", "UnityEngine.U2D.SpriteShapeRenderer", "SpriteShapeRenderer");
            if (component == null)
                return Response.Error("SpriteShapeRenderer type is unavailable. Install/enable the 2D SpriteShape package in this Unity project.");

            Undo.RecordObject(component, "Configure SpriteShapeRenderer");
            if (component is Renderer renderer)
            {
                renderer.sortingLayerName = GetString(parameters, "SortingLayerName", "sortingLayerName", "sorting_layer_name") ?? renderer.sortingLayerName;
                renderer.sortingOrder = GetInt(parameters, renderer.sortingOrder, "SortingOrder", "sortingOrder", "sorting_order");
                ApplyRenderingLayerMaskIfPresent(renderer, parameters);
            }

            ApplyExtraProperties(component, parameters);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("SpriteShapeRenderer added or updated.", BuildRenderingSummary(target));
        }

        static object AddDecalProjector(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Decal Projector");
            var component = EnsureRenderingExtraComponent(target, "DecalProjector",
                "UnityEngine.Rendering.Universal.DecalProjector",
                "UnityEngine.Rendering.Universal.DecalProjector, Unity.RenderPipelines.Universal.Runtime",
                "DecalProjector");
            if (component == null)
                return Response.Error("DecalProjector type is unavailable. Install/enable Universal Render Pipeline decal support in this Unity project.");

            ConfigureRenderingExtraTransform(target, parameters, "Configure DecalProjector Transform");
            Undo.RecordObject(component, "Configure DecalProjector");
            ApplyRenderingLayerMaskIfPresent(component, parameters);
            ApplyExtraProperties(component, parameters, BuildDecalProjectorProperties(parameters));
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("DecalProjector added or updated.", BuildRenderingSummary(target));
        }

        static object AddLensFlare(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Lens Flare");
            ConfigureRenderingExtraTransform(target, parameters, "Configure LensFlare Transform");

            var mode = Normalize(GetString(parameters, "Mode", "mode", "LensFlareMode", "lensFlareMode", "lens_flare_mode") ?? "Auto");
            Component component = null;
            var isSrp = false;
            if (mode != "legacy")
            {
                component = EnsureRenderingExtraComponent(target, "LensFlareComponentSRP",
                    "UnityEngine.Rendering.LensFlareComponentSRP",
                    "UnityEngine.Rendering.LensFlareComponentSRP, Unity.RenderPipelines.Core.Runtime",
                    "LensFlareComponentSRP");
                isSrp = component != null;
            }

            if (component == null && mode != "srp")
                component = EnsureRenderingExtraComponent(target, "LensFlare",
                    "UnityEngine.LensFlare",
                    "UnityEngine.LensFlare, UnityEngine.CoreModule",
                    "LensFlare");

            if (component == null)
                return Response.Error("No LensFlare component type is available in this Unity project.");

            Undo.RecordObject(component, "Configure LensFlare");
            ApplyExtraProperties(component, parameters, isSrp ? BuildSrpLensFlareProperties(parameters) : BuildLegacyLensFlareProperties(parameters));
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success(isSrp ? "LensFlareComponentSRP added or updated." : "LensFlare added or updated.", BuildRenderingSummary(target));
        }

        static object AddFlareLayer(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Flare Layer");
            ConfigureRenderingExtraTransform(target, parameters, "Configure FlareLayer Transform");
            var component = EnsureRenderingExtraComponent(target, "FlareLayer",
                "UnityEngine.FlareLayer",
                "UnityEngine.FlareLayer, UnityEngine.CoreModule",
                "FlareLayer");
            if (component == null)
                return Response.Error("FlareLayer type is unavailable in this Unity project.");

            Undo.RecordObject(component, "Configure FlareLayer");
            ApplyExtraProperties(component, parameters);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("FlareLayer added or updated.", BuildRenderingSummary(target));
        }

        static object AddWindZone(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Wind Zone");
            ConfigureRenderingExtraTransform(target, parameters, "Configure WindZone Transform");
            var component = EnsureRenderingExtraComponent(target, "WindZone",
                "UnityEngine.WindZone",
                "UnityEngine.WindZone, UnityEngine.WindModule",
                "WindZone");
            if (component == null)
                return Response.Error("WindZone component type is unavailable after resolver lookup. Enable the Unity wind module in this project.");

            Undo.RecordObject(component, "Configure WindZone");
            ApplyExtraProperties(component, parameters, BuildWindZoneProperties(parameters));
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("WindZone added or updated.", BuildRenderingSummary(target));
        }

        static object AddProjector(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Projector");
            ConfigureRenderingExtraTransform(target, parameters, "Configure Projector Transform");
            var component = EnsureRenderingExtraComponent(target, "Projector",
                "UnityEngine.Projector",
                "UnityEngine.Projector, UnityEngine.CoreModule",
                "Projector");
            if (component == null)
                return Response.Error("Legacy Projector type is unavailable in this Unity project.");

            Undo.RecordObject(component, "Configure Projector");
            ApplyExtraProperties(component, parameters, BuildProjectorProperties(parameters));
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Projector added or updated.", BuildRenderingSummary(target));
        }

        static object AddLODGroup(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "LOD Group");
            ConfigureRenderingExtraTransform(target, parameters, "Configure LODGroup Transform");

            var lodGroup = target.GetComponent<LODGroup>();
            if (lodGroup == null)
                lodGroup = Undo.AddComponent<LODGroup>(target);

            Undo.RecordObject(lodGroup, "Configure LODGroup");
            lodGroup.localReferencePoint = ParseVector3(GetToken(parameters, "LocalReferencePoint", "localReferencePoint", "local_reference_point")) ?? lodGroup.localReferencePoint;
            if (TryReadFloat(GetToken(parameters, "LODSize", "LodSize", "lodSize", "lod_size", "Size", "size"), out var size))
                lodGroup.size = Mathf.Max(0.0001f, size);

            lodGroup.fadeMode = ParseEnum(GetString(parameters, "FadeMode", "fadeMode", "fade_mode"), lodGroup.fadeMode);
            lodGroup.animateCrossFading = GetBool(parameters, lodGroup.animateCrossFading, "AnimateCrossFading", "animateCrossFading", "animate_cross_fading");
            lodGroup.lastLODBillboard = GetBool(parameters, lodGroup.lastLODBillboard, "LastLODBillboard", "lastLODBillboard", "last_lod_billboard");
            if (GetToken(parameters, "Enabled", "enabled") != null)
                lodGroup.enabled = GetBool(parameters, lodGroup.enabled, "Enabled", "enabled");

            var warnings = new List<string>();
            var lods = BuildLODArray(target, parameters, warnings);
            if (lods.Length > 0)
                lodGroup.SetLODs(lods);
            else
                warnings.Add("No renderers were found, so existing LOD levels were left unchanged.");

            if (GetBool(parameters, true, "RecalculateBounds", "recalculateBounds", "recalculate_bounds"))
                lodGroup.RecalculateBounds();

            ApplyExtraProperties(lodGroup, parameters);
            EditorUtility.SetDirty(lodGroup);
            EditorSceneManager.MarkSceneDirty(target.scene);

            return Response.Success("LODGroup added or updated.", new
            {
                summary = BuildRenderingSummary(target),
                lodGroup = BuildLODGroupSummary(lodGroup),
                warnings = warnings.ToArray()
            });
        }

        static object AddReflectionProbe(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Reflection Probe");
            ConfigureRenderingExtraTransform(target, parameters, "Configure ReflectionProbe Transform");
            var probe = target.GetComponent<ReflectionProbe>();
            if (probe == null)
                probe = Undo.AddComponent<ReflectionProbe>(target);

            Undo.RecordObject(probe, "Configure ReflectionProbe");
            ConfigureReflectionProbe(probe, parameters);
            ApplyCullingMaskIfPresent(probe, parameters);
            ApplyExtraProperties(probe, parameters);
            EditorUtility.SetDirty(probe);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("ReflectionProbe added or updated.", BuildRenderingSummary(target));
        }

        static object AddLightProbeGroup(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Light Probe Group");
            ConfigureRenderingExtraTransform(target, parameters, "Configure LightProbeGroup Transform");
            var group = target.GetComponent<LightProbeGroup>();
            if (group == null)
                group = Undo.AddComponent<LightProbeGroup>(target);

            Undo.RecordObject(group, "Configure LightProbeGroup");
            var positions = ParseVector3Array(GetToken(parameters, "ProbePositions", "probePositions", "probe_positions", "Positions", "positions"));
            if (positions == null || positions.Length == 0)
                positions = BuildDefaultProbePositions(ParseVector3(GetToken(parameters, "Size", "size")) ?? new Vector3(4f, 3f, 4f), GetString(parameters, "ProbeLayout", "probeLayout", "probe_layout"));
            group.probePositions = positions;
            group.dering = GetBool(parameters, group.dering, "Dering", "dering");
            group.enabled = GetBool(parameters, group.enabled, "Enabled", "enabled");

            ApplyExtraProperties(group, parameters);
            EditorUtility.SetDirty(group);
            EditorSceneManager.MarkSceneDirty(target.scene);
            if (GetBool(parameters, false, "Tetrahedralize", "tetrahedralize"))
                RequestLightProbesTetrahedralize();

            return Response.Success("LightProbeGroup added or updated.", BuildRenderingSummary(target));
        }

        static object AddLightProbeProxyVolume(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "Light Probe Proxy Volume");
            ConfigureRenderingExtraTransform(target, parameters, "Configure LightProbeProxyVolume Transform");
            var volume = target.GetComponent<LightProbeProxyVolume>();
            if (volume == null)
                volume = Undo.AddComponent<LightProbeProxyVolume>(target);

            Undo.RecordObject(volume, "Configure LightProbeProxyVolume");
            ConfigureLightProbeProxyVolume(volume, parameters);
            ApplyExtraProperties(volume, parameters);
            EditorUtility.SetDirty(volume);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("LightProbeProxyVolume added or updated.", BuildRenderingSummary(target));
        }

        static object CreateProductPreviewRig(JObject parameters)
        {
            var root = ResolveOrCreateTarget(new JObject { ["Name"] = GetString(parameters, "Name", "name") ?? "UniBridge Product Preview Rig" }, GetString(parameters, "Name", "name") ?? "UniBridge Product Preview Rig");
            var created = new List<object>();

            var cameraResponse = JObject.FromObject(CreateCamera(new JObject
            {
                ["Name"] = "Preview Camera",
                ["Parent"] = SceneObjectLocator.GetHierarchyPath(root),
                ["Position"] = new JArray(0f, 1.6f, -6f),
                ["Rotation"] = new JArray(12f, 0f, 0f),
                ["FieldOfView"] = 35f,
                ["ClearFlags"] = "SolidColor",
                ["BackgroundColor"] = "#111318"
            }));
            created.Add(cameraResponse["data"]);
            CreatePreviewLight(root, "Key Light", new Vector3(-3f, 4f, -3f), new Vector3(45f, -35f, 0f), 2.4f, "#FFF4D8", created);
            CreatePreviewLight(root, "Fill Light", new Vector3(4f, 2f, -3f), new Vector3(20f, 35f, 0f), 0.7f, "#9CCBFF", created);
            CreatePreviewLight(root, "Rim Light", new Vector3(0f, 3f, 4f), new Vector3(35f, 180f, 0f), 1.4f, "#EAD7FF", created);

            return Response.Success("Product preview rig created.", new { root = BuildRenderingSummary(root), created = created.ToArray() });
        }

        static object CreateThreePointLighting(JObject parameters)
        {
            var root = ResolveOrCreateTarget(new JObject { ["Name"] = GetString(parameters, "Name", "name") ?? "UniBridge Three Point Lighting" }, GetString(parameters, "Name", "name") ?? "UniBridge Three Point Lighting");
            var created = new List<object>();
            CreatePreviewLight(root, "Key Light", new Vector3(-4f, 5f, -4f), new Vector3(50f, -35f, 0f), 1.7f, "#FFE9C7", created);
            CreatePreviewLight(root, "Fill Light", new Vector3(4f, 3f, -3f), new Vector3(30f, 45f, 0f), 0.55f, "#AFCBFF", created);
            CreatePreviewLight(root, "Back Light", new Vector3(0f, 4f, 5f), new Vector3(40f, 180f, 0f), 1.0f, "#FFFFFF", created);
            return Response.Success("Three-point lighting rig created.", new { root = BuildRenderingSummary(root), created = created.ToArray() });
        }

        static void CreatePreviewLight(GameObject root, string name, Vector3 position, Vector3 rotation, float intensity, string color, List<object> created)
        {
            var result = CreateLight(new JObject
            {
                ["Name"] = name,
                ["Parent"] = SceneObjectLocator.GetHierarchyPath(root),
                ["Position"] = new JArray(position.x, position.y, position.z),
                ["Rotation"] = new JArray(rotation.x, rotation.y, rotation.z),
                ["LightType"] = "Directional",
                ["Intensity"] = intensity,
                ["Color"] = color,
                ["Shadows"] = "Soft"
            });
            created.Add(JObject.FromObject(result)["data"]);
        }

        static void AssignVolumeProfile(Component volume, string profilePath)
        {
            EnsureParentDirectory(profilePath);
            var profileType = FindType("UnityEngine.Rendering.VolumeProfile");
            if (profileType == null)
                return;

            var profile = AssetDatabase.LoadAssetAtPath(profilePath, profileType);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance(profileType);
                AssetDatabase.CreateAsset(profile, profilePath);
                AssetDatabase.SaveAssets();
            }

            var property = volume.GetType().GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite)
                property.SetValue(volume, profile);
            else
            {
                var so = new SerializedObject(volume);
                var serializedProfile = so.FindProperty("m_Profile") ?? so.FindProperty("profile");
                if (serializedProfile != null)
                {
                    serializedProfile.objectReferenceValue = profile;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        static JObject BuildVolumeProperties(JObject parameters)
        {
            var props = new JObject();
            CopyIfPresent(parameters, props, "isGlobal", "IsGlobal", "isGlobal", "is_global");
            CopyIfPresent(parameters, props, "weight", "Weight", "weight");
            CopyIfPresent(parameters, props, "priority", "Priority", "priority");
            return props;
        }

        static JObject BuildDecalProjectorProperties(JObject parameters)
        {
            var props = new JObject();
            CopyIfPresent(parameters, props, "material", "Material", "material", "MaterialPath", "materialPath", "material_path");
            CopyIfPresent(parameters, props, "drawDistance", "DrawDistance", "drawDistance", "draw_distance");
            CopyIfPresent(parameters, props, "fadeScale", "FadeScale", "fadeScale", "fade_scale");
            CopyIfPresent(parameters, props, "startAngleFade", "StartAngleFade", "startAngleFade", "start_angle_fade");
            CopyIfPresent(parameters, props, "endAngleFade", "EndAngleFade", "endAngleFade", "end_angle_fade");
            CopyIfPresent(parameters, props, "uvScale", "UvScale", "UVScale", "uvScale", "uv_scale");
            CopyIfPresent(parameters, props, "uvBias", "UvBias", "UVBias", "uvBias", "uv_bias");
            CopyIfPresent(parameters, props, "scaleMode", "ScaleMode", "scaleMode", "scale_mode");
            CopyIfPresent(parameters, props, "pivot", "Pivot", "pivot");
            CopyIfPresent(parameters, props, "size", "Size", "size");
            CopyIfPresent(parameters, props, "fadeFactor", "FadeFactor", "fadeFactor", "fade_factor");
            return props;
        }

        static JObject BuildSrpLensFlareProperties(JObject parameters)
        {
            var props = new JObject();
            CopyIfPresent(parameters, props, "lensFlareData", "LensFlareData", "lensFlareData", "lens_flare_data", "LensFlareDataPath", "lensFlareDataPath", "lens_flare_data_path", "FlareAssetPath", "flareAssetPath", "flare_asset_path");
            CopyIfPresent(parameters, props, "intensity", "Intensity", "intensity", "Brightness", "brightness");
            CopyIfPresent(parameters, props, "maxAttenuationDistance", "MaxAttenuationDistance", "maxAttenuationDistance", "max_attenuation_distance");
            CopyIfPresent(parameters, props, "maxAttenuationScale", "MaxAttenuationScale", "maxAttenuationScale", "max_attenuation_scale");
            CopyIfPresent(parameters, props, "attenuationByLightShape", "AttenuationByLightShape", "attenuationByLightShape", "attenuation_by_light_shape");
            CopyIfPresent(parameters, props, "useOcclusion", "UseOcclusion", "useOcclusion", "use_occlusion");
            CopyIfPresent(parameters, props, "environmentOcclusion", "EnvironmentOcclusion", "environmentOcclusion", "environment_occlusion");
            CopyIfPresent(parameters, props, "occlusionRadius", "OcclusionRadius", "occlusionRadius", "occlusion_radius");
            CopyIfPresent(parameters, props, "sampleCount", "SampleCount", "sampleCount", "sample_count");
            CopyIfPresent(parameters, props, "occlusionOffset", "OcclusionOffset", "occlusionOffset", "occlusion_offset");
            CopyIfPresent(parameters, props, "scale", "Scale", "scale");
            CopyIfPresent(parameters, props, "allowOffScreen", "AllowOffScreen", "allowOffScreen", "allow_off_screen");
            CopyIfPresent(parameters, props, "lightOverride", "LightOverride", "lightOverride", "light_override");
            return props;
        }

        static JObject BuildLegacyLensFlareProperties(JObject parameters)
        {
            var props = new JObject();
            CopyIfPresent(parameters, props, "flare", "Flare", "flare", "FlareAssetPath", "flareAssetPath", "flare_asset_path");
            CopyIfPresent(parameters, props, "color", "Color", "color", "Tint", "tint");
            CopyIfPresent(parameters, props, "brightness", "Brightness", "brightness", "Intensity", "intensity");
            CopyIfPresent(parameters, props, "fadeSpeed", "FadeSpeed", "fadeSpeed", "fade_speed");
            return props;
        }

        static JObject BuildWindZoneProperties(JObject parameters)
        {
            var props = new JObject();
            CopyIfPresent(parameters, props, "mode", "Mode", "mode", "WindMode", "windMode", "wind_mode");
            CopyIfPresent(parameters, props, "radius", "Radius", "radius");
            CopyIfPresent(parameters, props, "windMain", "WindMain", "windMain", "wind_main");
            CopyIfPresent(parameters, props, "windTurbulence", "WindTurbulence", "windTurbulence", "wind_turbulence", "Turbulence", "turbulence");
            CopyIfPresent(parameters, props, "windPulseMagnitude", "WindPulseMagnitude", "windPulseMagnitude", "wind_pulse_magnitude");
            CopyIfPresent(parameters, props, "windPulseFrequency", "WindPulseFrequency", "windPulseFrequency", "wind_pulse_frequency");
            return props;
        }

        static JObject BuildProjectorProperties(JObject parameters)
        {
            var props = new JObject();
            CopyIfPresent(parameters, props, "nearClipPlane", "NearClip", "nearClip", "near_clip", "NearClipPlane", "nearClipPlane", "near_clip_plane");
            CopyIfPresent(parameters, props, "farClipPlane", "FarClip", "farClip", "far_clip", "FarClipPlane", "farClipPlane", "far_clip_plane");
            CopyIfPresent(parameters, props, "fieldOfView", "FieldOfView", "fieldOfView", "field_of_view", "Fov", "fov");
            CopyIfPresent(parameters, props, "aspectRatio", "AspectRatio", "aspectRatio", "aspect_ratio");
            CopyIfPresent(parameters, props, "orthographic", "Orthographic", "orthographic", "ortho");
            CopyIfPresent(parameters, props, "orthographicSize", "OrthographicSizeProjector", "orthographicSizeProjector", "OrthographicSize", "orthographicSize", "orthographic_size");
            CopyIfPresent(parameters, props, "material", "Material", "material", "MaterialPath", "materialPath", "material_path");
            CopyIfPresent(parameters, props, "ignoreLayers", "IgnoreLayers", "ignoreLayers", "ignore_layers", "CullingMask", "cullingMask", "culling_mask");
            CopyIfPresent(parameters, props, "enabled", "Enabled", "enabled");
            return props;
        }

        static JObject BuildReflectionProbeProperties(JObject parameters)
        {
            var props = new JObject();
            CopyIfPresent(parameters, props, "mode", "Mode", "mode", "ProbeMode", "probeMode", "probe_mode");
            CopyIfPresent(parameters, props, "refreshMode", "RefreshMode", "refreshMode", "refresh_mode");
            CopyIfPresent(parameters, props, "timeSlicingMode", "TimeSlicingMode", "timeSlicingMode", "time_slicing_mode");
            CopyIfPresent(parameters, props, "renderDynamicObjects", "RenderDynamicObjects", "renderDynamicObjects", "render_dynamic_objects");
            CopyIfPresent(parameters, props, "customBakedTexture", "CustomBakedTexture", "customBakedTexture", "custom_baked_texture", "CustomBakedTexturePath", "customBakedTexturePath", "custom_baked_texture_path");
            CopyIfPresent(parameters, props, "importance", "Importance", "importance");
            CopyIfPresent(parameters, props, "intensity", "Intensity", "intensity");
            CopyIfPresent(parameters, props, "boxProjection", "BoxProjection", "boxProjection", "box_projection");
            CopyIfPresent(parameters, props, "blendDistance", "BlendDistance", "blendDistance", "blend_distance");
            CopyIfPresent(parameters, props, "size", "ProbeSize", "probeSize", "probe_size", "Size", "size");
            CopyIfPresent(parameters, props, "center", "Center", "center");
            CopyIfPresent(parameters, props, "resolution", "Resolution", "resolution");
            CopyIfPresent(parameters, props, "hdr", "Hdr", "HDR", "hdr");
            CopyIfPresent(parameters, props, "shadowDistance", "ShadowDistance", "shadowDistance", "shadow_distance");
            CopyIfPresent(parameters, props, "clearFlags", "ClearFlags", "clearFlags", "clear_flags");
            CopyIfPresent(parameters, props, "backgroundColor", "BackgroundColor", "backgroundColor", "background_color");
            CopyIfPresent(parameters, props, "occlusionCulling", "OcclusionCulling", "occlusionCulling", "occlusion_culling");
            CopyIfPresent(parameters, props, "nearClipPlane", "NearClipPlane", "nearClipPlane", "near_clip_plane", "NearClip", "nearClip", "near_clip");
            CopyIfPresent(parameters, props, "farClipPlane", "FarClipPlane", "farClipPlane", "far_clip_plane", "FarClip", "farClip", "far_clip");
            CopyIfPresent(parameters, props, "enabled", "Enabled", "enabled");
            return props;
        }

        static JObject BuildLightProbeProxyVolumeProperties(JObject parameters)
        {
            var props = new JObject();
            CopyIfPresent(parameters, props, "refreshMode", "RefreshMode", "refreshMode", "refresh_mode");
            CopyIfPresent(parameters, props, "qualityMode", "QualityMode", "qualityMode", "quality_mode");
            CopyIfPresent(parameters, props, "dataFormat", "DataFormat", "dataFormat", "data_format");
            CopyIfPresent(parameters, props, "boundingBoxMode", "BoundingBoxMode", "boundingBoxMode", "bounding_box_mode");
            CopyIfPresent(parameters, props, "sizeCustom", "SizeCustom", "sizeCustom", "size_custom", "Size", "size");
            CopyIfPresent(parameters, props, "originCustom", "OriginCustom", "originCustom", "origin_custom", "Center", "center", "Origin", "origin");
            CopyIfPresent(parameters, props, "resolutionMode", "ResolutionMode", "resolutionMode", "resolution_mode");
            CopyIfPresent(parameters, props, "probeDensity", "ProbeDensity", "probeDensity", "probe_density");
            CopyIfPresent(parameters, props, "gridResolutionX", "GridResolutionX", "gridResolutionX", "grid_resolution_x");
            CopyIfPresent(parameters, props, "gridResolutionY", "GridResolutionY", "gridResolutionY", "grid_resolution_y");
            CopyIfPresent(parameters, props, "gridResolutionZ", "GridResolutionZ", "gridResolutionZ", "grid_resolution_z");
            CopyIfPresent(parameters, props, "probePositionMode", "ProbePositionMode", "probePositionMode", "probe_position_mode");
            CopyIfPresent(parameters, props, "enabled", "Enabled", "enabled");
            return props;
        }

        static void ConfigureReflectionProbe(ReflectionProbe probe, JObject parameters)
        {
            probe.mode = ParseEnum(GetString(parameters, "Mode", "mode", "ProbeMode", "probeMode", "probe_mode"), probe.mode);
            probe.refreshMode = ParseEnum(GetString(parameters, "RefreshMode", "refreshMode", "refresh_mode"), probe.refreshMode);
            probe.timeSlicingMode = ParseEnum(GetString(parameters, "TimeSlicingMode", "timeSlicingMode", "time_slicing_mode"), probe.timeSlicingMode);
            probe.renderDynamicObjects = GetBool(parameters, probe.renderDynamicObjects, "RenderDynamicObjects", "renderDynamicObjects", "render_dynamic_objects");
            probe.importance = GetInt(parameters, probe.importance, "Importance", "importance");
            probe.intensity = GetFloat(parameters, probe.intensity, "Intensity", "intensity");
            probe.boxProjection = GetBool(parameters, probe.boxProjection, "BoxProjection", "boxProjection", "box_projection");
            probe.blendDistance = GetFloat(parameters, probe.blendDistance, "BlendDistance", "blendDistance", "blend_distance");
            probe.size = ParseVector3(GetToken(parameters, "ProbeSize", "probeSize", "probe_size", "Size", "size")) ?? probe.size;
            probe.center = ParseVector3(GetToken(parameters, "Center", "center")) ?? probe.center;
            probe.resolution = GetInt(parameters, probe.resolution, "Resolution", "resolution");
            probe.hdr = GetBool(parameters, probe.hdr, "Hdr", "HDR", "hdr");
            probe.shadowDistance = GetFloat(parameters, probe.shadowDistance, "ShadowDistance", "shadowDistance", "shadow_distance");
            probe.clearFlags = ParseEnum(GetString(parameters, "ClearFlags", "clearFlags", "clear_flags"), probe.clearFlags);
            var backgroundToken = GetToken(parameters, "BackgroundColor", "backgroundColor", "background_color");
            if (backgroundToken != null)
                probe.backgroundColor = ParseColor(backgroundToken, probe.backgroundColor);
            probe.nearClipPlane = GetFloat(parameters, probe.nearClipPlane, "NearClipPlane", "nearClipPlane", "near_clip_plane", "NearClip", "nearClip", "near_clip");
            probe.farClipPlane = GetFloat(parameters, probe.farClipPlane, "FarClipPlane", "farClipPlane", "far_clip_plane", "FarClip", "farClip", "far_clip");
            probe.enabled = GetBool(parameters, probe.enabled, "Enabled", "enabled");

            var customTexture = ResolveTextureReference(GetToken(parameters, "CustomBakedTexture", "customBakedTexture", "custom_baked_texture", "CustomBakedTexturePath", "customBakedTexturePath", "custom_baked_texture_path"));
            if (customTexture != null)
                probe.customBakedTexture = customTexture;
        }

        static void ConfigureLightProbeProxyVolume(LightProbeProxyVolume volume, JObject parameters)
        {
            volume.refreshMode = ParseEnum(GetString(parameters, "RefreshMode", "refreshMode", "refresh_mode"), volume.refreshMode);
            volume.qualityMode = ParseEnum(GetString(parameters, "QualityMode", "qualityMode", "quality_mode"), volume.qualityMode);
            volume.dataFormat = ParseEnum(GetString(parameters, "DataFormat", "dataFormat", "data_format"), volume.dataFormat);
            volume.boundingBoxMode = ParseEnum(GetString(parameters, "BoundingBoxMode", "boundingBoxMode", "bounding_box_mode"), volume.boundingBoxMode);
            volume.sizeCustom = ParseVector3(GetToken(parameters, "SizeCustom", "sizeCustom", "size_custom", "Size", "size")) ?? volume.sizeCustom;
            volume.originCustom = ParseVector3(GetToken(parameters, "OriginCustom", "originCustom", "origin_custom", "Center", "center", "Origin", "origin")) ?? volume.originCustom;
            volume.resolutionMode = ParseEnum(GetString(parameters, "ResolutionMode", "resolutionMode", "resolution_mode"), volume.resolutionMode);
            volume.probeDensity = GetFloat(parameters, volume.probeDensity, "ProbeDensity", "probeDensity", "probe_density");
            volume.gridResolutionX = GetInt(parameters, volume.gridResolutionX, "GridResolutionX", "gridResolutionX", "grid_resolution_x");
            volume.gridResolutionY = GetInt(parameters, volume.gridResolutionY, "GridResolutionY", "gridResolutionY", "grid_resolution_y");
            volume.gridResolutionZ = GetInt(parameters, volume.gridResolutionZ, "GridResolutionZ", "gridResolutionZ", "grid_resolution_z");
            volume.probePositionMode = ParseEnum(GetString(parameters, "ProbePositionMode", "probePositionMode", "probe_position_mode"), volume.probePositionMode);
            volume.enabled = GetBool(parameters, volume.enabled, "Enabled", "enabled");
        }

        static LOD[] BuildLODArray(GameObject target, JObject parameters, List<string> warnings)
        {
            var lodToken = GetToken(parameters, "Lods", "LODs", "lods", "LODLevels", "lodLevels", "lod_levels", "Levels", "levels");
            if (lodToken is JArray lodArray)
                return BuildExplicitLODArray(target, lodArray, warnings);

            var renderers = ResolveRendererReferences(GetToken(parameters, "Renderers", "renderers", "Renderer", "renderer"), target, GetBool(parameters, true, "UseChildRenderers", "useChildRenderers", "use_child_renderers"));
            if (renderers.Length == 0 && GetBool(parameters, true, "UseChildRenderers", "useChildRenderers", "use_child_renderers"))
                renderers = target.GetComponentsInChildren<Renderer>(includeInactive: true).Where(renderer => renderer != null).Distinct().ToArray();

            if (renderers.Length == 0)
                return Array.Empty<LOD>();

            var height = ReadLODHeight(parameters, 0, 1);
            var lod = new LOD(height, renderers)
            {
                fadeTransitionWidth = GetFloat(parameters, 0f, "FadeTransitionWidth", "fadeTransitionWidth", "fade_transition_width")
            };
            return new[] { lod };
        }

        static LOD[] BuildExplicitLODArray(GameObject target, JArray levels, List<string> warnings)
        {
            var lods = new List<LOD>();
            for (var i = 0; i < levels.Count; i++)
            {
                var token = levels[i];
                var levelObject = token as JObject;
                var rendererToken = levelObject == null ? token : GetToken(levelObject, "Renderers", "renderers", "Renderer", "renderer", "Targets", "targets", "Objects", "objects", "Target", "target", "GameObject", "gameObject", "game_object");
                var includeChildren = levelObject == null || GetBool(levelObject, true, "IncludeChildren", "includeChildren", "include_children", "UseChildRenderers", "useChildRenderers", "use_child_renderers");
                var renderers = ResolveRendererReferences(rendererToken, target, includeChildren);
                if (renderers.Length == 0)
                {
                    warnings.Add($"LOD level {i} has no resolved renderers and was skipped.");
                    continue;
                }

                var height = levelObject == null ? DefaultLODHeight(i, levels.Count) : ReadLODHeight(levelObject, i, levels.Count);
                var fadeWidth = levelObject == null ? 0f : GetFloat(levelObject, 0f, "FadeTransitionWidth", "fadeTransitionWidth", "fade_transition_width");
                lods.Add(new LOD(height, renderers)
                {
                    fadeTransitionWidth = Mathf.Clamp01(fadeWidth)
                });
            }

            return lods
                .OrderByDescending(lod => lod.screenRelativeTransitionHeight)
                .ToArray();
        }

        static float ReadLODHeight(JObject source, int index, int count)
        {
            return TryReadFloat(GetToken(source,
                    "ScreenRelativeTransitionHeight", "screenRelativeTransitionHeight", "screen_relative_transition_height",
                    "Height", "height", "Transition", "transition"), out var height)
                ? Mathf.Clamp(height, 0.0001f, 1f)
                : DefaultLODHeight(index, count);
        }

        static float DefaultLODHeight(int index, int count)
        {
            if (count <= 1)
                return 0.5f;
            return Mathf.Clamp01(Mathf.Lerp(0.6f, 0.1f, index / (float)(count - 1)));
        }

        static Renderer[] ResolveRendererReferences(JToken token, GameObject scope, bool includeChildren)
        {
            var renderers = new List<Renderer>();
            CollectRendererReferences(token, scope, includeChildren, renderers);
            return renderers.Where(renderer => renderer != null).Distinct().ToArray();
        }

        static void CollectRendererReferences(JToken token, GameObject scope, bool includeChildren, List<Renderer> renderers)
        {
            if (token == null || token.Type == JTokenType.Null)
                return;

            if (token is JArray array)
            {
                foreach (var item in array)
                    CollectRendererReferences(item, scope, includeChildren, renderers);
                return;
            }

            if (token is JObject obj)
            {
                var nested = GetToken(obj, "Renderers", "renderers", "Renderer", "renderer", "Targets", "targets", "Objects", "objects");
                if (nested != null)
                {
                    CollectRendererReferences(nested, scope, GetBool(obj, includeChildren, "IncludeChildren", "includeChildren", "include_children", "UseChildRenderers", "useChildRenderers", "use_child_renderers"), renderers);
                    return;
                }

                var objectToken = GetToken(obj, "Target", "target", "GameObject", "gameObject", "game_object", "Path", "path", "Name", "name", "ObjectId", "objectId", "InstanceId", "instanceId", "ComponentId", "componentId");
                CollectRendererReferences(objectToken, scope, GetBool(obj, includeChildren, "IncludeChildren", "includeChildren", "include_children", "UseChildRenderers", "useChildRenderers", "use_child_renderers"), renderers);
                return;
            }

            var objectRef = ResolveUnityObjectReference(token, scope);
            if (objectRef == null)
                return;

            AddRenderersFromObject(objectRef, includeChildren, renderers);
        }

        static UnityEngine.Object ResolveUnityObjectReference(JToken token, GameObject scope)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type == JTokenType.Integer && long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return UnityApiAdapter.GetObjectFromId(id);

            var text = token.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                var direct = UnityApiAdapter.GetObjectFromId(id);
                if (direct != null)
                    return direct;
            }

            var sceneObject = SceneObjectLocator.FindObject(text, null, new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            if (sceneObject != null)
                return sceneObject;

            if (scope != null)
            {
                var child = scope.GetComponentsInChildren<Transform>(includeInactive: true)
                    .FirstOrDefault(transform =>
                        string.Equals(transform.name, text, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(SceneObjectLocator.GetHierarchyPath(transform.gameObject), text, StringComparison.OrdinalIgnoreCase));
                if (child != null)
                    return child.gameObject;
            }

            return null;
        }

        static Texture ResolveTextureReference(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type == JTokenType.Integer && long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return UnityApiAdapter.GetObjectFromId(id) as Texture;

            var text = token.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                var direct = UnityApiAdapter.GetObjectFromId(id) as Texture;
                if (direct != null)
                    return direct;
            }

            var assetPath = NormalizeAssetPath(text);
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
            if (texture != null)
                return texture;

            if (GUID.TryParse(text, out var guid))
                return AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(guid.ToString()));

            return null;
        }

        static void AddRenderersFromObject(UnityEngine.Object value, bool includeChildren, List<Renderer> renderers)
        {
            switch (value)
            {
                case Renderer renderer:
                    renderers.Add(renderer);
                    break;
                case Component component:
                    AddRenderersFromGameObject(component.gameObject, includeChildren, renderers);
                    break;
                case GameObject gameObject:
                    AddRenderersFromGameObject(gameObject, includeChildren, renderers);
                    break;
            }
        }

        static void AddRenderersFromGameObject(GameObject gameObject, bool includeChildren, List<Renderer> renderers)
        {
            if (gameObject == null)
                return;

            if (includeChildren)
                renderers.AddRange(gameObject.GetComponentsInChildren<Renderer>(includeInactive: true).Where(renderer => renderer != null));
            else
            {
                var renderer = gameObject.GetComponent<Renderer>();
                if (renderer != null)
                    renderers.Add(renderer);
            }
        }

        static Vector3[] ParseVector3Array(JToken token)
        {
            if (token is not JArray array)
                return null;

            var values = new List<Vector3>();
            foreach (var item in array)
            {
                var value = ParseVector3(item);
                if (value.HasValue)
                    values.Add(value.Value);
            }

            return values.ToArray();
        }

        static Vector3[] BuildDefaultProbePositions(Vector3 size, string layout)
        {
            var half = new Vector3(
                Mathf.Max(0.1f, Mathf.Abs(size.x)) * 0.5f,
                Mathf.Max(0.1f, Mathf.Abs(size.y)) * 0.5f,
                Mathf.Max(0.1f, Mathf.Abs(size.z)) * 0.5f);

            if (Normalize(layout) == "tetrahedron")
            {
                return new[]
                {
                    new Vector3(-half.x, -half.y, -half.z),
                    new Vector3(half.x, -half.y, half.z),
                    new Vector3(-half.x, half.y, half.z),
                    new Vector3(half.x, half.y, -half.z)
                };
            }

            return new[]
            {
                new Vector3(-half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, -half.z),
                new Vector3(-half.x, half.y, -half.z),
                new Vector3(half.x, half.y, -half.z),
                new Vector3(-half.x, -half.y, half.z),
                new Vector3(half.x, -half.y, half.z),
                new Vector3(-half.x, half.y, half.z),
                new Vector3(half.x, half.y, half.z)
            };
        }

        static void ApplyCullingMaskIfPresent(ReflectionProbe probe, JObject parameters)
        {
            var token = GetToken(parameters, "CullingMask", "cullingMask", "culling_mask");
            if (token == null)
                return;

            probe.cullingMask = ReadLayerMask(token, probe.cullingMask);
        }

        static void RequestLightProbesTetrahedralize()
        {
            var method = typeof(Lightmapping).GetMethod("TetrahedralizeAsync", BindingFlags.Static | BindingFlags.Public) ??
                         typeof(Lightmapping).GetMethod("Tetrahedralize", BindingFlags.Static | BindingFlags.Public);
            method?.Invoke(null, null);
        }

        static void ConfigureRenderingExtraTransform(GameObject target, JObject parameters, string undoName)
        {
            Undo.RecordObject(target.transform, undoName);
            target.transform.localPosition = ParseVector3(GetToken(parameters, "Position", "position")) ?? target.transform.localPosition;
            target.transform.localEulerAngles = ParseVector3(GetToken(parameters, "Rotation", "rotation", "Euler", "euler")) ?? target.transform.localEulerAngles;
            target.transform.localScale = ParseVector3(GetToken(parameters, "Scale", "scale")) ?? target.transform.localScale;
            AssignParent(target, parameters);
        }

        static void ApplyExtraProperties(UnityEngine.Object component, JObject parameters, JObject implicitProperties = null)
        {
            var props = parameters["Properties"] as JObject ?? parameters["properties"] as JObject;
            if (props == null)
                props = implicitProperties;
            else if (implicitProperties != null)
                foreach (var property in implicitProperties.Properties())
                    if (props[property.Name] == null)
                        props[property.Name] = property.Value.DeepClone();

            if (props == null)
                return;

            var so = new SerializedObject(component);
            foreach (var property in props.Properties())
            {
                var result = SerializedPropertyPatcher.TryApplyProperty(component, so, property.Name, property.Value, dryRun: false);
                if (!result.Success)
                    TrySetPublicProperty(component, property.Name, property.Value);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void ApplyRenderingLayerMaskIfPresent(UnityEngine.Object component, JObject parameters)
        {
            var token = GetToken(parameters, "RenderingLayerMask", "renderingLayerMask", "rendering_layer_mask");
            if (token == null)
                return;

            using var serializedObject = new SerializedObject(component);
            var property = serializedObject.FindProperty("m_RenderingLayerMask");
            if (property == null)
                throw new InvalidOperationException($"{component.GetType().Name} does not expose m_RenderingLayerMask.");

            property.intValue = RenderingLayerUtility.ReadRenderingLayerMaskToken(token);
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(component);
        }

        static bool TrySetPublicProperty(UnityEngine.Object target, string name, JToken value)
        {
            var normalized = Normalize(name);
            var property = target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(prop => prop.CanWrite && Normalize(prop.Name) == normalized);
            if (property == null)
                return false;

            try
            {
                object converted;
                if (property.PropertyType == typeof(Color))
                    converted = ParseColor(value, Color.white);
                else if (property.PropertyType == typeof(Vector2))
                    converted = ParseVector2(value) ?? Vector2.zero;
                else if (property.PropertyType == typeof(Vector3))
                    converted = ParseVector3(value) ?? Vector3.zero;
                else if (property.PropertyType.IsEnum)
                    converted = value.Type == JTokenType.Integer ? Enum.ToObject(property.PropertyType, value.ToObject<int>()) : Enum.Parse(property.PropertyType, value.ToString(), true);
                else
                    converted = value.ToObject(property.PropertyType);

                property.SetValue(target, converted);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static Component AddOptionalComponent(GameObject target, JObject parameters, params string[] typeNames)
        {
            var enabled = GetBool(parameters, false, "AddAdditionalData", "addAdditionalData", "add_additional_data", "AddUniversalAdditionalData", "addUniversalAdditionalData");
            if (!enabled)
                return null;

            var type = FindType(typeNames);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                return null;

            var component = target.GetComponent(type);
            if (component == null)
                component = Undo.AddComponent(target, type);
            ApplyExtraProperties(component, parameters);
            EditorUtility.SetDirty(component);
            return component;
        }

        static Component EnsureOptionalComponent(GameObject target, string displayName, params string[] typeNames)
        {
            return EnsureOptionalComponent(target, displayName, true, typeNames);
        }

        static Component EnsureOptionalComponent(GameObject target, string displayName, bool logUnavailable, params string[] typeNames)
        {
            var type = FindType(typeNames);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                if (logUnavailable)
                    Debug.LogWarning($"[ManageRendering] Optional component type not found: {displayName}");
                return null;
            }

            var component = target.GetComponent(type);
            return component ?? Undo.AddComponent(target, type);
        }

        static Component EnsureRenderingExtraComponent(GameObject target, string displayName, params string[] typeNames)
        {
            foreach (var name in typeNames.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var componentName = StripAssemblyQualifier(name);
                var existingByName = FindComponentByTypeName(target, componentName);
                if (existingByName != null)
                    return existingByName;

                var gameObjectResolvedType = ManageGameObject.FindType(componentName);
                if (gameObjectResolvedType != null && typeof(Component).IsAssignableFrom(gameObjectResolvedType))
                {
                    var existing = target.GetComponent(gameObjectResolvedType);
                    return existing ?? Undo.AddComponent(target, gameObjectResolvedType);
                }

                var typeCacheType = ResolveComponentFromTypeCache(componentName);
                if (typeCacheType != null)
                {
                    var existing = target.GetComponent(typeCacheType);
                    return existing ?? Undo.AddComponent(target, typeCacheType);
                }

                if (ComponentResolver.TryResolve(componentName, out var resolvedType, out _) &&
                    typeof(Component).IsAssignableFrom(resolvedType))
                {
                    var existing = target.GetComponent(resolvedType);
                    return existing ?? Undo.AddComponent(target, resolvedType);
                }
            }

            var resolved = EnsureOptionalComponent(target, displayName, false, typeNames);
            if (resolved != null)
                return resolved;

            return TryAddComponentThroughGameObjectTool(target, typeNames);
        }

        static Type ResolveComponentFromTypeCache(string name)
        {
#if UNITY_EDITOR
            return TypeCache.GetTypesDerivedFrom<Component>()
                .FirstOrDefault(type =>
                    string.Equals(type.FullName, name, StringComparison.Ordinal) ||
                    string.Equals(type.Name, name, StringComparison.Ordinal));
#else
            return null;
#endif
        }

        static Component FindComponentByTypeName(GameObject target, string name)
        {
            return target.GetComponents<Component>()
                .FirstOrDefault(component =>
                    component != null &&
                    (string.Equals(component.GetType().FullName, name, StringComparison.Ordinal) ||
                     string.Equals(component.GetType().Name, name, StringComparison.Ordinal)));
        }

        static Component TryAddComponentThroughGameObjectTool(GameObject target, string[] typeNames)
        {
            var targetId = UnityApiAdapter.GetObjectId(target);
            foreach (var typeName in typeNames.Select(StripAssemblyQualifier).Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var existing = FindComponentByTypeName(target, typeName);
                if (existing != null)
                    return existing;

                var response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
                {
                    ["action"] = "AddComponent",
                    ["target"] = targetId.ToString(CultureInfo.InvariantCulture),
                    ["search_method"] = "ById",
                    ["component_name"] = typeName
                }));

                if (response["success"]?.Value<bool>() != true)
                    continue;

                existing = FindComponentByTypeName(target, typeName);
                if (existing != null)
                    return existing;
            }

            return null;
        }

        static bool HasRenderingComponent(GameObject go)
        {
            return go.GetComponent<Camera>() != null ||
                   go.GetComponent<Light>() != null ||
                   go.GetComponent<ReflectionProbe>() != null ||
                   go.GetComponent<LightProbeGroup>() != null ||
                   go.GetComponent<LightProbeProxyVolume>() != null ||
                   go.GetComponents<Component>().Any(component => component != null && (IsVolumeType(component.GetType()) || Is2DRenderingType(component.GetType()) || IsRenderingExtraType(component.GetType())));
        }

        static bool IsVolumeType(Type type)
        {
            return string.Equals(type.FullName, "UnityEngine.Rendering.Volume", StringComparison.Ordinal) ||
                   string.Equals(type.Name, "Volume", StringComparison.Ordinal);
        }

        static bool Is2DRenderingType(Type type)
        {
            var name = type?.Name;
            return string.Equals(name, "PixelPerfectCamera", StringComparison.Ordinal) ||
                   string.Equals(name, "Light2D", StringComparison.Ordinal) ||
                   string.Equals(name, "ShadowCaster2D", StringComparison.Ordinal) ||
                   string.Equals(name, "SpriteShapeRenderer", StringComparison.Ordinal);
        }

        static bool IsRenderingExtraType(Type type)
        {
            var name = type?.Name;
            return string.Equals(name, "DecalProjector", StringComparison.Ordinal) ||
                   string.Equals(name, "LensFlareComponentSRP", StringComparison.Ordinal) ||
                   string.Equals(name, "LensFlare", StringComparison.Ordinal) ||
                   string.Equals(name, "FlareLayer", StringComparison.Ordinal) ||
                   string.Equals(name, "WindZone", StringComparison.Ordinal) ||
                   string.Equals(name, "Projector", StringComparison.Ordinal) ||
                   string.Equals(name, "LODGroup", StringComparison.Ordinal);
        }

        static object BuildRenderingSummary(GameObject target)
        {
            return new
            {
                gameObject = new
                {
                    name = target.name,
                    instanceId = UnityApiAdapter.GetObjectId(target),
                    path = SceneObjectLocator.GetHierarchyPath(target),
                    scene = target.scene.IsValid() ? new { name = target.scene.name, path = target.scene.path } : null,
                    position = SerializeVector3(target.transform.position),
                    rotation = SerializeVector3(target.transform.eulerAngles)
                },
                cameras = target.GetComponents<Camera>().Select(BuildCameraSummary).ToArray(),
                lights = target.GetComponents<Light>().Select(BuildLightSummary).ToArray(),
                reflectionProbes = target.GetComponents<ReflectionProbe>().Select(BuildReflectionProbeSummary).ToArray(),
                lightProbeGroups = target.GetComponents<LightProbeGroup>().Select(BuildLightProbeGroupSummary).ToArray(),
                lightProbeProxyVolumes = target.GetComponents<LightProbeProxyVolume>().Select(BuildLightProbeProxyVolumeSummary).ToArray(),
                volumes = target.GetComponents<Component>().Where(component => component != null && IsVolumeType(component.GetType())).Select(BuildVolumeSummary).ToArray(),
                rendering2D = target.GetComponents<Component>().Where(component => component != null && Is2DRenderingType(component.GetType())).Select(Build2DRenderingSummary).ToArray(),
                renderingExtras = target.GetComponents<Component>().Where(component => component != null && IsRenderingExtraType(component.GetType())).Select(BuildRenderingExtraSummary).ToArray()
            };
        }

        static object BuildCameraSummary(Camera camera)
        {
            return new
            {
                type = camera.GetType().FullName,
                camera.orthographic,
                camera.orthographicSize,
                camera.fieldOfView,
                camera.nearClipPlane,
                camera.farClipPlane,
                camera.depth,
                clearFlags = camera.clearFlags.ToString(),
                backgroundColor = SerializeColor(camera.backgroundColor),
                cullingMask = camera.cullingMask,
                enabled = camera.enabled
            };
        }

        static object BuildLightSummary(Light light)
        {
            return new
            {
                type = light.GetType().FullName,
                lightType = light.type.ToString(),
                color = SerializeColor(light.color),
                light.intensity,
                light.range,
                light.spotAngle,
                shadows = light.shadows.ToString(),
                renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(light),
                enabled = light.enabled
            };
        }

        static object BuildReflectionProbeSummary(ReflectionProbe probe)
        {
            return new
            {
                type = probe.GetType().FullName,
                enabled = probe.enabled,
                mode = probe.mode.ToString(),
                refreshMode = probe.refreshMode.ToString(),
                timeSlicingMode = probe.timeSlicingMode.ToString(),
                renderDynamicObjects = probe.renderDynamicObjects,
                customBakedTexture = SerializeObjectReference(probe.customBakedTexture),
                probe.importance,
                probe.intensity,
                probe.boxProjection,
                probe.blendDistance,
                size = SerializeVector3(probe.size),
                center = SerializeVector3(probe.center),
                probe.resolution,
                hdr = probe.hdr,
                probe.shadowDistance,
                clearFlags = probe.clearFlags.ToString(),
                backgroundColor = SerializeColor(probe.backgroundColor),
                cullingMask = SerializeLayerMask(probe.cullingMask),
                occlusionCulling = SerializeValue(ReadProperty(probe, "occlusionCulling")),
                probe.nearClipPlane,
                probe.farClipPlane
            };
        }

        static object BuildLightProbeGroupSummary(LightProbeGroup group)
        {
            return new
            {
                type = group.GetType().FullName,
                enabled = group.enabled,
                dering = group.dering,
                probeCount = group.probePositions?.Length ?? 0,
                probePositions = (group.probePositions ?? Array.Empty<Vector3>()).Select(SerializeVector3).ToArray()
            };
        }

        static object BuildLightProbeProxyVolumeSummary(LightProbeProxyVolume volume)
        {
            return new
            {
                type = volume.GetType().FullName,
                enabled = volume.enabled,
                refreshMode = volume.refreshMode.ToString(),
                qualityMode = volume.qualityMode.ToString(),
                dataFormat = volume.dataFormat.ToString(),
                boundingBoxMode = volume.boundingBoxMode.ToString(),
                sizeCustom = SerializeVector3(volume.sizeCustom),
                originCustom = SerializeVector3(volume.originCustom),
                resolutionMode = volume.resolutionMode.ToString(),
                probeDensity = volume.probeDensity,
                gridResolutionX = volume.gridResolutionX,
                gridResolutionY = volume.gridResolutionY,
                gridResolutionZ = volume.gridResolutionZ,
                probePositionMode = volume.probePositionMode.ToString()
            };
        }

        static object BuildVolumeSummary(Component volume)
        {
            var type = volume.GetType();
            return new
            {
                type = type.FullName,
                isGlobal = ReadProperty(volume, "isGlobal"),
                weight = ReadProperty(volume, "weight"),
                priority = ReadProperty(volume, "priority"),
                profile = SerializeObjectReference(ReadProperty(volume, "profile") as UnityEngine.Object)
            };
        }

        static object Build2DRenderingSummary(Component component)
        {
            return new
            {
                type = component.GetType().FullName,
                enabled = component is Behaviour behaviour ? behaviour.enabled : (bool?)null,
                lightType = ReadProperty(component, "lightType")?.ToString(),
                intensity = ReadProperty(component, "intensity"),
                color = ReadProperty(component, "color") is Color color ? SerializeColor(color) : null,
                sortingLayerName = component is Renderer renderer ? renderer.sortingLayerName : null,
                sortingOrder = component is Renderer renderer2 ? renderer2.sortingOrder : (int?)null,
                renderingLayerMask = component is Renderer renderer3 ? RenderingLayerUtility.SerializeRenderingLayerMask(renderer3) : RenderingLayerUtility.SerializeRenderingLayerMask(component)
            };
        }

        static object BuildRenderingExtraSummary(Component component)
        {
            var name = component.GetType().Name;
            return name switch
            {
                "DecalProjector" => new
                {
                    type = component.GetType().FullName,
                    enabled = ComponentEnabled(component),
                    material = SerializeObjectReference(ReadProperty(component, "material") as UnityEngine.Object),
                    drawDistance = SerializeValue(ReadProperty(component, "drawDistance")),
                    fadeScale = SerializeValue(ReadProperty(component, "fadeScale")),
                    startAngleFade = SerializeValue(ReadProperty(component, "startAngleFade")),
                    endAngleFade = SerializeValue(ReadProperty(component, "endAngleFade")),
                    uvScale = SerializeValue(ReadProperty(component, "uvScale")),
                    uvBias = SerializeValue(ReadProperty(component, "uvBias")),
                    scaleMode = SerializeValue(ReadProperty(component, "scaleMode")),
                    pivot = SerializeValue(ReadProperty(component, "pivot")),
                    size = SerializeValue(ReadProperty(component, "size")),
                    fadeFactor = SerializeValue(ReadProperty(component, "fadeFactor")),
                    renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(component)
                },
                "LensFlareComponentSRP" => new
                {
                    type = component.GetType().FullName,
                    enabled = ComponentEnabled(component),
                    lensFlareData = SerializeObjectReference(ReadProperty(component, "lensFlareData") as UnityEngine.Object),
                    intensity = SerializeValue(ReadProperty(component, "intensity")),
                    maxAttenuationDistance = SerializeValue(ReadProperty(component, "maxAttenuationDistance")),
                    maxAttenuationScale = SerializeValue(ReadProperty(component, "maxAttenuationScale")),
                    attenuationByLightShape = SerializeValue(ReadProperty(component, "attenuationByLightShape")),
                    useOcclusion = SerializeValue(ReadProperty(component, "useOcclusion")),
                    environmentOcclusion = SerializeValue(ReadProperty(component, "environmentOcclusion")),
                    occlusionRadius = SerializeValue(ReadProperty(component, "occlusionRadius")),
                    sampleCount = SerializeValue(ReadProperty(component, "sampleCount")),
                    occlusionOffset = SerializeValue(ReadProperty(component, "occlusionOffset")),
                    scale = SerializeValue(ReadProperty(component, "scale")),
                    allowOffScreen = SerializeValue(ReadProperty(component, "allowOffScreen")),
                    lightOverride = SerializeObjectReference(ReadProperty(component, "lightOverride") as UnityEngine.Object)
                },
                "LensFlare" => new
                {
                    type = component.GetType().FullName,
                    enabled = ComponentEnabled(component),
                    flare = SerializeObjectReference(ReadProperty(component, "flare") as UnityEngine.Object),
                    color = SerializeValue(ReadProperty(component, "color")),
                    brightness = SerializeValue(ReadProperty(component, "brightness")),
                    fadeSpeed = SerializeValue(ReadProperty(component, "fadeSpeed"))
                },
                "WindZone" => new
                {
                    type = component.GetType().FullName,
                    enabled = ComponentEnabled(component),
                    mode = SerializeValue(ReadProperty(component, "mode")),
                    radius = SerializeValue(ReadProperty(component, "radius")),
                    windMain = SerializeValue(ReadProperty(component, "windMain")),
                    windTurbulence = SerializeValue(ReadProperty(component, "windTurbulence")),
                    windPulseMagnitude = SerializeValue(ReadProperty(component, "windPulseMagnitude")),
                    windPulseFrequency = SerializeValue(ReadProperty(component, "windPulseFrequency"))
                },
                "Projector" => new
                {
                    type = component.GetType().FullName,
                    enabled = ComponentEnabled(component),
                    nearClipPlane = SerializeValue(ReadProperty(component, "nearClipPlane")),
                    farClipPlane = SerializeValue(ReadProperty(component, "farClipPlane")),
                    fieldOfView = SerializeValue(ReadProperty(component, "fieldOfView")),
                    aspectRatio = SerializeValue(ReadProperty(component, "aspectRatio")),
                    orthographic = SerializeValue(ReadProperty(component, "orthographic")),
                    orthographicSize = SerializeValue(ReadProperty(component, "orthographicSize")),
                    material = SerializeObjectReference(ReadProperty(component, "material") as UnityEngine.Object),
                    ignoreLayers = SerializeLayerMask(ReadProperty(component, "ignoreLayers"))
                },
                "LODGroup" => BuildLODGroupSummary((LODGroup)component),
                _ => new
                {
                    type = component.GetType().FullName,
                    enabled = ComponentEnabled(component)
                }
            };
        }

        static object BuildLODGroupSummary(LODGroup lodGroup)
        {
            var lods = lodGroup.GetLODs();
            return new
            {
                type = lodGroup.GetType().FullName,
                enabled = lodGroup.enabled,
                localReferencePoint = SerializeVector3(lodGroup.localReferencePoint),
                size = lodGroup.size,
                fadeMode = lodGroup.fadeMode.ToString(),
                animateCrossFading = lodGroup.animateCrossFading,
                lastLODBillboard = lodGroup.lastLODBillboard,
                lodCount = lods.Length,
                lods = lods.Select((lod, index) => new
                {
                    index,
                    screenRelativeTransitionHeight = lod.screenRelativeTransitionHeight,
                    fadeTransitionWidth = lod.fadeTransitionWidth,
                    rendererCount = lod.renderers?.Count(renderer => renderer != null) ?? 0,
                    renderers = (lod.renderers ?? Array.Empty<Renderer>())
                        .Where(renderer => renderer != null)
                        .Select(SerializeRendererReference)
                        .ToArray()
                }).ToArray()
            };
        }

        static object SerializeRendererReference(Renderer renderer)
        {
            return new
            {
                name = renderer.name,
                type = renderer.GetType().FullName,
                objectId = UnityApiAdapter.GetObjectId(renderer),
                gameObjectId = UnityApiAdapter.GetObjectId(renderer.gameObject),
                path = SceneObjectLocator.GetHierarchyPath(renderer.gameObject),
                enabled = renderer.enabled,
                sortingLayerName = renderer.sortingLayerName,
                sortingOrder = renderer.sortingOrder,
                renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(renderer)
            };
        }

        static object BuildRenderSettingsSummary()
        {
            return new
            {
                ambientMode = RenderSettings.ambientMode.ToString(),
                ambientLight = SerializeColor(RenderSettings.ambientLight),
                fog = RenderSettings.fog,
                fogColor = SerializeColor(RenderSettings.fogColor),
                fogDensity = RenderSettings.fogDensity,
                skybox = SerializeObjectReference(RenderSettings.skybox)
            };
        }

        static object ReadProperty(object target, string name)
        {
            return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
        }

        static bool? ComponentEnabled(Component component)
        {
            return component is Behaviour behaviour ? behaviour.enabled : (bool?)null;
        }

        static object SerializeValue(object value)
        {
            return value switch
            {
                null => null,
                UnityEngine.Object obj => SerializeObjectReference(obj),
                Color color => SerializeColor(color),
                Vector2 vector2 => SerializeVector2(vector2),
                Vector3 vector3 => SerializeVector3(vector3),
                Vector4 vector4 => new { x = vector4.x, y = vector4.y, z = vector4.z, w = vector4.w },
                Quaternion quaternion => new { x = quaternion.x, y = quaternion.y, z = quaternion.z, w = quaternion.w },
                Enum enumValue => enumValue.ToString(),
                _ => value
            };
        }

        static object SerializeLayerMask(object value)
        {
            if (value is LayerMask layerMask)
                return SerializeLayerMask(layerMask.value);
            if (value is int mask)
                return SerializeLayerMask(mask);
            return null;
        }

        static object SerializeLayerMask(int mask)
        {
            var names = new List<string>();
            for (var i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0)
                    continue;
                var name = LayerMask.LayerToName(i);
                names.Add(string.IsNullOrWhiteSpace(name) ? i.ToString(CultureInfo.InvariantCulture) : name);
            }

            return new
            {
                mask,
                mode = mask == 0 ? "Nothing" : mask == -1 ? "Everything" : "Mixed",
                names = names.ToArray()
            };
        }

        static object SerializeObjectReference(UnityEngine.Object value)
        {
            if (value == null)
                return null;
            var path = AssetDatabase.GetAssetPath(value);
            return new { name = value.name, type = value.GetType().FullName, assetPath = string.IsNullOrWhiteSpace(path) ? null : path };
        }

        static GameObject ResolveOrCreateTarget(JObject parameters, string fallbackName)
        {
            var target = ResolveTarget(parameters, required: false);
            if (target != null)
                return target;

            var go = new GameObject(GetString(parameters, "Name", "name") ?? fallbackName);
            Undo.RegisterCreatedObjectUndo(go, "Create Rendering Object");
            AssignParent(go, parameters);
            return go;
        }

        static void AssignParent(GameObject target, JObject parameters)
        {
            var parentToken = GetToken(parameters, "Parent", "parent");
            if (parentToken == null || parentToken.Type == JTokenType.Null || string.IsNullOrWhiteSpace(parentToken.ToString()))
                return;

            var parent = SceneObjectLocator.FindObject(parentToken.ToString(), GetString(parameters, "SearchMethod", "searchMethod", "search_method"), new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            if (parent != null)
                target.transform.SetParent(parent.transform, worldPositionStays: false);
        }

        static GameObject ResolveTarget(JObject parameters, bool required)
        {
            var target = GetToken(parameters, "Target", "target", "GameObject", "gameObject", "game_object");
            if (target == null || target.Type == JTokenType.Null || string.IsNullOrWhiteSpace(target.ToString()))
            {
                if (required)
                    throw new InvalidOperationException("Target GameObject is required.");
                return null;
            }

            var go = SceneObjectLocator.FindObject(target.ToString(), GetString(parameters, "SearchMethod", "searchMethod", "search_method"), new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            if (go == null && required)
                throw new InvalidOperationException($"Target GameObject '{target}' was not found.");
            return go;
        }

        static Type FindType(params string[] names)
        {
            foreach (var name in names.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                var direct = Type.GetType(name, false);
                if (direct != null)
                    return direct;

                var assemblyQualified = ResolveAssemblyQualifiedType(name);
                if (assemblyQualified != null)
                    return assemblyQualified;

                var componentName = StripAssemblyQualifier(name);
                if (ComponentResolver.TryResolve(componentName, out var componentType, out _))
                    return componentType;

#if UNITY_EDITOR
                var typeCacheMatch = TypeCache.GetTypesDerivedFrom<Component>()
                    .FirstOrDefault(type =>
                        string.Equals(type.FullName, componentName, StringComparison.Ordinal) ||
                        string.Equals(type.Name, componentName, StringComparison.Ordinal));
                if (typeCacheMatch != null)
                    return typeCacheMatch;
#endif

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

                    var match = types.FirstOrDefault(type =>
                        string.Equals(type.FullName, name, StringComparison.Ordinal) ||
                        string.Equals(type.Name, name, StringComparison.Ordinal) ||
                        string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        return match;
                }
            }

            return null;
        }

        static string StripAssemblyQualifier(string name)
        {
            var comma = name.IndexOf(',');
            return comma < 0 ? name.Trim() : name.Substring(0, comma).Trim();
        }

        static Type ResolveAssemblyQualifiedType(string name)
        {
            var comma = name.IndexOf(',');
            if (comma < 0)
                return null;

            var typeName = name.Substring(0, comma).Trim();
            var assemblyName = name.Substring(comma + 1).Trim();
            var nextComma = assemblyName.IndexOf(',');
            if (nextComma >= 0)
                assemblyName = assemblyName.Substring(0, nextComma).Trim();

            if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(assemblyName))
                return null;

            try
            {
                var assembly = Assembly.Load(new AssemblyName(assemblyName));
                return assembly.GetType(typeName, false, ignoreCase: true);
            }
            catch
            {
                return null;
            }
        }

        static Vector3? ParseVector3(JToken token)
        {
            if (token is JArray arr && arr.Count >= 3)
                return new Vector3(ReadFloat(arr, 0), ReadFloat(arr, 1), ReadFloat(arr, 2));
            if (token is JObject obj)
                return new Vector3(ReadFloatMember(obj, "x", 0), ReadFloatMember(obj, "y", 0), ReadFloatMember(obj, "z", 0));
            return null;
        }

        static Vector2Int? ParseVector2Int(JToken token)
        {
            if (token is JArray arr && arr.Count >= 2)
                return new Vector2Int(ReadInt(arr, 0), ReadInt(arr, 1));
            if (token is JObject obj)
                return new Vector2Int(ReadIntMember(obj, "x", 0), ReadIntMember(obj, "y", 0));
            return null;
        }

        static Vector2? ParseVector2(JToken token)
        {
            if (token is JArray arr && arr.Count >= 2)
                return new Vector2(ReadFloat(arr, 0), ReadFloat(arr, 1));
            if (token is JObject obj)
                return new Vector2(ReadFloatMember(obj, "x", 0), ReadFloatMember(obj, "y", 0));
            return null;
        }

        static Color ParseColor(JToken token, Color fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
                return fallback;
            if (token.Type == JTokenType.String && ColorUtility.TryParseHtmlString(token.ToString(), out var htmlColor))
                return htmlColor;
            if (token is JArray arr && arr.Count >= 3)
                return new Color(ReadFloat(arr, 0), ReadFloat(arr, 1), ReadFloat(arr, 2), arr.Count > 3 ? ReadFloat(arr, 3) : 1f);
            if (token is JObject obj)
                return new Color(ReadFloatMember(obj, "r", fallback.r), ReadFloatMember(obj, "g", fallback.g), ReadFloatMember(obj, "b", fallback.b), ReadFloatMember(obj, "a", fallback.a));
            return fallback;
        }

        static int ReadLayerMask(JToken token, int fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
                return fallback;
            if (token.Type == JTokenType.Integer)
                return token.ToObject<int>();
            if (token.Type == JTokenType.String)
            {
                var text = token.ToString().Trim();
                if (string.Equals(text, "Everything", StringComparison.OrdinalIgnoreCase))
                    return -1;
                if (string.Equals(text, "Nothing", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "None", StringComparison.OrdinalIgnoreCase))
                    return 0;
                var layer = LayerMask.NameToLayer(text);
                return layer >= 0 ? 1 << layer : fallback;
            }
            if (token is JArray arr)
            {
                var mask = 0;
                foreach (var item in arr)
                {
                    var value = ReadLayerMask(item, 0);
                    mask |= value;
                }
                return mask;
            }
            if (token is JObject obj)
            {
                var nested = GetToken(obj, "Names", "names", "Layers", "layers", "Value", "value", "Mask", "mask");
                if (nested != null)
                    return ReadLayerMask(nested, fallback);
            }
            return fallback;
        }

        static void SetSerializedInt(SerializedObject serializedObject, int value, params string[] paths)
        {
            foreach (var path in paths)
            {
                var property = serializedObject.FindProperty(path);
                if (property == null)
                    continue;
                if (property.propertyType == SerializedPropertyType.Integer)
                {
                    property.intValue = value;
                    return;
                }
                if (property.propertyType == SerializedPropertyType.Enum && value >= 0 && value < property.enumNames.Length)
                {
                    property.enumValueIndex = value;
                    return;
                }
            }
        }

        static void SetSerializedFloat(SerializedObject serializedObject, float value, params string[] paths)
        {
            foreach (var path in paths)
            {
                var property = serializedObject.FindProperty(path);
                if (property != null && property.propertyType == SerializedPropertyType.Float)
                {
                    property.floatValue = value;
                    return;
                }
            }
        }

        static void SetSerializedFloatIfPresent(SerializedObject serializedObject, JObject parameters, string serializedPath, params string[] keys)
        {
            var token = GetToken(parameters, keys);
            if (!TryReadFloat(token, out var value))
                return;

            SetSerializedFloat(serializedObject, value, serializedPath);
        }

        static void SetSerializedBool(SerializedObject serializedObject, bool value, params string[] paths)
        {
            foreach (var path in paths)
            {
                var property = serializedObject.FindProperty(path);
                if (property != null && property.propertyType == SerializedPropertyType.Boolean)
                {
                    property.boolValue = value;
                    return;
                }
            }
        }

        static void SetSerializedColor(SerializedObject serializedObject, Color value, params string[] paths)
        {
            foreach (var path in paths)
            {
                var property = serializedObject.FindProperty(path);
                if (property != null && property.propertyType == SerializedPropertyType.Color)
                {
                    property.colorValue = value;
                    return;
                }
            }
        }

        static void CopyIfPresent(JObject source, JObject dest, string canonical, params string[] aliases)
        {
            foreach (var alias in aliases.Concat(new[] { canonical }))
            {
                if (source.TryGetValue(alias, StringComparison.OrdinalIgnoreCase, out var token))
                {
                    dest[canonical] = token.DeepClone();
                    return;
                }
            }
        }

        static void EnsureParentDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || AssetDatabase.IsValidFolder(directory))
                return;

            var parts = directory.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var normalized = path.Trim().Replace('\\', '/').TrimStart('/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return normalized;
            return "Assets/" + normalized;
        }

        static object SerializeVector2(Vector2 value) => new { x = value.x, y = value.y };
        static object SerializeVector3(Vector3 value) => new { x = value.x, y = value.y, z = value.z };
        static object SerializeColor(Color value) => new { r = value.r, g = value.g, b = value.b, a = value.a };
        static string Normalize(string value) => (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

        static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            return !string.IsNullOrWhiteSpace(value) && Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
        }

        static JToken GetToken(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
                    return token;
            return null;
        }

        static string GetString(JObject obj, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString().Trim();
        }

        static bool GetBool(JObject obj, bool defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && bool.TryParse(token.ToString(), out var value) ? value : defaultValue;
        }

        static float GetFloat(JObject obj, float defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return TryReadFloat(token, out var value) ? value : defaultValue;
        }

        static float ReadFloat(JArray arr, int index)
        {
            return arr.Count > index && TryReadFloat(arr[index], out var value) ? value : 0f;
        }

        static int ReadInt(JArray arr, int index)
        {
            return arr.Count > index && int.TryParse(arr[index].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }

        static float ReadFloatMember(JObject obj, string property, float defaultValue)
        {
            return obj.TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var token) &&
                   TryReadFloat(token, out var value)
                ? value
                : defaultValue;
        }

        static int ReadIntMember(JObject obj, string property, int defaultValue)
        {
            return obj.TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var token) &&
                   int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : defaultValue;
        }

        static int GetInt(JObject obj, int defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : defaultValue;
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
            var text = token.ToString();
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
    }
}
#pragma warning restore 0618
