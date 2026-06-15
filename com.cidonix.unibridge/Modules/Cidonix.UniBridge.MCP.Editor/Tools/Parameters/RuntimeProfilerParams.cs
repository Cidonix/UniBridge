#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum RuntimeProfilerAction
    {
        Snapshot,
        Sample,
        Metrics
    }

    public record RuntimeProfilerParams
    {
        [McpDescription("Operation to perform: Snapshot, Sample, or Metrics.", Required = false, Default = RuntimeProfilerAction.Snapshot)]
        public RuntimeProfilerAction Action { get; set; } = RuntimeProfilerAction.Snapshot;

        [McpDescription("Optional human-readable sample name used for saved profiler files.", Required = false)]
        public string Name { get; set; }

        [McpDescription("Profiler metrics to sample. Use aliases such as main_thread_ms, gc_alloc_bytes, batches_count, or category/name such as Internal/Main Thread.", Required = false)]
        public string[] Metrics { get; set; }

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

        [McpDescription("Save the full sample payload under Library/UniBridge/RuntimeProfiler. Default true.", Required = false, Default = true)]
        public bool? SaveToFile { get; set; }

        [McpDescription("Return raw per-frame samples inline. Default false; saved files contain the raw samples when SaveToFile=true.", Required = false, Default = false)]
        public bool? ReturnSamples { get; set; }
    }
}
