#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Cidonix.UniBridge.Toolkit;
using Newtonsoft.Json;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read-only runtime and profiler snapshot tool for AI Play Mode debugging.
    /// </summary>
    public static class RuntimeProfiler
    {
        const string ToolName = "UniBridge_RuntimeProfiler";
        const int DefaultSampleFrames = 120;
        const int MaxSampleFrames = 600;
        const int DefaultTimeoutMs = 30000;
        const int MaxBehaviourTypes = 100;
        const int DefaultMaxSpikes = 5;

        public const string Title = "Inspect runtime state and profiler samples";

        public const string Description = @"Inspect live Unity runtime state and capture bounded profiler samples for AI debugging.

Use this when a Play Mode issue needs data instead of guessing: frame time, GC allocation, memory, rendering counters, physics/script markers, object counts, active scenes, and spike summaries.

Actions:
    Snapshot: Return current editor/runtime state, loaded-scene object counts, memory, and supported metric hints.
    Sample: In Play Mode by default, sample selected ProfilerRecorder metrics for a bounded number of editor update ticks. Returns compact averages/p95/max/last/spikes and can save full raw samples under Library/UniBridge/RuntimeProfiler.
    Metrics: List supported metric aliases and category/name pairs.

Search aliases: UniBridge Unity runtime profiler PlayMode performance FPS GC memory spikes stutter frame time ProfilerRecorder runtime state.";

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "runtime", "diagnostics", "profiler" }, EnabledByDefault = true, ExecutionPolicy = ToolExecutionPolicy.ReadOnly)]
        public static async Task<object> HandleCommand(RuntimeProfilerParams parameters)
        {
            parameters ??= new RuntimeProfilerParams();

            try
            {
                switch (parameters.Action)
                {
                    case RuntimeProfilerAction.Metrics:
                        return Response.Success("Listed runtime profiler metrics.", BuildMetricsList(parameters));
                    case RuntimeProfilerAction.Sample:
                        return await Sample(parameters);
                    case RuntimeProfilerAction.Snapshot:
                    default:
                        return Response.Success("Built runtime profiler snapshot.", BuildSnapshot(parameters));
                }
            }
            catch (Exception ex)
            {
                return Response.Error($"Runtime profiler action '{parameters.Action}' failed: {ex.Message}");
            }
        }

        static object BuildSnapshot(RuntimeProfilerParams parameters)
        {
            return new
            {
                schema = "unibridge.runtimeProfiler.snapshot.v1",
                capturedUtc = DateTime.UtcNow.ToString("o"),
                editor = BuildEditorRuntimeState(),
                scenes = parameters.IncludeSceneSummary ?? true ? BuildSceneSummary(parameters) : null,
                memory = parameters.IncludeMemory ?? true ? BuildMemorySnapshot() : null,
                supportedMetrics = BuildMetricsList(parameters)
            };
        }

        static async Task<object> Sample(RuntimeProfilerParams parameters)
        {
            var requirePlayMode = parameters.RequirePlayMode ?? true;
            if (requirePlayMode && !Application.isPlaying)
            {
                return Response.Error("PLAY_MODE_REQUIRED", new
                {
                    isPlaying = Application.isPlaying,
                    isPaused = EditorApplication.isPaused,
                    hint = "Enter Play Mode first, or pass RequirePlayMode=false for editor-time sampling."
                });
            }

            var frames = Mathf.Clamp(parameters.SampleFrames ?? DefaultSampleFrames, 1, MaxSampleFrames);
            var timeoutMs = Mathf.Clamp(parameters.TimeoutMs ?? DefaultTimeoutMs, 1000, 300000);
            var maxSpikes = Mathf.Clamp(parameters.MaxSpikes ?? DefaultMaxSpikes, 0, 50);
            var spikeThreshold = parameters.MainThreadSpikeThresholdMs ?? 33.3;
            var metricDefinitions = ResolveMetrics(parameters.Metrics).ToArray();
            var recorders = metricDefinitions.Select(CreateRecorder).ToArray();
            var unavailable = recorders
                .Where(item => !item.Available)
                .Select(item => new
                {
                    key = item.Metric.Key,
                    category = item.Metric.CategoryName,
                    profilerName = item.Metric.ProfilerName,
                    reason = item.Error ?? "ProfilerRecorder was not valid for this metric in the current Unity session."
                })
                .ToArray();

            var sampleRows = new List<SampleRow>(frames);
            var startUtc = DateTime.UtcNow;
            var startEditorTime = EditorApplication.timeSinceStartup;
            var deadline = startEditorTime + timeoutMs / 1000.0;

            try
            {
                await EditorTask.Yield();
                while (sampleRows.Count < frames && EditorApplication.timeSinceStartup <= deadline)
                {
                    sampleRows.Add(ReadSample(sampleRows.Count, startEditorTime, recorders));
                    await EditorTask.Yield();
                }
            }
            finally
            {
                foreach (var recorder in recorders)
                    recorder.Dispose();
            }

            var elapsedMs = Math.Max(0, (EditorApplication.timeSinceStartup - startEditorTime) * 1000.0);
            var summaries = BuildMetricSummaries(metricDefinitions, sampleRows);
            var spikes = BuildSpikes(sampleRows, "main_thread_ms", spikeThreshold, maxSpikes);
            var state = BuildEditorRuntimeState();
            var sceneSummary = parameters.IncludeSceneSummary ?? true ? BuildSceneSummary(parameters) : null;
            var memory = parameters.IncludeMemory ?? true ? BuildMemorySnapshot() : null;
            var payload = new
            {
                schema = "unibridge.runtimeProfiler.sample.v1",
                name = string.IsNullOrWhiteSpace(parameters.Name) ? "runtime_sample" : parameters.Name.Trim(),
                startedUtc = startUtc.ToString("o"),
                endedUtc = DateTime.UtcNow.ToString("o"),
                requestedFrames = frames,
                sampleRows = sampleRows.Count,
                timedOut = sampleRows.Count < frames,
                elapsedMs,
                editor = state,
                scenes = sceneSummary,
                memory,
                metrics = summaries,
                unavailableMetrics = unavailable,
                spikes,
                samples = sampleRows.Select(row => row.ToDto()).ToArray()
            };

            string savedPath = null;
            if (parameters.SaveToFile ?? true)
            {
                savedPath = SavePayload(payload, parameters.Name);
            }

            var responseData = new
            {
                schema = "unibridge.runtimeProfiler.sample.summary.v1",
                name = payload.name,
                requestedFrames = frames,
                sampleRows = sampleRows.Count,
                timedOut = sampleRows.Count < frames,
                elapsedMs,
                savedPath,
                editor = state,
                sceneSummary,
                memory,
                metrics = summaries,
                unavailableMetrics = unavailable,
                spikes,
                samples = parameters.ReturnSamples == true ? sampleRows.Select(row => row.ToDto()).ToArray() : null
            };

            return Response.Success($"Captured runtime profiler sample with {sampleRows.Count} row(s).", responseData);
        }

        static object BuildMetricsList(RuntimeProfilerParams parameters)
        {
            return new
            {
                defaults = DefaultMetricKeys,
                metrics = SupportedMetrics()
                    .Select(metric => new
                    {
                        key = metric.Key,
                        category = metric.CategoryName,
                        profilerName = metric.ProfilerName,
                        unit = metric.Unit,
                        scale = metric.Scale,
                        aliases = metric.Aliases
                    })
                    .ToArray(),
                customMetricSyntax = "Use category/name, for example Internal/Main Thread, Memory/GC.Alloc, Render/Batches Count."
            };
        }

        static object BuildEditorRuntimeState()
        {
            return new
            {
                unityVersion = Application.unityVersion,
                isPlaying = Application.isPlaying,
                isPaused = EditorApplication.isPaused,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                frameCount = SafeInt(() => Time.frameCount),
                renderedFrameCount = SafeInt(() => Time.renderedFrameCount),
                realtimeSinceStartup = SafeFloat(() => Time.realtimeSinceStartup),
                time = SafeFloat(() => Time.time),
                deltaTime = SafeFloat(() => Time.deltaTime),
                fixedDeltaTime = SafeFloat(() => Time.fixedDeltaTime),
                timeScale = SafeFloat(() => Time.timeScale),
                editorTimeSinceStartup = EditorApplication.timeSinceStartup
            };
        }

        static object BuildSceneSummary(RuntimeProfilerParams parameters)
        {
            var maxBehaviourTypes = Mathf.Clamp(parameters.MaxBehaviourTypes ?? 20, 0, MaxBehaviourTypes);
            var includeBehaviourTypes = parameters.IncludeBehaviourTypeCounts ?? true;
            var loadedScenes = new List<object>();
            var totals = new SceneCounters();
            var behaviourTypes = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;

                var roots = scene.GetRootGameObjects();
                var sceneCounters = CountSceneObjects(roots, includeBehaviourTypes ? behaviourTypes : null);
                totals.Add(sceneCounters);
                loadedScenes.Add(new
                {
                    name = scene.name,
                    path = NormalizePath(scene.path),
                    isActive = scene == SceneManager.GetActiveScene(),
                    rootCount = roots.Length,
                    objectCounts = sceneCounters.ToDto()
                });
            }

            return new
            {
                loadedSceneCount = loadedScenes.Count,
                activeScene = new
                {
                    name = SceneManager.GetActiveScene().name,
                    path = NormalizePath(SceneManager.GetActiveScene().path)
                },
                totals = totals.ToDto(),
                behaviourTypeCounts = includeBehaviourTypes
                    ? behaviourTypes
                        .OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Take(maxBehaviourTypes)
                        .Select(pair => new { type = pair.Key, count = pair.Value })
                        .ToArray()
                    : null,
                scenes = loadedScenes.ToArray()
            };
        }

        static SceneCounters CountSceneObjects(GameObject[] roots, Dictionary<string, int> behaviourTypes)
        {
            var counters = new SceneCounters();
            foreach (var root in roots ?? Array.Empty<GameObject>())
            {
                CountTransformRecursive(root.transform, counters);

                var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
                counters.MonoBehaviours += behaviours.Count(item => item != null);
                counters.EnabledMonoBehaviours += behaviours.Count(item => item != null && item.enabled);
                if (behaviourTypes != null)
                {
                    foreach (var behaviour in behaviours)
                    {
                        if (behaviour == null)
                            continue;
                        var typeName = behaviour.GetType().FullName ?? behaviour.GetType().Name;
                        behaviourTypes[typeName] = behaviourTypes.TryGetValue(typeName, out var count) ? count + 1 : 1;
                    }
                }

                counters.Renderers += root.GetComponentsInChildren<Renderer>(true).Length;
                counters.Cameras += root.GetComponentsInChildren<Camera>(true).Length;
                counters.Lights += root.GetComponentsInChildren<Light>(true).Length;
                counters.Colliders3D += root.GetComponentsInChildren<Collider>(true).Length;
                counters.Colliders2D += root.GetComponentsInChildren<Collider2D>(true).Length;
                counters.Rigidbodies3D += root.GetComponentsInChildren<Rigidbody>(true).Length;
                counters.Rigidbodies2D += root.GetComponentsInChildren<Rigidbody2D>(true).Length;
                counters.ParticleSystems += root.GetComponentsInChildren<ParticleSystem>(true).Length;
                counters.AudioSources += root.GetComponentsInChildren<AudioSource>(true).Length;
                counters.Canvases += root.GetComponentsInChildren<Canvas>(true).Length;
                counters.EventSystems += root.GetComponentsInChildren<EventSystem>(true).Length;
            }

            return counters;
        }

        static void CountTransformRecursive(Transform transform, SceneCounters counters)
        {
            if (transform == null)
                return;

            counters.GameObjects++;
            if (transform.gameObject.activeInHierarchy)
                counters.ActiveGameObjects++;
            else
                counters.InactiveGameObjects++;

            counters.MissingScripts += MissingScriptCount(transform.gameObject);

            for (var i = 0; i < transform.childCount; i++)
                CountTransformRecursive(transform.GetChild(i), counters);
        }

        static int MissingScriptCount(GameObject gameObject)
        {
            try
            {
                return GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
            }
            catch
            {
                return 0;
            }
        }

        static object BuildMemorySnapshot()
        {
            return new
            {
                monoUsedBytes = SafeLong(Profiler.GetMonoUsedSizeLong),
                monoHeapBytes = SafeLong(Profiler.GetMonoHeapSizeLong),
                totalAllocatedBytes = SafeLong(Profiler.GetTotalAllocatedMemoryLong),
                totalReservedBytes = SafeLong(Profiler.GetTotalReservedMemoryLong),
                totalUnusedReservedBytes = SafeLong(Profiler.GetTotalUnusedReservedMemoryLong)
            };
        }

        static SampleRow ReadSample(int index, double startEditorTime, RecorderState[] recorders)
        {
            var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var recorder in recorders)
            {
                if (!recorder.Available)
                    continue;
                var raw = recorder.ReadRawValue();
                metrics[recorder.Metric.Key] = raw * recorder.Metric.Scale;
            }

            return new SampleRow
            {
                Index = index,
                ElapsedMs = Math.Max(0, (EditorApplication.timeSinceStartup - startEditorTime) * 1000.0),
                EditorTime = EditorApplication.timeSinceStartup,
                UnityFrame = SafeInt(() => Time.frameCount),
                RenderedFrame = SafeInt(() => Time.renderedFrameCount),
                Metrics = metrics
            };
        }

        static object[] BuildMetricSummaries(MetricDefinition[] metrics, List<SampleRow> rows)
        {
            return metrics.Select(metric =>
            {
                var values = rows
                    .Where(row => row.Metrics.ContainsKey(metric.Key))
                    .Select(row => row.Metrics[metric.Key])
                    .ToArray();

                return new
                {
                    key = metric.Key,
                    category = metric.CategoryName,
                    profilerName = metric.ProfilerName,
                    unit = metric.Unit,
                    available = values.Length > 0,
                    samples = values.Length,
                    avg = values.Length > 0 ? values.Average() : (double?)null,
                    p50 = values.Length > 0 ? Percentile(values, 0.50) : (double?)null,
                    p95 = values.Length > 0 ? Percentile(values, 0.95) : (double?)null,
                    max = values.Length > 0 ? values.Max() : (double?)null,
                    last = values.Length > 0 ? values[values.Length - 1] : (double?)null
                };
            }).ToArray();
        }

        static object[] BuildSpikes(List<SampleRow> rows, string metricKey, double threshold, int maxSpikes)
        {
            if (maxSpikes <= 0)
                return Array.Empty<object>();

            return rows
                .Where(row => row.Metrics.TryGetValue(metricKey, out var value) && value >= threshold)
                .OrderByDescending(row => row.Metrics[metricKey])
                .Take(maxSpikes)
                .Select(row => new
                {
                    metric = metricKey,
                    threshold,
                    value = row.Metrics[metricKey],
                    sampleIndex = row.Index,
                    unityFrame = row.UnityFrame,
                    renderedFrame = row.RenderedFrame,
                    elapsedMs = row.ElapsedMs
                })
                .ToArray();
        }

        static double Percentile(double[] values, double percentile)
        {
            if (values == null || values.Length == 0)
                return 0;

            var sorted = values.OrderBy(value => value).ToArray();
            var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            if (index < 0)
                index = 0;
            if (index >= sorted.Length)
                index = sorted.Length - 1;
            return sorted[index];
        }

        static RecorderState CreateRecorder(MetricDefinition metric)
        {
            var state = new RecorderState { Metric = metric };
            try
            {
                state.Recorder = ProfilerRecorder.StartNew(metric.Category, metric.ProfilerName, 1, ProfilerRecorderOptions.Default);
                state.Available = state.Recorder.Valid;
                if (!state.Available)
                    state.Error = "ProfilerRecorder.Valid=false";
            }
            catch (Exception ex)
            {
                state.Available = false;
                state.Error = ex.Message;
            }

            return state;
        }

        static IEnumerable<MetricDefinition> ResolveMetrics(string[] requested)
        {
            var supported = SupportedMetrics();
            var results = new List<MetricDefinition>();
            var source = requested != null && requested.Length > 0
                ? requested.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray()
                : DefaultMetricKeys;

            foreach (var raw in source)
            {
                var metric = ResolveMetric(raw, supported);
                if (metric != null && results.All(item => !string.Equals(item.Key, metric.Key, StringComparison.OrdinalIgnoreCase)))
                    results.Add(metric);
            }

            if (results.Count == 0)
                results.AddRange(DefaultMetricKeys.Select(key => ResolveMetric(key, supported)).Where(metric => metric != null));

            return results;
        }

        static MetricDefinition ResolveMetric(string raw, MetricDefinition[] supported)
        {
            var normalized = NormalizeKey(raw);
            var known = supported.FirstOrDefault(metric =>
                string.Equals(NormalizeKey(metric.Key), normalized, StringComparison.OrdinalIgnoreCase) ||
                metric.Aliases.Any(alias => string.Equals(NormalizeKey(alias), normalized, StringComparison.OrdinalIgnoreCase)));
            if (known != null)
                return known;

            var slash = raw.IndexOf('/');
            if (slash > 0 && slash < raw.Length - 1 && TryGetCategory(raw.Substring(0, slash), out var category, out var categoryName))
            {
                var profilerName = raw.Substring(slash + 1).Trim();
                return new MetricDefinition
                {
                    Key = NormalizeKey(raw),
                    Category = category,
                    CategoryName = categoryName,
                    ProfilerName = profilerName,
                    Unit = "raw",
                    Scale = 1,
                    Aliases = Array.Empty<string>()
                };
            }

            return null;
        }

        static MetricDefinition[] SupportedMetrics()
        {
            return new[]
            {
                Metric("main_thread_ms", ProfilerCategory.Internal, "Internal", "Main Thread", "ms", 0.000001, "main_thread", "frame_time", "cpu_ms"),
                Metric("render_thread_ms", ProfilerCategory.Internal, "Internal", "Render Thread", "ms", 0.000001, "render_thread"),
                Metric("gc_alloc_bytes", ProfilerCategory.Memory, "Memory", "GC.Alloc", "bytes", 1, "gc_alloc", "gc"),
                Metric("gc_reserved_mb", ProfilerCategory.Memory, "Memory", "GC Reserved Memory", "MB", 0.000001, "gc_reserved"),
                Metric("system_used_memory_mb", ProfilerCategory.Memory, "Memory", "System Used Memory", "MB", 0.000001, "system_memory", "memory"),
                Metric("total_used_memory_mb", ProfilerCategory.Memory, "Memory", "Total Used Memory", "MB", 0.000001, "total_memory"),
                Metric("batches_count", ProfilerCategory.Render, "Render", "Batches Count", "count", 1, "batches"),
                Metric("setpass_calls", ProfilerCategory.Render, "Render", "SetPass Calls Count", "count", 1, "setpass"),
                Metric("triangles_count", ProfilerCategory.Render, "Render", "Triangles Count", "count", 1, "triangles", "tris"),
                Metric("vertices_count", ProfilerCategory.Render, "Render", "Vertices Count", "count", 1, "vertices", "verts"),
                Metric("script_update_ms", ProfilerCategory.Internal, "Internal", "Update.ScriptRunBehaviourUpdate", "ms", 0.000001, "behaviour_update", "script_update"),
                Metric("physics_simulate_ms", ProfilerCategory.Physics, "Physics", "Physics.Simulate", "ms", 0.000001, "physics"),
                Metric("physics2d_simulate_ms", ProfilerCategory.Physics2D, "Physics2D", "Physics2D.Simulate", "ms", 0.000001, "physics2d")
            };
        }

        static MetricDefinition Metric(string key, ProfilerCategory category, string categoryName, string profilerName, string unit, double scale, params string[] aliases)
        {
            return new MetricDefinition
            {
                Key = key,
                Category = category,
                CategoryName = categoryName,
                ProfilerName = profilerName,
                Unit = unit,
                Scale = scale,
                Aliases = aliases ?? Array.Empty<string>()
            };
        }

        static bool TryGetCategory(string raw, out ProfilerCategory category, out string categoryName)
        {
            var normalized = NormalizeKey(raw);
            if (normalized == "internal")
            {
                category = ProfilerCategory.Internal;
                categoryName = "Internal";
                return true;
            }
            if (normalized == "memory")
            {
                category = ProfilerCategory.Memory;
                categoryName = "Memory";
                return true;
            }
            if (normalized == "render" || normalized == "rendering")
            {
                category = ProfilerCategory.Render;
                categoryName = "Render";
                return true;
            }
            if (normalized == "scripts" || normalized == "script")
            {
                category = ProfilerCategory.Scripts;
                categoryName = "Scripts";
                return true;
            }
            if (normalized == "physics")
            {
                category = ProfilerCategory.Physics;
                categoryName = "Physics";
                return true;
            }
            if (normalized == "physics2d")
            {
                category = ProfilerCategory.Physics2D;
                categoryName = "Physics2D";
                return true;
            }
            if (normalized == "animation")
            {
                category = ProfilerCategory.Animation;
                categoryName = "Animation";
                return true;
            }
            if (normalized == "audio")
            {
                category = ProfilerCategory.Audio;
                categoryName = "Audio";
                return true;
            }

            category = ProfilerCategory.Internal;
            categoryName = null;
            return false;
        }

        static string SavePayload(object payload, string name)
        {
            var directory = Path.Combine(ProjectRoot(), "Library", "UniBridge", "RuntimeProfiler");
            Directory.CreateDirectory(directory);
            var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(name) ? "runtime-sample" : name);
            var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safeName}.json";
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, JsonConvert.SerializeObject(payload, Formatting.Indented));
            return NormalizePath(path);
        }

        static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/');
        }

        static string NormalizeKey(string value)
        {
            return (value ?? string.Empty)
                .Trim()
                .Replace(" ", "_")
                .Replace("-", "_")
                .Replace("/", "_")
                .ToLowerInvariant();
        }

        static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string((value ?? "runtime-sample").Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
            return string.IsNullOrWhiteSpace(cleaned) ? "runtime-sample" : cleaned;
        }

        static int SafeInt(Func<int> getValue)
        {
            try { return getValue(); }
            catch { return 0; }
        }

        static float SafeFloat(Func<float> getValue)
        {
            try { return getValue(); }
            catch { return 0; }
        }

        static long SafeLong(Func<long> getValue)
        {
            try { return getValue(); }
            catch { return 0; }
        }

        static readonly string[] DefaultMetricKeys =
        {
            "main_thread_ms",
            "render_thread_ms",
            "gc_alloc_bytes",
            "gc_reserved_mb",
            "system_used_memory_mb",
            "batches_count",
            "setpass_calls",
            "triangles_count"
        };

        sealed class MetricDefinition
        {
            public string Key;
            public ProfilerCategory Category;
            public string CategoryName;
            public string ProfilerName;
            public string Unit;
            public double Scale = 1;
            public string[] Aliases = Array.Empty<string>();
        }

        sealed class RecorderState : IDisposable
        {
            public MetricDefinition Metric;
            public ProfilerRecorder Recorder;
            public bool Available;
            public string Error;

            public long ReadRawValue()
            {
                try
                {
                    return Recorder.LastValue;
                }
                catch
                {
                    return 0;
                }
            }

            public void Dispose()
            {
                try
                {
                    if (Available)
                        Recorder.Dispose();
                }
                catch
                {
                    // Dispose must not hide the profiler result.
                }
            }
        }

        sealed class SampleRow
        {
            public int Index;
            public double ElapsedMs;
            public double EditorTime;
            public int UnityFrame;
            public int RenderedFrame;
            public Dictionary<string, double> Metrics = new(StringComparer.Ordinal);

            public object ToDto() => new
            {
                index = Index,
                elapsedMs = ElapsedMs,
                editorTime = EditorTime,
                unityFrame = UnityFrame,
                renderedFrame = RenderedFrame,
                metrics = Metrics
            };
        }

        sealed class SceneCounters
        {
            public int GameObjects;
            public int ActiveGameObjects;
            public int InactiveGameObjects;
            public int MissingScripts;
            public int MonoBehaviours;
            public int EnabledMonoBehaviours;
            public int Renderers;
            public int Cameras;
            public int Lights;
            public int Colliders3D;
            public int Colliders2D;
            public int Rigidbodies3D;
            public int Rigidbodies2D;
            public int ParticleSystems;
            public int AudioSources;
            public int Canvases;
            public int EventSystems;

            public void Add(SceneCounters other)
            {
                if (other == null)
                    return;
                GameObjects += other.GameObjects;
                ActiveGameObjects += other.ActiveGameObjects;
                InactiveGameObjects += other.InactiveGameObjects;
                MissingScripts += other.MissingScripts;
                MonoBehaviours += other.MonoBehaviours;
                EnabledMonoBehaviours += other.EnabledMonoBehaviours;
                Renderers += other.Renderers;
                Cameras += other.Cameras;
                Lights += other.Lights;
                Colliders3D += other.Colliders3D;
                Colliders2D += other.Colliders2D;
                Rigidbodies3D += other.Rigidbodies3D;
                Rigidbodies2D += other.Rigidbodies2D;
                ParticleSystems += other.ParticleSystems;
                AudioSources += other.AudioSources;
                Canvases += other.Canvases;
                EventSystems += other.EventSystems;
            }

            public object ToDto() => new
            {
                gameObjects = GameObjects,
                activeGameObjects = ActiveGameObjects,
                inactiveGameObjects = InactiveGameObjects,
                missingScripts = MissingScripts,
                monoBehaviours = MonoBehaviours,
                enabledMonoBehaviours = EnabledMonoBehaviours,
                renderers = Renderers,
                cameras = Cameras,
                lights = Lights,
                colliders3D = Colliders3D,
                colliders2D = Colliders2D,
                rigidbodies3D = Rigidbodies3D,
                rigidbodies2D = Rigidbodies2D,
                particleSystems = ParticleSystems,
                audioSources = AudioSources,
                canvases = Canvases,
                eventSystems = EventSystems
            };
        }
    }
}
