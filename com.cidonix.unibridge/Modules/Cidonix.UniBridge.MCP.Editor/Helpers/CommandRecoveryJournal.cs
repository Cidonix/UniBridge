using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Main-thread command admission and query-only recovery. Storage delegates use Editor
    /// SessionState in production, so a domain reload preserves evidence without replaying work.
    /// A missing result is never evidence that the command did not change the project.
    /// </summary>
    internal sealed class CommandRecoveryJournal
    {
        public const string SessionKey = "Cidonix.UniBridge.CommandRecovery.Session.v1";
        public const string JournalKey = "Cidonix.UniBridge.CommandRecovery.Journal.v1";
        public const int RetentionSeconds = 600;
        public const int MaxRecords = 2048;
        public const int MaxStoredResponseBytes = 512 * 1024;
        public const int MaxJournalBytes = 8 * 1024 * 1024;

        readonly Action<string, string> write;
        readonly Func<DateTime> clock;
        readonly Dictionary<string, Record> records = new Dictionary<string, Record>(StringComparer.Ordinal);
        bool active = true;

        public string SessionId { get; private set; }
        public bool IsAvailable { get; private set; }

        public sealed class Record
        {
            internal string Key;
            internal string Fingerprint;
            internal long ExpiresUtcTicks;
            internal string State;
            internal string Response;
            internal string RetentionReason;
            internal bool Persistent;
            internal TaskCompletionSource<string> CompletionSource;
            public Task<string> Completion => CompletionSource?.Task;
        }

        public sealed class RecoveryResult
        {
            public string State { get; internal set; }
            public string Response { get; internal set; }
            public string Reason { get; internal set; }
        }

        public sealed class Admission
        {
            public bool ShouldExecute { get; internal set; }
            public Record Record { get; internal set; }
            public RecoveryResult Existing { get; internal set; }
        }

        public CommandRecoveryJournal(Func<string, string> read, Action<string, string> write, Func<DateTime> clock = null)
        {
            this.write = write ?? throw new ArgumentNullException(nameof(write));
            this.clock = clock ?? (() => DateTime.UtcNow);
            if (read == null) throw new ArgumentNullException(nameof(read));
            try
            {
                SessionId = read(SessionKey);
                if (string.IsNullOrEmpty(SessionId))
                {
                    SessionId = Guid.NewGuid().ToString("N");
                    write(SessionKey, SessionId);
                }

                var stored = read(JournalKey);
                if (!string.IsNullOrEmpty(stored))
                {
                    if (Encoding.UTF8.GetByteCount(stored) > MaxJournalBytes)
                        throw new InvalidOperationException("Command journal exceeded its storage bound.");
                    var journal = JObject.Parse(stored);
                    if (journal.Value<int>("version") != 1 || journal.Value<string>("sessionId") != SessionId)
                        throw new InvalidOperationException("Command journal belongs to another session.");
                    var entries = journal["records"] as JArray;
                    if (entries == null || entries.Count > MaxRecords)
                        throw new InvalidOperationException("Invalid command journal records.");
                    foreach (var token in entries)
                    {
                        var item = token as JObject ?? throw new InvalidOperationException("Invalid command journal record.");
                        var record = new Record
                        {
                            Key = item.Value<string>("key"),
                            Fingerprint = item.Value<string>("fingerprint"),
                            ExpiresUtcTicks = item.Value<long>("expiresUtcTicks"),
                            State = item.Value<string>("state"),
                            Response = item.Value<string>("response"),
                            RetentionReason = item.Value<string>("retentionReason"),
                            Persistent = true
                        };
                        if (string.IsNullOrEmpty(record.Key) || string.IsNullOrEmpty(record.Fingerprint) ||
                            (record.State != "started" && record.State != "completed"))
                            throw new InvalidOperationException("Invalid command journal record identity.");
                        records.Add(record.Key, record);
                    }
                }
                IsAvailable = true;
                Prune();
            }
            catch
            {
                // Do not silently replace corrupt evidence and admit possibly repeated writes.
                IsAvailable = false;
                records.Clear();
            }
        }

        public Admission Begin(string identity, string requestId, string type, JObject parameters, bool persist = true)
        {
            if (!active || (!IsAvailable && persist))
                return Refused("journal_unavailable");
            if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(type))
                return Refused("missing_operation_identity");

            Prune();
            var key = Key(identity, requestId);
            var fingerprint = Fingerprint(type, parameters);
            if (records.TryGetValue(key, out var existing))
                return new Admission { Record = existing, Existing = Inspect(existing, fingerprint) };

            // Read-only traffic may discard its completed memory cache under pressure,
            // but may never displace durable write evidence or another active operation.
            while (records.Count >= MaxRecords)
            {
                var removable = records.Values.Where(item => !item.Persistent && item.State == "completed")
                    .OrderBy(item => item.ExpiresUtcTicks).FirstOrDefault();
                if (removable == null) break;
                records.Remove(removable.Key);
            }
            if (records.Count >= MaxRecords)
                return Refused("journal_capacity_reached");
            var record = new Record
            {
                Key = key,
                Fingerprint = fingerprint,
                ExpiresUtcTicks = clock().AddSeconds(RetentionSeconds).Ticks,
                State = "started",
                Persistent = persist,
                CompletionSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            records.Add(key, record);
            try
            {
                if (persist)
                    Persist(); // Evidence must exist before a mutating handler can run.
                return new Admission { ShouldExecute = true, Record = record };
            }
            catch
            {
                records.Remove(key);
                IsAvailable = false;
                return Refused("journal_start_not_persisted");
            }
        }

        public void Complete(Record record, string response)
        {
            if (!active || record == null || !records.TryGetValue(record.Key, out var current) || !ReferenceEquals(current, record))
                return;
            record.State = "completed";
            var terminalResponse = StripRoutingId(response);
            record.Response = terminalResponse;
            if (Encoding.UTF8.GetByteCount(terminalResponse) > MaxStoredResponseBytes)
            {
                record.Response = null;
                record.RetentionReason = "response_exceeds_storage_bound";
            }
            record.ExpiresUtcTicks = clock().AddSeconds(RetentionSeconds).Ticks;
            try
            {
                if (record.Persistent)
                    Persist(); // Publish the terminal result durably before releasing duplicate waiters.
                else
                    TrimReadCache();
            }
            catch
            {
                // The persisted 'started' record still prevents execution after reload.
                // Current-domain callers may use the known terminal result.
                IsAvailable = false;
            }
            record.CompletionSource?.TrySetResult(terminalResponse);
            record.CompletionSource = null;
        }

        public RecoveryResult Query(string identity, string requestId, string type, JObject parameters, string expectedSessionId)
        {
            if (string.IsNullOrEmpty(expectedSessionId) || expectedSessionId != SessionId)
                return Unknown("editor_session_changed");
            if (!active || !IsAvailable)
                return Unknown("journal_unavailable");
            if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(type))
                return Unknown("missing_operation_identity");
            Prune();
            return records.TryGetValue(Key(identity, requestId), out var record)
                ? Inspect(record, Fingerprint(type, parameters))
                : Unknown("operation_not_retained");
        }

        public void Deactivate()
        {
            active = false;
        }

        static Admission Refused(string reason) => new Admission { Existing = Unknown(reason) };
        static RecoveryResult Unknown(string reason) => new RecoveryResult { State = "outcome_unknown", Reason = reason };

        static RecoveryResult Inspect(Record record, string fingerprint)
        {
            if (!string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal))
                return new RecoveryResult { State = "request_conflict", Reason = "request_id_reused_with_different_command" };
            if (record.State == "completed")
                return record.Response == null
                    ? Unknown(record.RetentionReason ?? "response_not_retained")
                    : new RecoveryResult { State = "completed", Response = record.Response };
            return record.CompletionSource != null
                ? new RecoveryResult { State = "in_flight" }
                : Unknown("execution_interrupted_by_reload");
        }

        void Prune()
        {
            var now = clock().Ticks;
            foreach (var key in records.Where(pair => pair.Value.ExpiresUtcTicks <= now &&
                (pair.Value.CompletionSource == null || pair.Value.State == "completed")).Select(pair => pair.Key).ToArray())
                records.Remove(key);
        }

        void Persist()
        {
            Prune();
            var array = new JArray();
            foreach (var record in records.Values.Where(item => item.Persistent).OrderByDescending(item => item.ExpiresUtcTicks))
            {
                var response = record.Response;
                var reason = record.RetentionReason;
                if (response != null && Encoding.UTF8.GetByteCount(response) > MaxStoredResponseBytes)
                {
                    response = null;
                    reason = "response_exceeds_storage_bound";
                }
                array.Add(new JObject
                {
                    ["key"] = record.Key,
                    ["fingerprint"] = record.Fingerprint,
                    ["expiresUtcTicks"] = record.ExpiresUtcTicks,
                    ["state"] = record.State,
                    ["response"] = response,
                    ["retentionReason"] = reason
                });
            }
            var journal = new JObject { ["version"] = 1, ["sessionId"] = SessionId, ["records"] = array };
            var serialized = journal.ToString(Formatting.None);
            // Keep admission evidence for every retained operation; omit older response bodies
            // first when their combined size would exceed SessionState's explicit journal bound.
            for (int i = array.Count - 1; Encoding.UTF8.GetByteCount(serialized) > MaxJournalBytes && i >= 0; i--)
            {
                var item = (JObject)array[i];
                if (item["response"]?.Type == JTokenType.String)
                {
                    item["response"] = null;
                    item["retentionReason"] = "journal_response_storage_bound";
                    var key = item.Value<string>("key");
                    records[key].Response = null;
                    records[key].RetentionReason = "journal_response_storage_bound";
                    serialized = journal.ToString(Formatting.None);
                }
            }
            if (Encoding.UTF8.GetByteCount(serialized) > MaxJournalBytes)
                throw new InvalidOperationException("Command journal capacity exhausted.");
            write(JournalKey, serialized);
        }

        void TrimReadCache()
        {
            var completedReads = records.Values.Where(item => !item.Persistent && item.State == "completed")
                .OrderByDescending(item => item.ExpiresUtcTicks).ToArray();
            long bytes = 0;
            foreach (var record in completedReads)
            {
                bytes += record.Response == null ? 0 : Encoding.UTF8.GetByteCount(record.Response);
                if (bytes > MaxJournalBytes)
                    records.Remove(record.Key);
            }
        }

        static string Key(string identity, string requestId) => Hash(identity.Length + ":" + identity + requestId);

        public static string Fingerprint(string type, JObject parameters)
        {
            return Hash((type ?? string.Empty) + "\n" + Canonical(parameters ?? new JObject()).ToString(Formatting.None));
        }

        static JToken Canonical(JToken token)
        {
            if (token is JObject obj)
                return new JObject(obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => new JProperty(property.Name, Canonical(property.Value))));
            if (token is JArray array)
                return new JArray(array.Select(Canonical));
            return token.DeepClone();
        }

        static string Hash(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty);
        }

        static string StripRoutingId(string response)
        {
            try
            {
                var obj = JObject.Parse(response);
                obj.Remove("requestId");
                return obj.ToString(Formatting.None);
            }
            catch
            {
                return JsonConvert.SerializeObject(new { status = "error", error = "Command returned an invalid protocol response." });
            }
        }
    }
}
