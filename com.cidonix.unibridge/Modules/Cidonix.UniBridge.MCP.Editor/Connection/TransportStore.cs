using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Connection;
using Cidonix.UniBridge.MCP.Editor.Models;
using Cidonix.UniBridge.MCP.Editor.Security;

namespace Cidonix.UniBridge.MCP.Editor
{
    /// <summary>
    /// Per-transport connection approval state.
    /// </summary>
    enum ConnectionApprovalState
    {
        Unknown,
        Validating,
        AwaitingApproval,
        Approved,
        Denied
    }

    /// <summary>
    /// Consolidated state for a single local MCP transport connection.
    /// </summary>
    class TransportState
    {
        public readonly IConnectionTransport Transport;

        /// <summary>
        /// Identity key. Starts as "pending-{ConnectionId}", updated to real
        /// CombinedIdentityKey after validation completes.
        /// </summary>
        public string IdentityKey;

        public ConnectionApprovalState ApprovalState;
        public ValidationDecision ValidationDecision;

        /// <summary>
        /// MCP client info received via set_client_info command.
        /// Typically arrives before validation completes.
        /// </summary>
        public ClientInfo ClientInfo;

        public TransportState(IConnectionTransport transport, string initialIdentityKey)
        {
            Transport = transport;
            IdentityKey = initialIdentityKey;
        }
    }

    /// <summary>
    /// Thread-safe runtime store for local MCP transport state.
    /// Supports multiple transports per identity key while keeping approval
    /// and display state per physical connection.
    /// </summary>
    static class TransportStore
    {
        static readonly ConcurrentDictionary<IConnectionTransport, TransportState> States = new();
        static readonly ConcurrentDictionary<string, ConcurrentDictionary<IConnectionTransport, byte>> IdentityToTransports = new();
        static readonly object IdentityLock = new();

        public static TransportState Register(IConnectionTransport transport, string initialIdentityKey)
        {
            var state = new TransportState(transport, initialIdentityKey);
            States[transport] = state;

            var set = IdentityToTransports.GetOrAdd(initialIdentityKey, _ => new ConcurrentDictionary<IConnectionTransport, byte>());
            set[transport] = 0;

            return state;
        }

        public static void UpdateIdentityKey(IConnectionTransport transport, string newKey)
        {
            if (!States.TryGetValue(transport, out var state))
                return;

            lock (IdentityLock)
            {
                var oldKey = state.IdentityKey;
                if (oldKey != null && IdentityToTransports.TryGetValue(oldKey, out var oldSet))
                {
                    oldSet.TryRemove(transport, out _);
                    if (oldSet.IsEmpty)
                        IdentityToTransports.TryRemove(oldKey, out _);
                }

                var newSet = IdentityToTransports.GetOrAdd(newKey, _ => new ConcurrentDictionary<IConnectionTransport, byte>());
                newSet[transport] = 0;

                state.IdentityKey = newKey;
            }
        }

        public static TransportState GetState(IConnectionTransport transport)
        {
            States.TryGetValue(transport, out var state);
            return state;
        }

        public static IConnectionTransport GetTransportByIdentity(string identityKey)
        {
            if (IdentityToTransports.TryGetValue(identityKey, out var set))
                return set.Keys.FirstOrDefault();
            return null;
        }

        public static IReadOnlyList<IConnectionTransport> GetAllTransportsByIdentity(string identityKey)
        {
            if (IdentityToTransports.TryGetValue(identityKey, out var set))
                return set.Keys.ToList();
            return Array.Empty<IConnectionTransport>();
        }

        public static List<string> GetActiveIdentityKeys()
        {
            return IdentityToTransports.Keys.ToList();
        }

        public static int CountConnections()
        {
            int count = 0;
            foreach (var set in IdentityToTransports.Values)
                count += set.Count;
            return count;
        }

        /// <summary>
        /// Get all active, connected transport states.
        /// Returns one entry per physical connection for the settings UI.
        /// </summary>
        public static List<TransportState> GetActiveTransportStates()
        {
            var result = new List<TransportState>();
            foreach (var state in States.Values)
            {
                if (state.IdentityKey != null &&
                    !state.IdentityKey.StartsWith("pending-") &&
                    state.Transport.IsConnected)
                {
                    result.Add(state);
                }
            }
            return result;
        }

        public static TransportState Remove(IConnectionTransport transport)
        {
            if (!States.TryRemove(transport, out var state))
                return null;

            lock (IdentityLock)
            {
                var identityKey = state.IdentityKey;
                if (identityKey == null)
                    return state;

                if (IdentityToTransports.TryGetValue(identityKey, out var set))
                {
                    set.TryRemove(transport, out _);
                    if (set.IsEmpty)
                        IdentityToTransports.TryRemove(identityKey, out _);
                }
            }

            return state;
        }

        public static IConnectionTransport[] Clear()
        {
            var toClose = States.Keys
                .Concat(IdentityToTransports.Values.SelectMany(set => set.Keys))
                .Distinct()
                .ToArray();

            States.Clear();
            IdentityToTransports.Clear();

            return toClose;
        }
    }
}
