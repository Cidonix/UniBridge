#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum UIAction
    {
        Inspect,
        CreateCanvas,
        EnsureEventSystem,
        CreateElement,
        CreateTemplate,
        CreateScrollView,
        AddScrollItem,
        SetGraphic,
        SetButtonEvent,
        ClearButtonEvents,
        SetSelectableTransition,
        SetRectTransformLayout,
        SetRectTransform,
        SetLayoutGroup,
        SetContentSizeFitter,
        SetLayoutElement,
        Validate,
        Audit,
        RepairPlan,
        AutoFix
    }

    public enum UIElementType
    {
        Empty,
        Panel,
        Image,
        Text,
        Button,
        TextMeshProText,
        TextMeshProButton,
        Toggle,
        Slider,
        Dropdown,
        InputField,
        Scrollbar,
        TextMeshProInputField,
        TextMeshProDropdown,
        ToggleGroup
    }

    public enum UITemplateType
    {
        Panel,
        Modal,
        Toolbar,
        List,
        CardGrid,
        HUD
    }

    public enum RectLayoutHorizontal
    {
        Left,
        Center,
        Right,
        Stretch
    }

    public enum RectLayoutVertical
    {
        Top,
        Middle,
        Bottom,
        Stretch
    }

    public enum CanvasRenderModeOption
    {
        ScreenSpaceOverlay,
        ScreenSpaceCamera,
        WorldSpace
    }

    public enum UITextAlignment
    {
        UpperLeft,
        UpperCenter,
        UpperRight,
        MiddleLeft,
        MiddleCenter,
        MiddleRight,
        LowerLeft,
        LowerCenter,
        LowerRight
    }

    public enum UILayoutGroupType
    {
        Horizontal,
        Vertical,
        Grid
    }

    public enum UILayoutFitMode
    {
        Unconstrained,
        MinSize,
        PreferredSize
    }

    public enum UIGridStartCorner
    {
        UpperLeft,
        UpperRight,
        LowerLeft,
        LowerRight
    }

    public enum UIGridStartAxis
    {
        Horizontal,
        Vertical
    }

    public enum UIGridConstraint
    {
        Flexible,
        FixedColumnCount,
        FixedRowCount
    }

    public enum UIAutoFixMode
    {
        Safe,
        Layout,
        Aggressive
    }

    public enum UIScrollDirection
    {
        Vertical,
        Horizontal,
        Both
    }

    public enum UIScrollMovementType
    {
        Unrestricted,
        Elastic,
        Clamped
    }

    public enum UIButtonEventArgumentType
    {
        Void,
        String,
        Int,
        Float,
        Bool
    }

    public enum UISelectableTransitionMode
    {
        None,
        ColorTint,
        SpriteSwap,
        Animation
    }

    public record ManageUIParams
    {
        [McpDescription("Operation to perform: Inspect, CreateCanvas, EnsureEventSystem, CreateElement, CreateTemplate, CreateScrollView, AddScrollItem, SetGraphic, SetButtonEvent, ClearButtonEvents, SetSelectableTransition, SetRectTransformLayout, SetRectTransform, SetLayoutGroup, SetContentSizeFitter, SetLayoutElement, Validate, Audit, RepairPlan, or AutoFix.", Required = false, Default = UIAction.Inspect)]
        public UIAction Action { get; set; } = UIAction.Inspect;

        [McpDescription("Target GameObject name, hierarchy path, or object id. If omitted, the current selection is used where possible.", Required = false)]
        public string Target { get; set; }

        [McpDescription("Optional parent GameObject name, full hierarchy path, or stringified object/EntityId for CreateCanvas, CreateElement, CreateTemplate, or CreateScrollView. When Prefab Stage is open, resolution is restricted to that stage and failure/ambiguity is reported without scene fallback.", Required = false)]
        public string Parent { get; set; }

        [McpDescription("Optional stringified Unity object/EntityId for the UI creation parent. Takes precedence over Parent and Target and is useful when hierarchy names are duplicated.", Required = false)]
        public string ParentObjectIdString { get; set; }

        [McpDescription("Name for a created Canvas or UI element.", Required = false)]
        public string Name { get; set; }

        [McpDescription("UI element type for CreateElement or AddScrollItem: Empty, Panel, Image, Text, Button, TextMeshProText, TextMeshProButton, Toggle, Slider, Dropdown, InputField, Scrollbar, TextMeshProInputField, TextMeshProDropdown, or ToggleGroup. Empty scroll items create stable row labels.", Required = false, Default = UIElementType.Empty)]
        public UIElementType ElementType { get; set; } = UIElementType.Empty;

        [McpDescription("High-level UI template for CreateTemplate: Panel, Modal, Toolbar, List, CardGrid, or HUD.", Required = false, Default = UITemplateType.Panel)]
        public UITemplateType TemplateType { get; set; } = UITemplateType.Panel;

        [McpDescription("Canvas render mode for CreateCanvas: ScreenSpaceOverlay, ScreenSpaceCamera, or WorldSpace.", Required = false, Default = CanvasRenderModeOption.ScreenSpaceOverlay)]
        public CanvasRenderModeOption RenderMode { get; set; } = CanvasRenderModeOption.ScreenSpaceOverlay;

        [McpDescription("Optional camera target for ScreenSpaceCamera canvas mode.", Required = false)]
        public string Camera { get; set; }

        [McpDescription("Canvas sorting order for CreateCanvas. Defaults to 100 so camera-space UI renders above normal scene content.", Required = false)]
        public int? SortingOrder { get; set; }

        [McpDescription("Enable Canvas overrideSorting for CreateCanvas. Defaults to true for predictable AI-created UI.", Required = false, Default = true)]
        public bool? OverrideSorting { get; set; }

        [McpDescription("Create an EventSystem when creating UI if the scene does not already have one. Default true.", Required = false, Default = true)]
        public bool? EnsureEventSystem { get; set; }

        [McpDescription("For CreateElement, CreateTemplate, or CreateScrollView, create a Canvas automatically when no parent/target is available. In Prefab Stage the Canvas is created under the prefab root. Default true.", Required = false, Default = true)]
        public bool? CreateParentCanvas { get; set; }

        [McpDescription("Horizontal layout preset for SetRectTransformLayout or CreateElement: Left, Center, Right, Stretch.", Required = false, Default = RectLayoutHorizontal.Center)]
        public RectLayoutHorizontal LayoutHorizontal { get; set; } = RectLayoutHorizontal.Center;

        [McpDescription("Vertical layout preset for SetRectTransformLayout or CreateElement: Top, Middle, Bottom, Stretch.", Required = false, Default = RectLayoutVertical.Middle)]
        public RectLayoutVertical LayoutVertical { get; set; } = RectLayoutVertical.Middle;

        [McpDescription("When using layout presets, also set pivot to match the chosen layout. Default true.", Required = false, Default = true)]
        public bool? AlsoSetPivot { get; set; }

        [McpDescription("When using layout presets, also align anchored position/size to the new anchors. Default false preserves the visual placement.", Required = false, Default = false)]
        public bool? AlsoSetPosition { get; set; }

        [McpDescription("Anchor min [x,y] for SetRectTransform.", Required = false)]
        public float[] AnchorMin { get; set; }

        [McpDescription("Anchor max [x,y] for SetRectTransform.", Required = false)]
        public float[] AnchorMax { get; set; }

        [McpDescription("Pivot [x,y] for SetRectTransform.", Required = false)]
        public float[] Pivot { get; set; }

        [McpDescription("Anchored position [x,y] for SetRectTransform or CreateElement.", Required = false)]
        public float[] AnchoredPosition { get; set; }

        [McpDescription("Size delta [width,height] for SetRectTransform or CreateElement.", Required = false)]
        public float[] SizeDelta { get; set; }

        [McpDescription("Offset min [left,bottom] for SetRectTransform.", Required = false)]
        public float[] OffsetMin { get; set; }

        [McpDescription("Offset max [right,top] for SetRectTransform.", Required = false)]
        public float[] OffsetMax { get; set; }

        [McpDescription("Local scale [x,y] or [x,y,z] for SetRectTransform or CreateElement.", Required = false)]
        public float[] LocalScale { get; set; }

        [McpDescription("Keep the world position stable when changing pivot directly. Default true.", Required = false, Default = true)]
        public bool? MaintainWorldPosition { get; set; }

        [McpDescription("Text value for Text and Button elements.", Required = false)]
        public string Text { get; set; }

        [McpDescription("Initial value for controls such as Slider, Scrollbar, Dropdown, TMP_Dropdown, InputField, or TMP_InputField. Numeric controls accept 0..1 by default.", Required = false)]
        public string Value { get; set; }

        [McpDescription("Minimum numeric value for Slider. Default 0.", Required = false)]
        public float? MinValue { get; set; }

        [McpDescription("Maximum numeric value for Slider. Default 1.", Required = false)]
        public float? MaxValue { get; set; }

        [McpDescription("For Slider controls, snap values to whole numbers.", Required = false, Default = false)]
        public bool? WholeNumbers { get; set; }

        [McpDescription("Dropdown option labels for Dropdown or TextMeshProDropdown. Falls back to ItemTexts or a small default option set.", Required = false)]
        public string[] Options { get; set; }

        [McpDescription("Placeholder text for InputField or TextMeshProInputField.", Required = false)]
        public string Placeholder { get; set; }

        [McpDescription("Initial checked state for Toggle. Default false.", Required = false, Default = false)]
        public bool? IsOn { get; set; }

        [McpDescription("Optional ToggleGroup target for Toggle, or group settings target for ToggleGroup.", Required = false)]
        public string ToggleGroup { get; set; }

        [McpDescription("Title text for CreateTemplate. Falls back to Text or Name when omitted.", Required = false)]
        public string Title { get; set; }

        [McpDescription("Optional secondary text for CreateTemplate headers, modals, panels, and lists.", Required = false)]
        public string Subtitle { get; set; }

        [McpDescription("Button/action labels for CreateTemplate toolbars, modal actions, HUD action bars, or panel/list footers.", Required = false)]
        public string[] ActionTexts { get; set; }

        [McpDescription("Font size for Text and Button label elements. Default 24 for Text and 22 for Button.", Required = false)]
        public int? FontSize { get; set; }

        [McpDescription("Optional text alignment for Text, Button, and SetGraphic text updates. Creation actions default to MiddleCenter when omitted.", Required = false)]
        public UITextAlignment? Alignment { get; set; }

        [McpDescription("Enable Unity UI Text best-fit resizing for Text and Button labels. Default false.", Required = false, Default = false)]
        public bool? BestFit { get; set; }

        [McpDescription("Minimum font size when BestFit is enabled. Default 12.", Required = false, Default = 12)]
        public int? MinFontSize { get; set; }

        [McpDescription("Maximum font size when BestFit is enabled. Defaults to FontSize.", Required = false)]
        public int? MaxFontSize { get; set; }

        [McpDescription("Enable rich text tags for Text or TextMesh Pro text. When omitted, Unity/TMP defaults are preserved.", Required = false)]
        public bool? RichText { get; set; }

        [McpDescription("Optional TextMesh Pro overflow mode for TextMeshProText/TextMeshProButton, e.g. Overflow, Ellipsis, Truncate, Masking, or Linked.", Required = false)]
        public string OverflowMode { get; set; }

        [McpDescription("Optional TextMesh Pro font asset path under Assets/... for TextMeshProText/TextMeshProButton.", Required = false)]
        public string FontAssetPath { get; set; }

        [McpDescription("For TextMeshProText/TextMeshProButton, create a small project-local default TMP font asset when no TMP_FontAsset is found. Default true.", Required = false, Default = true)]
        public bool? CreateTmpFontAssetIfMissing { get; set; }

        [McpDescription("Sprite asset path or GUID for SetGraphic, button backgrounds, icons, or Selectable sprite state.", Required = false)]
        public string SpritePath { get; set; }

        [McpDescription("Texture asset path or GUID for SetGraphic when using RawImage.", Required = false)]
        public string TexturePath { get; set; }

        [McpDescription("Material asset path or GUID for SetGraphic on Image/RawImage/Text graphics.", Required = false)]
        public string MaterialPath { get; set; }

        [McpDescription("Optional Image.type value for SetGraphic: Simple, Sliced, Tiled, or Filled.", Required = false)]
        public string ImageType { get; set; }

        [McpDescription("For Image graphics, preserve the sprite aspect ratio. Default keeps the current value.", Required = false)]
        public bool? PreserveAspect { get; set; }

        [McpDescription("Set Graphic.raycastTarget on Image/RawImage/Text graphics.", Required = false)]
        public bool? RaycastTarget { get; set; }

        [McpDescription("Call Image.SetNativeSize or RawImage.SetNativeSize after assigning sprite/texture.", Required = false, Default = false)]
        public bool? SetNativeSize { get; set; }

        [McpDescription("Optional highlighted sprite path/GUID for Selectable SpriteSwap transition.", Required = false)]
        public string HighlightedSpritePath { get; set; }

        [McpDescription("Optional pressed sprite path/GUID for Selectable SpriteSwap transition.", Required = false)]
        public string PressedSpritePath { get; set; }

        [McpDescription("Optional selected sprite path/GUID for Selectable SpriteSwap transition.", Required = false)]
        public string SelectedSpritePath { get; set; }

        [McpDescription("Optional disabled sprite path/GUID for Selectable SpriteSwap transition.", Required = false)]
        public string DisabledSpritePath { get; set; }

        [McpDescription("Selectable transition mode for SetSelectableTransition: None, ColorTint, SpriteSwap, or Animation.", Required = false, Default = UISelectableTransitionMode.ColorTint)]
        public UISelectableTransitionMode Transition { get; set; } = UISelectableTransitionMode.ColorTint;

        [McpDescription("Optional target Graphic object name/path/id for Selectable transitions. Defaults to the target's existing targetGraphic or own Graphic.", Required = false)]
        public string TargetGraphic { get; set; }

        [McpDescription("Normal color for Selectable ColorTint as [r,g,b] or [r,g,b,a].", Required = false)]
        public float[] NormalColor { get; set; }

        [McpDescription("Highlighted color for Selectable ColorTint as [r,g,b] or [r,g,b,a].", Required = false)]
        public float[] HighlightedColor { get; set; }

        [McpDescription("Pressed color for Selectable ColorTint as [r,g,b] or [r,g,b,a].", Required = false)]
        public float[] PressedColor { get; set; }

        [McpDescription("Selected color for Selectable ColorTint as [r,g,b] or [r,g,b,a].", Required = false)]
        public float[] SelectedColor { get; set; }

        [McpDescription("Disabled color for Selectable ColorTint as [r,g,b] or [r,g,b,a].", Required = false)]
        public float[] DisabledColor { get; set; }

        [McpDescription("Color multiplier for Selectable ColorTint.", Required = false)]
        public float? ColorMultiplier { get; set; }

        [McpDescription("Fade duration for Selectable ColorTint.", Required = false)]
        public float? FadeDuration { get; set; }

        [McpDescription("Button onClick target GameObject name/path/id for SetButtonEvent. Defaults to the button GameObject.", Required = false)]
        public string EventTarget { get; set; }

        [McpDescription("Component type name on EventTarget that owns EventMethod. If omitted, UniBridge searches components for a matching method.", Required = false)]
        public string EventComponent { get; set; }

        [McpDescription("Public instance method to call from Button.onClick.", Required = false)]
        public string EventMethod { get; set; }

        [McpDescription("Argument value for Button.onClick persistent listener when EventArgumentType is String, Int, Float, or Bool.", Required = false)]
        public string EventArgument { get; set; }

        [McpDescription("Argument type for Button.onClick persistent listener: Void, String, Int, Float, or Bool. Default Void.", Required = false, Default = UIButtonEventArgumentType.Void)]
        public UIButtonEventArgumentType EventArgumentType { get; set; } = UIButtonEventArgumentType.Void;

        [McpDescription("Remove existing Button.onClick persistent listeners before adding the requested listener. ClearButtonEvents always clears.", Required = false, Default = false)]
        public bool? ClearExistingEvents { get; set; }

        [McpDescription("Scroll axis for CreateScrollView: Vertical, Horizontal, or Both. Default Vertical.", Required = false, Default = UIScrollDirection.Vertical)]
        public UIScrollDirection ScrollDirection { get; set; } = UIScrollDirection.Vertical;

        [McpDescription("Optional viewport child name for CreateScrollView. Default Viewport.", Required = false)]
        public string ViewportName { get; set; }

        [McpDescription("Optional content child name for CreateScrollView, or AddScrollItem target child lookup. Default Content.", Required = false)]
        public string ContentName { get; set; }

        [McpDescription("Use RectMask2D on the viewport for clipping. Default true.", Required = false, Default = true)]
        public bool? UseRectMask2D { get; set; }

        [McpDescription("ScrollRect movement type: Unrestricted, Elastic, or Clamped. Default Clamped.", Required = false, Default = UIScrollMovementType.Clamped)]
        public UIScrollMovementType MovementType { get; set; } = UIScrollMovementType.Clamped;

        [McpDescription("Enable ScrollRect inertia. Default true.", Required = false, Default = true)]
        public bool? Inertia { get; set; }

        [McpDescription("ScrollRect elasticity for Elastic movement. Default keeps Unity's value.", Required = false)]
        public float? Elasticity { get; set; }

        [McpDescription("ScrollRect deceleration rate. Default keeps Unity's value.", Required = false)]
        public float? DecelerationRate { get; set; }

        [McpDescription("ScrollRect scroll sensitivity. Default 30.", Required = false, Default = 30)]
        public float? ScrollSensitivity { get; set; }

        [McpDescription("Optional item texts for CreateScrollView or AddScrollItem. When provided, UniBridge creates one item per text.", Required = false)]
        public string[] ItemTexts { get; set; }

        [McpDescription("Optional ScrollView item size [width,height]. Use this for row/button/cell size; SizeDelta remains the ScrollView container size.", Required = false)]
        public float[] ItemSizeDelta { get; set; }

        [McpDescription("Preferred column count for CreateTemplate CardGrid. Values below 1 are clamped to 1.", Required = false)]
        public int? Columns { get; set; }

        [McpDescription("For CreateTemplate, prefer TextMesh Pro text/buttons when available. Default true.", Required = false, Default = true)]
        public bool? UseTextMeshPro { get; set; }

        [McpDescription("For CreateTemplate, run Validate on the created template and include the validation summary in the response. Default true.", Required = false, Default = true)]
        public bool? ValidateAfterCreate { get; set; }

        [McpDescription("Layout group type for SetLayoutGroup: Horizontal, Vertical, or Grid.", Required = false, Default = UILayoutGroupType.Vertical)]
        public UILayoutGroupType LayoutGroupType { get; set; } = UILayoutGroupType.Vertical;

        [McpDescription("Padding for SetLayoutGroup as [left,right,top,bottom]. Values are rounded to integers.", Required = false)]
        public float[] Padding { get; set; }

        [McpDescription("Spacing for SetLayoutGroup. Horizontal/Vertical groups use the first value; Grid uses [x,y], or one value for both axes.", Required = false)]
        public float[] Spacing { get; set; }

        [McpDescription("Child alignment for SetLayoutGroup. Omit to keep the current Unity default/alignment.", Required = false)]
        public UITextAlignment? ChildAlignment { get; set; }

        [McpDescription("For Horizontal/Vertical layout groups, control child widths.", Required = false)]
        public bool? ChildControlWidth { get; set; }

        [McpDescription("For Horizontal/Vertical layout groups, control child heights.", Required = false)]
        public bool? ChildControlHeight { get; set; }

        [McpDescription("For Horizontal/Vertical layout groups, force children to expand horizontally.", Required = false)]
        public bool? ChildForceExpandWidth { get; set; }

        [McpDescription("For Horizontal/Vertical layout groups, force children to expand vertically.", Required = false)]
        public bool? ChildForceExpandHeight { get; set; }

        [McpDescription("For Horizontal/Vertical layout groups, include child local scale on width calculations.", Required = false)]
        public bool? ChildScaleWidth { get; set; }

        [McpDescription("For Horizontal/Vertical layout groups, include child local scale on height calculations.", Required = false)]
        public bool? ChildScaleHeight { get; set; }

        [McpDescription("For Horizontal/Vertical layout groups, lay out children in reverse order.", Required = false)]
        public bool? ReverseArrangement { get; set; }

        [McpDescription("Grid cell size [width,height] for SetLayoutGroup when LayoutGroupType=Grid.", Required = false)]
        public float[] CellSize { get; set; }

        [McpDescription("Grid start corner for SetLayoutGroup when LayoutGroupType=Grid.", Required = false, Default = UIGridStartCorner.UpperLeft)]
        public UIGridStartCorner StartCorner { get; set; } = UIGridStartCorner.UpperLeft;

        [McpDescription("Grid start axis for SetLayoutGroup when LayoutGroupType=Grid.", Required = false, Default = UIGridStartAxis.Horizontal)]
        public UIGridStartAxis StartAxis { get; set; } = UIGridStartAxis.Horizontal;

        [McpDescription("Grid constraint for SetLayoutGroup when LayoutGroupType=Grid.", Required = false, Default = UIGridConstraint.Flexible)]
        public UIGridConstraint Constraint { get; set; } = UIGridConstraint.Flexible;

        [McpDescription("Grid constraint count for FixedColumnCount or FixedRowCount. Values below 1 are clamped to 1.", Required = false)]
        public int? ConstraintCount { get; set; }

        [McpDescription("Remove other LayoutGroup components from the target before adding the requested group type. Default true.", Required = false, Default = true)]
        public bool? RemoveExistingLayoutGroups { get; set; }

        [McpDescription("Horizontal fit for SetContentSizeFitter: Unconstrained, MinSize, or PreferredSize.", Required = false)]
        public UILayoutFitMode? HorizontalFit { get; set; }

        [McpDescription("Vertical fit for SetContentSizeFitter: Unconstrained, MinSize, or PreferredSize.", Required = false)]
        public UILayoutFitMode? VerticalFit { get; set; }

        [McpDescription("For SetLayoutElement, ignore this element in parent layout calculations.", Required = false)]
        public bool? IgnoreLayout { get; set; }

        [McpDescription("For SetLayoutElement, minimum width.", Required = false)]
        public float? MinWidth { get; set; }

        [McpDescription("For SetLayoutElement, minimum height.", Required = false)]
        public float? MinHeight { get; set; }

        [McpDescription("For SetLayoutElement, preferred width.", Required = false)]
        public float? PreferredWidth { get; set; }

        [McpDescription("For SetLayoutElement, preferred height.", Required = false)]
        public float? PreferredHeight { get; set; }

        [McpDescription("For SetLayoutElement, flexible width weight.", Required = false)]
        public float? FlexibleWidth { get; set; }

        [McpDescription("For SetLayoutElement, flexible height weight.", Required = false)]
        public float? FlexibleHeight { get; set; }

        [McpDescription("For SetLayoutElement, layout priority.", Required = false)]
        public int? LayoutPriority { get; set; }

        [McpDescription("For Audit, include inactive UI objects under the target/root canvases. Default false.", Required = false, Default = false)]
        public bool? IncludeInactive { get; set; }

        [McpDescription("For Audit, maximum number of issues to return. Default 100.", Required = false, Default = 100)]
        public int? MaxIssues { get; set; }

        [McpDescription("For Audit, pixel/unit tolerance for overflow and overlap checks. Default 1.", Required = false, Default = 1)]
        public float? AuditTolerance { get; set; }

        [McpDescription("For RepairPlan/AutoFix, optional issue codes to include or fix. Defaults depend on AutoFixMode.", Required = false)]
        public string[] FixCodes { get; set; }

        [McpDescription("For AutoFix, maximum number of fixes to apply or preview. Default 25.", Required = false, Default = 25)]
        public int? MaxFixes { get; set; }

        [McpDescription("For RepairPlan/AutoFix, controls how much UniBridge may change UI layout: Safe only fixes low-risk setup/text issues; Layout may move children to resolve obvious overlap/outside-parent issues; Aggressive may also convert manual containers into LayoutGroups with LayoutElements. Default Safe.", Required = false, Default = UIAutoFixMode.Safe)]
        public UIAutoFixMode AutoFixMode { get; set; } = UIAutoFixMode.Safe;

        [McpDescription("Foreground/text color as [r,g,b] or [r,g,b,a]. Accepts 0..1 or 0..255 ranges.", Required = false)]
        public float[] Color { get; set; }

        [McpDescription("Image/background color as [r,g,b] or [r,g,b,a]. Accepts 0..1 or 0..255 ranges.", Required = false)]
        public float[] BackgroundColor { get; set; }

        [McpDescription("Select the created or modified GameObject after the operation. Default false.", Required = false, Default = false)]
        public bool? Select { get; set; }

        [McpDescription("Preview the operation without modifying the scene. Default false.", Required = false, Default = false)]
        public bool? DryRun { get; set; }
    }
}
