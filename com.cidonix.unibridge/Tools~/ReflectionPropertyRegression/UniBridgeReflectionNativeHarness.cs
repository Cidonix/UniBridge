// Additive native fixture: no authored scene is saved/reloaded, no transport mutation retry.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UniBridgeQualification;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class UniBridgeReflectionNativeHarness
{
    static Scene owned, original;
    static GameObject[] roots;
    static ReflectionPropertyProbe probe;
    static Material material;
    static string folder;
    static JArray cases, checks;
    static JObject report, expected;
    static int next;
    static List<Scene> author;
    // Native importer replaces this marker from the UUID-owned shader .meta GUID.
    const string OwnedShaderGuid = "__OWNED_SHADER_GUID__";

    [MenuItem("Tools/UniBridge Qualification/Reflection Paths/Begin")]
    public static void BeginMenu() { Debug.Log(Begin(AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(OwnedShaderGuid)))); }
    [MenuItem("Tools/UniBridge Qualification/Reflection Paths/Verify")]
    public static void VerifyMenu() { Debug.Log(Verify()); }
    [MenuItem("Tools/UniBridge Qualification/Reflection Paths/Finish")]
    public static void FinishMenu() { Debug.Log(Finish()); }

    public static string Begin(Shader ownedShader)
    {
        if (probe != null) throw new InvalidOperationException("Finish the existing owned fixture first.");
        if (ownedShader == null) throw new ArgumentNullException("ownedShader");
        folder = Path.Combine(Path.GetTempPath(), "unibridge-reflection-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        original = SceneManager.GetActiveScene();
        author = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).ToList();
        checks = new JArray(); cases = new JArray(); next = 0;
        report = new JObject { ["folder"] = folder, ["unityVersion"] = Application.unityVersion,
            ["authorBefore"] = Author(), ["checks"] = checks, ["cases"] = cases };
        owned = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);
        roots = owned.GetRootGameObjects();
        var camera = roots.SelectMany(r => r.GetComponentsInChildren<Camera>(true)).Single();
        camera.enabled = false;
        probe = camera.gameObject.AddComponent<ReflectionPropertyProbe>();
        material = new Material(ownedShader); probe.OwnedMaterial = material;
        material.SetColor("_Tint", Color.white); material.SetVector("_Direction", Vector4.zero); material.SetFloat("_Amount", 0f);
        expected = Physical();
        Add("nested-property", "Nested.Value", new JValue(3), "value", new JValue(3), true);
        Add("nested-field", "Nested.Field", new JValue(4), "field", new JValue(4), true);
        Add("list-traversal", "Nodes[0].Value", new JValue(5), "list", new JValue(5), true);
        Add("array-traversal", "NodeArray[0].Value", new JValue(6), "array", new JValue(6), true);
        Add("clamp-mismatch", "Nested.Clamped", new JValue(3), "clamped", new JValue(1f), false, true);
        Add("bad-index-rejected", "Nodes[99].Value", new JValue(7), null, null, false);
        Add("null-parent-rejected", "Absent.Value", new JValue(7), null, null, false);
        Add("shader-color", "OwnedMaterial._Tint", new JArray(.25, .5, .75, 1), "tint", new JArray(.25f, .5f, .75f, 1f), true);
        Add("shader-vector", "OwnedMaterial._Direction", new JArray(1, 2, 3, 4), "direction", new JArray(1f, 2f, 3f, 4f), true);
        Add("shader-bool-float", "OwnedMaterial._Amount", new JValue(true), "amount", new JValue(1f), true);
        if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
        Require(JToken.DeepEquals(Author(), report["authorBefore"]), "Author scene identity/dirty state changed at begin.");
        Save(); return Summary();
    }
    static void Add(string name, string path, JToken input, string key, JToken value, bool success, bool clamp = false)
    {
        var adapter = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Cidonix.UniBridge.MCP.Editor.Helpers.UnityApiAdapter")).First(t => t != null);
        var id = (long)adapter.GetMethod("GetObjectId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { probe.gameObject });
        cases.Add(new JObject { ["name"] = name, ["key"] = key, ["expected"] = value,
            ["success"] = success, ["clamp"] = clamp, ["wirePath"] = Path.Combine(folder, "case-" + cases.Count.ToString("D2") + ".wire.json"),
            ["payload"] = new JObject { ["Action"] = "SetComponentProperty", ["Target"] = id, ["SearchMethod"] = "ById",
                ["ComponentName"] = typeof(ReflectionPropertyProbe).FullName, ["ComponentProperties"] = new JObject { [path] = input } } });
    }
    public static string Verify()
    {
        Require(next < cases.Count, "No pending case.");
        var c = (JObject)cases[next];
        var wire = JObject.Parse(File.ReadAllText((string)c["wirePath"]));
        wire = (wire["raw"] as JObject) ?? wire;
        var business = wire["structuredContent"] as JObject;
        if (business == null)
        {
            var content = (wire["result"] ?? wire)["content"] as JArray;
            if (content != null) business = JObject.Parse((string)content.First(x => (string)x["type"] == "text")["text"]);
            else business = wire;
        }
        bool passed = false; string error = null;
        try
        {
            Require((bool?)business["success"] == (bool)c["success"], "Unexpected business result.");
            if (!string.IsNullOrEmpty((string)c["key"])) expected[(string)c["key"]] = c["expected"].DeepClone();
            Require(JToken.DeepEquals(Physical(), expected), "Physical state differs from independent expected state.");
            var data = business["data"] ?? business;
            var applied = data[(bool)c["clamp"] ? "skipped" : "applied"] as JArray;
            if ((bool)c["success"] || (bool)c["clamp"])
            {
                Require(applied != null && applied.Count == 1, "One attempted reflection report required.");
                Require((string)applied[0]["route"] == "reflection", "Dedicated route hid the reflection resolver.");
                Require((bool?)applied[0]["readbackVerified"] == (bool)c["success"], "Readback truthfulness differs.");
                if ((bool)c["clamp"]) Require(applied[0]["actualValue"].Value<float>() == 1f, "Clamped actualValue missing.");
            }
            Require(JToken.DeepEquals(Author(), report["authorBefore"]), "Author scenes changed.");
            passed = true;
        }
        catch (Exception e) { error = e.ToString(); }
        checks.Add(new JObject { ["name"] = c["name"], ["passed"] = passed, ["error"] = error, ["physical"] = Physical(), ["business"] = business });
        next++; Save(); return Summary();
    }
    public static string Finish()
    {
        Require(owned.IsValid() && owned.isLoaded, "Owned scene unavailable.");
        var actualRoots = owned.GetRootGameObjects();
        Require(actualRoots.Length == roots.Length && actualRoots.All(r => roots.Contains(r)), "Unexpected objects in owned scene; do not delete.");
        Require(EditorSceneManager.CloseScene(owned, true), "Owned scene did not close.");
        if (material != null) UnityEngine.Object.DestroyImmediate(material);
        if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
        Require(JToken.DeepEquals(Author(), report["authorBefore"]), "Author scene identity/dirty state changed at finish.");
        report["closed"] = true; report["passed"] = next == cases.Count && checks.All(c => (bool)c["passed"]);
        Save(); probe = null; return Summary();
    }
    static JObject Physical()
    {
        var color = material.GetColor("_Tint"); var vector = material.GetVector("_Direction");
        return new JObject { ["value"] = probe.Nested.Value, ["field"] = probe.Nested.Field,
            ["list"] = probe.Nodes[0].Value, ["array"] = probe.NodeArray[0].Value, ["clamped"] = probe.Nested.Clamped,
            ["tint"] = new JArray(color.r, color.g, color.b, color.a), ["direction"] = new JArray(vector.x, vector.y, vector.z, vector.w), ["amount"] = material.GetFloat("_Amount") };
    }
    static JArray Author() { return new JArray(author.Select(s => new JObject { ["handle"] = s.handle.ToString(), ["loaded"] = s.isLoaded, ["dirty"] = s.isDirty })); }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Save() { report["next"] = next; File.WriteAllText(Path.Combine(folder, "native-report.json"), report.ToString()); }
    static string Summary() { return new JObject { ["folder"] = folder, ["reportPath"] = Path.Combine(folder, "native-report.json"), ["next"] = next,
        ["case"] = next < cases.Count ? cases[next] : null, ["passed"] = report["passed"] }.ToString(); }
}
