#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum UnitySearchAction
    {
        Search,
        Resolve,
        Selection
    }

    public enum UnitySearchSortMode
    {
        Relevance,
        Source,
        Name,
        Path,
        Type
    }

    public enum UnitySearchBackend
    {
        UniBridge,
        NativeSearchService,
        Hybrid
    }

    /// <summary>
    /// Parameters for UniBridge_UnitySearch.
    /// </summary>
    public record UnitySearchParams
    {
        [McpDescription("Operation to perform: Search, Resolve, or Selection.", Required = false, Default = UnitySearchAction.Search)]
        public UnitySearchAction Action { get; set; } = UnitySearchAction.Search;

        [McpDescription("Search text. Matches assets, scene object names/paths/components, scripts, loaded types, shaders, and menu items.", Required = false)]
        public string Query { get; set; }

        [McpDescription("Search sources. Values: All, Assets, SceneObjects, Scripts, Types, Shaders, Menus.", Required = false)]
        public string[] Sources { get; set; }

        [McpDescription("Search backend. UniBridge uses deterministic AssetDatabase/scene scans; NativeSearchService uses UnityEditor.Search asset/scene providers; Hybrid combines both.", Required = false, Default = UnitySearchBackend.UniBridge)]
        public UnitySearchBackend Backend { get; set; } = UnitySearchBackend.UniBridge;

        [McpDescription("Timeout in milliseconds for NativeSearchService backend requests.", Required = false, Default = 5000)]
        public int? NativeTimeoutMs { get; set; } = 5000;

        [McpDescription("Optional asset/folder path or scene target hint. Asset/folder paths scope asset/script/shader searches.", Required = false)]
        public string Path { get; set; }

        [McpDescription("Optional asset/folder paths for asset/script/shader search scopes.", Required = false)]
        public string[] Paths { get; set; }

        [McpDescription("Optional asset GUID for direct asset resolution.", Required = false)]
        public string Guid { get; set; }

        [McpDescription("Optional scene GameObject target hint for direct scene-object resolution.", Required = false)]
        public string Target { get; set; }

        [McpDescription("Asset type filters for asset/script/shader searches, e.g. Sprite, Texture2D, Material, Prefab, MonoScript.", Required = false)]
        public string[] Types { get; set; }

        [McpDescription("Asset extension filters, with or without dot, e.g. png, prefab, cs, mat.", Required = false)]
        public string[] Extensions { get; set; }

        [McpDescription("Unity asset label filters.", Required = false)]
        public string[] Labels { get; set; }

        [McpDescription("Include Packages/... assets and scripts. Default false keeps search focused on Assets/.", Required = false, Default = false)]
        public bool IncludePackages { get; set; }

        [McpDescription("Include inactive scene objects. Default true because search should find hidden UI/helpers too.", Required = false, Default = true)]
        public bool IncludeInactive { get; set; } = true;

        [McpDescription("Include component summaries on scene-object results.", Required = false, Default = true)]
        public bool IncludeComponents { get; set; } = true;

        [McpDescription("Require stricter exact name/path matching.", Required = false, Default = false)]
        public bool Exact { get; set; }

        [McpDescription("Maximum total results returned.", Required = false, Default = 50)]
        public int Limit { get; set; } = 50;

        [McpDescription("Maximum results returned per source before final ranking.", Required = false, Default = 25)]
        public int PerSourceLimit { get; set; } = 25;

        [McpDescription("Maximum assets scanned per asset-like source.", Required = false, Default = 5000)]
        public int MaxScanAssets { get; set; } = 5000;

        [McpDescription("Maximum scene objects scanned.", Required = false, Default = 3000)]
        public int MaxSceneObjects { get; set; } = 3000;

        [McpDescription("Maximum MonoScript assets scanned.", Required = false, Default = 3000)]
        public int MaxScanScripts { get; set; } = 3000;

        [McpDescription("Sort mode for final results.", Required = false, Default = UnitySearchSortMode.Relevance)]
        public UnitySearchSortMode SortBy { get; set; } = UnitySearchSortMode.Relevance;
    }
}
