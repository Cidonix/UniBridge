#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum WorkflowRecipeAction
    {
        List,
        Describe,
        BuildBatch,
        DryRun,
        Execute
    }

    public record WorkflowRecipesParams
    {
        [McpDescription("Recipe operation: List, Describe, BuildBatch, DryRun, or Execute.", Required = false, Default = WorkflowRecipeAction.List)]
        public WorkflowRecipeAction Action { get; set; } = WorkflowRecipeAction.List;

        [McpDescription("Recipe name or alias. Examples: CreateInventoryScreen, ImportSpriteFolderAs2D, CreateSpriteMaterialAndPreview, CreateHUDFromAssets, SetupClickableUIButton, CreateScriptableConfigAndBindToScene, RunCoreSmokeTest, RunUISmokeTest, RunAssetSmokeTest.", Required = false)]
        public string Recipe { get; set; }

        [McpDescription("Human-readable name for created objects/assets. Recipe-specific default is used when omitted.", Required = false)]
        public string Name { get; set; }

        [McpDescription("Target GameObject, hierarchy path, asset path, or other primary target depending on the recipe.", Required = false)]
        public string Target { get; set; }

        [McpDescription("Optional UI parent GameObject for UI recipes.", Required = false)]
        public string Parent { get; set; }

        [McpDescription("Folder path used by asset recipes, e.g. Assets/Sprites.", Required = false)]
        public string Folder { get; set; }

        [McpDescription("Primary asset path used by material, capture, ScriptableObject, or binding recipes.", Required = false)]
        public string AssetPath { get; set; }

        [McpDescription("Multiple asset paths used by UI/asset recipes.", Required = false)]
        public string[] AssetPaths { get; set; }

        [McpDescription("Sprite or texture path used by sprite/material/UI recipes.", Required = false)]
        public string SpritePath { get; set; }

        [McpDescription("Material asset path to create or update.", Required = false)]
        public string MaterialPath { get; set; }

        [McpDescription("ScriptableObject asset path to create or update.", Required = false)]
        public string ScriptableObjectPath { get; set; }

        [McpDescription("ScriptableObject type name for ScriptableObject recipes.", Required = false)]
        public string ScriptableObjectType { get; set; }

        [McpDescription("Button event target GameObject for clickable UI recipes. Defaults to Target.", Required = false)]
        public string EventTarget { get; set; }

        [McpDescription("Component type that owns the event method for clickable UI recipes.", Required = false)]
        public string EventComponent { get; set; }

        [McpDescription("Method to call for clickable UI recipes.", Required = false)]
        public string EventMethod { get; set; }

        [McpDescription("Optional static event argument for clickable UI recipes.", Required = false)]
        public string EventArgument { get; set; }

        [McpDescription("Optional static event argument type: Void, String, Int, Float, or Bool.", Required = false, Default = "Void")]
        public string EventArgumentType { get; set; }

        [McpDescription("Maximum number of assets to include in generated recipe steps. Default 24.", Required = false, Default = 24)]
        public int? MaxAssets { get; set; }

        [McpDescription("When Action=Execute, force dry-run instead of execution. BuildBatch ignores this and returns both planned default and steps.", Required = false)]
        public bool? DryRun { get; set; }

        [McpDescription("Recipe-specific options. Common keys include title, subtitle, items, actions, pixelsPerUnit, maxTextureSize, color, componentName, propertyName, properties.", Required = false)]
        public JObject Options { get; set; }
    }
}
