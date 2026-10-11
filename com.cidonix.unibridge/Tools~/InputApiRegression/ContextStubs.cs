using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
namespace UnityEngine { public static class Application { public static string unityVersion = "portable context fixture"; } }
namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal static class UnityCompilationReferenceContext
    {
        internal static int Collected;
        internal static bool Partial;
        internal static List<MetadataReference> References;
        internal static CompilationReferenceContext Collect(string path = null) { Collected++; return new CompilationReferenceContext(); }
    }
    internal sealed class CompilationReferenceContext
    {
        internal readonly FixtureAssembly Owner = new FixtureAssembly();
        internal readonly FixtureReferences References = new FixtureReferences();
        internal bool Partial => UnityCompilationReferenceContext.Partial;
        internal readonly List<string> Limitations = new List<string>();
        internal readonly CSharpCompilationOptions CompilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        internal List<SyntaxTree> GetSourceTrees(string source, string path) { return new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, path: path ?? "<memory>") }; }
    }
    internal sealed class FixtureAssembly { internal string name = "fixture-owned-context"; }
    internal sealed class FixtureReferences
    {
        internal List<MetadataReference> References => UnityCompilationReferenceContext.References;
        internal readonly List<FixtureReferenceIssue> Issues = new List<FixtureReferenceIssue>();
    }
    internal sealed class FixtureReferenceIssue { internal string Category, Path, Detail; }
}
namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class ManageScript
    {
        enum ValidationLevel { Basic, Standard, Strict, Comprehensive }
        static ValidationLevel ParseValidationLevel(string level) { return level == "basic" ? ValidationLevel.Basic : ValidationLevel.Standard; }
    }
}
