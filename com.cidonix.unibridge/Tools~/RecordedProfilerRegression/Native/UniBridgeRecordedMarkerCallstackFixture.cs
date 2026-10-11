#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using Unity.Collections;
using Unity.Jobs;
using System.Threading;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

public static class UniBridgeRecordedMarkerCallstackFixture
{
    const string MarkerName = "UniBridge Fixture Allocation 20261011";
    static readonly ProfilerMarker Marker = new(MarkerName);
    static bool running, previousEnabled, previousEditor, previousCallstacks, hadRecordedFrames, baselineSaved, captureStarted, restoredBaseline;
    static bool holdForWire, restorePending;
    static ProfilerMemoryRecordMode previousRecordMode;
    static readonly string[] StackMarkers = { "GC.Alloc", "JobHandle.Complete", "UnsafeUtility.Malloc", "Native.Alloc" };
    static int originalFirstFrame, originalLastFrame, startFrame, ticks, phase;
    static double deadline;
    static string directory;
    static JObject withoutStack, withStack;
    static readonly List<object> cases = new();

    [MenuItem("Tools/UniBridge Validation/Remaining Profiler 4/GenerateKnownCapture")]
    public static void GenerateKnownCapture() => StartCapture(false);

    [MenuItem("Tools/UniBridge Validation/Remaining Profiler 4/GenerateKnownCaptureForWire")]
    public static void GenerateKnownCaptureForWire() => StartCapture(true);

    static void StartCapture(bool wire)
    {
        if (running || restorePending) throw new InvalidOperationException("Owned callstack fixture is running or awaiting baseline restoration.");
        holdForWire = wire; restorePending = false; withoutStack = withStack = null;
        directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "AgentValidation", "RemainingProfiler4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        cases.Clear();
        previousEnabled = ProfilerDriver.enabled; previousEditor = ProfilerDriver.profileEditor;
        previousCallstacks = Profiler.enableAllocationCallstacks; previousRecordMode = ProfilerDriver.memoryRecordMode;
        originalFirstFrame = ProfilerDriver.firstFrameIndex; originalLastFrame = ProfilerDriver.lastFrameIndex;
        hadRecordedFrames = originalFirstFrame >= 0 && originalLastFrame >= originalFirstFrame;
        baselineSaved = captureStarted = restoredBaseline = false;
        running = true; phase = ticks = 0; deadline = EditorApplication.timeSinceStartup + 40;
        try
        {
            ProfilerDriver.enabled = false;
            if (hadRecordedFrames)
            {
                string path = Path.Combine(directory, "baseline.data");
                baselineSaved = ProfilerDriver.SaveProfile(path) && File.Exists(path);
                if (!baselineSaved) throw new IOException("Existing Profiler capture could not be preserved; owned capture was not started.");
            }
            ProfilerDriver.profileEditor = true;
            ProfilerDriver.memoryRecordMode = ProfilerMemoryRecordMode.None;
            Profiler.enableAllocationCallstacks = false;
            startFrame = ProfilerDriver.lastFrameIndex;
            ProfilerDriver.enabled = true;
            captureStarted = true;
            EditorApplication.update += Tick;
            Debug.Log("UniBridge RemainingProfiler4 fixture started: " + directory);
        }
        catch (Exception ex) { Finish(ex); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void AllocateKnownBytes()
    {
        using (Marker.Auto())
        {
            var allocation = new byte[8192];
            allocation[0] = 17;
            GC.KeepAlive(allocation);
            var native = new NativeArray<byte>(8192, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            try { native[0] = 17; } finally { native.Dispose(); }
            var handle = new OwnedWaitJob().Schedule();
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();
        }
    }

    struct OwnedWaitJob : IJob
    {
        public void Execute() { Thread.Sleep(8); }
    }

    static void Tick()
    {
        try
        {
            AllocateKnownBytes();
            if (EditorApplication.timeSinceStartup >= deadline) throw new TimeoutException("Owned native callstack capture deadline elapsed.");
            if (++ticks < 16) return;
            ProfilerDriver.enabled = false;
            var observation = FindOwnedSamples(startFrame, phase == 1);
            if (phase == 0)
            {
                withoutStack = observation;
                File.WriteAllText(Path.Combine(directory, "independent-no-stack.json"), observation.ToString());
                phase = 1; ticks = 0; startFrame = ProfilerDriver.lastFrameIndex;
                Profiler.enableAllocationCallstacks = true;
                ProfilerDriver.memoryRecordMode = ProfilerMemoryRecordMode.GCAlloc | ProfilerMemoryRecordMode.UnsafeUtilityMalloc | ProfilerMemoryRecordMode.JobHandleComplete | ProfilerMemoryRecordMode.NativeAlloc;
                ProfilerDriver.enabled = true;
            }
            else
            {
                withStack = observation;
                withoutStack["WaitForJobGroupID"] = FindOwnedAllocation(startFrame, false, "WaitForJobGroupID");
                File.WriteAllText(Path.Combine(directory, "independent-no-stack.json"), withoutStack.ToString());
                File.WriteAllText(Path.Combine(directory, "independent-with-stack.json"), observation.ToString());
                EditorApplication.update -= Tick;
                _ = Verify();
            }
        }
        catch (Exception ex) { Finish(ex); }
    }

    static JObject FindOwnedSamples(int afterFrame, bool requireStack)
    {
        var observations = new JObject();
        // Native allocation markers need their recording mode; missing-stack evidence uses GC.Alloc.
        foreach (var marker in requireStack ? StackMarkers : new[] { "GC.Alloc" })
            observations[marker] = FindOwnedAllocation(afterFrame, requireStack, marker);
        return observations;
    }

    static JObject FindOwnedAllocation(int afterFrame, bool requireStack, string markerName)
    {
        int first = ProfilerDriver.firstFrameIndex, cursor = ProfilerDriver.lastFrameIndex, examined = 0;
        var stack = new List<ulong>();
        while (cursor >= first && cursor > afterFrame && examined++ < 80)
        {
            using (var raw = ProfilerDriver.GetRawFrameDataView(cursor, 0))
            {
                if (raw.valid)
                {
                    int ownMarker = raw.GetMarkerId(MarkerName), gcMarker = raw.GetMarkerId(markerName);
                    if (ownMarker >= 0 && gcMarker >= 0)
                    {
                        int bounded = Math.Min(raw.sampleCount, 100000);
                        for (int i = 0; i < bounded; i++)
                        {
                            if (raw.GetSampleMarkerId(i) != ownMarker) continue;
                            int end = Math.Min(bounded - 1, i + raw.GetSampleChildrenCountRecursive(i));
                            for (int child = i + 1; child <= end; child++)
                            {
                                if (raw.GetSampleMarkerId(child) != gcMarker) continue;
                                stack.Clear(); raw.GetSampleCallstack(child, stack);
                                if (requireStack && stack.Count == 0 || !requireStack && stack.Count > 0) continue;
                                var resolved = new JArray();
                                foreach (ulong address in stack.Take(64))
                                {
                                    var method = raw.ResolveMethodInfo(address);
                                    resolved.Add(new JObject { ["address"] = "0x" + address.ToString("X16", CultureInfo.InvariantCulture),
                                        ["method"] = method.methodName, ["sourceFile"] = method.sourceFileName, ["sourceLine"] = method.sourceFileLine });
                                }
                                if (requireStack && !resolved.Any(row => row.Value<string>("method")?.Contains(nameof(AllocateKnownBytes)) == true)) continue;
                                return new JObject { ["frameIndex"] = cursor, ["threadId"] = raw.threadId.ToString(CultureInfo.InvariantCulture), ["sampleIndex"] = child,
                                    ["sampleId"] = cursor.ToString(CultureInfo.InvariantCulture) + ":" + raw.threadId.ToString(CultureInfo.InvariantCulture) + ":" + child.ToString(CultureInfo.InvariantCulture),
                                    ["marker"] = markerName, ["recordedDepth"] = stack.Count, ["symbols"] = resolved };
                            }
                        }
                    }
                }
            }
            int previous = ProfilerDriver.GetPreviousFrameIndex(cursor);
            if (previous < 0 || previous >= cursor) break;
            cursor = previous;
        }
        throw new InvalidOperationException("Owned " + (requireStack ? "resolved recorded" : "unrecorded") + " " + markerName + " callstack not found; native evidence did not satisfy the fixture.");
    }

    static JObject Request(JObject observation, bool include)
    {
        var request = new JObject { ["Action"] = "RecordedFrames", ["FrameIndex"] = observation["frameIndex"], ["FrameCount"] = 1,
            ["ThreadIds"] = new JArray(observation["threadId"]), ["MaxThreads"] = 1, ["MaxRawSamples"] = 100000,
            ["IncludeCallstacks"] = include, ["MaxCallstackSamples"] = 1, ["MaxCallstackDepth"] = 64,
            ["MarkerFilters"] = new JArray("GC.Alloc"), ["RecordedSortBy"] = "GcBytes", ["ReturnSamples"] = true, ["SaveToFile"] = false };
        request["MarkerFilters"] = new JArray(observation["marker"]);
        if (include)
        {
            request["CallstackSampleIds"] = new JArray(observation["sampleId"]);
            request["CallstackMarkers"] = new JArray(observation["marker"]);
        }
        return request;
    }

    static async Task<JObject> Read(JObject observation, bool include)
    {
        var parameters = Request(observation, include).ToObject<RuntimeProfilerParams>();
        return JObject.FromObject(await RuntimeProfiler.HandleCommand(parameters));
    }

    static JToken Sample(JObject response, JObject observation) =>
        (response["data"]?["frames"]?[0]?["threads"]?[0]?["samples"] as JArray)?.FirstOrDefault(row => row.Value<string>("sampleId") == observation.Value<string>("sampleId"));

    static async Task Verify()
    {
        Exception failure = null;
        try
        {
            foreach (string marker in StackMarkers)
            {
                var off = (JObject)withoutStack[marker]; var on = (JObject)withStack[marker];
                Check("Native stack resolves owned method: " + marker, on["symbols"].Any(row => row.Value<string>("method")?.Contains(nameof(AllocateKnownBytes)) == true), on.ToString());
                if (off != null)
                {
                    Check("Native disabled stack: " + marker, off.Value<int>("recordedDepth") == 0, off.ToString());
                    var negative = await Read(off, true); var missing = Sample(negative, off);
                    File.WriteAllText(Path.Combine(directory, "production-no-stack-" + Array.IndexOf(StackMarkers, marker) + ".json"), negative.ToString());
                    Check("Production missing stack stays unavailable: " + marker, missing?.Value<string>("callstackStatus") == "CALLSTACK_NOT_RECORDED", negative.ToString());
                }
                var positive = await Read(on, true); var sample = Sample(positive, on);
                File.WriteAllText(Path.Combine(directory, "production-with-stack-" + Array.IndexOf(StackMarkers, marker) + ".json"), positive.ToString());
                Check("Production physical stack matches capture: " + marker, sample?.Value<string>("callstackStatus") == "recorded" && sample["callstack"].Value<int>("recordedDepth") == on.Value<int>("recordedDepth"), positive.ToString());
                Check("Production stack includes owned method: " + marker, sample?["callstack"]?["entries"] is JArray entries && entries.Any(row => row.Value<string>("method")?.Contains(nameof(AllocateKnownBytes)) == true), positive.ToString());
                Check("Exact selected sample spends one budget: " + marker, positive["data"]?.Value<int>("requestedCallstacks") == 1 && positive["data"]?["unobservedCallstackSampleIds"] is JArray ids && ids.Count == 0, positive.ToString());
                var defaultRequest = Request(on, true); defaultRequest.Remove("CallstackMarkers");
                var defaultResponse = JObject.FromObject(await RuntimeProfiler.HandleCommand(defaultRequest.ToObject<RuntimeProfilerParams>()));
                var defaultSample = Sample(defaultResponse, on);
                Check("Default remains GC-only: " + marker, marker == "GC.Alloc" ? defaultSample?.Value<string>("callstackStatus") == "recorded" : defaultSample?.Value<string>("callstackStatus") == "MARKER_NOT_SELECTED" && defaultResponse["data"]?.Value<int>("requestedCallstacks") == 0, defaultResponse.ToString());
            }
            var wait = (JObject)withoutStack["WaitForJobGroupID"];
            Check("Native actual Wait physical sample has no captured stack", wait.Value<int>("recordedDepth") == 0, wait.ToString());
            var waitResponse = await Read(wait, true); var waitSample = Sample(waitResponse, wait);
            File.WriteAllText(Path.Combine(directory, "production-wait-no-stack.json"), waitResponse.ToString());
            Check("Wait physical sample never borrows JobHandle.Complete stack", waitSample?.Value<string>("callstackStatus") == "CALLSTACK_NOT_RECORDED" && waitSample?["callstack"]?.Value<int>("recordedDepth") == 0 && waitResponse["data"]?.Value<int>("requestedCallstacks") == 1, waitResponse.ToString());
            Check("Production reads retain capture flags", !ProfilerDriver.enabled && ProfilerDriver.memoryRecordMode == (ProfilerMemoryRecordMode.GCAlloc | ProfilerMemoryRecordMode.UnsafeUtilityMalloc | ProfilerMemoryRecordMode.JobHandleComplete | ProfilerMemoryRecordMode.NativeAlloc), "Read changed record mode");
        }
        catch (Exception ex) { failure = ex; }
        finally { Finish(failure); }
    }

    static void Check(string name, bool passed, string detail) => cases.Add(new { name, passed, detail = passed ? null : detail });

    [MenuItem("Tools/UniBridge Validation/Remaining Profiler 4/RestoreAfterWire")]
    public static void RestoreAfterWire()
    {
        if (!restorePending) throw new InvalidOperationException("No successful callstack wire capture is awaiting restoration.");
        Exception failure = null;
        try { Restore(); } catch (Exception error) { failure = error; }
        bool restored = failure == null && ProfilerDriver.enabled == previousEnabled &&
            ProfilerDriver.profileEditor == previousEditor && Profiler.enableAllocationCallstacks == previousCallstacks && ProfilerDriver.memoryRecordMode == previousRecordMode && restoredBaseline;
        restorePending = !restored;
        var receipt = new JObject { ["passed"] = restored, ["restorePending"] = restorePending,
            ["restoredBaseline"] = restoredBaseline, ["restoredEnabled"] = ProfilerDriver.enabled,
            ["restoredEditor"] = ProfilerDriver.profileEditor, ["restoredCallstacks"] = Profiler.enableAllocationCallstacks,
            ["restoredRecordMode"] = ProfilerDriver.memoryRecordMode.ToString(),
            ["error"] = failure?.ToString() };
        File.WriteAllText(Path.Combine(directory, "restoration-receipt.json"), receipt.ToString());
        string resultPath = Path.Combine(directory, "result.json");
        var result = JObject.Parse(File.ReadAllText(resultPath));
        result["restorationReceipt"] = receipt; result["restorePending"] = restorePending;
        result["restoredBaseline"] = restoredBaseline; result["restoredEnabled"] = ProfilerDriver.enabled;
        result["restoredEditor"] = ProfilerDriver.profileEditor; result["restoredCallstacks"] = Profiler.enableAllocationCallstacks;
        result["restoredRecordMode"] = ProfilerDriver.memoryRecordMode.ToString();
        if (!restored) result["passed"] = false;
        File.WriteAllText(resultPath, result.ToString());
        if (!restored) throw new IOException("Callstack wire capture baseline restoration failed; evidence retained.", failure);
    }

    static void Restore()
    {
        EditorApplication.update -= Tick;
        try
        {
            ProfilerDriver.enabled = false;
            if (baselineSaved)
            {
                restoredBaseline = ProfilerDriver.LoadProfile(Path.Combine(directory, "baseline.data"), false);
                if (!restoredBaseline) throw new IOException("Profiler capture restore failed; saved baseline.data is retained.");
            }
            else if (captureStarted && !hadRecordedFrames) { ProfilerDriver.ClearAllFrames(); restoredBaseline = true; }
        }
        finally
        {
            Profiler.enableAllocationCallstacks = previousCallstacks;
            ProfilerDriver.memoryRecordMode = previousRecordMode;
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
                try { Restore(); } catch (Exception ex) { cases.Add(new { name = "Baseline restoration", passed = false, detail = ex.ToString() }); }
            }
            bool passed = cases.All(item => (bool)item.GetType().GetProperty("passed").GetValue(item));
            var wireRequests = new JObject();
            if (withStack != null && withoutStack != null)
                foreach (string marker in StackMarkers)
                {
                    wireRequests[marker + " recorded"] = Request((JObject)withStack[marker], true);
                    if (withoutStack[marker] is JObject missing) wireRequests[marker + " unrecorded"] = Request(missing, true);
                }
            if (withoutStack?["WaitForJobGroupID"] is JObject wait) wireRequests["WaitForJobGroupID unrecorded"] = Request(wait, true);
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonConvert.SerializeObject(new { passed, unityVersion = Application.unityVersion,
                processBits = IntPtr.Size * 8, previousEnabled, previousEditor, previousCallstacks, hadRecordedFrames, baselineSaved,
                originalFirstFrame, originalLastFrame, restoredBaseline, restoredEnabled = ProfilerDriver.enabled,
                restoredEditor = ProfilerDriver.profileEditor, restoredCallstacks = Profiler.enableAllocationCallstacks,
                restorePending, previousRecordMode = previousRecordMode.ToString(), restoredRecordMode = ProfilerDriver.memoryRecordMode.ToString(), stableFrameIndex = withStack?["GC.Alloc"]?.Value<int>("frameIndex"),
                stableThreadId = withStack?["GC.Alloc"]?.Value<string>("threadId"), observations = withStack, exactAllocationSampleId = withStack?["GC.Alloc"]?.Value<string>("sampleId"),
                expectedAllocationDepth = withStack?["GC.Alloc"]?.Value<int>("recordedDepth"), expectedAllocationMethod = nameof(AllocateKnownBytes), unrecordedCallstackMarkers = new[] { "GC.Alloc", "WaitForJobGroupID" }, recordedCallstackMarkers = StackMarkers,
                missingStackScope = "GC.Alloc from disabled capture and exact WaitForJobGroupID from enabled capture; distinct JobHandle.Complete stack is never substituted.",
                wireRequests, restoreAfterWireMenu = "Tools/UniBridge Validation/Remaining Profiler 4/RestoreAfterWire", cases }, Formatting.Indented));
            Debug.Log("UniBridge RemainingProfiler4 fixture " + (passed ? "PASS" : "FAIL") + ": " + directory);
        }
        catch (Exception ex)
        {
            try { Restore(); restorePending = false; } catch (Exception restoreError) { Debug.LogError(restoreError); }
            Debug.LogError("Owned callstack restoration/report failed: " + ex);
        }
    }
}
#endif
