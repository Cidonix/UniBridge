using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read-only project asset intelligence inspired by the old agent-oriented tooling  asset layer.
    /// </summary>
    public static class AssetIntelligence
    {
        const int DefaultLimit = 50;
        const int MaxLimit = 500;
        const int MaxPreviewSize = 512;
        const int MaxTextChars = 200000;
        const int MaxDependencyItems = 300;
        const int MaxSubAssets = 80;
        const string ToolName = "UniBridge_AssetIntelligence";

        static readonly HashSet<string> k_TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".asset", ".asmdef", ".asmref", ".cginc", ".compute", ".controller", ".css", ".csv",
            ".hlsl", ".html", ".inputactions", ".json", ".mat", ".md", ".meta",
            ".prefab", ".shader", ".spriteatlas", ".txt", ".uss", ".uxml", ".xml",
            ".yaml", ".yml", ".unity", ".cs", ".js"
        };

        static AssetReferenceIndex s_ReferenceIndex;

        public const string Title = "Asset intelligence";

        public const string Description = @"Explore Unity project assets in an AI-friendly, read-only way.

Use this when you need to understand the Project window: search assets, inspect one asset, read text assets, find dependencies/dependents, generate previews, inspect selected assets, or summarize project asset distribution.

Actions:
    Search: Ranked AssetDatabase search with filters for query, type, labels, extensions, folder scope, pagination, and optional previews.
    Inspect: Detailed metadata for one asset, including GUID, type, labels, importer, sub-assets, dependencies, dependents, and type-specific hints.
    Read: Read text-like assets such as .cs, .prefab, .unity, .mat, .json, .asset, .asmdef with line/byte slicing or multiple chunks.
    Dependencies: List assets used by the target.
    Dependents: Scan project assets and list assets that reference the target.
    Stats / Types: Compact asset distribution summaries for orientation.
    Selection: Inspect selected Project assets.
    Preview: Save or return a PNG preview when Unity can render one.
    Serialize / Snapshot: Build bounded AI-friendly upload envelopes with text/YAML, prefab hierarchy, component serialized fields, importer data, and asset metadata.
    Context: Build a structured one-call asset context envelope with summary, optional text chunks, serialized data, and fuzzy missing-path suggestions.
    Structure: List/search/read prefab or already-loaded scene hierarchy structure with indexed paths, components, and optional serialized field matching.
    ReferenceGraph: Build or query a cached project asset reference graph for dependencies and reverse references.
    Impact: Estimate references affected by changing, moving, renaming, deleting, or reimporting an asset.
    ResolveMissing: Resolve a missing or mistyped asset path with fuzzy suggestions.
    SemanticDiff: Compare two Unity YAML/text assets semantically, including YAML documents, fileIDs, GUID/script references, changed properties, and bounded line diff samples.

This tool does not modify assets. Use UniBridge_ManageAsset, UniBridge_ManagePrefab, or UniBridge_BatchActions for changes.";

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "assets" }, EnabledByDefault = true)]
        public static object HandleCommand(AssetIntelligenceParams parameters)
        {
            parameters ??= new AssetIntelligenceParams();

            try
            {
                return parameters.Action switch
                {
                    AssetIntelligenceAction.Search => Search(parameters),
                    AssetIntelligenceAction.Inspect => Inspect(parameters),
                    AssetIntelligenceAction.Read => Read(parameters),
                    AssetIntelligenceAction.Dependencies => Dependencies(parameters),
                    AssetIntelligenceAction.Dependents => Dependents(parameters),
                    AssetIntelligenceAction.Stats => Stats(parameters),
                    AssetIntelligenceAction.Types => Types(parameters),
                    AssetIntelligenceAction.Selection => Selection(parameters),
                    AssetIntelligenceAction.Preview => Preview(parameters),
                    AssetIntelligenceAction.Serialize => Serialize(parameters, "Serialize"),
                    AssetIntelligenceAction.Snapshot => Serialize(parameters, "Snapshot"),
                    AssetIntelligenceAction.Context => Context(parameters),
                    AssetIntelligenceAction.Structure => Structure(parameters),
                    AssetIntelligenceAction.ReferenceGraph => ReferenceGraph(parameters),
                    AssetIntelligenceAction.Impact => Impact(parameters),
                    AssetIntelligenceAction.ResolveMissing => ResolveMissing(parameters),
                    AssetIntelligenceAction.SemanticDiff => AssetSemanticDiff.Handle(parameters),
                    _ => Response.Error($"Unsupported action: {parameters.Action}")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetIntelligence] {parameters.Action} failed: {ex}");
                return Response.Error($"Asset intelligence failed: {ex.Message}");
            }
        }

        static object Search(AssetIntelligenceParams p)
        {
            var limit = Clamp(p.Limit <= 0 ? DefaultLimit : p.Limit, 1, MaxLimit);
            var page = Math.Max(1, p.Page);
            var query = NormalizeQuery(p);
            var folderScopes = BuildFolderScopes(p);
            var filter = BuildAssetDatabaseFilter(query, p);
            var candidatePaths = FindCandidatePaths(filter, folderScopes, p);

            var results = candidatePaths
                .Select(path => new RankedAsset(path, ScoreAsset(path, query, p)))
                .Where(item => item.Score > int.MinValue)
                .ToList();

            SortResults(results, p.SortBy);

            var startIndex = (page - 1) * limit;
            var pageItems = results
                .Skip(startIndex)
                .Take(limit)
                .Select(item => BuildAssetSummary(
                    item.Path,
                    query,
                    p.IncludePreview,
                    p.IncludePreview ? p.PreviewMode : AssetPreviewOutputMode.None,
                    p.IncludeImporter,
                    p.IncludeDependencies))
                .ToList();

            var data = new
            {
                action = "Search",
                query,
                assetDatabaseFilter = filter,
                total = results.Count,
                page,
                limit,
                returned = pageItems.Count,
                scope = folderScopes,
                sortBy = p.SortBy.ToString(),
                assets = pageItems
            };

            return Response.Success($"Found {results.Count} asset(s).", data);
        }

        static object Inspect(AssetIntelligenceParams p)
        {
            var paths = ResolveTargetPaths(p);
            if (paths.Count == 0)
                return MissingTargetError("Inspect", p);

            var limit = Clamp(p.Limit <= 0 ? 20 : p.Limit, 1, MaxLimit);
            var assets = paths
                .Take(limit)
                .Select(path => BuildAssetDetail(path, p))
                .ToList();

            return Response.Success($"Inspected {assets.Count} asset(s).", new
            {
                action = "Inspect",
                totalTargets = paths.Count,
                returned = assets.Count,
                assets
            });
        }

        static object Read(AssetIntelligenceParams p)
        {
            var path = ResolveSingleTargetPath(p);
            if (string.IsNullOrEmpty(path))
                return MissingTargetError("Read", p);

            var absolutePath = AssetPathToAbsolutePath(path);
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return Response.Error($"Asset file does not exist on disk or cannot be resolved: {path}");

            if (!IsTextLike(path))
                return Response.Error($"Asset does not look text-readable: {path}", new
                {
                    path,
                    extension = Path.GetExtension(path),
                    hint = "Use Inspect or Preview for binary assets."
                });

            var maxChars = Clamp(p.MaxTextChars <= 0 ? 60000 : p.MaxTextChars, 1000, MaxTextChars);
            var fullText = File.ReadAllText(absolutePath);
            var read = BuildTextReadPayload(fullText, p, maxChars);
            var asset = BuildAssetSummary(path, NormalizeQuery(p), false, AssetPreviewOutputMode.None, true, false);

            return Response.Success($"Read text asset '{path}'.", new
            {
                action = "Read",
                asset,
                text = read.Text,
                chunks = read.Chunks,
                slice = read.Slice,
                mode = read.Mode,
                lengthChars = fullText.Length,
                lengthBytes = new FileInfo(absolutePath).Length,
                sha256 = ComputeSha256(absolutePath)
            });
        }

        static object Structure(AssetIntelligenceParams p)
        {
            var path = ResolveSingleTargetPath(p);
            if (string.IsNullOrEmpty(path))
                return MissingTargetError("Structure", p);

            return AssetStructureReader.Handle(path, p);
        }

        static object Dependencies(AssetIntelligenceParams p)
        {
            var path = ResolveSingleTargetPath(p);
            if (string.IsNullOrEmpty(path))
                return MissingTargetError("Dependencies", p);

            var dependencies = AssetDatabase.GetDependencies(path, p.Recursive)
                .Where(dep => !string.Equals(dep, path, StringComparison.OrdinalIgnoreCase))
                .Where(dep => ShouldIncludePath(dep, p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(dep => BuildAssetSummary(dep, null, false, AssetPreviewOutputMode.None, p.IncludeImporter, false))
                .Take(MaxDependencyItems)
                .ToList();

            return Response.Success($"Found {dependencies.Count} dependenc{(dependencies.Count == 1 ? "y" : "ies")}.", new
            {
                action = "Dependencies",
                target = BuildAssetSummary(path, null, false, AssetPreviewOutputMode.None, true, false),
                recursive = p.Recursive,
                dependencies
            });
        }

        static object Dependents(AssetIntelligenceParams p)
        {
            var path = ResolveSingleTargetPath(p);
            if (string.IsNullOrEmpty(path))
                return MissingTargetError("Dependents", p);

            if (p.UseReferenceIndex)
                return IndexedDependents(path, p);

            var guid = AssetDatabase.AssetPathToGUID(path);
            var maxScan = Clamp(p.MaxScanAssets <= 0 ? 8000 : p.MaxScanAssets, 100, 50000);
            var candidatePaths = GetScopedAssetPaths(p)
                .Where(candidate => !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                .Take(maxScan)
                .ToList();

            var dependents = new List<object>();
            var dependentPaths = new List<string>();
            foreach (var candidate in candidatePaths)
            {
                var dependencies = AssetDatabase.GetDependencies(candidate, true);
                var dependsOnTarget = dependencies.Any(dep =>
                    string.Equals(dep, path, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(guid) && string.Equals(AssetDatabase.AssetPathToGUID(dep), guid, StringComparison.OrdinalIgnoreCase)));

                if (dependsOnTarget)
                {
                    dependents.Add(BuildAssetSummary(candidate, null, false, AssetPreviewOutputMode.None, p.IncludeImporter, false));
                    dependentPaths.Add(candidate);
                    if (dependents.Count >= Clamp(p.Limit <= 0 ? DefaultLimit : p.Limit, 1, MaxLimit))
                        break;
                }
            }

            return Response.Success($"Found {dependents.Count} dependent asset(s).", new
            {
                action = "Dependents",
                target = BuildAssetSummary(path, null, false, AssetPreviewOutputMode.None, true, false),
                scanned = candidatePaths.Count,
                maxScan,
                truncatedScan = candidatePaths.Count >= maxScan,
                dependents,
                referenceLocations = p.IncludeReferenceLocations
                    ? BuildIncomingReferenceLocationGroups(path, dependentPaths, p)
                    : null
            });
        }

        static object IndexedDependents(string path, AssetIntelligenceParams p)
        {
            var index = GetReferenceIndex(p, p.RefreshReferenceIndex);
            var limit = Clamp(p.Limit <= 0 ? DefaultLimit : p.Limit, 1, MaxLimit);
            var dependentPaths = index.GetDependents(path)
                .Take(limit)
                .ToList();

            var dependents = dependentPaths
                .Select(dependent => BuildAssetSummary(dependent, null, false, AssetPreviewOutputMode.None, p.IncludeImporter, false))
                .ToList();

            return Response.Success($"Found {dependents.Count} dependent asset(s) from the reference index.", new
            {
                action = "Dependents",
                target = BuildAssetSummary(path, null, false, AssetPreviewOutputMode.None, true, false),
                indexed = true,
                index = BuildReferenceIndexSummary(index),
                returned = dependents.Count,
                truncated = index.GetDependentCount(path) > dependents.Count,
                dependents,
                referenceLocations = p.IncludeReferenceLocations
                    ? BuildIncomingReferenceLocationGroups(path, dependentPaths, p)
                    : null
            });
        }

        static object ReferenceGraph(AssetIntelligenceParams p)
        {
            var index = GetReferenceIndex(p, p.RefreshReferenceIndex);
            var path = ResolveSingleTargetPath(p);
            var limit = Clamp(p.Limit <= 0 ? DefaultLimit : p.Limit, 1, MaxLimit);

            if (string.IsNullOrEmpty(path))
            {
                var topReferenced = index.GetMostReferenced(limit)
                    .Select(item => new
                    {
                        asset = BuildAssetSummary(item.Path, null, false, AssetPreviewOutputMode.None, false, false),
                        dependentCount = item.Count
                    })
                    .ToList();

                return Response.Success($"Built reference graph for {index.AssetCount} asset(s).", new
                {
                    action = "ReferenceGraph",
                    index = BuildReferenceIndexSummary(index),
                    scope = index.Scopes,
                    topReferenced,
                    edges = p.IncludeReferenceEdges ? index.GetEdges(Clamp(p.MaxReferenceEdges <= 0 ? 200 : p.MaxReferenceEdges, 1, 5000)) : null
                });
            }

            var dependencies = index.GetDependencies(path)
                .Take(MaxDependencyItems)
                .Select(dep => BuildAssetSummary(dep, null, false, AssetPreviewOutputMode.None, p.IncludeImporter, false))
                .ToList();
            var dependencyPaths = index.GetDependencies(path)
                .Take(MaxDependencyItems)
                .ToList();
            var dependents = index.GetDependents(path)
                .Take(limit)
                .Select(dep => BuildAssetSummary(dep, null, false, AssetPreviewOutputMode.None, p.IncludeImporter, false))
                .ToList();
            var dependentPaths = index.GetDependents(path)
                .Take(limit)
                .ToList();

            return Response.Success($"Built reference graph slice for '{path}'.", new
            {
                action = "ReferenceGraph",
                target = BuildAssetSummary(path, null, false, AssetPreviewOutputMode.None, true, false),
                index = BuildReferenceIndexSummary(index),
                dependencies,
                dependents,
                dependencyCount = index.GetDependencyCount(path),
                dependentCount = index.GetDependentCount(path),
                edges = p.IncludeReferenceEdges ? index.GetEdgesFor(path, Clamp(p.MaxReferenceEdges <= 0 ? 200 : p.MaxReferenceEdges, 1, 5000)) : null,
                referenceLocations = p.IncludeReferenceLocations
                    ? new
                    {
                        incoming = BuildIncomingReferenceLocationGroups(path, dependentPaths, p),
                        outgoing = BuildOutgoingReferenceLocationGroups(path, dependencyPaths, p)
                    }
                    : null
            });
        }

        static object Impact(AssetIntelligenceParams p)
        {
            var path = ResolveSingleTargetPath(p);
            if (string.IsNullOrEmpty(path))
                return MissingTargetError("Impact", p);

            var index = GetReferenceIndex(p, p.RefreshReferenceIndex);
            var limit = Clamp(p.Limit <= 0 ? DefaultLimit : p.Limit, 1, MaxLimit);
            var operation = string.IsNullOrWhiteSpace(p.ImpactOperation) ? "Modify" : p.ImpactOperation.Trim();
            var dependentCount = index.GetDependentCount(path);
            var dependencyCount = index.GetDependencyCount(path);
            var risk = ClassifyImpactRisk(operation, dependentCount);
            var dependents = index.GetDependents(path)
                .Take(limit)
                .Select(dep => BuildAssetSummary(dep, null, false, AssetPreviewOutputMode.None, p.IncludeImporter, false))
                .ToList();
            var dependentPaths = index.GetDependents(path)
                .Take(limit)
                .ToList();
            var dependencies = index.GetDependencies(path)
                .Take(Math.Min(MaxDependencyItems, limit))
                .Select(dep => BuildAssetSummary(dep, null, false, AssetPreviewOutputMode.None, p.IncludeImporter, false))
                .ToList();
            var dependencyPaths = index.GetDependencies(path)
                .Take(Math.Min(MaxDependencyItems, limit))
                .ToList();

            return Response.Success($"Impact for {operation} on '{path}': {risk}.", new
            {
                action = "Impact",
                operation,
                target = BuildAssetSummary(path, null, false, AssetPreviewOutputMode.None, true, false),
                risk,
                index = BuildReferenceIndexSummary(index),
                dependencyCount,
                dependentCount,
                returnedDependents = dependents.Count,
                returnedDependencies = dependencies.Count,
                dependents,
                dependencies,
                referenceLocations = p.IncludeReferenceLocations
                    ? new
                    {
                        incoming = BuildIncomingReferenceLocationGroups(path, dependentPaths, p),
                        outgoing = BuildOutgoingReferenceLocationGroups(path, dependencyPaths, p)
                    }
                    : null,
                guidance = BuildImpactGuidance(operation, dependentCount)
            });
        }

        static object ResolveMissing(AssetIntelligenceParams p)
        {
            var requested = GetRequestedTarget(p);
            var resolved = ResolveTargetPaths(p);
            var limit = Clamp(p.MaxSuggestions <= 0 ? 5 : p.MaxSuggestions, 1, 25);

            if (resolved.Count > 0)
            {
                var assets = resolved
                    .Take(Clamp(p.Limit <= 0 ? DefaultLimit : p.Limit, 1, MaxLimit))
                    .Select(path => BuildAssetSummary(path, NormalizeQuery(p), false, AssetPreviewOutputMode.None, p.IncludeImporter, false))
                    .ToList();

                return Response.Success($"Resolved {assets.Count} asset candidate(s).", new
                {
                    action = "ResolveMissing",
                    requested,
                    resolved = true,
                    totalResolved = resolved.Count,
                    assets
                });
            }

            var suggestions = BuildSimilarAssetSuggestions(p, requested, limit);
            return Response.Success(
                suggestions.Count > 0
                    ? $"Asset was not found. Returned {suggestions.Count} similar candidate(s)."
                    : "Asset was not found and no close candidates were found.",
                new
                {
                    action = "ResolveMissing",
                    requested,
                    resolved = false,
                    suggestions,
                    hint = suggestions.Count > 0
                        ? "Use the suggested asset path as Path, or narrow with Types/Extensions/Path folder scope."
                        : "Try a broader Query, enable IncludePackages, or check whether the asset has not been imported yet."
                });
        }

        static object Stats(AssetIntelligenceParams p)
        {
            var maxScan = Clamp(p.MaxScanAssets <= 0 ? 8000 : p.MaxScanAssets, 100, 50000);
            var paths = GetScopedAssetPaths(p)
                .Where(path => ShouldIncludePath(path, p))
                .Take(maxScan)
                .ToList();

            var records = paths.Select(BuildLightRecord).ToList();
            var byType = records.GroupBy(r => r.TypeName)
                .Select(g => new { type = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .ThenBy(x => x.type)
                .Take(40)
                .ToList();

            var byExtension = records.GroupBy(r => string.IsNullOrEmpty(r.Extension) ? "(none)" : r.Extension)
                .Select(g => new { extension = g.Key, count = g.Count(), bytes = g.Sum(x => x.SizeBytes) })
                .OrderByDescending(x => x.count)
                .ThenBy(x => x.extension)
                .Take(40)
                .ToList();

            var byFolder = records.GroupBy(r => GetTopFolder(r.Path))
                .Select(g => new { folder = g.Key, count = g.Count(), bytes = g.Sum(x => x.SizeBytes) })
                .OrderByDescending(x => x.count)
                .ThenBy(x => x.folder)
                .Take(40)
                .ToList();

            var largest = records
                .Where(r => r.SizeBytes > 0)
                .OrderByDescending(r => r.SizeBytes)
                .Take(20)
                .Select(ToLightAssetData)
                .ToList();

            var recent = records
                .Where(r => r.ModifiedUtc.HasValue)
                .OrderByDescending(r => r.ModifiedUtc.Value)
                .Take(20)
                .Select(ToLightAssetData)
                .ToList();

            return Response.Success($"Summarized {records.Count} asset(s).", new
            {
                action = "Stats",
                scanned = records.Count,
                maxScan,
                truncatedScan = records.Count >= maxScan,
                totalBytes = records.Sum(r => r.SizeBytes),
                byType,
                byExtension,
                byFolder,
                largest,
                recent
            });
        }

        static object Types(AssetIntelligenceParams p)
        {
            var maxScan = Clamp(p.MaxScanAssets <= 0 ? 8000 : p.MaxScanAssets, 100, 50000);
            var paths = GetScopedAssetPaths(p)
                .Where(path => ShouldIncludePath(path, p))
                .Take(maxScan)
                .ToList();

            var types = paths
                .GroupBy(GetAssetTypeName)
                .Select(g => new
                {
                    type = g.Key,
                    count = g.Count(),
                    examples = g.Take(5).ToArray()
                })
                .OrderByDescending(x => x.count)
                .ThenBy(x => x.type)
                .Take(Clamp(p.Limit <= 0 ? 80 : p.Limit, 1, 250))
                .ToList();

            return Response.Success($"Found {types.Count} asset type group(s).", new
            {
                action = "Types",
                scanned = paths.Count,
                types
            });
        }

        static object Selection(AssetIntelligenceParams p)
        {
            var paths = UnityEditor.Selection.assetGUIDs
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => ShouldIncludePath(path, p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var limit = Clamp(p.Limit <= 0 ? 50 : p.Limit, 1, MaxLimit);
            var assets = paths
                .Take(limit)
                .Select(path => BuildAssetDetail(path, p))
                .ToList();

            return Response.Success($"Inspected {assets.Count} selected asset(s).", new
            {
                action = "Selection",
                totalSelectedAssets = paths.Count,
                returned = assets.Count,
                assets
            });
        }

        static object Preview(AssetIntelligenceParams p)
        {
            var path = ResolveSingleTargetPath(p);
            if (string.IsNullOrEmpty(path))
                return MissingTargetError("Preview", p);

            var preview = BuildPreview(path, p.PreviewMode == AssetPreviewOutputMode.None ? AssetPreviewOutputMode.File : p.PreviewMode, p.PreviewSize);
            if (preview == null)
                return Response.Error($"Unity could not generate a preview for asset: {path}", new
                {
                    target = BuildAssetSummary(path, null, false, AssetPreviewOutputMode.None, true, false)
                });

            return Response.Success($"Generated preview for '{path}'.", new
            {
                action = "Preview",
                asset = BuildAssetSummary(path, null, false, AssetPreviewOutputMode.None, true, false),
                preview
            });
        }

        static object Context(AssetIntelligenceParams p)
        {
            var limit = Clamp(p.Limit <= 0 ? 8 : p.Limit, 1, MaxLimit);
            var requests = BuildAssetContextRequests(p);
            var resolvedPaths = new List<string>();
            var unresolved = new List<object>();

            if (requests.Count == 0)
            {
                resolvedPaths.AddRange(ResolveTargetPaths(p));
            }
            else
            {
                foreach (var request in requests)
                {
                    var path = ResolveContextRequestPath(request, p);
                    if (!string.IsNullOrEmpty(path) && AssetPathExists(path))
                    {
                        resolvedPaths.Add(path);
                        continue;
                    }

                    unresolved.Add(BuildMissingAssetContext(request, p));
                }
            }

            resolvedPaths = resolvedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();

            var assets = resolvedPaths
                .Select(path => BuildAssetContext(path, p))
                .ToList();

            if (assets.Count == 0 && unresolved.Count == 0)
                return MissingTargetError("Context", p);

            return Response.Success($"Built context for {assets.Count} asset(s).", new
            {
                action = "Context",
                profile = p.ContextProfile.ToString(),
                resolved = assets.Count,
                unresolved = unresolved.Count,
                returned = assets.Count,
                limit,
                assets,
                missing = unresolved
            });
        }

        static object Serialize(AssetIntelligenceParams p, string action)
        {
            var paths = ResolveTargetPaths(p);
            if (paths.Count == 0)
                return MissingTargetError(action, p);

            var limit = Clamp(p.Limit <= 0 ? 10 : p.Limit, 1, MaxLimit);
            var selectedPaths = paths.Take(limit).ToList();
            var assets = AssetSnapshotSerializer.SerializeAssets(selectedPaths, p, action);

            return Response.Success($"{action} serialized {assets.Count} asset(s).", new
            {
                action,
                mode = p.SerializeMode.ToString(),
                totalTargets = paths.Count,
                returned = assets.Count,
                truncated = paths.Count > selectedPaths.Count,
                assets
            });
        }

        static List<AssetContextRequest> BuildAssetContextRequests(AssetIntelligenceParams p)
        {
            var requests = new List<AssetContextRequest>();

            if (p.Paths != null)
            {
                foreach (var path in p.Paths.Where(value => !string.IsNullOrWhiteSpace(value)))
                    requests.Add(new AssetContextRequest("path", path.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(p.Path))
                requests.Add(new AssetContextRequest("path", p.Path.Trim()));

            if (!string.IsNullOrWhiteSpace(p.Guid))
                requests.Add(new AssetContextRequest("guid", p.Guid.Trim()));

            return requests;
        }

        static string ResolveContextRequestPath(AssetContextRequest request, AssetIntelligenceParams p)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Value))
                return null;

            if (string.Equals(request.Kind, "guid", StringComparison.OrdinalIgnoreCase))
            {
                var path = AssetDatabase.GUIDToAssetPath(request.Value);
                return AssetPathExists(path) && ShouldIncludePath(path, p) ? path : null;
            }

            var normalized = NormalizeAssetPath(request.Value, assumeAssetRelative: true);
            return AssetPathExists(normalized) && ShouldIncludePath(normalized, p) ? normalized : null;
        }

        static object BuildAssetContext(string path, AssetIntelligenceParams p)
        {
            var profile = p.ContextProfile;
            var isText = IsTextLike(path);
            object read = null;
            object serialized = null;

            if (ShouldIncludeTextContext(path, profile, p))
            {
                var absolutePath = AssetPathToAbsolutePath(path);
                if (!string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath))
                {
                    var maxChars = Clamp(p.MaxTextChars <= 0 ? 60000 : p.MaxTextChars, 1000, MaxTextChars);
                    read = BuildTextReadPayload(File.ReadAllText(absolutePath), p, maxChars);
                }
            }

            if (ShouldIncludeSerializedContext(path, profile, isText))
            {
                var serializeParams = BuildContextSerializationParams(p, profile);
                serialized = AssetSnapshotSerializer.SerializeAssets(new[] { path }, serializeParams, "Context").FirstOrDefault();
            }

            return new
            {
                path,
                profile = profile.ToString(),
                isTextLike = isText,
                detail = BuildAssetDetail(path, BuildContextDetailParams(p, profile)),
                read,
                serialized,
                guidance = BuildAssetContextGuidance(path, isText, read != null, serialized != null)
            };
        }

        static object BuildMissingAssetContext(AssetContextRequest request, AssetIntelligenceParams p)
        {
            var requested = request?.Value;
            var searchParams = p with { Path = requested, Guid = null, Query = null, SearchPattern = null };
            var suggestions = FindSimilarAssetCandidates(searchParams, requested, Clamp(p.MaxSuggestions <= 0 ? 5 : p.MaxSuggestions, 1, 25));
            object bestSuggestionContext = null;

            if (p.IncludeBestSuggestionContext && suggestions.Count > 0)
            {
                var best = suggestions[0];
                bestSuggestionContext = BuildAssetContext(best.Path, p with
                {
                    ContextProfile = p.ContextProfile == AssetContextProfile.Deep ? AssetContextProfile.Deep : AssetContextProfile.Auto,
                    Limit = 1
                });
            }

            return new
            {
                request = requested,
                requestKind = request?.Kind,
                resolved = false,
                suggestions = suggestions.Select(candidate => new
                {
                    candidate.Path,
                    candidate.Score,
                    candidate.Reason,
                    asset = BuildAssetSummary(candidate.Path, null, false, AssetPreviewOutputMode.None, p.IncludeImporter, false)
                }).ToArray(),
                bestSuggestionContext
            };
        }

        static AssetIntelligenceParams BuildContextDetailParams(AssetIntelligenceParams p, AssetContextProfile profile)
        {
            if (profile == AssetContextProfile.Deep)
            {
                return p with
                {
                    IncludeSubAssets = true,
                    IncludeDependencies = true,
                    IncludeImporter = true,
                    IncludeSerializedProperties = true,
                    SerializeMode = AssetSerializationMode.Full
                };
            }

            return p with
            {
                IncludeImporter = true,
                IncludeSubAssets = p.IncludeSubAssets || profile == AssetContextProfile.Serialized,
                IncludeDependencies = p.IncludeDependencies || profile == AssetContextProfile.Serialized
            };
        }

        static AssetIntelligenceParams BuildContextSerializationParams(AssetIntelligenceParams p, AssetContextProfile profile)
        {
            if (profile == AssetContextProfile.Deep)
            {
                return p with
                {
                    SerializeMode = AssetSerializationMode.Full,
                    IncludeSerializedProperties = true,
                    IncludeHierarchy = true,
                    IncludeSubAssets = true
                };
            }

            return p with
            {
                SerializeMode = p.SerializeMode == AssetSerializationMode.Minimal && profile == AssetContextProfile.Auto
                    ? AssetSerializationMode.Standard
                    : p.SerializeMode,
                IncludeSerializedProperties = p.IncludeSerializedProperties,
                IncludeHierarchy = true
            };
        }

        static bool ShouldIncludeTextContext(string path, AssetContextProfile profile, AssetIntelligenceParams p)
        {
            if (!p.IncludeRawText || !IsTextLike(path))
                return false;

            if (profile == AssetContextProfile.Summary || profile == AssetContextProfile.Serialized)
                return false;

            return true;
        }

        static bool ShouldIncludeSerializedContext(string path, AssetContextProfile profile, bool isText)
        {
            if (profile == AssetContextProfile.Summary || profile == AssetContextProfile.Text)
                return false;

            if (profile == AssetContextProfile.Serialized || profile == AssetContextProfile.Deep)
                return true;

            if (!isText)
                return true;

            var extension = Path.GetExtension(path);
            return extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".unity", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mat", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".asset", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".controller", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".spriteatlas", StringComparison.OrdinalIgnoreCase);
        }

        static object BuildAssetContextGuidance(string path, bool isText, bool includedRead, bool includedSerialized)
        {
            var suggestions = new List<string>();
            if (isText && !includedRead)
                suggestions.Add("Use ContextProfile=Text or IncludeRawText=true to include text slices.");
            if (!includedSerialized)
                suggestions.Add("Use ContextProfile=Serialized or Deep for prefab hierarchy, importer/main/sub-asset serialized data.");
            if (AssetDatabase.GetDependencies(path, true).Length > 1)
                suggestions.Add("Use Action=Impact or ReferenceGraph before move/rename/delete.");

            return suggestions.Count == 0 ? null : suggestions.ToArray();
        }

        static string NormalizeQuery(AssetIntelligenceParams p)
        {
            return (p.Query ?? p.SearchPattern ?? string.Empty).Trim();
        }

        static string BuildAssetDatabaseFilter(string query, AssetIntelligenceParams p)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(query))
                parts.Add(query);

            if (p.Types != null)
            {
                foreach (var type in p.Types.Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    var token = BuildAssetDatabaseTypeToken(type);
                    if (!string.IsNullOrWhiteSpace(token))
                        parts.Add($"t:{token}");
                }
            }

            if (p.Labels != null)
            {
                foreach (var label in p.Labels.Where(value => !string.IsNullOrWhiteSpace(value)))
                    parts.Add(label.Trim().StartsWith("l:", StringComparison.OrdinalIgnoreCase) ? label.Trim() : $"l:{label.Trim()}");
            }

            return string.Join(" ", parts);
        }

        static string[] BuildFolderScopes(AssetIntelligenceParams p)
        {
            var scopes = new List<string>();

            if (!string.IsNullOrWhiteSpace(p.Path) && IsFolderPath(NormalizeAssetPath(p.Path, assumeAssetRelative: false)))
                scopes.Add(NormalizeAssetPath(p.Path, assumeAssetRelative: false));

            if (p.Paths != null)
            {
                foreach (var rawPath in p.Paths)
                {
                    var path = NormalizeAssetPath(rawPath, assumeAssetRelative: false);
                    if (IsFolderPath(path))
                        scopes.Add(path);
                }
            }

            if (scopes.Count == 0)
            {
                scopes.Add("Assets");
                if (p.IncludePackages)
                    scopes.Add("Packages");
            }

            return scopes
                .Where(scope => AssetDatabase.IsValidFolder(scope))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static List<string> FindCandidatePaths(string assetDatabaseFilter, string[] folderScopes, AssetIntelligenceParams p)
        {
            IEnumerable<string> paths;
            try
            {
                paths = string.IsNullOrWhiteSpace(assetDatabaseFilter)
                    ? GetScopedAssetPaths(p)
                    : AssetDatabase.FindAssets(assetDatabaseFilter, folderScopes)
                        .Select(AssetDatabase.GUIDToAssetPath);
            }
            catch
            {
                paths = GetScopedAssetPaths(p);
            }

            return paths
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => ShouldIncludePath(path, p))
                .Where(path => MatchesExtensionFilters(path, p.Extensions))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static List<string> GetScopedAssetPaths(AssetIntelligenceParams p)
        {
            var scopes = BuildFolderScopes(p);
            var allPaths = AssetDatabase.GetAllAssetPaths();

            return allPaths
                .Where(path => scopes.Length == 0 || scopes.Any(scope => IsUnderScope(path, scope)))
                .Where(path => ShouldIncludePath(path, p))
                .Where(path => MatchesExtensionFilters(path, p.Extensions))
                .ToList();
        }

        static bool ShouldIncludePath(string path, AssetIntelligenceParams p)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            if (!p.IncludePackages && path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!p.IncludeFolders && AssetDatabase.IsValidFolder(path))
                return false;

            if (!p.IncludeHidden)
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(name) && name.StartsWith(".", StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        static bool MatchesExtensionFilters(string path, string[] extensions)
        {
            if (extensions == null || extensions.Length == 0)
                return true;

            var ext = Path.GetExtension(path);
            foreach (var raw in extensions)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var normalized = raw.Trim();
                if (!normalized.StartsWith(".", StringComparison.Ordinal))
                    normalized = "." + normalized;

                if (string.Equals(ext, normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static bool IsUnderScope(string path, string scope)
        {
            if (string.IsNullOrEmpty(scope))
                return true;

            scope = scope.TrimEnd('/') + "/";
            return path.StartsWith(scope, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(path.TrimEnd('/'), scope.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        static int ScoreAsset(string path, string query, AssetIntelligenceParams p)
        {
            if (!MatchesExtensionFilters(path, p.Extensions))
                return int.MinValue;

            var score = 0;
            var typeName = GetAssetTypeName(path);
            var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var lowerName = name.ToLowerInvariant();
            var lowerPath = path.ToLowerInvariant();
            var lowerType = typeName.ToLowerInvariant();

            if (p.Types != null && p.Types.Length > 0)
            {
                var matchesType = p.Types.Any(type => MatchesTypeFilter(path, typeName, type));
                if (!matchesType)
                    return int.MinValue;
                score += 20;
            }

            if (p.Labels != null && p.Labels.Length > 0)
            {
                var labels = GetLabels(path);
                if (!p.Labels.Where(value => !string.IsNullOrWhiteSpace(value))
                        .All(label => labels.Contains(label.Trim().TrimStart('l', ':'), StringComparer.OrdinalIgnoreCase)))
                    return int.MinValue;
                score += 20;
            }

            if (string.IsNullOrWhiteSpace(query))
                return score;

            var terms = TokenizeQuery(query);
            if (terms.Count == 0)
                return score;

            var matched = 0;
            foreach (var term in terms)
            {
                var lowerTerm = term.ToLowerInvariant();
                if (p.Exact)
                {
                    if (string.Equals(lowerName, lowerTerm, StringComparison.OrdinalIgnoreCase))
                    {
                        matched++;
                        score += 100;
                    }
                    else if (lowerPath.Contains("/" + lowerTerm + ".") || lowerPath.Contains("/" + lowerTerm + "/"))
                    {
                        matched++;
                        score += 60;
                    }
                }
                else if (lowerName.Contains(lowerTerm))
                {
                    matched++;
                    score += lowerName.StartsWith(lowerTerm, StringComparison.OrdinalIgnoreCase) ? 90 : 70;
                }
                else if (lowerPath.Contains(lowerTerm))
                {
                    matched++;
                    score += 35;
                }
                else if (lowerType.Contains(lowerTerm))
                {
                    matched++;
                    score += 20;
                }
            }

            if (matched == 0 && terms.Count > 0)
                return int.MinValue;

            if (matched == terms.Count)
                score += 35;

            score += Math.Max(0, 20 - path.Count(c => c == '/'));
            return score;
        }

        static List<string> TokenizeQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<string>();

            return query
                .Split(new[] { ' ', '\t', '\r', '\n', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(term => term.Trim())
                .Where(term => term.Length > 1 && !term.StartsWith("t:", StringComparison.OrdinalIgnoreCase) && !term.StartsWith("l:", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static string BuildAssetDatabaseTypeToken(string rawType)
        {
            var type = NormalizeTypeFilter(rawType);
            if (string.IsNullOrWhiteSpace(type))
                return null;

            return type.ToLowerInvariant() switch
            {
                "csharp" or "csharpscript" or "script" => "MonoScript",
                "scene" => "SceneAsset",
                "prefab" or "prefabs" => null,
                _ => type
            };
        }

        static bool MatchesTypeFilter(string path, string typeName, string rawType)
        {
            var type = NormalizeTypeFilter(rawType);
            if (string.IsNullOrWhiteSpace(type))
                return false;

            var lowerFilter = type.ToLowerInvariant();
            var lowerType = (typeName ?? string.Empty).ToLowerInvariant();
            var extension = Path.GetExtension(path);

            if (lowerFilter is "prefab" or "prefabs")
                return string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase);

            if (lowerFilter is "scene" or "scenes")
                return string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(typeName, "SceneAsset", StringComparison.OrdinalIgnoreCase);

            if (lowerFilter is "script" or "scripts" or "csharp" or "csharpscript")
                return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(typeName, "MonoScript", StringComparison.OrdinalIgnoreCase);

            if (lowerFilter is "texture" or "textures")
                return lowerType.Contains("texture") ||
                       string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(extension, ".tga", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(extension, ".psd", StringComparison.OrdinalIgnoreCase);

            return lowerType.Contains(lowerFilter) ||
                   string.Equals(type, typeName, StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizeTypeFilter(string rawType)
        {
            if (string.IsNullOrWhiteSpace(rawType))
                return null;

            var type = rawType.Trim();
            if (type.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
                type = type.Substring(2);

            return type.Trim();
        }

        static void SortResults(List<RankedAsset> results, AssetIntelligenceSortMode sortMode)
        {
            Comparison<RankedAsset> comparison = sortMode switch
            {
                AssetIntelligenceSortMode.Path => (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase),
                AssetIntelligenceSortMode.Name => (a, b) => string.Compare(Path.GetFileName(a.Path), Path.GetFileName(b.Path), StringComparison.OrdinalIgnoreCase),
                AssetIntelligenceSortMode.Type => (a, b) => string.Compare(GetAssetTypeName(a.Path), GetAssetTypeName(b.Path), StringComparison.OrdinalIgnoreCase),
                AssetIntelligenceSortMode.Extension => (a, b) => string.Compare(Path.GetExtension(a.Path), Path.GetExtension(b.Path), StringComparison.OrdinalIgnoreCase),
                AssetIntelligenceSortMode.SizeAscending => (a, b) => GetFileSize(a.Path).CompareTo(GetFileSize(b.Path)),
                AssetIntelligenceSortMode.SizeDescending => (a, b) => GetFileSize(b.Path).CompareTo(GetFileSize(a.Path)),
                AssetIntelligenceSortMode.ModifiedAscending => (a, b) => Nullable.Compare(GetModifiedUtc(a.Path), GetModifiedUtc(b.Path)),
                AssetIntelligenceSortMode.ModifiedDescending => (a, b) => Nullable.Compare(GetModifiedUtc(b.Path), GetModifiedUtc(a.Path)),
                _ => (a, b) =>
                {
                    var scoreCompare = b.Score.CompareTo(a.Score);
                    return scoreCompare != 0
                        ? scoreCompare
                        : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
                }
            };

            results.Sort(comparison);
        }

        static List<string> ResolveTargetPaths(AssetIntelligenceParams p)
        {
            var paths = new List<string>();

            if (p.Paths != null)
            {
                foreach (var rawPath in p.Paths)
                    AddResolvedPath(paths, rawPath);
            }

            AddResolvedPath(paths, p.Path);

            if (!string.IsNullOrWhiteSpace(p.Guid))
            {
                var guidPath = AssetDatabase.GUIDToAssetPath(p.Guid.Trim());
                AddResolvedPath(paths, guidPath);
            }

            if (paths.Count == 0)
            {
                var query = NormalizeQuery(p);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var searchParams = p with { Limit = Math.Max(1, p.Limit <= 0 ? 1 : p.Limit) };
                    var filter = BuildAssetDatabaseFilter(query, searchParams);
                    var found = FindCandidatePaths(filter, BuildFolderScopes(searchParams), searchParams)
                        .Select(path => new RankedAsset(path, ScoreAsset(path, query, searchParams)))
                        .Where(item => item.Score > int.MinValue)
                        .ToList();
                    SortResults(found, AssetIntelligenceSortMode.Relevance);
                    paths.AddRange(found.Select(item => item.Path));
                }
            }

            return paths
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => AssetPathExists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static string ResolveSingleTargetPath(AssetIntelligenceParams p)
        {
            return ResolveTargetPaths(p).FirstOrDefault();
        }

        static void AddResolvedPath(List<string> paths, string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return;

            var path = NormalizeAssetPath(rawPath, assumeAssetRelative: true);
            if (AssetPathExists(path))
                paths.Add(path);
        }

        static object MissingTargetError(string action, AssetIntelligenceParams p)
        {
            var requested = GetRequestedTarget(p);
            var suggestions = p.SuggestSimilar
                ? BuildSimilarAssetSuggestions(p, requested, Clamp(p.MaxSuggestions <= 0 ? 5 : p.MaxSuggestions, 1, 25))
                : new List<object>();

            return Response.Error($"No target asset found for {action}. Provide Path, Guid, Paths, or Query.", new
            {
                action,
                requested,
                suggestions,
                hint = suggestions.Count > 0
                    ? "Use one of the suggested asset paths, or call ResolveMissing for a dedicated recovery response."
                    : "Try a broader Query, enable IncludePackages, or verify the asset is imported."
            });
        }

        static string GetRequestedTarget(AssetIntelligenceParams p)
        {
            if (!string.IsNullOrWhiteSpace(p.Path))
                return p.Path.Trim();

            if (!string.IsNullOrWhiteSpace(p.Guid))
                return p.Guid.Trim();

            if (p.Paths != null)
            {
                var firstPath = p.Paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
                if (!string.IsNullOrWhiteSpace(firstPath))
                    return firstPath.Trim();
            }

            return NormalizeQuery(p);
        }

        static List<object> BuildSimilarAssetSuggestions(AssetIntelligenceParams p, string requested, int limit)
        {
            return FindSimilarAssetCandidates(p, requested, limit)
                .Select(candidate => (object)new
                {
                    score = candidate.Score,
                    reason = candidate.Reason,
                    path = candidate.Path,
                    asset = BuildAssetSummary(candidate.Path, null, false, AssetPreviewOutputMode.None, p.IncludeImporter, false)
                })
                .ToList();
        }

        static List<SimilarAssetCandidate> FindSimilarAssetCandidates(AssetIntelligenceParams p, string requested, int limit)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return new List<SimilarAssetCandidate>();

            var normalizedRequested = NormalizeAssetPath(requested, assumeAssetRelative: true);
            var maxScan = Clamp(p.MaxScanAssets <= 0 ? 8000 : p.MaxScanAssets, 100, 50000);
            return GetScopedAssetPaths(p)
                .Where(path => !AssetDatabase.IsValidFolder(path))
                .Take(maxScan)
                .Select(path => ScoreSimilarAsset(path, normalizedRequested, NormalizeQuery(p)))
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        static SimilarAssetCandidate ScoreSimilarAsset(string path, string requestedPath, string query)
        {
            var requestedFile = Path.GetFileName(requestedPath) ?? requestedPath;
            var requestedName = Path.GetFileNameWithoutExtension(requestedPath) ?? requestedFile;
            var requestedExt = Path.GetExtension(requestedPath);
            var requestedFolder = NormalizePath(Path.GetDirectoryName(requestedPath));
            var file = Path.GetFileName(path) ?? path;
            var name = Path.GetFileNameWithoutExtension(path) ?? file;
            var ext = Path.GetExtension(path);
            var pathFolder = NormalizePath(Path.GetDirectoryName(path)) ?? string.Empty;
            var score = 0;
            var reasons = new List<string>();

            if (string.Equals(path, requestedPath, StringComparison.OrdinalIgnoreCase))
            {
                score += 1000;
                reasons.Add("exact path");
            }

            if (string.Equals(file, requestedFile, StringComparison.OrdinalIgnoreCase))
            {
                score += 700;
                reasons.Add("same file name");
            }
            else if (file.IndexOf(requestedFile, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     requestedFile.IndexOf(file, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 250;
                reasons.Add("file name contains requested text");
            }

            if (string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase))
            {
                score += 500;
                reasons.Add("same asset name");
            }

            if (!string.IsNullOrEmpty(requestedExt) && string.Equals(ext, requestedExt, StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
                reasons.Add("same extension");
            }

            if (!string.IsNullOrEmpty(requestedFolder) &&
                pathFolder.IndexOf(requestedFolder, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 80;
                reasons.Add("same folder");
            }

            var normalizedRequestedName = NormalizeFuzzyToken(requestedName);
            var normalizedName = NormalizeFuzzyToken(name);
            if (!string.IsNullOrEmpty(normalizedRequestedName) && !string.IsNullOrEmpty(normalizedName))
            {
                var maxLength = Math.Max(normalizedRequestedName.Length, normalizedName.Length);
                var distance = LevenshteinDistance(normalizedRequestedName, normalizedName);
                var similarity = maxLength == 0 ? 0 : (int)Math.Round((1.0 - (double)distance / maxLength) * 300);
                if (similarity > 0)
                {
                    score += similarity;
                    reasons.Add("similar name");
                }
            }

            foreach (var term in TokenizeQuery(string.IsNullOrWhiteSpace(query) ? requestedName : query))
            {
                if (path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 40;
                    reasons.Add("query term match");
                }
            }

            return new SimilarAssetCandidate
            {
                Path = path,
                Score = score,
                Reason = reasons.Count == 0 ? "weak fuzzy match" : string.Join(", ", reasons.Distinct(StringComparer.OrdinalIgnoreCase))
            };
        }

        static string NormalizeFuzzyToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var builder = new StringBuilder(value.Length);
            foreach (var c in value.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                    builder.Append(c);
            }

            return builder.ToString();
        }

        static int LevenshteinDistance(string a, string b)
        {
            a ??= string.Empty;
            b ??= string.Empty;

            if (a.Length == 0)
                return b.Length;
            if (b.Length == 0)
                return a.Length;

            var costs = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
                costs[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                var previous = costs[0];
                costs[0] = i;

                for (var j = 1; j <= b.Length; j++)
                {
                    var temp = costs[j];
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    costs[j] = Math.Min(Math.Min(costs[j] + 1, costs[j - 1] + 1), previous + cost);
                    previous = temp;
                }
            }

            return costs[b.Length];
        }

        static object BuildAssetDetail(string path, AssetIntelligenceParams p)
        {
            var summary = BuildAssetSummary(path, NormalizeQuery(p), p.IncludePreview, p.IncludePreview ? p.PreviewMode : AssetPreviewOutputMode.None, p.IncludeImporter, p.IncludeDependencies);
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            var typeDetails = BuildTypeDetails(path, mainAsset);

            object subAssets = null;
            if (p.IncludeSubAssets)
            {
                subAssets = AssetDatabase.LoadAllAssetsAtPath(path)
                    .Where(asset => asset != null && asset != mainAsset)
                    .Take(MaxSubAssets)
                    .Select(asset => new
                    {
                        name = asset.name,
                        type = asset.GetType().FullName,
                        instanceId = UnityApiAdapter.GetObjectId(asset),
                        hideFlags = asset.hideFlags.ToString()
                    })
                    .ToArray();
            }

            object dependencies = null;
            if (p.IncludeDependencies)
            {
                dependencies = AssetDatabase.GetDependencies(path, p.Recursive)
                    .Where(dep => !string.Equals(dep, path, StringComparison.OrdinalIgnoreCase))
                    .Where(dep => ShouldIncludePath(dep, p))
                    .Take(MaxDependencyItems)
                    .Select(dep => BuildAssetSummary(dep, null, false, AssetPreviewOutputMode.None, false, false))
                    .ToArray();
            }

            object dependents = null;
            if (p.IncludeDependents)
            {
                var depParams = p with { Path = path, Limit = Math.Min(Math.Max(1, p.Limit), 100) };
                var dependentPaths = p.UseReferenceIndex
                    ? GetReferenceIndex(depParams, p.RefreshReferenceIndex)
                        .GetDependents(path)
                        .Take(Clamp(depParams.Limit <= 0 ? DefaultLimit : depParams.Limit, 1, MaxLimit))
                        .ToList()
                    : FindDependentPaths(path, depParams);
                dependents = dependentPaths
                    .Select(dep => BuildAssetSummary(dep, null, false, AssetPreviewOutputMode.None, false, false))
                    .ToArray();
            }

            return new
            {
                summary,
                typeDetails,
                subAssets,
                dependencies,
                dependents
            };
        }

        static object BuildAssetSummary(
            string path,
            string query,
            bool includePreview,
            AssetPreviewOutputMode previewMode,
            bool includeImporter,
            bool includeDependencies)
        {
            var absolutePath = AssetPathToAbsolutePath(path);
            var existsOnDisk = !string.IsNullOrEmpty(absolutePath) && (File.Exists(absolutePath) || Directory.Exists(absolutePath));
            var fileInfo = File.Exists(absolutePath) ? new FileInfo(absolutePath) : null;
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            var guid = AssetDatabase.AssetPathToGUID(path);

            object importerInfo = null;
            if (includeImporter)
                importerInfo = BuildImporterInfo(path);

            object dependencyInfo = null;
            if (includeDependencies)
            {
                var dependencies = AssetDatabase.GetDependencies(path, true)
                    .Where(dep => !string.Equals(dep, path, StringComparison.OrdinalIgnoreCase))
                    .Where(dep => dep.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || dep.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                    .Take(30)
                    .ToArray();
                dependencyInfo = new
                {
                    count = dependencies.Length,
                    sample = dependencies
                };
            }

            object preview = null;
            if (includePreview && previewMode != AssetPreviewOutputMode.None)
                preview = BuildPreview(path, previewMode, 256);

            return new
            {
                path,
                guid,
                name = Path.GetFileNameWithoutExtension(path),
                fileName = Path.GetFileName(path),
                extension = Path.GetExtension(path),
                type = type?.FullName ?? mainAsset?.GetType().FullName ?? "Unknown",
                typeName = type?.Name ?? mainAsset?.GetType().Name ?? "Unknown",
                isFolder = AssetDatabase.IsValidFolder(path),
                isTextLike = IsTextLike(path),
                labels = GetLabels(path),
                assetBundleName = AssetImporter.GetAtPath(path)?.assetBundleName,
                existsOnDisk,
                sizeBytes = fileInfo?.Length ?? 0,
                modifiedUtc = fileInfo?.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                instanceId = mainAsset != null ? UnityApiAdapter.GetObjectId(mainAsset) : 0,
                relevance = string.IsNullOrWhiteSpace(query) ? (int?)null : ScoreAsset(path, query, new AssetIntelligenceParams()),
                importer = importerInfo,
                dependencies = dependencyInfo,
                preview
            };
        }

        static object BuildImporterInfo(string path)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null)
                return null;

            return new
            {
                type = importer.GetType().FullName,
                userData = string.IsNullOrEmpty(importer.userData) ? null : importer.userData,
                assetBundleName = string.IsNullOrEmpty(importer.assetBundleName) ? null : importer.assetBundleName,
                assetBundleVariant = string.IsNullOrEmpty(importer.assetBundleVariant) ? null : importer.assetBundleVariant,
                importSettingsMissing = importer.importSettingsMissing,
                saveAndReimportAvailable = true
            };
        }

        static object BuildTypeDetails(string path, Object mainAsset)
        {
            if (mainAsset == null)
                return null;

            switch (mainAsset)
            {
                case Texture texture:
                    return new
                    {
                        kind = "Texture",
                        width = texture.width,
                        height = texture.height,
                        dimension = texture.dimension.ToString()
                    };

                case AudioClip clip:
                    return new
                    {
                        kind = "AudioClip",
                        length = clip.length,
                        samples = clip.samples,
                        channels = clip.channels,
                        frequency = clip.frequency,
                        loadType = GetImporterProperty(path, "loadType")
                    };

                case Material material:
                    return new
                    {
                        kind = "Material",
                        shader = material.shader != null ? material.shader.name : null,
                        renderQueue = material.renderQueue,
                        enabledKeywords = material.enabledKeywords.Select(keyword => keyword.ToString()).ToArray(),
                        texturePropertyNames = material.GetTexturePropertyNames()
                    };

                case GameObject prefab:
                    return new
                    {
                        kind = "GameObjectAsset",
                        prefabAssetType = PrefabUtility.GetPrefabAssetType(prefab).ToString(),
                        prefabInstanceStatus = PrefabUtility.GetPrefabInstanceStatus(prefab).ToString(),
                        childCount = prefab.transform.childCount,
                        componentTypes = prefab.GetComponents<Component>()
                            .Where(component => component != null)
                            .Select(component => component.GetType().FullName)
                            .ToArray(),
                        hierarchyPreview = BuildPrefabHierarchyPreview(prefab, 3, 80)
                    };

                case MonoScript script:
                    var scriptClass = script.GetClass();
                    return new
                    {
                        kind = "MonoScript",
                        className = scriptClass?.FullName,
                        baseType = scriptClass?.BaseType?.FullName,
                        isMonoBehaviour = scriptClass != null && typeof(MonoBehaviour).IsAssignableFrom(scriptClass),
                        isScriptableObject = scriptClass != null && typeof(ScriptableObject).IsAssignableFrom(scriptClass)
                    };

                case SceneAsset:
                    return new
                    {
                        kind = "Scene",
                        enabledInBuildSettings = EditorBuildSettings.scenes.Any(scene => string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase) && scene.enabled),
                        buildIndex = Array.FindIndex(EditorBuildSettings.scenes, scene => string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase))
                    };

                case Shader shader:
                    return new
                    {
                        kind = "Shader",
                        name = shader.name,
                        isSupported = shader.isSupported,
                        propertyCount = shader.GetPropertyCount()
                    };

                default:
                    return new
                    {
                        kind = mainAsset.GetType().Name
                    };
            }
        }

        static object GetImporterProperty(string path, string propertyName)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null)
                return null;

            var property = importer.GetType().GetProperty(propertyName);
            if (property == null || !property.CanRead)
                return null;

            try
            {
                var value = property.GetValue(importer);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        static object[] BuildPrefabHierarchyPreview(GameObject root, int maxDepth, int maxItems)
        {
            var items = new List<object>();
            Visit(root.transform, 0);
            return items.ToArray();

            void Visit(Transform transform, int depth)
            {
                if (transform == null || depth > maxDepth || items.Count >= maxItems)
                    return;

                items.Add(new
                {
                    depth,
                    name = transform.name,
                    path = GetTransformPath(transform),
                    components = transform.GetComponents<Component>()
                        .Where(component => component != null)
                        .Select(component => component.GetType().Name)
                        .ToArray()
                });

                for (var i = 0; i < transform.childCount && items.Count < maxItems; i++)
                    Visit(transform.GetChild(i), depth + 1);
            }
        }

        static string GetTransformPath(Transform transform)
        {
            var stack = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            return "/" + string.Join("/", stack);
        }

        static List<string> FindDependentPaths(string targetPath, AssetIntelligenceParams p)
        {
            var guid = AssetDatabase.AssetPathToGUID(targetPath);
            var maxScan = Clamp(p.MaxScanAssets <= 0 ? 8000 : p.MaxScanAssets, 100, 50000);
            var limit = Clamp(p.Limit <= 0 ? DefaultLimit : p.Limit, 1, MaxLimit);
            var dependents = new List<string>();

            foreach (var candidate in GetScopedAssetPaths(p).Take(maxScan))
            {
                if (string.Equals(candidate, targetPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dependencies = AssetDatabase.GetDependencies(candidate, true);
                if (dependencies.Any(dep =>
                    string.Equals(dep, targetPath, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(guid) && string.Equals(AssetDatabase.AssetPathToGUID(dep), guid, StringComparison.OrdinalIgnoreCase))))
                {
                    dependents.Add(candidate);
                    if (dependents.Count >= limit)
                        break;
                }
            }

            return dependents;
        }

        static AssetReferenceIndex GetReferenceIndex(AssetIntelligenceParams p, bool refresh)
        {
            var maxScan = Clamp(p.MaxScanAssets <= 0 ? 8000 : p.MaxScanAssets, 100, 50000);
            var scopes = BuildFolderScopes(p);
            var fingerprint = AssetReferenceIndex.BuildFingerprint(scopes, p.IncludePackages, p.IncludeFolders, p.IncludeHidden, p.Recursive, maxScan);

            if (!refresh && s_ReferenceIndex != null && string.Equals(s_ReferenceIndex.Fingerprint, fingerprint, StringComparison.Ordinal))
                return s_ReferenceIndex;

            var candidates = GetScopedAssetPaths(p)
                .Where(path => !AssetDatabase.IsValidFolder(path))
                .Take(maxScan)
                .ToList();

            var index = new AssetReferenceIndex(fingerprint, scopes, p.Recursive, maxScan, candidates.Count >= maxScan);
            foreach (var candidate in candidates)
            {
                index.AddAsset(candidate);

                string[] dependencies;
                try
                {
                    dependencies = AssetDatabase.GetDependencies(candidate, p.Recursive);
                }
                catch
                {
                    continue;
                }

                foreach (var dependency in dependencies)
                {
                    if (string.Equals(dependency, candidate, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!ShouldIncludePath(dependency, p))
                        continue;

                    index.AddDependency(candidate, dependency);
                }
            }

            s_ReferenceIndex = index;
            return index;
        }

        static object BuildReferenceIndexSummary(AssetReferenceIndex index)
        {
            return new
            {
                builtUtc = index.BuiltUtc.ToString("o", CultureInfo.InvariantCulture),
                scopes = index.Scopes,
                recursive = index.Recursive,
                maxScan = index.MaxScan,
                scanned = index.Scanned,
                indexedAssets = index.AssetCount,
                edges = index.EdgeCount,
                truncatedScan = index.TruncatedScan
            };
        }

        static string ClassifyImpactRisk(string operation, int dependentCount)
        {
            var highRiskOperation = IsStructuralImpactOperation(operation);
            if (dependentCount == 0)
                return highRiskOperation ? "Low" : "None";

            if (dependentCount <= 3)
                return highRiskOperation ? "Medium" : "Low";

            if (dependentCount <= 20)
                return highRiskOperation ? "High" : "Medium";

            return "High";
        }

        static bool IsStructuralImpactOperation(string operation)
        {
            if (string.IsNullOrWhiteSpace(operation))
                return false;

            return operation.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   operation.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   operation.IndexOf("rename", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string[] BuildImpactGuidance(string operation, int dependentCount)
        {
            var guidance = new List<string>();
            if (dependentCount > 0)
            {
                guidance.Add("Review dependents before editing this asset; these files reference it directly or through recursive dependencies.");
            }
            else
            {
                guidance.Add("No indexed dependents were found in the current scope.");
            }

            if (IsStructuralImpactOperation(operation))
            {
                guidance.Add("For move/rename/delete, prefer Unity AssetDatabase operations so GUID references stay valid.");
            }

            guidance.Add("RefreshReferenceIndex=true rebuilds the cache after imports, moves, deletes, or package changes.");
            return guidance.ToArray();
        }

        static object[] BuildIncomingReferenceLocationGroups(string targetPath, IEnumerable<string> dependentPaths, AssetIntelligenceParams p)
        {
            var targetGuid = AssetDatabase.AssetPathToGUID(targetPath);
            return BuildReferenceLocationGroups(
                dependentPaths,
                sourcePath => targetPath,
                sourcePath => targetGuid,
                "incoming",
                p);
        }

        static object[] BuildOutgoingReferenceLocationGroups(string sourcePath, IEnumerable<string> dependencyPaths, AssetIntelligenceParams p)
        {
            return BuildReferenceLocationGroups(
                dependencyPaths,
                dependencyPath => dependencyPath,
                AssetDatabase.AssetPathToGUID,
                "outgoing",
                p,
                fixedSourcePath: sourcePath);
        }

        static object[] BuildReferenceLocationGroups(
            IEnumerable<string> paths,
            Func<string, string> targetPathSelector,
            Func<string, string> targetGuidSelector,
            string direction,
            AssetIntelligenceParams p,
            string fixedSourcePath = null)
        {
            var groups = new List<object>();
            var remaining = Clamp(p.MaxReferenceLocations <= 0 ? 50 : p.MaxReferenceLocations, 1, 500);
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                if (remaining <= 0)
                    break;

                var sourcePath = fixedSourcePath ?? path;
                var targetPath = targetPathSelector(path);
                var targetGuid = targetGuidSelector(path);
                if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetGuid))
                    continue;

                var locations = AssetReferenceLocator.FindGuidReferences(sourcePath, targetGuid, targetPath, remaining);
                if (locations.Length == 0)
                    continue;

                groups.Add(new
                {
                    direction,
                    source = sourcePath,
                    target = targetPath,
                    targetGuid,
                    count = locations.Length,
                    locations
                });
                remaining -= locations.Length;
            }

            return groups.ToArray();
        }

        static PreviewData BuildPreview(string path, AssetPreviewOutputMode mode, int requestedSize)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
                return null;

            requestedSize = Clamp(requestedSize <= 0 ? 256 : requestedSize, 32, MaxPreviewSize);

            Texture2D preview = AssetPreview.GetAssetPreview(asset);
            if (preview == null)
                preview = AssetPreview.GetMiniThumbnail(asset);
            if (preview == null)
                return null;

            Texture2D readable = null;
            RenderTexture rt = null;
            var previous = RenderTexture.active;

            try
            {
                var width = Math.Min(requestedSize, Math.Max(1, preview.width));
                var height = Math.Min(requestedSize, Math.Max(1, preview.height));
                rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(preview, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply();

                var png = readable.EncodeToPNG();
                if (png == null || png.Length == 0)
                    return null;

                string filePath = null;
                string fileUri = null;
                string base64 = null;

                if (mode == AssetPreviewOutputMode.File || mode == AssetPreviewOutputMode.Both)
                {
                    var directory = GetPreviewDirectory();
                    Directory.CreateDirectory(directory);
                    var fileName = $"{SanitizeFileName(Path.GetFileNameWithoutExtension(path))}_{ShortHash(path)}.png";
                    filePath = Path.Combine(directory, fileName);
                    File.WriteAllBytes(filePath, png);
                    fileUri = new Uri(filePath).AbsoluteUri;
                }

                if (mode == AssetPreviewOutputMode.Base64 || mode == AssetPreviewOutputMode.Both)
                    base64 = Convert.ToBase64String(png);

                return new PreviewData
                {
                    Width = width,
                    Height = height,
                    Format = "png",
                    Path = NormalizePath(filePath),
                    FileUri = fileUri,
                    Base64 = base64,
                    Bytes = png.Length
                };
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null)
                    RenderTexture.ReleaseTemporary(rt);
                if (readable != null)
                    Object.DestroyImmediate(readable);
            }
        }

        static string GetPreviewDirectory()
        {
            var identity = ProjectIdentity.GetOrCreate();
            var projectId = string.IsNullOrEmpty(identity.ProjectId)
                ? "unknown"
                : identity.ProjectId.Substring(0, Math.Min(8, identity.ProjectId.Length));
            var projectFolder = $"{SanitizeFileName(identity.ProjectName)}_{projectId}";
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".unibridge",
                "asset-previews",
                projectFolder);
        }

        static bool IsFolderPath(string path)
        {
            return !string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path);
        }

        static string NormalizeAssetPath(string path, bool assumeAssetRelative)
        {
            return ProjectPathResolver.NormalizeAssetPath(path, assumeAssetRelative);
        }

        static bool AssetPathExists(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            if (AssetDatabase.IsValidFolder(path))
                return true;

            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                return true;

            var absolute = AssetPathToAbsolutePath(path);
            return !string.IsNullOrEmpty(absolute) && (File.Exists(absolute) || Directory.Exists(absolute));
        }

        static string AssetPathToAbsolutePath(string path)
        {
            return ProjectPathResolver.ToAbsolutePath(path, assumeAssetRelative: false);
        }

        static string GetProjectRoot()
        {
            return ProjectPathResolver.ProjectRoot;
        }

        static bool IsTextLike(string path)
        {
            var ext = Path.GetExtension(path);
            if (k_TextExtensions.Contains(ext))
                return true;

            var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            return mainAsset is TextAsset || mainAsset is MonoScript;
        }

        static TextReadPayload BuildTextReadPayload(string text, AssetIntelligenceParams p, int maxChars)
        {
            if (p.Chunks != null && p.Chunks.Length > 0)
            {
                var chunks = BuildTextChunks(text, p.Chunks, maxChars);
                return new TextReadPayload
                {
                    Mode = "chunks",
                    Text = null,
                    Chunks = chunks,
                    Slice = new
                    {
                        totalLines = CountLines(text),
                        truncated = chunks.Any(chunk => chunk.Truncated),
                        reason = "chunks"
                    }
                };
            }

            var sliced = SliceText(text, p, maxChars);
            return new TextReadPayload
            {
                Mode = "slice",
                Text = sliced.Text,
                Chunks = null,
                Slice = new
                {
                    sliced.StartLine,
                    sliced.EndLine,
                    sliced.TotalLines,
                    sliced.Truncated,
                    sliced.Reason
                }
            };
        }

        static TextChunkResult[] BuildTextChunks(string text, AssetTextChunk[] requestedChunks, int maxChars)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var totalLines = lines.Length;
            var remainingChars = maxChars;
            var results = new List<TextChunkResult>();

            foreach (var requested in requestedChunks.Where(chunk => chunk != null))
            {
                var start = Clamp(requested.StartLine <= 0 ? 1 : requested.StartLine, 1, Math.Max(1, totalLines));
                var lineCount = requested.LineCount <= 0 ? 80 : requested.LineCount;
                var end = requested.EndLine >= start
                    ? requested.EndLine
                    : start + lineCount - 1;
                end = Clamp(end, start, Math.Max(start, totalLines));

                var content = string.Join("\n", lines.Skip(start - 1).Take(end - start + 1));
                var truncated = false;
                if (content.Length > remainingChars)
                {
                    content = content.Substring(0, Math.Max(0, remainingChars));
                    truncated = true;
                }

                results.Add(new TextChunkResult
                {
                    StartLine = start,
                    EndLine = end,
                    TotalLines = totalLines,
                    Truncated = truncated,
                    Content = content
                });

                remainingChars -= content.Length;
                if (remainingChars <= 0)
                    break;
            }

            return results.ToArray();
        }

        static TextSlice SliceText(string text, AssetIntelligenceParams p, int maxChars)
        {
            if (p.HeadBytes > 0)
            {
                var head = text.Substring(0, Math.Min(text.Length, p.HeadBytes));
                return new TextSlice(head, 1, CountLines(head), CountLines(text), text.Length > head.Length, "head_bytes");
            }

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var totalLines = lines.Length;
            var start = Math.Max(1, p.StartLine);
            var count = p.LineCount;
            var reason = "line_range";

            if (!string.IsNullOrWhiteSpace(p.Pattern))
            {
                var matchLine = FindFirstMatchingLine(lines, p.Pattern);
                if (matchLine > 0)
                {
                    var before = p.LineCount > 0 ? Math.Max(2, p.LineCount / 3) : 20;
                    start = Math.Max(1, matchLine - before);
                    count = p.LineCount > 0 ? p.LineCount : 80;
                    reason = "pattern_window";
                }
            }

            if (p.TailLines > 0)
            {
                count = Math.Min(totalLines, p.TailLines);
                start = Math.Max(1, totalLines - count + 1);
                reason = "tail_lines";
            }

            if (count <= 0)
                count = totalLines - start + 1;

            start = Clamp(start, 1, Math.Max(1, totalLines));
            var end = Clamp(start + count - 1, start, Math.Max(start, totalLines));
            var selected = string.Join("\n", lines.Skip(start - 1).Take(end - start + 1));
            var truncated = false;

            if (selected.Length > maxChars)
            {
                selected = selected.Substring(0, maxChars);
                truncated = true;
                reason += "+max_chars";
            }

            return new TextSlice(selected, start, end, totalLines, truncated || end < totalLines || start > 1, reason);
        }

        static int FindFirstMatchingLine(string[] lines, string pattern)
        {
            try
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                        return i + 1;
                }
            }
            catch
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        return i + 1;
                }
            }

            return -1;
        }

        static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            var count = 1;
            foreach (var c in text)
            {
                if (c == '\n')
                    count++;
            }
            return count;
        }

        static string[] GetLabels(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            return asset != null ? AssetDatabase.GetLabels(asset) : Array.Empty<string>();
        }

        static string GetAssetTypeName(string path)
        {
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type != null)
                return type.Name;

            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            return asset != null ? asset.GetType().Name : (AssetDatabase.IsValidFolder(path) ? "Folder" : "Unknown");
        }

        static LightAssetRecord BuildLightRecord(string path)
        {
            return new LightAssetRecord
            {
                Path = path,
                TypeName = GetAssetTypeName(path),
                Extension = Path.GetExtension(path),
                SizeBytes = GetFileSize(path),
                ModifiedUtc = GetModifiedUtc(path)
            };
        }

        static object ToLightAssetData(LightAssetRecord record)
        {
            return new
            {
                path = record.Path,
                type = record.TypeName,
                extension = record.Extension,
                sizeBytes = record.SizeBytes,
                modifiedUtc = record.ModifiedUtc?.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        static string GetTopFolder(string path)
        {
            var parts = path.Split('/');
            if (parts.Length >= 2)
                return parts[0] + "/" + parts[1];
            return parts.Length > 0 ? parts[0] : "(unknown)";
        }

        static long GetFileSize(string path)
        {
            var absolutePath = AssetPathToAbsolutePath(path);
            return !string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath)
                ? new FileInfo(absolutePath).Length
                : 0;
        }

        static DateTime? GetModifiedUtc(string path)
        {
            var absolutePath = AssetPathToAbsolutePath(path);
            if (!string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath))
                return File.GetLastWriteTimeUtc(absolutePath);

            return null;
        }

        static string ComputeSha256(string absolutePath)
        {
            using var stream = File.OpenRead(absolutePath);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        static string SanitizeFileName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "asset";

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
                builder.Append(invalid.Contains(c) ? '_' : c);

            var result = builder.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "asset" : result;
        }

        static string ShortHash(string value)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var c in value ?? string.Empty)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return hash.ToString("x8");
            }
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        sealed class RankedAsset
        {
            public RankedAsset(string path, int score)
            {
                Path = path;
                Score = score;
            }

            public string Path { get; }
            public int Score { get; }
        }

        sealed class LightAssetRecord
        {
            public string Path;
            public string TypeName;
            public string Extension;
            public long SizeBytes;
            public DateTime? ModifiedUtc;
        }

        sealed class TextSlice
        {
            public TextSlice(string text, int startLine, int endLine, int totalLines, bool truncated, string reason)
            {
                Text = text;
                StartLine = startLine;
                EndLine = endLine;
                TotalLines = totalLines;
                Truncated = truncated;
                Reason = reason;
            }

            public string Text { get; }
            public int StartLine { get; }
            public int EndLine { get; }
            public int TotalLines { get; }
            public bool Truncated { get; }
            public string Reason { get; }
        }

        sealed class TextReadPayload
        {
            public string Mode;
            public string Text;
            public TextChunkResult[] Chunks;
            public object Slice;
        }

        sealed class TextChunkResult
        {
            public int StartLine;
            public int EndLine;
            public int TotalLines;
            public bool Truncated;
            public string Content;
        }

        sealed class AssetContextRequest
        {
            public AssetContextRequest(string kind, string value)
            {
                Kind = kind;
                Value = value;
            }

            public string Kind { get; }
            public string Value { get; }
        }

        sealed class PreviewData
        {
            public int Width;
            public int Height;
            public string Format;
            public string Path;
            public string FileUri;
            public string Base64;
            public int Bytes;
        }

        sealed class SimilarAssetCandidate
        {
            public string Path;
            public int Score;
            public string Reason;
        }

        sealed class ReferenceCount
        {
            public string Path;
            public int Count;
        }

        sealed class AssetReferenceIndex
        {
            readonly Dictionary<string, HashSet<string>> _dependenciesByAsset = new(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, HashSet<string>> _dependentsByAsset = new(StringComparer.OrdinalIgnoreCase);

            public AssetReferenceIndex(string fingerprint, string[] scopes, bool recursive, int maxScan, bool truncatedScan)
            {
                Fingerprint = fingerprint;
                Scopes = scopes ?? Array.Empty<string>();
                Recursive = recursive;
                MaxScan = maxScan;
                TruncatedScan = truncatedScan;
                BuiltUtc = DateTime.UtcNow;
            }

            public string Fingerprint { get; }
            public string[] Scopes { get; }
            public bool Recursive { get; }
            public int MaxScan { get; }
            public bool TruncatedScan { get; }
            public DateTime BuiltUtc { get; }
            public int Scanned { get; private set; }
            public int AssetCount => _dependenciesByAsset.Count;
            public int EdgeCount { get; private set; }

            public static string BuildFingerprint(string[] scopes, bool includePackages, bool includeFolders, bool includeHidden, bool recursive, int maxScan)
            {
                var scopeText = string.Join("|", (scopes ?? Array.Empty<string>()).OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase));
                return $"{scopeText};packages={includePackages};folders={includeFolders};hidden={includeHidden};recursive={recursive};max={maxScan}";
            }

            public void AddAsset(string path)
            {
                if (string.IsNullOrEmpty(path))
                    return;

                if (!_dependenciesByAsset.ContainsKey(path))
                {
                    _dependenciesByAsset[path] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Scanned++;
                }
            }

            public void AddDependency(string assetPath, string dependencyPath)
            {
                if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(dependencyPath))
                    return;

                AddAsset(assetPath);
                if (!_dependenciesByAsset[assetPath].Add(dependencyPath))
                    return;

                if (!_dependentsByAsset.TryGetValue(dependencyPath, out var dependents))
                {
                    dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _dependentsByAsset[dependencyPath] = dependents;
                }

                dependents.Add(assetPath);
                EdgeCount++;
            }

            public IEnumerable<string> GetDependencies(string path)
            {
                return !string.IsNullOrEmpty(path) && _dependenciesByAsset.TryGetValue(path, out var dependencies)
                    ? dependencies.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    : Enumerable.Empty<string>();
            }

            public IEnumerable<string> GetDependents(string path)
            {
                return !string.IsNullOrEmpty(path) && _dependentsByAsset.TryGetValue(path, out var dependents)
                    ? dependents.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    : Enumerable.Empty<string>();
            }

            public int GetDependencyCount(string path)
            {
                return !string.IsNullOrEmpty(path) && _dependenciesByAsset.TryGetValue(path, out var dependencies)
                    ? dependencies.Count
                    : 0;
            }

            public int GetDependentCount(string path)
            {
                return !string.IsNullOrEmpty(path) && _dependentsByAsset.TryGetValue(path, out var dependents)
                    ? dependents.Count
                    : 0;
            }

            public IEnumerable<ReferenceCount> GetMostReferenced(int limit)
            {
                return _dependentsByAsset
                    .Select(pair => new ReferenceCount { Path = pair.Key, Count = pair.Value.Count })
                    .OrderByDescending(item => item.Count)
                    .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .Take(limit);
            }

            public object[] GetEdges(int limit)
            {
                return _dependenciesByAsset
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(pair => pair.Value
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .Select(dependency => (object)new
                        {
                            source = pair.Key,
                            target = dependency
                        }))
                    .Take(limit)
                    .ToArray();
            }

            public object[] GetEdgesFor(string path, int limit)
            {
                var edges = new List<object>();

                foreach (var dependency in GetDependencies(path))
                {
                    edges.Add(new { source = path, target = dependency });
                    if (edges.Count >= limit)
                        return edges.ToArray();
                }

                foreach (var dependent in GetDependents(path))
                {
                    edges.Add(new { source = dependent, target = path });
                    if (edges.Count >= limit)
                        break;
                }

                return edges.ToArray();
            }
        }
    }
}
