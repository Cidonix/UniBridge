#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class WorkSession
    {
        // Assets and their .meta are one restoration unit. Never leave a newly
        // orphaned author-edited companion after a failed per-file operation.
        static object[] ExecuteAddedRevertGroup(SessionState state, FileChange[] group)
        {
            return ExecuteAddedRevertGroupWithRecovery(state, group, null);
        }

        static object[] ExecuteAddedRevertGroupWithRecovery(SessionState state, FileChange[] group,
            List<AddedRevertGroupRecovery> recoveries)
        {
            if (group == null || group.Length == 0 || group.Any(change => change == null || change.ChangeType != "Added"))
                throw new InvalidOperationException("An added-file revert group must contain only added files.");
            if (group.Select(change => change.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Length)
                throw new InvalidOperationException("An added-file revert group cannot contain duplicate paths.");
            if (group.Length > 2 || (group.Length == 2 && !group.Any(change =>
                !change.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                group.Any(companion => string.Equals(companion.Path, change.Path + ".meta", StringComparison.OrdinalIgnoreCase)))))
                throw new InvalidOperationException("Only an added asset and its exact companion metadata can be grouped.");

            var entries = new List<AddedRevertEntry>();
            foreach (var change in group)
            {
                var reason = GetRevertBlocker(state, change);
                if (reason != null)
                    throw new InvalidOperationException(change.Path + ": " + reason);
                var receipt = state.OwnedWrites[change.Path];
                var recovery = Path.Combine(GetSessionDir(state.SessionId), "revert-recovery", Guid.NewGuid().ToString("N"));
                EnsureNoReparsePoints(recovery, ProjectRoot);
                entries.Add(new AddedRevertEntry
                {
                    Change = change,
                    Absolute = ToAbsoluteProjectPath(change.Path),
                    Recovery = recovery,
                    Receipt = receipt,
                    PriorAfterExists = receipt.AfterExists,
                    PriorAfterSha256 = receipt.AfterSha256
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entries[0].Recovery));
            var journalAttempted = false;
            try
            {
                foreach (var entry in entries)
                {
                    // Repeat each member's checks immediately before moving it. The
                    // moved bytes, not the earlier scan, are the final hash evidence.
                    var reason = GetRevertBlocker(state, entry.Change);
                    if (reason != null)
                        throw new InvalidOperationException(entry.Change.Path + ": " + reason);
                    AssertAddedCompanionPreconditions(entries);
                    File.Move(entry.Absolute, entry.Recovery);
                    entry.Moved = true;
                    if (!string.Equals(TryComputeSha256(entry.Recovery), entry.PriorAfterSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(entry.Change.Path + ": file changed during quarantine.");
                }

                AssertQuarantinedAddedGroup(entries);
                foreach (var entry in entries)
                {
                    entry.Receipt.AfterExists = false;
                    entry.Receipt.AfterSha256 = null;
                }
                journalAttempted = true;
                SaveState(state);
                recoveries?.Add(new AddedRevertGroupRecovery { Entries = entries });
                return entries.Select(entry => (object)new
                {
                    path = entry.Change.Path,
                    operation = "Quarantined owned added file as a verified asset/metadata group",
                    recoveryPath = ToProjectDisplayPath(entry.Recovery)
                }).ToArray();
            }
            catch (Exception failure)
            {
                throw CompensateAddedRevertGroup(state, entries, failure, journalAttempted);
            }
        }

        static void AssertAddedCompanionPreconditions(List<AddedRevertEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (!entry.Change.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    var companion = entry.Change.Path + ".meta";
                    if (entries.Any(member => string.Equals(member.Change.Path, companion, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var metadata = ReadWriteFileState(companion);
                    if (metadata.Exists || !string.IsNullOrEmpty(metadata.Error))
                        throw new InvalidOperationException(entry.Change.Path + ": unselected companion metadata appeared or cannot be verified; preserve it: " + companion);
                }
                else
                {
                    var asset = entry.Change.Path.Substring(0, entry.Change.Path.Length - 5);
                    if (entries.Any(member => string.Equals(member.Change.Path, asset, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var assetState = ReadWriteFileState(asset);
                    if (assetState.Exists || !string.IsNullOrEmpty(assetState.Error))
                        throw new InvalidOperationException(entry.Change.Path + ": an unselected asset/folder appeared or cannot be verified; preserve its metadata: " + asset);
                }
            }
        }

        static void AssertQuarantinedAddedGroup(List<AddedRevertEntry> entries)
        {
            foreach (var entry in entries)
            {
                var current = ReadWriteFileState(entry.Change.Path);
                if (current.Exists || !string.IsNullOrEmpty(current.Error))
                    throw new InvalidOperationException(entry.Change.Path + ": original path was replaced or could not be verified after quarantine.");
            }
            AssertAddedCompanionPreconditions(entries);
        }

        static Exception CompensateAddedRevertGroup(SessionState state, List<AddedRevertEntry> entries,
            Exception failure, bool journalAttempted)
        {
            var compensationErrors = new List<string>();
            foreach (var entry in entries.AsEnumerable().Reverse())
            {
                entry.Receipt.AfterExists = entry.PriorAfterExists;
                entry.Receipt.AfterSha256 = entry.PriorAfterSha256;
                if (!entry.Moved)
                    continue;
                try
                {
                    EnsureNoReparsePoints(entry.Absolute, ProjectRoot);
                    // Non-overwriting moves preserve any independently recreated file.
                    File.Move(entry.Recovery, entry.Absolute);
                    entry.Moved = false;
                }
                catch (Exception ex)
                {
                    compensationErrors.Add(entry.Change.Path + ": " + ex.Message + "; retained recovery bytes at " + entry.Recovery);
                }
            }
            if (journalAttempted)
            {
                try { SaveState(state); }
                catch (Exception ex) { compensationErrors.Add("Receipt compensation could not be persisted: " + ex.Message); }
            }
            if (compensationErrors.Count > 0)
                return new RevertMutationException("Added asset/metadata revert stopped; restoration was partial or cannot be verified. " +
                    failure.Message + " " + string.Join(" ", compensationErrors));
            return new InvalidOperationException("Added asset/metadata revert failed; all moved files were returned without overwriting other changes. " + failure.Message, failure);
        }

        // Recheck successful earlier groups after later Unity/tool callbacks, before
        // allowing a refresh to observe an independently created orphan companion.
        static void ValidateAddedRevertGroupsBeforeRefresh(SessionState state, List<AddedRevertGroupRecovery> recoveries)
        {
            var failures = new List<string>();
            var partial = false;
            foreach (var recovery in recoveries ?? new List<AddedRevertGroupRecovery>())
            {
                if (recovery.Compensated)
                    continue;
                try { AssertQuarantinedAddedGroup(recovery.Entries); }
                catch (Exception failure)
                {
                    recovery.Compensated = true;
                    var result = CompensateAddedRevertGroup(state, recovery.Entries, failure, journalAttempted: true);
                    failures.Add(result.Message);
                    partial |= result is RevertMutationException;
                }
            }
            if (failures.Count > 0)
            {
                var message = string.Join(" ", failures);
                if (partial) throw new RevertMutationException(message);
                throw new InvalidOperationException(message);
            }
        }

        sealed class AddedRevertGroupRecovery
        {
            public List<AddedRevertEntry> Entries;
            public bool Compensated;
            public string[] CompensatedPaths => Compensated
                ? Entries.Where(entry => !entry.Moved).Select(entry => entry.Change.Path).ToArray()
                : Array.Empty<string>();
        }

        sealed class AddedRevertEntry
        {
            public FileChange Change;
            public string Absolute;
            public string Recovery;
            public OwnedWriteReceipt Receipt;
            public bool PriorAfterExists;
            public string PriorAfterSha256;
            public bool Moved;
        }
    }
}
