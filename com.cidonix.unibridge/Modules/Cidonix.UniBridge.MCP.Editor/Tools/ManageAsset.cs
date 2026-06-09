using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry; // For Response class
using Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters;

#if UNITY_6000_0_OR_NEWER
using PhysicsMaterialType = UnityEngine.PhysicsMaterial;
using PhysicsMaterialCombine = UnityEngine.PhysicsMaterialCombine;
#else
using PhysicsMaterialType = UnityEngine.PhysicMaterial;
using PhysicsMaterialCombine = UnityEngine.PhysicMaterialCombine;
#endif

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Handles asset management operations within the Unity project.
    /// </summary>
    public static class ManageAsset
    {
        /// <summary>
        /// Human-readable description of the UniBridge_ManageAsset tool functionality and usage.
        /// </summary>
        public const string Title = "Manage assets";

        public const string Description = @"Manage Unity project assets through AssetDatabase-safe operations.

Use this for folders, materials, textures, physics materials, render textures, terrain layers, avatar masks, shader variant collections, prefabs/assets metadata, search, reimport, move/rename, duplicate, delete, and asset info. Use UniBridge_ImportExternalModel for external FBX model import.

Args:
    Action: Import, Create, CreateOrUpdate, Modify, Delete, Duplicate, Move, Rename, Search, GetInfo, CreateFolder, or GetComponents.
    Path: Assets/... path for the asset or search scope.
    AssetType: Required for Create/CreateOrUpdate, for example Material, PhysicsMaterial, PhysicsMaterial2D, RenderTexture, TerrainLayer, AvatarMask, ShaderVariantCollection, ScriptableObject, or Folder.
    Properties: JSON object for Create or Modify, such as material color/shader or texture settings.
    Destination: Target path for Duplicate, Move, or Rename.
    SearchPattern, FilterType, FilterDate, PageSize, PageNumber: Search and pagination controls.
    GeneratePreview: Include preview data when supported by GetInfo.

Returns:
    success, message, and action-specific asset data such as paths, GUIDs, metadata, search results, or component summaries.";
        // --- Main Handler ---

        /// <summary>
        /// Demonstrates typed parameter handling with automatic schema generation.
        /// The schema is auto-generated from the ManageAssetParams record type.
        /// </summary>
        /// <param name="params">Parameters containing the action to perform and relevant asset information.</param>
        /// <returns>A response object indicating success or failure with relevant asset data.</returns>
        [McpTool("UniBridge_ManageAsset", Description, Title, Groups = new[] { "core", "assets" }, EnabledByDefault = true)]
        public static object HandleCommand(ManageAssetParams @params)
        {
            if (@params == null)
            {
                return Response.Error("Parameters cannot be null.");
            }

            // Common parameters - now type-safe!
            string path = @params.Path;

            try
            {
                switch (@params.Action)
                {
                    case AssetAction.Import:
                        // Note: Unity typically auto-imports. This might re-import or configure import settings.
                        return ReimportAsset(path, @params.Properties);
                    case AssetAction.Create:
                        return CreateAssetTyped(@params, allowUpdate: false);
                    case AssetAction.CreateOrUpdate:
                        return CreateAssetTyped(@params, allowUpdate: true);
                    case AssetAction.Modify:
                        return ModifyAsset(path, @params.Properties);
                    case AssetAction.Delete:
                        return DeleteAsset(path);
                    case AssetAction.Duplicate:
                        return DuplicateAsset(path, @params.Destination);
                    case AssetAction.Move: // Often same as rename if within Assets/
                    case AssetAction.Rename:
                        return MoveOrRenameAsset(path, @params.Destination);
                    case AssetAction.Search:
                        return SearchAssetsTyped(@params);
                    case AssetAction.GetInfo:
                        return GetAssetInfo(path, @params.GeneratePreview);
                    case AssetAction.CreateFolder: // Added specific action for clarity
                        return CreateFolder(path);
                    case AssetAction.GetComponents:
                        return GetComponentsFromAsset(path);

                    default:
                        return Response.Error($"Unknown action: '{@params.Action}'. Valid actions are: {string.Join(", ", Enum.GetNames(typeof(AssetAction)))}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ManageAsset] Action '{@params.Action}' failed for path '{path}': {e}");
                return Response.Error(
                    $"Internal error processing action '{@params.Action}' on '{path}': {e.Message}"
                );
            }
        }

        // --- Action Implementations ---

        static object ReimportAsset(string path, JObject properties)
        {
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for reimport.");
            string fullPath = SanitizeAssetPath(path);
            if (!AssetExists(fullPath))
                return Response.Error($"Asset not found at path: {fullPath}");

            try
            {
                EnsureEditable(fullPath);
                // TODO: Apply importer properties before reimporting?
                // This is complex as it requires getting the AssetImporter, casting it,
                // applying properties via reflection or specific methods, saving, then reimporting.
                if (properties != null && properties.HasValues)
                {
                    Debug.LogWarning(
                        "[ManageAsset.Reimport] Modifying importer properties before reimport is not fully implemented yet."
                    );
                    // AssetImporter importer = AssetImporter.GetAtPath(fullPath);
                    // if (importer != null) { /* Apply properties */ AssetDatabase.WriteImportSettingsIfDirty(fullPath); }
                }

                AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceUpdate);
                // AssetDatabase.Refresh(); // Usually ImportAsset handles refresh
                return Response.Success($"Asset '{fullPath}' reimported.", GetAssetData(fullPath));
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to reimport asset '{fullPath}': {e.Message}");
            }
        }

        static object CreateAssetTyped(ManageAssetParams @params, bool allowUpdate)
        {
            string path = @params.Path;
            string assetType = @params.AssetType;
            JObject properties = @params.Properties;

            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for create.");
            if (string.IsNullOrEmpty(assetType))
                return Response.Error("'assetType' is required for create.");

            string fullPath = SanitizeAssetPath(path);
            string directory = Path.GetDirectoryName(fullPath);

            // Ensure directory exists
            if (!Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), directory)))
            {
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), directory));
                AssetDatabase.Refresh(); // Make sure Unity knows about the new folder
            }

            try
            {
                UnityEngine.Object newAsset = null;
                string lowerAssetType = assetType.ToLowerInvariant();
                if (lowerAssetType == "folder")
                {
                    return CreateFolder(path);
                }

                UnityEngine.Object existingAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(fullPath);
                if (existingAsset != null)
                {
                    if (!allowUpdate)
                        return Response.Error($"Asset already exists at path: {fullPath}");

                    if (!IsAllowedAssetType(existingAsset, lowerAssetType))
                    {
                        return Response.Error(
                            $"Cannot update '{fullPath}' as '{assetType}'. Existing asset type is '{existingAsset.GetType().FullName}'."
                        );
                    }

                    EnsureEditable(fullPath);
                    var modified = ApplyGenericAssetProperties(existingAsset, properties);
                    EditorUtility.SetDirty(existingAsset);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceUpdate);
                    return Response.Success(
                        modified
                            ? $"Asset '{fullPath}' updated successfully."
                            : $"Asset '{fullPath}' already existed; no applicable properties changed.",
                        GetAssetData(fullPath)
                    );
                }

                // Handle common asset types
                if (lowerAssetType == "material")
                {
                    // Prefer provided shader; fall back to common pipelines
                    var requested = properties?["shader"]?.ToString();
                    Shader shader =
                        (!string.IsNullOrEmpty(requested) ? Shader.Find(requested) : null)
                        ?? Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("HDRP/Lit")
                        ?? Shader.Find("Standard")
                        ?? Shader.Find("Unlit/Color");
                    if (shader == null)
                        return Response.Error($"Could not find a suitable shader (requested: '{requested ?? "none"}').");

                    var mat = new Material(shader);
                    if (properties != null)
                        ApplyMaterialProperties(mat, properties);
                    AssetDatabase.CreateAsset(mat, fullPath);
                    newAsset = mat;
                }
                else if (IsAssetType(lowerAssetType, "physicsmaterial", "physicmaterial", "physics_material", "unityengine.physicsmaterial", "unityengine.physicmaterial"))
                {
                    PhysicsMaterialType pmat = new PhysicsMaterialType();
                    if (properties != null)
                        ApplyPhysicsMaterialProperties(pmat, properties);
                    AssetDatabase.CreateAsset(pmat, fullPath);
                    newAsset = pmat;
                }
                else if (IsAssetType(lowerAssetType, "physicsmaterial2d", "physics_material_2d", "unityengine.physicsmaterial2d"))
                {
                    var pmat2d = new PhysicsMaterial2D();
                    if (properties != null)
                        ApplyPhysicsMaterial2DProperties(pmat2d, properties);
                    AssetDatabase.CreateAsset(pmat2d, fullPath);
                    newAsset = pmat2d;
                }
                else if (IsAssetType(lowerAssetType, "rendertexture", "render_texture", "unityengine.rendertexture"))
                {
                    var rt = CreateRenderTexture(properties);
                    AssetDatabase.CreateAsset(rt, fullPath);
                    newAsset = rt;
                }
                else if (IsAssetType(lowerAssetType, "terrainlayer", "terrain_layer", "unityengine.terrainlayer"))
                {
                    var terrainLayer = new TerrainLayer();
                    if (properties != null)
                        ApplyTerrainLayerProperties(terrainLayer, properties);
                    AssetDatabase.CreateAsset(terrainLayer, fullPath);
                    newAsset = terrainLayer;
                }
                else if (IsAssetType(lowerAssetType, "avatarmask", "avatar_mask", "unityengine.avatarmask"))
                {
                    var avatarMask = new AvatarMask();
                    if (properties != null)
                        ApplyAvatarMaskProperties(avatarMask, properties);
                    AssetDatabase.CreateAsset(avatarMask, fullPath);
                    newAsset = avatarMask;
                }
                else if (IsAssetType(lowerAssetType, "shadervariantcollection", "shader_variant_collection", "unityengine.shadervariantcollection"))
                {
                    var collection = new ShaderVariantCollection();
                    if (properties != null)
                        ApplyShaderVariantCollectionProperties(collection, properties);
                    AssetDatabase.CreateAsset(collection, fullPath);
                    newAsset = collection;
                }
                else if (lowerAssetType == "scriptableobject")
                {
                    string scriptClassName = properties?["scriptClass"]?.ToString();
                    if (string.IsNullOrEmpty(scriptClassName))
                        return Response.Error(
                            "'scriptClass' property required when creating ScriptableObject asset."
                        );

                    Type scriptType = ComponentResolver.TryResolve(scriptClassName, out var resolvedType, out var error) ? resolvedType : null;
                    if (
                        scriptType == null
                        || !typeof(ScriptableObject).IsAssignableFrom(scriptType)
                    )
                    {
                        var reason = scriptType == null
                            ? (string.IsNullOrEmpty(error) ? "Type not found." : error)
                            : "Type found but does not inherit from ScriptableObject.";
                        return Response.Error($"Script class '{scriptClassName}' invalid: {reason}");
                    }

                    ScriptableObject so = ScriptableObject.CreateInstance(scriptType);
                    // TODO: Apply properties from JObject to the ScriptableObject instance?
                    AssetDatabase.CreateAsset(so, fullPath);
                    newAsset = so;
                }
                else if (lowerAssetType == "prefab")
                {
                    // Creating prefabs usually involves saving an existing GameObject hierarchy.
                    // A common pattern is to create an empty GameObject, configure it, and then save it.
                    return Response.Error(
                        "Creating prefabs programmatically usually requires a source GameObject. Use UniBridge_ManageGameObject to create/configure, then save as prefab via a separate mechanism or future enhancement."
                    );
                    // Example (conceptual):
                    // GameObject source = GameObject.Find(properties["sourceGameObject"].ToString());
                    // if(source != null) PrefabUtility.SaveAsPrefabAsset(source, fullPath);
                }
                // TODO: Add more asset types (Animation Controller, Scene, etc.)
                else
                {
                    // Generic creation attempt (might fail or create empty files)
                    // For some types, just creating the file might be enough if Unity imports it.
                    // File.Create(Path.Combine(Directory.GetCurrentDirectory(), fullPath)).Close();
                    // AssetDatabase.ImportAsset(fullPath); // Let Unity try to import it
                    // newAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(fullPath);
                    return Response.Error(
                        $"Creation for asset type '{assetType}' is not explicitly supported yet. Supported: Folder, Material, PhysicsMaterial, PhysicsMaterial2D, RenderTexture, TerrainLayer, AvatarMask, ShaderVariantCollection, ScriptableObject."
                    );
                }

                if (
                    newAsset == null
                    && !Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), fullPath))
                ) // Check if it wasn't a folder and asset wasn't created
                {
                    return Response.Error(
                        $"Failed to create asset '{assetType}' at '{fullPath}'. See logs for details."
                    );
                }

                AssetDatabase.SaveAssets();
                // AssetDatabase.Refresh(); // CreateAsset often handles refresh
                return Response.Success(
                    $"Asset '{fullPath}' created successfully.",
                    GetAssetData(fullPath)
                );
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to create asset at '{fullPath}': {e.Message}");
            }
        }

        static object CreateFolder(string path)
        {
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for create_folder.");
            string fullPath = SanitizeAssetPath(path);
            string parentDir = Path.GetDirectoryName(fullPath);
            string folderName = Path.GetFileName(fullPath);

            if (AssetExists(fullPath))
            {
                // Check if it's actually a folder already
                if (AssetDatabase.IsValidFolder(fullPath))
                {
                    return Response.Success(
                        $"Folder already exists at path: {fullPath}",
                        GetAssetData(fullPath)
                    );
                }
                else
                {
                    return Response.Error(
                        $"An asset (not a folder) already exists at path: {fullPath}"
                    );
                }
            }

            try
            {
                // Ensure parent exists
                if (!string.IsNullOrEmpty(parentDir) && !AssetDatabase.IsValidFolder(parentDir))
                {
                    // Recursively create parent folders if needed (AssetDatabase handles this internally)
                    // Or we can do it manually: Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), parentDir)); AssetDatabase.Refresh();
                }

                string guid = AssetDatabase.CreateFolder(parentDir, folderName);
                if (string.IsNullOrEmpty(guid))
                {
                    return Response.Error(
                        $"Failed to create folder '{fullPath}'. Check logs and permissions."
                    );
                }

                // AssetDatabase.Refresh(); // CreateFolder usually handles refresh
                return Response.Success(
                    $"Folder '{fullPath}' created successfully.",
                    GetAssetData(fullPath)
                );
            }
            catch (Exception e)
            {
                return Response.Error($"Failed to create folder '{fullPath}': {e.Message}");
            }
        }

        static object ModifyAsset(string path, JObject properties)
        {
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for modify.");
            if (properties == null || !properties.HasValues)
                return Response.Error("'properties' are required for modify.");

            string fullPath = SanitizeAssetPath(path);
            if (!AssetExists(fullPath))
                return Response.Error($"Asset not found at path: {fullPath}");

            try
            {
                EnsureEditable(fullPath);
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                    fullPath
                );
                if (asset == null)
                    return Response.Error($"Failed to load asset at path: {fullPath}");

                bool modified = false; // Flag to track if any changes were made

                // --- NEW: Handle GameObject / Prefab Component Modification ---
                if (asset is GameObject gameObject)
                {
                    // Iterate through the properties JSON: keys are component names, values are properties objects for that component
                    foreach (var prop in properties.Properties())
                    {
                        string componentName = prop.Name; // e.g., "Collectible"
                        // Check if the value associated with the component name is actually an object containing properties
                        if (
                            prop.Value is JObject componentProperties
                            && componentProperties.HasValues
                        ) // e.g., {"bobSpeed": 2.0}
                        {
                            // Resolve component type via ComponentResolver, then fetch by Type
                            Component targetComponent = null;
                            bool resolved = ComponentResolver.TryResolve(componentName, out var compType, out var compError);
                            if (resolved)
                            {
                                targetComponent = gameObject.GetComponent(compType);
                            }

                            // Only warn about resolution failure if component also not found
                            if (targetComponent == null && !resolved)
                            {
                                Debug.LogWarning(
                                    $"[ManageAsset.ModifyAsset] Failed to resolve component '{componentName}' on '{gameObject.name}': {compError}"
                                );
                            }

                            if (targetComponent != null)
                            {
                                // Apply the nested properties (e.g., bobSpeed) to the found component instance
                                // Use |= to ensure 'modified' becomes true if any component is successfully modified
                                modified |= ApplyObjectProperties(
                                    targetComponent,
                                    componentProperties
                                );
                            }
                            else
                            {
                                // Log a warning if a specified component couldn't be found
                                Debug.LogWarning(
                                    $"[ManageAsset.ModifyAsset] Component '{componentName}' not found on GameObject '{gameObject.name}' in asset '{fullPath}'. Skipping modification for this component."
                                );
                            }
                        }
                        else
                        {
                            // Log a warning if the structure isn't {"ComponentName": {"prop": value}}
                            // We could potentially try to apply this property directly to the GameObject here if needed,
                            // but the primary goal is component modification.
                            Debug.LogWarning(
                                $"[ManageAsset.ModifyAsset] Property '{prop.Name}' for GameObject modification should have a JSON object value containing component properties. Value was: {prop.Value.Type}. Skipping."
                            );
                        }
                    }
                    // Note: 'modified' is now true if ANY component property was successfully changed.
                }
                // --- End NEW ---

                // --- Existing logic for other asset types (now as else-if) ---
                // Example: Modifying a Material
                else if (asset is Material material)
                {
                    // Apply properties directly to the material. If this modifies, it sets modified=true.
                    // Use |= in case the asset was already marked modified by previous logic (though unlikely here)
                    modified |= ApplyMaterialProperties(material, properties);
                }
                // Example: Modifying a ScriptableObject
                else if (asset is ScriptableObject so)
                {
                    // Apply properties directly to the ScriptableObject.
                    modified |= ApplyObjectProperties(so, properties); // General helper
                }
                else if (asset is PhysicsMaterialType || asset is PhysicsMaterial2D || asset is RenderTexture || asset is TerrainLayer || asset is AvatarMask || asset is ShaderVariantCollection)
                {
                    modified |= ApplyGenericAssetProperties(asset, properties);
                }
                // Example: Modifying TextureImporter settings
                else if (asset is Texture)
                {
                    AssetImporter importer = AssetImporter.GetAtPath(fullPath);
                    if (importer is TextureImporter textureImporter)
                    {
                        bool importerModified = ApplyObjectProperties(textureImporter, properties);
                        if (importerModified)
                        {
                            // Importer settings need saving and reimporting
                            AssetDatabase.WriteImportSettingsIfDirty(fullPath);
                            AssetDatabase.ImportAsset(fullPath, ImportAssetOptions.ForceUpdate); // Reimport to apply changes
                            modified = true; // Mark overall operation as modified
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"Could not get TextureImporter for {fullPath}.");
                    }
                }
                // TODO: Add modification logic for other common asset types (Models, AudioClips importers, etc.)
                else // Fallback for other asset types OR direct properties on non-GameObject assets
                {
                    // This block handles non-GameObject/Material/ScriptableObject/Texture assets.
                    // Attempts to apply properties directly to the asset itself.
                    Debug.LogWarning(
                        $"[ManageAsset.ModifyAsset] Asset type '{asset.GetType().Name}' at '{fullPath}' is not explicitly handled for component modification. Attempting generic property setting on the asset itself."
                    );
                    modified |= ApplyObjectProperties(asset, properties);
                }
                // --- End Existing Logic ---

                // Check if any modification happened (either component or direct asset modification)
                if (modified)
                {
                    // Mark the asset as dirty (important for prefabs/SOs) so Unity knows to save it.
                    EditorUtility.SetDirty(asset);
                    // Save all modified assets to disk.
                    AssetDatabase.SaveAssets();
                    // Refresh might be needed in some edge cases, but SaveAssets usually covers it.
                    // AssetDatabase.Refresh();
                    return Response.Success(
                        $"Asset '{fullPath}' modified successfully.",
                        GetAssetData(fullPath)
                    );
                }
                else
                {
                    // If no changes were made (e.g., component not found, property names incorrect, value unchanged), return a success message indicating nothing changed.
                    return Response.Success(
                        $"No applicable or modifiable properties found for asset '{fullPath}'. Check component names, property names, and values.",
                        GetAssetData(fullPath)
                    );
                    // Previous message: return Response.Success($"No applicable properties found to modify for asset '{fullPath}'.", GetAssetData(fullPath));
                }
            }
            catch (Exception e)
            {
                // Log the detailed error internally
                Debug.LogError($"[ManageAsset] Action 'modify' failed for path '{path}': {e}");
                // Return a user-friendly error message
                return Response.Error($"Failed to modify asset '{fullPath}': {e.Message}");
            }
        }

        static object DeleteAsset(string path)
        {
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for delete.");
            string fullPath = SanitizeAssetPath(path);
            if (!AssetExists(fullPath))
                return Response.Error($"Asset not found at path: {fullPath}");

            try
            {
                EnsureEditable(fullPath);
                bool success = AssetDatabase.DeleteAsset(fullPath);
                if (success)
                {
                    // AssetDatabase.Refresh(); // DeleteAsset usually handles refresh
                    return Response.Success($"Asset '{fullPath}' deleted successfully.");
                }
                else
                {
                    // This might happen if the file couldn't be deleted (e.g., locked)
                    return Response.Error(
                        $"Failed to delete asset '{fullPath}'. Check logs or if the file is locked."
                    );
                }
            }
            catch (Exception e)
            {
                return Response.Error($"Error deleting asset '{fullPath}': {e.Message}");
            }
        }

        static object DuplicateAsset(string path, string destinationPath)
        {
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for duplicate.");

            string sourcePath = SanitizeAssetPath(path);
            if (!AssetExists(sourcePath))
                return Response.Error($"Source asset not found at path: {sourcePath}");

            string destPath;
            if (string.IsNullOrEmpty(destinationPath))
            {
                // Generate a unique path if destination is not provided
                destPath = AssetDatabase.GenerateUniqueAssetPath(sourcePath);
            }
            else
            {
                destPath = SanitizeAssetPath(destinationPath);
                if (AssetExists(destPath))
                    return Response.Error($"Asset already exists at destination path: {destPath}");
                // Ensure destination directory exists
                EnsureDirectoryExists(Path.GetDirectoryName(destPath));
            }

            try
            {
                bool success = AssetDatabase.CopyAsset(sourcePath, destPath);
                if (success)
                {
                    // AssetDatabase.Refresh();
                    return Response.Success(
                        $"Asset '{sourcePath}' duplicated to '{destPath}'.",
                        GetAssetData(destPath)
                    );
                }
                else
                {
                    return Response.Error(
                        $"Failed to duplicate asset from '{sourcePath}' to '{destPath}'."
                    );
                }
            }
            catch (Exception e)
            {
                return Response.Error($"Error duplicating asset '{sourcePath}': {e.Message}");
            }
        }

        static object MoveOrRenameAsset(string path, string destinationPath)
        {
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for move/rename.");
            if (string.IsNullOrEmpty(destinationPath))
                return Response.Error("'destination' path is required for move/rename.");

            string sourcePath = SanitizeAssetPath(path);
            string destPath = SanitizeAssetPath(destinationPath);

            if (!AssetExists(sourcePath))
                return Response.Error($"Source asset not found at path: {sourcePath}");
            if (AssetExists(destPath))
                return Response.Error(
                    $"An asset already exists at the destination path: {destPath}"
                );

            // Ensure destination directory exists
            EnsureDirectoryExists(Path.GetDirectoryName(destPath));

            try
            {
                EnsureEditable(sourcePath);
                // Validate will return an error string if failed, null if successful
                string error = AssetDatabase.ValidateMoveAsset(sourcePath, destPath);
                if (!string.IsNullOrEmpty(error))
                {
                    return Response.Error(
                        $"Failed to move/rename asset from '{sourcePath}' to '{destPath}': {error}"
                    );
                }

                string moveError = AssetDatabase.MoveAsset(sourcePath, destPath);
                if (string.IsNullOrEmpty(moveError))
                {
                    // AssetDatabase.Refresh(); // MoveAsset usually handles refresh
                    return Response.Success(
                        $"Asset moved/renamed from '{sourcePath}' to '{destPath}'.",
                        GetAssetData(destPath)
                    );
                }
                else
                {
                    return Response.Error(
                        $"Failed to move/rename asset from '{sourcePath}' to '{destPath}': {moveError}"
                    );
                }
            }
            catch (Exception e)
            {
                return Response.Error($"Error moving/renaming asset '{sourcePath}': {e.Message}");
            }
        }

        static object SearchAssetsTyped(ManageAssetParams @params)
        {
            string searchPattern = @params.SearchPattern;
            string filterType = @params.FilterType;
            string pathScope = @params.Path; // Use path as folder scope
            string filterDateAfterStr = @params.FilterDate;
            int pageSize = @params.PageSize; // Default set in record
            int pageNumber = @params.PageNumber; // Default set in record
            bool generatePreview = @params.GeneratePreview;

            List<string> searchFilters = new List<string>();
            if (!string.IsNullOrEmpty(searchPattern))
                searchFilters.Add(searchPattern);
            if (!string.IsNullOrEmpty(filterType))
                searchFilters.Add($"t:{filterType}");

            string[] folderScope = null;
            if (!string.IsNullOrEmpty(pathScope))
            {
                folderScope = new string[] { SanitizeAssetPath(pathScope) };
                if (!AssetDatabase.IsValidFolder(folderScope[0]))
                {
                    // Maybe the user provided a file path instead of a folder?
                    // We could search in the containing folder, or return an error.
                    Debug.LogWarning(
                        $"Search path '{folderScope[0]}' is not a valid folder. Searching entire project."
                    );
                    folderScope = null; // Search everywhere if path isn't a folder
                }
            }

            DateTime? filterDateAfter = null;
            if (!string.IsNullOrEmpty(filterDateAfterStr))
            {
                if (
                    DateTime.TryParse(
                        filterDateAfterStr,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime parsedDate
                    )
                )
                {
                    filterDateAfter = parsedDate;
                }
                else
                {
                    Debug.LogWarning(
                        $"Could not parse filterDateAfter: '{filterDateAfterStr}'. Expected ISO 8601 format."
                    );
                }
            }

            try
            {
                string[] guids = AssetDatabase.FindAssets(
                    string.Join(" ", searchFilters),
                    folderScope
                );
                List<object> results = new List<object>();
                int totalFound = 0;

                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(assetPath))
                        continue;

                    // Apply date filter if present
                    if (filterDateAfter.HasValue)
                    {
                        DateTime lastWriteTime = File.GetLastWriteTimeUtc(
                            Path.Combine(Directory.GetCurrentDirectory(), assetPath)
                        );
                        if (lastWriteTime <= filterDateAfter.Value)
                        {
                            continue; // Skip assets older than or equal to the filter date
                        }
                    }

                    totalFound++; // Count matching assets before pagination
                    results.Add(GetAssetData(assetPath, generatePreview));
                }

                // Apply pagination
                int startIndex = (pageNumber - 1) * pageSize;
                var pagedResults = results.Skip(startIndex).Take(pageSize).ToList();

                return Response.Success(
                    $"Found {totalFound} asset(s). Returning page {pageNumber} ({pagedResults.Count} assets).",
                    new
                    {
                        totalAssets = totalFound,
                        pageSize = pageSize,
                        pageNumber = pageNumber,
                        assets = pagedResults,
                    }
                );
            }
            catch (Exception e)
            {
                return Response.Error($"Error searching assets: {e.Message}");
            }
        }

        static object GetAssetInfo(string path, bool generatePreview)
        {
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for get_info.");
            string fullPath = SanitizeAssetPath(path);
            if (!AssetExists(fullPath))
                return Response.Error($"Asset not found at path: {fullPath}");

            try
            {
                return Response.Success(
                    "Asset info retrieved.",
                    GetAssetData(fullPath, generatePreview)
                );
            }
            catch (Exception e)
            {
                return Response.Error($"Error getting info for asset '{fullPath}': {e.Message}");
            }
        }

        /// <summary>
        /// Retrieves components attached to a GameObject asset (like a Prefab).
        /// </summary>
        /// <param name="path">The asset path of the GameObject or Prefab.</param>
        /// <returns>A response object containing a list of component type names or an error.</returns>
        static object GetComponentsFromAsset(string path)
        {
            // 1. Validate input path
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for get_components.");

            // 2. Sanitize and check existence
            string fullPath = SanitizeAssetPath(path);
            if (!AssetExists(fullPath))
                return Response.Error($"Asset not found at path: {fullPath}");

            try
            {
                // 3. Load the asset
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                    fullPath
                );
                if (asset == null)
                    return Response.Error($"Failed to load asset at path: {fullPath}");

                // 4. Check if it's a GameObject (Prefabs load as GameObjects)
                GameObject gameObject = asset as GameObject;
                if (gameObject == null)
                {
                    // Also check if it's *directly* a Component type (less common for primary assets)
                    Component componentAsset = asset as Component;
                    if (componentAsset != null)
                    {
                        // If the asset itself *is* a component, maybe return just its info?
                        // This is an edge case. Let's stick to GameObjects for now.
                        return Response.Error(
                            $"Asset at '{fullPath}' is a Component ({asset.GetType().FullName}), not a GameObject. Components are typically retrieved *from* a GameObject."
                        );
                    }
                    return Response.Error(
                        $"Asset at '{fullPath}' is not a GameObject (Type: {asset.GetType().FullName}). Cannot get components from this asset type."
                    );
                }

                // 5. Get components
                Component[] components = gameObject.GetComponents<Component>();

                // 6. Format component data
                List<object> componentList = components
                    .Select(comp => new
                    {
                        typeName = comp.GetType().FullName,
                        instanceID = UnityApiAdapter.GetObjectId(comp),
                        // TODO: Add more component-specific details here if needed in the future?
                        //       Requires reflection or specific handling per component type.
                    })
                    .ToList<object>(); // Explicit cast for clarity if needed

                // 7. Return success response
                return Response.Success(
                    $"Found {componentList.Count} component(s) on asset '{fullPath}'.",
                    componentList
                );
            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"[ManageAsset.GetComponentsFromAsset] Error getting components for '{fullPath}': {e}"
                );
                return Response.Error(
                    $"Error getting components for asset '{fullPath}': {e.Message}"
                );
            }
        }

        // --- Internal Helpers ---

        /// <summary>
        /// Ensures the asset path starts with "Assets/".
        /// </summary>
        static string SanitizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            path = path.Replace('\\', '/'); // Normalize separators
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return "Assets/" + path.TrimStart('/');
            }
            return path;
        }

        /// <summary>
        /// Checks if an asset exists at the specified path.
        /// </summary>
        /// <param name="assetPath">The asset path to check.</param>
        /// <returns>True if the asset exists; otherwise, false.</returns>
        public static bool AssetPathExists(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;

            // Works for both "Assets/..." and "Packages/..."
            if (AssetDatabase.IsValidFolder(assetPath)) return true;              // folder
            return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)); // file/asset
        }

        static void EnsureEditable(string assetPath)
        {
            VersionControlUtility.EnsureAssetEditable(assetPath, checkout: true, throwOnBlocked: true);
        }

        /// <summary>
        /// Checks if an asset exists at the given path (file or folder).
        /// </summary>
        static bool AssetExists(string sanitizedPath)
        {
            // AssetDatabase APIs are generally preferred over raw File/Directory checks for assets.
            // Check if it's a known asset GUID.
#if UNITY_2022_0_OR_NEWER
            var assetPathExists = AssetDatabase.AssetPathExists(sanitizedPath);
#else
            var assetPathExists = AssetPathExists(sanitizedPath);
#endif
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(sanitizedPath)) && assetPathExists)
            {
                return true;
            }
            // AssetPathToGUID might not work for newly created folders not yet refreshed.
            // Check directory explicitly for folders.
            if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), sanitizedPath)))
            {
                // Check if it's considered a *valid* folder by Unity
                return AssetDatabase.IsValidFolder(sanitizedPath);
            }
            // Check file existence for non-folder assets.
            if (File.Exists(Path.Combine(Directory.GetCurrentDirectory(), sanitizedPath)))
            {
                return true; // Assume if file exists, it's an asset or will be imported
            }

            return false;
            // Alternative: return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(sanitizedPath));
        }

        /// <summary>
        /// Ensures the directory for a given asset path exists, creating it if necessary.
        /// </summary>
        static void EnsureDirectoryExists(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath))
                return;
            string fullDirPath = Path.Combine(Directory.GetCurrentDirectory(), directoryPath);
            if (!Directory.Exists(fullDirPath))
            {
                Directory.CreateDirectory(fullDirPath);
                AssetDatabase.Refresh(); // Let Unity know about the new folder
            }
        }

        /// <summary>
        /// Applies properties from JObject to a Material.
        /// </summary>
        static bool IsAssetType(string lowerAssetType, params string[] aliases)
        {
            return aliases.Any(alias => string.Equals(lowerAssetType, alias, StringComparison.OrdinalIgnoreCase));
        }

        static bool IsAllowedAssetType(UnityEngine.Object asset, string lowerAssetType)
        {
            if (asset == null)
                return false;
            if (IsAssetType(lowerAssetType, "material", "unityengine.material"))
                return asset is Material;
            if (IsAssetType(lowerAssetType, "physicsmaterial", "physicmaterial", "physics_material", "unityengine.physicsmaterial", "unityengine.physicmaterial"))
                return asset is PhysicsMaterialType;
            if (IsAssetType(lowerAssetType, "physicsmaterial2d", "physics_material_2d", "unityengine.physicsmaterial2d"))
                return asset is PhysicsMaterial2D;
            if (IsAssetType(lowerAssetType, "rendertexture", "render_texture", "unityengine.rendertexture"))
                return asset is RenderTexture;
            if (IsAssetType(lowerAssetType, "terrainlayer", "terrain_layer", "unityengine.terrainlayer"))
                return asset is TerrainLayer;
            if (IsAssetType(lowerAssetType, "avatarmask", "avatar_mask", "unityengine.avatarmask"))
                return asset is AvatarMask;
            if (IsAssetType(lowerAssetType, "shadervariantcollection", "shader_variant_collection", "unityengine.shadervariantcollection"))
                return asset is ShaderVariantCollection;
            if (IsAssetType(lowerAssetType, "scriptableobject", "scriptable_object"))
                return asset is ScriptableObject;

            return string.Equals(asset.GetType().FullName, lowerAssetType, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(asset.GetType().Name, lowerAssetType, StringComparison.OrdinalIgnoreCase);
        }

        static bool ApplyGenericAssetProperties(UnityEngine.Object asset, JObject properties)
        {
            if (asset == null || properties == null)
                return false;
            if (asset is Material material)
                return ApplyMaterialProperties(material, properties);
            if (asset is PhysicsMaterialType physicsMaterial)
                return ApplyPhysicsMaterialProperties(physicsMaterial, properties);
            if (asset is PhysicsMaterial2D physicsMaterial2D)
                return ApplyPhysicsMaterial2DProperties(physicsMaterial2D, properties);
            if (asset is RenderTexture renderTexture)
                return ApplyRenderTextureProperties(renderTexture, properties);
            if (asset is TerrainLayer terrainLayer)
                return ApplyTerrainLayerProperties(terrainLayer, properties);
            if (asset is AvatarMask avatarMask)
                return ApplyAvatarMaskProperties(avatarMask, properties);
            if (asset is ShaderVariantCollection collection)
                return ApplyShaderVariantCollectionProperties(collection, properties);

            return ApplyObjectProperties(asset, properties);
        }

        static RenderTexture CreateRenderTexture(JObject properties)
        {
            ApplyRenderTexturePresetDefaults(properties);

            var width = Mathf.Clamp(ReadInt(properties, "width", 256), 1, 8192);
            var height = Mathf.Clamp(ReadInt(properties, "height", 256), 1, 8192);
            var depth = Mathf.Clamp(ReadInt(properties, "depth", 0), 0, 32);
            var format = ReadEnum(properties, "format", RenderTextureFormat.ARGB32);
            var rt = new RenderTexture(width, height, depth, format)
            {
                name = properties?["name"]?.ToString(),
                antiAliasing = Mathf.Clamp(ReadInt(properties, "antiAliasing", 1), 1, 8),
                useMipMap = ReadBool(properties, "useMipMap", false),
                autoGenerateMips = ReadBool(properties, "autoGenerateMips", true),
                filterMode = ReadEnum(properties, "filterMode", FilterMode.Bilinear),
                wrapMode = ReadEnum(properties, "wrapMode", TextureWrapMode.Clamp)
            };
            return rt;
        }

        static bool ApplyMaterialProperties(Material mat, JObject properties)
        {
            if (mat == null || properties == null)
                return false;
            bool modified = false;

            // Example: Set shader
            if (properties["shader"]?.Type == JTokenType.String)
            {
                Shader newShader = Shader.Find(properties["shader"].ToString());
                if (newShader != null && mat.shader != newShader)
                {
                    mat.shader = newShader;
                    modified = true;
                }
            }
            // Example: Set color property
            if (properties["color"] is JObject colorProps)
            {
                string propName = colorProps["name"]?.ToString() ?? "_Color"; // Default main color
                if (colorProps["value"] is JArray colArr && colArr.Count >= 3)
                {
                    try
                    {
                        Color newColor = new Color(
                            colArr[0].ToObject<float>(),
                            colArr[1].ToObject<float>(),
                            colArr[2].ToObject<float>(),
                            colArr.Count > 3 ? colArr[3].ToObject<float>() : 1.0f
                        );
                        if (mat.HasProperty(propName) && mat.GetColor(propName) != newColor)
                        {
                            mat.SetColor(propName, newColor);
                            modified = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"Error parsing color property '{propName}': {ex.Message}"
                        );
                    }
                }
            } else if (properties["color"] is JArray colorArr) //Use color now with examples set in UniBridge_ManageAsset.py
            {
                string propName =  "_Color";
                try {
                    if (colorArr.Count >= 3)
                    {
                        Color newColor = new Color(
                            colorArr[0].ToObject<float>(),
                            colorArr[1].ToObject<float>(),
                            colorArr[2].ToObject<float>(),
                            colorArr.Count > 3 ? colorArr[3].ToObject<float>() : 1.0f
                        );
                        if (mat.HasProperty(propName) && mat.GetColor(propName) != newColor)
                        {
                            mat.SetColor(propName, newColor);
                            modified = true;
                        }
                    }
                }
                catch (Exception ex) {
                    Debug.LogWarning(
                        $"Error parsing color property '{propName}': {ex.Message}"
                    );
                }
            }
            // Example: Set float property
            if (properties["float"] is JObject floatProps)
            {
                string propName = floatProps["name"]?.ToString();
                if (
                    !string.IsNullOrEmpty(propName) &&
                    (floatProps["value"]?.Type == JTokenType.Float || floatProps["value"]?.Type == JTokenType.Integer)
                )
                {
                    try
                    {
                        float newVal = floatProps["value"].ToObject<float>();
                        if (mat.HasProperty(propName) && mat.GetFloat(propName) != newVal)
                        {
                            mat.SetFloat(propName, newVal);
                            modified = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"Error parsing float property '{propName}': {ex.Message}"
                        );
                    }
                }
            }
            // Example: Set texture property
            if (properties["texture"] is JObject texProps)
            {
                string propName = texProps["name"]?.ToString() ?? "_MainTex"; // Default main texture
                string texPath = texProps["path"]?.ToString();
                if (!string.IsNullOrEmpty(texPath))
                {
                    Texture newTex = AssetDatabase.LoadAssetAtPath<Texture>(
                        SanitizeAssetPath(texPath)
                    );
                    if (
                        newTex != null
                        && mat.HasProperty(propName)
                        && mat.GetTexture(propName) != newTex
                    )
                    {
                        mat.SetTexture(propName, newTex);
                        modified = true;
                    }
                    else if (newTex == null)
                    {
                        Debug.LogWarning($"Texture not found at path: {texPath}");
                    }
                }
            }

            // TODO: Add handlers for other property types (Vectors, Ints, Keywords, RenderQueue, etc.)
            return modified;
        }

        /// <summary>
        ///  Applies properties from JObject to a PhysicsMaterial.
        /// </summary>
        static bool ApplyPhysicsMaterialProperties(PhysicsMaterialType pmat, JObject properties)
        {
            if (pmat == null || properties == null)
                return false;
            bool modified = false;

            // Example: Set dynamic friction
            if (properties["dynamicFriction"]?.Type == JTokenType.Float)
            {
                float dynamicFriction = properties["dynamicFriction"].ToObject<float>();
                pmat.dynamicFriction = dynamicFriction;
                modified = true;
            }

            // Example: Set static friction
            if (properties["staticFriction"]?.Type == JTokenType.Float)
            {
                float staticFriction = properties["staticFriction"].ToObject<float>();
                pmat.staticFriction = staticFriction;
                modified = true;
            }

            // Example: Set bounciness
            if (properties["bounciness"]?.Type == JTokenType.Float)
            {
                float bounciness = properties["bounciness"].ToObject<float>();
                pmat.bounciness = bounciness;
                modified = true;
            }

            List<String> averageList = new List<String> { "ave", "Ave", "average", "Average" };
            List<String> multiplyList = new List<String> { "mul", "Mul", "mult", "Mult", "multiply", "Multiply" };
            List<String> minimumList = new List<String> { "min", "Min", "minimum", "Minimum" };
            List<String> maximumList = new List<String> { "max", "Max", "maximum", "Maximum" };

            // Example: Set friction combine
            if (properties["frictionCombine"]?.Type == JTokenType.String)
            {
                string frictionCombine = properties["frictionCombine"].ToString();
                if (averageList.Contains(frictionCombine))
                    pmat.frictionCombine = PhysicsMaterialCombine.Average;
                else if (multiplyList.Contains(frictionCombine))
                    pmat.frictionCombine = PhysicsMaterialCombine.Multiply;
                else if (minimumList.Contains(frictionCombine))
                    pmat.frictionCombine = PhysicsMaterialCombine.Minimum;
                else if (maximumList.Contains(frictionCombine))
                    pmat.frictionCombine = PhysicsMaterialCombine.Maximum;
                modified = true;
            }

            // Example: Set bounce combine
            if (properties["bounceCombine"]?.Type == JTokenType.String)
            {
                string bounceCombine = properties["bounceCombine"].ToString();
                if (averageList.Contains(bounceCombine))
                    pmat.bounceCombine = PhysicsMaterialCombine.Average;
                else if (multiplyList.Contains(bounceCombine))
                    pmat.bounceCombine = PhysicsMaterialCombine.Multiply;
                else if (minimumList.Contains(bounceCombine))
                    pmat.bounceCombine = PhysicsMaterialCombine.Minimum;
                else if (maximumList.Contains(bounceCombine))
                    pmat.bounceCombine = PhysicsMaterialCombine.Maximum;
                modified = true;
            }

            return modified;
        }

        static bool ApplyPhysicsMaterial2DProperties(PhysicsMaterial2D pmat, JObject properties)
        {
            if (pmat == null || properties == null)
                return false;

            var modified = false;
            var preset = properties["preset"]?.ToString();
            if (!string.IsNullOrWhiteSpace(preset))
            {
                switch (preset.Trim().ToLowerInvariant())
                {
                    case "bouncy":
                    case "bounce":
                        pmat.friction = 0.1f;
                        pmat.bounciness = 0.9f;
                        modified = true;
                        break;
                    case "ice":
                    case "slippery":
                        pmat.friction = 0.02f;
                        pmat.bounciness = 0.05f;
                        modified = true;
                        break;
                    case "sticky":
                        pmat.friction = 1f;
                        pmat.bounciness = 0f;
                        modified = true;
                        break;
                }
            }

            if (TryReadFloat(properties, "friction", out var friction))
            {
                pmat.friction = friction;
                modified = true;
            }

            if (TryReadFloat(properties, "bounciness", out var bounciness))
            {
                pmat.bounciness = bounciness;
                modified = true;
            }

            return modified;
        }

        static bool ApplyRenderTextureProperties(RenderTexture rt, JObject properties)
        {
            if (rt == null || properties == null)
                return false;

            var modified = false;
            modified |= ApplyRenderTexturePresetDefaults(properties);

            var width = Mathf.Clamp(ReadInt(properties, "width", rt.width), 1, 8192);
            var height = Mathf.Clamp(ReadInt(properties, "height", rt.height), 1, 8192);
            var depth = Mathf.Clamp(ReadInt(properties, "depth", rt.depth), 0, 32);
            if (rt.width != width || rt.height != height || rt.depth != depth)
            {
                rt.Release();
                rt.width = width;
                rt.height = height;
                rt.depth = depth;
                modified = true;
            }

            var antiAliasing = Mathf.Clamp(ReadInt(properties, "antiAliasing", rt.antiAliasing), 1, 8);
            if (rt.antiAliasing != antiAliasing)
            {
                rt.antiAliasing = antiAliasing;
                modified = true;
            }

            modified |= SetIfDifferent(rt.useMipMap, ReadBool(properties, "useMipMap", rt.useMipMap), value => rt.useMipMap = value);
            modified |= SetIfDifferent(rt.autoGenerateMips, ReadBool(properties, "autoGenerateMips", rt.autoGenerateMips), value => rt.autoGenerateMips = value);
            modified |= SetIfDifferent(rt.filterMode, ReadEnum(properties, "filterMode", rt.filterMode), value => rt.filterMode = value);
            modified |= SetIfDifferent(rt.wrapMode, ReadEnum(properties, "wrapMode", rt.wrapMode), value => rt.wrapMode = value);
            return modified;
        }

        static bool ApplyRenderTexturePresetDefaults(JObject properties)
        {
            var preset = properties?["preset"]?.ToString()?.Trim().ToLowerInvariant();
            if (preset == "ui")
            {
                SetDefault(properties, "width", 1024);
                SetDefault(properties, "height", 1024);
                SetDefault(properties, "depth", 0);
                SetDefault(properties, "format", "ARGB32");
                return true;
            }
            else if (preset == "camera")
            {
                SetDefault(properties, "width", 1920);
                SetDefault(properties, "height", 1080);
                SetDefault(properties, "depth", 24);
                SetDefault(properties, "format", "ARGB32");
                return true;
            }
            else if (preset == "depth")
            {
                SetDefault(properties, "width", 1024);
                SetDefault(properties, "height", 1024);
                SetDefault(properties, "depth", 24);
                return true;
            }

            return false;
        }

        static bool ApplyTerrainLayerProperties(TerrainLayer layer, JObject properties)
        {
            if (layer == null || properties == null)
                return false;

            var modified = false;
            var diffusePath = properties["diffuseTexture"]?.ToString() ?? properties["diffuse"]?.ToString();
            if (!string.IsNullOrWhiteSpace(diffusePath))
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(SanitizeAssetPath(diffusePath));
                if (texture != null && layer.diffuseTexture != texture)
                {
                    layer.diffuseTexture = texture;
                    modified = true;
                }
            }

            var normalPath = properties["normalMapTexture"]?.ToString() ?? properties["normal"]?.ToString();
            if (!string.IsNullOrWhiteSpace(normalPath))
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(SanitizeAssetPath(normalPath));
                if (texture != null && layer.normalMapTexture != texture)
                {
                    layer.normalMapTexture = texture;
                    modified = true;
                }
            }

            if (properties["tileSize"] is JArray tileSize && tileSize.Count >= 2)
            {
                layer.tileSize = new Vector2(tileSize[0].ToObject<float>(), tileSize[1].ToObject<float>());
                modified = true;
            }

            if (properties["tileOffset"] is JArray tileOffset && tileOffset.Count >= 2)
            {
                layer.tileOffset = new Vector2(tileOffset[0].ToObject<float>(), tileOffset[1].ToObject<float>());
                modified = true;
            }

            modified |= ApplyObjectProperties(layer, RemoveHandledProperties(properties, "preset", "diffuseTexture", "diffuse", "normalMapTexture", "normal", "tileSize", "tileOffset"));
            return modified;
        }

        static bool ApplyAvatarMaskProperties(AvatarMask mask, JObject properties)
        {
            if (mask == null || properties == null)
                return false;

            var modified = false;
            var preset = properties["preset"]?.ToString()?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(preset))
            {
                var active = preset != "none";
                foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
                {
                    if (part == AvatarMaskBodyPart.LastBodyPart)
                        continue;
                    mask.SetHumanoidBodyPartActive(part, active);
                }

                if (preset == "upperbody" || preset == "upper_body")
                {
                    mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg, false);
                    mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg, false);
                    mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFootIK, false);
                    mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFootIK, false);
                }

                modified = true;
            }

            if (properties["bodyParts"] is JObject bodyParts)
            {
                foreach (var prop in bodyParts.Properties())
                {
                    if (Enum.TryParse(prop.Name, ignoreCase: true, out AvatarMaskBodyPart part) &&
                        part != AvatarMaskBodyPart.LastBodyPart &&
                        prop.Value.Type == JTokenType.Boolean)
                    {
                        mask.SetHumanoidBodyPartActive(part, prop.Value.ToObject<bool>());
                        modified = true;
                    }
                }
            }

            if (properties["transformPaths"] is JArray transformPaths)
            {
                mask.transformCount = transformPaths.Count;
                for (var i = 0; i < transformPaths.Count; i++)
                {
                    var path = transformPaths[i]?.ToString();
                    mask.SetTransformPath(i, path);
                    mask.SetTransformActive(i, true);
                }
                modified = true;
            }

            return modified;
        }

        static bool ApplyShaderVariantCollectionProperties(ShaderVariantCollection collection, JObject properties)
        {
            if (collection == null || properties == null)
                return false;

            var modified = false;
            if (properties["variants"] is not JArray variants)
                return modified;

            foreach (var token in variants.OfType<JObject>())
            {
                var shaderName = token["shader"]?.ToString();
                if (string.IsNullOrWhiteSpace(shaderName))
                    continue;

                var shader = Shader.Find(shaderName) ?? AssetDatabase.LoadAssetAtPath<Shader>(SanitizeAssetPath(shaderName));
                if (shader == null)
                {
                    Debug.LogWarning($"[ManageAsset] ShaderVariantCollection shader not found: {shaderName}");
                    continue;
                }

                var passType = ReadEnum(token, "passType", PassType.Normal);
                var keywords = token["keywords"] is JArray keywordArray
                    ? keywordArray.Select(item => item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray()
                    : Array.Empty<string>();
                var variant = new ShaderVariantCollection.ShaderVariant(shader, passType, keywords);
                if (collection.Add(variant))
                    modified = true;
            }

            return modified;
        }

        static JObject RemoveHandledProperties(JObject source, params string[] handled)
        {
            var result = new JObject(source);
            foreach (var name in handled)
                result.Remove(name);
            return result;
        }

        static bool TryReadFloat(JObject properties, string name, out float value)
        {
            value = 0f;
            var token = properties?[name];
            if (token == null || token.Type == JTokenType.Null)
                return false;
            value = token.ToObject<float>();
            return true;
        }

        static int ReadInt(JObject properties, string name, int fallback)
        {
            var token = properties?[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToObject<int>();
        }

        static bool ReadBool(JObject properties, string name, bool fallback)
        {
            var token = properties?[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToObject<bool>();
        }

        static TEnum ReadEnum<TEnum>(JObject properties, string name, TEnum fallback) where TEnum : struct
        {
            var token = properties?[name];
            if (token == null || token.Type == JTokenType.Null)
                return fallback;
            return Enum.TryParse(token.ToString(), ignoreCase: true, out TEnum value) ? value : fallback;
        }

        static void SetDefault(JObject properties, string name, JToken value)
        {
            if (properties != null && properties[name] == null)
                properties[name] = value;
        }

        static bool SetIfDifferent<T>(T current, T next, Action<T> setter)
        {
            if (EqualityComparer<T>.Default.Equals(current, next))
                return false;
            setter(next);
            return true;
        }

        /// <summary>
        /// Generic helper to set properties on any UnityEngine.Object using reflection.
        /// </summary>
        static bool ApplyObjectProperties(UnityEngine.Object target, JObject properties)
        {
            if (target == null || properties == null)
                return false;
            bool modified = false;
            Type type = target.GetType();

            foreach (var prop in properties.Properties())
            {
                string propName = prop.Name;
                JToken propValue = prop.Value;
                if (SetPropertyOrField(target, propName, propValue, type))
                {
                    modified = true;
                }
            }
            return modified;
        }

        /// <summary>
        /// Helper to set a property or field via reflection, handling basic types and Unity objects.
        /// </summary>
        static bool SetPropertyOrField(
            object target,
            string memberName,
            JToken value,
            Type type = null
        )
        {
            type = type ?? target.GetType();
            System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.IgnoreCase;

            try
            {
                System.Reflection.PropertyInfo propInfo = type.GetProperty(memberName, flags);
                if (propInfo != null && propInfo.CanWrite)
                {
                    object convertedValue = ConvertJTokenToType(value, propInfo.PropertyType);
                    if (
                        convertedValue != null
                        && !Equals(propInfo.GetValue(target), convertedValue)
                    )
                    {
                        propInfo.SetValue(target, convertedValue);
                        return true;
                    }
                }
                else
                {
                    System.Reflection.FieldInfo fieldInfo = type.GetField(memberName, flags);
                    if (fieldInfo != null)
                    {
                        object convertedValue = ConvertJTokenToType(value, fieldInfo.FieldType);
                        if (
                            convertedValue != null
                            && !Equals(fieldInfo.GetValue(target), convertedValue)
                        )
                        {
                            fieldInfo.SetValue(target, convertedValue);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[SetPropertyOrField] Failed to set '{memberName}' on {type.Name}: {ex.Message}"
                );
            }
            return false;
        }

        /// <summary>
        /// Simple JToken to Type conversion for common Unity types and primitives.
        /// </summary>
        static object ConvertJTokenToType(JToken token, Type targetType)
        {
            try
            {
                if (token == null || token.Type == JTokenType.Null)
                    return null;

                if (targetType == typeof(string))
                    return token.ToObject<string>();
                if (targetType == typeof(int))
                    return token.ToObject<int>();
                if (targetType == typeof(float))
                    return token.ToObject<float>();
                if (targetType == typeof(bool))
                    return token.ToObject<bool>();
                if (targetType == typeof(Vector2) && token is JArray arrV2 && arrV2.Count == 2)
                    return new Vector2(arrV2[0].ToObject<float>(), arrV2[1].ToObject<float>());
                if (targetType == typeof(Vector3) && token is JArray arrV3 && arrV3.Count == 3)
                    return new Vector3(
                        arrV3[0].ToObject<float>(),
                        arrV3[1].ToObject<float>(),
                        arrV3[2].ToObject<float>()
                    );
                if (targetType == typeof(Vector4) && token is JArray arrV4 && arrV4.Count == 4)
                    return new Vector4(
                        arrV4[0].ToObject<float>(),
                        arrV4[1].ToObject<float>(),
                        arrV4[2].ToObject<float>(),
                        arrV4[3].ToObject<float>()
                    );
                if (targetType == typeof(Quaternion) && token is JArray arrQ && arrQ.Count == 4)
                    return new Quaternion(
                        arrQ[0].ToObject<float>(),
                        arrQ[1].ToObject<float>(),
                        arrQ[2].ToObject<float>(),
                        arrQ[3].ToObject<float>()
                    );
                if (targetType == typeof(Color) && token is JArray arrC && arrC.Count >= 3) // Allow RGB or RGBA
                    return new Color(
                        arrC[0].ToObject<float>(),
                        arrC[1].ToObject<float>(),
                        arrC[2].ToObject<float>(),
                        arrC.Count > 3 ? arrC[3].ToObject<float>() : 1.0f
                    );
                if (targetType.IsEnum)
                    return Enum.Parse(targetType, token.ToString(), true); // Case-insensitive enum parsing

                // Handle loading Unity Objects (Materials, Textures, etc.) by path
                if (
                    typeof(UnityEngine.Object).IsAssignableFrom(targetType)
                    && token.Type == JTokenType.String
                )
                {
                    string assetPath = SanitizeAssetPath(token.ToString());
                    UnityEngine.Object loadedAsset = AssetDatabase.LoadAssetAtPath(
                        assetPath,
                        targetType
                    );
                    if (loadedAsset == null)
                    {
                        Debug.LogWarning(
                            $"[ConvertJTokenToType] Could not load asset of type {targetType.Name} from path: {assetPath}"
                        );
                    }
                    return loadedAsset;
                }

                // Fallback: Try direct conversion (might work for other simple value types)
                return token.ToObject(targetType);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[ConvertJTokenToType] Could not convert JToken '{token}' (type {token.Type}) to type '{targetType.Name}': {ex.Message}"
                );
                return null;
            }
        }


        // --- Data Serialization ---

        /// <summary>
        /// Creates a serializable representation of an asset.
        /// </summary>
        static object GetAssetData(string path, bool generatePreview = false)
        {
            if (string.IsNullOrEmpty(path) || !AssetExists(path))
                return null;

            string guid = AssetDatabase.AssetPathToGUID(path);
            Type assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            string previewBase64 = null;
            int previewWidth = 0;
            int previewHeight = 0;

            if (generatePreview && asset != null)
            {
                Texture2D preview = AssetPreview.GetAssetPreview(asset);

                if (preview != null)
                {
                    try
                    {
                        // Ensure texture is readable for EncodeToPNG
                        // Creating a temporary readable copy is safer
                        RenderTexture rt = null;
                        Texture2D readablePreview = null;
                        RenderTexture previous = RenderTexture.active;
                        try
                        {
                            rt = RenderTexture.GetTemporary(preview.width, preview.height);
                            Graphics.Blit(preview, rt);
                            RenderTexture.active = rt;
                            readablePreview = new Texture2D(preview.width, preview.height, TextureFormat.RGB24, false);
                            readablePreview.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                            readablePreview.Apply();

                            var pngData = readablePreview.EncodeToPNG();
                            if (pngData != null && pngData.Length > 0)
                            {
                                previewBase64 = Convert.ToBase64String(pngData);
                                previewWidth = readablePreview.width;
                                previewHeight = readablePreview.height;
                            }
                        }
                        finally
                        {
                            RenderTexture.active = previous;
                            if (rt != null) RenderTexture.ReleaseTemporary(rt);
                            if (readablePreview != null) UnityEngine.Object.DestroyImmediate(readablePreview);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"Failed to generate readable preview for '{path}': {ex.Message}. Preview might not be readable."
                        );
                        // Fallback: Try getting static preview if available?
                        // Texture2D staticPreview = AssetPreview.GetMiniThumbnail(asset);
                    }
                }
                else
                {
                    Debug.LogWarning(
                        $"Could not get asset preview for {path} (Type: {assetType?.Name}). Is it supported?"
                    );
                }
            }

            return new
            {
                path = path,
                guid = guid,
                assetType = assetType?.FullName ?? "Unknown",
                name = Path.GetFileNameWithoutExtension(path),
                fileName = Path.GetFileName(path),
                isFolder = AssetDatabase.IsValidFolder(path),
                instanceID = UnityApiAdapter.GetObjectId(asset),
                lastWriteTimeUtc = File.GetLastWriteTimeUtc(
                        Path.Combine(Directory.GetCurrentDirectory(), path)
                    )
                    .ToString("o"), // ISO 8601
                // --- Preview Data ---
                previewBase64 = previewBase64, // PNG data as Base64 string
                previewWidth = previewWidth,
                previewHeight = previewHeight,
                // TODO: Add more metadata? Importer settings? Dependencies?
            };
        }
    }
}
