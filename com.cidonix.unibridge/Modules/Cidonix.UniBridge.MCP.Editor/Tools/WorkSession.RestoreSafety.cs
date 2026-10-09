#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class WorkSession
    {
        static SessionState ReadSessionState(string path)
        {
            var state = McpJson.DeserializeObject<SessionState>(File.ReadAllText(path));
            if (state == null || !string.Equals(Path.GetFullPath(state.ProjectRoot ?? ""), ProjectRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Work session belongs to a different project.");
            state.Files = new Dictionary<string, FileSnapshot>(state.Files ?? new Dictionary<string, FileSnapshot>(), StringComparer.OrdinalIgnoreCase);
            state.OwnedWrites = new Dictionary<string, OwnedWriteReceipt>(state.OwnedWrites ?? new Dictionary<string, OwnedWriteReceipt>(), StringComparer.OrdinalIgnoreCase);
            return state;
        }

        static void EnsureNoReparsePoints(string path, string boundary)
        {
            var root = Path.GetFullPath(boundary).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(path);
            if (!current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Path is outside the intended project/session boundary.");
            while (!string.IsNullOrWhiteSpace(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Reparse-point paths cannot be restored or captured safely.");
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(current);
            }
        }

        static string HashStream(Stream stream)
        {
            stream.Position = 0;
            using var sha = SHA256.Create();
            var hash = BytesToHex(sha.ComputeHash(stream));
            stream.Position = 0;
            return hash;
        }

        static string GetRevertBlocker(SessionState state, FileChange change)
        {
            try
            {
                if (state.Version < 2 || state.Baseline?.ScanComplete != true)
                    return "A complete guarded session baseline is required; legacy sessions remain review-only.";
                if (!state.OwnedWrites.TryGetValue(change.Path, out var owned))
                    return "No explicit owned-write receipt; filesystem differences do not prove authorship.";
                if (owned.Conflict)
                    return "Owned-write chain is conflicted: " + owned.Reason;
                var absolute = ToAbsoluteProjectPath(change.Path);
                EnsureNoReparsePoints(absolute, ProjectRoot);
                if (Directory.Exists(absolute)) return "A directory replaced the selected file.";
                if (File.Exists(absolute) != owned.AfterExists)
                    return "Existence changed after the last verified owned write.";
                if (owned.AfterExists)
                {
                    var current = TryComputeSha256(absolute);
                    if (string.IsNullOrWhiteSpace(owned.AfterSha256) || !string.Equals(current, owned.AfterSha256, StringComparison.OrdinalIgnoreCase))
                        return "File was changed or cannot be verified after the last owned write.";
                }
                var loadedBlocker = GetLoadedRestoreBlocker(change.Path);
                if (loadedBlocker != null) return loadedBlocker;
                if (change.ChangeType != "Added")
                {
                    if (!state.Files.TryGetValue(change.Path, out var snapshot) || !snapshot.Captured || !CaptureFileExists(state, snapshot))
                        return "Captured baseline bytes are unavailable.";
                    using var capture = new FileStream(GetAbsoluteCapturePath(state, snapshot), FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (string.IsNullOrWhiteSpace(snapshot.Sha256) || !string.Equals(HashStream(capture), snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                        return "Captured baseline bytes do not match their original fingerprint.";
                }
                return null;
            }
            catch (Exception ex) { return "Restore precondition cannot be verified: " + ex.Message; }
        }

        static string PreviewFingerprint(SessionState state, RevertPlan[] plan, string[] selected, bool all, bool metadata)
        {
            var text = McpJson.SerializeObject(new
            {
                state.SessionId, state.Version, all, metadata,
                selected = selected.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                plan = plan.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(item => new
                {
                    item.Path, item.ChangeType, item.ExpectedExists, item.ExpectedSha256, item.BaselineSha256, item.CapturePath,
                    receipt = state.OwnedWrites[item.Path]
                }).ToArray()
            });
            using var sha = SHA256.Create();
            return BytesToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        static string PreviewPath(SessionState state, string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("A valid one-use PlanId from a successful preview is required.");
            var path = Path.Combine(GetSessionDir(state.SessionId), "revert-plans", id + ".json");
            EnsureNoReparsePoints(path, ProjectRoot);
            return path;
        }

        static string SaveRevertPreview(SessionState state, RevertPlan[] plan, string[] selected, bool all, bool metadata)
        {
            var id = Guid.NewGuid().ToString("N");
            var path = PreviewPath(state, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, McpJson.SerializeObject(new RevertPreview
            {
                Fingerprint = PreviewFingerprint(state, plan, selected, all, metadata),
                CreatedUtc = DateTime.UtcNow.ToString("o")
            }, Formatting.Indented));
            return id;
        }

        static void ValidateAndConsumeRevertPreview(SessionState state, RevertPlan[] plan, string[] selected, bool all, bool metadata, string id, List<string> blockers)
        {
            try
            {
                using var stream = new FileStream(PreviewPath(state, id), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                string json;
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true)) json = reader.ReadToEnd();
                var preview = McpJson.DeserializeObject<RevertPreview>(json);
                if (preview == null || preview.Consumed || !string.Equals(preview.Fingerprint, PreviewFingerprint(state, plan, selected, all, metadata), StringComparison.Ordinal))
                {
                    blockers.Add("The preview was consumed or the selection/files/ownership changed. Build a fresh dry-run.");
                    return;
                }
                // Consume BEFORE touching project files. Failed/partial restores require
                // a fresh review; neither retries nor domain reload can reuse the plan.
                preview.Consumed = true;
                var bytes = Encoding.UTF8.GetBytes(McpJson.SerializeObject(preview, Formatting.Indented));
                stream.Position = 0;
                stream.Write(bytes, 0, bytes.Length);
                stream.SetLength(bytes.Length);
                stream.Flush(true);
            }
            catch (Exception ex) { blockers.Add("Cannot admit revert: " + ex.Message); }
        }

        static object ExecuteGuardedRevert(SessionState state, FileChange change)
        {
            var reason = GetRevertBlocker(state, change);
            if (reason != null) throw new InvalidOperationException(reason);
            var absolute = ToAbsoluteProjectPath(change.Path);
            var owned = state.OwnedWrites[change.Path];
            var recovery = Path.Combine(GetSessionDir(state.SessionId), "revert-recovery", Guid.NewGuid().ToString("N"));
            EnsureNoReparsePoints(recovery, ProjectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(recovery));
            if (change.ChangeType == "Added")
                throw new InvalidOperationException("Added assets must use the guarded asset/metadata group operation.");

            var snapshot = state.Files[change.Path];
            using var capture = new FileStream(GetAbsoluteCapturePath(state, snapshot), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(HashStream(capture), snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Captured bytes changed before restore.");
            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            // CreateNew refuses a concurrent author file on a formerly deleted path.
            // Existing files are hashed and restored through the SAME exclusive handle.
            using var target = new FileStream(absolute, owned.AfterExists ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            if (owned.AfterExists && !string.Equals(HashStream(target), owned.AfterSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File changed before the exclusive restore handle was acquired.");
            var touched = false;
            var previousHash = owned.AfterSha256;
            try
            {
                using (var backup = new FileStream(recovery, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    target.CopyTo(backup);
                    backup.Flush(true);
                }
                target.Position = 0;
                touched = true;
                capture.CopyTo(target);
                target.SetLength(capture.Length);
                target.Flush(true);
                if (!string.Equals(HashStream(target), snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Restored bytes do not match baseline.");
                owned.AfterExists = true;
                owned.AfterSha256 = snapshot.Sha256;
                SaveState(state);
            }
            catch (Exception ex)
            {
                // Keep pre-restore bytes for recovery and report partial truthfully.
                // The exclusive handle prevents compensation overwriting a later edit.
                if (touched && change.ChangeType == "Modified")
                {
                    try
                    {
                        using var backup = File.OpenRead(recovery);
                        target.Position = 0;
                        backup.CopyTo(target);
                        target.SetLength(backup.Length);
                        target.Flush(true);
                        owned.AfterExists = true;
                        owned.AfterSha256 = previousHash;
                        throw new InvalidOperationException("Restore failed; previous owned bytes were recovered. " + ex.Message);
                    }
                    catch (InvalidOperationException) { throw; }
                    catch (Exception compensation) { throw new RevertMutationException("Restore and recovery failed; retained backup " + recovery + ": " + compensation.Message); }
                }
                if (!touched && change.ChangeType == "Modified") throw;
                throw new RevertMutationException("Restore failed after creating/restoring the target; retained backup " + recovery + ": " + ex.Message);
            }
            return new { path = change.Path, operation = "Restored verified baseline", bytes = capture.Length, recoveryPath = ToProjectDisplayPath(recovery) };
        }

        sealed class RevertPreview
        {
            public string Fingerprint;
            public string CreatedUtc;
            public bool Consumed;
        }

        sealed class RevertMutationException : IOException
        {
            public RevertMutationException(string message) : base(message) { }
        }
    }
}
