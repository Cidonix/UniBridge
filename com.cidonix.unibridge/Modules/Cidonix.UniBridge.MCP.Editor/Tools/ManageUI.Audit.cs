#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class ManageUI
    {
        static object Audit(ManageUIParams parameters)
        {
            var report = BuildAuditReport(parameters);
            if (!string.IsNullOrEmpty(report.Error))
            {
                return Response.Error(report.Error);
            }

            return Response.Success($"Audited {report.ScannedRects} UI RectTransform(s); found {report.Issues.Count} issue(s).", report.ToResponseData());
        }

        static object RepairPlan(ManageUIParams parameters)
        {
            var report = BuildAuditReport(parameters);
            if (!string.IsNullOrEmpty(report.Error))
            {
                return Response.Error(report.Error);
            }

            var mode = parameters.AutoFixMode;
            var requestedCodes = BuildRequestedFixCodeSet(parameters.FixCodes, mode);
            var proposals = report.Issues
                .Select(issue => BuildRepairProposal(issue, requestedCodes, mode))
                .ToArray();

            var autoFixable = proposals.Count(proposal => (bool)proposal["canAutoFix"]);
            var enabled = proposals.Count(proposal => (bool)proposal["enabledByMode"] && (bool)proposal["includedByCodeFilter"] && (bool)proposal["canAutoFix"]);
            return Response.Success(
                $"Built UI repair plan for {report.Issues.Count} issue(s); {enabled} fix(es) are enabled by AutoFixMode={mode}.",
                new
                {
                    mode = mode.ToString(),
                    requestedCodes = requestedCodes.OrderBy(code => code).ToArray(),
                    summary = new
                    {
                        issueCount = report.Issues.Count,
                        autoFixableCount = autoFixable,
                        enabledAutoFixCount = enabled,
                        blockedByModeCount = proposals.Count(proposal => (bool)proposal["canAutoFix"] && !(bool)proposal["enabledByMode"]),
                        unsupportedCount = proposals.Count(proposal => !(bool)proposal["canAutoFix"])
                    },
                    audit = report.ToResponseData(),
                    proposals
                });
        }

        static object AutoFix(ManageUIParams parameters)
        {
            var before = BuildAuditReport(parameters);
            if (!string.IsNullOrEmpty(before.Error))
            {
                return Response.Error(before.Error);
            }

            var dryRun = parameters.DryRun ?? false;
            var mode = parameters.AutoFixMode;
            var maxFixes = Mathf.Clamp(parameters.MaxFixes ?? 25, 1, 500);
            var requestedCodes = BuildRequestedFixCodeSet(parameters.FixCodes, mode);
            var fixes = new List<Dictionary<string, object>>();
            var skipped = new List<Dictionary<string, object>>();

            foreach (var issue in before.Issues)
            {
                if (fixes.Count >= maxFixes)
                {
                    skipped.Add(BuildSkippedFix(issue, "MaxFixes reached."));
                    continue;
                }

                var code = issue.TryGetValue("code", out var codeValue) ? codeValue as string : null;
                if (string.IsNullOrWhiteSpace(code) || !requestedCodes.Contains(code))
                {
                    skipped.Add(BuildSkippedFix(issue, "Issue code is not in the requested AutoFix set."));
                    continue;
                }

                if (!IsFixModeAllowed(code, mode))
                {
                    skipped.Add(BuildSkippedFix(issue, $"Issue code requires AutoFixMode={GetRequiredFixMode(code)?.ToString() ?? "Unsupported"}. Current mode is {mode}."));
                    continue;
                }

                var result = TryAutoFixIssue(issue, parameters, dryRun);
                if (result.Applied || result.WouldApply)
                {
                    fixes.Add(new Dictionary<string, object>
                    {
                        ["code"] = code,
                        ["mode"] = mode.ToString(),
                        ["dryRun"] = dryRun,
                        ["applied"] = result.Applied,
                        ["wouldApply"] = result.WouldApply,
                        ["message"] = result.Message,
                        ["target"] = issue.TryGetValue("target", out var target) ? target : null
                    });
                }
                else
                {
                    skipped.Add(BuildSkippedFix(issue, result.Message));
                }
            }

            if (!dryRun && fixes.Count > 0)
            {
                foreach (var root in before.Roots)
                {
                    MarkSceneDirty(root);
                }

                Canvas.ForceUpdateCanvases();
                SceneView.RepaintAll();
            }

            var after = dryRun ? null : BuildAuditReport(parameters);
            return Response.Success(
                dryRun
                    ? $"Dry run: {fixes.Count} UI issue(s) would be fixed."
                    : $"Applied {fixes.Count} UI fix(es).",
                new
                {
                    dryRun,
                    mode = mode.ToString(),
                    requestedCodes = requestedCodes.OrderBy(code => code).ToArray(),
                    before = before.ToResponseData(),
                    fixes,
                    skipped,
                    after = after?.ToResponseData()
                });
        }

        static UiAuditReport BuildAuditReport(ManageUIParams parameters)
        {
            var includeInactive = parameters.IncludeInactive ?? false;
            var maxIssues = Mathf.Clamp(parameters.MaxIssues ?? 100, 1, 1000);
            var tolerance = Mathf.Max(0f, parameters.AuditTolerance ?? 1f);
            var roots = ResolveAuditRoots(parameters.Target, includeInactive);
            if (roots.Count == 0)
            {
                return new UiAuditReport
                {
                    Error = "Audit requires a Target UI object, current UI selection, or at least one Canvas in the scene."
                };
            }

            Canvas.ForceUpdateCanvases();

            var report = new UiAuditReport
            {
                Roots = roots
            };

            foreach (var root in roots)
            {
                if (root == null)
                {
                    continue;
                }

                var rects = root.GetComponentsInChildren<RectTransform>(includeInactive)
                    .Where(rect => rect != null && IsEditableSceneObject(rect.gameObject))
                    .Where(rect => includeInactive || rect.gameObject.activeInHierarchy)
                    .Distinct()
                    .ToArray();

                report.RootSummaries.Add(new
                {
                    root = BuildGameObjectInfo(root),
                    rectCount = rects.Length,
                    canvas = BuildCanvasInfo(root.GetComponentInParent<Canvas>(true) ?? root.GetComponent<Canvas>())
                });

                report.ScannedRects += rects.Length;
                report.ScannedTexts += rects.Count(rect => rect.GetComponent<Text>() != null || HasTextMeshProText(rect.gameObject));
                report.ScannedButtons += rects.Count(rect => rect.GetComponent<Button>() != null);

                AuditCanvas(root, rects, report.Issues, maxIssues);
                AuditRects(rects, report.Issues, maxIssues, tolerance);
                AuditSiblingOverlap(rects, report.Issues, maxIssues, tolerance);

                if (report.Issues.Count >= maxIssues)
                {
                    report.Truncated = true;
                    break;
                }
            }

            return report;
        }



        static void AuditCanvas(GameObject root, RectTransform[] rects, List<Dictionary<string, object>> issues, int maxIssues)
        {
            var canvases = rects
                .Select(rect => rect.GetComponent<Canvas>())
                .Where(canvas => canvas != null)
                .Distinct()
                .ToArray();

            foreach (var canvas in canvases)
            {
                if (issues.Count >= maxIssues)
                    return;

                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    AddIssue(issues, maxIssues, "info", "SCREEN_SPACE_OVERLAY_CAPTURE_CAVEAT", canvas.gameObject,
                        "ScreenSpaceOverlay Canvas is visible in the Editor/Game View but is not included in Camera.RenderTexture captures. Use ScreenSpaceCamera when AI visual capture needs to see this UI.");
                }

                if (!canvas.enabled)
                {
                    AddIssue(issues, maxIssues, "warning", "DISABLED_CANVAS", canvas.gameObject,
                        "Canvas component is disabled, so this UI hierarchy will not render.");
                }

                if (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera == null)
                {
                    AddIssue(issues, maxIssues, "warning", "CAMERA_SPACE_CANVAS_MISSING_CAMERA", canvas.gameObject,
                        "ScreenSpaceCamera Canvas has no worldCamera assigned. It may not render in the expected camera capture or Game View.");
                }

                if (canvas.GetComponent<CanvasScaler>() == null)
                {
                    AddIssue(issues, maxIssues, "warning", "MISSING_CANVAS_SCALER", canvas.gameObject,
                        "Canvas has no CanvasScaler, so UI may not scale predictably across resolutions.");
                }

                if (canvas.GetComponent<GraphicRaycaster>() == null && rects.Any(rect => rect.GetComponent<Selectable>() != null))
                {
                    AddIssue(issues, maxIssues, "warning", "MISSING_GRAPHIC_RAYCASTER", canvas.gameObject,
                        "Canvas contains selectable UI but has no GraphicRaycaster.");
                }

                if ((canvas.isRootCanvas || canvas.overrideSorting)
                    && canvas.sortingOrder < 0
                    && CanvasHasVisibleUiContent(canvas))
                {
                    AddIssue(issues, maxIssues, "info", "LOW_CANVAS_SORTING_ORDER", canvas.gameObject,
                        "Canvas has a negative sorting order. This can be intentional, but AI-created HUDs and overlays usually need a non-negative order so they do not hide behind other canvases.");
                }
            }

            var sortedCanvases = canvases
                .Where(canvas => canvas != null && canvas.enabled && canvas.gameObject.activeInHierarchy)
                .Where(canvas => canvas.isRootCanvas || canvas.overrideSorting)
                .Where(CanvasHasVisibleUiContent)
                .GroupBy(canvas => $"{canvas.sortingLayerID}:{canvas.sortingOrder}")
                .Where(group => group.Count() > 1);

            foreach (var group in sortedCanvases)
            {
                if (issues.Count >= maxIssues)
                    return;

                var first = group.First();
                AddIssue(issues, maxIssues, "info", "CANVAS_SORTING_CONFLICT", first.gameObject,
                    $"Multiple active canvases share sorting layer {first.sortingLayerID} and order {first.sortingOrder}. Their visual stacking may depend on hierarchy/order instead of an explicit sorting contract.",
                    new Dictionary<string, object>
                    {
                        ["canvases"] = group.Select(canvas => new
                        {
                            name = canvas.name,
                            path = GetHierarchyPath(canvas.gameObject),
                            id = UnityApiAdapter.GetObjectId(canvas.gameObject)
                        }).ToArray()
                    });
            }

            if (rects.Any(rect => rect.GetComponent<Selectable>() != null) && FindEventSystem() == null)
            {
                AddIssue(issues, maxIssues, "warning", "MISSING_EVENT_SYSTEM", root,
                    "Selectable UI exists but no EventSystem was found in the scene.");
            }
        }

        static bool CanvasHasVisibleUiContent(Canvas canvas)
        {
            if (canvas == null || !canvas.enabled || !canvas.gameObject.activeInHierarchy)
            {
                return false;
            }

            return canvas.GetComponentsInChildren<Graphic>(false).Any(IsPotentiallyVisibleGraphic)
                   || canvas.GetComponentsInChildren<Selectable>(false).Any(selectable => selectable != null && selectable.gameObject.activeInHierarchy);
        }

        static bool IsPotentiallyVisibleGraphic(Graphic graphic)
        {
            if (graphic == null || !graphic.enabled || !graphic.gameObject.activeInHierarchy)
            {
                return false;
            }

            var rendererAlpha = graphic.canvasRenderer != null ? graphic.canvasRenderer.GetAlpha() : 1f;
            return graphic.color.a > 0.01f && rendererAlpha > 0.01f && CalculateCanvasGroupAlpha(graphic.gameObject) > 0.01f;
        }

        static void AuditRects(RectTransform[] rects, List<Dictionary<string, object>> issues, int maxIssues, float tolerance)
        {
            foreach (var rect in rects)
            {
                if (issues.Count >= maxIssues)
                    return;

                AuditVisibility(rect, issues, maxIssues);

                if (rect.rect.width <= tolerance || rect.rect.height <= tolerance)
                {
                    AddIssue(issues, maxIssues, "warning", "ZERO_OR_TINY_RECT", rect.gameObject,
                        $"RectTransform is very small ({rect.rect.width:0.##} x {rect.rect.height:0.##}). It may be invisible or impossible to interact with.");
                }

                var layoutGroups = rect.GetComponents<LayoutGroup>();
                if (layoutGroups.Length > 1)
                {
                    AddIssue(issues, maxIssues, "warning", "MULTIPLE_LAYOUT_GROUPS", rect.gameObject,
                        $"Object has {layoutGroups.Length} LayoutGroup components. Usually only one layout group should control a container.");
                }

                if (rect.childCount >= 4 && layoutGroups.Length == 0 && rect.GetComponent<ContentSizeFitter>() == null)
                {
                    var uiChildCount = rect.Cast<Transform>().OfType<RectTransform>().Count(child => child.GetComponent<Graphic>() != null || child.GetComponentsInChildren<Graphic>(true).Length > 0);
                    if (uiChildCount >= 4)
                    {
                        AddIssue(issues, maxIssues, "info", "MANUAL_CONTAINER_LAYOUT", rect.gameObject,
                            "Container has several UI children but no LayoutGroup. A Horizontal, Vertical, or Grid layout group may make it more robust.");
                    }
                }

                if (rect.parent is RectTransform parentRect
                    && !IsScrollRectContent(rect, parentRect)
                    && IsOutsideParent(rect, parentRect, tolerance))
                {
                    AddIssue(issues, maxIssues, "warning", "CHILD_OUTSIDE_PARENT", rect.gameObject,
                        $"RectTransform extends outside parent '{parentRect.name}'. This may be intentional, but can indicate broken anchors or offsets.");
                }

                var text = rect.GetComponent<Text>();
                if (text != null)
                {
                    AuditText(rect, text, issues, maxIssues, tolerance);
                }

                var textMeshPro = GetTextMeshProText(rect.gameObject);
                if (textMeshPro != null)
                {
                    AuditTextMeshPro(rect, textMeshPro, issues, maxIssues, tolerance);
                }
            }
        }

        static void AuditVisibility(RectTransform rect, List<Dictionary<string, object>> issues, int maxIssues)
        {
            if (rect == null || issues.Count >= maxIssues)
            {
                return;
            }

            var hasInteractiveOrTextUi = HasInteractiveOrTextUi(rect);
            var hasGraphic = rect.GetComponent<Graphic>() != null;
            var hasUiSurface = hasInteractiveOrTextUi || hasGraphic || rect.GetComponentsInChildren<Graphic>(true).Length > 0;

            if (!rect.gameObject.activeSelf || !rect.gameObject.activeInHierarchy)
            {
                AddIssue(issues, maxIssues, "info", "INACTIVE_UI_ELEMENT", rect.gameObject,
                    "UI object is inactive. IncludeInactive=true was used, so this may be intentional.");
                if (issues.Count >= maxIssues)
                    return;
            }

            var scale = rect.localScale;
            if (Mathf.Abs(scale.x) <= 0.001f || Mathf.Abs(scale.y) <= 0.001f)
            {
                AddIssue(issues, maxIssues, "warning", "ZERO_SCALE", rect.gameObject,
                    "RectTransform local scale is effectively zero on X or Y, so it will be invisible or impossible to interact with.",
                    new Dictionary<string, object>
                    {
                        ["localScale"] = ToArray(scale)
                    });
                if (issues.Count >= maxIssues)
                    return;
            }

            var graphic = rect.GetComponent<Graphic>();
            if (graphic != null)
            {
                if (!graphic.enabled)
                {
                    AddIssue(issues, maxIssues, hasInteractiveOrTextUi ? "warning" : "info", "DISABLED_GRAPHIC", rect.gameObject,
                        "Graphic component is disabled, so this UI element will not render.");
                    if (issues.Count >= maxIssues)
                        return;
                }

                if (graphic.color.a <= 0.01f)
                {
                    AddIssue(issues, maxIssues, hasInteractiveOrTextUi ? "warning" : "info", "INVISIBLE_GRAPHIC_ALPHA", rect.gameObject,
                        "Graphic color alpha is effectively transparent.",
                        new Dictionary<string, object>
                        {
                            ["alpha"] = graphic.color.a
                        });
                    if (issues.Count >= maxIssues)
                        return;
                }

                var rendererAlpha = graphic.canvasRenderer != null ? graphic.canvasRenderer.GetAlpha() : 1f;
                if (rendererAlpha <= 0.01f)
                {
                    AddIssue(issues, maxIssues, hasInteractiveOrTextUi ? "warning" : "info", "INVISIBLE_CANVAS_RENDERER_ALPHA", rect.gameObject,
                        "CanvasRenderer alpha is effectively transparent.",
                        new Dictionary<string, object>
                        {
                            ["alpha"] = rendererAlpha
                        });
                    if (issues.Count >= maxIssues)
                        return;
                }
            }

            if (hasUiSurface)
            {
                var canvasGroupAlpha = CalculateCanvasGroupAlpha(rect.gameObject);
                if (canvasGroupAlpha <= 0.01f)
                {
                    AddIssue(issues, maxIssues, hasInteractiveOrTextUi ? "warning" : "info", "UI_HIDDEN_BY_CANVAS_GROUP", rect.gameObject,
                        "A CanvasGroup on this object or an ancestor makes this UI effectively transparent.",
                        new Dictionary<string, object>
                        {
                            ["cumulativeAlpha"] = canvasGroupAlpha
                        });
                }
            }
        }

        static bool HasInteractiveOrTextUi(RectTransform rect)
        {
            return rect != null
                   && (rect.GetComponent<Selectable>() != null
                       || rect.GetComponent<Text>() != null
                       || HasTextMeshProText(rect.gameObject));
        }

        static float CalculateCanvasGroupAlpha(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return 1f;
            }

            var alpha = 1f;
            foreach (var group in gameObject.GetComponentsInParent<CanvasGroup>(true))
            {
                if (group == null || !group.enabled)
                {
                    continue;
                }

                alpha *= Mathf.Clamp01(group.alpha);
                if (group.ignoreParentGroups)
                {
                    break;
                }
            }

            return alpha;
        }

        static void AuditText(RectTransform rect, Text text, List<Dictionary<string, object>> issues, int maxIssues, float tolerance)
        {
            if (string.IsNullOrEmpty(text.text))
            {
                return;
            }

            var widthOverflow = text.preferredWidth - rect.rect.width;
            var heightOverflow = text.preferredHeight - rect.rect.height;
            if (widthOverflow > tolerance || heightOverflow > tolerance)
            {
                AddIssue(issues, maxIssues, text.resizeTextForBestFit ? "info" : "warning", "TEXT_OVERFLOW_RISK", rect.gameObject,
                    $"Text preferred size ({text.preferredWidth:0.##} x {text.preferredHeight:0.##}) exceeds rect ({rect.rect.width:0.##} x {rect.rect.height:0.##}).",
                    new Dictionary<string, object>
                    {
                        ["textPreview"] = Truncate(text.text, 80),
                        ["preferredWidth"] = text.preferredWidth,
                        ["preferredHeight"] = text.preferredHeight,
                        ["rectWidth"] = rect.rect.width,
                        ["rectHeight"] = rect.rect.height,
                        ["bestFit"] = text.resizeTextForBestFit
                    });
            }

            if (text.fontSize < 9)
            {
                AddIssue(issues, maxIssues, "info", "VERY_SMALL_TEXT", rect.gameObject,
                    $"Text font size is {text.fontSize}; it may be hard to read in captures.");
            }
        }

        static void AuditTextMeshPro(RectTransform rect, Component textMeshPro, List<Dictionary<string, object>> issues, int maxIssues, float tolerance)
        {
            var text = GetMemberValue(textMeshPro, "text") as string;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            ForceTextMeshProMeshUpdate(textMeshPro);

            var preferredWidth = ReadFloatMember(textMeshPro, "preferredWidth");
            var preferredHeight = ReadFloatMember(textMeshPro, "preferredHeight");
            var fontSize = ReadFloatMember(textMeshPro, "fontSize");
            var autoSizing = ReadBoolMember(textMeshPro, "enableAutoSizing");
            var overflowMode = GetMemberValue(textMeshPro, "overflowMode")?.ToString();
            var widthOverflow = preferredWidth - rect.rect.width;
            var heightOverflow = preferredHeight - rect.rect.height;

            if (widthOverflow > tolerance || heightOverflow > tolerance || IsTextMeshProOverflowing(textMeshPro))
            {
                AddIssue(issues, maxIssues, autoSizing ? "info" : "warning", "TEXT_OVERFLOW_RISK", rect.gameObject,
                    $"TextMesh Pro preferred size ({preferredWidth:0.##} x {preferredHeight:0.##}) exceeds rect ({rect.rect.width:0.##} x {rect.rect.height:0.##}) or reports overflow.",
                    new Dictionary<string, object>
                    {
                        ["textSystem"] = "TextMeshProUGUI",
                        ["textPreview"] = Truncate(text, 80),
                        ["preferredWidth"] = preferredWidth,
                        ["preferredHeight"] = preferredHeight,
                        ["rectWidth"] = rect.rect.width,
                        ["rectHeight"] = rect.rect.height,
                        ["autoSizing"] = autoSizing,
                        ["overflowMode"] = overflowMode,
                        ["isOverflowing"] = IsTextMeshProOverflowing(textMeshPro)
                    });
            }

            if (GetMemberValue(textMeshPro, "font") == null)
            {
                AddIssue(issues, maxIssues, "warning", "TMP_MISSING_FONT_ASSET", rect.gameObject,
                    "TextMesh Pro text has no font asset assigned. It may render incorrectly in a fresh project or build.");
            }

            if (fontSize > 0f && fontSize < 9f)
            {
                AddIssue(issues, maxIssues, "info", "VERY_SMALL_TEXT", rect.gameObject,
                    $"TextMesh Pro font size is {fontSize:0.##}; it may be hard to read in captures.");
            }
        }

        static void AuditSiblingOverlap(RectTransform[] rects, List<Dictionary<string, object>> issues, int maxIssues, float tolerance)
        {
            var groups = rects
                .Where(rect => rect.parent is RectTransform)
                .GroupBy(rect => rect.parent)
                .ToArray();

            foreach (var group in groups)
            {
                var parent = group.Key as RectTransform;
                if (parent == null)
                {
                    continue;
                }

                var siblings = group
                    .Where(IsVisibleUiLeafOrControl)
                    .Select(rect => new { rect, aabb = GetLocalAabbInParent(rect, parent) })
                    .Where(item => item.aabb.width > tolerance && item.aabb.height > tolerance)
                    .ToArray();

                for (var i = 0; i < siblings.Length; i++)
                {
                    for (var j = i + 1; j < siblings.Length; j++)
                    {
                        if (issues.Count >= maxIssues)
                            return;

                        var overlap = RectOverlap(siblings[i].aabb, siblings[j].aabb);
                        if (overlap.width <= tolerance || overlap.height <= tolerance)
                            continue;

                        var smallerArea = Mathf.Min(siblings[i].aabb.width * siblings[i].aabb.height, siblings[j].aabb.width * siblings[j].aabb.height);
                        var overlapArea = overlap.width * overlap.height;
                        if (smallerArea <= 0f || overlapArea / smallerArea < 0.2f)
                            continue;

                        AddIssue(issues, maxIssues, "warning", "SIBLING_OVERLAP", siblings[i].rect.gameObject,
                            $"Sibling UI objects '{siblings[i].rect.name}' and '{siblings[j].rect.name}' overlap significantly.",
                            new Dictionary<string, object>
                            {
                                ["other"] = BuildGameObjectInfo(siblings[j].rect.gameObject),
                                ["otherPath"] = GetHierarchyPath(siblings[j].rect.gameObject),
                                ["overlapArea"] = overlapArea,
                                ["overlapRect"] = new { overlap.xMin, overlap.yMin, overlap.xMax, overlap.yMax, overlap.width, overlap.height }
                            });
                    }
                }
            }
        }

        static bool IsVisibleUiLeafOrControl(RectTransform rect)
        {
            if (rect == null || !rect.gameObject.activeInHierarchy)
            {
                return false;
            }

            if (rect.GetComponent<Selectable>() != null || rect.GetComponent<Text>() != null || HasTextMeshProText(rect.gameObject))
            {
                return true;
            }

            var image = rect.GetComponent<Image>();
            return image != null && rect.childCount == 0;
        }

        static bool IsOutsideParent(RectTransform rect, RectTransform parentRect, float tolerance)
        {
            var childAabb = GetLocalAabbInParent(rect, parentRect);
            var parentAabb = parentRect.rect;
            return childAabb.xMin < parentAabb.xMin - tolerance
                   || childAabb.xMax > parentAabb.xMax + tolerance
                   || childAabb.yMin < parentAabb.yMin - tolerance
                   || childAabb.yMax > parentAabb.yMax + tolerance;
        }

        static bool IsScrollRectContent(RectTransform rect, RectTransform parentRect)
        {
            if (rect == null || parentRect == null)
            {
                return false;
            }

            var scrollRect = parentRect.GetComponentInParent<ScrollRect>();
            return scrollRect != null && scrollRect.content == rect;
        }

        static Rect GetWorldAabb(RectTransform rectTransform)
        {
            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            var minX = corners.Min(corner => corner.x);
            var maxX = corners.Max(corner => corner.x);
            var minY = corners.Min(corner => corner.y);
            var maxY = corners.Max(corner => corner.y);
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        static Rect RectOverlap(Rect a, Rect b)
        {
            var minX = Mathf.Max(a.xMin, b.xMin);
            var minY = Mathf.Max(a.yMin, b.yMin);
            var maxX = Mathf.Min(a.xMax, b.xMax);
            var maxY = Mathf.Min(a.yMax, b.yMax);
            if (maxX <= minX || maxY <= minY)
            {
                return Rect.zero;
            }

            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        static void AddIssue(
            List<Dictionary<string, object>> issues,
            int maxIssues,
            string severity,
            string code,
            GameObject target,
            string message,
            Dictionary<string, object> details = null)
        {
            if (issues.Count >= maxIssues)
            {
                return;
            }

            var issue = new Dictionary<string, object>
            {
                ["severity"] = severity,
                ["code"] = code,
                ["message"] = message,
                ["targetPath"] = GetHierarchyPath(target),
                ["target"] = BuildGameObjectInfo(target)
            };

            if (details != null && details.Count > 0)
            {
                issue["details"] = details;
            }

            issues.Add(issue);
        }

        static Dictionary<string, object> BuildRepairProposal(Dictionary<string, object> issue, HashSet<string> requestedCodes, UIAutoFixMode mode)
        {
            var code = issue.TryGetValue("code", out var codeValue) ? codeValue as string : null;
            var target = issue.TryGetValue("target", out var targetValue) ? targetValue : null;
            var severity = issue.TryGetValue("severity", out var severityValue) ? severityValue as string : null;
            var message = issue.TryGetValue("message", out var messageValue) ? messageValue as string : null;
            var requiredMode = GetRequiredFixMode(code);
            var canAutoFix = requiredMode.HasValue;
            var includedByCodeFilter = !string.IsNullOrWhiteSpace(code) && requestedCodes.Contains(code);
            var enabledByMode = canAutoFix && IsFixModeAllowed(code, mode);

            var proposal = new Dictionary<string, object>
            {
                ["code"] = code,
                ["severity"] = severity,
                ["target"] = target,
                ["message"] = message,
                ["canAutoFix"] = canAutoFix,
                ["includedByCodeFilter"] = includedByCodeFilter,
                ["enabledByMode"] = enabledByMode,
                ["requiredMode"] = requiredMode?.ToString(),
                ["risk"] = BuildFixRisk(code),
                ["recommendedAction"] = BuildFixRecommendation(code)
            };

            if (canAutoFix)
            {
                proposal["autoFixCommand"] = new
                {
                    Action = UIAction.AutoFix.ToString(),
                    AutoFixMode = requiredMode.Value.ToString(),
                    FixCodes = new[] { code },
                    DryRun = true
                };
            }

            return proposal;
        }

        static string BuildFixRisk(string code)
        {
            return (code ?? string.Empty).ToUpperInvariant() switch
            {
                "MISSING_CANVAS_SCALER" => "low",
                "MISSING_GRAPHIC_RAYCASTER" => "low",
                "MISSING_EVENT_SYSTEM" => "low",
                "TEXT_OVERFLOW_RISK" => "low",
                "DISABLED_CANVAS" => "manual",
                "CAMERA_SPACE_CANVAS_MISSING_CAMERA" => "manual",
                "LOW_CANVAS_SORTING_ORDER" => "manual",
                "CANVAS_SORTING_CONFLICT" => "manual",
                "INACTIVE_UI_ELEMENT" => "manual",
                "ZERO_SCALE" => "manual",
                "DISABLED_GRAPHIC" => "manual",
                "INVISIBLE_GRAPHIC_ALPHA" => "manual",
                "INVISIBLE_CANVAS_RENDERER_ALPHA" => "manual",
                "UI_HIDDEN_BY_CANVAS_GROUP" => "manual",
                "MULTIPLE_LAYOUT_GROUPS" => "medium",
                "CHILD_OUTSIDE_PARENT" => "medium",
                "SIBLING_OVERLAP" => "medium",
                "MANUAL_CONTAINER_LAYOUT" => "high",
                "TMP_MISSING_FONT_ASSET" => "manual",
                _ => "manual"
            };
        }

        static string BuildFixRecommendation(string code)
        {
            return (code ?? string.Empty).ToUpperInvariant() switch
            {
                "MISSING_CANVAS_SCALER" => "Add a CanvasScaler with ScaleWithScreenSize 1920x1080.",
                "MISSING_GRAPHIC_RAYCASTER" => "Add a GraphicRaycaster so selectable UI can receive events.",
                "MISSING_EVENT_SYSTEM" => "Create or repair the scene EventSystem with a compatible input module.",
                "TEXT_OVERFLOW_RISK" => "Enable legacy Text BestFit or TextMesh Pro auto sizing as a fallback; prefer LayoutElement/LayoutGroup for final UI.",
                "DISABLED_CANVAS" => "Manual decision: enable the Canvas or keep it disabled if this is a hidden/inactive screen.",
                "CAMERA_SPACE_CANVAS_MISSING_CAMERA" => "Manual decision: assign the intended UI camera or switch the Canvas render mode.",
                "LOW_CANVAS_SORTING_ORDER" => "Manual decision: raise sortingOrder for visible HUD/overlay canvases that should render above other UI.",
                "CANVAS_SORTING_CONFLICT" => "Manual decision: give each root/override-sorted Canvas an explicit sorting order for deterministic stacking.",
                "INACTIVE_UI_ELEMENT" => "Manual decision: activate the UI element or exclude inactive objects from validation.",
                "ZERO_SCALE" => "Manual decision: set local scale back to a visible value, usually [1,1,1].",
                "DISABLED_GRAPHIC" => "Manual decision: enable the Graphic component or remove the unused visual element.",
                "INVISIBLE_GRAPHIC_ALPHA" => "Manual decision: raise the Graphic alpha or keep it transparent if it is an intentional hit area/layout helper.",
                "INVISIBLE_CANVAS_RENDERER_ALPHA" => "Manual decision: reset CanvasRenderer alpha or keep it transparent if intentionally hidden by animation.",
                "UI_HIDDEN_BY_CANVAS_GROUP" => "Manual decision: raise the CanvasGroup alpha or keep it hidden if this is an inactive panel state.",
                "MULTIPLE_LAYOUT_GROUPS" => "Keep one LayoutGroup component and remove duplicate layout controllers.",
                "CHILD_OUTSIDE_PARENT" => "Move the child RectTransform back inside its parent bounds.",
                "SIBLING_OVERLAP" => "Move one sibling along the smaller overlap axis to separate controls.",
                "MANUAL_CONTAINER_LAYOUT" => "Convert the container to an inferred Horizontal, Vertical, or Grid LayoutGroup and preserve child sizes with LayoutElement.",
                "TMP_MISSING_FONT_ASSET" => "Assign a valid TMP_FontAsset via FontAssetPath or the Unity Inspector.",
                "SCREEN_SPACE_OVERLAY_CAPTURE_CAVEAT" => "Manual decision: switch to ScreenSpaceCamera only when AI camera captures must see this UI.",
                "ZERO_OR_TINY_RECT" => "Manual decision: give the RectTransform a meaningful size.",
                "VERY_SMALL_TEXT" => "Manual decision: increase font size or adjust layout.",
                _ => "No automatic repair is available."
            };
        }

        static HashSet<string> BuildRequestedFixCodeSet(string[] fixCodes, UIAutoFixMode mode)
        {
            var defaults = GetDefaultFixCodes(mode);

            var source = fixCodes == null || fixCodes.Length == 0
                ? defaults
                : fixCodes;

            return source
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim().ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        static string[] GetDefaultFixCodes(UIAutoFixMode mode)
        {
            var codes = new List<string>
            {
                "MISSING_CANVAS_SCALER",
                "MISSING_GRAPHIC_RAYCASTER",
                "MISSING_EVENT_SYSTEM",
                "TEXT_OVERFLOW_RISK"
            };

            if (mode >= UIAutoFixMode.Layout)
            {
                codes.Add("MULTIPLE_LAYOUT_GROUPS");
                codes.Add("CHILD_OUTSIDE_PARENT");
                codes.Add("SIBLING_OVERLAP");
            }

            if (mode >= UIAutoFixMode.Aggressive)
            {
                codes.Add("MANUAL_CONTAINER_LAYOUT");
            }

            return codes.ToArray();
        }

        static UIAutoFixMode? GetRequiredFixMode(string code)
        {
            return (code ?? string.Empty).ToUpperInvariant() switch
            {
                "MISSING_CANVAS_SCALER" => UIAutoFixMode.Safe,
                "MISSING_GRAPHIC_RAYCASTER" => UIAutoFixMode.Safe,
                "MISSING_EVENT_SYSTEM" => UIAutoFixMode.Safe,
                "TEXT_OVERFLOW_RISK" => UIAutoFixMode.Safe,
                "MULTIPLE_LAYOUT_GROUPS" => UIAutoFixMode.Layout,
                "CHILD_OUTSIDE_PARENT" => UIAutoFixMode.Layout,
                "SIBLING_OVERLAP" => UIAutoFixMode.Layout,
                "MANUAL_CONTAINER_LAYOUT" => UIAutoFixMode.Aggressive,
                _ => null
            };
        }

        static bool IsFixModeAllowed(string code, UIAutoFixMode mode)
        {
            var requiredMode = GetRequiredFixMode(code);
            return requiredMode.HasValue && mode >= requiredMode.Value;
        }

        static Dictionary<string, object> BuildSkippedFix(Dictionary<string, object> issue, string reason)
        {
            return new Dictionary<string, object>
            {
                ["code"] = issue.TryGetValue("code", out var code) ? code : null,
                ["reason"] = reason,
                ["target"] = issue.TryGetValue("target", out var target) ? target : null
            };
        }

        static AutoFixResult TryAutoFixIssue(Dictionary<string, object> issue, ManageUIParams parameters, bool dryRun)
        {
            var code = issue.TryGetValue("code", out var codeValue) ? codeValue as string : null;
            var targetPath = issue.TryGetValue("targetPath", out var targetPathValue) ? targetPathValue as string : null;
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(targetPath))
            {
                return AutoFixResult.Skip("Issue has no resolvable target.");
            }

            var target = ResolveTargetGameObject(targetPath);
            if (target == null)
            {
                return AutoFixResult.Skip($"Target '{targetPath}' was not found.");
            }

            switch (code.ToUpperInvariant())
            {
                case "MISSING_CANVAS_SCALER":
                    return AutoFixMissingCanvasScaler(target, dryRun);
                case "MISSING_GRAPHIC_RAYCASTER":
                    return AutoFixMissingGraphicRaycaster(target, dryRun);
                case "MISSING_EVENT_SYSTEM":
                    return AutoFixMissingEventSystem(dryRun);
                case "TEXT_OVERFLOW_RISK":
                    return AutoFixTextOverflowRisk(target, dryRun);
                case "MULTIPLE_LAYOUT_GROUPS":
                    return AutoFixMultipleLayoutGroups(target, dryRun);
                case "CHILD_OUTSIDE_PARENT":
                    return AutoFixChildOutsideParent(target, dryRun);
                case "SIBLING_OVERLAP":
                    return AutoFixSiblingOverlap(target, issue, dryRun);
                case "MANUAL_CONTAINER_LAYOUT":
                    return AutoFixManualContainerLayout(target, parameters, dryRun);
                default:
                    return AutoFixResult.Skip($"AutoFix does not support issue code '{code}'.");
            }
        }

        static AutoFixResult AutoFixMissingCanvasScaler(GameObject target, bool dryRun)
        {
            var canvas = target.GetComponent<Canvas>();
            if (canvas == null)
            {
                return AutoFixResult.Skip("Target is not a Canvas.");
            }

            if (target.GetComponent<CanvasScaler>() != null)
            {
                return AutoFixResult.Skip("CanvasScaler already exists.");
            }

            if (dryRun)
            {
                return AutoFixResult.Would("Would add CanvasScaler with ScaleWithScreenSize 1920x1080.");
            }

            var scaler = Undo.AddComponent<CanvasScaler>(target);
            ConfigureDefaultCanvasScaler(scaler);
            EditorUtility.SetDirty(scaler);
            MarkSceneDirty(target);
            return AutoFixResult.Done("Added CanvasScaler with ScaleWithScreenSize 1920x1080.");
        }

        static AutoFixResult AutoFixMissingGraphicRaycaster(GameObject target, bool dryRun)
        {
            var canvas = target.GetComponent<Canvas>();
            if (canvas == null)
            {
                return AutoFixResult.Skip("Target is not a Canvas.");
            }

            if (target.GetComponent<GraphicRaycaster>() != null)
            {
                return AutoFixResult.Skip("GraphicRaycaster already exists.");
            }

            if (dryRun)
            {
                return AutoFixResult.Would("Would add GraphicRaycaster.");
            }

            var raycaster = Undo.AddComponent<GraphicRaycaster>(target);
            EditorUtility.SetDirty(raycaster);
            MarkSceneDirty(target);
            return AutoFixResult.Done("Added GraphicRaycaster.");
        }

        static AutoFixResult AutoFixMissingEventSystem(bool dryRun)
        {
            if (FindEventSystem() != null)
            {
                return AutoFixResult.Skip("EventSystem already exists.");
            }

            if (dryRun)
            {
                return AutoFixResult.Would("Would create EventSystem with a compatible UI input module.");
            }

            EnsureEventSystemObject();
            return AutoFixResult.Done("Created EventSystem with a compatible UI input module.");
        }

        static AutoFixResult AutoFixTextOverflowRisk(GameObject target, bool dryRun)
        {
            var text = target.GetComponent<Text>();
            if (text != null)
            {
                if (text.resizeTextForBestFit)
                {
                    return AutoFixResult.Skip("BestFit is already enabled.");
                }

                var minSize = Mathf.Clamp(Mathf.Min(12, text.fontSize), 1, Mathf.Max(1, text.fontSize));
                var maxSize = Mathf.Max(text.fontSize, minSize);
                if (dryRun)
                {
                    return AutoFixResult.Would($"Would enable Text BestFit with min={minSize}, max={maxSize}.");
                }

                Undo.RecordObject(text, "AutoFix UniBridge Text BestFit");
                text.resizeTextForBestFit = true;
                text.resizeTextMinSize = minSize;
                text.resizeTextMaxSize = maxSize;
                EditorUtility.SetDirty(text);
                MarkSceneDirty(target);
                return AutoFixResult.Done($"Enabled Text BestFit with min={minSize}, max={maxSize}.");
            }

            var textMeshPro = GetTextMeshProText(target);
            if (textMeshPro == null)
            {
                return AutoFixResult.Skip("Target has no legacy Text or TextMesh Pro text component.");
            }

            if (ReadBoolMember(textMeshPro, "enableAutoSizing"))
            {
                return AutoFixResult.Skip("TextMesh Pro auto sizing is already enabled.");
            }

            var fontSize = Mathf.Max(1f, ReadFloatMember(textMeshPro, "fontSize"));
            var tmpMinSize = Mathf.Clamp(Mathf.Min(12f, fontSize), 1f, fontSize);
            var tmpMaxSize = Mathf.Max(fontSize, tmpMinSize);
            if (dryRun)
            {
                return AutoFixResult.Would($"Would enable TextMesh Pro auto sizing with min={tmpMinSize:0.##}, max={tmpMaxSize:0.##}.");
            }

            Undo.RecordObject(textMeshPro, "AutoFix UniBridge TextMesh Pro Auto Size");
            SetMemberValue(textMeshPro, "enableAutoSizing", true);
            SetMemberValue(textMeshPro, "fontSizeMin", tmpMinSize);
            SetMemberValue(textMeshPro, "fontSizeMax", tmpMaxSize);
            EditorUtility.SetDirty(textMeshPro);
            MarkSceneDirty(target);
            return AutoFixResult.Done($"Enabled TextMesh Pro auto sizing with min={tmpMinSize:0.##}, max={tmpMaxSize:0.##}.");
        }

        static AutoFixResult AutoFixMultipleLayoutGroups(GameObject target, bool dryRun)
        {
            var groups = target.GetComponents<LayoutGroup>();
            if (groups.Length <= 1)
            {
                return AutoFixResult.Skip("Target does not have duplicate LayoutGroup components.");
            }

            var keep = groups[0];
            var removeCount = groups.Length - 1;
            if (dryRun)
            {
                return AutoFixResult.Would($"Would keep {keep.GetType().Name} and remove {removeCount} duplicate LayoutGroup component(s).");
            }

            Undo.RecordObject(target, "AutoFix duplicate UniBridge LayoutGroups");
            for (var i = 1; i < groups.Length; i++)
            {
                Undo.DestroyObjectImmediate(groups[i]);
            }

            MarkSceneDirty(target);
            LayoutRebuilder.MarkLayoutForRebuild(target.GetComponent<RectTransform>());
            return AutoFixResult.Done($"Kept {keep.GetType().Name} and removed {removeCount} duplicate LayoutGroup component(s).");
        }

        static AutoFixResult AutoFixChildOutsideParent(GameObject target, bool dryRun)
        {
            var rect = target.GetComponent<RectTransform>();
            if (rect == null || rect.parent is not RectTransform parentRect)
            {
                return AutoFixResult.Skip("Target has no RectTransform parent.");
            }

            var delta = ComputeContainmentDelta(rect, parentRect, 2f);
            if (delta.sqrMagnitude <= 0.0001f)
            {
                return AutoFixResult.Skip("Target is already inside parent bounds.");
            }

            if (dryRun)
            {
                return AutoFixResult.Would($"Would move child by local delta ({delta.x:0.##}, {delta.y:0.##}) to fit inside parent '{parentRect.name}'.");
            }

            Undo.RecordObject(rect, "AutoFix UniBridge child inside parent");
            rect.localPosition += new Vector3(delta.x, delta.y, 0f);
            EditorUtility.SetDirty(rect);
            MarkSceneDirty(target);
            LayoutRebuilder.MarkLayoutForRebuild(parentRect);
            return AutoFixResult.Done($"Moved child by local delta ({delta.x:0.##}, {delta.y:0.##}) to fit inside parent '{parentRect.name}'.");
        }

        static AutoFixResult AutoFixSiblingOverlap(GameObject target, Dictionary<string, object> issue, bool dryRun)
        {
            var rect = target.GetComponent<RectTransform>();
            var otherPath = GetIssueDetailString(issue, "otherPath");
            var other = ResolveTargetGameObject(otherPath)?.GetComponent<RectTransform>();
            if (rect == null || other == null || rect.parent == null || rect.parent != other.parent)
            {
                return AutoFixResult.Skip("Overlap pair could not be resolved as sibling RectTransforms.");
            }

            var parentRect = rect.parent as RectTransform;
            var rectBounds = GetLocalAabbInParent(rect, parentRect);
            var otherBounds = GetLocalAabbInParent(other, parentRect);
            var overlap = RectOverlap(rectBounds, otherBounds);
            if (overlap.width <= 0f || overlap.height <= 0f)
            {
                return AutoFixResult.Skip("Siblings no longer overlap.");
            }

            const float padding = 8f;
            var delta = overlap.width <= overlap.height
                ? new Vector2(rectBounds.center.x <= otherBounds.center.x ? -(overlap.width + padding) : overlap.width + padding, 0f)
                : new Vector2(0f, rectBounds.center.y <= otherBounds.center.y ? -(overlap.height + padding) : overlap.height + padding);

            if (parentRect != null)
            {
                delta += ComputeContainmentDeltaAfterMove(rect, parentRect, delta, 2f);
            }

            if (delta.sqrMagnitude <= 0.0001f)
            {
                return AutoFixResult.Skip("No safe movement delta could be computed for overlap.");
            }

            if (dryRun)
            {
                return AutoFixResult.Would($"Would move '{rect.name}' by local delta ({delta.x:0.##}, {delta.y:0.##}) away from '{other.name}'.");
            }

            Undo.RecordObject(rect, "AutoFix UniBridge sibling overlap");
            rect.localPosition += new Vector3(delta.x, delta.y, 0f);
            EditorUtility.SetDirty(rect);
            MarkSceneDirty(target);
            if (parentRect != null)
            {
                LayoutRebuilder.MarkLayoutForRebuild(parentRect);
            }
            return AutoFixResult.Done($"Moved '{rect.name}' by local delta ({delta.x:0.##}, {delta.y:0.##}) away from '{other.name}'.");
        }

        static AutoFixResult AutoFixManualContainerLayout(GameObject target, ManageUIParams parameters, bool dryRun)
        {
            var rect = target.GetComponent<RectTransform>();
            if (rect == null)
            {
                return AutoFixResult.Skip("Target has no RectTransform.");
            }

            if (target.GetComponents<LayoutGroup>().Length > 0)
            {
                return AutoFixResult.Skip("Target already has a LayoutGroup.");
            }

            var children = GetDirectUiLayoutChildren(rect).ToArray();
            if (children.Length < 2)
            {
                return AutoFixResult.Skip("Target does not have enough direct UI children for a LayoutGroup.");
            }

            var inferredType = InferLayoutGroupType(rect, children);
            var childSizes = children.ToDictionary(
                child => child,
                child => new Vector2(
                    Mathf.Max(1f, Mathf.Abs(child.rect.width)),
                    Mathf.Max(1f, Mathf.Abs(child.rect.height))));
            if (dryRun)
            {
                return AutoFixResult.Would($"Would add inferred {inferredType} LayoutGroup and preserve {children.Length} child size(s) with LayoutElement.");
            }

            var componentType = GetLayoutGroupComponentType(inferredType);
            var group = (LayoutGroup)Undo.AddComponent(target, componentType);
            ConfigureInferredLayoutGroup(group, childSizes.Values.ToArray());

            foreach (var child in children)
            {
                PreserveChildLayoutSize(child, childSizes[child]);
            }

            EditorUtility.SetDirty(group);
            MarkSceneDirty(target);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            return AutoFixResult.Done($"Added inferred {inferredType} LayoutGroup and preserved {children.Length} child size(s) with LayoutElement.");
        }

        static string GetIssueDetailString(Dictionary<string, object> issue, string key)
        {
            if (issue == null || !issue.TryGetValue("details", out var detailsValue))
            {
                return null;
            }

            if (detailsValue is Dictionary<string, object> details && details.TryGetValue(key, out var value))
            {
                return value as string;
            }

            return null;
        }

        static Rect GetLocalAabbInParent(RectTransform rect, RectTransform parent)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            for (var i = 0; i < corners.Length; i++)
            {
                corners[i] = parent.InverseTransformPoint(corners[i]);
            }

            var minX = corners.Min(corner => corner.x);
            var maxX = corners.Max(corner => corner.x);
            var minY = corners.Min(corner => corner.y);
            var maxY = corners.Max(corner => corner.y);
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        static Vector2 ComputeContainmentDelta(RectTransform rect, RectTransform parent, float padding)
        {
            return ComputeContainmentDelta(GetLocalAabbInParent(rect, parent), parent.rect, padding);
        }

        static Vector2 ComputeContainmentDeltaAfterMove(RectTransform rect, RectTransform parent, Vector2 moveDelta, float padding)
        {
            var moved = GetLocalAabbInParent(rect, parent);
            moved.position += moveDelta;
            return ComputeContainmentDelta(moved, parent.rect, padding);
        }

        static Vector2 ComputeContainmentDelta(Rect childBounds, Rect parentRect, float padding)
        {
            var parentMinX = parentRect.xMin + padding;
            var parentMaxX = parentRect.xMax - padding;
            var parentMinY = parentRect.yMin + padding;
            var parentMaxY = parentRect.yMax - padding;

            var deltaX = 0f;
            if (childBounds.width > parentMaxX - parentMinX)
            {
                deltaX = parentRect.center.x - childBounds.center.x;
            }
            else if (childBounds.xMin < parentMinX)
            {
                deltaX = parentMinX - childBounds.xMin;
            }
            else if (childBounds.xMax > parentMaxX)
            {
                deltaX = parentMaxX - childBounds.xMax;
            }

            var deltaY = 0f;
            if (childBounds.height > parentMaxY - parentMinY)
            {
                deltaY = parentRect.center.y - childBounds.center.y;
            }
            else if (childBounds.yMin < parentMinY)
            {
                deltaY = parentMinY - childBounds.yMin;
            }
            else if (childBounds.yMax > parentMaxY)
            {
                deltaY = parentMaxY - childBounds.yMax;
            }

            return new Vector2(deltaX, deltaY);
        }

        static IEnumerable<RectTransform> GetDirectUiLayoutChildren(RectTransform rect)
        {
            return rect.Cast<Transform>()
                .OfType<RectTransform>()
                .Where(child => child.gameObject.activeInHierarchy)
                .Where(child => child.GetComponent<LayoutElement>() == null || !child.GetComponent<LayoutElement>().ignoreLayout)
                .Where(child => child.GetComponent<Graphic>() != null || child.GetComponentsInChildren<Graphic>(true).Length > 0);
        }

        static UILayoutGroupType InferLayoutGroupType(RectTransform container, RectTransform[] children)
        {
            if (children.Length >= 4)
            {
                var xRange = children.Max(child => child.anchoredPosition.x) - children.Min(child => child.anchoredPosition.x);
                var yRange = children.Max(child => child.anchoredPosition.y) - children.Min(child => child.anchoredPosition.y);
                var averageWidth = Mathf.Max(1f, children.Average(child => Mathf.Abs(child.rect.width)));
                var averageHeight = Mathf.Max(1f, children.Average(child => Mathf.Abs(child.rect.height)));
                if (xRange > averageWidth * 1.5f && yRange > averageHeight * 1.5f)
                {
                    return UILayoutGroupType.Grid;
                }
            }

            var horizontalSpread = children.Max(child => child.anchoredPosition.x) - children.Min(child => child.anchoredPosition.x);
            var verticalSpread = children.Max(child => child.anchoredPosition.y) - children.Min(child => child.anchoredPosition.y);
            return horizontalSpread >= verticalSpread
                ? UILayoutGroupType.Horizontal
                : UILayoutGroupType.Vertical;
        }

        static void ConfigureInferredLayoutGroup(LayoutGroup group, Vector2[] childSizes)
        {
            group.padding = new RectOffset(12, 12, 12, 12);
            group.childAlignment = TextAnchor.MiddleCenter;

            if (group is HorizontalOrVerticalLayoutGroup horizontalOrVertical)
            {
                horizontalOrVertical.spacing = 8f;
                horizontalOrVertical.childControlWidth = true;
                horizontalOrVertical.childControlHeight = true;
                horizontalOrVertical.childForceExpandWidth = false;
                horizontalOrVertical.childForceExpandHeight = false;
                horizontalOrVertical.childScaleWidth = false;
                horizontalOrVertical.childScaleHeight = false;
            }
            else if (group is GridLayoutGroup grid)
            {
                var widths = childSizes.Select(size => Mathf.Max(32f, size.x)).OrderBy(value => value).ToArray();
                var heights = childSizes.Select(size => Mathf.Max(24f, size.y)).OrderBy(value => value).ToArray();
                grid.spacing = new Vector2(8f, 8f);
                grid.cellSize = new Vector2(Median(widths), Median(heights));
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(childSizes.Length)));
            }
        }

        static void PreserveChildLayoutSize(RectTransform child, Vector2 size)
        {
            var element = child.GetComponent<LayoutElement>() ?? Undo.AddComponent<LayoutElement>(child.gameObject);
            Undo.RecordObject(element, "Preserve UniBridge child layout size");
            element.preferredWidth = Mathf.Max(1f, size.x);
            element.preferredHeight = Mathf.Max(1f, size.y);
            element.flexibleWidth = 0f;
            element.flexibleHeight = 0f;
            EditorUtility.SetDirty(element);
        }

        static void PreserveExplicitLayoutSizeWhenNeeded(RectTransform child, ManageUIParams parameters)
        {
            if (child == null || parameters.SizeDelta == null || parameters.SizeDelta.Length < 2)
            {
                return;
            }

            if (child.parent is not RectTransform parent || parent.GetComponent<LayoutGroup>() == null)
            {
                return;
            }

            if (!TryReadVector2(parameters.SizeDelta, out var size))
            {
                size = child.sizeDelta;
            }
            if (size.x <= 0f && size.y <= 0f)
            {
                return;
            }

            var element = child.GetComponent<LayoutElement>() ?? child.gameObject.AddComponent<LayoutElement>();
            if (size.x > 0f && element.preferredWidth < 0f)
            {
                element.preferredWidth = Mathf.Max(1f, size.x);
            }

            if (size.y > 0f && element.preferredHeight < 0f)
            {
                element.preferredHeight = Mathf.Max(1f, size.y);
            }

            if (size.x > 0f && element.flexibleWidth < 0f)
            {
                element.flexibleWidth = 0f;
            }

            if (size.y > 0f && element.flexibleHeight < 0f)
            {
                element.flexibleHeight = 0f;
            }

            EditorUtility.SetDirty(element);
        }

        static float Median(float[] sortedValues)
        {
            if (sortedValues == null || sortedValues.Length == 0)
            {
                return 0f;
            }

            var middle = sortedValues.Length / 2;
            return sortedValues.Length % 2 == 0
                ? (sortedValues[middle - 1] + sortedValues[middle]) * 0.5f
                : sortedValues[middle];
        }

        static void ConfigureDefaultCanvasScaler(CanvasScaler scaler)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
        }

        static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength - 3) + "...";
        }


    }
}
