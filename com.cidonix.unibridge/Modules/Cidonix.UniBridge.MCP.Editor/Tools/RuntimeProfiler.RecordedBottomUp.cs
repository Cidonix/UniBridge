using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class RuntimeProfiler
    {
        static async Task<object> RecordedBottomUp(RuntimeProfilerParams parameters)
        {
            if (parameters.RequirePlayMode == true && !Application.isPlaying)
                return Response.Error("PLAY_MODE_REQUIRED", new { hint = "Recorded bottom-up can read historical frames in Edit Mode; omit RequirePlayMode or pass false." });
            int frames = Mathf.Clamp(parameters.FrameCount ?? 1, 1, 60), maxGroups = Mathf.Clamp(parameters.MaxBottomUpGroups ?? 40, 1, 300);
            int timeoutMs = Mathf.Clamp(parameters.TimeoutMs ?? DefaultTimeoutMs, 1000, 300000);
            await ToolExecutionScheduler.YieldIfNotCancelledAsync(); var elapsed = Stopwatch.StartNew();
            var read = RecordedProfilerFrameReader.Read(new UnityRecordedProfilerSource(), new RecordedProfilerReadOptions {
                FrameIndex = parameters.FrameIndex, FrameCount = frames, ThreadIds = parameters.ThreadIds, ThreadNameFilters = parameters.ThreadNameFilters,
                MaxThreads = Mathf.Clamp(parameters.MaxThreads ?? 8, 1, 64), MaxRawSamples = Mathf.Clamp(parameters.MaxRawSamples ?? 20000, 1, 100000),
                IncludeMetadata = parameters.IncludeMetadata ?? true }, () => elapsed.ElapsedMilliseconds >= timeoutMs, ToolExecutionScheduler.ThrowIfCancellationRequested);
            var aggregation = RecordedProfilerBottomUp.Analyze(read, maxGroups, parameters.MarkerFilters, parameters.ExcludeMarkerFilters,
                parameters.ProfilerCategories, (RecordedProfilerSort)parameters.RecordedSortBy, () => elapsed.ElapsedMilliseconds >= timeoutMs, ToolExecutionScheduler.ThrowIfCancellationRequested);
            ToolExecutionScheduler.ThrowIfCancellationRequested();
            var payload = new {
                schema = "unibridge.runtimeProfiler.recordedBottomUp.v1", dataSource = "copied physical RawFrameDataView samples",
                outcome = read.ErrorCode != null ? "unavailable" : aggregation.complete ? "completed" : "partial", complete = aggregation.complete,
                errorCode = read.ErrorCode, timedOut = read.TimedOut || aggregation.timedOut, elapsedMs = elapsed.Elapsed.TotalMilliseconds,
                available = new { firstFrameIndex = read.FirstAvailableFrame, lastFrameIndex = read.LastAvailableFrame },
                request = new { parameters.FrameIndex, frameCount = frames, parameters.ThreadIds, parameters.ThreadNameFilters, maxGroups,
                    sortBy = parameters.RecordedSortBy.ToString(), parameters.MarkerFilters, parameters.ExcludeMarkerFilters, parameters.ProfilerCategories },
                selectedFrameIndices = read.SelectedFrameIndices, readFrameIndices = read.Frames.Select(frame => frame.Index).ToArray(), copiedSamples = read.CopiedSamples,
                aggregation.observedGroups, aggregation.physicalSamples, aggregation.groupsTruncated,
                identity = "aggregate group ID is distinct from physical sampleId; grouping preserves physical thread, marker ID, category ID and recorded names",
                inclusiveTimeMethod = "union of same-group intervals separately per physical frame/thread; recursive occurrences are not double-counted",
                gcScope = "each physical GC.Alloc event counted once per distinct ancestor logical group; different caller groups may share allocations and must not be summed",
                issues = read.Issues.Concat(aggregation.issues).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray(), groups = aggregation.groups
            };
            string savedPath = null;
            if ((parameters.SaveToFile ?? true) && aggregation.groups.Length > 0) savedPath = SavePayload(payload, parameters.Name ?? "recorded-bottom-up");
            var response = new { payload.schema, payload.dataSource, payload.outcome, payload.complete, payload.errorCode, payload.timedOut, payload.elapsedMs,
                payload.available, payload.request, payload.selectedFrameIndices, payload.readFrameIndices, payload.copiedSamples,
                payload.observedGroups, payload.physicalSamples, payload.groupsTruncated, payload.identity, payload.inclusiveTimeMethod, payload.gcScope, payload.issues, payload.groups, savedPath };
            if (read.ErrorCode != null) return Response.Error(read.ErrorCode, response);
            if (!aggregation.complete) return Response.Error("RECORDED_BOTTOM_UP_PARTIAL", response);
            return Response.Success("Aggregated recorded physical Profiler samples.", response);
        }
    }
}
