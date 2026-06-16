#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read-only semantic diff for Unity YAML/text assets.
    /// </summary>
    internal static class AssetSemanticDiff
    {
        const int DefaultMaxChanges = 120;
        const int HardMaxChanges = 1000;
        const int DefaultMaxPropertiesPerDocument = 20;
        const int HardMaxPropertiesPerDocument = 100;
        const int DefaultMaxLineDiffs = 40;
        const int HardMaxLineDiffs = 300;
        const int DefaultMaxGuidDiffs = 80;
        const int HardMaxGuidDiffs = 500;
        const int MaxExactLineDiffLines = 1500;

        static readonly Regex k_DocumentHeader = new("^---\\s+!u!(\\d+)\\s+&(-?\\d+)", RegexOptions.Compiled);
        static readonly Regex k_PropertyLine = new("^(\\s*)(?:-\\s*)?([A-Za-z0-9_.$-]+):\\s*(.*)$", RegexOptions.Compiled);
        static readonly Regex k_GuidReference = new("guid:\\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);
        static readonly Regex k_FileIdReference = new("fileID:\\s*(-?\\d+)", RegexOptions.Compiled);

        static readonly HashSet<string> k_TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".anim", ".asset", ".asmdef", ".asmref", ".controller", ".inputactions", ".json",
            ".mat", ".meta", ".overridecontroller", ".playable", ".prefab", ".rendertexture",
            ".spriteatlas", ".unity", ".uss", ".uxml", ".yaml", ".yml", ".txt"
        };

        public static object Handle(AssetIntelligenceParams parameters)
        {
            var p = parameters ?? new AssetIntelligenceParams();
            var left = ResolveTarget(p.Path, p.Guid, p.Paths?.Length > 0 ? p.Paths[0] : null, assumeAssetRelative: true);
            var right = ResolveTarget(p.OtherPath, p.OtherGuid, p.Paths?.Length > 1 ? p.Paths[1] : null, assumeAssetRelative: true);

            if (!left.Exists || !right.Exists)
            {
                return Response.Error("SemanticDiff requires two existing text/YAML files.", new
                {
                    action = "SemanticDiff",
                    left = ToTargetDto(left),
                    right = ToTargetDto(right),
                    hint = "Pass Path and OtherPath, or pass two entries in Paths. Assets/..., Packages/..., ProjectSettings/..., absolute paths, and unity://path/... are supported."
                });
            }

            if (!IsTextLike(left.AbsolutePath) || !IsTextLike(right.AbsolutePath))
            {
                return Response.Error("SemanticDiff supports text-like Unity assets only.", new
                {
                    action = "SemanticDiff",
                    left = ToTargetDto(left),
                    right = ToTargetDto(right),
                    supportedExtensions = k_TextExtensions.OrderBy(ext => ext).ToArray()
                });
            }

            var maxChanges = Clamp(p.MaxDiffItems <= 0 ? DefaultMaxChanges : p.MaxDiffItems, 1, HardMaxChanges);
            var maxProperties = Clamp(p.MaxChangedPropertiesPerDocument <= 0 ? DefaultMaxPropertiesPerDocument : p.MaxChangedPropertiesPerDocument, 1, HardMaxPropertiesPerDocument);
            var maxGuidDiffs = Clamp(p.MaxGuidReferenceDiffs <= 0 ? DefaultMaxGuidDiffs : p.MaxGuidReferenceDiffs, 1, HardMaxGuidDiffs);
            var maxLineDiffs = Clamp(p.MaxLineDiffs <= 0 ? DefaultMaxLineDiffs : p.MaxLineDiffs, 1, HardMaxLineDiffs);

            var leftText = ReadAllText(left.AbsolutePath);
            var rightText = ReadAllText(right.AbsolutePath);
            var leftAsset = ParseAsset(left, leftText);
            var rightAsset = ParseAsset(right, rightText);
            var comparison = CompareAssets(leftAsset, rightAsset, maxChanges, maxProperties, maxGuidDiffs, p.IncludeLineDiff, maxLineDiffs);

            return Response.Success(
                $"Semantic asset diff: {comparison.CreatedDocuments} created, {comparison.DeletedDocuments} deleted, {comparison.ModifiedDocuments} modified YAML document(s).",
                new
                {
                    action = "SemanticDiff",
                    readOnly = true,
                    left = ToAssetDto(leftAsset),
                    right = ToAssetDto(rightAsset),
                    summary = comparison.Summary,
                    riskSummary = comparison.RiskSummary,
                    changedGuidReferences = comparison.GuidReferenceDiff,
                    changedScriptReferences = comparison.ScriptReferenceDiff,
                    changes = comparison.Changes,
                    lineDiff = comparison.LineDiff,
                    guidance = new[]
                    {
                        "Use this before or after manual YAML/prefab/scene/material edits to understand semantic blast radius.",
                        "Treat m_Script, prefab source, fileID, GUID, component list, transform parent, and sorting changes as review gates.",
                        "For actual project edits, use WorkSession or BatchActions and verify with ReadConsole/Domain tools afterward."
                    }
                });
        }

        static SemanticComparison CompareAssets(
            ParsedAsset left,
            ParsedAsset right,
            int maxChanges,
            int maxProperties,
            int maxGuidDiffs,
            bool includeLineDiff,
            int maxLineDiffs)
        {
            var leftDocs = ToDocumentMap(left.Documents);
            var rightDocs = ToDocumentMap(right.Documents);
            var leftKeys = new HashSet<string>(leftDocs.Keys, StringComparer.Ordinal);
            var rightKeys = new HashSet<string>(rightDocs.Keys, StringComparer.Ordinal);
            var commonKeys = leftKeys.Intersect(rightKeys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList();
            var createdKeys = rightKeys.Except(leftKeys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList();
            var deletedKeys = leftKeys.Except(rightKeys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList();

            var allChanges = new List<object>();
            var risk = new RiskAccumulator();

            foreach (var key in createdKeys)
            {
                var doc = rightDocs[key];
                risk.Add("CreatedDocument", RiskForDocument(doc, null, "Created"));
                allChanges.Add(new
                {
                    kind = "CreatedDocument",
                    document = ToDocumentSummary(doc),
                    risk = RiskForDocument(doc, null, "Created")
                });
            }

            foreach (var key in deletedKeys)
            {
                var doc = leftDocs[key];
                risk.Add("DeletedDocument", RiskForDocument(doc, null, "Deleted"));
                allChanges.Add(new
                {
                    kind = "DeletedDocument",
                    document = ToDocumentSummary(doc),
                    risk = RiskForDocument(doc, null, "Deleted")
                });
            }

            var modifiedCount = 0;
            foreach (var key in commonKeys)
            {
                var before = leftDocs[key];
                var after = rightDocs[key];
                if (string.Equals(before.Signature, after.Signature, StringComparison.Ordinal))
                    continue;

                modifiedCount++;
                var propertyDiffs = BuildPropertyDiffs(before, after, maxProperties);
                var docReferenceDiff = BuildDocumentReferenceDiff(before, after, maxGuidDiffs: 12);
                var docRisk = RiskForModifiedDocument(before, after, propertyDiffs.TotalChangedProperties, docReferenceDiff.ChangedGuidCount, docReferenceDiff.ScriptChanged);
                risk.Add("ModifiedDocument", docRisk);

                allChanges.Add(new
                {
                    kind = "ModifiedDocument",
                    documentKey = key,
                    document = new
                    {
                        before = ToDocumentSummary(before),
                        after = ToDocumentSummary(after)
                    },
                    changedPropertyCount = propertyDiffs.TotalChangedProperties,
                    changedProperties = propertyDiffs.Properties,
                    changedGuidReferences = docReferenceDiff,
                    risk = docRisk
                });
            }

            var returnedChanges = allChanges.Take(maxChanges).ToArray();
            var summary = new
            {
                leftPath = left.Target.DisplayPath,
                rightPath = right.Target.DisplayPath,
                identical = left.Sha256 == right.Sha256,
                leftSha256 = left.Sha256,
                rightSha256 = right.Sha256,
                yamlLike = left.YamlLike || right.YamlLike,
                leftDocumentCount = left.Documents.Count,
                rightDocumentCount = right.Documents.Count,
                createdDocuments = createdKeys.Count,
                deletedDocuments = deletedKeys.Count,
                modifiedDocuments = modifiedCount,
                unchangedDocuments = commonKeys.Count - modifiedCount,
                totalSemanticChanges = allChanges.Count,
                returnedSemanticChanges = returnedChanges.Length,
                truncated = allChanges.Count > returnedChanges.Length,
                changedByClass = BuildChangedByClass(createdKeys, deletedKeys, commonKeys, leftDocs, rightDocs),
                lineCounts = new
                {
                    left = left.LineCount,
                    right = right.LineCount
                }
            };

            return new SemanticComparison
            {
                CreatedDocuments = createdKeys.Count,
                DeletedDocuments = deletedKeys.Count,
                ModifiedDocuments = modifiedCount,
                Summary = summary,
                RiskSummary = risk.ToDto(),
                GuidReferenceDiff = BuildGlobalGuidReferenceDiff(left, right, maxGuidDiffs),
                ScriptReferenceDiff = BuildScriptReferenceDiff(left, right, maxGuidDiffs),
                Changes = returnedChanges,
                LineDiff = includeLineDiff ? BuildLineDiff(left, right, maxLineDiffs) : new { included = false, reason = "IncludeLineDiff=false" }
            };
        }

        static Dictionary<string, ParsedDocument> ToDocumentMap(IEnumerable<ParsedDocument> documents)
        {
            return documents
                .GroupBy(doc => doc.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }

        static object BuildChangedByClass(
            List<string> createdKeys,
            List<string> deletedKeys,
            List<string> commonKeys,
            Dictionary<string, ParsedDocument> leftDocs,
            Dictionary<string, ParsedDocument> rightDocs)
        {
            var counts = new Dictionary<string, ClassChangeCount>(StringComparer.Ordinal);
            foreach (var key in createdKeys)
                GetClassCount(counts, rightDocs[key]).Created++;
            foreach (var key in deletedKeys)
                GetClassCount(counts, leftDocs[key]).Deleted++;
            foreach (var key in commonKeys)
            {
                if (leftDocs[key].Signature != rightDocs[key].Signature)
                    GetClassCount(counts, rightDocs[key]).Modified++;
            }

            return counts.Values
                .OrderByDescending(item => item.Created + item.Deleted + item.Modified)
                .ThenBy(item => item.ClassName, StringComparer.OrdinalIgnoreCase)
                .Select(item => new
                {
                    item.ClassId,
                    item.ClassName,
                    item.Created,
                    item.Deleted,
                    item.Modified
                })
                .ToArray();
        }

        static ClassChangeCount GetClassCount(Dictionary<string, ClassChangeCount> counts, ParsedDocument doc)
        {
            var key = $"{doc.ClassId}:{doc.ClassName}";
            if (!counts.TryGetValue(key, out var item))
            {
                item = new ClassChangeCount { ClassId = doc.ClassId, ClassName = doc.ClassName };
                counts[key] = item;
            }
            return item;
        }

        static PropertyDiffResult BuildPropertyDiffs(ParsedDocument before, ParsedDocument after, int maxProperties)
        {
            var paths = new HashSet<string>(before.Properties.Keys, StringComparer.Ordinal);
            paths.UnionWith(after.Properties.Keys);

            var diffs = new List<object>();
            var total = 0;
            foreach (var path in paths.OrderBy(path => path, StringComparer.Ordinal))
            {
                before.Properties.TryGetValue(path, out var beforeProp);
                after.Properties.TryGetValue(path, out var afterProp);
                var beforeValue = beforeProp?.Value;
                var afterValue = afterProp?.Value;
                if (string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
                    continue;

                total++;
                if (diffs.Count >= maxProperties)
                    continue;

                diffs.Add(new
                {
                    path,
                    before = beforeProp == null ? null : TruncateValue(beforeValue),
                    after = afterProp == null ? null : TruncateValue(afterValue),
                    beforeLine = beforeProp?.Line,
                    afterLine = afterProp?.Line,
                    category = ClassifyPropertyPath(path)
                });
            }

            return new PropertyDiffResult { TotalChangedProperties = total, Properties = diffs.ToArray() };
        }

        static DocumentReferenceDiff BuildDocumentReferenceDiff(ParsedDocument before, ParsedDocument after, int maxGuidDiffs)
        {
            var beforeCounts = CountGuids(before.GuidReferences);
            var afterCounts = CountGuids(after.GuidReferences);
            var allGuids = new HashSet<string>(beforeCounts.Keys, StringComparer.OrdinalIgnoreCase);
            allGuids.UnionWith(afterCounts.Keys);

            var samples = new List<object>();
            var changed = 0;
            foreach (var guid in allGuids.OrderBy(guid => guid, StringComparer.OrdinalIgnoreCase))
            {
                beforeCounts.TryGetValue(guid, out var beforeCount);
                afterCounts.TryGetValue(guid, out var afterCount);
                if (beforeCount == afterCount)
                    continue;

                changed++;
                if (samples.Count >= maxGuidDiffs)
                    continue;

                samples.Add(new
                {
                    guid,
                    beforeCount,
                    afterCount,
                    assetPath = AssetDatabase.GUIDToAssetPath(guid)
                });
            }

            return new DocumentReferenceDiff
            {
                ChangedGuidCount = changed,
                Samples = samples.ToArray(),
                ScriptChanged = ScriptGuidSet(before).OrderBy(x => x).SequenceEqual(ScriptGuidSet(after).OrderBy(x => x), StringComparer.OrdinalIgnoreCase) == false
            };
        }

        static object BuildGlobalGuidReferenceDiff(ParsedAsset left, ParsedAsset right, int maxGuidDiffs)
        {
            var leftCounts = CountGuids(left.GuidReferences);
            var rightCounts = CountGuids(right.GuidReferences);
            var allGuids = new HashSet<string>(leftCounts.Keys, StringComparer.OrdinalIgnoreCase);
            allGuids.UnionWith(rightCounts.Keys);

            var changed = new List<object>();
            foreach (var guid in allGuids.OrderBy(guid => guid, StringComparer.OrdinalIgnoreCase))
            {
                leftCounts.TryGetValue(guid, out var beforeCount);
                rightCounts.TryGetValue(guid, out var afterCount);
                if (beforeCount == afterCount)
                    continue;

                changed.Add(new
                {
                    guid,
                    beforeCount,
                    afterCount,
                    delta = afterCount - beforeCount,
                    assetPath = AssetDatabase.GUIDToAssetPath(guid)
                });
            }

            return new
            {
                totalChangedGuids = changed.Count,
                returned = Math.Min(changed.Count, maxGuidDiffs),
                truncated = changed.Count > maxGuidDiffs,
                changed = changed.Take(maxGuidDiffs).ToArray()
            };
        }

        static object BuildScriptReferenceDiff(ParsedAsset left, ParsedAsset right, int maxGuidDiffs)
        {
            var leftRefs = left.GuidReferences.Where(item => item.IsScriptReference).ToList();
            var rightRefs = right.GuidReferences.Where(item => item.IsScriptReference).ToList();
            var leftCounts = CountGuids(leftRefs);
            var rightCounts = CountGuids(rightRefs);
            var allGuids = new HashSet<string>(leftCounts.Keys, StringComparer.OrdinalIgnoreCase);
            allGuids.UnionWith(rightCounts.Keys);

            var changed = new List<object>();
            foreach (var guid in allGuids.OrderBy(guid => guid, StringComparer.OrdinalIgnoreCase))
            {
                leftCounts.TryGetValue(guid, out var beforeCount);
                rightCounts.TryGetValue(guid, out var afterCount);
                if (beforeCount == afterCount)
                    continue;

                changed.Add(new
                {
                    guid,
                    beforeCount,
                    afterCount,
                    delta = afterCount - beforeCount,
                    scriptPath = AssetDatabase.GUIDToAssetPath(guid),
                    beforeLocations = leftRefs.Where(item => string.Equals(item.Guid, guid, StringComparison.OrdinalIgnoreCase)).Take(5).Select(ToReferenceLocation).ToArray(),
                    afterLocations = rightRefs.Where(item => string.Equals(item.Guid, guid, StringComparison.OrdinalIgnoreCase)).Take(5).Select(ToReferenceLocation).ToArray()
                });
            }

            return new
            {
                totalChangedScriptGuids = changed.Count,
                returned = Math.Min(changed.Count, maxGuidDiffs),
                truncated = changed.Count > maxGuidDiffs,
                changed = changed.Take(maxGuidDiffs).ToArray()
            };
        }

        static object BuildLineDiff(ParsedAsset left, ParsedAsset right, int maxLineDiffs)
        {
            if (left.Lines.Length > MaxExactLineDiffLines || right.Lines.Length > MaxExactLineDiffLines)
            {
                return new
                {
                    included = false,
                    reason = "line_count_too_large_for_exact_lcs",
                    maxExactLineDiffLines = MaxExactLineDiffLines,
                    leftLines = left.Lines.Length,
                    rightLines = right.Lines.Length,
                    hint = "Semantic document/property/reference diff is still included. Use AssetIntelligence Read chunks for exact line windows."
                };
            }

            var edits = BuildLineEdits(left.Lines, right.Lines);
            var hunks = CollapseLineEdits(edits, maxLineDiffs);
            return new
            {
                included = true,
                algorithm = "bounded_lcs",
                totalEditLines = edits.Count(edit => edit.Kind != "Equal"),
                returnedHunks = hunks.Length,
                truncated = hunks.Length >= maxLineDiffs && edits.Count(edit => edit.Kind != "Equal") > hunks.Sum(hunk => hunk.EditCount),
                hunks
            };
        }

        static List<LineEdit> BuildLineEdits(string[] left, string[] right)
        {
            var lcs = new int[left.Length + 1, right.Length + 1];
            for (var i = left.Length - 1; i >= 0; i--)
            {
                for (var j = right.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = left[i] == right[j]
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            var edits = new List<LineEdit>();
            var a = 0;
            var b = 0;
            while (a < left.Length && b < right.Length)
            {
                if (left[a] == right[b])
                {
                    edits.Add(new LineEdit("Equal", a + 1, b + 1, left[a]));
                    a++;
                    b++;
                }
                else if (lcs[a + 1, b] >= lcs[a, b + 1])
                {
                    edits.Add(new LineEdit("Delete", a + 1, null, left[a]));
                    a++;
                }
                else
                {
                    edits.Add(new LineEdit("Add", null, b + 1, right[b]));
                    b++;
                }
            }

            while (a < left.Length)
                edits.Add(new LineEdit("Delete", a + 1, null, left[a++]));
            while (b < right.Length)
                edits.Add(new LineEdit("Add", null, b + 1, right[b++]));
            return edits;
        }

        static LineDiffHunk[] CollapseLineEdits(List<LineEdit> edits, int maxHunks)
        {
            var hunks = new List<LineDiffHunk>();
            var index = 0;
            while (index < edits.Count && hunks.Count < maxHunks)
            {
                while (index < edits.Count && edits[index].Kind == "Equal")
                    index++;
                if (index >= edits.Count)
                    break;

                var start = Math.Max(0, index - 2);
                var end = index;
                while (end < edits.Count && (edits[end].Kind != "Equal" || HasNearbyEdit(edits, end, 2)))
                    end++;
                end = Math.Min(edits.Count - 1, end + 1);

                var slice = edits.Skip(start).Take(end - start + 1).ToArray();
                hunks.Add(new LineDiffHunk
                {
                    LeftStartLine = slice.Select(edit => edit.LeftLine).Where(line => line.HasValue).Select(line => line.Value).DefaultIfEmpty(0).Min(),
                    RightStartLine = slice.Select(edit => edit.RightLine).Where(line => line.HasValue).Select(line => line.Value).DefaultIfEmpty(0).Min(),
                    EditCount = slice.Count(edit => edit.Kind != "Equal"),
                    Lines = slice.Select(edit => new
                    {
                        kind = edit.Kind,
                        leftLine = edit.LeftLine,
                        rightLine = edit.RightLine,
                        text = TruncateValue(edit.Text, 220)
                    }).ToArray()
                });
                index = end + 1;
            }

            return hunks.ToArray();
        }

        static bool HasNearbyEdit(List<LineEdit> edits, int index, int window)
        {
            var end = Math.Min(edits.Count - 1, index + window);
            for (var i = index; i <= end; i++)
            {
                if (edits[i].Kind != "Equal")
                    return true;
            }
            return false;
        }

        static ParsedAsset ParseAsset(AssetTarget target, string text)
        {
            var normalized = NormalizeLineEndings(text);
            var lines = normalized.Split('\n');
            var headers = new List<DocumentHeader>();
            for (var i = 0; i < lines.Length; i++)
            {
                var match = k_DocumentHeader.Match(lines[i]);
                if (match.Success)
                {
                    headers.Add(new DocumentHeader
                    {
                        LineIndex = i,
                        ClassId = match.Groups[1].Value,
                        FileId = match.Groups[2].Value,
                        Header = lines[i]
                    });
                }
            }

            var documents = new List<ParsedDocument>();
            if (headers.Count == 0)
            {
                documents.Add(ParseDocument(target, lines, 0, lines.Length - 1, null, 0));
            }
            else
            {
                for (var i = 0; i < headers.Count; i++)
                {
                    var start = headers[i].LineIndex;
                    var end = i + 1 < headers.Count ? headers[i + 1].LineIndex - 1 : lines.Length - 1;
                    documents.Add(ParseDocument(target, lines, start, end, headers[i], i));
                }
            }

            var references = documents.SelectMany(doc => doc.GuidReferences).ToList();
            return new ParsedAsset
            {
                Target = target,
                Text = normalized,
                Lines = lines,
                LineCount = lines.Length,
                Sha256 = ComputeSha256(normalized),
                YamlLike = headers.Count > 0,
                Documents = documents,
                GuidReferences = references
            };
        }

        static ParsedDocument ParseDocument(AssetTarget target, string[] lines, int start, int end, DocumentHeader header, int ordinal)
        {
            var classId = header?.ClassId ?? "Text";
            var fileId = header?.FileId ?? "0";
            var className = ClassName(classId);
            var properties = new Dictionary<string, ParsedProperty>(StringComparer.Ordinal);
            var references = new List<GuidReference>();
            var stack = new List<StackItem>();
            string name = null;
            string gameObjectFileId = null;
            var scriptGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = start; i <= end && i < lines.Length; i++)
            {
                var line = lines[i];
                var propertyMatch = k_PropertyLine.Match(line);
                string propertyPath = null;
                if (propertyMatch.Success)
                {
                    var indent = propertyMatch.Groups[1].Value.Length;
                    var key = propertyMatch.Groups[2].Value;
                    var value = propertyMatch.Groups[3].Value.Trim();
                    while (stack.Count > 0 && stack[stack.Count - 1].Indent >= indent)
                        stack.RemoveAt(stack.Count - 1);

                    propertyPath = stack.Count > 0
                        ? string.Join(".", stack.Select(item => item.Key).Concat(new[] { key }))
                        : key;

                    var uniquePath = MakeUniquePropertyPath(properties, propertyPath);
                    properties[uniquePath] = new ParsedProperty
                    {
                        Path = uniquePath,
                        Value = value,
                        Line = i + 1
                    };

                    if (string.Equals(propertyPath, "m_Name", StringComparison.Ordinal))
                        name = CleanYamlScalar(value);
                    if (string.Equals(propertyPath, "m_GameObject", StringComparison.Ordinal))
                        gameObjectFileId = ExtractFileId(value);
                    if (string.Equals(propertyPath, "m_Script", StringComparison.Ordinal))
                    {
                        foreach (var guid in ExtractGuids(value))
                            scriptGuids.Add(guid);
                    }

                    if (string.IsNullOrWhiteSpace(value))
                        stack.Add(new StackItem { Indent = indent, Key = key });
                }

                foreach (var guidMatch in k_GuidReference.Matches(line).OfType<Match>())
                {
                    var guid = guidMatch.Groups[1].Value.ToLowerInvariant();
                    var referencedFileId = ExtractFileId(line);
                    var isScript = propertyPath != null && propertyPath.EndsWith("m_Script", StringComparison.Ordinal);
                    if (isScript)
                        scriptGuids.Add(guid);
                    references.Add(new GuidReference
                    {
                        Guid = guid,
                        FileId = referencedFileId,
                        Line = i + 1,
                        PropertyPath = propertyPath,
                        DocumentKey = $"{classId}:{header?.FileId ?? "0"}",
                        ClassId = classId,
                        ClassName = className,
                        IsScriptReference = isScript
                    });
                }
            }

            var text = string.Join("\n", lines.Skip(start).Take(Math.Max(0, end - start + 1)));
            return new ParsedDocument
            {
                Key = $"{classId}:{fileId}",
                Ordinal = ordinal,
                ClassId = classId,
                ClassName = className,
                FileId = fileId,
                Header = header?.Header,
                StartLine = start + 1,
                EndLine = Math.Max(start + 1, end + 1),
                Name = name,
                GameObjectFileId = gameObjectFileId,
                ScriptGuids = scriptGuids.ToArray(),
                Properties = properties,
                GuidReferences = references,
                Signature = ComputeSha256(text)
            };
        }

        static object ToAssetDto(ParsedAsset asset)
        {
            return new
            {
                target = ToTargetDto(asset.Target),
                sha256 = asset.Sha256,
                lineCount = asset.LineCount,
                yamlLike = asset.YamlLike,
                documentCount = asset.Documents.Count,
                documentCountByClass = asset.Documents
                    .GroupBy(doc => doc.ClassName, StringComparer.Ordinal)
                    .Select(group => new { className = group.Key, count = group.Count() })
                    .OrderByDescending(item => item.count)
                    .ThenBy(item => item.className, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                guidReferenceCount = asset.GuidReferences.Count,
                uniqueGuidReferenceCount = asset.GuidReferences.Select(item => item.Guid).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                scriptReferenceCount = asset.GuidReferences.Count(item => item.IsScriptReference)
            };
        }

        static object ToTargetDto(AssetTarget target)
        {
            return new
            {
                requested = target.Requested,
                path = target.DisplayPath,
                assetPath = target.AssetPath,
                absolutePath = target.AbsolutePath,
                guid = target.Guid,
                exists = target.Exists,
                projectAsset = target.ProjectAsset
            };
        }

        static object ToDocumentSummary(ParsedDocument doc)
        {
            return new
            {
                doc.Key,
                doc.ClassId,
                doc.ClassName,
                doc.FileId,
                doc.Name,
                doc.GameObjectFileId,
                doc.ScriptGuids,
                scriptPaths = doc.ScriptGuids.Select(guid => AssetDatabase.GUIDToAssetPath(guid)).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
                doc.StartLine,
                doc.EndLine,
                propertyCount = doc.Properties.Count,
                guidReferenceCount = doc.GuidReferences.Count
            };
        }

        static object ToReferenceLocation(GuidReference item)
        {
            return new
            {
                item.Line,
                item.PropertyPath,
                item.DocumentKey,
                item.ClassName,
                item.FileId
            };
        }

        static string[] ScriptGuidSet(ParsedDocument doc)
        {
            return doc.ScriptGuids ?? Array.Empty<string>();
        }

        static Dictionary<string, int> CountGuids(IEnumerable<GuidReference> references)
        {
            return references
                .GroupBy(item => item.Guid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        }

        static string[] ExtractGuids(string value)
        {
            return k_GuidReference.Matches(value ?? string.Empty)
                .OfType<Match>()
                .Select(match => match.Groups[1].Value.ToLowerInvariant())
                .ToArray();
        }

        static string ExtractFileId(string value)
        {
            var match = k_FileIdReference.Match(value ?? string.Empty);
            return match.Success ? match.Groups[1].Value : null;
        }

        static string MakeUniquePropertyPath(Dictionary<string, ParsedProperty> properties, string path)
        {
            if (!properties.ContainsKey(path))
                return path;

            var index = 2;
            while (properties.ContainsKey($"{path}#{index}"))
                index++;
            return $"{path}#{index}";
        }

        static string ClassifyPropertyPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "Other";
            if (path.IndexOf("m_Script", StringComparison.OrdinalIgnoreCase) >= 0)
                return "ScriptReference";
            if (path.IndexOf("guid", StringComparison.OrdinalIgnoreCase) >= 0)
                return "GuidReference";
            if (path.IndexOf("m_Component", StringComparison.OrdinalIgnoreCase) >= 0)
                return "ComponentList";
            if (path.IndexOf("m_Parent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("m_Children", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Hierarchy";
            if (path.IndexOf("m_LocalPosition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("m_LocalRotation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("m_LocalScale", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Transform";
            if (path.IndexOf("Sorting", StringComparison.OrdinalIgnoreCase) >= 0)
                return "RenderingSorting";
            if (path.IndexOf("m_Name", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Name";
            return "Other";
        }

        static string RiskForDocument(ParsedDocument doc, object _, string kind)
        {
            if (doc.ClassId == "114" || doc.ScriptGuids.Length > 0)
                return "High";
            if (doc.ClassId == "1001" || doc.ClassId == "1" || doc.ClassId == "4")
                return "Medium";
            return kind == "Deleted" ? "Medium" : "Low";
        }

        static string RiskForModifiedDocument(ParsedDocument before, ParsedDocument after, int changedProperties, int changedGuidCount, bool scriptChanged)
        {
            if (scriptChanged)
                return "High";
            if (changedGuidCount > 0)
                return "Medium";
            if (before.ClassId == "1001" || before.ClassId == "114" || after.ClassId == "114")
                return "Medium";
            if (changedProperties > 20)
                return "Medium";
            return "Low";
        }

        static string TruncateValue(string value, int max = 300)
        {
            if (value == null)
                return null;
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        static string CleanYamlScalar(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;
            value = value.Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') || (value[0] == '\'' && value[value.Length - 1] == '\'')))
                value = value.Substring(1, value.Length - 2);
            return value;
        }

        static AssetTarget ResolveTarget(string path, string guid, string fallbackPath, bool assumeAssetRelative)
        {
            var requested = FirstNonEmpty(path, guid, fallbackPath);
            if (!string.IsNullOrWhiteSpace(guid))
            {
                var guidPath = AssetDatabase.GUIDToAssetPath(guid.Trim());
                if (!string.IsNullOrWhiteSpace(guidPath))
                    return ResolveTarget(guidPath, null, null, assumeAssetRelative);
            }

            var raw = FirstNonEmpty(path, fallbackPath);
            var target = new AssetTarget { Requested = requested };
            if (string.IsNullOrWhiteSpace(raw))
                return target;

            raw = raw.Trim().Replace('\\', '/');
            const string unityPathPrefix = "unity://path/";
            if (raw.StartsWith(unityPathPrefix, StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(unityPathPrefix.Length);

            var projectRoot = GetProjectRoot().Replace('\\', '/').TrimEnd('/');
            string assetPath = null;
            string absolutePath;
            if (Path.IsPathRooted(raw))
            {
                absolutePath = Path.GetFullPath(raw);
                var normalizedAbsolute = absolutePath.Replace('\\', '/');
                if (normalizedAbsolute.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                    assetPath = normalizedAbsolute.Substring(projectRoot.Length + 1);
            }
            else
            {
                assetPath = NormalizeAssetPath(raw, assumeAssetRelative);
                absolutePath = AssetPathToAbsolutePath(assetPath);
            }

            target.AssetPath = assetPath;
            target.AbsolutePath = absolutePath;
            target.DisplayPath = !string.IsNullOrWhiteSpace(assetPath) ? assetPath : absolutePath;
            target.Exists = !string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath);
            target.ProjectAsset = !string.IsNullOrWhiteSpace(assetPath) && (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) || assetPath.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase));
            target.Guid = !string.IsNullOrWhiteSpace(assetPath) ? AssetDatabase.AssetPathToGUID(assetPath) : null;
            return target;
        }

        static string NormalizeAssetPath(string path, bool assumeAssetRelative)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            path = path.Trim().Replace('\\', '/');
            if (path.StartsWith("project://database/", StringComparison.OrdinalIgnoreCase))
                path = path.Substring("project://database/".Length);

            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
                return path;

            return assumeAssetRelative ? "Assets/" + path.TrimStart('/') : path;
        }

        static string AssetPathToAbsolutePath(string path)
        {
            path = NormalizeAssetPath(path, assumeAssetRelative: false);
            var projectRoot = GetProjectRoot();
            if (path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                var localPath = Path.GetFullPath(Path.Combine(projectRoot, path));
                if (File.Exists(localPath))
                    return localPath;

                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                if (packageInfo != null && !string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
                {
                    var prefix = "Packages/" + packageInfo.name;
                    var relativeInsidePackage = path.Length > prefix.Length
                        ? path.Substring(prefix.Length).TrimStart('/')
                        : string.Empty;
                    return Path.GetFullPath(Path.Combine(packageInfo.resolvedPath, relativeInsidePackage));
                }
            }

            return Path.GetFullPath(Path.Combine(projectRoot, path));
        }

        static string GetProjectRoot()
        {
            var assetsDirectory = new DirectoryInfo(Application.dataPath);
            return assetsDirectory.Parent?.FullName ?? Directory.GetCurrentDirectory();
        }

        static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }

        static bool IsTextLike(string absolutePath)
        {
            var extension = Path.GetExtension(absolutePath);
            return k_TextExtensions.Contains(extension);
        }

        static string ReadAllText(string absolutePath)
        {
            using var reader = new StreamReader(absolutePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        static string NormalizeLineEndings(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        static string ComputeSha256(string text)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        static string ClassName(string classId)
        {
            return classId switch
            {
                "1" => "GameObject",
                "4" => "Transform",
                "20" => "Camera",
                "21" => "Material",
                "23" => "MeshRenderer",
                "28" => "Texture2D",
                "33" => "MeshFilter",
                "48" => "Shader",
                "74" => "AnimationClip",
                "91" => "AnimatorController",
                "95" => "Animator",
                "114" => "MonoBehaviour",
                "115" => "MonoScript",
                "212" => "SpriteRenderer",
                "222" => "CanvasRenderer",
                "223" => "Canvas",
                "224" => "RectTransform",
                "225" => "CanvasGroup",
                "1001" => "PrefabInstance",
                "100100000" => "Prefab",
                "Text" => "Text",
                _ => $"Class{classId}"
            };
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        sealed class AssetTarget
        {
            public string Requested;
            public string DisplayPath;
            public string AssetPath;
            public string AbsolutePath;
            public string Guid;
            public bool Exists;
            public bool ProjectAsset;
        }

        sealed class ParsedAsset
        {
            public AssetTarget Target;
            public string Text;
            public string[] Lines;
            public int LineCount;
            public string Sha256;
            public bool YamlLike;
            public List<ParsedDocument> Documents = new();
            public List<GuidReference> GuidReferences = new();
        }

        sealed class ParsedDocument
        {
            public string Key;
            public int Ordinal;
            public string ClassId;
            public string ClassName;
            public string FileId;
            public string Header;
            public int StartLine;
            public int EndLine;
            public string Name;
            public string GameObjectFileId;
            public string[] ScriptGuids = Array.Empty<string>();
            public Dictionary<string, ParsedProperty> Properties = new(StringComparer.Ordinal);
            public List<GuidReference> GuidReferences = new();
            public string Signature;
        }

        sealed class ParsedProperty
        {
            public string Path;
            public string Value;
            public int Line;
        }

        sealed class GuidReference
        {
            public string Guid;
            public string FileId;
            public int Line;
            public string PropertyPath;
            public string DocumentKey;
            public string ClassId;
            public string ClassName;
            public bool IsScriptReference;
        }

        sealed class DocumentHeader
        {
            public int LineIndex;
            public string ClassId;
            public string FileId;
            public string Header;
        }

        sealed class StackItem
        {
            public int Indent;
            public string Key;
        }

        sealed class PropertyDiffResult
        {
            public int TotalChangedProperties;
            public object[] Properties;
        }

        sealed class DocumentReferenceDiff
        {
            public int ChangedGuidCount;
            public object[] Samples;
            public bool ScriptChanged;
        }

        sealed class SemanticComparison
        {
            public int CreatedDocuments;
            public int DeletedDocuments;
            public int ModifiedDocuments;
            public object Summary;
            public object RiskSummary;
            public object GuidReferenceDiff;
            public object ScriptReferenceDiff;
            public object[] Changes;
            public object LineDiff;
        }

        sealed class ClassChangeCount
        {
            public string ClassId;
            public string ClassName;
            public int Created;
            public int Deleted;
            public int Modified;
        }

        sealed class RiskAccumulator
        {
            readonly Dictionary<string, int> _byRisk = new(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, int> _byKind = new(StringComparer.OrdinalIgnoreCase);

            public void Add(string kind, string risk)
            {
                _byKind[kind] = _byKind.TryGetValue(kind, out var kindCount) ? kindCount + 1 : 1;
                _byRisk[risk] = _byRisk.TryGetValue(risk, out var riskCount) ? riskCount + 1 : 1;
            }

            public object ToDto()
            {
                return new
                {
                    byRisk = _byRisk.OrderByDescending(item => RiskRank(item.Key)).ThenBy(item => item.Key).Select(item => new { risk = item.Key, count = item.Value }).ToArray(),
                    byKind = _byKind.OrderByDescending(item => item.Value).ThenBy(item => item.Key).Select(item => new { kind = item.Key, count = item.Value }).ToArray(),
                    highestRisk = _byRisk.Count == 0 ? "None" : _byRisk.Keys.OrderByDescending(RiskRank).First()
                };
            }

            static int RiskRank(string risk)
            {
                return risk switch
                {
                    "High" => 3,
                    "Medium" => 2,
                    "Low" => 1,
                    _ => 0
                };
            }
        }

        sealed class LineEdit
        {
            public LineEdit(string kind, int? leftLine, int? rightLine, string text)
            {
                Kind = kind;
                LeftLine = leftLine;
                RightLine = rightLine;
                Text = text;
            }

            public string Kind;
            public int? LeftLine;
            public int? RightLine;
            public string Text;
        }

        sealed class LineDiffHunk
        {
            public int LeftStartLine;
            public int RightStartLine;
            public int EditCount;
            public object[] Lines;
        }
    }
}
