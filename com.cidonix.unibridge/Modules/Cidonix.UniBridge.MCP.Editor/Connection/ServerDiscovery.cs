using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Cidonix.UniBridge.MCP.Editor.Settings;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Manages connection discovery files for UniBridge MCP Bridge.
    /// Replaces TCP port-based discovery with named pipe/Unix socket path discovery.
    /// </summary>
    static class ServerDiscovery
    {
        /// <summary>
        /// Contains information about the MCP Bridge connection for discovery by MCP servers.
        /// </summary>
        [Serializable]
        class ConnectionInfo
        {
            /// <summary>
            /// Type of connection used ("named_pipe" or "unix_socket").
            /// </summary>
            public string connection_type; // "named_pipe" or "unix_socket"

            /// <summary>
            /// Full path to the named pipe or Unix socket.
            /// </summary>
            public string connection_path; // Full path to pipe or socket

            /// <summary>
            /// ISO 8601 timestamp of when the connection was created.
            /// </summary>
            public string created_date;

            /// <summary>
            /// Full path to the Unity project's Assets folder.
            /// </summary>
            public string project_path;

            /// <summary>
            /// Stable UniBridge project ID from ProjectSettings/UniBridge/project.json.
            /// </summary>
            public string project_id;

            /// <summary>
            /// Human-readable Unity project name.
            /// </summary>
            public string project_name;

            /// <summary>
            /// Full path to the Unity project root.
            /// </summary>
            public string project_root;

            /// <summary>
            /// Version of the MCP protocol being used.
            /// </summary>
            public string protocol_version;

            /// <summary>
            /// Process ID of the Unity Editor instance.
            /// Allows distinguishing multiple instances of the same project.
            /// </summary>
            public int editor_pid;
        }

        static readonly int s_EditorPid = Process.GetCurrentProcess().Id;

        /// <summary>
        /// Get connection path for the current Unity project.
        /// Includes the editor PID so multiple instances of the same project get separate pipes.
        /// </summary>
        /// <returns>Platform-specific connection path</returns>
        public static string GetConnectionPath()
        {
            string projectHash = ComputeProjectHash(Application.dataPath);
            return GenerateConnectionPath(projectHash, s_EditorPid);
        }

        /// <summary>
        /// Generate platform-specific connection path from project hash and editor PID.
        /// </summary>
        static string GenerateConnectionPath(string projectHash, int pid)
        {
            if (IsWindows())
            {
                // Windows: Named pipe
                // Format: \\.\pipe\unity-mcp-{hash}-{pid}
                return $"\\\\.\\pipe\\unity-mcp-{projectHash}-{pid}";
            }
            else
            {
                // Mac/Linux: Unix domain socket in an app-owned temp directory.
                // Keep the path short because sockaddr_un has a small platform limit.
                return Path.Combine("/tmp", "unibridge-mcp", $"unity-mcp-{projectHash}-{pid}.sock");
            }
        }

        /// <summary>
        /// Save connection info to discovery file
        /// </summary>
        /// <param name="connectionPath">Platform-specific connection path to the named pipe or Unix socket.</param>
        public static void SaveConnectionInfo(string connectionPath)
        {
            try
            {
                var projectIdentity = ProjectIdentity.GetOrCreate();
                var connectionInfo = new ConnectionInfo
                {
                    connection_type = IsWindows() ? "named_pipe" : "unix_socket",
                    connection_path = connectionPath,
                    created_date = DateTime.UtcNow.ToString("O"),
                    project_path = Application.dataPath,
                    project_id = projectIdentity.ProjectId,
                    project_name = projectIdentity.ProjectName,
                    project_root = projectIdentity.ProjectRoot,
                    protocol_version = "2.0",
                    editor_pid = s_EditorPid
                };

                string registryDir = GetRegistryDirectory();
                Directory.CreateDirectory(registryDir);

                string registryFile = GetRegistryFilePath();
                string json = JsonConvert.SerializeObject(connectionInfo, Formatting.Indented);
                File.WriteAllText(registryFile, json, new UTF8Encoding(false));

                #if UNITY_EDITOR_LINUX
                // Ubuntu CI: Ensure file is flushed to disk before MCP server tries to read it
                // File system operations can be slower on CI environments
                try
                {
                    using (var fs = new FileStream(registryFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.Flush(true); // Flush OS buffers to disk
                    }
                    McpLog.Log("[Ubuntu] Flushed registry file to disk");
                }
                catch (Exception flushEx)
                {
                    McpLog.Warning($"Could not flush registry file: {flushEx.Message}");
                }
                #endif

                McpLog.Log($"Saved connection info to {registryFile}");
            }
            catch (Exception ex)
            {
                McpLog.Warning($"Could not save connection info: {ex.Message}");
            }
        }

        /// <summary>
        /// Delete discovery files on shutdown.
        /// </summary>
        public static void DeleteDiscoveryFiles()
        {
            try
            {
                string registryFile = GetRegistryFilePath();
                if (File.Exists(registryFile))
                    File.Delete(registryFile);

                string statusFile = GetStatusFilePath();
                if (File.Exists(statusFile))
                    File.Delete(statusFile);

                McpLog.Log("Deleted discovery files");
            }
            catch (Exception ex)
            {
                McpLog.Warning($"Could not delete discovery files: {ex.Message}");
            }
        }

        static string GetRegistryDirectory()
        {
            return MCPConstants.StatusDirectory;
        }

        static string GetRegistryFilePath()
        {
            string dir = GetRegistryDirectory();
            string hash = ComputeProjectHash(Application.dataPath);
            string fileName = $"bridge-{hash}-{s_EditorPid}.json";
            return Path.Combine(dir, fileName);
        }

        static string GetStatusFilePath()
        {
            string dir = GetRegistryDirectory();
            string hash = ComputeProjectHash(Application.dataPath);
            string fileName = $"bridge-status-{hash}-{s_EditorPid}.json";
            return Path.Combine(dir, fileName);
        }

        static string ComputeProjectHash(string input)
        {
            try
            {
                using (SHA1 sha1 = SHA1.Create())
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
                    byte[] hashBytes = sha1.ComputeHash(bytes);
                    var sb = new StringBuilder();
                    foreach (byte b in hashBytes)
                    {
                        sb.Append(b.ToString("x2"));
                    }
                    return sb.ToString().Substring(0, 8); // short, sufficient for uniqueness
                }
            }
            catch
            {
                return "default";
            }
        }

        static bool IsWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }
    }
}
