#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

// Installed only in the approved Test project's UUID-owned, temporary Assets fixture.
// The runner substitutes these identifiers before standard validation and import.
namespace UniBridgeAgentJson__TOKEN__
{
    [InitializeOnLoad]
    static class Probe
    {
        const string Menu = "Tools/UniBridge Agent Tests/JSON __TOKEN__/";
        const string ReportRelative = "Library/AgentValidation/UniBridgeJson__TOKEN__";
        static string Report => Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", ReportRelative));
        static Func<JsonSerializerSettings> original;
        static Func<JsonSerializerSettings> poison;
        static bool active;
        static double expires;
        static int factoryCalls;
        static int reportNumber;
        static readonly Dictionary<string, bool> checks = new Dictionary<string, bool>();

        static Probe()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Restore;
            EditorApplication.quitting += Restore;
        }

        static Type Find(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).First(t => t != null);
        }

        static object Call(string type, string method, Type[] signature, params object[] arguments)
        {
            return Find(type).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, signature, null).Invoke(null, arguments);
        }

        static void Check(string name, bool value)
        {
            checks[name] = value;
            if (!value) throw new InvalidOperationException("Native JSON qualification: " + name);
        }

        [MenuItem(Menu + "Begin")]
        public static void Begin()
        {
            if (active) throw new InvalidOperationException("An owned JSON probe is already active.");
            checks.Clear();
            original = JsonConvert.DefaultSettings;
            factoryCalls = 0;
            poison = () =>
            {
                Interlocked.Increment(ref factoryCalls);
                throw new InvalidOperationException("UniBridge owned global JSON isolation probe __TOKEN__");
            };
            active = true;
            expires = EditorApplication.timeSinceStartup + 55;
            JsonConvert.DefaultSettings = poison;
            EditorApplication.update += Guard;
            try
            {
                const string helper = "Cidonix.UniBridge.MCP.Editor.Helpers.McpJson";
                var input = new { value = 7, label = "native" };
                var serialized = (string)Call(helper, "SerializeObject", new[] { typeof(object) }, input);
                Check("isolated serialize", serialized == "{\"value\":7,\"label\":\"native\"}");
                var obj = (JObject)Call(helper, "ObjectFromObject", new[] { typeof(object) }, input);
                Check("isolated object", (int)obj["value"] == 7 && (string)obj["label"] == "native");
                var array = (JArray)Call(helper, "ArrayFromObject", new[] { typeof(object) }, new[] { 1, 2, 3 });
                Check("isolated array", array.Count == 3 && (int)array[2] == 3);
                var token = (JToken)Call(helper, "TokenFromObject", new[] { typeof(object) }, input);
                Check("isolated token", JToken.DeepEquals(obj, token));
                var serializer = (JsonSerializer)Call(helper, "CreateSerializer", new[] { typeof(JsonSerializerSettings) }, new object[] { null });
                Check("isolated serializer", serializer != null && serializer.Formatting == Formatting.None);
                var deserialize = Find(helper).GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .Single(m => m.Name == "DeserializeObject" && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(JObject));
                var decoded = (JObject)deserialize.Invoke(null, new object[] { serialized, null });
                Check("isolated deserialize", JToken.DeepEquals(decoded, obj));
                const string tracing = "Cidonix.UniBridge.Tracing.TraceJson";
                var trace = (string)Call(tracing, "SerializeObject", new[] { typeof(object) }, input);
                Check("isolated trace serialize", trace == serialized);
                var traceObj = (JObject)Call(tracing, "ObjectFromObject", new[] { typeof(object) }, input);
                Check("isolated trace object", JToken.DeepEquals(traceObj, obj));
                const string protocol = "Cidonix.UniBridge.MCP.Editor.Connection.MessageProtocol";
                var progress = (string)Call(protocol, "CreateCommandInProgressMessage", Type.EmptyTypes);
                Check("actual protocol producer", (string)JObject.Parse(progress)["type"] == "command_in_progress");
                Check("global callback not consulted", Volatile.Read(ref factoryCalls) == 0);
                Check("owned callback retained", ReferenceEquals(JsonConvert.DefaultSettings, poison));
                Write("active", null);
            }
            catch (Exception ex)
            {
                RestoreCore("failed_begin", ex.ToString());
                throw;
            }
        }

        [MenuItem(Menu + "Inspect")]
        public static void Inspect()
        {
            Check("probe remained active", active && EditorApplication.timeSinceStartup < expires);
            Check("global callback not consulted by actual MCP calls", Volatile.Read(ref factoryCalls) == 0);
            Check("same owned callback after actual MCP calls", ReferenceEquals(JsonConvert.DefaultSettings, poison));
            Write("active_inspected", null);
        }

        static void Guard()
        {
            if (active && EditorApplication.timeSinceStartup >= expires)
                RestoreCore("deadline_restore", null);
        }

        [MenuItem(Menu + "Restore")]
        public static void Restore()
        {
            if (active) RestoreCore("explicit_restore", null);
        }

        static void RestoreCore(string reason, string error)
        {
            bool owned = ReferenceEquals(JsonConvert.DefaultSettings, poison);
            if (owned) JsonConvert.DefaultSettings = original;
            active = false;
            EditorApplication.update -= Guard;
            checks["exact original callback restored"] = owned && ReferenceEquals(JsonConvert.DefaultSettings, original);
            Write(reason, error ?? (owned ? null : "Concurrent global settings change preserved."));
        }

        static void Write(string reason, string error)
        {
            Directory.CreateDirectory(Report);
            var value = new { reason, active, factoryCalls = Volatile.Read(ref factoryCalls), checks = new Dictionary<string, bool>(checks), error };
            using (var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            using (var writer = new JsonTextWriter(text))
            {
                var serializer = JsonSerializer.Create(new JsonSerializerSettings { Formatting = Formatting.Indented });
                serializer.Serialize(writer, value);
                File.WriteAllText(Path.Combine(Report, (++reportNumber).ToString("D2") + ".json"), text.ToString(), new System.Text.UTF8Encoding(false));
            }
        }
    }
}
#endif
