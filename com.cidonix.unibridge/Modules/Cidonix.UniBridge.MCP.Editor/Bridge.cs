using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Connection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Models;
using Cidonix.UniBridge.MCP.Editor.Security;
using Cidonix.UniBridge.MCP.Editor.Settings;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.UI;
using Cidonix.UniBridge.Toolkit;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using ClientInfo = Cidonix.UniBridge.MCP.Editor.Models.ClientInfo;

namespace Cidonix.UniBridge.MCP.Editor
{
    /// <summary>
    /// UniBridge MCP Bridge - Uses named pipes (Windows) / Unix sockets (Mac/Linux)
    /// Cleanly separates connection management from messaging
    /// </summary>
    class Bridge : IDisposable
    {
        // Connection layer
        IConnectionListener listener;
        volatile bool isRunning;
        readonly object startStopLock = new();
        bool isBatchMode; // captured on main thread in Start(), safe to read from background threads
        CancellationTokenSource cts;
        Task listenerTask;
        volatile bool disposed;
        int generationNumber;
        volatile BridgeGeneration currentGeneration;

        sealed class BridgeGeneration
        {
            public readonly int Number;
            public readonly IConnectionListener Listener;
            public readonly CancellationToken Token;
            public readonly McpToolInfo[] Tools;
            public readonly string ToolsHash;
            public readonly CommandRecoveryJournal Recovery;
            public readonly JObject RecoveryHandshake;
            public readonly ValidationConfig Validation;
            public readonly bool IsBatchMode;
            public readonly ConcurrentDictionary<IConnectionTransport, byte> Transports = new();

            public BridgeGeneration(int number, IConnectionListener listener, CancellationToken token,
                McpToolInfo[] tools, string toolsHash, CommandRecoveryJournal recovery,
                JObject recoveryHandshake, ValidationConfig validation, bool batchMode)
            {
                Number = number;
                Listener = listener;
                Token = token;
                Tools = tools;
                ToolsHash = toolsHash;
                Recovery = recovery;
                RecoveryHandshake = recoveryHandshake;
                Validation = validation;
                IsBatchMode = batchMode;
            }
        }

        // Command processing
        int processingCommands;
        readonly ConcurrentDictionary<string, (Command command, TaskCompletionSource<string> tcs, IConnectionTransport client, CancellationToken cancellationToken)> commandQueue = new();
        readonly ConcurrentDictionary<string, TaskCompletionSource<string>> commandWaiters = new();
        const string StoppedResponse = "{\"status\":\"error\",\"error\":\"Bridge stopped\"}";

        // Lifecycle
        bool initScheduled;
        bool ensureUpdateHooked;
        bool isStarting;
        double nextStartAt;
        // Tools
        string s_CurrentToolsHash;
        McpToolInfo[] s_ToolsSnapshot;

        // Connection info
        string currentConnectionPath;

        // Security validation
        ValidationConfig validationConfig;

        // Per-transport state is managed by TransportStore (static, thread-safe).
        // Per-identity approval tracking — one TCS per identity, stays in Bridge.
        static readonly Dictionary<string, TaskCompletionSource<bool>> pendingApprovalsByIdentity = new();
        static readonly object pendingApprovalsLock = new();

        // Main-thread admission journal. A recovery request only observes this evidence;
        // it never invokes the original handler when a response has been lost.
        CommandRecoveryJournal commandRecovery;
        JObject commandRecoveryHandshake;

        // Write serialization — multiple async responses and heartbeats may complete concurrently
        readonly SemaphoreSlim transportWriteLock = new(1, 1);
        readonly CancellationTokenSource writeShutdown = new();
        int activeTransportWrites;
        bool writeResourcesDisposed;

        // Per-connection analytics tracking
        readonly ConcurrentDictionary<IConnectionTransport, McpSessionTracker> transportSessionTrackers = new();

        /// <summary>
        /// Tracks per-connection analytics state for MCP session and tool call metrics.
        /// </summary>
        class McpSessionTracker
        {
            public readonly Stopwatch SessionTimer = Stopwatch.StartNew();
            public readonly Stopwatch TimeToFirstSuccess = Stopwatch.StartNew();
            public int ToolCallCount;
            public string LastToolName;
            public string ClientName;
            public int HadFirstSuccess; // 0 = false, 1 = true; int for Interlocked.CompareExchange
        }

        /// <summary>
        /// Event fired when a client connects or disconnects.
        /// This event is always invoked on the main thread via EditorTask.delayCall.
        /// </summary>
        public static event Action OnClientConnectionChanged;

        /// <summary>
        /// Diagnostic events for testing and observability.
        /// Most events fire immediately (synchronously) from background threads.
        /// OnDialogShown fires on main thread via EditorTask.delayCall.
        /// Event handlers should be thread-safe or marshal to main thread if needed.
        /// </summary>
        public static event Action<string> OnConnectionAttempt;  // Fired immediately when AcceptClientAsync returns (connectionId)
        public static event Action<string, ValidationStatus> OnValidationComplete;  // Fired immediately after validation (connectionId, status)
        public static event Action<string> OnDialogShown;  // Fired on main thread after dialog opens (connectionId)


        /// <summary>
        /// The currently shown approval dialog, if any.
        /// Used by tests to access the actual dialog instance.
        /// </summary>
        public static ConnectionApprovalDialog CurrentApprovalDialog { get; internal set; }

        public bool IsRunning => isRunning;
        public string CurrentConnectionPath => currentConnectionPath;

        /// <summary>
        /// Get the set of currently active identity keys.
        /// Used by ConnectionStore to filter active connections.
        /// </summary>
        public IEnumerable<string> GetActiveIdentityKeys()
        {
            return TransportStore.GetActiveIdentityKeys();
        }

        public string GetClientInfo()
        {
            return ConnectionStore.GetClientInfo(GetActiveIdentityKeys());
        }

        /// <summary>
        /// Disconnect any active connections matching the given identity.
        /// Used when revoking a previously-approved connection from settings.
        /// Server will see connection loss and attempt to reconnect, at which point
        /// it will receive approval_denied during the handshake if status is Rejected.
        /// </summary>
        public void DisconnectConnectionByIdentity(ConnectionIdentity identity)
        {
            if (identity == null || string.IsNullOrEmpty(identity.CombinedIdentityKey))
                return;

            var transports = TransportStore.GetAllTransportsByIdentity(identity.CombinedIdentityKey);
            if (transports.Count > 0)
            {
                McpLog.LogDelayed($"Disconnecting {transports.Count} connection(s) with identity: {identity.CombinedIdentityKey}");
                foreach (var transport in transports)
                {
                    try
                    {
                        transport.Dispose();
                    }
                    catch (Exception ex)
                    {
                        McpLog.LogDelayed($"Error disconnecting transport: {ex.Message}", LogType.Warning);
                    }
                }
            }
            else
            {
                McpLog.LogDelayed($"No active connection found for identity: {identity.CombinedIdentityKey}");
            }
        }

        /// <summary>
        /// Disconnect all active connections.
        /// Used when removing all connections from settings.
        /// </summary>
        public void DisconnectAll()
        {
            var keys = TransportStore.GetActiveIdentityKeys();
            McpLog.LogDelayed($"Disconnecting all connections ({keys.Count} active)");
            var toClose = keys.Select(k => TransportStore.GetTransportByIdentity(k)).Where(t => t != null);
            foreach (var transport in toClose)
            {
                try
                {
                    transport.Dispose();
                }
                catch (Exception ex)
                {
                    McpLog.LogDelayed($"Error disconnecting transport: {ex.Message}", LogType.Warning);
                }
            }
        }

        /// <summary>
        /// Complete a pending approval for a connection with the given identity.
        /// Called from settings UI when user accepts/denies a pending connection.
        /// </summary>
        /// <param name="identityKey">The combined identity key (server+client)</param>
        /// <param name="approved">True to approve, false to deny</param>
        public static void CompletePendingApproval(string identityKey, bool approved)
        {
            if (string.IsNullOrEmpty(identityKey))
                return;

            lock (pendingApprovalsLock)
            {
                if (pendingApprovalsByIdentity.TryGetValue(identityKey, out var tcs))
                {
                    McpLog.LogDelayed($"Completing pending approval for identity {identityKey}: {(approved ? "approved" : "denied")}");
                    tcs.TrySetResult(approved);
                    pendingApprovalsByIdentity.Remove(identityKey);
                }
                else
                {
                    McpLog.LogDelayed($"No pending approval found for identity {identityKey}");
                }
            }
        }

        public Bridge(bool autoScheduleStart = true)
        {
            try
            {
                // Load before subscribing so a failed configuration acquisition cannot
                // retain an otherwise unreachable bridge through static events.
                validationConfig = ValidatedConfigs.Unity;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
                EditorApplication.quitting += Stop;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                McpToolRegistry.ToolsChanged += OnToolsChanged;

                if (autoScheduleStart)
                {
                    // Defer start until the editor is idle and not compiling
                    ScheduleInitRetry();

                    // Add a safety net update hook in case delayCall is missed during reload churn
                    if (!ensureUpdateHooked)
                    {
                        ensureUpdateHooked = true;
                        EditorApplication.update += EnsureStartedOnEditorIdle;
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Start()
        {
            lock (startStopLock)
            {
                if (disposed)
                    return;
                if (isRunning && listener != null)
                {
                    McpLog.Log($"UniBridge MCP bridge already running on {currentConnectionPath}");
                    return;
                }

                Stop();

                IConnectionListener startedListener = null;
                CancellationTokenSource startedCancellation = null;
                CommandRecoveryJournal startedRecovery = null;
                Task startedTask = null;
                var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool discoveryAttempted = false;
                try
                {
                    var startedValidation = ValidatedConfigs.Unity;
                    startedListener = ConnectionFactory.CreateListener();
                    var startedPath = ServerDiscovery.GetConnectionPath();
                    var startedBatchMode = Application.isBatchMode;
                    LogBreadcrumb("Start");
                    ComputeToolsSnapshotAndHash();
                    startedRecovery = new CommandRecoveryJournal(
                        key => SessionState.GetString(key, string.Empty),
                        (key, value) => SessionState.SetString(key, value));
                    var startedHandshake = new JObject
                    {
                        ["version"] = 1,
                        ["mode"] = "query-only",
                        ["sessionId"] = startedRecovery.SessionId,
                        ["retentionSeconds"] = CommandRecoveryJournal.RetentionSeconds
                    };

                    startedListener.Start(startedPath);
                    startedCancellation = new CancellationTokenSource();
                    var generation = new BridgeGeneration(++generationNumber, startedListener,
                        startedCancellation.Token, s_ToolsSnapshot, s_CurrentToolsHash,
                        startedRecovery, startedHandshake, startedValidation, startedBatchMode);
                    EditorApplication.update += ProcessCommands;
                    // The worker cannot accept or observe published state until every
                    // acquisition succeeds. It owns this generation's immutable resources.
                    startedTask = Task.Run(async () =>
                    {
                        if (await startSignal.Task.ConfigureAwait(false))
                            await ListenerLoopAsync(generation).ConfigureAwait(false);
                    });
                    discoveryAttempted = true;
                    if (!ServerDiscovery.SaveConnectionInfo(startedPath))
                        throw new InvalidOperationException("Connection discovery could not be published.");

                    listener = startedListener;
                    cts = startedCancellation;
                    listenerTask = startedTask;
                    commandRecovery = startedRecovery;
                    commandRecoveryHandshake = startedHandshake;
                    validationConfig = startedValidation;
                    currentConnectionPath = startedPath;
                    isBatchMode = startedBatchMode;
                    currentGeneration = generation;
                    isRunning = true;
                    startSignal.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    isRunning = false;
                    currentGeneration = null;
                    startSignal.TrySetResult(false);
                    try { EditorApplication.update -= ProcessCommands; } catch { }
                    try { startedRecovery?.Deactivate(); } catch { }
                    try { startedCancellation?.Cancel(); } catch { }
                    try { startedListener?.Stop(); } catch { }
                    try { startedListener?.Dispose(); } catch { }
                    try { startedCancellation?.Dispose(); } catch { }
                    if (discoveryAttempted)
                        try { ServerDiscovery.DeleteDiscoveryFiles(); } catch { }
                    listener = null;
                    cts = null;
                    listenerTask = null;
                    commandRecovery = null;
                    commandRecoveryHandshake = null;
                    currentConnectionPath = null;
                    s_ToolsSnapshot = null;
                    s_CurrentToolsHash = null;
                    Debug.LogError($"Failed to start MCP Bridge: {ex.Message}");
                    return;
                }
                try { McpLog.Log($"MCP Bridge V2 started using {ConnectionFactory.GetConnectionTypeName()} at {currentConnectionPath} (OS={Application.platform})"); } catch { }
            }
        }

        public void Stop()
        {
            Task toWait = null;
            CancellationTokenSource toDispose = null;
            IConnectionTransport[] toClose = Array.Empty<IConnectionTransport>();
            lock (startStopLock)
            {
                var generation = currentGeneration;
                bool hadResources = isRunning || generation != null || listener != null || cts != null || listenerTask != null;
                isRunning = false;
                currentGeneration = null;
                CancelScheduledStart();
                try { EditorApplication.update -= ProcessCommands; } catch { }
                try { commandRecovery?.Deactivate(); } catch { }
                commandRecovery = null;
                commandRecoveryHandshake = null;
                toDispose = cts;
                cts = null;
                try { toDispose?.Cancel(); } catch { }
                try { listener?.Stop(); } catch { }
                try { listener?.Dispose(); } catch { }
                listener = null;
                toWait = listenerTask;
                listenerTask = null;
                currentConnectionPath = null;
                s_CurrentToolsHash = null;
                s_ToolsSnapshot = null;
                if (hadResources)
                {
                    try { ServerDiscovery.DeleteDiscoveryFiles(); } catch { }
                    IConnectionTransport[] registered = Array.Empty<IConnectionTransport>();
                    try { registered = TransportStore.Clear(); } catch { }
                    toClose = registered.Concat(generation?.Transports.Keys ?? Enumerable.Empty<IConnectionTransport>()).Distinct().ToArray();
                    try { ConnectionCensus.Clear(); } catch { }
                }
                // Queue admission uses this same lock. A stopped generation cannot
                // add a waiter after this drain, even if a read completed during Stop.
                foreach (var queued in commandQueue.Values)
                    queued.tcs.TrySetResult(StoppedResponse);
                commandQueue.Clear();
                foreach (var waiter in commandWaiters.Values)
                    waiter.TrySetResult(StoppedResponse);
                commandWaiters.Clear();
            }
            try { McpLog.ClearOnceKeys(); } catch { }
            foreach (var c in toClose)
            {
                try { c.Close(); } catch { }
                try { c.Dispose(); } catch { }
            }
            if (toWait != null)
            {
                // Wait for listener task to complete (increased timeout for slower CI machines)
                // The listener task should complete quickly after cancellation, but on Linux
                // the accept() call may take a moment to return after the socket is closed
                try { toWait.Wait(1000); } catch { }
            }
            try { toDispose?.Dispose(); } catch { }
            try { McpLog.Log("UniBridge MCP bridge stopped."); } catch { }
        }

        public void Dispose()
        {
            lock (startStopLock)
            {
                if (disposed) return;
                disposed = true;
                try { writeShutdown.Cancel(); } catch { }
            }
            try { Stop(); } catch { }
            try { EditorApplication.update -= EnsureStartedOnEditorIdle; } catch { }
            try { EditorApplication.update -= ProcessCommands; } catch { }
            try { EditorApplication.quitting -= Stop; } catch { }
            try { AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload; } catch { }
            try { AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload; } catch { }
            try { McpToolRegistry.ToolsChanged -= OnToolsChanged; } catch { }
            try { EditorApplication.playModeStateChanged -= OnPlayModeStateChanged; } catch { }
            DisposeWriteResourcesWhenIdle();
        }

        void DisposeWriteResourcesWhenIdle()
        {
            lock (startStopLock)
            {
                if (!disposed || activeTransportWrites != 0 || writeResourcesDisposed)
                    return;
                writeResourcesDisposed = true;
                try { transportWriteLock.Dispose(); } catch { }
                try { writeShutdown.Dispose(); } catch { }
            }
        }

        void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            ScheduleInitRetry();
        }

        void CancelScheduledStart()
        {
            initScheduled = false;
            nextStartAt = 0;
            ensureUpdateHooked = false;
            try { EditorApplication.update -= EnsureStartedOnEditorIdle; } catch { }
            try { EditorTask.delayCall -= InitializeAfterCompilation; } catch { }
        }

        void ScheduleInitRetry()
        {
            lock (startStopLock)
            {
                if (disposed) return;
                if (initScheduled) return;
                initScheduled = true;
                nextStartAt = EditorApplication.timeSinceStartup + 0.20f;
                if (!ensureUpdateHooked)
                {
                    ensureUpdateHooked = true;
                    EditorApplication.update += EnsureStartedOnEditorIdle;
                }
                EditorTask.delayCall += InitializeAfterCompilation;
            }
        }

        // Safety net: ensure the bridge starts shortly after domain reload when editor is idle
        void EnsureStartedOnEditorIdle()
        {
            lock (startStopLock)
            {
                if (disposed || !ensureUpdateHooked) return;
                // Do nothing while compiling
                if (IsCompiling()) return;

                // If already running, remove the hook
                if (isRunning)
                {
                    EditorApplication.update -= EnsureStartedOnEditorIdle;
                    ensureUpdateHooked = false;
                    return;
                }
                // Debounced start: wait until the scheduled time
                if (nextStartAt > 0 && EditorApplication.timeSinceStartup < nextStartAt) return;
                if (isStarting) return;

                isStarting = true;
                // Attempt start; if it succeeds, remove the hook to avoid overhead
                try { Start(); }
                finally { isStarting = false; }

                if (isRunning)
                {
                    EditorApplication.update -= EnsureStartedOnEditorIdle;
                    ensureUpdateHooked = false;
                }
            }
        }

        /// <summary>
        /// Initialize the MCP bridge after Unity is fully loaded and compilation is complete.
        /// This prevents repeated restarts during script compilation that cause port hopping.
        /// </summary>
        void InitializeAfterCompilation()
        {
            lock (startStopLock)
            {
                if (disposed || !initScheduled) return;
                initScheduled = false;

                // Play-mode friendly: allow starting in play mode; only defer while compiling
                if (IsCompiling())
                {
                    ScheduleInitRetry();
                    return;
                }

                if (!isRunning)
                {
                    Start();
                    // If a race prevented start, retry later
                    if (!isRunning) ScheduleInitRetry();
                }
            }
        }

        bool IsCurrentGeneration(BridgeGeneration generation)
        {
            return generation != null && !disposed && isRunning &&
                ReferenceEquals(currentGeneration, generation) && !generation.Token.IsCancellationRequested;
        }

        void RunForGeneration(BridgeGeneration generation, CancellationToken token, Action action)
        {
            lock (startStopLock)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrentGeneration(generation))
                    throw new OperationCanceledException("Bridge generation stopped.", token);
                action();
            }
        }

        CancellationTokenSource CreateTransportCancellation(BridgeGeneration generation)
        {
            lock (startStopLock)
            {
                if (!IsCurrentGeneration(generation))
                    throw new OperationCanceledException("Bridge generation stopped.", generation.Token);
                // Stop cannot cancel/dispose the owning source between the
                // generation check and registering this linked token.
                return CancellationTokenSource.CreateLinkedTokenSource(generation.Token);
            }
        }

        bool TryQueueCommand(BridgeGeneration generation, string commandId, Command command,
            TaskCompletionSource<string> completion, IConnectionTransport transport, CancellationToken token)
        {
            lock (startStopLock)
            {
                if (!IsCurrentGeneration(generation) || token.IsCancellationRequested || !transport.IsConnected)
                {
                    completion.TrySetResult(StoppedResponse);
                    return false;
                }
                commandWaiters[commandId] = completion;
                commandQueue[commandId] = (command, completion, transport, token);
                return true;
            }
        }

        static void CloseAndDisposeTransport(IConnectionTransport transport)
        {
            try { transport?.Close(); } catch { }
            try { transport?.Dispose(); } catch { }
        }

        async Task ListenerLoopAsync(BridgeGeneration generation)
        {
            var token = generation.Token;
            while (IsCurrentGeneration(generation))
            {
                IConnectionTransport clientTransport = null;
                bool handedOff = false;
                try
                {
                    clientTransport = await generation.Listener.AcceptClientAsync(token);
                    lock (startStopLock)
                    {
                        if (!IsCurrentGeneration(generation))
                            break;
                        generation.Transports[clientTransport] = 0;
                    }

                    // Capture peer PID immediately while the socket is still connected.
                    // Deferring this to the background thread risks ENOTCONN if the peer disconnects.
                    clientTransport.CacheClientProcessId();

                    // Fire diagnostic event immediately (not via delayCall)
                    // Event handlers can marshal to main thread if needed, but event fires synchronously
                    var connId = clientTransport.ConnectionId;
                    OnConnectionAttempt?.Invoke(connId);

                    // Fire and forget each client connection
                    var acceptedTransport = clientTransport;
                    // Always run the owner, even if cancellation wins before scheduling;
                    // it must dispose a transport already returned by AcceptClientAsync.
                    _ = Task.Run(() => HandleClientAsync(acceptedTransport, generation));
                    handedOff = true;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (IsCurrentGeneration(generation))
                    {
                        McpLog.LogDelayed($"Listener error: {ex.Message}", LogType.Error);
                    }
                }
                finally
                {
                    if (!handedOff && clientTransport != null)
                    {
                        generation.Transports.TryRemove(clientTransport, out _);
                        CloseAndDisposeTransport(clientTransport);
                    }
                }
            }
        }

        void SetApprovalState(IConnectionTransport transport, ConnectionApprovalState state)
        {
            var ts = TransportStore.GetState(transport);
            if (ts != null)
                ts.ApprovalState = state;
        }

        ConnectionApprovalState GetApprovalState(IConnectionTransport transport)
        {
            return TransportStore.GetState(transport)?.ApprovalState ?? ConnectionApprovalState.Unknown;
        }

        /// <summary>
        /// Apply any ClientInfo received via set_client_info (which may arrive before
        /// validation completes) to the ConnectionStore record.
        /// Called after RecordConnection so the record exists to update.
        /// </summary>
        void ApplyTransportClientInfo(IConnectionTransport transport, string identityKey)
        {
            var clientInfo = TransportStore.GetState(transport)?.ClientInfo;
            if (clientInfo != null)
            {
                ConnectionStore.UpdateClientInfo(identityKey, clientInfo);
            }
        }

        /// <summary>
        /// Send a duplicate_connection notification to a transport before closing it.
        /// The MCP server handles this as a non-retryable error and clears its tool cache.
        /// </summary>
        async Task SendDuplicateNotificationAsync(IConnectionTransport transport, string reason, CancellationToken ct)
        {
            try
            {
                string message = McpJson.SerializeObject(new
                {
                    type = "duplicate_connection",
                    reason
                });
                await WriteWithLockAsync(transport, message, ct);
            }
            catch (OperationCanceledException)
            {
                // The duplicate transport disconnected while the notification was in flight.
            }
            catch (Exception ex)
            {
                McpLog.LogDelayed($"Failed to send duplicate notification: {ex.Message}");
            }
        }

        async Task HandleClientAsync(IConnectionTransport transport, BridgeGeneration generation)
        {
            var token = generation.Token;
            using (transport)
            {
                if (!IsCurrentGeneration(generation))
                {
                    generation.Transports.TryRemove(transport, out _);
                    return;
                }
                CancellationTokenSource transportCts = null;
                Task heartbeatTask = Task.CompletedTask;
                try
                {
                    // Acquisition shares Stop's generation lock; cleanup also owns
                    // any failure before the handshake or state registration.
                    transportCts = CreateTransportCancellation(generation);
                    var transportToken = transportCts.Token;
                    transport.OnDisconnected += () =>
                    {
                        var state = TransportStore.GetState(transport);
                        if (state != null)
                        {
                            var identityKey = state.IdentityKey;
                            var record = ConnectionStore.GetConnectionByIdentity(identityKey);
                            var clientInfo = record?.Info?.ClientInfo ?? state.ClientInfo;
                            if (clientInfo != null)
                            {
                                string displayName = string.IsNullOrEmpty(clientInfo.Title) ? clientInfo.Name : clientInfo.Title;
                                McpLog.LogDelayed($"Client disconnected: {displayName} v{clientInfo.Version}");
                            }
                        }
                    };
                    McpLog.LogDelayed($"Client connected: {transport.ConnectionId}");

                    // === EAGER HANDSHAKE ===
                    // Send handshake immediately - don't block on validation or approval.
                    // Validation and approval run in the background; tool calls are gated in ExecuteCommandAsync.
                    SetApprovalState(transport, ConnectionApprovalState.Unknown);

                    try
                    {
                        // Include pre-warmed tools in handshake to eliminate discovery round trips
                        var handshake = JObject.Parse(MessageProtocol.CreateHandshakeMessage(generation.Tools, generation.ToolsHash));
                        handshake["commandRecovery"] = generation.RecoveryHandshake?.DeepClone();
                        await WriteWithLockAsync(transport, handshake.ToString(Formatting.None), transportToken);
                        McpLog.LogDelayed($"Sent handshake (unity-mcp protocol v2.0, tools={generation.Tools?.Length ?? 0})");
                    }
                    catch (Exception ex)
                    {
                        // errno=32 (EPIPE) = client disconnected before handshake completed (benign race)
                        if (ex.Message.Contains("errno=32"))
                            McpLog.LogDelayed($"Handshake skipped: client disconnected");
                        else
                            McpLog.LogDelayed($"Handshake failed: {ex.Message}", LogType.Warning);
                        return;
                    }

                    // Register with temporary identity key (updated when validation completes)
                    string connectionIdentityKey = $"pending-{transport.ConnectionId}";
                    RunForGeneration(generation, transportToken, () => TransportStore.Register(transport, connectionIdentityKey));

                    // Notify listeners on main thread that a client connected
                    EditorTask.delayCall += () => OnClientConnectionChanged?.Invoke();

                    // Initialize per-connection analytics tracker
                    var sessionTracker = new McpSessionTracker();
                    transportSessionTrackers[transport] = sessionTracker;

                    // Launch background validation + approval (fire-and-forget)
                    // This runs concurrently with the message loop below.
                    // isBatchMode was captured on main thread in Start().
                    _ = ValidateAndApproveAsync(transport, transportToken, generation);

                    // Transport-level heartbeat — sends command_in_progress every 1.5s as a
                    // connection liveness signal. The MCP server (unity-connection.ts) skips
                    // these messages; they never reach external clients (Claude Code, Cursor).
                    // This lets both sides detect stale connections promptly, especially when
                    // long-running tool calls keep the main thread busy.
                    // Scoped to transportToken so it's cancelled when the transport disconnects.
                    heartbeatTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!transportToken.IsCancellationRequested)
                            {
                                await Task.Delay(1500, transportToken);
                                if (transport.IsConnected)
                                {
                                    string msg = MessageProtocol.CreateCommandInProgressMessage();
                                    await WriteWithLockAsync(transport, msg, transportToken);
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }
                        catch (OperationCanceledException) { /* transport disconnected */ }
                        catch { /* transport disposed — stop silently */ }
                    }, transportToken);

                    while (IsCurrentGeneration(generation) && !transportToken.IsCancellationRequested && transport.IsConnected)
                    {
                        try
                        {
                            // Read and parse on the I/O thread (Command is a plain POCO — no Unity deps)
                            // No timeout — idle connections are normal (client sends commands sporadically).
                            // The loop exits when the pipe closes or the listener stops.
                            string commandText = await MessageProtocol.ReadMessageAsync(transport, timeoutMs: -1, cancellationToken: transportToken);
                            transportToken.ThrowIfCancellationRequested();
                            Command command;
                            try
                            {
                                command = McpJson.DeserializeObject<Command>(commandText);
                            }
                            catch
                            {
                                // Malformed JSON — respond with error directly, don't queue
                                string errorResponse = McpJson.SerializeObject(new
                                {
                                    status = "error",
                                    error = "Invalid JSON format",
                                    receivedText = commandText.Length > 50 ? commandText[..50] + "..." : commandText
                                });
                                await WriteWithLockAsync(transport, errorResponse, token);
                                continue;
                            }

                            if (command == null || string.IsNullOrEmpty(command.type))
                            {
                                string errorResponse = McpJson.SerializeObject(new
                                {
                                    status = "error",
                                    error = command == null ? "Command deserialized to null" : "Command type cannot be empty"
                                });
                                await WriteWithLockAsync(transport, errorResponse, token);
                                continue;
                            }

                            // Fast-path: respond to pings on the I/O thread
                            // without going through the main-thread command queue.
                            if (command.type.Equals("ping", StringComparison.OrdinalIgnoreCase))
                            {
                                string pingResponse = InjectRequestId(
                                    McpJson.SerializeObject(new { status = "success", result = new { message = "pong" } }),
                                    command.requestId);
                                await WriteWithLockAsync(transport, pingResponse, token);
                                continue;
                            }

                            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                            string commandId = Guid.NewGuid().ToString();

                            if (!TryQueueCommand(generation, commandId, command, tcs, transport, transportToken))
                                break;

                            // Fire-and-forget: write response when ready (non-blocking).
                            // The reader loop continues immediately so multiple commands
                            // can be in-flight concurrently (multiplexed by requestId).
                            _ = WriteResponseWhenReadyAsync(tcs.Task, transport, token);
                        }
                        catch (Exception ex)
                        {
                            string msg = ex.Message ?? string.Empty;
                            bool isBenign = msg.Contains("closed", StringComparison.OrdinalIgnoreCase)
                                || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                                || msg.Contains("errno=9", StringComparison.Ordinal)  // EBADF — fd closed while reading
                                || msg.Contains("errno=32", StringComparison.Ordinal) // EPIPE — broken pipe
                                || msg.Contains("errno=38", StringComparison.Ordinal) // ENOTSOCK — fd closed while writing
                                || ex is TimeoutException
                                || ex is ObjectDisposedException;

                            if (isBenign)
                                McpLog.LogDelayed($"Client handler: {msg}");
                            else
                                McpLog.LogDelayed($"Client handler error: {msg}", LogType.Error);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // A stopped generation does not enter or retain a client owner.
                }
                catch (Exception ex)
                {
                    McpLog.LogDelayed($"HandleClientAsync unhandled exception for {transport.ConnectionId}: {ex}", LogType.Error);
                }
                finally
                {
                    McpLog.LogDelayed($"HandleClientAsync exiting for transport {transport.ConnectionId} [isConnected={transport.IsConnected}]");

                    // Cancel background validation/approval and heartbeat for this transport
                    try { transportCts?.Cancel(); } catch { /* best-effort */ }

                    // Wait briefly for heartbeat task to stop
                    try { await Task.WhenAny(heartbeatTask, Task.Delay(500)); } catch { /* best-effort */ }
                    try { transportCts?.Dispose(); } catch { /* best-effort */ }

                    // Remove all per-transport state (identity mappings,
                    // approval state, validation decisions).
                    var removedState = TransportStore.Remove(transport);
                    bool clientRemoved = removedState != null;

                    // Always unregister from the census so logical-client counts shrink
                    // immediately. Safe to call even if the transport was never registered.
                    ConnectionCensus.UnregisterTransport(transport);

                    // Note: we do NOT cancel or remove pendingApprovalsByIdentity TCS here.
                    // The approval dialog may still be open, and the user can approve/deny
                    // the identity for future connections. ValidateAndApproveAsync registers
                    // a continuation to process the result after it detects disconnection.

                    transportSessionTrackers.TryRemove(transport, out _);
                    generation.Transports.TryRemove(transport, out _);

                    // Notify listeners on main thread that a client disconnected
                    if (clientRemoved)
                    {
                        EditorTask.delayCall += () => OnClientConnectionChanged?.Invoke();
                    }
                }
            }
        }

        /// <summary>
        /// Runs validation and approval in the background, concurrently with the message loop.
        /// Updates approval state so that ExecuteCommandAsync can gate tool calls.
        /// </summary>
        async Task ValidateAndApproveAsync(IConnectionTransport transport, CancellationToken token, BridgeGeneration generation)
        {
            var validationConfig = generation.Validation;
            var isBatchMode = generation.IsBatchMode;
            try
            {
                SetApprovalState(transport, ConnectionApprovalState.Validating);

                ValidationDecision decision = null;

                // Run expensive validation (SHA256, signatures) on background thread
                if (validationConfig != null && validationConfig.Enabled && validationConfig.Mode != ValidationMode.Disabled)
                {
                    try
                    {
                        var validationStart = DateTime.Now;
                        decision = await Task.Run(() => ConnectionValidator.ValidateConnection(transport, validationConfig));
                        var validationMs = (DateTime.Now - validationStart).TotalMilliseconds;
                        McpLog.LogDelayed($"[TIMING] Validation took {validationMs:F0}ms");

                        // Exit early if transport disconnected during validation
                        token.ThrowIfCancellationRequested();

                        OnValidationComplete?.Invoke(transport.ConnectionId, decision.Status);
                        LogConnectionDecision(decision);

                        if (!decision.IsAccepted)
                        {
                            McpLog.LogDelayed($"Connection rejected: {decision.Reason}", LogType.Warning);
                            SetApprovalState(transport, ConnectionApprovalState.Denied);
                            return;
                        }

                        if (decision.Status == ValidationStatus.Warning)
                        {
                            McpLog.LogDelayed($"Connection allowed with warning: {decision.Reason}", LogType.Warning);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Connection closed during validation (e.g. brief probe) — nothing to do
                        return;
                    }
                    catch (Exception ex)
                    {
                        McpLog.LogDelayed($"Validation exception: {ex.Message}\n{ex.StackTrace}", LogType.Error);

                        if (validationConfig.Mode == ValidationMode.Strict)
                        {
                            SetApprovalState(transport, ConnectionApprovalState.Denied);
                            return;
                        }

                        McpLog.LogDelayed("Allowing connection despite validation error (LogOnly mode)", LogType.Warning);
                    }
                }
                else
                {
                    McpLog.LogDelayed(
                        "Connection validation is DISABLED - connections will not appear in MCP Settings UI. " +
                        "This should only be used for automated tests.",
                        LogType.Warning);
                    SetApprovalState(transport, ConnectionApprovalState.Approved);
                    return;
                }

                if (decision == null)
                {
                    SetApprovalState(transport, ConnectionApprovalState.Approved);
                    return;
                }

                // Update identity mapping with real identity
                var identity = ConnectionIdentity.FromConnectionInfo(decision.Connection);
                RunForGeneration(generation, token, () =>
                {
                    var transportState = TransportStore.GetState(transport);
                    if (transportState != null)
                        transportState.ValidationDecision = decision;
                    if (identity != null && !string.IsNullOrEmpty(identity.CombinedIdentityKey))
                        TransportStore.UpdateIdentityKey(transport, identity.CombinedIdentityKey);
                });

                // Exit early if transport disconnected during identity mapping
                token.ThrowIfCancellationRequested();

                // Client process not identifiable — likely a probe connection (e.g. Codex startup).
                // Wait briefly; if it disconnects, clean up silently without showing a dialog.
                if (decision.Connection.Client == null)
                {
                    McpLog.LogDelayed("Client process not identifiable — waiting to determine if probe");
                    try
                    {
                        await Task.Delay(5000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        McpLog.LogDelayed("Probe connection disconnected during grace period — ignoring");
                        return;
                    }
                    McpLog.LogDelayed("Connection persists with unidentifiable client — proceeding with approval");
                }

                TaskCompletionSource<bool> approvalTcs;
                string identityKey = identity?.CombinedIdentityKey;
                lock (startStopLock)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrentGeneration(generation))
                        throw new OperationCanceledException("Bridge generation stopped.", token);
                    var policy = MCPSettingsManager.Settings.connectionPolicies.direct;

                    // Check if origin is allowed
                    if (!policy.allowed)
                    {
                        McpLog.LogDelayed("Connection policy denied: local MCP connections are not allowed", LogType.Warning);
                        SetApprovalState(transport, ConnectionApprovalState.Denied);
                        return;
                    }

                    // Enforce the local MCP capacity limit. The census dedupes multiple
                    // transports from the same logical client (codex probes, etc.) so they
                    // collapse to one slot.
                    var reservation = ConnectionCensus.TryReserveDirect(decision.Connection);
                    if (!reservation.Allowed)
                    {
                        decision.Status = ValidationStatus.CapacityLimit;
                        decision.Reason = BuildCapacityDenialReason(reservation);
                        var capacityDecision = decision;
                        ConnectionStore.RecordConnection(capacityDecision);
                        ApplyTransportClientInfo(transport, identity.CombinedIdentityKey);
                        SetApprovalState(transport, ConnectionApprovalState.Denied);
                        return;
                    }

                    ConnectionCensus.RegisterDirectTransport(transport, decision.Connection);

                    // If approval not required, auto-approve
                    if (!policy.requiresApproval)
                    {
                        var decisionToRecord = decision;
                        ConnectionStore.RecordConnection(decisionToRecord);
                        ApplyTransportClientInfo(transport, identity.CombinedIdentityKey);
                        SetApprovalState(transport, ConnectionApprovalState.Approved);
                        return;
                    }

                    // Batch mode: auto-approve or deny based on setting (no UI available)
                    if (isBatchMode)
                    {
                        if (MCPSettingsManager.Settings.autoApproveInBatchMode)
                        {
                            McpLog.LogDelayed("Batch mode: auto-approving connection");
                            var decisionToRecord = decision;
                            ConnectionStore.RecordConnection(decisionToRecord);
                            ApplyTransportClientInfo(transport, identity.CombinedIdentityKey);
                            SetApprovalState(transport, ConnectionApprovalState.Approved);
                        }
                        else
                        {
                            McpLog.LogDelayed("Batch mode: auto-approve disabled, denying connection", LogType.Warning);
                            SetApprovalState(transport, ConnectionApprovalState.Denied);
                        }
                        return;
                    }

                    // Check existing approval history (exact identity match, then publisher fallback)
                    var existingRecord = ConnectionStore.FindMatchingConnection(decision.Connection)
                        ?? ConnectionStore.FindMatchingConnectionByPublisher(decision.Connection);

                    if (existingRecord != null &&
                        (existingRecord.Status == ValidationStatus.Accepted ||
                         existingRecord.Status == ValidationStatus.Warning ||
                         existingRecord.Status == ValidationStatus.CapacityLimit))
                    {
                        McpLog.LogDelayed("Connection auto-approved: previously accepted by user");
                        var decisionToRecord = new ValidationDecision
                        {
                            Status = ValidationStatus.Accepted,
                            Reason = "Auto-approved: previously accepted",
                            Connection = decision.Connection
                        };
                        ConnectionStore.RecordConnection(decisionToRecord);
                        ApplyTransportClientInfo(transport, identity.CombinedIdentityKey);
                        SetApprovalState(transport, ConnectionApprovalState.Approved);
                        return;
                    }

                    if (existingRecord != null && existingRecord.Status == ValidationStatus.Rejected)
                    {
                        McpLog.LogDelayed("Connection denied: previously rejected by user", LogType.Warning);
                        SetApprovalState(transport, ConnectionApprovalState.Denied);
                        return;
                    }

                    // Normal Editor mode can optionally auto-approve new local MCP clients.
                    // Previously rejected identities remain denied by the history check above.
                    if (!isBatchMode && MCPSettingsManager.Settings.autoApproveInEditorMode)
                    {
                        McpLog.LogDelayed("Editor mode: auto-approving new connection");
                        var decisionToRecord = new ValidationDecision
                        {
                            Status = ValidationStatus.Accepted,
                            Reason = "Auto-approved in Editor Mode",
                            Connection = decision.Connection
                        };
                        ConnectionStore.RecordConnection(decisionToRecord);
                        ApplyTransportClientInfo(transport, identity.CombinedIdentityKey);
                        SetApprovalState(transport, ConnectionApprovalState.Approved);
                        return;
                    }

                    // Exit early if transport disconnected before recording Pending
                    token.ThrowIfCancellationRequested();

                    // New connection — needs user approval
                    SetApprovalState(transport, ConnectionApprovalState.AwaitingApproval);

                    // Record as Pending
                    var pendingDecision = new ValidationDecision
                    {
                        Status = ValidationStatus.Pending,
                        Reason = "Awaiting user approval",
                        Connection = decision.Connection
                    };
                    ConnectionStore.RecordConnection(pendingDecision);
                    ApplyTransportClientInfo(transport, identity.CombinedIdentityKey);

                    // Show dialog proactively
                    if (string.IsNullOrEmpty(identityKey))
                    {
                        McpLog.LogDelayed("Unable to determine identity key for approval", LogType.Warning);
                        SetApprovalState(transport, ConnectionApprovalState.Denied);
                        return;
                    }

                    lock (pendingApprovalsLock)
                    {
                        if (!pendingApprovalsByIdentity.TryGetValue(identityKey, out approvalTcs) ||
                            approvalTcs.Task.IsCompleted)
                        {
                            approvalTcs = new TaskCompletionSource<bool>();
                            pendingApprovalsByIdentity[identityKey] = approvalTcs;
                        }
                    }

                    // Show dialog on main thread
                    ShowApprovalDialogForTransport(transport);
                }

                // Await user decision or transport disconnection
                var cancellationTcs = new TaskCompletionSource<bool>();
                using var reg = token.Register(() => cancellationTcs.TrySetCanceled());

                var completedTask = await Task.WhenAny(approvalTcs.Task, cancellationTcs.Task);

                if (completedTask == cancellationTcs.Task || token.IsCancellationRequested)
                {
                    // Transport disconnected while awaiting approval.
                    // Don't cancel the TCS — the approval dialog may still be open and
                    // the user can approve/deny the identity for future connections.
                    McpLog.LogDelayed("Connection disconnected while awaiting approval");

                    // Update registry reason so settings UI shows the disconnection
                    var disconnectedRecord = ConnectionStore.FindMatchingConnection(identity);
                    if (disconnectedRecord != null && disconnectedRecord.Status == ValidationStatus.Pending)
                    {
                        ConnectionStore.UpdateConnectionStatus(
                            disconnectedRecord.Info.ConnectionId,
                            ValidationStatus.Pending,
                            "Client disconnected \u2014 approve to allow future connections from this client");
                    }

                    // Register continuation so the user's decision (from dialog or settings)
                    // is processed even though this method is returning.
                    _ = approvalTcs.Task.ContinueWith(task =>
                    {
                        if (!task.IsCompletedSuccessfully) return;
                        bool userApproved = task.Result;

                        lock (pendingApprovalsLock)
                        {
                            if (pendingApprovalsByIdentity.TryGetValue(identityKey, out var pending) && ReferenceEquals(pending, approvalTcs))
                                pendingApprovalsByIdentity.Remove(identityKey);
                        }

                        var record = ConnectionStore.FindMatchingConnection(identity);
                        if (record == null) return;

                        if (userApproved)
                        {
                            McpLog.Log("Connection approved by user (after disconnect)");
                            ConnectionStore.UpdateConnectionStatus(
                                record.Info.ConnectionId,
                                ValidationStatus.Accepted,
                                "Approved by user");
                        }
                        else
                        {
                            McpLog.Warning("Connection denied by user (after disconnect)");
                            ConnectionStore.UpdateConnectionStatus(
                                record.Info.ConnectionId,
                                ValidationStatus.Rejected,
                                "Denied by user");
                        }
                    }, TaskScheduler.Default);

                    return;
                }

                bool approved = await approvalTcs.Task; // Already completed, won't block

                lock (pendingApprovalsLock)
                {
                    if (pendingApprovalsByIdentity.TryGetValue(identityKey, out var pending) && ReferenceEquals(pending, approvalTcs))
                        pendingApprovalsByIdentity.Remove(identityKey);
                }
                RunForGeneration(generation, token, () =>
                {
                    if (approved)
                    {
                        McpLog.LogDelayed("Connection approved by user");
                        SetApprovalState(transport, ConnectionApprovalState.Approved);

                        var approvedRecord = ConnectionStore.FindMatchingConnection(identity);
                        if (approvedRecord != null)
                        {
                            ConnectionStore.UpdateConnectionStatus(
                                approvedRecord.Info.ConnectionId,
                                ValidationStatus.Accepted,
                                "Approved by user");
                        }
                    }
                    else
                    {
                        McpLog.LogDelayed("Connection denied by user", LogType.Warning);
                        SetApprovalState(transport, ConnectionApprovalState.Denied);

                        var rejectedRecord = ConnectionStore.FindMatchingConnection(identity);
                        if (rejectedRecord != null)
                        {
                            ConnectionStore.UpdateConnectionStatus(
                                rejectedRecord.Info.ConnectionId,
                                ValidationStatus.Rejected,
                                "Denied by user");
                        }
                        // Do NOT close the connection — tool calls will fail with error message
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Transport disconnected during validation — not an error
                McpLog.LogDelayed("Validation cancelled: client disconnected");
            }
            catch (Exception ex)
            {
                McpLog.LogDelayed($"Background validation/approval error: {ex.Message}", LogType.Error);
                SetApprovalState(transport, ConnectionApprovalState.Denied);
            }
        }

        /// <summary>
        /// Show the approval dialog for a transport that hasn't been approved yet.
        /// Called both proactively (after validation) and reactively (on tool call rejection).
        /// Can re-show the dialog if previously dismissed without a decision.
        /// </summary>
        void ShowApprovalDialogForTransport(IConnectionTransport transport)
        {
            var ts = TransportStore.GetState(transport);
            if (ts == null)
                return;

            // Can only show dialog if validation is complete
            var decision = ts.ValidationDecision;
            if (decision == null)
                return;

            if (ts.ApprovalState != ConnectionApprovalState.AwaitingApproval)
                return;

            var identity = ConnectionIdentity.FromConnectionInfo(decision.Connection);
            string identityKey = identity?.CombinedIdentityKey;
            if (string.IsNullOrEmpty(identityKey)) return;

            TaskCompletionSource<bool> approvalTcs;
            lock (pendingApprovalsLock)
            {
                if (!pendingApprovalsByIdentity.TryGetValue(identityKey, out approvalTcs) ||
                    approvalTcs.Task.IsCompleted)
                {
                    // Create new TCS if completed (e.g. dialog was dismissed and re-triggered)
                    approvalTcs = new TaskCompletionSource<bool>();
                    pendingApprovalsByIdentity[identityKey] = approvalTcs;
                }
            }

            var eventConnId = transport.ConnectionId;
            var tcs = approvalTcs;
            var decisionForDialog = decision;
            EditorTask.delayCall += () =>
            {
                try
                {
                    if (!tcs.Task.IsCompleted)
                    {
                        CurrentApprovalDialog = ConnectionApprovalDialog.ShowApprovalDialog(decisionForDialog, tcs);
                        OnDialogShown?.Invoke(eventConnId);
                    }
                }
                catch (Exception ex)
                {
                    McpLog.LogDelayed($"Error showing approval dialog: {ex.Message}", LogType.Error);
                    tcs.TrySetResult(false);
                }
            };
        }

        void ProcessCommands()
        {
            if (!isRunning) return;
            // Reentrancy guard
            if (Interlocked.Exchange(ref processingCommands, 1) == 1) return;

            try
            {
                if (commandQueue.IsEmpty)
                    return;

                // Snapshot commands (already parsed on the I/O thread)
                var work = commandQueue.Select(kvp => (kvp.Key, kvp.Value.command, kvp.Value.tcs, kvp.Value.client, kvp.Value.cancellationToken)).ToList();

                foreach (var item in work)
                {
                    string id = item.Key;
                    Command command = item.command;
                    TaskCompletionSource<string> tcs = item.tcs;
                    IConnectionTransport client = item.client;
                    CancellationToken cancellationToken = item.cancellationToken;

                    try
                    {
                        // Remove from queue BEFORE starting async execution to prevent
                        // re-processing on subsequent Update frames
                        Task<string> resultTask;
                        lock (startStopLock)
                        {
                            if (!isRunning || cancellationToken.IsCancellationRequested || !commandQueue.TryRemove(id, out _))
                            {
                                tcs.TrySetResult(StoppedResponse);
                                commandWaiters.TryRemove(id, out _);
                                continue;
                            }
                            // Capture admission's journal before Stop may publish another
                            // generation; asynchronous completion keeps that original owner.
                            resultTask = DispatchCommandAsync(command, client, cancellationToken);
                        }
                        _ = CompleteQueuedCommandAsync(resultTask, tcs, command.requestId, id);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error processing command: {ex.Message}\\n{ex.StackTrace}");
                        tcs.TrySetResult(InjectRequestId(McpJson.SerializeObject(new
                        {
                            status = "error",
                            error = ex.Message,
                            commandType = command.type ?? "Unknown"
                        }), command.requestId));
                        commandWaiters.TryRemove(id, out _);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref processingCommands, 0);
            }
        }

        // ========================================================================
        // Multiplexed protocol helpers
        // ========================================================================

        /// <summary>
        /// Inject requestId into a JSON response string for multiplexed routing.
        /// If requestId is null/empty or the response isn't valid JSON, returns the response as-is.
        /// </summary>
        static string InjectRequestId(string response, string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
                return response;

            try
            {
                var jobj = JObject.Parse(response);
                jobj["requestId"] = requestId;
                return jobj.ToString(Formatting.None);
            }
            catch
            {
                return response;
            }
        }

        /// <summary>
        /// Awaits a response task and writes the result to the transport with write serialization.
        /// Used by the listener loop to fire-and-forget response writes.
        /// </summary>
        async Task WriteResponseWhenReadyAsync(Task<string> responseTask, IConnectionTransport transport, CancellationToken ct)
        {
            try
            {
                string response = await responseTask.ConfigureAwait(false);
                await WriteWithLockAsync(transport, response, ct);
            }
            catch (OperationCanceledException) { /* connection closed */ }
            catch (Exception ex)
            {
                McpLog.LogDelayed($"Failed to write response: {ex.Message}");
            }
        }

        /// <summary>
        /// Write a message to the transport, serialized with the write lock to prevent
        /// interleaved writes from concurrent responses and heartbeats.
        /// </summary>
        async Task WriteWithLockAsync(IConnectionTransport transport, string message, CancellationToken ct = default)
        {
            CancellationTokenSource writeCancellation;
            lock (startStopLock)
            {
                if (disposed)
                    throw new OperationCanceledException("Bridge disposed.", ct);
                writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, writeShutdown.Token);
                activeTransportWrites++;
            }
            bool acquired = false;
            try
            {
                await transportWriteLock.WaitAsync(writeCancellation.Token);
                acquired = true;
                await MessageProtocol.WriteMessageAsync(transport, message, writeCancellation.Token);
            }
            finally
            {
                // The admitted writer owns the semaphore until Release. Dispose
                // cancels waiting writers and releases resources only after all
                // admitted operations have reached this cleanup.
                if (acquired)
                    transportWriteLock.Release();
                writeCancellation.Dispose();
                lock (startStopLock)
                {
                    activeTransportWrites--;
                    DisposeWriteResourcesWhenIdle();
                }
            }
        }

        async Task<string> DispatchCommandAsync(Command command, IConnectionTransport client, CancellationToken cancellationToken)
        {
            var journal = commandRecovery;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // These protocol-management commands have no project handler and no result cache.
                // Keep the eager handshake usable while connection validation is still running.
                if (IsProtocolManagementCommand(command.type))
                    return await ExecuteCommandAsync(command, client, cancellationToken);

                var identity = await RequireCommandAdmissionAsync(command, client, cancellationToken);

                if (string.Equals(command.type, "recover_command", StringComparison.Ordinal))
                {
                    var args = command.@params ?? new JObject();
                    var originalType = args.Value<string>("originalType");
                    var originalParams = args["originalParams"] as JObject;
                    if (originalParams == null || string.IsNullOrEmpty(originalType))
                        return RecoveryEnvelope(args.Value<string>("originalRequestId"),
                            new CommandRecoveryJournal.RecoveryResult { State = "outcome_unknown", Reason = "invalid_recovery_arguments" });
                    // Guard both the current request and the immutable original arguments before
                    // observing any cached result. This path never calls ExecuteCommandAsync.
                    ProjectContextGuard.AssertExpectedRootForPolicy(originalType, originalParams,
                        ToolExecutionScheduler.ResolvePolicy(originalType, originalParams, McpToolRegistry.GetTool(originalType ?? string.Empty)));
                    var recovered = journal?.Query(identity, args.Value<string>("originalRequestId"), originalType,
                        originalParams, args.Value<string>("expectedSessionId"))
                        ?? new CommandRecoveryJournal.RecoveryResult { State = "outcome_unknown", Reason = "journal_unavailable" };
                    return RecoveryEnvelope(args.Value<string>("originalRequestId"), recovered);
                }

                if (string.IsNullOrEmpty(command.requestId))
                    return await ExecuteCommandAsync(command, client, cancellationToken);

                var policy = ToolExecutionScheduler.ResolvePolicy(command.type, command.@params,
                    McpToolRegistry.GetTool(command.type ?? string.Empty));
                bool persist = RequiresDurableCommandEvidence(command.type, command.@params, policy,
                    McpToolRegistry.GetTool(command.type ?? string.Empty));
                var admission = journal?.Begin(identity, command.requestId, command.type, command.@params, persist);
                if (admission == null)
                    return RecoveryRefusal(command.requestId, "outcome_unknown", "journal_unavailable");
                if (!admission.ShouldExecute)
                {
                    if (admission.Existing.State == "completed")
                        return admission.Existing.Response;
                    if (admission.Existing.State == "in_flight")
                        return await admission.Record.Completion;
                    return RecoveryRefusal(command.requestId, admission.Existing.State, admission.Existing.Reason);
                }

                // The durable started record was published before admission. Complete catches
                // terminal handler errors as well, and persists before this response is released.
                var response = await ExecuteCommandAsync(command, client, cancellationToken);
                journal.Complete(admission.Record, response);
                return response;
            }
            catch (Exception ex)
            {
                return McpJson.SerializeObject(new
                {
                    status = "error",
                    error = ex.Message,
                    command = command?.type ?? "Unknown",
                    projectContext = ProjectContextGuard.BuildProjectContext()
                });
            }
        }

        static bool IsProtocolManagementCommand(string type)
        {
            return string.Equals(type, "set_client_info", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "get_available_tools", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "mcp/request_tool_approval", StringComparison.OrdinalIgnoreCase);
        }

        static bool RequiresDurableCommandEvidence(string type, JObject parameters, ToolExecutionPolicy policy, IToolHandler handler)
        {
            if (policy != ToolExecutionPolicy.ReadOnly && policy != ToolExecutionPolicy.Observer)
                return true;

            // Scheduling a command as a read is not proof that it has no side effects:
            // exports, snapshot persistence, probe resets, and custom observers also need
            // durable admission so a raw duplicate cannot execute them again after reload.
            var contract = McpJson.ObjectFromObject(ToolExecutionScheduler.BuildAnnotation(type, handler));
            if (contract.Value<bool>("replaySafe"))
                return false;

            // These built-in mixed tools have separately inspected pure read actions.
            // Keep ordinary Editor/Console polling out of the durable write budget.
            // An explicit custom policy without replay opt-in cannot inherit this exception.
            if (handler?.Attribute?.ExecutionPolicy != ToolExecutionPolicy.Auto)
                return true;
            if (type == "UniBridge_ManageEditor" || type == "UniBridge_ManageScene" ||
                type == "UniBridge_WorkSession" || type == "UniBridge_EditorSnapshot" ||
                type == "UniBridge_VersionControl")
                return false; // The resolved read policy already selects their pure actions.

            var action = (parameters?["Action"] ?? parameters?["action"] ??
                parameters?["operation"] ?? parameters?["Operation"])?.ToString();
            if (type == "UniBridge_ReadConsole")
            {
                if (string.IsNullOrEmpty(action)) action = "Get";
                return !new[] { "Get", "ReadSinceMarker", "Search", "Overview", "Groups", "GroupDetails",
                    "Timeline", "TimelineWindow", "DiagnosticSummary", "ImportantRanges" }
                    .Any(candidate => string.Equals(candidate, action, StringComparison.OrdinalIgnoreCase));
            }
            if (type == "UniBridge_EditorEvents")
                return !string.IsNullOrEmpty(action) &&
                    !string.Equals(action, "Snapshot", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(action, "Get", StringComparison.OrdinalIgnoreCase);

            // Conservatively retain evidence for other dynamic/unknown read contracts.
            return true;
        }

        async Task<string> RequireCommandAdmissionAsync(Command command, IConnectionTransport client, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            TransportState state;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                state = TransportStore.GetState(client);
                if (!isRunning || state == null || !client.IsConnected)
                    throw new InvalidOperationException("Connection closed before command admission; the command was not executed.");
                if (state.ApprovalState == ConnectionApprovalState.Denied)
                    throw new InvalidOperationException("Connection revoked. Go to Unity Editor > Project Settings > UniBridge > MCP to change approval.");
                if (state.ApprovalState == ConnectionApprovalState.Approved)
                    break;
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("Connection identity or approval is not ready; the command was not executed.");
                await Task.Delay(25, cancellationToken);
            }

            // Apply the ordinary tool/project policy before every admission/cache/query lookup.
            // The internal recovery request is conservatively guarded like a mutating command.
            var policy = string.Equals(command.type, "recover_command", StringComparison.Ordinal)
                ? ToolExecutionPolicy.Mutating
                : ToolExecutionScheduler.ResolvePolicy(command.type, command.@params, McpToolRegistry.GetTool(command.type ?? string.Empty));
            ProjectContextGuard.AssertExpectedRootForPolicy(command.type, command.@params, policy);

            // Validation-disabled or unidentified transports cannot share results across reconnects.
            // Use a private transport namespace instead of accidentally sharing an "Unknown" key.
            return string.IsNullOrEmpty(state.IdentityKey) || state.IdentityKey.StartsWith("pending-", StringComparison.Ordinal) ||
                state.IdentityKey.IndexOf("Unknown:", StringComparison.Ordinal) >= 0
                ? "transport:" + client.ConnectionId
                : state.IdentityKey;
        }

        static string RecoveryEnvelope(string originalRequestId, CommandRecoveryJournal.RecoveryResult recovered)
        {
            return new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject
                {
                    ["state"] = recovered.State,
                    ["originalRequestId"] = originalRequestId,
                    ["response"] = recovered.Response == null ? null : JObject.Parse(recovered.Response),
                    ["reason"] = recovered.Reason
                }
            }.ToString(Formatting.None);
        }

        static string RecoveryRefusal(string requestId, string state, string reason)
        {
            return McpJson.SerializeObject(new
            {
                status = "error",
                error = state == "request_conflict"
                    ? "Request ID was already used for different arguments; command refused."
                    : "The earlier command outcome is unknown; command refused. Inspect project state before issuing a new operation.",
                commandRecovery = new { state, originalRequestId = requestId, reason }
            });
        }

        async Task<string> ExecuteCommandAsync(Command command, IConnectionTransport client, CancellationToken cancellationToken = default)
        {
            McpSessionTracker sessionTracker = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(command.type))
                {
                    return McpJson.SerializeObject(new
                    {
                        status = "error",
                        error = "Command type cannot be empty",
                        projectContext = ProjectContextGuard.BuildProjectContext()
                    });
                }

                if (command.type.Equals("set_client_info", StringComparison.OrdinalIgnoreCase))
                {
                    string name = command.@params?.Value<string>("name") ?? "unknown";
                    string version = command.@params?.Value<string>("version") ?? "unknown";
                    string title = command.@params?.Value<string>("title");

                    var clientInfo = new ClientInfo
                    {
                        Name = name,
                        Version = version,
                        Title = title,
                        ConnectionId = client.ConnectionId
                    };

                    // Store on TransportState (survives the pending→real identity transition)
                    var clientState = TransportStore.GetState(client);
                    if (clientState != null)
                    {
                        clientState.ClientInfo = clientInfo;
                        // Also try to update ConnectionStore — may fail if identity is still pending
                        ConnectionStore.UpdateClientInfo(clientState.IdentityKey, clientInfo);
                    }

                    if (transportSessionTrackers.TryGetValue(client, out var tracker))
                    {
                        tracker.ClientName = name;
                    }

                    string displayName = string.IsNullOrEmpty(title) ? name : title;
                    McpLog.Log($"MCP client info: {displayName} v{version}");

                    return McpJson.SerializeObject(new
                    {
                        status = "success",
                        result = new { message = "Client info received" }
                    });
                }

                if (command.type.Equals("get_available_tools", StringComparison.OrdinalIgnoreCase))
                {
                    string requestedHash = command.@params?.Value<string>("hash");

                    // Recompute when hash is missing or differs from request
                    if (string.IsNullOrEmpty(s_CurrentToolsHash) || s_CurrentToolsHash != requestedHash)
                    {
                        ComputeToolsSnapshotAndHash();
                        McpLog.Log($"Tools changed: hash={s_CurrentToolsHash}, count={s_ToolsSnapshot?.Length ?? 0}");
                    }
                    // No logging for unchanged case - it's periodic polling noise

                    if (requestedHash == s_CurrentToolsHash)
                    {
                        return McpJson.SerializeObject(new
                        {
                            status = "success",
                            result = new { unchanged = true, hash = s_CurrentToolsHash }
                        });
                    }

                    var response = McpJson.SerializeObject(new
                    {
                        status = "success",
                        result = new
                        {
                            hash = s_CurrentToolsHash,
                            tools = s_ToolsSnapshot ?? Array.Empty<McpToolInfo>()
                        }
                    });
                    McpLog.Log($"Sending tools response with {s_ToolsSnapshot?.Length ?? 0} tools");
                    return response;
                }

                // Handle MCP tool approval requests (from Codex via MCP)
                if (command.type.Equals("mcp/request_tool_approval", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleMcpToolApprovalAsync(command);
                }

                // Approval gate — accept-by-default policy.
                // All commands above (set_client_info, get_available_tools,
                // mcp/request_tool_approval, ping) are exempt.
                // Tool calls are allowed in all states EXCEPT Denied (user explicitly revoked).
                var approvalState = GetApprovalState(client);
                if (approvalState == ConnectionApprovalState.Denied)
                {
                    return McpJson.SerializeObject(new
                    {
                        status = "error",
                        error = "Connection revoked. Go to Unity Editor > Project Settings > UniBridge > MCP to change approval.",
                        isError = true,
                        projectContext = ProjectContextGuard.BuildProjectContext()
                    });
                }

                // Use JObject for parameters as the handlers expect this
                JObject paramsObject = command.@params ?? new JObject();

                transportSessionTrackers.TryGetValue(client, out sessionTracker);
                var result = await McpToolRegistry.ExecuteToolAsync(command.type, paramsObject, cancellationToken);

                if (result == null)
                    result = Response.Success("Operation completed.");

                // Update session tracker fields atomically (accessed from concurrent tool calls)
                if (sessionTracker != null)
                {
                    Interlocked.Increment(ref sessionTracker.ToolCallCount);
                    sessionTracker.LastToolName = command.type;

                    if (Interlocked.CompareExchange(ref sessionTracker.HadFirstSuccess, 1, 0) == 0)
                    {
                        sessionTracker.TimeToFirstSuccess.Stop();
                    }
                }

                // Standard success response format
                return McpJson.SerializeObject(new { status = "success", result });
            }
            catch (OperationCanceledException ex)
            {
                if (sessionTracker != null)
                {
                    Interlocked.Increment(ref sessionTracker.ToolCallCount);
                    sessionTracker.LastToolName = command.type;
                }

                McpLog.Log($"Command '{command?.type ?? "Unknown"}' was canceled: {ex.Message}");
                return McpJson.SerializeObject(new
                {
                    status = "error",
                    error = ex.Message,
                    canceled = true,
                    command = command?.type ?? "Unknown",
                    projectContext = ProjectContextGuard.BuildProjectContext()
                });
            }
            catch (Exception ex)
            {
                if (sessionTracker != null)
                {
                    Interlocked.Increment(ref sessionTracker.ToolCallCount);
                    sessionTracker.LastToolName = command.type;
                }

                Debug.LogError($"Error executing command '{command?.type ?? "Unknown"}': {ex.Message}\n{ex.StackTrace}");
                return McpJson.SerializeObject(new
                {
                    status = "error",
                    error = ex.Message,
                    command = command?.type ?? "Unknown",
                    projectContext = ProjectContextGuard.BuildProjectContext()
                });
            }
        }

        /// <summary>
        /// Handle MCP tool approval requests from Codex.
        /// UniBridge currently runs as a local MCP-only bridge, so approval is
        /// handled by the connection trust dialog and local tool policy.
        /// </summary>
        Task<string> HandleMcpToolApprovalAsync(Command command)
        {
            var toolName = command.@params?.Value<string>("toolName");

            McpLog.Log($"[MCP Approval] Local MCP request auto-approved: {toolName}");
            return Task.FromResult(McpJson.SerializeObject(new
            {
                status = "success",
                result = new { approved = true, reason = "Local UniBridge MCP connection approved" }
            }));
        }

        void OnBeforeAssemblyReload()
        {
            // Stop cleanly before reload
            try { Stop(); } catch { }
            // Avoid file I/O or heavy work here
        }

        void OnAfterAssemblyReload()
        {
            LogBreadcrumb("Idle");
            // Schedule a safe restart after reload to avoid races during compilation
            ScheduleInitRetry();
        }

        void OnToolsChanged(McpToolRegistry.ToolChangeEventArgs args)
        {
            // Notify connected clients about tool changes
            // This allows MCP clients to refresh their tool list without reconnecting
            if (isRunning && args != null)
            {
                var changeType = args.ChangeType.ToString().ToLowerInvariant();
                var message = args.ChangeType == McpToolRegistry.ToolChangeType.Refreshed
                    ? "Tools were refreshed"
                    : $"Tool '{args.ToolName}' was {changeType}";
                McpLog.Log($"[UniBridgeMcpBridge] {message}");
            }
            // Invalidate cached tools hash/snapshot so it will be recomputed on next request
            s_CurrentToolsHash = null;
            s_ToolsSnapshot = null;
        }

        public void InvalidateToolsCache()
        {
            s_CurrentToolsHash = null;
            s_ToolsSnapshot = null;
        }

        /// <summary>
        /// Wait for an existing in-flight task and complete the TCS with its result.
        /// Used for command deduplication when a duplicate requestId is detected.
        /// </summary>
        async Task WaitForExistingAndComplete(Task<string> existingTask, TaskCompletionSource<string> tcs, string requestId)
        {
            try
            {
                string result = await existingTask;
                tcs.TrySetResult(InjectRequestId(result, requestId));
            }
            catch (Exception ex)
            {
                McpLog.LogDelayed($"Error waiting for existing command {requestId}: {ex.Message}");
                tcs.TrySetResult(InjectRequestId(McpJson.SerializeObject(new
                {
                    status = "error",
                    error = $"Deduplication error: {ex.Message}"
                }), requestId));
            }
        }

        async Task CompleteQueuedCommandAsync(Task<string> task, TaskCompletionSource<string> completion, string requestId, string commandId)
        {
            try { await WaitForExistingAndComplete(task, completion, requestId); }
            finally { commandWaiters.TryRemove(commandId, out _); }
        }

        void ComputeToolsSnapshotAndHash()
        {
            s_ToolsSnapshot = McpToolRegistry.GetAvailableTools();
            var tools = s_ToolsSnapshot ?? Array.Empty<McpToolInfo>();
            var minimal = new object[tools.Length];
            for (int i = 0; i < tools.Length; i++)
            {
                minimal[i] = new { tools[i].name, tools[i].description, tools[i].inputSchema };
            }
            var json = McpJson.SerializeObject(minimal, Formatting.None);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
            s_CurrentToolsHash = Convert.ToBase64String(hashBytes);
        }

        void LogConnectionDecision(ValidationDecision decision)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Connection Info ===");

            // Server info
            sb.AppendLine("MCP Server:");
            var server = decision.Connection?.Server;
            if (server != null)
            {
                sb.AppendLine($"  PID: {server.ProcessId}");
                sb.AppendLine($"  Name: {server.ProcessName ?? "unknown"}");
                sb.AppendLine($"  Executable: {server.Identity?.Path ?? "unknown"}");
                if (server.Identity != null)
                {
                    sb.AppendLine($"  Hash: {server.Identity.SHA256Hash?.Substring(0, 16) ?? "unknown"}...");
                    sb.AppendLine($"  Signed: {(server.Identity.IsSigned ? "Yes" : "No")}");
                    if (server.Identity.IsSigned)
                    {
                        sb.AppendLine($"  Publisher: {server.Identity.SignaturePublisher ?? "unknown"}");
                        sb.AppendLine($"  Signature Valid: {(server.Identity.SignatureValid ? "Yes" : "No")}");
                    }
                }
            }
            else
            {
                sb.AppendLine("  Unable to determine server info");
            }

            // Validation status
            sb.AppendLine($"  Validation: {decision.Status}");
            sb.AppendLine($"  Reason: {decision.Reason}");

            // Client info
            sb.AppendLine("MCP Client:");
            var client = decision.Connection?.Client;
            if (client != null)
            {
                sb.AppendLine($"  Name: {client.ProcessName ?? "unknown"}");
                sb.AppendLine($"  PID: {client.ProcessId}");
                sb.AppendLine($"  Executable: {client.Identity?.Path ?? "unknown"}");
                if (decision.Connection.ClientChainDepth > 0)
                {
                    sb.AppendLine($"  Chain depth: {decision.Connection.ClientChainDepth} (walked up {decision.Connection.ClientChainDepth} level{(decision.Connection.ClientChainDepth == 1 ? "" : "s")})");
                }
            }
            else
            {
                sb.AppendLine("  Unable to determine (parent may have exited or permissions denied)");
            }

            sb.Append("======================");

            // Use LogDelayed since this is called from HandleClientAsync background thread
            McpLog.LogDelayed(sb.ToString());
        }

        static bool IsCompiling()
        {
            if (EditorApplication.isCompiling) return true;
            try
            {
                Type pipeline = Type.GetType("UnityEditor.Compilation.CompilationPipeline, UnityEditor");
                var prop = pipeline?.GetProperty("isCompiling", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop != null) return (bool)prop.GetValue(null);
            }
            catch { }
            return false;
        }

        static void LogBreadcrumb(string stage) => McpLog.Log($"[{stage}]");

        /// <summary>
        /// Get information about currently connected clients
        /// </summary>
        public ClientInfo[] GetConnectedClients()
        {
            return TransportStore.GetActiveTransportStates()
                .Select(ts => ts.ClientInfo)
                .Where(ci => ci != null)
                .ToArray();
        }

        /// <summary>
        /// Get the count of currently connected clients
        /// </summary>
        public int GetConnectedClientCount()
        {
            return TransportStore.CountConnections();
        }

        /// <summary>
        /// Build a local MCP denial message from a failed census reservation.
        /// </summary>
        static string BuildCapacityDenialReason(ReservationResult reservation)
        {
            var cap = reservation.PoolCap < 0 ? "unlimited" : reservation.PoolCap.ToString();
            return $"Local MCP connection capacity reached ({reservation.PoolCount}/{cap}).";
        }
    }
}
