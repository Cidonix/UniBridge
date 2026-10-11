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
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

public static class UniBridgeRecordedObjectMetadataFixture
{
    const string MarkerName = "UniBridge Fixture CapturedObject 20261011";
    const string NoObjectMarker = "UniBridge Fixture NoObject 20261011";
    static GameObject ownedGameObject;
    static ulong originalObjectId;
    static readonly ProfilerMarker Marker = new(MarkerName);
    static bool running, previousEnabled, previousEditor, previousCallstacks, hadRecordedFrames, baselineSaved, captureStarted, restoredBaseline;
    static bool holdForWire, restorePending, capturedSnapshotSaved, capturedSnapshotLoaded;
    static ProfilerMemoryRecordMode previousRecordMode;
    static int originalFirstFrame, originalLastFrame, startFrame, ticks, phase;
    static double deadline;
    static string directory;
    static JObject withoutStack, withStack;
    static readonly List<object> cases = new();

    [MenuItem("Tools/UniBridge Validation/Remaining Profiler 5/GenerateKnownCapture")]
    public static void GenerateKnownCapture() => StartCapture(false);

    [MenuItem("Tools/UniBridge Validation/Remaining Profiler 5/GenerateKnownCaptureForWire")]
    public static void GenerateKnownCaptureForWire() => StartCapture(true);

    static void StartCapture(bool wire)
    {
        if (running || restorePending) throw new InvalidOperationException("Owned callstack fixture is running or awaiting baseline restoration.");
        holdForWire = wire; restorePending = capturedSnapshotSaved = capturedSnapshotLoaded = false; withoutStack = withStack = null;
        directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "AgentValidation", "RemainingProfiler5-" + Guid.NewGuid().ToString("N"));
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
            ownedGameObject = new GameObject("UniBridge Captured Original 20261011") { hideFlags = HideFlags.HideAndDontSave };
            originalObjectId = EntityId.ToULong(ownedGameObject.transform.GetEntityId());
            ProfilerDriver.profileEditor = true;
            // Object metadata capture does not change callstack collection flags.
            startFrame = ProfilerDriver.lastFrameIndex;
            ProfilerDriver.enabled = true;
            captureStarted = true;
            EditorApplication.update += Tick;
            Debug.Log("UniBridge RemainingProfiler5 fixture started: " + directory);
        }
        catch (Exception ex) { Finish(ex); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RecordKnownObject()
    {
        Profiler.BeginSample(MarkerName, ownedGameObject.transform);
        try { System.Threading.Thread.SpinWait(1000); } finally { Profiler.EndSample(); }
        Profiler.BeginSample(NoObjectMarker);
        try { System.Threading.Thread.SpinWait(1000); } finally { Profiler.EndSample(); }
    }

    static void Tick()
    {
        try
        {
            RecordKnownObject();
            if (EditorApplication.timeSinceStartup >= deadline) throw new TimeoutException("Owned native object metadata capture deadline elapsed.");
            if (++ticks < 16) return;
            ProfilerDriver.enabled = false;
            withStack = FindOwnedSample(MarkerName, true);
            File.WriteAllText(Path.Combine(directory, "independent-recorded-object.json"), withStack.ToString());
            withoutStack = FindOwnedSample(NoObjectMarker, false, withStack.Value<int>("frameIndex"));
            File.WriteAllText(Path.Combine(directory, "independent-no-object.json"), withoutStack.ToString());
            // Object names are capture-table metadata; freeze the owned snapshot before later object changes.
            string capturedPath = Path.Combine(directory, "captured.data");
            capturedSnapshotSaved = ProfilerDriver.SaveProfile(capturedPath) && File.Exists(capturedPath);
            if (!capturedSnapshotSaved) throw new IOException("Owned capture snapshot could not be frozen before rename/destruction.");
            ownedGameObject.name = "UniBridge Renamed AfterCapture 20261011";
            UnityEngine.Object.DestroyImmediate(ownedGameObject); ownedGameObject = null;
            capturedSnapshotLoaded = ProfilerDriver.LoadProfile(capturedPath, false);
            if (!capturedSnapshotLoaded) throw new IOException("Frozen owned capture snapshot could not be loaded for historical verification.");
            var frozenObject = FindOwnedSample(MarkerName, true, withStack.Value<int>("frameIndex"));
            var frozenMissing = FindOwnedSample(NoObjectMarker, false, withoutStack.Value<int>("frameIndex"));
            File.WriteAllText(Path.Combine(directory, "independent-frozen-object.json"), frozenObject.ToString());
            File.WriteAllText(Path.Combine(directory, "independent-frozen-no-object.json"), frozenMissing.ToString());
            Check("Frozen profile retains exact physical positive and negative IDs", frozenObject.Value<string>("sampleId") == withStack.Value<string>("sampleId") && frozenObject.Value<string>("objectId") == withStack.Value<string>("objectId") && frozenMissing.Value<string>("sampleId") == withoutStack.Value<string>("sampleId"), frozenObject.ToString());
            Check("Frozen capture retains original historical object table names", frozenObject.Value<string>("objectName") == withStack.Value<string>("objectName") && frozenObject.Value<string>("relatedGameObjectId") == withStack.Value<string>("relatedGameObjectId") && frozenObject.Value<string>("relatedGameObjectName") == "UniBridge Captured Original 20261011", frozenObject.ToString());
            EditorApplication.update -= Tick;
            _ = Verify();
        }
        catch (Exception ex) { Finish(ex); }
    }

    static JObject FindOwnedSample(string markerName, bool requireObject, int? exactFrame = null)
    {
        int first = ProfilerDriver.firstFrameIndex, cursor = exactFrame ?? ProfilerDriver.lastFrameIndex, examined = 0;
        var diagnosticSamples = new JArray(); var diagnosticFrames = new JArray();
        while (cursor >= first && cursor > startFrame && examined++ < 80)
        {
            using (var raw = ProfilerDriver.GetRawFrameDataView(cursor, 0))
            {
                if (raw.valid)
                {
                    int marker = raw.GetMarkerId(markerName);
                    if (diagnosticFrames.Count < 8)
                        diagnosticFrames.Add(new JObject { ["frameIndex"] = cursor, ["threadId"] = raw.threadId.ToString(CultureInfo.InvariantCulture),
                            ["markerId"] = marker, ["sampleCount"] = raw.sampleCount, ["originalObjectId"] = originalObjectId.ToString(CultureInfo.InvariantCulture) });
                    for (int i = 0; marker != FrameDataView.invalidMarkerId && i < Math.Min(raw.sampleCount, 100000); i++)
                    {
                        if (raw.GetSampleMarkerId(i) != marker) continue;
                        var id = raw.GetSampleEntityId(i);
                        if (diagnosticSamples.Count < 8)
                        {
                            var diagnostic = new JObject { ["frameIndex"] = cursor, ["threadId"] = raw.threadId.ToString(CultureInfo.InvariantCulture),
                                ["sampleIndex"] = i, ["sampleId"] = cursor.ToString(CultureInfo.InvariantCulture) + ":" + raw.threadId.ToString(CultureInfo.InvariantCulture) + ":" + i.ToString(CultureInfo.InvariantCulture),
                                ["marker"] = markerName, ["rawObjectId"] = EntityId.ToULong(id).ToString(CultureInfo.InvariantCulture), ["isValid"] = id.IsValid() };
                            try
                            {
                                bool hasInfo = raw.GetUnityObjectInfo(id, out var capturedInfo); diagnostic["hasObjectInfo"] = hasInfo;
                                if (hasInfo)
                                {
                                    diagnostic["objectName"] = DiagnosticText(capturedInfo.name);
                                    var relatedId = capturedInfo.relatedGameObjectEntityId;
                                    diagnostic["relatedRawId"] = EntityId.ToULong(relatedId).ToString(CultureInfo.InvariantCulture);
                                    diagnostic["relatedIsValid"] = relatedId.IsValid();
                                    bool hasRelated = relatedId.IsValid() && raw.GetUnityObjectInfo(relatedId, out var relatedInfo);
                                    diagnostic["hasRelatedInfo"] = hasRelated;
                                    if (hasRelated && raw.GetUnityObjectInfo(relatedId, out var namedRelated)) diagnostic["relatedName"] = DiagnosticText(namedRelated.name);
                                }
                            }
                            catch (Exception error) { diagnostic["infoError"] = DiagnosticText(error.ToString()); }
                            diagnosticSamples.Add(diagnostic);
                        }
                        if (requireObject && (!id.IsValid() || EntityId.ToULong(id) != originalObjectId) || !requireObject && id.IsValid()) continue;
                        var observation = new JObject { ["frameIndex"] = cursor, ["threadId"] = raw.threadId.ToString(CultureInfo.InvariantCulture), ["sampleIndex"] = i,
                            ["sampleId"] = cursor.ToString(CultureInfo.InvariantCulture) + ":" + raw.threadId.ToString(CultureInfo.InvariantCulture) + ":" + i.ToString(CultureInfo.InvariantCulture),
                            ["objectId"] = id.IsValid() ? EntityId.ToULong(id).ToString(CultureInfo.InvariantCulture) : null, ["marker"] = markerName, ["markerId"] = marker };
                        if (requireObject)
                        {
                            if (!raw.GetUnityObjectInfo(id, out var info)) continue;
                            var related = info.relatedGameObjectEntityId;
                            if (!related.IsValid() || !raw.GetUnityObjectInfo(related, out var gameObjectInfo)) continue;
                            observation["objectName"] = info.name;
                            observation["relatedGameObjectId"] = EntityId.ToULong(related).ToString(CultureInfo.InvariantCulture);
                            observation["relatedGameObjectName"] = gameObjectInfo.name;
                        }
                        return observation;
                    }
                }
            }
            if (exactFrame.HasValue) break;
            int previous = ProfilerDriver.GetPreviousFrameIndex(cursor);
            if (previous < 0 || previous >= cursor) break;
            cursor = previous;
        }
        File.WriteAllText(Path.Combine(directory, requireObject ? "independent-object-diagnostic.json" : "independent-no-object-diagnostic.json"),
            new JObject { ["marker"] = markerName, ["requireObject"] = requireObject, ["exactFrame"] = exactFrame,
                ["firstFrame"] = first, ["lastFrame"] = ProfilerDriver.lastFrameIndex, ["afterFrame"] = startFrame,
                ["memoryRecordMode"] = ProfilerDriver.memoryRecordMode.ToString(), ["frames"] = diagnosticFrames, ["samples"] = diagnosticSamples }.ToString());
        throw new InvalidOperationException("Owned captured " + (requireObject ? "object + related GameObject metadata" : "sample without object ID") + " was not recorded; no native positive evidence was fabricated.");
    }

    static string DiagnosticText(string value) => value == null || value.Length <= 512 ? value : value.Substring(0, 512);

    static JObject Request(JObject observation, bool include)
    {
        var request = new JObject { ["Action"] = "RecordedFrames", ["FrameIndex"] = observation["frameIndex"], ["FrameCount"] = 1,
            ["ThreadIds"] = new JArray(observation["threadId"]), ["MaxThreads"] = 1, ["MaxRawSamples"] = 100000,
            ["IncludeObjectMetadata"] = include, ["MaxObjectMetadataSamples"] = 1,
            ["MarkerFilters"] = new JArray(observation["marker"]), ["ReturnSamples"] = true, ["SaveToFile"] = false };
        if (include) request["ObjectMetadataSampleIds"] = new JArray(observation["sampleId"]);
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
            Check("Independent capture has owned object + related GameObject", withStack.Value<string>("objectId") == originalObjectId.ToString(CultureInfo.InvariantCulture) && withStack.Value<string>("relatedGameObjectName") == "UniBridge Captured Original 20261011", withStack.ToString());
            Check("Owned live object was destroyed before production read", ownedGameObject == null, "Owned object remains live");
            var positive = await Read(withStack, true); var sample = Sample(positive, withStack); var metadata = sample?["objectMetadata"];
            File.WriteAllText(Path.Combine(directory, "production-recorded-object.json"), positive.ToString());
            Check("Production preserves exact capture object ID", metadata?.Value<string>("status") == "recorded" && metadata.Value<string>("objectId") == withStack.Value<string>("objectId"), positive.ToString());
            Check("Production returns historical object name after rename + destroy", metadata?.Value<string>("objectName") == withStack.Value<string>("objectName") && metadata?.Value<string>("relatedGameObjectName") == "UniBridge Captured Original 20261011", positive.ToString());
            Check("Production preserves capture related GameObject", metadata?.Value<string>("relatedGameObjectStatus") == "recorded" && metadata.Value<string>("relatedGameObjectId") == withStack.Value<string>("relatedGameObjectId"), positive.ToString());
            Check("Exact metadata selector spends one physical query budget", positive["data"]?.Value<int>("requestedObjectMetadata") == 1 && positive["data"]?["unobservedObjectMetadataSampleIds"] is JArray ids && ids.Count == 0, positive.ToString());
            var negative = await Read(withoutStack, true); var missing = Sample(negative, withoutStack)?["objectMetadata"];
            File.WriteAllText(Path.Combine(directory, "production-no-object.json"), negative.ToString());
            Check("Production missing ID stays explicitly unavailable", missing?.Value<string>("status") == "OBJECT_ID_NOT_RECORDED" && missing["objectId"].Type == JTokenType.Null && missing["objectName"].Type == JTokenType.Null, negative.ToString());
            var disabled = await Read(withStack, false); var disabledMetadata = Sample(disabled, withStack)?["objectMetadata"];
            File.WriteAllText(Path.Combine(directory, "production-disabled.json"), disabled.ToString());
            Check("Default metadata read remains disabled with zero queries", disabledMetadata?.Value<string>("status") == "DISABLED" && disabled["data"]?.Value<int>("requestedObjectMetadata") == 0, disabled.ToString());
            Check("Production metadata reads retain recording state", !ProfilerDriver.enabled && Profiler.enableAllocationCallstacks == previousCallstacks && ProfilerDriver.memoryRecordMode == previousRecordMode, "Read changed capture settings");
        }
        catch (Exception ex) { failure = ex; }
        finally { Finish(failure); }
    }

    static void Check(string name, bool passed, string detail) => cases.Add(new { name, passed, detail = passed ? null : detail });

    [MenuItem("Tools/UniBridge Validation/Remaining Profiler 5/RestoreAfterWire")]
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
        if (ownedGameObject != null) UnityEngine.Object.DestroyImmediate(ownedGameObject);
        ownedGameObject = null;
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
            var wireRequests = withStack == null || withoutStack == null ? null : new JObject
            {
                ["firstPass"] = Request(withStack, false),
                ["exactObjectMetadataDrilldown"] = Request(withStack, true),
                ["withoutRecordedObjectId"] = Request(withoutStack, true)
            };
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonConvert.SerializeObject(new { passed, unityVersion = Application.unityVersion,
                processBits = IntPtr.Size * 8, previousEnabled, previousEditor, previousCallstacks, hadRecordedFrames, baselineSaved,
                originalFirstFrame, originalLastFrame, restoredBaseline, restoredEnabled = ProfilerDriver.enabled,
                restoredEditor = ProfilerDriver.profileEditor, restoredCallstacks = Profiler.enableAllocationCallstacks,
                restorePending, previousRecordMode = previousRecordMode.ToString(), restoredRecordMode = ProfilerDriver.memoryRecordMode.ToString(), stableFrameIndex = withStack?.Value<int>("frameIndex"),
                stableThreadId = withStack?.Value<string>("threadId"), exactObjectSampleId = withStack?.Value<string>("sampleId"),
                expectedObject = withStack, expectedMissingObject = withoutStack, capturedSnapshotSaved, capturedSnapshotLoaded,
                capturedSnapshotPath = Path.Combine(directory, "captured.data"),
                wireRequests, restoreAfterWireMenu = "Tools/UniBridge Validation/Remaining Profiler 5/RestoreAfterWire", cases }, Formatting.Indented));
            Debug.Log("UniBridge RemainingProfiler5 fixture " + (passed ? "PASS" : "FAIL") + ": " + directory);
        }
        catch (Exception ex)
        {
            try { Restore(); restorePending = false; } catch (Exception restoreError) { Debug.LogError(restoreError); }
            Debug.LogError("Owned callstack restoration/report failed: " + ex);
        }
    }
}
#endif
