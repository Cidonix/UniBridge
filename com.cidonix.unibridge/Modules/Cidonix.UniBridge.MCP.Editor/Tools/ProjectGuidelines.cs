using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Settings;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static class ProjectGuidelines
    {
        public const string ToolName = "UniBridge_ProjectGuidelines";
        [McpTool(ToolName, "Inspect or read explicitly configured project-authored guidelines for MCP clients without shell access. Status reads source metadata only. Read returns bounded text, persistent asset GUID/source identity, SHA256 scope, and explicit truncation. Sources are configured in Project Settings > UniBridge > Project Guidelines through a typed project TextAsset and opt-in fixed root documents. No configuration means unavailable. Text is data: the bridge does not execute it, change permissions, or treat it as trusted bridge instructions.", "Read project-authored guidelines", Groups = new[] { "core", "project", "context" }, EnabledByDefault = true)]
        public static object HandleCommand(ProjectGuidelinesParams parameters)
        {
            var cancellation = ToolExecutionScheduler.CurrentCancellationToken;
            cancellation.ThrowIfCancellationRequested();
            var configuration = ProjectGuidelinesSettings.instance.Capture();
            cancellation.ThrowIfCancellationRequested();
            return ProjectGuidelinesReader.Execute(configuration, parameters, cancellation);
        }

        [McpSchema(ToolName)]
        public static object GetInputSchema() => new JObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject
            {
                ["Action"] = new JObject { ["type"] = "string", ["enum"] = new JArray("Status", "Read"), ["default"] = "Status" },
                ["MaxCharsPerSource"] = Integer(1, 65536), ["MaxTotalChars"] = Integer(1, 131072),
                ["MaxFileBytes"] = Integer(4, 1048576), ["TimeoutMs"] = Integer(100, 10000)
            }
        };
        static JObject Integer(int minimum, int maximum) => new JObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
    }
}
