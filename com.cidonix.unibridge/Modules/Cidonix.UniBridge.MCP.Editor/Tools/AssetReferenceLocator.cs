using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Bounded YAML reference locator used to explain where an asset GUID is referenced.
    /// </summary>
    internal static class AssetReferenceLocator
    {
        const int DefaultLimit = 20;
        const int HardLimit = 500;

        static readonly Regex HeaderRegex = new(@"^--- !u!(?<classId>\d+) &(?<fileId>-?\d+)", RegexOptions.Compiled);
        static readonly Regex FieldRegex = new(@"^\s*(?:-\s*)?(?<name>[A-Za-z_][A-Za-z0-9_\.]*)\s*:", RegexOptions.Compiled);
        static readonly Regex GameObjectRegex = new(@"\bm_GameObject:\s*\{fileID:\s*(?<id>-?\d+)", RegexOptions.Compiled);
        static readonly Regex FatherRegex = new(@"\bm_Father:\s*\{fileID:\s*(?<id>-?\d+)", RegexOptions.Compiled);
        static readonly Regex ComponentRegex = new(@"\bcomponent:\s*\{fileID:\s*(?<id>-?\d+)", RegexOptions.Compiled);
        static readonly Regex ScriptRegex = new(@"\bm_Script:\s*\{[^}]*guid:\s*(?<guid>[0-9a-fA-F]{32})", RegexOptions.Compiled);

        public static object[] FindGuidReferences(string assetPath, string targetGuid, string targetPath, int requestedLimit)
        {
            var limit = Clamp(requestedLimit <= 0 ? DefaultLimit : requestedLimit, 1, HardLimit);
            if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(targetGuid))
                return Array.Empty<object>();

            var fullPath = AssetPathToFullPath(assetPath);
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                return Array.Empty<object>();

            string[] lines;
            try
            {
                lines = File.ReadAllLines(fullPath);
            }
            catch
            {
                return Array.Empty<object>();
            }

            var documents = ParseDocuments(lines);
            var hierarchy = HierarchyIndex.Build(documents);
            var results = new List<object>();

            for (var docIndex = 0; docIndex < documents.Count && results.Count < limit; docIndex++)
            {
                var doc = documents[docIndex];
                for (var lineIndex = doc.StartLine; lineIndex <= doc.EndLine && lineIndex < lines.Length && results.Count < limit; lineIndex++)
                {
                    var line = lines[lineIndex];
                    var column = line.IndexOf(targetGuid, StringComparison.OrdinalIgnoreCase);
                    if (column < 0)
                        continue;

                    var propertyPath = BuildPropertyPath(lines, doc.StartLine + 1, lineIndex);
                    var objectPath = ResolveObjectPath(doc, hierarchy);
                    var indexedObjectPath = ResolveIndexedObjectPath(doc, hierarchy);
                    var componentType = ResolveComponentType(doc);
                    var refPath = BuildReferencePath(objectPath, componentType, propertyPath, doc.TypeName);

                    results.Add(new
                    {
                        assetPath,
                        targetGuid,
                        targetPath,
                        line = lineIndex + 1,
                        column = column + 1,
                        refPath,
                        propertyPath,
                        yamlDocument = new
                        {
                            type = doc.TypeName,
                            classId = doc.ClassId,
                            fileId = doc.FileId
                        },
                        objectPath,
                        indexedObjectPath,
                        gameObjectName = !string.IsNullOrWhiteSpace(doc.GameObjectFileId)
                            ? hierarchy.GetName(doc.GameObjectFileId)
                            : doc.TypeName == "GameObject" ? doc.Name : null,
                        componentType,
                        scriptType = doc.ScriptType,
                        scriptAssetPath = doc.ScriptAssetPath,
                        preview = TrimPreview(line)
                    });
                }
            }

            return results.ToArray();
        }

        static List<YamlDocumentInfo> ParseDocuments(string[] lines)
        {
            var documents = new List<YamlDocumentInfo>();
            YamlDocumentInfo current = null;

            for (var i = 0; i < lines.Length; i++)
            {
                var match = HeaderRegex.Match(lines[i]);
                if (!match.Success)
                    continue;

                if (current != null)
                {
                    current.EndLine = Math.Max(current.StartLine, i - 1);
                    documents.Add(current);
                }

                current = new YamlDocumentInfo
                {
                    StartLine = i,
                    EndLine = lines.Length - 1,
                    ClassId = match.Groups["classId"].Value,
                    FileId = match.Groups["fileId"].Value
                };

                if (i + 1 < lines.Length)
                    current.TypeName = lines[i + 1].Trim().TrimEnd(':');
            }

            if (current != null)
                documents.Add(current);

            foreach (var document in documents)
                PopulateDocument(lines, document);

            return documents;
        }

        static void PopulateDocument(string[] lines, YamlDocumentInfo document)
        {
            for (var i = document.StartLine + 1; i <= document.EndLine && i < lines.Length; i++)
            {
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(document.Name))
                {
                    var name = ReadScalar(line, "m_Name");
                    if (!string.IsNullOrWhiteSpace(name))
                        document.Name = name;
                }

                if (string.IsNullOrWhiteSpace(document.GameObjectFileId))
                {
                    var goMatch = GameObjectRegex.Match(line);
                    if (goMatch.Success)
                        document.GameObjectFileId = goMatch.Groups["id"].Value;
                }

                if (string.IsNullOrWhiteSpace(document.FatherTransformFileId))
                {
                    var fatherMatch = FatherRegex.Match(line);
                    if (fatherMatch.Success)
                        document.FatherTransformFileId = fatherMatch.Groups["id"].Value;
                }

                var componentMatch = ComponentRegex.Match(line);
                if (componentMatch.Success)
                    document.ComponentFileIds.Add(componentMatch.Groups["id"].Value);

                if (string.IsNullOrWhiteSpace(document.ScriptGuid))
                {
                    var scriptMatch = ScriptRegex.Match(line);
                    if (scriptMatch.Success)
                    {
                        document.ScriptGuid = scriptMatch.Groups["guid"].Value;
                        document.ScriptAssetPath = AssetDatabase.GUIDToAssetPath(document.ScriptGuid);
                        document.ScriptType = ResolveMonoScriptTypeName(document.ScriptAssetPath);
                    }
                }
            }
        }

        static string BuildPropertyPath(string[] lines, int startLine, int targetLine)
        {
            var stack = new List<PropertyStackItem>();

            for (var i = Math.Max(0, startLine); i <= targetLine && i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("---", StringComparison.Ordinal))
                    continue;

                var fieldMatch = FieldRegex.Match(line);
                if (!fieldMatch.Success)
                    continue;

                var name = fieldMatch.Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var indent = CountIndent(line);
                while (stack.Count > 0 && stack[stack.Count - 1].Indent >= indent)
                    stack.RemoveAt(stack.Count - 1);

                stack.Add(new PropertyStackItem(indent, name));
            }

            return stack.Count == 0
                ? null
                : string.Join(".", stack.Select(item => item.Name));
        }

        static string BuildReferencePath(string objectPath, string componentType, string propertyPath, string documentType)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(objectPath))
                parts.Add(objectPath.Trim('/'));
            if (!string.IsNullOrWhiteSpace(componentType))
                parts.Add(componentType);
            else if (!string.IsNullOrWhiteSpace(documentType))
                parts.Add(documentType);
            if (!string.IsNullOrWhiteSpace(propertyPath))
                parts.Add(propertyPath);

            return parts.Count == 0 ? propertyPath : string.Join("/", parts);
        }

        static string ResolveObjectPath(YamlDocumentInfo document, HierarchyIndex hierarchy)
        {
            if (document == null)
                return null;

            if (document.TypeName == "GameObject")
                return hierarchy.GetPath(document.FileId);

            return !string.IsNullOrWhiteSpace(document.GameObjectFileId)
                ? hierarchy.GetPath(document.GameObjectFileId)
                : null;
        }

        static string ResolveIndexedObjectPath(YamlDocumentInfo document, HierarchyIndex hierarchy)
        {
            if (document == null)
                return null;

            if (document.TypeName == "GameObject")
                return hierarchy.GetIndexedPath(document.FileId);

            return !string.IsNullOrWhiteSpace(document.GameObjectFileId)
                ? hierarchy.GetIndexedPath(document.GameObjectFileId)
                : null;
        }

        static string ResolveComponentType(YamlDocumentInfo document)
        {
            if (document == null || document.TypeName == "GameObject" || document.TypeName == "PrefabInstance")
                return null;

            return !string.IsNullOrWhiteSpace(document.ScriptType)
                ? document.ScriptType
                : document.TypeName;
        }

        static string ResolveMonoScriptTypeName(string scriptAssetPath)
        {
            if (string.IsNullOrWhiteSpace(scriptAssetPath))
                return null;

            try
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptAssetPath);
                var type = script != null ? script.GetClass() : null;
                return type != null ? type.FullName : Path.GetFileNameWithoutExtension(scriptAssetPath);
            }
            catch
            {
                return Path.GetFileNameWithoutExtension(scriptAssetPath);
            }
        }

        static string ReadScalar(string line, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            var trimmed = line.TrimStart();
            var prefix = fieldName + ":";
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                return null;

            var value = trimmed.Substring(prefix.Length).Trim();
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value.Trim('"');
        }

        static int CountIndent(string line)
        {
            var count = 0;
            while (count < line.Length && line[count] == ' ')
                count++;
            return count;
        }

        static string TrimPreview(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;

            var trimmed = line.Trim();
            return trimmed.Length <= 180 ? trimmed : trimmed.Substring(0, 177) + "...";
        }

        static string AssetPathToFullPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            if (Path.IsPathRooted(assetPath))
                return assetPath;

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            return string.IsNullOrWhiteSpace(projectRoot)
                ? null
                : Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        sealed class YamlDocumentInfo
        {
            public int StartLine;
            public int EndLine;
            public string ClassId;
            public string FileId;
            public string TypeName;
            public string Name;
            public string GameObjectFileId;
            public string FatherTransformFileId;
            public string ScriptGuid;
            public string ScriptAssetPath;
            public string ScriptType;
            public readonly List<string> ComponentFileIds = new();

            public bool IsTransform =>
                string.Equals(ClassId, "4", StringComparison.Ordinal) ||
                string.Equals(ClassId, "224", StringComparison.Ordinal) ||
                string.Equals(TypeName, "Transform", StringComparison.Ordinal) ||
                string.Equals(TypeName, "RectTransform", StringComparison.Ordinal);
        }

        readonly struct PropertyStackItem
        {
            public PropertyStackItem(int indent, string name)
            {
                Indent = indent;
                Name = name;
            }

            public readonly int Indent;
            public readonly string Name;
        }

        sealed class HierarchyIndex
        {
            readonly Dictionary<string, YamlDocumentInfo> _gameObjects = new(StringComparer.Ordinal);
            readonly Dictionary<string, string> _transformByGameObject = new(StringComparer.Ordinal);
            readonly Dictionary<string, string> _gameObjectByTransform = new(StringComparer.Ordinal);
            readonly Dictionary<string, string> _parentByGameObject = new(StringComparer.Ordinal);
            readonly Dictionary<string, string> _pathCache = new(StringComparer.Ordinal);
            readonly Dictionary<string, string> _indexedPathCache = new(StringComparer.Ordinal);
            readonly Dictionary<string, int> _siblingOrdinal = new(StringComparer.Ordinal);
            readonly Dictionary<string, int> _siblingNameCounts = new(StringComparer.Ordinal);

            public static HierarchyIndex Build(IEnumerable<YamlDocumentInfo> documents)
            {
                var index = new HierarchyIndex();
                var docs = documents?.ToList() ?? new List<YamlDocumentInfo>();

                foreach (var doc in docs)
                {
                    if (doc.TypeName == "GameObject" && !string.IsNullOrWhiteSpace(doc.FileId))
                        index._gameObjects[doc.FileId] = doc;

                    if (doc.IsTransform && !string.IsNullOrWhiteSpace(doc.FileId) && !string.IsNullOrWhiteSpace(doc.GameObjectFileId))
                    {
                        index._transformByGameObject[doc.GameObjectFileId] = doc.FileId;
                        index._gameObjectByTransform[doc.FileId] = doc.GameObjectFileId;
                    }
                }

                foreach (var doc in docs.Where(item => item.IsTransform))
                {
                    if (string.IsNullOrWhiteSpace(doc.GameObjectFileId) || string.IsNullOrWhiteSpace(doc.FatherTransformFileId))
                        continue;

                    if (index._gameObjectByTransform.TryGetValue(doc.FatherTransformFileId, out var parentGameObjectId))
                        index._parentByGameObject[doc.GameObjectFileId] = parentGameObjectId;
                }

                index.BuildSiblingOrdinals();
                return index;
            }

            public string GetName(string gameObjectFileId)
            {
                return !string.IsNullOrWhiteSpace(gameObjectFileId) && _gameObjects.TryGetValue(gameObjectFileId, out var doc)
                    ? doc.Name ?? $"GameObject_{gameObjectFileId}"
                    : null;
            }

            public string GetPath(string gameObjectFileId)
            {
                return BuildPath(gameObjectFileId, indexed: false);
            }

            public string GetIndexedPath(string gameObjectFileId)
            {
                return BuildPath(gameObjectFileId, indexed: true);
            }

            string BuildPath(string gameObjectFileId, bool indexed)
            {
                if (string.IsNullOrWhiteSpace(gameObjectFileId) || !_gameObjects.ContainsKey(gameObjectFileId))
                    return null;

                var cache = indexed ? _indexedPathCache : _pathCache;
                if (cache.TryGetValue(gameObjectFileId, out var cached))
                    return cached;

                var segments = new List<string>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var current = gameObjectFileId;
                while (!string.IsNullOrWhiteSpace(current) && _gameObjects.TryGetValue(current, out var doc) && visited.Add(current))
                {
                    var name = string.IsNullOrWhiteSpace(doc.Name) ? $"GameObject_{current}" : doc.Name;
                    segments.Add(indexed ? FormatIndexedSegment(current, name) : name);
                    current = _parentByGameObject.TryGetValue(current, out var parent) ? parent : null;
                }

                segments.Reverse();
                var path = "/" + string.Join("/", segments);
                cache[gameObjectFileId] = path;
                return path;
            }

            void BuildSiblingOrdinals()
            {
                var children = _gameObjects.Keys
                    .GroupBy(id => _parentByGameObject.TryGetValue(id, out var parent) ? parent : string.Empty, StringComparer.Ordinal);

                foreach (var siblingGroup in children)
                {
                    var byName = siblingGroup
                        .GroupBy(id => GetName(id) ?? string.Empty, StringComparer.Ordinal)
                        .ToArray();

                    foreach (var nameGroup in byName)
                    {
                        var ordered = nameGroup
                            .OrderBy(id => _gameObjects[id].StartLine)
                            .ToArray();
                        var keyPrefix = siblingGroup.Key + "|" + nameGroup.Key;
                        _siblingNameCounts[keyPrefix] = ordered.Length;
                        for (var i = 0; i < ordered.Length; i++)
                            _siblingOrdinal[ordered[i]] = i;
                    }
                }
            }

            string FormatIndexedSegment(string gameObjectFileId, string name)
            {
                var parent = _parentByGameObject.TryGetValue(gameObjectFileId, out var parentId) ? parentId : string.Empty;
                var key = parent + "|" + (name ?? string.Empty);
                if (_siblingNameCounts.TryGetValue(key, out var count) && count > 1)
                {
                    var ordinal = _siblingOrdinal.TryGetValue(gameObjectFileId, out var value) ? value : 0;
                    return $"{name}[{ordinal.ToString(CultureInfo.InvariantCulture)}]";
                }

                return name;
            }
        }
    }
}
