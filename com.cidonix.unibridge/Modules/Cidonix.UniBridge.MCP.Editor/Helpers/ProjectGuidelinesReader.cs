using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // All Unity object/GUID work is completed by the configuration adapter before
    // entering this bounded reader. No source text is interpreted or executed.
    internal static class ProjectGuidelinesReader
    {
        const int DefaultCharsPerSource = 8192;
        const int DefaultTotalChars = 32768;
        const int DefaultFileBytes = 262144;
        const int DefaultTimeoutMs = 3000;

        sealed class BudgetExpired : Exception { }

        internal static object Execute(ProjectGuidelinesConfiguration configuration, ProjectGuidelinesParams input, CancellationToken cancellation)
        {
            input = input ?? new ProjectGuidelinesParams();
            configuration = configuration ?? new ProjectGuidelinesConfiguration();
            var clock = Stopwatch.StartNew();
            var result = new ProjectGuidelinesData
            {
                Action = input.Action.ToString(), Status = "unavailable", Configured = configuration.Configured,
                Complete = true, ConfigurationPath = configuration.ConfigurationPath,
                MaxCharsPerSource = input.MaxCharsPerSource ?? DefaultCharsPerSource,
                MaxTotalChars = input.MaxTotalChars ?? DefaultTotalChars,
                MaxFileBytes = input.MaxFileBytes ?? DefaultFileBytes,
                TimeoutMs = input.TimeoutMs ?? DefaultTimeoutMs,
                Issues = configuration.Issues ?? Array.Empty<string>()
            };
            Func<string, object> fail = code =>
            {
                result.Complete = false;
                result.ElapsedMs = clock.ElapsedMilliseconds;
                return Response.Error(code, result);
            };
            cancellation.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(typeof(ProjectGuidelinesAction), input.Action)) return fail("INVALID_ACTION");
            if (result.MaxCharsPerSource < 1 || result.MaxCharsPerSource > 65536 ||
                result.MaxTotalChars < 1 || result.MaxTotalChars > 131072 ||
                result.MaxFileBytes < 4 || result.MaxFileBytes > 1048576 ||
                result.TimeoutMs < 100 || result.TimeoutMs > 10000) return fail("INVALID_GUIDELINES_BUDGET");
            if ((configuration.Sources?.Length ?? 0) > 3) return fail("INVALID_GUIDELINES_CONFIGURATION");
            if (!result.Configured)
            {
                result.ElapsedMs = clock.ElapsedMilliseconds;
                return input.Action == ProjectGuidelinesAction.Status
                    ? Response.Success("No project guidelines sources are configured.", result)
                    : fail("GUIDELINES_NOT_CONFIGURED");
            }

            var rows = new List<ProjectGuidelinesSourceResult>();
            bool operationalFailure = result.Issues.Length != 0;
            foreach (var source in configuration.Sources ?? Array.Empty<ProjectGuidelinesSource>())
            {
                cancellation.ThrowIfCancellationRequested();
                var row = Identity(source);
                rows.Add(row);
                try
                {
                    Check(clock, result.TimeoutMs, cancellation);
                    ValidatePath(configuration.ProjectRoot, source.AbsolutePath);
                    var info = new FileInfo(source.AbsolutePath);
                    row.Exists = info.Exists;
                    if (!row.Exists)
                    {
                        row.Status = "missing";
                        row.Detail = "The explicitly configured source is missing.";
                        operationalFailure |= input.Action == ProjectGuidelinesAction.Read;
                        continue;
                    }
                    row.LengthBytes = info.Length;
                    row.LastModifiedUtc = info.LastWriteTimeUtc.ToString("o");
                    if (input.Action == ProjectGuidelinesAction.Status)
                    {
                        row.Status = "available";
                        continue;
                    }
                    var remainingChars = result.MaxTotalChars - result.ReturnedChars;
                    if (remainingChars <= 0)
                    {
                        row.Status = "skipped";
                        row.Detail = "total_char_budget";
                        row.Truncated = true;
                        continue;
                    }
                    Read(source.AbsolutePath, row, result.MaxFileBytes,
                        Math.Min(result.MaxCharsPerSource, remainingChars), clock, result.TimeoutMs, cancellation);
                    result.ReturnedChars += row.ReturnedChars;
                }
                catch (OperationCanceledException) { throw; }
                catch (BudgetExpired)
                {
                    row.Status = "error";
                    row.Detail = "timeout";
                    operationalFailure = true;
                }
                catch (DecoderFallbackException)
                {
                    row.Status = "error";
                    row.Detail = "invalid_text_encoding";
                    operationalFailure = true;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
                {
                    row.Status = "error";
                    row.Detail = Clip(error.Message, 400);
                    operationalFailure = true;
                }
            }
            result.Sources = rows.ToArray();
            result.BytesRead = rows.Sum(row => row.BytesRead);
            result.Truncated = rows.Any(row => row.Truncated);
            result.Complete = !operationalFailure && !result.Truncated;
            var usable = rows.Count(row => row.Status == "available" || row.Status == "read");
            result.Status = usable == 0 ? "unavailable" : operationalFailure || result.Truncated ? "partial" : "available";
            result.ElapsedMs = clock.ElapsedMilliseconds;
            cancellation.ThrowIfCancellationRequested();
            if (operationalFailure) return Response.Error("GUIDELINES_READ_FAILED", result);
            if (input.Action == ProjectGuidelinesAction.Read && usable == 0) return Response.Error("GUIDELINES_UNAVAILABLE", result);
            return Response.Success(input.Action == ProjectGuidelinesAction.Status ? "Project guidelines status inspected." : "Project-authored guidelines read.", result);
        }

        static ProjectGuidelinesSourceResult Identity(ProjectGuidelinesSource source)
        {
            return new ProjectGuidelinesSourceResult
            {
                Kind = source.Kind, SourceId = source.SourceId, AssetGuid = source.AssetGuid,
                DisplayName = source.DisplayName, RelativePath = source.RelativePath,
                Uri = string.IsNullOrEmpty(source.RelativePath) ? null
                    : source.Kind == "text_asset" ? "unity://path/" + source.RelativePath : "project://" + source.RelativePath,
                Status = "configured"
            };
        }

        static void ValidatePath(string root, string file)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(file)) throw new IOException("Source path is unavailable.");
            var project = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(file);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(project + Path.DirectorySeparatorChar, comparison)) throw new IOException("Guidelines source must remain inside this project.");
            for (var cursor = full; !string.Equals(cursor, project, comparison); cursor = Path.GetDirectoryName(cursor))
            {
                if (string.IsNullOrEmpty(cursor)) throw new IOException("Source ancestry could not be verified.");
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Guidelines source cannot pass through a reparse point.");
            }
        }

        static void Read(string path, ProjectGuidelinesSourceResult row, int fileCap, int charCap,
            Stopwatch clock, int timeout, CancellationToken cancellation)
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            byte[] bytes;
            bool full;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192))
            using (var copy = new MemoryStream())
            using (var hash = SHA256.Create())
            {
                var originalLength = stream.Length;
                row.LengthBytes = originalLength;
                var buffer = new byte[8192];
                while (copy.Length < fileCap)
                {
                    Check(clock, timeout, cancellation);
                    var amount = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, fileCap - copy.Length));
                    Check(clock, timeout, cancellation);
                    if (amount == 0) break;
                    copy.Write(buffer, 0, amount);
                    hash.TransformBlock(buffer, 0, amount, buffer, 0);
                    row.BytesRead += amount;
                }
                full = copy.Length == originalLength;
                if (stream.Length != originalLength || !File.Exists(path) || File.GetLastWriteTimeUtc(path) != stamp)
                    throw new IOException("file_changed_during_read");
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                row.Sha256 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                row.Sha256Scope = full ? "full" : "captured_prefix";
                row.BytesHashed = copy.Length;
                bytes = copy.ToArray();
            }
            Check(clock, timeout, cancellation);
            int offset;
            var encoding = ResolveEncoding(bytes, out offset, out var name);
            row.Encoding = name;
            var decoder = encoding.GetDecoder();
            var chars = new char[Math.Max(1, bytes.Length)];
            decoder.Convert(bytes, offset, bytes.Length - offset, chars, 0, chars.Length, full,
                out _, out var charCount, out _);
            Check(clock, timeout, cancellation);
            var returned = Math.Min(charCount, charCap);
            if (returned > 0 && returned < charCount && char.IsHighSurrogate(chars[returned - 1])) returned--;
            row.Text = new string(chars, 0, returned);
            row.ReturnedChars = returned;
            row.Truncated = !full || returned < charCount;
            row.Detail = !full ? "file_byte_budget" : returned < charCount ? "character_budget" : null;
            row.Status = "read";
        }

        static Encoding ResolveEncoding(byte[] bytes, out int offset, out string name)
        {
            offset = 0; name = "utf-8";
            if (bytes.Length >= 4 && (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0 ||
                bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF))
                throw new DecoderFallbackException("UTF-32 sources are unsupported.");
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) offset = 3;
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            { offset = 2; name = "utf-16-le"; return new UnicodeEncoding(false, true, true); }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            { offset = 2; name = "utf-16-be"; return new UnicodeEncoding(true, true, true); }
            return new UTF8Encoding(false, true);
        }

        static void Check(Stopwatch clock, int timeout, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); if (clock.ElapsedMilliseconds >= timeout) throw new BudgetExpired(); }
        static string Clip(string value, int limit) => value == null || value.Length <= limit ? value : value.Substring(0, limit);
    }
}
