using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // No explicit load workflow or user-code invocation; runtime reflection can resolve dependencies.
    internal static class LoadedAssemblyCatalog
    {
        const int MaxIssues = 32;
        static readonly ConditionalWeakTable<Assembly, RuntimeIdentity> Identities = new ConditionalWeakTable<Assembly, RuntimeIdentity>();
        static long s_NextIdentity;
        static long s_Generation = 1;
        static readonly object SnapshotLock = new object();
        static WeakReference<AssemblySnapshot> s_Snapshot;
        static string s_LastMembership;

        static LoadedAssemblyCatalog()
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
        }

        static void OnAssemblyLoaded(object sender, AssemblyLoadEventArgs args) => Invalidate();
        internal static void Invalidate() => Interlocked.Increment(ref s_Generation);
        internal static long CurrentGeneration => Volatile.Read(ref s_Generation);
        internal static Assembly[] GetLoadedAssemblies() => Capture().Assemblies.Select(descriptor => descriptor.Assembly).ToArray();
        internal static Type[] EnumerateTypes(Assembly assembly) => GetTypesSafe(assembly).Types;
        internal static Type LookupType(Assembly assembly, string name, bool throwOnError = false, bool ignoreCase = false)
        {
            var result = GetTypeSafe(assembly, name, ignoreCase);
            if (result.Types.Length > 0) return result.Types[0];
            if (throwOnError) throw new TypeLoadException("The requested loaded type is unavailable: " + name + ".");
            return null;
        }
        internal static Type ResolveType(string typeName, bool throwOnError = false, bool ignoreCase = false)
        {
            var result = Resolve(typeName, ignoreCase: ignoreCase);
            if (!result.Partial && result.Status == "unique") return result.Type;
            if (throwOnError) throw new TypeLoadException("The requested loaded type could not be uniquely resolved: " + typeName + ".");
            return null;
        }

        internal static AssemblySnapshot Capture()
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var capturedGeneration = CurrentGeneration;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var membership = string.Join(";", assemblies.Where(a => a != null)
                    .Select(GetRuntimeIdentity).OrderBy(id => id));
                lock (SnapshotLock)
                {
                    if (s_LastMembership != null && !string.Equals(s_LastMembership, membership, StringComparison.Ordinal))
                    {
                        var before = CurrentGeneration;
                        var after = Interlocked.Increment(ref s_Generation);
                        if (before == capturedGeneration) capturedGeneration = after;
                    }
                    s_LastMembership = membership;
                    var generation = capturedGeneration;
                    if (s_Snapshot != null && s_Snapshot.TryGetTarget(out var cached) &&
                        cached.Generation == generation && string.Equals(cached.Membership, membership, StringComparison.Ordinal))
                        if (generation == CurrentGeneration) return cached;
                    var snapshot = Build(assemblies, generation, membership);
                    // The first enumeration or descriptor inspection can load runtime implementation assemblies.
                    if (generation != Volatile.Read(ref s_Generation))
                    {
                        if (attempt == 0)
                            continue;
                        snapshot = snapshot.WithChanging("The loaded assembly set changed while it was inspected.");
                    }
                    s_Snapshot = new WeakReference<AssemblySnapshot>(snapshot);
                    return snapshot;
                }
            }
            throw new InvalidOperationException("Unreachable snapshot capture state.");
        }

        // Kept internal so deterministic tests can exercise expected reflection failures without a process-wide hook.
        internal static AssemblySnapshot Build(IEnumerable<Assembly> assemblies, long generation, string membership = null)
        {
            var descriptors = new List<AssemblyDescriptor>();
            var issues = new List<CatalogIssue>();
            var seen = new HashSet<Assembly>(AssemblyReferenceComparer.Instance);
            foreach (var assembly in assemblies ?? Enumerable.Empty<Assembly>())
            {
                if (assembly == null || !seen.Add(assembly))
                    continue;
                var descriptor = Describe(assembly);
                descriptors.Add(descriptor);
                foreach (var issue in descriptor.Issues)
                    AddIssue(issues, issue);
            }
            descriptors.Sort((a, b) =>
            {
                var order = string.CompareOrdinal(a.FullName, b.FullName);
                if (order == 0) order = string.CompareOrdinal(a.ModuleVersionId, b.ModuleVersionId);
                if (order == 0) order = string.CompareOrdinal(a.ContextName, b.ContextName);
                return order != 0 ? order : a.RuntimeId.CompareTo(b.RuntimeId);
            });
            return new AssemblySnapshot(generation, descriptors, issues, membership, issues.Count > 0);
        }

        internal static AssemblyDescriptor Describe(Assembly assembly)
        {
            if (assembly == null)
                throw new ArgumentNullException(nameof(assembly));
            var descriptor = new AssemblyDescriptorBuilder(assembly, GetRuntimeIdentity(assembly));
            try
            {
                var identity = assembly.GetName();
                descriptor.SimpleName = identity?.Name ?? string.Empty;
                descriptor.FullName = identity?.FullName ?? string.Empty;
            }
            catch (Exception ex) when (ExpectedInspectionFailure(ex))
            {
                AddIssue(descriptor.Issues, Issue("identity-unavailable", descriptor, ex));
            }
            try { descriptor.IsDynamic = assembly.IsDynamic; }
            catch (Exception ex) when (ExpectedInspectionFailure(ex)) { AddIssue(descriptor.Issues, Issue("dynamic-status-unavailable", descriptor, ex)); }
            try { descriptor.ModuleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("N"); }
            catch (Exception ex) when (ExpectedInspectionFailure(ex)) { AddIssue(descriptor.Issues, Issue("module-id-unavailable", descriptor, ex)); }
            var path = TryGetPhysicalMetadataPath(assembly);
            descriptor.LocationStatus = path.Status;
            descriptor.PhysicalPath = path.Path;
            if (path.Issue != null)
                AddIssue(descriptor.Issues, path.Issue);
            ReadBuiltInContextMetadata(assembly, descriptor);
            return descriptor.Freeze();
        }

        internal static AssemblyPathResult TryGetPhysicalMetadataPath(Assembly assembly)
        {
            if (assembly == null)
                return new AssemblyPathResult("null", null, null);
            try
            {
                if (assembly.IsDynamic)
                    return new AssemblyPathResult("dynamic", null, null);
                var location = assembly.Location;
                if (string.IsNullOrWhiteSpace(location))
                    return new AssemblyPathResult("locationless", null, null);
                var fullPath = Path.GetFullPath(location);
                // Path eligibility is independent of managed metadata readability, which the reference reader checks.
                if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
                    return new AssemblyPathResult("invalid-path", null, new CatalogIssue("location-invalid-path", null, "The location is a directory."));
                return new AssemblyPathResult("available", fullPath, null);
            }
            catch (FileNotFoundException ex) { return PathFailure("missing", ex); }
            catch (DirectoryNotFoundException ex) { return PathFailure("missing", ex); }
            catch (UnauthorizedAccessException ex) { return PathFailure("access-denied", ex); }
            catch (SecurityException ex) { return PathFailure("access-denied", ex); }
            catch (NotSupportedException ex) { return PathFailure("unsupported", ex); }
            catch (ArgumentException ex) { return PathFailure("invalid-path", ex); }
            catch (Exception ex) when (ExpectedInspectionFailure(ex)) { return PathFailure("unavailable", ex); }
        }

        static AssemblyPathResult PathFailure(string status, Exception ex) =>
            new AssemblyPathResult(status, null, new CatalogIssue("location-" + status, null, ex.GetType().Name));

        static void ReadBuiltInContextMetadata(Assembly assembly, AssemblyDescriptorBuilder descriptor)
        {
            // Optional metadata only; Mono builds have no System.Runtime.Loader dependency.
            // This reflects framework members rather than arbitrary user assembly members.
            try
            {
                var collectibleProperty = typeof(Assembly).GetProperty("IsCollectible");
                if (collectibleProperty != null)
                    descriptor.IsCollectible = collectibleProperty.GetValue(assembly) as bool?;
                var loaderType = typeof(Assembly).Assembly.GetType("System.Runtime.Loader.AssemblyLoadContext", false);
                var getContext = loaderType?.GetMethod("GetLoadContext", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(Assembly) }, null);
                var context = getContext?.Invoke(null, new object[] { assembly });
                if (context != null)
                    descriptor.ContextName = loaderType.GetProperty("Name")?.GetValue(context) as string ?? string.Empty;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null && ExpectedInspectionFailure(ex.InnerException))
            {
                AddIssue(descriptor.Issues, Issue("context-metadata-unavailable", descriptor, ex.InnerException));
            }
            catch (Exception ex) when (ExpectedInspectionFailure(ex))
            {
                AddIssue(descriptor.Issues, Issue("context-metadata-unavailable", descriptor, ex));
            }
        }

        internal static TypeEnumeration GetTypesSafe(Assembly assembly)
        {
            var result = new TypeEnumeration();
            if (assembly == null)
                return result;
            try { result.Types = assembly.GetTypes().Where(type => type != null).ToArray(); }
            catch (ReflectionTypeLoadException ex)
            {
                result.Types = (ex.Types ?? Array.Empty<Type>()).Where(type => type != null).ToArray();
                result.Partial = true;
                foreach (var error in ex.LoaderExceptions ?? Array.Empty<Exception>())
                    if (error != null)
                        AddIssue(result.Issues, new CatalogIssue("partial-type-load", null, error.GetType().Name));
            }
            catch (Exception ex) when (ExpectedInspectionFailure(ex))
            {
                result.Partial = true;
                AddIssue(result.Issues, new CatalogIssue("types-unavailable", null, ex.GetType().Name));
            }
            return result;
        }

        internal static TypeEnumeration GetTypeSafe(Assembly assembly, string fullName, bool ignoreCase = false)
        {
            var result = new TypeEnumeration();
            if (assembly == null || string.IsNullOrWhiteSpace(fullName))
                return result;
            try
            {
                var type = fullName.IndexOf('[') >= 0
                    ? GetConstructedTypeFromLoadedAssemblies(assembly, fullName, ignoreCase)
                    : assembly.GetType(fullName, false, ignoreCase);
                if (type != null) result.Types = new[] { type };
            }
            catch (Exception ex) when (ExpectedInspectionFailure(ex))
            {
                result.Partial = true;
                AddIssue(result.Issues, new CatalogIssue("type-lookup-unavailable", null, ex.GetType().Name));
            }
            return result;
        }

        static Type GetConstructedTypeFromLoadedAssemblies(Assembly owner, string name, bool ignoreCase)
        {
            var snapshot = Capture();
            var ownerDescriptor = snapshot.Assemblies.FirstOrDefault(item => ReferenceEquals(item.Assembly, owner)) ?? Describe(owner);
            // The CLR parser handles array/generic syntax. Both resolvers are supplied so an argument's
            // assembly-qualified name cannot fall back to an implicit Assembly.Load by that parser.
            return Type.GetType(name + ", " + owner.GetName().FullName,
                requested =>
                {
                    if (IdentityMatches(ownerDescriptor, requested)) return owner;
                    var matches = snapshot.Assemblies.Where(item => IdentityMatches(item, requested)).ToArray();
                    return matches.Length == 1 ? matches[0].Assembly : null;
                },
                (assembly, definition, insensitive) =>
                {
                    if (assembly != null) return assembly.GetType(definition, false, insensitive);
                    var matches = snapshot.Assemblies.Select(item => GetTypeSafe(item.Assembly, definition, insensitive))
                        .SelectMany(item => item.Types).Distinct().ToArray();
                    return matches.Length == 1 ? matches[0] : null;
                }, false, ignoreCase);
        }

        internal static TypeResolution Resolve(string query, Func<Type, bool> filter = null,
            ISet<string> preferredAssemblyNames = null, bool ignoreCase = false, AssemblySnapshot snapshot = null,
            bool retryChanging = true)
        {
            var result = new TypeResolution();
            if (string.IsNullOrWhiteSpace(query))
                return result;
            var mayRetry = snapshot == null && retryChanging;
            snapshot = snapshot ?? Capture();
            result.Generation = snapshot.Generation;
            // A missing physical DLL does not make already loaded runtime types unavailable.
            result.Partial = snapshot.Issues.Any(issue => issue.Category == "catalog-changing");
            foreach (var issue in snapshot.Issues) AddIssue(result.Issues, issue);
            var comma = FindTopLevelComma(query);
            var typeName = comma < 0 ? query.Trim() : query.Substring(0, comma).Trim();
            AssemblyName requestedIdentity = null;
            if (comma >= 0)
            {
                try { requestedIdentity = new AssemblyName(query.Substring(comma + 1).Trim()); }
                catch (Exception ex) when (ExpectedInspectionFailure(ex))
                {
                    result.Partial = true;
                    AddIssue(result.Issues, new CatalogIssue("invalid-assembly-identity", null, ex.GetType().Name));
                    return result;
                }
            }
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var descriptor in snapshot.Assemblies)
            {
                if (requestedIdentity != null && !IdentityMatches(descriptor, requestedIdentity))
                    continue;
                var direct = typeName.Contains(".") || requestedIdentity != null;
                var types = direct
                    ? GetTypeSafe(descriptor.Assembly, typeName, ignoreCase)
                    : GetTypesSafe(descriptor.Assembly);
                if (types.Partial) result.Partial = true;
                foreach (var issue in types.Issues) AddIssue(result.Issues, issue);
                foreach (var type in types.Types)
                {
                    if (!direct && !string.Equals(type.FullName, typeName, comparison) && !string.Equals(type.Name, typeName, comparison))
                        continue;
                    if (filter != null && !filter(type)) continue;
                    if (!result.Candidates.Contains(type)) result.Candidates.Add(type);
                }
            }
            if (preferredAssemblyNames != null)
            {
                var preferred = result.Candidates.Where(type => preferredAssemblyNames.Contains(type.Assembly.GetName().Name)).ToList();
                if (preferred.Count > 0) result.Candidates = preferred;
            }
            result.Status = result.Candidates.Count == 0 ? "not-found" : result.Candidates.Count == 1 ? "unique" : "ambiguous";
            if (snapshot.Membership != null && snapshot.Generation != CurrentGeneration)
            {
                if (mayRetry) return Resolve(query, filter, preferredAssemblyNames, ignoreCase, retryChanging: false);
                result.Partial = true;
                AddIssue(result.Issues, new CatalogIssue("catalog-changing", null, "The loaded assembly set changed during type resolution."));
            }
            return result;
        }

        static bool IdentityMatches(AssemblyDescriptor descriptor, AssemblyName requested)
        {
            AssemblyName actual;
            try { actual = descriptor.Assembly.GetName(); }
            catch (Exception ex) when (ExpectedInspectionFailure(ex)) { return false; }
            if (!string.Equals(actual.Name, requested.Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (requested.Version != null && requested.Version != actual.Version) return false;
            if (requested.CultureName != null && !string.Equals(requested.CultureName, actual.CultureName, StringComparison.OrdinalIgnoreCase)) return false;
            var requestedToken = requested.GetPublicKeyToken();
            if (requestedToken != null && !requestedToken.SequenceEqual(actual.GetPublicKeyToken() ?? Array.Empty<byte>())) return false;
            return true;
        }

        static int FindTopLevelComma(string value)
        {
            var nesting = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '[') nesting++;
                else if (value[i] == ']') nesting--;
                else if (value[i] == ',' && nesting == 0) return i;
            }
            return -1;
        }

        internal static bool ExpectedInspectionFailure(Exception ex) =>
            ex is NotSupportedException || ex is BadImageFormatException || ex is FileLoadException ||
            ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException ||
            ex is ArgumentException || ex is InvalidOperationException || ex is TypeLoadException;

        static CatalogIssue Issue(string category, AssemblyDescriptorBuilder descriptor, Exception ex) =>
            new CatalogIssue(category, descriptor.FullName, ex.GetType().Name);
        static void AddIssue(List<CatalogIssue> issues, CatalogIssue issue)
        {
            if (issues.Count < MaxIssues && !issues.Any(item => item.Category == issue.Category && item.AssemblyName == issue.AssemblyName && item.Detail == issue.Detail))
                issues.Add(issue);
        }
        static long GetRuntimeIdentity(Assembly assembly) => Identities.GetValue(assembly, _ => new RuntimeIdentity(Interlocked.Increment(ref s_NextIdentity))).Id;
        sealed class RuntimeIdentity { internal readonly long Id; internal RuntimeIdentity(long id) { Id = id; } }
        sealed class AssemblyReferenceComparer : IEqualityComparer<Assembly>
        {
            internal static readonly AssemblyReferenceComparer Instance = new AssemblyReferenceComparer();
            public bool Equals(Assembly x, Assembly y) => ReferenceEquals(x, y);
            public int GetHashCode(Assembly value) => RuntimeHelpers.GetHashCode(value);
        }
    }

    internal sealed class AssemblySnapshot
    {
        internal readonly long Generation;
        internal readonly ReadOnlyCollection<AssemblyDescriptor> Assemblies;
        internal readonly ReadOnlyCollection<CatalogIssue> Issues;
        internal readonly string Membership;
        internal readonly bool Partial;
        internal AssemblySnapshot(long generation, IEnumerable<AssemblyDescriptor> assemblies,
            IEnumerable<CatalogIssue> issues, string membership, bool partial)
        {
            Generation = generation;
            Assemblies = Array.AsReadOnly(assemblies.ToArray());
            Issues = Array.AsReadOnly(issues.ToArray());
            Membership = membership;
            Partial = partial;
        }
        internal AssemblySnapshot WithChanging(string detail) => new AssemblySnapshot(Generation, Assemblies,
            Issues.Take(31).Concat(new[] { new CatalogIssue("catalog-changing", null, detail) }), Membership, true);
    }
    internal sealed class AssemblyDescriptor
    {
        internal readonly Assembly Assembly;
        internal readonly long RuntimeId;
        internal readonly string SimpleName;
        internal readonly string FullName;
        internal readonly string ModuleVersionId;
        internal readonly bool IsDynamic;
        internal readonly bool? IsCollectible;
        internal readonly string ContextName;
        internal readonly string PhysicalPath;
        internal readonly string LocationStatus;
        internal readonly ReadOnlyCollection<CatalogIssue> Issues;
        internal AssemblyDescriptor(AssemblyDescriptorBuilder value)
        {
            Assembly = value.Assembly; RuntimeId = value.RuntimeId; SimpleName = value.SimpleName;
            FullName = value.FullName; ModuleVersionId = value.ModuleVersionId; IsDynamic = value.IsDynamic;
            IsCollectible = value.IsCollectible; ContextName = value.ContextName;
            PhysicalPath = value.PhysicalPath; LocationStatus = value.LocationStatus;
            Issues = Array.AsReadOnly(value.Issues.ToArray());
        }
    }
    internal sealed class AssemblyDescriptorBuilder
    {
        internal readonly Assembly Assembly;
        internal readonly long RuntimeId;
        internal string SimpleName = string.Empty;
        internal string FullName = string.Empty;
        internal string ModuleVersionId = string.Empty;
        internal bool IsDynamic;
        internal bool? IsCollectible;
        internal string ContextName = string.Empty;
        internal string PhysicalPath;
        internal string LocationStatus;
        internal readonly List<CatalogIssue> Issues = new List<CatalogIssue>();
        internal AssemblyDescriptorBuilder(Assembly assembly, long id) { Assembly = assembly; RuntimeId = id; }
        internal AssemblyDescriptor Freeze() => new AssemblyDescriptor(this);
    }
    internal sealed class AssemblyPathResult
    {
        internal readonly string Status;
        internal readonly string Path;
        internal readonly CatalogIssue Issue;
        internal AssemblyPathResult(string status, string path, CatalogIssue issue) { Status = status; Path = path; Issue = issue; }
    }
    internal sealed class TypeEnumeration
    {
        internal Type[] Types = Array.Empty<Type>();
        internal bool Partial;
        internal readonly List<CatalogIssue> Issues = new List<CatalogIssue>();
    }
    internal sealed class TypeResolution
    {
        internal string Status = "not-found";
        internal List<Type> Candidates = new List<Type>();
        internal bool Partial;
        internal long Generation;
        internal readonly List<CatalogIssue> Issues = new List<CatalogIssue>();
        internal Type Type => Status == "unique" ? Candidates[0] : null;
    }
    internal sealed class CatalogIssue
    {
        internal readonly string Category;
        internal readonly string AssemblyName;
        internal readonly string Detail;
        internal CatalogIssue(string category, string assemblyName, string detail) { Category = category; AssemblyName = assemblyName; Detail = detail; }
    }
}
