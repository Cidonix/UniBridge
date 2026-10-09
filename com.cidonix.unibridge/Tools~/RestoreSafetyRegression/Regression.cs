using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Tools;

namespace Cidonix.UniBridge.RestoreSafetyRegression
{
    public sealed class CaseResult
    {
        public string Name = "";
        public bool Passed;
        public string Detail = "";
        public object? Evidence;
    }

    public static class RestoreSafetyRegression
    {
        public static string FixtureRoot = "";
        public static string? Filter;
        public static readonly List<object> AllResponses = new();

        public static int Main(string[] args)
        {
            if (args.Length < 2)
                throw new ArgumentException("Expected report path and production source provenance.");
            var report = Path.GetFullPath(args[0]);
            Filter = args.Length > 2 ? args[2] : null;
            // Production intentionally excludes paths containing /Temp/.
            // Keep fake Assets outside Temp, or the scan would silently skip
            // every fixture and test a different condition from the target.
            FixtureRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UniBridgeRegression", Path.GetFileNameWithoutExtension(report)
                + "-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FixtureRoot);
            File.WriteAllText(Path.Combine(FixtureRoot, "ownership-manifest.json"), JsonConvert.SerializeObject(new
            {
                owner = "UniBridge RestoreSafetyRegression",
                createdUtc = DateTime.UtcNow.ToString("o"),
                root = FixtureRoot,
                scope = "All contents below this unique newly created directory belong to the regression. No author project is accessed."
            }, Formatting.Indented));
            var results = new List<CaseResult>();
            WorkSessionRegression.Run(results);
            // SnapshotRegression is supplied by the independent EditorSnapshot
            // qualification unit, and compiled by the same runner when present.
            var snapshot = typeof(RestoreSafetyRegression).Assembly.GetType("Cidonix.UniBridge.RestoreSafetyRegression.SnapshotRegression");
            snapshot?.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.Invoke(null, new object[] { results });
            var failed = results.Count(item => !item.Passed);
            File.WriteAllText(report, JsonConvert.SerializeObject(new
            {
                passed = failed == 0,
                caseCount = results.Count,
                passedCount = results.Count - failed,
                failedCount = failed,
                fixtureRoot = FixtureRoot,
                sourceProvenance = JObject.Parse(File.ReadAllText(args[1])),
                cases = results,
                responses = AllResponses,
                unitySubstrate = "Deterministic fake Editor state; actual unchanged production filesystem/restore code. This report is not live Unity proof.",
                authorProjectAccessed = false
            }, Formatting.Indented));
            foreach (var result in results)
                Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name}: {result.Detail}");
            Console.WriteLine($"Restore regression: {results.Count - failed}/{results.Count}; report={report}");
            return failed == 0 ? 0 : 1;
        }

        public static void Check(List<CaseResult> results, string name, Action body)
        {
            if (Filter != null && name.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) < 0)
                return;
            var item = new CaseResult { Name = name };
            try { body(); item.Passed = true; item.Detail = "All assertions passed."; }
            catch (Exception error)
            {
                item.Detail = error is TargetInvocationException && error.InnerException != null
                    ? error.InnerException.ToString() : error.ToString();
            }
            results.Add(item);
        }

        public static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    internal sealed class OwnedProject : IDisposable
    {
        internal string Root { get; }
        internal string? SessionId;
        internal readonly Dictionary<string, string?> LastOwned = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<object> Events = new();

        internal OwnedProject()
        {
            Root = Path.Combine(RestoreSafetyRegression.FixtureRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "Assets"));
            UnityEngine.Application.dataPath = Path.Combine(Root, "Assets");
            UnityEditor.AssetDatabase.RefreshCount = 0;
            UnityEditor.AssetDatabase.DisallowCalls = 0;
            UnityEditor.AssetDatabase.AllowCalls = 0;
            UnityEditor.AssetDatabase.AutoRefreshDepth = 0;
            UnityEditor.AssetDatabase.BeforeRefresh = null;
            UnityEditor.AssetDatabase.BeforeLoadAll = null;
            UnityEditor.SceneManagement.EditorSceneManager.Reset();
            UnityEditor.EditorUtility.DirtyAssets.Clear();
            UnityEditor.AssetDatabase.SubAssets.Clear();
            UnityEngine.Debug.Errors.Clear();
            PersistManifest();
        }

        internal string Absolute(string path)
        {
            var full = Path.GetFullPath(Path.Combine(Root, path));
            RestoreSafetyRegression.Require(full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture path escapes owned root.");
            return full;
        }

        internal void Write(string path, string text)
        {
            var target = Absolute(path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text);
            LastOwned[path] = Hash(target);
            Events.Add(new { operation = "write-owned-fixture", path, sha256 = LastOwned[path] });
            PersistManifest();
        }

        internal string Read(string path) => File.ReadAllText(Absolute(path));
        internal bool Exists(string path) => File.Exists(Absolute(path));
        internal static string? Hash(string path) => File.Exists(path)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : null;
        internal string StateFile => Absolute($"Library/UniBridge/WorkSessions/{SessionId}/session.json");
        internal JObject State => JObject.Parse(File.ReadAllText(StateFile));

        internal JObject Command(string action, JObject? args = null)
        {
            args ??= new();
            args["Action"] = action;
            if (SessionId != null) args["SessionId"] = SessionId;
            var result = JObject.FromObject(WorkSession.HandleCommand(args));
            RestoreSafetyRegression.Require(UnityEditor.AssetDatabase.AutoRefreshDepth == 0
                && UnityEditor.AssetDatabase.DisallowCalls == UnityEditor.AssetDatabase.AllowCalls,
                "Command left automatic refresh suppressed or released it without matching admission.");
            var evidence = new { fixture = Root, request = args.DeepClone(), response = result.DeepClone() };
            Events.Add(evidence);
            RestoreSafetyRegression.AllResponses.Add(evidence);
            PersistManifest();
            return result;
        }

        internal JObject Begin(int maxFiles = 12000, long singleBytes = 2097152)
        {
            var result = Command("Begin", new JObject
            {
                ["Name"] = "Restore safety owned fixture",
                ["IncludeSceneSemantics"] = false,
                ["IncludeProjectSettings"] = false,
                ["IncludePackageManifests"] = false,
                ["MaxFiles"] = maxFiles,
                ["MaxSingleCaptureBytes"] = singleBytes
            });
            SessionId = result["data"]?["session"]?["sessionId"]?.Value<string>();
            RestoreSafetyRegression.Require(!string.IsNullOrEmpty(SessionId), "Begin did not return a session id: " + result);
            return result;
        }

        internal JObject Preview(params string[] paths) => Command("Revert", new JObject
        {
            ["Paths"] = new JArray(paths), ["DryRun"] = true,
            ["DeleteAddedMetaWithAsset"] = false
        });

        internal JObject Execute(JObject preview, params string[] paths) => Command("Revert", new JObject
        {
            ["Paths"] = new JArray(paths), ["DryRun"] = false,
            ["PlanId"] = preview["data"]?["planId"]?.DeepClone() ?? JValue.CreateNull(),
            ["DeleteAddedMetaWithAsset"] = false
        });

        internal void PersistManifest() => File.WriteAllText(Path.Combine(Root, "ownership-manifest.json"), JsonConvert.SerializeObject(new
        {
            owner = "UniBridge RestoreSafetyRegression", root = Root, lastOwnedHashes = LastOwned, events = Events
        }, Formatting.Indented));
        public void Dispose()
        {
            UnityEditor.AssetDatabase.BeforeRefresh = null;
            UnityEditor.AssetDatabase.BeforeLoadAll = null;
            UnityEditor.SceneManagement.EditorSceneManager.Reset();
            UnityEditor.EditorUtility.DirtyAssets.Clear();
            UnityEditor.AssetDatabase.SubAssets.Clear();
            PersistManifest();
        }
    }
}
