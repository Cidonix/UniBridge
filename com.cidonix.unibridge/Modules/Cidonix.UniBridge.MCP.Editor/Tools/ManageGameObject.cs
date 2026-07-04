#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json; // Added for JsonSerializationException
using Newtonsoft.Json.Linq;
using UnityEditor;

// For CompilationPipeline
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry; // For Response class
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;
using Cidonix.UniBridge.MCP.Runtime.Serialization;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Handles GameObject manipulation within the current scene (CRUD, find, components).
    /// </summary>
    public static class ManageGameObject
    {
        /// <summary>
        /// Description of the ManageGameObject tool functionality and parameters.
        /// </summary>
        public const string Title = "Manage GameObjects";

        public const string Description = @"Create, find, inspect, modify, or delete GameObjects in the open scene.

Use this for scene hierarchy work: object creation, transforms, parenting, tags/layers, component add/remove, component property edits, and serialized component inspection.

Args:
    Action: Create, Modify, Delete, Find, GetComponents, GetComponent, AddComponent, RemoveComponent, or SetComponentProperty.
    Target: GameObject name, hierarchy path, or instance/entity ID for existing-object actions.
    SearchMethod: Auto, ByName, ById, ByPath, ByTag, ByLayer, or ByComponent.
    IncludeInactive/SearchInactive: Include inactive scene objects during target resolution.
    Name, Tag, Layer, Parent, StaticEditorFlags: Common creation and modification fields.
    Sibling/Placement/SiblingIndex/WorldTransformStays: Optional hierarchy placement controls for parenting or reordering.
    Position, Rotation, Scale: Transform values for create or modify.
    ComponentsToAdd: Component type names or objects with TypeName and Properties.
    ComponentName: Component type for GetComponent, RemoveComponent, or SetComponentProperty.
    ComponentProperties: Component property values to set. Asset references may use Assets/... paths; scene references may use {""find"": ""Player"", ""method"": ""by_name""}.
    IncludeNonPublicSerialized: Include private [SerializeField] data in component summaries.

PascalCase names above are canonical for new agents. Legacy snake_case aliases are still accepted and normalized internally.

Returns:
    success, message, and action-specific scene data such as object summaries, component summaries, or search results.";
        // Shared JsonSerializer to avoid per-call allocation overhead
        static readonly JsonSerializer InputSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            Converters = new List<JsonConverter>
            {
                new Vector3Converter(),
                new Vector2Converter(),
                new QuaternionConverter(),
                new ColorConverter(),
                new RectConverter(),
                new BoundsConverter(),
                new UnityEngineObjectConverter()
            }
        });

        static JObject NormalizeInput(JObject raw)
        {
            var parameters = raw == null ? new JObject() : (JObject)raw.DeepClone();

            CopyAlias(parameters, "action", "Action", "operation", "Operation");
            NormalizeAction(parameters);

            CopyAlias(parameters, "target", "Target", "gameObject", "GameObject", "game_object", "Path", "path");
            CopyAlias(parameters, "search_method", "SearchMethod", "searchMethod", "ResolveBy", "resolveBy", "Method", "method");
            NormalizeSearchMethod(parameters);

            CopyAlias(parameters, "name", "Name");
            CopyAlias(parameters, "tag", "Tag");
            CopyAlias(parameters, "layer", "Layer");
            CopyAlias(parameters, "parent", "Parent", "parentPath", "parent_path");
            CopyAlias(parameters, "static_editor_flags", "StaticEditorFlags", "staticEditorFlags", "static_flags", "staticFlags", "StaticFlags");
            CopyAlias(parameters, "sibling", "Sibling", "siblingPath", "sibling_path", "SiblingPath");
            CopyAlias(parameters, "placement", "Placement", "siblingPlacement", "sibling_placement", "SiblingPlacement");
            CopyAlias(parameters, "sibling_index", "SiblingIndex", "siblingIndex");
            CopyAlias(parameters, "world_transform_stays", "WorldTransformStays", "worldTransformStays", "WorldPositionStays", "worldPositionStays");
            CopyAlias(parameters, "position", "Position");
            CopyAlias(parameters, "rotation", "Rotation");
            CopyAlias(parameters, "scale", "Scale");
            CopyAlias(parameters, "positionType", "PositionType", "position_type");

            CopyAlias(parameters, "primitive_type", "PrimitiveType", "primitiveType");
            CopyAlias(parameters, "save_as_prefab", "SaveAsPrefab", "saveAsPrefab");
            CopyAlias(parameters, "prefab_path", "PrefabPath", "prefabPath");
            CopyAlias(parameters, "prefab_folder", "PrefabFolder", "prefabFolder");
            CopyAlias(parameters, "set_active", "SetActive", "setActive", "Active", "active");

            CopyAlias(parameters, "components_to_add", "ComponentsToAdd", "componentsToAdd");
            CopyAlias(parameters, "components_to_remove", "ComponentsToRemove", "componentsToRemove");
            CopyAlias(parameters, "component_name", "ComponentName", "componentName", "Component", "component", "ComponentType", "componentType");
            CopyAlias(parameters, "component_properties", "ComponentProperties", "componentProperties");
            CopyAlias(parameters, "search_term", "SearchTerm", "searchTerm", "Query", "query");
            CopyAlias(parameters, "find_all", "FindAll", "findAll");
            CopyAlias(parameters, "search_in_children", "SearchInChildren", "searchInChildren");
            CopyAlias(parameters, "search_inactive", "SearchInactive", "searchInactive", "IncludeInactive", "includeInactive");
            CopyAlias(parameters, "include_non_public_serialized", "IncludeNonPublicSerialized", "includeNonPublicSerialized");

            if (parameters["target"] == null && parameters["search_term"] != null)
            {
                parameters["target"] = parameters["search_term"].DeepClone();
            }

            NormalizeComponentArray(parameters["components_to_add"] as JArray);
            NormalizeComponentArray(parameters["components_to_remove"] as JArray);

            if (parameters["component_properties"] == null && parameters["Properties"] is JObject flatProperties)
            {
                var componentName = parameters["component_name"]?.ToString();
                parameters["component_properties"] = string.IsNullOrWhiteSpace(componentName)
                    ? flatProperties.DeepClone()
                    : new JObject { [componentName] = flatProperties.DeepClone() };
            }

            return parameters;
        }

        static void CopyAlias(JObject parameters, string canonicalName, params string[] aliases)
        {
            if (parameters[canonicalName] != null)
            {
                return;
            }

            foreach (var alias in aliases)
            {
                if (parameters.TryGetValue(alias, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                {
                    parameters[canonicalName] = token.DeepClone();
                    return;
                }
            }
        }

        static void NormalizeAction(JObject parameters)
        {
            var raw = parameters["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var key = raw.Trim().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
            parameters["action"] = key switch
            {
                "create" or "creategameobject" or "newgameobject" => "create",
                "modify" or "update" or "patch" or "updategameobject" => "modify",
                "delete" or "destroy" or "removegameobject" or "deletegameobject" => "delete",
                "find" or "search" or "searchgameobjects" => "find",
                "getcomponents" or "listcomponents" or "inspectcomponents" => "get_components",
                "getcomponent" or "inspectcomponent" => "get_component",
                "addcomponent" => "add_component",
                "removecomponent" or "deletecomponent" => "remove_component",
                "setcomponentproperty" or "setcomponentproperties" or "updatecomponent" or "patchcomponent" => "set_component_property",
                _ => raw.Trim().ToLowerInvariant()
            };
        }

        static void NormalizeSearchMethod(JObject parameters)
        {
            var raw = parameters["search_method"]?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            parameters["search_method"] = SceneObjectLocator.NormalizeSearchMethod(raw);
        }

        static void NormalizeComponentArray(JArray components)
        {
            if (components == null)
            {
                return;
            }

            foreach (var token in components.OfType<JObject>())
            {
                CopyAlias(token, "typeName", "TypeName", "type", "Type", "componentName", "ComponentName", "componentType", "ComponentType");
                CopyAlias(token, "properties", "Properties", "props", "Props");
            }
        }

        // --- Main Handler ---

        /// <summary>
        /// Returns the input schema for this tool.
        /// </summary>
        /// <returns>The JSON schema object describing the tool's input structure.</returns>
        [McpSchema("UniBridge_ManageGameObject")]
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
                        description = "Operation to perform",
                        @enum = new[]
                        {
                            "Create", "Modify", "Delete", "Find", "GetComponents", "GetComponent",
                            "AddComponent", "RemoveComponent", "SetComponentProperty"
                        }
                    },
                    // Targeting and search
                    Target = new
                    {
                        description = "GameObject identifier (name/path or instance ID)",
                        anyOf = new object[] { new { type = "string" }, new { type = "integer" } }
                    },
                    SearchMethod = new
                    {
                        type = "string",
                        description = "How to find objects. Auto resolves id/name/path; ByPath is best for deterministic edits.",
                        @enum = new[] { "Auto", "ByName", "ById", "ByPath", "ByTag", "ByLayer", "ByComponent" }
                    },

                    // Common fields for create/modify
                    Name = new { type = "string", description = "GameObject name" },
                    Tag = new { type = "string", description = "Tag name" },
                    Layer = new { type = "string", description = "Layer name" },
                    StaticEditorFlags = new
                    {
                        description = "Static flags as None, Everything, a string/array of flag names, or an integer mask. Common flags: ContributeGI, OccluderStatic, BatchingStatic, NavigationStatic, OccludeeStatic, OffMeshLinkGeneration, ReflectionProbeStatic.",
                        anyOf = new object[] { new { type = "string" }, new { type = "array", items = new { type = "string" } }, new { type = "integer" } }
                    },
                    Parent = new
                    {
                        description = "Parent GameObject (name/path or instance ID)",
                        anyOf = new object[] { new { type = "string" }, new { type = "integer" } }
                    },
                    Sibling = new
                    {
                        description = "Sibling GameObject used with Placement=Before/After. Must be a child of the target parent, or a root object when parent is root.",
                        anyOf = new object[] { new { type = "string" }, new { type = "integer" } }
                    },
                    Placement = new
                    {
                        type = "string",
                        description = "Hierarchy placement relative to Sibling or parent child list.",
                        @enum = new[] { "Before", "After", "First", "Last", "Index" }
                    },
                    SiblingIndex = new { type = "integer", description = "Explicit sibling index when Placement=Index or when no Sibling is provided." },
                    WorldTransformStays = new { type = "boolean", description = "Preserve world transform when changing parent. Default true." },
                    Position = new
                    {
                        type = "array",
                        description = "Local position [x,y,z]",
                        items = new { type = "number" },
                        min_items = 3,
                        max_items = 3
                    },
                    Rotation = new
                    {
                        type = "array",
                        description = "Local rotation euler [x,y,z]",
                        items = new { type = "number" },
                        min_items = 3,
                        max_items = 3
                    },
                    Scale = new
                    {
                        type = "array",
                        description = "Local scale [x,y,z]",
                        items = new { type = "number" },
                        min_items = 3,
                        max_items = 3
                    },

                    // Creation helpers
                    PrimitiveType = new { type = "string", description = "Unity primitive type to create (e.g., Cube, Sphere)" },
                    SaveAsPrefab = new { type = "boolean", description = "If true, save created object as prefab" },
                    PrefabPath = new { type = "string", description = "Prefab path (Assets/... .prefab) when instantiating or saving prefab" },
                    PrefabFolder = new { type = "string", description = "Folder for prefab creation (defaults to Assets/Prefabs)" },

                    // Modify toggles
                    SetActive = new { type = "boolean", description = "Set GameObject active state" },

                    // Component operations
                    ComponentsToAdd = new { type = "array", description = "List of component type names or objects { TypeName, Properties } to add" },
                    ComponentsToRemove = new { type = "array", items = new { type = "string" }, description = "List of component type names to remove" },
                    ComponentName = new { type = "string", description = "Single component type name for get/remove/set operations" },
                    ComponentProperties = new
                    {
                        type = "object",
                        description = "Map of component names to property dictionaries",
                        additional_properties = new { type = "object" }
                    },
                    Properties = new
                    {
                        type = "object",
                        description = "Convenience flat property dictionary for ComponentName when setting one component.",
                        additionalProperties = true
                    },

                    // Find parameters
                    SearchTerm = new { type = "string", description = "Search term for Find" },
                    FindAll = new { type = "boolean", description = "If true, return all matching objects" },
                    SearchInChildren = new { type = "boolean", description = "Search within children" },
                    SearchInactive = new { type = "boolean", description = "Include inactive objects in search" },
                    IncludeInactive = new { type = "boolean", description = "Include inactive scene objects during target resolution for find, modify, component, parent, and sibling lookups" },

                    // Serialization controls
                    IncludeNonPublicSerialized = new { type = "boolean", description = "Include [SerializeField] private fields in component data" }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }
        /// <summary>
        /// Main handler for GameObject management actions.
        /// </summary>
        /// <param name="params">The JObject containing action and parameters for GameObject operations.</param>
        /// <returns>A response object containing success status, message, and optional data.</returns>
        [McpTool("UniBridge_ManageGameObject", Description, Title, Groups = new string[] { "core", "scene" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return Response.Error("Parameters cannot be null.");
            }

            @params = NormalizeInput(@params);

            string action = @params["action"]?.ToString().ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
            {
                return Response.Error("Action parameter is required.");
            }

            // Parameters used by various actions
            JToken targetToken = @params["target"]; // Can be string (name/path) or int (instanceID)
            string searchMethod = @params["search_method"]?.ToString();

            // Get common parameters (consolidated)
            string name = @params["name"]?.ToString();
            string tag = @params["tag"]?.ToString();
            string layer = @params["layer"]?.ToString();
            JToken parentToken = @params["parent"];

            // --- Add parameter for controlling non-public field inclusion ---
            bool includeNonPublicSerialized = @params["include_non_public_serialized"]?.ToObject<bool>() ?? true; // Default to true
            // --- End add parameter ---

            // --- Prefab Redirection Check ---
            string targetPath =
                targetToken?.Type == JTokenType.String ? targetToken.ToString() : null;
            if (
                !string.IsNullOrEmpty(targetPath)
                && targetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            )
            {
                // Allow 'create' (instantiate), 'find' (?), 'get_components' (?)
                if (action == "modify" || action == "set_component_property")
                {
                    Debug.Log(
                        $"[ManageGameObject->ManageAsset] Redirecting action '{action}' for prefab '{targetPath}' to ManageAsset."
                    );
                    // Prepare params for ManageAsset.ModifyAsset
                    var assetParams = new ManageAssetParams
                    {
                        Action = AssetAction.Modify,
                        Path = targetPath
                    };

                    // Extract properties.
                    // For 'set_component_property', combine componentName and componentProperties.
                    // For 'modify', directly use componentProperties.
                    JObject properties = null;
                    if (action == "set_component_property")
                    {
                        string compName = @params["component_name"]?.ToString();
                        JObject compProps = @params["component_properties"]?[compName] as JObject; // Handle potential nesting
                        if (string.IsNullOrEmpty(compName))
                            return Response.Error(
                                "Missing 'componentName' for 'set_component_property' on prefab."
                            );
                        if (compProps == null)
                            return Response.Error(
                                $"Missing or invalid 'componentProperties' for component '{compName}' for 'set_component_property' on prefab."
                            );

                        properties = new JObject();
                        properties[compName] = compProps;
                    }
                    else // action == "modify"
                    {
                        properties = @params["component_properties"] as JObject;
                        if (properties == null)
                            return Response.Error(
                                "Missing 'componentProperties' for 'modify' action on prefab."
                            );
                    }

                    assetParams.Properties = properties;

                    // Call ManageAsset handler
                    return ManageAsset.HandleCommand(assetParams);
                }
                else if (
                    action == "delete"
                    || action == "add_component"
                    || action == "remove_component"
                    || action == "get_components"
                ) // Added get_components here too
                {
                    // Explicitly block other modifications on the prefab asset itself via UniBridge_ManageGameObject
                    return Response.Error(
                        $"Action '{action}' on a prefab asset ('{targetPath}') should be performed using the 'UniBridge_ManageAsset' command."
                    );
                }
                // Allow 'create' (instantiation) and 'find' to proceed, although finding a prefab asset by path might be less common via UniBridge_ManageGameObject.
                // No specific handling needed here, the code below will run.
            }
            // --- End Prefab Redirection Check ---

            try
            {
                switch (action)
                {
                    case "create":
                        return CreateGameObject(@params);
                    case "modify":
                        return ModifyGameObject(@params, targetToken, searchMethod);
                    case "delete":
                        return DeleteGameObject(@params, targetToken, searchMethod);
                    case "find":
                        return FindGameObjects(@params, targetToken, searchMethod);
                    case "get_components":
                        string getCompTarget = targetToken?.ToString(); // Expect name, path, or ID string
                        if (getCompTarget == null)
                            return Response.Error(
                                "'target' parameter required for get_components."
                            );
                        // Pass the includeNonPublicSerialized flag here
                        return GetComponentsFromTarget(@params, getCompTarget, searchMethod, includeNonPublicSerialized);
                    case "get_component":
                        string getSingleCompTarget = targetToken?.ToString();
                        if (getSingleCompTarget == null)
                            return Response.Error(
                                "'target' parameter required for get_component."
                            );
                        string componentName = @params["component_name"]?.ToString() ?? @params["componentName"]?.ToString();
                        if (string.IsNullOrEmpty(componentName))
                            return Response.Error(
                                "'component_name' parameter required for get_component."
                            );
                        return GetSingleComponentFromTarget(@params, getSingleCompTarget, searchMethod, componentName, includeNonPublicSerialized);
                    case "add_component":
                        return AddComponentToTarget(@params, targetToken, searchMethod);
                    case "remove_component":
                        return RemoveComponentFromTarget(@params, targetToken, searchMethod);
                    case "set_component_property":
                        return SetComponentPropertyOnTarget(@params, targetToken, searchMethod);

                    default:
                        return Response.Error($"Unknown action: '{action}'.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ManageGameObject] Action '{action}' failed: {e}");
                return Response.Error($"Internal error processing action '{action}': {e.Message}");
            }
        }

        // --- Action Implementations ---

        static object CreateGameObject(JObject @params)
        {
            string name = @params["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                return Response.Error("'name' parameter is required for 'create' action.");
            }

            // Get prefab creation parameters
            bool saveAsPrefab = @params["save_as_prefab"]?.ToObject<bool>() ?? false;
            string prefabPath = @params["prefab_path"]?.ToString();
            string prefabFolder = @params["prefab_folder"]?.ToString() ?? "Assets/Prefabs";
            string tag = @params["tag"]?.ToString(); // Get tag for creation
            string primitiveType = @params["primitive_type"]?.ToString(); // Keep primitiveType check

            // --- Handle Prefab Path Logic (Python server parity) ---
            if (saveAsPrefab)
            {
                if (string.IsNullOrEmpty(prefabPath))
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return Response.Error("Cannot create default prefab path: 'name' parameter is missing.");
                    }
                    // Construct path using prefab_folder and name
                    string constructedPath = $"{prefabFolder}/{name}.prefab";
                    // Ensure clean path separators (Unity prefers '/')
                    prefabPath = constructedPath.Replace("\\", "/");
                    Debug.Log($"[ManageGameObject.Create] Constructed prefab path: '{prefabPath}'");
                }
                else if (!prefabPath.ToLower().EndsWith(".prefab"))
                {
                    return Response.Error($"Invalid prefab_path: '{prefabPath}' must end with .prefab");
                }
            }
            // --- End Prefab Path Logic ---

            GameObject newGo = null; // Initialize as null

            // --- Try Instantiating Prefab First ---
            string originalPrefabPath = prefabPath; // Keep original for messages
            if (!string.IsNullOrEmpty(prefabPath))
            {
                // If no extension, search for the prefab by name
                if (
                    !prefabPath.Contains("/")
                    && !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                )
                {
                    string prefabNameOnly = prefabPath;
                    Debug.Log(
                        $"[ManageGameObject.Create] Searching for prefab named: '{prefabNameOnly}'"
                    );
                    string[] guids = AssetDatabase.FindAssets($"t:Prefab {prefabNameOnly}");
                    if (guids.Length == 0)
                    {
                        return Response.Error(
                            $"Prefab named '{prefabNameOnly}' not found anywhere in the project."
                        );
                    }
                    else if (guids.Length > 1)
                    {
                        string foundPaths = string.Join(
                            ", ",
                            guids.Select(g => AssetDatabase.GUIDToAssetPath(g))
                        );
                        return Response.Error(
                            $"Multiple prefabs found matching name '{prefabNameOnly}': {foundPaths}. Please provide a more specific path."
                        );
                    }
                    else // Exactly one found
                    {
                        prefabPath = AssetDatabase.GUIDToAssetPath(guids[0]); // Update prefabPath with the full path
                        Debug.Log(
                            $"[ManageGameObject.Create] Found unique prefab at path: '{prefabPath}'"
                        );
                    }
                }
                else if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    // If it looks like a path but doesn't end with .prefab, assume user forgot it and append it.
                    Debug.LogWarning(
                        $"[ManageGameObject.Create] Provided prefabPath '{prefabPath}' does not end with .prefab. Assuming it's missing and appending."
                    );
                    prefabPath += ".prefab";
                    // Note: This path might still not exist, AssetDatabase.LoadAssetAtPath will handle that.
                }
                // The logic above now handles finding or assuming the .prefab extension.

                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabAsset != null)
                {
                    try
                    {
                        // Instantiate the prefab, initially place it at the root
                        // Parent will be set later if specified
                        newGo = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;

                        if (newGo == null)
                        {
                            // This might happen if the asset exists but isn't a valid GameObject prefab somehow
                            Debug.LogError(
                                $"[ManageGameObject.Create] Failed to instantiate prefab at '{prefabPath}', asset might be corrupted or not a GameObject."
                            );
                            return Response.Error(
                                $"Failed to instantiate prefab at '{prefabPath}'."
                            );
                        }
                        // Name the instance based on the 'name' parameter, not the prefab's default name
                        if (!string.IsNullOrEmpty(name))
                        {
                            newGo.name = name;
                        }
                        // Register Undo for prefab instantiation
                        Undo.RegisterCreatedObjectUndo(
                            newGo,
                            $"Instantiate Prefab '{prefabAsset.name}' as '{newGo.name}'"
                        );
                        Debug.Log(
                            $"[ManageGameObject.Create] Instantiated prefab '{prefabAsset.name}' from path '{prefabPath}' as '{newGo.name}'."
                        );
                    }
                    catch (Exception e)
                    {
                        return Response.Error(
                            $"Error instantiating prefab '{prefabPath}': {e.Message}"
                        );
                    }
                }
                else
                {
                    if (!saveAsPrefab)
                    {
                        Debug.Log(
                            $"[ManageGameObject.Create] Prefab asset not found at path: '{prefabPath}'. Will proceed to create a new object if specified."
                        );
                    }
                    // Do not return error here; save_as_prefab uses prefab_path as the target path.
                }
            }

            // --- Fallback: Create Primitive or Empty GameObject ---
            bool createdNewObject = false; // Flag to track if we created (not instantiated)
            if (newGo == null) // Only proceed if prefab instantiation didn't happen
            {
                if (!string.IsNullOrEmpty(primitiveType))
                {
                    try
                    {
                        PrimitiveType type = (PrimitiveType)
                            Enum.Parse(typeof(PrimitiveType), primitiveType, true);
                        newGo = GameObject.CreatePrimitive(type);
                        // Set name *after* creation for primitives
                        if (!string.IsNullOrEmpty(name))
                        {
                            newGo.name = name;
                        }
                        else
                        {
                            UnityEngine.Object.DestroyImmediate(newGo); // cleanup leak
                            return Response.Error(
                                "'name' parameter is required when creating a primitive."
                            ); // Name is essential
                        }
                        createdNewObject = true;
                    }
                    catch (ArgumentException)
                    {
                        return Response.Error(
                            $"Invalid primitive type: '{primitiveType}'. Valid types: {string.Join(", ", Enum.GetNames(typeof(PrimitiveType)))}"
                        );
                    }
                    catch (Exception e)
                    {
                        return Response.Error(
                            $"Failed to create primitive '{primitiveType}': {e.Message}"
                        );
                    }
                }
                else // Create empty GameObject
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return Response.Error(
                            "'name' parameter is required for 'create' action when not instantiating a prefab or creating a primitive."
                        );
                    }
                    newGo = new GameObject(name);
                    createdNewObject = true;
                }
                // Record creation for Undo *only* if we created a new object
                if (createdNewObject)
                {
                    Undo.RegisterCreatedObjectUndo(newGo, $"Create GameObject '{newGo.name}'");
                }
            }
            // --- Common Setup (Parent, Transform, Tag, Components) - Applied AFTER object exists ---
            if (newGo == null)
            {
                // Should theoretically not happen if logic above is correct, but safety check.
                return Response.Error("Failed to create or instantiate the GameObject.");
            }

            // Record potential changes to the existing prefab instance or the new GO
            // Record transform separately in case parent changes affect it
            Undo.RecordObject(newGo.transform, "Set GameObject Transform");
            Undo.RecordObject(newGo, "Set GameObject Properties");

            // Set Transform
            Vector3? position = ParseVector3(@params["position"] as JArray);
            Vector3? rotation = ParseVector3(@params["rotation"] as JArray);
            Vector3? scale = ParseVector3(@params["scale"] as JArray);

            if (position.HasValue)
                newGo.transform.localPosition = position.Value;
            if (rotation.HasValue)
                newGo.transform.localEulerAngles = rotation.Value;
            if (scale.HasValue)
                newGo.transform.localScale = scale.Value;

            if (@params["static_editor_flags"] != null)
            {
                var staticResult = ApplyStaticEditorFlags(newGo, @params["static_editor_flags"], out _);
                if (staticResult != null)
                {
                    UnityEngine.Object.DestroyImmediate(newGo);
                    return staticResult;
                }
            }

            // Set Parent / Sibling order
            var worldTransformStays = @params["world_transform_stays"]?.ToObject<bool?>() ?? true;
            var hierarchyResult = ApplyHierarchyPlacement(newGo, @params, worldTransformStays, out _, out _);
            if (hierarchyResult != null)
            {
                UnityEngine.Object.DestroyImmediate(newGo);
                return hierarchyResult;
            }

            // Set Tag (added for create action)
            if (!string.IsNullOrEmpty(tag))
            {
                // Similar logic as in ModifyGameObject for setting/creating tags
                string tagToSet = string.IsNullOrEmpty(tag) ? "Untagged" : tag;
                try
                {
                    newGo.tag = tagToSet;
                }
                catch (UnityException ex)
                {
                    if (ex.Message.Contains("is not defined"))
                    {
                        Debug.LogWarning(
                            $"[ManageGameObject.Create] Tag '{tagToSet}' not found. Attempting to create it."
                        );
                        try
                        {
                            InternalEditorUtility.AddTag(tagToSet);
                            newGo.tag = tagToSet; // Retry
                            Debug.Log(
                                $"[ManageGameObject.Create] Tag '{tagToSet}' created and assigned successfully."
                            );
                        }
                        catch (Exception innerEx)
                        {
                            UnityEngine.Object.DestroyImmediate(newGo); // Clean up
                            return Response.Error(
                                $"Failed to create or assign tag '{tagToSet}' during creation: {innerEx.Message}."
                            );
                        }
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(newGo); // Clean up
                        return Response.Error(
                            $"Failed to set tag to '{tagToSet}' during creation: {ex.Message}."
                        );
                    }
                }
            }

            // Set Layer (new for create action)
            string layerName = @params["layer"]?.ToString();
            if (!string.IsNullOrEmpty(layerName))
            {
                int layerId = LayerMask.NameToLayer(layerName);
                if (layerId != -1)
                {
                    newGo.layer = layerId;
                }
                else
                {
                    Debug.LogWarning(
                        $"[ManageGameObject.Create] Layer '{layerName}' not found. Using default layer."
                    );
                }
            }

            // Add Components
            if (@params["components_to_add"] is JArray componentsToAddArray)
            {
                foreach (var compToken in componentsToAddArray)
                {
                    string typeName = null;
                    JObject properties = null;

                    if (compToken.Type == JTokenType.String)
                    {
                        typeName = compToken.ToString();
                    }
                    else if (compToken is JObject compObj)
                    {
                        typeName = compObj["typeName"]?.ToString();
                        properties = compObj["properties"] as JObject;
                    }

                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var addResult = AddComponentInternal(newGo, typeName, properties);
                        if (addResult != null) // Check if AddComponentInternal returned an error object
                        {
                            UnityEngine.Object.DestroyImmediate(newGo); // Clean up
                            return addResult; // Return the error response
                        }
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[ManageGameObject] Invalid component format in components_to_add: {compToken}"
                        );
                    }
                }
            }

            // Save as Prefab ONLY if we *created* a new object AND saveAsPrefab is true
            GameObject finalInstance = newGo; // Use this for selection and return data
            if (createdNewObject && saveAsPrefab)
            {
                string finalPrefabPath = prefabPath; // Use a separate variable for saving path
                // This check should now happen *before* attempting to save
                if (string.IsNullOrEmpty(finalPrefabPath))
                {
                    // Clean up the created object before returning error
                    UnityEngine.Object.DestroyImmediate(newGo);
                    return Response.Error(
                        "'prefabPath' is required when 'saveAsPrefab' is true and creating a new object."
                    );
                }
                // Ensure the *saving* path ends with .prefab
                if (!finalPrefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log(
                        $"[ManageGameObject.Create] Appending .prefab extension to save path: '{finalPrefabPath}' -> '{finalPrefabPath}.prefab'"
                    );
                    finalPrefabPath += ".prefab";
                }

                try
                {
                    // Ensure directory exists using the final saving path
                    string directoryPath = System.IO.Path.GetDirectoryName(finalPrefabPath);
                    if (
                        !string.IsNullOrEmpty(directoryPath)
                        && !System.IO.Directory.Exists(directoryPath)
                    )
                    {
                        System.IO.Directory.CreateDirectory(directoryPath);
                        AssetDatabase.Refresh(); // Refresh asset database to recognize the new folder
                        Debug.Log(
                            $"[ManageGameObject.Create] Created directory for prefab: {directoryPath}"
                        );
                    }
                    // Use SaveAsPrefabAssetAndConnect with the final saving path
                    finalInstance = PrefabUtility.SaveAsPrefabAssetAndConnect(
                        newGo,
                        finalPrefabPath,
                        InteractionMode.UserAction
                    );

                    if (finalInstance == null)
                    {
                        // Destroy the original if saving failed somehow (shouldn't usually happen if path is valid)
                        UnityEngine.Object.DestroyImmediate(newGo);
                        return Response.Error(
                            $"Failed to save GameObject '{name}' as prefab at '{finalPrefabPath}'. Check path and permissions."
                        );
                    }
                    Debug.Log(
                        $"[ManageGameObject.Create] GameObject '{name}' saved as prefab to '{finalPrefabPath}' and instance connected."
                    );
                    // Mark the new prefab asset as dirty? Not usually necessary, SaveAsPrefabAsset handles it.
                    // EditorUtility.SetDirty(finalInstance); // Instance is handled by SaveAsPrefabAssetAndConnect
                }
                catch (Exception e)
                {
                    // Clean up the instance if prefab saving fails
                    UnityEngine.Object.DestroyImmediate(newGo); // Destroy the original attempt
                    return Response.Error($"Error saving prefab '{finalPrefabPath}': {e.Message}");
                }
            }

            // Select the instance in the scene (either prefab instance or newly created/saved one)
            Selection.activeGameObject = finalInstance;

            // Determine appropriate success message using the potentially updated or original path
            string messagePrefabPath =
                finalInstance == null
                    ? originalPrefabPath
                    : AssetDatabase.GetAssetPath(
                        PrefabUtility.GetCorrespondingObjectFromSource(finalInstance)
                            ?? (UnityEngine.Object)finalInstance
                    );
            string successMessage;
            if (!createdNewObject && !string.IsNullOrEmpty(messagePrefabPath)) // Instantiated existing prefab
            {
                successMessage =
                    $"Prefab '{messagePrefabPath}' instantiated successfully as '{finalInstance.name}'.";
            }
            else if (createdNewObject && saveAsPrefab && !string.IsNullOrEmpty(messagePrefabPath)) // Created new and saved as prefab
            {
                successMessage =
                    $"GameObject '{finalInstance.name}' created and saved as prefab to '{messagePrefabPath}'.";
            }
            else // Created new primitive or empty GO, didn't save as prefab
            {
                successMessage =
                    $"GameObject '{finalInstance.name}' created successfully in scene.";
            }

            // Use the new serializer helper
            //return Response.Success(successMessage, GetGameObjectData(finalInstance));
            return Response.Success(successMessage, GameObjectSerializer.GetGameObjectData(finalInstance));
        }

        static object ModifyGameObject(
            JObject @params,
            JToken targetToken,
            string searchMethod
        )
        {
            GameObject targetGo = ResolveGameObject(targetToken, searchMethod, @params);
            if (targetGo == null)
            {
                return Response.Error(
                    BuildTargetNotFoundMessage(targetToken, searchMethod, @params)
                );
            }

            // Record state for Undo *before* modifications
            Undo.RecordObject(targetGo.transform, "Modify GameObject Transform");
            Undo.RecordObject(targetGo, "Modify GameObject Properties");

            bool modified = false;

            // Rename (using consolidated 'name' parameter)
            string name = @params["name"]?.ToString();
            if (!string.IsNullOrEmpty(name) && targetGo.name != name)
            {
                targetGo.name = name;
                modified = true;
            }

            // Set Active State
            bool? setActive = @params["set_active"]?.ToObject<bool?>();
            if (setActive.HasValue && targetGo.activeSelf != setActive.Value)
            {
                targetGo.SetActive(setActive.Value);
                modified = true;
            }

            if (@params["static_editor_flags"] != null)
            {
                var staticResult = ApplyStaticEditorFlags(targetGo, @params["static_editor_flags"], out var staticChanged);
                if (staticResult != null)
                    return staticResult;
                modified |= staticChanged;
            }

            // Change Tag (using consolidated 'tag' parameter)
            string tag = @params["tag"]?.ToString();
            // Only attempt to change tag if a non-null tag is provided and it's different from the current one.
            // Allow setting an empty string to remove the tag (Unity uses "Untagged").
            if (tag != null && targetGo.tag != tag)
            {
                // Ensure the tag is not empty, if empty, it means "Untagged" implicitly
                string tagToSet = string.IsNullOrEmpty(tag) ? "Untagged" : tag;
                try
                {
                    targetGo.tag = tagToSet;
                    modified = true;
                }
                catch (UnityException ex)
                {
                    // Check if the error is specifically because the tag doesn't exist
                    if (ex.Message.Contains("is not defined"))
                    {
                        Debug.LogWarning(
                            $"[ManageGameObject] Tag '{tagToSet}' not found. Attempting to create it."
                        );
                        try
                        {
                            // Attempt to create the tag using internal utility
                            InternalEditorUtility.AddTag(tagToSet);
                            // Wait a frame maybe? Not strictly necessary but sometimes helps editor updates.
                            // yield return null; // Cannot yield here, editor script limitation

                            // Retry setting the tag immediately after creation
                            targetGo.tag = tagToSet;
                            modified = true;
                            Debug.Log(
                                $"[ManageGameObject] Tag '{tagToSet}' created and assigned successfully."
                            );
                        }
                        catch (Exception innerEx)
                        {
                            // Handle failure during tag creation or the second assignment attempt
                            Debug.LogError(
                                $"[ManageGameObject] Failed to create or assign tag '{tagToSet}' after attempting creation: {innerEx.Message}"
                            );
                            return Response.Error(
                                $"Failed to create or assign tag '{tagToSet}': {innerEx.Message}. Check Tag Manager and permissions."
                            );
                        }
                    }
                    else
                    {
                        // If the exception was for a different reason, return the original error
                        return Response.Error($"Failed to set tag to '{tagToSet}': {ex.Message}.");
                    }
                }
            }

            // Change Layer (using consolidated 'layer' parameter)
            string layerName = @params["layer"]?.ToString();
            if (!string.IsNullOrEmpty(layerName))
            {
                int layerId = LayerMask.NameToLayer(layerName);
                if (layerId == -1 && layerName != "Default")
                {
                    return Response.Error(
                        $"Invalid layer specified: '{layerName}'. Use a valid layer name."
                    );
                }
                if (layerId != -1 && targetGo.layer != layerId)
                {
                    targetGo.layer = layerId;
                    modified = true;
                }
            }

            // Transform Modifications
            Vector3? position = ParseVector3(@params["position"] as JArray);
            Vector3? rotation = ParseVector3(@params["rotation"] as JArray);
            Vector3? scale = ParseVector3(@params["scale"] as JArray);

            if (position.HasValue && targetGo.transform.localPosition != position.Value)
            {
                string positionType = @params["positionType"]?.ToString().ToLower();
                if (string.IsNullOrEmpty(positionType))
                {
                    positionType = "center";
                }

                var positionToSet = position.Value;
                switch (positionType)
                {
                    case "center":
                        var center = ComponentResolver.GetObjectWorldCenter(targetGo);
                        var delta = center - targetGo.transform.position;
                        positionToSet -= delta;
                        break;
                    case "pivot":
                        // no changes
                        break;
                }
                targetGo.transform.localPosition = positionToSet;
                modified = true;
            }
            if (rotation.HasValue && targetGo.transform.localEulerAngles != rotation.Value)
            {
                targetGo.transform.localEulerAngles = rotation.Value;
                modified = true;
            }
            if (scale.HasValue && targetGo.transform.localScale != scale.Value)
            {
                targetGo.transform.localScale = scale.Value;
                modified = true;
            }

            // Change parent / sibling order
            var worldTransformStays = @params["world_transform_stays"]?.ToObject<bool?>() ?? true;
            var hierarchyResult = ApplyHierarchyPlacement(targetGo, @params, worldTransformStays, out var hierarchyChanged, out _);
            if (hierarchyResult != null)
                return hierarchyResult;
            modified |= hierarchyChanged;

            // --- Component Modifications ---
            // Note: These might need more specific Undo recording per component

            // Remove Components
            if (@params["components_to_remove"] is JArray componentsToRemoveArray)
            {
                foreach (var compToken in componentsToRemoveArray)
                {
                    // ... (parsing logic as in CreateGameObject) ...
                    string typeName = compToken.ToString();
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var removeResult = RemoveComponentInternal(targetGo, typeName);
                        if (removeResult != null)
                            return removeResult; // Return error if removal failed
                        modified = true;
                    }
                }
            }

            // Add Components (similar to create)
            if (@params["components_to_add"] is JArray componentsToAddArrayModify)
            {
                foreach (var compToken in componentsToAddArrayModify)
                {
                    string typeName = null;
                    JObject properties = null;
                    if (compToken.Type == JTokenType.String)
                        typeName = compToken.ToString();
                    else if (compToken is JObject compObj)
                    {
                        typeName = compObj["typeName"]?.ToString();
                        properties = compObj["properties"] as JObject;
                    }

                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var addResult = AddComponentInternal(targetGo, typeName, properties);
                        if (addResult != null)
                            return addResult;
                        modified = true;
                    }
                }
            }

            // Set Component Properties
            var componentErrors = new List<object>();
            if (@params["component_properties"] is JObject componentPropertiesObj)
            {
                foreach (var prop in componentPropertiesObj.Properties())
                {
                    string compName = prop.Name;
                    JObject propertiesToSet = prop.Value as JObject;
                    if (propertiesToSet != null)
                    {
                        var setResult = SetComponentPropertiesInternal(
                            targetGo,
                            compName,
                            propertiesToSet
                        );
                        if (setResult != null)
                        {
                            componentErrors.Add(setResult);
                        }
                        else
                        {
                            modified = true;
                        }
                    }
                }
            }

            // Return component errors if any occurred (after processing all components)
            if (componentErrors.Count > 0)
            {
                // Aggregate flattened error strings to make tests/API assertions simpler
                var aggregatedErrors = new List<string>();
                foreach (var errorObj in componentErrors)
                {
                    try
                    {
                        var dataProp = errorObj?.GetType().GetProperty("data");
                        var dataVal = dataProp?.GetValue(errorObj);
                        if (dataVal != null)
                        {
                            var errorsProp = dataVal.GetType().GetProperty("errors");
                            var errorsEnum = errorsProp?.GetValue(dataVal) as System.Collections.IEnumerable;
                            if (errorsEnum != null)
                            {
                                foreach (var item in errorsEnum)
                                {
                                    var s = item?.ToString();
                                    if (!string.IsNullOrEmpty(s)) aggregatedErrors.Add(s);
                                }
                            }
                        }
                    }
                    catch { }
                }

                return Response.Error(
                    $"One or more component property operations failed on '{targetGo.name}'.",
                    new { componentErrors = componentErrors, errors = aggregatedErrors }
                );
            }

            if (!modified)
            {
                // Use the new serializer helper
                // return Response.Success(
                //     $"No modifications applied to GameObject '{targetGo.name}'.",
                //     GetGameObjectData(targetGo));

                return Response.Success(
                    $"No modifications applied to GameObject '{targetGo.name}'.",
                    GameObjectSerializer.GetGameObjectData(targetGo)
                );
            }

            EditorUtility.SetDirty(targetGo); // Mark scene as dirty
            // Use the new serializer helper
            return Response.Success(
                $"GameObject '{targetGo.name}' modified successfully.",
                GameObjectSerializer.GetGameObjectData(targetGo)
            );
            // return Response.Success(
            //     $"GameObject '{targetGo.name}' modified successfully.",
            //     GetGameObjectData(targetGo));

        }

        static object DeleteGameObject(JObject @params, JToken targetToken, string searchMethod)
        {
            // Find potentially multiple objects if name/tag search is used without find_all=false implicitly
            List<GameObject> targets = ResolveGameObjects(targetToken, searchMethod, true, @params); // find_all=true for delete safety

            if (targets.Count == 0)
            {
                return Response.Error(
                    BuildTargetNotFoundMessage(targetToken, searchMethod, @params)
                );
            }

            List<object> deletedObjects = new List<object>();
            foreach (var targetGo in targets)
            {
                if (targetGo != null)
                {
                    string goName = targetGo.name;
                    long goId = UnityApiAdapter.GetObjectId(targetGo);
                    // Use Undo.DestroyObjectImmediate for undo support
                    Undo.DestroyObjectImmediate(targetGo);
                    deletedObjects.Add(new { name = goName, instanceID = goId });
                }
            }

            if (deletedObjects.Count > 0)
            {
                string message =
                    targets.Count == 1
                        ? $"GameObject '{deletedObjects[0].GetType().GetProperty("name").GetValue(deletedObjects[0])}' deleted successfully."
                        : $"{deletedObjects.Count} GameObjects deleted successfully.";
                return Response.Success(message, deletedObjects);
            }
            else
            {
                // Should not happen if targets.Count > 0 initially, but defensive check
                return Response.Error("Failed to delete target GameObject(s).");
            }
        }

        static object FindGameObjects(
            JObject @params,
            JToken targetToken,
            string searchMethod
        )
        {
            bool findAll = @params["find_all"]?.ToObject<bool>() ?? @params["findAll"]?.ToObject<bool>() ?? false;
            List<GameObject> foundObjects = ObjectsHelper.FindObjects(
                targetToken,
                searchMethod,
                findAll,
                @params
            );

            if (foundObjects.Count == 0)
            {
                return Response.Success("No matching GameObjects found.", new List<object>());
            }

            // Use the new serializer helper
            //var results = foundObjects.Select(go => GetGameObjectData(go)).ToList();
            var results = foundObjects.Select(go => GameObjectSerializer.GetGameObjectData(go)).ToList();
            return Response.Success($"Found {results.Count} GameObject(s).", results);
        }

        static object GetComponentsFromTarget(JObject @params, string target, string searchMethod, bool includeNonPublicSerialized = true)
        {
            GameObject targetGo = ResolveGameObject(new JValue(target), searchMethod, @params);
            if (targetGo == null)
            {
                return Response.Error(
                    BuildTargetNotFoundMessage(target, searchMethod, @params)
                );
            }

            try
            {
                // --- Get components, immediately copy to list, and null original array ---
                Component[] originalComponents = targetGo.GetComponents<Component>();
                List<Component> componentsToIterate = new List<Component>(originalComponents ?? Array.Empty<Component>()); // Copy immediately, handle null case
                int componentCount = componentsToIterate.Count;
                originalComponents = null; // Null the original reference
                // Debug.Log($"[GetComponentsFromTarget] Found {componentCount} components on {targetGo.name}. Copied to list, nulled original. Starting REVERSE for loop...");
                // --- End Copy and Null ---

                var componentData = new List<object>();

                for (int i = componentCount - 1; i >= 0; i--) // Iterate backwards over the COPY
                {
                    Component c = componentsToIterate[i]; // Use the copy
                    if (c == null)
                    {
                        // Debug.LogWarning($"[GetComponentsFromTarget REVERSE for] Encountered a null component at index {i} on {targetGo.name}. Skipping.");
                        continue; // Safety check
                    }
                    // Debug.Log($"[GetComponentsFromTarget REVERSE for] Processing component: {c.GetType()?.FullName ?? "null"} (ID: {UnityApiAdapter.GetObjectId(c)}) at index {i} on {targetGo.name}");
                    try
                    {
                        var data = GameObjectSerializer.GetComponentSummaryData(c);
                        if (data != null) // Ensure GetComponentData didn't return null
                        {
                            componentData.Insert(0, data); // Insert at beginning to maintain original order in final list
                        }
                        // else
                        // {
                        //     Debug.LogWarning($"[GetComponentsFromTarget REVERSE for] GetComponentData returned null for component {c.GetType().FullName} (ID: {UnityApiAdapter.GetObjectId(c)}) on {targetGo.name}. Skipping addition.");
                        // }
                    }
                    catch (Exception ex)
                    {
                        long componentId = UnityApiAdapter.GetObjectId(c);
                        Debug.LogError($"[GetComponentsFromTarget REVERSE for] Error processing component {c.GetType().FullName} (ID: {componentId}) on {targetGo.name}: {ex.Message}\n{ex.StackTrace}");
                        componentData.Insert(0, new JObject(
                            new JProperty("typeName", c.GetType().FullName + " (Serialization Error)"),
                            new JProperty("instanceID", componentId),
                            new JProperty("error", ex.Message)
                        ));
                    }
                }
                // Debug.Log($"[GetComponentsFromTarget] Finished REVERSE for loop.");

                // Cleanup the list we created
                componentsToIterate.Clear();
                componentsToIterate = null;

                return Response.Success(
                    $"Retrieved {componentData.Count} components from '{targetGo.name}'.",
                    componentData // List was built in original order
                );
            }
            catch (Exception e)
            {
                return Response.Error(
                    $"Error getting components from '{targetGo.name}': {e.Message}"
                );
            }
        }

        /// <summary>
        /// Gets a single component from the target GameObject and returns its serialized data.
        /// </summary>
        static object GetSingleComponentFromTarget(JObject @params, string target, string searchMethod, string componentName, bool includeNonPublicSerialized = true)
        {
            GameObject targetGo = ResolveGameObject(new JValue(target), searchMethod, @params);
            if (targetGo == null)
            {
                return Response.Error(
                    BuildTargetNotFoundMessage(target, searchMethod, @params)
                );
            }

            try
            {
                // Try to find the component by name using ComponentResolver first
                Component targetComponent = null;
                if (ComponentResolver.TryResolve(componentName, out var compType, out var compError))
                {
                    targetComponent = targetGo.GetComponent(compType);
                }

                // Fallback: search all components for name/type match
                if (targetComponent == null)
                {
                    Component[] allComponents = targetGo.GetComponents<Component>();
                    foreach (Component comp in allComponents)
                    {
                        if (comp != null)
                        {
                            string typeName = comp.GetType().Name;
                            string fullTypeName = comp.GetType().FullName;

                            if (typeName.Equals(componentName, StringComparison.OrdinalIgnoreCase) ||
                                fullTypeName.Equals(componentName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetComponent = comp;
                                break;
                            }
                        }
                    }
                }

                if (targetComponent == null)
                {
                    return Response.Error(
                        $"Component '{componentName}' not found on GameObject '{targetGo.name}'."
                    );
                }

                var componentData = GameObjectSerializer.GetComponentSummaryData(targetComponent);

                if (componentData == null)
                {
                    return Response.Error(
                        $"Failed to serialize component '{componentName}' on GameObject '{targetGo.name}'."
                    );
                }

                return Response.Success(
                    $"Retrieved component '{componentName}' from '{targetGo.name}'.",
                    componentData
                );
            }
            catch (Exception e)
            {
                return Response.Error(
                    $"Error getting component '{componentName}' from '{targetGo.name}': {e.Message}"
                );
            }
        }

        static object AddComponentToTarget(
            JObject @params,
            JToken targetToken,
            string searchMethod
        )
        {
            GameObject targetGo = ResolveGameObject(targetToken, searchMethod, @params);
            if (targetGo == null)
            {
                return Response.Error(
                    BuildTargetNotFoundMessage(targetToken, searchMethod, @params)
                );
            }

            string typeName = null;
            JObject properties = null;

            // Allow adding component specified directly or via componentsToAdd array (take first)
            if (@params["component_name"] != null)
            {
                typeName = @params["component_name"]?.ToString();
                properties = @params["component_properties"]?[typeName] as JObject; // Check if props are nested under name
            }
            else if (
                @params["components_to_add"] is JArray componentsToAddArray
                && componentsToAddArray.Count > 0
            )
            {
                var compToken = componentsToAddArray.First;
                if (compToken.Type == JTokenType.String)
                    typeName = compToken.ToString();
                else if (compToken is JObject compObj)
                {
                    typeName = compObj["typeName"]?.ToString();
                    properties = compObj["properties"] as JObject;
                }
            }

            if (string.IsNullOrEmpty(typeName))
            {
                return Response.Error(
                    "Component type name ('componentName' or first element in 'components_to_add') is required."
                );
            }

            var addResult = AddComponentInternal(targetGo, typeName, properties);
            if (addResult != null)
                return addResult; // Return error

            EditorUtility.SetDirty(targetGo);
            // Use the new serializer helper
            return Response.Success(
                $"Component '{typeName}' added to '{targetGo.name}'.",
                GameObjectSerializer.GetGameObjectData(targetGo)
            ); // Return updated GO data
        }

        static object RemoveComponentFromTarget(
            JObject @params,
            JToken targetToken,
            string searchMethod
        )
        {
            GameObject targetGo = ResolveGameObject(targetToken, searchMethod, @params);
            if (targetGo == null)
            {
                return Response.Error(
                    BuildTargetNotFoundMessage(targetToken, searchMethod, @params)
                );
            }

            string typeName = null;
            // Allow removing component specified directly or via componentsToRemove array (take first)
            if (@params["component_name"] != null)
            {
                typeName = @params["component_name"]?.ToString();
            }
            else if (
                @params["components_to_remove"] is JArray componentsToRemoveArray
                && componentsToRemoveArray.Count > 0
            )
            {
                typeName = componentsToRemoveArray.First?.ToString();
            }

            if (string.IsNullOrEmpty(typeName))
            {
                return Response.Error(
                    "Component type name ('componentName' or first element in 'componentsToRemove') is required."
                );
            }

            var removeResult = RemoveComponentInternal(targetGo, typeName);
            if (removeResult != null)
                return removeResult; // Return error

            EditorUtility.SetDirty(targetGo);
             // Use the new serializer helper
            return Response.Success(
                $"Component '{typeName}' removed from '{targetGo.name}'.",
                GameObjectSerializer.GetGameObjectData(targetGo)
            );
        }

        static object SetComponentPropertyOnTarget(
            JObject @params,
            JToken targetToken,
            string searchMethod
        )
        {
            GameObject targetGo = ResolveGameObject(targetToken, searchMethod, @params);
            if (targetGo == null)
            {
                return Response.Error(
                    BuildTargetNotFoundMessage(targetToken, searchMethod, @params)
                );
            }

            string compName = @params["component_name"]?.ToString();
            JObject propertiesToSet = null;

            if (!string.IsNullOrEmpty(compName))
            {
                // Properties might be directly under componentProperties or nested under the component name
                if (@params["component_properties"] is JObject compProps)
                {
                    propertiesToSet = compProps[compName] as JObject ?? compProps; // Allow flat or nested structure
                }
            }
            else
            {
                return Response.Error("'componentName' parameter is required.");
            }

            if (propertiesToSet == null || !propertiesToSet.HasValues)
            {
                return Response.Error(
                    "'componentProperties' dictionary for the specified component is required and cannot be empty."
                );
            }

            var appliedChanges = new List<object>();
            var setResult = SetComponentPropertiesInternal(targetGo, compName, propertiesToSet, appliedChanges: appliedChanges);
            if (setResult != null)
                return setResult; // Return error

            EditorUtility.SetDirty(targetGo);
            if (targetGo.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(targetGo.scene);
            }
             // Use the new serializer helper
            return Response.Success(
                $"Properties set for component '{compName}' on '{targetGo.name}'.",
                new
                {
                    target = GameObjectSerializer.GetGameObjectData(targetGo),
                    componentName = compName,
                    applied = appliedChanges
                }
            );
        }

        // --- Internal Helpers ---

        static GameObject ResolveGameObject(JToken targetToken, string searchMethod, JObject parameters)
        {
            return SceneObjectLocator.FindObject(targetToken, searchMethod, BuildLocatorParams(parameters));
        }

        static List<GameObject> ResolveGameObjects(JToken targetToken, string searchMethod, bool findAll, JObject parameters)
        {
            return SceneObjectLocator.FindObjects(targetToken, searchMethod, findAll, BuildLocatorParams(parameters));
        }

        static JObject BuildLocatorParams(JObject parameters)
        {
            var result = new JObject();
            if (parameters == null)
            {
                return result;
            }

            if (TryReadBool(parameters, out var includeInactive, "search_inactive", "SearchInactive", "searchInactive", "IncludeInactive", "includeInactive", "include_inactive"))
            {
                result["search_inactive"] = includeInactive;
            }

            if (TryReadBool(parameters, out var searchInChildren, "search_in_children", "SearchInChildren", "searchInChildren", "IncludeChildren", "includeChildren"))
            {
                result["search_in_children"] = searchInChildren;
            }

            if (parameters.TryGetValue("search_term", StringComparison.OrdinalIgnoreCase, out var searchTerm) &&
                searchTerm != null &&
                searchTerm.Type != JTokenType.Null)
            {
                result["search_term"] = searchTerm.DeepClone();
            }

            return result;
        }

        static string BuildTargetNotFoundMessage(object targetToken, string searchMethod, JObject parameters)
        {
            TryReadBool(parameters, out var includeInactive, "search_inactive", "SearchInactive", "searchInactive", "IncludeInactive", "includeInactive", "include_inactive");
            var method = string.IsNullOrWhiteSpace(searchMethod) ? "auto" : searchMethod;
            return $"Target GameObject(s) ('{targetToken}') not found using method '{method}' (IncludeInactive={includeInactive}).";
        }

        static bool TryReadBool(JObject parameters, out bool value, params string[] names)
        {
            value = false;
            if (parameters == null)
            {
                return false;
            }

            foreach (var name in names)
            {
                if (!parameters.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var token) ||
                    token == null ||
                    token.Type == JTokenType.Null)
                {
                    continue;
                }

                if (token.Type == JTokenType.Boolean)
                {
                    value = token.Value<bool>();
                    return true;
                }

                if (bool.TryParse(token.ToString(), out var parsed))
                {
                    value = parsed;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Parses a JArray like [x, y, z] into a Vector3.
        /// </summary>
        static Vector3? ParseVector3(JArray array)
        {
            if (array != null && array.Count == 3)
            {
                try
                {
                    return new Vector3(
                        array[0].ToObject<float>(),
                        array[1].ToObject<float>(),
                        array[2].ToObject<float>()
                    );
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to parse JArray as Vector3: {array}. Error: {ex.Message}");
                }
            }
            return null;
        }

        static object ApplyStaticEditorFlags(GameObject targetGo, JToken token, out bool changed)
        {
            changed = false;
            if (targetGo == null || token == null || token.Type == JTokenType.Null)
                return null;

            if (!TryReadStaticEditorFlags(token, out var flags, out var error))
                return Response.Error(error);

            var before = GameObjectUtility.GetStaticEditorFlags(targetGo);
            if (before == flags)
                return null;

            Undo.RecordObject(targetGo, "Set Static Editor Flags");
            GameObjectUtility.SetStaticEditorFlags(targetGo, flags);
            changed = true;
            return null;
        }

        static bool TryReadStaticEditorFlags(JToken token, out UnityEditor.StaticEditorFlags flags, out string error)
        {
            flags = 0;
            error = null;

            if (token == null || token.Type == JTokenType.Null)
                return true;

            if (token.Type == JTokenType.Integer)
            {
                flags = (UnityEditor.StaticEditorFlags)token.Value<int>();
                return true;
            }

            if (token is JObject obj)
            {
                var nested = obj["flags"] ?? obj["Flags"] ?? obj["values"] ?? obj["Values"] ?? obj["value"] ?? obj["Value"];
                if (nested != null)
                    return TryReadStaticEditorFlags(nested, out flags, out error);
            }

            var parts = new List<string>();
            if (token is JArray array)
            {
                parts.AddRange(array.Select(item => item?.ToString()).Where(text => !string.IsNullOrWhiteSpace(text)));
            }
            else
            {
                parts.AddRange(token.ToString()
                    .Split(new[] { ',', '|', '+' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim())
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            }

            foreach (var part in parts)
            {
                var key = NormalizeFlagToken(part);
                if (key == "none" || key == "nothing")
                {
                    flags = 0;
                    continue;
                }

                if (key == "everything" || key == "all")
                {
                    flags = GetAllStaticEditorFlags();
                    continue;
                }

                if (key == "lightmapstatic")
                    key = "contributegi";

                var match = Enum.GetNames(typeof(UnityEditor.StaticEditorFlags))
                    .FirstOrDefault(name => NormalizeFlagToken(name) == key);
                if (match == null || !Enum.TryParse(match, true, out UnityEditor.StaticEditorFlags parsed))
                {
                    error = $"Invalid StaticEditorFlags value '{part}'. Valid values: None, Everything, {string.Join(", ", Enum.GetNames(typeof(UnityEditor.StaticEditorFlags)))}.";
                    return false;
                }

                flags |= parsed;
            }

            return true;
        }

        static UnityEditor.StaticEditorFlags GetAllStaticEditorFlags()
        {
            var all = (UnityEditor.StaticEditorFlags)0;
            foreach (UnityEditor.StaticEditorFlags value in Enum.GetValues(typeof(UnityEditor.StaticEditorFlags)))
            {
                var numeric = Convert.ToInt32(value);
                if (numeric > 0)
                    all |= value;
            }

            return all;
        }

        static string NormalizeFlagToken(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim()
                    .Replace(" ", string.Empty)
                    .Replace("_", string.Empty)
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        static object ApplyHierarchyPlacement(GameObject targetGo, JObject parameters, bool worldTransformStays, out bool changed, out string message)
        {
            changed = false;
            message = null;
            if (targetGo == null || parameters == null)
                return null;

            var hasParent = parameters["parent"] != null;
            var hasSibling = parameters["sibling"] != null;
            var hasPlacement = parameters["placement"] != null;
            var hasIndex = parameters["sibling_index"] != null;
            if (!hasParent && !hasSibling && !hasPlacement && !hasIndex)
                return null;

            Transform parentTransform = targetGo.transform.parent;
            if (hasParent)
            {
                var parentToken = parameters["parent"];
                if (IsNullOrEmptyParentToken(parentToken))
                {
                    parentTransform = null;
                }
                else
                {
                    var parentGo = ResolveGameObject(parentToken, "by_id_or_name_or_path", parameters);
                    if (parentGo == null)
                        return Response.Error($"Parent specified ('{parentToken}') but not found.");
                    parentTransform = parentGo.transform;
                }
            }

            Transform siblingTransform = null;
            if (hasSibling && !IsNullOrEmptyParentToken(parameters["sibling"]))
            {
                var siblingGo = ResolveGameObject(parameters["sibling"], "by_id_or_name_or_path", parameters);
                if (siblingGo == null)
                    return Response.Error($"Sibling specified ('{parameters["sibling"]}') but not found.");
                if (siblingGo == targetGo)
                    return Response.Error("Sibling cannot be the target GameObject itself.");

                siblingTransform = siblingGo.transform;
                if (!hasParent)
                    parentTransform = siblingTransform.parent;
            }

            if (parentTransform != null && parentTransform.IsChildOf(targetGo.transform))
            {
                return Response.Error(
                    $"Cannot parent '{targetGo.name}' to '{parentTransform.name}', as it would create a hierarchy loop.");
            }

            if (siblingTransform != null && siblingTransform.parent != parentTransform)
            {
                return parentTransform == null
                    ? Response.Error($"Sibling '{siblingTransform.name}' is not a root object.")
                    : Response.Error($"Sibling '{siblingTransform.name}' is not a child of '{parentTransform.name}'.");
            }

            if (targetGo.transform.parent != parentTransform)
            {
                Undo.SetTransformParent(targetGo.transform, parentTransform, worldTransformStays, "Reparent GameObject");
                changed = true;
            }

            if (!TryResolveSiblingIndex(targetGo.transform, parentTransform, siblingTransform, parameters, out var targetIndex, out var error))
                return Response.Error(error);

            if (targetIndex.HasValue)
            {
                var currentIndex = targetGo.transform.GetSiblingIndex();
                var clamped = ClampSiblingIndex(parentTransform, targetIndex.Value);
                if (currentIndex != clamped)
                {
                    Undo.RegisterCompleteObjectUndo(targetGo.transform, "Reorder GameObject Sibling");
                    targetGo.transform.SetSiblingIndex(clamped);
                    changed = true;
                }
            }

            if (changed)
                message = $"Hierarchy placement updated for '{targetGo.name}'.";

            return null;
        }

        static bool IsNullOrEmptyParentToken(JToken token)
        {
            return token == null ||
                   token.Type == JTokenType.Null ||
                   (token.Type == JTokenType.String && string.IsNullOrWhiteSpace(token.ToString()));
        }

        static bool TryResolveSiblingIndex(
            Transform target,
            Transform parent,
            Transform sibling,
            JObject parameters,
            out int? index,
            out string error)
        {
            index = null;
            error = null;

            var placement = parameters["placement"]?.ToString();
            var key = string.IsNullOrWhiteSpace(placement)
                ? string.Empty
                : placement.Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

            if (sibling != null)
            {
                index = sibling.GetSiblingIndex() + (key == "before" ? 0 : 1);
                return true;
            }

            if (parameters["sibling_index"] != null && parameters["sibling_index"].Type != JTokenType.Null)
            {
                if (!int.TryParse(parameters["sibling_index"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    error = $"SiblingIndex '{parameters["sibling_index"]}' is not a valid integer.";
                    return false;
                }

                index = parsed;
                return true;
            }

            switch (key)
            {
                case "":
                    return true;
                case "first":
                    index = 0;
                    return true;
                case "last":
                    index = GetSiblingCount(parent) - 1;
                    return true;
                case "index":
                    error = "Placement=Index requires SiblingIndex.";
                    return false;
                case "before":
                case "after":
                    error = $"Placement={placement} requires Sibling.";
                    return false;
                default:
                    error = $"Invalid Placement '{placement}'. Use Before, After, First, Last, or Index.";
                    return false;
            }
        }

        static int GetSiblingCount(Transform parent)
        {
            if (parent != null)
                return parent.childCount;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return scene.IsValid() ? scene.rootCount : 0;
        }

        static int ClampSiblingIndex(Transform parent, int index)
        {
            var max = Mathf.Max(0, GetSiblingCount(parent) - 1);
            return Mathf.Clamp(index, 0, max);
        }

        // Helper to get all scene objects efficiently
        internal static IEnumerable<GameObject> GetAllSceneObjects(bool includeInactive)
        {
            return SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options
            {
                IncludeInactive = includeInactive,
                IncludePrefabStage = true
            });
        }

        /// <summary>
        /// Adds a component by type name and optionally sets properties.
        /// Returns null on success, or an error response object on failure.
        /// </summary>
        static object AddComponentInternal(
            GameObject targetGo,
            string typeName,
            JObject properties
        )
        {
            Type componentType = FindType(typeName);
            if (componentType == null)
            {
                return Response.Error(
                    $"Component type '{typeName}' not found or is not a valid Component."
                );
            }
            if (!typeof(Component).IsAssignableFrom(componentType))
            {
                return Response.Error($"Type '{typeName}' is not a Component.");
            }

            // Prevent adding Transform again
            if (componentType == typeof(Transform))
            {
                return Response.Error("Cannot add another Transform component.");
            }

            if (typeof(Graphic).IsAssignableFrom(componentType))
            {
                var existingGraphic = targetGo.GetComponents<Graphic>().FirstOrDefault(graphic => graphic != null);
                if (existingGraphic != null)
                {
                    var existingType = existingGraphic.GetType();
                    return Response.Error(
                        $"Object '{targetGo.name}' already has Graphic component '{existingType.Name}'; remove it before adding '{componentType.Name}'."
                    );
                }
            }

            // Check for 2D/3D physics component conflicts
            bool isAdding2DPhysics =
                typeof(Rigidbody2D).IsAssignableFrom(componentType)
                || typeof(Collider2D).IsAssignableFrom(componentType);
            bool isAdding3DPhysics =
                typeof(Rigidbody).IsAssignableFrom(componentType)
                || typeof(Collider).IsAssignableFrom(componentType);

            if (isAdding2DPhysics)
            {
                // Check if the GameObject already has any 3D Rigidbody or Collider
                if (
                    targetGo.GetComponent<Rigidbody>() != null
                    || targetGo.GetComponent<Collider>() != null
                )
                {
                    return Response.Error(
                        $"Cannot add 2D physics component '{typeName}' because the GameObject '{targetGo.name}' already has a 3D Rigidbody or Collider."
                    );
                }
            }
            else if (isAdding3DPhysics)
            {
                // Check if the GameObject already has any 2D Rigidbody or Collider
                if (
                    targetGo.GetComponent<Rigidbody2D>() != null
                    || targetGo.GetComponent<Collider2D>() != null
                )
                {
                    return Response.Error(
                        $"Cannot add 3D physics component '{typeName}' because the GameObject '{targetGo.name}' already has a 2D Rigidbody or Collider."
                    );
                }
            }

            try
            {
                // Use Undo.AddComponent for undo support
                Component newComponent = Undo.AddComponent(targetGo, componentType);
                if (newComponent == null)
                {
                    return Response.Error(
                        $"Failed to add component '{typeName}' to '{targetGo.name}'. It might be disallowed (e.g., adding script twice)."
                    );
                }

                // Set default values for specific component types
                if (newComponent is Light light)
                {
                    // Default newly added lights to directional
                    light.type = LightType.Directional;
                }

                // Set properties if provided
                if (properties != null)
                {
                    var setResult = SetComponentPropertiesInternal(
                        targetGo,
                        typeName,
                        properties,
                        newComponent
                    ); // Pass the new component instance
                    if (setResult != null)
                    {
                        // If setting properties failed, maybe remove the added component?
                        Undo.DestroyObjectImmediate(newComponent);
                        return setResult; // Return the error from setting properties
                    }
                }

                return null; // Success
            }
            catch (Exception e)
            {
                return Response.Error(
                    $"Error adding component '{typeName}' to '{targetGo.name}': {e.Message}"
                );
            }
        }

        /// <summary>
        /// Removes a component by type name.
        /// Returns null on success, or an error response object on failure.
        /// </summary>
        static object RemoveComponentInternal(GameObject targetGo, string typeName)
        {
            Type componentType = FindType(typeName);
            if (componentType == null)
            {
                return Response.Error($"Component type '{typeName}' not found for removal.");
            }

            // Prevent removing essential components
            if (componentType == typeof(Transform))
            {
                return Response.Error("Cannot remove the Transform component.");
            }

            Component componentToRemove = targetGo.GetComponent(componentType);
            if (componentToRemove == null)
            {
                return Response.Error(
                    $"Component '{typeName}' not found on '{targetGo.name}' to remove."
                );
            }

            try
            {
                // Use Undo.DestroyObjectImmediate for undo support
                Undo.DestroyObjectImmediate(componentToRemove);
                return null; // Success
            }
            catch (Exception e)
            {
                return Response.Error(
                    $"Error removing component '{typeName}' from '{targetGo.name}': {e.Message}"
                );
            }
        }

        /// <summary>
        /// Sets properties on a component.
        /// Returns null on success, or an error response object on failure.
        /// </summary>
        static object SetComponentPropertiesInternal(
            GameObject targetGo,
            string compName,
            JObject propertiesToSet,
            Component targetComponentInstance = null,
            List<object> appliedChanges = null
        )
        {
            Component targetComponent = targetComponentInstance;
            if (targetComponent == null)
            {
                if (ComponentResolver.TryResolve(compName, out var compType, out var compError))
                {
                    targetComponent = targetGo.GetComponent(compType);
                }
                else
                {
                    targetComponent = targetGo.GetComponent(compName); // fallback to string-based lookup
                }
            }
            if (targetComponent == null)
            {
                return Response.Error(
                    $"Component '{compName}' not found on '{targetGo.name}' to set properties."
                );
            }

            Undo.RecordObject(targetComponent, "Set Component Properties");

            var failures = new List<string>();
            using var serializedObject = new SerializedObject(targetComponent);
            serializedObject.Update();
            var serializedChanged = false;

            foreach (var prop in propertiesToSet.Properties())
            {
                string propName = prop.Name;
                JToken propValue = prop.Value;

                try
                {
                    if (TryApplyTextMeshProFont(targetGo, targetComponent, serializedObject, propName, propValue, out var tmpFontReport, out var tmpFontError))
                    {
                        if (tmpFontError != null)
                        {
                            Debug.LogWarning($"[ManageGameObject] {tmpFontError}");
                            failures.Add(tmpFontError);
                        }
                        else
                        {
                            serializedChanged = true;
                            appliedChanges?.Add(tmpFontReport);
                        }

                        continue;
                    }

                    if (TryApplyRendererMaterial(targetComponent, propName, propValue, out var rendererReport, out var rendererError))
                    {
                        if (rendererError != null)
                        {
                            Debug.LogWarning($"[ManageGameObject] {rendererError}");
                            failures.Add(rendererError);
                        }
                        else
                        {
                            appliedChanges?.Add(rendererReport);
                        }

                        continue;
                    }

                    var serializedResult = SerializedPropertyPatcher.TryApplyProperty(
                        targetComponent,
                        serializedObject,
                        propName,
                        propValue,
                        dryRun: false);

                    if (serializedResult.Found)
                    {
                        if (serializedResult.Success)
                        {
                            serializedChanged = serializedChanged || serializedResult.Changes.Count > 0;
                            foreach (var change in serializedResult.Changes)
                            {
                                appliedChanges?.Add(new
                                {
                                    requestedName = change.requestedName,
                                    propertyPath = change.propertyPath,
                                    propertyType = change.propertyType,
                                    before = change.before,
                                    after = change.after,
                                    dryRun = change.dryRun,
                                    route = "serializedProperty"
                                });
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[ManageGameObject] {serializedResult.Error}");
                            failures.Add(serializedResult.Error);
                        }

                        continue;
                    }

                    if (TrySetUnityObjectMember(targetComponent, propName, propValue, out var objectMemberReport, out var objectMemberError))
                    {
                        if (objectMemberError != null)
                        {
                            Debug.LogWarning($"[ManageGameObject] {objectMemberError}");
                            failures.Add(objectMemberError);
                        }
                        else
                        {
                            appliedChanges?.Add(objectMemberReport);
                        }

                        continue;
                    }

                    bool setResult = SetProperty(targetComponent, propName, propValue);
                    if (!setResult)
                    {
                        var availableProperties = ComponentResolver.GetAllComponentProperties(targetComponent.GetType());
                        var suggestions = ComponentResolver.GetAIPropertySuggestions(propName, availableProperties);
                        var msg = suggestions.Any()
                            ? $"Property '{propName}' not found. Did you mean: {string.Join(", ", suggestions)}? Available: [{string.Join(", ", availableProperties)}]"
                            : $"Property '{propName}' not found. Available: [{string.Join(", ", availableProperties)}]";
                        Debug.LogWarning($"[ManageGameObject] {msg}");
                        failures.Add(msg);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError(
                        $"[ManageGameObject] Error setting property '{propName}' on '{compName}': {e.Message}"
                    );
                    failures.Add($"Error setting '{propName}': {e.Message}");
                }
            }

            if (serializedChanged)
            {
                serializedObject.ApplyModifiedProperties();
            }

            EditorUtility.SetDirty(targetComponent);
            return failures.Count == 0
                ? null
                : Response.Error($"One or more properties failed on '{compName}'.", new { errors = failures });
        }

        static bool TryApplyTextMeshProFont(
            GameObject targetGo,
            Component targetComponent,
            SerializedObject serializedObject,
            string propName,
            JToken propValue,
            out object report,
            out string error)
        {
            report = null;
            error = null;
            if (!IsTextMeshProComponent(targetComponent))
            {
                return false;
            }

            var key = NormalizeMemberKey(propName);
            if (key != "font" &&
                key != "fontasset" &&
                key != "fontassetpath" &&
                key != "tmpfontasset" &&
                key != "tmpfontassetpath")
            {
                return false;
            }

            try
            {
                var fontType = ResolveTextMeshProFontAssetType(targetComponent) ?? typeof(UnityEngine.Object);
                var fontAsset = SerializedPropertyPatcher.ResolveObjectReferenceValue(fontType, propValue);
                if (fontAsset == null)
                {
                    error = $"TextMeshPro font property '{propName}' resolved to null. Provide a valid TMP_FontAsset path/GUID/objectId.";
                    return true;
                }

                var material = ResolveTextMeshProSharedMaterial(fontAsset, propValue);
                var before = BuildTextMeshProFontState(targetGo, targetComponent);

                var fontProperty = serializedObject.FindProperty("m_fontAsset");
                if (fontProperty == null || !fontProperty.editable)
                {
                    error = $"TextMeshPro component '{targetComponent.GetType().FullName}' does not expose editable serialized property 'm_fontAsset'.";
                    return true;
                }

                fontProperty.objectReferenceValue = fontAsset;

                var materialChanged = false;
                var materialWarning = (string)null;
                var sharedMaterialProperty = serializedObject.FindProperty("m_sharedMaterial");
                if (sharedMaterialProperty != null && sharedMaterialProperty.editable && material != null)
                {
                    sharedMaterialProperty.objectReferenceValue = material;
                    materialChanged = true;
                }
                else if (sharedMaterialProperty != null && material == null)
                {
                    materialWarning = "TMP font asset was changed, but no matching shared material could be resolved from the font asset; m_sharedMaterial was left unchanged.";
                }
                else if (sharedMaterialProperty == null)
                {
                    materialWarning = "TMP font asset was changed, but this TMP component did not expose m_sharedMaterial; material was left unchanged.";
                }

                serializedObject.ApplyModifiedProperties();
                serializedObject.Update();

                TrySetUnityObjectMemberValue(targetComponent, "font", fontAsset);
                if (material != null)
                {
                    TrySetUnityObjectMemberValue(targetComponent, "fontSharedMaterial", material);
                    if (targetGo != null && targetGo.TryGetComponent<Renderer>(out var renderer))
                    {
                        renderer.sharedMaterial = material as Material;
                        EditorUtility.SetDirty(renderer);
                    }
                }

                EditorUtility.SetDirty(targetComponent);
                var after = BuildTextMeshProFontState(targetGo, targetComponent);
                report = new
                {
                    requestedName = propName,
                    route = "textMeshProFont",
                    propertyPath = "m_fontAsset",
                    syncedMaterialPropertyPath = sharedMaterialProperty?.propertyPath,
                    before,
                    after,
                    assignedFontAsset = BuildObjectReferenceInfo(fontAsset),
                    assignedSharedMaterial = BuildObjectReferenceInfo(material),
                    sharedMaterialChanged = materialChanged,
                    warning = materialWarning
                };
            }
            catch (Exception ex)
            {
                error = $"TextMeshPro font property '{propName}' could not be applied: {ex.Message}";
            }

            return true;
        }

        static bool TryApplyRendererMaterial(
            Component targetComponent,
            string propName,
            JToken propValue,
            out object report,
            out string error)
        {
            report = null;
            error = null;
            var renderer = targetComponent as Renderer;
            if (renderer == null)
            {
                return false;
            }

            var key = NormalizeMemberKey(propName);
            if (key == "sharedmaterial")
            {
                try
                {
                    var before = renderer.sharedMaterial;
                    var material = SerializedPropertyPatcher.ResolveObjectReferenceValue(typeof(Material), propValue) as Material;
                    if (material == null && propValue != null && propValue.Type != JTokenType.Null)
                    {
                        error = $"Renderer sharedMaterial payload for '{propName}' resolved to null. Use an asset path/GUID or {{guid,fileID,type}} for a Material subasset.";
                        return true;
                    }

                    renderer.sharedMaterial = material;
                    EditorUtility.SetDirty(renderer);
                    report = new
                    {
                        requestedName = propName,
                        route = "rendererSharedMaterial",
                        before = BuildObjectReferenceInfo(before),
                        after = BuildObjectReferenceInfo(renderer.sharedMaterial)
                    };
                }
                catch (Exception ex)
                {
                    error = $"Renderer sharedMaterial could not be set: {ex.Message}";
                }

                return true;
            }

            if (key == "sharedmaterials")
            {
                try
                {
                    var before = renderer.sharedMaterials?.Select(BuildObjectReferenceInfo).ToArray() ?? Array.Empty<object>();
                    var materials = ResolveMaterialArray(propValue);
                    renderer.sharedMaterials = materials;
                    EditorUtility.SetDirty(renderer);
                    report = new
                    {
                        requestedName = propName,
                        route = "rendererSharedMaterials",
                        before,
                        after = renderer.sharedMaterials?.Select(BuildObjectReferenceInfo).ToArray() ?? Array.Empty<object>()
                    };
                }
                catch (Exception ex)
                {
                    error = $"Renderer sharedMaterials could not be set: {ex.Message}";
                }

                return true;
            }

            return false;
        }

        static bool TrySetUnityObjectMember(
            Component targetComponent,
            string propName,
            JToken propValue,
            out object report,
            out string error)
        {
            report = null;
            error = null;
            if (targetComponent == null || string.IsNullOrWhiteSpace(propName))
            {
                return false;
            }

            var member = FindWritableObjectMember(targetComponent.GetType(), propName);
            if (member.Member == null)
            {
                return false;
            }

            try
            {
                var beforeValue = member.GetValue(targetComponent);
                object afterValue;
                if (member.IsObjectArray)
                {
                    afterValue = ResolveObjectArray(propValue, member.ElementType);
                }
                else
                {
                    afterValue = SerializedPropertyPatcher.ResolveObjectReferenceValue(member.MemberType, propValue);
                }

                member.SetValue(targetComponent, afterValue);
                EditorUtility.SetDirty(targetComponent);
                var currentValue = member.GetValue(targetComponent);
                report = new
                {
                    requestedName = propName,
                    route = member.IsField ? "unityObjectField" : "unityObjectProperty",
                    memberType = member.MemberType.FullName,
                    before = SerializeMemberObjectValue(beforeValue),
                    after = SerializeMemberObjectValue(currentValue),
                    resolved = propValue == null || propValue.Type == JTokenType.Null || currentValue != null
                };
            }
            catch (Exception ex)
            {
                error = $"Unity object reference member '{propName}' on '{targetComponent.GetType().FullName}' could not be set: {ex.Message}";
            }

            return true;
        }

        static bool IsTextMeshProComponent(Component component)
        {
            var type = component?.GetType();
            while (type != null)
            {
                var fullName = type.FullName ?? string.Empty;
                if (fullName == "TMPro.TMP_Text" ||
                    fullName == "TMPro.TextMeshPro" ||
                    fullName == "TMPro.TextMeshProUGUI")
                {
                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }

        static Type ResolveTextMeshProFontAssetType(Component component)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var fontProperty = component?.GetType().GetProperty("font", flags);
            if (fontProperty != null && typeof(UnityEngine.Object).IsAssignableFrom(fontProperty.PropertyType))
            {
                return fontProperty.PropertyType;
            }

            return FindLoadedUnityObjectType("TMPro.TMP_FontAsset");
        }

        static UnityEngine.Object ResolveTextMeshProSharedMaterial(UnityEngine.Object fontAsset, JToken fontPayload)
        {
            if (fontPayload is JObject obj)
            {
                var materialToken =
                    obj["sharedMaterial"] ??
                    obj["shared_material"] ??
                    obj["material"] ??
                    obj["materialPath"] ??
                    obj["material_path"];
                if (materialToken != null)
                {
                    return SerializedPropertyPatcher.ResolveObjectReferenceValue(typeof(Material), materialToken);
                }
            }

            var reflected = ReadUnityObjectMember(fontAsset, "material", "material_EditorRef", "m_Material");
            if (reflected is Material)
            {
                return reflected;
            }

            var assetPath = AssetDatabase.GetAssetPath(fontAsset);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return null;
            }

            return AssetDatabase.LoadAllAssetsAtPath(assetPath)
                .OfType<Material>()
                .FirstOrDefault();
        }

        static object BuildTextMeshProFontState(GameObject targetGo, Component textComponent)
        {
            UnityEngine.Object serializedFont = null;
            UnityEngine.Object serializedMaterial = null;
            try
            {
                using var so = new SerializedObject(textComponent);
                serializedFont = so.FindProperty("m_fontAsset")?.objectReferenceValue;
                serializedMaterial = so.FindProperty("m_sharedMaterial")?.objectReferenceValue;
            }
            catch
            {
                // Best-effort diagnostics only.
            }

            var font = ReadUnityObjectMember(textComponent, "font") ?? serializedFont;
            var fontSharedMaterial = ReadUnityObjectMember(textComponent, "fontSharedMaterial", "sharedMaterial") ?? serializedMaterial;
            var rendererMaterial = targetGo != null && targetGo.TryGetComponent<Renderer>(out var renderer)
                ? renderer.sharedMaterial
                : null;

            return new
            {
                font = BuildObjectReferenceInfo(font),
                serializedFontAsset = BuildObjectReferenceInfo(serializedFont),
                fontSharedMaterial = BuildObjectReferenceInfo(fontSharedMaterial),
                serializedSharedMaterial = BuildObjectReferenceInfo(serializedMaterial),
                rendererSharedMaterial = BuildObjectReferenceInfo(rendererMaterial)
            };
        }

        static bool TrySetUnityObjectMemberValue(object target, string memberName, UnityEngine.Object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName))
            {
                return false;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var property = target.GetType().GetProperty(memberName, flags);
            if (property != null && property.CanWrite && typeof(UnityEngine.Object).IsAssignableFrom(property.PropertyType))
            {
                property.SetValue(target, value);
                return true;
            }

            var field = target.GetType().GetField(memberName, flags);
            if (field != null && typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
            {
                field.SetValue(target, value);
                return true;
            }

            return false;
        }

        static UnityEngine.Object ReadUnityObjectMember(object target, params string[] memberNames)
        {
            if (target == null || memberNames == null)
            {
                return null;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            foreach (var memberName in memberNames)
            {
                if (string.IsNullOrWhiteSpace(memberName))
                {
                    continue;
                }

                var property = target.GetType().GetProperty(memberName, flags);
                if (property != null && property.CanRead && typeof(UnityEngine.Object).IsAssignableFrom(property.PropertyType))
                {
                    return property.GetValue(target) as UnityEngine.Object;
                }

                var field = target.GetType().GetField(memberName, flags);
                if (field != null && typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                {
                    return field.GetValue(target) as UnityEngine.Object;
                }
            }

            return null;
        }

        static Material[] ResolveMaterialArray(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return Array.Empty<Material>();
            }

            if (token is not JArray array)
            {
                var material = SerializedPropertyPatcher.ResolveObjectReferenceValue(typeof(Material), token) as Material;
                if (material == null)
                {
                    throw new ArgumentException("Material payload resolved to null.");
                }

                return new[] { material };
            }

            var materials = new Material[array.Count];
            for (var i = 0; i < array.Count; i++)
            {
                materials[i] = SerializedPropertyPatcher.ResolveObjectReferenceValue(typeof(Material), array[i]) as Material;
                if (materials[i] == null && array[i] != null && array[i].Type != JTokenType.Null)
                {
                    throw new ArgumentException($"Material payload at index {i} resolved to null.");
                }
            }

            return materials;
        }

        static Array ResolveObjectArray(JToken token, Type elementType)
        {
            if (elementType == null || !typeof(UnityEngine.Object).IsAssignableFrom(elementType))
            {
                throw new ArgumentException("Object array element type must derive from UnityEngine.Object.");
            }

            if (token == null || token.Type == JTokenType.Null)
            {
                return Array.CreateInstance(elementType, 0);
            }

            var array = token as JArray ?? new JArray(token.DeepClone());
            var result = Array.CreateInstance(elementType, array.Count);
            for (var i = 0; i < array.Count; i++)
            {
                var value = SerializedPropertyPatcher.ResolveObjectReferenceValue(elementType, array[i]);
                result.SetValue(value, i);
            }

            return result;
        }

        static ObjectMemberAccessor FindWritableObjectMember(Type type, string memberName)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var property = type.GetProperty(memberName, flags);
            if (property != null && property.CanWrite)
            {
                var memberType = property.PropertyType;
                if (typeof(UnityEngine.Object).IsAssignableFrom(memberType))
                {
                    return ObjectMemberAccessor.ForProperty(property, memberType);
                }

                if (memberType.IsArray && typeof(UnityEngine.Object).IsAssignableFrom(memberType.GetElementType()))
                {
                    return ObjectMemberAccessor.ForProperty(property, memberType);
                }
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                var memberType = field.FieldType;
                if (typeof(UnityEngine.Object).IsAssignableFrom(memberType))
                {
                    return ObjectMemberAccessor.ForField(field, memberType);
                }

                if (memberType.IsArray && typeof(UnityEngine.Object).IsAssignableFrom(memberType.GetElementType()))
                {
                    return ObjectMemberAccessor.ForField(field, memberType);
                }
            }

            return default;
        }

        static object SerializeMemberObjectValue(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is UnityEngine.Object obj)
            {
                return BuildObjectReferenceInfo(obj);
            }

            if (value is Array array)
            {
                return array.Cast<object>()
                    .Select(item => item is UnityEngine.Object objectValue ? BuildObjectReferenceInfo(objectValue) : null)
                    .ToArray();
            }

            return value.ToString();
        }

        static object BuildObjectReferenceInfo(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return null;
            }

            var objectId = UnityApiAdapter.GetObjectId(obj);
            if (obj is GameObject gameObject)
            {
                return new
                {
                    type = "GameObject",
                    name = gameObject.name,
                    path = BuildHierarchyPath(gameObject),
                    objectId,
                    objectIdString = objectId.ToString(CultureInfo.InvariantCulture)
                };
            }

            if (obj is Component component)
            {
                return new
                {
                    type = component.GetType().Name,
                    fullType = component.GetType().FullName,
                    name = component.name,
                    path = BuildHierarchyPath(component.gameObject),
                    objectId,
                    objectIdString = objectId.ToString(CultureInfo.InvariantCulture),
                    gameObject = new
                    {
                        name = component.gameObject.name,
                        path = BuildHierarchyPath(component.gameObject)
                    }
                };
            }

            var assetPath = AssetDatabase.GetAssetPath(obj);
            return new
            {
                type = obj.GetType().Name,
                fullType = obj.GetType().FullName,
                name = obj.name,
                path = string.IsNullOrWhiteSpace(assetPath) ? null : assetPath,
                guid = string.IsNullOrWhiteSpace(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath),
                fileID = GetLocalIdentifier(obj),
                objectId,
                objectIdString = objectId.ToString(CultureInfo.InvariantCulture)
            };
        }

        static string BuildHierarchyPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var parts = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }

        static string GetLocalIdentifier(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return null;
            }

            try
            {
                var method = typeof(Unsupported).GetMethod(
                    "GetLocalIdentifierInFileForPersistentObject",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var value = method?.Invoke(null, new object[] { obj });
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        static string NormalizeMemberKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            if (normalized.StartsWith("m", StringComparison.OrdinalIgnoreCase) && normalized.Length > 1)
            {
                normalized = normalized.Substring(1);
            }

            return normalized.ToLowerInvariant();
        }

        static Type FindLoadedUnityObjectType(string fullNameOrName)
        {
            if (string.IsNullOrWhiteSpace(fullNameOrName))
            {
                return null;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (var type in types)
                {
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (string.Equals(type.FullName, fullNameOrName, StringComparison.Ordinal) ||
                        string.Equals(type.Name, fullNameOrName, StringComparison.Ordinal))
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        readonly struct ObjectMemberAccessor
        {
            public MemberInfo Member { get; }
            public Type MemberType { get; }
            public Type ElementType { get; }
            public bool IsObjectArray { get; }
            public bool IsField => Member is FieldInfo;

            ObjectMemberAccessor(MemberInfo member, Type memberType)
            {
                Member = member;
                MemberType = memberType;
                ElementType = memberType.IsArray ? memberType.GetElementType() : null;
                IsObjectArray = ElementType != null && typeof(UnityEngine.Object).IsAssignableFrom(ElementType);
            }

            public static ObjectMemberAccessor ForProperty(PropertyInfo property, Type memberType) => new(property, memberType);
            public static ObjectMemberAccessor ForField(FieldInfo field, Type memberType) => new(field, memberType);

            public object GetValue(object target)
            {
                return Member switch
                {
                    PropertyInfo property => property.GetValue(target),
                    FieldInfo field => field.GetValue(target),
                    _ => null
                };
            }

            public void SetValue(object target, object value)
            {
                switch (Member)
                {
                    case PropertyInfo property:
                        property.SetValue(target, value);
                        break;
                    case FieldInfo field:
                        field.SetValue(target, value);
                        break;
                }
            }
        }

        /// <summary>
        /// Helper to set a property or field via reflection, handling basic types.
        /// </summary>
        static bool SetProperty(object target, string memberName, JToken value)
        {
            Type type = target.GetType();
            BindingFlags flags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

            // Use shared serializer to avoid per-call allocation
            var inputSerializer = InputSerializer;

            try
            {
                // Handle special case for materials with dot notation (material.property)
                // Examples: material.color, sharedMaterial.color, materials[0].color
                if (memberName.Contains('.') || memberName.Contains('['))
                {
                    // Pass the inputSerializer down for nested conversions
                    return SetNestedProperty(target, memberName, value, inputSerializer);
                }

                PropertyInfo propInfo = type.GetProperty(memberName, flags);
                if (propInfo != null && propInfo.CanWrite)
                {
                    // Use the inputSerializer for conversion
                    object convertedValue = ConvertJTokenToType(value, propInfo.PropertyType, inputSerializer);
                    if (convertedValue != null || value.Type == JTokenType.Null) // Allow setting null
                    {
                        propInfo.SetValue(target, convertedValue);
                        return true;
                    }
                    else
                    {
                        Debug.LogWarning($"[SetProperty] Conversion failed for property '{memberName}' (Type: {propInfo.PropertyType.Name}) from token: {value.ToString(Formatting.None)}");
                    }
                }
                else
                {
                    FieldInfo fieldInfo = type.GetField(memberName, flags);
                    if (fieldInfo != null) // Check if !IsLiteral?
                    {
                        // Use the inputSerializer for conversion
                        object convertedValue = ConvertJTokenToType(value, fieldInfo.FieldType, inputSerializer);
                        if (convertedValue != null || value.Type == JTokenType.Null) // Allow setting null
                        {
                            fieldInfo.SetValue(target, convertedValue);
                            return true;
                        }
                        else
                        {
                            Debug.LogWarning($"[SetProperty] Conversion failed for field '{memberName}' (Type: {fieldInfo.FieldType.Name}) from token: {value.ToString(Formatting.None)}");
                        }
                    }
                    else
                    {
                        // Try NonPublic [SerializeField] fields
                        var npField = type.GetField(memberName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (npField != null && npField.GetCustomAttribute<SerializeField>() != null)
                        {
                            object convertedValue = ConvertJTokenToType(value, npField.FieldType, inputSerializer);
                            if (convertedValue != null || value.Type == JTokenType.Null)
                            {
                                npField.SetValue(target, convertedValue);
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[SetProperty] Failed to set '{memberName}' on {type.Name}: {ex.Message}\nToken: {value.ToString(Formatting.None)}"
                );
            }
            return false;
        }

        /// <summary>
        /// Sets a nested property using dot notation (e.g., "material.color") or array access (e.g., "materials[0]")
        /// </summary>
        // Pass the input serializer for conversions
        //Using the serializer helper
        static bool SetNestedProperty(object target, string path, JToken value, JsonSerializer inputSerializer)
        {
            try
            {
                // Split the path into parts (handling both dot notation and array indexing)
                string[] pathParts = SplitPropertyPath(path);
                if (pathParts.Length == 0)
                    return false;

                object currentObject = target;
                Type currentType = currentObject.GetType();
                BindingFlags flags =
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

                // Traverse the path until we reach the final property
                for (int i = 0; i < pathParts.Length - 1; i++)
                {
                    string part = pathParts[i];
                    bool isArray = false;
                    int arrayIndex = -1;

                    // Check if this part contains array indexing
                    if (part.Contains("["))
                    {
                        int startBracket = part.IndexOf('[');
                        int endBracket = part.IndexOf(']');
                        if (startBracket > 0 && endBracket > startBracket)
                        {
                            string indexStr = part.Substring(
                                startBracket + 1,
                                endBracket - startBracket - 1
                            );
                            if (int.TryParse(indexStr, out arrayIndex))
                            {
                                isArray = true;
                                part = part.Substring(0, startBracket);
                            }
                        }
                    }
                    // Get the property/field
                    PropertyInfo propInfo = currentType.GetProperty(part, flags);
                    FieldInfo fieldInfo = null;
                    if (propInfo == null)
                    {
                        fieldInfo = currentType.GetField(part, flags);
                        if (fieldInfo == null)
                        {
                            Debug.LogWarning(
                                $"[SetNestedProperty] Could not find property or field '{part}' on type '{currentType.Name}'"
                            );
                            return false;
                        }
                    }

                    // Get the value
                    currentObject =
                        propInfo != null
                            ? propInfo.GetValue(currentObject)
                            : fieldInfo.GetValue(currentObject);
                    //Need to stop if current property is null
                    if (currentObject == null)
                    {
                        Debug.LogWarning(
                            $"[SetNestedProperty] Property '{part}' is null, cannot access nested properties."
                        );
                        return false;
                    }
                    // If this part was an array or list, access the specific index
                    if (isArray)
                    {
                        if (currentObject is Material[])
                        {
                            var materials = currentObject as Material[];
                            if (arrayIndex < 0 || arrayIndex >= materials.Length)
                            {
                                Debug.LogWarning(
                                    $"[SetNestedProperty] Material index {arrayIndex} out of range (0-{materials.Length - 1})"
                                );
                                return false;
                            }
                            currentObject = materials[arrayIndex];
                        }
                        else if (currentObject is System.Collections.IList)
                        {
                            var list = currentObject as System.Collections.IList;
                            if (arrayIndex < 0 || arrayIndex >= list.Count)
                            {
                                Debug.LogWarning(
                                    $"[SetNestedProperty] Index {arrayIndex} out of range (0-{list.Count - 1})"
                                );
                                return false;
                            }
                            currentObject = list[arrayIndex];
                        }
                        else
                        {
                            Debug.LogWarning(
                                $"[SetNestedProperty] Property '{part}' is not an array or list, cannot access by index."
                            );
                            return false;
                        }
                    }
                    currentType = currentObject.GetType();
                }

                // Set the final property
                string finalPart = pathParts[pathParts.Length - 1];

                // Special handling for Material properties (shader properties)
                if (currentObject is Material material && finalPart.StartsWith("_"))
                {
                    // Use the serializer to convert the JToken value first
                    if (value is JArray jArray)
                    {
                        // Try converting to known types that SetColor/SetVector accept
                        if (jArray.Count == 4) {
                            try { Color color = value.ToObject<Color>(inputSerializer); material.SetColor(finalPart, color); return true; } catch { }
                            try { Vector4 vec = value.ToObject<Vector4>(inputSerializer); material.SetVector(finalPart, vec); return true; } catch { }
                        } else if (jArray.Count == 3) {
                            try { Color color = value.ToObject<Color>(inputSerializer); material.SetColor(finalPart, color); return true; } catch { } // ToObject handles conversion to Color
                        } else if (jArray.Count == 2) {
                            try { Vector2 vec = value.ToObject<Vector2>(inputSerializer); material.SetVector(finalPart, vec); return true; } catch { }
                        }
                    }
                    else if (value.Type == JTokenType.Float || value.Type == JTokenType.Integer)
                    {
                        try { material.SetFloat(finalPart, value.ToObject<float>(inputSerializer)); return true; } catch { }
                    }
                    else if (value.Type == JTokenType.Boolean)
                    {
                        try { material.SetFloat(finalPart, value.ToObject<bool>(inputSerializer) ? 1f : 0f); return true; } catch { }
                    }
                    else if (value.Type == JTokenType.String)
                    {
                        // Try converting to Texture using the serializer/converter
                        try {
                            Texture texture = value.ToObject<Texture>(inputSerializer);
                            if (texture != null) {
                                material.SetTexture(finalPart, texture);
                                return true;
                            }
                        } catch { }
                    }

                    Debug.LogWarning(
                        $"[SetNestedProperty] Unsupported or failed conversion for material property '{finalPart}' from value: {value.ToString(Formatting.None)}"
                    );
                    return false;
                }

                // For standard properties (not shader specific)
                PropertyInfo finalPropInfo = currentType.GetProperty(finalPart, flags);
                if (finalPropInfo != null && finalPropInfo.CanWrite)
                {
                    // Use the inputSerializer for conversion
                    object convertedValue = ConvertJTokenToType(value, finalPropInfo.PropertyType, inputSerializer);
                    if (convertedValue != null || value.Type == JTokenType.Null)
                    {
                        finalPropInfo.SetValue(currentObject, convertedValue);
                        return true;
                    }
                    else
                    {
                        Debug.LogWarning($"[SetNestedProperty] Final conversion failed for property '{finalPart}' (Type: {finalPropInfo.PropertyType.Name}) from token: {value.ToString(Formatting.None)}");
                    }
                }
                else
                {
                    FieldInfo finalFieldInfo = currentType.GetField(finalPart, flags);
                    if (finalFieldInfo != null)
                    {
                        // Use the inputSerializer for conversion
                        object convertedValue = ConvertJTokenToType(value, finalFieldInfo.FieldType, inputSerializer);
                        if (convertedValue != null || value.Type == JTokenType.Null)
                        {
                            finalFieldInfo.SetValue(currentObject, convertedValue);
                            return true;
                        }
                        else
                        {
                            Debug.LogWarning($"[SetNestedProperty] Final conversion failed for field '{finalPart}' (Type: {finalFieldInfo.FieldType.Name}) from token: {value.ToString(Formatting.None)}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[SetNestedProperty] Could not find final writable property or field '{finalPart}' on type '{currentType.Name}'"
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[SetNestedProperty] Error setting nested property '{path}': {ex.Message}\nToken: {value.ToString(Formatting.None)}"
                );
            }

            return false;
        }


        /// <summary>
        /// Split a property path into parts, handling both dot notation and array indexers
        /// </summary>
        static string[] SplitPropertyPath(string path)
        {
            // Handle complex paths with both dots and array indexers
            List<string> parts = new List<string>();
            int startIndex = 0;
            bool inBrackets = false;

            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];

                if (c == '[')
                {
                    inBrackets = true;
                }
                else if (c == ']')
                {
                    inBrackets = false;
                }
                else if (c == '.' && !inBrackets)
                {
                    // Found a dot separator outside of brackets
                    parts.Add(path.Substring(startIndex, i - startIndex));
                    startIndex = i + 1;
                }
            }
            if (startIndex < path.Length)
            {
                parts.Add(path.Substring(startIndex));
            }
            return parts.ToArray();
        }

        /// <summary>
        /// Simple JToken to Type conversion for common Unity types, using JsonSerializer.
        /// </summary>
        // Pass the input serializer
        static object ConvertJTokenToType(JToken token, Type targetType, JsonSerializer inputSerializer)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                {
                    Debug.LogWarning($"Cannot assign null to non-nullable value type {targetType.Name}. Returning default value.");
                    return Activator.CreateInstance(targetType);
                }
                return null;
            }

            try
            {
                if (targetType.IsArray && typeof(UnityEngine.Object).IsAssignableFrom(targetType.GetElementType()))
                {
                    return ResolveObjectArray(token, targetType.GetElementType());
                }

                if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
                {
                    return SerializedPropertyPatcher.ResolveObjectReferenceValue(targetType, token);
                }

                // Use the provided serializer instance which includes our custom converters
                return token.ToObject(targetType, inputSerializer);
            }
            catch (JsonSerializationException jsonEx)
            {
                 Debug.LogError($"JSON Deserialization Error converting token to {targetType.FullName}: {jsonEx.Message}\nToken: {token.ToString(Formatting.None)}");
                 // Optionally re-throw or return null/default
                 // return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
                 throw; // Re-throw to indicate failure higher up
            }
            catch (ArgumentException argEx)
            {
                Debug.LogError($"Argument Error converting token to {targetType.FullName}: {argEx.Message}\nToken: {token.ToString(Formatting.None)}");
                 throw;
            }
            catch (Exception ex)
            {
                 Debug.LogError($"Unexpected error converting token to {targetType.FullName}: {ex}\nToken: {token.ToString(Formatting.None)}");
                 throw;
            }
            // If ToObject succeeded, it would have returned. If it threw, we wouldn't reach here.
             // This fallback logic is likely unreachable if ToObject covers all cases or throws on failure.
             // Debug.LogWarning($"Conversion failed for token to {targetType.FullName}. Token: {token.ToString(Formatting.None)}");
             // return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
        }

        // --- ParseJTokenTo... helpers are likely redundant now with the serializer approach ---
        // Keep them temporarily for reference or if specific fallback logic is ever needed.

        static Vector3 ParseJTokenToVector3(JToken token)
        {
            // ... (implementation - likely replaced by Vector3Converter) ...
            // Consider removing these if the serializer handles them reliably.
            if (token is JObject obj && obj.ContainsKey("x") && obj.ContainsKey("y") && obj.ContainsKey("z"))
            {
                return new Vector3(obj["x"].ToObject<float>(), obj["y"].ToObject<float>(), obj["z"].ToObject<float>());
            }
            if (token is JArray arr && arr.Count >= 3)
            {
                 return new Vector3(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>());
            }
            Debug.LogWarning($"Could not parse JToken '{token}' as Vector3 using fallback. Returning Vector3.zero.");
            return Vector3.zero;

        }

        static Vector2 ParseJTokenToVector2(JToken token)
        {
            // ... (implementation - likely replaced by Vector2Converter) ...
             if (token is JObject obj && obj.ContainsKey("x") && obj.ContainsKey("y"))
            {
                return new Vector2(obj["x"].ToObject<float>(), obj["y"].ToObject<float>());
            }
            if (token is JArray arr && arr.Count >= 2)
            {
                 return new Vector2(arr[0].ToObject<float>(), arr[1].ToObject<float>());
            }
            Debug.LogWarning($"Could not parse JToken '{token}' as Vector2 using fallback. Returning Vector2.zero.");
            return Vector2.zero;
        }

        static Quaternion ParseJTokenToQuaternion(JToken token)
        {
            // ... (implementation - likely replaced by QuaternionConverter) ...
            if (token is JObject obj && obj.ContainsKey("x") && obj.ContainsKey("y") && obj.ContainsKey("z") && obj.ContainsKey("w"))
            {
                return new Quaternion(obj["x"].ToObject<float>(), obj["y"].ToObject<float>(), obj["z"].ToObject<float>(), obj["w"].ToObject<float>());
            }
            if (token is JArray arr && arr.Count >= 4)
            {
                 return new Quaternion(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>(), arr[3].ToObject<float>());
            }
            Debug.LogWarning($"Could not parse JToken '{token}' as Quaternion using fallback. Returning Quaternion.identity.");
            return Quaternion.identity;
        }

        static Color ParseJTokenToColor(JToken token)
        {
             // ... (implementation - likely replaced by ColorConverter) ...
            if (token is JObject obj && obj.ContainsKey("r") && obj.ContainsKey("g") && obj.ContainsKey("b") && obj.ContainsKey("a"))
            {
                return new Color(obj["r"].ToObject<float>(), obj["g"].ToObject<float>(), obj["b"].ToObject<float>(), obj["a"].ToObject<float>());
            }
            if (token is JArray arr && arr.Count >= 4)
            {
                 return new Color(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>(), arr[3].ToObject<float>());
            }
            Debug.LogWarning($"Could not parse JToken '{token}' as Color using fallback. Returning Color.white.");
            return Color.white;
        }

        static Rect ParseJTokenToRect(JToken token)
        {
             // ... (implementation - likely replaced by RectConverter) ...
            if (token is JObject obj && obj.ContainsKey("x") && obj.ContainsKey("y") && obj.ContainsKey("width") && obj.ContainsKey("height"))
            {
                return new Rect(obj["x"].ToObject<float>(), obj["y"].ToObject<float>(), obj["width"].ToObject<float>(), obj["height"].ToObject<float>());
            }
            if (token is JArray arr && arr.Count >= 4)
            {
                 return new Rect(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>(), arr[3].ToObject<float>());
            }
            Debug.LogWarning($"Could not parse JToken '{token}' as Rect using fallback. Returning Rect.zero.");
            return Rect.zero;
        }

        static Bounds ParseJTokenToBounds(JToken token)
        {
             // ... (implementation - likely replaced by BoundsConverter) ...
            if (token is JObject obj && obj.ContainsKey("center") && obj.ContainsKey("size"))
            {
                // Requires Vector3 conversion, which should ideally use the serializer too
                Vector3 center = ParseJTokenToVector3(obj["center"]); // Or use obj["center"].ToObject<Vector3>(inputSerializer)
                Vector3 size = ParseJTokenToVector3(obj["size"]);     // Or use obj["size"].ToObject<Vector3>(inputSerializer)
                return new Bounds(center, size);
            }
            // Array fallback for Bounds is less intuitive, maybe remove?
            // if (token is JArray arr && arr.Count >= 6)
            // {
            //      return new Bounds(new Vector3(arr[0].ToObject<float>(), arr[1].ToObject<float>(), arr[2].ToObject<float>()), new Vector3(arr[3].ToObject<float>(), arr[4].ToObject<float>(), arr[5].ToObject<float>()));
            // }
            Debug.LogWarning($"Could not parse JToken '{token}' as Bounds using fallback. Returning new Bounds(Vector3.zero, Vector3.zero).");
            return new Bounds(Vector3.zero, Vector3.zero);
        }
        // --- End Redundant Parse Helpers ---

        /// <summary>
        /// Finds a specific UnityEngine.Object based on a find instruction JObject.
        /// Primarily used by UnityEngineObjectConverter during deserialization.
        /// </summary>
        /// <param name="instruction">The JObject containing find instructions (find term, method, component).</param>
        /// <param name="targetType">The type of Unity Object to find.</param>
        /// <returns>The found UnityEngine.Object or null if not found.</returns>
        // Made public static so UnityEngineObjectConverter can call it. Moved from ConvertJTokenToType.
        public static UnityEngine.Object FindObjectByInstruction(JObject instruction, Type targetType)
        {
            string findTerm = instruction["find"]?.ToString();
            string method = instruction["method"]?.ToString()?.ToLower();
            string componentName = instruction["component"]?.ToString(); // Specific component to get

            if (string.IsNullOrEmpty(findTerm))
            {
                Debug.LogWarning("Find instruction missing 'find' term.");
                return null;
            }

            // Use a flexible default search method if none provided
            string searchMethodToUse = string.IsNullOrEmpty(method) ? "by_id_or_name_or_path" : method;

            // If the target is an asset (Material, Texture, ScriptableObject etc.) try AssetDatabase first
            if (typeof(Material).IsAssignableFrom(targetType) ||
                typeof(Texture).IsAssignableFrom(targetType) ||
                typeof(ScriptableObject).IsAssignableFrom(targetType) ||
                targetType.FullName.StartsWith("UnityEngine.U2D") || // Sprites etc.
                typeof(AudioClip).IsAssignableFrom(targetType) ||
                typeof(AnimationClip).IsAssignableFrom(targetType) ||
                typeof(Font).IsAssignableFrom(targetType) ||
                typeof(Shader).IsAssignableFrom(targetType) ||
                typeof(ComputeShader).IsAssignableFrom(targetType) ||
                typeof(GameObject).IsAssignableFrom(targetType) && findTerm.StartsWith("Assets/")) // Prefab check
            {
                // Try loading directly by path/GUID first
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(findTerm, targetType);
                if (asset != null) return asset;
                asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(findTerm); // Try generic if type specific failed
                if (asset != null && targetType.IsAssignableFrom(asset.GetType())) return asset;


                // If direct path failed, try finding by name/type using FindAssets
                string searchFilter = $"t:{targetType.Name} {System.IO.Path.GetFileNameWithoutExtension(findTerm)}"; // Search by type and name
                string[] guids = AssetDatabase.FindAssets(searchFilter);

                if (guids.Length == 1)
                {
                    asset = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guids[0]), targetType);
                    if (asset != null) return asset;
                }
                else if (guids.Length > 1)
                {
                    Debug.LogWarning($"[FindObjectByInstruction] Ambiguous asset find: Found {guids.Length} assets matching filter '{searchFilter}'. Provide a full path or unique name.");
                    // Optionally return the first one? Or null? Returning null is safer.
                    return null;
                }
                // If still not found, fall through to scene search (though unlikely for assets)
            }


            // --- Scene Object Search ---
            // Find the GameObject using the internal finder
            GameObject foundGo = SceneObjectLocator.FindObject(
                findTerm,
                searchMethodToUse,
                new SceneObjectLocator.Options
                {
                    IncludeInactive = true,
                    IncludePrefabStage = true
                });

            if (foundGo == null)
            {
                // Don't warn yet, could still be an asset not found above
                // Debug.LogWarning($"Could not find GameObject using instruction: {instruction}");
                return null;
            }

            // Now, get the target object/component from the found GameObject
            if (targetType == typeof(GameObject))
            {
                return foundGo; // We were looking for a GameObject
            }
            else if (typeof(Component).IsAssignableFrom(targetType))
            {
                Type componentToGetType = targetType;
                if (!string.IsNullOrEmpty(componentName))
                {
                    Type specificCompType = FindType(componentName);
                    if (specificCompType != null && typeof(Component).IsAssignableFrom(specificCompType))
                    {
                        componentToGetType = specificCompType;
                    }
                    else
                    {
                        Debug.LogWarning($"Could not find component type '{componentName}' specified in find instruction. Falling back to target type '{targetType.Name}'.");
                    }
                }

                Component foundComp = foundGo.GetComponent(componentToGetType);
                if (foundComp == null)
                {
                    Debug.LogWarning($"Found GameObject '{foundGo.name}' but could not find component of type '{componentToGetType.Name}'.");
                }
                return foundComp;
            }
            else
            {
                Debug.LogWarning($"Find instruction handling not implemented for target type: {targetType.Name}");
                return null;
            }
        }


        /// <summary>
        /// Robust component resolver that avoids Assembly.LoadFrom and works with asmdefs.
        /// Searches already-loaded assemblies, prioritizing runtime script assemblies.
        /// </summary>
        internal static Type FindType(string typeName)
        {
            if (ComponentResolver.TryResolve(typeName, out Type resolvedType, out string error))
            {
                return resolvedType;
            }

            // Log the resolver error if type wasn't found
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogWarning($"[FindType] {error}");
            }

            return null;
        }
    }

    /// <summary>
    /// Robust component resolver that avoids Assembly.LoadFrom and supports assembly definitions.
    /// Prioritizes runtime (Player) assemblies over Editor assemblies.
    /// </summary>
    static class ComponentResolver
    {
        static readonly Dictionary<string, Type> CacheByFqn = new(StringComparer.Ordinal);
        static readonly Dictionary<string, Type> CacheByName = new(StringComparer.Ordinal);

        /// <summary>
        /// Resolve a Component/MonoBehaviour type by short or fully-qualified name.
        /// Prefers runtime (Player) script assemblies; falls back to Editor assemblies.
        /// Never uses Assembly.LoadFrom.
        /// </summary>
        public static bool TryResolve(string nameOrFullName, out Type type, out string error)
        {
            error = string.Empty;
            type = null!;

            // Handle null/empty input
            if (string.IsNullOrWhiteSpace(nameOrFullName))
            {
                error = "Component name cannot be null or empty";
                return false;
            }

            // 1) Exact cache hits
            if (CacheByFqn.TryGetValue(nameOrFullName, out type)) return true;
            if (!nameOrFullName.Contains(".") && CacheByName.TryGetValue(nameOrFullName, out type)) return true;
            type = Type.GetType(nameOrFullName, throwOnError: false);
            if (IsValidComponent(type)) { Cache(type); return true; }

            // 2) Search loaded assemblies (prefer Player assemblies)
            var candidates = FindCandidates(nameOrFullName);
            if (candidates.Count == 1) { type = candidates[0]; Cache(type); return true; }
            if (candidates.Count > 1) { error = Ambiguity(nameOrFullName, candidates); type = null!; return false; }

#if UNITY_EDITOR
            // 3) Last resort: Editor-only TypeCache (fast index)
            var tc = TypeCache.GetTypesDerivedFrom<Component>()
                              .Where(t => NamesMatch(t, nameOrFullName));
            candidates = PreferPlayer(tc).ToList();
            if (candidates.Count == 1) { type = candidates[0]; Cache(type); return true; }
            if (candidates.Count > 1) { error = Ambiguity(nameOrFullName, candidates); type = null!; return false; }
#endif

            error = $"Component type '{nameOrFullName}' not found in loaded runtime assemblies. " +
                    "Use a fully-qualified name (Namespace.TypeName) and ensure the script compiled.";
            type = null!;
            return false;
        }

        static bool NamesMatch(Type t, string q) =>
            t.Name.Equals(q, StringComparison.Ordinal) ||
            (t.FullName?.Equals(q, StringComparison.Ordinal) ?? false);

        static bool IsValidComponent(Type t) =>
            t != null && typeof(Component).IsAssignableFrom(t);

        static void Cache(Type t)
        {
            if (t.FullName != null) CacheByFqn[t.FullName] = t;
            CacheByName[t.Name] = t;
        }

        static List<Type> FindCandidates(string query)
        {
            bool isShort = !query.Contains('.');
            var loaded = AppDomain.CurrentDomain.GetAssemblies();

#if UNITY_EDITOR
            // Names of Player (runtime) script assemblies (asmdefs + Assembly-CSharp)
            var playerAsmNames = new HashSet<string>(
                UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Player).Select(a => a.name),
                StringComparer.Ordinal);

            IEnumerable<Assembly> playerAsms = loaded.Where(a => playerAsmNames.Contains(a.GetName().Name));
            IEnumerable<Assembly> editorAsms = loaded.Except(playerAsms);
#else
            IEnumerable<System.Reflection.Assembly> playerAsms = loaded;
            IEnumerable<System.Reflection.Assembly> editorAsms = Array.Empty<System.Reflection.Assembly>();
#endif
            static IEnumerable<Type> SafeGetTypes(Assembly a)
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { return rtle.Types.Where(t => t != null)!; }
            }

            Func<Type, bool> match = isShort
                ? (t => t.Name.Equals(query, StringComparison.Ordinal))
                : (t => t.FullName!.Equals(query, StringComparison.Ordinal));

            var fromPlayer = playerAsms.SelectMany(SafeGetTypes)
                                       .Where(IsValidComponent)
                                       .Where(match);
            var fromEditor = editorAsms.SelectMany(SafeGetTypes)
                                       .Where(IsValidComponent)
                                       .Where(match);

            var list = new List<Type>(fromPlayer);
            if (list.Count == 0) list.AddRange(fromEditor);
            return list;
        }

#if UNITY_EDITOR
        static IEnumerable<Type> PreferPlayer(IEnumerable<Type> seq)
        {
            var player = new HashSet<string>(
                UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Player).Select(a => a.name),
                StringComparer.Ordinal);

            return seq.OrderBy(t => player.Contains(t.Assembly.GetName().Name) ? 0 : 1);
        }
#endif

        static string Ambiguity(string query, IEnumerable<Type> cands)
        {
            var lines = cands.Select(t => $"{t.FullName} (assembly {t.Assembly.GetName().Name})");
            return $"Multiple component types matched '{query}':\n - " + string.Join("\n - ", lines) +
                   "\nProvide a fully qualified type name to disambiguate.";
        }

        /// <summary>
        /// Gets all accessible property and field names from a component type.
        /// </summary>
        public static List<string> GetAllComponentProperties(Type componentType)
        {
            if (componentType == null) return new List<string>();

            var properties = componentType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                         .Where(p => p.CanRead && p.CanWrite)
                                         .Select(p => p.Name);

            var fields = componentType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                                     .Where(f => !f.IsInitOnly && !f.IsLiteral)
                                     .Select(f => f.Name);

            // Also include SerializeField private fields (common in Unity)
            var serializeFields = componentType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                                              .Where(f => f.GetCustomAttribute<SerializeField>() != null)
                                              .Select(f => f.Name);

            return properties.Concat(fields).Concat(serializeFields).Distinct().OrderBy(x => x).ToList();
        }

        /// <summary>
        /// Uses AI to suggest the most likely property matches for a user's input.
        /// </summary>
        public static List<string> GetAIPropertySuggestions(string userInput, List<string> availableProperties)
        {
            if (string.IsNullOrWhiteSpace(userInput) || !availableProperties.Any())
                return new List<string>();

            // Simple caching to avoid repeated AI calls for the same input
            var cacheKey = $"{userInput.ToLowerInvariant()}:{string.Join(",", availableProperties)}";
            if (PropertySuggestionCache.TryGetValue(cacheKey, out var cached))
                return cached;

            try
            {
                var prompt = $"A Unity developer is trying to set a component property but used an incorrect name.\n\n" +
                             $"User requested: \"{userInput}\"\n" +
                             $"Available properties: [{string.Join(", ", availableProperties)}]\n\n" +
                             $"Find 1-3 most likely matches considering:\n" +
                             $"- Unity Inspector display names vs actual field names (e.g., \"Max Reach Distance\" → \"maxReachDistance\")\n" +
                             $"- camelCase vs PascalCase vs spaces\n" +
                             $"- Similar meaning/semantics\n" +
                             $"- Common Unity naming patterns\n\n" +
                             $"Return ONLY the matching property names, comma-separated, no quotes or explanation.\n" +
                             $"If confidence is low (<70%), return empty string.\n\n" +
                             $"Examples:\n" +
                             $"- \"Max Reach Distance\" → \"maxReachDistance\"\n" +
                             $"- \"Health Points\" → \"healthPoints, hp\"\n" +
                             $"- \"Move Speed\" → \"moveSpeed, movementSpeed\"";

                // For now, we'll use a simple rule-based approach that mimics AI behavior
                // This can be replaced with actual AI calls later
                var suggestions = GetRuleBasedSuggestions(userInput, availableProperties);

                PropertySuggestionCache[cacheKey] = suggestions;
                return suggestions;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AI Property Matching] Error getting suggestions for '{userInput}': {ex.Message}");
                return new List<string>();
            }
        }

        static readonly Dictionary<string, List<string>> PropertySuggestionCache = new();

        /// <summary>
        /// Rule-based suggestions that mimic AI behavior for property matching.
        /// This provides immediate value while we could add real AI integration later.
        /// </summary>
        static List<string> GetRuleBasedSuggestions(string userInput, List<string> availableProperties)
        {
            var suggestions = new List<string>();
            var cleanedInput = userInput.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");

            foreach (var property in availableProperties)
            {
                var cleanedProperty = property.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");

                // Exact match after cleaning
                if (cleanedProperty == cleanedInput)
                {
                    suggestions.Add(property);
                    continue;
                }

                // Check if property contains all words from input
                var inputWords = userInput.ToLowerInvariant().Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                if (inputWords.All(word => cleanedProperty.Contains(word.ToLowerInvariant())))
                {
                    suggestions.Add(property);
                    continue;
                }

                // Levenshtein distance for close matches
                if (LevenshteinDistance(cleanedInput, cleanedProperty) <= Math.Max(2, cleanedInput.Length / 4))
                {
                    suggestions.Add(property);
                }
            }

            // Prioritize exact matches, then by similarity
            return suggestions.OrderBy(s => LevenshteinDistance(cleanedInput, s.ToLowerInvariant().Replace(" ", "")))
                             .Take(3)
                             .ToList();
        }

        /// <summary>
        /// Calculates Levenshtein distance between two strings for similarity matching.
        /// </summary>
        static int LevenshteinDistance(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
            if (string.IsNullOrEmpty(s2)) return s1.Length;

            var matrix = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++) matrix[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++) matrix[0, j] = j;

            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    int cost = (s2[j - 1] == s1[i - 1]) ? 0 : 1;
                    matrix[i, j] = Math.Min(Math.Min(
                        matrix[i - 1, j] + 1,      // deletion
                        matrix[i, j - 1] + 1),     // insertion
                        matrix[i - 1, j - 1] + cost); // substitution
                }
            }

            return matrix[s1.Length, s2.Length];
        }

        /// <summary>
        /// Get the center of the gameobject based on collider or meshrenderer bounds
        /// </summary>
        /// <param name="targetGo">The gameobject to target</param>
        /// <returns>Vector3 world position of the center</returns>
        public static Vector3 GetObjectWorldCenter(GameObject targetGo)
        {
            if (targetGo.TryGetComponent<Collider>(out var collider))
            {
                return collider.bounds.center;
            }
            if (targetGo.TryGetComponent<MeshRenderer>(out var meshRenderer))
            {
                return meshRenderer.bounds.center;
            }

            return targetGo.transform.position;
        }
    }
}
