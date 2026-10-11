// Qualification only: analyzes submitted strings with real Editor metadata, never changes PlayerSettings or project source.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

public static class UniBridgeInputApiLiveRegression
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tools/UniBridge Qualification/Input API Hints")]
    public static void RunFromMenu()
    {
        var report = Path.Combine(Path.GetTempPath(), "unibridge-input-api-native-" + Guid.NewGuid().ToString("N") + ".json");
        Debug.Log("UniBridge Input API qualification: " + Run(report) + " Report: " + report);
    }

    public static string Run(string reportPath)
    {
        var checks = new List<object>();
        var observations = new JObject();
        var fixtureAssetPath = OwnFixtureAssetPath();
        observations["fixtureAssetPath"] = fixtureAssetPath;
        var helper = FindType("Cidonix.UniBridge.MCP.Editor.Helpers.InputApiAnalyzer");
        var modeType = FindType("Cidonix.UniBridge.MCP.Editor.Helpers.InputBackendMode");
        var collect = FindType("Cidonix.UniBridge.MCP.Editor.Helpers.UnityCompilationReferenceContext").GetMethod("Collect", Static);
        var context = collect.Invoke(null, new object[] { fixtureAssetPath });
        var referenceReport = context.GetType().GetField("References", Instance).GetValue(context);
        var references = (IEnumerable<MetadataReference>)referenceReport.GetType().GetField("References", Instance).GetValue(referenceReport);
        var options = (CSharpCompilationOptions)context.GetType().GetField("CompilationOptions", Instance).GetValue(context);
        var analyze = helper.GetMethod("Analyze", Static);
        Func<string, string, string[], JObject> run = delegate(string source, string mode, string[] defines)
        {
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: defines ?? new string[0]));
            var compilation = CSharpCompilation.Create("UniBridge_InputApi_Native_Qualification", new[] { tree }, references, options);
            return JObject.FromObject(analyze.Invoke(null, new[] { (object)compilation, tree, Enum.Parse(modeType, mode), 100, 2500 }));
        };
        Check(checks, "actual-new-mode-old-api", delegate {
            var result = run("public class P { bool Read() { return UnityEngine.Input.GetKey(UnityEngine.KeyCode.Space); } }", "New", null);
            Count(result, 1); observations["newOldApi"] = result;
        });
        Check(checks, "actual-old-mode-new-api", delegate {
            var result = run("public class P { bool Read() { return UnityEngine.InputSystem.Keyboard.current != null; } }", "Old", null);
            Count(result, 1); observations["oldNewApi"] = result;
        });
        Check(checks, "actual-both-mode-both-apis", delegate {
            Count(run("public class P { bool Read() { return UnityEngine.Input.anyKey || UnityEngine.InputSystem.Keyboard.current != null; } }", "Both", null), 0);
        });
        Check(checks, "actual-alias-bound-old-api", delegate {
            Count(run("using OldInput = UnityEngine.Input; public class P { bool Read() { return OldInput.anyKey; } }", "New", null), 1);
        });
        Check(checks, "actual-static-using-bound-old-api", delegate {
            Count(run("using static UnityEngine.Input; public class P { bool Read() { return GetKey(UnityEngine.KeyCode.Space); } }", "New", null), 1);
        });
        Check(checks, "actual-inactive-branch", delegate {
            Count(run("public class P { bool Read() {\n#if DISABLED\nreturn UnityEngine.Input.anyKey;\n#else\nreturn false;\n#endif\n} }", "New", null), 0);
        });
        Check(checks, "actual-source-owned-input-name", delegate {
            Count(run("public static class Input { public static bool anyKey { get { return false; } } } public class P { bool Read() { return Input.anyKey; } }", "New", null), 0);
        });
        Check(checks, "actual-nameof-is-not-a-backend-read", delegate {
            Count(run("public class P { string Read() { return nameof(UnityEngine.Input.anyKey); } }", "New", null), 0);
        });
        Check(checks, "unknown-does-not-guess", delegate {
            var report = run("public class P { bool Read() { return UnityEngine.Input.anyKey; } }", "Unknown", null);
            Assert((string)report["status"] == "unavailable" && (int)report["totalHints"] == 0, "Unknown backend was guessed.");
        });
        Check(checks, "basic-validation-skips-input-analysis", delegate {
            var method = FindType("Cidonix.UniBridge.MCP.Editor.Tools.ManageScript").GetMethod("AnalyzeInputApiHints", Static,
                null, new[] { typeof(string), typeof(string), typeof(string) }, null);
            var report = JObject.FromObject(method.Invoke(null, new object[] { "public class P {}", "basic", null }));
            Assert((string)report["status"] == "skipped", "Basic validation ran semantic Input analysis.");
        });
        Check(checks, "actual-wrapper-reports-real-project-backend", delegate {
            var wrapper = FindType("Cidonix.UniBridge.MCP.Editor.Helpers.InputApiHints").GetMethod("AnalyzeSource", Static);
            var report = JObject.FromObject(wrapper.Invoke(null, new object[] { "public class P { bool Read() { return UnityEngine.Input.anyKey; } }", fixtureAssetPath, 100 }));
            var resolution = FindType("Cidonix.UniBridge.MCP.Editor.Helpers.InputBackendResolver").GetMethod("Resolve", Static).Invoke(null, null);
            var actualMode = resolution.GetType().GetField("Mode", Instance).GetValue(resolution).ToString();
            Assert((string)report["inputBackend"] == actualMode, "Wrapper disagrees with the current project setting.");
            Assert((int)report["totalHints"] == (actualMode == "New" ? 1 : 0), "Wrapper produced incorrect mode-dependent hints.");
            Assert((string)report["status"] == "available", "The owning fixture assembly context did not complete: " + report);
            observations["actualProject"] = report;
        });
        var rows = JArray.FromObject(checks);
        var failed = rows.Count(row => !(bool)row["passed"]);
        var result = new JObject { ["unityVersion"] = Application.unityVersion, ["passed"] = rows.Count - failed,
            ["failed"] = failed, ["checks"] = rows, ["observations"] = observations };
        File.WriteAllText(reportPath, result.ToString(Formatting.Indented));
        return result.ToString(Formatting.None);
    }

    // The compiler supplies this fixture's own location; no authored asset is searched or changed.
    static string OwnFixtureAssetPath([CallerFilePath] string sourcePath = "")
    {
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var fullPath = Path.GetFullPath(Path.IsPathRooted(sourcePath) ? sourcePath : Path.Combine(projectRoot, sourcePath));
        var prefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new Exception("Cannot identify the imported qualification fixture's owning assembly source: " + sourcePath);
        var assetPath = fullPath.Substring(prefix.Length).Replace('\\', '/');
        if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) && !assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            throw new Exception("Qualification fixture must be imported into Assets or Packages: " + assetPath);
        return assetPath;
    }

    static Type FindType(string name)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(value => value != null);
        if (type == null) throw new Exception("Required qualification type missing: " + name);
        return type;
    }
    static void Count(JObject result, int count)
    {
        Assert((int)result["totalHints"] == count && ((JArray)result["hints"]).Count == count, "Wrong bound Input hint count: " + result);
        Assert((string)result["status"] == "available", "Physical metadata analysis did not complete: " + result);
    }
    static void Check(List<object> checks, string name, Action action)
    {
        try { action(); checks.Add(new { name, passed = true }); }
        catch (Exception exception) { checks.Add(new { name, passed = false, detail = exception.ToString() }); }
    }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
