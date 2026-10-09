using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Models;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Connection;

namespace Cidonix.UniBridge.BridgeIsolationRegression;

static class LifeFault
{
    public static string Stage;
    public static readonly List<string> Acquired = new();
    public static Action<string> Callback;
    public static void At(string stage)
    {
        Callback?.Invoke(stage);
        if (Stage == stage) throw new InvalidOperationException("Injected owned acquisition failure: " + stage);
    }
    public static void Reset() { Stage = null; Callback = null; Acquired.Clear(); }
}

partial class Bridge : IDisposable
{
    IConnectionListener listener;
    bool isRunning, isBatchMode, initScheduled, ensureUpdateHooked, isStarting;
    double nextStartAt;
    readonly object startStopLock = new();
    CancellationTokenSource cts;
    Task listenerTask;
    readonly ConcurrentDictionary<string, (Command command, TaskCompletionSource<string> tcs, IConnectionTransport client, CancellationToken cancellationToken)> commandQueue = new();
    string s_CurrentToolsHash, currentConnectionPath;
    McpToolInfo[] s_ToolsSnapshot;
    ValidationConfig validationConfig;
    CommandRecoveryJournal commandRecovery;
    JObject commandRecoveryHandshake;
    readonly SemaphoreSlim transportWriteLock = new(1, 1);
    readonly CancellationTokenSource writeShutdown = new();
    int activeTransportWrites;
    bool writeResourcesDisposed;
    volatile bool disposed;
    int generationNumber, processingCommands;
    volatile BridgeGeneration currentGeneration;
    readonly ConcurrentDictionary<string, TaskCompletionSource<string>> commandWaiters = new();
    const string StoppedResponse = "{\"status\":\"error\",\"error\":\"Bridge stopped\"}";
    public static event Action<string> OnConnectionAttempt;
    public static Task<string> DispatchResult = Task.FromResult("{\"status\":\"success\",\"result\":{\"owned\":true}}");
    public static int HandOffs;
    static void LogBreadcrumb(string message) { }
    Task<string> DispatchCommandAsync(Command command, IConnectionTransport client, CancellationToken token) => DispatchResult;
    Task HandleClientAsync(IConnectionTransport transport, BridgeGeneration generation)
    {
        if (IsCurrentGeneration(generation)) Interlocked.Increment(ref HandOffs);
        generation.Transports.TryRemove(transport, out _);
        CloseAndDisposeTransport(transport);
        return Task.CompletedTask;
    }
    public bool Running => isRunning;
    public IConnectionListener Listener => listener;
    public object Generation => currentGeneration;
    public Task WorkerTask => listenerTask;
    public CancellationToken Cancellation => cts?.Token ?? default;
    public int QueueCount => commandQueue.Count;
    public int WaiterCount => commandWaiters.Count;
    public int WriteCount => activeTransportWrites;
    public bool WriteResourcesDisposed => writeResourcesDisposed;
    public Task Write(IConnectionTransport client, string message, CancellationToken token = default) => WriteWithLockAsync(client, message, token);
    public CancellationTokenSource LinkedTransport(object generation) => CreateTransportCancellation((BridgeGeneration)generation);
    public bool Queue(object generation, string id, TaskCompletionSource<string> tcs, IConnectionTransport client, CancellationToken token)
        => TryQueueCommand((BridgeGeneration)generation, id, new Command { type = "Owned_Read", requestId = id }, tcs, client, token);
    public void Process() => ProcessCommands();
    public void OwnedGenerationCallback(object generation, CancellationToken token, Action action) => RunForGeneration((BridgeGeneration)generation, token, action);
    public void AddTrackedTransport(IConnectionTransport transport) { currentGeneration.Transports[transport] = 0; TransportStore.Items.Add(transport); }
    public static void ResetStatic() { OnConnectionAttempt = null; HandOffs = 0; DispatchResult = Task.FromResult("{\"status\":\"success\"}"); }
    public string ToolsHash { get { ComputeToolsSnapshotAndHash(); return s_CurrentToolsHash; } }
    public TaskCompletionSource<string> AddPending(string name = "pending")
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        commandQueue[name] = (new Command(), tcs, null, default);
        return tcs;
    }
    public object Inspect() => new { isRunning, listenerPresent = listener != null, ctsPresent = cts != null,
        listenerTaskPresent = listenerTask != null, queued = commandQueue.Count, updateSubscriptions = EditorApplication.UpdateSubscriptions,
        playSubscriptions = EditorApplication.PlayModeSubscriptions, delaySubscriptions = EditorTask.DelaySubscriptions,
        discovery = ServerDiscovery.Exists };
}

sealed class FakeListener : IConnectionListener
{
    public bool Started, Disposed;
    public int Stops, DisposeCalls;
    public bool ThrowStop, ThrowDispose;
    public bool IgnoreCancellation;
    public readonly ManualResetEventSlim AcceptStarted = new(false);
    public readonly TaskCompletionSource<IConnectionTransport> PendingAccept = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int AcceptCalls;
    public void Start(string path) { Started = true; LifeFault.Acquired.Add("listener-started"); LifeFault.At("listener-start"); }
    public void Stop() { Stops++; Started = false; if (ThrowStop) throw new IOException("Owned listener Stop failure"); }
    public void Dispose() { DisposeCalls++; Disposed = true; if (ThrowDispose) throw new IOException("Owned listener Dispose failure"); }
    public Task<IConnectionTransport> AcceptClientAsync(CancellationToken token)
    {
        var count = Interlocked.Increment(ref AcceptCalls); AcceptStarted.Set();
        var pending = count == 1 ? PendingAccept : new TaskCompletionSource<IConnectionTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IgnoreCancellation) token.Register(() => pending.TrySetCanceled(token));
        return pending.Task;
    }
}
static class ConnectionFactory
{
    public static readonly List<FakeListener> Instances = new();
    public static IConnectionListener CreateListener() { LifeFault.At("listener-create"); var item = new FakeListener(); Instances.Add(item); LifeFault.Acquired.Add("listener-created"); return item; }
    public static string GetConnectionTypeName() => "owned-no-native-handle";
}
static class ServerDiscovery
{
    public static bool Exists;
    public static string GetConnectionPath() { LifeFault.At("connection-path"); return "owned-no-os-resource"; }
    public static bool SaveConnectionInfo(string path) { Exists = true; LifeFault.Acquired.Add("discovery"); LifeFault.At("discovery"); return LifeFault.Stage != "discovery-false"; }
    public static void DeleteDiscoveryFiles() { Exists = false; LifeFault.At("discovery-delete"); }
}
sealed class ValidationConfig { }
static class ValidatedConfigs { public static ValidationConfig Unity { get { LifeFault.At("validation"); return new ValidationConfig(); } } }
enum PlayModeStateChange { EnteredEditMode, ExitingEditMode, EnteredPlayMode, ExitingPlayMode }
enum LogType { Log, Warning, Error }
static class Application { public static bool isBatchMode; public static string platform => "owned-substrate"; }
static class Debug { public static void LogError(object value) { } }
static class SessionState
{
    public static readonly Dictionary<string, string> Data = new();
    public static string GetString(string key, string fallback) => Data.TryGetValue(key, out var value) ? value : fallback;
    public static void SetString(string key, string value) => Data[key] = value;
}
sealed class CommandRecoveryJournal
{
    public const int RetentionSeconds = 3600;
    public static readonly List<CommandRecoveryJournal> Instances = new();
    public string SessionId => "owned-journal";
    public bool Deactivated;
    public CommandRecoveryJournal(Func<string, string> read, Action<string, string> write) { LifeFault.At("journal"); Instances.Add(this); }
    public void Deactivate() { Deactivated = true; LifeFault.At("journal-deactivate"); }
}
static class TransportStore
{
    public static List<IConnectionTransport> Items = new();
    public static IConnectionTransport[] Clear() { var items = Items.ToArray(); Items = new(); LifeFault.At("transport-clear"); return items; }
}
static class ConnectionCensus { public static void Clear() { LifeFault.At("census-clear"); } }
sealed class FakeTransport : IConnectionTransport
{
    public bool ThrowClose, ThrowDispose, Disposed, Closed;
    public bool ThrowCache;
    public int CloseCalls, DisposeCalls;
    public Task WriteResult = Task.CompletedTask;
    public int WriteCalls;
    public readonly ManualResetEventSlim WriteEntered = new(false);
    public CancellationToken LastWriteToken;
    public byte[] LastWriteBytes;
    public bool IsConnected => !Closed;
    public string ConnectionId => "owned-transport";
    public event Action OnDisconnected;
    public void Close() { CloseCalls++; Closed = true; if (ThrowClose) throw new IOException("Owned client Close failure"); }
    public void Dispose() { DisposeCalls++; Disposed = true; if (ThrowDispose) throw new IOException("Owned client Dispose failure"); }
    public int? GetClientProcessId() => null;
    public void CacheClientProcessId() { if (ThrowCache) throw new IOException("Owned peer cache failure"); }
    public Task WriteAsync(byte[] data, CancellationToken token) { LastWriteBytes = data; LastWriteToken = token; Interlocked.Increment(ref WriteCalls); WriteEntered.Set(); return WriteResult; }
    public Task<byte[]> ReadUntilDelimiterAsync(byte delimiter, int maxBytes, int timeoutMs, CancellationToken token) => Task.FromResult(Array.Empty<byte>());
}
static class EditorApplication
{
    static Action updates, quittingEvent;
    static Action<PlayModeStateChange> playModeEvent;
    static bool compiling;
    public static bool isCompiling { get { LifeFault.At("compilation-check"); return compiling; } set { compiling = value; } }
    public static double timeSinceStartup = 1;
    public static event Action update { add { LifeFault.At("update-subscribe"); updates += value; } remove { updates -= value; } }
    public static event Action quitting { add { LifeFault.At("quitting-subscribe"); quittingEvent += value; } remove { quittingEvent -= value; } }
    public static event Action<PlayModeStateChange> playModeStateChanged { add { LifeFault.At("play-subscribe"); playModeEvent += value; } remove { playModeEvent -= value; } }
    public static int UpdateSubscriptions => updates?.GetInvocationList().Length ?? 0;
    public static int PlayModeSubscriptions => playModeEvent?.GetInvocationList().Length ?? 0;
    public static int QuittingSubscriptions => quittingEvent?.GetInvocationList().Length ?? 0;
    public static Action CaptureUpdates() => updates;
    public static void RaisePlay() => playModeEvent?.Invoke(PlayModeStateChange.EnteredPlayMode);
    public static void RaiseUpdate() => updates?.Invoke();
    public static void Reset() { updates = null; quittingEvent = null; playModeEvent = null; compiling = false; timeSinceStartup = 1; }
}
static class EditorTask
{
    static Action callbacks;
    public static event Action delayCall { add { LifeFault.At("delay-subscribe"); callbacks += value; } remove { callbacks -= value; } }
    public static int DelaySubscriptions => callbacks?.GetInvocationList().Length ?? 0;
    public static Action CaptureDelayed() => callbacks;
    public static void RaiseDelayed() { var prior = callbacks; callbacks = null; prior?.Invoke(); }
    public static void Reset() => callbacks = null;
}
static class AssemblyReloadEvents
{
    static Action beforeEvent, afterEvent;
    public static event Action beforeAssemblyReload { add { LifeFault.At("before-reload-subscribe"); beforeEvent += value; } remove { beforeEvent -= value; } }
    public static event Action afterAssemblyReload { add { LifeFault.At("after-reload-subscribe"); afterEvent += value; } remove { afterEvent -= value; } }
    public static int BeforeSubscriptions => beforeEvent?.GetInvocationList().Length ?? 0;
    public static int AfterSubscriptions => afterEvent?.GetInvocationList().Length ?? 0;
    public static void RaiseBefore() => beforeEvent?.Invoke();
    public static void RaiseAfter() => afterEvent?.Invoke();
    public static void Reset() { beforeEvent = null; afterEvent = null; }
}
