#nullable disable
#pragma warning disable 0618 // LightProbeProxyVolume is deprecated in Unity 6000.5, but scene inspection still reports existing components.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read-only scene and GameObject view with bounded detail levels.
    /// </summary>
    public static class SceneObjectView
    {
        const int MaxDepthLimit = 12;
        const int MaxObjectsLimit = 2000;
        const int MaxChildrenLimit = 500;
        const int MaxRootsLimit = 1000;
        const int MaxSerializedPropertiesLimit = 500;

        public const string Title = "View scene objects with detail levels";

        public const string Description = @"Inspect loaded scene hierarchy and GameObjects with bounded detail levels.

Use this when an agent needs a compact scene/object view: a flattened hierarchy for orientation, then focused structured object details only where needed. It is read-only and designed to avoid dumping huge scenes by default.

Args:
    Action: Hierarchy, View, Query, or Selection.
    Detail: Brief, Standard, Detailed, or Full.
    Profile: Optional component focus profile: Rendering, Physics2D, Physics3D, UI, Animation, VFX, Audio, Gameplay, Navigation, Input, Tilemap2D, Lighting, or VideoTimeline.
    Target/Targets: GameObject id, hierarchy path, name, tag/layer/component query depending on SearchMethod.
    Query filters: NameContains/ExactName, ComponentType, Tag, Layer, and Offset. Filters are combined with AND logic.
    ScenePath: Optional loaded scene path or name.
    IncludeDontDestroyOnLoad: Include runtime DontDestroyOnLoad objects in play mode. Defaults to true.
    Index: Optional duplicate index for path/name results.
    SearchMethod: Auto, ById, ByPath, ByName, ByTag, ByLayer, or ByComponent.
    MaxDepth/MaxObjects/MaxChildren/MaxRoots: Output limits.
    IncludeSerializedProperties/IncludeComponentProperties: Full-detail component property controls.

Returns:
    success, message, and scene/object view data with flattened hierarchy, structured nodes, component summaries, bounds, prefab hints, and truncation metadata.";

        [McpTool("UniBridge_SceneObjectView", Description, Title, Groups = new[] { "core", "scene", "debug" }, EnabledByDefault = true)]
        public static object HandleCommand(SceneObjectViewParams parameters)
        {
            parameters ??= new SceneObjectViewParams();

            try
            {
                var options = ViewOptions.From(parameters);
                return parameters.Action switch
                {
                    SceneObjectViewAction.Hierarchy => BuildHierarchy(parameters, options),
                    SceneObjectViewAction.View => BuildObjectView(parameters, options),
                    SceneObjectViewAction.Query => BuildQueryView(parameters, options),
                    SceneObjectViewAction.Selection => BuildSelectionView(parameters, options),
                    _ => Response.Error($"Unsupported SceneObjectView action '{parameters.Action}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SceneObjectView] {parameters.Action} failed: {ex}");
                return Response.Error($"SceneObjectView failed: {ex.Message}");
            }
        }

        static object BuildHierarchy(SceneObjectViewParams parameters, ViewOptions options)
        {
            var scenes = ResolveScenes(parameters.ScenePath, parameters.IncludeDontDestroyOnLoad);
            if (scenes.Count == 0)
                return Response.Error($"No loaded scene matched ScenePath '{parameters.ScenePath}'.");

            var includeFlattened = parameters.IncludeFlattened ?? true;
            var includeStructured = parameters.IncludeStructured ?? parameters.Detail != SceneObjectDetailLevel.Brief;
            var sceneResults = new List<object>();
            var totalReturned = 0;
            var truncatedByCount = false;
            var truncatedByDepth = false;
            var remaining = options.MaxObjects;

            foreach (var scene in scenes)
            {
                var roots = scene.GetRootGameObjects()
                    .Where(root => root != null)
                    .Take(options.MaxRoots)
                    .ToArray();
                var rootsTruncated = scene.rootCount > roots.Length;

                string flattened = null;
                if (includeFlattened)
                {
                    flattened = PrintHierarchy(
                        roots.Select(root => root.transform).ToList(),
                        options.MaxObjects,
                        options.MaxChildren,
                        parameters.IndexDisplayMode);
                }

                List<object> structuredRoots = null;
                if (includeStructured)
                {
                    structuredRoots = new List<object>();
                    foreach (var root in roots)
                    {
                        if (remaining <= 0)
                        {
                            truncatedByCount = true;
                            break;
                        }

                        structuredRoots.Add(SerializeGameObject(root, options, depth: 0, ref remaining, ref totalReturned, ref truncatedByCount, ref truncatedByDepth));
                    }
                }

                sceneResults.Add(new
                {
                    scene = ToSceneInfo(scene),
                    rootCount = scene.rootCount,
                    rootsReturned = roots.Length,
                    rootsTruncated,
                    flattened,
                    roots = structuredRoots
                });

                if (remaining <= 0)
                {
                    truncatedByCount = true;
                    break;
                }
            }

            return Response.Success("Built scene hierarchy view.", new
            {
                action = "Hierarchy",
                detail = parameters.Detail.ToString(),
                profile = options.Profile.ToString(),
                limits = options.ToLimits(),
                scenes = sceneResults,
                summary = new
                {
                    sceneCount = sceneResults.Count,
                    objectsReturned = totalReturned,
                    truncatedByCount,
                    truncatedByDepth,
                    flattenedIncluded = includeFlattened,
                    structuredIncluded = includeStructured
                }
            });
        }

        static object BuildObjectView(SceneObjectViewParams parameters, ViewOptions options)
        {
            var targets = new List<string>();
            if (parameters.Targets != null)
                targets.AddRange(parameters.Targets.Where(target => !string.IsNullOrWhiteSpace(target)));
            if (!string.IsNullOrWhiteSpace(parameters.Target))
                targets.Add(parameters.Target);

            if (targets.Count == 0 && Selection.gameObjects.Length > 0)
                targets.AddRange(Selection.gameObjects.Select(go => UnityApiAdapter.GetObjectId(go).ToString()));

            if (targets.Count == 0)
                return Response.Error("SceneObjectView View requires Target/Targets or a selected GameObject.");

            var remaining = options.MaxObjects;
            var totalReturned = 0;
            var truncatedByCount = false;
            var truncatedByDepth = false;
            var resolvedObjects = new List<GameObject>();
            var resultObjects = new List<object>();
            var misses = new List<object>();

            foreach (var target in targets)
            {
                var matches = ResolveGameObjects(target, parameters, options);
                if (parameters.Index.HasValue)
                    matches = parameters.Index.Value >= 0 && parameters.Index.Value < matches.Count ? new List<GameObject> { matches[parameters.Index.Value] } : new List<GameObject>();

                if (matches.Count == 0)
                {
                    misses.Add(new { target, error = "No matching GameObject." });
                    continue;
                }

                foreach (var go in matches)
                {
                    if (remaining <= 0)
                    {
                        truncatedByCount = true;
                        break;
                    }

                    resolvedObjects.Add(go);
                    resultObjects.Add(SerializeGameObject(go, options, depth: 0, ref remaining, ref totalReturned, ref truncatedByCount, ref truncatedByDepth));
                }
            }

            if (parameters.Select && resolvedObjects.Count > 0)
                Selection.objects = resolvedObjects.Cast<Object>().ToArray();

            return Response.Success($"Viewed {resultObjects.Count} GameObject(s).", new
            {
                action = "View",
                detail = parameters.Detail.ToString(),
                profile = options.Profile.ToString(),
                limits = options.ToLimits(),
                requested = targets,
                returned = resultObjects.Count,
                objects = resultObjects,
                misses,
                summary = new
                {
                    objectsReturned = totalReturned,
                    truncatedByCount,
                    truncatedByDepth,
                    selected = parameters.Select && resolvedObjects.Count > 0
                }
            });
        }

        static object BuildSelectionView(SceneObjectViewParams parameters, ViewOptions options)
        {
            var selected = Selection.gameObjects
                .Where(go => go != null)
                .ToList();

            var remaining = options.MaxObjects;
            var totalReturned = 0;
            var truncatedByCount = false;
            var truncatedByDepth = false;
            var objects = new List<object>();

            foreach (var go in selected)
            {
                if (remaining <= 0)
                {
                    truncatedByCount = true;
                    break;
                }

                objects.Add(SerializeGameObject(go, options, depth: 0, ref remaining, ref totalReturned, ref truncatedByCount, ref truncatedByDepth));
            }

            return Response.Success($"Viewed {objects.Count} selected GameObject(s).", new
            {
                action = "Selection",
                detail = parameters.Detail.ToString(),
                profile = options.Profile.ToString(),
                limits = options.ToLimits(),
                selectionCount = Selection.count,
                selectedGameObjects = selected.Count,
                objects,
                summary = new
                {
                    objectsReturned = totalReturned,
                    truncatedByCount,
                    truncatedByDepth
                }
            });
        }

        static object BuildQueryView(SceneObjectViewParams parameters, ViewOptions options)
        {
            var exactName = string.IsNullOrWhiteSpace(parameters.ExactName) ? null : parameters.ExactName.Trim();
            var nameContains = string.IsNullOrWhiteSpace(parameters.NameContains) ? null : parameters.NameContains.Trim();
            if (string.IsNullOrWhiteSpace(nameContains) &&
                string.IsNullOrWhiteSpace(exactName) &&
                !string.IsNullOrWhiteSpace(parameters.Target) &&
                (string.IsNullOrWhiteSpace(parameters.SearchMethod) ||
                 string.Equals(parameters.SearchMethod, "Auto", StringComparison.OrdinalIgnoreCase)))
            {
                nameContains = parameters.Target.Trim();
            }

            var componentType = string.IsNullOrWhiteSpace(parameters.ComponentType) ? null : parameters.ComponentType.Trim();
            var tag = string.IsNullOrWhiteSpace(parameters.Tag) ? null : parameters.Tag.Trim();
            var layer = string.IsNullOrWhiteSpace(parameters.Layer) ? null : parameters.Layer.Trim();

            if (exactName == null && nameContains == null && componentType == null && tag == null && layer == null)
            {
                return Response.Error("SceneObjectView Query requires at least one filter: ExactName, NameContains, ComponentType, Tag, or Layer.");
            }

            if (tag != null && !InternalEditorUtility.tags.Contains(tag))
                return Response.Error($"Tag '{tag}' does not exist.");

            int? layerIndex = null;
            if (layer != null)
            {
                if (int.TryParse(layer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericLayer))
                {
                    if (numericLayer < 0 || numericLayer > 31)
                        return Response.Error($"Layer index '{layer}' is outside the valid 0..31 range.");
                    layerIndex = numericLayer;
                }
                else
                {
                    var namedLayer = LayerMask.NameToLayer(layer);
                    if (namedLayer == -1)
                        return Response.Error($"Layer '{layer}' does not exist.");
                    layerIndex = namedLayer;
                }
            }

            Type resolvedComponentType = null;
            if (componentType != null && ComponentResolver.TryResolve(componentType, out var componentResolved, out _))
                resolvedComponentType = componentResolved;

            var scenes = ResolveScenes(parameters.ScenePath, parameters.IncludeDontDestroyOnLoad);
            if (scenes.Count == 0)
                return Response.Error($"No loaded scene matched ScenePath '{parameters.ScenePath}'.");

            var allMatches = new List<GameObject>();
            foreach (var scene in scenes)
            {
                allMatches.AddRange(GetSceneObjects(scene)
                    .Where(go => QueryMatches(go, exactName, nameContains, resolvedComponentType, componentType, tag, layerIndex, parameters.IncludeInactive)));
            }

            var offset = Mathf.Max(0, parameters.Offset ?? 0);
            var page = allMatches.Skip(offset).Take(options.MaxObjects).ToList();
            var previousIncludeChildren = options.IncludeChildren;
            if (!parameters.IncludeChildren.HasValue)
                options.IncludeChildren = false;

            var remaining = options.MaxObjects;
            var totalReturned = 0;
            var truncatedByCount = allMatches.Count > offset + page.Count;
            var truncatedByDepth = false;
            var objects = new List<object>();
            foreach (var go in page)
            {
                if (remaining <= 0)
                    break;

                objects.Add(SerializeGameObject(go, options, depth: 0, ref remaining, ref totalReturned, ref truncatedByCount, ref truncatedByDepth));
            }

            options.IncludeChildren = previousIncludeChildren;

            if (parameters.Select && page.Count > 0)
                Selection.objects = page.Cast<Object>().ToArray();

            return Response.Success($"Query matched {allMatches.Count} GameObject(s); returned {objects.Count}.", new
            {
                action = "Query",
                detail = parameters.Detail.ToString(),
                profile = options.Profile.ToString(),
                query = new
                {
                    exactName,
                    nameContains,
                    componentType,
                    tag,
                    layer,
                    resolvedLayerIndex = layerIndex,
                    offset
                },
                limits = options.ToLimits(),
                totalCount = allMatches.Count,
                offset,
                count = objects.Count,
                objects,
                summary = new
                {
                    objectsReturned = totalReturned,
                    truncatedByCount,
                    truncatedByDepth,
                    selected = parameters.Select && page.Count > 0
                }
            });
        }

        static object SerializeGameObject(
            GameObject gameObject,
            ViewOptions options,
            int depth,
            ref int remaining,
            ref int totalReturned,
            ref bool truncatedByCount,
            ref bool truncatedByDepth)
        {
            if (gameObject == null)
                return null;

            remaining--;
            totalReturned++;

            var objectId = UnityApiAdapter.GetObjectId(gameObject);
            var result = new Dictionary<string, object>
            {
                ["name"] = gameObject.name,
                ["objectId"] = objectId,
                ["objectIdString"] = objectId.ToString(CultureInfo.InvariantCulture),
                ["path"] = GetHierarchyPath(gameObject),
                ["scene"] = ToSceneInfo(gameObject.scene),
                ["activeSelf"] = gameObject.activeSelf,
                ["activeInHierarchy"] = gameObject.activeInHierarchy,
                ["childCount"] = gameObject.transform.childCount
            };

            if (options.Detail >= SceneObjectDetailLevel.Standard)
            {
                result["tag"] = SafeTag(gameObject);
                result["layer"] = gameObject.layer;
                result["layerName"] = LayerMask.LayerToName(gameObject.layer);
                result["isStatic"] = gameObject.isStatic;
                result["staticEditorFlags"] = GameObjectUtility.GetStaticEditorFlags(gameObject).ToString();
                result["transform"] = SerializeTransform(gameObject.transform);
                result["components"] = SerializeComponents(gameObject, options);

                if (options.IncludeBounds)
                    result["bounds"] = BuildBounds(gameObject);
                if (options.IncludePrefab)
                    result["prefab"] = BuildPrefabSummary(gameObject);
            }
            else
            {
                result["components"] = gameObject.GetComponents<Component>()
                    .Where(component => component != null)
                    .Select(component => component.GetType().Name)
                    .ToArray();
            }

            if (options.IncludeChildren)
            {
                var children = new List<object>();
                var childCount = gameObject.transform.childCount;
                if (depth < options.MaxDepth)
                {
                    var maxChildren = Math.Min(childCount, options.MaxChildren);
                    for (var i = 0; i < maxChildren; i++)
                    {
                        if (remaining <= 0)
                        {
                            truncatedByCount = true;
                            break;
                        }

                        children.Add(SerializeGameObject(
                            gameObject.transform.GetChild(i).gameObject,
                            options,
                            depth + 1,
                            ref remaining,
                            ref totalReturned,
                            ref truncatedByCount,
                            ref truncatedByDepth));
                    }

                    result["childrenReturned"] = children.Count;
                    result["childrenTruncated"] = childCount > children.Count;
                }
                else
                {
                    result["childrenReturned"] = 0;
                    result["childrenTruncated"] = childCount > 0;
                    if (childCount > 0)
                        truncatedByDepth = true;
                }

                if (children.Count > 0)
                    result["children"] = children;
            }

            return result;
        }

        static object[] SerializeComponents(GameObject gameObject, ViewOptions options)
        {
            return gameObject.GetComponents<Component>()
                .Where(component => component != null)
                .Select(component => SerializeComponent(component, options))
                .ToArray();
        }

        static object SerializeComponent(Component component, ViewOptions options)
        {
            var type = component.GetType();
            var objectId = UnityApiAdapter.GetObjectId(component);
            var result = new Dictionary<string, object>
            {
                ["type"] = type.FullName ?? type.Name,
                ["typeName"] = type.Name,
                ["objectId"] = objectId,
                ["objectIdString"] = objectId.ToString(CultureInfo.InvariantCulture)
            };

            if (component is Behaviour behaviour)
                result["enabled"] = behaviour.enabled;

            if (options.Detail >= SceneObjectDetailLevel.Detailed || options.HasFocusedProfile)
                AddKnownComponentDetails(component, result);

            if (options.IncludeSerializedProperties && ShouldIncludeSerializedProperties(component, options))
                result["serializedProperties"] = SerializeProperties(component, options.MaxSerializedProperties);

            return result;
        }

        static void AddKnownComponentDetails(Component component, Dictionary<string, object> result)
        {
            switch (component)
            {
                case RectTransform rect:
                    result["rectTransform"] = new
                    {
                        anchorMin = SerializeVector2(rect.anchorMin),
                        anchorMax = SerializeVector2(rect.anchorMax),
                        pivot = SerializeVector2(rect.pivot),
                        anchoredPosition = SerializeVector2(rect.anchoredPosition),
                        sizeDelta = SerializeVector2(rect.sizeDelta),
                        rect = SerializeRect(rect.rect)
                    };
                    break;
                case Transform transform:
                    result["transform"] = SerializeTransform(transform);
                    break;
                case SpriteRenderer spriteRenderer:
                    result["spriteRenderer"] = new
                    {
                        sprite = ToObjectReference(spriteRenderer.sprite),
                        color = SerializeColor(spriteRenderer.color),
                        sortingLayerName = spriteRenderer.sortingLayerName,
                        sortingOrder = spriteRenderer.sortingOrder,
                        renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(spriteRenderer),
                        shadowCastingMode = spriteRenderer.shadowCastingMode.ToString(),
                        receiveShadows = spriteRenderer.receiveShadows,
                        lightProbeUsage = spriteRenderer.lightProbeUsage.ToString(),
                        reflectionProbeUsage = spriteRenderer.reflectionProbeUsage.ToString(),
                        motionVectorGenerationMode = spriteRenderer.motionVectorGenerationMode.ToString(),
                        drawMode = spriteRenderer.drawMode.ToString(),
                        bounds = SerializeBounds(spriteRenderer.bounds)
                    };
                    break;
                case ParticleSystemRenderer particleRenderer:
                    result["particleSystemRenderer"] = new
                    {
                        enabled = particleRenderer.enabled,
                        renderMode = particleRenderer.renderMode.ToString(),
                        sortingLayerName = particleRenderer.sortingLayerName,
                        sortingOrder = particleRenderer.sortingOrder,
                        renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(particleRenderer),
                        shadowCastingMode = particleRenderer.shadowCastingMode.ToString(),
                        receiveShadows = particleRenderer.receiveShadows,
                        lightProbeUsage = particleRenderer.lightProbeUsage.ToString(),
                        reflectionProbeUsage = particleRenderer.reflectionProbeUsage.ToString(),
                        motionVectorGenerationMode = particleRenderer.motionVectorGenerationMode.ToString(),
                        sharedMaterial = ToObjectReference(particleRenderer.sharedMaterial),
                        trailMaterial = ToObjectReference(particleRenderer.trailMaterial),
                        bounds = SerializeBounds(particleRenderer.bounds)
                    };
                    break;
                case LineRenderer lineRenderer:
                    result["lineRenderer"] = new
                    {
                        enabled = lineRenderer.enabled,
                        positionCount = lineRenderer.positionCount,
                        loop = lineRenderer.loop,
                        widthMultiplier = lineRenderer.widthMultiplier,
                        textureMode = lineRenderer.textureMode.ToString(),
                        sortingLayerName = lineRenderer.sortingLayerName,
                        sortingOrder = lineRenderer.sortingOrder,
                        renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(lineRenderer),
                        shadowCastingMode = lineRenderer.shadowCastingMode.ToString(),
                        receiveShadows = lineRenderer.receiveShadows,
                        lightProbeUsage = lineRenderer.lightProbeUsage.ToString(),
                        reflectionProbeUsage = lineRenderer.reflectionProbeUsage.ToString(),
                        motionVectorGenerationMode = lineRenderer.motionVectorGenerationMode.ToString(),
                        sharedMaterials = lineRenderer.sharedMaterials.Select(ToObjectReference).ToArray(),
                        bounds = SerializeBounds(lineRenderer.bounds)
                    };
                    break;
                case TrailRenderer trailRenderer:
                    result["trailRenderer"] = new
                    {
                        enabled = trailRenderer.enabled,
                        emitting = trailRenderer.emitting,
                        time = trailRenderer.time,
                        minVertexDistance = trailRenderer.minVertexDistance,
                        widthMultiplier = trailRenderer.widthMultiplier,
                        autodestruct = trailRenderer.autodestruct,
                        sortingLayerName = trailRenderer.sortingLayerName,
                        sortingOrder = trailRenderer.sortingOrder,
                        renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(trailRenderer),
                        shadowCastingMode = trailRenderer.shadowCastingMode.ToString(),
                        receiveShadows = trailRenderer.receiveShadows,
                        lightProbeUsage = trailRenderer.lightProbeUsage.ToString(),
                        reflectionProbeUsage = trailRenderer.reflectionProbeUsage.ToString(),
                        motionVectorGenerationMode = trailRenderer.motionVectorGenerationMode.ToString(),
                        sharedMaterials = trailRenderer.sharedMaterials.Select(ToObjectReference).ToArray(),
                        bounds = SerializeBounds(trailRenderer.bounds)
                    };
                    break;
                case Renderer renderer:
                    result["renderer"] = new
                    {
                        enabled = renderer.enabled,
                        sortingLayerName = renderer.sortingLayerName,
                        sortingOrder = renderer.sortingOrder,
                        renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(renderer),
                        shadowCastingMode = renderer.shadowCastingMode.ToString(),
                        receiveShadows = renderer.receiveShadows,
                        lightProbeUsage = renderer.lightProbeUsage.ToString(),
                        reflectionProbeUsage = renderer.reflectionProbeUsage.ToString(),
                        motionVectorGenerationMode = renderer.motionVectorGenerationMode.ToString(),
                        sharedMaterials = renderer.sharedMaterials.Select(ToObjectReference).ToArray(),
                        bounds = SerializeBounds(renderer.bounds)
                    };
                    break;
                case Camera camera:
                    result["camera"] = new
                    {
                        enabled = camera.enabled,
                        clearFlags = camera.clearFlags.ToString(),
                        backgroundColor = SerializeColor(camera.backgroundColor),
                        orthographic = camera.orthographic,
                        orthographicSize = camera.orthographicSize,
                        fieldOfView = camera.fieldOfView,
                        depth = camera.depth,
                        cullingMask = camera.cullingMask
                    };
                    break;
                case Light light:
                    result["light"] = new
                    {
                        type = light.type.ToString(),
                        color = SerializeColor(light.color),
                        intensity = light.intensity,
                        range = light.range,
                        shadows = light.shadows.ToString(),
                        renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(light)
                    };
                    break;
                case ReflectionProbe reflectionProbe:
                    result["reflectionProbe"] = new
                    {
                        enabled = reflectionProbe.enabled,
                        mode = reflectionProbe.mode.ToString(),
                        importance = reflectionProbe.importance,
                        intensity = reflectionProbe.intensity,
                        boxProjection = reflectionProbe.boxProjection,
                        size = SerializeVector3(reflectionProbe.size),
                        center = SerializeVector3(reflectionProbe.center)
                    };
                    break;
                case LightProbeGroup lightProbeGroup:
                    result["lightProbeGroup"] = new
                    {
                        probeCount = lightProbeGroup.probePositions?.Length ?? 0
                    };
                    break;
                case LightProbeProxyVolume proxyVolume:
                    result["lightProbeProxyVolume"] = new
                    {
                        enabled = proxyVolume.enabled,
                        resolutionMode = proxyVolume.resolutionMode.ToString(),
                        boundingBoxMode = proxyVolume.boundingBoxMode.ToString(),
                        sizeCustom = SerializeVector3(proxyVolume.sizeCustom),
                        originCustom = SerializeVector3(proxyVolume.originCustom)
                    };
                    break;
                case Collider collider:
                    result["collider"] = new
                    {
                        enabled = collider.enabled,
                        isTrigger = collider.isTrigger,
                        bounds = SerializeBounds(collider.bounds)
                    };
                    break;
                case Collider2D collider2D:
                    result["collider2D"] = new
                    {
                        enabled = collider2D.enabled,
                        isTrigger = collider2D.isTrigger,
                        bounds = SerializeBounds(collider2D.bounds)
                    };
                    break;
                case Rigidbody rigidbody:
                    result["rigidbody"] = new
                    {
                        mass = rigidbody.mass,
                        useGravity = rigidbody.useGravity,
                        isKinematic = rigidbody.isKinematic
                    };
                    break;
                case Rigidbody2D rigidbody2D:
                    result["rigidbody2D"] = new
                    {
                        bodyType = rigidbody2D.bodyType.ToString(),
                        mass = rigidbody2D.mass,
                        gravityScale = rigidbody2D.gravityScale
                    };
                    break;
                case Animator animator:
                    result["animator"] = new
                    {
                        enabled = animator.enabled,
                        runtimeAnimatorController = ToObjectReference(animator.runtimeAnimatorController),
                        avatar = ToObjectReference(animator.avatar),
                        applyRootMotion = animator.applyRootMotion,
                        updateMode = animator.updateMode.ToString(),
                        cullingMode = animator.cullingMode.ToString(),
                        layerCount = animator.layerCount,
                        parameterCount = animator.parameterCount,
                        parameters = animator.parameters
                            .Take(24)
                            .Select(parameter => new { parameter.name, type = parameter.type.ToString() })
                            .ToArray()
                    };
                    break;
                case Animation animation:
                    result["animation"] = new
                    {
                        enabled = animation.enabled,
                        clip = ToObjectReference(animation.clip),
                        playAutomatically = animation.playAutomatically,
                        isPlaying = animation.isPlaying,
                        wrapMode = animation.wrapMode.ToString(),
                        animationCount = animation.Cast<AnimationState>().Count()
                    };
                    break;
                case ParticleSystem particleSystem:
                    {
                        var main = particleSystem.main;
                        var emission = particleSystem.emission;
                        var shape = particleSystem.shape;
                        result["particleSystem"] = new
                        {
                            isPlaying = particleSystem.isPlaying,
                            isPaused = particleSystem.isPaused,
                            isStopped = particleSystem.isStopped,
                            particleCount = particleSystem.particleCount,
                            main = new
                            {
                                duration = main.duration,
                                loop = main.loop,
                                startDelay = SerializeMinMaxCurve(main.startDelay),
                                startLifetime = SerializeMinMaxCurve(main.startLifetime),
                                startSpeed = SerializeMinMaxCurve(main.startSpeed),
                                startSize = SerializeMinMaxCurve(main.startSize),
                                startColor = SerializeMinMaxGradient(main.startColor),
                                simulationSpace = main.simulationSpace.ToString(),
                                maxParticles = main.maxParticles
                            },
                            emission = new
                            {
                                enabled = emission.enabled,
                                rateOverTime = SerializeMinMaxCurve(emission.rateOverTime),
                                rateOverDistance = SerializeMinMaxCurve(emission.rateOverDistance)
                            },
                            shape = new
                            {
                                enabled = shape.enabled,
                                shapeType = shape.shapeType.ToString()
                            }
                        };
                    }
                    break;
                case AudioSource audioSource:
                    result["audioSource"] = new
                    {
                        enabled = audioSource.enabled,
                        clip = ToObjectReference(audioSource.clip),
                        outputAudioMixerGroup = ToObjectReference(audioSource.outputAudioMixerGroup),
                        playOnAwake = audioSource.playOnAwake,
                        loop = audioSource.loop,
                        mute = audioSource.mute,
                        volume = audioSource.volume,
                        pitch = audioSource.pitch,
                        spatialBlend = audioSource.spatialBlend,
                        minDistance = audioSource.minDistance,
                        maxDistance = audioSource.maxDistance
                    };
                    break;
                case SortingGroup sortingGroup:
                    result["sortingGroup"] = new
                    {
                        sortingLayerName = sortingGroup.sortingLayerName,
                        sortingLayerID = sortingGroup.sortingLayerID,
                        sortingOrder = sortingGroup.sortingOrder
                    };
                    break;
                case Canvas canvas:
                    result["canvas"] = new
                    {
                        renderMode = canvas.renderMode.ToString(),
                        sortingLayerName = canvas.sortingLayerName,
                        sortingOrder = canvas.sortingOrder,
                        overrideSorting = canvas.overrideSorting,
                        worldCamera = ToObjectReference(canvas.worldCamera)
                    };
                    break;
                case EventSystem eventSystem:
                    result["eventSystem"] = new
                    {
                        enabled = eventSystem.enabled,
                        sendNavigationEvents = eventSystem.sendNavigationEvents,
                        pixelDragThreshold = eventSystem.pixelDragThreshold,
                        currentSelected = ToObjectReference(eventSystem.currentSelectedGameObject),
                        currentInputModule = eventSystem.currentInputModule == null ? null : eventSystem.currentInputModule.GetType().FullName
                    };
                    break;
                case BaseInputModule inputModule:
                    var owningEventSystem = inputModule.GetComponent<EventSystem>();
                    result["inputModule"] = new
                    {
                        enabled = inputModule.enabled,
                        type = inputModule.GetType().FullName,
                        eventSystem = ToObjectReference(owningEventSystem)
                    };
                    break;
                case BaseRaycaster raycaster:
                    result["raycaster"] = new
                    {
                        enabled = raycaster.enabled,
                        type = raycaster.GetType().FullName,
                        eventCamera = ToObjectReference(raycaster.eventCamera),
                        sortOrderPriority = raycaster.sortOrderPriority,
                        renderOrderPriority = raycaster.renderOrderPriority
                    };
                    break;
                case Graphic graphic:
                    result["graphic"] = new
                    {
                        color = SerializeColor(graphic.color),
                        raycastTarget = graphic.raycastTarget,
                        material = ToObjectReference(graphic.material)
                    };
                    break;
                case UIDocument document:
                    result["uiDocument"] = new
                    {
                        visualTreeAsset = ToObjectReference(document.visualTreeAsset),
                        panelSettings = ToObjectReference(document.panelSettings),
                        rootElementName = document.rootVisualElement?.name
                    };
                    break;
            }

            if (component is MonoBehaviour monoBehaviour)
            {
                var script = MonoScript.FromMonoBehaviour(monoBehaviour);
                if (script != null)
                    result["script"] = ToObjectReference(script);
            }

            AddOptionalKnownComponentDetails(component, result);
        }

        static void AddOptionalKnownComponentDetails(Component component, Dictionary<string, object> result)
        {
            var type = component.GetType();
            var fullName = type.FullName ?? type.Name;

            if (string.Equals(fullName, "UnityEngine.VFX.VisualEffect", StringComparison.Ordinal))
            {
                result["visualEffect"] = new
                {
                    visualEffectAsset = ToObjectReference(GetPropertyValue(component, "visualEffectAsset") as Object),
                    initialEventName = GetPropertyValue(component, "initialEventName"),
                    pause = GetPropertyValue(component, "pause"),
                    aliveParticleCount = GetPropertyValue(component, "aliveParticleCount"),
                    culled = GetPropertyValue(component, "culled")
                };
            }
            else if (string.Equals(fullName, "UnityEngine.Playables.PlayableDirector", StringComparison.Ordinal))
            {
                result["playableDirector"] = new
                {
                    playableAsset = ToObjectReference(GetPropertyValue(component, "playableAsset") as Object),
                    playOnAwake = GetPropertyValue(component, "playOnAwake"),
                    extrapolationMode = GetPropertyValue(component, "extrapolationMode")?.ToString(),
                    timeUpdateMode = GetPropertyValue(component, "timeUpdateMode")?.ToString(),
                    state = GetPropertyValue(component, "state")?.ToString(),
                    time = GetPropertyValue(component, "time"),
                    duration = GetPropertyValue(component, "duration")
                };
            }
            else if (fullName.StartsWith("Unity.AI.Navigation.", StringComparison.Ordinal))
            {
                result["aiNavigation"] = new
                {
                    type = fullName,
                    agentTypeID = GetPropertyValue(component, "agentTypeID"),
                    collectObjects = GetPropertyValue(component, "collectObjects")?.ToString(),
                    useGeometry = GetPropertyValue(component, "useGeometry")?.ToString(),
                    defaultArea = GetPropertyValue(component, "defaultArea"),
                    ignoreFromBuild = GetPropertyValue(component, "ignoreFromBuild"),
                    affectedAgents = GetPropertyValue(component, "affectedAgents"),
                    area = GetPropertyValue(component, "area")
                };
            }
            else if (fullName.StartsWith("UnityEngine.AI.", StringComparison.Ordinal))
            {
                result["navigation"] = new
                {
                    type = fullName,
                    enabled = GetPropertyValue(component, "enabled"),
                    agentTypeID = GetPropertyValue(component, "agentTypeID"),
                    radius = GetPropertyValue(component, "radius"),
                    height = GetPropertyValue(component, "height"),
                    speed = GetPropertyValue(component, "speed"),
                    acceleration = GetPropertyValue(component, "acceleration"),
                    angularSpeed = GetPropertyValue(component, "angularSpeed"),
                    stoppingDistance = GetPropertyValue(component, "stoppingDistance"),
                    autoBraking = GetPropertyValue(component, "autoBraking"),
                    autoRepath = GetPropertyValue(component, "autoRepath"),
                    obstacleAvoidanceType = GetPropertyValue(component, "obstacleAvoidanceType")?.ToString(),
                    carving = GetPropertyValue(component, "carving"),
                    areaMask = GetPropertyValue(component, "areaMask"),
                    hasPath = GetPropertyValue(component, "hasPath"),
                    pathPending = GetPropertyValue(component, "pathPending"),
                    remainingDistance = GetPropertyValue(component, "remainingDistance")
                };
            }
            else if (fullName.StartsWith("UnityEngine.InputSystem.", StringComparison.Ordinal))
            {
                result["inputSystem"] = new
                {
                    type = fullName,
                    actions = ToObjectReference(GetPropertyValue(component, "actions") as Object),
                    actionsAsset = ToObjectReference(GetPropertyValue(component, "actionsAsset") as Object),
                    defaultActionMap = GetPropertyValue(component, "defaultActionMap"),
                    currentActionMap = ToObjectReference(GetPropertyValue(component, "currentActionMap") as Object),
                    notificationBehavior = GetPropertyValue(component, "notificationBehavior")?.ToString(),
                    pointerBehavior = GetPropertyValue(component, "pointerBehavior")?.ToString(),
                    moveRepeatDelay = GetPropertyValue(component, "moveRepeatDelay"),
                    moveRepeatRate = GetPropertyValue(component, "moveRepeatRate"),
                    controlPath = GetPropertyValue(component, "controlPath")
                };
            }
            else if (fullName.StartsWith("UnityEngine.Tilemaps.", StringComparison.Ordinal) ||
                     string.Equals(fullName, "UnityEngine.Grid", StringComparison.Ordinal))
            {
                result["tilemap2D"] = new
                {
                    type = fullName,
                    cellSize = SerializeMaybeVector3(GetPropertyValue(component, "cellSize")),
                    cellGap = SerializeMaybeVector3(GetPropertyValue(component, "cellGap")),
                    cellLayout = GetPropertyValue(component, "cellLayout")?.ToString(),
                    cellSwizzle = GetPropertyValue(component, "cellSwizzle")?.ToString(),
                    origin = SerializeMaybeVector3Int(GetPropertyValue(component, "origin")),
                    size = SerializeMaybeVector3Int(GetPropertyValue(component, "size")),
                    tileAnchor = SerializeMaybeVector3(GetPropertyValue(component, "tileAnchor")),
                    color = SerializeMaybeColor(GetPropertyValue(component, "color")),
                    orientation = GetPropertyValue(component, "orientation")?.ToString(),
                    mode = GetPropertyValue(component, "mode")?.ToString(),
                    sortingLayerName = GetPropertyValue(component, "sortingLayerName"),
                    sortingOrder = GetPropertyValue(component, "sortingOrder"),
                    usedByComposite = GetPropertyValue(component, "usedByComposite"),
                    bounds = SerializeMaybeBounds(GetPropertyValue(component, "bounds"))
                };
            }
            else if (fullName.StartsWith("UnityEngine.Rendering.Universal.", StringComparison.Ordinal))
            {
                result["urp"] = new
                {
                    type = fullName,
                    lightType = GetPropertyValue(component, "lightType")?.ToString(),
                    intensity = GetPropertyValue(component, "intensity"),
                    color = SerializeMaybeColor(GetPropertyValue(component, "color")),
                    renderShadows = GetPropertyValue(component, "renderShadows"),
                    priority = GetPropertyValue(component, "priority"),
                    volumeProfile = ToObjectReference(GetPropertyValue(component, "profile") as Object),
                    requiresDepthTexture = GetPropertyValue(component, "requiresDepthTextureOption")?.ToString(),
                    requiresColorTexture = GetPropertyValue(component, "requiresColorTextureOption")?.ToString()
                };
            }
            else if (string.Equals(fullName, "UnityEngine.Rendering.Volume", StringComparison.Ordinal))
            {
                result["volume"] = new
                {
                    isGlobal = GetPropertyValue(component, "isGlobal"),
                    priority = GetPropertyValue(component, "priority"),
                    weight = GetPropertyValue(component, "weight"),
                    blendDistance = GetPropertyValue(component, "blendDistance"),
                    sharedProfile = ToObjectReference(GetPropertyValue(component, "sharedProfile") as Object),
                    profile = ToObjectReference(GetPropertyValue(component, "profile") as Object)
                };
            }
            else if (string.Equals(fullName, "UnityEngine.U2D.SpriteShapeRenderer", StringComparison.Ordinal))
            {
                result["spriteShapeRenderer"] = new
                {
                    enabled = GetPropertyValue(component, "enabled"),
                    sortingLayerName = GetPropertyValue(component, "sortingLayerName"),
                    sortingOrder = GetPropertyValue(component, "sortingOrder"),
                    sharedMaterial = ToObjectReference(GetPropertyValue(component, "sharedMaterial") as Object),
                    bounds = SerializeMaybeBounds(GetPropertyValue(component, "bounds"))
                };
            }
            else if (string.Equals(fullName, "UnityEngine.Video.VideoPlayer", StringComparison.Ordinal))
            {
                result["videoPlayer"] = new
                {
                    enabled = GetPropertyValue(component, "enabled"),
                    playOnAwake = GetPropertyValue(component, "playOnAwake"),
                    source = GetPropertyValue(component, "source")?.ToString(),
                    clip = ToObjectReference(GetPropertyValue(component, "clip") as Object),
                    url = GetPropertyValue(component, "url"),
                    renderMode = GetPropertyValue(component, "renderMode")?.ToString(),
                    targetCamera = ToObjectReference(GetPropertyValue(component, "targetCamera") as Object),
                    targetTexture = ToObjectReference(GetPropertyValue(component, "targetTexture") as Object),
                    isLooping = GetPropertyValue(component, "isLooping"),
                    playbackSpeed = GetPropertyValue(component, "playbackSpeed"),
                    frame = GetPropertyValue(component, "frame"),
                    frameCount = GetPropertyValue(component, "frameCount"),
                    time = GetPropertyValue(component, "time"),
                    length = GetPropertyValue(component, "length")
                };
            }
        }

        static object GetPropertyValue(object target, string propertyName)
        {
            try
            {
                return target?.GetType()
                    .GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    ?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        static object SerializeMaybeColor(object value)
        {
            return value is Color color ? SerializeColor(color) : value;
        }

        static object SerializeMaybeBounds(object value)
        {
            return value is Bounds bounds ? SerializeBounds(bounds) : value;
        }

        static object SerializeMaybeVector3(object value)
        {
            return value is Vector3 vector ? SerializeVector3(vector) : value;
        }

        static object SerializeMaybeVector3Int(object value)
        {
            return value is Vector3Int vector ? SerializeVector3Int(vector) : value;
        }

        static object SerializeMinMaxCurve(ParticleSystem.MinMaxCurve curve)
        {
            return new
            {
                mode = curve.mode.ToString(),
                constant = curve.constant,
                constantMin = curve.constantMin,
                constantMax = curve.constantMax,
                curveMultiplier = curve.curveMultiplier
            };
        }

        static object SerializeMinMaxGradient(ParticleSystem.MinMaxGradient gradient)
        {
            return new
            {
                mode = gradient.mode.ToString(),
                color = SerializeColor(gradient.color),
                colorMin = SerializeColor(gradient.colorMin),
                colorMax = SerializeColor(gradient.colorMax)
            };
        }

        static object SerializeProperties(Object obj, int maxProperties)
        {
            var properties = new List<object>();
            var truncated = false;

            try
            {
                using var serializedObject = new SerializedObject(obj);
                var iterator = serializedObject.GetIterator();
                try
                {
                    if (iterator.NextVisible(true))
                    {
                        do
                        {
                            properties.Add(new
                            {
                                path = iterator.propertyPath,
                                name = iterator.name,
                                displayName = iterator.displayName,
                                type = iterator.propertyType.ToString(),
                                value = SerializePropertyValue(iterator)
                            });

                            if (properties.Count >= maxProperties)
                            {
                                truncated = true;
                                break;
                            }
                        }
                        while (iterator.NextVisible(false));
                    }
                }
                finally
                {
                    iterator.Dispose();
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

        static object SerializePropertyValue(SerializedProperty property)
        {
            try
            {
                return SerializedPropertyPatcher.SerializePropertyValue(property);
            }
            catch (Exception ex)
            {
                return new
                {
                    error = "SerializedProperty value serialization failed.",
                    propertyType = property.propertyType.ToString(),
                    ex.Message
                };
            }
        }

        static bool ShouldIncludeSerializedProperties(Component component, ViewOptions options)
        {
            if (options.IncludeComponentProperties == null || options.IncludeComponentProperties.Length == 0)
                return true;

            return options.IncludeComponentProperties.Any(filter => ComponentMatchesFilter(component, filter));
        }

        static bool ComponentMatchesFilter(Component component, string filter)
        {
            if (component == null || string.IsNullOrWhiteSpace(filter))
                return false;

            filter = filter.Trim();
            var type = component.GetType();
            for (var current = type; current != null; current = current.BaseType)
            {
                if (string.Equals(filter, current.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(filter, current.FullName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return type.GetInterfaces().Any(iface =>
                string.Equals(filter, iface.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filter, iface.FullName, StringComparison.OrdinalIgnoreCase));
        }

        static bool QueryMatches(
            GameObject gameObject,
            string exactName,
            string nameContains,
            Type componentType,
            string componentFilter,
            string tag,
            int? layer,
            bool includeInactive)
        {
            if (gameObject == null)
                return false;

            if (!includeInactive && !gameObject.activeInHierarchy)
                return false;

            if (!string.IsNullOrWhiteSpace(exactName) &&
                !string.Equals(gameObject.name, exactName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(nameContains) &&
                gameObject.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (!string.IsNullOrWhiteSpace(tag) &&
                !string.Equals(SafeTag(gameObject), tag, StringComparison.OrdinalIgnoreCase))
                return false;

            if (layer.HasValue && gameObject.layer != layer.Value)
                return false;

            if (componentType != null && gameObject.GetComponent(componentType) == null)
                return false;

            if (componentType == null && !string.IsNullOrWhiteSpace(componentFilter) &&
                !gameObject.GetComponents<Component>().Any(component => ComponentMatchesFilter(component, componentFilter)))
                return false;

            return true;
        }

        static List<GameObject> ResolveGameObjects(string target, SceneObjectViewParams parameters, ViewOptions options)
        {
            if (string.IsNullOrWhiteSpace(target))
                return new List<GameObject>();

            var method = SceneObjectLocator.NormalizeSearchMethod(parameters.SearchMethod, target);
            return SceneObjectLocator.FindObjects(target, method, new SceneObjectLocator.Options
            {
                IncludeInactive = parameters.IncludeInactive,
                IncludePrefabStage = true,
                IncludeDontDestroyOnLoad = parameters.IncludeDontDestroyOnLoad,
                ScenePath = parameters.ScenePath
            });
        }

        static string PrintHierarchy(List<Transform> rootTransforms, int maxLines, int maxChildren, SceneObjectIndexDisplayMode indexDisplayMode)
        {
            if (rootTransforms == null || rootTransforms.Count == 0 || maxLines <= 0)
                return "<Empty Scene>";

            var selected = SelectNodesBfs(rootTransforms, maxChildren, maxLines);
            var allowed = new HashSet<Transform>(selected.Select(info => info.Transform));
            var map = selected.ToDictionary(info => info.Transform, info => info);
            var builder = new StringBuilder();
            var lines = 0;

            foreach (var root in rootTransforms)
            {
                if (root != null && allowed.Contains(root))
                    PrintNode(root, builder, allowed, map, ref lines, maxLines, maxChildren, indexDisplayMode);
            }

            return builder.Length == 0 ? "<Empty Scene>" : builder.ToString();
        }

        static List<NodePrintInfo> SelectNodesBfs(List<Transform> roots, int maxChildren, int maxLines)
        {
            var result = new List<NodePrintInfo>();
            var visited = new HashSet<Transform>();
            var duplicatePathIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<(Transform transform, int depth, string path)>();

            foreach (var root in roots)
            {
                if (root == null || !visited.Add(root))
                    continue;

                var path = "/" + root.name;
                var duplicateIndex = NextDuplicateIndex(duplicatePathIndexes, path);
                result.Add(new NodePrintInfo(root, 0, root.childCount, duplicateIndex));

                var count = Math.Min(root.childCount, maxChildren);
                for (var i = 0; i < count; i++)
                {
                    var child = root.GetChild(i);
                    if (child != null)
                        queue.Enqueue((child, 1, path + "/" + child.name));
                }
            }

            while (queue.Count > 0 && result.Count < maxLines)
            {
                var (transform, depth, path) = queue.Dequeue();
                if (transform == null || !visited.Add(transform))
                    continue;

                var duplicateIndex = NextDuplicateIndex(duplicatePathIndexes, path);
                result.Add(new NodePrintInfo(transform, depth, transform.childCount, duplicateIndex));

                var count = Math.Min(transform.childCount, maxChildren);
                for (var i = 0; i < count && result.Count + queue.Count < maxLines + maxChildren; i++)
                {
                    var child = transform.GetChild(i);
                    if (child != null && !visited.Contains(child))
                        queue.Enqueue((child, depth + 1, path + "/" + child.name));
                }
            }

            return result;
        }

        static int NextDuplicateIndex(Dictionary<string, int> indexes, string path)
        {
            if (!indexes.TryGetValue(path, out var current))
            {
                indexes[path] = 0;
                return 0;
            }

            current++;
            indexes[path] = current;
            return current;
        }

        static void PrintNode(
            Transform node,
            StringBuilder builder,
            HashSet<Transform> allowed,
            Dictionary<Transform, NodePrintInfo> map,
            ref int lines,
            int maxLines,
            int maxChildren,
            SceneObjectIndexDisplayMode indexDisplayMode)
        {
            if (node == null || lines >= maxLines || !map.TryGetValue(node, out var info))
                return;

            if (indexDisplayMode == SceneObjectIndexDisplayMode.MetadataColumn)
            {
                builder.Append(info.DuplicateIndex.ToString().PadLeft(4));
                builder.Append(' ');
            }

            builder.Append(' ', info.Depth * 2);
            builder.Append('/');
            builder.AppendLine(node.name);
            lines++;

            var printedChildren = 0;
            var maxVisibleChildren = Math.Min(info.ActualChildCount, maxChildren);
            for (var i = 0; i < maxVisibleChildren && lines < maxLines; i++)
            {
                var child = node.GetChild(i);
                if (child != null && allowed.Contains(child))
                {
                    var before = lines;
                    PrintNode(child, builder, allowed, map, ref lines, maxLines, maxChildren, indexDisplayMode);
                    if (lines > before)
                        printedChildren++;
                }
            }

            var hiddenChildren = info.ActualChildCount - printedChildren;
            if (hiddenChildren > 0 && (lines < maxLines || info.Depth == 0))
            {
                if (indexDisplayMode == SceneObjectIndexDisplayMode.MetadataColumn)
                    builder.Append(' ', 5);
                builder.Append(' ', (info.Depth + 1) * 2);
                builder.AppendLine($"<+{hiddenChildren} children>");
                if (lines < maxLines)
                    lines++;
            }
        }

        static List<Scene> ResolveScenes(string scenePath, bool includeDontDestroyOnLoad)
        {
            var scenes = LoadedScenes(includeDontDestroyOnLoad);
            if (string.IsNullOrWhiteSpace(scenePath))
                return scenes;

            var normalized = scenePath.Replace('\\', '/').TrimStart('/');
            return scenes
                .Where(scene =>
                    DontDestroyOnLoadSceneCache.Matches(scene, scenePath) ||
                    string.Equals(scene.name, scenePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(scene.path, normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        static List<Scene> LoadedScenes(bool includeDontDestroyOnLoad)
        {
            return SceneObjectLocator.GetLoadedScenes(includeDontDestroyOnLoad: includeDontDestroyOnLoad);
        }

        static IEnumerable<GameObject> GetSceneObjects(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null)
                    continue;
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    yield return transform.gameObject;
            }
        }

        static object ToSceneInfo(Scene scene)
        {
            if (!scene.IsValid())
                return new { valid = false };

            return new
            {
                valid = true,
                name = scene.name,
                path = scene.path,
                isDontDestroyOnLoad = DontDestroyOnLoadSceneCache.IsDontDestroyOnLoadScene(scene),
                buildIndex = scene.buildIndex,
                isDirty = scene.isDirty,
                rootCount = scene.isLoaded ? scene.rootCount : 0
            };
        }

        static object SerializeTransform(Transform transform)
        {
            return new
            {
                localPosition = SerializeVector3(transform.localPosition),
                localRotationEuler = SerializeVector3(transform.localEulerAngles),
                localScale = SerializeVector3(transform.localScale),
                worldPosition = SerializeVector3(transform.position),
                worldRotationEuler = SerializeVector3(transform.eulerAngles),
                lossyScale = SerializeVector3(transform.lossyScale)
            };
        }

        static object BuildBounds(GameObject gameObject)
        {
            var hasBounds = false;
            var bounds = new Bounds(gameObject.transform.position, Vector3.zero);

            foreach (var renderer in gameObject.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                    continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                    bounds.Encapsulate(renderer.bounds);
            }

            foreach (var collider in gameObject.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null)
                    continue;
                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                    bounds.Encapsulate(collider.bounds);
            }

            foreach (var collider in gameObject.GetComponentsInChildren<Collider2D>(true))
            {
                if (collider == null)
                    continue;
                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                    bounds.Encapsulate(collider.bounds);
            }

            return hasBounds ? SerializeBounds(bounds) : null;
        }

        static object BuildPrefabSummary(GameObject gameObject)
        {
            try
            {
                var assetType = PrefabUtility.GetPrefabAssetType(gameObject);
                var instanceStatus = PrefabUtility.GetPrefabInstanceStatus(gameObject);
                var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
                var nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);

                var nearestRootId = UnityApiAdapter.GetObjectId(nearestRoot);
                return new
                {
                    assetType = assetType.ToString(),
                    instanceStatus = instanceStatus.ToString(),
                    sourcePath = string.IsNullOrWhiteSpace(sourcePath) ? null : sourcePath,
                    nearestInstanceRoot = nearestRoot != null ? new
                    {
                        name = nearestRoot.name,
                        objectId = nearestRootId,
                        objectIdString = nearestRootId.ToString(CultureInfo.InvariantCulture),
                        path = GetHierarchyPath(nearestRoot)
                    } : null,
                    isAnyPrefabInstanceRoot = PrefabUtility.IsAnyPrefabInstanceRoot(gameObject)
                };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        static object ToObjectReference(Object obj)
        {
            if (obj == null)
                return null;

            var path = AssetDatabase.GetAssetPath(obj);
            var objectId = UnityApiAdapter.GetObjectId(obj);
            return new
            {
                name = obj.name,
                type = obj.GetType().FullName,
                objectId,
                objectIdString = objectId.ToString(CultureInfo.InvariantCulture),
                assetPath = string.IsNullOrWhiteSpace(path) ? null : path,
                guid = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.AssetPathToGUID(path)
            };
        }

        static string GetHierarchyPath(GameObject gameObject)
        {
            if (gameObject == null)
                return null;

            var parts = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return "/" + string.Join("/", parts);
        }

        static string SafeTag(GameObject gameObject)
        {
            try { return gameObject.tag; }
            catch { return "Untagged"; }
        }

        static object SerializeVector2(Vector2 value) => new { x = value.x, y = value.y };
        static object SerializeVector3(Vector3 value) => new { x = value.x, y = value.y, z = value.z };
        static object SerializeVector4(Vector4 value) => new { x = value.x, y = value.y, z = value.z, w = value.w };
        static object SerializeVector2Int(Vector2Int value) => new { x = value.x, y = value.y };
        static object SerializeVector3Int(Vector3Int value) => new { x = value.x, y = value.y, z = value.z };
        static object SerializeQuaternion(Quaternion value) => new { x = value.x, y = value.y, z = value.z, w = value.w };
        static object SerializeColor(Color value) => new { r = value.r, g = value.g, b = value.b, a = value.a };
        static object SerializeRect(Rect value) => new { x = value.x, y = value.y, width = value.width, height = value.height, xMin = value.xMin, yMin = value.yMin, xMax = value.xMax, yMax = value.yMax };
        static object SerializeRectInt(RectInt value) => new { x = value.x, y = value.y, width = value.width, height = value.height, xMin = value.xMin, yMin = value.yMin, xMax = value.xMax, yMax = value.yMax };
        static object SerializeBounds(Bounds value) => new { center = SerializeVector3(value.center), size = SerializeVector3(value.size), extents = SerializeVector3(value.extents), min = SerializeVector3(value.min), max = SerializeVector3(value.max) };
        static object SerializeBoundsInt(BoundsInt value) => new { position = SerializeVector3Int(value.position), size = SerializeVector3Int(value.size), min = SerializeVector3Int(value.min), max = SerializeVector3Int(value.max) };

        sealed class NodePrintInfo
        {
            public Transform Transform { get; }
            public int Depth { get; }
            public int ActualChildCount { get; }
            public int DuplicateIndex { get; }

            public NodePrintInfo(Transform transform, int depth, int actualChildCount, int duplicateIndex)
            {
                Transform = transform;
                Depth = depth;
                ActualChildCount = actualChildCount;
                DuplicateIndex = duplicateIndex;
            }
        }

        sealed class ViewOptions
        {
            public SceneObjectDetailLevel Detail;
            public SceneObjectViewProfile Profile;
            public int MaxDepth;
            public int MaxObjects;
            public int MaxChildren;
            public int MaxRoots;
            public int MaxSerializedProperties;
            public bool IncludeChildren;
            public bool IncludeBounds;
            public bool IncludePrefab;
            public bool IncludeSerializedProperties;
            public string[] IncludeComponentProperties;
            public bool HasFocusedProfile => Profile != SceneObjectViewProfile.Auto && Profile != SceneObjectViewProfile.Basic;

            public static ViewOptions From(SceneObjectViewParams p)
            {
                var defaults = DefaultsFor(p.Detail);
                var profileComponents = ComponentsForProfile(p.Profile);
                var requestedComponents = p.IncludeComponentProperties ?? Array.Empty<string>();
                var includeComponentProperties = requestedComponents
                    .Concat(profileComponents)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var hasFocusedProfile = p.Profile != SceneObjectViewProfile.Auto && p.Profile != SceneObjectViewProfile.Basic;

                return new ViewOptions
                {
                    Detail = p.Detail,
                    Profile = p.Profile,
                    MaxDepth = Clamp(p.MaxDepth ?? defaults.depth, 0, MaxDepthLimit),
                    MaxObjects = Clamp(p.MaxObjects ?? defaults.objects, 1, MaxObjectsLimit),
                    MaxChildren = Clamp(p.MaxChildren ?? 50, 0, MaxChildrenLimit),
                    MaxRoots = Clamp(p.MaxRoots ?? 200, 1, MaxRootsLimit),
                    MaxSerializedProperties = Clamp(p.MaxSerializedProperties ?? 80, 1, MaxSerializedPropertiesLimit),
                    IncludeChildren = p.IncludeChildren ?? true,
                    IncludeBounds = p.IncludeBounds,
                    IncludePrefab = p.IncludePrefab,
                    IncludeSerializedProperties = p.IncludeSerializedProperties ?? (p.Detail == SceneObjectDetailLevel.Full || hasFocusedProfile),
                    IncludeComponentProperties = includeComponentProperties
                };
            }

            public object ToLimits() => new
            {
                profile = Profile.ToString(),
                maxDepth = MaxDepth,
                maxObjects = MaxObjects,
                maxChildren = MaxChildren,
                maxRoots = MaxRoots,
                maxSerializedProperties = MaxSerializedProperties,
                includeChildren = IncludeChildren,
                includeSerializedProperties = IncludeSerializedProperties,
                includeComponentProperties = IncludeComponentProperties
            };

            static string[] ComponentsForProfile(SceneObjectViewProfile profile)
            {
                return profile switch
                {
                    SceneObjectViewProfile.Rendering => new[]
                    {
                        "Transform", "RectTransform", "Renderer", "SpriteRenderer", "MeshRenderer",
                        "SkinnedMeshRenderer", "ParticleSystemRenderer", "LineRenderer", "TrailRenderer",
                        "SortingGroup", "Camera", "Light", "DecalProjector", "LensFlareComponentSRP",
                        "LensFlare", "FlareLayer", "WindZone", "Projector", "LODGroup",
                        "ReflectionProbe", "LightProbeGroup", "LightProbeProxyVolume"
                    },
                    SceneObjectViewProfile.Physics2D => new[]
                    {
                        "Transform", "Rigidbody2D", "Collider2D", "Joint2D", "CompositeCollider2D", "Effector2D"
                    },
                    SceneObjectViewProfile.Physics3D => new[]
                    {
                        "Transform", "Rigidbody", "Collider", "Joint", "CharacterController"
                    },
                    SceneObjectViewProfile.UI => new[]
                    {
                        "RectTransform", "Canvas", "CanvasScaler", "GraphicRaycaster", "Graphic", "Image",
                        "RawImage", "Text", "Button", "Selectable", "ScrollRect", "TextMeshProUGUI", "TMP_Text"
                    },
                    SceneObjectViewProfile.Animation => new[]
                    {
                        "Transform", "Animator", "Animation", "PlayableDirector", "SpriteRenderer", "SkinnedMeshRenderer"
                    },
                    SceneObjectViewProfile.VFX => new[]
                    {
                        "Transform", "ParticleSystem", "ParticleSystemRenderer", "VisualEffect", "TrailRenderer", "LineRenderer", "Renderer"
                    },
                    SceneObjectViewProfile.Audio => new[]
                    {
                        "Transform", "AudioSource", "AudioListener", "AudioReverbZone", "AudioChorusFilter",
                        "AudioEchoFilter", "AudioLowPassFilter", "AudioHighPassFilter", "AudioDistortionFilter"
                    },
                    SceneObjectViewProfile.Gameplay => new[]
                    {
                        "Transform", "MonoBehaviour", "Animator", "Rigidbody", "Rigidbody2D", "Collider", "Collider2D", "AudioSource"
                    },
                    SceneObjectViewProfile.Navigation => new[]
                    {
                        "Transform", "NavMeshAgent", "NavMeshObstacle", "OffMeshLink", "NavMeshSurface",
                        "NavMeshLink", "NavMeshModifier", "NavMeshModifierVolume"
                    },
                    SceneObjectViewProfile.Input => new[]
                    {
                        "EventSystem", "BaseInputModule", "StandaloneInputModule", "InputSystemUIInputModule",
                        "PlayerInput", "PlayerInputManager", "OnScreenButton", "OnScreenStick", "VirtualMouseInput",
                        "TrackedPoseDriver", "TrackedDeviceRaycaster", "BaseRaycaster", "GraphicRaycaster"
                    },
                    SceneObjectViewProfile.Tilemap2D => new[]
                    {
                        "Transform", "Grid", "Tilemap", "TilemapRenderer", "TilemapCollider2D",
                        "CompositeCollider2D", "SpriteShapeRenderer", "Light2D", "ShadowCaster2D", "PixelPerfectCamera"
                    },
                    SceneObjectViewProfile.Lighting => new[]
                    {
                        "Transform", "Light", "ReflectionProbe", "LightProbeGroup", "LightProbeProxyVolume",
                        "Volume", "Light2D", "UniversalAdditionalCameraData", "UniversalAdditionalLightData",
                        "DecalProjector", "LensFlareComponentSRP", "LensFlare", "FlareLayer", "WindZone", "Projector", "LODGroup"
                    },
                    SceneObjectViewProfile.VideoTimeline => new[]
                    {
                        "Transform", "PlayableDirector", "VideoPlayer", "Animator", "Animation", "AudioSource", "Renderer"
                    },
                    _ => Array.Empty<string>()
                };
            }

            static (int depth, int objects) DefaultsFor(SceneObjectDetailLevel detail)
            {
                return detail switch
                {
                    SceneObjectDetailLevel.Brief => (3, 300),
                    SceneObjectDetailLevel.Standard => (3, 160),
                    SceneObjectDetailLevel.Detailed => (2, 80),
                    SceneObjectDetailLevel.Full => (1, 24),
                    _ => (3, 160)
                };
            }

            static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
        }
    }
}
#pragma warning restore 0618
