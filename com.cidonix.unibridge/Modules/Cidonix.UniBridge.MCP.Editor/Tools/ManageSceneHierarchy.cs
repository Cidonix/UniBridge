#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Performs safe, objectId-based hierarchy edits for large scenes.
    /// </summary>
    public static class ManageSceneHierarchy
    {
        const string ToolName = "UniBridge_ManageSceneHierarchy";
        const string UndoName = "UniBridge Scene Hierarchy Edit";
        const float PositionTolerance = 0.0005f;
        const float RotationTolerance = 0.05f;
        const float ScaleTolerance = 0.0005f;

        public const string Title = "Safely batch-edit scene hierarchy";

        public const string Description = @"Safely reparent, sort, and organize scene GameObjects by objectId.

Use this after UniBridge_SceneHierarchyExport when a large scene needs reliable hierarchy cleanup without losing objects or changing world transforms.

Args:
    Action: Reparent or CreateContainer.
    ScenePath: Optional scene path/name used for validation counts and root placement.
    Moves: For Reparent, objectId/target, parentObjectId/parent, optional siblingIndex, optional worldPositionStays.
    ObjectIds/ObjectIdStrings, ContainerName, ParentObjectId/Parent: For CreateContainer, create an empty organizational parent and move objects into it. Prefer ObjectIdStrings in JavaScript clients because Unity 6 EntityIds can exceed JS safe integer precision.
    DryRun: Preview before changing the scene. Defaults true.
    WorldPositionStays: Preserve world transform. Defaults true.
    ValidateExpectedObjectCountDelta: Reparent validates count delta 0; CreateContainer validates count delta +1. ValidateObjectCountUnchanged is kept as a compatibility alias.

Returns:
    success, message, dry-run or execution diff, planned parent metadata, before/after parent/sibling data, world-transform preservation checks, Undo group, and object-count validation with mode/expectedDelta/actualDelta/passed.";

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "hierarchy", "editor" }, EnabledByDefault = true, ExecutionPolicy = ToolExecutionPolicy.Mutating)]
        public static object HandleCommand(ManageSceneHierarchyParams parameters)
        {
            parameters ??= new ManageSceneHierarchyParams();

            try
            {
                return parameters.Action switch
                {
                    ManageSceneHierarchyAction.Reparent => Reparent(parameters),
                    ManageSceneHierarchyAction.CreateContainer => CreateContainer(parameters),
                    _ => Response.Error($"Unsupported scene hierarchy action: {parameters.Action}")
                };
            }
            catch (Exception ex)
            {
                return Response.Error($"Scene hierarchy edit failed: {ex.Message}", new { exceptionType = ex.GetType().FullName });
            }
        }

        static object Reparent(ManageSceneHierarchyParams parameters)
        {
            if (parameters.Moves == null || parameters.Moves.Length == 0)
            {
                return Response.Error("Moves are required for Action=Reparent.");
            }

            var countBefore = CountSceneObjects(parameters.ScenePath);
            var plans = BuildMovePlans(parameters, parameters.Moves, null);
            if (plans.Any(plan => !string.IsNullOrWhiteSpace(plan.Error)))
            {
                return Response.Error("One or more hierarchy moves could not be resolved.", new
                {
                    dryRun = parameters.DryRun,
                    objectCountBefore = countBefore,
                    plans = plans.Select(plan => plan.ToResult(includeAfter: false)).ToArray()
                });
            }

            if (parameters.DryRun)
            {
                return Response.Success("Scene hierarchy reparent dry-run completed.", new
                {
                    dryRun = true,
                    expectedObjectCountDelta = 0,
                    objectCountBefore = countBefore,
                    expectedObjectCountAfter = countBefore,
                    objectCountValidation = BuildPlannedCountValidation(0),
                    willDeleteObjects = false,
                    moves = plans.Select(plan => plan.ToResult(includeAfter: false)).ToArray()
                });
            }

            return ExecutePlans(parameters, plans, expectedDelta: 0, countBefore);
        }

        static object CreateContainer(ManageSceneHierarchyParams parameters)
        {
            var objectIds = ResolveObjectIds(parameters.ObjectIds, parameters.ObjectIdStrings, out var objectIdErrors);
            if (objectIdErrors.Count > 0)
            {
                return Response.Error("One or more ObjectIdStrings could not be parsed.", new { errors = objectIdErrors });
            }

            if (objectIds.Length == 0)
            {
                return Response.Error("ObjectIds or ObjectIdStrings are required for Action=CreateContainer.");
            }

            var countBefore = CountSceneObjects(parameters.ScenePath);
            var parentObjectId = ResolveObjectId(parameters.ParentObjectId, parameters.ParentObjectIdString, out var parentIdError);
            if (!string.IsNullOrWhiteSpace(parentIdError))
            {
                return Response.Error(parentIdError);
            }

            var parent = ResolveGameObject(parentObjectId, parameters.Parent, parameters.ParentSearchMethod, parameters.ScenePath);
            if (ParentWasRequested(parentObjectId, parameters.ParentObjectIdString, parameters.Parent) && parent == null)
            {
                return Response.Error("Requested container parent was not found.", new
                {
                    parameters.ParentObjectId,
                    parameters.ParentObjectIdString,
                    parameters.Parent,
                    parameters.ParentSearchMethod,
                    parameters.ScenePath
                });
            }

            var targetScene = ResolveTargetScene(parameters.ScenePath, parent);
            var moveRequests = objectIds
                .Select(id => new SceneHierarchyMove
                {
                    ObjectId = id,
                    ParentObjectId = null,
                    Parent = null,
                    SiblingIndex = null,
                    WorldPositionStays = parameters.WorldPositionStays
                })
                .ToArray();

            var plans = BuildMovePlans(parameters, moveRequests, parentOverride: null);
            if (plans.Any(plan => !string.IsNullOrWhiteSpace(plan.Error)))
            {
                return Response.Error("One or more objects for the container move could not be resolved.", new
                {
                    dryRun = parameters.DryRun,
                    objectCountBefore = countBefore,
                    plans = plans.Select(plan => plan.ToResult(includeAfter: false)).ToArray()
                });
            }

            var containerName = string.IsNullOrWhiteSpace(parameters.ContainerName)
                ? "Scene_Group"
                : parameters.ContainerName.Trim();
            var plannedParentPath = BuildPlannedContainerPath(parent, containerName);
            var plannedParent = new PlannedParentInfo
            {
                ContainerName = containerName,
                PlannedPath = plannedParentPath,
                ObjectId = null,
                WillBeCreated = true
            };

            if (parameters.DryRun)
            {
                return Response.Success("Scene hierarchy container dry-run completed.", new
                {
                    dryRun = true,
                    expectedObjectCountDelta = 1,
                    objectCountBefore = countBefore,
                    expectedObjectCountAfter = countBefore + 1,
                    objectCountValidation = BuildPlannedCountValidation(1),
                    willCreateContainer = new
                    {
                        name = containerName,
                        parent = parent != null ? SerializeReference(parent) : null,
                        scene = targetScene.IsValid() ? SceneHierarchyExportUtility.SerializeScene(targetScene) : null,
                        siblingIndex = parameters.SiblingIndex
                    },
                    willDeleteObjects = false,
                    moves = plans.Select(plan => plan.ToResult(includeAfter: false, plannedParent)).ToArray()
                });
            }

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);

            GameObject container = null;
            try
            {
                container = new GameObject(containerName);
                Undo.RegisterCreatedObjectUndo(container, UndoName);

                if (targetScene.IsValid() && targetScene.isLoaded && container.scene != targetScene)
                {
                    SceneManager.MoveGameObjectToScene(container, targetScene);
                }

                if (parent != null)
                {
                    Undo.SetTransformParent(container.transform, parent.transform, UndoName);
                }

                if (parameters.SiblingIndex.HasValue && parameters.SiblingIndex.Value >= 0)
                {
                    container.transform.SetSiblingIndex(parameters.SiblingIndex.Value);
                }

                MarkSceneDirty(container.scene);

                foreach (var plan in plans)
                {
                    plan.Parent = container;
                    ApplyMove(plan);
                }

                var countAfter = CountSceneObjects(parameters.ScenePath);
                var validation = ValidateObjectCount(parameters, group, countBefore, countAfter, expectedDelta: 1);
                if (validation != null)
                {
                    return validation;
                }

                Undo.CollapseUndoOperations(group);
                return Response.Success("Created organizational container and moved objects.", new
                {
                    dryRun = false,
                    undoGroup = group,
                    createdContainer = SerializeReference(container),
                    expectedObjectCountDelta = 1,
                    objectCountBefore = countBefore,
                    objectCountAfter = countAfter,
                    objectCountValidation = BuildCountValidation(countBefore, countAfter, 1),
                    willDeleteObjects = false,
                    moves = plans.Select(plan => plan.ToResult(includeAfter: parameters.IncludeDiff)).ToArray()
                });
            }
            catch (Exception ex)
            {
                TryRevertUndoGroup(group);
                return Response.Error($"Failed to create scene hierarchy container: {ex.Message}", new { undoGroup = group });
            }
        }

        static object ExecutePlans(
            ManageSceneHierarchyParams parameters,
            IReadOnlyList<MovePlan> plans,
            int expectedDelta,
            int countBefore)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);

            try
            {
                foreach (var plan in plans)
                {
                    ApplyMove(plan);
                }

                var countAfter = CountSceneObjects(parameters.ScenePath);
                var validation = ValidateObjectCount(parameters, group, countBefore, countAfter, expectedDelta);
                if (validation != null)
                {
                    return validation;
                }

                Undo.CollapseUndoOperations(group);
                return Response.Success("Scene hierarchy reparent completed.", new
                {
                    dryRun = false,
                    undoGroup = group,
                    expectedObjectCountDelta = expectedDelta,
                    objectCountBefore = countBefore,
                    objectCountAfter = countAfter,
                    objectCountValidation = BuildCountValidation(countBefore, countAfter, expectedDelta),
                    willDeleteObjects = false,
                    moves = plans.Select(plan => plan.ToResult(includeAfter: parameters.IncludeDiff)).ToArray()
                });
            }
            catch (Exception ex)
            {
                TryRevertUndoGroup(group);
                return Response.Error($"Failed to apply scene hierarchy moves: {ex.Message}", new { undoGroup = group });
            }
        }

        static List<MovePlan> BuildMovePlans(
            ManageSceneHierarchyParams parameters,
            IEnumerable<SceneHierarchyMove> moves,
            GameObject parentOverride)
        {
            var plans = new List<MovePlan>();
            foreach (var move in moves)
            {
                var objectId = ResolveObjectId(move.ObjectId, move.ObjectIdString, out var objectIdError);
                var parentObjectId = ResolveObjectId(move.ParentObjectId, move.ParentObjectIdString, out var parentObjectIdError);
                var target = string.IsNullOrWhiteSpace(objectIdError)
                    ? ResolveGameObject(objectId, move.Target, move.SearchMethod, parameters.ScenePath)
                    : null;
                var parent = parentOverride ?? (string.IsNullOrWhiteSpace(parentObjectIdError)
                    ? ResolveGameObject(parentObjectId, move.Parent, move.ParentSearchMethod, parameters.ScenePath)
                    : null);
                var plan = new MovePlan
                {
                    Request = move,
                    Target = target,
                    Parent = parent,
                    WorldPositionStays = move.WorldPositionStays ?? parameters.WorldPositionStays,
                    SiblingIndex = move.SiblingIndex,
                    Before = target != null ? TransformSnapshot.Capture(target.transform) : null
                };

                if (!string.IsNullOrWhiteSpace(objectIdError))
                {
                    plan.Error = objectIdError;
                }
                else if (!string.IsNullOrWhiteSpace(parentObjectIdError))
                {
                    plan.Error = parentObjectIdError;
                }
                else if (target == null)
                {
                    plan.Error = $"Target was not found for objectId '{objectId}' / objectIdString '{move.ObjectIdString}' or target '{move.Target}'.";
                }
                else if (ParentWasRequested(parentObjectId, move.ParentObjectIdString, move.Parent) && parent == null)
                {
                    plan.Error = $"Parent was not found for parentObjectId '{parentObjectId}' / parentObjectIdString '{move.ParentObjectIdString}' or parent '{move.Parent}'.";
                }
                else if (parent != null && (parent == target || parent.transform.IsChildOf(target.transform)))
                {
                    plan.Error = $"Invalid reparent: '{parent.name}' is the target itself or a child of '{target.name}'.";
                }

                plans.Add(plan);
            }

            return plans;
        }

        static long? ResolveObjectId(long? numericId, string stringId, out string error)
        {
            error = null;
            if (numericId.HasValue && numericId.Value != 0)
            {
                return numericId;
            }

            if (string.IsNullOrWhiteSpace(stringId))
            {
                return null;
            }

            if (long.TryParse(stringId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed != 0)
            {
                return parsed;
            }

            error = $"ObjectIdString '{stringId}' is not a valid non-zero Int64 Unity object id.";
            return null;
        }

        static long[] ResolveObjectIds(long[] numericIds, string[] stringIds, out List<string> errors)
        {
            errors = new List<string>();
            var ids = new List<long>();

            if (numericIds != null)
            {
                ids.AddRange(numericIds.Where(id => id != 0));
            }

            if (stringIds != null)
            {
                foreach (var stringId in stringIds)
                {
                    var parsed = ResolveObjectId(null, stringId, out var error);
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        errors.Add(error);
                    }
                    else if (parsed.HasValue && parsed.Value != 0)
                    {
                        ids.Add(parsed.Value);
                    }
                }
            }

            return ids.Distinct().ToArray();
        }

        static bool ParentWasRequested(long? parentObjectId, string parentObjectIdString, string parent)
        {
            return (parentObjectId.HasValue && parentObjectId.Value != 0) ||
                   !string.IsNullOrWhiteSpace(parentObjectIdString) ||
                   !string.IsNullOrWhiteSpace(parent);
        }

        static void ApplyMove(MovePlan plan)
        {
            var target = plan.Target;
            var transform = target.transform;
            plan.Before ??= TransformSnapshot.Capture(transform);

            Undo.RegisterCompleteObjectUndo(transform, UndoName);
            Undo.SetTransformParent(transform, plan.Parent != null ? plan.Parent.transform : null, UndoName);

            if (plan.WorldPositionStays)
            {
                RestoreWorldTransform(transform, plan.Before);
            }

            if (plan.SiblingIndex.HasValue && plan.SiblingIndex.Value >= 0)
            {
                Undo.RecordObject(transform, UndoName);
                transform.SetSiblingIndex(plan.SiblingIndex.Value);
            }

            plan.After = TransformSnapshot.Capture(transform);
            MarkSceneDirty(target.scene);
            if (plan.Parent != null)
            {
                MarkSceneDirty(plan.Parent.scene);
            }
        }

        static GameObject ResolveGameObject(long? objectId, string target, string searchMethod, string scenePath)
        {
            if (objectId.HasValue && objectId.Value != 0)
            {
                var obj = UnityApiAdapter.GetObjectFromId(objectId.Value);
                if (obj is GameObject gameObject)
                {
                    return MatchesScene(gameObject, scenePath) ? gameObject : null;
                }

                if (obj is Component component)
                {
                    return MatchesScene(component.gameObject, scenePath) ? component.gameObject : null;
                }
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            return SceneObjectLocator.FindObject(target, searchMethod, new SceneObjectLocator.Options
            {
                IncludeInactive = true,
                IncludePrefabStage = true,
                IncludeDontDestroyOnLoad = false,
                ScenePath = scenePath,
                MatchContainsFallback = false
            });
        }

        static Scene ResolveTargetScene(string scenePath, GameObject parent)
        {
            if (parent != null && parent.scene.IsValid())
            {
                return parent.scene;
            }

            var scenes = SceneObjectLocator.GetLoadedScenes(scenePath).ToList();
            if (scenes.Count > 0)
            {
                return scenes[0];
            }

            return SceneManager.GetActiveScene();
        }

        static bool MatchesScene(GameObject gameObject, string scenePath)
        {
            if (gameObject == null || string.IsNullOrWhiteSpace(scenePath))
            {
                return gameObject != null;
            }

            var normalized = scenePath.Replace('\\', '/').Trim().TrimStart('/');
            var scene = gameObject.scene;
            return string.Equals(scene.name, normalized, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scene.path, normalized, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scene.path?.TrimStart('/'), normalized, StringComparison.OrdinalIgnoreCase);
        }

        static int CountSceneObjects(string scenePath)
        {
            return SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options
            {
                IncludeInactive = true,
                IncludePrefabStage = true,
                IncludeDontDestroyOnLoad = false,
                ScenePath = scenePath,
                ExcludeHiddenAndDontSave = true
            }).Count();
        }

        static object ValidateObjectCount(
            ManageSceneHierarchyParams parameters,
            int undoGroup,
            int countBefore,
            int countAfter,
            int expectedDelta)
        {
            if (!parameters.ValidateObjectCountUnchanged || !parameters.ValidateExpectedObjectCountDelta)
            {
                return null;
            }

            if (countAfter == countBefore + expectedDelta)
            {
                return null;
            }

            TryRevertUndoGroup(undoGroup);
            var objectCountValidation = BuildCountValidation(countBefore, countAfter, expectedDelta);
            return Response.Error("Scene hierarchy object-count validation failed; reverted the Undo group.", new
            {
                undoGroup,
                objectCountBefore = countBefore,
                objectCountAfter = countAfter,
                expectedObjectCountAfter = countBefore + expectedDelta,
                expectedDelta,
                validation = objectCountValidation,
                objectCountValidation,
                reverted = true
            });
        }

        static object BuildPlannedCountValidation(int expectedDelta)
        {
            return new
            {
                mode = "ExpectedDelta",
                validationMode = "ExpectedObjectCountDelta",
                expectedDelta,
                expectedObjectCountDelta = expectedDelta,
                actualDelta = (int?)null,
                actualObjectCountDelta = (int?)null,
                plannedDelta = expectedDelta,
                passed = true,
                objectCountValidationPassed = true,
                noUnexpectedCreateOrDelete = true
            };
        }

        static object BuildCountValidation(int countBefore, int countAfter, int expectedDelta)
        {
            var actualDelta = countAfter - countBefore;
            var passed = actualDelta == expectedDelta;
            return new
            {
                mode = "ExpectedDelta",
                validationMode = "ExpectedObjectCountDelta",
                expectedDelta,
                expectedObjectCountDelta = expectedDelta,
                actualDelta,
                actualObjectCountDelta = actualDelta,
                passed,
                objectCountValidationPassed = passed,
                noUnexpectedCreateOrDelete = passed
            };
        }

        static string BuildPlannedContainerPath(GameObject parent, string containerName)
        {
            return parent != null
                ? "/" + SceneObjectLocator.GetHierarchyPath(parent) + "/" + containerName
                : "/" + containerName;
        }

        static void RestoreWorldTransform(Transform transform, TransformSnapshot snapshot)
        {
            transform.position = snapshot.WorldPosition;
            transform.rotation = snapshot.WorldRotation;

            var parent = transform.parent;
            if (parent == null)
            {
                transform.localScale = snapshot.WorldScale;
                return;
            }

            var parentScale = parent.lossyScale;
            transform.localScale = new Vector3(
                SafeDivide(snapshot.WorldScale.x, parentScale.x, transform.localScale.x),
                SafeDivide(snapshot.WorldScale.y, parentScale.y, transform.localScale.y),
                SafeDivide(snapshot.WorldScale.z, parentScale.z, transform.localScale.z));
        }

        static float SafeDivide(float value, float divisor, float fallback)
        {
            return Mathf.Abs(divisor) > 0.000001f ? value / divisor : fallback;
        }

        static void MarkSceneDirty(Scene scene)
        {
            if (scene.IsValid() && scene.isLoaded)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        static bool TryRevertUndoGroup(int group)
        {
            try
            {
                var method = typeof(Undo).GetMethod("RevertAllDownToGroup", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                {
                    return false;
                }

                method.Invoke(null, new object[] { group });
                return true;
            }
            catch
            {
                return false;
            }
        }

        static object SerializeReference(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var objectId = UnityApiAdapter.GetObjectId(gameObject);
            return new
            {
                name = gameObject.name,
                objectId,
                objectIdString = objectId.ToString(CultureInfo.InvariantCulture),
                path = "/" + SceneObjectLocator.GetHierarchyPath(gameObject),
                scenePath = gameObject.scene.path,
                siblingIndex = gameObject.transform.GetSiblingIndex()
            };
        }

        sealed class MovePlan
        {
            public SceneHierarchyMove Request;
            public GameObject Target;
            public GameObject Parent;
            public bool WorldPositionStays;
            public int? SiblingIndex;
            public TransformSnapshot Before;
            public TransformSnapshot After;
            public string Error;

            public object ToResult(bool includeAfter, PlannedParentInfo plannedParent = null)
            {
                var plannedParentPath = plannedParent?.PlannedPath;
                var plannedParentObjectId = plannedParent?.ObjectId;
                var plannedParentWillBeCreated = plannedParent?.WillBeCreated ?? false;

                if (plannedParent == null && Parent != null)
                {
                    plannedParentPath = "/" + SceneObjectLocator.GetHierarchyPath(Parent);
                    plannedParentObjectId = UnityApiAdapter.GetObjectId(Parent);
                    plannedParentWillBeCreated = false;
                }

                return new
                {
                    target = SerializeReference(Target),
                    requested = new
                    {
                        Request.ObjectId,
                        Request.ObjectIdString,
                        Request.Target,
                        Request.ParentObjectId,
                        Request.ParentObjectIdString,
                        Request.Parent,
                        siblingIndex = SiblingIndex,
                        worldPositionStays = WorldPositionStays
                    },
                    error = Error,
                    before = Before?.ToResult(),
                    after = includeAfter ? After?.ToResult() : null,
                    diff = includeAfter && Before != null && After != null ? BuildTransformDiff(Before, After) : null,
                    parent = Parent != null ? SerializeReference(Parent) : null,
                    plannedParentContainerName = plannedParent?.ContainerName,
                    plannedParentPath,
                    plannedParentObjectId,
                    plannedParentObjectIdString = plannedParentObjectId?.ToString(CultureInfo.InvariantCulture),
                    plannedParentWillBeCreated
                };
            }

            static object BuildTransformDiff(TransformSnapshot before, TransformSnapshot after)
            {
                var positionDelta = Vector3.Distance(before.WorldPosition, after.WorldPosition);
                var rotationDelta = Quaternion.Angle(before.WorldRotation, after.WorldRotation);
                var scaleDelta = Vector3.Distance(before.WorldScale, after.WorldScale);
                return new
                {
                    parentChanged = before.ParentObjectId != after.ParentObjectId,
                    siblingIndexChanged = before.SiblingIndex != after.SiblingIndex,
                    worldPositionDelta = positionDelta.ToString("G9", CultureInfo.InvariantCulture),
                    worldRotationDeltaDegrees = rotationDelta.ToString("G9", CultureInfo.InvariantCulture),
                    worldScaleDelta = scaleDelta.ToString("G9", CultureInfo.InvariantCulture),
                    worldTransformPreserved = positionDelta <= PositionTolerance &&
                                              rotationDelta <= RotationTolerance &&
                                              scaleDelta <= ScaleTolerance
                };
            }
        }

        sealed class PlannedParentInfo
        {
            public string ContainerName;
            public string PlannedPath;
            public long? ObjectId;
            public bool WillBeCreated;
        }

        sealed class TransformSnapshot
        {
            public long ObjectId;
            public long? ParentObjectId;
            public string Path;
            public string ParentPath;
            public int SiblingIndex;
            public Vector3 LocalPosition;
            public Vector3 WorldPosition;
            public Quaternion LocalRotation;
            public Quaternion WorldRotation;
            public Vector3 LocalScale;
            public Vector3 WorldScale;

            public static TransformSnapshot Capture(Transform transform)
            {
                var parent = transform.parent;
                var objectId = UnityApiAdapter.GetObjectId(transform.gameObject);
                var parentObjectId = parent != null ? UnityApiAdapter.GetObjectId(parent.gameObject) : (long?)null;
                return new TransformSnapshot
                {
                    ObjectId = objectId,
                    ParentObjectId = parentObjectId,
                    Path = "/" + SceneObjectLocator.GetHierarchyPath(transform.gameObject),
                    ParentPath = parent != null ? "/" + SceneObjectLocator.GetHierarchyPath(parent.gameObject) : null,
                    SiblingIndex = transform.GetSiblingIndex(),
                    LocalPosition = transform.localPosition,
                    WorldPosition = transform.position,
                    LocalRotation = transform.localRotation,
                    WorldRotation = transform.rotation,
                    LocalScale = transform.localScale,
                    WorldScale = transform.lossyScale
                };
            }

            public object ToResult()
            {
                return new
                {
                    objectId = ObjectId,
                    objectIdString = ObjectId.ToString(CultureInfo.InvariantCulture),
                    parentObjectId = ParentObjectId,
                    parentObjectIdString = ParentObjectId?.ToString(CultureInfo.InvariantCulture),
                    path = Path,
                    parentPath = ParentPath,
                    siblingIndex = SiblingIndex,
                    localPosition = SerializeVector3(LocalPosition),
                    worldPosition = SerializeVector3(WorldPosition),
                    localRotationEuler = SerializeVector3(LocalRotation.eulerAngles),
                    worldRotationEuler = SerializeVector3(WorldRotation.eulerAngles),
                    localScale = SerializeVector3(LocalScale),
                    worldScale = SerializeVector3(WorldScale)
                };
            }

            static object SerializeVector3(Vector3 value)
            {
                return new { x = value.x, y = value.y, z = value.z };
            }
        }
    }
}
