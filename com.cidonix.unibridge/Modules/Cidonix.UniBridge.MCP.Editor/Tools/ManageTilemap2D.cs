#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// High-level 2D Tilemap authoring for agents.
    /// </summary>
    public static class ManageTilemap2D
    {
        const string ToolName = "UniBridge_ManageTilemap2D";
        const int MaxInspectCells = 2000;

        public const string Title = "Manage 2D Tilemaps";

        public const string Description = @"Create and edit Grid/Tilemap authoring structures, Tile assets, and tile cells.

Use this when an agent needs to build 2D levels quickly: create a Grid, add Tilemap layers, create Tile assets from sprites, paint/erase cells, inspect bounds, and configure TilemapRenderer/TilemapCollider2D basics.

Args:
    Action: Inspect, CreateGrid, CreateLayer, CreateTileAsset, PaintCells, EraseCells, Clear, or CompressBounds.
    GridName: Grid GameObject name for CreateGrid/CreateLayer.
    LayerName: Tilemap child name for CreateLayer or target lookup.
    Target: Existing Tilemap GameObject name/path/id for PaintCells/EraseCells/Clear/Inspect.
    TilePath: Assets/... .asset Tile to paint.
    SpritePath: Assets/... sprite for CreateTileAsset.
    Cells: Array of [x,y,z] or {x,y,z,tilePath,color}.
    AddCollider: Add TilemapCollider2D to created layer.
    Composite: Configure Rigidbody2D + CompositeCollider2D for created layer.

Returns:
    success, message, and data with created/target object summaries, tile asset data, painted cell counts, and tilemap bounds.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new { type = "string", description = "Tilemap operation.", @enum = new[] { "Inspect", "CreateGrid", "CreateLayer", "CreateTileAsset", "PaintCells", "EraseCells", "Clear", "CompressBounds" } },
                    Target = new { description = "Tilemap GameObject target.", anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    SearchMethod = new { type = "string", description = "Target search method." },
                    GridName = new { type = "string", description = "Grid GameObject name.", @default = "Grid" },
                    LayerName = new { type = "string", description = "Tilemap layer GameObject name.", @default = "Tilemap" },
                    Parent = new { description = "Optional parent for Grid.", anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    CellSize = new { type = "array", description = "Grid cell size [x,y,z].", items = new { type = "number" }, minItems = 2, maxItems = 3 },
                    TilePath = new { type = "string", description = "Tile asset path for paint or create.", @default = "Assets/Tiles/NewTile.asset" },
                    SpritePath = new { type = "string", description = "Sprite asset path for CreateTileAsset." },
                    Color = new { type = "array", description = "RGBA color [r,g,b,a].", items = new { type = "number" }, minItems = 3, maxItems = 4 },
                    ColliderType = new { type = "string", description = "Tile collider type: None, Sprite, Grid.", @default = "Sprite" },
                    AddCollider = new { type = "boolean", description = "Add TilemapCollider2D when creating a layer.", @default = false },
                    Composite = new { type = "boolean", description = "Add Rigidbody2D + CompositeCollider2D for a created layer.", @default = false },
                    Cells = new { type = "array", description = "Cells to paint/erase.", items = new { type = "object", additionalProperties = true } },
                    IncludeCells = new { type = "boolean", description = "Include occupied cells in Inspect.", @default = true },
                    Limit = new { type = "integer", description = "Max occupied cells to return.", @default = 200 }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "2d", "tilemap" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = Normalize(GetString(parameters, "Action", "action") ?? "Inspect");

            try
            {
                return action switch
                {
                    "inspect" => Inspect(parameters),
                    "creategrid" => CreateGrid(parameters),
                    "createlayer" => CreateLayer(parameters),
                    "createtileasset" or "createtile" => CreateTileAsset(parameters),
                    "paintcells" or "paint" => PaintCells(parameters, erase: false),
                    "erasecells" or "erase" => PaintCells(parameters, erase: true),
                    "clear" or "cleartilemap" => Clear(parameters),
                    "compressbounds" or "compress" => Compress(parameters),
                    _ => Response.Error($"Unknown Tilemap2D action '{GetString(parameters, "Action", "action")}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageTilemap2D] Action '{action}' failed: {ex}");
                return Response.Error($"Tilemap2D action '{action}' failed: {ex.Message}");
            }
        }

        static object CreateGrid(JObject parameters)
        {
            var name = GetString(parameters, "GridName", "gridName", "grid_name", "Name", "name") ?? "Grid";
            var existing = SceneObjectLocator.FindObject(name, "by_name", new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            var created = false;
            var gridObject = existing;
            if (gridObject == null)
            {
                gridObject = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(gridObject, "Create Grid");
                created = true;
            }

            var grid = gridObject.GetComponent<Grid>();
            if (grid == null)
                grid = Undo.AddComponent<Grid>(gridObject);
            if (grid == null)
                return Response.Error($"Grid component could not be added to '{name}'.");
            var cellSize = ParseVector3(GetToken(parameters, "CellSize", "cellSize", "cell_size"));
            if (cellSize.HasValue)
                grid.cellSize = cellSize.Value;

            var parentToken = GetToken(parameters, "Parent", "parent");
            if (parentToken != null && parentToken.Type != JTokenType.Null)
            {
                var parent = SceneObjectLocator.FindObject(parentToken.ToString(), "by_id_or_name_or_path", new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
                if (parent == null)
                    return Response.Error($"Parent '{parentToken}' was not found.");
                Undo.SetTransformParent(gridObject.transform, parent.transform, "Parent Grid");
            }

            EditorSceneManager.MarkSceneDirty(gridObject.scene);
            return Response.Success(created ? "Grid created." : "Grid updated.", new { created, grid = BuildObjectSummary(gridObject), cellSize = SerializeVector3(grid.cellSize) });
        }

        static object CreateLayer(JObject parameters)
        {
            var gridName = GetString(parameters, "GridName", "gridName", "grid_name") ?? "Grid";
            var layerName = GetString(parameters, "LayerName", "layerName", "layer_name", "Name", "name") ?? "Tilemap";
            var gridObject = SceneObjectLocator.FindObject(gridName, "by_name", new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            if (gridObject == null)
            {
                var gridResult = CreateGrid(new JObject { ["GridName"] = gridName, ["CellSize"] = GetToken(parameters, "CellSize", "cellSize", "cell_size") });
                gridObject = SceneObjectLocator.FindObject(gridName, "by_name", new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
                if (gridObject == null)
                    return gridResult;
            }

            var layer = new GameObject(layerName);
            Undo.RegisterCreatedObjectUndo(layer, "Create Tilemap Layer");
            Undo.SetTransformParent(layer.transform, gridObject.transform, "Parent Tilemap Layer");
            layer.transform.localPosition = Vector3.zero;
            layer.transform.localRotation = Quaternion.identity;
            layer.transform.localScale = Vector3.one;

            var tilemap = layer.GetComponent<Tilemap>();
            if (tilemap == null)
                tilemap = Undo.AddComponent<Tilemap>(layer);
            if (tilemap == null)
                return Response.Error($"Tilemap component could not be added to '{layer.name}'.");

            var renderer = layer.GetComponent<TilemapRenderer>();
            if (renderer == null)
                renderer = Undo.AddComponent<TilemapRenderer>(layer);
            if (renderer == null)
                return Response.Error($"TilemapRenderer component could not be added to '{layer.name}'.");
            renderer.sortingLayerName = GetString(parameters, "SortingLayer", "sortingLayer", "sorting_layer") ?? renderer.sortingLayerName;
            renderer.sortingOrder = GetInt(parameters, renderer.sortingOrder, "SortingOrder", "sortingOrder", "sorting_order");

            var addCollider = GetBool(parameters, false, "AddCollider", "addCollider", "add_collider", "Collider", "collider");
            var composite = GetBool(parameters, false, "Composite", "composite", "UseComposite", "useComposite", "use_composite");
            TilemapCollider2D tilemapCollider = null;
            CompositeCollider2D compositeCollider = null;
            Rigidbody2D rigidbody = null;
            if (addCollider || composite)
            {
                tilemapCollider = layer.GetComponent<TilemapCollider2D>();
                if (tilemapCollider == null)
                    tilemapCollider = Undo.AddComponent<TilemapCollider2D>(layer);
                if (tilemapCollider == null)
                    return Response.Error($"TilemapCollider2D component could not be added to '{layer.name}'.");

                if (composite)
                {
                    rigidbody = layer.GetComponent<Rigidbody2D>();
                    if (rigidbody == null)
                        rigidbody = Undo.AddComponent<Rigidbody2D>(layer);
                    if (rigidbody == null)
                        return Response.Error($"Rigidbody2D component could not be added to '{layer.name}'.");
                    rigidbody.bodyType = RigidbodyType2D.Static;
                    compositeCollider = layer.GetComponent<CompositeCollider2D>();
                    if (compositeCollider == null)
                        compositeCollider = Undo.AddComponent<CompositeCollider2D>(layer);
                    if (compositeCollider == null)
                        return Response.Error($"CompositeCollider2D component could not be added to '{layer.name}'.");
#if UNITY_2023_1_OR_NEWER
                    tilemapCollider.compositeOperation = Collider2D.CompositeOperation.Merge;
#else
                    tilemapCollider.usedByComposite = true;
#endif
                }
            }

            EditorSceneManager.MarkSceneDirty(layer.scene);
            return Response.Success("Tilemap layer created.", new
            {
                grid = BuildObjectSummary(gridObject),
                layer = BuildTilemapSummary(tilemap),
                renderer = new { sortingLayerName = renderer.sortingLayerName, sortingOrder = renderer.sortingOrder, mode = renderer.mode.ToString() },
                collider = tilemapCollider != null,
                composite = compositeCollider != null,
                rigidbody = rigidbody != null ? new { bodyType = rigidbody.bodyType.ToString() } : null
            });
        }

        static object CreateTileAsset(JObject parameters)
        {
            var path = NormalizeAssetPath(GetString(parameters, "TilePath", "tilePath", "tile_path", "Path", "path") ?? "Assets/Tiles/NewTile.asset");
            var spritePath = NormalizeAssetPath(GetString(parameters, "SpritePath", "spritePath", "sprite_path"));
            var sprite = string.IsNullOrWhiteSpace(spritePath) ? null : AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
            if (!string.IsNullOrWhiteSpace(spritePath) && sprite == null)
                return Response.Error($"Sprite was not found at '{spritePath}'.");

            EnsureParentDirectory(path);
            var existing = AssetDatabase.LoadAssetAtPath<Tile>(path);
            var created = false;
            var tile = existing;
            if (tile == null)
            {
                tile = ScriptableObject.CreateInstance<Tile>();
                AssetDatabase.CreateAsset(tile, path);
                created = true;
            }
            else
            {
                VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true);
            }

            Undo.RecordObject(tile, "Create or update Tile");
            if (sprite != null)
                tile.sprite = sprite;
            tile.color = ParseColor(GetToken(parameters, "Color", "color")) ?? tile.color;
            tile.colliderType = ParseEnum(GetString(parameters, "ColliderType", "colliderType", "collider_type"), Tile.ColliderType.Sprite);
            EditorUtility.SetDirty(tile);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return Response.Success(created ? "Tile asset created." : "Tile asset updated.", BuildTileAssetSummary(path, tile));
        }

        static object PaintCells(JObject parameters, bool erase)
        {
            var tilemap = ResolveTilemap(parameters);
            if (tilemap == null)
                return Response.Error("Target Tilemap was not found.");

            var cells = GetArray(parameters, "Cells", "cells");
            if (cells == null || cells.Count == 0)
                return Response.Error("Cells array is required.");

            var defaultTilePath = NormalizeAssetPath(GetString(parameters, "TilePath", "tilePath", "tile_path"));
            var defaultTile = erase || string.IsNullOrWhiteSpace(defaultTilePath)
                ? null
                : AssetDatabase.LoadAssetAtPath<TileBase>(defaultTilePath);
            if (!erase && !string.IsNullOrWhiteSpace(defaultTilePath) && defaultTile == null)
                return Response.Error($"Tile asset was not found at '{defaultTilePath}'.");

            Undo.RecordObject(tilemap, erase ? "Erase Tilemap Cells" : "Paint Tilemap Cells");
            var changed = 0;
            foreach (var cellToken in cells)
            {
                var position = ParseCellPosition(cellToken);
                var tile = defaultTile;
                if (!erase && cellToken is JObject cellObj)
                {
                    var cellTilePath = NormalizeAssetPath(GetString(cellObj, "TilePath", "tilePath", "tile_path"));
                    if (!string.IsNullOrWhiteSpace(cellTilePath))
                        tile = AssetDatabase.LoadAssetAtPath<TileBase>(cellTilePath);
                }

                tilemap.SetTile(position, erase ? null : tile);
                if (!erase && cellToken is JObject obj)
                {
                    var color = ParseColor(GetToken(obj, "Color", "color"));
                    if (color.HasValue)
                        tilemap.SetColor(position, color.Value);
                }
                changed++;
            }

            tilemap.RefreshAllTiles();
            EditorUtility.SetDirty(tilemap);
            EditorSceneManager.MarkSceneDirty(tilemap.gameObject.scene);

            return Response.Success(erase ? "Tilemap cells erased." : "Tilemap cells painted.", new
            {
                target = BuildTilemapSummary(tilemap),
                changed,
                erased = erase
            });
        }

        static object Clear(JObject parameters)
        {
            var tilemap = ResolveTilemap(parameters);
            if (tilemap == null)
                return Response.Error("Target Tilemap was not found.");

            Undo.RecordObject(tilemap, "Clear Tilemap");
            tilemap.ClearAllTiles();
            EditorUtility.SetDirty(tilemap);
            EditorSceneManager.MarkSceneDirty(tilemap.gameObject.scene);
            return Response.Success("Tilemap cleared.", BuildTilemapSummary(tilemap));
        }

        static object Compress(JObject parameters)
        {
            var tilemap = ResolveTilemap(parameters);
            if (tilemap == null)
                return Response.Error("Target Tilemap was not found.");

            Undo.RecordObject(tilemap, "Compress Tilemap Bounds");
            tilemap.CompressBounds();
            EditorUtility.SetDirty(tilemap);
            EditorSceneManager.MarkSceneDirty(tilemap.gameObject.scene);
            return Response.Success("Tilemap bounds compressed.", BuildTilemapSummary(tilemap));
        }

        static object Inspect(JObject parameters)
        {
            var tilemap = ResolveTilemap(parameters);
            if (tilemap == null)
            {
                var all = SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true })
                    .Select(go => go.GetComponent<Tilemap>())
                    .Where(tm => tm != null)
                    .Select(BuildTilemapSummary)
                    .ToArray();
                return Response.Success("Listed Tilemaps.", new { count = all.Length, tilemaps = all });
            }

            var includeCells = GetBool(parameters, true, "IncludeCells", "includeCells", "include_cells");
            var limit = Mathf.Clamp(GetInt(parameters, 200, "Limit", "limit", "MaxCells", "maxCells", "max_cells"), 0, MaxInspectCells);
            return Response.Success("Inspected Tilemap.", new
            {
                tilemap = BuildTilemapSummary(tilemap),
                cells = includeCells ? ReadCells(tilemap, limit) : null
            });
        }

        static Tilemap ResolveTilemap(JObject parameters)
        {
            var targetToken = GetToken(parameters, "Target", "target", "Tilemap", "tilemap", "LayerName", "layerName", "layer_name");
            if (targetToken == null || targetToken.Type == JTokenType.Null || string.IsNullOrWhiteSpace(targetToken.ToString()))
                return Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<Tilemap>() : null;

            var searchMethod = GetString(parameters, "SearchMethod", "searchMethod", "search_method");
            var go = SceneObjectLocator.FindObject(targetToken.ToString(), searchMethod, new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            return go != null ? go.GetComponent<Tilemap>() : null;
        }

        static object BuildObjectSummary(GameObject go)
        {
            return go == null ? null : new
            {
                name = go.name,
                instanceId = UnityApiAdapter.GetObjectId(go),
                path = SceneObjectLocator.GetHierarchyPath(go),
                scene = go.scene.IsValid() ? new { name = go.scene.name, path = go.scene.path } : null
            };
        }

        static object BuildTilemapSummary(Tilemap tilemap)
        {
            if (tilemap == null)
                return null;

            var renderer = tilemap.GetComponent<TilemapRenderer>();
            var collider = tilemap.GetComponent<TilemapCollider2D>();
            return new
            {
                gameObject = BuildObjectSummary(tilemap.gameObject),
                cellBounds = SerializeBoundsInt(tilemap.cellBounds),
                localBounds = SerializeBounds(tilemap.localBounds),
                color = SerializeColor(tilemap.color),
                orientation = tilemap.orientation.ToString(),
                tileAnchor = SerializeVector3(tilemap.tileAnchor),
                renderer = renderer != null ? new { sortingLayerName = renderer.sortingLayerName, sortingOrder = renderer.sortingOrder, mode = renderer.mode.ToString() } : null,
                collider = collider != null
            };
        }

        static object BuildTileAssetSummary(string path, Tile tile)
        {
            return new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                sprite = tile.sprite != null ? new { name = tile.sprite.name, path = AssetDatabase.GetAssetPath(tile.sprite) } : null,
                color = SerializeColor(tile.color),
                colliderType = tile.colliderType.ToString(),
                flags = tile.flags.ToString()
            };
        }

        static object[] ReadCells(Tilemap tilemap, int limit)
        {
            var cells = new List<object>();
            var bounds = tilemap.cellBounds;
            foreach (var position in bounds.allPositionsWithin)
            {
                var tile = tilemap.GetTile(position);
                if (tile == null)
                    continue;
                cells.Add(new
                {
                    position = new { x = position.x, y = position.y, z = position.z },
                    tile = new { name = tile.name, path = AssetDatabase.GetAssetPath(tile) },
                    color = SerializeColor(tilemap.GetColor(position))
                });
                if (cells.Count >= limit)
                    break;
            }

            return cells.ToArray();
        }

        static Vector3Int ParseCellPosition(JToken token)
        {
            if (token is JArray arr)
            {
                return new Vector3Int(ReadInt(arr, 0), ReadInt(arr, 1), ReadInt(arr, 2));
            }

            if (token is JObject obj)
            {
                return new Vector3Int(
                    GetInt(obj, 0, "x", "X"),
                    GetInt(obj, 0, "y", "Y"),
                    GetInt(obj, 0, "z", "Z"));
            }

            return Vector3Int.zero;
        }

        static Vector3? ParseVector3(JToken token)
        {
            if (token is not JArray arr || arr.Count < 2)
                return null;
            return new Vector3(ReadFloat(arr, 0), ReadFloat(arr, 1), arr.Count > 2 ? ReadFloat(arr, 2) : 1f);
        }

        static Color? ParseColor(JToken token)
        {
            if (token is not JArray arr || arr.Count < 3)
                return null;
            return new Color(ReadFloat(arr, 0), ReadFloat(arr, 1), ReadFloat(arr, 2), arr.Count > 3 ? ReadFloat(arr, 3) : 1f);
        }

        static object SerializeVector3(Vector3 value) => new { x = value.x, y = value.y, z = value.z };

        static object SerializeVector3Int(Vector3Int value) => new { x = value.x, y = value.y, z = value.z };

        static object SerializeColor(Color value) => new { r = value.r, g = value.g, b = value.b, a = value.a };

        static object SerializeBounds(Bounds value) => new { center = SerializeVector3(value.center), size = SerializeVector3(value.size), min = SerializeVector3(value.min), max = SerializeVector3(value.max) };

        static object SerializeBoundsInt(BoundsInt value) => new { position = SerializeVector3Int(value.position), size = SerializeVector3Int(value.size), min = SerializeVector3Int(value.min), max = SerializeVector3Int(value.max) };

        static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            return !string.IsNullOrWhiteSpace(value) && Enum.TryParse<T>(value, true, out var parsed)
                ? parsed
                : fallback;
        }

        static void EnsureParentDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || AssetDatabase.IsValidFolder(directory))
                return;

            var parts = directory.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var normalized = path.Trim().Replace('\\', '/').TrimStart('/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return normalized;
            return "Assets/" + normalized;
        }

        static string Normalize(string value) => (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

        static JToken GetToken(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
                    return token;
            return null;
        }

        static JArray GetArray(JObject obj, params string[] keys)
        {
            return keys.Select(key => obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) ? token as JArray : null).FirstOrDefault(arr => arr != null);
        }

        static string GetString(JObject obj, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString().Trim();
        }

        static bool GetBool(JObject obj, bool defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && bool.TryParse(token.ToString(), out var value) ? value : defaultValue;
        }

        static int GetInt(JObject obj, int defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && int.TryParse(token.ToString(), out var value) ? value : defaultValue;
        }

        static int ReadInt(JArray arr, int index)
        {
            return arr.Count > index && int.TryParse(arr[index]?.ToString(), out var value) ? value : 0;
        }

        static float ReadFloat(JArray arr, int index)
        {
            return arr.Count > index && float.TryParse(arr[index]?.ToString(), out var value) ? value : 0f;
        }
    }
}
