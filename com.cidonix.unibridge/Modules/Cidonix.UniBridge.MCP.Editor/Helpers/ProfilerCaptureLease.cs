using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal sealed class ProfilerCaptureState
    {
        public bool enabled, profileEditor, allocationCallstacks;
        public int memoryRecordMode;
        public bool SameAs(ProfilerCaptureState other) => other != null && enabled == other.enabled && profileEditor == other.profileEditor &&
            allocationCallstacks == other.allocationCallstacks && memoryRecordMode == other.memoryRecordMode;
    }
    internal interface IProfilerCaptureBackend
    {
        ProfilerCaptureState ReadState();
        void Pause();
        void ApplyState(ProfilerCaptureState state);
        bool HasFrames { get; }
        bool Save(string path);
        bool Load(string path);
        void Clear();
    }
    internal sealed class ProfilerCaptureReceipt
    {
        public string schema = "unibridge.runtimeProfiler.captureLease.v1", leaseId, state, projectRoot, capturePath;
        public int editorPid;
        public string captureSha256, originalSha256, originalLoadedSha256, loadedSha256;
        public bool hadOriginalFrames, restorationVerified;
        public ProfilerCaptureState originalState, loadedState;
        public string errorCode;
        public string[] issues = Array.Empty<string>();
    }
    internal sealed class ProfilerCaptureLeaseResult
    {
        public string schema = "unibridge.runtimeProfiler.captureLease.v1", outcome = "blocked", leaseId, state = "none", projectRoot;
        public int editorPid;
        public string capturePath, captureSha256, originalSha256, originalLoadedSha256, loadedSha256;
        public bool hadOriginalFrames, restorationVerified, recordingEnabled, profileEditor, allocationCallstacks;
        public int memoryRecordMode;
        public string receiptPath, errorCode;
        public string[] issues = Array.Empty<string>();
    }
    // Synchronous transaction: no yielded native views and no replay. Only owned files are written.
    // The pointer and receipt are durable, so an explicit Restore survives same-process domain reload.
    internal sealed class ProfilerCaptureLease
    {
        const long MaxCaptureBytes = 256L * 1024 * 1024;
        readonly string root, storage;
        readonly int pid;
        readonly IProfilerCaptureBackend backend;
        string Active => Path.Combine(storage, "active.json");
        public ProfilerCaptureLease(string root, int pid, IProfilerCaptureBackend backend)
        { this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); this.pid = pid; this.backend = backend; storage = Path.Combine(this.root, "Library", "UniBridge", "ProfilerCaptures"); }
        public ProfilerCaptureLeaseResult Execute(string operation, string expectedRoot, string capturePath, string sha, string leaseId, Action cancelled = null)
        {
            try
            {
                if (operation != "status" && !string.Equals(Path.GetFullPath(expectedRoot ?? "" ).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                    return Failure(null, "PROJECT_ROOT_MISMATCH");
                cancelled?.Invoke();
                GuardPath(storage, false);
                Directory.CreateDirectory(storage);
                using var locked = new FileStream(Path.Combine(storage, "transaction.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var active = ReadActive();
                if (operation == "status") return Result(active, "completed");
                if (operation == "restore")
                {
                    if (active == null || !string.Equals(leaseId, active.leaseId, StringComparison.Ordinal)) return Failure(active, "CAPTURE_LEASE_MISMATCH");
                    if (active.editorPid != pid || !string.Equals(active.projectRoot, root, StringComparison.OrdinalIgnoreCase)) return Failure(active, "CAPTURE_LEASE_EDITOR_MISMATCH");
                    if (active.state != "loaded") return Failure(active, "CAPTURE_LEASE_RECOVERY_REQUIRED");
                    if (!backend.ReadState().SameAs(active.loadedState)) return Failure(active, "CAPTURE_STATE_CHANGED");
                    var dir = LeaseDirectory(active.leaseId);
                    if (Fingerprint(dir) != active.loadedSha256) return Failure(active, "CAPTURE_BUFFER_CHANGED");
                    cancelled?.Invoke();
                    active.state = "restoring"; active.errorCode = null; active.issues = Array.Empty<string>(); WriteReceipt(active);
                    try
                    {
                        RestoreOriginal(active);
                        active.state = "restored"; active.restorationVerified = true; WriteReceipt(active); ClearPointer(active);
                        return Result(active, "completed");
                    }
                    catch (Exception error)
                    {
                        // Failed restoration may itself replace a native buffer. Put the unchanged lease capture back.
                        bool rolledBack = false;
                        try { backend.Pause(); rolledBack = backend.Load(Path.Combine(dir, "requested.data")) && Fingerprint(dir) == active.loadedSha256; if (rolledBack) backend.ApplyState(active.loadedState); }
                        catch { }
                        active.state = rolledBack ? "loaded" : "restoreFailed";
                        active.errorCode = "CAPTURE_RESTORE_FAILED"; active.issues = new[] { Bounded(error.Message), rolledBack ? "Loaded lease buffer restored after failed baseline restoration." : "Native buffer outcome is uncertain; automatic replacement is blocked." };
                        WriteReceipt(active); return Result(active, "failed");
                    }
                }
                if (operation != "load") return Failure(active, "INVALID_CAPTURE_OPERATION");
                if (active != null) return Failure(active, "CAPTURE_LEASE_ACTIVE");
                var source = RequestedPath(capturePath);
                if (sha == null || sha.Length != 64 || !IsHex(sha)) return Failure(null, "CAPTURE_HASH_REQUIRED");
                var receipt = new ProfilerCaptureReceipt { leaseId = Guid.NewGuid().ToString("N"), state = "preparing", projectRoot = root, editorPid = pid,
                    capturePath = capturePath.Replace('\\', '/'), captureSha256 = sha.ToLowerInvariant(), originalState = backend.ReadState() };
                var folder = LeaseDirectory(receipt.leaseId); Directory.CreateDirectory(folder);
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length == 0 || input.Length > MaxCaptureBytes) return Failure(receipt, "CAPTURE_SIZE_OUT_OF_RANGE");
                    using var output = new FileStream(Path.Combine(folder, "requested.data"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output); output.Flush(true);
                }
                if (Hash(Path.Combine(folder, "requested.data")) != receipt.captureSha256) return Failure(receipt, "CAPTURE_HASH_MISMATCH");
                cancelled?.Invoke();
                bool nativeAttempted = false;
                try
                {
                    backend.Pause();
                    receipt.hadOriginalFrames = backend.HasFrames;
                    if (receipt.hadOriginalFrames)
                    {
                        string original = Path.Combine(folder, "original.data");
                        if (!backend.Save(original) || !File.Exists(original)) throw new IOException("Original Profiler buffer could not be preserved.");
                        receipt.originalSha256 = Hash(original);
                    }
                    // Native LoadProfile rewrites the saved .data representation. Preserve exact backup bytes,
                    // but qualify the loaded original buffer's stable serialized representation separately.
                    receipt.state = "qualifyingOriginal"; WriteReceipt(receipt); WritePointer(receipt);
                    if (receipt.hadOriginalFrames)
                    {
                        nativeAttempted = true;
                        if (!backend.Load(Path.Combine(folder, "original.data")) || !backend.HasFrames)
                            throw new IOException("Original capture preflight could not be loaded.");
                        receipt.originalLoadedSha256 = StableFingerprint(folder);
                        WriteReceipt(receipt);
                    }
                    receipt.state = "loading"; WriteReceipt(receipt);
                    nativeAttempted = true;
                    if (!backend.Load(Path.Combine(folder, "requested.data")) || !backend.HasFrames) throw new IOException("Requested capture did not produce recorded frames.");
                    receipt.loadedState = backend.ReadState();
                    if (receipt.loadedState.enabled) throw new IOException("Loaded capture unexpectedly enabled recording.");
                    receipt.loadedSha256 = StableFingerprint(folder);
                    receipt.state = "loaded"; WriteReceipt(receipt); return Result(receipt, "completed");
                }
                catch (Exception error)
                {
                    bool restored = false;
                    try { if (nativeAttempted) RestoreOriginal(receipt); else backend.ApplyState(receipt.originalState); restored = backend.ReadState().SameAs(receipt.originalState); }
                    catch (Exception restoreError) { receipt.issues = new[] { Bounded(error.Message), Bounded(restoreError.Message) }; }
                    receipt.state = restored ? "loadFailedRestored" : "loadFailed"; receipt.restorationVerified = restored;
                    receipt.errorCode = restored ? "CAPTURE_LOAD_FAILED_RESTORED" : "CAPTURE_LOAD_FAILED_RESTORE_FAILED";
                    if (receipt.issues.Length == 0) receipt.issues = new[] { Bounded(error.Message) };
                    WriteReceipt(receipt); if (restored) ClearPointer(receipt);
                    return Result(receipt, "failed");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { var result = Failure(null, "CAPTURE_TRANSACTION_FAILED"); result.issues = new[] { Bounded(error.Message) }; return result; }
        }
        void RestoreOriginal(ProfilerCaptureReceipt receipt)
        {
            backend.Pause(); var folder = LeaseDirectory(receipt.leaseId);
            if (receipt.hadOriginalFrames)
            {
                var path = Path.Combine(folder, "original.data");
                if (!File.Exists(path) || Hash(path) != receipt.originalSha256) throw new IOException("Original capture backup is missing or changed.");
                if (!backend.Load(path) || !backend.HasFrames) throw new IOException("Original capture restoration could not be loaded.");
                var restored = StableFingerprint(folder);
                if (receipt.originalLoadedSha256 == null)
                    receipt.originalLoadedSha256 = restored; // Only successful loading of the integrity-checked original can qualify a previously interrupted preflight.
                else if (restored != receipt.originalLoadedSha256)
                    throw new IOException("Original capture restoration could not be verified against its qualified loaded representation.");
            }
            else { backend.Clear(); if (backend.HasFrames) throw new IOException("Original empty buffer restoration failed."); }
            backend.ApplyState(receipt.originalState);
            if (!backend.ReadState().SameAs(receipt.originalState)) throw new IOException("Original Profiler flags were not restored.");
        }
        string StableFingerprint(string folder)
        {
            var first = Fingerprint(folder);
            if (Fingerprint(folder) != first) throw new IOException("Stopped native capture serialization is not stable; restoration cannot be certified.");
            return first;
        }
        string Fingerprint(string folder)
        {
            if (backend.ReadState().enabled) throw new IOException("Recording is active; buffer comparison would be unstable.");
            string path = Path.Combine(folder, "probe-" + Guid.NewGuid().ToString("N") + ".data");
            if (!backend.Save(path) || !File.Exists(path)) throw new IOException("Active Profiler buffer could not be fingerprinted.");
            var result = Hash(path); File.Delete(path); return result;
        }
        ProfilerCaptureReceipt ReadActive()
        {
            if (!File.Exists(Active)) return null;
            GuardPath(Active, true);
            if (new FileInfo(Active).Length > 1024) throw new IOException("Invalid lease pointer.");
            var id = File.ReadAllText(Active).Trim(); var path = Path.Combine(LeaseDirectory(id), "receipt.json");
            GuardPath(path, true);
            if (new FileInfo(path).Length > 16384) throw new IOException("Invalid capture receipt.");
            var result = McpJson.DeserializeObject<ProfilerCaptureReceipt>(File.ReadAllText(path));
            if (result == null || result.leaseId != id || result.schema != "unibridge.runtimeProfiler.captureLease.v1") throw new IOException("Capture receipt identity mismatch.");
            return result;
        }
        void WriteReceipt(ProfilerCaptureReceipt receipt) => Atomic(Path.Combine(LeaseDirectory(receipt.leaseId), "receipt.json"), McpJson.SerializeObject(receipt, Formatting.Indented));
        void WritePointer(ProfilerCaptureReceipt receipt)
        { using var stream = new FileStream(Active, FileMode.CreateNew, FileAccess.Write, FileShare.None); var bytes = System.Text.Encoding.UTF8.GetBytes(receipt.leaseId); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        void ClearPointer(ProfilerCaptureReceipt receipt) { if (File.Exists(Active) && File.ReadAllText(Active).Trim() == receipt.leaseId) File.Delete(Active); }
        static void Atomic(string path, string json)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".writing";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = System.Text.Encoding.UTF8.GetBytes(json); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        string LeaseDirectory(string id)
        { if (id == null || id.Length != 32 || !IsHex(id)) throw new IOException("Invalid capture lease ID."); var dir = Path.Combine(storage, id); GuardPath(dir, false); return dir; }
        string RequestedPath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || !relative.EndsWith(".data", StringComparison.OrdinalIgnoreCase)) throw new IOException("Capture must be a project-relative .data file.");
            var path = Path.GetFullPath(Path.Combine(root, relative)); GuardPath(path, true); return path;
        }
        void GuardPath(string path, bool mustExist)
        {
            path = Path.GetFullPath(path);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path leaves the expected project root.");
            var cursor = path;
            while (cursor.Length > root.Length)
            {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Capture paths may not traverse reparse points.");
                cursor = Path.GetDirectoryName(cursor);
            }
            if (mustExist && !File.Exists(path)) throw new FileNotFoundException("Requested capture does not exist.");
        }
        static bool IsHex(string value) { foreach (char c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F')) return false; return true; }
        internal static string Hash(string path) { using var algorithm = SHA256.Create(); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        static string Bounded(string value) => value == null ? "Unknown failure." : value.Substring(0, Math.Min(value.Length, 512));
        ProfilerCaptureLeaseResult Failure(ProfilerCaptureReceipt receipt, string code) { var result = Result(receipt, "blocked"); result.errorCode = code; return result; }
        ProfilerCaptureLeaseResult Result(ProfilerCaptureReceipt receipt, string outcome)
        {
            var state = backend.ReadState();
            return new ProfilerCaptureLeaseResult { outcome = outcome, leaseId = receipt?.leaseId, state = receipt?.state ?? "none", projectRoot = root, editorPid = pid,
                capturePath = receipt?.capturePath, captureSha256 = receipt?.captureSha256, originalSha256 = receipt?.originalSha256, originalLoadedSha256 = receipt?.originalLoadedSha256, loadedSha256 = receipt?.loadedSha256,
                hadOriginalFrames = receipt?.hadOriginalFrames ?? false, restorationVerified = receipt?.restorationVerified ?? false, recordingEnabled = state.enabled,
                profileEditor = state.profileEditor, allocationCallstacks = state.allocationCallstacks, memoryRecordMode = state.memoryRecordMode,
                receiptPath = receipt == null ? null : Path.Combine(LeaseDirectory(receipt.leaseId), "receipt.json"), errorCode = receipt?.errorCode, issues = receipt?.issues ?? Array.Empty<string>() };
        }
    }
}
