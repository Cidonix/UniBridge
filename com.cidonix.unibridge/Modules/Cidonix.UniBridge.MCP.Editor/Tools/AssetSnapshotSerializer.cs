using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read-only AI-friendly asset serialization used by UniBridge_AssetIntelligence Serialize/Snapshot.
    /// </summary>
    internal static class AssetSnapshotSerializer
    {
        const int DefaultTextLimit = 60000;
        const int MinimalTextLimit = 12000;
        const int MaxTextLimit = 200000;
        const int DefaultPropertyLimit = 250;
        const int MaxPropertyLimit = 2000;
        const int DefaultHierarchyDepth = 5;
        const int MaxHierarchyDepth = 12;
        const int DefaultItemLimit = 200;
        const int MaxItemLimit = 2000;

        static readonly HashSet<string> k_TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".asset", ".asmdef", ".asmref", ".cginc", ".compute", ".controller", ".css", ".csv",
            ".hlsl", ".html", ".inputactions", ".json", ".mat", ".md", ".meta",
            ".prefab", ".shader", ".spriteatlas", ".txt", ".uss", ".uxml", ".xml",
            ".yaml", ".yml", ".unity", ".cs", ".js"
        };

        public static List<object> SerializeAssets(IEnumerable<string> assetPaths, AssetIntelligenceParams parameters, string action)
        {
            var p = parameters ?? new AssetIntelligenceParams();
            var mode = p.SerializeMode;
            return assetPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => SerializeAsset(path, p, mode, action))
                .ToList();
        }

        static object SerializeAsset(string assetPath, AssetIntelligenceParams p, AssetSerializationMode mode, string action)
        {
            assetPath = NormalizeAssetPath(assetPath);
            var metadata = BuildMetadata(assetPath);
            var limits = BuildLimits(p, mode);

            try
            {
                if (AssetDatabase.IsValidFolder(assetPath))
                {
                    return CreateEnvelope(assetPath, metadata, mode, "json", new
                    {
                        assetType = "Directory",
                        children = ListFolderChildren(assetPath, limits.ItemLimit)
                    }, limits, action);
                }

                var absolutePath = AssetPathToAbsolutePath(assetPath);
                if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                {
                    return CreateEnvelope(assetPath, metadata, mode, "error", new
                    {
                        assetType = "DeletedAsset",
                        message = "The asset file could not be found on disk.",
                        assetPath
                    }, limits, action);
                }

                var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                var mainType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                var importer = AssetImporter.GetAtPath(assetPath);
                var serialized = SerializeAssetContent(assetPath, absolutePath, mainAsset, mainType, importer, p, mode, limits, out var contentType, out var syntax, out var interfaceSummary);

                return CreateEnvelope(assetPath, metadata, mode, contentType, serialized, limits, action, syntax, interfaceSummary);
            }
            catch (Exception ex)
            {
                return CreateEnvelope(assetPath, metadata, mode, "error", new
                {
                    assetType = metadata.Type,
                    message = "Failed to serialize asset.",
                    exception = ex.GetType().FullName,
                    ex.Message
                }, limits, action);
            }
        }

        static object SerializeAssetContent(
            string assetPath,
            string absolutePath,
            Object mainAsset,
            Type mainType,
            AssetImporter importer,
            AssetIntelligenceParams p,
            AssetSerializationMode mode,
            SerializationLimits limits,
            out string contentType,
            out string syntax,
            out string interfaceSummary)
        {
            syntax = null;
            interfaceSummary = null;

            if (mainAsset is MonoScript script)
            {
                contentType = "text";
                syntax = "csharp";
                interfaceSummary = BuildScriptInterfaceSummary(script);
                return ReadText(absolutePath, limits.TextLimit);
            }

            if (mainAsset is SceneAsset)
            {
                var activeScene = SceneManager.GetActiveScene();
                if (p.IncludeHierarchy &&
                    activeScene.IsValid() &&
                    string.Equals(activeScene.path, assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    contentType = "json";
                    return SerializeSceneHierarchy(activeScene, p, limits);
                }

                contentType = "text";
                syntax = "yaml";
                return ReadText(absolutePath, limits.TextLimit);
            }

            if (IsPrefabAsset(assetPath, importer, mainAsset))
            {
                if (p.IncludeHierarchy && mode != AssetSerializationMode.Minimal)
                {
                    contentType = "json";
                    return SerializePrefabHierarchy(assetPath, p, limits);
                }

                contentType = "text";
                syntax = "yaml";
                return ReadText(absolutePath, limits.TextLimit);
            }

            if (IsTextLike(assetPath, mainAsset) && p.IncludeRawText)
            {
                var profile = BuildSmartProfile(mainAsset, importer, assetPath, limits);
                if (profile != null)
                {
                    contentType = "json";
                    syntax = GuessSyntax(assetPath);
                    return new
                    {
                        profile,
                        text = ReadText(absolutePath, limits.TextLimit)
                    };
                }

                contentType = "text";
                syntax = GuessSyntax(assetPath);
                return ReadText(absolutePath, limits.TextLimit);
            }

            contentType = "json";
            return SerializeUnityAsset(assetPath, mainAsset, importer, p, mode, limits);
        }

        static object SerializeUnityAsset(
            string assetPath,
            Object mainAsset,
            AssetImporter importer,
            AssetIntelligenceParams p,
            AssetSerializationMode mode,
            SerializationLimits limits)
        {
            var result = new Dictionary<string, object>
            {
                ["assetPath"] = assetPath,
                ["mainAsset"] = mainAsset != null ? SerializeObjectEnvelope(mainAsset, p.IncludeSerializedProperties, limits) : null,
                ["importer"] = importer != null ? SerializeObjectEnvelope(importer, p.IncludeSerializedProperties, limits) : null,
                ["profile"] = BuildSmartProfile(mainAsset, importer, assetPath, limits)
            };

            if (mainAsset is Texture texture)
            {
                result["texture"] = new
                {
                    texture.width,
                    texture.height,
                    dimension = texture.dimension.ToString(),
                    runtimeMemoryBytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture)
                };
            }
            else if (mainAsset is AudioClip clip)
            {
                result["audioClip"] = new
                {
                    clip.length,
                    clip.samples,
                    clip.channels,
                    clip.frequency
                };
            }
            else if (mainAsset is Material material)
            {
                result["material"] = SerializeMaterial(material, limits);
            }

            if (mode == AssetSerializationMode.Full || p.IncludeSubAssets)
            {
                var subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                    .Where(asset => asset != null && asset != mainAsset)
                    .Take(limits.ItemLimit)
                    .Select(asset => SerializeObjectEnvelope(asset, p.IncludeSerializedProperties && mode != AssetSerializationMode.Minimal, limits))
                    .ToArray();

                result["subAssets"] = subAssets;
                result["subAssetsTruncated"] = subAssets.Length >= limits.ItemLimit;
            }

            return result;
        }

        static object SerializePrefabHierarchy(string assetPath, AssetIntelligenceParams p, SerializationLimits limits)
        {
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(assetPath);
                return new
                {
                    assetPath,
                    prefabAssetType = PrefabUtility.GetPrefabAssetType(root).ToString(),
                    root = SerializeGameObject(root, p, limits, 0, new Counter())
                };
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static object SerializeSceneHierarchy(Scene scene, AssetIntelligenceParams p, SerializationLimits limits)
        {
            var counter = new Counter();
            var roots = scene.GetRootGameObjects()
                .Take(limits.ItemLimit)
                .Select(root => SerializeGameObject(root, p, limits, 0, counter))
                .ToArray();

            return new
            {
                scene.name,
                scene.path,
                scene.isLoaded,
                scene.isDirty,
                rootCount = scene.rootCount,
                roots,
                truncated = counter.Count >= limits.ItemLimit
            };
        }

        static object SerializeGameObject(GameObject gameObject, AssetIntelligenceParams p, SerializationLimits limits, int depth, Counter counter)
        {
            if (gameObject == null)
                return null;

            counter.Count++;
            var transform = gameObject.transform;
            var components = gameObject.GetComponents<Component>()
                .Where(component => component != null)
                .Take(limits.ItemLimit)
                .Select(component => SerializeComponent(component, p, limits))
                .ToArray();

            object[] children = Array.Empty<object>();
            var childrenTruncated = false;
            if (depth < limits.HierarchyDepth && counter.Count < limits.ItemLimit)
            {
                var items = new List<object>();
                for (var i = 0; i < transform.childCount && counter.Count < limits.ItemLimit; i++)
                    items.Add(SerializeGameObject(transform.GetChild(i).gameObject, p, limits, depth + 1, counter));

                children = items.ToArray();
                childrenTruncated = transform.childCount > children.Length;
            }
            else
            {
                childrenTruncated = transform.childCount > 0;
            }

            return new
            {
                name = gameObject.name,
                path = GetTransformPath(transform),
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                tag = gameObject.tag,
                layer = LayerMask.LayerToName(gameObject.layer),
                entityId = UnityApiAdapter.GetObjectId(gameObject),
                transform = new
                {
                    localPosition = SerializeVector3(transform.localPosition),
                    localRotationEuler = SerializeVector3(transform.localEulerAngles),
                    localScale = SerializeVector3(transform.localScale)
                },
                componentCount = components.Length,
                components,
                children,
                childrenTruncated
            };
        }

        static object SerializeComponent(Component component, AssetIntelligenceParams p, SerializationLimits limits)
        {
            var result = new Dictionary<string, object>
            {
                ["type"] = component.GetType().FullName ?? component.GetType().Name,
                ["typeName"] = component.GetType().Name,
                ["entityId"] = UnityApiAdapter.GetObjectId(component)
            };

            if (component is Behaviour behaviour)
                result["enabled"] = behaviour.enabled;
            if (component is Renderer renderer)
            {
                result["bounds"] = SerializeBounds(renderer.bounds);
                result["renderingLayerMask"] = RenderingLayerUtility.SerializeRenderingLayerMask(renderer);
            }
            if (component is Collider collider)
                result["bounds"] = SerializeBounds(collider.bounds);
            if (component is Collider2D collider2D)
                result["bounds"] = SerializeBounds(collider2D.bounds);

            if (p.IncludeSerializedProperties)
                result["serializedProperties"] = SerializeProperties(component, limits);
            result["profile"] = BuildSmartProfile(component, null, null, limits);

            return result;
        }

        static object SerializeObjectEnvelope(Object obj, bool includeProperties, SerializationLimits limits)
        {
            if (obj == null)
                return null;

            var result = new Dictionary<string, object>
            {
                ["name"] = obj.name,
                ["type"] = obj.GetType().FullName ?? obj.GetType().Name,
                ["typeName"] = obj.GetType().Name,
                ["entityId"] = UnityApiAdapter.GetObjectId(obj),
                ["assetPath"] = NormalizePath(AssetDatabase.GetAssetPath(obj)),
                ["profile"] = BuildSmartProfile(obj, null, AssetDatabase.GetAssetPath(obj), limits)
            };

            if (includeProperties)
                result["serializedProperties"] = SerializeProperties(obj, limits);

            return result;
        }

        static object SerializeProperties(Object obj, SerializationLimits limits)
        {
            if (obj == null)
                return null;

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

                            if (properties.Count >= limits.PropertyLimit)
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
                return property.propertyType switch
                {
                    SerializedPropertyType.Generic => property.isArray
                        ? SerializeArrayProperty(property)
                        : SerializeGenericProperty(property),
                    SerializedPropertyType.ManagedReference => new
                    {
                        managedReference = true,
                        fieldType = property.managedReferenceFieldTypename,
                        type = property.managedReferenceFullTypename,
                        hasValue = property.managedReferenceValue != null,
                        id = property.managedReferenceId,
                        data = property.managedReferenceValue != null ? SerializeGenericProperty(property) : null
                    },
                    _ => SerializedPropertyPatcher.SerializePropertyValue(property)
                };
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

        static object SerializeArrayProperty(SerializedProperty property)
        {
            const int inlineLimit = 12;
            var count = property.arraySize;
            var items = new List<object>();
            var limit = Math.Min(count, inlineLimit);
            for (var i = 0; i < limit; i++)
            {
                try
                {
                    var element = property.GetArrayElementAtIndex(i);
                    items.Add(new
                    {
                        index = i,
                        path = element.propertyPath,
                        type = element.propertyType.ToString(),
                        value = SerializePropertyValue(element)
                    });
                }
                catch
                {
                    items.Add(new { index = i, error = "Failed to serialize array element." });
                }
            }

            return new
            {
                array = true,
                arraySize = count,
                returned = items.Count,
                truncated = count > items.Count,
                items
            };
        }

        static object SerializeGenericProperty(SerializedProperty property)
        {
            const int childLimit = 24;
            var children = new List<object>();
            var copy = property.Copy();
            var end = copy.GetEndProperty();
            var enterChildren = true;

            while (copy.NextVisible(enterChildren) && !SerializedProperty.EqualContents(copy, end))
            {
                enterChildren = false;
                children.Add(new
                {
                    path = copy.propertyPath,
                    name = copy.name,
                    displayName = copy.displayName,
                    type = copy.propertyType.ToString(),
                    value = copy.propertyType == SerializedPropertyType.Generic
                        ? new { generic = true, copy.hasVisibleChildren }
                        : SerializePropertyValue(copy)
                });

                if (children.Count >= childLimit)
                    break;
            }

            return new
            {
                generic = true,
                managedReference = property.propertyType == SerializedPropertyType.ManagedReference,
                returned = children.Count,
                truncated = children.Count >= childLimit,
                children
            };
        }

        static object BuildSmartProfile(Object obj, AssetImporter importer, string assetPath, SerializationLimits limits)
        {
            if (obj == null && importer == null)
                return null;

            try
            {
                if (obj is AnimationClip clip)
                {
                    return new
                    {
                        profile = "animationClip",
                        clip.length,
                        clip.frameRate,
                        clip.wrapMode,
                        clip.empty,
                        legacy = clip.legacy,
                        events = AnimationUtility.GetAnimationEvents(clip).Length,
                        curveBindings = AnimationUtility.GetCurveBindings(clip)
                            .Take(limits.ItemLimit)
                            .Select(binding => new
                            {
                                binding.path,
                                binding.propertyName,
                                type = binding.type?.FullName
                            })
                            .ToArray(),
                        objectReferenceCurveBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip).Length
                    };
                }

                if (obj is RenderTexture renderTexture)
                {
                    return new
                    {
                        profile = "renderTexture",
                        renderTexture.width,
                        renderTexture.height,
                        renderTexture.depth,
                        dimension = renderTexture.dimension.ToString(),
                        graphicsFormat = renderTexture.graphicsFormat.ToString(),
                        colorFormat = renderTexture.format.ToString(),
                        renderTexture.volumeDepth,
                        renderTexture.antiAliasing,
                        renderTexture.useMipMap,
                        renderTexture.autoGenerateMips,
                        renderTexture.enableRandomWrite,
                        renderTexture.useDynamicScale,
                        filterMode = renderTexture.filterMode.ToString(),
                        wrapMode = renderTexture.wrapMode.ToString()
                    };
                }

                if (obj is Material material)
                {
                    return new
                    {
                        profile = "material",
                        shader = material.shader != null ? material.shader.name : null,
                        shaderPath = material.shader != null ? NormalizePath(AssetDatabase.GetAssetPath(material.shader)) : null,
                        material.renderQueue,
                        material.enableInstancing,
                        textureProperties = material.GetTexturePropertyNames()
                            .Take(Math.Min(limits.ItemLimit, 40))
                            .Select(name => new
                            {
                                name,
                                texture = SerializeObjectReference(material.GetTexture(name)),
                                scale = SerializeVector2(material.GetTextureScale(name)),
                                offset = SerializeVector2(material.GetTextureOffset(name))
                            })
                            .ToArray()
                    };
                }

                if (obj is Texture texture)
                {
                    return new
                    {
                        profile = obj is Sprite ? "spriteTexture" : "texture",
                        texture.width,
                        texture.height,
                        dimension = texture.dimension.ToString(),
                        importer = importer == null ? null : new
                        {
                            type = importer.GetType().FullName,
                            path = NormalizePath(importer.assetPath)
                        }
                    };
                }

                if (obj is Sprite sprite)
                {
                    return new
                    {
                        profile = "sprite",
                        rect = SerializeRect(sprite.rect),
                        pivot = SerializeVector2(sprite.pivot),
                        border = SerializeVector4(sprite.border),
                        pixelsPerUnit = sprite.pixelsPerUnit,
                        texture = SerializeObjectReference(sprite.texture)
                    };
                }

                if (obj is AudioClip audio)
                {
                    return new
                    {
                        profile = "audioClip",
                        audio.length,
                        audio.samples,
                        audio.channels,
                        audio.frequency,
                        loadType = importer is AudioImporter audioImporter ? audioImporter.defaultSampleSettings.loadType.ToString() : null
                    };
                }

                if (obj is ParticleSystem particles)
                {
                    return new
                    {
                        profile = "particleSystem",
                        mainDuration = particles.main.duration,
                        mainLoop = particles.main.loop,
                        startLifetime = particles.main.startLifetime.mode.ToString(),
                        startSpeed = particles.main.startSpeed.mode.ToString(),
                        maxParticles = particles.main.maxParticles,
                        emissionEnabled = particles.emission.enabled,
                        shapeEnabled = particles.shape.enabled
                    };
                }

                if (obj is SpriteRenderer spriteRenderer)
                    return BuildSpriteRendererProfile(spriteRenderer, limits);

                if (obj is ParticleSystemRenderer particleRenderer)
                    return BuildParticleSystemRendererProfile(particleRenderer, limits);

                if (obj is LineRenderer lineRenderer)
                    return BuildLineRendererProfile(lineRenderer, limits);

                if (obj is TrailRenderer trailRenderer)
                    return BuildTrailRendererProfile(trailRenderer, limits);

                if (obj is Renderer renderer)
                    return BuildRendererProfile(renderer, limits);

                if (obj is Camera camera)
                    return BuildCameraProfile(camera);

                if (obj is Light light)
                    return BuildLightProfile(light);

                if (importer is TextureImporter textureImporter)
                {
                    return new
                    {
                        profile = "textureImporter",
                        path = NormalizePath(textureImporter.assetPath),
                        textureImporter.textureType,
                        textureImporter.spriteImportMode,
                        textureImporter.spritePixelsPerUnit,
                        textureImporter.alphaIsTransparency,
                        textureImporter.mipmapEnabled,
                        maxTextureSize = textureImporter.maxTextureSize,
                        textureCompression = textureImporter.textureCompression.ToString()
                    };
                }

                var reflectedProfile = BuildReflectedAssetProfile(obj, assetPath, limits);
                if (reflectedProfile != null)
                    return reflectedProfile;

                if (!string.IsNullOrWhiteSpace(assetPath) && assetPath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
                {
                    return new
                    {
                        profile = "animatorController",
                        path = NormalizePath(assetPath),
                        guidance = "Use UniBridge_ManageAnimatorController for layers, parameters, states, transitions, and blend trees instead of patching raw YAML."
                    };
                }
            }
            catch (Exception ex)
            {
                return new
                {
                    profile = "error",
                    message = ex.Message
                };
            }

            return null;
        }

        internal static object BuildSmartProfileForObject(Object obj, AssetImporter importer = null, string assetPath = null, int itemLimit = 80)
        {
            var limits = new SerializationLimits
            {
                TextLimit = MinimalTextLimit,
                PropertyLimit = DefaultPropertyLimit,
                HierarchyDepth = DefaultHierarchyDepth,
                ItemLimit = Clamp(itemLimit <= 0 ? 80 : itemLimit, 1, MaxItemLimit)
            };

            assetPath ??= obj == null ? importer?.assetPath : AssetDatabase.GetAssetPath(obj);
            return BuildSmartProfile(obj, importer, assetPath, limits);
        }

        static object BuildRendererCommonProfile(Renderer renderer, SerializationLimits limits)
        {
            return new
            {
                renderer.enabled,
                sortingLayerName = renderer.sortingLayerName,
                sortingOrder = renderer.sortingOrder,
                renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(renderer),
                shadowCastingMode = renderer.shadowCastingMode.ToString(),
                receiveShadows = renderer.receiveShadows,
                lightProbeUsage = renderer.lightProbeUsage.ToString(),
                reflectionProbeUsage = renderer.reflectionProbeUsage.ToString(),
                motionVectorGenerationMode = renderer.motionVectorGenerationMode.ToString(),
                bounds = SerializeBounds(renderer.bounds),
                sharedMaterials = renderer.sharedMaterials
                    .Take(Math.Min(limits.ItemLimit, 32))
                    .Select(SerializeObjectReference)
                    .ToArray()
            };
        }

        static object BuildRendererProfile(Renderer renderer, SerializationLimits limits)
        {
            return new
            {
                profile = "renderer",
                renderer = BuildRendererCommonProfile(renderer, limits)
            };
        }

        static object BuildSpriteRendererProfile(SpriteRenderer renderer, SerializationLimits limits)
        {
            return new
            {
                profile = "spriteRenderer",
                renderer = BuildRendererCommonProfile(renderer, limits),
                sprite = SerializeObjectReference(renderer.sprite),
                color = SerializeColor(renderer.color),
                drawMode = renderer.drawMode.ToString(),
                size = SerializeVector2(renderer.size),
                tileMode = renderer.tileMode.ToString(),
                maskInteraction = renderer.maskInteraction.ToString(),
                spriteSortPoint = renderer.spriteSortPoint.ToString()
            };
        }

        static object BuildParticleSystemRendererProfile(ParticleSystemRenderer renderer, SerializationLimits limits)
        {
            return new
            {
                profile = "particleSystemRenderer",
                renderer = BuildRendererCommonProfile(renderer, limits),
                renderMode = renderer.renderMode.ToString(),
                sortMode = renderer.sortMode.ToString(),
                alignment = renderer.alignment.ToString(),
                mesh = SerializeObjectReference(renderer.mesh),
                trailMaterial = SerializeObjectReference(renderer.trailMaterial),
                cameraVelocityScale = renderer.cameraVelocityScale,
                velocityScale = renderer.velocityScale,
                lengthScale = renderer.lengthScale
            };
        }

        static object BuildLineRendererProfile(LineRenderer renderer, SerializationLimits limits)
        {
            return new
            {
                profile = "lineRenderer",
                renderer = BuildRendererCommonProfile(renderer, limits),
                positionCount = renderer.positionCount,
                loop = renderer.loop,
                widthMultiplier = renderer.widthMultiplier,
                textureMode = renderer.textureMode.ToString(),
                alignment = renderer.alignment.ToString(),
                useWorldSpace = renderer.useWorldSpace
            };
        }

        static object BuildTrailRendererProfile(TrailRenderer renderer, SerializationLimits limits)
        {
            return new
            {
                profile = "trailRenderer",
                renderer = BuildRendererCommonProfile(renderer, limits),
                emitting = renderer.emitting,
                time = renderer.time,
                minVertexDistance = renderer.minVertexDistance,
                widthMultiplier = renderer.widthMultiplier,
                textureMode = renderer.textureMode.ToString(),
                alignment = renderer.alignment.ToString(),
                autodestruct = renderer.autodestruct
            };
        }

        static object BuildCameraProfile(Camera camera)
        {
            return new
            {
                profile = "camera",
                camera.enabled,
                cameraType = camera.cameraType.ToString(),
                clearFlags = camera.clearFlags.ToString(),
                backgroundColor = SerializeColor(camera.backgroundColor),
                orthographic = camera.orthographic,
                orthographicSize = camera.orthographicSize,
                fieldOfView = camera.fieldOfView,
                nearClipPlane = camera.nearClipPlane,
                farClipPlane = camera.farClipPlane,
                depth = camera.depth,
                rect = SerializeRect(camera.rect),
                pixelRect = SerializeRect(camera.pixelRect),
                cullingMask = SerializeLayerMask(camera.cullingMask),
                targetTexture = SerializeObjectReference(camera.targetTexture),
                allowHDR = camera.allowHDR,
                allowMSAA = camera.allowMSAA
            };
        }

        static object BuildLightProfile(Light light)
        {
            return new
            {
                profile = "light",
                light.enabled,
                type = light.type.ToString(),
                color = SerializeColor(light.color),
                intensity = light.intensity,
                range = light.range,
                spotAngle = light.spotAngle,
                shadows = light.shadows.ToString(),
                renderingLayerMask = RenderingLayerUtility.SerializeRenderingLayerMask(light)
            };
        }

        static object BuildReflectedAssetProfile(Object obj, string assetPath, SerializationLimits limits)
        {
            if (obj == null)
                return BuildPathOnlyProfile(assetPath, limits);

            var typeName = obj.GetType().FullName ?? obj.GetType().Name;
            switch (typeName)
            {
                case "TMPro.TMP_FontAsset":
                    return BuildTmpFontAssetProfile(obj, limits);
                case "UnityEngine.Audio.AudioMixer":
                    return BuildAudioMixerProfile(obj, limits);
                case "UnityEngine.Audio.AudioMixerGroup":
                    return BuildAudioMixerGroupProfile(obj, limits);
                case "UnityEngine.Audio.AudioMixerSnapshot":
                    return BuildAudioMixerSnapshotProfile(obj);
                case "UnityEngine.Timeline.TimelineAsset":
                    return BuildTimelineAssetProfile(obj, limits);
                case "UnityEngine.InputSystem.InputActionAsset":
                    return BuildInputActionAssetProfile(obj, assetPath, limits);
                case "UnityEngine.U2D.SpriteAtlas":
                    return BuildSpriteAtlasProfile(obj, limits);
                case "UnityEngine.Tilemaps.Tile":
                case "UnityEngine.Tilemaps.TileBase":
                    return BuildTileProfile(obj);
                case "UnityEngine.Video.VideoClip":
                    return BuildVideoClipProfile(obj);
                case "UnityEngine.VFX.VisualEffectAsset":
                    return BuildVisualEffectAssetProfile(obj, assetPath, limits);
                case "UnityEngine.UIElements.VisualTreeAsset":
                    return BuildUiToolkitAssetProfile("uiToolkitVisualTreeAsset", obj, assetPath, limits);
                case "UnityEngine.UIElements.StyleSheet":
                    return BuildUiToolkitAssetProfile("uiToolkitStyleSheet", obj, assetPath, limits);
                case "UnityEngine.UIElements.ThemeStyleSheet":
                    return BuildUiToolkitAssetProfile("uiToolkitThemeStyleSheet", obj, assetPath, limits);
                case "UnityEngine.UIElements.PanelSettings":
                    return BuildPanelSettingsProfile(obj, assetPath, limits);
            }

            if (obj is TerrainLayer terrainLayer)
                return BuildTerrainLayerProfile(terrainLayer);
            if (obj is AvatarMask avatarMask)
                return BuildAvatarMaskProfile(avatarMask, limits);
            if (obj is ShaderVariantCollection collection)
                return BuildShaderVariantCollectionProfile(collection);
            if (obj is Mesh mesh)
                return BuildMeshProfile(mesh);
            if (obj is Shader shader)
                return BuildShaderProfile(shader, limits);
            if (obj is ComputeShader computeShader)
                return BuildComputeShaderProfile(computeShader, assetPath, limits);

            return BuildPathOnlyProfile(assetPath, limits);
        }

        static object BuildPathOnlyProfile(string assetPath, SerializationLimits limits)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            if (assetPath.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
                return BuildInputActionAssetProfile(null, assetPath, limits);
            if (assetPath.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
                return BuildUiToolkitAssetProfile("uiToolkitVisualTreeAsset", null, assetPath, limits);
            if (assetPath.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
                return BuildUiToolkitAssetProfile("uiToolkitStyleSheet", null, assetPath, limits);
            if (assetPath.EndsWith(".spriteatlas", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    profile = "spriteAtlas",
                    path = NormalizePath(assetPath),
                    text = BuildTextAssetStats(assetPath)
                };
            }

            return null;
        }

        static object BuildTmpFontAssetProfile(Object fontAsset, SerializationLimits limits)
        {
            return new
            {
                profile = "tmpFontAsset",
                atlasWidth = ReadInt(fontAsset, "atlasWidth", "m_AtlasWidth"),
                atlasHeight = ReadInt(fontAsset, "atlasHeight", "m_AtlasHeight"),
                atlasPadding = ReadInt(fontAsset, "atlasPadding", "m_AtlasPadding"),
                atlasPopulationMode = ReadString(fontAsset, "atlasPopulationMode", "m_AtlasPopulationMode"),
                material = SerializeObjectReference(ReadUnityObject(fontAsset, "material", "material_EditorRef")),
                sourceFontFile = SerializeObjectReference(ReadUnityObject(fontAsset, "sourceFontFile", "m_SourceFontFile_EditorRef")),
                atlasTextures = ReadUnityObjectArray(fontAsset, limits.ItemLimit, "atlasTextures", "m_AtlasTextures"),
                fallbackFontAssets = ReadUnityObjectArray(fontAsset, limits.ItemLimit, "fallbackFontAssetTable", "fallbackFontAssets"),
                glyphCount = CountEnumerable(ReadMember(fontAsset, "glyphTable")),
                characterCount = CountEnumerable(ReadMember(fontAsset, "characterTable")),
                fontWeightCount = CountEnumerable(ReadMember(fontAsset, "fontWeightTable", "fontWeights")),
                multiAtlas = ReadBool(fontAsset, "isMultiAtlasTexturesEnabled", "m_IsMultiAtlasTexturesEnabled"),
                clearDynamicDataOnBuild = ReadBool(fontAsset, "clearDynamicDataOnBuild", "m_ClearDynamicDataOnBuild")
            };
        }

        static object BuildAudioMixerProfile(Object mixer, SerializationLimits limits)
        {
            var groups = InvokeEnumerable(mixer, "FindMatchingGroups", string.Empty)
                .Take(limits.ItemLimit)
                .OfType<Object>()
                .ToArray();
            var snapshots = CollectSerializedObjectReferences(mixer, "AudioMixerSnapshot", limits.ItemLimit);

            return new
            {
                profile = "audioMixer",
                updateMode = ReadString(mixer, "updateMode"),
                outputAudioMixerGroup = SerializeObjectReference(ReadUnityObject(mixer, "outputAudioMixerGroup")),
                groupCount = groups.Length,
                groups = groups.Select(SerializeObjectReference).ToArray(),
                snapshotCount = snapshots.Length,
                snapshots,
                exposedParameterHints = CollectSerializedPropertyNames(mixer, "exposed", limits.ItemLimit),
                guidance = "Use scene AudioSource outputAudioMixerGroup routing and mixer snapshots instead of patching raw mixer internals."
            };
        }

        static object BuildAudioMixerGroupProfile(Object group, SerializationLimits limits)
        {
            return new
            {
                profile = "audioMixerGroup",
                audioMixer = SerializeObjectReference(ReadUnityObject(group, "audioMixer")),
                children = ReadUnityObjectArray(group, limits.ItemLimit, "children"),
                effects = CollectSerializedPropertyNames(group, "effect", limits.ItemLimit),
                mute = ReadBool(group, "mute"),
                solo = ReadBool(group, "solo"),
                bypassEffects = ReadBool(group, "bypassEffects")
            };
        }

        static object BuildAudioMixerSnapshotProfile(Object snapshot)
        {
            return new
            {
                profile = "audioMixerSnapshot",
                audioMixer = SerializeObjectReference(ReadUnityObject(snapshot, "audioMixer")),
                name = snapshot.name
            };
        }

        static object BuildTimelineAssetProfile(Object timeline, SerializationLimits limits)
        {
            var outputTracks = InvokeEnumerable(timeline, "GetOutputTracks").ToArray();
            var tracks = (outputTracks.Length > 0 ? outputTracks : InvokeEnumerable(timeline, "GetRootTracks").ToArray())
                .Take(limits.ItemLimit)
                .ToArray();

            return new
            {
                profile = "timelineAsset",
                duration = ReadDouble(timeline, "duration"),
                fixedDuration = ReadDouble(timeline, "fixedDuration"),
                outputTrackCount = ReadInt(timeline, "outputTrackCount"),
                rootTrackCount = ReadInt(timeline, "rootTrackCount"),
                markerTrack = SerializeObjectReference(ReadUnityObject(timeline, "markerTrack")),
                tracks = tracks.Select(track => new
                {
                    name = ReadString(track, "name") ?? (track as Object)?.name,
                    type = track?.GetType().FullName,
                    muted = ReadBool(track, "muted"),
                    locked = ReadBool(track, "locked"),
                    clipCount = CountEnumerable(InvokeEnumerable(track, "GetClips")),
                    clips = InvokeEnumerable(track, "GetClips")
                        .Take(Math.Min(limits.ItemLimit, 24))
                        .Select(clip => new
                        {
                            displayName = ReadString(clip, "displayName"),
                            start = ReadDouble(clip, "start"),
                            duration = ReadDouble(clip, "duration"),
                            end = ReadDouble(clip, "end"),
                            clipIn = ReadDouble(clip, "clipIn"),
                            asset = SerializeObjectReference(ReadUnityObject(clip, "asset"))
                        })
                        .ToArray()
                }).ToArray()
            };
        }

        static object BuildInputActionAssetProfile(Object inputActionAsset, string assetPath, SerializationLimits limits)
        {
            var jsonProfile = BuildInputActionsJsonProfile(assetPath, limits);
            if (jsonProfile != null)
                return jsonProfile;

            var maps = ReadEnumerable(inputActionAsset, limits.ItemLimit, "actionMaps").ToArray();
            var controlSchemes = ReadEnumerable(inputActionAsset, limits.ItemLimit, "controlSchemes").ToArray();
            return new
            {
                profile = "inputActionAsset",
                actionMapCount = maps.Length,
                maps = maps.Select(map => new
                {
                    name = ReadString(map, "name"),
                    actionCount = CountEnumerable(ReadMember(map, "actions")),
                    bindingCount = CountEnumerable(ReadMember(map, "bindings"))
                }).ToArray(),
                controlSchemeCount = controlSchemes.Length,
                controlSchemes = controlSchemes.Select(scheme => ReadString(scheme, "name")).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray()
            };
        }

        static object BuildInputActionsJsonProfile(string assetPath, SerializationLimits limits)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var absolute = AssetPathToAbsolutePath(assetPath);
            if (string.IsNullOrWhiteSpace(absolute) || !File.Exists(absolute))
                return null;

            try
            {
                var json = JObject.Parse(File.ReadAllText(absolute));
                var maps = json["maps"] as JArray ?? new JArray();
                var controlSchemes = json["controlSchemes"] as JArray ?? new JArray();
                return new
                {
                    profile = "inputActionAsset",
                    path = NormalizePath(assetPath),
                    actionMapCount = maps.Count,
                    maps = maps.Take(limits.ItemLimit).Select(map => new
                    {
                        name = map.Value<string>("name"),
                        actionCount = (map["actions"] as JArray)?.Count ?? 0,
                        bindingCount = (map["bindings"] as JArray)?.Count ?? 0
                    }).ToArray(),
                    controlSchemeCount = controlSchemes.Count,
                    controlSchemes = controlSchemes.Take(limits.ItemLimit).Select(scheme => scheme.Value<string>("name")).ToArray(),
                    text = BuildTextAssetStats(assetPath)
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    profile = "inputActionAsset",
                    path = NormalizePath(assetPath),
                    parseError = ex.Message,
                    text = BuildTextAssetStats(assetPath)
                };
            }
        }

        static object BuildSpriteAtlasProfile(Object atlas, SerializationLimits limits)
        {
            var spriteCount = ReadInt(atlas, "spriteCount") ?? 0;
            return new
            {
                profile = "spriteAtlas",
                spriteCount,
                isVariant = ReadBool(atlas, "isVariant"),
                tag = ReadString(atlas, "tag"),
                sprites = ReadSpriteAtlasSprites(atlas, spriteCount, limits.ItemLimit)
            };
        }

        static object[] ReadSpriteAtlasSprites(Object atlas, int spriteCount, int limit)
        {
            try
            {
                var count = Math.Max(0, Math.Min(spriteCount <= 0 ? limit : spriteCount, limit));
                var array = new Sprite[count];
                var method = atlas.GetType().GetMethod("GetSprites", new[] { typeof(Sprite[]) });
                if (method == null)
                    return Array.Empty<object>();
                method.Invoke(atlas, new object[] { array });
                return array.Where(sprite => sprite != null).Select(SerializeObjectReference).ToArray();
            }
            catch
            {
                return Array.Empty<object>();
            }
        }

        static object BuildTileProfile(Object tile)
        {
            return new
            {
                profile = "tile",
                sprite = SerializeObjectReference(ReadUnityObject(tile, "sprite")),
                color = SerializeLooseValue(ReadMember(tile, "color")),
                transform = SerializeLooseValue(ReadMember(tile, "transform")),
                flags = ReadString(tile, "flags"),
                colliderType = ReadString(tile, "colliderType")
            };
        }

        static object BuildVideoClipProfile(Object video)
        {
            return new
            {
                profile = "videoClip",
                width = ReadUInt(video, "width"),
                height = ReadUInt(video, "height"),
                length = ReadDouble(video, "length"),
                frameCount = ReadULong(video, "frameCount"),
                frameRate = ReadDouble(video, "frameRate"),
                pixelAspectRatioNumerator = ReadUInt(video, "pixelAspectRatioNumerator"),
                pixelAspectRatioDenominator = ReadUInt(video, "pixelAspectRatioDenominator"),
                audioTrackCount = ReadUInt(video, "audioTrackCount")
            };
        }

        static object BuildVisualEffectAssetProfile(Object asset, string assetPath, SerializationLimits limits)
        {
            return new
            {
                profile = "visualEffectAsset",
                path = NormalizePath(assetPath),
                dependencies = BuildDependencySlice(assetPath, limits, ".vfx", ".shader", ".compute", ".png", ".tga", ".exr", ".mat"),
                exposedPropertyHints = CollectSerializedPropertyNames(asset, "exposed", limits.ItemLimit)
            };
        }

        static object BuildUiToolkitAssetProfile(string profile, Object obj, string assetPath, SerializationLimits limits)
        {
            return new
            {
                profile,
                path = NormalizePath(assetPath),
                type = obj?.GetType().FullName,
                text = BuildTextAssetStats(assetPath),
                dependencies = BuildDependencySlice(assetPath, limits, ".uxml", ".uss", ".asset", ".png", ".jpg", ".jpeg", ".tga", ".svg", ".ttf", ".otf")
            };
        }

        static object BuildPanelSettingsProfile(Object panelSettings, string assetPath, SerializationLimits limits)
        {
            return new
            {
                profile = "uiToolkitPanelSettings",
                path = NormalizePath(assetPath),
                scaleMode = ReadString(panelSettings, "scaleMode"),
                referenceResolution = SerializeLooseValue(ReadMember(panelSettings, "referenceResolution")),
                scale = ReadFloat(panelSettings, "scale"),
                targetTexture = SerializeObjectReference(ReadUnityObject(panelSettings, "targetTexture")),
                themeStyleSheet = SerializeObjectReference(ReadUnityObject(panelSettings, "themeStyleSheet")),
                textSettings = SerializeObjectReference(ReadUnityObject(panelSettings, "textSettings")),
                dependencies = BuildDependencySlice(assetPath, limits, ".uss", ".asset", ".ttf", ".otf")
            };
        }

        static object BuildTerrainLayerProfile(TerrainLayer layer)
        {
            return new
            {
                profile = "terrainLayer",
                diffuseTexture = SerializeObjectReference(layer.diffuseTexture),
                normalMapTexture = SerializeObjectReference(layer.normalMapTexture),
                maskMapTexture = SerializeObjectReference(layer.maskMapTexture),
                tileSize = SerializeVector2(layer.tileSize),
                tileOffset = SerializeVector2(layer.tileOffset),
                layer.metallic,
                layer.smoothness,
                layer.normalScale,
                diffuseRemapMin = SerializeVector4(layer.diffuseRemapMin),
                diffuseRemapMax = SerializeVector4(layer.diffuseRemapMax),
                maskMapRemapMin = SerializeVector4(layer.maskMapRemapMin),
                maskMapRemapMax = SerializeVector4(layer.maskMapRemapMax)
            };
        }

        static object BuildAvatarMaskProfile(AvatarMask mask, SerializationLimits limits)
        {
            var bodyParts = Enum.GetValues(typeof(AvatarMaskBodyPart))
                .Cast<AvatarMaskBodyPart>()
                .Select(part => new { part = part.ToString(), active = mask.GetHumanoidBodyPartActive(part) })
                .ToArray();
            var transformLimit = Math.Min(mask.transformCount, limits.ItemLimit);
            var transforms = Enumerable.Range(0, transformLimit)
                .Select(i => new
                {
                    index = i,
                    path = mask.GetTransformPath(i),
                    active = mask.GetTransformActive(i)
                })
                .ToArray();

            return new
            {
                profile = "avatarMask",
                bodyParts,
                transformCount = mask.transformCount,
                returnedTransforms = transforms.Length,
                transforms
            };
        }

        static object BuildShaderVariantCollectionProfile(ShaderVariantCollection collection)
        {
            return new
            {
                profile = "shaderVariantCollection",
                collection.shaderCount,
                collection.variantCount,
                guidance = "Use ManageAsset CreateOrUpdate with ShaderVariantCollection variants for authoring; raw variant details are not fully exposed by Unity public API."
            };
        }

        static object BuildMeshProfile(Mesh mesh)
        {
            var subMeshes = Enumerable.Range(0, mesh.subMeshCount)
                .Select(i => new
                {
                    index = i,
                    indexCount = mesh.GetIndexCount(i),
                    topology = mesh.GetTopology(i).ToString()
                })
                .ToArray();

            return new
            {
                profile = "mesh",
                mesh.vertexCount,
                mesh.subMeshCount,
                indexFormat = mesh.indexFormat.ToString(),
                mesh.isReadable,
                bounds = SerializeBounds(mesh.bounds),
                mesh.blendShapeCount,
                subMeshes
            };
        }

        static object BuildShaderProfile(Shader shader, SerializationLimits limits)
        {
            var propertyCount = shader.GetPropertyCount();
            var propertyLimit = Math.Min(propertyCount, Math.Min(limits.ItemLimit, 80));
            var properties = Enumerable.Range(0, propertyLimit)
                .Select(i => new
                {
                    index = i,
                    name = shader.GetPropertyName(i),
                    description = shader.GetPropertyDescription(i),
                    type = shader.GetPropertyType(i).ToString(),
                    flags = shader.GetPropertyFlags(i).ToString(),
                    attributes = shader.GetPropertyAttributes(i)
                })
                .ToArray();

            return new
            {
                profile = "shader",
                shader.name,
                shader.isSupported,
                propertyCount,
                returnedProperties = properties.Length,
                properties
            };
        }

        static object BuildComputeShaderProfile(ComputeShader shader, string assetPath, SerializationLimits limits)
        {
            return new
            {
                profile = "computeShader",
                shader.name,
                path = NormalizePath(assetPath),
                text = BuildTextAssetStats(assetPath),
                dependencies = BuildDependencySlice(assetPath, limits, ".compute", ".hlsl", ".cginc", ".shader")
            };
        }

        static object BuildDependencySlice(string assetPath, SerializationLimits limits, params string[] extensions)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return Array.Empty<object>();

            try
            {
                var allowed = new HashSet<string>(extensions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                return AssetDatabase.GetDependencies(assetPath, true)
                    .Where(path => !string.Equals(path, assetPath, StringComparison.OrdinalIgnoreCase))
                    .Where(path => allowed.Count == 0 || allowed.Contains(Path.GetExtension(path)))
                    .Take(limits.ItemLimit)
                    .Select(path => new
                    {
                        path = NormalizePath(path),
                        guid = AssetDatabase.AssetPathToGUID(path),
                        type = AssetDatabase.GetMainAssetTypeAtPath(path)?.FullName
                    })
                    .ToArray();
            }
            catch
            {
                return Array.Empty<object>();
            }
        }

        static object BuildTextAssetStats(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            try
            {
                var absolute = AssetPathToAbsolutePath(assetPath);
                if (string.IsNullOrWhiteSpace(absolute) || !File.Exists(absolute))
                    return null;

                var info = new FileInfo(absolute);
                var lineCount = 0;
                foreach (var _ in File.ReadLines(absolute))
                    lineCount++;

                return new
                {
                    bytes = info.Length,
                    lineCount,
                    extension = Path.GetExtension(assetPath)
                };
            }
            catch
            {
                return null;
            }
        }

        static object[] CollectSerializedObjectReferences(Object obj, string typeNameContains, int limit)
        {
            if (obj == null)
                return Array.Empty<object>();

            var refs = new List<object>();
            var seen = new HashSet<long>();
            try
            {
                using var so = new SerializedObject(obj);
                var iterator = so.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren) && refs.Count < limit)
                {
                    enterChildren = false;
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference || iterator.objectReferenceValue == null)
                        continue;

                    var referenced = iterator.objectReferenceValue;
                    if (!string.IsNullOrWhiteSpace(typeNameContains) &&
                        referenced.GetType().FullName?.IndexOf(typeNameContains, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (!seen.Add(UnityApiAdapter.GetObjectId(referenced)))
                        continue;

                    refs.Add(new
                    {
                        propertyPath = iterator.propertyPath,
                        reference = SerializeObjectReference(referenced)
                    });
                }
            }
            catch
            {
                return Array.Empty<object>();
            }

            return refs.ToArray();
        }

        static string[] CollectSerializedPropertyNames(Object obj, string contains, int limit)
        {
            if (obj == null)
                return Array.Empty<string>();

            var names = new List<string>();
            try
            {
                using var so = new SerializedObject(obj);
                var iterator = so.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren) && names.Count < limit)
                {
                    enterChildren = false;
                    var path = iterator.propertyPath;
                    if (string.IsNullOrWhiteSpace(path))
                        continue;
                    if (!string.IsNullOrWhiteSpace(contains) &&
                        path.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0 &&
                        iterator.displayName.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    names.Add(path);
                }
            }
            catch
            {
                return Array.Empty<string>();
            }

            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        static object ReadMember(object target, params string[] names)
        {
            if (target == null || names == null)
                return null;

            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var name in names.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                try
                {
                    var property = type.GetProperty(name, flags);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        return property.GetValue(target);

                    var field = type.GetField(name, flags);
                    if (field != null)
                        return field.GetValue(target);
                }
                catch
                {
                    // Try the next alias.
                }
            }

            return null;
        }

        static string ReadString(object target, params string[] names)
        {
            return ReadMember(target, names)?.ToString();
        }

        static int? ReadInt(object target, params string[] names)
        {
            var value = ReadMember(target, names);
            try
            {
                return value == null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        static uint? ReadUInt(object target, params string[] names)
        {
            var value = ReadMember(target, names);
            try
            {
                return value == null ? null : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        static ulong? ReadULong(object target, params string[] names)
        {
            var value = ReadMember(target, names);
            try
            {
                return value == null ? null : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        static float? ReadFloat(object target, params string[] names)
        {
            var value = ReadMember(target, names);
            try
            {
                return value == null ? null : Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        static double? ReadDouble(object target, params string[] names)
        {
            var value = ReadMember(target, names);
            try
            {
                return value == null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        static bool? ReadBool(object target, params string[] names)
        {
            var value = ReadMember(target, names);
            try
            {
                return value == null ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        static Object ReadUnityObject(object target, params string[] names)
        {
            return ReadMember(target, names) as Object;
        }

        static object[] ReadUnityObjectArray(object target, int limit, params string[] names)
        {
            return ReadEnumerable(target, limit, names)
                .OfType<Object>()
                .Select(SerializeObjectReference)
                .ToArray();
        }

        static IEnumerable<object> ReadEnumerable(object target, int limit, params string[] names)
        {
            var value = ReadMember(target, names);
            return EnumerateObjects(value).Take(Math.Max(1, limit));
        }

        static IEnumerable<object> InvokeEnumerable(object target, string methodName, params object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName))
                return Enumerable.Empty<object>();

            try
            {
                var argTypes = args?.Select(arg => arg?.GetType() ?? typeof(object)).ToArray() ?? Type.EmptyTypes;
                var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, argTypes, null)
                             ?? target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                 .FirstOrDefault(candidate => candidate.Name == methodName && candidate.GetParameters().Length == (args?.Length ?? 0));
                var value = method?.Invoke(target, args);
                return EnumerateObjects(value);
            }
            catch
            {
                return Enumerable.Empty<object>();
            }
        }

        static IEnumerable<object> EnumerateObjects(object value)
        {
            if (value == null || value is string)
                yield break;

            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null)
                        yield return item;
                }
            }
        }

        static int CountEnumerable(object value)
        {
            if (value == null || value is string)
                return 0;
            if (value is ICollection collection)
                return collection.Count;
            if (value is IEnumerable enumerable)
                return enumerable.Cast<object>().Count();
            return 0;
        }

        static object SerializeLooseValue(object value)
        {
            return value switch
            {
                null => null,
                Object unityObject => SerializeObjectReference(unityObject),
                Vector2 v => SerializeVector2(v),
                Vector3 v => SerializeVector3(v),
                Vector4 v => SerializeVector4(v),
                Vector2Int v => SerializeVector2Int(v),
                Vector3Int v => SerializeVector3Int(v),
                Color c => SerializeColor(c),
                Color32 c => SerializeColor(c),
                Quaternion q => SerializeQuaternion(q),
                Rect r => SerializeRect(r),
                RectInt r => SerializeRectInt(r),
                Bounds b => SerializeBounds(b),
                BoundsInt b => SerializeBoundsInt(b),
                Matrix4x4 m => new
                {
                    row0 = SerializeVector4(m.GetRow(0)),
                    row1 = SerializeVector4(m.GetRow(1)),
                    row2 = SerializeVector4(m.GetRow(2)),
                    row3 = SerializeVector4(m.GetRow(3))
                },
                _ when value.GetType().IsEnum => value.ToString(),
                _ when value is IConvertible => value,
                _ => value.ToString()
            };
        }

        static object SerializeObjectReference(Object value)
        {
            if (value == null)
                return null;

            var assetPath = AssetDatabase.GetAssetPath(value);
            return new
            {
                name = value.name,
                type = value.GetType().FullName ?? value.GetType().Name,
                entityId = UnityApiAdapter.GetObjectId(value),
                assetPath = NormalizePath(assetPath),
                guid = string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath)
            };
        }

        static object SerializeMaterial(Material material, SerializationLimits limits)
        {
            var shader = material.shader;
            var textureNames = material.GetTexturePropertyNames()
                .Take(limits.ItemLimit)
                .Select(name => new
                {
                    name,
                    texture = SerializeObjectReference(material.GetTexture(name))
                })
                .ToArray();

            return new
            {
                shader = shader != null ? shader.name : null,
                shaderPath = shader != null ? AssetDatabase.GetAssetPath(shader) : null,
                material.renderQueue,
                material.enableInstancing,
                material.doubleSidedGI,
                enabledKeywords = material.enabledKeywords.Select(keyword => keyword.ToString()).ToArray(),
                textures = textureNames
            };
        }

        static object CreateEnvelope(
            string assetPath,
            AssetMetadata metadata,
            AssetSerializationMode mode,
            string contentType,
            object serializedAsset,
            SerializationLimits limits,
            string action,
            string syntax = null,
            string interfaceSummary = null)
        {
            return new
            {
                version = 1,
                serializer = "UniBridge.AssetSnapshotSerializer",
                action,
                mode = mode.ToString(),
                assetPath,
                assetGuid = metadata.Guid,
                name = metadata.Name,
                assetType = metadata.Type,
                contentType,
                syntax,
                serializedAssetInterface = interfaceSummary,
                metadata,
                limits,
                serializedAsset
            };
        }

        static AssetMetadata BuildMetadata(string assetPath)
        {
            var normalized = NormalizeAssetPath(assetPath);
            var absolutePath = AssetPathToAbsolutePath(normalized);
            var fileInfo = !string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath)
                ? new FileInfo(absolutePath)
                : null;
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(normalized);
            var type = AssetDatabase.GetMainAssetTypeAtPath(normalized);
            var importer = AssetImporter.GetAtPath(normalized);

            return new AssetMetadata
            {
                Path = normalized,
                Guid = AssetDatabase.AssetPathToGUID(normalized),
                Name = Path.GetFileNameWithoutExtension(normalized),
                FileName = Path.GetFileName(normalized),
                Extension = Path.GetExtension(normalized),
                Type = type?.FullName ?? mainAsset?.GetType().FullName ?? "Unknown",
                TypeName = type?.Name ?? mainAsset?.GetType().Name ?? "Unknown",
                IsFolder = AssetDatabase.IsValidFolder(normalized),
                IsTextLike = IsTextLike(normalized, mainAsset),
                Labels = mainAsset != null ? AssetDatabase.GetLabels(mainAsset) : Array.Empty<string>(),
                AssetBundleName = importer?.assetBundleName,
                ExistsOnDisk = fileInfo != null || (!string.IsNullOrEmpty(absolutePath) && Directory.Exists(absolutePath)),
                SizeBytes = fileInfo?.Length ?? 0,
                ModifiedUtc = fileInfo?.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                ImporterType = importer?.GetType().FullName
            };
        }

        static SerializationLimits BuildLimits(AssetIntelligenceParams p, AssetSerializationMode mode)
        {
            var requestedText = p.MaxTextChars <= 0 ? DefaultTextLimit : p.MaxTextChars;
            if (mode == AssetSerializationMode.Minimal)
                requestedText = Math.Min(requestedText, MinimalTextLimit);

            return new SerializationLimits
            {
                TextLimit = Clamp(requestedText, 1000, MaxTextLimit),
                PropertyLimit = Clamp(p.MaxSerializedProperties <= 0 ? DefaultPropertyLimit : p.MaxSerializedProperties, 10, MaxPropertyLimit),
                HierarchyDepth = Clamp(p.MaxSerializedDepth <= 0 ? DefaultHierarchyDepth : p.MaxSerializedDepth, 0, MaxHierarchyDepth),
                ItemLimit = Clamp(p.MaxSerializedItems <= 0 ? DefaultItemLimit : p.MaxSerializedItems, 1, MaxItemLimit)
            };
        }

        static object[] ListFolderChildren(string folderPath, int limit)
        {
            return AssetDatabase.GetSubFolders(folderPath)
                .Cast<string>()
                .Concat(Directory.Exists(AssetPathToAbsolutePath(folderPath))
                    ? Directory.GetFiles(AssetPathToAbsolutePath(folderPath))
                        .Select(path => NormalizePath(ToProjectRelativePath(path)))
                        .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    : Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(path => new
                {
                    path,
                    type = AssetDatabase.GetMainAssetTypeAtPath(path)?.FullName,
                    isFolder = AssetDatabase.IsValidFolder(path)
                })
                .ToArray();
        }

        static bool IsPrefabAsset(string assetPath, AssetImporter importer, Object mainAsset)
        {
            return string.Equals(Path.GetExtension(assetPath), ".prefab", StringComparison.OrdinalIgnoreCase) ||
                   importer?.GetType().FullName == "UnityEditor.PrefabImporter" ||
                   mainAsset is GameObject && !string.IsNullOrEmpty(assetPath);
        }

        static bool IsTextLike(string path, Object mainAsset)
        {
            var ext = Path.GetExtension(path);
            return k_TextExtensions.Contains(ext) || mainAsset is TextAsset or MonoScript;
        }

        static string ReadText(string absolutePath, int maxChars)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return string.Empty;

            var text = File.ReadAllText(absolutePath);
            return text.Length <= maxChars
                ? text
                : text.Substring(0, maxChars) + "\n/* ... truncated by UniBridge AssetSnapshotSerializer ... */";
        }

        static string BuildScriptInterfaceSummary(MonoScript script)
        {
            if (script == null)
                return string.Empty;

            var scriptClass = script.GetClass();
            if (scriptClass == null)
                return BuildSourceFallbackSummary(script);

            var lines = new List<string>
            {
                scriptClass.FullName ?? scriptClass.Name,
                "base: " + (scriptClass.BaseType?.FullName ?? "(none)")
            };

            var flags = System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.DeclaredOnly;

            var interfaces = scriptClass.GetInterfaces()
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Take(8)
                .ToArray();
            if (interfaces.Length > 0)
                lines.Add("interfaces: " + string.Join(", ", interfaces));

            var fields = scriptClass.GetFields(flags)
                .Take(12)
                .Select(field => "field " + field.FieldType.Name + " " + field.Name);
            lines.AddRange(fields);

            var properties = scriptClass.GetProperties(flags)
                .Take(12)
                .Select(property => "property " + property.PropertyType.Name + " " + property.Name);
            lines.AddRange(properties);

            var methods = scriptClass
                .GetMethods(flags)
                .Where(method => !method.IsSpecialName)
                .Take(12)
                .Select(method =>
                {
                    var parameters = string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name + " " + parameter.Name));
                    return method.ReturnType.Name + " " + method.Name + "(" + parameters + ")";
                });

            lines.AddRange(methods);
            return string.Join("\n", lines);
        }

        static string BuildSourceFallbackSummary(MonoScript script)
        {
            var text = script == null ? string.Empty : ((TextAsset)script).text;
            if (string.IsNullOrWhiteSpace(text))
                return script?.name ?? string.Empty;

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select((line, index) => new { line = line.Trim(), index })
                .Where(item =>
                    item.line.StartsWith("public ", StringComparison.Ordinal) ||
                    item.line.StartsWith("internal ", StringComparison.Ordinal) ||
                    item.line.StartsWith("protected ", StringComparison.Ordinal) ||
                    item.line.StartsWith("class ", StringComparison.Ordinal) ||
                    item.line.StartsWith("struct ", StringComparison.Ordinal) ||
                    item.line.StartsWith("interface ", StringComparison.Ordinal))
                .Where(item => item.line.Contains(" class ") || item.line.Contains(" struct ") || item.line.Contains(" interface ") || item.line.Contains("(") || item.line.Contains("{ get"))
                .Take(16)
                .Select(item => $"line {item.index + 1}: {item.line}")
                .ToArray();

            return lines.Length == 0 ? script.name : string.Join("\n", lines);
        }

        static string GuessSyntax(string assetPath)
        {
            return Path.GetExtension(assetPath).ToLowerInvariant() switch
            {
                ".cs" => "csharp",
                ".json" or ".asmdef" or ".asmref" => "json",
                ".shader" or ".compute" or ".hlsl" or ".cginc" => "shader",
                ".xml" or ".uxml" => "xml",
                ".yaml" or ".yml" or ".prefab" or ".unity" or ".mat" or ".asset" => "yaml",
                ".uss" or ".css" => "css",
                ".md" => "markdown",
                _ => "text"
            };
        }

        static object SerializeLayerMask(int value)
        {
            var layers = new List<string>();
            for (var bit = 0; bit < 32; bit++)
            {
                if ((value & (1 << bit)) != 0)
                {
                    var name = LayerMask.LayerToName(bit);
                    if (!string.IsNullOrWhiteSpace(name))
                        layers.Add(name);
                }
            }

            return new
            {
                mask = value,
                mode = value switch
                {
                    0 => "Nothing",
                    -1 => "Everything",
                    _ => "Named"
                },
                names = layers.ToArray()
            };
        }

        static object SerializeEnum(SerializedProperty property)
        {
            if (property.enumNames == null ||
                property.enumNames.Length == 0 ||
                property.enumValueIndex < 0 ||
                property.enumValueIndex >= property.enumNames.Length)
            {
                return null;
            }

            return property.enumNames[property.enumValueIndex];
        }

        static object SerializeAnimationCurve(AnimationCurve curve)
        {
            if (curve == null)
                return null;

            return new
            {
                keys = curve.keys.Select(key => new
                {
                    key.time,
                    key.value,
                    key.inTangent,
                    key.outTangent,
                    key.weightedMode
                }).ToArray(),
                preWrapMode = curve.preWrapMode.ToString(),
                postWrapMode = curve.postWrapMode.ToString()
            };
        }

        static object SerializeGradient(Gradient gradient)
        {
            if (gradient == null)
                return null;

            return new
            {
                mode = gradient.mode.ToString(),
                colorSpace = GetGradientColorSpace(gradient),
                colorKeys = gradient.colorKeys.Select(key => new
                {
                    key.time,
                    r = key.color.r,
                    g = key.color.g,
                    b = key.color.b,
                    a = key.color.a
                }).ToArray(),
                alphaKeys = gradient.alphaKeys.Select(key => new
                {
                    key.time,
                    key.alpha
                }).ToArray()
            };
        }

        static object GetGradientColorSpace(Gradient gradient)
        {
            var property = typeof(Gradient).GetProperty("colorSpace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            return property != null ? property.GetValue(gradient)?.ToString() : null;
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

        static string NormalizeAssetPath(string path)
        {
            return ProjectPathResolver.NormalizeAssetPath(path, assumeAssetRelative: false);
        }

        static string AssetPathToAbsolutePath(string path)
        {
            return ProjectPathResolver.ToAbsolutePath(path, assumeAssetRelative: false);
        }

        static string ToProjectRelativePath(string absolutePath)
        {
            return ProjectPathResolver.ToProjectRelativePath(absolutePath, assumeAssetRelative: false);
        }

        static string GetProjectRoot()
        {
            return ProjectPathResolver.ProjectRoot;
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        static object SerializeVector2(Vector2 value) => new { value.x, value.y };
        static object SerializeVector3(Vector3 value) => new { value.x, value.y, value.z };
        static object SerializeVector4(Vector4 value) => new { value.x, value.y, value.z, value.w };
        static object SerializeVector2Int(Vector2Int value) => new { value.x, value.y };
        static object SerializeVector3Int(Vector3Int value) => new { value.x, value.y, value.z };
        static object SerializeColor(Color value) => new { value.r, value.g, value.b, value.a };
        static object SerializeQuaternion(Quaternion value) => new { value.x, value.y, value.z, value.w };
        static object SerializeRect(Rect value) => new { value.x, value.y, value.width, value.height };
        static object SerializeRectInt(RectInt value) => new { value.x, value.y, value.width, value.height };
        static object SerializeBounds(Bounds value) => new { center = SerializeVector3(value.center), size = SerializeVector3(value.size) };
        static object SerializeBoundsInt(BoundsInt value) => new { position = SerializeVector3Int(value.position), size = SerializeVector3Int(value.size) };

        sealed class Counter
        {
            public int Count;
        }

        sealed class SerializationLimits
        {
            public int TextLimit { get; set; }
            public int PropertyLimit { get; set; }
            public int HierarchyDepth { get; set; }
            public int ItemLimit { get; set; }
        }

        sealed class AssetMetadata
        {
            public string Path { get; set; }
            public string Guid { get; set; }
            public string Name { get; set; }
            public string FileName { get; set; }
            public string Extension { get; set; }
            public string Type { get; set; }
            public string TypeName { get; set; }
            public bool IsFolder { get; set; }
            public bool IsTextLike { get; set; }
            public string[] Labels { get; set; }
            public string AssetBundleName { get; set; }
            public bool ExistsOnDisk { get; set; }
            public long SizeBytes { get; set; }
            public string ModifiedUtc { get; set; }
            public string ImporterType { get; set; }
        }
    }
}
