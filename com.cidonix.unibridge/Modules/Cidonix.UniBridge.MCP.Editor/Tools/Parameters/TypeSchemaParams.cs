#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum TypeSchemaAction
    {
        Inspect,
        ListTypes,
        InspectShader,
        InspectAsset,
        InspectGameObject,
        PatchExamples
    }

    public enum TypeSchemaKind
    {
        Any,
        Component,
        MonoBehaviour,
        ScriptableObject,
        AssetImporter,
        Asset,
        Shader
    }

    /// <summary>
    /// Parameters for UniBridge_TypeSchema.
    /// </summary>
    public record TypeSchemaParams
    {
        [McpDescription("Operation to perform: Inspect, ListTypes, InspectShader, InspectAsset, InspectGameObject, or PatchExamples.", Required = false, Default = TypeSchemaAction.Inspect)]
        public TypeSchemaAction Action { get; set; } = TypeSchemaAction.Inspect;

        [McpDescription("Type family to search or inspect: Any, Component, MonoBehaviour, ScriptableObject, AssetImporter, Asset, or Shader.", Required = false, Default = TypeSchemaKind.Any)]
        public TypeSchemaKind Kind { get; set; } = TypeSchemaKind.Any;

        [McpDescription("Type name to inspect. Accepts short, full, nested-with-dot, nested-with-plus, or assembly-qualified names.", Required = false)]
        public string TypeName { get; set; }

        [McpDescription("Type names to inspect in one call.", Required = false)]
        public string[] TypeNames { get; set; }

        [McpDescription("Search text for ListTypes, matched against type name, namespace, full name, and assembly.", Required = false)]
        public string Query { get; set; }

        [McpDescription("Asset path for InspectAsset or InspectShader. For materials, shader schema is included. For any asset, importer schema is included when available.", Required = false)]
        public string Path { get; set; }

        [McpDescription("Asset GUID for InspectAsset or InspectShader when Path is not known.", Required = false)]
        public string Guid { get; set; }

        [McpDescription("Shader path or Shader.Find name for InspectShader, for example 'Sprites/Default' or 'Assets/Shaders/My.shader'.", Required = false)]
        public string Shader { get; set; }

        [McpDescription("Scene GameObject target for InspectGameObject. Accepts name, hierarchy path, or object id.", Required = false)]
        public string Target { get; set; }

        [McpDescription("Search method for Target: by_id, by_name, by_path, by_tag, by_layer, by_component, or by_id_or_name_or_path.", Required = false, Default = "by_id_or_name_or_path")]
        public string SearchMethod { get; set; } = "by_id_or_name_or_path";

        [McpDescription("Include inactive scene and Prefab Stage objects when resolving InspectGameObject Target.", Required = false, Default = false)]
        public bool IncludeInactive { get; set; } = false;

        [McpDescription("Optional component type filters for InspectGameObject.", Required = false)]
        public string[] ComponentTypes { get; set; }

        [McpDescription("Include inherited fields and properties.", Required = false, Default = true)]
        public bool IncludeInherited { get; set; } = true;

        [McpDescription("Include public/settable C# properties in addition to fields.", Required = false, Default = true)]
        public bool IncludeProperties { get; set; } = true;

        [McpDescription("Include fields in schemas.", Required = false, Default = true)]
        public bool IncludeFields { get; set; } = true;

        [McpDescription("Include private fields marked [SerializeField].", Required = false, Default = true)]
        public bool IncludePrivateSerialized { get; set; } = true;

        [McpDescription("Include read-only fields/properties in the schema output. They are marked writable=false.", Required = false, Default = false)]
        public bool IncludeReadOnly { get; set; } = false;

        [McpDescription("Include obsolete members. Default false keeps patch targets cleaner.", Required = false, Default = false)]
        public bool IncludeObsolete { get; set; } = false;

        [McpDescription("Include Unity SerializedObject property paths when inspecting live objects, importers, or assets.", Required = false, Default = true)]
        public bool IncludeSerializedProperties { get; set; } = true;

        [McpDescription("Include current values for serialized properties where values are cheap and safe to serialize.", Required = false, Default = false)]
        public bool IncludeValues { get; set; } = false;

        [McpDescription("Include abstract types in ListTypes results.", Required = false, Default = false)]
        public bool IncludeAbstract { get; set; } = false;

        [McpDescription("Maximum ListTypes results.", Required = false, Default = 80)]
        public int Limit { get; set; } = 80;

        [McpDescription("Maximum serialized properties to return per object/importer/asset.", Required = false, Default = 160)]
        public int MaxSerializedProperties { get; set; } = 160;

        [McpDescription("Include ready-to-call patch examples for the inspected target/type. Useful for new agents before authoring property patches.", Required = false, Default = false)]
        public bool IncludePatchExamples { get; set; } = false;

        [McpDescription("Maximum patch examples to return per schema. Values are clamped to 1..20.", Required = false, Default = 6)]
        public int ExampleLimit { get; set; } = 6;
    }
}
