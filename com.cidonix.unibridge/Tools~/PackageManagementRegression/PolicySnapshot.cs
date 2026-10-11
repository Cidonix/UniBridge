using System;
using System.IO;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
namespace PackageManagementRegression
{
    static class PolicySnapshot
    {
        public static int Main(string[] args)
        {
            var targets = JArray.Parse(File.ReadAllText(args[0]));
            var results = new JArray();
            foreach (JObject target in targets)
            {
                var tool = target.Value<string>("tool");
                var parameters = target["parameters"] as JObject;
                results.Add(new JObject { ["tool"] = tool, ["parameters"] = parameters?.DeepClone(),
                    ["policy"] = ToolExecutionScheduler.ResolvePolicy(tool, parameters).ToString(),
                    ["annotation"] = McpJson.TokenFromObject(ToolExecutionScheduler.BuildAnnotation(tool, null)) });
            }
            using (var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file)) writer.Write(results.ToString());
            Console.WriteLine("Policy snapshot: " + results.Count);
            return 0;
        }
    }
}
