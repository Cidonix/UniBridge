using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Cidonix.UniBridge.MCP.Editor.Helpers;
namespace Cidonix.UniBridge.Toolkit
{
    public static class EditorTask { public static Task Delay(int milliseconds, CancellationToken token) => Task.Delay(milliseconds, token); public static Task Yield() => Task.CompletedTask; }
}
namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    public sealed class McpDescriptionAttribute : Attribute { public McpDescriptionAttribute(string text) { } public bool Required; public object Default; }
    public sealed class McpToolAttribute : Attribute
    {
        public McpToolAttribute(string name, string description, string title) { Name = name; }
        public string Name;
        public string[] Groups; public bool EnabledByDefault, ReplaySafe; public ToolExecutionPolicy ExecutionPolicy;
    }
    public sealed class McpSchemaAttribute : Attribute { public McpSchemaAttribute(string name) { } }
    public interface IToolHandler { McpToolAttribute Attribute { get; } }
}
namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    public static class McpLog { public static void Log(string message) { } }
    // No native adapter methods are exercised by the input schema's static constructor.
    internal sealed class UnityPackageManagerAdapter : IUnityPackageManager
    {
        public string ProjectRoot => throw new NotSupportedException(); public double Now => throw new NotSupportedException();
        public PackageEditorObservation ObserveEditor(bool mutation) => throw new NotSupportedException();
        public Task Delay(int milliseconds, CancellationToken token) => throw new NotSupportedException();
        public IPackageRequest List(bool offline) => throw new NotSupportedException();
        public IPackageRequest Search(string name, bool offline) => throw new NotSupportedException();
        public IPackageRequest Add(string id) => throw new NotSupportedException();
        public PackageSampleHandle[] Samples(string name, string version) => throw new NotSupportedException();
        public string ResolveGuid(string guid) => throw new NotSupportedException();
    }
}
namespace Cidonix.UniBridge.MCP.Runtime.Serialization
{
    // UnityValueSerialization references this only for CreateConverters(true), which this suite never calls.
    sealed class UnityEngineObjectConverter : JsonConverter
    {
        public override bool CanConvert(Type type) => throw new NotSupportedException();
        public override object ReadJson(JsonReader reader, Type type, object value, JsonSerializer serializer) => throw new NotSupportedException();
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new NotSupportedException();
    }
}
