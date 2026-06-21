#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.Search;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// agent-oriented unified search across Unity project, scene, scripts, types, shaders, and menus.
    /// </summary>
    public static class UnitySearch
    {
        const int DefaultLimit = 50;
        const int MaxLimit = 300;
        const int DefaultPerSourceLimit = 25;
        const int MaxPerSourceLimit = 150;
        const int MaxAssetScanLimit = 20000;
        const int MaxSceneScanLimit = 10000;
        const int MaxScriptScanLimit = 10000;

        static readonly string[] DefaultSources =
        {
            "Assets",
            "SceneObjects",
            "Scripts",
            "Types",
            "Shaders",
            "Menus"
        };

        public const string Title = "Search Unity project and scene";

        public const string Description = @"Search across the Unity project and current editor context.

This is the UniBridge-native version of the unified search idea. It gives agents one fast first-pass lookup over Project assets, open scene objects, C# scripts, loaded Unity/C# types, shaders, and editor menu commands. It can also use UnityEditor.Search.SearchService for structured native asset/scene provider search. Results include normalized handles and hints for the specialized tool to use next.

Args:
    Action: Search, Resolve, or Selection.
    Query: Search text.
    Sources: Optional subset of Assets, SceneObjects, Scripts, Types, Shaders, Menus, or All.
    Backend: UniBridge, NativeSearchService, or Hybrid.
    Path/Guid: Optional asset/folder scope or direct asset hint.
    Target: Optional scene-object hint.
    Types/Extensions/Labels: Optional asset filters.
    IncludePackages/IncludeInactive/IncludeComponents: Scope and scene detail controls.

Returns:
    success, message, result totals, ranked unified results, and best-match/ambiguity hints.";

        [McpTool("UniBridge_UnitySearch", Description, Title, Groups = new[] { "core", "search", "assets", "scene", "scripts" }, EnabledByDefault = true)]
        public static async Task<object> HandleCommand(UnitySearchParams parameters)
        {
            parameters ??= new UnitySearchParams();

            try
            {
                return parameters.Action switch
                {
                    UnitySearchAction.Search => await Search(parameters),
                    UnitySearchAction.Resolve => await Resolve(parameters),
                    UnitySearchAction.Selection => Selection(parameters),
                    _ => Response.Error($"Unsupported UnitySearch action '{parameters.Action}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnitySearch] {parameters.Action} failed: {ex}");
                return Response.Error($"UnitySearch action '{parameters.Action}' failed: {ex.Message}");
            }
        }

        static async Task<object> Search(UnitySearchParams parameters)
        {
            if (!HasSearchInput(parameters))
                return Response.Error("Search requires Query, Path, Guid, or Target.");

            var result = await BuildSearch(parameters);
            return Response.Success($"Found {result.Total} result(s).", BuildSearchData("Search", parameters, result));
        }

        static async Task<object> Resolve(UnitySearchParams parameters)
        {
            if (!HasSearchInput(parameters))
                return Response.Error("Resolve requires Query, Path, Guid, or Target.");

            var search = await BuildSearch(parameters);
            var data = BuildSearchData("Resolve", parameters, search);
            if (search.Results.Count == 0)
                return Response.Error("No matching Unity item was found.", data);

            return Response.Success($"Resolved best match: {search.Results[0].Name}.", data);
        }

        static object Selection(UnitySearchParams parameters)
        {
            var results = UnityEditor.Selection.objects
                .Where(obj => obj != null)
                .Select(BuildSelectionResult)
                .Where(result => result != null)
                .Cast<SearchResultRecord>()
                .ToList();

            return Response.Success($"Found {results.Count} selected object(s).", new
            {
                action = "Selection",
                returned = results.Count,
                results = results.Select(ToResponseObject).ToArray()
            });
        }

        static async Task<SearchBuildResult> BuildSearch(UnitySearchParams parameters)
        {
            var options = SearchOptions.From(parameters);
            var results = new List<SearchResultRecord>();
            var sourceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            void AddSource(string source, IEnumerable<SearchResultRecord> records)
            {
                var list = records
                    .Where(record => record != null && record.Score > 0)
                    .OrderByDescending(record => record.Score)
                    .ThenBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
                    .Take(options.PerSourceLimit)
                    .ToList();

                sourceCounts[source] = list.Count;
                results.AddRange(list);
            }

            if (options.Backend is UnitySearchBackend.UniBridge or UnitySearchBackend.Hybrid)
            {
                if (options.HasSource("Assets"))
                    AddSource("Assets", SearchAssets(options));
                if (options.HasSource("SceneObjects"))
                    AddSource("SceneObjects", SearchSceneObjects(options));
                if (options.HasSource("Scripts"))
                    AddSource("Scripts", SearchScripts(options));
                if (options.HasSource("Types"))
                    AddSource("Types", SearchTypes(options));
                if (options.HasSource("Shaders"))
                    AddSource("Shaders", SearchShaders(options));
                if (options.HasSource("Menus"))
                    AddSource("Menus", SearchMenus(options));
            }

            if (options.Backend is UnitySearchBackend.NativeSearchService or UnitySearchBackend.Hybrid)
            {
                var nativeRecords = await SearchNativeSearchService(options);
                foreach (var group in nativeRecords.GroupBy(record => record.Source, StringComparer.OrdinalIgnoreCase))
                {
                    var source = options.Backend == UnitySearchBackend.Hybrid ? $"Native{group.Key}" : group.Key;
                    sourceCounts[source] = group.Count();
                    results.AddRange(group);
                }
            }

            var distinct = results
                .GroupBy(record => record.DedupeKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(record => record.Score).First())
                .ToList();

            SortFinalResults(distinct, parameters.SortBy);

            var limited = distinct.Take(options.Limit).ToList();
            return new SearchBuildResult
            {
                Total = distinct.Count,
                Results = limited,
                SourceCounts = sourceCounts,
                Options = options
            };
        }

        static IEnumerable<SearchResultRecord> SearchAssets(SearchOptions options)
        {
            foreach (var direct in DirectAssetResults(options, "Assets"))
                yield return direct;

            var filter = BuildAssetDatabaseFilter(options, includeScriptType: false, includeShaderType: false);
            var scopes = BuildAssetScopes(options);
            string[] guids;
            try
            {
                guids = scopes.Length > 0 ? AssetDatabase.FindAssets(filter, scopes) : AssetDatabase.FindAssets(filter);
            }
            catch
            {
                guids = Array.Empty<string>();
            }

            var scanned = 0;
            foreach (var guid in guids)
            {
                if (scanned++ >= options.MaxScanAssets)
                    yield break;

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!ShouldIncludeAssetPath(path, options))
                    continue;

                if (!MatchesAssetFilters(path, options))
                    continue;

                var record = BuildAssetResult(path, guid, "Assets", options);
                if (record != null)
                    yield return record;
            }
        }

        static IEnumerable<SearchResultRecord> SearchSceneObjects(SearchOptions options)
        {
            foreach (var direct in DirectSceneResults(options))
                yield return direct;

            var scanned = 0;
            foreach (var gameObject in EnumerateSceneObjects(options.IncludeInactive))
            {
                if (scanned++ >= options.MaxSceneObjects)
                    yield break;

                var record = BuildSceneObjectResult(gameObject, options);
                if (record != null)
                    yield return record;
            }
        }

        static async Task<List<SearchResultRecord>> SearchNativeSearchService(SearchOptions options)
        {
            var providers = new List<string>();
            if (options.HasSource("Assets"))
                providers.Add("asset");
            if (options.HasSource("SceneObjects"))
                providers.Add("scene");

            if (providers.Count == 0)
                return new List<SearchResultRecord>();

            if (Application.isBatchMode)
                throw new InvalidOperationException("UnityEditor.Search.SearchService is not available in batch mode.");

            using var context = SearchService.CreateContext(providers.ToArray(), options.Query, (SearchFlags)2);
            var tcs = new TaskCompletionSource<IList<SearchItem>>();
            SearchService.Request(context, (_, items) => tcs.TrySetResult(items ?? Array.Empty<SearchItem>()), (SearchFlags)0);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(options.NativeTimeoutMs));
            if (completed != tcs.Task)
                throw new TimeoutException($"Unity SearchService did not complete within {options.NativeTimeoutMs} ms.");

            var records = new List<SearchResultRecord>();
            foreach (var item in tcs.Task.Result.Take(options.Limit * 4))
            {
                var record = BuildNativeSearchResult(item, options);
                if (record != null)
                    records.Add(record);
            }

            return records;
        }

        static SearchResultRecord BuildNativeSearchResult(SearchItem item, SearchOptions options)
        {
            if (item == null)
                return null;

            var providerId = item.provider?.id;
            if (string.Equals(providerId, "scene", StringComparison.OrdinalIgnoreCase))
            {
                var obj = UnityApiAdapter.GetObjectFromNativeSearchId(item.id);
                var gameObject = obj as GameObject ?? (obj as Component)?.gameObject;
                if (gameObject == null)
                    return null;

                var record = BuildSceneObjectResult(gameObject, options, direct: true);
                if (record == null)
                    return null;

                record.Score = item.score;
                record.MatchedFields = new[] { "nativeSearchService" };
                return record;
            }

            var path = ResolveNativeAssetPath(item);
            if (string.IsNullOrWhiteSpace(path) || !ShouldIncludeAssetPath(path, options) || !MatchesAssetFilters(path, options))
                return null;

            var assetRecord = BuildAssetResult(path, AssetDatabase.AssetPathToGUID(path), "Assets", options, direct: true);
            if (assetRecord == null)
                return null;

            assetRecord.Score = item.score;
            assetRecord.MatchedFields = new[] { "nativeSearchService" };
            return assetRecord;
        }

        static string ResolveNativeAssetPath(SearchItem item)
        {
            if (item == null)
                return null;

            if (!string.IsNullOrWhiteSpace(item.id) &&
                GlobalObjectId.TryParse(item.id, out var globalObjectId))
            {
                var path = AssetDatabase.GUIDToAssetPath(globalObjectId.assetGUID.ToString());
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
            }

            if (!string.IsNullOrWhiteSpace(item.id))
            {
                var path = AssetDatabase.GUIDToAssetPath(item.id);
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
                if (item.id.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    item.id.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                    return item.id;
            }

            return null;
        }

        static IEnumerable<SearchResultRecord> SearchScripts(SearchOptions options)
        {
            var scopes = BuildAssetScopes(options);
            string[] guids;
            try
            {
                guids = scopes.Length > 0 ? AssetDatabase.FindAssets("t:MonoScript", scopes) : AssetDatabase.FindAssets("t:MonoScript");
            }
            catch
            {
                guids = Array.Empty<string>();
            }

            var scanned = 0;
            foreach (var guid in guids)
            {
                if (scanned++ >= options.MaxScanScripts)
                    yield break;

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!ShouldIncludeAssetPath(path, options))
                    continue;
                if (!MatchesExtensionFilters(path, options))
                    continue;

                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                var type = SafeGetScriptClass(script);
                var name = type?.Name ?? Path.GetFileNameWithoutExtension(path);
                var score = Score(options, new[]
                {
                    Field(name, 10, "scriptName"),
                    Field(type?.FullName, 9, "type"),
                    Field(type?.Namespace, 4, "namespace"),
                    Field(type?.BaseType?.Name, 5, "baseType"),
                    Field(path, 6, "path"),
                    Field(Path.GetFileName(path), 7, "fileName")
                }, out var matched);

                if (score <= 0 && options.Terms.Length == 0 && options.Types.Any(typeFilter =>
                        string.Equals(typeFilter, "MonoScript", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(typeFilter, "Script", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(typeFilter, type?.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    matched = new[] { "filter" };
                    score = 100;
                }

                if (score <= 0)
                    continue;

                yield return new SearchResultRecord
                {
                    Source = "Scripts",
                    Kind = ClassifyScriptType(type),
                    Name = name,
                    Path = path,
                    Guid = guid,
                    Type = type?.FullName,
                    Score = score,
                    MatchedFields = matched,
                    SuggestedTool = "UniBridge_ScriptIntelligence",
                    SuggestedAction = "Analyze",
                    DedupeKey = $"script:{path}"
                };
            }
        }

        static IEnumerable<SearchResultRecord> SearchTypes(SearchOptions options)
        {
            foreach (var type in GetAllLoadedTypes())
            {
                if (type == null || (!type.IsClass && !type.IsEnum && !type.IsValueType))
                    continue;
                if (type.IsGenericTypeDefinition)
                    continue;

                var score = Score(options, new[]
                {
                    Field(type.Name, 10, "typeName"),
                    Field(type.FullName, 9, "fullName"),
                    Field(type.Namespace, 4, "namespace"),
                    Field(type.Assembly.GetName().Name, 3, "assembly"),
                    Field(type.BaseType?.Name, 5, "baseType")
                }, out var matched);

                if (score <= 0 && options.Terms.Length == 0 && options.Types.Any(typeFilter =>
                        string.Equals(typeFilter, "Shader", StringComparison.OrdinalIgnoreCase)))
                {
                    matched = new[] { "filter" };
                    score = 100;
                }

                if (score <= 0)
                    continue;

                yield return new SearchResultRecord
                {
                    Source = "Types",
                    Kind = ClassifyType(type),
                    Name = type.Name,
                    Path = type.FullName,
                    Type = type.FullName,
                    Score = score,
                    MatchedFields = matched,
                    SuggestedTool = "UniBridge_TypeSchema",
                    SuggestedAction = "Inspect",
                    DedupeKey = $"type:{type.AssemblyQualifiedName}"
                };
            }
        }

        static IEnumerable<SearchResultRecord> SearchShaders(SearchOptions options)
        {
            var scopes = BuildAssetScopes(options);
            string[] guids;
            try
            {
                var filter = BuildAssetDatabaseFilter(options, includeScriptType: false, includeShaderType: true);
                guids = scopes.Length > 0 ? AssetDatabase.FindAssets(filter, scopes) : AssetDatabase.FindAssets(filter);
            }
            catch
            {
                guids = Array.Empty<string>();
            }

            foreach (var guid in guids.Take(options.MaxScanAssets))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!ShouldIncludeAssetPath(path, options))
                    continue;

                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null)
                    continue;

                var score = Score(options, new[]
                {
                    Field(shader.name, 10, "shaderName"),
                    Field(path, 6, "path"),
                    Field(Path.GetFileName(path), 7, "fileName")
                }, out var matched);

                if (score <= 0)
                    continue;

                yield return new SearchResultRecord
                {
                    Source = "Shaders",
                    Kind = "Shader",
                    Name = shader.name,
                    Path = path,
                    Guid = guid,
                    Type = typeof(Shader).FullName,
                    Score = score,
                    MatchedFields = matched,
                    SuggestedTool = "UniBridge_TypeSchema",
                    SuggestedAction = "InspectShader",
                    DedupeKey = $"shader:{path}"
                };
            }
        }

        static IEnumerable<SearchResultRecord> SearchMenus(SearchOptions options)
        {
            var menuItems = TypeCache.GetMethodsWithAttribute<MenuItem>()
                .SelectMany(method => method.GetCustomAttributes(typeof(MenuItem), false).OfType<MenuItem>())
                .Select(attribute => attribute.menuItem)
                .Concat(BuiltinMenuItems())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal);

            foreach (var menuItem in menuItems)
            {
                var score = Score(options, new[]
                {
                    Field(menuItem, 10, "menuPath"),
                    Field(Path.GetFileName(menuItem.Replace('\\', '/')), 6, "menuName")
                }, out var matched);

                if (score <= 0)
                    continue;

                yield return new SearchResultRecord
                {
                    Source = "Menus",
                    Kind = "EditorMenu",
                    Name = menuItem.Split('/').Last(),
                    Path = menuItem,
                    MenuPath = menuItem,
                    Type = "UnityEditor.MenuItem",
                    Score = score,
                    MatchedFields = matched,
                    SuggestedTool = "UniBridge_ManageMenuItem",
                    SuggestedAction = "Execute",
                    DedupeKey = $"menu:{menuItem}"
                };
            }
        }

        static IEnumerable<SearchResultRecord> DirectAssetResults(SearchOptions options, string source)
        {
            if (!string.IsNullOrWhiteSpace(options.Guid))
            {
                var path = AssetDatabase.GUIDToAssetPath(options.Guid);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var direct = BuildAssetResult(path, options.Guid, source, options, direct: true);
                    if (direct != null)
                        yield return direct;
                }
            }

            if (!string.IsNullOrWhiteSpace(options.Path))
            {
                var path = NormalizeAssetPath(options.Path);
                if (AssetDatabase.LoadMainAssetAtPath(path) != null || AssetImporter.GetAtPath(path) != null)
                {
                    var direct = BuildAssetResult(path, AssetDatabase.AssetPathToGUID(path), source, options, direct: true);
                    if (direct != null)
                        yield return direct;
                }
            }
        }

        static IEnumerable<SearchResultRecord> DirectSceneResults(SearchOptions options)
        {
            var target = options.Target;
            if (string.IsNullOrWhiteSpace(target) && !IsLikelyAssetPath(options.Path))
                target = options.Path;
            if (string.IsNullOrWhiteSpace(target))
                yield break;

            foreach (var go in EnumerateSceneObjects(options.IncludeInactive))
            {
                var path = GetHierarchyPath(go);
                if (string.Equals(go.name, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path.Trim('/'), target.Trim('/'), StringComparison.OrdinalIgnoreCase))
                {
                    var record = BuildSceneObjectResult(go, options, direct: true);
                    if (record != null)
                        yield return record;
                }
            }
        }

        static SearchResultRecord BuildAssetResult(string path, string guid, string source, SearchOptions options, bool direct = false)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            var name = mainAsset != null ? mainAsset.name : Path.GetFileNameWithoutExtension(path);
            var labels = mainAsset != null
                ? AssetDatabase.GetLabels(mainAsset).Where(label => !string.IsNullOrWhiteSpace(label)).ToArray()
                : Array.Empty<string>();
            var extension = Path.GetExtension(path);
            var typeName = assetType?.FullName ?? mainAsset?.GetType().FullName;
            var kind = ClassifyAsset(path, assetType, mainAsset);
            string[] matched = Array.Empty<string>();
            var score = direct
                ? 10000
                : Score(options, new[]
                {
                    Field(name, 10, "assetName"),
                    Field(Path.GetFileName(path), 8, "fileName"),
                    Field(path, 6, "path"),
                    Field(typeName, 5, "type"),
                    Field(extension, 4, "extension"),
                    Field(string.Join(" ", labels), 3, "labels")
                }, out matched);

            if (!direct && score <= 0 && options.Terms.Length == 0 && (options.Types.Count > 0 || options.Extensions.Count > 0 || options.Labels.Count > 0))
            {
                matched = new[] { "filter" };
                score = 100;
            }

            if (!direct && score <= 0)
                return null;

            if (direct)
                matched = new[] { !string.IsNullOrWhiteSpace(options.Guid) ? "guid" : "path" };

            return new SearchResultRecord
            {
                Source = source,
                Kind = kind,
                Name = name,
                Path = path,
                Guid = string.IsNullOrWhiteSpace(guid) ? AssetDatabase.AssetPathToGUID(path) : guid,
                Type = typeName,
                Extension = extension,
                Labels = labels,
                Score = score,
                MatchedFields = matched,
                SuggestedTool = SuggestAssetTool(kind, extension),
                SuggestedAction = "Inspect",
                DedupeKey = $"asset:{path}"
            };
        }

        static SearchResultRecord BuildSceneObjectResult(GameObject gameObject, SearchOptions options, bool direct = false)
        {
            if (gameObject == null)
                return null;

            var path = GetHierarchyPath(gameObject);
            var components = options.IncludeComponents
                ? gameObject.GetComponents<Component>()
                    .Where(component => component != null)
                    .Select(component => component.GetType().Name)
                    .Distinct(StringComparer.Ordinal)
                    .Take(20)
                    .ToArray()
                : Array.Empty<string>();
            var scenePath = gameObject.scene.IsValid() ? gameObject.scene.path : null;
            var layerName = LayerMask.LayerToName(gameObject.layer);
            string[] matched = Array.Empty<string>();
            var score = direct
                ? 10000
                : Score(options, new[]
                {
                    Field(gameObject.name, 10, "objectName"),
                    Field(path, 8, "hierarchyPath"),
                    Field(gameObject.tag, 4, "tag"),
                    Field(layerName, 4, "layer"),
                    Field(string.Join(" ", components), 7, "components"),
                    Field(scenePath, 3, "scenePath")
                }, out matched);

            if (!direct && score <= 0)
                return null;

            if (direct)
                matched = new[] { "target" };

            var objectId = UnityApiAdapter.GetObjectId(gameObject);
            return new SearchResultRecord
            {
                Source = "SceneObjects",
                Kind = "GameObject",
                Name = gameObject.name,
                Path = path,
                ScenePath = scenePath,
                ObjectId = objectId,
                Type = typeof(GameObject).FullName,
                Components = components,
                Score = score,
                MatchedFields = matched,
                SuggestedTool = "UniBridge_SceneObjectView",
                SuggestedAction = "View",
                CaptureTool = "UniBridge_CaptureView",
                DedupeKey = $"scene:{objectId}"
            };
        }

        static SearchResultRecord BuildSelectionResult(Object obj)
        {
            if (obj == null)
                return null;

            if (obj is GameObject go)
            {
                var options = SearchOptions.From(new UnitySearchParams { Query = go.name });
                return BuildSceneObjectResult(go, options, direct: true);
            }

            if (obj is Component component)
            {
                var options = SearchOptions.From(new UnitySearchParams { Query = component.GetType().Name });
                var result = BuildSceneObjectResult(component.gameObject, options, direct: true);
                if (result != null)
                {
                    var objectId = UnityApiAdapter.GetObjectId(component);
                    result.Kind = "Component";
                    result.Name = component.GetType().Name;
                    result.Type = component.GetType().FullName;
                    result.ObjectId = objectId;
                    result.SuggestedTool = "UniBridge_TypeSchema";
                    result.SuggestedAction = "InspectGameObject";
                    result.DedupeKey = $"component:{objectId}";
                }

                return result;
            }

            var path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrWhiteSpace(path))
            {
                var options = SearchOptions.From(new UnitySearchParams { Query = obj.name });
                return BuildAssetResult(path, AssetDatabase.AssetPathToGUID(path), "Assets", options, direct: true);
            }

            var selectionObjectId = UnityApiAdapter.GetObjectId(obj);
            return new SearchResultRecord
            {
                Source = "Selection",
                Kind = obj.GetType().Name,
                Name = obj.name,
                Type = obj.GetType().FullName,
                ObjectId = selectionObjectId,
                Score = 10000,
                MatchedFields = new[] { "selection" },
                SuggestedTool = "UniBridge_ContextSnapshot",
                SuggestedAction = "Selection",
                DedupeKey = $"selection:{selectionObjectId}"
            };
        }

        static object BuildSearchData(string action, UnitySearchParams parameters, SearchBuildResult search)
        {
            var best = search.Results.FirstOrDefault();
            var second = search.Results.Skip(1).FirstOrDefault();
            var ambiguous = best != null && second != null && second.Score >= best.Score - 50;

            return new
            {
                action,
                query = search.Options.Query,
                terms = search.Options.Terms,
                sources = search.Options.SourceNames,
                backend = search.Options.Backend.ToString(),
                total = search.Total,
                returned = search.Results.Count,
                sourceCounts = search.SourceCounts,
                sortBy = parameters.SortBy.ToString(),
                best = best == null ? null : ToResponseObject(best),
                ambiguous,
                ambiguity = ambiguous
                    ? new
                    {
                        best = ToResponseObject(best),
                        next = ToResponseObject(second),
                        hint = "Scores are close; inspect both candidates or narrow the query."
                    }
                    : null,
                results = search.Results.Select(ToResponseObject).ToArray()
            };
        }

        static object ToResponseObject(SearchResultRecord record)
        {
            return new
            {
                source = record.Source,
                kind = record.Kind,
                name = record.Name,
                path = record.Path,
                guid = record.Guid,
                type = record.Type,
                extension = record.Extension,
                labels = record.Labels,
                scenePath = record.ScenePath,
                objectId = record.ObjectId > 0 ? record.ObjectId : (long?)null,
                objectIdString = record.ObjectId > 0 ? record.ObjectId.ToString(CultureInfo.InvariantCulture) : null,
                menuPath = record.MenuPath,
                components = record.Components,
                score = Math.Round(record.Score, 2),
                matchedFields = record.MatchedFields,
                suggestedTool = record.SuggestedTool,
                suggestedAction = record.SuggestedAction,
                captureTool = record.CaptureTool
            };
        }

        static string BuildAssetDatabaseFilter(SearchOptions options, bool includeScriptType, bool includeShaderType)
        {
            var parts = new List<string>();
            var query = StripNonTextFilters(options.Query);
            if (!string.IsNullOrWhiteSpace(query))
                parts.Add(query);

            if (includeScriptType)
            {
                parts.Add("t:MonoScript");
            }
            else if (includeShaderType)
            {
                parts.Add("t:Shader");
            }
            else
            {
                foreach (var type in options.Types)
                {
                    if (!string.IsNullOrWhiteSpace(type))
                        parts.Add($"t:{type}");
                }
            }

            foreach (var label in options.Labels)
            {
                if (!string.IsNullOrWhiteSpace(label))
                    parts.Add($"l:{label}");
            }

            return string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        static string[] BuildAssetScopes(SearchOptions options)
        {
            var scopes = new List<string>();
            foreach (var raw in options.ScopePaths)
            {
                var path = NormalizeAssetPath(raw);
                if (AssetDatabase.IsValidFolder(path))
                    scopes.Add(path);
            }

            return scopes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        static bool MatchesAssetFilters(string path, SearchOptions options)
        {
            return MatchesExtensionFilters(path, options) && MatchesTypeFilters(path, options);
        }

        static bool MatchesExtensionFilters(string path, SearchOptions options)
        {
            if (options.Extensions.Count == 0)
                return true;

            var extension = Path.GetExtension(path).TrimStart('.');
            return options.Extensions.Any(filter => string.Equals(filter.TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase));
        }

        static bool MatchesTypeFilters(string path, SearchOptions options)
        {
            if (options.Types.Count == 0)
                return true;

            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            var kind = ClassifyAsset(path, type, asset);
            return options.Types.Any(filter =>
                string.Equals(filter, kind, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filter, type?.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filter, type?.FullName, StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(filter, "Prefab", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)));
        }

        static bool ShouldIncludeAssetPath(string path, SearchOptions options)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (!options.IncludePackages && path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return false;
            if (path.StartsWith("Library/", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        static string NormalizeAssetPath(string path)
        {
            return ProjectPathResolver.NormalizeAssetPath(path, assumeAssetRelative: true) ?? string.Empty;
        }

        static bool IsLikelyAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            var normalized = path.Replace('\\', '/').Trim();
            return normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase);
        }

        static Type SafeGetScriptClass(MonoScript script)
        {
            if (script == null)
                return null;
            try
            {
                return script.GetClass();
            }
            catch
            {
                return null;
            }
        }

        static IEnumerable<GameObject> EnumerateSceneObjects(bool includeInactive)
        {
            return SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options
            {
                IncludeInactive = includeInactive,
                IncludePrefabStage = true
            });
        }

        static string GetHierarchyPath(GameObject gameObject)
        {
            return SceneObjectLocator.GetHierarchyPath(gameObject);
        }

        static double Score(SearchOptions options, IEnumerable<SearchField> fields, out string[] matchedFields)
        {
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (options.Terms.Length == 0)
            {
                matchedFields = Array.Empty<string>();
                return 0;
            }

            var score = 0.0;
            foreach (var field in fields)
            {
                if (string.IsNullOrWhiteSpace(field.Text))
                    continue;

                var lower = field.Text.ToLowerInvariant();
                var fieldScore = 0.0;
                if (string.Equals(lower, options.QueryLower, StringComparison.Ordinal))
                    fieldScore += 1000;
                else if (lower.StartsWith(options.QueryLower, StringComparison.Ordinal))
                    fieldScore += 700;
                else if (lower.Contains(options.QueryLower))
                    fieldScore += 450;

                var tokenMatches = options.Terms.Count(term => lower.Contains(term));
                if (tokenMatches == options.Terms.Length)
                    fieldScore += 250;
                else if (tokenMatches > 0 && !options.Exact)
                    fieldScore += 90 * tokenMatches;

                if (options.Exact && fieldScore < 250)
                    fieldScore = 0;

                if (fieldScore > 0)
                {
                    matched.Add(field.Name);
                    score += fieldScore * field.Weight;
                }
            }

            matchedFields = matched.ToArray();
            return score;
        }

        static SearchField Field(string text, double weight, string name) => new SearchField
        {
            Text = text,
            Weight = weight,
            Name = name
        };

        static IEnumerable<string> BuiltinMenuItems()
        {
            yield return "Assets/Refresh";
            yield return "File/Save";
            yield return "File/Save As...";
            yield return "File/Save Project";
            yield return "GameObject/Create Empty";
            yield return "GameObject/2D Object/Sprite/Square";
            yield return "GameObject/UI/Canvas";
            yield return "GameObject/UI/Event System";
            yield return "Edit/Undo";
            yield return "Edit/Redo";
            yield return "Window/General/Console";
            yield return "Window/General/Hierarchy";
            yield return "Window/General/Project";
            yield return "Window/General/Inspector";
        }

        static void SortFinalResults(List<SearchResultRecord> results, UnitySearchSortMode sortBy)
        {
            IOrderedEnumerable<SearchResultRecord> ordered = sortBy switch
            {
                UnitySearchSortMode.Source => results.OrderBy(record => SourceRank(record.Source)).ThenByDescending(record => record.Score),
                UnitySearchSortMode.Name => results.OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(record => record.Score),
                UnitySearchSortMode.Path => results.OrderBy(record => record.Path, StringComparer.OrdinalIgnoreCase).ThenByDescending(record => record.Score),
                UnitySearchSortMode.Type => results.OrderBy(record => record.Type, StringComparer.OrdinalIgnoreCase).ThenByDescending(record => record.Score),
                _ => results.OrderByDescending(record => record.Score).ThenBy(record => SourceRank(record.Source)).ThenBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
            };

            var sorted = ordered.ToList();
            results.Clear();
            results.AddRange(sorted);
        }

        static int SourceRank(string source) => source switch
        {
            "SceneObjects" => 0,
            "Assets" => 1,
            "Scripts" => 2,
            "Types" => 3,
            "Shaders" => 4,
            "Menus" => 5,
            _ => 10
        };

        static string StripNonTextFilters(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return string.Empty;

            return string.Join(" ", Regex.Split(query, "\\s+")
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Where(token => !token.StartsWith("t:", StringComparison.OrdinalIgnoreCase) &&
                                !token.StartsWith("l:", StringComparison.OrdinalIgnoreCase)));
        }

        static string[] SplitTerms(string query)
        {
            return Regex.Split(StripNonTextFilters(query ?? string.Empty).ToLowerInvariant(), "\\s+")
                .Select(term => term.Trim())
                .Where(term => term.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static bool HasSearchInput(UnitySearchParams parameters)
        {
            return !string.IsNullOrWhiteSpace(parameters.Query) ||
                   !string.IsNullOrWhiteSpace(parameters.Path) ||
                   !string.IsNullOrWhiteSpace(parameters.Guid) ||
                   !string.IsNullOrWhiteSpace(parameters.Target);
        }

        static string ClassifyAsset(string path, Type type, Object asset)
        {
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return "Prefab";
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                return "Scene";
            if (type == typeof(MonoScript))
                return "Script";
            return type?.Name ?? asset?.GetType().Name ?? "Asset";
        }

        static string ClassifyScriptType(Type type)
        {
            if (type == null)
                return "UncompiledScript";
            if (typeof(MonoBehaviour).IsAssignableFrom(type))
                return "MonoBehaviour";
            if (typeof(ScriptableObject).IsAssignableFrom(type))
                return "ScriptableObject";
            if (typeof(UnityEditor.EditorWindow).IsAssignableFrom(type))
                return "EditorWindow";
            if (typeof(UnityEditor.Editor).IsAssignableFrom(type))
                return "Editor";
            return "PlainCSharp";
        }

        static string ClassifyType(Type type)
        {
            if (type == null)
                return "Type";
            if (typeof(Component).IsAssignableFrom(type))
                return typeof(MonoBehaviour).IsAssignableFrom(type) ? "MonoBehaviour" : "Component";
            if (typeof(ScriptableObject).IsAssignableFrom(type))
                return "ScriptableObject";
            if (typeof(AssetImporter).IsAssignableFrom(type))
                return "AssetImporter";
            if (typeof(Object).IsAssignableFrom(type))
                return "UnityObject";
            if (type.IsEnum)
                return "Enum";
            return "CSharpType";
        }

        static string SuggestAssetTool(string kind, string extension)
        {
            if (string.Equals(kind, "Prefab", StringComparison.OrdinalIgnoreCase))
                return "UniBridge_ManagePrefab";
            if (string.Equals(kind, "Script", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
                return "UniBridge_ScriptIntelligence";
            if (string.Equals(kind, "Material", StringComparison.OrdinalIgnoreCase))
                return "UniBridge_ManageMaterial";
            if (string.Equals(kind, "VisualTreeAsset", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".uxml", StringComparison.OrdinalIgnoreCase))
                return "UniBridge_CaptureUIToolkit";
            return "UniBridge_AssetIntelligence";
        }

        static IEnumerable<Type> GetAllLoadedTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                    yield return type;
            }
        }

        class SearchOptions
        {
            public string Query;
            public string QueryLower;
            public string[] Terms;
            public HashSet<string> Sources;
            public string[] SourceNames;
            public string Path;
            public string Guid;
            public string Target;
            public List<string> ScopePaths;
            public List<string> Types;
            public List<string> Extensions;
            public List<string> Labels;
            public UnitySearchBackend Backend;
            public bool IncludePackages;
            public bool IncludeInactive;
            public bool IncludeComponents;
            public bool Exact;
            public int Limit;
            public int PerSourceLimit;
            public int MaxScanAssets;
            public int MaxSceneObjects;
            public int MaxScanScripts;
            public int NativeTimeoutMs;

            public bool HasSource(string source) => Sources.Contains("All") || Sources.Contains(source);

            public static SearchOptions From(UnitySearchParams parameters)
            {
                var sourceNames = NormalizeSources(parameters.Sources);
                var query = parameters.Query;
                if (string.IsNullOrWhiteSpace(query))
                    query = parameters.Target ?? parameters.Path ?? parameters.Guid ?? string.Empty;
                query ??= string.Empty;
                var scopePaths = BuildScopePaths(parameters);

                return new SearchOptions
                {
                    Query = query.Trim(),
                    QueryLower = query.Trim().ToLowerInvariant(),
                    Terms = SplitTerms(query),
                    Sources = new HashSet<string>(sourceNames, StringComparer.OrdinalIgnoreCase),
                    SourceNames = sourceNames,
                    Path = parameters.Path,
                    Guid = parameters.Guid,
                    Target = parameters.Target,
                    ScopePaths = scopePaths,
                    Types = NormalizeStrings(parameters.Types).Concat(ExtractQueryFilters(query, "t:")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Extensions = NormalizeStrings(parameters.Extensions),
                    Labels = NormalizeStrings(parameters.Labels).Concat(ExtractQueryFilters(query, "l:")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Backend = parameters.Backend,
                    IncludePackages = parameters.IncludePackages,
                    IncludeInactive = parameters.IncludeInactive,
                    IncludeComponents = parameters.IncludeComponents,
                    Exact = parameters.Exact,
                    Limit = Clamp(parameters.Limit <= 0 ? DefaultLimit : parameters.Limit, 1, MaxLimit),
                    PerSourceLimit = Clamp(parameters.PerSourceLimit <= 0 ? DefaultPerSourceLimit : parameters.PerSourceLimit, 1, MaxPerSourceLimit),
                    MaxScanAssets = Clamp(parameters.MaxScanAssets <= 0 ? 5000 : parameters.MaxScanAssets, 1, MaxAssetScanLimit),
                    MaxSceneObjects = Clamp(parameters.MaxSceneObjects <= 0 ? 3000 : parameters.MaxSceneObjects, 1, MaxSceneScanLimit),
                    MaxScanScripts = Clamp(parameters.MaxScanScripts <= 0 ? 3000 : parameters.MaxScanScripts, 1, MaxScriptScanLimit),
                    NativeTimeoutMs = Clamp(parameters.NativeTimeoutMs ?? 5000, 250, 30000)
                };
            }

            static List<string> BuildScopePaths(UnitySearchParams parameters)
            {
                var values = new List<string>();
                if (!string.IsNullOrWhiteSpace(parameters.Path))
                    values.Add(parameters.Path);
                if (parameters.Paths != null)
                    values.AddRange(parameters.Paths);
                return values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Where(IsLikelyAssetPath)
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            static string[] NormalizeSources(string[] values)
            {
                var raw = NormalizeStrings(values);
                if (raw.Count == 0 || raw.Any(value => string.Equals(value, "All", StringComparison.OrdinalIgnoreCase)))
                    return DefaultSources;

                var result = new List<string>();
                foreach (var value in raw)
                {
                    var normalized = value.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
                    switch (normalized)
                    {
                        case "asset":
                        case "assets":
                        case "project":
                        case "projectassets":
                            result.Add("Assets");
                            break;
                        case "scene":
                        case "sceneobject":
                        case "sceneobjects":
                        case "hierarchy":
                        case "gameobject":
                        case "gameobjects":
                            result.Add("SceneObjects");
                            break;
                        case "script":
                        case "scripts":
                        case "csharp":
                        case "code":
                            result.Add("Scripts");
                            break;
                        case "type":
                        case "types":
                        case "componenttypes":
                            result.Add("Types");
                            break;
                        case "shader":
                        case "shaders":
                            result.Add("Shaders");
                            break;
                        case "menu":
                        case "menus":
                        case "commands":
                            result.Add("Menus");
                            break;
                    }
                }

                return result.Count == 0
                    ? DefaultSources
                    : result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        static List<string> NormalizeStrings(string[] values)
        {
            return values == null
                ? new List<string>()
                : values.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        class SearchField
        {
            public string Text;
            public double Weight;
            public string Name;
        }

        class SearchResultRecord
        {
            public string Source;
            public string Kind;
            public string Name;
            public string Path;
            public string Guid;
            public string Type;
            public string Extension;
            public string[] Labels;
            public string ScenePath;
            public long ObjectId;
            public string MenuPath;
            public string[] Components;
            public double Score;
            public string[] MatchedFields;
            public string SuggestedTool;
            public string SuggestedAction;
            public string CaptureTool;
            public string DedupeKey;
        }

        class SearchBuildResult
        {
            public int Total;
            public List<SearchResultRecord> Results;
            public Dictionary<string, int> SourceCounts;
            public SearchOptions Options;
        }

        static IEnumerable<string> ExtractQueryFilters(string query, string prefix)
        {
            if (string.IsNullOrWhiteSpace(query))
                yield break;

            foreach (var token in Regex.Split(query, "\\s+"))
            {
                if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && token.Length > prefix.Length)
                    yield return token.Substring(prefix.Length).Trim();
            }
        }
    }
}
