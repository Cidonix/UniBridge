#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Shared component identity helpers used by scene search and schema tools.
    /// Keeps script name, namespace, MonoScript GUID, and serialized class-id matching consistent.
    /// </summary>
    public static class ComponentIdentity
    {
        const string EditorClassIdentifierPath = "m_EditorClassIdentifier";

        public static bool MatchesAnyComponent(GameObject gameObject, string query)
        {
            if (gameObject == null || string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            var components = gameObject.GetComponents<Component>();
            foreach (var component in components)
            {
                if (Matches(component, query))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool Matches(Component component, string query)
        {
            if (component == null || string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            var normalizedQuery = NormalizeKey(query);
            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                return false;
            }

            foreach (var key in GetSearchKeys(component))
            {
                if (string.Equals(NormalizeKey(key), normalizedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static Dictionary<string, object> BuildScriptIdentity(Component component)
        {
            if (component == null)
            {
                return null;
            }

            var type = component.GetType();
            var script = GetMonoScript(component);
            var scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
            var scriptGuid = string.IsNullOrWhiteSpace(scriptPath) ? null : AssetDatabase.AssetPathToGUID(scriptPath);
            var editorClassIdentifier = GetEditorClassIdentifier(component);
            var migration = BuildNamespaceMigrationDiagnostic(type, editorClassIdentifier);

            var data = new Dictionary<string, object>
            {
                ["runtimeType"] = type.FullName,
                ["runtimeShortName"] = type.Name,
                ["runtimeNamespace"] = type.Namespace,
                ["runtimeAssembly"] = type.Assembly.GetName().Name,
                ["monoScriptName"] = script != null ? script.name : null,
                ["monoScriptPath"] = scriptPath,
                ["monoScriptGuid"] = string.IsNullOrWhiteSpace(scriptGuid) ? null : scriptGuid,
                ["serializedEditorClassIdentifier"] = string.IsNullOrWhiteSpace(editorClassIdentifier) ? null : editorClassIdentifier,
                ["namespaceMigration"] = migration
            };

            return data;
        }

        public static object BuildNamespaceMigrationDiagnostic(Component component)
        {
            if (component == null)
            {
                return null;
            }

            return BuildNamespaceMigrationDiagnostic(component.GetType(), GetEditorClassIdentifier(component));
        }

        public static string GetEditorClassIdentifier(Component component)
        {
            if (component == null)
            {
                return null;
            }

            try
            {
                var serializedObject = new SerializedObject(component);
                var property = serializedObject.FindProperty(EditorClassIdentifierPath);
                return string.IsNullOrWhiteSpace(property?.stringValue) ? null : property.stringValue.Trim();
            }
            catch
            {
                return null;
            }
        }

        static object BuildNamespaceMigrationDiagnostic(Type runtimeType, string editorClassIdentifier)
        {
            if (runtimeType == null || string.IsNullOrWhiteSpace(editorClassIdentifier))
            {
                return null;
            }

            var parsed = ParseEditorClassIdentifier(editorClassIdentifier);
            if (string.IsNullOrWhiteSpace(parsed.ClassIdentifier))
            {
                return null;
            }

            var runtimeFullName = runtimeType.FullName ?? runtimeType.Name;
            if (string.Equals(parsed.ClassIdentifier, runtimeFullName, StringComparison.Ordinal) ||
                string.Equals(parsed.ClassName, runtimeType.Name, StringComparison.Ordinal) &&
                string.Equals(parsed.ClassIdentifier, runtimeFullName, StringComparison.Ordinal))
            {
                return null;
            }

            var sameShortName = string.Equals(parsed.ClassName, runtimeType.Name, StringComparison.Ordinal);
            var serializedHadNamespace = parsed.ClassIdentifier.Contains(".");
            var runtimeHasNamespace = !string.IsNullOrWhiteSpace(runtimeType.Namespace);

            if (!sameShortName && string.Equals(parsed.ClassIdentifier, runtimeType.Name, StringComparison.Ordinal))
            {
                sameShortName = true;
            }

            if (!sameShortName && serializedHadNamespace)
            {
                return null;
            }

            if (!sameShortName && !runtimeHasNamespace)
            {
                return null;
            }

            return new
            {
                serializedClassIdentifier = editorClassIdentifier,
                serializedAssembly = parsed.AssemblyName,
                serializedClass = parsed.ClassIdentifier,
                serializedShortName = parsed.ClassName,
                resolvedRuntimeType = runtimeFullName,
                resolvedRuntimeAssembly = runtimeType.Assembly.GetName().Name,
                message = $"Serialized class id '{editorClassIdentifier}' resolves at runtime as '{runtimeFullName}'."
            };
        }

        static IEnumerable<string> GetSearchKeys(Component component)
        {
            var type = component.GetType();
            yield return type.Name;
            yield return type.FullName;
            yield return type.AssemblyQualifiedName;
            yield return type.Assembly.GetName().Name + "::" + (type.FullName ?? type.Name);
            yield return type.Assembly.GetName().Name + "::" + type.Name;

            var script = GetMonoScript(component);
            if (script != null)
            {
                yield return script.name;
                var scriptPath = AssetDatabase.GetAssetPath(script);
                if (!string.IsNullOrWhiteSpace(scriptPath))
                {
                    yield return scriptPath;
                    yield return AssetDatabase.AssetPathToGUID(scriptPath);
                }
            }

            var editorClassIdentifier = GetEditorClassIdentifier(component);
            if (!string.IsNullOrWhiteSpace(editorClassIdentifier))
            {
                yield return editorClassIdentifier;
                var parsed = ParseEditorClassIdentifier(editorClassIdentifier);
                yield return parsed.ClassIdentifier;
                yield return parsed.ClassName;
                if (!string.IsNullOrWhiteSpace(parsed.AssemblyName))
                {
                    yield return parsed.AssemblyName + "::" + parsed.ClassIdentifier;
                    yield return parsed.AssemblyName + "::" + parsed.ClassName;
                }
            }
        }

        static MonoScript GetMonoScript(Component component)
        {
            if (component is not MonoBehaviour monoBehaviour)
            {
                return null;
            }

            try
            {
                return MonoScript.FromMonoBehaviour(monoBehaviour);
            }
            catch
            {
                return null;
            }
        }

        static ParsedClassIdentifier ParseEditorClassIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return default;
            }

            var trimmed = value.Trim();
            string assembly = null;
            var classIdentifier = trimmed;
            var split = trimmed.Split(new[] { "::" }, 2, StringSplitOptions.None);
            if (split.Length == 2)
            {
                assembly = split[0];
                classIdentifier = split[1];
            }

            var className = classIdentifier;
            var dot = classIdentifier.LastIndexOf('.');
            if (dot >= 0 && dot < classIdentifier.Length - 1)
            {
                className = classIdentifier.Substring(dot + 1);
            }

            return new ParsedClassIdentifier
            {
                AssemblyName = assembly,
                ClassIdentifier = classIdentifier,
                ClassName = className
            };
        }

        static string NormalizeKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("guid:".Length).Trim();
            }

            return trimmed.Replace("-", string.Empty);
        }

        struct ParsedClassIdentifier
        {
            public string AssemblyName;
            public string ClassIdentifier;
            public string ClassName;
        }
    }
}
