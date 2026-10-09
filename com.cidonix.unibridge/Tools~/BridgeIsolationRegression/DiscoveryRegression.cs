using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using RealDiscovery = Cidonix.UniBridge.MCP.Editor.Helpers.ServerDiscovery;

namespace Cidonix.UniBridge.BridgeIsolationRegression;

static class DiscoveryRegression
{
    public static string Storage;
    public static void Run(List<CaseResult> cases)
    {
        Check.Run(cases, "Actual ServerDiscovery persists protocol/project identity under throwing global factory", () =>
        {
            var root = Path.Combine(Storage, "discovery"); Directory.CreateDirectory(root);
            UnityEngine.Application.dataPath = Path.Combine(root, "owned-project", "Assets"); MCPConstants.StatusDirectory = root;
            var prior = JsonConvert.DefaultSettings; var calls = 0; bool saved;
            var connectionPath = RealDiscovery.GetConnectionPath();
            try { JsonConvert.DefaultSettings = () => { calls++; throw new InvalidOperationException("Owned discovery poison"); }; saved = RealDiscovery.SaveConnectionInfo(connectionPath); }
            finally { JsonConvert.DefaultSettings = prior; }
            var files = Directory.GetFiles(root, "bridge-*.json");
            Check.Assert(saved && calls == 0 && files.Length == 1, "Discovery producer silently lost readiness due to global settings");
            var parsed = JObject.Parse(File.ReadAllText(files[0]));
            Check.Assert(parsed.Value<string>("connection_path") == connectionPath && parsed.Value<string>("project_id") == "owned-discovery-project" && parsed.Value<string>("project_path") == UnityEngine.Application.dataPath && parsed.Value<string>("protocol_version") == "2.0" && parsed.Value<int>("editor_pid") == Environment.ProcessId, "Discovery field names/project identity changed");
            Check.Assert(!File.ReadAllBytes(files[0]).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "Discovery UTF8-no-BOM changed");
            RealDiscovery.DeleteDiscoveryFiles(); Check.Assert(Directory.GetFiles(root).Length == 0, "Actual discovery cleanup left own status");
        });
        Check.Run(cases, "Actual ServerDiscovery returns false for publication I/O failure preserving existing bytes", () =>
        {
            var root = Path.Combine(Storage, "discovery-publication-failure"); Directory.CreateDirectory(root);
            var blocker = Path.Combine(root, "existing-file"); var expected = new byte[] { 3, 7, 11, 19 }; File.WriteAllBytes(blocker, expected); MCPConstants.StatusDirectory = blocker;
            var warningsBefore = McpLog.Warnings.Count;
            Check.Assert(!RealDiscovery.SaveConnectionInfo("owned-no-resource"), "Failed disk publication reported ready/success");
            Check.Assert(File.ReadAllBytes(blocker).AsSpan().SequenceEqual(expected) && McpLog.Warnings.Count == warningsBefore + 1, "Publication failed destructively or hid diagnostic");
        });
    }
}
