using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Cidonix.UniBridge.MCP.Editor.Connection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Models;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Cidonix.UniBridge.BridgeIsolationRegression
{
    /// <summary>
    /// Inputs from the immutable neutral JSON baseline. Persisted private models remain
    /// opaque: the runner compiles their candidate declarations and producers unchanged
    /// and supplies adapters. No simplified replacement persistence implementation is used.
    /// </summary>
    public static class JsonFixtures
    {
        public static string GoldenDirectory;
        static Func<string, object> parseState, parseToken, parsePreview, parseSemantic, parseDiscovery, parseSnapshot;
        static Func<object, string> persistState, persistToken, persistSnapshot;
        static JsonSerializerSettings snapshotSettings;

        public static void ConfigurePersistedProducers(
            Func<string, object> stateParser, Func<string, object> tokenParser,
            Func<string, object> previewParser, Func<string, object> semanticParser,
            Func<string, object> discoveryParser, Func<string, object> snapshotParser,
            Func<object, string> stateWriter, Func<object, string> tokenWriter,
            Func<object, string> snapshotWriter, JsonSerializerSettings privateSnapshotSettings)
        {
            parseState = stateParser; parseToken = tokenParser; parsePreview = previewParser;
            parseSemantic = semanticParser; parseDiscovery = discoveryParser; parseSnapshot = snapshotParser;
            persistState = stateWriter; persistToken = tokenWriter; persistSnapshot = snapshotWriter;
            snapshotSettings = privateSnapshotSettings;
        }

        public static readonly string[] GoldenNames =
        {
            "protocol-handshake", "protocol-minimal-tools", "protocol-success", "protocol-stopped",
            "protocol-progress", "command-model", "discovery-model", "worksession-state",
            "worksession-write-token", "worksession-preview", "worksession-semantic", "snapshot-state",
            "typed-parameters"
        };

        static readonly Dictionary<string, string> BaselineHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["protocol-handshake"] = "58673e2569999356181e2bdc5a8e8dd9147d9763cfa8caa13ecd230163f6cd81",
            ["protocol-minimal-tools"] = "a2d2054166e420e961c37afa2f481958a8e8002044c31ad6b48731f60dfe601a",
            ["protocol-success"] = "1f84844787bb896befa60488ff3662a0ed0de5f8a0da7a5f34a76c8eca97d790",
            ["protocol-stopped"] = "878b16bc42edfb0f46e77beb634f9cf681ea51f90727b805066b208cfe7c8d53",
            ["protocol-progress"] = "732a9d327397dc81efc88b42c97d38d93201f1285a8a4243f0f7d36ce7d032fd",
            ["command-model"] = "d9dfeaa44bb083e6887fcddb769ac00cda751f16cb16da22a05bfaaa9e5d4b80",
            ["discovery-model"] = "4fc223ef262a935b34d5d5ee27096c6e497655136d617c6c510739818905338b",
            ["worksession-state"] = "99f7f7b754831b5135f1983d3d0db82298d01fc25be02e625e45b8926693b9d4",
            ["worksession-write-token"] = "c98051e6cd1f6c771d18edb43e611798fdf58a20ca9f0166066a3c19292a19e0",
            ["worksession-preview"] = "6394ed8a01e46a9aa634fcbe98de1accc5e7746808e08d4eb8ab20259de00555",
            ["worksession-semantic"] = "a9a87d2d5eaf81b1c8bb4f80a4661c4e17e6e898659d7a939dba90f147fc3dc0",
            ["snapshot-state"] = "b421fb121d2d79cb6125c6b82c2b49c1283090577a5729620b067b9492380d1f",
            ["typed-parameters"] = "140264936010a914f8328e6a5f0ab40a2e0af7a0bf45bc2f067ee879a4067cbe"
        };

        public const string CommandInput = "{\"type\":\"UniBridge_Golden\",\"params\":{\"Label\":\"2026-10-09T01:02:03Z\",\"Count\":3},\"requestId\":\"golden-request\",\"futureExtension\":true}";
        public const string DiscoveryInput = "{\"connection_type\":\"named_pipe\",\"connection_path\":\"audit-only\",\"created_date\":\"2026-10-09T01:02:03.0000000Z\",\"project_path\":\"X:/Golden/Assets\",\"project_id\":\"00000000-0000-0000-0000-000000000001\",\"project_name\":\"Домовик Golden\",\"project_root\":\"X:/Golden\",\"protocol_version\":\"2.0\",\"editor_pid\":4242}";
        public const string StateInput = "{\"Version\":2,\"SessionId\":\"golden-session\",\"Name\":\"Робоча сесія \\\"Golden\\\"\",\"ProjectRoot\":\"X:/Golden\",\"UnityVersion\":\"6000.6.5f1\",\"StartedUtc\":\"2026-10-09T01:02:03.0000000Z\",\"Options\":{},\"Baseline\":{\"FileCount\":2,\"CapturedFiles\":1,\"CapturedBytes\":9007199254740993,\"ScanComplete\":true,\"Warnings\":[\"Зразок\"]},\"SemanticBaseline\":{\"Enabled\":true,\"SceneCount\":1,\"ObjectCount\":2,\"SnapshotPath\":\"semantic.json\"},\"Files\":{\"Assets/A.cs\":{\"Path\":\"Assets/A.cs\",\"Exists\":true,\"SizeBytes\":21,\"Sha256\":\"aa\",\"TextLike\":true,\"Captured\":true,\"CapturePath\":\"captures/aa.cs\"},\"Assets/A.cs.meta\":{\"Path\":\"Assets/A.cs.meta\",\"Exists\":false}},\"OwnedWrites\":{\"Assets/A.cs\":{\"BeforeExists\":true,\"BeforeSha256\":\"aa\",\"AfterExists\":true,\"AfterSha256\":\"bb\",\"OperationId\":\"op-1\",\"Source\":\"golden external writer\",\"RecordedUtc\":\"2026-10-09T01:03:04Z\"}}}";
        public const string TokenInput = "{\"SessionId\":\"golden-session\",\"TokenId\":\"golden-token\",\"Source\":\"Known payload\",\"StartedUtc\":\"2026-10-09T01:02:03Z\",\"Completed\":false,\"Before\":{\"Assets/A.cs\":{\"Exists\":true,\"Sha256\":\"aa\"},\"Assets/New.cs\":{\"Exists\":false}}}";
        public const string PreviewInput = "{\"Fingerprint\":\"golden-fingerprint\",\"CreatedUtc\":\"2026-10-09T01:02:03Z\",\"Consumed\":true}";
        public const string SemanticInput = "{\"Version\":1,\"CaptureMode\":\"loaded-scenes\",\"CapturedUtc\":\"2026-10-09T01:02:03Z\",\"ProjectRoot\":\"X:/Golden\",\"UnityVersion\":\"6000.6.5f1\",\"MaxObjects\":30000,\"TotalObjects\":1,\"Scenes\":[{\"SceneName\":\"Golden\",\"ScenePath\":\"Assets/Golden.unity\",\"BuildIndex\":-1,\"IsDirty\":true,\"RootCount\":1,\"Objects\":[{\"ObjectId\":9007199254740993,\"Name\":\"Зразок\",\"ParentObjectId\":null,\"ActiveSelf\":true,\"ComponentTypes\":[\"UnityEngine.Transform\"]}]}]}";
        public const string SnapshotInput = "{\"snapshotId\":\"golden-snapshot\",\"name\":\"Збережений редактор\",\"createdUtc\":\"2026-10-09T01:02:03Z\",\"project\":{\"id\":\"golden\",\"name\":\"Golden\",\"root\":\"X:/Golden\",\"unityVersion\":\"6000.6.5f1\"},\"scenes\":{\"activeScenePath\":\"Assets/Golden.unity\",\"activeSceneName\":\"Golden\",\"loadedScenes\":[{\"path\":\"Assets/Golden.unity\",\"name\":\"Golden\",\"isLoaded\":true,\"isDirty\":true,\"buildIndex\":-1,\"rootCount\":2}]},\"sceneView\":{\"exists\":true,\"pivot\":{\"x\":1.25,\"y\":-2.5,\"z\":0},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"size\":10.5,\"orthographic\":true,\"in2DMode\":true},\"selection\":{\"activeGlobalId\":\"GlobalObjectId_V1-golden\",\"objects\":[]},\"prefabStage\":{\"isOpen\":false,\"isDirty\":false},\"prefabAutoSave\":{\"prefabModeAllowAutoSave\":false,\"stageNavigationAutoSave\":null},\"windows\":[],\"dockTabs\":[]}";
        public const string TypedInput = "{\"Count\":3,\"Label\":\"Зразок\",\"Mode\":\"Execute\",\"futureExtension\":true}";

        public static byte[] ExpectedBytes(string name)
        {
            if (string.IsNullOrEmpty(GoldenDirectory)) throw new InvalidOperationException("Runner must configure immutable GoldenDirectory.");
            var bytes = File.ReadAllBytes(Path.Combine(GoldenDirectory, name + ".json"));
            Check.Assert(BaselineHashes.TryGetValue(name, out var baselineHash) && Hash(bytes) == baselineHash,
                "Immutable neutral baseline bytes changed: " + name);
            return bytes;
        }

        public static object ParsePersistedSnapshot(string input) => Required(parseSnapshot)(input);

        public static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public static byte[] ProduceGolden(string name, out byte[] actualPersistedBytes)
        {
            actualPersistedBytes = null;
            object model;
            string text;
            switch (name)
            {
                case "protocol-handshake":
                    var tools = Tools();
                    var minimal = MinimalTools(tools);
                    using (var sha = SHA256.Create())
                    {
                        var toolsHash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(McpJson.SerializeObject(minimal, Formatting.None))));
                        text = MessageProtocol.CreateHandshakeMessage(tools, toolsHash);
                    }
                    break;
                case "protocol-minimal-tools": text = McpJson.SerializeObject(MinimalTools(Tools())); break;
                case "protocol-success": text = McpJson.SerializeObject(new { status = "success", result = new { message = "pong" } }); break;
                case "protocol-stopped": text = McpJson.SerializeObject(new { status = "error", error = "Bridge stopped" }); break;
                case "protocol-progress": text = MessageProtocol.CreateCommandInProgressMessage(); break;
                case "command-model": text = McpJson.SerializeObject(McpJson.DeserializeObject<Command>(CommandInput)); break;
                case "discovery-model": text = McpJson.SerializeObject(Required(parseDiscovery)(DiscoveryInput), Formatting.Indented); break;
                case "worksession-state":
                    model = Required(parseState)(StateInput);
                    text = McpJson.SerializeObject(model, Formatting.Indented);
                    actualPersistedBytes = File.ReadAllBytes(Required(persistState)(model));
                    break;
                case "worksession-write-token":
                    model = Required(parseToken)(TokenInput);
                    text = McpJson.SerializeObject(model, Formatting.Indented);
                    actualPersistedBytes = File.ReadAllBytes(Required(persistToken)(model));
                    break;
                case "worksession-preview": text = McpJson.SerializeObject(Required(parsePreview)(PreviewInput), Formatting.Indented); break;
                case "worksession-semantic": text = McpJson.SerializeObject(Required(parseSemantic)(SemanticInput)); break;
                case "snapshot-state":
                    model = Required(parseSnapshot)(SnapshotInput);
                    if (snapshotSettings == null) throw new InvalidOperationException("Runner must supply unchanged private EditorSnapshot JSON settings.");
                    text = McpJson.SerializeObject(model, snapshotSettings);
                    actualPersistedBytes = File.ReadAllBytes(Required(persistSnapshot)(model));
                    break;
                case "typed-parameters": text = McpJson.SerializeObject(JObject.Parse(TypedInput).ToObjectIndependent<ExampleParameters>()); break;
                default: throw new ArgumentOutOfRangeException(nameof(name));
            }
            return new UTF8Encoding(false).GetBytes(text);
        }

        static T Required<T>(T producer) where T : class => producer ?? throw new InvalidOperationException("Runner must configure unchanged candidate persisted model/producer adapters.");
        static object[] MinimalTools(McpToolInfo[] tools) => tools.Select(tool => (object)new { tool.name, tool.description, tool.inputSchema }).ToArray();
        static McpToolInfo[] Tools() => new[]
        {
            new McpToolInfo
            {
                name = "UniBridge_Golden", title = "Діагностика", description = "Рядок: \"лапки\", slash \\ і newline\n",
                inputSchema = JObject.Parse("{\"type\":\"object\",\"properties\":{\"Count\":{\"type\":\"integer\"}},\"additionalProperties\":false}"),
                outputSchema = new { type = "object" }, annotations = new { readOnlyHint = true, uniBridgeExecution = new { policy = "read" } }
            }
        };

        public enum ExampleMode { None, Preview, Execute }
        public class ExampleParameters { public int Count { get; set; } public string Label { get; set; } public ExampleMode Mode { get; set; } }
        public sealed class Probe
        {
            public string PascalName { get; set; } = "Зразок \\\"\n";
            public string NullMember { get; set; }
            public int DefaultMember { get; set; }
            public decimal DecimalValue { get; set; } = 123.4567890123456789m;
            public DateTime When { get; set; } = new DateTime(2026, 10, 9, 1, 2, 3, DateTimeKind.Utc);
            public ExampleMode Mode { get; set; } = ExampleMode.Execute;
        }

        public static JsonSerializerSettings HarmfulSettings() => new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy { ProcessDictionaryKeys = true, OverrideSpecifiedNames = true } },
            Converters = { new GlobalProbeConverter(), new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore,
            Culture = CultureInfo.GetCultureInfo("de-DE"), DateFormatString = "yyyy/MM/dd HH:mm",
            DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal,
            MissingMemberHandling = MissingMemberHandling.Error, Formatting = Formatting.Indented
        };

        sealed class GlobalProbeConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType) => objectType == typeof(Probe);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => writer.WriteValue("global-poison");
            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.StartObject || reader.TokenType == JsonToken.StartArray) reader.Skip();
                return new Probe { PascalName = "global-poison", DecimalValue = -1 };
            }
        }

        public sealed class PrivateValue
        {
            public string Value { get; set; }
        }

        public sealed class PrivateValueConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType) => objectType == typeof(PrivateValue);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => writer.WriteValue("private:" + ((PrivateValue)value).Value);
            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                var text = (string)reader.Value;
                if (text == null || !text.StartsWith("private:", StringComparison.Ordinal)) throw new JsonSerializationException("Private prefix required.");
                return new PrivateValue { Value = text.Substring(8) };
            }
        }

        public sealed class GlobalSettingsScope : IDisposable
        {
            readonly Func<JsonSerializerSettings> previous;
            bool disposed;
            public GlobalSettingsScope(Func<JsonSerializerSettings> replacement)
            {
                previous = JsonConvert.DefaultSettings;
                JsonConvert.DefaultSettings = replacement;
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                JsonConvert.DefaultSettings = previous;
                Check.Assert(ReferenceEquals(JsonConvert.DefaultSettings, previous), "Global callback reference was not restored exactly.");
            }
        }
    }
}
