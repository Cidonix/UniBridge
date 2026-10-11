using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Filesystem work is cooperative: checks surround bounded reads/regex calls and
    // lazy enumeration. An operating-system I/O call itself cannot be interrupted.
    internal static class ResourceBudgetIO
    {
        internal const int DefaultFileBytes = 16 * 1024 * 1024;
        internal const int DefaultReturnBytes = 256 * 1024;
        internal const int DefaultWorkUnits = 1000000;
        internal const int DefaultTimeoutMs = 3000;
        internal const int DefaultRegexTimeoutMs = 50;
        internal const int DefaultEntries = 20000;
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        sealed class Stop : Exception
        {
            internal readonly string Status;
            internal readonly string Reason;
            internal Stop(string status, string reason) : base(reason) { Status = status; Reason = reason; }
        }

        sealed class Budget
        {
            readonly Stopwatch clock = Stopwatch.StartNew();
            readonly CancellationToken cancellation;
            readonly int timeout;
            readonly int work;
            internal readonly ResourceOperationOutcome Outcome = new ResourceOperationOutcome();
            internal Budget(int maxWork, int timeoutMs, CancellationToken token)
            { work = Bound(maxWork, DefaultWorkUnits, 8000000); timeout = Bound(timeoutMs, DefaultTimeoutMs, 10000); cancellation = token; }
            internal void Check()
            {
                if (cancellation.IsCancellationRequested) throw new Stop("cancelled", "cancelled");
                if (clock.ElapsedMilliseconds >= timeout) throw new Stop("partial", "timeout");
            }
            internal void Step(int amount = 1)
            {
                Check();
                if (amount > work - Outcome.WorkUnits) throw new Stop("partial", "work_limit");
                Outcome.WorkUnits += amount;
            }
            internal void StopAt(string reason) { throw new Stop("partial", reason); }
            internal void Finish() { Outcome.ElapsedMs = clock.ElapsedMilliseconds; }
            internal void Fail(Exception error)
            {
                var stop = error as Stop;
                Outcome.Status = stop == null ? "error" : stop.Status;
                Outcome.Reason = stop == null ? "io_error" : stop.Reason;
                Outcome.Complete = false;
                if (stop == null) Outcome.Detail = error.Message;
            }
            internal void Partial(string reason)
            { Outcome.Status = "partial"; Outcome.Reason = reason; Outcome.Complete = false; }
        }

        static int Bound(int value, int fallback, int maximum) => value <= 0 ? fallback : Math.Min(value, maximum);

        sealed class FileData
        {
            internal byte[] Bytes;
            internal string Text;
            internal string Sha256;
            internal long Length;
            internal bool Complete;
        }

        static FileData Load(string path, int maxFileBytes, Budget budget)
        {
            var data = new FileData();
            var cap = Bound(maxFileBytes, DefaultFileBytes, 64 * 1024 * 1024);
            budget.Check();
            var stamp = File.GetLastWriteTimeUtc(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192))
            using (var copy = new MemoryStream())
            using (var hash = SHA256.Create())
            {
                data.Length = stream.Length;
                var buffer = new byte[8192];
                while (copy.Length < cap)
                {
                    budget.Step();
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, cap - copy.Length));
                    budget.Check();
                    if (read == 0) { data.Complete = true; break; }
                    copy.Write(buffer, 0, read);
                    hash.TransformBlock(buffer, 0, read, buffer, 0);
                    budget.Outcome.BytesScanned += read;
                }
                // The exact boundary is complete only for the same entire file.
                data.Complete |= copy.Length == stream.Length && copy.Length == data.Length;
                if (stream.Length != data.Length || File.GetLastWriteTimeUtc(path) != stamp)
                    throw new Stop("error", "file_changed");
                if (data.Complete)
                {
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    data.Sha256 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                }
                data.Bytes = copy.ToArray();
            }
            budget.Check();
            data.Text = Utf8.GetString(data.Bytes);
            if (!data.Complete) budget.Partial("file_bytes");
            return data;
        }

        static string LimitText(string text, int limit, Budget budget)
        {
            int end = 0, bytes = 0;
            while (end < text.Length)
            {
                budget.Step();
                char c = text[end];
                int chars = char.IsHighSurrogate(c) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
                int width = chars == 2 ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                if (width > limit - bytes) { budget.Partial("return_bytes"); break; }
                bytes += width; end += chars;
            }
            budget.Outcome.ReturnedBytes = bytes;
            return end == text.Length ? text : text.Substring(0, end);
        }

        internal static ReadResourceResponse Generated(string text, ReadResourceParams input, CancellationToken token)
        {
            var budget = new Budget(input.MaxWorkUnits, input.TimeoutMs, token);
            var response = new ReadResourceResponse { Text = "", Outcome = budget.Outcome };
            try
            {
                budget.Check();
                int cap = Bound(input.MaxFileBytes, DefaultFileBytes, 64 * 1024 * 1024);
                if (text.Length > cap) throw new Stop("blocked", "file_bytes");
                int length = Utf8.GetByteCount(text);
                response.Metadata.LengthBytes = length;
                if (length > cap) throw new Stop("blocked", "file_bytes");
                var bytes = Utf8.GetBytes(text);
                using (var hash = SHA256.Create())
                {
                    for (int i = 0; i < bytes.Length; i += 8192)
                    {
                        budget.Step();
                        int count = Math.Min(8192, bytes.Length - i);
                        hash.TransformBlock(bytes, i, count, bytes, i);
                        budget.Outcome.BytesScanned += count;
                    }
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    response.Metadata.Sha256 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                    response.Metadata.Sha256Complete = true;
                }
                response.Text = LimitText(text, Bound(input.MaxReturnBytes, DefaultReturnBytes, 2 * 1024 * 1024), budget);
            }
            catch (Exception error) { budget.Fail(error); }
            finally { budget.Finish(); }
            return response;
        }

        internal static ReadResourceResponse Read(string path, ReadResourceParams input, CancellationToken token)
        {
            var budget = new Budget(input.MaxWorkUnits, input.TimeoutMs, token);
            var response = new ReadResourceResponse { Text = "", Outcome = budget.Outcome };
            try
            {
                var file = Load(path, input.MaxFileBytes, budget);
                response.Metadata = new ResourceMetadata { LengthBytes = file.Length, Sha256 = file.Sha256, Sha256Complete = file.Complete };
                int start = input.StartLine, count = input.LineCount, head = input.HeadBytes, tail = input.TailLines;
                ResolveRequest(input.Request, file.Text, input.StartLine == 1 && input.LineCount == -1,
                    ref start, ref count, ref head, ref tail, input.RegexTimeoutMs, budget);
                string selection;
                if (head > 0) selection = Utf8.GetString(file.Bytes, 0, Math.Min(head, file.Bytes.Length));
                else if (tail > 0)
                {
                    if (!file.Complete) { selection = ""; budget.Partial("tail_unavailable_without_eof"); }
                    else selection = Tail(file.Text, Math.Min(tail, 1000), budget);
                    if (tail > 1000) budget.Partial("line_limit");
                }
                else selection = Lines(file.Text, Math.Max(1, start), count, budget);
                response.Text = LimitText(selection, Bound(input.MaxReturnBytes, DefaultReturnBytes, 2 * 1024 * 1024), budget);
            }
            catch (Exception error) { budget.Fail(error); }
            finally { budget.Finish(); }
            return response;
        }

        static string Lines(string text, int start, int count, Budget budget)
        {
            if (start == 1 && (count == -1 || count == 0)) return text;
            int index = 0, line = 1;
            while (line < start)
            {
                budget.Step();
                int next = text.IndexOf('\n', index);
                if (next < 0) return "";
                index = next + 1; line++;
            }
            if (count < 0) return text.Substring(index);
            int end = index;
            for (int i = 0; i < count; i++)
            {
                budget.Step();
                int next = text.IndexOf('\n', end);
                if (next < 0) { end = text.Length; break; }
                end = next + 1;
                if (i == count - 1) end--;
            }
            return text.Substring(index, Math.Max(0, end - index));
        }

        static string Tail(string text, int count, Budget budget)
        {
            var starts = new Queue<int>();
            starts.Enqueue(0);
            int index = 0;
            while (index < text.Length)
            {
                budget.Step();
                int next = text.IndexOf('\n', index);
                if (next < 0) break;
                starts.Enqueue(next + 1);
                if (starts.Count > count) starts.Dequeue();
                index = next + 1;
            }
            return text.Substring(starts.Peek());
        }

        static Regex BoundedRegex(string pattern, RegexOptions options, int requestedTimeout)
        {
            if (pattern == null || pattern.Length > 4096) throw new Stop("blocked", "invalid_regex");
            try { return new Regex(pattern, options, TimeSpan.FromMilliseconds(Bound(requestedTimeout, DefaultRegexTimeoutMs, 250))); }
            catch (ArgumentException) { throw new Stop("blocked", "invalid_regex"); }
        }

        static void ResolveRequest(string request, string text, bool defaultLines,
            ref int start, ref int count, ref int head, ref int tail, int regexTimeout, Budget budget)
        {
            if (string.IsNullOrEmpty(request)) return;
            if (request.Length > 1024) throw new Stop("blocked", "request_limit");
            var req = request.Trim().ToLowerInvariant();
            var matcher = BoundedRegex(@"(?:last\s+(\d+)\s+lines)|(?:first\s+(\d+)\s+lines)|(?:first\s+(\d+)\s*bytes)|(?:show\s+(\d+)\s+lines\s+around\s+([a-z_][a-z0-9_]*))", RegexOptions.None, regexTimeout);
            budget.Step();
            Match match;
            try { match = matcher.Match(req); }
            catch (RegexMatchTimeoutException) { throw new Stop("partial", "regex_timeout"); }
            if (!match.Success) return;
            int value;
            for (int i = 1; i <= 4; i++)
            {
                if (!match.Groups[i].Success) continue;
                if (!int.TryParse(match.Groups[i].Value, out value)) throw new Stop("blocked", "request_limit");
                if (i == 1 && tail <= 0) tail = value;
                if (i == 2 && defaultLines) { start = 1; count = value; }
                if (i == 3 && head <= 0) head = value;
                if (i == 4 && defaultLines)
                {
                    var method = BoundedRegex(@"^\s*(?:\[[^\]]+\]\s*)*(?:public|private|protected|internal|static|virtual|override|sealed|async|extern|unsafe|new|partial).*?\b" + Regex.Escape(match.Groups[5].Value) + @"\s*\(", RegexOptions.IgnoreCase, regexTimeout);
                    int index = 0, line = 1;
                    while (index <= text.Length)
                    {
                        budget.Step();
                        int next = text.IndexOf('\n', index); if (next < 0) next = text.Length;
                        try { if (method.IsMatch(text.Substring(index, next - index))) { start = Math.Max(1, line - Math.Max(1, value / 2)); count = value; break; } }
                        catch (RegexMatchTimeoutException) { throw new Stop("partial", "regex_timeout"); }
                        if (next == text.Length) break;
                        index = next + 1; line++;
                    }
                }
                break;
            }
        }

        internal static FindInFileResponse Find(string path, FindInFileParams input, CancellationToken token)
        {
            var budget = new Budget(input.MaxWorkUnits, input.TimeoutMs, token);
            var response = new FindInFileResponse { Outcome = budget.Outcome };
            try
            {
                var regex = BoundedRegex(input.Pattern, RegexOptions.Multiline | (input.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), input.RegexTimeoutMs);
                var file = Load(path, input.MaxFileBytes, budget);
                response.Sha256 = file.Sha256; response.Sha256Complete = file.Complete;
                int index = 0, line = 1, limit = Math.Min(1000, Math.Max(1, input.MaxResults));
                while (index <= file.Text.Length)
                {
                    budget.Step();
                    int next = file.Text.IndexOf('\n', index); if (next < 0) next = file.Text.Length;
                    Match match;
                    try { match = regex.Match(file.Text.Substring(index, next - index)); }
                    catch (RegexMatchTimeoutException) { throw new Stop("partial", "regex_timeout"); }
                    budget.Check();
                    if (match.Success)
                    {
                        response.Matches.Add(new FindMatch { StartLine = line, StartCol = match.Index + 1, EndLine = line, EndCol = match.Index + match.Length + 1 });
                        if (response.Matches.Count >= limit && next < file.Text.Length)
                        {
                            // A requested result cap must not hide an earlier incomplete
                            // byte scan and turn that stronger failure into success.
                            if (budget.Outcome.Complete) budget.Partial("result_limit");
                            break;
                        }
                    }
                    if (next == file.Text.Length) break;
                    index = next + 1; line++;
                }
            }
            catch (Exception error) { budget.Fail(error); }
            finally { response.Count = response.Matches.Count; budget.Finish(); }
            return response;
        }

        internal static ListResourcesResponse List(string projectRoot, string root, ListResourcesParams input, CancellationToken token)
        {
            var budget = new Budget(input.MaxWorkUnits, input.TimeoutMs, token);
            var response = new ListResourcesResponse { Outcome = budget.Outcome };
            try
            {
                int entryLimit = Bound(input.MaxEntries, DefaultEntries, 100000);
                int resultLimit = Math.Min(1000, Math.Max(1, input.Limit));
                var pattern = input.Pattern ?? "*";
                if (pattern.Length > 256) throw new Stop("blocked", "pattern_limit");
                var folders = new Stack<string>();
                if (Directory.Exists(root)) folders.Push(root);
                while (folders.Count != 0)
                {
                    budget.Step();
                    var folder = folders.Pop();
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { budget.Outcome.SkippedEntries++; continue; }
                    using (var iterator = Directory.EnumerateFileSystemEntries(folder).GetEnumerator())
                        while (true)
                        {
                            budget.Check();
                            if (budget.Outcome.EntriesVisited >= entryLimit) budget.StopAt("entry_limit");
                            if (!iterator.MoveNext()) break;
                            budget.Step();
                            budget.Outcome.EntriesVisited++;
                            var path = iterator.Current;
                            var attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) { budget.Outcome.SkippedEntries++; continue; }
                            if ((attributes & FileAttributes.Directory) != 0) { folders.Push(path); continue; }
                            if (!Glob(Path.GetFileName(path), pattern, budget)) continue;
                            string relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
                            response.Uris.Add("unity://path/" + relative);
                            if (response.Uris.Count >= resultLimit) { budget.StopAt("result_limit"); }
                        }
                }
                if (budget.Outcome.SkippedEntries > 0) budget.Partial("entries_skipped");
            }
            catch (Exception error) { budget.Fail(error); }
            finally { response.Count = response.Uris.Count; budget.Finish(); }
            return response;
        }

        static bool Glob(string text, string pattern, Budget budget)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            int i = 0, j = 0, star = -1, retry = 0;
            while (i < text.Length)
            {
                budget.Step();
                if (j < pattern.Length && (pattern[j] == '?' || char.ToUpperInvariant(pattern[j]) == char.ToUpperInvariant(text[i]))) { i++; j++; }
                else if (j < pattern.Length && pattern[j] == '*') { star = j++; retry = i; }
                else if (star >= 0) { j = star + 1; i = ++retry; }
                else return false;
            }
            while (j < pattern.Length && pattern[j] == '*') { budget.Step(); j++; }
            return j == pattern.Length;
        }
    }
}
