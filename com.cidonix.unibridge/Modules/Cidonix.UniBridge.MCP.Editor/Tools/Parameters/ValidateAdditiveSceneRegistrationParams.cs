#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    /// <summary>
    /// Parameters for UniBridge_ValidateAdditiveSceneRegistration.
    /// </summary>
    public record ValidateAdditiveSceneRegistrationParams
    {
        [McpDescription("Scene asset path, e.g. Assets/_Domovyk/Scenes/darkness/darkness12.unity. Either ScenePath or SceneName is required.", Required = false)]
        public string ScenePath { get; set; }

        [McpDescription("Scene name without extension, e.g. darkness12. Used when ScenePath is not supplied.", Required = false)]
        public string SceneName { get; set; }

        [McpDescription("Optional metadata asset path. If omitted, UniBridge looks for <sceneName>.asset beside the scene, then searches the project.", Required = false)]
        public string MetadataAssetPath { get; set; }

        [McpDescription("Optional scenesManager prefab path. If omitted, UniBridge searches for a scenesManager.prefab asset.", Required = false)]
        public string ScenesManagerPrefabPath { get; set; }

        [McpDescription("Require ProjectSettings/EditorBuildSettings.asset to contain the scene path/GUID. Defaults to true.", Required = false, Default = true)]
        public bool? RequireBuildSettingsEntry { get; set; } = true;

        [McpDescription("Check whether the .unity YAML references the metadata asset GUID. Defaults to true.", Required = false, Default = true)]
        public bool? CheckSceneReferencesMetadata { get; set; } = true;

        [McpDescription("Check whether scenesManager.prefab contains a runtime entry for this scene. Defaults to true.", Required = false, Default = true)]
        public bool? CheckScenesManager { get; set; } = true;

        [McpDescription("Validate SceneBoundaries, SceneLoadingBoundaries, ScenePaddingBoundaries, and ScenePaddingWideScreenExpansion counts. Defaults to true.", Required = false, Default = true)]
        public bool? CheckBoundaries { get; set; } = true;

        [McpDescription("Check supplied template/old scene names and GUIDs for stale references in the scene, metadata, and scenesManager entry. Defaults to true.", Required = false, Default = true)]
        public bool? CheckStaleReferences { get; set; } = true;

        [McpDescription("Optional previous/template scene name that should no longer be referenced.", Required = false)]
        public string TemplateSceneName { get; set; }

        [McpDescription("Optional previous/template scene GUID that should no longer be referenced.", Required = false)]
        public string TemplateSceneGuid { get; set; }

        [McpDescription("Optional previous/template metadata GUID that should no longer be referenced.", Required = false)]
        public string TemplateMetadataGuid { get; set; }

        [McpDescription("Optional old scene name that should no longer be referenced after cloning/renaming.", Required = false)]
        public string OldSceneName { get; set; }

        [McpDescription("Optional neighbor scene paths/names for a lightweight reciprocal boundary sanity check.", Required = false)]
        public string[] NeighborScenePaths { get; set; }

        [McpDescription("Enable lightweight neighbor scene existence/metadata/boundary checks. Defaults to false.", Required = false, Default = false)]
        public bool? CheckReciprocalNeighbors { get; set; } = false;

        [McpDescription("Maximum issue/info samples to return for repeated checks.", Required = false, Default = 12)]
        public int? MaxSamples { get; set; } = 12;
    }
}
