using System;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    static class ExtensionUtilities
    {
        internal static McpToolInfo ToMcpToolInfo(this IToolHandler handler)
        {
            if (handler == null) return null;
            var annotations = MergeAnnotations(handler);
            return new()
            {
                name = handler.Attribute?.Name,
                title = handler.Attribute?.Title,
                description = handler.Attribute?.Description,
                inputSchema = handler.GetInputSchema(),
                outputSchema = handler.GetOutputSchema(),
                annotations = annotations,
            };
        }

        static object MergeAnnotations(IToolHandler handler)
        {
            var annotations = new JObject();
            if (handler.Attribute?.Annotations != null)
            {
                try
                {
                    annotations = JObject.FromObject(handler.Attribute.Annotations);
                }
                catch
                {
                    annotations["custom"] = JToken.FromObject(handler.Attribute.Annotations);
                }
            }

            annotations["uniBridgeExecution"] = JToken.FromObject(ToolExecutionScheduler.BuildAnnotation(handler.Attribute?.Name, handler));
            annotations["uniBridgeProject"] = ProjectContextGuard.BuildProjectContext();
            return annotations;
        }
    }
}
