using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Settings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class ManageScript
    {
        internal sealed class ScriptValidationReport
        {
            public bool Ok;
            public ScriptDiagnostic[] Diagnostics = Array.Empty<ScriptDiagnostic>();
            public ObsoleteApiReport ObsoleteApiHints;
        }

        internal sealed class ScriptDiagnostic
        {
            public int line;
            public int col;
            public string severity;
            public string message;
        }

        internal static ScriptValidationReport ValidateScriptSource(string fileText, string level, string scriptPath = null)
        {
            var chosen = ParseValidationLevel(level);
            var ok = ValidateScriptSyntax(fileText ?? string.Empty, chosen, out string[] diagsRaw, scriptPath);
            return new ScriptValidationReport
            {
                Ok = ok,
                Diagnostics = ParseDiagnostics(diagsRaw),
                ObsoleteApiHints = AnalyzeObsoleteApiHints(fileText, chosen, scriptPath)
            };
        }

        internal static ObsoleteApiReport AnalyzeObsoleteApiHints(string source, string level, string scriptPath = null)
        {
            return AnalyzeObsoleteApiHints(source, ParseValidationLevel(level), scriptPath);
        }

        static ObsoleteApiReport AnalyzeObsoleteApiHints(string source, ValidationLevel level, string scriptPath)
        {
            if (level == ValidationLevel.Basic)
            {
                return new ObsoleteApiReport
                {
                    status = "skipped",
                    limitations = new List<string>
                    {
                        "Basic validation does not build a semantic context; use standard for deprecated API hints."
                    }
                };
            }

            return Cidonix.UniBridge.MCP.Editor.Helpers.ObsoleteApiHints.AnalyzeSource(source ?? string.Empty, scriptPath);
        }

        internal static object GetObsoleteApiHintsOutputSchema()
        {
            return new
            {
                type = "object",
                description = "Advisory deprecated API usages from the submitted source and the running Editor's assembly context. Never changes validation success, warning/error counts, or source. Empty hints only imply no resolved usages when status is available; partial/unavailable/skipped are coverage limitations.",
                properties = new
                {
                    status = new { type = "string", @enum = new[] { "available", "partial", "unavailable", "skipped" } },
                    hints = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                symbol = new { type = "string" },
                                signature = new { type = "string" },
                                kind = new { type = "string" },
                                severity = new { type = "string", description = "Severity declared by System.ObsoleteAttribute, independent of validation diagnostics." },
                                message = new { type = new[] { "string", "null" } },
                                diagnosticId = new { type = "string" },
                                binding = new { type = "string" },
                                line = new { type = "integer", description = "One-based line in the submitted or final proposed source." },
                                column = new { type = "integer", description = "One-based column in that source." },
                                length = new { type = "integer" },
                                oldReturnType = new { type = new[] { "string", "null" } },
                                replacement = new
                                {
                                    type = "object",
                                    description = "Replacement evidence from metadata; unresolved or ambiguous suggestions are not treated as a verified replacement. A changed return type may require changing the receiving variable or conversion.",
                                    properties = new
                                    {
                                        status = new { type = "string" },
                                        symbol = new { type = new[] { "string", "null" } },
                                        signature = new { type = new[] { "string", "null" } },
                                        newReturnType = new { type = new[] { "string", "null" } },
                                        returnTypeChanged = new { type = "boolean" },
                                        guidance = new { type = new[] { "string", "null" } },
                                        candidates = new { type = "array", items = new { type = "string" } }
                                    }
                                }
                            }
                        }
                    },
                    totalHints = new { type = "integer", description = "Usages found within the analysis budget before the result limit." },
                    truncated = new { type = "boolean" },
                    bindingErrors = new { type = "integer", description = "Other syntax or binding errors that can limit semantic coverage; these are not added to validation counts." },
                    limitations = new { type = "array", items = new { type = "string" } },
                    assemblyName = new { type = new[] { "string", "null" } },
                    unityVersion = new { type = new[] { "string", "null" } },
                    sourceFileCount = new { type = "integer" },
                    referenceCount = new { type = "integer" }
                },
                required = new[] { "status", "hints", "totalHints", "truncated", "bindingErrors", "limitations" }
            };
        }

        static ValidationLevel ParseValidationLevel(string level)
        {
            return (level ?? "standard").ToLowerInvariant() switch
            {
                "basic" => ValidationLevel.Basic,
                "standard" => ValidationLevel.Standard,
                "strict" => ValidationLevel.Strict,
                "comprehensive" => ValidationLevel.Comprehensive,
                _ => ValidationLevel.Standard
            };
        }

        static ScriptDiagnostic[] ParseDiagnostics(string[] rawDiagnostics)
        {
            return (rawDiagnostics ?? Array.Empty<string>()).Select(s =>
            {
                var m = Regex.Match(
                    s,
                    @"^(ERROR|WARNING|INFO): (.*?)(?: \(Line (\d+)(?:, Column (\d+))?\))?$",
                    RegexOptions.CultureInvariant | RegexOptions.Multiline,
                    TimeSpan.FromMilliseconds(250)
                );

                return new ScriptDiagnostic
                {
                    line = m.Success && int.TryParse(m.Groups[3].Value, out var l) ? l : 0,
                    col = m.Success && int.TryParse(m.Groups[4].Value, out var c) ? c : 0,
                    severity = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "info",
                    message = m.Success ? m.Groups[2].Value : s
                };
            }).ToArray();
        }

        /// <summary>
        /// Gets the validation level from the GUI settings.
        /// </summary>
        static ValidationLevel GetValidationLevelFromGUI()
        {
            try
            {
                var settings = MCPSettingsManager.Settings;
                string savedLevel = settings.validationLevel ?? "standard";
                return savedLevel.ToLower() switch
                {
                    "basic" => ValidationLevel.Basic,
                    "standard" => ValidationLevel.Standard,
                    "comprehensive" => ValidationLevel.Comprehensive,
                    "strict" => ValidationLevel.Strict,
                    _ => ValidationLevel.Standard
                };
            }
            catch
            {
                return ValidationLevel.Standard;
            }
        }

        /// <summary>
        /// Validates C# script syntax using multiple validation layers.
        /// </summary>
        static bool ValidateScriptSyntax(string contents)
        {
            return ValidateScriptSyntax(contents, ValidationLevel.Standard, out _);
        }

        /// <summary>
        /// Advanced syntax validation with detailed diagnostics and configurable strictness.
        /// </summary>
        static bool ValidateScriptSyntax(string contents, ValidationLevel level, out string[] errors,string scriptPath = null)
        {
            var errorList = new List<string>();
            errors = null;

            if (string.IsNullOrEmpty(contents))
            {
                return true;
            }

            if (!ValidateBasicStructure(contents, errorList))
            {
                errors = errorList.ToArray();
                return false;
            }

#if USE_ROSLYN
            if (level >= ValidationLevel.Standard)
            {
                if (!ValidateScriptSyntaxRoslyn(contents, level, errorList))
                {
                    errors = errorList.ToArray();
                    return false;
                }
            }
#endif

            if (level >= ValidationLevel.Standard)
            {
                ValidateScriptSyntaxUnity(contents, errorList);
            }

            if (level >= ValidationLevel.Comprehensive)
            {
                ValidateSemanticRules(contents, errorList);
            }

#if USE_ROSLYN
            if (level == ValidationLevel.Strict)
            {
                if (!ValidateScriptSemantics(contents, errorList,scriptPath))
                {
                    errors = errorList.ToArray();
                    return false;
                }
            }
#endif

            errors = errorList.ToArray();
            return errorList.Count == 0 || (level != ValidationLevel.Strict && !errorList.Any(e => e.StartsWith("ERROR:")));
        }

        enum ValidationLevel
        {
            Basic,
            Standard,
            Comprehensive,
            Strict
        }

        static bool ValidateBasicStructure(string contents, List<string> errors)
        {
            bool isValid = true;
            int braceBalance = 0;
            int parenBalance = 0;
            int bracketBalance = 0;
            bool inStringLiteral = false;
            bool inCharLiteral = false;
            bool inSingleLineComment = false;
            bool inMultiLineComment = false;
            bool escaped = false;

            for (int i = 0; i < contents.Length; i++)
            {
                char c = contents[i];
                char next = i + 1 < contents.Length ? contents[i + 1] : '\0';

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && (inStringLiteral || inCharLiteral))
                {
                    escaped = true;
                    continue;
                }

                if (!inStringLiteral && !inCharLiteral)
                {
                    if (c == '/' && next == '/' && !inMultiLineComment)
                    {
                        inSingleLineComment = true;
                        continue;
                    }
                    if (c == '/' && next == '*' && !inSingleLineComment)
                    {
                        inMultiLineComment = true;
                        i++;
                        continue;
                    }
                    if (c == '*' && next == '/' && inMultiLineComment)
                    {
                        inMultiLineComment = false;
                        i++;
                        continue;
                    }
                }

                if (c == '\n')
                {
                    inSingleLineComment = false;
                    continue;
                }

                if (inSingleLineComment || inMultiLineComment)
                    continue;

                if (c == '"' && !inCharLiteral)
                {
                    inStringLiteral = !inStringLiteral;
                    continue;
                }
                if (c == '\'' && !inStringLiteral)
                {
                    inCharLiteral = !inCharLiteral;
                    continue;
                }

                if (inStringLiteral || inCharLiteral)
                    continue;

                switch (c)
                {
                    case '{': braceBalance++; break;
                    case '}': braceBalance--; break;
                    case '(': parenBalance++; break;
                    case ')': parenBalance--; break;
                    case '[': bracketBalance++; break;
                    case ']': bracketBalance--; break;
                }

                if (braceBalance < 0)
                {
                    errors.Add("ERROR: Unmatched closing brace '}'");
                    isValid = false;
                }
                if (parenBalance < 0)
                {
                    errors.Add("ERROR: Unmatched closing parenthesis ')'");
                    isValid = false;
                }
                if (bracketBalance < 0)
                {
                    errors.Add("ERROR: Unmatched closing bracket ']'");
                    isValid = false;
                }
            }

            if (braceBalance != 0)
            {
                errors.Add($"ERROR: Unbalanced braces (difference: {braceBalance})");
                isValid = false;
            }
            if (parenBalance != 0)
            {
                errors.Add($"ERROR: Unbalanced parentheses (difference: {parenBalance})");
                isValid = false;
            }
            if (bracketBalance != 0)
            {
                errors.Add($"ERROR: Unbalanced brackets (difference: {bracketBalance})");
                isValid = false;
            }
            if (inStringLiteral)
            {
                errors.Add("ERROR: Unterminated string literal");
                isValid = false;
            }
            if (inCharLiteral)
            {
                errors.Add("ERROR: Unterminated character literal");
                isValid = false;
            }
            if (inMultiLineComment)
            {
                errors.Add("WARNING: Unterminated multi-line comment");
            }

            return isValid;
        }

#if USE_ROSLYN

        static bool ValidateScriptSyntaxRoslyn(string contents, ValidationLevel level, List<string> errors)
        {
            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(contents);
                var diagnostics = syntaxTree.GetDiagnostics();

                bool hasErrors = false;
                foreach (var diagnostic in diagnostics)
                {
                    string severity = diagnostic.Severity.ToString().ToUpper();
                    string message = $"{severity}: {diagnostic.GetMessage()}";

                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        hasErrors = true;
                    }

                    if (level >= ValidationLevel.Standard || diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        var location = diagnostic.Location.GetLineSpan();
                        if (location.IsValid)
                        {
                            message += $" (Line {location.StartLinePosition.Line + 1})";
                        }
                        errors.Add(message);
                    }
                }

                return !hasErrors;
            }
            catch (Exception ex)
            {
                errors.Add($"ERROR: Roslyn validation failed: {ex.Message}");
                return false;
            }
        }

        static bool ValidateScriptSemantics(string contents, List<string> errors,string scriptPath = null)
        {
            try
            {
                var context = UnityCompilationReferenceContext.Collect(scriptPath);
                var trees = context.GetSourceTrees(contents, scriptPath);
                foreach (var limitation in context.Limitations.Take(20)) errors.Add("WARNING: " + limitation);
                foreach (var issue in context.References.Issues.Take(20)) errors.Add("WARNING: " + issue.Category + ": " + issue.Path + " (" + issue.Detail + ").");
                if (context.Partial || context.References.References.Count == 0)
                {
                    errors.Add("ERROR: Complete owning-assembly references and source context are unavailable for strict semantic validation.");
                    return false;
                }
                var compilation = CSharpCompilation.Create(context.Owner?.name ?? "TempValidation", trees,
                    context.References.References, context.CompilationOptions);
                bool hasErrors = false;
                foreach (var diagnostic in compilation.GetDiagnostics())
                {
                    if (diagnostic.Severity != DiagnosticSeverity.Error && diagnostic.Severity != DiagnosticSeverity.Warning) continue;
                    var location = diagnostic.Location.GetLineSpan();
                    string locationInfo = location.IsValid ? $" (Line {location.StartLinePosition.Line + 1}, Column {location.StartLinePosition.Character + 1})" : "";
                    string diagnosticId = string.IsNullOrEmpty(diagnostic.Id) ? "" : $" [{diagnostic.Id}]";
                    var severity = diagnostic.Severity == DiagnosticSeverity.Error ? "ERROR" : "WARNING";
                    errors.Add($"{severity}: {diagnostic.GetMessage()}{diagnosticId}{locationInfo}");
                    hasErrors |= diagnostic.Severity == DiagnosticSeverity.Error;
                }
                return !hasErrors;
            }
            catch (Exception ex)
            {
                errors.Add($"ERROR: Semantic validation failed: {ex.Message}");
                return false;
            }
        }

#if UNITY_EDITOR
#endif
#else
        static bool ValidateScriptSyntaxRoslyn(string contents, ValidationLevel level, List<string> errors)
        {
            return true;
        }
#endif

        /// <summary>
        /// Validates Unity-specific coding rules and best practices.
        /// </summary>
        static void ValidateScriptSyntaxUnity(string contents, List<string> errors)
        {
            if (ContainsInvocationInsideParameterlessMethod(contents, "Update", IsFindObjectOfTypeInvocation))
            {
                errors.Add("WARNING: FindObjectOfType in Update() can cause performance issues");
            }

            if (ContainsInvocationInsideParameterlessMethod(contents, "Update", IsGameObjectFindInvocation))
            {
                errors.Add("WARNING: GameObject.Find in Update() can cause performance issues");
            }

            if (contents.Contains(": MonoBehaviour") && !contents.Contains("using UnityEngine"))
            {
                errors.Add("WARNING: MonoBehaviour requires 'using UnityEngine;'");
            }

            if (contents.Contains("[SerializeField]") && !contents.Contains("using UnityEngine"))
            {
                errors.Add("WARNING: SerializeField requires 'using UnityEngine;'");
            }

            if (contents.Contains("StartCoroutine") && !contents.Contains("IEnumerator"))
            {
                errors.Add("WARNING: StartCoroutine typically requires IEnumerator methods");
            }

            errors.AddRange(FindUpdateRigidbodyWarnings(contents));

            if (contents.Contains("GetComponent<") && !contents.Contains("!= null"))
            {
                errors.Add("WARNING: Consider null checking GetComponent results");
            }

            if (contents.Contains("void Start(") && !contents.Contains("void Start()"))
            {
                errors.Add("WARNING: Start() should not have parameters");
            }

            if (contents.Contains("void Update(") && !contents.Contains("void Update()"))
            {
                errors.Add("WARNING: Update() should not have parameters");
            }

            errors.AddRange(FindUpdateStringAllocationWarnings(contents));
        }

        static bool ContainsInvocationInsideParameterlessMethod(
            string contents,
            string methodName,
            Func<InvocationExpressionSyntax, bool> predicate)
        {
            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(contents).GetRoot();
            }
            catch
            {
                return false;
            }

            return root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method =>
                    string.Equals(method.Identifier.ValueText, methodName, StringComparison.Ordinal) &&
                    method.ParameterList.Parameters.Count == 0 &&
                    method.Body != null)
                .SelectMany(method => method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                .Any(predicate);
        }

        static bool IsFindObjectOfTypeInvocation(InvocationExpressionSyntax invocation)
        {
            return invocation?.Expression switch
            {
                IdentifierNameSyntax identifier => string.Equals(identifier.Identifier.ValueText, "FindObjectOfType", StringComparison.Ordinal),
                GenericNameSyntax generic => string.Equals(generic.Identifier.ValueText, "FindObjectOfType", StringComparison.Ordinal),
                MemberAccessExpressionSyntax member => string.Equals(member.Name.Identifier.ValueText, "FindObjectOfType", StringComparison.Ordinal),
                _ => false
            };
        }

        static bool IsGameObjectFindInvocation(InvocationExpressionSyntax invocation)
        {
            if (invocation?.Expression is not MemberAccessExpressionSyntax member ||
                !string.Equals(member.Name.Identifier.ValueText, "Find", StringComparison.Ordinal))
            {
                return false;
            }

            string receiver = member.Expression.ToString();
            return string.Equals(receiver, "GameObject", StringComparison.Ordinal) ||
                   string.Equals(receiver, "UnityEngine.GameObject", StringComparison.Ordinal);
        }

        static IEnumerable<string> FindUpdateRigidbodyWarnings(string contents)
        {
            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(contents).GetRoot();
            }
            catch
            {
                return Array.Empty<string>();
            }

            bool hasFixedUpdate = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Any(method =>
                    string.Equals(method.Identifier.ValueText, "FixedUpdate", StringComparison.Ordinal) &&
                    method.ParameterList.Parameters.Count == 0);
            if (hasFixedUpdate)
            {
                return Array.Empty<string>();
            }

            var warnings = new List<string>();
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!string.Equals(method.Identifier.ValueText, "Update", StringComparison.Ordinal) ||
                    method.ParameterList.Parameters.Count != 0 ||
                    method.Body == null)
                {
                    continue;
                }

                var rigidbodyNode = method.Body.DescendantNodes().FirstOrDefault(IsRigidbodyRelatedNode);
                if (rigidbodyNode != null)
                {
                    warnings.Add(FormatUpdateAllocationWarning(
                        "Consider using FixedUpdate() for Rigidbody operations",
                        rigidbodyNode));
                }
            }

            return warnings;
        }

        static bool IsRigidbodyRelatedNode(SyntaxNode node)
        {
            return node switch
            {
                IdentifierNameSyntax identifier => ContainsRigidbodyName(identifier.Identifier.ValueText),
                GenericNameSyntax generic => generic.TypeArgumentList.Arguments.Any(arg => ContainsRigidbodyName(arg.ToString())),
                MemberAccessExpressionSyntax member => ContainsRigidbodyName(member.Name.Identifier.ValueText) ||
                                                       ContainsRigidbodyName(member.Expression?.ToString()),
                ObjectCreationExpressionSyntax creation => ContainsRigidbodyName(creation.Type?.ToString()),
                VariableDeclarationSyntax declaration => ContainsRigidbodyName(declaration.Type?.ToString()),
                CastExpressionSyntax cast => ContainsRigidbodyName(cast.Type?.ToString()),
                _ => false
            };
        }

        static bool ContainsRigidbodyName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf("Rigidbody", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static IEnumerable<string> FindUpdateStringAllocationWarnings(string contents)
        {
            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(contents).GetRoot();
            }
            catch
            {
                return Array.Empty<string>();
            }

            var warnings = new List<string>();
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!string.Equals(method.Identifier.ValueText, "Update", StringComparison.Ordinal) ||
                    method.ParameterList.Parameters.Count != 0 ||
                    method.Body == null)
                {
                    continue;
                }

                foreach (var node in method.Body.DescendantNodes())
                {
                    if (node is InterpolatedStringExpressionSyntax interpolated)
                    {
                        warnings.Add(FormatUpdateAllocationWarning(
                            "String interpolation in Update() can cause garbage collection issues",
                            interpolated));
                        continue;
                    }

                    if (node is BinaryExpressionSyntax binary &&
                        binary.IsKind(SyntaxKind.AddExpression) &&
                        IsObviousStringExpression(binary.Left, binary.Right))
                    {
                        warnings.Add(FormatUpdateAllocationWarning(
                            "String concatenation in Update() can cause garbage collection issues",
                            binary));
                        continue;
                    }

                    if (node is InvocationExpressionSyntax invocation)
                    {
                        if (IsStringConcatInvocation(invocation))
                        {
                            warnings.Add(FormatUpdateAllocationWarning(
                                "string.Concat in Update() can cause garbage collection issues",
                                invocation));
                        }
                        else if (IsToStringInvocation(invocation))
                        {
                            warnings.Add(FormatUpdateAllocationWarning(
                                "ToString() in Update() can allocate garbage if called every frame",
                                invocation));
                        }
                    }
                }
            }

            return warnings;
        }

        static bool IsObviousStringExpression(ExpressionSyntax left, ExpressionSyntax right)
        {
            return ContainsStringLiteral(left) ||
                   ContainsStringLiteral(right) ||
                   left is InterpolatedStringExpressionSyntax ||
                   right is InterpolatedStringExpressionSyntax ||
                   IsStringConcatInvocation(left as InvocationExpressionSyntax) ||
                   IsStringConcatInvocation(right as InvocationExpressionSyntax) ||
                   IsToStringInvocation(left as InvocationExpressionSyntax) ||
                   IsToStringInvocation(right as InvocationExpressionSyntax);
        }

        static bool ContainsStringLiteral(SyntaxNode node)
        {
            if (node == null)
            {
                return false;
            }

            return node.DescendantNodesAndSelf()
                .OfType<LiteralExpressionSyntax>()
                .Any(literal => literal.IsKind(SyntaxKind.StringLiteralExpression));
        }

        static bool IsStringConcatInvocation(InvocationExpressionSyntax invocation)
        {
            if (invocation?.Expression is not MemberAccessExpressionSyntax member ||
                !string.Equals(member.Name.Identifier.ValueText, "Concat", StringComparison.Ordinal))
            {
                return false;
            }

            string receiver = member.Expression.ToString();
            return string.Equals(receiver, "string", StringComparison.Ordinal) ||
                   string.Equals(receiver, "String", StringComparison.Ordinal) ||
                   string.Equals(receiver, "System.String", StringComparison.Ordinal);
        }

        static bool IsToStringInvocation(InvocationExpressionSyntax invocation)
        {
            return invocation?.Expression is MemberAccessExpressionSyntax member &&
                   string.Equals(member.Name.Identifier.ValueText, "ToString", StringComparison.Ordinal);
        }

        static string FormatUpdateAllocationWarning(string message, SyntaxNode node)
        {
            var lineSpan = node.GetLocation().GetLineSpan();
            string expression = TruncateDiagnosticExpression(node.ToString());
            if (!lineSpan.IsValid)
            {
                return $"WARNING: {message}. Expression: {expression}";
            }

            int line = lineSpan.StartLinePosition.Line + 1;
            int column = lineSpan.StartLinePosition.Character + 1;
            return $"WARNING: {message}. Expression: {expression} (Line {line}, Column {column})";
        }

        static string TruncateDiagnosticExpression(string expression)
        {
            expression = (expression ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            return expression.Length <= 120 ? expression : expression.Substring(0, 117) + "...";
        }

        static void ValidateSemanticRules(string contents, List<string> errors)
        {
            if (contents.Contains("new ") && contents.Contains("Update()"))
            {
                errors.Add("WARNING: Creating objects in Update() may cause memory issues");
            }

            var magicNumberPattern = new Regex(@"\b\d+\.?\d*f?\b(?!\s*[;})\]])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            var matches = magicNumberPattern.Matches(contents);
            if (matches.Count > 5)
            {
                errors.Add("WARNING: Consider using named constants instead of magic numbers");
            }

            var methodPattern = new Regex(@"(public|private|protected|internal)?\s*(static)?\s*\w+\s+\w+\s*\([^)]*\)\s*{", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            var methodMatches = methodPattern.Matches(contents);
            foreach (Match match in methodMatches)
            {
                int startIndex = match.Index;
                int braceCount = 0;
                int lineCount = 0;
                bool inMethod = false;

                for (int i = startIndex; i < contents.Length; i++)
                {
                    if (contents[i] == '{')
                    {
                        braceCount++;
                        inMethod = true;
                    }
                    else if (contents[i] == '}')
                    {
                        braceCount--;
                        if (braceCount == 0 && inMethod)
                            break;
                    }
                    else if (contents[i] == '\n' && inMethod)
                    {
                        lineCount++;
                    }
                }

                if (lineCount > 50)
                {
                    errors.Add("WARNING: Method is very long, consider breaking it into smaller methods");
                    break;
                }
            }

            if (contents.Contains("catch") && contents.Contains("catch()"))
            {
                errors.Add("WARNING: Empty catch blocks should be avoided");
            }

            if (contents.Contains("async ") && !contents.Contains("await"))
            {
                errors.Add("WARNING: Async method should contain await or return Task");
            }

            if (contents.Contains("\"Player\"") || contents.Contains("\"Enemy\""))
            {
                errors.Add("WARNING: Consider using constants for tags instead of hardcoded strings");
            }
        }
    }
}
