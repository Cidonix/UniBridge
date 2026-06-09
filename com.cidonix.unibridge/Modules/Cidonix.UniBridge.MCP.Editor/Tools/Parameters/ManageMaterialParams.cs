#nullable disable
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters
{
    /// <summary>
    /// Material operations for UniBridge_ManageMaterial.
    /// </summary>
    public enum MaterialAction
    {
        Inspect,
        Validate,
        CreateOrUpdate,
        SetShader,
        SetProperties,
        ApplyPreset
    }

    /// <summary>
    /// High-level material presets for common agent workflows.
    /// </summary>
    public enum MaterialPreset
    {
        None,
        URPLit,
        URPUnlit,
        Standard,
        UnlitColor,
        SpriteDefault,
        UIDefault,
        Transparent,
        Cutout
    }

    /// <summary>
    /// Parameters for UniBridge_ManageMaterial.
    /// </summary>
    public record ManageMaterialParams
    {
        [McpDescription("Operation to perform: Inspect, Validate, CreateOrUpdate, SetShader, SetProperties, or ApplyPreset.", Required = false, Default = MaterialAction.Inspect)]
        public MaterialAction Action { get; set; } = MaterialAction.Inspect;

        [McpDescription("Material asset path. Accepts Assets/... or Packages/...; relative paths are treated as Assets/.... Missing .mat extension is appended for mutating actions.", Required = false)]
        public string Path { get; set; }

        [McpDescription("Material asset GUID, used when Path is not known.", Required = false)]
        public string Guid { get; set; }

        [McpDescription("Shader path or shader name. Asset paths are loaded through AssetDatabase; other values use Shader.Find.", Required = false)]
        public string Shader { get; set; }

        [McpDescription("Shader property patch and optional material settings. Direct shader property keys such as _Color are supported. structured { shader: { shaderPath, props } } is also supported.", Required = false)]
        public JObject Properties { get; set; }

        [McpDescription("Optional high-level preset. Presets can be combined with Shader and Properties; explicit Properties are applied last.", Required = false, Default = MaterialPreset.None)]
        public MaterialPreset Preset { get; set; } = MaterialPreset.None;

        [McpDescription("Preview mutating operations without creating or changing material assets. Default false for direct calls; UniBridge_BatchActions dry-runs by default.", Required = false, Default = false)]
        public bool DryRun { get; set; }

        [McpDescription("Allow mutating materials under Packages/.... Default false to avoid accidentally changing package cache or embedded package files.", Required = false, Default = false)]
        public bool AllowPackages { get; set; }

        [McpDescription("Include shader property metadata in Inspect/response output. Default true.", Required = false, Default = true)]
        public bool IncludeShaderProperties { get; set; } = true;

        [McpDescription("Include current shader property values in Inspect/response output. Default true.", Required = false, Default = true)]
        public bool IncludeValues { get; set; } = true;

        [McpDescription("Include hidden shader properties in metadata and validation. Default false.", Required = false, Default = false)]
        public bool IncludeHiddenProperties { get; set; }

        [McpDescription("Maximum shader properties returned by Inspect. Default 200.", Required = false, Default = 200)]
        public int MaxShaderProperties { get; set; } = 200;

        [McpDescription("Optional texture path used by presets. Applied to the first compatible main texture property such as _BaseMap or _MainTex.", Required = false)]
        public string TexturePath { get; set; }

        [McpDescription("Optional color used by presets. Accepts {r,g,b,a}, [r,g,b,a], or #RRGGBB/#RRGGBBAA.", Required = false)]
        public JToken Color { get; set; }

        [McpDescription("Optional enableInstancing material setting.", Required = false)]
        public bool? EnableInstancing { get; set; }

        [McpDescription("Optional doubleSidedGI material setting.", Required = false)]
        public bool? DoubleSidedGI { get; set; }

        [McpDescription("Optional renderQueue material setting. Use -1 for shader default.", Required = false)]
        public int? RenderQueue { get; set; }

        [McpDescription("Optional shader keyword replacement list.", Required = false)]
        public string[] Keywords { get; set; }

        [McpDescription("Shader keywords to enable.", Required = false)]
        public string[] EnableKeywords { get; set; }

        [McpDescription("Shader keywords to disable.", Required = false)]
        public string[] DisableKeywords { get; set; }

        [McpDescription("Select the material asset after a successful mutating operation.", Required = false, Default = false)]
        public bool Select { get; set; }
    }
}
