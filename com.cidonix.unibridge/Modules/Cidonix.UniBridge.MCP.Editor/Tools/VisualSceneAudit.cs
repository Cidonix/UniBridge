#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Deterministic visual quality gate for scene/camera captures.
    /// </summary>
    public static class VisualSceneAudit
    {
        const string ToolName = "UniBridge_VisualSceneAudit";
        const int DefaultWidth = 960;
        const int DefaultHeight = 540;
        const int MinDimension = 64;
        const int MaxDimension = 4096;
        const float DefaultMaxMagentaRatio = 0.12f;
        const float DefaultWarnMagentaRatio = 0.04f;
        const float DefaultMaxSingleColorRatio = 0.92f;
        const float DefaultWarnSingleColorRatio = 0.75f;
        const float DefaultMaxNearWhiteRatio = 0.45f;
        const float DefaultWarnNearWhiteRatio = 0.28f;
        const float DefaultMaxDarkRatio = 0.78f;
        const float DefaultMaxBrightRatio = 0.58f;
        const float DefaultMinColorDiversity = 0.004f;
        const float DefaultMinTargetCoverage = 0.015f;
        const float DefaultMaxTargetCoverage = 0.98f;

        public const string Title = "Audit scene visual quality";

        public const string Description = @"Audit a Unity scene/camera capture for obvious visual self-check failures.

Use this after an agent creates or changes visible scene content, materials, lighting, cameras, or UI staging and needs a deterministic sanity check before claiming that the visual result is acceptable.

Actions:
    AuditCapture: Render a Camera to PNG, analyze pixels, scene renderers, materials, target framing, and optionally the console.
    AuditImage: Analyze an existing PNG path.
    AuditScene: Analyze scene renderers, materials, target framing, and optionally the console without writing a PNG.

The audit intentionally catches broad, high-signal failures rather than judging art direction: fallback-magenta dominance, near-white block dominance, nearly blank/monochrome output, too-dark/too-bright captures, missing/broken materials, target not visible to the camera, suspicious target framing, and console warnings/errors.

Returns success=true for completed audits with data.passed=false when quality gates fail. Set FailOnIssues=true when the caller wants audit failures to return an MCP error.";

        [McpSchema(ToolName)]
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
                        description = "Audit operation.",
                        @enum = new[] { "AuditCapture", "AuditImage", "AuditScene" },
                        @default = "AuditCapture"
                    },
                    Target = new { type = "string", description = "Optional GameObject name/path/id whose renderers should be treated as the authored subject." },
                    Camera = new { type = "string", description = "Optional Camera name/path/id for AuditCapture or framing checks. If omitted, Main Camera or the first enabled Camera is used; AuditCapture can create a temporary overview camera as a fallback." },
                    SearchMethod = new
                    {
                        type = "string",
                        description = "How to resolve Target or Camera.",
                        @enum = new[] { "by_name", "by_id", "by_path", "by_id_or_name_or_path" },
                        @default = "by_id_or_name_or_path"
                    },
                    ImagePath = new { type = "string", description = "PNG path to analyze for AuditImage." },
                    OutputPath = new { type = "string", description = "Optional PNG path for AuditCapture. Defaults to Library/UniBridge/VisualAudits." },
                    Width = new { type = "integer", description = "AuditCapture PNG width in pixels, clamped to 64..4096.", @default = DefaultWidth },
                    Height = new { type = "integer", description = "AuditCapture PNG height in pixels, clamped to 64..4096.", @default = DefaultHeight },
                    Strict = new { type = "boolean", description = "When true, warnings also make data.passed=false.", @default = false },
                    IncludeConsole = new { type = "boolean", description = "Include UniBridge_ReadConsole DiagnosticSummary and gate on current warnings/errors.", @default = true },
                    FailOnIssues = new { type = "boolean", description = "When true, return success=false if data.passed=false.", @default = false },
                    MaxMagentaRatio = new { type = "number", description = "Error threshold for fallback-magenta-like pixel coverage.", @default = DefaultMaxMagentaRatio },
                    MaxSingleColorRatio = new { type = "number", description = "Error threshold for one quantized color dominating the capture.", @default = DefaultMaxSingleColorRatio },
                    MaxNearWhiteRatio = new { type = "number", description = "Error threshold for near-white area dominance.", @default = DefaultMaxNearWhiteRatio },
                    MaxDarkRatio = new { type = "number", description = "Warning threshold for very dark captures.", @default = DefaultMaxDarkRatio },
                    MaxBrightRatio = new { type = "number", description = "Warning threshold for very bright captures.", @default = DefaultMaxBrightRatio },
                    MinColorDiversity = new { type = "number", description = "Warning threshold for quantized color diversity.", @default = DefaultMinColorDiversity },
                    MinTargetCoverage = new { type = "number", description = "Warning threshold for target viewport area.", @default = DefaultMinTargetCoverage },
                    MaxTargetCoverage = new { type = "number", description = "Warning threshold for target viewport area.", @default = DefaultMaxTargetCoverage }
                }
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "vision", "scene", "debug" }, EnabledByDefault = true, ExecutionPolicy = ToolExecutionPolicy.Capture)]
        public static object HandleCommand(JObject rawParameters)
        {
            var parameters = AuditParameters.Parse(rawParameters);
            var issues = new List<AuditIssue>();
            Texture2D texture = null;
            string imagePath = null;
            Camera camera = null;
            bool createdTemporaryCamera = false;

            try
            {
                var target = ResolveOptionalGameObject(parameters.Target, parameters.SearchMethod);
                if (!string.IsNullOrWhiteSpace(parameters.Target) && target == null)
                {
                    return Response.Error($"Target '{parameters.Target}' was not found.");
                }

                if (parameters.Action is VisualSceneAuditAction.AuditCapture or VisualSceneAuditAction.AuditScene)
                {
                    camera = ResolveCamera(parameters.Camera, parameters.SearchMethod);
                    if (!string.IsNullOrWhiteSpace(parameters.Camera) && camera == null)
                    {
                        return Response.Error($"Camera '{parameters.Camera}' was not found.");
                    }

                    if (parameters.Action == VisualSceneAuditAction.AuditCapture && camera == null)
                    {
                        camera = CreateTemporaryOverviewCamera(target);
                        createdTemporaryCamera = true;
                    }
                }

                PixelAuditMetrics pixelMetrics = null;
                if (parameters.Action == VisualSceneAuditAction.AuditCapture)
                {
                    imagePath = ResolveOutputPath(parameters.OutputPath);
                    texture = RenderCamera(camera, parameters.Width, parameters.Height, imagePath);
                    pixelMetrics = AnalyzePixels(texture, parameters, issues);
                }
                else if (parameters.Action == VisualSceneAuditAction.AuditImage)
                {
                    imagePath = parameters.ImagePath;
                    if (string.IsNullOrWhiteSpace(imagePath))
                    {
                        return Response.Error("AuditImage requires ImagePath.");
                    }

                    texture = LoadTexture(imagePath);
                    pixelMetrics = AnalyzePixels(texture, parameters, issues);
                }

                var sceneMetrics = parameters.Action == VisualSceneAuditAction.AuditImage
                    ? null
                    : AnalyzeScene(target, camera, parameters, issues);
                var consoleSummary = parameters.IncludeConsole ? AnalyzeConsole(parameters, issues) : null;
                var issueData = issues.Select(issue => issue.ToData()).ToArray();
                var errorCount = issues.Count(issue => issue.Severity == "error");
                var warningCount = issues.Count(issue => issue.Severity == "warning");
                var passed = errorCount == 0 && (!parameters.Strict || warningCount == 0);
                var score = ComputeScore(issues, pixelMetrics, sceneMetrics);
                var data = new
                {
                    action = parameters.Action.ToString(),
                    passed,
                    score,
                    strict = parameters.Strict,
                    issueCount = issues.Count,
                    errorCount,
                    warningCount,
                    issues = issueData,
                    recommendations = BuildRecommendations(issues, pixelMetrics, sceneMetrics),
                    image = string.IsNullOrWhiteSpace(imagePath) ? null : new
                    {
                        path = imagePath,
                        uri = new Uri(Path.GetFullPath(imagePath)).AbsoluteUri,
                        width = texture != null ? texture.width : (int?)null,
                        height = texture != null ? texture.height : (int?)null
                    },
                    pixelMetrics,
                    sceneMetrics,
                    console = consoleSummary,
                    camera = camera == null ? null : new
                    {
                        name = camera.name,
                        path = SceneObjectLocator.GetHierarchyPath(camera.gameObject),
                        instanceId = UnityApiAdapter.GetObjectId(camera),
                        createdTemporary = createdTemporaryCamera,
                        orthographic = camera.orthographic,
                        fieldOfView = camera.fieldOfView,
                        position = ToVector3Data(camera.transform.position),
                        rotation = ToVector3Data(camera.transform.eulerAngles)
                    },
                    target = target == null ? null : new
                    {
                        name = target.name,
                        path = SceneObjectLocator.GetHierarchyPath(target),
                        instanceId = UnityApiAdapter.GetObjectId(target)
                    }
                };

                if (!passed && parameters.FailOnIssues)
                {
                    return Response.Error("Visual scene audit failed.", data);
                }

                return Response.Success(passed ? "Visual scene audit passed." : "Visual scene audit completed with issues.", data);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VisualSceneAudit] {parameters.Action} failed: {ex}");
                return Response.Error($"Visual scene audit failed: {ex.Message}");
            }
            finally
            {
                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }

                if (createdTemporaryCamera && camera != null)
                {
                    Object.DestroyImmediate(camera.gameObject);
                }
            }
        }

        static PixelAuditMetrics AnalyzePixels(Texture2D texture, AuditParameters parameters, List<AuditIssue> issues)
        {
            var pixels = texture.GetPixels32();
            var sampleCount = pixels.Length;
            var histogram = new Dictionary<int, int>();
            var magenta = 0;
            var nearWhite = 0;
            var dark = 0;
            var bright = 0;
            var saturated = 0;
            var transparent = 0;
            double luminanceSum = 0d;

            foreach (var pixel in pixels)
            {
                if (pixel.a < 8)
                {
                    transparent++;
                }

                var r = pixel.r / 255f;
                var g = pixel.g / 255f;
                var b = pixel.b / 255f;
                var luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                luminanceSum += luminance;

                if (r > 0.78f && b > 0.68f && g < 0.32f)
                {
                    magenta++;
                }

                if (r > 0.92f && g > 0.92f && b > 0.92f)
                {
                    nearWhite++;
                }

                if (luminance < 0.045f)
                {
                    dark++;
                }

                if (luminance > 0.90f)
                {
                    bright++;
                }

                var max = Mathf.Max(r, Mathf.Max(g, b));
                var min = Mathf.Min(r, Mathf.Min(g, b));
                if (max > 0.40f && (max - min) > 0.68f)
                {
                    saturated++;
                }

                var key = Quantize(pixel.r, pixel.g, pixel.b);
                histogram.TryGetValue(key, out var count);
                histogram[key] = count + 1;
            }

            var dominant = histogram.OrderByDescending(pair => pair.Value).FirstOrDefault();
            var magentaRatio = Ratio(magenta, sampleCount);
            var nearWhiteRatio = Ratio(nearWhite, sampleCount);
            var darkRatio = Ratio(dark, sampleCount);
            var brightRatio = Ratio(bright, sampleCount);
            var saturatedRatio = Ratio(saturated, sampleCount);
            var dominantRatio = Ratio(dominant.Value, sampleCount);
            var colorDiversity = Ratio(histogram.Count, sampleCount);
            var averageLuminance = sampleCount == 0 ? 0f : (float)(luminanceSum / sampleCount);

            if (magentaRatio >= parameters.MaxMagentaRatio)
            {
                issues.Add(AuditIssue.Error(
                    "fallback_magenta_dominance",
                    $"Capture has {Percent(magentaRatio)} fallback-magenta-like pixels.",
                    "Check missing shaders/materials, SRP compatibility, or accidental debug magenta materials."));
            }
            else if (magentaRatio >= DefaultWarnMagentaRatio)
            {
                issues.Add(AuditIssue.Warning(
                    "magenta_pixels",
                    $"Capture has {Percent(magentaRatio)} magenta-like pixels.",
                    "Verify this is intentional styling and not a material fallback."));
            }

            if (dominantRatio >= parameters.MaxSingleColorRatio)
            {
                issues.Add(AuditIssue.Error(
                    "single_color_dominance",
                    $"One quantized color covers {Percent(dominantRatio)} of the capture.",
                    "The view is likely blank, dominated by one object, or framed too close."));
            }
            else if (dominantRatio >= DefaultWarnSingleColorRatio)
            {
                issues.Add(AuditIssue.Warning(
                    "high_single_color_area",
                    $"One quantized color covers {Percent(dominantRatio)} of the capture.",
                    "Check framing and material variety before treating the capture as a good visual proof."));
            }

            if (nearWhiteRatio >= parameters.MaxNearWhiteRatio)
            {
                issues.Add(AuditIssue.Error(
                    "near_white_block_dominance",
                    $"Near-white pixels cover {Percent(nearWhiteRatio)} of the capture.",
                    "A large untextured/light surface may be blocking the authored subject."));
            }
            else if (nearWhiteRatio >= DefaultWarnNearWhiteRatio)
            {
                issues.Add(AuditIssue.Warning(
                    "large_near_white_area",
                    $"Near-white pixels cover {Percent(nearWhiteRatio)} of the capture.",
                    "Verify large white areas are intentional and not placeholder geometry."));
            }

            if (darkRatio >= parameters.MaxDarkRatio)
            {
                issues.Add(AuditIssue.Warning(
                    "very_dark_capture",
                    $"Very dark pixels cover {Percent(darkRatio)} of the capture.",
                    "Add/adjust lighting, camera exposure, or background before visual review."));
            }

            if (brightRatio >= parameters.MaxBrightRatio)
            {
                issues.Add(AuditIssue.Warning(
                    "very_bright_capture",
                    $"Very bright pixels cover {Percent(brightRatio)} of the capture.",
                    "Check exposure, clear color, and large unlit surfaces."));
            }

            if (colorDiversity < parameters.MinColorDiversity)
            {
                issues.Add(AuditIssue.Warning(
                    "low_color_diversity",
                    $"Quantized color diversity is only {colorDiversity.ToString("0.0000", CultureInfo.InvariantCulture)}.",
                    "The capture may be blank or visually under-specified."));
            }

            return new PixelAuditMetrics
            {
                sampleCount = sampleCount,
                averageLuminance = Round(averageLuminance),
                magentaRatio = Round(magentaRatio),
                nearWhiteRatio = Round(nearWhiteRatio),
                darkRatio = Round(darkRatio),
                brightRatio = Round(brightRatio),
                saturatedRatio = Round(saturatedRatio),
                dominantColorRatio = Round(dominantRatio),
                transparentRatio = Round(Ratio(transparent, sampleCount)),
                colorDiversity = Round(colorDiversity),
                dominantColor = DecodeQuantizedColor(dominant.Key)
            };
        }

        static SceneAuditMetrics AnalyzeScene(GameObject target, Camera camera, AuditParameters parameters, List<AuditIssue> issues)
        {
            var scopeObjects = target == null
                ? SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true }).ToArray()
                : target.GetComponentsInChildren<Transform>(true).Select(transform => transform.gameObject).ToArray();
            var renderers = scopeObjects
                .SelectMany(go => go.GetComponents<Renderer>())
                .Where(renderer => renderer != null)
                .ToArray();
            var enabledRenderers = renderers.Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy).ToArray();
            var materialIssues = new List<object>();
            var magentaMaterials = 0;
            var missingMaterials = 0;
            var brokenShaders = 0;

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials ?? Array.Empty<Material>();
                if (materials.Length == 0)
                {
                    missingMaterials++;
                    materialIssues.Add(new
                    {
                        objectPath = SceneObjectLocator.GetHierarchyPath(renderer.gameObject),
                        renderer = renderer.GetType().Name,
                        issue = "no_material_slots"
                    });
                    continue;
                }

                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null)
                    {
                        missingMaterials++;
                        materialIssues.Add(new
                        {
                            objectPath = SceneObjectLocator.GetHierarchyPath(renderer.gameObject),
                            renderer = renderer.GetType().Name,
                            slot = i,
                            issue = "missing_material"
                        });
                        continue;
                    }

                    if (IsBrokenShader(material))
                    {
                        brokenShaders++;
                        materialIssues.Add(new
                        {
                            objectPath = SceneObjectLocator.GetHierarchyPath(renderer.gameObject),
                            renderer = renderer.GetType().Name,
                            slot = i,
                            material = material.name,
                            shader = material.shader != null ? material.shader.name : null,
                            issue = "broken_shader"
                        });
                    }

                    if (IsMagentaMaterial(material))
                    {
                        magentaMaterials++;
                    }
                }
            }

            if (renderers.Length == 0)
            {
                issues.Add(AuditIssue.Warning(
                    "no_renderers_in_scope",
                    target == null ? "No renderers were found in the loaded scene scope." : $"Target '{target.name}' has no renderers under it.",
                    "Make sure the visual proof targets the authored visible objects."));
            }

            if (missingMaterials > 0 || brokenShaders > 0)
            {
                issues.Add(AuditIssue.Error(
                    "material_or_shader_issues",
                    $"Found {missingMaterials} missing material slot(s) and {brokenShaders} broken shader material(s).",
                    "Fix material assignments or shader compatibility before accepting the capture."));
            }

            if (magentaMaterials > 0)
            {
                issues.Add(AuditIssue.Warning(
                    "magenta_material_colors",
                    $"Found {magentaMaterials} material color(s) that are strongly magenta.",
                    "Verify these are intentional accents and not debug/fallback colors."));
            }

            Bounds? bounds = enabledRenderers.Length == 0 ? null : CombineBounds(enabledRenderers.Select(renderer => renderer.bounds));
            var scaleRatio = ComputeRendererScaleRatio(enabledRenderers);
            if (scaleRatio > 35f && enabledRenderers.Length > 2)
            {
                issues.Add(AuditIssue.Warning(
                    "extreme_renderer_scale_ratio",
                    $"Largest renderer bounds are {scaleRatio.ToString("0.0", CultureInfo.InvariantCulture)}x larger than the smallest visible renderer.",
                    "Check accidental giant geometry, helper markers, or mismatched units."));
            }

            CameraFramingMetrics framing = null;
            if (camera != null && bounds.HasValue)
            {
                framing = AnalyzeFraming(camera, bounds.Value);
                if (framing.cornersInFront == 0 || !framing.intersectsViewport)
                {
                    issues.Add(AuditIssue.Error(
                        "target_not_visible_to_camera",
                        "The audited bounds are not visible to the selected camera.",
                        "Move the camera, select the right target, or use a controlled overview camera."));
                }
                else if (framing.clippedCoverage < parameters.MinTargetCoverage)
                {
                    issues.Add(AuditIssue.Warning(
                        "target_too_small_in_camera",
                        $"Audited bounds cover only {Percent(framing.clippedCoverage)} of the camera viewport.",
                        "Frame the authored subject more deliberately before using the capture as proof."));
                }
                else if (framing.clippedCoverage > parameters.MaxTargetCoverage)
                {
                    issues.Add(AuditIssue.Warning(
                        "target_overfills_camera",
                        $"Audited bounds cover {Percent(framing.clippedCoverage)} of the camera viewport.",
                        "The camera may be too close or dominated by one large object."));
                }
            }

            return new SceneAuditMetrics
            {
                scopedObjectCount = scopeObjects.Length,
                rendererCount = renderers.Length,
                enabledRendererCount = enabledRenderers.Length,
                missingMaterialSlots = missingMaterials,
                brokenShaderMaterials = brokenShaders,
                magentaMaterialColors = magentaMaterials,
                materialIssues = materialIssues.Take(20).ToArray(),
                bounds = bounds.HasValue ? ToBoundsData(bounds.Value) : null,
                rendererScaleRatio = scaleRatio <= 0f ? 0f : Round(scaleRatio),
                cameraFraming = framing
            };
        }

        static object AnalyzeConsole(AuditParameters parameters, List<AuditIssue> issues)
        {
            try
            {
                var response = ReadConsole.HandleCommand(new ReadConsoleParams
                {
                    Action = ConsoleAction.DiagnosticSummary,
                    IncludeStacktrace = false,
                    MaxIssues = 5,
                    MaxSamples = 3
                });
                var json = JObject.FromObject(response);
                var data = json["data"];
                var totals = data?["totals"];
                var warningCount = totals?["warningCount"]?.ToObject<int>() ?? 0;
                var errorCount = totals?["errorCount"]?.ToObject<int>() ?? 0;
                var exceptionCount = totals?["exceptionCount"]?.ToObject<int>() ?? 0;
                var assertCount = totals?["assertCount"]?.ToObject<int>() ?? 0;

                if (errorCount > 0 || exceptionCount > 0 || assertCount > 0)
                {
                    issues.Add(AuditIssue.Error(
                        "console_errors_present",
                        $"Console currently has {errorCount} error(s), {exceptionCount} exception(s), and {assertCount} assert(s).",
                        "Read the diagnostic summary and fix console issues before accepting the visual result."));
                }
                else if (warningCount > 0)
                {
                    issues.Add(AuditIssue.Warning(
                        "console_warnings_present",
                        $"Console currently has {warningCount} warning(s).",
                        "Check whether warnings are related to the authored visual result."));
                }

                return data;
            }
            catch (Exception ex)
            {
                issues.Add(AuditIssue.Warning(
                    "console_audit_unavailable",
                    $"Console diagnostic summary was unavailable: {ex.Message}",
                    "Run UniBridge_ReadConsole Action=DiagnosticSummary directly if console health matters."));
                return new { unavailable = true, error = ex.Message };
            }
        }

        static Camera ResolveCamera(string cameraToken, string searchMethod)
        {
            if (!string.IsNullOrWhiteSpace(cameraToken))
            {
                if (long.TryParse(cameraToken.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    var direct = UnityApiAdapter.GetObjectFromId(id);
                    if (direct is Camera directCamera)
                    {
                        return directCamera;
                    }

                    if (direct is Component component)
                    {
                        return component.GetComponent<Camera>() ?? component.GetComponentInChildren<Camera>(true);
                    }

                    if (direct is GameObject gameObject)
                    {
                        return gameObject.GetComponent<Camera>() ?? gameObject.GetComponentInChildren<Camera>(true);
                    }
                }

                var cameraObject = ResolveOptionalGameObject(cameraToken, searchMethod);
                if (cameraObject != null)
                {
                    return cameraObject.GetComponent<Camera>() ?? cameraObject.GetComponentInChildren<Camera>(true);
                }

                return Object.FindObjectsByType<Camera>(FindObjectsInactive.Include)
                    .FirstOrDefault(cam => string.Equals(cam.name, cameraToken, StringComparison.OrdinalIgnoreCase));
            }

            var main = Camera.main;
            if (main != null && main.enabled && main.gameObject.activeInHierarchy)
            {
                return main;
            }

            return Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude)
                .FirstOrDefault(cam => cam.enabled && cam.gameObject.activeInHierarchy);
        }

        static GameObject ResolveOptionalGameObject(string value, string searchMethod)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return SceneObjectLocator.FindObject(
                value,
                string.IsNullOrWhiteSpace(searchMethod) ? "by_id_or_name_or_path" : searchMethod,
                new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
        }

        static Camera CreateTemporaryOverviewCamera(GameObject target)
        {
            var bounds = target != null
                ? CombineBounds(target.GetComponentsInChildren<Renderer>(true).Where(renderer => renderer.enabled).Select(renderer => renderer.bounds))
                : CombineBounds(SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options { IncludeInactive = false, IncludePrefabStage = true })
                    .SelectMany(go => go.GetComponents<Renderer>())
                    .Where(renderer => renderer.enabled)
                    .Select(renderer => renderer.bounds));

            var cameraObject = new GameObject("__UniBridgeVisualAudit_TemporaryCamera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.06f, 0.075f, 1f);
            camera.orthographic = true;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 1000f;

            if (bounds.HasValue)
            {
                var center = bounds.Value.center;
                var radius = Mathf.Max(1f, bounds.Value.extents.magnitude);
                var direction = new Vector3(0.85f, 0.55f, -1f).normalized;
                camera.transform.position = center - direction * Mathf.Max(6f, radius * 2.8f);
                camera.transform.LookAt(center);
                camera.orthographicSize = Mathf.Max(1.5f, radius * 0.85f);
                camera.farClipPlane = Mathf.Max(1000f, radius * 8f);
            }
            else
            {
                camera.transform.position = new Vector3(0f, 3f, -7f);
                camera.transform.rotation = Quaternion.Euler(22f, 0f, 0f);
                camera.orthographicSize = 5f;
            }

            return camera;
        }

        static Texture2D RenderCamera(Camera camera, int width, int height, string outputPath)
        {
            if (camera == null)
            {
                throw new InvalidOperationException("AuditCapture could not resolve or create a camera.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1
            };
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);

            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false, false);
                File.WriteAllBytes(outputPath, ImageConversion.EncodeToPNG(texture));
                return texture;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
        }

        static Texture2D LoadTexture(string imagePath)
        {
            var fullPath = Path.GetFullPath(imagePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("ImagePath does not exist.", fullPath);
            }

            var bytes = File.ReadAllBytes(fullPath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, bytes))
            {
                Object.DestroyImmediate(texture);
                throw new InvalidOperationException($"ImagePath '{fullPath}' could not be decoded as an image.");
            }

            return texture;
        }

        static string ResolveOutputPath(string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested))
            {
                var full = Path.GetFullPath(requested);
                if (Path.GetExtension(full).Length == 0)
                {
                    full += ".png";
                }

                return full;
            }

            var dir = Path.Combine(Directory.GetCurrentDirectory(), "Library", "UniBridge", "VisualAudits");
            var fileName = $"visual_audit_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            return Path.Combine(dir, fileName);
        }

        static Bounds? CombineBounds(IEnumerable<Bounds> boundsItems)
        {
            var hasBounds = false;
            var combined = default(Bounds);
            foreach (var bounds in boundsItems)
            {
                if (!hasBounds)
                {
                    combined = bounds;
                    hasBounds = true;
                }
                else
                {
                    combined.Encapsulate(bounds);
                }
            }

            return hasBounds ? combined : null;
        }

        static CameraFramingMetrics AnalyzeFraming(Camera camera, Bounds bounds)
        {
            var corners = GetBoundsCorners(bounds);
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            var cornersInFront = 0;
            var cornersInside = 0;

            foreach (var corner in corners)
            {
                var point = camera.WorldToViewportPoint(corner);
                if (point.z <= 0f)
                {
                    continue;
                }

                cornersInFront++;
                min = Vector2.Min(min, new Vector2(point.x, point.y));
                max = Vector2.Max(max, new Vector2(point.x, point.y));
                if (point.x >= 0f && point.x <= 1f && point.y >= 0f && point.y <= 1f)
                {
                    cornersInside++;
                }
            }

            if (cornersInFront == 0)
            {
                return new CameraFramingMetrics
                {
                    cornersInFront = 0,
                    cornersInside = 0,
                    viewportCoverage = 0f,
                    clippedCoverage = 0f,
                    intersectsViewport = false,
                    viewportRect = null
                };
            }

            var rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            var clippedMinX = Mathf.Clamp01(rect.xMin);
            var clippedMinY = Mathf.Clamp01(rect.yMin);
            var clippedMaxX = Mathf.Clamp01(rect.xMax);
            var clippedMaxY = Mathf.Clamp01(rect.yMax);
            var clippedWidth = Mathf.Max(0f, clippedMaxX - clippedMinX);
            var clippedHeight = Mathf.Max(0f, clippedMaxY - clippedMinY);
            var clippedCoverage = clippedWidth * clippedHeight;
            var viewportCoverage = Mathf.Max(0f, rect.width) * Mathf.Max(0f, rect.height);
            var intersects = clippedWidth > 0f && clippedHeight > 0f;

            return new CameraFramingMetrics
            {
                cornersInFront = cornersInFront,
                cornersInside = cornersInside,
                viewportCoverage = Round(viewportCoverage),
                clippedCoverage = Round(clippedCoverage),
                intersectsViewport = intersects,
                viewportRect = new
                {
                    xMin = Round(rect.xMin),
                    yMin = Round(rect.yMin),
                    xMax = Round(rect.xMax),
                    yMax = Round(rect.yMax),
                    width = Round(rect.width),
                    height = Round(rect.height)
                }
            };
        }

        static Vector3[] GetBoundsCorners(Bounds bounds)
        {
            var min = bounds.min;
            var max = bounds.max;
            return new[]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z)
            };
        }

        static float ComputeRendererScaleRatio(Renderer[] renderers)
        {
            var sizes = renderers
                .Select(renderer => renderer.bounds.size.magnitude)
                .Where(size => size > 0.001f)
                .OrderBy(size => size)
                .ToArray();
            if (sizes.Length < 2)
            {
                return 0f;
            }

            return sizes[^1] / Mathf.Max(0.001f, sizes[0]);
        }

        static bool IsBrokenShader(Material material)
        {
            if (material == null || material.shader == null)
            {
                return true;
            }

            var shaderName = material.shader.name ?? string.Empty;
            return shaderName.IndexOf("InternalErrorShader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   shaderName.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsMagentaMaterial(Material material)
        {
            if (material == null)
            {
                return false;
            }

            return TryGetMaterialColor(material, "_BaseColor", out var baseColor) && IsStrongMagenta(baseColor) ||
                   TryGetMaterialColor(material, "_Color", out var color) && IsStrongMagenta(color);
        }

        static bool TryGetMaterialColor(Material material, string property, out Color color)
        {
            color = default;
            if (material == null || !material.HasProperty(property))
            {
                return false;
            }

            try
            {
                color = material.GetColor(property);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool IsStrongMagenta(Color color)
        {
            return color.r > 0.75f && color.b > 0.65f && color.g < 0.35f;
        }

        static int Quantize(byte r, byte g, byte b)
        {
            return (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
        }

        static object DecodeQuantizedColor(int key)
        {
            var r = ((key >> 8) & 0x0F) * 17;
            var g = ((key >> 4) & 0x0F) * 17;
            var b = (key & 0x0F) * 17;
            return new { r, g, b, hex = $"#{r:X2}{g:X2}{b:X2}" };
        }

        static float Ratio(int value, int total)
        {
            return total <= 0 ? 0f : (float)value / total;
        }

        static string Percent(float ratio)
        {
            return (ratio * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        static float Round(float value)
        {
            return (float)Math.Round(value, 4);
        }

        static int ComputeScore(List<AuditIssue> issues, PixelAuditMetrics pixelMetrics, SceneAuditMetrics sceneMetrics)
        {
            var score = 100;
            score -= issues.Count(issue => issue.Severity == "error") * 25;
            score -= issues.Count(issue => issue.Severity == "warning") * 8;
            if (pixelMetrics != null)
            {
                score -= Mathf.RoundToInt(pixelMetrics.magentaRatio * 30f);
                score -= Mathf.RoundToInt(pixelMetrics.dominantColorRatio * 10f);
            }

            if (sceneMetrics != null && sceneMetrics.cameraFraming != null && !sceneMetrics.cameraFraming.intersectsViewport)
            {
                score -= 25;
            }

            return Mathf.Clamp(score, 0, 100);
        }

        static string[] BuildRecommendations(List<AuditIssue> issues, PixelAuditMetrics pixelMetrics, SceneAuditMetrics sceneMetrics)
        {
            if (issues.Count == 0)
            {
                return new[] { "Capture and scene metadata passed deterministic sanity checks; still inspect the PNG visually when visual quality matters." };
            }

            return issues
                .Select(issue => issue.Recommendation)
                .Where(recommendation => !string.IsNullOrWhiteSpace(recommendation))
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();
        }

        static object ToVector3Data(Vector3 value)
        {
            return new
            {
                x = Round(value.x),
                y = Round(value.y),
                z = Round(value.z)
            };
        }

        static object ToBoundsData(Bounds bounds)
        {
            return new
            {
                center = ToVector3Data(bounds.center),
                size = ToVector3Data(bounds.size),
                extents = ToVector3Data(bounds.extents)
            };
        }

        enum VisualSceneAuditAction
        {
            AuditCapture,
            AuditImage,
            AuditScene
        }

        sealed class AuditParameters
        {
            public VisualSceneAuditAction Action { get; set; } = VisualSceneAuditAction.AuditCapture;
            public string Target { get; set; }
            public string Camera { get; set; }
            public string SearchMethod { get; set; } = "by_id_or_name_or_path";
            public string ImagePath { get; set; }
            public string OutputPath { get; set; }
            public int Width { get; set; } = DefaultWidth;
            public int Height { get; set; } = DefaultHeight;
            public bool Strict { get; set; }
            public bool IncludeConsole { get; set; } = true;
            public bool FailOnIssues { get; set; }
            public float MaxMagentaRatio { get; set; } = DefaultMaxMagentaRatio;
            public float MaxSingleColorRatio { get; set; } = DefaultMaxSingleColorRatio;
            public float MaxNearWhiteRatio { get; set; } = DefaultMaxNearWhiteRatio;
            public float MaxDarkRatio { get; set; } = DefaultMaxDarkRatio;
            public float MaxBrightRatio { get; set; } = DefaultMaxBrightRatio;
            public float MinColorDiversity { get; set; } = DefaultMinColorDiversity;
            public float MinTargetCoverage { get; set; } = DefaultMinTargetCoverage;
            public float MaxTargetCoverage { get; set; } = DefaultMaxTargetCoverage;

            public static AuditParameters Parse(JObject raw)
            {
                raw ??= new JObject();
                return new AuditParameters
                {
                    Action = GetEnum(raw, VisualSceneAuditAction.AuditCapture, "Action", "action"),
                    Target = GetString(raw, "Target", "target"),
                    Camera = GetString(raw, "Camera", "camera"),
                    SearchMethod = GetString(raw, "SearchMethod", "searchMethod", "search_method") ?? "by_id_or_name_or_path",
                    ImagePath = GetString(raw, "ImagePath", "imagePath", "image_path", "Path", "path"),
                    OutputPath = GetString(raw, "OutputPath", "outputPath", "output_path"),
                    Width = Mathf.Clamp(GetInt(raw, DefaultWidth, "Width", "width"), MinDimension, MaxDimension),
                    Height = Mathf.Clamp(GetInt(raw, DefaultHeight, "Height", "height"), MinDimension, MaxDimension),
                    Strict = GetBool(raw, false, "Strict", "strict"),
                    IncludeConsole = GetBool(raw, true, "IncludeConsole", "includeConsole", "include_console"),
                    FailOnIssues = GetBool(raw, false, "FailOnIssues", "failOnIssues", "fail_on_issues"),
                    MaxMagentaRatio = Mathf.Clamp01(GetFloat(raw, DefaultMaxMagentaRatio, "MaxMagentaRatio", "maxMagentaRatio", "max_magenta_ratio")),
                    MaxSingleColorRatio = Mathf.Clamp01(GetFloat(raw, DefaultMaxSingleColorRatio, "MaxSingleColorRatio", "maxSingleColorRatio", "max_single_color_ratio")),
                    MaxNearWhiteRatio = Mathf.Clamp01(GetFloat(raw, DefaultMaxNearWhiteRatio, "MaxNearWhiteRatio", "maxNearWhiteRatio", "max_near_white_ratio")),
                    MaxDarkRatio = Mathf.Clamp01(GetFloat(raw, DefaultMaxDarkRatio, "MaxDarkRatio", "maxDarkRatio", "max_dark_ratio")),
                    MaxBrightRatio = Mathf.Clamp01(GetFloat(raw, DefaultMaxBrightRatio, "MaxBrightRatio", "maxBrightRatio", "max_bright_ratio")),
                    MinColorDiversity = Mathf.Clamp01(GetFloat(raw, DefaultMinColorDiversity, "MinColorDiversity", "minColorDiversity", "min_color_diversity")),
                    MinTargetCoverage = Mathf.Clamp01(GetFloat(raw, DefaultMinTargetCoverage, "MinTargetCoverage", "minTargetCoverage", "min_target_coverage")),
                    MaxTargetCoverage = Mathf.Clamp01(GetFloat(raw, DefaultMaxTargetCoverage, "MaxTargetCoverage", "maxTargetCoverage", "max_target_coverage"))
                };
            }
        }

        sealed class AuditIssue
        {
            public string Severity { get; private set; }
            public string Code { get; private set; }
            public string Message { get; private set; }
            public string Recommendation { get; private set; }

            public object ToData()
            {
                return new
                {
                    severity = Severity,
                    code = Code,
                    message = Message,
                    recommendation = Recommendation
                };
            }

            public static AuditIssue Error(string code, string message, string recommendation)
            {
                return new AuditIssue
                {
                    Severity = "error",
                    Code = code,
                    Message = message,
                    Recommendation = recommendation
                };
            }

            public static AuditIssue Warning(string code, string message, string recommendation)
            {
                return new AuditIssue
                {
                    Severity = "warning",
                    Code = code,
                    Message = message,
                    Recommendation = recommendation
                };
            }
        }

        sealed class PixelAuditMetrics
        {
            public int sampleCount;
            public float averageLuminance;
            public float magentaRatio;
            public float nearWhiteRatio;
            public float darkRatio;
            public float brightRatio;
            public float saturatedRatio;
            public float dominantColorRatio;
            public float transparentRatio;
            public float colorDiversity;
            public object dominantColor;
        }

        sealed class SceneAuditMetrics
        {
            public int scopedObjectCount;
            public int rendererCount;
            public int enabledRendererCount;
            public int missingMaterialSlots;
            public int brokenShaderMaterials;
            public int magentaMaterialColors;
            public object[] materialIssues;
            public object bounds;
            public float rendererScaleRatio;
            public CameraFramingMetrics cameraFraming;
        }

        sealed class CameraFramingMetrics
        {
            public int cornersInFront;
            public int cornersInside;
            public float viewportCoverage;
            public float clippedCoverage;
            public bool intersectsViewport;
            public object viewportRect;
        }

        static string GetString(JObject obj, params string[] names)
        {
            foreach (var name in names)
            {
                var token = obj[name];
                if (token != null && token.Type != JTokenType.Null)
                {
                    var value = token.ToString();
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }

            return null;
        }

        static int GetInt(JObject obj, int defaultValue, params string[] names)
        {
            foreach (var name in names)
            {
                var token = obj[name];
                if (token == null || token.Type == JTokenType.Null)
                {
                    continue;
                }

                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                {
                    return token.ToObject<int>();
                }

                if (int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            return defaultValue;
        }

        static float GetFloat(JObject obj, float defaultValue, params string[] names)
        {
            foreach (var name in names)
            {
                var token = obj[name];
                if (token == null || token.Type == JTokenType.Null)
                {
                    continue;
                }

                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                {
                    return token.ToObject<float>();
                }

                if (float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            return defaultValue;
        }

        static bool GetBool(JObject obj, bool defaultValue, params string[] names)
        {
            foreach (var name in names)
            {
                var token = obj[name];
                if (token == null || token.Type == JTokenType.Null)
                {
                    continue;
                }

                if (token.Type == JTokenType.Boolean)
                {
                    return token.ToObject<bool>();
                }

                if (bool.TryParse(token.ToString(), out var value))
                {
                    return value;
                }
            }

            return defaultValue;
        }

        static T GetEnum<T>(JObject obj, T defaultValue, params string[] names) where T : struct
        {
            var value = GetString(obj, names);
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            var normalized = value.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(normalized, name.Replace("_", string.Empty), StringComparison.OrdinalIgnoreCase))
                {
                    return (T)Enum.Parse(typeof(T), name);
                }
            }

            return defaultValue;
        }
    }
}
