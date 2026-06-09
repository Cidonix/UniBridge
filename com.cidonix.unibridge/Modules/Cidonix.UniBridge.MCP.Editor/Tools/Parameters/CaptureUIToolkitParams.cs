#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum CaptureUIToolkitAction
    {
        Capture,
        Inspect,
        ListUxml
    }

    /// <summary>
    /// Parameters for UniBridge_CaptureUIToolkit.
    /// </summary>
    public record CaptureUIToolkitParams
    {
        [McpDescription("UI Toolkit operation. Capture renders a UXML/UIDocument to PNG; Inspect renders offscreen and returns the resolved VisualElement tree; ListUxml searches VisualTreeAsset assets.", Required = false, Default = CaptureUIToolkitAction.Capture)]
        public CaptureUIToolkitAction Action { get; set; } = CaptureUIToolkitAction.Capture;

        [McpDescription("UXML VisualTreeAsset path under Assets/... or Packages/....", Required = false)]
        public string Path { get; set; }

        [McpDescription("VisualTreeAsset GUID, used when Path is not known.", Required = false)]
        public string Guid { get; set; }

        [McpDescription("Optional scene GameObject target with a UIDocument component. When provided, UniBridge captures that document's VisualTreeAsset and panel theme.", Required = false)]
        public string Target { get; set; }

        [McpDescription("Search query for ListUxml or for resolving a VisualTreeAsset when Path/Guid/Target are omitted.", Required = false)]
        public string Query { get; set; }

        [McpDescription("Folders to search for ListUxml/Query resolution. Defaults to Assets and Packages.", Required = false)]
        public string[] Folders { get; set; }

        [McpDescription("PNG width in pixels. Values are clamped between 64 and 4096.", Required = false, Default = 1280)]
        public int? Width { get; set; } = 1280;

        [McpDescription("PNG height in pixels. Values are clamped between 64 and 4096.", Required = false, Default = 720)]
        public int? Height { get; set; } = 720;

        [McpDescription("RenderTexture readback mode. Immediate uses Texture2D.ReadPixels. GpuReadback uses a synchronous AsyncGPUReadback request with ReadPixels fallback for UI Toolkit target textures.", Required = false, Default = CaptureReadbackMode.Immediate)]
        public CaptureReadbackMode ReadbackMode { get; set; } = CaptureReadbackMode.Immediate;

        [McpDescription("Number of forced UI Toolkit render/update passes before readback. Values are clamped between 1 and 8; the default two-pass warmup is best for Unity 6 targetTexture captures.", Required = false, Default = 2)]
        public int? RenderPasses { get; set; } = 2;

        [McpDescription("PanelSettings scale. Defaults to 1 / EditorGUIUtility.pixelsPerPoint, matching the UI Toolkit capture behavior.", Required = false)]
        public float? PanelScale { get; set; }

        [McpDescription("Preserve alpha in the PNG. Defaults to true.", Required = false, Default = true)]
        public bool? TransparentBackground { get; set; } = true;

        [McpDescription("Background color as [r,g,b] or [r,g,b,a]. Used when TransparentBackground is false.", Required = false)]
        public float[] BackgroundColor { get; set; }

        [McpDescription("Optional ThemeStyleSheet path. When omitted, UniBridge attempts to reuse the target panel theme, UI Builder theme, or UnityDefaultRuntimeTheme.tss when available.", Required = false)]
        public string ThemeStyleSheetPath { get; set; }

        [McpDescription("Include resolved VisualElement tree metadata in Capture/Inspect responses.", Required = false, Default = true)]
        public bool IncludeTree { get; set; } = true;

        [McpDescription("Include layout/visibility issue hints for the rendered VisualElement tree.", Required = false, Default = true)]
        public bool IncludeIssues { get; set; } = true;

        [McpDescription("Maximum VisualElement tree depth to return. Values are clamped between 1 and 32.", Required = false, Default = 8)]
        public int? MaxTreeDepth { get; set; } = 8;

        [McpDescription("Maximum VisualElement nodes to include in tree/issue metadata. Values are clamped between 1 and 1000.", Required = false, Default = 200)]
        public int? MaxTreeItems { get; set; } = 200;

        [McpDescription("Maximum ListUxml results. Values are clamped between 1 and 200.", Required = false, Default = 50)]
        public int? Limit { get; set; } = 50;

        [McpDescription("Optional folder where PNG files should be written. Defaults to ~/.unibridge/uitoolkit-captures/<project>.", Required = false)]
        public string OutputDirectory { get; set; }

        [McpDescription("Optional PNG file name. .png is appended if missing.", Required = false)]
        public string FileName { get; set; }

        [McpDescription("Optional human-readable tag added to generated file names and returned metadata.", Required = false)]
        public string Tag { get; set; }

        [McpDescription("Select the resolved VisualTreeAsset after capture/inspect.", Required = false, Default = false)]
        public bool Select { get; set; }
    }
}
