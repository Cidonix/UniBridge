using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Advisory metadata only. This analyzer never rewrites source or changes validation counts.
    internal sealed class InputApiReport
    {
        public string status = "available";
        public string inputBackend = "Unknown";
        public string backendSource;
        public List<InputApiHint> hints = new List<InputApiHint>();
        public int totalHints;
        public bool truncated;
        public int bindingErrors;
        public List<string> limitations = new List<string>();
        public string assemblyName;
        public string unityVersion;
        public int sourceFileCount;
        public int referenceCount;
    }

    internal sealed class InputApiHint
    {
        public string symbol, signature, apiBackend, assemblyName, severity, message, guidance;
        public int line, column, length;
    }

    internal sealed class InputApiCoverageReport
    {
        public string status = "available";
        public int analyzed, skipped;
        public bool truncated;
        public List<InputApiFileReport> reports = new List<InputApiFileReport>();
        public string guidance = "Narrow Query/Path or run Analyze for each unexamined script to complete Input API coverage.";
    }

    internal sealed class InputApiFileReport
    {
        public string path;
        public InputApiReport inputApiHints;
    }

    internal static class InputApiAnalyzer
    {
        internal static InputApiReport Analyze(CSharpCompilation compilation, SyntaxTree tree, InputBackendMode mode,
            int maxHints = 100, int budgetMilliseconds = 2500)
        {
            var report = new InputApiReport { inputBackend = mode.ToString() };
            maxHints = Math.Max(1, Math.Min(100, maxHints));
            if (mode == InputBackendMode.Unknown)
            {
                report.status = "unavailable";
                report.limitations.Add("Active Input Handling is unknown; no API mismatch was guessed.");
                return report;
            }
            if (compilation == null || tree == null || !compilation.SyntaxTrees.Contains(tree))
            {
                report.status = "unavailable";
                report.limitations.Add("A semantic model for the submitted source is unavailable.");
                return report;
            }
            using (var cancellation = new CancellationTokenSource(Math.Max(1, Math.Min(2500, budgetMilliseconds))))
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    var model = compilation.GetSemanticModel(tree);
                    report.bindingErrors = model.GetDiagnostics(cancellationToken: cancellation.Token)
                        .Count(item => item.Severity == DiagnosticSeverity.Error);
                    if (report.bindingErrors > 0)
                    {
                        report.status = "partial";
                        report.limitations.Add("Unresolved source or metadata can hide additional Input API usages; validation diagnostics are reported separately.");
                    }
                    if (mode == InputBackendMode.Both) return report;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var count = 0;
                    foreach (var node in tree.GetRoot(cancellation.Token).DescendantNodes())
                    {
                        if (++count > 25000 || watch.ElapsedMilliseconds >= budgetMilliseconds)
                        {
                            report.status = "partial";
                            report.truncated = true;
                            report.limitations.Add("The semantic traversal/time budget was reached; additional usages may exist.");
                            break;
                        }
                        if (!(node is SimpleNameSyntax name) || name.Ancestors().Any(parent => parent is UsingDirectiveSyntax)) continue;
                        if (name.Ancestors().Any(parent => parent is TypeOfExpressionSyntax)) continue;
                        if (name.Ancestors().OfType<InvocationExpressionSyntax>().Any(invocation =>
                            invocation.Expression is IdentifierNameSyntax identifierName && identifierName.Identifier.Text == "nameof" &&
                            model.GetOperation(invocation, cancellation.Token) is INameOfOperation)) continue;
                        var symbol = model.GetSymbolInfo(name, cancellation.Token).Symbol;
                        if (symbol is IAliasSymbol alias) symbol = alias.Target;
                        if (symbol == null && name is IdentifierNameSyntax identifier)
                            symbol = model.GetAliasInfo(identifier, cancellation.Token)?.Target;
                        var api = Classify(symbol);
                        if (api == InputBackendMode.Unknown || api == mode) continue;
                        if (symbol is INamedTypeSymbol && HasMoreSpecificMember(name, model, api, cancellation.Token)) continue;
                        var key = name.SpanStart + ":" + name.Span.Length + ":" + symbol.ToDisplayString();
                        if (!seen.Add(key)) continue;
                        report.totalHints++;
                        if (report.hints.Count >= maxHints) { report.truncated = true; continue; }
                        var position = tree.GetLineSpan(name.Span, cancellation.Token).StartLinePosition;
                        var signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                        report.hints.Add(new InputApiHint
                        {
                            symbol = symbol.Name,
                            signature = signature,
                            apiBackend = api.ToString(),
                            assemblyName = symbol.ContainingAssembly?.Identity.Name,
                            severity = "warning",
                            line = position.Line + 1,
                            column = position.Character + 1,
                            length = name.Span.Length,
                            message = signature + " uses the " + api + " Input API while Active Input Handling is " + mode + ".",
                            guidance = mode == InputBackendMode.New
                                ? "Use the enabled Input System API, or explicitly enable the legacy backend/Both in PlayerSettings if this script requires it."
                                : "Use the enabled legacy Input API, or explicitly enable the new backend/Both in PlayerSettings if this script requires it."
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    report.status = "partial";
                    report.truncated = true;
                    report.limitations.Add("The semantic time budget was reached; additional usages may exist.");
                }
                catch (Exception exception)
                {
                    report.status = report.totalHints > 0 ? "partial" : "unavailable";
                    report.limitations.Add("Input API analysis could not complete: " + exception.GetType().Name + ": " + exception.Message);
                }
            }
            return report;
        }

        static InputBackendMode Classify(ISymbol symbol)
        {
            var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
            if (type == null || !type.Locations.Any(location => location.IsInMetadata)) return InputBackendMode.Unknown;
            var assembly = type.ContainingAssembly?.Identity.Name;
            var ns = type.ContainingNamespace?.ToDisplayString();
            if (type.Name == "Input" && ns == "UnityEngine" &&
                (assembly == "UnityEngine.InputLegacyModule" || assembly == "UnityEngine")) return InputBackendMode.Old;
            if (assembly == "Unity.InputSystem" && (ns == "UnityEngine.InputSystem" ||
                (ns != null && ns.StartsWith("UnityEngine.InputSystem.", StringComparison.Ordinal)))) return InputBackendMode.New;
            return InputBackendMode.Unknown;
        }

        static bool HasMoreSpecificMember(SimpleNameSyntax name, SemanticModel model, InputBackendMode api, CancellationToken cancellation)
        {
            SyntaxNode expression = name;
            while (expression.Parent is MemberAccessExpressionSyntax qualification && qualification.Name == expression)
                expression = qualification;
            if (expression.Parent is MemberAccessExpressionSyntax member && member.Expression == expression)
                return Classify(model.GetSymbolInfo(member.Name, cancellation).Symbol) == api;
            return false;
        }
    }
}
