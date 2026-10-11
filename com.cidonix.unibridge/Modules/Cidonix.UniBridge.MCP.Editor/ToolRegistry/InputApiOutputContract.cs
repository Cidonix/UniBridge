using System.Collections.Generic;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    // Extend only producer objects which already carry the independent obsolete API report.
    // All added Input report fields remain closed DTO schemas, including nested adapter detail objects.
    internal static class InputApiOutputContract
    {
        internal static void AddTo(JToken schema)
        {
            if (schema is JObject obj)
            {
                if (obj["properties"] is JObject properties && properties["obsoleteApiHints"] != null)
                {
                    properties["inputApiHints"] = ActionOutputSchema.Nullable(ActionOutputSchema.DTO(typeof(InputApiReport)));
                    if (obj["required"] is JArray required && required.Any(value => (string)value == "obsoleteApiHints"))
                        required.Add("inputApiHints");
                }
                if (obj["properties"] is JObject coverageProperties && coverageProperties["semanticCoverage"] != null)
                {
                    coverageProperties["inputApiCoverage"] = ActionOutputSchema.DTO(typeof(InputApiCoverageReport));
                    if (obj["required"] is JArray required) required.Add("inputApiCoverage");
                }
                foreach (var property in obj.Properties().ToArray()) AddTo(property.Value);
            }
            else if (schema is JArray array)
                foreach (var child in array) AddTo(child);
        }
    }
}
