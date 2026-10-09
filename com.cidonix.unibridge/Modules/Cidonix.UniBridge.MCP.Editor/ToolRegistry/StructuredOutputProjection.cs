using System;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    static class StructuredOutputProjection
    {
        public static JObject Completed(object result, string owner, bool addMirror)
        {
            JToken serialized = null;
            JObject context = null;
            try
            {
                // Serialize each returned object once, never repeat a getter after conversion failure.
                serialized = result is JToken token ? token.DeepClone() : result == null ? null : McpJson.TokenFromObject(result);
                context = ProjectContextGuard.BuildProjectContext();
                var full = serialized as JObject ?? (serialized == null ? new JObject() : new JObject { ["value"] = serialized });
                if (full["projectContext"] == null) full["projectContext"] = context.DeepClone();
                return Project(full, addMirror);
            }
            catch (Exception ex)
            {
                var failure = new JObject
                {
                    ["status"] = "error", ["code"] = "RESULT_PROJECTION_FAILED",
                    ["error"] = "Result delivery failed after the tool handler completed.",
                    ["command"] = owner,
                    ["executionEvidence"] = new JObject
                    {
                        ["handlerCompleted"] = true, ["retryOriginal"] = false,
                        ["operationOutcome"] = serialized == null ? "unavailable" : "serialized_result_retained"
                    },
                    ["projectionFailure"] = new JObject { ["exceptionType"] = ex.GetType().Name }
                };
                if (context != null) failure["projectContext"] = context.DeepClone();
                if (serialized != null) failure["originalResponse"] = serialized.DeepClone();
                return Project(failure, true);
            }
        }

        public static JObject Project(object result, bool addMirror = true)
        {
            var token = result is JToken jt ? jt.DeepClone() : result == null ? JValue.CreateNull() : McpJson.TokenFromObject(result);
            var full = token as JObject ?? new JObject { ["value"] = token };
            if (full.TryGetValue("structuredContent", out var authored))
            {
                if (authored is not JObject) throw new InvalidOperationException("Authored structuredContent must be a JSON object.");
                return full; // Its deep clone preserves authored contents, including independent nested business fields.
            }
            if (addMirror) full["structuredContent"] = full.DeepClone();
            return full;
        }
    }
}
