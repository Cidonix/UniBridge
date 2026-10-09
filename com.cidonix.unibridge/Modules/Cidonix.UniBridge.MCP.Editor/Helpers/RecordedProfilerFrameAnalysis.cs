using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Copied value data only. Native frame views never enter this analyzer.
    internal sealed class RecordedProfilerSample
    {
        public int Index;
        public int MarkerId;
        public string Name;
        public string Category;
        public ushort CategoryId;
        public string Flags;
        public ulong StartNs;
        public ulong DurationNs;
        public int Children;
        public int Descendants;
        public bool IsGcAllocation;
        public long? GcBytes;
        public string GcMetadataStatus;
    }

    internal sealed class RecordedProfilerThread
    {
        public int FrameIndex;
        public int ThreadIndex;
        public ulong ThreadId;
        public string ThreadName;
        public string ThreadGroup;
        public ulong FrameStartNs;
        public ulong FrameDurationNs;
        public int DeclaredSampleCount;
        public bool GcMarkerAvailable;
        public RecordedProfilerSample[] Samples = Array.Empty<RecordedProfilerSample>();
        public string ReadIssue;
    }

    internal sealed class RecordedProfilerNode
    {
        public RecordedProfilerSample Raw;
        public int? ParentIndex;
        public int Depth;
        public long SubtreeEnd;
        public bool TopologyComplete = true;
        public double? SelfTimeMs;
        public readonly List<int> Children = new();
        public readonly HashSet<string> Issues = new(StringComparer.Ordinal);
        public double TotalTimeMs => Raw.DurationNs / 1000000.0;
    }

    internal sealed class RecordedProfilerAnalysis
    {
        public RecordedProfilerThread Thread;
        public RecordedProfilerNode[] Nodes;
        public bool RawCoverageComplete;
        public bool TopologyComplete;
        public long KnownGcBytes;
        public long? GcBytes;
        public int GcEvents;
        public int UnknownGcEvents;
        public bool GcSumOverflow;
        public string[] Issues;
    }

    internal enum RecordedProfilerSort
    {
        SelfTime,
        TotalTime,
        GcBytes
    }

    internal static class RecordedProfilerFrameAnalysis
    {
        public static RecordedProfilerAnalysis Analyze(RecordedProfilerThread thread, Action check = null)
        {
            if (thread == null) throw new ArgumentNullException(nameof(thread));
            var samples = thread.Samples ?? Array.Empty<RecordedProfilerSample>();
            var nodes = new RecordedProfilerNode[samples.Length];
            var parents = new Stack<int>();
            var issues = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < samples.Length; i++)
            {
                if ((i & 63) == 0) check?.Invoke();
                while (parents.Count > 0 && nodes[parents.Peek()].SubtreeEnd < i)
                    parents.Pop();
                var raw = samples[i];
                var node = new RecordedProfilerNode
                {
                    Raw = raw,
                    ParentIndex = parents.Count > 0 ? parents.Peek() : (int?)null,
                    SubtreeEnd = (long)i + raw.Descendants
                };
                nodes[i] = node;
                if (node.ParentIndex.HasValue)
                {
                    var parent = nodes[node.ParentIndex.Value];
                    parent.Children.Add(i);
                    node.Depth = parent.Depth + 1;
                    if (node.SubtreeEnd > parent.SubtreeEnd)
                    {
                        node.TopologyComplete = parent.TopologyComplete = false;
                        node.Issues.Add("SUBTREE_EXCEEDS_PARENT");
                        parent.Issues.Add("SUBTREE_EXCEEDS_PARENT");
                    }
                }
                if (raw.Index != i || raw.Children < 0 || raw.Descendants < raw.Children ||
                    node.SubtreeEnd >= thread.DeclaredSampleCount)
                {
                    node.TopologyComplete = false;
                    node.Issues.Add("INVALID_RECORDED_TOPOLOGY");
                }
                if (raw.Descendants > 0)
                    parents.Push(i);
            }

            // Reverse order propagates unknown topology through physical ancestors.
            for (var i = nodes.Length - 1; i >= 0; i--)
            {
                if ((i & 63) == 0) check?.Invoke();
                var node = nodes[i];
                if (node.SubtreeEnd >= nodes.Length)
                {
                    node.TopologyComplete = false;
                    node.Issues.Add("INCOMPLETE_SUBTREE");
                }
                if (node.Raw.Children != node.Children.Count)
                {
                    node.TopologyComplete = false;
                    node.Issues.Add("DIRECT_CHILD_COUNT_MISMATCH");
                }
                foreach (var child in node.Children)
                    if (!nodes[child].TopologyComplete)
                    {
                        node.TopologyComplete = false;
                        node.Issues.Add("INCOMPLETE_CHILD_TOPOLOGY");
                    }
                node.SelfTimeMs = ComputeSelfTime(node, nodes);
                foreach (var issue in node.Issues) issues.Add(issue);
            }

            long knownGcBytes = 0;
            int gcEvents = 0, unknownGcEvents = 0;
            bool overflow = false;
            foreach (var raw in samples.Where(item => item.IsGcAllocation))
            {
                gcEvents++;
                if (!raw.GcBytes.HasValue || raw.GcBytes.Value < 0)
                {
                    unknownGcEvents++;
                    issues.Add("GC_METADATA_UNAVAILABLE");
                    continue;
                }
                if (overflow) continue;
                try { knownGcBytes = checked(knownGcBytes + raw.GcBytes.Value); }
                catch (OverflowException) { overflow = true; issues.Add("GC_SUM_OVERFLOW"); }
            }
            bool rawComplete = samples.Length == thread.DeclaredSampleCount && thread.ReadIssue == null;
            if (!rawComplete) issues.Add("RAW_COVERAGE_INCOMPLETE");
            if (!thread.GcMarkerAvailable) issues.Add("GC_MARKER_NOT_RECORDED");
            if (thread.ReadIssue != null) issues.Add(thread.ReadIssue);
            return new RecordedProfilerAnalysis
            {
                Thread = thread, Nodes = nodes, RawCoverageComplete = rawComplete,
                TopologyComplete = rawComplete && nodes.All(node => node.TopologyComplete),
                KnownGcBytes = knownGcBytes,
                GcBytes = rawComplete && thread.GcMarkerAvailable && unknownGcEvents == 0 && !overflow ? knownGcBytes : (long?)null,
                GcEvents = gcEvents, UnknownGcEvents = unknownGcEvents, GcSumOverflow = overflow,
                Issues = issues.OrderBy(item => item, StringComparer.Ordinal).ToArray()
            };
        }

        static double? ComputeSelfTime(RecordedProfilerNode node, RecordedProfilerNode[] nodes)
        {
            if (!node.TopologyComplete) return null;
            var parent = node.Raw;
            if (parent.DurationNs > ulong.MaxValue - parent.StartNs)
            {
                node.Issues.Add("TIMESTAMP_OVERFLOW");
                return null;
            }
            ulong parentEnd = parent.StartNs + parent.DurationNs;
            var intervals = new List<(ulong start, ulong end)>();
            foreach (var index in node.Children)
            {
                var child = nodes[index].Raw;
                if (child.DurationNs > ulong.MaxValue - child.StartNs)
                {
                    node.Issues.Add("CHILD_TIMESTAMP_OVERFLOW");
                    return null;
                }
                ulong end = child.StartNs + child.DurationNs;
                if (child.StartNs < parent.StartNs || end > parentEnd)
                    node.Issues.Add("CHILD_INTERVAL_CLIPPED");
                ulong start = Math.Max(parent.StartNs, child.StartNs);
                end = Math.Min(parentEnd, end);
                if (end > start) intervals.Add((start, end));
            }
            ulong covered = 0, previousStart = 0, previousEnd = 0;
            bool present = false;
            foreach (var interval in intervals.OrderBy(item => item.start).ThenBy(item => item.end))
            {
                if (!present) { previousStart = interval.start; previousEnd = interval.end; present = true; }
                else if (interval.start <= previousEnd)
                {
                    if (interval.start < previousEnd) node.Issues.Add("DIRECT_CHILD_INTERVALS_OVERLAP");
                    previousEnd = Math.Max(previousEnd, interval.end);
                }
                else
                {
                    covered += previousEnd - previousStart;
                    previousStart = interval.start; previousEnd = interval.end;
                }
            }
            if (present) covered += previousEnd - previousStart;
            return (parent.DurationNs - covered) / 1000000.0;
        }

        public static RecordedProfilerNode[] Select(RecordedProfilerAnalysis analysis,
            string[] include, string[] exclude, string[] categories, double minTotalMs,
            int maxSamples, RecordedProfilerSort sort)
        {
            bool Match(string value, string[] filters) => filters == null || filters.Length == 0 ||
                filters.Any(filter => !string.IsNullOrWhiteSpace(filter) && value?.IndexOf(filter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            double Score(RecordedProfilerNode node) => sort == RecordedProfilerSort.TotalTime ? node.TotalTimeMs :
                sort == RecordedProfilerSort.GcBytes ? node.Raw.GcBytes ?? -1 : node.SelfTimeMs ?? -1;
            return analysis.Nodes.Where(node => node.TotalTimeMs >= minTotalMs && Match(node.Raw.Name, include) &&
                (exclude == null || exclude.Length == 0 || !Match(node.Raw.Name, exclude)) && Match(node.Raw.Category, categories))
                .OrderByDescending(Score).ThenBy(node => node.Raw.Index).Take(Math.Max(0, maxSamples)).ToArray();
        }

        public static string SampleId(RecordedProfilerThread thread, int sampleIndex)
        {
            return thread.FrameIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                thread.ThreadId.ToString(CultureInfo.InvariantCulture) + ":" + sampleIndex.ToString(CultureInfo.InvariantCulture);
        }

        public static object SampleDto(RecordedProfilerAnalysis analysis, RecordedProfilerNode node)
        {
            return new
            {
                sampleId = SampleId(analysis.Thread, node.Raw.Index), sampleIndex = node.Raw.Index,
                parentSampleId = node.ParentIndex.HasValue ? SampleId(analysis.Thread, node.ParentIndex.Value) : null,
                parentSampleIndex = node.ParentIndex, depth = node.Depth, name = node.Raw.Name,
                markerId = node.Raw.MarkerId, category = node.Raw.Category, categoryId = node.Raw.CategoryId,
                flags = node.Raw.Flags, startTimeNs = node.Raw.StartNs.ToString(CultureInfo.InvariantCulture),
                durationNs = node.Raw.DurationNs.ToString(CultureInfo.InvariantCulture), totalTimeMs = node.TotalTimeMs,
                selfTimeMs = node.SelfTimeMs, selfTimeComplete = node.SelfTimeMs.HasValue,
                directChildren = node.Children.ToArray(), isGcAllocation = node.Raw.IsGcAllocation,
                gcAllocationBytes = node.Raw.GcBytes, gcMetadataStatus = node.Raw.GcMetadataStatus,
                topologyComplete = node.TopologyComplete,
                issues = node.Issues.OrderBy(item => item, StringComparer.Ordinal).ToArray()
            };
        }
    }
}
