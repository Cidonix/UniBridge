using System;
using System.Threading;
using Newtonsoft.Json;
namespace UnityEditor { }
namespace UnityEngine { public static class Application { public static string dataPath => ""; } public static class Debug { public static void LogWarning(object value) { } } }
namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    [AttributeUsage(AttributeTargets.Method)] public class McpToolAttribute : Attribute { public McpToolAttribute(string a,string b,string c) {} public string[] Groups { get;set; } public bool EnabledByDefault { get;set; } }
    [AttributeUsage(AttributeTargets.Property)] public class McpDescriptionAttribute : Attribute { public McpDescriptionAttribute(string a) {} public bool Required { get;set; } }
    public static class ToolExecutionScheduler { public static CancellationToken CurrentCancellationToken { get;set; } }
    public class ToolInfo { public string name,description,title; }
    public static class McpToolRegistry { public static ToolInfo[] GetAvailableTools(bool ignoreEnabledState) => new ToolInfo[0]; }
}
namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Resource DTOs contain no Unity converter types. This explicit serializer seam
    // uses the same default resolver/null settings as production McpJson.
    internal static class McpJson { public static JsonSerializer CreateSerializer() => JsonSerializer.Create(); }
}
