using System;
using System.Threading;
using System.Threading.Tasks;
namespace Cidonix.UniBridge.Toolkit
{
    // Compile-only seam. Native request polling uses the real production EditorTask in Editor.
    public static class EditorTask { public static Task Delay(int milliseconds, CancellationToken token) => Task.Delay(milliseconds, token); }
}
namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    public sealed class McpDescriptionAttribute : Attribute { public McpDescriptionAttribute(string text) { } public bool Required; public object Default; }
    public sealed class McpToolAttribute : Attribute { public McpToolAttribute(string name, string description, string title) { } public string[] Groups; public bool EnabledByDefault; }
    public sealed class McpSchemaAttribute : Attribute { public McpSchemaAttribute(string name) { } }
    public static class ToolExecutionScheduler { public static CancellationToken CurrentCancellationToken => CancellationToken.None; }
}
