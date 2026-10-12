using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal sealed class RecordedBottomUpGroup
    {
        public string groupId, threadId, threadName, threadGroupName, name, category;
        public int markerId, categoryId, physicalCallCount, framesObserved;
        public double inclusiveTimeMs, knownSelfTimeMs;
        public double? selfTimeMs;
        public bool selfTimeComplete, rawCoverageComplete, gcSumOverflow;
        public long knownGcAllocationBytes;
        public long? gcAllocationBytes;
        public int gcAllocationEvents, unknownGcAllocationEvents;
        public string[] sampleIds = Array.Empty<string>(), issues = Array.Empty<string>();
        public bool sampleIdsTruncated;
    }
    internal sealed class RecordedBottomUpResult
    {
        public bool complete, timedOut, groupsTruncated;
        public int observedGroups, physicalSamples;
        public RecordedBottomUpGroup[] groups = Array.Empty<RecordedBottomUpGroup>();
        public string[] issues = Array.Empty<string>();
    }
    // Physical occurrences remain physical. Aggregate IDs cannot be used as sample IDs.
    internal static class RecordedProfilerBottomUp
    {
        sealed class Accumulator
        {
            public RecordedBottomUpGroup Value;
            public readonly Dictionary<int, List<(ulong start, ulong end)>> Intervals = new();
            public readonly HashSet<int> Frames = new();
            public readonly List<string> Samples = new();
            public readonly HashSet<string> Issues = new(StringComparer.Ordinal);
            public bool selfKnown = true, gcKnown = true;
        }
        public static RecordedBottomUpResult Analyze(RecordedProfilerReadResult read, int maxGroups, string[] includes, string[] excludes,
            string[] categories, RecordedProfilerSort sort, Func<bool> expired = null, Action cancelled = null)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            if (maxGroups < 1 || maxGroups > 300) throw new ArgumentOutOfRangeException(nameof(maxGroups));
            var result = new RecordedBottomUpResult { complete = read.Complete };
            var issues = new HashSet<string>(read.Issues, StringComparer.Ordinal);
            var groups = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
            bool Check()
            {
                cancelled?.Invoke();
                if (expired?.Invoke() != true) return false;
                result.complete = false; result.timedOut = true; issues.Add("AGGREGATION_TIMEOUT"); return true;
            }
            foreach (var frame in read.Frames)
            {
                if (Check()) break;
                foreach (var analysis in frame.Threads)
                {
                    if (Check()) break;
                    result.complete &= analysis.RawCoverageComplete && analysis.TopologyComplete;
                    var keys = new string[analysis.Nodes.Length];
                    for (int i = 0; i < analysis.Nodes.Length; i++)
                    {
                        if ((i & 63) == 0 && Check()) break;
                        var node = analysis.Nodes[i]; var raw = node.Raw;
                        if (!Matches(raw.Name, includes) || MatchesAny(raw.Name, excludes) || !Matches(raw.Category, categories)) continue;
                        var key = Identity(analysis.Thread.ThreadId, raw);
                        if (!groups.TryGetValue(key, out var group))
                        {
                            if (groups.Count >= 512) { result.complete = false; issues.Add("AGGREGATE_GROUP_LIMIT"); continue; }
                            group = new Accumulator { Value = new RecordedBottomUpGroup { groupId = "aggregate:" + key,
                                threadId = analysis.Thread.ThreadId.ToString(CultureInfo.InvariantCulture), threadName = analysis.Thread.ThreadName,
                                threadGroupName = analysis.Thread.ThreadGroup, markerId = raw.MarkerId, name = raw.Name, categoryId = raw.CategoryId,
                                category = raw.Category, rawCoverageComplete = true } };
                            groups.Add(key, group);
                        }
                        keys[i] = key;
                        result.physicalSamples++; group.Value.physicalCallCount++; group.Frames.Add(frame.Index);
                        if (group.Samples.Count < 8) group.Samples.Add(RecordedProfilerFrameAnalysis.SampleId(analysis.Thread, raw.Index));
                        else group.Value.sampleIdsTruncated = true;
                        group.Value.rawCoverageComplete &= analysis.RawCoverageComplete && analysis.TopologyComplete;
                        if (!analysis.Thread.GcMarkerAvailable) { group.gcKnown = false; group.Issues.Add("GC_MARKER_NOT_RECORDED"); }
                        if (node.SelfTimeMs.HasValue) group.Value.knownSelfTimeMs += node.SelfTimeMs.Value;
                        else { group.selfKnown = false; group.Issues.Add("SELF_TIME_INCOMPLETE"); }
                        ulong end;
                        try { end = checked(raw.StartNs + raw.DurationNs); }
                        catch (OverflowException) { group.Value.rawCoverageComplete = false; group.Issues.Add("TIME_INTERVAL_OVERFLOW"); result.complete = false; continue; }
                        if (!group.Intervals.TryGetValue(frame.Index, out var intervals)) { intervals = new(); group.Intervals.Add(frame.Index, intervals); }
                        intervals.Add((raw.StartNs, end));
                    }
                    if (result.timedOut) break;
                    // Each recorded allocation event is attributed once to each distinct logical group in its physical ancestry.
                    // Recursive occurrences of the same marker do not multiply that event's bytes.
                    for (int i = 0; i < analysis.Nodes.Length; i++)
                    {
                        if ((i & 63) == 0 && Check()) break;
                        var allocation = analysis.Nodes[i].Raw;
                        if (!allocation.IsGcAllocation) continue;
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        int? cursor = i; int depth = 0;
                        while (cursor.HasValue)
                        {
                            if ((depth++ & 31) == 0 && Check()) break;
                            var key = keys[cursor.Value];
                            if (key != null && seen.Add(key))
                            {
                                var group = groups[key]; group.Value.gcAllocationEvents++;
                                if (!allocation.GcBytes.HasValue || allocation.GcBytes < 0)
                                { group.gcKnown = false; group.Value.unknownGcAllocationEvents++; group.Issues.Add("GC_METADATA_UNAVAILABLE"); }
                                else if (!group.Value.gcSumOverflow)
                                {
                                    try { group.Value.knownGcAllocationBytes = checked(group.Value.knownGcAllocationBytes + allocation.GcBytes.Value); }
                                    catch (OverflowException) { group.gcKnown = false; group.Value.gcSumOverflow = true; group.Issues.Add("GC_SUM_OVERFLOW"); }
                                }
                            }
                            cursor = analysis.Nodes[cursor.Value].ParentIndex;
                        }
                        if (result.timedOut) break;
                    }
                    if (result.timedOut) break;
                }
                if (result.timedOut) break;
            }
            foreach (var group in groups.Values)
            {
                cancelled?.Invoke();
                foreach (var frame in group.Intervals.Values)
                {
                    var ordered = frame.OrderBy(item => item.start).ThenBy(item => item.end).ToArray();
                    if (ordered.Length == 0) continue;
                    ulong start = ordered[0].start, end = ordered[0].end; double nanoseconds = 0;
                    for (int i = 1; i < ordered.Length; i++)
                    {
                        if ((i & 63) == 0 && Check()) break;
                        if (ordered[i].start <= end) end = Math.Max(end, ordered[i].end);
                        else { nanoseconds += end - start; start = ordered[i].start; end = ordered[i].end; }
                    }
                    group.Value.inclusiveTimeMs += (nanoseconds + end - start) / 1000000d;
                }
                group.Value.framesObserved = group.Frames.Count;
                group.Value.rawCoverageComplete &= result.complete;
                group.Value.selfTimeComplete = group.selfKnown && group.Value.rawCoverageComplete;
                group.Value.selfTimeMs = group.Value.selfTimeComplete ? group.Value.knownSelfTimeMs : (double?)null;
                group.Value.gcAllocationBytes = group.gcKnown && group.Value.rawCoverageComplete ? group.Value.knownGcAllocationBytes : (long?)null;
                if (!group.Value.rawCoverageComplete) group.Issues.Add("AGGREGATE_COVERAGE_INCOMPLETE");
                group.Value.sampleIds = group.Samples.ToArray(); group.Value.issues = group.Issues.OrderBy(item => item, StringComparer.Ordinal).ToArray();
            }
            result.observedGroups = groups.Count; result.groupsTruncated = groups.Count > maxGroups;
            if (!result.complete)
                foreach (var group in groups.Values)
                {
                    group.Value.rawCoverageComplete = group.Value.selfTimeComplete = false;
                    group.Value.selfTimeMs = null; group.Value.gcAllocationBytes = null;
                    group.Value.issues = group.Value.issues.Concat(new[] { "AGGREGATE_COVERAGE_INCOMPLETE" }).Distinct(StringComparer.Ordinal).ToArray();
                }
            double Score(RecordedBottomUpGroup group) => sort == RecordedProfilerSort.TotalTime ? group.inclusiveTimeMs :
                sort == RecordedProfilerSort.GcBytes ? group.gcAllocationBytes ?? -1 : group.selfTimeMs ?? group.knownSelfTimeMs;
            result.groups = groups.Values.Select(item => item.Value).OrderByDescending(Score).ThenBy(item => item.groupId, StringComparer.Ordinal).Take(maxGroups).ToArray();
            result.issues = issues.OrderBy(item => item, StringComparer.Ordinal).ToArray(); return result;
        }
        static string Identity(ulong threadId, RecordedProfilerSample raw)
        {
            using var hash = SHA256.Create();
            var identity = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes((raw.Name ?? "") + "\0" + (raw.Category ?? "")))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            return threadId.ToString(CultureInfo.InvariantCulture) + ":" + raw.CategoryId.ToString(CultureInfo.InvariantCulture) + ":" + raw.MarkerId.ToString(CultureInfo.InvariantCulture) + ":" + identity;
        }
        static bool Matches(string value, string[] filters) => filters == null || filters.Length == 0 || MatchesAny(value, filters);
        static bool MatchesAny(string value, string[] filters) => filters != null && filters.Any(filter => !string.IsNullOrWhiteSpace(filter) && (value ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
