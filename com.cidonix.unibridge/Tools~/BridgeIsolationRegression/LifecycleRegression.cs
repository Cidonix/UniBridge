using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.BridgeIsolationRegression;

static class LifecycleRegression
{
    static void Reset()
    {
        LifeFault.Reset(); EditorApplication.Reset(); EditorTask.Reset(); AssemblyReloadEvents.Reset();
        McpToolRegistry.Reset(); ConnectionFactory.Instances.Clear();
        CommandRecoveryJournal.Instances.Clear(); ServerDiscovery.Exists = false; TransportStore.Items.Clear(); SessionState.Data.Clear();
        Bridge.ResetStatic();
    }
    static void Await(Func<bool> predicate, string message)
    {
        Check.Assert(SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(3)), message);
    }
    static void Clean(Bridge bridge)
    {
        var state = JObject.FromObject(bridge.Inspect(), JsonSerializer.Create());
        Check.Assert(!bridge.Running && bridge.Listener == null && bridge.Generation == null, "Stopped readiness/resource publication retained");
        Check.Assert(!state.Value<bool>("ctsPresent") && !state.Value<bool>("listenerTaskPresent") && !state.Value<bool>("discovery"), "Stopped cancellation/task/discovery retained");
        Check.Assert(bridge.QueueCount == 0 && bridge.WaiterCount == 0 && EditorApplication.UpdateSubscriptions == 0 && EditorTask.DelaySubscriptions == 0, "Stopped queue/waiters/start hooks retained");
    }
    static void NoHooks()
    {
        Check.Assert(EditorApplication.UpdateSubscriptions == 0 && EditorApplication.PlayModeSubscriptions == 0 &&
            EditorApplication.QuittingSubscriptions == 0 && EditorTask.DelaySubscriptions == 0 &&
            AssemblyReloadEvents.BeforeSubscriptions == 0 && AssemblyReloadEvents.AfterSubscriptions == 0 && McpToolRegistry.ToolSubscriptions == 0,
            "Disposed bridge retained static event/subscription ownership");
    }
    static void SchedulerStopInterleaving(string callbackName, bool dispose)
    {
        Reset(); var bridge = new Bridge(callbackName != "ScheduleInitRetry");
        EditorApplication.timeSinceStartup = 2;
        Action callback = callbackName == "ScheduleInitRetry" ? EditorApplication.RaisePlay :
            callbackName == "EnsureStartedOnEditorIdle" ? EditorApplication.CaptureUpdates() : EditorTask.CaptureDelayed();
        var pauseStage = callbackName == "ScheduleInitRetry" ? "update-subscribe" : "compilation-check";
        using var reached = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var stopping = new ManualResetEventSlim();
        var pausedOnce = 0;
        LifeFault.Callback = stage =>
        {
            if (stage != pauseStage || Interlocked.Exchange(ref pausedOnce, 1) != 0) return;
            reached.Set(); Check.Assert(release.Wait(5000), "Owned scheduler pause was not released");
        };
        var callbackTask = Task.Run(callback);
        Task stopTask = null;
        bool serialized = false;
        try
        {
            Check.Assert(reached.Wait(3000), "Production callback did not reach controlled acquisition boundary: " + callbackName);
            stopTask = Task.Run(() => { stopping.Set(); if (dispose) bridge.Dispose(); else bridge.Stop(); });
            Check.Assert(stopping.Wait(3000), "Concurrent shutdown task did not begin");
            serialized = !stopTask.Wait(150);
        }
        finally
        {
            release.Set(); callbackTask.GetAwaiter().GetResult(); stopTask?.GetAwaiter().GetResult(); LifeFault.Callback = null;
        }
        try
        {
            Check.Assert(serialized, "Shutdown passed a callback's unchecked in-progress acquisition boundary");
            Clean(bridge);
            Check.Assert(ConnectionFactory.Instances.All(item => item.Disposed && !item.Started), "Callback recreated resources after completed shutdown");
            if (dispose) NoHooks();
        }
        finally { bridge.Dispose(); }
    }
    public static void Run(List<CaseResult> cases)
    {
        foreach (var callback in new[] { "ScheduleInitRetry", "EnsureStartedOnEditorIdle", "InitializeAfterCompilation" })
        {
            foreach (var dispose in new[] { false, true })
            {
                Check.Run(cases, "Bridge actual scheduler " + callback + " serializes acquisition against " + (dispose ? "Dispose" : "Stop"), () => SchedulerStopInterleaving(callback, dispose));
            }
        }
        foreach (var stage in new[] { "validation", "listener-create", "connection-path", "tools", "journal", "listener-start", "update-subscribe", "discovery", "discovery-false" })
        {
            Check.Run(cases, "Bridge failed Start acquisition rollback: " + stage, () =>
            {
                Reset(); using var bridge = new Bridge(false); LifeFault.Stage = stage;
                bridge.Start(); LifeFault.Stage = null;
                Clean(bridge);
                Check.Assert(ConnectionFactory.Instances.All(item => !item.Started && item.Disposed), "Acquired listener not independently stopped/disposed");
                Check.Assert(ConnectionFactory.Instances.All(item => item.AcceptCalls == 0), "Failed startup exposed listener worker before commit");
                Check.Assert(CommandRecoveryJournal.Instances.All(item => item.Deactivated), "Failed-start recovery journal remained active");
                bridge.Start(); Check.Assert(bridge.Running, "Retry after failed acquisition did not become ready"); bridge.Stop(); Clean(bridge);
            });
        }
        foreach (var stage in new[] { "validation", "before-reload-subscribe", "after-reload-subscribe", "quitting-subscribe", "play-subscribe", "update-subscribe", "delay-subscribe" })
        {
            Check.Run(cases, "Bridge failed constructor releases acquired hooks: " + stage, () =>
            {
                Reset(); LifeFault.Stage = stage; var threw = false;
                try { _ = new Bridge(); } catch (InvalidOperationException) { threw = true; }
                LifeFault.Stage = null;
                Check.Assert(threw, "Expected constructor acquisition fault did not occur"); NoHooks();
            });
        }
        Check.Run(cases, "Bridge Start publishes readiness only after discovery and worker gate commit", () =>
        {
            Reset(); using var bridge = new Bridge(false);
            var witnessed = false;
            LifeFault.Callback = stage => { if (stage == "discovery") { witnessed = true; Check.Assert(!bridge.Running && bridge.Listener == null && bridge.Generation == null && ConnectionFactory.Instances.All(item => item.AcceptCalls == 0), "Uncommitted resources became observable"); } };
            bridge.Start(); LifeFault.Callback = null;
            Check.Assert(witnessed && bridge.Running, "No readiness commit witness");
            Await(() => ConnectionFactory.Instances[0].AcceptCalls == 1, "Successful worker did not enter accept");
            bridge.Start(); Check.Assert(ConnectionFactory.Instances.Count == 1 && EditorApplication.UpdateSubscriptions == 1, "Idempotent Start duplicated listener/update ownership");
            bridge.Stop(); Clean(bridge);
        });
        Check.Run(cases, "Bridge Stop drains pending commands even when readiness was false", () =>
        {
            Reset(); using var bridge = new Bridge(false); var pending = bridge.AddPending(); bridge.Stop();
            Check.Assert(pending.Task.IsCompletedSuccessfully && JObject.Parse(pending.Task.Result).Value<string>("error") == "Bridge stopped", "False readiness stranded a pending result"); Clean(bridge);
        });
        foreach (var stage in new[] { "journal-deactivate", "discovery-delete", "transport-clear", "census-clear" })
        {
            Check.Run(cases, "Bridge independent Stop cleanup survives dependency fault: " + stage, () =>
            {
                Reset(); using var bridge = new Bridge(false); bridge.Start();
                var listener = (FakeListener)bridge.Listener; var client = new FakeTransport { ThrowClose = true, ThrowDispose = true }; bridge.AddTrackedTransport(client);
                var pending = bridge.AddPending(); LifeFault.Stage = stage; bridge.Stop(); LifeFault.Stage = null;
                Clean(bridge); Check.Assert(listener.Disposed && !listener.Started && client.CloseCalls == 1 && client.DisposeCalls == 1 && pending.Task.IsCompletedSuccessfully, "Cleanup fault suppressed later ownership cleanup or waiter drain");
            });
        }
        Check.Run(cases, "Bridge independent listener Stop and Dispose failures cannot strand clients or waiters", () =>
        {
            Reset(); using var bridge = new Bridge(false); bridge.Start(); var listener = (FakeListener)bridge.Listener;
            listener.ThrowStop = listener.ThrowDispose = true; var client = new FakeTransport { ThrowClose = true }; bridge.AddTrackedTransport(client); var pending = bridge.AddPending();
            bridge.Stop(); Clean(bridge);
            Check.Assert(listener.Stops == 1 && listener.DisposeCalls == 1 && client.CloseCalls == 1 && client.DisposeCalls == 1 && pending.Task.IsCompletedSuccessfully, "Independent cleanup operation was skipped");
            bridge.Stop(); Check.Assert(listener.DisposeCalls == 1 && client.DisposeCalls == 1, "Repeated Stop reowned closed resources");
        });
        Check.Run(cases, "Bridge Stop never uses global JSON factory for cancellation envelopes", () =>
        {
            Reset(); using var bridge = new Bridge(false); bridge.Start(); var pending = bridge.AddPending(); var prior = JsonConvert.DefaultSettings; var calls = 0;
            try { JsonConvert.DefaultSettings = () => { calls++; throw new InvalidOperationException("Owned stop poison"); }; bridge.Stop(); }
            finally { JsonConvert.DefaultSettings = prior; }
            Check.Assert(calls == 0 && pending.Task.IsCompletedSuccessfully, "Stop was interrupted by global JSON settings"); Clean(bridge);
        });
        Check.Run(cases, "Bridge Stop cancels queued retry and Dispose removes all static ownership", () =>
        {
            Reset(); var bridge = new Bridge(); Check.Assert(EditorTask.DelaySubscriptions == 1 && EditorApplication.UpdateSubscriptions == 1, "Auto startup fixture not scheduled");
            bridge.Stop(); Clean(bridge); bridge.Dispose(); NoHooks(); bridge.Dispose(); NoHooks();
        });
        Check.Run(cases, "Disposed Bridge ignores already captured delayed callback, play mode and reload callbacks", () =>
        {
            Reset(); var bridge = new Bridge(); var oldDelay = EditorTask.CaptureDelayed(); var oldUpdate = EditorApplication.CaptureUpdates(); bridge.Dispose();
            oldDelay?.Invoke(); oldUpdate?.Invoke(); EditorApplication.RaisePlay(); AssemblyReloadEvents.RaiseBefore(); AssemblyReloadEvents.RaiseAfter(); bridge.Start();
            Check.Assert(ConnectionFactory.Instances.Count == 0 && !bridge.Running, "Disposed object restarted from stale callback"); NoHooks();
        });
        Check.Run(cases, "Bridge Stop prevents an already captured startup callback from restarting", () =>
        {
            Reset(); using var bridge = new Bridge(); var oldDelay = EditorTask.CaptureDelayed(); var oldUpdate = EditorApplication.CaptureUpdates(); bridge.Stop();
            oldDelay?.Invoke(); oldUpdate?.Invoke();
            Check.Assert(ConnectionFactory.Instances.Count == 0 && !bridge.Running, "Cancelled startup callback restarted stopped bridge"); Clean(bridge);
        });
        Check.Run(cases, "Bridge compiling retry stays bounded and starts once compilation ends", () =>
        {
            Reset(); using var bridge = new Bridge(); EditorApplication.isCompiling = true; EditorTask.RaiseDelayed();
            Check.Assert(ConnectionFactory.Instances.Count == 0 && EditorTask.DelaySubscriptions == 1 && EditorApplication.UpdateSubscriptions == 1, "Compilation deferral lost/duplicated retry hooks");
            EditorApplication.isCompiling = false; EditorApplication.timeSinceStartup = 2; EditorTask.RaiseDelayed();
            Check.Assert(bridge.Running && ConnectionFactory.Instances.Count == 1, "Compile completion did not start exactly once"); bridge.Stop(); Clean(bridge);
        });
        Check.Run(cases, "Bridge reload stop and deferred after-reload startup use a new generation", () =>
        {
            Reset(); using var bridge = new Bridge(false); bridge.Start(); var first = bridge.Generation; AssemblyReloadEvents.RaiseBefore(); Clean(bridge);
            AssemblyReloadEvents.RaiseAfter(); Check.Assert(!bridge.Running && EditorTask.DelaySubscriptions == 1, "Reload started before idle deferral"); EditorTask.RaiseDelayed();
            Check.Assert(bridge.Running && !ReferenceEquals(first, bridge.Generation) && ConnectionFactory.Instances.Count == 2, "Reload reused stopped generation"); bridge.Stop(); Clean(bridge);
        });
        Check.Run(cases, "Bridge old generation cannot queue command after Stop and successful restart", () =>
        {
            Reset(); using var bridge = new Bridge(false); bridge.Start(); var old = bridge.Generation; var oldToken = bridge.Cancellation; bridge.Stop(); bridge.Start();
            var rejected = new TaskCompletionSource<string>(); Check.Assert(!bridge.Queue(old, "late-old", rejected, new FakeTransport(), oldToken), "Old generation admitted work");
            Check.Assert(rejected.Task.IsCompletedSuccessfully && JObject.Parse(rejected.Task.Result).Value<string>("error") == "Bridge stopped" && bridge.QueueCount == 0 && bridge.WaiterCount == 0, "Rejected old command left pending ownership");
            var mutated = false; var cancelled = false; try { bridge.OwnedGenerationCallback(old, oldToken, () => mutated = true); } catch (OperationCanceledException) { cancelled = true; }
            Check.Assert(cancelled && !mutated, "Stale generation callback mutated replacement state"); bridge.Stop(); Clean(bridge);
        });
        Check.Run(cases, "Bridge current generation rejects cancelled and disconnected admissions", () =>
        {
            Reset(); using var bridge = new Bridge(false); bridge.Start();
            var cancelled = new TaskCompletionSource<string>(); var disconnected = new TaskCompletionSource<string>();
            Check.Assert(!bridge.Queue(bridge.Generation, "cancelled", cancelled, new FakeTransport(), new CancellationToken(true)), "Cancelled command admitted");
            Check.Assert(!bridge.Queue(bridge.Generation, "closed", disconnected, new FakeTransport { Closed = true }, default), "Disconnected command admitted");
            Check.Assert(cancelled.Task.IsCompletedSuccessfully && disconnected.Task.IsCompletedSuccessfully && bridge.QueueCount == 0, "Refused admission stranded waiter"); bridge.Stop(); Clean(bridge);
        });
        Check.Run(cases, "Bridge admitted asynchronous command waiter drains on Stop after queue removal", () =>
        {
            Reset(); using var bridge = new Bridge(false); bridge.Start(); var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); Bridge.DispatchResult = result.Task;
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Check.Assert(bridge.Queue(bridge.Generation, "inflight", pending, new FakeTransport(), bridge.Cancellation), "Valid command not admitted"); bridge.Process();
            Check.Assert(bridge.QueueCount == 0 && bridge.WaiterCount == 1 && !pending.Task.IsCompleted, "Actual ProcessCommands did not retain async waiter ownership");
            bridge.Stop(); Check.Assert(pending.Task.IsCompletedSuccessfully && pending.Task.Result.Contains("Bridge stopped"), "Stop ignored removed-but-in-flight command waiter");
            result.SetResult("{\"status\":\"success\"}"); Await(() => bridge.WaiterCount == 0, "Late completion retained command waiter");
            Check.Assert(pending.Task.Result.Contains("Bridge stopped"), "Late success replaced stopped response"); Clean(bridge);
        });
        Check.Run(cases, "Bridge parallel admission versus Stop cannot strand any of 128 results", () =>
        {
            Reset(); using var bridge = new Bridge(false); bridge.Start(); var generation = bridge.Generation; var token = bridge.Cancellation;
            var pending = Enumerable.Range(0, 128).Select(_ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            using var gate = new ManualResetEventSlim();
            var admissions = pending.Select((completion, i) => Task.Run(() => { gate.Wait(); bridge.Queue(generation, "parallel-" + i, completion, new FakeTransport(), token); })).ToArray();
            var stop = Task.Run(() => { gate.Wait(); bridge.Stop(); }); gate.Set(); Check.Assert(Task.WaitAll(admissions.Concat(new[] { stop }).ToArray(), 5000), "Admission/Stop interleaving did not finish");
            Check.Assert(pending.All(item => item.Task.IsCompletedSuccessfully && item.Task.Result.Contains("Bridge stopped")), "Admission raced after Stop drain leaving pending completion"); Clean(bridge);
        });
        Check.Run(cases, "Bridge late accept from old listener closes and disposes before new generation handoff", () =>
        {
            Reset(); using var bridge = new Bridge(false); LifeFault.Callback = stage => { if (stage == "listener-start") ConnectionFactory.Instances.Last().IgnoreCancellation = true; }; bridge.Start(); LifeFault.Callback = null;
            var oldListener = (FakeListener)bridge.Listener; var oldWorker = bridge.WorkerTask;
            Check.Assert(oldListener.AcceptStarted.Wait(3000), "Old accept not waiting"); bridge.Stop(); bridge.Start(); var replacement = (FakeListener)bridge.Listener;
            var client = new FakeTransport { ThrowClose = true }; var attempts = 0; Bridge.OnConnectionAttempt += _ => Interlocked.Increment(ref attempts);
            oldListener.PendingAccept.SetResult(client); Check.Assert(oldWorker.Wait(3000), "Old listener worker did not exit after late accept");
            Check.Assert(client.CloseCalls == 1 && client.DisposeCalls == 1 && attempts == 0 && Bridge.HandOffs == 0 && bridge.Running, "Late accept reached replacement generation or missed cleanup");
            Check.Assert(oldListener.AcceptCalls == 1 && replacement.AcceptCalls <= 1, "Old worker accepted using replacement listener"); bridge.Stop(); Clean(bridge);
        });
        foreach (var callbackFault in new[] { false, true })
        {
            Check.Run(cases, "Bridge accepted transport cleanup survives " + (callbackFault ? "connection callback" : "peer cache") + " failure", () =>
            {
                Reset(); using var bridge = new Bridge(false); bridge.Start(); var listener = (FakeListener)bridge.Listener; Check.Assert(listener.AcceptStarted.Wait(3000), "Accept not waiting");
                if (callbackFault) Bridge.OnConnectionAttempt += _ => throw new InvalidOperationException("Owned event failure");
                var client = new FakeTransport { ThrowCache = !callbackFault, ThrowClose = true };
                listener.PendingAccept.SetResult(client); Await(() => client.DisposeCalls == 1, "Accepted failed transport not disposed");
                Check.Assert(client.CloseCalls == 1 && Bridge.HandOffs == 0, "Failure handed off transport or skipped Close"); bridge.Stop(); Clean(bridge);
            });
        }
        Check.Run(cases, "Actual WriteWithLockAsync Dispose cancels waiting writer and defers semaphore disposal until active Release", () =>
        {
            Reset(); var bridge = new Bridge(false); var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = new FakeTransport { WriteResult = finish.Task }; var waiting = new FakeTransport();
            var first = bridge.Write(active, "{\"status\":\"success\"}"); Check.Assert(active.WriteEntered.Wait(3000), "Active write not entered");
            var second = bridge.Write(waiting, "{\"status\":\"success\"}"); Check.Assert(bridge.WriteCount == 2 && waiting.WriteCalls == 0, "Waiting write not admitted behind active owner");
            bridge.Dispose(); var cancelled = false; try { second.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check.Assert(cancelled && active.LastWriteToken.IsCancellationRequested && !bridge.WriteResourcesDisposed && bridge.WriteCount == 1 && waiting.WriteCalls == 0, "Dispose failed to cancel waiting writer or prematurely disposed active resources");
            finish.SetResult(true); first.GetAwaiter().GetResult();
            Check.Assert(first.IsCompletedSuccessfully && bridge.WriteCount == 0 && bridge.WriteResourcesDisposed, "Active Release faulted or deferred resources remained"); NoHooks();
        });
        Check.Run(cases, "Actual WriteWithLockAsync keeps original transport I/O error across concurrent Dispose", () =>
        {
            Reset(); var bridge = new Bridge(false); var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new FakeTransport { WriteResult = finish.Task }; var write = bridge.Write(transport, "{\"status\":\"error\"}");
            Check.Assert(transport.WriteEntered.Wait(3000), "Write not entered"); bridge.Dispose(); finish.SetException(new System.IO.IOException("owned original write failure"));
            Exception observed = null; try { write.GetAwaiter().GetResult(); } catch (Exception ex) { observed = ex; }
            Check.Assert(observed is System.IO.IOException && observed.Message == "owned original write failure" && bridge.WriteCount == 0 && bridge.WriteResourcesDisposed, "Cleanup masked original error with disposed semaphore or leaked ownership"); NoHooks();
        });
        Check.Run(cases, "Actual WriteWithLockAsync post-Dispose admission is cancelled before transport access", () =>
        {
            Reset(); var bridge = new Bridge(false); bridge.Dispose(); var transport = new FakeTransport(); var cancelled = false;
            try { bridge.Write(transport, "owned").GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check.Assert(cancelled && transport.WriteCalls == 0 && bridge.WriteCount == 0 && bridge.WriteResourcesDisposed, "Disposed writer accessed dead resources or transport"); NoHooks();
        });
        Check.Run(cases, "Actual WriteWithLockAsync queued caller cancellation preserves active writer semaphore ownership", () =>
        {
            Reset(); using var bridge = new Bridge(false); var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = new FakeTransport { WriteResult = finish.Task }; var waiting = new FakeTransport(); var first = bridge.Write(active, "{\"owned\":true}");
            using var caller = new CancellationTokenSource(); var second = bridge.Write(waiting, "{\"other\":true}", caller.Token); caller.Cancel();
            var cancelled = false; try { second.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check.Assert(cancelled && bridge.WriteCount == 1 && !bridge.WriteResourcesDisposed && waiting.WriteCalls == 0, "Queued cancellation released/disposed another writer's semaphore"); finish.SetResult(true); first.GetAwaiter().GetResult();
            Check.Assert(bridge.WriteCount == 0 && !bridge.WriteResourcesDisposed && System.Text.Encoding.UTF8.GetString(active.LastWriteBytes) == "{\"owned\":true}\n", "Real MessageProtocol output framing changed");
        });
        Check.Run(cases, "Actual transport linked-cancellation admission stays generation-owned across Stop/restart", () =>
        {
            Reset(); using var bridge = new Bridge(false); bridge.Start(); var generation = bridge.Generation; using var linked = bridge.LinkedTransport(generation);
            Check.Assert(!linked.IsCancellationRequested, "Current generation linked token born cancelled"); bridge.Stop(); Check.Assert(linked.IsCancellationRequested, "Stop did not cancel current transport child token"); bridge.Start();
            var cancelled = false; try { using var stale = bridge.LinkedTransport(generation); } catch (OperationCanceledException) { cancelled = true; }
            Check.Assert(cancelled && bridge.Running, "Stale transport registered against disposed source or damaged replacement generation"); bridge.Stop(); Clean(bridge);
        });
        Check.Run(cases, "Actual transport linked-token admission interleaved with Stop cannot touch disposed source", () =>
        {
            Reset(); using var bridge = new Bridge(false); bridge.Start(); var generation = bridge.Generation;
            var outcomes = new System.Collections.Concurrent.ConcurrentBag<Exception>(); using var gate = new ManualResetEventSlim();
            var admissions = Enumerable.Range(0, 64).Select(_ => Task.Run(() => { gate.Wait(); try { using var child = bridge.LinkedTransport(generation); } catch (OperationCanceledException) { } catch (Exception ex) { outcomes.Add(ex); } })).ToArray();
            var stop = Task.Run(() => { gate.Wait(); bridge.Stop(); }); gate.Set(); Check.Assert(Task.WaitAll(admissions.Concat(new[] { stop }).ToArray(), 5000), "Linked-source/Stop interleaving did not finish");
            Check.Assert(outcomes.IsEmpty, "Generation check-to-link race reached disposed source: " + string.Join(",", outcomes.Select(ex => ex.GetType().Name))); Clean(bridge);
        });
        Reset();
    }
}
