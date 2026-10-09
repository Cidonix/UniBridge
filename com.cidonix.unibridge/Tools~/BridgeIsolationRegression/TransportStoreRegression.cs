using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RealStore = Cidonix.UniBridge.MCP.Editor.TransportStore;

namespace Cidonix.UniBridge.BridgeIsolationRegression
{
    public static class StoreRegression
    {
        public static void Run(List<CaseResult> cases)
        {
            Case(cases, "Store actual production register remap remove preserve physical identity", () =>
            {
                var first = new StoreTransport("NamedPipe-1");
                var second = new StoreTransport("NamedPipe-2");
                var firstState = RealStore.Register(first, "pending-NamedPipe-1");
                RealStore.Register(second, "pending-NamedPipe-2");
                RealStore.UpdateIdentityKey(first, "validated-shared");
                RealStore.UpdateIdentityKey(second, "validated-shared");
                StoreFixtures.AssertConsistent();
                Check.Assert(RealStore.GetAllTransportsByIdentity("validated-shared").Count == 2, "Two physical transports collapsed into one state.");
                Check.Assert(ReferenceEquals(RealStore.Remove(first), firstState) && RealStore.GetState(first) == null, "Removal returned or retained the wrong physical state.");
                Check.Assert(RealStore.GetAllTransportsByIdentity("validated-shared").Single() == second, "Removing one transport removed its independent companion.");
                StoreFixtures.AssertConsistent();
            });
            Case(cases, "Store re-register same transport removes its previous index", () =>
            {
                var transport = new StoreTransport("NamedPipe-1");
                var previous = RealStore.Register(transport, "pending-old");
                var replacement = RealStore.Register(transport, "pending-new");
                Check.Assert(!ReferenceEquals(previous, replacement) && ReferenceEquals(RealStore.GetState(transport), replacement), "Re-registration did not replace its physical state.");
                Check.Assert(RealStore.GetAllTransportsByIdentity("pending-old").Count == 0 && RealStore.CountConnections() == 1, "Old index retained a re-registered transport.");
                StoreFixtures.AssertConsistent();
            });
            Case(cases, "Store invalid registration fails before any partial state mutation", () =>
            {
                var transport = new StoreTransport("NamedPipe-1");
                ExpectArgument(() => RealStore.Register(transport, null));
                ExpectArgument(() => RealStore.Register(null, "pending-owned"));
                Check.Assert(RealStore.GetState(transport) == null && RealStore.CountConnections() == 0, "Invalid registration published partial state.");
                StoreFixtures.AssertConsistent();
            });
            Case(cases, "Store missing transport remap is no-op and existing null remap preserves membership", () =>
            {
                var transport = new StoreTransport("NamedPipe-1");
                RealStore.UpdateIdentityKey(transport, null);
                RealStore.Register(transport, "pending-owned");
                ExpectArgument(() => RealStore.UpdateIdentityKey(transport, null));
                Check.Assert(RealStore.GetState(transport).IdentityKey == "pending-owned", "Invalid remap damaged existing membership.");
                StoreFixtures.AssertConsistent();
            });
            Case(cases, "Store Clear returns all owned transports once without closing authored resources", () =>
            {
                var transports = new[] { new StoreTransport("NamedPipe-1"), new StoreTransport("NamedPipe-2") };
                foreach (var transport in transports) RealStore.Register(transport, "pending-" + transport.ConnectionId);
                var cleared = RealStore.Clear();
                Check.Assert(cleared.Length == 2 && transports.All(transport => cleared.Contains(transport)), "Clear lost a physical transport needed by its caller for cleanup.");
                Check.Assert(transports.All(transport => transport.CloseCalls == 0 && transport.DisposeCalls == 0), "Store assumed transport resource ownership instead of returning it.");
                Check.Assert(RealStore.Clear().Length == 0 && transports.All(transport => RealStore.GetState(transport) == null), "Repeated Clear retained old state.");
                StoreFixtures.AssertConsistent();
            });
            Case(cases, "Store Register cannot mutate state while another mutator owns IdentityLock", () =>
            {
                var transport = new StoreTransport("NamedPipe-1");
                using var worker = new StoreFixtures.Worker(() => RealStore.Register(transport, "pending-NamedPipe-1"));
                lock (StoreFixtures.IdentityLock)
                {
                    worker.Start();
                    Check.Assert(worker.WaitUntilBlockedOrCompleted(), "Register bypassed the production identity transaction monitor.");
                    Check.Assert(RealStore.GetState(transport) == null, "Register wrote physical state before acquiring the identity monitor.");
                }
                worker.Join();
                Check.Assert(RealStore.GetState(transport) != null, "Register did not complete after monitor release.");
                StoreFixtures.AssertConsistent();
            });
            Case(cases, "Store Remove cannot erase physical state before acquiring IdentityLock", () =>
            {
                var transport = new StoreTransport("NamedPipe-1");
                var state = RealStore.Register(transport, "pending-NamedPipe-1");
                using var worker = new StoreFixtures.Worker(() => RealStore.Remove(transport));
                lock (StoreFixtures.IdentityLock)
                {
                    worker.Start();
                    Check.Assert(worker.WaitUntilBlockedOrCompleted(), "Remove bypassed the production identity transaction monitor.");
                    Check.Assert(ReferenceEquals(RealStore.GetState(transport), state), "Remove erased physical state before acquiring the identity monitor.");
                }
                worker.Join();
                Check.Assert(RealStore.GetState(transport) == null, "Remove did not complete after monitor release.");
                StoreFixtures.AssertConsistent();
            });
            Case(cases, "Store Clear cannot erase maps while another mutator owns IdentityLock", () =>
            {
                var transport = new StoreTransport("NamedPipe-1");
                RealStore.Register(transport, "pending-NamedPipe-1");
                using var worker = new StoreFixtures.Worker(() => RealStore.Clear());
                lock (StoreFixtures.IdentityLock)
                {
                    worker.Start();
                    Check.Assert(worker.WaitUntilBlockedOrCompleted(), "Clear bypassed the production identity transaction monitor.");
                    Check.Assert(RealStore.GetState(transport) != null && RealStore.CountConnections() == 1, "Clear changed maps before acquiring the identity monitor.");
                }
                worker.Join();
                Check.Assert(RealStore.GetState(transport) == null, "Clear did not complete after monitor release.");
                StoreFixtures.AssertConsistent();
            });
            Case(cases, "Store blocked UpdateIdentityKey cannot resurrect a transport removed before admission", () =>
            {
                var transport = new StoreTransport("NamedPipe-1");
                RealStore.Register(transport, "pending-NamedPipe-1");
                using var worker = new StoreFixtures.Worker(() => RealStore.UpdateIdentityKey(transport, "validated-late"));
                lock (StoreFixtures.IdentityLock)
                {
                    worker.Start();
                    Check.Assert(worker.WaitUntilBlockedOrCompleted(), "Update did not reach the controlled monitor boundary.");
                    RealStore.Remove(transport); // Reentrant same-thread cleanup while the updater is blocked.
                    Check.Assert(RealStore.GetState(transport) == null, "Controlled removal did not occur.");
                }
                worker.Join();
                Check.Assert(RealStore.GetAllTransportsByIdentity("validated-late").Count == 0 && RealStore.CountConnections() == 0, "Updater resurrected a removed transport from a stale pre-lock lookup.");
                StoreFixtures.AssertConsistent();
            });
            Case(cases, "Store old pending cleanup before restart cannot remove reused pending identity", () =>
            {
                var oldTransport = new StoreTransport("NamedPipe-1");
                var replacement = new StoreTransport("NamedPipe-1");
                RealStore.Register(oldTransport, "pending-NamedPipe-1");
                using var worker = new StoreFixtures.Worker(() => RealStore.Remove(oldTransport));
                lock (StoreFixtures.IdentityLock)
                {
                    worker.Start();
                    Check.Assert(worker.WaitUntilBlockedOrCompleted(), "Old cleanup did not reach the held identity transaction boundary.");
                    RealStore.Clear();
                    RealStore.Register(replacement, "pending-NamedPipe-1");
                }
                worker.Join();
                Check.Assert(RealStore.GetState(oldTransport) == null && RealStore.GetAllTransportsByIdentity("pending-NamedPipe-1").Single() == replacement, "Old pending cleanup damaged the replacement pending index.");
                StoreFixtures.AssertConsistent();
            });
            Case(cases, "Store 128 parallel register remap cleanup operations preserve physical indexes", () =>
            {
                var transports = Enumerable.Range(0, 128).Select(i => new StoreTransport("NamedPipe-" + i)).ToArray();
                using var start = new ManualResetEventSlim();
                var tasks = transports.Select((transport, i) => Task.Run(() =>
                {
                    start.Wait();
                    RealStore.Register(transport, "pending-" + transport.ConnectionId);
                    RealStore.UpdateIdentityKey(transport, "validated-" + i % 4);
                    if (i % 2 == 0) RealStore.Remove(transport);
                })).ToArray();
                start.Set();
                Check.Assert(Task.WaitAll(tasks, TimeSpan.FromSeconds(5)), "Parallel actual store operations did not finish.");
                Check.Assert(RealStore.CountConnections() == 64 && transports.Where((transport, i) => i % 2 != 0).All(transport => RealStore.GetState(transport) != null), "Parallel cleanup lost independently retained physical state.");
                StoreFixtures.AssertConsistent();
            });
        }

        static void Case(List<CaseResult> cases, string name, Action action)
        {
            Check.Run(cases, name, () =>
            {
                RealStore.Clear();
                try { action(); }
                finally { RealStore.Clear(); }
            });
        }

        static void ExpectArgument(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Expected invalid argument failure before mutation.");
        }
    }
}
