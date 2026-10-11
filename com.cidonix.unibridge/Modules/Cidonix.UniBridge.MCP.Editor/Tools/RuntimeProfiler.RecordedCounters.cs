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
        static async Task<object> RecordedCounters(RuntimeProfilerParams parameters)
        {
            if (parameters.RequirePlayMode == true && !Application.isPlaying)
                return Response.Error("PLAY_MODE_REQUIRED", new { hint = "Recorded counters can be read in Edit Mode; omit RequirePlayMode or pass false." });
            RecordedProfilerCounterDefinition[] definitions;
            try { definitions = RecordedProfilerCounterReader.Resolve(parameters.Metrics); }
            catch (ArgumentException ex) { return Response.Error("INVALID_RECORDED_COUNTER_REQUEST", new { hint = ex.Message }); }
            int frameCount = Mathf.Clamp(parameters.FrameCount ?? 1, 1, 60);
            int timeoutMs = Mathf.Clamp(parameters.TimeoutMs ?? DefaultTimeoutMs, 1000, 300000);
            await ToolExecutionScheduler.YieldIfNotCancelledAsync();
            var elapsed = Stopwatch.StartNew();
            var result = RecordedProfilerCounterReader.Read(new UnityRecordedProfilerCounterSource(), parameters.FrameIndex,
                frameCount, definitions, () => elapsed.ElapsedMilliseconds >= timeoutMs, ToolExecutionScheduler.ThrowIfCancellationRequested);
            ToolExecutionScheduler.ThrowIfCancellationRequested();
            var payload = new
            {
                schema = "unibridge.runtimeProfiler.recordedCounters.v1", dataSource = "ProfilerDriver.RawFrameDataView",
                outcome = result.ErrorCode != null ? "unavailable" : result.Complete ? "completed" : "partial", complete = result.Complete,
                available = new { firstFrameIndex = result.FirstFrame, lastFrameIndex = result.LastFrame },
                request = new { parameters.FrameIndex, frameCount, metrics = definitions.Select(item => item.Key).ToArray(), timeoutMs },
                selectedFrameIndices = result.SelectedFrames, readFrameIndices = result.Frames.Select(frame => frame.frameIndex).ToArray(),
                timedOut = result.TimedOut, elapsedMs = elapsed.Elapsed.TotalMilliseconds, issues = result.Issues.ToArray(), errorCode = result.ErrorCode,
                summaries = RecordedProfilerCounterReader.Summaries(result, definitions), frames = result.Frames.ToArray()
            };
            string savedPath = null;
            if ((parameters.SaveToFile ?? true) && result.Frames.Count > 0)
                savedPath = SavePayload(payload, string.IsNullOrWhiteSpace(parameters.Name) ? "recorded-profiler-counters" : parameters.Name);
            var response = new { payload.schema, payload.dataSource, payload.outcome, payload.complete, payload.available, payload.request,
                payload.selectedFrameIndices, payload.readFrameIndices, payload.timedOut, payload.elapsedMs, payload.issues, payload.errorCode,
                payload.summaries, frames = parameters.ReturnSamples == true ? payload.frames : null, savedPath,
                hint = "Only existing recorded frame counters are read. Missing counter values are null with status; measured zero remains zero. Summaries use available values only." };
            if (result.ErrorCode != null) return Response.Error(result.ErrorCode, response);
            if (!result.Complete) return Response.Error("RECORDED_COUNTER_DATA_PARTIAL", response);
            return Response.Success($"Read counters from {result.Frames.Count} recorded profiler frame(s).", response);
        }
    }
}
