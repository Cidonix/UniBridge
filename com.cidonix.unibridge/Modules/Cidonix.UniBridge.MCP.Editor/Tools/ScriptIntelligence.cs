#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read-only script intelligence layer for AI agents.
    /// </summary>
    public static class ScriptIntelligence
    {
        const string ToolName = "UniBridge_ScriptIntelligence";
        const int DefaultLimit = 50;
        const int MaxLimit = 500;
        const int DefaultMaxScanScripts = 3000;
        const int MaxScanScriptsHardLimit = 20000;
        const int DefaultMaxScanAssets = 8000;
        const int MaxScanAssetsHardLimit = 50000;
        const int DefaultMaxTextChars = 60000;
        const int MaxTextCharsHardLimit = 250000;
        const int DefaultMaxReferences = 200;
        const int MaxReferencesHardLimit = 2000;
        const int MaxSourceAnalysisChars = 400000;
        const int MaxLineAnalysisLength = 1200;

        static readonly HashSet<string> k_AssetReferenceExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim", ".overridecontroller",
            ".playable", ".spriteatlas", ".inputactions", ".uxml", ".uss"
        };

        static readonly HashSet<string> k_UnityMessageNames = new(StringComparer.Ordinal)
        {
            "Awake", "OnEnable", "Start", "Update", "FixedUpdate", "LateUpdate", "OnDisable", "OnDestroy",
            "OnValidate", "Reset", "OnGUI", "OnDrawGizmos", "OnDrawGizmosSelected",
            "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit",
            "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D",
            "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit",
            "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D",
            "OnMouseDown", "OnMouseUp", "OnMouseDrag", "OnMouseEnter", "OnMouseExit",
            "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit"
        };

        static readonly Regex k_UsingLineRegex = new(@"^\s*using\s+(?:static\s+)?(?<name>[A-Za-z_][A-Za-z0-9_.]*)(?:\s*=\s*[A-Za-z_][A-Za-z0-9_.<>]*)?\s*;", RegexOptions.Compiled);
        static readonly Regex k_NamespaceLineRegex = new(@"^\s*namespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)\s*(?:[;{])", RegexOptions.Compiled);
        static readonly Regex k_TypeLineRegex = new(@"^\s*(?<attrs>(?:\[[^\]]+\]\s*)*)\s*(?<mods>(?:(?:public|private|protected|internal|static|abstract|sealed|partial|unsafe|new)\s+)*)?(?<kind>class|struct|interface|enum|record)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*:\s*(?<bases>[^{\r\n]+))?", RegexOptions.Compiled);
        static readonly Regex k_MethodLineRegex = new(@"^\s*(?<attrs>(?:\[[^\]]+\]\s*)*)\s*(?<mods>(?:(?:public|private|protected|internal|static|virtual|override|abstract|async|sealed|new|extern|unsafe|partial)\s+)*)?(?<return>[A-Za-z_][A-Za-z0-9_<>\[\].?, \t]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<params>[^)]*)\)\s*(?:where\b[^{;]*)?\s*(?:\{|=>|;)?\s*$", RegexOptions.Compiled);
        static readonly Regex k_PropertyLineRegex = new(@"^\s*(?<attrs>(?:\[[^\]]+\]\s*)*)\s*(?<mods>(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|new|unsafe)\s+)*)?(?<type>[A-Za-z_][A-Za-z0-9_<>\[\].?, \t]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\{\s*(?:get|set|init)\b|=>)", RegexOptions.Compiled);
        static readonly Regex k_FieldLineRegex = new(@"^\s*(?<attrs>(?:\[[^\]]+\]\s*)*)\s*(?<mods>(?:(?:public|private|protected|internal|static|readonly|const|volatile|new|unsafe)\s+)*)?(?<type>[A-Za-z_][A-Za-z0-9_<>\[\].?, \t]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:=|;|,)", RegexOptions.Compiled);
        static readonly Regex k_AttributeNameRegex = new(@"\[\s*(?<name>[A-Za-z_][A-Za-z0-9_.]*)(?:Attribute)?(?:\s*\(|\s*\]|\s*,)", RegexOptions.Compiled);

        public const string Title = "Script intelligence";

        public const string Description = @"Explore Unity C# scripts in an AI-friendly, read-only way.

Use this when you need to understand code before editing: list MonoBehaviours and ScriptableObjects, inspect one script, read code for known component types, find references, locate prefab/scene usages, summarize assemblies, or detect maintenance hotspots.

Actions:
    Catalog: List scripts and compiled types with path, kind, assembly, and Unity role. Set IncludeMembers=true only when you need member summaries.
    Analyze: Detail one script by path, GUID, query, or type name.
    ReadTypes: Return source and summaries for requested type names.
    References: Search C# source files for a type/member/text/regex pattern.
    Usages: Find scenes, prefabs, and assets that reference a script asset GUID.
    Hotspots: Scan scripts for TODO/FIXME, missing compiled types, file/class mismatches, obsolete Unity APIs, and large files.
    Assemblies: Summarize Unity compilation assemblies and script counts.
    Selection: Analyze selected MonoScript assets.
    Metrics: Return aggregate script counts by kind, assembly, folder, and Unity callback.

This tool does not modify files. Use UniBridge_ReadResource, UniBridge_ScriptApplyEdits, UniBridge_ApplyTextEdits, and UniBridge_ValidateScript for script editing workflows.";

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scripting", "resources" }, EnabledByDefault = true)]
        public static object HandleCommand(ScriptIntelligenceParams parameters)
        {
            parameters ??= new ScriptIntelligenceParams();

            try
            {
                return parameters.Action switch
                {
                    ScriptIntelligenceAction.Catalog => Catalog(parameters),
                    ScriptIntelligenceAction.Analyze => Analyze(parameters),
                    ScriptIntelligenceAction.ReadTypes => ReadTypes(parameters),
                    ScriptIntelligenceAction.References => References(parameters),
                    ScriptIntelligenceAction.Usages => Usages(parameters),
                    ScriptIntelligenceAction.Hotspots => Hotspots(parameters),
                    ScriptIntelligenceAction.Assemblies => Assemblies(parameters),
                    ScriptIntelligenceAction.Selection => Selection(parameters),
                    ScriptIntelligenceAction.Metrics => Metrics(parameters),
                    _ => Response.Error($"Unsupported action: {parameters.Action}")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ScriptIntelligence] {parameters.Action} failed: {ex}");
                return Response.Error($"Script intelligence failed: {ex.Message}");
            }
        }

        static object Catalog(ScriptIntelligenceParams p)
        {
            var all = FindScriptRecords(p).ToList();
            SortRecords(all, p.SortBy);

            var limit = Clamp(p.Limit <= 0 ? DefaultLimit : p.Limit, 1, MaxLimit);
            var page = Math.Max(1, p.Page);
            var start = (page - 1) * limit;

            var scripts = all
                .Skip(start)
                .Take(limit)
                .Select(record => BuildScriptSummary(record, p, p.IncludeMembers, false))
                .ToList();

            return Response.Success($"Cataloged {all.Count} script(s).", new
            {
                action = "Catalog",
                total = all.Count,
                page,
                limit,
                returned = scripts.Count,
                filters = BuildFilterSummary(p),
                scripts
            });
        }

        static object Analyze(ScriptIntelligenceParams p)
        {
            var record = ResolveSingleScript(p);
            if (record == null)
                return Response.Error("No script found. Provide Path, Guid, TypeName, or Query.");

            var detail = BuildScriptDetail(record, p, p.IncludeSource, p.IncludeUsages);
            return Response.Success($"Analyzed script '{record.Path}'.", new
            {
                action = "Analyze",
                script = detail
            });
        }

        static object ReadTypes(ScriptIntelligenceParams p)
        {
            var requested = BuildRequestedTypeNames(p).ToList();
            if (requested.Count == 0)
                return Response.Error("ReadTypes requires TypeName or TypeNames.");

            var records = FindScriptRecords(new ScriptIntelligenceParams
            {
                IncludePackages = p.IncludePackages,
                IncludeAbstract = true,
                MaxScanScripts = p.MaxScanScripts,
                Limit = p.Limit,
                Kind = ScriptKindFilter.Any
            }).ToList();

            var assets = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var typeName in requested)
            {
                if (!seen.Add(typeName))
                {
                    assets.Add(new { requestedType = typeName, error = $"Type '{typeName}' listed multiple times." });
                    continue;
                }

                var record = FindRecordByTypeName(records, typeName);
                if (record == null)
                {
                    assets.Add(new { requestedType = typeName, error = $"Type '{typeName}' was not found in MonoScript assets." });
                    continue;
                }

                assets.Add(BuildScriptDetail(record, p, true, false));
            }

            return Response.Success($"Read {assets.Count} requested type entr{(assets.Count == 1 ? "y" : "ies")}.", new
            {
                action = "ReadTypes",
                requested = requested.Count,
                assets
            });
        }

        static object References(ScriptIntelligenceParams p)
        {
            var pattern = FirstNonEmpty(p.Pattern, p.TypeName, p.Query);
            if (string.IsNullOrWhiteSpace(pattern))
                return Response.Error("References requires Pattern, TypeName, or Query.");

            Regex regex = null;
            if (p.UseRegex)
            {
                try
                {
                    regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.Multiline);
                }
                catch (Exception ex)
                {
                    return Response.Error($"Invalid regex pattern: {ex.Message}");
                }
            }

            var maxReferences = Clamp(p.MaxReferences <= 0 ? DefaultMaxReferences : p.MaxReferences, 1, MaxReferencesHardLimit);
            var scripts = FindScriptRecords(new ScriptIntelligenceParams
            {
                IncludePackages = p.IncludePackages,
                MaxScanScripts = p.MaxScanScripts,
                Path = p.Path,
                Paths = p.Paths,
                Kind = ScriptKindFilter.Any,
                IncludeAbstract = true
            }).ToList();

            var matches = new List<object>();
            foreach (var record in scripts)
            {
                var text = record.GetSourceText();
                if (string.IsNullOrEmpty(text))
                    continue;

                AddSourceMatches(record, text, pattern, regex, p.UseRegex, maxReferences, matches);
                if (matches.Count >= maxReferences)
                    break;
            }

            return Response.Success($"Found {matches.Count} source reference(s).", new
            {
                action = "References",
                pattern,
                useRegex = p.UseRegex,
                totalScanned = scripts.Count,
                returned = matches.Count,
                truncated = matches.Count >= maxReferences,
                matches
            });
        }

        static object Usages(ScriptIntelligenceParams p)
        {
            var record = ResolveSingleScript(p);
            if (record == null)
                return Response.Error("No script found for usage scan. Provide Path, Guid, TypeName, or Query.");

            var usages = FindScriptAssetUsages(record, p);
            return Response.Success($"Found {usages.Count} asset usage(s) for '{record.Path}'.", new
            {
                action = "Usages",
                target = BuildScriptSummary(record, p, false, false),
                usages
            });
        }

        static object Hotspots(ScriptIntelligenceParams p)
        {
            var records = FindScriptRecords(p).ToList();
            var maxItems = Clamp(p.Limit <= 0 ? DefaultLimit : p.Limit, 1, MaxLimit);
            var issues = new List<object>();

            foreach (var record in records)
            {
                var analysis = AnalyzeSource(record);
                foreach (var issue in BuildIssues(record, analysis, p))
                {
                    issues.Add(issue);
                    if (issues.Count >= maxItems)
                        break;
                }

                if (issues.Count >= maxItems)
                    break;
            }

            return Response.Success($"Found {issues.Count} script hotspot(s).", new
            {
                action = "Hotspots",
                scanned = records.Count,
                returned = issues.Count,
                truncated = issues.Count >= maxItems,
                issues
            });
        }

        static object Assemblies(ScriptIntelligenceParams p)
        {
            var query = NormalizeQuery(p.Query);
            var assemblies = CompilationPipeline.GetAssemblies()
                .Where(assembly => string.IsNullOrEmpty(query) || ContainsIgnoreCase(assembly.name, query) || ContainsIgnoreCase(assembly.outputPath, query))
                .Select(assembly => new
                {
                    name = assembly.name,
                    outputPath = NormalizePath(assembly.outputPath),
                    flags = assembly.flags.ToString(),
                    sourceFileCount = assembly.sourceFiles?.Length ?? 0,
                    defineCount = assembly.defines?.Length ?? 0,
                    defines = TakeOrdered(assembly.defines, 80),
                    assemblyReferences = assembly.assemblyReferences == null
                        ? Array.Empty<string>()
                        : assembly.assemblyReferences
                            .Where(reference => reference != null)
                            .Select(reference => reference.name)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(name => name, StringComparer.Ordinal)
                            .Take(80)
                            .ToArray(),
                    sampleSources = TakeOrdered(assembly.sourceFiles?.Select(NormalizePath), 20)
                })
                .OrderBy(item => item.name, StringComparer.OrdinalIgnoreCase)
                .Take(Clamp(p.Limit <= 0 ? 100 : p.Limit, 1, MaxLimit))
                .ToList();

            var asmdefAssets = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => ShouldIncludePath(path, p.IncludePackages))
                .Select(path => new
                {
                    path,
                    guid = AssetDatabase.AssetPathToGUID(path),
                    name = Path.GetFileNameWithoutExtension(path)
                })
                .OrderBy(item => item.path, StringComparer.OrdinalIgnoreCase)
                .Take(Clamp(p.Limit <= 0 ? 100 : p.Limit, 1, MaxLimit))
                .ToList();

            return Response.Success($"Found {assemblies.Count} compilation assembl{(assemblies.Count == 1 ? "y" : "ies")}.", new
            {
                action = "Assemblies",
                query,
                assemblies,
                asmdefs = asmdefAssets
            });
        }

        static object Selection(ScriptIntelligenceParams p)
        {
            var records = UnityEditor.Selection.objects
                .OfType<MonoScript>()
                .Select(script => AssetDatabase.GetAssetPath(script))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => BuildScriptRecord(path))
                .Where(record => record != null)
                .Take(Clamp(p.Limit <= 0 ? DefaultLimit : p.Limit, 1, MaxLimit))
                .Select(record => BuildScriptDetail(record, p, p.IncludeSource, p.IncludeUsages))
                .ToList();

            return Response.Success($"Analyzed {records.Count} selected script(s).", new
            {
                action = "Selection",
                selectionCount = UnityEditor.Selection.count,
                scripts = records
            });
        }

        static object Metrics(ScriptIntelligenceParams p)
        {
            var records = FindScriptRecords(p).ToList();
            var analyses = records.Select(record => new { record, analysis = AnalyzeSource(record) }).ToList();

            var callbackCounts = analyses
                .SelectMany(item => item.analysis.UnityMessages)
                .GroupBy(name => name, StringComparer.Ordinal)
                .Select(group => new { name = group.Key, count = group.Count() })
                .OrderByDescending(item => item.count)
                .ThenBy(item => item.name, StringComparer.Ordinal)
                .Take(60)
                .ToList();

            return Response.Success($"Built metrics for {records.Count} script(s).", new
            {
                action = "Metrics",
                totalScripts = records.Count,
                totalLines = analyses.Sum(item => item.analysis.LineCount),
                byKind = CountBy(records, record => record.Kind),
                byAssembly = CountBy(records, record => record.AssemblyName ?? "unknown").Take(80).ToList(),
                byTopFolder = CountBy(records, record => TopFolder(record.Path)).Take(80).ToList(),
                unityMessages = callbackCounts,
                largestScripts = records
                    .OrderByDescending(record => AnalyzeSource(record).LineCount)
                    .Take(20)
                    .Select(record => new
                    {
                        path = record.Path,
                        fullName = record.FullName,
                        kind = record.Kind,
                        lineCount = AnalyzeSource(record).LineCount
                    })
                    .ToList()
            });
        }

        static IEnumerable<ScriptRecord> FindScriptRecords(ScriptIntelligenceParams p)
        {
            var maxScan = Clamp(p.MaxScanScripts <= 0 ? DefaultMaxScanScripts : p.MaxScanScripts, 1, MaxScanScriptsHardLimit);
            var query = NormalizeQuery(FirstNonEmpty(p.Query, p.TypeName));
            var scopes = BuildScriptSearchScopes(p);
            var guids = scopes.Length > 0
                ? AssetDatabase.FindAssets("t:MonoScript", scopes)
                : AssetDatabase.FindAssets("t:MonoScript");

            var scanned = 0;
            foreach (var guid in guids.Distinct(StringComparer.Ordinal))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(path) || !ShouldIncludePath(path, p.IncludePackages))
                    continue;

                scanned++;
                if (scanned > maxScan)
                    yield break;

                var record = BuildScriptRecord(path);
                if (record == null)
                    continue;

                if (!p.IncludeAbstract && record.Type != null && (record.Type.IsAbstract || record.Type.IsGenericTypeDefinition))
                    continue;

                if (!MatchesKind(record, p.Kind))
                    continue;

                if (!MatchesBaseType(record, p.BaseType))
                    continue;

                if (!MatchesQuery(record, query))
                    continue;

                yield return record;
            }
        }

        static ScriptRecord ResolveSingleScript(ScriptIntelligenceParams p)
        {
            if (!string.IsNullOrWhiteSpace(p.Guid))
            {
                var path = AssetDatabase.GUIDToAssetPath(p.Guid.Trim());
                var record = BuildScriptRecord(path);
                if (record != null)
                    return record;
            }

            foreach (var path in ExpandPaths(p))
            {
                var record = BuildScriptRecord(path);
                if (record != null)
                    return record;
            }

            if (!string.IsNullOrWhiteSpace(p.TypeName))
            {
                var records = FindScriptRecords(new ScriptIntelligenceParams
                {
                    IncludePackages = p.IncludePackages,
                    IncludeAbstract = true,
                    MaxScanScripts = p.MaxScanScripts,
                    Kind = ScriptKindFilter.Any
                }).ToList();
                var byType = FindRecordByTypeName(records, p.TypeName);
                if (byType != null)
                    return byType;
            }

            if (!string.IsNullOrWhiteSpace(p.Query))
                return FindScriptRecords(p).FirstOrDefault();

            return null;
        }

        static ScriptRecord BuildScriptRecord(string path)
        {
            path = NormalizeAssetPath(path);
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return null;

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (script == null)
                return null;

            Type type = null;
            try
            {
                type = script.GetClass();
            }
            catch
            {
                type = null;
            }

            var absolutePath = AssetPathToAbsolutePath(path);
            var info = !string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath) ? new FileInfo(absolutePath) : null;
            return new ScriptRecord
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                Name = Path.GetFileNameWithoutExtension(path),
                MonoScript = script,
                Type = type,
                FullName = type?.FullName,
                Namespace = type?.Namespace,
                TypeName = type?.Name,
                AssemblyName = type?.Assembly.GetName().Name,
                Kind = ClassifyType(type),
                AbsolutePath = absolutePath,
                SizeBytes = info?.Length ?? 0,
                ModifiedUtc = info?.LastWriteTimeUtc
            };
        }

        static object BuildScriptSummary(ScriptRecord record, ScriptIntelligenceParams p, bool includeMembers, bool includeSource)
        {
            var analysis = includeMembers || includeSource ? AnalyzeSource(record) : null;
            var primaryDeclaration = analysis?.Types.FirstOrDefault();

            return new
            {
                path = record.Path,
                guid = record.Guid,
                name = record.Name,
                typeName = record.TypeName ?? primaryDeclaration?.Name,
                fullName = record.FullName ?? CombineNamespaceAndName(analysis?.Namespace, primaryDeclaration?.Name),
                namespaceName = record.Namespace ?? analysis?.Namespace,
                kind = record.Kind,
                assembly = record.AssemblyName,
                baseType = GetBaseTypeName(record.Type) ?? primaryDeclaration?.BaseTypes?.FirstOrDefault(),
                attachable = record.Type != null && typeof(Component).IsAssignableFrom(record.Type) && !record.Type.IsAbstract && !record.Type.IsGenericTypeDefinition,
                isAbstract = record.Type?.IsAbstract ?? false,
                isGenericTypeDefinition = record.Type?.IsGenericTypeDefinition ?? false,
                lineCount = analysis?.LineCount,
                sizeBytes = record.SizeBytes,
                modifiedUtc = record.ModifiedUtc?.ToString("O"),
                attributes = BuildTypeAttributes(record.Type, analysis),
                requireComponents = BuildRequireComponents(record.Type),
                addComponentMenu = GetAddComponentMenu(record.Type),
                declarations = includeMembers ? BuildTypeDeclarationObjects(analysis?.Types) : null,
                unityMessages = includeMembers ? analysis?.UnityMessages : null,
                inspectorFields = includeMembers ? analysis?.InspectorFields : null,
                publicMethods = includeMembers ? analysis?.PublicMethods : null,
                source = includeSource ? TruncateText(record.GetSourceText(), Clamp(p.MaxTextChars <= 0 ? DefaultMaxTextChars : p.MaxTextChars, 1000, MaxTextCharsHardLimit)) : null
            };
        }

        static object BuildScriptDetail(ScriptRecord record, ScriptIntelligenceParams p, bool includeSource, bool includeUsages)
        {
            var analysis = AnalyzeSource(record);
            var sourceLimit = Clamp(p.MaxTextChars <= 0 ? DefaultMaxTextChars : p.MaxTextChars, 1000, MaxTextCharsHardLimit);

            return new
            {
                summary = BuildScriptSummary(record, p, true, false),
                sourceShape = new
                {
                    namespaceName = analysis.Namespace,
                    usings = analysis.Usings,
                    interfaceSummary = analysis.InterfaceSummary,
                    declarations = BuildTypeDeclarationObjects(analysis.Types),
                    fields = FilterMembers(analysis.Fields, p.IncludePrivateMembers),
                    properties = FilterMembers(analysis.Properties, p.IncludePrivateMembers),
                    methods = FilterMembers(analysis.Methods, p.IncludePrivateMembers),
                    unityMessages = analysis.UnityMessages,
                    inspectorFields = analysis.InspectorFields
                },
                reflection = BuildReflectionSummary(record.Type, p.IncludePrivateMembers),
                metrics = new
                {
                    analysis.LineCount,
                    analysis.UsingCount,
                    analysis.TypeCount,
                    fieldCount = analysis.Fields.Count,
                    propertyCount = analysis.Properties.Count,
                    methodCount = analysis.Methods.Count,
                    unityMessageCount = analysis.UnityMessages.Count,
                    inspectorFieldCount = analysis.InspectorFields.Count
                },
                issues = BuildIssues(record, analysis, p),
                usages = includeUsages ? FindScriptAssetUsages(record, p) : null,
                source = includeSource ? TruncateText(record.GetSourceText(), sourceLimit) : null
            };
        }

        static SourceAnalysis AnalyzeSource(ScriptRecord record)
        {
            if (record.Analysis != null)
                return record.Analysis;

            var text = record.GetSourceText() ?? string.Empty;
            var analysis = new SourceAnalysis
            {
                LineCount = CountLines(text)
            };

            var scanText = text.Length > MaxSourceAnalysisChars ? text.Substring(0, MaxSourceAnalysisChars) : text;
            var lines = SplitLines(scanText);
            var braceDepths = ComputeBraceDepths(lines);
            var pendingAttributes = string.Empty;
            for (var i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];
                if (string.IsNullOrWhiteSpace(rawLine) || rawLine.Length > MaxLineAnalysisLength)
                    continue;

                var lineNumber = i + 1;
                var braceDepth = braceDepths.Length > i ? braceDepths[i] : 0;
                var trimmed = rawLine.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;

                if (trimmed.StartsWith("[", StringComparison.Ordinal) && IsAttributeOnlyLine(trimmed))
                {
                    pendingAttributes += trimmed + " ";
                    continue;
                }

                var usingMatch = k_UsingLineRegex.Match(rawLine);
                if (usingMatch.Success)
                {
                    var usingName = usingMatch.Groups["name"].Value;
                    if (!string.IsNullOrWhiteSpace(usingName))
                        analysis.Usings.Add(usingName);
                    pendingAttributes = string.Empty;
                    continue;
                }

                var namespaceMatch = k_NamespaceLineRegex.Match(rawLine);
                if (namespaceMatch.Success)
                {
                    analysis.Namespace ??= namespaceMatch.Groups["name"].Value;
                    pendingAttributes = string.Empty;
                    continue;
                }

                var typeMatch = k_TypeLineRegex.Match(rawLine);
                if (typeMatch.Success)
                {
                    var typeName = typeMatch.Groups["name"].Value;
                    analysis.Types.Add(new ParsedType
                    {
                        Kind = typeMatch.Groups["kind"].Value,
                        Name = typeName,
                        FullName = CombineNamespaceAndName(analysis.Namespace, typeName),
                        Modifiers = SplitWords(typeMatch.Groups["mods"].Value),
                        BaseTypes = SplitBaseTypes(typeMatch.Groups["bases"].Value),
                        Attributes = ExtractAttributeNames(pendingAttributes + typeMatch.Groups["attrs"].Value),
                        Line = lineNumber,
                        BodyDepth = braceDepth + 1
                    });
                    pendingAttributes = string.Empty;
                    continue;
                }

                var propertyMatch = k_PropertyLineRegex.Match(rawLine);
                if (propertyMatch.Success && IsDirectTypeMemberLine(analysis, lineNumber, braceDepth))
                {
                    analysis.Properties.Add(BuildMember(propertyMatch, lineNumber, "property", pendingAttributes));
                    pendingAttributes = string.Empty;
                    continue;
                }

                var methodMatch = k_MethodLineRegex.Match(rawLine);
                if (methodMatch.Success && !LooksLikeControlStatement(methodMatch) && IsDirectTypeMemberLine(analysis, lineNumber, braceDepth))
                {
                    analysis.Methods.Add(BuildMember(methodMatch, lineNumber, "method", pendingAttributes));
                    pendingAttributes = string.Empty;
                    continue;
                }

                if (rawLine.IndexOf('(') >= 0)
                {
                    pendingAttributes = string.Empty;
                    continue;
                }

                var fieldMatch = k_FieldLineRegex.Match(rawLine);
                if (fieldMatch.Success && !LooksLikeControlStatement(fieldMatch) && IsDirectTypeMemberLine(analysis, lineNumber, braceDepth))
                {
                    analysis.Fields.Add(BuildMember(fieldMatch, lineNumber, "field", pendingAttributes));
                    pendingAttributes = string.Empty;
                    continue;
                }

                pendingAttributes = string.Empty;
            }

            analysis.Usings = analysis.Usings
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();

            analysis.UnityMessages = analysis.Methods
                .Where(member => k_UnityMessageNames.Contains(member.Name))
                .Select(member => member.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            analysis.InspectorFields = analysis.Fields
                .Where(IsInspectorField)
                .Select(member => new
                {
                    member.name,
                    member.type,
                    member.visibility,
                    member.isStatic,
                    member.attributes,
                    member.line
                })
                .Cast<object>()
                .ToList();

            analysis.PublicMethods = analysis.Methods
                .Where(member => member.Visibility == "public" && !member.IsStatic)
                .Select(member => new
                {
                    member.name,
                    returnType = member.type,
                    member.parameters,
                    member.attributes,
                    member.line
                })
                .Cast<object>()
                .ToList();

            analysis.UsingCount = analysis.Usings.Count;
            analysis.TypeCount = analysis.Types.Count;
            analysis.InterfaceSummary = BuildSourceInterfaceSummary(analysis);
            record.Analysis = analysis;
            return analysis;
        }

        static ParsedMember BuildMember(Match match, int lineNumber, string kind, string pendingAttributes = null)
        {
            var mods = SplitWords(match.Groups["mods"].Value);
            var attributes = ExtractAttributeNames((pendingAttributes ?? string.Empty) + match.Groups["attrs"].Value);
            return new ParsedMember
            {
                Kind = kind,
                Name = match.Groups["name"].Value,
                Type = NormalizeWhitespace(match.Groups["return"].Success ? match.Groups["return"].Value : match.Groups["type"].Value),
                Visibility = DetermineVisibility(mods),
                IsStatic = mods.Contains("static"),
                Modifiers = mods,
                Attributes = attributes,
                Parameters = match.Groups["params"].Success ? NormalizeWhitespace(match.Groups["params"].Value) : null,
                Line = lineNumber
            };
        }

        static bool IsDirectTypeMemberLine(SourceAnalysis analysis, int lineNumber, int braceDepth)
        {
            var currentType = analysis.Types
                .Where(type => type.Line < lineNumber && braceDepth >= type.BodyDepth)
                .OrderByDescending(type => type.BodyDepth)
                .ThenByDescending(type => type.Line)
                .FirstOrDefault();

            return currentType != null && braceDepth == currentType.BodyDepth;
        }

        static int[] ComputeBraceDepths(string[] lines)
        {
            var result = new int[lines.Length];
            var depth = 0;
            var inBlockComment = false;
            for (var i = 0; i < lines.Length; i++)
            {
                result[i] = depth;
                var code = StripCommentsAndStrings(lines[i], ref inBlockComment);
                foreach (var ch in code)
                {
                    if (ch == '{')
                        depth++;
                    else if (ch == '}')
                        depth = Math.Max(0, depth - 1);
                }
            }

            return result;
        }

        static string StripCommentsAndStrings(string line, ref bool inBlockComment)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var chars = line.ToCharArray();
            var inString = false;
            var inChar = false;
            var verbatim = false;
            for (var i = 0; i < chars.Length; i++)
            {
                var current = chars[i];
                var next = i + 1 < chars.Length ? chars[i + 1] : '\0';

                if (inBlockComment)
                {
                    chars[i] = ' ';
                    if (current == '*' && next == '/')
                    {
                        chars[i + 1] = ' ';
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }

                if (inString)
                {
                    chars[i] = ' ';
                    if (!verbatim && current == '\\' && next != '\0')
                    {
                        chars[i + 1] = ' ';
                        i++;
                        continue;
                    }

                    if (current == '"' && (!verbatim || next != '"'))
                        inString = false;
                    else if (current == '"' && verbatim && next == '"')
                    {
                        chars[i + 1] = ' ';
                        i++;
                    }
                    continue;
                }

                if (inChar)
                {
                    chars[i] = ' ';
                    if (current == '\\' && next != '\0')
                    {
                        chars[i + 1] = ' ';
                        i++;
                        continue;
                    }

                    if (current == '\'')
                        inChar = false;
                    continue;
                }

                if (current == '/' && next == '/')
                {
                    for (var j = i; j < chars.Length; j++)
                        chars[j] = ' ';
                    break;
                }

                if (current == '/' && next == '*')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    inBlockComment = true;
                    i++;
                    continue;
                }

                if (current == '@' && next == '"')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    inString = true;
                    verbatim = true;
                    i++;
                    continue;
                }

                if (current == '"')
                {
                    chars[i] = ' ';
                    inString = true;
                    verbatim = false;
                    continue;
                }

                if (current == '\'')
                {
                    chars[i] = ' ';
                    inChar = true;
                }
            }

            return new string(chars);
        }

        static object BuildSourceInterfaceSummary(SourceAnalysis analysis)
        {
            var members = analysis.Fields
                .Concat(analysis.Properties)
                .Concat(analysis.Methods)
                .Where(member => member.Visibility == "public" || member.IsInspectorVisible || k_UnityMessageNames.Contains(member.Name))
                .OrderBy(member => member.Line)
                .Take(80)
                .Select(member => new
                {
                    member.Kind,
                    member.Name,
                    signature = FormatMemberSignature(member),
                    member.Visibility,
                    inspectorVisible = member.IsInspectorVisible,
                    unityMessage = k_UnityMessageNames.Contains(member.Name),
                    member.Line
                })
                .ToArray();

            return new
            {
                typeCount = analysis.Types.Count,
                memberCount = members.Length,
                types = analysis.Types.Take(20).Select(type => new
                {
                    type.Kind,
                    type.Name,
                    type.FullName,
                    bases = type.BaseTypes,
                    type.Line
                }).ToArray(),
                members
            };
        }

        static string FormatMemberSignature(ParsedMember member)
        {
            var prefix = string.Join(" ", member.Modifiers.Where(mod => mod != "public" && mod != "private" && mod != "protected" && mod != "internal"));
            if (!string.IsNullOrWhiteSpace(prefix))
                prefix += " ";

            return member.Kind == "method"
                ? $"{member.Visibility} {prefix}{member.Type} {member.Name}({member.Parameters})"
                : $"{member.Visibility} {prefix}{member.Type} {member.Name}";
        }

        static List<object> BuildIssues(ScriptRecord record, SourceAnalysis analysis, ScriptIntelligenceParams p)
        {
            var issues = new List<object>();
            var source = record.GetSourceText() ?? string.Empty;

            if (record.Type == null)
            {
                issues.Add(BuildIssue("warning", "missingCompiledType", record, 1, "Unity could not resolve a compiled type for this MonoScript. This can mean a compile error, an editor refresh gap, or a file/class mismatch."));
            }

            var declarationName = FirstDeclarationName(analysis);
            if (record.Type != null &&
                typeof(Object).IsAssignableFrom(record.Type) &&
                !string.IsNullOrEmpty(declarationName) &&
                !string.Equals(record.Name, declarationName, StringComparison.Ordinal))
            {
                issues.Add(BuildIssue("warning", "fileClassNameMismatch", record, 1, $"File name '{record.Name}' differs from main Unity object type '{declarationName}'. Unity object scripts are safest when file and type names match."));
            }

            foreach (Match match in Regex.Matches(source, @"(?im)\b(TODO|FIXME|HACK)\b[:\s-]*(?<text>[^\r\n]*)"))
            {
                issues.Add(BuildIssue("info", "todo", record, LineNumberAt(source, match.Index), match.Value.Trim()));
                if (issues.Count >= 20)
                    break;
            }

            AddPatternIssues(source, record, issues, @"\bGetInstanceID\s*\(", "obsoleteUnityApi", "Object.GetInstanceID is obsolete in Unity 6. Prefer EntityId helpers where possible.");
            AddPatternIssues(source, record, issues, @"\bFindObjectsSortMode\b", "obsoleteUnityApi", "FindObjectsSortMode is obsolete in Unity 6. Prefer FindObjectsByType overloads without sort mode.");
            AddPatternIssues(source, record, issues, @"\busedByComposite\b", "obsoleteUnityApi", "Collider2D.usedByComposite is obsolete. Prefer compositeOperation.");

            if (analysis.LineCount > 800)
                issues.Add(BuildIssue("info", "largeFile", record, 1, $"Large script ({analysis.LineCount} lines). Consider focused reads before editing."));

            if (record.Path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !record.Path.Contains("/Editor/", StringComparison.OrdinalIgnoreCase) &&
                source.Contains("using UnityEditor", StringComparison.Ordinal))
            {
                issues.Add(BuildIssue("warning", "unityEditorInRuntimeFolder", record, 1, "Script imports UnityEditor outside an Editor folder. This may break player builds unless an asmdef constrains it to Editor."));
            }

            return issues;
        }

        static List<object> FindScriptAssetUsages(ScriptRecord target, ScriptIntelligenceParams p)
        {
            var usages = new List<object>();
            if (string.IsNullOrWhiteSpace(target.Guid))
                return usages;

            var maxScan = Clamp(p.MaxScanAssets <= 0 ? DefaultMaxScanAssets : p.MaxScanAssets, 1, MaxScanAssetsHardLimit);
            var remainingLocations = Clamp(p.MaxUsageLocations <= 0 ? 100 : p.MaxUsageLocations, 1, MaxReferencesHardLimit);
            var paths = AssetDatabase.FindAssets("")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(path => ShouldIncludePath(path, p.IncludePackages))
                .Where(path => !string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase))
                .Where(path => k_AssetReferenceExtensions.Contains(Path.GetExtension(path)))
                .Take(maxScan)
                .ToList();

            foreach (var path in paths)
            {
                var text = ReadAssetText(path);
                if (string.IsNullOrEmpty(text) || text.IndexOf(target.Guid, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var count = CountOccurrences(text, target.Guid, StringComparison.OrdinalIgnoreCase);
                var locations = p.IncludeUsageLocations && remainingLocations > 0
                    ? AssetReferenceLocator.FindGuidReferences(path, target.Guid, target.Path, remainingLocations)
                    : Array.Empty<object>();
                remainingLocations -= locations.Length;

                usages.Add(new
                {
                    path,
                    guid = AssetDatabase.AssetPathToGUID(path),
                    type = AssetDatabase.GetMainAssetTypeAtPath(path)?.Name ?? "Unknown",
                    referenceCount = count,
                    locationCount = locations.Length,
                    locations = p.IncludeUsageLocations ? locations : null,
                    dependency = AssetDatabase.GetDependencies(path, false).Contains(target.Path),
                    extension = Path.GetExtension(path)
                });
            }

            return usages
                .OrderBy(item => GetAnonymousString(item, "path"), StringComparer.OrdinalIgnoreCase)
                .Cast<object>()
                .ToList();
        }

        static void AddSourceMatches(ScriptRecord record, string text, string pattern, Regex regex, bool useRegex, int maxReferences, List<object> matches)
        {
            var lines = SplitLines(text);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var lineMatches = useRegex
                    ? regex.Matches(line).Cast<Match>().Where(match => match.Success).Select(match => new { index = match.Index, value = match.Value })
                    : FindPlainMatches(line, pattern).Select(index => new { index, value = pattern });

                foreach (var match in lineMatches)
                {
                    matches.Add(new
                    {
                        path = record.Path,
                        guid = record.Guid,
                        fullName = record.FullName,
                        kind = record.Kind,
                        line = i + 1,
                        column = match.index + 1,
                        match = match.value,
                        preview = TrimLine(line)
                    });

                    if (matches.Count >= maxReferences)
                        return;
                }
            }
        }

        static object BuildReflectionSummary(Type type, bool includePrivate)
        {
            if (type == null)
                return null;

            var flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public;
            if (includePrivate)
                flags |= BindingFlags.NonPublic;

            return new
            {
                fullName = type.FullName,
                assembly = type.Assembly.GetName().Name,
                baseType = GetBaseTypeName(type),
                interfaces = type.GetInterfaces().Select(t => t.FullName).OrderBy(name => name, StringComparer.Ordinal).Take(80).ToArray(),
                attributes = type.GetCustomAttributes(false).Select(attr => attr.GetType().Name).OrderBy(name => name, StringComparer.Ordinal).Take(80).ToArray(),
                fields = type.GetFields(flags).Select(field => new
                {
                    field.Name,
                    fieldType = FriendlyTypeName(field.FieldType),
                    isPublic = field.IsPublic,
                    isStatic = field.IsStatic,
                    attributes = field.GetCustomAttributes(false).Select(attr => attr.GetType().Name).ToArray()
                }).Take(120).ToArray(),
                properties = type.GetProperties(flags).Select(prop => new
                {
                    prop.Name,
                    propertyType = FriendlyTypeName(prop.PropertyType),
                    canRead = prop.CanRead,
                    canWrite = prop.CanWrite,
                    attributes = prop.GetCustomAttributes(false).Select(attr => attr.GetType().Name).ToArray()
                }).Take(120).ToArray(),
                methods = type.GetMethods(flags)
                    .Where(method => !method.IsSpecialName)
                    .Select(method => new
                    {
                        method.Name,
                        returnType = FriendlyTypeName(method.ReturnType),
                        isPublic = method.IsPublic,
                        isStatic = method.IsStatic,
                        parameters = method.GetParameters().Select(param => FriendlyTypeName(param.ParameterType) + " " + param.Name).ToArray(),
                        attributes = method.GetCustomAttributes(false).Select(attr => attr.GetType().Name).ToArray()
                    }).Take(160).ToArray()
            };
        }

        static string[] BuildTypeAttributes(Type type, SourceAnalysis analysis)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (type != null)
            {
                foreach (var attr in type.GetCustomAttributes(false))
                    result.Add(attr.GetType().Name);
            }

            if (analysis != null)
            {
                foreach (var declaration in analysis.Types)
                {
                    foreach (var attr in declaration.Attributes)
                        result.Add(attr);
                }
            }

            return result.OrderBy(value => value, StringComparer.Ordinal).Take(80).ToArray();
        }

        static string[] BuildRequireComponents(Type type)
        {
            if (type == null)
                return Array.Empty<string>();

            return type.GetCustomAttributes(typeof(RequireComponent), true)
                .OfType<RequireComponent>()
                .SelectMany(attr => new[]
                {
                    GetRequireComponentType(attr, "m_Type0"),
                    GetRequireComponentType(attr, "m_Type1"),
                    GetRequireComponentType(attr, "m_Type2")
                })
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        static string GetRequireComponentType(RequireComponent attr, string fieldName)
        {
            var field = typeof(RequireComponent).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            var type = field?.GetValue(attr) as Type;
            return type == null ? null : FriendlyTypeName(type);
        }

        static string GetAddComponentMenu(Type type)
        {
            if (type == null)
                return null;

            var attr = type.GetCustomAttributes(typeof(AddComponentMenu), true).OfType<AddComponentMenu>().FirstOrDefault();
            if (attr == null)
                return null;

            var field = typeof(AddComponentMenu).GetField("m_AddComponentMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(attr) as string;
        }

        static List<object> FilterMembers(List<ParsedMember> members, bool includePrivate)
        {
            return members
                .Where(member => includePrivate || member.Visibility == "public" || member.IsInspectorVisible)
                .Select(member => new
                {
                    kind = member.Kind,
                    name = member.Name,
                    type = member.Type,
                    visibility = member.Visibility,
                    isStatic = member.IsStatic,
                    modifiers = member.Modifiers,
                    attributes = member.Attributes,
                    parameters = member.Parameters,
                    line = member.Line
                })
                .Cast<object>()
                .ToList();
        }

        static List<object> BuildTypeDeclarationObjects(List<ParsedType> types)
        {
            if (types == null)
                return null;

            return types
                .Select(type => new
                {
                    kind = type.Kind,
                    name = type.Name,
                    fullName = type.FullName,
                    modifiers = type.Modifiers,
                    baseTypes = type.BaseTypes,
                    attributes = type.Attributes,
                    line = type.Line
                })
                .Cast<object>()
                .ToList();
        }

        static bool IsInspectorField(ParsedMember member)
        {
            if (member.Kind != "field" || member.IsStatic)
                return false;

            if (member.Attributes.Any(attr => string.Equals(attr, "NonSerialized", StringComparison.Ordinal) || string.Equals(attr, "HideInInspector", StringComparison.Ordinal)))
                return false;

            if (member.Visibility == "public")
                return true;

            return member.Attributes.Any(attr =>
                string.Equals(attr, "SerializeField", StringComparison.Ordinal) ||
                string.Equals(attr, "SerializeReference", StringComparison.Ordinal));
        }

        static IEnumerable<string> BuildRequestedTypeNames(ScriptIntelligenceParams p)
        {
            if (!string.IsNullOrWhiteSpace(p.TypeName))
                yield return p.TypeName.Trim();

            if (p.TypeNames == null)
                yield break;

            foreach (var typeName in p.TypeNames)
            {
                if (!string.IsNullOrWhiteSpace(typeName))
                    yield return typeName.Trim();
            }
        }

        static ScriptRecord FindRecordByTypeName(List<ScriptRecord> records, string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            var trimmed = typeName.Trim();
            return records.FirstOrDefault(record =>
                string.Equals(record.FullName, trimmed, StringComparison.Ordinal) ||
                string.Equals(record.TypeName, trimmed, StringComparison.Ordinal) ||
                string.Equals(record.Name, trimmed, StringComparison.Ordinal));
        }

        static void SortRecords(List<ScriptRecord> records, ScriptIntelligenceSortMode sortBy)
        {
            var ordered = (sortBy switch
            {
                ScriptIntelligenceSortMode.Path => records.OrderBy(record => record.Path, StringComparer.OrdinalIgnoreCase),
                ScriptIntelligenceSortMode.Kind => records.OrderBy(record => record.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase),
                ScriptIntelligenceSortMode.Assembly => records.OrderBy(record => record.AssemblyName ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase),
                ScriptIntelligenceSortMode.LineCountDescending => records.OrderByDescending(record => AnalyzeSource(record).LineCount),
                ScriptIntelligenceSortMode.ModifiedDescending => records.OrderByDescending(record => record.ModifiedUtc ?? DateTime.MinValue),
                _ => records.OrderBy(record => record.TypeName ?? record.Name, StringComparer.OrdinalIgnoreCase)
            }).ToList();

            records.Clear();
            records.AddRange(ordered);
        }

        static bool MatchesQuery(ScriptRecord record, string query)
        {
            if (string.IsNullOrEmpty(query))
                return true;

            return ContainsIgnoreCase(record.Path, query) ||
                   ContainsIgnoreCase(record.Name, query) ||
                   ContainsIgnoreCase(record.TypeName, query) ||
                   ContainsIgnoreCase(record.FullName, query) ||
                   ContainsIgnoreCase(record.Namespace, query) ||
                   ContainsIgnoreCase(record.AssemblyName, query);
        }

        static bool MatchesKind(ScriptRecord record, ScriptKindFilter kind)
        {
            if (kind == ScriptKindFilter.Any)
                return true;

            return string.Equals(record.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        static bool MatchesBaseType(ScriptRecord record, string baseType)
        {
            if (string.IsNullOrWhiteSpace(baseType))
                return true;

            if (record.Type == null)
                return false;

            var target = baseType.Trim();
            var type = record.Type;
            while (type != null)
            {
                if (string.Equals(type.Name, target, StringComparison.Ordinal) ||
                    string.Equals(type.FullName, target, StringComparison.Ordinal))
                    return true;

                type = type.BaseType;
            }

            return false;
        }

        static string[] BuildScriptSearchScopes(ScriptIntelligenceParams p)
        {
            var scopes = new List<string>();
            foreach (var path in ExpandPaths(p))
            {
                var normalized = NormalizeAssetPath(path);
                if (string.IsNullOrEmpty(normalized))
                    continue;

                if (AssetDatabase.IsValidFolder(normalized))
                    scopes.Add(normalized);
                else if (normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    scopes.Add(Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? "Assets");
            }

            return scopes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        static IEnumerable<string> ExpandPaths(ScriptIntelligenceParams p)
        {
            if (!string.IsNullOrWhiteSpace(p.Path))
                yield return p.Path;

            if (p.Paths == null)
                yield break;

            foreach (var path in p.Paths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    yield return path;
            }
        }

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = path.Trim().Replace('\\', '/');
            if (normalized.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("unity://path/".Length);

            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return normalized;

            if (normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Packages", StringComparison.OrdinalIgnoreCase))
                return normalized;

            return "Assets/" + normalized.TrimStart('/');
        }

        static bool ShouldIncludePath(string path, bool includePackages)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.Equals("Assets", StringComparison.OrdinalIgnoreCase))
                return true;

            if (includePackages && (path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) || path.Equals("Packages", StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        static string AssetPathToAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        }

        static string ReadAssetText(string path)
        {
            var absolute = AssetPathToAbsolutePath(path);
            if (!string.IsNullOrEmpty(absolute) && File.Exists(absolute))
                return File.ReadAllText(absolute);

            var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            return textAsset == null ? null : textAsset.text;
        }

        static string ClassifyType(Type type)
        {
            if (type == null)
                return "Uncompiled";

            if (typeof(MonoBehaviour).IsAssignableFrom(type))
                return "MonoBehaviour";

            if (typeof(ScriptableObject).IsAssignableFrom(type))
                return "ScriptableObject";

            if (typeof(EditorWindow).IsAssignableFrom(type))
                return "EditorWindow";

            if (typeof(UnityEditor.Editor).IsAssignableFrom(type))
                return "Editor";

            if (typeof(Component).IsAssignableFrom(type))
                return "Component";

            return "PlainCSharp";
        }

        static object BuildFilterSummary(ScriptIntelligenceParams p)
        {
            return new
            {
                query = p.Query,
                typeName = p.TypeName,
                kind = p.Kind.ToString(),
                baseType = p.BaseType,
                includePackages = p.IncludePackages,
                includeAbstract = p.IncludeAbstract,
                paths = ExpandPaths(p).ToArray()
            };
        }

        static IEnumerable<object> CountBy<T>(IEnumerable<ScriptRecord> records, Func<ScriptRecord, T> selector)
        {
            return records
                .GroupBy(selector)
                .Select(group => new { key = group.Key?.ToString() ?? "unknown", count = group.Count() })
                .OrderByDescending(item => item.count)
                .ThenBy(item => item.key, StringComparer.OrdinalIgnoreCase)
                .Cast<object>();
        }

        static object BuildIssue(string severity, string code, ScriptRecord record, int line, string message)
        {
            return new
            {
                severity,
                code,
                path = record.Path,
                fullName = record.FullName,
                line,
                message
            };
        }

        static void AddPatternIssues(string source, ScriptRecord record, List<object> issues, string regex, string code, string message)
        {
            foreach (Match match in Regex.Matches(source, regex))
            {
                issues.Add(BuildIssue("info", code, record, LineNumberAt(source, match.Index), message));
                if (issues.Count >= 80)
                    return;
            }
        }

        static string[] SplitWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            return text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static string[] SplitBaseTypes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            return text.Split(',')
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Take(20)
                .ToArray();
        }

        static string[] ExtractAttributeNames(string attributesText)
        {
            if (string.IsNullOrWhiteSpace(attributesText))
                return Array.Empty<string>();

            return k_AttributeNameRegex.Matches(attributesText).Cast<Match>()
                .Select(match => match.Groups["name"].Value.Split('.').LastOrDefault())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        static bool IsAttributeOnlyLine(string trimmedLine)
        {
            if (string.IsNullOrWhiteSpace(trimmedLine))
                return false;

            var lastBracket = trimmedLine.LastIndexOf(']');
            return lastBracket >= 0 && string.IsNullOrWhiteSpace(trimmedLine.Substring(lastBracket + 1));
        }

        static bool LooksLikeControlStatement(Match match)
        {
            var name = match.Groups["name"].Value;
            return name is "if" or "for" or "foreach" or "while" or "switch" or "catch" or "using" or "lock";
        }

        static string DetermineVisibility(string[] modifiers)
        {
            if (modifiers.Contains("public"))
                return "public";
            if (modifiers.Contains("protected"))
                return modifiers.Contains("internal") ? "protected internal" : "protected";
            if (modifiers.Contains("internal"))
                return "internal";
            if (modifiers.Contains("private"))
                return "private";
            return "private";
        }

        static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            var count = 1;
            foreach (var ch in text)
            {
                if (ch == '\n')
                    count++;
            }

            return count;
        }

        static int LineNumberAt(string text, int index)
        {
            if (string.IsNullOrEmpty(text) || index <= 0)
                return 1;

            index = Math.Min(index, text.Length);
            var line = 1;
            for (var i = 0; i < index; i++)
            {
                if (text[i] == '\n')
                    line++;
            }

            return line;
        }

        static string[] SplitLines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        static IEnumerable<int> FindPlainMatches(string line, string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                yield break;

            var index = 0;
            while (index < line.Length)
            {
                var found = line.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    yield break;

                yield return found;
                index = found + Math.Max(1, pattern.Length);
            }
        }

        static string FirstMatch(Regex regex, string text, string groupName)
        {
            var match = regex.Match(text ?? string.Empty);
            return match.Success ? match.Groups[groupName].Value : null;
        }

        static string FirstDeclarationName(SourceAnalysis analysis)
        {
            if (analysis?.Types == null || analysis.Types.Count == 0)
                return null;

            return analysis.Types[0].Name;
        }

        static string CombineNamespaceAndName(string namespaceName, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            return string.IsNullOrWhiteSpace(namespaceName) ? name : namespaceName + "." + name;
        }

        static string FriendlyTypeName(Type type)
        {
            if (type == null)
                return null;

            if (!type.IsGenericType)
                return type.FullName ?? type.Name;

            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
                name = name.Substring(0, tick);

            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName)) + ">";
        }

        static string GetBaseTypeName(Type type)
        {
            return type?.BaseType == null ? null : FriendlyTypeName(type.BaseType);
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/');
        }

        static string NormalizeQuery(string query)
        {
            return string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        }

        static bool ContainsIgnoreCase(string value, string query)
        {
            return !string.IsNullOrEmpty(value) &&
                   !string.IsNullOrEmpty(query) &&
                   value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }

        static string NormalizeWhitespace(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return Regex.Replace(value.Trim(), @"\s+", " ");
        }

        static string TrimLine(string line)
        {
            line = line?.Trim() ?? string.Empty;
            return line.Length <= 240 ? line : line.Substring(0, 240) + "...";
        }

        static string TruncateText(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text;

            return text.Substring(0, maxChars) + "\n/* UniBridge: source truncated at " + maxChars + " characters */";
        }

        static int CountOccurrences(string text, string needle, StringComparison comparison)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle))
                return 0;

            var count = 0;
            var index = 0;
            while (index < text.Length)
            {
                var found = text.IndexOf(needle, index, comparison);
                if (found < 0)
                    break;

                count++;
                index = found + needle.Length;
            }

            return count;
        }

        static string TopFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "unknown";

            var parts = path.Replace('\\', '/').Split('/');
            if (parts.Length <= 1)
                return path;

            return parts.Length >= 2 ? parts[0] + "/" + parts[1] : parts[0];
        }

        static string[] TakeOrdered(IEnumerable<string> values, int max)
        {
            if (values == null)
                return Array.Empty<string>();

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(NormalizePath)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(max)
                .ToArray();
        }

        static string[] TakeFileNames(IEnumerable<string> paths, int max)
        {
            if (paths == null)
                return Array.Empty<string>();

            return paths
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(path => Path.GetFileName(path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(max)
                .ToArray();
        }

        static string GetAnonymousString(object item, string propertyName)
        {
            return item?.GetType().GetProperty(propertyName)?.GetValue(item) as string;
        }

        sealed class ScriptRecord
        {
            public string Path;
            public string Guid;
            public string Name;
            public MonoScript MonoScript;
            public Type Type;
            public string FullName;
            public string Namespace;
            public string TypeName;
            public string AssemblyName;
            public string Kind;
            public string AbsolutePath;
            public long SizeBytes;
            public DateTime? ModifiedUtc;
            public SourceAnalysis Analysis;
            string m_SourceText;

            public string GetSourceText()
            {
                if (m_SourceText != null)
                    return m_SourceText;

                if (!string.IsNullOrEmpty(AbsolutePath) && File.Exists(AbsolutePath))
                    m_SourceText = File.ReadAllText(AbsolutePath);
                else
                    m_SourceText = MonoScript == null ? string.Empty : ((TextAsset)MonoScript).text;

                return m_SourceText ?? string.Empty;
            }
        }

        sealed class SourceAnalysis
        {
            public string Namespace;
            public List<string> Usings = new();
            public int UsingCount;
            public int TypeCount;
            public int LineCount;
            public List<ParsedType> Types = new();
            public List<ParsedMember> Fields = new();
            public List<ParsedMember> Properties = new();
            public List<ParsedMember> Methods = new();
            public List<string> UnityMessages = new();
            public List<object> InspectorFields = new();
            public List<object> PublicMethods = new();
            public object InterfaceSummary;
        }

        sealed class ParsedMember
        {
            public string Kind;
            public string Name;
            public string Type;
            public string Visibility;
            public bool IsStatic;
            public string[] Modifiers = Array.Empty<string>();
            public string[] Attributes = Array.Empty<string>();
            public string Parameters;
            public int Line;

            public bool IsInspectorVisible => ScriptIntelligence.IsInspectorField(this);

            public string name => Name;
            public string type => Type;
            public string visibility => Visibility;
            public bool isStatic => IsStatic;
            public string[] attributes => Attributes;
            public string parameters => Parameters;
            public int line => Line;
        }

        sealed class ParsedType
        {
            public string Kind;
            public string Name;
            public string FullName;
            public string[] Modifiers = Array.Empty<string>();
            public string[] BaseTypes = Array.Empty<string>();
            public string[] Attributes = Array.Empty<string>();
            public int Line;
            public int BodyDepth;
        }
    }
}
