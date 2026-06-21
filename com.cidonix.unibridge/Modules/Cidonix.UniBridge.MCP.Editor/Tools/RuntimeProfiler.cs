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
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
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
        const int DefaultHierarchyFrames = 1;
        const int DefaultMaxProfilerMarkers = 160;
        const int MaxProfilerMarkers = 500;
        const int DefaultMaxHierarchySamples = 40;
        const int MaxHierarchySamples = 300;
        const int DefaultMaxHierarchyDepth = 5;
        const int MaxHierarchyDepth = 12;

        public const string Title = "Inspect runtime state and profiler samples";

        public const string Description = @"Inspect live Unity runtime state and capture bounded profiler samples for AI debugging.

Use this when a Play Mode issue needs data instead of guessing: frame time, GC allocation, memory, rendering counters, physics/script markers, object counts, active scenes, and spike summaries.

Actions:
    Snapshot: Return current editor/runtime state, loaded-scene object counts, memory, and supported metric hints.
    Sample: In Play Mode by default, sample selected ProfilerRecorder metrics for a bounded number of editor update ticks. Returns compact averages/p95/max/last/spikes and can save full raw samples under Library/UniBridge/RuntimeProfiler.
    Hierarchy: Sample available profiler marker handles for one frame or a short window, returning top marker paths by time plus a synthetic hierarchy and optional saved JSON export.
    Metrics: List supported metric aliases and category/name pairs.

Search aliases: UniBridge Unity runtime profiler PlayMode performance FPS GC memory spikes stutter frame time profiler hierarchy marker hierarchy frame export ProfilerRecorder runtime state.";

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
                    case RuntimeProfilerAction.Hierarchy:
                        return await Hierarchy(parameters);
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
                await ToolExecutionScheduler.YieldIfNotCancelledAsync();
                while (sampleRows.Count < frames && EditorApplication.timeSinceStartup <= deadline)
                {
                    ToolExecutionScheduler.ThrowIfCancellationRequested();
                    sampleRows.Add(ReadSample(sampleRows.Count, startEditorTime, recorders));
                    await ToolExecutionScheduler.YieldIfNotCancelledAsync();
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

        static async Task<object> Hierarchy(RuntimeProfilerParams parameters)
        {
            var requirePlayMode = parameters.RequirePlayMode ?? true;
            if (requirePlayMode && !Application.isPlaying)
            {
                return Response.Error("PLAY_MODE_REQUIRED", new
                {
                    isPlaying = Application.isPlaying,
                    isPaused = EditorApplication.isPaused,
                    hint = "Enter Play Mode first, or pass RequirePlayMode=false for editor-time profiler marker sampling."
                });
            }

            var frames = Mathf.Clamp(parameters.SampleFrames ?? DefaultHierarchyFrames, 1, MaxSampleFrames);
            var timeoutMs = Mathf.Clamp(parameters.TimeoutMs ?? DefaultTimeoutMs, 1000, 300000);
            var maxMarkers = Mathf.Clamp(parameters.MaxProfilerMarkers ?? DefaultMaxProfilerMarkers, 1, MaxProfilerMarkers);
            var maxSamples = Mathf.Clamp(parameters.MaxHierarchySamples ?? DefaultMaxHierarchySamples, 1, MaxHierarchySamples);
            var maxDepth = Mathf.Clamp(parameters.MaxHierarchyDepth ?? DefaultMaxHierarchyDepth, 1, MaxHierarchyDepth);
            var minTimeMs = Math.Max(0, parameters.MinHierarchySampleMs ?? 0);
            var includeCounters = parameters.IncludeCounters ?? false;

            var markerSelection = BuildHierarchyMarkerSelection(parameters, includeCounters, maxMarkers);
            if (markerSelection.Selected.Length == 0)
            {
                return Response.Error("NO_PROFILER_MARKERS_SELECTED", new
                {
                    markerSelection.available,
                    markerSelection.filtered,
                    includeCounters,
                    categories = EffectiveHierarchyCategoryNames(parameters),
                    markerFilters = parameters.MarkerFilters,
                    excludeMarkerFilters = parameters.ExcludeMarkerFilters,
                    hint = "Try fewer filters, IncludeCounters=true, or Action=Metrics for known stable counters."
                });
            }

            var recorders = markerSelection.Selected.Select(marker => CreateHierarchyRecorder(marker, frames)).ToArray();
            var unavailable = recorders
                .Where(item => !item.Available)
                .Select(item => new
                {
                    category = item.Marker.CategoryName,
                    marker = item.Marker.Name,
                    unit = item.Marker.Unit,
                    flags = item.Marker.Flags,
                    reason = item.Error ?? "ProfilerRecorder was not valid for this marker in the current Unity session."
                })
                .ToArray();

            var startedUtc = DateTime.UtcNow;
            var startEditorTime = EditorApplication.timeSinceStartup;
            var deadline = startEditorTime + timeoutMs / 1000.0;
            var sampledFrames = 0;

            try
            {
                while (sampledFrames < frames && EditorApplication.timeSinceStartup <= deadline)
                {
                    await ToolExecutionScheduler.YieldIfNotCancelledAsync();
                    sampledFrames++;
                }

                var elapsedMs = Math.Max(0, (EditorApplication.timeSinceStartup - startEditorTime) * 1000.0);
                var entries = BuildHierarchyEntries(recorders.Where(item => item.Available), minTimeMs, maxDepth)
                    .OrderByDescending(item => item.SortScore)
                    .ThenBy(item => item.CategoryName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var topEntries = entries.Take(maxSamples).ToArray();
                var payload = new
                {
                    schema = "unibridge.runtimeProfiler.hierarchy.v1",
                    name = string.IsNullOrWhiteSpace(parameters.Name) ? "runtime_hierarchy" : parameters.Name.Trim(),
                    dataSource = "ProfilerRecorderMarkers",
                    note = "Unity exposes marker samples through ProfilerRecorder, not the full Profiler Window call tree. This export is a bounded marker hierarchy/top-sample view for AI triage.",
                    startedUtc = startedUtc.ToString("o"),
                    endedUtc = DateTime.UtcNow.ToString("o"),
                    requestedFrames = frames,
                    sampledFrames,
                    timedOut = sampledFrames < frames,
                    elapsedMs,
                    editor = BuildEditorRuntimeState(),
                    selection = new
                    {
                        markerSelection.available,
                        markerSelection.filtered,
                        selected = markerSelection.Selected.Length,
                        recorderAvailable = recorders.Count(item => item.Available),
                        recorderUnavailable = unavailable.Length,
                        includeCounters,
                        categories = EffectiveHierarchyCategoryNames(parameters),
                        markerFilters = parameters.MarkerFilters,
                        excludeMarkerFilters = parameters.ExcludeMarkerFilters,
                        maxMarkers,
                        maxDepth,
                        minTimeMs
                    },
                    summary = BuildHierarchySummary(entries),
                    categorySummary = BuildHierarchyCategorySummary(entries),
                    topMarkers = topEntries.Select(item => item.ToDto()).ToArray(),
                    hierarchy = BuildHierarchyTree(topEntries, maxDepth),
                    unavailableMarkers = unavailable,
                    allMarkers = entries.Select(item => item.ToDto()).ToArray()
                };

                string savedPath = null;
                if (parameters.SaveToFile ?? true)
                    savedPath = SavePayload(payload, string.IsNullOrWhiteSpace(parameters.Name) ? "runtime-hierarchy" : parameters.Name);

                var responseData = new
                {
                    schema = "unibridge.runtimeProfiler.hierarchy.summary.v1",
                    name = payload.name,
                    dataSource = payload.dataSource,
                    note = payload.note,
                    requestedFrames = frames,
                    sampledFrames,
                    timedOut = sampledFrames < frames,
                    elapsedMs,
                    savedPath,
                    editor = payload.editor,
                    selection = payload.selection,
                    summary = payload.summary,
                    categorySummary = payload.categorySummary,
                    topMarkers = payload.topMarkers,
                    hierarchy = payload.hierarchy,
                    unavailableMarkers = unavailable,
                    allMarkers = parameters.ReturnSamples == true ? payload.allMarkers : null
                };

                return Response.Success($"Captured profiler marker hierarchy with {topEntries.Length} top marker(s).", responseData);
            }
            finally
            {
                foreach (var recorder in recorders)
                    recorder.Dispose();
            }
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

        static HierarchyMarkerSelection BuildHierarchyMarkerSelection(RuntimeProfilerParams parameters, bool includeCounters, int maxMarkers)
        {
            var handles = new List<ProfilerRecorderHandle>();
            try
            {
                ProfilerRecorderHandle.GetAvailable(handles);
            }
            catch
            {
                return new HierarchyMarkerSelection
                {
                    available = 0,
                    filtered = 0,
                    Selected = Array.Empty<HierarchyMarkerDefinition>()
                };
            }

            var categoryFilters = EffectiveHierarchyCategoryKeys(parameters);
            var includeFilters = CleanFilters(parameters.MarkerFilters);
            var excludeFilters = CleanFilters(parameters.ExcludeMarkerFilters);
            var definitions = new List<HierarchyMarkerDefinition>();

            foreach (var handle in handles)
            {
                if (!handle.Valid)
                    continue;

                ProfilerRecorderDescription description;
                try
                {
                    description = ProfilerRecorderHandle.GetDescription(handle);
                }
                catch
                {
                    continue;
                }

                var name = description.Name;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var categoryName = description.Category.Name;
                if (categoryFilters.Length > 0 && !categoryFilters.Contains(NormalizeKey(categoryName)))
                    continue;

                var unit = description.UnitType.ToString();
                var isTime = description.UnitType == ProfilerMarkerDataUnit.TimeNanoseconds;
                var isCounter = description.Flags.HasFlag(MarkerFlags.Counter) || !isTime;
                if (!includeCounters && !isTime)
                    continue;

                var searchable = $"{categoryName}/{name} {unit} {description.Flags}";
                if (includeFilters.Length > 0 && !MatchesAny(searchable, includeFilters))
                    continue;
                if (excludeFilters.Length > 0 && MatchesAny(searchable, excludeFilters))
                    continue;

                definitions.Add(new HierarchyMarkerDefinition
                {
                    Category = description.Category,
                    CategoryName = string.IsNullOrWhiteSpace(categoryName) ? "Unknown" : categoryName,
                    Name = name,
                    Unit = unit,
                    UnitType = description.UnitType,
                    Flags = description.Flags.ToString(),
                    IsCounter = isCounter,
                    IsTime = isTime,
                    Importance = HierarchyMarkerImportance(categoryName, name)
                });
            }

            var selected = definitions
                .OrderByDescending(item => item.IsTime)
                .ThenByDescending(item => item.Importance)
                .ThenBy(item => item.CategoryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(maxMarkers)
                .ToArray();

            return new HierarchyMarkerSelection
            {
                available = handles.Count,
                filtered = definitions.Count,
                Selected = selected
            };
        }

        static HierarchyRecorderState CreateHierarchyRecorder(HierarchyMarkerDefinition marker, int frames)
        {
            var state = new HierarchyRecorderState { Marker = marker };
            try
            {
                state.Recorder = ProfilerRecorder.StartNew(marker.Category, marker.Name, Mathf.Clamp(frames, 1, MaxSampleFrames), ProfilerRecorderOptions.Default);
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

        static IEnumerable<HierarchyMarkerEntry> BuildHierarchyEntries(IEnumerable<HierarchyRecorderState> recorders, double minTimeMs, int maxDepth)
        {
            foreach (var recorder in recorders)
            {
                ProfilerRecorderSample[] samples;
                try
                {
                    samples = recorder.Recorder.ToArray();
                }
                catch
                {
                    samples = Array.Empty<ProfilerRecorderSample>();
                }

                var values = samples != null && samples.Length > 0
                    ? samples.Select(sample => ScaleHierarchyValue(sample.Value, recorder.Marker.UnitType)).ToArray()
                    : Array.Empty<double>();
                if (values.Length == 0)
                {
                    long lastRaw = 0;
                    try { lastRaw = recorder.Recorder.LastValue; }
                    catch { lastRaw = 0; }
                    values = new[] { ScaleHierarchyValue(lastRaw, recorder.Marker.UnitType) };
                }
                var nonZero = values.Where(value => Math.Abs(value) > double.Epsilon).ToArray();
                var basis = nonZero.Length > 0 ? nonZero : values;
                var total = basis.Sum();
                var max = basis.Length > 0 ? basis.Max() : 0;
                var avg = basis.Length > 0 ? basis.Average() : 0;
                var last = values.Length > 0 ? values[values.Length - 1] : 0;

                if (recorder.Marker.IsTime && max < minTimeMs && total < minTimeMs)
                    continue;

                var pathParts = BuildHierarchyPathParts(recorder.Marker.CategoryName, recorder.Marker.Name, maxDepth);
                yield return new HierarchyMarkerEntry
                {
                    CategoryName = recorder.Marker.CategoryName,
                    Name = recorder.Marker.Name,
                    PathParts = pathParts,
                    Path = string.Join("/", pathParts),
                    Unit = recorder.Marker.IsTime ? "ms" : recorder.Marker.Unit,
                    UnitType = recorder.Marker.Unit,
                    Flags = recorder.Marker.Flags,
                    IsCounter = recorder.Marker.IsCounter,
                    IsTime = recorder.Marker.IsTime,
                    Samples = values.Length,
                    NonZeroSamples = nonZero.Length,
                    TotalValue = total,
                    AvgValue = avg,
                    MaxValue = max,
                    LastValue = last
                };
            }
        }

        static object BuildHierarchySummary(HierarchyMarkerEntry[] entries)
        {
            var timeEntries = entries.Where(item => item.IsTime).ToArray();
            return new
            {
                markerCount = entries.Length,
                timeMarkerCount = timeEntries.Length,
                counterMarkerCount = entries.Length - timeEntries.Length,
                totalTimeMs = timeEntries.Sum(item => item.TotalValue),
                maxMarkerTimeMs = timeEntries.Length > 0 ? timeEntries.Max(item => item.MaxValue) : 0,
                topMarker = entries.OrderByDescending(item => item.SortScore).FirstOrDefault()?.ToCompactDto()
            };
        }

        static object[] BuildHierarchyCategorySummary(HierarchyMarkerEntry[] entries)
        {
            return entries
                .GroupBy(item => item.CategoryName ?? "Unknown", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var time = group.Where(item => item.IsTime).ToArray();
                    return new
                    {
                        category = group.Key,
                        markerCount = group.Count(),
                        timeMarkerCount = time.Length,
                        totalTimeMs = time.Sum(item => item.TotalValue),
                        maxMarkerTimeMs = time.Length > 0 ? time.Max(item => item.MaxValue) : 0
                    };
                })
                .OrderByDescending(item => item.totalTimeMs)
                .ThenBy(item => item.category, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static object[] BuildHierarchyTree(HierarchyMarkerEntry[] entries, int maxDepth)
        {
            var roots = new Dictionary<string, HierarchyTreeNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var parts = entry.PathParts == null || entry.PathParts.Length == 0
                    ? new[] { entry.CategoryName ?? "Unknown", entry.Name ?? "Marker" }
                    : entry.PathParts;
                var depthLimit = Math.Min(parts.Length, maxDepth);
                Dictionary<string, HierarchyTreeNode> siblings = roots;
                var parentPath = string.Empty;

                for (var i = 0; i < depthLimit; i++)
                {
                    var part = parts[i];
                    if (!siblings.TryGetValue(part, out var current))
                    {
                        var path = string.IsNullOrEmpty(parentPath) ? part : $"{parentPath}/{part}";
                        current = new HierarchyTreeNode
                        {
                            Name = part,
                            Path = path,
                            Depth = i
                        };
                        siblings[part] = current;
                    }

                    current.MarkerCount++;
                    current.TotalTimeMs += entry.IsTime ? entry.TotalValue : 0;
                    current.MaxTimeMs = Math.Max(current.MaxTimeMs, entry.IsTime ? entry.MaxValue : 0);
                    current.MaxValue = Math.Max(current.MaxValue, entry.MaxValue);
                    siblings = current.Children;
                    parentPath = current.Path;
                }
            }

            return roots.Values
                .OrderByDescending(item => Math.Max(item.TotalTimeMs, item.MaxValue))
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.ToDto())
                .ToArray();
        }

        static double ScaleHierarchyValue(long rawValue, ProfilerMarkerDataUnit unit)
        {
            return unit == ProfilerMarkerDataUnit.TimeNanoseconds
                ? rawValue / 1000000.0
                : rawValue;
        }

        static string[] BuildHierarchyPathParts(string categoryName, string markerName, int maxDepth)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(categoryName))
                parts.Add(categoryName.Trim());

            var normalized = (markerName ?? "Marker")
                .Replace("::", "/")
                .Replace("\\", "/")
                .Replace(".", "/");
            foreach (var raw in normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Trim();
                if (part.Length == 0)
                    continue;
                if (parts.Count > 0 && string.Equals(NormalizeKey(parts[parts.Count - 1]), NormalizeKey(part), StringComparison.OrdinalIgnoreCase))
                    continue;
                parts.Add(part);
            }

            if (parts.Count <= maxDepth)
                return parts.ToArray();

            var result = parts.Take(Math.Max(1, maxDepth - 1)).ToList();
            result.Add("...");
            return result.ToArray();
        }

        static int HierarchyMarkerImportance(string categoryName, string markerName)
        {
            var key = NormalizeKey($"{categoryName}_{markerName}");
            var score = 0;
            if (key.Contains("main_thread")) score += 100;
            if (key.Contains("playerloop")) score += 90;
            if (key.Contains("script") || key.Contains("behaviour")) score += 80;
            if (key.Contains("update")) score += 70;
            if (key.Contains("render") || key.Contains("camera")) score += 60;
            if (key.Contains("physics")) score += 55;
            if (key.Contains("animation") || key.Contains("animator")) score += 50;
            if (key.Contains("canvas") || key.Contains("ui") || key.Contains("gui")) score += 45;
            if (key.Contains("particle")) score += 40;
            if (key.Contains("gc") || key.Contains("alloc")) score += 35;
            if (key.Contains("audio")) score += 25;
            return score;
        }

        static string[] EffectiveHierarchyCategoryNames(RuntimeProfilerParams parameters)
        {
            var source = parameters.ProfilerCategories != null && parameters.ProfilerCategories.Length > 0
                ? parameters.ProfilerCategories
                : DefaultHierarchyCategories;
            return source
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static string[] EffectiveHierarchyCategoryKeys(RuntimeProfilerParams parameters)
        {
            return EffectiveHierarchyCategoryNames(parameters)
                .Select(NormalizeKey)
                .Where(item => item.Length > 0)
                .ToArray();
        }

        static string[] CleanFilters(string[] filters)
        {
            return (filters ?? Array.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToArray();
        }

        static bool MatchesAny(string value, string[] filters)
        {
            if (filters == null || filters.Length == 0)
                return false;

            return filters.Any(filter => value?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
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

        static readonly string[] DefaultHierarchyCategories =
        {
            "Internal",
            "Scripts",
            "Render",
            "Physics",
            "Physics2D",
            "Animation",
            "Audio",
            "GUI",
            "Input",
            "Loading"
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

        sealed class HierarchyMarkerSelection
        {
            public int available;
            public int filtered;
            public HierarchyMarkerDefinition[] Selected = Array.Empty<HierarchyMarkerDefinition>();
        }

        sealed class HierarchyMarkerDefinition
        {
            public ProfilerCategory Category;
            public string CategoryName;
            public string Name;
            public string Unit;
            public ProfilerMarkerDataUnit UnitType;
            public string Flags;
            public bool IsCounter;
            public bool IsTime;
            public int Importance;
        }

        sealed class HierarchyRecorderState : IDisposable
        {
            public HierarchyMarkerDefinition Marker;
            public ProfilerRecorder Recorder;
            public bool Available;
            public string Error;

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

        sealed class HierarchyMarkerEntry
        {
            public string CategoryName;
            public string Name;
            public string[] PathParts = Array.Empty<string>();
            public string Path;
            public string Unit;
            public string UnitType;
            public string Flags;
            public bool IsCounter;
            public bool IsTime;
            public int Samples;
            public int NonZeroSamples;
            public double TotalValue;
            public double AvgValue;
            public double MaxValue;
            public double LastValue;
            public double SortScore => IsTime ? TotalValue : MaxValue;

            public object ToDto() => new
            {
                category = CategoryName,
                name = Name,
                path = Path,
                depth = PathParts?.Length ?? 0,
                unit = Unit,
                unitType = UnitType,
                flags = Flags,
                isCounter = IsCounter,
                samples = Samples,
                nonZeroSamples = NonZeroSamples,
                total = TotalValue,
                avg = AvgValue,
                max = MaxValue,
                last = LastValue,
                totalTimeMs = IsTime ? TotalValue : (double?)null,
                avgTimeMs = IsTime ? AvgValue : (double?)null,
                maxTimeMs = IsTime ? MaxValue : (double?)null,
                lastTimeMs = IsTime ? LastValue : (double?)null
            };

            public object ToCompactDto() => new
            {
                category = CategoryName,
                name = Name,
                path = Path,
                unit = Unit,
                total = TotalValue,
                max = MaxValue,
                totalTimeMs = IsTime ? TotalValue : (double?)null,
                maxTimeMs = IsTime ? MaxValue : (double?)null
            };
        }

        sealed class HierarchyTreeNode
        {
            public string Name;
            public string Path;
            public int Depth;
            public int MarkerCount;
            public double TotalTimeMs;
            public double MaxTimeMs;
            public double MaxValue;
            public Dictionary<string, HierarchyTreeNode> Children = new(StringComparer.OrdinalIgnoreCase);

            public object ToDto() => new
            {
                name = Name,
                path = Path,
                depth = Depth,
                markerCount = MarkerCount,
                totalTimeMs = TotalTimeMs,
                maxTimeMs = MaxTimeMs,
                children = Children.Values
                    .OrderByDescending(item => Math.Max(item.TotalTimeMs, item.MaxValue))
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .Select(item => item.ToDto())
                    .ToArray()
            };
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
