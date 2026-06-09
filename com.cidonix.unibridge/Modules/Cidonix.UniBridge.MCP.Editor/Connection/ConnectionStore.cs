using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Cidonix.UniBridge.MCP.Editor.Models;
using Cidonix.UniBridge.MCP.Editor.Security;
using Cidonix.UniBridge.Toolkit;

namespace Cidonix.UniBridge.MCP.Editor
{
    /// <summary>
    /// Thread-safe runtime store for MCP connection data.
    /// All public methods are static and safe to call from any thread.
    ///
    /// Persistence is handled separately by <see cref="ConnectionRegistry"/> (ScriptableSingleton),
    /// which hydrates this store on domain load and flushes it before save.
    /// </summary>
    static class ConnectionStore
    {
        /// <summary>
        /// Thread-safe runtime store for persisted connections, keyed by CombinedIdentityKey.
        /// </summary>
        internal static ConcurrentDictionary<string, ConnectionRecord> ConnectionsByIdentity = new();

        /// <summary>
        /// Event fired when connection history changes (add, update, remove, clear).
        /// Always invoked on the main thread.
        /// </summary>
        public static event Action OnConnectionHistoryChanged;

        /// <summary>
        /// Called by <see cref="ConnectionRegistry"/> to persist changes to disk.
        /// </summary>
        internal static Action OnSaveRequested;

        static void NotifyAndMarkDirty()
        {
            EditorTask.delayCall += () =>
            {
                OnConnectionHistoryChanged?.Invoke();
            };
            // Always persist after mutation — SaveManager debounces via delayCall
            OnSaveRequested?.Invoke();
        }

        /// <summary>
        /// Record a new connection attempt.
        /// If a connection with the same identity already exists, replace it while preserving approval status.
        /// </summary>
        public static void RecordConnection(ValidationDecision decision)
        {
            if (decision?.Connection == null)
                return;

            // Validate connection data before recording
            if (decision.Connection.Timestamp == DateTime.MinValue)
            {
                Debug.LogWarning($"[MCP] Attempting to record connection with invalid timestamp (MinValue). ConnectionId: {decision.Connection.ConnectionId}, Client: {decision.Connection.Client?.ProcessName ?? "unknown"}. This connection will not be recorded.");
                return;
            }

            // Create identity for this connection
            var identity = ConnectionIdentity.FromConnectionInfo(decision.Connection);
            if (identity?.CombinedIdentityKey == null)
                return;

            ConnectionsByIdentity.AddOrUpdate(
                identity.CombinedIdentityKey,
                // Add factory: new connection
                _ => new ConnectionRecord
                {
                    Info = decision.Connection,
                    Status = decision.Status,
                    ValidationReason = decision.Reason,
                    Identity = identity
                },
                // Update factory: merge with existing
                (_, existingRecord) =>
                {
                    // Preserve ClientInfo from previous session if new connection
                    // doesn't have it yet (set_client_info arrives later in the handshake)
                    var previousClientInfo = existingRecord.Info?.ClientInfo;
                    existingRecord.Info = decision.Connection;
                    if (existingRecord.Info.ClientInfo == null && previousClientInfo != null)
                        existingRecord.Info.ClientInfo = previousClientInfo;
                    existingRecord.Identity = identity;

                    // System-enforced statuses (like CapacityLimit) always override,
                    // but user decisions (Accepted/Rejected) are preserved against
                    // non-system statuses (e.g. a reconnect shouldn't reset approval).
                    bool isSystemEnforced = decision.Status == ValidationStatus.CapacityLimit;
                    bool shouldPreserveStatus = !isSystemEnforced &&
                        (existingRecord.Status == ValidationStatus.Accepted ||
                         existingRecord.Status == ValidationStatus.Rejected);

                    if (!shouldPreserveStatus)
                    {
                        existingRecord.Status = decision.Status;
                        existingRecord.ValidationReason = decision.Reason;
                    }

                    return existingRecord;
                });

            // Evict oldest if over 1000 connections
            if (ConnectionsByIdentity.Count > 1000)
            {
                var oldest = ConnectionsByIdentity.Values
                    .OrderBy(c => c.Info?.Timestamp ?? DateTime.MinValue)
                    .FirstOrDefault();
                if (oldest?.Identity?.CombinedIdentityKey != null)
                {
                    ConnectionsByIdentity.TryRemove(oldest.Identity.CombinedIdentityKey, out _);
                }
            }

            NotifyAndMarkDirty();
        }

        /// <summary>
        /// Update the status of an existing connection
        /// </summary>
        public static bool UpdateConnectionStatus(string connectionId, ValidationStatus newStatus, string newReason = null)
        {
            if (string.IsNullOrEmpty(connectionId))
                return false;

            var record = ConnectionsByIdentity.Values
                .FirstOrDefault(c => c.Info?.ConnectionId == connectionId);

            if (record != null)
            {
                record.Status = newStatus;
                if (newReason != null)
                {
                    record.ValidationReason = newReason;
                }

                NotifyAndMarkDirty();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Find a connection record that matches the given identity.
        /// </summary>
        public static ConnectionRecord FindMatchingConnection(ConnectionIdentity identity)
        {
            if (identity?.CombinedIdentityKey == null)
                return null;

            ConnectionsByIdentity.TryGetValue(identity.CombinedIdentityKey, out var record);
            return record;
        }

        /// <summary>
        /// Find a connection record that matches the given ConnectionInfo's identity.
        /// </summary>
        public static ConnectionRecord FindMatchingConnection(ConnectionInfo connectionInfo)
        {
            if (connectionInfo == null)
                return null;

            var identity = ConnectionIdentity.FromConnectionInfo(connectionInfo);
            return FindMatchingConnection(identity);
        }

        /// <summary>
        /// Find a connection record matching the same server+client publisher pair.
        /// Used as a fallback when exact identity key doesn't match (e.g., after version update).
        /// </summary>
        public static ConnectionRecord FindMatchingConnectionByPublisher(ConnectionInfo connectionInfo)
        {
            if (connectionInfo?.Server?.Identity == null || connectionInfo?.Client?.Identity == null)
                return null;

            var serverPub = connectionInfo.Server.Identity.SignaturePublisher;
            var clientPub = connectionInfo.Client.Identity.SignaturePublisher;

            if (string.IsNullOrEmpty(serverPub) || string.IsNullOrEmpty(clientPub))
                return null;

            ConnectionRecord bestMatch = null;
            foreach (var record in ConnectionsByIdentity.Values)
            {
                var s = record.Info?.Server?.Identity;
                var c = record.Info?.Client?.Identity;
                if (s == null || c == null) continue;

                if (string.Equals(s.SignaturePublisher, serverPub, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.SignaturePublisher, clientPub, StringComparison.OrdinalIgnoreCase))
                {
                    if (record.Status == ValidationStatus.Accepted)
                        return record;
                    if (record.Status == ValidationStatus.Rejected)
                        bestMatch = record;
                }
            }
            return bestMatch;
        }

        /// <summary>
        /// Remove a connection from history
        /// </summary>
        public static bool RemoveConnection(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
                return false;

            var record = ConnectionsByIdentity.Values
                .FirstOrDefault(c => c.Info?.ConnectionId == connectionId);

            if (record?.Identity?.CombinedIdentityKey != null &&
                ConnectionsByIdentity.TryRemove(record.Identity.CombinedIdentityKey, out _))
            {
                NotifyAndMarkDirty();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Remove all connections from history
        /// </summary>
        public static void ClearAllConnections()
        {
            if (ConnectionsByIdentity.IsEmpty)
                return;

            ConnectionsByIdentity.Clear();
            NotifyAndMarkDirty();
        }

        /// <summary>
        /// Get recent connections (newest first)
        /// </summary>
        public static List<ConnectionRecord> GetRecentConnections(int count = 50)
        {
            return ConnectionsByIdentity.Values
                .OrderByDescending(c => c.Info?.Timestamp ?? DateTime.MinValue)
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// Clear the DialogShown flag for a connection, allowing the dialog to show again if needed.
        /// Useful when user manually approves a previously dismissed connection.
        /// </summary>
        public static void ClearDialogShown(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
                return;

            var record = ConnectionsByIdentity.Values
                .FirstOrDefault(c => c.Info?.ConnectionId == connectionId);

            if (record != null && record.DialogShown)
            {
                record.DialogShown = false;
                NotifyAndMarkDirty();
            }
        }

        /// <summary>
        /// Get connection record by identity key.
        /// </summary>
        public static ConnectionRecord GetConnectionByIdentity(string identityKey)
        {
            if (string.IsNullOrEmpty(identityKey))
                return null;

            ConnectionsByIdentity.TryGetValue(identityKey, out var record);
            return record;
        }

        /// <summary>
        /// Update client info (Name, Version, Title) for a connection.
        /// </summary>
        public static void UpdateClientInfo(string identityKey, ClientInfo clientInfo)
        {
            if (string.IsNullOrEmpty(identityKey) || clientInfo == null)
                return;

            if (ConnectionsByIdentity.TryGetValue(identityKey, out var record) && record.Info != null)
            {
                record.Info.ClientInfo = clientInfo;
                NotifyAndMarkDirty();
            }
        }

        /// <summary>
        /// Get active connection records (those with identities in the active set).
        /// </summary>
        public static List<ConnectionRecord> GetActiveConnections(IEnumerable<string> activeIdentityKeys)
        {
            if (activeIdentityKeys == null)
                return new List<ConnectionRecord>();

            var result = new List<ConnectionRecord>();
            foreach (var key in activeIdentityKeys)
            {
                if (ConnectionsByIdentity.TryGetValue(key, out var record))
                {
                    result.Add(record);
                }
            }

            return result;
        }

        /// <summary>
        /// Get formatted client info string for all active connections.
        /// Used by debug menu item.
        /// </summary>
        public static string GetClientInfo(IEnumerable<string> activeIdentityKeys)
        {
            var activeConnections = GetActiveConnections(activeIdentityKeys);

            if (activeConnections.Count == 0)
                return "No clients connected";

            var sb = new StringBuilder();
            sb.AppendLine($"Connected clients: {activeConnections.Count}");
            foreach (var record in activeConnections)
            {
                var clientInfo = record.Info?.ClientInfo;
                if (clientInfo != null)
                {
                    string displayName = string.IsNullOrEmpty(clientInfo.Title) ? clientInfo.Name : clientInfo.Title;
                    sb.AppendLine($"  - {displayName} v{clientInfo.Version} (connection: {clientInfo.ConnectionId})");
                }
                else
                {
                    // Fallback if ClientInfo not set yet
                    sb.AppendLine($"  - {record.Info?.DisplayName ?? "Unknown"} (connection: {record.Info?.ConnectionId ?? "unknown"})");
                }
            }
            return sb.ToString().TrimEnd();
        }
    }
}
