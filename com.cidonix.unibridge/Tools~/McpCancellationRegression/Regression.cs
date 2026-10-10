using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal sealed class LogOptions { public object Data { get; set; } }
    internal static class McpLog
    {
        public static void Log(string message, LogOptions options = null) { }
        public static void Error(string message, LogOptions options = null) { }
    }
    internal static class ProjectContextGuard
    {
        public static void AssertExpectedRootForPolicy(string tool, JObject parameters, ToolExecutionPolicy policy) { }
        public static JObject BuildProjectContext() => new();
    }
}
namespace Cidonix.UniBridge.Toolkit
{
    internal static class EditorTask
    {
        public static Task Delay(int milliseconds, CancellationToken token) => Task.Delay(milliseconds, token);
        public static Task Yield() => Task.Delay(1);
    }
}
namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    internal interface IToolHandler { McpToolAttribute Attribute { get; } Task<object> ExecuteAsync(JObject parameters); }
    internal interface IOutputContractHandler { OutputContract OutputContract { get; } }
    internal static class StructuredOutputProjection
    {
        public static object Completed(object result, string tool, bool schema) => JObject.FromObject(new { result });
        public static JObject Project(JObject result) => result;
    }
    internal static class RecoveryActionOutputRules { public static JObject CompactActiveReview() => ActionOutputSchema.Type("null"); }
    internal static class ReadConsoleActionOutputRules { public static JObject DiagnosticSummaryData(bool value) => ActionOutputSchema.Type("null"); }
    internal static partial class McpToolRegistry
    {
        static readonly Dictionary<string, IToolHandler> k_Tools = new();
        public static void Register(string name, IToolHandler handler) => k_Tools[name] = handler;
        public static IToolHandler GetTool(string name) => k_Tools.GetValueOrDefault(name);
        public static IEnumerable<SettingsEntry> GetAllToolsForSettings() => Array.Empty<SettingsEntry>();
        internal sealed class SettingsEntry
        {
            public ToolInfo Info { get; set; }
            public bool IsEnabled { get; set; }
            public bool IsDefault { get; set; }
            public string[] Groups { get; set; }
        }
        internal sealed class ToolInfo { public string name { get; set; } public string title { get; set; } }
    }
    internal sealed class McpToolAttribute : Attribute
    {
        public McpToolAttribute() { }
        public McpToolAttribute(string name, string description, string title) { }
        public ToolExecutionPolicy ExecutionPolicy { get; set; }
        public bool ReplaySafe { get; set; }
        public string[] Groups { get; set; }
        public bool EnabledByDefault { get; set; }
    }
    internal sealed class McpSchemaAttribute(string name) : Attribute { }
}
namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    internal static class WorkSession { public static object BuildCompactActiveReview(int maxChanged, bool includeChangedFiles, bool includeSemanticReview, int maxSemanticChanges) => null; }
}
namespace Cidonix.UniBridge.MCP.Editor { internal interface IConnectionTransport { } }

internal static class Program
{
    sealed class Handler(ToolExecutionPolicy policy, Func<Task<object>> body = null) : IToolHandler
    {
        public McpToolAttribute Attribute { get; } = new() { ExecutionPolicy = policy };
        public Task<object> ExecuteAsync(JObject parameters) => body();
    }
    sealed class Transport : IConnectionTransport { }
    static readonly List<object> Results = new();
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static JObject Snapshot() => JObject.FromObject(ToolExecutionScheduler.Snapshot(80));
    static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(3);
        while (!condition()) { if (DateTime.UtcNow >= end) throw new TimeoutException("Deterministic condition was not reached."); await Task.Delay(5); }
    }
    static JObject Active(string requestId) => Snapshot()["activeOperations"]!.OfType<JObject>().Single(record => record.Value<string>("requestId") == requestId);
    static JObject Recent(string requestId) => Snapshot()["recentOperations"]!.OfType<JObject>().Single(record => record.Value<string>("requestId") == requestId);
    static JObject Status(string action) => Cidonix.UniBridge.MCP.Editor.Helpers.McpJson.ObjectFromObject(
        Cidonix.UniBridge.MCP.Editor.Tools.ExecutionStatus.HandleCommand(new JObject
        {
            ["Action"] = action, ["IncludeWorkSession"] = false, ["RecentLimit"] = 80,
            ["GraceMs"] = 1000000, ["ForceReadOnly"] = false
        }));
    static void ValidateStatus(string action)
    {
        var schema = EditorObservationActionOutputRules.Build("UniBridge_ExecutionStatus")[action];
        var errors = ActionOutputValidation.Check(Status(action), schema, null);
        Assert(errors.Count == 0, "Actual ExecutionStatus " + action + " producer violates its closed action schema: " + new JArray(errors));
    }
    static Task<object> Execute(string requestId, ToolExecutionPolicy policy, Func<Task<object>> body, CancellationToken token = default, int timeout = 120000)
    {
        var previous = ToolExecutionScheduler.CurrentRequestId;
        ToolExecutionScheduler.CurrentRequestId = requestId;
        try { return ToolExecutionScheduler.ExecuteAsync("CancellationRegression", new JObject { ["ExecutionTimeoutMs"] = timeout }, new Handler(policy), body, token); }
        finally { ToolExecutionScheduler.CurrentRequestId = previous; }
    }
    static async Task Canceled(Task task)
    {
        var winner = await Task.WhenAny(task, Task.Delay(3000));
        Assert(winner == task, "Cancellation waited for the operation's own timeout.");
        try { await task; throw new Exception("Canceled operation completed successfully."); }
        catch (OperationCanceledException) { }
    }
    static async Task Run(string name, Func<Task> test)
    {
        try { await test(); Results.Add(new { name, passed = true }); }
        catch (Exception error) { Results.Add(new { name, passed = false, error = error.ToString() }); }
        Assert(Snapshot().Value<int>("activeReaders") == 0 && Snapshot()["activeOperations"]!.Count() == 0, "Test left an execution slot/operation active.");
    }
    static async Task Main(string[] args)
    {
        foreach (var policy in new[] { ToolExecutionPolicy.ReadOnly, ToolExecutionPolicy.Observer, ToolExecutionPolicy.Mutating, ToolExecutionPolicy.Capture, ToolExecutionPolicy.CompileReload })
            await Run("Pre-canceled " + policy + " never calls handler", async () =>
            {
                using var caller = new CancellationTokenSource(); caller.Cancel(); var calls = 0; var id = Guid.NewGuid().ToString();
                await Canceled(Execute(id, policy, () => { calls++; return Task.FromResult<object>(true); }, caller.Token));
                var record = Recent(id); Assert(calls == 0 && record["startedUtc"]!.Type == JTokenType.Null && !record.Value<bool>("changedProject"), "Pre-canceled handler started or claimed project changes.");
            });

        foreach (var blockedPolicy in new[] { ToolExecutionPolicy.Mutating, ToolExecutionPolicy.ReadOnly })
            await Run("Queued " + blockedPolicy + " cancels behind exclusive lease", async () =>
            {
                var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                var blockerId = Guid.NewGuid().ToString(); var blocker = Execute(blockerId, ToolExecutionPolicy.Mutating, () => release.Task);
                using var caller = new CancellationTokenSource(); var calls = 0; var id = Guid.NewGuid().ToString();
                var queued = Execute(id, blockedPolicy, () => { calls++; return Task.FromResult<object>(true); }, caller.Token);
                try
                {
                    Assert(Active(id).Value<string>("outcome") == "queued", "Queued operation was invisible."); caller.Cancel(); await Canceled(queued);
                    var record = Recent(id); Assert(calls == 0 && record["startedUtc"]!.Type == JTokenType.Null && !record.Value<bool>("changedProject"), "Queued cancellation executed project work.");
                }
                finally { release.TrySetResult(true); await blocker; }
            });

        foreach (var policy in new[] { ToolExecutionPolicy.ReadOnly, ToolExecutionPolicy.Observer })
            await Run("Started " + policy + " cooperatively cancels and cleans up", async () =>
            {
                using var caller = new CancellationTokenSource(); var cleaned = false; var id = Guid.NewGuid().ToString();
                var operation = Execute(id, policy, async () =>
                {
                    try { await Task.Delay(120000, ToolExecutionScheduler.CurrentCancellationToken); return true; }
                    finally { cleaned = true; }
                }, caller.Token);
                Assert(Active(id).Value<string>("outcome") == "running", "Started operation was not admitted."); caller.Cancel(); await Canceled(operation); await Until(() => cleaned);
                var record = Recent(id); Assert(record.Value<string>("outcome") == "canceled" && record["forcedRelease"]!.Type == JTokenType.Null, "Cancellation relied on stale lease reaping.");
            });

        await Run("Canceling a writer queued behind a read allows the next reader through", async () =>
        {
            var release = new TaskCompletionSource<object>(); var first = Execute(Guid.NewGuid().ToString(), ToolExecutionPolicy.ReadOnly, () => release.Task);
            using var caller = new CancellationTokenSource(); var calls = 0; var id = Guid.NewGuid().ToString();
            var queued = Execute(id, ToolExecutionPolicy.Mutating, () => { calls++; return Task.FromResult<object>(true); }, caller.Token);
            var nextReader = Execute(Guid.NewGuid().ToString(), ToolExecutionPolicy.ReadOnly, () => Task.FromResult<object>("next read"));
            try
            {
                Assert(Active(id).Value<string>("outcome") == "queued", "Queued writer admission not visible.");
                caller.Cancel(); await Canceled(queued);
                Assert(await Task.WhenAny(nextReader, Task.Delay(3000)) == nextReader && (string)await nextReader == "next read", "Canceled writer retained the queue gate.");
                Assert(calls == 0 && !Recent(id).Value<bool>("changedProject"), "Canceled writer executed project work.");
            }
            finally { release.TrySetResult(true); await first; }
        });

        await Run("Registry consumer passes cancellation to a queued mutating scheduler operation", async () =>
        {
            var release = new TaskCompletionSource<object>(); var blocker = Execute(Guid.NewGuid().ToString(), ToolExecutionPolicy.ReadOnly, () => release.Task);
            using var caller = new CancellationTokenSource(); var calls = 0; var name = "RegistryQueuedMutation";
            McpToolRegistry.Register(name, new Handler(ToolExecutionPolicy.Mutating, () => { calls++; return Task.FromResult<object>(true); }));
            var queued = McpToolRegistry.ExecuteToolAsync(name, new JObject(), caller.Token);
            try { caller.Cancel(); await Canceled(queued); Assert(calls == 0, "Registry discarded caller token before exclusive admission."); }
            finally { release.TrySetResult(true); await blocker; }
        });

        await Run("Registry consumer preserves completed begun mutation after caller cancellation", async () =>
        {
            using var caller = new CancellationTokenSource(); var release = new TaskCompletionSource<object>(); var name = "RegistryStartedMutation";
            McpToolRegistry.Register(name, new Handler(ToolExecutionPolicy.Mutating, async () => { await release.Task; ToolExecutionScheduler.ThrowIfCancellationRequested(); return "project actually changed"; }));
            var operation = McpToolRegistry.ExecuteToolAsync(name, new JObject(), caller.Token); caller.Cancel(); release.SetResult(true);
            var result = (JObject)await operation; Assert(result.Value<string>("result") == "project actually changed", "Registry replaced an actual completed mutation result.");
        });

        foreach (var policy in new[] { ToolExecutionPolicy.Mutating, ToolExecutionPolicy.Capture, ToolExecutionPolicy.CompileReload })
            await Run("Started " + policy + " preserves actual success after caller cancellation", async () =>
            {
                using var caller = new CancellationTokenSource(); var release = new TaskCompletionSource<object>(); var id = Guid.NewGuid().ToString();
                var operation = Execute(id, policy, async () => { var result = await release.Task; ToolExecutionScheduler.ThrowIfCancellationRequested(); return result; }, caller.Token);
                caller.Cancel(); Assert(!operation.IsCompleted, "Begun exclusive operation was abandoned."); release.SetResult("real completed result");
                Assert((string)await operation == "real completed result", "Real completed result was replaced.");
                var record = Recent(id); Assert(record.Value<string>("outcome") == "success" && record.Value<bool>("cancellationDeferred") && record.Value<bool>("cancellationRequested"), "Started write reported canceled instead of real completion.");
            });

        await Run("Started mutation preserves actual failure after caller cancellation", async () =>
        {
            using var caller = new CancellationTokenSource(); var release = new TaskCompletionSource<object>(); var id = Guid.NewGuid().ToString();
            var operation = Execute(id, ToolExecutionPolicy.Mutating, () => release.Task, caller.Token); caller.Cancel(); release.SetException(new InvalidOperationException("actual write failure"));
            try { await operation; throw new Exception("Real failure lost."); } catch (InvalidOperationException) { }
            Assert(Recent(id).Value<string>("outcome") == "failed" && Recent(id).Value<bool>("cancellationDeferred"), "Real mutation failure became cancellation.");
        });

        await Run("Request cancellation is scoped to the exact transport and correlation ID", () =>
        {
            var bridge = new Bridge(); var first = new Transport(); var other = new Transport();
            using var a = new CancellationTokenSource(); using var b = new CancellationTokenSource(); using var c = new CancellationTokenSource();
            bridge.Add("a", first, "shared", a); bridge.Add("b", other, "shared", b); bridge.Add("c", first, "different", c);
            bridge.Cancel(first, "shared"); Assert(a.IsCancellationRequested && !b.IsCancellationRequested && !c.IsCancellationRequested, "Cancellation crossed client/request boundaries.");
            bridge.Release("a"); bridge.Release("b"); bridge.Release("c"); Assert(bridge.Count == 0, "Request sources retained after completion."); return Task.CompletedTask;
        });

        await Run("Concurrent cancellation versus completion/disposal is harmless", async () =>
        {
            var bridge = new Bridge(); var client = new Transport();
            for (var index = 0; index < 100; index++)
            {
                var source = new CancellationTokenSource(); var id = index.ToString(); bridge.Add(id, client, id, source);
                await Task.WhenAll(Task.Run(() => bridge.Cancel(client, id)), Task.Run(() => bridge.Release(id)));
            }
            Assert(bridge.Count == 0, "Race retained a per-request source.");
        });

        foreach (var policy in new[] { ToolExecutionPolicy.ReadOnly, ToolExecutionPolicy.Observer })
            await Run("Successful " + policy + " disposes waiters without canceling caller or operation", async () =>
            {
                using var caller = new CancellationTokenSource(); var id = Guid.NewGuid().ToString();
                var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                var operation = Execute(id, policy, () => release.Task, caller.Token);
                release.SetResult("completed read");
                Assert((string)await operation == "completed read" && !caller.IsCancellationRequested, "Waiter cleanup canceled the caller.");
                var record = Recent(id);
                Assert(record.Value<string>("outcome") == "success" && !record.Value<bool>("cancellationRequested"), "Waiter cleanup marked successful operation canceled.");
                // A later caller cancellation must neither rewrite the completed history
                // nor leave an execution lease that blocks the next independent write.
                caller.Cancel();
                Assert(Recent(id).Value<string>("outcome") == "success" && !Recent(id).Value<bool>("cancellationRequested"), "Completed operation retained a live caller registration.");
                await Execute(Guid.NewGuid().ToString(), ToolExecutionPolicy.Mutating, () => Task.FromResult<object>(true));
            });

        await Run("Actual ExecutionStatus producer schemas cover queued, started, canceled, and deferred records", async () =>
        {
            using var mutationCaller = new CancellationTokenSource(); using var queuedCaller = new CancellationTokenSource();
            var release = new TaskCompletionSource<object>(); var mutationId = Guid.NewGuid().ToString(); var queueId = Guid.NewGuid().ToString();
            var mutation = Execute(mutationId, ToolExecutionPolicy.Mutating, () => release.Task, mutationCaller.Token);
            var queued = Execute(queueId, ToolExecutionPolicy.ReadOnly, () => Task.FromResult<object>(true), queuedCaller.Token);
            try
            {
                ValidateStatus("Snapshot"); ValidateStatus("Recent"); ValidateStatus("ReapStale");
                queuedCaller.Cancel(); await Canceled(queued); mutationCaller.Cancel();
                Assert(Active(mutationId).Value<bool>("cancellationDeferred"), "Started mutation not visibly deferred.");
                ValidateStatus("Snapshot"); ValidateStatus("Recent"); ValidateStatus("ReapStale");
            }
            finally { release.TrySetResult("completed write"); await mutation; }
            await Execute(null, ToolExecutionPolicy.ReadOnly, () => Task.FromResult<object>(true));
            ValidateStatus("Snapshot"); ValidateStatus("Recent"); ValidateStatus("ReapStale");
        });

        await Run("ExecutionStatus action schemas remain closed and reject wrong cancellation field types", () =>
        {
            var schema = EditorObservationActionOutputRules.Build("UniBridge_ExecutionStatus")["Snapshot"];
            foreach (var field in new[] { "requestId", "cancellationDeferred", "undeclaredCancellationField" })
            {
                var malformed = Status("Snapshot");
                var record = (JObject)malformed["data"]!["scheduler"]!["recentOperations"]![0]!;
                record[field] = field == "requestId" ? new JValue(123) : new JValue("incorrect type");
                Assert(ActionOutputValidation.Check(malformed, schema, null).Count > 0, "Closed schema accepted malformed " + field + ".");
            }
            return Task.CompletedTask;
        });
        var failed = Results.Count(result => !JObject.FromObject(result).Value<bool>("passed"));
        File.WriteAllText(args[0], JsonConvert.SerializeObject(new { success = failed == 0, passed = Results.Count - failed, failed, cases = Results, provenance = JObject.Parse(File.ReadAllText(args[1])) }, Formatting.Indented));
        Console.WriteLine($"MCP cancellation regression: {Results.Count - failed}/{Results.Count} PASS");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
}
