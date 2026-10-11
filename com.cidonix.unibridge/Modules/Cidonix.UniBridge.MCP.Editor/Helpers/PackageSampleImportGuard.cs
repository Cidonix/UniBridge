using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal sealed class PackageSampleBlockedException : Exception
    {
        public string Code { get; }
        public PackageSampleBlockedException(string code, string message) : base(message) { Code = code; }
    }
    internal sealed class PackageSampleImportPlan
    {
        public string Source, Target;
        public long Bytes;
        public List<PackageSampleFile> Files = new List<PackageSampleFile>();
    }
    internal sealed class PackageSampleFile { public string RelativePath, Sha256, Guid; }
    internal static class PackageSampleImportGuard
    {
        static readonly Regex GuidPattern = new Regex(@"^guid:\s*([0-9a-f]{32})\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal static PackageSampleImportPlan Prepare(IUnityPackageManager backend, PackageInfoData package, PackageSampleData sample, int maxFiles, long maxBytes)
        {
            if (sample.InteractiveImport) throw Block("INTERACTIVE_SAMPLE_UNSUPPORTED", "Interactive unitypackage samples cannot guarantee a non-overwriting import.");
            if (sample.Imported) throw Block("SAMPLE_ALREADY_IMPORTED", "This sample is already imported. Existing author files are never overwritten.");
            var root = Path.GetFullPath(backend.ProjectRoot);
            var assets = Path.GetFullPath(Path.Combine(root, "Assets"));
            var target = Path.GetFullPath(Path.IsPathRooted(sample.ImportPath) ? sample.ImportPath : Path.Combine(root, sample.ImportPath ?? string.Empty));
            var samplesRoot = Path.Combine(assets, "Samples");
            if (!Inside(target, samplesRoot)) throw Block("UNSAFE_SAMPLE_DESTINATION", "Sample import path must remain beneath this project's Assets/Samples.");
            NoReparseParents(target, root);
            CheckDestinationStillAbsent(target, root);
            var packageRoot = Path.GetFullPath(package.ResolvedPath ?? string.Empty);
            var source = Path.GetFullPath(sample.ResolvedPath ?? string.Empty);
            if (!Inside(source, packageRoot) || !Directory.Exists(source)) throw Block("UNSAFE_SAMPLE_SOURCE", "Sample source must be an existing directory beneath the resolved package root.");
            NoReparseParents(source, packageRoot);
            var plan = new PackageSampleImportPlan { Source = source, Target = target };
            var pending = new Stack<string>(); pending.Push(source);
            int directoryCount = 1;
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw Block("UNSAFE_SAMPLE_SOURCE", "Sample sources containing reparse points are not imported.");
                    if (Directory.Exists(entry))
                    {
                        if (++directoryCount > maxFiles * 2) throw Block("SAMPLE_BUDGET_EXCEEDED", "Sample directory traversal exceeds the requested bounded scan.");
                        pending.Push(entry); continue;
                    }
                    var info = new FileInfo(entry);
                    if (plan.Files.Count >= maxFiles || info.Length > maxBytes - plan.Bytes) throw Block("SAMPLE_BUDGET_EXCEEDED", "Sample source exceeds the requested file or byte budget.");
                    var relative = entry.Substring(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1);
                    string guid = null;
                    if (entry.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        if (info.Length > 65536) throw Block("INVALID_SAMPLE_METADATA", "Sample metadata exceeds 64 KiB.");
                        var match = GuidPattern.Match(File.ReadAllText(entry));
                        if (!match.Success) throw Block("INVALID_SAMPLE_METADATA", "Sample metadata is missing its GUID.");
                        guid = match.Groups[1].Value.ToLowerInvariant();
                        if (!guids.Add(guid) || !string.IsNullOrEmpty(backend.ResolveGuid(guid)))
                            throw Block("SAMPLE_GUID_CONFLICT", "A sample GUID already identifies an imported author/dependency asset or is duplicated in the source.");
                    }
                    plan.Files.Add(new PackageSampleFile { RelativePath = relative, Sha256 = Hash(entry), Guid = guid });
                    plan.Bytes += info.Length;
                }
            }
            if (plan.Files.Count == 0) throw Block("EMPTY_SAMPLE", "The sample source contains no files.");
            return plan;
        }
        internal static void CheckDestinationStillAbsent(string target, string projectRoot)
        {
            NoReparseParents(target, Path.GetFullPath(projectRoot));
            if (File.Exists(target) || Directory.Exists(target) || File.Exists(target + ".meta") || Directory.Exists(target + ".meta"))
                throw Block("SAMPLE_DESTINATION_EXISTS", "Sample destination or its metadata already exists; no overwrite option is enabled.");
        }
        internal static int Verify(PackageSampleImportPlan plan, IUnityPackageManager backend)
        {
            foreach (var file in plan.Files)
            {
                var target = Path.Combine(plan.Target, file.RelativePath);
                NoReparseParents(target, Path.GetFullPath(backend.ProjectRoot));
                if (!File.Exists(target) || Hash(target) != file.Sha256)
                    throw Block("SAMPLE_IMPORT_MISMATCH", "Imported file bytes differ from the guarded sample source: " + file.RelativePath);
                if (file.Guid != null)
                {
                    var assetPath = target.Substring(Path.GetFullPath(backend.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1);
                    assetPath = assetPath.Substring(0, assetPath.Length - ".meta".Length).Replace('\\', '/');
                    if (!string.Equals(backend.ResolveGuid(file.Guid), assetPath, StringComparison.OrdinalIgnoreCase))
                        throw Block("SAMPLE_GUID_UNVERIFIED", "AssetDatabase did not confirm the imported sample GUID at its new asset path.");
                }
            }
            return plan.Files.Count;
        }
        static bool Inside(string path, string root) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        static void NoReparseParents(string path, string boundary)
        {
            for (var current = path; current != null && !string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw Block("UNSAFE_SAMPLE_PATH", "A sample source/destination ancestor is a reparse point.");
                if (!Inside(current, boundary)) throw Block("UNSAFE_SAMPLE_PATH", "Sample path escaped its declared boundary.");
            }
        }
        static string Hash(string path)
        {
            using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path))
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }
        static PackageSampleBlockedException Block(string code, string detail) => new PackageSampleBlockedException(code, detail);
    }
}
