#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class VisualSemanticNativeFixture
{
    static string folder, run, report;
    static JObject sceneBefore;
    static byte[] cacheBaseline;
    static string expectedCache, hiddenWorker, originalWorker, originalSecondGuid;
    static JObject replacementIdentity;
    static bool cacheObserved, held;
    static readonly Dictionary<string, string> owned = new();
    static readonly Dictionary<string, byte[]> originals = new();
    static readonly HashSet<string> ownedGuids = new(StringComparer.Ordinal);
    static readonly string[] Names = { "101", "102", "103", "104" };
    const string WorkerSha = "7c01b41f06396f298534649bdd3dd360281cf1f3e7a27a19792abf9eb40098ec";
    static string Root => Directory.GetParent(Application.dataPath).FullName;
    static string Cache => Path.Combine(Root, "Library/UniBridge/VisualAssetSearch/index-v1.json");
    static string Absolute(string relative) => Path.GetFullPath(Path.Combine(Root, relative));
    static string Hash(string path) { using var stream = File.OpenRead(path); using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    static object Field(string name) => typeof(VisualAssetSearch).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    static SemaphoreSlim Gate => (SemaphoreSlim)Field("Gate");
    static JObject Scenes()
    {
            var rows = new JArray();
        for (int i = 0; i < SceneManager.sceneCount; i++) { var scene = SceneManager.GetSceneAt(i); rows.Add(new JObject { ["handle"] = scene.handle.GetRawData().ToString(System.Globalization.CultureInfo.InvariantCulture), ["path"] = scene.path, ["dirty"] = scene.isDirty, ["loaded"] = scene.isLoaded }); }
        return new JObject { ["activeHandle"] = SceneManager.GetActiveScene().handle.GetRawData().ToString(System.Globalization.CultureInfo.InvariantCulture), ["scenes"] = rows };
    }
    static void CaptureFile(string path)
    { if (!File.Exists(path)) throw new IOException("Owned file is absent: " + path); owned[path] = Hash(path); if (!originals.ContainsKey(path)) originals[path] = File.ReadAllBytes(path); }
    static void GuardFile(string path)
    { if (!owned.TryGetValue(path, out string expected) || !File.Exists(path) || Hash(path) != expected || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Owned-file SHA/reparse guard rejected change: " + path); }
    static JObject State(string phase)
    {
        var assets = new JArray();
        if (folder != null)
            foreach (string name in Names)
            {
                string path = folder + "/" + name + ".png";
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long id))
                    assets.Add(new JObject { ["Guid"] = guid, ["LocalFileId"] = id, ["path"] = path, ["sourceSha256"] = Hash(Absolute(path)) });
            }
        return new JObject { ["phase"] = phase, ["folder"] = folder, ["run"] = run, ["assets"] = assets,
            ["sceneBefore"] = sceneBefore?.DeepClone(), ["sceneNow"] = Scenes(), ["sceneRetained"] = sceneBefore == null || JToken.DeepEquals(sceneBefore, Scenes()),
            ["processBits"] = IntPtr.Size * 8, ["unityVersion"] = Application.unityVersion, ["mainThreadId"] = Thread.CurrentThread.ManagedThreadId,
            ["monoRuntime"] = Type.GetType("Mono.Runtime") != null,
            ["activeWorkerPid"] = (int)Field("ActiveWorkerPid"), ["lastCaptureThreadId"] = (int)Field("LastCaptureThreadId"),
            ["renderTextureRestored"] = (bool)Field("LastRenderTextureRestored"), ["textureDestroyed"] = (bool)Field("LastTextureDestroyed"),
            ["temporaryReleased"] = (bool)Field("LastTemporaryReleased"), ["gateHeld"] = held, ["gateAvailable"] = Gate.CurrentCount,
            ["workerHidden"] = hiddenWorker != null, ["replacementIdentity"] = replacementIdentity?.DeepClone() };
    }
    static void Write(string phase)
    { Directory.CreateDirectory(Path.GetDirectoryName(report)); File.WriteAllText(report, State(phase).ToString()); }

    [McpTool("UniBridge_VisualFixtureState", "Read opt-in owned visual qualification state only.", EnabledByDefault = true, ExecutionPolicy = ToolExecutionPolicy.Observer)]
    public static object Observe() => Response.Success("Native visual fixture observed.", State("observed"));

    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Prepare")]
    public static void Prepare()
    {
        if (folder != null) throw new InvalidOperationException("Previous owned fixture must be cleaned first.");
        sceneBefore = Scenes(); run = Guid.NewGuid().ToString("N");
        folder = "Assets/UniBridgeVisualFixture_" + run;
        string nativeRoot = Path.Combine(Root, "Library/AgentValidation/VisualSemanticNative");
        report = Path.Combine(nativeRoot, run, "state.json");
        if (Directory.Exists(Absolute(folder)) || File.Exists(Absolute(folder) + ".meta")) throw new IOException("Fixture path collision.");
        if (File.Exists(Cache)) { cacheBaseline = File.ReadAllBytes(Cache); expectedCache = Hash(Cache); } else { cacheBaseline = null; expectedCache = null; }
        cacheObserved = true; owned.Clear(); originals.Clear(); ownedGuids.Clear(); originalSecondGuid = null; replacementIdentity = null;
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)); CaptureFile(Absolute(folder) + ".meta");
        foreach (string name in Names)
        {
            string path = folder + "/" + name + ".png"; string absolute = Absolute(path);
            if (File.Exists(absolute) || File.Exists(absolute + ".meta")) throw new IOException("Asset fixture collision.");
            File.WriteAllBytes(absolute, Png(name)); CaptureFile(absolute); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            CaptureFile(absolute); CaptureFile(absolute + ".meta");
            string guid = AssetDatabase.AssetPathToGUID(path); ownedGuids.Add(guid); if (name == "102") originalSecondGuid = guid;
        }
        Write("prepared");
        Directory.CreateDirectory(nativeRoot);
        File.WriteAllText(Path.Combine(nativeRoot, "latest.json"), new JObject { ["run"] = run, ["statePath"] = report }.ToString());
    }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Record Cache")]
    public static void RecordCache()
    {
        if (File.Exists(Cache))
        {
            var current = JObject.Parse(File.ReadAllText(Cache));
            var baseline = cacheBaseline == null ? new JArray() : (JArray)JObject.Parse(System.Text.Encoding.UTF8.GetString(cacheBaseline))["items"];
            foreach (JObject entry in (JArray)current["items"])
                if (!ownedGuids.Contains(entry.Value<string>("guid")) && !baseline.Any(old => JToken.DeepEquals(old, entry))) throw new IOException("Unexpected concurrent index entry; preserve it.");
            expectedCache = Hash(Cache);
        }
        else expectedCache = null;
        cacheObserved = true; Write("cache_recorded");
    }
    static void GuardCache()
    { if (!cacheObserved || (File.Exists(Cache) ? Hash(Cache) : null) != expectedCache) throw new IOException("Generated cache changed outside the known fixture journal; preserve it."); }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Swap First")]
    public static void SwapFirst()
    {
        string relative = folder + "/101.png", path = Absolute(relative); GuardFile(path); GuardFile(path + ".meta");
        File.WriteAllBytes(path, Png("104")); AssetDatabase.ImportAsset(relative, ImportAssetOptions.ForceSynchronousImport);
        owned[path] = Hash(path); owned[path + ".meta"] = Hash(path + ".meta"); Write("first_changed");
    }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Delete Second")]
    public static void DeleteSecond()
    {
        string relative = folder + "/102.png", path = Absolute(relative); GuardFile(path); GuardFile(path + ".meta");
        if (!AssetDatabase.DeleteAsset(relative)) throw new IOException("Owned asset deletion failed.");
        owned.Remove(path); owned.Remove(path + ".meta"); Write("second_deleted");
    }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Replace Second Identity")]
    public static void ReplaceSecondIdentity()
    {
        GuardCache();
        string relative = folder + "/102.png", path = Absolute(relative);
        GuardFile(Absolute(folder) + ".meta");
        if ((File.GetAttributes(Absolute(folder)) & FileAttributes.ReparsePoint) != 0) throw new IOException("Preserve unexpected replacement reparse directory.");
        if (File.Exists(path) || File.Exists(path + ".meta")) throw new IOException("Replacement requires the already deleted own asset path.");
        var cache = JObject.Parse(File.ReadAllText(Cache));
        var entry = ((JArray)cache["items"]).OfType<JObject>().Single(row => row.Value<string>("guid") == originalSecondGuid);
        byte[] originalMeta = originals[path + ".meta"];
        var strictUtf8 = new System.Text.UTF8Encoding(false, true);
        string metaText = strictUtf8.GetString(originalMeta);
        var fields = System.Text.RegularExpressions.Regex.Matches(metaText, @"^guid: ([a-fA-F0-9]{32})\r?$", System.Text.RegularExpressions.RegexOptions.Multiline, TimeSpan.FromMilliseconds(100));
        if (fields.Count != 1 || !string.Equals(fields[0].Groups[1].Value, originalSecondGuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Own original .meta must contain exactly one root YAML GUID field matching the original identity.");
        string seededGuid;
        do { seededGuid = Guid.NewGuid().ToString("N"); } while (ownedGuids.Contains(seededGuid) || string.Equals(seededGuid, originalSecondGuid, StringComparison.OrdinalIgnoreCase));
        // Copy exact original bytes and change only the 32 ASCII GUID bytes.
        // BOM, newline style, importer settings and every other .meta byte remain intact.
        byte[] seededMeta = (byte[])originalMeta.Clone();
        int guidByteOffset = strictUtf8.GetByteCount(metaText.Substring(0, fields[0].Groups[1].Index));
        byte[] guidBytes = System.Text.Encoding.ASCII.GetBytes(seededGuid);
        Buffer.BlockCopy(guidBytes, 0, seededMeta, guidByteOffset, guidBytes.Length);
        File.WriteAllBytes(path, originals[path]); CaptureFile(path);
        using (var meta = new FileStream(path + ".meta", FileMode.CreateNew, FileAccess.Write, FileShare.None)) meta.Write(seededMeta, 0, seededMeta.Length);
        CaptureFile(path + ".meta");
        AssetDatabase.ImportAsset(relative, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        CaptureFile(path); CaptureFile(path + ".meta");
        var asset = AssetDatabase.LoadMainAssetAtPath(relative);
        if (asset == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string actualGuid, out long actualLocalId) || string.Equals(actualGuid, originalSecondGuid, StringComparison.OrdinalIgnoreCase) || !string.Equals(actualGuid, seededGuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Own replacement did not acquire a distinct GUID identity.");
        ownedGuids.Add(actualGuid);
        // Deliberately make all non-GUID cache checks pass against the replacement.
        // Only this owned old row changes; its old GUID must never bind the new asset.
        entry["localFileId"] = actualLocalId; entry["dependencyHash"] = AssetDatabase.GetAssetDependencyHash(relative).ToString(); entry["sourceSha256"] = Hash(path);
        File.WriteAllText(Cache, cache.ToString()); expectedCache = Hash(Cache);
        var predicate = typeof(VisualAssetSearch).GetMethod("MatchesAssetIdentity", BindingFlags.Static | BindingFlags.NonPublic);
        bool oldAccepted = (bool)predicate.Invoke(null, new object[] { asset, originalSecondGuid, actualLocalId });
        bool newAccepted = (bool)predicate.Invoke(null, new object[] { asset, actualGuid, actualLocalId });
        replacementIdentity = new JObject { ["oldGuid"] = originalSecondGuid, ["seededGuid"] = seededGuid, ["actualGuid"] = actualGuid, ["localFileId"] = actualLocalId,
            ["replacementPath"] = relative, ["oldGuidLookup"] = AssetDatabase.GUIDToAssetPath(originalSecondGuid),
            ["forcedCurrentHashes"] = entry.Value<string>("dependencyHash") == AssetDatabase.GetAssetDependencyHash(relative).ToString() && entry.Value<string>("sourceSha256") == Hash(path),
            ["oldNativeIdentityAccepted"] = oldAccepted, ["newNativeIdentityAccepted"] = newAccepted };
        Write("second_replaced");
    }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Delete Replacement Second")]
    public static void DeleteReplacementSecond()
    {
        string current = AssetDatabase.AssetPathToGUID(folder + "/102.png");
        if (string.IsNullOrEmpty(current) || current == originalSecondGuid) throw new InvalidOperationException("Only the distinct own replacement identity may be deleted here.");
        DeleteSecond(); Write("replacement_deleted");
    }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Restore Second")]
    public static void RestoreSecond()
    {
        string relative = folder + "/102.png", path = Absolute(relative);
        if (File.Exists(path) || File.Exists(path + ".meta")) throw new IOException("Unexpected file prevents own-asset restoration.");
        File.WriteAllBytes(path, originals[path]); File.WriteAllBytes(path + ".meta", originals[path + ".meta"]);
        AssetDatabase.ImportAsset(relative, ImportAssetOptions.ForceSynchronousImport); CaptureFile(path); CaptureFile(path + ".meta"); Write("second_restored");
    }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Corrupt Cache")]
    public static void CorruptCache()
    { GuardCache(); Directory.CreateDirectory(Path.GetDirectoryName(Cache)); File.WriteAllText(Cache, "{owned-invalid-cache"); expectedCache = Hash(Cache); Write("cache_corrupted"); }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Hold Gate")]
    public static void HoldGate()
    { if (held || !Gate.Wait(0)) throw new InvalidOperationException("Cannot own the private gate."); held = true; Write("gate_held"); }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Release Gate")]
    public static void ReleaseGate()
    { if (!held) throw new InvalidOperationException("Fixture does not own the gate."); Gate.Release(); held = false; Write("gate_released"); }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Hide Worker")]
    public static void HideWorker()
    {
        string package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VisualAssetSearch).Assembly).resolvedPath;
        originalWorker = Path.GetFullPath(Path.Combine(package, "VisualWorker~/win-x64/unibridge_visual_worker.exe"));
        if (!originalWorker.StartsWith(Path.Combine(Root, "Packages") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Hash(originalWorker) != WorkerSha)
            throw new IOException("Only this candidate's project-owned embedded worker can be hidden.");
        string destination = Path.Combine(Path.GetDirectoryName(report), "owned-worker.exe");
        if (File.Exists(destination) || hiddenWorker != null) throw new IOException("Owned worker backup collision.");
        File.Move(originalWorker, destination); hiddenWorker = destination; Write("worker_hidden");
    }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Restore Worker")]
    public static void RestoreWorker()
    {
        if (hiddenWorker == null || File.Exists(originalWorker) || Hash(hiddenWorker) != WorkerSha) throw new IOException("Worker preservation guard failed.");
        File.Move(hiddenWorker, originalWorker); hiddenWorker = null; Write("worker_restored");
    }
    [MenuItem("Tools/UniBridge Validation/Visual Semantic/Finish")]
    public static void Finish()
    {
        if (hiddenWorker != null) RestoreWorker();
        if (held) ReleaseGate();
        GuardCache();
        if (cacheBaseline == null) { if (File.Exists(Cache)) File.Delete(Cache); }
        else { Directory.CreateDirectory(Path.GetDirectoryName(Cache)); File.WriteAllBytes(Cache, cacheBaseline); }
        string absolute = Absolute(folder);
        if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new IOException("Preserve unexpected fixture reparse directory.");
        var actual = Directory.GetFiles(absolute, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = owned.Keys.Where(path => path.StartsWith(absolute + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected)) throw new IOException("Preserve unknown concurrent files in fixture directory.");
        foreach (string file in owned.Keys) GuardFile(file);
        if (!AssetDatabase.DeleteAsset(folder)) throw new IOException("Owned fixture cleanup failed.");
        if (Directory.Exists(absolute) || File.Exists(absolute + ".meta")) throw new IOException("Owned fixture remains after cleanup.");
        string previous = folder; folder = null; Write("finished");
        if (!JToken.DeepEquals(sceneBefore, Scenes())) throw new InvalidOperationException("Author scene identity/dirty state changed; no scene save/rollback attempted.");
    }
    static byte[] Png(string name)
    {
        if (name == "101") return Convert.FromBase64String("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");
        if (name == "102") return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAOAAAADgCAIAAACVT/22AAAFe0lEQVR4nO3dTW6cRRSG0Q5iWrKEUBYAmbMJxtkLK2EvjNlE5sACUISEsgAz8CQiidPurp+36p5n6NjVX6SjW1Ut2f3q8fHxIqX2zeoHkJ4LUEUHqKIDVNEBqugAVXSAKjpAFR2gig5QRQeoogNU0QGq6ABVdIAqOkAVHaCKDlBFB6iiA7Rzr9++6b1k6QDt2eu3b9pP3zPaMUA767xcLox2DNDOOtU3QIdkiPYK0FHjk9EuATpwc2f0/gAde/Rk9M4AHX4xYvSeAL0x1/Y5ATojQ/TmAJ00Phm9LUDnbe6M3hCgU4+ejL40QGdfjBh9UYBem2v7kgBdkCF6fYCuGZ+MXhmgyzZ3Rq8J0JVHT0a/GqCLL0aMPh+gX8y1PSFA12eIPhOgEeOT0S8FaMrmzuhnAzTo6MnopwGadTFi9H8BGqRTnwZo5x5++O7OFQzRjwO05/h80sloxwAN3dzN0acA7abz48F5/xBl9KnqQEfo/NJXbqiV/2OOpYGO0/n813V9pYFuUas9ROsCHT0+r/nXK2uFjRYFOkfn9d/z1VpVoxWBztT50u98plbSaDmggW95Xl+rZ7QW0I46XzoU3ehvqxbQXt2mzUZ/Q4WAzj969v3Zmht9FaBbHz0rGy0BdOHRc8QKpYyeDzRKZ991WgGjhwMN1DlitYM7HOjxtdOH6MlAY8dn3zXb0UaPBRqus+/K7VyjZwLdQmff9duhRg8EethbnsWNnga0r845d203+kJAN3Vjoy8BdKOj57hXbGdt9OcALXv0PNvoIUB3PHoOfd12itETgJ6hs/urtyOMbg/0JJ05z5DT9kB19hDdG+h54/MpG/0JQE/V+RSjewM9W+dTDy5MmwL1lmed8+h+QLvrzByf+c82p/2AVhPwUHuj3wxohaPnpz0UNroTUEfPgka3AVrq6Dn6ads+RvcAWlxnZaPfXkru7P/+9U/fBVV6gmpEWwzRdKAuRsWNRgOlc0LhRnOB0jmtZKOhQOmcXEs1mgiUTkUD1ZJa5BCNA2p8LqzlGc0CSufyWpjRIKB0htSSjKYApTOqFmM0AiidigaqwFrGEF0P1PiMrQUYffX4+Ljw5Xv9//1du8/24d37S4/+/u2PS02g92cAP9+Hd+8X8jphi78nOrfYposCpbOC0Y2BqkK7AjU+iwzRLYHSWcfofkDpLGV0M6B0VjO6E1A6CxrdCagKtg1Q47PmEN0DKJ1ljW4AlM7KRtOB0lncaDRQOicUbjQaqJQL1PicVvIQDQVK5+RaqtFEoHQuqUUajQNK58JantEsoHQur4UZzQIq5QI1PkNqSUM0BSidUbUYoxFA6QysZRhdD5TO2FqA0cVA6QyvrTa6foJKoUCNzy1qS4foMqB0blRbZ3QNUDq3qy0yugAonZvWVhidDZTOrWvTjbrFK7qpQI3PA2pzh+g8oHQeU5todBJQOg+rzTI6AyidR9amGB0OlM6Da+ONusUrurFAjc/jGz1EBwKls0htpNFRQOksVRtmdAhQOgvWxhjtD5TOsrUBRt3iFV1noMZn8VrvIdoTKJ269Dba7fPil/9+qtLq8jn13YDW6Zeff7znx3/9/c9+z3J+LkmKDlBFB6iiA1TRAaroAFV0gCo6QBUdoIoOUEUHqKIDVNEBqugAVXSAKjpAFR2gig5QRQeoogNU0QGq6ABVdIAqOkAVHaCKDlBFB6iiA1TRAaroAFV0gCo6QBUdoIoOUEUHqKIDVNEBqugAVXSAKjpAFR2gig5QRQeoogNU0QGq6ABVdD6OW9GZoIoOUEUHqKIDVNEBqugAVXSAKjpAFR2gig5QRQeoogNU0QGq6ABVdIAqOkAVHaCKDlBFB6iiA1TRAaroANUluf8AaJLu195Q+XYAAAAASUVORK5CYII=");
        if (name == "103") return Convert.FromBase64String("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");
        if (name == "104") return Convert.FromBase64String("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");
        throw new ArgumentException("Unknown owned fixture identity.");
    }
}
#endif
