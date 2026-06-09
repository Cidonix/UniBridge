#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Builds AI-readable prefab override reports and applies/reverts selected overrides.
    /// </summary>
    public static class PrefabOverrideUtility
    {
        const int DefaultMaxItems = 250;
        const int MaxSummaryItems = 80;

        public sealed class DiffOptions
        {
            public bool IncludeDefaultOverrides;
            public bool IncludeValues = true;
            public bool IncludeVariantChain = true;
            public int MaxItems = DefaultMaxItems;
        }

        public sealed class OverrideSelection
        {
            public string OverrideId;
            public string[] OverrideIds;
            public string OverrideKind;
            public string ObjectPath;
            public string ComponentType;
            public string PropertyPath;
            public bool DryRun;
        }

        public static object BuildDiff(GameObject instanceRoot, DiffOptions options = null)
        {
            options ??= new DiffOptions();
            instanceRoot = ResolveInstanceRoot(instanceRoot);

            var collected = CollectCandidates(instanceRoot, options);
            var assetPath = GetPrefabAssetPath(instanceRoot);
            var counts = BuildCounts(collected);

            return new
            {
                prefabAssetPath = ToOptionalString(assetPath),
                prefabGuid = string.IsNullOrWhiteSpace(assetPath) ? null : ToOptionalString(AssetDatabase.AssetPathToGUID(assetPath)),
                instanceRoot = BuildObjectRef(instanceRoot, instanceRoot),
                hasOverrides = collected.PublicEntries.Count > 0,
                counts,
                summary = BuildSummary(collected.PublicEntries),
                variantChain = options.IncludeVariantChain ? BuildVariantChain(instanceRoot) : null,
                overrides = new
                {
                    properties = Limit(collected.PublicEntries.Where(e => e.kind == OverrideKinds.Property).ToList(), options.MaxItems, out var propertiesTruncated),
                    objects = Limit(collected.PublicEntries.Where(e => e.kind == OverrideKinds.Object).ToList(), options.MaxItems, out var objectsTruncated),
                    addedComponents = Limit(collected.PublicEntries.Where(e => e.kind == OverrideKinds.AddedComponent).ToList(), options.MaxItems, out var addedComponentsTruncated),
                    removedComponents = Limit(collected.PublicEntries.Where(e => e.kind == OverrideKinds.RemovedComponent).ToList(), options.MaxItems, out var removedComponentsTruncated),
                    addedGameObjects = Limit(collected.PublicEntries.Where(e => e.kind == OverrideKinds.AddedGameObject).ToList(), options.MaxItems, out var addedGameObjectsTruncated),
                    removedGameObjects = Limit(collected.PublicEntries.Where(e => e.kind == OverrideKinds.RemovedGameObject).ToList(), options.MaxItems, out var removedGameObjectsTruncated)
                },
                truncated = new
                {
                    properties = propertiesTruncated,
                    objects = objectsTruncated,
                    addedComponents = addedComponentsTruncated,
                    removedComponents = removedComponentsTruncated,
                    addedGameObjects = addedGameObjectsTruncated,
                    removedGameObjects = removedGameObjectsTruncated
                },
                warnings = collected.Warnings.ToArray()
            };
        }

        public static object ApplyOrRevert(GameObject instanceRoot, OverrideSelection selection, bool apply)
        {
            selection ??= new OverrideSelection();
            instanceRoot = ResolveInstanceRoot(instanceRoot);

            var options = new DiffOptions { IncludeDefaultOverrides = true, IncludeValues = true, MaxItems = int.MaxValue };
            var collected = CollectCandidates(instanceRoot, options);
            var selected = SelectCandidates(collected.Candidates, selection).ToList();
            if (selected.Count == 0)
            {
                return Response.Error("No prefab overrides matched the selection.", new
                {
                    selection = BuildSelectionEcho(selection),
                    availableOverrideIds = collected.PublicEntries.Select(e => e.id).Take(50).ToArray()
                });
            }

            var assetPath = GetPrefabAssetPath(instanceRoot);
            if (apply && string.IsNullOrWhiteSpace(assetPath))
            {
                return Response.Error("Cannot apply overrides because the prefab asset path could not be resolved.");
            }

            var operation = apply ? "apply" : "revert";
            var selectedEntries = selected.Select(c => c.Entry).ToList();
            if (selection.DryRun)
            {
                return Response.Success($"Prefab override {operation} dry-run completed.", new
                {
                    operation,
                    dryRun = true,
                    selectedCount = selected.Count,
                    selectedOverrides = selectedEntries
                });
            }

            Undo.SetCurrentGroupName(apply ? "Apply UniBridge Prefab Overrides" : "Revert UniBridge Prefab Overrides");
            var failures = new List<object>();
            foreach (var candidate in selected)
            {
                try
                {
                    if (apply)
                    {
                        Apply(candidate, assetPath);
                    }
                    else
                    {
                        Revert(candidate);
                    }
                }
                catch (Exception e)
                {
                    failures.Add(new
                    {
                        candidate.Entry.id,
                        candidate.Entry.kind,
                        candidate.Entry.path,
                        error = e.Message
                    });
                }
            }

            if (instanceRoot.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(instanceRoot.scene);
            }

            AssetDatabase.SaveAssets();

            if (failures.Count > 0)
            {
                return Response.Error($"Prefab override {operation} completed with failures.", new
                {
                    operation,
                    dryRun = false,
                    selectedCount = selected.Count,
                    failureCount = failures.Count,
                    failures,
                    selectedOverrides = selectedEntries
                });
            }

            return Response.Success($"Prefab override {operation} completed.", new
            {
                operation,
                dryRun = false,
                selectedCount = selected.Count,
                selectedOverrides = selectedEntries,
                prefabStatus = BuildDiff(instanceRoot, new DiffOptions { IncludeDefaultOverrides = false, IncludeValues = false, MaxItems = DefaultMaxItems })
            });
        }

        public static object RemoveUnusedOverrides(GameObject instanceRoot, bool dryRun)
        {
            instanceRoot = ResolveInstanceRoot(instanceRoot);
            var before = BuildDiff(instanceRoot, new DiffOptions { IncludeDefaultOverrides = true, IncludeValues = false });

            if (dryRun)
            {
                return Response.Success("Unused prefab override cleanup dry-run completed.", new
                {
                    dryRun = true,
                    before
                });
            }

            PrefabUtility.RemoveUnusedOverrides(new[] { instanceRoot }, InteractionMode.AutomatedAction);
            if (instanceRoot.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(instanceRoot.scene);
            }

            AssetDatabase.SaveAssets();
            var after = BuildDiff(instanceRoot, new DiffOptions { IncludeDefaultOverrides = true, IncludeValues = false });

            return Response.Success("Unused prefab overrides removed.", new
            {
                dryRun = false,
                before,
                after
            });
        }

        static CollectedOverrides CollectCandidates(GameObject instanceRoot, DiffOptions options)
        {
            var result = new CollectedOverrides();
            var assetPath = GetPrefabAssetPath(instanceRoot);
            var sourceRoot = ResolveSourceRoot(instanceRoot);

            AddPropertyOverrides(result, instanceRoot, sourceRoot, options);
            AddObjectOverrides(result, instanceRoot, options);
            AddAddedComponents(result, instanceRoot);
            AddRemovedComponents(result, instanceRoot);
            AddAddedGameObjects(result, instanceRoot);
            AddRemovedGameObjects(result, instanceRoot);

            result.PublicEntries.Sort((a, b) =>
            {
                var pathCompare = string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase);
                if (pathCompare != 0) return pathCompare;
                var kindCompare = string.Compare(a.kind, b.kind, StringComparison.OrdinalIgnoreCase);
                if (kindCompare != 0) return kindCompare;
                return string.Compare(a.propertyPath, b.propertyPath, StringComparison.OrdinalIgnoreCase);
            });

            result.Candidates.Sort((a, b) =>
            {
                var pathCompare = string.Compare(a.Entry.path, b.Entry.path, StringComparison.OrdinalIgnoreCase);
                if (pathCompare != 0) return pathCompare;
                var kindCompare = string.Compare(a.Entry.kind, b.Entry.kind, StringComparison.OrdinalIgnoreCase);
                if (kindCompare != 0) return kindCompare;
                return string.Compare(a.Entry.propertyPath, b.Entry.propertyPath, StringComparison.OrdinalIgnoreCase);
            });

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                result.Warnings.Add("Prefab asset path could not be resolved.");
            }

            return result;
        }

        static void AddPropertyOverrides(CollectedOverrides result, GameObject instanceRoot, GameObject sourceRoot, DiffOptions options)
        {
            PropertyModification[] modifications;
            try
            {
                modifications = PrefabUtility.GetPropertyModifications(instanceRoot);
            }
            catch (Exception e)
            {
                result.Warnings.Add($"Failed to read property modifications: {e.Message}");
                return;
            }

            if (modifications == null || modifications.Length == 0)
            {
                return;
            }

            foreach (var modification in modifications)
            {
                if (modification == null || modification.target == null || string.IsNullOrWhiteSpace(modification.propertyPath))
                {
                    continue;
                }

                var isDefaultOverride = SafeIsDefaultOverride(modification);
                if (!options.IncludeDefaultOverrides && isDefaultOverride)
                {
                    result.DefaultOverrideCount++;
                    continue;
                }

                if (string.Equals(modification.propertyPath, "m_RootOrder", StringComparison.Ordinal))
                {
                    result.IgnoredOverrideCount++;
                    continue;
                }

                var instanceObject = ResolveInstanceObjectForSource(instanceRoot, modification.target);
                var targetObject = instanceObject ?? modification.target;
                var go = GetGameObject(targetObject);
                var path = go == null ? null : GetPath(instanceRoot.transform, go.transform);
                if (path == null && sourceRoot != null)
                {
                    var sourceGo = GetGameObject(modification.target);
                    path = sourceGo == null ? null : GetPath(sourceRoot.transform, sourceGo.transform);
                }

                path ??= "/";

                var componentType = targetObject is Component component ? component.GetType().FullName : null;
                var objectType = targetObject.GetType().FullName;
                var id = BuildId(OverrideKinds.Property, path, componentType ?? objectType, modification.propertyPath);

                var entry = new OverrideEntry
                {
                    id = id,
                    kind = OverrideKinds.Property,
                    path = path,
                    objectName = go == null ? modification.target.name : go.name,
                    objectInstanceId = UnityApiAdapter.GetObjectId(go),
                    objectType = objectType,
                    componentType = componentType,
                    propertyPath = modification.propertyPath,
                    displayPropertyPath = DisplayPropertyPath(modification.propertyPath),
                    value = options.IncludeValues ? modification.value : null,
                    objectReference = options.IncludeValues ? BuildObjectRef(modification.objectReference, instanceRoot) : null,
                    isDefaultOverride = isDefaultOverride,
                    canApply = instanceObject != null,
                    canRevert = instanceObject != null,
                    description = BuildPropertyDescription(path, componentType, modification.propertyPath, modification.value, isDefaultOverride)
                };

                result.PublicEntries.Add(entry);
                if (instanceObject != null)
                {
                    result.Candidates.Add(new OverrideCandidate
                    {
                        Entry = entry,
                        PropertyObject = instanceObject,
                        PropertyPath = modification.propertyPath
                    });
                }
                else
                {
                    result.Warnings.Add($"Property override '{id}' could not be mapped back to an instance object.");
                }
            }
        }

        static void AddObjectOverrides(CollectedOverrides result, GameObject instanceRoot, DiffOptions options)
        {
            List<ObjectOverride> overrides;
            try
            {
                overrides = PrefabUtility.GetObjectOverrides(instanceRoot, options.IncludeDefaultOverrides);
            }
            catch (Exception e)
            {
                result.Warnings.Add($"Failed to read object overrides: {e.Message}");
                return;
            }

            foreach (var objectOverride in overrides)
            {
                var instanceObject = objectOverride.instanceObject;
                if (instanceObject == null)
                {
                    continue;
                }

                var go = GetGameObject(instanceObject);
                var path = go == null ? "/" : GetPath(instanceRoot.transform, go.transform) ?? "/";
                var componentType = instanceObject is Component component ? component.GetType().FullName : null;
                var objectType = instanceObject.GetType().FullName;
                var id = BuildId(OverrideKinds.Object, path, componentType ?? objectType);
                var entry = new OverrideEntry
                {
                    id = id,
                    kind = OverrideKinds.Object,
                    path = path,
                    objectName = go == null ? instanceObject.name : go.name,
                    objectInstanceId = UnityApiAdapter.GetObjectId(go),
                    objectType = objectType,
                    componentType = componentType,
                    canApply = true,
                    canRevert = true,
                    description = $"Object override on {DescribeTarget(path, componentType ?? objectType)}."
                };

                result.PublicEntries.Add(entry);
                result.Candidates.Add(new OverrideCandidate
                {
                    Entry = entry,
                    ObjectOverride = objectOverride
                });
            }
        }

        static void AddAddedComponents(CollectedOverrides result, GameObject instanceRoot)
        {
            foreach (var added in SafeList(() => PrefabUtility.GetAddedComponents(instanceRoot), result.Warnings, "added components"))
            {
                var component = added.instanceComponent;
                if (component == null)
                {
                    continue;
                }

                var path = GetPath(instanceRoot.transform, component.transform) ?? "/";
                var componentType = component.GetType().FullName;
                var id = BuildId(OverrideKinds.AddedComponent, path, componentType, UnityApiAdapter.GetObjectId(component).ToString());
                var entry = new OverrideEntry
                {
                    id = id,
                    kind = OverrideKinds.AddedComponent,
                    path = path,
                    objectName = component.gameObject.name,
                    objectInstanceId = UnityApiAdapter.GetObjectId(component.gameObject),
                    objectType = typeof(GameObject).FullName,
                    componentType = componentType,
                    component = GameObjectSerializer.GetComponentSummaryData(component),
                    canApply = true,
                    canRevert = true,
                    description = $"Added component {componentType} on '{path}'."
                };

                result.PublicEntries.Add(entry);
                result.Candidates.Add(new OverrideCandidate
                {
                    Entry = entry,
                    AddedComponent = added
                });
            }
        }

        static void AddRemovedComponents(CollectedOverrides result, GameObject instanceRoot)
        {
            foreach (var removed in SafeList(() => PrefabUtility.GetRemovedComponents(instanceRoot), result.Warnings, "removed components"))
            {
                var component = removed.assetComponent;
                var container = removed.containingInstanceGameObject;
                var path = container == null ? "/" : GetPath(instanceRoot.transform, container.transform) ?? "/";
                var componentType = component == null ? "Missing Component" : component.GetType().FullName;
                var id = BuildId(OverrideKinds.RemovedComponent, path, componentType);
                var entry = new OverrideEntry
                {
                    id = id,
                    kind = OverrideKinds.RemovedComponent,
                    path = path,
                    objectName = container == null ? null : container.name,
                    objectInstanceId = UnityApiAdapter.GetObjectId(container),
                    objectType = typeof(GameObject).FullName,
                    componentType = componentType,
                    canApply = true,
                    canRevert = true,
                    description = $"Removed component {componentType} from '{path}'."
                };

                result.PublicEntries.Add(entry);
                result.Candidates.Add(new OverrideCandidate
                {
                    Entry = entry,
                    RemovedComponent = removed
                });
            }
        }

        static void AddAddedGameObjects(CollectedOverrides result, GameObject instanceRoot)
        {
            foreach (var added in SafeList(() => PrefabUtility.GetAddedGameObjects(instanceRoot), result.Warnings, "added GameObjects"))
            {
                var gameObject = added.instanceGameObject;
                if (gameObject == null)
                {
                    continue;
                }

                var path = GetPath(instanceRoot.transform, gameObject.transform) ?? gameObject.name;
                var id = BuildId(OverrideKinds.AddedGameObject, path, UnityApiAdapter.GetObjectId(gameObject).ToString());
                var entry = new OverrideEntry
                {
                    id = id,
                    kind = OverrideKinds.AddedGameObject,
                    path = path,
                    objectName = gameObject.name,
                    objectInstanceId = UnityApiAdapter.GetObjectId(gameObject),
                    objectType = typeof(GameObject).FullName,
                    gameObject = GameObjectSerializer.GetGameObjectData(gameObject),
                    canApply = true,
                    canRevert = true,
                    description = $"Added GameObject '{path}'."
                };

                result.PublicEntries.Add(entry);
                result.Candidates.Add(new OverrideCandidate
                {
                    Entry = entry,
                    AddedGameObject = added
                });
            }
        }

        static void AddRemovedGameObjects(CollectedOverrides result, GameObject instanceRoot)
        {
            foreach (var removed in SafeList(() => PrefabUtility.GetRemovedGameObjects(instanceRoot), result.Warnings, "removed GameObjects"))
            {
                var assetGameObject = removed.assetGameObject;
                var parent = removed.parentOfRemovedGameObjectInInstance;
                var parentPath = parent == null ? "/" : GetPath(instanceRoot.transform, parent.transform) ?? "/";
                var name = assetGameObject == null ? "Missing GameObject" : assetGameObject.name;
                var path = parentPath == "/" ? name : $"{parentPath}/{name}";
                var id = BuildId(OverrideKinds.RemovedGameObject, path);
                var entry = new OverrideEntry
                {
                    id = id,
                    kind = OverrideKinds.RemovedGameObject,
                    path = path,
                    objectName = name,
                    objectInstanceId = UnityApiAdapter.GetObjectId(parent),
                    objectType = typeof(GameObject).FullName,
                    canApply = true,
                    canRevert = true,
                    description = $"Removed GameObject '{path}'."
                };

                result.PublicEntries.Add(entry);
                result.Candidates.Add(new OverrideCandidate
                {
                    Entry = entry,
                    RemovedGameObject = removed
                });
            }
        }

        static void Apply(OverrideCandidate candidate, string assetPath)
        {
            switch (candidate.Entry.kind)
            {
                case OverrideKinds.Property:
                    var property = ResolveSerializedProperty(candidate.PropertyObject, candidate.PropertyPath);
                    PrefabUtility.ApplyPropertyOverride(property, assetPath, InteractionMode.AutomatedAction);
                    break;
                case OverrideKinds.Object:
                    candidate.ObjectOverride.Apply(assetPath, InteractionMode.AutomatedAction);
                    break;
                case OverrideKinds.AddedComponent:
                    candidate.AddedComponent.Apply(assetPath, InteractionMode.AutomatedAction);
                    break;
                case OverrideKinds.RemovedComponent:
                    candidate.RemovedComponent.Apply(assetPath, InteractionMode.AutomatedAction);
                    break;
                case OverrideKinds.AddedGameObject:
                    candidate.AddedGameObject.Apply(assetPath, InteractionMode.AutomatedAction);
                    break;
                case OverrideKinds.RemovedGameObject:
                    candidate.RemovedGameObject.Apply(assetPath, InteractionMode.AutomatedAction);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported prefab override kind '{candidate.Entry.kind}'.");
            }
        }

        static void Revert(OverrideCandidate candidate)
        {
            switch (candidate.Entry.kind)
            {
                case OverrideKinds.Property:
                    var property = ResolveSerializedProperty(candidate.PropertyObject, candidate.PropertyPath);
                    PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
                    break;
                case OverrideKinds.Object:
                    candidate.ObjectOverride.Revert(InteractionMode.AutomatedAction);
                    break;
                case OverrideKinds.AddedComponent:
                    candidate.AddedComponent.Revert(InteractionMode.AutomatedAction);
                    break;
                case OverrideKinds.RemovedComponent:
                    candidate.RemovedComponent.Revert(InteractionMode.AutomatedAction);
                    break;
                case OverrideKinds.AddedGameObject:
                    candidate.AddedGameObject.Revert(InteractionMode.AutomatedAction);
                    break;
                case OverrideKinds.RemovedGameObject:
                    candidate.RemovedGameObject.Revert(InteractionMode.AutomatedAction);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported prefab override kind '{candidate.Entry.kind}'.");
            }
        }

        static SerializedProperty ResolveSerializedProperty(Object instanceObject, string propertyPath)
        {
            if (instanceObject == null)
            {
                throw new InvalidOperationException("Cannot resolve SerializedProperty because the instance object is missing.");
            }

            var serializedObject = new SerializedObject(instanceObject);
            var property = serializedObject.FindProperty(propertyPath);
            if (property == null)
            {
                throw new InvalidOperationException($"SerializedProperty '{propertyPath}' could not be resolved on '{instanceObject.name}'.");
            }

            return property;
        }

        static IEnumerable<OverrideCandidate> SelectCandidates(IEnumerable<OverrideCandidate> candidates, OverrideSelection selection)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(selection.OverrideId))
            {
                ids.Add(selection.OverrideId);
            }

            if (selection.OverrideIds != null)
            {
                foreach (var id in selection.OverrideIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    ids.Add(id);
                }
            }

            var hasCriteria =
                ids.Count > 0 ||
                !string.IsNullOrWhiteSpace(selection.OverrideKind) ||
                !string.IsNullOrWhiteSpace(selection.ObjectPath) ||
                !string.IsNullOrWhiteSpace(selection.ComponentType) ||
                !string.IsNullOrWhiteSpace(selection.PropertyPath);

            if (!hasCriteria)
            {
                return Array.Empty<OverrideCandidate>();
            }

            return candidates.Where(candidate =>
            {
                var entry = candidate.Entry;
                if (ids.Count > 0 && !ids.Contains(entry.id))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(selection.OverrideKind) &&
                    !MatchesKind(entry.kind, selection.OverrideKind))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(selection.ObjectPath) &&
                    !string.Equals(NormalizePath(entry.path), NormalizePath(selection.ObjectPath), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(selection.ComponentType) &&
                    !MatchesType(entry.componentType, selection.ComponentType) &&
                    !MatchesType(entry.objectType, selection.ComponentType))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(selection.PropertyPath) &&
                    !string.Equals(entry.propertyPath, selection.PropertyPath, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry.displayPropertyPath, selection.PropertyPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            });
        }

        static object BuildCounts(CollectedOverrides collected)
        {
            var entries = collected.PublicEntries;
            var propertyCount = entries.Count(e => e.kind == OverrideKinds.Property);
            var objectCount = entries.Count(e => e.kind == OverrideKinds.Object);
            var addedComponentCount = entries.Count(e => e.kind == OverrideKinds.AddedComponent);
            var removedComponentCount = entries.Count(e => e.kind == OverrideKinds.RemovedComponent);
            var addedGameObjectCount = entries.Count(e => e.kind == OverrideKinds.AddedGameObject);
            var removedGameObjectCount = entries.Count(e => e.kind == OverrideKinds.RemovedGameObject);
            return new
            {
                total = propertyCount + objectCount + addedComponentCount + removedComponentCount + addedGameObjectCount + removedGameObjectCount,
                properties = propertyCount,
                objects = objectCount,
                addedComponents = addedComponentCount,
                removedComponents = removedComponentCount,
                addedGameObjects = addedGameObjectCount,
                removedGameObjects = removedGameObjectCount,
                skippedDefaultOverrides = collected.DefaultOverrideCount,
                ignoredOverrides = collected.IgnoredOverrideCount
            };
        }

        static List<object> BuildSummary(List<OverrideEntry> entries)
        {
            var summary = new List<object>();
            foreach (var entry in entries.Take(MaxSummaryItems))
            {
                summary.Add(new
                {
                    entry.kind,
                    entry.path,
                    target = entry.componentType ?? entry.objectType,
                    property = entry.displayPropertyPath,
                    entry.description
                });
            }

            if (entries.Count > MaxSummaryItems)
            {
                summary.Add(new
                {
                    kind = "truncated",
                    description = $"{entries.Count - MaxSummaryItems} more prefab overrides omitted from summary."
                });
            }

            return summary;
        }

        static object[] BuildVariantChain(GameObject instanceRoot)
        {
            var entries = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var assetPath = GetPrefabAssetPath(instanceRoot);
            while (!string.IsNullOrWhiteSpace(assetPath) && seen.Add(assetPath))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (asset == null)
                {
                    break;
                }

                entries.Add(new
                {
                    name = asset.name,
                    assetPath,
                    guid = AssetDatabase.AssetPathToGUID(assetPath),
                    prefabAssetType = PrefabUtility.GetPrefabAssetType(asset).ToString(),
                    isVariant = PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.Variant
                });

                var parent = PrefabUtility.GetCorrespondingObjectFromSource(asset) as GameObject;
                var parentPath = parent == null ? null : AssetDatabase.GetAssetPath(parent);
                if (string.IsNullOrWhiteSpace(parentPath) || string.Equals(parentPath, assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                assetPath = parentPath;
            }

            return entries.ToArray();
        }

        static GameObject ResolveInstanceRoot(GameObject target)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target), "Prefab target cannot be null.");
            }

            var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(target);
            if (instanceRoot == null)
            {
                throw new InvalidOperationException($"GameObject '{target.name}' is not part of a prefab instance.");
            }

            return instanceRoot;
        }

        static GameObject ResolveSourceRoot(GameObject instanceRoot)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot) as GameObject;
            if (source != null)
            {
                return source;
            }

            return PrefabUtility.GetCorrespondingObjectFromOriginalSource(instanceRoot) as GameObject;
        }

        static Object ResolveInstanceObjectForSource(GameObject instanceRoot, Object sourceObject)
        {
            if (sourceObject == null)
            {
                return null;
            }

            if (IsUnderRoot(GetGameObject(sourceObject), instanceRoot))
            {
                return sourceObject;
            }

            if (sourceObject is GameObject sourceGameObject)
            {
                return FindInstanceGameObjectForSource(instanceRoot, sourceGameObject);
            }

            if (sourceObject is Component sourceComponent)
            {
                var instanceGameObject = FindInstanceGameObjectForSource(instanceRoot, sourceComponent.gameObject);
                if (instanceGameObject == null)
                {
                    return null;
                }

                var typeFallback = (Component)null;
                foreach (var component in instanceGameObject.GetComponents<Component>())
                {
                    if (component == null)
                    {
                        continue;
                    }

                    if (SameObject(PrefabUtility.GetCorrespondingObjectFromSource(component), sourceObject) ||
                        SameObject(PrefabUtility.GetCorrespondingObjectFromOriginalSource(component), sourceObject))
                    {
                        return component;
                    }

                    if (typeFallback == null && component.GetType() == sourceComponent.GetType())
                    {
                        typeFallback = component;
                    }
                }

                return typeFallback;
            }

            return null;
        }

        static GameObject FindInstanceGameObjectForSource(GameObject instanceRoot, GameObject sourceGameObject)
        {
            foreach (var transform in EnumerateTransforms(instanceRoot.transform))
            {
                var go = transform.gameObject;
                if (SameObject(PrefabUtility.GetCorrespondingObjectFromSource(go), sourceGameObject) ||
                    SameObject(PrefabUtility.GetCorrespondingObjectFromOriginalSource(go), sourceGameObject))
                {
                    return go;
                }
            }

            return null;
        }

        static IEnumerable<Transform> EnumerateTransforms(Transform root)
        {
            yield return root;
            for (var i = 0; i < root.childCount; i++)
            {
                foreach (var child in EnumerateTransforms(root.GetChild(i)))
                {
                    yield return child;
                }
            }
        }

        static string GetPath(Transform root, Transform target)
        {
            if (root == null || target == null)
            {
                return null;
            }

            if (root == target)
            {
                return "/";
            }

            var parts = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            if (current != root)
            {
                return null;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        static bool IsUnderRoot(GameObject gameObject, GameObject root)
        {
            if (gameObject == null || root == null)
            {
                return false;
            }

            var current = gameObject.transform;
            while (current != null)
            {
                if (current == root.transform)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        static GameObject GetGameObject(Object obj)
        {
            if (obj is GameObject gameObject)
            {
                return gameObject;
            }

            if (obj is Component component)
            {
                return component.gameObject;
            }

            return null;
        }

        static string GetPrefabAssetPath(GameObject instanceRoot)
        {
            return instanceRoot == null ? null : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instanceRoot);
        }

        static bool SafeIsDefaultOverride(PropertyModification modification)
        {
            try
            {
                return PrefabUtility.IsDefaultOverride(modification);
            }
            catch
            {
                return false;
            }
        }

        static List<T> SafeList<T>(Func<List<T>> read, List<string> warnings, string label)
        {
            try
            {
                return read() ?? new List<T>();
            }
            catch (Exception e)
            {
                warnings.Add($"Failed to read {label}: {e.Message}");
                return new List<T>();
            }
        }

        static bool SameObject(Object a, Object b)
        {
            return a != null && b != null && a == b;
        }

        static object BuildObjectRef(Object obj, GameObject root)
        {
            if (obj == null)
            {
                return null;
            }

            var go = GetGameObject(obj);
            return new
            {
                name = obj.name,
                type = obj.GetType().FullName,
                instanceId = UnityApiAdapter.GetObjectId(obj),
                gameObjectName = go == null ? null : go.name,
                gameObjectPath = go == null || root == null ? null : GetPath(root.transform, go.transform),
                assetPath = AssetDatabase.GetAssetPath(obj)
            };
        }

        static string DisplayPropertyPath(string propertyPath)
        {
            if (string.IsNullOrWhiteSpace(propertyPath))
            {
                return propertyPath;
            }

            if (propertyPath.StartsWith("m_", StringComparison.Ordinal) && propertyPath.Length > 2)
            {
                var trimmed = propertyPath.Substring(2);
                return char.ToLowerInvariant(trimmed[0]) + trimmed.Substring(1);
            }

            return propertyPath;
        }

        static string BuildPropertyDescription(string path, string componentType, string propertyPath, string value, bool isDefaultOverride)
        {
            var target = DescribeTarget(path, componentType ?? typeof(GameObject).FullName);
            var defaultSuffix = isDefaultOverride ? " (default override)" : string.Empty;
            return string.IsNullOrWhiteSpace(value)
                ? $"Property override {DisplayPropertyPath(propertyPath)} on {target}{defaultSuffix}."
                : $"Property override {DisplayPropertyPath(propertyPath)} on {target} = {value}{defaultSuffix}.";
        }

        static string DescribeTarget(string path, string type)
        {
            return string.IsNullOrWhiteSpace(type) ? $"'{path}'" : $"'{path}' [{type}]";
        }

        static bool MatchesKind(string entryKind, string requestedKind)
        {
            var requested = NormalizeKind(requestedKind);
            return string.Equals(entryKind, requested, StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizeKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                return kind;
            }

            var normalized = kind.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            switch (normalized)
            {
                case "property":
                case "property_override":
                case "modification":
                    return OverrideKinds.Property;
                case "object":
                case "object_override":
                    return OverrideKinds.Object;
                case "added_component":
                case "component_added":
                    return OverrideKinds.AddedComponent;
                case "removed_component":
                case "component_removed":
                    return OverrideKinds.RemovedComponent;
                case "added_gameobject":
                case "added_game_object":
                case "gameobject_added":
                case "game_object_added":
                    return OverrideKinds.AddedGameObject;
                case "removed_gameobject":
                case "removed_game_object":
                case "gameobject_removed":
                case "game_object_removed":
                    return OverrideKinds.RemovedGameObject;
                default:
                    return kind;
            }
        }

        static bool MatchesType(string actualType, string requestedType)
        {
            if (string.IsNullOrWhiteSpace(actualType) || string.IsNullOrWhiteSpace(requestedType))
            {
                return false;
            }

            return string.Equals(actualType, requestedType, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(actualType.Split('.').Last(), requestedType, StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "/")
            {
                return "/";
            }

            return path.Trim().Trim('/').Replace('\\', '/');
        }

        static List<OverrideEntry> Limit(List<OverrideEntry> entries, int maxItems, out bool truncated)
        {
            maxItems = maxItems <= 0 ? DefaultMaxItems : maxItems;
            truncated = entries.Count > maxItems;
            return truncated ? entries.Take(maxItems).ToList() : entries;
        }

        static object BuildSelectionEcho(OverrideSelection selection)
        {
            return new
            {
                selection.OverrideId,
                selection.OverrideIds,
                selection.OverrideKind,
                selection.ObjectPath,
                selection.ComponentType,
                selection.PropertyPath,
                selection.DryRun
            };
        }

        static string BuildId(string kind, params string[] parts)
        {
            var normalized = string.Join("|", new[] { kind }.Concat(parts.Select(part => part ?? string.Empty)));
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            var shortHash = BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
            return $"{kind}:{shortHash}";
        }

        static string ToOptionalString(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        static class OverrideKinds
        {
            public const string Property = "property";
            public const string Object = "object";
            public const string AddedComponent = "added_component";
            public const string RemovedComponent = "removed_component";
            public const string AddedGameObject = "added_game_object";
            public const string RemovedGameObject = "removed_game_object";
        }

        sealed class CollectedOverrides
        {
            public readonly List<OverrideEntry> PublicEntries = new List<OverrideEntry>();
            public readonly List<OverrideCandidate> Candidates = new List<OverrideCandidate>();
            public readonly List<string> Warnings = new List<string>();
            public int DefaultOverrideCount;
            public int IgnoredOverrideCount;
        }

        sealed class OverrideCandidate
        {
            public OverrideEntry Entry;
            public Object PropertyObject;
            public string PropertyPath;
            public ObjectOverride ObjectOverride;
            public AddedComponent AddedComponent;
            public RemovedComponent RemovedComponent;
            public AddedGameObject AddedGameObject;
            public RemovedGameObject RemovedGameObject;
        }

        public sealed class OverrideEntry
        {
            public string id { get; set; }
            public string kind { get; set; }
            public string path { get; set; }
            public string objectName { get; set; }
            public long objectInstanceId { get; set; }
            public string objectType { get; set; }
            public string componentType { get; set; }
            public string propertyPath { get; set; }
            public string displayPropertyPath { get; set; }
            public string value { get; set; }
            public object objectReference { get; set; }
            public object component { get; set; }
            public object gameObject { get; set; }
            public bool isDefaultOverride { get; set; }
            public bool canApply { get; set; }
            public bool canRevert { get; set; }
            public string description { get; set; }
        }
    }
}
