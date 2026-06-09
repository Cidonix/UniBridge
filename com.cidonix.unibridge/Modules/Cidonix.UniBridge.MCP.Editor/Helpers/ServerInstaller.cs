using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Settings;
using Cidonix.UniBridge.MCP.Editor.Settings.Utilities;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Copies the relay binary from the package's RelayApp~ directory to ~/.unibridge/relay/
    /// so that MCP clients can reference a stable, well-known executable location.
    /// Runs automatically when the editor domain loads. The session guard is tied to
    /// the bundled relay identity (version + binary hash), so replacing the package
    /// inside an already-open Unity project can still trigger a relay update.
    /// </summary>
    [InitializeOnLoad]
    static class ServerInstaller
    {
        const string k_SessionStateKey = "ServerInstaller.CheckedRelayIdentityThisSession.v1";
        const string k_FallbackVersion = "0.0.0";

        static ServerInstaller()
        {
            string bundledIdentity = GetBundledRelayIdentity();
            bool installedRelayExists = File.Exists(MCPConstants.InstalledServerMainFile);

            if (installedRelayExists &&
                string.Equals(SessionState.GetString(k_SessionStateKey, string.Empty), bundledIdentity, StringComparison.Ordinal))
                return;

            if (InstallOrUpdateRelay())
                SessionState.SetString(k_SessionStateKey, bundledIdentity);
        }

        internal static bool InstallOrUpdateRelay()
        {
            try
            {
                string sourceDir = Path.GetFullPath(MCPConstants.relayAppPath);
                if (!Directory.Exists(sourceDir))
                {
                    McpLog.Warning($"Relay app directory not found at {sourceDir}");
                    return false;
                }

                string targetDir = MCPConstants.RelayBaseDirectory;
                string bundledVersion = ReadBundledVersion(Path.Combine(sourceDir, "relay.json"));
                string installedVersion = ReadInstalledVersion();

                if (!ShouldInstallRelay(bundledVersion, installedVersion))
                {
                    McpLog.Log($"Relay is up to date (bundled: {bundledVersion}, installed: {installedVersion})");
                    return true;
                }

                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                CopyRelayFiles(sourceDir, targetDir);

                McpLog.Log($"Relay installed to {targetDir} (version {bundledVersion})");
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warning($"Could not install relay: {ex.Message}");
                return false;
            }
        }

        static string GetBundledRelayIdentity()
        {
            try
            {
                string sourceDir = Path.GetFullPath(MCPConstants.relayAppPath);
                string version = ReadBundledVersion(Path.Combine(sourceDir, "relay.json"));
                string binaryHash = ComputeFileSha256(MCPConstants.BundledRelayMainFile);
                return $"{version}|{binaryHash}";
            }
            catch
            {
                return $"{k_FallbackVersion}|unknown";
            }
        }

        static string ReadBundledVersion(string relayJsonPath)
        {
            try
            {
                if (!File.Exists(relayJsonPath))
                    return k_FallbackVersion;

                string json = File.ReadAllText(relayJsonPath);
                var jsonObj = JObject.Parse(json);
                return jsonObj["version"]?.ToString() ?? k_FallbackVersion;
            }
            catch
            {
                return k_FallbackVersion;
            }
        }

        static string ReadInstalledVersion()
        {
            try
            {
                string binaryPath = MCPConstants.InstalledServerMainFile;
                if (!File.Exists(binaryPath))
                    return k_FallbackVersion;

                var result = ProcessUtils.Execute(binaryPath, "--version", timeoutMs: 5000);
                if (!result.Success || string.IsNullOrEmpty(result.Output))
                    return k_FallbackVersion;

                return ParseVersionFromOutput(result.Output);
            }
            catch
            {
                return k_FallbackVersion;
            }
        }

        static string ParseVersionFromOutput(string output)
        {
            const string prefix = "Version: ";
            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(prefix.Length).Trim();
            }
            return k_FallbackVersion;
        }

        static bool IsNewerVersion(string packageVersion, string installedVersion)
        {
            try
            {
                var pkgBase = new Version(CleanVersion(packageVersion));
                var instBase = new Version(CleanVersion(installedVersion));

                int cmp = pkgBase.CompareTo(instBase);
                if (cmp != 0)
                    return cmp > 0;

                // Base versions equal — compare build numbers from pre-release tag
                return ExtractBuildNumber(packageVersion) > ExtractBuildNumber(installedVersion);
            }
            catch
            {
                return true;
            }
        }

        static bool ShouldInstallRelay(string bundledVersion, string installedVersion)
        {
            string installedBinaryPath = MCPConstants.InstalledServerMainFile;
            if (!File.Exists(installedBinaryPath))
                return true;

            if (IsNewerVersion(bundledVersion, installedVersion))
                return true;

            if (!string.Equals(bundledVersion, installedVersion, StringComparison.OrdinalIgnoreCase))
                return false;

            string bundledHash = ComputeFileSha256(MCPConstants.BundledRelayMainFile);
            string installedHash = ComputeFileSha256(installedBinaryPath);
            return !string.IsNullOrEmpty(bundledHash) &&
                   !string.IsNullOrEmpty(installedHash) &&
                   !string.Equals(bundledHash, installedHash, StringComparison.OrdinalIgnoreCase);
        }

        static string ComputeFileSha256(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return string.Empty;

                using var stream = File.OpenRead(filePath);
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                byte[] hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        static int ExtractBuildNumber(string version)
        {
            // Parse "X.Y.Z-build.N" → N, or 0 if no tag
            int dashIndex = version.IndexOf('-');
            if (dashIndex < 0) return 0;

            string tag = version.Substring(dashIndex + 1);
            int lastDot = tag.LastIndexOf('.');
            if (lastDot >= 0 && int.TryParse(tag.Substring(lastDot + 1), out int n))
                return n;

            return 0;
        }

        static string CleanVersion(string version)
        {
            int dashIndex = version.IndexOf('-');
            return dashIndex >= 0 ? version.Substring(0, dashIndex) : version;
        }

        static void CopyRelayFiles(string sourceDir, string targetDir)
        {
            // Clean up .old files from previous rename-on-locked-binary operations
            CleanupOldFiles(targetDir);

            string relayFileName = MCPConstants.CurrentRelayFileName;
            string targetPath = Path.Combine(targetDir, relayFileName);
            CopyToTargetDir(Path.Combine(sourceDir, relayFileName));

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(targetPath))
            {
                SetExecutable(targetPath);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    ClearMacQuarantine(targetPath);
            }

            CleanupLegacyRelayFiles(targetDir);

            void CopyToTargetDir(string path)
            {
                if (!File.Exists(path))
                {
                    McpLog.Warning($"Failed to copy file {path} to targetDir {targetDir} because original file does not exist");
                    return;
                }

                string fileName = Path.GetFileName(path);
                string targetPath = Path.Combine(targetDir, fileName);

                try
                {
                    File.Copy(path, targetPath, true);
                }
                catch (IOException) when (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Binary is likely locked by a running relay process.
                    // Windows allows renaming a running exe, so rename it out of the way and copy the new one.
                    try
                    {
                        string oldPath = targetPath + ".old";
                        if (File.Exists(oldPath))
                            File.Delete(oldPath);
                        File.Move(targetPath, oldPath);
                        File.Copy(path, targetPath, true);
                    }
                    catch (IOException)
                    {
                        string processName = Path.GetFileNameWithoutExtension(fileName);
                        var pids = System.Diagnostics.Process.GetProcessesByName(processName).Select(p => p.Id);
                        McpLog.Warning($"Cannot update relay binary at {targetPath}: file is locked by process(es) [{string.Join(", ", pids)}]. Will retry next editor session.");
                        throw;
                    }
                }
            }
        }

        static void CleanupOldFiles(string targetDir)
        {
            try
            {
                foreach (string oldFile in Directory.GetFiles(targetDir, "*.old"))
                {
                    try { File.Delete(oldFile); }
                    catch { /* Still in use, ignore */ }
                }
            }
            catch
            {
                // Target directory may not exist yet
            }
        }

        static void CleanupLegacyRelayFiles(string targetDir)
        {
            string[] legacyFiles =
            {
                "relay_win.exe",
                "relay_linux",
                "relay_mac_x64",
                "relay_mac_arm64"
            };

            foreach (string fileName in legacyFiles)
            {
                try
                {
                    string legacyPath = Path.Combine(targetDir, fileName);
                    if (File.Exists(legacyPath))
                        File.Delete(legacyPath);
                }
                catch
                {
                    // A running old relay can keep a legacy executable locked. The new relay path is already used.
                }
            }

            string[] legacyDirectories =
            {
                "relay_mac_x64.app",
                "relay_mac_arm64.app"
            };

            foreach (string directoryName in legacyDirectories)
            {
                try
                {
                    string legacyDirectory = Path.GetFullPath(Path.Combine(targetDir, directoryName));
                    string relayDirectory = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string requiredPrefix = relayDirectory + Path.DirectorySeparatorChar;

                    if (!legacyDirectory.StartsWith(requiredPrefix, StringComparison.Ordinal) ||
                        !Directory.Exists(legacyDirectory))
                        continue;

                    Directory.Delete(legacyDirectory, recursive: true);
                }
                catch
                {
                    // Ignore stale app bundles that are locked or already gone.
                }
            }
        }

        static void SetExecutable(string filePath)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(startInfo);
                process?.WaitForExit(5000);
            }
            catch
            {
                // chmod not available on this platform
            }
        }

        static void ClearMacQuarantine(string filePath)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xattr",
                    Arguments = $"-d com.apple.quarantine \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(startInfo);
                process?.WaitForExit(5000);
            }
            catch
            {
                // xattr may be unavailable, or the file may not carry a quarantine attribute.
            }
        }
    }
}
