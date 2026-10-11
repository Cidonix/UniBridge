using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal interface IRecordedProfilerSource
    {
        int FirstFrame { get; }
        int LastFrame { get; }
        int NextFrame(int index);
        int PreviousFrame(int index);
        int ThreadCount(int frame);
        IRecordedProfilerView Open(int frame, int threadIndex);
    }

    internal interface IRecordedProfilerView : IDisposable
    {
        bool Valid { get; }
        RecordedProfilerThread CopyHeader();
        RecordedProfilerSample CopySample(int index, bool includeMetadata);
    }

    internal interface IRecordedProfilerCallstackView
    {
        RecordedProfilerCallstack CopyCallstack(int sampleIndex, int maxDepth, Action cancelled);
    }

    internal sealed class RecordedProfilerReadOptions
    {
        public int? FrameIndex;
        public int FrameCount = 1;
        public string[] ThreadIds;
        public string[] ThreadNameFilters;
        public int MaxThreads = 8;
        public int MaxThreadViews = 256;
        public int MaxRawSamples = 20000;
        public bool IncludeMetadata = true;
        public bool IncludeCallstacks;
        public string[] CallstackSampleIds;
        public int MaxCallstackSamples = 32;
        public int MaxCallstackDepth = 32;
    }

    internal sealed class RecordedProfilerFrame
    {
        public int Index;
        public int AvailableThreadCount;
        public int ExaminedThreadViews;
        public readonly List<RecordedProfilerAnalysis> Threads = new();
        public readonly List<string> Issues = new();
    }

    internal sealed class RecordedProfilerReadResult
    {
        public string ErrorCode;
        public int FirstAvailableFrame = -1;
        public int LastAvailableFrame = -1;
        public int RequestedFrameCount;
        public int[] SelectedFrameIndices = Array.Empty<int>();
        public int CopiedSamples;
        public int RequestedCallstacks;
        public string[] UnobservedCallstackSampleIds = Array.Empty<string>();
        public bool TimedOut;
        public bool Complete;
        public readonly List<RecordedProfilerFrame> Frames = new();
        public readonly List<string> Issues = new();
    }

    internal static class RecordedProfilerFrameReader
    {
        public static RecordedProfilerReadResult Read(IRecordedProfilerSource source,
            RecordedProfilerReadOptions options, Func<bool> budgetExpired = null, Action checkCancellation = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.FrameCount < 1 || options.FrameCount > 60 || options.MaxThreads < 1 || options.MaxThreads > 64 ||
                options.MaxThreadViews < 1 || options.MaxThreadViews > 512 || options.MaxRawSamples < 1 || options.MaxRawSamples > 100000 ||
                options.MaxCallstackSamples < 1 || options.MaxCallstackSamples > 64 || options.MaxCallstackDepth < 1 || options.MaxCallstackDepth > 64)
                throw new ArgumentOutOfRangeException(nameof(options), "Recorded extraction limits are outside the supported bounds.");
            var result = new RecordedProfilerReadResult { RequestedFrameCount = options.FrameCount };
            var requestedStacks = new HashSet<string>(StringComparer.Ordinal);
            var observedStacks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in options.CallstackSampleIds ?? Array.Empty<string>())
            {
                var pieces = id?.Trim().Split(':');
                if (options.CallstackSampleIds.Length > 64 || !options.IncludeCallstacks || pieces == null || pieces.Length != 3 ||
                    !int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var frame) || frame < 0 ||
                    !ulong.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var thread) ||
                    !int.TryParse(pieces[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sample) || sample < 0)
                { result.ErrorCode = "INVALID_RECORDED_CALLSTACK_SAMPLE_ID"; return result; }
                requestedStacks.Add(frame.ToString(CultureInfo.InvariantCulture) + ":" + thread.ToString(CultureInfo.InvariantCulture) + ":" + sample.ToString(CultureInfo.InvariantCulture));
            }
            var ids = new HashSet<ulong>();
            foreach (var id in options.ThreadIds ?? Array.Empty<string>())
            {
                if (!ulong.TryParse(id?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    result.ErrorCode = "INVALID_RECORDED_THREAD_ID";
                    return result;
                }
                ids.Add(parsed);
            }
            bool Expired()
            {
                checkCancellation?.Invoke();
                if (budgetExpired?.Invoke() != true) return false;
                result.TimedOut = true;
                if (!result.Issues.Contains("EXTRACTION_TIMEOUT")) result.Issues.Add("EXTRACTION_TIMEOUT");
                return true;
            }
            result.FirstAvailableFrame = source.FirstFrame;
            result.LastAvailableFrame = source.LastFrame;
            if (result.FirstAvailableFrame < 0 || result.LastAvailableFrame < result.FirstAvailableFrame)
            {
                result.ErrorCode = "NO_RECORDED_PROFILER_FRAMES";
                return result;
            }
            if (options.FrameIndex.HasValue && (options.FrameIndex < result.FirstAvailableFrame || options.FrameIndex > result.LastAvailableFrame))
            {
                result.ErrorCode = "RECORDED_FRAME_OUT_OF_RANGE";
                return result;
            }
            var indices = new List<int>();
            var visited = new HashSet<int>();
            int cursor = options.FrameIndex ?? result.LastAvailableFrame;
            while (indices.Count < options.FrameCount && cursor >= result.FirstAvailableFrame && cursor <= result.LastAvailableFrame)
            {
                if (Expired()) break;
                if (!visited.Add(cursor)) { result.Issues.Add("INVALID_FRAME_TRAVERSAL"); break; }
                indices.Add(cursor);
                if (indices.Count == options.FrameCount) break;
                int next = options.FrameIndex.HasValue ? source.NextFrame(cursor) : source.PreviousFrame(cursor);
                if (next < 0) break;
                if (options.FrameIndex.HasValue ? next <= cursor : next >= cursor)
                { result.Issues.Add("INVALID_FRAME_TRAVERSAL"); break; }
                cursor = next;
            }
            if (!options.FrameIndex.HasValue) indices.Reverse();
            result.SelectedFrameIndices = indices.ToArray();
            if (indices.Count < options.FrameCount) result.Issues.Add("FRAME_RANGE_INCOMPLETE");
            foreach (var frameIndex in indices)
            {
                if (Expired()) break;
                var frame = new RecordedProfilerFrame { Index = frameIndex };
                var observedRequestedIds = new HashSet<ulong>();
                void RecordRequestedCoverage()
                {
                    foreach (var id in ids.OrderBy(id => id))
                        if (!observedRequestedIds.Contains(id))
                            frame.Issues.Add("REQUESTED_THREAD_COVERAGE_INCOMPLETE:" + id.ToString(CultureInfo.InvariantCulture));
                }
                result.Frames.Add(frame);
                try { frame.AvailableThreadCount = source.ThreadCount(frameIndex); }
                catch (Exception ex) { frame.Issues.Add("FRAME_UNAVAILABLE: " + ex.Message); RecordRequestedCoverage(); continue; }
                if (frame.AvailableThreadCount <= 0) { frame.Issues.Add("FRAME_UNAVAILABLE"); RecordRequestedCoverage(); continue; }
                int examinedLimit = Math.Min(frame.AvailableThreadCount, options.MaxThreadViews);
                for (var threadIndex = 0; threadIndex < examinedLimit; threadIndex++)
                {
                    if (Expired()) break;
                    RecordedProfilerThread copied = null;
                    var samples = new List<RecordedProfilerSample>();
                    try
                    {
                        // Every opened view, including invalid/filtered/throwing views,
                        // is disposed before analysis or the next view can run.
                        using (var view = source.Open(frameIndex, threadIndex))
                        {
                            frame.ExaminedThreadViews++;
                            if (view == null || !view.Valid) { frame.Issues.Add("THREAD_VIEW_UNAVAILABLE:" + threadIndex); continue; }
                            copied = view.CopyHeader();
                            if (copied.FrameIndex != frameIndex || copied.ThreadIndex != threadIndex)
                            { frame.Issues.Add("THREAD_HEADER_IDENTITY_MISMATCH:" + threadIndex); copied = null; continue; }
                            bool idMatch = ids.Count == 0 || ids.Contains(copied.ThreadId);
                            // Physical observation is independent of the name-filter intersection.
                            // An explicitly observed but intentionally filtered ID is not missing.
                            if (idMatch && ids.Contains(copied.ThreadId)) observedRequestedIds.Add(copied.ThreadId);
                            var nameFilters = options.ThreadNameFilters?.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray();
                            bool nameMatch = nameFilters == null || nameFilters.Length == 0 || nameFilters.Any(filter =>
                                (copied.ThreadGroup + "/" + copied.ThreadName).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (!idMatch || !nameMatch) { copied = null; continue; }
                            // Caps apply only to another selected physical thread/sample.
                            // Header-only scans must still prove that trailing views do not match.
                            if (frame.Threads.Count >= options.MaxThreads)
                            { frame.Issues.Add("SELECTED_THREAD_LIMIT"); copied = null; break; }
                            if (copied.DeclaredSampleCount < 0) { copied.ReadIssue = "INVALID_SAMPLE_COUNT"; continue; }
                            if (result.CopiedSamples >= options.MaxRawSamples && copied.DeclaredSampleCount > 0)
                            { frame.Issues.Add("RAW_SAMPLE_LIMIT"); copied = null; break; }
                            int limit = Math.Min(copied.DeclaredSampleCount, options.MaxRawSamples - result.CopiedSamples);
                            for (var sampleIndex = 0; sampleIndex < limit; sampleIndex++)
                            {
                                if (Expired()) { copied.ReadIssue = "EXTRACTION_TIMEOUT"; break; }
                                if ((sampleIndex & 63) == 0 && !view.Valid) { copied.ReadIssue = "THREAD_VIEW_EVICTED"; break; }
                                var sample = view.CopySample(sampleIndex, options.IncludeMetadata);
                                samples.Add(sample);
                                result.CopiedSamples++;
                                string stackId = RecordedProfilerFrameAnalysis.SampleId(copied, sampleIndex);
                                bool stackSelected = requestedStacks.Count == 0 || requestedStacks.Contains(stackId);
                                if (requestedStacks.Contains(stackId))
                                {
                                    observedStacks.Add(stackId);
                                    if (!sample.IsGcAllocation) sample.CallstackStatus = "NOT_ALLOCATION_SAMPLE";
                                }
                                if (sample.IsGcAllocation)
                                {
                                    if (!options.IncludeCallstacks) sample.CallstackStatus = "DISABLED";
                                    else if (!stackSelected) sample.CallstackStatus = "NOT_REQUESTED";
                                    else if (result.RequestedCallstacks >= options.MaxCallstackSamples) sample.CallstackStatus = "SAMPLE_LIMIT";
                                    else if (!(view is IRecordedProfilerCallstackView stacks)) sample.CallstackStatus = "CALLSTACK_API_UNSUPPORTED";
                                    else
                                    {
                                        result.RequestedCallstacks++;
                                        sample.Callstack = stacks.CopyCallstack(sampleIndex, options.MaxCallstackDepth, checkCancellation);
                                        sample.CallstackStatus = sample.Callstack?.status ?? "CALLSTACK_READ_FAILED";
                                    }
                                }
                            }
                            if (samples.Count < copied.DeclaredSampleCount && copied.ReadIssue == null)
                                copied.ReadIssue = "RAW_SAMPLE_LIMIT";
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (copied != null) copied.ReadIssue = "THREAD_READ_FAILED: " + ex.Message;
                        else frame.Issues.Add("THREAD_READ_FAILED:" + threadIndex + ": " + ex.Message);
                    }
                    finally
                    {
                        if (copied != null)
                        {
                            copied.Samples = samples.ToArray();
                            frame.Threads.Add(RecordedProfilerFrameAnalysis.Analyze(copied, checkCancellation));
                        }
                    }
                }
                if (frame.AvailableThreadCount > options.MaxThreadViews && frame.ExaminedThreadViews == examinedLimit)
                    frame.Issues.Add("THREAD_ENUMERATION_LIMIT");
                RecordRequestedCoverage();
            }
            if (result.Frames.Count < indices.Count) result.Issues.Add("FRAME_EXTRACTION_INCOMPLETE");
            result.UnobservedCallstackSampleIds = requestedStacks.Where(id => !observedStacks.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (result.UnobservedCallstackSampleIds.Length > 0) result.Issues.Add("CALLSTACK_SAMPLE_COVERAGE_INCOMPLETE");
            Expired();
            if (!result.Frames.SelectMany(frame => frame.Threads).Any())
                result.ErrorCode = result.Issues.Count == 0 && result.Frames.All(frame => frame.Issues.Count == 0)
                    ? "NO_RECORDED_THREAD_MATCH" : "RECORDED_PROFILER_DATA_UNAVAILABLE";
            result.Complete = result.ErrorCode == null && result.Issues.Count == 0 && result.Frames.All(frame => frame.Issues.Count == 0 && frame.Threads.All(thread =>
                thread.RawCoverageComplete && thread.TopologyComplete && thread.Nodes.All(node => node.SelfTimeMs.HasValue) && thread.GcBytes.HasValue));
            return result;
        }
    }
}
