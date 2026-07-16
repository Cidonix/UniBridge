using System;
using System.IO;
using System.Net;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Shared project path resolver for MCP tools that need to accept Unity asset paths,
    /// project-relative paths, package paths, file URIs, and absolute paths consistently.
    /// </summary>
    static class ProjectPathResolver
    {
        public sealed class Result
        {
            public string Input;
            public string ProjectRoot;
            public string ProjectRelativePath;
            public string AssetPath;
            public string AbsolutePath;
            public string DisplayPath;
            public string PackageName;
            public string Error;
            public bool Exists;
            public bool IsProjectRelative;
            public bool IsAssetDatabasePath;
            public bool IsInsideProject;
            public bool IsPackage;
            public bool IsExternalPackage;
        }

        public static string ProjectRoot
        {
            get
            {
                try
                {
                    var assetsDirectory = new DirectoryInfo(Application.dataPath);
                    return Path.GetFullPath(assetsDirectory.Parent?.FullName ?? Directory.GetCurrentDirectory());
                }
                catch
                {
                    return Path.GetFullPath(Directory.GetCurrentDirectory());
                }
            }
        }

        public static Result Resolve(string input, bool assumeAssetRelative = false)
        {
            var result = new Result
            {
                Input = input,
                ProjectRoot = NormalizeSlashes(ProjectRoot).TrimEnd('/')
            };

            if (string.IsNullOrWhiteSpace(input))
            {
                result.Error = "path_empty";
                return result;
            }

            var path = DecodeAndNormalize(input);
            if (string.IsNullOrWhiteSpace(path))
            {
                result.Error = "path_empty";
                return result;
            }

            if (HasInvalidPathCharacters(path))
            {
                result.Error = "path_invalid_characters";
                return result;
            }

            try
            {
                if (IsRooted(path))
                {
                    ResolveAbsolute(result, path);
                }
                else
                {
                    ResolveProjectRelative(result, path, assumeAssetRelative);
                }

                result.DisplayPath = !string.IsNullOrWhiteSpace(result.AssetPath)
                    ? result.AssetPath
                    : (!string.IsNullOrWhiteSpace(result.ProjectRelativePath) ? result.ProjectRelativePath : result.AbsolutePath);

                result.Exists = Exists(result);
            }
            catch (Exception ex)
            {
                result.Error = "path_invalid: " + ex.Message;
            }
            return result;
        }

        public static string NormalizeAssetPath(string input, bool assumeAssetRelative = false)
        {
            var resolved = Resolve(input, assumeAssetRelative);
            return !string.IsNullOrWhiteSpace(resolved.AssetPath)
                ? resolved.AssetPath
                : resolved.ProjectRelativePath;
        }

        public static string ToAbsolutePath(string input, bool assumeAssetRelative = false)
        {
            return Resolve(input, assumeAssetRelative).AbsolutePath;
        }

        public static string ToProjectRelativePath(string input, bool assumeAssetRelative = false)
        {
            var resolved = Resolve(input, assumeAssetRelative);
            return !string.IsNullOrWhiteSpace(resolved.ProjectRelativePath)
                ? resolved.ProjectRelativePath
                : resolved.AssetPath;
        }

        public static bool TryNormalizeAssetPath(string input, out string assetPath, bool assumeAssetRelative = false)
        {
            var resolved = Resolve(input, assumeAssetRelative);
            assetPath = resolved.AssetPath;
            return !string.IsNullOrWhiteSpace(assetPath);
        }

        static void ResolveAbsolute(Result result, string path)
        {
            try
            {
                var absolute = Path.GetFullPath(ToNativeSeparators(path));
                result.AbsolutePath = NormalizeSlashes(absolute);

                var projectRoot = result.ProjectRoot.TrimEnd('/');
                if (IsUnderRoot(result.AbsolutePath, projectRoot))
                {
                    result.ProjectRelativePath = TrimKnownLeadingSlash(result.AbsolutePath.Substring(projectRoot.Length).TrimStart('/', '\\'));
                    result.IsInsideProject = true;
                    result.IsProjectRelative = true;
                    SetAssetPathFlags(result, result.ProjectRelativePath);
                    return;
                }

                if (TryMapAbsolutePackagePath(result, result.AbsolutePath))
                    return;

                result.Error = "path_outside_project";
            }
            catch (Exception ex)
            {
                result.Error = "path_invalid: " + ex.Message;
            }
        }

        static void ResolveProjectRelative(Result result, string path, bool assumeAssetRelative)
        {
            var relative = TrimKnownLeadingSlash(path);
            if (relative.StartsWith("project://database/", StringComparison.OrdinalIgnoreCase))
                relative = relative.Substring("project://database/".Length);

            if (relative.StartsWith("project:/", StringComparison.OrdinalIgnoreCase))
                relative = relative.Substring("project:/".Length).TrimStart('/');

            if (!HasKnownProjectRoot(relative) && assumeAssetRelative)
                relative = "Assets/" + relative.TrimStart('/');

            result.ProjectRelativePath = NormalizeSlashes(relative);
            result.IsProjectRelative = true;
            SetAssetPathFlags(result, result.ProjectRelativePath);

            result.AbsolutePath = ResolveAbsoluteForProjectRelative(result.ProjectRelativePath);
            if (!string.IsNullOrWhiteSpace(result.AbsolutePath))
                result.IsInsideProject = IsUnderRoot(result.AbsolutePath, result.ProjectRoot);
        }

        static string ResolveAbsoluteForProjectRelative(string projectRelativePath)
        {
            if (string.IsNullOrWhiteSpace(projectRelativePath))
                return null;

            if (projectRelativePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                var packagePath = ResolvePackageAbsolutePath(projectRelativePath);
                if (!string.IsNullOrWhiteSpace(packagePath))
                    return NormalizeSlashes(packagePath);
            }

            return NormalizeSlashes(Path.GetFullPath(Path.Combine(ProjectRoot, ToNativeSeparators(projectRelativePath))));
        }

        static string ResolvePackageAbsolutePath(string assetPath)
        {
            var localPath = Path.GetFullPath(Path.Combine(ProjectRoot, ToNativeSeparators(assetPath)));
            if (File.Exists(localPath) || Directory.Exists(localPath))
                return localPath;

            try
            {
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (packageInfo != null && !string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
                {
                    var prefix = "Packages/" + packageInfo.name;
                    var relativeInsidePackage = assetPath.Length > prefix.Length
                        ? assetPath.Substring(prefix.Length).TrimStart('/')
                        : string.Empty;
                    return Path.GetFullPath(Path.Combine(packageInfo.resolvedPath, ToNativeSeparators(relativeInsidePackage)));
                }
            }
            catch
            {
                // Package manager can throw while Unity is reloading; fall through to project-local path.
            }

            return localPath;
        }

        static bool TryMapAbsolutePackagePath(Result result, string absolutePath)
        {
            try
            {
                foreach (var packageInfo in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (packageInfo == null || string.IsNullOrWhiteSpace(packageInfo.resolvedPath) || string.IsNullOrWhiteSpace(packageInfo.name))
                        continue;

                    var root = NormalizeSlashes(Path.GetFullPath(packageInfo.resolvedPath)).TrimEnd('/');
                    if (!IsUnderRoot(absolutePath, root))
                        continue;

                    var relativeInsidePackage = absolutePath.Length > root.Length
                        ? absolutePath.Substring(root.Length).TrimStart('/')
                        : string.Empty;
                    result.PackageName = packageInfo.name;
                    result.AssetPath = ("Packages/" + packageInfo.name + "/" + relativeInsidePackage).TrimEnd('/');
                    result.ProjectRelativePath = result.AssetPath;
                    result.IsAssetDatabasePath = true;
                    result.IsPackage = true;
                    result.IsExternalPackage = !IsUnderRoot(absolutePath, result.ProjectRoot);
                    return true;
                }
            }
            catch
            {
                // Best effort only. The original absolute path remains available.
            }

            return false;
        }

        static void SetAssetPathFlags(Result result, string projectRelativePath)
        {
            if (string.IsNullOrWhiteSpace(projectRelativePath))
                return;

            if (projectRelativePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                projectRelativePath.Equals("Assets", StringComparison.OrdinalIgnoreCase) ||
                projectRelativePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                projectRelativePath.Equals("Packages", StringComparison.OrdinalIgnoreCase))
            {
                result.AssetPath = projectRelativePath;
                result.IsAssetDatabasePath = true;
                result.IsPackage = projectRelativePath.StartsWith("Packages", StringComparison.OrdinalIgnoreCase);
            }
        }

        static bool Exists(Result result)
        {
            if (!string.IsNullOrWhiteSpace(result.AbsolutePath) &&
                (File.Exists(result.AbsolutePath) || Directory.Exists(result.AbsolutePath)))
                return true;

            if (!string.IsNullOrWhiteSpace(result.AssetPath))
            {
                try
                {
                    return AssetDatabase.IsValidFolder(result.AssetPath) ||
                           !string.IsNullOrWhiteSpace(AssetDatabase.AssetPathToGUID(result.AssetPath));
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        static string DecodeAndNormalize(string input)
        {
            var value = input.Trim().Trim('"').Replace('\\', '/');

            if (value.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("unity://path/".Length);

            if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var uri = new Uri(value);
                    var host = uri.Host?.Trim() ?? string.Empty;
                    var localPath = uri.LocalPath ?? string.Empty;
                    value = !string.IsNullOrEmpty(host) && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        ? $"//{host}{localPath}"
                        : localPath;
                }
                catch
                {
                    value = value.Substring("file://".Length);
                }
            }

            value = WebUtility.UrlDecode(value).Replace('\\', '/');
            if (Application.platform == RuntimePlatform.WindowsEditor &&
                value.Length >= 3 &&
                value[0] == '/' &&
                value[2] == ':')
            {
                value = value.Substring(1);
            }

            return NormalizeSlashes(value);
        }

        static bool HasKnownProjectRoot(string path)
        {
            return path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("Assets", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("Packages", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("Library/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("Library", StringComparison.OrdinalIgnoreCase);
        }

        static string TrimKnownLeadingSlash(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            while (path.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/Assets", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/Packages/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/Packages", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/ProjectSettings/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/ProjectSettings", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/Library/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("/Library", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(1);
            }

            return path;
        }

        static bool IsRooted(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.StartsWith("//", StringComparison.Ordinal))
                return true;

            try
            {
                return Path.IsPathRooted(ToNativeSeparators(path));
            }
            catch
            {
                return false;
            }
        }

        static bool HasInvalidPathCharacters(string path)
        {
            return !string.IsNullOrEmpty(path) && path.IndexOfAny(Path.GetInvalidPathChars()) >= 0;
        }

        static bool IsUnderRoot(string path, string root)
        {
            var normalizedPath = NormalizeSlashes(path).TrimEnd('/');
            var normalizedRoot = NormalizeSlashes(root).TrimEnd('/');
            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizeSlashes(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        static string ToNativeSeparators(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
