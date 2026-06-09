#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Captures Unity visual state into PNG files for MCP clients.
    /// </summary>
    public static class CaptureView
    {
        const int DefaultWidth = 1280;
        const int DefaultHeight = 720;
        const int MinDimension = 64;
        const int MaxDimension = 4096;
        const int DefaultMaxObjects = 40;
        const int MaxMetadataObjects = 200;
        const int DefaultSeriesCount = 3;
        const int MaxSeriesCount = 30;
        const int DefaultContactSheetGutter = 8;
        const int MaxContactSheetCells = 64;
        const int MaxContactSheetDimension = 8192;
        const float DefaultNearbyRadius = 8f;
        const int DiffThreshold = 12;
        const int DefaultScreenshotTimeoutMs = 5000;
        const int MaxScreenshotTimeoutMs = 30000;
        static readonly CaptureViewDirection[] DefaultContactSheetViews =
        {
            CaptureViewDirection.Iso,
            CaptureViewDirection.Front,
            CaptureViewDirection.Top,
            CaptureViewDirection.Right
        };

        static readonly Color32[] OverlayPalette =
        {
            new Color32(64, 220, 255, 255),
            new Color32(116, 255, 128, 255),
            new Color32(255, 128, 224, 255),
            new Color32(255, 176, 72, 255),
            new Color32(176, 144, 255, 255),
            new Color32(230, 230, 230, 255)
        };

        public const string Title = "Capture Unity view";

        public const string Description = @"Capture Unity visual state to a PNG file for AI inspection.

Use this when an agent needs to see the current scene, camera framing, layout, or visual result without relying only on metadata.

Args:
    action: CaptureSceneView, CaptureGameView, CaptureGameCamera, CaptureSelection, CaptureObject, CapturePrefabStage, CaptureSceneOverview, CaptureAroundObject, CaptureSeries, CaptureContactSheet, CaptureDiff, ClearCaptures, or ListCameras.
    width, height: PNG dimensions, clamped to 64..4096. CaptureGameView returns the actual Game View screenshot size instead.
    target: Optional GameObject name/path/id to center and frame for Scene View captures. If provided but not found, the capture fails instead of returning an unrelated view.
    search_method: by_name, by_id, by_path, or by_id_or_name_or_path.
    view: Current, Iso, Front, Back, Left, Right, Top, or Bottom for Scene View captures.
    zoom: Close, Normal, or Far when framing a target.
    orthographic: Optional Scene View projection override.
    camera: Optional Camera name/path/id for game-camera capture.
    output_directory, file_name, tag: Optional output controls.
    transparent_background: Preserve PNG alpha. Defaults to false so captures match Unity's opaque Game View/Scene View presentation.
    overlay: Draw target/nearby bounds, color-coded numeric markers, and a compact non-overlapping legend.
    separate_overlay: Keep the main capture clean and write visual hints into a separate transparent PNG layer plus an optional composite PNG.
    include_nearby_objects, nearby_radius, max_objects: Control visual-context metadata.
    series_count, series_interval_seconds: CaptureSeries and CaptureContactSheet time-slice controls.
    views, contact_sheet_columns, include_contact_sheet_labels: CaptureContactSheet multi-view grid controls.
    baseline_path, compare_path: CaptureDiff inputs.
    delete_all, keep_latest, max_age_days: ClearCaptures controls.
    advance_ms, simulate_particles, sample_animations: Best-effort animated/VFX preview advance before rendering.
    readback_mode: Immediate or GpuReadback. GpuReadback uses synchronous AsyncGPUReadback with ReadPixels fallback for SRP-heavy captures.
    screenshot_timeout_ms: Maximum wait for exact Game View screenshot file creation.

Returns:
    success, message, and data with PNG path(s), file URI(s), dimensions, source, project identity, active scene, camera, selected target, nearby/visible object metadata, and optional diff/cleanup results.

Notes:
    CaptureSceneView and CaptureGameCamera render through Unity cameras into a RenderTexture. CaptureGameView asks Unity to write the post-render Game View screenshot after repaint/update, useful when camera.Render misses overlays, SRP post-processing, or Game View presentation details. CaptureContactSheet stitches several Scene View directions and optional time slices into one PNG for fast spatial inspection.";

        [McpSchema("UniBridge_CaptureView")]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new
                    {
                        type = "string",
                        description = "Capture operation. Use target-aware actions when an AI agent needs a controlled view of a scene object, prefab stage, overview, series, diff, or cleanup.",
                        @enum = new[]
                        {
                            "CaptureSceneView",
                            "CaptureGameView",
                            "CaptureGameCamera",
                            "CaptureSelection",
                            "CaptureObject",
                            "CapturePrefabStage",
                            "CaptureSceneOverview",
                            "CaptureAroundObject",
                            "CaptureSeries",
                            "CaptureContactSheet",
                            "CaptureDiff",
                            "ClearCaptures",
                            "ListCameras"
                        },
                        @default = "CaptureSceneView"
                    },
                    Width = new { type = "integer", description = "PNG width in pixels. Values are clamped between 64 and 4096. CaptureGameView returns the actual Game View screenshot size.", @default = 1280 },
                    Height = new { type = "integer", description = "PNG height in pixels. Values are clamped between 64 and 4096. CaptureGameView returns the actual Game View screenshot size.", @default = 720 },
                    Target = new { type = "string", description = "Scene object name, hierarchy path, or entity/instance ID to center and frame when capturing the Scene View. If provided but not found, the capture fails." },
                    SearchMethod = new
                    {
                        type = "string",
                        description = "How to resolve Target or Camera.",
                        @enum = new[] { "by_name", "by_id", "by_path", "by_id_or_name_or_path" },
                        @default = "by_id_or_name_or_path"
                    },
                    View = new
                    {
                        type = "string",
                        description = "Scene View camera direction.",
                        @enum = new[] { "Current", "Iso", "Front", "Back", "Left", "Right", "Top", "Bottom" },
                        @default = "Current"
                    },
                    Views = new
                    {
                        description = "For CaptureContactSheet, Scene View directions to render into one stitched PNG. Accepts an array or comma-separated string.",
                        oneOf = new object[]
                        {
                            new { type = "array", items = new { type = "string", @enum = new[] { "Current", "Iso", "Front", "Back", "Left", "Right", "Top", "Bottom" } } },
                            new { type = "string" }
                        }
                    },
                    Zoom = new
                    {
                        type = "string",
                        description = "Scene View zoom level when framing a target.",
                        @enum = new[] { "Close", "Normal", "Far" },
                        @default = "Normal"
                    },
                    Orthographic = new { type = "boolean", description = "Force orthographic Scene View capture. If omitted, the current Scene View projection is preserved." },
                    Camera = new { type = "string", description = "Camera name, hierarchy path, or entity/instance ID for CaptureGameCamera. When empty, UniBridge uses Main Camera or the first enabled Camera." },
                    OutputDirectory = new { type = "string", description = "Optional folder where PNG files should be written. Defaults to ~/.unibridge/captures/<project>." },
                    FileName = new { type = "string", description = "Optional file name. .png is appended if missing. Defaults to action, optional tag, and timestamp." },
                    Tag = new { type = "string", description = "Optional human-readable tag added to the generated file name and returned metadata." },
                    TransparentBackground = new { type = "boolean", description = "Preserve alpha in the PNG. Defaults to false so captures match Unity's opaque Game View and Scene View presentation.", @default = false },
                    Overlay = new { type = "boolean", description = "Draw target/nearby object bounds, numeric markers, and a compact legend into the PNG.", @default = false },
                    SeparateOverlay = new { type = "boolean", description = "Write visual hints as a separate transparent PNG layer and keep the main capture clean.", @default = false },
                    CompositeOverlay = new { type = "boolean", description = "When SeparateOverlay is true, also write a convenience composite PNG combining the clean capture and overlay layer.", @default = true },
                    IncludeNearbyObjects = new { type = "boolean", description = "Include nearby object metadata around the target. Defaults to true for target-based captures." },
                    NearbyRadius = new { type = "number", description = "World-space radius for nearby object metadata and CaptureAroundObject context.", @default = 8 },
                    MaxObjects = new { type = "integer", description = "Maximum number of visible/nearby objects returned in metadata and labeled in overlays.", @default = 40 },
                    SeriesCount = new { type = "integer", description = "Number of frames for CaptureSeries, or time slices per view for CaptureContactSheet. Values are clamped to 1..30; CaptureContactSheet defaults to 1 when omitted.", @default = 3 },
                    SeriesIntervalSeconds = new { type = "number", description = "Best-effort delay between CaptureSeries frames or CaptureContactSheet time slices in seconds. This blocks the Editor command while capturing.", @default = 0 },
                    ContactSheetColumns = new { type = "integer", description = "For CaptureContactSheet, optional column count for the stitched PNG grid. Defaults to a compact square-ish layout." },
                    IncludeContactSheetLabels = new { type = "boolean", description = "For CaptureContactSheet, draw compact labels such as ISO 01 or FRONT 02 into each grid cell.", @default = true },
                    BaselinePath = new { type = "string", description = "Baseline PNG path for CaptureDiff." },
                    ComparePath = new { type = "string", description = "Comparison PNG path for CaptureDiff." },
                    DeleteAll = new { type = "boolean", description = "For ClearCaptures, delete all PNG captures in the selected capture directory." },
                    KeepLatest = new { type = "integer", description = "For ClearCaptures, keep the newest N captures and delete older captures." },
                    MaxAgeDays = new { type = "number", description = "For ClearCaptures, delete captures older than this many days." },
                    AdvanceMs = new { type = "integer", description = "Advance animated/VFX targets before capture by this many milliseconds. Values are clamped to 0..30000.", @default = 0 },
                    SimulateParticles = new { type = "boolean", description = "When AdvanceMs > 0, simulate ParticleSystem components under capture targets before rendering. Defaults to true when AdvanceMs is set." },
                    SampleAnimations = new { type = "boolean", description = "When AdvanceMs > 0, sample Animator/Animation clips under capture targets before rendering. Defaults to true when AdvanceMs is set." },
                    ReadbackMode = new
                    {
                        type = "string",
                        description = "RenderTexture readback mode. Immediate uses Texture2D.ReadPixels. GpuReadback uses synchronous AsyncGPUReadback with ReadPixels fallback.",
                        @enum = new[] { "Immediate", "GpuReadback" },
                        @default = "Immediate"
                    },
                    ScreenshotTimeoutMs = new { type = "integer", description = "For CaptureGameView, maximum milliseconds to wait for Unity to finish writing the exact Game View screenshot.", @default = 5000 }
                }
            };
        }

        [McpTool("UniBridge_CaptureView", Description, Title, Groups = new[] { "core", "vision", "scene", "debug" }, EnabledByDefault = true)]
        public static async Task<object> HandleCommand(JObject rawParameters)
        {
            var parameters = ParseParameters(rawParameters);

            try
            {
                switch (parameters.Action)
                {
                    case CaptureViewAction.CaptureSceneView:
                    case CaptureViewAction.CaptureSelection:
                    case CaptureViewAction.CaptureObject:
                    case CaptureViewAction.CapturePrefabStage:
                    case CaptureViewAction.CaptureSceneOverview:
                    case CaptureViewAction.CaptureAroundObject:
                        return CaptureSceneView(parameters);
                    case CaptureViewAction.CaptureGameView:
                        return await CaptureGameView(parameters);
                    case CaptureViewAction.CaptureGameCamera:
                        return CaptureGameCamera(parameters);
                    case CaptureViewAction.CaptureSeries:
                        return CaptureSeries(parameters);
                    case CaptureViewAction.CaptureContactSheet:
                        return CaptureContactSheet(parameters);
                    case CaptureViewAction.CaptureDiff:
                        return CaptureDiff(parameters);
                    case CaptureViewAction.ClearCaptures:
                        return ClearCaptures(parameters);
                    case CaptureViewAction.ListCameras:
                        return ListCameras();
                    default:
                        return Response.Error($"Unsupported capture action '{parameters.Action}'.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CaptureView] {parameters.Action} failed: {ex}");
                return Response.Error($"Capture action '{parameters.Action}' failed: {ex.Message}");
            }
        }

        static CaptureViewParams ParseParameters(JObject raw)
        {
            raw ??= new JObject();

            return new CaptureViewParams
            {
                Action = GetEnum(raw, CaptureViewAction.CaptureSceneView, "Action", "action"),
                Width = GetNullableInt(raw, "Width", "width"),
                Height = GetNullableInt(raw, "Height", "height"),
                Target = GetString(raw, "Target", "target"),
                SearchMethod = GetString(raw, "SearchMethod", "searchMethod", "search_method") ?? "by_id_or_name_or_path",
                View = GetEnum(raw, CaptureViewDirection.Current, "View", "view"),
                Views = GetEnumArray<CaptureViewDirection>(raw, "Views", "views", "ViewDirections", "viewDirections", "view_directions"),
                Zoom = GetEnum(raw, CaptureViewZoom.Normal, "Zoom", "zoom"),
                Orthographic = GetNullableBool(raw, "Orthographic", "orthographic"),
                Camera = GetString(raw, "Camera", "camera"),
                OutputDirectory = GetString(raw, "OutputDirectory", "outputDirectory", "output_directory"),
                FileName = GetString(raw, "FileName", "fileName", "file_name"),
                Tag = GetString(raw, "Tag", "tag"),
                TransparentBackground = GetNullableBool(raw, "TransparentBackground", "transparentBackground", "transparent_background"),
                Overlay = GetNullableBool(raw, "Overlay", "overlay"),
                SeparateOverlay = GetNullableBool(raw, "SeparateOverlay", "separateOverlay", "separate_overlay"),
                CompositeOverlay = GetNullableBool(raw, "CompositeOverlay", "compositeOverlay", "composite_overlay"),
                IncludeNearbyObjects = GetNullableBool(raw, "IncludeNearbyObjects", "includeNearbyObjects", "include_nearby_objects"),
                NearbyRadius = GetNullableFloat(raw, "NearbyRadius", "nearbyRadius", "nearby_radius"),
                MaxObjects = GetNullableInt(raw, "MaxObjects", "maxObjects", "max_objects"),
                SeriesCount = GetNullableInt(raw, "SeriesCount", "seriesCount", "series_count"),
                SeriesIntervalSeconds = GetNullableFloat(raw, "SeriesIntervalSeconds", "seriesIntervalSeconds", "series_interval_seconds"),
                ContactSheetColumns = GetNullableInt(raw, "ContactSheetColumns", "contactSheetColumns", "contact_sheet_columns", "Columns", "columns"),
                IncludeContactSheetLabels = GetNullableBool(raw, "IncludeContactSheetLabels", "includeContactSheetLabels", "include_contact_sheet_labels", "Labels", "labels"),
                BaselinePath = GetString(raw, "BaselinePath", "baselinePath", "baseline_path"),
                ComparePath = GetString(raw, "ComparePath", "comparePath", "compare_path"),
                DeleteAll = GetNullableBool(raw, "DeleteAll", "deleteAll", "delete_all"),
                KeepLatest = GetNullableInt(raw, "KeepLatest", "keepLatest", "keep_latest"),
                MaxAgeDays = GetNullableFloat(raw, "MaxAgeDays", "maxAgeDays", "max_age_days"),
                AdvanceMs = GetNullableInt(raw, "AdvanceMs", "advanceMs", "advance_ms"),
                SimulateParticles = GetNullableBool(raw, "SimulateParticles", "simulateParticles", "simulate_particles"),
                SampleAnimations = GetNullableBool(raw, "SampleAnimations", "sampleAnimations", "sample_animations"),
                ReadbackMode = GetEnum(raw, CaptureReadbackMode.Immediate, "ReadbackMode", "readbackMode", "readback_mode", "CaptureBackend", "captureBackend", "capture_backend"),
                ScreenshotTimeoutMs = GetNullableInt(raw, "ScreenshotTimeoutMs", "screenshotTimeoutMs", "screenshot_timeout_ms", "GameViewTimeoutMs", "gameViewTimeoutMs", "game_view_timeout_ms")
            };
        }

        static string GetString(JObject raw, params string[] names)
        {
            var token = GetToken(raw, names);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        static int? GetNullableInt(JObject raw, params string[] names)
        {
            var token = GetToken(raw, names);
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.ToObject<int?>();
        }

        static bool? GetNullableBool(JObject raw, params string[] names)
        {
            var token = GetToken(raw, names);
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.ToObject<bool?>();
        }

        static float? GetNullableFloat(JObject raw, params string[] names)
        {
            var token = GetToken(raw, names);
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.ToObject<float?>();
        }

        static TEnum GetEnum<TEnum>(JObject raw, TEnum fallback, params string[] names) where TEnum : struct
        {
            var token = GetToken(raw, names);
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            if (token.Type == JTokenType.Integer && Enum.IsDefined(typeof(TEnum), token.ToObject<int>()))
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), token.ToObject<int>());
            }

            return Enum.TryParse(token.ToString(), ignoreCase: true, out TEnum value) ? value : fallback;
        }

        static TEnum[] GetEnumArray<TEnum>(JObject raw, params string[] names) where TEnum : struct
        {
            var token = GetToken(raw, names);
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            var values = new List<TEnum>();
            IEnumerable<JToken> items;
            if (token.Type == JTokenType.Array)
            {
                items = token.Children();
            }
            else
            {
                var text = token.ToString();
                if (string.Equals(text, "MultiView", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    return typeof(TEnum) == typeof(CaptureViewDirection)
                        ? DefaultContactSheetViews.Cast<TEnum>().ToArray()
                        : null;
                }

                items = text
                    .Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => new JValue(part.Trim()));
            }

            foreach (var item in items)
            {
                if (item == null || item.Type == JTokenType.Null)
                {
                    continue;
                }

                TEnum value;
                if (item.Type == JTokenType.Integer)
                {
                    var intValue = item.ToObject<int>();
                    if (!Enum.IsDefined(typeof(TEnum), intValue))
                    {
                        continue;
                    }

                    value = (TEnum)Enum.ToObject(typeof(TEnum), intValue);
                }
                else if (!Enum.TryParse(item.ToString(), ignoreCase: true, out value))
                {
                    continue;
                }

                if (!values.Contains(value))
                {
                    values.Add(value);
                }
            }

            return values.Count == 0 ? null : values.ToArray();
        }

        static JToken GetToken(JObject raw, params string[] names)
        {
            foreach (var name in names)
            {
                if (raw.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var token))
                {
                    return token;
                }
            }

            return null;
        }

        static object CaptureSceneView(CaptureViewParams parameters)
        {
            if (Application.isBatchMode)
            {
                return Response.Error("Scene View capture is not available in Unity Batch Mode. Use CaptureGameCamera with a project camera instead.");
            }

            var width = ClampDimension(parameters.Width, DefaultWidth);
            var height = ClampDimension(parameters.Height, DefaultHeight);
            var previousSelection = Selection.objects;
            using var sceneViewHandle = GetOrCreateSceneView();
            var sceneView = sceneViewHandle.SceneView;
            if (sceneView == null || sceneView.camera == null)
            {
                return Response.Error("Scene View camera is not available.");
            }

            var previousRotation = sceneView.rotation;
            var previousPivot = sceneView.pivot;
            var previousSize = sceneView.size;
            var previousOrthographic = sceneView.orthographic;
            var previousIn2DMode = sceneView.in2DMode;
            var targetResult = ResolveSceneCaptureTarget(parameters);
            if (!targetResult.Success)
            {
                return Response.Error(targetResult.Error);
            }

            try
            {
                var frame = ApplySceneViewFraming(sceneView, targetResult.Target, targetResult.FrameBounds, parameters);
                using var framedCameraHandle = frame == null
                    ? null
                    : CreateFramedRenderCamera(sceneView.camera, sceneView.rotation, sceneView.orthographic, frame.Bounds, parameters, width, height);
                var renderCamera = framedCameraHandle?.Camera ?? sceneView.camera;
                var renderSource = framedCameraHandle == null
                    ? "SceneViewCamera.RenderTexture"
                    : "FramedSceneCamera.RenderTexture";
                using var advance = CaptureObjectAdvancer.Advance(
                    ResolveAdvanceTargets(parameters, targetResult.Target, renderCamera),
                    parameters.AdvanceMs,
                    parameters.SimulateParticles,
                    parameters.SampleAnimations);
                SceneView.RepaintAll();
                sceneView.Repaint();
                EditorApplication.QueuePlayerLoopUpdate();

                var outputPath = BuildOutputPath(parameters, targetResult.FilePrefix);
                var visualContext = BuildVisualContext(renderCamera, targetResult.Target, frame?.Bounds, parameters);
                var renderInfo = RenderCameraToPng(
                    renderCamera,
                    outputPath,
                    width,
                    height,
                    parameters.TransparentBackground == true,
                    ShouldRenderOverlay(parameters) ? visualContext : null,
                    parameters);
                var data = BuildCaptureData(
                    outputPath,
                    renderInfo.Width,
                    renderInfo.Height,
                    targetResult.CaptureKind,
                    renderSource,
                    parameters,
                    targetResult.Target,
                    renderCamera,
                    frame,
                    visualContext,
                    renderInfo);

                data["request"] = JToken.FromObject(BuildRequestInfo(parameters));
                data["view"] = parameters.View.ToString();
                data["zoom"] = parameters.Zoom.ToString();
                data["orthographic"] = renderCamera.orthographic;
                data["framedRenderCamera"] = framedCameraHandle == null
                    ? null
                    : JToken.FromObject(framedCameraHandle.Info);
                data["stage"] = targetResult.StageInfo == null ? null : JToken.FromObject(targetResult.StageInfo);
                data["advance"] = JToken.FromObject(advance.Info);
                data["overlay"] = JToken.FromObject(new
                {
                    enabled = ShouldRenderOverlay(parameters),
                    separate = parameters.SeparateOverlay == true,
                    composite = parameters.SeparateOverlay == true && parameters.CompositeOverlay != false,
                    itemCount = visualContext?.OverlayItems.Count ?? 0
                });

                return Response.Success($"Captured Scene View to '{outputPath}'.", data);
            }
            finally
            {
                Selection.objects = previousSelection;
                if (sceneView.in2DMode)
                {
                    sceneView.in2DMode = false;
                }

                sceneView.rotation = previousRotation;
                sceneView.pivot = previousPivot;
                sceneView.size = previousSize;
                sceneView.orthographic = previousOrthographic;
                sceneView.in2DMode = previousIn2DMode;
                sceneView.Repaint();
            }
        }

        static object CaptureGameCamera(CaptureViewParams parameters)
        {
            var camera = ResolveCamera(parameters);
            if (camera == null)
            {
                return Response.Error("No camera found. Provide camera by name/path/id, or add a Main Camera to the scene.");
            }

            var width = ClampDimension(parameters.Width, DefaultWidth);
            var height = ClampDimension(parameters.Height, DefaultHeight);
            var target = ResolveOptionalGameObject(parameters.Target, parameters.SearchMethod);
            if (target == null && !string.IsNullOrWhiteSpace(parameters.Target))
            {
                return Response.Error($"Target GameObject '{parameters.Target}' was not found.");
            }

            var outputPath = BuildOutputPath(parameters, "gamecamera");
            using var advance = CaptureObjectAdvancer.Advance(
                ResolveAdvanceTargets(parameters, target, camera),
                parameters.AdvanceMs,
                parameters.SimulateParticles,
                parameters.SampleAnimations);
            var visualContext = BuildVisualContext(camera, target, target == null ? (Bounds?)null : CalculateObjectBounds(target), parameters);
            var renderInfo = RenderCameraToPng(
                camera,
                outputPath,
                width,
                height,
                parameters.TransparentBackground == true,
                ShouldRenderOverlay(parameters) ? visualContext : null,
                parameters);
            var data = BuildCaptureData(
                outputPath,
                renderInfo.Width,
                renderInfo.Height,
                "GameCamera",
                "Camera.RenderTexture",
                parameters,
                target,
                camera,
                target == null ? null : new SceneViewFrame(target, CalculateObjectBounds(target)),
                visualContext,
                renderInfo);

            data["request"] = JToken.FromObject(BuildRequestInfo(parameters));
            data["advance"] = JToken.FromObject(advance.Info);
            data["overlay"] = JToken.FromObject(new
            {
                enabled = ShouldRenderOverlay(parameters),
                separate = parameters.SeparateOverlay == true,
                composite = parameters.SeparateOverlay == true && parameters.CompositeOverlay != false,
                itemCount = visualContext?.OverlayItems.Count ?? 0
            });
            return Response.Success($"Captured camera '{camera.name}' to '{outputPath}'.", data);
        }

        static async Task<object> CaptureGameView(CaptureViewParams parameters)
        {
            if (Application.isBatchMode)
            {
                return Response.Error("Exact Game View screenshot capture is not available in Unity Batch Mode. Use CaptureGameCamera with a project camera instead.");
            }

            var target = ResolveOptionalGameObject(parameters.Target, parameters.SearchMethod);
            if (target == null && !string.IsNullOrWhiteSpace(parameters.Target))
            {
                return Response.Error($"Target GameObject '{parameters.Target}' was not found.");
            }

            var outputPath = BuildOutputPath(parameters, "gameview");
            var tempPath = BuildTempGameViewScreenshotPath();
            Texture2D screenshotTexture = null;
            ScreenshotCaptureResult screenshot = default;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath.FullPath));
                if (File.Exists(tempPath.FullPath))
                {
                    File.Delete(tempPath.FullPath);
                }

                var camera = ResolveCamera(parameters);
                using var advance = CaptureObjectAdvancer.Advance(
                    ResolveAdvanceTargets(parameters, target, camera),
                    parameters.AdvanceMs,
                    parameters.SimulateParticles,
                    parameters.SampleAnimations);

                var gameViewWindow = FocusGameViewWindow();
                RepaintEditorViews(gameViewWindow);
                await WaitForEditorUpdateAsync(0);
                RepaintEditorViews(gameViewWindow);

                ScreenCapture.CaptureScreenshot(tempPath.RelativePath);
                screenshot = await WaitForScreenshotAsync(tempPath.FullPath, parameters.ScreenshotTimeoutMs);
                screenshotTexture = screenshot.Texture;

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.Copy(tempPath.FullPath, outputPath, true);

                var visualContext = camera == null
                    ? null
                    : BuildVisualContext(camera, target, target == null ? (Bounds?)null : CalculateObjectBounds(target), parameters);
                var renderInfo = new RenderInfo(screenshotTexture.width, screenshotTexture.height, null, null, "ScreenCapture");
                var data = BuildCaptureData(
                    outputPath,
                    renderInfo.Width,
                    renderInfo.Height,
                    "GameView",
                    "ScreenCapture.CaptureScreenshot",
                    parameters,
                    target,
                    camera,
                    target == null ? null : new SceneViewFrame(target, CalculateObjectBounds(target)),
                    visualContext,
                    renderInfo);

                data["request"] = JToken.FromObject(BuildRequestInfo(parameters));
                data["advance"] = JToken.FromObject(advance.Info);
                data["gameView"] = JToken.FromObject(new
                {
                    exactPostRender = true,
                    windowFound = gameViewWindow != null,
                    windowTitle = gameViewWindow?.titleContent?.text,
                    tempRelativePath = tempPath.RelativePath,
                    waitedMs = screenshot.ElapsedMs,
                    loadAttempts = screenshot.LoadAttempts,
                    tempFileSizeBytes = screenshot.FileSizeBytes,
                    requestedWidth = parameters.Width,
                    requestedHeight = parameters.Height,
                    returnedActualSize = true
                });
                data["overlay"] = JToken.FromObject(new
                {
                    requested = ShouldRenderOverlay(parameters),
                    rendered = false,
                    reason = ShouldRenderOverlay(parameters)
                        ? "CaptureGameView preserves exact post-render pixels, so overlays are reported as metadata only."
                        : null,
                    itemCount = visualContext?.OverlayItems.Count ?? 0
                });

                return Response.Success($"Captured exact Game View screenshot to '{outputPath}'.", data);
            }
            finally
            {
                if (screenshotTexture != null)
                {
                    Object.DestroyImmediate(screenshotTexture);
                }

                TryDeleteFile(tempPath.FullPath);
            }
        }

        static object CaptureSeries(CaptureViewParams parameters)
        {
            var count = Mathf.Clamp(parameters.SeriesCount.GetValueOrDefault(DefaultSeriesCount), 1, MaxSeriesCount);
            var intervalSeconds = Mathf.Clamp(parameters.SeriesIntervalSeconds.GetValueOrDefault(0f), 0f, 10f);
            var useGameCamera = !string.IsNullOrWhiteSpace(parameters.Camera);
            var captures = new JArray();
            var errors = new JArray();

            for (var index = 0; index < count; index++)
            {
                var frameParams = parameters with
                {
                    Action = useGameCamera ? CaptureViewAction.CaptureGameCamera : CaptureViewAction.CaptureSceneView,
                    FileName = BuildSeriesFileName(parameters, index, count),
                    Tag = string.IsNullOrWhiteSpace(parameters.Tag) ? $"series_{index + 1:000}" : $"{parameters.Tag}_series_{index + 1:000}"
                };

                var response = useGameCamera ? CaptureGameCamera(frameParams) : CaptureSceneView(frameParams);
                var responseJson = JObject.FromObject(response);
                if (responseJson.Value<bool?>("success") == true)
                {
                    captures.Add(responseJson["data"]);
                }
                else
                {
                    errors.Add(responseJson);
                }

                if (index < count - 1 && intervalSeconds > 0f)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    Thread.Sleep(Mathf.RoundToInt(intervalSeconds * 1000f));
                }
            }

            return Response.Success($"Captured {captures.Count} of {count} requested frame(s).", new
            {
                requestedCount = count,
                capturedCount = captures.Count,
                errorCount = errors.Count,
                source = useGameCamera ? "GameCamera" : "SceneView",
                intervalSeconds,
                blockingSeries = true,
                note = "CaptureSeries is synchronous and blocks the Editor command while capturing. Use short intervals for now.",
                captures,
                errors
            });
        }

        static object CaptureContactSheet(CaptureViewParams parameters)
        {
            if (Application.isBatchMode)
            {
                return Response.Error("Scene View contact sheet capture is not available in Unity Batch Mode. Use CaptureGameCamera or asset capture instead.");
            }

            var views = ResolveContactSheetViews(parameters).ToArray();
            if (views.Length == 0)
            {
                return Response.Error("CaptureContactSheet requires at least one valid Scene View direction.");
            }

            var requestedFramesPerView = Mathf.Clamp(parameters.SeriesCount.GetValueOrDefault(1), 1, MaxSeriesCount);
            var maxFramesPerView = Mathf.Max(1, MaxContactSheetCells / views.Length);
            var framesPerView = Mathf.Min(requestedFramesPerView, maxFramesPerView);
            var totalCells = views.Length * framesPerView;
            var columns = ResolveContactSheetColumns(parameters.ContactSheetColumns, totalCells);
            var rows = Mathf.CeilToInt(totalCells / (float)columns);
            var cellWidth = ClampDimension(parameters.Width, DefaultWidth);
            var cellHeight = ClampDimension(parameters.Height, DefaultHeight);
            var sheetWidth = cellWidth * columns + DefaultContactSheetGutter * (columns - 1);
            var sheetHeight = cellHeight * rows + DefaultContactSheetGutter * (rows - 1);
            if (sheetWidth > MaxContactSheetDimension || sheetHeight > MaxContactSheetDimension)
            {
                return Response.Error(
                    $"CaptureContactSheet output would be too large ({sheetWidth}x{sheetHeight}). Reduce Width, Height, Views, SeriesCount, or ContactSheetColumns.",
                    new
                    {
                        requestedWidth = cellWidth,
                        requestedHeight = cellHeight,
                        views = views.Select(view => view.ToString()).ToArray(),
                        requestedFramesPerView,
                        framesPerView,
                        columns,
                        rows,
                        maxContactSheetDimension = MaxContactSheetDimension
                    });
            }

            var intervalSeconds = Mathf.Clamp(parameters.SeriesIntervalSeconds.GetValueOrDefault(0f), 0f, 10f);
            var includeLabels = parameters.IncludeContactSheetLabels != false;
            var captures = new JArray();
            var errors = new JArray();
            var cells = new List<ContactSheetCell>();
            var cellIndex = 0;

            try
            {
                for (var viewIndex = 0; viewIndex < views.Length; viewIndex++)
                {
                    var view = views[viewIndex];
                    for (var frameIndex = 0; frameIndex < framesPerView; frameIndex++)
                    {
                        if (cellIndex > 0 && intervalSeconds > 0f)
                        {
                            EditorApplication.QueuePlayerLoopUpdate();
                            Thread.Sleep(Mathf.RoundToInt(intervalSeconds * 1000f));
                        }

                        var frameParams = parameters with
                        {
                            Action = CaptureViewAction.CaptureSceneView,
                            View = view,
                            FileName = BuildContactSheetFrameFileName(parameters, view, frameIndex, framesPerView),
                            Tag = BuildContactSheetFrameTag(parameters, view, frameIndex, framesPerView),
                            ContactSheetColumns = null,
                            IncludeContactSheetLabels = null
                        };

                        var response = CaptureSceneView(frameParams);
                        var responseJson = JObject.FromObject(response);
                        if (responseJson.Value<bool?>("success") != true)
                        {
                            errors.Add(responseJson);
                            cellIndex++;
                            continue;
                        }

                        var data = responseJson["data"] as JObject;
                        var imagePath = data?.Value<string>("imagePath") ?? data?.Value<string>("path");
                        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                        {
                            errors.Add(new JObject
                            {
                                ["success"] = false,
                                ["message"] = $"CaptureContactSheet frame {cellIndex + 1} did not return a readable image path.",
                                ["data"] = data
                            });
                            cellIndex++;
                            continue;
                        }

                        var texture = LoadPngTexture(imagePath);
                        var label = BuildContactSheetLabel(view, frameIndex, framesPerView);
                        cells.Add(new ContactSheetCell(
                            cellIndex + 1,
                            view,
                            frameIndex + 1,
                            label,
                            imagePath,
                            texture,
                            data));
                        captures.Add(new JObject
                        {
                            ["index"] = cellIndex + 1,
                            ["view"] = view.ToString(),
                            ["frame"] = frameIndex + 1,
                            ["label"] = label,
                            ["imagePath"] = imagePath,
                            ["fileUri"] = new Uri(imagePath).AbsoluteUri,
                            ["width"] = texture.width,
                            ["height"] = texture.height,
                            ["fileSizeBytes"] = new FileInfo(imagePath).Length,
                            ["captureKind"] = data?.Value<string>("captureKind"),
                            ["source"] = data?.Value<string>("source"),
                            ["readbackMode"] = data?.Value<string>("readbackMode")
                        });
                        cellIndex++;
                    }
                }

                if (cells.Count == 0)
                {
                    return Response.Error($"CaptureContactSheet failed to capture any frames ({errors.Count} error(s)).", new
                    {
                        requestedViews = views.Select(view => view.ToString()).ToArray(),
                        requestedFramesPerView,
                        framesPerView,
                        errors
                    });
                }

                var outputPath = BuildOutputPath(parameters, "view_contact_sheet");
                var background = parameters.TransparentBackground == true
                    ? new Color32(0, 0, 0, 0)
                    : new Color32(18, 20, 24, 255);
                var sheet = BuildViewContactSheet(cells, cellWidth, cellHeight, columns, rows, DefaultContactSheetGutter, includeLabels, background);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                    File.WriteAllBytes(outputPath, ImageConversion.EncodeToPNG(sheet));
                    var info = new FileInfo(outputPath);
                    return Response.Success($"Captured {cells.Count} Scene View frame(s) to contact sheet '{outputPath}'.", new
                    {
                        captureKind = "ContactSheet",
                        source = "SceneViewMultiView",
                        imagePath = outputPath,
                        fileUri = new Uri(outputPath).AbsoluteUri,
                        contentType = "image/png",
                        width = sheet.width,
                        height = sheet.height,
                        fileSizeBytes = info.Exists ? info.Length : 0,
                        createdUtc = DateTime.UtcNow.ToString("O"),
                        renderMode = "ContactSheet",
                        columns,
                        rows,
                        cellWidth,
                        cellHeight,
                        gutter = DefaultContactSheetGutter,
                        labels = includeLabels,
                        requestedViews = views.Select(view => view.ToString()).ToArray(),
                        requestedFramesPerView,
                        framesPerView,
                        truncatedFramesPerView = framesPerView < requestedFramesPerView,
                        intervalSeconds,
                        capturedCount = cells.Count,
                        errorCount = errors.Count,
                        maxContactSheetCells = MaxContactSheetCells,
                        blockingSeries = true,
                        project = BuildProjectInfo(),
                        activeScene = BuildSceneInfo(SceneManager.GetActiveScene()),
                        request = BuildRequestInfo(parameters),
                        captures,
                        errors
                    });
                }
                finally
                {
                    Object.DestroyImmediate(sheet);
                }
            }
            finally
            {
                foreach (var cell in cells)
                {
                    if (cell.Texture != null)
                    {
                        Object.DestroyImmediate(cell.Texture);
                    }
                }
            }
        }

        static object CaptureDiff(CaptureViewParams parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters.BaselinePath) || string.IsNullOrWhiteSpace(parameters.ComparePath))
            {
                return Response.Error("CaptureDiff requires both BaselinePath and ComparePath.");
            }

            var baselinePath = Path.GetFullPath(ExpandHomePath(parameters.BaselinePath.Trim()));
            var comparePath = Path.GetFullPath(ExpandHomePath(parameters.ComparePath.Trim()));
            if (!File.Exists(baselinePath))
            {
                return Response.Error($"Baseline image does not exist: {baselinePath}");
            }

            if (!File.Exists(comparePath))
            {
                return Response.Error($"Compare image does not exist: {comparePath}");
            }

            var baselineTexture = LoadPngTexture(baselinePath);
            var compareTexture = LoadPngTexture(comparePath);
            try
            {
                if (baselineTexture.width != compareTexture.width || baselineTexture.height != compareTexture.height)
                {
                    return Response.Error("CaptureDiff requires images with the same dimensions.", new
                    {
                        baseline = new { baselineTexture.width, baselineTexture.height },
                        compare = new { compareTexture.width, compareTexture.height }
                    });
                }

                var width = baselineTexture.width;
                var height = baselineTexture.height;
                var baselinePixels = baselineTexture.GetPixels32();
                var comparePixels = compareTexture.GetPixels32();
                var heatmapPixels = new Color32[comparePixels.Length];
                var changedPixels = 0;
                var totalDelta = 0L;
                var minX = width;
                var minY = height;
                var maxX = -1;
                var maxY = -1;

                for (var i = 0; i < comparePixels.Length; i++)
                {
                    var a = baselinePixels[i];
                    var b = comparePixels[i];
                    var delta = Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b) + Math.Abs(a.a - b.a);
                    totalDelta += delta;
                    var changed = delta >= DiffThreshold;
                    if (changed)
                    {
                        changedPixels++;
                        var x = i % width;
                        var y = i / width;
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                        heatmapPixels[i] = new Color32(255, 64, 64, 255);
                    }
                    else
                    {
                        var gray = (byte)((b.r + b.g + b.b) / 3);
                        heatmapPixels[i] = new Color32(gray, gray, gray, 255);
                    }
                }

                var diffPath = BuildOutputPath(parameters, "diff");
                var heatmapTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
                try
                {
                    heatmapTexture.SetPixels32(heatmapPixels);
                    heatmapTexture.Apply(false, false);
                    Directory.CreateDirectory(Path.GetDirectoryName(diffPath));
                    File.WriteAllBytes(diffPath, ImageConversion.EncodeToPNG(heatmapTexture));
                }
                finally
                {
                    Object.DestroyImmediate(heatmapTexture);
                }

                var totalPixels = width * height;
                return Response.Success($"Compared captures. Changed pixels: {changedPixels} of {totalPixels}.", new
                {
                    baselinePath,
                    comparePath,
                    diffPath,
                    fileUri = new Uri(diffPath).AbsoluteUri,
                    width,
                    height,
                    changedPixels,
                    totalPixels,
                    changedPercent = totalPixels == 0 ? 0d : Math.Round(changedPixels * 100d / totalPixels, 4),
                    averageDelta = totalPixels == 0 ? 0d : Math.Round(totalDelta / (double)totalPixels, 4),
                    threshold = DiffThreshold,
                    changedBounds = changedPixels == 0 ? null : new
                    {
                        minX,
                        minY,
                        maxX,
                        maxY,
                        width = maxX - minX + 1,
                        height = maxY - minY + 1
                    }
                });
            }
            finally
            {
                Object.DestroyImmediate(baselineTexture);
                Object.DestroyImmediate(compareTexture);
            }
        }

        static object ClearCaptures(CaptureViewParams parameters)
        {
            var directory = ResolveOutputDirectory(parameters.OutputDirectory);
            if (!Directory.Exists(directory))
            {
                return Response.Success("Capture directory does not exist; nothing to clear.", new
                {
                    directory,
                    deletedCount = 0
                });
            }

            var files = Directory.GetFiles(directory, "*.png", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToList();

            var deleteAll = parameters.DeleteAll == true || (!parameters.KeepLatest.HasValue && !parameters.MaxAgeDays.HasValue);
            var keepLatest = Mathf.Max(parameters.KeepLatest.GetValueOrDefault(0), 0);
            var maxAgeDays = parameters.MaxAgeDays.GetValueOrDefault(-1f);
            var cutoff = maxAgeDays >= 0f ? DateTime.UtcNow.AddDays(-maxAgeDays) : DateTime.MinValue;
            var deleted = new JArray();

            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                var shouldDelete = deleteAll ||
                    (parameters.KeepLatest.HasValue && index >= keepLatest) ||
                    (parameters.MaxAgeDays.HasValue && file.LastWriteTimeUtc < cutoff);

                if (!shouldDelete)
                {
                    continue;
                }

                try
                {
                    var fullName = file.FullName;
                    file.Delete();
                    deleted.Add(fullName);
                }
                catch (Exception ex)
                {
                    deleted.Add(new JObject
                    {
                        ["path"] = file.FullName,
                        ["error"] = ex.Message
                    });
                }
            }

            return Response.Success($"Deleted {deleted.Count} capture file(s).", new
            {
                directory,
                scannedCount = files.Count,
                deletedCount = deleted.Count,
                deleteAll,
                keepLatest = parameters.KeepLatest,
                maxAgeDays = parameters.MaxAgeDays,
                deleted
            });
        }

        static object ListCameras()
        {
            var cameras = UnityApiAdapter.FindObjectsByType(typeof(Camera), FindObjectsInactive.Include)
                .OfType<Camera>()
                .OrderBy(camera => camera.scene.IsValid() ? camera.scene.name : string.Empty)
                .ThenBy(camera => GetHierarchyPath(camera.gameObject))
                .Select(BuildCameraInfo)
                .ToArray();

            return Response.Success($"Found {cameras.Length} camera(s).", new
            {
                project = BuildProjectInfo(),
                activeScene = BuildSceneInfo(SceneManager.GetActiveScene()),
                cameras
            });
        }

        static SceneCaptureTargetResult ResolveSceneCaptureTarget(CaptureViewParams parameters)
        {
            GameObject target = null;
            Bounds? frameBounds = null;
            object stageInfo = null;
            var captureKind = parameters.Action.ToString();
            var filePrefix = ActionToFilePrefix(parameters.Action);

            switch (parameters.Action)
            {
                case CaptureViewAction.CaptureSelection:
                    target = Selection.activeGameObject;
                    if (target == null)
                    {
                        return SceneCaptureTargetResult.Fail("CaptureSelection requires an active selected GameObject.");
                    }
                    break;

                case CaptureViewAction.CaptureObject:
                case CaptureViewAction.CaptureAroundObject:
                    if (string.IsNullOrWhiteSpace(parameters.Target))
                    {
                        return SceneCaptureTargetResult.Fail($"{parameters.Action} requires Target.");
                    }

                    target = ResolveOptionalGameObject(parameters.Target, parameters.SearchMethod);
                    if (target == null)
                    {
                        return SceneCaptureTargetResult.Fail($"Target GameObject '{parameters.Target}' was not found.");
                    }
                    break;

                case CaptureViewAction.CapturePrefabStage:
                    var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                    if (prefabStage == null || prefabStage.prefabContentsRoot == null)
                    {
                        return SceneCaptureTargetResult.Fail("CapturePrefabStage requires an open Prefab Mode stage.");
                    }

                    target = prefabStage.prefabContentsRoot;
                    stageInfo = BuildPrefabStageInfo(prefabStage);
                    break;

                case CaptureViewAction.CaptureSceneOverview:
                    frameBounds = CalculateSceneBounds();
                    if (!frameBounds.HasValue)
                    {
                        return SceneCaptureTargetResult.Fail("No scene objects with bounds were found for CaptureSceneOverview.");
                    }
                    break;

                case CaptureViewAction.CaptureSceneView:
                default:
                    target = ResolveOptionalGameObject(parameters.Target, parameters.SearchMethod);
                    if (target == null && !string.IsNullOrWhiteSpace(parameters.Target))
                    {
                        return SceneCaptureTargetResult.Fail($"Target GameObject '{parameters.Target}' was not found.");
                    }
                    break;
            }

            if (target != null)
            {
                frameBounds = CalculateObjectBounds(target);
                if (parameters.Action == CaptureViewAction.CaptureAroundObject)
                {
                    var radius = Mathf.Max(parameters.NearbyRadius.GetValueOrDefault(DefaultNearbyRadius), 0.25f);
                    var aroundBounds = frameBounds.Value;
                    aroundBounds.Expand(Vector3.one * radius);
                    frameBounds = aroundBounds;
                }
            }

            return SceneCaptureTargetResult.Ok(target, frameBounds, captureKind, filePrefix, stageInfo);
        }

        static SceneViewFrame ApplySceneViewFraming(SceneView sceneView, GameObject target, Bounds? frameBounds, CaptureViewParams parameters)
        {
            if (parameters.Orthographic.HasValue)
            {
                sceneView.orthographic = parameters.Orthographic.Value;
            }

            if (target == null && !frameBounds.HasValue)
            {
                if (parameters.View != CaptureViewDirection.Current)
                {
                    SetSceneViewRotation(sceneView, parameters.View);
                }

                return null;
            }

            var bounds = AddBoundsPadding(frameBounds ?? CalculateObjectBounds(target), 0.12f, 0.1f);
            if (target != null)
            {
                Selection.activeGameObject = target;
            }

            if (parameters.View != CaptureViewDirection.Current)
            {
                SetSceneViewRotation(sceneView, parameters.View);
                sceneView.orthographic = parameters.Orthographic ?? true;
            }

            var size = ComputeOrthographicSize(bounds) * GetEffectiveZoomScale(parameters);
            ApplySceneViewCamera(sceneView, bounds.center, sceneView.rotation, size, sceneView.orthographic);
            return new SceneViewFrame(target, bounds);
        }

        static void SetSceneViewRotation(SceneView sceneView, CaptureViewDirection direction)
        {
            if (sceneView.in2DMode)
            {
                sceneView.in2DMode = false;
            }

            sceneView.rotation = DirectionToRotation(direction);
        }

        static void ApplySceneViewCamera(SceneView sceneView, Vector3 pivot, Quaternion rotation, float size, bool orthographic)
        {
            sceneView.LookAt(pivot, rotation, size, orthographic, true);
            sceneView.rotation = rotation;
            sceneView.pivot = pivot;
            sceneView.size = size;
            sceneView.orthographic = orthographic;
            sceneView.Repaint();
        }

        static TempCameraHandle CreateFramedRenderCamera(
            Camera template,
            Quaternion rotation,
            bool orthographic,
            Bounds bounds,
            CaptureViewParams parameters,
            int width,
            int height)
        {
            var cameraObject = EditorUtility.CreateGameObjectWithHideFlags(
                "UniBridge Framed Capture Camera",
                HideFlags.HideAndDontSave,
                typeof(Camera));
            var camera = cameraObject.GetComponent<Camera>();
            CopyCameraSettings(template, camera);

            var aspect = height <= 0 ? 1f : Mathf.Max(0.01f, width / (float)height);
            var radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
            var forward = rotation * Vector3.forward;

            camera.transform.rotation = rotation;
            camera.orthographic = orthographic;
            camera.aspect = aspect;

            if (orthographic)
            {
                var size = ComputeOrthographicSize(bounds, rotation, aspect) * GetEffectiveZoomScale(parameters);
                var distance = Mathf.Max(radius * 3f, size * 2f, 10f);
                camera.orthographicSize = size;
                camera.transform.position = bounds.center - forward * distance;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = Mathf.Max(distance + radius * 6f, 1000f);
            }
            else
            {
                var halfFov = Mathf.Max(camera.fieldOfView, 1f) * 0.5f * Mathf.Deg2Rad;
                var distance = Mathf.Max(radius / Mathf.Sin(halfFov), 1f) * GetEffectiveZoomScale(parameters);
                camera.transform.position = bounds.center - forward * (distance + radius);
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = Mathf.Max(distance + radius * 8f, 1000f);
            }

            camera.enabled = false;
            camera.targetTexture = null;
            return new TempCameraHandle(camera, bounds);
        }

        static void CopyCameraSettings(Camera source, Camera destination)
        {
            if (source != null)
            {
                destination.CopyFrom(source);
            }

            destination.enabled = false;
            destination.targetTexture = null;
            destination.depthTextureMode = source == null ? DepthTextureMode.None : source.depthTextureMode;
        }

        static Camera ResolveCamera(CaptureViewParams parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameters.Camera))
            {
                if (long.TryParse(parameters.Camera.Trim(), out var id))
                {
                    var objectById = UnityApiAdapter.GetObjectFromId(id);
                    if (objectById is Camera cameraById)
                    {
                        return cameraById;
                    }

                    if (objectById is Component componentById)
                    {
                        var cameraFromComponent = componentById.GetComponent<Camera>();
                        if (cameraFromComponent != null)
                        {
                            return cameraFromComponent;
                        }
                    }

                    if (objectById is GameObject gameObjectById)
                    {
                        var cameraFromGameObject = gameObjectById.GetComponent<Camera>();
                        if (cameraFromGameObject != null)
                        {
                            return cameraFromGameObject;
                        }
                    }
                }

                var cameraObject = ResolveOptionalGameObject(parameters.Camera, parameters.SearchMethod);
                if (cameraObject != null)
                {
                    var resolvedCamera = cameraObject.GetComponent<Camera>();
                    if (resolvedCamera != null)
                    {
                        return resolvedCamera;
                    }
                }

                var namedCamera = UnityApiAdapter.FindObjectsByType(typeof(Camera), FindObjectsInactive.Include)
                    .OfType<Camera>()
                    .FirstOrDefault(camera => camera.name.Equals(parameters.Camera, StringComparison.OrdinalIgnoreCase));
                if (namedCamera != null)
                {
                    return namedCamera;
                }
            }

            if (Camera.main != null)
            {
                return Camera.main;
            }

            return UnityApiAdapter.FindObjectsByType(typeof(Camera), FindObjectsInactive.Include)
                .OfType<Camera>()
                .OrderByDescending(camera => camera.isActiveAndEnabled)
                .ThenByDescending(camera => camera.tag == "MainCamera")
                .FirstOrDefault();
        }

        static GameObject ResolveOptionalGameObject(string value, string searchMethod)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return ObjectsHelper.FindObject(
                new JValue(value.Trim()),
                string.IsNullOrWhiteSpace(searchMethod) ? "by_id_or_name_or_path" : searchMethod.Trim().ToLowerInvariant(),
                new JObject { ["search_inactive"] = true });
        }

        static IEnumerable<GameObject> ResolveAdvanceTargets(CaptureViewParams parameters, GameObject target, Camera camera)
        {
            if (parameters.AdvanceMs.GetValueOrDefault(0) <= 0)
                return Array.Empty<GameObject>();

            if (target != null)
                return new[] { target };

            if (camera != null && camera.scene.IsValid())
                return camera.scene.GetRootGameObjects();

            var activeScene = SceneManager.GetActiveScene();
            return activeScene.IsValid() ? activeScene.GetRootGameObjects() : Array.Empty<GameObject>();
        }

        static TempScreenshotPath BuildTempGameViewScreenshotPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            var relativePath = $"Temp/UniBridgeCapture/GameView_{Guid.NewGuid():N}.png";
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return new TempScreenshotPath(relativePath, fullPath);
        }

        static EditorWindow FocusGameViewWindow()
        {
            try
            {
                var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType == null)
                {
                    return null;
                }

                var window = Resources.FindObjectsOfTypeAll(gameViewType)
                    .OfType<EditorWindow>()
                    .FirstOrDefault();
                window ??= EditorWindow.GetWindow(gameViewType);
                window?.Focus();
                window?.Repaint();
                return window;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CaptureView] Unable to focus Game View before screenshot: {ex.Message}");
                return null;
            }
        }

        static void RepaintEditorViews(EditorWindow extraWindow = null)
        {
            SceneView.RepaintAll();
            extraWindow?.Repaint();
            InternalEditorUtility.RepaintAllViews();
            EditorApplication.QueuePlayerLoopUpdate();
        }

        static async Task WaitForEditorUpdateAsync(int minimumDelayMs)
        {
            minimumDelayMs = Mathf.Clamp(minimumDelayMs, 0, 5000);
            var dueTime = EditorApplication.timeSinceStartup + minimumDelayMs / 1000.0;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Tick()
            {
                if (EditorApplication.timeSinceStartup < dueTime)
                {
                    return;
                }

                EditorApplication.update -= Tick;
                tcs.TrySetResult(true);
            }

            EditorApplication.update += Tick;
            RepaintEditorViews();
            await tcs.Task;
        }

        static async Task<ScreenshotCaptureResult> WaitForScreenshotAsync(string fullPath, int? requestedTimeoutMs)
        {
            var timeoutMs = Mathf.Clamp(requestedTimeoutMs.GetValueOrDefault(DefaultScreenshotTimeoutMs), 250, MaxScreenshotTimeoutMs);
            var startedAtUtc = DateTime.UtcNow;
            var loadAttempts = 0;

            while ((DateTime.UtcNow - startedAtUtc).TotalMilliseconds <= timeoutMs)
            {
                await WaitForEditorUpdateAsync(16);

                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var info = new FileInfo(fullPath);
                if (info.Length <= 0)
                {
                    continue;
                }

                loadAttempts++;
                Texture2D texture = null;
                try
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(texture, File.ReadAllBytes(fullPath)))
                    {
                        var elapsedMs = (int)(DateTime.UtcNow - startedAtUtc).TotalMilliseconds;
                        return new ScreenshotCaptureResult(texture, elapsedMs, loadAttempts, info.Length);
                    }
                }
                catch (IOException)
                {
                    // Unity may still be finishing the file write; try again on the next editor update.
                }
                catch (UnauthorizedAccessException)
                {
                    // Treat transient file locks the same way as partial writes.
                }

                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }
            }

            throw new TimeoutException($"Timed out after {timeoutMs}ms waiting for Unity to write Game View screenshot '{fullPath}'.");
        }

        static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CaptureView] Unable to delete temporary screenshot '{path}': {ex.Message}");
            }
        }

        static RenderInfo RenderCameraToPng(Camera camera, string outputPath, int width, int height, bool transparentBackground, VisualContext visualContext, CaptureViewParams parameters)
        {
            var previousTargetTexture = camera.targetTexture;
            var previousActiveTexture = RenderTexture.active;
            var renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var textureFormat = parameters.ReadbackMode == CaptureReadbackMode.GpuReadback || transparentBackground
                ? TextureFormat.RGBA32
                : TextureFormat.RGB24;
            var texture = new Texture2D(width, height, textureFormat, false);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var readbackMode = ReadRenderTexture(renderTexture, texture, width, height, parameters.ReadbackMode);
                string overlayPath = null;
                string compositePath = null;

                if (visualContext != null && parameters.SeparateOverlay == true)
                {
                    File.WriteAllBytes(outputPath, ImageConversion.EncodeToPNG(texture));
                    overlayPath = BuildRelatedPngPath(outputPath, "overlay");
                    WriteOverlayLayer(texture.width, texture.height, overlayPath, camera, visualContext);

                    if (parameters.CompositeOverlay != false)
                    {
                        compositePath = BuildRelatedPngPath(outputPath, "composite");
                        DrawOverlay(texture, camera, visualContext);
                        texture.Apply(false, false);
                        File.WriteAllBytes(compositePath, ImageConversion.EncodeToPNG(texture));
                    }

                    return new RenderInfo(width, height, overlayPath, compositePath, readbackMode);
                }

                if (visualContext != null)
                {
                    DrawOverlay(texture, camera, visualContext);
                    texture.Apply(false, false);
                }

                File.WriteAllBytes(outputPath, ImageConversion.EncodeToPNG(texture));
                return new RenderInfo(width, height, overlayPath, compositePath, readbackMode);
            }
            finally
            {
                camera.targetTexture = previousTargetTexture;
                RenderTexture.active = previousActiveTexture;
                RenderTexture.ReleaseTemporary(renderTexture);
                Object.DestroyImmediate(texture);
            }
        }

        static string ReadRenderTexture(RenderTexture renderTexture, Texture2D texture, int width, int height, CaptureReadbackMode mode)
        {
            if (mode == CaptureReadbackMode.GpuReadback && SystemInfo.supportsAsyncGPUReadback)
            {
                try
                {
                    var request = AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGBA32);
                    request.WaitForCompletion();
                    if (!request.hasError)
                    {
                        texture.LoadRawTextureData(request.GetData<byte>());
                        texture.Apply(false, false);
                        return "GpuReadback";
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CaptureView] GPU readback failed, falling back to ReadPixels: {ex.Message}");
                }
            }

            texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            texture.Apply(false, false);
            return mode == CaptureReadbackMode.GpuReadback ? "ReadPixelsFallback" : "Immediate";
        }

        static VisualContext BuildVisualContext(Camera camera, GameObject target, Bounds? focusBounds, CaptureViewParams parameters)
        {
            var maxObjects = Mathf.Clamp(parameters.MaxObjects.GetValueOrDefault(DefaultMaxObjects), 1, MaxMetadataObjects);
            var includeNearby = parameters.IncludeNearbyObjects ?? target != null;
            var nearbyRadius = Mathf.Max(parameters.NearbyRadius.GetValueOrDefault(DefaultNearbyRadius), 0.25f);
            var focusCenter = focusBounds?.center ?? target?.transform.position ?? Vector3.zero;
            var allObjects = GetSceneGameObjects(includeInactive: false);
            var visible = new List<VisualObjectInfo>();
            var nearby = new List<VisualObjectInfo>();
            var targetId = target == null ? 0 : UnityApiAdapter.GetObjectId(target);

            foreach (var gameObject in allObjects)
            {
                if (gameObject == null || gameObject == camera.gameObject)
                {
                    continue;
                }

                var bounds = CalculateObjectBounds(gameObject);
                if (!TryProjectBounds(camera, bounds, out var viewportRect))
                {
                    if (includeNearby && target != null && IsNearby(bounds, focusCenter, nearbyRadius))
                    {
                        nearby.Add(BuildVisualObjectInfo(gameObject, bounds, Rect.zero, focusCenter, UnityApiAdapter.ObjectIdEquals(gameObject, targetId), false));
                    }

                    continue;
                }

                var intersectsViewport = viewportRect.xMax >= 0f && viewportRect.xMin <= 1f && viewportRect.yMax >= 0f && viewportRect.yMin <= 1f;
                var isTarget = UnityApiAdapter.ObjectIdEquals(gameObject, targetId);
                if (intersectsViewport)
                {
                    visible.Add(BuildVisualObjectInfo(gameObject, bounds, viewportRect, focusCenter, isTarget, true));
                }

                if (includeNearby && target != null && (isTarget || IsNearby(bounds, focusCenter, nearbyRadius)))
                {
                    nearby.Add(BuildVisualObjectInfo(gameObject, bounds, viewportRect, focusCenter, isTarget, intersectsViewport));
                }
            }

            visible = visible
                .OrderByDescending(item => item.IsTarget)
                .ThenBy(item => item.DistanceToFocus)
                .Take(maxObjects)
                .ToList();

            nearby = nearby
                .GroupBy(item => item.ObjectId)
                .Select(group => group.OrderByDescending(item => item.IsVisible).First())
                .OrderByDescending(item => item.IsTarget)
                .ThenBy(item => item.DistanceToFocus)
                .Take(maxObjects)
                .ToList();

            var overlayItems = new List<VisualObjectInfo>();
            if (target != null)
            {
                overlayItems.AddRange(nearby.Where(item => item.IsVisible && item.IsTarget));
                overlayItems.AddRange(nearby.Where(item => item.IsVisible && !item.IsTarget).Take(Math.Min(maxObjects, 5)));
            }
            else
            {
                overlayItems.AddRange(visible.Take(Math.Min(maxObjects, 8)));
            }

            for (var index = 0; index < overlayItems.Count; index++)
            {
                overlayItems[index].Marker = index + 1;
            }

            return new VisualContext(visible, nearby, overlayItems);
        }

        static List<GameObject> GetSceneGameObjects(bool includeInactive)
        {
            return UnityApiAdapter.FindObjectsByType(typeof(Transform), includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude)
                .OfType<Transform>()
                .Select(transform => transform.gameObject)
                .Where(gameObject => gameObject != null)
                .Where(gameObject => gameObject.scene.IsValid())
                .Where(gameObject => (gameObject.hideFlags & HideFlags.HideInHierarchy) == 0)
                .Distinct()
                .ToList();
        }

        static bool IsNearby(Bounds bounds, Vector3 focusCenter, float radius)
        {
            return Vector3.Distance(bounds.ClosestPoint(focusCenter), focusCenter) <= radius;
        }

        static VisualObjectInfo BuildVisualObjectInfo(GameObject gameObject, Bounds bounds, Rect viewportRect, Vector3 focusCenter, bool isTarget, bool isVisible)
        {
            var metadata = new
            {
                name = gameObject.name,
                objectId = UnityApiAdapter.GetObjectId(gameObject),
                hierarchyPath = GetHierarchyPath(gameObject),
                scene = BuildSceneInfo(gameObject.scene),
                isTarget,
                isVisible,
                distanceToFocus = Vector3.Distance(bounds.center, focusCenter),
                bounds = BuildBoundsInfo(bounds),
                viewportRect = isVisible ? BuildRectInfo(viewportRect) : null,
                components = gameObject.GetComponents<Component>()
                    .Where(component => component != null)
                    .Select(component => component.GetType().Name)
                    .ToArray()
            };

            return new VisualObjectInfo(
                gameObject.name,
                UnityApiAdapter.GetObjectId(gameObject),
                isTarget,
                isVisible,
                Vector3.Distance(bounds.center, focusCenter),
                viewportRect,
                metadata);
        }

        static object BuildRectInfo(Rect rect)
        {
            return new
            {
                xMin = rect.xMin,
                yMin = rect.yMin,
                xMax = rect.xMax,
                yMax = rect.yMax,
                width = rect.width,
                height = rect.height
            };
        }

        static object BuildAnnotationInfo(VisualObjectInfo item)
        {
            return new
            {
                marker = item.Marker,
                label = item.Label,
                objectId = item.ObjectId,
                isTarget = item.IsTarget,
                isVisible = item.IsVisible,
                viewportRect = item.IsVisible ? BuildRectInfo(item.ViewportRect) : null,
                metadata = item.Metadata
            };
        }

        static bool TryProjectBounds(Camera camera, Bounds bounds, out Rect viewportRect)
        {
            var corners = GetBoundsCorners(bounds);
            var minX = float.PositiveInfinity;
            var minY = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var maxY = float.NegativeInfinity;
            var anyInFront = false;

            foreach (var corner in corners)
            {
                var point = camera.WorldToViewportPoint(corner);
                if (point.z <= 0f)
                {
                    continue;
                }

                anyInFront = true;
                minX = Mathf.Min(minX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxX = Mathf.Max(maxX, point.x);
                maxY = Mathf.Max(maxY, point.y);
            }

            if (!anyInFront)
            {
                viewportRect = Rect.zero;
                return false;
            }

            viewportRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        static Vector3[] GetBoundsCorners(Bounds bounds)
        {
            var min = bounds.min;
            var max = bounds.max;
            return new[]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, max.y, max.z)
            };
        }

        static void WriteOverlayLayer(int width, int height, string outputPath, Camera camera, VisualContext visualContext)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var clear = new Color32(0, 0, 0, 0);
                var pixels = Enumerable.Repeat(clear, width * height).ToArray();
                texture.SetPixels32(pixels);
                DrawOverlay(texture, camera, visualContext);
                texture.Apply(false, false);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.WriteAllBytes(outputPath, ImageConversion.EncodeToPNG(texture));
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        static string BuildRelatedPngPath(string outputPath, string suffix)
        {
            var directory = Path.GetDirectoryName(outputPath);
            var fileName = Path.GetFileNameWithoutExtension(outputPath);
            return Path.Combine(directory, $"{fileName}_{suffix}.png");
        }

        static void DrawOverlay(Texture2D texture, Camera camera, VisualContext visualContext)
        {
            var visibleItems = visualContext.OverlayItems
                .Where(item => item.IsVisible)
                .ToList();
            var objectRects = visibleItems
                .Select(item => ViewportToPixelRect(item.ViewportRect, texture.width, texture.height))
                .ToList();
            var occupiedMarkers = new List<Rect>();

            for (var index = 0; index < visibleItems.Count; index++)
            {
                var item = visibleItems[index];
                var pixelRect = objectRects[index];
                var color = GetOverlayColor(item);
                DrawRect(texture, pixelRect, color, item.IsTarget ? 3 : 2);
                var marker = item.Marker > 0 ? item.Marker.ToString() : (index + 1).ToString();
                DrawMarker(texture, pixelRect, marker, color, occupiedMarkers, objectRects);
            }

            DrawOverlayLegend(texture, visibleItems, objectRects);
        }

        static Color32 GetOverlayColor(VisualObjectInfo item)
        {
            if (item.IsTarget)
            {
                return new Color32(255, 220, 48, 255);
            }

            var marker = Mathf.Max(1, item.Marker);
            return OverlayPalette[(marker - 1) % OverlayPalette.Length];
        }

        static void DrawOverlayLegend(Texture2D texture, IReadOnlyList<VisualObjectInfo> visibleItems, IReadOnlyList<Rect> objectRects)
        {
            if (visibleItems.Count == 0)
            {
                return;
            }

            const int scale = 2;
            const int padding = 6;
            const int lineHeight = 20;
            const int swatchSize = 8;
            const int swatchGap = 5;
            var charWidth = 6 * scale;
            var maxLines = Mathf.Max(1, (texture.height - padding * 2 - 4) / lineHeight);
            var lines = new List<OverlayLegendLine>();
            var maxTextChars = Mathf.Clamp((int)((texture.width * 0.42f - padding * 2 - swatchSize - swatchGap) / charWidth), 12, 42);
            var visibleLineBudget = Math.Min(visibleItems.Count, maxLines);

            if (visibleItems.Count > maxLines)
            {
                visibleLineBudget = Math.Max(1, maxLines - 1);
            }

            foreach (var item in visibleItems.Take(visibleLineBudget))
            {
                var marker = item.Marker > 0 ? item.Marker.ToString() : "?";
                var prefix = item.IsTarget ? $"{marker} TARGET " : $"{marker} ";
                var label = TruncateLabel(item.Label, Math.Max(1, maxTextChars - prefix.Length));
                lines.Add(new OverlayLegendLine(prefix + label, GetOverlayColor(item)));
            }

            if (visibleItems.Count > visibleLineBudget)
            {
                lines.Add(new OverlayLegendLine($"+{visibleItems.Count - visibleLineBudget} MORE IN METADATA", new Color32(210, 210, 210, 255)));
            }

            var widestText = lines.Max(line => Math.Min(line.Text.Length, maxTextChars) * charWidth);
            var maxPanelWidth = Mathf.Max(1, texture.width - padding * 2);
            var maxPanelHeight = Mathf.Max(1, texture.height - padding * 2);
            var minPanelWidth = Mathf.Min(80, maxPanelWidth);
            var minPanelHeight = Mathf.Min(lineHeight + padding * 2, maxPanelHeight);
            var panelWidth = Mathf.Clamp(widestText + padding * 2 + swatchSize + swatchGap, minPanelWidth, maxPanelWidth);
            var panelHeight = Mathf.Clamp(lines.Count * lineHeight + padding * 2, minPanelHeight, maxPanelHeight);
            var panelRect = ChooseLegendRect(texture.width, texture.height, panelWidth, panelHeight, objectRects);
            FillRect(
                texture,
                Mathf.RoundToInt(panelRect.xMin),
                Mathf.RoundToInt(panelRect.yMin),
                Mathf.RoundToInt(panelRect.width),
                Mathf.RoundToInt(panelRect.height),
                new Color32(0, 0, 0, 176));
            DrawRect(texture, panelRect, new Color32(235, 235, 235, 190), 1);

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                var y = Mathf.RoundToInt(panelRect.yMax - padding - (index + 1) * lineHeight + 3);
                var swatchX = Mathf.RoundToInt(panelRect.xMin + padding);
                var swatchY = y + 3;
                FillRect(texture, swatchX, swatchY, swatchSize, swatchSize, line.Color);
                DrawLabelText(texture, swatchX + swatchSize + swatchGap, y, TruncateLabel(line.Text, maxTextChars), line.Color, scale);
            }
        }

        static Rect ChooseLegendRect(int textureWidth, int textureHeight, int panelWidth, int panelHeight, IReadOnlyList<Rect> objectRects)
        {
            const int margin = 8;
            var candidates = new[]
            {
                new Rect(margin, textureHeight - panelHeight - margin, panelWidth, panelHeight),
                new Rect(textureWidth - panelWidth - margin, textureHeight - panelHeight - margin, panelWidth, panelHeight),
                new Rect(margin, margin, panelWidth, panelHeight),
                new Rect(textureWidth - panelWidth - margin, margin, panelWidth, panelHeight)
            };

            var bestRect = candidates[0];
            var bestOverlap = float.PositiveInfinity;
            foreach (var candidate in candidates)
            {
                var overlap = 0f;
                foreach (var objectRect in objectRects)
                {
                    overlap += CalculateOverlapArea(candidate, objectRect);
                }

                if (overlap < bestOverlap)
                {
                    bestOverlap = overlap;
                    bestRect = candidate;
                }
            }

            return bestRect;
        }

        static float CalculateOverlapArea(Rect a, Rect b)
        {
            var xOverlap = Mathf.Max(0f, Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin));
            var yOverlap = Mathf.Max(0f, Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin));
            return xOverlap * yOverlap;
        }

        static string TruncateLabel(string label, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return "?";
            }

            if (maxLength <= 0 || label.Length <= maxLength)
            {
                return label;
            }

            return maxLength <= 3 ? label.Substring(0, maxLength) : label.Substring(0, maxLength - 3) + "...";
        }

        static Rect ViewportToPixelRect(Rect viewportRect, int width, int height)
        {
            var xMin = Mathf.Clamp(viewportRect.xMin * width, 0, width - 1);
            var xMax = Mathf.Clamp(viewportRect.xMax * width, 0, width - 1);
            var yMin = Mathf.Clamp(viewportRect.yMin * height, 0, height - 1);
            var yMax = Mathf.Clamp(viewportRect.yMax * height, 0, height - 1);
            return Rect.MinMaxRect(xMin, yMin, Mathf.Max(xMin + 1, xMax), Mathf.Max(yMin + 1, yMax));
        }

        static void DrawRect(Texture2D texture, Rect rect, Color32 color, int thickness)
        {
            var xMin = Mathf.Clamp(Mathf.RoundToInt(rect.xMin), 0, texture.width - 1);
            var xMax = Mathf.Clamp(Mathf.RoundToInt(rect.xMax), 0, texture.width - 1);
            var yMin = Mathf.Clamp(Mathf.RoundToInt(rect.yMin), 0, texture.height - 1);
            var yMax = Mathf.Clamp(Mathf.RoundToInt(rect.yMax), 0, texture.height - 1);

            for (var offset = 0; offset < thickness; offset++)
            {
                DrawHorizontalLine(texture, xMin, xMax, yMin + offset, color);
                DrawHorizontalLine(texture, xMin, xMax, yMax - offset, color);
                DrawVerticalLine(texture, xMin + offset, yMin, yMax, color);
                DrawVerticalLine(texture, xMax - offset, yMin, yMax, color);
            }
        }

        static bool TryPlaceLabel(int textureWidth, int textureHeight, Rect anchorRect, string label, List<Rect> occupiedLabels, out Rect labelRect)
        {
            var size = MeasureLabel(label);
            var margin = 4f;
            var candidates = new[]
            {
                new Vector2(anchorRect.xMin, anchorRect.yMax + margin),
                new Vector2(anchorRect.xMin, anchorRect.yMin - size.y - margin),
                new Vector2(anchorRect.xMax + margin, anchorRect.yMin),
                new Vector2(anchorRect.xMin - size.x - margin, anchorRect.yMin),
                new Vector2(anchorRect.xMax + margin, anchorRect.yMax + margin),
                new Vector2(anchorRect.xMin - size.x - margin, anchorRect.yMax + margin),
                new Vector2(anchorRect.xMax + margin, anchorRect.yMin - size.y - margin),
                new Vector2(anchorRect.xMin - size.x - margin, anchorRect.yMin - size.y - margin)
            };

            foreach (var candidate in candidates)
            {
                var clampedX = Mathf.Clamp(candidate.x, 0, Mathf.Max(0, textureWidth - size.x - 1));
                var clampedY = Mathf.Clamp(candidate.y, 0, Mathf.Max(0, textureHeight - size.y - 1));
                var rect = new Rect(clampedX, clampedY, size.x, size.y);
                if (RectIntersectsAny(rect, occupiedLabels) || rect.Overlaps(anchorRect))
                {
                    continue;
                }

                labelRect = rect;
                return true;
            }

            labelRect = default;
            return false;
        }

        static Vector2 MeasureLabel(string label)
        {
            var safeLength = string.IsNullOrWhiteSpace(label) ? 0 : Math.Min(label.Length, 28);
            var scale = 2;
            var charWidth = 6 * scale;
            var labelWidth = safeLength * charWidth + 4;
            var labelHeight = 9 * scale;
            return new Vector2(labelWidth, labelHeight);
        }

        static bool RectIntersectsAny(Rect rect, IReadOnlyList<Rect> occupiedLabels)
        {
            foreach (var occupied in occupiedLabels)
            {
                if (rect.Overlaps(occupied))
                {
                    return true;
                }
            }

            return false;
        }

        static Rect ExpandRect(Rect rect, float padding)
        {
            return Rect.MinMaxRect(
                rect.xMin - padding,
                rect.yMin - padding,
                rect.xMax + padding,
                rect.yMax + padding);
        }

        static void DrawMarker(Texture2D texture, Rect anchorRect, string marker, Color32 color, List<Rect> occupiedMarkers, IReadOnlyList<Rect> objectRects)
        {
            marker = string.IsNullOrWhiteSpace(marker) ? "?" : marker;
            var scale = 2;
            const float gap = 8f;
            var markerWidth = marker.Length * 6 * scale + 4;
            var markerHeight = 9 * scale;
            var candidates = new[]
            {
                new Vector2(anchorRect.xMin - markerWidth - gap, anchorRect.yMax + gap),
                new Vector2(anchorRect.xMin, anchorRect.yMax + gap),
                new Vector2(anchorRect.xMax + gap, anchorRect.yMax - markerHeight),
                new Vector2(anchorRect.xMin - markerWidth - gap, anchorRect.yMin - markerHeight - gap),
                new Vector2(anchorRect.xMax + gap, anchorRect.yMin),
                new Vector2(anchorRect.xMin, anchorRect.yMin - markerHeight - gap),
                new Vector2(anchorRect.xMax + gap, anchorRect.yMax + gap),
                new Vector2(anchorRect.xMin - markerWidth - gap, anchorRect.yMax - markerHeight)
            };

            var markerRect = Rect.zero;
            foreach (var candidate in candidates)
            {
                var clampedX = Mathf.Clamp(candidate.x, 0, Mathf.Max(0, texture.width - markerWidth - 1));
                var clampedY = Mathf.Clamp(candidate.y, 0, Mathf.Max(0, texture.height - markerHeight - 1));
                var rect = new Rect(clampedX, clampedY, markerWidth, markerHeight);
                if (RectIntersectsAny(rect, occupiedMarkers) || RectIntersectsAny(ExpandRect(rect, 2f), objectRects))
                {
                    continue;
                }

                markerRect = rect;
                break;
            }

            if (markerRect == Rect.zero)
            {
                markerRect = new Rect(
                    Mathf.Clamp(Mathf.RoundToInt(anchorRect.xMin - markerWidth - gap), 0, Mathf.Max(0, texture.width - markerWidth - 1)),
                    Mathf.Clamp(Mathf.RoundToInt(anchorRect.yMax + gap), 0, Mathf.Max(0, texture.height - markerHeight - 1)),
                    markerWidth,
                    markerHeight);
            }

            occupiedMarkers.Add(markerRect);
            var x = Mathf.RoundToInt(markerRect.xMin);
            var y = Mathf.RoundToInt(markerRect.yMin);
            FillRect(texture, x, y, markerWidth, markerHeight, new Color32(0, 0, 0, 210));
            DrawLabelText(texture, x + 2, y + 2, marker, color, scale);
        }

        static void DrawHorizontalLine(Texture2D texture, int xMin, int xMax, int y, Color32 color)
        {
            if (y < 0 || y >= texture.height)
            {
                return;
            }

            for (var x = Mathf.Max(0, xMin); x <= Mathf.Min(texture.width - 1, xMax); x++)
            {
                texture.SetPixel(x, y, color);
            }
        }

        static void DrawVerticalLine(Texture2D texture, int x, int yMin, int yMax, Color32 color)
        {
            if (x < 0 || x >= texture.width)
            {
                return;
            }

            for (var y = Mathf.Max(0, yMin); y <= Mathf.Min(texture.height - 1, yMax); y++)
            {
                texture.SetPixel(x, y, color);
            }
        }

        static void DrawLabel(Texture2D texture, int x, int y, string label, Color32 color)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            label = label.Length > 28 ? label.Substring(0, 28) : label;
            var scale = 2;
            var charWidth = 6 * scale;
            var labelWidth = label.Length * charWidth + 4;
            var labelHeight = 9 * scale;
            x = Mathf.Clamp(x, 0, Mathf.Max(0, texture.width - labelWidth - 1));
            y = Mathf.Clamp(y, 0, Mathf.Max(0, texture.height - labelHeight - 1));
            FillRect(texture, x, y, labelWidth, labelHeight, new Color32(0, 0, 0, 185));
            DrawLabelText(texture, x + 2, y + 2, label, color, scale);
        }

        static void DrawLabelText(Texture2D texture, int x, int y, string label, Color32 color, int scale)
        {
            for (var index = 0; index < label.Length; index++)
            {
                DrawGlyph(texture, x + index * 6 * scale, y, label[index], color, scale);
            }
        }

        static void FillRect(Texture2D texture, int x, int y, int width, int height, Color32 color)
        {
            for (var yy = Mathf.Max(0, y); yy < Mathf.Min(texture.height, y + height); yy++)
            {
                for (var xx = Mathf.Max(0, x); xx < Mathf.Min(texture.width, x + width); xx++)
                {
                    BlendPixel(texture, xx, yy, color);
                }
            }
        }

        static void BlendPixel(Texture2D texture, int x, int y, Color32 source)
        {
            if (source.a >= 250)
            {
                texture.SetPixel(x, y, source);
                return;
            }

            var destination = texture.GetPixel(x, y);
            var alpha = source.a / 255f;
            var blended = new Color(
                Mathf.Lerp(destination.r, source.r / 255f, alpha),
                Mathf.Lerp(destination.g, source.g / 255f, alpha),
                Mathf.Lerp(destination.b, source.b / 255f, alpha),
                Mathf.Clamp01(destination.a + alpha * (1f - destination.a)));
            texture.SetPixel(x, y, blended);
        }

        static void DrawGlyph(Texture2D texture, int x, int y, char character, Color32 color, int scale)
        {
            var rows = GetGlyphRows(character);
            for (var row = 0; row < rows.Length; row++)
            {
                for (var col = 0; col < 5; col++)
                {
                    if ((rows[row] & (1 << (4 - col))) == 0)
                    {
                        continue;
                    }

                    FillRect(texture, x + col * scale, y + (rows.Length - 1 - row) * scale, scale, scale, color);
                }
            }
        }

        static byte[] GetGlyphRows(char character)
        {
            switch (char.ToUpperInvariant(character))
            {
                case 'A': return new byte[] { 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 };
                case 'B': return new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E };
                case 'C': return new byte[] { 0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E };
                case 'D': return new byte[] { 0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E };
                case 'E': return new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F };
                case 'F': return new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10 };
                case 'G': return new byte[] { 0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F };
                case 'H': return new byte[] { 0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 };
                case 'I': return new byte[] { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x1F };
                case 'J': return new byte[] { 0x01, 0x01, 0x01, 0x01, 0x11, 0x11, 0x0E };
                case 'K': return new byte[] { 0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11 };
                case 'L': return new byte[] { 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F };
                case 'M': return new byte[] { 0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11 };
                case 'N': return new byte[] { 0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11 };
                case 'O': return new byte[] { 0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E };
                case 'P': return new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10 };
                case 'Q': return new byte[] { 0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D };
                case 'R': return new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11 };
                case 'S': return new byte[] { 0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E };
                case 'T': return new byte[] { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04 };
                case 'U': return new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E };
                case 'V': return new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04 };
                case 'W': return new byte[] { 0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A };
                case 'X': return new byte[] { 0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11 };
                case 'Y': return new byte[] { 0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04 };
                case 'Z': return new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F };
                case '0': return new byte[] { 0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E };
                case '1': return new byte[] { 0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E };
                case '2': return new byte[] { 0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F };
                case '3': return new byte[] { 0x1E, 0x01, 0x01, 0x0E, 0x01, 0x01, 0x1E };
                case '4': return new byte[] { 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02 };
                case '5': return new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x01, 0x01, 0x1E };
                case '6': return new byte[] { 0x0E, 0x10, 0x10, 0x1E, 0x11, 0x11, 0x0E };
                case '7': return new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08 };
                case '8': return new byte[] { 0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E };
                case '9': return new byte[] { 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x01, 0x0E };
                case '_': return new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F };
                case '-': return new byte[] { 0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00 };
                case '.': return new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C };
                case '/': return new byte[] { 0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10 };
                case ':': return new byte[] { 0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00 };
                case ' ': return new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                default: return new byte[] { 0x1F, 0x11, 0x01, 0x02, 0x04, 0x00, 0x04 };
            }
        }

        static JObject BuildCaptureData(
            string outputPath,
            int width,
            int height,
            string captureKind,
            string source,
            CaptureViewParams parameters,
            GameObject target,
            Camera camera,
            SceneViewFrame frame,
            VisualContext visualContext,
            RenderInfo renderInfo)
        {
            var info = new FileInfo(outputPath);
            var data = JObject.FromObject(new
            {
                captureKind,
                source,
                imagePath = outputPath,
                fileUri = new Uri(outputPath).AbsoluteUri,
                contentType = "image/png",
                width,
                height,
                fileSizeBytes = info.Exists ? info.Length : 0,
                createdUtc = DateTime.UtcNow.ToString("O"),
                tag = parameters.Tag,
                project = BuildProjectInfo(),
                activeScene = BuildSceneInfo(SceneManager.GetActiveScene()),
                camera = camera == null ? null : BuildCameraInfo(camera),
                target = target == null ? null : BuildGameObjectInfo(target),
                transparentBackground = parameters.TransparentBackground == true,
                readbackMode = renderInfo.ReadbackMode,
                foregroundWindowRequired = false
            });

            if (!string.IsNullOrWhiteSpace(renderInfo.OverlayPath))
            {
                var overlayInfo = new FileInfo(renderInfo.OverlayPath);
                data["overlayImagePath"] = renderInfo.OverlayPath;
                data["overlayFileUri"] = new Uri(renderInfo.OverlayPath).AbsoluteUri;
                data["overlayFileSizeBytes"] = overlayInfo.Exists ? overlayInfo.Length : 0;
            }

            if (!string.IsNullOrWhiteSpace(renderInfo.CompositePath))
            {
                var compositeInfo = new FileInfo(renderInfo.CompositePath);
                data["compositeImagePath"] = renderInfo.CompositePath;
                data["compositeFileUri"] = new Uri(renderInfo.CompositePath).AbsoluteUri;
                data["compositeFileSizeBytes"] = compositeInfo.Exists ? compositeInfo.Length : 0;
            }

            data["framedTarget"] = frame?.FocusedTarget == null ? null : JToken.FromObject(BuildGameObjectInfo(frame.FocusedTarget));
            data["frameBounds"] = frame == null ? null : JToken.FromObject(BuildBoundsInfo(frame.Bounds));
            data["visibleObjects"] = visualContext == null ? new JArray() : JToken.FromObject(visualContext.VisibleObjects.Select(item => item.Metadata).ToArray());
            data["nearbyObjects"] = visualContext == null ? new JArray() : JToken.FromObject(visualContext.NearbyObjects.Select(item => item.Metadata).ToArray());
            data["annotations"] = visualContext == null ? new JArray() : JToken.FromObject(visualContext.OverlayItems.Select(BuildAnnotationInfo).ToArray());
            return data;
        }

        static object BuildProjectInfo()
        {
            var identity = ProjectIdentity.GetOrCreate();
            return new
            {
                projectId = identity.ProjectId,
                projectName = identity.ProjectName,
                projectRoot = identity.ProjectRoot
            };
        }

        static object BuildRequestInfo(CaptureViewParams parameters)
        {
            return new
            {
                action = parameters.Action.ToString(),
                width = parameters.Width,
                height = parameters.Height,
                target = parameters.Target,
                searchMethod = parameters.SearchMethod,
                view = parameters.View.ToString(),
                views = parameters.Views == null ? null : parameters.Views.Select(view => view.ToString()).ToArray(),
                zoom = parameters.Zoom.ToString(),
                orthographic = parameters.Orthographic,
                camera = parameters.Camera,
                outputDirectory = parameters.OutputDirectory,
                fileName = parameters.FileName,
                tag = parameters.Tag,
                transparentBackground = parameters.TransparentBackground == true,
                overlay = parameters.Overlay == true,
                separateOverlay = parameters.SeparateOverlay == true,
                compositeOverlay = parameters.CompositeOverlay != false,
                includeNearbyObjects = parameters.IncludeNearbyObjects,
                nearbyRadius = parameters.NearbyRadius,
                maxObjects = parameters.MaxObjects,
                seriesCount = parameters.SeriesCount,
                seriesIntervalSeconds = parameters.SeriesIntervalSeconds,
                contactSheetColumns = parameters.ContactSheetColumns,
                includeContactSheetLabels = parameters.IncludeContactSheetLabels,
                baselinePath = parameters.BaselinePath,
                comparePath = parameters.ComparePath,
                deleteAll = parameters.DeleteAll,
                keepLatest = parameters.KeepLatest,
                maxAgeDays = parameters.MaxAgeDays,
                advanceMs = parameters.AdvanceMs,
                simulateParticles = parameters.SimulateParticles,
                sampleAnimations = parameters.SampleAnimations,
                readbackMode = parameters.ReadbackMode.ToString(),
                screenshotTimeoutMs = parameters.ScreenshotTimeoutMs
            };
        }

        static object BuildSceneInfo(Scene scene)
        {
            return new
            {
                name = scene.name,
                path = scene.path,
                buildIndex = scene.buildIndex,
                isLoaded = scene.isLoaded,
                isDirty = scene.isDirty
            };
        }

        static object BuildCameraInfo(Camera camera)
        {
            if (camera == null)
            {
                return null;
            }

            return new
            {
                name = camera.name,
                objectId = UnityApiAdapter.GetObjectId(camera),
                gameObjectId = UnityApiAdapter.GetObjectId(camera.gameObject),
                hierarchyPath = GetHierarchyPath(camera.gameObject),
                scene = BuildSceneInfo(camera.gameObject.scene),
                enabled = camera.enabled,
                activeInHierarchy = camera.gameObject.activeInHierarchy,
                tag = camera.gameObject.tag,
                depth = camera.depth,
                clearFlags = camera.clearFlags.ToString(),
                orthographic = camera.orthographic,
                fieldOfView = camera.fieldOfView,
                orthographicSize = camera.orthographicSize,
                nearClipPlane = camera.nearClipPlane,
                farClipPlane = camera.farClipPlane
            };
        }

        static object BuildGameObjectInfo(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            return new
            {
                name = gameObject.name,
                objectId = UnityApiAdapter.GetObjectId(gameObject),
                hierarchyPath = GetHierarchyPath(gameObject),
                scene = BuildSceneInfo(gameObject.scene),
                tag = gameObject.tag,
                layer = LayerMask.LayerToName(gameObject.layer),
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                transform = new
                {
                    position = BuildVector3Info(gameObject.transform.position),
                    rotation = BuildVector3Info(gameObject.transform.eulerAngles),
                    scale = BuildVector3Info(gameObject.transform.lossyScale)
                },
                bounds = BuildBoundsInfo(CalculateObjectBounds(gameObject)),
                components = gameObject.GetComponents<Component>()
                    .Where(component => component != null)
                    .Select(component => component.GetType().Name)
                    .ToArray()
            };
        }

        static object BuildBoundsInfo(Bounds bounds)
        {
            return new
            {
                center = BuildVector3Info(bounds.center),
                size = BuildVector3Info(bounds.size),
                min = BuildVector3Info(bounds.min),
                max = BuildVector3Info(bounds.max)
            };
        }

        static object BuildVector3Info(Vector3 value)
        {
            return new
            {
                x = value.x,
                y = value.y,
                z = value.z
            };
        }

        static string BuildOutputPath(CaptureViewParams parameters, string defaultPrefix)
        {
            var directory = ResolveOutputDirectory(parameters.OutputDirectory);
            var fileName = string.IsNullOrWhiteSpace(parameters.FileName)
                ? BuildDefaultFileName(defaultPrefix, parameters.Tag)
                : SanitizeFileName(parameters.FileName.Trim());

            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                fileName += ".png";
            }

            return Path.GetFullPath(Path.Combine(directory, fileName));
        }

        static string ResolveOutputDirectory(string requestedDirectory)
        {
            if (!string.IsNullOrWhiteSpace(requestedDirectory))
            {
                return ExpandHomePath(requestedDirectory.Trim());
            }

            var identity = ProjectIdentity.GetOrCreate();
            var projectFolder = $"{SanitizeFileName(identity.ProjectName)}_{identity.ProjectId.Substring(0, Math.Min(8, identity.ProjectId.Length))}";
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".unibridge",
                "captures",
                projectFolder);
        }

        static string ExpandHomePath(string path)
        {
            if (path == "~")
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
            }

            return path;
        }

        static Texture2D LoadPngTexture(string path)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path)))
            {
                Object.DestroyImmediate(texture);
                throw new InvalidOperationException($"Unable to load PNG image: {path}");
            }

            return texture;
        }

        static IEnumerable<CaptureViewDirection> ResolveContactSheetViews(CaptureViewParams parameters)
        {
            if (parameters.Views != null && parameters.Views.Length > 0)
            {
                return parameters.Views;
            }

            if (parameters.View != CaptureViewDirection.Current)
            {
                return new[] { parameters.View };
            }

            return DefaultContactSheetViews;
        }

        static int ResolveContactSheetColumns(int? requestedColumns, int totalCells)
        {
            if (totalCells <= 1)
            {
                return 1;
            }

            var fallback = Mathf.CeilToInt(Mathf.Sqrt(totalCells));
            return Mathf.Clamp(requestedColumns.GetValueOrDefault(fallback), 1, totalCells);
        }

        static string BuildContactSheetFrameFileName(CaptureViewParams parameters, CaptureViewDirection view, int frameIndex, int framesPerView)
        {
            var viewSuffix = SanitizeFileName(view.ToString().ToLowerInvariant());
            var frameSuffix = framesPerView > 1 ? $"_{frameIndex + 1:00}" : string.Empty;

            if (string.IsNullOrWhiteSpace(parameters.FileName))
            {
                var tagParts = new[] { parameters.Tag, viewSuffix + frameSuffix }
                    .Where(part => !string.IsNullOrWhiteSpace(part));
                return BuildDefaultFileName("contact_cell", string.Join("_", tagParts));
            }

            var safe = SanitizeFileName(parameters.FileName.Trim());
            var extension = Path.GetExtension(safe);
            var nameWithoutExtension = string.IsNullOrWhiteSpace(extension)
                ? safe
                : safe.Substring(0, safe.Length - extension.Length);

            return $"{nameWithoutExtension}_{viewSuffix}{frameSuffix}{(string.IsNullOrWhiteSpace(extension) ? ".png" : extension)}";
        }

        static string BuildContactSheetFrameTag(CaptureViewParams parameters, CaptureViewDirection view, int frameIndex, int framesPerView)
        {
            var viewSuffix = view.ToString().ToLowerInvariant();
            var frameSuffix = framesPerView > 1 ? $"_{frameIndex + 1:00}" : string.Empty;
            return string.IsNullOrWhiteSpace(parameters.Tag)
                ? $"contact_{viewSuffix}{frameSuffix}"
                : $"{parameters.Tag}_{viewSuffix}{frameSuffix}";
        }

        static string BuildContactSheetLabel(CaptureViewDirection view, int frameIndex, int framesPerView)
        {
            var label = view.ToString().ToUpperInvariant();
            return framesPerView > 1 ? $"{label} {frameIndex + 1:00}" : label;
        }

        static Texture2D BuildViewContactSheet(
            IReadOnlyList<ContactSheetCell> cells,
            int cellWidth,
            int cellHeight,
            int columns,
            int rows,
            int gutter,
            bool includeLabels,
            Color32 background)
        {
            var width = cellWidth * columns + gutter * (columns - 1);
            var height = cellHeight * rows + gutter * (rows - 1);
            var sheet = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            sheet.SetPixels32(Enumerable.Repeat(background, width * height).ToArray());
            for (var index = 0; index < cells.Count; index++)
            {
                var column = index % columns;
                var row = index / columns;
                var x = column * (cellWidth + gutter);
                var y = (rows - 1 - row) * (cellHeight + gutter);
                BlitTextureIntoSheet(sheet, cells[index].Texture, x, y, cellWidth, cellHeight);

                if (includeLabels)
                {
                    DrawLabel(sheet, x + 8, y + cellHeight - 28, cells[index].Label, new Color32(255, 255, 255, 255));
                }
            }

            sheet.Apply(false, false);
            return sheet;
        }

        static void BlitTextureIntoSheet(Texture2D destination, Texture2D source, int offsetX, int offsetY, int cellWidth, int cellHeight)
        {
            if (source == null)
            {
                return;
            }

            var sourcePixels = source.GetPixels32();
            var copyWidth = Mathf.Min(source.width, cellWidth);
            var copyHeight = Mathf.Min(source.height, cellHeight);
            var startX = offsetX + Mathf.Max(0, (cellWidth - copyWidth) / 2);
            var startY = offsetY + Mathf.Max(0, (cellHeight - copyHeight) / 2);
            for (var y = 0; y < copyHeight; y++)
            {
                for (var x = 0; x < copyWidth; x++)
                {
                    var pixel = sourcePixels[y * source.width + x];
                    if (pixel.a == 0)
                    {
                        continue;
                    }

                    BlendPixel(destination, startX + x, startY + y, pixel);
                }
            }
        }

        static string BuildDefaultFileName(string prefix, string tag)
        {
            var safePrefix = SanitizeFileName(prefix);
            var safeTag = SanitizeFileName(tag);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            return string.IsNullOrWhiteSpace(safeTag)
                ? $"{safePrefix}_{timestamp}.png"
                : $"{safePrefix}_{safeTag}_{timestamp}.png";
        }

        static string BuildSeriesFileName(CaptureViewParams parameters, int index, int count)
        {
            var digits = count >= 100 ? 3 : 2;
            var suffix = (index + 1).ToString(new string('0', digits));

            if (string.IsNullOrWhiteSpace(parameters.FileName))
            {
                var prefix = !string.IsNullOrWhiteSpace(parameters.Camera) ? "series_gamecamera" : "series_sceneview";
                var tag = string.IsNullOrWhiteSpace(parameters.Tag) ? suffix : $"{parameters.Tag}_{suffix}";
                return BuildDefaultFileName(prefix, tag);
            }

            var safe = SanitizeFileName(parameters.FileName.Trim());
            var extension = Path.GetExtension(safe);
            var nameWithoutExtension = string.IsNullOrWhiteSpace(extension)
                ? safe
                : safe.Substring(0, safe.Length - extension.Length);

            return $"{nameWithoutExtension}_{suffix}{(string.IsNullOrWhiteSpace(extension) ? ".png" : extension)}";
        }

        static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(value
                .Select(character => invalid.Contains(character) ? '_' : character)
                .Select(character => char.IsWhiteSpace(character) ? '_' : character)
                .ToArray());

            safe = new string(safe.Where(character => char.IsLetterOrDigit(character) || character == '_' || character == '-' || character == '.').ToArray());
            return string.IsNullOrWhiteSpace(safe) ? "capture" : safe.Trim('_', '.');
        }

        static int ClampDimension(int? value, int fallback)
        {
            return Mathf.Clamp(value.GetValueOrDefault(fallback), MinDimension, MaxDimension);
        }

        static bool ShouldRenderOverlay(CaptureViewParams parameters)
        {
            return parameters.Overlay == true || parameters.SeparateOverlay == true;
        }

        static string ActionToFilePrefix(CaptureViewAction action)
        {
            switch (action)
            {
                case CaptureViewAction.CaptureGameView:
                    return "gameview";
                case CaptureViewAction.CaptureSelection:
                    return "selection";
                case CaptureViewAction.CaptureObject:
                    return "object";
                case CaptureViewAction.CapturePrefabStage:
                    return "prefabstage";
                case CaptureViewAction.CaptureSceneOverview:
                    return "sceneoverview";
                case CaptureViewAction.CaptureAroundObject:
                    return "aroundobject";
                case CaptureViewAction.CaptureContactSheet:
                    return "view_contact_sheet";
                case CaptureViewAction.CaptureSceneView:
                default:
                    return "sceneview";
            }
        }

        static object BuildPrefabStageInfo(PrefabStage prefabStage)
        {
            if (prefabStage == null)
            {
                return new
                {
                    isOpen = false,
                    assetPath = (string)null,
                    rootName = (string)null,
                    rootHierarchyPath = (string)null,
                    isDirty = false,
                    mode = (string)null
                };
            }

            return new
            {
                isOpen = true,
                assetPath = prefabStage.assetPath,
                rootName = prefabStage.prefabContentsRoot?.name,
                rootHierarchyPath = prefabStage.prefabContentsRoot == null ? null : GetHierarchyPath(prefabStage.prefabContentsRoot),
                isDirty = prefabStage.scene.isDirty,
                mode = prefabStage.mode.ToString()
            };
        }

        static Quaternion DirectionToRotation(CaptureViewDirection direction)
        {
            switch (direction)
            {
                case CaptureViewDirection.Front:
                    return Quaternion.LookRotation(Vector3.forward, Vector3.up);
                case CaptureViewDirection.Back:
                    return Quaternion.LookRotation(Vector3.back, Vector3.up);
                case CaptureViewDirection.Left:
                    return Quaternion.LookRotation(Vector3.right, Vector3.up);
                case CaptureViewDirection.Right:
                    return Quaternion.LookRotation(Vector3.left, Vector3.up);
                case CaptureViewDirection.Top:
                    return Quaternion.LookRotation(Vector3.down, Vector3.forward);
                case CaptureViewDirection.Bottom:
                    return Quaternion.LookRotation(Vector3.up, Vector3.forward);
                case CaptureViewDirection.Iso:
                default:
                    return Quaternion.LookRotation(new Vector3(1f, -1f, 1f).normalized, Vector3.up);
            }
        }

        static float GetZoomScale(CaptureViewZoom zoom)
        {
            switch (zoom)
            {
                case CaptureViewZoom.Close:
                    return 0.5f;
                case CaptureViewZoom.Far:
                    return 2.5f;
                case CaptureViewZoom.Normal:
                default:
                    return 1f;
            }
        }

        static float GetEffectiveZoomScale(CaptureViewParams parameters)
        {
            if (parameters.Action == CaptureViewAction.CaptureAroundObject && parameters.Zoom == CaptureViewZoom.Normal)
            {
                return GetZoomScale(CaptureViewZoom.Far);
            }

            if (parameters.Action == CaptureViewAction.CaptureSceneOverview && parameters.Zoom == CaptureViewZoom.Normal)
            {
                return 1.25f;
            }

            return GetZoomScale(parameters.Zoom);
        }

        static float ComputeOrthographicSize(Bounds bounds)
        {
            return Mathf.Max(bounds.extents.y * 1.25f, bounds.extents.x * 0.75f, bounds.extents.z * 0.75f, 0.5f);
        }

        static float ComputeOrthographicSize(Bounds bounds, Quaternion rotation, float aspect)
        {
            var inverseRotation = Quaternion.Inverse(rotation);
            var center = bounds.center;
            var maxX = 0f;
            var maxY = 0f;

            foreach (var corner in GetBoundsCorners(bounds))
            {
                var local = inverseRotation * (corner - center);
                maxX = Mathf.Max(maxX, Mathf.Abs(local.x));
                maxY = Mathf.Max(maxY, Mathf.Abs(local.y));
            }

            return Mathf.Max(maxY, maxX / Mathf.Max(aspect, 0.01f), 0.5f) * 1.05f;
        }

        static Bounds CalculateObjectBounds(GameObject gameObject)
        {
            var hasBounds = false;
            var bounds = default(Bounds);

            void Encapsulate(Bounds candidate)
            {
                if (!hasBounds)
                {
                    bounds = candidate;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(candidate);
                }
            }

            foreach (var renderer in gameObject.GetComponentsInChildren<Renderer>(true))
            {
                Encapsulate(renderer.bounds);
            }

            foreach (var collider in gameObject.GetComponentsInChildren<Collider>(true))
            {
                Encapsulate(collider.bounds);
            }

            foreach (var collider2D in gameObject.GetComponentsInChildren<Collider2D>(true))
            {
                Encapsulate(collider2D.bounds);
            }

            foreach (var rectTransform in gameObject.GetComponentsInChildren<RectTransform>(true))
            {
                var corners = new Vector3[4];
                rectTransform.GetWorldCorners(corners);
                foreach (var corner in corners)
                {
                    Encapsulate(new Bounds(corner, Vector3.zero));
                }
            }

            return hasBounds ? bounds : new Bounds(gameObject.transform.position, Vector3.one);
        }

        static Bounds? CalculateSceneBounds()
        {
            var hasBounds = false;
            var bounds = default(Bounds);
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            var roots = prefabStage != null && prefabStage.prefabContentsRoot != null
                ? new[] { prefabStage.prefabContentsRoot }
                : SceneManager.GetActiveScene().GetRootGameObjects();

            foreach (var root in roots)
            {
                if (root == null || !root.activeInHierarchy)
                {
                    continue;
                }

                var objectBounds = CalculateObjectBounds(root);
                if (!hasBounds)
                {
                    bounds = objectBounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(objectBounds);
                }
            }

            return hasBounds ? bounds : null;
        }

        static Bounds AddBoundsPadding(Bounds bounds, float relativePadding, float minimumAxisPadding)
        {
            var padding = new Vector3(
                Mathf.Max(bounds.size.x * relativePadding, minimumAxisPadding),
                Mathf.Max(bounds.size.y * relativePadding, minimumAxisPadding),
                Mathf.Max(bounds.size.z * relativePadding, minimumAxisPadding));

            bounds.Expand(padding);
            return bounds;
        }

        static string GetHierarchyPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var path = gameObject.name;
            var current = gameObject.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        static SceneViewHandle GetOrCreateSceneView()
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                return new SceneViewHandle(sceneView, null);
            }

            sceneView = EditorWindow.GetWindow<SceneView>();
            return new SceneViewHandle(sceneView, () => sceneView.Close());
        }

        readonly struct TempScreenshotPath
        {
            public TempScreenshotPath(string relativePath, string fullPath)
            {
                RelativePath = relativePath;
                FullPath = fullPath;
            }

            public string RelativePath { get; }
            public string FullPath { get; }
        }

        readonly struct ScreenshotCaptureResult
        {
            public ScreenshotCaptureResult(Texture2D texture, int elapsedMs, int loadAttempts, long fileSizeBytes)
            {
                Texture = texture;
                ElapsedMs = elapsedMs;
                LoadAttempts = loadAttempts;
                FileSizeBytes = fileSizeBytes;
            }

            public Texture2D Texture { get; }
            public int ElapsedMs { get; }
            public int LoadAttempts { get; }
            public long FileSizeBytes { get; }
        }

        readonly struct RenderInfo
        {
            public RenderInfo(int width, int height, string overlayPath, string compositePath, string readbackMode)
            {
                Width = width;
                Height = height;
                OverlayPath = overlayPath;
                CompositePath = compositePath;
                ReadbackMode = readbackMode;
            }

            public int Width { get; }
            public int Height { get; }
            public string OverlayPath { get; }
            public string CompositePath { get; }
            public string ReadbackMode { get; }
        }

        sealed class TempCameraHandle : IDisposable
        {
            public TempCameraHandle(Camera camera, Bounds frameBounds)
            {
                Camera = camera;
                Info = new
                {
                    created = camera != null,
                    cameraName = camera == null ? null : camera.name,
                    orthographic = camera != null && camera.orthographic,
                    orthographicSize = camera == null ? 0f : camera.orthographicSize,
                    fieldOfView = camera == null ? 0f : camera.fieldOfView,
                    nearClipPlane = camera == null ? 0f : camera.nearClipPlane,
                    farClipPlane = camera == null ? 0f : camera.farClipPlane,
                    position = camera == null ? null : BuildVector3Info(camera.transform.position),
                    rotation = camera == null ? null : BuildVector3Info(camera.transform.eulerAngles),
                    frameBounds = BuildBoundsInfo(frameBounds)
                };
            }

            public Camera Camera { get; }
            public object Info { get; }

            public void Dispose()
            {
                if (Camera != null)
                {
                    Object.DestroyImmediate(Camera.gameObject);
                }
            }
        }

        sealed class ContactSheetCell
        {
            public ContactSheetCell(int index, CaptureViewDirection view, int frame, string label, string imagePath, Texture2D texture, JObject data)
            {
                Index = index;
                View = view;
                Frame = frame;
                Label = label;
                ImagePath = imagePath;
                Texture = texture;
                Data = data;
            }

            public int Index { get; }
            public CaptureViewDirection View { get; }
            public int Frame { get; }
            public string Label { get; }
            public string ImagePath { get; }
            public Texture2D Texture { get; }
            public JObject Data { get; }
        }

        sealed class SceneViewHandle : IDisposable
        {
            readonly Action m_OnDispose;

            public SceneViewHandle(SceneView sceneView, Action onDispose)
            {
                SceneView = sceneView;
                m_OnDispose = onDispose;
            }

            public SceneView SceneView { get; }

            public void Dispose()
            {
                m_OnDispose?.Invoke();
            }
        }

        sealed class SceneViewFrame
        {
            public SceneViewFrame(GameObject focusedTarget, Bounds bounds)
            {
                FocusedTarget = focusedTarget;
                Bounds = bounds;
            }

            public GameObject FocusedTarget { get; }
            public Bounds Bounds { get; }
        }

        sealed class SceneCaptureTargetResult
        {
            SceneCaptureTargetResult(bool success, GameObject target, Bounds? frameBounds, string captureKind, string filePrefix, object stageInfo, string error)
            {
                Success = success;
                Target = target;
                FrameBounds = frameBounds;
                CaptureKind = captureKind;
                FilePrefix = filePrefix;
                StageInfo = stageInfo;
                Error = error;
            }

            public bool Success { get; }
            public GameObject Target { get; }
            public Bounds? FrameBounds { get; }
            public string CaptureKind { get; }
            public string FilePrefix { get; }
            public object StageInfo { get; }
            public string Error { get; }

            public static SceneCaptureTargetResult Ok(GameObject target, Bounds? frameBounds, string captureKind, string filePrefix, object stageInfo)
            {
                return new SceneCaptureTargetResult(true, target, frameBounds, captureKind, filePrefix, stageInfo, null);
            }

            public static SceneCaptureTargetResult Fail(string error)
            {
                return new SceneCaptureTargetResult(false, null, null, null, null, null, error);
            }
        }

        sealed class VisualContext
        {
            public VisualContext(List<VisualObjectInfo> visibleObjects, List<VisualObjectInfo> nearbyObjects, List<VisualObjectInfo> overlayItems)
            {
                VisibleObjects = visibleObjects;
                NearbyObjects = nearbyObjects;
                OverlayItems = overlayItems;
            }

            public List<VisualObjectInfo> VisibleObjects { get; }
            public List<VisualObjectInfo> NearbyObjects { get; }
            public List<VisualObjectInfo> OverlayItems { get; }
        }

        readonly struct OverlayLegendLine
        {
            public OverlayLegendLine(string text, Color32 color)
            {
                Text = text;
                Color = color;
            }

            public string Text { get; }
            public Color32 Color { get; }
        }

        sealed class VisualObjectInfo
        {
            public VisualObjectInfo(string name, long objectId, bool isTarget, bool isVisible, float distanceToFocus, Rect viewportRect, object metadata)
            {
                Label = name;
                ObjectId = objectId;
                IsTarget = isTarget;
                IsVisible = isVisible;
                DistanceToFocus = distanceToFocus;
                ViewportRect = viewportRect;
                Metadata = metadata;
            }

            public int Marker { get; set; }
            public string Label { get; }
            public long ObjectId { get; }
            public bool IsTarget { get; }
            public bool IsVisible { get; }
            public float DistanceToFocus { get; }
            public Rect ViewportRect { get; }
            public object Metadata { get; }
        }
    }
}
