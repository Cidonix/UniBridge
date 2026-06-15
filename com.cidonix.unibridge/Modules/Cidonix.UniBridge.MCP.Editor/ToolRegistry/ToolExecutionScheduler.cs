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
            Func<Task<object>> execute)
        {
            if (execute == null)
                throw new ArgumentNullException(nameof(execute));

            var policy = ResolvePolicy(toolName, parameters, handler);
            var timeoutMs = ReadTimeout(parameters);
            var operation = BeginOperation(toolName, policy, timeoutMs, parameters);
            Exception capturedError = null;

            try
            {
                if (policy == ToolExecutionPolicy.Observer)
                {
                    MarkOperationStarted(operation);
                    return await execute();
                }

                if (policy == ToolExecutionPolicy.ReadOnly && (WriteDepth.Value > 0 || ReadDepth.Value > 0))
                {
                    MarkOperationStarted(operation);
                    ReadDepth.Value++;
                    try
                    {
                        return await execute();
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
                            return await execute();
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
                    MarkOperationStarted(operation);
                    IncrementDepth(policy);
                    try
                    {
                        return await execute();
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
                        timedOut = TotalTimedOut
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

        static async Task<IDisposable> AcquireAsync(string toolName, ToolExecutionPolicy policy, int timeoutMs)
        {
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
                        ?? parameters?["scheduler_timeout_ms"];
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

        static void CompleteOperation(OperationRecord record, Exception error)
        {
            lock (StatusLock)
            {
                if (record.FinishedUtc.HasValue)
                    return;

                record.FinishedUtc = DateTime.UtcNow;
                record.Outcome = error == null ? "success" : "failed";
                if (error != null)
                {
                    record.ErrorType = error.GetType().FullName;
                    record.ErrorMessage = error.Message;
                }

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

        sealed class Lease : IDisposable
        {
            Action dispose;

            public Lease(Action dispose)
            {
                this.dispose = dispose;
            }

            public void Dispose()
            {
                var action = dispose;
                dispose = null;
                action?.Invoke();
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
        }
    }
}
