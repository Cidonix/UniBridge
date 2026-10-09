using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Cidonix.UniBridge.Toolkit;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class RuntimeProfiler
    {
        static async Task<object> RecordedFrames(RuntimeProfilerParams parameters)
        {
            if (parameters.RequirePlayMode == true && !Application.isPlaying)
                return Response.Error("PLAY_MODE_REQUIRED", new { hint = "Recorded frames can normally be read in Edit Mode; omit RequirePlayMode or pass false." });
            int frames = Mathf.Clamp(parameters.FrameCount ?? 1, 1, 60);
            int maxThreads = Mathf.Clamp(parameters.MaxThreads ?? 8, 1, 64);
            int maxRaw = Mathf.Clamp(parameters.MaxRawSamples ?? 20000, 1, 100000);
            int maxTop = Mathf.Clamp(parameters.MaxHierarchySamples ?? DefaultMaxHierarchySamples, 1, MaxHierarchySamples);
            int maxDepth = Mathf.Clamp(parameters.MaxHierarchyDepth ?? DefaultMaxHierarchyDepth, 1, MaxHierarchyDepth);
            int timeoutMs = Mathf.Clamp(parameters.TimeoutMs ?? DefaultTimeoutMs, 1000, 300000);
            var startedUtc = DateTime.UtcNow;
            // Yield before acquiring views; every view is synchronously closed by Read.
            await ToolExecutionScheduler.YieldIfNotCancelledAsync();
            var elapsed = Stopwatch.StartNew();
            var read = RecordedProfilerFrameReader.Read(new UnityRecordedProfilerSource(), new RecordedProfilerReadOptions
            {
                FrameIndex = parameters.FrameIndex, FrameCount = frames,
                ThreadIds = parameters.ThreadIds, ThreadNameFilters = parameters.ThreadNameFilters,
                MaxThreads = maxThreads, MaxRawSamples = maxRaw, IncludeMetadata = parameters.IncludeMetadata ?? true
            }, () => elapsed.ElapsedMilliseconds >= timeoutMs, ToolExecutionScheduler.ThrowIfCancellationRequested);
            ToolExecutionScheduler.ThrowIfCancellationRequested();
            var frameData = read.Frames.Select(frame => BuildRecordedFrameDto(frame, parameters, maxTop, maxDepth, true)).ToArray();
            var payload = new
            {
                schema = "unibridge.runtimeProfiler.recordedFrames.v1",
                dataSource = "ProfilerDriver.RawFrameDataView",
                outcome = read.ErrorCode != null ? "unavailable" : read.Complete ? "completed" : "partial",
                complete = read.Complete, startedUtc = startedUtc.ToString("o"), capturedUtc = DateTime.UtcNow.ToString("o"),
                elapsedMs = elapsed.Elapsed.TotalMilliseconds,
                available = new { firstFrameIndex = read.FirstAvailableFrame, lastFrameIndex = read.LastAvailableFrame },
                request = new { parameters.FrameIndex, frameCount = frames, parameters.ThreadIds, parameters.ThreadNameFilters,
                    maxThreads, maxRawSamples = maxRaw, maxTopSamples = maxTop, maxHierarchyDepth = maxDepth,
                    includeMetadata = parameters.IncludeMetadata ?? true, sortBy = parameters.RecordedSortBy.ToString() },
                selectedFrameIndices = read.SelectedFrameIndices, readFrameIndices = read.Frames.Select(frame => frame.Index).ToArray(),
                copiedSamples = read.CopiedSamples, timedOut = read.TimedOut, issues = read.Issues.ToArray(),
                errorCode = read.ErrorCode,
                selfTimeMethod = "recorded duration minus clipped union of direct-child intervals; unknown for incomplete topology",
                gcScope = "Recorded GC.Alloc events in the selected threads; unknown metadata and unrecorded markers are not zero. Thread work is not frame wall time.",
                frames = frameData
            };
            string savedPath = null;
            if ((parameters.SaveToFile ?? true) && read.Frames.SelectMany(frame => frame.Threads).Any())
                savedPath = SavePayload(payload, string.IsNullOrWhiteSpace(parameters.Name) ? "recorded-profiler-frames" : parameters.Name);
            var responseData = new
            {
                payload.schema, payload.dataSource, payload.outcome, payload.complete, payload.available, payload.request,
                payload.selectedFrameIndices, payload.readFrameIndices, payload.copiedSamples, payload.timedOut,
                payload.elapsedMs, payload.issues, payload.errorCode, payload.selfTimeMethod, payload.gcScope, savedPath,
                hint = read.ErrorCode != null ? "Record CPU profiling data in Unity first, or select a frame/thread still available in the already loaded Profiler buffer. This action does not enable recording or load/clear captures." : null,
                frames = parameters.ReturnSamples == true ? frameData :
                    read.Frames.Select(frame => BuildRecordedFrameDto(frame, parameters, maxTop, maxDepth, false)).ToArray()
            };
            if (read.ErrorCode != null)
                return Response.Error(read.ErrorCode, responseData);
            if (!read.Complete)
                return Response.Error("RECORDED_PROFILER_DATA_PARTIAL", responseData);
            return Response.Success($"Read {read.Frames.Count} recorded profiler frame(s).", responseData);
        }

        static object BuildRecordedFrameDto(RecordedProfilerFrame frame, RuntimeProfilerParams parameters, int maxTop, int maxDepth, bool full)
        {
            long knownGc = 0;
            bool gcComplete = frame.Threads.Count > 0 && frame.Threads.All(thread => thread.GcBytes.HasValue);
            foreach (var thread in frame.Threads)
            {
                try { knownGc = checked(knownGc + thread.KnownGcBytes); }
                catch (OverflowException) { gcComplete = false; break; }
            }
            return new
            {
                frameIndex = frame.Index,
                frameDurationMs = frame.Threads.Count > 0 ? frame.Threads[0].Thread.FrameDurationNs / 1000000.0 : (double?)null,
                availableThreadCount = frame.AvailableThreadCount, examinedThreadViews = frame.ExaminedThreadViews,
                selectedThreadCount = frame.Threads.Count,
                knownSelectedThreadGcAllocationBytes = knownGc,
                selectedThreadGcAllocationBytes = gcComplete ? knownGc : (long?)null,
                gcScope = "selected threads, one count per recorded allocation event",
                issues = frame.Issues.ToArray(),
                threads = frame.Threads.Select(analysis => BuildRecordedThreadDto(analysis, parameters, maxTop, maxDepth, full)).ToArray()
            };
        }

        static object BuildRecordedThreadDto(RecordedProfilerAnalysis analysis, RuntimeProfilerParams parameters, int maxTop, int maxDepth, bool full)
        {
            var selected = RecordedProfilerFrameAnalysis.Select(analysis, parameters.MarkerFilters, parameters.ExcludeMarkerFilters,
                parameters.ProfilerCategories, Math.Max(0, parameters.MinHierarchySampleMs ?? 0), maxTop,
                (RecordedProfilerSort)parameters.RecordedSortBy);
            var thread = analysis.Thread;
            return new
            {
                threadIndex = thread.ThreadIndex, threadId = thread.ThreadId.ToString(CultureInfo.InvariantCulture),
                threadName = thread.ThreadName, threadGroupName = thread.ThreadGroup,
                declaredRawSampleCount = thread.DeclaredSampleCount, copiedRawSampleCount = analysis.Nodes.Length,
                rawCoverageComplete = analysis.RawCoverageComplete, topologyComplete = analysis.TopologyComplete,
                gcMarkerAvailable = thread.GcMarkerAvailable, knownGcAllocationBytes = analysis.KnownGcBytes,
                gcAllocationBytes = analysis.GcBytes, gcAllocationEvents = analysis.GcEvents,
                unknownGcAllocationEvents = analysis.UnknownGcEvents, gcSumOverflow = analysis.GcSumOverflow,
                issues = analysis.Issues,
                topSamples = selected.Select(node => RecordedProfilerFrameAnalysis.SampleDto(analysis, node)).ToArray(),
                hierarchy = BuildRecordedHierarchy(analysis, selected, maxDepth),
                samples = full ? analysis.Nodes.Select(node => RecordedProfilerFrameAnalysis.SampleDto(analysis, node)).ToArray() : null
            };
        }

        sealed class RecordedHierarchyNode
        {
            public object sample;
            public bool selected;
            public bool depthTruncated;
            public readonly List<RecordedHierarchyNode> children = new();
        }

        static object BuildRecordedHierarchy(RecordedProfilerAnalysis analysis, RecordedProfilerNode[] selected, int maxDepth)
        {
            var selectedIndices = new HashSet<int>(selected.Select(node => node.Raw.Index));
            var included = new HashSet<int>();
            foreach (var node in selected)
            {
                var cursor = node;
                while (cursor != null)
                {
                    ToolExecutionScheduler.ThrowIfCancellationRequested();
                    if (!included.Add(cursor.Raw.Index)) break;
                    cursor = cursor.ParentIndex.HasValue ? analysis.Nodes[cursor.ParentIndex.Value] : null;
                }
            }
            var roots = new List<RecordedHierarchyNode>();
            var byIndex = new Dictionary<int, RecordedHierarchyNode>();
            foreach (var index in included.OrderBy(index => index))
            {
                var node = analysis.Nodes[index];
                if (node.Depth >= maxDepth) continue;
                var dto = new RecordedHierarchyNode
                {
                    sample = RecordedProfilerFrameAnalysis.SampleDto(analysis, node), selected = selectedIndices.Contains(index),
                    depthTruncated = node.Depth == maxDepth - 1 && node.Children.Any(included.Contains)
                };
                byIndex[index] = dto;
                if (node.ParentIndex.HasValue && byIndex.TryGetValue(node.ParentIndex.Value, out var parent)) parent.children.Add(dto);
                else roots.Add(dto);
            }
            return roots.ToArray();
        }
    }
}
