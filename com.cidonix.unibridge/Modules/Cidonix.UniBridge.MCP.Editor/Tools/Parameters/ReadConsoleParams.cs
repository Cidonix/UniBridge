using System;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    /// <summary>
    /// Actions that can be performed by the UniBridge_ReadConsole tool.
    /// </summary>
    public enum ConsoleAction
    {
        /// <summary>
        /// Get console log entries.
        /// </summary>
        Get,

        /// <summary>
        /// Clear the console.
        /// </summary>
        Clear,

        /// <summary>
        /// Return compact totals, recent entries, and the highest-signal grouped issues.
        /// </summary>
        Overview,

        /// <summary>
        /// Return grouped console messages with repeat counts and representative samples.
        /// </summary>
        Groups,

        /// <summary>
        /// Return details and sample entries for a single grouped console fingerprint.
        /// </summary>
        GroupDetails,

        /// <summary>
        /// Return a chronological console timeline.
        /// </summary>
        Timeline,

        /// <summary>
        /// Return a focused timeline window by entry range, center entry, or fingerprint.
        /// </summary>
        TimelineWindow,

        /// <summary>
        /// Return an AI-oriented diagnostic summary of current console issues.
        /// </summary>
        DiagnosticSummary,

        /// <summary>
        /// Return automatically detected important timeline ranges.
        /// </summary>
        ImportantRanges,

        /// <summary>
        /// Search console entries by text.
        /// </summary>
        Search,

        /// <summary>
        /// Add a marker entry to the Unity Console for later post-run filtering.
        /// </summary>
        MarkSession,

        /// <summary>
        /// Alias for MarkSession with clearer agent-facing naming.
        /// </summary>
        CreateMarker,

        /// <summary>
        /// Alias for Get that requires AfterMarkerId and returns entries after that marker.
        /// </summary>
        ReadSinceMarker,

        /// <summary>
        /// Alias for Clear with clearer agent-facing naming.
        /// </summary>
        ClearConsole
    }

    /// <summary>
    /// Console log types for filtering.
    /// </summary>
    public enum ConsoleLogType
    {
        /// <summary>
        /// Regular log messages.
        /// </summary>
        Log,

        /// <summary>
        /// Warning messages.
        /// </summary>
        Warning,

        /// <summary>
        /// Error messages.
        /// </summary>
        Error,

        /// <summary>
        /// Exception messages.
        /// </summary>
        Exception,

        /// <summary>
        /// Assertion messages.
        /// </summary>
        Assert,

        /// <summary>
        /// All message types.
        /// </summary>
        All
    }

    /// <summary>
    /// Output format for console entries.
    /// </summary>
    public enum ConsoleOutputFormat
    {
        /// <summary>
        /// Plain text format.
        /// </summary>
        Plain,

        /// <summary>
        /// JSON format.
        /// </summary>
        Json,

        /// <summary>
        /// Detailed format with all information.
        /// </summary>
        Detailed
    }

    /// <summary>
    /// Parameters for the UniBridge_ReadConsole tool.
    /// </summary>
    public record ReadConsoleParams
    {
        /// <summary>
        /// Gets or sets the console operation to perform.
        /// </summary>
        [McpDescription("Console operation to perform: Get, Clear, ClearConsole, Overview, Groups, GroupDetails, Timeline, TimelineWindow, DiagnosticSummary, ImportantRanges, Search, MarkSession, CreateMarker, or ReadSinceMarker", Required = false, Default = ConsoleAction.Get)]
        public ConsoleAction Action { get; set; } = ConsoleAction.Get;

        /// <summary>
        /// Gets or sets the console log types to retrieve.
        /// </summary>
        [McpDescription("Console log types to retrieve", Required = false)]
        public ConsoleLogType[] Types { get; set; } = { ConsoleLogType.Error, ConsoleLogType.Warning, ConsoleLogType.Log };

        /// <summary>
        /// Gets or sets the maximum number of console entries to retrieve.
        /// </summary>
        [McpDescription("Maximum number of console entries to retrieve", Required = false, Default = 100)]
        public int? Count { get; set; } = 100;

        /// <summary>
        /// Gets or sets the filter text to search for in messages.
        /// </summary>
        [McpDescription("Filter text to search for in messages", Required = false)]
        public string FilterText { get; set; }

        /// <summary>
        /// Gets or sets the marker id. When set, console actions only inspect entries after that marker.
        /// </summary>
        [McpDescription("Marker id returned by MarkSession. When set, console actions only inspect entries after that marker.", Required = false)]
        public string AfterMarkerId { get; set; }

        /// <summary>
        /// Gets or sets whether the marker entry itself should be included when AfterMarkerId is used.
        /// </summary>
        [McpDescription("Include the marker entry itself when filtering with AfterMarkerId", Required = false, Default = false)]
        public bool? IncludeMarker { get; set; } = false;

        /// <summary>
        /// Gets or sets a human-readable marker label for MarkSession.
        /// </summary>
        [McpDescription("Optional human-readable label for MarkSession", Required = false)]
        public string MarkerLabel { get; set; }

        /// <summary>
        /// Gets or sets the reserved timestamp filter.
        /// </summary>
        [McpDescription("Reserved timestamp filter. Unity console backlog does not expose reliable timestamps.", Required = false)]
        public string SinceTimestamp { get; set; }

        /// <summary>
        /// Gets or sets the output format for console entries.
        /// </summary>
        [McpDescription("Output format for console entries", Required = false, Default = ConsoleOutputFormat.Detailed)]
        public ConsoleOutputFormat Format { get; set; } = ConsoleOutputFormat.Detailed;

        /// <summary>
        /// Gets or sets whether to include stack traces in output.
        /// </summary>
        [McpDescription("Include stack traces in output", Required = false, Default = true)]
        public bool IncludeStacktrace { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of grouped issues to return for overview/group actions.
        /// </summary>
        [McpDescription("Maximum number of grouped console issues to return", Required = false, Default = 10)]
        public int? TopGroupCount { get; set; } = 10;

        /// <summary>
        /// Gets or sets the maximum number of timeline events to return.
        /// </summary>
        [McpDescription("Maximum number of timeline events to return", Required = false, Default = 50)]
        public int? MaxEvents { get; set; } = 50;

        /// <summary>
        /// Gets or sets the maximum number of issue groups to include in diagnostic summaries.
        /// </summary>
        [McpDescription("Maximum number of critical/warning/spam issue groups in diagnostic summaries", Required = false, Default = 8)]
        public int? MaxIssues { get; set; } = 8;

        /// <summary>
        /// Gets or sets the maximum number of representative entries to include.
        /// </summary>
        [McpDescription("Maximum number of representative entries to include", Required = false, Default = 12)]
        public int? MaxSamples { get; set; } = 12;

        /// <summary>
        /// Gets or sets the maximum number of important ranges to return.
        /// </summary>
        [McpDescription("Maximum number of automatically detected important timeline ranges to return", Required = false, Default = 8)]
        public int? MaxRanges { get; set; } = 8;

        /// <summary>
        /// Gets or sets the first console entryId in a timeline window.
        /// </summary>
        [McpDescription("First console entryId for Timeline or TimelineWindow range selection", Required = false)]
        public int? StartEntryId { get; set; }

        /// <summary>
        /// Gets or sets the last console entryId in a timeline window.
        /// </summary>
        [McpDescription("Last console entryId for Timeline or TimelineWindow range selection", Required = false)]
        public int? EndEntryId { get; set; }

        /// <summary>
        /// Gets or sets the center console entryId for a timeline window.
        /// </summary>
        [McpDescription("Center console entryId for TimelineWindow; ContextBefore and ContextAfter control the window size", Required = false)]
        public int? CenterEntryId { get; set; }

        /// <summary>
        /// Gets or sets how many entries to include before a center or important event.
        /// </summary>
        [McpDescription("Number of entries before CenterEntryId, Fingerprint match, or important event", Required = false, Default = 25)]
        public int? ContextBefore { get; set; } = 25;

        /// <summary>
        /// Gets or sets how many entries to include after a center or important event.
        /// </summary>
        [McpDescription("Number of entries after CenterEntryId, Fingerprint match, or important event", Required = false, Default = 50)]
        public int? ContextAfter { get; set; } = 50;

        /// <summary>
        /// Gets or sets whether consecutive repeated messages should be collapsed in timeline output.
        /// </summary>
        [McpDescription("Collapse consecutive repeated console entries into compact repeat blocks in timeline output", Required = false, Default = true)]
        public bool? CollapseRepeats { get; set; } = true;

        /// <summary>
        /// Gets or sets the minimum consecutive repeat count required for repeat collapse.
        /// </summary>
        [McpDescription("Minimum consecutive repeat count required before TimelineWindow/ImportantRanges collapses entries into a repeat block", Required = false, Default = 3)]
        public int? CollapseThreshold { get; set; } = 3;

        /// <summary>
        /// Gets or sets the console group fingerprint for GroupDetails.
        /// </summary>
        [McpDescription("Console group fingerprint returned by Groups or DiagnosticSummary, used by GroupDetails or TimelineWindow", Required = false)]
        public string Fingerprint { get; set; }
    }
}
