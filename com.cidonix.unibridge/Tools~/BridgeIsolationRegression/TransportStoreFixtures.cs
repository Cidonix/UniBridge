using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor;
using Cidonix.UniBridge.MCP.Editor.Connection;
using RealStore = Cidonix.UniBridge.MCP.Editor.TransportStore;

// Only the types of these optional metadata fields are needed by the unchanged store.
// These stubs do not stand in for its dictionary, lock, identity or mutation logic.
namespace Cidonix.UniBridge.MCP.Editor.Models { sealed class ClientInfo { } }
namespace Cidonix.UniBridge.MCP.Editor.Security { sealed class ValidationDecision { } }

namespace Cidonix.UniBridge.BridgeIsolationRegression
{
    static class StoreFixtures
    {
        public static object IdentityLock => RequiredField("IdentityLock");
        static ConcurrentDictionary<IConnectionTransport, TransportState> States =>
            (ConcurrentDictionary<IConnectionTransport, TransportState>)RequiredField("States");
        static ConcurrentDictionary<string, ConcurrentDictionary<IConnectionTransport, byte>> Index =>
            (ConcurrentDictionary<string, ConcurrentDictionary<IConnectionTransport, byte>>)RequiredField("IdentityToTransports");

        static object RequiredField(string name)
        {
            var field = typeof(RealStore).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            return field?.GetValue(null) ?? throw new InvalidOperationException("Unchanged production store fixture field unavailable: " + name);
        }

        public static void AssertConsistent()
        {
            // Call only after workers have joined or while holding the actual store lock.
            // The expected invariant is independent of any replacement mutation algorithm.
            var states = States.ToArray();
            var indexed = Index.ToArray();
            Check.Assert(indexed.All(entry => !entry.Value.IsEmpty), "Store retained an empty identity index.");
            Check.Assert(states.Length == indexed.Sum(entry => entry.Value.Count), "State and identity membership counts diverged.");
            foreach (var entry in states)
            {
                Check.Assert(ReferenceEquals(entry.Key, entry.Value.Transport), "Transport state references another physical transport.");
                Check.Assert(Index.TryGetValue(entry.Value.IdentityKey, out var set) && set.ContainsKey(entry.Key), "Registered physical transport lost its identity index.");
                Check.Assert(RealStore.GetAllTransportsByIdentity(entry.Value.IdentityKey).Contains(entry.Key), "Public identity lookup missed a registered transport.");
            }
            foreach (var entry in indexed)
                foreach (var transport in entry.Value.Keys)
                    Check.Assert(States.TryGetValue(transport, out var state) && state.IdentityKey == entry.Key, "Identity index retained a removed or displaced physical transport.");
            Check.Assert(RealStore.CountConnections() == states.Length, "Public connection count differs from physical state count.");
        }

        public sealed class Worker : IDisposable
        {
            readonly Action action;
            readonly Thread thread;
            readonly ManualResetEventSlim started = new(false);
            readonly ManualResetEventSlim completed = new(false);
            Exception failure;
            public bool Completed => completed.IsSet;
            public Worker(Action action)
            {
                this.action = action;
                thread = new Thread(Execute) { IsBackground = true, Name = "Transport store controlled regression" };
            }
            void Execute()
            {
                started.Set();
                try { action(); }
                catch (Exception ex) { failure = ex; }
                finally { completed.Set(); }
            }
            public void Start()
            {
                thread.Start();
                Check.Assert(started.Wait(TimeSpan.FromSeconds(3)), "Controlled store worker did not start.");
            }
            public bool WaitUntilBlockedOrCompleted()
            {
                Check.Assert(SpinWait.SpinUntil(() => completed.IsSet ||
                    (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3)),
                    "Store worker neither completed nor reached the held monitor.");
                return !completed.IsSet && (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
            }
            public void Join()
            {
                Check.Assert(thread.Join(TimeSpan.FromSeconds(3)), "Store worker did not finish after monitor release.");
                if (failure != null) throw new InvalidOperationException("Controlled store worker failed.", failure);
            }
            public void Dispose()
            {
                // Test scopes release the monitor before disposal, even on assertion failure.
                if (thread.IsAlive) thread.Join(TimeSpan.FromSeconds(3));
                if (!thread.IsAlive) { started.Dispose(); completed.Dispose(); }
            }
        }
    }

    sealed class StoreTransport : IConnectionTransport
    {
        public string ConnectionId { get; }
        public bool IsConnected { get; private set; } = true;
        public int CloseCalls, DisposeCalls;
        public event Action OnDisconnected;
        public StoreTransport(string connectionId) { ConnectionId = connectionId; }
        public void Close() { CloseCalls++; IsConnected = false; OnDisconnected?.Invoke(); }
        public void Dispose() { DisposeCalls++; IsConnected = false; }
        public int? GetClientProcessId() => null;
        public void CacheClientProcessId() { }
        public Task WriteAsync(byte[] bytes, CancellationToken token) => throw new NotSupportedException("No native or protocol writes in store qualification.");
        public Task<byte[]> ReadUntilDelimiterAsync(byte delimiter, int maxBytes, int timeoutMs, CancellationToken token) => throw new NotSupportedException("No native or protocol reads in store qualification.");
    }
}
