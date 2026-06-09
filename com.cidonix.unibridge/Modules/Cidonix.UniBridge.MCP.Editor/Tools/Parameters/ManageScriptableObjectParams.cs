#nullable disable
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters
{
    /// <summary>
    /// ScriptableObject asset operations for UniBridge_ManageScriptableObject.
    /// </summary>
    public enum ScriptableObjectAction
    {
        Inspect,
        Validate,
        CreateOrUpdate,
        SetProperties,
        ListTypes
    }

    /// <summary>
    /// Parameters for UniBridge_ManageScriptableObject.
    /// </summary>
    public record ManageScriptableObjectParams
    {
        [McpDescription("Operation to perform: Inspect, Validate, CreateOrUpdate, SetProperties, or ListTypes.", Required = false, Default = ScriptableObjectAction.Inspect)]
        public ScriptableObjectAction Action { get; set; } = ScriptableObjectAction.Inspect;

        [McpDescription("ScriptableObject asset path. Accepts Assets/... or Packages/...; relative paths are treated as Assets/.... Missing .asset extension is appended for mutating actions.", Required = false)]
        public string Path { get; set; }

        [McpDescription("ScriptableObject asset GUID, used when Path is not known.", Required = false)]
        public string Guid { get; set; }

        [McpDescription("ScriptableObject type name. Accepts short type name, namespace-qualified name, or assembly-qualified name. Required when creating a missing asset.", Required = false)]
        public string ScriptableObjectType { get; set; }

        [McpDescription("Properties to validate or apply. Public fields/properties and serialized fields are supported. structured { scriptableObjectType, props } may be supplied through ScriptableObject.", Required = false)]
        public JObject Properties { get; set; }

        [McpDescription("structured scriptable object shape: { scriptableObjectType: 'Namespace.Type', props: { ... } }.", Required = false)]
        public JObject ScriptableObject { get; set; }

        [McpDescription("Preview mutating operations without creating or changing assets. Default false for direct calls; UniBridge_BatchActions dry-runs by default.", Required = false, Default = false)]
        public bool DryRun { get; set; }

        [McpDescription("Allow mutating ScriptableObject assets under Packages/.... Default false to avoid accidentally changing package cache or embedded package files.", Required = false, Default = false)]
        public bool AllowPackages { get; set; }

        [McpDescription("Include SerializedObject/SerializedProperty data in Inspect/response output. Default true.", Required = false, Default = true)]
        public bool IncludeSerializedProperties { get; set; } = true;

        [McpDescription("Maximum serialized properties returned by Inspect. Default 200.", Required = false, Default = 200)]
        public int MaxSerializedProperties { get; set; } = 200;

        [McpDescription("Search text for ListTypes. Matches type name, full name, namespace, assembly, or CreateAssetMenu menu name.", Required = false)]
        public string Query { get; set; }

        [McpDescription("Maximum ScriptableObject types returned by ListTypes. Default 100.", Required = false, Default = 100)]
        public int Limit { get; set; } = 100;

        [McpDescription("Select the ScriptableObject asset after a successful mutating operation.", Required = false, Default = false)]
        public bool Select { get; set; }
    }
}
