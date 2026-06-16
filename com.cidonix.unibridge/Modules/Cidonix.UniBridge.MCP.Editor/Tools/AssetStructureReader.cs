using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read-only structure view for prefab assets and already-loaded scene assets.
    /// </summary>
    internal static class AssetStructureReader
    {
        const int DefaultMaxDepth = 5;
        const int DefaultMaxNodes = 300;
        const int HardMaxDepth = 16;
        const int HardMaxNodes = 5000;
        const int DefaultSearchLimit = 50;
        const int MaxSearchLimit = 500;
        const int DefaultSerializedProperties = 120;
        const int HardMaxSerializedProperties = 1000;
        const int HardFieldDepth = 8;
        const int HardArrayItems = 200;

        public static object Handle(string assetPath, AssetIntelligenceParams parameters)
        {
            var p = parameters ?? new AssetIntelligenceParams();
            assetPath = NormalizeAssetPath(assetPath);

            if (string.IsNullOrWhiteSpace(assetPath))
                return Response.Error("AssetStructure requires a resolved asset path.");

            var extension = Path.GetExtension(assetPath).ToLowerInvariant();
            if (extension != ".prefab" && extension != ".unity")
            {
                return Response.Error("AssetStructure supports prefab assets and already-loaded scene assets only.", new
                {
                    action = "Structure",
                    assetPath,
                    extension,
                    hint = "Use AssetIntelligence Read/Context/Serialize for text or non-hierarchy assets."
                });
            }

            if (extension == ".prefab")
                return WithPrefabRoots(assetPath, p, roots => BuildResponse(assetPath, "Prefab", roots, p));

            if (TryFindLoadedScene(assetPath, out var scene))
                return BuildResponse(assetPath, "LoadedScene", scene.GetRootGameObjects(), p, scene.name);

            return Response.Error("Scene asset is not loaded in the Unity Editor.", new
            {
                action = "Structure",
                assetPath,
                hint = "Open or load the scene first, or use AssetIntelligence Read for raw YAML text and SceneHierarchyExport for loaded scene exports."
            });
        }

        static object WithPrefabRoots(string assetPath, AssetIntelligenceParams p, Func<GameObject[], object> read)
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null
                && string.Equals(prefabStage.assetPath, assetPath, StringComparison.OrdinalIgnoreCase)
                && prefabStage.prefabContentsRoot != null)
            {
                return read(new[] { prefabStage.prefabContentsRoot });
            }

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(assetPath);
                if (root == null)
                    return Response.Error($"Prefab could not be loaded: {assetPath}");

                return read(new[] { root });
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static object BuildResponse(string assetPath, string assetKind, GameObject[] roots, AssetIntelligenceParams p, string sceneName = null)
        {
            var options = StructureOptions.From(p);
            var nodes = BuildNodes(roots ?? Array.Empty<GameObject>());
            if (!string.IsNullOrWhiteSpace(options.PathPrefix))
                nodes = SelectPrefix(nodes, options.PathPrefix.Trim());

            var flat = Flatten(nodes).ToList();
            var summary = BuildSummary(assetPath, assetKind, sceneName, roots ?? Array.Empty<GameObject>(), flat);

            return p.StructureMode switch
            {
                AssetStructureMode.Search => BuildSearchResponse(assetPath, assetKind, sceneName, nodes, flat, summary, options),
                AssetStructureMode.Read => BuildReadResponse(assetPath, assetKind, sceneName, nodes, flat, summary, options),
                _ => BuildListResponse(assetPath, assetKind, sceneName, nodes, flat, summary, options)
            };
        }

        static object BuildListResponse(
            string assetPath,
            string assetKind,
            string sceneName,
            List<StructureNode> roots,
            List<StructureNode> flat,
            object summary,
            StructureOptions options)
        {
            var counter = 0;
            var rootObjects = roots
                .Select(node => ToListNode(node, options, depth: 0, ref counter))
                .Where(node => node != null)
                .ToArray();

            return Response.Success($"Built structure list for '{assetPath}'.", new
            {
                action = "Structure",
                mode = "List",
                assetPath,
                assetKind,
                sceneName,
                summary,
                returnedObjects = counter,
                totalObjects = flat.Count,
                truncated = counter < flat.Count,
                roots = rootObjects,
                guidance = new[]
                {
                    "Use StructureMode=Search with Query/ComponentFilter/MatchFields to find a node without reading the full hierarchy.",
                    "Use StructureMode=Read with ObjectPath set to path or indexedPath for component and serialized field drill-down."
                }
            });
        }

        static object BuildSearchResponse(
            string assetPath,
            string assetKind,
            string sceneName,
            List<StructureNode> roots,
            List<StructureNode> flat,
            object summary,
            StructureOptions options)
        {
            if (!options.HasSearchFilters)
            {
                return Response.Error("AssetStructure Search requires Query or ComponentFilter.", new
                {
                    action = "Structure",
                    mode = "Search",
                    assetPath,
                    hint = "Set Query for name/path/component/tag/layer/prefab matching, ComponentFilter for a component type, or MatchFields=fields to include serialized field matching."
                });
            }

            var matches = flat
                .Select(node => new SearchMatch(node, NodeMatchReasons(node, options).ToArray()))
                .Where(match => match.Reasons.Length > 0)
                .ToList();

            var returned = matches
                .Take(options.Limit)
                .Select(match => new
                {
                    match.Node.name,
                    match.Node.path,
                    match.Node.indexedPath,
                    match.Node.activeSelf,
                    match.Node.activeInHierarchy,
                    match.Node.tag,
                    match.Node.layerName,
                    componentTypes = match.Node.ComponentTypeNames,
                    prefabSource = match.Node.prefabSource,
                    matchedBy = match.Reasons
                })
                .ToArray();

            return Response.Success($"Asset structure search matched {matches.Count} object(s).", new
            {
                action = "Structure",
                mode = "Search",
                assetPath,
                assetKind,
                sceneName,
                query = options.Query,
                componentFilter = options.ComponentFilters,
                matchFields = options.MatchFieldLabels,
                summary,
                totalObjects = flat.Count,
                totalMatches = matches.Count,
                returned = returned.Length,
                limit = options.Limit,
                truncated = matches.Count > returned.Length,
                matches = returned
            });
        }

        static object BuildReadResponse(
            string assetPath,
            string assetKind,
            string sceneName,
            List<StructureNode> roots,
            List<StructureNode> flat,
            object summary,
            StructureOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.ObjectPath))
            {
                return Response.Error("AssetStructure Read requires ObjectPath.", new
                {
                    action = "Structure",
                    mode = "Read",
                    assetPath,
                    hint = "Use StructureMode=List/Search first, then pass one returned path or indexedPath as ObjectPath."
                });
            }

            var matches = FindNodes(flat, options.ObjectPath.Trim()).ToList();
            if (matches.Count == 0)
            {
                return Response.Error("No GameObject matched ObjectPath.", new
                {
                    action = "Structure",
                    mode = "Read",
                    assetPath,
                    objectPath = options.ObjectPath,
                    suggestions = flat
                        .Where(node => ContainsIgnoreCase(node.path, options.ObjectPath) || ContainsIgnoreCase(node.indexedPath, options.ObjectPath) || ContainsIgnoreCase(node.name, options.ObjectPath))
                        .Take(10)
                        .Select(node => new { node.name, node.path, node.indexedPath, componentTypes = node.ComponentTypeNames })
                        .ToArray()
                });
            }

            if (matches.Count > 1)
            {
                return Response.Error("ObjectPath is ambiguous; use indexedPath.", new
                {
                    action = "Structure",
                    mode = "Read",
                    assetPath,
                    objectPath = options.ObjectPath,
                    matches = matches
                        .Take(20)
                        .Select(node => new { node.name, node.path, node.indexedPath, componentTypes = node.ComponentTypeNames })
                        .ToArray(),
                    matchCount = matches.Count
                });
            }

            var detail = ToDetailNode(matches[0], options);
            return Response.Success($"Read structure object '{matches[0].path}'.", new
            {
                action = "Structure",
                mode = "Read",
                assetPath,
                assetKind,
                sceneName,
                summary,
                objectPath = options.ObjectPath,
                obj = detail
            });
        }

        static object BuildSummary(string assetPath, string assetKind, string sceneName, GameObject[] roots, List<StructureNode> flat)
        {
            var componentCounts = flat
                .SelectMany(node => node.ComponentTypeNames)
                .GroupBy(type => type, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(20)
                .Select(group => new { type = group.Key, count = group.Count() })
                .ToArray();

            var duplicateGroups = flat
                .GroupBy(node => node.path, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();

            var duplicateObjectCount = duplicateGroups.Sum(group => group.Count());
            var topDuplicateGroups = duplicateGroups
                .Take(10)
                .Select(group => new
                {
                    path = group.Key,
                    count = group.Count(),
                    sampleIndexedPaths = group.Take(3).Select(node => node.indexedPath).ToArray()
                })
                .ToArray();

            return new
            {
                assetPath,
                assetKind,
                sceneName,
                rootObjects = roots.Length,
                totalObjects = flat.Count,
                inactiveObjects = flat.Count(node => !node.activeSelf || !node.activeInHierarchy),
                missingScripts = flat.Sum(node => node.missingScripts),
                prefabInstanceCount = flat.Count(node => !string.IsNullOrEmpty(node.prefabSource)),
                duplicatePathGroupCount = duplicateGroups.Count,
                duplicatePathObjectCount = duplicateObjectCount,
                topDuplicatePathGroups = topDuplicateGroups,
                componentCountByType = componentCounts
            };
        }

        static List<StructureNode> BuildNodes(GameObject[] roots)
        {
            var rootNameTotals = CountNames(roots.Select(root => root != null ? root.name : string.Empty));
            var rootOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
            var result = new List<StructureNode>();

            foreach (var root in roots)
            {
                if (root == null)
                    continue;

                var namePath = "/" + root.name;
                var indexedPath = "/" + FormatIndexedSegment(root.name, rootNameTotals, rootOrdinals);
                result.Add(BuildNode(root, namePath, indexedPath));
            }

            return result;
        }

        static StructureNode BuildNode(GameObject gameObject, string path, string indexedPath)
        {
            var components = BuildComponentRefs(gameObject);
            var node = new StructureNode
            {
                gameObject = gameObject,
                name = gameObject.name,
                path = path,
                indexedPath = indexedPath,
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                tag = SafeTag(gameObject),
                layer = gameObject.layer,
                layerName = LayerMask.LayerToName(gameObject.layer),
                prefabSource = BuildPrefabSource(gameObject),
                components = components,
                missingScripts = components.Count(component => component.IsMissing)
            };

            var childTotals = CountNames(Children(gameObject.transform).Select(child => child.name));
            var childOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < gameObject.transform.childCount; i++)
            {
                var child = gameObject.transform.GetChild(i).gameObject;
                var childPath = path + "/" + child.name;
                var childIndexedPath = indexedPath + "/" + FormatIndexedSegment(child.name, childTotals, childOrdinals);
                node.children.Add(BuildNode(child, childPath, childIndexedPath));
            }

            return node;
        }

        static object ToListNode(StructureNode node, StructureOptions options, int depth, ref int counter)
        {
            if (node == null || counter >= options.MaxNodes)
                return null;

            counter++;
            object[] children = Array.Empty<object>();
            var childrenTruncated = false;
            if (depth < options.MaxDepth)
            {
                var childItems = new List<object>();
                foreach (var child in node.children)
                {
                    if (counter >= options.MaxNodes)
                    {
                        childrenTruncated = true;
                        break;
                    }

                    var childNode = ToListNode(child, options, depth + 1, ref counter);
                    if (childNode != null)
                        childItems.Add(childNode);
                }

                children = childItems.ToArray();
                childrenTruncated |= node.children.Count > children.Length;
            }
            else
            {
                childrenTruncated = node.children.Count > 0;
            }

            return new
            {
                node.name,
                node.path,
                node.indexedPath,
                node.activeSelf,
                node.activeInHierarchy,
                node.tag,
                node.layerName,
                componentTypes = node.ComponentTypeNames,
                node.missingScripts,
                node.prefabSource,
                childCount = node.children.Count,
                childrenTruncated,
                children
            };
        }

        static object ToDetailNode(StructureNode node, StructureOptions options)
        {
            var transform = node.gameObject.transform;
            return new
            {
                node.name,
                node.path,
                node.indexedPath,
                objectId = UnityApiAdapter.GetObjectId(node.gameObject),
                node.activeSelf,
                node.activeInHierarchy,
                node.tag,
                node.layer,
                node.layerName,
                node.prefabSource,
                transform = new
                {
                    localPosition = Vector3Object(transform.localPosition),
                    localRotationEuler = Vector3Object(transform.localEulerAngles),
                    localScale = Vector3Object(transform.localScale),
                    worldPosition = Vector3Object(transform.position),
                    worldRotationEuler = Vector3Object(transform.eulerAngles)
                },
                components = node.components.Select(component => ToComponentDetail(component, options)).ToArray(),
                children = node.children.Select(child => new { child.name, child.path, child.indexedPath, componentTypes = child.ComponentTypeNames }).ToArray()
            };
        }

        static object ToComponentDetail(ComponentRef component, StructureOptions options)
        {
            if (component.IsMissing)
            {
                return new
                {
                    typeName = "(Missing Script)",
                    fullName = (string)null,
                    missingScript = true
                };
            }

            var result = new Dictionary<string, object>
            {
                ["typeName"] = component.TypeName,
                ["fullName"] = component.FullName,
                ["objectId"] = UnityApiAdapter.GetObjectId(component.Component),
                ["enabled"] = TryGetEnabled(component.Component, out var enabled) ? (bool?)enabled : null
            };

            if (component.Component is Renderer renderer)
            {
                result["renderer"] = new
                {
                    renderer.sortingLayerName,
                    renderer.sortingLayerID,
                    renderer.sortingOrder,
                    material = FormatObjectReference(renderer.sharedMaterial)
                };
            }

            if (options.IncludeSerializedProperties)
                result["serializedProperties"] = SerializeProperties(component.Component, options);

            return result;
        }

        static IEnumerable<string> NodeMatchReasons(StructureNode node, StructureOptions options)
        {
            if (node == null)
                yield break;

            if (options.ComponentFilters.Length > 0 && node.components.Any(component => ComponentMatches(component, options.ComponentFilters)))
                yield return "componentFilter";

            if (string.IsNullOrWhiteSpace(options.Query))
                yield break;

            if (options.MatchPath && (ContainsIgnoreCase(node.path, options.Query) || ContainsIgnoreCase(node.indexedPath, options.Query)))
                yield return "path";
            if (options.MatchName && ContainsIgnoreCase(node.name, options.Query))
                yield return "name";
            if (options.MatchComponent && node.components.Any(component => ComponentMatchesQuery(component, options.Query)))
                yield return "component";
            if (options.MatchTag && ContainsIgnoreCase(node.tag, options.Query))
                yield return "tag";
            if (options.MatchLayer && (ContainsIgnoreCase(node.layerName, options.Query) || string.Equals(node.layer.ToString(CultureInfo.InvariantCulture), options.Query, StringComparison.OrdinalIgnoreCase)))
                yield return "layer";
            if (options.MatchPrefab && ContainsIgnoreCase(node.prefabSource, options.Query))
                yield return "prefab";
            if ((options.MatchFieldName || options.MatchFieldValue) && SerializedFieldsMatch(node, options))
                yield return options.MatchFieldName && options.MatchFieldValue ? "serializedField" : options.MatchFieldName ? "serializedFieldName" : "serializedFieldValue";
        }

        static bool SerializedFieldsMatch(StructureNode node, StructureOptions options)
        {
            foreach (var component in node.components)
            {
                if (component.IsMissing || component.Component == null)
                    continue;

                SerializedObject serializedObject;
                try
                {
                    serializedObject = new SerializedObject(component.Component);
                }
                catch
                {
                    continue;
                }

                var prop = serializedObject.GetIterator();
                var enterChildren = true;
                while (prop.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (PropertyMatches(prop, options, 0))
                        return true;
                }
            }

            return false;
        }

        static bool PropertyMatches(SerializedProperty prop, StructureOptions options, int depth)
        {
            if (options.MatchFieldName
                && (ContainsIgnoreCase(prop.name, options.Query)
                    || ContainsIgnoreCase(prop.displayName, options.Query)
                    || ContainsIgnoreCase(prop.propertyPath, options.Query)))
                return true;

            if (options.MatchFieldValue && TryFormatPropertySearchValue(prop, out var value) && ContainsIgnoreCase(value, options.Query))
                return true;

            if (depth >= options.MaxFieldDepth || prop.propertyType != SerializedPropertyType.Generic)
                return false;

            if (prop.isArray)
            {
                var arraySize = SafeArraySize(prop);
                var max = Math.Min(arraySize, options.MaxArrayItems);
                for (var i = 0; i < max; i++)
                {
                    try
                    {
                        if (PropertyMatches(prop.GetArrayElementAtIndex(i), options, depth + 1))
                            return true;
                    }
                    catch
                    {
                    }
                }

                return false;
            }

            var copy = prop.Copy();
            var end = copy.GetEndProperty();
            var enterChildren = true;
            while (copy.NextVisible(enterChildren) && !SerializedProperty.EqualContents(copy, end))
            {
                enterChildren = false;
                if (PropertyMatches(copy, options, depth + 1))
                    return true;
            }

            return false;
        }

        static object SerializeProperties(Object obj, StructureOptions options)
        {
            var properties = new List<object>();
            var truncated = false;

            try
            {
                using var serializedObject = new SerializedObject(obj);
                var iterator = serializedObject.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    properties.Add(SerializeProperty(iterator.Copy(), options, 0, ref truncated));
                    if (properties.Count >= options.MaxSerializedProperties)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                return new
                {
                    error = "SerializedObject traversal failed.",
                    exception = ex.GetType().FullName,
                    ex.Message
                };
            }

            return new
            {
                count = properties.Count,
                truncated,
                properties
            };
        }

        static object SerializeProperty(SerializedProperty prop, StructureOptions options, int depth, ref bool truncated)
        {
            var item = new Dictionary<string, object>
            {
                ["path"] = prop.propertyPath,
                ["name"] = prop.name,
                ["displayName"] = prop.displayName,
                ["type"] = prop.propertyType.ToString()
            };

            if (prop.propertyType == SerializedPropertyType.Generic && depth < options.MaxFieldDepth)
            {
                if (prop.isArray)
                {
                    var arraySize = SafeArraySize(prop);
                    var returned = Math.Min(arraySize, options.MaxArrayItems);
                    var items = new List<object>();
                    for (var index = 0; index < returned; index++)
                    {
                        try
                        {
                            items.Add(SerializeProperty(prop.GetArrayElementAtIndex(index), options, depth + 1, ref truncated));
                        }
                        catch (Exception ex)
                        {
                            items.Add(new { index, error = ex.Message });
                        }
                    }

                    item["array"] = true;
                    item["arraySize"] = arraySize;
                    item["items"] = items;
                    item["truncated"] = arraySize > returned;
                    truncated |= arraySize > returned;
                }
                else
                {
                    var children = new List<object>();
                    var copy = prop.Copy();
                    var end = copy.GetEndProperty();
                    var enterChildren = true;
                    while (copy.NextVisible(enterChildren) && !SerializedProperty.EqualContents(copy, end))
                    {
                        enterChildren = false;
                        children.Add(SerializeProperty(copy.Copy(), options, depth + 1, ref truncated));
                        if (children.Count >= options.MaxArrayItems)
                        {
                            truncated = true;
                            break;
                        }
                    }

                    item["children"] = children;
                    item["truncated"] = truncated;
                }
            }
            else
            {
                item["value"] = SerializedPropertyPatcher.SerializePropertyValue(prop);
            }

            return item;
        }

        static bool TryFormatPropertySearchValue(SerializedProperty prop, out string value)
        {
            value = null;
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        value = prop.intValue.ToString(CultureInfo.InvariantCulture);
                        return true;
                    case SerializedPropertyType.Boolean:
                        value = prop.boolValue ? "true" : "false";
                        return true;
                    case SerializedPropertyType.Float:
                        value = prop.floatValue.ToString("G5", CultureInfo.InvariantCulture);
                        return true;
                    case SerializedPropertyType.String:
                        value = prop.stringValue;
                        return true;
                    case SerializedPropertyType.Enum:
                        value = prop.enumDisplayNames != null
                            && prop.enumValueIndex >= 0
                            && prop.enumValueIndex < prop.enumDisplayNames.Length
                                ? prop.enumDisplayNames[prop.enumValueIndex]
                                : prop.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                        return true;
                    case SerializedPropertyType.ObjectReference:
                        value = FormatObjectReference(prop.objectReferenceValue) ?? "None";
                        return true;
                    case SerializedPropertyType.Vector2:
                        value = prop.vector2Value.ToString();
                        return true;
                    case SerializedPropertyType.Vector3:
                        value = prop.vector3Value.ToString();
                        return true;
                    case SerializedPropertyType.Vector4:
                        value = prop.vector4Value.ToString();
                        return true;
                    case SerializedPropertyType.Quaternion:
                        value = prop.quaternionValue.eulerAngles.ToString();
                        return true;
                    case SerializedPropertyType.Color:
                        value = prop.colorValue.ToString();
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        static IEnumerable<StructureNode> Flatten(IEnumerable<StructureNode> roots)
        {
            foreach (var root in roots)
            {
                yield return root;
                foreach (var child in Flatten(root.children))
                    yield return child;
            }
        }

        static List<StructureNode> SelectPrefix(List<StructureNode> roots, string prefix)
        {
            var exact = Flatten(roots)
                .Where(node => PathMatches(node, prefix))
                .ToList();

            if (exact.Count > 0)
                return exact;

            return roots
                .Select(root => FilterByPrefix(root, prefix))
                .Where(node => node != null)
                .ToList();
        }

        static StructureNode FilterByPrefix(StructureNode node, string prefix)
        {
            if (node == null)
                return null;

            if (ContainsIgnoreCase(node.path, prefix) || ContainsIgnoreCase(node.indexedPath, prefix))
                return node;

            var clone = node.ShallowClone();
            foreach (var child in node.children)
            {
                var filtered = FilterByPrefix(child, prefix);
                if (filtered != null)
                    clone.children.Add(filtered);
            }

            return clone.children.Count > 0 ? clone : null;
        }

        static IEnumerable<StructureNode> FindNodes(List<StructureNode> flat, string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return Enumerable.Empty<StructureNode>();

            var normalized = NormalizeObjectPath(target);
            var exact = flat.Where(node =>
                string.Equals(NormalizeObjectPath(node.indexedPath), normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeObjectPath(node.path), normalized, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0)
                return exact;

            return flat.Where(node => string.Equals(node.name, target, StringComparison.OrdinalIgnoreCase));
        }

        static bool PathMatches(StructureNode node, string prefix)
        {
            var normalizedPrefix = NormalizeObjectPath(prefix);
            return string.Equals(NormalizeObjectPath(node.path), normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeObjectPath(node.indexedPath), normalizedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        static List<ComponentRef> BuildComponentRefs(GameObject gameObject)
        {
            return gameObject.GetComponents<Component>()
                .Select(component => new ComponentRef(component))
                .ToList();
        }

        static bool ComponentMatches(ComponentRef component, string[] filters)
        {
            if (component == null || filters == null || filters.Length == 0)
                return false;

            return filters.Any(filter => ComponentMatchesQuery(component, filter));
        }

        static bool ComponentMatchesQuery(ComponentRef component, string query)
        {
            if (component == null || string.IsNullOrWhiteSpace(query))
                return false;

            if (component.IsMissing)
                return ContainsIgnoreCase("(Missing Script)", query) || ContainsIgnoreCase("missing", query);

            return ContainsIgnoreCase(component.TypeName, query)
                || ContainsIgnoreCase(component.FullName, query)
                || ContainsIgnoreCase(component.AssemblyQualifiedName, query);
        }

        static bool TryFindLoadedScene(string assetPath, out Scene scene)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var candidate = SceneManager.GetSceneAt(i);
                if (candidate.IsValid()
                    && candidate.isLoaded
                    && string.Equals(NormalizeAssetPath(candidate.path), assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    scene = candidate;
                    return true;
                }
            }

            scene = default;
            return false;
        }

        static string NormalizeAssetPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim();
        }

        static string NormalizeObjectPath(string path)
        {
            path = (path ?? string.Empty).Replace('\\', '/').Trim();
            return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        }

        static IEnumerable<Transform> Children(Transform transform)
        {
            for (var i = 0; i < transform.childCount; i++)
                yield return transform.GetChild(i);
        }

        static Dictionary<string, int> CountNames(IEnumerable<string> names)
        {
            return names
                .Where(name => name != null)
                .GroupBy(name => name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        }

        static string FormatIndexedSegment(string name, Dictionary<string, int> totals, Dictionary<string, int> ordinals)
        {
            name ??= string.Empty;
            if (!ordinals.TryGetValue(name, out var ordinal))
                ordinal = 0;
            ordinals[name] = ordinal + 1;

            return totals.TryGetValue(name, out var total) && total > 1
                ? $"{name}[{ordinal}]"
                : name;
        }

        static string SafeTag(GameObject gameObject)
        {
            try
            {
                return gameObject.tag;
            }
            catch
            {
                return null;
            }
        }

        static string BuildPrefabSource(GameObject gameObject)
        {
            try
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(gameObject);
                return source != null ? NormalizeAssetPath(AssetDatabase.GetAssetPath(source)) : null;
            }
            catch
            {
                return null;
            }
        }

        static bool TryGetEnabled(Component component, out bool enabled)
        {
            enabled = false;
            switch (component)
            {
                case Behaviour behaviour:
                    enabled = behaviour.enabled;
                    return true;
                case Renderer renderer:
                    enabled = renderer.enabled;
                    return true;
                case Collider collider:
                    enabled = collider.enabled;
                    return true;
                default:
                    return false;
            }
        }

        static int SafeArraySize(SerializedProperty property)
        {
            try
            {
                return property.arraySize;
            }
            catch
            {
                return 0;
            }
        }

        static string FormatObjectReference(Object obj)
        {
            if (obj == null)
                return null;

            var path = NormalizeAssetPath(AssetDatabase.GetAssetPath(obj));
            var guid = !string.IsNullOrWhiteSpace(path) ? AssetDatabase.AssetPathToGUID(path) : null;
            return string.IsNullOrWhiteSpace(path)
                ? $"{obj.name} ({obj.GetType().Name})"
                : $"{obj.name} ({obj.GetType().Name}) @ {path}" + (!string.IsNullOrWhiteSpace(guid) ? $" guid:{guid}" : string.Empty);
        }

        static object Vector3Object(Vector3 value)
        {
            return new { x = value.x, y = value.y, z = value.z };
        }

        static bool ContainsIgnoreCase(string value, string query)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrWhiteSpace(query)
                && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        sealed class StructureOptions
        {
            public string Query;
            public string ObjectPath;
            public string PathPrefix;
            public string[] ComponentFilters = Array.Empty<string>();
            public string[] MatchFieldLabels = Array.Empty<string>();
            public int Limit;
            public int MaxDepth;
            public int MaxNodes;
            public int MaxFieldDepth;
            public int MaxArrayItems;
            public int MaxSerializedProperties;
            public bool IncludeSerializedProperties;
            public bool MatchPath = true;
            public bool MatchName = true;
            public bool MatchComponent = true;
            public bool MatchTag = true;
            public bool MatchLayer = true;
            public bool MatchPrefab = true;
            public bool MatchFieldName;
            public bool MatchFieldValue;

            public bool HasSearchFilters => !string.IsNullOrWhiteSpace(Query) || ComponentFilters.Length > 0;

            public static StructureOptions From(AssetIntelligenceParams p)
            {
                var options = new StructureOptions
                {
                    Query = FirstNonEmpty(p.Query, p.SearchPattern),
                    ObjectPath = p.ObjectPath,
                    PathPrefix = p.PathPrefix,
                    Limit = Clamp(p.Limit <= 0 ? DefaultSearchLimit : p.Limit, 1, MaxSearchLimit),
                    MaxDepth = Clamp(p.MaxStructureDepth ?? (p.MaxSerializedDepth <= 0 ? DefaultMaxDepth : p.MaxSerializedDepth), 0, HardMaxDepth),
                    MaxNodes = Clamp(p.MaxStructureItems ?? (p.MaxSerializedItems <= 0 ? DefaultMaxNodes : p.MaxSerializedItems), 1, HardMaxNodes),
                    MaxFieldDepth = Clamp(p.MaxFieldDepth <= 0 ? 3 : p.MaxFieldDepth, 0, HardFieldDepth),
                    MaxArrayItems = Clamp(p.MaxArrayItems <= 0 ? 30 : p.MaxArrayItems, 1, HardArrayItems),
                    MaxSerializedProperties = Clamp(p.MaxSerializedProperties <= 0 ? DefaultSerializedProperties : p.MaxSerializedProperties, 1, HardMaxSerializedProperties),
                    IncludeSerializedProperties = p.IncludeSerializedProperties
                };

                options.ComponentFilters = SplitLabels(p.ComponentFilter);
                ConfigureMatchFields(options, p.MatchFields);
                return options;
            }

            static void ConfigureMatchFields(StructureOptions options, string matchFields)
            {
                if (string.IsNullOrWhiteSpace(matchFields))
                {
                    options.MatchFieldLabels = new[] { "path", "name", "component", "tag", "layer", "prefab" };
                    return;
                }

                options.MatchPath = false;
                options.MatchName = false;
                options.MatchComponent = false;
                options.MatchTag = false;
                options.MatchLayer = false;
                options.MatchPrefab = false;
                options.MatchFieldName = false;
                options.MatchFieldValue = false;

                var labels = SplitLabels(matchFields);
                foreach (var label in labels.Select(label => label.ToLowerInvariant()))
                {
                    switch (label)
                    {
                        case "all":
                            options.MatchPath = true;
                            options.MatchName = true;
                            options.MatchComponent = true;
                            options.MatchTag = true;
                            options.MatchLayer = true;
                            options.MatchPrefab = true;
                            options.MatchFieldName = true;
                            options.MatchFieldValue = true;
                            break;
                        case "fields":
                        case "field":
                            options.MatchFieldName = true;
                            options.MatchFieldValue = true;
                            break;
                        case "fieldname":
                        case "field_name":
                            options.MatchFieldName = true;
                            break;
                        case "fieldvalue":
                        case "field_value":
                            options.MatchFieldValue = true;
                            break;
                        case "component":
                        case "components":
                            options.MatchComponent = true;
                            break;
                        case "tag":
                            options.MatchTag = true;
                            break;
                        case "layer":
                            options.MatchLayer = true;
                            break;
                        case "prefab":
                        case "prefabsource":
                            options.MatchPrefab = true;
                            break;
                        case "path":
                            options.MatchPath = true;
                            break;
                        case "name":
                            options.MatchName = true;
                            break;
                    }
                }

                options.MatchFieldLabels = labels;
            }
        }

        sealed class StructureNode
        {
            public GameObject gameObject;
            public string name;
            public string path;
            public string indexedPath;
            public bool activeSelf;
            public bool activeInHierarchy;
            public string tag;
            public int layer;
            public string layerName;
            public string prefabSource;
            public int missingScripts;
            public List<ComponentRef> components = new();
            public List<StructureNode> children = new();

            public string[] ComponentTypeNames => components
                .Select(component => component.TypeName)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .ToArray();

            public StructureNode ShallowClone()
            {
                return new StructureNode
                {
                    gameObject = gameObject,
                    name = name,
                    path = path,
                    indexedPath = indexedPath,
                    activeSelf = activeSelf,
                    activeInHierarchy = activeInHierarchy,
                    tag = tag,
                    layer = layer,
                    layerName = layerName,
                    prefabSource = prefabSource,
                    missingScripts = missingScripts,
                    components = components
                };
            }
        }

        sealed class ComponentRef
        {
            public readonly Component Component;
            public readonly bool IsMissing;
            public readonly string TypeName;
            public readonly string FullName;
            public readonly string AssemblyQualifiedName;

            public ComponentRef(Component component)
            {
                Component = component;
                IsMissing = component == null;
                if (IsMissing)
                {
                    TypeName = "(Missing Script)";
                    return;
                }

                var type = component.GetType();
                TypeName = type.Name;
                FullName = type.FullName ?? type.Name;
                AssemblyQualifiedName = type.AssemblyQualifiedName;
            }
        }

        readonly struct SearchMatch
        {
            public readonly StructureNode Node;
            public readonly string[] Reasons;

            public SearchMatch(StructureNode node, string[] reasons)
            {
                Node = node;
                Reasons = reasons;
            }
        }

        static string[] SplitLabels(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Array.Empty<string>();

            return value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
