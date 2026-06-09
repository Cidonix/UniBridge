#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Renders UI Toolkit VisualTreeAsset/UIDocument content into deterministic PNG captures.
    /// </summary>
    public static class CaptureUIToolkit
    {
        const int MinSize = 64;
        const int MaxSize = 4096;

        public const string Title = "Capture UI Toolkit UXML";

        public const string Description = @"Render UI Toolkit UXML or UIDocument content into a deterministic PNG and inspect the resolved VisualElement tree.

Use this when an agent needs to visually inspect a UXML VisualTreeAsset, debug a UIDocument layout, or verify UI Toolkit visibility before editing USS/UXML. It creates a temporary PanelSettings + UIDocument, renders to RenderTexture, and reads back a PNG. UniBridge also returns bounded VisualElement metadata, layout bounds, blank-capture pixel stats, and simple visibility/layout issue hints.

Args:
    Action: Capture, Inspect, or ListUxml.
    Path/Guid: VisualTreeAsset UXML to render.
    Target: Optional scene GameObject with UIDocument; uses its VisualTreeAsset and theme when available.
    Query/Folders/Limit: Search VisualTreeAsset assets.
    Width/Height: PNG size, clamped to 64..4096.
    ReadbackMode: Immediate or GpuReadback. GpuReadback uses synchronous AsyncGPUReadback with ReadPixels fallback.
    RenderPasses: Number of forced UI Toolkit render/update passes before readback, clamped to 1..8.
    ThemeStyleSheetPath: Optional ThemeStyleSheet override; otherwise target panel, UI Builder theme, or UnityDefaultRuntimeTheme.tss is used when available.
    IncludeTree/IncludeIssues/MaxTreeDepth/MaxTreeItems: Metadata controls.
    OutputDirectory/FileName/Tag: Optional output controls.

        Returns:
    success, message, and data containing the PNG path and metadata for Capture, or resolved VisualElement tree and issue hints for Inspect/ListUxml.";

        [McpTool("UniBridge_CaptureUIToolkit", Description, Title, Groups = new[] { "core", "assets", "visual", "ui" }, EnabledByDefault = true)]
        public static async Task<object> HandleCommand(CaptureUIToolkitParams parameters)
        {
            parameters ??= new CaptureUIToolkitParams();

            try
            {
                switch (parameters.Action)
                {
                    case CaptureUIToolkitAction.Capture:
                        return await CaptureOrInspectAsync(parameters, capturePng: true);
                    case CaptureUIToolkitAction.Inspect:
                        return await CaptureOrInspectAsync(parameters, capturePng: false);
                    case CaptureUIToolkitAction.ListUxml:
                        return ListUxml(parameters);
                    default:
                        return Response.Error($"Unsupported UI Toolkit capture action '{parameters.Action}'.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CaptureUIToolkit] {parameters.Action} failed for '{parameters.Path ?? parameters.Guid ?? parameters.Target ?? parameters.Query}': {ex}");
                return Response.Error($"UI Toolkit capture failed: {ex.Message}");
            }
        }

        static async Task<object> CaptureOrInspectAsync(CaptureUIToolkitParams parameters, bool capturePng)
        {
            if (EditorApplication.isPlaying)
                return Response.Error("UniBridge_CaptureUIToolkit requires Edit Mode. Exit Play Mode before capturing UXML.");

            FocusUnityForRuntimePanelCapture();

            var resolved = ResolveDocument(parameters);
            if (!resolved.Success)
                return Response.Error(resolved.Error);

            var width = Mathf.Clamp(parameters.Width ?? 1280, MinSize, MaxSize);
            var height = Mathf.Clamp(parameters.Height ?? 720, MinSize, MaxSize);
            var transparent = parameters.TransparentBackground ?? true;
            var background = transparent
                ? new Color(0f, 0f, 0f, 0f)
                : ReadColor(parameters.BackgroundColor, new Color(0.035f, 0.04f, 0.055f, 1f));
            var maxDepth = Mathf.Clamp(parameters.MaxTreeDepth ?? 8, 1, 32);
            var maxItems = Mathf.Clamp(parameters.MaxTreeItems ?? 200, 1, 1000);
            var renderPasses = Mathf.Clamp(parameters.RenderPasses ?? 2, 1, 8);

            PanelSettings panelSettings = null;
            RenderTexture renderTexture = null;
            Texture2D readable = null;
            GameObject host = null;
            RenderPassInfo renderPassInfo = null;
            string actualReadbackMode = null;
            var previousActive = RenderTexture.active;

            try
            {
                panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                panelSettings.hideFlags = HideFlags.HideAndDontSave;
                panelSettings.scale = Mathf.Max(0.01f, parameters.PanelScale ?? (1f / EditorGUIUtility.pixelsPerPoint));
                panelSettings.clearColor = true;
                panelSettings.clearDepthStencil = false;
                panelSettings.colorClearValue = background;
                panelSettings.themeStyleSheet = ResolveTheme(parameters, resolved);

                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                renderTexture.Create();
                panelSettings.targetTexture = renderTexture;

                host = new GameObject("UniBridge_UIToolkitCapture_UIDocument")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = panelSettings;
                document.visualTreeAsset = resolved.VisualTreeAsset;

                var root = document.rootVisualElement;
                root.style.width = width;
                root.style.height = height;
                root.style.flexGrow = 1f;

                renderPassInfo = await RenderDocumentPassesAsync(document, renderPasses);

                byte[] png = null;
                PixelStats pixelStats = null;
                if (capturePng)
                {
                    readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    actualReadbackMode = ReadRenderTexture(renderTexture, readable, parameters.ReadbackMode);

                    pixelStats = AnalyzePixels(readable);
                    png = readable.EncodeToPNG();
                    if (png == null || png.Length == 0)
                        return Response.Error("UI Toolkit capture produced an empty PNG.");
                }

                var issueList = parameters.IncludeIssues ? BuildIssues(root, width, height, maxItems, pixelStats) : new List<object>();
                var treeTruncated = false;
                var tree = parameters.IncludeTree ? BuildTree(root, maxDepth, maxItems, out treeTruncated) : null;

                string outputPath = null;
                if (capturePng)
                {
                    outputPath = ResolveOutputPath(parameters, resolved.AssetPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                    File.WriteAllBytes(outputPath, png);
                }

                if (parameters.Select)
                    Selection.activeObject = resolved.VisualTreeAsset;

                var data = new
                {
                    action = parameters.Action.ToString(),
                    path = capturePng ? NormalizePath(outputPath) : null,
                    fileUri = capturePng ? new Uri(outputPath).AbsoluteUri : null,
                    width,
                    height,
                    bytes = png?.Length ?? 0,
                    format = capturePng ? "png" : null,
                    captureKind = capturePng ? "UIToolkitPNG" : "UIToolkitInspect",
                    transparentBackground = transparent,
                    panel = new
                    {
                        scale = panelSettings.scale,
                        themeStyleSheet = panelSettings.themeStyleSheet != null ? AssetDatabase.GetAssetPath(panelSettings.themeStyleSheet) : null,
                        targetTexture = new { width = renderTexture.width, height = renderTexture.height }
                    },
                    render = new
                    {
                        passesRequested = renderPasses,
                        passesCompleted = renderPassInfo?.PassesCompleted ?? 0,
                        editorUpdatesWaited = renderPassInfo?.EditorUpdatesWaited ?? 0,
                        requestedReadbackMode = capturePng ? parameters.ReadbackMode.ToString() : null,
                        readbackMode = actualReadbackMode
                    },
                    document = new
                    {
                        path = resolved.AssetPath,
                        guid = AssetDatabase.AssetPathToGUID(resolved.AssetPath),
                        name = resolved.VisualTreeAsset.name,
                        type = resolved.VisualTreeAsset.GetType().FullName,
                        source = resolved.Source,
                        target = resolved.TargetName
                    },
                    pixelStats = pixelStats?.ToObject(),
                    tree,
                    treeTruncated = parameters.IncludeTree && treeTruncated,
                    issues = issueList,
                    summary = new
                    {
                        issueCount = issueList.Count,
                        blankCapture = pixelStats != null && pixelStats.NonTransparentPixels == 0,
                        treeIncluded = parameters.IncludeTree,
                        issuesIncluded = parameters.IncludeIssues
                    }
                };

                return Response.Success(
                    capturePng
                        ? $"Captured UI Toolkit document '{resolved.AssetPath}' to '{outputPath}'."
                        : $"Inspected UI Toolkit document '{resolved.AssetPath}'.",
                    data);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (readable != null)
                    Object.DestroyImmediate(readable);
                if (host != null)
                    Object.DestroyImmediate(host);
                if (renderTexture != null)
                    Object.DestroyImmediate(renderTexture);
                if (panelSettings != null)
                    Object.DestroyImmediate(panelSettings);
            }
        }

        static async Task<RenderPassInfo> RenderDocumentPassesAsync(UIDocument document, int renderPasses)
        {
            var info = new RenderPassInfo();
            for (var i = 0; i < renderPasses; i++)
            {
                ForceRenderUIDocument(document);
                info.PassesCompleted++;
                EditorApplication.QueuePlayerLoopUpdate();
                await WaitForEditorUpdateAsync();
                info.EditorUpdatesWaited++;
            }

            return info;
        }

        static Task WaitForEditorUpdateAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            void Tick()
            {
                EditorApplication.update -= Tick;
                tcs.TrySetResult(true);
            }

            EditorApplication.update += Tick;
            SceneView.RepaintAll();
            InternalEditorUtility.RepaintAllViews();
            EditorApplication.QueuePlayerLoopUpdate();
            return tcs.Task;
        }

        static string ReadRenderTexture(RenderTexture renderTexture, Texture2D texture, CaptureReadbackMode mode)
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
                    Debug.LogWarning($"[CaptureUIToolkit] GPU readback failed, falling back to ReadPixels: {ex.Message}");
                }
            }

            RenderTexture.active = renderTexture;
            texture.ReadPixels(new Rect(0f, 0f, texture.width, texture.height), 0, 0, false);
            texture.Apply(false, false);
            return mode == CaptureReadbackMode.GpuReadback ? "ReadPixelsFallback" : "Immediate";
        }

        static void FocusUnityForRuntimePanelCapture()
        {
            try
            {
                if (Application.platform != RuntimePlatform.WindowsEditor || InternalEditorUtility.isApplicationActive)
                    return;

                var window = EditorWindow.focusedWindow ?? SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
                window?.Focus();

                var handle = Process.GetCurrentProcess().MainWindowHandle;
                if (handle == IntPtr.Zero)
                    return;

                var foregroundWindow = GetForegroundWindow();
                var foregroundThread = GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
                var currentThread = GetCurrentThreadId();
                var attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
                try
                {
                    ShowWindow(handle, 9);
                    SetForegroundWindow(handle);
                }
                finally
                {
                    if (attached)
                        AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
            catch
            {
                // Focus improves UI Toolkit targetTexture reliability on Unity 6, but capture can continue without it.
            }
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        static object ListUxml(CaptureUIToolkitParams parameters)
        {
            var limit = Mathf.Clamp(parameters.Limit ?? 50, 1, 200);
            var folders = NormalizeFolders(parameters.Folders);
            var query = "t:VisualTreeAsset";
            if (!string.IsNullOrWhiteSpace(parameters.Query))
                query += " " + parameters.Query.Trim();

            var guids = folders.Length > 0
                ? AssetDatabase.FindAssets(query, folders)
                : AssetDatabase.FindAssets(query);

            var results = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(path =>
                {
                    var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
                    return new
                    {
                        path,
                        guid = AssetDatabase.AssetPathToGUID(path),
                        name = asset != null ? asset.name : Path.GetFileNameWithoutExtension(path),
                        type = typeof(VisualTreeAsset).FullName,
                        extension = Path.GetExtension(path)
                    };
                })
                .ToArray();

            return Response.Success($"Found {results.Length} UXML VisualTreeAsset asset(s).", new
            {
                query = parameters.Query,
                folders,
                returned = results.Length,
                limit,
                results
            });
        }

        static DocumentResolution ResolveDocument(CaptureUIToolkitParams parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameters.Target))
            {
                var target = FindGameObject(parameters.Target);
                if (target == null)
                    return DocumentResolution.Fail($"Target GameObject '{parameters.Target}' was not found.");

                var document = target.GetComponent<UIDocument>();
                if (document == null)
                    return DocumentResolution.Fail($"Target GameObject '{parameters.Target}' does not have a UIDocument component.");

                if (document.visualTreeAsset == null)
                    return DocumentResolution.Fail($"UIDocument on '{target.name}' has no VisualTreeAsset assigned.");

                var targetAssetPath = AssetDatabase.GetAssetPath(document.visualTreeAsset);
                if (string.IsNullOrWhiteSpace(targetAssetPath))
                    return DocumentResolution.Fail($"UIDocument on '{target.name}' references a VisualTreeAsset without an AssetDatabase path.");

                return DocumentResolution.Ok(targetAssetPath, document.visualTreeAsset, "Target", target.name, document.panelSettings);
            }

            var pathFromGuid = string.IsNullOrWhiteSpace(parameters.Guid) ? null : AssetDatabase.GUIDToAssetPath(parameters.Guid);
            var requestedPath = !string.IsNullOrWhiteSpace(parameters.Path) ? parameters.Path : pathFromGuid;
            if (string.IsNullOrWhiteSpace(requestedPath) && !string.IsNullOrWhiteSpace(parameters.Query))
            {
                var folders = NormalizeFolders(parameters.Folders);
                var query = "t:VisualTreeAsset " + parameters.Query.Trim();
                var guid = (folders.Length > 0 ? AssetDatabase.FindAssets(query, folders) : AssetDatabase.FindAssets(query)).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(guid))
                    requestedPath = AssetDatabase.GUIDToAssetPath(guid);
            }

            if (string.IsNullOrWhiteSpace(requestedPath))
                return DocumentResolution.Fail("CaptureUIToolkit requires Path, Guid, Target, or Query.");

            if (!TryNormalizeAssetPath(requestedPath, out var normalizedPath, out var error))
                return DocumentResolution.Fail(error);

            if (AssetDatabase.GetMainAssetTypeAtPath(normalizedPath) == typeof(VisualTreeAsset))
                AssetDatabase.ImportAsset(normalizedPath, ImportAssetOptions.ForceUpdate);

            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(normalizedPath);
            if (asset == null)
                return DocumentResolution.Fail($"VisualTreeAsset was not found at '{normalizedPath}'.");

            return DocumentResolution.Ok(normalizedPath, asset, string.IsNullOrWhiteSpace(parameters.Guid) ? "Path" : "Guid", null, null);
        }

        static ThemeStyleSheet ResolveTheme(CaptureUIToolkitParams parameters, DocumentResolution resolved)
        {
            if (!string.IsNullOrWhiteSpace(parameters.ThemeStyleSheetPath))
            {
                if (TryNormalizeAssetPath(parameters.ThemeStyleSheetPath, out var themePath, out _))
                {
                    var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
                    if (theme != null)
                        return theme;
                }
            }

            if (resolved.SourcePanelSettings != null && resolved.SourcePanelSettings.themeStyleSheet != null)
                return resolved.SourcePanelSettings.themeStyleSheet;

            return TryGetUIBuilderTheme(resolved.AssetPath) ?? FindDefaultRuntimeTheme();
        }

        static ThemeStyleSheet FindDefaultRuntimeTheme()
        {
            const string commonPath = "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss";
            var commonTheme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(commonPath);
            if (commonTheme != null)
                return commonTheme;

            var themePath = AssetDatabase.FindAssets("UnityDefaultRuntimeTheme t:ThemeStyleSheet")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(path => path.EndsWith("UnityDefaultRuntimeTheme.tss", StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrWhiteSpace(themePath) ? null : AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
        }

        static ThemeStyleSheet TryGetUIBuilderTheme(string uxmlPath)
        {
            var builderDocumentType = Type.GetType("Unity.UI.Builder.BuilderDocument, Unity.UI.Builder.Editor");
            if (builderDocumentType == null)
                builderDocumentType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("Unity.UI.Builder.BuilderDocument"))
                    .FirstOrDefault(type => type != null);
            if (builderDocumentType == null)
                return null;

            var documents = Resources.FindObjectsOfTypeAll(builderDocumentType);
            if (documents == null || documents.Length == 0)
                return null;

            var document = documents[0];
            const BindingFlags privateFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags publicFlags = BindingFlags.Instance | BindingFlags.Public;

            if (builderDocumentType.GetField("m_SavedBuilderUxmlToThemeStyleSheetList", privateFlags)?.GetValue(document) is IList list)
            {
                foreach (var item in list)
                {
                    var type = item.GetType();
                    var uxmlUri = type.GetField("UxmlURI", publicFlags)?.GetValue(item) as string;
                    var themeUri = type.GetField("ThemeStyleSheetURI", publicFlags)?.GetValue(item) as string;
                    if (!string.IsNullOrWhiteSpace(uxmlUri) &&
                        !string.IsNullOrWhiteSpace(themeUri) &&
                        uxmlUri.Contains(uxmlPath) &&
                        themeUri.StartsWith("project://database/", StringComparison.OrdinalIgnoreCase))
                    {
                        var end = themeUri.IndexOf('?');
                        if (end < 0)
                            end = themeUri.Length;
                        var assetPath = Uri.UnescapeDataString(themeUri.Substring("project://database/".Length, end - "project://database/".Length));
                        var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(assetPath);
                        if (theme != null)
                            return theme;
                    }
                }
            }

            var currentTheme = builderDocumentType.GetField("m_CurrentCanvasThemeStyleSheetReference", privateFlags)?.GetValue(document);
            return currentTheme as ThemeStyleSheet;
        }

        static void ForceRenderUIDocument(UIDocument document)
        {
            if (document == null || document.rootVisualElement?.panel == null)
                return;

            var panel = document.rootVisualElement.panel;
            var type = panel.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            type.GetMethod("ValidateLayout", flags)?.Invoke(panel, null);
            type.GetMethod("Update", flags)?.Invoke(panel, null);
            var repaint = type.GetMethods(flags)
                .FirstOrDefault(method => method.Name == "Repaint" && method.GetParameters().Length == 1) ??
                type.GetMethods(flags).FirstOrDefault(method => method.Name == "Repaint");
            if (repaint != null)
            {
                var parameters = repaint.GetParameters();
                repaint.Invoke(panel, parameters.Length == 0 ? null : new object[parameters.Length]);
            }
        }

        static object BuildTree(VisualElement root, int maxDepth, int maxItems, out bool truncated)
        {
            var count = 0;
            truncated = false;
            return BuildTreeNode(root, depth: 0, maxDepth, maxItems, ref count, ref truncated);
        }

        static object BuildTreeNode(VisualElement element, int depth, int maxDepth, int maxItems, ref int count, ref bool truncated)
        {
            if (element == null || count >= maxItems || depth > maxDepth)
            {
                truncated = true;
                return null;
            }

            count++;
            var children = new List<object>();
            foreach (var child in element.Children())
            {
                if (count >= maxItems || depth + 1 > maxDepth)
                {
                    truncated = true;
                    break;
                }

                var childNode = BuildTreeNode(child, depth + 1, maxDepth, maxItems, ref count, ref truncated);
                if (childNode != null)
                    children.Add(childNode);
            }

            return new
            {
                depth,
                type = element.GetType().FullName,
                elementType = element.GetType().Name,
                name = element.name,
                classes = element.GetClasses().ToArray(),
                text = element is TextElement textElement ? textElement.text : null,
                tooltip = element.tooltip,
                pickingMode = element.pickingMode.ToString(),
                layout = RectToObject(element.layout),
                worldBound = RectToObject(element.worldBound),
                resolved = new
                {
                    width = element.resolvedStyle.width,
                    height = element.resolvedStyle.height,
                    opacity = element.resolvedStyle.opacity,
                    display = element.resolvedStyle.display.ToString(),
                    position = element.resolvedStyle.position.ToString()
                },
                childCount = element.childCount,
                children
            };
        }

        static List<object> BuildIssues(VisualElement root, int width, int height, int maxItems, PixelStats pixelStats)
        {
            var issues = new List<object>();
            if (pixelStats != null && pixelStats.NonTransparentPixels == 0)
            {
                issues.Add(new
                {
                    code = "BLANK_CAPTURE",
                    severity = "error",
                    path = "png",
                    message = "Rendered PNG has no non-transparent pixels."
                });
            }

            var visited = 0;
            var visibleLeafRecords = new List<ElementIssueRecord>();
            VisitForIssues(root, "root", width, height, maxItems, ref visited, issues, visibleLeafRecords);
            AddOverlapIssues(visibleLeafRecords, issues);
            return issues;
        }

        static void VisitForIssues(VisualElement element, string path, int width, int height, int maxItems, ref int visited, List<object> issues, List<ElementIssueRecord> visibleLeafRecords)
        {
            if (element == null || visited >= maxItems)
                return;

            visited++;
            var display = element.resolvedStyle.display;
            var opacity = element.resolvedStyle.opacity;
            var bound = element.worldBound;
            var visibleByDisplay = display != DisplayStyle.None;

            if (visibleByDisplay && element.childCount == 0 && (bound.width <= 0.5f || bound.height <= 0.5f))
            {
                issues.Add(new
                {
                    code = "ZERO_SIZE_ELEMENT",
                    severity = "warning",
                    path,
                    element = ElementLabel(element),
                    bounds = RectToObject(bound),
                    message = "Visible leaf VisualElement has near-zero world bounds."
                });
            }

            if (visibleByDisplay && opacity <= 0.01f)
            {
                issues.Add(new
                {
                    code = "INVISIBLE_OPACITY",
                    severity = "info",
                    path,
                    element = ElementLabel(element),
                    opacity,
                    message = "VisualElement resolved opacity is near zero."
                });
            }

            if (visibleByDisplay && bound.width > 1f && bound.height > 1f &&
                (bound.xMax < 0f || bound.yMax < 0f || bound.xMin > width || bound.yMin > height))
            {
                issues.Add(new
                {
                    code = "OUTSIDE_RENDER_TARGET",
                    severity = "warning",
                    path,
                    element = ElementLabel(element),
                    bounds = RectToObject(bound),
                    message = "VisualElement is laid out outside the render target."
                });
            }

            var visibleForPaint = visibleByDisplay && opacity > 0.01f && bound.width > 1f && bound.height > 1f;
            if (visibleForPaint)
            {
                var isText = element is TextElement textElement && !string.IsNullOrWhiteSpace(textElement.text);
                if (element.childCount == 0 || isText)
                {
                    visibleLeafRecords.Add(new ElementIssueRecord
                    {
                        Element = element,
                        Path = path,
                        Bounds = bound
                    });
                }

                if (isText)
                    AddTextOverflowIssue((TextElement)element, path, bound, issues);
            }

            var index = 0;
            foreach (var child in element.Children())
            {
                var childName = string.IsNullOrWhiteSpace(child.name) ? child.GetType().Name : child.name;
                VisitForIssues(child, $"{path}/{index}:{childName}", width, height, maxItems, ref visited, issues, visibleLeafRecords);
                index++;
            }
        }

        static void AddTextOverflowIssue(TextElement element, string path, Rect bound, List<object> issues)
        {
            var text = element.text;
            if (string.IsNullOrWhiteSpace(text) || bound.width <= 1f || bound.height <= 1f)
                return;

            var fontSize = element.resolvedStyle.fontSize;
            if (float.IsNaN(fontSize) || fontSize <= 0f)
                fontSize = 12f;

            var lineHeight = fontSize * 1.2f;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var longestLineLength = lines.Length == 0 ? text.Length : lines.Max(line => line.Length);
            var approximateWidth = longestLineLength * fontSize * 0.55f;
            var approximateHeight = Mathf.Max(1, lines.Length) * lineHeight;
            var likelyWidthOverflow = approximateWidth > bound.width + 2f && lines.Length <= 1;
            var likelyHeightOverflow = approximateHeight > bound.height + 2f;
            if (!likelyWidthOverflow && !likelyHeightOverflow)
                return;

            issues.Add(new
            {
                code = likelyWidthOverflow ? "TEXT_MAY_OVERFLOW_WIDTH" : "TEXT_MAY_OVERFLOW_HEIGHT",
                severity = "warning",
                path,
                element = ElementLabel(element),
                bounds = RectToObject(bound),
                textLength = text.Length,
                approximateTextSize = new { width = approximateWidth, height = approximateHeight },
                message = likelyWidthOverflow
                    ? "TextElement text is likely wider than its resolved world bounds."
                    : "TextElement text is likely taller than its resolved world bounds."
            });
        }

        static void AddOverlapIssues(List<ElementIssueRecord> records, List<object> issues)
        {
            const int maxOverlapIssues = 20;
            var added = 0;
            for (var i = 0; i < records.Count && added < maxOverlapIssues; i++)
            {
                for (var j = i + 1; j < records.Count && added < maxOverlapIssues; j++)
                {
                    var a = records[i];
                    var b = records[j];
                    if (AreRelatedElements(a.Element, b.Element))
                        continue;

                    var intersection = Intersect(a.Bounds, b.Bounds);
                    if (intersection.width <= 0f || intersection.height <= 0f)
                        continue;

                    var intersectionArea = intersection.width * intersection.height;
                    if (intersectionArea < 16f)
                        continue;

                    var minArea = Mathf.Max(1f, Mathf.Min(a.Bounds.width * a.Bounds.height, b.Bounds.width * b.Bounds.height));
                    var ratio = intersectionArea / minArea;
                    if (ratio < 0.35f)
                        continue;

                    issues.Add(new
                    {
                        code = "VISIBLE_ELEMENTS_OVERLAP",
                        severity = ratio > 0.8f ? "warning" : "info",
                        firstPath = a.Path,
                        secondPath = b.Path,
                        first = ElementLabel(a.Element),
                        second = ElementLabel(b.Element),
                        intersection = RectToObject(intersection),
                        overlapRatio = ratio,
                        message = "Visible leaf VisualElements overlap significantly."
                    });
                    added++;
                }
            }
        }

        static bool AreRelatedElements(VisualElement a, VisualElement b)
        {
            return IsAncestorOf(a, b) || IsAncestorOf(b, a);
        }

        static bool IsAncestorOf(VisualElement possibleAncestor, VisualElement element)
        {
            var current = element?.parent;
            while (current != null)
            {
                if (current == possibleAncestor)
                    return true;
                current = current.parent;
            }

            return false;
        }

        static Rect Intersect(Rect a, Rect b)
        {
            var xMin = Mathf.Max(a.xMin, b.xMin);
            var yMin = Mathf.Max(a.yMin, b.yMin);
            var xMax = Mathf.Min(a.xMax, b.xMax);
            var yMax = Mathf.Min(a.yMax, b.yMax);
            return xMax <= xMin || yMax <= yMin
                ? Rect.zero
                : Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        static PixelStats AnalyzePixels(Texture2D texture)
        {
            var pixels = texture.GetPixels32();
            var nonTransparent = 0;
            long alphaSum = 0;
            var minX = texture.width;
            var minY = texture.height;
            var maxX = -1;
            var maxY = -1;

            for (var y = 0; y < texture.height; y++)
            {
                for (var x = 0; x < texture.width; x++)
                {
                    var pixel = pixels[y * texture.width + x];
                    alphaSum += pixel.a;
                    if (pixel.a <= 2)
                        continue;

                    nonTransparent++;
                    minX = Mathf.Min(minX, x);
                    minY = Mathf.Min(minY, y);
                    maxX = Mathf.Max(maxX, x);
                    maxY = Mathf.Max(maxY, y);
                }
            }

            return new PixelStats
            {
                Width = texture.width,
                Height = texture.height,
                TotalPixels = pixels.Length,
                NonTransparentPixels = nonTransparent,
                AverageAlpha = pixels.Length == 0 ? 0f : (float)alphaSum / (pixels.Length * 255f),
                ContentBounds = nonTransparent == 0 ? null : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1)
            };
        }

        static GameObject FindGameObject(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return null;

            var all = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(go => go != null && go.scene.IsValid())
                .ToArray();

            return all.FirstOrDefault(go => string.Equals(go.name, target, StringComparison.Ordinal)) ??
                   all.FirstOrDefault(go => GetHierarchyPath(go).Equals(target, StringComparison.OrdinalIgnoreCase)) ??
                   all.FirstOrDefault(go => go.name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string GetHierarchyPath(GameObject go)
        {
            var parts = new Stack<string>();
            var current = go.transform;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }

        static string[] NormalizeFolders(string[] folders)
        {
            if (folders == null || folders.Length == 0)
                return Array.Empty<string>();

            return folders
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Select(folder =>
                {
                    var candidate = folder.Trim().Replace('\\', '/').TrimEnd('/');
                    if (!candidate.StartsWith("Assets", StringComparison.OrdinalIgnoreCase) &&
                        !candidate.StartsWith("Packages", StringComparison.OrdinalIgnoreCase))
                    {
                        candidate = "Assets/" + candidate.TrimStart('/');
                    }

                    return candidate;
                })
                .Where(folder => AssetDatabase.IsValidFolder(folder))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static bool TryNormalizeAssetPath(string path, out string normalizedPath, out string error)
        {
            normalizedPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Asset path is empty.";
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

        static string ResolveOutputPath(CaptureUIToolkitParams parameters, string assetPath)
        {
            var directory = ResolveOutputDirectory(parameters.OutputDirectory);
            var fileName = string.IsNullOrWhiteSpace(parameters.FileName)
                ? BuildDefaultFileName("uitoolkit_capture", parameters.Tag, assetPath)
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
                "uitoolkit-captures",
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

        static object RectToObject(Rect rect)
        {
            return new { rect.x, rect.y, rect.width, rect.height, xMin = rect.xMin, yMin = rect.yMin, xMax = rect.xMax, yMax = rect.yMax };
        }

        static object ElementLabel(VisualElement element)
        {
            return new
            {
                type = element.GetType().Name,
                name = element.name,
                classes = element.GetClasses().ToArray()
            };
        }

        sealed class DocumentResolution
        {
            public bool Success;
            public string Error;
            public string AssetPath;
            public VisualTreeAsset VisualTreeAsset;
            public string Source;
            public string TargetName;
            public PanelSettings SourcePanelSettings;

            public static DocumentResolution Ok(string path, VisualTreeAsset asset, string source, string targetName, PanelSettings panelSettings) => new()
            {
                Success = true,
                AssetPath = path,
                VisualTreeAsset = asset,
                Source = source,
                TargetName = targetName,
                SourcePanelSettings = panelSettings
            };

            public static DocumentResolution Fail(string error) => new()
            {
                Success = false,
                Error = error
            };
        }

        sealed class PixelStats
        {
            public int Width;
            public int Height;
            public int TotalPixels;
            public int NonTransparentPixels;
            public float AverageAlpha;
            public RectInt? ContentBounds;

            public object ToObject() => new
            {
                width = Width,
                height = Height,
                totalPixels = TotalPixels,
                nonTransparentPixels = NonTransparentPixels,
                coverage = TotalPixels == 0 ? 0f : (float)NonTransparentPixels / TotalPixels,
                averageAlpha = AverageAlpha,
                contentBounds = ContentBounds.HasValue
                    ? new
                    {
                        ContentBounds.Value.x,
                        ContentBounds.Value.y,
                        ContentBounds.Value.width,
                        ContentBounds.Value.height,
                        xMin = ContentBounds.Value.xMin,
                        yMin = ContentBounds.Value.yMin,
                        xMax = ContentBounds.Value.xMax,
                        yMax = ContentBounds.Value.yMax
                    }
                    : null
            };
        }

        sealed class RenderPassInfo
        {
            public int PassesCompleted;
            public int EditorUpdatesWaited;
        }

        sealed class ElementIssueRecord
        {
            public VisualElement Element;
            public string Path;
            public Rect Bounds;
        }
    }
}
