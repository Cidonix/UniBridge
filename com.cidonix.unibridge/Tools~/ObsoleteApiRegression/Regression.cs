using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

internal static class ObsoleteApiRegression
{
    static readonly List<Check> Checks = new List<Check>();
    static readonly Dictionary<string, object> SemanticObservations = new Dictionary<string, object>();
    static readonly MetadataReference[] RuntimeReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: Regression REPORT PROVENANCE");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Run the regression from an Administrator terminal.");

        foreach (var fixture in ObsoleteFixtures.All())
        {
            Run(fixture.Name, () => CheckFixture(fixture));
        }

        Run("metadata-symbol", MetadataSymbol);
        Run("source-replaces-old-metadata-type", SourceReplacesMetadataType);
        Run("three-usages-have-no-nested-node-duplicates", RepeatedUsages);
        Run("hint-limit-retains-total-and-truncated", HintLimit);
        Run("ambiguous-call-does-not-invent-resolved-hint", AmbiguousCall);
        Run("custom-diagnostic-id", CustomDiagnosticId);
        Run("obsolete-method-group", MethodGroup);
        Run("obsolete-generic-method", GenericMethod);
        Run("obsolete-null-conditional-call", ConditionalCall);
        Run("obsolete-explicit-conversion", ExplicitConversion);
        Run("obsolete-implicit-conversion", ImplicitConversion);
        Run("unchanged-return-type-replacement", SameReturnTypeReplacement);
        Run("changed-return-type-replacement", ChangedReturnTypeReplacement);
        Run("ambiguous-replacement-is-not-guessed", AmbiguousReplacement);
        Run("replacement-overload-matches-old-signature", ExactReplacementOverload);
        Run("missing-replacement-remains-unresolved", MissingReplacement);
        Run("no-message-has-no-invented-replacement", NoReplacementMessage);
        Run("unity-upgradable-replacement-marker", UnityUpgradableReplacement);
        Run("qualified-replacement-symbol", QualifiedReplacement);
        Run("obsolete-conversion-back-guidance", ConversionBackGuidance);
        Run("type-replacement-symbol", TypeReplacement);
        Run("inaccessible-replacement-is-not-advertised", InaccessibleReplacement);
        Run("obsolete-containing-type-inferred-receiver", () => InferredObsoleteReceiver(false));
        Run("obsolete-containing-type-inferred-conditional-receiver", () => InferredObsoleteReceiver(true));

        var provenance = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(args[1]));
        var result = new {
            provenance,
            runtime = RuntimeInformation.FrameworkDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            administrator = true,
            loadedAssemblies = new[] {typeof(CSharpCompilation).Assembly, typeof(MetadataReference).Assembly,
                typeof(System.Collections.Immutable.ImmutableArray<>).Assembly, typeof(System.Reflection.Metadata.MetadataReader).Assembly}
                .Select(assembly => new {name=assembly.GetName().Name, version=assembly.GetName().Version.ToString(), location=assembly.Location}).ToArray(),
            passed = Checks.Count(item => item.passed),
            failed = Checks.Count(item => !item.passed),
            checks = Checks,
            semanticObservations = SemanticObservations
        };
        File.WriteAllText(args[0], JsonSerializer.Serialize(result, new JsonSerializerOptions {WriteIndented=true, IncludeFields=true}));
        Console.WriteLine(JsonSerializer.Serialize(new {report=args[0], result.passed, result.failed}));
        return result.failed == 0 ? 0 : 1;
    }

    static void CheckFixture(ObsoleteFixture fixture)
    {
        var parsed = Compile(fixture.Source, fixture.AdditionalSource, fixture.Defines);
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree);
        Assert(report != null && report.hints != null, "Collector returned no structured report.");
        if (!fixture.ExpectHint)
        {
            Assert(report.hints.Count == 0, "Unexpected hint: " + Summary(report));
        }
        else
        {
            var relevant = report.hints.Where(hint =>
                (string.IsNullOrEmpty(fixture.Message) ? string.IsNullOrEmpty(hint.message) : hint.message == fixture.Message) &&
                hint.severity == (fixture.IsError ? "error" : "warning")).ToArray();
            Assert(relevant.Length > 0, "Expected author's message/error flag missing: " + Summary(report));
            var usageStart = fixture.Source.LastIndexOf(fixture.UsageText, StringComparison.Ordinal);
            Assert(usageStart >= 0, "Fixture usage marker missing.");
            Assert(relevant.Any(hint => AtUsage(parsed.tree, hint, usageStart, fixture.UsageText.Length)),
                "Hint location does not overlap the API reference: " + Summary(report));
            foreach (var hint in relevant)
            {
                Assert(hint.binding == "resolved", "Hint is not marked as semantically resolved.");
                Assert(!string.IsNullOrEmpty(hint.symbol) && !string.IsNullOrEmpty(hint.signature), "Semantic symbol identity missing.");
            }
        }
        if (fixture.Incomplete)
        {
            Assert(report.status == "partial" && report.bindingErrors > 0 && report.limitations.Count > 0,
                "Incomplete binding must be disclosed: " + Summary(report));
        }
    }

    static void MetadataSymbol()
    {
        const string api = "public class MetadataApi { [System.Obsolete(\"Use NewCall\")] public static void OldCall() {} }";
        var reference = EmitReference("OldApi", api);
        var parsed = Compile("class Use { void Run() { MetadataApi.OldCall(); } }", extraReferences:new[] {reference});
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree);
        Assert(report.hints.Count == 1 && report.hints[0].message == "Use NewCall", "Metadata attribute was not read: " + Summary(report));
        Assert(report.hints[0].symbol.Contains("MetadataApi", StringComparison.Ordinal), "Metadata symbol identity missing.");
    }

    static void SourceReplacesMetadataType()
    {
        var reference = EmitReference("OldApi", "namespace Domain { [System.Obsolete(\"Stale metadata\")] public class Api { [System.Obsolete(\"Stale member\")] public static void Call() {} } }");
        var parsed = Compile("namespace Domain { public class Api { public static void Call() {} } } class Use { void Run() { Domain.Api.Call(); } }", extraReferences:new[] {reference});
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree);
        Assert(report.hints.Count == 0, "Source replacement inherited stale obsolete metadata: " + Summary(report));
    }

    static string RepeatedSource => "class Api { [System.Obsolete(\"Use New\")] public static void Old() {} }\nclass Use { void Run() {\nApi.Old();\nApi.Old();\nApi.Old();\n} }";

    static void RepeatedUsages()
    {
        var parsed = Compile(RepeatedSource);
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree);
        Assert(report.hints.Count == 3 && report.totalHints == 3 && !report.truncated,
            "Expected exactly one hint per call: " + Summary(report));
        Assert(report.hints.Select(hint => (hint.symbol, hint.line, hint.column)).Distinct().Count() == 3, "Duplicate nested syntax node hints.");
    }

    static void HintLimit()
    {
        var parsed = Compile(RepeatedSource);
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree, 1);
        Assert(report.hints.Count == 1 && report.totalHints == 3 && report.truncated,
            "Hint limit must retain the actual total: " + Summary(report));
    }

    static void AmbiguousCall()
    {
        var parsed = Compile("class Api { [System.Obsolete(\"Wrong guess\")] public static void Call(string value) {} public static void Call(System.Exception value) {} } class Use { void Run() { Api.Call(null); } }");
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree);
        Assert(report.hints.Count == 0 && report.status == "partial" && report.bindingErrors > 0,
            "An ambiguous candidate was advertised as a resolved obsolete reference: " + Summary(report));
    }

    static void CustomDiagnosticId()
    {
        var parsed = Compile("class Api { [System.Obsolete(\"Use New\", DiagnosticId=\"API9001\")] public static void Old() {} } class Use { void Run() { Api.Old(); } }");
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree);
        Assert(report.hints.Count == 1 && report.hints[0].diagnosticId == "API9001", "Custom ObsoleteAttribute diagnostic ID was lost: " + Summary(report));
    }

    static void MethodGroup() => PositiveSource("class Api { [System.Obsolete(\"Use New\")] public static void Old() {} } class Use { System.Action Run() { return Api.Old; } }", "Use New");
    static void GenericMethod() => PositiveSource("class Api { [System.Obsolete(\"Use New\")] public static T Old<T>() {return default(T);} } class Use { int Run() {return Api.Old<int>();} }", "Use New");
    static void ConditionalCall() => PositiveSource("class Api { [System.Obsolete(\"Use New\")] public void Old() {} } class Use { void Run(Api api) { api?.Old(); } }", "Use New");
    static void ExplicitConversion() => PositiveSource("class Api { [System.Obsolete(\"Use Convert\")] public static explicit operator int(Api value) {return 1;} } class Use { int Run(Api api) {return (int)api;} }", "Use Convert");
    static void ImplicitConversion() => PositiveSource("class Api { [System.Obsolete(\"Use Convert\")] public static implicit operator int(Api value) {return 1;} } class Use { int Run(Api api) {return api;} }", "Use Convert");

    static void SameReturnTypeReplacement() => Replacement("int", false);
    static void ChangedReturnTypeReplacement() => Replacement("string", true);

    static void AmbiguousReplacement()
    {
        var hint = SingleHint("class Api { [System.Obsolete(\"Use Modern instead\")] public static int Old() {return 1;} public static int Modern(int value) {return value;} public static int Modern(string value) {return 1;} } class Use { int Run() {return Api.Old();} }");
        Assert(hint.replacement.status == "ambiguous" && hint.replacement.candidates.Count == 2 && hint.replacement.symbol == null,
            "Multiple replacement candidates must remain explicitly ambiguous.");
    }

    static void ExactReplacementOverload()
    {
        var hint = SingleHint("class Api { [System.Obsolete(\"Use Modern instead\")] public static int Old(int value) {return value;} public static int Modern(int value) {return value;} public static string Modern(string value) {return value;} } class Use { int Run() {return Api.Old(1);} }");
        Assert(hint.replacement.status == "resolved" && hint.replacement.signature.Contains("Modern(int", StringComparison.Ordinal) && !hint.replacement.returnTypeChanged,
            "Replacement must select the unique matching overload.");
    }

    static void MissingReplacement()
    {
        var hint = SingleHint("class Api { [System.Obsolete(\"Use Missing instead\")] public static int Old() {return 1;} } class Use { int Run() {return Api.Old();} }");
        Assert(hint.replacement.status == "unresolved" && hint.replacement.symbol == null && hint.replacement.candidates.Count == 0,
            "A missing migration target must not be invented.");
    }

    static void NoReplacementMessage()
    {
        var hint = SingleHint("class Api { [System.Obsolete] public static int Old() {return 1;} } class Use { int Run() {return Api.Old();} }");
        Assert(hint.replacement.status == "notSpecified" && hint.replacement.symbol == null, "Missing migration message must remain notSpecified.");
    }

    static void UnityUpgradableReplacement()
    {
        var hint = SingleHint("class Api { [System.Obsolete(\"Renamed. (UnityUpgradable) -> Modern\")] public static int Old() {return 1;} public static int Modern() {return 1;} } class Use { int Run() {return Api.Old();} }");
        Assert(hint.replacement.status == "resolved" && hint.replacement.symbol.Contains("Modern", StringComparison.Ordinal), "UnityUpgradable marker was not recognized.");
    }

    static void QualifiedReplacement()
    {
        var hint = SingleHint("namespace Domain { class OldApi { [System.Obsolete(\"Use Domain.ModernApi.Current instead\")] public static int Old() {return 1;} } class ModernApi { public static string Current() {return \"value\";} } } class Use { int Run() {return Domain.OldApi.Old();} }");
        Assert(hint.replacement.status == "resolved" && hint.replacement.symbol.Contains("Domain.ModernApi.Current", StringComparison.Ordinal) && hint.replacement.returnTypeChanged,
            "Fully qualified replacement identity/return type missing.");
    }

    static void ConversionBackGuidance()
    {
        var hint = SingleHint("class OldValue {} class NewValue { [System.Obsolete(\"Avoid conversion\")] public static implicit operator OldValue(NewValue value) {return new OldValue();} } class Api { [System.Obsolete(\"Use Modern instead\")] public static OldValue Old() {return new OldValue();} public static NewValue Modern() {return new NewValue();} } class Use { OldValue Run() {return Api.Old();} }");
        Assert(hint.replacement.status == "resolved" && hint.replacement.returnTypeChanged &&
            hint.replacement.guidance.Contains("obsolete conversion", StringComparison.OrdinalIgnoreCase),
            "Advice must disclose that converting the new result back uses an obsolete conversion.");
    }

    static void TypeReplacement()
    {
        var hint = SingleHint("namespace Domain { [System.Obsolete(\"Use CurrentType instead\")] class OldType {} class CurrentType {} } class Use { Domain.OldType value; }");
        Assert(hint.replacement.status == "resolved" && hint.replacement.symbol.Contains("Domain.CurrentType", StringComparison.Ordinal), "Type migration target was not resolved in its namespace.");
    }

    static void InaccessibleReplacement()
    {
        var hint = SingleHint("class Api { [System.Obsolete(\"Use Modern instead\")] public static int Old() {return 1;} private static int Modern() {return 1;} } class Use { int Run() {return Api.Old();} }");
        Assert(hint.replacement.status == "unresolved" && hint.replacement.symbol == null,
            "A private replacement inaccessible at the call site must not be advertised as usable.");
    }

    static void InferredObsoleteReceiver(bool conditional)
    {
        var usage = conditional ? "value?.Current()" : "value.Current()";
        var source = "[System.Obsolete(\"Use ModernApi\")] class Api { public void Current() {} }\n"
            + "class Factory { public static Api Make() { return new Api(); } }\n"
            + "class Use { void Run() { var value = Factory.Make(); " + usage + "; } }";
        var parsed = Compile(source);
        var usageStart = source.LastIndexOf(usage, StringComparison.Ordinal);
        var usageSpan = new TextSpan(usageStart, usage.Length);
        var compilerDiagnostics = parsed.compilation.GetDiagnostics().Where(diagnostic =>
            diagnostic.Id == "CS0612" || diagnostic.Id == "CS0618" || diagnostic.Id == "CS0619").ToArray();
        var compilerWarnsAtCall = compilerDiagnostics.Any(diagnostic => diagnostic.Location.SourceTree == parsed.tree &&
            diagnostic.Location.SourceSpan.OverlapsWith(usageSpan));
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree);
        var hintAtCall = report.hints.Any(hint => AtUsage(parsed.tree, hint, usageStart, usage.Length));
        SemanticObservations[conditional ? "inferredConditionalReceiver" : "inferredReceiver"] = new {
            source,
            compilerWarnsAtCall,
            hintAtCall,
            compilerDiagnostics = compilerDiagnostics.Select(diagnostic => new {
                id=diagnostic.Id, message=diagnostic.GetMessage(),
                line=diagnostic.Location.GetLineSpan().StartLinePosition.Line+1,
                column=diagnostic.Location.GetLineSpan().StartLinePosition.Character+1,
                length=diagnostic.Location.SourceSpan.Length}).ToArray(),
            hints = report.hints
        };
        Assert(compilerDiagnostics.Length > 0 && report.hints.Count > 0,
            "Fixture must diagnose the explicit obsolete Api references in Factory.");
        Assert(hintAtCall == compilerWarnsAtCall,
            "Inferred receiver usage must follow the actual Roslyn obsolete diagnostic semantics: " + Summary(report));
    }

    static ObsoleteApiHint SingleHint(string source)
    {
        var parsed = Compile(source);
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree);
        Assert(report.hints.Count == 1, "Expected exactly one API usage: " + Summary(report));
        Assert(report.hints[0].replacement != null, "Replacement classification missing.");
        return report.hints[0];
    }

    static void Replacement(string replacementReturnType, bool changed)
    {
        var parsed = Compile("class Api { [System.Obsolete(\"Use Modern instead\")] public static int Old() {return 1;} public static " + replacementReturnType + " Modern() {return default(" + replacementReturnType + ");} } class Use { int Run() {return Api.Old();} }");
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree);
        Assert(report.hints.Count == 1 && report.hints[0].replacement != null, "Replacement report missing.");
        var hint = report.hints[0];
        Assert(hint.replacement.status == "resolved" && hint.replacement.symbol.Contains("Modern", StringComparison.Ordinal),
            "Unambiguous migration target wasn't resolved: " + Summary(report));
        Assert(hint.replacement.returnTypeChanged == changed, "Return type change flag incorrect.");
        Assert(!string.IsNullOrEmpty(hint.oldReturnType) && !string.IsNullOrEmpty(hint.replacement.newReturnType), "Return type metadata missing.");
    }

    static void PositiveSource(string source, string message)
    {
        var parsed = Compile(source);
        var report = ObsoleteApiAnalyzer.Analyze(parsed.compilation, parsed.tree);
        Assert(report.hints.Any(hint => hint.message == message && hint.binding == "resolved"), "Resolved semantic reference missing: " + Summary(report));
    }

    static (CSharpCompilation compilation, SyntaxTree tree) Compile(string source, string additionalSource=null, string[] defines=null, MetadataReference[] extraReferences=null)
    {
        var options = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols:defines ?? Array.Empty<string>());
        var tree = CSharpSyntaxTree.ParseText(source, options, "Candidate.cs");
        var trees = new List<SyntaxTree> {tree};
        if (additionalSource != null) trees.Add(CSharpSyntaxTree.ParseText(additionalSource, options, "Api.cs"));
        var refs = RuntimeReferences.Concat(extraReferences ?? Array.Empty<MetadataReference>());
        var compilation = CSharpCompilation.Create("Fixture_" + Guid.NewGuid().ToString("N"), trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (compilation, tree);
    }

    static MetadataReference EmitReference(string name, string source)
    {
        var parsed = Compile(source);
        using (var bytes = new MemoryStream())
        {
            var emitted = parsed.compilation.WithAssemblyName(name).Emit(bytes);
            Assert(emitted.Success, "Metadata fixture failed compilation: " + string.Join("; ", emitted.Diagnostics));
            return MetadataReference.CreateFromImage(bytes.ToArray());
        }
    }

    static bool AtUsage(SyntaxTree tree, ObsoleteApiHint hint, int usageStart, int usageLength)
    {
        var text = tree.GetText();
        if (hint.line <= 0 || hint.line > text.Lines.Count || hint.column <= 0 || hint.length <= 0) return false;
        var line = text.Lines[hint.line-1];
        var offset = line.Start + hint.column-1;
        if (offset > line.End || offset + hint.length > text.Length) return false;
        return new TextSpan(offset, hint.length).OverlapsWith(new TextSpan(usageStart, usageLength));
    }

    static string Summary(ObsoleteApiReport report) => JsonSerializer.Serialize(report, new JsonSerializerOptions {IncludeFields=true});

    static void Run(string name, Action action)
    {
        try {action(); Checks.Add(new Check {name=name, passed=true, detail="Semantic assertions passed."});}
        catch (Exception error) {Checks.Add(new Check {name=name, passed=false, detail=error.ToString()});}
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    sealed class Check
    {
        public string name;
        public bool passed;
        public string detail;
    }
}
