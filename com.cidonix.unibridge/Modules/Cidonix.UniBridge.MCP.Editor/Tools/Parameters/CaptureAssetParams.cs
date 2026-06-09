#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum CaptureAssetAction
    {
        Capture,
        CaptureGrid,
        CaptureContactSheet
    }

    public enum CaptureAssetView
    {
        Auto,
        Iso,
        Front,
        Back,
        Left,
        Right,
        Top,
        Bottom
    }

    /// <summary>
    /// Parameters for UniBridge_CaptureAsset.
    /// </summary>
    public record CaptureAssetParams
    {
        [McpDescription("Asset capture operation. Capture renders one asset; CaptureGrid renders several assets; CaptureContactSheet renders one asset across several views/time slices into a structured contact sheet.", Required = false, Default = CaptureAssetAction.Capture)]
        public CaptureAssetAction Action { get; set; } = CaptureAssetAction.Capture;

        [McpDescription("Asset path under Assets/... or Packages/... to render into a PNG.", Required = false)]
        public string Path { get; set; }

        [McpDescription("Asset GUID, used when Path is not known.", Required = false)]
        public string Guid { get; set; }

        [McpDescription("Asset paths for CaptureGrid. Each path must be under Assets/... or Packages/....", Required = false)]
        public string[] Paths { get; set; }

        [McpDescription("Asset GUIDs for CaptureGrid, used when paths are not known.", Required = false)]
        public string[] Guids { get; set; }

        [McpDescription("Folder path for CaptureGrid search. Accepts Assets/... or Packages/...; relative paths are treated as Assets/....", Required = false)]
        public string Folder { get; set; }

        [McpDescription("Folder paths for CaptureGrid search.", Required = false)]
        public string[] Folders { get; set; }

        [McpDescription("AssetDatabase search query for CaptureGrid, such as 'cake t:Texture2D'.", Required = false)]
        public string Query { get; set; }

        [McpDescription("Alias for Query, kept for agents that naturally say SearchPattern.", Required = false)]
        public string SearchPattern { get; set; }

        [McpDescription("Optional type filters for CaptureGrid, such as Sprite, Texture2D, Material, Mesh, GameObject, or Prefab.", Required = false)]
        public string[] Types { get; set; }

        [McpDescription("PNG width in pixels. Values are clamped between 64 and 4096.", Required = false, Default = 1024)]
        public int? Width { get; set; } = 1024;

        [McpDescription("PNG height in pixels. Values are clamped between 64 and 4096.", Required = false, Default = 1024)]
        public int? Height { get; set; } = 1024;

        [McpDescription("CaptureGrid preview cell width in pixels. Values are clamped between 64 and 1024.", Required = false, Default = 256)]
        public int? CellWidth { get; set; } = 256;

        [McpDescription("CaptureGrid preview cell height in pixels. Values are clamped between 64 and 1024.", Required = false, Default = 256)]
        public int? CellHeight { get; set; } = 256;

        [McpDescription("CaptureGrid maximum number of assets to include. Values are clamped between 1 and 100.", Required = false, Default = 24)]
        public int? MaxResults { get; set; } = 24;

        [McpDescription("CaptureGrid column count. Defaults to a square-ish grid like the StitchIntoSquareGrid.", Required = false)]
        public int? Columns { get; set; }

        [McpDescription("Draw numeric index badges over CaptureGrid or CaptureContactSheet cells. Full label/path/view mapping is always returned in metadata.", Required = false, Default = true)]
        public bool? IncludeLabels { get; set; } = true;

        [McpDescription("For CaptureContactSheet, number of time slices per view. Values are clamped to 1..30 and total cells are capped for safety.", Required = false, Default = 1)]
        public int? SeriesCount { get; set; }

        [McpDescription("For CaptureContactSheet, best-effort delay between time slices in seconds. This blocks the Editor command while rendering.", Required = false, Default = 0)]
        public float? SeriesIntervalSeconds { get; set; }

        [McpDescription("Camera direction for the asset preview. Auto uses Front for sprites/textures and Iso for 3D assets/materials.", Required = false, Default = CaptureAssetView.Auto)]
        public CaptureAssetView View { get; set; } = CaptureAssetView.Auto;

        [McpDescription("For CaptureContactSheet, camera directions to render into one stitched PNG. Defaults to Front for 2D assets, or Iso, Front, Top, Right for 3D assets.", Required = false)]
        public CaptureAssetView[] Views { get; set; }

        [McpDescription("Force orthographic camera. Defaults to true for deterministic asset previews.", Required = false, Default = true)]
        public bool? Orthographic { get; set; } = true;

        [McpDescription("Bounds padding multiplier. Values are clamped between 1.0 and 3.0.", Required = false, Default = 1.4)]
        public float? Padding { get; set; } = 1.4f;

        [McpDescription("Preserve alpha in the PNG. Defaults to true for reusable asset previews.", Required = false, Default = true)]
        public bool? TransparentBackground { get; set; } = true;

        [McpDescription("Background color as [r,g,b] or [r,g,b,a]. Used when TransparentBackground is false. Default dark neutral.", Required = false)]
        public float[] BackgroundColor { get; set; }

        [McpDescription("Optional folder where PNG files should be written. Defaults to ~/.unibridge/asset-captures/<project>.", Required = false)]
        public string OutputDirectory { get; set; }

        [McpDescription("Optional file name. .png is appended if missing. Defaults to asset_capture, optional tag, and timestamp.", Required = false)]
        public string FileName { get; set; }

        [McpDescription("Optional human-readable tag added to the generated file name and returned metadata.", Required = false)]
        public string Tag { get; set; }

        [McpDescription("Select the source asset after capture.", Required = false, Default = false)]
        public bool Select { get; set; }

        [McpDescription("Advance animated/VFX prefab/model previews before capture by this many milliseconds. Values are clamped to 0..30000.", Required = false, Default = 0)]
        public int? AdvanceMs { get; set; }

        [McpDescription("When AdvanceMs > 0, simulate ParticleSystem components in prefab/model previews before rendering. Defaults to true when AdvanceMs is set.", Required = false)]
        public bool? SimulateParticles { get; set; }

        [McpDescription("When AdvanceMs > 0, sample Animator/Animation clips in prefab/model previews before rendering. Defaults to true when AdvanceMs is set.", Required = false)]
        public bool? SampleAnimations { get; set; }

        [McpDescription("RenderTexture readback mode for 3D/prefab previews. Immediate uses Texture2D.ReadPixels. GpuReadback uses synchronous AsyncGPUReadback with ReadPixels fallback.", Required = false, Default = CaptureReadbackMode.Immediate)]
        public CaptureReadbackMode ReadbackMode { get; set; } = CaptureReadbackMode.Immediate;
    }
}
