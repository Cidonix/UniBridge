using System;
using System.IO;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json;

namespace UnityEngine
{
    public static class Application { public static bool isPlaying; }
    public static class Mathf { public static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value)); }
}
namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    public enum ToolExecutionPolicy { Auto, ReadOnly }
    public static class McpToolRegistry { public static string SanitizeToolName(string name) => name; }
    public static class ToolExecutionScheduler
    {
        public static Action Cancellation;
        public static void ThrowIfCancellationRequested() => Cancellation?.Invoke();
        public static Task YieldIfNotCancelledAsync() { ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class RuntimeProfiler
    {
        const int DefaultTimeoutMs = 30000;
        public static string FixtureStorage;
        public static int ExportWrites;
        public static Task<object> RunCounters(RuntimeProfilerParams parameters) => RecordedCounters(parameters);
        static string SavePayload(object payload, string name)
        {
            Directory.CreateDirectory(FixtureStorage);
            var path = Path.Combine(FixtureStorage, "owned-export-" + (++ExportWrites) + ".json");
            File.WriteAllText(path, McpJson.SerializeObject(payload, Formatting.Indented));
            return path;
        }
    }
}
