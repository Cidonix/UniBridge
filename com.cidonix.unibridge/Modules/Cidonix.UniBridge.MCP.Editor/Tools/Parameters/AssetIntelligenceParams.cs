using System;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry.Parameters
{
    /// <summary>
    /// Read-only asset intelligence operations for UniBridge_AssetIntelligence.
    /// </summary>
    public enum AssetIntelligenceAction
    {
        /// <summary>
        /// Search project assets with Unity AssetDatabase filters plus lightweight ranking.
        /// </summary>
        Search,

        /// <summary>
        /// Inspect one asset in detail.
        /// </summary>
        Inspect,

        /// <summary>
        /// Read a text-like asset with optional slicing.
        /// </summary>
        Read,

        /// <summary>
        /// List dependencies for an asset.
        /// </summary>
        Dependencies,

        /// <summary>
        /// Find assets that depend on a target asset.
        /// </summary>
        Dependents,

        /// <summary>
        /// Summarize asset counts, largest files, recent files, and folder/type distribution.
        /// </summary>
        Stats,

        /// <summary>
        /// List asset type counts under a scope.
        /// </summary>
        Types,

        /// <summary>
        /// Inspect selected assets from the Project view.
        /// </summary>
        Selection,

        /// <summary>
        /// Save or return a PNG preview for an asset.
        /// </summary>
        Preview,

        /// <summary>
        /// Serialize assets into AI-friendly upload envelopes with bounded content.
        /// </summary>
        Serialize,

        /// <summary>
        /// Alias for Serialize focused on snapshot-style asset context.
        /// </summary>
        Snapshot,

        /// <summary>
        /// Build one agent-oriented context envelope for assets, combining summary, text slices, serialized data, and missing-path suggestions.
        /// </summary>
        Context,

        /// <summary>
        /// Build or query a cached graph of asset dependencies and dependents.
        /// </summary>
        ReferenceGraph,

        /// <summary>
        /// Estimate the impact of changing, moving, renaming, or deleting an asset.
        /// </summary>
        Impact,

        /// <summary>
        /// Resolve a missing or mistyped asset path with fuzzy suggestions.
        /// </summary>
        ResolveMissing
    }

    /// <summary>
    /// Sort modes for asset intelligence search and summaries.
    /// </summary>
    public enum AssetIntelligenceSortMode
    {
        Relevance,
        Path,
        Name,
        Type,
        Extension,
        SizeAscending,
        SizeDescending,
        ModifiedAscending,
        ModifiedDescending
    }

    /// <summary>
    /// Controls how asset previews are returned.
    /// </summary>
    public enum AssetPreviewOutputMode
    {
        None,
        File,
        Base64,
        Both
    }

    /// <summary>
    /// Controls how much asset content is serialized.
    /// </summary>
    public enum AssetSerializationMode
    {
        Minimal,
        Standard,
        Full
    }

    /// <summary>
    /// Controls which parts of an asset are emphasized by Context.
    /// </summary>
    public enum AssetContextProfile
    {
        Auto,
        Summary,
        Text,
        Serialized,
        Deep
    }

    /// <summary>
    /// A line range used by Read/Context to return several precise text slices in one call.
    /// </summary>
    public record AssetTextChunk
    {
        [McpDescription("First line to include, 1-based.", Required = true)]
        public int StartLine { get; set; }

        [McpDescription("Last line to include, 1-based. If omitted or lower than StartLine, LineCount is used.", Required = false)]
        public int EndLine { get; set; }

        [McpDescription("Line count to include when EndLine is omitted. Defaults to 80.", Required = false, Default = 80)]
        public int LineCount { get; set; } = 80;
    }

    /// <summary>
    /// Parameters for UniBridge_AssetIntelligence.
    /// </summary>
    public record AssetIntelligenceParams
    {
        [McpDescription("Operation to perform: Search, Inspect, Read, Dependencies, Dependents, Stats, Types, Selection, Preview, Serialize, Snapshot, Context, ReferenceGraph, Impact, or ResolveMissing.", Required = false, Default = AssetIntelligenceAction.Search)]
        public AssetIntelligenceAction Action { get; set; } = AssetIntelligenceAction.Search;

        [McpDescription("Natural search text or AssetDatabase query fragment. Examples: 'player controller', 't:Prefab enemy', 'l:ui'.", Required = false)]
        public string Query { get; set; }

        [McpDescription("Alias for Query, kept for agents that naturally say SearchPattern.", Required = false)]
        public string SearchPattern { get; set; }

        [McpDescription("Asset path or folder scope. Accepts Assets/..., Packages/..., unity://path/..., or a path relative to Assets/.", Required = false)]
        public string Path { get; set; }

        [McpDescription("Asset GUID for Inspect/Read/Dependencies/Dependents/Preview when path is not known.", Required = false)]
        public string Guid { get; set; }

        [McpDescription("Optional paths for multi-asset inspection, dependency checks, or search scopes.", Required = false)]
        public string[] Paths { get; set; }

        [McpDescription("Asset type filters. Examples: Prefab, Texture2D, Material, AudioClip, SceneAsset, MonoScript.", Required = false)]
        public string[] Types { get; set; }

        [McpDescription("File extension filters without or with dot. Examples: cs, prefab, png, mat.", Required = false)]
        public string[] Extensions { get; set; }

        [McpDescription("Unity asset label filters.", Required = false)]
        public string[] Labels { get; set; }

        [McpDescription("Include Packages/... assets. Default false keeps results focused on Assets/.", Required = false, Default = false)]
        public bool IncludePackages { get; set; }

        [McpDescription("Include folder assets in results.", Required = false, Default = false)]
        public bool IncludeFolders { get; set; }

        [McpDescription("Include hidden/internal Unity assets. Default false.", Required = false, Default = false)]
        public bool IncludeHidden { get; set; }

        [McpDescription("Include sub-assets when inspecting an asset.", Required = false, Default = false)]
        public bool IncludeSubAssets { get; set; }

        [McpDescription("Include dependency summary on Inspect/Search results.", Required = false, Default = false)]
        public bool IncludeDependencies { get; set; }

        [McpDescription("Include dependents summary on Inspect results. Can be expensive on large projects.", Required = false, Default = false)]
        public bool IncludeDependents { get; set; }

        [McpDescription("Include AssetImporter metadata where available.", Required = false, Default = true)]
        public bool IncludeImporter { get; set; } = true;

        [McpDescription("Generate an asset preview where Unity can provide one.", Required = false, Default = false)]
        public bool IncludePreview { get; set; }

        [McpDescription("Preview output mode: None, File, Base64, or Both. File writes under ~/.unibridge/asset-previews/<project>.", Required = false, Default = AssetPreviewOutputMode.File)]
        public AssetPreviewOutputMode PreviewMode { get; set; } = AssetPreviewOutputMode.File;

        [McpDescription("Requested preview size in pixels. Unity previews may be smaller depending on asset type.", Required = false, Default = 256)]
        public int PreviewSize { get; set; } = 256;

        [McpDescription("Maximum number of results/items returned.", Required = false, Default = 50)]
        public int Limit { get; set; } = 50;

        [McpDescription("Page number for search results, 1-based.", Required = false, Default = 1)]
        public int Page { get; set; } = 1;

        [McpDescription("Sort mode for search and summaries.", Required = false, Default = AssetIntelligenceSortMode.Relevance)]
        public AssetIntelligenceSortMode SortBy { get; set; } = AssetIntelligenceSortMode.Relevance;

        [McpDescription("When true, query terms must match name/path more strictly.", Required = false, Default = false)]
        public bool Exact { get; set; }

        [McpDescription("Regex/text pattern for Read to return a window around the first match.", Required = false)]
        public string Pattern { get; set; }

        [McpDescription("Starting line for Read, 1-based.", Required = false, Default = 1)]
        public int StartLine { get; set; } = 1;

        [McpDescription("Number of lines to read. -1 means all remaining lines, capped by MaxTextChars.", Required = false, Default = -1)]
        public int LineCount { get; set; } = -1;

        [McpDescription("Tail line count for Read. Takes precedence over StartLine/LineCount when > 0.", Required = false, Default = 0)]
        public int TailLines { get; set; }

        [McpDescription("Head byte count for Read. Takes precedence over line slicing when > 0.", Required = false, Default = 0)]
        public int HeadBytes { get; set; }

        [McpDescription("Optional multiple line ranges for Read/Context. Useful when an agent needs a few exact regions from a text-like asset in one call.", Required = false)]
        public AssetTextChunk[] Chunks { get; set; }

        [McpDescription("Maximum text characters returned by Read.", Required = false, Default = 60000)]
        public int MaxTextChars { get; set; } = 60000;

        [McpDescription("Recursive dependency scan. Default true.", Required = false, Default = true)]
        public bool Recursive { get; set; } = true;

        [McpDescription("Maximum assets scanned for Dependents/Stats. Keeps large projects responsive.", Required = false, Default = 8000)]
        public int MaxScanAssets { get; set; } = 8000;

        [McpDescription("Refresh the cached asset reference index before ReferenceGraph, Impact, or indexed Dependents.", Required = false, Default = false)]
        public bool RefreshReferenceIndex { get; set; }

        [McpDescription("Use the cached reference index for Dependents instead of a direct scan. ReferenceGraph and Impact always use the index.", Required = false, Default = false)]
        public bool UseReferenceIndex { get; set; }

        [McpDescription("Include explicit dependency edge samples in ReferenceGraph output.", Required = false, Default = false)]
        public bool IncludeReferenceEdges { get; set; }

        [McpDescription("Maximum explicit reference edges returned by ReferenceGraph.", Required = false, Default = 200)]
        public int MaxReferenceEdges { get; set; } = 200;

        [McpDescription("Operation name for Impact reports, such as Modify, Move, Rename, Delete, or Reimport.", Required = false, Default = "Modify")]
        public string ImpactOperation { get; set; } = "Modify";

        [McpDescription("Include fuzzy suggestions when a requested asset path cannot be resolved.", Required = false, Default = true)]
        public bool SuggestSimilar { get; set; } = true;

        [McpDescription("Maximum fuzzy suggestions for missing or ambiguous asset paths.", Required = false, Default = 5)]
        public int MaxSuggestions { get; set; } = 5;

        [McpDescription("Serialization detail for Serialize/Snapshot: Minimal, Standard, or Full.", Required = false, Default = AssetSerializationMode.Standard)]
        public AssetSerializationMode SerializeMode { get; set; } = AssetSerializationMode.Standard;

        [McpDescription("Context profile for Action=Context: Auto, Summary, Text, Serialized, or Deep.", Required = false, Default = AssetContextProfile.Auto)]
        public AssetContextProfile ContextProfile { get; set; } = AssetContextProfile.Auto;

        [McpDescription("When Action=Context cannot resolve a requested asset, include a serialized context payload for the best fuzzy suggestion when available.", Required = false, Default = true)]
        public bool IncludeBestSuggestionContext { get; set; } = true;

        [McpDescription("Include SerializedObject/SerializedProperty data where Unity exposes it. Default true.", Required = false, Default = true)]
        public bool IncludeSerializedProperties { get; set; } = true;

        [McpDescription("Include hierarchy data for prefab assets and the active scene. Default true.", Required = false, Default = true)]
        public bool IncludeHierarchy { get; set; } = true;

        [McpDescription("Include raw text/YAML for text-like assets. Default true for text assets.", Required = false, Default = true)]
        public bool IncludeRawText { get; set; } = true;

        [McpDescription("Maximum serialized properties per object/component in Serialize/Snapshot.", Required = false, Default = 250)]
        public int MaxSerializedProperties { get; set; } = 250;

        [McpDescription("Maximum hierarchy depth for prefab/scene serialization.", Required = false, Default = 5)]
        public int MaxSerializedDepth { get; set; } = 5;

        [McpDescription("Maximum hierarchy/sub-asset items for Serialize/Snapshot.", Required = false, Default = 200)]
        public int MaxSerializedItems { get; set; } = 200;
    }
}
