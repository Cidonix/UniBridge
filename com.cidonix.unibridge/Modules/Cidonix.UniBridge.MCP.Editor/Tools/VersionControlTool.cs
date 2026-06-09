#nullable disable
using System;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static class VersionControlTool
    {
        const string ToolName = "UniBridge_VersionControl";

        public const string Title = "Inspect and checkout version-controlled assets";

        public const string Description = @"Inspect Unity Version Control editability and checkout tracked assets before mutation.

Use this before changing existing assets in Plastic, Perforce, or Unity Version Control projects.

Args:
    Action: GetStatus, InspectAsset, InspectAssets, EnsureEditable, or Checkout.
    AssetPath: Single Assets/... path.
    AssetPaths: Multiple Assets/... paths.
    Checkout: For EnsureEditable, whether to checkout when needed. Defaults true.

Returns:
    success, message, and provider/editability/checkout data.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new
                    {
                        type = "string",
                        description = "Version control operation",
                        @enum = new[] { "GetStatus", "InspectAsset", "InspectAssets", "EnsureEditable", "Checkout" }
                    },
                    AssetPath = new { type = "string", description = "Project asset path, e.g. Assets/Scenes/Main.unity" },
                    AssetPaths = new
                    {
                        type = "array",
                        description = "Project asset paths",
                        items = new { type = "string" }
                    },
                    Checkout = new { type = "boolean", description = "Checkout when EnsureEditable detects a required checkout", @default = true },
                    ThrowOnBlocked = new { type = "boolean", description = "Return an error response when an asset is blocked", @default = false }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "assets", "editor" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = GetString(parameters, "Action", "action") ?? "GetStatus";
            var key = NormalizeKey(action);

            try
            {
                return key switch
                {
                    "getstatus" or "status" => Response.Success("Unity Version Control provider status.", VersionControlUtility.BuildProjectStatus()),
                    "inspectasset" or "inspect" => InspectAsset(parameters),
                    "inspectassets" or "inspectmany" => InspectAssets(parameters),
                    "ensureeditable" or "ensureedit" or "preflight" => EnsureEditable(parameters, forceCheckout: null),
                    "checkout" or "checkoutasset" => EnsureEditable(parameters, forceCheckout: true),
                    _ => Response.Error($"Unknown Action '{action}'. Supported: GetStatus, InspectAsset, InspectAssets, EnsureEditable, Checkout.")
                };
            }
            catch (Exception ex)
            {
                return Response.Error($"Version control action '{action}' failed: {ex.Message}");
            }
        }

        static object InspectAsset(JObject parameters)
        {
            var path = RequirePath(parameters);
            return Response.Success($"Inspected editability for '{path}'.", VersionControlUtility.InspectAsset(path));
        }

        static object InspectAssets(JObject parameters)
        {
            var paths = GetPaths(parameters);
            return Response.Success("Inspected editability for assets.", new
            {
                count = paths.Length,
                assets = VersionControlUtility.InspectAssets(paths)
            });
        }

        static object EnsureEditable(JObject parameters, bool? forceCheckout)
        {
            var path = RequirePath(parameters);
            var checkout = forceCheckout ?? GetBool(parameters, true, "Checkout", "checkout");
            var throwOnBlocked = GetBool(parameters, false, "ThrowOnBlocked", "throw_on_blocked", "throwOnBlocked");
            var result = VersionControlUtility.EnsureAssetEditable(path, checkout, false);
            if (throwOnBlocked && !string.IsNullOrWhiteSpace(result.error))
                return Response.Error(result.error, result);

            var message = string.IsNullOrWhiteSpace(result.error)
                ? result.attempted
                    ? $"Asset '{result.assetPath}' was checked out or is now editable."
                    : $"Asset '{result.assetPath}' is editable; checkout was not needed."
                : result.error;

            return Response.Success(message, result);
        }

        static string RequirePath(JObject parameters)
        {
            var path = GetString(parameters, "AssetPath", "assetPath", "asset_path", "Path", "path");
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("AssetPath is required.");
            return path;
        }

        static string[] GetPaths(JObject parameters)
        {
            var array = parameters["AssetPaths"] as JArray ??
                        parameters["assetPaths"] as JArray ??
                        parameters["asset_paths"] as JArray ??
                        parameters["Paths"] as JArray ??
                        parameters["paths"] as JArray;
            if (array == null)
            {
                var single = GetString(parameters, "AssetPath", "assetPath", "asset_path", "Path", "path");
                if (!string.IsNullOrWhiteSpace(single))
                    return new[] { single };
                throw new InvalidOperationException("AssetPaths or AssetPath is required.");
            }

            return array.Select(token => token?.ToString()).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        }

        static string GetString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                    return token.ToString();
            }

            return null;
        }

        static bool GetBool(JObject obj, bool fallback, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null &&
                    bool.TryParse(token.ToString(), out var value))
                    return value;
            }

            return fallback;
        }

        static string NormalizeKey(string value)
        {
            return (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }
    }
}
