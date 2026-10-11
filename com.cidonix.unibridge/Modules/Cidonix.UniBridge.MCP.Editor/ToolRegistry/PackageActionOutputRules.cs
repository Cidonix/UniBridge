using System;
using System.Collections.Generic;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    internal static class PackageActionOutputRules
    {
        internal static Dictionary<string, JObject> Build()
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in new[] { "List", "Inspect", "Search", "Add", "ListSamples", "ImportSample" })
            {
                var data = Data(action);
                result[action] = ActionOutputSchema.AnyOf(ActionOutputSchema.Success(data), ActionOutputSchema.Error(data));
            }
            return result;
        }
        internal static JObject UnknownAction()
        {
            var data = Data();
            data["properties"] = new JObject { ["action"] = new JObject {
                ["type"] = "string", ["not"] = ActionOutputSchema.Enum("List", "Inspect", "Search", "Add", "ListSamples", "ImportSample") } };
            return ActionOutputSchema.Error(data);
        }
        static JObject Data(string action = null)
        {
            var data = ActionOutputSchema.DTO(typeof(PackageOperationData));
            BoundArrays(data);
            if (data["$defs"] is JObject definitions)
                foreach (var definition in definitions.Properties())
                    if (definition.Value["properties"] is JObject properties && properties.ContainsKey("schema") && properties.ContainsKey("operationId"))
                    {
                        properties["schema"] = ActionOutputSchema.Const("unibridge.packages.v1");
                    }
            // Action constraints are siblings of the shared DTO reference. Its
            // definition keeps one identical structural identity across all families.
            if (action != null) data["properties"] = new JObject { ["action"] = ActionOutputSchema.Const(action) };
            return data;
        }
        static void BoundArrays(JToken token)
        {
            if (token is JObject obj)
            {
                if (obj["properties"] is JObject properties)
                    foreach (var property in properties.Properties())
                        if (property.Name == "packages" || property.Name == "samples") BoundProperty(property.Value, 100);
                        else if (property.Name == "compatibleVersions") BoundProperty(property.Value, 30);
                        else if (property.Name == "dependencies") BoundProperty(property.Value, 60);
                foreach (var property in obj.Properties()) BoundArrays(property.Value);
            }
            else if (token is JArray array) foreach (var item in array) BoundArrays(item);
        }
        static void BoundProperty(JToken token, int maximum)
        {
            if (!(token is JObject schema)) return;
            if (schema.Value<string>("type") == "array") schema["maxItems"] = maximum;
            if (schema["anyOf"] is JArray alternatives) foreach (var item in alternatives) BoundProperty(item, maximum);
        }
    }
}
