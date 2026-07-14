#nullable disable
using System;
using System.Collections.Generic;
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
    AssetPath: Single Assets/... path. Supported by every asset action.
    AssetPaths: Non-empty array of Assets/... paths. Supported by every asset action.
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
                        minItems = 1,
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
                    "inspectasset" or "inspect" => InspectAssets(parameters),
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

        static object InspectAssets(JObject parameters)
        {
            var paths = GetPaths(parameters);
            var assets = VersionControlUtility.InspectAssets(paths);
            var missingCount = assets.Count(asset => asset is VersionControlUtility.AssetEditability status && !status.exists);
            return Response.Success(
                paths.Length == 1
                    ? $"Inspected editability for '{paths[0]}'."
                    : $"Inspected editability for {paths.Length} assets.",
                new
            {
                count = paths.Length,
                existingCount = paths.Length - missingCount,
                missingCount,
                assets
            });
        }

        static object EnsureEditable(JObject parameters, bool? forceCheckout)
        {
            var paths = GetPaths(parameters);
            var checkout = forceCheckout ?? GetBool(parameters, true, "Checkout", "checkout");
            var throwOnBlocked = GetBool(parameters, false, "ThrowOnBlocked", "throw_on_blocked", "throwOnBlocked");
            var results = paths.Select(path => EnsureOne(path, checkout)).ToArray();
            var blocked = results.Where(result => !string.IsNullOrWhiteSpace(result.error)).ToArray();
            var attemptedCount = results.Count(result => result.attempted);
            var checkedOutCount = results.Count(result => result.checkedOut);
            var data = new
            {
                count = results.Length,
                editableCount = results.Length - blocked.Length,
                blockedCount = blocked.Length,
                checkoutRequested = checkout,
                checkoutAttemptedCount = attemptedCount,
                checkedOutCount,
                assets = results
            };

            if (throwOnBlocked && blocked.Length > 0)
            {
                var blockedPaths = string.Join(", ", blocked.Select(result => $"'{result.assetPath}'"));
                return Response.Error(
                    $"{blocked.Length} of {results.Length} assets are not editable: {blockedPaths}.",
                    data);
            }

            var message = blocked.Length > 0
                ? $"Checked {results.Length} assets; {blocked.Length} are not editable."
                : attemptedCount > 0
                    ? $"All {results.Length} assets are editable after {attemptedCount} checkout attempt(s)."
                    : $"All {results.Length} assets are editable; checkout was not needed.";

            return Response.Success(message, data);
        }

        static VersionControlUtility.CheckoutResult EnsureOne(string path, bool checkout)
        {
            var inspected = VersionControlUtility.InspectAsset(path);
            if (!inspected.exists)
            {
                return VersionControlUtility.CheckoutResult.From(
                    inspected,
                    attempted: false,
                    checkedOut: false,
                    $"Asset '{inspected.assetPath ?? path}' does not exist.");
            }

            return VersionControlUtility.EnsureAssetEditable(path, checkout, false);
        }

        static string[] GetPaths(JObject parameters)
        {
            JToken pathsToken = null;
            foreach (var key in new[] { "AssetPaths", "assetPaths", "asset_paths", "Paths", "paths" })
            {
                if (parameters.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var candidate))
                {
                    pathsToken = candidate;
                    break;
                }
            }

            if (pathsToken == null || pathsToken.Type == JTokenType.Null)
            {
                var single = GetString(parameters, "AssetPath", "assetPath", "asset_path", "Path", "path");
                if (!string.IsNullOrWhiteSpace(single))
                    return new[] { single.Trim() };
                throw new InvalidOperationException("AssetPath or a non-empty AssetPaths array is required.");
            }

            if (pathsToken is not JArray array)
                throw new InvalidOperationException("AssetPaths must be an array of non-empty strings.");
            if (array.Count == 0)
                throw new InvalidOperationException("AssetPaths must contain at least one non-empty asset path.");

            var paths = new List<string>(array.Count);
            for (var index = 0; index < array.Count; index++)
            {
                var path = array[index]?.Type == JTokenType.String ? array[index]?.ToString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException($"AssetPaths[{index}] must be a non-empty string.");
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    paths.Add(path);
            }

            return paths.ToArray();
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
