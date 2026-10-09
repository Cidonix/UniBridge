using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.BridgeIsolationRegression;

static class Program
{
    static int Main(string[] args)
    {
        var reportPath = Path.GetFullPath(args[0]);
        var fixtureRoot = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(fixtureRoot);
        var originalSettings = JsonConvert.DefaultSettings;
        var cases = new List<CaseResult>();
        var groups = args[4].Split(',');
        Golden.SessionProduction.Storage = Golden.SnapshotProduction.Storage = Path.Combine(fixtureRoot, "persisted");
        Directory.CreateDirectory(Golden.SessionProduction.Storage);
        JsonFixtures.GoldenDirectory = args[2];
        JsonFixtures.ConfigurePersistedProducers(Golden.SessionProduction.ParseState, Golden.SessionProduction.ParseToken,
            Golden.SessionProduction.ParsePreview, Golden.SessionProduction.ParseSemantic, Golden.DiscoveryProduction.ParseModel,
            Golden.SnapshotProduction.ParseModel, Golden.SessionProduction.PersistState, Golden.SessionProduction.PersistToken,
            Golden.SnapshotProduction.Persist, Golden.SnapshotProduction.Settings);
        TracingRegression.Storage = fixtureRoot;
        DiscoveryRegression.Storage = fixtureRoot;
        try
        {
            if (groups.Contains("json")) JsonRegression.Run(cases);
            if (groups.Contains("routes")) ToolRouteRegression.Run(cases);
            if (groups.Contains("tracing")) TracingRegression.Run(cases);
            if (groups.Contains("json")) DiscoveryRegression.Run(cases);
            if (groups.Contains("lifecycle"))
            {
                var type = typeof(Program).Assembly.GetType("Cidonix.UniBridge.BridgeIsolationRegression.LifecycleRegression", throwOnError: true);
                type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { cases });
            }
            if (groups.Contains("store"))
            {
                var type = typeof(Program).Assembly.GetType("Cidonix.UniBridge.BridgeIsolationRegression.StoreRegression", throwOnError: true);
                type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { cases });
            }
        }
        catch (Exception ex) { cases.Add(new CaseResult { Name = "Regression orchestrator", Passed = false, Detail = ex.ToString() }); }
        finally { JsonConvert.DefaultSettings = originalSettings; }
        Check.Run(cases, "Final exact global callback preservation", () => Check.Assert(ReferenceEquals(JsonConvert.DefaultSettings, originalSettings), "Original callback changed"));
        var failed = cases.Count(item => !item.Passed);
        File.WriteAllText(reportPath, McpJson.SerializeObject(new
        {
            passed = failed == 0, passedCount = cases.Count - failed, failedCount = failed, caseCount = cases.Count,
            groups, cases,
            sourceProvenance = JObject.Parse(File.ReadAllText(args[1])),
            scope = "Unchanged real production methods/files with declared owned adapters; no Unity invocation, authored files, native handles, or editor lifecycle changes",
            nativeHandleQualification = false,
            concurrency = new { JsonRegression.ConcurrentOperationCount, JsonRegression.MaxConcurrentSerializerEntries, JsonRegression.DistinctConcurrentSerializers },
            goldenHashes = JsonRegression.GoldenHashes,
        }, Formatting.Indented));
        Console.WriteLine($"Bridge isolation regression: {cases.Count - failed}/{cases.Count}; report {reportPath}");
        foreach (var item in cases.Where(item => !item.Passed)) Console.WriteLine(item.Name + ": " + item.Detail);
        return failed == 0 ? 0 : 1;
    }
}
