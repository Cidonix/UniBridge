using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UniBridge.SerializedNumericRegression
{
    public static class SerializedNumericNativeFixture
    {
        static string run, folder, assetPath, assetGuid, journal, pointerHash, lastAssetHash, lastMetaHash, lastFolderMetaHash;
        static Scene ownScene;
        static GameObject ownObject;
        static SerializedNumericComponent component;
        static JObject originalScenes;
        static Object[] originalSelection;
        static int observations, requests;
        static string Root => Path.GetDirectoryName(Application.dataPath);
        static string EvidenceRoot => Path.Combine(Root, "Library", "AgentValidation", "SerializedNumericRegression");
        static string Pointer => Path.Combine(EvidenceRoot, "active.json");

        [MenuItem("Tools/UniBridge Validation/Serialized Numeric/Prepare")]
        public static void Prepare()
        {
            if (run != null || File.Exists(Pointer)) throw new InvalidOperationException("An existing numeric fixture is preserved; Finish it before a new run.");
            originalScenes = SceneState(); originalSelection = Selection.objects;
            run = Guid.NewGuid().ToString("N");
            journal = Path.Combine(EvidenceRoot, run); Directory.CreateDirectory(journal);
            folder = "Assets/UniBridgeNumericFixture_" + run;
            if (Directory.Exists(Path.Combine(Root, folder)) || File.Exists(Path.Combine(Root, folder + ".meta"))) throw new IOException("Own unique folder collision.");
            Directory.CreateDirectory(EvidenceRoot);
            WriteNew(Pointer, new JObject { ["run"] = run, ["journal"] = journal, ["folder"] = folder });
            pointerHash = Hash(Pointer);
            Directory.CreateDirectory(Path.Combine(Root, folder)); AssetDatabase.ImportAsset(folder);
            assetPath = folder + "/OwnedNumbers.asset";
            var asset = ScriptableObject.CreateInstance<SerializedNumericAsset>();
            asset.name = "OwnNumericAsset_" + run;
            AssetDatabase.CreateAsset(asset, assetPath); // Writes only this newly owned asset; never SaveAssets.
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            RememberOwnHashes();
            ownScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            ownObject = new GameObject("OwnNumericObject_" + run);
            SceneManager.MoveGameObjectToScene(ownObject, ownScene);
            component = ownObject.AddComponent<SerializedNumericComponent>();
            if (component == null) throw new InvalidOperationException("Native AddComponent returned null. The numeric model scripts must be in their own runtime Models assembly, referenced by the Editor fixture; no fallback or fabricated Component is permitted.");
            // NewScene can activate the own scene. Restore the exact original active handle immediately.
            RestoreActiveScene();
            WriteObservation("prepared.json", "prepared");
        }

        [MenuItem("Tools/UniBridge Validation/Serialized Numeric/Observe")]
        public static void Observe()
        {
            RequireOwnedState();
            WriteObservation("observed-" + (++observations).ToString("D4", CultureInfo.InvariantCulture) + ".json", "observed");
        }

        [MenuItem("Tools/UniBridge Validation/Serialized Numeric/Apply Owned Asset Request")]
        public static void ApplyOwnedAssetRequest()
        {
            RequireOwnedState();
            int ordinal = requests + 1;
            string suffix = ordinal.ToString("D4", CultureInfo.InvariantCulture);
            string requestPath = Path.GetFullPath(Path.Combine(journal, "apply-request-" + suffix + ".json"));
            string boundary = Path.GetFullPath(journal) + Path.DirectorySeparatorChar;
            if (!requestPath.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !File.Exists(requestPath)) throw new IOException("Exact owned request is missing.");
            for (string cursor = requestPath; !string.Equals(cursor, Path.GetFullPath(journal), StringComparison.OrdinalIgnoreCase); cursor = Path.GetDirectoryName(cursor))
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Owned request cannot cross a reparse point.");
            string requestHash = Hash(requestPath);
            var request = JObject.Parse(File.ReadAllText(requestPath));
            if (Hash(requestPath) != requestHash || request.Value<string>("run") != run || request.Value<string>("targetGuid") != assetGuid ||
                request.Value<int?>("ordinal") != ordinal || request.Properties().Any(p => p.Name != "run" && p.Name != "targetGuid" && p.Name != "ordinal" && p.Name != "properties") ||
                request["properties"] is not JObject properties || !properties.HasValues || properties.Count > 40)
                throw new IOException("Owned request identity, ordinal, shape or bytes mismatch.");
            var asset = AssetDatabase.LoadAssetAtPath<SerializedNumericAsset>(assetPath);
            if (asset == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string actualGuid, out long _) || actualGuid != assetGuid)
                throw new IOException("Owned ScriptableObject GUID did not resolve to its actual object.");
            string fullAsset = Path.GetFullPath(Path.Combine(Root, assetPath));
            string ownBoundary = Path.GetFullPath(Path.Combine(Root, folder)) + Path.DirectorySeparatorChar;
            if (!fullAsset.StartsWith(ownBoundary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Owned save target is outside its fixture directory.");
            foreach (string ownedPath in new[] { fullAsset, fullAsset + ".meta", Path.GetDirectoryName(fullAsset), Path.GetFullPath(journal) })
                if ((File.GetAttributes(ownedPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Own native request/save path is a reparse point.");
            if (Hash(fullAsset) != lastAssetHash || Hash(fullAsset + ".meta") != lastMetaHash) throw new IOException("Owned asset bytes changed after observation.");
            // Consumption is durable before any shared writer can run. A request is never replayed.
            WriteNew(Path.Combine(journal, "apply-consumed-" + suffix + ".json"), new JObject { ["run"] = run,
                ["ordinal"] = ordinal, ["requestSha256"] = requestHash, ["targetGuid"] = assetGuid });
            requests = ordinal;
            bool applied = false, success = false;
            var validations = new JArray(); var writes = new JArray(); var errors = new JArray();
            try
            {
                // Invoke the exact public production helper; the oracle still reads actual CLR fields independently.
                var patcher = typeof(Cidonix.UniBridge.MCP.Editor.Helpers.SerializedPropertyPatcher);
                var method = patcher.GetMethod("TryApplyProperty", BindingFlags.Public | BindingFlags.Static);
                if (method == null) throw new MissingMethodException("Actual production TryApplyProperty is unavailable.");
                using (var serialized = new SerializedObject(asset))
                {
                    foreach (var property in properties.Properties())
                    {
                        var result = method.Invoke(null, new object[] { asset, serialized, property.Name, property.Value, true });
                        var json = JObject.FromObject(result); validations.Add(json);
                        if (json.Value<bool>("Success") != true) errors.Add(json.Value<string>("Error") ?? "Property was not found: " + property.Name);
                    }
                    if (errors.Count == 0)
                    {
                        foreach (var property in properties.Properties())
                        {
                            var result = method.Invoke(null, new object[] { asset, serialized, property.Name, property.Value, false });
                            var json = JObject.FromObject(result); writes.Add(json);
                            if (json.Value<bool>("Success") != true) errors.Add(json.Value<string>("Error") ?? "Write failed: " + property.Name);
                        }
                        if (errors.Count == 0)
                        {
                            applied = serialized.ApplyModifiedProperties();
                            EditorUtility.SetDirty(asset);
                            AssetDatabase.SaveAssetIfDirty(asset); // Exact owned object only; never global SaveAssets.
                            success = true;
                        }
                    }
                }
            }
            catch (Exception error) { errors.Add(error.GetType().Name + ": " + error.Message); }
            if (Hash(requestPath) != requestHash) errors.Add("Request bytes changed during native application.");
            WriteNew(Path.Combine(journal, "apply-result-" + suffix + ".json"), new JObject { ["phase"] = "owned_asset_applied", ["run"] = run,
                ["ordinal"] = ordinal, ["requestSha256"] = requestHash, ["targetGuid"] = assetGuid, ["success"] = success && errors.Count == 0,
                ["applyModifiedPropertiesReturned"] = applied, ["savedOnlyOwnedAsset"] = success, ["globalSaveAssetsCalled"] = false,
                ["validation"] = validations, ["writes"] = writes, ["errors"] = errors,
                ["actualNativeValues"] = Flatten(asset), ["assetSha256"] = Hash(fullAsset), ["assetMetaSha256"] = Hash(fullAsset + ".meta") });
        }

        [MenuItem("Tools/UniBridge Validation/Serialized Numeric/Finish")]
        public static void Finish()
        {
            if (run == null || string.IsNullOrEmpty(journal) || !File.Exists(Pointer)) throw new InvalidOperationException("No owned numeric cleanup journal.");
            string savedJournal = journal;
            if (Hash(Pointer) != pointerHash) throw new IOException("Own fixture pointer changed; preserve it.");
            if (Hash(Path.Combine(Root, assetPath)) != lastAssetHash || Hash(Path.Combine(Root, assetPath + ".meta")) != lastMetaHash)
                throw new IOException("Own asset changed after the last native Observe; preserve it until separately inspected.");
            // A partially failed Prepare may still have the freshly created own scene active.
            if (ownScene.IsValid() && SceneManager.GetActiveScene().handle == ownScene.handle) RestoreActiveScene();
            var authorNow = AuthorSceneState();
            if (!JToken.DeepEquals(originalScenes, authorNow)) throw new InvalidOperationException("Author scene state changed; preserve own fixture until coordinated cleanup.");
            if (ownObject != null) Object.DestroyImmediate(ownObject); ownObject = null; component = null;
            if (ownScene.IsValid() && ownScene.isLoaded && !EditorSceneManager.CloseScene(ownScene, true)) throw new IOException("Own unsaved additive scene did not close.");
            RestoreActiveScene();
            string absolute = Path.GetFullPath(Path.Combine(Root, folder));
            string assetsBoundary = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(assetsBoundary, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(absolute) != "UniBridgeNumericFixture_" + run)
                throw new IOException("Own fixture directory boundary mismatch.");
            if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new IOException("Own fixture directory is a reparse point.");
            var files = Directory.GetFileSystemEntries(absolute).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var expected = new[] { Path.Combine(absolute, "OwnedNumbers.asset"), Path.Combine(absolute, "OwnedNumbers.asset.meta") }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (!files.SequenceEqual(expected)) throw new IOException("Unexpected own folder content; preserve it.");
            if (AssetDatabase.AssetPathToGUID(assetPath) != assetGuid) throw new IOException("Own asset identity changed; preserve it.");
            foreach (var file in files) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Own file is a reparse point.");
            string quarantine = Path.Combine(savedJournal, "quarantine"); Directory.CreateDirectory(quarantine);
            string folderMeta = absolute + ".meta";
            if ((File.Exists(folderMeta) ? Hash(folderMeta) : null) != lastFolderMetaHash) throw new IOException("Own folder metadata changed after observation.");
            if (File.Exists(folderMeta) && (File.GetAttributes(folderMeta) & FileAttributes.ReparsePoint) != 0) throw new IOException("Own folder metadata is a reparse point.");
            Directory.Move(absolute, Path.Combine(quarantine, Path.GetFileName(absolute)));
            if (File.Exists(folderMeta)) File.Move(folderMeta, Path.Combine(quarantine, Path.GetFileName(folderMeta)));
            AssetDatabase.Refresh();
            var after = SceneState();
            bool scenesRetained = JToken.DeepEquals(originalScenes, after);
            bool selectionRetained = Selection.objects.SequenceEqual(originalSelection);
            WriteNew(Path.Combine(savedJournal, "finished.json"), new JObject { ["phase"] = "finished", ["run"] = run,
                ["sceneBefore"] = originalScenes, ["sceneAfter"] = after, ["sceneRetained"] = scenesRetained,
                ["selectionRetained"] = selectionRetained, ["ownAssetsQuarantined"] = true,
                ["noFixtureSelectionWrites"] = true,
                ["authorScenesNeverSaved"] = true, ["globalSaveAssetsNeverCalledByFixture"] = true });
            if (Hash(Pointer) != pointerHash) throw new IOException("Own pointer changed during cleanup; retain it.");
            File.Delete(Pointer);
            run = folder = assetPath = assetGuid = journal = pointerHash = lastAssetHash = lastMetaHash = lastFolderMetaHash = null;
            observations = requests = 0;
            if (!scenesRetained) throw new InvalidOperationException("Author scene retention failed.");
        }

        static void RequireOwnedState()
        {
            if (run == null || component == null || ownObject == null || !ownScene.IsValid() || !ownScene.isLoaded)
                throw new InvalidOperationException("No live own numeric fixture is available; no guessed object cleanup.");
            if (!File.Exists(Pointer) || Hash(Pointer) != pointerHash) throw new IOException("Own pointer is missing or changed.");
        }
        static void WriteObservation(string filename, string phase)
        {
            var asset = AssetDatabase.LoadAssetAtPath<SerializedNumericAsset>(assetPath);
            if (asset == null || AssetDatabase.AssetPathToGUID(assetPath) != assetGuid) throw new IOException("Own ScriptableObject identity is unavailable.");
            RememberOwnHashes();
            var componentValues = Flatten(component); var assetValues = Flatten(asset);
            WriteNew(Path.Combine(journal, filename), new JObject { ["phase"] = phase, ["run"] = run, ["unityVersion"] = Application.unityVersion,
                ["processBits"] = IntPtr.Size * 8, ["monoRuntime"] = Type.GetType("Mono.Runtime") != null,
                ["gameObjectId"] = ObjectId(ownObject).ToString(CultureInfo.InvariantCulture), ["componentType"] = component.GetType().FullName,
                ["assetPath"] = assetPath, ["assetGuid"] = assetGuid, ["assetType"] = asset.GetType().FullName,
                ["componentValues"] = componentValues, ["assetValues"] = assetValues,
                ["assetSha256"] = lastAssetHash, ["assetMetaSha256"] = lastMetaHash,
                ["folderMetaSha256"] = lastFolderMetaHash,
                ["sceneBefore"] = originalScenes, ["authorSceneNow"] = AuthorSceneState(),
                ["sceneRetained"] = JToken.DeepEquals(originalScenes, AuthorSceneState()),
                ["selectionRetained"] = Selection.objects.SequenceEqual(originalSelection),
                ["noFixtureSelectionWrites"] = true,
                ["globalSaveAssetsNeverCalledByFixture"] = true });
        }
        static void RememberOwnHashes()
        {
            lastAssetHash = Hash(Path.Combine(Root, assetPath)); lastMetaHash = Hash(Path.Combine(Root, assetPath + ".meta"));
            string meta = Path.Combine(Root, folder + ".meta"); lastFolderMetaHash = File.Exists(meta) ? Hash(meta) : null;
        }
        static JObject Flatten(Object owner)
        {
            var values = new JObject();
            foreach (var field in owner.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                FlattenValue(values, field.Name, field.GetValue(owner));
            return values;
        }
        static void FlattenValue(JObject values, string path, object value)
        {
            if (value is float f) { values[path] = new JObject { ["kind"] = "Single", ["value"] = f.ToString("R", CultureInfo.InvariantCulture) }; return; }
            if (value is double d) { values[path] = new JObject { ["kind"] = "Double", ["value"] = d.ToString("R", CultureInfo.InvariantCulture) }; return; }
            if (value is int || value is uint || value is long || value is ulong)
            { values[path] = new JObject { ["kind"] = value.GetType().Name, ["value"] = Convert.ToString(value, CultureInfo.InvariantCulture) }; return; }
            if (value is IList list) { for (int i = 0; i < list.Count; i++) FlattenValue(values, path + ".Array.data[" + i.ToString(CultureInfo.InvariantCulture) + "]", list[i]); return; }
            foreach (var field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) FlattenValue(values, path + "." + field.Name, field.GetValue(value));
        }
        static long ObjectId(Object value)
        {
#if UNITY_6000_5_OR_NEWER
            return (long)EntityId.ToULong(value.GetEntityId());
#else
#pragma warning disable 0618
            return value.GetInstanceID();
#pragma warning restore 0618
#endif
        }
        static JObject SceneState() => new JObject { ["activeHandle"] = SceneManager.GetActiveScene().handle.ToString(),
            ["scenes"] = new JArray(Enumerable.Range(0, SceneManager.sceneCount).Select(i => Scene(SceneManager.GetSceneAt(i)))) };
        static JObject AuthorSceneState() => new JObject { ["activeHandle"] = SceneManager.GetActiveScene().handle.ToString(),
            ["scenes"] = new JArray(Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.handle != ownScene.handle).Select(Scene)) };
        static JObject Scene(Scene scene) => new JObject { ["handle"] = scene.handle.ToString(), ["path"] = scene.path,
            ["dirty"] = scene.isDirty, ["loaded"] = scene.isLoaded, ["roots"] = scene.rootCount };
        static void RestoreActiveScene()
        {
            var active = originalScenes.Value<string>("activeHandle");
            for (int i = 0; i < SceneManager.sceneCount; i++) { var scene = SceneManager.GetSceneAt(i); if (scene.handle.ToString() == active) { SceneManager.SetActiveScene(scene); return; } }
            throw new InvalidOperationException("Original active scene no longer loaded.");
        }
        static string Hash(string path) { using var stream = File.OpenRead(path); using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        static void WriteNew(string path, JObject body) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); using var writer = new StreamWriter(stream); writer.Write(body.ToString()); }
    }
}
