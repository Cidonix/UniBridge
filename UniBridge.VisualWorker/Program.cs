using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Cidonix.UniBridge.VisualWorker;

internal static class Program
{
    const string Revision = "6ef1ebc8b0766a7a8d11b146462c99cdf74dd22d";
    const string ModelId = "clip-vit-b32-int8-v1";
    const string Source = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/" + Revision + "/";
    const int Pixels = 3 * 224 * 224;
    static readonly Artifact[] Artifacts =
    {
        new("text_model_quantized.onnx", "onnx/text_model_quantized.onnx", 64504507, "73baab855d406190da9faa498cfedf65f15cf309f4cc7385b7b032e6d08e5c3a"),
        new("vision_model_quantized.onnx", "onnx/vision_model_quantized.onnx", 89117001, "583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299"),
        new("vocab.json", "vocab.json", 862328, "5047b556ce86ccaf6aa22b3ffccfc52d391ea4accdab9c2f2407da5b742d4363"),
        new("merges.txt", "merges.txt", 524619, "9fd691f7c8039210e0fced15865466c65820d09b63988b0174bfe25de299051a")
    };
    static readonly string Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ModelId + "|" + string.Join("|", Artifacts.Select(a => a.Sha256))))).ToLowerInvariant();
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    record Artifact(string Name, string Remote, long Bytes, string Sha256);
    record Request(string? Action, string? RequestId, string? Text, string? TensorFile, string? JobRoot, string? ModelDirectory, int DeadlineMs = 15000, bool AllowDownload = false);

    static async Task<int> Main(string[] args)
    {
        string? requestId = null;
        try
        {
            if (args.Length != 1 || args[0] != "--stdio") throw new ArgumentException("Use --stdio and one JSON request line.");
            var input = Console.OpenStandardInput();
            var data = new List<byte>(4096);
            while (true)
            {
                int b = input.ReadByte();
                if (b == -1 || b == '\n') break;
                if (data.Count >= 65536) throw new ArgumentException("Request exceeds 64 KiB.");
                data.Add((byte)b);
            }
            var request = JsonSerializer.Deserialize<Request>(data.ToArray(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ArgumentException("Request must be a JSON object.");
            requestId = request.RequestId;
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128) throw new ArgumentException("A requestId of at most 128 characters is required.");
            int deadline = Math.Clamp(request.DeadlineMs, 100, request.Action == "install" ? 600000 : 120000);
            using var cancellation = new CancellationTokenSource(deadline);
            var clock = Stopwatch.StartNew();
            string directory = request.ModelDirectory is null ? DefaultModelDirectory() : Path.GetFullPath(request.ModelDirectory);
            if (request.Action == "install")
            {
                if (!request.AllowDownload) throw new InvalidOperationException("MODEL_DOWNLOAD_NOT_AUTHORIZED: install requires allowDownload=true.");
                await Install(directory, cancellation.Token);
                Reply(new { success = true, requestId, action = request.Action, modelId = ModelId, modelRevision = Revision, modelFingerprint = Fingerprint,
                    ready = true, offlineQueries = true, elapsedMs = clock.ElapsedMilliseconds });
                return 0;
            }
            var missing = Verify(directory, cancellation.Token);
            if (request.Action == "status")
            {
                Reply(new { success = true, requestId, action = request.Action, modelId = ModelId, modelRevision = Revision, modelFingerprint = Fingerprint,
                    ready = missing.Count == 0, inactiveReason = missing.Count == 0 ? null : "model_missing_or_invalid", missing,
                    modelLanguage = "English", embeddingDimensions = 512, offlineQueries = true, elapsedMs = clock.ElapsedMilliseconds });
                return 0;
            }
            if (missing.Count != 0) throw new FileNotFoundException("MODEL_NOT_READY: run the explicit install action; missing/invalid=" + string.Join(",", missing));
            if (request.Action == "tokenize")
            {
                if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.");
                var ids = new ClipTokenizer(directory).Encode(request.Text, cancellation.Token);
                Reply(new { success = true, requestId, action = request.Action, tokenIds = ids, modelId = ModelId, modelFingerprint = Fingerprint });
                return 0;
            }
            var embedding = request.Action switch
            {
                "encode_text" => EncodeText(directory, request.Text, cancellation.Token),
                "encode_image" => EncodeImage(directory, request.TensorFile, request.JobRoot, cancellation.Token),
                _ => throw new ArgumentException("Unsupported action. Use status, install, tokenize, encode_text or encode_image.")
            };
            Reply(new { success = true, requestId, action = request.Action, modelId = ModelId, modelRevision = Revision, modelFingerprint = Fingerprint,
                embeddingDimensions = embedding.Length, embedding, normalized = true, modelLanguage = "English", offline = true,
                elapsedMs = clock.ElapsedMilliseconds });
            return 0;
        }
        catch (Exception error)
        {
            Reply(new { success = false, requestId, modelId = ModelId, modelFingerprint = Fingerprint,
                code = error is OperationCanceledException ? "deadline_exceeded" : error is FileNotFoundException ? "model_not_ready" : "visual_worker_error",
                error = error.Message.Length > 1000 ? error.Message[..1000] : error.Message });
            return 1;
        }
    }

    static void Reply(object body) => Console.Out.WriteLine(JsonSerializer.Serialize(body, Json));
    static string DefaultModelDirectory() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unibridge", "models", ModelId, Revision);
    static List<string> Verify(string directory, CancellationToken cancellation)
    {
        var missing = new List<string>();
        foreach (var artifact in Artifacts)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, artifact.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != artifact.Bytes || Hash(path, cancellation) != artifact.Sha256) missing.Add(artifact.Name);
        }
        return missing;
    }
    static string Hash(string path, CancellationToken cancellation)
    {
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) != 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, n); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    static async Task Install(string directory, CancellationToken cancellation)
    {
        Directory.CreateDirectory(directory);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var artifact in Artifacts)
        {
            var destination = Path.Combine(directory, artifact.Name);
            if (File.Exists(destination))
            {
                if (new FileInfo(destination).Length == artifact.Bytes && Hash(destination, cancellation) == artifact.Sha256) continue;
                throw new IOException("EXISTING_MODEL_MISMATCH: preserve the unexpected file and select an empty external model cache.");
            }
            string partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using var response = await client.GetAsync(Source + artifact.Remote, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != artifact.Bytes) throw new InvalidDataException("Model Content-Length mismatch.");
                await using (var download = await response.Content.ReadAsStreamAsync(cancellation))
                await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536]; long total = 0; int read;
                    while ((read = await download.ReadAsync(buffer, cancellation)) != 0)
                    {
                        total += read;
                        if (total > artifact.Bytes) throw new InvalidDataException("Model download exceeded the pinned length.");
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    }
                    if (total != artifact.Bytes) throw new InvalidDataException("Incomplete model download.");
                }
                if (Hash(partial, cancellation) != artifact.Sha256) throw new InvalidDataException("Model SHA-256 mismatch.");
                File.Move(partial, destination, overwrite: false);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
    }
    static SessionOptions Options() => new() { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 2), InterOpNumThreads = 1,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL };
    static float[] EncodeText(string directory, string? text, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.");
        var ids = new ClipTokenizer(directory).Encode(text, cancellation);
        using var options = Options();
        using var session = new InferenceSession(Path.Combine(directory, "text_model_quantized.onnx"), options);
        using var tokens = OrtValue.CreateTensorValueFromMemory(ids, new long[] { 1, 77 });
        using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, 77).ToArray(), new long[] { 1, 77 });
        var input = new Dictionary<string, OrtValue> { ["input_ids"] = tokens };
        if (session.InputMetadata.ContainsKey("attention_mask")) input["attention_mask"] = mask;
        return Run(session, input, "text_embeds", cancellation);
    }
    static float[] EncodeImage(string directory, string? tensorFile, string? jobRoot, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(tensorFile) || string.IsNullOrWhiteSpace(jobRoot)) throw new ArgumentException("tensorFile and jobRoot are required.");
        string root = Path.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(tensorFile);
        if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("Tensor file must be inside its declared job root.");
        if (new FileInfo(path).Length != Pixels * sizeof(float)) throw new InvalidDataException("Expected a 3x224x224 float32 tensor.");
        var bytes = File.ReadAllBytes(path);
        var pixels = new float[Pixels];
        Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);
        if (pixels.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Image tensor contains non-finite values.");
        cancellation.ThrowIfCancellationRequested();
        using var options = Options();
        using var session = new InferenceSession(Path.Combine(directory, "vision_model_quantized.onnx"), options);
        using var tensor = OrtValue.CreateTensorValueFromMemory(pixels, new long[] { 1, 3, 224, 224 });
        return Run(session, new Dictionary<string, OrtValue> { ["pixel_values"] = tensor }, "image_embeds", cancellation);
    }
    static float[] Run(InferenceSession session, Dictionary<string, OrtValue> input, string outputName, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!session.OutputMetadata.ContainsKey(outputName)) throw new InvalidDataException("Pinned embedding output is missing: " + outputName);
        using var options = new RunOptions();
        using var registration = cancellation.Register(() => options.Terminate = true);
        IDisposableReadOnlyCollection<OrtValue> output;
        try { output = session.Run(options, input, new[] { outputName }); }
        catch (OnnxRuntimeException error) when (cancellation.IsCancellationRequested)
        { throw new OperationCanceledException("Visual inference deadline exceeded.", error, cancellation); }
        using var outputOwner = output;
        cancellation.ThrowIfCancellationRequested();
        var values = output[0].GetTensorDataAsSpan<float>().ToArray();
        if (values.Length != 512 || values.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Expected 512 finite embedding values.");
        double length = Math.Sqrt(values.Sum(v => (double)v * v));
        if (length < 1e-12) throw new InvalidDataException("Embedding has zero length.");
        for (int i = 0; i < values.Length; i++) values[i] = (float)(values[i] / length);
        return values;
    }
}
