#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    // Temp-only independent production candidate. Native pixel/stage qualification is outstanding.
    internal static class Region2DCapture
    {
        internal sealed class ImageResult
        {
            public int tileIndex, width, height, byteCount;
            public string path, sha256, readbackMode;
        }
        internal sealed class ScopeResult
        {
            public string stage;
            public ulong sceneCullingMask;
            public object[] scenes;
            public string singleSceneIsolation = "not_claimed";
        }

        // Caller supplies the existing CaptureView output path policy; no alternate location rules.
        internal static object Execute(JObject raw, Func<string, string> resolveDirectory)
        {
            Region2DPlan plan = null;
            Region2DRequest request = null;
            ScopeResult scope = null;
            var images = new List<ImageResult>();
            var cleanupErrors = new List<string>();
            GameObject cameraObject = null;
            string[] outputPaths = null;
            string failure = null, failureCode = null;
            bool outputAttempted = false;
            try {
                request = Region2DPlanner.Parse(raw);
                plan = Region2DPlanner.Build(request, SystemInfo.maxTextureSize);
                scope = ResolveScope(request.stage);
                var paths = outputPaths = OutputPaths(request, plan, resolveDirectory);
                foreach (var path in paths) if (File.Exists(path) || Directory.Exists(path))
                    throw new Region2DException("OUTPUT_EXISTS", "Capture output already exists: " + path);
                if (request.preview)
                    return new { success = true, message = "Capture2DRegion plan; nothing rendered or written.",
                        data = new { status = "preview", captureKind = "Capture2DRegion", preview = true, written = false,
                            plan, scope, outputPaths = paths, nativePixelQualification = "not_run" } };

                // Hidden camera stays outside author/Prefab scenes; no preview scene is created or closed.
                // Closing a rendered preview scene while Prefab Mode is open dirties the author Main scene.
                cameraObject = EditorUtility.CreateGameObjectWithHideFlags(
                    "UniBridge Capture2DRegion Camera", HideFlags.HideAndDontSave, typeof(Camera));
                var camera = cameraObject.GetComponent<Camera>();
                camera.cameraType = CameraType.Preview;
                camera.enabled = false;
                camera.orthographic = true;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = request.transparentBackground ? new Color(0, 0, 0, 0) : Color.black;
                camera.allowHDR = false; camera.allowMSAA = false; camera.allowDynamicResolution = false;
                camera.depthTextureMode = DepthTextureMode.None;
                camera.cullingMask = -1;
                camera.overrideSceneCullingMask = scope.sceneCullingMask;
                camera.rect = new Rect(0, 0, 1, 1);
                camera.transform.rotation = Quaternion.identity;
                // Camera.scene deliberately unset: ordinary author scenes are not a supported value.
                foreach (var tile in plan.tiles) {
                    camera.transform.position = new Vector3(tile.camera.x, tile.camera.y, tile.camera.z);
                    camera.orthographicSize = tile.camera.orthographicSize;
                    camera.aspect = tile.camera.aspect;
                    camera.nearClipPlane = tile.camera.nearClipPlane;
                    camera.farClipPlane = tile.camera.farClipPlane;
                    camera.ResetProjectionMatrix();
                    string readback;
                    var png = RenderPng(camera, tile.width, tile.height, request.readbackMode, out readback);
                    // Validate before writing. Decoding is real Unity PNG decode, not only the IHDR header.
                    ValidatePng(png, tile.width, tile.height);
                    var path = paths[tile.index];
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
                        outputAttempted = true;
                        output.Write(png, 0, png.Length); output.Flush(true);
                        output.Position = 0;
                        var persisted = new byte[checked((int)output.Length)];
                        int offset = 0;
                        while (offset < persisted.Length) {
                            int read = output.Read(persisted, offset, persisted.Length - offset);
                            if (read == 0) throw new IOException("PNG readback ended before its expected length.");
                            offset += read;
                        }
                        if (!png.SequenceEqual(persisted)) throw new IOException("Written PNG bytes differ from encoded bytes.");
                        images.Add(new ImageResult { tileIndex = tile.index, path = path, width = tile.width,
                            height = tile.height, byteCount = png.Length, sha256 = Sha(persisted), readbackMode = readback });
                    }
                }
            }
            catch (Exception e) { failure = e.Message; failureCode = (e as Region2DException)?.Code ?? "CAPTURE_FAILED"; }
            finally {
                // Only the exact hidden camera is owned; author and existing Prefab scenes are never closed.
                try { if (cameraObject != null) Object.DestroyImmediate(cameraObject); } catch (Exception e) { cleanupErrors.Add("camera: " + e.Message); }
            }
            bool succeeded = failure == null && cleanupErrors.Count == 0;
            return new { success = succeeded, message = succeeded ? "Capture2DRegion PNG output verified." : failure ?? "Capture cleanup failed.",
                data = new { status = succeeded ? "completed" : outputAttempted || images.Count != 0 ? "partial" : "blocked",
                    captureKind = "Capture2DRegion", preview = request?.preview ?? false, written = outputAttempted,
                    errorCode = failureCode, plan, scope, outputPaths, images, cleanupErrors,
                    nativePixelQualification = "not_run", seamParityScope = "geometry only; pipeline effects require native qualification" } };
        }

        internal static ScopeResult ResolveScope(string stageName)
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            var scenes = new List<Scene>();
            if (stageName == "CurrentPrefabStage") {
                if (prefabStage == null || !prefabStage.scene.IsValid() || !prefabStage.scene.isLoaded)
                    throw new Region2DException("PREFAB_STAGE_REQUIRED", "CurrentPrefabStage requires an open, loaded Prefab Stage.");
                scenes.Add(prefabStage.scene);
            } else {
                for (int i = 0; i < SceneManager.sceneCount; i++) {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.IsValid() && scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene)
                        && (prefabStage == null || scene != prefabStage.scene)) scenes.Add(scene);
                }
            }
            ulong mask = 0;
            foreach (var scene in scenes) mask |= EditorSceneManager.GetSceneCullingMask(scene);
            if (scenes.Count == 0 || mask == 0) throw new Region2DException("SCENE_SCOPE_EMPTY", "No renderable scene culling mask exists for the explicit stage.");
            return new ScopeResult { stage = stageName, sceneCullingMask = mask,
                scenes = scenes.Select(s => (object)new { identity = s.handle.ToString(), path = s.path, name = s.name, dirty = s.isDirty }).ToArray() };
        }

        internal static string[] OutputPaths(Region2DRequest request, Region2DPlan plan, Func<string, string> resolveDirectory)
        {
            var directory = Path.GetFullPath(resolveDirectory(request.outputDirectory));
            string name = string.IsNullOrWhiteSpace(request.fileName) ? "Capture2DRegion_" + Guid.NewGuid().ToString("N") + ".png" : request.fileName.Trim();
            if (Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new Region2DException("FILE_NAME_INVALID", "FileName must be a single valid PNG filename.");
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name += ".png";
            var stem = Path.GetFileNameWithoutExtension(name);
            return plan.tiles.Select(t => Path.GetFullPath(Path.Combine(directory,
                plan.tiles.Count == 1 ? name : stem + "_tile_" + t.index.ToString("D3") + ".png"))).ToArray();
        }

        internal static byte[] RenderPng(Camera camera, int width, int height, string mode, out string actualMode)
        {
            RenderTexture rt = null;
            Texture2D texture = null;
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var cleanup = new List<Exception>();
            Exception original = null;
            byte[] png = null;
            actualMode = "Immediate";
            try {
                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 1);
                rt.filterMode = FilterMode.Point;
                rt.useDynamicScale = false;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                camera.targetTexture = rt;
                if (GraphicsSettings.currentRenderPipeline == null) camera.Render();
                else {
                    var request = new RenderPipeline.StandardRequest { destination = rt };
                    if (!RenderPipeline.SupportsRenderRequest(camera, request)) throw new Region2DException("PIPELINE_UNSUPPORTED", "The current pipeline does not support a StandardRequest for the independent region camera.");
                    RenderPipeline.SubmitRenderRequest(camera, request);
                }
                RenderTexture.active = rt;
                bool gpuSucceeded = false;
                if (mode == "GpuReadback" && SystemInfo.supportsAsyncGPUReadback) {
                    var readback = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
                    readback.WaitForCompletion();
                    if (!readback.hasError) {
                        texture.LoadRawTextureData(readback.GetData<byte>()); texture.Apply(false, false);
                        actualMode = "GpuReadback"; gpuSucceeded = true;
                    }
                }
                if (!gpuSucceeded) {
                    texture.ReadPixels(new Rect(0, 0, width, height), 0, 0); texture.Apply(false, false);
                    actualMode = mode == "GpuReadback" ? "ReadPixelsFallback" : "Immediate";
                }
                png = texture.EncodeToPNG();
                if (png == null || png.Length == 0) throw new IOException("PNG encoder returned no data.");
            } catch (Exception e) { original = e; }
            finally {
                try { camera.targetTexture = previousTarget; } catch (Exception e) { cleanup.Add(e); }
                try { RenderTexture.active = previousActive; } catch (Exception e) { cleanup.Add(e); }
                try { if (rt != null) RenderTexture.ReleaseTemporary(rt); } catch (Exception e) { cleanup.Add(e); }
                try { if (texture != null) Object.DestroyImmediate(texture); } catch (Exception e) { cleanup.Add(e); }
            }
            if (original != null || cleanup.Count != 0) {
                if (original != null) cleanup.Insert(0, original);
                throw new AggregateException("Region rendering or cleanup failed.", cleanup);
            }
            return png;
        }

        internal static void ValidatePng(byte[] png, int width, int height)
        {
            Texture2D verifier = null;
            try {
                verifier = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(verifier, png, true) || verifier.width != width || verifier.height != height)
                    throw new IOException("Encoded PNG cannot be decoded at the planned dimensions.");
            } finally { if (verifier != null) Object.DestroyImmediate(verifier); }
        }
        internal static string Sha(byte[] bytes) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
