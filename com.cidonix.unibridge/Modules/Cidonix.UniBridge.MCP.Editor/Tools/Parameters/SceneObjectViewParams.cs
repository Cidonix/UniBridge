#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum SceneObjectViewAction
    {
        Hierarchy,
        View,
        Query,
        Selection
    }

    public enum SceneObjectDetailLevel
    {
        Brief,
        Standard,
        Detailed,
        Full
    }

    public enum SceneObjectIndexDisplayMode
    {
        None,
        MetadataColumn
    }

    public enum SceneObjectViewProfile
    {
        Auto,
        Basic,
        Rendering,
        Physics2D,
        Physics3D,
        UI,
        Animation,
        VFX,
        Audio,
        Gameplay,
        Navigation,
        Input,
        Tilemap2D,
        Lighting,
        VideoTimeline
    }

    /// <summary>
    /// Parameters for UniBridge_SceneObjectView.
    /// </summary>
    public record SceneObjectViewParams
    {
        [McpDescription("Operation to perform: Hierarchy, View, Query, or Selection.", Required = false, Default = SceneObjectViewAction.Hierarchy)]
        public SceneObjectViewAction Action { get; set; } = SceneObjectViewAction.Hierarchy;

        [McpDescription("Detail level. Brief is compact names/paths, Standard adds transform/components, Detailed adds component summaries, Full adds bounded SerializedObject properties.", Required = false, Default = SceneObjectDetailLevel.Standard)]
        public SceneObjectDetailLevel Detail { get; set; } = SceneObjectDetailLevel.Standard;

        [McpDescription("Optional focus profile that chooses useful component summaries/properties for a task: Auto, Basic, Rendering, Physics2D, Physics3D, UI, Animation, VFX, Audio, Gameplay, Navigation, Input, Tilemap2D, Lighting, or VideoTimeline.", Required = false, Default = SceneObjectViewProfile.Auto)]
        public SceneObjectViewProfile Profile { get; set; } = SceneObjectViewProfile.Auto;

        [McpDescription("Scene GameObject target for View. Accepts object id, hierarchy path, or name.", Required = false)]
        public string Target { get; set; }

        [McpDescription("Several scene GameObject targets for View. Each accepts object id, hierarchy path, or name.", Required = false)]
        public string[] Targets { get; set; }

        [McpDescription("Optional loaded scene path or scene name. Empty means all loaded scenes for hierarchy/path resolution.", Required = false)]
        public string ScenePath { get; set; }

        [McpDescription("Optional duplicate index for View when a hierarchy path resolves to several GameObjects with the same path.", Required = false)]
        public int? Index { get; set; }

        [McpDescription("Target resolution method: Auto, ById, ByPath, ByName, ByTag, ByLayer, or ByComponent.", Required = false, Default = "Auto")]
        public string SearchMethod { get; set; } = "Auto";

        [McpDescription("For Query: case-insensitive substring match on GameObject name. If omitted, Target may be used as the name filter when no SearchMethod is specified.", Required = false)]
        public string NameContains { get; set; }

        [McpDescription("For Query: exact GameObject name match. Takes precedence over NameContains.", Required = false)]
        public string ExactName { get; set; }

        [McpDescription("For Query: required component type name/full name. Multiple filters are combined with AND logic.", Required = false)]
        public string ComponentType { get; set; }

        [McpDescription("For Query: required tag. Multiple filters are combined with AND logic.", Required = false)]
        public string Tag { get; set; }

        [McpDescription("For Query: required layer name or numeric layer index. Multiple filters are combined with AND logic.", Required = false)]
        public string Layer { get; set; }

        [McpDescription("For Query: pagination offset. Default 0.", Required = false, Default = 0)]
        public int? Offset { get; set; }

        [McpDescription("For hierarchy output, include duplicate-path indexes in a metadata column like the indexDisplayMode.", Required = false, Default = SceneObjectIndexDisplayMode.None)]
        public SceneObjectIndexDisplayMode IndexDisplayMode { get; set; } = SceneObjectIndexDisplayMode.None;

        [McpDescription("Include inactive GameObjects when resolving names/tags/layers/components. Hierarchy traversal always includes inactive children.", Required = false, Default = true)]
        public bool IncludeInactive { get; set; } = true;

        [McpDescription("Include runtime DontDestroyOnLoad objects while the editor is in play mode. Useful for managers and spawned runtime services.", Required = false, Default = true)]
        public bool IncludeDontDestroyOnLoad { get; set; } = true;

        [McpDescription("Include child nodes in structured object output. Defaults to true for Hierarchy/View.", Required = false)]
        public bool? IncludeChildren { get; set; }

        [McpDescription("Include flattened hierarchy text. Defaults to true for Hierarchy.", Required = false)]
        public bool? IncludeFlattened { get; set; }

        [McpDescription("Include structured hierarchy/object nodes. Defaults to false in Brief hierarchy mode, true otherwise.", Required = false)]
        public bool? IncludeStructured { get; set; }

        [McpDescription("Include renderer/collider bounds summaries.", Required = false, Default = true)]
        public bool IncludeBounds { get; set; } = true;

        [McpDescription("Include prefab status/source/override summary where available.", Required = false, Default = true)]
        public bool IncludePrefab { get; set; } = true;

        [McpDescription("Include bounded SerializedObject properties for components. Defaults to true only in Full detail.", Required = false)]
        public bool? IncludeSerializedProperties { get; set; }

        [McpDescription("Component type names whose SerializedObject properties should be included. Empty means all components when IncludeSerializedProperties is true.", Required = false)]
        public string[] IncludeComponentProperties { get; set; }

        [McpDescription("Maximum hierarchy/object depth for structured output. Defaults depend on Detail.", Required = false)]
        public int? MaxDepth { get; set; }

        [McpDescription("Maximum GameObjects returned across structured outputs. Defaults depend on Detail.", Required = false)]
        public int? MaxObjects { get; set; }

        [McpDescription("Maximum children per GameObject included in flattened and structured hierarchy output.", Required = false, Default = 50)]
        public int? MaxChildren { get; set; } = 50;

        [McpDescription("Maximum root GameObjects included per scene in hierarchy output.", Required = false, Default = 200)]
        public int? MaxRoots { get; set; } = 200;

        [McpDescription("Maximum SerializedObject properties returned per component.", Required = false, Default = 80)]
        public int? MaxSerializedProperties { get; set; } = 80;

        [McpDescription("Select resolved View targets in the Unity Editor.", Required = false, Default = false)]
        public bool Select { get; set; }
    }
}
