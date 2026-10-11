using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cidonix.UniBridge.Toolkit;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.PackageManager.UI;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal sealed class UnityPackageManagerAdapter : IUnityPackageManager
    {
        public string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public double Now => EditorApplication.timeSinceStartup;
        public Task Delay(int milliseconds, CancellationToken token) => EditorTask.Delay(milliseconds, token);
        public PackageEditorObservation ObserveEditor(bool mutation) => new PackageEditorObservation
        {
            ObservedUtc = DateTime.UtcNow.ToString("o"), IsCompiling = EditorApplication.isCompiling,
            IsUpdating = EditorApplication.isUpdating, IsPlaying = EditorApplication.isPlayingOrWillChangePlaymode,
            IdleObserved = !EditorApplication.isCompiling && !EditorApplication.isUpdating,
            // An idle observation cannot promise that no later package registration/reload is pending.
            ReadinessRequiresFollowUp = mutation
        };
        public IPackageRequest List(bool offline)
        {
            var request = Client.List(offline, true);
            return new NativeRequest(request, () => request.Result == null ? Array.Empty<PackageInfoData>() : request.Result.Select(Project).ToArray());
        }
        public IPackageRequest Search(string exactNameOrNull, bool offline)
        {
            var request = exactNameOrNull == null ? Client.SearchAll(offline) : Client.Search(exactNameOrNull, offline);
            return new NativeRequest(request, () => request.Result == null ? Array.Empty<PackageInfoData>() : request.Result.Select(Project).ToArray());
        }
        public IPackageRequest Add(string exactId)
        {
            var request = Client.Add(exactId);
            return new NativeRequest(request, () => request.Result == null ? Array.Empty<PackageInfoData>() : new[] { Project(request.Result) });
        }
        public PackageSampleHandle[] Samples(string packageName, string version)
        {
            return Sample.FindByPackage(packageName, version).Select((sample, index) => new PackageSampleHandle
            {
                Data = new PackageSampleData
                {
                    Index = index, DisplayName = Clip(sample.displayName, 256), Description = Clip(sample.description, 1024),
                    ResolvedPath = sample.resolvedPath, ImportPath = sample.importPath, Imported = sample.isImported,
                    InteractiveImport = sample.interactiveImport
                },
                // No override option: existing imports and author destinations remain protected.
                Import = () => sample.Import(Sample.ImportOptions.HideImportWindow)
            }).ToArray();
        }
        public string ResolveGuid(string guid) => AssetDatabase.GUIDToAssetPath(guid);
        static PackageInfoData Project(UnityEditor.PackageManager.PackageInfo package)
        {
            var versions = package.versions?.compatible ?? Array.Empty<string>();
            var dependencies = package.dependencies ?? Array.Empty<DependencyInfo>();
            return new PackageInfoData
            {
                Name = Clip(package.name, 256), DisplayName = Clip(package.displayName, 256), Version = Clip(package.version, 128),
                Source = package.source.ToString(), ResolvedPath = package.resolvedPath, DirectDependency = package.isDirectDependency,
                Description = Clip(package.description, 1024), LatestCompatible = Clip(package.versions?.latestCompatible, 128),
                CompatibleVersions = versions.Take(30).Select(value => Clip(value, 128)).ToArray(), CompatibleVersionCount = versions.Length,
                Dependencies = dependencies.Take(60).Select(value => new PackageDependencyData { Name = Clip(value.name, 256), Version = Clip(value.version, 128) }).ToArray(),
                DependencyCount = dependencies.Length
            };
        }
        static string Clip(string value, int maximum) => value == null || value.Length <= maximum ? value : value.Substring(0, maximum);
        sealed class NativeRequest : IPackageRequest
        {
            readonly Request request;
            readonly Func<PackageInfoData[]> result;
            public NativeRequest(Request request, Func<PackageInfoData[]> result) { this.request = request; this.result = result; }
            public bool IsCompleted => request.IsCompleted;
            public bool Succeeded => IsCompleted && request.Status == StatusCode.Success;
            public string Error => Clip(request.Error?.message, 2048);
            public PackageInfoData[] Packages => Succeeded ? result() : Array.Empty<PackageInfoData>();
        }
    }
}
