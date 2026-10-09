using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Tools;
using static Cidonix.UniBridge.RestoreSafetyRegression.RestoreSafetyRegression;

namespace Cidonix.UniBridge.RestoreSafetyRegression
{
    static class WorkSessionRegression
    {
        const string PathA = "Assets/a.txt", PathB = "Assets/b.txt";
        static bool Success(JObject result) => result["success"]?.Value<bool>() == true;
        static string? Status(JObject result) => result["data"]?["status"]?.Value<string>();
        static void Blocked(JObject result) => Require(!Success(result) && Status(result) == "blocked", "Expected truthful blocked response: " + result);
        static void Completed(JObject result) => Require(Success(result) && Status(result) == "completed", "Expected truthful completed response: " + result);

        static object BeginWrite(params string[] paths)
        {
            var method = typeof(WorkSession).GetMethod("BeginWrite", BindingFlags.Static | BindingFlags.Public);
            Require(method != null, "Production BeginWrite API is missing.");
            return method!.Invoke(null, new object[] { paths, "RestoreSafetyRegression/known-payload" })!;
        }

        static JObject CompleteWrite(object token, Dictionary<string, string?> expected)
        {
            var method = typeof(WorkSession).GetMethod("CompleteWrite", BindingFlags.Static | BindingFlags.Public);
            Require(method != null, "Production CompleteWrite API is missing.");
            try { return JObject.FromObject(method!.Invoke(null, new object[] { token, expected })!); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                // Public helper refusal may throw; tool wrappers translate the
                // same refusal to a structured error. Both forbid ownership.
                return new JObject { ["Succeeded"] = false, ["RefusalException"] = error.InnerException.Message };
            }
        }

        static string PayloadHash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        static void OwnedWrite(OwnedProject project, string path, string payload)
        {
            var token = BeginWrite(path);
            project.Write(path, payload);
            var result = CompleteWrite(token, new() { [path] = PayloadHash(payload) });
            Require(result["Succeeded"]?.Value<bool>() == true, "Known payload ownership was not recorded: " + result);
        }

        static void OwnedDelete(OwnedProject project, string path)
        {
            var token = BeginWrite(path);
            File.Delete(project.Absolute(path));
            var result = CompleteWrite(token, new() { [path] = null });
            Require(result["Succeeded"]?.Value<bool>() == true, "Owned deletion was not recorded: " + result);
        }

        static OwnedProject Baseline(params string[] paths)
        {
            var project = new OwnedProject();
            foreach (var path in paths) project.Write(path, "baseline:" + path);
            var result = project.Begin();
            Require(Success(result), "Begin failed: " + result);
            return project;
        }

        static (object State, Array Group, object Recoveries) AddedGroupFixture(OwnedProject project, string path)
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var state = typeof(WorkSession).GetMethod("LoadStateById", flags)!.Invoke(null, new object[] { project.SessionId! })!;
            var options = state.GetType().GetField("Options")!.GetValue(state);
            var changes = typeof(WorkSession).GetMethod("BuildChanges", flags)!.Invoke(null, new object?[] { state, options, int.MaxValue, true })!;
            var all = (System.Collections.IEnumerable)changes.GetType().GetField("All")!.GetValue(changes)!;
            var added = all.Cast<object>().Single(item => item.GetType().GetField("Path")!.GetValue(item)?.ToString() == path);
            var group = Array.CreateInstance(added.GetType(), 1);
            group.SetValue(added, 0);
            var recoveryType = typeof(WorkSession).GetNestedType("AddedRevertGroupRecovery", BindingFlags.NonPublic)!;
            Require(recoveryType != null, "Production recovery group contract is missing.");
            return (state, group, Activator.CreateInstance(typeof(List<>).MakeGenericType(recoveryType!))!);
        }

        internal static void Run(List<CaseResult> results)
        {
            Check(results, "WorkSession unowned modified file is blocked", () =>
            {
                using var project = Baseline(PathA);
                project.Write(PathA, "author concurrent change");
                Blocked(project.Preview(PathA));
                Require(project.Read(PathA) == "author concurrent change", "Preview changed an author file.");
            });
            Check(results, "WorkSession unowned added file is preserved", () =>
            {
                using var project = Baseline();
                project.Write(PathA, "author added file");
                var preview = project.Preview(PathA);
                Blocked(preview);
                Blocked(project.Execute(preview, PathA));
                Require(project.Read(PathA) == "author added file", "Unowned addition was deleted.");
            });
            Check(results, "WorkSession unowned deleted file is not resurrected", () =>
            {
                using var project = Baseline(PathA);
                File.Delete(project.Absolute(PathA));
                Blocked(project.Preview(PathA));
                Require(!project.Exists(PathA), "Author deletion was reverted without ownership.");
            });
            Check(results, "WorkSession known owned modification restores baseline", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned version");
                var preview = project.Preview(PathA);
                Require(Success(preview) && Status(preview) == "preview", "Valid preview did not qualify.");
                Require(preview["data"]?["dryRun"]?.Value<bool>() == true, "Preview dryRun is not truthful.");
                Require(project.Read(PathA) == "owned version", "Dry-run mutated file.");
                Completed(project.Execute(preview, PathA));
                Require(project.Read(PathA) == "baseline:" + PathA, "Baseline bytes not restored exactly.");
            });
            Check(results, "WorkSession known owned added file may be deleted", () =>
            {
                using var project = Baseline();
                OwnedWrite(project, PathA, "owned new file");
                var response = project.Execute(project.Preview(PathA), PathA);
                Completed(response);
                Require(!project.Exists(PathA), "Owned addition was not removed.");
                var recoveryPath = response["data"]?["reverted"]?[0]?["recoveryPath"]?.Value<string>();
                Require(recoveryPath != null && project.Read(recoveryPath) == "owned new file", "Owned addition recovery bytes were not retained.");
            });
            Check(results, "WorkSession known owned deletion restores baseline", () =>
            {
                using var project = Baseline(PathA);
                OwnedDelete(project, PathA);
                Completed(project.Execute(project.Preview(PathA), PathA));
                Require(project.Read(PathA) == "baseline:" + PathA, "Owned deletion was not restored.");
            });
            Check(results, "WorkSession ownership chains successive writes", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "first owned");
                OwnedWrite(project, PathA, "second owned");
                Completed(project.Execute(project.Preview(PathA), PathA));
                Require(project.Read(PathA) == "baseline:" + PathA, "Chained write restore failed.");
            });
            Check(results, "WorkSession author edit after owned write blocks rollback", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned version");
                project.Write(PathA, "author later version");
                Blocked(project.Preview(PathA));
                Require(project.Read(PathA) == "author later version", "Later author edit was overwritten.");
            });
            Check(results, "WorkSession author edit before first owned write keeps conflict", () =>
            {
                using var project = Baseline(PathA);
                project.Write(PathA, "author before agent");
                var token = BeginWrite(PathA);
                project.Write(PathA, "agent after author");
                var ownership = CompleteWrite(token, new() { [PathA] = PayloadHash("agent after author") });
                Require(ownership["Succeeded"]?.Value<bool>() == false, "Preexisting edit was silently adopted.");
                Blocked(project.Preview(PathA));
                Require(project.Read(PathA) == "agent after author", "Guarded preview changed file.");
            });
            Check(results, "WorkSession author edit between owned writes keeps conflict", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "first owned");
                project.Write(PathA, "author between writes");
                var token = BeginWrite(PathA);
                project.Write(PathA, "second agent write");
                var ownership = CompleteWrite(token, new() { [PathA] = PayloadHash("second agent write") });
                Require(ownership["Succeeded"]?.Value<bool>() == false, "Concurrent edit was silently adopted.");
                Blocked(project.Preview(PathA));
            });
            Check(results, "WorkSession expected payload mismatch is not adopted", () =>
            {
                using var project = Baseline(PathA);
                var token = BeginWrite(PathA);
                project.Write(PathA, "observed unexpected bytes");
                var ownership = CompleteWrite(token, new() { [PathA] = PayloadHash("intended different bytes") });
                Require(ownership["Succeeded"]?.Value<bool>() == false, "Unknown observed bytes became owned.");
                Blocked(project.Preview(PathA));
            });
            Check(results, "WorkSession missing expected hash is not adopted", () =>
            {
                using var project = Baseline(PathA);
                var token = BeginWrite(PathA);
                project.Write(PathA, "unknown write");
                var ownership = CompleteWrite(token, new());
                Require(ownership["Succeeded"]?.Value<bool>() == false, "Missing expected payload became owned.");
                Blocked(project.Preview(PathA));
            });
            Check(results, "WorkSession CompleteWrite receipt cannot be replayed", () =>
            {
                using var project = Baseline(PathA);
                var token = BeginWrite(PathA);
                project.Write(PathA, "owned payload");
                Require(CompleteWrite(token, new() { [PathA] = PayloadHash("owned payload") })["Succeeded"]?.Value<bool>() == true, "First write completion failed.");
                project.Write(PathA, "author edit after completion");
                Require(CompleteWrite(token, new() { [PathA] = PayloadHash("author edit after completion") })["Succeeded"]?.Value<bool>() == false, "Completed token adopted an author edit.");
                Blocked(project.Preview(PathA));
            });
            Check(results, "WorkSession receipt committed before token consumption retry stays valid", () =>
            {
                using var project = Baseline(PathA);
                var token = BeginWrite(PathA);
                project.Write(PathA, "owned payload");
                Require(CompleteWrite(token, new() { [PathA] = PayloadHash("owned payload") })["Succeeded"]?.Value<bool>() == true, "Initial receipt failed.");
                var receiptBefore = project.State["OwnedWrites"]?[PathA]?.DeepClone();
                Require(receiptBefore != null && receiptBefore["Conflict"]?.Value<bool>() == false, "Initial valid ownership receipt missing.");
                var tokenId = token.GetType().GetField("TokenId")!.GetValue(token)?.ToString();
                var tokenPath = project.Absolute($"Library/UniBridge/WorkSessions/{project.SessionId}/write-tracking/{tokenId}.json");
                var persisted = JObject.Parse(File.ReadAllText(tokenPath));
                // Models the legitimate receipt-save/token-consumption crash
                // boundary, without substituting production write-chain logic.
                persisted["Completed"] = false;
                File.WriteAllText(tokenPath, persisted.ToString());
                Require(CompleteWrite(token, new() { [PathA] = PayloadHash("owned payload") })["Succeeded"]?.Value<bool>() == false, "Committed write token was admitted again.");
                Require(JToken.DeepEquals(receiptBefore, project.State["OwnedWrites"]?[PathA]), "Replay poisoned the already valid receipt.");
                Require(project.Read(PathA) == "owned payload", "Replay changed current bytes.");
                Completed(project.Execute(project.Preview(PathA), PathA));
                Require(project.Read(PathA) == "baseline:" + PathA, "Untainted committed receipt stopped being revertable.");
            });
            Check(results, "WorkSession crash-boundary retry cannot adopt later author bytes", () =>
            {
                using var project = Baseline(PathA);
                var token = BeginWrite(PathA);
                project.Write(PathA, "owned payload");
                Require(CompleteWrite(token, new() { [PathA] = PayloadHash("owned payload") })["Succeeded"]?.Value<bool>() == true, "Initial receipt failed.");
                var receiptBefore = project.State["OwnedWrites"]?[PathA]?.DeepClone();
                var tokenId = token.GetType().GetField("TokenId")!.GetValue(token)?.ToString();
                var tokenPath = project.Absolute($"Library/UniBridge/WorkSessions/{project.SessionId}/write-tracking/{tokenId}.json");
                var persisted = JObject.Parse(File.ReadAllText(tokenPath));
                persisted["Completed"] = false;
                File.WriteAllText(tokenPath, persisted.ToString());
                project.Write(PathA, "author edit after committed receipt");
                Require(CompleteWrite(token, new() { [PathA] = PayloadHash("author edit after committed receipt") })["Succeeded"]?.Value<bool>() == false, "Retry adopted author bytes after a committed receipt.");
                Require(JToken.DeepEquals(receiptBefore, project.State["OwnedWrites"]?[PathA]), "Retry changed the valid old receipt.");
                Blocked(project.Preview(PathA));
                Require(project.Read(PathA) == "author edit after committed receipt", "Crash-boundary retry or rollback lost author bytes.");
            });
            Check(results, "WorkSession sticky conflict survives later known overwrite", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "first owned");
                project.Write(PathA, "intermediate author bytes");
                var token = BeginWrite(PathA);
                project.Write(PathA, "known second payload");
                Require(CompleteWrite(token, new() { [PathA] = PayloadHash("known second payload") })["Succeeded"]?.Value<bool>() == false, "First conflict was lost.");
                var later = BeginWrite(PathA);
                project.Write(PathA, "known third payload");
                Require(CompleteWrite(later, new() { [PathA] = PayloadHash("known third payload") })["Succeeded"]?.Value<bool>() == false, "Later write erased persistent conflict.");
                Blocked(project.Preview(PathA));
            });
            Check(results, "WorkSession locked before-state never means absent baseline", () =>
            {
                using var project = Baseline(PathA);
                object token;
                using (var locked = new FileStream(project.Absolute(PathA), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    token = BeginWrite(PathA);
                project.Write(PathA, "known payload after unreadable before-state");
                var ownership = CompleteWrite(token, new() { [PathA] = PayloadHash("known payload after unreadable before-state") });
                Require(ownership["Succeeded"]?.Value<bool>() == false, "Unreadable before-state was treated as valid absence.");
                Blocked(project.Preview(PathA));
            });
            Check(results, "WorkSession token from ended session cannot record writes", () =>
            {
                using var project = Baseline(PathA);
                var token = BeginWrite(PathA);
                Require(Success(project.Command("End")), "End failed.");
                project.Write(PathA, "post-end payload");
                var ownership = CompleteWrite(token, new() { [PathA] = PayloadHash("post-end payload") });
                Require(ownership["Succeeded"]?.Value<bool>() == false, "Ended session adopted later write.");
                Require(project.Read(PathA) == "post-end payload", "Completing expired token mutated fixture.");
            });
            Check(results, "WorkSession state project identity mismatch is refused", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned payload");
                var state = project.State;
                state["ProjectRoot"] = project.Root + "-different-project";
                File.WriteAllText(project.StateFile, state.ToString());
                var response = project.Preview(PathA);
                Require(!Success(response), "Foreign project metadata was accepted.");
                Require(project.Read(PathA) == "owned payload", "Foreign project state mutated fixture.");
            });
            Check(results, "WorkSession real execution without reviewed PlanId is blocked", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned payload");
                var response = project.Command("Revert", new JObject { ["Paths"] = new JArray(PathA), ["DryRun"] = false, ["DeleteAddedMetaWithAsset"] = false });
                Blocked(response);
                Require(response["data"]?["dryRun"]?.Value<bool>() == false, "Rejected execution was falsely described as dry-run.");
                Require(project.Read(PathA) == "owned payload", "Unreviewed revert executed.");
            });
            Check(results, "WorkSession stale preview blocks every selected file", () =>
            {
                using var project = Baseline(PathA, PathB);
                OwnedWrite(project, PathA, "owned a");
                OwnedWrite(project, PathB, "owned b");
                var preview = project.Preview(PathA, PathB);
                Require(Success(preview), "Precondition preview failed.");
                project.Write(PathB, "author after preview");
                Blocked(project.Execute(preview, PathA, PathB));
                Require(project.Read(PathA) == "owned a" && project.Read(PathB) == "author after preview", "Stale plan modified a selected target.");
            });
            Check(results, "WorkSession altered plan target selection is blocked", () =>
            {
                using var project = Baseline(PathA, PathB);
                OwnedWrite(project, PathA, "owned a");
                OwnedWrite(project, PathB, "owned b");
                var preview = project.Preview(PathA);
                Blocked(project.Execute(preview, PathA, PathB));
                Require(project.Read(PathA) == "owned a" && project.Read(PathB) == "owned b", "Altered selection executed.");
            });
            Check(results, "WorkSession successful plan is consumed before reuse", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned payload");
                var preview = project.Preview(PathA);
                Completed(project.Execute(preview, PathA));
                OwnedWrite(project, PathA, "next owned payload");
                Blocked(project.Execute(preview, PathA));
                Require(project.Read(PathA) == "next owned payload", "Consumed preview executed again.");
            });
            Check(results, "WorkSession one invalid target blocks complete transaction", () =>
            {
                using var project = Baseline(PathA, PathB);
                OwnedWrite(project, PathA, "owned a");
                project.Write(PathB, "author b");
                var preview = project.Preview(PathA, PathB);
                Blocked(preview);
                Blocked(project.Execute(preview, PathA, PathB));
                Require(project.Read(PathA) == "owned a" && project.Read(PathB) == "author b", "Guarded transaction performed a partial mutation.");
            });
            Check(results, "WorkSession missing selected changed target is blocked", () =>
            {
                using var project = Baseline(PathA, PathB);
                OwnedWrite(project, PathA, "owned a");
                var preview = project.Preview(PathA, PathB);
                Blocked(preview);
                Require(project.Read(PathA) == "owned a", "Unchanged unknown selection caused mutation.");
            });
            Check(results, "WorkSession capture corruption is blocked", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned payload");
                var capture = project.State["Files"]?[PathA]?["CapturePath"]?.Value<string>();
                Require(capture != null, "Baseline capture metadata missing.");
                var absolute = project.Absolute($"Library/UniBridge/WorkSessions/{project.SessionId}/{capture}");
                File.WriteAllText(absolute, "damaged snapshot bytes");
                Blocked(project.Preview(PathA));
                Require(project.Read(PathA) == "owned payload", "Corrupt capture overwrote current file.");
            });
            Check(results, "WorkSession missing capture is blocked", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned payload");
                var capture = project.State["Files"]?[PathA]?["CapturePath"]?.Value<string>();
                File.Delete(project.Absolute($"Library/UniBridge/WorkSessions/{project.SessionId}/{capture}"));
                Blocked(project.Preview(PathA));
                Require(project.Read(PathA) == "owned payload", "Missing capture allowed mutation.");
            });
            Check(results, "WorkSession capture altered after preview is blocked", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned payload");
                var preview = project.Preview(PathA);
                Require(Success(preview), "Valid preview failed.");
                var capture = project.State["Files"]?[PathA]?["CapturePath"]?.Value<string>();
                File.WriteAllText(project.Absolute($"Library/UniBridge/WorkSessions/{project.SessionId}/{capture}"), "corrupt after preview");
                Blocked(project.Execute(preview, PathA));
                Require(project.Read(PathA) == "owned payload", "Late capture corruption overwrote target.");
            });
            Check(results, "WorkSession uncaptured large baseline is blocked", () =>
            {
                using var project = new OwnedProject();
                project.Write(PathA, "baseline larger than budget");
                Require(Success(project.Begin(singleBytes: 1)), "Begin with capture budget failed unexpectedly.");
                OwnedWrite(project, PathA, "owned payload");
                Blocked(project.Preview(PathA));
            });
            Check(results, "WorkSession incomplete baseline scan refuses restore", () =>
            {
                using var project = new OwnedProject();
                for (var i = 0; i < 110; i++) project.Write($"Assets/scan{i:D3}.txt", "baseline " + i);
                var begin = project.Begin(maxFiles: 100);
                var response = project.Preview("Assets/scan000.txt");
                Blocked(response);
                Require(!Success(begin) || project.State["Baseline"]?["ScanComplete"]?.Value<bool>() == false, "Truncated baseline falsely claims completeness.");
                Require(Directory.GetFiles(project.Absolute("Assets")).Length == 110, "Scan modified fixtures.");
            });
            Check(results, "WorkSession incomplete current scan refuses restore", () =>
            {
                using var project = new OwnedProject();
                project.Write(PathA, "baseline:" + PathA);
                Require(Success(project.Begin(maxFiles: 100)), "Small baseline failed.");
                OwnedWrite(project, PathA, "owned a");
                for (var i = 0; i < 110; i++) project.Write($"Assets/new{i:D3}.txt", "author added " + i);
                Blocked(project.Preview(PathA));
                Require(project.Read(PathA) == "owned a", "Incomplete current scan allowed restore.");
            });
            Check(results, "WorkSession legacy baseline cannot claim owned restore", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned payload");
                var state = project.State;
                state["Version"] = 1;
                state.Remove("OwnedWrites");
                File.WriteAllText(project.StateFile, state.ToString());
                Blocked(project.Preview(PathA));
                Require(project.Read(PathA) == "owned payload", "Legacy state reverted without ownership evidence.");
            });
            Check(results, "WorkSession independently edited metadata blocks paired deletion", () =>
            {
                using var project = Baseline();
                OwnedWrite(project, PathA, "owned added asset");
                OwnedWrite(project, PathA + ".meta", "owned metadata");
                project.Write(PathA + ".meta", "author metadata edit");
                var preview = project.Command("Revert", new JObject { ["Paths"] = new JArray(PathA), ["DryRun"] = true, ["DeleteAddedMetaWithAsset"] = true });
                Blocked(preview);
                Require(project.Read(PathA) == "owned added asset" && project.Read(PathA + ".meta") == "author metadata edit", "Metadata conflict deleted independent bytes.");
            });
            Check(results, "WorkSession unsaved loaded scene blocks owned file restore", () =>
            {
                const string scenePath = "Assets/owned-scene.unity";
                using var project = Baseline(scenePath);
                OwnedWrite(project, scenePath, "owned serialized scene");
                var scene = UnityEngine.SceneManagement.SceneManager.Add(scenePath, true);
                Blocked(project.Preview(scenePath));
                Require(scene.IsValid() && scene.isDirty && project.Read(scenePath) == "owned serialized scene", "Unsaved loaded scene was invalidated.");
            });
            Check(results, "WorkSession unsaved scene also blocks metadata restore", () =>
            {
                const string scenePath = "Assets/owned-scene.unity";
                using var project = Baseline(scenePath + ".meta");
                OwnedWrite(project, scenePath + ".meta", "owned scene metadata");
                var scene = UnityEngine.SceneManagement.SceneManager.Add(scenePath, true);
                Blocked(project.Preview(scenePath + ".meta"));
                Require(scene.IsValid() && scene.isDirty, "Metadata restore invalidated dirty scene.");
            });
            Check(results, "WorkSession unsaved Prefab Stage blocks owned prefab restore", () =>
            {
                const string prefabPath = "Assets/owned-prefab.prefab";
                using var project = Baseline(prefabPath);
                OwnedWrite(project, prefabPath, "owned serialized prefab");
                var stageScene = UnityEngine.SceneManagement.SceneManager.Add("", true);
                UnityEditor.SceneManagement.PrefabStageUtility.Current = new UnityEditor.SceneManagement.PrefabStage { assetPath = prefabPath, scene = stageScene };
                Blocked(project.Preview(prefabPath));
                Require(stageScene.IsValid() && stageScene.isDirty, "Dirty Prefab Stage was invalidated.");
            });
            Check(results, "WorkSession dirty loaded asset blocks owned serialized restore", () =>
            {
                const string assetPath = "Assets/owned-material.mat";
                using var project = Baseline(assetPath);
                OwnedWrite(project, assetPath, "owned serialized material");
                var asset = new UnityEngine.GameObject { AssetPath = assetPath };
                UnityEditor.AssetDatabase.Prefabs[assetPath] = asset;
                UnityEditor.EditorUtility.DirtyAssets.Add(asset);
                Blocked(project.Preview(assetPath));
                Require(project.Read(assetPath) == "owned serialized material", "Dirty asset serialized bytes were replaced.");
            });
            Check(results, "WorkSession dirty subasset blocks restore even when main asset is clean", () =>
            {
                const string assetPath = "Assets/owned-container.asset";
                using var project = Baseline(assetPath);
                OwnedWrite(project, assetPath, "owned serialized container");
                var main = new UnityEngine.GameObject { AssetPath = assetPath };
                var subasset = new UnityEngine.GameObject { AssetPath = assetPath };
                UnityEditor.AssetDatabase.Prefabs[assetPath] = main;
                UnityEditor.AssetDatabase.SubAssets[assetPath] = new[] { main, subasset };
                UnityEditor.EditorUtility.DirtyAssets.Add(subasset);
                Blocked(project.Preview(assetPath));
                Require(project.Read(assetPath) == "owned serialized container", "A dirty subasset was overwritten because its main asset was clean.");
            });
            Check(results, "WorkSession unowned companion metadata is not implicitly deleted", () =>
            {
                using var project = Baseline();
                OwnedWrite(project, PathA, "owned added asset");
                project.Write(PathA + ".meta", "independent added metadata");
                var preview = project.Command("Revert", new JObject { ["Paths"] = new JArray(PathA), ["DryRun"] = true, ["DeleteAddedMetaWithAsset"] = true });
                Blocked(preview);
                Require(project.Read(PathA + ".meta") == "independent added metadata", "Unowned meta adopted by association.");
            });
            Check(results, "WorkSession baseline metadata prevents deleting only newly added asset", () =>
            {
                using var project = Baseline(PathA + ".meta");
                OwnedWrite(project, PathA, "owned added asset");
                var preview = project.Command("Revert", new JObject { ["Paths"] = new JArray(PathA), ["DryRun"] = true, ["DeleteAddedMetaWithAsset"] = true });
                Blocked(preview);
                Require(project.Read(PathA) == "owned added asset" && project.Read(PathA + ".meta") == "baseline:" + PathA + ".meta", "Unchanged baseline metadata was deleted by association.");
            });
            Check(results, "WorkSession modified baseline metadata cannot pair with added asset deletion", () =>
            {
                using var project = Baseline(PathA + ".meta");
                OwnedWrite(project, PathA, "owned added asset");
                OwnedWrite(project, PathA + ".meta", "owned changed baseline metadata");
                var preview = project.Command("Revert", new JObject { ["Paths"] = new JArray(PathA), ["DryRun"] = true, ["DeleteAddedMetaWithAsset"] = true });
                Blocked(preview);
                Require(project.Read(PathA) == "owned added asset" && project.Read(PathA + ".meta") == "owned changed baseline metadata", "Mixed added/modified pair was destructively applied.");
            });
            Check(results, "WorkSession metadata-only deletion cannot regenerate retained asset GUID", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA + ".meta", "owned new metadata");
                Blocked(project.Preview(PathA + ".meta"));
                Require(project.Read(PathA) == "baseline:" + PathA && project.Read(PathA + ".meta") == "owned new metadata", "Retained asset lost its metadata GUID.");
            });
            Check(results, "WorkSession folder metadata-only deletion is blocked", () =>
            {
                const string folder = "Assets/retained-folder";
                using var project = Baseline();
                Directory.CreateDirectory(project.Absolute(folder));
                OwnedWrite(project, folder + ".meta", "owned folder metadata");
                Blocked(project.Preview(folder + ".meta"));
                Require(Directory.Exists(project.Absolute(folder)) && project.Read(folder + ".meta") == "owned folder metadata", "Retained folder lost its metadata GUID.");
            });
            Check(results, "WorkSession owned paired asset metadata revert succeeds", () =>
            {
                using var project = Baseline();
                OwnedWrite(project, PathA, "owned added asset");
                OwnedWrite(project, PathA + ".meta", "owned added metadata");
                var preview = project.Command("Revert", new JObject { ["Paths"] = new JArray(PathA), ["DryRun"] = true, ["DeleteAddedMetaWithAsset"] = true });
                Require(Success(preview), "Owned companion metadata did not qualify.");
                var response = project.Command("Revert", new JObject { ["Paths"] = new JArray(PathA), ["DryRun"] = false, ["DeleteAddedMetaWithAsset"] = true, ["PlanId"] = preview["data"]?["planId"]?.DeepClone() });
                Completed(response);
                Require(!project.Exists(PathA) && !project.Exists(PathA + ".meta"), "Owned paired addition remains.");
            });
            Check(results, "WorkSession failed companion move preserves both owned files", () =>
            {
                using var project = Baseline();
                OwnedWrite(project, PathA, "owned added asset");
                OwnedWrite(project, PathA + ".meta", "owned added metadata");
                var preview = project.Command("Revert", new JObject { ["Paths"] = new JArray(PathA), ["DryRun"] = true, ["DeleteAddedMetaWithAsset"] = true });
                Require(Success(preview), "Paired preview failed.");
                // Readers can still hash this metadata; Windows forbids the
                // later rename because the open handle denies delete sharing.
                // The asset may already have moved, so compensation must keep
                // the entire pair rather than expose orphan metadata to Refresh.
                using var metadataLock = new FileStream(project.Absolute(PathA + ".meta"), FileMode.Open, FileAccess.Read, FileShare.Read);
                var response = project.Command("Revert", new JObject { ["Paths"] = new JArray(PathA), ["DryRun"] = false, ["DeleteAddedMetaWithAsset"] = true, ["PlanId"] = preview["data"]?["planId"]?.DeepClone() });
                Require(!Success(response), "Failed companion quarantine was reported successful.");
                Require(Status(response) == "blocked" || Status(response) == "partial", "Failed pair lost truthful result state.");
                Require(project.Read(PathA) == "owned added asset" && project.Read(PathA + ".meta") == "owned added metadata", "Companion failure lost or orphaned fixture bytes.");
                Require(UnityEditor.AssetDatabase.RefreshCount == 0, "Failed paired revert refreshed an orphaned metadata state.");
                Require(UnityEditor.AssetDatabase.AutoRefreshDepth == 0 && UnityEditor.AssetDatabase.DisallowCalls == UnityEditor.AssetDatabase.AllowCalls,
                    "Paired failure leaked automatic refresh suppression.");
            });
            Check(results, "WorkSession new companion after quarantine is preserved before refresh", () =>
            {
                using var project = Baseline();
                OwnedWrite(project, PathA, "owned added asset");
                var receiptBefore = project.State["OwnedWrites"]?[PathA]?.DeepClone();
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var state = typeof(WorkSession).GetMethod("LoadStateById", flags)!.Invoke(null, new object[] { project.SessionId! })!;
                var options = state.GetType().GetField("Options")!.GetValue(state);
                var changes = typeof(WorkSession).GetMethod("BuildChanges", flags)!.Invoke(null, new object?[] { state, options, int.MaxValue, true })!;
                var all = (System.Collections.IEnumerable)changes.GetType().GetField("All")!.GetValue(changes)!;
                var added = all.Cast<object>().Single(item => item.GetType().GetField("Path")!.GetValue(item)?.ToString() == PathA);
                var group = Array.CreateInstance(added.GetType(), 1);
                group.SetValue(added, 0);
                var recoveryType = typeof(WorkSession).GetNestedType("AddedRevertGroupRecovery", BindingFlags.NonPublic)!;
                Require(recoveryType != null, "Production recovery-group contract missing.");
                var recoveries = Activator.CreateInstance(typeof(List<>).MakeGenericType(recoveryType!))!;
                typeof(WorkSession).GetMethod("ExecuteAddedRevertGroupWithRecovery", flags)!
                    .Invoke(null, new object[] { state, group, recoveries });
                Require(!project.Exists(PathA), "Precondition: actual production quarantine did not move the asset.");
                // Deterministic interleave at the actual quarantine/refresh
                // boundary: a new independent companion did not exist in the
                // admitted group. No artificial timing window or retries.
                project.Write(PathA + ".meta", "independent new metadata after asset move");
                var refused = false;
                try
                {
                    typeof(WorkSession).GetMethod("ValidateAddedRevertGroupsBeforeRefresh", flags)!
                        .Invoke(null, new object[] { state, recoveries });
                }
                catch (TargetInvocationException error) when (error.InnerException != null) { refused = true; }
                Require(refused, "New unowned companion was silently admitted to Refresh.");
                Require(project.Read(PathA) == "owned added asset", "Original asset was not compensated before Refresh.");
                Require(project.Read(PathA + ".meta") == "independent new metadata after asset move", "Independent companion was overwritten or deleted.");
                Require(JToken.DeepEquals(receiptBefore, project.State["OwnedWrites"]?[PathA]), "Companion compensation did not restore the prior receipt.");
                Require(UnityEditor.AssetDatabase.RefreshCount == 0, "Unknown new companion was exposed to orphan cleanup.");
            });
            Check(results, "WorkSession group refuses companion newly present before asset move", () =>
            {
                using var project = Baseline();
                OwnedWrite(project, PathA, "owned added asset");
                var f = AddedGroupFixture(project, PathA);
                project.Write(PathA + ".meta", "independent late companion");
                var method = typeof(WorkSession).GetMethod("ExecuteAddedRevertGroupWithRecovery", BindingFlags.Static | BindingFlags.NonPublic);
                Require(method != null, "Production added group helper missing.");
                var refused = false;
                try { method!.Invoke(null, new object[] { f.State, f.Group, f.Recoveries }); }
                catch (TargetInvocationException error) when (error.InnerException != null) { refused = true; }
                Require(refused, "Unknown companion did not block group quarantine.");
                Require(project.Read(PathA) == "owned added asset" && project.Read(PathA + ".meta") == "independent late companion", "Pre-move boundary modified independent bytes.");
            });
            Check(results, "WorkSession metadata group refuses newly present retained base asset", () =>
            {
                using var project = Baseline();
                OwnedWrite(project, PathA + ".meta", "owned orphan metadata");
                var f = AddedGroupFixture(project, PathA + ".meta");
                project.Write(PathA, "independent late base asset");
                var method = typeof(WorkSession).GetMethod("ExecuteAddedRevertGroupWithRecovery", BindingFlags.Static | BindingFlags.NonPublic);
                Require(method != null, "Production added group helper missing.");
                var refused = false;
                try { method!.Invoke(null, new object[] { f.State, f.Group, f.Recoveries }); }
                catch (TargetInvocationException error) when (error.InnerException != null) { refused = true; }
                Require(refused, "Newly present base asset did not protect its metadata.");
                Require(project.Read(PathA) == "independent late base asset" && project.Read(PathA + ".meta") == "owned orphan metadata", "Retained base asset lost its GUID companion.");
            });
            Check(results, "WorkSession late companion compensation is absent from reverted results", () =>
            {
                using var project = Baseline();
                OwnedWrite(project, PathA, "owned first addition");
                OwnedWrite(project, PathB, "owned second addition");
                var preview = project.Preview(PathA, PathB);
                Require(Success(preview), "Initial valid preview failed.");
                var interleaved = false;
                UnityEditor.AssetDatabase.BeforeLoadAll = path =>
                {
                    if (!interleaved && path == PathB && !project.Exists(PathA))
                    {
                        interleaved = true;
                        project.Write(PathA + ".meta", "independent metadata between groups");
                    }
                };
                var response = project.Execute(preview, PathA, PathB);
                Require(interleaved, "Deterministic post-quarantine interleave did not run.");
                Require(!Success(response) && Status(response) == "partial", "Compensated result falsely claimed completion: " + response);
                Require(project.Read(PathA) == "owned first addition" && project.Read(PathA + ".meta") == "independent metadata between groups", "Compensation lost original or independent companion bytes.");
                Require(!project.Exists(PathB), "Independent successful second quarantine did not complete.");
                var reverted = response["data"]?["reverted"] as JArray;
                Require(reverted != null && !reverted.Any(item => item["path"]?.Value<string>() == PathA)
                    && reverted.Any(item => item["path"]?.Value<string>() == PathB), "Compensated member was still reported as reverted.");
                Require(UnityEditor.AssetDatabase.RefreshCount == 0 && UnityEditor.AssetDatabase.AutoRefreshDepth == 0, "Partial compensation refreshed or leaked suppression.");
            });
            Check(results, "WorkSession RevertAll selects only session-owned changes", () =>
            {
                using var project = Baseline(PathA, PathB);
                OwnedWrite(project, PathA, "owned a");
                project.Write(PathB, "author b");
                var preview = project.Command("Revert", new JObject { ["RevertAll"] = true, ["DryRun"] = true, ["DeleteAddedMetaWithAsset"] = false });
                Require(Success(preview), "Owned-only RevertAll preview failed.");
                var response = project.Command("Revert", new JObject { ["RevertAll"] = true, ["DryRun"] = false, ["DeleteAddedMetaWithAsset"] = false, ["PlanId"] = preview["data"]?["planId"]?.DeepClone() });
                Completed(response);
                Require(project.Read(PathA) == "baseline:" + PathA && project.Read(PathB) == "author b", "RevertAll reverted unrelated author bytes.");
            });
            Check(results, "WorkSession late filesystem failure reports partial failure", () =>
            {
                using var project = Baseline(PathA, PathB);
                OwnedWrite(project, PathA, "owned a");
                OwnedWrite(project, PathB, "owned b");
                var preview = project.Preview(PathA, PathB);
                Require(Success(preview), "Valid preview failed.");
                var absoluteB = project.Absolute(PathB);
                File.SetAttributes(absoluteB, FileAttributes.ReadOnly);
                try
                {
                    var response = project.Execute(preview, PathA, PathB);
                    Require(!Success(response) && Status(response) == "partial", "Write failure claimed success or lost partial outcome: " + response);
                    Require(project.Read(PathA) == "baseline:" + PathA && project.Read(PathB) == "owned b", "Partial restore result disagrees with bytes.");
                    var recoveryPath = response["data"]?["reverted"]?[0]?["recoveryPath"]?.Value<string>();
                    Require(recoveryPath != null && project.Read(recoveryPath) == "owned a", "Pre-restore recovery bytes were not retained for the completed part.");
                }
                finally { File.SetAttributes(absoluteB, FileAttributes.Normal); }
            });
            Check(results, "WorkSession all target writes failing never claims completion", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned a");
                var preview = project.Preview(PathA);
                var absolute = project.Absolute(PathA);
                File.SetAttributes(absolute, FileAttributes.ReadOnly);
                try
                {
                    var response = project.Execute(preview, PathA);
                    Require(!Success(response) && (Status(response) == "blocked" || Status(response) == "partial"), "Failed mutation claimed completion: " + response);
                    Require(project.Read(PathA) == "owned a", "Read-only failure changed fixture.");
                }
                finally { File.SetAttributes(absolute, FileAttributes.Normal); }
            });
            Check(results, "WorkSession post-refresh callback changing restored bytes is partial", () =>
            {
                using var project = Baseline(PathA);
                OwnedWrite(project, PathA, "owned before restore");
                var preview = project.Preview(PathA);
                Require(Success(preview), "Initial preview failed.");
                UnityEditor.AssetDatabase.BeforeRefresh = () => project.Write(PathA, "independent callback changed restored bytes");
                var response = project.Execute(preview, PathA);
                Require(!Success(response) && Status(response) == "partial", "Post-refresh changes still claimed completion: " + response);
                Require(project.Read(PathA) == "independent callback changed restored bytes", "Post-refresh independent bytes were overwritten or discarded.");
                Require(UnityEditor.AssetDatabase.AutoRefreshDepth == 0, "Post-refresh failure leaked automatic refresh suppression.");
            });
        }
    }
}
