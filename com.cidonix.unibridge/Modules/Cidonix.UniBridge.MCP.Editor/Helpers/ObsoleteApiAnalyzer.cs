using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Advisory only: never rewrites source, emits assemblies, or runs user code.
    internal sealed class ObsoleteApiReport
    {
        public string status = "available";
        public List<ObsoleteApiHint> hints = new List<ObsoleteApiHint>();
        public int totalHints;
        public bool truncated;
        public int bindingErrors;
        public List<string> limitations = new List<string>();
        public string assemblyName;
        public string unityVersion;
        public int sourceFileCount;
        public int referenceCount;
    }

    internal sealed class ObsoleteApiHint
    {
        public string symbol, signature, kind, severity, message, diagnosticId, binding;
        public int line, column, length;
        public string oldReturnType;
        public ObsoleteApiReplacement replacement;
        internal int offset;
    }

    internal sealed class ObsoleteApiReplacement
    {
        public string status = "notSpecified";
        public string symbol, signature, newReturnType, guidance;
        public bool returnTypeChanged;
        public List<string> candidates = new List<string>();
    }

    internal static class ObsoleteApiAnalyzer
    {
        private static readonly Regex AdvertisedReplacement = new Regex(
            @"(?:UnityUpgradable\s*\)?\s*->\s*|\buse\s+)[`'""\s]*(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*(?:<[^>\r\n]{1,120}>)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

        internal static ObsoleteApiReport Analyze(CSharpCompilation compilation, SyntaxTree tree, int maxHints = 100)
        {
            var report = new ObsoleteApiReport();
            maxHints = Math.Max(1, Math.Min(200, maxHints));
            if (compilation == null || tree == null || !compilation.SyntaxTrees.Contains(tree))
            {
                report.status = "unavailable";
                report.limitations.Add("A semantic model for the submitted source is unavailable.");
                return report;
            }
            try
            {
                var model = compilation.GetSemanticModel(tree);
                var obsoleteType = compilation.GetTypeByMetadataName("System.ObsoleteAttribute");
                if (obsoleteType == null)
                {
                    report.status = "unavailable";
                    report.limitations.Add("System.ObsoleteAttribute could not be resolved from the compilation references.");
                    return report;
                }
                var found = new List<ObsoleteApiHint>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var watch = Stopwatch.StartNew();
                int nodeCount = 0;
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    if (++nodeCount > 25000 || (nodeCount % 128 == 0 && watch.ElapsedMilliseconds > 2500))
                    {
                        report.status = "partial";
                        report.limitations.Add("The semantic traversal budget was reached; additional usages may exist.");
                        break;
                    }
                    if (node is SimpleNameSyntax name)
                    {
                        var symbol = model.GetSymbolInfo(name).Symbol;
                        if (symbol is IAliasSymbol alias) symbol = alias.Target;
                        if (symbol == null && name is IdentifierNameSyntax identifier)
                            symbol = model.GetAliasInfo(identifier)?.Target;
                        Add(symbol, name, compilation, obsoleteType, found, seen);
                        if (symbol is IPropertySymbol property)
                        {
                            var access = AccessExpression(name);
                            bool write = IsWrite(access, out bool alsoRead);
                            if (!write || alsoRead) Add(property.GetMethod, name, compilation, obsoleteType, found, seen);
                            if (write) Add(property.SetMethod, name, compilation, obsoleteType, found, seen);
                        }
                        else if (symbol is IEventSymbol ev)
                        {
                            var access = AccessExpression(name);
                            if (access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access)
                                Add(assignment.IsKind(SyntaxKind.AddAssignmentExpression) ? ev.AddMethod : ev.RemoveMethod,
                                    name, compilation, obsoleteType, found, seen);
                        }
                    }
                    else if (node is ObjectCreationExpressionSyntax || node is ImplicitObjectCreationExpressionSyntax ||
                             node is ConstructorInitializerSyntax || node is BinaryExpressionSyntax ||
                             node is PrefixUnaryExpressionSyntax || node is PostfixUnaryExpressionSyntax ||
                             node is CastExpressionSyntax || node is ElementAccessExpressionSyntax || node is ElementBindingExpressionSyntax ||
                             node is AssignmentExpressionSyntax)
                    {
                        var symbol = model.GetSymbolInfo(node).Symbol;
                        Add(symbol, node, compilation, obsoleteType, found, seen);
                        if (symbol is IPropertySymbol property)
                        {
                            bool write = IsWrite(node, out bool alsoRead);
                            if (!write || alsoRead) Add(property.GetMethod, node, compilation, obsoleteType, found, seen);
                            if (write) Add(property.SetMethod, node, compilation, obsoleteType, found, seen);
                        }
                    }
                    if (node is ExpressionSyntax expression)
                    {
                        // Includes implicit conversions at arguments, assignments and returns.
                        var conversion = model.GetConversion(expression);
                        if (conversion.IsUserDefined)
                            Add(conversion.MethodSymbol, expression, compilation, obsoleteType, found, seen);
                    }
                }
                found.Sort((a, b) => a.offset != b.offset ? a.offset.CompareTo(b.offset) : string.CompareOrdinal(a.symbol, b.symbol));
                report.totalHints = found.Count;
                report.hints = found.Take(maxHints).ToList();
                report.truncated = found.Count > maxHints;
                if (report.truncated)
                {
                    report.status = "partial";
                    report.limitations.Add("The hint limit was reached; totalHints counts the usages found within the traversal budget.");
                }
                var obsoleteIds = new HashSet<string>(found.Where(x => !string.IsNullOrEmpty(x.diagnosticId)).Select(x => x.diagnosticId), StringComparer.Ordinal)
                    { "CS0612", "CS0618", "CS0619" };
                report.bindingErrors = model.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error && !obsoleteIds.Contains(d.Id));
                if (report.bindingErrors > 0)
                {
                    report.status = "partial";
                    report.limitations.Add("The source contains binding or syntax errors; only successfully resolved usages are reported.");
                }
            }
            catch (Exception ex)
            {
                report.status = "unavailable";
                report.limitations.Add("Semantic analysis failed: " + Bound(ex.GetType().Name + ": " + ex.Message, 300));
            }
            return report;
        }

        private static SyntaxNode AccessExpression(SimpleNameSyntax name)
        {
            return name.Parent is MemberAccessExpressionSyntax member && member.Name == name ? member : (SyntaxNode)name;
        }

        private static bool IsWrite(SyntaxNode node, out bool alsoRead)
        {
            alsoRead = false;
            while (node.Parent is ParenthesizedExpressionSyntax ||
                   node.Parent is ArgumentSyntax argument && argument.Parent is TupleExpressionSyntax ||
                   node.Parent is TupleExpressionSyntax)
                node = node.Parent;
            if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
            {
                alsoRead = !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression);
                return true;
            }
            if (node.Parent is PrefixUnaryExpressionSyntax prefix &&
                (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)) ||
                node.Parent is PostfixUnaryExpressionSyntax postfix &&
                (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
            {
                alsoRead = true;
                return true;
            }
            return false;
        }

        private static ISymbol Definition(ISymbol symbol)
        {
            if (symbol is IMethodSymbol method && method.ReducedFrom != null) symbol = method.ReducedFrom;
            return symbol?.OriginalDefinition;
        }

        private static AttributeData Obsolete(ISymbol symbol, INamedTypeSymbol attributeType)
        {
            return Definition(symbol)?.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));
        }

        private static void Add(ISymbol symbol, SyntaxNode node, CSharpCompilation compilation,
            INamedTypeSymbol attributeType, List<ObsoleteApiHint> found, HashSet<string> seen)
        {
            if (symbol == null || symbol.Kind == SymbolKind.ErrorType) return;
            var attribute = Obsolete(symbol, attributeType);
            if (attribute == null) return;
            string signature = Display(symbol);
            string key = node.SpanStart + ":" + node.Span.Length + ":" + Display(Definition(symbol));
            if (!seen.Add(key)) return;
            string message = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
            bool error = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is bool flag && flag;
            string diagnosticId = attribute.NamedArguments.FirstOrDefault(a => a.Key == "DiagnosticId").Value.Value as string;
            var location = node.GetLocation().GetLineSpan().StartLinePosition;
            var returnType = ReturnType(symbol);
            found.Add(new ObsoleteApiHint
            {
                symbol = Bound(Display(Definition(symbol)), 512), signature = Bound(signature, 512),
                kind = symbol.Kind.ToString(), severity = error ? "error" : "warning", message = Bound(message, 1000),
                diagnosticId = diagnosticId ?? (error ? "CS0619" : string.IsNullOrEmpty(message) ? "CS0612" : "CS0618"),
                binding = "resolved", line = location.Line + 1, column = location.Character + 1,
                length = node.Span.Length, offset = node.SpanStart,
                oldReturnType = returnType == null ? null : Display(returnType),
                replacement = FindReplacement(symbol, message, compilation, attributeType,
                    compilation.GetSemanticModel(node.SyntaxTree).GetEnclosingSymbol(node.SpanStart))
            });
        }

        private static ObsoleteApiReplacement FindReplacement(ISymbol old, string message,
            CSharpCompilation compilation, INamedTypeSymbol attributeType, ISymbol enclosingSymbol)
        {
            var result = new ObsoleteApiReplacement();
            if (string.IsNullOrWhiteSpace(message)) return result;
            var match = AdvertisedReplacement.Match(message);
            if (!match.Success) return result;
            result.status = "unresolved";
            string advertised = match.Groups["name"].Value;
            int genericStart = advertised.IndexOf('<');
            if (genericStart >= 0) advertised = advertised.Substring(0, genericStart);
            string memberName = advertised.Substring(advertised.LastIndexOf('.') + 1);
            string qualifier = advertised.LastIndexOf('.') >= 0 ? advertised.Substring(0, advertised.LastIndexOf('.')) : null;
            var candidates = new List<ISymbol>();
            if (old is INamedTypeSymbol oldType)
            {
                var type = ResolveType(advertised, oldType, compilation);
                if (type != null) candidates.Add(type);
            }
            else
            {
                var containingType = qualifier == null ? old.ContainingType : ResolveType(qualifier, old.ContainingType, compilation);
                if (containingType != null)
                    candidates.AddRange(containingType.GetMembers(memberName).Where(s => s.Kind == old.Kind && Obsolete(s, attributeType) == null));
            }
            var oldMethod = old as IMethodSymbol;
            if (oldMethod != null)
            {
                candidates = candidates.OfType<IMethodSymbol>().Where(m => m.Arity == oldMethod.Arity).Select(m =>
                {
                    if (m.Arity > 0 && oldMethod.TypeArguments.Length == m.Arity)
                        return (ISymbol)m.Construct(oldMethod.TypeArguments.ToArray());
                    return m;
                }).ToList();
                if (candidates.Count > 1)
                {
                    var exact = candidates.OfType<IMethodSymbol>().Where(m => SameParameters(oldMethod, m)).Cast<ISymbol>().ToList();
                    if (exact.Count == 1) candidates = exact;
                }
            }
            ISymbol within = enclosingSymbol as INamedTypeSymbol ?? (ISymbol)enclosingSymbol?.ContainingType ?? compilation.Assembly;
            candidates = candidates.Where(s => Obsolete(s, attributeType) == null && compilation.IsSymbolAccessibleWithin(s, within))
                .Distinct(SymbolEqualityComparer.Default).ToList();
            if (candidates.Count != 1)
            {
                result.status = candidates.Count > 1 ? "ambiguous" : "unresolved";
                result.candidates = candidates.Take(5).Select(s => Bound(Display(s), 512)).ToList();
                result.guidance = "The obsolete message advertises " + Bound(advertised, 160) + "; its replacement could not be uniquely resolved. Review the API before changing the call.";
                return result;
            }
            var replacement = candidates[0];
            result.status = "resolved";
            result.symbol = Bound(Display(Definition(replacement)), 512);
            result.signature = Bound(Display(replacement), 512);
            var oldReturn = ReturnType(old);
            var newReturn = ReturnType(replacement);
            result.newReturnType = newReturn == null ? null : Display(newReturn);
            result.returnTypeChanged = oldReturn != null && newReturn != null && !SymbolEqualityComparer.Default.Equals(oldReturn, newReturn);
            result.guidance = "Review the replacement signature and arguments before editing.";
            if (result.returnTypeChanged)
            {
                result.guidance += " The result type changes from " + Display(oldReturn) + " to " + Display(newReturn) +
                    "; update the receiving type or use var.";
                var back = compilation.ClassifyConversion(newReturn, oldReturn);
                if (back.IsUserDefined && Obsolete(back.MethodSymbol, attributeType) != null)
                    result.guidance += " Converting the new result back to the old type uses an obsolete conversion; avoid that conversion.";
            }
            return result;
        }

        private static bool SameParameters(IMethodSymbol left, IMethodSymbol right)
        {
            if (left.Parameters.Length != right.Parameters.Length) return false;
            for (int i = 0; i < left.Parameters.Length; i++)
                if (left.Parameters[i].RefKind != right.Parameters[i].RefKind ||
                    !SymbolEqualityComparer.Default.Equals(left.Parameters[i].Type, right.Parameters[i].Type)) return false;
            return true;
        }

        private static INamedTypeSymbol ResolveType(string name, INamedTypeSymbol context, CSharpCompilation compilation)
        {
            if (context != null && (name == context.Name || name == Display(context))) return context;
            var exact = compilation.GetTypeByMetadataName(name);
            if (exact != null) return exact;
            for (var ns = context?.ContainingNamespace; ns != null; ns = ns.IsGlobalNamespace ? null : ns.ContainingNamespace)
            {
                string prefix = ns.IsGlobalNamespace ? "" : ns.ToDisplayString() + ".";
                var candidate = compilation.GetTypeByMetadataName(prefix + name);
                if (candidate != null) return candidate;
            }
            return null;
        }

        private static ITypeSymbol ReturnType(ISymbol symbol)
        {
            if (symbol is IMethodSymbol method) return method.ReturnType;
            if (symbol is IPropertySymbol property) return property.Type;
            if (symbol is IFieldSymbol field) return field.Type;
            if (symbol is IEventSymbol ev) return ev.Type;
            return null;
        }

        private static string Display(ISymbol symbol) => symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        private static string Bound(string value, int limit) => value == null ? null : value.Length <= limit ? value : value.Substring(0, limit) + "…";
    }
}
