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
    /// <summary>
    /// UI and RectTransform helpers for AI-controlled Unity UI work.
    /// </summary>
    public static partial class ManageUI
    {
        public const string Title = "Manage Unity UI and RectTransforms";

        public const string Description = @"Create and inspect Unity UI objects and safely adjust RectTransform layout.

Use this when an agent needs to work with Canvas-based UI without manually calculating anchor and pivot math.

Actions:
    Inspect: Return RectTransform, Canvas, parent, world-corner, and layout information for a UI object.
    CreateCanvas: Create a Canvas with CanvasScaler and GraphicRaycaster, optionally with an EventSystem.
    EnsureEventSystem: Create or repair an EventSystem with an input module compatible with the project input handling.
    CreateElement: Create an Empty, Panel, Image, Text, Button, TextMeshProText, TextMeshProButton, Toggle, Slider, Dropdown, InputField, Scrollbar, TextMeshProInputField, TextMeshProDropdown, or ToggleGroup UI object under a parent/canvas.
    CreateTemplate: Create a higher-level UI composition such as Panel, Modal, Toolbar, List, CardGrid, or HUD using layout groups and optional post-create validation.
    CreateScrollView: Create a full ScrollRect hierarchy with Viewport, clipped Content, layout group, and optional starter items.
    AddScrollItem: Add one or more stable row items to an existing ScrollRect content object.
    SetGraphic: Configure Image, RawImage, or Text/TMP Graphic color, sprite, texture, material, raycast target, and native size.
    SetSelectableTransition: Configure Button/Selectable transition, target graphic, color tint block, and sprite-swap state.
    SetButtonEvent: Add a persistent Button.onClick listener to a GameObject or Component method.
    ClearButtonEvents: Remove persistent Button.onClick listeners.
    SetRectTransformLayout: Apply layout presets inspired by common Unity anchor presets: Left/Center/Right/Stretch and Top/Middle/Bottom/Stretch.
    SetRectTransform: Directly set anchors, pivot, anchored position, size, offsets, or local scale.
    SetLayoutGroup: Add or update HorizontalLayoutGroup, VerticalLayoutGroup, or GridLayoutGroup on a UI container.
    SetContentSizeFitter: Add or update ContentSizeFitter for automatic container sizing.
    SetLayoutElement: Add or update LayoutElement on a UI child so parent layout groups size it predictably.
    Validate/Audit: Read-only scan for UI quality issues such as legacy/TMP text overflow, sibling overlap, invisible elements, zero scale, bad Canvas sorting, missing EventSystem, duplicate layout groups, and capture caveats.
    RepairPlan: Read-only, agent-oriented repair plan that explains what can be fixed safely, with layout changes, or aggressively.
    AutoFix: Fix audit findings according to AutoFixMode: Safe, Layout, or Aggressive.

Layout notes:
    SetRectTransformLayout requires the target RectTransform to have a RectTransform parent.
    CreateTemplate is the preferred high-level entry point when the agent wants a usable screen section instead of manually placing many RectTransforms.
    CreateScrollView creates the canonical ScrollRect -> Viewport -> Content hierarchy, with RectMask2D clipping by default.
    AddScrollItem accepts the ScrollRect root, Viewport, or Content object and resolves the correct Content target automatically.
    SetGraphic is useful for button backgrounds, panel graphics, sprites/icons, RawImage textures, and material assignment.
    SetButtonEvent uses Unity persistent calls, so configured onClick listeners survive scene saves and can be inspected later.
    SetLayoutGroup is best used on container objects such as panels, rows, columns, and grids.
    SetLayoutElement is best used on children inside a parent LayoutGroup.
    TextMesh Pro support is optional and uses the project's installed TMPro.TextMeshProUGUI type when available.
    AlsoSetPivot=true makes the pivot match the layout preset.
    AlsoSetPosition=false preserves the current visual position while changing anchors.
    AlsoSetPosition=true snaps the anchored position/size to the selected layout preset.
    DryRun=true reports what would change without modifying the scene.
    AutoFixMode=Safe keeps changes conservative. Layout may move children to resolve obvious overlap/outside-parent issues. Aggressive may convert manual containers into LayoutGroups with LayoutElements.";

        [McpTool("UniBridge_ManageUI", Description, Title, Groups = new[] { "core", "scene", "ui" }, EnabledByDefault = true)]
        public static object HandleCommand(ManageUIParams parameters)
        {
            parameters ??= new ManageUIParams();

            try
            {
                return parameters.Action switch
                {
                    UIAction.Inspect => Inspect(parameters),
                    UIAction.CreateCanvas => CreateCanvas(parameters),
                    UIAction.EnsureEventSystem => EnsureEventSystem(parameters),
                    UIAction.CreateElement => CreateElement(parameters),
                    UIAction.CreateTemplate => CreateTemplate(parameters),
                    UIAction.CreateScrollView => CreateScrollView(parameters),
                    UIAction.AddScrollItem => AddScrollItem(parameters),
                    UIAction.SetGraphic => SetGraphic(parameters),
                    UIAction.SetButtonEvent => SetButtonEvent(parameters),
                    UIAction.ClearButtonEvents => ClearButtonEvents(parameters),
                    UIAction.SetSelectableTransition => SetSelectableTransition(parameters),
                    UIAction.SetRectTransformLayout => SetRectTransformLayout(parameters),
                    UIAction.SetRectTransform => SetRectTransform(parameters),
                    UIAction.SetLayoutGroup => SetLayoutGroup(parameters),
                    UIAction.SetContentSizeFitter => SetContentSizeFitter(parameters),
                    UIAction.SetLayoutElement => SetLayoutElement(parameters),
                    UIAction.Validate => Audit(parameters),
                    UIAction.Audit => Audit(parameters),
                    UIAction.RepairPlan => RepairPlan(parameters),
                    UIAction.AutoFix => AutoFix(parameters),
                    _ => Response.Error($"Unsupported UI action '{parameters.Action}'.")
                };
            }
            catch (Exception ex)
            {
                return Response.Error($"UI action '{parameters.Action}' failed: {ex.Message}");
            }
        }

        static object Inspect(ManageUIParams parameters)
        {
            var target = ResolveTargetGameObject(parameters.Target);
            if (target == null)
            {
                return Response.Error("Inspect requires a UI target GameObject, hierarchy path, object id, or selected object.");
            }

            var rectTransform = target.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                var worldTextMeshPro = GetWorldTextMeshProText(target);
                if (worldTextMeshPro != null)
                {
                    return SetWorldTextMeshProGraphic(target, worldTextMeshPro, parameters);
                }

                return Response.Error($"Target '{target.name}' does not have a RectTransform or a supported world-space TMPro.TextMeshPro component.");
            }

            return Response.Success($"Inspected UI object '{target.name}'.", new
            {
                target = BuildGameObjectInfo(target),
                rectTransform = BuildRectTransformInfo(rectTransform),
                layoutComponents = BuildLayoutComponentsInfo(target),
                graphic = BuildGraphicInfo(target),
                selectable = BuildSelectableInfo(target),
                button = BuildButtonInfo(target),
                textComponent = BuildTextComponentInfo(target),
                canvas = BuildCanvasInfo(target.GetComponentInParent<Canvas>(true)),
                parent = rectTransform.parent is RectTransform parent ? BuildGameObjectInfo(parent.gameObject) : null,
                children = rectTransform.Cast<Transform>()
                    .OfType<RectTransform>()
                    .Select(child => new
                    {
                        name = child.name,
                        path = GetHierarchyPath(child.gameObject),
                        id = UnityApiAdapter.GetObjectId(child.gameObject),
                        rect = BuildCompactRectTransformInfo(child)
                    })
                    .ToArray()
            });
        }

        static object CreateCanvas(ManageUIParams parameters)
        {
            var dryRun = parameters.DryRun ?? false;
            var name = string.IsNullOrWhiteSpace(parameters.Name) ? "UniBridge Canvas" : parameters.Name.Trim();
            var eventSystemPlan = parameters.EnsureEventSystem ?? true;

            if (dryRun)
            {
                return Response.Success("Dry run: Canvas would be created.", new
                {
                    name,
                    renderMode = parameters.RenderMode.ToString(),
                    overrideSorting = parameters.OverrideSorting ?? true,
                    sortingOrder = parameters.SortingOrder ?? 100,
                    ensureEventSystem = eventSystemPlan
                });
            }

            var canvasObject = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            GameObjectUtility.EnsureUniqueNameForSibling(canvasObject);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = ConvertRenderMode(parameters.RenderMode);
            canvas.overrideSorting = parameters.OverrideSorting ?? true;
            canvas.sortingOrder = parameters.SortingOrder ?? 100;
            if (parameters.RenderMode == CanvasRenderModeOption.ScreenSpaceCamera)
            {
                canvas.worldCamera = ResolveCamera(parameters.Camera);
            }

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            Undo.RegisterCreatedObjectUndo(canvasObject, "Create UniBridge UI Canvas");

            GameObject eventSystem = null;
            if (eventSystemPlan)
            {
                eventSystem = EnsureEventSystemObject();
            }

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = canvasObject;
            }

            MarkSceneDirty(canvasObject);
            SceneView.RepaintAll();

            return Response.Success($"Created Canvas '{canvasObject.name}'.", new
            {
                canvas = BuildGameObjectInfo(canvasObject),
                rectTransform = BuildRectTransformInfo(canvasObject.GetComponent<RectTransform>()),
                renderMode = canvas.renderMode.ToString(),
                overrideSorting = canvas.overrideSorting,
                sortingOrder = canvas.sortingOrder,
                eventSystem = eventSystem != null ? BuildGameObjectInfo(eventSystem) : null
            });
        }

        static object EnsureEventSystem(ManageUIParams parameters)
        {
            var existing = FindEventSystem();
            if (existing != null)
            {
                return Response.Success($"EventSystem already exists: '{existing.name}'.", new
                {
                    created = false,
                    eventSystem = BuildGameObjectInfo(existing)
                });
            }

            if (parameters.DryRun ?? false)
            {
                return Response.Success("Dry run: EventSystem would be created.", new { created = true, name = "EventSystem" });
            }

            var eventSystem = EnsureEventSystemObject();
            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = eventSystem;
            }

            MarkSceneDirty(eventSystem);
            return Response.Success($"Created EventSystem '{eventSystem.name}'.", new
            {
                created = true,
                eventSystem = BuildGameObjectInfo(eventSystem)
            });
        }

        static object CreateElement(ManageUIParams parameters)
        {
            var dryRun = parameters.DryRun ?? false;
            var parent = ResolveParentForCreate(parameters);
            var createCanvas = parameters.CreateParentCanvas ?? true;

            if (parent == null && !createCanvas)
            {
                return Response.Error("CreateElement requires Parent, Target, or CreateParentCanvas=true.");
            }

            var name = string.IsNullOrWhiteSpace(parameters.Name)
                ? DefaultElementName(parameters.ElementType)
                : parameters.Name.Trim();

            if (dryRun)
            {
                return Response.Success("Dry run: UI element would be created.", new
                {
                    name,
                    elementType = parameters.ElementType.ToString(),
                    parent = parent != null ? BuildGameObjectInfo(parent) : null,
                    createParentCanvas = parent == null && createCanvas,
                    layoutHorizontal = parameters.LayoutHorizontal.ToString(),
                    layoutVertical = parameters.LayoutVertical.ToString(),
                    fontSize = parameters.FontSize,
                    alignment = parameters.Alignment.ToString(),
                    bestFit = parameters.BestFit ?? false,
                    richText = parameters.RichText,
                    overflowMode = parameters.OverflowMode,
                    fontAssetPath = parameters.FontAssetPath,
                    createTmpFontAssetIfMissing = parameters.CreateTmpFontAssetIfMissing ?? true,
                    requiresTextMeshPro = IsTextMeshProElement(parameters.ElementType)
                });
            }

            if (IsTextMeshProElement(parameters.ElementType) && GetTextMeshProUGUIType() == null)
            {
                return Response.Error("TextMesh Pro support was requested, but TMPro.TextMeshProUGUI is not available in this project. Install/import TextMesh Pro, or create a legacy Text/Button element instead.");
            }

            if (parent == null)
            {
                var canvasResult = CreateCanvasObject(
                    "UniBridge Canvas",
                    parameters.RenderMode,
                    parameters.Camera,
                    parameters.EnsureEventSystem ?? true,
                    parameters.OverrideSorting ?? true,
                    parameters.SortingOrder ?? 100);
                parent = canvasResult.canvasObject;
            }

            var element = new GameObject(name, typeof(RectTransform));
            element.transform.SetParent(parent.transform, false);
            GameObjectUtility.EnsureUniqueNameForSibling(element);

            var rectTransform = element.GetComponent<RectTransform>();
            ApplyLayoutPreset(
                rectTransform,
                parameters.LayoutHorizontal,
                parameters.LayoutVertical,
                parameters.AlsoSetPivot ?? true,
                true);
            ApplyOptionalRectValues(rectTransform, parameters, recordUndo: false);

            ConfigureElement(element, parameters);
            PreserveExplicitLayoutSizeWhenNeeded(rectTransform, parameters);
            Undo.RegisterCreatedObjectUndo(element, "Create UniBridge UI Element");

            if (parameters.EnsureEventSystem ?? true)
            {
                EnsureEventSystemObject();
            }

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = element;
            }

            MarkSceneDirty(element);
            SceneView.RepaintAll();

            return Response.Success($"Created {parameters.ElementType} UI element '{element.name}'.", new
            {
                element = BuildGameObjectInfo(element),
                parent = BuildGameObjectInfo(parent),
                rectTransform = BuildRectTransformInfo(rectTransform),
                layoutComponents = BuildLayoutComponentsInfo(element),
                components = element.GetComponents<Component>().Select(component => component.GetType().Name).ToArray()
            });
        }

        static object CreateTemplate(ManageUIParams parameters)
        {
            var dryRun = parameters.DryRun ?? false;
            var parent = ResolveExplicitParentForCreate(parameters);
            var createCanvas = parameters.CreateParentCanvas ?? true;
            var templateName = DefaultTemplateName(parameters);

            if (parent == null && !createCanvas)
            {
                return Response.Error("CreateTemplate requires Parent, Target, or CreateParentCanvas=true.");
            }

            if (dryRun)
            {
                return Response.Success("Dry run: UI template would be created.", new
                {
                    name = templateName,
                    templateType = parameters.TemplateType.ToString(),
                    parent = parent != null ? BuildGameObjectInfo(parent) : null,
                    createParentCanvas = parent == null && createCanvas,
                    title = ResolveTemplateTitle(parameters),
                    subtitle = parameters.Subtitle,
                    itemCount = ResolveTemplateItems(parameters).Length,
                    actionCount = ResolveTemplateActions(parameters).Length,
                    columns = Mathf.Max(1, parameters.Columns ?? 3),
                    useTextMeshPro = ShouldUseTemplateTextMeshPro(parameters),
                    validateAfterCreate = parameters.ValidateAfterCreate ?? true
                });
            }

            GameObject createdCanvas = null;
            if (parent == null)
            {
                var canvasResult = CreateCanvasObject(
                    $"{templateName} Canvas",
                    parameters.RenderMode,
                    parameters.Camera,
                    parameters.EnsureEventSystem ?? true,
                    parameters.OverrideSorting ?? true,
                    parameters.SortingOrder ?? 100);
                parent = canvasResult.canvasObject;
                createdCanvas = canvasResult.canvasObject;
            }

            if (parent.GetComponent<RectTransform>() == null)
            {
                return Response.Error($"Parent '{parent.name}' does not have a RectTransform. CreateTemplate needs a Canvas/UI parent.");
            }

            var roles = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            var root = parameters.TemplateType switch
            {
                UITemplateType.Modal => BuildModalTemplate(parent, templateName, parameters, roles),
                UITemplateType.Toolbar => BuildToolbarTemplate(parent, templateName, parameters, roles),
                UITemplateType.List => BuildListTemplate(parent, templateName, parameters, roles),
                UITemplateType.CardGrid => BuildCardGridTemplate(parent, templateName, parameters, roles),
                UITemplateType.HUD => BuildHudTemplate(parent, templateName, parameters, roles),
                _ => BuildPanelTemplate(parent, templateName, parameters, roles)
            };

            Undo.RegisterCreatedObjectUndo(root, "Create UniBridge UI Template");

            if (parameters.EnsureEventSystem ?? true)
            {
                EnsureEventSystemObject();
            }

            RebuildLayout(root.GetComponent<RectTransform>());

            object validation = null;
            if (parameters.ValidateAfterCreate ?? true)
            {
                var validationParameters = parameters with
                {
                    Action = UIAction.Validate,
                    Target = GetHierarchyPath(root),
                    IncludeInactive = true,
                    MaxIssues = parameters.MaxIssues ?? 100
                };
                validation = BuildAuditReport(validationParameters).ToResponseData();
            }

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = root;
            }

            MarkSceneDirty(root);
            SceneView.RepaintAll();

            return Response.Success($"Created {parameters.TemplateType} UI template '{root.name}'.", new
            {
                templateType = parameters.TemplateType.ToString(),
                root = BuildGameObjectInfo(root),
                parent = BuildGameObjectInfo(parent),
                createdCanvas = createdCanvas != null ? BuildGameObjectInfo(createdCanvas) : null,
                roles = roles.ToDictionary(pair => pair.Key, pair => BuildGameObjectInfo(pair.Value), StringComparer.OrdinalIgnoreCase),
                createdObjects = root.GetComponentsInChildren<Transform>(true)
                    .Select(transform => BuildGameObjectInfo(transform.gameObject))
                    .ToArray(),
                validation
            });
        }

        static object CreateScrollView(ManageUIParams parameters)
        {
            var dryRun = parameters.DryRun ?? false;
            var parent = ResolveParentForCreate(parameters);
            var createCanvas = parameters.CreateParentCanvas ?? true;
            var name = string.IsNullOrWhiteSpace(parameters.Name) ? "Scroll View" : parameters.Name.Trim();
            var viewportName = string.IsNullOrWhiteSpace(parameters.ViewportName) ? "Viewport" : parameters.ViewportName.Trim();
            var contentName = string.IsNullOrWhiteSpace(parameters.ContentName) ? "Content" : parameters.ContentName.Trim();
            var itemTexts = GetRequestedItemTexts(parameters);
            var itemElementType = ResolveScrollItemElementType(parameters);

            if (parent == null && !createCanvas)
            {
                return Response.Error("CreateScrollView requires Parent, Target, or CreateParentCanvas=true.");
            }

            if (itemTexts.Length > 0 && IsTextMeshProElement(itemElementType) && GetTextMeshProUGUIType() == null)
            {
                return Response.Error("CreateScrollView item creation requested TextMesh Pro, but TMPro.TextMeshProUGUI is not available in this project. Import TextMesh Pro essentials or set ElementType=Text.");
            }

            if (dryRun)
            {
                return Response.Success("Dry run: ScrollView would be created.", new
                {
                    name,
                    parent = parent != null ? BuildGameObjectInfo(parent) : null,
                    createParentCanvas = parent == null && createCanvas,
                    viewportName,
                    contentName,
                    scrollDirection = parameters.ScrollDirection.ToString(),
                    movementType = parameters.MovementType.ToString(),
                    useRectMask2D = parameters.UseRectMask2D ?? true,
                    itemCount = itemTexts.Length,
                    itemElementType = itemElementType.ToString()
                });
            }

            if (parent == null)
            {
                var canvasResult = CreateCanvasObject(
                    "UniBridge Canvas",
                    parameters.RenderMode,
                    parameters.Camera,
                    parameters.EnsureEventSystem ?? true,
                    parameters.OverrideSorting ?? true,
                    parameters.SortingOrder ?? 100);
                parent = canvasResult.canvasObject;
            }

            if (parent.GetComponent<RectTransform>() == null)
            {
                return Response.Error($"Parent '{parent.name}' does not have a RectTransform. ScrollView UI must be created under a Canvas/UI object.");
            }

            var scrollView = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollView.transform.SetParent(parent.transform, false);
            GameObjectUtility.EnsureUniqueNameForSibling(scrollView);

            var scrollRectTransform = scrollView.GetComponent<RectTransform>();
            ApplyLayoutPreset(
                scrollRectTransform,
                parameters.LayoutHorizontal,
                parameters.LayoutVertical,
                parameters.AlsoSetPivot ?? true,
                true);
            if (parameters.SizeDelta == null || parameters.SizeDelta.Length < 2)
            {
                scrollRectTransform.sizeDelta = new Vector2(420f, 300f);
            }

            ApplyOptionalRectValues(scrollRectTransform, parameters, recordUndo: false);

            var background = scrollView.GetComponent<Image>();
            background.color = ReadColor(parameters.BackgroundColor, new Color(0.035f, 0.045f, 0.065f, 0.94f));
            background.raycastTarget = true;

            var viewport = new GameObject(viewportName, typeof(RectTransform), typeof(Image));
            viewport.transform.SetParent(scrollView.transform, false);
            GameObjectUtility.EnsureUniqueNameForSibling(viewport);
            var viewportRect = viewport.GetComponent<RectTransform>();
            ConfigureStretchRect(viewportRect);
            var viewportImage = viewport.GetComponent<Image>();
            viewportImage.color = new Color(1f, 1f, 1f, 0.01f);
            viewportImage.raycastTarget = true;
            if (parameters.UseRectMask2D ?? true)
            {
                viewport.AddComponent<RectMask2D>();
            }

            var content = new GameObject(contentName, typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            GameObjectUtility.EnsureUniqueNameForSibling(content);
            var contentRect = content.GetComponent<RectTransform>();
            ConfigureScrollContentRect(contentRect, parameters.ScrollDirection, scrollRectTransform.rect.size);
            ConfigureScrollContentLayout(contentRect, parameters);

            var scrollRect = scrollView.GetComponent<ScrollRect>();
            ConfigureScrollRect(scrollRect, viewportRect, contentRect, parameters);

            var createdItems = CreateScrollItems(contentRect, parameters, itemTexts, itemElementType);

            Undo.RegisterCreatedObjectUndo(scrollView, "Create UniBridge ScrollView");

            if (parameters.EnsureEventSystem ?? true)
            {
                EnsureEventSystemObject();
            }

            RebuildLayout(contentRect);

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = scrollView;
            }

            MarkSceneDirty(scrollView);
            SceneView.RepaintAll();

            return Response.Success($"Created ScrollView '{scrollView.name}'.", new
            {
                scrollView = BuildGameObjectInfo(scrollView),
                parent = BuildGameObjectInfo(parent),
                rectTransform = BuildRectTransformInfo(scrollRectTransform),
                layoutComponents = BuildLayoutComponentsInfo(scrollView),
                viewport = BuildGameObjectInfo(viewport),
                viewportRect = BuildRectTransformInfo(viewportRect),
                content = BuildGameObjectInfo(content),
                contentRect = BuildRectTransformInfo(contentRect),
                contentLayout = BuildLayoutComponentsInfo(content),
                createdItems = createdItems.Select(BuildGameObjectInfo).ToArray()
            });
        }

        static object AddScrollItem(ManageUIParams parameters)
        {
            var target = ResolveTargetGameObject(parameters.Target);
            if (target == null)
            {
                return Response.Error("AddScrollItem requires Target pointing to a ScrollRect root, Viewport, Content object, or selected ScrollView.");
            }

            var contentRect = ResolveScrollContent(target, parameters.ContentName);
            if (contentRect == null)
            {
                return Response.Error($"Could not resolve ScrollRect content from '{target.name}'. Target the ScrollRect root or a child named '{(string.IsNullOrWhiteSpace(parameters.ContentName) ? "Content" : parameters.ContentName.Trim())}'.");
            }

            var itemTexts = GetRequestedItemTexts(parameters);
            if (itemTexts.Length == 0)
            {
                itemTexts = new[] { parameters.Text ?? parameters.Name ?? "Item" };
            }

            var itemElementType = ResolveScrollItemElementType(parameters);
            if (IsTextMeshProElement(itemElementType) && GetTextMeshProUGUIType() == null)
            {
                return Response.Error("AddScrollItem requested TextMesh Pro, but TMPro.TextMeshProUGUI is not available in this project. Import TextMesh Pro essentials or set ElementType=Text.");
            }

            if (parameters.DryRun ?? false)
            {
                return Response.Success("Dry run: Scroll item(s) would be added.", new
                {
                    target = BuildGameObjectInfo(target),
                    content = BuildGameObjectInfo(contentRect.gameObject),
                    itemCount = itemTexts.Length,
                    itemElementType = itemElementType.ToString(),
                    names = BuildScrollItemNames(parameters, itemTexts.Length)
                });
            }

            var beforeChildCount = contentRect.childCount;
            var createdItems = CreateScrollItems(contentRect, parameters, itemTexts, itemElementType);
            RebuildLayout(contentRect);

            if (parameters.Select ?? false && createdItems.Count > 0)
            {
                Selection.activeGameObject = createdItems[0];
            }

            MarkSceneDirty(contentRect.gameObject);
            SceneView.RepaintAll();

            return Response.Success($"Added {createdItems.Count} ScrollView item(s) under '{contentRect.name}'.", new
            {
                target = BuildGameObjectInfo(target),
                content = BuildGameObjectInfo(contentRect.gameObject),
                beforeChildCount,
                afterChildCount = contentRect.childCount,
                createdItems = createdItems.Select(BuildGameObjectInfo).ToArray(),
                contentLayout = BuildLayoutComponentsInfo(contentRect.gameObject)
            });
        }

        static object SetRectTransformLayout(ManageUIParams parameters)
        {
            var target = ResolveTargetGameObject(parameters.Target);
            if (target == null)
            {
                return Response.Error("SetRectTransformLayout requires Target or a selected UI object.");
            }

            var rectTransform = target.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                return Response.Error($"Target '{target.name}' does not have a RectTransform.");
            }

            if (rectTransform.parent is not RectTransform)
            {
                return Response.Error($"Target '{target.name}' needs a RectTransform parent for layout presets.");
            }

            var before = BuildRectTransformInfo(rectTransform);
            var alsoSetPivot = parameters.AlsoSetPivot ?? true;
            var alsoSetPosition = parameters.AlsoSetPosition ?? false;

            if (parameters.DryRun ?? false)
            {
                return Response.Success("Dry run: RectTransform layout would be changed.", new
                {
                    target = BuildGameObjectInfo(target),
                    before,
                    planned = new
                    {
                        layoutHorizontal = parameters.LayoutHorizontal.ToString(),
                        layoutVertical = parameters.LayoutVertical.ToString(),
                        alsoSetPivot,
                        alsoSetPosition
                    }
                });
            }

            Undo.RecordObject(rectTransform, "Set UniBridge RectTransform Layout");
            ApplyLayoutPreset(rectTransform, parameters.LayoutHorizontal, parameters.LayoutVertical, alsoSetPivot, alsoSetPosition);

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = target;
            }

            EditorUtility.SetDirty(rectTransform);
            MarkSceneDirty(target);
            SceneView.RepaintAll();

            return Response.Success($"Updated RectTransform layout for '{target.name}'.", new
            {
                target = BuildGameObjectInfo(target),
                before,
                after = BuildRectTransformInfo(rectTransform)
            });
        }

        static object SetRectTransform(ManageUIParams parameters)
        {
            var target = ResolveTargetGameObject(parameters.Target);
            if (target == null)
            {
                return Response.Error("SetRectTransform requires Target or a selected UI object.");
            }

            var rectTransform = target.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                return Response.Error($"Target '{target.name}' does not have a RectTransform.");
            }

            var before = BuildRectTransformInfo(rectTransform);

            if (parameters.DryRun ?? false)
            {
                return Response.Success("Dry run: RectTransform values would be changed.", new
                {
                    target = BuildGameObjectInfo(target),
                    before,
                    planned = BuildDirectRectPlan(parameters)
                });
            }

            ApplyOptionalRectValues(rectTransform, parameters, recordUndo: true);

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = target;
            }

            EditorUtility.SetDirty(rectTransform);
            MarkSceneDirty(target);
            SceneView.RepaintAll();

            return Response.Success($"Updated RectTransform values for '{target.name}'.", new
            {
                target = BuildGameObjectInfo(target),
                before,
                after = BuildRectTransformInfo(rectTransform)
            });
        }

        static object SetLayoutGroup(ManageUIParams parameters)
        {
            var target = ResolveTargetGameObject(parameters.Target);
            if (target == null)
            {
                return Response.Error("SetLayoutGroup requires Target or a selected UI container.");
            }

            var rectTransform = target.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                return Response.Error($"Target '{target.name}' does not have a RectTransform.");
            }

            var before = BuildLayoutComponentsInfo(target);
            var componentType = GetLayoutGroupComponentType(parameters.LayoutGroupType);

            if (parameters.DryRun ?? false)
            {
                return Response.Success("Dry run: LayoutGroup would be added or updated.", new
                {
                    target = BuildGameObjectInfo(target),
                    before,
                    planned = BuildLayoutGroupPlan(parameters),
                    removeExistingLayoutGroups = parameters.RemoveExistingLayoutGroups ?? true
                });
            }

            if (parameters.RemoveExistingLayoutGroups ?? true)
            {
                RemoveLayoutGroupsExcept(target, componentType);
            }

            var group = GetFirstLayoutGroup(target, componentType);
            if (group == null)
            {
                group = (LayoutGroup)Undo.AddComponent(target, componentType);
            }

            RemoveDuplicateLayoutGroups(target, componentType, group);

            Undo.RecordObject(group, "Set UniBridge Layout Group");
            ApplyLayoutGroupSettings(group, parameters);

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = target;
            }

            EditorUtility.SetDirty(group);
            LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            MarkSceneDirty(target);
            SceneView.RepaintAll();

            return Response.Success($"Updated {group.GetType().Name} for '{target.name}'.", new
            {
                target = BuildGameObjectInfo(target),
                before,
                after = BuildLayoutComponentsInfo(target)
            });
        }

        static object SetContentSizeFitter(ManageUIParams parameters)
        {
            var target = ResolveTargetGameObject(parameters.Target);
            if (target == null)
            {
                return Response.Error("SetContentSizeFitter requires Target or a selected UI object.");
            }

            var rectTransform = target.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                return Response.Error($"Target '{target.name}' does not have a RectTransform.");
            }

            if (!parameters.HorizontalFit.HasValue && !parameters.VerticalFit.HasValue)
            {
                return Response.Error("SetContentSizeFitter requires HorizontalFit, VerticalFit, or both.");
            }

            var before = BuildLayoutComponentsInfo(target);
            if (parameters.DryRun ?? false)
            {
                return Response.Success("Dry run: ContentSizeFitter would be added or updated.", new
                {
                    target = BuildGameObjectInfo(target),
                    before,
                    planned = new
                    {
                        horizontalFit = parameters.HorizontalFit?.ToString(),
                        verticalFit = parameters.VerticalFit?.ToString()
                    }
                });
            }

            var fitter = target.GetComponent<ContentSizeFitter>() ?? Undo.AddComponent<ContentSizeFitter>(target);
            Undo.RecordObject(fitter, "Set UniBridge Content Size Fitter");
            if (parameters.HorizontalFit.HasValue)
            {
                fitter.horizontalFit = ConvertFitMode(parameters.HorizontalFit.Value);
            }

            if (parameters.VerticalFit.HasValue)
            {
                fitter.verticalFit = ConvertFitMode(parameters.VerticalFit.Value);
            }

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = target;
            }

            EditorUtility.SetDirty(fitter);
            LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            MarkSceneDirty(target);
            SceneView.RepaintAll();

            return Response.Success($"Updated ContentSizeFitter for '{target.name}'.", new
            {
                target = BuildGameObjectInfo(target),
                before,
                after = BuildLayoutComponentsInfo(target)
            });
        }

        static object SetLayoutElement(ManageUIParams parameters)
        {
            var target = ResolveTargetGameObject(parameters.Target);
            if (target == null)
            {
                return Response.Error("SetLayoutElement requires Target or a selected UI child.");
            }

            var rectTransform = target.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                return Response.Error($"Target '{target.name}' does not have a RectTransform.");
            }

            if (!HasLayoutElementSettings(parameters))
            {
                return Response.Error("SetLayoutElement requires at least one LayoutElement setting such as PreferredWidth, PreferredHeight, FlexibleWidth, or IgnoreLayout.");
            }

            var before = BuildLayoutComponentsInfo(target);
            if (parameters.DryRun ?? false)
            {
                return Response.Success("Dry run: LayoutElement would be added or updated.", new
                {
                    target = BuildGameObjectInfo(target),
                    before,
                    planned = BuildLayoutElementPlan(parameters)
                });
            }

            var layoutElement = target.GetComponent<LayoutElement>() ?? Undo.AddComponent<LayoutElement>(target);
            Undo.RecordObject(layoutElement, "Set UniBridge Layout Element");
            ApplyLayoutElementSettings(layoutElement, parameters);

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = target;
            }

            EditorUtility.SetDirty(layoutElement);
            LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            if (rectTransform.parent is RectTransform parentRect)
            {
                LayoutRebuilder.MarkLayoutForRebuild(parentRect);
            }

            MarkSceneDirty(target);
            SceneView.RepaintAll();

            return Response.Success($"Updated LayoutElement for '{target.name}'.", new
            {
                target = BuildGameObjectInfo(target),
                before,
                after = BuildLayoutComponentsInfo(target)
            });
        }

        static object SetGraphic(ManageUIParams parameters)
        {
            var target = ResolveTargetGameObject(parameters.Target);
            if (target == null)
            {
                return Response.Error("SetGraphic requires Target or a selected UI object.");
            }

            var rectTransform = target.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                return Response.Error($"Target '{target.name}' does not have a RectTransform.");
            }

            var before = BuildGraphicInfo(target);
            var planned = BuildGraphicPlan(parameters);
            if (parameters.DryRun ?? false)
            {
                return Response.Success("Dry run: UI Graphic would be added or updated.", new
                {
                    target = BuildGameObjectInfo(target),
                    before,
                    planned
                });
            }

            var graphic = ResolveOrCreateGraphic(target, parameters, out var graphicError);
            if (graphic == null)
            {
                return Response.Error(graphicError ?? $"Target '{target.name}' could not resolve a supported Graphic.");
            }

            Undo.RecordObject(graphic, "Set UniBridge UI Graphic");

            Sprite assignedSprite = null;
            Texture assignedTexture = null;
            Material assignedMaterial = null;

            if (!string.IsNullOrWhiteSpace(parameters.SpritePath))
            {
                assignedSprite = LoadUiAsset<Sprite>(parameters.SpritePath, "Sprite");
                if (assignedSprite == null)
                {
                    return Response.Error($"Sprite asset was not found at '{parameters.SpritePath}'.");
                }

                if (graphic is not Image image)
                {
                    return Response.Error($"SpritePath requires an Image graphic, but '{target.name}' uses {graphic.GetType().Name}.");
                }

                image.sprite = assignedSprite;
            }

            if (!string.IsNullOrWhiteSpace(parameters.TexturePath))
            {
                assignedTexture = LoadUiAsset<Texture>(parameters.TexturePath, "Texture");
                if (assignedTexture == null)
                {
                    return Response.Error($"Texture asset was not found at '{parameters.TexturePath}'.");
                }

                if (graphic is not RawImage rawImage)
                {
                    return Response.Error($"TexturePath requires a RawImage graphic, but '{target.name}' uses {graphic.GetType().Name}.");
                }

                rawImage.texture = assignedTexture;
            }

            if (!string.IsNullOrWhiteSpace(parameters.MaterialPath))
            {
                assignedMaterial = LoadUiAsset<Material>(parameters.MaterialPath, "Material");
                if (assignedMaterial == null)
                {
                    return Response.Error($"Material asset was not found at '{parameters.MaterialPath}'.");
                }

                graphic.material = assignedMaterial;
            }

            if (TryReadGraphicColor(parameters, graphic.color, out var color))
            {
                graphic.color = color;
            }

            if (parameters.RaycastTarget.HasValue)
            {
                graphic.raycastTarget = parameters.RaycastTarget.Value;
            }

            if (graphic is Image configuredImage)
            {
                if (!string.IsNullOrWhiteSpace(parameters.ImageType))
                {
                    configuredImage.type = ParseImageType(parameters.ImageType);
                }

                if (parameters.PreserveAspect.HasValue)
                {
                    configuredImage.preserveAspect = parameters.PreserveAspect.Value;
                }

                if (parameters.SetNativeSize ?? false)
                {
                    configuredImage.SetNativeSize();
                }
            }
            else if (graphic is RawImage configuredRawImage && (parameters.SetNativeSize ?? false))
            {
                configuredRawImage.SetNativeSize();
            }

            ApplySelectableSpriteState(target, parameters);

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = target;
            }

            EditorUtility.SetDirty(graphic);
            MarkSceneDirty(target);
            SceneView.RepaintAll();

            return Response.Success($"Updated UI Graphic on '{target.name}'.", new
            {
                target = BuildGameObjectInfo(target),
                before,
                after = BuildGraphicInfo(target),
                selectable = BuildSelectableInfo(target),
                assigned = new
                {
                    sprite = assignedSprite != null ? BuildAssetReferenceInfo(assignedSprite) : null,
                    texture = assignedTexture != null ? BuildAssetReferenceInfo(assignedTexture) : null,
                    material = assignedMaterial != null ? BuildAssetReferenceInfo(assignedMaterial) : null
                }
            });
        }

        static object SetWorldTextMeshProGraphic(GameObject target, Component textMeshPro, ManageUIParams parameters)
        {
            var before = BuildWorldTextMeshProInfo(target, textMeshPro);
            var planned = BuildGraphicPlan(parameters);
            if (parameters.DryRun ?? false)
            {
                return Response.Success("Dry run: world-space TextMeshPro graphic would be updated.", new
                {
                    target = BuildGameObjectInfo(target),
                    textMeshPro = new
                    {
                        before,
                        planned
                    }
                });
            }

            Undo.RecordObject(textMeshPro, "Set UniBridge World TextMeshPro Graphic");
            if (target.TryGetComponent<Renderer>(out var renderer))
            {
                Undo.RecordObject(renderer, "Set UniBridge World TextMeshPro Graphic");
            }

            Object assignedFontAsset = null;
            Object assignedMaterial = null;
            string materialWarning = null;

            if (!string.IsNullOrWhiteSpace(parameters.FontAssetPath))
            {
                assignedFontAsset = ResolveTextMeshProFontAsset(parameters.FontAssetPath, parameters.CreateTmpFontAssetIfMissing ?? true);
                if (assignedFontAsset == null)
                {
                    return Response.Error($"TextMesh Pro font asset was not found at '{parameters.FontAssetPath}'.");
                }

                assignedMaterial = ResolveTextMeshProFontMaterial(assignedFontAsset);
                ApplyWorldTextMeshProFont(target, textMeshPro, assignedFontAsset, assignedMaterial, out materialWarning);
            }

            if (!string.IsNullOrWhiteSpace(parameters.MaterialPath))
            {
                assignedMaterial = LoadMaterialAsset(parameters.MaterialPath);
                if (assignedMaterial == null)
                {
                    return Response.Error($"Material asset was not found at '{parameters.MaterialPath}'.");
                }

                ApplyWorldTextMeshProMaterial(target, textMeshPro, assignedMaterial as Material);
                materialWarning = null;
            }

            var currentColor = ReadTextMeshProColor(textMeshPro);
            if (TryReadGraphicColor(parameters, currentColor, out var color))
            {
                SetMemberValue(textMeshPro, "color", color);
            }

            ForceTextMeshProMeshUpdate(textMeshPro);
            EditorUtility.SetDirty(textMeshPro);
            if (target.TryGetComponent<Renderer>(out var finalRenderer))
            {
                EditorUtility.SetDirty(finalRenderer);
            }

            MarkSceneDirty(target);
            SceneView.RepaintAll();

            return Response.Success($"Updated world-space TextMeshPro graphic on '{target.name}'.", new
            {
                target = BuildGameObjectInfo(target),
                textMeshPro = new
                {
                    before,
                    after = BuildWorldTextMeshProInfo(target, textMeshPro)
                },
                assigned = new
                {
                    fontAsset = assignedFontAsset != null ? BuildAssetReferenceInfo(assignedFontAsset) : null,
                    material = assignedMaterial != null ? BuildAssetReferenceInfo(assignedMaterial) : null
                },
                warning = materialWarning
            });
        }

        static object SetSelectableTransition(ManageUIParams parameters)
        {
            var target = ResolveTargetGameObject(parameters.Target);
            if (target == null)
            {
                return Response.Error("SetSelectableTransition requires Target or a selected Button/Selectable object.");
            }

            var selectable = target.GetComponent<Selectable>();
            if (selectable == null)
            {
                return Response.Error($"Target '{target.name}' does not have a Selectable component such as Button, Toggle, Slider, or Dropdown.");
            }

            var before = BuildSelectableInfo(target);
            var planned = BuildSelectableTransitionPlan(parameters);
            if (parameters.DryRun ?? false)
            {
                return Response.Success("Dry run: Selectable transition would be updated.", new
                {
                    target = BuildGameObjectInfo(target),
                    before,
                    planned
                });
            }

            Undo.RecordObject(selectable, "Set UniBridge Selectable Transition");

            selectable.transition = ConvertSelectableTransition(parameters.Transition);
            var targetGraphic = ResolveTargetGraphic(target, parameters.TargetGraphic);
            if (!string.IsNullOrWhiteSpace(parameters.TargetGraphic) && targetGraphic == null)
            {
                return Response.Error($"TargetGraphic '{parameters.TargetGraphic}' was not found or does not have a Graphic component.");
            }

            if (targetGraphic != null)
            {
                selectable.targetGraphic = targetGraphic;
            }

            if (HasColorBlockSettings(parameters))
            {
                selectable.colors = ApplyColorBlockSettings(selectable.colors, parameters);
            }

            if (HasSpriteStateSettings(parameters))
            {
                selectable.spriteState = BuildSpriteState(selectable.spriteState, parameters);
            }

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = target;
            }

            EditorUtility.SetDirty(selectable);
            MarkSceneDirty(target);
            SceneView.RepaintAll();

            return Response.Success($"Updated Selectable transition on '{target.name}'.", new
            {
                target = BuildGameObjectInfo(target),
                before,
                after = BuildSelectableInfo(target)
            });
        }

        static object SetButtonEvent(ManageUIParams parameters)
        {
            return ConfigureButtonEvent(parameters, clearOnly: false);
        }

        static object ClearButtonEvents(ManageUIParams parameters)
        {
            return ConfigureButtonEvent(parameters, clearOnly: true);
        }

        static object ConfigureButtonEvent(ManageUIParams parameters, bool clearOnly)
        {
            var target = ResolveTargetGameObject(parameters.Target);
            if (target == null)
            {
                return Response.Error("SetButtonEvent/ClearButtonEvents requires Target or a selected Button object.");
            }

            var button = target.GetComponent<Button>();
            if (button == null)
            {
                return Response.Error($"Target '{target.name}' does not have a Button component.");
            }

            var clearExisting = clearOnly || (parameters.ClearExistingEvents ?? false);
            if (!clearOnly && string.IsNullOrWhiteSpace(parameters.EventMethod))
            {
                return Response.Error("SetButtonEvent requires EventMethod, or use ClearButtonEvents to remove listeners.");
            }

            var before = BuildButtonInfo(target);
            UnityEventPersistentCallUtility.PersistentCallSpec binding = null;
            JObject callObject = null;
            if (!clearOnly)
            {
                callObject = BuildButtonPersistentCall(parameters);
                if (!UnityEventPersistentCallUtility.TryBuildPersistentCallSpec(
                        callObject,
                        target,
                        parameters.EventComponent,
                        out binding,
                        out var bindingError))
                {
                    return Response.Error(bindingError);
                }
            }

            var dryRun = parameters.DryRun ?? false;
            var planned = UnityEventPersistentCallUtility.ApplyPersistentCalls(
                button.onClick,
                binding == null ? Array.Empty<UnityEventPersistentCallUtility.PersistentCallSpec>() : new[] { binding },
                clearExisting,
                dryRun: true);

            if (dryRun)
            {
                return Response.Success("Dry run: Button.onClick persistent listener(s) would be updated.", new
                {
                    target = BuildGameObjectInfo(target),
                    before,
                    planned = new
                    {
                        clearExisting,
                        calls = planned.Calls
                    }
                });
            }

            Undo.RecordObject(button, "Set UniBridge Button Event");
            var apply = UnityEventPersistentCallUtility.ApplyPersistentCalls(
                button.onClick,
                binding == null ? Array.Empty<UnityEventPersistentCallUtility.PersistentCallSpec>() : new[] { binding },
                clearExisting,
                dryRun: false);

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = target;
            }

            EditorUtility.SetDirty(button);
            MarkSceneDirty(target);
            SceneView.RepaintAll();

            return Response.Success(
                clearOnly
                    ? $"Cleared {apply.RemovedCount} Button.onClick listener(s) from '{target.name}'."
                    : $"Updated Button.onClick on '{target.name}'.",
                new
                {
                    target = BuildGameObjectInfo(target),
                    removedCount = apply.RemovedCount,
                    added = clearOnly ? null : UnityEventPersistentCallUtility.BuildPersistentCallSpecInfo(binding),
                    before,
                    after = BuildButtonInfo(target)
                });
        }

        static JObject BuildButtonPersistentCall(ManageUIParams parameters)
        {
            var call = new JObject
            {
                ["methodName"] = parameters.EventMethod?.Trim(),
                ["argumentType"] = parameters.EventArgumentType.ToString(),
                ["callState"] = "RuntimeOnly"
            };

            if (!string.IsNullOrWhiteSpace(parameters.EventTarget))
            {
                call["target"] = parameters.EventTarget.Trim();
            }

            if (!string.IsNullOrWhiteSpace(parameters.EventComponent))
            {
                call["component"] = parameters.EventComponent.Trim();
            }

            if (parameters.EventArgumentType != UIButtonEventArgumentType.Void)
            {
                call["argument"] = parameters.EventArgumentType switch
                {
                    UIButtonEventArgumentType.Int => JToken.FromObject(ParseIntArgument(parameters.EventArgument)),
                    UIButtonEventArgumentType.Float => JToken.FromObject(ParseFloatArgument(parameters.EventArgument)),
                    UIButtonEventArgumentType.Bool => JToken.FromObject(ParseBoolArgument(parameters.EventArgument)),
                    _ => parameters.EventArgument ?? string.Empty
                };
            }

            return call;
        }

        static void ConfigureElement(GameObject element, ManageUIParams parameters)
        {
            switch (parameters.ElementType)
            {
                case UIElementType.Panel:
                    element.AddComponent<Image>().color = ReadColor(parameters.BackgroundColor, new Color(0.12f, 0.14f, 0.18f, 0.85f));
                    break;
                case UIElementType.Image:
                    element.AddComponent<Image>().color = ReadColor(parameters.BackgroundColor, Color.white);
                    break;
                case UIElementType.Text:
                    ConfigureText(
                        element,
                        parameters.Text ?? element.name,
                        ReadColor(parameters.Color, Color.white),
                        parameters.FontSize ?? 24,
                        ConvertTextAnchor(parameters.Alignment),
                        parameters);
                    break;
                case UIElementType.Button:
                    ConfigureButton(element, parameters);
                    break;
                case UIElementType.TextMeshProText:
                    ConfigureTextMeshProText(
                        element,
                        parameters.Text ?? element.name,
                        ReadColor(parameters.Color, Color.white),
                        parameters.FontSize ?? 24,
                        parameters);
                    break;
                case UIElementType.TextMeshProButton:
                    ConfigureTextMeshProButton(element, parameters);
                    break;
                case UIElementType.Toggle:
                    ConfigureToggle(element, parameters);
                    break;
                case UIElementType.Slider:
                    ConfigureSlider(element, parameters);
                    break;
                case UIElementType.Dropdown:
                    ConfigureDropdown(element, parameters);
                    break;
                case UIElementType.InputField:
                    ConfigureInputField(element, parameters);
                    break;
                case UIElementType.Scrollbar:
                    ConfigureScrollbar(element, parameters);
                    break;
                case UIElementType.TextMeshProInputField:
                    ConfigureTextMeshProInputField(element, parameters);
                    break;
                case UIElementType.TextMeshProDropdown:
                    ConfigureTextMeshProDropdown(element, parameters);
                    break;
                case UIElementType.ToggleGroup:
                    ConfigureToggleGroup(element, parameters);
                    break;
            }
        }

        static void ConfigureStretchRect(RectTransform rectTransform)
        {
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.anchoredPosition = Vector2.zero;
            rectTransform.sizeDelta = Vector2.zero;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
        }

        static void ConfigureScrollContentRect(RectTransform contentRect, UIScrollDirection direction, Vector2 viewportSize)
        {
            switch (direction)
            {
                case UIScrollDirection.Horizontal:
                    contentRect.anchorMin = new Vector2(0f, 0f);
                    contentRect.anchorMax = new Vector2(0f, 1f);
                    contentRect.pivot = new Vector2(0f, 0.5f);
                    contentRect.anchoredPosition = Vector2.zero;
                    contentRect.sizeDelta = new Vector2(0f, 0f);
                    break;
                case UIScrollDirection.Both:
                    contentRect.anchorMin = new Vector2(0f, 1f);
                    contentRect.anchorMax = new Vector2(0f, 1f);
                    contentRect.pivot = new Vector2(0f, 1f);
                    contentRect.anchoredPosition = Vector2.zero;
                    contentRect.sizeDelta = new Vector2(Mathf.Max(1f, viewportSize.x), Mathf.Max(1f, viewportSize.y));
                    break;
                default:
                    contentRect.anchorMin = new Vector2(0f, 1f);
                    contentRect.anchorMax = new Vector2(1f, 1f);
                    contentRect.pivot = new Vector2(0.5f, 1f);
                    contentRect.anchoredPosition = Vector2.zero;
                    contentRect.sizeDelta = new Vector2(0f, 0f);
                    break;
            }
        }

        static void ConfigureScrollContentLayout(RectTransform contentRect, ManageUIParams parameters)
        {
            var layoutGroupType = ResolveScrollLayoutGroupType(parameters);
            var componentType = GetLayoutGroupComponentType(layoutGroupType);

            RemoveLayoutGroupsExcept(contentRect.gameObject, componentType);
            var group = GetFirstLayoutGroup(contentRect.gameObject, componentType);
            if (group == null)
            {
                group = (LayoutGroup)contentRect.gameObject.AddComponent(componentType);
            }

            ConfigureDefaultScrollLayoutGroup(group, parameters);
            ApplyLayoutGroupSettings(group, parameters);

            var fitter = contentRect.gameObject.GetComponent<ContentSizeFitter>() ?? contentRect.gameObject.AddComponent<ContentSizeFitter>();
            switch (parameters.ScrollDirection)
            {
                case UIScrollDirection.Horizontal:
                    fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                    fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                    break;
                case UIScrollDirection.Both:
                    fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                    fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                    break;
                default:
                    fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                    break;
            }
        }

        static UILayoutGroupType ResolveScrollLayoutGroupType(ManageUIParams parameters)
        {
            return parameters.ScrollDirection switch
            {
                UIScrollDirection.Horizontal => UILayoutGroupType.Horizontal,
                UIScrollDirection.Both => parameters.LayoutGroupType == UILayoutGroupType.Vertical ? UILayoutGroupType.Grid : parameters.LayoutGroupType,
                _ => parameters.LayoutGroupType
            };
        }

        static void ConfigureDefaultScrollLayoutGroup(LayoutGroup group, ManageUIParams parameters)
        {
            group.childAlignment = parameters.ScrollDirection == UIScrollDirection.Horizontal
                ? TextAnchor.MiddleLeft
                : TextAnchor.UpperCenter;

            group.padding = TryReadRectOffset(parameters.Padding, out var customPadding)
                ? customPadding
                : new RectOffset(12, 12, 12, 12);

            switch (group)
            {
                case HorizontalOrVerticalLayoutGroup horizontalOrVertical:
                    horizontalOrVertical.spacing = TryReadSpacing(parameters.Spacing, out var spacing) ? spacing : 8f;
                    horizontalOrVertical.childControlWidth = true;
                    horizontalOrVertical.childControlHeight = true;
                    horizontalOrVertical.childForceExpandWidth = parameters.ScrollDirection != UIScrollDirection.Horizontal;
                    horizontalOrVertical.childForceExpandHeight = false;
                    break;
                case GridLayoutGroup grid:
                    grid.cellSize = TryReadVector2(parameters.CellSize, out var cellSize)
                        ? cellSize
                        : new Vector2(160f, 52f);
                    grid.spacing = TryReadVector2Flexible(parameters.Spacing, out var gridSpacing)
                        ? gridSpacing
                        : new Vector2(8f, 8f);
                    grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
                    grid.startAxis = GridLayoutGroup.Axis.Horizontal;
                    grid.constraint = GridLayoutGroup.Constraint.Flexible;
                    break;
            }
        }

        static void ConfigureScrollRect(ScrollRect scrollRect, RectTransform viewportRect, RectTransform contentRect, ManageUIParams parameters)
        {
            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;
            scrollRect.horizontal = parameters.ScrollDirection is UIScrollDirection.Horizontal or UIScrollDirection.Both;
            scrollRect.vertical = parameters.ScrollDirection is UIScrollDirection.Vertical or UIScrollDirection.Both;
            scrollRect.movementType = ConvertScrollMovementType(parameters.MovementType);
            scrollRect.inertia = parameters.Inertia ?? true;
            scrollRect.scrollSensitivity = parameters.ScrollSensitivity ?? 30f;
            if (parameters.Elasticity.HasValue)
                scrollRect.elasticity = Mathf.Max(0f, parameters.Elasticity.Value);
            if (parameters.DecelerationRate.HasValue)
                scrollRect.decelerationRate = Mathf.Clamp01(parameters.DecelerationRate.Value);
        }

        static ScrollRect.MovementType ConvertScrollMovementType(UIScrollMovementType movementType)
        {
            return movementType switch
            {
                UIScrollMovementType.Unrestricted => ScrollRect.MovementType.Unrestricted,
                UIScrollMovementType.Elastic => ScrollRect.MovementType.Elastic,
                _ => ScrollRect.MovementType.Clamped
            };
        }

        static UIElementType ResolveScrollItemElementType(ManageUIParams parameters)
        {
            if (parameters.ElementType != UIElementType.Empty)
            {
                return parameters.ElementType;
            }

            return GetTextMeshProUGUIType() != null
                ? UIElementType.TextMeshProText
                : UIElementType.Text;
        }

        static string[] GetRequestedItemTexts(ManageUIParams parameters)
        {
            return parameters.ItemTexts?
                       .Where(text => !string.IsNullOrWhiteSpace(text))
                       .Select(text => text.Trim())
                       .ToArray()
                   ?? Array.Empty<string>();
        }

        static string[] BuildScrollItemNames(ManageUIParams parameters, int count)
        {
            var baseName = parameters.Action == UIAction.CreateScrollView || string.IsNullOrWhiteSpace(parameters.Name)
                ? "Item"
                : parameters.Name.Trim();
            return Enumerable.Range(0, Mathf.Max(0, count))
                .Select(index => count == 1 ? baseName : $"{baseName} {index + 1:00}")
                .ToArray();
        }

        static List<GameObject> CreateScrollItems(RectTransform contentRect, ManageUIParams parameters, string[] itemTexts, UIElementType itemElementType)
        {
            var createdItems = new List<GameObject>();
            if (contentRect == null || itemTexts == null || itemTexts.Length == 0)
            {
                return createdItems;
            }

            var names = BuildScrollItemNames(parameters, itemTexts.Length);
            var scrollDirection = InferScrollDirection(contentRect, parameters);
            for (var index = 0; index < itemTexts.Length; index++)
            {
                var item = CreateScrollItemObject(contentRect, names[index], itemTexts[index], parameters, itemElementType, scrollDirection);
                createdItems.Add(item);
            }

            return createdItems;
        }

        static GameObject CreateScrollItemObject(
            RectTransform contentRect,
            string name,
            string text,
            ManageUIParams parameters,
            UIElementType itemElementType,
            UIScrollDirection scrollDirection)
        {
            var item = new GameObject(name, typeof(RectTransform));
            item.transform.SetParent(contentRect, false);
            GameObjectUtility.EnsureUniqueNameForSibling(item);

            var rectTransform = item.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0f, 1f);
            rectTransform.anchorMax = new Vector2(1f, 1f);
            rectTransform.pivot = new Vector2(0.5f, 1f);
            rectTransform.anchoredPosition = Vector2.zero;
            rectTransform.sizeDelta = ResolveScrollItemSize(parameters, scrollDirection);

            if (parameters.ElementType == UIElementType.Empty)
            {
                ConfigureDefaultScrollRow(item, text, parameters);
            }
            else
            {
                var itemParameters = parameters with
                {
                    ElementType = itemElementType,
                    Text = text
                };
                ConfigureElement(item, itemParameters);
            }

            ConfigureScrollItemLayoutElement(item, parameters, scrollDirection);
            Undo.RegisterCreatedObjectUndo(item, "Create UniBridge Scroll Item");
            return item;
        }

        static Vector2 ResolveScrollItemSize(ManageUIParams parameters, UIScrollDirection scrollDirection)
        {
            if (TryReadVector2(parameters.ItemSizeDelta, out var customSize))
            {
                return customSize;
            }

            if (parameters.Action == UIAction.AddScrollItem && TryReadVector2(parameters.SizeDelta, out customSize))
            {
                return customSize;
            }

            return scrollDirection switch
            {
                UIScrollDirection.Horizontal => new Vector2(180f, 0f),
                UIScrollDirection.Both => TryReadVector2(parameters.CellSize, out var cellSize) ? cellSize : new Vector2(160f, 52f),
                _ => new Vector2(0f, 44f)
            };
        }

        static void ConfigureDefaultScrollRow(GameObject item, string text, ManageUIParams parameters)
        {
            var image = item.AddComponent<Image>();
            image.color = ReadColor(parameters.BackgroundColor, new Color(0.12f, 0.18f, 0.27f, 0.86f));
            image.raycastTarget = true;

            var label = new GameObject("Label", typeof(RectTransform));
            label.transform.SetParent(item.transform, false);
            var labelRect = label.GetComponent<RectTransform>();
            ConfigureStretchRect(labelRect);
            labelRect.offsetMin = new Vector2(12f, 2f);
            labelRect.offsetMax = new Vector2(-12f, -2f);

            var labelParameters = parameters with
            {
                Text = text,
                Alignment = UITextAlignment.MiddleLeft,
                FontSize = parameters.FontSize ?? 18,
                Color = parameters.Color ?? new[] { 0.92f, 0.96f, 1f, 1f },
                ElementType = GetTextMeshProUGUIType() != null ? UIElementType.TextMeshProText : UIElementType.Text
            };

            if (labelParameters.ElementType == UIElementType.TextMeshProText)
            {
                ConfigureTextMeshProText(
                    label,
                    text,
                    ReadColor(labelParameters.Color, Color.white),
                    labelParameters.FontSize ?? 18,
                    labelParameters);
            }
            else
            {
                ConfigureText(
                    label,
                    text,
                    ReadColor(labelParameters.Color, Color.white),
                    labelParameters.FontSize ?? 18,
                    TextAnchor.MiddleLeft,
                    labelParameters);
            }
        }

        static void ConfigureScrollItemLayoutElement(GameObject item, ManageUIParams parameters, UIScrollDirection scrollDirection)
        {
            var layoutElement = item.GetComponent<LayoutElement>() ?? item.AddComponent<LayoutElement>();
            var size = ResolveScrollItemSize(parameters, scrollDirection);

            switch (scrollDirection)
            {
                case UIScrollDirection.Horizontal:
                    layoutElement.preferredWidth = parameters.PreferredWidth ?? Mathf.Max(1f, size.x);
                    layoutElement.flexibleHeight = parameters.FlexibleHeight ?? 1f;
                    break;
                case UIScrollDirection.Both:
                    layoutElement.preferredWidth = parameters.PreferredWidth ?? Mathf.Max(1f, size.x);
                    layoutElement.preferredHeight = parameters.PreferredHeight ?? Mathf.Max(1f, size.y);
                    break;
                default:
                    layoutElement.preferredHeight = parameters.PreferredHeight ?? Mathf.Max(1f, size.y);
                    layoutElement.flexibleWidth = parameters.FlexibleWidth ?? 1f;
                    break;
            }

            ApplyLayoutElementSettings(layoutElement, parameters);
        }

        static UIScrollDirection InferScrollDirection(RectTransform contentRect, ManageUIParams parameters)
        {
            var scrollRect = contentRect.GetComponentInParent<ScrollRect>();
            if (scrollRect != null && scrollRect.content == contentRect)
            {
                if (scrollRect.horizontal && scrollRect.vertical)
                    return UIScrollDirection.Both;
                if (scrollRect.horizontal)
                    return UIScrollDirection.Horizontal;
                if (scrollRect.vertical)
                    return UIScrollDirection.Vertical;
            }

            return parameters.ScrollDirection;
        }

        static RectTransform ResolveScrollContent(GameObject target, string contentName)
        {
            if (target == null)
            {
                return null;
            }

            var preferredName = string.IsNullOrWhiteSpace(contentName) ? "Content" : contentName.Trim();
            var scrollRect = target.GetComponent<ScrollRect>() ?? target.GetComponentInParent<ScrollRect>();
            if (scrollRect != null && scrollRect.content != null)
            {
                return scrollRect.content;
            }

            var targetRect = target.GetComponent<RectTransform>();
            if (targetRect != null && IsLikelyScrollContent(targetRect, preferredName))
            {
                return targetRect;
            }

            return target.GetComponentsInChildren<RectTransform>(true)
                .FirstOrDefault(rect => string.Equals(rect.name, preferredName, StringComparison.OrdinalIgnoreCase));
        }

        static bool IsLikelyScrollContent(RectTransform rectTransform, string contentName)
        {
            return rectTransform != null
                   && (string.Equals(rectTransform.name, contentName, StringComparison.OrdinalIgnoreCase)
                       || rectTransform.GetComponent<LayoutGroup>() != null
                       || rectTransform.GetComponent<ContentSizeFitter>() != null);
        }

        static void RebuildLayout(RectTransform contentRect)
        {
            if (contentRect == null)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
            if (contentRect.parent is RectTransform viewportRect)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(viewportRect);
                if (viewportRect.parent is RectTransform scrollRect)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRect);
                }
            }

            Canvas.ForceUpdateCanvases();
        }

        static void ConfigureButton(GameObject element, ManageUIParams parameters)
        {
            var image = element.AddComponent<Image>();
            image.color = ReadColor(parameters.BackgroundColor, new Color(0.18f, 0.28f, 0.45f, 1f));

            var button = element.AddComponent<Button>();
            button.targetGraphic = image;

            var label = new GameObject("Label", typeof(RectTransform));
            label.transform.SetParent(element.transform, false);
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            ConfigureText(
                label,
                parameters.Text ?? element.name,
                ReadColor(parameters.Color, Color.white),
                parameters.FontSize ?? 22,
                ConvertTextAnchor(parameters.Alignment),
                parameters);
        }

        static void ConfigureText(GameObject element, string text, Color color, int fontSize, TextAnchor alignment, ManageUIParams parameters)
        {
            var textComponent = element.AddComponent<Text>();
            textComponent.text = text;
            textComponent.color = color;
            textComponent.fontSize = fontSize;
            textComponent.alignment = alignment;
            textComponent.raycastTarget = false;
            textComponent.font = GetBuiltinFont();
            if (parameters.RichText.HasValue)
            {
                textComponent.supportRichText = parameters.RichText.Value;
            }

            if (parameters.BestFit ?? false)
            {
                textComponent.resizeTextForBestFit = true;
                textComponent.resizeTextMinSize = Mathf.Max(1, parameters.MinFontSize ?? 12);
                textComponent.resizeTextMaxSize = Mathf.Max(textComponent.resizeTextMinSize, parameters.MaxFontSize ?? fontSize);
            }
        }

        static void ConfigureToggle(GameObject element, ManageUIParams parameters)
        {
            var background = CreateRectChild(element.transform, "Background", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            var backgroundRect = background.GetComponent<RectTransform>();
            backgroundRect.anchoredPosition = new Vector2(14f, 0f);
            backgroundRect.sizeDelta = new Vector2(24f, 24f);
            var backgroundImage = background.AddComponent<Image>();
            backgroundImage.color = ReadColor(parameters.BackgroundColor, new Color(0.16f, 0.19f, 0.25f, 1f));

            var checkmark = CreateRectChild(background.transform, "Checkmark", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var checkmarkRect = checkmark.GetComponent<RectTransform>();
            checkmarkRect.offsetMin = new Vector2(4f, 4f);
            checkmarkRect.offsetMax = new Vector2(-4f, -4f);
            var checkmarkImage = checkmark.AddComponent<Image>();
            checkmarkImage.color = ReadColor(parameters.Color, new Color(0.2f, 0.85f, 0.95f, 1f));

            var label = CreateRectChild(element.transform, "Label", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.offsetMin = new Vector2(42f, 0f);
            labelRect.offsetMax = Vector2.zero;
            ConfigureText(label, parameters.Text ?? element.name, ReadColor(parameters.Color, Color.white), parameters.FontSize ?? 20, TextAnchor.MiddleLeft, parameters);

            var toggle = element.AddComponent<Toggle>();
            toggle.targetGraphic = backgroundImage;
            toggle.graphic = checkmarkImage;
            toggle.isOn = parameters.IsOn ?? false;

            if (!string.IsNullOrWhiteSpace(parameters.ToggleGroup))
            {
                var groupObject = ResolveTargetGameObject(parameters.ToggleGroup);
                var group = groupObject != null ? groupObject.GetComponent<ToggleGroup>() : null;
                if (group != null)
                    toggle.group = group;
            }
        }

        static void ConfigureToggleGroup(GameObject element, ManageUIParams parameters)
        {
            var group = element.AddComponent<ToggleGroup>();
            group.allowSwitchOff = parameters.IsOn ?? false;
        }

        static void ConfigureSlider(GameObject element, ManageUIParams parameters)
        {
            var slider = element.AddComponent<Slider>();
            slider.minValue = parameters.MinValue ?? 0f;
            slider.maxValue = parameters.MaxValue ?? 1f;
            slider.wholeNumbers = parameters.WholeNumbers ?? false;
            slider.value = Mathf.Clamp(ReadControlFloat(parameters.Value, 0.5f), slider.minValue, slider.maxValue);
            slider.direction = parameters.ScrollDirection == UIScrollDirection.Vertical
                ? Slider.Direction.BottomToTop
                : Slider.Direction.LeftToRight;

            var background = CreateRectChild(element.transform, "Background", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var backgroundRect = background.GetComponent<RectTransform>();
            backgroundRect.offsetMin = new Vector2(0f, 8f);
            backgroundRect.offsetMax = new Vector2(0f, -8f);
            var backgroundImage = background.AddComponent<Image>();
            backgroundImage.color = ReadColor(parameters.BackgroundColor, new Color(0.12f, 0.14f, 0.18f, 1f));

            var fillArea = CreateRectChild(element.transform, "Fill Area", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var fillAreaRect = fillArea.GetComponent<RectTransform>();
            fillAreaRect.offsetMin = new Vector2(5f, 8f);
            fillAreaRect.offsetMax = new Vector2(-5f, -8f);

            var fill = CreateRectChild(fillArea.transform, "Fill", Vector2.zero, Vector2.one, new Vector2(0f, 0.5f));
            var fillImage = fill.AddComponent<Image>();
            fillImage.color = ReadColor(parameters.Color, new Color(0.2f, 0.75f, 0.95f, 1f));

            var handleArea = CreateRectChild(element.transform, "Handle Slide Area", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var handleAreaRect = handleArea.GetComponent<RectTransform>();
            handleAreaRect.offsetMin = new Vector2(10f, 0f);
            handleAreaRect.offsetMax = new Vector2(-10f, 0f);

            var handle = CreateRectChild(handleArea.transform, "Handle", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            var handleRect = handle.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(20f, 20f);
            var handleImage = handle.AddComponent<Image>();
            handleImage.color = Color.white;

            slider.fillRect = fill.GetComponent<RectTransform>();
            slider.handleRect = handleRect;
            slider.targetGraphic = handleImage;
        }

        static void ConfigureScrollbar(GameObject element, ManageUIParams parameters)
        {
            var background = element.AddComponent<Image>();
            background.color = ReadColor(parameters.BackgroundColor, new Color(0.12f, 0.14f, 0.18f, 1f));

            var slidingArea = CreateRectChild(element.transform, "Sliding Area", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var handle = CreateRectChild(slidingArea.transform, "Handle", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var handleRect = handle.GetComponent<RectTransform>();
            handleRect.offsetMin = Vector2.zero;
            handleRect.offsetMax = Vector2.zero;
            var handleImage = handle.AddComponent<Image>();
            handleImage.color = ReadColor(parameters.Color, new Color(0.2f, 0.75f, 0.95f, 1f));

            var scrollbar = element.AddComponent<Scrollbar>();
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImage;
            scrollbar.value = Mathf.Clamp01(ReadControlFloat(parameters.Value, 1f));
            scrollbar.size = Mathf.Clamp01(parameters.MaxValue ?? 0.2f);
            scrollbar.direction = parameters.ScrollDirection == UIScrollDirection.Vertical
                ? Scrollbar.Direction.BottomToTop
                : Scrollbar.Direction.LeftToRight;
        }

        static void ConfigureInputField(GameObject element, ManageUIParams parameters)
        {
            var image = element.AddComponent<Image>();
            image.color = ReadColor(parameters.BackgroundColor, new Color(0.08f, 0.1f, 0.14f, 1f));

            var textArea = CreateRectChild(element.transform, "Text Area", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var textAreaRect = textArea.GetComponent<RectTransform>();
            textAreaRect.offsetMin = new Vector2(10f, 6f);
            textAreaRect.offsetMax = new Vector2(-10f, -6f);
            textArea.AddComponent<RectMask2D>();

            var placeholder = CreateRectChild(textArea.transform, "Placeholder", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            ConfigureText(placeholder, parameters.Placeholder ?? "Enter text...", new Color(0.72f, 0.76f, 0.82f, 0.65f), parameters.FontSize ?? 20, TextAnchor.MiddleLeft, parameters);

            var text = CreateRectChild(textArea.transform, "Text", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            ConfigureText(text, parameters.Value ?? parameters.Text ?? string.Empty, ReadColor(parameters.Color, Color.white), parameters.FontSize ?? 20, TextAnchor.MiddleLeft, parameters);

            var input = element.AddComponent<InputField>();
            input.targetGraphic = image;
            input.textComponent = text.GetComponent<Text>();
            input.placeholder = placeholder.GetComponent<Text>();
            input.text = parameters.Value ?? parameters.Text ?? string.Empty;
        }

        static void ConfigureDropdown(GameObject element, ManageUIParams parameters)
        {
            var image = element.AddComponent<Image>();
            image.color = ReadColor(parameters.BackgroundColor, new Color(0.12f, 0.16f, 0.22f, 1f));

            var label = CreateRectChild(element.transform, "Label", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.offsetMin = new Vector2(10f, 0f);
            labelRect.offsetMax = new Vector2(-34f, 0f);
            ConfigureText(label, string.Empty, ReadColor(parameters.Color, Color.white), parameters.FontSize ?? 20, TextAnchor.MiddleLeft, parameters);

            var arrow = CreateRectChild(element.transform, "Arrow", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            var arrowRect = arrow.GetComponent<RectTransform>();
            arrowRect.sizeDelta = new Vector2(28f, 0f);
            arrowRect.anchoredPosition = new Vector2(-14f, 0f);
            ConfigureText(arrow, "v", ReadColor(parameters.Color, Color.white), parameters.FontSize ?? 18, TextAnchor.MiddleCenter, parameters);

            var template = CreateDropdownTemplate(element.transform, parameters, useTextMeshPro: false);
            template.SetActive(false);

            var dropdown = element.AddComponent<Dropdown>();
            dropdown.targetGraphic = image;
            dropdown.captionText = label.GetComponent<Text>();
            dropdown.itemText = template.transform.Find("Viewport/Content/Item/Item Label")?.GetComponent<Text>();
            dropdown.template = template.GetComponent<RectTransform>();
            dropdown.options = BuildDropdownOptions(parameters)
                .Select(option => new Dropdown.OptionData(option))
                .ToList();
            dropdown.value = Mathf.Clamp(ReadControlInt(parameters.Value, 0), 0, Mathf.Max(0, dropdown.options.Count - 1));
            dropdown.RefreshShownValue();
        }

        static void ConfigureTextMeshProInputField(GameObject element, ManageUIParams parameters)
        {
            var inputType = GetTextMeshProInputFieldType();
            if (inputType == null)
                throw new InvalidOperationException("TMPro.TMP_InputField is not available in this project.");

            var image = element.AddComponent<Image>();
            image.color = ReadColor(parameters.BackgroundColor, new Color(0.08f, 0.1f, 0.14f, 1f));

            var textArea = CreateRectChild(element.transform, "Text Area", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var textAreaRect = textArea.GetComponent<RectTransform>();
            textAreaRect.offsetMin = new Vector2(10f, 6f);
            textAreaRect.offsetMax = new Vector2(-10f, -6f);
            textArea.AddComponent<RectMask2D>();

            var placeholder = CreateRectChild(textArea.transform, "Placeholder", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            ConfigureTextMeshProText(placeholder, parameters.Placeholder ?? "Enter text...", new Color(0.72f, 0.76f, 0.82f, 0.65f), parameters.FontSize ?? 20, parameters);

            var text = CreateRectChild(textArea.transform, "Text", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            ConfigureTextMeshProText(text, parameters.Value ?? parameters.Text ?? string.Empty, ReadColor(parameters.Color, Color.white), parameters.FontSize ?? 20, parameters);

            var input = element.AddComponent(inputType);
            SetMemberValue(input, "targetGraphic", image);
            SetMemberValue(input, "textViewport", textAreaRect);
            SetMemberValue(input, "textComponent", GetTextMeshProText(text));
            SetMemberValue(input, "placeholder", GetTextMeshProText(placeholder));
            SetMemberValue(input, "text", parameters.Value ?? parameters.Text ?? string.Empty);
        }

        static void ConfigureTextMeshProDropdown(GameObject element, ManageUIParams parameters)
        {
            var dropdownType = GetTextMeshProDropdownType();
            if (dropdownType == null)
                throw new InvalidOperationException("TMPro.TMP_Dropdown is not available in this project.");

            var image = element.AddComponent<Image>();
            image.color = ReadColor(parameters.BackgroundColor, new Color(0.12f, 0.16f, 0.22f, 1f));

            var label = CreateRectChild(element.transform, "Label", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.offsetMin = new Vector2(10f, 0f);
            labelRect.offsetMax = new Vector2(-34f, 0f);
            ConfigureTextMeshProText(label, string.Empty, ReadColor(parameters.Color, Color.white), parameters.FontSize ?? 20, parameters);

            var arrow = CreateRectChild(element.transform, "Arrow", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            var arrowRect = arrow.GetComponent<RectTransform>();
            arrowRect.sizeDelta = new Vector2(28f, 0f);
            arrowRect.anchoredPosition = new Vector2(-14f, 0f);
            ConfigureTextMeshProText(arrow, "v", ReadColor(parameters.Color, Color.white), parameters.FontSize ?? 18, parameters);

            var template = CreateDropdownTemplate(element.transform, parameters, useTextMeshPro: true);
            template.SetActive(false);

            var dropdown = element.AddComponent(dropdownType);
            SetMemberValue(dropdown, "targetGraphic", image);
            SetMemberValue(dropdown, "captionText", GetTextMeshProText(label));
            var itemLabel = template.transform.Find("Viewport/Content/Item/Item Label")?.gameObject;
            SetMemberValue(dropdown, "itemText", itemLabel != null ? GetTextMeshProText(itemLabel) : null);
            SetMemberValue(dropdown, "template", template.GetComponent<RectTransform>());
            SetTmpDropdownOptions(dropdown, BuildDropdownOptions(parameters));
            SetMemberValue(dropdown, "value", ReadControlInt(parameters.Value, 0));
            InvokeNoArg(dropdown, "RefreshShownValue");
        }

        static GameObject CreateDropdownTemplate(Transform parent, ManageUIParams parameters, bool useTextMeshPro)
        {
            var template = CreateRectChild(parent, "Template", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 1f));
            var templateRect = template.GetComponent<RectTransform>();
            templateRect.anchoredPosition = new Vector2(0f, 2f);
            templateRect.sizeDelta = new Vector2(0f, 150f);
            var templateImage = template.AddComponent<Image>();
            templateImage.color = new Color(0.08f, 0.1f, 0.14f, 0.98f);
            var scrollRect = template.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            var viewport = CreateRectChild(template.transform, "Viewport", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            viewport.AddComponent<RectMask2D>();
            var viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = new Color(1f, 1f, 1f, 0.02f);
            scrollRect.viewport = viewport.GetComponent<RectTransform>();

            var content = CreateRectChild(viewport.transform, "Content", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 32f);
            scrollRect.content = contentRect;

            var item = CreateRectChild(content.transform, "Item", new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f));
            var itemRect = item.GetComponent<RectTransform>();
            itemRect.sizeDelta = new Vector2(0f, 32f);
            var itemToggle = item.AddComponent<Toggle>();
            var itemBackground = item.AddComponent<Image>();
            itemBackground.color = new Color(0.12f, 0.16f, 0.22f, 0.95f);
            itemToggle.targetGraphic = itemBackground;

            var checkmark = CreateRectChild(item.transform, "Item Checkmark", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            var checkmarkRect = checkmark.GetComponent<RectTransform>();
            checkmarkRect.anchoredPosition = new Vector2(14f, 0f);
            checkmarkRect.sizeDelta = new Vector2(16f, 16f);
            var checkmarkImage = checkmark.AddComponent<Image>();
            checkmarkImage.color = new Color(0.2f, 0.85f, 0.95f, 1f);
            itemToggle.graphic = checkmarkImage;

            var label = CreateRectChild(item.transform, "Item Label", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.offsetMin = new Vector2(34f, 0f);
            labelRect.offsetMax = new Vector2(-8f, 0f);
            if (useTextMeshPro)
            {
                ConfigureTextMeshProText(label, "Option", ReadColor(parameters.Color, Color.white), parameters.FontSize ?? 18, parameters);
            }
            else
            {
                ConfigureText(label, "Option", ReadColor(parameters.Color, Color.white), parameters.FontSize ?? 18, TextAnchor.MiddleLeft, parameters);
            }

            return template;
        }

        static GameObject CreateRectChild(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent, false);
            var rect = child.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return child;
        }

        static string[] BuildDropdownOptions(ManageUIParams parameters)
        {
            var options = parameters.Options ?? parameters.ItemTexts;
            return options?
                       .Where(option => !string.IsNullOrWhiteSpace(option))
                       .Select(option => option.Trim())
                       .ToArray()
                   ?? new[] { "Option A", "Option B", "Option C" };
        }

        static float ReadControlFloat(string value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        static int ReadControlInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        static Graphic ResolveOrCreateGraphic(GameObject target, ManageUIParams parameters, out string error)
        {
            error = null;
            var existingGraphics = target.GetComponents<Graphic>().Where(graphic => graphic != null).ToArray();
            var wantsRawImage = !string.IsNullOrWhiteSpace(parameters.TexturePath);
            var wantsImage = !string.IsNullOrWhiteSpace(parameters.SpritePath)
                             || !string.IsNullOrWhiteSpace(parameters.ImageType)
                             || parameters.PreserveAspect.HasValue
                             || ((parameters.SetNativeSize ?? false) && string.IsNullOrWhiteSpace(parameters.TexturePath));

            if (wantsRawImage)
            {
                var rawImage = target.GetComponent<RawImage>();
                if (rawImage != null)
                {
                    return rawImage;
                }

                if (existingGraphics.Length > 0)
                {
                    error = $"TexturePath requires a RawImage target, but '{target.name}' already has {existingGraphics[0].GetType().Name}.";
                    return null;
                }

                return Undo.AddComponent<RawImage>(target);
            }

            if (wantsImage)
            {
                var image = target.GetComponent<Image>();
                if (image != null)
                {
                    return image;
                }

                if (existingGraphics.Length > 0)
                {
                    error = $"Sprite/Image settings require an Image target, but '{target.name}' already has {existingGraphics[0].GetType().Name}.";
                    return null;
                }

                return Undo.AddComponent<Image>(target);
            }

            return existingGraphics.FirstOrDefault() ?? Undo.AddComponent<Image>(target);
        }

        static object BuildGraphicPlan(ManageUIParams parameters)
        {
            return new
            {
                spritePath = parameters.SpritePath,
                texturePath = parameters.TexturePath,
                materialPath = parameters.MaterialPath,
                imageType = parameters.ImageType,
                color = parameters.Color ?? parameters.BackgroundColor,
                preserveAspect = parameters.PreserveAspect,
                raycastTarget = parameters.RaycastTarget,
                setNativeSize = parameters.SetNativeSize ?? false,
                selectableSpriteState = HasSpriteStateSettings(parameters) ? new
                {
                    highlightedSpritePath = parameters.HighlightedSpritePath,
                    pressedSpritePath = parameters.PressedSpritePath,
                    selectedSpritePath = parameters.SelectedSpritePath,
                    disabledSpritePath = parameters.DisabledSpritePath
                } : null
            };
        }

        static bool TryReadGraphicColor(ManageUIParams parameters, Color fallback, out Color color)
        {
            if (parameters.Color != null && parameters.Color.Length >= 3)
            {
                color = ReadColor(parameters.Color, fallback);
                return true;
            }

            if (parameters.BackgroundColor != null && parameters.BackgroundColor.Length >= 3)
            {
                color = ReadColor(parameters.BackgroundColor, fallback);
                return true;
            }

            color = fallback;
            return false;
        }

        static Image.Type ParseImageType(string imageType)
        {
            var normalized = imageType.Replace(" ", string.Empty).Replace("_", string.Empty);
            var match = Enum.GetNames(typeof(Image.Type))
                .FirstOrDefault(name => string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new ArgumentException($"'{imageType}' is not a valid Image.Type value. Use Simple, Sliced, Tiled, or Filled.");
            }

            return (Image.Type)Enum.Parse(typeof(Image.Type), match);
        }

        static Selectable.Transition ConvertSelectableTransition(UISelectableTransitionMode transition)
        {
            return transition switch
            {
                UISelectableTransitionMode.None => Selectable.Transition.None,
                UISelectableTransitionMode.SpriteSwap => Selectable.Transition.SpriteSwap,
                UISelectableTransitionMode.Animation => Selectable.Transition.Animation,
                _ => Selectable.Transition.ColorTint
            };
        }

        static object BuildSelectableTransitionPlan(ManageUIParams parameters)
        {
            return new
            {
                transition = parameters.Transition.ToString(),
                targetGraphic = parameters.TargetGraphic,
                colors = HasColorBlockSettings(parameters) ? new
                {
                    normalColor = parameters.NormalColor,
                    highlightedColor = parameters.HighlightedColor,
                    pressedColor = parameters.PressedColor,
                    selectedColor = parameters.SelectedColor,
                    disabledColor = parameters.DisabledColor,
                    colorMultiplier = parameters.ColorMultiplier,
                    fadeDuration = parameters.FadeDuration
                } : null,
                spriteState = HasSpriteStateSettings(parameters) ? new
                {
                    highlightedSpritePath = parameters.HighlightedSpritePath,
                    pressedSpritePath = parameters.PressedSpritePath,
                    selectedSpritePath = parameters.SelectedSpritePath,
                    disabledSpritePath = parameters.DisabledSpritePath
                } : null
            };
        }

        static Graphic ResolveTargetGraphic(GameObject target, string targetGraphic)
        {
            if (!string.IsNullOrWhiteSpace(targetGraphic))
            {
                var graphicTarget = ResolveTargetGameObject(targetGraphic);
                return graphicTarget != null ? graphicTarget.GetComponent<Graphic>() : null;
            }

            var selectable = target.GetComponent<Selectable>();
            return selectable?.targetGraphic ?? target.GetComponent<Graphic>();
        }

        static bool HasColorBlockSettings(ManageUIParams parameters)
        {
            return parameters.NormalColor != null
                   || parameters.HighlightedColor != null
                   || parameters.PressedColor != null
                   || parameters.SelectedColor != null
                   || parameters.DisabledColor != null
                   || parameters.ColorMultiplier.HasValue
                   || parameters.FadeDuration.HasValue;
        }

        static ColorBlock ApplyColorBlockSettings(ColorBlock colors, ManageUIParams parameters)
        {
            if (parameters.NormalColor != null)
                colors.normalColor = ReadColor(parameters.NormalColor, colors.normalColor);
            if (parameters.HighlightedColor != null)
                colors.highlightedColor = ReadColor(parameters.HighlightedColor, colors.highlightedColor);
            if (parameters.PressedColor != null)
                colors.pressedColor = ReadColor(parameters.PressedColor, colors.pressedColor);
            if (parameters.SelectedColor != null)
                colors.selectedColor = ReadColor(parameters.SelectedColor, colors.selectedColor);
            if (parameters.DisabledColor != null)
                colors.disabledColor = ReadColor(parameters.DisabledColor, colors.disabledColor);
            if (parameters.ColorMultiplier.HasValue)
                colors.colorMultiplier = Mathf.Max(0f, parameters.ColorMultiplier.Value);
            if (parameters.FadeDuration.HasValue)
                colors.fadeDuration = Mathf.Max(0f, parameters.FadeDuration.Value);
            return colors;
        }

        static bool HasSpriteStateSettings(ManageUIParams parameters)
        {
            return !string.IsNullOrWhiteSpace(parameters.HighlightedSpritePath)
                   || !string.IsNullOrWhiteSpace(parameters.PressedSpritePath)
                   || !string.IsNullOrWhiteSpace(parameters.SelectedSpritePath)
                   || !string.IsNullOrWhiteSpace(parameters.DisabledSpritePath);
        }

        static void ApplySelectableSpriteState(GameObject target, ManageUIParams parameters)
        {
            if (!HasSpriteStateSettings(parameters))
            {
                return;
            }

            var selectable = target.GetComponent<Selectable>();
            if (selectable == null)
            {
                return;
            }

            Undo.RecordObject(selectable, "Set UniBridge Selectable Sprite State");
            selectable.spriteState = BuildSpriteState(selectable.spriteState, parameters);
            EditorUtility.SetDirty(selectable);
        }

        static SpriteState BuildSpriteState(SpriteState current, ManageUIParams parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameters.HighlightedSpritePath))
            {
                current.highlightedSprite = LoadRequiredSprite(parameters.HighlightedSpritePath, nameof(parameters.HighlightedSpritePath));
            }

            if (!string.IsNullOrWhiteSpace(parameters.PressedSpritePath))
            {
                current.pressedSprite = LoadRequiredSprite(parameters.PressedSpritePath, nameof(parameters.PressedSpritePath));
            }

            if (!string.IsNullOrWhiteSpace(parameters.SelectedSpritePath))
            {
                current.selectedSprite = LoadRequiredSprite(parameters.SelectedSpritePath, nameof(parameters.SelectedSpritePath));
            }

            if (!string.IsNullOrWhiteSpace(parameters.DisabledSpritePath))
            {
                current.disabledSprite = LoadRequiredSprite(parameters.DisabledSpritePath, nameof(parameters.DisabledSpritePath));
            }

            return current;
        }

        static Sprite LoadRequiredSprite(string pathOrGuid, string fieldName)
        {
            var sprite = LoadUiAsset<Sprite>(pathOrGuid, "Sprite");
            if (sprite == null)
            {
                throw new InvalidOperationException($"{fieldName} sprite asset was not found at '{pathOrGuid}'.");
            }

            return sprite;
        }

        static T LoadUiAsset<T>(string pathOrGuid, string label) where T : Object
        {
            var path = ResolveAssetPathOrGuid(pathOrGuid);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null)
            {
                return asset;
            }

            if (typeof(T) == typeof(Sprite))
            {
                return AssetDatabase.LoadAllAssetsAtPath(path).OfType<T>().FirstOrDefault();
            }

            return null;
        }

        static string ResolveAssetPathOrGuid(string pathOrGuid)
        {
            if (string.IsNullOrWhiteSpace(pathOrGuid))
            {
                return null;
            }

            var trimmed = pathOrGuid.Trim().Replace('\\', '/');
            var guidPath = AssetDatabase.GUIDToAssetPath(trimmed);
            if (!string.IsNullOrWhiteSpace(guidPath))
            {
                return guidPath;
            }

            return trimmed;
        }

        static int ParseIntArgument(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new FormatException($"EventArgument '{value}' is not a valid Int value.");
        }

        static float ParseFloatArgument(string value)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new FormatException($"EventArgument '{value}' is not a valid Float value.");
        }

        static bool ParseBoolArgument(string value)
        {
            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                return numeric != 0;
            }

            throw new FormatException($"EventArgument '{value}' is not a valid Bool value.");
        }

        static void ConfigureTextMeshProButton(GameObject element, ManageUIParams parameters)
        {
            var image = element.AddComponent<Image>();
            image.color = ReadColor(parameters.BackgroundColor, new Color(0.18f, 0.28f, 0.45f, 1f));

            var button = element.AddComponent<Button>();
            button.targetGraphic = image;

            var label = new GameObject("Label", typeof(RectTransform));
            label.transform.SetParent(element.transform, false);
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            ConfigureTextMeshProText(
                label,
                parameters.Text ?? element.name,
                ReadColor(parameters.Color, Color.white),
                parameters.FontSize ?? 22,
                parameters);
        }

        static void ConfigureTextMeshProText(GameObject element, string text, Color color, int fontSize, ManageUIParams parameters)
        {
            var textMeshProType = GetTextMeshProUGUIType();
            if (textMeshProType == null)
            {
                throw new InvalidOperationException("TMPro.TextMeshProUGUI is not available in this project.");
            }

            var component = element.AddComponent(textMeshProType);
            SetMemberValue(component, "text", text);
            SetMemberValue(component, "fontSize", (float)Mathf.Max(1, fontSize));
            SetMemberValue(component, "color", color);
            SetEnumMemberValue(component, "alignment", MapTextMeshProAlignment(parameters.Alignment));

            if (component is Graphic graphic)
            {
                graphic.color = color;
                graphic.raycastTarget = false;
            }
            else
            {
                SetMemberValue(component, "raycastTarget", false);
            }

            if (parameters.RichText.HasValue)
            {
                SetMemberValue(component, "richText", parameters.RichText.Value);
            }

            if (parameters.BestFit ?? false)
            {
                var minSize = Mathf.Max(1, parameters.MinFontSize ?? 12);
                var maxSize = Mathf.Max(minSize, parameters.MaxFontSize ?? fontSize);
                SetMemberValue(component, "enableAutoSizing", true);
                SetMemberValue(component, "fontSizeMin", (float)minSize);
                SetMemberValue(component, "fontSizeMax", (float)maxSize);
            }

            if (!string.IsNullOrWhiteSpace(parameters.OverflowMode))
            {
                SetEnumMemberValue(component, "overflowMode", parameters.OverflowMode.Trim());
            }

            var fontAsset = ResolveTextMeshProFontAsset(parameters.FontAssetPath, parameters.CreateTmpFontAssetIfMissing ?? true);
            if (fontAsset != null)
            {
                SetMemberValue(component, "font", fontAsset);
            }
        }

        static bool IsTextMeshProElement(UIElementType elementType)
        {
            return elementType == UIElementType.TextMeshProText
                   || elementType == UIElementType.TextMeshProButton
                   || elementType == UIElementType.TextMeshProInputField
                   || elementType == UIElementType.TextMeshProDropdown;
        }

        static Type GetTextMeshProUGUIType()
        {
            return FindLoadedType("TMPro.TextMeshProUGUI");
        }

        static Type GetTextMeshProInputFieldType()
        {
            return FindLoadedType("TMPro.TMP_InputField");
        }

        static Type GetTextMeshProDropdownType()
        {
            return FindLoadedType("TMPro.TMP_Dropdown");
        }

        static Type GetTextMeshProFontAssetType()
        {
            return FindLoadedType("TMPro.TMP_FontAsset");
        }

        static bool HasTextMeshProText(GameObject gameObject)
        {
            var textMeshProType = GetTextMeshProUGUIType();
            return textMeshProType != null && gameObject.GetComponent(textMeshProType) != null;
        }

        static Component GetTextMeshProText(GameObject gameObject)
        {
            var textMeshProType = GetTextMeshProUGUIType();
            return textMeshProType != null ? gameObject.GetComponent(textMeshProType) : null;
        }

        static Component GetWorldTextMeshProText(GameObject gameObject)
        {
            var textMeshProType = FindLoadedType("TMPro.TextMeshPro");
            return textMeshProType != null ? gameObject.GetComponent(textMeshProType) : null;
        }

        static void ApplyWorldTextMeshProFont(
            GameObject target,
            Component textMeshPro,
            Object fontAsset,
            Object material,
            out string materialWarning)
        {
            materialWarning = null;
            using var serializedObject = new SerializedObject(textMeshPro);
            serializedObject.Update();

            var fontProperty = serializedObject.FindProperty("m_fontAsset");
            if (fontProperty == null || !fontProperty.editable)
            {
                throw new InvalidOperationException("World-space TextMeshPro component did not expose editable serialized property 'm_fontAsset'.");
            }

            fontProperty.objectReferenceValue = fontAsset;

            var sharedMaterialProperty = serializedObject.FindProperty("m_sharedMaterial");
            if (sharedMaterialProperty != null && sharedMaterialProperty.editable && material != null)
            {
                sharedMaterialProperty.objectReferenceValue = material;
            }
            else if (sharedMaterialProperty != null && material == null)
            {
                materialWarning = "TMP font asset was changed, but no matching shared material could be resolved from the font asset; m_sharedMaterial was left unchanged.";
            }
            else if (sharedMaterialProperty == null)
            {
                materialWarning = "TMP font asset was changed, but this TMP component did not expose m_sharedMaterial; material was left unchanged.";
            }

            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();

            SetMemberValue(textMeshPro, "font", fontAsset);
            if (material is Material materialAsset)
            {
                ApplyWorldTextMeshProMaterial(target, textMeshPro, materialAsset);
            }
        }

        static void ApplyWorldTextMeshProMaterial(GameObject target, Component textMeshPro, Material material)
        {
            if (material == null)
            {
                return;
            }

            using var serializedObject = new SerializedObject(textMeshPro);
            serializedObject.Update();
            var sharedMaterialProperty = serializedObject.FindProperty("m_sharedMaterial");
            if (sharedMaterialProperty != null && sharedMaterialProperty.editable)
            {
                sharedMaterialProperty.objectReferenceValue = material;
                serializedObject.ApplyModifiedProperties();
            }

            SetMemberValue(textMeshPro, "fontSharedMaterial", material);
            if (target != null && target.TryGetComponent<Renderer>(out var renderer))
            {
                renderer.sharedMaterial = material;
                EditorUtility.SetDirty(renderer);
            }
        }

        static Object ResolveTextMeshProFontMaterial(Object fontAsset)
        {
            var reflected = GetMemberValue(fontAsset, "material") as Object
                            ?? GetMemberValue(fontAsset, "material_EditorRef") as Object
                            ?? GetMemberValue(fontAsset, "m_Material") as Object;
            if (reflected is Material)
            {
                return reflected;
            }

            var assetPath = AssetDatabase.GetAssetPath(fontAsset);
            return string.IsNullOrWhiteSpace(assetPath)
                ? null
                : AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Material>().FirstOrDefault();
        }

        static Material LoadMaterialAsset(string pathOrGuid)
        {
            var path = ResolveAssetPathOrGuid(pathOrGuid);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<Material>(path)
                   ?? AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().FirstOrDefault();
        }

        static Color ReadTextMeshProColor(Component textMeshPro)
        {
            var value = GetMemberValue(textMeshPro, "color");
            return value is Color color ? color : Color.white;
        }

        static string MapTextMeshProAlignment(UITextAlignment alignment)
        {
            return alignment switch
            {
                UITextAlignment.UpperLeft => "TopLeft",
                UITextAlignment.UpperCenter => "Top",
                UITextAlignment.UpperRight => "TopRight",
                UITextAlignment.MiddleLeft => "Left",
                UITextAlignment.MiddleRight => "Right",
                UITextAlignment.LowerLeft => "BottomLeft",
                UITextAlignment.LowerCenter => "Bottom",
                UITextAlignment.LowerRight => "BottomRight",
                _ => "Center"
            };
        }

        static bool SetMemberValue(object target, string memberName, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName))
            {
                return false;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = target.GetType().GetProperty(memberName, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, CoerceValue(value, property.PropertyType));
                return true;
            }

            var field = target.GetType().GetField(memberName, flags);
            if (field != null)
            {
                field.SetValue(target, CoerceValue(value, field.FieldType));
                return true;
            }

            return false;
        }

        static object GetMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = target.GetType().GetProperty(memberName, flags);
            if (property != null && property.CanRead)
            {
                return property.GetValue(target);
            }

            var field = target.GetType().GetField(memberName, flags);
            return field != null ? field.GetValue(target) : null;
        }

        static void InvokeNoArg(object target, string methodName)
        {
            var method = target?.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            method?.Invoke(target, null);
        }

        static bool SetTmpDropdownOptions(object dropdown, string[] optionTexts)
        {
            if (dropdown == null)
                return false;

            var optionsProperty = dropdown.GetType().GetProperty("options", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var optionDataType = dropdown.GetType().GetNestedType("OptionData", BindingFlags.Public | BindingFlags.NonPublic)
                                ?? FindLoadedType("TMPro.TMP_Dropdown+OptionData");
            if (optionsProperty == null || optionDataType == null)
                return false;

            var listType = typeof(List<>).MakeGenericType(optionDataType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType);
            foreach (var text in optionTexts ?? Array.Empty<string>())
            {
                var option = Activator.CreateInstance(optionDataType);
                SetMemberValue(option, "text", text);
                list.Add(option);
            }

            optionsProperty.SetValue(dropdown, list);
            return true;
        }

        static float ReadFloatMember(object target, string memberName)
        {
            var value = GetMemberValue(target, memberName);
            return value is IConvertible
                ? Convert.ToSingle(value, CultureInfo.InvariantCulture)
                : 0f;
        }

        static bool ReadBoolMember(object target, string memberName)
        {
            var value = GetMemberValue(target, memberName);
            return value is bool boolValue && boolValue;
        }

        static void ForceTextMeshProMeshUpdate(Component textMeshPro)
        {
            var method = textMeshPro?.GetType().GetMethod(
                "ForceMeshUpdate",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
            if (method != null)
            {
                method.Invoke(textMeshPro, null);
            }
            else
            {
                method = textMeshPro?.GetType().GetMethod(
                    "ForceMeshUpdate",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(bool), typeof(bool) },
                    null);
                method?.Invoke(textMeshPro, new object[] { true, false });
            }
        }

        static bool IsTextMeshProOverflowing(Component textMeshPro)
        {
            return ReadBoolMember(textMeshPro, "isTextOverflowing")
                   || ReadBoolMember(textMeshPro, "isTextTruncated");
        }

        static bool SetEnumMemberValue(object target, string memberName, string enumName)
        {
            if (target == null || string.IsNullOrWhiteSpace(enumName))
            {
                return false;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var memberType = target.GetType().GetProperty(memberName, flags)?.PropertyType
                             ?? target.GetType().GetField(memberName, flags)?.FieldType;
            if (memberType == null || !memberType.IsEnum)
            {
                return false;
            }

            var normalized = enumName.Replace(" ", string.Empty).Replace("_", string.Empty);
            var match = Enum.GetNames(memberType)
                .FirstOrDefault(name => string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new ArgumentException($"'{enumName}' is not a valid {memberType.Name} value.");
            }

            return SetMemberValue(target, memberName, Enum.Parse(memberType, match));
        }

        static object CoerceValue(object value, Type targetType)
        {
            if (value == null || targetType == null)
            {
                return value;
            }

            var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullableType.IsInstanceOfType(value))
            {
                return value;
            }

            if (nonNullableType.IsEnum && value is string enumText)
            {
                return Enum.Parse(nonNullableType, enumText, ignoreCase: true);
            }

            if (nonNullableType == typeof(float) && value is IConvertible)
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }

            if (nonNullableType == typeof(int) && value is IConvertible)
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            if (nonNullableType == typeof(bool) && value is IConvertible)
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }

            return value;
        }

        static Object ResolveTextMeshProFontAsset(string fontAssetPath, bool createIfMissing)
        {
            var fontAssetType = GetTextMeshProFontAssetType();
            if (fontAssetType == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(fontAssetPath))
            {
                var explicitFontAsset = AssetDatabase.LoadAssetAtPath(fontAssetPath.Trim(), fontAssetType);
                if (explicitFontAsset == null)
                {
                    throw new InvalidOperationException($"TextMesh Pro font asset was not found at '{fontAssetPath}'.");
                }

                return explicitFontAsset;
            }

            var existingPath = AssetDatabase.FindAssets("t:TMP_FontAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path.IndexOf("Fallback", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                .ThenBy(path => path.IndexOf("LiberationSans SDF.asset", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .ThenBy(path => path.IndexOf("Liberation", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .ThenBy(path => path.IndexOf("SDF", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(existingPath))
            {
                return AssetDatabase.LoadAssetAtPath(existingPath, fontAssetType);
            }

            return createIfMissing ? CreateDefaultTextMeshProFontAsset(fontAssetType) : null;
        }

        static Object CreateDefaultTextMeshProFontAsset(Type fontAssetType)
        {
            const string folder = "Assets/UniBridgeGenerated/TextMeshPro";
            const string assetPath = folder + "/UniBridge Default TMP Font.asset";

            var existing = AssetDatabase.LoadAssetAtPath(assetPath, fontAssetType);
            if (existing != null)
            {
                return existing;
            }

            var font = GetBuiltinFont();
            if (font == null)
            {
                return null;
            }

            var method = fontAssetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(candidate => candidate.Name == "CreateFontAsset")
                .OrderBy(candidate => candidate.GetParameters().Length)
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length >= 1 && parameters[0].ParameterType == typeof(Font);
                });
            if (method == null)
            {
                return null;
            }

            var arguments = BuildDefaultArguments(method.GetParameters(), font);
            var fontAsset = method.Invoke(null, arguments) as Object;
            if (fontAsset == null)
            {
                return null;
            }

            fontAsset.name = "UniBridge Default TMP Font";
            EnsureAssetFolder(folder);
            AssetDatabase.CreateAsset(fontAsset, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath);
            return AssetDatabase.LoadAssetAtPath(assetPath, fontAssetType) ?? fontAsset;
        }

        static object[] BuildDefaultArguments(ParameterInfo[] parameters, Font font)
        {
            var arguments = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameterType = parameters[i].ParameterType;
                if (i == 0 && parameterType == typeof(Font))
                {
                    arguments[i] = font;
                }
                else if (parameters[i].HasDefaultValue)
                {
                    arguments[i] = parameters[i].DefaultValue;
                }
                else if (parameterType == typeof(int))
                {
                    arguments[i] = parameters[i].Name != null && parameters[i].Name.IndexOf("atlas", StringComparison.OrdinalIgnoreCase) >= 0
                        ? 1024
                        : 90;
                }
                else if (parameterType == typeof(bool))
                {
                    arguments[i] = true;
                }
                else if (parameterType.IsEnum)
                {
                    arguments[i] = Enum.GetValues(parameterType).GetValue(0);
                }
                else
                {
                    arguments[i] = parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
                }
            }

            return arguments;
        }

        static void EnsureAssetFolder(string folder)
        {
            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        static GameObject ResolveParentForCreate(ManageUIParams parameters)
        {
            var explicitParent = !string.IsNullOrWhiteSpace(parameters.Parent)
                ? ResolveTargetGameObject(parameters.Parent)
                : null;
            if (explicitParent != null)
            {
                return explicitParent;
            }

            var targetParent = !string.IsNullOrWhiteSpace(parameters.Target)
                ? ResolveTargetGameObject(parameters.Target)
                : null;
            if (targetParent != null)
            {
                return targetParent;
            }

            var selected = Selection.activeGameObject;
            if (selected != null && selected.GetComponent<RectTransform>() != null)
            {
                return selected;
            }

            return UnityApiAdapter.FindObjectsByType(typeof(Canvas), FindObjectsInactive.Include)
                .OfType<Canvas>()
                .FirstOrDefault(canvas => IsEditableSceneObject(canvas.gameObject))
                ?.gameObject;
        }

        static GameObject ResolveExplicitParentForCreate(ManageUIParams parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameters.Parent))
            {
                return ResolveTargetGameObject(parameters.Parent);
            }

            if (!string.IsNullOrWhiteSpace(parameters.Target))
            {
                return ResolveTargetGameObject(parameters.Target);
            }

            return null;
        }

        static GameObject ResolveTargetGameObject(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                if (Selection.activeGameObject != null)
                {
                    return Selection.activeGameObject;
                }

                if (Selection.activeObject is Component component)
                {
                    return component.gameObject;
                }

                return null;
            }

            target = target.Trim();
            return SceneObjectLocator.FindObjects(target, "by_id_or_name_or_path", new SceneObjectLocator.Options
                {
                    IncludeInactive = true,
                    IncludePrefabStage = true,
                    EditableSceneObjectsOnly = true,
                    MatchContainsFallback = false
                })
                .FirstOrDefault(IsEditableSceneObject);
        }

        static bool IsEditableSceneObject(GameObject gameObject)
        {
            return SceneObjectLocator.IsEditableSceneObject(gameObject);
        }

        static List<GameObject> ResolveAuditRoots(string target, bool includeInactive)
        {
            var roots = new List<GameObject>();
            var explicitTarget = ResolveTargetGameObject(target);
            if (explicitTarget != null)
            {
                roots.Add(explicitTarget);
                return roots;
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                return roots;
            }

            if (Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<RectTransform>() != null)
            {
                roots.Add(Selection.activeGameObject);
                return roots;
            }

            roots.AddRange(UnityApiAdapter.FindObjectsByType(typeof(Canvas), includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude)
                .OfType<Canvas>()
                .Where(canvas => IsEditableSceneObject(canvas.gameObject))
                .Where(canvas => includeInactive || canvas.gameObject.activeInHierarchy)
                .OrderBy(canvas => canvas.sortingOrder)
                .Select(canvas => canvas.gameObject));
            return roots.Distinct().ToList();
        }

        static Camera ResolveCamera(string target)
        {
            if (!string.IsNullOrWhiteSpace(target))
            {
                var gameObject = ResolveTargetGameObject(target);
                var camera = gameObject != null ? gameObject.GetComponent<Camera>() : null;
                if (camera != null)
                {
                    return camera;
                }
            }

            if (Camera.main != null)
            {
                return Camera.main;
            }

            return UnityApiAdapter.FindObjectsByType(typeof(Camera), FindObjectsInactive.Include).OfType<Camera>().FirstOrDefault();
        }

        static RenderMode ConvertRenderMode(CanvasRenderModeOption renderMode)
        {
            return renderMode switch
            {
                CanvasRenderModeOption.ScreenSpaceCamera => RenderMode.ScreenSpaceCamera,
                CanvasRenderModeOption.WorldSpace => RenderMode.WorldSpace,
                _ => RenderMode.ScreenSpaceOverlay
            };
        }

        static (GameObject canvasObject, Canvas canvas) CreateCanvasObject(
            string name,
            CanvasRenderModeOption renderMode,
            string cameraTarget,
            bool ensureEventSystem,
            bool overrideSorting,
            int sortingOrder)
        {
            var canvasObject = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            GameObjectUtility.EnsureUniqueNameForSibling(canvasObject);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = ConvertRenderMode(renderMode);
            canvas.overrideSorting = overrideSorting;
            canvas.sortingOrder = sortingOrder;
            if (renderMode == CanvasRenderModeOption.ScreenSpaceCamera)
            {
                canvas.worldCamera = ResolveCamera(cameraTarget);
            }

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            Undo.RegisterCreatedObjectUndo(canvasObject, "Create UniBridge UI Canvas");

            if (ensureEventSystem)
            {
                EnsureEventSystemObject();
            }

            MarkSceneDirty(canvasObject);
            return (canvasObject, canvas);
        }

        static GameObject EnsureEventSystemObject()
        {
            var existing = FindEventSystem();
            if (existing != null)
            {
                EnsureCompatibleInputModule(existing);
                return existing;
            }

            var eventSystem = new GameObject("EventSystem", typeof(EventSystem));
            GameObjectUtility.EnsureUniqueNameForSibling(eventSystem);
            Undo.RegisterCreatedObjectUndo(eventSystem, "Create UniBridge EventSystem");
            EnsureCompatibleInputModule(eventSystem);
            MarkSceneDirty(eventSystem);
            return eventSystem;
        }

        static void EnsureCompatibleInputModule(GameObject eventSystem)
        {
            if (eventSystem == null)
            {
                return;
            }

            var inputSystemModuleType = FindLoadedType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
            var desiredModuleType = ShouldUseInputSystemUiInputModule(inputSystemModuleType)
                ? inputSystemModuleType
                : typeof(StandaloneInputModule);

            if (desiredModuleType != typeof(StandaloneInputModule))
            {
                var standaloneModule = eventSystem.GetComponent<StandaloneInputModule>();
                if (standaloneModule != null)
                {
                    Undo.DestroyObjectImmediate(standaloneModule);
                }
            }
            else if (inputSystemModuleType != null)
            {
                var inputSystemModule = eventSystem.GetComponent(inputSystemModuleType);
                if (inputSystemModule != null)
                {
                    Undo.DestroyObjectImmediate(inputSystemModule);
                }
            }

            if (eventSystem.GetComponent(desiredModuleType) == null)
            {
                Undo.AddComponent(eventSystem, desiredModuleType);
                MarkSceneDirty(eventSystem);
            }
        }

        static bool ShouldUseInputSystemUiInputModule(Type inputSystemModuleType)
        {
            if (inputSystemModuleType == null)
            {
                return false;
            }

            var activeInputHandlingProperty = typeof(PlayerSettings).GetProperty(
                "activeInputHandling",
                BindingFlags.Public | BindingFlags.Static);
            var activeInputHandling = activeInputHandlingProperty?.GetValue(null)?.ToString();
            if (activeInputHandling != null
                && activeInputHandling.IndexOf("InputManager", StringComparison.OrdinalIgnoreCase) >= 0
                && activeInputHandling.IndexOf("Both", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            return true;
        }

        static Type FindLoadedType(string fullName)
        {
            var type = Type.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        static GameObject FindEventSystem()
        {
            var eventSystem = UnityApiAdapter.FindObjectsByType(typeof(EventSystem), FindObjectsInactive.Include)
                .OfType<EventSystem>()
                .FirstOrDefault(system => IsEditableSceneObject(system.gameObject));
            return eventSystem != null ? eventSystem.gameObject : null;
        }

        static string DefaultElementName(UIElementType elementType)
        {
            return elementType switch
            {
                UIElementType.Panel => "Panel",
                UIElementType.Image => "Image",
                UIElementType.Text => "Text",
                UIElementType.Button => "Button",
                UIElementType.TextMeshProText => "TMP Text",
                UIElementType.TextMeshProButton => "TMP Button",
                UIElementType.Toggle => "Toggle",
                UIElementType.Slider => "Slider",
                UIElementType.Dropdown => "Dropdown",
                UIElementType.InputField => "Input Field",
                UIElementType.Scrollbar => "Scrollbar",
                UIElementType.TextMeshProInputField => "TMP Input Field",
                UIElementType.TextMeshProDropdown => "TMP Dropdown",
                UIElementType.ToggleGroup => "Toggle Group",
                _ => "UI Element"
            };
        }

        static float PivotForHorizontal(RectLayoutHorizontal layout)
        {
            return layout switch
            {
                RectLayoutHorizontal.Left => 0f,
                RectLayoutHorizontal.Right => 1f,
                _ => 0.5f
            };
        }

        static float PivotForVertical(RectLayoutVertical layout)
        {
            return layout switch
            {
                RectLayoutVertical.Top => 1f,
                RectLayoutVertical.Bottom => 0f,
                _ => 0.5f
            };
        }

        static float AnchorMinForHorizontal(RectLayoutHorizontal layout)
        {
            return layout == RectLayoutHorizontal.Stretch ? 0f : PivotForHorizontal(layout);
        }

        static float AnchorMaxForHorizontal(RectLayoutHorizontal layout)
        {
            return layout == RectLayoutHorizontal.Stretch ? 1f : PivotForHorizontal(layout);
        }

        static float AnchorMinForVertical(RectLayoutVertical layout)
        {
            return layout == RectLayoutVertical.Stretch ? 0f : PivotForVertical(layout);
        }

        static float AnchorMaxForVertical(RectLayoutVertical layout)
        {
            return layout == RectLayoutVertical.Stretch ? 1f : PivotForVertical(layout);
        }

        static bool TryReadVector2(float[] values, out Vector2 vector)
        {
            vector = default;
            if (values == null || values.Length < 2)
            {
                return false;
            }

            vector = new Vector2(values[0], values[1]);
            return true;
        }

        static bool TryReadVector3(float[] values, out Vector3 vector)
        {
            vector = default;
            if (values == null || values.Length < 2)
            {
                return false;
            }

            vector = values.Length >= 3
                ? new Vector3(values[0], values[1], values[2])
                : new Vector3(values[0], values[1], 1f);
            return true;
        }

        static Color ReadColor(float[] values, Color fallback)
        {
            if (values == null || values.Length < 3)
            {
                return fallback;
            }

            var max = values.Take(Math.Min(values.Length, 4)).Max();
            var scale = max > 1f ? 255f : 1f;
            return new Color(
                Mathf.Clamp01(values[0] / scale),
                Mathf.Clamp01(values[1] / scale),
                Mathf.Clamp01(values[2] / scale),
                values.Length >= 4 ? Mathf.Clamp01(values[3] / scale) : fallback.a);
        }

        static TextAnchor ConvertTextAnchor(UITextAlignment alignment)
        {
            return alignment switch
            {
                UITextAlignment.UpperLeft => TextAnchor.UpperLeft,
                UITextAlignment.UpperCenter => TextAnchor.UpperCenter,
                UITextAlignment.UpperRight => TextAnchor.UpperRight,
                UITextAlignment.MiddleLeft => TextAnchor.MiddleLeft,
                UITextAlignment.MiddleRight => TextAnchor.MiddleRight,
                UITextAlignment.LowerLeft => TextAnchor.LowerLeft,
                UITextAlignment.LowerCenter => TextAnchor.LowerCenter,
                UITextAlignment.LowerRight => TextAnchor.LowerRight,
                _ => TextAnchor.MiddleCenter
            };
        }

        static object BuildDirectRectPlan(ManageUIParams parameters)
        {
            return new
            {
                anchorMin = parameters.AnchorMin,
                anchorMax = parameters.AnchorMax,
                pivot = parameters.Pivot,
                anchoredPosition = parameters.AnchoredPosition,
                sizeDelta = parameters.SizeDelta,
                offsetMin = parameters.OffsetMin,
                offsetMax = parameters.OffsetMax,
                localScale = parameters.LocalScale,
                maintainWorldPosition = parameters.MaintainWorldPosition ?? true
            };
        }

        static object BuildGraphicInfo(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var graphics = gameObject.GetComponents<Graphic>()
                .Where(graphic => graphic != null)
                .Select(BuildGraphicComponentInfo)
                .ToArray();

            return graphics.Length == 0
                ? null
                : new
                {
                    primary = graphics[0],
                    graphics
                };
        }

        static object BuildGraphicComponentInfo(Graphic graphic)
        {
            var data = new Dictionary<string, object>
            {
                ["type"] = graphic.GetType().Name,
                ["fullType"] = graphic.GetType().FullName,
                ["enabled"] = graphic.enabled,
                ["color"] = ToArray((Vector4)graphic.color),
                ["material"] = BuildAssetReferenceInfo(graphic.material),
                ["raycastTarget"] = graphic.raycastTarget,
                ["raycastPadding"] = ToArray(graphic.raycastPadding),
                ["maskable"] = graphic is MaskableGraphic maskableGraphic && maskableGraphic.maskable
            };

            if (graphic is Image image)
            {
                data["image"] = new
                {
                    sprite = BuildAssetReferenceInfo(image.sprite),
                    overrideSprite = BuildAssetReferenceInfo(image.overrideSprite),
                    type = image.type.ToString(),
                    preserveAspect = image.preserveAspect,
                    fillMethod = image.fillMethod.ToString(),
                    fillAmount = image.fillAmount,
                    pixelsPerUnitMultiplier = image.pixelsPerUnitMultiplier
                };
            }
            else if (graphic is RawImage rawImage)
            {
                data["rawImage"] = new
                {
                    texture = BuildAssetReferenceInfo(rawImage.texture),
                    uvRect = new
                    {
                        x = rawImage.uvRect.x,
                        y = rawImage.uvRect.y,
                        width = rawImage.uvRect.width,
                        height = rawImage.uvRect.height
                    }
                };
            }

            return data;
        }

        static object BuildWorldTextMeshProInfo(GameObject gameObject, Component textMeshPro)
        {
            if (gameObject == null || textMeshPro == null)
            {
                return null;
            }

            ForceTextMeshProMeshUpdate(textMeshPro);
            Object serializedFont = null;
            Object serializedMaterial = null;
            try
            {
                using var serializedObject = new SerializedObject(textMeshPro);
                serializedFont = serializedObject.FindProperty("m_fontAsset")?.objectReferenceValue;
                serializedMaterial = serializedObject.FindProperty("m_sharedMaterial")?.objectReferenceValue;
            }
            catch
            {
                // Best-effort diagnostics only.
            }

            var fontAsset = GetMemberValue(textMeshPro, "font") as Object ?? serializedFont;
            var sharedMaterial = GetMemberValue(textMeshPro, "fontSharedMaterial") as Object ?? serializedMaterial;
            var rendererMaterial = gameObject.TryGetComponent<Renderer>(out var renderer)
                ? renderer.sharedMaterial
                : null;

            return new
            {
                type = textMeshPro.GetType().Name,
                fullType = textMeshPro.GetType().FullName,
                text = GetMemberValue(textMeshPro, "text") as string,
                fontSize = ReadFloatMember(textMeshPro, "fontSize"),
                color = ToArray((Vector4)ReadTextMeshProColor(textMeshPro)),
                alignment = GetMemberValue(textMeshPro, "alignment")?.ToString(),
                richText = ReadBoolMember(textMeshPro, "richText"),
                autoSizing = ReadBoolMember(textMeshPro, "enableAutoSizing"),
                minFontSize = ReadFloatMember(textMeshPro, "fontSizeMin"),
                maxFontSize = ReadFloatMember(textMeshPro, "fontSizeMax"),
                overflowMode = GetMemberValue(textMeshPro, "overflowMode")?.ToString(),
                preferredWidth = ReadFloatMember(textMeshPro, "preferredWidth"),
                preferredHeight = ReadFloatMember(textMeshPro, "preferredHeight"),
                isOverflowing = IsTextMeshProOverflowing(textMeshPro),
                fontAsset = BuildAssetReferenceInfo(fontAsset),
                serializedFontAsset = BuildAssetReferenceInfo(serializedFont),
                fontSharedMaterial = BuildAssetReferenceInfo(sharedMaterial),
                serializedSharedMaterial = BuildAssetReferenceInfo(serializedMaterial),
                rendererSharedMaterial = BuildAssetReferenceInfo(rendererMaterial)
            };
        }

        static object BuildSelectableInfo(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var selectable = gameObject.GetComponent<Selectable>();
            if (selectable == null)
            {
                return null;
            }

            return new
            {
                type = selectable.GetType().Name,
                fullType = selectable.GetType().FullName,
                enabled = selectable.enabled,
                interactable = selectable.interactable,
                transition = selectable.transition.ToString(),
                targetGraphic = BuildObjectReferenceInfo(selectable.targetGraphic),
                colors = BuildColorBlockInfo(selectable.colors),
                spriteState = BuildSpriteStateInfo(selectable.spriteState),
                animationTriggers = new
                {
                    normalTrigger = selectable.animationTriggers.normalTrigger,
                    highlightedTrigger = selectable.animationTriggers.highlightedTrigger,
                    pressedTrigger = selectable.animationTriggers.pressedTrigger,
                    selectedTrigger = selectable.animationTriggers.selectedTrigger,
                    disabledTrigger = selectable.animationTriggers.disabledTrigger
                },
                navigation = BuildNavigationInfo(selectable.navigation)
            };
        }

        static object BuildButtonInfo(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var button = gameObject.GetComponent<Button>();
            if (button == null)
            {
                return null;
            }

            return new
            {
                enabled = button.enabled,
                interactable = button.interactable,
                targetGraphic = BuildObjectReferenceInfo(button.targetGraphic),
                onClick = UnityEventPersistentCallUtility.BuildEventInfo(button.onClick)
            };
        }

        static object BuildColorBlockInfo(ColorBlock colors)
        {
            return new
            {
                normalColor = ToArray((Vector4)colors.normalColor),
                highlightedColor = ToArray((Vector4)colors.highlightedColor),
                pressedColor = ToArray((Vector4)colors.pressedColor),
                selectedColor = ToArray((Vector4)colors.selectedColor),
                disabledColor = ToArray((Vector4)colors.disabledColor),
                colorMultiplier = colors.colorMultiplier,
                fadeDuration = colors.fadeDuration
            };
        }

        static object BuildSpriteStateInfo(SpriteState spriteState)
        {
            return new
            {
                highlightedSprite = BuildAssetReferenceInfo(spriteState.highlightedSprite),
                pressedSprite = BuildAssetReferenceInfo(spriteState.pressedSprite),
                selectedSprite = BuildAssetReferenceInfo(spriteState.selectedSprite),
                disabledSprite = BuildAssetReferenceInfo(spriteState.disabledSprite)
            };
        }

        static object BuildNavigationInfo(Navigation navigation)
        {
            return new
            {
                mode = navigation.mode.ToString(),
                wrapAround = navigation.wrapAround,
                selectOnUp = BuildObjectReferenceInfo(navigation.selectOnUp),
                selectOnDown = BuildObjectReferenceInfo(navigation.selectOnDown),
                selectOnLeft = BuildObjectReferenceInfo(navigation.selectOnLeft),
                selectOnRight = BuildObjectReferenceInfo(navigation.selectOnRight)
            };
        }

        static object BuildObjectReferenceInfo(Object obj)
        {
            if (obj == null)
            {
                return null;
            }

            if (obj is GameObject gameObject)
            {
                return new
                {
                    type = "GameObject",
                    name = gameObject.name,
                    path = IsEditableSceneObject(gameObject) ? GetHierarchyPath(gameObject) : AssetDatabase.GetAssetPath(gameObject),
                    id = UnityApiAdapter.GetObjectId(gameObject)
                };
            }

            if (obj is Component component)
            {
                return new
                {
                    type = component.GetType().Name,
                    fullType = component.GetType().FullName,
                    name = component.name,
                    path = IsEditableSceneObject(component.gameObject) ? GetHierarchyPath(component.gameObject) : AssetDatabase.GetAssetPath(component),
                    id = UnityApiAdapter.GetObjectId(component),
                    gameObject = BuildGameObjectInfo(component.gameObject)
                };
            }

            return BuildAssetReferenceInfo(obj);
        }

        static object BuildAssetReferenceInfo(Object asset)
        {
            if (asset == null)
            {
                return null;
            }

            var path = AssetDatabase.GetAssetPath(asset);
            return new
            {
                type = asset.GetType().Name,
                fullType = asset.GetType().FullName,
                name = asset.name,
                path = string.IsNullOrWhiteSpace(path) ? null : path,
                guid = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.AssetPathToGUID(path),
                id = UnityApiAdapter.GetObjectId(asset)
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
                path = GetHierarchyPath(gameObject),
                id = UnityApiAdapter.GetObjectId(gameObject),
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                scene = gameObject.scene.IsValid() ? gameObject.scene.name : null
            };
        }

        static object BuildCanvasInfo(Canvas canvas)
        {
            if (canvas == null)
            {
                return null;
            }

            return new
            {
                name = canvas.name,
                path = GetHierarchyPath(canvas.gameObject),
                id = UnityApiAdapter.GetObjectId(canvas.gameObject),
                renderMode = canvas.renderMode.ToString(),
                sortingLayerId = canvas.sortingLayerID,
                sortingOrder = canvas.sortingOrder,
                worldCamera = canvas.worldCamera != null ? canvas.worldCamera.name : null
            };
        }

        static object BuildCompactRectTransformInfo(RectTransform rectTransform)
        {
            return new
            {
                anchorMin = ToArray(rectTransform.anchorMin),
                anchorMax = ToArray(rectTransform.anchorMax),
                pivot = ToArray(rectTransform.pivot),
                anchoredPosition = ToArray(rectTransform.anchoredPosition),
                sizeDelta = ToArray(rectTransform.sizeDelta),
                rect = new { width = rectTransform.rect.width, height = rectTransform.rect.height }
            };
        }

        static object BuildLayoutComponentsInfo(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var fitter = gameObject.GetComponent<ContentSizeFitter>();
            var layoutElement = gameObject.GetComponent<LayoutElement>();
            var scrollRect = gameObject.GetComponent<ScrollRect>();
            var rectMask = gameObject.GetComponent<RectMask2D>();
            var mask = gameObject.GetComponent<Mask>();
            return new
            {
                layoutGroups = gameObject.GetComponents<LayoutGroup>().Select(BuildLayoutGroupInfo).ToArray(),
                contentSizeFitter = fitter != null ? new
                {
                    enabled = fitter.enabled,
                    horizontalFit = fitter.horizontalFit.ToString(),
                    verticalFit = fitter.verticalFit.ToString()
                } : null,
                layoutElement = layoutElement != null ? new
                {
                    enabled = layoutElement.enabled,
                    ignoreLayout = layoutElement.ignoreLayout,
                    minWidth = layoutElement.minWidth,
                    minHeight = layoutElement.minHeight,
                    preferredWidth = layoutElement.preferredWidth,
                    preferredHeight = layoutElement.preferredHeight,
                    flexibleWidth = layoutElement.flexibleWidth,
                    flexibleHeight = layoutElement.flexibleHeight,
                    layoutPriority = layoutElement.layoutPriority
                } : null,
                scrollRect = scrollRect != null ? BuildScrollRectInfo(scrollRect) : null,
                rectMask2D = rectMask != null ? new
                {
                    enabled = rectMask.enabled,
                    padding = ToArray(rectMask.padding),
                    softness = new[] { rectMask.softness.x, rectMask.softness.y }
                } : null,
                mask = mask != null ? new
                {
                    enabled = mask.enabled,
                    showMaskGraphic = mask.showMaskGraphic
                } : null
            };
        }

        static object BuildScrollRectInfo(ScrollRect scrollRect)
        {
            if (scrollRect == null)
            {
                return null;
            }

            return new
            {
                enabled = scrollRect.enabled,
                horizontal = scrollRect.horizontal,
                vertical = scrollRect.vertical,
                movementType = scrollRect.movementType.ToString(),
                inertia = scrollRect.inertia,
                decelerationRate = scrollRect.decelerationRate,
                elasticity = scrollRect.elasticity,
                scrollSensitivity = scrollRect.scrollSensitivity,
                normalizedPosition = ToArray(scrollRect.normalizedPosition),
                velocity = ToArray(scrollRect.velocity),
                viewport = scrollRect.viewport != null ? BuildGameObjectInfo(scrollRect.viewport.gameObject) : null,
                content = scrollRect.content != null ? BuildGameObjectInfo(scrollRect.content.gameObject) : null,
                horizontalScrollbar = scrollRect.horizontalScrollbar != null ? BuildGameObjectInfo(scrollRect.horizontalScrollbar.gameObject) : null,
                verticalScrollbar = scrollRect.verticalScrollbar != null ? BuildGameObjectInfo(scrollRect.verticalScrollbar.gameObject) : null
            };
        }

        static object BuildTextComponentInfo(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var legacyText = gameObject.GetComponent<Text>();
            if (legacyText != null)
            {
                return new
                {
                    type = "Text",
                    text = legacyText.text,
                    fontSize = legacyText.fontSize,
                    color = ToArray((Vector4)legacyText.color),
                    alignment = legacyText.alignment.ToString(),
                    richText = legacyText.supportRichText,
                    bestFit = legacyText.resizeTextForBestFit,
                    minFontSize = legacyText.resizeTextMinSize,
                    maxFontSize = legacyText.resizeTextMaxSize,
                    preferredWidth = legacyText.preferredWidth,
                    preferredHeight = legacyText.preferredHeight,
                    raycastTarget = legacyText.raycastTarget,
                    font = legacyText.font != null ? legacyText.font.name : null
                };
            }

            var textMeshPro = GetTextMeshProText(gameObject);
            if (textMeshPro == null)
            {
                return null;
            }

            ForceTextMeshProMeshUpdate(textMeshPro);
            var graphic = textMeshPro as Graphic;
            var fontAsset = GetMemberValue(textMeshPro, "font") as Object;
            return new
            {
                type = "TextMeshProUGUI",
                text = GetMemberValue(textMeshPro, "text") as string,
                fontSize = ReadFloatMember(textMeshPro, "fontSize"),
                color = graphic != null ? ToArray((Vector4)graphic.color) : null,
                alignment = GetMemberValue(textMeshPro, "alignment")?.ToString(),
                richText = ReadBoolMember(textMeshPro, "richText"),
                autoSizing = ReadBoolMember(textMeshPro, "enableAutoSizing"),
                minFontSize = ReadFloatMember(textMeshPro, "fontSizeMin"),
                maxFontSize = ReadFloatMember(textMeshPro, "fontSizeMax"),
                overflowMode = GetMemberValue(textMeshPro, "overflowMode")?.ToString(),
                preferredWidth = ReadFloatMember(textMeshPro, "preferredWidth"),
                preferredHeight = ReadFloatMember(textMeshPro, "preferredHeight"),
                isOverflowing = IsTextMeshProOverflowing(textMeshPro),
                raycastTarget = graphic != null && graphic.raycastTarget,
                fontAsset = fontAsset != null ? new
                {
                    name = fontAsset.name,
                    path = AssetDatabase.GetAssetPath(fontAsset)
                } : null
            };
        }

        static object BuildLayoutGroupInfo(LayoutGroup group)
        {
            var info = new Dictionary<string, object>
            {
                ["type"] = group.GetType().Name,
                ["enabled"] = group.enabled,
                ["childAlignment"] = group.childAlignment.ToString(),
                ["padding"] = ToArray(group.padding)
            };

            if (group is HorizontalOrVerticalLayoutGroup horizontalOrVertical)
            {
                info["spacing"] = horizontalOrVertical.spacing;
                info["childControlWidth"] = horizontalOrVertical.childControlWidth;
                info["childControlHeight"] = horizontalOrVertical.childControlHeight;
                info["childForceExpandWidth"] = horizontalOrVertical.childForceExpandWidth;
                info["childForceExpandHeight"] = horizontalOrVertical.childForceExpandHeight;
                info["childScaleWidth"] = horizontalOrVertical.childScaleWidth;
                info["childScaleHeight"] = horizontalOrVertical.childScaleHeight;
                info["reverseArrangement"] = horizontalOrVertical.reverseArrangement;
            }
            else if (group is GridLayoutGroup grid)
            {
                info["cellSize"] = ToArray(grid.cellSize);
                info["spacing"] = ToArray(grid.spacing);
                info["startCorner"] = grid.startCorner.ToString();
                info["startAxis"] = grid.startAxis.ToString();
                info["constraint"] = grid.constraint.ToString();
                info["constraintCount"] = grid.constraintCount;
            }

            return info;
        }

        static object BuildRectTransformInfo(RectTransform rectTransform)
        {
            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            return new
            {
                anchorMin = ToArray(rectTransform.anchorMin),
                anchorMax = ToArray(rectTransform.anchorMax),
                pivot = ToArray(rectTransform.pivot),
                anchoredPosition = ToArray(rectTransform.anchoredPosition),
                sizeDelta = ToArray(rectTransform.sizeDelta),
                offsetMin = ToArray(rectTransform.offsetMin),
                offsetMax = ToArray(rectTransform.offsetMax),
                localScale = ToArray(rectTransform.localScale),
                rect = new
                {
                    x = rectTransform.rect.x,
                    y = rectTransform.rect.y,
                    width = rectTransform.rect.width,
                    height = rectTransform.rect.height
                },
                inferredLayout = new
                {
                    horizontal = InferHorizontalLayout(rectTransform),
                    vertical = InferVerticalLayout(rectTransform)
                },
                worldCorners = corners.Select(ToArray).ToArray()
            };
        }

        static string InferHorizontalLayout(RectTransform rectTransform)
        {
            if (Mathf.Approximately(rectTransform.anchorMin.x, 0f) && Mathf.Approximately(rectTransform.anchorMax.x, 1f))
                return RectLayoutHorizontal.Stretch.ToString();
            if (Mathf.Approximately(rectTransform.anchorMin.x, 0f) && Mathf.Approximately(rectTransform.anchorMax.x, 0f))
                return RectLayoutHorizontal.Left.ToString();
            if (Mathf.Approximately(rectTransform.anchorMin.x, 0.5f) && Mathf.Approximately(rectTransform.anchorMax.x, 0.5f))
                return RectLayoutHorizontal.Center.ToString();
            if (Mathf.Approximately(rectTransform.anchorMin.x, 1f) && Mathf.Approximately(rectTransform.anchorMax.x, 1f))
                return RectLayoutHorizontal.Right.ToString();
            return "Custom";
        }

        static string InferVerticalLayout(RectTransform rectTransform)
        {
            if (Mathf.Approximately(rectTransform.anchorMin.y, 0f) && Mathf.Approximately(rectTransform.anchorMax.y, 1f))
                return RectLayoutVertical.Stretch.ToString();
            if (Mathf.Approximately(rectTransform.anchorMin.y, 0f) && Mathf.Approximately(rectTransform.anchorMax.y, 0f))
                return RectLayoutVertical.Bottom.ToString();
            if (Mathf.Approximately(rectTransform.anchorMin.y, 0.5f) && Mathf.Approximately(rectTransform.anchorMax.y, 0.5f))
                return RectLayoutVertical.Middle.ToString();
            if (Mathf.Approximately(rectTransform.anchorMin.y, 1f) && Mathf.Approximately(rectTransform.anchorMax.y, 1f))
                return RectLayoutVertical.Top.ToString();
            return "Custom";
        }

        static float[] ToArray(Vector2 vector) => new[] { vector.x, vector.y };

        static float[] ToArray(Vector3 vector) => new[] { vector.x, vector.y, vector.z };

        static float[] ToArray(Vector4 vector) => new[] { vector.x, vector.y, vector.z, vector.w };

        static int[] ToArray(RectOffset rectOffset)
        {
            return rectOffset == null
                ? null
                : new[] { rectOffset.left, rectOffset.right, rectOffset.top, rectOffset.bottom };
        }

        static string GetHierarchyPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var stack = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", stack);
        }

        static void MarkSceneDirty(GameObject gameObject)
        {
            if (gameObject != null && gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
        }

        sealed class UiAuditReport
        {
            public string Error;
            public List<GameObject> Roots = new();
            public List<object> RootSummaries = new();
            public List<Dictionary<string, object>> Issues = new();
            public int ScannedRects;
            public int ScannedTexts;
            public int ScannedButtons;
            public bool Truncated;

            public object ToResponseData()
            {
                return new
                {
                    summary = new
                    {
                        rootCount = Roots.Count,
                        rectCount = ScannedRects,
                        textCount = ScannedTexts,
                        buttonCount = ScannedButtons,
                        issueCount = Issues.Count,
                        errorCount = Issues.Count(issue => string.Equals(issue["severity"] as string, "error", StringComparison.OrdinalIgnoreCase)),
                        warningCount = Issues.Count(issue => string.Equals(issue["severity"] as string, "warning", StringComparison.OrdinalIgnoreCase)),
                        infoCount = Issues.Count(issue => string.Equals(issue["severity"] as string, "info", StringComparison.OrdinalIgnoreCase)),
                        issueCodes = Issues
                            .Where(issue => issue.TryGetValue("code", out var code) && code is string)
                            .GroupBy(issue => (string)issue["code"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(group => group.Count())
                            .ThenBy(group => group.Key)
                            .Select(group => new
                            {
                                code = group.Key,
                                count = group.Count(),
                                warningCount = group.Count(issue => string.Equals(issue["severity"] as string, "warning", StringComparison.OrdinalIgnoreCase)),
                                infoCount = group.Count(issue => string.Equals(issue["severity"] as string, "info", StringComparison.OrdinalIgnoreCase))
                            })
                            .ToArray(),
                        truncated = Truncated
                    },
                    roots = RootSummaries,
                    issues = Issues
                };
            }
        }

        readonly struct AutoFixResult
        {
            public readonly bool Applied;
            public readonly bool WouldApply;
            public readonly string Message;

            AutoFixResult(bool applied, bool wouldApply, string message)
            {
                Applied = applied;
                WouldApply = wouldApply;
                Message = message;
            }

            public static AutoFixResult Done(string message) => new(true, false, message);
            public static AutoFixResult Would(string message) => new(false, true, message);
            public static AutoFixResult Skip(string message) => new(false, false, message);
        }
    }
}
