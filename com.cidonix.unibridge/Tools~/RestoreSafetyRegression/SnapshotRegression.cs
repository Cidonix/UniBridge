using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using static Cidonix.UniBridge.RestoreSafetyRegression.RestoreSafetyRegression;

namespace Cidonix.UniBridge.RestoreSafetyRegression
{
    public static class SnapshotRegression
    {
        public static void Run(List<CaseResult> cases)
        {
            Check(cases, "Snapshot identical dirty target is never reopened", () =>
            {
                using var f = new SnapshotSandbox(); var scene = SceneManager.Add(f.A, true);
                var response = f.Restore(f.Target(f.A));
                Require(scene.IsValid() && scene.isDirty && SceneManager.GetSceneAt(0).handle == scene.handle, "Dirty scene identity or dirty state was lost.");
                NoOpenClose(); Require(EditorSceneManager.Operations.Count == 0, "An unchanged scene set must perform no scene operations."); Completed(response);
            });
            Check(cases, "Snapshot SaveDirtyScenes does not save a retained author target", () =>
            {
                using var f = new SnapshotSandbox(); var scene = SceneManager.Add(f.A, true);
                var p = f.Target(f.A); p.SaveDirtyScenes = true;
                Completed(f.Restore(p)); Require(scene.isDirty, "A retained author scene was saved."); Require(EditorSceneManager.Operations.Count == 0, "Retained scene was unnecessarily saved or reopened.");
            });
            Check(cases, "Snapshot missing additive target preserves dirty retained scenes", () =>
            {
                using var f = new SnapshotSandbox(); var scene = SceneManager.Add(f.A, true);
                var p = f.Target(f.A, f.B); p.CloseExtraScenes = false;
                Completed(f.Restore(p)); Require(scene.IsValid() && scene.isDirty, "Dirty retained scene was altered.");
                Require(EditorSceneManager.Operations.SequenceEqual(new[] { "open:Additive:" + f.B }), "Missing scene must open once additively.");
            });
            Check(cases, "Snapshot dirty extra blocks before all mutations", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); var extra = SceneManager.Add(f.B, true);
                Blocked(f.Restore(f.Target(f.A))); Require(extra.IsValid() && extra.isDirty, "Dirty extra was lost."); Require(EditorSceneManager.Operations.Count == 0, "Blocked restore mutated scenes.");
            });
            Check(cases, "Snapshot false SaveScene aborts before opening or closing", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); var extra = SceneManager.Add(f.B, true);
                EditorSceneManager.SaveResult = _ => false;
                var p = f.Target(f.C); p.SaveDirtyScenes = true;
                Partial(f.Restore(p)); NoOpenClose(); Require(extra.IsValid() && extra.isDirty, "Failed save lost dirty extra.");
            });
            Check(cases, "Snapshot false SaveScene callback mutation is reported as partial", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); var extra = SceneManager.Add(f.B, true);
                EditorSceneManager.SaveResult = scene => { File.WriteAllText(scene.path, "Save callback mutation before false\n"); return false; };
                var p = f.Target(f.A); p.SaveDirtyScenes = true;
                var response = f.Restore(p); Partial(response); NoOpenClose();
                Require(extra.IsValid() && extra.isDirty && File.ReadAllText(f.B).StartsWith("Save callback mutation"), "The false-save side effect was not exercised.");
                Require(((JArray)response["data"]["applied"]).Count == 0, "Fixture should exercise uncertainty before any acknowledged mutation.");
                Require(response["data"].Value<bool>("mutationAttempted"), "Mutation uncertainty was not retained in the response.");
            });
            Check(cases, "Snapshot true save with dirty scene still aborts", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); var extra = SceneManager.Add(f.B, true);
                EditorSceneManager.SaveResult = _ => true;
                var p = f.Target(f.A); p.SaveDirtyScenes = true;
                Partial(f.Restore(p)); NoOpenClose(); Require(extra.IsValid() && extra.isDirty, "Still-dirty scene was closed.");
            });
            Check(cases, "Snapshot dirty untitled cannot be auto-saved even with discard override", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); var extra = SceneManager.Add("", true);
                var p = f.Target(f.A); p.SaveDirtyScenes = true; p.AllowDirtySceneReload = true;
                Blocked(f.Restore(p)); Require(EditorSceneManager.Operations.Count == 0 && extra.IsValid() && extra.isDirty, "Dirty untitled scene was saved or discarded.");
            });
            Check(cases, "Snapshot dirty untitled default safety includes empty paths", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); var extra = SceneManager.Add("", true);
                Blocked(f.Restore(f.Target(f.A))); Require(EditorSceneManager.Operations.Count == 0 && extra.IsValid(), "Empty-path extra escaped safety checks.");
            });
            Check(cases, "Snapshot explicit dirty discard closes only the extra", () =>
            {
                using var f = new SnapshotSandbox(); var retained = SceneManager.Add(f.A, true); var extra = SceneManager.Add(f.B, true);
                var p = f.Target(f.A); p.AllowDirtySceneReload = true;
                Completed(f.Restore(p)); Require(retained.IsValid() && retained.isDirty && !extra.IsValid(), "Explicit discard affected the wrong scene.");
                Require(EditorSceneManager.Operations.SequenceEqual(new[] { "close:" + f.B }), "Explicit discard reopened a retained scene.");
            });
            Check(cases, "Snapshot untitled snapshot identity cannot be reconstructed", () =>
            {
                using var f = new SnapshotSandbox(); var scene = SceneManager.Add("", true);
                Blocked(f.Restore(f.Target(""))); Require(scene.IsValid() && scene.isDirty && EditorSceneManager.Operations.Count == 0, "Unidentified snapshot caused scene loss.");
            });
            Check(cases, "Snapshot missing target blocks before saving dirty extras", () =>
            {
                using var f = new SnapshotSandbox(); var scene = SceneManager.Add(f.A, true);
                var p = f.Target(Path.Combine(f.Root, "missing.unity")); p.SaveDirtyScenes = true;
                Blocked(f.Restore(p)); Require(scene.isDirty && EditorSceneManager.Operations.Count == 0, "Missing target still saved or closed an author scene.");
            });
            Check(cases, "Snapshot second failed save reports partial and closes nothing", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); var first = SceneManager.Add(f.B, true); var second = SceneManager.Add(f.C, true);
                EditorSceneManager.SaveResult = scene => { if (scene.path == f.C) return false; scene.State.Dirty = false; return true; };
                var p = f.Target(f.A); p.SaveDirtyScenes = true;
                Partial(f.Restore(p)); NoOpenClose(); Require(first.IsValid() && !first.isDirty && second.IsValid() && second.isDirty, "Partial save result concealed incorrect scene mutation.");
            });
            Check(cases, "Snapshot successful saves precede all opens and closes", () =>
            {
                using var f = new SnapshotSandbox(); var extra = SceneManager.Add(f.A, true);
                var p = f.Target(f.B); p.SaveDirtyScenes = true;
                Completed(f.Restore(p)); var trace = EditorSceneManager.Operations;
                Require(trace[0] == "save:" + f.A && trace[1] == "open:Additive:" + f.B && trace[2] == "close:" + f.A, "Scene mutation occurred before saving finished.");
                Require(!extra.IsValid(), "Saved extra was not closed.");
            });
            Check(cases, "Snapshot save callback dirty Prefab Stage blocks closing extra", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); var extra = SceneManager.Add(f.B, true);
                EditorSceneManager.SaveResult = scene =>
                {
                    scene.State.Dirty = false;
                    PrefabStageUtility.Current = new PrefabStage { assetPath = "Assets/CallbackPrefab.prefab", scene = new Scene { State = new SnapshotSceneState { Dirty = true } } };
                    return true;
                };
                var p = f.Target(f.A); p.SaveDirtyScenes = true;
                Partial(f.Restore(p)); NoOpenClose();
                Require(extra.IsValid() && extra.isLoaded && !extra.isDirty && PrefabStageUtility.Current?.scene.isDirty == true, "A save callback dirty Prefab Stage was discarded or extra was closed.");
            });
            Check(cases, "Snapshot save callback dirty Prefab Stage blocks opening missing target", () =>
            {
                using var f = new SnapshotSandbox(); var extra = SceneManager.Add(f.A, true);
                EditorSceneManager.SaveResult = scene =>
                {
                    scene.State.Dirty = false;
                    PrefabStageUtility.Current = new PrefabStage { assetPath = "Assets/CallbackPrefab.prefab", scene = new Scene { State = new SnapshotSceneState { Dirty = true } } };
                    return true;
                };
                var p = f.Target(f.B); p.SaveDirtyScenes = true;
                Partial(f.Restore(p)); NoOpenClose();
                Require(extra.IsValid() && extra.isLoaded && PrefabStageUtility.Current?.scene.isDirty == true && SceneManager.sceneCount == 1, "A save callback dirty stage did not stop opening the missing target.");
            });
            Check(cases, "Snapshot CloseScene false is an error", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); var extra = SceneManager.Add(f.B);
                EditorSceneManager.CloseResult = _ => false;
                Partial(f.Restore(f.Target(f.A))); Require(extra.IsValid(), "Failed close must retain the scene.");
            });
            Check(cases, "Snapshot invalid OpenScene result never closes extras", () =>
            {
                using var f = new SnapshotSandbox(); var extra = SceneManager.Add(f.A); EditorSceneManager.ReturnInvalidOpen = true;
                Partial(f.Restore(f.Target(f.B))); Require(extra.IsValid() && !EditorSceneManager.Operations.Any(op => op.StartsWith("close:")), "Invalid target open still closed original scenes.");
            });
            Check(cases, "Snapshot scene callback dirties an extra and it is preserved", () =>
            {
                using var f = new SnapshotSandbox(); var extra = SceneManager.Add(f.A);
                EditorSceneManager.AfterOpen = _ => extra.State.Dirty = true;
                Partial(f.Restore(f.Target(f.B))); Require(extra.IsValid() && extra.isDirty, "Callback-created unsaved changes were lost.");
                Require(!EditorSceneManager.Operations.Any(op => op.StartsWith("close:")), "Newly dirty extra was closed.");
            });
            Check(cases, "Snapshot callback-created scene is preserved and result is partial", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); Scene callbackScene = default;
                EditorSceneManager.AfterOpen = _ => callbackScene = SceneManager.Add(f.C, true);
                Partial(f.Restore(f.Target(f.B))); Require(callbackScene.IsValid() && callbackScene.isDirty, "An unplanned callback scene was implicitly closed.");
            });
            Check(cases, "Snapshot false SetActiveScene is an error", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); SceneManager.Add(f.B); SceneManager.ActiveSucceeds = false;
                var p = f.Target(f.A, f.B); var json = JObject.Parse(p.SnapshotJson); json["scenes"]["activeScenePath"] = f.B; p.SnapshotJson = json.ToString();
                Partial(f.Restore(p)); Require(SceneManager.GetActiveScene().path == f.A, "Failed active scene transition was concealed.");
            });
            Check(cases, "Snapshot dirty Prefab Stage cannot be replaced", () =>
            {
                using var f = new SnapshotSandbox(); var stageScene = new Scene { State = new SnapshotSceneState { Dirty = true } };
                PrefabStageUtility.Current = new PrefabStage { assetPath = "Assets/OwnedPrefab.prefab", scene = stageScene };
                var p = f.Target(f.A); p.RestoreScenes = false; p.RestorePrefabStage = true; p.SnapshotJson = "{\"snapshotId\":\"fixture\",\"prefabStage\":{\"isOpen\":false}}";
                Blocked(f.Restore(p)); Require(PrefabStageUtility.Current != null && EditorSceneManager.Operations.Count == 0, "Dirty Prefab Stage was discarded.");
            });
            Check(cases, "Snapshot failed Prefab Stage open is an error", () =>
            {
                using var f = new SnapshotSandbox(); const string path = "Assets/OwnedPrefab.prefab";
                AssetDatabase.Prefabs[path] = new UnityEngine.GameObject { AssetPath = path }; AssetDatabase.OpenSucceeds = false;
                var p = f.Target(f.A); p.RestoreScenes = false; p.RestorePrefabStage = true; p.SnapshotJson = "{\"snapshotId\":\"fixture\",\"prefabStage\":{\"isOpen\":true,\"assetPath\":\"" + path + "\"}}";
                Partial(f.Restore(p)); Require(PrefabStageUtility.Current == null, "Failed prefab open claimed activation.");
            });
            Check(cases, "Snapshot missing active scene blocked when opening is disabled", () =>
            {
                using var f = new SnapshotSandbox(); var scene = SceneManager.Add(f.A, true);
                var p = f.Target(f.B); p.OpenMissingScenes = false; p.CloseExtraScenes = false;
                Blocked(f.Restore(p)); Require(scene.isDirty && EditorSceneManager.Operations.Count == 0, "Disabled open still mutated context.");
            });
            Check(cases, "Snapshot compiling Editor blocks before mutation", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); EditorApplication.isCompiling = true;
                Blocked(f.Restore(f.Target(f.A))); Require(EditorSceneManager.Operations.Count == 0, "Compiling Editor was mutated.");
            });
            Check(cases, "Snapshot blocked dry-run reports false and changes nothing", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); SceneManager.Add(f.B, true);
                var p = f.Target(f.A); p.DryRun = true;
                Blocked(f.Restore(p)); Require(EditorSceneManager.Operations.Count == 0, "Blocked dry-run changed state.");
            });
            Check(cases, "Snapshot valid dry-run reports plan without restoring", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A);
                var p = f.Target(f.B); p.DryRun = true; var response = f.Restore(p);
                Require(response.Value<bool>("success") && response["data"].Value<string>("status") == "dry_run" && !response["data"].Value<bool>("restored"), "Dry-run pretended to restore.");
                Require(EditorSceneManager.Operations.Count == 0 && SceneManager.GetSceneAt(0).path == f.A, "Dry-run changed scenes.");
            });
            Check(cases, "Snapshot missing requested UI state warning is not success", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A); EditorSnapshotTool.InjectSelectionWarning = true;
                var p = f.Target(f.A); p.RestoreSelection = true;
                Partial(f.Restore(p)); NoOpenClose();
            });
            Check(cases, "Snapshot target disappearing during restore preserves originals", () =>
            {
                using var f = new SnapshotSandbox(); var original = SceneManager.Add(f.A);
                EditorSceneManager.AfterOpen = _ => File.Delete(f.C);
                Partial(f.Restore(f.Target(f.B, f.C))); Require(original.IsValid() && !EditorSceneManager.Operations.Any(op => op.StartsWith("close:")), "Missing later target still closed originals.");
            });
            Check(cases, "Snapshot duplicate target paths open only once", () =>
            {
                using var f = new SnapshotSandbox(); SceneManager.Add(f.A);
                var p = f.Target(f.A, f.B, f.B); Completed(f.Restore(p));
                Require(EditorSceneManager.Operations.Count(op => op.StartsWith("open:")) == 1 && SceneManager.sceneCount == 2, "Duplicate targets opened duplicate scenes.");
            });
        }

        static void Completed(JObject response)
        {
            Require(response.Value<bool>("success") && response["data"].Value<string>("status") == "completed" && response["data"].Value<bool>("restored"), "Expected truthful completed restore: " + response);
        }
        static void Blocked(JObject response)
        {
            Require(!response.Value<bool>("success") && response["data"].Value<string>("status") == "blocked" && !response["data"].Value<bool>("restored"), "Expected truthful blocked restore: " + response);
        }
        static void Partial(JObject response)
        {
            Require(!response.Value<bool>("success") && response["data"].Value<string>("status") == "partial" && !response["data"].Value<bool>("restored"), "Expected truthful partial restore: " + response);
        }
        static void NoOpenClose() => Require(!EditorSceneManager.Operations.Any(op => op.StartsWith("open:") || op.StartsWith("close:")), "An unsafe restore opened or closed a scene.");

        sealed class SnapshotSandbox : IDisposable
        {
            public readonly string Root;
            public readonly string A;
            public readonly string B;
            public readonly string C;
            public SnapshotSandbox()
            {
                EditorSceneManager.Reset(); EditorSnapshotTool.InjectSelectionWarning = false;
                Root = Path.Combine(FixtureRoot, "snapshot-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
                A = Path.Combine(Root, "OwnedA.unity").Replace('\\', '/'); B = Path.Combine(Root, "OwnedB.unity").Replace('\\', '/'); C = Path.Combine(Root, "OwnedC.unity").Replace('\\', '/');
                foreach (var path in new[] { A, B, C }) File.WriteAllText(path, "Owned snapshot regression fixture\n");
            }
            public EditorSnapshotParams Target(params string[] paths) => new()
            {
                Action = EditorSnapshotAction.Restore,
                SnapshotJson = JObject.FromObject(new { snapshotId = "fixture", scenes = new { activeScenePath = paths.FirstOrDefault(), loadedScenes = paths.Select(path => new { path, isLoaded = true }).ToArray() } }).ToString(),
                RestoreScenes = true, RestorePrefabStage = false, RestorePrefabAutoSave = false, RestoreSceneView = false,
                RestoreSelection = false, RestoreActiveTool = false, RestoreDockTabs = false, RestoreFocusedWindow = false
            };
            public JObject Restore(EditorSnapshotParams parameters)
            {
                var response = JObject.FromObject(EditorSnapshotTool.RunSnapshotFixture(parameters));
                AllResponses.Add(new { snapshot = response, trace = EditorSceneManager.Operations.ToArray(), scenes = SceneManager.Scenes.Select(scene => new { scene.path, scene.handle, scene.isDirty, valid = scene.IsValid() }).ToArray() });
                return response;
            }
            public void Dispose() { EditorSceneManager.Reset(); EditorSnapshotTool.InjectSelectionWarning = false; }
        }
    }
}
