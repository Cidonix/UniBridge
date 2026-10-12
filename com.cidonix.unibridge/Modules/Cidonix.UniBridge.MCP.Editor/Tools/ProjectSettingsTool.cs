#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public enum ProjectSettingsAction { List, Inspect }
    public record ProjectSettingsParams
    {
        [McpDescription("List known ProjectSettings assets or Inspect the current native SerializedObject. This tool never writes settings.", Required = false, Default = ProjectSettingsAction.List)]
        public ProjectSettingsAction Action { get; set; }
        [McpDescription("Logical settings name returned by List, for example QualitySettings, Physics2DSettings, AudioManager or PlayerSettings. Not a filesystem path.", Required = false)]
        public string Name { get; set; }
        [McpDescription("Exact canonical Unity serialized property path from a previous response, including Array.data[index] when needed. Omit to read root properties.", Required = false)]
        public string PropertyPath { get; set; }
        public int ObjectIndex { get; set; }
        public int MaxSettings { get; set; } = 64;
        public int MaxProperties { get; set; } = 160;
        public int MaxDepth { get; set; } = 3;
        public int MaxArrayElements { get; set; } = 8;
        public int MaxStringChars { get; set; } = 512;
        public int TimeoutMs { get; set; } = 2500;
        public bool IncludeValues { get; set; } = true;
    }

    public static class ProjectSettingsTool
    {
        public const string ToolName = "UniBridge_ProjectSettings";
        static readonly Regex NamePattern = new(@"^[A-Za-z][A-Za-z0-9_ -]{0,79}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25));
        static readonly Regex PropertyPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.(?:[A-Za-z_][A-Za-z0-9_]*|data\[[0-9]{1,8}\]))*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25));

        [McpTool(ToolName, "Read current native Unity ProjectSettings with typed serialized property paths and bounded array/depth/text coverage. List returns actual settings names and object counts. Inspect accepts a name returned by List; PlayerSettings is an alias for ProjectSettings. Use returned PropertyPath values for narrow drilldown. Values come from Unity's current SerializedObject, so they can differ from saved YAML. Missing settings/properties and partial coverage are explicit. This tool does not apply or save changes.", "Read Unity project settings", Groups = new[] { "core", "project", "settings", "schema" }, EnabledByDefault = true, ExecutionPolicy = ToolExecutionPolicy.ReadOnly)]
        public static object HandleCommand(ProjectSettingsParams parameters)
        {
            parameters ??= new ProjectSettingsParams();
            if (!ValidLimits(parameters)) return Response.Error("INVALID_SETTINGS_LIMIT", "Settings request limits are outside their supported range.");
            if (!Enum.IsDefined(typeof(ProjectSettingsAction), parameters.Action)) return Response.Error("INVALID_SETTINGS_ACTION");
            if (parameters.Action == ProjectSettingsAction.Inspect && (parameters.Name == null || !NamePattern.IsMatch(parameters.Name)))
                return Response.Error("INVALID_SETTINGS_NAME", "Use a logical name returned by List, not a path.");
            if (parameters.PropertyPath != null && (parameters.PropertyPath.Length > 512 || !PropertyPattern.IsMatch(parameters.PropertyPath)))
                return Response.Error("INVALID_PROPERTY_PATH", "Use the canonical Unity property path returned by Inspect.");
            var token = ToolExecutionScheduler.CurrentCancellationToken;
            var result = new SettingsReadData { action = parameters.Action.ToString(), settingsName = parameters.Name, propertyPath = parameters.PropertyPath };
            var watch = Stopwatch.StartNew();
            try
            {
                token.ThrowIfCancellationRequested();
                string root = Directory.GetParent(Application.dataPath).FullName;
                string folder = Path.Combine(root, "ProjectSettings");
                if (parameters.Action == ProjectSettingsAction.List)
                {
                    var files = new List<string>();
                    foreach (string file in Directory.EnumerateFiles(folder, "*.asset", SearchOption.TopDirectoryOnly))
                    {
                        Check(token, watch, parameters.TimeoutMs);
                        if (files.Count == 256) { result.Limit("enumeration_limit"); break; }
                        files.Add(file);
                    }
                    foreach (string file in files.OrderBy(Path.GetFileName, StringComparer.Ordinal))
                    {
                        Check(token, watch, parameters.TimeoutMs);
                        if (result.settings.Count == parameters.MaxSettings) { result.Limit("settings_limit"); break; }
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (!NamePattern.IsMatch(name) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                        var objects = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/" + Path.GetFileName(file));
                        result.settings.Add(new SettingsEntry { name = name, aliases = name == "ProjectSettings" ? new[] { "PlayerSettings" } : Array.Empty<string>(), available = objects.Length > 0,
                            path = "ProjectSettings/" + Path.GetFileName(file), objectTypes = objects.Where(x => x != null).Select(x => x.GetType().FullName).ToArray() });
                    }
                }
                else
                {
                    string name = parameters.Name == "PlayerSettings" ? "ProjectSettings" : parameters.Name;
                    string path = "ProjectSettings/" + name + ".asset";
                    string file = Path.Combine(root, path);
                    if (!File.Exists(file)) return Response.Error("SETTINGS_NOT_FOUND", "Settings asset was not found: " + name);
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return Response.Error("SETTINGS_REPARSE_PATH", "Settings asset must be a regular project file.");
                    Check(token, watch, parameters.TimeoutMs);
                    var objects = AssetDatabase.LoadAllAssetsAtPath(path);
                    if (parameters.ObjectIndex >= objects.Length || objects[parameters.ObjectIndex] == null)
                        return Response.Error("SETTINGS_OBJECT_UNAVAILABLE", "The requested settings object is unavailable; inspect List objectTypes first.");
                    InspectTarget(objects[parameters.ObjectIndex], parameters, path, token, watch, result);
                }
                result.elapsedMs = watch.Elapsed.TotalMilliseconds;
                return Response.Success(result.complete ? "Project settings read." : "Project settings read with explicitly bounded coverage.", result);
            }
            catch (MissingSettingsPropertyException) { return Response.Error("SETTINGS_PROPERTY_NOT_FOUND", parameters.PropertyPath); }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException)
            {
                result.Limit("timeout"); result.elapsedMs = watch.Elapsed.TotalMilliseconds;
                return Response.Error("SETTINGS_READ_TIMEOUT", result);
            }
            catch (Exception error) { return Response.Error("SETTINGS_READ_FAILED", error.Message); }
        }

        static bool ValidLimits(ProjectSettingsParams p) => p.ObjectIndex >= 0 && p.ObjectIndex < 64 && p.MaxSettings >= 1 && p.MaxSettings <= 64 &&
            p.MaxProperties >= 1 && p.MaxProperties <= 512 && p.MaxDepth >= 0 && p.MaxDepth <= 12 && p.MaxArrayElements >= 0 && p.MaxArrayElements <= 32 &&
            p.MaxStringChars >= 1 && p.MaxStringChars <= 4096 && p.TimeoutMs >= 100 && p.TimeoutMs <= 10000;

        internal static SettingsReadData InspectTarget(Object target, ProjectSettingsParams p, string path, CancellationToken token, Stopwatch watch, SettingsReadData result = null)
        {
            result ??= new SettingsReadData();
            result.action = "Inspect"; result.settingsName = p.Name; result.propertyPath = p.PropertyPath; result.sourcePath = path; result.objectIndex = p.ObjectIndex;
            result.objectType = target.GetType().FullName; result.objectId = UnityApiAdapter.GetObjectId(target).ToString(CultureInfo.InvariantCulture);
            using var serialized = new SerializedObject(target);
            serialized.UpdateIfRequiredOrScript();
            var reader = new Reader(p, result, token, watch);
            if (!string.IsNullOrEmpty(p.PropertyPath))
            {
                var property = serialized.FindProperty(p.PropertyPath);
                if (property == null) throw new MissingSettingsPropertyException(p.PropertyPath);
                reader.Read(property, 0);
            }
            else
            {
                var iterator = serialized.GetIterator();
                bool first = true;
                while (iterator.NextVisible(first))
                {
                    first = false;
                    if (!reader.Read(iterator, 0)) break;
                }
            }
            result.elapsedMs = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        sealed class MissingSettingsPropertyException : Exception { public MissingSettingsPropertyException(string path) : base("SETTINGS_PROPERTY_NOT_FOUND: " + path) { } }
        static void Check(CancellationToken token, Stopwatch watch, int timeout)
        { token.ThrowIfCancellationRequested(); if (watch.ElapsedMilliseconds >= timeout) throw new TimeoutException(); }

        sealed class Reader
        {
            readonly ProjectSettingsParams p; readonly SettingsReadData result; readonly CancellationToken token; readonly Stopwatch watch;
            public Reader(ProjectSettingsParams p, SettingsReadData result, CancellationToken token, Stopwatch watch) { this.p = p; this.result = result; this.token = token; this.watch = watch; }
            public bool Read(SerializedProperty property, int depth)
            {
                Check(token, watch, p.TimeoutMs);
                if (result.properties.Count >= p.MaxProperties) { result.Limit("property_limit"); return false; }
                bool array = property.isArray && property.propertyType != SerializedPropertyType.String;
                bool children = array ? property.arraySize > 0 : property.hasVisibleChildren;
                bool container = array || property.propertyType == SerializedPropertyType.Generic || property.propertyType == SerializedPropertyType.ManagedReference;
                var row = new SettingsProperty { path = property.propertyPath, type = property.propertyType.ToString(), declaredType = property.type, depth = property.depth,
                    relativeDepth = depth, isArray = array, arraySize = array ? property.arraySize : (int?)null, hasChildren = children, valueStatus = container ? "container" : p.IncludeValues ? "read" : "not_requested" };
                result.properties.Add(row);
                if (!container && p.IncludeValues)
                {
                    try
                    {
                        object value;
                        switch (property.propertyType)
                        {
                            case SerializedPropertyType.Integer:
                                value = property.type == "ulong" || property.type == "UInt64" ? (object)unchecked((ulong)property.longValue) : property.longValue; break;
                            case SerializedPropertyType.Float:
                                value = property.doubleValue;
                                if (double.IsNaN((double)value) || double.IsInfinity((double)value)) { value = ((double)value).ToString(CultureInfo.InvariantCulture); row.valueStatus = "nonfinite"; } break;
                            case SerializedPropertyType.AnimationCurve:
                            case SerializedPropertyType.Gradient:
                            case SerializedPropertyType.ManagedReference:
                            case SerializedPropertyType.Generic:
                                value = null; row.valueStatus = "unsupported"; result.Limit("unsupported_value"); break;
                            default: value = SerializedPropertyPatcher.SerializePropertyValue(property); break;
                        }
                        row.value = value == null ? JValue.CreateNull() : McpJson.TokenFromObject(value);
                        BoundText(row.value, row);
                    }
                    catch (Exception error)
                    {
                        row.valueStatus = "read_failed"; row.issue = error.GetType().Name; row.value = JValue.CreateNull(); result.Limit("value_read_failed");
                    }
                }
                if (!children) return true;
                if (depth >= p.MaxDepth) { row.childrenTruncated = true; result.Limit("depth_limit"); return true; }
                if (array)
                {
                    int count = Math.Min(property.arraySize, p.MaxArrayElements);
                    if (count < property.arraySize) { row.childrenTruncated = true; result.Limit("array_limit"); }
                    for (int i = 0; i < count; i++) if (!Read(property.GetArrayElementAtIndex(i), depth + 1)) { row.childrenTruncated = true; break; }
                }
                else
                {
                    var child = property.Copy(); var end = property.GetEndProperty(); bool first = true;
                    while (child.NextVisible(first) && !SerializedProperty.EqualContents(child, end))
                    { first = false; if (!Read(child, depth + 1)) { row.childrenTruncated = true; break; } }
                }
                return true;
            }
            void BoundText(JToken value, SettingsProperty row)
            {
                if (value is JValue scalar && scalar.Type == JTokenType.String && ((string)scalar.Value)?.Length > p.MaxStringChars)
                {
                    string text = (string)scalar.Value; int count = p.MaxStringChars;
                    if (count > 0 && char.IsHighSurrogate(text[count - 1]) && count < text.Length && char.IsLowSurrogate(text[count])) count--;
                    scalar.Value = text.Substring(0, count); row.valueStatus = "truncated"; result.Limit("text_limit");
                }
                else if (value is JContainer container) foreach (var child in container.Children()) BoundText(child is JProperty prop ? prop.Value : child, row);
            }
        }

        [McpOutputSchema(ToolName)]
        public static object GetOutputSchema()
        {
            var text = ActionOutputSchema.Nullable(ActionOutputSchema.Type("string"));
            var integer = ActionOutputSchema.Type("integer"); var boolean = ActionOutputSchema.Type("boolean");
            var property = ActionOutputSchema.Obj(("path", text), ("type", text), ("declaredType", text), ("valueStatus", text), ("issue", text),
                ("depth", integer), ("relativeDepth", integer), ("isArray", boolean), ("hasChildren", boolean), ("childrenTruncated", boolean),
                ("arraySize", ActionOutputSchema.Nullable(integer)), ("value", ActionOutputSchema.JsonValue()));
            var data = ActionOutputSchema.Obj(("schema", ActionOutputSchema.Const("unibridge.projectSettings.v1")), ("action", ActionOutputSchema.Enum("List", "Inspect")),
                ("settingsName", text), ("sourcePath", text), ("objectType", text), ("objectId", text), ("propertyPath", text), ("valueOrigin", ActionOutputSchema.Const("Unity.SerializedObject.current")),
                ("objectIndex", integer), ("complete", boolean), ("elapsedMs", ActionOutputSchema.Type("number")), ("limitations", ActionOutputSchema.Arr(ActionOutputSchema.Type("string"))),
                ("settings", ActionOutputSchema.Arr(ActionOutputSchema.DTO(typeof(SettingsEntry)))), ("properties", ActionOutputSchema.Arr(property)));
            return CoreActionOutputContracts.WithContext(ActionOutputSchema.Compose(new[] { ActionOutputSchema.Success(data), ActionOutputSchema.Error(),
                ActionOutputSchema.Error(data), ActionOutputSchema.Error(ActionOutputSchema.Type("string")) }), false);
        }
    }

    internal sealed class SettingsReadData
    {
        public string schema = "unibridge.projectSettings.v1", action, settingsName, sourcePath, objectType, objectId, propertyPath;
        public string valueOrigin = "Unity.SerializedObject.current";
        public int objectIndex;
        public bool complete = true;
        public double elapsedMs;
        public List<string> limitations = new();
        public List<SettingsEntry> settings = new();
        public List<SettingsProperty> properties = new();
        public void Limit(string reason) { complete = false; if (!limitations.Contains(reason)) limitations.Add(reason); }
    }
    internal sealed class SettingsEntry { public string name, path; public string[] aliases, objectTypes; public bool available; }
    internal sealed class SettingsProperty
    {
        public string path, type, declaredType, valueStatus, issue;
        public int depth, relativeDepth;
        public bool isArray, hasChildren, childrenTruncated;
        public int? arraySize;
        public JToken value;
    }
}
