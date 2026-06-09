#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read-only validation for additive scene registration workflows.
    /// </summary>
    public static class ValidateAdditiveSceneRegistration
    {
        const string ToolName = "UniBridge_ValidateAdditiveSceneRegistration";

        public const string Title = "Validate additive scene registration";

        public const string Description = @"Read-only validation for Unity additive scene setup and cloned scene registration.

Checks .unity/.meta GUIDs, metadata .asset/.meta GUIDs, scene-to-metadata GUID references, ProjectSettings/EditorBuildSettings.asset, scenesManager.prefab runtime entries, SceneBoundaries/SceneLoadingBoundaries/ScenePaddingBoundaries/ScenePaddingWideScreenExpansion consistency, stale template references, and optional neighbor scene sanity.

Search aliases: UniBridge Unity ValidateAdditiveSceneRegistration additive scene validation scene registration scenesManager SceneBoundaries SceneLoadingBoundaries ScenePaddingBoundaries ScenePaddingWideScreenExpansion BuildSettings metadata GUID darkness12 darkness13. This tool is read-only and safe in UniBridge_BatchActions.";

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "validation", "guide" }, EnabledByDefault = true, ExecutionPolicy = ToolExecutionPolicy.ReadOnly)]
        public static object HandleCommand(ValidateAdditiveSceneRegistrationParams parameters)
        {
            parameters ??= new ValidateAdditiveSceneRegistrationParams();

            var errors = new List<object>();
            var warnings = new List<object>();
            var info = new List<object>();
            var checks = new List<object>();
            var maxSamples = Math.Max(1, Math.Min(100, parameters.MaxSamples ?? 12));

            try
            {
                var scene = ResolveScene(parameters.ScenePath, parameters.SceneName, warnings);
                if (scene == null)
                {
                    Add(errors, "SCENE_NOT_FOUND", "ScenePath or SceneName did not resolve to a .unity asset.", new { parameters.ScenePath, parameters.SceneName });
                    return BuildResponse(false, "Additive scene registration validation failed.", null, null, null, errors, warnings, info, checks);
                }

                ValidateAssetExists(scene.Path, ".unity", "SCENE_ASSET", errors, checks);
                var sceneGuid = AssetDatabase.AssetPathToGUID(scene.Path);
                ValidateGuid(scene.Path, sceneGuid, "SCENE_GUID", errors, checks);

                var sceneText = ReadText(scene.Path, warnings);
                var metadata = ResolveMetadata(scene, parameters.MetadataAssetPath, warnings);
                string metadataGuid = null;
                string metadataText = null;
                object metadataSummary = null;

                if (metadata == null)
                {
                    Add(errors, "METADATA_NOT_FOUND", "Metadata asset was not found. Supply MetadataAssetPath or create a matching .asset beside the scene.", new { scene = scene.Path, parameters.MetadataAssetPath });
                }
                else
                {
                    ValidateAssetExists(metadata.Path, ".asset", "METADATA_ASSET", errors, checks);
                    metadataGuid = AssetDatabase.AssetPathToGUID(metadata.Path);
                    ValidateGuid(metadata.Path, metadataGuid, "METADATA_GUID", errors, checks);
                    metadataText = ReadText(metadata.Path, warnings);

                    if (parameters.CheckSceneReferencesMetadata != false)
                    {
                        var referencesMetadata = !string.IsNullOrWhiteSpace(metadataGuid) &&
                                                 !string.IsNullOrEmpty(sceneText) &&
                                                 sceneText.IndexOf(metadataGuid, StringComparison.OrdinalIgnoreCase) >= 0;
                        AddCheck(checks, "SCENE_REFERENCES_METADATA", referencesMetadata, new { scene = scene.Path, metadata = metadata.Path, metadataGuid });
                        if (!referencesMetadata)
                            Add(errors, "SCENE_METADATA_REFERENCE_MISSING", "Scene YAML does not reference the metadata asset GUID.", new { scene = scene.Path, metadata = metadata.Path, metadataGuid });
                    }

                    if (parameters.CheckBoundaries != false)
                    {
                        metadataSummary = ValidateMetadataBoundaries(metadata.Path, metadataText, errors, warnings, info, checks, maxSamples);
                    }
                }

                var buildSettings = ValidateBuildSettings(scene.Path, sceneGuid, parameters.RequireBuildSettingsEntry != false, errors, warnings, checks);

                object scenesManager = null;
                if (parameters.CheckScenesManager != false)
                {
                    scenesManager = ValidateScenesManager(scene.Name, parameters.ScenesManagerPrefabPath, metadataGuid, errors, warnings, info, checks);
                }

                object staleReferences = null;
                if (parameters.CheckStaleReferences != false)
                {
                    staleReferences = ValidateStaleReferences(parameters, scene, sceneText, metadata, metadataText, scenesManager, errors, info, maxSamples);
                }

                object neighborSummary = null;
                if (parameters.CheckReciprocalNeighbors == true)
                {
                    neighborSummary = ValidateNeighborScenes(parameters.NeighborScenePaths, scene.Path, errors, warnings, info, maxSamples);
                }

                var data = new
                {
                    action = "ValidateAdditiveSceneRegistration",
                    readOnly = true,
                    summary = new
                    {
                        passed = errors.Count == 0,
                        errorCount = errors.Count,
                        warningCount = warnings.Count,
                        infoCount = info.Count,
                        scene = scene.Path,
                        metadata = metadata?.Path,
                        scenesManagerPath = ExtractScenesManagerPath(scenesManager),
                        buildSettingsPresent = buildSettings?.GetType().GetProperty("present")?.GetValue(buildSettings)
                    },
                    scene = new
                    {
                        name = scene.Name,
                        path = scene.Path,
                        guid = sceneGuid,
                        exists = File.Exists(ToAbsolutePath(scene.Path)),
                        metaExists = File.Exists(ToAbsolutePath(scene.Path + ".meta"))
                    },
                    metadata = metadata == null ? null : new
                    {
                        path = metadata.Path,
                        guid = metadataGuid,
                        exists = File.Exists(ToAbsolutePath(metadata.Path)),
                        metaExists = File.Exists(ToAbsolutePath(metadata.Path + ".meta")),
                        boundaries = metadataSummary
                    },
                    buildSettings,
                    scenesManager,
                    staleReferences,
                    neighbors = neighborSummary,
                    checks = checks.ToArray(),
                    errors = errors.ToArray(),
                    warnings = warnings.ToArray(),
                    info = info.ToArray()
                };

                return Response.Success(errors.Count == 0
                        ? "Additive scene registration validation passed."
                        : $"Additive scene registration validation found {errors.Count} error(s).",
                    data);
            }
            catch (Exception ex)
            {
                Add(errors, "VALIDATION_EXCEPTION", ex.Message, new { exceptionType = ex.GetType().FullName });
                return BuildResponse(false, $"Additive scene registration validation failed: {ex.Message}", null, null, null, errors, warnings, info, checks);
            }
        }

        static object BuildResponse(bool success, string message, object scene, object metadata, object scenesManager, List<object> errors, List<object> warnings, List<object> info, List<object> checks)
        {
            return success
                ? Response.Success(message, new { scene, metadata, scenesManager, errors, warnings, info, checks })
                : Response.Error(message, new { scene, metadata, scenesManager, errors, warnings, info, checks });
        }

        static SceneRef ResolveScene(string scenePath, string sceneName, List<object> warnings)
        {
            var normalizedPath = NormalizeAssetPath(scenePath);
            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                return new SceneRef(normalizedPath, Path.GetFileNameWithoutExtension(normalizedPath));
            }

            if (string.IsNullOrWhiteSpace(sceneName))
                return null;

            var exactName = Path.GetFileNameWithoutExtension(sceneName.Trim());
            var candidates = AssetDatabase.FindAssets(exactName)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), exactName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (candidates.Length > 1)
            {
                Add(warnings, "SCENE_NAME_AMBIGUOUS", "SceneName matched multiple .unity assets. Using the first result.", new { sceneName = exactName, candidates = candidates.Take(8).ToArray() });
            }

            return candidates.Length == 0 ? null : new SceneRef(candidates[0], Path.GetFileNameWithoutExtension(candidates[0]));
        }

        static AssetRef ResolveMetadata(SceneRef scene, string metadataAssetPath, List<object> warnings)
        {
            var normalized = NormalizeAssetPath(metadataAssetPath);
            if (!string.IsNullOrWhiteSpace(normalized))
                return new AssetRef(normalized);

            var besideScene = $"{Path.GetDirectoryName(scene.Path)?.Replace('\\', '/')}/{scene.Name}.asset";
            if (File.Exists(ToAbsolutePath(besideScene)))
                return new AssetRef(besideScene);

            var candidates = AssetDatabase.FindAssets(scene.Name)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), scene.Name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (candidates.Length > 1)
            {
                Add(warnings, "METADATA_NAME_AMBIGUOUS", "Scene metadata name matched multiple .asset files. Using the first result.", new { scene = scene.Path, candidates = candidates.Take(8).ToArray() });
            }

            return candidates.Length == 0 ? null : new AssetRef(candidates[0]);
        }

        static void ValidateAssetExists(string path, string expectedExtension, string checkName, List<object> errors, List<object> checks)
        {
            var exists = File.Exists(ToAbsolutePath(path));
            var metaExists = File.Exists(ToAbsolutePath(path + ".meta"));
            var hasExpectedExtension = path.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase);

            AddCheck(checks, checkName, exists && metaExists && hasExpectedExtension, new { path, exists, metaExists, expectedExtension, hasExpectedExtension });
            if (!hasExpectedExtension)
                Add(errors, $"{checkName}_BAD_EXTENSION", $"Asset path must end with {expectedExtension}.", new { path });
            if (!exists)
                Add(errors, $"{checkName}_MISSING", "Asset file does not exist.", new { path });
            if (!metaExists)
                Add(errors, $"{checkName}_META_MISSING", "Asset .meta file does not exist.", new { path = path + ".meta" });
        }

        static void ValidateGuid(string path, string guid, string checkName, List<object> errors, List<object> checks)
        {
            var ok = !string.IsNullOrWhiteSpace(guid);
            AddCheck(checks, checkName, ok, new { path, guid });
            if (!ok)
                Add(errors, $"{checkName}_MISSING", "Unity AssetDatabase did not return a GUID for this asset.", new { path });
        }

        static object ValidateBuildSettings(string scenePath, string sceneGuid, bool required, List<object> errors, List<object> warnings, List<object> checks)
        {
            var matching = EditorBuildSettings.scenes
                .Select((scene, index) => new
                {
                    index,
                    scene.path,
                    scene.enabled,
                    guid = AssetDatabase.AssetPathToGUID(scene.path),
                    pathMatches = string.Equals(scene.path, scenePath, StringComparison.OrdinalIgnoreCase),
                    guidMatches = !string.IsNullOrWhiteSpace(sceneGuid) && string.Equals(AssetDatabase.AssetPathToGUID(scene.path), sceneGuid, StringComparison.OrdinalIgnoreCase)
                })
                .Where(entry => entry.pathMatches || entry.guidMatches)
                .ToArray();

            var present = matching.Length > 0;
            AddCheck(checks, "BUILD_SETTINGS_ENTRY", required ? present : true, new { required, scenePath, sceneGuid, present, matches = matching });
            if (required && !present)
                Add(errors, "BUILD_SETTINGS_ENTRY_MISSING", "EditorBuildSettings does not contain this scene path/GUID.", new { scenePath, sceneGuid });
            else if (present && matching.All(entry => !entry.enabled))
                Add(warnings, "BUILD_SETTINGS_ENTRY_DISABLED", "EditorBuildSettings contains the scene, but all matching entries are disabled.", new { scenePath, matches = matching });

            return new
            {
                required,
                present,
                matches = matching
            };
        }

        static object ValidateMetadataBoundaries(string metadataPath, string metadataText, List<object> errors, List<object> warnings, List<object> info, List<object> checks, int maxSamples)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(metadataPath);
            var serialized = asset == null ? null : new SerializedObject(asset);

            var sceneBoundaries = ReadBoundaryList(serialized, metadataText, "SceneBoundaries", maxSamples);
            var loadingBoundaries = ReadBoundaryList(serialized, metadataText, "SceneLoadingBoundaries", maxSamples);
            var paddingBoundaries = ReadBoundaryList(serialized, metadataText, "ScenePaddingBoundaries", maxSamples);
            var wideExpansions = ReadBoundaryList(serialized, metadataText, "ScenePaddingWideScreenExpansion", maxSamples, validateRects: false);

            AddCheck(checks, "SCENE_BOUNDARIES_PRESENT", sceneBoundaries.count > 0, new { metadataPath, sceneBoundaries.count });
            if (sceneBoundaries.count == 0)
                Add(errors, "SCENE_BOUNDARIES_EMPTY", "SceneBoundaries is empty or missing.", new { metadataPath });

            AddCheck(checks, "SCENE_LOADING_BOUNDARIES_PRESENT", loadingBoundaries.count > 0, new { metadataPath, loadingBoundaries.count });
            if (loadingBoundaries.count == 0)
                Add(warnings, "SCENE_LOADING_BOUNDARIES_EMPTY", "SceneLoadingBoundaries is empty or missing. This may be valid for background/static scenes, but additive gameplay scenes usually need loading zones.", new { metadataPath });

            AddCheck(checks, "PADDING_EXPANSION_COUNT_MATCH", paddingBoundaries.count == wideExpansions.count, new { metadataPath, paddingBoundaries = paddingBoundaries.count, wideScreenExpansion = wideExpansions.count });
            if (paddingBoundaries.count != wideExpansions.count)
                Add(errors, "PADDING_EXPANSION_COUNT_MISMATCH", "ScenePaddingWideScreenExpansion count must match ScenePaddingBoundaries count.", new { metadataPath, paddingBoundaries = paddingBoundaries.count, wideScreenExpansion = wideExpansions.count });

            foreach (var invalid in sceneBoundaries.invalid.Concat(loadingBoundaries.invalid).Concat(paddingBoundaries.invalid).Take(maxSamples))
            {
                Add(errors, "INVALID_BOUNDARY_RECT", "Boundary rect has non-positive or non-finite size.", invalid);
            }

            Add(info, "BOUNDARY_COUNTS", "Read scene metadata boundary counts.", new { metadataPath, sceneBoundaries = sceneBoundaries.count, loadingBoundaries = loadingBoundaries.count, paddingBoundaries = paddingBoundaries.count, wideScreenExpansion = wideExpansions.count });

            return new
            {
                sceneBoundaries,
                sceneLoadingBoundaries = loadingBoundaries,
                scenePaddingBoundaries = paddingBoundaries,
                scenePaddingWideScreenExpansion = wideExpansions
            };
        }

        static BoundaryReadResult ReadBoundaryList(SerializedObject serialized, string yaml, string propertyName, int maxSamples, bool validateRects = true)
        {
            var result = new BoundaryReadResult { name = propertyName, count = 0, source = "missing" };
            var prop = serialized?.FindProperty(propertyName);
            if (prop != null && prop.isArray)
            {
                result.source = "SerializedObject";
                result.count = prop.arraySize;
                result.samples = Enumerable.Range(0, Math.Min(maxSamples, prop.arraySize))
                    .Select(index => ReadPropertySample(prop.GetArrayElementAtIndex(index), index, validateRects, result.invalid))
                    .ToArray();
                return result;
            }

            if (!string.IsNullOrEmpty(yaml))
            {
                result.source = "YAML";
                result.count = CountYamlListItems(yaml, propertyName);
            }

            result.samples = Array.Empty<object>();
            return result;
        }

        static object ReadPropertySample(SerializedProperty element, int index, bool validateRect, List<object> invalid)
        {
            if (element == null)
                return new { index, value = (object)null };

            if (element.propertyType == SerializedPropertyType.Float)
                return new { index, value = element.floatValue };

            if (element.propertyType == SerializedPropertyType.Rect)
            {
                var rect = element.rectValue;
                if (validateRect && (!IsFinite(rect.x) || !IsFinite(rect.y) || !IsFinite(rect.width) || !IsFinite(rect.height) || rect.width <= 0f || rect.height <= 0f))
                    invalid.Add(new { index, x = rect.x, y = rect.y, width = rect.width, height = rect.height });
                return new { index, x = rect.x, y = rect.y, width = rect.width, height = rect.height };
            }

            var x = element.FindPropertyRelative("x");
            var y = element.FindPropertyRelative("y");
            var width = element.FindPropertyRelative("width");
            var height = element.FindPropertyRelative("height");
            if (x != null && y != null && width != null && height != null)
            {
                var sample = new { index, x = x.floatValue, y = y.floatValue, width = width.floatValue, height = height.floatValue };
                if (validateRect && (!IsFinite(sample.x) || !IsFinite(sample.y) || !IsFinite(sample.width) || !IsFinite(sample.height) || sample.width <= 0f || sample.height <= 0f))
                    invalid.Add(sample);
                return sample;
            }

            return new { index, propertyType = element.propertyType.ToString(), value = element.displayName };
        }

        static object ValidateScenesManager(string sceneName, string scenesManagerPrefabPath, string metadataGuid, List<object> errors, List<object> warnings, List<object> info, List<object> checks)
        {
            var path = NormalizeAssetPath(scenesManagerPrefabPath);
            if (string.IsNullOrWhiteSpace(path))
                path = FindScenesManagerPrefab(warnings);

            if (string.IsNullOrWhiteSpace(path))
            {
                Add(errors, "SCENES_MANAGER_PREFAB_NOT_FOUND", "scenesManager prefab was not found. Supply ScenesManagerPrefabPath.", null);
                AddCheck(checks, "SCENES_MANAGER_ENTRY", false, new { sceneName });
                return new { path = (string)null, found = false, entryFound = false };
            }

            var exists = File.Exists(ToAbsolutePath(path));
            var text = ReadText(path, warnings);
            var block = ExtractSceneManagerEntry(text, sceneName);
            var entryFound = !string.IsNullOrWhiteSpace(block);
            AddCheck(checks, "SCENES_MANAGER_ENTRY", exists && entryFound, new { path, exists, sceneName, entryFound });
            if (!exists)
                Add(errors, "SCENES_MANAGER_PREFAB_MISSING", "scenesManager prefab path does not exist.", new { path });
            if (!entryFound)
                Add(errors, "SCENES_MANAGER_ENTRY_MISSING", "scenesManager.prefab does not contain a runtime entry for this scene.", new { path, sceneName });

            var boundaryCounts = entryFound
                ? new
                {
                    sceneLoadingBoundaries = CountYamlListItems(block, "SceneLoadingBoundaries"),
                    sceneBoundaries = CountYamlListItems(block, "SceneBoundaries"),
                    scenePaddingBoundaries = CountYamlListItems(block, "ScenePaddingBoundaries"),
                    scenePaddingWideScreenExpansion = CountYamlListItems(block, "ScenePaddingWideScreenExpansion")
                }
                : null;

            if (boundaryCounts != null && boundaryCounts.scenePaddingBoundaries != boundaryCounts.scenePaddingWideScreenExpansion)
                Add(errors, "SCENES_MANAGER_PADDING_COUNT_MISMATCH", "scenesManager runtime entry padding expansion count does not match padding boundary count.", new { path, sceneName, boundaryCounts });

            if (!string.IsNullOrWhiteSpace(metadataGuid) && !string.IsNullOrEmpty(block) && block.IndexOf(metadataGuid, StringComparison.OrdinalIgnoreCase) < 0)
                Add(info, "SCENES_MANAGER_ENTRY_EMBEDDED", "scenesManager runtime entry does not directly reference the metadata GUID; it appears to store embedded runtime metadata values.", new { path, sceneName, metadataGuid });

            return new
            {
                path,
                found = exists,
                entryFound,
                boundaryCounts,
                entryPreview = entryFound ? Truncate(block.Trim(), 1200) : null
            };
        }

        static object ValidateStaleReferences(ValidateAdditiveSceneRegistrationParams parameters, SceneRef scene, string sceneText, AssetRef metadata, string metadataText, object scenesManager, List<object> errors, List<object> info, int maxSamples)
        {
            var candidates = new[]
                {
                    new { kind = "TemplateSceneName", value = parameters.TemplateSceneName },
                    new { kind = "TemplateSceneGuid", value = parameters.TemplateSceneGuid },
                    new { kind = "TemplateMetadataGuid", value = parameters.TemplateMetadataGuid },
                    new { kind = "OldSceneName", value = parameters.OldSceneName }
                }
                .Where(item => !string.IsNullOrWhiteSpace(item.value))
                .ToArray();

            if (candidates.Length == 0)
            {
                Add(info, "STALE_REFERENCE_CHECK_SKIPPED", "No TemplateSceneName, TemplateSceneGuid, TemplateMetadataGuid, or OldSceneName was supplied, so stale reference scanning was skipped.", null);
                return new { checkedCandidates = 0, matches = Array.Empty<object>(), skipped = true };
            }

            var sources = new List<(string label, string path, string text)>
            {
                ("scene", scene.Path, sceneText),
                ("metadata", metadata?.Path, metadataText)
            };

            var scenesManagerPath = ExtractScenesManagerPath(scenesManager);
            if (!string.IsNullOrWhiteSpace(scenesManagerPath))
            {
                var scenesManagerText = ReadText(scenesManagerPath, new List<object>());
                var targetEntry = ExtractSceneManagerEntry(scenesManagerText, scene.Name);
                if (!string.IsNullOrWhiteSpace(targetEntry))
                {
                    sources.Add(($"scenesManager:{scene.Name}", scenesManagerPath, targetEntry));
                    Add(info, "STALE_REFERENCE_SCENES_MANAGER_SCOPE", "Stale reference scan is scoped to the target scenesManager entry, not the whole prefab.", new { scene = scene.Name, scenesManagerPath });
                }
                else
                {
                    Add(info, "STALE_REFERENCE_SCENES_MANAGER_SCOPE_SKIPPED", "scenesManager target entry was not found, so stale reference scan skipped scenesManager content instead of scanning the whole prefab.", new { scene = scene.Name, scenesManagerPath });
                }
            }

            var matches = new List<object>();
            foreach (var candidate in candidates)
            {
                foreach (var source in sources.Where(source => !string.IsNullOrEmpty(source.text)))
                {
                    var lineMatches = FindLineMatches(source.text, candidate.value, maxSamples);
                    if (lineMatches.Length == 0)
                        continue;
                    var match = new { candidate.kind, candidate.value, source = source.label, source.path, lineMatches };
                    matches.Add(match);
                    Add(errors, "STALE_REFERENCE_FOUND", "A supplied old/template name or GUID is still present.", match);
                }
            }

            return new
            {
                checkedCandidates = candidates.Length,
                candidates,
                matchCount = matches.Count,
                matches = matches.ToArray()
            };
        }

        static object ValidateNeighborScenes(string[] neighborScenePaths, string currentScenePath, List<object> errors, List<object> warnings, List<object> info, int maxSamples)
        {
            if (neighborScenePaths == null || neighborScenePaths.Length == 0)
            {
                Add(info, "NEIGHBOR_CHECK_SKIPPED", "CheckReciprocalNeighbors=true, but no NeighborScenePaths were supplied.", null);
                return new { checkedNeighbors = 0, skipped = true };
            }

            var neighbors = new List<object>();
            foreach (var raw in neighborScenePaths.Take(maxSamples))
            {
                var neighbor = ResolveScene(raw, raw, warnings);
                if (neighbor == null)
                {
                    Add(errors, "NEIGHBOR_SCENE_NOT_FOUND", "Neighbor scene path/name did not resolve.", new { neighbor = raw, currentScenePath });
                    neighbors.Add(new { requested = raw, found = false });
                    continue;
                }

                var metadata = ResolveMetadata(neighbor, null, warnings);
                var metadataText = metadata == null ? null : ReadText(metadata.Path, warnings);
                var boundaryCounts = metadata == null
                    ? null
                    : new
                    {
                        sceneLoadingBoundaries = CountYamlListItems(metadataText, "SceneLoadingBoundaries"),
                        sceneBoundaries = CountYamlListItems(metadataText, "SceneBoundaries"),
                        scenePaddingBoundaries = CountYamlListItems(metadataText, "ScenePaddingBoundaries"),
                        scenePaddingWideScreenExpansion = CountYamlListItems(metadataText, "ScenePaddingWideScreenExpansion")
                    };

                if (metadata == null)
                    Add(errors, "NEIGHBOR_METADATA_NOT_FOUND", "Neighbor metadata asset was not found.", new { neighbor = neighbor.Path });
                else if (boundaryCounts.scenePaddingBoundaries != boundaryCounts.scenePaddingWideScreenExpansion)
                    Add(errors, "NEIGHBOR_PADDING_COUNT_MISMATCH", "Neighbor padding expansion count does not match padding boundary count.", new { neighbor = neighbor.Path, metadata = metadata.Path, boundaryCounts });

                neighbors.Add(new
                {
                    requested = raw,
                    found = true,
                    path = neighbor.Path,
                    metadata = metadata?.Path,
                    boundaryCounts
                });
            }

            return new
            {
                checkedNeighbors = neighbors.Count,
                neighbors = neighbors.ToArray(),
                note = "This is a lightweight neighbor sanity check. Directional reciprocal zone pairing still needs project-specific semantics."
            };
        }

        static string FindScenesManagerPrefab(List<object> warnings)
        {
            var candidates = AssetDatabase.FindAssets("scenesManager t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => string.Equals(Path.GetFileName(path), "scenesManager.prefab", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (candidates.Length > 1)
                Add(warnings, "SCENES_MANAGER_AMBIGUOUS", "Found multiple scenesManager prefab candidates. Using the first result.", new { candidates = candidates.Take(8).ToArray() });

            return candidates.FirstOrDefault();
        }

        static string ExtractSceneManagerEntry(string text, string sceneName)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(sceneName))
                return null;

            var lines = SplitLines(text);
            var pattern = new Regex(@"^(\s*)-\s*Scene:\s*" + Regex.Escape(sceneName) + @"\s*$", RegexOptions.IgnoreCase);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = pattern.Match(lines[i]);
                if (!match.Success)
                    continue;

                var baseIndent = match.Groups[1].Value.Length;
                var end = lines.Length;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].Length == 0)
                        continue;
                    var indent = CountIndent(lines[j]);
                    if (indent == baseIndent && Regex.IsMatch(lines[j], @"^\s*-\s*Scene:\s*", RegexOptions.IgnoreCase))
                    {
                        end = j;
                        break;
                    }
                }

                return string.Join("\n", lines.Skip(i).Take(end - i));
            }

            return null;
        }

        static int CountYamlListItems(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(key))
                return 0;

            var lines = SplitLines(text);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(key + ":", StringComparison.Ordinal))
                    continue;

                if (trimmed.EndsWith("[]", StringComparison.Ordinal))
                    return 0;

                var baseIndent = CountIndent(line);
                var count = 0;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var current = lines[j];
                    if (string.IsNullOrWhiteSpace(current))
                        continue;

                    var indent = CountIndent(current);
                    if (indent <= baseIndent)
                        break;

                    if (current.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                        count++;
                }

                return count;
            }

            return 0;
        }

        static object[] FindLineMatches(string text, string needle, int max)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(needle))
                return Array.Empty<object>();

            var lines = SplitLines(text);
            var matches = new List<object>();
            for (var i = 0; i < lines.Length && matches.Count < max; i++)
            {
                if (lines[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    matches.Add(new { line = i + 1, text = Truncate(lines[i].Trim(), 240) });
            }

            return matches.ToArray();
        }

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var value = path.Trim().Trim('"').Replace('\\', '/');
            if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                value = new Uri(value).LocalPath.Replace('\\', '/');
            }
            else if (value.StartsWith("project:/", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("project:/".Length).TrimStart('/');
            }

            var root = ProjectRoot();
            if (Path.IsPathRooted(value))
            {
                var full = Path.GetFullPath(value).Replace('\\', '/');
                if (full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                    value = full.Substring(root.Length + 1);
            }

            if (value.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase) || value.StartsWith("/Packages/", StringComparison.OrdinalIgnoreCase) || value.StartsWith("/ProjectSettings/", StringComparison.OrdinalIgnoreCase))
                value = value.TrimStart('/');

            return value;
        }

        static string ToAbsolutePath(string assetPath)
        {
            var normalized = NormalizeAssetPath(assetPath);
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            if (Path.IsPathRooted(normalized))
                return Path.GetFullPath(normalized);

            return Path.GetFullPath(Path.Combine(ProjectRoot(), normalized)).Replace('\\', '/');
        }

        static string ReadText(string assetPath, List<object> warnings)
        {
            try
            {
                var absolute = ToAbsolutePath(assetPath);
                return File.Exists(absolute) ? File.ReadAllText(absolute) : null;
            }
            catch (Exception ex)
            {
                Add(warnings, "READ_TEXT_FAILED", "Could not read asset text.", new { assetPath, message = ex.Message });
                return null;
            }
        }

        static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
        }

        static string[] SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        static int CountIndent(string line)
        {
            var count = 0;
            while (count < line.Length && line[count] == ' ')
                count++;
            return count;
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
                return text;
            return text.Substring(0, Math.Max(0, max - 3)) + "...";
        }

        static string ExtractScenesManagerPath(object scenesManager)
        {
            if (scenesManager == null)
                return null;
            var property = scenesManager.GetType().GetProperty("path");
            return property?.GetValue(scenesManager) as string;
        }

        static void Add(List<object> issues, string code, string message, object details)
        {
            issues.Add(new { code, message, details });
        }

        static void AddCheck(List<object> checks, string name, bool passed, object details)
        {
            checks.Add(new { name, passed, details });
        }

        sealed class SceneRef
        {
            public readonly string Path;
            public readonly string Name;

            public SceneRef(string path, string name)
            {
                Path = path;
                Name = name;
            }
        }

        sealed class AssetRef
        {
            public readonly string Path;

            public AssetRef(string path)
            {
                Path = path;
            }
        }

        sealed class BoundaryReadResult
        {
            public string name;
            public int count;
            public string source;
            public object[] samples = Array.Empty<object>();
            public readonly List<object> invalid = new();
        }
    }
}
