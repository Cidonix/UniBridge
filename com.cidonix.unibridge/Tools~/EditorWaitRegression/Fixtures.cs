using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;

// Minimal Editor substrate. The production poll, readiness predicate, response
// formatter and reload wrapper are compiled unchanged. No Unity process runs.
namespace UnityEditor
{
    static class EditorApplication
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly Timer Pump = new(_ => update?.Invoke(), null, 0, 5);
        public static bool isCompiling, isUpdating, isPlaying, isPaused, isPlayingOrWillChangePlaymode;
        public static double timeSinceStartup => Clock.Elapsed.TotalSeconds;
        public static event Action? update;
        public static void QueuePlayerLoopUpdate() { }
    }
    static class SceneView { public static void RepaintAll() { } }
    enum ImportAssetOptions { ForceUpdate = 1 }
    static class AssetDatabase
    {
        internal static int RefreshCount;
        public static void Refresh(ImportAssetOptions options) { RefreshCount++; }
        public static void SaveAssets() { }
    }
    static class Undo { public static void PerformUndo() { } public static void PerformRedo() { } }
}
namespace UnityEditorInternal
{
    static class InternalEditorUtility { public static void RepaintAllViews() { } }
}
namespace UnityEngine.SceneManagement
{
    struct Scene
    {
        public string path;
        public bool isLoaded;
        public bool IsValid() => true;
    }
    static class SceneManager
    {
        public static Scene GetActiveScene() => new() { path = "", isLoaded = true };
        public static Scene GetSceneByPath(string path) => new() { path = path, isLoaded = true };
    }
}
namespace UnityEngine
{
    static class Application { public static string unityVersion = "fixture"; public static string platform = "fixture"; }
}
namespace UnityEditor.SceneManagement
{
    sealed class PrefabStage { public string? assetPath; }
    static class PrefabStageUtility
    {
        public static PrefabStage? GetCurrentPrefabStage() => null;
        public static void OpenPrefab(string path) { }
    }
    static class StageUtility { public static void GoToMainStage() { } }
    static class EditorSceneManager
    {
        public static bool SaveScene(UnityEngine.SceneManagement.Scene scene) => true;
        public static bool SaveOpenScenes() => true;
    }
}
namespace Cidonix.UniBridge.Relay
{
    static partial class EditorWaitProduction
    {
        internal sealed class ManageEditorParams
        {
            public int? TimeoutMs, PollIntervalMs;
            public bool? RequireNotPlaying;
            public string[]? ModifiedAssetPaths;
            public bool? RestoreScenes, RestorePrefabStage, Force, AllowDirtySceneReload, SaveUnmodifiedScenes, RepaintEditor, WaitForCompletion;
        }
        internal static int DiagnosticsCalls;
        internal static bool ThrowDiagnostics;
        static class ReadConsole
        {
            public static object BuildBuildSystemHealth(int maxIssues, bool includeStacktrace)
            {
                DiagnosticsCalls++;
                if (ThrowDiagnostics) throw new InvalidOperationException("Fixture diagnostic failure");
                return new { hasCriticalIssues = false };
            }
        }
        static object BuildScriptAssemblyFreshness() => new { available = true };
        static object BuildCompilationDiagnosticsData(object health, object freshness, bool includeBuildEvidence) => new { errors = 0, warnings = 0 };
        static object BuildCompileHealthSummary(JToken health, JToken freshness) => new { healthy = true };
        sealed class LoadedSceneInfo { public string path = "", name = ""; public bool isDirty; }
        static List<LoadedSceneInfo> GetLoadedSceneInfos() => new();
        static string NormalizePath(string? path) => path ?? "";
        static string NormalizeReloadAssetPath(string path) => path;
        static void ReopenLoadedScenes(List<string> paths, string active) { }
        static void RepaintEditorViews() { }
        internal static void Reset()
        {
            DiagnosticsCalls = 0;
            UnityEditor.AssetDatabase.RefreshCount = 0;
            ThrowDiagnostics = false;
            UnityEditor.EditorApplication.isCompiling = false;
            UnityEditor.EditorApplication.isUpdating = false;
            UnityEditor.EditorApplication.isPlaying = false;
            UnityEditor.EditorApplication.isPaused = false;
            UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode = false;
        }
        internal static async Task<JObject> InvokeAsync(string method, ManageEditorParams args)
        {
            var target = typeof(EditorWaitProduction).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = await (Task<object>)target.Invoke(null, new object[] { args })!;
            return JObject.FromObject(result);
        }
    }

    // The reload-cancellation branch needs a deterministic update exception.
    // Only the update seam is fake; WaitForPlayModeState is verbatim production.
    static partial class PlayWaitProduction
    {
        internal sealed class ManageEditorParams { public int? TimeoutMs, PollIntervalMs; }
        internal static bool CancelUpdate;
        static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
        static void ClearPendingPlayModeState(bool target) { }
        static object BuildReadinessData(bool requireNotPlaying, DateTime startedAtUtc) => new
        {
            isPlaying = UnityEditor.EditorApplication.isPlaying,
            isPlayingOrWillChangePlaymode = UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode
        };
        static object BuildPlayModeBoundaryData(bool targetPlaying, DateTime start, string? boundaryReason) => new
        {
            targetPlaying, reloadBoundary = true, reconnectRequired = true, boundaryReason
        };
        static Task WaitForEditorUpdateAsync(int milliseconds) => CancelUpdate
            ? Task.FromException(new OperationCanceledException("fixture reload cancellation")) : Task.Delay(milliseconds);
        internal static async Task<JObject> InvokeAsync(bool targetPlaying)
        {
            var method = typeof(PlayWaitProduction).GetMethod("WaitForPlayModeState", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = await (Task<object>)method.Invoke(null, new object[] { targetPlaying,
                new ManageEditorParams { TimeoutMs = 100, PollIntervalMs = 25 } })!;
            return JObject.FromObject(result);
        }
    }

    static partial class LegacyWaitProduction
    {
        static class UniBridgeLegacyHost { public static object BuildProjectContext() => new { projectId = "fixture" }; }
        static class UniBridgeLegacyConsole { public static object BuildSummary() => new { errors = 0 }; }
        static object BuildSceneSummary(UnityEngine.SceneManagement.Scene scene) => new { path = scene.path };
        internal static JObject Invoke(string action, bool requireNotPlaying)
        {
            var parameters = new Dictionary<string, object> { ["Action"] = action, ["RequireNotPlaying"] = requireNotPlaying, ["TimeoutMs"] = 5000 };
            var method = typeof(LegacyWaitProduction).GetMethod("ManageEditor", BindingFlags.NonPublic | BindingFlags.Static)!;
            return JObject.FromObject(method.Invoke(null, new object[] { parameters })!);
        }
    }

    sealed class EditorWaitFaultUnity : IAsyncDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "unibridge-editor-wait-fixture-" + Guid.NewGuid().ToString("N"));
        readonly string pipeName = "unibridge-editor-wait-" + Guid.NewGuid().ToString("N");
        readonly string? oldStatusDirectory;
        readonly CancellationTokenSource lifetime = new();
        readonly ConcurrentBag<NamedPipeServerStream> streams = new();
        readonly ConcurrentBag<Task> handlers = new();
        readonly List<JsonObject> received = new();
        readonly TaskCompletionSource<bool> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly McpServer relay;
        Task? listener;
        internal Func<JsonObject, Task<JsonObject?>>? Respond;

        EditorWaitFaultUnity()
        {
            Directory.CreateDirectory(root);
            oldStatusDirectory = Environment.GetEnvironmentVariable("UNIBRIDGE_MCP_STATUS_DIR");
            Environment.SetEnvironmentVariable("UNIBRIDGE_MCP_STATUS_DIR", root);
            var projectId = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(root, "bridge-test.json"), new JsonObject
            {
                ["connection_type"] = "named_pipe", ["connection_path"] = @"\\.\pipe\" + pipeName,
                ["project_id"] = projectId, ["project_name"] = "EditorWaitFixture", ["project_root"] = root,
                ["project_path"] = Path.Combine(root, "Assets")
            }.ToJsonString());
            relay = new McpServer(RelayOptions.Parse(new[] { "--mcp", "--project-id", projectId, "--project-path", root }), new Logger(false));
        }
        internal static async Task<EditorWaitFaultUnity> StartAsync()
        {
            var fixture = new EditorWaitFaultUnity();
            fixture.listener = fixture.ListenAsync();
            await fixture.listening.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return fixture;
        }
        internal async Task<JsonObject> RecoverAsync(string kind, int timeoutMs = 5000, bool targetPlaying = false)
        {
            string methodName = kind switch
            {
                "compilation" => "RecoverScriptCompilationAfterReloadAsync",
                "refresh" => "RecoverRefreshAssetsAfterReloadAsync",
                "play" or "stop" => "RecoverPlayModeAfterReloadAsync",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            var args = new JsonObject
            {
                ["Action"] = kind switch { "compilation" => "RequestScriptCompilation", "refresh" => "RefreshAssets", "play" => "Play", _ => "Stop" },
                ["TimeoutMs"] = timeoutMs, ["PollIntervalMs"] = 50, ["RequireNotPlaying"] = !targetPlaying
            };
            object[] arguments = kind is "play" or "stop"
                ? new object[] { "UniBridge_ManageEditor", args, new IOException("fixture reload boundary"), targetPlaying, CancellationToken.None }
                : new object[] { "UniBridge_ManageEditor", args, new IOException("fixture reload boundary"), CancellationToken.None };
            var method = typeof(McpServer).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
            Task<JsonObject> task;
            try { task = (Task<JsonObject>)method.Invoke(relay, arguments)!; }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            return await task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        internal List<JsonObject> Commands()
        {
            lock (received) return received.Where(r => r["type"]?.GetValue<string>() == "UniBridge_ManageEditor").Select(r => r.DeepClone().AsObject()).ToList();
        }
        internal int ToolCommandCount(string tool)
        {
            lock (received) return received.Count(r => r["type"]?.GetValue<string>() == tool);
        }
        internal async Task<JsonObject> CallMcpAsync(string tool = "UniBridge_ManageEditor")
        {
            var method = typeof(McpServer).GetMethod("HandleToolsCallAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var originalOutput = Console.Out;
            using var capture = new StringWriter();
            Console.SetOut(capture);
            try
            {
                var task = (Task)method.Invoke(relay, new object[]
                {
                    JsonValue.Create(420)!,
                    new JsonObject { ["name"] = tool, ["arguments"] = tool == "UniBridge_BatchActions"
                        ? new JsonObject { ["Steps"] = new JsonArray(), ["DryRun"] = false }
                        : new JsonObject { ["Action"] = "WaitForReadyAfterReload" } },
                    CancellationToken.None
                })!;
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { Console.SetOut(originalOutput); }
            return capture.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonNode.Parse(line)!.AsObject()).Single(message => message["id"]?.GetValue<int>() == 420);
        }
        async Task ListenAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    streams.Add(pipe);
                    listening.TrySetResult(true);
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                    handlers.Add(HandleAsync(pipe));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
        }
        async Task HandleAsync(NamedPipeServerStream pipe)
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            try
            {
                await writer.WriteLineAsync(new JsonObject
                {
                    ["protocol"] = "unity-mcp", ["version"] = "1.0", ["toolsHash"] = "test", ["tools"] = new JsonArray()
                }.ToJsonString());
                while (!lifetime.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(lifetime.Token);
                    if (line == null) break;
                    var command = JsonNode.Parse(line)!.AsObject();
                    lock (received) received.Add(command.DeepClone().AsObject());
                    var type = command["type"]?.GetValue<string>();
                    var response = type is "set_client_info" or "get_available_tools"
                        ? Success(new() { ["unchanged"] = true })
                        : Respond == null ? Success(new()) : await Respond(command);
                    if (response == null) { pipe.Dispose(); break; }
                    response["requestId"] = command["requestId"]?.DeepClone();
                    await writer.WriteLineAsync(response.ToJsonString());
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
            finally
            {
                // The relay closes the pipe during fixture teardown. Disposing a
                // StreamWriter may flush after that close; this is not a tool failure.
                try { writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
                try { reader.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
                pipe.Dispose();
            }
        }
        internal static JsonObject Success(JsonObject data) => new()
        {
            ["status"] = "success", ["result"] = new JsonObject { ["success"] = true, ["data"] = data }
        };
        internal static JsonObject Error(string code) => new()
        {
            ["status"] = "error", ["result"] = new JsonObject { ["success"] = false, ["code"] = code, ["error"] = code }
        };
        public async ValueTask DisposeAsync()
        {
            await relay.DisposeAsync();
            lifetime.Cancel();
            foreach (var stream in streams) stream.Dispose();
            if (listener != null) await listener;
            await Task.WhenAll(handlers);
            lifetime.Dispose();
            Environment.SetEnvironmentVariable("UNIBRIDGE_MCP_STATUS_DIR", oldStatusDirectory);
            var resolved = Path.GetFullPath(root);
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("unibridge-editor-wait-fixture-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing fixture cleanup outside the owned temporary directory.");
            Directory.Delete(resolved, true);
        }
    }
}
