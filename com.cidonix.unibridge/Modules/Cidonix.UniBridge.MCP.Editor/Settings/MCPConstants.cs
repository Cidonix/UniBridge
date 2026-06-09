using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Cidonix.UniBridge.MCP.Editor.Settings.Utilities;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Settings
{
    /// <summary>
    /// Contains constants used throughout the UniBridge MCP package for configuration,
    /// file paths, and system integration.
    /// </summary>
    static class MCPConstants
    {
        // EditorPrefs Keys
        /// <summary>
        /// EditorPrefs key for storing MCP project settings.
        /// </summary>
        public const string prefProjectSettings = "Cidonix.UniBridge.ProjectSettings.v1";

        // Unity Paths and Namespaces
        /// <summary>
        /// Path to MCP server settings in Unity's Project Settings window.
        /// </summary>
        public const string projectSettingsPath = "Project/UniBridge/MCP";

        // Package Configuration
        /// <summary>
        /// The Unity package name for MCP integration.
        /// </summary>
        public static string packageName = "com.cidonix.unibridge";

        public static string moduleName = "Cidonix.UniBridge.MCP.Editor";

        /// <summary>
        /// Path to the package's Editor directory.
        /// </summary>
        public static string modulePath = $"Packages/{packageName}/Modules/{moduleName}";

        /// <summary>
        /// Path to the relay application directory (contains compiled binaries).
        /// </summary>
        public static string relayAppPath = $"Packages/{packageName}/RelayApp~";

        /// <summary>
        /// Path to the UI template files for settings.
        /// </summary>
        public static string uiTemplatesPath = $"{modulePath}/Settings/UI";

        // Client Configuration
        /// <summary>
        /// JSON key used to identify UniBridge in MCP client configuration files.
        /// </summary>
        public static string jsonKeyIntegration = "unibridge";

        // Relay Installation
        /// <summary>
        /// Name of the relay installation directory relative to the user's home directory.
        /// The relay binary is copied here so MCP clients can reference a stable location.
        /// </summary>
        public static string relayBaseDirectoryName = ".unibridge/relay";

        /// <summary>
        /// Windows relay executable name.
        /// </summary>
        public const string windowsRelayFileName = "unibridge_relay_win.exe";

        /// <summary>
        /// Linux relay executable name.
        /// </summary>
        public const string linuxRelayFileName = "unibridge_relay_linux";

        /// <summary>
        /// macOS x64 relay executable name.
        /// </summary>
        public const string macX64RelayFileName = "unibridge_relay_mac_x64";

        /// <summary>
        /// macOS arm64 relay executable name.
        /// </summary>
        public const string macArm64RelayFileName = "unibridge_relay_mac_arm64";

        /// <summary>
        /// Gets the relay installation directory (~/.unibridge/relay).
        /// </summary>
        public static string RelayBaseDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), relayBaseDirectoryName);

        // Status File Configuration
        /// <summary>
        /// Name of the base MCP directory relative to the user's home directory.
        /// </summary>
        public static string mcpBaseDirectoryName = ".unibridge/mcp";

        /// <summary>
        /// Subdirectory within the MCP base directory for connection status files.
        /// </summary>
        public static string connectionsSubdirectory = "connections";

        /// <summary>
        /// File pattern for locating bridge status JSON files.
        /// </summary>
        public static string statusFilePattern = "bridge-status-*.json";

        /// <summary>
        /// Environment variable name for overriding the status directory location.
        /// </summary>
        public static string statusDirEnvVar = "UNIBRIDGE_MCP_STATUS_DIR";

        /// <summary>
        /// Gets the base MCP directory (~/.unibridge/mcp)
        /// </summary>
        public static string McpBaseDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), mcpBaseDirectoryName);

        /// <summary>
        /// Gets the full path to the connections directory where bridge status files are stored.
        /// Can be overridden via UNIBRIDGE_MCP_STATUS_DIR environment variable.
        /// </summary>
        public static string StatusDirectory
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable(statusDirEnvVar);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    dir = Path.Combine(McpBaseDirectory, connectionsSubdirectory);
                }
                return dir;
            }
        }

        /// <summary>
        /// Gets the path to the relay binary installed at ~/.unibridge/relay.
        /// MCP client configurations reference this stable location.
        /// </summary>
        public static string InstalledServerMainFile
        {
            get
            {
                return Path.Combine(RelayBaseDirectory, CurrentRelayFileName);
            }
        }

        /// <summary>
        /// Gets the path to the relay binary bundled with the package (source for installation).
        /// </summary>
        internal static string BundledRelayMainFile
        {
            get
            {
                return Path.Combine(Path.GetFullPath(relayAppPath), CurrentRelayFileName);
            }
        }

        /// <summary>
        /// Gets the relay executable name for the current platform and architecture.
        /// </summary>
        internal static string CurrentRelayFileName
        {
            get
            {
                if (PlatformUtils.IsWindows)
                    return windowsRelayFileName;

                if (PlatformUtils.IsLinux)
                    return linuxRelayFileName;

                if (PlatformUtils.IsMacOS)
                {
                    return RuntimeInformation.OSArchitecture == Architecture.Arm64
                        ? macArm64RelayFileName
                        : macX64RelayFileName;
                }

                return linuxRelayFileName;
            }
        }


        /// <summary>
        /// Gets all bridge status files in the status directory, ordered by most recently modified.
        /// </summary>
        public static string[] StatusFiles =>
            Directory.GetFiles(StatusDirectory, statusFilePattern)
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToArray();

        /// <summary>
        /// Gets the path to the heartbeat file for the current Unity project.
        /// </summary>
        public static string HeartbeatFilePath =>
            Path.Combine(StatusDirectory, $"bridge-status-{ComputeProjectHash(Application.dataPath)}.json");

        /// <summary>
        /// Gets the path to the port registry file for the current Unity project.
        /// </summary>
        public static string PortRegistryFilePath =>
            Path.Combine(StatusDirectory, $"bridge-port-{ComputeProjectHash(Application.dataPath)}.json");

        /// <summary>
        /// Computes a stable hash for a project path.
        /// </summary>
        /// <param name="projectPath">The project path to hash.</param>
        /// <returns>A 16-character lowercase hexadecimal hash string.</returns>
        static string ComputeProjectHash(string projectPath)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(projectPath);
                var hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
        }
    }
}
