using System.Text.Json;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor.Profiling;
using UnityEditorInternal;
using Unity.Profiling;
using Unity.Profiling.LowLevel;

class CounterRegression
{
    static readonly List<object> cases = new();
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Case(string name, Action action)
    {
        try { ProfilerDriver.Frames.Clear(); action(); Closed(); cases.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { cases.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static NativeCounter Counter(string name, object value, ProfilerMarkerDataType type = ProfilerMarkerDataType.Int64, ProfilerMarkerDataUnit unit = ProfilerMarkerDataUnit.Count) =>
        new() { Name = name, Value = value, Metadata = new[] { new FrameDataView.MarkerMetadataInfo { type = type, unit = unit } } };
    static NativeFrame Install(int index, params NativeCounter[] counters)
    { var frame = new NativeFrame { Index = index, Counters = counters }; ProfilerDriver.Frames[index] = frame; return frame; }
    static RecordedProfilerCounterResult Read(string name = "Batches Count", int? first = null, int count = 1, Func<bool> expired = null, Action cancel = null) =>
        RecordedProfilerCounterReader.Read(new UnityRecordedProfilerCounterSource(), first, count, RecordedProfilerCounterReader.Resolve(new[] { name }), expired, cancel);
    static void Closed() => Assert(ProfilerDriver.Frames.Values.All(frame => frame.Opened == frame.Disposed && frame.AccessAfterDispose == 0), "Native view lifetime mismatch");
    static JObject Run(RuntimeProfilerParams request) => McpJson.ObjectFromObject(RuntimeProfiler.RunCounters(request).GetAwaiter().GetResult());
    static void Schema(JToken token, JObject schema)
    {
        if (schema["anyOf"] is JArray alternatives)
        {
            foreach (JObject alternative in alternatives) { try { Schema(token, alternative); return; } catch { } }
            throw new Exception("No schema branch matches " + token.Path);
        }
        if (schema["const"] != null) Assert(JToken.DeepEquals(token, schema["const"]), "Const mismatch " + token.Path);
        if (schema["type"] != null)
        {
            var names = schema["type"] is JArray many ? many.Values<string>().ToArray() : new[] { schema.Value<string>("type") };
            string type = token.Type switch { JTokenType.Null => "null", JTokenType.Object => "object", JTokenType.Array => "array", JTokenType.Boolean => "boolean", JTokenType.Integer => "integer", JTokenType.Float => "number", _ => "string" };
            Assert(names.Contains(type) || type == "integer" && names.Contains("number"), "Type mismatch " + token.Path);
        }
        if (token is JObject obj && schema["properties"] is JObject properties)
        {
            foreach (string required in schema["required"]?.Values<string>() ?? Array.Empty<string>()) Assert(obj.Property(required) != null, "Missing schema field " + required);
            foreach (var property in obj.Properties())
            { Assert(properties.Property(property.Name) != null || schema.Value<bool?>("additionalProperties") != false, "Unexpected schema field " + property.Name); if (properties[property.Name] is JObject child) Schema(property.Value, child); }
        }
        if (token is JArray array && schema["items"] is JObject items) foreach (var item in array) Schema(item, items);
    }
    static int Main(string[] args)
    {
        RuntimeProfiler.FixtureStorage = args[1];
        Case("Sparse frame traversal is exact and ascending", () => { Install(3, Counter("Batches Count", 3L)); Install(9, Counter("Batches Count", 9L)); Install(27, Counter("Batches Count", 27L)); var result = Read(count: 3); Assert(result.Complete && result.SelectedFrames.SequenceEqual(new[] { 3, 9, 27 }), "Invented contiguous frames"); });
        Case("Explicit start follows next recorded frame", () => { Install(2, Counter("Batches Count", 2L)); Install(7, Counter("Batches Count", 7L)); Assert(Read(first: 2, count: 2).SelectedFrames.SequenceEqual(new[] { 2, 7 }), "Wrong explicit traversal"); });
        Case("Measured zero stays recorded zero", () => { Install(1, Counter("Batches Count", 0L)); var value = Read().Frames[0].counters[0]; Assert(value.value == 0 && value.status == "recorded", "Zero changed to unknown"); });
        Case("Missing value is never read or changed to zero", () => { var counter = Counter("Batches Count", 0L); counter.Present = false; var frame = Install(1, counter); var result = Read(); Assert(result.Complete && result.Frames[0].counters[0].value == null && result.Frames[0].counters[0].status == "COUNTER_VALUE_NOT_RECORDED" && frame.ValueReads == 0, "Absent value fabricated"); });
        Case("Missing marker is explicitly unavailable", () => { Install(1); Assert(Read().Frames[0].counters[0].status == "MARKER_NOT_RECORDED", "Missing marker unspecified"); });
        Case("Sample marker cannot masquerade as counter", () => { var counter = Counter("Batches Count", 99L); counter.Flags = MarkerFlags.Default; var frame = Install(1, counter); Assert(Read().Frames[0].counters[0].status == "MARKER_IS_NOT_COUNTER" && frame.ValueReads == 0, "Sample was read as counter"); });
        Case("Recorded unit mismatch prevents typed value read", () => { var frame = Install(1, Counter("Batches Count", 9L, unit: ProfilerMarkerDataUnit.Bytes)); Assert(Read().Frames[0].counters[0].status == "COUNTER_UNIT_MISMATCH" && frame.ValueReads == 0, "Wrong unit accepted"); });
        Case("Descriptor unavailable is truthful", () => { var counter = Counter("Batches Count", 9L); counter.Metadata = Array.Empty<FrameDataView.MarkerMetadataInfo>(); Install(1, counter); Assert(Read().Frames[0].counters[0].status == "COUNTER_DESCRIPTOR_UNAVAILABLE", "Missing descriptor invented"); });
        Case("Unsupported datatype never enters typed getter", () => { var frame = Install(1, Counter("Batches Count", "text", ProfilerMarkerDataType.String16)); Assert(Read().Frames[0].counters[0].status == "COUNTER_TYPE_UNSUPPORTED" && frame.ValueReads == 0, "Nonnumeric data read"); });
        foreach (var item in new[] { (ProfilerMarkerDataType.Int32, (object)17, "17"), (ProfilerMarkerDataType.UInt32, (object)uint.MaxValue, uint.MaxValue.ToString()), (ProfilerMarkerDataType.Int64, (object)9007199254740993L, "9007199254740993"), (ProfilerMarkerDataType.UInt64, (object)ulong.MaxValue, ulong.MaxValue.ToString()), (ProfilerMarkerDataType.Float, (object)1.25f, "1.25"), (ProfilerMarkerDataType.Double, (object)2.5d, "2.5") })
            Case("Exact numeric API and raw string: " + item.Item1, () => { Install(1, Counter("Owned counter", item.Item2, item.Item1)); var value = Read("Owned counter").Frames[0].counters[0]; Assert(value.status == "recorded" && value.rawValue == item.Item3, "Wrong getter or numeric precision loss"); });
        foreach (object number in new object[] { -1L, double.NaN, double.PositiveInfinity })
            Case("Negative/nonfinite is unknown rather than clamped: " + number, () => { Install(1, Counter("Owned counter", number, number is long ? ProfilerMarkerDataType.Int64 : ProfilerMarkerDataType.Double)); var value = Read("Owned counter").Frames[0].counters[0]; Assert(value.value == null && value.status == "COUNTER_VALUE_OUT_OF_RANGE", "Invalid measured value masked"); });
        Case("Time values scale from recorded nanoseconds", () => { Install(1, Counter("CPU Main Thread Active Time", 12345678L, unit: ProfilerMarkerDataUnit.TimeNanoseconds)); Assert(Math.Abs(Read("cpu_main_active_ms").Frames[0].counters[0].value.Value - 12.345678) < 0.0000001, "Wrong time scale"); });
        Case("Summary ignores unavailable values and preserves final unknown", () => { Install(1, Counter("Batches Count", 0L)); Install(3, Counter("Batches Count", 10L)); var missing = Counter("Batches Count", 0L); missing.Present = false; Install(9, missing); var result = Read(count: 3); var summary = McpJson.ObjectFromObject(RecordedProfilerCounterReader.Summaries(result, RecordedProfilerCounterReader.Resolve(new[] { "batches_count" }))[0]); Assert(summary.Value<int>("availableFrames") == 2 && summary.Value<int>("unavailableFrames") == 1 && summary.Value<double>("avg") == 5 && summary.Value<double>("median") == 5 && summary["last"].Type == JTokenType.Null, "Summary fabricated samples or final value"); });
        Case("All unavailable summary stays unknown", () => { Install(1); var result = Read(); var summary = McpJson.ObjectFromObject(RecordedProfilerCounterReader.Summaries(result, RecordedProfilerCounterReader.Resolve(null))[0]); Assert(summary["avg"].Type == JTokenType.Null && summary.Value<int>("availableFrames") == 0, "Unavailable summary became zero"); });
        Case("Custom counter unit changes cannot mix summary dimensions", () => { Install(1, Counter("Owned counter", 10L, unit: ProfilerMarkerDataUnit.Bytes)); Install(3, Counter("Owned counter", 99L)); var result = Read("Owned counter", count: 2); Assert(result.Frames[1].counters[0].value == null && result.Frames[1].counters[0].status == "COUNTER_UNIT_CHANGED", "Bytes were averaged with counts"); });
        Case("Finite large counter average cannot overflow", () => { Install(1, Counter("Owned counter", double.MaxValue, ProfilerMarkerDataType.Double)); Install(3, Counter("Owned counter", double.MaxValue, ProfilerMarkerDataType.Double)); var result = Read("Owned counter", count: 2); var summary = McpJson.ObjectFromObject(RecordedProfilerCounterReader.Summaries(result, RecordedProfilerCounterReader.Resolve(new[] { "Owned counter" }))[0]); Assert(summary.Value<double>("avg") == double.MaxValue, "Finite summary overflowed"); });
        Case("Native per-counter failure is retained without leaking view", () => { var counter = Counter("Batches Count", 10L); counter.ThrowRead = true; Install(1, counter); Assert(Read().Frames[0].counters[0].status == "COUNTER_READ_FAILED", "Getter failure hidden"); });
        Case("Invalid frame view is disposed and response partial", () => { var frame = Install(1); frame.Valid = false; Assert(!Read().Complete && frame.Disposed == 1, "Invalid frame leaked or completed"); });
        Case("Cancellation inside native view disposes it", () => { Install(1, Counter("Batches Count", 1L)); int checks = 0; bool stopped = false; try { Read(cancel: () => { if (++checks == 3) throw new OperationCanceledException(); }); } catch (OperationCanceledException) { stopped = true; } Assert(stopped && ProfilerDriver.Frames[1].Opened == 1, "Cancellation failed to enter bounded reader"); });
        Case("Timeout returns structured partial instead of success", () => { Install(1, Counter("Batches Count", 1L)); int checks = 0; var result = Read(expired: () => ++checks == 3); Assert(result.TimedOut && !result.Complete && result.Frames[0].counters[0].value == null && result.Issues.Contains("EXTRACTION_TIMEOUT"), "Timeout falsely complete"); });
        Case("Requested frame window cannot silently shorten", () => { Install(1, Counter("Batches Count", 1L)); var result = Read(count: 2); Assert(!result.Complete && result.Issues.Contains("FRAME_RANGE_INCOMPLETE"), "Short range silently completed"); });
        Case("Counter request cap rejects rather than truncates", () => { bool rejected = false; try { RecordedProfilerCounterReader.Resolve(Enumerable.Range(0, 33).Select(i => i.ToString()).ToArray()); } catch (ArgumentException) { rejected = true; } Assert(rejected, "Counter cap silently truncated"); });
        Case("Counter aliases are deduplicated", () => Assert(RecordedProfilerCounterReader.Resolve(new[] { "batches_count", "Batches Count" }).Length == 1, "Duplicate native counter lookup"));
        Case("Complete tool response matches closed output schema", () => { Install(1, Counter("Batches Count", 17L)); var response = Run(new() { Metrics = new[] { "batches_count", "Missing fixture" }, SaveToFile = false, ReturnSamples = true }); Assert(response.Value<bool>("success"), "Extraction unavailable became global failure"); Schema(response, RuntimeProfilerActionOutputRules.Build()["RecordedCounters"]); });
        Case("Unavailable and invalid request match closed schemas", () => { foreach (var request in new[] { new RuntimeProfilerParams { SaveToFile = false }, new RuntimeProfilerParams { Metrics = new[] { "" }, SaveToFile = false }, new RuntimeProfilerParams { RequirePlayMode = true, SaveToFile = false } }) { var response = Run(request); Assert(!response.Value<bool>("success"), "Unavailable unexpectedly succeeded"); Schema(response, RuntimeProfilerActionOutputRules.Build()["RecordedCounters"]); } });
        Case("Partial response matches closed schema", () => { Install(1, Counter("Batches Count", 17L)); var response = Run(new() { FrameCount = 2, SaveToFile = false, ReturnSamples = true }); Assert(response.Value<string>("code") == "RECORDED_COUNTER_DATA_PARTIAL", "Wrong partial family"); Schema(response, RuntimeProfilerActionOutputRules.Build()["RecordedCounters"]); });
        Case("Default compact response and saved export retain exact table", () => { Install(1, Counter("Batches Count", 17L)); var response = Run(new() { Metrics = new[] { "batches_count" }, SaveToFile = true }); Assert(response["data"]["frames"].Type == JTokenType.Null, "Inline default not bounded compact"); var saved = JObject.Parse(File.ReadAllText(response["data"].Value<string>("savedPath"))); Assert(saved["frames"][0]["counters"][0].Value<string>("rawValue") == "17", "Saved table lost exact raw value"); Schema(response, RuntimeProfilerActionOutputRules.Build()["RecordedCounters"]); });
        Case("New action preserves original enum ordinals", () => Assert((int)RuntimeProfilerAction.Snapshot == 0 && (int)RuntimeProfilerAction.RecordedFrames == 4 && (int)RuntimeProfilerAction.RecordedCounters == 5, "Enum ordinal compatibility changed"));
        int failed = cases.Count(item => (bool)item.GetType().GetProperty("passed").GetValue(item) == false);
        File.WriteAllText(args[0], System.Text.Json.JsonSerializer.Serialize(new { passed = failed == 0, passedCount = cases.Count - failed, failedCount = failed,
            scope = "Exact recorded counter adapter/reader/action/schema sources against deterministic native API substrate; no Unity execution", cases }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
