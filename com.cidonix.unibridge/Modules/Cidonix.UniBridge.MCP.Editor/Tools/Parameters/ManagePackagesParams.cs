using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum ManagePackagesAction { List, Inspect, Search, Add, ListSamples, ImportSample }

    public sealed class ManagePackagesParams
    {
        [McpDescription("List, Inspect, Search, Add, ListSamples or ImportSample. Add and ImportSample change the project.", Required = false, Default = ManagePackagesAction.List)]
        public ManagePackagesAction Action { get; set; } = ManagePackagesAction.List;
        [McpDescription("Registry package identifier, e.g. com.unity.inputsystem. Required for Inspect, ListSamples and ImportSample.", Required = false)]
        public string PackageName { get; set; }
        [McpDescription("Add only: registry package@exact-semver. URLs, local paths, ranges, latest and git specifications are rejected.", Required = false)]
        public string PackageId { get; set; }
        [McpDescription("Optional bounded case-insensitive package name/display-name filter. Search uses an exact package request when this is a registry package name.", Required = false)]
        public string Query { get; set; }
        [McpDescription("Zero-based sample index from ListSamples. Required for ImportSample.", Required = false)]
        public int? SampleIndex { get; set; }
        [McpDescription("Use Package Manager's offline cache for List/Inspect/Search. Default true for installed-package reads, false for Search.", Required = false)]
        public bool? Offline { get; set; }
        [McpDescription("Maximum package/sample results. Default 25, bounded 1..100; truncation is explicit.", Required = false, Default = 25)]
        public int? MaxResults { get; set; }
        [McpDescription("Native request/import observation timeout in milliseconds. Default 30000, bounded 1000..120000. A timeout does not cancel or replay a native UPM operation.", Required = false, Default = 30000)]
        public int? TimeoutMs { get; set; }
        [McpDescription("Sample import source-file budget, including metadata. Default 1000, bounded 1..4000. Over-budget samples are blocked before import.", Required = false, Default = 1000)]
        public int? MaxSampleFiles { get; set; }
        [McpDescription("Sample import source-byte budget. Default 16777216, bounded 1..67108864. Over-budget samples are blocked before import.", Required = false, Default = 16777216)]
        public long? MaxSampleBytes { get; set; }
    }
}
