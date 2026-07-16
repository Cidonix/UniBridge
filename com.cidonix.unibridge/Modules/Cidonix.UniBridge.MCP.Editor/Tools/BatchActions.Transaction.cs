#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class BatchActions
    {
        sealed class BatchTransaction
        {
            const int MaxRollbackFiles = 600;
            const long MaxRollbackBytes = 50L * 1024L * 1024L;
            const long MaxRollbackSingleFileBytes = 10L * 1024L * 1024L;

            readonly List<RollbackRoot> roots = new();
            readonly List<string> warnings = new();
            readonly List<string> errors = new();

            public bool Active;
            public bool RollbackOnFailure;
            public bool RollbackAssets;
            public bool UseUndoGroup;
            public int UndoGroup = -1;
            public int CapturedFiles;
            public long CapturedBytes;
            public bool CaptureTruncated;

            public static BatchTransaction Begin(BatchOptions options, IReadOnlyList<BatchStep> steps)
            {
                var transaction = new BatchTransaction
                {
                    Active = !options.DryRun,
                    RollbackOnFailure = !options.DryRun && options.RollbackOnFailure,
                    RollbackAssets = !options.DryRun && options.RollbackOnFailure && options.RollbackAssets,
                    UseUndoGroup = !options.DryRun && options.UseUndoGroup
                };

                if (transaction.UseUndoGroup)
                {
                    Undo.IncrementCurrentGroup();
                    transaction.UndoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName(options.Name);
                }

                if (transaction.RollbackAssets)
                {
                    transaction.CaptureAssets(steps);
                }

                return transaction;
            }

            public static object BuildImpact(BatchOptions options, IReadOnlyList<BatchStep> steps)
            {
                var assetPaths = ExtractAssetPathCandidates(steps);
                foreach (var path in assetPaths.ToArray())
                    AddMissingAncestors(path, assetPaths);

                var projectSettings = ExtractProjectSettingsCandidates(steps);
                var sceneHints = ExtractSceneHints(steps, assetPaths);
                var sceneObjectReferences = ExtractSceneObjectReferences(steps)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Take(80)
                    .ToArray();

                var assets = assetPaths
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => BuildAssetImpact(path, steps))
                    .ToArray();

                var scenes = sceneHints
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(80)
                    .Select(path => new
                    {
                        path,
                        loaded = SceneObjectLocator.GetLoadedScenes(path).Count > 0,
                        active = string.Equals(SceneManager.GetActiveScene().path, path, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(SceneManager.GetActiveScene().name, path, StringComparison.OrdinalIgnoreCase)
                    })
                    .ToArray();

                var toolSummary = steps
                    .GroupBy(step => step.ToolName, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        tool = group.Key,
                        count = group.Count(),
                        actions = group.Select(StepAction).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
                    })
                    .ToArray();
                var stepPlans = steps.Select(BuildStepImpactPlan).ToArray();

                return new
                {
                    enabled = options.IncludeImpact,
                    dryRun = options.DryRun,
                    stepCount = steps.Count,
                    tools = toolSummary,
                    steps = stepPlans,
                    assets = new
                    {
                        count = assets.Length,
                        items = assets,
                        rollbackSnapshotPlanned = !options.DryRun && options.RollbackOnFailure && options.RollbackAssets
                    },
                    scenes = new
                    {
                        count = scenes.Length,
                        items = scenes
                    },
                    projectSettings = new
                    {
                        count = projectSettings.Count,
                        items = projectSettings.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
                    },
                    sceneObjectReferences,
                    notes = BuildImpactNotes(assets.Length, scenes.Length, projectSettings.Count, sceneObjectReferences.Length),
                    validationModel = new
                    {
                        mode = "central-plus-step-impact-hints",
                        note = "Each step reports its likely touched assets/settings/scene references. Tool-specific validation remains centralized and can be split into per-tool providers as tools grow."
                    }
                };
            }

            static object BuildStepImpactPlan(BatchStep step)
            {
                var single = new[] { step };
                var assetPaths = ExtractAssetPathCandidates(single)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(30)
                    .ToArray();
                var projectSettings = ExtractProjectSettingsCandidates(single)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(30)
                    .ToArray();
                var sceneObjectReferences = ExtractSceneObjectReferences(single)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Take(30)
                    .ToArray();

                return new
                {
                    index = step.Index,
                    id = step.Id,
                    description = step.Description,
                    tool = step.ToolName,
                    action = StepAction(step),
                    optional = step.Optional,
                    skipped = step.Skip,
                    validationProvider = ResolveValidationProviderName(step.ToolName),
                    likelyAssetPaths = assetPaths,
                    likelyProjectSettings = projectSettings,
                    sceneObjectReferences,
                    rollbackHint = BuildRollbackHint(step, assetPaths.Length)
                };
            }

            static string BuildRollbackHint(BatchStep step, int assetPathCount)
            {
                if (string.Equals(step?.ToolName, "UniBridge_ValidateScript", StringComparison.Ordinal))
                {
                    return "Read-only script validation; rollback/undo is not required for this step.";
                }

                return assetPathCount > 0
                    ? "Asset snapshot rollback can protect existing referenced paths and delete newly-created referenced paths when RollbackAssets=true."
                    : null;
            }

            static string ResolveValidationProviderName(string toolName)
            {
                if (string.IsNullOrWhiteSpace(toolName))
                    return null;

                var normalized = toolName.StartsWith("UniBridge_", StringComparison.Ordinal)
                    ? toolName.Substring("UniBridge_".Length)
                    : toolName;
                return $"{normalized}Validation";
            }

            public object Finish(bool batchSuccess, string reason)
            {
                if (!Active)
                {
                    return ToReport(triggered: false, completed: true, reason: "Dry-run transaction did not execute.");
                }

                if (!batchSuccess && RollbackOnFailure)
                {
                    return Rollback(reason);
                }

                if (UndoGroup >= 0)
                {
                    try
                    {
                        Undo.CollapseUndoOperations(UndoGroup);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Failed to collapse Undo group {UndoGroup}: {ex.Message}");
                    }
                }

                return ToReport(triggered: false, completed: true, reason: batchSuccess ? "Batch committed." : "Rollback disabled.");
            }

            object Rollback(string reason)
            {
                var undoReverted = false;
                if (UndoGroup >= 0)
                {
                    try
                    {
                        Undo.RevertAllDownToGroup(UndoGroup);
                        undoReverted = true;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to revert Undo group {UndoGroup}: {ex.Message}");
                    }
                }

                var restoredFiles = 0;
                var deletedCreatedRoots = 0;
                if (RollbackAssets)
                {
                    foreach (var root in roots.OrderByDescending(root => root.AbsolutePath.Length))
                    {
                        try
                        {
                            if (!root.Existed)
                            {
                                if (DeleteCreatedRoot(root))
                                {
                                    deletedCreatedRoots++;
                                }

                                continue;
                            }

                            restoredFiles += RestoreRoot(root);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Failed to restore '{root.AssetPath}': {ex.Message}");
                        }
                    }

                    try
                    {
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"AssetDatabase.Refresh after rollback failed: {ex.Message}");
                    }
                }

                return new
                {
                    enabled = RollbackOnFailure,
                    triggered = true,
                    completed = errors.Count == 0,
                    reason,
                    undoGroup = UndoGroup >= 0 ? UndoGroup : (int?)null,
                    undoReverted,
                    assetRollback = new
                    {
                        enabled = RollbackAssets,
                        roots = roots.Count,
                        capturedFiles = CapturedFiles,
                        capturedBytes = CapturedBytes,
                        captureTruncated = CaptureTruncated,
                        restoredFiles,
                        deletedCreatedRoots
                    },
                    warnings,
                    errors
                };
            }

            object ToReport(bool triggered, bool completed, string reason)
            {
                return new
                {
                    enabled = RollbackOnFailure,
                    triggered,
                    completed,
                    reason,
                    undoGroup = UndoGroup >= 0 ? UndoGroup : (int?)null,
                    assetRollback = new
                    {
                        enabled = RollbackAssets,
                        roots = roots.Count,
                        capturedFiles = CapturedFiles,
                        capturedBytes = CapturedBytes,
                        captureTruncated = CaptureTruncated
                    },
                    warnings,
                    errors
                };
            }

            void CaptureAssets(IReadOnlyList<BatchStep> steps)
            {
                var candidates = ExtractAssetPathCandidates(steps);
                foreach (var path in candidates.ToArray())
                {
                    AddMissingAncestors(path, candidates);
                }

                foreach (var path in candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (!TryGetAbsoluteProjectPath(path, out var absolutePath, out var normalizedPath, out var error))
                    {
                        warnings.Add(error);
                        continue;
                    }

                    var root = new RollbackRoot
                    {
                        AssetPath = normalizedPath,
                        AbsolutePath = absolutePath,
                        Existed = File.Exists(absolutePath) || Directory.Exists(absolutePath),
                        WasDirectory = Directory.Exists(absolutePath)
                    };

                    if (root.Existed)
                    {
                        if (root.WasDirectory)
                        {
                            CaptureDirectory(root);
                        }
                        else
                        {
                            CaptureFileIfExists(root, absolutePath);
                        }

                        CaptureFileIfExists(root, absolutePath + ".meta");
                    }

                    roots.Add(root);
                }
            }

            void CaptureDirectory(RollbackRoot root)
            {
                CaptureFileIfExists(root, root.AbsolutePath + ".meta");

                string[] files;
                try
                {
                    files = Directory.GetFiles(root.AbsolutePath, "*", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not enumerate '{root.AssetPath}' for rollback snapshot: {ex.Message}");
                    return;
                }

                foreach (var file in files)
                {
                    if (CaptureTruncated)
                    {
                        break;
                    }

                    CaptureFileIfExists(root, file);
                }
            }

            void CaptureFileIfExists(RollbackRoot root, string absolutePath)
            {
                if (!File.Exists(absolutePath) || root.Files.ContainsKey(absolutePath))
                {
                    return;
                }

                if (CapturedFiles >= MaxRollbackFiles)
                {
                    CaptureTruncated = true;
                    warnings.Add($"Rollback snapshot reached the file limit ({MaxRollbackFiles}). Some files will not be restored.");
                    return;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(absolutePath);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not inspect '{absolutePath}' for rollback snapshot: {ex.Message}");
                    return;
                }

                if (info.Length > MaxRollbackSingleFileBytes)
                {
                    warnings.Add($"Skipped large rollback file '{ToProjectDisplayPath(absolutePath)}' ({info.Length} bytes).");
                    return;
                }

                if (CapturedBytes + info.Length > MaxRollbackBytes)
                {
                    CaptureTruncated = true;
                    warnings.Add($"Rollback snapshot reached the byte limit ({MaxRollbackBytes}). Some files will not be restored.");
                    return;
                }

                try
                {
                    root.Files[absolutePath] = new RollbackFile
                    {
                        AbsolutePath = absolutePath,
                        Bytes = File.ReadAllBytes(absolutePath)
                    };
                    CapturedFiles++;
                    CapturedBytes += info.Length;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not capture '{ToProjectDisplayPath(absolutePath)}' for rollback: {ex.Message}");
                }
            }

            int RestoreRoot(RollbackRoot root)
            {
                var restored = 0;
                if (root.WasDirectory)
                {
                    Directory.CreateDirectory(root.AbsolutePath);

                    if (!CaptureTruncated && Directory.Exists(root.AbsolutePath))
                    {
                        foreach (var currentFile in Directory.GetFiles(root.AbsolutePath, "*", SearchOption.AllDirectories))
                        {
                            if (!root.Files.ContainsKey(currentFile))
                            {
                                TryDeleteFile(currentFile);
                            }
                        }
                    }
                }
                else if (Directory.Exists(root.AbsolutePath))
                {
                    Directory.Delete(root.AbsolutePath, recursive: true);
                }

                foreach (var file in root.Files.Values)
                {
                    var parent = Path.GetDirectoryName(file.AbsolutePath);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    File.WriteAllBytes(file.AbsolutePath, file.Bytes);
                    restored++;
                }

                if (!root.WasDirectory && !root.Files.ContainsKey(root.AbsolutePath) && File.Exists(root.AbsolutePath))
                {
                    TryDeleteFile(root.AbsolutePath);
                }

                return restored;
            }

            bool DeleteCreatedRoot(RollbackRoot root)
            {
                var deleted = false;
                if (Directory.Exists(root.AbsolutePath))
                {
                    Directory.Delete(root.AbsolutePath, recursive: true);
                    deleted = true;
                }
                else if (File.Exists(root.AbsolutePath))
                {
                    File.Delete(root.AbsolutePath);
                    deleted = true;
                }

                var metaPath = root.AbsolutePath + ".meta";
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                    deleted = true;
                }

                return deleted;
            }

            static void TryDeleteFile(string absolutePath)
            {
                try
                {
                    if (File.Exists(absolutePath))
                    {
                        File.Delete(absolutePath);
                    }
                }
                catch
                {
                    // Rollback continues and reports root-level failures through the caller.
                }
            }

            static HashSet<string> ExtractAssetPathCandidates(IReadOnlyList<BatchStep> steps)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (TryNormalizeAssetCandidate(scene.path, out var scenePath))
                    {
                        paths.Add(scenePath);
                    }
                }

                foreach (var step in steps)
                {
                    ExtractAssetPathCandidates(step.Parameters, paths);
                }

                paths.RemoveWhere(path => path.Equals("Assets", StringComparison.OrdinalIgnoreCase) || path.Equals("Packages", StringComparison.OrdinalIgnoreCase));
                return paths;
            }

            static void ExtractAssetPathCandidates(JToken token, HashSet<string> paths)
            {
                if (token == null)
                {
                    return;
                }

                if (token.Type == JTokenType.String)
                {
                    if (TryNormalizeAssetCandidate(token.Value<string>(), out var candidate))
                    {
                        paths.Add(candidate);
                    }

                    return;
                }

                foreach (var child in token.Children())
                {
                    ExtractAssetPathCandidates(child, paths);
                }
            }

            static HashSet<string> ExtractProjectSettingsCandidates(IReadOnlyList<BatchStep> steps)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var step in steps)
                    ExtractProjectSettingsCandidates(step.Parameters, paths);
                return paths;
            }

            static void ExtractProjectSettingsCandidates(JToken token, HashSet<string> paths)
            {
                if (token == null)
                    return;

                if (token.Type == JTokenType.String)
                {
                    if (TryNormalizeProjectSettingsCandidate(token.Value<string>(), out var candidate))
                    {
                        paths.Add(candidate);
                    }

                    return;
                }

                foreach (var child in token.Children())
                    ExtractProjectSettingsCandidates(child, paths);
            }

            static HashSet<string> ExtractSceneHints(IReadOnlyList<BatchStep> steps, HashSet<string> assetPaths)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!string.IsNullOrWhiteSpace(scene.path))
                        paths.Add(scene.path);
                }

                foreach (var assetPath in assetPaths)
                {
                    if (string.Equals(Path.GetExtension(assetPath), ".unity", StringComparison.OrdinalIgnoreCase))
                        paths.Add(assetPath);
                }

                foreach (var step in steps)
                    ExtractSceneHints(step.Parameters, paths);

                return paths;
            }

            static void ExtractSceneHints(JToken token, HashSet<string> paths)
            {
                if (token is JObject obj)
                {
                    foreach (var property in obj.Properties())
                    {
                        if (property.Value.Type == JTokenType.String &&
                            IsSceneHintKey(property.Name))
                        {
                            var rawValue = property.Value.Value<string>()?.Trim();
                            var value = TryNormalizeAssetCandidate(rawValue, out var assetPath)
                                ? assetPath
                                : NormalizeSceneHint(rawValue);
                            if (!string.IsNullOrWhiteSpace(value))
                                paths.Add(value);
                        }

                        ExtractSceneHints(property.Value, paths);
                    }

                    return;
                }

                if (token?.Type == JTokenType.String)
                {
                    if (TryNormalizeAssetCandidate(token.Value<string>(), out var value) &&
                        string.Equals(Path.GetExtension(value), ".unity", StringComparison.OrdinalIgnoreCase))
                    {
                        paths.Add(value);
                    }
                }
            }

            static IEnumerable<string> ExtractSceneObjectReferences(IReadOnlyList<BatchStep> steps)
            {
                foreach (var step in steps)
                foreach (var reference in ExtractSceneObjectReferences(step.Parameters))
                    yield return reference;
            }

            static IEnumerable<string> ExtractSceneObjectReferences(JToken token)
            {
                if (token == null)
                    yield break;

                if (token is JArray array)
                {
                    foreach (var item in array)
                    {
                        foreach (var nested in ExtractSceneObjectReferences(item))
                            yield return nested;
                    }

                    yield break;
                }

                if (token is not JObject obj)
                    yield break;

                if (TryReadSceneFindInstruction(obj, out var instructionReference))
                    yield return instructionReference;

                foreach (var property in obj.Properties())
                {
                    if (IsSceneReferenceKey(property.Name))
                    {
                        foreach (var reference in ExtractSceneReferenceValues(property.Value))
                            yield return reference;
                    }

                    foreach (var nested in ExtractSceneObjectReferences(property.Value))
                        yield return nested;
                }
            }

            static object BuildAssetImpact(string assetPath, IReadOnlyList<BatchStep> steps)
            {
                var exists = false;
                var kind = "missing";
                long? sizeBytes = null;
                if (TryGetAbsoluteProjectPath(assetPath, out var absolutePath, out var normalizedPath, out _))
                {
                    if (File.Exists(absolutePath))
                    {
                        exists = true;
                        kind = "file";
                        sizeBytes = new FileInfo(absolutePath).Length;
                    }
                    else if (Directory.Exists(absolutePath))
                    {
                        exists = true;
                        kind = "folder";
                    }
                }
                else
                {
                    normalizedPath = assetPath;
                }

                return new
                {
                    path = normalizedPath,
                    exists,
                    kind,
                    extension = Path.GetExtension(normalizedPath),
                    sizeBytes,
                    likelyIntent = InferPathIntents(steps, normalizedPath)
                };
            }

            static string[] InferPathIntents(IReadOnlyList<BatchStep> steps, string assetPath)
            {
                var intents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var step in steps)
                {
                    if (!ContainsPath(step.Parameters, assetPath))
                        continue;

                    if (string.Equals(step.ToolName, "UniBridge_ValidateScript", StringComparison.Ordinal))
                    {
                        intents.Add("read");
                        continue;
                    }

                    var action = NormalizeAction(StepAction(step));
                    if (action.Contains("delete") || action.Contains("remove"))
                        intents.Add("delete");
                    else if (action.Contains("move") || action.Contains("rename"))
                        intents.Add("moveOrRename");
                    else if (action.Contains("create") || action.Contains("update") || action.Contains("upsert") || action.Contains("import") || action.Contains("save"))
                        intents.Add("createOrModify");
                    else if (action.Contains("inspect") || action.Contains("capture") || action.Contains("search") || action.Contains("validate"))
                        intents.Add("read");
                    else
                        intents.Add("touch");
                }

                return intents.Count == 0 ? new[] { "referenced" } : intents.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            static bool ContainsPath(JToken token, string assetPath)
            {
                if (token == null)
                    return false;

                if (token.Type == JTokenType.String)
                    return TryNormalizeAssetCandidate(token.Value<string>(), out var candidate) &&
                           string.Equals(candidate, assetPath, StringComparison.OrdinalIgnoreCase);

                return token.Children().Any(child => ContainsPath(child, assetPath));
            }

            static string StepAction(BatchStep step)
            {
                if (step?.Parameters == null)
                    return null;
                return step.Parameters.TryGetValue("Action", StringComparison.OrdinalIgnoreCase, out var action) ? action?.ToString() : null;
            }

            static bool IsSceneHintKey(string key)
            {
                var normalized = NormalizeAction(key);
                return normalized is "scene" or "scenename" or "scenepath" or "targetscene" or "targetscenepath";
            }

            static bool IsSceneReferenceKey(string key)
            {
                var normalized = NormalizeAction(key);
                return normalized is "target" or "targets" or "parent" or "parents" or "sibling" or "siblings" or
                    "source" or "sourcetarget" or "connectedbodytarget" or "starttarget" or "endtarget" or "root" or "roots";
            }

            static IEnumerable<string> ExtractSceneReferenceValues(JToken token)
            {
                if (token == null)
                    yield break;

                if (token.Type == JTokenType.String)
                {
                    var value = token.Value<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value) && !TryNormalizeAssetCandidate(value, out _))
                        yield return value;
                    yield break;
                }

                if (token is JArray array)
                {
                    foreach (var item in array)
                    foreach (var nested in ExtractSceneReferenceValues(item))
                        yield return nested;
                    yield break;
                }

                if (token is JObject obj && TryReadSceneFindInstruction(obj, out var instructionReference))
                    yield return instructionReference;
            }

            static bool TryReadSceneFindInstruction(JObject obj, out string sceneReference)
            {
                sceneReference = null;
                if (obj == null || !obj.TryGetValue("find", StringComparison.OrdinalIgnoreCase, out var findToken) ||
                    findToken?.Type != JTokenType.String)
                {
                    return false;
                }

                var value = findToken.Value<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(value) || TryNormalizeAssetCandidate(value, out _))
                    return false;

                var method = obj.TryGetValue("method", StringComparison.OrdinalIgnoreCase, out var methodToken)
                    ? NormalizeAction(methodToken?.ToString())
                    : string.Empty;
                if (!string.IsNullOrEmpty(method) && method is not "bypath" and not "byname" and not "byid" and not "byidornameorpath")
                    return false;

                sceneReference = value;
                return true;
            }

            static string NormalizeAction(string value)
            {
                return (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
            }

            static string[] BuildImpactNotes(int assetCount, int sceneCount, int settingsCount, int objectReferenceCount)
            {
                var notes = new List<string>();
                if (assetCount == 0)
                    notes.Add("No explicit Assets/Packages paths were found in batch parameters.");
                if (sceneCount == 0)
                    notes.Add("No scene path hints were found; scene mutations may still target the active scene.");
                if (settingsCount > 0)
                    notes.Add("ProjectSettings paths are reported but are not covered by asset rollback snapshots.");
                if (objectReferenceCount > 0)
                    notes.Add("Scene object references are best-effort strings; validation still resolves the final objects per step.");
                return notes.ToArray();
            }

            static void AddMissingAncestors(string assetPath, HashSet<string> paths)
            {
                if (!TryGetAbsoluteProjectPath(assetPath, out var absolutePath, out _, out _))
                {
                    return;
                }

                if (File.Exists(absolutePath) || Directory.Exists(absolutePath))
                {
                    return;
                }

                var parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                while (IsAssetLikePath(parent) &&
                       !parent.Equals("Assets", StringComparison.OrdinalIgnoreCase) &&
                       !parent.Equals("Packages", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryGetAbsoluteProjectPath(parent, out var parentAbsolute, out var normalizedParent, out _))
                    {
                        break;
                    }

                    if (File.Exists(parentAbsolute) || Directory.Exists(parentAbsolute))
                    {
                        break;
                    }

                    paths.Add(normalizedParent);
                    parent = Path.GetDirectoryName(parent)?.Replace('\\', '/');
                }
            }

            static string NormalizeAssetPath(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                var text = value.Trim().Trim('"').Replace('\\', '/');
                if (text.Contains('\n') || text.Contains('\r'))
                {
                    return null;
                }

                if (HasInvalidPathCharacters(DecodePathForClassification(text)))
                    return null;

                try
                {
                    text = ProjectPathResolver.ToProjectRelativePath(text, assumeAssetRelative: false);
                }
                catch
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(text))
                    return null;

                if (text.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    return null;
                }

                return text.TrimEnd('/');
            }

            static bool TryNormalizeAssetCandidate(string value, out string assetPath)
            {
                assetPath = null;
                if (!LooksLikeProjectFileCandidate(value))
                    return false;

                var normalized = NormalizeAssetPath(value);
                if (!IsAssetLikePath(normalized))
                    return false;

                assetPath = normalized;
                return true;
            }

            static bool TryNormalizeProjectSettingsCandidate(string value, out string settingsPath)
            {
                settingsPath = null;
                if (!LooksLikeProjectFileCandidate(value))
                    return false;

                var normalized = NormalizeAssetPath(value);
                if (string.IsNullOrWhiteSpace(normalized) ||
                    !normalized.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                settingsPath = normalized;
                return true;
            }

            static bool LooksLikeProjectFileCandidate(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return false;

                var text = value.Trim().Trim('"').Replace('\\', '/');
                if (text.Contains('\n') || text.Contains('\r'))
                    return false;

                var decoded = DecodePathForClassification(text);
                if (string.IsNullOrWhiteSpace(decoded) || HasInvalidPathCharacters(decoded))
                    return false;

                var projectRelative = TrimProjectRelativeLeadingSlash(StripProjectUriPrefix(decoded));
                if (IsKnownProjectFilePrefix(projectRelative))
                    return true;

                try
                {
                    return Path.IsPathRooted(projectRelative.Replace('/', Path.DirectorySeparatorChar));
                }
                catch
                {
                    return false;
                }
            }

            static string DecodePathForClassification(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return value;

                try
                {
                    return System.Net.WebUtility.UrlDecode(value).Replace('\\', '/');
                }
                catch
                {
                    return value.Replace('\\', '/');
                }
            }

            static bool HasInvalidPathCharacters(string value)
            {
                return !string.IsNullOrEmpty(value) && value.IndexOfAny(Path.GetInvalidPathChars()) >= 0;
            }

            static bool IsKnownProjectFilePrefix(string path)
            {
                return !string.IsNullOrWhiteSpace(path) &&
                       (path.Equals("Assets", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("Packages", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("Library", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("Library/", StringComparison.OrdinalIgnoreCase));
            }

            static string NormalizeSceneHint(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return null;

                var text = value.Trim();
                if (text.Contains('\n') || text.Contains('\r') || HasInvalidPathCharacters(text))
                    return null;

                return text;
            }

            static string StripProjectUriPrefix(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }

                if (path.StartsWith("project://database/", StringComparison.OrdinalIgnoreCase))
                {
                    return path.Substring("project://database/".Length);
                }

                if (path.StartsWith("project://", StringComparison.OrdinalIgnoreCase))
                {
                    return path.Substring("project://".Length);
                }

                if (path.StartsWith("project:/", StringComparison.OrdinalIgnoreCase))
                {
                    return path.Substring("project:/".Length);
                }

                if (path.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                {
                    return path.Substring("unity://path/".Length);
                }

                if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        return new Uri(path).LocalPath.Replace('\\', '/');
                    }
                    catch
                    {
                        return path.Substring("file://".Length);
                    }
                }

                return path;
            }

            static string TrimProjectRelativeLeadingSlash(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }

                while (path.StartsWith("./", StringComparison.Ordinal))
                {
                    path = path.Substring(2);
                }

                while (path.StartsWith("/", StringComparison.Ordinal) &&
                       (path.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/Assets", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("/Packages/", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/Packages", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("/ProjectSettings/", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/ProjectSettings", StringComparison.OrdinalIgnoreCase)))
                {
                    path = path.Substring(1);
                }

                return path;
            }

            static bool IsAssetLikePath(string path)
            {
                return !string.IsNullOrWhiteSpace(path) &&
                       (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase));
            }

            static bool TryGetAbsoluteProjectPath(string assetPath, out string absolutePath, out string normalizedPath, out string error)
            {
                absolutePath = null;
                normalizedPath = NormalizeAssetPath(assetPath);
                error = null;

                if (!IsAssetLikePath(normalizedPath))
                {
                    error = $"Rollback ignored non-project asset path '{assetPath}'.";
                    return false;
                }

                absolutePath = ProjectPathResolver.ToAbsolutePath(normalizedPath, assumeAssetRelative: false);
                if (string.IsNullOrWhiteSpace(absolutePath))
                {
                    error = $"Rollback ignored path outside project root: '{assetPath}'.";
                    return false;
                }

                return true;
            }

            static string ToProjectDisplayPath(string absolutePath)
            {
                return ProjectPathResolver.ToProjectRelativePath(absolutePath, assumeAssetRelative: false) ?? absolutePath;
            }

            static string ProjectRoot => ProjectPathResolver.ProjectRoot;

            sealed class RollbackRoot
            {
                public string AssetPath;
                public string AbsolutePath;
                public bool Existed;
                public bool WasDirectory;
                public readonly Dictionary<string, RollbackFile> Files = new(StringComparer.OrdinalIgnoreCase);
            }

            sealed class RollbackFile
            {
                public string AbsolutePath;
                public byte[] Bytes;
            }
        }

    }
}
