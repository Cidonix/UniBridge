using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.BridgeIsolationRegression;

static class ToolRouteRegression
{
    public enum Mode { None, Preview, Execute }
    public sealed class RouteParameters
    {
        public int Count { get; set; }
        public Mode Mode { get; set; }
        public DateTimeOffset Created { get; set; }
        public string Label { get; set; } = "retained-default";
        public JArray Entries { get; set; }
    }
    public sealed class GenericRoute : IUnityMcpTool<RouteParameters>
    {
        public Task<object> ExecuteAsync(RouteParameters value) => Task.FromResult<object>(value);
    }
    public sealed class FlexibleRoute : IUnityMcpTool
    {
        public Task<object> ExecuteAsync(object value) => Task.FromResult(value);
    }
    public static object ScalarRoute(int count = 42) => count;
    public static object TypedRoute(RouteParameters value) => value;
    public static object RawRoute(JObject value) => value;
    public static object NoParameters() => new { Status = "ready", Count = 3 };
    static MethodInfo Method(string name) => typeof(ToolRouteRegression).GetMethod(name, BindingFlags.Static | BindingFlags.Public);
    static void Poison(Action action)
    {
        var original = JsonConvert.DefaultSettings; var calls = 0;
        try { JsonConvert.DefaultSettings = () => { calls++; throw new InvalidOperationException("Owned global factory poison"); }; action(); Check.Assert(calls == 0, "Actual handler touched global settings"); }
        finally { JsonConvert.DefaultSettings = original; }
    }
    static McpToolAttribute Attribute(string name) => new(name);
    public static void Run(List<CaseResult> cases)
    {
        Check.Run(cases, "Real TypedToolHandler: primitive input and declared method default remain independent", () =>
        {
            var handler = new TypedToolHandler(Method(nameof(ScalarRoute)), Attribute("Owned_Scalar"), typeof(int));
            Poison(() => { Check.Assert((int)handler.ExecuteAsync(JObject.Parse("{\"count\":3}")).GetAwaiter().GetResult() == 3, "Scalar conversion corrupted"); Check.Assert((int)handler.ExecuteAsync(new JObject()).GetAwaiter().GetResult() == 42, "Declared default lost"); });
        });
        Check.Run(cases, "Real TypedToolHandler: complex enum/date/null/unknown-member binding preserves private settings", () =>
        {
            var handler = new TypedToolHandler(Method(nameof(TypedRoute)), Attribute("Owned_Typed"), typeof(RouteParameters));
            var source = JObject.Parse("{\"Count\":3,\"Mode\":\"execute\",\"Created\":\"2026-10-09T04:02:03+03:00\",\"Label\":null,\"Entries\":{\"id\":17},\"futureExtension\":true}");
            var unchanged = source.ToString(Formatting.None);
            Poison(() => { var value = (RouteParameters)handler.ExecuteAsync(source).GetAwaiter().GetResult(); Check.Assert(value.Count == 3 && value.Mode == Mode.Execute && value.Created.Offset == TimeSpan.FromHours(3) && value.Label == "retained-default" && value.Entries.Count == 1 && value.Entries[0]["id"].Value<int>() == 17, "Complex wrapper binding contract changed"); });
            Check.Assert(source.ToString(Formatting.None) == unchanged, "Normalization mutated caller's input JObject");
        });
        Check.Run(cases, "Real GenericClassToolHandler: typed invocation remains isolated", () =>
        {
            var handler = new GenericClassToolHandler(new GenericRoute(), Attribute("Owned_Generic"), typeof(RouteParameters));
            Poison(() => { var value = (RouteParameters)handler.ExecuteAsync(JObject.Parse("{\"Count\":9,\"Mode\":\"Preview\"}")).GetAwaiter().GetResult(); Check.Assert(value.Count == 9 && value.Mode == Mode.Preview, "Generic class wrapper corrupted"); });
        });
        Check.Run(cases, "Real JObjectToolHandler and ClassToolHandler preserve raw token identity", () =>
        {
            var value = JObject.Parse("{\"Count\":3,\"date\":\"2026-10-09T01:02:03Z\"}");
            var method = new JObjectToolHandler(Method(nameof(RawRoute)), Attribute("Owned_Raw"));
            var instance = new ClassToolHandler(new FlexibleRoute(), Attribute("Owned_Flexible"));
            Poison(() => { Check.Assert(ReferenceEquals(method.ExecuteAsync(value).GetAwaiter().GetResult(), value), "Raw method parameter identity changed"); Check.Assert(ReferenceEquals(instance.ExecuteAsync(value).GetAwaiter().GetResult(), value), "Raw class parameter identity changed"); });
        });
        Check.Run(cases, "Real SimpleToolHandler: no-param result survives isolated envelope serialization", () =>
        {
            var handler = new SimpleToolHandler(Method(nameof(NoParameters)), Attribute("Owned_Simple"));
            Poison(() => { var result = handler.ExecuteAsync(null).GetAwaiter().GetResult(); var envelope = McpJson.ObjectFromObject(new { status = "success", result }); Check.Assert(envelope["status"].Value<string>() == "success" && envelope["result"]["Count"].Value<int>() == 3, "Simple route output contract changed"); });
        });
        Check.Run(cases, "Real typed wrapper rejects malformed scalar and does not silently select default", () =>
        {
            var handler = new TypedToolHandler(Method(nameof(ScalarRoute)), Attribute("Owned_BadScalar"), typeof(int));
            Poison(() => { var rejected = false; try { handler.ExecuteAsync(JObject.Parse("{\"count\":\"not-an-integer\"}")).GetAwaiter().GetResult(); } catch (JsonException) { rejected = true; } Check.Assert(rejected, "Malformed scalar accepted or replaced by default"); });
        });
    }
}
