using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PackageManagementRegression
{
    static class Regression
    {
        static readonly List<object> Cases = new List<object>();
        static readonly List<object> Responses = new List<object>();
        static string root;
        static int failures;
        static void Check(string name, Func<Task<bool>> action)
        {
            bool passed = false; string error = null;
            try { passed = action().GetAwaiter().GetResult(); } catch (Exception e) { error = e.ToString(); }
            Cases.Add(new { name, passed, error }); if (!passed) failures++;
            Console.WriteLine((passed ? "PASS " : "FAIL ") + name + (error == null ? "" : ": " + error));
        }
        static JObject Token(object value)
        {
            return (JObject)McpJson.TokenFromObject(value);
        }
        static async Task<JObject> Call(PackageManagementService service, ManagePackagesParams parameters, CancellationToken token = default)
        {
            var response = Token(await service.Execute(parameters, token));
            Responses.Add(new { action = parameters.Action.ToString(), response });
            return response;
        }
        static PackageInfoData Package(string name = "com.cidonix.qualifier", string version = "1.2.3") => new PackageInfoData { Name = name, Version = version, DisplayName = name, Source = "Registry", ResolvedPath = Path.Combine(root, "PackageSource") };
        static bool Error(JObject value, string code) => !value.Value<bool>("success") && value.Value<string>("code") == code;
        public static int Main(string[] args)
        {
            root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
            Check("exact-semver-supported", () => Task.FromResult(PackageManagementService.TryExactId("com.unity.inputsystem@1.20.0-pre.2+build.1", out var name, out var version) && name == "com.unity.inputsystem" && version == "1.20.0-pre.2+build.1"));
            foreach (var id in new[] { "com.unity.inputsystem@latest", "com.unity.inputsystem@^1.0.0", "com.unity.inputsystem@1.0", "com.unity.inputsystem@01.0.0", "com.unity.inputsystem@1.0.0-01", "https://example.invalid/package.git", "file:../package", "com.unity.inputsystem@1.0.0@other" })
            {
                var capture = id;
                Check("reject-id-" + id, () => Task.FromResult(!PackageManagementService.TryExactId(capture, out var name, out var version)));
            }
            Check("bounded-installed-list-and-query", async () =>
            {
                var backend = new Backend(root); backend.Request = new Request { Items = new[] { Package("com.cidonix.alpha"), Package("com.cidonix.beta"), Package("com.other.third") } };
                var value = await Call(new PackageManagementService(backend), new ManagePackagesParams { Query = "cidonix", MaxResults = 1 });
                return value.Value<bool>("success") && value["data"].Value<int>("totalCount") == 2 && value["data"].Value<bool>("truncated") && ((JArray)value["data"]["packages"]).Count == 1 && backend.Offline;
            });
            Check("inspect-missing-package-truthful", async () => Error(await Call(new PackageManagementService(new Backend(root)), new ManagePackagesParams { Action = ManagePackagesAction.Inspect, PackageName = "com.cidonix.absent" }), "PACKAGE_NOT_INSTALLED"));
            Check("registry-exact-search-path", async () =>
            {
                var backend = new Backend(root);
                var value = await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.Search, Query = "com.cidonix.qualifier" });
                return value.Value<bool>("success") && backend.SearchName == "com.cidonix.qualifier" && !backend.Offline;
            });
            Check("registry-substring-search-all-path", async () =>
            {
                var backend = new Backend(root); await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.Search, Query = "qualifier" });
                return backend.SearchName == null && backend.SearchCalls == 1;
            });
            Check("add-exact-version-confirmed-not-ready-promise", async () =>
            {
                var backend = new Backend(root); var value = await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.Add, PackageId = "com.cidonix.qualifier@1.2.3" });
                return value.Value<bool>("success") && value["data"].Value<bool>("requestCompleted") && value["data"].Value<bool>("mutationStarted") && value["data"]["editor"].Value<bool>("readinessRequiresFollowUp") && backend.AddCalls == 1;
            });
            Check("add-result-version-mismatch-not-success", async () =>
            {
                var backend = new Backend(root); backend.Request.Items = new[] { Package(version: "1.2.4") };
                return Error(await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.Add, PackageId = "com.cidonix.qualifier@1.2.3" }), "PACKAGE_RESULT_MISMATCH");
            });
            Check("add-source-mismatch-not-success", async () =>
            {
                var backend = new Backend(root); backend.Request.Items[0].Source = "Embedded";
                return Error(await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.Add, PackageId = "com.cidonix.qualifier@1.2.3" }), "PACKAGE_RESULT_MISMATCH");
            });
            Check("native-failure-not-success", async () =>
            {
                var backend = new Backend(root); backend.Request.Success = false;
                return Error(await Call(new PackageManagementService(backend), new ManagePackagesParams()), "PACKAGE_REQUEST_FAILED");
            });
            Check("native-add-dispatch-exception-truthful-unconfirmed", async () =>
            {
                var backend = new Backend(root) { ThrowAdd = true };
                var value = await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.Add, PackageId = "com.cidonix.qualifier@1.2.3" });
                return Error(value, "PACKAGE_OPERATION_FAILED") && value["data"].Value<bool>("mutationAttempted") && value["data"].Value<bool>("nativeRequestMayContinue") && !value["data"].Value<bool>("requestCompleted") && value["data"].Value<string>("status") == "dispatch_unconfirmed";
            });
            Check("timeout-keeps-request-running-and-blocks-replay", async () =>
            {
                var backend = new Backend(root); backend.Request.Complete = false; var service = new PackageManagementService(backend);
                var parameters = new ManagePackagesParams { Action = ManagePackagesAction.Add, PackageId = "com.cidonix.qualifier@1.2.3", TimeoutMs = 1000 };
                var first = await Call(service, parameters); var second = await Call(service, parameters);
                return Error(first, "PACKAGE_REQUEST_TIMEOUT") && first["data"].Value<bool>("nativeRequestMayContinue") && !first["data"].Value<bool>("requestCompleted") && Error(second, "PACKAGE_MANAGER_BUSY") && backend.AddCalls == 1;
            });
            Check("started-add-retains-real-result-after-cancel", async () =>
            {
                var backend = new Backend(root); backend.Request.Complete = false; using var cancellation = new CancellationTokenSource();
                backend.OnDelay = () => { cancellation.Cancel(); backend.Request.Complete = true; };
                var value = await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.Add, PackageId = "com.cidonix.qualifier@1.2.3" }, cancellation.Token);
                return value.Value<bool>("success") && backend.AddCalls == 1;
            });
            Check("queued-cancel-does-not-dispatch", async () =>
            {
                var backend = new Backend(root); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.Add, PackageId = "com.cidonix.qualifier@1.2.3" }, cancellation.Token); return false; }
                catch (OperationCanceledException) { return backend.AddCalls == 0; }
            });
            Check("read-cancel-propagates-and-does-not-install", async () =>
            {
                var backend = new Backend(root); backend.Request.Complete = false; using var cancellation = new CancellationTokenSource(); backend.OnDelay = cancellation.Cancel;
                try { await Call(new PackageManagementService(backend), new ManagePackagesParams(), cancellation.Token); return false; }
                catch (OperationCanceledException) { return backend.AddCalls == 0 && backend.ListCalls == 1; }
            });
            Check("invalid-fields-do-not-dispatch", async () =>
            {
                var backend = new Backend(root); var service = new PackageManagementService(backend);
                var first = await Call(service, new ManagePackagesParams { Action = ManagePackagesAction.Add, PackageId = "file:../elsewhere" });
                var second = await Call(service, new ManagePackagesParams { TimeoutMs = 1 });
                return Error(first, "EXACT_REGISTRY_VERSION_REQUIRED") && Error(second, "INVALID_TIMEOUT") && backend.AddCalls == 0 && backend.ListCalls == 0;
            });
            Check("unknown-typed-action-returns-closed-error-before-dispatch", async () =>
            {
                var backend = new Backend(root);
                return Error(await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = (ManagePackagesAction)123 }), "INVALID_ACTION") && backend.ListCalls == 0 && backend.AddCalls == 0;
            });
            Check("editor-busy-blocks-add-before-dispatch", async () =>
            {
                var backend = new Backend(root); backend.Busy = true;
                return Error(await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.Add, PackageId = "com.cidonix.qualifier@1.2.3" }), "EDITOR_NOT_READY") && backend.AddCalls == 0;
            });
            Check("sample-list-bounded-index-stable", async () =>
            {
                var backend = SampleBackend("list"); backend.Handles = new[] { backend.Handles[0], new PackageSampleHandle { Data = new PackageSampleData { Index = 1, DisplayName = "Second" } } };
                var value = await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.ListSamples, PackageName = "com.cidonix.qualifier", MaxResults = 1 });
                return value.Value<bool>("success") && value["data"].Value<int>("totalCount") == 2 && value["data"].Value<bool>("truncated") && value["data"]["samples"][0].Value<int>("index") == 0;
            });
            Check("sample-copy-byte-and-guid-proof", async () =>
            {
                var backend = SampleBackend("success"); var value = await Import(backend);
                return value.Value<bool>("success") && value["data"].Value<string>("status") == "sample_import_verified" && value["data"].Value<int>("verifiedFileCount") == 2 && backend.ImportCalls == 1;
            });
            Check("existing-author-destination-never-touched", async () =>
            {
                var backend = SampleBackend("existing"); var target = backend.Handles[0].Data.ImportPath; Directory.CreateDirectory(target); var author = Path.Combine(target, "author.txt"); File.WriteAllText(author, "author bytes");
                return Error(await Import(backend), "SAMPLE_DESTINATION_EXISTS") && File.ReadAllText(author) == "author bytes" && backend.ImportCalls == 0;
            });
            Check("existing-destination-meta-blocks-import", async () =>
            {
                var backend = SampleBackend("meta"); var target = backend.Handles[0].Data.ImportPath; Directory.CreateDirectory(Path.GetDirectoryName(target)); File.WriteAllText(target + ".meta", "author meta");
                return Error(await Import(backend), "SAMPLE_DESTINATION_EXISTS") && File.ReadAllText(target + ".meta") == "author meta" && backend.ImportCalls == 0;
            });
            Check("sample-guid-collision-blocked-before-copy", async () =>
            {
                var backend = SampleBackend("guid-conflict"); backend.GuidPaths[backend.SampleGuid] = "Assets/Author.asset";
                return Error(await Import(backend), "SAMPLE_GUID_CONFLICT") && backend.ImportCalls == 0;
            });
            Check("source-reparse-directory-blocked-before-copy", async () =>
            {
                var backend = SampleBackend("source-reparse"); var external = Path.Combine(backend.ProjectRoot, "OwnedExternal"); Directory.CreateDirectory(external);
                Directory.CreateSymbolicLink(Path.Combine(backend.Handles[0].Data.ResolvedPath, "escape"), external);
                return Error(await Import(backend), "UNSAFE_SAMPLE_SOURCE") && backend.ImportCalls == 0;
            });
            Check("sample-outside-assets-blocked", async () =>
            {
                var backend = SampleBackend("escape"); backend.Handles[0].Data.ImportPath = Path.Combine(backend.ProjectRoot, "Elsewhere");
                return Error(await Import(backend), "UNSAFE_SAMPLE_DESTINATION") && backend.ImportCalls == 0;
            });
            Check("interactive-sample-blocked", async () =>
            {
                var backend = SampleBackend("interactive"); backend.Handles[0].Data.InteractiveImport = true;
                return Error(await Import(backend), "INTERACTIVE_SAMPLE_UNSUPPORTED") && backend.ImportCalls == 0;
            });
            Check("sample-byte-budget-before-copy", async () =>
            {
                var backend = SampleBackend("byte-budget"); return Error(await Import(backend, maxBytes: 1), "SAMPLE_BUDGET_EXCEEDED") && backend.ImportCalls == 0;
            });
            Check("sample-file-budget-before-copy", async () =>
            {
                var backend = SampleBackend("file-budget"); return Error(await Import(backend, maxFiles: 1), "SAMPLE_BUDGET_EXCEEDED") && backend.ImportCalls == 0;
            });
            Check("native-import-false-not-success", async () =>
            {
                var backend = SampleBackend("false"); backend.Handles[0].Import = () => { backend.ImportCalls++; return false; };
                return Error(await Import(backend), "SAMPLE_IMPORT_FAILED") && backend.ImportCalls == 1;
            });
            Check("sample-import-timeout-never-success-or-retry", async () =>
            {
                var backend = SampleBackend("sample-timeout"); var normal = backend.Handles[0].Import;
                backend.Handles[0].Import = () => { normal(); backend.Busy = true; return true; };
                var value = await Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.ImportSample, PackageName = "com.cidonix.qualifier", SampleIndex = 0, TimeoutMs = 1000 });
                return Error(value, "SAMPLE_IMPORT_TIMEOUT") && value["data"].Value<bool>("mutationStarted") && value["data"].Value<bool>("requestCompleted") && value["data"].Value<string>("status") == "sample_import_unverified" && backend.ImportCalls == 1;
            });
            Check("native-import-corrupt-bytes-not-success", async () =>
            {
                var backend = SampleBackend("corrupt"); var normal = backend.Handles[0].Import;
                backend.Handles[0].Import = () => { normal(); File.WriteAllText(Path.Combine(backend.Handles[0].Data.ImportPath, "sample.txt"), "corrupt"); return true; };
                var value = await Import(backend); return Error(value, "SAMPLE_IMPORT_MISMATCH") && value["data"].Value<bool>("mutationStarted") && value["data"].Value<string>("status") == "sample_import_unverified";
            });
            Check("assetdatabase-guid-unverified-not-success", async () =>
            {
                var backend = SampleBackend("unverified-guid"); var normal = backend.Handles[0].Import;
                backend.Handles[0].Import = () => { normal(); backend.GuidPaths.Clear(); return true; };
                return Error(await Import(backend), "SAMPLE_GUID_UNVERIFIED");
            });
            Check("closed-output-family-builds-with-real-mcp-math-serializer", () => Task.FromResult(PackageActionOutputRules.Build().Count == 6 && McpJson.CreateSerializer().Converters.Count > 0));
            Check("packages-whole-tool-not-readonly-or-replay-safe", () =>
            {
                var value = Token(ToolExecutionScheduler.BuildAnnotation("UniBridge_ManagePackages", null));
                return Task.FromResult(!value.Value<bool>("readOnly") && !value.Value<bool>("replaySafe"));
            });
            var policies = new JObject();
            foreach (var action in new[] { "", "List", "Inspect", "Search", "Add", "ListSamples", "ImportSample", "0", "1", "2", "3", "4", "5", "unknown" })
                policies[action] = ToolExecutionScheduler.ResolvePolicy("UniBridge_ManagePackages", new JObject { ["Action"] = action }).ToString();
            Check("packages-policy-read-mutate-reload-numeric-and-unknown", () => Task.FromResult(
                policies.Value<string>("List") == "ReadOnly" && policies.Value<string>("Inspect") == "ReadOnly" && policies.Value<string>("Search") == "ReadOnly" && policies.Value<string>("ListSamples") == "ReadOnly" &&
                policies.Value<string>("Add") == "CompileReload" && policies.Value<string>("ImportSample") == "Mutating" && policies.Value<string>("3") == "CompileReload" && policies.Value<string>("5") == "Mutating" && policies.Value<string>("unknown") == "CompileReload"));
            Check("packages-policy-matches-last-non-null-typed-action", () =>
            {
                // The same native Json.NET enum/null settings used by tool handlers.
                var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, MissingMemberHandling = MissingMemberHandling.Ignore };
                settings.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter());
                var inputs = new[]
                {
                    new JObject { ["Action"] = "List", ["action"] = "Add" },
                    new JObject { ["action"] = "Add", ["Action"] = "List" },
                    new JObject { ["ACTION"] = " ImportSample ", ["Action"] = JValue.CreateNull() },
                    new JObject { ["Action"] = JValue.CreateNull(), ["aCtIoN"] = " list " },
                    new JObject { ["Action"] = 3, ["action"] = JValue.CreateNull() },
                    new JObject { ["Operation"] = "Add" },
                    new JObject { ["Action"] = JValue.CreateNull() }
                };
                return Task.FromResult(inputs.All(input =>
                {
                    var typed = input.ToObject<ManagePackagesParams>(JsonSerializer.Create(settings));
                    var expected = typed.Action == ManagePackagesAction.Add ? ToolExecutionPolicy.CompileReload :
                        typed.Action == ManagePackagesAction.ImportSample ? ToolExecutionPolicy.Mutating : ToolExecutionPolicy.ReadOnly;
                    return ToolExecutionScheduler.ResolvePolicy("UniBridge_ManagePackages", input) == expected;
                }));
            });
            Check("packages-invalid-action-conservatively-exclusive", () => Task.FromResult(
                new JToken[] { new JObject(), new JArray(), new JValue(true), new JValue(1.5), new JValue(""), new JValue("999") }.All(value =>
                    ToolExecutionScheduler.ResolvePolicy("UniBridge_ManagePackages", new JObject { ["Action"] = value }) == ToolExecutionPolicy.CompileReload)));
            var contract = OutputContractProvider.Resolve("UniBridge_ManagePackages", typeof(ManagePackages).GetMethod("HandleCommand"));
            Check("actual-package-provider-source-action-contract-not-fallback", () => Task.FromResult(contract.HasSchema && contract.Fidelity == "source_action_contract" && contract.WholeActionDataQualified && contract.ActionSchemas.Count == 6));
            Check("actual-default-action-selector-list", () => Task.FromResult(ActionOutputValidation.SelectAction(contract, new JObject()) == "List"));
            Check("actual-package-enum-name-case-and-numeric-selectors", () => Task.FromResult(ActionOutputValidation.SelectAction(contract, new JObject { ["Action"] = "importsample" }) == "ImportSample" && ActionOutputValidation.SelectAction(contract, new JObject { ["Action"] = 3 }) == "Add"));
            Check("actual-unknown-selector-does-not-fallback-to-list", () => Task.FromResult(ActionOutputValidation.SelectAction(contract, new JObject { ["Action"] = "unknown" }) == null && contract.UnknownActionUsesDefault == false));
            var registeredResponses = new List<object>();
            foreach (dynamic row in Responses)
            {
                var contextualized = (JObject)row.response.DeepClone();
                contextualized["projectContext"] = new JObject { ["id"] = "owned-portable-project", ["name"] = "Owned Portable Project", ["root"] = root, ["assetsPath"] = Path.Combine(root, "Assets"), ["settingsPath"] = Path.Combine(root, "ProjectSettings") };
                registeredResponses.Add(new { action = (string)row.action, response = contextualized });
            }
            Check("actual-action-output-validator-accepts-every-contextualized-service-response", () => Task.FromResult(registeredResponses.Cast<dynamic>().All(row =>
                ActionOutputValidation.Completed(row.response, "UniBridge_ManagePackages", new JObject { ["Action"] = row.action }, contract) == row.response)));
            var schemas = new JObject();
            foreach (var pair in PackageActionOutputRules.Build()) schemas[pair.Key] = ActionOutputSchema.Compose(new[] { pair.Value });
            var output = new { passed = failures == 0, failures, cases = Cases, responses = Responses, outputSchemas = schemas,
                unknownActionSchema = ActionOutputSchema.Compose(new[] { PackageActionOutputRules.UnknownAction() }),
                inputSchema = ManagePackages.GetInputSchema(), policies, registeredOutputSchema = contract.Schema, registeredResponses,
                proof = "Whole production service/guard/models/input tool/policy/output schema with actual McpJson/math converter; deterministic native request/clock/editor/Sample.Import seams, real filesystem bytes/GUID checks. No Unity API execution or Editor launch." };
            File.WriteAllText(args[0], McpJson.SerializeObject(output, Formatting.Indented));
            return failures == 0 ? 0 : 1;
        }
        static Task<JObject> Import(Backend backend, int? maxFiles = null, long? maxBytes = null) => Call(new PackageManagementService(backend), new ManagePackagesParams { Action = ManagePackagesAction.ImportSample, PackageName = "com.cidonix.qualifier", SampleIndex = 0, MaxSampleFiles = maxFiles, MaxSampleBytes = maxBytes });
        static Backend SampleBackend(string label)
        {
            var targetRoot = Path.Combine(root, label); Directory.CreateDirectory(targetRoot);
            var backend = new Backend(targetRoot); var packageRoot = Path.Combine(targetRoot, "PackageSource"); var source = Path.Combine(packageRoot, "Samples~", "Demo"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "sample.txt"), "owned sample bytes"); backend.SampleGuid = Guid.NewGuid().ToString("N"); File.WriteAllText(Path.Combine(source, "sample.txt.meta"), "fileFormatVersion: 2\nguid: " + backend.SampleGuid + "\nDefaultImporter:\n  externalObjects: {}\n");
            backend.Request.Items[0].ResolvedPath = packageRoot;
            var target = Path.Combine(targetRoot, "Assets", "Samples", "Qualifier", "1.2.3", "Demo");
            var data = new PackageSampleData { Index = 0, DisplayName = "Demo", ResolvedPath = source, ImportPath = target };
            backend.Handles = new[] { new PackageSampleHandle { Data = data, Import = () =>
            {
                backend.ImportCalls++; Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
                backend.GuidPaths[backend.SampleGuid] = "Assets/Samples/Qualifier/1.2.3/Demo/sample.txt"; return true;
            } } };
            return backend;
        }
        sealed class Request : IPackageRequest
        {
            public bool Complete = true, Success = true;
            public PackageInfoData[] Items = new[] { Package() };
            public bool IsCompleted => Complete;
            public bool Succeeded => Complete && Success;
            public string Error => "Native failure fixture";
            public PackageInfoData[] Packages => Items;
        }
        sealed class Backend : IUnityPackageManager
        {
            public Backend(string root) { ProjectRoot = root; }
            public string ProjectRoot { get; }
            public double Now { get; private set; }
            public Request Request = new Request();
            public int AddCalls, ListCalls, SearchCalls, ImportCalls;
            public bool Offline, Busy, ThrowAdd;
            public string SearchName, SampleGuid;
            public Action OnDelay;
            public PackageSampleHandle[] Handles = Array.Empty<PackageSampleHandle>();
            public readonly Dictionary<string, string> GuidPaths = new Dictionary<string, string>();
            public PackageEditorObservation ObserveEditor(bool mutation) => new PackageEditorObservation { IsUpdating = Busy, IdleObserved = !Busy, ReadinessRequiresFollowUp = mutation, ObservedUtc = DateTime.UtcNow.ToString("o") };
            public Task Delay(int milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); Now += milliseconds / 1000d; OnDelay?.Invoke(); return Task.CompletedTask; }
            public IPackageRequest List(bool offline) { ListCalls++; Offline = offline; return Request; }
            public IPackageRequest Search(string name, bool offline) { SearchCalls++; SearchName = name; Offline = offline; return Request; }
            public IPackageRequest Add(string id) { AddCalls++; if (ThrowAdd) throw new IOException("Native dispatch fixture exception"); return Request; }
            public PackageSampleHandle[] Samples(string name, string version) => Handles;
            public string ResolveGuid(string guid) => GuidPaths.TryGetValue(guid, out var path) ? path : string.Empty;
        }
    }
}
