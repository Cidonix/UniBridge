using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.Legacy
{
    internal static class UniBridgeLegacyCapture
    {
        private const int MinimumDimension = 64;
        private const int MaximumDimension = 4096;
        private const int DefaultWidth = 1280;
        private const int DefaultHeight = 720;
        private const int DefaultTimeoutMs = 10000;
        private const int MaximumCaptureRecords = 128;

        private sealed class CaptureRecord
        {
            public string Id;
            public string Action;
            public string Status;
            public string Path;
            public string Error;
            public string RequestedUtc;
            public string CompletedUtc;
            public int Width;
            public int Height;
            public int SuperSize;
            public long ByteLength;
            public string Sha256;
            public bool IsAsync;
            public bool IncludesEditorChrome;
            public bool IncludesGameOverlays;
            public bool IncludesSceneGizmos;
            public DateTime DeadlineUtc;
            public bool ScreenshotRequested;
            public long LastObservedLength;
            public int StableLengthUpdates;
            public bool RestoreFocus;
            public EditorWindow PreviousWindow;
            public EditorWindow GameView;
        }

        private static readonly Dictionary<string, CaptureRecord> Records =
            new Dictionary<string, CaptureRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> RecordOrder = new List<string>();
        private static CaptureRecord pendingGameView;

        static UniBridgeLegacyCapture()
        {
            EditorApplication.update += UpdatePendingGameView;
        }

        internal static Dictionary<string, object> Execute(Dictionary<string, object> parameters)
        {
            UpdatePendingGameView();
            string action = UniBridgeLegacyValue.GetString(parameters, "Action", "Capabilities");
            if (String.Equals(action, "Capabilities", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(action, "GetCapabilities", StringComparison.OrdinalIgnoreCase))
                return BuildCapabilities();
            if (String.Equals(action, "CaptureSceneView", StringComparison.OrdinalIgnoreCase))
                return CaptureSceneView(parameters);
            if (String.Equals(action, "CaptureGameCamera", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(action, "CaptureCamera", StringComparison.OrdinalIgnoreCase))
                return CaptureGameCamera(parameters);
            if (String.Equals(action, "CaptureGameView", StringComparison.OrdinalIgnoreCase))
                return CaptureGameView(parameters);
            if (String.Equals(action, "GetCaptureStatus", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(action, "Get", StringComparison.OrdinalIgnoreCase))
                return GetCaptureStatus(parameters);
            if (String.Equals(action, "ListCaptures", StringComparison.OrdinalIgnoreCase))
                return ListCaptures(parameters);
            if (String.Equals(action, "ListCameras", StringComparison.OrdinalIgnoreCase))
                return ListCameras(parameters);
            throw new InvalidOperationException("Unsupported CaptureView action '" + action + "'.");
        }

        private static Dictionary<string, object> BuildCapabilities()
        {
            Dictionary<string, object> result = Result("Legacy Unity visual capture capabilities.");
            result["actions"] = new List<object>
            {
                "CaptureSceneView",
                "CaptureGameCamera",
                "CaptureGameView",
                "GetCaptureStatus",
                "ListCaptures",
                "ListCameras"
            };
            result["outputDirectory"] = GetCaptureDirectory();
            result["minimumDimension"] = MinimumDimension;
            result["maximumDimension"] = MaximumDimension;
            result["sceneView"] = UniBridgeLegacyValue.Object(
                "notes",
                "Immediate camera render of the current Scene View. It does not include Unity window chrome or IMGUI overlays/gizmos.");
            result["gameCamera"] = UniBridgeLegacyValue.Object(
                "notes",
                "Immediate render from a live Camera selected by ComponentObjectId/ObjectId. Screen Space Overlay UI is not included.");
            result["gameView"] = UniBridgeLegacyValue.Object(
                "notes",
                "Exact Unity Game View PNG requested asynchronously through Application.CaptureScreenshot. Poll with GetCaptureStatus; an existing Game View is required unless CreateViewIfMissing=true.");
            result["editorWindowCapture"] = false;
            result["editorWindowCaptureReason"] =
                "Legacy Unity has no public cross-platform API for capturing full Editor chrome, Inspector, or Console pixels. Those require an OS-level screen capture outside the Unity MCP adapter.";
            return result;
        }

        private static Dictionary<string, object> CaptureSceneView(Dictionary<string, object> parameters)
        {
            EnsureEditorReadyForCapture();
            int width = ReadDimension(parameters, "Width", DefaultWidth);
            int height = ReadDimension(parameters, "Height", DefaultHeight);
            bool createIfMissing = UniBridgeLegacyValue.GetBool(parameters, "CreateViewIfMissing", false);

            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null && createIfMissing)
                sceneView = EditorWindow.GetWindow<SceneView>();
            if (sceneView == null || sceneView.camera == null)
                throw new InvalidOperationException(
                    "No live Scene View is available. Open a Scene tab or pass CreateViewIfMissing=true.");

            string path = CreateCapturePath("scene-view", parameters);
            byte[] png = RenderCameraToPng(sceneView.camera, width, height);
            WriteCaptureAtomically(path, png);

            CaptureRecord record = CreateCompletedRecord(
                "CaptureSceneView",
                path,
                width,
                height,
                false,
                false,
                false);
            AddRecord(record);
            Dictionary<string, object> result = BuildRecordResult(record);
            result["sceneViewObjectId"] = sceneView.GetInstanceID();
            result["cameraObjectId"] = sceneView.camera.GetInstanceID();
            return result;
        }

        private static Dictionary<string, object> CaptureGameCamera(Dictionary<string, object> parameters)
        {
            EnsureEditorReadyForCapture();
            int width = ReadDimension(parameters, "Width", DefaultWidth);
            int height = ReadDimension(parameters, "Height", DefaultHeight);
            Camera camera = ResolveCamera(parameters);
            string path = CreateCapturePath("game-camera", parameters);
            byte[] png = RenderCameraToPng(camera, width, height);
            WriteCaptureAtomically(path, png);

            CaptureRecord record = CreateCompletedRecord(
                "CaptureGameCamera",
                path,
                width,
                height,
                false,
                false,
                false);
            AddRecord(record);
            Dictionary<string, object> result = BuildRecordResult(record);
            result["camera"] = BuildCameraSummary(camera);
            return result;
        }

        private static Dictionary<string, object> CaptureGameView(Dictionary<string, object> parameters)
        {
            EnsureEditorReadyForCapture();
            if (pendingGameView != null && IsPending(pendingGameView))
                throw new InvalidOperationException(
                    "An exact Game View capture is already pending. Poll CaptureId '" + pendingGameView.Id + "'.");

            bool createIfMissing = UniBridgeLegacyValue.GetBool(parameters, "CreateViewIfMissing", false);
            EditorWindow gameView = FindGameView(createIfMissing);
            if (gameView == null)
                throw new InvalidOperationException(
                    "No live Game View is available. Open a Game tab or pass CreateViewIfMissing=true.");

            int timeoutMs = Clamp(UniBridgeLegacyValue.GetInt(parameters, "TimeoutMs", DefaultTimeoutMs), 1000, 30000);
            int superSize = Clamp(UniBridgeLegacyValue.GetInt(parameters, "SuperSize", 1), 1, 4);
            string path = CreateCapturePath("game-view", parameters);
            CaptureRecord record = new CaptureRecord();
            record.Id = Guid.NewGuid().ToString("N");
            record.Action = "CaptureGameView";
            record.Status = "pendingFocus";
            record.Path = path;
            record.RequestedUtc = UtcNow();
            record.IsAsync = true;
            record.IncludesEditorChrome = false;
            record.IncludesGameOverlays = true;
            record.IncludesSceneGizmos = false;
            record.DeadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            record.RestoreFocus = UniBridgeLegacyValue.GetBool(parameters, "RestoreFocus", true);
            record.PreviousWindow = EditorWindow.focusedWindow;
            record.GameView = gameView;
            record.SuperSize = superSize;
            AddRecord(record);
            pendingGameView = record;

            gameView.Show();
            gameView.Focus();
            gameView.Repaint();

            Dictionary<string, object> result = BuildRecordResult(record);
            result["pollAction"] = "GetCaptureStatus";
            result["note"] =
                "The request is queued so legacy Unity can render another Editor frame. Poll CaptureId until status is complete or failed.";
            return result;
        }

        private static Dictionary<string, object> GetCaptureStatus(Dictionary<string, object> parameters)
        {
            string id = UniBridgeLegacyValue.GetString(parameters, "CaptureId", null);
            if (String.IsNullOrEmpty(id))
                throw new InvalidOperationException("CaptureId is required.");
            CaptureRecord record;
            if (!Records.TryGetValue(id, out record))
                throw new InvalidOperationException("CaptureId is not known in this adapter session: " + id);
            return BuildRecordResult(record);
        }

        private static Dictionary<string, object> ListCaptures(Dictionary<string, object> parameters)
        {
            int maximum = Clamp(UniBridgeLegacyValue.GetInt(parameters, "MaxResults", 20), 1, 100);
            List<object> captures = new List<object>();
            int start = Math.Max(0, RecordOrder.Count - maximum);
            for (int index = RecordOrder.Count - 1; index >= start; index--)
            {
                CaptureRecord record;
                if (Records.TryGetValue(RecordOrder[index], out record))
                    captures.Add(BuildRecordResult(record));
            }
            Dictionary<string, object> result = Result("Legacy capture records from the current adapter session.");
            result["captures"] = captures;
            result["count"] = captures.Count;
            result["outputDirectory"] = GetCaptureDirectory();
            return result;
        }

        private static Dictionary<string, object> ListCameras(Dictionary<string, object> parameters)
        {
            int maximum = Clamp(UniBridgeLegacyValue.GetInt(parameters, "MaxResults", 50), 1, 200);
            Camera[] cameras = Resources.FindObjectsOfTypeAll<Camera>();
            List<Camera> sceneCameras = new List<Camera>();
            for (int index = 0; index < cameras.Length; index++)
            {
                Camera camera = cameras[index];
                if (camera == null || camera.gameObject == null || !camera.gameObject.scene.IsValid())
                    continue;
                sceneCameras.Add(camera);
            }
            sceneCameras.Sort(delegate(Camera left, Camera right)
            {
                return String.CompareOrdinal(GetHierarchyPath(left.transform), GetHierarchyPath(right.transform));
            });

            List<object> values = new List<object>();
            for (int index = 0; index < sceneCameras.Count && index < maximum; index++)
                values.Add(BuildCameraSummary(sceneCameras[index]));
            Dictionary<string, object> result = Result("Live scene Cameras addressable by Unity object identity.");
            result["cameras"] = values;
            result["count"] = values.Count;
            result["total"] = sceneCameras.Count;
            result["truncated"] = sceneCameras.Count > values.Count;
            return result;
        }

        private static void UpdatePendingGameView()
        {
            CaptureRecord record = pendingGameView;
            if (record == null || !IsPending(record))
                return;

            try
            {
                if (DateTime.UtcNow > record.DeadlineUtc)
                {
                    FailRecord(record, "Timed out waiting for Unity 5.6 to write the Game View PNG.");
                    return;
                }

                if (!record.ScreenshotRequested)
                {
                    if (record.GameView == null)
                    {
                        FailRecord(record, "The Game View closed before capture could begin.");
                        return;
                    }
                    record.GameView.Focus();
                    record.GameView.Repaint();
                    RequestGameViewScreenshot(record.Path, record.SuperSize);
                    record.ScreenshotRequested = true;
                    record.Status = "pendingFile";
                    return;
                }

                if (record.GameView != null)
                    record.GameView.Repaint();
                if (!File.Exists(record.Path))
                    return;

                long length = new FileInfo(record.Path).Length;
                if (length <= 8)
                    return;
                if (length == record.LastObservedLength)
                    record.StableLengthUpdates++;
                else
                {
                    record.LastObservedLength = length;
                    record.StableLengthUpdates = 0;
                }
                if (record.StableLengthUpdates < 2)
                    return;

                int width;
                int height;
                try
                {
                    ReadPngDimensions(record.Path, out width, out height);
                    record.Sha256 = ComputeFileSha256(record.Path);
                }
                catch (IOException)
                {
                    record.StableLengthUpdates = 0;
                    return;
                }
                catch (InvalidOperationException)
                {
                    record.StableLengthUpdates = 0;
                    return;
                }
                record.Width = width;
                record.Height = height;
                record.ByteLength = length;
                record.Status = "complete";
                record.CompletedUtc = UtcNow();
                FinishPendingGameView(record);
            }
            catch (Exception exception)
            {
                FailRecord(record, exception.Message);
            }
        }

        private static void FailRecord(CaptureRecord record, string error)
        {
            record.Status = "failed";
            record.Error = error;
            record.CompletedUtc = UtcNow();
            FinishPendingGameView(record);
        }

        private static void FinishPendingGameView(CaptureRecord record)
        {
            if (record.RestoreFocus && record.PreviousWindow != null && record.PreviousWindow != record.GameView)
            {
                try
                {
                    record.PreviousWindow.Focus();
                    record.PreviousWindow.Repaint();
                }
                catch { }
            }
            if (pendingGameView == record)
                pendingGameView = null;
        }

        private static EditorWindow FindGameView(bool createIfMissing)
        {
            Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
                throw new InvalidOperationException("UnityEditor.GameView type is unavailable in this Editor revision.");

            UnityEngine.Object[] views = Resources.FindObjectsOfTypeAll(gameViewType);
            for (int index = 0; index < views.Length; index++)
            {
                EditorWindow window = views[index] as EditorWindow;
                if (window != null)
                    return window;
            }
            if (!createIfMissing)
                return null;
            return EditorWindow.GetWindow(gameViewType);
        }

        private static void RequestGameViewScreenshot(string path, int superSize)
        {
            Assembly unityEngineAssembly = typeof(Application).Assembly;
            Type screenCaptureType = unityEngineAssembly.GetType("UnityEngine.ScreenCapture");
            MethodInfo capture = null;
            if (screenCaptureType != null)
            {
                capture = screenCaptureType.GetMethod(
                    "CaptureScreenshot",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new Type[] { typeof(string), typeof(int) },
                    null);
            }
            if (capture == null)
            {
                capture = typeof(Application).GetMethod(
                    "CaptureScreenshot",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new Type[] { typeof(string), typeof(int) },
                    null);
            }
            if (capture == null)
                throw new InvalidOperationException("This Unity revision has no compatible screenshot API.");
            try
            {
                capture.Invoke(null, new object[] { path, superSize });
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    exception.InnerException == null ? exception.Message : exception.InnerException.Message);
            }
        }

        private static Camera ResolveCamera(Dictionary<string, object> parameters)
        {
            int componentObjectId = UniBridgeLegacyValue.GetInt(parameters, "ComponentObjectId", 0);
            if (componentObjectId != 0)
            {
                Camera byComponentId = EditorUtility.InstanceIDToObject(componentObjectId) as Camera;
                if (byComponentId == null)
                    throw new InvalidOperationException("ComponentObjectId did not resolve to a live Camera.");
                return byComponentId;
            }

            int objectId = UniBridgeLegacyValue.GetInt(parameters, "ObjectId", 0);
            if (objectId != 0)
            {
                UnityEngine.Object target = EditorUtility.InstanceIDToObject(objectId);
                Camera byCameraId = target as Camera;
                if (byCameraId != null)
                    return byCameraId;
                GameObject gameObject = target as GameObject;
                if (gameObject == null)
                    throw new InvalidOperationException("ObjectId did not resolve to a live Camera or GameObject.");
                Camera[] attached = gameObject.GetComponents<Camera>();
                if (attached.Length == 1)
                    return attached[0];
                if (attached.Length == 0)
                    throw new InvalidOperationException("The selected GameObject has no Camera component.");
                throw new InvalidOperationException(
                    "The selected GameObject has multiple Cameras. Use ComponentObjectId from ListCameras.");
            }

            Camera main = Camera.main;
            if (main != null && main.gameObject != null && main.gameObject.scene.IsValid())
                return main;

            Camera[] all = Resources.FindObjectsOfTypeAll<Camera>();
            List<Camera> enabled = new List<Camera>();
            for (int index = 0; index < all.Length; index++)
            {
                Camera candidate = all[index];
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.scene.IsValid())
                    continue;
                if (candidate.enabled && candidate.gameObject.activeInHierarchy)
                    enabled.Add(candidate);
            }
            if (enabled.Count == 1)
                return enabled[0];
            if (enabled.Count == 0)
                throw new InvalidOperationException(
                    "No enabled live scene Camera was found. Use ListCameras and pass ComponentObjectId.");
            throw new InvalidOperationException(
                "Multiple enabled Cameras are available. Use ListCameras and pass ComponentObjectId.");
        }

        private static byte[] RenderCameraToPng(Camera camera, int width, int height)
        {
            if (camera == null)
                throw new InvalidOperationException("A live Camera is required for capture.");

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            float previousAspect = camera.aspect;
            RenderTexture target = null;
            Texture2D readable = null;
            try
            {
                target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;
                camera.aspect = (float)width / (float)height;
                RenderTexture.active = target;
                camera.Render();

                readable = new Texture2D(width, height, TextureFormat.RGB24, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply(false);
                byte[] png = readable.EncodeToPNG();
                if (png == null || png.Length == 0)
                    throw new InvalidOperationException("Unity returned an empty PNG payload.");
                return png;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                camera.aspect = previousAspect;
                RenderTexture.active = previousActive;
                if (readable != null)
                    UnityEngine.Object.DestroyImmediate(readable);
                if (target != null)
                    RenderTexture.ReleaseTemporary(target);
            }
        }

        private static CaptureRecord CreateCompletedRecord(
            string action,
            string path,
            int width,
            int height,
            bool includesEditorChrome,
            bool includesGameOverlays,
            bool includesSceneGizmos)
        {
            CaptureRecord record = new CaptureRecord();
            record.Id = Guid.NewGuid().ToString("N");
            record.Action = action;
            record.Status = "complete";
            record.Path = path;
            record.RequestedUtc = UtcNow();
            record.CompletedUtc = record.RequestedUtc;
            record.Width = width;
            record.Height = height;
            record.ByteLength = new FileInfo(path).Length;
            record.Sha256 = ComputeFileSha256(path);
            record.IsAsync = false;
            record.IncludesEditorChrome = includesEditorChrome;
            record.IncludesGameOverlays = includesGameOverlays;
            record.IncludesSceneGizmos = includesSceneGizmos;
            return record;
        }

        private static void AddRecord(CaptureRecord record)
        {
            Records[record.Id] = record;
            RecordOrder.Add(record.Id);
            while (RecordOrder.Count > MaximumCaptureRecords)
            {
                string oldest = RecordOrder[0];
                RecordOrder.RemoveAt(0);
                Records.Remove(oldest);
            }
        }

        private static Dictionary<string, object> BuildRecordResult(CaptureRecord record)
        {
            Dictionary<string, object> result = Result("Unity view capture " + record.Status + ".");
            result["captureId"] = record.Id;
            result["action"] = record.Action;
            result["status"] = record.Status;
            result["isReady"] = String.Equals(record.Status, "complete", StringComparison.OrdinalIgnoreCase);
            result["isAsync"] = record.IsAsync;
            result["path"] = record.Path;
            result["exists"] = !String.IsNullOrEmpty(record.Path) && File.Exists(record.Path);
            result["width"] = record.Width > 0 ? (object)record.Width : null;
            result["height"] = record.Height > 0 ? (object)record.Height : null;
            result["superSize"] = record.SuperSize > 0 ? (object)record.SuperSize : null;
            result["byteLength"] = record.ByteLength > 0 ? (object)record.ByteLength : null;
            result["sha256"] = record.Sha256;
            result["requestedUtc"] = record.RequestedUtc;
            result["completedUtc"] = record.CompletedUtc;
            result["error"] = record.Error;
            result["includesEditorChrome"] = record.IncludesEditorChrome;
            result["includesGameOverlays"] = record.IncludesGameOverlays;
            result["includesSceneGizmos"] = record.IncludesSceneGizmos;
            return result;
        }

        private static Dictionary<string, object> BuildCameraSummary(Camera camera)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["componentObjectId"] = camera.GetInstanceID();
            result["gameObjectId"] = camera.gameObject.GetInstanceID();
            result["name"] = camera.gameObject.name;
            result["path"] = GetHierarchyPath(camera.transform);
            result["sceneId"] = camera.gameObject.scene.GetHashCode();
            result["scenePath"] = camera.gameObject.scene.path;
            result["enabled"] = camera.enabled;
            result["activeInHierarchy"] = camera.gameObject.activeInHierarchy;
            result["depth"] = camera.depth;
            bool isMain = false;
            try { isMain = camera.CompareTag("MainCamera"); }
            catch { }
            result["isMainCamera"] = isMain;
            return result;
        }

        private static string GetHierarchyPath(Transform transform)
        {
            List<string> parts = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return "/" + String.Join("/", parts.ToArray());
        }

        private static string CreateCapturePath(string kind, Dictionary<string, object> parameters)
        {
            string directory = GetCaptureDirectory();
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            string label = SanitizeLabel(UniBridgeLegacyValue.GetString(parameters, "Label", null));
            string fileName = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                              "-" + kind;
            if (!String.IsNullOrEmpty(label))
                fileName += "-" + label;
            fileName += "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png";
            return Path.GetFullPath(Path.Combine(directory, fileName));
        }

        private static string GetCaptureDirectory()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(Path.Combine(Path.Combine(projectRoot, "Library"), "UniBridge"), "Captures");
        }

        private static string SanitizeLabel(string value)
        {
            if (String.IsNullOrEmpty(value))
                return null;
            string trimmed = value.Trim();
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int index = 0; index < trimmed.Length && builder.Length < 40; index++)
            {
                char character = trimmed[index];
                if (Char.IsLetterOrDigit(character) || character == '-' || character == '_')
                    builder.Append(character);
                else if (Char.IsWhiteSpace(character) && builder.Length > 0 && builder[builder.Length - 1] != '-')
                    builder.Append('-');
            }
            return builder.Length == 0 ? null : builder.ToString();
        }

        private static void WriteCaptureAtomically(string path, byte[] bytes)
        {
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch { }
            }
        }

        private static void ReadPngDimensions(string path, out int width, out int height)
        {
            byte[] header = new byte[24];
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int offset = 0;
                while (offset < header.Length)
                {
                    int read = stream.Read(header, offset, header.Length - offset);
                    if (read <= 0)
                        break;
                    offset += read;
                }
                if (offset < header.Length)
                    throw new InvalidOperationException("The Game View PNG is incomplete.");
            }
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
                throw new InvalidOperationException("Unity wrote a file that is not a PNG.");
            width = ReadBigEndianInt32(header, 16);
            height = ReadBigEndianInt32(header, 20);
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Unity wrote a PNG with invalid dimensions.");
        }

        private static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) |
                   (bytes[offset + 1] << 16) |
                   (bytes[offset + 2] << 8) |
                   bytes[offset + 3];
        }

        private static string ComputeFileSha256(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] bytes = hash.ComputeHash(stream);
                System.Text.StringBuilder builder = new System.Text.StringBuilder(bytes.Length * 2);
                for (int index = 0; index < bytes.Length; index++)
                    builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static void EnsureEditorReadyForCapture()
        {
            if (EditorApplication.isCompiling)
                throw new InvalidOperationException("Unity is compiling scripts; wait before visual capture.");
            if (EditorApplication.isUpdating)
                throw new InvalidOperationException("Unity is updating the AssetDatabase; wait before visual capture.");
        }

        private static int ReadDimension(Dictionary<string, object> parameters, string key, int fallback)
        {
            return Clamp(UniBridgeLegacyValue.GetInt(parameters, key, fallback), MinimumDimension, MaximumDimension);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static bool IsPending(CaptureRecord record)
        {
            return record != null &&
                   (String.Equals(record.Status, "pendingFocus", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(record.Status, "pendingFile", StringComparison.OrdinalIgnoreCase));
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, object> Result(string message)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["success"] = true;
            result["message"] = message;
            return result;
        }
    }
}
