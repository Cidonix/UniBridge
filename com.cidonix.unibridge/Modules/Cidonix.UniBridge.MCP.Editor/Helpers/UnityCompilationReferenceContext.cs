using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEditor;
using UnityEditor.Compilation;
using UnityAssembly = UnityEditor.Compilation.Assembly;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    [InitializeOnLoad]
    internal static class UnityCompilationReferenceContext
    {
        static UnityCompilationReferenceContext()
        {
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += Invalidate;
        }
        static void OnCompilationFinished(object unused) => Invalidate();
        static void Invalidate()
        {
            LoadedAssemblyCatalog.Invalidate();
            MetadataReferenceCatalog.Invalidate();
        }

        internal static CompilationReferenceContext Collect(string scriptPath = null)
        {
            var context = new CompilationReferenceContext { Generation = LoadedAssemblyCatalog.CurrentGeneration };
            var root = ProjectPathResolver.ProjectRoot;
            var targetPath = ResolvePath(scriptPath);
            UnityAssembly[] assemblies;
            try { assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor) ?? Array.Empty<UnityAssembly>(); }
            catch (Exception ex) when (LoadedAssemblyCatalog.ExpectedInspectionFailure(ex))
            {
                AddIssue(context, "Unity compilation assemblies are unavailable: " + ex.GetType().Name + ".");
                assemblies = Array.Empty<UnityAssembly>();
            }
            var comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (targetPath != null)
                context.Owner = assemblies.FirstOrDefault(assembly => (assembly.sourceFiles ?? Array.Empty<string>())
                    .Any(path => comparer.Equals(ResolvePath(path), targetPath)));
            if (context.Owner == null && !string.IsNullOrWhiteSpace(scriptPath))
            {
                try
                {
                    var resolved = ProjectPathResolver.Resolve(scriptPath, assumeAssetRelative: true);
                    var name = CompilationPipeline.GetAssemblyNameFromScriptPath(resolved.AssetPath ?? resolved.ProjectRelativePath ?? scriptPath);
                    if (!string.IsNullOrEmpty(name) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                    context.Owner = assemblies.FirstOrDefault(assembly => string.Equals(assembly.name, name, StringComparison.Ordinal));
                }
                catch (Exception ex) when (LoadedAssemblyCatalog.ExpectedInspectionFailure(ex))
                {
                    AddIssue(context, "The owning compilation assembly could not be resolved: " + ex.GetType().Name + ".");
                }
            }
            var arguments = new List<string> { "-target:library", "-out:UniBridge_ReadOnly_Context.dll" };
            var options = context.Owner?.compilerOptions;
            var language = GetOption(options, "LanguageVersion", context) as string;
            arguments.Add("-langversion:" + (string.IsNullOrWhiteSpace(language) ? "9.0" : language));
            if (options?.AllowUnsafeCode == true) arguments.Add("-unsafe+");
            var defines = context.Owner?.defines ?? Array.Empty<string>();
            if (defines.Length > 0) arguments.Add("-define:" + string.Join(";", defines));
            if (GetOption(options, "AdditionalCompilerArguments", context) is IEnumerable<string> extra)
                arguments.AddRange(extra.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()));
            if (GetOption(options, "ResponseFiles", context) is IEnumerable<string> responseFiles)
            {
                foreach (var responseFile in responseFiles)
                {
                    var path = ResolvePath(responseFile);
                    if (path != null && File.Exists(path)) arguments.Add("@" + path);
                    else AddIssue(context, "A compiler response file is unavailable: " + responseFile + ".");
                }
            }
            var parsed = CSharpCommandLineParser.Default.Parse(arguments, root, sdkDirectory: null);
            context.ParseOptions = parsed.ParseOptions;
            context.CompilationOptions = parsed.CompilationOptions.WithOutputKind(OutputKind.DynamicallyLinkedLibrary);
            foreach (var error in parsed.Errors.Where(item => item.Severity == DiagnosticSeverity.Error).Take(5))
                AddIssue(context, "A compiler option could not be parsed: " + error.GetMessage() + ".");
            var propertyOverrides = new Dictionary<string, List<MetadataReferenceProperties>>(comparer);
            foreach (var reference in parsed.MetadataReferences)
            {
                var path = ResolveReferencePath(reference.Reference, parsed.ReferencePaths, root);
                if (path == null)
                {
                    AddIssue(context, "A compiler reference path could not be resolved: " + reference.Reference + ".");
                    continue;
                }
                if (!propertyOverrides.TryGetValue(path, out var properties)) propertyOverrides.Add(path, properties = new List<MetadataReferenceProperties>());
                properties.Add(reference.Properties);
            }
            var paths = new HashSet<string>(comparer);
            if (context.Owner != null)
                AddPaths(context.Owner.allReferences, paths, context);
            else
            {
                AddIssue(context, "No owning Unity compilation assembly was found; broader Editor references do not prove a specific script target.");
                foreach (var assembly in assemblies)
                {
                    AddPaths(assembly.allReferences, paths, context);
                    AddPaths(new[] { assembly.outputPath }, paths, context);
                }
                var loaded = LoadedAssemblyCatalog.Capture();
                foreach (var descriptor in loaded.Assemblies)
                    if (descriptor.LocationStatus == "available") paths.Add(descriptor.PhysicalPath);
                if (loaded.Partial) AddIssue(context, "Some loaded runtime assembly descriptors were unavailable during broader fallback.");
            }
            foreach (var path in propertyOverrides.Keys) paths.Add(path);
            var requests = new List<MetadataReferenceRequest>();
            foreach (var path in paths.OrderBy(item => item, StringComparer.Ordinal))
            {
                // A response-file reference specifies the intended aliases. It replaces the same-path default global request.
                if (propertyOverrides.TryGetValue(path, out var properties))
                    requests.AddRange(properties.Select(property => new MetadataReferenceRequest(path, property)));
                else requests.Add(new MetadataReferenceRequest(path));
            }
            context.References = MetadataReferenceCatalog.Build(requests, root, context.Owner?.outputPath, context.Owner != null);
            if (EditorApplication.isCompiling)
                AddIssue(context, "Unity compilation is in progress; this captured reference context may be changing.");
            if (context.Generation != LoadedAssemblyCatalog.CurrentGeneration)
                AddIssue(context, "Assembly or compilation generation changed during reference capture.");
            return context;
        }

        static object GetOption(object instance, string property, CompilationReferenceContext context)
        {
            try { return instance?.GetType().GetProperty(property)?.GetValue(instance); }
            catch (TargetInvocationException ex) when (ex.InnerException != null && LoadedAssemblyCatalog.ExpectedInspectionFailure(ex.InnerException))
            { AddIssue(context, "A compiler option is unavailable: " + property + " (" + ex.InnerException.GetType().Name + ")."); return null; }
            catch (Exception ex) when (LoadedAssemblyCatalog.ExpectedInspectionFailure(ex))
            { AddIssue(context, "A compiler option is unavailable: " + property + " (" + ex.GetType().Name + ")."); return null; }
        }
        static string ResolveReferencePath(string path, IEnumerable<string> searchPaths, string root)
        {
            var absolute = ResolvePath(path);
            if (absolute == null || Path.IsPathRooted(path) || File.Exists(absolute)) return absolute;
            foreach (var directory in searchPaths ?? Enumerable.Empty<string>())
            {
                var candidate = MetadataReferenceCatalog.Canonicalize(path, MetadataReferenceCatalog.Canonicalize(directory, root));
                if (candidate != null && File.Exists(candidate)) return candidate;
            }
            return absolute;
        }
        internal static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
                if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("unity://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    return MetadataReferenceCatalog.Canonicalize(ProjectPathResolver.Resolve(path, assumeAssetRelative: true).AbsolutePath, ProjectPathResolver.ProjectRoot);
                return MetadataReferenceCatalog.Canonicalize(path, ProjectPathResolver.ProjectRoot);
            }
            catch (Exception ex) when (LoadedAssemblyCatalog.ExpectedInspectionFailure(ex)) { return null; }
        }
        static void AddPaths(IEnumerable<string> requested, ISet<string> paths, CompilationReferenceContext context)
        {
            foreach (var path in requested ?? Enumerable.Empty<string>())
            {
                var absolute = ResolvePath(path);
                if (absolute != null) paths.Add(absolute);
                else if (!string.IsNullOrWhiteSpace(path)) AddIssue(context, "A metadata reference path could not be resolved: " + path + ".");
            }
        }
        internal static void AddIssue(CompilationReferenceContext context, string message)
        {
            if (context.Limitations.Count < 32 && !context.Limitations.Contains(message)) context.Limitations.Add(message);
        }
    }

    internal sealed class CompilationReferenceContext
    {
        internal UnityAssembly Owner;
        internal CSharpParseOptions ParseOptions;
        internal CSharpCompilationOptions CompilationOptions;
        internal MetadataReferenceReport References;
        internal long Generation;
        internal readonly List<string> Limitations = new List<string>();
        internal bool Partial => Limitations.Count > 0 || References == null || References.Partial;

        internal List<SyntaxTree> GetSourceTrees(string source, string scriptPath)
        {
            const int maxFiles = 128;
            const int maxCharacters = 1000000;
            source = source ?? string.Empty;
            var target = UnityCompilationReferenceContext.ResolvePath(scriptPath);
            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, ParseOptions, target ?? scriptPath ?? "<in-memory>") };
            var characters = source.Length;
            if (characters > maxCharacters)
            {
                UnityCompilationReferenceContext.AddIssue(this, "The target source exceeds 1000000 characters.");
                return trees;
            }
            var comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var seen = new HashSet<string>(comparer);
            if (target != null) seen.Add(target);
            foreach (var path in (Owner?.sourceFiles ?? Array.Empty<string>()).OrderBy(item => item, StringComparer.Ordinal))
            {
                var absolute = UnityCompilationReferenceContext.ResolvePath(path);
                if (absolute == null)
                {
                    UnityCompilationReferenceContext.AddIssue(this, "An owning source path could not be resolved: " + path + ".");
                    continue;
                }
                if (!seen.Add(absolute)) continue;
                if (trees.Count >= maxFiles || characters >= maxCharacters)
                {
                    UnityCompilationReferenceContext.AddIssue(this, "The owning source context exceeds 128 files or 1000000 characters.");
                    break;
                }
                try
                {
                    using (var reader = new StreamReader(absolute, true))
                    {
                        var remaining = maxCharacters - characters;
                        var buffer = new char[Math.Min(8192, remaining + 1)];
                        var text = new System.Text.StringBuilder(Math.Min(8192, remaining));
                        int read;
                        while ((read = reader.Read(buffer, 0, Math.Min(buffer.Length, remaining - text.Length + 1))) > 0)
                        {
                            if (text.Length + read > remaining)
                            {
                                UnityCompilationReferenceContext.AddIssue(this, "An owning source exceeds the remaining character budget: " + path + ".");
                                break;
                            }
                            text.Append(buffer, 0, read);
                        }
                        if (read > 0) continue;
                        characters += text.Length;
                        trees.Add(CSharpSyntaxTree.ParseText(text.ToString(), ParseOptions, absolute));
                    }
                }
                catch (Exception ex) when (LoadedAssemblyCatalog.ExpectedInspectionFailure(ex))
                {
                    UnityCompilationReferenceContext.AddIssue(this, "An owning source is unavailable: " + path + " (" + ex.GetType().Name + ").");
                }
            }
            if (Generation != LoadedAssemblyCatalog.CurrentGeneration)
                UnityCompilationReferenceContext.AddIssue(this, "Assembly or compilation generation changed during source capture.");
            return trees;
        }
    }
}
