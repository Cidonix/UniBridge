using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.Toolkit;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    // The index is disposable derived Editor data. GUID/local-file-ID references
    // are re-resolved for every operation; authored assets are never modified.
    public static class VisualAssetSearch
    {
        const string ToolName = "UniBridge_VisualAssetSearch";
        const string Provider = "clip-vit-b32-int8-v1";
        const string Fingerprint = "cf01db9192c62826c46cf2ca9386d1ef12a6e535994960b0294eb7dbe53fd8eb";
        const int MaxEntries = 2000;
        static readonly SemaphoreSlim Gate = new(1, 1);
        // Private ownership diagnostics for the opt-in native fixture; no product payload fields.
        static int ActiveWorkerPid, LastCaptureThreadId;
        static bool LastRenderTextureRestored, LastTextureDestroyed, LastTemporaryReleased;
        static JObject OwnershipDiagnostics() => new()
        { ["activeWorkerPid"] = Volatile.Read(ref ActiveWorkerPid), ["captureThreadId"] = LastCaptureThreadId,
            ["renderTextureRestored"] = LastRenderTextureRestored, ["textureDestroyed"] = LastTextureDestroyed,
            ["temporaryReleased"] = LastTemporaryReleased };

        [McpSchema(ToolName)]
        public static object GetInputSchema() => JObject.Parse(@"{
          'type':'object','additionalProperties':false,'properties':{
            'Action':{'type':'string','enum':['Status','InstallModel','Index','Search','ClearIndex'],'default':'Status'},
            'Query':{'type':'string','maxLength':2048,'description':'English visual-content query; names and filenames are not ranking inputs.'},
            'AssetReferences':{'type':'array','maxItems':100,'items':{'type':'object','additionalProperties':false,
              'properties':{'Guid':{'type':'string','pattern':'^[a-fA-F0-9]{32}$'},'LocalFileId':{'type':'integer'}},'required':['Guid']}},
            'MaxAssets':{'type':'integer','minimum':1,'maximum':100,'default':20},
            'Offset':{'type':'integer','minimum':0,'maximum':50000,'default':0,'description':'Deterministic GUID-order cursor for bounded default texture indexing.'},
            'Limit':{'type':'integer','minimum':1,'maximum':50,'default':10},
            'MinScore':{'type':'number','minimum':-1,'maximum':1,'default':-1},
            'TimeoutMs':{'type':'integer','minimum':100,'maximum':120000,'default':30000},
            'AllowDownload':{'type':'boolean','default':false,'description':'Required true only for explicit InstallModel.'}
          }}");

        [McpOutputSchema(ToolName)]
        public static object GetOutputSchema() => JObject.Parse(@"{
          'type':'object','additionalProperties':false,'properties':{
            'success':{'type':'boolean'},'message':{'type':'string'},'code':{'type':'string'},'error':{'type':'string'},
            'projectContext':{'type':'object'},
            'data':{'type':'object','additionalProperties':false,'properties':{
              'action':{'type':'string'},'backend':{'const':'local_onnx_clip_cpu'},'modelId':{'type':'string'},
              'modelFingerprint':{'type':'string'},'modelLanguage':{'const':'English'},'ready':{'type':'boolean'},
              'inactiveReason':{'type':['string','null']},'indexedCount':{'type':'integer'},'processed':{'type':'integer'},
              'totalCandidates':{'type':'integer'},'complete':{'type':'boolean'},'elapsedMs':{'type':'integer'},
              'nextOffset':{'type':'integer'},
              'results':{'type':'array','items':{'type':'object','additionalProperties':false,'properties':{
                'guid':{'type':'string'},'localFileId':{'type':'integer'},'path':{'type':'string'},'name':{'type':'string'},
                'type':{'type':'string'},'score':{'type':['number','null']},'dependencyHash':{'type':'string'},'sourceSha256':{'type':'string'}},
                'required':['guid','localFileId','path','name','type','score','dependencyHash','sourceSha256']}},
              'failures':{'type':'array','items':{'type':'object','additionalProperties':false,'properties':{
                'guid':{'type':'string'},'code':{'type':'string'},'error':{'type':'string'}},'required':['guid','code','error']}},
              'staleCount':{'type':'integer'}},
              'required':['action','backend','modelId','modelFingerprint','modelLanguage','ready','inactiveReason','indexedCount',
                'processed','totalCandidates','complete','elapsedMs','nextOffset','results','failures','staleCount']}}
          ,'required':['success','data'],
          'anyOf':[{'properties':{'success':{'const':true}},'required':['message']},
                   {'properties':{'success':{'const':false}},'required':['code','error']}]}");

        [McpTool(ToolName, "Search project assets by actual visual content using a local CLIP model. Status is offline; InstallModel explicitly downloads pinned public model artifacts once. Index records GUID/local-file-ID assets into disposable Library cache; omitted references enumerate main Texture2D assets with MaxAssets bound. Search ranks genuine 512-dimensional text/image embeddings, excludes changed/deleted assets and declares stale coverage. English queries; scores are cosine similarity, not probability. No asset-name/color heuristic fallback, cloud query or authored-asset change.", "Search assets by visual meaning", Groups = new[] { "assets", "search", "intelligence" }, EnabledByDefault = true)]
        public static async Task<object> HandleCommand(JObject input)
        {
            input ??= new JObject();
            string action = input["Action"]?.Type == JTokenType.String ? input.Value<string>("Action") : "Status";
            var watch = Stopwatch.StartNew();
            var data = NewData(action);
            string invalid = ValidateInput(input);
            if (invalid != null) return Error("INVALID_ARGUMENTS", invalid, data, watch);
            var token = ToolExecutionScheduler.CurrentCancellationToken;
            int timeout = Math.Max(100, Math.Min(120000, input.Value<int?>("TimeoutMs") ?? 30000));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            bool entered = false;
            try
            {
                await Gate.WaitAsync(deadline.Token).ConfigureAwaitMainThread(); entered = true;
                deadline.Token.ThrowIfCancellationRequested();
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    return Error("EDITOR_NOT_READY", "Wait for actual Editor compilation/import readiness.", data, watch);
                if (action == "ClearIndex")
                {
                    if (File.Exists(IndexPath())) File.Delete(IndexPath());
                    data["indexedCount"] = 0; data["inactiveReason"] = "model_not_observed";
                    return Success("Disposable visual index cleared.", data, watch);
                }
                var index = LoadIndex();
                data["indexedCount"] = index.Count;
                if (action != "Status" && action != "InstallModel" && action != "Index" && action != "Search")
                    return Error("INVALID_ACTION", "Unsupported visual search action.", data, watch);
                var status = await RunWorker(new JObject { ["action"] = action == "InstallModel" ? "install" : "status",
                    ["allowDownload"] = action == "InstallModel" && (input.Value<bool?>("AllowDownload") ?? false) },
                    action == "InstallModel" ? Math.Max(100, timeout - (int)watch.ElapsedMilliseconds) : Remaining(timeout, watch), deadline.Token);
                data["ready"] = status.Value<bool?>("ready") ?? false;
                data["inactiveReason"] = status["inactiveReason"]?.DeepClone() ?? JValue.CreateNull();
                if (action == "Status" || action == "InstallModel")
                    return Success(action == "Status" ? "Visual model/index status observed." : "Pinned local model installed and verified.", data, watch);
                if (!data.Value<bool>("ready")) return Error("VISUAL_MODEL_NOT_READY", "Run InstallModel with AllowDownload=true before indexing/querying.", data, watch);
                if (action == "Search")
                    return await Search(input, index, data, timeout, watch, deadline.Token);
                return await Index(input, index, data, timeout, watch, deadline.Token);
            }
            catch (OperationCanceledException)
            { return Error(token.IsCancellationRequested ? "CANCELED" : "VISUAL_SEARCH_TIMEOUT", "Visual observation stopped; authored assets are unchanged.", data, watch); }
            catch (FileNotFoundException error) when (action == "Status")
            { data["inactiveReason"] = "worker_unavailable"; return Success(error.Message, data, watch); }
            catch (PlatformNotSupportedException error) when (action == "Status")
            { data["inactiveReason"] = "platform_unsupported"; return Success(error.Message, data, watch); }
            catch (Exception error)
            { return Error("VISUAL_SEARCH_ERROR", error.Message, data, watch); }
            finally { if (entered) Gate.Release(); }
        }

        // The registry exposes schemas but does not execute a JSON Schema validator.
        // Validate this tool's closed wire contract before any native/cache work;
        // explicit malformed references must never become default enumeration.
        static string ValidateInput(JObject input)
        {
            string[] allowed = { "Action", "Query", "AssetReferences", "MaxAssets", "Offset", "Limit", "MinScore", "TimeoutMs", "AllowDownload" };
            foreach (var property in input.Properties())
                if (property.Name != ProjectContextGuard.ExpectedProjectRootParameter && !allowed.Contains(property.Name, StringComparer.Ordinal)) return "Unknown visual search parameter: " + property.Name;
            // The relay adds this exact transport field after client schema validation.
            // It is not a business parameter, and no arbitrary reserved prefix is allowed.
            if (input.TryGetValue(ProjectContextGuard.ExpectedProjectRootParameter, out var expectedRoot) &&
                (expectedRoot.Type != JTokenType.String || string.IsNullOrWhiteSpace(expectedRoot.Value<string>())))
                return "The internal expected project root must be a nonempty string.";
            if (input.TryGetValue("Action", out var action) && (action.Type != JTokenType.String || !new[] { "Status", "InstallModel", "Index", "Search", "ClearIndex" }.Contains(action.Value<string>(), StringComparer.Ordinal)))
                return "Action must be one of Status, InstallModel, Index, Search or ClearIndex.";
            if (input.TryGetValue("Query", out var query) && (query.Type != JTokenType.String || query.Value<string>().Length > 2048))
                return "Query must be a string of at most 2048 characters.";
            if (input.TryGetValue("AllowDownload", out var download) && download.Type != JTokenType.Boolean)
                return "AllowDownload must be a boolean.";
            foreach (var bound in new[] { ("MaxAssets", 1, 100), ("Offset", 0, 50000), ("Limit", 1, 50), ("TimeoutMs", 100, 120000) })
                if (input.TryGetValue(bound.Item1, out var value) && !IntegerInRange(value, bound.Item2, bound.Item3))
                    return bound.Item1 + " must be an integer within its advertised range.";
            if (input.TryGetValue("MinScore", out var score))
            {
                if (score.Type != JTokenType.Integer && score.Type != JTokenType.Float) return "MinScore must be a finite number in [-1, 1].";
                try { double number = score.Value<double>(); if (double.IsNaN(number) || double.IsInfinity(number) || number < -1 || number > 1) return "MinScore must be a finite number in [-1, 1]."; }
                catch { return "MinScore must be a finite number in [-1, 1]."; }
            }
            if (input.TryGetValue("AssetReferences", out var references))
            {
                if (references is not JArray rows || rows.Count > 100) return "Explicit AssetReferences must be an array of at most 100 object references.";
                foreach (var row in rows)
                {
                    if (row is not JObject reference || reference.Properties().Any(property => property.Name != "Guid" && property.Name != "LocalFileId"))
                        return "Each asset reference must be a closed Guid/LocalFileId object.";
                    if (!reference.TryGetValue("Guid", out var guid) || guid.Type != JTokenType.String || guid.Value<string>().Length != 32 || !System.Guid.TryParseExact(guid.Value<string>(), "N", out _))
                        return "Each asset reference requires a GUID with exactly 32 hexadecimal digits.";
                    if (reference.TryGetValue("LocalFileId", out var id) && !IntegerInRange(id, long.MinValue, long.MaxValue))
                        return "LocalFileId must be an exact signed 64-bit integer.";
                }
            }
            return null;
        }
        static bool IntegerInRange(JToken token, decimal minimum, decimal maximum)
        {
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
            try { decimal value = token.Value<decimal>(); return decimal.Truncate(value) == value && value >= minimum && value <= maximum; }
            catch { return false; }
        }

        static JObject NewData(string action) => new()
        {
            ["action"] = action, ["backend"] = "local_onnx_clip_cpu", ["modelId"] = Provider,
            ["modelFingerprint"] = Fingerprint, ["modelLanguage"] = "English", ["ready"] = false,
            ["inactiveReason"] = JValue.CreateNull(), ["indexedCount"] = 0, ["processed"] = 0,
            ["totalCandidates"] = 0, ["complete"] = true, ["elapsedMs"] = 0,
            ["nextOffset"] = 0,
            ["results"] = new JArray(), ["failures"] = new JArray(), ["staleCount"] = 0
        };
        static object Success(string message, JObject data, Stopwatch watch)
        { data["elapsedMs"] = watch.ElapsedMilliseconds; return Response.Success(message, data); }
        static object Error(string code, string error, JObject data, Stopwatch watch)
        {
            data["elapsedMs"] = watch.ElapsedMilliseconds; data["complete"] = false;
            return new { success = false, code, error, data };
        }
        static int Remaining(int budget, Stopwatch clock)
        { int left = budget - (int)clock.ElapsedMilliseconds; if (left < 100) throw new OperationCanceledException(); return Math.Min(15000, left); }
        static string IndexPath() => Path.Combine(ProjectRoot(), "Library", "UniBridge", "VisualAssetSearch", "index-v1.json");
        static string ProjectRoot() => Directory.GetParent(Application.dataPath).FullName;
        static JArray LoadIndex()
        {
            if (!File.Exists(IndexPath())) return new JArray();
            if (new FileInfo(IndexPath()).Length > 16 * 1024 * 1024) throw new InvalidDataException("Visual index exceeds its cache limit.");
            var document = JObject.Parse(File.ReadAllText(IndexPath()));
            if (document.Value<int?>("version") != 1 || document.Value<string>("modelFingerprint") != Fingerprint)
                throw new InvalidDataException("Visual index model/version mismatch; explicitly ClearIndex and reindex.");
            var items = document["items"] as JArray ?? throw new InvalidDataException("Invalid visual index items.");
            if (items.Count > MaxEntries) throw new InvalidDataException("Visual index entry limit exceeded.");
            foreach (JObject item in items)
                ValidateVector(item["embedding"] as JArray);
            return items;
        }
        static void SaveIndex(JArray items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string target = IndexPath(), partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            try
            {
                string serialized = McpJson.SerializeObject(new JObject { ["version"] = 1, ["modelFingerprint"] = Fingerprint, ["items"] = items });
                if (System.Text.Encoding.UTF8.GetByteCount(serialized) > 16 * 1024 * 1024) throw new InvalidDataException("Visual index exceeds its cache byte budget.");
                File.WriteAllText(partial, serialized);
                token.ThrowIfCancellationRequested();
                if (File.Exists(target)) File.Replace(partial, target, null); else File.Move(partial, target);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }

        static async Task<object> Index(JObject input, JArray items, JObject data, int timeout, Stopwatch watch, CancellationToken token)
        {
            int cap = Math.Max(1, Math.Min(100, input.Value<int?>("MaxAssets") ?? 20));
            int offset = Math.Max(0, Math.Min(50000, input.Value<int?>("Offset") ?? 0));
            var references = input["AssetReferences"] as JArray;
            if (references == null)
                references = new JArray(AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" }).OrderBy(guid => guid, StringComparer.Ordinal).Select(guid => new JObject { ["Guid"] = guid }));
            if (references.Count > 50000) throw new InvalidDataException("Candidate enumeration exceeds 50000 entries; provide bounded GUID references.");
            data["totalCandidates"] = references.Count;
            data["nextOffset"] = Math.Min(references.Count, offset + cap);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject reference in references.Skip(offset).Take(cap))
            {
                token.ThrowIfCancellationRequested();
                string guid = reference.Value<string>("Guid"); long? requestedId = reference.Value<long?>("LocalFileId");
                try
                {
                    if (!System.Guid.TryParseExact(guid, "N", out var parsedGuid)) throw new InvalidDataException("Asset GUID must contain exactly 32 hexadecimal digits.");
                    guid = parsedGuid.ToString("N");
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) throw new InvalidDataException("Asset GUID is absent from project Assets.");
                    Object asset = requestedId.HasValue ? AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(candidate =>
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out string _, out long id) && id == requestedId.Value) : AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string resolvedGuid, out long localId) || !string.Equals(resolvedGuid, guid, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Asset GUID/local-file-ID could not be resolved.");
                    string key = guid + ":" + localId;
                    if (!seen.Add(key)) continue;
                    string dependency = AssetDatabase.GetAssetDependencyHash(path).ToString();
                    string sourceHash = SourceHash(path, token);
                    var pixels = await CaptureTensor(asset, token);
                    var job = Path.Combine(ProjectRoot(), "Library", "UniBridge", "VisualAssetSearch", "jobs", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(job);
                    string expectedTensorHash = null;
                    JObject encoded;
                    try
                    {
                        string file = Path.Combine(job, "image.tensor");
                        var bytes = new byte[pixels.Length * sizeof(float)]; Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length); File.WriteAllBytes(file, bytes);
                        using (var hash = SHA256.Create()) expectedTensorHash = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
                        encoded = await RunWorker(new JObject { ["action"] = "encode_image", ["tensorFile"] = file, ["jobRoot"] = job }, Remaining(timeout, watch), token);
                    }
                    finally
                    {
                        if (!CleanupJob(job, expectedTensorHash))
                            ((JArray)data["failures"]).Add(new JObject { ["guid"] = guid, ["code"] = "JOB_CLEANUP_PRESERVED", ["error"] = "Unexpected changed/reparse job content retained; original operation outcome is preserved." });
                    }
                    if (AssetDatabase.GUIDToAssetPath(guid) != path || AssetDatabase.GetAssetDependencyHash(path).ToString() != dependency || SourceHash(path, token) != sourceHash)
                        throw new InvalidDataException("Asset changed while its visual embedding was generated; no index entry published.");
                    var vector = encoded["embedding"] as JArray; ValidateVector(vector);
                    var old = items.OfType<JObject>().FirstOrDefault(item => item.Value<string>("guid") == guid && item.Value<long>("localFileId") == localId);
                    if (old != null) items.Remove(old);
                    if (items.Count >= MaxEntries) throw new InvalidDataException("Visual index holds 2000 entries; explicitly ClearIndex or narrow the index.");
                    var entry = new JObject { ["guid"] = guid, ["localFileId"] = localId, ["dependencyHash"] = dependency,
                        ["sourceSha256"] = sourceHash, ["embedding"] = vector.DeepClone() };
                    items.Add(entry); SaveIndex(items, token); data["indexedCount"] = items.Count;
                    ((JArray)data["results"]).Add(Result(entry, asset, path, null));
                    data["processed"] = data.Value<int>("processed") + 1;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                { ((JArray)data["failures"]).Add(new JObject { ["guid"] = guid ?? "", ["code"] = "ASSET_NOT_INDEXED", ["error"] = error.Message }); }
                await EditorTask.Yield();
            }
            data["indexedCount"] = items.Count;
            bool complete = references.Count <= offset + cap && ((JArray)data["failures"]).Count == 0;
            data["complete"] = complete;
            return complete ? Success("Visual embeddings indexed from actual asset pixels.", data, watch)
                : Error("VISUAL_INDEX_PARTIAL", "Indexing reached its asset cap or some assets failed; successful entries remain in disposable cache.", data, watch);
        }

        static async Task<object> Search(JObject input, JArray items, JObject data, int timeout, Stopwatch watch, CancellationToken token)
        {
            string query = input.Value<string>("Query");
            if (string.IsNullOrWhiteSpace(query)) return Error("QUERY_REQUIRED", "Search requires an English visual-content Query.", data, watch);
            if (items.Count == 0) return Error("VISUAL_INDEX_EMPTY", "Index actual asset pixels before searching.", data, watch);
            var encoded = await RunWorker(new JObject { ["action"] = "encode_text", ["text"] = query }, Remaining(timeout, watch), token);
            var vector = encoded["embedding"] as JArray; ValidateVector(vector);
            var queryVector = vector.Values<float>().ToArray();
            var results = new List<JObject>();
            data["totalCandidates"] = items.Count;
            foreach (JObject entry in items)
            {
                token.ThrowIfCancellationRequested();
                string guid = entry.Value<string>("guid"), path = AssetDatabase.GUIDToAssetPath(guid);
                Object asset = null;
                if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) &&
                    AssetDatabase.GetAssetDependencyHash(path).ToString() == entry.Value<string>("dependencyHash") && SourceHashForSearch(path, token) == entry.Value<string>("sourceSha256"))
                    asset = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(candidate => MatchesAssetIdentity(candidate, guid, entry.Value<long>("localFileId")));
                if (asset == null) { data["staleCount"] = data.Value<int>("staleCount") + 1; continue; }
                var values = ((JArray)entry["embedding"]).Values<float>().ToArray();
                double score = 0; for (int i = 0; i < 512; i++) score += (double)queryVector[i] * values[i];
                if (score >= (input.Value<double?>("MinScore") ?? -1)) results.Add(Result(entry, asset, path, Math.Max(-1, Math.Min(1, score))));
                data["processed"] = data.Value<int>("processed") + 1;
                if (data.Value<int>("processed") % 10 == 0) await EditorTask.Yield();
            }
            data["results"] = new JArray(results.OrderByDescending(result => result.Value<double>("score")).ThenBy(result => result.Value<string>("guid"), StringComparer.Ordinal).Take(Math.Max(1, Math.Min(50, input.Value<int?>("Limit") ?? 10))));
            data["complete"] = data.Value<int>("staleCount") == 0;
            return data.Value<bool>("complete") ? Success("Assets ranked by actual local CLIP visual embeddings.", data, watch)
                : Error("VISUAL_INDEX_STALE", "Changed/deleted assets were excluded; reindex their GUID identities for complete coverage.", data, watch);
        }
        static bool MatchesAssetIdentity(Object candidate, string expectedGuid, long expectedLocalId) =>
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out string actualGuid, out long actualLocalId) &&
            actualLocalId == expectedLocalId && string.Equals(actualGuid, expectedGuid, StringComparison.OrdinalIgnoreCase);
        static JObject Result(JObject entry, Object asset, string path, double? score) => new()
        { ["guid"] = entry["guid"].DeepClone(), ["localFileId"] = entry["localFileId"].DeepClone(), ["path"] = path,
            ["name"] = asset.name, ["type"] = asset.GetType().FullName, ["score"] = score.HasValue ? new JValue(score.Value) : JValue.CreateNull(),
            ["dependencyHash"] = entry["dependencyHash"].DeepClone(), ["sourceSha256"] = entry["sourceSha256"].DeepClone() };
        static bool CleanupJob(string job, string expectedHash)
        {
            try
            {
                if (!Directory.Exists(job)) return true;
                string root = Path.GetFullPath(Path.Combine(ProjectRoot(), "Library", "UniBridge", "VisualAssetSearch", "jobs")) + Path.DirectorySeparatorChar;
                string absolute = Path.GetFullPath(job);
                if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(Path.GetFileName(job), "N", out _) ||
                    (File.GetAttributes(job) & FileAttributes.ReparsePoint) != 0) return false;
                var entries = Directory.GetFileSystemEntries(job);
                if (entries.Length == 0) { Directory.Delete(job); return true; }
                string file = Path.Combine(job, "image.tensor");
                if (entries.Length != 1 || entries[0] != file || expectedHash == null ||
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
                using (var stream = File.OpenRead(file)) using (var hash = SHA256.Create())
                    if (BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "") != expectedHash) return false;
                File.Delete(file); Directory.Delete(job); return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        static void ValidateVector(JArray vector)
        {
            if (vector == null || vector.Count != 512) throw new InvalidDataException("Expected 512-dimensional visual embedding.");
            double length = 0;
            foreach (var value in vector)
            { double n = value.Value<double>(); if (double.IsNaN(n) || double.IsInfinity(n)) throw new InvalidDataException("Non-finite embedding."); length += n * n; }
            if (Math.Abs(length - 1) > .001) throw new InvalidDataException("Visual embedding is not normalized.");
        }
        static string SourceHash(string assetPath, CancellationToken token)
        {
            string full = Path.GetFullPath(Path.Combine(ProjectRoot(), assetPath));
            using var stream = File.OpenRead(full);
            if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Asset source exceeds 64 MiB visual observation budget.");
            using var hash = SHA256.Create(); var bytes = new byte[65536]; int read;
            while ((read = stream.Read(bytes, 0, bytes.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.TransformBlock(bytes, 0, read, bytes, 0); }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
        }
        static string SourceHashForSearch(string assetPath, CancellationToken token)
        {
            try { return SourceHash(assetPath, token); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        static async Task<float[]> CaptureTensor(Object asset, CancellationToken token)
        {
            LastCaptureThreadId = Thread.CurrentThread.ManagedThreadId;
            LastRenderTextureRestored = LastTextureDestroyed = LastTemporaryReleased = false;
            Texture texture = asset as Texture2D;
            if (asset is Sprite sprite) texture = null; // Actual sprite pixels require its rendered asset preview, not the whole atlas.
            for (int attempt = 0; texture == null && attempt < 60; attempt++)
            {
                token.ThrowIfCancellationRequested(); texture = AssetPreview.GetAssetPreview(asset);
                if (texture == null) await EditorTask.Delay(50, token);
            }
            if (texture == null) throw new InvalidDataException("Actual visual preview unavailable; generic type icons are not semantic input.");
            var previous = RenderTexture.active; RenderTexture target = null; Texture2D copy = null;
            try
            {
                target = RenderTexture.GetTemporary(224, 224, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                RenderTexture.active = target; GL.Clear(true, true, Color.white);
                float width = texture.width, height = texture.height;
                Vector2 scale = width >= height ? new Vector2(height / width, 1) : new Vector2(1, width / height);
                Vector2 offset = (Vector2.one - scale) * .5f;
                Graphics.Blit(texture, target, scale, offset);
                copy = new Texture2D(224, 224, TextureFormat.RGBA32, false, false);
                copy.ReadPixels(new Rect(0, 0, 224, 224), 0, 0); copy.Apply(false, false);
                var colors = copy.GetPixels32(); var values = new float[3 * 224 * 224];
                float[] mean = { .48145466f, .4578275f, .40821073f }, std = { .26862954f, .26130258f, .27577711f };
                for (int y = 0; y < 224; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < 224; x++)
                    {
                        Color32 c = colors[(223 - y) * 224 + x]; float alpha = c.a / 255f;
                        float r = c.r / 255f * alpha + 1 - alpha, g = c.g / 255f * alpha + 1 - alpha, b = c.b / 255f * alpha + 1 - alpha;
                        int position = y * 224 + x;
                        values[position] = (r - mean[0]) / std[0]; values[224 * 224 + position] = (g - mean[1]) / std[1]; values[2 * 224 * 224 + position] = (b - mean[2]) / std[2];
                    }
                }
                return values;
            }
            finally
            {
                RenderTexture.active = previous; LastRenderTextureRestored = ReferenceEquals(RenderTexture.active, previous);
                if (copy != null) Object.DestroyImmediate(copy); LastTextureDestroyed = copy == null;
                if (target != null) RenderTexture.ReleaseTemporary(target); LastTemporaryReleased = true;
            }
        }

        static async Task<JObject> RunWorker(JObject request, int timeout, CancellationToken token)
        {
            string package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VisualAssetSearch).Assembly)?.resolvedPath;
            if (string.IsNullOrEmpty(package)) throw new InvalidOperationException("Installed UniBridge package could not be identified.");
            string rid, filename;
            if (Application.platform == RuntimePlatform.WindowsEditor) { rid = "win-x64"; filename = "unibridge_visual_worker.exe"; }
            else if (Application.platform == RuntimePlatform.LinuxEditor) { rid = "linux-x64"; filename = "unibridge_visual_worker"; }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            { rid = SystemInfo.processorType.IndexOf("Apple", StringComparison.OrdinalIgnoreCase) >= 0 ? "osx-arm64" : "osx-x64"; filename = "unibridge_visual_worker"; }
            else throw new PlatformNotSupportedException("Visual semantic provider is unavailable on this Editor platform.");
            string worker = Path.Combine(package, "VisualWorker~", rid, filename);
            if (!File.Exists(worker)) throw new FileNotFoundException("VISUAL_WORKER_NOT_INSTALLED: optional companion binary is unavailable.");
            request["requestId"] = Guid.NewGuid().ToString("N"); request["deadlineMs"] = timeout;
            return await Task.Run(() =>
            {
                using var process = new Process { StartInfo = new ProcessStartInfo { FileName = worker, Arguments = "--stdio",
                    WorkingDirectory = Path.GetDirectoryName(worker), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new InvalidOperationException("Visual worker failed to start.");
                Volatile.Write(ref ActiveWorkerPid, process.Id);
                Exception operationFailure = null;
                try
                {
                    using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } });
                    process.StandardInput.WriteLine(McpJson.SerializeObject(request)); process.StandardInput.Close();
                    var stdout = ReadBounded(process.StandardOutput, 32768);
                    var stderr = ReadBounded(process.StandardError, 8192);
                    if (!process.WaitForExit(timeout + 2000)) { try { process.Kill(); } catch { } throw new TimeoutException("Visual worker observation deadline exceeded."); }
                    token.ThrowIfCancellationRequested();
                    string output = stdout.GetAwaiter().GetResult(); string error = stderr.GetAwaiter().GetResult();
                    var result = JObject.Parse(output);
                    if (result.Value<string>("requestId") != request.Value<string>("requestId") || result.Value<string>("modelFingerprint") != Fingerprint)
                        throw new InvalidDataException("Visual worker response identity/model mismatch.");
                    if (process.ExitCode != 0 || result.Value<bool?>("success") != true)
                        throw new InvalidOperationException(result.Value<string>("code") + ": " + result.Value<string>("error") + (string.IsNullOrEmpty(error) ? "" : " (worker stderr retained)"));
                    return result;
                }
                catch (Exception error) { operationFailure = error; throw; }
                finally
                {
                    Exception cleanupFailure = null;
                    bool exited = false;
                    try
                    {
                        if (!process.HasExited) process.Kill();
                        exited = process.WaitForExit(3000);
                        if (!exited) cleanupFailure = new TimeoutException("Own visual worker did not physically exit within its cleanup bound.");
                    }
                    catch (Exception error) { cleanupFailure = error; try { exited = process.HasExited; } catch { } }
                    if (exited) Volatile.Write(ref ActiveWorkerPid, 0);
                    else if (cleanupFailure != null)
                    {
                        if (operationFailure != null) throw new AggregateException("Visual operation and own child cleanup failed; both outcomes retained.", operationFailure, cleanupFailure);
                        throw new IOException("Own visual child cleanup failed; its PID remains observable.", cleanupFailure);
                    }
                }
            }, token).ConfigureAwaitMainThread();
        }
        static async Task<string> ReadBounded(StreamReader reader, int limit)
        {
            var buffer = new char[1024]; var text = new System.Text.StringBuilder(); int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            { if (text.Length + read > limit) throw new InvalidDataException("Visual worker output exceeded its protocol bound."); text.Append(buffer, 0, read); }
            return text.ToString();
        }
    }
}
