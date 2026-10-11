using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    public sealed class PackageDependencyData
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("version")] public string Version;
    }
    public sealed class PackageInfoData
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("displayName")] public string DisplayName;
        [JsonProperty("version")] public string Version;
        [JsonProperty("source")] public string Source;
        [JsonProperty("resolvedPath")] public string ResolvedPath;
        [JsonProperty("directDependency")] public bool DirectDependency;
        [JsonProperty("description")] public string Description;
        [JsonProperty("latestCompatible")] public string LatestCompatible;
        [JsonProperty("compatibleVersions")] public string[] CompatibleVersions = Array.Empty<string>();
        [JsonProperty("compatibleVersionCount")] public int CompatibleVersionCount;
        [JsonProperty("dependencies")] public PackageDependencyData[] Dependencies = Array.Empty<PackageDependencyData>();
        [JsonProperty("dependencyCount")] public int DependencyCount;
    }
    public sealed class PackageSampleData
    {
        [JsonProperty("index")] public int Index;
        [JsonProperty("displayName")] public string DisplayName;
        [JsonProperty("description")] public string Description;
        [JsonProperty("resolvedPath")] public string ResolvedPath;
        [JsonProperty("importPath")] public string ImportPath;
        [JsonProperty("imported")] public bool Imported;
        [JsonProperty("interactiveImport")] public bool InteractiveImport;
    }
    public sealed class PackageEditorObservation
    {
        [JsonProperty("observedUtc")] public string ObservedUtc;
        [JsonProperty("isCompiling")] public bool IsCompiling;
        [JsonProperty("isUpdating")] public bool IsUpdating;
        [JsonProperty("isPlaying")] public bool IsPlaying;
        [JsonProperty("idleObserved")] public bool IdleObserved;
        [JsonProperty("readinessRequiresFollowUp")] public bool ReadinessRequiresFollowUp;
    }
    public sealed class PackageOperationData
    {
        [JsonProperty("schema")] public string Schema = "unibridge.packages.v1";
        [JsonProperty("action")] public string Action;
        [JsonProperty("status")] public string Status;
        [JsonProperty("operationId")] public string OperationId;
        [JsonProperty("packageName")] public string PackageName;
        [JsonProperty("requestedPackageId")] public string RequestedPackageId;
        [JsonProperty("requestCompleted")] public bool RequestCompleted;
        [JsonProperty("mutationAttempted")] public bool MutationAttempted;
        [JsonProperty("mutationStarted")] public bool MutationStarted;
        [JsonProperty("nativeRequestMayContinue")] public bool NativeRequestMayContinue;
        [JsonProperty("totalCount")] public int TotalCount;
        [JsonProperty("returnedCount")] public int ReturnedCount;
        [JsonProperty("truncated")] public bool Truncated;
        [JsonProperty("packages")] public PackageInfoData[] Packages = Array.Empty<PackageInfoData>();
        [JsonProperty("samples")] public PackageSampleData[] Samples = Array.Empty<PackageSampleData>();
        [JsonProperty("sampleIndex")] public int? SampleIndex;
        [JsonProperty("importPath")] public string ImportPath;
        [JsonProperty("verifiedFileCount")] public int VerifiedFileCount;
        [JsonProperty("sourceBytes")] public long SourceBytes;
        [JsonProperty("detail")] public string Detail;
        [JsonProperty("editor")] public PackageEditorObservation Editor;
    }
    internal interface IPackageRequest
    {
        bool IsCompleted { get; }
        bool Succeeded { get; }
        string Error { get; }
        PackageInfoData[] Packages { get; }
    }
    internal sealed class PackageSampleHandle
    {
        public PackageSampleData Data;
        public Func<bool> Import;
    }
    internal interface IUnityPackageManager
    {
        string ProjectRoot { get; }
        double Now { get; }
        PackageEditorObservation ObserveEditor(bool mutation);
        Task Delay(int milliseconds, CancellationToken token);
        IPackageRequest List(bool offline);
        IPackageRequest Search(string exactNameOrNull, bool offline);
        IPackageRequest Add(string exactId);
        PackageSampleHandle[] Samples(string packageName, string version);
        string ResolveGuid(string guid);
    }
}
