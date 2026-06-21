using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    public enum ToolExecutionPolicy
    {
        Auto,
        Observer,
        ReadOnly,
        Mutating,
        Capture,
        CompileReload
    }

    /// <summary>
    /// Coordinates MCP tool execution so read-only work can overlap while scene, asset,
    /// capture, and lifecycle operations run with editor-wide exclusivity.
    /// </summary>
    internal static class ToolExecutionScheduler
    {
        const int DefaultTimeoutMs = 120000;
        const int MaxTimeoutMs = 600000;
        const int MaxRecentOperations = 80;

        static readonly SemaphoreSlim QueueLock = new(1, 1);
        static readonly SemaphoreSlim ResourceLock = new(1, 1);
        static readonly SemaphoreSlim ReaderLock = new(1, 1);
        static readonly AsyncLocal<CancellationToken> AmbientCancellation = new();
        static readonly AsyncLocal<int> ReadDepth = new();
        static readonly AsyncLocal<int> WriteDepth = new();
        static readonly object StatusLock = new();
        static readonly Dictionary<long, OperationRecord> ActiveOperations = new();
        static readonly Queue<OperationRecord> RecentOperations = new();

        static int ActiveReaders;
        static int PendingReaders;
        static int PendingExclusive;
        static long NextLeaseId;
        static long NextOperationId;
        static long ActiveExclusiveLeaseId;
        static string ActiveExclusiveTool;
        static string ActiveExclusivePolicy;
        static DateTime ActiveExclusiveStartedUtc;
        static long TotalStarted;
        static long TotalCompleted;
        static long TotalFaulted;
        static long TotalTimedOut;
        static long TotalCanceled;
        static long TotalReaped;

        static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
        {
            "UniBridge_AssetIntelligence",
            "UniBridge_BehaviourContext",
            "UniBridge_ContextSnapshot",
            "UniBridge_Discover",
            "UniBridge_DomainCatalog",
            "UniBridge_FindInFile",
            "UniBridge_GetSha",
            "UniBridge_ListResources",
            "UniBridge_ReadResource",
            "UniBridge_RuntimeProfiler",
            "UniBridge_RuntimeStateProbe",
            "UniBridge_SceneObjectView",
            "UniBridge_ScriptIntelligence",
            "UniBridge_ToolGuide",
            "UniBridge_TypeSchema",
            "UniBridge_UnitySearch",
            "UniBridge_ValidateAdditiveSceneRegistration",
            "UniBridge_ValidateScript"
        };

        static readonly HashSet<string> CaptureTools = new(StringComparer.Ordinal)
        {
            "UniBridge_CaptureAsset",
            "UniBridge_CaptureUIToolkit",
            "UniBridge_CaptureView",
            "UniBridge_VisualSceneAudit"
        };

        static readonly HashSet<string> AlwaysMutatingTools = new(StringComparer.Ordinal)
        {
            "UniBridge_ApplyTextEdits",
            "UniBridge_CreateScript",
            "UniBridge_DeleteScript",
            "UniBridge_ImportExternalModel",
            "UniBridge_ManageAnimationClip",
            "UniBridge_ManageAnimatorController",
            "UniBridge_ManageAsset",
            "UniBridge_ManageAssetImporter",
            "UniBridge_ManageAudio",
            "UniBridge_ManageConstraints",
            "UniBridge_ManageGameObject",
            "UniBridge_ManageInputActions",
            "UniBridge_ManageMaterial",
            "UniBridge_ManageMenuItem",
            "UniBridge_ManageNavigation",
            "UniBridge_ManagePhysics2D",
            "UniBridge_ManagePhysics3D",
            "UniBridge_ManagePrefab",
            "UniBridge_ManageRendering",
            "UniBridge_ManageScript",
            "UniBridge_ManageScriptableObject",
            "UniBridge_ManageShader",
            "UniBridge_ManageTilemap2D",
            "UniBridge_ManageTimeline",
            "UniBridge_ManageUI",
            "UniBridge_ManageUIToolkit",
            "UniBridge_ManageUnityEvent",
            "UniBridge_ManageVFX",
            "UniBridge_ScriptApplyEdits",
            "UniBridge_ScopedEdit"
        };

        public static async Task<object> ExecuteAsync(
            string toolName,
            JObject parameters,
            IToolHandler handler,
            Func<Task<object>> execute,
            CancellationToken cancellationToken = default)
        {
            if (execute == null)
                throw new ArgumentNullException(nameof(execute));

            var policy = ResolvePolicy(toolName, parameters, handler);
            var timeoutMs = ReadTimeout(parameters);
            var operation = BeginOperation(toolName, policy, timeoutMs, parameters);
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operation.CancellationSource = operationCancellation;
            Exception capturedError = null;

            try
            {
                SweepExpiredReadOperations("before operation start");

                if (policy == ToolExecutionPolicy.Observer)
                {
                    MarkOperationStarted(operation);
                    return await ExecuteBodyAsync(operation, policy, timeoutMs, execute, operationCancellation);
                }

                if (policy == ToolExecutionPolicy.ReadOnly && (WriteDepth.Value > 0 || ReadDepth.Value > 0))
                {
                    MarkOperationStarted(operation);
                    ReadDepth.Value++;
                    try
                    {
                        return await ExecuteBodyAsync(operation, policy, timeoutMs, execute, operationCancellation);
                    }
                    finally
                    {
                        ReadDepth.Value--;
                    }
                }

                if (policy != ToolExecutionPolicy.ReadOnly)
                {
                    if (WriteDepth.Value > 0)
                    {
                        MarkOperationStarted(operation);
                        WriteDepth.Value++;
                        try
                        {
                            return await ExecuteBodyAsync(operation, policy, timeoutMs, execute, operationCancellation);
                        }
                        finally
                        {
                            WriteDepth.Value--;
                        }
                    }

                    if (ReadDepth.Value > 0)
                    {
                        throw new InvalidOperationException($"Tool '{toolName}' requested an exclusive {policy} execution while already inside a read-only UniBridge execution context.");
                    }
                }

                IDisposable lease = null;
                IncrementPending(policy);
                try
                {
                    lease = await AcquireAsync(toolName, policy, timeoutMs);
                }
                finally
                {
                    DecrementPending(policy);
                }

                using (lease)
                {
                    AttachLease(operation, lease);
                    MarkOperationStarted(operation);
                    IncrementDepth(policy);
                    try
                    {
                        return await ExecuteBodyAsync(operation, policy, timeoutMs, execute, operationCancellation);
                    }
                    finally
                    {
                        DecrementDepth(policy);
                    }
                }
            }
            catch (Exception ex)
            {
                capturedError = ex;
                throw;
            }
            finally
            {
                CompleteOperation(operation, capturedError);
            }
        }

        public static CancellationToken CurrentCancellationToken => AmbientCancellation.Value;

        public static void ThrowIfCancellationRequested()
        {
            AmbientCancellation.Value.ThrowIfCancellationRequested();
        }

        public static async Task YieldIfNotCancelledAsync()
        {
            var token = AmbientCancellation.Value;
            token.ThrowIfCancellationRequested();
            if (token.CanBeCanceled)
                await Cidonix.UniBridge.Toolkit.EditorTask.Delay(1, token);
            else
                await Cidonix.UniBridge.Toolkit.EditorTask.Yield();
            token.ThrowIfCancellationRequested();
        }

        static async Task<object> ExecuteBodyAsync(
            OperationRecord operation,
            ToolExecutionPolicy policy,
            int timeoutMs,
            Func<Task<object>> execute,
            CancellationTokenSource operationCancellation)
        {
            var previousToken = AmbientCancellation.Value;
            AmbientCancellation.Value = operationCancellation.Token;
            try
            {
                operationCancellation.Token.ThrowIfCancellationRequested();
                if (policy == ToolExecutionPolicy.ReadOnly)
                    return await AwaitReadOnlyBodyAsync(operation, timeoutMs, execute, operationCancellation);

                return await execute();
            }
            finally
            {
                AmbientCancellation.Value = previousToken;
            }
        }

        static async Task<object> AwaitReadOnlyBodyAsync(
            OperationRecord operation,
            int timeoutMs,
            Func<Task<object>> execute,
            CancellationTokenSource operationCancellation)
        {
            var bodyTask = execute();
            if (bodyTask.IsCompleted)
            {
                var completedResult = await bodyTask;
                operationCancellation.Token.ThrowIfCancellationRequested();
                return completedResult;
            }

            var cancellationTask = WaitForCancellationAsync(operationCancellation.Token);
            var timeoutTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(bodyTask, cancellationTask, timeoutTask);
            if (ReferenceEquals(completed, bodyTask))
            {
                var completedResult = await bodyTask;
                operationCancellation.Token.ThrowIfCancellationRequested();
                return completedResult;
            }

            if (ReferenceEquals(completed, timeoutTask))
            {
                SafeCancel(operationCancellation);
                ObserveLateCompletion(bodyTask, operation, "timed out");
                throw new TimeoutException($"Timed out after {timeoutMs}ms running UniBridge read-only tool '{operation.Tool}'. The read slot was released and the operation was marked timed out.");
            }

            ObserveLateCompletion(bodyTask, operation, "canceled");
            throw new OperationCanceledException($"UniBridge read-only tool '{operation.Tool}' was canceled because the caller disconnected or canceled the request.", operationCancellation.Token);
        }

        static async Task WaitForCancellationAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
                await Task.Delay(50);
            token.ThrowIfCancellationRequested();
        }

        static void ObserveLateCompletion(Task task, OperationRecord operation, string reason)
        {
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var message = t.Exception?.GetBaseException().Message ?? "Unknown error";
                    McpLog.Log($"[ToolExecutionScheduler] Late read-only operation {operation.OperationId} for {operation.Tool} faulted after being {reason}: {message}");
                }
                else
                {
                    McpLog.Log($"[ToolExecutionScheduler] Late read-only operation {operation.OperationId} for {operation.Tool} completed after being {reason}.");
                }
            }, TaskScheduler.Default);
        }

        static void SafeCancel(CancellationTokenSource source)
        {
            try { source.Cancel(); } catch { }
        }

        public static ToolExecutionPolicy ResolvePolicy(string toolName, JObject parameters, IToolHandler handler = null)
        {
            if (handler?.Attribute != null && handler.Attribute.ExecutionPolicy != ToolExecutionPolicy.Auto)
                return handler.Attribute.ExecutionPolicy;

            toolName ??= string.Empty;

            if (CaptureTools.Contains(toolName))
                return ToolExecutionPolicy.Capture;

            if (string.Equals(toolName, "UniBridge_WaitForEvent", StringComparison.Ordinal))
                return ToolExecutionPolicy.Observer;

            if (string.Equals(toolName, "UniBridge_BatchActions", StringComparison.Ordinal))
                return IsDryRun(parameters) ? ToolExecutionPolicy.ReadOnly : ToolExecutionPolicy.Mutating;

            if (string.Equals(toolName, "UniBridge_WorkflowRecipes", StringComparison.Ordinal))
                return ResolveWorkflowPolicy(parameters);

            if (string.Equals(toolName, "UniBridge_WorkSession", StringComparison.Ordinal))
                return IsAction(parameters, "Status", "Review", "Diff", "List")
                    ? ToolExecutionPolicy.ReadOnly
                    : ToolExecutionPolicy.Mutating;

            if (string.Equals(toolName, "UniBridge_EditorEvents", StringComparison.Ordinal))
                return IsAction(parameters, "Clear") ? ToolExecutionPolicy.Mutating : ToolExecutionPolicy.ReadOnly;

            if (string.Equals(toolName, "UniBridge_ReadConsole", StringComparison.Ordinal))
                return IsAction(parameters, "Clear", "MarkSession") ? ToolExecutionPolicy.Mutating : ToolExecutionPolicy.ReadOnly;

            if (string.Equals(toolName, "UniBridge_ManageEditor", StringComparison.Ordinal))
                return ResolveManageEditorPolicy(parameters);

            if (string.Equals(toolName, "UniBridge_ManageScene", StringComparison.Ordinal))
                return IsAction(parameters, "GetHierarchy", "GetActive", "GetBuildSettings")
                    ? ToolExecutionPolicy.ReadOnly
                    : ToolExecutionPolicy.Mutating;

            if (string.Equals(toolName, "UniBridge_EditorSnapshot", StringComparison.Ordinal))
                return IsAction(parameters, "List", "Inspect")
                    ? ToolExecutionPolicy.ReadOnly
                    : ToolExecutionPolicy.Mutating;

            if (string.Equals(toolName, "UniBridge_VersionControl", StringComparison.Ordinal))
                return IsAction(parameters, "GetStatus", "InspectAsset", "InspectAssets")
                    ? ToolExecutionPolicy.ReadOnly
                    : ToolExecutionPolicy.Mutating;

            if (ReadOnlyTools.Contains(toolName))
                return ToolExecutionPolicy.ReadOnly;

            if (AlwaysMutatingTools.Contains(toolName))
                return ToolExecutionPolicy.Mutating;

            return ToolExecutionPolicy.Mutating;
        }

        public static object BuildAnnotation(string toolName, IToolHandler handler)
        {
            var policy = ResolvePolicy(toolName, null, handler);
            return new
            {
                policy = policy.ToString(),
                readOnly = policy == ToolExecutionPolicy.ReadOnly || policy == ToolExecutionPolicy.Observer,
                exclusive = policy != ToolExecutionPolicy.ReadOnly && policy != ToolExecutionPolicy.Observer,
                timeoutParameters = new[] { "ExecutionTimeoutMs", "SchedulerTimeoutMs" },
                note = policy == ToolExecutionPolicy.Observer
                    ? "Observes editor state/events without holding the UniBridge execution gate."
                    : policy == ToolExecutionPolicy.ReadOnly
                        ? "May run concurrently with other read-only UniBridge tools."
                        : "Runs through UniBridge's exclusive editor execution gate."
            };
        }

        public static object Snapshot(int recentLimit = 20)
        {
            SweepExpiredReadOperations("snapshot");
            var now = DateTime.UtcNow;
            recentLimit = Math.Max(0, Math.Min(MaxRecentOperations, recentLimit));
            lock (StatusLock)
            {
                return new
                {
                    activeReaders = ActiveReaders,
                    pending = new
                    {
                        readers = PendingReaders,
                        exclusive = PendingExclusive
                    },
                    readDepth = ReadDepth.Value,
                    writeDepth = WriteDepth.Value,
                    activeExclusive = ActiveExclusiveLeaseId == 0 ? null : new
                    {
                        leaseId = ActiveExclusiveLeaseId,
                        tool = ActiveExclusiveTool,
                        policy = ActiveExclusivePolicy,
                        startedUtc = ActiveExclusiveStartedUtc.ToString("o"),
                        elapsedMs = ElapsedMs(ActiveExclusiveStartedUtc, now)
                    },
                    activeOperations = ActiveOperations.Values
                        .OrderBy(record => record.StartedUtc ?? record.QueuedUtc)
                        .Select(record => ToOperationSnapshot(record, now))
                        .ToArray(),
                    recentOperations = RecentOperations
                        .Reverse()
                        .Take(recentLimit)
                        .Select(record => ToOperationSnapshot(record, now))
                        .ToArray(),
                    totals = new
                    {
                        started = TotalStarted,
                        completed = TotalCompleted,
                        faulted = TotalFaulted,
                        timedOut = TotalTimedOut,
                        canceled = TotalCanceled,
                        reaped = TotalReaped
                    },
                    limits = new
                    {
                        defaultTimeoutMs = DefaultTimeoutMs,
                        maxTimeoutMs = MaxTimeoutMs,
                        maxRecentOperations = MaxRecentOperations
                    }
                };
            }
        }

        public static object Recent(int limit = 20)
        {
            SweepExpiredReadOperations("recent");
            var now = DateTime.UtcNow;
            limit = Math.Max(0, Math.Min(MaxRecentOperations, limit));
            lock (StatusLock)
            {
                return new
                {
                    count = Math.Min(limit, RecentOperations.Count),
                    maxRecentOperations = MaxRecentOperations,
                    operations = RecentOperations
                        .Reverse()
                        .Take(limit)
                        .Select(record => ToOperationSnapshot(record, now))
                        .ToArray()
                };
            }
        }

        public static object ReapStale(int recentLimit = 20, int graceMs = 1000, bool forceReadOnly = true)
        {
            var reaped = SweepExpiredReadOperations("manual reap", graceMs, forceReadOnly);
            return new
            {
                reaped = reaped.Select(record => ToOperationSnapshot(record, DateTime.UtcNow)).ToArray(),
                scheduler = Snapshot(recentLimit)
            };
        }

        static OperationRecord[] SweepExpiredReadOperations(string reason, int graceMs = 1000, bool forceReadOnly = true)
        {
            var now = DateTime.UtcNow;
            var candidates = new List<OperationRecord>();
            lock (StatusLock)
            {
                foreach (var record in ActiveOperations.Values)
                {
                    if (!record.StartedUtc.HasValue ||
                        record.FinishedUtc.HasValue ||
                        record.Policy != ToolExecutionPolicy.ReadOnly)
                    {
                        continue;
                    }

                    var timeoutMs = Math.Max(1000, record.TimeoutMs) + Math.Max(0, graceMs);
                    if (ElapsedMs(record.StartedUtc.Value, now) >= timeoutMs)
                        candidates.Add(record);
                }
            }

            foreach (var record in candidates)
            {
                SafeCancel(record.CancellationSource);
                if (forceReadOnly)
                    ForceReleaseLease(record, $"stale read-only operation exceeded {record.TimeoutMs}ms during {reason}");

                CompleteOperation(
                    record,
                    new TimeoutException($"Stale read-only operation exceeded {record.TimeoutMs}ms and was reaped during {reason}."),
                    "timedOut",
                    forcedReleaseReason: record.ForceReleaseReason);
            }

            return candidates.ToArray();
        }

        static void AttachLease(OperationRecord operation, IDisposable lease)
        {
            if (lease is Lease typedLease)
            {
                lock (StatusLock)
                {
                    operation.Lease = typedLease;
                }
            }
        }

        static void ForceReleaseLease(OperationRecord record, string reason)
        {
            Lease lease;
            lock (StatusLock)
            {
                lease = record.Lease;
            }

            if (lease == null || !lease.ForceRelease())
                return;

            lock (StatusLock)
            {
                record.ForceReleasedUtc = DateTime.UtcNow;
                record.ForceReleaseReason = reason;
                TotalReaped++;
            }

            McpLog.Log($"[ToolExecutionScheduler] Force-released read-only lease for operation {record.OperationId} ({record.Tool}): {reason}");
        }

        static async Task<IDisposable> AcquireAsync(string toolName, ToolExecutionPolicy policy, int timeoutMs)
        {
            SweepExpiredReadOperations($"before acquiring {policy} slot for {toolName}");
            return policy == ToolExecutionPolicy.ReadOnly
                ? await AcquireReadAsync(toolName, timeoutMs)
                : await AcquireWriteAsync(toolName, policy, timeoutMs);
        }

        static async Task<IDisposable> AcquireReadAsync(string toolName, int timeoutMs)
        {
            await WaitOrThrow(QueueLock, timeoutMs, toolName, ToolExecutionPolicy.ReadOnly);
            try
            {
                await WaitOrThrow(ReaderLock, timeoutMs, toolName, ToolExecutionPolicy.ReadOnly);
                try
                {
                    ActiveReaders++;
                    if (ActiveReaders == 1)
                    {
                        try
                        {
                            await WaitOrThrow(ResourceLock, timeoutMs, toolName, ToolExecutionPolicy.ReadOnly);
                        }
                        catch
                        {
                            ActiveReaders = Math.Max(0, ActiveReaders - 1);
                            throw;
                        }
                    }

                    return new Lease(ReleaseRead);
                }
                finally
                {
                    ReaderLock.Release();
                }
            }
            finally
            {
                QueueLock.Release();
            }
        }

        static async Task<IDisposable> AcquireWriteAsync(string toolName, ToolExecutionPolicy policy, int timeoutMs)
        {
            var queueHeld = false;
            try
            {
                await WaitOrThrow(QueueLock, timeoutMs, toolName, policy);
                queueHeld = true;
                await WaitOrThrow(ResourceLock, timeoutMs, toolName, policy);
                QueueLock.Release();
                queueHeld = false;
            }
            catch
            {
                if (queueHeld)
                    QueueLock.Release();
                throw;
            }

            var leaseId = Interlocked.Increment(ref NextLeaseId);
            lock (StatusLock)
            {
                ActiveExclusiveLeaseId = leaseId;
                ActiveExclusiveTool = toolName;
                ActiveExclusivePolicy = policy.ToString();
                ActiveExclusiveStartedUtc = DateTime.UtcNow;
            }

            McpLog.Log($"[ToolExecutionScheduler] Acquired {policy} lease {leaseId} for {toolName}");
            return new Lease(() =>
            {
                lock (StatusLock)
                {
                    if (ActiveExclusiveLeaseId == leaseId)
                    {
                        ActiveExclusiveLeaseId = 0;
                        ActiveExclusiveTool = null;
                        ActiveExclusivePolicy = null;
                        ActiveExclusiveStartedUtc = default;
                    }
                }
                ResourceLock.Release();
                McpLog.Log($"[ToolExecutionScheduler] Released {policy} lease {leaseId} for {toolName}");
            });
        }

        static void IncrementDepth(ToolExecutionPolicy policy)
        {
            if (policy == ToolExecutionPolicy.ReadOnly)
                ReadDepth.Value++;
            else
                WriteDepth.Value++;
        }

        static void IncrementPending(ToolExecutionPolicy policy)
        {
            if (policy == ToolExecutionPolicy.ReadOnly)
                Interlocked.Increment(ref PendingReaders);
            else
                Interlocked.Increment(ref PendingExclusive);
        }

        static void DecrementPending(ToolExecutionPolicy policy)
        {
            if (policy == ToolExecutionPolicy.ReadOnly)
                Interlocked.Decrement(ref PendingReaders);
            else
                Interlocked.Decrement(ref PendingExclusive);
        }

        static void DecrementDepth(ToolExecutionPolicy policy)
        {
            if (policy == ToolExecutionPolicy.ReadOnly)
                ReadDepth.Value--;
            else
                WriteDepth.Value--;
        }

        static void ReleaseRead()
        {
            ReaderLock.Wait();
            try
            {
                ActiveReaders = Math.Max(0, ActiveReaders - 1);
                if (ActiveReaders == 0)
                    ResourceLock.Release();
            }
            finally
            {
                ReaderLock.Release();
            }
        }

        static async Task WaitOrThrow(SemaphoreSlim semaphore, int timeoutMs, string toolName, ToolExecutionPolicy policy)
        {
            if (await semaphore.WaitAsync(timeoutMs))
                return;

            throw new TimeoutException($"Timed out after {timeoutMs}ms waiting for UniBridge {policy} execution slot before running '{toolName}'.");
        }

        static ToolExecutionPolicy ResolveWorkflowPolicy(JObject parameters)
        {
            if (IsAction(parameters, "List", "Describe", "BuildBatch", "DryRun"))
                return ToolExecutionPolicy.ReadOnly;
            if (IsAction(parameters, "Execute") && IsDryRun(parameters))
                return ToolExecutionPolicy.ReadOnly;
            return ToolExecutionPolicy.Mutating;
        }

        static ToolExecutionPolicy ResolveManageEditorPolicy(JObject parameters)
        {
            if (IsAction(parameters,
                    "GetState",
                    "GetPlayModeState",
                    "GetCompilationDiagnostics",
                    "GetProjectRoot",
                    "GetWindows",
                    "GetActiveTool",
                    "GetSelection",
                    "GetPrefabStage",
                    "GetTags",
                    "GetLayers",
                    "WaitForReady",
                    "WaitForReadyAfterReload",
                    "WaitForPlayMode",
                    "WaitForEditMode",
                    "WaitIdle"))
            {
                return ToolExecutionPolicy.ReadOnly;
            }

            if (IsAction(parameters,
                    "RefreshAssets",
                    "RequestPlayModeNoWait",
                    "RequestScriptCompilation",
                    "RequestScriptCompilationNoWait",
                    "SaveAll",
                    "SaveAssets",
                    "GenerateSolutionFiles",
                    "GenerateSolutionFile",
                    "ReloadCheckpoint"))
            {
                return ToolExecutionPolicy.CompileReload;
            }

            return ToolExecutionPolicy.Mutating;
        }

        static int ReadTimeout(JObject parameters)
        {
            var token = parameters?["ExecutionTimeoutMs"]
                        ?? parameters?["executionTimeoutMs"]
                        ?? parameters?["execution_timeout_ms"]
                        ?? parameters?["SchedulerTimeoutMs"]
                        ?? parameters?["schedulerTimeoutMs"]
                        ?? parameters?["scheduler_timeout_ms"]
                        ?? parameters?["TimeoutMs"]
                        ?? parameters?["timeoutMs"]
                        ?? parameters?["timeout_ms"];
            if (token != null && token.Type != JTokenType.Null && int.TryParse(token.ToString(), out var timeout))
                return Math.Max(1000, Math.Min(MaxTimeoutMs, timeout));
            return DefaultTimeoutMs;
        }

        static bool IsDryRun(JObject parameters)
        {
            var token = parameters?["DryRun"] ?? parameters?["dryRun"] ?? parameters?["dry_run"];
            if (token == null || token.Type == JTokenType.Null)
                return false;
            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>();
            return bool.TryParse(token.ToString(), out var value) && value;
        }

        static bool IsDryRunExecution(string toolName, JObject parameters)
        {
            var token = parameters?["DryRun"] ?? parameters?["dryRun"] ?? parameters?["dry_run"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type == JTokenType.Boolean)
                    return token.Value<bool>();
                return bool.TryParse(token.ToString(), out var value) && value;
            }

            return string.Equals(toolName, "UniBridge_ManageSceneHierarchy", StringComparison.Ordinal);
        }

        static bool IsAction(JObject parameters, params string[] actions)
        {
            var action = ReadAction(parameters);
            return actions.Any(candidate => string.Equals(action, candidate, StringComparison.OrdinalIgnoreCase));
        }

        static string ReadAction(JObject parameters)
        {
            var token = parameters?["Action"] ?? parameters?["action"] ?? parameters?["operation"] ?? parameters?["Operation"];
            return token?.ToString() ?? string.Empty;
        }

        static OperationRecord BeginOperation(string toolName, ToolExecutionPolicy policy, int timeoutMs, JObject parameters)
        {
            var mode = ResolveMode(toolName, parameters, policy);
            return new OperationRecord
            {
                OperationId = Interlocked.Increment(ref NextOperationId),
                Tool = toolName ?? string.Empty,
                Policy = policy,
                Mode = mode,
                ChangedProject = ChangedProjectFor(policy, mode),
                TimeoutMs = timeoutMs,
                QueuedUtc = DateTime.UtcNow,
                Outcome = "queued"
            };
        }

        static string ResolveMode(string toolName, JObject parameters, ToolExecutionPolicy policy)
        {
            if (IsDryRunExecution(toolName, parameters))
                return "DryRun";
            if (policy == ToolExecutionPolicy.Observer)
                return "Observer";
            if (policy == ToolExecutionPolicy.ReadOnly)
                return "ReadOnly";
            if (policy == ToolExecutionPolicy.Capture)
                return "Capture";
            if (policy == ToolExecutionPolicy.CompileReload)
                return "CompileReload";
            return "Execute";
        }

        static bool ChangedProjectFor(ToolExecutionPolicy policy, string mode)
        {
            if (string.Equals(mode, "DryRun", StringComparison.Ordinal) ||
                policy == ToolExecutionPolicy.Observer ||
                policy == ToolExecutionPolicy.ReadOnly ||
                policy == ToolExecutionPolicy.Capture)
            {
                return false;
            }

            return true;
        }

        static void MarkOperationStarted(OperationRecord record)
        {
            lock (StatusLock)
            {
                if (record.StartedUtc.HasValue)
                    return;

                record.StartedUtc = DateTime.UtcNow;
                record.Outcome = "running";
                ActiveOperations[record.OperationId] = record;
                TotalStarted++;
            }
        }

        static void CompleteOperation(OperationRecord record, Exception error, string outcomeOverride = null, string forcedReleaseReason = null)
        {
            lock (StatusLock)
            {
                if (record.FinishedUtc.HasValue)
                    return;

                var cancellationRequested = record.CancellationSource?.IsCancellationRequested == true;
                record.FinishedUtc = DateTime.UtcNow;
                record.Outcome = outcomeOverride ?? (error == null ? "success" :
                    error is OperationCanceledException ? "canceled" :
                    error is TimeoutException ? "timedOut" :
                    "failed");
                if (error == null && cancellationRequested && record.Outcome == "success")
                    record.Outcome = "canceled";
                if (error != null)
                {
                    record.ErrorType = error.GetType().FullName;
                    record.ErrorMessage = error.Message;
                }
                if (!string.IsNullOrWhiteSpace(forcedReleaseReason))
                    record.ForceReleaseReason = forcedReleaseReason;
                record.CancellationRequested = cancellationRequested;

                ActiveOperations.Remove(record.OperationId);
                RecentOperations.Enqueue(record);
                while (RecentOperations.Count > MaxRecentOperations)
                    RecentOperations.Dequeue();

                if (record.StartedUtc.HasValue)
                    TotalCompleted++;
                if (error != null)
                {
                    TotalFaulted++;
                    if (error is TimeoutException)
                        TotalTimedOut++;
                    if (error is OperationCanceledException)
                        TotalCanceled++;
                }
                else if (record.CancellationRequested)
                {
                    TotalCanceled++;
                }
            }
        }

        static object ToOperationSnapshot(OperationRecord record, DateTime now)
        {
            var started = record.StartedUtc;
            var finished = record.FinishedUtc;
            var effectiveFinished = finished ?? now;
            return new
            {
                operationId = record.OperationId,
                tool = record.Tool,
                policy = record.Policy.ToString(),
                mode = record.Mode,
                changedProject = record.ChangedProject,
                queuedUtc = record.QueuedUtc.ToString("o"),
                startedUtc = started?.ToString("o"),
                finishedUtc = finished?.ToString("o"),
                waitMs = ElapsedMs(record.QueuedUtc, started ?? effectiveFinished),
                durationMs = started.HasValue ? ElapsedMs(started.Value, effectiveFinished) : (long?)null,
                timeoutMs = record.TimeoutMs,
                outcome = record.Outcome,
                cancellationRequested = IsCancellationRequested(record),
                forcedRelease = record.ForceReleasedUtc.HasValue ? new
                {
                    releasedUtc = record.ForceReleasedUtc?.ToString("o"),
                    reason = record.ForceReleaseReason
                } : null,
                error = string.IsNullOrWhiteSpace(record.ErrorType) ? null : new
                {
                    type = record.ErrorType,
                    message = record.ErrorMessage
                }
            };
        }

        static long ElapsedMs(DateTime startUtc, DateTime endUtc)
        {
            return Math.Max(0, (long)(endUtc - startUtc).TotalMilliseconds);
        }

        static bool IsCancellationRequested(OperationRecord record)
        {
            if (record.CancellationRequested)
                return true;
            try
            {
                return record.CancellationSource?.IsCancellationRequested == true;
            }
            catch
            {
                return record.CancellationRequested;
            }
        }

        sealed class Lease : IDisposable
        {
            Action dispose;
            int disposed;

            public Lease(Action dispose)
            {
                this.dispose = dispose;
            }

            public void Dispose()
            {
                ForceRelease();
            }

            public bool ForceRelease()
            {
                var action = dispose;
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return false;
                dispose = null;
                action?.Invoke();
                return true;
            }
        }

        sealed class OperationRecord
        {
            public long OperationId;
            public string Tool;
            public ToolExecutionPolicy Policy;
            public string Mode;
            public bool ChangedProject;
            public DateTime QueuedUtc;
            public DateTime? StartedUtc;
            public DateTime? FinishedUtc;
            public int TimeoutMs;
            public string Outcome;
            public string ErrorType;
            public string ErrorMessage;
            public CancellationTokenSource CancellationSource;
            public bool CancellationRequested;
            public Lease Lease;
            public DateTime? ForceReleasedUtc;
            public string ForceReleaseReason;
        }
    }
}
