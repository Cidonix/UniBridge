using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

internal static class InputApiRegression
{
    static readonly List<object> Checks = new List<object>();
    static readonly MetadataReference[] Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    static MetadataReference Old, New;
    const string LegacySource = "namespace UnityEngine { public static class Input { public static bool anyKey { get { return false; } } public static bool GetKey(int code) { return false; } public static float GetAxis(string name) { return 0; } } }";
    const string NewSource = "namespace UnityEngine.InputSystem { public class InputAction { public void Enable() {} } public static class Keyboard { public static bool anyKey { get { return false; } } } } namespace UnityEngine.InputSystem.Controls { public class KeyControl { public bool isPressed { get { return false; } } } }";

    static int Main(string[] args)
    {
        Old = Emit("UnityEngine.InputLegacyModule", LegacySource);
        New = Emit("Unity.InputSystem", NewSource);
        Test("new-mode-warns-for-bound-old-api", () => Count(Analyze("public class P { bool Read() { return UnityEngine.Input.GetKey(1); } }", InputBackendMode.New), 1));
        Test("qualified-old-member-has-no-type-duplicate", () => {
            var report = Analyze("public class P { bool Read() { return global::UnityEngine.Input.anyKey; } }", InputBackendMode.New); Count(report, 1); Equal("anyKey", report.hints[0].symbol);
        });
        Test("alias-usage-resolves-metadata-symbol", () => Count(Analyze("using UInput = UnityEngine.Input; public class P { bool Read() { return UInput.GetKey(1); } }", InputBackendMode.New), 1));
        Test("static-using-usage-resolves-metadata-symbol", () => Count(Analyze("using static UnityEngine.Input; public class P { bool Read() { return GetKey(1); } }", InputBackendMode.New), 1));
        Test("alias-import-alone-is-not-a-usage", () => Count(Analyze("using UInput = UnityEngine.Input; public class P {}", InputBackendMode.New), 0));
        Test("namespace-import-alone-is-not-a-usage", () => Count(Analyze("using UnityEngine.InputSystem; public class P {}", InputBackendMode.Old), 0));
        Test("inactive-preprocessor-branch-is-not-analyzed", () => Count(Analyze("public class P { bool Read() {\n#if NEVER\nreturn UnityEngine.Input.anyKey;\n#else\nreturn false;\n#endif\n} }", InputBackendMode.New), 0));
        Test("active-preprocessor-branch-is-analyzed", () => Count(Analyze("public class P { bool Read() {\n#if TEST\nreturn UnityEngine.Input.anyKey;\n#else\nreturn false;\n#endif\n} }", InputBackendMode.New, defines: new[] { "TEST" }), 1));
        Test("source-owned-input-name-is-not-unity-api", () => Count(Analyze("public static class Input { public static bool GetKey(int x) {return false;} } public class P { bool Read() { return Input.GetKey(1); } }", InputBackendMode.New), 0));
        Test("source-owned-qualified-input-is-not-metadata-api", () => Count(Analyze(LegacySource + " public class P { bool Read() { return UnityEngine.Input.GetKey(1); } }", InputBackendMode.New), 0));
        Test("same-namespace-type-in-other-assembly-is-not-unity-api", () => Count(Analyze("public class P { bool Read() { return UnityEngine.Input.anyKey; } }", InputBackendMode.New, references: Framework.Concat(new[] { Emit("Game.Code", LegacySource) }).ToArray()), 0));
        Test("same-new-namespace-in-other-assembly-is-not-input-package", () => Count(Analyze("public class P { void Read() { new UnityEngine.InputSystem.InputAction().Enable(); } }", InputBackendMode.Old, references: Framework.Concat(new[] { Old, Emit("Game.Input", NewSource) }).ToArray()), 0));
        Test("legacy-monolithic-unity-assembly-is-recognized", () => Count(Analyze("public class P { bool Read() { return UnityEngine.Input.anyKey; } }", InputBackendMode.New, references: Framework.Concat(new[] { Emit("UnityEngine", LegacySource) }).ToArray()), 1));
        Test("old-mode-warns-for-new-package-api", () => {
            var report = Analyze("public class P { bool Read() { return UnityEngine.InputSystem.Keyboard.anyKey; } }", InputBackendMode.Old);
            Count(report, 1); Equal("New", report.hints[0].apiBackend); Equal("Unity.InputSystem", report.hints[0].assemblyName);
        });
        Test("old-mode-warns-for-nested-package-api", () => Count(Analyze("public class P { bool Read(UnityEngine.InputSystem.Controls.KeyControl key) { return key.isPressed; } }", InputBackendMode.Old), 2));
        Test("new-api-type-alias-is-recognized", () => Count(Analyze("using K = UnityEngine.InputSystem.Keyboard; public class P { bool Read() { return K.anyKey; } }", InputBackendMode.Old), 1));
        Test("old-mode-allows-old-api", () => Count(Analyze("public class P { bool Read() { return UnityEngine.Input.anyKey; } }", InputBackendMode.Old), 0));
        Test("new-mode-allows-new-api", () => Count(Analyze("public class P { bool Read() { return UnityEngine.InputSystem.Keyboard.anyKey; } }", InputBackendMode.New), 0));
        Test("both-allows-both-apis", () => Count(Analyze("public class P { bool Read() { return UnityEngine.Input.anyKey || UnityEngine.InputSystem.Keyboard.anyKey; } }", InputBackendMode.Both), 0));
        Test("unknown-mode-does-not-guess", () => { var r = Analyze("public class P { bool Read() { return UnityEngine.Input.anyKey; } }", InputBackendMode.Unknown); Equal("unavailable", r.status); Equal(0, r.totalHints); });
        Test("strings-and-comments-do-not-create-hints", () => Count(Analyze("public class P { string s = \"UnityEngine.Input.GetKey(1)\"; /* UnityEngine.Input.anyKey */ }", InputBackendMode.New), 0));
        Test("hints-report-correct-one-based-location", () => { var r = Analyze("public class P {\n bool Read() { return UnityEngine.Input.anyKey; }\n}", InputBackendMode.New); Count(r, 1); Equal(2, r.hints[0].line); Equal(6, r.hints[0].length); });
        Test("nameof-old-api-does-not-require-old-backend", () => Count(Analyze("public class P { string Read() { return nameof(UnityEngine.Input.anyKey); } }", InputBackendMode.New), 0));
        Test("typeof-input-type-does-not-require-backend", () => Count(Analyze("public class P { System.Type Read() { return typeof(UnityEngine.Input); } }", InputBackendMode.New), 0));
        Test("limit-retains-total-and-truncated", () => { var source = "public class P { bool Read() { return " + string.Join(" || ", Enumerable.Repeat("UnityEngine.Input.anyKey", 20)) + "; } }"; var r = Analyze(source, InputBackendMode.New, maxHints: 3); Equal(20, r.totalHints); Equal(3, r.hints.Count); Equal(true, r.truncated); });
        Test("binding-errors-produce-partial-independent-advice", () => { var r = Analyze("public class P { bool Read() { Missing(); return UnityEngine.Input.anyKey; } }", InputBackendMode.New); Equal("partial", r.status); Equal(1, r.hints.Count); Require(r.bindingErrors > 0); });
        Test("unresolved-new-api-does-not-invent-hint", () => { var r = Analyze("public class P { bool Read() { return UnityEngine.InputSystem.Keyboard.anyKey; } }", InputBackendMode.Old, references: Framework.Concat(new[] { Old }).ToArray()); Equal("partial", r.status); Equal(0, r.totalHints); });
        Test("missing-compilation-is-unavailable", () => Equal("unavailable", InputApiAnalyzer.Analyze(null, null, InputBackendMode.New).status));
        Test("semantic-time-budget-is-explicit", () => {
            var source = "public class P { bool Read() { return " + string.Join(" || ", Enumerable.Repeat("UnityEngine.Input.anyKey", 6000)) + "; } }";
            var r = Analyze(source, InputBackendMode.New, milliseconds: 1); Equal("partial", r.status); Equal(true, r.truncated); Require(r.limitations.Any(reason => reason.Contains("budget")));
        });
        Test("basic-validation-skips-semantic-context", () => { var r = ManageScript.AnalyzeInputApiHints("", "basic"); Equal("skipped", r.status); Equal(0, UnityCompilationReferenceContext.Collected); });
        Test("wrapper-uses-actual-project-setting", () => { SerializedObject.ActiveInputHandler = 1; var r = InputApiHints.AnalyzeSource("public class P { bool Read() { return UnityEngine.Input.anyKey; } }"); Count(r, 1); Equal("New", r.inputBackend); Equal("PlayerSettings.activeInputHandler", r.backendSource); });
        Test("wrapper-partial-context-is-not-reported-complete", () => { SerializedObject.ActiveInputHandler = 1; UnityCompilationReferenceContext.Partial = true; Equal("partial", InputApiHints.AnalyzeSource("public class P {}").status); });
        Test("wrapper-unavailable-mode-does-not-build-context", () => { SerializedObject.ActiveInputHandler = 99; Equal("unavailable", InputApiHints.AnalyzeSource("public class P {}").status); Equal(0, UnityCompilationReferenceContext.Collected); });
        Test("wrapper-source-budget-is-explicit", () => { SerializedObject.ActiveInputHandler = 1; Equal("unavailable", InputApiHints.AnalyzeSource(new string(' ', 1000001)).status); Equal(0, UnityCompilationReferenceContext.Collected); });
        Test("closed-input-report-schema-accepts-real-report", () => {
            var report = Analyze("public class P { bool Read() { return UnityEngine.Input.anyKey; } }", InputBackendMode.New);
            var schema = ActionOutputSchema.Compose(new[] { ActionOutputSchema.DTO(typeof(InputApiReport)) });
            Equal(0, ActionOutputValidation.Check(JToken.FromObject(report), schema, (JObject)schema["$defs"]).Count);
        });
        Test("closed-input-report-schema-rejects-extra-fields", () => {
            var value = JObject.FromObject(new InputApiReport()); value["invented"] = true;
            var schema = ActionOutputSchema.Compose(new[] { ActionOutputSchema.DTO(typeof(InputApiReport)) });
            Require(ActionOutputValidation.Check(value, schema, (JObject)schema["$defs"]).Count > 0);
        });
        Test("script-action-contracts-compose-without-definition-collisions", () => {
            foreach (var owner in new[] { "UniBridge_Script", "UniBridge_CreateScript", "UniBridge_ApplyTextEdits", "UniBridge_ScriptApplyEdits", "UniBridge_ValidateScript" })
                ActionOutputSchema.Compose(ScriptActionOutputRules.Build(owner).Values);
        });
        Test("intelligence-action-contracts-compose-without-definition-collisions", () => ActionOutputSchema.Compose(IntelligenceActionOutputRules.Build("UniBridge_ScriptIntelligence").Values));
        Test("script-validate-accepts-independent-advisory-reports", () => {
            var schema = ActionOutputSchema.Compose(new[] { ScriptActionOutputRules.Build("UniBridge_Script")["validate"] });
            var value = JObject.FromObject(new { success = true, message = "Validated.", data = new { diagnostics = new object[0], obsoleteApiHints = new ObsoleteApiReport(), inputApiHints = new InputApiReport() } });
            Equal(0, ActionOutputValidation.Check(value, schema, (JObject)schema["$defs"]).Count);
            value["data"]["inputApiHints"]["invented"] = true;
            Require(ActionOutputValidation.Check(value, schema, (JObject)schema["$defs"]).Count > 0);
        });
        foreach (var path in Directory.GetFiles(args[1], "*.cs", SearchOption.AllDirectories))
            Test("production-syntax-" + Path.GetRelativePath(args[1], path), () => {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9));
                var errors = tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
                if (errors.Length > 0) throw new Exception(string.Join("\n", errors.Select(error => error.ToString())));
            });
        var failed = Checks.Cast<object>().Count(check => !(bool)JObject.FromObject(check)["passed"]);
        File.WriteAllText(args[0], JsonConvert.SerializeObject(new { passed = Checks.Count - failed, failed, checks = Checks }, Formatting.Indented));
        Console.WriteLine((Checks.Count - failed) + "/" + Checks.Count + " PASS");
        return failed == 0 ? 0 : 1;
    }

    static void Test(string name, Action action)
    {
        SerializedObject.ActiveInputHandler = 2; AssetDatabase.FailRead = false; UnityCompilationReferenceContext.Collected = 0; UnityCompilationReferenceContext.Partial = false;
        UnityCompilationReferenceContext.References = Framework.Concat(new[] { Old, New }).ToList();
        try { action(); Checks.Add(new { name, passed = true }); }
        catch (Exception exception) { Checks.Add(new { name, passed = false, detail = exception.ToString() }); }
    }
    static InputApiReport Analyze(string source, InputBackendMode mode, int maxHints = 100, int milliseconds = 2500, string[] defines = null, MetadataReference[] references = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: defines ?? Array.Empty<string>()));
        var compilation = CSharpCompilation.Create("Submitted", new[] { tree }, references ?? Framework.Concat(new[] { Old, New }), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return InputApiAnalyzer.Analyze(compilation, tree, mode, maxHints, milliseconds);
    }
    static MetadataReference Emit(string name, string source)
    {
        var compilation = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, Framework, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var memory = new MemoryStream(); var result = compilation.Emit(memory);
        if (!result.Success) throw new Exception(string.Join("\n", result.Diagnostics));
        return MetadataReference.CreateFromImage(memory.ToArray());
    }
    static void Count(InputApiReport report, int count) { Equal(count, report.totalHints); Equal(count, report.hints.Count); Require(report.status == "available" || report.status == "partial"); }
    static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
}
