#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Exports complete scene hierarchies for large-scene agent workflows.
    /// </summary>
    public static class SceneHierarchyExport
    {
        const string ToolName = "UniBridge_SceneHierarchyExport";
        const int DefaultPagePreviewLimit = 200;
        const int MaxInlineLimit = 5000;

        public const string Title = "Export and compare complete scene hierarchies";

        public const string Description = @"Export complete, stable scene hierarchy data for large Unity scenes and compare hierarchy export files.

Use this when bounded SceneObjectView snapshots are too small for safe scene organization, sorting-layer audits, prototype-to-production scene comparison, or large batch planning.

Args:
    Action: Export or CompareExports.
    ScenePath: Optional loaded scene path/name. Omit to export all loaded scenes.
    IncludeInactive: Include inactive branches. Defaults true.
    IncludeComponents, IncludeRenderers, IncludePrefabInfo, IncludeLight2D: Include data needed for sorting, prefab, and missing-script audits.
    Format: Json or Jsonl for export files.
    Offset/Limit/Cursor: Stable depth-first pagination. Cursor is the next traversal offset returned by the previous call.
    WriteToFile/AutoFileThreshold: Write full exports to Library/UniBridge/SceneHierarchyExports when large.
    OpenIfNotLoaded: Optionally open a scene asset additively for export, then close it without saving.
    LeftExportPath/RightExportPath: Files to compare for Action=CompareExports.
    IncludeDuplicateKeys/MaxDuplicateKeys: Opt into verbose duplicate compare-key rows.
    IncludeDuplicateSummary: Include bounded duplicate group examples in CompareExports summary. Defaults true.
    MaxDuplicateSamples: Limit sample objectIds/indexedPaths per duplicate path group.

Returns:
    success, message, scenes, summary, totalObjects, stable traversal metadata, page objects, optional full export file path, and diff summaries for CompareExports.";

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "hierarchy", "debug" }, EnabledByDefault = true, ExecutionPolicy = ToolExecutionPolicy.Mutating)]
        public static object HandleCommand(SceneHierarchyExportParams parameters)
        {
            parameters ??= new SceneHierarchyExportParams();

            try
            {
                return parameters.Action switch
                {
                    SceneHierarchyExportAction.Export => Export(parameters),
                    SceneHierarchyExportAction.CompareExports => CompareExports(parameters),
                    _ => Response.Error($"Unsupported scene hierarchy export action: {parameters.Action}")
                };
            }
            catch (Exception ex)
            {
                return Response.Error($"Scene hierarchy export failed: {ex.Message}", new { exceptionType = ex.GetType().FullName });
            }
        }

        static object Export(SceneHierarchyExportParams parameters)
        {
            var openedScenes = new List<Scene>();
            var activeScene = SceneManager.GetActiveScene();

            try
            {
                var scenes = ResolveScenes(parameters, openedScenes);
                if (scenes.Count == 0)
                {
                    return Response.Error("No loaded scenes matched the export request.", new
                    {
                        parameters.ScenePath,
                        parameters.OpenIfNotLoaded,
                        hint = "Load the scene first with UniBridge_ManageScene or pass OpenIfNotLoaded=true for a scene asset path."
                    });
                }

                var options = new SceneHierarchyExportOptions
                {
                    IncludeInactive = parameters.IncludeInactive,
                    IncludeComponents = parameters.IncludeComponents,
                    IncludeRenderers = parameters.IncludeRenderers,
                    IncludePrefabInfo = parameters.IncludePrefabInfo,
                    IncludeLight2D = parameters.IncludeLight2D
                };

                var objects = SceneHierarchyExportUtility.CollectObjects(scenes, options);
                var total = objects.Count;
                var summary = BuildExportSummary(objects, parameters.MaxDuplicateKeys, parameters.MaxDuplicateSamples);
                var offset = ResolveOffset(parameters.Cursor, parameters.Offset);
                var autoThreshold = Math.Max(0, parameters.AutoFileThreshold);
                var shouldWriteFile = parameters.WriteToFile ||
                                      parameters.Format == SceneHierarchyExportFormat.Jsonl ||
                                      (autoThreshold > 0 && total > autoThreshold);

                var limit = parameters.Limit.HasValue
                    ? Math.Max(0, Math.Min(parameters.Limit.Value, MaxInlineLimit))
                    : shouldWriteFile
                        ? Math.Min(DefaultPagePreviewLimit, total)
                        : Math.Min(MaxInlineLimit, total);

                offset = Math.Max(0, Math.Min(offset, total));
                var page = limit == 0
                    ? new List<Dictionary<string, object>>()
                    : objects.Skip(offset).Take(limit).ToList();
                var hasMore = offset + page.Count < total;

                ExportFileInfo fileInfo = null;
                if (shouldWriteFile)
                {
                    fileInfo = SceneHierarchyExportUtility.WriteExportFile(
                        BuildExportDocument(scenes, objects, options, summary),
                        parameters.Format,
                        SceneHierarchyExportUtility.BuildSceneKey(scenes));
                }

                var data = new
                {
                    schema = SceneHierarchyExportUtility.Schema,
                    project = SceneHierarchyExportUtility.BuildProjectInfo(),
                    scenes = scenes.Select(SceneHierarchyExportUtility.SerializeScene).ToArray(),
                    stableOrder = "Depth-first pre-order traversal in loaded scene order, root sibling order, then child siblingIndex order.",
                    include = options,
                    summary,
                    totalObjects = total,
                    offset,
                    limit,
                    count = page.Count,
                    hasMore,
                    nextCursor = hasMore ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null,
                    file = fileInfo,
                    objects = page
                };

                var message = fileInfo != null
                    ? $"Exported {total} scene objects to {fileInfo.absolutePath}."
                    : $"Returned {page.Count} of {total} scene objects inline.";
                return Response.Success(message, data);
            }
            finally
            {
                RestoreSceneState(openedScenes, activeScene);
            }
        }

        static object CompareExports(SceneHierarchyExportParams parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters.LeftExportPath) || string.IsNullOrWhiteSpace(parameters.RightExportPath))
            {
                return Response.Error("LeftExportPath and RightExportPath are required for CompareExports.");
            }

            var leftPath = SceneHierarchyExportUtility.ResolveFilePath(parameters.LeftExportPath);
            var rightPath = SceneHierarchyExportUtility.ResolveFilePath(parameters.RightExportPath);
            var leftObjects = SceneHierarchyExportUtility.ReadExportObjects(leftPath);
            var rightObjects = SceneHierarchyExportUtility.ReadExportObjects(rightPath);
            var maxItems = Math.Max(1, Math.Min(parameters.MaxDiffItems, 5000));
            var maxDuplicateKeys = Math.Max(0, Math.Min(parameters.MaxDuplicateKeys, 500));
            var maxDuplicateSamples = Math.Max(0, Math.Min(parameters.MaxDuplicateSamples, 100));

            var leftIndex = BuildExportIndex(leftObjects, out var leftDuplicates);
            var rightIndex = BuildExportIndex(rightObjects, out var rightDuplicates);
            var leftDuplicateSummary = BuildDuplicatePathSummary(leftObjects, maxDuplicateKeys, maxDuplicateSamples);
            var rightDuplicateSummary = BuildDuplicatePathSummary(rightObjects, maxDuplicateKeys, maxDuplicateSamples);
            var leftKeys = new HashSet<string>(leftIndex.Keys, StringComparer.Ordinal);
            var rightKeys = new HashSet<string>(rightIndex.Keys, StringComparer.Ordinal);

            var onlyLeft = leftKeys.Except(rightKeys, StringComparer.Ordinal)
                .Take(maxItems)
                .Select(key => BuildDiffRef(key, leftIndex[key]))
                .ToArray();
            var onlyRight = rightKeys.Except(leftKeys, StringComparer.Ordinal)
                .Take(maxItems)
                .Select(key => BuildDiffRef(key, rightIndex[key]))
                .ToArray();

            var changed = new List<object>();
            foreach (var key in leftKeys.Intersect(rightKeys, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            {
                if (changed.Count >= maxItems)
                {
                    break;
                }

                var diff = CompareMatchingObjects(key, leftIndex[key], rightIndex[key], parameters);
                if (diff != null)
                {
                    changed.Add(diff);
                }
            }

            var leftPayload = new Dictionary<string, object>
            {
                ["totalObjects"] = leftObjects.Count,
                ["duplicateGroupCount"] = leftDuplicateSummary.duplicateGroupCount,
                ["duplicateObjectCount"] = leftDuplicateSummary.duplicateObjectCount
            };
            var rightPayload = new Dictionary<string, object>
            {
                ["totalObjects"] = rightObjects.Count,
                ["duplicateGroupCount"] = rightDuplicateSummary.duplicateGroupCount,
                ["duplicateObjectCount"] = rightDuplicateSummary.duplicateObjectCount
            };

            if (parameters.IncludeDuplicateKeys)
            {
                leftPayload["duplicateKeys"] = leftDuplicates.Take(maxDuplicateKeys).ToArray();
                rightPayload["duplicateKeys"] = rightDuplicates.Take(maxDuplicateKeys).ToArray();
            }

            var duplicateSummary = BuildCompareDuplicateSummary(leftDuplicateSummary, rightDuplicateSummary, parameters.IncludeDuplicateSummary);
            var data = new
            {
                left = leftPayload,
                right = rightPayload,
                comparedBy = "indexedPath when available, then hierarchy path, then scene/name/traversal fallback; use objectId only inside a single live Unity session.",
                summary = new
                {
                    onlyInLeft = leftKeys.Except(rightKeys, StringComparer.Ordinal).Count(),
                    onlyInRight = rightKeys.Except(leftKeys, StringComparer.Ordinal).Count(),
                    changed = changed.Count,
                    returnedLimit = maxItems,
                    includeDuplicateKeys = parameters.IncludeDuplicateKeys,
                    includeDuplicateSummary = parameters.IncludeDuplicateSummary,
                    duplicates = duplicateSummary
                },
                onlyLeft,
                onlyRight,
                changed
            };

            return Response.Success("Compared scene hierarchy exports.", data);
        }

        static Dictionary<string, object> BuildExportDocument(
            IReadOnlyList<Scene> scenes,
            IReadOnlyList<Dictionary<string, object>> objects,
            SceneHierarchyExportOptions options,
            object summary)
        {
            return new Dictionary<string, object>
            {
                ["schema"] = SceneHierarchyExportUtility.Schema,
                ["createdUtc"] = DateTime.UtcNow.ToString("O"),
                ["project"] = SceneHierarchyExportUtility.BuildProjectInfo(),
                ["unityVersion"] = Application.unityVersion,
                ["scenes"] = scenes.Select(SceneHierarchyExportUtility.SerializeScene).ToArray(),
                ["stableOrder"] = "Depth-first pre-order traversal in loaded scene order, root sibling order, then child siblingIndex order.",
                ["include"] = options,
                ["summary"] = summary,
                ["totalObjects"] = objects.Count,
                ["objects"] = objects
            };
        }

        static object BuildExportSummary(IReadOnlyList<Dictionary<string, object>> objects, int maxDuplicateGroups, int maxDuplicateSamples)
        {
            var rendererCountByType = new Dictionary<string, int>(StringComparer.Ordinal);
            var rendererCountBySortingLayer = new Dictionary<string, int>(StringComparer.Ordinal);
            var rendererCount = 0;
            var light2DCount = 0;
            var prefabInstanceCount = 0;
            var missingScriptsTotal = 0;
            var objectsWithMissingScripts = 0;
            var rootObjects = 0;
            var inactiveObjects = 0;

            foreach (var obj in objects)
            {
                if (GetInt(obj, "depth") == 0)
                {
                    rootObjects++;
                }

                if (obj.TryGetValue("activeInHierarchy", out var activeValue) &&
                    activeValue is bool activeInHierarchy &&
                    !activeInHierarchy)
                {
                    inactiveObjects++;
                }

                var missingScripts = GetInt(obj, "missingScripts");
                missingScriptsTotal += missingScripts;
                if (missingScripts > 0)
                {
                    objectsWithMissingScripts++;
                }

                foreach (var renderer in ToJArray(obj.TryGetValue("renderers", out var renderers) ? renderers : null).OfType<JObject>())
                {
                    rendererCount++;
                    Increment(rendererCountByType, renderer["rendererType"]?.ToString() ?? "(unknown)");
                    Increment(rendererCountBySortingLayer, renderer["sortingLayerName"]?.ToString() ?? "(none)");
                }

                light2DCount += ToJArray(obj.TryGetValue("light2D", out var light2D) ? light2D : null).Count;

                var prefab = obj.TryGetValue("prefab", out var prefabValue) ? ToJObject(prefabValue) : null;
                if (prefab?["isPartOfPrefabInstance"]?.ToObject<bool?>() == true)
                {
                    prefabInstanceCount++;
                }
            }

            var duplicateSummary = BuildDuplicatePathSummary(objects, maxDuplicateGroups, maxDuplicateSamples);
            return new
            {
                totalObjects = objects.Count,
                rootObjects,
                inactiveObjects,
                missingScriptsTotal,
                objectsWithMissingScripts,
                rendererCount,
                rendererCountByType = OrderCounts(rendererCountByType),
                rendererCountBySortingLayer = OrderCounts(rendererCountBySortingLayer),
                light2DCount,
                prefabInstanceCount,
                duplicatePathGroupCount = duplicateSummary.duplicateGroupCount,
                topDuplicatePathGroups = duplicateSummary.topDuplicateGroups
            };
        }

        static List<Scene> ResolveScenes(SceneHierarchyExportParams parameters, List<Scene> openedScenes)
        {
            var scenes = SceneObjectLocator.GetLoadedScenes(parameters.ScenePath).ToList();
            if (scenes.Count > 0 || !parameters.OpenIfNotLoaded || string.IsNullOrWhiteSpace(parameters.ScenePath))
            {
                return scenes;
            }

            var sceneAssetPath = ResolveSceneAssetPath(parameters.ScenePath);
            if (string.IsNullOrWhiteSpace(sceneAssetPath))
            {
                return scenes;
            }

            var opened = EditorSceneManager.OpenScene(sceneAssetPath, OpenSceneMode.Additive);
            if (opened.IsValid() && opened.isLoaded)
            {
                openedScenes.Add(opened);
                scenes.Add(opened);
            }

            return scenes;
        }

        static string ResolveSceneAssetPath(string scenePathOrName)
        {
            var normalized = scenePathOrName.Replace('\\', '/').Trim();
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(SceneHierarchyExportUtility.ProjectRoot, normalized).Replace('/', Path.DirectorySeparatorChar)))
            {
                return normalized;
            }

            if (!normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                var guids = AssetDatabase.FindAssets($"{Path.GetFileNameWithoutExtension(normalized)} t:Scene");
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.Equals(Path.GetFileNameWithoutExtension(path), normalized, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
            }

            return null;
        }

        static int ResolveOffset(string cursor, int? offset)
        {
            if (!string.IsNullOrWhiteSpace(cursor) &&
                int.TryParse(cursor.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return offset ?? 0;
        }

        static void RestoreSceneState(IReadOnlyList<Scene> openedScenes, Scene activeScene)
        {
            for (var i = openedScenes.Count - 1; i >= 0; i--)
            {
                var scene = openedScenes[i];
                if (scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            if (activeScene.IsValid() && activeScene.isLoaded)
            {
                SceneManager.SetActiveScene(activeScene);
            }
        }

        static Dictionary<string, JObject> BuildExportIndex(IReadOnlyList<JObject> objects, out List<object> duplicateKeys)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var index = new Dictionary<string, JObject>(StringComparer.Ordinal);
            duplicateKeys = new List<object>();

            foreach (var obj in objects)
            {
                var baseKey = BuildCompareKey(obj);
                counts.TryGetValue(baseKey, out var count);
                counts[baseKey] = count + 1;
                var key = count == 0 ? baseKey : $"{baseKey}#duplicate{count + 1}";
                if (count > 0)
                {
                    duplicateKeys.Add(new { key = baseKey, duplicateIndex = count + 1, path = obj["path"]?.ToString(), name = obj["name"]?.ToString() });
                }

                index[key] = obj;
            }

            return index;
        }

        static DuplicatePathSummary BuildDuplicatePathSummary(IReadOnlyList<Dictionary<string, object>> objects, int maxGroups, int maxSamples)
        {
            return BuildDuplicatePathSummary(
                objects.Select(obj => new ExportPathRef
                {
                    ScenePath = GetString(obj, "scenePath"),
                    Path = GetString(obj, "path"),
                    IndexedPath = GetString(obj, "indexedPath"),
                    Name = GetString(obj, "name"),
                    ObjectId = GetLong(obj, "objectId")
                }),
                maxGroups,
                maxSamples);
        }

        static DuplicatePathSummary BuildDuplicatePathSummary(IReadOnlyList<JObject> objects, int maxGroups, int maxSamples)
        {
            return BuildDuplicatePathSummary(
                objects.Select(obj => new ExportPathRef
                {
                    ScenePath = obj["scenePath"]?.ToString(),
                    Path = obj["path"]?.ToString(),
                    IndexedPath = obj["indexedPath"]?.ToString(),
                    Name = obj["name"]?.ToString(),
                    ObjectId = obj["objectId"]?.ToObject<long?>()
                }),
                maxGroups,
                maxSamples);
        }

        static DuplicatePathSummary BuildDuplicatePathSummary(IEnumerable<ExportPathRef> refs, int maxGroups, int maxSamples)
        {
            var groups = refs
                .Where(item => !string.IsNullOrWhiteSpace(item.Path) || !string.IsNullOrWhiteSpace(item.IndexedPath))
                .GroupBy(item => $"{item.ScenePath}|{(string.IsNullOrWhiteSpace(item.Path) ? item.IndexedPath : item.Path)}", StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();

            var limit = Math.Max(0, Math.Min(maxGroups, 500));
            var sampleLimit = Math.Max(0, Math.Min(maxSamples, 100));
            return new DuplicatePathSummary
            {
                duplicateGroupCount = groups.Length,
                duplicateObjectCount = groups.Sum(group => group.Count()),
                topDuplicateGroups = groups
                    .Take(limit)
                    .Select(group =>
                    {
                        var first = group.First();
                        return new DuplicatePathGroup
                        {
                            scenePath = first.ScenePath,
                            path = string.IsNullOrWhiteSpace(first.Path) ? first.IndexedPath : first.Path,
                            count = group.Count(),
                            sampleNames = group.Select(item => item.Name).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Take(sampleLimit).ToArray(),
                            sampleObjectIds = group.Select(item => item.ObjectId).Where(value => value.HasValue).Select(value => value.Value).Take(sampleLimit).ToArray(),
                            sampleIndexedPaths = group.Select(item => item.IndexedPath).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Take(sampleLimit).ToArray()
                        };
                    })
                    .ToArray()
            };
        }

        static Dictionary<string, object> BuildCompareDuplicateSummary(
            DuplicatePathSummary left,
            DuplicatePathSummary right,
            bool includeDetails)
        {
            var result = new Dictionary<string, object>
            {
                ["leftGroupCount"] = left.duplicateGroupCount,
                ["rightGroupCount"] = right.duplicateGroupCount,
                ["leftObjectCount"] = left.duplicateObjectCount,
                ["rightObjectCount"] = right.duplicateObjectCount
            };

            if (!includeDetails)
            {
                return result;
            }

            if (DuplicateGroupsMatch(left.topDuplicateGroups, right.topDuplicateGroups))
            {
                result["sharedTopGroups"] = left.topDuplicateGroups
                    .Select(group => new
                    {
                        group.scenePath,
                        group.path,
                        group.count,
                        group.sampleNames,
                        group.sampleIndexedPaths
                    })
                    .ToArray();
            }
            else
            {
                result["leftTopGroups"] = left.topDuplicateGroups;
                result["rightTopGroups"] = right.topDuplicateGroups;
            }

            return result;
        }

        static bool DuplicateGroupsMatch(IReadOnlyList<DuplicatePathGroup> left, IReadOnlyList<DuplicatePathGroup> right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Count; i++)
            {
                var a = left[i];
                var b = right[i];
                if (!string.Equals(a.scenePath, b.scenePath, StringComparison.Ordinal) ||
                    !string.Equals(a.path, b.path, StringComparison.Ordinal) ||
                    a.count != b.count ||
                    !(a.sampleIndexedPaths ?? Array.Empty<string>()).SequenceEqual(b.sampleIndexedPaths ?? Array.Empty<string>(), StringComparer.Ordinal) ||
                    !(a.sampleNames ?? Array.Empty<string>()).SequenceEqual(b.sampleNames ?? Array.Empty<string>(), StringComparer.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        static string BuildCompareKey(JObject obj)
        {
            var scenePath = obj["scenePath"]?.ToString();
            var path = obj["path"]?.ToString();
            var indexedPath = obj["indexedPath"]?.ToString();
            var name = obj["name"]?.ToString();
            return !string.IsNullOrWhiteSpace(indexedPath)
                ? $"{scenePath}|{indexedPath}"
                : !string.IsNullOrWhiteSpace(path)
                    ? $"{scenePath}|{path}"
                    : $"{scenePath}|{name}|{obj["traversalIndex"]}";
        }

        static int GetInt(Dictionary<string, object> obj, string key)
        {
            return obj.TryGetValue(key, out var value) ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
        }

        static long? GetLong(Dictionary<string, object> obj, string key)
        {
            if (!obj.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        static string GetString(Dictionary<string, object> obj, string key)
        {
            return obj.TryGetValue(key, out var value) ? value?.ToString() : null;
        }

        static JArray ToJArray(object value)
        {
            if (value == null)
            {
                return new JArray();
            }

            return value is JArray array ? array : JArray.FromObject(value);
        }

        static JObject ToJObject(object value)
        {
            if (value == null)
            {
                return null;
            }

            return value as JObject ?? JObject.FromObject(value);
        }

        static void Increment(IDictionary<string, int> counts, string key)
        {
            key = string.IsNullOrWhiteSpace(key) ? "(none)" : key;
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        static object OrderCounts(IDictionary<string, int> counts)
        {
            return counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        static object BuildDiffRef(string key, JObject obj)
        {
            return new
            {
                key,
                name = obj["name"]?.ToString(),
                path = obj["path"]?.ToString(),
                indexedPath = obj["indexedPath"]?.ToString(),
                scenePath = obj["scenePath"]?.ToString(),
                prefab = obj["prefab"] is JObject prefab ? new
                {
                    sourcePath = prefab["sourcePath"]?.ToString(),
                    sourceGuid = prefab["sourceGuid"]?.ToString()
                } : null
            };
        }

        static object CompareMatchingObjects(string key, JObject left, JObject right, SceneHierarchyExportParams parameters)
        {
            var changes = new List<string>();

            if (!string.Equals(left["parentPath"]?.ToString(), right["parentPath"]?.ToString(), StringComparison.Ordinal))
            {
                changes.Add("parentPath");
            }

            if (!string.Equals(left["siblingIndex"]?.ToString(), right["siblingIndex"]?.ToString(), StringComparison.Ordinal))
            {
                changes.Add("siblingIndex");
            }

            if (parameters.CompareRenderers && !JToken.DeepEquals(NormalizeRenderers(left), NormalizeRenderers(right)))
            {
                changes.Add("renderers");
            }

            if (parameters.ComparePrefabInfo && !JToken.DeepEquals(NormalizePrefab(left), NormalizePrefab(right)))
            {
                changes.Add("prefab");
            }

            if (parameters.CompareLight2D && !JToken.DeepEquals(NormalizeLight2D(left), NormalizeLight2D(right)))
            {
                changes.Add("light2D");
            }

            if (changes.Count == 0)
            {
                return null;
            }

            return new
            {
                key,
                name = left["name"]?.ToString(),
                path = left["path"]?.ToString(),
                changes = changes.ToArray(),
                left = BuildComparableSummary(left),
                right = BuildComparableSummary(right)
            };
        }

        static object BuildComparableSummary(JObject obj)
        {
            return new
            {
                parentPath = obj["parentPath"]?.ToString(),
                siblingIndex = obj["siblingIndex"]?.ToObject<int?>(),
                renderers = NormalizeRenderers(obj),
                prefab = NormalizePrefab(obj),
                light2D = NormalizeLight2D(obj)
            };
        }

        static JToken NormalizeRenderers(JObject obj)
        {
            if (obj["renderers"] is not JArray renderers)
            {
                return new JArray();
            }

            return new JArray(renderers
                .OfType<JObject>()
                .Select(renderer => new JObject
                {
                    ["rendererType"] = renderer["rendererType"]?.DeepClone() ?? JValue.CreateNull(),
                    ["sortingLayerName"] = renderer["sortingLayerName"]?.DeepClone() ?? JValue.CreateNull(),
                    ["sortingLayerId"] = renderer["sortingLayerId"]?.DeepClone() ?? JValue.CreateNull(),
                    ["sortingOrder"] = renderer["sortingOrder"]?.DeepClone() ?? JValue.CreateNull(),
                    ["materialPath"] = renderer["material"]?["assetPath"]?.DeepClone() ?? JValue.CreateNull()
                }));
        }

        static JToken NormalizePrefab(JObject obj)
        {
            if (obj["prefab"] is not JObject prefab)
            {
                return JValue.CreateNull();
            }

            return new JObject
            {
                ["sourcePath"] = prefab["sourcePath"]?.DeepClone() ?? JValue.CreateNull(),
                ["sourceGuid"] = prefab["sourceGuid"]?.DeepClone() ?? JValue.CreateNull(),
                ["sourceLocalId"] = prefab["sourceLocalId"]?.DeepClone() ?? JValue.CreateNull()
            };
        }

        static JToken NormalizeLight2D(JObject obj)
        {
            return obj["light2D"]?.DeepClone() ?? new JArray();
        }

        sealed class ExportPathRef
        {
            public string ScenePath;
            public string Path;
            public string IndexedPath;
            public string Name;
            public long? ObjectId;
        }

        sealed class DuplicatePathSummary
        {
            public int duplicateGroupCount { get; set; }
            public int duplicateObjectCount { get; set; }
            public DuplicatePathGroup[] topDuplicateGroups { get; set; } = Array.Empty<DuplicatePathGroup>();
        }

        sealed class DuplicatePathGroup
        {
            public string scenePath { get; set; }
            public string path { get; set; }
            public int count { get; set; }
            public string[] sampleNames { get; set; } = Array.Empty<string>();
            public long[] sampleObjectIds { get; set; } = Array.Empty<long>();
            public string[] sampleIndexedPaths { get; set; } = Array.Empty<string>();
        }
    }

    internal sealed class SceneHierarchyExportOptions
    {
        public bool IncludeInactive { get; set; }
        public bool IncludeComponents { get; set; }
        public bool IncludeRenderers { get; set; }
        public bool IncludePrefabInfo { get; set; }
        public bool IncludeLight2D { get; set; }
    }

    internal sealed class ExportFileInfo
    {
        public string absolutePath { get; set; }
        public string projectRelativePath { get; set; }
        public string format { get; set; }
        public long byteLength { get; set; }
    }

    internal static class SceneHierarchyExportUtility
    {
        public const string Schema = "unibridge.scene-hierarchy-export.v1";

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static List<Dictionary<string, object>> CollectObjects(IReadOnlyList<Scene> scenes, SceneHierarchyExportOptions options)
        {
            var objects = new List<Dictionary<string, object>>();
            var traversalIndex = 0;

            foreach (var scene in scenes)
            {
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects().OrderBy(go => go.transform.GetSiblingIndex()))
                {
                    Traverse(root, null, 0, options, objects, ref traversalIndex);
                }
            }

            return objects;
        }

        static void Traverse(
            GameObject gameObject,
            GameObject parent,
            int depth,
            SceneHierarchyExportOptions options,
            List<Dictionary<string, object>> objects,
            ref int traversalIndex)
        {
            if (gameObject == null)
            {
                return;
            }

            if (!options.IncludeInactive && !gameObject.activeInHierarchy)
            {
                return;
            }

            objects.Add(SerializeGameObject(gameObject, parent, depth, traversalIndex++, options));

            var transform = gameObject.transform;
            var childCount = transform.childCount;
            for (var i = 0; i < childCount; i++)
            {
                Traverse(transform.GetChild(i).gameObject, gameObject, depth + 1, options, objects, ref traversalIndex);
            }
        }

        public static Dictionary<string, object> SerializeGameObject(
            GameObject gameObject,
            GameObject parent,
            int depth,
            int traversalIndex,
            SceneHierarchyExportOptions options)
        {
            var transform = gameObject.transform;
            var components = gameObject.GetComponents<Component>();
            var parentTransform = transform.parent;
            var parentGameObject = parentTransform != null ? parentTransform.gameObject : parent;

            var result = new Dictionary<string, object>
            {
                ["traversalIndex"] = traversalIndex,
                ["depth"] = depth,
                ["objectId"] = UnityApiAdapter.GetObjectId(gameObject),
                ["instanceId"] = UnityApiAdapter.GetObjectId(gameObject),
                ["name"] = gameObject.name,
                ["path"] = "/" + SceneObjectLocator.GetHierarchyPath(gameObject),
                ["indexedPath"] = BuildIndexedPath(gameObject),
                ["parentObjectId"] = parentGameObject != null ? UnityApiAdapter.GetObjectId(parentGameObject) : (long?)null,
                ["parentPath"] = parentGameObject != null ? "/" + SceneObjectLocator.GetHierarchyPath(parentGameObject) : null,
                ["sceneName"] = gameObject.scene.name,
                ["scenePath"] = gameObject.scene.path,
                ["siblingIndex"] = transform.GetSiblingIndex(),
                ["childCount"] = transform.childCount,
                ["activeSelf"] = gameObject.activeSelf,
                ["activeInHierarchy"] = gameObject.activeInHierarchy,
                ["tag"] = SafeTag(gameObject),
                ["layer"] = gameObject.layer,
                ["layerName"] = LayerMask.LayerToName(gameObject.layer),
                ["transform"] = SerializeTransform(transform),
                ["missingScripts"] = components.Count(component => component == null)
            };

            if (options.IncludeComponents)
            {
                result["components"] = SerializeComponents(components);
            }

            if (options.IncludeRenderers)
            {
                result["renderers"] = gameObject.GetComponents<Renderer>()
                    .Where(renderer => renderer != null)
                    .Select(SerializeRenderer)
                    .ToArray();
            }

            if (options.IncludePrefabInfo)
            {
                result["prefab"] = SerializePrefab(gameObject);
            }

            if (options.IncludeLight2D)
            {
                var light2D = SerializeLight2D(components);
                if (light2D.Length > 0)
                {
                    result["light2D"] = light2D;
                }
            }

            return result;
        }

        public static object SerializeScene(Scene scene)
        {
            return new
            {
                name = scene.name,
                path = scene.path,
                buildIndex = scene.buildIndex,
                isLoaded = scene.isLoaded,
                isDirty = scene.isDirty,
                rootCount = scene.IsValid() && scene.isLoaded ? scene.rootCount : 0
            };
        }

        public static object BuildProjectInfo()
        {
            return new
            {
                name = Application.productName,
                root = NormalizePath(ProjectRoot),
                assetsPath = NormalizePath(Application.dataPath),
                unityVersion = Application.unityVersion
            };
        }

        public static string BuildSceneKey(IReadOnlyList<Scene> scenes)
        {
            var names = scenes
                .Where(scene => scene.IsValid())
                .Select(scene => string.IsNullOrWhiteSpace(scene.name) ? "Untitled" : scene.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
            return names.Length == 0 ? "LoadedScenes" : string.Join("_", names);
        }

        public static ExportFileInfo WriteExportFile(
            Dictionary<string, object> exportDocument,
            SceneHierarchyExportFormat format,
            string sceneKey)
        {
            var directory = Path.Combine(ProjectRoot, "Library", "UniBridge", "SceneHierarchyExports");
            Directory.CreateDirectory(directory);

            var safeKey = SanitizeFileName(sceneKey);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var extension = format == SceneHierarchyExportFormat.Jsonl ? ".jsonl" : ".json";
            var path = Path.Combine(directory, $"{safeKey}_{timestamp}{extension}");

            if (format == SceneHierarchyExportFormat.Jsonl)
            {
                WriteJsonLines(path, exportDocument);
            }
            else
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(exportDocument, Formatting.Indented));
            }

            var info = new FileInfo(path);
            return new ExportFileInfo
            {
                absolutePath = NormalizePath(info.FullName),
                projectRelativePath = NormalizePath(Path.GetRelativePath(ProjectRoot, info.FullName)),
                format = format.ToString().ToLowerInvariant(),
                byteLength = info.Length
            };
        }

        static void WriteJsonLines(string path, Dictionary<string, object> exportDocument)
        {
            using var writer = new StreamWriter(path);
            var metadata = new JObject
            {
                ["recordType"] = "metadata",
                ["schema"] = Schema,
                ["createdUtc"] = exportDocument.TryGetValue("createdUtc", out var created) ? JToken.FromObject(created) : JValue.CreateNull(),
                ["project"] = JToken.FromObject(exportDocument["project"]),
                ["unityVersion"] = Application.unityVersion,
                ["scenes"] = JToken.FromObject(exportDocument["scenes"]),
                ["stableOrder"] = JToken.FromObject(exportDocument["stableOrder"]),
                ["include"] = JToken.FromObject(exportDocument["include"]),
                ["summary"] = exportDocument.TryGetValue("summary", out var summary) ? JToken.FromObject(summary) : JValue.CreateNull(),
                ["totalObjects"] = JToken.FromObject(exportDocument["totalObjects"])
            };
            writer.WriteLine(metadata.ToString(Formatting.None));

            if (exportDocument["objects"] is IEnumerable<Dictionary<string, object>> objects)
            {
                foreach (var obj in objects)
                {
                    var line = JObject.FromObject(obj);
                    line["recordType"] = "object";
                    writer.WriteLine(line.ToString(Formatting.None));
                }
            }
        }

        public static List<JObject> ReadExportObjects(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Scene hierarchy export file not found.", path);
            }

            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                return File.ReadLines(path)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => JObject.Parse(line))
                    .Where(obj => string.Equals(obj["recordType"]?.ToString(), "object", StringComparison.OrdinalIgnoreCase) ||
                                  obj["recordType"] == null)
                    .ToList();
            }

            var root = JToken.Parse(File.ReadAllText(path));
            if (root is JArray array)
            {
                return array.OfType<JObject>().ToList();
            }

            if (root is JObject obj && obj["objects"] is JArray objects)
            {
                return objects.OfType<JObject>().ToList();
            }

            return new List<JObject>();
        }

        public static string ResolveFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
            {
                return normalized;
            }

            return Path.GetFullPath(Path.Combine(ProjectRoot, normalized));
        }

        static object[] SerializeComponents(Component[] components)
        {
            var result = new List<object>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                {
                    result.Add(new
                    {
                        index = i,
                        missing = true,
                        type = (string)null,
                        typeName = (string)null
                    });
                    continue;
                }

                var type = component.GetType();
                result.Add(new
                {
                    index = i,
                    missing = false,
                    type = type.FullName ?? type.Name,
                    typeName = type.Name,
                    objectId = UnityApiAdapter.GetObjectId(component),
                    enabled = component is Behaviour behaviour ? behaviour.enabled : (bool?)null
                });
            }

            return result.ToArray();
        }

        static object SerializeRenderer(Renderer renderer)
        {
            var material = renderer.sharedMaterial;
            var spriteRenderer = renderer as SpriteRenderer;
            var particleRenderer = renderer as ParticleSystemRenderer;

            return new
            {
                rendererType = renderer.GetType().FullName ?? renderer.GetType().Name,
                typeName = renderer.GetType().Name,
                objectId = UnityApiAdapter.GetObjectId(renderer),
                enabled = renderer.enabled,
                sortingLayerName = renderer.sortingLayerName,
                sortingLayerId = renderer.sortingLayerID,
                sortingOrder = renderer.sortingOrder,
                material = ToObjectReference(material),
                materials = renderer.sharedMaterials?.Select(ToObjectReference).ToArray() ?? Array.Empty<object>(),
                bounds = SerializeBounds(renderer.bounds),
                sprite = spriteRenderer != null ? ToObjectReference(spriteRenderer.sprite) : null,
                particleRenderMode = particleRenderer != null ? particleRenderer.renderMode.ToString() : null
            };
        }

        static object[] SerializeLight2D(Component[] components)
        {
            return components
                .Where(component => component != null && IsLight2D(component.GetType()))
                .Select(component =>
                {
                    var layerIds = ReadLight2DSortingLayerIds(component);
                    return new
                    {
                        objectId = UnityApiAdapter.GetObjectId(component),
                        type = component.GetType().FullName ?? component.GetType().Name,
                        typeName = component.GetType().Name,
                        enabled = component is Behaviour behaviour ? behaviour.enabled : (bool?)null,
                        applyToSortingLayers = layerIds.Select(id => new
                        {
                            id,
                            name = SortingLayer.IDToName(id)
                        }).ToArray()
                    };
                })
                .ToArray();
        }

        static bool IsLight2D(Type type)
        {
            return type != null &&
                   (string.Equals(type.Name, "Light2D", StringComparison.Ordinal) ||
                    string.Equals(type.FullName, "UnityEngine.Rendering.Universal.Light2D", StringComparison.Ordinal));
        }

        static int[] ReadLight2DSortingLayerIds(Component component)
        {
            try
            {
                var property = component.GetType().GetProperty("applyToSortingLayers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var value = property?.GetValue(component);
                if (value is int[] ids)
                {
                    return ids;
                }
            }
            catch
            {
                // Fall back to SerializedObject below.
            }

            try
            {
                using var serializedObject = new SerializedObject(component);
                var property = serializedObject.FindProperty("m_ApplyToSortingLayers");
                if (property != null && property.isArray)
                {
                    var values = new List<int>();
                    for (var i = 0; i < property.arraySize; i++)
                    {
                        values.Add(property.GetArrayElementAtIndex(i).intValue);
                    }

                    return values.ToArray();
                }
            }
            catch
            {
                // Optional URP internals vary by version; absence is reported as an empty layer list.
            }

            return Array.Empty<int>();
        }

        static object SerializePrefab(GameObject gameObject)
        {
            try
            {
                var assetType = PrefabUtility.GetPrefabAssetType(gameObject);
                var instanceStatus = PrefabUtility.GetPrefabInstanceStatus(gameObject);
                var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
                var nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
                var corresponding = PrefabUtility.GetCorrespondingObjectFromSource(gameObject);

                return new
                {
                    assetType = assetType.ToString(),
                    instanceStatus = instanceStatus.ToString(),
                    sourcePath = string.IsNullOrWhiteSpace(sourcePath) ? null : sourcePath,
                    sourceGuid = string.IsNullOrWhiteSpace(sourcePath) ? null : AssetDatabase.AssetPathToGUID(sourcePath),
                    sourceLocalId = GetLocalIdentifier(corresponding),
                    isPartOfPrefabInstance = PrefabUtility.IsPartOfPrefabInstance(gameObject),
                    isAnyPrefabInstanceRoot = PrefabUtility.IsAnyPrefabInstanceRoot(gameObject),
                    nearestInstanceRoot = nearestRoot != null ? new
                    {
                        name = nearestRoot.name,
                        objectId = UnityApiAdapter.GetObjectId(nearestRoot),
                        path = "/" + SceneObjectLocator.GetHierarchyPath(nearestRoot)
                    } : null
                };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        static string GetLocalIdentifier(Object obj)
        {
            if (obj == null)
            {
                return null;
            }

            try
            {
                var method = typeof(Unsupported).GetMethod(
                    "GetLocalIdentifierInFileForPersistentObject",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var value = method?.Invoke(null, new object[] { obj });
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        static object SerializeTransform(Transform transform)
        {
            return new
            {
                localPosition = SerializeVector3(transform.localPosition),
                worldPosition = SerializeVector3(transform.position),
                localRotationEuler = SerializeVector3(transform.localEulerAngles),
                worldRotationEuler = SerializeVector3(transform.eulerAngles),
                localScale = SerializeVector3(transform.localScale),
                lossyScale = SerializeVector3(transform.lossyScale)
            };
        }

        static string BuildIndexedPath(GameObject gameObject)
        {
            var parts = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                parts.Push($"{current.name}[{current.GetSiblingIndex()}]");
                current = current.parent;
            }

            return "/" + string.Join("/", parts);
        }

        static object ToObjectReference(Object obj)
        {
            if (obj == null)
            {
                return null;
            }

            var path = AssetDatabase.GetAssetPath(obj);
            return new
            {
                name = obj.name,
                type = obj.GetType().FullName ?? obj.GetType().Name,
                objectId = UnityApiAdapter.GetObjectId(obj),
                assetPath = string.IsNullOrWhiteSpace(path) ? null : path,
                guid = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.AssetPathToGUID(path)
            };
        }

        static object SerializeBounds(Bounds bounds)
        {
            return new
            {
                center = SerializeVector3(bounds.center),
                size = SerializeVector3(bounds.size),
                min = SerializeVector3(bounds.min),
                max = SerializeVector3(bounds.max)
            };
        }

        static object SerializeVector3(Vector3 value)
        {
            return new { x = value.x, y = value.y, z = value.z };
        }

        static string SafeTag(GameObject gameObject)
        {
            try { return gameObject != null ? gameObject.tag : "Untagged"; }
            catch { return "Untagged"; }
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/');
        }

        static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "SceneHierarchy";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            return new string(chars).Trim('_');
        }
    }
}
