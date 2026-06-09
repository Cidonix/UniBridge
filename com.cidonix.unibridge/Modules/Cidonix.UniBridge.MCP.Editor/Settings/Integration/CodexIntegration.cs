using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Models;
using Cidonix.UniBridge.MCP.Editor.Settings.Utilities;

namespace Cidonix.UniBridge.MCP.Editor.Settings.Integration
{
    /// <summary>
    /// Integration for Codex (OpenAI) client, managing configuration in TOML format.
    /// </summary>
    class CodexIntegration : IClientIntegration
    {
        public McpClient Client { get; }

        public CodexIntegration(McpClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public bool Configure()
        {
            string serverPath = PathUtils.GetServerPath();
            if (string.IsNullOrEmpty(serverPath))
            {
                UpdateStatus(McpStatus.Error, "Server not found");
                return false;
            }

            string mainFile = PathUtils.GetServerMainFile(serverPath);
            if (!File.Exists(mainFile))
            {
                ServerInstaller.InstallOrUpdateRelay();
                if (!File.Exists(mainFile))
                {
                    UpdateStatus(McpStatus.Error, "Server main file not found");
                    return false;
                }
            }

            string configPath = PlatformUtils.GetConfigPathForClient(Client);
            if (string.IsNullOrEmpty(configPath))
            {
                UpdateStatus(McpStatus.Error, "Config path not available for current platform");
                return false;
            }

            bool success = WriteTomlConfig(configPath, mainFile);
            McpStatus status = success ? McpStatus.Configured : McpStatus.Error;
            string message = success ? "Successfully configured" : "Failed to update configuration";

            UpdateStatus(status, message);
            return success;
        }

        public bool Disable()
        {
            string configPath = PlatformUtils.GetConfigPathForClient(Client);
            if (string.IsNullOrEmpty(configPath))
            {
                UpdateStatus(McpStatus.Error, "Config path not available for current platform");
                return false;
            }

            bool success = RemoveTomlSection(configPath);
            McpStatus status = success ? McpStatus.NotConfigured : McpStatus.Error;
            string message = success ? "Successfully unconfigured" : "Failed to remove configuration";

            UpdateStatus(status, message);
            return success;
        }

        public void CheckConfiguration()
        {
            string configPath = PlatformUtils.GetConfigPathForClient(Client);
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            {
                UpdateStatus(McpStatus.NotConfigured, "Configuration file not found");
                return;
            }

            string content = File.ReadAllText(configPath);
            var identity = ProjectIdentity.GetOrCreate();
            var serverKey = McpServerKeyUtility.GetProjectServerKey(identity);
            var sectionHeader = McpServerKeyUtility.GetCodexTomlSection(serverKey);

            bool isConfigured = content.Contains(sectionHeader);
            McpStatus status = isConfigured ? McpStatus.Configured : McpStatus.NotConfigured;
            string message = isConfigured ? $"Configured as {serverKey}" : "Not configured";
            UpdateStatus(status, message);
        }

        public bool HasMissingDependencies(out string warningText, out string helpUrl)
        {
            warningText = string.Empty;
            helpUrl = string.Empty;
            return false;
        }

        bool WriteTomlConfig(string configPath, string mainFile)
        {
            try
            {
                // Escape backslashes for TOML strings
                string escapedPath = EscapeTomlString(mainFile);
                var identity = ProjectIdentity.GetOrCreate();
                string projectId = identity.ProjectId;
                string projectRoot = identity.ProjectRoot;
                string escapedProjectRoot = EscapeTomlString(projectRoot);
                string serverKey = McpServerKeyUtility.GetProjectServerKey(identity);
                string escapedServerKey = EscapeTomlString(serverKey);
                string sectionHeader = McpServerKeyUtility.GetCodexTomlSection(serverKey);
                string legacySectionHeader = McpServerKeyUtility.GetCodexTomlSection(McpServerKeyUtility.LegacyUniBridgeKey);

                string section = $@"

{sectionHeader}
command = ""{escapedPath}""
args = [""--mcp"", ""--project-id"", ""{projectId}"", ""--project-path"", ""{escapedProjectRoot}"", ""--name"", ""{escapedServerKey}""]
enabled = true
";

                if (File.Exists(configPath))
                {
                    string content = File.ReadAllText(configPath);

                    // Replace this project's section and clean up the old unscoped UniBridge entry.
                    content = RemoveSectionFromContent(content, sectionHeader);
                    if (legacySectionHeader != sectionHeader)
                        content = RemoveSectionFromContent(content, legacySectionHeader);

                    content = content.TrimEnd() + section;
                    File.WriteAllText(configPath, content);
                }
                else
                {
                    string directory = Path.GetDirectoryName(configPath);
                    if (!Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    File.WriteAllText(configPath, section.TrimStart());
                }

                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warning($"Failed to write Codex config: {ex.Message}");
                return false;
            }
        }

        bool RemoveTomlSection(string configPath)
        {
            try
            {
                if (!File.Exists(configPath))
                    return true;

                var identity = ProjectIdentity.GetOrCreate();
                string serverKey = McpServerKeyUtility.GetProjectServerKey(identity);
                string sectionHeader = McpServerKeyUtility.GetCodexTomlSection(serverKey);
                string content = File.ReadAllText(configPath);
                if (!content.Contains(sectionHeader))
                    return true;

                content = RemoveSectionFromContent(content, sectionHeader);
                File.WriteAllText(configPath, content);
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warning($"Failed to remove Codex config section: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes one [mcp_servers.*] section from TOML content.
        /// A section ends at the next [section] header or end of file.
        /// </summary>
        static string RemoveSectionFromContent(string content, string sectionHeader)
        {
            // Match from our section header to the next section header or end of file
            var pattern = @"\n?" + Regex.Escape(sectionHeader) + @"(?:(?!\r?\n\s*\[)[\s\S])*";
            return Regex.Replace(content, pattern, "");
        }

        static string EscapeTomlString(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }

        void UpdateStatus(McpStatus status, string message = "")
        {
            Client.SetStatus(status, message);
            MCPSettingsManager.Settings.UpdateClientState(Client.name, status, message);
            MCPSettingsManager.MarkDirty();
        }
    }
}
