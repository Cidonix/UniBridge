using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>Native requests are observed once; a timeout never cancels or reissues them.</summary>
    internal sealed class PackageManagementService
    {
        readonly IUnityPackageManager backend;
        IPackageRequest activeRequest;
        readonly object admission = new object();
        static readonly Regex NamePattern = new Regex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)+$", RegexOptions.CultureInvariant);
        static readonly Regex VersionPattern = new Regex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);

        public PackageManagementService(IUnityPackageManager backend) { this.backend = backend ?? throw new ArgumentNullException(nameof(backend)); }
        internal static bool IsPackageName(string value) => value != null && value.Length <= 256 && NamePattern.IsMatch(value);
        internal static bool TryExactId(string value, out string name, out string version)
        {
            name = null; version = null;
            if (value == null || value.Length > 512) return false;
            var parts = value.Split('@');
            if (parts.Length != 2 || !IsPackageName(parts[0])) return false;
            var match = VersionPattern.Match(parts[1]);
            if (!match.Success) return false;
            if (match.Groups[4].Success)
                foreach (var identifier in match.Groups[4].Value.Split('.'))
                    if (identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsDigit)) return false;
            name = parts[0]; version = parts[1]; return true;
        }

        public async Task<object> Execute(ManagePackagesParams input, CancellationToken cancellationToken)
        {
            input = input ?? new ManagePackagesParams();
            var result = new PackageOperationData
            {
                Action = input.Action.ToString(), Status = "blocked", OperationId = Guid.NewGuid().ToString("N"),
                PackageName = Clip(input.PackageName, 256), RequestedPackageId = Clip(input.PackageId, 512), SampleIndex = input.SampleIndex
            };
            bool mutation = input.Action == ManagePackagesAction.Add || input.Action == ManagePackagesAction.ImportSample;
            Func<string, string, object> error = (code, detail) =>
            {
                result.Detail = Clip(detail); result.Editor = backend.ObserveEditor(mutation);
                return Response.Error(code, result);
            };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Enum.IsDefined(typeof(ManagePackagesAction), input.Action)) return error("INVALID_ACTION", "Unknown package action.");
                if (input.Query != null && input.Query.Length > 256) return error("INVALID_QUERY", "Query is limited to 256 characters.");
                if (input.MaxResults.HasValue && (input.MaxResults < 1 || input.MaxResults > 100)) return error("INVALID_LIMIT", "MaxResults must be 1..100.");
                if (input.TimeoutMs.HasValue && (input.TimeoutMs < 1000 || input.TimeoutMs > 120000)) return error("INVALID_TIMEOUT", "TimeoutMs must be 1000..120000.");
                if (input.MaxSampleFiles.HasValue && (input.MaxSampleFiles < 1 || input.MaxSampleFiles > 4000)) return error("INVALID_SAMPLE_BUDGET", "MaxSampleFiles must be 1..4000.");
                if (input.MaxSampleBytes.HasValue && (input.MaxSampleBytes < 1 || input.MaxSampleBytes > 67108864)) return error("INVALID_SAMPLE_BUDGET", "MaxSampleBytes must be 1..67108864.");
                string addName = null, addVersion = null;
                if (input.Action == ManagePackagesAction.Add && !TryExactId(input.PackageId, out addName, out addVersion))
                    return error("EXACT_REGISTRY_VERSION_REQUIRED", "Use a registry package@exact-semver; paths, URLs, tags and ranges are not accepted.");
                if (input.Action == ManagePackagesAction.Add) result.PackageName = addName;
                if ((input.Action == ManagePackagesAction.Inspect || input.Action == ManagePackagesAction.ListSamples || input.Action == ManagePackagesAction.ImportSample) && !IsPackageName(input.PackageName))
                    return error("INVALID_PACKAGE_NAME", "A registry-format PackageName is required.");
                if (input.Action == ManagePackagesAction.ImportSample && (!input.SampleIndex.HasValue || input.SampleIndex < 0))
                    return error("INVALID_SAMPLE_INDEX", "ImportSample requires the nonnegative sample index from ListSamples.");
                var state = backend.ObserveEditor(mutation);
                if (mutation && (state.IsCompiling || state.IsUpdating || state.IsPlaying))
                    return error("EDITOR_NOT_READY", "Wait for Editor compilation/import and Play Mode transition to finish before changing packages.");
                bool offline = input.Offline ?? input.Action != ManagePackagesAction.Search;
                IPackageRequest request;
                lock (admission)
                {
                    if (activeRequest != null && !activeRequest.IsCompleted)
                        return error("PACKAGE_MANAGER_BUSY", "An earlier native request is still running; it was not canceled or replayed.");
                    result.MutationAttempted = input.Action == ManagePackagesAction.Add;
                    request = input.Action == ManagePackagesAction.Add ? backend.Add(input.PackageId) :
                        input.Action == ManagePackagesAction.Search ? backend.Search(IsPackageName(input.Query) ? input.Query : null, offline) : backend.List(offline);
                    activeRequest = request ?? throw new InvalidOperationException("Package Manager did not return a native request.");
                    result.MutationStarted = input.Action == ManagePackagesAction.Add;
                }
                var deadline = backend.Now + (input.TimeoutMs ?? 30000) / 1000d;
                // UPM offers no supported cancel API. After Add is issued, retain its real outcome.
                var observationToken = mutation ? CancellationToken.None : cancellationToken;
                while (!request.IsCompleted)
                {
                    observationToken.ThrowIfCancellationRequested();
                    if (backend.Now >= deadline)
                    {
                        result.Status = "native_request_in_progress"; result.NativeRequestMayContinue = true;
                        return error("PACKAGE_REQUEST_TIMEOUT", "Observation timed out. The native request may still finish; inspect installed state and Editor readiness instead of replaying installation.");
                    }
                    await backend.Delay(100, observationToken);
                }
                result.RequestCompleted = true;
                if (!request.Succeeded)
                {
                    result.Status = "native_request_failed";
                    return error("PACKAGE_REQUEST_FAILED", request.Error ?? "Native Package Manager request failed.");
                }
                var packages = request.Packages ?? Array.Empty<PackageInfoData>();
                if (input.Action == ManagePackagesAction.Add)
                {
                    result.Packages = packages.Take(1).ToArray(); result.TotalCount = packages.Length; result.ReturnedCount = result.Packages.Length;
                    result.Truncated = result.TotalCount > result.ReturnedCount;
                    if (packages.Length != 1 || packages[0].Name != addName || packages[0].Version != addVersion || packages[0].Source != "Registry")
                    {
                        result.Status = "native_result_mismatch";
                        return error("PACKAGE_RESULT_MISMATCH", "Native result did not confirm the requested registry name and exact version; the project may already have changed.");
                    }
                    result.Status = "native_request_succeeded";
                    result.Detail = "Exact registry version was reported by UPM. Editor readiness requires separate follow-up after package registration or domain reload.";
                }
                else if (input.Action == ManagePackagesAction.List || input.Action == ManagePackagesAction.Search)
                {
                    var query = input.Query ?? string.Empty;
                    var selected = packages.Where(package => Contains(package.Name, query) || Contains(package.DisplayName, query)).OrderBy(package => package.Name, StringComparer.Ordinal).ToArray();
                    result.TotalCount = selected.Length; result.Packages = selected.Take(input.MaxResults ?? 25).ToArray();
                    result.ReturnedCount = result.Packages.Length; result.Truncated = result.TotalCount > result.ReturnedCount;
                    result.Status = "read_succeeded";
                }
                else
                {
                    var package = packages.SingleOrDefault(value => value.Name == input.PackageName);
                    if (package == null) return error("PACKAGE_NOT_INSTALLED", "The requested package was not present in the completed installed-package list.");
                    result.Packages = new[] { package };
                    if (input.Action == ManagePackagesAction.Inspect)
                    {
                        result.TotalCount = result.ReturnedCount = 1; result.Status = "read_succeeded";
                    }
                    else
                    {
                        var samples = backend.Samples(package.Name, package.Version) ?? Array.Empty<PackageSampleHandle>();
                        if (input.Action == ManagePackagesAction.ListSamples)
                        {
                            result.TotalCount = samples.Length; result.Samples = samples.Take(input.MaxResults ?? 25).Select(value => value.Data).ToArray();
                            result.ReturnedCount = result.Samples.Length; result.Truncated = result.ReturnedCount != result.TotalCount; result.Status = "read_succeeded";
                        }
                        else
                        {
                            var sample = samples.SingleOrDefault(value => value.Data.Index == input.SampleIndex.Value);
                            if (sample == null) return error("SAMPLE_NOT_FOUND", "Sample index is absent from this installed package version.");
                            result.Samples = new[] { sample.Data }; result.TotalCount = result.ReturnedCount = 1;
                            var plan = PackageSampleImportGuard.Prepare(backend, package, sample.Data, input.MaxSampleFiles ?? 1000, input.MaxSampleBytes ?? 16777216);
                            result.ImportPath = plan.Target; result.SourceBytes = plan.Bytes;
                            observationToken.ThrowIfCancellationRequested();
                            PackageSampleImportGuard.CheckDestinationStillAbsent(plan.Target, backend.ProjectRoot);
                            result.MutationAttempted = true; result.MutationStarted = true;
                            if (!sample.Import())
                            {
                                result.Status = "sample_import_failed";
                                return error("SAMPLE_IMPORT_FAILED", "Native Sample.Import returned false. No retry or overwrite was attempted; inspect any partial destination.");
                            }
                            while (backend.ObserveEditor(true).IsUpdating)
                            {
                                if (backend.Now >= deadline)
                                {
                                    result.Status = "sample_import_unverified";
                                    return error("SAMPLE_IMPORT_TIMEOUT", "Sample copy was issued, but import readiness did not settle within the observation timeout. Do not replay the import.");
                                }
                                await backend.Delay(100, CancellationToken.None);
                            }
                            result.VerifiedFileCount = PackageSampleImportGuard.Verify(plan, backend);
                            sample.Data.Imported = true;
                            result.Status = "sample_import_verified";
                            result.Detail = "All declared source files and metadata GUIDs were verified at the new destination. Editor readiness remains a separate observation.";
                        }
                    }
                }
                result.Editor = backend.ObserveEditor(mutation);
                return Response.Success("Package operation observed: " + result.Status + ".", result);
            }
            catch (OperationCanceledException) { throw; }
            catch (PackageSampleBlockedException errorValue)
            {
                if (result.MutationStarted) result.Status = "sample_import_unverified";
                return error(errorValue.Code, errorValue.Message);
            }
            catch (Exception errorValue)
            {
                result.NativeRequestMayContinue = result.MutationAttempted && input.Action == ManagePackagesAction.Add && !result.RequestCompleted;
                result.Status = result.MutationStarted ? "mutation_unverified" : result.MutationAttempted ? "dispatch_unconfirmed" : "failed";
                return error("PACKAGE_OPERATION_FAILED", errorValue.Message);
            }
        }
        static bool Contains(string value, string query) => (value ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        static string Clip(string value) => value == null || value.Length <= 2048 ? value : value.Substring(0, 2048);
        static string Clip(string value, int maximum) => value == null || value.Length <= maximum ? value : value.Substring(0, maximum);
    }
}
