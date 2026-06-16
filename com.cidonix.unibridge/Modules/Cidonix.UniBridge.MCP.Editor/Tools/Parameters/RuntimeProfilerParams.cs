#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum RuntimeProfilerAction
    {
        Snapshot,
        Sample,
        Hierarchy,
        Metrics
    }

    public record RuntimeProfilerParams
    {
        [McpDescription("Operation to perform: Snapshot, Sample, Hierarchy, or Metrics.", Required = false, Default = RuntimeProfilerAction.Snapshot)]
        public RuntimeProfilerAction Action { get; set; } = RuntimeProfilerAction.Snapshot;

        [McpDescription("Optional human-readable sample name used for saved profiler files.", Required = false)]
        public string Name { get; set; }

        [McpDescription("Profiler metrics to sample. Use aliases such as main_thread_ms, gc_alloc_bytes, batches_count, or category/name such as Internal/Main Thread.", Required = false)]
        public string[] Metrics { get; set; }

        [McpDescription("For Action=Hierarchy, profiler categories to inspect. Examples: Internal, Scripts, Render, Physics, Physics2D, Animation, Audio. Defaults to common CPU categories.", Required = false)]
        public string[] ProfilerCategories { get; set; }

        [McpDescription("For Action=Hierarchy, optional case-insensitive marker name filters. If omitted, all selected category markers are considered up to MaxProfilerMarkers.", Required = false)]
        public string[] MarkerFilters { get; set; }

        [McpDescription("For Action=Hierarchy, optional case-insensitive marker name filters to exclude noisy markers.", Required = false)]
        public string[] ExcludeMarkerFilters { get; set; }

        [McpDescription("Number of editor update ticks to sample for Action=Sample. Default 120, clamped to 1..600.", Required = false, Default = 120)]
        public int? SampleFrames { get; set; }

        [McpDescription("Timeout for Action=Sample in milliseconds. Default 30000.", Required = false, Default = 30000)]
        public int? TimeoutMs { get; set; }

        [McpDescription("Require Play Mode for Action=Sample. Default true because runtime profiler samples are most useful while the game is running.", Required = false, Default = true)]
        public bool? RequirePlayMode { get; set; }

        [McpDescription("Include loaded-scene and object-count summary. Default true.", Required = false, Default = true)]
        public bool? IncludeSceneSummary { get; set; }

        [McpDescription("Include memory snapshot from Unity Profiler APIs. Default true.", Required = false, Default = true)]
        public bool? IncludeMemory { get; set; }

        [McpDescription("Include top MonoBehaviour type counts in the scene summary. Default true.", Required = false, Default = true)]
        public bool? IncludeBehaviourTypeCounts { get; set; }

        [McpDescription("Maximum MonoBehaviour type groups returned in scene summary. Default 20.", Required = false, Default = 20)]
        public int? MaxBehaviourTypes { get; set; }

        [McpDescription("Main-thread spike threshold in milliseconds for Action=Sample. Default 33.3.", Required = false, Default = 33.3)]
        public double? MainThreadSpikeThresholdMs { get; set; }

        [McpDescription("Maximum spike samples returned in the response. Default 5.", Required = false, Default = 5)]
        public int? MaxSpikes { get; set; }

        [McpDescription("For Action=Hierarchy, maximum profiler markers to sample. Default 160, clamped to 1..500.", Required = false, Default = 160)]
        public int? MaxProfilerMarkers { get; set; }

        [McpDescription("For Action=Hierarchy, maximum top marker samples returned inline. Default 40, clamped to 1..300.", Required = false, Default = 40)]
        public int? MaxHierarchySamples { get; set; }

        [McpDescription("For Action=Hierarchy, maximum marker path depth returned in the synthetic hierarchy. Default 5, clamped to 1..12.", Required = false, Default = 5)]
        public int? MaxHierarchyDepth { get; set; }

        [McpDescription("For Action=Hierarchy, minimum time in milliseconds a marker must report before it appears in top samples. Default 0.", Required = false, Default = 0)]
        public double? MinHierarchySampleMs { get; set; }

        [McpDescription("For Action=Hierarchy, include non-time profiler counters as well as time samples. Default false.", Required = false, Default = false)]
        public bool? IncludeCounters { get; set; }

        [McpDescription("Save the full sample payload under Library/UniBridge/RuntimeProfiler. Default true.", Required = false, Default = true)]
        public bool? SaveToFile { get; set; }

        [McpDescription("Return raw per-frame samples inline. Default false; saved files contain the raw samples when SaveToFile=true.", Required = false, Default = false)]
        public bool? ReturnSamples { get; set; }
    }
}
