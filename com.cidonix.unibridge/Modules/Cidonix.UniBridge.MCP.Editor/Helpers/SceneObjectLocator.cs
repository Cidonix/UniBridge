#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Shared resolver for scene GameObjects. Keeps MCP tools consistent when resolving
    /// ids, hierarchy paths, names, tags, layers, components, inactive objects, and Prefab Mode objects.
    /// </summary>
    public static class SceneObjectLocator
    {
        public sealed class Options
        {
            public bool IncludeInactive { get; set; }
            public bool IncludePrefabStage { get; set; } = true;
            public bool IncludeDontDestroyOnLoad { get; set; }
            public string ScenePath { get; set; }
            public GameObject Root { get; set; }
            public bool SearchInChildren { get; set; }
            public bool MatchContainsFallback { get; set; } = true;
            public bool EditableSceneObjectsOnly { get; set; }
            public bool ExcludeHiddenAndDontSave { get; set; } = true;

            public Options Clone()
            {
                return new Options
                {
                    IncludeInactive = IncludeInactive,
                    IncludePrefabStage = IncludePrefabStage,
                    IncludeDontDestroyOnLoad = IncludeDontDestroyOnLoad,
                    ScenePath = ScenePath,
                    Root = Root,
                    SearchInChildren = SearchInChildren,
                    MatchContainsFallback = MatchContainsFallback,
                    EditableSceneObjectsOnly = EditableSceneObjectsOnly,
                    ExcludeHiddenAndDontSave = ExcludeHiddenAndDontSave
                };
            }
        }

        public static GameObject FindObject(JToken targetToken, string searchMethod, JObject findParams = null)
        {
            var results = FindObjects(targetToken, searchMethod, findAll: false, findParams);
            return results.Count > 0 ? results[0] : null;
        }

        public static List<GameObject> FindObjects(
            JToken targetToken,
            string searchMethod,
            bool findAll,
            JObject findParams = null)
        {
            if (targetToken == null || targetToken.Type == JTokenType.Null)
            {
                return new List<GameObject>();
            }

            var options = new Options
            {
                IncludeInactive = findParams?["search_inactive"]?.ToObject<bool>() ?? false,
                SearchInChildren = findParams?["search_in_children"]?.ToObject<bool>() ?? false
            };

            var searchTerm = findParams?["search_term"]?.ToString() ?? TokenToTarget(targetToken);
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return new List<GameObject>();
            }

            if (targetToken.Type == JTokenType.Integer ||
                (NormalizeSearchMethod(searchMethod, searchTerm) == "by_id" &&
                 long.TryParse(searchTerm, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            {
                findAll = false;
            }

            if (options.SearchInChildren)
            {
                var rootOptions = options.Clone();
                rootOptions.SearchInChildren = false;
                rootOptions.IncludeInactive = true;
                rootOptions.Root = null;
                var root = FindObject(TokenToTarget(targetToken), "by_id_or_name_or_path", rootOptions);
                if (root == null)
                {
                    Debug.LogWarning($"[SceneObjectLocator] Root object '{targetToken}' for child search was not found.");
                    return new List<GameObject>();
                }

                options.Root = root;
            }

            var matches = FindObjects(searchTerm, searchMethod, options);
            if (!findAll && matches.Count > 1)
            {
                return new List<GameObject> { matches[0] };
            }

            return matches;
        }

        public static GameObject FindObject(string target, string searchMethod = null, Options options = null)
        {
            var results = FindObjects(target, searchMethod, options);
            return results.Count > 0 ? results[0] : null;
        }

        public static List<GameObject> FindObjects(string target, string searchMethod = null, Options options = null)
        {
            options ??= new Options();
            if (string.IsNullOrWhiteSpace(target))
            {
                return new List<GameObject>();
            }

            var trimmed = target.Trim();
            var method = NormalizeSearchMethod(searchMethod, trimmed);
            var pool = GetSearchPool(options).ToList();
            var results = new List<GameObject>();

            switch (method)
            {
                case "by_id":
                    if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    {
                        var direct = UnityApiAdapter.GetObjectFromId(id);
                        results.AddRange(ObjectToGameObjects(direct, pool));
                    }
                    break;

                case "by_path":
                    results.AddRange(pool.Where(go => PathEquals(go, trimmed, options.Root)));
                    break;

                case "by_name":
                    results.AddRange(pool.Where(go => string.Equals(go.name, trimmed, StringComparison.Ordinal)));
                    break;

                case "by_tag":
                    results.AddRange(pool.Where(go => string.Equals(SafeTag(go), trimmed, StringComparison.OrdinalIgnoreCase)));
                    break;

                case "by_layer":
                    var layer = int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerIndex)
                        ? layerIndex
                        : LayerMask.NameToLayer(trimmed);
                    if (layer >= 0)
                    {
                        results.AddRange(pool.Where(go => go.layer == layer));
                    }
                    break;

                case "by_component":
                    results.AddRange(pool.Where(go => ComponentIdentity.MatchesAnyComponent(go, trimmed)));
                    break;

                default:
                    results.AddRange(ResolveAutomatically(trimmed, pool, options));
                    break;
            }

            return results.Where(go => go != null).Distinct().ToList();
        }

        public static IEnumerable<GameObject> GetAllSceneObjects(Options options = null)
        {
            options ??= new Options();
            return GetSearchPool(options);
        }

        public static string GetHierarchyPath(GameObject gameObject, bool leadingSlash = false)
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

            var path = string.Join("/", parts);
            return leadingSlash ? "/" + path : path;
        }

        public static string GetRelativeHierarchyPath(GameObject root, GameObject gameObject)
        {
            if (root == null || gameObject == null)
            {
                return null;
            }

            var rootTransform = root.transform;
            var current = gameObject.transform;
            var parts = new Stack<string>();
            while (current != null && current != rootTransform)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return current == rootTransform ? string.Join("/", parts) : null;
        }

        public static List<Scene> GetLoadedScenes(string scenePath = null, bool includeDontDestroyOnLoad = false)
        {
            var scenes = new List<Scene>();
            var normalized = NormalizeScenePath(scenePath);

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(normalized) || SceneMatches(scene, normalized))
                {
                    scenes.Add(scene);
                }
            }

            if (includeDontDestroyOnLoad)
            {
                var runtimeScene = DontDestroyOnLoadSceneCache.GetScene();
                if (runtimeScene.IsValid() &&
                    runtimeScene.isLoaded &&
                    (string.IsNullOrWhiteSpace(normalized) || DontDestroyOnLoadSceneCache.Matches(runtimeScene, normalized)) &&
                    !scenes.Contains(runtimeScene))
                {
                    scenes.Add(runtimeScene);
                }
            }

            return scenes;
        }

        public static bool IsEditableSceneObject(GameObject gameObject)
        {
            return gameObject != null
                   && !EditorUtility.IsPersistent(gameObject)
                   && gameObject.scene.IsValid()
                   && !gameObject.hideFlags.HasFlag(HideFlags.HideAndDontSave);
        }

        public static string NormalizeSearchMethod(string searchMethod, string target = null)
        {
            if (string.IsNullOrWhiteSpace(searchMethod) ||
                string.Equals(searchMethod, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(target) &&
                    long.TryParse(target.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    return "by_id_or_name_or_path";
                }

                return !string.IsNullOrWhiteSpace(target) && target.Contains("/")
                    ? "by_path"
                    : "by_id_or_name_or_path";
            }

            var method = searchMethod.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "");
            return method switch
            {
                "byid" or "id" or "instanceid" => "by_id",
                "bypath" or "path" or "hierarchypath" => "by_path",
                "byname" or "name" => "by_name",
                "bytag" or "tag" => "by_tag",
                "bylayer" or "layer" => "by_layer",
                "bycomponent" or "component" or "componenttype" => "by_component",
                "byidornameorpath" or "idnamepath" or "auto" => "by_id_or_name_or_path",
                _ => "by_id_or_name_or_path"
            };
        }

        static IEnumerable<GameObject> ResolveAutomatically(string target, List<GameObject> pool, Options options)
        {
            if (long.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var direct = UnityApiAdapter.GetObjectFromId(id);
                var byId = ObjectToGameObjects(direct, pool);
                if (byId.Count > 0)
                {
                    return byId;
                }
            }

            var byPath = pool.Where(go => PathEquals(go, target, options.Root)).ToList();
            if (byPath.Count > 0)
            {
                return byPath;
            }

            var byName = pool.Where(go => string.Equals(go.name, target, StringComparison.Ordinal)).ToList();
            if (byName.Count > 0)
            {
                return byName;
            }

            var byNameIgnoreCase = pool.Where(go => string.Equals(go.name, target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byNameIgnoreCase.Count > 0)
            {
                return byNameIgnoreCase;
            }

            return options.MatchContainsFallback
                ? pool.Where(go => go.name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
                : new List<GameObject>();
        }

        static IEnumerable<GameObject> GetSearchPool(Options options)
        {
            if (options.Root != null)
            {
                foreach (var transform in options.Root.GetComponentsInChildren<Transform>(options.IncludeInactive))
                {
                    if (transform != null && ShouldInclude(transform.gameObject, options))
                    {
                        yield return transform.gameObject;
                    }
                }

                yield break;
            }

            var seenScenes = new HashSet<Scene>();
            foreach (var scene in OrderedScenes(options))
            {
                if (!scene.IsValid() || !scene.isLoaded || !seenScenes.Add(scene))
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null)
                    {
                        continue;
                    }

                    foreach (var transform in root.GetComponentsInChildren<Transform>(options.IncludeInactive))
                    {
                        if (transform != null && ShouldInclude(transform.gameObject, options))
                        {
                            yield return transform.gameObject;
                        }
                    }
                }
            }
        }

        static IEnumerable<Scene> OrderedScenes(Options options)
        {
            var prefabStage = options.IncludePrefabStage ? PrefabStageUtility.GetCurrentPrefabStage() : null;
            if (prefabStage != null &&
                prefabStage.scene.IsValid() &&
                prefabStage.scene.isLoaded &&
                SceneMatchesRequestedPath(prefabStage.scene, options.ScenePath))
            {
                yield return prefabStage.scene;
            }

            var active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.isLoaded && SceneMatchesRequestedPath(active, options.ScenePath))
            {
                yield return active;
            }

            if (options.IncludeDontDestroyOnLoad)
            {
                var runtimeScene = DontDestroyOnLoadSceneCache.GetScene();
                if (runtimeScene.IsValid() &&
                    runtimeScene.isLoaded &&
                    SceneMatchesRequestedPath(runtimeScene, options.ScenePath))
                {
                    yield return runtimeScene;
                }
            }

            foreach (var scene in GetLoadedScenes(options.ScenePath, options.IncludeDontDestroyOnLoad))
            {
                yield return scene;
            }
        }

        static bool ShouldInclude(GameObject gameObject, Options options)
        {
            if (gameObject == null)
            {
                return false;
            }

            if (!options.IncludeInactive && !gameObject.activeInHierarchy)
            {
                return false;
            }

            if (options.ExcludeHiddenAndDontSave && gameObject.hideFlags.HasFlag(HideFlags.HideAndDontSave))
            {
                return false;
            }

            return !options.EditableSceneObjectsOnly || IsEditableSceneObject(gameObject);
        }

        static List<GameObject> ObjectToGameObjects(Object obj, List<GameObject> pool)
        {
            GameObject gameObject = null;
            if (obj is GameObject direct)
            {
                gameObject = direct;
            }
            else if (obj is Component component)
            {
                gameObject = component.gameObject;
            }

            if (gameObject == null)
            {
                return new List<GameObject>();
            }

            return pool.Contains(gameObject) ? new List<GameObject> { gameObject } : new List<GameObject>();
        }

        static bool PathEquals(GameObject gameObject, string query, GameObject root)
        {
            if (gameObject == null || string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            var normalizedQuery = NormalizeHierarchyPath(query);
            var path = NormalizeHierarchyPath(GetHierarchyPath(gameObject));
            if (string.Equals(path, normalizedQuery, StringComparison.Ordinal) ||
                string.Equals(path, normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (root == null)
            {
                return false;
            }

            var relative = NormalizeHierarchyPath(GetRelativeHierarchyPath(root, gameObject));
            return !string.IsNullOrWhiteSpace(relative) &&
                   (string.Equals(relative, normalizedQuery, StringComparison.Ordinal) ||
                    string.Equals(relative, normalizedQuery, StringComparison.OrdinalIgnoreCase));
        }

        static string NormalizeHierarchyPath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : path.Replace('\\', '/').Trim('/');
        }

        static string NormalizeScenePath(string scenePath)
        {
            return string.IsNullOrWhiteSpace(scenePath)
                ? null
                : scenePath.Replace('\\', '/').Trim().TrimStart('/');
        }

        static bool SceneMatchesRequestedPath(Scene scene, string scenePath)
        {
            var normalized = NormalizeScenePath(scenePath);
            return string.IsNullOrWhiteSpace(normalized) || SceneMatches(scene, normalized);
        }

        static bool SceneMatches(Scene scene, string normalizedScenePath)
        {
            if (DontDestroyOnLoadSceneCache.Matches(scene, normalizedScenePath))
            {
                return true;
            }

            return string.Equals(scene.name, normalizedScenePath, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scene.path, normalizedScenePath, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scene.path?.TrimStart('/'), normalizedScenePath, StringComparison.OrdinalIgnoreCase);
        }

        static string SafeTag(GameObject gameObject)
        {
            try
            {
                return gameObject != null ? gameObject.tag : "Untagged";
            }
            catch
            {
                return "Untagged";
            }
        }

        static string TokenToTarget(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.String || token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                return token.ToString();
            }

            if (token is JObject obj)
            {
                foreach (var key in new[] { "target", "Target", "name", "Name", "path", "Path", "id", "Id", "instanceId", "InstanceId" })
                {
                    if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var value) && value != null)
                    {
                        return value.ToString();
                    }
                }
            }

            return token.ToString();
        }

    }

    static class DontDestroyOnLoadSceneCache
    {
        static Scene s_Scene;

        static DontDestroyOnLoadSceneCache()
        {
            EditorApplication.playModeStateChanged += _ => s_Scene = default;
        }

        public static Scene GetScene()
        {
            if (!Application.isPlaying)
                return default;

            if (s_Scene.IsValid() && s_Scene.isLoaded)
                return s_Scene;

            var probe = new GameObject("UniBridge_DontDestroyOnLoadSceneProbe")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            try
            {
                Object.DontDestroyOnLoad(probe);
                s_Scene = probe.scene;
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }

            return s_Scene;
        }

        public static bool IsDontDestroyOnLoadScene(Scene scene)
        {
            return scene.IsValid() &&
                   string.IsNullOrEmpty(scene.path) &&
                   scene.name.IndexOf("DontDestroyOnLoad", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool Matches(Scene scene, string scenePath)
        {
            if (!IsDontDestroyOnLoadScene(scene))
                return false;

            if (string.IsNullOrWhiteSpace(scenePath))
                return true;

            var normalized = scenePath.Trim().Trim('/').Replace('\\', '/');
            return string.Equals(normalized, scene.name, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "DontDestroyOnLoad", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "DDOL", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "Runtime", StringComparison.OrdinalIgnoreCase);
        }
    }
}
