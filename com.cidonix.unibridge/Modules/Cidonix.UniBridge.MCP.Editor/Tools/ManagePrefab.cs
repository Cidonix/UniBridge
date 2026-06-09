#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Handles prefab-specific workflows: creation, instantiation, variants, overrides, unpacking, and prefab stages.
    /// </summary>
    public static class ManagePrefab
    {
        public const string Title = "Manage prefabs";

        public const string Description = @"Create, instantiate, inspect, and edit Unity Prefabs.

Use this for prefab asset workflows that are too specific for generic scene or asset tools: saving scene objects as prefabs, creating prefabs from model/GameObject assets, instantiating prefab instances, checking prefab status and overrides, reading detailed prefab override diffs, applying/reverting full or selected overrides, unpacking instances, creating variants, and opening/saving/closing Prefab Mode.

Args:
    action: create, create_from_asset, instantiate, get_status, diff_overrides, apply_overrides, revert_overrides, apply_override, revert_override, remove_unused_overrides, unpack, create_variant, open_stage, save_stage, or close_stage.
    prefab_path: Assets/... .prefab path used as source or destination depending on action.
    asset_path: Source GameObject/model/prefab asset path for create_from_asset, instantiate, get_status, or open_stage.
    target: Scene GameObject name, hierarchy path, or instance/entity ID for instance-based actions.
    search_method: by_name, by_id, by_path, or by_id_or_name_or_path when resolving target/parent.
    parent, parent_instance_id, scene_path: Optional destination placement controls for instantiate.
    position, rotation, scale: Local transform values [x,y,z] for instantiate.
    connect_instance: For create, save and connect the source scene object to the new prefab.
    variant_path, source_prefab_path, source_asset_path: Inputs for create_variant.
    mode: For unpack, outermost_root/default or completely.
    include_default_overrides, include_values, max_items: Controls for diff_overrides.
    override_id, override_ids, override_kind, object_path, component_type, property_path: Optional selectors for apply_override/revert_override.
    dry_run: Preview selected override actions without changing prefabs or scenes.

Returns:
    success, message, and prefab-specific data such as prefab asset path/GUID, instantiated GameObject snapshot, prefab status, detailed override diff, selected override results, or Prefab Stage state.";

        [McpSchema("UniBridge_ManagePrefab")]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    action = new
                    {
                        type = "string",
                        description = "Prefab operation to perform",
                        @enum = new[]
                        {
                            "create",
                            "create_from_asset",
                            "instantiate",
                            "get_status",
                            "diff_overrides",
                            "apply_overrides",
                            "revert_overrides",
                            "apply_override",
                            "revert_override",
                            "remove_unused_overrides",
                            "unpack",
                            "create_variant",
                            "open_stage",
                            "save_stage",
                            "close_stage"
                        }
                    },
                    prefab_path = new { type = "string", description = "Prefab asset path, usually Assets/... .prefab" },
                    asset_path = new { type = "string", description = "Source asset path for GameObject, model, or prefab assets" },
                    source_prefab_path = new { type = "string", description = "Source prefab path for create_variant" },
                    source_asset_path = new { type = "string", description = "Source asset path for create_variant" },
                    variant_path = new { type = "string", description = "Destination .prefab path for create_variant" },
                    target = new
                    {
                        description = "Scene GameObject target (name/path or instance/entity ID)",
                        anyOf = new object[] { new { type = "string" }, new { type = "integer" }, new { type = "object" } }
                    },
                    game_object = new
                    {
                        description = "Alias for target",
                        anyOf = new object[] { new { type = "string" }, new { type = "integer" }, new { type = "object" } }
                    },
                    search_method = new
                    {
                        type = "string",
                        description = "Target search method",
                        @enum = new[] { "by_name", "by_id", "by_path", "by_id_or_name_or_path" }
                    },
                    parent = new
                    {
                        description = "Optional parent GameObject for instantiate",
                        anyOf = new object[] { new { type = "string" }, new { type = "integer" }, new { type = "object" } }
                    },
                    parent_instance_id = new { type = "integer", description = "Optional parent GameObject or Component instance/entity ID" },
                    scene_path = new { type = "string", description = "Loaded scene path or name for instantiate" },
                    name = new { type = "string", description = "Optional instance or prefab root name" },
                    position = new
                    {
                        type = "array",
                        description = "Local position [x,y,z] for instantiate",
                        items = new { type = "number" },
                        minItems = 3,
                        maxItems = 3
                    },
                    rotation = new
                    {
                        type = "array",
                        description = "Local rotation euler [x,y,z] for instantiate",
                        items = new { type = "number" },
                        minItems = 3,
                        maxItems = 3
                    },
                    scale = new
                    {
                        type = "array",
                        description = "Local scale [x,y,z] for instantiate",
                        items = new { type = "number" },
                        minItems = 3,
                        maxItems = 3
                    },
                    transform = new
                    {
                        type = "object",
                        description = "Optional transform object with position/localPosition, rotation/localRotation, and scale arrays",
                        additionalProperties = true
                    },
                    connect_instance = new { type = "boolean", description = "For create, connect source scene object to the created prefab" },
                    select_instantiated = new { type = "boolean", description = "Select instantiated prefab instance, default true" },
                    select_created = new { type = "boolean", description = "Alias for select_instantiated" },
                    mode = new
                    {
                        type = "string",
                        description = "Unpack mode",
                        @enum = new[] { "outermost_root", "outermost", "completely", "complete" }
                    },
                    unpack_mode = new
                    {
                        type = "string",
                        description = "Alias for mode",
                        @enum = new[] { "outermost_root", "outermost", "completely", "complete" }
                    },
                    include_default_overrides = new { type = "boolean", description = "For diff_overrides, include Unity default overrides such as root transform overrides" },
                    include_values = new { type = "boolean", description = "For diff_overrides, include serialized override values and object references, default true" },
                    include_variant_chain = new { type = "boolean", description = "For diff_overrides, include prefab/variant ancestry, default true" },
                    max_items = new { type = "integer", description = "Maximum items per override group returned by diff_overrides" },
                    dry_run = new { type = "boolean", description = "Preview selected override mutations without changing prefab or scene data" },
                    override_id = new { type = "string", description = "A specific override id from diff_overrides" },
                    override_ids = new
                    {
                        type = "array",
                        description = "Specific override ids from diff_overrides",
                        items = new { type = "string" }
                    },
                    override_kind = new
                    {
                        type = "string",
                        description = "Optional override kind selector",
                        @enum = new[] { "property", "object", "added_component", "removed_component", "added_game_object", "removed_game_object" }
                    },
                    object_path = new { type = "string", description = "Optional prefab instance-relative object path selector, '/' for root" },
                    component_type = new { type = "string", description = "Optional component/object type selector, full name or short name" },
                    property_path = new { type = "string", description = "Optional serialized property path selector" }
                },
                required = new[] { "action" },
                additionalProperties = false
            };
        }

        [McpTool("UniBridge_ManagePrefab", Description, Title, Groups = new[] { "core", "assets", "scene" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return Response.Error("Parameters cannot be null.");
            }

            var action = GetString(@params, "action")?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                return Response.Error("'action' parameter is required.");
            }

            try
            {
                switch (action)
                {
                    case "create":
                        return CreatePrefab(@params);
                    case "create_from_asset":
                    case "createfromasset":
                        return CreatePrefabFromAsset(@params);
                    case "instantiate":
                        return InstantiatePrefab(@params);
                    case "get_status":
                    case "getstatus":
                    case "status":
                        return GetPrefabStatus(@params);
                    case "diff_overrides":
                    case "diffoverrides":
                    case "list_overrides":
                    case "listoverrides":
                    case "inspect_overrides":
                    case "inspectoverrides":
                        return DiffPrefabOverrides(@params);
                    case "apply_overrides":
                    case "applyoverrides":
                        return ApplyPrefabOverrides(@params);
                    case "revert_overrides":
                    case "revertoverrides":
                        return RevertPrefabOverrides(@params);
                    case "apply_override":
                    case "applyoverride":
                    case "apply_selected_overrides":
                    case "applyselectedoverrides":
                        return ApplySelectedPrefabOverrides(@params);
                    case "revert_override":
                    case "revertoverride":
                    case "revert_selected_overrides":
                    case "revertselectedoverrides":
                        return RevertSelectedPrefabOverrides(@params);
                    case "remove_unused_overrides":
                    case "removeunusedoverrides":
                    case "cleanup_overrides":
                    case "cleanupoverrides":
                        return RemoveUnusedPrefabOverrides(@params);
                    case "unpack":
                        return UnpackPrefab(@params);
                    case "create_variant":
                    case "createvariant":
                        return CreatePrefabVariant(@params);
                    case "open_stage":
                    case "openstage":
                        return OpenPrefabStage(@params);
                    case "save_stage":
                    case "savestage":
                        return SavePrefabStage();
                    case "close_stage":
                    case "closestage":
                        return ClosePrefabStage();
                    default:
                        return Response.Error($"Unknown action: '{action}'. Supported actions: create, create_from_asset, instantiate, get_status, diff_overrides, apply_overrides, revert_overrides, apply_override, revert_override, remove_unused_overrides, unpack, create_variant, open_stage, save_stage, close_stage.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ManagePrefab] Action '{action}' failed: {e}");
                return Response.Error($"Prefab action '{action}' failed: {e.Message}");
            }
        }

        static object CreatePrefab(JObject @params)
        {
            var prefabPath = NormalizePrefabPath(GetString(@params, "prefab_path", "prefabPath"));
            if (AssetExists(prefabPath))
            {
                return Response.Error($"Prefab asset already exists at '{prefabPath}'.");
            }

            var targetToken = GetToken(@params, "target", "game_object", "gameObject");
            var connectInstance = GetBool(@params, false, "connect_instance", "connectInstance");
            if (targetToken == null && connectInstance)
            {
                return Response.Error("'connect_instance' cannot be true when creating an empty prefab.");
            }

            EnsureWritablePrefabPath(prefabPath);
            EnsureParentDirectoryExists(prefabPath);

            var sourceGameObject = targetToken == null ? null : ResolveGameObject(targetToken, GetString(@params, "search_method", "searchMethod"));
            if (targetToken != null && sourceGameObject == null)
            {
                return Response.Error($"Target GameObject '{targetToken}' was not found.");
            }

            var createdTemporaryObject = false;
            var prefabSource = sourceGameObject;
            if (prefabSource == null)
            {
                prefabSource = new GameObject(Path.GetFileNameWithoutExtension(prefabPath));
                createdTemporaryObject = true;
            }

            try
            {
                GameObject prefabAsset;
                if (connectInstance && sourceGameObject != null)
                {
                    prefabAsset = PrefabUtility.SaveAsPrefabAssetAndConnect(
                        prefabSource,
                        prefabPath,
                        InteractionMode.AutomatedAction);

                    if (sourceGameObject.scene.IsValid())
                    {
                        EditorSceneManager.MarkSceneDirty(sourceGameObject.scene);
                    }
                }
                else
                {
                    prefabAsset = PrefabUtility.SaveAsPrefabAsset(prefabSource, prefabPath);
                }

                if (prefabAsset == null)
                {
                    return Response.Error($"Failed to create prefab at '{prefabPath}'.");
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                return Response.Success(
                    $"Prefab created at '{prefabPath}'.",
                    new
                    {
                        prefabPath,
                        prefabGuid = ToOptionalString(AssetDatabase.AssetPathToGUID(prefabPath)),
                        prefabRootName = prefabAsset.name,
                        connectInstance,
                        sourceGameObject = sourceGameObject == null ? null : BuildGameObjectSnapshot(sourceGameObject),
                        prefabStatus = BuildPrefabStatusSnapshot(null, prefabPath)
                    });
            }
            finally
            {
                if (createdTemporaryObject && prefabSource != null)
                {
                    Object.DestroyImmediate(prefabSource);
                }
            }
        }

        static object CreatePrefabFromAsset(JObject @params)
        {
            var assetPath = NormalizeAssetPath(GetString(@params, "asset_path", "assetPath"));
            var prefabPath = NormalizePrefabPath(GetString(@params, "prefab_path", "prefabPath"));

            if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return Response.Error("'asset_path' cannot be a scene file.");
            }

            if (AssetExists(prefabPath))
            {
                return Response.Error($"Prefab asset already exists at '{prefabPath}'.");
            }

            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (sourceAsset == null)
            {
                return Response.Error($"Asset '{assetPath}' could not be loaded as a GameObject/model/prefab asset.");
            }

            EnsureWritablePrefabPath(prefabPath);
            EnsureParentDirectoryExists(prefabPath);

            var temporaryInstance = PrefabUtility.InstantiatePrefab(sourceAsset) as GameObject;
            if (temporaryInstance == null)
            {
                return Response.Error($"Failed to instantiate source asset '{assetPath}'.");
            }

            try
            {
                temporaryInstance.name = Path.GetFileNameWithoutExtension(prefabPath);
                var prefabAsset = PrefabUtility.SaveAsPrefabAsset(temporaryInstance, prefabPath);
                if (prefabAsset == null)
                {
                    return Response.Error($"Failed to create prefab at '{prefabPath}'.");
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                return Response.Success(
                    $"Prefab created from asset '{assetPath}' at '{prefabPath}'.",
                    new
                    {
                        assetPath,
                        prefabPath,
                        prefabGuid = ToOptionalString(AssetDatabase.AssetPathToGUID(prefabPath)),
                        prefabRootName = prefabAsset.name,
                        prefabStatus = BuildPrefabStatusSnapshot(null, prefabPath)
                    });
            }
            finally
            {
                Object.DestroyImmediate(temporaryInstance);
            }
        }

        static object InstantiatePrefab(JObject @params)
        {
            var prefabPath = NormalizeAssetPath(GetString(@params, "prefab_path", "prefabPath", "asset_path", "assetPath"));
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                return Response.Error($"Prefab or model asset '{prefabPath}' could not be loaded as a GameObject.");
            }

            var parentTransform = ResolveOptionalParentTransform(@params);
            var destinationScene = ResolveDestinationScene(GetString(@params, "scene_path", "scenePath"), parentTransform);
            var instantiatedObject = PrefabUtility.InstantiatePrefab(prefabAsset, destinationScene) as GameObject;
            if (instantiatedObject == null)
            {
                return Response.Error($"Failed to instantiate prefab asset '{prefabPath}'.");
            }

            Undo.RegisterCreatedObjectUndo(instantiatedObject, $"Instantiate {instantiatedObject.name}");

            var desiredName = GetString(@params, "name");
            if (!string.IsNullOrWhiteSpace(desiredName))
            {
                instantiatedObject.name = desiredName.Trim();
            }

            var effectiveParent = parentTransform ?? ResolvePrefabStageRootAsParent(destinationScene);
            if (effectiveParent != null && instantiatedObject.transform.parent != effectiveParent)
            {
                Undo.SetTransformParent(instantiatedObject.transform, effectiveParent, $"Parent {instantiatedObject.name}");
            }

            Undo.RecordObject(instantiatedObject.transform, $"Configure {instantiatedObject.name} Transform");
            ApplyTransform(instantiatedObject.transform, @params);

            var selectInstantiated = GetBool(@params, true, "select_instantiated", "selectInstantiated", "select_created", "selectCreated");
            if (selectInstantiated)
            {
                Selection.activeGameObject = instantiatedObject;
            }

            if (instantiatedObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(instantiatedObject.scene);
            }

            return Response.Success(
                $"Prefab '{prefabPath}' instantiated as '{instantiatedObject.name}'.",
                new
                {
                    prefabPath,
                    instantiated = true,
                    selected = selectInstantiated,
                    gameObject = BuildGameObjectSnapshot(instantiatedObject),
                    prefabStatus = BuildPrefabStatusSnapshot(instantiatedObject, prefabPath)
                });
        }

        static object GetPrefabStatus(JObject @params)
        {
            var targetToken = GetToken(@params, "target", "game_object", "gameObject");
            var assetPath = GetString(@params, "prefab_path", "prefabPath", "asset_path", "assetPath");
            var targetGameObject = targetToken == null ? null : ResolveGameObject(targetToken, GetString(@params, "search_method", "searchMethod"));
            var normalizedAssetPath = string.IsNullOrWhiteSpace(assetPath) ? null : NormalizeAssetPath(assetPath);

            if (targetToken != null && targetGameObject == null)
            {
                return Response.Error($"Target GameObject '{targetToken}' was not found.");
            }

            if (targetGameObject == null && string.IsNullOrWhiteSpace(normalizedAssetPath))
            {
                return Response.Error("'get_status' requires 'target', 'game_object', 'prefab_path', or 'asset_path'.");
            }

            return Response.Success("Prefab status retrieved.", BuildPrefabStatusSnapshot(targetGameObject, normalizedAssetPath));
        }

        static object DiffPrefabOverrides(JObject @params)
        {
            var instanceRoot = ResolvePrefabInstanceRoot(@params);
            var options = new PrefabOverrideUtility.DiffOptions
            {
                IncludeDefaultOverrides = GetBool(@params, false, "include_default_overrides", "includeDefaultOverrides"),
                IncludeValues = GetBool(@params, true, "include_values", "includeValues"),
                IncludeVariantChain = GetBool(@params, true, "include_variant_chain", "includeVariantChain"),
                MaxItems = GetInt(@params, 250, "max_items", "maxItems", "limit")
            };

            return Response.Success("Prefab override diff retrieved.", PrefabOverrideUtility.BuildDiff(instanceRoot, options));
        }

        static object ApplyPrefabOverrides(JObject @params)
        {
            var instanceRoot = ResolvePrefabInstanceRoot(@params);
            PrefabUtility.ApplyPrefabInstance(instanceRoot, InteractionMode.AutomatedAction);

            if (instanceRoot.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(instanceRoot.scene);
            }

            return Response.Success(
                $"Prefab overrides applied for '{instanceRoot.name}'.",
                new
                {
                    applied = true,
                    instanceRoot = BuildGameObjectSnapshot(instanceRoot),
                    prefabStatus = BuildPrefabStatusSnapshot(instanceRoot, null)
                });
        }

        static object ApplySelectedPrefabOverrides(JObject @params)
        {
            var instanceRoot = ResolvePrefabInstanceRoot(@params);
            return PrefabOverrideUtility.ApplyOrRevert(instanceRoot, BuildOverrideSelection(@params), apply: true);
        }

        static object RevertPrefabOverrides(JObject @params)
        {
            var instanceRoot = ResolvePrefabInstanceRoot(@params);
            PrefabUtility.RevertPrefabInstance(instanceRoot, InteractionMode.AutomatedAction);

            if (instanceRoot.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(instanceRoot.scene);
            }

            return Response.Success(
                $"Prefab overrides reverted for '{instanceRoot.name}'.",
                new
                {
                    reverted = true,
                    instanceRoot = BuildGameObjectSnapshot(instanceRoot),
                    prefabStatus = BuildPrefabStatusSnapshot(instanceRoot, null)
                });
        }

        static object RevertSelectedPrefabOverrides(JObject @params)
        {
            var instanceRoot = ResolvePrefabInstanceRoot(@params);
            return PrefabOverrideUtility.ApplyOrRevert(instanceRoot, BuildOverrideSelection(@params), apply: false);
        }

        static object RemoveUnusedPrefabOverrides(JObject @params)
        {
            var instanceRoot = ResolvePrefabInstanceRoot(@params);
            var dryRun = GetBool(@params, false, "dry_run", "dryRun");
            return PrefabOverrideUtility.RemoveUnusedOverrides(instanceRoot, dryRun);
        }

        static object UnpackPrefab(JObject @params)
        {
            var instanceRoot = ResolvePrefabInstanceRoot(@params);
            var mode = ResolveUnpackMode(GetString(@params, "mode", "unpack_mode", "unpackMode"));
            PrefabUtility.UnpackPrefabInstance(instanceRoot, mode, InteractionMode.AutomatedAction);

            if (instanceRoot.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(instanceRoot.scene);
            }

            return Response.Success(
                $"Prefab instance '{instanceRoot.name}' unpacked with mode '{mode}'.",
                new
                {
                    unpacked = true,
                    unpackMode = mode.ToString(),
                    gameObject = BuildGameObjectSnapshot(instanceRoot),
                    prefabStatus = BuildPrefabStatusSnapshot(instanceRoot, null)
                });
        }

        static object CreatePrefabVariant(JObject @params)
        {
            var variantPath = NormalizePrefabPath(GetString(@params, "variant_path", "variantPath", "prefab_path", "prefabPath"));
            if (AssetExists(variantPath))
            {
                return Response.Error($"Prefab asset already exists at '{variantPath}'.");
            }

            EnsureWritablePrefabPath(variantPath);
            EnsureParentDirectoryExists(variantPath);

            var targetToken = GetToken(@params, "target", "game_object", "gameObject");
            var sourceAssetPath = GetString(@params, "source_prefab_path", "sourcePrefabPath", "source_asset_path", "sourceAssetPath", "asset_path", "assetPath");

            GameObject variantSourceRoot = null;
            GameObject temporaryInstance = null;
            string resolvedSourcePrefabPath = null;

            try
            {
                if (targetToken != null)
                {
                    var targetGameObject = ResolveGameObject(targetToken, GetString(@params, "search_method", "searchMethod"));
                    if (targetGameObject == null)
                    {
                        return Response.Error($"Target GameObject '{targetToken}' was not found.");
                    }

                    variantSourceRoot = ResolvePrefabInstanceRoot(targetGameObject);
                    resolvedSourcePrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(variantSourceRoot);
                }
                else if (!string.IsNullOrWhiteSpace(sourceAssetPath))
                {
                    resolvedSourcePrefabPath = NormalizeAssetPath(sourceAssetPath);
                    var sourcePrefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(resolvedSourcePrefabPath);
                    if (sourcePrefabAsset == null)
                    {
                        return Response.Error($"Failed to load source prefab asset at '{resolvedSourcePrefabPath}'.");
                    }

                    temporaryInstance = PrefabUtility.InstantiatePrefab(sourcePrefabAsset) as GameObject;
                    if (temporaryInstance == null)
                    {
                        return Response.Error($"Failed to instantiate source prefab asset '{resolvedSourcePrefabPath}'.");
                    }

                    variantSourceRoot = temporaryInstance;
                }
                else
                {
                    return Response.Error("'create_variant' requires 'target'/'game_object' or 'source_prefab_path'/'source_asset_path'/'asset_path'.");
                }

                if (variantSourceRoot == null)
                {
                    return Response.Error("Failed to resolve a prefab variant source.");
                }

                variantSourceRoot.name = Path.GetFileNameWithoutExtension(variantPath);
                var createdVariant = PrefabUtility.SaveAsPrefabAsset(variantSourceRoot, variantPath);
                if (createdVariant == null)
                {
                    return Response.Error($"Failed to create prefab variant at '{variantPath}'.");
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                return Response.Success(
                    $"Prefab variant created at '{variantPath}'.",
                    new
                    {
                        created = true,
                        variantPath,
                        sourcePrefabPath = ToOptionalString(resolvedSourcePrefabPath),
                        prefabGuid = ToOptionalString(AssetDatabase.AssetPathToGUID(variantPath)),
                        prefabStatus = BuildPrefabStatusSnapshot(null, variantPath)
                    });
            }
            finally
            {
                if (temporaryInstance != null)
                {
                    Object.DestroyImmediate(temporaryInstance);
                }
            }
        }

        static object OpenPrefabStage(JObject @params)
        {
            var prefabPath = NormalizePrefabPath(GetString(@params, "prefab_path", "prefabPath", "asset_path", "assetPath"));
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                return Response.Error($"Failed to load prefab asset at '{prefabPath}'.");
            }

            var opened = AssetDatabase.OpenAsset(prefabAsset);
            return Response.Success(
                opened ? $"Prefab stage opened for '{prefabPath}'." : $"Unity did not open prefab stage for '{prefabPath}'.",
                new
                {
                    opened,
                    prefabPath,
                    prefabStage = BuildPrefabStageSnapshot(PrefabStageUtility.GetCurrentPrefabStage())
                });
        }

        static object SavePrefabStage()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null || prefabStage.prefabContentsRoot == null || string.IsNullOrWhiteSpace(prefabStage.assetPath))
            {
                return Response.Error("There is no active prefab stage to save.", new
                {
                    prefabStage = BuildPrefabStageSnapshot(null),
                    reason = "PrefabStageUtility.GetCurrentPrefabStage returned null or an incomplete stage. The stage may already have been closed by Unity, a previous save/reload, or an editor focus transition."
                });
            }

            var beforeStage = BuildPrefabStageSnapshot(prefabStage);
            var savedAsset = PrefabUtility.SaveAsPrefabAsset(prefabStage.prefabContentsRoot, prefabStage.assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var afterStage = PrefabStageUtility.GetCurrentPrefabStage();
            var stageStillOpen = afterStage != null && afterStage.prefabContentsRoot != null;

            return Response.Success(
                savedAsset != null ? $"Prefab stage saved for '{prefabStage.assetPath}'." : $"Prefab stage save returned no asset for '{prefabStage.assetPath}'.",
                new
                {
                    saved = savedAsset != null,
                    prefabPath = prefabStage.assetPath,
                    stageStillOpen,
                    stageStateBeforeSave = beforeStage,
                    prefabStage = BuildPrefabStageSnapshot(afterStage),
                    stageStateNote = stageStillOpen
                        ? "Prefab Stage is still open after save."
                        : "Prefab Stage is no longer current after save; Unity may have closed or reloaded the stage.",
                    prefabStatus = BuildPrefabStatusSnapshot(null, prefabStage.assetPath)
                });
        }

        static object ClosePrefabStage()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null)
            {
                return Response.Success(
                    "No prefab stage is currently open; it may already have been closed by Unity or a previous save/reload.",
                    new
                    {
                        closed = false,
                        prefabStage = BuildPrefabStageSnapshot(null),
                        reason = "PrefabStageUtility.GetCurrentPrefabStage returned null immediately before close_stage."
                    });
            }

            var closedAssetPath = prefabStage.assetPath;
            var beforeStage = BuildPrefabStageSnapshot(prefabStage);
            StageUtility.GoToMainStage();
            var remainingStage = PrefabStageUtility.GetCurrentPrefabStage();

            return Response.Success(
                remainingStage == null ? "Prefab stage closed." : "Prefab stage close was requested, but a prefab stage is still active.",
                new
                {
                    closed = remainingStage == null,
                    closedAssetPath = ToOptionalString(closedAssetPath),
                    stageStateBeforeClose = beforeStage,
                    prefabStage = BuildPrefabStageSnapshot(remainingStage)
                });
        }

        static GameObject ResolvePrefabInstanceRoot(JObject @params)
        {
            var targetToken = GetToken(@params, "target", "game_object", "gameObject");
            if (targetToken == null)
            {
                throw new InvalidOperationException("A target prefab instance is required.");
            }

            var target = ResolveGameObject(targetToken, GetString(@params, "search_method", "searchMethod"));
            if (target == null)
            {
                throw new InvalidOperationException($"Target GameObject '{targetToken}' was not found.");
            }

            return ResolvePrefabInstanceRoot(target);
        }

        static GameObject ResolvePrefabInstanceRoot(GameObject target)
        {
            var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(target);
            if (instanceRoot == null)
            {
                throw new InvalidOperationException($"GameObject '{GetHierarchyPath(target)}' is not part of a prefab instance.");
            }

            return instanceRoot;
        }

        static Transform ResolveOptionalParentTransform(JObject @params)
        {
            var parentToken = GetToken(@params, "parent");
            if (parentToken != null)
            {
                var parentGameObject = ResolveGameObject(parentToken, GetString(@params, "search_method", "searchMethod"));
                if (parentGameObject == null)
                {
                    throw new InvalidOperationException($"Parent GameObject '{parentToken}' was not found.");
                }

                return parentGameObject.transform;
            }

            var parentInstanceId = GetLong(@params, "parent_instance_id", "parentInstanceId");
            if (!parentInstanceId.HasValue || parentInstanceId.Value <= 0)
            {
                return null;
            }

            var parentObject = UnityApiAdapter.GetObjectFromId(parentInstanceId.Value);
            if (parentObject is GameObject gameObject)
            {
                return gameObject.transform;
            }

            if (parentObject is Component component)
            {
                return component.transform;
            }

            throw new InvalidOperationException("'parent_instance_id' does not reference a GameObject or Component.");
        }

        static GameObject ResolveGameObject(JToken token, string searchMethod = null)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer && long.TryParse(token.ToString(), out var id))
            {
                var obj = UnityApiAdapter.GetObjectFromId(id);
                if (obj is GameObject gameObject)
                {
                    return gameObject;
                }

                if (obj is Component component)
                {
                    return component.gameObject;
                }
            }

            if (token is JObject instruction && instruction.TryGetValue("find", out _))
            {
                var foundObject = ManageGameObject.FindObjectByInstruction(instruction, typeof(GameObject));
                return foundObject as GameObject;
            }

            return ObjectsHelper.FindObject(token, string.IsNullOrWhiteSpace(searchMethod) ? "by_id_or_name_or_path" : searchMethod);
        }

        static Scene ResolveDestinationScene(string scenePath, Transform parentTransform)
        {
            if (parentTransform != null)
            {
                return parentTransform.gameObject.scene;
            }

            if (!string.IsNullOrWhiteSpace(scenePath))
            {
                var normalizedScenePath = scenePath.Replace('\\', '/').Trim();
                for (var index = 0; index < SceneManager.sceneCount; index++)
                {
                    var candidateScene = SceneManager.GetSceneAt(index);
                    if (string.Equals(candidateScene.path, normalizedScenePath, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(candidateScene.name, normalizedScenePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidateScene;
                    }
                }

                throw new InvalidOperationException($"Scene '{scenePath}' is not currently open.");
            }

            var currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (currentPrefabStage != null && currentPrefabStage.scene.IsValid() && currentPrefabStage.scene.isLoaded)
            {
                return currentPrefabStage.scene;
            }

            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded)
            {
                throw new InvalidOperationException("Cannot resolve a destination scene for prefab instantiation.");
            }

            return activeScene;
        }

        static Transform ResolvePrefabStageRootAsParent(Scene destinationScene)
        {
            var currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (currentPrefabStage != null &&
                currentPrefabStage.scene == destinationScene &&
                currentPrefabStage.prefabContentsRoot != null)
            {
                return currentPrefabStage.prefabContentsRoot.transform;
            }

            return null;
        }

        static void ApplyTransform(Transform transform, JObject @params)
        {
            var transformObject = @params["transform"] as JObject;

            var position = ParseVector3(GetToken(@params, "position") as JArray)
                ?? ParseVector3(GetToken(transformObject, "local_position", "localPosition", "position") as JArray);
            var rotation = ParseVector3(GetToken(@params, "rotation") as JArray)
                ?? ParseVector3(GetToken(transformObject, "local_rotation", "localRotation", "rotation") as JArray);
            var scale = ParseVector3(GetToken(@params, "scale") as JArray)
                ?? ParseVector3(GetToken(transformObject, "local_scale", "localScale", "scale") as JArray);

            if (position.HasValue)
            {
                transform.localPosition = position.Value;
            }

            if (rotation.HasValue)
            {
                transform.localEulerAngles = rotation.Value;
            }

            if (scale.HasValue)
            {
                transform.localScale = scale.Value;
            }
        }

        static Vector3? ParseVector3(JArray array)
        {
            if (array == null)
            {
                return null;
            }

            if (array.Count != 3)
            {
                throw new InvalidOperationException("Vector3 values must be arrays of exactly 3 numbers: [x, y, z].");
            }

            return new Vector3(
                array[0].ToObject<float>(),
                array[1].ToObject<float>(),
                array[2].ToObject<float>());
        }

        static PrefabUnpackMode ResolveUnpackMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                return PrefabUnpackMode.OutermostRoot;
            }

            if (string.Equals(mode, "completely", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "complete", StringComparison.OrdinalIgnoreCase))
            {
                return PrefabUnpackMode.Completely;
            }

            return PrefabUnpackMode.OutermostRoot;
        }

        static PrefabOverrideUtility.OverrideSelection BuildOverrideSelection(JObject @params)
        {
            return new PrefabOverrideUtility.OverrideSelection
            {
                OverrideId = GetString(@params, "override_id", "overrideId", "id"),
                OverrideIds = GetStringArray(@params, "override_ids", "overrideIds", "ids"),
                OverrideKind = GetString(@params, "override_kind", "overrideKind", "kind"),
                ObjectPath = GetString(@params, "object_path", "objectPath", "path"),
                ComponentType = GetString(@params, "component_type", "componentType", "type"),
                PropertyPath = GetString(@params, "property_path", "propertyPath"),
                DryRun = GetBool(@params, false, "dry_run", "dryRun")
            };
        }

        static object BuildPrefabStatusSnapshot(GameObject targetGameObject, string prefabAssetPath)
        {
            var normalizedPrefabAssetPath = string.IsNullOrWhiteSpace(prefabAssetPath)
                ? null
                : NormalizeAssetPath(prefabAssetPath);
            var resolvedAssetPath = normalizedPrefabAssetPath;

            if (string.IsNullOrWhiteSpace(resolvedAssetPath) && targetGameObject != null)
            {
                resolvedAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(targetGameObject);
                if (string.IsNullOrWhiteSpace(resolvedAssetPath) && PrefabUtility.IsPartOfPrefabAsset(targetGameObject))
                {
                    resolvedAssetPath = AssetDatabase.GetAssetPath(targetGameObject);
                }
            }

            var prefabAsset = string.IsNullOrWhiteSpace(resolvedAssetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(resolvedAssetPath);
            var instanceRoot = targetGameObject == null ? null : PrefabUtility.GetNearestPrefabInstanceRoot(targetGameObject);
            var outermostRoot = targetGameObject == null ? null : PrefabUtility.GetOutermostPrefabInstanceRoot(targetGameObject);
            var hasAnyOverrides = instanceRoot != null && PrefabUtility.HasPrefabInstanceAnyOverrides(instanceRoot, true);
            var hasNonDefaultOverrides = instanceRoot != null && PrefabUtility.HasPrefabInstanceAnyOverrides(instanceRoot, false);

            return new
            {
                prefabAssetPath = ToOptionalString(resolvedAssetPath),
                prefabGuid = string.IsNullOrWhiteSpace(resolvedAssetPath)
                    ? null
                    : ToOptionalString(AssetDatabase.AssetPathToGUID(resolvedAssetPath)),
                prefabAssetType = prefabAsset == null
                    ? PrefabAssetType.NotAPrefab.ToString()
                    : PrefabUtility.GetPrefabAssetType(prefabAsset).ToString(),
                prefabInstanceStatus = targetGameObject == null
                    ? PrefabInstanceStatus.NotAPrefab.ToString()
                    : PrefabUtility.GetPrefabInstanceStatus(targetGameObject).ToString(),
                isPrefabAsset = prefabAsset != null && PrefabUtility.IsPartOfPrefabAsset(prefabAsset),
                isPrefabInstance = targetGameObject != null && PrefabUtility.IsPartOfPrefabInstance(targetGameObject),
                isAnyPrefabInstanceRoot = targetGameObject != null && PrefabUtility.IsAnyPrefabInstanceRoot(targetGameObject),
                hasOverrides = hasAnyOverrides,
                hasNonDefaultOverrides,
                overrides = BuildOverrideSummary(instanceRoot),
                nearestPrefabInstanceRoot = instanceRoot == null ? null : BuildGameObjectSnapshot(instanceRoot),
                outermostPrefabInstanceRoot = outermostRoot == null ? null : BuildGameObjectSnapshot(outermostRoot),
                target = targetGameObject == null ? null : BuildGameObjectSnapshot(targetGameObject),
                prefabAsset = prefabAsset == null
                    ? null
                    : new
                    {
                        name = prefabAsset.name,
                        assetPath = resolvedAssetPath,
                        assetGuid = AssetDatabase.AssetPathToGUID(resolvedAssetPath)
                    },
                prefabStage = BuildPrefabStageSnapshot(PrefabStageUtility.GetCurrentPrefabStage())
            };
        }

        static object BuildOverrideSummary(GameObject instanceRoot)
        {
            if (instanceRoot == null)
            {
                return new
                {
                    objectOverrides = 0,
                    addedComponents = 0,
                    removedComponents = 0,
                    addedGameObjects = 0
                };
            }

            try
            {
                return new
                {
                    objectOverrides = PrefabUtility.GetObjectOverrides(instanceRoot, false).Count,
                    addedComponents = PrefabUtility.GetAddedComponents(instanceRoot).Count,
                    removedComponents = PrefabUtility.GetRemovedComponents(instanceRoot).Count,
                    addedGameObjects = PrefabUtility.GetAddedGameObjects(instanceRoot).Count
                };
            }
            catch (Exception e)
            {
                return new
                {
                    objectOverrides = 0,
                    addedComponents = 0,
                    removedComponents = 0,
                    addedGameObjects = 0,
                    error = e.Message
                };
            }
        }

        static object BuildPrefabStageSnapshot(PrefabStage prefabStage)
        {
            if (prefabStage == null)
            {
                return new
                {
                    isOpen = false,
                    assetPath = (string)null,
                    rootName = (string)null,
                    rootHierarchyPath = (string)null,
                    isDirty = false,
                    mode = (string)null
                };
            }

            return new
            {
                isOpen = true,
                assetPath = ToOptionalString(prefabStage.assetPath),
                rootName = prefabStage.prefabContentsRoot?.name,
                rootHierarchyPath = prefabStage.prefabContentsRoot == null ? null : GetHierarchyPath(prefabStage.prefabContentsRoot),
                isDirty = prefabStage.scene.isDirty,
                mode = prefabStage.mode.ToString()
            };
        }

        static object BuildGameObjectSnapshot(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            return new
            {
                name = gameObject.name,
                instanceID = UnityApiAdapter.GetObjectId(gameObject),
                hierarchyPath = GetHierarchyPath(gameObject),
                sceneName = gameObject.scene.name,
                scenePath = gameObject.scene.path,
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                prefabAssetPath = ToOptionalString(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject)),
                transform = new
                {
                    localPosition = ToVectorObject(gameObject.transform.localPosition),
                    localRotation = ToVectorObject(gameObject.transform.localEulerAngles),
                    localScale = ToVectorObject(gameObject.transform.localScale)
                }
            };
        }

        static object ToVectorObject(Vector3 value)
        {
            return new { x = value.x, y = value.y, z = value.z };
        }

        static string GetHierarchyPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var parts = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }

        static string NormalizePrefabPath(string assetPath)
        {
            var normalizedPath = NormalizeAssetPath(assetPath);
            if (!normalizedPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("'prefab_path' must point to a .prefab asset.");
            }

            return normalizedPath;
        }

        static string NormalizeAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new InvalidOperationException("Asset path cannot be empty.");
            }

            var normalized = assetPath.Replace('\\', '/').Trim();
            var projectRoot = GetProjectRoot().Replace('\\', '/').TrimEnd('/');
            var assetsRoot = Application.dataPath.Replace('\\', '/').TrimEnd('/');

            if (normalized.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(projectRoot.Length + 1);
            }
            else if (normalized.StartsWith(assetsRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "Assets/" + normalized.Substring(assetsRoot.Length + 1);
            }

            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            return "Assets/" + normalized.TrimStart('/');
        }

        static void EnsureWritablePrefabPath(string prefabPath)
        {
            if (!prefabPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Prefab write operations must target the project Assets folder.");
            }
        }

        static bool AssetExists(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

#if UNITY_2022_0_OR_NEWER
            if (AssetDatabase.AssetPathExists(assetPath))
            {
                return true;
            }
#endif
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
            {
                return true;
            }

            var absolutePath = Path.Combine(GetProjectRoot(), assetPath).Replace('\\', '/');
            return File.Exists(absolutePath) || Directory.Exists(absolutePath);
        }

        static void EnsureParentDirectoryExists(string assetPath)
        {
            var directoryPath = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directoryPath) || AssetDatabase.IsValidFolder(directoryPath))
            {
                return;
            }

            var absoluteDirectory = Path.Combine(GetProjectRoot(), directoryPath);
            Directory.CreateDirectory(absoluteDirectory);
            AssetDatabase.Refresh();
        }

        static string GetProjectRoot()
        {
            var assetsDirectory = new DirectoryInfo(Application.dataPath);
            return assetsDirectory.Parent?.FullName ?? Directory.GetCurrentDirectory();
        }

        static string GetString(JObject obj, params string[] names)
        {
            var token = GetToken(obj, names);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        static JToken GetToken(JObject obj, params string[] names)
        {
            if (obj == null || names == null)
            {
                return null;
            }

            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    obj.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                {
                    return token;
                }
            }

            return null;
        }

        static bool GetBool(JObject obj, bool defaultValue, params string[] names)
        {
            var token = GetToken(obj, names);
            return token == null ? defaultValue : token.ToObject<bool>();
        }

        static int GetInt(JObject obj, int defaultValue, params string[] names)
        {
            var token = GetToken(obj, names);
            return token == null ? defaultValue : token.ToObject<int>();
        }

        static long? GetLong(JObject obj, params string[] names)
        {
            var token = GetToken(obj, names);
            if (token == null)
            {
                return null;
            }

            return token.ToObject<long>();
        }

        static string[] GetStringArray(JObject obj, params string[] names)
        {
            var token = GetToken(obj, names);
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Array)
            {
                return token.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            }

            var single = token.ToString();
            return string.IsNullOrWhiteSpace(single) ? null : new[] { single };
        }

        static string ToOptionalString(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
