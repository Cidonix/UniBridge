using System.Text;
using System.Text.Json.Nodes;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Newtonsoft.Json.Linq;
using static Cidonix.UniBridge.Relay.CommandReplayRegression;

namespace Cidonix.UniBridge.Relay;

static class JournalRegression
{
    public static async Task RunAsync()
    {
        await CaseAsync("journal_durable_admission_and_simultaneous_duplicate", async () =>
        {
            var fixture = new Storage();
            var journal = fixture.Create();
            var first = journal.Begin("project", "operation-1", "Write", JObject.Parse("{\"value\":1}"));
            Assert(first.ShouldExecute, "first original operation was not admitted");
            Assert(fixture.Values[CommandRecoveryJournal.JournalKey].Contains("started", StringComparison.Ordinal), "execution admitted before durable started record");
            var second = journal.Begin("project", "operation-1", "Write", JObject.Parse("{\"value\":1}"));
            Assert(!second.ShouldExecute && second.Existing.State == "in_flight", "simultaneous duplicate could execute");
            var completion = second.Record.Completion;
            Assert(completion != null && !completion.IsCompleted, "duplicate did not wait for original terminal response");
            journal.Complete(first.Record, "{\"status\":\"success\",\"requestId\":\"old-route\",\"result\":{\"created\":1}}");
            var response = await completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(!response.Contains("requestId", StringComparison.Ordinal), "old transport route leaked into retained result");
            Assert(fixture.Values[CommandRecoveryJournal.JournalKey].Contains("completed", StringComparison.Ordinal), "duplicate released without durable terminal result");
            Assert(journal.Query("project", "operation-1", "Write", JObject.Parse("{\"value\":1}"), journal.SessionId).State == "completed", "completed result not queryable");
            return new() { ["duplicateState"] = second.Existing.State, ["terminalResponse"] = response };
        });

        await CaseAsync("journal_completed_success_and_error_survive_reload", () =>
        {
            var fixture = new Storage();
            var journal = fixture.Create();
            foreach (var status in new[] { "success", "error" })
            {
                var admission = journal.Begin("project", status, "Write", new());
                Assert(admission.ShouldExecute, "initial operation refused");
                journal.Complete(admission.Record, "{\"status\":\"" + status + "\",\"result\":{\"value\":42}}");
            }
            var restored = fixture.Create();
            Assert(restored.SessionId == journal.SessionId, "domain reload changed editor epoch");
            foreach (var status in new[] { "success", "error" })
            {
                var recovery = restored.Query("project", status, "Write", new(), journal.SessionId);
                Assert(recovery.State == "completed" && JObject.Parse(recovery.Response).Value<string>("status") == status, "reload lost exact terminal envelope");
                Assert(!restored.Begin("project", status, "Write", new()).ShouldExecute, "restored completed operation was readmitted");
            }
            return Task.FromResult<JsonObject>(new() { ["retainedResults"] = 2, ["sameEditorEpoch"] = true });
        });

        await CaseAsync("journal_started_operation_after_reload_remains_unknown", () =>
        {
            var fixture = new Storage();
            var oldJournal = fixture.Create();
            var admission = oldJournal.Begin("project", "started-before-reload", "Write", new());
            Assert(admission.ShouldExecute, "initial operation refused");
            oldJournal.Deactivate();
            var restored = fixture.Create();
            var before = fixture.Values[CommandRecoveryJournal.JournalKey];
            oldJournal.Complete(admission.Record, "{\"status\":\"success\"}");
            Assert(fixture.Values[CommandRecoveryJournal.JournalKey] == before, "old-domain completion overwrote restored evidence");
            var recovery = restored.Query("project", "started-before-reload", "Write", new(), oldJournal.SessionId);
            Assert(recovery.State == "outcome_unknown", "started operation incorrectly became completed after reload");
            Assert(!restored.Begin("project", "started-before-reload", "Write", new()).ShouldExecute, "interrupted operation was readmitted");
            return Task.FromResult<JsonObject>(new() { ["state"] = recovery.State, ["reason"] = recovery.Reason });
        });

        await CaseAsync("journal_fingerprint_mismatch_and_property_order", () =>
        {
            var fixture = new Storage();
            var journal = fixture.Create();
            var arguments = JObject.Parse("{\"target\":{\"b\":2,\"a\":1},\"array\":[1,2]}");
            var admitted = journal.Begin("project", "stable", "Write", arguments);
            Assert(admitted.ShouldExecute, "initial operation refused");
            var reordered = JObject.Parse("{\"array\":[1,2],\"target\":{\"a\":1,\"b\":2}}");
            Assert(journal.Query("project", "stable", "Write", reordered, journal.SessionId).State == "in_flight", "equivalent property order became conflict");
            foreach (var changed in new[] { JObject.Parse("{\"target\":{\"b\":2,\"a\":3},\"array\":[1,2]}"), JObject.Parse("{\"target\":{\"b\":2,\"a\":1},\"array\":[2,1]}") })
            {
                var duplicate = journal.Begin("project", "stable", "Write", changed);
                Assert(!duplicate.ShouldExecute && duplicate.Existing.State == "request_conflict", "different arguments shared operation ID");
            }
            Assert(journal.Query("project", "stable", "OtherWrite", arguments, journal.SessionId).State == "request_conflict", "different tool shared operation ID");
            return Task.FromResult<JsonObject>(new() { ["equivalentOrderAccepted"] = true, ["changedArgumentsRefused"] = 2, ["changedToolRefused"] = true });
        });

        await CaseAsync("journal_identity_and_editor_epoch_are_scoped", () =>
        {
            var fixture = new Storage();
            var journal = fixture.Create();
            var first = journal.Begin("project-a", "same-id", "Write", new());
            journal.Complete(first.Record, "{\"status\":\"success\",\"result\":{\"private\":true}}");
            Assert(journal.Query("project-b", "same-id", "Write", new(), journal.SessionId).State == "outcome_unknown", "result leaked to another project identity");
            Assert(journal.Query("project-a", "same-id", "Write", new(), "new-editor-epoch").State == "outcome_unknown", "old editor result leaked to another epoch");
            Assert(journal.Begin("project-b", "same-id", "Write", new()).ShouldExecute, "independent identity collided with original request ID");
            return Task.FromResult<JsonObject>(new() { ["identityIsolation"] = true, ["epochIsolation"] = true });
        });

        await CaseAsync("journal_corrupt_storage_and_admission_write_failure_fail_closed", () =>
        {
            var corrupt = new Storage();
            corrupt.Values[CommandRecoveryJournal.JournalKey] = "broken json";
            var damaged = corrupt.Create();
            Assert(!damaged.IsAvailable && !damaged.Begin("project", "operation", "Write", new()).ShouldExecute, "corrupt evidence was silently replaced");
            var failing = new Storage();
            var journal = failing.Create();
            failing.FailWrites = true;
            var admission = journal.Begin("project", "operation", "Write", new());
            Assert(!admission.ShouldExecute && admission.Existing.State == "outcome_unknown", "storage write failure admitted execution");
            return Task.FromResult<JsonObject>(new() { ["corruptStorageBlocked"] = true, ["failedDurableStartBlocked"] = true });
        });

        await CaseAsync("journal_completion_write_failure_does_not_readmit", async () =>
        {
            var fixture = new Storage();
            var journal = fixture.Create();
            var admission = journal.Begin("project", "operation", "Write", new());
            var waiter = journal.Begin("project", "operation", "Write", new()).Record.Completion;
            fixture.FailWrites = true;
            journal.Complete(admission.Record, "{\"status\":\"success\",\"result\":{\"executed\":1}}");
            Assert((await waiter.WaitAsync(TimeSpan.FromSeconds(2))).Contains("executed", StringComparison.Ordinal), "original completion was not released");
            Assert(!journal.Begin("project", "operation", "Write", new()).ShouldExecute, "storage failure reopened admission");
            fixture.FailWrites = false;
            var restored = fixture.Create();
            Assert(restored.Query("project", "operation", "Write", new(), journal.SessionId).State == "outcome_unknown", "reload invented success after failed terminal persistence");
            Assert(!restored.Begin("project", "operation", "Write", new()).ShouldExecute, "persisted started operation replayed after reload");
            return new() { ["terminalWaiterReleased"] = true, ["afterReloadState"] = "outcome_unknown" };
        });

        await CaseAsync("journal_large_response_preserves_admission_evidence", async () =>
        {
            var fixture = new Storage();
            var journal = fixture.Create();
            var admission = journal.Begin("project", "large", "Write", new());
            var waiter = journal.Begin("project", "large", "Write", new()).Record.Completion;
            var response = new JObject { ["status"] = "success", ["result"] = new string('x', CommandRecoveryJournal.MaxStoredResponseBytes + 32) }.ToString(Newtonsoft.Json.Formatting.None);
            journal.Complete(admission.Record, response);
            Assert((await waiter.WaitAsync(TimeSpan.FromSeconds(2))).Length == response.Length, "large original response was truncated for active duplicate");
            var recovery = journal.Query("project", "large", "Write", new(), journal.SessionId);
            Assert(recovery.State == "outcome_unknown" && recovery.Reason == "response_exceeds_storage_bound", "oversized result incorrectly retained");
            Assert(!journal.Begin("project", "large", "Write", new()).ShouldExecute, "omitted large response lost admission evidence");
            var restored = fixture.Create();
            Assert(!restored.Begin("project", "large", "Write", new()).ShouldExecute, "reload replayed large-result mutation");
            return new() { ["originalResponseBytes"] = Encoding.UTF8.GetByteCount(response), ["retainedState"] = recovery.State };
        });

        await CaseAsync("journal_combined_storage_bound_never_drops_admission", () =>
        {
            var fixture = new Storage();
            var journal = fixture.Create();
            for (var i = 0; i < 18; i++)
            {
                var admission = journal.Begin("project", "large-" + i, "Write", new());
                Assert(admission.ShouldExecute, "journal capacity refused bounded test operation");
                journal.Complete(admission.Record, new JObject { ["status"] = "success", ["result"] = new string('x', 500 * 1024) }.ToString(Newtonsoft.Json.Formatting.None));
            }
            var bytes = Encoding.UTF8.GetByteCount(fixture.Values[CommandRecoveryJournal.JournalKey]);
            Assert(bytes <= CommandRecoveryJournal.MaxJournalBytes, "combined response storage exceeds bound");
            var restored = fixture.Create();
            Assert(restored.IsAvailable, "bounded journal became corrupt on reload");
            for (var i = 0; i < 18; i++)
                Assert(!restored.Begin("project", "large-" + i, "Write", new()).ShouldExecute, "response eviction dropped operation identity");
            return Task.FromResult<JsonObject>(new() { ["journalBytes"] = bytes, ["admissionsRetained"] = 18 });
        });

        await CaseAsync("journal_retention_expiry_reports_unknown", () =>
        {
            var fixture = new Storage();
            var journal = fixture.Create();
            var admission = journal.Begin("project", "operation", "Write", new());
            journal.Complete(admission.Record, "{\"status\":\"success\"}");
            fixture.Utc = fixture.Utc.AddSeconds(CommandRecoveryJournal.RetentionSeconds + 1);
            var recovery = journal.Query("project", "operation", "Write", new(), journal.SessionId);
            Assert(recovery.State == "outcome_unknown" && recovery.Reason == "operation_not_retained", "expired result advertised as completed");
            return Task.FromResult<JsonObject>(new() { ["state"] = recovery.State, ["reason"] = recovery.Reason });
        });

        await CaseAsync("journal_read_dedup_does_not_write_storage_or_survive_reload", () =>
        {
            var fixture = new Storage();
            var journal = fixture.Create();
            var initialWrites = fixture.WriteCount;
            var read = journal.Begin("project", "read-id", "Read", new(), persist: false);
            Assert(read.ShouldExecute, "original read refused");
            var duplicate = journal.Begin("project", "read-id", "Read", new(), persist: false);
            Assert(!duplicate.ShouldExecute && duplicate.Existing.State == "in_flight", "in-memory read duplicate could execute");
            journal.Complete(read.Record, "{\"status\":\"success\",\"result\":{\"value\":42}}");
            Assert(journal.Query("project", "read-id", "Read", new(), journal.SessionId).State == "completed", "same-domain read result not cached");
            Assert(fixture.WriteCount == initialWrites, "read lifecycle wrote durable SessionState journal");
            var restored = fixture.Create();
            Assert(restored.Query("project", "read-id", "Read", new(), journal.SessionId).State == "outcome_unknown", "ephemeral read retained across reload");
            Assert(restored.Begin("project", "read-id", "Read", new(), persist: false).ShouldExecute, "safe read blocked after reload");
            return Task.FromResult<JsonObject>(new() { ["storageWritesForRead"] = 0, ["sameDomainDedup"] = true, ["reloadReadmitted"] = true });
        });

        await CaseAsync("journal_read_pressure_preserves_live_durable_write", () =>
        {
            var fixture = new Storage();
            var journal = fixture.Create();
            var write = journal.Begin("project", "durable-write", "Write", new());
            Assert(write.ShouldExecute, "original write refused");
            var initialWrites = fixture.WriteCount;
            for (var i = 0; i < CommandRecoveryJournal.MaxRecords + 32; i++)
            {
                var read = journal.Begin("project", "read-" + i, "Read", new(), persist: false);
                Assert(read.ShouldExecute, "completed read pressure exhausted journal instead of evicting completed reads");
                journal.Complete(read.Record, "{\"status\":\"success\"}");
            }
            Assert(fixture.WriteCount == initialWrites, "read pressure wrote durable journal");
            var duplicate = journal.Begin("project", "durable-write", "Write", new());
            Assert(!duplicate.ShouldExecute && duplicate.Existing.State == "in_flight", "read pressure evicted active durable write evidence");
            var restored = fixture.Create();
            Assert(!restored.Begin("project", "durable-write", "Write", new()).ShouldExecute, "reload lost durable write after read pressure");
            return Task.FromResult<JsonObject>(new() { ["completedReads"] = CommandRecoveryJournal.MaxRecords + 32, ["durableWritePreserved"] = true });
        });

        await CaseAsync("journal_corrupt_storage_does_not_block_ephemeral_reads", () =>
        {
            var fixture = new Storage();
            fixture.Values[CommandRecoveryJournal.JournalKey] = "broken json";
            var journal = fixture.Create();
            Assert(!journal.IsAvailable, "corrupt journal unexpectedly available");
            Assert(journal.Begin("project", "safe-read", "Read", new(), persist: false).ShouldExecute, "corrupt write journal blocked independently certified read");
            Assert(!journal.Begin("project", "unsafe-write", "Write", new()).ShouldExecute, "corrupt journal admitted write");
            return Task.FromResult<JsonObject>(new() { ["safeReadAdmitted"] = true, ["writeRefused"] = true });
        });
    }

    sealed class Storage
    {
        public readonly Dictionary<string, string> Values = new();
        public DateTime Utc = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        public bool FailWrites;
        public int WriteCount;
        public CommandRecoveryJournal Create() => new(
            key => Values.TryGetValue(key, out var value) ? value : "",
            (key, value) => { if (FailWrites) throw new IOException("injected storage failure"); Values[key] = value; WriteCount++; },
            () => Utc);
    }
}
