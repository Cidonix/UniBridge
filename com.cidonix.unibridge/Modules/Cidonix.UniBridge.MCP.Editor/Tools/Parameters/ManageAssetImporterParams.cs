#nullable disable
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters
{
    /// <summary>
    /// AssetImporter operations for UniBridge_ManageAssetImporter.
    /// </summary>
    public enum AssetImporterAction
    {
        Inspect,
        SetProperties,
        ApplyPreset,
        Reimport
    }

    /// <summary>
    /// High-level importer presets for common agent workflows.
    /// </summary>
    public enum AssetImporterPreset
    {
        None,
        TextureSprite2D,
        TextureUI,
        TextureReadable,
        TextureNormalMap,
        ModelStatic,
        ModelAnimated,
        Audio2D,
        AudioStreaming
    }

    /// <summary>
    /// Parameters for UniBridge_ManageAssetImporter.
    /// </summary>
    public record ManageAssetImporterParams
    {
        [McpDescription("Operation to perform: Inspect, SetProperties, ApplyPreset, or Reimport.", Required = false, Default = AssetImporterAction.Inspect)]
        public AssetImporterAction Action { get; set; } = AssetImporterAction.Inspect;

        [McpDescription("Asset path whose AssetImporter should be inspected or changed. Accepts Assets/... or Packages/...; relative paths are treated as Assets/....", Required = false)]
        public string Path { get; set; }

        [McpDescription("Asset GUID, used when Path is not known.", Required = false)]
        public string Guid { get; set; }

        [McpDescription("Optional expected importer type, e.g. UnityEditor.TextureImporter, TextureImporter, ModelImporter, or AudioImporter. If supplied, it must match the asset importer.", Required = false)]
        public string ImporterType { get; set; }

        [McpDescription("Generic importer property patch. Keys can be public importer property names or SerializedProperty paths. Values may be strings, numbers, booleans, arrays, objects, or asset paths for object references.", Required = false)]
        public JObject Properties { get; set; }

        [McpDescription("Optional high-level preset for ApplyPreset. Presets can be combined with Properties; Properties are applied last.", Required = false, Default = AssetImporterPreset.None)]
        public AssetImporterPreset Preset { get; set; } = AssetImporterPreset.None;

        [McpDescription("Preview mutating operations without applying importer changes. Default false for direct calls; UniBridge_BatchActions dry-runs by default.", Required = false, Default = false)]
        public bool DryRun { get; set; }

        [McpDescription("Call AssetImporter.SaveAndReimport after changes. Default true for SetProperties/ApplyPreset/Reimport.", Required = false, Default = true)]
        public bool Reimport { get; set; } = true;

        [McpDescription("Allow mutating assets under Packages/.... Default false to avoid accidentally changing package cache or embedded package files.", Required = false, Default = false)]
        public bool AllowPackages { get; set; }

        [McpDescription("Include SerializedObject/SerializedProperty data in Inspect output. Default true.", Required = false, Default = true)]
        public bool IncludeSerializedProperties { get; set; } = true;

        [McpDescription("Maximum serialized properties returned by Inspect. Default 200.", Required = false, Default = 200)]
        public int MaxSerializedProperties { get; set; } = 200;

        [McpDescription("Sprite pixels per unit used by TextureSprite2D/TextureUI presets when provided. Default 100.", Required = false, Default = 100)]
        public float? SpritePixelsPerUnit { get; set; }

        [McpDescription("Texture max size used by texture presets when provided.", Required = false)]
        public int? MaxTextureSize { get; set; }

        [McpDescription("Texture compression quality used by texture presets when provided.", Required = false)]
        public int? CompressionQuality { get; set; }

        [McpDescription("Model global scale used by model presets when provided. Default 1.", Required = false, Default = 1)]
        public float? GlobalScale { get; set; }

        [McpDescription("Importer readable/read-write flag used by texture/model presets when provided.", Required = false)]
        public bool? IsReadable { get; set; }

        [McpDescription("Audio quality used by AudioStreaming preset when provided. Default 0.65.", Required = false, Default = 0.65)]
        public float? AudioQuality { get; set; }
    }
}
