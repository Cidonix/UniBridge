using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal sealed class RecordedProfilerCounterDefinition
    {
        public string Key;
        public string MarkerName;
        public string Unit;
        public string RecordedUnit;
        public double Scale = 1;
    }

    internal sealed class RecordedProfilerCounterValue
    {
        public string key;
        public string markerName;
        public string category;
        public string unit;
        public string recordedUnit;
        public string dataType;
        public string rawValue;
        public double? value;
        public string status;
        public string issue;
    }

    internal interface IRecordedProfilerCounterSource
    {
        int FirstFrame { get; }
        int LastFrame { get; }
        int NextFrame(int index);
        int PreviousFrame(int index);
        IRecordedProfilerCounterView Open(int frameIndex);
    }

    internal interface IRecordedProfilerCounterView : IDisposable
    {
        bool Valid { get; }
        int FrameIndex { get; }
        RecordedProfilerCounterValue CopyCounter(RecordedProfilerCounterDefinition definition);
    }

    internal sealed class RecordedProfilerCounterFrame
    {
        public int frameIndex;
        public readonly List<RecordedProfilerCounterValue> counters = new();
        public readonly List<string> issues = new();
    }

    internal sealed class RecordedProfilerCounterResult
    {
        public int FirstFrame = -1;
        public int LastFrame = -1;
        public int[] SelectedFrames = Array.Empty<int>();
        public readonly List<RecordedProfilerCounterFrame> Frames = new();
        public readonly List<string> Issues = new();
        public bool Complete;
        public bool TimedOut;
        public string ErrorCode;
    }

    internal static class RecordedProfilerCounterReader
    {
        internal const int MaxMetrics = 32;
        internal static readonly RecordedProfilerCounterDefinition[] Catalog =
        {
            Define("cpu_main_active_ms", "CPU Main Thread Active Time", "ms", "TimeNanoseconds", 0.000001),
            Define("cpu_render_active_ms", "CPU Render Thread Active Time", "ms", "TimeNanoseconds", 0.000001),
            Define("gpu_frame_ms", "GPU Frame Time", "ms", "TimeNanoseconds", 0.000001),
            Define("gc_alloc_bytes", "GC Allocated In Frame", "bytes", "Bytes"),
            Define("draw_calls_count", "Draw Calls Count", "count", "Count"),
            Define("batches_count", "Batches Count", "count", "Count"),
            Define("setpass_calls", "SetPass Calls Count", "count", "Count"),
            Define("triangles_count", "Triangles Count", "count", "Count")
        };
        static RecordedProfilerCounterDefinition Define(string key, string markerName, string unit, string recordedUnit, double scale = 1) =>
            new RecordedProfilerCounterDefinition { Key = key, MarkerName = markerName, Unit = unit, RecordedUnit = recordedUnit, Scale = scale };

        public static RecordedProfilerCounterDefinition[] Resolve(string[] metrics)
        {
            if (metrics == null || metrics.Length == 0) return Catalog.ToArray();
            if (metrics.Length > MaxMetrics) throw new ArgumentException("At most 32 recorded counter names are supported.", nameof(metrics));
            var result = new List<RecordedProfilerCounterDefinition>();
            foreach (var metric in metrics)
            {
                var name = metric?.Trim();
                if (string.IsNullOrEmpty(name)) throw new ArgumentException("Recorded counter names cannot be empty.", nameof(metrics));
                var known = Catalog.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.MarkerName, name, StringComparison.OrdinalIgnoreCase));
                var definition = known ?? Define(name, name, null, null);
                if (!result.Any(item => string.Equals(item.MarkerName, definition.MarkerName, StringComparison.OrdinalIgnoreCase))) result.Add(definition);
            }
            return result.ToArray();
        }

        internal static RecordedProfilerCounterValue Unavailable(RecordedProfilerCounterDefinition definition, string status, string issue = null) =>
            new RecordedProfilerCounterValue { key = definition.Key, markerName = definition.MarkerName, unit = definition.Unit,
                recordedUnit = definition.RecordedUnit, status = status, issue = issue };

        public static RecordedProfilerCounterResult Read(IRecordedProfilerCounterSource source, int? firstFrame, int frameCount,
            RecordedProfilerCounterDefinition[] counters, Func<bool> expired = null, Action cancelled = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (frameCount < 1 || frameCount > 60 || counters == null || counters.Length < 1 || counters.Length > MaxMetrics)
                throw new ArgumentOutOfRangeException(nameof(frameCount), "Recorded counter request limits are outside supported bounds.");
            var result = new RecordedProfilerCounterResult();
            bool Stop()
            {
                cancelled?.Invoke();
                if (expired?.Invoke() != true) return false;
                result.TimedOut = true;
                if (!result.Issues.Contains("EXTRACTION_TIMEOUT")) result.Issues.Add("EXTRACTION_TIMEOUT");
                return true;
            }
            result.FirstFrame = source.FirstFrame;
            result.LastFrame = source.LastFrame;
            if (result.FirstFrame < 0 || result.LastFrame < result.FirstFrame)
            { result.ErrorCode = "NO_RECORDED_PROFILER_FRAMES"; return result; }
            if (firstFrame.HasValue && (firstFrame < result.FirstFrame || firstFrame > result.LastFrame))
            { result.ErrorCode = "RECORDED_FRAME_OUT_OF_RANGE"; return result; }
            var selected = new List<int>();
            var visited = new HashSet<int>();
            int cursor = firstFrame ?? result.LastFrame;
            while (selected.Count < frameCount && cursor >= result.FirstFrame && cursor <= result.LastFrame)
            {
                if (Stop()) break;
                if (!visited.Add(cursor)) { result.Issues.Add("INVALID_FRAME_TRAVERSAL"); break; }
                selected.Add(cursor);
                if (selected.Count == frameCount) break;
                int next = firstFrame.HasValue ? source.NextFrame(cursor) : source.PreviousFrame(cursor);
                if (next < 0) break;
                if (firstFrame.HasValue ? next <= cursor : next >= cursor)
                { result.Issues.Add("INVALID_FRAME_TRAVERSAL"); break; }
                cursor = next;
            }
            if (!firstFrame.HasValue) selected.Reverse();
            result.SelectedFrames = selected.ToArray();
            if (selected.Count < frameCount) result.Issues.Add("FRAME_RANGE_INCOMPLETE");
            var recordedUnits = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (int frameIndex in selected)
            {
                if (Stop()) break;
                var frame = new RecordedProfilerCounterFrame { frameIndex = frameIndex };
                result.Frames.Add(frame);
                try
                {
                    using (var view = source.Open(frameIndex))
                    {
                        if (view == null || !view.Valid || view.FrameIndex != frameIndex)
                        {
                            frame.issues.Add("FRAME_VIEW_UNAVAILABLE");
                            foreach (var definition in counters) frame.counters.Add(Unavailable(definition, "FRAME_VIEW_UNAVAILABLE"));
                            continue;
                        }
                        foreach (var definition in counters)
                        {
                            if (Stop()) break;
                            if (!view.Valid)
                            { frame.issues.Add("FRAME_VIEW_EVICTED"); break; }
                            try
                            {
                                var counter = view.CopyCounter(definition);
                                if (counter.value.HasValue)
                                {
                                    if (recordedUnits.TryGetValue(counter.key, out var previousUnit) && previousUnit != counter.recordedUnit)
                                    { counter.value = null; counter.status = "COUNTER_UNIT_CHANGED"; }
                                    else recordedUnits[counter.key] = counter.recordedUnit;
                                }
                                frame.counters.Add(counter);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) { frame.counters.Add(Unavailable(definition, "COUNTER_READ_FAILED", ex.Message)); }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { frame.issues.Add("FRAME_READ_FAILED:" + ex.Message); }
                if (frame.counters.Count < counters.Length)
                {
                    frame.issues.Add("COUNTER_EXTRACTION_INCOMPLETE");
                    for (int i = frame.counters.Count; i < counters.Length; i++) frame.counters.Add(Unavailable(counters[i], "NOT_READ"));
                }
            }
            if (result.Frames.Count < selected.Count) result.Issues.Add("FRAME_EXTRACTION_INCOMPLETE");
            if (result.Frames.Count == 0) result.ErrorCode = "RECORDED_COUNTER_DATA_UNAVAILABLE";
            result.Complete = result.ErrorCode == null && result.Issues.Count == 0 && result.Frames.All(frame => frame.issues.Count == 0);
            return result;
        }

        public static object[] Summaries(RecordedProfilerCounterResult result, RecordedProfilerCounterDefinition[] definitions) =>
            definitions.Select(definition =>
            {
                var rows = result.Frames.SelectMany(frame => frame.counters).Where(item => item.key == definition.Key).ToArray();
                var values = rows.Where(item => item.value.HasValue).Select(item => item.value.Value).OrderBy(value => value).ToArray();
                double? median = values.Length == 0 ? (double?)null : values.Length % 2 == 1 ? values[values.Length / 2] :
                    values[values.Length / 2 - 1] / 2 + values[values.Length / 2] / 2;
                return (object)new { key = definition.Key, markerName = definition.MarkerName,
                    unit = rows.FirstOrDefault(item => item.value.HasValue)?.unit ?? definition.Unit,
                    requestedFrames = result.SelectedFrames.Length, readFrames = rows.Length, availableFrames = values.Length,
                    unavailableFrames = rows.Count(item => !item.value.HasValue),
                    min = values.Length > 0 ? values[0] : (double?)null,
                    max = values.Length > 0 ? values[values.Length - 1] : (double?)null,
                    avg = Average(values), median,
                    p95 = values.Length > 0 ? values[Math.Max(0, (int)Math.Ceiling(values.Length * 0.95) - 1)] : (double?)null,
                    last = rows.LastOrDefault()?.value,
                    statuses = rows.Select(item => item.status).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray() };
            }).ToArray();

        static double? Average(double[] sortedValues)
        {
            if (sortedValues.Length == 0) return null;
            double mean = 0;
            for (int i = 0; i < sortedValues.Length; i++) mean += (sortedValues[i] - mean) / (i + 1);
            return mean;
        }
    }
}
