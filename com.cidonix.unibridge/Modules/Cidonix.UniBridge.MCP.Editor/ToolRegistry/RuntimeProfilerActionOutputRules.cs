using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    // Source-authored business response families. Transport/project/projection
    // branches are composed by the registry, outside these business schemas.
    internal static class RuntimeProfilerActionOutputRules
    {
        static JObject T(string type, bool nullable = false) => new JObject
        { ["type"] = nullable ? new JArray(type, "null") : (JToken)new JValue(type) };
        static JObject S(bool nullable = false) => T("string", nullable);
        static JObject I(bool nullable = false) => T("integer", nullable);
        static JObject B() => T("boolean");
        // Newtonsoft's default FloatFormatHandling.String represents only these
        // nonfinite IEEE values as strings; ordinary finite values remain numbers.
        static JObject N(bool nullable = false)
        {
            var values = new JArray(T("number"), new JObject { ["enum"] = new JArray("NaN", "Infinity", "-Infinity") });
            if (nullable) values.Add(T("null"));
            return new JObject { ["anyOf"] = values };
        }
        static JObject C(object value) => new JObject { ["const"] = value == null ? JValue.CreateNull() : JToken.FromObject(value) };
        static JObject O(params (string Name, JObject Schema)[] fields)
        {
            var properties = new JObject(); var required = new JArray();
            foreach (var field in fields) { properties[field.Name] = field.Schema; required.Add(field.Name); }
            return new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
        }
        static JObject A(JObject item, bool nullable = false) => new JObject
        { ["type"] = nullable ? new JArray("array", "null") : (JToken)new JValue("array"), ["items"] = item };
        static JObject Nullable(JObject schema) { schema["type"] = new JArray("object", "null"); return schema; }
        static JObject Strings(bool nullable = false) => A(S(true), nullable);
        static JObject Integers() => A(I());
        static JObject Success(JObject data) => O(("success", C(true)), ("message", S()), ("data", data));
        static JObject Error(string code, JObject data) => O(("success", C(false)), ("code", C(code)), ("error", C(code)), ("data", data));
        static JObject ExceptionResponse() => O(("success", C(false)), ("code", S()), ("error", S()));
        static JObject Family(JObject success, params JObject[] errors)
        {
            var branches = new JArray(success);
            foreach (var error in errors) branches.Add(error);
            branches.Add(ExceptionResponse());
            return new JObject { ["type"] = "object", ["anyOf"] = branches };
        }

        internal static Dictionary<string, JObject> Build()
        {
            var play = O(("isPlaying", B()), ("isPaused", B()), ("hint", S()));
            var recordedErrors = new List<JObject> { Error("PLAY_MODE_REQUIRED", O(("hint", S()))) };
            foreach (var code in new[] { "INVALID_RECORDED_THREAD_ID", "NO_RECORDED_PROFILER_FRAMES", "RECORDED_FRAME_OUT_OF_RANGE", "NO_RECORDED_THREAD_MATCH", "RECORDED_PROFILER_DATA_UNAVAILABLE" })
                recordedErrors.Add(Error(code, RecordedData("unavailable", false, code, true)));
            recordedErrors.Add(Error("RECORDED_PROFILER_DATA_PARTIAL", RecordedData("partial", false, null, false)));
            return new Dictionary<string, JObject>(StringComparer.Ordinal)
            {
                ["Snapshot"] = Family(Success(SnapshotData())),
                ["Metrics"] = Family(Success(MetricsData())),
                ["Sample"] = Family(Success(SampleData()), Error("PLAY_MODE_REQUIRED", (JObject)play.DeepClone())),
                ["Hierarchy"] = Family(Success(HierarchyData()), Error("PLAY_MODE_REQUIRED", play),
                    Error("NO_PROFILER_MARKERS_SELECTED", O(("available", I()), ("filtered", I()), ("includeCounters", B()),
                        ("categories", Strings()), ("markerFilters", Strings(true)), ("excludeMarkerFilters", Strings(true)), ("hint", S())))),
                ["RecordedFrames"] = Family(Success(RecordedData("completed", true, null, false)), recordedErrors.ToArray())
            };
        }

        static JObject MetricsData() => O(("defaults", Strings()),
            ("metrics", A(O(("key", S()), ("category", S()), ("profilerName", S()), ("unit", S()), ("scale", N()), ("aliases", Strings())))),
            ("customMetricSyntax", S()));
        static JObject EditorState() => O(("unityVersion", S()), ("isPlaying", B()), ("isPaused", B()),
            ("isPlayingOrWillChangePlaymode", B()), ("isCompiling", B()), ("isUpdating", B()),
            ("frameCount", I()), ("renderedFrameCount", I()), ("realtimeSinceStartup", N()), ("time", N()),
            ("deltaTime", N()), ("fixedDeltaTime", N()), ("timeScale", N()), ("editorTimeSinceStartup", N()));
        static JObject ObjectCounts()
        {
            var names = new[] { "gameObjects", "activeGameObjects", "inactiveGameObjects", "missingScripts", "monoBehaviours", "enabledMonoBehaviours", "renderers", "cameras", "lights", "colliders3D", "colliders2D", "rigidbodies3D", "rigidbodies2D", "particleSystems", "audioSources", "canvases", "eventSystems" };
            var fields = new (string, JObject)[names.Length]; for (int i = 0; i < names.Length; i++) fields[i] = (names[i], I());
            return O(fields);
        }
        static JObject Scenes() => Nullable(O(("loadedSceneCount", I()), ("activeScene", O(("name", S(true)), ("path", S(true)))),
            ("totals", ObjectCounts()), ("behaviourTypeCounts", A(O(("type", S()), ("count", I())), true)),
            ("scenes", A(O(("name", S(true)), ("path", S(true)), ("isActive", B()), ("rootCount", I()), ("objectCounts", ObjectCounts()))))));
        static JObject Memory() => Nullable(O(("monoUsedBytes", I()), ("monoHeapBytes", I()), ("totalAllocatedBytes", I()), ("totalReservedBytes", I()), ("totalUnusedReservedBytes", I())));
        static JObject SnapshotData() => O(("schema", C("unibridge.runtimeProfiler.snapshot.v1")), ("capturedUtc", S()),
            ("editor", EditorState()), ("scenes", Scenes()), ("memory", Memory()), ("supportedMetrics", MetricsData()));
        static JObject MetricSummary() => O(("key", S()), ("category", S()), ("profilerName", S()), ("unit", S()),
            ("available", B()), ("samples", I()), ("avg", N(true)), ("p50", N(true)), ("p95", N(true)), ("max", N(true)), ("last", N(true)));
        static JObject SampleRow() => O(("index", I()), ("elapsedMs", N()), ("editorTime", N()), ("unityFrame", I()), ("renderedFrame", I()),
            ("metrics", new JObject { ["type"] = "object", ["additionalProperties"] = N() }));
        static JObject SampleData() => O(("schema", C("unibridge.runtimeProfiler.sample.summary.v1")), ("name", S()),
            ("requestedFrames", I()), ("sampleRows", I()), ("timedOut", B()), ("elapsedMs", N()), ("savedPath", S(true)),
            ("editor", EditorState()), ("sceneSummary", Scenes()), ("memory", Memory()), ("metrics", A(MetricSummary())),
            ("unavailableMetrics", A(O(("key", S()), ("category", S()), ("profilerName", S()), ("reason", S())))),
            ("spikes", A(O(("metric", S()), ("threshold", N()), ("value", N()), ("sampleIndex", I()), ("unityFrame", I()), ("renderedFrame", I()), ("elapsedMs", N())))),
            ("samples", A(SampleRow(), true)));

        static JObject Marker(bool compact)
        {
            if (compact) return O(("category", S(true)), ("name", S(true)), ("path", S()), ("unit", S(true)), ("total", N()), ("max", N()), ("totalTimeMs", N(true)), ("maxTimeMs", N(true)));
            return O(("category", S(true)), ("name", S(true)), ("path", S()), ("depth", I()), ("unit", S(true)), ("unitType", S(true)), ("flags", S(true)),
                ("isCounter", B()), ("samples", I()), ("nonZeroSamples", I()), ("total", N()), ("avg", N()), ("max", N()), ("last", N()),
                ("totalTimeMs", N(true)), ("avgTimeMs", N(true)), ("maxTimeMs", N(true)), ("lastTimeMs", N(true)));
        }
        static JObject MarkerTree(int remaining)
        {
            var children = remaining > 1 ? A(MarkerTree(remaining - 1)) : new JObject { ["type"] = "array", ["maxItems"] = 0 };
            children["maxItems"] = remaining > 1 ? 20 : 0;
            return O(("name", S()), ("path", S()), ("depth", I()), ("markerCount", I()), ("totalTimeMs", N()), ("maxTimeMs", N()), ("children", children));
        }
        static JObject HierarchyData() => O(("schema", C("unibridge.runtimeProfiler.hierarchy.summary.v1")), ("name", S()),
            ("dataSource", C("ProfilerRecorderMarkers")), ("note", S()), ("requestedFrames", I()), ("sampledFrames", I()),
            ("timedOut", B()), ("elapsedMs", N()), ("savedPath", S(true)), ("editor", EditorState()),
            ("selection", O(("available", I()), ("filtered", I()), ("selected", I()), ("recorderAvailable", I()), ("recorderUnavailable", I()),
                ("includeCounters", B()), ("categories", Strings()), ("markerFilters", Strings(true)), ("excludeMarkerFilters", Strings(true)),
                ("maxMarkers", I()), ("maxDepth", I()), ("minTimeMs", N()))),
            ("summary", O(("markerCount", I()), ("timeMarkerCount", I()), ("counterMarkerCount", I()), ("totalTimeMs", N()), ("maxMarkerTimeMs", N()), ("topMarker", Nullable(Marker(true))))),
            ("categorySummary", A(O(("category", S()), ("markerCount", I()), ("timeMarkerCount", I()), ("totalTimeMs", N()), ("maxMarkerTimeMs", N())))),
            ("topMarkers", A(Marker(false))), ("hierarchy", A(MarkerTree(12))),
            ("unavailableMarkers", A(O(("category", S(true)), ("marker", S(true)), ("unit", S(true)), ("flags", S(true)), ("reason", S())))),
            ("allMarkers", A(Marker(false), true)));

        static JObject RecordedSample() => O(("sampleId", S()), ("sampleIndex", I()), ("parentSampleId", S(true)), ("parentSampleIndex", I(true)),
            ("depth", I()), ("name", S(true)), ("markerId", I()), ("category", S(true)), ("categoryId", I()), ("flags", S(true)),
            ("startTimeNs", S()), ("durationNs", S()), ("totalTimeMs", N()), ("selfTimeMs", N(true)), ("selfTimeComplete", B()),
            ("directChildren", Integers()), ("isGcAllocation", B()), ("gcAllocationBytes", I(true)), ("gcMetadataStatus", S(true)),
            ("topologyComplete", B()), ("issues", Strings()));
        static JObject RecordedTree(int remaining)
        {
            var children = remaining > 1 ? A(RecordedTree(remaining - 1)) : new JObject { ["type"] = "array", ["maxItems"] = 0 };
            return O(("sample", RecordedSample()), ("selected", B()), ("depthTruncated", B()), ("children", children));
        }
        static JObject RecordedThread() => O(("threadIndex", I()), ("threadId", S()), ("threadName", S(true)), ("threadGroupName", S(true)),
            ("declaredRawSampleCount", I()), ("copiedRawSampleCount", I()), ("rawCoverageComplete", B()), ("topologyComplete", B()),
            ("gcMarkerAvailable", B()), ("knownGcAllocationBytes", I()), ("gcAllocationBytes", I(true)), ("gcAllocationEvents", I()),
            ("unknownGcAllocationEvents", I()), ("gcSumOverflow", B()), ("issues", Strings()), ("topSamples", A(RecordedSample())),
            ("hierarchy", A(RecordedTree(12))), ("samples", A(RecordedSample(), true)));
        static JObject RecordedFrame() => O(("frameIndex", I()), ("frameDurationMs", N(true)), ("availableThreadCount", I()),
            ("examinedThreadViews", I()), ("selectedThreadCount", I()), ("knownSelectedThreadGcAllocationBytes", I()),
            ("selectedThreadGcAllocationBytes", I(true)), ("gcScope", S()), ("issues", Strings()), ("threads", A(RecordedThread())));
        static JObject RecordedData(string outcome, bool complete, string errorCode, bool hint) => O(
            ("schema", C("unibridge.runtimeProfiler.recordedFrames.v1")), ("dataSource", C("ProfilerDriver.RawFrameDataView")),
            ("outcome", C(outcome)), ("complete", C(complete)),
            ("available", O(("firstFrameIndex", I()), ("lastFrameIndex", I()))),
            ("request", O(("FrameIndex", I(true)), ("frameCount", I()), ("ThreadIds", Strings(true)), ("ThreadNameFilters", Strings(true)),
                ("maxThreads", I()), ("maxRawSamples", I()), ("maxTopSamples", I()), ("maxHierarchyDepth", I()), ("includeMetadata", B()), ("sortBy", S()))),
            ("selectedFrameIndices", Integers()), ("readFrameIndices", Integers()), ("copiedSamples", I()), ("timedOut", B()),
            ("elapsedMs", N()), ("issues", Strings()), ("errorCode", C(errorCode)), ("selfTimeMethod", S()), ("gcScope", S()),
            ("savedPath", S(true)), ("hint", hint ? S() : C(null)), ("frames", A(RecordedFrame())));
    }
}
