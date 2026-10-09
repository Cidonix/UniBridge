using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEngine.SceneManagement;

namespace UnityEngine
{
    public class GameObject { public string AssetPath = ""; }
}

namespace UnityEngine.SceneManagement
{
    public sealed class SnapshotSceneState
    {
        public string Path = "";
        public string Name = "Scene";
        public bool Loaded = true;
        public bool Dirty;
        public bool Valid = true;
        public int Handle;
    }

    public struct Scene
    {
        internal SnapshotSceneState State;
        public string path => State?.Path ?? "";
        public string name => State?.Name ?? "";
        public bool isLoaded => State?.Loaded == true;
        public bool isDirty => State?.Dirty == true;
        public int handle => State?.Handle ?? 0;
        public bool IsValid() => State?.Valid == true;
    }

    public static class SceneManager
    {
        public static readonly List<Scene> Scenes = new();
        public static int ActiveHandle;
        public static bool ActiveSucceeds = true;
        static int nextHandle;
        public static int sceneCount => Scenes.Count;
        public static Scene GetSceneAt(int index) => Scenes[index];
        public static Scene GetActiveScene() => Scenes.FirstOrDefault(scene => scene.handle == ActiveHandle);
        public static bool SetActiveScene(Scene scene)
        {
            UnityEditor.SceneManagement.EditorSceneManager.Operations.Add("active:" + scene.path);
            if (ActiveSucceeds) ActiveHandle = scene.handle;
            return ActiveSucceeds;
        }
        public static Scene Add(string path, bool dirty = false)
        {
            var scene = new Scene { State = new SnapshotSceneState { Path = path, Name = Path.GetFileNameWithoutExtension(path), Dirty = dirty, Handle = ++nextHandle } };
            Scenes.Add(scene);
            if (ActiveHandle == 0) ActiveHandle = scene.handle;
            return scene;
        }
        public static void Reset()
        {
            Scenes.Clear(); ActiveHandle = 0; ActiveSucceeds = true; nextHandle = 0;
        }
    }
}

namespace UnityEditor
{
    public static class EditorApplication
    {
        public static bool isCompiling;
        public static bool isUpdating;
        public static bool isPlayingOrWillChangePlaymode;
    }
    static partial class AssetDatabase
    {
        public static readonly Dictionary<string, UnityEngine.GameObject> Prefabs = new();
        public static bool OpenSucceeds = true;
        public static bool ActivateStage = true;
        public static T LoadAssetAtPath<T>(string path) where T : class => Prefabs.TryGetValue(path ?? "", out var value) ? value as T : null;
        public static bool OpenAsset(UnityEngine.GameObject asset)
        {
            SceneManagement.EditorSceneManager.Operations.Add("prefab:" + asset.AssetPath);
            if (OpenSucceeds && ActivateStage)
                SceneManagement.PrefabStageUtility.Current = new SceneManagement.PrefabStage { assetPath = asset.AssetPath, scene = new Scene { State = new SnapshotSceneState { Valid = true, Loaded = true } } };
            return OpenSucceeds;
        }
    }
}

namespace UnityEditor.SceneManagement
{
    public enum OpenSceneMode { Single, Additive }
    public static class EditorSceneManager
    {
        public static readonly List<string> Operations = new();
        public static Func<Scene, bool> SaveResult;
        public static Func<Scene, bool> CloseResult;
        public static Action<Scene> AfterOpen;
        public static bool ReturnInvalidOpen;
        public static bool SaveScene(Scene scene)
        {
            Operations.Add("save:" + scene.path);
            if (SaveResult != null) return SaveResult(scene);
            scene.State.Dirty = false;
            return true;
        }
        public static Scene OpenScene(string path, OpenSceneMode mode)
        {
            Operations.Add("open:" + mode + ":" + path);
            if (mode == OpenSceneMode.Single)
            {
                foreach (var existing in SceneManager.Scenes) existing.State.Valid = false;
                SceneManager.Scenes.Clear();
            }
            var scene = SceneManager.Add(path);
            AfterOpen?.Invoke(scene);
            return ReturnInvalidOpen ? default : scene;
        }
        public static bool CloseScene(Scene scene, bool removeScene)
        {
            Operations.Add("close:" + scene.path);
            if (CloseResult != null && !CloseResult(scene)) return false;
            SceneManager.Scenes.RemoveAll(item => item.handle == scene.handle);
            scene.State.Valid = false;
            return true;
        }
        public static void Reset()
        {
            Operations.Clear(); SaveResult = null; CloseResult = null; AfterOpen = null; ReturnInvalidOpen = false;
            SceneManager.Reset(); PrefabStageUtility.Current = null;
            UnityEditor.EditorApplication.isCompiling = false;
            UnityEditor.EditorApplication.isUpdating = false;
            UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode = false;
            UnityEditor.AssetDatabase.Prefabs.Clear(); UnityEditor.AssetDatabase.OpenSucceeds = true; UnityEditor.AssetDatabase.ActivateStage = true;
        }
    }
    public sealed class PrefabStage { public string assetPath; public Scene scene; }
    public static class PrefabStageUtility
    {
        public static PrefabStage Current;
        public static PrefabStage GetCurrentPrefabStage() => Current;
    }
    public static class StageUtility
    {
        public static void GoToMainStage()
        {
            EditorSceneManager.Operations.Add("mainStage"); PrefabStageUtility.Current = null;
        }
    }
}

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    [AttributeUsage(AttributeTargets.Property)]
    sealed class McpDescriptionAttribute : Attribute
    {
        public bool Required { get; set; }
        public object Default { get; set; }
        public McpDescriptionAttribute(string description) { }
    }
}

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class EditorSnapshotTool
    {
        // Only unrelated Editor UI restore steps, persistence and summaries are
        // fixtures. Planning, save/close/open execution and responses are copied
        // unchanged from production by SnapshotSource.py.
        internal static bool InjectSelectionWarning;
        public static object RunSnapshotFixture(EditorSnapshotParams parameters) => Restore(parameters);
        static EditorSnapshotData LoadSnapshot(EditorSnapshotParams parameters) => JsonConvert.DeserializeObject<EditorSnapshotData>(parameters.SnapshotJson);
        static object BuildCurrentSummary() => new { loadedScenePaths = GetLoadedScenes().Select(scene => scene.path).ToArray(), activeScenePath = SceneManager.GetActiveScene().path };
        static object BuildTargetSummary(EditorSnapshotData snapshot) => new { loadedScenePaths = snapshot.scenes?.loadedScenes?.Select(scene => scene.path).ToArray() };
        static void RestorePrefabAutoSave(EditorSnapshotData snapshot, List<string> applied, List<string> warnings) { }
        static void RestoreSceneView(EditorSnapshotData snapshot, List<string> applied, List<string> warnings) { }
        static void RestoreSelection(EditorSnapshotData snapshot, List<string> applied, List<string> warnings)
        {
            if (InjectSelectionWarning) warnings.Add("Could not resolve fixture selection.");
        }
        static void RestoreActiveTool(EditorSnapshotData snapshot, List<string> applied, List<string> warnings) { }
        static void RestoreDockTabs(EditorSnapshotData snapshot, List<string> applied, List<string> warnings) { }
        static void RestoreFocusedWindow(EditorSnapshotData snapshot, RestoreOptions options, List<string> applied, List<string> warnings) { }
    }
}
