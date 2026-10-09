using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.BridgeIsolationRegression;

namespace UnityEngine
{
    static class Application { public static string dataPath = "owned Assets"; }
    public readonly struct EntityId
    {
        readonly ulong value;
        EntityId(ulong value) { this.value = value; }
        public static EntityId FromULong(ulong value) => new(value);
        public static ulong ToULong(EntityId value) => value.value;
    }
    public class Object
    {
        public string name; public int Id; public long LongId;
        public int GetInstanceID() => Id;
        public EntityId GetEntityId() => EntityId.FromULong((ulong)(LongId > 0 ? LongId : Id));
    }
    public static class Debug
    {
        public static readonly List<string> Messages = new();
        public static void Log(object value) => Messages.Add(value?.ToString());
        public static void LogWarning(object value) => Messages.Add(value?.ToString());
        public static void LogError(object value) => Messages.Add(value?.ToString());
        public static void LogException(Exception value) => Messages.Add(value?.Message);
    }
}
namespace UnityEditor
{
    public sealed class InitializeOnLoadMethodAttribute : Attribute { }
    static class EditorUserSettings
    {
        public static readonly Dictionary<string, string> Values = new();
        public static string GetConfigValue(string key) => Values.TryGetValue(key, out var value) ? value : null;
        public static void SetConfigValue(string key, string value) => Values[key] = value;
    }
    static class AssetDatabase
    {
        public static bool Contains(UnityEngine.Object obj) => false;
        public static string GetAssetPath(UnityEngine.Object obj) => "";
        public static UnityEngine.Object LoadAssetAtPath(string path, Type type) => null;
    }
    static class EditorUtility
    {
        public static readonly Dictionary<int, UnityEngine.Object> Objects = new();
        public static readonly Dictionary<long, UnityEngine.Object> EntityObjects = new();
        public static int? LastRequestedId;
        public static long? LastRequestedObjectId;
        public static UnityEngine.Object InstanceIDToObject(int id)
        {
            LastRequestedId = id; LastRequestedObjectId = id;
            return Objects.TryGetValue(id, out var obj) ? obj : null;
        }
        public static UnityEngine.Object EntityIdToObject(UnityEngine.EntityId id)
        {
            var value = checked((long)UnityEngine.EntityId.ToULong(id)); LastRequestedObjectId = value;
            LastRequestedId = value <= int.MaxValue ? (int)value : null;
            if (EntityObjects.TryGetValue(value, out var obj)) return obj;
            return value <= int.MaxValue && Objects.TryGetValue((int)value, out obj) ? obj : null;
        }
    }
}
namespace UnityEditor.Search { static class Dispatcher { public static void Enqueue(Action action) => action(); } }
namespace Cidonix.UniBridge.MCP.Editor.Settings { sealed class SettingsNamespaceFixture { } }
namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    static class MCPConstants { public static string StatusDirectory; }
    static class ProjectIdentity
    {
        public static ProjectIdentityData GetOrCreate() => new();
    }
    class ProjectIdentityData
    {
        public string ProjectId => "owned-discovery-project";
        public string ProjectName => "Owned discovery";
        public string ProjectRoot => System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);
    }
    static class McpLog
    {
        public static readonly List<string> Warnings = new();
        public static void Log(object value) { }
        public static void ClearOnceKeys() { }
        public static void LogDelayed(object value, Cidonix.UniBridge.BridgeIsolationRegression.LogType type = Cidonix.UniBridge.BridgeIsolationRegression.LogType.Log) { }
        public static void Warning(string value) => Warnings.Add(value);
    }
}
namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    public enum ToolExecutionPolicy { Auto, ReadOnly, Observer, Mutating, Lifecycle, Capture }
    static class SchemaGenerator
    {
        public static object GenerateSchema(Type type) => throw new NotSupportedException("Schema generation is outside this binding qualification");
        public static object GenerateOutputSchemaFromMethod(object instance, string method = "ExecuteAsync", Type[] args = null) => throw new NotSupportedException("Schema generation is outside this binding qualification");
    }
    static class McpToolRegistry
    {
        public enum ToolChangeType { Refreshed, Added, Removed }
        public sealed class ToolChangeEventArgs { public ToolChangeType ChangeType; public string ToolName; }
        public static event Action<ToolChangeEventArgs> ToolsChanged;
        public static int ToolSubscriptions => ToolsChanged?.GetInvocationList().Length ?? 0;
        public static McpToolInfo[] Tools = new[] { new McpToolInfo { name = "Owned_Golden", description = "Owned tool", inputSchema = new { type = "object" } } };
        public static McpToolInfo[] GetAvailableTools() { LifeFault.At("tools"); return Tools; }
        public static string SanitizeToolName(string name) => name;
        public static void Reset() { ToolsChanged = null; }
    }
}
namespace Cidonix.UniBridge.Tracing { static class Trace { public static void Warn(string value) => UnityEngine.Debug.LogWarning(value); } }
