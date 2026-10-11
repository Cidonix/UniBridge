using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal static class InputApiHints
    {
        internal static InputApiReport AnalyzeSource(string source, string scriptPath = null, int maxHints = 100)
        {
            var backend = InputBackendResolver.Resolve();
            var report = new InputApiReport { inputBackend = backend.Mode.ToString(), backendSource = backend.Source };
            if (backend.Mode == InputBackendMode.Unknown)
                return Unavailable(report, "Active Input Handling could not be determined. " + backend.Detail);
            source = source ?? string.Empty;
            if (source.Length > 1000000) return Unavailable(report, "The target source exceeds the semantic analysis limit of 1000000 characters.");
            try
            {
                var context = UnityCompilationReferenceContext.Collect(scriptPath);
                var trees = context.GetSourceTrees(source, scriptPath);
                if (context.References.References.Count == 0)
                    return Unavailable(report, "No usable metadata references were available for semantic Input API analysis.");
                var compilation = CSharpCompilation.Create(context.Owner?.name ?? "UniBridge_InputApi_InMemory",
                    trees, context.References.References, context.CompilationOptions);
                report = InputApiAnalyzer.Analyze(compilation, trees[0], backend.Mode, maxHints);
                report.backendSource = backend.Source;
                report.assemblyName = context.Owner?.name;
                report.sourceFileCount = trees.Count;
                report.referenceCount = context.References.References.Count;
                report.unityVersion = Application.unityVersion;
                foreach (var limitation in context.Limitations) AddLimitation(report, limitation);
                foreach (var issue in context.References.Issues)
                    AddLimitation(report, issue.Category + ": " + issue.Path + " (" + issue.Detail + ").");
                if (context.Partial && report.status == "available") report.status = "partial";
                return report;
            }
            catch (Exception exception)
            {
                return Unavailable(report, "Semantic Input API context could not be built: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        internal static InputApiReport Skipped(string reason)
        {
            var backend = InputBackendResolver.Resolve();
            return new InputApiReport { status = "skipped", inputBackend = backend.Mode.ToString(),
                backendSource = backend.Source, limitations = new List<string> { reason } };
        }

        static InputApiReport Unavailable(InputApiReport report, string reason)
        {
            report.status = "unavailable";
            AddLimitation(report, reason);
            return report;
        }

        static void AddLimitation(InputApiReport report, string reason)
        {
            if (report.limitations.Count < 20 && !report.limitations.Contains(reason)) report.limitations.Add(reason);
        }
    }
}
