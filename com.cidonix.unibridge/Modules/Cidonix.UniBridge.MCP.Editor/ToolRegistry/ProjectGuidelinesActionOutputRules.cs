using System;
using System.Collections.Generic;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    internal static class ProjectGuidelinesActionOutputRules
    {
        // Native/MCP qualification is a separate release gate. Root changes this
        // only after the final candidate's positive and negative actions pass.
        internal const bool RuntimeQualified = true;
        internal static Dictionary<string, JObject> Build()
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in new[] { "Status", "Read" })
            {
                var data = Data();
                data["properties"]["action"] = ActionOutputSchema.Const(action);
                result[action] = ActionOutputSchema.AnyOf(ActionOutputSchema.Success(data), ActionOutputSchema.Error(data));
            }
            return result;
        }

        internal static JObject UnknownAction()
        {
            var data = Data();
            data["properties"]["action"] = new JObject { ["type"] = "string", ["not"] = ActionOutputSchema.Enum("Status", "Read") };
            return ActionOutputSchema.Error(data);
        }

        internal static JObject Orientation() => ActionOutputSchema.DTO(typeof(ProjectGuidelinesOrientation));

        static JObject Data()
        {
            var data = ActionOutputSchema.DTO(typeof(ProjectGuidelinesData));
            Bound(data);
            data["properties"] = new JObject
            {
                ["schema"] = ActionOutputSchema.Const("unibridge.project-guidelines.v1"),
                ["contentRole"] = ActionOutputSchema.Const("authored_project_guidance"),
                ["status"] = ActionOutputSchema.Enum("available", "partial", "unavailable")
            };
            return data;
        }
        static void Bound(JToken token)
        {
            if (token is JObject obj)
            {
                if (obj["properties"] is JObject properties)
                {
                    if (properties.ContainsKey("sources")) BoundProperty(properties["sources"], "maxItems", 3);
                    if (properties.ContainsKey("text")) BoundProperty(properties["text"], "maxLength", 65536);
                }
                foreach (var property in obj.Properties()) Bound(property.Value);
            }
            else if (token is JArray array) foreach (var child in array) Bound(child);
        }
        static void BoundProperty(JToken token, string keyword, int maximum)
        {
            if (!(token is JObject schema)) return;
            if (schema.Value<string>("type") == "array" || schema.Value<string>("type") == "string") schema[keyword] = maximum;
            if (schema["anyOf"] is JArray alternatives) foreach (var child in alternatives) BoundProperty(child, keyword, maximum);
        }
    }
}
