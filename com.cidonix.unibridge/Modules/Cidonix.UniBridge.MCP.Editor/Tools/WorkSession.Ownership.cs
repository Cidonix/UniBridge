#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class WorkSession
    {
        // File differences are review evidence, never evidence of ownership. Only an
        // explicit writer's before-state and expected payload can authorize a revert.
        public sealed class WriteTrackingToken
        {
            public string SessionId;
            public string TokenId;
            public string Source;
            public string StartedUtc;
            public bool Completed;
            public Dictionary<string, WriteFileState> Before = new(StringComparer.OrdinalIgnoreCase);
        }

        public sealed class WriteFileState
        {
            public bool Exists;
            public string Sha256;
            public string Error;
        }

        public sealed class WriteTrackingResult
        {
            public bool Succeeded;
            public int RecordedCount;
            public int ConflictCount;
            public string[] Issues = Array.Empty<string>();
        }

        sealed class OwnedWriteReceipt
        {
            public bool BeforeExists;
            public string BeforeSha256;
            public bool AfterExists;
            public string AfterSha256;
            public string OperationId;
            public string Source;
            public bool Conflict;
            public string Reason;
            public string RecordedUtc;
        }

        public static string ComputeWriteSha256(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            using var sha = SHA256.Create();
            return BytesToHex(sha.ComputeHash(payload));
        }

        public static WriteTrackingToken BeginWrite(string[] paths, string source)
        {
            var sessionId = GetActiveSessionId();
            if (string.IsNullOrWhiteSpace(sessionId))
                return null;

            var state = LoadStateById(sessionId);
            AssertWriteSession(state);
            var token = new WriteTrackingToken
            {
                SessionId = state.SessionId,
                TokenId = Guid.NewGuid().ToString("N"),
                Source = string.IsNullOrWhiteSpace(source) ? "explicit_write" : source,
                StartedUtc = DateTime.UtcNow.ToString("o")
            };

            foreach (var suppliedPath in paths ?? Array.Empty<string>())
            {
                var path = CanonicalWritePath(suppliedPath);
                token.Before[path] = ReadWriteFileState(path);
            }
            if (token.Before.Count == 0)
                throw new InvalidOperationException("Write tracking requires at least one explicit file path.");

            // Publish the intent before the actual write. A reload or crash without a
            // completed expected-payload receipt leaves the changed file unowned.
            SaveWriteToken(token);
            return token;
        }

        public static WriteTrackingResult CompleteWrite(WriteTrackingToken token, IDictionary<string, string> expectedAfterHashes)
        {
            return CompleteWriteCore(token, expectedAfterHashes, skipUnchangedUnverified: false);
        }

        static WriteTrackingResult CompleteWriteCore(WriteTrackingToken token, IDictionary<string, string> expectedAfterHashes,
            bool skipUnchangedUnverified)
        {
            if (token == null)
                return new WriteTrackingResult { Succeeded = true };

            var persisted = LoadWriteToken(token.SessionId, token.TokenId);
            if (persisted.Completed)
                throw new InvalidOperationException("This write-tracking token has already been completed.");
            var state = LoadStateById(persisted.SessionId);
            AssertWriteSession(state);
            state.OwnedWrites ??= new Dictionary<string, OwnedWriteReceipt>(StringComparer.OrdinalIgnoreCase);
            state.OwnedWrites = new Dictionary<string, OwnedWriteReceipt>(state.OwnedWrites, StringComparer.OrdinalIgnoreCase);

            // The state commit and token consumption use separate atomic files. A
            // crash between them must not re-evaluate the original BEFORE against
            // the already committed AFTER and poison valid ownership evidence.
            // Refuse this replay without claiming or rewriting any current bytes.
            if (persisted.Before.Keys.Any(path => state.OwnedWrites.TryGetValue(path, out var receipt) &&
                string.Equals(receipt.OperationId, persisted.TokenId, StringComparison.Ordinal)))
                throw new InvalidOperationException("WRITE_TRACKING_ALREADY_COMMITTED: ownership evidence for this token was already persisted; completion replay was refused without changing its receipts.");

            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in expectedAfterHashes ?? new Dictionary<string, string>())
            {
                var path = CanonicalWritePath(entry.Key);
                if (!persisted.Before.ContainsKey(path))
                    throw new InvalidOperationException($"Path was not declared when write tracking began: {path}");
                if (entry.Value != null && !IsWriteSha256(entry.Value))
                    throw new InvalidOperationException($"Expected after hash must be a SHA-256 value or null for deletion: {path}");
                expected[path] = entry.Value?.ToLowerInvariant();
            }

            var issues = new List<string>();
            var result = new WriteTrackingResult();
            foreach (var entry in persisted.Before)
            {
                var path = entry.Key;
                var before = entry.Value;
                var after = ReadWriteFileState(path);
                state.OwnedWrites.TryGetValue(path, out var prior);
                // An unverified serializer scope commonly declares an unchanged .meta
                // alongside its changed asset. Do not poison or claim that companion.
                if (skipUnchangedUnverified && !expected.ContainsKey(path) && before != null && string.IsNullOrEmpty(before.Error) &&
                    string.IsNullOrEmpty(after.Error) && SameWriteState(before.Exists, before.Sha256, after.Exists, after.Sha256))
                    continue;
                var reason = GetWriteConflictReason(state, path, before, after, prior, expected);
                var receipt = new OwnedWriteReceipt
                {
                    BeforeExists = before.Exists,
                    BeforeSha256 = before.Sha256,
                    AfterExists = after.Exists,
                    AfterSha256 = after.Sha256,
                    OperationId = persisted.TokenId,
                    Source = persisted.Source,
                    Conflict = reason != null,
                    Reason = reason,
                    RecordedUtc = DateTime.UtcNow.ToString("o")
                };
                state.OwnedWrites[path] = receipt;
                if (receipt.Conflict)
                {
                    result.ConflictCount++;
                    issues.Add(path + ": " + reason);
                }
                else
                {
                    result.RecordedCount++;
                }
            }

            // Save the receipt before marking the token consumed, so no successful
            // acknowledgement can escape without persisted ownership evidence.
            SaveState(state);
            persisted.Completed = true;
            SaveWriteToken(persisted);
            token.Completed = true;
            result.Succeeded = result.ConflictCount == 0;
            result.Issues = issues.ToArray();
            return result;
        }

        // Unity serializes scenes and assets through callbacks, including preexisting
        // dirty author state. A post-save readback alone cannot prove its ownership.
        // Such writes are deliberately reviewable but ineligible for automatic revert.
        public static WriteTrackingResult CompleteUnverifiedWrite(WriteTrackingToken token)
        {
            return CompleteWriteCore(token, new Dictionary<string, string>(), skipUnchangedUnverified: true);
        }

        static string GetWriteConflictReason(SessionState state, string path, WriteFileState before,
            WriteFileState after, OwnedWriteReceipt prior, Dictionary<string, string> expected)
        {
            if (prior?.Conflict == true)
                return prior.Reason ?? "prior_ownership_conflict";
            if (before == null || !string.IsNullOrEmpty(before.Error) || !string.IsNullOrEmpty(after.Error))
                return "file_state_unreadable";
            if (before.Exists && !IsWriteSha256(before.Sha256))
                return "before_hash_unavailable";

            if (prior != null)
            {
                if (!SameWriteState(before.Exists, before.Sha256, prior.AfterExists, prior.AfterSha256))
                    return "external_change_before_write";
            }
            else
            {
                state.Files.TryGetValue(path, out var baseline);
                if (!SameWriteState(before.Exists, before.Sha256, baseline?.Exists == true, baseline?.Sha256))
                    return "write_started_after_external_change";
            }

            if (!expected.TryGetValue(path, out var afterHash))
                return "expected_payload_unavailable";
            if (!SameWriteState(after.Exists, after.Sha256, afterHash != null, afterHash))
                return "expected_payload_mismatch";
            return null;
        }

        static bool SameWriteState(bool leftExists, string leftHash, bool rightExists, string rightHash)
        {
            return leftExists == rightExists && (!leftExists ||
                (IsWriteSha256(leftHash) && IsWriteSha256(rightHash) && string.Equals(leftHash, rightHash, StringComparison.OrdinalIgnoreCase)));
        }

        static bool IsWriteSha256(string hash)
        {
            return hash != null && hash.Length == 64 && hash.All(c =>
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }

        static WriteFileState ReadWriteFileState(string path)
        {
            try
            {
                var absolute = ToAbsoluteProjectPath(path);
                AssertNoWriteReparsePoints(absolute);
                if (Directory.Exists(absolute))
                    return new WriteFileState { Error = "path_is_directory" };
                try
                {
                    // Opening rather than File.Exists distinguishes absence from denied
                    // access, which must never be mistaken for a successfully deleted file.
                    using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var sha = SHA256.Create();
                    return new WriteFileState { Exists = true, Sha256 = BytesToHex(sha.ComputeHash(stream)) };
                }
                catch (FileNotFoundException) { return new WriteFileState(); }
                catch (DirectoryNotFoundException) { return new WriteFileState(); }
            }
            catch (Exception ex)
            {
                return new WriteFileState { Error = ex.GetType().Name + ": " + ex.Message };
            }
        }

        static string CanonicalWritePath(string supplied)
        {
            if (string.IsNullOrWhiteSpace(supplied))
                throw new InvalidOperationException("Write tracking path cannot be empty.");
            var absolute = ToAbsoluteProjectPath(supplied);
            AssertNoWriteReparsePoints(absolute);
            var path = ToProjectRelativePath(absolute);
            if (string.IsNullOrEmpty(path) || path.StartsWith("Library/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Write tracking must name a project file outside Library.");
            return path;
        }

        static void AssertNoWriteReparsePoints(string absolute)
        {
            var root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = absolute;
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Write tracking refuses reparse-point paths.");
                if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                    break;
                current = Path.GetDirectoryName(current);
            }
        }

        static void AssertWriteSession(SessionState state)
        {
            if (state == null || !string.IsNullOrEmpty(state.EndedUtc))
                throw new InvalidOperationException("Write tracking requires a non-ended work session.");
            if (!string.Equals(Path.GetFullPath(state.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Write tracking session belongs to another project.");
        }

        static string GetWriteTokenPath(string sessionId, string tokenId)
        {
            if (!Guid.TryParseExact(tokenId, "N", out _))
                throw new InvalidOperationException("Invalid write-tracking token id.");
            return Path.Combine(GetSessionDir(sessionId), "write-tracking", tokenId + ".json");
        }

        static WriteTrackingToken LoadWriteToken(string sessionId, string tokenId)
        {
            var token = McpJson.DeserializeObject<WriteTrackingToken>(File.ReadAllText(GetWriteTokenPath(sessionId, tokenId)));
            if (token == null || token.Before == null || token.Before.Count == 0 ||
                !string.Equals(token.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(token.TokenId, tokenId, StringComparison.Ordinal))
                throw new InvalidOperationException("Write-tracking token is invalid.");
            token.Before = new Dictionary<string, WriteFileState>(token.Before, StringComparer.OrdinalIgnoreCase);
            return token;
        }

        static void SaveWriteToken(WriteTrackingToken token)
        {
            var path = GetWriteTokenPath(token.SessionId, token.TokenId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, McpJson.SerializeObject(token, Formatting.Indented));
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        static object BeginWriteTracking(JObject parameters)
        {
            var requested = GetString(parameters, "SessionId", "sessionId", "session_id");
            if (!string.IsNullOrWhiteSpace(requested) && !string.Equals(requested, GetActiveSessionId(), StringComparison.Ordinal))
                return Response.Error("BeginWrite requires the active work session.");
            var token = BeginWrite(ReadPaths(parameters), GetString(parameters, "Source", "source") ?? "external_two_phase_write");
            if (token == null)
                return Response.Error("BeginWrite requires an active work session.");
            return Response.Success("Captured explicit file write preconditions.", new
            {
                action = "BeginWrite",
                sessionId = token.SessionId,
                tokenId = token.TokenId,
                before = token.Before,
                note = "Honor these before-state preconditions when writing. CompleteWrite must provide hashes computed from the known intended payload; null means deletion. This token does not claim current project differences."
            });
        }

        static object CompleteWriteTracking(JObject parameters)
        {
            var state = LoadRequestedState(parameters, required: true);
            var token = LoadWriteToken(state.SessionId, GetString(parameters, "TokenId", "tokenId", "token_id"));
            var payload = parameters.GetValue("AfterSha256", StringComparison.OrdinalIgnoreCase) as JObject;
            if (payload == null)
                return Response.Error("CompleteWrite requires AfterSha256 as an object mapping declared file paths to hashes, or null for deletion.");
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in payload.Properties())
            {
                if (property.Value.Type != JTokenType.String && property.Value.Type != JTokenType.Null)
                    return Response.Error("AfterSha256 values must be hash strings or null.");
                expected[property.Name] = property.Value.Type == JTokenType.Null ? null : property.Value.Value<string>();
            }
            var result = CompleteWrite(token, expected);
            var data = new
            {
                action = "CompleteWrite",
                sessionId = token.SessionId,
                tokenId = token.TokenId,
                recorded = result.RecordedCount,
                conflicted = result.ConflictCount,
                issues = result.Issues
            };
            return result.Succeeded
                ? Response.Success("Recorded verified session-owned file writes.", data)
                : Response.Error("Write ownership could not be verified; conflicted files are protected from revert.", data);
        }
    }
}
