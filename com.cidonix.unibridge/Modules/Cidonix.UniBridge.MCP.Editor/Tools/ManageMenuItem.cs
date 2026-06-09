using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Cidonix.UniBridge.MCP.Editor.Helpers;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Supports execute, list, exists, and refresh operations with caching and search capabilities.
    /// </summary>
    public static class ManageMenuItem
    {
        static List<string> _cached;

        [InitializeOnLoadMethod]
        static void Build() => Refresh();

        /// <summary>
        /// Display title for the ManageMenuItem tool.
        /// </summary>
        public const string Title = "Manage Editor menu items";

        /// <summary>
        /// Description of the ManageMenuItem tool functionality and parameters.
        /// </summary>
        public const string Description = @"List, check, refresh, or execute Unity Editor menu items.

Use List or Exists before Execute when the exact menu path is unknown. Execution is blocked for unsafe menu items such as File/Quit.

Args:
    Action: Execute, List, Exists, or Refresh.
    MenuPath: Menu path for Execute or Exists, for example File/Save Project.
    Search: Optional case-insensitive filter for List.
    Refresh: Force menu cache refresh before the action.

Returns:
    success, message, and data containing execution status, existence status, or matching menu paths.";

        // Basic blacklist to prevent accidental execution of potentially disruptive menu items.
        // This can be expanded based on needs.
        static readonly HashSet<string> _menuPathBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            "File/Quit",
            // Add other potentially dangerous items like "Edit/Preferences...", "File/Build Settings..." if needed
        };

        /// <summary>
        /// Returns the cached list, refreshing if necessary.
        /// </summary>
        static IReadOnlyList<string> AllMenuItems(bool forceRefresh = false)
        {
            if (forceRefresh || _cached == null)
                Refresh();
            return _cached ?? new List<string>();
        }

        /// <summary>
        /// Rebuilds the cached list from reflection.
        /// </summary>
        static List<string> Refresh()
        {
            try
            {
                var methods = TypeCache.GetMethodsWithAttribute<MenuItem>();
                _cached = methods
                    // Methods can have multiple [MenuItem] attributes; collect them all
                    .SelectMany(m => m
                        .GetCustomAttributes(typeof(MenuItem), false)
                        .OfType<MenuItem>()
                        .Select(attr => attr.menuItem))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.Ordinal) // Ensure no duplicates
                    .OrderBy(s => s, StringComparer.Ordinal) // Ensure consistent ordering
                    .ToList();
                return _cached;
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageMenuItem] Failed to scan menu items: {e}");
                _cached = _cached ?? new List<string>();
                return _cached;
            }
        }

        /// <summary>
        /// Execute the tool with strongly-typed parameters.
        /// </summary>
        /// <param name="parameters">The parameters specifying the menu action and related settings.</param>
        /// <returns>A response object containing success status, message, and optional data.</returns>
        [McpTool("UniBridge_ManageMenuItem", Description, Title, Groups = new[] { "core", "editor" }, EnabledByDefault = true)]
        public static object HandleCommand(ManageMenuItemParams parameters)
        {
            try
            {
                return parameters.Action switch
                {
                    MenuItemAction.Execute => ExecuteItem(parameters),
                    MenuItemAction.List => ListMenuItems(parameters),
                    MenuItemAction.Exists => CheckMenuItemExists(parameters),
                    MenuItemAction.Refresh => RefreshMenuCache(parameters),
                    _ => Response.Error($"Unknown action: '{parameters.Action}'. Valid actions are: Execute, List, Exists, Refresh.")
                };
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageMenuItem] Action '{parameters.Action}' failed: {e}");
                return Response.Error($"Internal error processing action '{parameters.Action}': {e.Message}");
            }
        }

        /// <summary>
        /// Executes a specific menu item with safety checks.
        /// </summary>
        static object ExecuteItem(ManageMenuItemParams parameters)
        {
            string menuPath = parameters.MenuPath;

            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return Response.Error("Required parameter 'MenuPath' is missing or empty.");
            }

            // Validate against blacklist
            if (_menuPathBlacklist.Contains(menuPath))
            {
                return Response.Error($"Execution of menu item '{menuPath}' is blocked for safety reasons.");
            }

            // Optional: Check existence before execution if cache refresh is requested
            if (parameters.Refresh)
            {
                AllMenuItems(forceRefresh: true);
            }

            try
            {
                McpLog.Log($"[ManageMenuItem] Request to execute menu: '{menuPath}'");

                bool executed = EditorApplication.ExecuteMenuItem(menuPath);
                if (executed)
                {
                    McpLog.Log($"[ManageMenuItem] Executed successfully: '{menuPath}'");
                    return Response.Success(
                        $"Executed menu item: '{menuPath}'",
                        new { executed = true, menuPath }
                    );
                }

                McpLog.Log($"[ManageMenuItem] Failed (not found/disabled): '{menuPath}'");
                return Response.Error(
                    $"Failed to execute menu item (not found or disabled): '{menuPath}'",
                    new { executed = false, menuPath }
                );
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageMenuItem] Error executing '{menuPath}': {e}");
                return Response.Error($"Error executing menu item '{menuPath}': {e.Message}");
            }
        }

        /// <summary>
        /// Lists available menu items with optional search filtering.
        /// </summary>
        static object ListMenuItems(ManageMenuItemParams parameters)
        {
            try
            {
                List<string> menuItems;

                if (!string.IsNullOrEmpty(parameters.Search))
                {
                    var items = AllMenuItems(parameters.Refresh);
                    menuItems = items
                        .Where(s => s.IndexOf(parameters.Search, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    return Response.Success(
                        $"Found {menuItems.Count} menu items matching '{parameters.Search}'",
                        menuItems
                    );
                }
                else
                {
                    var allItems = AllMenuItems(parameters.Refresh);
                    menuItems = new List<string>(allItems);
                    return Response.Success(
                        $"Retrieved {menuItems.Count} menu items",
                        menuItems
                    );
                }
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageMenuItem] Error listing menu items: {e}");
                return Response.Error($"Error retrieving menu items: {e.Message}");
            }
        }

        /// <summary>
        /// Checks if a menu item exists in the cache.
        /// </summary>
        static object CheckMenuItemExists(ManageMenuItemParams parameters)
        {
            string menuPath = parameters.MenuPath;

            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return Response.Error("Required parameter 'MenuPath' is missing or empty.");
            }

            try
            {
                if (parameters.Refresh)
                {
                    AllMenuItems(forceRefresh: true);
                }

                var items = AllMenuItems();
                bool exists = items.Contains(menuPath);
                return Response.Success(
                    $"Exists check completed for '{menuPath}'.",
                    new { exists, menuPath }
                );
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageMenuItem] Error checking menu item existence: {e}");
                return Response.Error($"Error checking if menu item exists: {e.Message}");
            }
        }

        /// <summary>
        /// Forces refresh of the menu cache.
        /// Provides equivalent functionality to unity-mcp bridge refresh action.
        /// </summary>
        static object RefreshMenuCache(ManageMenuItemParams parameters)
        {
            try
            {
                var menuItems = AllMenuItems(forceRefresh: true);
                int count = menuItems.Count;

                return Response.Success(
                    $"Menu cache refreshed. Found {count} items.",
                    new { refreshed = true, itemCount = count }
                );
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManageMenuItem] Error refreshing menu cache: {e}");
                return Response.Error($"Error refreshing menu cache: {e.Message}");
            }
        }
    }
}
