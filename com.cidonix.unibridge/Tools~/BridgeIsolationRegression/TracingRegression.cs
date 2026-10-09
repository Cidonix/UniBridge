using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.Tracing;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Runtime.Serialization;

namespace Cidonix.UniBridge.BridgeIsolationRegression;

static class TracingRegression
{
    public static string Storage;
    sealed class CaptureSink : ITraceSink
    {
        public string Name => "owned-capture";
        public TraceConfig Config { get; set; } = new() { DefaultLevel = "debug", FilterRecurring = false };
        public readonly List<TraceEvent> Events = new();
        public void Write(TraceEvent evt) => Events.Add(evt);
    }
    sealed class LongIdConverter : JsonConverter<long>
    {
        public int Calls;
        public override void WriteJson(JsonWriter writer, long value, JsonSerializer serializer) => writer.WriteValue(value);
        public override long ReadJson(JsonReader reader, Type type, long existing, bool hasExisting, JsonSerializer serializer) { Calls++; return Convert.ToInt64(reader.Value) + 100; }
    }
    static void Poison(Action action)
    {
        var before = JsonConvert.DefaultSettings;
        var calls = 0;
        Func<JsonSerializerSettings> poison = () => { calls++; throw new InvalidOperationException("Owned throwing global JSON factory"); };
        try { JsonConvert.DefaultSettings = poison; action(); Check.Assert(calls == 0, "Actual producer read global settings factory"); }
        finally { JsonConvert.DefaultSettings = before; }
        Check.Assert(ReferenceEquals(JsonConvert.DefaultSettings, before), "Prior global callback not exactly restored");
    }
    static TraceEvent FixedEvent() => new()
    {
        ts = "2026-10-09T01:02:03.004Z", spanId = "golden-span", component = "unity", name = "bridge.ready", kind = "event", level = "info",
        data = JObject.Parse("{\"message\":\"Діагностика\",\"count\":3,\"empty\":null}"), recurring = false,
        exception = new IOException("Ignored original exception")
    };
    static void ResetConfig()
    {
        typeof(TraceSinkConfigManager).GetField("s_Data", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
    }
    static string NormalizeEvent(TraceEvent evt)
    {
        var value = McpJson.ObjectFromObject(evt);
        value["ts"] = "normalized-timestamp"; value["spanId"] = "normalized-span";
        return value.ToString(Formatting.None);
    }
    public static void Run(List<CaseResult> cases)
    {
        Check.Run(cases, "Tracing FileSink: fixed native producer bytes survive throwing global factory", () =>
        {
            var neutral = Path.Combine(Storage, "trace-neutral");
            var poisoned = Path.Combine(Storage, "trace-poisoned");
            new FileSink(neutral, new TraceConfig()).Write(FixedEvent());
            Poison(() => new FileSink(poisoned, new TraceConfig()).Write(FixedEvent()));
            var before = File.ReadAllBytes(Path.Combine(neutral, "traces.jsonl"));
            var after = File.ReadAllBytes(Path.Combine(poisoned, "traces.jsonl"));
            Check.Assert(before.AsSpan().SequenceEqual(after), "Actual FileSink output byte parity changed");
            var content = Encoding.UTF8.GetString(after);
            Check.Assert(content.EndsWith("\n") && !content.Contains("exception") && !content.Contains("sessionId"), "FileSink private null/ignore contract changed");
            Check.Assert(JObject.Parse(content)["data"]["message"].Value<string>() == "Діагностика", "FileSink lost structured event");
        });
        Check.Run(cases, "Tracing TraceWriter: message merge and typed event payload survive throwing globals", () =>
        {
            var neutral = new CaptureSink(); var poisoned = new CaptureSink();
            var writer = new TraceWriter("unity", Path.Combine(Storage, "writer-neutral")); writer.AddSink(neutral);
            writer.Log(new TraceEventOptions { Message = "Started", Data = new { Count = 3, Empty = (string)null }, Level = "info" });
            Poison(() => { var other = new TraceWriter("unity", Path.Combine(Storage, "writer-poisoned")); other.AddSink(poisoned); other.Log(new TraceEventOptions { Message = "Started", Data = new { Count = 3, Empty = (string)null }, Level = "info" }); });
            Check.Assert(neutral.Events.Count == 1 && poisoned.Events.Count == 1, "Writer swallowed poison into silent missing event");
            Check.Assert(NormalizeEvent(neutral.Events[0]) == NormalizeEvent(poisoned.Events[0]), "Nonvolatile trace event changed");
            Check.Assert(poisoned.Events[0].data["Count"].Value<int>() == 3 && poisoned.Events[0].data["message"].Value<string>() == "Started", "Typed message merge changed");
        });
        Check.Run(cases, "Tracing TraceWriter: artifact bytes and reference emitted under throwing global factory", () =>
        {
            var sink = new CaptureSink();
            var root = Path.Combine(Storage, "trace-artifact");
            var payload = new { Message = new string('x', 600), Count = 17 };
            var expected = McpJson.SerializeObject(payload);
            Poison(() => { var writer = new TraceWriter("unity", root, artifactThreshold: 80); writer.AddSink(sink); writer.Event("bridge.large", new TraceEventOptions { Data = payload }); });
            Check.Assert(sink.Events.Count == 1, "Artifact writer silently swallowed poisoned serialization");
            var data = sink.Events[0].data;
            var path = Path.Combine(root, "artifacts", data["_artifact"].Value<string>());
            Check.Assert(File.ReadAllText(path) == expected && data["size"].Value<int>() == expected.Length, "Artifact payload/reference mismatch");
        });
        Check.Run(cases, "Tracing config: actual settings persistence/load remains isolated with exact neutral bytes", () =>
        {
            UnityEditor.EditorUserSettings.Values.Clear(); ResetConfig();
            var config = new TraceConfig { DefaultLevel = "debug", FilterRecurring = false, Categories = new() { ["mcp"] = "info" }, Sessions = new() { ["owned-session"] = "warn" } };
            TraceSinkConfigManager.SetSinkConfig("relay.file", config);
            var neutral = UnityEditor.EditorUserSettings.Values["Trace.SinkConfigs"];
            UnityEditor.EditorUserSettings.Values.Clear(); ResetConfig();
            Poison(() => { TraceSinkConfigManager.SetSinkConfig("relay.file", config); ResetConfig(); var loaded = TraceSinkConfigManager.GetSinkConfig("relay.file"); Check.Assert(loaded.DefaultLevel == "debug" && !loaded.FilterRecurring && loaded.Categories["mcp"] == "info" && loaded.Sessions["owned-session"] == "warn", "Config load fell back to defaults"); });
            Check.Assert(UnityEditor.EditorUserSettings.Values["Trace.SinkConfigs"] == neutral, "Persisted config bytes changed");
        });
        Check.Run(cases, "Tracing relay config: actual config-file producer preserves neutral bytes under poison", () =>
        {
            var neutral = Path.Combine(Storage, "config-neutral"); var poisoned = Path.Combine(Storage, "config-poisoned");
            TraceConfigFileWriter.WriteTraceConfigFile(neutral);
            Poison(() => TraceConfigFileWriter.WriteTraceConfigFile(poisoned));
            Check.Assert(File.ReadAllBytes(Path.Combine(neutral, "trace-config.json")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(poisoned, "trace-config.json"))), "Actual relay config producer bytes differ");
            var json = JObject.Parse(File.ReadAllText(Path.Combine(poisoned, "trace-config.json")));
            Check.Assert(json["file"]["categories"]["mcp"].Value<string>() == "info" && json["console"]["defaultLevel"].Value<string>() == "info", "Relay config shape/defaults changed");
        });
        Check.Run(cases, "Runtime Unity ID converter: provided serializer controls ID conversion despite poisoned globals", () =>
        {
            var obj = new UnityEngine.Object { Id = 421, name = "owned-typed-object" };
            UnityEditor.EditorUtility.Objects.Clear(); UnityEditor.EditorUtility.Objects[obj.Id] = obj;
            var idConverter = new LongIdConverter();
            var serializer = JsonSerializer.Create(new JsonSerializerSettings { Converters = { new UnityEngineObjectConverter(), idConverter } });
            UnityEngine.Object result = null;
            Poison(() => { using var text = new StringReader("{\"instanceID\":321,\"name\":\"Ignored label\"}"); using var reader = new JsonTextReader(text); result = serializer.Deserialize<UnityEngine.Object>(reader); });
            Check.Assert(ReferenceEquals(result, obj) && idConverter.Calls == 1 && UnityEditor.EditorUtility.LastRequestedId == 421, "Converter ignored supplied serializer or typed ID identity");
        });
        Check.Run(cases, "Runtime Unity ID converter: null and typed mismatch remain truthful", () =>
        {
            var serializer = JsonSerializer.Create(new JsonSerializerSettings { Converters = { new UnityEngineObjectConverter() } });
            Poison(() => { using var text = new StringReader("null"); using var reader = new JsonTextReader(text); Check.Assert(serializer.Deserialize<UnityEngine.Object>(reader) == null, "Null object ID semantics changed"); });
        });
        Check.Run(cases, "Runtime Unity6 EntityId converter preserves a 64bit ID above JSON double precision", () =>
        {
            const long id = 9007199254740993;
            var obj = new UnityEngine.Object { name = "owned-wide-id", LongId = id }; UnityEditor.EditorUtility.EntityObjects[id] = obj;
            var serializer = JsonSerializer.Create(new JsonSerializerSettings { Converters = { new UnityEngineObjectConverter() } }); UnityEngine.Object result = null;
            Poison(() => { using var text = new StringReader("{\"instanceID\":9007199254740993}"); using var reader = new JsonTextReader(text); result = serializer.Deserialize<UnityEngine.Object>(reader); });
            Check.Assert(ReferenceEquals(result, obj) && UnityEditor.EditorUtility.LastRequestedObjectId == id, "Long EntityId was rounded, truncated, or looked up by name");
        });
    }
}
