using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Reference images are captured from files, never loaded as executable assemblies.
    // Content is read on each request; timestamp and length cannot hide a replacement.
    internal static class MetadataReferenceCatalog
    {
        internal const int MaxReferences = 1024;
        internal const long MaxImageBytes = 32L * 1024 * 1024;
        internal const long MaxRequestBytes = 256L * 1024 * 1024;
        const long MaxCacheBytes = 128L * 1024 * 1024;
        const int MaxCacheEntries = 512;
        const int MaxIssues = 32;
        static readonly StringComparer PathComparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        static readonly object CacheLock = new object();
        static readonly Dictionary<string, CachedImage> Cache = new Dictionary<string, CachedImage>(StringComparer.Ordinal);
        static long s_Access;
        static long s_CacheBytes;

        internal static void Invalidate()
        {
            lock (CacheLock)
            {
                Cache.Clear();
                s_CacheBytes = 0;
            }
        }

        internal static MetadataReferenceReport Build(IEnumerable<MetadataReferenceRequest> requested,
            string baseDirectory, string ownOutput = null, bool targetSpecific = true)
        {
            var report = new MetadataReferenceReport { TargetSpecific = targetSpecific };
            if (!targetSpecific)
                AddIssue(report, "broader-fallback", null, "The reference set is not tied to an owning Unity compilation assembly.");
            var ownPath = Canonicalize(ownOutput, baseDirectory);
            var plan = new Dictionary<string, MetadataReferenceRequest>(PathComparer);
            foreach (var request in requested ?? Enumerable.Empty<MetadataReferenceRequest>())
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                    continue;
                var path = Canonicalize(request.Path, baseDirectory);
                if (path == null)
                {
                    report.RequiredUnavailable |= request.Required;
                    AddIssue(report, "invalid-path", request.Path, "A reference path could not be canonicalized.");
                    continue;
                }
                if (ownPath != null && PathComparer.Equals(ownPath, path))
                {
                    report.ExcludedOwnOutput++;
                    continue;
                }
                var properties = NormalizeProperties(request.Properties);
                var key = path + "|" + properties.Kind + "|" + properties.EmbedInteropTypes;
                if (plan.TryGetValue(key, out var previous))
                {
                    previous.Properties = previous.Properties.WithAliases(previous.Properties.Aliases.Concat(properties.Aliases)
                        .Distinct(StringComparer.Ordinal).OrderBy(alias => alias, StringComparer.Ordinal));
                    previous.Required |= request.Required;
                    report.DuplicateRequests++;
                    continue;
                }
                if (plan.Count >= MaxReferences)
                {
                    report.RequiredUnavailable |= request.Required;
                    AddIssue(report, "reference-limit", path, "The reference set exceeds 1024 property-distinct requests.");
                    continue;
                }
                plan.Add(key, new MetadataReferenceRequest(path, properties, request.Required));
            }
            report.PlannedCount = plan.Count;
            var captures = new List<MetadataReferenceCapture>();
            long bytes = 0;
            foreach (var request in plan.Values.OrderBy(item => item.Path, StringComparer.Ordinal)
                         .ThenBy(item => item.Properties.Kind).ThenBy(item => item.Properties.EmbedInteropTypes))
            {
                try
                {
                    var capture = Capture(request, MaxRequestBytes - bytes);
                    bytes += capture.Length;
                    captures.Add(capture);
                }
                catch (Exception ex) when (LoadedAssemblyCatalog.ExpectedInspectionFailure(ex))
                {
                    report.UnavailableCount++;
                    report.RequiredUnavailable |= request.Required;
                    AddIssue(report, "reference-unavailable", request.Path, ex.GetType().Name + ": " + ex.Message);
                }
            }
            report.CapturedBytes = bytes;
            var conflicts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in captures.GroupBy(item => item.Identity, StringComparer.Ordinal))
            {
                if (group.Select(item => item.Sha256).Distinct(StringComparer.Ordinal).Count() <= 1)
                    continue;
                conflicts.Add(group.Key);
                report.RequiredUnavailable |= group.Any(item => item.Required);
                AddIssue(report, "conflicting-reference-identity", null, group.Key);
            }
            // Equal artifact content may be requested through more than one path. Preserve property-distinct variants.
            var emitted = new Dictionary<string, MetadataReferenceCapture>(StringComparer.Ordinal);
            foreach (var capture in captures)
            {
                if (conflicts.Contains(capture.Identity))
                {
                    report.UnavailableCount++;
                    continue;
                }
                var key = capture.Sha256 + "|" + capture.Properties.Kind + "|" + capture.Properties.EmbedInteropTypes;
                if (emitted.TryGetValue(key, out var previous))
                {
                    previous.Properties = previous.Properties.WithAliases(previous.Properties.Aliases.Concat(capture.Properties.Aliases)
                        .Distinct(StringComparer.Ordinal).OrderBy(alias => alias, StringComparer.Ordinal));
                    previous.Required |= capture.Required;
                    report.DuplicateArtifacts++;
                }
                else emitted.Add(key, capture);
            }
            foreach (var capture in emitted.Values.OrderBy(item => item.Path, StringComparer.Ordinal)
                         .ThenBy(item => item.Properties.Kind).ThenBy(item => item.Properties.EmbedInteropTypes))
            {
                report.Captures.Add(capture);
                report.References.Add(capture.Reference.WithProperties(capture.Properties));
            }
            report.Status = report.References.Count == 0 ? "unavailable" : report.Partial ? "partial" : "available";
            return report;
        }

        static MetadataReferenceCapture Capture(MetadataReferenceRequest request, long remainingBytes)
        {
            byte[] image;
            using (var stream = new FileStream(request.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length <= 0 || stream.Length > MaxImageBytes || stream.Length > remainingBytes)
                    throw new IOException("The captured metadata image exceeds its 32 MiB image or 256 MiB request budget, or is empty.");
                image = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < image.Length)
                {
                    var read = stream.Read(image, offset, image.Length - offset);
                    if (read == 0) throw new EndOfStreamException("The reference was truncated while being captured.");
                    offset += read;
                }
                if (stream.ReadByte() >= 0)
                    throw new IOException("The metadata reference length changed while being captured.");
            }
            string sha;
            using (var hasher = SHA256.Create())
                sha = BitConverter.ToString(hasher.ComputeHash(image)).Replace("-", "").ToLowerInvariant();
            var cacheKey = request.Path + "|" + sha + "|" + request.Properties.Kind;
            CachedImage cached;
            lock (CacheLock)
            {
                if (Cache.TryGetValue(cacheKey, out cached))
                {
                    cached.LastAccess = ++s_Access;
                    return new MetadataReferenceCapture(request, sha, image.Length, cached.Identity, cached.Reference);
                }
            }
            var identity = ReadIdentity(image, request.Properties.Kind);
            var reference = MetadataReference.CreateFromImage(ImmutableArray.CreateRange(image), request.Properties, filePath: request.Path);
            // Force managed metadata validation now, rather than deferring a malformed image to compilation.
            reference.GetMetadata();
            lock (CacheLock)
            {
                if (!Cache.TryGetValue(cacheKey, out cached))
                {
                    while (Cache.Count > 0 && (Cache.Count >= MaxCacheEntries || s_CacheBytes + image.Length > MaxCacheBytes))
                    {
                        var oldest = Cache.OrderBy(pair => pair.Value.LastAccess).First();
                        Cache.Remove(oldest.Key);
                        s_CacheBytes -= oldest.Value.Length;
                    }
                    cached = new CachedImage { Identity = identity, Reference = reference, Length = image.Length, LastAccess = ++s_Access };
                    Cache.Add(cacheKey, cached);
                    s_CacheBytes += image.Length;
                }
            }
            return new MetadataReferenceCapture(request, sha, image.Length, cached.Identity, cached.Reference);
        }

        static string ReadIdentity(byte[] image, MetadataImageKind kind)
        {
            using (var stream = new MemoryStream(image, false))
            using (var pe = new PEReader(stream))
            {
                if (!pe.HasMetadata)
                    throw new BadImageFormatException("The reference has no managed metadata.");
                var reader = pe.GetMetadataReader();
                if (kind == MetadataImageKind.Module)
                {
                    if (reader.IsAssembly)
                        throw new BadImageFormatException("A module reference was supplied with assembly metadata.");
                    var module = reader.GetModuleDefinition();
                    return "module:" + reader.GetString(module.Name) + ":" + reader.GetGuid(module.Mvid).ToString("N");
                }
                if (!reader.IsAssembly)
                    throw new BadImageFormatException("An assembly reference was supplied with module metadata.");
                var definition = reader.GetAssemblyDefinition();
                var culture = definition.Culture.IsNil ? "neutral" : reader.GetString(definition.Culture);
                var publicKey = definition.PublicKey.IsNil ? Array.Empty<byte>() : reader.GetBlobBytes(definition.PublicKey);
                var token = "null";
                if (publicKey.Length > 0)
                {
                    using (var hasher = SHA1.Create())
                        token = BitConverter.ToString(hasher.ComputeHash(publicKey).Reverse().Take(8).ToArray()).Replace("-", "").ToLowerInvariant();
                }
                return reader.GetString(definition.Name) + ", Version=" + definition.Version + ", Culture=" +
                       (string.IsNullOrEmpty(culture) ? "neutral" : culture) + ", PublicKeyToken=" + token;
            }
        }

        internal static string Canonicalize(string path, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory ?? Environment.CurrentDirectory, path)); }
            catch (Exception ex) when (LoadedAssemblyCatalog.ExpectedInspectionFailure(ex)) { return null; }
        }
        static MetadataReferenceProperties NormalizeProperties(MetadataReferenceProperties properties) =>
            properties.Kind == MetadataImageKind.Module ? properties :
            properties.WithAliases((properties.Aliases.IsDefaultOrEmpty ? new[] { "global" } : properties.Aliases.ToArray())
                .Distinct(StringComparer.Ordinal).OrderBy(alias => alias, StringComparer.Ordinal));
        static void AddIssue(MetadataReferenceReport report, string category, string path, string detail)
        {
            report.Partial = true;
            if (report.Issues.Count < MaxIssues)
                report.Issues.Add(new MetadataReferenceIssue(category, path, detail));
        }
        sealed class CachedImage
        {
            internal string Identity;
            internal PortableExecutableReference Reference;
            internal long Length;
            internal long LastAccess;
        }
    }
    internal sealed class MetadataReferenceRequest
    {
        internal readonly string Path;
        internal MetadataReferenceProperties Properties;
        internal bool Required;
        internal MetadataReferenceRequest(string path, MetadataReferenceProperties properties = default, bool required = true)
        { Path = path; Properties = properties; Required = required; }
    }
    internal sealed class MetadataReferenceCapture
    {
        internal readonly string Path;
        internal MetadataReferenceProperties Properties;
        internal bool Required;
        internal readonly string Sha256;
        internal readonly long Length;
        internal readonly string Identity;
        internal readonly PortableExecutableReference Reference;
        internal MetadataReferenceCapture(MetadataReferenceRequest request, string sha, long length, string identity, PortableExecutableReference reference)
        { Path = request.Path; Properties = request.Properties; Required = request.Required; Sha256 = sha; Length = length; Identity = identity; Reference = reference; }
    }
    internal sealed class MetadataReferenceIssue
    {
        internal readonly string Category;
        internal readonly string Path;
        internal readonly string Detail;
        internal MetadataReferenceIssue(string category, string path, string detail) { Category = category; Path = path; Detail = detail; }
    }
    internal sealed class MetadataReferenceReport
    {
        internal string Status;
        internal bool Partial;
        internal bool RequiredUnavailable;
        internal bool TargetSpecific;
        internal int PlannedCount;
        internal int ExcludedOwnOutput;
        internal int DuplicateRequests;
        internal int DuplicateArtifacts;
        internal int UnavailableCount;
        internal long CapturedBytes;
        internal readonly List<MetadataReferenceIssue> Issues = new List<MetadataReferenceIssue>();
        internal readonly List<MetadataReferenceCapture> Captures = new List<MetadataReferenceCapture>();
        internal readonly List<MetadataReference> References = new List<MetadataReference>();
    }
}
