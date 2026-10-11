#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

// Owned qualification fixture. Installation/capture is controlled by the parent agent.
public static class UniBridgeRecordedCounterFixture
{
    const string CountName = "UniBridge Fixture Recorded Count 20261011";
    const string BytesName = "UniBridge Fixture Recorded Bytes 20261011";
    static readonly ProfilerCounterValue<long> Count = new(ProfilerCategory.Scripts, CountName, ProfilerMarkerDataUnit.Count);
    static readonly ProfilerCounterValue<long> Bytes = new(ProfilerCategory.Memory, BytesName, ProfilerMarkerDataUnit.Bytes);
    static bool running;
    static bool holdForWire, restorePending;
    static bool previousEnabled, previousEditor;
    static bool previousCallstacks, hadRecordedFrames, savedBaseline, captureStarted, restoredBaseline;
    static int originalFirstFrame, originalLastFrame;
    static int ticks, startFrame;
    static double deadline;
    static string directory;
    static int stableFrameIndex;
    static JObject stableCounterObservation;
    static readonly List<object> cases = new();

    [MenuItem("Tools/UniBridge Validation/Profiler 4/GenerateKnownCapture")]
    public static void GenerateKnownCapture() => StartCapture(false);

    [MenuItem("Tools/UniBridge Validation/Profiler 4/GenerateKnownCaptureForWire")]
    public static void GenerateKnownCaptureForWire() => StartCapture(true);

    static void StartCapture(bool wire)
    {
        if (running || restorePending) throw new InvalidOperationException("Owned counter fixture is running or awaiting baseline restoration.");
        holdForWire = wire; restorePending = false; stableFrameIndex = -1; stableCounterObservation = null;
        directory = Path.GetFullPath(Path.Combine("Library", "AgentValidation", "Profiler4-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        cases.Clear();
        previousEnabled = ProfilerDriver.enabled;
        previousEditor = ProfilerDriver.profileEditor;
        previousCallstacks = Profiler.enableAllocationCallstacks;
        originalFirstFrame = ProfilerDriver.firstFrameIndex;
        originalLastFrame = ProfilerDriver.lastFrameIndex;
        hadRecordedFrames = originalFirstFrame >= 0 && originalLastFrame >= originalFirstFrame;
        savedBaseline = captureStarted = restoredBaseline = false;
        startFrame = ProfilerDriver.lastFrameIndex;
        ticks = 0;
        deadline = EditorApplication.timeSinceStartup + 30;
        running = true;
        try
        {
            ProfilerDriver.enabled = false;
            if (hadRecordedFrames)
            {
                string baseline = Path.Combine(directory, "baseline.data");
                savedBaseline = ProfilerDriver.SaveProfile(baseline) && File.Exists(baseline);
                if (!savedBaseline) throw new IOException("Existing Profiler capture could not be preserved; owned capture was not started.");
            }
            ProfilerDriver.profileEditor = true;
            ProfilerDriver.enabled = true;
            captureStarted = true;
            EditorApplication.update += Tick;
            Debug.Log("UniBridge Profiler4 fixture started: " + directory);
        }
        catch { Restore(); throw; }
    }

    static void Tick()
    {
        try
        {
            Count.Value = ticks % 4; // includes an actual measured zero, not missing data.
            Bytes.Value = (ticks % 4) * 4096;
            ticks++;
            if (ticks < 32 && EditorApplication.timeSinceStartup < deadline) return;
            EditorApplication.update -= Tick;
            ProfilerDriver.enabled = false;
            _ = Verify();
        }
        catch (Exception ex) { Finish(ex); }
    }

    static async Task Verify()
    {
        Exception failure = null;
        try
        {
            int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
            var observations = new List<JObject>();
            int cursor = last, scanned = 0;
            while (cursor >= first && cursor > startFrame && scanned++ < 120)
            {
                using (var raw = ProfilerDriver.GetRawFrameDataView(cursor, 0))
                {
                    if (raw.valid)
                    {
                        int countId = raw.GetMarkerId(CountName), bytesId = raw.GetMarkerId(BytesName);
                        if (countId >= 0 && bytesId >= 0 && raw.HasCounterValue(countId) && raw.HasCounterValue(bytesId))
                            observations.Add(new JObject { ["frameIndex"] = cursor, ["count"] = raw.GetCounterValueAsLong(countId),
                                ["bytes"] = raw.GetCounterValueAsLong(bytesId),
                                ["countDescriptor"] = JArray.FromObject(raw.GetMarkerMetadataInfo(countId)),
                                ["bytesDescriptor"] = JArray.FromObject(raw.GetMarkerMetadataInfo(bytesId)),
                                ["countFlags"] = raw.GetMarkerFlags(countId).ToString(),
                                ["countCategory"] = raw.GetCategoryInfo(raw.GetMarkerCategoryIndex(countId)).name });
                    }
                }
                int previous = ProfilerDriver.GetPreviousFrameIndex(cursor);
                if (previous < 0 || previous >= cursor) break;
                cursor = previous;
            }
            observations.Reverse();
            File.WriteAllText(Path.Combine(directory, "independent-native-observations.json"), JsonConvert.SerializeObject(observations, Formatting.Indented));
            Check("Owned native frame counters captured", observations.Count >= 2, observations.Count.ToString());
            Check("Capture contains measured zero", observations.Any(row => row.Value<long>("count") == 0), "Zero frame is required");
            stableCounterObservation = observations.FirstOrDefault(row => row.Value<long>("count") > 0) ?? observations.FirstOrDefault();
            stableFrameIndex = stableCounterObservation?.Value<int>("frameIndex") ?? -1;
            var tested = observations.Take(12).ToArray();
            foreach (var observation in tested)
            {
                int frameIndex = observation.Value<int>("frameIndex");
                var response = JObject.FromObject(await RuntimeProfiler.HandleCommand(new RuntimeProfilerParams
                { Action = (RuntimeProfilerAction)5, FrameIndex = frameIndex, FrameCount = 1,
                    Metrics = new[] { CountName, BytesName, "UniBridge Missing Counter 20261011" }, ReturnSamples = true, SaveToFile = false }));
                File.WriteAllText(Path.Combine(directory, "production-frame-" + frameIndex + ".json"), response.ToString());
                var rows = response["data"]?["frames"]?[0]?["counters"] as JArray;
                Check("Production frame identity " + frameIndex, response.Value<bool>("success") && rows?.Count == 3, response.ToString());
                if (rows == null || rows.Count < 3) continue;
                Check("Count exact historical value " + frameIndex, rows[0].Value<string>("rawValue") == observation.Value<long>("count").ToString(System.Globalization.CultureInfo.InvariantCulture) && rows[0].Value<string>("status") == "recorded", rows[0].ToString());
                Check("Bytes exact historical value " + frameIndex, rows[1].Value<string>("rawValue") == observation.Value<long>("bytes").ToString(System.Globalization.CultureInfo.InvariantCulture) && rows[1].Value<string>("unit") == "bytes", rows[1].ToString());
                Check("Missing stays unknown " + frameIndex, rows[2]["value"].Type == JTokenType.Null && rows[2].Value<string>("status") == "MARKER_NOT_RECORDED", rows[2].ToString());
            }
            if (observations.Count >= 2)
            {
                var response = JObject.FromObject(await RuntimeProfiler.HandleCommand(new RuntimeProfilerParams
                { Action = (RuntimeProfilerAction)5, FrameIndex = observations[0].Value<int>("frameIndex"), FrameCount = observations.Count,
                    Metrics = new[] { CountName, BytesName }, ReturnSamples = true, SaveToFile = false }));
                File.WriteAllText(Path.Combine(directory, "production-range.json"), response.ToString());
                var actual = response["data"]?["summaries"]?[0];
                double[] expected = observations.Select(row => (double)row.Value<long>("count")).ToArray();
                Check("Native range summaries use recorded values", response.Value<bool>("success") && actual?.Value<double>("min") == expected.Min() &&
                    actual.Value<double>("max") == expected.Max() && Math.Abs(actual.Value<double>("avg") - expected.Average()) < 0.000001 &&
                    actual.Value<int>("availableFrames") == expected.Length, response.ToString());
            }
            var defaults = JObject.FromObject(await RuntimeProfiler.HandleCommand(new RuntimeProfilerParams
            { Action = (RuntimeProfilerAction)5, FrameIndex = observations.Count > 0 ? observations[0].Value<int>("frameIndex") : (int?)null,
                FrameCount = 1, ReturnSamples = true, SaveToFile = false }));
            File.WriteAllText(Path.Combine(directory, "production-default-counters.json"), defaults.ToString());
            Check("Default catalogue returns explicit availability", defaults.Value<bool>("success") && defaults["data"]?["frames"]?[0]?["counters"] is JArray list && list.Count == 8 && list.All(row => row["status"] != null), defaults.ToString());
            Check("At least one real default counter is readable", defaults["data"]?["frames"]?[0]?["counters"] is JArray builtins && builtins.Any(row => row.Value<string>("status") == "recorded"), defaults.ToString());
        }
        catch (Exception ex) { failure = ex; }
        finally { Finish(failure); }
    }

    static void Check(string name, bool passed, string detail) => cases.Add(new { name, passed, detail = passed ? null : detail });

    [MenuItem("Tools/UniBridge Validation/Profiler 4/RestoreAfterWire")]
    public static void RestoreAfterWire()
    {
        if (!restorePending) throw new InvalidOperationException("No successful counter wire capture is awaiting restoration.");
        Exception failure = null;
        try { Restore(); } catch (Exception error) { failure = error; }
        bool restored = failure == null && ProfilerDriver.enabled == previousEnabled &&
            ProfilerDriver.profileEditor == previousEditor && Profiler.enableAllocationCallstacks == previousCallstacks && restoredBaseline;
        restorePending = !restored;
        var receipt = new JObject { ["passed"] = restored, ["restorePending"] = restorePending,
            ["restoredBaseline"] = restoredBaseline, ["restoredEnabled"] = ProfilerDriver.enabled,
            ["restoredEditor"] = ProfilerDriver.profileEditor, ["restoredCallstacks"] = Profiler.enableAllocationCallstacks,
            ["error"] = failure?.ToString() };
        File.WriteAllText(Path.Combine(directory, "restoration-receipt.json"), receipt.ToString());
        string resultPath = Path.Combine(directory, "result.json");
        var result = JObject.Parse(File.ReadAllText(resultPath));
        result["restorationReceipt"] = receipt; result["restorePending"] = restorePending;
        result["restoredBaseline"] = restoredBaseline; result["restoredEnabled"] = ProfilerDriver.enabled;
        result["restoredEditor"] = ProfilerDriver.profileEditor; result["restoredCallstacks"] = Profiler.enableAllocationCallstacks;
        if (!restored) result["passed"] = false;
        File.WriteAllText(resultPath, result.ToString());
        if (!restored) throw new IOException("Counter wire capture baseline restoration failed; evidence retained.", failure);
    }

    static void Restore()
    {
        EditorApplication.update -= Tick;
        try
        {
            ProfilerDriver.enabled = false;
            if (savedBaseline)
            {
                restoredBaseline = ProfilerDriver.LoadProfile(Path.Combine(directory, "baseline.data"), false);
                if (!restoredBaseline) throw new IOException("Profiler baseline restore failed; baseline.data is retained.");
            }
            else if (captureStarted && !hadRecordedFrames)
            {
                ProfilerDriver.ClearAllFrames();
                restoredBaseline = true;
            }
        }
        finally
        {
            Profiler.enableAllocationCallstacks = previousCallstacks;
            ProfilerDriver.profileEditor = previousEditor;
            ProfilerDriver.enabled = previousEnabled;
            running = false;
        }
    }
    static void Finish(Exception failure)
    {
        try
        {
            if (failure != null) cases.Add(new { name = "Fixture exception", passed = false, detail = failure.ToString() });
            bool verified = cases.All(item => (bool)item.GetType().GetProperty("passed").GetValue(item));
            if (holdForWire && verified) restorePending = true;
            else
            {
                try { Restore(); }
                catch (Exception ex) { cases.Add(new { name = "Profiler baseline restoration", passed = false, detail = ex.ToString() }); }
            }
            bool passed = cases.All(item => (bool)item.GetType().GetProperty("passed").GetValue(item));
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonConvert.SerializeObject(new { passed, unityVersion = Application.unityVersion,
                processBits = IntPtr.Size * 8, ticks, previousEnabled, previousEditor,
                previousCallstacks, restoredCallstacks = Profiler.enableAllocationCallstacks,
                originalFirstFrame, originalLastFrame, hadRecordedFrames, savedBaseline, restoredBaseline,
                restoredEnabled = ProfilerDriver.enabled, restoredEditor = ProfilerDriver.profileEditor,
                restorePending, stableFrameIndex, stableCounterObservation,
                wireCounterRequest = new { Action = "RecordedCounters", FrameIndex = stableFrameIndex, FrameCount = 1,
                    Metrics = new[] { CountName, BytesName, "UniBridge Missing Counter 20261011" }, ReturnSamples = true, SaveToFile = false },
                wireDefaultRequest = new { Action = "RecordedCounters", FrameIndex = stableFrameIndex, FrameCount = 1, ReturnSamples = true, SaveToFile = false },
                restoreAfterWireMenu = "Tools/UniBridge Validation/Profiler 4/RestoreAfterWire", cases }, Formatting.Indented));
            Debug.Log("UniBridge Profiler4 fixture " + (passed ? "PASS" : "FAIL") + ": " + directory);
        }
        catch (Exception ex)
        {
            try { Restore(); restorePending = false; } catch (Exception restoreError) { Debug.LogError(restoreError); }
            Debug.LogError("Owned counter fixture restoration/report failed: " + ex);
        }
    }
}
#endif
