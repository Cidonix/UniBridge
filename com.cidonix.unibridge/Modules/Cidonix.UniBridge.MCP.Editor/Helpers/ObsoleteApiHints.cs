using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEditor.Compilation;
using UnityEngine;
using UnityAssembly = UnityEditor.Compilation.Assembly;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Builds a read-only Roslyn context from the assembly that owns a Unity script.
    /// Neither compilation emit nor user code execution is needed for these hints.
    /// </summary>
    internal static class ObsoleteApiHints
    {

        const int MaxSourceChars = 1000000;

        const int MaxLimitations = 20;
        static readonly StringComparer PathComparer = Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;


        internal static ObsoleteApiReport AnalyzeSource(string source, string scriptPath = null, int maxHints = 100)
        {
            var limitations = new List<string>();
            source = source ?? string.Empty;
            if (source.Length > MaxSourceChars)
                return Unavailable("The target source exceeds the semantic analysis limit of 1000000 characters.");
            try
            {
                var context = UnityCompilationReferenceContext.Collect(scriptPath);
                var trees = context.GetSourceTrees(source, scriptPath);
                foreach (var limitation in context.Limitations) AddLimitation(limitations, limitation);
                foreach (var issue in context.References.Issues)
                    AddLimitation(limitations, issue.Category + ": " + issue.Path + " (" + issue.Detail + ").");
                var references = context.References.References;
                if (references.Count == 0)
                    return Unavailable("No usable metadata references were available for semantic analysis.", limitations);
                var compilation = CSharpCompilation.Create(context.Owner?.name ?? "UniBridge_ObsoleteApi_InMemory",
                    trees, references, context.CompilationOptions);
                var report = ObsoleteApiAnalyzer.Analyze(compilation, trees[0], maxHints);
                report.assemblyName = context.Owner?.name;
                report.sourceFileCount = trees.Count;
                report.referenceCount = references.Count;
                report.unityVersion = Application.unityVersion;
                foreach (var limitation in limitations) AddLimitation(report.limitations, limitation);
                if (context.Partial && string.Equals(report.status, "available", StringComparison.Ordinal)) report.status = "partial";
                return report;
            }
            catch (Exception ex)
            {
                return Unavailable("Semantic context could not be built: " + ex.GetType().Name + ": " + ex.Message, limitations);
            }
        }


        static string ResolveFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                if (Path.IsPathRooted(path))
                    return Path.GetFullPath(path);
                if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("unity://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    var absolute = ProjectPathResolver.Resolve(path, assumeAssetRelative: true).AbsolutePath;
                    return string.IsNullOrWhiteSpace(absolute) ? null : Path.GetFullPath(absolute);
                }
                return Path.GetFullPath(Path.Combine(ProjectPathResolver.ProjectRoot, path));
            }
            catch
            {
                return null;
            }
        }

        static ObsoleteApiReport Unavailable(string reason, List<string> limitations = null)
        {
            var report = new ObsoleteApiReport { status = "unavailable" };
            foreach (var limitation in limitations ?? Enumerable.Empty<string>())
                AddLimitation(report.limitations, limitation);
            AddLimitation(report.limitations, reason);
            return report;
        }

        static void AddLimitation(List<string> limitations, string message)
        {
            if (limitations.Count < MaxLimitations && !limitations.Contains(message))
                limitations.Add(message);
        }

    }
}
