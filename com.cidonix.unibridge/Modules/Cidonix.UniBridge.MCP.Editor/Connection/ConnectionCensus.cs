using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Models;
using Cidonix.UniBridge.MCP.Editor.Security;
using Cidonix.UniBridge.Toolkit;

namespace Cidonix.UniBridge.MCP.Editor.Connection
{
    /// <summary>
    /// Identifies which cap caused a <see cref="ConnectionCensus"/> reservation to fail.
    /// </summary>
    enum CapKind
    {
        None = 0,
        /// <summary>The local direct-connection cap was hit.</summary>
        Pool,
    }

    /// <summary>
    /// Immutable local-connection policy snapshot. -1 means "unlimited".
    /// </summary>
    record ConnectionPolicy(int MaxDirect)
    {
        public static ConnectionPolicy Unlimited { get; } = new(-1);
    }

    /// <summary>
    /// Outcome of a reservation attempt. Carries the live count and cap so
    /// callers can build an accurate denial message without re-reading state.
    /// </summary>
    record ReservationResult(
        CapKind RejectedBy,
        int PoolCount,
        int PoolCap,
        string ClientKey)
    {
        public bool Allowed => RejectedBy == CapKind.None;
    }

    /// <summary>Snapshot view of a logical local MCP client used by developer tools.</summary>
    record LogicalClientSnapshot(
        string ClientKey,
        int? RootPid,
        string ExecutableKey,
        string DisplayName,
        int DirectTransportCount)
    {
        public bool HasDirect => DirectTransportCount > 0;
    }

    /// <summary>
    /// Single authoritative source of truth for active local MCP clients.
    /// Logical clients are deduped by deepest non-shell ancestor PID and, when
    /// Unity cannot resolve a PID, by executable identity as a fallback.
    /// </summary>
    static class ConnectionCensus
    {
        class LogicalClient
        {
            public string ClientKey;
            public int? RootPid;
            public string ExecutableKey;
            public string DisplayName;
            public long RegistrationSequence;
            public readonly HashSet<IConnectionTransport> DirectTransports = new();

            public bool HasDirect => DirectTransports.Count > 0;
            public bool IsEmpty => !HasDirect;
        }

        static readonly Dictionary<string, LogicalClient> s_Clients = new();
        static readonly ConcurrentDictionary<IConnectionTransport, string> s_TransportToClient = new();

        // Secondary index used by LookupClientLocked. Two aliases per client:
        //   "pid:{RootPid}"   - indexed whenever the client has a known PID.
        //   "exe:{ExeKey}"    - indexed only when the client has no known PID.
        static readonly Dictionary<string, string> s_KeyAliases = new();

        static readonly object s_Lock = new();
        static ConnectionPolicy s_Policy = ConnectionPolicy.Unlimited;
        static long s_NextRegistrationSequence;

        /// <summary>Raised on any logical-client registration / unregistration. Always marshalled via EditorTask.delayCall.</summary>
        public static event Action Changed;

        /// <summary>Raised when <see cref="Policy"/> changes. Subscribers read <see cref="Policy"/> for the new values.</summary>
        public static event Action PolicyChanged;

        /// <summary>Current local MCP connection policy. Read-only; mutate via <see cref="SetPolicy"/>.</summary>
        public static ConnectionPolicy Policy => s_Policy;

        public static void SetPolicy(ConnectionPolicy policy)
        {
            ConnectionPolicy previous;
            lock (s_Lock)
            {
                if (s_Policy.Equals(policy)) return;
                previous = s_Policy;
                s_Policy = policy;
            }

            PolicyChanged?.Invoke();

            if (Tightened(previous.MaxDirect, policy.MaxDirect))
                EditorTask.delayCall += EnforcePolicyFireAndForget;
        }

        static async void EnforcePolicyFireAndForget()
        {
            try { await EnforcePolicyAsync(); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }

        internal static Task EnforcePolicyAsync()
        {
            EvictOverflow();
            return Task.CompletedTask;
        }

        static void EvictOverflow()
        {
            List<LogicalClient> victims;
            lock (s_Lock)
            {
                int cap = s_Policy.MaxDirect;
                if (cap < 0) return;

                int overflow = CountLocked() - cap;
                if (overflow <= 0) return;

                victims = s_Clients.Values
                    .Where(c => c.HasDirect)
                    .OrderBy(c => c.RegistrationSequence)
                    .Take(overflow)
                    .ToList();
            }

            foreach (var client in victims)
            {
                List<IConnectionTransport> transports;
                lock (s_Lock)
                {
                    transports = new List<IConnectionTransport>(client.DirectTransports);
                }

                foreach (var transport in transports)
                {
                    try { transport.Dispose(); }
                    catch { /* best-effort - UnregisterTransport is called from transport cleanup */ }
                }
            }
        }

        static bool Tightened(int prev, int next)
        {
            if (prev == next) return false;
            if (prev < 0) return true;
            if (next < 0) return false;
            return next < prev;
        }

        /// <summary>Distinct logical clients with at least one local MCP transport.</summary>
        public static int DirectCount { get { lock (s_Lock) return CountLocked(); } }

        /// <summary>Total distinct logical clients.</summary>
        public static int LogicalClientCount { get { lock (s_Lock) return s_Clients.Count; } }

        public static string ResolveClientKey(ConnectionInfo info)
        {
            if (info == null) return null;
            var (rootPid, exeKey) = Identify(info);
            lock (s_Lock)
            {
                var existing = LookupClientLocked(rootPid, exeKey);
                if (existing != null) return existing.ClientKey;
            }
            return BuildClientKey(rootPid, exeKey, info.DisplayName);
        }

        /// <summary>
        /// Try to reserve a slot in the local MCP pool. Existing logical clients
        /// are free because they are already counted.
        /// </summary>
        public static ReservationResult TryReserveDirect(ConnectionInfo info) =>
            EvaluateLocked(info);

        /// <summary>Register a local MCP transport with the census after approval.</summary>
        public static void RegisterDirectTransport(IConnectionTransport transport, ConnectionInfo info) =>
            RegisterTransport(transport, info, "direct");

        public static void UnregisterTransport(IConnectionTransport transport)
        {
            if (transport == null) return;
            if (!s_TransportToClient.TryRemove(transport, out var clientKey)) return;

            bool changed = false;
            lock (s_Lock)
            {
                if (s_Clients.TryGetValue(clientKey, out var client))
                {
                    if (client.DirectTransports.Remove(transport))
                        changed = true;
                    if (client.IsEmpty)
                        RemoveClientLocked(client);
                }
            }

            if (changed) NotifyChanged();
        }

        public static IReadOnlyList<LogicalClientSnapshot> Snapshot()
        {
            lock (s_Lock)
            {
                return s_Clients.Values
                    .Select(c => new LogicalClientSnapshot(
                        c.ClientKey,
                        c.RootPid,
                        c.ExecutableKey,
                        c.DisplayName,
                        c.DirectTransports.Count))
                    .ToList();
            }
        }

        internal static void Clear()
        {
            lock (s_Lock)
            {
                s_Clients.Clear();
                s_KeyAliases.Clear();
                s_NextRegistrationSequence = 0;
            }
            s_TransportToClient.Clear();
            NotifyChanged();
        }

        static ReservationResult EvaluateLocked(ConnectionInfo info)
        {
            var (rootPid, exeKey) = Identify(info);

            lock (s_Lock)
            {
                var existing = LookupClientLocked(rootPid, exeKey);
                if (existing?.HasDirect == true)
                    return BuildResult(CapKind.None, existing.ClientKey);

                string clientKey = BuildClientKey(rootPid, exeKey, info?.DisplayName);
                return EvaluateNewClientLocked(clientKey);
            }
        }

        static ReservationResult EvaluateNewClientLocked(string clientKey)
        {
            int poolCount = CountLocked();
            int poolCap = s_Policy.MaxDirect;
            return poolCap >= 0 && poolCount >= poolCap
                ? BuildResult(CapKind.Pool, clientKey)
                : BuildResult(CapKind.None, clientKey);
        }

        static ReservationResult BuildResult(CapKind kind, string clientKey) =>
            new(kind,
                PoolCount: CountLocked(),
                PoolCap: s_Policy.MaxDirect,
                ClientKey: clientKey);

        static int CountLocked()
        {
            int count = 0;
            foreach (var client in s_Clients.Values)
            {
                if (client.HasDirect)
                    count++;
            }
            return count;
        }

        static void RegisterTransport(IConnectionTransport transport, ConnectionInfo info, string fallbackName)
        {
            if (transport == null) return;

            lock (s_Lock)
            {
                var client = GetOrCreateClientLocked(info, fallbackName);
                client.DirectTransports.Add(transport);
                s_TransportToClient[transport] = client.ClientKey;
            }

            NotifyChanged();
        }

        static (int? rootPid, string exeKey) Identify(ConnectionInfo info)
        {
            if (info == null) return (null, null);

            int? rootPid = info.Client?.ProcessId > 0
                ? info.Client.ProcessId
                : (info.Server?.ProcessId > 0 ? info.Server.ProcessId : (int?)null);

            string exeKey = info.Client?.Identity != null
                ? ExecutableIdentityComparer.GetIdentityKey(info.Client.Identity)
                : (info.Server?.Identity != null
                    ? ExecutableIdentityComparer.GetIdentityKey(info.Server.Identity)
                    : null);

            return (rootPid, exeKey);
        }

        static string BuildClientKey(int? rootPid, string exeKey, string fallbackName)
        {
            if (!rootPid.HasValue && string.IsNullOrEmpty(exeKey))
                return $"unknown:{fallbackName ?? Guid.NewGuid().ToString("N")}";
            return $"pid:{(rootPid?.ToString() ?? "?")}|exe:{exeKey ?? "?"}";
        }

        static LogicalClient LookupClientLocked(int? rootPid, string exeKey)
        {
            if (rootPid.HasValue)
            {
                return s_KeyAliases.TryGetValue($"pid:{rootPid.Value}", out var byPid)
                       && s_Clients.TryGetValue(byPid, out var pidClient)
                    ? pidClient
                    : null;
            }

            if (!string.IsNullOrEmpty(exeKey)
                && s_KeyAliases.TryGetValue($"exe:{exeKey}", out var byExe)
                && s_Clients.TryGetValue(byExe, out var exeClient))
                return exeClient;

            return null;
        }

        static LogicalClient GetOrCreateClientLocked(ConnectionInfo info, string fallbackName)
        {
            var (rootPid, exeKey) = Identify(info);
            string displayName = info?.DisplayName ?? fallbackName;

            var existing = LookupClientLocked(rootPid, exeKey);
            if (existing != null)
            {
                bool gainedPid = !existing.RootPid.HasValue && rootPid.HasValue;
                if (gainedPid) existing.RootPid = rootPid;
                if (string.IsNullOrEmpty(existing.ExecutableKey) && !string.IsNullOrEmpty(exeKey))
                    existing.ExecutableKey = exeKey;
                if (string.IsNullOrEmpty(existing.DisplayName) && !string.IsNullOrEmpty(displayName))
                    existing.DisplayName = displayName;
                if (gainedPid)
                    RemoveStaleAliasesLocked(existing);
                IndexAliasesLocked(existing);
                return existing;
            }

            string clientKey = BuildClientKey(rootPid, exeKey, displayName);
            if (!s_Clients.TryGetValue(clientKey, out var client))
            {
                client = new LogicalClient
                {
                    ClientKey = clientKey,
                    RootPid = rootPid,
                    ExecutableKey = exeKey,
                    DisplayName = displayName,
                    RegistrationSequence = s_NextRegistrationSequence++
                };
                s_Clients[clientKey] = client;
            }

            IndexAliasesLocked(client);
            return client;
        }

        static IEnumerable<string> AliasKeys(LogicalClient client)
        {
            if (client.RootPid.HasValue)
            {
                yield return $"pid:{client.RootPid.Value}";
                yield break;
            }

            if (!string.IsNullOrEmpty(client.ExecutableKey))
                yield return $"exe:{client.ExecutableKey}";
        }

        static void IndexAliasesLocked(LogicalClient client)
        {
            foreach (var alias in AliasKeys(client))
                s_KeyAliases[alias] = client.ClientKey;
        }

        static void RemoveStaleAliasesLocked(LogicalClient client)
        {
            var current = new HashSet<string>(AliasKeys(client));
            var toRemove = new List<string>();
            foreach (var kv in s_KeyAliases)
            {
                if (kv.Value == client.ClientKey && !current.Contains(kv.Key))
                    toRemove.Add(kv.Key);
            }
            foreach (var alias in toRemove)
                s_KeyAliases.Remove(alias);
        }

        static void RemoveClientLocked(LogicalClient client)
        {
            s_Clients.Remove(client.ClientKey);
            foreach (var alias in AliasKeys(client))
            {
                if (s_KeyAliases.TryGetValue(alias, out var mapped) && mapped == client.ClientKey)
                    s_KeyAliases.Remove(alias);
            }
        }

        static void NotifyChanged() => EditorTask.delayCall += () => Changed?.Invoke();
    }
}
