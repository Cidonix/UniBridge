using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Helper utilities for script refresh and compilation operations.
    /// </summary>
    static class ScriptRefreshHelpers
    {
        /// <summary>
        /// Sanitizes and normalizes an asset path to ensure it's valid for Unity.
        /// </summary>
        /// <param name="p">The path to sanitize</param>
        /// <returns>A normalized Assets-relative path</returns>
        public static string SanitizeAssetsPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            p = p.Replace('\\', '/').Trim();
            if (p.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                p = p.Substring("unity://path/".Length);
            while (p.StartsWith("Assets/Assets/", StringComparison.OrdinalIgnoreCase))
                p = p.Substring("Assets/".Length);
            if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                p = "Assets/" + p.TrimStart('/');
            return p;
        }

        /// <summary>
        /// Schedules a script refresh operation with debouncing to avoid excessive recompilation.
        /// </summary>
        /// <param name="relPath">The relative path to the script to refresh</param>
        public static void ScheduleScriptRefresh(string relPath)
        {
            var sp = SanitizeAssetsPath(relPath);
            RefreshDebounce.Schedule(sp, TimeSpan.FromMilliseconds(200));
        }

        /// <summary>
        /// Cancels all pending script refresh/compile operations.
        /// Useful for test cleanup to prevent cross-contamination between tests.
        /// </summary>
        public static void CancelPendingRefreshes()
        {
            RefreshDebounce.CancelAll();
        }

        /// <summary>
        /// Imports an asset and requests script compilation.
        /// </summary>
        /// <param name="relPath">The relative path to the script to import</param>
        /// <param name="synchronous">Whether to force synchronous import (default: true)</param>
        public static void ImportAndRequestCompile(string relPath, bool synchronous = true)
        {
            var sp = SanitizeAssetsPath(relPath);
            var opts = ImportAssetOptions.ForceUpdate;
            if (synchronous) opts |= ImportAssetOptions.ForceSynchronousImport;
            AssetDatabase.ImportAsset(sp, opts);
#if UNITY_EDITOR
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
#endif
        }

        /// <summary>
        /// Split an incoming URI or path into (name, directory) suitable for Unity.
        ///
        /// Rules:
        /// - unity://path/Assets/... or unity://path/Packages/... → keep as Unity project path.
        /// - file://... and absolute paths → resolve through ProjectPathResolver.
        /// - plain paths → resolve as Assets-relative by default for backward compatibility.
        /// </summary>
        /// <param name="uri">The URI or path to split</param>
        /// <returns>A tuple containing (name, directory) where name is the filename without extension and directory is the Assets-relative path</returns>
        public static (string name, string directory) SplitUri(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return (null, null);

            var resolved = ProjectPathResolver.Resolve(uri, assumeAssetRelative: true);
            var effectivePath = resolved.AssetPath ?? resolved.ProjectRelativePath ?? resolved.AbsolutePath;
            if (string.IsNullOrWhiteSpace(effectivePath))
                return (null, null);

            // Extract name (filename without extension) and directory
            string name = Path.GetFileNameWithoutExtension(effectivePath);
            string directory = Path.GetDirectoryName(effectivePath)?.Replace('\\', '/');

            return (name, directory);
        }
    }
}
