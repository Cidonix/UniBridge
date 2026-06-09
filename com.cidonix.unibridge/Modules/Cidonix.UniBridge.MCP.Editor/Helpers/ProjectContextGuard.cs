using System;
using System.IO;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Adds project identity metadata to tool results and protects mutating calls
    /// from executing against a Unity project other than the caller's expected root.
    /// </summary>
    static class ProjectContextGuard
    {
        public const string ExpectedProjectRootParameter = "__unibridge_expected_project_root";
        const string ProjectContextProperty = "projectContext";

        static readonly string[] ExpectedRootKeys =
        {
            ExpectedProjectRootParameter,
            "ExpectedProjectRoot",
            "expectedProjectRoot",
            "expected_project_root",
            "ProjectRoot",
            "projectRoot",
            "project_root"
        };

        public static void AssertExpectedRootForPolicy(string toolName, JObject parameters, ToolExecutionPolicy policy)
        {
            if (policy is ToolExecutionPolicy.Observer or ToolExecutionPolicy.ReadOnly or ToolExecutionPolicy.Capture)
                return;

            var expectedRoot = ReadExpectedRoot(parameters);
            if (string.IsNullOrWhiteSpace(expectedRoot))
                return;

            var actualRoot = NormalizeProjectRoot(ProjectIdentity.ProjectRoot);
            var normalizedExpected = NormalizeProjectRoot(expectedRoot);

            if (string.Equals(actualRoot, normalizedExpected, StringComparison.OrdinalIgnoreCase))
                return;

            throw new InvalidOperationException(
                $"Project root mismatch for mutating UniBridge tool '{toolName}'. " +
                $"Expected '{normalizedExpected}', but this Unity Editor is attached to '{actualRoot}'. " +
                "Mutation refused to avoid changing the wrong project.");
        }

        public static object AttachProjectContext(object result)
        {
            var context = BuildProjectContext();

            if (result is JObject existingObject)
            {
                AttachContext(existingObject, context);
                return existingObject;
            }

            if (result is JToken token)
            {
                if (token is JObject tokenObject)
                {
                    AttachContext(tokenObject, context);
                    return tokenObject;
                }

                return new JObject
                {
                    ["value"] = token.DeepClone(),
                    [ProjectContextProperty] = context
                };
            }

            if (result == null)
            {
                return new JObject
                {
                    [ProjectContextProperty] = context
                };
            }

            try
            {
                var obj = JObject.FromObject(result);
                AttachContext(obj, context);
                return obj;
            }
            catch
            {
                return new JObject
                {
                    ["value"] = JToken.FromObject(result),
                    [ProjectContextProperty] = context
                };
            }
        }

        public static JObject BuildProjectContext()
        {
            var identity = ProjectIdentity.GetOrCreate();
            return new JObject
            {
                ["id"] = identity.ProjectId,
                ["name"] = identity.ProjectName,
                ["root"] = NormalizeProjectRoot(identity.ProjectRoot),
                ["assetsPath"] = NormalizePath(Application.dataPath),
                ["settingsPath"] = NormalizePath(identity.SettingsPath)
            };
        }

        static void AttachContext(JObject obj, JObject context)
        {
            if (obj[ProjectContextProperty] == null)
                obj[ProjectContextProperty] = context;
        }

        static string ReadExpectedRoot(JObject parameters)
        {
            if (parameters == null)
                return null;

            foreach (var key in ExpectedRootKeys)
            {
                var token = parameters[key];
                if (token != null && token.Type != JTokenType.Null)
                    return token.ToString();
            }

            return null;
        }

        static string NormalizeProjectRoot(string path)
        {
            var normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
                return normalized;

            if (normalized.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - "/Assets".Length);

            return normalized.TrimEnd('/');
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            var trimmed = path.Trim().Trim('"');
            try
            {
                trimmed = Path.GetFullPath(trimmed);
            }
            catch
            {
                // Keep the caller-provided value when it is not a local filesystem path.
            }

            return trimmed.Replace('\\', '/').TrimEnd('/');
        }
    }
}
