#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum RuntimeStateProbeAction
    {
        Snapshot,
        Sample,
        ListMembers
    }

    public record RuntimeStateProbeParams
    {
        [McpDescription("Operation to perform: Snapshot, Sample, or ListMembers.", Required = false, Default = RuntimeStateProbeAction.Snapshot)]
        public RuntimeStateProbeAction Action { get; set; } = RuntimeStateProbeAction.Snapshot;

        [McpDescription("Optional human-readable probe name used for saved files.", Required = false)]
        public string Name { get; set; }

        [McpDescription("GameObject target name, hierarchy path, instance ID, or component query depending on SearchMethod. If omitted, selection is used, or Component finds matching scene objects.", Required = false)]
        public string Target { get; set; }

        [McpDescription("Target search method: Auto, ById, ByPath, ByName, ByTag, ByLayer, or ByComponent.", Required = false, Default = "Auto")]
        public string SearchMethod { get; set; }

        [McpDescription("Component type filter. Matches short name, full name, MonoScript GUID/path, and serialized editor class identifier. If Target is omitted, this finds scene objects with the component.", Required = false)]
        public string Component { get; set; }

        [McpDescription("Optional member/property paths to read. Supports reflected field/property names and SerializedProperty paths such as m_Field or nested.property.", Required = false)]
        public string[] Members { get; set; }

        [McpDescription("Include inactive scene and Prefab Stage objects during target resolution. Default true.", Required = false, Default = true)]
        public bool? IncludeInactive { get; set; }

        [McpDescription("Include Prefab Stage objects during target resolution. Default true.", Required = false, Default = true)]
        public bool? IncludePrefabStage { get; set; }

        [McpDescription("Optional loaded scene path to limit target search.", Required = false)]
        public string ScenePath { get; set; }

        [McpDescription("Maximum matching GameObjects to probe. Default 5, clamped to 1..50.", Required = false, Default = 5)]
        public int? MaxTargets { get; set; }

        [McpDescription("Maximum components per GameObject to probe. Default 8, clamped to 1..100.", Required = false, Default = 8)]
        public int? MaxComponents { get; set; }

        [McpDescription("Maximum members per component to read when Members is omitted. Default 80, clamped to 1..500.", Required = false, Default = 80)]
        public int? MaxMembers { get; set; }

        [McpDescription("Include SerializedObject/SerializedProperty fields. Default true.", Required = false, Default = true)]
        public bool? IncludeSerializedFields { get; set; }

        [McpDescription("Include reflected fields and readable properties. Default true.", Required = false, Default = true)]
        public bool? IncludeReadableMembers { get; set; }

        [McpDescription("Include non-public reflected fields when listing/reading members. Explicit member names can still resolve non-public fields. Default false.", Required = false, Default = false)]
        public bool? IncludeNonPublicFields { get; set; }

        [McpDescription("Number of editor update ticks to sample for Action=Sample. Default 30, clamped to 1..600.", Required = false, Default = 30)]
        public int? SampleFrames { get; set; }

        [McpDescription("Timeout for Action=Sample in milliseconds. Default 30000.", Required = false, Default = 30000)]
        public int? TimeoutMs { get; set; }

        [McpDescription("Require Play Mode for Action=Sample. Default true because runtime state probes are intended for live gameplay.", Required = false, Default = true)]
        public bool? RequirePlayMode { get; set; }

        [McpDescription("Maximum string characters returned for a single value. Default 400.", Required = false, Default = 400)]
        public int? MaxStringLength { get; set; }

        [McpDescription("Maximum collection items serialized for a single value. Default 16.", Required = false, Default = 16)]
        public int? MaxCollectionItems { get; set; }

        [McpDescription("Save full sample payload under Library/UniBridge/RuntimeStateProbe. Default true.", Required = false, Default = true)]
        public bool? SaveToFile { get; set; }

        [McpDescription("Return raw per-frame samples inline. Default false; saved files contain raw samples when SaveToFile=true.", Required = false, Default = false)]
        public bool? ReturnSamples { get; set; }
    }
}
