using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Cidonix.UniBridge.Relay;

// Links the exact production relay, forwarding all calls to the real Unity named
// pipe. Faults affect only this task-owned proxy transport, never the Editor's pipe.
static class LiveCommandReplayRegression
{
    const string Probe = "UniBridge_CommandReplayProbe";
    const string Status = "UniBridge_CommandReplayProbeStatus";
    const string UncertifiedRead = "UniBridge_CommandReplayUncertifiedRead";
    const string Cleanup = "UniBridge_CommandReplayProbeCleanup";
    const string ExpectedRootKey = "__unibridge_expected_project_root";
    static readonly List<JsonObject> Results = new();

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4)
            throw new ArgumentException("Expected realDiscoveryJson, projectRoot, reportJson and runId.");
        var discoveryPath = Path.GetFullPath(args[0]);
        var projectRoot = Path.GetFullPath(args[1]);
        var report = Path.GetFullPath(args[2]);
        var runId = args[3];
        var discovery = JsonNode.Parse(File.ReadAllText(discoveryPath))!.AsObject();
        Assert(string.Equals(NormalizeRoot(discovery["project_root"]!.GetValue<string>()), NormalizeRoot(projectRoot), StringComparison.OrdinalIgnoreCase), "Discovery does not identify the authorized test project.");
        Assert(File.Exists(Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt")), "Target is not a Unity project.");
        var pipePath = discovery["connection_path"]!.GetValue<string>();
        Assert(pipePath.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase), "Live qualification only supports the verified local named pipe.");
        var realPipe = pipePath[9..];
        JsonNode? before = null;
        JsonNode? after = null;
        JsonObject? cleanupResult = null;
        LiveProxy? proxy = null;
        var cleanupAttempted = false;
        using var suiteTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            proxy = await LiveProxy.StartAsync(discovery, projectRoot, realPipe, runId, suiteTimeout.Token);
            var initial = await proxy.CallAsync(Status, Parameters(runId), suiteTimeout.Token);
            before = Data(initial)["snapshot"]?.DeepClone();
            Assert(before != null, "Fixture status/snapshot unavailable; transient fixture must be compiled before running.");

            await CaseAsync("live_actual_tool_metadata_requires_audited_replay_certificate", async () =>
            {
                var response = await RawCallAsync(realPipe, new JsonObject
                {
                    ["type"] = "get_available_tools", ["params"] = new JsonObject(),
                    ["requestId"] = "live-metadata-" + Guid.NewGuid().ToString("N")
                }, suiteTimeout.Token);
                var tools = response["result"]?["tools"]?.AsArray()
                    ?? throw new InvalidOperationException("Actual Unity tool metadata response missing tools: " + response);
                var evidence = new JsonArray();
                foreach (var name in new[]
                {
                    "UniBridge_AssetIntelligence", "UniBridge_RuntimeProfiler", "UniBridge_RuntimeStateProbe",
                    "UniBridge_TypeSchema", "UniBridge_SceneObjectView", "UniBridge_ExecutionStatus",
                    "UniBridge_ReadConsole", "UniBridge_EditorEvents", "UniBridge_Discover", Status, UncertifiedRead
                })
                {
                    var tool = tools.OfType<JsonObject>().SingleOrDefault(t => t["name"]?.GetValue<string>() == name);
                    Assert(tool != null, "Expected actual Unity tool descriptor not advertised: " + name);
                    var execution = tool!["annotations"]?["uniBridgeExecution"]?.AsObject();
                    var replaySafe = execution?["replaySafe"]?.GetValue<bool>() == true;
                    var expected = name is "UniBridge_Discover" or Status;
                    Assert(replaySafe == expected, "Actual tool replay certificate mismatch for " + name + ": " + execution);
                    if (expected)
                        Assert(execution?["policy"]?.GetValue<string>() is "ReadOnly" or "Observer", "Replay certificate requires actual read-only/observer policy.");
                    evidence.Add(new JsonObject { ["tool"] = name, ["execution"] = execution?.DeepClone(), ["replaySafe"] = replaySafe });
                }
                return new JsonObject { ["toolCount"] = tools.Count, ["actualUnityMetadata"] = evidence };
            });

            JsonObject? completedOriginal = null;
            JsonObject? uncertifiedOriginal = null;
            string? originalSession = null;
            string? domainBeforeReload = null;
            string? domainAfterReload = null;
            int reloadEventsBefore = 0;
            int reloadEventsAfter = 0;
            await CaseAsync("live_completed_response_lost_no_replay", async () =>
            {
                const string caseId = "lost_completed";
                proxy.ArmDropAfterReply(Probe, caseId);
                var response = await proxy.CallAsync(Probe, Parameters(runId, caseId), suiteTimeout.Token);
                var command = proxy.Commands(Probe, caseId).Single();
                completedOriginal = command;
                originalSession = proxy.FirstSessionId;
                var state = await proxy.CallAsync(Status, Parameters(runId, caseId), suiteTimeout.Token);
                Assert(IsSuccess(response), "Dropped completed reply was not recovered: " + response);
                Assert(Data(state)["count"]?.GetValue<int>() == 1, "Lost response repeated the real mutation.");
                Assert(proxy.DropCount == 1 && proxy.Commands(Probe, caseId).Count == 1, "Fault did not drop exactly one completed reply or relay resent mutation.");
                return Evidence(response, state, command, proxy.Commands("recover_command").Count);
            });

            await CaseAsync("live_duplicate_same_id_returns_same_result", async () =>
            {
                Assert(completedOriginal != null, "Prerequisite completed operation missing.");
                var duplicate = await RawCallAsync(realPipe, completedOriginal!, suiteTimeout.Token);
                var state = await proxy.CallAsync(Status, Parameters(runId, "lost_completed"), suiteTimeout.Token);
                Assert(IsSuccess(duplicate), "Same-ID duplicate did not return original result: " + duplicate);
                Assert(Data(state)["count"]?.GetValue<int>() == 1, "Same-ID duplicate executed real mutation again.");
                return Evidence(duplicate, state, completedOriginal!, 0);
            });

            await CaseAsync("live_duplicate_same_id_different_arguments_refused", async () =>
            {
                Assert(completedOriginal != null, "Prerequisite completed operation missing.");
                var conflicting = completedOriginal!.DeepClone().AsObject();
                conflicting["params"]!["Mode"] = "ThrowAfterEffect";
                var response = await RawCallAsync(realPipe, conflicting, suiteTimeout.Token);
                var state = await proxy.CallAsync(Status, Parameters(runId, "lost_completed"), suiteTimeout.Token);
                Assert(response.ToJsonString().Contains("request_conflict", StringComparison.Ordinal), "Changed payload was not refused: " + response);
                Assert(Data(state)["count"]?.GetValue<int>() == 1, "Conflicting payload executed a second mutation.");
                return Evidence(response, state, conflicting, 0);
            });

            await CaseAsync("live_error_after_side_effect_recovered_without_replay", async () =>
            {
                const string caseId = "throw_after_effect";
                proxy.ArmDropAfterReply(Probe, caseId);
                var response = await proxy.CallAsync(Probe, Parameters(runId, caseId, "ThrowAfterEffect"), suiteTimeout.Token);
                var original = proxy.Commands(Probe, caseId).Single();
                var duplicate = await RawCallAsync(realPipe, original, suiteTimeout.Token);
                var state = await proxy.CallAsync(Status, Parameters(runId, caseId), suiteTimeout.Token);
                Assert(response["status"]?.GetValue<string>() == "error" && response.ToJsonString().Contains("Intentional regression exception", StringComparison.Ordinal), "Terminal error was not recovered intact: " + response);
                Assert(duplicate["status"]?.GetValue<string>() == "error", "Duplicate terminal error became success.");
                Assert(Data(state)["count"]?.GetValue<int>() == 1, "Throwing command repeated its already-completed side effect.");
                return Evidence(response, state, original, proxy.Commands("recover_command").Count);
            });

            await CaseAsync("live_in_flight_recovery_never_reenters_mutation", async () =>
            {
                const string caseId = "in_flight";
                proxy.ArmDropAfterMarker(Probe, caseId);
                var response = await proxy.CallAsync(Probe, Parameters(runId, caseId, "Hold", 3500), suiteTimeout.Token);
                var state = await proxy.CallAsync(Status, Parameters(runId, caseId), suiteTimeout.Token);
                Assert(IsSuccess(response), "In-flight command did not complete through queries: " + response);
                Assert(Data(state)["count"]?.GetValue<int>() == 1 && proxy.Commands(Probe, caseId).Count == 1, "In-flight command was executed twice.");
                Assert(proxy.RecoveryStates.Contains("in_flight"), "Fault did not expose a deterministic in-flight recovery state.");
                return Evidence(response, state, proxy.Commands(Probe, caseId).Single(), proxy.Commands("recover_command").Count);
            });

            await CaseAsync("live_declared_read_only_lost_reply", async () =>
            {
                const string caseId = "readonly";
                proxy.ArmDropAfterReply(Status, caseId);
                var response = await proxy.CallAsync(Status, Parameters(runId, caseId), suiteTimeout.Token);
                var commands = proxy.Commands(Status, caseId);
                Assert(IsSuccess(response), "Read-only reply loss did not recover.");
                Assert(commands.Count <= 2, "Read-only loss retried more than once.");
                Assert(commands.Select(c => c["requestId"]?.GetValue<string>()).Distinct().Count() == 1, "Read-only retry changed logical ID.");
                Assert(Data(response)["count"]?.GetValue<int>() == 0, "Read-only fixture changed its counter.");
                return new() { ["response"] = response.DeepClone(), ["dispatchCount"] = commands.Count, ["requestId"] = commands[0]["requestId"]?.DeepClone() };
            });

            await CaseAsync("live_uncertified_read_only_side_effect_recorded_once", async () =>
            {
                const string caseId = "uncertified_read";
                var response = await proxy.CallAsync(UncertifiedRead, Parameters(runId, caseId), suiteTimeout.Token);
                uncertifiedOriginal = proxy.Commands(UncertifiedRead, caseId).Single();
                var state = await proxy.CallAsync(Status, Parameters(runId, caseId), suiteTimeout.Token);
                Assert(IsSuccess(response) && Data(state)["count"]?.GetValue<int>() == 1, "Uncertified read-only fixture did not produce exactly one task-owned side effect.");
                return Evidence(response, state, uncertifiedOriginal, 0);
            });

            await CaseAsync("live_reload_interrupted_operation_remains_unknown", async () =>
            {
                const string caseId = "reload_interrupted";
                var beforeReload = Data(await proxy.CallAsync(Status, Parameters(runId, caseId), suiteTimeout.Token));
                domainBeforeReload = beforeReload["fixtureDomainId"]?.GetValue<string>();
                reloadEventsBefore = beforeReload["reloadEvents"]?.GetValue<int>() ?? 0;
                Assert(!string.IsNullOrEmpty(domainBeforeReload), "Fixture did not expose initial domain generation.");
                var response = await proxy.CallAsync(Probe, Parameters(runId, caseId, "Reload"), suiteTimeout.Token, 70000);
                var state = await proxy.CallAsync(Status, Parameters(runId, caseId), suiteTimeout.Token);
                domainAfterReload = Data(state)["fixtureDomainId"]?.GetValue<string>();
                reloadEventsAfter = Data(state)["reloadEvents"]?.GetValue<int>() ?? 0;
                Assert(!string.IsNullOrEmpty(domainAfterReload) && domainAfterReload != domainBeforeReload && reloadEventsAfter > reloadEventsBefore,
                    "Actual domain reload not proven: fixture domain must change and beforeAssemblyReload event must advance.");
                Assert(response.ToJsonString().Contains("outcome_unknown", StringComparison.Ordinal), "Reload-interrupted mutation did not report unknown: " + response);
                Assert(Data(state)["count"]?.GetValue<int>() == 1 && proxy.Commands(Probe, caseId).Count == 1, "Reload-interrupted mutation was replayed.");
                var evidence = Evidence(response, state, proxy.Commands(Probe, caseId).Single(), proxy.Commands("recover_command").Count);
                evidence["domainBefore"] = domainBeforeReload;
                evidence["domainAfter"] = domainAfterReload;
                evidence["reloadEventsBefore"] = reloadEventsBefore;
                evidence["reloadEventsAfter"] = reloadEventsAfter;
                return evidence;
            });

            await CaseAsync("live_completed_result_survives_domain_reload", async () =>
            {
                Assert(completedOriginal != null && originalSession != null, "Prerequisite completed operation missing.");
                Assert(!string.IsNullOrEmpty(domainBeforeReload) && !string.IsNullOrEmpty(domainAfterReload) &&
                    domainBeforeReload != domainAfterReload && reloadEventsAfter > reloadEventsBefore,
                    "Cannot qualify retained result without proof of an actual domain reload.");
                var query = RecoveryCommand(completedOriginal!, originalSession!);
                var response = await RawCallAsync(realPipe, query, suiteTimeout.Token);
                var recovery = response["result"]?.AsObject();
                var state = await proxy.CallAsync(Status, Parameters(runId, "lost_completed"), suiteTimeout.Token);
                Assert(recovery?["state"]?.GetValue<string>() == "completed", "Completed operation was not retained through domain reload: " + response);
                Assert(Data(state)["count"]?.GetValue<int>() == 1, "Recovered completed mutation reentered after reload.");
                return Evidence(response, state, completedOriginal!, 0);
            });

            await CaseAsync("live_uncertified_read_only_duplicate_after_reload_keeps_one_effect", async () =>
            {
                Assert(uncertifiedOriginal != null, "Prerequisite uncertified read-only operation missing.");
                Assert(!string.IsNullOrEmpty(domainBeforeReload) && !string.IsNullOrEmpty(domainAfterReload) &&
                    domainBeforeReload != domainAfterReload && reloadEventsAfter > reloadEventsBefore,
                    "Cannot qualify unsafe read-only durability without actual domain reload proof.");
                var beforeDuplicate = Data(await proxy.CallAsync(Status, Parameters(runId, "uncertified_read"), suiteTimeout.Token));
                var rootsBefore = beforeDuplicate["ownedSceneRoots"]?.GetValue<int>();
                var response = await RawCallAsync(realPipe, uncertifiedOriginal!, suiteTimeout.Token);
                var state = await proxy.CallAsync(Status, Parameters(runId, "uncertified_read"), suiteTimeout.Token);
                Assert(IsSuccess(response), "Unsafe read-only operation did not retain its cached result: " + response);
                Assert(Data(state)["count"]?.GetValue<int>() == 1, "Same-ID unsafe read-only duplicate reentered after reload.");
                Assert(Data(state)["ownedSceneRoots"]?.GetValue<int>() == rootsBefore, "Unsafe read-only duplicate created another real object after reload.");
                var evidence = Evidence(response, state, uncertifiedOriginal!, 0);
                evidence["rootsBeforeDuplicate"] = rootsBefore;
                evidence["domainBefore"] = domainBeforeReload;
                evidence["domainAfter"] = domainAfterReload;
                return evidence;
            });

            await CaseAsync("live_manual_command_status_only_observes", async () =>
            {
                Assert(completedOriginal != null && originalSession != null, "Prerequisite completed operation missing.");
                var statusArgs = new JsonObject
                {
                    ["OperationId"] = completedOriginal!["requestId"]?.DeepClone(), ["ToolName"] = Probe,
                    ["Arguments"] = completedOriginal["params"]?.DeepClone(), ["SessionId"] = originalSession
                };
                var response = await proxy.CommandStatusAsync(statusArgs, suiteTimeout.Token);
                var state = await proxy.CallAsync(Status, Parameters(runId, "lost_completed"), suiteTimeout.Token);
                Assert(response["state"]?.GetValue<string>() == "completed", "Manual CommandStatus did not return completed record: " + response);
                Assert(Data(state)["count"]?.GetValue<int>() == 1, "Manual CommandStatus reran mutation.");
                return Evidence(response, state, completedOriginal!, 0);
            });

            await CaseAsync("live_original_scene_and_selection_preserved", async () =>
            {
                var state = await proxy.CallAsync(Status, Parameters(runId), suiteTimeout.Token);
                after = Data(state)["snapshot"]?.DeepClone();
                Assert(JsonNode.DeepEquals(before, after), "Pre-existing scene state, roots, dirty flags, active scene or selection changed.");
                return new() { ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone(), ["fixtureSceneRoots"] = Data(state)["ownedSceneRoots"]?.DeepClone() };
            });
        }
        catch (Exception ex)
        {
            Results.Add(new() { ["name"] = "suite_exception", ["passed"] = false, ["error"] = ex.ToString() });
        }
        finally
        {
            // Cleanup remains bounded even if the suite's cancellation expired. It only
            // closes the fixture's own additive scene and removes its own Library files.
            cleanupAttempted = true;
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                var cleanupCommand = new JsonObject { ["type"] = Cleanup, ["params"] = Parameters(runId), ["requestId"] = "live-cleanup-" + Guid.NewGuid().ToString("N") };
                cleanupCommand["params"]![ExpectedRootKey] = NormalizeRoot(projectRoot);
                cleanupResult = await RawCallAsync(realPipe, cleanupCommand, cleanupTimeout.Token);
                Assert(IsSuccess(cleanupResult), "Fixture cleanup failed: " + cleanupResult);
                var cleanedSnapshot = Data(cleanupResult)["snapshot"];
                Assert(before == null || JsonNode.DeepEquals(before, cleanedSnapshot), "Original scene state changed after fixture cleanup.");
                Results.Add(new() { ["name"] = "live_task_owned_fixture_cleanup", ["passed"] = true, ["evidence"] = cleanupResult.DeepClone() });
            }
            catch (Exception ex)
            {
                Results.Add(new() { ["name"] = "live_task_owned_fixture_cleanup", ["passed"] = false, ["error"] = ex.ToString() });
            }
            if (proxy != null)
            {
                try { await proxy.DisposeAsync(); }
                catch (Exception ex)
                {
                    Results.Add(new() { ["name"] = "live_proxy_transport_cleanup", ["passed"] = false, ["error"] = ex.ToString() });
                }
            }
        }
        var failed = Results.Count(r => r["passed"]?.GetValue<bool>() != true);
        var output = new JsonObject
        {
            ["suite"] = "LIVE UniBridge command delivery qualification", ["createdUtc"] = DateTime.UtcNow.ToString("O"),
            ["projectRoot"] = projectRoot, ["projectId"] = discovery["project_id"]?.DeepClone(), ["editorPid"] = discovery["editor_pid"]?.DeepClone(),
            ["runId"] = runId, ["realPipe"] = pipePath, ["passed"] = Results.Count - failed, ["failed"] = failed,
            ["scope"] = "Exact current relay source plus real Unity Editor through a task-owned fault proxy; fixture creates only its additive scene and Library markers.",
            ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone(), ["cleanupAttempted"] = cleanupAttempted,
            ["proxyFaults"] = proxy?.DropCount, ["observedRecoveryStates"] = new JsonArray((proxy?.RecoveryStates ?? Array.Empty<string>()).Select(s => JsonValue.Create(s)).ToArray()),
            ["forwardedCommands"] = new JsonArray((proxy?.AllCommands() ?? new List<JsonObject>()).Select(c => c.DeepClone()).ToArray()),
            ["results"] = new JsonArray(Results.Select(r => r.DeepClone()).ToArray())
        };
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, output.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"LIVE delivery qualification: {Results.Count - failed}/{Results.Count} passed; report: {report}");
        return failed == 0 ? 0 : 1;
    }

    static async Task CaseAsync(string name, Func<Task<JsonObject>> action)
    {
        var started = DateTime.UtcNow;
        try
        {
            var evidence = await action();
            Results.Add(new() { ["name"] = name, ["passed"] = true, ["elapsedMs"] = (DateTime.UtcNow - started).TotalMilliseconds, ["evidence"] = evidence });
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            Results.Add(new() { ["name"] = name, ["passed"] = false, ["elapsedMs"] = (DateTime.UtcNow - started).TotalMilliseconds, ["error"] = ex.ToString() });
            Console.WriteLine("FAIL " + name + ": " + ex.Message);
        }
    }

    static JsonObject Parameters(string runId, string? caseId = null, string mode = "Complete", int delayMs = 2500) => new()
    {
        ["RunId"] = runId, ["CaseId"] = caseId, ["Mode"] = mode, ["DelayMs"] = delayMs
    };
    static JsonObject Data(JsonObject response) => response["result"]?["data"]?.AsObject() ?? throw new InvalidOperationException("Expected fixture data: " + response);
    static bool IsSuccess(JsonObject response) => response["status"]?.GetValue<string>() == "success" && response["result"]?["success"]?.GetValue<bool>() != false;
    static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static string NormalizeRoot(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
    static JsonObject Evidence(JsonObject response, JsonObject state, JsonObject original, int queryCount) => new()
    {
        ["response"] = response.DeepClone(), ["fixtureState"] = Data(state).DeepClone(), ["originalRequestId"] = original["requestId"]?.DeepClone(), ["recoveryQueryCount"] = queryCount
    };
    static JsonObject RecoveryCommand(JsonObject original, string session) => new()
    {
        ["type"] = "recover_command", ["requestId"] = "live-recovery-" + Guid.NewGuid().ToString("N"),
        ["params"] = new JsonObject
        {
            ["originalRequestId"] = original["requestId"]?.DeepClone(), ["originalType"] = original["type"]?.DeepClone(),
            ["originalParams"] = original["params"]?.DeepClone(), ["expectedSessionId"] = session,
            [ExpectedRootKey] = original["params"]?[ExpectedRootKey]?.DeepClone()
        }
    };

    static async Task<JsonObject> RawCallAsync(string pipeName, JsonObject command, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(15000, ct);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var line = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("Editor closed before handshake.");
            if (JsonNode.Parse(line)?["protocol"]?.GetValue<string>() == "unity-mcp") break;
        }
        await writer.WriteLineAsync(command.ToJsonString().AsMemory(), deadline.Token);
        var id = command["requestId"]?.GetValue<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("Editor closed before matching response.");
            var response = JsonNode.Parse(line)?.AsObject();
            if (response != null && response["requestId"]?.GetValue<string>() == id && response["status"] != null)
                return response;
        }
    }

    sealed class LiveProxy : IAsyncDisposable
    {
        readonly string realPipe;
        readonly string markerRoot;
        readonly string root;
        readonly string pipeName = "unibridge-live-delivery-" + Guid.NewGuid().ToString("N");
        readonly string? oldStatusDirectory;
        readonly CancellationTokenSource lifetime;
        readonly ConcurrentBag<IDisposable> streams = new();
        readonly ConcurrentBag<Task> handlers = new();
        readonly List<JsonObject> forwarded = new();
        readonly List<string> recoveryStates = new();
        readonly TaskCompletionSource<bool> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly McpServer relay;
        readonly object faultLock = new();
        string? dropTool;
        string? dropCase;
        bool dropAtMarker;
        Task? listener;
        public int DropCount;
        public string? FirstSessionId { get; private set; }
        public string[] RecoveryStates { get { lock (recoveryStates) return recoveryStates.ToArray(); } }

        LiveProxy(JsonObject sourceDiscovery, string projectRoot, string actualPipe, string runId, CancellationToken ct)
        {
            realPipe = actualPipe;
            markerRoot = Path.Combine(projectRoot, "Library", "UniBridge", "CommandReplayProbe", runId);
            root = Path.Combine(Path.GetTempPath(), "unibridge-live-delivery-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            oldStatusDirectory = Environment.GetEnvironmentVariable("UNIBRIDGE_MCP_STATUS_DIR");
            Environment.SetEnvironmentVariable("UNIBRIDGE_MCP_STATUS_DIR", root);
            var discovery = sourceDiscovery.DeepClone().AsObject();
            discovery["connection_path"] = @"\\.\pipe\" + pipeName;
            // Production discovery deliberately enumerates only bridge-*.json files.
            // Validate the exact isolated descriptor before making any Editor calls.
            File.WriteAllText(Path.Combine(root, "bridge-live-proxy.json"), discovery.ToJsonString());
            var options = RelayOptions.Parse(new[] { "--mcp", "--project-id", discovery["project_id"]!.GetValue<string>(), "--project-path", projectRoot });
            var selected = Discovery.FindConnection(options, new Logger(false));
            Assert(selected != null && selected.ConnectionPath == @"\\.\pipe\" + pipeName,
                "Isolated proxy discovery did not resolve its own bridge descriptor.");
            relay = new McpServer(options, new Logger(false));
        }

        public static async Task<LiveProxy> StartAsync(JsonObject discovery, string projectRoot, string pipeName, string runId, CancellationToken ct)
        {
            var proxy = new LiveProxy(discovery, projectRoot, pipeName, runId, ct);
            proxy.listener = proxy.ListenAsync();
            await proxy.listening.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            return proxy;
        }
        public void ArmDropAfterReply(string tool, string caseId) { lock (faultLock) { dropTool = tool; dropCase = caseId; dropAtMarker = false; } }
        public void ArmDropAfterMarker(string tool, string caseId) { lock (faultLock) { dropTool = tool; dropCase = caseId; dropAtMarker = true; } }
        public List<JsonObject> Commands(string tool, string? caseId = null)
        {
            lock (forwarded) return forwarded.Where(c => c["type"]?.GetValue<string>() == tool && (caseId == null || c["params"]?["CaseId"]?.GetValue<string>() == caseId)).Select(c => c.DeepClone().AsObject()).ToList();
        }
        public List<JsonObject> AllCommands() { lock (forwarded) return forwarded.Select(c => c.DeepClone().AsObject()).ToList(); }

        public async Task<JsonObject> CallAsync(string tool, JsonObject args, CancellationToken ct, int timeoutMs = 30000)
        {
            var method = typeof(McpServer).GetMethod("SendUnityCommandWithReconnectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try { return await ((Task<JsonObject>)method.Invoke(relay, new object[] { tool, args, ct })!).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        public async Task<JsonObject> CommandStatusAsync(JsonObject args, CancellationToken ct)
        {
            var method = typeof(McpServer).GetMethod("GetCommandStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try { return await ((Task<JsonObject>)method.Invoke(relay, new object[] { args, ct })!).WaitAsync(TimeSpan.FromSeconds(20), ct); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        async Task ListenAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    streams.Add(pipe);
                    listening.TrySetResult(true);
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                    handlers.Add(HandleAsync(pipe));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
        }

        async Task HandleAsync(NamedPipeServerStream downstream)
        {
            using var upstream = new NamedPipeClientStream(".", realPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            streams.Add(upstream);
            using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var ct = connectionLifetime.Token;
            var commands = new ConcurrentDictionary<string, JsonObject>();
            Task? toEditor = null;
            Task? toRelay = null;
            try
            {
                await upstream.ConnectAsync(15000, ct);
                using var editorReader = new StreamReader(upstream, new UTF8Encoding(false), false, leaveOpen: true);
                using var editorWriter = new StreamWriter(upstream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                using var relayReader = new StreamReader(downstream, new UTF8Encoding(false), false, leaveOpen: true);
                using var relayWriter = new StreamWriter(downstream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                toEditor = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await relayReader.ReadLineAsync(ct);
                        if (line == null) return;
                        var command = JsonNode.Parse(line)!.AsObject();
                        if (command["requestId"]?.GetValue<string>() is string id) commands[id] = command;
                        lock (forwarded) forwarded.Add(command.DeepClone().AsObject());
                        await editorWriter.WriteLineAsync(line.AsMemory(), ct);
                        if (TakeFault(command, marker: true))
                        {
                            var marker = Path.Combine(markerRoot, command["params"]!["CaseId"]!.GetValue<string>() + ".json");
                            var deadline = DateTime.UtcNow.AddSeconds(10);
                            while (!File.Exists(marker) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);
                            Assert(File.Exists(marker), "Mutation marker not written before in-flight fault deadline.");
                            Interlocked.Increment(ref DropCount);
                            downstream.Dispose();
                            upstream.Dispose();
                            return;
                        }
                    }
                }, ct);
                toRelay = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await editorReader.ReadLineAsync(ct);
                        if (line == null) return;
                        var response = JsonNode.Parse(line)!.AsObject();
                        if (response["commandRecovery"]?["sessionId"]?.GetValue<string>() is string session && FirstSessionId == null)
                            FirstSessionId = session;
                        if (response["requestId"]?.GetValue<string>() is string id && response["status"] != null && commands.TryGetValue(id, out var original))
                        {
                            if (original["type"]?.GetValue<string>() == "recover_command" && response["result"]?["state"]?.GetValue<string>() is string state)
                                lock (recoveryStates) recoveryStates.Add(state);
                            if (TakeFault(original, marker: false))
                            {
                                Interlocked.Increment(ref DropCount);
                                downstream.Dispose();
                                upstream.Dispose();
                                return;
                            }
                        }
                        await relayWriter.WriteLineAsync(line.AsMemory(), ct);
                    }
                }, ct);
                await Task.WhenAny(toEditor, toRelay);
                connectionLifetime.Cancel();
                downstream.Dispose();
                upstream.Dispose();
                try { await Task.WhenAll(toEditor, toRelay).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
            finally
            {
                connectionLifetime.Cancel();
                downstream.Dispose();
            }
        }

        bool TakeFault(JsonObject command, bool marker)
        {
            lock (faultLock)
            {
                if (dropTool == null || dropAtMarker != marker || command["type"]?.GetValue<string>() != dropTool || command["params"]?["CaseId"]?.GetValue<string>() != dropCase)
                    return false;
                dropTool = null;
                dropCase = null;
                return true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await relay.DisposeAsync(); }
            finally
            {
                lifetime.Cancel();
                foreach (var stream in streams) stream.Dispose();
                try
                {
                    if (listener != null) await listener.WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
                lifetime.Dispose();
                Environment.SetEnvironmentVariable("UNIBRIDGE_MCP_STATUS_DIR", oldStatusDirectory);
                var resolved = Path.GetFullPath(root);
                Assert(string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith("unibridge-live-delivery-fixture-", StringComparison.Ordinal), "Refusing proxy fixture cleanup outside the task-owned temporary directory.");
                Directory.Delete(resolved, true);
            }
        }
    }
}
