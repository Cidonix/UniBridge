#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// One-call context for MonoBehaviours attached to a scene GameObject.
    /// </summary>
    public static class BehaviourContext
    {
        const string ToolName = "UniBridge_BehaviourContext";
        const int DefaultMaxSourceChars = 12000;
        const int MaxSourceCharsLimit = 60000;
        const int DefaultMaxFieldCount = 200;

        public const string Title = "Read attached MonoBehaviour source context";

        public const string Description = @"Return MonoBehaviour script paths, bounded source text, and serialized field values for a selected or target GameObject.

Use this when debugging gameplay objects: it avoids separate object inspection, script lookup, source reads, and serialized field scans. Missing scripts are reported explicitly.

Args:
    Target: Optional GameObject name, hierarchy path, or instance ID. If omitted, Selection.activeGameObject is used.
    SearchMethod: Auto, ByName, ByPath, ById, ByTag, ByLayer, or ByComponent.
    IncludeInactive: Include inactive objects during target resolution.
    IncludeSource: Defaults true.
    IncludeSerializedFields: Defaults true.
    MaxSourceChars: Bounded source characters per script.
    MaxFieldCount: Bounded serialized fields per component.

Returns:
    success, message, and data with object summary, behaviour list, script paths, source snippets, serialized fields, and missing script count.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Target = new
                    {
                        description = "GameObject target. If omitted, uses the current selected GameObject.",
                        anyOf = new object[] { new { type = "string" }, new { type = "integer" } }
                    },
                    SearchMethod = new { type = "string", description = "Target search method: Auto, ByName, ByPath, ById, ByTag, ByLayer, or ByComponent." },
                    IncludeInactive = new { type = "boolean", description = "Include inactive objects during target resolution.", @default = true },
                    IncludeSource = new { type = "boolean", description = "Include bounded source text for each MonoBehaviour script.", @default = true },
                    IncludeSerializedFields = new { type = "boolean", description = "Include serialized fields/properties for each MonoBehaviour.", @default = true },
                    MaxSourceChars = new { type = "integer", description = "Maximum source characters per script.", @default = DefaultMaxSourceChars },
                    MaxFieldCount = new { type = "integer", description = "Maximum serialized fields per component.", @default = DefaultMaxFieldCount }
                },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "scripts", "debugging" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            try
            {
                var gameObject = ResolveTarget(parameters);
                if (gameObject == null)
                    return Response.Error("Target GameObject was not found and no GameObject is selected.");

                var includeSource = GetBool(parameters, true, "IncludeSource", "includeSource", "include_source");
                var includeSerializedFields = GetBool(parameters, true, "IncludeSerializedFields", "includeSerializedFields", "include_serialized_fields");
                var maxSourceChars = Mathf.Clamp(GetInt(parameters, DefaultMaxSourceChars, "MaxSourceChars", "maxSourceChars", "max_source_chars"), 0, MaxSourceCharsLimit);
                var maxFieldCount = Mathf.Clamp(GetInt(parameters, DefaultMaxFieldCount, "MaxFieldCount", "maxFieldCount", "max_field_count"), 0, 5000);

                var behaviours = BuildBehaviourContexts(gameObject, includeSource, includeSerializedFields, maxSourceChars, maxFieldCount);
                var missing = behaviours.Count(b => b.missingScript);

                return Response.Success($"Read behaviour context for '{gameObject.name}'.", new
                {
                    gameObject = new
                    {
                        name = gameObject.name,
                        instanceId = UnityApiAdapter.GetObjectId(gameObject),
                        path = SceneObjectLocator.GetHierarchyPath(gameObject),
                        scene = gameObject.scene.IsValid() ? new { name = gameObject.scene.name, path = gameObject.scene.path } : null,
                        activeSelf = gameObject.activeSelf,
                        activeInHierarchy = gameObject.activeInHierarchy
                    },
                    behaviourCount = behaviours.Count,
                    missingScriptCount = missing,
                    behaviours
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BehaviourContext] Failed: {ex}");
                return Response.Error($"Behaviour context failed: {ex.Message}");
            }
        }

        static GameObject ResolveTarget(JObject parameters)
        {
            var target = GetToken(parameters, "Target", "target", "GameObject", "gameObject", "game_object");
            if (target == null || target.Type == JTokenType.Null || string.IsNullOrWhiteSpace(target.ToString()))
                return Selection.activeGameObject;

            var searchMethod = GetString(parameters, "SearchMethod", "searchMethod", "search_method", "Method", "method");
            var options = new SceneObjectLocator.Options
            {
                IncludeInactive = GetBool(parameters, true, "IncludeInactive", "includeInactive", "include_inactive", "SearchInactive", "searchInactive", "search_inactive"),
                IncludePrefabStage = true
            };
            return SceneObjectLocator.FindObject(target.ToString(), searchMethod, options);
        }

        static List<BehaviourEntry> BuildBehaviourContexts(GameObject gameObject, bool includeSource, bool includeSerializedFields, int maxSourceChars, int maxFieldCount)
        {
            var entries = new List<BehaviourEntry>();
            var components = gameObject.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                {
                    entries.Add(new BehaviourEntry
                    {
                        index = i,
                        missingScript = true,
                        type = "<missing MonoBehaviour>"
                    });
                    continue;
                }

                if (component is not MonoBehaviour monoBehaviour)
                    continue;

                var type = monoBehaviour.GetType();
                var script = MonoScript.FromMonoBehaviour(monoBehaviour);
                var path = script != null ? AssetDatabase.GetAssetPath(script) : null;
                var entry = new BehaviourEntry
                {
                    index = i,
                    missingScript = false,
                    type = type.FullName,
                    assembly = type.Assembly.GetName().Name,
                    scriptPath = path,
                    scriptGuid = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.AssetPathToGUID(path),
                    enabled = monoBehaviour.enabled
                };

                if (includeSerializedFields)
                    entry.serializedFields = ReadSerializedFields(monoBehaviour, maxFieldCount);

                if (includeSource && !string.IsNullOrWhiteSpace(path))
                    entry.source = ReadSource(path, maxSourceChars);

                entries.Add(entry);
            }

            return entries;
        }

        static List<object> ReadSerializedFields(MonoBehaviour behaviour, int maxFieldCount)
        {
            var fields = new List<object>();
            if (maxFieldCount <= 0)
                return fields;

            var serializedObject = new SerializedObject(behaviour);
            var iterator = serializedObject.GetIterator();
            if (!iterator.NextVisible(true))
                return fields;

            do
            {
                if (iterator.propertyPath == "m_Script")
                    continue;

                fields.Add(new
                {
                    path = iterator.propertyPath,
                    name = iterator.name,
                    displayName = iterator.displayName,
                    type = iterator.propertyType.ToString(),
                    value = SerializePropertyValue(iterator)
                });
            }
            while (fields.Count < maxFieldCount && iterator.NextVisible(false));

            return fields;
        }

        static object SerializePropertyValue(SerializedProperty property)
        {
            try
            {
                return SerializedPropertyPatcher.SerializePropertyValue(property);
            }
            catch (Exception ex)
            {
                return new
                {
                    error = "SerializedProperty value serialization failed.",
                    propertyType = property.propertyType.ToString(),
                    ex.Message
                };
            }
        }

        static object SerializeVector2(Vector2 value) => new { x = value.x, y = value.y };

        static object SerializeVector3(Vector3 value) => new { x = value.x, y = value.y, z = value.z };

        static object SerializeVector4(Vector4 value) => new { x = value.x, y = value.y, z = value.z, w = value.w };

        static object SerializeVector2Int(Vector2Int value) => new { x = value.x, y = value.y };

        static object SerializeVector3Int(Vector3Int value) => new { x = value.x, y = value.y, z = value.z };

        static object SerializeColor(Color value) => new { r = value.r, g = value.g, b = value.b, a = value.a };

        static object SerializeRect(Rect value) => new { x = value.x, y = value.y, width = value.width, height = value.height };

        static object SerializeRectInt(RectInt value) => new { x = value.x, y = value.y, width = value.width, height = value.height };

        static object SerializeBounds(Bounds value) => new { center = SerializeVector3(value.center), size = SerializeVector3(value.size), min = SerializeVector3(value.min), max = SerializeVector3(value.max) };

        static object SerializeBoundsInt(BoundsInt value) => new { position = SerializeVector3Int(value.position), size = SerializeVector3Int(value.size), min = SerializeVector3Int(value.min), max = SerializeVector3Int(value.max) };

        static object ReadSource(string assetPath, int maxChars)
        {
            var fullPath = Path.GetFullPath(assetPath);
            if (!File.Exists(fullPath))
                fullPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), assetPath));
            if (!File.Exists(fullPath))
                return new { included = false, reason = "File not found.", assetPath };

            var text = File.ReadAllText(fullPath);
            var truncated = maxChars > 0 && text.Length > maxChars;
            var content = maxChars <= 0
                ? string.Empty
                : truncated
                    ? text.Substring(0, maxChars)
                    : text;
            return new
            {
                included = maxChars > 0,
                assetPath,
                fullPath,
                length = text.Length,
                truncated,
                content
            };
        }

        static JToken GetToken(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
                    return token;
            }

            return null;
        }

        static string GetString(JObject obj, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString().Trim();
        }

        static bool GetBool(JObject obj, bool defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && bool.TryParse(token.ToString(), out var value) ? value : defaultValue;
        }

        static int GetInt(JObject obj, int defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && int.TryParse(token.ToString(), out var value) ? value : defaultValue;
        }

        sealed class BehaviourEntry
        {
            public int index;
            public bool missingScript;
            public string type;
            public string assembly;
            public string scriptPath;
            public string scriptGuid;
            public bool? enabled;
            public List<object> serializedFields;
            public object source;
        }
    }
}
