#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum RuntimeProfilerAction
    {
        Snapshot,
        Sample,
        Hierarchy,
        Metrics,
        RecordedFrames
    }

    public enum RuntimeProfilerRecordedSort
    {
        SelfTime,
        TotalTime,
        GcBytes
    }

    public record RuntimeProfilerParams
    {
        [McpDescription("Operation to perform: Snapshot, Sample, Hierarchy, Metrics, or RecordedFrames.", Required = false, Default = RuntimeProfilerAction.Snapshot)]
        public RuntimeProfilerAction Action { get; set; } = RuntimeProfilerAction.Snapshot;

        [McpDescription("For RecordedFrames, first recorded frame index. Omit to read the latest FrameCount available recorded frames; indices follow actual Profiler history traversal.", Required = false)]
        public int? FrameIndex { get; set; }

        [McpDescription("For RecordedFrames, number of recorded frames to read. Default 1, clamped to 1..60; independent of SampleFrames editor ticks.", Required = false, Default = 1)]
        public int? FrameCount { get; set; }

        [McpDescription("For RecordedFrames, persistent thread IDs as decimal strings, resolved to frame-local indices separately in each frame. Every requested physical ID must be observed in each frame; missing or unobserved IDs make coverage incomplete. ThreadNameFilters then selects the intersection; an observed name-excluded ID is not missing coverage.", Required = false)]
        public string[] ThreadIds { get; set; }

        [McpDescription("For RecordedFrames, optional case-insensitive substring filters on thread group/name, intersected with ThreadIds. Physical requested-ID observation is checked before this name exclusion.", Required = false)]
        public string[] ThreadNameFilters { get; set; }

        [McpDescription("For RecordedFrames, maximum selected threads per frame. Default 8, clamped to 1..64. At most 256 thread views are examined per frame; coverage reports any omissions.", Required = false, Default = 8)]
        public int? MaxThreads { get; set; }

        [McpDescription("For RecordedFrames, total raw samples copied across the request before analysis. Default 20000, clamped to 1..100000. Incomplete subtrees have unknown self time.", Required = false, Default = 20000)]
        public int? MaxRawSamples { get; set; }

        [McpDescription("For RecordedFrames, read GC.Alloc metadata. Default true; disabled, missing or unsupported metadata is unknown rather than zero.", Required = false, Default = true)]
        public bool? IncludeMetadata { get; set; }

        [McpDescription("For RecordedFrames, sort physical top samples by SelfTime, TotalTime or GcBytes. Filters and Top-N are applied after full copied-tree accounting.", Required = false, Default = RuntimeProfilerRecordedSort.SelfTime)]
        public RuntimeProfilerRecordedSort RecordedSortBy { get; set; } = RuntimeProfilerRecordedSort.SelfTime;

        [McpDescription("Optional human-readable sample name used for saved profiler files.", Required = false)]
        public string Name { get; set; }

        [McpDescription("Profiler metrics to sample. Use aliases such as main_thread_ms, gc_alloc_bytes, batches_count, or category/name such as Internal/Main Thread.", Required = false)]
        public string[] Metrics { get; set; }

        [McpDescription("For Hierarchy or RecordedFrames, profiler categories to inspect. Examples: Internal, Scripts, Render, Physics, Physics2D, Animation, Audio. Hierarchy defaults to common CPU categories; RecordedFrames omission includes all captured categories.", Required = false)]
        public string[] ProfilerCategories { get; set; }

        [McpDescription("For Hierarchy or RecordedFrames, optional case-insensitive marker name filters. If omitted, all selected category markers are considered up to MaxProfilerMarkers.", Required = false)]
        public string[] MarkerFilters { get; set; }

        [McpDescription("For Hierarchy or RecordedFrames, optional case-insensitive marker name filters to exclude noisy markers.", Required = false)]
        public string[] ExcludeMarkerFilters { get; set; }

        [McpDescription("Number of editor update ticks to sample for Action=Sample. Default 120, clamped to 1..600.", Required = false, Default = 120)]
        public int? SampleFrames { get; set; }

        [McpDescription("Timeout for Action=Sample, or the RecordedFrames extraction budget, in milliseconds. Default 30000.", Required = false, Default = 30000)]
        public int? TimeoutMs { get; set; }

        [McpDescription("Require Play Mode for Sample/Hierarchy by default. RecordedFrames defaults false and reads historical frames in Edit Mode; pass true to require Play Mode explicitly.", Required = false)]
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

        [McpDescription("For Hierarchy or RecordedFrames, maximum top marker samples returned inline. Default 40, clamped to 1..300.", Required = false, Default = 40)]
        public int? MaxHierarchySamples { get; set; }

        [McpDescription("For Hierarchy or RecordedFrames, maximum output hierarchy depth. RecordedFrames retains real physical ancestry and marks depth truncation. Default 5, clamped to 1..12.", Required = false, Default = 5)]
        public int? MaxHierarchyDepth { get; set; }

        [McpDescription("For Hierarchy or RecordedFrames, minimum inclusive time in milliseconds a marker must report before it appears in top samples. Default 0.", Required = false, Default = 0)]
        public double? MinHierarchySampleMs { get; set; }

        [McpDescription("For Action=Hierarchy, include non-time profiler counters as well as time samples. Default false.", Required = false, Default = false)]
        public bool? IncludeCounters { get; set; }

        [McpDescription("Save the full sample payload under Library/UniBridge/RuntimeProfiler. Default true.", Required = false, Default = true)]
        public bool? SaveToFile { get; set; }

        [McpDescription("Return raw per-frame samples inline. Default false; saved files contain the raw samples when SaveToFile=true.", Required = false, Default = false)]
        public bool? ReturnSamples { get; set; }
    }
}
