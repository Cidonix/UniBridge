using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry; // For Response class
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Handles reading and clearing Unity Editor console log entries.
    /// Uses reflection to access internal LogEntry methods/properties.
    /// </summary>
    public static class ReadConsole
    {
        /// <summary>
        /// Gets the description text for the UniBridge_ReadConsole tool.
        /// Describes the available parameters and return values for reading or clearing console messages.
        /// </summary>
        public const string Title = "Read Unity console messages";

        public const string Description = @"Read, clear, search, and summarize the Unity Editor Console.

Use this first when checking compile errors, import problems, warnings, or runtime logs in the open Unity project.

Args:
    Action: Get, Clear, ClearConsole, Overview, Groups, GroupDetails, Timeline, TimelineWindow, DiagnosticSummary, ImportantRanges, Search, MarkSession, CreateMarker, or ReadSinceMarker.
    Types: Log, Warning, Error, Exception, Assert, or All.
    Count: Maximum entries to return; default is 100.
    FilterText: Optional text filter applied to message contents.
    AfterMarkerId: Marker id returned by MarkSession; filters results to entries after that marker.
    IncludeMarker: Include the marker entry itself when AfterMarkerId is used.
    MarkerLabel: Optional human-readable label for MarkSession.
    Format: Plain, Detailed, or Json.
    IncludeStacktrace: Include stack traces for deeper debugging.
    TopGroupCount: Maximum groups for Overview or Groups.
    MaxEvents: Maximum entries for Timeline.
    MaxIssues: Maximum issue groups for DiagnosticSummary.
    MaxSamples: Maximum representative entries for DiagnosticSummary or GroupDetails.
    MaxRanges: Maximum automatically detected ranges for ImportantRanges.
    StartEntryId/EndEntryId: Optional exact entryId range for Timeline or TimelineWindow.
    CenterEntryId + ContextBefore/ContextAfter: Optional focused timeline window around an entry.
    CollapseRepeats/CollapseThreshold: Collapse consecutive repeated timeline entries into compact repeat blocks.
    Fingerprint: Console group fingerprint for GroupDetails or TimelineWindow.
    SinceTimestamp: Reserved; Unity Editor console backlog does not expose reliable timestamps.

Returns:
    success, message, and action-specific structured data. Overview, Groups, Timeline, TimelineWindow, ImportantRanges, and DiagnosticSummary return compact console intelligence optimized for AI triage.";
        /// <summary>
        /// Returns the output schema for this tool.
        /// </summary>
        /// <returns>The JSON schema object describing the tool's output structure.</returns>
        [McpOutputSchema("UniBridge_ReadConsole")]
        public static object GetOutputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    success = new { type = "boolean", description = "Whether the operation succeeded" },
                    message = new { type = "string", description = "Human-readable message about the operation" },
                    data = new
                    {
                        type = "array",
                        description = "Console log entries (for get action)",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                message = new { type = "string", description = "Log message content" },
                                type = new { type = "string", description = "Log type (Error, Warning, Log, etc.)" },
                                file = new { type = "string", description = "Source file if available" },
                                line = new { type = "integer", description = "Line number if available" },
                                stackTrace = new { type = "string", description = "Stack trace if available" }
                            }
                        }
                    }
                },
                required = new[] { "success", "message" }
            };
        }

        // (Calibration removed)

        // Reflection members for accessing internal LogEntry data
        // private static MethodInfo _getEntriesMethod; // Removed as it's unused and fails reflection
        static MethodInfo _startGettingEntriesMethod;
        static MethodInfo _endGettingEntriesMethod; // Renamed from _stopGettingEntriesMethod, trying End...
        static MethodInfo _clearMethod;
        static MethodInfo _getCountMethod;
        static MethodInfo _getEntryMethod;
        static FieldInfo _modeField;
        static FieldInfo _messageField;
        static FieldInfo _fileField;
        static FieldInfo _lineField;

        static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        static readonly Regex GuidRegex = new Regex(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.Compiled);
        static readonly Regex HexRegex = new Regex(@"\b0x[0-9a-f]+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex NumberRegex = new Regex(@"\b\d+\b", RegexOptions.Compiled);
        static readonly string[] CriticalBuildSystemNeedles =
        {
            "Internal build system error",
            "BuildProgram exited with code",
            "ScriptCompilationBuildProgram",
            "Application Control policy has blocked this file",
            "Code Integrity",
            "CodeIntegrity",
            "FileLoadException",
            "NiceIO.dll",
            "did not meet Enterprise signing level",
            "violated code integrity policy",
            "Could not load file or assembly"
        };

        const int DefaultTopGroupCount = 10;
        const int HardMaxTopGroupCount = 50;
        const int DefaultTimelineEventCount = 50;
        const int HardMaxTimelineEventCount = 500;
        const int DefaultMaxIssues = 8;
        const int HardMaxIssues = 30;
        const int DefaultMaxSamples = 12;
        const int HardMaxSamples = 100;
        const int DefaultMaxRanges = 8;
        const int HardMaxRanges = 30;
        const int DefaultContextBefore = 25;
        const int DefaultContextAfter = 50;
        const int HardMaxContextEntries = 500;
        const int DefaultCollapseThreshold = 3;
        const int HardMaxCollapseThreshold = 100;
        const int SpamRunThreshold = 25;
        const string MarkerPrefix = "[UniBridge Console Marker]";
        const string MarkerSessionStatePrefix = "Cidonix.UniBridge.ReadConsole.Marker.";

        // Note: Timestamp is not directly available in LogEntry; need to parse message or find alternative?

        // Static constructor for reflection setup
        static ReadConsole()
        {
            try
            {
                Type logEntriesType = typeof(EditorApplication).Assembly.GetType(
                    "UnityEditor.LogEntries"
                );
                if (logEntriesType == null)
                    throw new Exception("Could not find internal type UnityEditor.LogEntries");



                // Include NonPublic binding flags as internal APIs might change accessibility
                BindingFlags staticFlags =
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                BindingFlags instanceFlags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _startGettingEntriesMethod = logEntriesType.GetMethod(
                    "StartGettingEntries",
                    staticFlags
                );
                if (_startGettingEntriesMethod == null)
                    throw new Exception("Failed to reflect LogEntries.StartGettingEntries");

                // Try reflecting EndGettingEntries based on warning message
                _endGettingEntriesMethod = logEntriesType.GetMethod(
                    "EndGettingEntries",
                    staticFlags
                );
                if (_endGettingEntriesMethod == null)
                    throw new Exception("Failed to reflect LogEntries.EndGettingEntries");

                _clearMethod = logEntriesType.GetMethod("Clear", staticFlags);
                if (_clearMethod == null)
                    throw new Exception("Failed to reflect LogEntries.Clear");

                _getCountMethod = logEntriesType.GetMethod("GetCount", staticFlags);
                if (_getCountMethod == null)
                    throw new Exception("Failed to reflect LogEntries.GetCount");

                _getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", staticFlags);
                if (_getEntryMethod == null)
                    throw new Exception("Failed to reflect LogEntries.GetEntryInternal");

                Type logEntryType = typeof(EditorApplication).Assembly.GetType(
                    "UnityEditor.LogEntry"
                );
                if (logEntryType == null)
                    throw new Exception("Could not find internal type UnityEditor.LogEntry");

                _modeField = logEntryType.GetField("mode", instanceFlags);
                if (_modeField == null)
                    throw new Exception("Failed to reflect LogEntry.mode");

                _messageField = logEntryType.GetField("message", instanceFlags);
                if (_messageField == null)
                    throw new Exception("Failed to reflect LogEntry.message");

                _fileField = logEntryType.GetField("file", instanceFlags);
                if (_fileField == null)
                    throw new Exception("Failed to reflect LogEntry.file");

                _lineField = logEntryType.GetField("line", instanceFlags);
                if (_lineField == null)
                    throw new Exception("Failed to reflect LogEntry.line");

                // (Calibration removed)

            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"[ReadConsole] Static Initialization Failed: Could not setup reflection for LogEntries/LogEntry. Console reading/clearing will likely fail. Specific Error: {e.Message}"
                );
                // Set members to null to prevent NullReferenceExceptions later, HandleCommand should check this.
                _startGettingEntriesMethod =
                    _endGettingEntriesMethod =
                    _clearMethod =
                    _getCountMethod =
                    _getEntryMethod =
                        null;
                _modeField = _messageField = _fileField = _lineField = null;
            }
        }

        // --- Input Validation ---

        /// <summary>
        /// Validates and normalizes input parameters, similar to Python's defensive parameter handling.
        /// Applies defaults and defensive coercion for robustness.
        /// </summary>
        static ReadConsoleParams ValidateInput(ReadConsoleParams parameters)
        {
            parameters ??= new ReadConsoleParams();

            return parameters with
            {
                // Default Types to [Error, Warning, Log] if null or empty
                Types = parameters.Types == null || parameters.Types.Length == 0
                    ? new[] { ConsoleLogType.Error, ConsoleLogType.Warning, ConsoleLogType.Log }
                    : parameters.Types,

                // Treat negative or zero Count as null (no limit)
                Count = parameters.Count.HasValue && parameters.Count.Value <= 0
                    ? null
                    : parameters.Count,

                // Trim and normalize FilterText
                FilterText = string.IsNullOrWhiteSpace(parameters.FilterText)
                    ? null
                    : parameters.FilterText.Trim(),

                AfterMarkerId = string.IsNullOrWhiteSpace(parameters.AfterMarkerId)
                    ? null
                    : parameters.AfterMarkerId.Trim(),

                MarkerLabel = string.IsNullOrWhiteSpace(parameters.MarkerLabel)
                    ? null
                    : NormalizeMarkerLabel(parameters.MarkerLabel),

                // Trim and normalize SinceTimestamp
                SinceTimestamp = string.IsNullOrWhiteSpace(parameters.SinceTimestamp)
                    ? null
                    : parameters.SinceTimestamp.Trim(),

                Fingerprint = string.IsNullOrWhiteSpace(parameters.Fingerprint)
                    ? null
                    : parameters.Fingerprint.Trim(),

                TopGroupCount = ClampNullable(parameters.TopGroupCount, DefaultTopGroupCount, 1, HardMaxTopGroupCount),
                MaxEvents = ClampNullable(parameters.MaxEvents, DefaultTimelineEventCount, 1, HardMaxTimelineEventCount),
                MaxIssues = ClampNullable(parameters.MaxIssues, DefaultMaxIssues, 1, HardMaxIssues),
                MaxSamples = ClampNullable(parameters.MaxSamples, DefaultMaxSamples, 1, HardMaxSamples),
                MaxRanges = ClampNullable(parameters.MaxRanges, DefaultMaxRanges, 1, HardMaxRanges),
                ContextBefore = ClampNullable(parameters.ContextBefore, DefaultContextBefore, 0, HardMaxContextEntries),
                ContextAfter = ClampNullable(parameters.ContextAfter, DefaultContextAfter, 0, HardMaxContextEntries),
                CollapseThreshold = ClampNullable(parameters.CollapseThreshold, DefaultCollapseThreshold, 2, HardMaxCollapseThreshold),
                StartEntryId = parameters.StartEntryId.HasValue && parameters.StartEntryId.Value < 0
                    ? 0
                    : parameters.StartEntryId,
                EndEntryId = parameters.EndEntryId.HasValue && parameters.EndEntryId.Value < 0
                    ? 0
                    : parameters.EndEntryId,
                CenterEntryId = parameters.CenterEntryId.HasValue && parameters.CenterEntryId.Value < 0
                    ? 0
                    : parameters.CenterEntryId
            };
        }

        static int? ClampNullable(int? value, int defaultValue, int min, int max)
        {
            var resolved = value ?? defaultValue;
            if (resolved < min) return min;
            if (resolved > max) return max;
            return resolved;
        }

        // --- Main Handler ---

        /// <summary>
        /// Main handler for console management actions.
        /// Processes UniBridge_ReadConsole tool requests to get or clear Unity console messages.
        /// </summary>
        /// <param name="parameters">The parameters specifying the console operation to perform.</param>
        /// <returns>A Response object containing the operation result. For 'Get' actions, includes console log entries in the data field.</returns>    
        [McpTool("UniBridge_ReadConsole", Description, Title, Groups = new[] { "debug", "editor" }, EnabledByDefault = true)]
        public static object HandleCommand(ReadConsoleParams parameters)
        {
            // Check if ALL required reflection members were successfully initialized.
            if (
                _startGettingEntriesMethod == null
                || _endGettingEntriesMethod == null
                || _clearMethod == null
                || _getCountMethod == null
                || _getEntryMethod == null
                || _modeField == null
                || _messageField == null
                || _fileField == null
                || _lineField == null
            )
            {
                // Log the error here as well for easier debugging in Unity Console
                Debug.LogError(
                    "[ReadConsole] HandleCommand called but reflection members are not initialized. Static constructor might have failed silently or there's an issue."
                );
                return Response.Error(
                    "ReadConsole handler failed to initialize due to reflection errors. Cannot access console logs."
                );
            }

            // Validate and normalize input parameters
            var @params = ValidateInput(parameters);

            try
            {
                switch (@params.Action)
                {
                    case ConsoleAction.Clear:
                    case ConsoleAction.ClearConsole:
                        return ClearConsole();
                    case ConsoleAction.Get:
                        return GetConsoleEntries(@params, "Retrieved console entries.");
                    case ConsoleAction.ReadSinceMarker:
                        if (string.IsNullOrWhiteSpace(@params.AfterMarkerId))
                        {
                            return Response.Error("ReadSinceMarker requires AfterMarkerId from CreateMarker/MarkSession.");
                        }
                        return GetConsoleEntries(@params, "Retrieved console entries since marker.");
                    case ConsoleAction.Search:
                        return GetConsoleEntries(@params, "Searched console entries.");
                    case ConsoleAction.MarkSession:
                    case ConsoleAction.CreateMarker:
                        return MarkSession(@params);
                    case ConsoleAction.Overview:
                        return Response.Success("Built console overview.", BuildOverview(@params));
                    case ConsoleAction.Groups:
                        return Response.Success("Built console groups.", BuildGroupsResponse(@params));
                    case ConsoleAction.GroupDetails:
                        return BuildGroupDetailsResponse(@params);
                    case ConsoleAction.Timeline:
                        return Response.Success("Built console timeline.", BuildTimeline(@params));
                    case ConsoleAction.TimelineWindow:
                        return Response.Success("Built console timeline window.", BuildTimelineWindow(@params));
                    case ConsoleAction.DiagnosticSummary:
                        return Response.Success("Built console diagnostic summary.", BuildDiagnosticSummary(@params));
                    case ConsoleAction.ImportantRanges:
                        return Response.Success("Built important console ranges.", BuildImportantRangesResponse(@params));
                    default:
                        return Response.Error(
                            $"Unknown action: '{@params.Action}'. Valid actions are Get, Clear, ClearConsole, Overview, Groups, GroupDetails, Timeline, TimelineWindow, DiagnosticSummary, ImportantRanges, Search, MarkSession, CreateMarker, or ReadSinceMarker."
                        );
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReadConsole] Action '{@params.Action}' failed: {e}");
                return Response.Error($"Internal error processing action '{@params.Action}': {e.Message}");
            }
        }

        // --- Action Implementations ---

        static object ClearConsole()
        {
            try
            {
                _clearMethod.Invoke(null, null); // Static method, no instance, no parameters

                return Response.Success("Console cleared successfully.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReadConsole] Failed to clear console: {e}");
                return Response.Error($"Failed to clear console: {e.Message}");
            }
        }

        public static object BuildBuildSystemHealth(int maxIssues = 5, bool includeStacktrace = false)
        {
            maxIssues = Mathf.Clamp(maxIssues <= 0 ? 5 : maxIssues, 1, HardMaxIssues);

            try
            {
                var groups = BuildGroups(ReadUnityConsoleEntries())
                    .Where(IsCriticalBuildSystemGroup)
                    .OrderByDescending(group => GetSeverityRank(group.Type))
                    .ThenByDescending(group => group.LastEntryId)
                    .Take(maxIssues)
                    .ToArray();

                return new
                {
                    hasCriticalIssues = groups.Length > 0,
                    criticalIssueCount = groups.Length,
                    criticalIssues = groups
                        .Select(group => ToGroupData(group, includeStacktrace))
                        .ToArray(),
                    fingerprints = groups.Select(group => group.Fingerprint).ToArray(),
                    checkedSignals = CriticalBuildSystemNeedles
                };
            }
            catch (Exception e)
            {
                return new
                {
                    hasCriticalIssues = false,
                    criticalIssueCount = 0,
                    criticalIssues = Array.Empty<object>(),
                    warning = $"Could not inspect Unity console for build-system failures: {e.Message}"
                };
            }
        }

        static object MarkSession(ReadConsoleParams parameters)
        {
            var beforeEntries = ReadUnityConsoleEntries();
            var markerId = CreateMarkerId();
            var label = parameters.MarkerLabel ?? "session";
            var createdUtc = DateTime.UtcNow.ToString("O");
            var markerMessage = $"{MarkerPrefix} id={markerId} label=\"{label}\" utc={createdUtc}";

            SessionState.SetString(GetMarkerSessionStateKey(markerId), $"{createdUtc}|{label}");
            Debug.Log(markerMessage);

            var afterEntries = ReadUnityConsoleEntries();
            var markerEntry = FindMarkerEntry(afterEntries, markerId);

            return Response.Success("Console session marker created.", new
            {
                action = "mark_session",
                markerId,
                label,
                createdUtc,
                message = markerMessage,
                backlogEntriesBefore = beforeEntries.Count,
                markerEntryId = markerEntry?.EntryId,
                markerFound = markerEntry != null,
                storedInSessionState = true,
                openWith = new
                {
                    afterMarkerId = markerId,
                    includeMarker = false
                },
                examples = new
                {
                    overview = new { Action = "Overview", AfterMarkerId = markerId },
                    importantRanges = new { Action = "ImportantRanges", AfterMarkerId = markerId },
                    timeline = new { Action = "Timeline", AfterMarkerId = markerId },
                    search = new { Action = "Search", AfterMarkerId = markerId, FilterText = "text-to-find" }
                },
                note = "Use AfterMarkerId on later ReadConsole actions to inspect only entries after this marker."
            });
        }

        static object GetConsoleEntries(ReadConsoleParams parameters, string successMessage)
        {
            try
            {
                var entries = GetFilteredEntries(parameters, out _, out var markerContext)
                    .Take(parameters.Count ?? int.MaxValue)
                    .Select(entry => ToConsoleLogEntry(entry, parameters.IncludeStacktrace, parameters.Format))
                    .ToList();

                if (markerContext.Requested && !markerContext.Known)
                {
                    return Response.Error("Console marker was not found.", ToMarkerContextData(markerContext));
                }

                return Response.Success($"{successMessage} Returned {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}.", entries);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReadConsole] Error while retrieving log entries: {e}");
                return Response.Error($"Error retrieving log entries: {e.Message}");
            }
        }

        static object BuildOverview(ReadConsoleParams parameters)
        {
            var filteredEntries = GetFilteredEntries(parameters, out var allEntries, out var markerContext);
            var groups = BuildGroups(filteredEntries);
            var topGroups = groups
                .OrderByDescending(group => GetSeverityRank(group.Type))
                .ThenByDescending(group => group.Count)
                .ThenByDescending(group => group.LastEntryId)
                .Take(parameters.TopGroupCount ?? DefaultTopGroupCount)
                .Select(group => ToGroupData(group, parameters.IncludeStacktrace))
                .ToArray();

            var recent = LastItems(filteredEntries, 10)
                .Select(entry => ToEntryData(entry, includeStacktrace: false))
                .ToArray();

            return new
            {
                action = "overview",
                totalBacklogEntries = allEntries.Count,
                filteredEntries = filteredEntries.Count,
                marker = ToMarkerContextData(markerContext),
                totals = BuildTotals(filteredEntries),
                topGroups,
                recent,
                note = "Unity Editor console backlog does not expose reliable timestamps; entryId is the current backlog order."
            };
        }

        static object BuildGroupsResponse(ReadConsoleParams parameters)
        {
            var entries = GetFilteredEntries(parameters, out _, out var markerContext);
            var allGroups = BuildGroups(entries);
            var groups = allGroups
                .OrderByDescending(group => GetSeverityRank(group.Type))
                .ThenByDescending(group => group.Count)
                .ThenByDescending(group => group.LastEntryId)
                .Take(parameters.TopGroupCount ?? DefaultTopGroupCount)
                .Select(group => ToGroupData(group, parameters.IncludeStacktrace))
                .ToArray();

            return new
            {
                action = "groups",
                filteredEntries = entries.Count,
                marker = ToMarkerContextData(markerContext),
                totalGroups = allGroups.Count,
                returnedGroups = groups.Length,
                groups
            };
        }

        static object BuildGroupDetailsResponse(ReadConsoleParams parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters.Fingerprint))
            {
                return Response.Error("Fingerprint is required for GroupDetails.");
            }

            var entries = GetFilteredEntries(parameters, out _, out var markerContext);
            var group = BuildGroups(entries)
                .FirstOrDefault(candidate => string.Equals(candidate.Fingerprint, parameters.Fingerprint, StringComparison.Ordinal));

            if (group == null)
            {
                return Response.Error("Console group was not found.", new { fingerprint = parameters.Fingerprint, marker = ToMarkerContextData(markerContext) });
            }

            var samples = LastItems(group.Entries, parameters.MaxSamples ?? DefaultMaxSamples)
                .Select(entry => ToEntryData(entry, parameters.IncludeStacktrace))
                .ToArray();

            return Response.Success("Built console group details.", new
            {
                action = "group_details",
                marker = ToMarkerContextData(markerContext),
                group = ToGroupData(group, parameters.IncludeStacktrace),
                returnedSamples = samples.Length,
                samples
            });
        }

        static object BuildTimeline(ReadConsoleParams parameters)
        {
            var filteredEntries = GetFilteredEntries(parameters, out _, out var markerContext);
            var selectedEntries = SelectTimelineEntries(filteredEntries, parameters, allowTailFallback: true, out var selection);
            var rawLimit = parameters.MaxEvents ?? DefaultTimelineEventCount;
            var entries = selectedEntries
                .Take(rawLimit)
                .Select(entry => ToEntryData(entry, parameters.IncludeStacktrace))
                .ToArray();

            return new
            {
                action = "timeline",
                selection,
                marker = ToMarkerContextData(markerContext),
                filteredEntries = filteredEntries.Count,
                totalSelectedEvents = selectedEntries.Count,
                returnedEvents = entries.Length,
                events = entries,
                compressedEvents = ShouldCollapseRepeats(parameters)
                    ? BuildCompressedTimeline(selectedEntries, parameters, rawLimit).ToArray()
                    : null,
                note = "Timeline uses current Unity console backlog order because console entries do not expose reliable timestamps."
            };
        }

        static object BuildTimelineWindow(ReadConsoleParams parameters)
        {
            var filteredEntries = GetFilteredEntries(parameters, out _, out var markerContext);
            var selectedEntries = SelectTimelineEntries(filteredEntries, parameters, allowTailFallback: false, out var selection);

            if (selectedEntries.Count == 0)
            {
                return new
                {
                    action = "timeline_window",
                    filteredEntries = filteredEntries.Count,
                    marker = ToMarkerContextData(markerContext),
                    selection,
                    returnedEvents = 0,
                    events = Array.Empty<object>(),
                    compressedEvents = Array.Empty<object>(),
                    note = "No entries matched the requested timeline window."
                };
            }

            var rawLimit = parameters.MaxEvents ?? DefaultTimelineEventCount;
            var rawEvents = selectedEntries
                .Take(rawLimit)
                .Select(entry => ToEntryData(entry, parameters.IncludeStacktrace))
                .ToArray();

            return new
            {
                action = "timeline_window",
                filteredEntries = filteredEntries.Count,
                marker = ToMarkerContextData(markerContext),
                selection,
                window = ToWindowData(selectedEntries),
                returnedEvents = rawEvents.Length,
                totalWindowEvents = selectedEntries.Count,
                events = rawEvents,
                compressedEvents = BuildCompressedTimeline(selectedEntries, parameters, rawLimit).ToArray(),
                note = "Use StartEntryId/EndEntryId, CenterEntryId, or Fingerprint to focus on a smaller post-run console window."
            };
        }

        static object BuildImportantRangesResponse(ReadConsoleParams parameters)
        {
            var entries = GetFilteredEntries(parameters, out _, out var markerContext);
            var groups = BuildGroups(entries);
            var ranges = DetectImportantRanges(entries, parameters)
                .Take(parameters.MaxRanges ?? DefaultMaxRanges)
                .Select(range => ToRangeData(range, entries, parameters))
                .ToArray();

            return new
            {
                action = "important_ranges",
                filteredEntries = entries.Count,
                marker = ToMarkerContextData(markerContext),
                totalGroups = groups.Count,
                returnedRanges = ranges.Length,
                ranges,
                noiseGroups = groups
                    .Where(group => group.Count >= SpamRunThreshold && (group.Type == LogType.Log || group.Type == LogType.Warning))
                    .OrderByDescending(group => group.Count)
                    .Take(parameters.MaxIssues ?? DefaultMaxIssues)
                    .Select(group => ToGroupData(group, includeStacktrace: false))
                    .ToArray(),
                guidance = "Open a returned range with TimelineWindow using StartEntryId/EndEntryId, or inspect a group with GroupDetails using its fingerprint."
            };
        }

        static object BuildDiagnosticSummary(ReadConsoleParams parameters)
        {
            var entries = GetFilteredEntries(parameters, out _, out var markerContext);
            var groups = BuildGroups(entries)
                .OrderByDescending(group => GetSeverityRank(group.Type))
                .ThenByDescending(group => group.Count)
                .ThenByDescending(group => group.LastEntryId)
                .ToList();

            var maxIssues = parameters.MaxIssues ?? DefaultMaxIssues;
            var maxSamples = parameters.MaxSamples ?? DefaultMaxSamples;

            var criticalIssues = groups
                .Where(group => group.Type == LogType.Exception || group.Type == LogType.Error || group.Type == LogType.Assert)
                .Take(maxIssues)
                .Select(group => ToGroupData(group, parameters.IncludeStacktrace))
                .ToArray();

            var warningIssues = groups
                .Where(group => group.Type == LogType.Warning)
                .Take(maxIssues)
                .Select(group => ToGroupData(group, parameters.IncludeStacktrace))
                .ToArray();

            var likelySpam = groups
                .Where(group => group.Count >= 25 && (group.Type == LogType.Log || group.Type == LogType.Warning))
                .Take(maxIssues)
                .Select(group => new
                {
                    fingerprint = group.Fingerprint,
                    type = group.Type.ToString(),
                    count = group.Count,
                    representativeMessage = group.RepresentativeMessage,
                    firstEntryId = group.FirstEntryId,
                    lastEntryId = group.LastEntryId
                })
                .ToArray();

            var recentSamples = entries
                .Where(entry => entry.Type == LogType.Exception || entry.Type == LogType.Error || entry.Type == LogType.Assert || entry.Type == LogType.Warning)
                .GroupBy(entry => entry.Fingerprint, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderByDescending(entry => entry.EntryId)
                .Take(maxSamples)
                .Select(entry => ToEntryData(entry, parameters.IncludeStacktrace))
                .ToArray();

            var timelineHighlightEntries = entries
                .Where(entry => entry.Type == LogType.Exception || entry.Type == LogType.Error || entry.Type == LogType.Assert || entry.Type == LogType.Warning)
                .ToList();

            var timelineHighlights = LastItems(timelineHighlightEntries, Math.Min(parameters.MaxEvents ?? DefaultTimelineEventCount, 30))
                .Select(entry => ToEntryData(entry, includeStacktrace: false))
                .ToArray();

            object dominantIssue = criticalIssues.Cast<object>()
                .Concat(warningIssues.Cast<object>())
                .Concat(likelySpam.Cast<object>())
                .FirstOrDefault();

            return new
            {
                action = "diagnostic_summary",
                marker = ToMarkerContextData(markerContext),
                totals = BuildTotals(entries),
                summary = new
                {
                    dominantIssue,
                    criticalIssues,
                    warningIssues,
                    likelySpam,
                    recentSamples,
                    timelineHighlights
                },
                guidance = "Use GroupDetails with a returned fingerprint when a representative issue needs full samples and stack traces."
            };
        }

        static List<ConsoleEntrySnapshot> ReadUnityConsoleEntries()
        {
            var entries = new List<ConsoleEntrySnapshot>();
            var started = false;

            try
            {
                _startGettingEntriesMethod.Invoke(null, null);
                started = true;

                int totalEntries = (int)_getCountMethod.Invoke(null, null);
                Type logEntryType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry");
                if (logEntryType == null)
                {
                    throw new Exception("Could not find internal type UnityEditor.LogEntry during console read.");
                }

                object logEntryInstance = Activator.CreateInstance(logEntryType);
                for (int index = 0; index < totalEntries; index++)
                {
                    _getEntryMethod.Invoke(null, new object[] { index, logEntryInstance });

                    int mode = (int)_modeField.GetValue(logEntryInstance);
                    string fullMessage = (string)_messageField.GetValue(logEntryInstance);
                    string file = (string)_fileField.GetValue(logEntryInstance);
                    int line = (int)_lineField.GetValue(logEntryInstance);

                    if (string.IsNullOrWhiteSpace(fullMessage))
                    {
                        continue;
                    }

                    var type = RefineLogTypeFromMessage(GetLogTypeFromMode(mode), fullMessage);
                    var stackTrace = ExtractStackTrace(fullMessage);
                    var message = ExtractMessageOnly(fullMessage, stackTrace);
                    entries.Add(new ConsoleEntrySnapshot
                    {
                        EntryId = index,
                        Type = type,
                        Message = message,
                        FullMessage = fullMessage,
                        File = file,
                        Line = line,
                        StackTrace = stackTrace,
                        Fingerprint = BuildFingerprint(type, message, stackTrace)
                    });
                }

                return entries;
            }
            finally
            {
                if (started)
                {
                    try
                    {
                        _endGettingEntriesMethod.Invoke(null, null);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[ReadConsole] Failed to call EndGettingEntries: {e}");
                    }
                }
            }
        }

        static List<ConsoleEntrySnapshot> GetFilteredEntries(
            ReadConsoleParams parameters,
            out List<ConsoleEntrySnapshot> allEntries,
            out MarkerContext markerContext)
        {
            allEntries = ReadUnityConsoleEntries();
            markerContext = ResolveMarkerContext(allEntries, parameters);
            return FilterEntries(allEntries, parameters, markerContext).ToList();
        }

        static MarkerContext ResolveMarkerContext(List<ConsoleEntrySnapshot> entries, ReadConsoleParams parameters)
        {
            var markerId = parameters.AfterMarkerId;
            var context = new MarkerContext
            {
                Requested = !string.IsNullOrEmpty(markerId),
                MarkerId = markerId,
                IncludeMarker = parameters.IncludeMarker ?? false
            };

            if (!context.Requested)
            {
                return context;
            }

            var markerEntry = FindMarkerEntry(entries, markerId);
            if (markerEntry == null)
            {
                var storedMarker = SessionState.GetString(GetMarkerSessionStateKey(markerId), null);
                if (!string.IsNullOrEmpty(storedMarker))
                {
                    context.Known = true;
                    context.FallbackReason = "Marker entry is not present in the current Unity Console backlog; using the current backlog because the marker is known in this Unity session.";
                    return context;
                }

                return context;
            }

            context.Known = true;
            context.EntryFound = true;
            context.EntryId = markerEntry.EntryId;
            context.Message = markerEntry.Message;
            return context;
        }

        static ConsoleEntrySnapshot FindMarkerEntry(List<ConsoleEntrySnapshot> entries, string markerId)
        {
            if (entries == null || string.IsNullOrWhiteSpace(markerId))
            {
                return null;
            }

            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var entry = entries[index];
                if (entry.Message.IndexOf(MarkerPrefix, StringComparison.Ordinal) >= 0 &&
                    entry.Message.IndexOf($"id={markerId}", StringComparison.Ordinal) >= 0)
                {
                    return entry;
                }
            }

            return null;
        }

        static object ToMarkerContextData(MarkerContext context)
        {
            if (context == null || !context.Requested)
            {
                return null;
            }

            return new
            {
                afterMarkerId = context.MarkerId,
                found = context.Known,
                markerEntryFound = context.EntryFound,
                includeMarker = context.IncludeMarker,
                markerEntryId = context.EntryFound ? context.EntryId : (int?)null,
                markerMessage = context.Message,
                fallbackReason = context.FallbackReason
            };
        }

        static IEnumerable<ConsoleEntrySnapshot> FilterEntries(
            IEnumerable<ConsoleEntrySnapshot> entries,
            ReadConsoleParams parameters,
            MarkerContext markerContext)
        {
            var typeFilters = parameters.Types ?? Array.Empty<ConsoleLogType>();
            foreach (var entry in entries)
            {
                if (markerContext.Requested)
                {
                    if (!markerContext.Known)
                    {
                        yield break;
                    }

                    if (markerContext.EntryFound &&
                        (entry.EntryId < markerContext.EntryId ||
                        (!markerContext.IncludeMarker && entry.EntryId == markerContext.EntryId)))
                    {
                        continue;
                    }
                }

                if (!MatchesRequestedType(entry.Type, typeFilters))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(parameters.FilterText) &&
                    !ContainsConsoleText(entry, parameters.FilterText))
                {
                    continue;
                }

                yield return entry;
            }
        }

        static bool ContainsConsoleText(ConsoleEntrySnapshot entry, string filterText)
        {
            if (entry == null || string.IsNullOrEmpty(filterText))
            {
                return false;
            }

            return ContainsOrdinalIgnoreCase(entry.Message, filterText)
                || ContainsOrdinalIgnoreCase(entry.FullMessage, filterText)
                || ContainsOrdinalIgnoreCase(entry.StackTrace, filterText);
        }

        static bool ContainsOrdinalIgnoreCase(string value, string filterText)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool MatchesRequestedType(LogType entryType, ConsoleLogType[] requestedTypes)
        {
            if (requestedTypes == null || requestedTypes.Length == 0 || requestedTypes.Contains(ConsoleLogType.All))
            {
                return true;
            }

            foreach (var requestedType in requestedTypes)
            {
                switch (requestedType)
                {
                    case ConsoleLogType.Log:
                        if (entryType == LogType.Log) return true;
                        break;
                    case ConsoleLogType.Warning:
                        if (entryType == LogType.Warning) return true;
                        break;
                    case ConsoleLogType.Error:
                        if (entryType == LogType.Error || entryType == LogType.Exception || entryType == LogType.Assert) return true;
                        break;
                    case ConsoleLogType.Exception:
                        if (entryType == LogType.Exception) return true;
                        break;
                    case ConsoleLogType.Assert:
                        if (entryType == LogType.Assert) return true;
                        break;
                }
            }

            return false;
        }

        static List<ConsoleGroupSnapshot> BuildGroups(List<ConsoleEntrySnapshot> entries)
        {
            return entries
                .GroupBy(entry => entry.Fingerprint, StringComparer.Ordinal)
                .Select(group =>
                {
                    var ordered = group.OrderBy(entry => entry.EntryId).ToList();
                    var first = ordered[0];
                    var last = ordered[ordered.Count - 1];
                    return new ConsoleGroupSnapshot
                    {
                        Fingerprint = group.Key,
                        Type = first.Type,
                        Count = ordered.Count,
                        RepresentativeMessage = first.Message,
                        SampleStackTrace = first.StackTrace,
                        File = first.File,
                        Line = first.Line,
                        FirstEntryId = first.EntryId,
                        LastEntryId = last.EntryId,
                        Entries = ordered
                    };
                })
                .ToList();
        }

        static object BuildTotals(List<ConsoleEntrySnapshot> entries)
        {
            return new
            {
                totalEntries = entries.Count,
                totalGroups = BuildGroups(entries).Count,
                logCount = entries.Count(entry => entry.Type == LogType.Log),
                warningCount = entries.Count(entry => entry.Type == LogType.Warning),
                errorCount = entries.Count(entry => entry.Type == LogType.Error),
                exceptionCount = entries.Count(entry => entry.Type == LogType.Exception),
                assertCount = entries.Count(entry => entry.Type == LogType.Assert)
            };
        }

        static List<ConsoleEntrySnapshot> SelectTimelineEntries(
            List<ConsoleEntrySnapshot> entries,
            ReadConsoleParams parameters,
            bool allowTailFallback,
            out object selection)
        {
            var contextBefore = parameters.ContextBefore ?? DefaultContextBefore;
            var contextAfter = parameters.ContextAfter ?? DefaultContextAfter;

            if (entries.Count == 0)
            {
                selection = new
                {
                    kind = "empty",
                    contextBefore,
                    contextAfter
                };
                return new List<ConsoleEntrySnapshot>();
            }

            if (!string.IsNullOrEmpty(parameters.Fingerprint))
            {
                var matchingIndexes = entries
                    .Select((entry, index) => new { entry, index })
                    .Where(item => string.Equals(item.entry.Fingerprint, parameters.Fingerprint, StringComparison.Ordinal))
                    .Select(item => item.index)
                    .ToList();

                if (matchingIndexes.Count > 0)
                {
                    var startIndex = Math.Max(0, matchingIndexes.First() - contextBefore);
                    var endIndex = Math.Min(entries.Count - 1, matchingIndexes.Last() + contextAfter);
                    selection = BuildSelectionData(
                        "fingerprint",
                        entries,
                        startIndex,
                        endIndex,
                        parameters,
                        parameters.Fingerprint);
                    return SliceEntries(entries, startIndex, endIndex);
                }
            }

            if (parameters.StartEntryId.HasValue || parameters.EndEntryId.HasValue)
            {
                var startId = parameters.StartEntryId ?? entries.First().EntryId;
                var endId = parameters.EndEntryId ?? entries.Last().EntryId;
                if (endId < startId)
                {
                    var tmp = startId;
                    startId = endId;
                    endId = tmp;
                }

                var selected = entries
                    .Where(entry => entry.EntryId >= startId && entry.EntryId <= endId)
                    .ToList();

                selection = selected.Count == 0
                    ? new
                    {
                        kind = "entry_range",
                        requestedStartEntryId = startId,
                        requestedEndEntryId = endId,
                        matched = false,
                        contextBefore,
                        contextAfter
                    }
                    : BuildSelectionData(
                        "entry_range",
                        selected,
                        0,
                        selected.Count - 1,
                        parameters,
                        null);
                return selected;
            }

            if (parameters.CenterEntryId.HasValue)
            {
                var centerId = parameters.CenterEntryId.Value;
                var centerIndex = entries.FindIndex(entry => entry.EntryId >= centerId);
                if (centerIndex < 0)
                {
                    centerIndex = entries.Count - 1;
                }

                var startIndex = Math.Max(0, centerIndex - contextBefore);
                var endIndex = Math.Min(entries.Count - 1, centerIndex + contextAfter);
                selection = BuildSelectionData("center", entries, startIndex, endIndex, parameters, null);
                return SliceEntries(entries, startIndex, endIndex);
            }

            if (allowTailFallback)
            {
                var maxEvents = parameters.MaxEvents ?? DefaultTimelineEventCount;
                var startIndex = Math.Max(0, entries.Count - maxEvents);
                var endIndex = entries.Count - 1;
                selection = BuildSelectionData("tail", entries, startIndex, endIndex, parameters, null);
                return SliceEntries(entries, startIndex, endIndex);
            }

            selection = new
            {
                kind = "none",
                matched = false,
                contextBefore,
                contextAfter,
                note = "Provide StartEntryId/EndEntryId, CenterEntryId, or Fingerprint for TimelineWindow."
            };
            return new List<ConsoleEntrySnapshot>();
        }

        static object BuildSelectionData(
            string kind,
            List<ConsoleEntrySnapshot> entries,
            int startIndex,
            int endIndex,
            ReadConsoleParams parameters,
            string fingerprint)
        {
            var start = entries[startIndex];
            var end = entries[endIndex];
            return new
            {
                kind,
                matched = true,
                requestedStartEntryId = parameters.StartEntryId,
                requestedEndEntryId = parameters.EndEntryId,
                requestedCenterEntryId = parameters.CenterEntryId,
                fingerprint,
                contextBefore = parameters.ContextBefore ?? DefaultContextBefore,
                contextAfter = parameters.ContextAfter ?? DefaultContextAfter,
                startEntryId = start.EntryId,
                endEntryId = end.EntryId,
                eventCount = endIndex - startIndex + 1
            };
        }

        static object ToWindowData(List<ConsoleEntrySnapshot> entries)
        {
            var groups = BuildGroups(entries);
            return new
            {
                startEntryId = entries.First().EntryId,
                endEntryId = entries.Last().EntryId,
                eventCount = entries.Count,
                groupCount = groups.Count,
                logCount = entries.Count(entry => entry.Type == LogType.Log),
                warningCount = entries.Count(entry => entry.Type == LogType.Warning),
                errorCount = entries.Count(entry => entry.Type == LogType.Error),
                exceptionCount = entries.Count(entry => entry.Type == LogType.Exception),
                assertCount = entries.Count(entry => entry.Type == LogType.Assert),
                topGroups = groups
                    .OrderByDescending(group => GetSeverityRank(group.Type))
                    .ThenByDescending(group => group.Count)
                    .Take(5)
                    .Select(group => ToGroupData(group, includeStacktrace: false))
                    .ToArray()
            };
        }

        static List<ConsoleEntrySnapshot> SliceEntries(List<ConsoleEntrySnapshot> entries, int startIndex, int endIndex)
        {
            if (entries.Count == 0 || endIndex < startIndex)
            {
                return new List<ConsoleEntrySnapshot>();
            }

            return entries
                .Skip(startIndex)
                .Take(endIndex - startIndex + 1)
                .ToList();
        }

        static bool ShouldCollapseRepeats(ReadConsoleParams parameters)
        {
            return parameters.CollapseRepeats ?? true;
        }

        static IEnumerable<object> BuildCompressedTimeline(
            List<ConsoleEntrySnapshot> entries,
            ReadConsoleParams parameters,
            int maxSegments)
        {
            if (entries == null || entries.Count == 0 || maxSegments <= 0)
            {
                yield break;
            }

            var collapse = ShouldCollapseRepeats(parameters);
            var threshold = parameters.CollapseThreshold ?? DefaultCollapseThreshold;
            var emitted = 0;

            foreach (var run in BuildRuns(entries))
            {
                if (emitted >= maxSegments)
                {
                    yield break;
                }

                if (collapse && run.Count >= threshold)
                {
                    yield return new
                    {
                        kind = "repeat",
                        type = run.Type.ToString(),
                        count = run.Count,
                        fingerprint = run.Fingerprint,
                        message = run.RepresentativeMessage,
                        file = run.File,
                        line = run.Line,
                        startEntryId = run.FirstEntryId,
                        endEntryId = run.LastEntryId
                    };
                    emitted++;
                    continue;
                }

                foreach (var entry in run.Entries)
                {
                    if (emitted >= maxSegments)
                    {
                        yield break;
                    }

                    yield return new
                    {
                        kind = "entry",
                        entry = ToEntryData(entry, includeStacktrace: false)
                    };
                    emitted++;
                }
            }
        }

        static List<ConsoleRunSnapshot> BuildRuns(List<ConsoleEntrySnapshot> entries)
        {
            var runs = new List<ConsoleRunSnapshot>();
            if (entries.Count == 0)
            {
                return runs;
            }

            var startIndex = 0;
            for (var index = 1; index <= entries.Count; index++)
            {
                var atEnd = index == entries.Count;
                if (!atEnd && string.Equals(entries[index].Fingerprint, entries[startIndex].Fingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                var runEntries = entries
                    .Skip(startIndex)
                    .Take(index - startIndex)
                    .ToList();
                var first = runEntries[0];
                var last = runEntries[runEntries.Count - 1];
                runs.Add(new ConsoleRunSnapshot
                {
                    StartIndex = startIndex,
                    EndIndex = index - 1,
                    Fingerprint = first.Fingerprint,
                    Type = first.Type,
                    Count = runEntries.Count,
                    RepresentativeMessage = first.Message,
                    File = first.File,
                    Line = first.Line,
                    FirstEntryId = first.EntryId,
                    LastEntryId = last.EntryId,
                    Entries = runEntries
                });
                startIndex = index;
            }

            return runs;
        }

        static IEnumerable<RangeCandidate> DetectImportantRanges(
            List<ConsoleEntrySnapshot> entries,
            ReadConsoleParams parameters)
        {
            var candidates = new List<RangeCandidate>();
            if (entries.Count == 0)
            {
                return candidates;
            }

            var before = parameters.ContextBefore ?? DefaultContextBefore;
            var after = parameters.ContextAfter ?? DefaultContextAfter;

            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry.Type == LogType.Exception || entry.Type == LogType.Error || entry.Type == LogType.Assert || entry.Type == LogType.Warning)
                {
                    AddRangeCandidate(
                        candidates,
                        entries,
                        Math.Max(0, index - before),
                        Math.Min(entries.Count - 1, index + after),
                        entry.Type == LogType.Warning ? "warning_context" : "critical_context",
                        $"{entry.Type} entry context",
                        100 + GetSeverityRank(entry.Type) * 10,
                        entry.EntryId);
                }
            }

            var runs = BuildRuns(entries);
            foreach (var run in runs)
            {
                if (run.Count < SpamRunThreshold)
                {
                    continue;
                }

                var score = run.Type == LogType.Log ? 20 : 60 + GetSeverityRank(run.Type) * 5;
                AddRangeCandidate(
                    candidates,
                    entries,
                    run.StartIndex,
                    run.EndIndex,
                    "repeated_run",
                    $"Repeated {run.Type} run x{run.Count}",
                    score,
                    run.FirstEntryId);
            }

            for (var index = 0; index < runs.Count - 1; index++)
            {
                var current = runs[index];
                var next = runs[index + 1];
                if (current.Count >= SpamRunThreshold && !string.Equals(current.Fingerprint, next.Fingerprint, StringComparison.Ordinal))
                {
                    AddRangeCandidate(
                        candidates,
                        entries,
                        Math.Max(0, current.EndIndex - Math.Min(before, 20)),
                        Math.Min(entries.Count - 1, next.StartIndex + Math.Min(after, 40)),
                        "pattern_change_after_repetition",
                        $"Pattern changed after repeated {current.Type} run x{current.Count}",
                        45 + GetSeverityRank(next.Type) * 5,
                        next.FirstEntryId);
                }
            }

            DetectAlternatingRanges(entries, candidates, parameters);

            return SelectDistinctRanges(candidates, parameters.MaxRanges ?? DefaultMaxRanges);
        }

        static void DetectAlternatingRanges(
            List<ConsoleEntrySnapshot> entries,
            List<RangeCandidate> candidates,
            ReadConsoleParams parameters)
        {
            const int windowSize = 40;
            const int step = 20;
            if (entries.Count < 12)
            {
                return;
            }

            for (var start = 0; start < entries.Count; start += step)
            {
                var end = Math.Min(entries.Count - 1, start + windowSize - 1);
                var window = SliceEntries(entries, start, end);
                if (window.Count < 12)
                {
                    continue;
                }

                var distinctFingerprints = window.Select(entry => entry.Fingerprint).Distinct().Count();
                if (distinctFingerprints < 2 || distinctFingerprints > 4)
                {
                    continue;
                }

                var switches = 0;
                for (var index = 1; index < window.Count; index++)
                {
                    if (!string.Equals(window[index - 1].Fingerprint, window[index].Fingerprint, StringComparison.Ordinal))
                    {
                        switches++;
                    }
                }

                if (switches < window.Count / 2)
                {
                    continue;
                }

                AddRangeCandidate(
                    candidates,
                    entries,
                    start,
                    end,
                    "alternating_patterns",
                    $"Alternating log patterns ({distinctFingerprints} fingerprints, {switches} switches)",
                    55,
                    window[0].EntryId);
            }
        }

        static void AddRangeCandidate(
            List<RangeCandidate> candidates,
            List<ConsoleEntrySnapshot> entries,
            int startIndex,
            int endIndex,
            string kind,
            string reason,
            int score,
            int anchorEntryId)
        {
            if (entries.Count == 0)
            {
                return;
            }

            startIndex = Math.Max(0, Math.Min(startIndex, entries.Count - 1));
            endIndex = Math.Max(0, Math.Min(endIndex, entries.Count - 1));
            if (endIndex < startIndex)
            {
                var tmp = startIndex;
                startIndex = endIndex;
                endIndex = tmp;
            }

            candidates.Add(new RangeCandidate
            {
                Kind = kind,
                Reason = reason,
                Score = score,
                StartIndex = startIndex,
                EndIndex = endIndex,
                AnchorEntryId = anchorEntryId
            });
        }

        static List<RangeCandidate> SelectDistinctRanges(List<RangeCandidate> candidates, int maxRanges)
        {
            var selected = new List<RangeCandidate>();
            foreach (var candidate in candidates
                         .OrderByDescending(range => range.Score)
                         .ThenBy(range => range.StartIndex))
            {
                if (selected.Any(existing => HasStrongOverlap(existing, candidate)))
                {
                    continue;
                }

                selected.Add(candidate);
                if (selected.Count >= maxRanges)
                {
                    break;
                }
            }

            return selected
                .OrderBy(range => range.StartIndex)
                .ToList();
        }

        static bool HasStrongOverlap(RangeCandidate a, RangeCandidate b)
        {
            var overlapStart = Math.Max(a.StartIndex, b.StartIndex);
            var overlapEnd = Math.Min(a.EndIndex, b.EndIndex);
            if (overlapEnd < overlapStart)
            {
                return false;
            }

            var overlap = overlapEnd - overlapStart + 1;
            var smaller = Math.Min(a.EndIndex - a.StartIndex + 1, b.EndIndex - b.StartIndex + 1);
            return overlap >= Math.Max(1, smaller / 2);
        }

        static object ToRangeData(RangeCandidate range, List<ConsoleEntrySnapshot> entries, ReadConsoleParams parameters)
        {
            var windowEntries = SliceEntries(entries, range.StartIndex, range.EndIndex);
            var groups = BuildGroups(windowEntries)
                .OrderByDescending(group => GetSeverityRank(group.Type))
                .ThenByDescending(group => group.Count)
                .Take(5)
                .Select(group => ToGroupData(group, includeStacktrace: false))
                .ToArray();

            return new
            {
                kind = range.Kind,
                reason = range.Reason,
                score = range.Score,
                startEntryId = windowEntries.First().EntryId,
                endEntryId = windowEntries.Last().EntryId,
                anchorEntryId = range.AnchorEntryId,
                eventCount = windowEntries.Count,
                groupCount = groups.Length,
                groups,
                compressedPreview = BuildCompressedTimeline(
                        windowEntries,
                        parameters,
                        parameters.MaxSamples ?? DefaultMaxSamples)
                    .ToArray(),
                openWith = new
                {
                    action = "TimelineWindow",
                    startEntryId = windowEntries.First().EntryId,
                    endEntryId = windowEntries.Last().EntryId
                }
            };
        }

        static object ToGroupData(ConsoleGroupSnapshot group, bool includeStacktrace)
        {
            return new
            {
                fingerprint = group.Fingerprint,
                type = group.Type.ToString(),
                count = group.Count,
                representativeMessage = group.RepresentativeMessage,
                file = group.File,
                line = group.Line,
                firstEntryId = group.FirstEntryId,
                lastEntryId = group.LastEntryId,
                sampleStackTrace = includeStacktrace ? group.SampleStackTrace : null
            };
        }

        static object ToEntryData(ConsoleEntrySnapshot entry, bool includeStacktrace)
        {
            return new
            {
                entryId = entry.EntryId,
                type = entry.Type.ToString(),
                message = entry.Message,
                file = entry.File,
                line = entry.Line,
                fingerprint = entry.Fingerprint,
                stackTrace = includeStacktrace ? entry.StackTrace : null
            };
        }

        static ConsoleLogEntry ToConsoleLogEntry(ConsoleEntrySnapshot entry, bool includeStacktrace, ConsoleOutputFormat format)
        {
            if (format == ConsoleOutputFormat.Plain)
            {
                return new ConsoleLogEntry
                {
                    Message = entry.Message,
                    Type = entry.Type.ToString()
                };
            }

            return new ConsoleLogEntry
            {
                Type = entry.Type.ToString(),
                Message = entry.Message,
                File = entry.File,
                Line = entry.Line,
                StackTrace = includeStacktrace ? entry.StackTrace : null
            };
        }

        static IEnumerable<T> LastItems<T>(IList<T> items, int count)
        {
            if (items == null || count <= 0)
            {
                yield break;
            }

            var start = Math.Max(0, items.Count - count);
            for (var index = start; index < items.Count; index++)
            {
                yield return items[index];
            }
        }

        static string CreateMarkerId()
        {
            return $"ubm-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        }

        static string GetMarkerSessionStateKey(string markerId)
        {
            return $"{MarkerSessionStatePrefix}{markerId}";
        }

        static string NormalizeMarkerLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var label = WhitespaceRegex.Replace(value.Trim(), " ")
                .Replace("\"", "'");

            return label.Length <= 80
                ? label
                : label.Substring(0, 80);
        }

        static string ExtractMessageOnly(string fullMessage, string stackTrace)
        {
            if (string.IsNullOrEmpty(fullMessage))
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(stackTrace))
            {
                return fullMessage.Trim();
            }

            var lines = fullMessage.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? fullMessage.Trim() : lines[0].Trim();
        }

        static string BuildFingerprint(LogType logType, string message, string stackTrace)
        {
            var normalizedMessage = NormalizeText(message);
            var normalizedFrame = NormalizeText(GetPrimaryStackFrame(stackTrace));
            return $"{logType}:{normalizedMessage}|{normalizedFrame}";
        }

        static string GetPrimaryStackFrame(string stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
            {
                return null;
            }

            return stackTrace
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }

        static string NormalizeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            normalized = GuidRegex.Replace(normalized, "<guid>");
            normalized = HexRegex.Replace(normalized, "<hex>");
            normalized = NumberRegex.Replace(normalized, "<n>");
            normalized = WhitespaceRegex.Replace(normalized, " ");
            return normalized;
        }

        static int GetSeverityRank(LogType logType)
        {
            switch (logType)
            {
                case LogType.Exception:
                    return 5;
                case LogType.Error:
                    return 4;
                case LogType.Assert:
                    return 3;
                case LogType.Warning:
                    return 2;
                default:
                    return 1;
            }
        }

        sealed class ConsoleEntrySnapshot
        {
            public int EntryId { get; set; }
            public LogType Type { get; set; }
            public string Message { get; set; }
            public string FullMessage { get; set; }
            public string File { get; set; }
            public int Line { get; set; }
            public string StackTrace { get; set; }
            public string Fingerprint { get; set; }
        }

        sealed class ConsoleGroupSnapshot
        {
            public string Fingerprint { get; set; }
            public LogType Type { get; set; }
            public int Count { get; set; }
            public string RepresentativeMessage { get; set; }
            public string SampleStackTrace { get; set; }
            public string File { get; set; }
            public int Line { get; set; }
            public int FirstEntryId { get; set; }
            public int LastEntryId { get; set; }
            public List<ConsoleEntrySnapshot> Entries { get; set; }
        }

        sealed class ConsoleRunSnapshot
        {
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public string Fingerprint { get; set; }
            public LogType Type { get; set; }
            public int Count { get; set; }
            public string RepresentativeMessage { get; set; }
            public string File { get; set; }
            public int Line { get; set; }
            public int FirstEntryId { get; set; }
            public int LastEntryId { get; set; }
            public List<ConsoleEntrySnapshot> Entries { get; set; }
        }

        sealed class RangeCandidate
        {
            public string Kind { get; set; }
            public string Reason { get; set; }
            public int Score { get; set; }
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public int AnchorEntryId { get; set; }
        }

        sealed class MarkerContext
        {
            public bool Requested { get; set; }
            public bool Known { get; set; }
            public bool EntryFound { get; set; }
            public string MarkerId { get; set; }
            public bool IncludeMarker { get; set; }
            public int EntryId { get; set; }
            public string Message { get; set; }
            public string FallbackReason { get; set; }
        }

        // --- Internal Helpers ---

        // Mapping bits from LogEntry.mode. These vary by Unity version.
        // Unity changed bit positions for log types between major versions.
        //
        // Unity 6000.x (verified via diagnostic logging on 6000.2.9f1):
        //   Error=0x100 (bit 8), Warning=0x200 (bit 9), Log=0x400 (bit 10)
        //
        // Unity 2023.x and earlier (based on historical codebase):
        //   Error=0x1 (bit 0), Warning=0x4 (bit 2), Log=0x8 (bit 3)
        //
        // Using conditional compilation to select correct bit positions at compile time.

#if UNITY_6000_0_OR_NEWER
        // Unity 6000.x uses higher bit positions
        const int ModeBitError = 1 << 8;        // 0x100
        const int ModeBitWarning = 1 << 9;      // 0x200
        const int ModeBitLog = 1 << 10;         // 0x400
#else
        // Unity 2023.x and earlier use lower bit positions
        const int ModeBitError = 1 << 0;        // 0x1
        const int ModeBitWarning = 1 << 2;      // 0x4
        const int ModeBitLog = 1 << 3;          // 0x8
#endif

        // These appear consistent across versions
        const int ModeBitAssert = 1 << 1;
        const int ModeBitException = 1 << 4;

        static LogType GetLogTypeFromMode(int mode)
        {
            // Check individual bits (positions are version-specific, see constants above)
            // Check each bit independently without OR-ing to avoid false positives
            if ((mode & ModeBitException) != 0) return LogType.Exception;
            if ((mode & ModeBitError) != 0) return LogType.Error;
            if ((mode & ModeBitAssert) != 0) return LogType.Assert;
            if ((mode & ModeBitWarning) != 0) return LogType.Warning;
            if ((mode & ModeBitLog) != 0) return LogType.Log;
            return LogType.Log; // Default fallback
        }

        // (Calibration helpers removed)

        /// <summary>
        /// Classifies severity using message/stacktrace content. Works across Unity versions.
        /// </summary>
        static LogType InferTypeFromMessage(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage)) return LogType.Log;

            var firstLine = GetFirstLine(fullMessage);

            if (ContainsCriticalBuildSystemText(fullMessage))
                return LogType.Error;

            // Fast path: look for explicit Debug API names in the appended stack trace
            // e.g., "UnityEngine.Debug:LogError (object)" or "LogWarning"
            if (fullMessage.IndexOf("LogException", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Exception;
            if (fullMessage.IndexOf("LogError", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Error;
            if (fullMessage.IndexOf("LogWarning", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Warning;

            // Compiler diagnostics (C#): "warning CSxxxx" / "error CSxxxx"
            if (firstLine.IndexOf(" warning CS", StringComparison.OrdinalIgnoreCase) >= 0
                || firstLine.IndexOf(": warning CS", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Warning;
            if (firstLine.IndexOf(" error CS", StringComparison.OrdinalIgnoreCase) >= 0
                || firstLine.IndexOf(": error CS", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Error;

            // Exceptions (avoid matching System.Exception in ordinary stack-frame signatures)
            if (firstLine.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Exception;

            // Unity assertions
            if (firstLine.IndexOf("Assertion", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Assert;

            return LogType.Log;
        }

        static bool IsCriticalBuildSystemGroup(ConsoleGroupSnapshot group)
        {
            if (group == null)
            {
                return false;
            }

            return ContainsCriticalBuildSystemText(group.RepresentativeMessage)
                || ContainsCriticalBuildSystemText(group.SampleStackTrace);
        }

        static bool ContainsCriticalBuildSystemText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return CriticalBuildSystemNeedles.Any(
                needle => value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string GetFirstLine(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var newlineIndex = value.IndexOf('\n');
            return newlineIndex >= 0
                ? value.Substring(0, newlineIndex)
                : value;
        }

        static LogType RefineLogTypeFromMessage(LogType modeType, string fullMessage)
        {
            var inferredType = InferTypeFromMessage(fullMessage);

            if (inferredType == LogType.Exception || inferredType == LogType.Assert)
            {
                return inferredType;
            }

            if (modeType == LogType.Log && (inferredType == LogType.Warning || inferredType == LogType.Error))
            {
                return inferredType;
            }

            return modeType;
        }

        static bool IsExplicitDebugLog(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage)) return false;
            if (fullMessage.IndexOf("Debug:Log (", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fullMessage.IndexOf("UnityEngine.Debug:Log (", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Applies the console mode remapping used by Unity's internal log filters.
        /// This ensures compatibility with the filtering logic that expects remapped types.
        /// </summary>
        static LogType GetRemappedTypeForFiltering(LogType unityType)
        {
            switch (unityType)
            {
                case LogType.Error:
                    return LogType.Warning; // Error becomes Warning
                case LogType.Warning:
                    return LogType.Log; // Warning becomes Log
                case LogType.Assert:
                    return LogType.Assert; // Assert remains Assert
                case LogType.Log:
                    return LogType.Log; // Log remains Log
                case LogType.Exception:
                    return LogType.Warning; // Exception becomes Warning
                default:
                    return LogType.Log; // Default fallback
            }
        }

        /// <summary>
        /// Attempts to extract the stack trace part from a log message.
        /// Unity log messages often have the stack trace appended after the main message,
        /// starting on a new line and typically indented or beginning with "at ".
        /// </summary>
        /// <param name="fullMessage">The complete log message including potential stack trace.</param>
        /// <returns>The extracted stack trace string, or null if none is found.</returns>
        static string ExtractStackTrace(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage))
                return null;

            // Split into lines, removing empty ones to handle different line endings gracefully.
            // Using StringSplitOptions.None might be better if empty lines matter within stack trace, but RemoveEmptyEntries is usually safer here.
            string[] lines = fullMessage.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries
            );

            // If there's only one line or less, there's no separate stack trace.
            if (lines.Length <= 1)
                return null;

            int stackStartIndex = -1;

            // Start checking from the second line onwards.
            for (int i = 1; i < lines.Length; ++i)
            {
                // Performance: TrimStart creates a new string. Consider using IsWhiteSpace check if performance critical.
                string trimmedLine = lines[i].TrimStart();

                // Check for common stack trace patterns.
                if (
                    trimmedLine.StartsWith("at ")
                    || trimmedLine.StartsWith("UnityEngine.")
                    || trimmedLine.StartsWith("UnityEditor.")
                    || trimmedLine.Contains("(at ")
                    || // Covers "(at Assets/..." pattern
                    // Heuristic: Check if line starts with likely namespace/class pattern (Uppercase.Something)
                    (
                        trimmedLine.Length > 0
                        && char.IsUpper(trimmedLine[0])
                        && trimmedLine.Contains('.')
                    )
                )
                {
                    stackStartIndex = i;
                    break; // Found the likely start of the stack trace
                }
            }

            // If a potential start index was found...
            if (stackStartIndex > 0)
            {
                // Join the lines from the stack start index onwards using standard newline characters.
                // This reconstructs the stack trace part of the message.
                return string.Join("\n", lines.Skip(stackStartIndex));
            }

            // No clear stack trace found based on the patterns.
            return null;
        }

        /* LogEntry.mode bits exploration (based on Unity decompilation/observation):
           May change between versions.

           Basic Types:
           kError = 1 << 0 (1)
           kAssert = 1 << 1 (2)
           kWarning = 1 << 2 (4)
           kLog = 1 << 3 (8)
           kFatal = 1 << 4 (16) - Often treated as Exception/Error

           Modifiers/Context:
           kAssetImportError = 1 << 7 (128)
           kAssetImportWarning = 1 << 8 (256)
           kScriptingError = 1 << 9 (512)
           kScriptingWarning = 1 << 10 (1024)
           kScriptingLog = 1 << 11 (2048)
           kScriptCompileError = 1 << 12 (4096)
           kScriptCompileWarning = 1 << 13 (8192)
           kStickyError = 1 << 14 (16384) - Stays visible even after Clear On Play
           kMayIgnoreLineNumber = 1 << 15 (32768)
           kReportBug = 1 << 16 (65536) - Shows the "Report Bug" button
           kDisplayPreviousErrorInStatusBar = 1 << 17 (131072)
           kScriptingException = 1 << 18 (262144)
           kDontExtractStacktrace = 1 << 19 (524288) - Hint to the console UI
           kShouldClearOnPlay = 1 << 20 (1048576) - Default behavior
           kGraphCompileError = 1 << 21 (2097152)
           kScriptingAssertion = 1 << 22 (4194304)
           kVisualScriptingError = 1 << 23 (8388608)

           Example observed values:
           Log: 2048 (ScriptingLog) or 8 (Log)
           Warning: 1028 (ScriptingWarning | Warning) or 4 (Warning)
           Error: 513 (ScriptingError | Error) or 1 (Error)
           Exception: 262161 (ScriptingException | Error | kFatal?) - Complex combination
           Assertion: 4194306 (ScriptingAssertion | Assert) or 2 (Assert)
        */
    }
}
