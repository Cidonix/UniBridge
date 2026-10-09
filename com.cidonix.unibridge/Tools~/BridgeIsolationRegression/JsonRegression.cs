using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Cidonix.UniBridge.BridgeIsolationRegression
{
    public static class JsonRegression
    {
        public static readonly Dictionary<string, string> GoldenHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        public static int ConcurrentOperationCount, MaxConcurrentSerializerEntries, DistinctConcurrentSerializers;

        public static void Run(List<CaseResult> cases)
        {
            var originalGlobalCallback = JsonConvert.DefaultSettings;
            try
            {
                GoldenCases(cases, "neutral", null);
                int throwingCalls = 0;
                Func<JsonSerializerSettings> throwing = () => { Interlocked.Increment(ref throwingCalls); throw new InvalidOperationException("Global default factory fixture."); };
                Check.Run(cases, "JSON poison negative control invokes legacy factory", () =>
                {
                    using var scope = new JsonFixtures.GlobalSettingsScope(throwing);
                    Expect<InvalidOperationException>(() => JsonConvert.SerializeObject(new JsonFixtures.Probe()));
                    Check.Assert(throwingCalls == 1, "Installed throwing factory was not exercised by legacy control.");
                });

                int candidateStartCalls = throwingCalls;
                Check.Run(cases, "JSON all facade operations ignore throwing global accessor", () =>
                {
                    using var scope = new JsonFixtures.GlobalSettingsScope(throwing);
                    VerifyFacade();
                    Check.Assert(throwingCalls == candidateStartCalls, "Candidate called global default settings factory.");
                });
                GoldenCases(cases, "throwing-global", throwing);
                Check.Run(cases, "JSON persisted goldens never invoke throwing global accessor", () =>
                    Check.Assert(throwingCalls == candidateStartCalls, "Protocol or persistence producer called global settings factory."));

                int harmfulCalls = 0;
                Func<JsonSerializerSettings> harmful = () => { Interlocked.Increment(ref harmfulCalls); return JsonFixtures.HarmfulSettings(); };
                Check.Run(cases, "JSON harmful resolver converter enum null culture settings negative controls", () =>
                {
                    using var scope = new JsonFixtures.GlobalSettingsScope(harmful);
                    Check.Assert(JsonConvert.SerializeObject(new JsonFixtures.Probe()) == "\"global-poison\"", "Poison converter negative control did not alter output.");
                    var altered = JsonConvert.SerializeObject(new { PascalName = "value", NullMember = (string)null, DefaultMember = 0, Mode = JsonFixtures.ExampleMode.Execute, When = new DateTime(2026, 10, 9, 1, 2, 3, DateTimeKind.Utc) });
                    // '/' in a DateTime format uses the selected culture's date separator.
                    Check.Assert(altered.Contains("pascal_name") && !altered.Contains("null_member") && !altered.Contains("default_member") && altered.Contains("Execute") && altered.Contains("2026.10.09"), "Naming/null/default/enum/date poison negative control was ineffective: " + altered);
                    Check.Assert(harmfulCalls >= 2, "Harmful factory negative control was not invoked.");
                });
                int harmfulStartCalls = harmfulCalls;
                Check.Run(cases, "JSON facade keeps neutral semantics under harmful global settings", () =>
                {
                    using var scope = new JsonFixtures.GlobalSettingsScope(harmful);
                    VerifyFacade();
                    Check.Assert(harmfulCalls == harmfulStartCalls, "Candidate adopted harmful global settings.");
                });
                GoldenCases(cases, "harmful-global", harmful);
                Check.Run(cases, "JSON persisted goldens never invoke harmful global factory", () =>
                    Check.Assert(harmfulCalls == harmfulStartCalls, "Protocol or persistence producer used global settings factory."));

                ExplicitSettingsCases(cases, throwing);
                ScalarCases(cases, throwing);
                InputContractCases(cases, throwing);
                Check.Run(cases, "JSON 128 default serialization calls overlap inside getters under poison", () =>
                {
                    using var scope = new JsonFixtures.GlobalSettingsScope(throwing);
                    RunConcurrent(false);
                });
                cases[cases.Count - 1].Evidence = new { operations = ConcurrentOperationCount, maximumSimultaneousSerializationEntries = MaxConcurrentSerializerEntries, overlapGate = "default serialization property getter" };
                Check.Run(cases, "JSON 128 private-converter serialization calls use 128 independent serializers", () =>
                {
                    using var scope = new JsonFixtures.GlobalSettingsScope(throwing);
                    RunConcurrent(true);
                });
                cases[cases.Count - 1].Evidence = new { operations = ConcurrentOperationCount, maximumSimultaneousSerializationEntries = MaxConcurrentSerializerEntries, distinctSerializers = DistinctConcurrentSerializers, overlapGate = "converter inside serializer.Serialize" };
                Check.Run(cases, "JSON complete suite restores exact original global callback", () =>
                    Check.Assert(ReferenceEquals(JsonConvert.DefaultSettings, originalGlobalCallback), "Suite did not restore the exact original callback reference."));
            }
            finally
            {
                // The standalone fixture owns this process. Never leave poison active on failure.
                JsonConvert.DefaultSettings = originalGlobalCallback;
            }
        }

        static void GoldenCases(List<CaseResult> cases, string mode, Func<JsonSerializerSettings> factory)
        {
            foreach (var name in JsonFixtures.GoldenNames)
            {
                Check.Run(cases, "JSON immutable golden " + name + " / " + mode, () =>
                {
                    using var scope = new JsonFixtures.GlobalSettingsScope(factory);
                    var expected = JsonFixtures.ExpectedBytes(name);
                    var actual = JsonFixtures.ProduceGolden(name, out var persisted);
                    Check.Assert(expected.SequenceEqual(actual), "Byte contract changed for " + name + "; expected " + JsonFixtures.Hash(expected) + ", actual " + JsonFixtures.Hash(actual));
                    if (persisted != null)
                        Check.Assert(expected.SequenceEqual(persisted), "Actual candidate persistence producer bytes differ for " + name + "; actual " + JsonFixtures.Hash(persisted));
                    GoldenHashes[name] = JsonFixtures.Hash(expected);
                    if (name == "snapshot-state")
                        Check.Assert(!System.Text.Encoding.UTF8.GetString(actual).Contains("stageNavigationAutoSave"), "Private snapshot NullValueHandling.Ignore changed.");
                });
                cases[cases.Count - 1].Evidence = new
                {
                    artifact = name, globalSettingsMode = mode,
                    sha256 = GoldenHashes.TryGetValue(name, out var hash) ? hash : null,
                    actualPersistedProducerCompared = name == "worksession-state" || name == "worksession-write-token" || name == "snapshot-state"
                };
            }
        }

        static void VerifyFacade()
        {
            var probe = new JsonFixtures.Probe();
            // Capture the neutral expectation with the candidate's independent serializer,
            // then assert fixed token facts as well, so poison cannot define our expectation.
            var text = McpJson.SerializeObject(probe);
            var parsed = (JObject)ReadNeutralToken(text);
            Check.Assert(parsed.Value<string>("PascalName") == probe.PascalName && parsed["NullMember"].Type == JTokenType.Null && parsed.Value<int>("DefaultMember") == 0 && parsed.Value<int>("Mode") == 2, "Neutral names/null/default/enum contract changed.");
            Check.Assert(parsed.Value<decimal>("DecimalValue") == probe.DecimalValue, "Decimal precision changed.");
            Check.Assert(text.Contains("2026-10-09T01:02:03Z") && !text.Contains("global-poison"), "Global date/converter settings leaked.");
            Check.Assert(McpJson.SerializeObject(probe, Formatting.None) == text, "Formatting.None overload changed.");
            Check.Assert(McpJson.SerializeObject(probe, (JsonSerializerSettings)null) == text, "Null explicit-settings overload changed.");
            Check.Assert(McpJson.SerializeObject(probe, Formatting.None, null) == text, "Three-argument overload changed.");
            var indented = McpJson.SerializeObject(probe, Formatting.Indented);
            Check.Assert(indented.Contains(Environment.NewLine) && JToken.DeepEquals(ReadNeutralToken(indented), parsed), "Indented overload changed semantics.");
            Check.Assert(McpJson.SerializeObject(null) == "null", "Null payload semantics changed.");
            var bound = McpJson.DeserializeObject<JsonFixtures.Probe>(text);
            Check.Assert(bound.PascalName == probe.PascalName && bound.DecimalValue == probe.DecimalValue && bound.When == probe.When && bound.Mode == probe.Mode, "Deserialize adopted global converter/settings.");
            var objectToken = McpJson.ObjectFromObject(probe);
            Check.Assert(JToken.DeepEquals(objectToken, parsed), "ObjectFromObject adopted global settings.");
            var arrayToken = McpJson.ArrayFromObject(new[] { probe, probe });
            Check.Assert(arrayToken.Count == 2 && JToken.DeepEquals(arrayToken[0], parsed), "ArrayFromObject adopted global settings.");
            Check.Assert(JToken.DeepEquals(McpJson.TokenFromObject(probe), parsed), "TokenFromObject adopted global settings.");
            Check.Assert(objectToken.ToObjectIndependent<JsonFixtures.Probe>().PascalName == probe.PascalName, "Generic ToObject adopted global converter.");
            Check.Assert(((JsonFixtures.Probe)objectToken.ToObjectIndependent(objectType: typeof(JsonFixtures.Probe))).PascalName == probe.PascalName, "Named non-generic ToObject adopted global converter.");
            var serializerA = McpJson.CreateSerializer();
            var serializerB = McpJson.CreateSerializer();
            Check.Assert(!ReferenceEquals(serializerA, serializerB), "Serializer instance was reused.");
            serializerA.NullValueHandling = NullValueHandling.Ignore;
            serializerA.Converters.Add(new StringEnumConverter());
            Check.Assert(serializerB.NullValueHandling == NullValueHandling.Include && serializerB.Converters.Count == 0 && McpJson.SerializeObject(probe) == text, "Mutation leaked between operation serializers.");
        }

        static void ExplicitSettingsCases(List<CaseResult> cases, Func<JsonSerializerSettings> poison)
        {
            Check.Run(cases, "JSON explicit private serialization settings preserved and unmodified", () =>
            {
                using var scope = new JsonFixtures.GlobalSettingsScope(poison);
                var converter = new JsonFixtures.PrivateValueConverter();
                var resolver = new CamelCasePropertyNamesContractResolver();
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented, NullValueHandling = NullValueHandling.Ignore,
                    DefaultValueHandling = DefaultValueHandling.Ignore, ContractResolver = resolver,
                    DateFormatString = "yyyy/MM/dd", Culture = CultureInfo.GetCultureInfo("uk-UA"),
                    Converters = { converter, new StringEnumConverter() }
                };
                var value = new { PascalName = "A", Missing = (string)null, Zero = 0, Private = new JsonFixtures.PrivateValue { Value = "B" }, Mode = JsonFixtures.ExampleMode.Preview, When = new DateTime(2026, 10, 9) };
                var text = McpJson.SerializeObject(value, settings);
                Check.Assert(text.Contains(Environment.NewLine) && text.Contains("\"pascalName\": \"A\"") && text.Contains("\"private:B\"") && text.Contains("\"Preview\"") && text.Contains("2026.10.09") && !text.Contains("missing") && !text.Contains("zero"), "Explicit private settings were lost: " + text);
                var compact = McpJson.SerializeObject(value, Formatting.None, settings);
                Check.Assert(!compact.Contains(Environment.NewLine) && JToken.DeepEquals(JToken.Parse(text), JToken.Parse(compact)), "Explicit Formatting override changed private semantics.");
                var created = McpJson.CreateSerializer(settings);
                Check.Assert(ReferenceEquals(created.ContractResolver, resolver) && ReferenceEquals(created.Converters[0], converter), "Explicit serializer resolver/converter identity changed.");
                Check.Assert(settings.Formatting == Formatting.Indented && settings.NullValueHandling == NullValueHandling.Ignore && settings.Converters.Count == 2 && ReferenceEquals(settings.ContractResolver, resolver), "Operation mutated caller's private settings.");
            });
            Check.Run(cases, "JSON explicit private deserialization converter settings preserved", () =>
            {
                using var scope = new JsonFixtures.GlobalSettingsScope(poison);
                var settings = new JsonSerializerSettings { Converters = { new JsonFixtures.PrivateValueConverter() } };
                Check.Assert(McpJson.DeserializeObject<JsonFixtures.PrivateValue>("\"private:Зразок\"", settings).Value == "Зразок", "Private deserialization converter ignored.");
                Expect<JsonSerializationException>(() => McpJson.DeserializeObject<JsonFixtures.PrivateValue>("\"wrong\"", settings));
            });
        }

        static void ScalarCases(List<CaseResult> cases, Func<JsonSerializerSettings> poison)
        {
            Scalar(cases, poison, "int", "3", 3);
            Scalar(cases, poison, "long beyond JS safe integer", "9007199254740993", 9007199254740993L);
            Scalar(cases, poison, "decimal precision", "123.4567890123456789", 123.4567890123456789m);
            Scalar(cases, poison, "double", "1.25", 1.25d);
            Scalar(cases, poison, "bool", "true", true);
            Scalar(cases, poison, "Unicode string", "\"Зразок\"", "Зразок");
            Scalar<int?>(cases, poison, "nullable null", "null", null);
            Scalar(cases, poison, "Guid", "\"00000000-0000-0000-0000-000000000001\"", Guid.Parse("00000000-0000-0000-0000-000000000001"));
            Scalar(cases, poison, "UTC DateTime", "\"2026-10-09T01:02:03Z\"", new DateTime(2026, 10, 9, 1, 2, 3, DateTimeKind.Utc));
            Scalar(cases, poison, "DateTimeOffset", "\"2026-10-09T04:02:03+03:00\"", new DateTimeOffset(2026, 10, 9, 4, 2, 3, TimeSpan.FromHours(3)));
            Scalar(cases, poison, "TimeSpan", "\"01:02:03\"", new TimeSpan(1, 2, 3));
            Scalar(cases, poison, "enum string", "\"Preview\"", JsonFixtures.ExampleMode.Preview);
            Scalar(cases, poison, "enum integer", "2", JsonFixtures.ExampleMode.Execute);
            Check.Run(cases, "JSON optional generic and named non-generic tokens retain null conditional behavior", () =>
            {
                using var scope = new JsonFixtures.GlobalSettingsScope(poison);
                JToken missing = null;
                int? generic = missing?.ToObjectIndependent<int>();
                var boxed = missing?.ToObjectIndependent(objectType: typeof(int));
                Check.Assert(generic == null && boxed == null, "Null conditional binding changed.");
            });
            Check.Run(cases, "JSON complex typed binding retains neutral unknown-field and enum semantics", () =>
            {
                using var scope = new JsonFixtures.GlobalSettingsScope(poison);
                var token = JObject.Parse(JsonFixtures.TypedInput);
                var generic = token.ToObjectIndependent<JsonFixtures.ExampleParameters>();
                var boxed = (JsonFixtures.ExampleParameters)token.ToObjectIndependent(objectType: typeof(JsonFixtures.ExampleParameters));
                Check.Assert(generic.Count == 3 && generic.Label == "Зразок" && generic.Mode == JsonFixtures.ExampleMode.Execute && McpJson.SerializeObject(generic) == McpJson.SerializeObject(boxed), "Typed parameters adopted global missing-member/enum settings.");
            });
        }

        static void Scalar<T>(List<CaseResult> cases, Func<JsonSerializerSettings> poison, string name, string json, T expected)
        {
            Check.Run(cases, "JSON independent scalar " + name, () =>
            {
                using var scope = new JsonFixtures.GlobalSettingsScope(poison);
                using var text = new StringReader(json);
                using var reader = new JsonTextReader(text) { FloatParseHandling = FloatParseHandling.Decimal };
                var token = JToken.ReadFrom(reader);
                Check.Assert(EqualityComparer<T>.Default.Equals(token.ToObjectIndependent<T>(), expected), "Generic scalar value changed.");
                Check.Assert(EqualityComparer<T>.Default.Equals((T)token.ToObjectIndependent(objectType: typeof(T)), expected), "Named non-generic scalar value changed.");
                Check.Assert(EqualityComparer<T>.Default.Equals(McpJson.DeserializeObject<T>(json), expected), "Deserialized scalar value changed.");
            });
        }

        static void InputContractCases(List<CaseResult> cases, Func<JsonSerializerSettings> poison)
        {
            Check.Run(cases, "JSON deserialization accepts a single value and trailing whitespace/comments", () =>
            {
                using var scope = new JsonFixtures.GlobalSettingsScope(poison);
                Check.Assert(McpJson.DeserializeObject<int>(" 12 \r\n /*done*/ ") == 12, "Valid single value/comment rejected.");
                Check.Assert(McpJson.DeserializeObject<string>("null") == null, "JSON null changed.");
            });
            Check.Run(cases, "JSON deserialization rejects additional values and malformed input", () =>
            {
                using var scope = new JsonFixtures.GlobalSettingsScope(poison);
                Expect<JsonException>(() => McpJson.DeserializeObject<int>("12 13"));
                Expect<JsonException>(() => McpJson.DeserializeObject<JsonFixtures.ExampleParameters>("{} {}"));
                Expect<JsonException>(() => McpJson.DeserializeObject<JsonFixtures.ExampleParameters>("{\"Count\":"));
                Expect<ArgumentNullException>(() => McpJson.DeserializeObject<int>(null));
            });
            Check.Run(cases, "JSON explicit CheckAdditionalContent false preserves first-value reader policy", () =>
            {
                using var scope = new JsonFixtures.GlobalSettingsScope(poison);
                var settings = new JsonSerializerSettings { CheckAdditionalContent = false };
                Check.Assert(McpJson.DeserializeObject<int>("12 13", settings) == 12, "Explicit first-value reader policy was overridden.");
                Check.Assert(!settings.CheckAdditionalContent, "Caller reader settings were mutated.");
            });
            Check.Run(cases, "JSON explicit CheckAdditionalContent true rejects a second value", () =>
            {
                using var scope = new JsonFixtures.GlobalSettingsScope(poison);
                Expect<JsonException>(() => McpJson.DeserializeObject<int>("12 13", new JsonSerializerSettings { CheckAdditionalContent = true }));
            });
            Check.Run(cases, "JSON actual private snapshot reader retains strict single-value policy", () =>
            {
                using var scope = new JsonFixtures.GlobalSettingsScope(poison);
                Expect<JsonException>(() => JsonFixtures.ParsePersistedSnapshot(JsonFixtures.SnapshotInput + " {}"));
            });
        }

        static void Expect<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected exception " + typeof(T).Name + ".");
        }

        static JToken ReadNeutralToken(string json)
        {
            using var text = new StringReader(json);
            using var reader = new JsonTextReader(text) { FloatParseHandling = FloatParseHandling.Decimal };
            return JToken.ReadFrom(reader);
        }

        static void RunConcurrent(bool converterMode)
        {
            const int count = 128;
            ConcurrentOperationCount = 0;
            MaxConcurrentSerializerEntries = 0;
            DistinctConcurrentSerializers = 0;
            using var gate = new ConcurrentGate(count);
            var errors = new ConcurrentQueue<Exception>();
            var outputs = new string[count];
            var threads = new List<Thread>(count);
            var settings = converterMode ? new JsonSerializerSettings { Converters = { new ConcurrentConverter(gate) } } : null;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            outputs[index] = converterMode
                                ? McpJson.SerializeObject(new ConcurrentValue { Index = index }, settings)
                                : McpJson.SerializeObject(new GetterValue(gate, index));
                        }
                        catch (Exception ex) { errors.Enqueue(ex); }
                    }, 256 * 1024) { IsBackground = true, Name = "JSON regression " + i };
                    threads.Add(thread);
                    thread.Start();
                }
                Check.Assert(gate.Entered.Wait(TimeSpan.FromSeconds(20)), "128 serialization operations did not simultaneously enter their serializers; entered=" + gate.Maximum);
            }
            finally
            {
                gate.Release.Set();
                foreach (var thread in threads)
                    Check.Assert(thread.Join(TimeSpan.FromSeconds(10)), "Concurrent fixture thread did not complete.");
                ConcurrentOperationCount = threads.Count;
                MaxConcurrentSerializerEntries = gate.Maximum;
                DistinctConcurrentSerializers = gate.SerializerCount;
            }
            Check.Assert(errors.IsEmpty, "Concurrent serializer failed: " + (errors.TryPeek(out var error) ? error.ToString() : "unknown"));
            Check.Assert(gate.Maximum == count, "Operations were sequential; maximum overlap=" + gate.Maximum);
            for (int i = 0; i < count; i++)
                Check.Assert(outputs[i] == (converterMode ? "{\"Index\":" + i + "}" : "{\"Value\":" + i + "}"), "Concurrent output mismatch at " + i);
            if (converterMode)
            {
                Check.Assert(DistinctConcurrentSerializers == count, "Serializer instances were shared across concurrent calls.");
            }
        }

        sealed class ConcurrentGate : IDisposable
        {
            public readonly CountdownEvent Entered;
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            readonly HashSet<JsonSerializer> serializers = new HashSet<JsonSerializer>();
            int active, maximum;
            public int Maximum => Volatile.Read(ref maximum);
            public int SerializerCount { get { lock (serializers) return serializers.Count; } }
            public ConcurrentGate(int count) { Entered = new CountdownEvent(count); }
            public void Enter(JsonSerializer serializer = null)
            {
                if (serializer != null) lock (serializers) serializers.Add(serializer);
                int current = Interlocked.Increment(ref active);
                int previous;
                do { previous = maximum; if (previous >= current) break; }
                while (Interlocked.CompareExchange(ref maximum, current, previous) != previous);
                Entered.Signal();
                try { if (!Release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Concurrent JSON fixture release was not signaled."); }
                finally { Interlocked.Decrement(ref active); }
            }
            public void Dispose() { Entered.Dispose(); Release.Dispose(); }
        }

        sealed class GetterValue
        {
            readonly ConcurrentGate gate;
            readonly int index;
            public GetterValue(ConcurrentGate gate, int index) { this.gate = gate; this.index = index; }
            public int Value { get { gate.Enter(); return index; } }
        }

        sealed class ConcurrentValue { public int Index { get; set; } }
        sealed class ConcurrentConverter : JsonConverter
        {
            readonly ConcurrentGate gate;
            public ConcurrentConverter(ConcurrentGate gate) { this.gate = gate; }
            public override bool CanConvert(Type objectType) => objectType == typeof(ConcurrentValue);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                gate.Enter(serializer);
                writer.WriteStartObject(); writer.WritePropertyName("Index"); writer.WriteValue(((ConcurrentValue)value).Index); writer.WriteEndObject();
            }
            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) => throw new NotSupportedException();
        }
    }
}
