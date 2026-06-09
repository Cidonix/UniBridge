using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Provides a stable per-Unity-project identity that survives project moves.
    /// </summary>
    static class ProjectIdentity
    {
        const int k_SchemaVersion = 1;
        const string k_SettingsDirectoryName = "UniBridge";
        const string k_SettingsFileName = "project.json";

        [Serializable]
        class ProjectIdentityFile
        {
            public int schema_version = k_SchemaVersion;
            public string project_id;
            public string project_name;
            public string created_date;
            public string updated_date;
        }

        public class Snapshot
        {
            public string ProjectId;
            public string ProjectName;
            public string ProjectRoot;
            public string SettingsPath;
        }

        public static string ProjectRoot
        {
            get
            {
                try
                {
                    var assetsDir = new DirectoryInfo(Application.dataPath);
                    return assetsDir.Parent?.FullName ?? Application.dataPath;
                }
                catch
                {
                    return Application.dataPath;
                }
            }
        }

        public static string SettingsPath =>
            Path.Combine(ProjectRoot, "ProjectSettings", k_SettingsDirectoryName, k_SettingsFileName);

        public static Snapshot GetOrCreate()
        {
            var settingsPath = SettingsPath;
            var projectRoot = ProjectRoot;
            var defaultName = GetDefaultProjectName(projectRoot);

            if (File.Exists(settingsPath))
            {
                try
                {
                    var file = JsonConvert.DeserializeObject<ProjectIdentityFile>(File.ReadAllText(settingsPath));
                    if (file != null && TryNormalizeProjectId(file.project_id, out var normalizedId))
                    {
                        var projectName = string.IsNullOrWhiteSpace(file.project_name)
                            ? defaultName
                            : file.project_name.Trim();

                        if (file.schema_version != k_SchemaVersion ||
                            file.project_id != normalizedId ||
                            file.project_name != projectName ||
                            string.IsNullOrWhiteSpace(file.created_date))
                        {
                            Write(settingsPath, normalizedId, projectName, file.created_date);
                        }

                        return CreateSnapshot(normalizedId, projectName, projectRoot, settingsPath);
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Warning($"Could not read UniBridge project identity: {ex.Message}");
                }
            }

            var snapshot = Write(settingsPath, Guid.NewGuid().ToString("N"), defaultName, null);
            McpLog.Log($"Created UniBridge project identity: {snapshot.ProjectId}");
            return snapshot;
        }

        public static Snapshot Regenerate()
        {
            var snapshot = Write(SettingsPath, Guid.NewGuid().ToString("N"), GetDefaultProjectName(ProjectRoot), null);
            McpLog.Log($"Regenerated UniBridge project identity: {snapshot.ProjectId}");
            return snapshot;
        }

        static Snapshot Write(string settingsPath, string projectId, string projectName, string createdDate)
        {
            var now = DateTime.UtcNow.ToString("O");
            var directory = Path.GetDirectoryName(settingsPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var file = new ProjectIdentityFile
            {
                schema_version = k_SchemaVersion,
                project_id = projectId,
                project_name = projectName,
                created_date = string.IsNullOrWhiteSpace(createdDate) ? now : createdDate,
                updated_date = now
            };

            var json = JsonConvert.SerializeObject(file, Formatting.Indented);
            File.WriteAllText(settingsPath, json, new UTF8Encoding(false));
            return CreateSnapshot(projectId, projectName, ProjectRoot, settingsPath);
        }

        static Snapshot CreateSnapshot(string projectId, string projectName, string projectRoot, string settingsPath)
        {
            return new Snapshot
            {
                ProjectId = projectId,
                ProjectName = projectName,
                ProjectRoot = projectRoot,
                SettingsPath = settingsPath
            };
        }

        static string GetDefaultProjectName(string projectRoot)
        {
            try
            {
                var name = new DirectoryInfo(projectRoot).Name;
                return string.IsNullOrWhiteSpace(name) ? "Unity Project" : name;
            }
            catch
            {
                return "Unity Project";
            }
        }

        static bool TryNormalizeProjectId(string value, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var trimmed = value.Trim();
            if (Guid.TryParse(trimmed, out var guid))
            {
                normalized = guid.ToString("N");
                return true;
            }

            var compact = trimmed.Replace("-", string.Empty);
            if (compact.Length == 32 && compact.All(IsHex))
            {
                normalized = compact.ToLowerInvariant();
                return true;
            }

            return false;
        }

        static bool IsHex(char c) =>
            c >= '0' && c <= '9' ||
            c >= 'a' && c <= 'f' ||
            c >= 'A' && c <= 'F';
    }
}
