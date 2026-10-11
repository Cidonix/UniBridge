// Copy to a unique Assets/Editor file in the disposable qualification project, then invoke Run(REPORT).
// All test objects live in a temporary Preview Scene. This never changes PlayerSettings or saves a scene.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static class UniBridgeInputBackendLiveRegression
{
    [MenuItem("Tools/UniBridge Qualification/Input Backend")]
    public static void RunFromMenu()
    {
        var report = Path.Combine(Path.GetTempPath(), "unibridge-input-backend-native-" + Guid.NewGuid().ToString("N") + ".json");
        Debug.Log("UniBridge Input Backend qualification: " + Run(report) + " Report: " + report);
    }

    public static string Run(string reportPath)
    {
        var checks = new List<object>();
        var tools = FindType("Cidonix.UniBridge.MCP.Editor.Tools.ManageUI");
        var resolver = FindType("Cidonix.UniBridge.MCP.Editor.Helpers.InputBackendResolver");
        var resolutionType = FindType("Cidonix.UniBridge.MCP.Editor.Helpers.InputBackendResolution");
        var modeType = FindType("Cidonix.UniBridge.MCP.Editor.Helpers.InputBackendMode");
        var newType = FindType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
        var repair = tools.GetMethod("EnsureCompatibleInputModule", BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { typeof(GameObject), resolutionType }, null);
        var preview = EditorSceneManager.NewPreviewScene();
        try
        {
            Check(checks, "actual-serialized-player-setting", delegate {
                var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").OfType<PlayerSettings>().First();
                var serialized = new SerializedObject(settings);
                var value = serialized.FindProperty("activeInputHandler").intValue;
                var expected = value == 0 ? "Old" : value == 1 ? "New" : value == 2 ? "Both" : "Unknown";
                var resolution = resolver.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                Assert(resolutionType.GetField("Mode").GetValue(resolution).ToString() == expected, "Actual resolver disagrees with the serialized project setting.");
            });
            foreach (var old in new[] { true, false })
            {
                var module = old ? typeof(StandaloneInputModule) : newType;
                var mode = old ? "Old" : "New";
                Check(checks, mode + "-preserves-existing-compatible-module", delegate {
                    var go = Create(preview, module); var original = go.GetComponent(module);
                    Assert(!Repair(repair, go, resolutionType, modeType, mode), "An already compatible object was modified.");
                    Assert(go.GetComponent(module) == original, "The original module was replaced.");
                });
                Check(checks, mode + "-adds-compatible-module", delegate {
                    var go = Create(preview);
                    Assert(Repair(repair, go, resolutionType, modeType, mode), "No change was reported.");
                    Assert(go.GetComponent(module) != null, "Compatible module was not added.");
                    Assert(!Repair(repair, go, resolutionType, modeType, mode), "A second call was not idempotent.");
                });
                Check(checks, mode + "-repairs-incompatible-existing-module", delegate {
                    var opposite = old ? newType : typeof(StandaloneInputModule);
                    var go = Create(preview, opposite);
                    Assert(Repair(repair, go, resolutionType, modeType, mode), "No repair was reported.");
                    Assert(go.GetComponent(module) != null && go.GetComponent(opposite) == null, "Input module repair failed.");
                });
                Check(checks, "Both-preserves-" + module.Name, delegate {
                    var go = Create(preview, module); var original = go.GetComponent(module);
                    Assert(!Repair(repair, go, resolutionType, modeType, "Both"), "Both replaced an already compatible module.");
                    Assert(go.GetComponent(module) == original, "Both replaced the original component.");
                });
                Check(checks, "Unknown-preserves-" + module.Name, delegate {
                    var go = Create(preview, module); var original = go.GetComponent(module);
                    ExpectBlocked(repair, go, resolutionType, modeType);
                    Assert(go.GetComponent(module) == original, "Unknown changed an original input module.");
                });
            }
            Check(checks, "Both-preserves-two-existing-compatible-modules", delegate {
                var go = Create(preview, typeof(StandaloneInputModule), newType);
                var old = go.GetComponent<StandaloneInputModule>(); var newer = go.GetComponent(newType);
                Assert(!Repair(repair, go, resolutionType, modeType, "Both"), "Both changed a valid existing setup.");
                Assert(go.GetComponent<StandaloneInputModule>() == old && go.GetComponent(newType) == newer, "Both removed a compatible module.");
            });
            Check(checks, "Unknown-does-not-add-a-module", delegate {
                var go = Create(preview); ExpectBlocked(repair, go, resolutionType, modeType);
                Assert(go.GetComponents<BaseInputModule>().Length == 0, "Unknown added a guessed module.");
            });
        }
        finally
        {
            foreach (var root in preview.GetRootGameObjects()) Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(preview);
        }
        var rows = JArray.FromObject(checks);
        var failed = rows.Count(row => !(bool)row["passed"]);
        var report = new JObject { ["unityVersion"] = Application.unityVersion, ["runtime"] = "Mono",
            ["passed"] = rows.Count - failed, ["failed"] = failed, ["checks"] = rows };
        File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
        return report.ToString(Formatting.None);
    }

    static Type FindType(string name)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(value => value != null);
        if (type == null) throw new InvalidOperationException("Required qualification type is missing: " + name);
        return type;
    }
    static GameObject Create(Scene scene, params Type[] modules)
    {
        var go = new GameObject("UniBridge Input Backend Qualification", typeof(EventSystem));
        SceneManager.MoveGameObjectToScene(go, scene);
        foreach (var module in modules) go.AddComponent(module);
        return go;
    }
    static bool Repair(MethodInfo method, GameObject go, Type resolutionType, Type modeType, string mode)
    {
        var resolution = Activator.CreateInstance(resolutionType, new[] { Enum.Parse(modeType, mode), "qualification fixture", null });
        return (bool)method.Invoke(null, new[] { (object)go, resolution });
    }
    static void ExpectBlocked(MethodInfo method, GameObject go, Type resolutionType, Type modeType)
    {
        try { Repair(method, go, resolutionType, modeType, "Unknown"); }
        catch (TargetInvocationException exception) { if (exception.InnerException is InvalidOperationException) return; throw; }
        throw new Exception("Unknown mode should have been blocked.");
    }
    static void Check(List<object> checks, string name, Action action)
    {
        try { action(); checks.Add(new { name, passed = true }); }
        catch (Exception exception) { checks.Add(new { name, passed = false, detail = exception.ToString() }); }
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
