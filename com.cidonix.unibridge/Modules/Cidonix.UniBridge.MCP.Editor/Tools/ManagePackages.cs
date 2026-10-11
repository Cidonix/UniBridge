using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static class ManagePackages
    {
        const string ToolName = "UniBridge_ManagePackages";
        static readonly PackageManagementService Service = new PackageManagementService(new UnityPackageManagerAdapter());
        [McpTool(ToolName, "Inspect installed Unity packages, search registry metadata, add an exact registry package version, and list/import samples without overwriting author assets. List/Inspect/Search/ListSamples read package state; Add may reload the domain; ImportSample changes assets. Native request completion is separate from Editor readiness. Never replay Add or ImportSample after timeout/reload: inspect the installed package and wait for Editor readiness first.", "Manage Unity packages and protected samples", Groups = new[] { "core", "packages", "project", "assets" }, EnabledByDefault = true)]
        public static Task<object> HandleCommand(ManagePackagesParams parameters)
            => Service.Execute(parameters, ToolExecutionScheduler.CurrentCancellationToken);

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            var properties = new JObject
            {
                ["Action"] = new JObject { ["type"] = "string", ["enum"] = new JArray("List", "Inspect", "Search", "Add", "ListSamples", "ImportSample"), ["default"] = "List" },
                ["PackageName"] = String(256), ["PackageId"] = String(512), ["Query"] = String(256),
                ["SampleIndex"] = Integer(0, 100000), ["Offline"] = new JObject { ["type"] = "boolean" },
                ["MaxResults"] = Integer(1, 100), ["TimeoutMs"] = Integer(1000, 120000),
                ["MaxSampleFiles"] = Integer(1, 4000), ["MaxSampleBytes"] = Integer(1, 67108864)
            };
            return new JObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        }
        static JObject String(int maximum) => new JObject { ["type"] = "string", ["maxLength"] = maximum };
        static JObject Integer(long minimum, long maximum) => new JObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
    }
}
