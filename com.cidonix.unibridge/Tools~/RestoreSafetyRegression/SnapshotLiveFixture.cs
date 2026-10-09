// Transient qualification fixture. Tools~ is ignored by Unity. Root installs a
// guarded copy only in the authorized test project's UniBridge Editor assembly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.RestoreSafetyQualification
{
    public sealed class SnapshotLiveParameters { public string RunId { get; set; } }

    public static class SnapshotLiveFixture
    {
        [McpTool("UniBridge_RestoreSnapshotProbe", "Transient test-project-only snapshot restore safety qualification.",
            ExecutionPolicy = ToolExecutionPolicy.Mutating, Groups = new[] { "core" }, EnabledByDefault = true)]
        public static object Run(SnapshotLiveParameters args)
        {
            if (args == null || !Regex.IsMatch(args.RunId ?? "", "^[a-f0-9]{32}$"))
                return Response.Error("A unique 32-character lowercase hex RunId is required.");
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            if (!string.Equals(projectRoot.Replace('\\', '/').TrimEnd('/'), "H:/Repos/UnityRepos/UniBridge_Test_Project", StringComparison.OrdinalIgnoreCase))
                return Response.Error("This fixture is restricted to UniBridge_Test_Project.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || PrefabStageUtility.GetCurrentPrefabStage() != null)
                return Response.Error("Run only in an idle Editor in Main Stage.");

            var directory = "Assets/__UniBridgeRestoreSnapshot_" + args.RunId;
            var fullDirectory = Path.Combine(projectRoot, directory);
            if (Directory.Exists(fullDirectory) || File.Exists(fullDirectory + ".meta"))
                return Response.Error("Fixture directory already exists; refusing to reuse it.");
            var originals = LoadedScenes().Select(scene => new SceneState(scene)).ToArray();
            var originalActive = SceneManager.GetActiveScene();
            var originalSelection = Selection.objects;
            var originalActiveObject = Selection.activeObject;
            var ownedScenes = new List<Scene>();
            var ownedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<object>();
            var errors = new List<string>();
            var responses = new List<object>();
            var fixtureDiagnostics = new List<object>();
            var restoredAuthors = false;
            try
            {
                Directory.CreateDirectory(fullDirectory);
                AssetDatabase.ImportAsset(directory, ImportAssetOptions.ForceSynchronousImport);
                var a = CreateOwnedScene(directory + "/Target.unity", ownedScenes, originalActive);
                var marker = CreateOwnedMarker(a, "OwnedSnapshotMarker_" + args.RunId);
                var markerId = UnityApiAdapter.GetObjectId(marker);
                var targetSnapshot = Capture();
                foreach (var file in Directory.GetFiles(fullDirectory, "*", SearchOption.AllDirectories))
                    ownedFiles[file] = Hash(file);
                if (File.Exists(fullDirectory + ".meta")) ownedFiles[fullDirectory + ".meta"] = Hash(fullDirectory + ".meta");

                var hasUntitledAuthor = originals.Any(scene => string.IsNullOrWhiteSpace(scene.Path));
                RequireOwnedDirtyScene(a, marker);
                Check(results, "Identical scene set retains dirty target identity", () =>
                {
                    var result = Restore(targetSnapshot, closeExtra: true);
                    responses.Add(result);
                    Require(marker != null && UnityApiAdapter.GetObjectId(marker) == markerId && a.IsValid() && a.isLoaded && a.isDirty, "Retained target was reopened, saved, or lost its object identity.");
                    if (hasUntitledAuthor)
                        Require(!result.Value<bool>("success") && result["data"].Value<string>("status") == "blocked", "Untitled author snapshot identity must block exact scene-set restore.");
                    else
                        Require(result.Value<bool>("success") && result["data"].Value<string>("status") == "completed", "A retained dirty saved target must complete without reopening it.");
                    VerifyOriginals(originals);
                }, hasUntitledAuthor ? "Initial author scene set contains an untitled scene; exact restore correctly blocks without mutation." : null);
                RequireOwnedDirtyScene(a, marker);
                Check(results, "Retained dirty target is not automatically saved", () =>
                {
                    var result = Restore(targetSnapshot, closeExtra: false, saveDirty: true);
                    responses.Add(result);
                    Require(result.Value<bool>("success") && a.isDirty && marker != null && UnityApiAdapter.GetObjectId(marker) == markerId, "SaveDirtyScenes changed a retained target.");
                    VerifyOriginals(originals);
                });

                RequireOwnedDirtyScene(a, marker);
                var b = CreateOwnedScene(directory + "/Extra.unity", ownedScenes, originalActive);
                var extraMarker = CreateOwnedMarker(b, "OwnedExtraMarker_" + args.RunId);
                foreach (var file in Directory.GetFiles(fullDirectory, "*", SearchOption.AllDirectories))
                    if (!ownedFiles.ContainsKey(file)) ownedFiles[file] = Hash(file);
                RequireOwnedDirtyScene(a, marker);
                RequireOwnedDirtyScene(b, extraMarker);
                Check(results, "Dirty extra blocks and preserves every scene", () =>
                {
                    var result = Restore(targetSnapshot, closeExtra: true);
                    responses.Add(result);
                    Require(!result.Value<bool>("success") && result["data"].Value<string>("status") == "blocked", "Dirty extra incorrectly succeeded.");
                    Require(b.IsValid() && b.isLoaded && b.isDirty && extraMarker != null && a.isDirty, "Blocked restore altered owned dirty scenes.");
                    VerifyOriginals(originals);
                });
                RequireOwnedDirtyScene(a, marker);
                RequireOwnedDirtyScene(b, extraMarker);
                Check(results, "Missing target blocks before saving dirty extras", () =>
                {
                    var missingSnapshot = (JObject)targetSnapshot.DeepClone();
                    ((JArray)missingSnapshot["scenes"]["loadedScenes"]).Add(new JObject { ["path"] = directory + "/Missing.unity", ["isLoaded"] = true });
                    var result = Restore(missingSnapshot, closeExtra: true, saveDirty: true);
                    responses.Add(result);
                    Require(!result.Value<bool>("success") && result["data"].Value<string>("status") == "blocked" && b.isDirty && extraMarker != null, "Missing target triggered save/close or false success.");
                    VerifyOriginals(originals);
                });
                if (!hasUntitledAuthor)
                {
                    RequireOwnedDirtyScene(a, marker);
                    RequireOwnedDirtyScene(b, extraMarker);
                    Check(results, "Actual failed save aborts scene restore", () =>
                    {
                        // File sharing denial is confined to our own saved fixture.
                        // Unity cannot replace or write the file while this handle
                        // is open; no ACL or author file is changed.
                        using (new FileStream(Path.Combine(projectRoot, b.path), FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var result = Restore(targetSnapshot, closeExtra: true, saveDirty: true);
                            responses.Add(result);
                            Require(!result.Value<bool>("success") && !result["data"].Value<bool>("restored") && result["data"].Value<string>("status") == "partial", "Failed save must conservatively report a partial attempt.");
                            Require(((JArray)result["data"]["errors"]).Any(item => item.ToString().IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0), "Expected actual save refusal was not observed.");
                        }
                        Require(b.IsValid() && b.isLoaded && b.isDirty && extraMarker != null && a.isDirty, "Save refusal still closed or saved a scene.");
                        VerifyOriginals(originals);
                    });
                }
                else
                {
                    results.Add(new { name = "Actual failed save aborts scene restore", passed = true, skipped = true, reason = "Untitled author scene identity blocks exact scene-set restore before save; deterministic suite independently verifies false SaveScene." });
                }

                RequireOwnedDirtyScene(a, marker);
                RequireOwnedDirtyScene(b, extraMarker);
                var previousHandles = LoadedScenes().Select(scene => scene.handle.ToString()).ToArray();
                // Unity 6000.6 may refuse activation of a newly empty additive
                // scene after a failed save. Let Unity create roots in its own
                // new scene; this never creates a GameObject in an author scene.
                var untitled = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);
                fixtureDiagnostics.Add(new
                {
                    kind = "untitled-construction", valid = untitled.IsValid(), loaded = untitled.isLoaded,
                    handle = untitled.handle.ToString(), path = untitled.path,
                    activeHandle = SceneManager.GetActiveScene().handle.ToString(), previousHandles
                });
                if (originalActive.IsValid() && originalActive.isLoaded) SceneManager.SetActiveScene(originalActive);
                if (untitled.IsValid() && !previousHandles.Contains(untitled.handle.ToString())) ownedScenes.Add(untitled);
                Require(untitled.IsValid() && untitled.isLoaded && string.IsNullOrWhiteSpace(untitled.path)
                    && !previousHandles.Contains(untitled.handle.ToString()), "Unity did not create a new loaded, owned untitled scene; see fixtureDiagnostics.");
                var untitledMarker = untitled.GetRootGameObjects().FirstOrDefault();
                Require(untitledMarker != null && untitledMarker.scene.handle.ToString() == untitled.handle.ToString(), "Unity did not create native roots inside its owned untitled scene.");
                Require(EditorSceneManager.MarkSceneDirty(untitled), "Unity refused to mark its owned untitled scene dirty.");
                RequireOwnedDirtyScene(a, marker);
                RequireOwnedDirtyScene(b, extraMarker);
                RequireOwnedDirtyScene(untitled, untitledMarker);
                Check(results, "Dirty untitled extra cannot be automatically saved or discarded", () =>
                {
                    var result = Restore(targetSnapshot, closeExtra: true, saveDirty: true, allowDirty: true);
                    responses.Add(result);
                    Require(!result.Value<bool>("success") && result["data"].Value<string>("status") == "blocked", "Untitled save override incorrectly succeeded.");
                    Require(untitled.IsValid() && untitled.isLoaded && untitled.isDirty && untitledMarker != null, "Dirty untitled fixture was lost.");
                    Require(b.isDirty && extraMarker != null && a.isDirty, "Untitled blocker failed to stop preceding saves.");
                    VerifyOriginals(originals);
                });
            }
            catch (Exception error) { errors.Add(error.ToString()); }
            finally
            {
                // Scene handles are the ownership boundary; never close a scene
                // found only by path/name or any pre-existing author handle.
                foreach (var scene in ownedScenes.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (scene.IsValid() && scene.isLoaded && !originals.Any(author => author.Handle == scene.handle.ToString()))
                            if (!EditorSceneManager.CloseScene(scene, true)) errors.Add("Could not close owned fixture scene handle " + scene.handle);
                    }
                    catch (Exception error) { errors.Add("Owned scene cleanup: " + error.Message); }
                }
                if (originalActive.IsValid() && originalActive.isLoaded) SceneManager.SetActiveScene(originalActive);
                Selection.objects = originalSelection;
                if (originalActiveObject != null) Selection.activeObject = originalActiveObject;
                try { VerifyOriginals(originals); restoredAuthors = true; }
                catch (Exception error) { errors.Add("Author state preservation: " + error.Message); }

                // Remove only bytes last observed as ours. A concurrently edited
                // fixture is preserved and identified in the response.
                foreach (var pair in ownedFiles.OrderByDescending(pair => pair.Key.Length))
                {
                    try
                    {
                        if (string.Equals(pair.Key, fullDirectory + ".meta", StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(pair.Key))
                        {
                            if (ownedFiles.TryGetValue(pair.Key + ".meta", out var metadataHash) && File.Exists(pair.Key + ".meta") && Hash(pair.Key + ".meta") != metadataHash)
                            {
                                errors.Add("Fixture metadata changed outside tracked cleanup; preserved its asset " + pair.Key);
                                continue;
                            }
                            var primary = pair.Key.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? pair.Key.Substring(0, pair.Key.Length - 5) : null;
                            if (primary != null && File.Exists(primary) && ownedFiles.TryGetValue(primary, out var primaryHash) && Hash(primary) != primaryHash)
                            {
                                errors.Add("Fixture asset changed outside tracked cleanup; preserved its metadata " + pair.Key);
                                continue;
                            }
                            if (Hash(pair.Key) != pair.Value) { errors.Add("Fixture bytes changed outside tracked cleanup; preserved " + pair.Key); continue; }
                            File.Delete(pair.Key);
                        }
                    }
                    catch (Exception error) { errors.Add("Owned file cleanup: " + error.Message); }
                }
                var folderMetadataUnchanged = !File.Exists(fullDirectory + ".meta") || (ownedFiles.TryGetValue(fullDirectory + ".meta", out var currentFolderHash) && Hash(fullDirectory + ".meta") == currentFolderHash);
                if (Directory.Exists(fullDirectory) && !Directory.EnumerateFileSystemEntries(fullDirectory).Any() && folderMetadataUnchanged)
                {
                    Directory.Delete(fullDirectory);
                    if (ownedFiles.TryGetValue(fullDirectory + ".meta", out var folderHash) && File.Exists(fullDirectory + ".meta") && Hash(fullDirectory + ".meta") == folderHash)
                        File.Delete(fullDirectory + ".meta");
                }
                AssetDatabase.Refresh();
            }
            var failed = results.Count(item => !JObject.FromObject(item).Value<bool>("passed"));
            var data = new { runId = args.RunId, cases = results, failedCount = failed, errors, responses, fixtureDiagnostics, authorSceneStatePreserved = restoredAuthors, fixtureDirectory = directory, fixtureDirectoryRemaining = Directory.Exists(fullDirectory), originalScenes = originals };
            return failed == 0 && errors.Count == 0 ? Response.Success("Live snapshot restore safety qualified.", data) : Response.Error("Live snapshot restore safety qualification failed.", data);
        }

        static Scene CreateOwnedScene(string path, List<Scene> owned, Scene originalActive)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            owned.Add(scene);
            if (!EditorSceneManager.SaveScene(scene, path)) throw new InvalidOperationException("Could not save owned fixture scene " + path);
            Require(scene.IsValid() && scene.isLoaded && string.Equals(scene.path.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.Ordinal), "Saved fixture scene does not have the expected loaded identity/path.");
            if (originalActive.IsValid() && originalActive.isLoaded) SceneManager.SetActiveScene(originalActive);
            return scene;
        }
        static GameObject CreateOwnedMarker(Scene scene, string name)
        {
            var active = SceneManager.GetActiveScene();
            try
            {
                if (!SceneManager.SetActiveScene(scene)) throw new InvalidOperationException("Could not activate owned fixture scene.");
                var marker = new GameObject(name);
                Require(marker.scene.handle.ToString() == scene.handle.ToString(), "New marker was not created in its owned fixture scene.");
                Require(EditorSceneManager.MarkSceneDirty(scene), "Unity refused to mark the owned fixture scene dirty.");
                RequireOwnedDirtyScene(scene, marker);
                return marker;
            }
            finally
            {
                if (active.IsValid() && active.isLoaded) SceneManager.SetActiveScene(active);
            }
        }
        static void RequireOwnedDirtyScene(Scene scene, GameObject marker)
        {
            Require(scene.IsValid() && scene.isLoaded && scene.isDirty && marker != null && marker.scene.handle.ToString() == scene.handle.ToString(),
                "Owned fixture setup or preceding case changed the dirty/loaded scene or marker identity. Stopping before another restore.");
        }
        static JObject Capture()
        {
            var result = JObject.FromObject(EditorSnapshotTool.HandleCommand(new EditorSnapshotParams
            {
                Action = EditorSnapshotAction.Capture, Persist = false, IncludeSceneView = false, IncludeSelection = false,
                IncludeWindows = false, IncludeDockTabs = false, IncludePrefabStage = false, IncludePrefabAutoSave = false
            }));
            Require(result.Value<bool>("success"), "Could not capture fixture Editor state.");
            return (JObject)result["data"]["snapshot"];
        }
        static JObject Restore(JObject snapshot, bool closeExtra, bool saveDirty = false, bool allowDirty = false)
        {
            return JObject.FromObject(EditorSnapshotTool.HandleCommand(new EditorSnapshotParams
            {
                Action = EditorSnapshotAction.Restore, SnapshotJson = snapshot.ToString(), RestoreScenes = true,
                RestorePrefabStage = false, RestorePrefabAutoSave = false, RestoreSceneView = false, RestoreSelection = false,
                RestoreActiveTool = false, RestoreDockTabs = false, RestoreFocusedWindow = false,
                CloseExtraScenes = closeExtra, SaveDirtyScenes = saveDirty, AllowDirtySceneReload = allowDirty
            }));
        }
        static void Check(List<object> results, string name, Action body, string detail = null)
        {
            try { body(); results.Add(new { name, passed = true, detail }); }
            catch (Exception error) { results.Add(new { name, passed = false, error = error.ToString() }); }
        }
        static Scene[] LoadedScenes() => Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(scene => scene.IsValid() && scene.isLoaded).ToArray();
        static void VerifyOriginals(SceneState[] originals)
        {
            var scenes = LoadedScenes();
            foreach (var original in originals)
            {
                var current = scenes.FirstOrDefault(scene => scene.handle.ToString() == original.Handle);
                Require(current.IsValid() && current.isLoaded && current.path == original.Path && current.isDirty == original.Dirty, "An author scene handle/path/dirty state changed: " + original.Handle);
            }
        }
        static string Hash(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        public sealed class SceneState
        {
            public string Handle;
            public string Path;
            public bool Dirty;
            public SceneState(Scene scene) { Handle = scene.handle.ToString(); Path = scene.path; Dirty = scene.isDirty; }
        }
    }
}
