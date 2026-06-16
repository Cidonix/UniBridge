using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    /// <summary>
    /// Read-only script intelligence operations for UniBridge_ScriptIntelligence.
    /// </summary>
    public enum ScriptIntelligenceAction
    {
        /// <summary>
        /// List scripts and compiled types with compact metadata.
        /// </summary>
        Catalog,

        /// <summary>
        /// Analyze one script in detail.
        /// </summary>
        Analyze,

        /// <summary>
        /// Read source and summaries for specific type names.
        /// </summary>
        ReadTypes,

        /// <summary>
        /// Search C# source references for a type/member/pattern.
        /// </summary>
        References,

        /// <summary>
        /// Find scenes, prefabs, and assets that reference a script asset.
        /// </summary>
        Usages,

        /// <summary>
        /// Scan scripts for likely cleanup or maintenance hotspots.
        /// </summary>
        Hotspots,

        /// <summary>
        /// List Unity compilation assemblies and assembly definition context.
        /// </summary>
        Assemblies,

        /// <summary>
        /// Analyze selected MonoScript assets from the Project window.
        /// </summary>
        Selection,

        /// <summary>
        /// Return aggregate script metrics.
        /// </summary>
        Metrics
    }

    /// <summary>
    /// Filters script catalog results by Unity role.
    /// </summary>
    public enum ScriptKindFilter
    {
        Any,
        MonoBehaviour,
        ScriptableObject,
        Component,
        Editor,
        EditorWindow,
        PlainCSharp,
        Uncompiled
    }

    /// <summary>
    /// Sort modes for script catalog output.
    /// </summary>
    public enum ScriptIntelligenceSortMode
    {
        Name,
        Path,
        Kind,
        Assembly,
        LineCountDescending,
        ModifiedDescending
    }

    /// <summary>
    /// Parameters for UniBridge_ScriptIntelligence.
    /// </summary>
    public record ScriptIntelligenceParams
    {
        [McpDescription("Operation to perform: Catalog, Analyze, ReadTypes, References, Usages, Hotspots, Assemblies, Selection, or Metrics.", Required = false, Default = ScriptIntelligenceAction.Catalog)]
        public ScriptIntelligenceAction Action { get; set; } = ScriptIntelligenceAction.Catalog;

        [McpDescription("Script asset path, folder scope, or readable project path. Accepts Assets/..., Packages/..., unity://path/..., or a path relative to Assets/.", Required = false)]
        public string Path { get; set; }

        [McpDescription("Optional script asset paths or folder scopes.", Required = false)]
        public string[] Paths { get; set; }

        [McpDescription("Asset GUID for a MonoScript when Path is not known.", Required = false)]
        public string Guid { get; set; }

        [McpDescription("Search text for script name, path, namespace, class, method, field, or assembly.", Required = false)]
        public string Query { get; set; }

        [McpDescription("Specific type name to analyze or search for. Accepts short or fully-qualified type names.", Required = false)]
        public string TypeName { get; set; }

        [McpDescription("Specific type names for ReadTypes.", Required = false)]
        public string[] TypeNames { get; set; }

        [McpDescription("Filter by script role: Any, MonoBehaviour, ScriptableObject, Component, Editor, EditorWindow, PlainCSharp, or Uncompiled.", Required = false, Default = ScriptKindFilter.Any)]
        public ScriptKindFilter Kind { get; set; } = ScriptKindFilter.Any;

        [McpDescription("Filter by base type name, for example MonoBehaviour, EnemyBase, ScriptableObject, or a fully-qualified type.", Required = false)]
        public string BaseType { get; set; }

        [McpDescription("Include Packages/... scripts. Default false keeps catalog/search focused on Assets/.", Required = false, Default = false)]
        public bool IncludePackages { get; set; }

        [McpDescription("Include abstract classes and generic type definitions in type-focused results.", Required = false, Default = false)]
        public bool IncludeAbstract { get; set; }

        [McpDescription("Include detailed member summaries: fields, properties, methods, Unity messages, and Inspector-facing fields. Keep disabled for broad catalogs; Analyze always returns focused details.", Required = false, Default = false)]
        public bool IncludeMembers { get; set; }

        [McpDescription("Include private members in summaries. Inspector-facing private serialized fields are always included.", Required = false, Default = false)]
        public bool IncludePrivateMembers { get; set; }

        [McpDescription("Include source text for Analyze/ReadTypes. ReadTypes includes source by default even if this is false.", Required = false, Default = false)]
        public bool IncludeSource { get; set; }

        [McpDescription("Include script asset usage scan in Analyze results. Can be expensive on large projects.", Required = false, Default = false)]
        public bool IncludeUsages { get; set; }

        [McpDescription("For Usages and Analyze IncludeUsages, include bounded YAML locations where the script GUID appears: line, property path, YAML document, and prefab/scene hierarchy context when inferable.", Required = false, Default = true)]
        public bool IncludeUsageLocations { get; set; } = true;

        [McpDescription("Text or regex pattern for References. Defaults to TypeName or Query.", Required = false)]
        public string Pattern { get; set; }

        [McpDescription("Treat Pattern as a regular expression for References.", Required = false, Default = false)]
        public bool UseRegex { get; set; }

        [McpDescription("Maximum script results/items returned.", Required = false, Default = 50)]
        public int Limit { get; set; } = 50;

        [McpDescription("Page number for Catalog results, 1-based.", Required = false, Default = 1)]
        public int Page { get; set; } = 1;

        [McpDescription("Maximum MonoScript assets scanned. Keeps large projects responsive.", Required = false, Default = 3000)]
        public int MaxScanScripts { get; set; } = 3000;

        [McpDescription("Maximum non-script assets scanned for script usages.", Required = false, Default = 8000)]
        public int MaxScanAssets { get; set; } = 8000;

        [McpDescription("Maximum source/reference matches returned.", Required = false, Default = 200)]
        public int MaxReferences { get; set; } = 200;

        [McpDescription("Maximum YAML script usage locations returned across all usage assets.", Required = false, Default = 100)]
        public int MaxUsageLocations { get; set; } = 100;

        [McpDescription("Maximum source characters returned per script when source is included.", Required = false, Default = 60000)]
        public int MaxTextChars { get; set; } = 60000;

        [McpDescription("Sort mode for Catalog output.", Required = false, Default = ScriptIntelligenceSortMode.Name)]
        public ScriptIntelligenceSortMode SortBy { get; set; } = ScriptIntelligenceSortMode.Name;
    }
}
