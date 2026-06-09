#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Builds a compact, AI-oriented snapshot of the current Unity project and Editor state.
    /// </summary>
    public static class ContextSnapshot
    {
        const int MaxHierarchyDepth = 3;
        const int MaxComponentNames = 14;
        const int MaxWindowCount = 80;

        public const string Title = "Build project context snapshot";

        public const string Description = @"Build one compact snapshot of the open Unity project for AI orientation.

Use this before planning or editing when the agent needs a reliable overview without making many smaller calls.

Args:
    Depth: Brief, Standard, or Detailed.
    IncludeConsole: Include a compact diagnostic console summary.
    IncludeHierarchy: Include a bounded scene hierarchy summary.
    IncludeSelection: Include current selection and prefab stage context.
    IncludeAssets: Include project asset counts and recent asset paths.
    IncludeTools: Include UniBridge tool availability.
    IncludeWindows: Include open Editor window metadata. Defaults to Detailed only.
    IncludeBuildSettings: Include scene Build Settings. Defaults to Detailed only.
    IncludeProjectRoots: Include project roots and registered package count; registered package root details are Brief-safe and only expand for Detailed or IncludePackageDependencies=true.
    IncludeProjectSettings: Include render pipeline, 2D/3D mode, tags, layers, and sorting layers.
    IncludePackageDependencies: Include package dependency overview from packages-lock.json or manifest.json.
    ConsoleSummaryMode: Compact or Detailed. Compact omits console timeline/sample dumps from ContextSnapshot.
    HierarchyDepth, MaxSceneObjects, MaxAssets, MaxConsoleIssues, MaxTools: Optional output limits.

Returns:
    success, message, and structured data with project identity, roots, render settings, package version/dependencies, editor state, scenes, optional hierarchy, selection, prefab stage, console diagnostics, assets, tools, windows, and hints.";

        [McpTool("UniBridge_ContextSnapshot", Description, Title, Groups = new[] { "core", "editor", "scene", "debug", "resources" }, EnabledByDefault = true)]
        public static object HandleCommand(ContextSnapshotParams parameters)
        {
            var options = SnapshotOptions.From(parameters);

            try
            {
                var projectIdentity = ProjectIdentity.GetOrCreate();
                var hierarchy = options.IncludeHierarchy ? BuildHierarchySnapshot(options) : null;
                var console = options.IncludeConsole ? BuildConsoleSnapshot(options) : null;

                var data = new
                {
                    snapshotId = CreateSnapshotId(projectIdentity),
                    createdUtc = DateTime.UtcNow.ToString("O"),
                    depth = options.Depth.ToString(),
                    limits = new
                    {
                        hierarchyDepth = options.HierarchyDepth,
                        maxSceneObjects = options.MaxSceneObjects,
                        maxAssets = options.MaxAssets,
                        maxConsoleIssues = options.MaxConsoleIssues,
                        consoleSummaryMode = options.ConsoleSummaryMode.ToString(),
                        maxTools = options.MaxTools
                    },
                    project = BuildProjectSnapshot(projectIdentity, options),
                    package = BuildPackageSnapshot(options),
                    editor = BuildEditorSnapshot(),
                    activeTool = BuildActiveToolSnapshot(),
                    scenes = BuildScenesSnapshot(options),
                    prefabStage = BuildPrefabStageSnapshot(),
                    selection = options.IncludeSelection ? BuildSelectionSnapshot() : null,
                    hierarchy,
                    console,
                    assets = options.IncludeAssets ? BuildAssetSnapshot(projectIdentity, options) : null,
                    tools = options.IncludeTools ? BuildToolsSnapshot(options) : null,
                    windows = options.IncludeWindows ? BuildWindowSnapshot() : null,
                    hints = BuildHints(options, hierarchy, console)
                };

                return Response.Success("Built UniBridge context snapshot.", data);
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to build context snapshot: {ex.Message}");
            }
        }

        static object BuildProjectSnapshot(ProjectIdentity.Snapshot identity, SnapshotOptions options)
        {
            return new
            {
                id = identity.ProjectId,
                name = identity.ProjectName,
                root = NormalizePath(identity.ProjectRoot),
                assetsPath = NormalizePath(Application.dataPath),
                settingsPath = NormalizePath(identity.SettingsPath),
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                isBatchMode = Application.isBatchMode,
                roots = options.IncludeProjectRoots ? BuildProjectRootsSnapshot(identity, options) : null,
                environment = options.IncludeProjectSettings ? BuildProjectEnvironmentSnapshot() : null
            };
        }

        static object BuildPackageSnapshot(SnapshotOptions options)
        {
            string name = "com.cidonix.unibridge";
            string version = null;
            string displayName = "UniBridge";

            try
            {
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ContextSnapshot).Assembly);
                if (packageInfo != null)
                {
                    name = packageInfo.name ?? name;
                    version = packageInfo.version;
                    displayName = packageInfo.displayName ?? displayName;
                }
            }
            catch
            {
                // Fallback below handles package metadata when PackageInfo is not available.
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                try
                {
                    var packageJson = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.cidonix.unibridge/package.json");
                    if (packageJson != null)
                    {
                        var json = JObject.Parse(packageJson.text);
                        name = json.Value<string>("name") ?? name;
                        version = json.Value<string>("version") ?? version;
                        displayName = json.Value<string>("displayName") ?? displayName;
                    }
                }
                catch
                {
                    // Keep best-effort package values.
                }
            }

            return new
            {
                name,
                displayName,
                version = version ?? "unknown",
                dependencies = options.IncludePackageDependencies ? BuildPackageDependenciesSnapshot() : null
            };
        }

        static object BuildProjectRootsSnapshot(ProjectIdentity.Snapshot identity, SnapshotOptions options)
        {
            var roots = new List<object>
            {
                BuildRootInfo("Project", string.Empty, identity.ProjectRoot, "project"),
                BuildRootInfo("Assets", "Assets", Application.dataPath, "project"),
                BuildRootInfo("ProjectSettings", "ProjectSettings", Path.Combine(identity.ProjectRoot, "ProjectSettings"), "project"),
                BuildRootInfo("Packages", "Packages", Path.Combine(identity.ProjectRoot, "Packages"), "project")
            };

            var packages = GetRegisteredPackages()
                .Where(package => package != null)
                .Where(package => !string.IsNullOrEmpty(package.name))
                .Where(package => !package.name.StartsWith("com.unity.modules.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(package => package.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var result = new Dictionary<string, object>
            {
                ["roots"] = roots,
                ["registeredPackageCount"] = packages.Length
            };

            if (options.IncludeRegisteredPackageRoots)
            {
                result["registeredPackages"] = packages
                    .Select(package => new
                    {
                        name = package.name,
                        displayName = package.displayName,
                        version = package.version,
                        assetPath = NormalizePath(package.assetPath),
                        resolvedPath = NormalizePath(package.resolvedPath),
                        source = package.source.ToString()
                    })
                    .ToArray();
            }

            return result;
        }

        static object BuildRootInfo(string name, string assetPath, string resolvedPath, string source)
        {
            resolvedPath = NormalizePath(resolvedPath);
            return new
            {
                name,
                assetPath = NormalizePath(assetPath),
                resolvedPath,
                source,
                exists = !string.IsNullOrEmpty(resolvedPath) && Directory.Exists(resolvedPath)
            };
        }

        static IEnumerable<UnityEditor.PackageManager.PackageInfo> GetRegisteredPackages()
        {
            try
            {
                var method = typeof(UnityEditor.PackageManager.PackageInfo).GetMethod(
                    "GetAllRegisteredPackages",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method?.Invoke(null, null) is UnityEditor.PackageManager.PackageInfo[] packages)
                {
                    return packages;
                }
            }
            catch
            {
                // Package roots are best-effort context; failures should not break orientation.
            }

            return Array.Empty<UnityEditor.PackageManager.PackageInfo>();
        }

        static object BuildProjectEnvironmentSnapshot()
        {
            RenderPipelineAsset renderPipelineAsset = null;
            try
            {
                renderPipelineAsset = GraphicsSettings.currentRenderPipeline;
            }
            catch
            {
                // Keep built-in fallback when graphics settings are unavailable.
            }

            var renderPipelineType = renderPipelineAsset != null ? renderPipelineAsset.GetType().FullName : null;
            var renderPipelineName = renderPipelineAsset != null ? renderPipelineAsset.name : "Built-in";

            return new
            {
                companyName = PlayerSettings.companyName,
                productName = PlayerSettings.productName,
                productGuid = PlayerSettings.productGUID.ToString(),
                colorSpace = PlayerSettings.colorSpace.ToString(),
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                activeBuildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString(),
                renderPipeline = new
                {
                    name = renderPipelineName,
                    type = renderPipelineType,
                    family = ClassifyRenderPipeline(renderPipelineType)
                },
                editorDefaults = new
                {
                    defaultBehaviorMode = EditorSettings.defaultBehaviorMode.ToString(),
                    rendererType = EditorSettings.defaultBehaviorMode == EditorBehaviorMode.Mode2D ? "2D" : "3D"
                },
                tags = SafeGetTags(),
                layers = SafeGetLayers(),
                renderingLayers = SafeGetRenderingLayers(),
                sortingLayers = SafeGetSortingLayers()
            };
        }

        static string ClassifyRenderPipeline(string renderPipelineType)
        {
            if (string.IsNullOrEmpty(renderPipelineType))
            {
                return "Built-in";
            }

            if (renderPipelineType.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "URP";
            }

            if (renderPipelineType.IndexOf("HD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                renderPipelineType.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "HDRP";
            }

            return "Custom";
        }

        static string[] SafeGetTags()
        {
            try
            {
                return InternalEditorUtility.tags ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        static object[] SafeGetLayers()
        {
            try
            {
                return Enumerable.Range(0, 32)
                    .Select(index => new
                    {
                        index,
                        name = LayerMask.LayerToName(index)
                    })
                    .Where(layer => !string.IsNullOrEmpty(layer.name))
                    .Cast<object>()
                    .ToArray();
            }
            catch
            {
                return Array.Empty<object>();
            }
        }

        static object[] SafeGetSortingLayers()
        {
            try
            {
                return SortingLayer.layers
                    .Select(layer => new
                    {
                        id = layer.id,
                        name = layer.name,
                        value = layer.value
                    })
                    .Cast<object>()
                    .ToArray();
            }
            catch
            {
                return Array.Empty<object>();
            }
        }

        static object[] SafeGetRenderingLayers()
        {
            try
            {
                return RenderingLayerUtility.GetRenderingLayerEntries();
            }
            catch
            {
                return Array.Empty<object>();
            }
        }

        static object BuildPackageDependenciesSnapshot()
        {
            var projectRoot = GetProjectRoot();
            var lockPath = Path.Combine(projectRoot, "Packages", "packages-lock.json");
            var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");

            if (File.Exists(lockPath))
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(lockPath));
                    var dependencies = json["dependencies"] as JObject;
                    var items = dependencies != null
                        ? dependencies.Properties()
                            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(property =>
                            {
                                var node = property.Value as JObject;
                                return (object)new
                                {
                                    name = property.Name,
                                    version = node?.Value<string>("version"),
                                    source = node?.Value<string>("source"),
                                    depth = node?.Value<int?>("depth")
                                };
                            })
                            .ToList()
                        : new List<object>();

                    return new
                    {
                        sourceFile = "Packages/packages-lock.json",
                        count = items.Count,
                        packages = items
                    };
                }
                catch (Exception ex)
                {
                    return new { sourceFile = "Packages/packages-lock.json", error = ex.Message };
                }
            }

            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(manifestPath));
                    var dependencies = json["dependencies"] as JObject;
                    var items = dependencies != null
                        ? dependencies.Properties()
                            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(property => (object)new
                            {
                                name = property.Name,
                                version = property.Value?.ToString(),
                                source = "manifest",
                                depth = (int?)null
                            })
                            .ToList()
                        : new List<object>();

                    return new
                    {
                        sourceFile = "Packages/manifest.json",
                        count = items.Count,
                        packages = items
                    };
                }
                catch (Exception ex)
                {
                    return new { sourceFile = "Packages/manifest.json", error = ex.Message };
                }
            }

            return new
            {
                sourceFile = (string)null,
                count = 0,
                packages = Array.Empty<object>(),
                note = "No Packages/packages-lock.json or Packages/manifest.json found."
            };
        }

        static object BuildEditorSnapshot()
        {
            var hasDirtyScenes = false;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                {
                    hasDirtyScenes = true;
                    break;
                }
            }

            return new
            {
                isPlaying = EditorApplication.isPlaying,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                isBatchMode = Application.isBatchMode,
                timeSinceStartup = Math.Round(EditorApplication.timeSinceStartup, 3),
                selectionCount = Selection.count,
                hasDirtyScenes,
                applicationPath = NormalizePath(EditorApplication.applicationPath)
            };
        }

        static object BuildActiveToolSnapshot()
        {
            try
            {
                return new
                {
                    tool = UnityEditor.Tools.current.ToString(),
                    pivotMode = UnityEditor.Tools.pivotMode.ToString(),
                    pivotRotation = UnityEditor.Tools.pivotRotation.ToString(),
                    handlePosition = ToVector3(UnityEditor.Tools.handlePosition)
                };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        static object BuildScenesSnapshot(SnapshotOptions options)
        {
            var loadedScenes = new List<object>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid())
                {
                    continue;
                }

                loadedScenes.Add(ToSceneInfo(scene));
            }

            return new
            {
                active = ToSceneInfo(SceneManager.GetActiveScene()),
                loadedCount = loadedScenes.Count,
                loaded = loadedScenes,
                buildSettings = options.IncludeBuildSettings ? BuildSceneBuildSettings() : null
            };
        }

        static object ToSceneInfo(Scene scene)
        {
            if (!scene.IsValid())
            {
                return new
                {
                    valid = false,
                    name = (string)null,
                    path = (string)null,
                    buildIndex = -1,
                    isLoaded = false,
                    isDirty = false,
                    rootCount = 0
                };
            }

            return new
            {
                valid = true,
                name = scene.name,
                path = scene.path,
                buildIndex = scene.buildIndex,
                isLoaded = scene.isLoaded,
                isDirty = scene.isDirty,
                rootCount = scene.isLoaded ? scene.rootCount : 0
            };
        }

        static object BuildSceneBuildSettings()
        {
            return EditorBuildSettings.scenes
                .Select((scene, index) => new
                {
                    index,
                    path = scene.path,
                    guid = scene.guid.ToString(),
                    enabled = scene.enabled
                })
                .ToList();
        }

        static object BuildPrefabStageSnapshot()
        {
            try
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage == null)
                {
                    return new
                    {
                        isOpen = false,
                        assetPath = (string)null,
                        prefabRootName = (string)null,
                        rootObjectId = 0L,
                        mode = (string)null,
                        isDirty = false
                    };
                }

                return new
                {
                    isOpen = true,
                    assetPath = stage.assetPath,
                    prefabRootName = stage.prefabContentsRoot != null ? stage.prefabContentsRoot.name : null,
                    rootObjectId = UnityApiAdapter.GetObjectId(stage.prefabContentsRoot),
                    mode = stage.mode.ToString(),
                    isDirty = stage.scene.isDirty
                };
            }
            catch (Exception ex)
            {
                return new { isOpen = false, error = ex.Message };
            }
        }

        static object BuildSelectionSnapshot()
        {
            var selectedObjects = Selection.objects
                .Where(obj => obj != null)
                .Take(40)
                .Select(ToUnityObjectInfo)
                .ToList();

            var selectedGameObjects = Selection.gameObjects
                .Where(go => go != null)
                .Take(40)
                .Select(go => ToGameObjectSelectionInfo(go))
                .ToList();

            return new
            {
                count = Selection.count,
                activeObject = ToUnityObjectInfo(Selection.activeObject),
                activeGameObject = ToGameObjectSelectionInfo(Selection.activeGameObject),
                objects = selectedObjects,
                gameObjects = selectedGameObjects,
                assetGuids = Selection.assetGUIDs
            };
        }

        static object ToUnityObjectInfo(Object obj)
        {
            if (obj == null)
            {
                return null;
            }

            var assetPath = AssetDatabase.GetAssetPath(obj);
            var gameObject = obj as GameObject;
            var component = obj as Component;

            return new
            {
                name = obj.name,
                type = obj.GetType().FullName,
                objectId = UnityApiAdapter.GetObjectId(obj),
                assetPath = string.IsNullOrEmpty(assetPath) ? null : assetPath,
                hierarchyPath = gameObject != null ? GetHierarchyPath(gameObject) : component != null ? GetHierarchyPath(component.gameObject) : null,
                gameObjectId = gameObject != null ? UnityApiAdapter.GetObjectId(gameObject) : component != null ? UnityApiAdapter.GetObjectId(component.gameObject) : 0L
            };
        }

        static object ToGameObjectSelectionInfo(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            return new
            {
                name = gameObject.name,
                objectId = UnityApiAdapter.GetObjectId(gameObject),
                hierarchyPath = GetHierarchyPath(gameObject),
                scene = gameObject.scene.IsValid() ? gameObject.scene.name : null,
                components = GetComponentTypeNames(gameObject)
            };
        }

        static object BuildHierarchySnapshot(SnapshotOptions options)
        {
            var remaining = options.MaxSceneObjects;
            var returnedObjects = 0;
            var truncatedByCount = false;
            var truncatedByDepth = false;
            var sceneSnapshots = new List<object>();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                var roots = scene.GetRootGameObjects();
                var rootSnapshots = new List<object>();
                foreach (var root in roots)
                {
                    if (remaining <= 0)
                    {
                        truncatedByCount = true;
                        break;
                    }

                    rootSnapshots.Add(BuildGameObjectSnapshot(root, options, 0, ref remaining, ref returnedObjects, ref truncatedByCount, ref truncatedByDepth));
                }

                sceneSnapshots.Add(new
                {
                    name = scene.name,
                    path = scene.path,
                    rootCount = roots.Length,
                    roots = rootSnapshots
                });

                if (remaining <= 0)
                {
                    truncatedByCount = true;
                    break;
                }
            }

            return new
            {
                depth = options.HierarchyDepth,
                maxObjects = options.MaxSceneObjects,
                returnedObjects,
                truncatedByCount,
                truncatedByDepth,
                scenes = sceneSnapshots
            };
        }

        static object BuildGameObjectSnapshot(
            GameObject gameObject,
            SnapshotOptions options,
            int level,
            ref int remaining,
            ref int returnedObjects,
            ref bool truncatedByCount,
            ref bool truncatedByDepth)
        {
            remaining--;
            returnedObjects++;

            var children = new List<object>();
            var childCount = gameObject.transform.childCount;

            if (level < options.HierarchyDepth)
            {
                for (var i = 0; i < childCount; i++)
                {
                    if (remaining <= 0)
                    {
                        truncatedByCount = true;
                        break;
                    }

                    children.Add(BuildGameObjectSnapshot(
                        gameObject.transform.GetChild(i).gameObject,
                        options,
                        level + 1,
                        ref remaining,
                        ref returnedObjects,
                        ref truncatedByCount,
                        ref truncatedByDepth));
                }
            }
            else if (childCount > 0)
            {
                truncatedByDepth = true;
            }

            return new
            {
                name = gameObject.name,
                objectId = UnityApiAdapter.GetObjectId(gameObject),
                path = GetHierarchyPath(gameObject),
                scene = gameObject.scene.IsValid() ? gameObject.scene.name : null,
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                tag = SafeGetTag(gameObject),
                layer = gameObject.layer,
                layerName = LayerMask.LayerToName(gameObject.layer),
                isStatic = gameObject.isStatic,
                transform = new
                {
                    localPosition = ToVector3(gameObject.transform.localPosition),
                    localRotationEuler = ToVector3(gameObject.transform.localRotation.eulerAngles),
                    localScale = ToVector3(gameObject.transform.localScale),
                    worldPosition = ToVector3(gameObject.transform.position)
                },
                components = GetComponentTypeNames(gameObject),
                prefab = GetPrefabSummary(gameObject),
                childCount,
                childrenReturned = children.Count,
                childrenTruncated = childCount > children.Count,
                children = children.Count > 0 ? children : null
            };
        }

        static object BuildConsoleSnapshot(SnapshotOptions options)
        {
            try
            {
                var response = ReadConsole.HandleCommand(new ReadConsoleParams
                {
                    Action = ConsoleAction.DiagnosticSummary,
                    Types = new[]
                    {
                        ConsoleLogType.Error,
                        ConsoleLogType.Warning,
                        ConsoleLogType.Exception,
                        ConsoleLogType.Assert
                    },
                    IncludeStacktrace = false,
                    Format = ConsoleOutputFormat.Detailed,
                    MaxIssues = options.MaxConsoleIssues,
                    MaxSamples = Math.Max(4, options.MaxConsoleIssues),
                    TopGroupCount = options.MaxConsoleIssues
                });

                var json = JObject.FromObject(response);
                var data = json["data"];
                if (options.ConsoleSummaryMode == ContextSnapshotConsoleSummaryMode.Compact)
                {
                    data = CompactConsoleDiagnosticSummary(data);
                }

                return new
                {
                    success = json.Value<bool?>("success") ?? false,
                    message = json.Value<string>("message") ?? json.Value<string>("error"),
                    summaryMode = options.ConsoleSummaryMode.ToString(),
                    data,
                    error = json.Value<string>("error")
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    success = false,
                    message = "Console diagnostics are unavailable.",
                    error = ex.Message
                };
            }
        }

        static JToken CompactConsoleDiagnosticSummary(JToken data)
        {
            if (data is not JObject source)
            {
                return data;
            }

            var summary = source["summary"] as JObject;
            if (summary == null)
            {
                return data;
            }

            return new JObject
            {
                ["action"] = source["action"]?.DeepClone(),
                ["marker"] = source["marker"]?.DeepClone(),
                ["totals"] = source["totals"]?.DeepClone(),
                ["summary"] = new JObject
                {
                    ["dominantIssue"] = summary["dominantIssue"]?.DeepClone(),
                    ["criticalIssues"] = summary["criticalIssues"]?.DeepClone() ?? new JArray(),
                    ["warningIssues"] = summary["warningIssues"]?.DeepClone() ?? new JArray(),
                    ["likelySpam"] = summary["likelySpam"]?.DeepClone() ?? new JArray()
                },
                ["omitted"] = new JArray("recentSamples", "timelineHighlights"),
                ["guidance"] = source["guidance"]?.DeepClone()
            };
        }

        static object BuildAssetSnapshot(ProjectIdentity.Snapshot identity, SnapshotOptions options)
        {
            var assetPaths = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                .Where(path => !AssetDatabase.IsValidFolder(path))
                .ToArray();

            var countsByKind = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var countsByExtension = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var recentCandidates = new List<RecentAssetInfo>();

            foreach (var assetPath in assetPaths)
            {
                var extension = Path.GetExtension(assetPath);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = "<none>";
                }

                var kind = DetermineAssetKind(extension);
                countsByKind[kind] = countsByKind.TryGetValue(kind, out var kindCount) ? kindCount + 1 : 1;
                countsByExtension[extension] = countsByExtension.TryGetValue(extension, out var extensionCount) ? extensionCount + 1 : 1;

                var absolutePath = Path.Combine(identity.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
                var lastWriteUtc = DateTime.MinValue;
                try
                {
                    if (File.Exists(absolutePath))
                    {
                        lastWriteUtc = File.GetLastWriteTimeUtc(absolutePath);
                    }
                }
                catch
                {
                    // Missing or inaccessible files are ignored for recency sorting.
                }

                recentCandidates.Add(new RecentAssetInfo
                {
                    Path = assetPath,
                    Kind = kind,
                    Extension = extension,
                    LastWriteUtc = lastWriteUtc
                });
            }

            var recentAssets = recentCandidates
                .OrderByDescending(asset => asset.LastWriteUtc)
                .ThenBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase)
                .Take(options.MaxAssets)
                .Select(asset => new
                {
                    path = asset.Path,
                    guid = AssetDatabase.AssetPathToGUID(asset.Path),
                    kind = asset.Kind,
                    extension = asset.Extension,
                    lastWriteUtc = asset.LastWriteUtc == DateTime.MinValue ? null : asset.LastWriteUtc.ToString("O")
                })
                .ToList();

            return new
            {
                totalAssets = assetPaths.Length,
                countsByKind,
                countsByExtension = countsByExtension
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(25)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                recentAssets,
                recentAssetsLimit = options.MaxAssets,
                note = "Only Assets/ project files are summarized; package internals are intentionally excluded."
            };
        }

        static object BuildToolsSnapshot(SnapshotOptions options)
        {
            var entries = McpToolRegistry.GetAllToolsForSettings();
            var groupCounts = new SortedDictionary<string, ToolGroupCount>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var groups = entry.Groups == null || entry.Groups.Length == 0
                    ? new[] { "uncategorized" }
                    : entry.Groups;

                foreach (var group in groups)
                {
                    if (!groupCounts.TryGetValue(group, out var count))
                    {
                        count = new ToolGroupCount();
                        groupCounts[group] = count;
                    }

                    count.Total++;
                    if (entry.IsEnabled)
                    {
                        count.Enabled++;
                    }
                }
            }

            return new
            {
                total = entries.Length,
                enabled = entries.Count(entry => entry.IsEnabled),
                disabled = entries.Count(entry => !entry.IsEnabled),
                groups = groupCounts.Select(pair => new
                {
                    name = pair.Key,
                    total = pair.Value.Total,
                    enabled = pair.Value.Enabled
                }).ToList(),
                entries = entries
                    .Take(options.MaxTools)
                    .Select(entry => new
                    {
                        name = entry.Info.name,
                        title = entry.Info.title,
                        enabled = entry.IsEnabled,
                        enabledByDefault = entry.IsDefault,
                        groups = entry.Groups
                    })
                    .ToList(),
                truncated = entries.Length > options.MaxTools
            };
        }

        static object BuildWindowSnapshot()
        {
            try
            {
                var focusedWindow = EditorWindow.focusedWindow;
                return Resources.FindObjectsOfTypeAll<EditorWindow>()
                    .Where(window => window != null)
                    .Take(MaxWindowCount)
                    .Select(window => new
                    {
                        title = window.titleContent != null ? window.titleContent.text : window.GetType().Name,
                        type = window.GetType().FullName,
                        isFocused = focusedWindow == window,
                        objectId = UnityApiAdapter.GetObjectId(window),
                        position = new
                        {
                            x = window.position.x,
                            y = window.position.y,
                            width = window.position.width,
                            height = window.position.height
                        }
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                return new[] { new { error = ex.Message } };
            }
        }

        static object BuildHints(SnapshotOptions options, object hierarchy, object console)
        {
            var hints = new List<string>();

            if (EditorApplication.isCompiling)
            {
                hints.Add("Unity is compiling; wait for compilation to finish before making code-dependent changes.");
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                hints.Add("The Editor is in or entering Play Mode; scene changes may be runtime-only unless explicitly saved.");
            }

            if (hierarchy != null && options.IncludeHierarchy)
            {
                hints.Add("Hierarchy is bounded. Increase HierarchyDepth/MaxSceneObjects when deeper object context is needed.");
            }

            if (console != null && options.IncludeConsole)
            {
                hints.Add("Use UniBridge_ReadConsole with ImportantRanges or TimelineWindow for deeper console investigation.");
            }

            hints.Add("Use UniBridge_CaptureView when visual layout or camera framing matters.");
            return hints;
        }

        static List<string> GetComponentTypeNames(GameObject gameObject)
        {
            try
            {
                return gameObject.GetComponents<Component>()
                    .Select(component => component == null ? "<MissingComponent>" : component.GetType().Name)
                    .Take(MaxComponentNames)
                    .ToList();
            }
            catch (Exception ex)
            {
                return new List<string> { $"<component-scan-error: {ex.Message}>" };
            }
        }

        static object GetPrefabSummary(GameObject gameObject)
        {
            try
            {
                var status = PrefabUtility.GetPrefabInstanceStatus(gameObject);
                var assetType = PrefabUtility.GetPrefabAssetType(gameObject);
                var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);

                return new
                {
                    instanceStatus = status.ToString(),
                    assetType = assetType.ToString(),
                    assetPath = string.IsNullOrEmpty(assetPath) ? null : assetPath
                };
            }
            catch
            {
                return null;
            }
        }

        static string SafeGetTag(GameObject gameObject)
        {
            try
            {
                return gameObject.tag;
            }
            catch
            {
                return "<invalid>";
            }
        }

        static string GetHierarchyPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var path = gameObject.name;
            var current = gameObject.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        static object ToVector3(Vector3 value)
        {
            return new
            {
                x = value.x,
                y = value.y,
                z = value.z
            };
        }

        static string DetermineAssetKind(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".cs":
                    return "script";
                case ".prefab":
                    return "prefab";
                case ".unity":
                    return "scene";
                case ".mat":
                    return "material";
                case ".shader":
                case ".shadergraph":
                    return "shader";
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tga":
                case ".psd":
                case ".exr":
                    return "texture";
                case ".wav":
                case ".mp3":
                case ".ogg":
                case ".aiff":
                    return "audio";
                case ".anim":
                case ".controller":
                case ".overridecontroller":
                    return "animation";
                case ".fbx":
                case ".obj":
                case ".gltf":
                case ".glb":
                    return "model";
                case ".asset":
                    return "scriptableObject";
                case ".json":
                case ".txt":
                case ".md":
                case ".xml":
                case ".yaml":
                case ".yml":
                    return "text";
                default:
                    return "other";
            }
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        static string GetProjectRoot()
        {
            var assetsDirectory = new DirectoryInfo(Application.dataPath);
            return assetsDirectory.Parent?.FullName ?? Directory.GetCurrentDirectory();
        }

        static string CreateSnapshotId(ProjectIdentity.Snapshot identity)
        {
            var projectPrefix = string.IsNullOrEmpty(identity.ProjectId)
                ? "unknown"
                : identity.ProjectId.Substring(0, Math.Min(8, identity.ProjectId.Length));
            return $"{projectPrefix}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        }

        sealed class SnapshotOptions
        {
            public ContextSnapshotDepth Depth;
            public bool IncludeConsole;
            public bool IncludeHierarchy;
            public bool IncludeSelection;
            public bool IncludeAssets;
            public bool IncludeTools;
            public bool IncludeWindows;
            public bool IncludeBuildSettings;
            public bool IncludeProjectRoots;
            public bool IncludeProjectSettings;
            public bool IncludePackageDependencies;
            public bool IncludeRegisteredPackageRoots;
            public ContextSnapshotConsoleSummaryMode ConsoleSummaryMode;
            public int HierarchyDepth;
            public int MaxSceneObjects;
            public int MaxAssets;
            public int MaxConsoleIssues;
            public int MaxTools;

            public static SnapshotOptions From(ContextSnapshotParams parameters)
            {
                parameters ??= new ContextSnapshotParams();
                var depth = parameters.Depth;
                var includePackageDependenciesExplicitly = parameters.IncludePackageDependencies == true;

                return new SnapshotOptions
                {
                    Depth = depth,
                    IncludeConsole = parameters.IncludeConsole ?? true,
                    IncludeHierarchy = parameters.IncludeHierarchy ?? true,
                    IncludeSelection = parameters.IncludeSelection ?? true,
                    IncludeAssets = parameters.IncludeAssets ?? true,
                    IncludeTools = parameters.IncludeTools ?? true,
                    IncludeWindows = parameters.IncludeWindows ?? depth == ContextSnapshotDepth.Detailed,
                    IncludeBuildSettings = parameters.IncludeBuildSettings ?? depth == ContextSnapshotDepth.Detailed,
                    IncludeProjectRoots = parameters.IncludeProjectRoots ?? true,
                    IncludeProjectSettings = parameters.IncludeProjectSettings ?? true,
                    IncludePackageDependencies = parameters.IncludePackageDependencies ?? depth != ContextSnapshotDepth.Brief,
                    IncludeRegisteredPackageRoots = includePackageDependenciesExplicitly || depth == ContextSnapshotDepth.Detailed,
                    ConsoleSummaryMode = parameters.ConsoleSummaryMode,
                    HierarchyDepth = Clamp(parameters.HierarchyDepth ?? DefaultHierarchyDepth(depth), 0, MaxHierarchyDepth),
                    MaxSceneObjects = Clamp(parameters.MaxSceneObjects ?? DefaultSceneObjectLimit(depth), 1, 500),
                    MaxAssets = Clamp(parameters.MaxAssets ?? DefaultAssetLimit(depth), 1, 300),
                    MaxConsoleIssues = Clamp(parameters.MaxConsoleIssues ?? DefaultConsoleIssueLimit(depth), 1, 30),
                    MaxTools = Clamp(parameters.MaxTools ?? 120, 1, 300)
                };
            }

            static int DefaultHierarchyDepth(ContextSnapshotDepth depth)
            {
                switch (depth)
                {
                    case ContextSnapshotDepth.Brief:
                        return 0;
                    case ContextSnapshotDepth.Detailed:
                        return 2;
                    default:
                        return 1;
                }
            }

            static int DefaultSceneObjectLimit(ContextSnapshotDepth depth)
            {
                switch (depth)
                {
                    case ContextSnapshotDepth.Brief:
                        return 12;
                    case ContextSnapshotDepth.Detailed:
                        return 120;
                    default:
                        return 40;
                }
            }

            static int DefaultAssetLimit(ContextSnapshotDepth depth)
            {
                switch (depth)
                {
                    case ContextSnapshotDepth.Brief:
                        return 10;
                    case ContextSnapshotDepth.Detailed:
                        return 80;
                    default:
                        return 30;
                }
            }

            static int DefaultConsoleIssueLimit(ContextSnapshotDepth depth)
            {
                switch (depth)
                {
                    case ContextSnapshotDepth.Brief:
                        return 3;
                    case ContextSnapshotDepth.Detailed:
                        return 15;
                    default:
                        return 8;
                }
            }
        }

        sealed class RecentAssetInfo
        {
            public string Path;
            public string Kind;
            public string Extension;
            public DateTime LastWriteUtc;
        }

        sealed class ToolGroupCount
        {
            public int Total;
            public int Enabled;
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }
}
