#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class WorkSession
    {
        static SessionSemanticBaseline CaptureSemanticBaseline(ScanOptions options, string sessionId, out List<string> warnings)
        {
            warnings = new List<string>();
            if (options?.IncludeSceneSemantics != true)
            {
                return new SessionSemanticBaseline
                {
                    Enabled = false,
                    CapturedUtc = DateTime.UtcNow.ToString("o"),
                    Warnings = new List<string> { "Scene semantic baseline capture was disabled for this session." }
                };
            }

            try
            {
                var snapshot = CaptureSceneSemantics(options);
                var path = GetSemanticBaselineFile(sessionId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(snapshot, Formatting.None));

                warnings.AddRange(snapshot.Warnings ?? new List<string>());
                return new SessionSemanticBaseline
                {
                    Enabled = true,
                    CapturedUtc = snapshot.CapturedUtc,
                    SceneCount = snapshot.Scenes?.Count ?? 0,
                    ObjectCount = snapshot.TotalObjects,
                    Truncated = snapshot.Truncated,
                    SnapshotPath = ToSessionRelativePath(sessionId, path),
                    Warnings = snapshot.Warnings ?? new List<string>()
                };
            }
            catch (Exception ex)
            {
                var message = $"Scene semantic baseline capture failed: {ex.Message}";
                warnings.Add(message);
                return new SessionSemanticBaseline
                {
                    Enabled = false,
                    CapturedUtc = DateTime.UtcNow.ToString("o"),
                    Warnings = new List<string> { message }
                };
            }
        }

        static object BuildSemanticReview(SessionState state, bool include, int maxChanges)
        {
            if (!include)
            {
                return new { enabled = false, reason = "disabled_by_request" };
            }

            if (state?.SemanticBaseline == null)
            {
                return new { enabled = false, reason = "no_semantic_baseline", message = "This session was created before scene semantic baselines were available, or Begin disabled IncludeSceneSemantics." };
            }

            if (!state.SemanticBaseline.Enabled)
            {
                return new
                {
                    enabled = false,
                    reason = "semantic_baseline_disabled",
                    warnings = state.SemanticBaseline.Warnings?.ToArray() ?? Array.Empty<string>()
                };
            }

            var baselinePath = GetAbsoluteSemanticBaselinePath(state);
            if (!File.Exists(baselinePath))
            {
                return new
                {
                    enabled = false,
                    reason = "semantic_baseline_file_missing",
                    path = state.SemanticBaseline.SnapshotPath,
                    message = "The semantic baseline file is missing from the WorkSession storage folder."
                };
            }

            try
            {
                var baseline = JsonConvert.DeserializeObject<SceneSemanticCollection>(File.ReadAllText(baselinePath));
                var current = CaptureSceneSemantics(state.Options ?? new ScanOptions());
                var comparison = CompareSceneSemantics(baseline, current, Math.Max(1, maxChanges));
                var warnings = new List<string>();
                warnings.AddRange(state.SemanticBaseline.Warnings ?? new List<string>());
                warnings.AddRange(baseline?.Warnings ?? new List<string>());
                warnings.AddRange(current.Warnings ?? new List<string>());
                warnings.AddRange(comparison.Warnings ?? new List<string>());

                return new
                {
                    enabled = true,
                    comparedBy = "loaded scene objectId, with hierarchy path/indexedPath for explanation",
                    baseline = new
                    {
                        capturedUtc = state.SemanticBaseline.CapturedUtc,
                        sceneCount = state.SemanticBaseline.SceneCount,
                        objectCount = state.SemanticBaseline.ObjectCount,
                        truncated = state.SemanticBaseline.Truncated,
                        snapshotPath = state.SemanticBaseline.SnapshotPath
                    },
                    current = new
                    {
                        capturedUtc = current.CapturedUtc,
                        sceneCount = current.Scenes?.Count ?? 0,
                        objectCount = current.TotalObjects,
                        truncated = current.Truncated
                    },
                    summary = ToSemanticSummaryDto(comparison.Summary),
                    scenes = comparison.Scenes.OfType<SemanticSceneReview>().Select(ToSemanticSceneReviewDto).ToArray(),
                    changes = comparison.Changes.Select(ToSemanticChangeDto).ToArray(),
                    truncated = comparison.Truncated,
                    warnings = warnings.Distinct().ToArray(),
                    nextSuggestedCalls = comparison.Summary.TotalChanges > 0
                        ? new[] { $"UniBridge_WorkSession Action=Review SessionId={state.SessionId} MaxSemanticChanges={Math.Max(DefaultMaxSemanticChanges, maxChanges * 2)}" }
                        : Array.Empty<string>()
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    enabled = false,
                    reason = "semantic_review_failed",
                    error = ex.Message
                };
            }
        }

        static SceneSemanticCollection CaptureSceneSemantics(ScanOptions options)
        {
            var maxObjects = Math.Max(0, options?.MaxSemanticObjects ?? DefaultMaxSemanticObjects);
            var collection = new SceneSemanticCollection
            {
                Version = 1,
                CapturedUtc = DateTime.UtcNow.ToString("o"),
                ProjectRoot = NormalizeSlashes(ProjectRoot),
                UnityVersion = Application.unityVersion,
                MaxObjects = maxObjects
            };

            var traversalIndex = 0;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                var sceneSnapshot = new SemanticSceneSnapshot
                {
                    SceneName = scene.name,
                    ScenePath = scene.path,
                    BuildIndex = scene.buildIndex,
                    IsDirty = scene.isDirty,
                    RootCount = scene.rootCount
                };

                foreach (var root in scene.GetRootGameObjects().OrderBy(go => go.transform.GetSiblingIndex()))
                {
                    TraverseSemanticObject(root, null, 0, collection, sceneSnapshot, ref traversalIndex, maxObjects);
                    if (collection.Truncated)
                    {
                        break;
                    }
                }

                collection.Scenes.Add(sceneSnapshot);
                if (collection.Truncated)
                {
                    break;
                }
            }

            collection.TotalObjects = collection.Scenes.Sum(scene => scene.Objects?.Count ?? 0);
            if (collection.Truncated)
            {
                collection.Warnings.Add($"Scene semantic capture reached MaxSemanticObjects={maxObjects}; semantic review may be partial.");
            }

            return collection;
        }

        static void TraverseSemanticObject(
            GameObject gameObject,
            GameObject parent,
            int depth,
            SceneSemanticCollection collection,
            SemanticSceneSnapshot scene,
            ref int traversalIndex,
            int maxObjects)
        {
            if (gameObject == null || collection.Truncated)
            {
                return;
            }

            if (gameObject.hideFlags.HasFlag(HideFlags.HideAndDontSave))
            {
                return;
            }

            if (maxObjects > 0 && collection.TotalObjects >= maxObjects)
            {
                collection.Truncated = true;
                return;
            }

            scene.Objects.Add(BuildSemanticObject(gameObject, parent, depth, traversalIndex++));
            collection.TotalObjects++;

            var transform = gameObject.transform;
            for (var i = 0; i < transform.childCount; i++)
            {
                TraverseSemanticObject(transform.GetChild(i).gameObject, gameObject, depth + 1, collection, scene, ref traversalIndex, maxObjects);
                if (collection.Truncated)
                {
                    return;
                }
            }
        }

        static SemanticObjectSnapshot BuildSemanticObject(GameObject gameObject, GameObject parent, int depth, int traversalIndex)
        {
            var transform = gameObject.transform;
            var parentTransform = transform.parent;
            var parentGameObject = parentTransform != null ? parentTransform.gameObject : parent;
            var components = gameObject.GetComponents<Component>();

            return new SemanticObjectSnapshot
            {
                TraversalIndex = traversalIndex,
                Depth = depth,
                ObjectId = UnityApiAdapter.GetObjectId(gameObject),
                Name = gameObject.name,
                Path = "/" + SceneObjectLocator.GetHierarchyPath(gameObject),
                IndexedPath = BuildSemanticIndexedPath(gameObject),
                ParentObjectId = parentGameObject != null ? UnityApiAdapter.GetObjectId(parentGameObject) : (long?)null,
                ParentPath = parentGameObject != null ? "/" + SceneObjectLocator.GetHierarchyPath(parentGameObject) : null,
                SceneName = gameObject.scene.name,
                ScenePath = gameObject.scene.path,
                SiblingIndex = transform.GetSiblingIndex(),
                ChildCount = transform.childCount,
                ActiveSelf = gameObject.activeSelf,
                ActiveInHierarchy = gameObject.activeInHierarchy,
                Tag = SafeTag(gameObject),
                Layer = gameObject.layer,
                LayerName = LayerMask.LayerToName(gameObject.layer),
                ComponentTypes = components.Select(component => component == null ? "<missing>" : component.GetType().FullName ?? component.GetType().Name).ToList(),
                MissingScripts = components.Count(component => component == null),
                Renderers = gameObject.GetComponents<Renderer>().Where(renderer => renderer != null).Select(BuildRendererSnapshot).ToList(),
                Prefab = BuildPrefabSnapshot(gameObject),
                LocalTransform = BuildLocalTransformSignature(transform),
                WorldTransform = BuildWorldTransformSignature(transform)
            };
        }

        static SemanticRendererSnapshot BuildRendererSnapshot(Renderer renderer)
        {
            var material = renderer.sharedMaterial;
            var materialPath = material != null ? AssetDatabase.GetAssetPath(material) : null;
            return new SemanticRendererSnapshot
            {
                ObjectId = UnityApiAdapter.GetObjectId(renderer),
                RendererType = renderer.GetType().FullName ?? renderer.GetType().Name,
                TypeName = renderer.GetType().Name,
                Enabled = renderer.enabled,
                SortingLayerName = renderer.sortingLayerName,
                SortingLayerId = renderer.sortingLayerID,
                SortingOrder = renderer.sortingOrder,
                MaterialName = material != null ? material.name : null,
                MaterialPath = string.IsNullOrWhiteSpace(materialPath) ? null : materialPath
            };
        }

        static SemanticPrefabSnapshot BuildPrefabSnapshot(GameObject gameObject)
        {
            try
            {
                var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
                var nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
                return new SemanticPrefabSnapshot
                {
                    IsPartOfPrefabInstance = PrefabUtility.IsPartOfPrefabInstance(gameObject),
                    IsAnyPrefabInstanceRoot = PrefabUtility.IsAnyPrefabInstanceRoot(gameObject),
                    SourcePath = string.IsNullOrWhiteSpace(sourcePath) ? null : sourcePath,
                    SourceGuid = string.IsNullOrWhiteSpace(sourcePath) ? null : AssetDatabase.AssetPathToGUID(sourcePath),
                    InstanceStatus = PrefabUtility.GetPrefabInstanceStatus(gameObject).ToString(),
                    NearestRootObjectId = nearestRoot != null ? UnityApiAdapter.GetObjectId(nearestRoot) : (long?)null,
                    NearestRootPath = nearestRoot != null ? "/" + SceneObjectLocator.GetHierarchyPath(nearestRoot) : null
                };
            }
            catch (Exception ex)
            {
                return new SemanticPrefabSnapshot { Error = ex.Message };
            }
        }

        static SemanticComparison CompareSceneSemantics(SceneSemanticCollection before, SceneSemanticCollection after, int maxChanges)
        {
            before ??= new SceneSemanticCollection();
            after ??= new SceneSemanticCollection();

            var beforeObjects = FlattenSemanticObjects(before).GroupBy(obj => obj.ObjectId).ToDictionary(group => group.Key, group => group.First());
            var afterObjects = FlattenSemanticObjects(after).GroupBy(obj => obj.ObjectId).ToDictionary(group => group.Key, group => group.First());
            var allIds = new HashSet<long>(beforeObjects.Keys);
            allIds.UnionWith(afterObjects.Keys);

            var comparison = new SemanticComparison();
            var allChanges = new List<SemanticChange>();
            var sceneCounts = BuildSceneCounts(before, after);

            foreach (var id in allIds.OrderBy(value => value))
            {
                beforeObjects.TryGetValue(id, out var oldObj);
                afterObjects.TryGetValue(id, out var newObj);

                if (oldObj == null && newObj != null)
                {
                    comparison.Summary.ObjectsCreated++;
                    AddSemanticChange(comparison, sceneCounts, allChanges, "Created", null, newObj, new { after = ToSemanticObjectSummary(newObj) });
                    continue;
                }

                if (oldObj != null && newObj == null)
                {
                    comparison.Summary.ObjectsDeleted++;
                    AddSemanticChange(comparison, sceneCounts, allChanges, "Deleted", oldObj, null, new { before = ToSemanticObjectSummary(oldObj) });
                    continue;
                }

                CompareExistingSemanticObject(comparison, sceneCounts, allChanges, oldObj, newObj);
            }

            comparison.Summary.ScenesTracked = sceneCounts.Count;
            comparison.Summary.BaselineObjects = before.TotalObjects;
            comparison.Summary.CurrentObjects = after.TotalObjects;
            comparison.Summary.TotalChanges = allChanges.Count;
            comparison.Truncated = allChanges.Count > maxChanges;
            comparison.Changes = allChanges.Take(maxChanges).ToList();
            comparison.Scenes = sceneCounts.Values
                .Where(scene => scene.TotalChanges > 0 || scene.BaselineObjects != scene.CurrentObjects)
                .OrderBy(scene => scene.ScenePath ?? scene.SceneName)
                .Cast<object>()
                .ToArray();

            if (before.TotalObjects > 0 &&
                after.TotalObjects > 0 &&
                comparison.Summary.CommonObjects == 0 &&
                comparison.Summary.ObjectsCreated > 0 &&
                comparison.Summary.ObjectsDeleted > 0)
            {
                comparison.Warnings.Add("No common scene object ids were found between baseline and current semantic snapshots. The scene may have been reloaded or object ids changed, so semantic diff may be noisy.");
            }

            return comparison;
        }

        static void CompareExistingSemanticObject(
            SemanticComparison comparison,
            Dictionary<string, SemanticSceneReview> sceneCounts,
            List<SemanticChange> allChanges,
            SemanticObjectSnapshot oldObj,
            SemanticObjectSnapshot newObj)
        {
            comparison.Summary.CommonObjects++;

            if (!string.Equals(oldObj.Name, newObj.Name, StringComparison.Ordinal))
            {
                comparison.Summary.ObjectsRenamed++;
                AddSemanticChange(comparison, sceneCounts, allChanges, "Renamed", oldObj, newObj, new { beforeName = oldObj.Name, afterName = newObj.Name });
            }

            if (oldObj.ParentObjectId != newObj.ParentObjectId || oldObj.SiblingIndex != newObj.SiblingIndex)
            {
                comparison.Summary.ObjectsMoved++;
                AddSemanticChange(comparison, sceneCounts, allChanges, "Moved", oldObj, newObj, new
                {
                    beforeParentObjectId = oldObj.ParentObjectId,
                    afterParentObjectId = newObj.ParentObjectId,
                    beforeParentPath = oldObj.ParentPath,
                    afterParentPath = newObj.ParentPath,
                    beforeSiblingIndex = oldObj.SiblingIndex,
                    afterSiblingIndex = newObj.SiblingIndex
                });
            }

            if (oldObj.ActiveSelf != newObj.ActiveSelf ||
                oldObj.ActiveInHierarchy != newObj.ActiveInHierarchy ||
                !string.Equals(oldObj.Tag, newObj.Tag, StringComparison.Ordinal) ||
                oldObj.Layer != newObj.Layer)
            {
                comparison.Summary.PropertiesChanged++;
                AddSemanticChange(comparison, sceneCounts, allChanges, "PropertiesChanged", oldObj, newObj, new
                {
                    before = new { activeSelf = oldObj.ActiveSelf, activeInHierarchy = oldObj.ActiveInHierarchy, tag = oldObj.Tag, layer = oldObj.Layer, layerName = oldObj.LayerName },
                    after = new { activeSelf = newObj.ActiveSelf, activeInHierarchy = newObj.ActiveInHierarchy, tag = newObj.Tag, layer = newObj.Layer, layerName = newObj.LayerName }
                });
            }

            if (!StringListEquals(oldObj.ComponentTypes, newObj.ComponentTypes))
            {
                comparison.Summary.ComponentsChanged++;
                AddSemanticChange(comparison, sceneCounts, allChanges, "ComponentsChanged", oldObj, newObj, new
                {
                    added = (newObj.ComponentTypes ?? new List<string>()).Except(oldObj.ComponentTypes ?? new List<string>()).Take(12).ToArray(),
                    removed = (oldObj.ComponentTypes ?? new List<string>()).Except(newObj.ComponentTypes ?? new List<string>()).Take(12).ToArray(),
                    beforeCount = oldObj.ComponentTypes?.Count ?? 0,
                    afterCount = newObj.ComponentTypes?.Count ?? 0
                });
            }

            if (oldObj.MissingScripts != newObj.MissingScripts)
            {
                if (newObj.MissingScripts > oldObj.MissingScripts)
                {
                    comparison.Summary.MissingScriptsIntroduced += newObj.MissingScripts - oldObj.MissingScripts;
                }
                else
                {
                    comparison.Summary.MissingScriptsResolved += oldObj.MissingScripts - newObj.MissingScripts;
                }

                AddSemanticChange(comparison, sceneCounts, allChanges, "MissingScriptsChanged", oldObj, newObj, new
                {
                    beforeMissingScripts = oldObj.MissingScripts,
                    afterMissingScripts = newObj.MissingScripts
                });
            }

            if (!string.Equals(RendererSortingSignature(oldObj), RendererSortingSignature(newObj), StringComparison.Ordinal))
            {
                comparison.Summary.RendererSortingChanged++;
                AddSemanticChange(comparison, sceneCounts, allChanges, "RendererSortingChanged", oldObj, newObj, new
                {
                    before = RendererSummary(oldObj),
                    after = RendererSummary(newObj)
                });
            }
            else if (!string.Equals(RendererFullSignature(oldObj), RendererFullSignature(newObj), StringComparison.Ordinal))
            {
                comparison.Summary.RenderersChanged++;
                AddSemanticChange(comparison, sceneCounts, allChanges, "RenderersChanged", oldObj, newObj, new
                {
                    before = RendererSummary(oldObj),
                    after = RendererSummary(newObj)
                });
            }

            if (!string.Equals(PrefabSignature(oldObj), PrefabSignature(newObj), StringComparison.Ordinal))
            {
                comparison.Summary.PrefabInfoChanged++;
                AddSemanticChange(comparison, sceneCounts, allChanges, "PrefabInfoChanged", oldObj, newObj, new
                {
                    before = PrefabSummary(oldObj),
                    after = PrefabSummary(newObj)
                });
            }

            if (!string.Equals(oldObj.LocalTransform, newObj.LocalTransform, StringComparison.Ordinal) ||
                !string.Equals(oldObj.WorldTransform, newObj.WorldTransform, StringComparison.Ordinal))
            {
                comparison.Summary.TransformsChanged++;
                AddSemanticChange(comparison, sceneCounts, allChanges, "TransformChanged", oldObj, newObj, new
                {
                    beforeLocal = oldObj.LocalTransform,
                    afterLocal = newObj.LocalTransform,
                    beforeWorld = oldObj.WorldTransform,
                    afterWorld = newObj.WorldTransform
                });
            }
        }

        static void AddSemanticChange(
            SemanticComparison comparison,
            Dictionary<string, SemanticSceneReview> sceneCounts,
            List<SemanticChange> allChanges,
            string type,
            SemanticObjectSnapshot before,
            SemanticObjectSnapshot after,
            object details)
        {
            var obj = after ?? before;
            var scene = GetSceneReview(sceneCounts, obj);
            scene.TotalChanges++;
            if (!scene.ByType.ContainsKey(type))
            {
                scene.ByType[type] = 0;
            }
            scene.ByType[type]++;

            allChanges.Add(new SemanticChange
            {
                ChangeType = type,
                ObjectId = obj?.ObjectId ?? 0,
                Name = obj?.Name,
                SceneName = obj?.SceneName,
                ScenePath = obj?.ScenePath,
                BeforePath = before?.Path,
                AfterPath = after?.Path,
                BeforeIndexedPath = before?.IndexedPath,
                AfterIndexedPath = after?.IndexedPath,
                Details = details
            });
        }

        static Dictionary<string, SemanticSceneReview> BuildSceneCounts(SceneSemanticCollection before, SceneSemanticCollection after)
        {
            var result = new Dictionary<string, SemanticSceneReview>(StringComparer.OrdinalIgnoreCase);
            foreach (var scene in before.Scenes ?? new List<SemanticSceneSnapshot>())
            {
                var review = GetSceneReview(result, scene);
                review.BaselineObjects = scene.Objects?.Count ?? 0;
            }

            foreach (var scene in after.Scenes ?? new List<SemanticSceneSnapshot>())
            {
                var review = GetSceneReview(result, scene);
                review.CurrentObjects = scene.Objects?.Count ?? 0;
            }

            return result;
        }

        static SemanticSceneReview GetSceneReview(Dictionary<string, SemanticSceneReview> scenes, SemanticSceneSnapshot scene)
        {
            var key = SceneKey(scene?.ScenePath, scene?.SceneName);
            if (!scenes.TryGetValue(key, out var review))
            {
                review = new SemanticSceneReview
                {
                    SceneName = scene?.SceneName,
                    ScenePath = scene?.ScenePath,
                    ByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                };
                scenes[key] = review;
            }

            return review;
        }

        static SemanticSceneReview GetSceneReview(Dictionary<string, SemanticSceneReview> scenes, SemanticObjectSnapshot obj)
        {
            var key = SceneKey(obj?.ScenePath, obj?.SceneName);
            if (!scenes.TryGetValue(key, out var review))
            {
                review = new SemanticSceneReview
                {
                    SceneName = obj?.SceneName,
                    ScenePath = obj?.ScenePath,
                    ByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                };
                scenes[key] = review;
            }

            return review;
        }

        static IEnumerable<SemanticObjectSnapshot> FlattenSemanticObjects(SceneSemanticCollection collection)
        {
            return (collection?.Scenes ?? new List<SemanticSceneSnapshot>())
                .SelectMany(scene => scene.Objects ?? new List<SemanticObjectSnapshot>())
                .Where(obj => obj != null && obj.ObjectId != 0);
        }

        static object ToSemanticObjectSummary(SemanticObjectSnapshot obj)
        {
            if (obj == null)
            {
                return null;
            }

            return new
            {
                objectId = obj.ObjectId,
                name = obj.Name,
                path = obj.Path,
                indexedPath = obj.IndexedPath,
                sceneName = obj.SceneName,
                scenePath = obj.ScenePath,
                parentObjectId = obj.ParentObjectId,
                parentPath = obj.ParentPath,
                siblingIndex = obj.SiblingIndex,
                activeSelf = obj.ActiveSelf,
                layer = obj.Layer,
                layerName = obj.LayerName,
                componentCount = obj.ComponentTypes?.Count ?? 0,
                missingScripts = obj.MissingScripts,
                rendererCount = obj.Renderers?.Count ?? 0,
                prefabSourcePath = obj.Prefab?.SourcePath
            };
        }

        static object ToSemanticSummaryDto(SemanticSummary summary)
        {
            return new
            {
                scenesTracked = summary.ScenesTracked,
                baselineObjects = summary.BaselineObjects,
                currentObjects = summary.CurrentObjects,
                commonObjects = summary.CommonObjects,
                totalChanges = summary.TotalChanges,
                objectsCreated = summary.ObjectsCreated,
                objectsDeleted = summary.ObjectsDeleted,
                objectsMoved = summary.ObjectsMoved,
                objectsRenamed = summary.ObjectsRenamed,
                propertiesChanged = summary.PropertiesChanged,
                componentsChanged = summary.ComponentsChanged,
                rendererSortingChanged = summary.RendererSortingChanged,
                renderersChanged = summary.RenderersChanged,
                prefabInfoChanged = summary.PrefabInfoChanged,
                transformsChanged = summary.TransformsChanged,
                missingScriptsIntroduced = summary.MissingScriptsIntroduced,
                missingScriptsResolved = summary.MissingScriptsResolved
            };
        }

        static object ToSemanticSceneReviewDto(SemanticSceneReview scene)
        {
            return new
            {
                sceneName = scene.SceneName,
                scenePath = scene.ScenePath,
                baselineObjects = scene.BaselineObjects,
                currentObjects = scene.CurrentObjects,
                totalChanges = scene.TotalChanges,
                byType = scene.ByType
            };
        }

        static object ToSemanticChangeDto(SemanticChange change)
        {
            return new
            {
                changeType = change.ChangeType,
                objectId = change.ObjectId,
                name = change.Name,
                sceneName = change.SceneName,
                scenePath = change.ScenePath,
                beforePath = change.BeforePath,
                afterPath = change.AfterPath,
                beforeIndexedPath = change.BeforeIndexedPath,
                afterIndexedPath = change.AfterIndexedPath,
                details = change.Details
            };
        }

        static object[] RendererSummary(SemanticObjectSnapshot obj)
        {
            return (obj?.Renderers ?? new List<SemanticRendererSnapshot>())
                .Select(renderer => new
                {
                    rendererType = renderer.TypeName ?? renderer.RendererType,
                    enabled = renderer.Enabled,
                    sortingLayerName = renderer.SortingLayerName,
                    sortingLayerId = renderer.SortingLayerId,
                    sortingOrder = renderer.SortingOrder,
                    materialName = renderer.MaterialName,
                    materialPath = renderer.MaterialPath
                })
                .Cast<object>()
                .ToArray();
        }

        static object PrefabSummary(SemanticObjectSnapshot obj)
        {
            var prefab = obj?.Prefab;
            if (prefab == null)
            {
                return null;
            }

            return new
            {
                sourcePath = prefab.SourcePath,
                sourceGuid = prefab.SourceGuid,
                instanceStatus = prefab.InstanceStatus,
                isPartOfPrefabInstance = prefab.IsPartOfPrefabInstance,
                isAnyPrefabInstanceRoot = prefab.IsAnyPrefabInstanceRoot,
                nearestRootObjectId = prefab.NearestRootObjectId,
                nearestRootPath = prefab.NearestRootPath,
                error = prefab.Error
            };
        }

        static string RendererSortingSignature(SemanticObjectSnapshot obj)
        {
            return string.Join("|", (obj?.Renderers ?? new List<SemanticRendererSnapshot>())
                .Select(renderer => string.Join(":", renderer.RendererType, renderer.SortingLayerName, renderer.SortingLayerId.ToString(CultureInfo.InvariantCulture), renderer.SortingOrder.ToString(CultureInfo.InvariantCulture))));
        }

        static string RendererFullSignature(SemanticObjectSnapshot obj)
        {
            return string.Join("|", (obj?.Renderers ?? new List<SemanticRendererSnapshot>())
                .Select(renderer => string.Join(":", renderer.RendererType, renderer.Enabled.ToString(), renderer.SortingLayerName, renderer.SortingLayerId.ToString(CultureInfo.InvariantCulture), renderer.SortingOrder.ToString(CultureInfo.InvariantCulture), renderer.MaterialPath, renderer.MaterialName)));
        }

        static string PrefabSignature(SemanticObjectSnapshot obj)
        {
            var prefab = obj?.Prefab;
            if (prefab == null)
            {
                return string.Empty;
            }

            return string.Join(":", prefab.SourcePath, prefab.SourceGuid, prefab.InstanceStatus, prefab.IsPartOfPrefabInstance.ToString(), prefab.IsAnyPrefabInstanceRoot.ToString(), prefab.NearestRootObjectId?.ToString(CultureInfo.InvariantCulture));
        }

        static bool StringListEquals(List<string> left, List<string> right)
        {
            left ??= new List<string>();
            right ??= new List<string>();
            return left.Count == right.Count && left.SequenceEqual(right, StringComparer.Ordinal);
        }

        static string BuildSemanticIndexedPath(GameObject gameObject)
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

        static string BuildLocalTransformSignature(Transform transform)
        {
            return string.Join("|",
                VectorSignature(transform.localPosition),
                VectorSignature(transform.localEulerAngles),
                VectorSignature(transform.localScale));
        }

        static string BuildWorldTransformSignature(Transform transform)
        {
            return string.Join("|",
                VectorSignature(transform.position),
                VectorSignature(transform.eulerAngles),
                VectorSignature(transform.lossyScale));
        }

        static string VectorSignature(Vector3 value)
        {
            return string.Join(",",
                value.x.ToString("0.####", CultureInfo.InvariantCulture),
                value.y.ToString("0.####", CultureInfo.InvariantCulture),
                value.z.ToString("0.####", CultureInfo.InvariantCulture));
        }

        static string SafeTag(GameObject gameObject)
        {
            try { return gameObject != null ? gameObject.tag : "Untagged"; }
            catch { return "Untagged"; }
        }

        static string SceneKey(string scenePath, string sceneName)
        {
            return !string.IsNullOrWhiteSpace(scenePath) ? scenePath : sceneName ?? "(untitled)";
        }

        static string GetSemanticBaselineFile(string sessionId)
        {
            return Path.Combine(GetSessionDir(sessionId), "semantic", "loaded-scenes-baseline.json");
        }

        static string GetAbsoluteSemanticBaselinePath(SessionState state)
        {
            if (string.IsNullOrWhiteSpace(state.SemanticBaseline?.SnapshotPath))
            {
                return GetSemanticBaselineFile(state.SessionId);
            }

            return Path.Combine(GetSessionDir(state.SessionId), state.SemanticBaseline.SnapshotPath.Replace('/', Path.DirectorySeparatorChar));
        }

        sealed class SessionSemanticBaseline
        {
            public bool Enabled;
            public string CapturedUtc;
            public int SceneCount;
            public int ObjectCount;
            public bool Truncated;
            public string SnapshotPath;
            public List<string> Warnings = new();
        }

        sealed class SceneSemanticCollection
        {
            public int Version;
            public string CapturedUtc;
            public string ProjectRoot;
            public string UnityVersion;
            public int MaxObjects;
            public int TotalObjects;
            public bool Truncated;
            public List<string> Warnings = new();
            public List<SemanticSceneSnapshot> Scenes = new();
        }

        sealed class SemanticSceneSnapshot
        {
            public string SceneName;
            public string ScenePath;
            public int BuildIndex;
            public bool IsDirty;
            public int RootCount;
            public List<SemanticObjectSnapshot> Objects = new();
        }

        sealed class SemanticObjectSnapshot
        {
            public int TraversalIndex;
            public int Depth;
            public long ObjectId;
            public string Name;
            public string Path;
            public string IndexedPath;
            public long? ParentObjectId;
            public string ParentPath;
            public string SceneName;
            public string ScenePath;
            public int SiblingIndex;
            public int ChildCount;
            public bool ActiveSelf;
            public bool ActiveInHierarchy;
            public string Tag;
            public int Layer;
            public string LayerName;
            public List<string> ComponentTypes = new();
            public int MissingScripts;
            public List<SemanticRendererSnapshot> Renderers = new();
            public SemanticPrefabSnapshot Prefab;
            public string LocalTransform;
            public string WorldTransform;
        }

        sealed class SemanticRendererSnapshot
        {
            public long ObjectId;
            public string RendererType;
            public string TypeName;
            public bool Enabled;
            public string SortingLayerName;
            public int SortingLayerId;
            public int SortingOrder;
            public string MaterialName;
            public string MaterialPath;
        }

        sealed class SemanticPrefabSnapshot
        {
            public bool IsPartOfPrefabInstance;
            public bool IsAnyPrefabInstanceRoot;
            public string SourcePath;
            public string SourceGuid;
            public string InstanceStatus;
            public long? NearestRootObjectId;
            public string NearestRootPath;
            public string Error;
        }

        sealed class SemanticComparison
        {
            public SemanticSummary Summary = new();
            public object[] Scenes = Array.Empty<object>();
            public List<SemanticChange> Changes = new();
            public bool Truncated;
            public List<string> Warnings = new();
        }

        sealed class SemanticSummary
        {
            public int ScenesTracked;
            public int BaselineObjects;
            public int CurrentObjects;
            public int CommonObjects;
            public int TotalChanges;
            public int ObjectsCreated;
            public int ObjectsDeleted;
            public int ObjectsMoved;
            public int ObjectsRenamed;
            public int PropertiesChanged;
            public int ComponentsChanged;
            public int RendererSortingChanged;
            public int RenderersChanged;
            public int PrefabInfoChanged;
            public int TransformsChanged;
            public int MissingScriptsIntroduced;
            public int MissingScriptsResolved;
        }

        sealed class SemanticSceneReview
        {
            public string SceneName;
            public string ScenePath;
            public int BaselineObjects;
            public int CurrentObjects;
            public int TotalChanges;
            public Dictionary<string, int> ByType = new(StringComparer.OrdinalIgnoreCase);
        }

        sealed class SemanticChange
        {
            public string ChangeType;
            public long ObjectId;
            public string Name;
            public string SceneName;
            public string ScenePath;
            public string BeforePath;
            public string AfterPath;
            public string BeforeIndexedPath;
            public string AfterIndexedPath;
            public object Details;
        }
    }
}
