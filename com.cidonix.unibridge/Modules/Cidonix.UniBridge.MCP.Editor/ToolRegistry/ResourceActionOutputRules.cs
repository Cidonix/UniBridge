using System;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    internal static class ResourceActionOutputRules
    {
        internal static JObject Build(string owner)
        {
            Type dto = owner == "UniBridge_ReadResource" ? typeof(ReadResourceResponse)
                : owner == "UniBridge_FindInFile" ? typeof(FindInFileResponse)
                : owner == "UniBridge_ListResources" ? typeof(ListResourcesResponse)
                : throw new ArgumentException("Unknown resource owner: " + owner);
            var data = ActionOutputSchema.DTO(dto);
            return ActionOutputSchema.Compose(new[] {
                ActionOutputSchema.Success(data), ActionOutputSchema.Error(), ActionOutputSchema.Error(data) });
        }
    }
}
