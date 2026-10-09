using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

// Only the Unity Editor substrate is substituted. The entire production
// WorkSession implementation and Response formatter are compiled unchanged.
namespace UnityEngine
{
    static class Application
    {
        internal static string dataPath = "";
        internal static string unityVersion = "restore-regression-substrate";
    }
    static class Debug
    {
        internal static readonly List<string> Errors = new();
        public static void LogError(object message) => Errors.Add(message?.ToString() ?? "null");
        public static void LogWarning(object message) { }
        public static void Log(object message) { }
    }
}

namespace UnityEditor
{
    enum ImportAssetOptions { ForceUpdate = 1 }
    static partial class AssetDatabase
    {
        internal static int RefreshCount;
        internal static int DisallowCalls, AllowCalls, AutoRefreshDepth;
        internal static readonly Dictionary<string, UnityEngine.GameObject[]> SubAssets = new();
        internal static Action? BeforeRefresh;
        internal static Action<string>? BeforeLoadAll;
        public static void Refresh(ImportAssetOptions options)
        {
            BeforeRefresh?.Invoke();
            RefreshCount++;
        }
        public static UnityEngine.GameObject LoadMainAssetAtPath(string path) => Prefabs.TryGetValue(path, out var value) ? value : null;
        public static UnityEngine.GameObject[] LoadAllAssetsAtPath(string path)
        {
            BeforeLoadAll?.Invoke(path);
            if (SubAssets.TryGetValue(path, out var values)) return values;
            return Prefabs.TryGetValue(path, out var main) ? new[] { main } : Array.Empty<UnityEngine.GameObject>();
        }
        public static void DisallowAutoRefresh() { DisallowCalls++; AutoRefreshDepth++; }
        public static void AllowAutoRefresh()
        {
            AllowCalls++; AutoRefreshDepth--;
            if (AutoRefreshDepth < 0) throw new InvalidOperationException("Automatic refresh suppression was released without matching admission.");
        }
    }
    static class EditorUtility
    {
        internal static readonly HashSet<object> DirtyAssets = new();
        public static bool IsDirty(object asset) => DirtyAssets.Contains(asset);
    }
}

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    [AttributeUsage(AttributeTargets.Method)]
    sealed class McpSchemaAttribute : Attribute
    {
        public McpSchemaAttribute(string name) { }
    }
    [AttributeUsage(AttributeTargets.Method)]
    sealed class McpToolAttribute : Attribute
    {
        public string[]? Groups { get; set; }
        public bool EnabledByDefault { get; set; }
        public McpToolAttribute(string name, string description, string title) { }
    }
}

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class WorkSession
    {
        sealed class SessionSemanticBaseline
        {
            public bool Enabled;
            public int SceneCount;
            public int ObjectCount;
            public bool Truncated;
        }
        static SessionSemanticBaseline CaptureSemanticBaseline(ScanOptions options, string sessionId, out List<string> warnings)
        {
            if (options.IncludeSceneSemantics)
                throw new InvalidOperationException("This filesystem suite must not request semantic Unity scene capture.");
            warnings = new();
            return null;
        }
        static object BuildSemanticReview(SessionState state, bool include, int maxChanges, bool lightweight = false)
        {
            if (include && state.SemanticBaseline != null)
                throw new InvalidOperationException("This filesystem suite must not request semantic Unity scene review.");
            return new { enabled = false };
        }
    }
}
