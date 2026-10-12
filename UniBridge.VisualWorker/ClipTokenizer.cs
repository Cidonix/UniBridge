using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cidonix.UniBridge.VisualWorker;

// Independent implementation of the public CLIP byte-level BPE vocabulary contract.
// The vocabulary/merge data are pinned model artifacts, never application source code.
internal sealed class ClipTokenizer
{
    readonly Dictionary<string, int> vocabulary;
    readonly Dictionary<(string, string), int> ranks = new();
    readonly string[] bytes = new string[256];
    readonly Dictionary<string, string[]> cache = new(StringComparer.Ordinal);
    static readonly Regex Words = new("'s|'t|'re|'ve|'m|'ll|'d|[\\p{L}]+|[\\p{N}]|[^\\s\\p{L}\\p{N}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    static readonly Regex Whitespace = new("\\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public ClipTokenizer(string directory)
    {
        vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(directory, "vocab.json")))
            ?? throw new InvalidDataException("Empty CLIP vocabulary.");
        int rank = 0;
        foreach (var line in File.ReadLines(Path.Combine(directory, "merges.txt")))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var pair = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pair.Length != 2) throw new InvalidDataException("Invalid CLIP merge data.");
            ranks.TryAdd((pair[0], pair[1]), rank++);
        }
        var ordinary = Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 12)).Concat(Enumerable.Range(174, 82)).ToHashSet();
        int extra = 256;
        for (int b = 0; b < 256; b++) bytes[b] = char.ConvertFromUtf32(ordinary.Contains(b) ? b : extra++);
        if (!vocabulary.ContainsKey("<|startoftext|>") || !vocabulary.ContainsKey("<|endoftext|>"))
            throw new InvalidDataException("CLIP special tokens are missing.");
    }

    public long[] Encode(string text, CancellationToken cancellation)
    {
        if (text.Length > 2048) throw new ArgumentException("Query exceeds 2048 characters.");
        text = Whitespace.Replace(WebUtility.HtmlDecode(WebUtility.HtmlDecode(text)).Normalize(NormalizationForm.FormC), " ").Trim().ToLowerInvariant();
        var ids = new List<long> { vocabulary["<|startoftext|>"] };
        foreach (Match match in Words.Matches(text))
        {
            cancellation.ThrowIfCancellationRequested();
            var encoded = string.Concat(Encoding.UTF8.GetBytes(match.Value).Select(value => bytes[value]));
            foreach (var part in Merge(encoded, cancellation))
            {
                if (!vocabulary.TryGetValue(part, out var id)) throw new InvalidDataException("Token is absent from the pinned vocabulary.");
                ids.Add(id);
                if (ids.Count > 76) throw new ArgumentException("Query exceeds the CLIP 75 content-token limit; shorten it.");
            }
        }
        ids.Add(vocabulary["<|endoftext|>"]);
        while (ids.Count < 77) ids.Add(vocabulary["<|endoftext|>"]);
        return ids.ToArray();
    }

    string[] Merge(string word, CancellationToken cancellation)
    {
        if (cache.TryGetValue(word, out var cached)) return cached;
        var pieces = word.Select(c => c.ToString()).ToList();
        if (pieces.Count == 0) return Array.Empty<string>();
        pieces[^1] += "</w>";
        while (pieces.Count > 1)
        {
            cancellation.ThrowIfCancellationRequested();
            int best = int.MaxValue;
            (string, string) pair = default;
            for (int i = 0; i + 1 < pieces.Count; i++)
                if (ranks.TryGetValue((pieces[i], pieces[i + 1]), out int rank) && rank < best)
                { best = rank; pair = (pieces[i], pieces[i + 1]); }
            if (best == int.MaxValue) break;
            var merged = new List<string>(pieces.Count);
            for (int i = 0; i < pieces.Count; i++)
            {
                if (i + 1 < pieces.Count && pieces[i] == pair.Item1 && pieces[i + 1] == pair.Item2)
                { merged.Add(pieces[i] + pieces[++i]); }
                else merged.Add(pieces[i]);
            }
            pieces = merged;
        }
        var result = pieces.ToArray();
        if (cache.Count < 4096) cache[word] = result;
        return result;
    }
}
