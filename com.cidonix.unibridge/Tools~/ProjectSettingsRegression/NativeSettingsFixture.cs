using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

public static class NativeSettingsFixture
{
    static JArray cases;
    static void Check(string name, bool passed, object detail = null)
    { cases.Add(new JObject { ["name"] = name, ["passed"] = passed, ["detail"] = passed ? null : JToken.FromObject(detail ?? "condition false") }); }
    static JObject Json(object value) => JObject.FromObject(value);
    static JObject Invoke(ProjectSettingsParams p) => Json(ProjectSettingsTool.HandleCommand(p));
    static JObject Read(SettingsReadProbe probe, ProjectSettingsParams p, CancellationToken token = default)
    {
        var method = typeof(ProjectSettingsTool).GetMethod("InspectTarget", BindingFlags.NonPublic | BindingFlags.Static);
        return Json(method.Invoke(null, new object[] { probe, p, "owned-memory-probe", token, Stopwatch.StartNew(), null }));
    }
    static JToken Property(JObject result, string path) => (result["properties"] as JArray)?.SingleOrDefault(x => x.Value<string>("path") == path);
    static JObject SceneState() => new() { ["loaded"] = SceneManager.sceneCount, ["scenes"] = new JArray(Enumerable.Range(0, SceneManager.sceneCount).Select(i => {
        var s = SceneManager.GetSceneAt(i); return new JObject { ["path"] = s.path, ["dirty"] = s.isDirty, ["roots"] = s.rootCount }; })) };

    [MenuItem("Tools/UniBridge Validation/Extensions Settings 1/Run")]
    public static void Run()
    {
        string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "AgentValidation", "ExtensionsSettings1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); cases = new JArray(); var sceneBefore = SceneState(); SettingsReadProbe probe = null;
        try
        {
            var list = Invoke(new ProjectSettingsParams());
            File.WriteAllText(Path.Combine(folder, "production-list.json"), list.ToString());
            Check("Actual native settings catalog includes QualitySettings", list.Value<bool>("success") && list["data"]?["settings"] is JArray entries && entries.Any(x => x.Value<string>("name") == "QualitySettings" && x.Value<bool>("available")), list);
            var quality = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0];
            bool dirtyBefore = EditorUtility.IsDirty(quality);
            using (var native = new SerializedObject(quality))
            {
                var current = native.FindProperty("m_CurrentQuality");
                var read = Invoke(new ProjectSettingsParams { Action = ProjectSettingsAction.Inspect, Name = "QualitySettings", PropertyPath = "m_CurrentQuality" });
                File.WriteAllText(Path.Combine(folder, "production-exact-quality.json"), read.ToString());
                Check("QualitySettings scalar matches independent SerializedObject", current != null && read.Value<bool>("success") && Property((JObject)read["data"], "m_CurrentQuality")?["value"]?.Value<long>() == current.longValue, read);
            }
            var arrays = Invoke(new ProjectSettingsParams { Action = ProjectSettingsAction.Inspect, Name = "QualitySettings", MaxArrayElements = 0 });
            File.WriteAllText(Path.Combine(folder, "production-quality-bounded.json"), arrays.ToString());
            Check("Actual QualitySettings array cap declared", arrays.Value<bool>("success") && arrays["data"]?.Value<bool>("complete") == false && arrays["data"]?["limitations"] is JArray limits && limits.Any(x => (string)x == "array_limit"), arrays);
            Check("Settings dirty state retained", EditorUtility.IsDirty(quality) == dirtyBefore);
            Check("PlayerSettings logical alias resolves current native object", Invoke(new ProjectSettingsParams { Action = ProjectSettingsAction.Inspect, Name = "PlayerSettings", MaxProperties = 1 }).Value<bool>("success"));

            probe = ScriptableObject.CreateInstance<SettingsReadProbe>(); probe.hideFlags = HideFlags.HideAndDontSave;
            string guid = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" }).FirstOrDefault();
            if (guid != null) probe.TypedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guid));
            var options = new ProjectSettingsParams { Name = "OwnedProbe", MaxDepth = 6, MaxArrayElements = 32, MaxStringChars = 4096 };
            var full = Read(probe, options); File.WriteAllText(Path.Combine(folder, "production-owned-probe.json"), full.ToString());
            Check("Signed64 exact beyond double mantissa", Property(full, "WideSigned")?["value"]?.ToString() == "9007199254740993", full);
            Check("Unsigned64 retains all bits", Property(full, "WideUnsigned")?["value"]?.ToString() == "18446744073709551615", full);
            Check("Native double precision retained", Math.Abs(Property(full, "Precise")?["value"]?.Value<double>() - probe.Precise ?? 1) < 1e-14, full);
            Check("Vector4 physical scalar shape", Property(full, "Vector")?["value"]?["w"]?.Value<int>() == 4, full);
            Check("Typed asset reference retains GUID", probe.TypedTexture != null && Property(full, "TypedTexture")?["value"]?.Value<string>("guid") == guid, full);
            var narrow = Read(probe, new ProjectSettingsParams { Name = "OwnedProbe", PropertyPath = "Numbers.Array.data[2]", MaxDepth = 0 });
            Check("Canonical array drilldown is exact", narrow.Value<bool>("complete") && narrow["properties"] is JArray rows && rows.Count == 1 && rows[0]["value"].Value<int>() == 33, narrow);
            var arrayCap = Read(probe, new ProjectSettingsParams { Name = "OwnedProbe", PropertyPath = "Numbers", MaxArrayElements = 2 });
            Check("Array count remains full while output is bounded", !arrayCap.Value<bool>("complete") && arrayCap["properties"] is JArray arrayRows && arrayRows.Count == 3 && arrayRows[0].Value<int>("arraySize") == 4 && arrayRows[0].Value<bool>("childrenTruncated"), arrayCap);
            var textCap = Read(probe, new ProjectSettingsParams { Name = "OwnedProbe", PropertyPath = "Text", MaxStringChars = 3 });
            Check("Text cap preserves surrogate boundary", !textCap.Value<bool>("complete") && Property(textCap, "Text")?["value"]?.Value<string>() == "ab", textCap);
            var depthCap = Read(probe, new ProjectSettingsParams { Name = "OwnedProbe", PropertyPath = "Nested", MaxDepth = 0 });
            Check("Depth cap explicit at exact container", !depthCap.Value<bool>("complete") && depthCap["properties"] is JArray depthRows && depthRows.Count == 1 && depthRows[0].Value<bool>("childrenTruncated"), depthCap);
            var propertyCap = Read(probe, new ProjectSettingsParams { Name = "OwnedProbe", MaxProperties = 1 });
            Check("Global property budget bounded", !propertyCap.Value<bool>("complete") && propertyCap["properties"] is JArray one && one.Count == 1, propertyCap);
            var noValues = Read(probe, new ProjectSettingsParams { Name = "OwnedProbe", PropertyPath = "WideSigned", IncludeValues = false });
            Check("Values omitted without reading scalar", Property(noValues, "WideSigned")?.Value<string>("valueStatus") == "not_requested" && Property(noValues, "WideSigned")?["value"].Type == JTokenType.Null, noValues);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool cancelled = false;
                try { Read(probe, options, cancel.Token); } catch (TargetInvocationException e) { cancelled = e.InnerException is OperationCanceledException; }
                Check("Native reader observes cancellation token", cancelled);
            }
            Check("Traversal name rejected", Invoke(new ProjectSettingsParams { Action = ProjectSettingsAction.Inspect, Name = "../QualitySettings" }).Value<string>("code") == "INVALID_SETTINGS_NAME");
            Check("Invalid property syntax rejected", Invoke(new ProjectSettingsParams { Action = ProjectSettingsAction.Inspect, Name = "QualitySettings", PropertyPath = "m_QualitySettings[-1]" }).Value<string>("code") == "INVALID_PROPERTY_PATH");
            Check("Missing property gives truthful failure", Invoke(new ProjectSettingsParams { Action = ProjectSettingsAction.Inspect, Name = "QualitySettings", PropertyPath = "NoSuchProperty" }).Value<string>("code") == "SETTINGS_PROPERTY_NOT_FOUND");
            Check("Missing settings gives truthful failure", Invoke(new ProjectSettingsParams { Action = ProjectSettingsAction.Inspect, Name = "MissingUniBridgeSettings20261012" }).Value<string>("code") == "SETTINGS_NOT_FOUND");
            Check("Oversized budget rejected before read", Invoke(new ProjectSettingsParams { Action = ProjectSettingsAction.Inspect, Name = "QualitySettings", MaxProperties = 513 }).Value<string>("code") == "INVALID_SETTINGS_LIMIT");
        }
        catch (Exception error) { Check("Fixture exception", false, error.ToString()); }
        finally
        {
            if (probe != null) UnityEngine.Object.DestroyImmediate(probe);
            Check("Author scene identity/dirty state retained", JToken.DeepEquals(sceneBefore, SceneState()));
            var result = new JObject { ["passed"] = cases.All(x => x.Value<bool>("passed")), ["unityVersion"] = Application.unityVersion, ["processBits"] = IntPtr.Size * 8,
                ["sceneBefore"] = sceneBefore, ["sceneAfter"] = SceneState(), ["cases"] = cases };
            File.WriteAllText(Path.Combine(folder, "result.json"), result.ToString());
            Debug.Log("UniBridge Extensions Settings1 qualification: " + folder + "; passed=" + result["passed"]);
        }
    }
}
