#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Renders project assets into deterministic PNG previews using a temporary preview scene.
    /// </summary>
    public static class CaptureAsset
    {
        const int MinSize = 64;
        const int MaxSize = 4096;
        const int MinGridCellSize = 64;
        const int MaxGridCellSize = 1024;
        const int MaxGridResults = 100;
        const int MaxContactSheetCells = 64;
        const int MaxContactSheetDimension = 8192;
        const int MaxContactSheetSeriesCount = 30;
        static readonly CaptureAssetView[] Default3DContactSheetViews =
        {
            CaptureAssetView.Iso,
            CaptureAssetView.Front,
            CaptureAssetView.Top,
            CaptureAssetView.Right
        };

        public const string Title = "Capture asset preview";

        public const string Description = @"Render a Unity asset into a deterministic PNG preview.

Use this when an agent needs to visually inspect prefab, model, mesh, sprite, texture, or material assets before placing them in a scene. Single Capture renders one asset. CaptureGrid follows the asset-image/contact-sheet pattern: resolve several assets by explicit paths or search inputs, render each preview, stitch them into a grid, and return an index-to-asset mapping. CaptureContactSheet renders one asset from several views and optional time slices into one grid.

Args:
    Path/Guid: Asset to capture. Supports GameObject/prefab/model assets, Mesh, Material, Texture2D, and Sprite.
    Paths/Guids: Explicit asset list for CaptureGrid.
    Folder/Folders/Query/SearchPattern/Types/MaxResults: Search inputs for CaptureGrid.
    Width/Height: PNG size, clamped to 64..4096.
    CellWidth/CellHeight/Columns/IncludeLabels: CaptureGrid layout controls.
    View: Auto, Iso, Front, Back, Left, Right, Top, or Bottom.
    Views/SeriesCount/SeriesIntervalSeconds: CaptureContactSheet multi-view and time-slice controls.
    Orthographic: Defaults to true for stable AI-readable previews.
    TransparentBackground: Defaults to true.
    AdvanceMs/SimulateParticles/SampleAnimations: Best-effort animated prefab/VFX preview advance before rendering.
    ReadbackMode: Immediate or GpuReadback for prefab/model RenderTexture readback.
    OutputDirectory/FileName/Tag: Optional output controls.

Returns:
    success, message, and data containing the PNG path, file URI, dimensions, source asset metadata, preview kind, bounds/camera metadata for single captures, or contact sheet metadata and index mapping for CaptureGrid/CaptureContactSheet.";

        [McpTool("UniBridge_CaptureAsset", Description, Title, Groups = new[] { "core", "assets", "visual" }, EnabledByDefault = true)]
        public static object HandleCommand(CaptureAssetParams parameters)
        {
            parameters ??= new CaptureAssetParams();

            try
            {
                return parameters.Action switch
                {
                    CaptureAssetAction.Capture => Capture(parameters),
                    CaptureAssetAction.CaptureGrid => CaptureGrid(parameters),
                    CaptureAssetAction.CaptureContactSheet => CaptureContactSheet(parameters),
                    _ => Response.Error($"Unsupported asset capture action '{parameters.Action}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CaptureAsset] {parameters.Action} failed for '{parameters.Path ?? parameters.Guid ?? parameters.Query ?? parameters.SearchPattern}': {ex}");
                return Response.Error($"Asset capture failed: {ex.Message}");
            }
        }

        static object Capture(CaptureAssetParams parameters)
        {
            var resolved = ResolveAsset(parameters);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            var width = Mathf.Clamp(parameters.Width ?? 1024, MinSize, MaxSize);
            var height = Mathf.Clamp(parameters.Height ?? 1024, MinSize, MaxSize);
            var padding = Mathf.Clamp(parameters.Padding ?? 1.4f, 1f, 3f);
            var transparent = parameters.TransparentBackground ?? true;
            var background = transparent
                ? new Color(0f, 0f, 0f, 0f)
                : ReadColor(parameters.BackgroundColor, new Color(0.035f, 0.04f, 0.055f, 1f));

            if (TryResolveFlat2DSource(resolved.AssetPath, resolved.Asset, parameters.View, out var flatSource))
                return CaptureFlat2D(parameters, resolved, flatSource, width, height, padding, transparent, background);

            var previewScene = EditorSceneManager.NewPreviewScene();
            var tempObjects = new List<Object>();
            RenderTexture renderTexture = null;
            Texture2D readable = null;
            var previousActive = RenderTexture.active;

            try
            {
                var preview = BuildPreviewObject(resolved.AssetPath, resolved.Asset, tempObjects);
                if (preview.Root == null)
                    return Response.Error($"Asset '{resolved.AssetPath}' of type '{resolved.Asset.GetType().FullName}' cannot be rendered by UniBridge_CaptureAsset yet.");

                SceneManager.MoveGameObjectToScene(preview.Root, previewScene);

                var cameraObject = new GameObject("UniBridge_AssetCapture_Camera");
                SceneManager.MoveGameObjectToScene(cameraObject, previewScene);
                var camera = cameraObject.AddComponent<Camera>();
                tempObjects.Add(cameraObject);

                var lightObject = new GameObject("UniBridge_AssetCapture_KeyLight");
                SceneManager.MoveGameObjectToScene(lightObject, previewScene);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.15f;
                light.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
                tempObjects.Add(lightObject);

                ConfigureCanvases(preview.Root, camera);

                using var advance = CaptureObjectAdvancer.Advance(
                    new[] { preview.Root },
                    parameters.AdvanceMs,
                    parameters.SimulateParticles,
                    parameters.SampleAnimations);

                var bounds = CalculateBounds(preview.Root);
                var offset = -bounds.center;
                preview.Root.transform.position += offset;
                bounds = CalculateBounds(preview.Root);

                var view = ResolveView(parameters.View, preview.Kind);
                var direction = GetViewDirection(view);
                ConfigureCamera(camera, bounds, direction, width, height, padding, parameters.Orthographic ?? true, background, transparent);

                renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();

                readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                var readbackMode = ReadRenderTexture(renderTexture, readable, width, height, parameters.ReadbackMode);

                var png = readable.EncodeToPNG();
                if (png == null || png.Length == 0)
                    return Response.Error("Asset capture produced an empty PNG.");

                var outputPath = ResolveOutputPath(parameters, resolved.AssetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.WriteAllBytes(outputPath, png);

                if (parameters.Select)
                    Selection.activeObject = resolved.Asset;

                return Response.Success($"Captured asset '{resolved.AssetPath}' to '{outputPath}'.", new
                {
                    path = NormalizePath(outputPath),
                    fileUri = new Uri(outputPath).AbsoluteUri,
                    width,
                    height,
                    bytes = png.Length,
                    format = "png",
                    asset = new
                    {
                        path = resolved.AssetPath,
                        guid = AssetDatabase.AssetPathToGUID(resolved.AssetPath),
                        name = resolved.Asset.name,
                        type = resolved.Asset.GetType().FullName,
                        kind = preview.Kind.ToString()
                    },
                    view = view.ToString(),
                    advance = advance.Info,
                    readbackMode,
                    orthographic = camera.orthographic,
                    camera = new
                    {
                        position = SerializeVector3(camera.transform.position),
                        rotationEuler = SerializeVector3(camera.transform.eulerAngles),
                        camera.orthographicSize,
                        camera.fieldOfView,
                        camera.nearClipPlane,
                        camera.farClipPlane
                    },
                    bounds = BuildBoundsInfo(bounds),
                    transparentBackground = transparent
                });
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (renderTexture != null)
                    RenderTexture.ReleaseTemporary(renderTexture);
                if (readable != null)
                    Object.DestroyImmediate(readable);

                foreach (var obj in tempObjects.Where(obj => obj != null))
                    Object.DestroyImmediate(obj);

                if (previewScene.IsValid())
                    EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        static string ReadRenderTexture(RenderTexture renderTexture, Texture2D texture, int width, int height, CaptureReadbackMode mode)
        {
            if (mode == CaptureReadbackMode.GpuReadback && SystemInfo.supportsAsyncGPUReadback)
            {
                try
                {
                    var request = AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGBA32);
                    request.WaitForCompletion();
                    if (!request.hasError)
                    {
                        texture.LoadRawTextureData(request.GetData<byte>());
                        texture.Apply(false, false);
                        return "GpuReadback";
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CaptureAsset] GPU readback failed, falling back to ReadPixels: {ex.Message}");
                }
            }

            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();
            return mode == CaptureReadbackMode.GpuReadback ? "ReadPixelsFallback" : "Immediate";
        }

        static object CaptureGrid(CaptureAssetParams parameters)
        {
            var selection = ResolveGridAssets(parameters);
            if (!selection.Success)
                return Response.Error(selection.Error);

            if (selection.Assets.Count == 0)
                return Response.Error("CaptureGrid found no candidate assets.");

            var cellWidth = Mathf.Clamp(parameters.CellWidth ?? 256, MinGridCellSize, MaxGridCellSize);
            var cellHeight = Mathf.Clamp(parameters.CellHeight ?? 256, MinGridCellSize, MaxGridCellSize);
            var padding = Mathf.Clamp(parameters.Padding ?? 1.4f, 1f, 3f);
            var transparent = parameters.TransparentBackground ?? true;
            var previewBackground = transparent
                ? new Color(0f, 0f, 0f, 0f)
                : ReadColor(parameters.BackgroundColor, new Color(0.035f, 0.04f, 0.055f, 1f));
            var includeLabels = parameters.IncludeLabels ?? true;
            var sheetBackground = includeLabels && transparent
                ? new Color(0.025f, 0.028f, 0.035f, 1f)
                : previewBackground;

            var renderedItems = new List<GridRenderedItem>();
            var skipped = new List<object>();

            try
            {
                for (var i = 0; i < selection.Assets.Count; i++)
                {
                    var asset = selection.Assets[i];
                    if (!TryRenderAssetPreviewTexture(
                            asset,
                            cellWidth,
                            cellHeight,
                            padding,
                            transparent,
                            previewBackground,
                            parameters.View,
                            parameters.Orthographic ?? true,
                            parameters.AdvanceMs,
                            parameters.SimulateParticles,
                            parameters.SampleAnimations,
                            out var rendered,
                            out var error))
                    {
                        skipped.Add(new
                        {
                            index = i,
                            path = asset.AssetPath,
                            name = asset.Asset?.name,
                            type = asset.Asset?.GetType().FullName,
                            reason = error
                        });
                        continue;
                    }

                    renderedItems.Add(new GridRenderedItem(renderedItems.Count + 1, asset, rendered));
                }

                if (renderedItems.Count == 0)
                {
                    return Response.Error("CaptureGrid could not render any of the selected assets.", new
                    {
                        selected = selection.Assets.Count,
                        skipped
                    });
                }

                var columns = parameters.Columns.HasValue && parameters.Columns.Value > 0
                    ? Mathf.Clamp(parameters.Columns.Value, 1, renderedItems.Count)
                    : Mathf.CeilToInt(Mathf.Sqrt(renderedItems.Count));
                var rows = Mathf.CeilToInt(renderedItems.Count / (float)columns);
                var gutter = Mathf.Clamp(cellWidth / 32, 8, 32);

                var sheet = BuildContactSheet(renderedItems, cellWidth, cellHeight, columns, rows, gutter, includeLabels, sheetBackground);
                try
                {
                    var png = sheet.EncodeToPNG();
                    if (png == null || png.Length == 0)
                        return Response.Error("CaptureGrid produced an empty PNG.");

                    var outputPath = ResolveGridOutputPath(parameters);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                    File.WriteAllBytes(outputPath, png);

                    if (parameters.Select && renderedItems.Count > 0)
                        Selection.activeObject = renderedItems[0].Asset.Asset;

                    return Response.Success($"Captured {renderedItems.Count} assets to contact sheet '{outputPath}'.", new
                    {
                        path = NormalizePath(outputPath),
                        fileUri = new Uri(outputPath).AbsoluteUri,
                        width = sheet.width,
                        height = sheet.height,
                        bytes = png.Length,
                        format = "png",
                        renderMode = "ContactSheet",
                        cellWidth,
                        cellHeight,
                        columns,
                        rows,
                        gutter,
                        includeLabels,
                        selected = selection.Assets.Count,
                        rendered = renderedItems.Count,
                        skippedCount = skipped.Count,
                        search = selection.SearchInfo,
                        warnings = selection.Warnings,
                        skipped,
                        items = renderedItems.Select(item => new
                        {
                            index = item.Index,
                            path = item.Asset.AssetPath,
                            guid = AssetDatabase.AssetPathToGUID(item.Asset.AssetPath),
                            name = item.Asset.Asset.name,
                            type = item.Asset.Asset.GetType().FullName,
                            kind = item.Rendered.Kind.ToString(),
                            renderMode = item.Rendered.RenderMode
                        }).ToArray()
                    });
                }
                finally
                {
                    Object.DestroyImmediate(sheet);
                }
            }
            finally
            {
                foreach (var item in renderedItems)
                {
                    if (item.Rendered.Texture != null)
                        Object.DestroyImmediate(item.Rendered.Texture);
                }
            }
        }

        static object CaptureContactSheet(CaptureAssetParams parameters)
        {
            var resolved = ResolveAsset(parameters);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            if (!IsRenderableAsset(resolved.AssetPath, resolved.Asset))
                return Response.Error($"Asset '{resolved.AssetPath}' of type '{resolved.Asset.GetType().FullName}' is not renderable by CaptureContactSheet.");

            var views = ResolveAssetContactSheetViews(parameters, resolved.AssetPath, resolved.Asset).ToArray();
            if (views.Length == 0)
                return Response.Error("CaptureContactSheet requires at least one valid asset preview view.");

            var requestedFramesPerView = Mathf.Clamp(parameters.SeriesCount ?? 1, 1, MaxContactSheetSeriesCount);
            var maxFramesPerView = Mathf.Max(1, MaxContactSheetCells / views.Length);
            var framesPerView = Mathf.Min(requestedFramesPerView, maxFramesPerView);
            var totalCells = views.Length * framesPerView;
            var cellWidth = Mathf.Clamp(parameters.CellWidth ?? parameters.Width ?? 256, MinGridCellSize, MaxGridCellSize);
            var cellHeight = Mathf.Clamp(parameters.CellHeight ?? parameters.Height ?? 256, MinGridCellSize, MaxGridCellSize);
            var columns = parameters.Columns.HasValue && parameters.Columns.Value > 0
                ? Mathf.Clamp(parameters.Columns.Value, 1, totalCells)
                : Mathf.CeilToInt(Mathf.Sqrt(totalCells));
            var rows = Mathf.CeilToInt(totalCells / (float)columns);
            var gutter = Mathf.Clamp(cellWidth / 32, 8, 32);
            var sheetWidth = cellWidth * columns + gutter * (columns - 1);
            var sheetHeight = cellHeight * rows + gutter * (rows - 1);
            if (sheetWidth > MaxContactSheetDimension || sheetHeight > MaxContactSheetDimension)
            {
                return Response.Error(
                    $"CaptureContactSheet output would be too large ({sheetWidth}x{sheetHeight}). Reduce CellWidth, CellHeight, Views, SeriesCount, or Columns.",
                    new
                    {
                        requestedCellWidth = cellWidth,
                        requestedCellHeight = cellHeight,
                        views = views.Select(view => view.ToString()).ToArray(),
                        requestedFramesPerView,
                        framesPerView,
                        columns,
                        rows,
                        maxContactSheetDimension = MaxContactSheetDimension
                    });
            }

            var padding = Mathf.Clamp(parameters.Padding ?? 1.4f, 1f, 3f);
            var transparent = parameters.TransparentBackground ?? true;
            var previewBackground = transparent
                ? new Color(0f, 0f, 0f, 0f)
                : ReadColor(parameters.BackgroundColor, new Color(0.035f, 0.04f, 0.055f, 1f));
            var includeLabels = parameters.IncludeLabels ?? true;
            var sheetBackground = includeLabels && transparent
                ? new Color(0.025f, 0.028f, 0.035f, 1f)
                : previewBackground;
            var intervalSeconds = Mathf.Clamp(parameters.SeriesIntervalSeconds ?? 0f, 0f, 10f);

            var renderedItems = new List<GridRenderedItem>();
            var skipped = new List<object>();
            var items = new List<object>();
            var cellIndex = 0;

            try
            {
                for (var viewIndex = 0; viewIndex < views.Length; viewIndex++)
                {
                    var view = views[viewIndex];
                    for (var frameIndex = 0; frameIndex < framesPerView; frameIndex++)
                    {
                        if (cellIndex > 0 && intervalSeconds > 0f)
                        {
                            EditorApplication.QueuePlayerLoopUpdate();
                            Thread.Sleep(Mathf.RoundToInt(intervalSeconds * 1000f));
                        }

                        if (!TryRenderAssetPreviewTexture(
                                resolved,
                                cellWidth,
                                cellHeight,
                                padding,
                                transparent,
                                previewBackground,
                                view,
                                parameters.Orthographic ?? true,
                                parameters.AdvanceMs,
                                parameters.SimulateParticles,
                                parameters.SampleAnimations,
                                out var rendered,
                                out var error))
                        {
                            skipped.Add(new
                            {
                                index = cellIndex + 1,
                                path = resolved.AssetPath,
                                name = resolved.Asset?.name,
                                type = resolved.Asset?.GetType().FullName,
                                requestedView = view.ToString(),
                                frame = frameIndex + 1,
                                reason = error
                            });
                            cellIndex++;
                            continue;
                        }

                        var index = renderedItems.Count + 1;
                        renderedItems.Add(new GridRenderedItem(index, resolved, rendered));
                        items.Add(new
                        {
                            index,
                            path = resolved.AssetPath,
                            guid = AssetDatabase.AssetPathToGUID(resolved.AssetPath),
                            name = resolved.Asset.name,
                            type = resolved.Asset.GetType().FullName,
                            requestedView = view.ToString(),
                            frame = frameIndex + 1,
                            kind = rendered.Kind.ToString(),
                            renderMode = rendered.RenderMode
                        });
                        cellIndex++;
                    }
                }

                if (renderedItems.Count == 0)
                {
                    return Response.Error("CaptureContactSheet could not render any cells.", new
                    {
                        asset = BuildAssetMetadata(resolved),
                        requestedViews = views.Select(view => view.ToString()).ToArray(),
                        requestedFramesPerView,
                        framesPerView,
                        skipped
                    });
                }

                var sheet = BuildContactSheet(renderedItems, cellWidth, cellHeight, columns, rows, gutter, includeLabels, sheetBackground);
                try
                {
                    var png = sheet.EncodeToPNG();
                    if (png == null || png.Length == 0)
                        return Response.Error("CaptureContactSheet produced an empty PNG.");

                    var outputPath = ResolveAssetContactSheetOutputPath(parameters, resolved.AssetPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                    File.WriteAllBytes(outputPath, png);

                    if (parameters.Select)
                        Selection.activeObject = resolved.Asset;

                    return Response.Success($"Captured asset '{resolved.AssetPath}' to contact sheet '{outputPath}'.", new
                    {
                        path = NormalizePath(outputPath),
                        fileUri = new Uri(outputPath).AbsoluteUri,
                        width = sheet.width,
                        height = sheet.height,
                        bytes = png.Length,
                        format = "png",
                        renderMode = "ContactSheet",
                        cellWidth,
                        cellHeight,
                        columns,
                        rows,
                        gutter,
                        includeLabels,
                        asset = BuildAssetMetadata(resolved),
                        requestedViews = views.Select(view => view.ToString()).ToArray(),
                        requestedFramesPerView,
                        framesPerView,
                        truncatedFramesPerView = framesPerView < requestedFramesPerView,
                        intervalSeconds,
                        rendered = renderedItems.Count,
                        skippedCount = skipped.Count,
                        maxContactSheetCells = MaxContactSheetCells,
                        items = items.ToArray(),
                        skipped
                    });
                }
                finally
                {
                    Object.DestroyImmediate(sheet);
                }
            }
            finally
            {
                foreach (var item in renderedItems)
                {
                    if (item.Rendered.Texture != null)
                        Object.DestroyImmediate(item.Rendered.Texture);
                }
            }
        }

        static PreviewObject BuildPreviewObject(string assetPath, Object asset, List<Object> tempObjects)
        {
            if (asset is GameObject)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                    instance = Object.Instantiate(prefab);

                instance.name = $"Preview_{prefab.name}";
                tempObjects.Add(instance);
                return new PreviewObject(instance, AssetPreviewKind.GameObject);
            }

            if (asset is Material material)
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = $"Preview_{material.name}";
                sphere.transform.localScale = Vector3.one * 3f;
                var renderer = sphere.GetComponent<Renderer>();
                renderer.sharedMaterial = material;
                tempObjects.Add(sphere);
                return new PreviewObject(sphere, AssetPreviewKind.Material);
            }

            if (asset is Mesh mesh)
            {
                var meshObject = new GameObject($"Preview_{mesh.name}", typeof(MeshFilter), typeof(MeshRenderer));
                meshObject.GetComponent<MeshFilter>().sharedMesh = mesh;
                meshObject.GetComponent<MeshRenderer>().sharedMaterial = CreateDefaultMaterial(tempObjects);
                tempObjects.Add(meshObject);
                return new PreviewObject(meshObject, AssetPreviewKind.Mesh);
            }

            if (asset is Sprite sprite)
            {
                var spriteObject = new GameObject($"Preview_{sprite.name}", typeof(SpriteRenderer));
                spriteObject.GetComponent<SpriteRenderer>().sprite = sprite;
                tempObjects.Add(spriteObject);
                return new PreviewObject(spriteObject, AssetPreviewKind.Sprite);
            }

            if (asset is Texture2D texture)
            {
                var textureSprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                if (textureSprite != null)
                {
                    var spriteObject = new GameObject($"Preview_{textureSprite.name}", typeof(SpriteRenderer));
                    spriteObject.GetComponent<SpriteRenderer>().sprite = textureSprite;
                    tempObjects.Add(spriteObject);
                    return new PreviewObject(spriteObject, AssetPreviewKind.Sprite);
                }

                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = $"Preview_{texture.name}";
                var renderer = quad.GetComponent<Renderer>();
                renderer.sharedMaterial = CreateTextureMaterial(texture, tempObjects);
                var aspect = texture.height <= 0 ? 1f : texture.width / (float)texture.height;
                quad.transform.localScale = new Vector3(Mathf.Max(0.01f, aspect), 1f, 1f);
                tempObjects.Add(quad);
                return new PreviewObject(quad, AssetPreviewKind.Texture);
            }

            return new PreviewObject(null, AssetPreviewKind.Unsupported);
        }

        static object CaptureFlat2D(
            CaptureAssetParams parameters,
            AssetResolution resolved,
            Flat2DSource source,
            int width,
            int height,
            float padding,
            bool transparent,
            Color background)
        {
            if (source.Texture == null)
                return Response.Error($"Asset '{resolved.AssetPath}' has no readable texture source for a flat 2D preview.");

            var pixelRect = ClampPixelRect(source.PixelRect, source.Texture.width, source.Texture.height);
            if (pixelRect.width <= 0f || pixelRect.height <= 0f)
                return Response.Error($"Asset '{resolved.AssetPath}' has an empty texture rectangle.");

            RenderTexture renderTexture = null;
            Texture2D readable = null;
            var previousActive = RenderTexture.active;

            try
            {
                renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                RenderTexture.active = renderTexture;
                GL.Clear(true, true, background);

                var target = CalculateFlatTargetRect(width, height, pixelRect.width, pixelRect.height, padding);
                var sourceRect = new Rect(
                    pixelRect.x / source.Texture.width,
                    pixelRect.y / source.Texture.height,
                    pixelRect.width / source.Texture.width,
                    pixelRect.height / source.Texture.height);

                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, width, height, 0f);
                Graphics.DrawTexture(target, source.Texture, sourceRect, 0, 0, 0, 0);
                GL.PopMatrix();

                readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply();

                var png = readable.EncodeToPNG();
                if (png == null || png.Length == 0)
                    return Response.Error("Flat 2D asset capture produced an empty PNG.");

                var outputPath = ResolveOutputPath(parameters, resolved.AssetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.WriteAllBytes(outputPath, png);

                if (parameters.Select)
                    Selection.activeObject = resolved.Asset;

                return Response.Success($"Captured 2D asset '{resolved.AssetPath}' to '{outputPath}'.", new
                {
                    path = NormalizePath(outputPath),
                    fileUri = new Uri(outputPath).AbsoluteUri,
                    width,
                    height,
                    bytes = png.Length,
                    format = "png",
                    renderMode = "Flat2D",
                    asset = new
                    {
                        path = resolved.AssetPath,
                        guid = AssetDatabase.AssetPathToGUID(resolved.AssetPath),
                        name = resolved.Asset.name,
                        type = resolved.Asset.GetType().FullName,
                        kind = source.Kind.ToString()
                    },
                    view = CaptureAssetView.Front.ToString(),
                    sourceRect = new
                    {
                        pixelRect.x,
                        pixelRect.y,
                        pixelRect.width,
                        pixelRect.height,
                        textureWidth = source.Texture.width,
                        textureHeight = source.Texture.height
                    },
                    targetRect = new
                    {
                        target.x,
                        target.y,
                        target.width,
                        target.height
                    },
                    transparentBackground = transparent
                });
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (renderTexture != null)
                    RenderTexture.ReleaseTemporary(renderTexture);
                if (readable != null)
                    Object.DestroyImmediate(readable);
            }
        }

        static bool TryRenderAssetPreviewTexture(
            AssetResolution resolved,
            int width,
            int height,
            float padding,
            bool transparent,
            Color background,
            CaptureAssetView requestedView,
            bool orthographic,
            int? advanceMs,
            bool? simulateParticles,
            bool? sampleAnimations,
            out RenderedAssetPreview rendered,
            out string error)
        {
            rendered = null;
            error = null;

            try
            {
                if (TryResolveFlat2DSource(resolved.AssetPath, resolved.Asset, requestedView, out var flatSource))
                {
                    var texture = RenderFlat2DTexture(flatSource, width, height, padding, background, out _, out _);
                    rendered = new RenderedAssetPreview(texture, flatSource.Kind, "Flat2D");
                    return true;
                }

                return TryRenderSceneAssetPreviewTexture(
                    resolved,
                    width,
                    height,
                    padding,
                    background,
                    requestedView,
                    orthographic,
                    advanceMs,
                    simulateParticles,
                    sampleAnimations,
                    out rendered,
                    out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (rendered?.Texture != null)
                    Object.DestroyImmediate(rendered.Texture);
                rendered = null;
                return false;
            }
        }

        static Texture2D RenderFlat2DTexture(
            Flat2DSource source,
            int width,
            int height,
            float padding,
            Color background,
            out Rect pixelRect,
            out Rect target)
        {
            if (source.Texture == null)
                throw new InvalidOperationException("Flat 2D source has no texture.");

            pixelRect = ClampPixelRect(source.PixelRect, source.Texture.width, source.Texture.height);
            if (pixelRect.width <= 0f || pixelRect.height <= 0f)
                throw new InvalidOperationException("Flat 2D source texture rectangle is empty.");

            RenderTexture renderTexture = null;
            var previousActive = RenderTexture.active;

            try
            {
                renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                RenderTexture.active = renderTexture;
                GL.Clear(true, true, background);

                target = CalculateFlatTargetRect(width, height, pixelRect.width, pixelRect.height, padding);
                var sourceRect = new Rect(
                    pixelRect.x / source.Texture.width,
                    pixelRect.y / source.Texture.height,
                    pixelRect.width / source.Texture.width,
                    pixelRect.height / source.Texture.height);

                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, width, height, 0f);
                Graphics.DrawTexture(target, source.Texture, sourceRect, 0, 0, 0, 0);
                GL.PopMatrix();

                var readable = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply();
                return readable;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (renderTexture != null)
                    RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        static bool TryRenderSceneAssetPreviewTexture(
            AssetResolution resolved,
            int width,
            int height,
            float padding,
            Color background,
            CaptureAssetView requestedView,
            bool orthographic,
            int? advanceMs,
            bool? simulateParticles,
            bool? sampleAnimations,
            out RenderedAssetPreview rendered,
            out string error)
        {
            rendered = null;
            error = null;

            var previewScene = EditorSceneManager.NewPreviewScene();
            var tempObjects = new List<Object>();
            RenderTexture renderTexture = null;
            Texture2D readable = null;
            var previousActive = RenderTexture.active;

            try
            {
                var preview = BuildPreviewObject(resolved.AssetPath, resolved.Asset, tempObjects);
                if (preview.Root == null)
                {
                    error = $"Asset '{resolved.AssetPath}' of type '{resolved.Asset.GetType().FullName}' cannot be rendered.";
                    return false;
                }

                SceneManager.MoveGameObjectToScene(preview.Root, previewScene);

                var cameraObject = new GameObject("UniBridge_AssetGrid_Camera");
                SceneManager.MoveGameObjectToScene(cameraObject, previewScene);
                var camera = cameraObject.AddComponent<Camera>();
                tempObjects.Add(cameraObject);

                var lightObject = new GameObject("UniBridge_AssetGrid_KeyLight");
                SceneManager.MoveGameObjectToScene(lightObject, previewScene);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.15f;
                light.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
                tempObjects.Add(lightObject);

                ConfigureCanvases(preview.Root, camera);

                using var advance = CaptureObjectAdvancer.Advance(
                    new[] { preview.Root },
                    advanceMs,
                    simulateParticles,
                    sampleAnimations);

                var bounds = CalculateBounds(preview.Root);
                preview.Root.transform.position += -bounds.center;
                bounds = CalculateBounds(preview.Root);

                var view = ResolveView(requestedView, preview.Kind);
                ConfigureCamera(camera, bounds, GetViewDirection(view), width, height, padding, orthographic, background, background.a < 1f);

                renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();

                readable = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply();

                rendered = new RenderedAssetPreview(readable, preview.Kind, "SceneCamera");
                readable = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (renderTexture != null)
                    RenderTexture.ReleaseTemporary(renderTexture);
                if (readable != null)
                    Object.DestroyImmediate(readable);

                foreach (var obj in tempObjects.Where(obj => obj != null))
                    Object.DestroyImmediate(obj);

                if (previewScene.IsValid())
                    EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        static Texture2D BuildContactSheet(
            List<GridRenderedItem> items,
            int cellWidth,
            int cellHeight,
            int columns,
            int rows,
            int gutter,
            bool includeLabels,
            Color background)
        {
            var width = cellWidth * columns + gutter * (columns - 1);
            var height = cellHeight * rows + gutter * (rows - 1);
            var pixels = Enumerable.Repeat((Color32)background, width * height).ToArray();
            var digits = Mathf.Max(2, items.Count.ToString().Length);

            for (var index = 0; index < items.Count; index++)
            {
                var column = index % columns;
                var row = index / columns;
                var x = column * (cellWidth + gutter);
                var y = (rows - 1 - row) * (cellHeight + gutter);

                AlphaBlit(pixels, width, height, items[index].Rendered.Texture, x, y);

                if (includeLabels)
                {
                    var label = items[index].Index.ToString($"D{digits}");
                    DrawIndexBadge(pixels, width, height, x + 8, y + cellHeight - 30, label);
                }
            }

            var sheet = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            sheet.SetPixels32(pixels);
            sheet.Apply();
            return sheet;
        }

        static void AlphaBlit(Color32[] destination, int destinationWidth, int destinationHeight, Texture2D source, int offsetX, int offsetY)
        {
            if (source == null)
                return;

            var sourcePixels = source.GetPixels32();
            var width = Mathf.Min(source.width, destinationWidth - offsetX);
            var height = Mathf.Min(source.height, destinationHeight - offsetY);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var sourcePixel = sourcePixels[y * source.width + x];
                    var destinationIndex = (offsetY + y) * destinationWidth + offsetX + x;
                    destination[destinationIndex] = AlphaBlend(sourcePixel, destination[destinationIndex]);
                }
            }
        }

        static Color32 AlphaBlend(Color32 source, Color32 destination)
        {
            var sourceAlpha = source.a / 255f;
            var destinationAlpha = destination.a / 255f;
            var outputAlpha = sourceAlpha + destinationAlpha * (1f - sourceAlpha);
            if (outputAlpha <= 0.0001f)
                return new Color32(0, 0, 0, 0);

            byte Blend(byte sourceChannel, byte destinationChannel)
            {
                var value = (sourceChannel * sourceAlpha + destinationChannel * destinationAlpha * (1f - sourceAlpha)) / outputAlpha;
                return (byte)Mathf.Clamp(Mathf.RoundToInt(value), 0, 255);
            }

            return new Color32(
                Blend(source.r, destination.r),
                Blend(source.g, destination.g),
                Blend(source.b, destination.b),
                (byte)Mathf.Clamp(Mathf.RoundToInt(outputAlpha * 255f), 0, 255));
        }

        static void DrawIndexBadge(Color32[] pixels, int width, int height, int x, int y, string label)
        {
            const int scale = 3;
            const int badgeHeight = 22;
            var badgeWidth = Mathf.Max(28, label.Length * 12 + 10);
            FillRect(pixels, width, height, x, y, badgeWidth, badgeHeight, new Color32(0, 0, 0, 190));

            var cursorX = x + 6;
            var cursorY = y + 4;
            foreach (var ch in label)
            {
                DrawDigit(pixels, width, height, cursorX, cursorY, ch, scale, new Color32(255, 255, 255, 255));
                cursorX += 4 * scale;
            }
        }

        static void DrawDigit(Color32[] pixels, int width, int height, int x, int y, char digit, int scale, Color32 color)
        {
            var pattern = digit switch
            {
                '0' => new[] { "111", "101", "101", "101", "111" },
                '1' => new[] { "010", "110", "010", "010", "111" },
                '2' => new[] { "111", "001", "111", "100", "111" },
                '3' => new[] { "111", "001", "111", "001", "111" },
                '4' => new[] { "101", "101", "111", "001", "001" },
                '5' => new[] { "111", "100", "111", "001", "111" },
                '6' => new[] { "111", "100", "111", "101", "111" },
                '7' => new[] { "111", "001", "010", "010", "010" },
                '8' => new[] { "111", "101", "111", "101", "111" },
                '9' => new[] { "111", "101", "111", "001", "111" },
                _ => new[] { "000", "000", "000", "000", "000" }
            };

            for (var row = 0; row < pattern.Length; row++)
            {
                for (var column = 0; column < pattern[row].Length; column++)
                {
                    if (pattern[row][column] != '1')
                        continue;

                    FillRect(
                        pixels,
                        width,
                        height,
                        x + column * scale,
                        y + (pattern.Length - 1 - row) * scale,
                        scale,
                        scale,
                        color);
                }
            }
        }

        static void FillRect(Color32[] pixels, int width, int height, int x, int y, int rectWidth, int rectHeight, Color32 color)
        {
            var xMin = Mathf.Clamp(x, 0, width);
            var yMin = Mathf.Clamp(y, 0, height);
            var xMax = Mathf.Clamp(x + rectWidth, 0, width);
            var yMax = Mathf.Clamp(y + rectHeight, 0, height);
            for (var yy = yMin; yy < yMax; yy++)
            {
                for (var xx = xMin; xx < xMax; xx++)
                {
                    pixels[yy * width + xx] = AlphaBlend(color, pixels[yy * width + xx]);
                }
            }
        }

        static bool TryResolveFlat2DSource(string assetPath, Object asset, CaptureAssetView view, out Flat2DSource source)
        {
            source = default;

            if (view != CaptureAssetView.Auto && view != CaptureAssetView.Front)
                return false;

            if (asset is Sprite sprite && sprite.texture != null)
            {
                source = new Flat2DSource(sprite.texture, GetSpritePixelRect(sprite), AssetPreviewKind.Sprite);
                return true;
            }

            if (asset is Texture2D texture)
            {
                var textureSprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                if (textureSprite != null && textureSprite.texture != null)
                {
                    source = new Flat2DSource(textureSprite.texture, GetSpritePixelRect(textureSprite), AssetPreviewKind.Sprite);
                    return true;
                }

                source = new Flat2DSource(texture, new Rect(0f, 0f, texture.width, texture.height), AssetPreviewKind.Texture);
                return true;
            }

            return false;
        }

        static Rect GetSpritePixelRect(Sprite sprite)
        {
            try
            {
                return sprite.textureRect;
            }
            catch
            {
                return sprite.rect;
            }
        }

        static Rect ClampPixelRect(Rect rect, int textureWidth, int textureHeight)
        {
            var xMin = Mathf.Clamp(rect.xMin, 0f, textureWidth);
            var yMin = Mathf.Clamp(rect.yMin, 0f, textureHeight);
            var xMax = Mathf.Clamp(rect.xMax, xMin, textureWidth);
            var yMax = Mathf.Clamp(rect.yMax, yMin, textureHeight);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        static Rect CalculateFlatTargetRect(int width, int height, float sourceWidth, float sourceHeight, float padding)
        {
            var safePadding = Mathf.Max(1f, padding);
            var innerWidth = width / safePadding;
            var innerHeight = height / safePadding;
            var scale = Mathf.Min(innerWidth / Mathf.Max(1f, sourceWidth), innerHeight / Mathf.Max(1f, sourceHeight));
            var drawWidth = sourceWidth * scale;
            var drawHeight = sourceHeight * scale;
            return new Rect((width - drawWidth) * 0.5f, (height - drawHeight) * 0.5f, drawWidth, drawHeight);
        }

        static Material CreateDefaultMaterial(List<Object> tempObjects)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("HDRP/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Unlit/Color");
            var material = new Material(shader);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", new Color(0.78f, 0.82f, 0.9f, 1f));
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", new Color(0.78f, 0.82f, 0.9f, 1f));
            tempObjects.Add(material);
            return material;
        }

        static Material CreateTextureMaterial(Texture2D texture, List<Object> tempObjects)
        {
            var shader = Shader.Find("Unlit/Transparent")
                ?? Shader.Find("Unlit/Texture")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Standard");
            var material = new Material(shader);
            material.mainTexture = texture;
            if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", texture);
            tempObjects.Add(material);
            return material;
        }

        static void ConfigureCanvases(GameObject root, Camera camera)
        {
            foreach (var canvas in root.GetComponentsInChildren<Canvas>(true))
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 5f;
            }
        }

        static void ConfigureCamera(Camera camera, Bounds bounds, Vector3 direction, int width, int height, float padding, bool orthographic, Color background, bool transparent)
        {
            if (bounds.size == Vector3.zero)
                bounds.Expand(Vector3.one);

            var center = bounds.center;
            var maxExtent = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z, 0.5f);
            var distance = Mathf.Max(maxExtent * 4f, 5f);
            camera.transform.position = center + direction.normalized * distance;
            camera.transform.rotation = LookAt(center, camera.transform.position, direction);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            camera.allowHDR = false;
            camera.allowMSAA = true;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = Mathf.Max(100f, distance + maxExtent * 6f);
            camera.orthographic = orthographic;

            if (orthographic)
            {
                camera.orthographicSize = Mathf.Max(0.05f, ComputeOrthographicSize(bounds, camera, width / (float)height) * padding);
            }
            else
            {
                camera.fieldOfView = 35f;
                var radius = bounds.extents.magnitude * padding;
                var fovRad = camera.fieldOfView * Mathf.Deg2Rad;
                var perspectiveDistance = radius / Mathf.Sin(fovRad * 0.5f);
                camera.transform.position = center + direction.normalized * Mathf.Max(distance, perspectiveDistance);
                camera.transform.rotation = LookAt(center, camera.transform.position, direction);
                camera.farClipPlane = Mathf.Max(100f, perspectiveDistance + radius * 4f);
            }
        }

        static Quaternion LookAt(Vector3 center, Vector3 position, Vector3 direction)
        {
            var forward = center - position;
            var up = Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > 0.96f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(forward.normalized, up);
        }

        static float ComputeOrthographicSize(Bounds bounds, Camera camera, float aspect)
        {
            var corners = GetBoundsCorners(bounds);
            var worldToCamera = camera.transform.worldToLocalMatrix;
            var maxX = 0f;
            var maxY = 0f;
            foreach (var corner in corners)
            {
                var local = worldToCamera.MultiplyPoint(corner);
                maxX = Mathf.Max(maxX, Mathf.Abs(local.x));
                maxY = Mathf.Max(maxY, Mathf.Abs(local.y));
            }

            return Mathf.Max(maxY, maxX / Mathf.Max(0.01f, aspect), 0.25f);
        }

        static Bounds CalculateBounds(GameObject root)
        {
            var hasBounds = false;
            var bounds = new Bounds(root.transform.position, Vector3.zero);

            void Encapsulate(Bounds candidate)
            {
                if (!hasBounds)
                {
                    bounds = candidate;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(candidate);
                }
            }

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                Encapsulate(renderer.bounds);
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                Encapsulate(collider.bounds);
            foreach (var collider in root.GetComponentsInChildren<Collider2D>(true))
                Encapsulate(collider.bounds);
            foreach (var rectTransform in root.GetComponentsInChildren<RectTransform>(true))
            {
                var corners = new Vector3[4];
                rectTransform.GetWorldCorners(corners);
                foreach (var corner in corners)
                    Encapsulate(new Bounds(corner, Vector3.zero));
            }

            return hasBounds ? bounds : new Bounds(root.transform.position, Vector3.one);
        }

        static Vector3[] GetBoundsCorners(Bounds bounds)
        {
            var min = bounds.min;
            var max = bounds.max;
            return new[]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z)
            };
        }

        static CaptureAssetView ResolveView(CaptureAssetView requested, AssetPreviewKind kind)
        {
            if (requested != CaptureAssetView.Auto)
                return requested;

            return kind is AssetPreviewKind.Sprite or AssetPreviewKind.Texture
                ? CaptureAssetView.Front
                : CaptureAssetView.Iso;
        }

        static IEnumerable<CaptureAssetView> ResolveAssetContactSheetViews(CaptureAssetParams parameters, string assetPath, Object asset)
        {
            var result = new List<CaptureAssetView>();
            if (parameters.Views != null && parameters.Views.Length > 0)
            {
                foreach (var view in parameters.Views)
                {
                    if (!result.Contains(view))
                        result.Add(view);
                }
            }

            if (result.Count > 0)
                return result;

            if (parameters.View != CaptureAssetView.Auto)
                return new[] { parameters.View };

            return IsFlat2DAsset(assetPath, asset)
                ? new[] { CaptureAssetView.Front }
                : Default3DContactSheetViews;
        }

        static bool IsFlat2DAsset(string assetPath, Object asset)
        {
            return asset is Sprite ||
                   asset is Texture2D ||
                   AssetDatabase.LoadAssetAtPath<Sprite>(assetPath) != null;
        }

        static Vector3 GetViewDirection(CaptureAssetView view)
        {
            return view switch
            {
                CaptureAssetView.Front => new Vector3(0f, 0f, -1f),
                CaptureAssetView.Back => new Vector3(0f, 0f, 1f),
                CaptureAssetView.Left => new Vector3(-1f, 0f, 0f),
                CaptureAssetView.Right => new Vector3(1f, 0f, 0f),
                CaptureAssetView.Top => new Vector3(0f, 1f, 0f),
                CaptureAssetView.Bottom => new Vector3(0f, -1f, 0f),
                _ => new Vector3(1f, 0.75f, -1f)
            };
        }

        static GridAssetSelection ResolveGridAssets(CaptureAssetParams parameters)
        {
            var maxResults = Mathf.Clamp(parameters.MaxResults ?? 24, 1, MaxGridResults);
            var warnings = new List<string>();
            var requestedPaths = new List<string>();
            var searchFolders = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRequestedPath(string rawPath)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                    return;

                if (!TryNormalizeAssetPath(rawPath, out var normalized, out var error))
                {
                    warnings.Add(error);
                    return;
                }

                if (AssetDatabase.IsValidFolder(normalized))
                {
                    searchFolders.Add(normalized);
                    return;
                }

                if (seen.Add(normalized))
                    requestedPaths.Add(normalized);
            }

            void AddFolder(string rawPath)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                    return;

                if (!TryNormalizeAssetPath(rawPath, out var normalized, out var error))
                {
                    warnings.Add(error);
                    return;
                }

                if (!AssetDatabase.IsValidFolder(normalized))
                {
                    warnings.Add($"Folder '{normalized}' was not found.");
                    return;
                }

                if (!searchFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    searchFolders.Add(normalized);
            }

            void AddGuid(string guid)
            {
                if (string.IsNullOrWhiteSpace(guid))
                    return;

                var path = AssetDatabase.GUIDToAssetPath(guid.Trim());
                if (string.IsNullOrWhiteSpace(path))
                {
                    warnings.Add($"Asset GUID '{guid}' was not found.");
                    return;
                }

                if (seen.Add(path))
                    requestedPaths.Add(path);
            }

            AddRequestedPath(parameters.Path);
            if (parameters.Paths != null)
            {
                foreach (var path in parameters.Paths)
                    AddRequestedPath(path);
            }

            AddGuid(parameters.Guid);
            if (parameters.Guids != null)
            {
                foreach (var guid in parameters.Guids)
                    AddGuid(guid);
            }

            AddFolder(parameters.Folder);
            if (parameters.Folders != null)
            {
                foreach (var folder in parameters.Folders)
                    AddFolder(folder);
            }

            var query = (parameters.Query ?? parameters.SearchPattern ?? string.Empty).Trim();
            var typeFilters = NormalizeTypeFilters(parameters.Types);
            var hasSearchInputs = !string.IsNullOrWhiteSpace(query) || searchFolders.Count > 0 || typeFilters.Count > 0;

            if (hasSearchInputs)
            {
                var scopes = searchFolders.Count > 0 ? searchFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : null;
                var filter = BuildAssetDatabaseSearchFilter(query, typeFilters);
                var guids = scopes == null
                    ? AssetDatabase.FindAssets(filter)
                    : AssetDatabase.FindAssets(filter, scopes);

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                        continue;

                    requestedPaths.Add(path);
                    if (requestedPaths.Count >= maxResults)
                        break;
                }
            }

            if (requestedPaths.Count == 0)
            {
                return GridAssetSelection.Fail("CaptureGrid requires Paths/Guids, a file Path/Guid, a Folder/Folders, Query/SearchPattern, or Types.");
            }

            var assets = new List<AssetResolution>();
            foreach (var path in requestedPaths)
            {
                if (assets.Count >= maxResults)
                    break;

                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null)
                {
                    warnings.Add($"Asset not found at '{path}'.");
                    continue;
                }

                if (!MatchesTypeFilters(path, asset, typeFilters))
                    continue;

                if (!IsRenderableAsset(path, asset))
                {
                    warnings.Add($"Asset '{path}' is '{asset.GetType().FullName}' and is not renderable by CaptureGrid.");
                    continue;
                }

                assets.Add(AssetResolution.Ok(path, asset));
            }

            return GridAssetSelection.Ok(assets, warnings, new
            {
                query,
                filter = BuildAssetDatabaseSearchFilter(query, typeFilters),
                folders = searchFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                types = typeFilters.ToArray(),
                requested = requestedPaths.Count,
                maxResults
            });
        }

        static List<string> NormalizeTypeFilters(string[] types)
        {
            var result = new List<string>();
            if (types == null)
                return result;

            foreach (var type in types)
            {
                var normalized = (type ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(normalized))
                    continue;
                if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    result.Add(normalized);
            }

            return result;
        }

        static string BuildAssetDatabaseSearchFilter(string query, List<string> typeFilters)
        {
            query = (query ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(query))
                return query;

            if (typeFilters.Count == 1)
            {
                var type = NormalizeTypeName(typeFilters[0]);
                if (type == "sprite" || type == "texture" || type == "texture2d")
                    return "t:Texture2D";
                if (type == "material")
                    return "t:Material";
                if (type == "mesh")
                    return "t:Mesh";
                if (type == "gameobject" || type == "prefab" || type == "model")
                    return "t:GameObject";
            }

            return string.Empty;
        }

        static bool MatchesTypeFilters(string path, Object asset, List<string> typeFilters)
        {
            if (typeFilters.Count == 0)
                return true;

            return typeFilters.Any(type => MatchesTypeFilter(path, asset, type));
        }

        static bool MatchesTypeFilter(string path, Object asset, string type)
        {
            switch (NormalizeTypeName(type))
            {
                case "sprite":
                    return asset is Sprite || AssetDatabase.LoadAssetAtPath<Sprite>(path) != null;
                case "texture":
                case "texture2d":
                    return asset is Texture2D;
                case "material":
                    return asset is Material;
                case "mesh":
                    return asset is Mesh;
                case "gameobject":
                case "prefab":
                case "model":
                    return asset is GameObject;
                default:
                    return string.Equals(asset.GetType().Name, type, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(asset.GetType().FullName, type, StringComparison.OrdinalIgnoreCase);
            }
        }

        static string NormalizeTypeName(string type)
        {
            return (type ?? string.Empty)
                .Trim()
                .Replace("UnityEngine.", string.Empty)
                .Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .ToLowerInvariant();
        }

        static bool IsRenderableAsset(string path, Object asset)
        {
            return asset is GameObject ||
                   asset is Mesh ||
                   asset is Material ||
                   asset is Texture2D ||
                   asset is Sprite ||
                   AssetDatabase.LoadAssetAtPath<Sprite>(path) != null;
        }

        static AssetResolution ResolveAsset(CaptureAssetParams parameters)
        {
            var path = parameters.Path;
            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(parameters.Guid))
            {
                path = AssetDatabase.GUIDToAssetPath(parameters.Guid);
                if (string.IsNullOrWhiteSpace(path))
                    return AssetResolution.Fail($"Asset GUID '{parameters.Guid}' was not found.");
            }

            if (!TryNormalizeAssetPath(path, out var assetPath, out var error))
                return AssetResolution.Fail(error);

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
                return AssetResolution.Fail($"Asset not found at '{assetPath}'.");

            return AssetResolution.Ok(assetPath, asset);
        }

        static bool TryNormalizeAssetPath(string path, out string normalizedPath, out string error)
        {
            normalizedPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Path or Guid is required.";
                return false;
            }

            var candidate = path.Replace('\\', '/').Trim();
            if (candidate.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                candidate = candidate.Substring("unity://path/".Length);

            if (candidate.Contains("../", StringComparison.Ordinal) ||
                candidate.Contains("/..", StringComparison.Ordinal) ||
                candidate.Contains(":", StringComparison.Ordinal) ||
                Path.IsPathRooted(candidate))
            {
                error = $"Asset path must not contain traversal or absolute roots: '{path}'.";
                return false;
            }

            if (!candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "Assets/" + candidate.TrimStart('/');
            }

            normalizedPath = candidate.TrimEnd('/');
            return true;
        }

        static string ResolveOutputPath(CaptureAssetParams parameters, string assetPath)
        {
            var directory = ResolveOutputDirectory(parameters.OutputDirectory);
            var fileName = string.IsNullOrWhiteSpace(parameters.FileName)
                ? BuildDefaultFileName("asset_capture", parameters.Tag, assetPath)
                : SanitizeFileName(parameters.FileName.Trim());

            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                fileName += ".png";

            return Path.GetFullPath(Path.Combine(directory, fileName));
        }

        static string ResolveGridOutputPath(CaptureAssetParams parameters)
        {
            var directory = ResolveOutputDirectory(parameters.OutputDirectory);
            var fileName = string.IsNullOrWhiteSpace(parameters.FileName)
                ? BuildDefaultFileName("asset_grid", parameters.Tag, "contact_sheet")
                : SanitizeFileName(parameters.FileName.Trim());

            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                fileName += ".png";

            return Path.GetFullPath(Path.Combine(directory, fileName));
        }

        static string ResolveAssetContactSheetOutputPath(CaptureAssetParams parameters, string assetPath)
        {
            var directory = ResolveOutputDirectory(parameters.OutputDirectory);
            var fileName = string.IsNullOrWhiteSpace(parameters.FileName)
                ? BuildDefaultFileName("asset_contact_sheet", parameters.Tag, assetPath)
                : SanitizeFileName(parameters.FileName.Trim());

            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                fileName += ".png";

            return Path.GetFullPath(Path.Combine(directory, fileName));
        }

        static string ResolveOutputDirectory(string requestedDirectory)
        {
            if (!string.IsNullOrWhiteSpace(requestedDirectory))
                return ExpandHomePath(requestedDirectory.Trim());

            var identity = ProjectIdentity.GetOrCreate();
            var projectId = string.IsNullOrEmpty(identity.ProjectId)
                ? "unknown"
                : identity.ProjectId.Substring(0, Math.Min(8, identity.ProjectId.Length));
            var projectFolder = $"{SanitizeFileName(identity.ProjectName)}_{projectId}";
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".unibridge",
                "asset-captures",
                projectFolder);
        }

        static string ExpandHomePath(string path)
        {
            if (path == "~")
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
            return path;
        }

        static string BuildDefaultFileName(string prefix, string tag, string assetPath)
        {
            var safePrefix = SanitizeFileName(prefix);
            var safeAsset = SanitizeFileName(Path.GetFileNameWithoutExtension(assetPath));
            var safeTag = SanitizeFileName(tag);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var middle = string.IsNullOrWhiteSpace(safeTag) ? safeAsset : $"{safeAsset}_{safeTag}";
            return $"{safePrefix}_{middle}_{timestamp}.png";
        }

        static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Trim()
                .Select(ch => invalid.Contains(ch) || ch == '/' || ch == '\\' || ch == ':' ? '_' : ch)
                .ToArray();
            var result = new string(chars);
            while (result.IndexOf("__", StringComparison.Ordinal) >= 0)
                result = result.Replace("__", "_");
            return result.Trim('_');
        }

        static Color ReadColor(float[] values, Color fallback)
        {
            if (values == null || values.Length < 3)
                return fallback;
            return new Color(values[0], values[1], values[2], values.Length > 3 ? values[3] : 1f);
        }

        static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/');
        }

        static object SerializeVector3(Vector3 value) => new { value.x, value.y, value.z };

        static object BuildBoundsInfo(Bounds bounds)
        {
            return new
            {
                center = SerializeVector3(bounds.center),
                size = SerializeVector3(bounds.size),
                min = SerializeVector3(bounds.min),
                max = SerializeVector3(bounds.max)
            };
        }

        static object BuildAssetMetadata(AssetResolution resolved)
        {
            return new
            {
                path = resolved.AssetPath,
                guid = AssetDatabase.AssetPathToGUID(resolved.AssetPath),
                name = resolved.Asset.name,
                type = resolved.Asset.GetType().FullName
            };
        }

        sealed class RenderedAssetPreview
        {
            public RenderedAssetPreview(Texture2D texture, AssetPreviewKind kind, string renderMode)
            {
                Texture = texture;
                Kind = kind;
                RenderMode = renderMode;
            }

            public Texture2D Texture { get; }
            public AssetPreviewKind Kind { get; }
            public string RenderMode { get; }
        }

        sealed class GridRenderedItem
        {
            public GridRenderedItem(int index, AssetResolution asset, RenderedAssetPreview rendered)
            {
                Index = index;
                Asset = asset;
                Rendered = rendered;
            }

            public int Index { get; }
            public AssetResolution Asset { get; }
            public RenderedAssetPreview Rendered { get; }
        }

        sealed class GridAssetSelection
        {
            public bool Success { get; private set; }
            public string Error { get; private set; }
            public List<AssetResolution> Assets { get; private set; }
            public List<string> Warnings { get; private set; }
            public object SearchInfo { get; private set; }

            public static GridAssetSelection Ok(List<AssetResolution> assets, List<string> warnings, object searchInfo) => new()
            {
                Success = true,
                Assets = assets,
                Warnings = warnings,
                SearchInfo = searchInfo
            };

            public static GridAssetSelection Fail(string error) => new()
            {
                Success = false,
                Error = error,
                Assets = new List<AssetResolution>(),
                Warnings = new List<string>()
            };
        }

        readonly struct PreviewObject
        {
            public PreviewObject(GameObject root, AssetPreviewKind kind)
            {
                Root = root;
                Kind = kind;
            }

            public GameObject Root { get; }
            public AssetPreviewKind Kind { get; }
        }

        readonly struct Flat2DSource
        {
            public Flat2DSource(Texture2D texture, Rect pixelRect, AssetPreviewKind kind)
            {
                Texture = texture;
                PixelRect = pixelRect;
                Kind = kind;
            }

            public Texture2D Texture { get; }
            public Rect PixelRect { get; }
            public AssetPreviewKind Kind { get; }
        }

        enum AssetPreviewKind
        {
            Unsupported,
            GameObject,
            Mesh,
            Material,
            Sprite,
            Texture
        }

        sealed class AssetResolution
        {
            public bool Success { get; private set; }
            public string Error { get; private set; }
            public string AssetPath { get; private set; }
            public Object Asset { get; private set; }

            public static AssetResolution Ok(string path, Object asset) => new()
            {
                Success = true,
                AssetPath = path,
                Asset = asset
            };

            public static AssetResolution Fail(string error) => new()
            {
                Success = false,
                Error = error
            };
        }
    }
}
