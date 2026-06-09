using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum CaptureViewAction
    {
        CaptureSceneView,
        CaptureGameView,
        CaptureGameCamera,
        CaptureSelection,
        CaptureObject,
        CapturePrefabStage,
        CaptureSceneOverview,
        CaptureAroundObject,
        CaptureSeries,
        CaptureContactSheet,
        CaptureDiff,
        ClearCaptures,
        ListCameras
    }

    public enum CaptureViewDirection
    {
        Current,
        Iso,
        Front,
        Back,
        Left,
        Right,
        Top,
        Bottom
    }

    public enum CaptureViewZoom
    {
        Close,
        Normal,
        Far
    }

    public enum CaptureReadbackMode
    {
        Immediate,
        GpuReadback
    }

    /// <summary>
    /// Parameters for the UniBridge_CaptureView tool.
    /// </summary>
    public record CaptureViewParams
    {
        [McpDescription("Capture operation: CaptureSceneView, CaptureGameView, CaptureGameCamera, CaptureSelection, CaptureObject, CapturePrefabStage, CaptureSceneOverview, CaptureAroundObject, CaptureSeries, CaptureContactSheet, CaptureDiff, ClearCaptures, or ListCameras.", Required = false, Default = CaptureViewAction.CaptureSceneView)]
        public CaptureViewAction Action { get; set; } = CaptureViewAction.CaptureSceneView;

        [McpDescription("PNG width in pixels. Values are clamped between 64 and 4096. CaptureGameView returns the actual Game View screenshot size.", Required = false, Default = 1280)]
        public int? Width { get; set; } = 1280;

        [McpDescription("PNG height in pixels. Values are clamped between 64 and 4096. CaptureGameView returns the actual Game View screenshot size.", Required = false, Default = 720)]
        public int? Height { get; set; } = 720;

        [McpDescription("Scene object name, hierarchy path, or entity/instance ID to center and frame when capturing the Scene View. If provided but not found, the capture fails.", Required = false)]
        public string Target { get; set; }

        [McpDescription("How to resolve Target or Camera: by_name, by_id, by_path, or by_id_or_name_or_path.", Required = false, Default = "by_id_or_name_or_path")]
        public string SearchMethod { get; set; } = "by_id_or_name_or_path";

        [McpDescription("Scene View camera direction. Current preserves the current Scene View direction; other values frame Target or the current Scene View pivot from that direction.", Required = false, Default = CaptureViewDirection.Current)]
        public CaptureViewDirection View { get; set; } = CaptureViewDirection.Current;

        [McpDescription("For CaptureContactSheet, Scene View directions to render into one stitched PNG. Accepts an array or comma-separated values: Current, Iso, Front, Back, Left, Right, Top, Bottom. Defaults to Iso, Front, Top, Right.", Required = false)]
        public CaptureViewDirection[] Views { get; set; }

        [McpDescription("Scene View zoom level when framing a target.", Required = false, Default = CaptureViewZoom.Normal)]
        public CaptureViewZoom Zoom { get; set; } = CaptureViewZoom.Normal;

        [McpDescription("Force orthographic Scene View capture. If omitted, the current Scene View projection is preserved.", Required = false)]
        public bool? Orthographic { get; set; }

        [McpDescription("Camera name, hierarchy path, or entity/instance ID for CaptureGameCamera. When empty, UniBridge uses Main Camera or the first enabled Camera.", Required = false)]
        public string Camera { get; set; }

        [McpDescription("Optional folder where PNG files should be written. Defaults to ~/.unibridge/captures/<project>.", Required = false)]
        public string OutputDirectory { get; set; }

        [McpDescription("Optional file name. .png is appended if missing. Defaults to action, optional tag, and timestamp.", Required = false)]
        public string FileName { get; set; }

        [McpDescription("Optional human-readable tag added to the generated file name and returned metadata.", Required = false)]
        public string Tag { get; set; }

        [McpDescription("Preserve alpha in the PNG. Defaults to false so captures match Unity Game View and Scene View opaque presentation.", Required = false, Default = false)]
        public bool? TransparentBackground { get; set; }

        [McpDescription("Draw AI-readable visual hints such as target bounds, nearby object bounds, and short labels into the PNG.", Required = false, Default = false)]
        public bool? Overlay { get; set; }

        [McpDescription("Write visual hints as a separate transparent PNG layer and keep the main capture clean. When true, UniBridge also writes a composite PNG unless CompositeOverlay is false.", Required = false, Default = false)]
        public bool? SeparateOverlay { get; set; }

        [McpDescription("When SeparateOverlay is true, also write a convenience composite PNG combining the clean capture and overlay layer.", Required = false, Default = true)]
        public bool? CompositeOverlay { get; set; }

        [McpDescription("Include nearby object metadata around the target. Defaults to true for target-based captures.", Required = false)]
        public bool? IncludeNearbyObjects { get; set; }

        [McpDescription("World-space radius for nearby object metadata and CaptureAroundObject context.", Required = false, Default = 8)]
        public float? NearbyRadius { get; set; }

        [McpDescription("Maximum number of visible/nearby objects returned in metadata and labeled in overlays.", Required = false, Default = 40)]
        public int? MaxObjects { get; set; }

        [McpDescription("Number of frames for CaptureSeries, or time slices per view for CaptureContactSheet. Values are clamped to 1..30; CaptureContactSheet defaults to 1 when omitted.", Required = false, Default = 3)]
        public int? SeriesCount { get; set; }

        [McpDescription("Best-effort delay between CaptureSeries frames or CaptureContactSheet time slices in seconds. This blocks the Editor command while capturing.", Required = false, Default = 0)]
        public float? SeriesIntervalSeconds { get; set; }

        [McpDescription("For CaptureContactSheet, optional column count for the stitched PNG grid. Defaults to a compact square-ish layout.", Required = false)]
        public int? ContactSheetColumns { get; set; }

        [McpDescription("For CaptureContactSheet, draw compact labels such as ISO 01 or FRONT 02 into each grid cell. Defaults to true.", Required = false, Default = true)]
        public bool? IncludeContactSheetLabels { get; set; }

        [McpDescription("Baseline PNG path for CaptureDiff.", Required = false)]
        public string BaselinePath { get; set; }

        [McpDescription("Comparison PNG path for CaptureDiff. When omitted, UniBridge reports an error instead of guessing.", Required = false)]
        public string ComparePath { get; set; }

        [McpDescription("For ClearCaptures, delete all PNG captures in the selected capture directory.", Required = false)]
        public bool? DeleteAll { get; set; }

        [McpDescription("For ClearCaptures, keep the newest N captures and delete older captures.", Required = false)]
        public int? KeepLatest { get; set; }

        [McpDescription("For ClearCaptures, delete captures older than this many days.", Required = false)]
        public float? MaxAgeDays { get; set; }

        [McpDescription("Advance animated/VFX targets before capture by this many milliseconds. Values are clamped to 0..30000.", Required = false, Default = 0)]
        public int? AdvanceMs { get; set; }

        [McpDescription("When AdvanceMs > 0, simulate ParticleSystem components under capture targets before rendering. Defaults to true when AdvanceMs is set.", Required = false)]
        public bool? SimulateParticles { get; set; }

        [McpDescription("When AdvanceMs > 0, sample Animator/Animation clips under capture targets before rendering. Defaults to true when AdvanceMs is set.", Required = false)]
        public bool? SampleAnimations { get; set; }

        [McpDescription("RenderTexture readback mode. Immediate uses Texture2D.ReadPixels. GpuReadback uses a synchronous AsyncGPUReadback request with ReadPixels fallback for SRP-heavy captures.", Required = false, Default = CaptureReadbackMode.Immediate)]
        public CaptureReadbackMode ReadbackMode { get; set; } = CaptureReadbackMode.Immediate;

        [McpDescription("For CaptureGameView, maximum milliseconds to wait for Unity to finish writing the exact Game View screenshot.", Required = false, Default = 5000)]
        public int? ScreenshotTimeoutMs { get; set; } = 5000;
    }
}
