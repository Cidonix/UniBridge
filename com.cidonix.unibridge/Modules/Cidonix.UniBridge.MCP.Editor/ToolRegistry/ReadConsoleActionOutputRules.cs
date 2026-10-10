using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using static Cidonix.UniBridge.MCP.Editor.ToolRegistry.ActionOutputSchema;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    // ReadConsole's exact serialized producers, including aliases and empty windows.
    internal static class ReadConsoleActionOutputRules
    {
        static JObject S() => Type("string");
        static JObject I() => Type("integer");
        static JObject B() => Type("boolean");
        static JObject NS() => Nullable(S());
        static JObject NI() => Nullable(I());
        static JObject Family(params JObject[] values)
        {
            var all = new List<JObject>(values) { Error() };
            return Compose(all);
        }
        internal static Dictionary<string, JObject> Build()
        {
            var result = new Dictionary<string, JObject>(StringComparer.Ordinal)
            {
                ["Clear"] = Family(Success()),
                ["Get"] = Family(Success(Arr(DTO(typeof(ConsoleLogEntry)))), Error(Marker())),
                ["ReadSinceMarker"] = Family(Success(Arr(DTO(typeof(ConsoleLogEntry)))), Error(Marker())),
                ["Search"] = Family(Success(Arr(DTO(typeof(ConsoleLogEntry)))), Error(Marker())),
                ["MarkSession"] = Family(Success(Obj(("action", Const("mark_session")), ("markerId", S()), ("label", S()),
                    ("createdUtc", S()), ("message", S()), ("backlogEntriesBefore", I()), ("markerEntryId", NI()), ("markerFound", B()),
                    ("storedInSessionState", B()), ("openWith", Obj(("afterMarkerId", S()), ("includeMarker", B()))),
                    ("examples", Obj(("overview", MarkerExample()), ("importantRanges", MarkerExample()), ("timeline", MarkerExample()),
                        ("search", Obj(("Action", S()), ("AfterMarkerId", S()), ("FilterText", S()))))), ("note", S())))),
                ["Overview"] = Family(Success(Obj(("action", Const("overview")), ("totalBacklogEntries", I()), ("filteredEntries", I()),
                    ("marker", Marker()), ("totals", Totals()), ("topGroups", Arr(Group())), ("recent", Arr(Entry())), ("note", S())))),
                ["Groups"] = Family(Success(Obj(("action", Const("groups")), ("filteredEntries", I()), ("marker", Marker()),
                    ("totalGroups", I()), ("returnedGroups", I()), ("groups", Arr(Group()))))),
                ["GroupDetails"] = Family(Success(Obj(("action", Const("group_details")), ("marker", Marker()), ("group", Group()),
                    ("returnedSamples", I()), ("samples", Arr(Entry())))), Error(Obj(("fingerprint", S()), ("marker", Marker())))),
                ["Timeline"] = Family(Success(Obj(("action", Const("timeline")), ("selection", Selection()), ("marker", Marker()),
                    ("filteredEntries", I()), ("totalSelectedEvents", I()), ("returnedEvents", I()), ("events", Arr(Entry())),
                    ("compressedEvents", Nullable(Arr(Compressed()))), ("note", S())))),
                ["TimelineWindow"] = Family(
                    Success(Obj(("action", Const("timeline_window")), ("filteredEntries", I()), ("marker", Marker()), ("selection", Selection()),
                        ("returnedEvents", I()), ("events", Arr(Entry())), ("compressedEvents", Arr(Compressed())), ("note", S()))),
                    Success(Obj(("action", Const("timeline_window")), ("filteredEntries", I()), ("marker", Marker()), ("selection", Selection()),
                        ("window", Window()), ("returnedEvents", I()), ("totalWindowEvents", I()), ("events", Arr(Entry())),
                        ("compressedEvents", Arr(Compressed())), ("note", S())))),
                ["DiagnosticSummary"] = Family(Success(DiagnosticSummaryData())),
                ["ImportantRanges"] = Family(Success(Obj(("action", Const("important_ranges")), ("filteredEntries", I()), ("marker", Marker()),
                    ("totalGroups", I()), ("returnedRanges", I()), ("ranges", Arr(Range())), ("noiseGroups", Arr(Group())), ("guidance", S()))))
            };
            result.Add("ClearConsole", (JObject)result["Clear"].DeepClone());
            result.Add("CreateMarker", (JObject)result["MarkSession"].DeepClone());
            return result;
        }
        // ContextSnapshot compact projection is authored explicitly in its producer.
        internal static JObject DiagnosticSummaryData(bool compact = false)
        {
            var fields = new List<(string, JObject)>
            {
                ("dominantIssue", Nullable(AnyOf(Group(), Spam()))), ("criticalIssues", Arr(Group())),
                ("warningIssues", Arr(Group())), ("likelySpam", Arr(Spam()))
            };
            if (!compact)
            {
                fields.Add(("recentSamples", Arr(Entry())));
                fields.Add(("timelineHighlights", Arr(Entry())));
            }
            var data = new List<(string, JObject)>
            {
                ("action", Const("diagnostic_summary")), ("marker", Marker()), ("totals", Totals()),
                ("summary", Obj(fields.ToArray()))
            };
            if (compact) data.Add(("omitted", new JObject { ["const"] = new JArray("recentSamples", "timelineHighlights") }));
            data.Add(("guidance", S()));
            return Obj(data.ToArray());
        }
        static JObject MarkerExample() => Obj(("Action", S()), ("AfterMarkerId", S()));
        internal static JObject Marker() => Nullable(Obj(("afterMarkerId", S()), ("found", B()), ("markerEntryFound", B()), ("includeMarker", B()),
            ("markerEntryId", NI()), ("markerMessage", NS()), ("fallbackReason", NS())));
        internal static JObject Totals() => Obj(("totalEntries", I()), ("totalGroups", I()), ("logCount", I()), ("warningCount", I()),
            ("errorCount", I()), ("exceptionCount", I()), ("assertCount", I()));
        internal static JObject Group() => Obj(("fingerprint", S()), ("type", S()), ("count", I()), ("representativeMessage", S()),
            ("file", NS()), ("line", I()), ("firstEntryId", I()), ("lastEntryId", I()), ("sampleStackTrace", NS()));
        internal static JObject Spam() => Obj(("fingerprint", S()), ("type", S()), ("count", I()), ("representativeMessage", S()), ("firstEntryId", I()), ("lastEntryId", I()));
        internal static JObject Entry() => Obj(("entryId", I()), ("type", S()), ("message", S()), ("file", NS()), ("line", I()), ("fingerprint", S()), ("stackTrace", NS()));
        static JObject Selection() => AnyOf(
            Obj(("kind", Const("empty")), ("contextBefore", I()), ("contextAfter", I())),
            Obj(("kind", Const("entry_range")), ("requestedStartEntryId", I()), ("requestedEndEntryId", I()), ("matched", Const(false)), ("contextBefore", I()), ("contextAfter", I())),
            Obj(("kind", Const("none")), ("matched", Const(false)), ("contextBefore", I()), ("contextAfter", I()), ("note", S())),
            Obj(("kind", Enum("fingerprint", "entry_range", "center", "tail")), ("matched", Const(true)), ("requestedStartEntryId", NI()),
                ("requestedEndEntryId", NI()), ("requestedCenterEntryId", NI()), ("fingerprint", NS()), ("contextBefore", I()), ("contextAfter", I()),
                ("startEntryId", I()), ("endEntryId", I()), ("eventCount", I())));
        static JObject Compressed() => AnyOf(
            Obj(("kind", Const("repeat")), ("type", S()), ("count", I()), ("fingerprint", S()), ("message", S()), ("file", NS()),
                ("line", I()), ("startEntryId", I()), ("endEntryId", I())),
            Obj(("kind", Const("entry")), ("entry", Entry())));
        static JObject Window() => Obj(("startEntryId", I()), ("endEntryId", I()), ("eventCount", I()), ("groupCount", I()),
            ("logCount", I()), ("warningCount", I()), ("errorCount", I()), ("exceptionCount", I()), ("assertCount", I()), ("topGroups", Arr(Group())));
        static JObject Range() => Obj(("kind", S()), ("reason", S()), ("score", I()), ("startEntryId", I()), ("endEntryId", I()), ("anchorEntryId", I()),
            ("eventCount", I()), ("groupCount", I()), ("groups", Arr(Group())), ("compressedPreview", Arr(Compressed())),
            ("openWith", Obj(("action", S()), ("startEntryId", I()), ("endEntryId", I()))));
    }
}
