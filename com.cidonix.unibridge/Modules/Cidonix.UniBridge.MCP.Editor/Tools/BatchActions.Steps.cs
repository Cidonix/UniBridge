#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class BatchActions
    {
        static ParseResult ParseSteps(JObject rawParameters)
        {
            var stepsToken = rawParameters["Steps"] ?? rawParameters["steps"] ?? rawParameters["Actions"] ?? rawParameters["actions"];
            if (stepsToken == null || stepsToken.Type == JTokenType.Null)
            {
                return ParseResult.Fail("Batch requires a Steps array.");
            }

            if (stepsToken is not JArray stepsArray)
            {
                return ParseResult.Fail("Steps must be an array.");
            }

            var steps = new List<BatchStep>();
            for (var i = 0; i < stepsArray.Count; i++)
            {
                if (stepsArray[i] is not JObject stepObject)
                {
                    return ParseResult.Fail($"Step {i + 1} must be an object.");
                }

                var rawTool = GetString(stepObject, "tool", "Tool", "toolName", "ToolName");
                var toolName = ResolveToolName(rawTool);
                var parameters = NormalizeStepParameters(toolName, ExtractStepParameters(stepObject));
                var step = new BatchStep
                {
                    Index = i + 1,
                    Id = GetString(stepObject, "id", "Id"),
                    Description = GetString(stepObject, "description", "Description"),
                    ToolName = toolName,
                    Parameters = parameters,
                    Optional = GetBool(stepObject, false, "optional", "Optional"),
                    Skip = GetBool(stepObject, false, "skip", "Skip")
                };

                if (string.IsNullOrWhiteSpace(step.Id))
                {
                    step.Id = $"step-{step.Index}";
                }

                steps.Add(step);
            }

            return ParseResult.Ok(steps);
        }

        static JObject ExtractStepParameters(JObject stepObject)
        {
            var explicitParameters =
                stepObject["parameters"] ??
                stepObject["Parameters"] ??
                stepObject["params"] ??
                stepObject["Params"] ??
                stepObject["arguments"] ??
                stepObject["Arguments"] ??
                stepObject["args"] ??
                stepObject["Args"];
            if (explicitParameters is JObject explicitObject)
            {
                return (JObject)explicitObject.DeepClone();
            }

            var parameters = new JObject();
            var controlKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "id", "tool", "toolName", "description", "optional", "skip",
                "parameters", "params", "arguments", "args"
            };

            foreach (var property in stepObject.Properties())
            {
                if (!controlKeys.Contains(property.Name))
                {
                    parameters[property.Name] = property.Value.DeepClone();
                }
            }

            return parameters;
        }

        static JObject NormalizeStepParameters(string toolName, JObject parameters)
        {
            if (parameters == null)
            {
                return new JObject();
            }

            switch (toolName)
            {
                case "UniBridge_ManageGameObject":
                    CopyAlias(parameters, "action", "Action");
                    NormalizeAction(parameters, "action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["create"] = "create",
                        ["creategameobject"] = "create",
                        ["create_game_object"] = "create",
                        ["newgameobject"] = "create",
                        ["modify"] = "modify",
                        ["update"] = "modify",
                        ["patch"] = "modify",
                        ["updategameobject"] = "modify",
                        ["delete"] = "delete",
                        ["destroy"] = "delete",
                        ["deletegameobject"] = "delete",
                        ["find"] = "find",
                        ["search"] = "find",
                        ["getcomponents"] = "get_components",
                        ["get_components"] = "get_components",
                        ["listcomponents"] = "get_components",
                        ["inspectcomponents"] = "get_components",
                        ["getcomponent"] = "get_component",
                        ["get_component"] = "get_component",
                        ["inspectcomponent"] = "get_component",
                        ["addcomponent"] = "add_component",
                        ["add_component"] = "add_component",
                        ["removecomponent"] = "remove_component",
                        ["remove_component"] = "remove_component",
                        ["setcomponentproperty"] = "set_component_property",
                        ["setcomponentproperties"] = "set_component_property",
                        ["set_component_property"] = "set_component_property",
                        ["patchcomponent"] = "set_component_property",
                        ["updatecomponent"] = "set_component_property"
                    });
                    CopyAlias(parameters, "name", "Name");
                    CopyAlias(parameters, "target", "Target", "game_object", "gameObject", "GameObject");
                    CopyAlias(parameters, "search_method", "SearchMethod", "searchMethod");
                    CopyAlias(parameters, "parent", "Parent", "parent_path", "parentPath");
                    CopyAlias(parameters, "position", "Position");
                    CopyAlias(parameters, "rotation", "Rotation");
                    CopyAlias(parameters, "scale", "Scale");
                    CopyAlias(parameters, "positionType", "PositionType", "position_type");
                    CopyAlias(parameters, "primitive_type", "PrimitiveType", "primitiveType", "type", "Type");
                    CopyAlias(parameters, "components_to_add", "ComponentsToAdd", "componentsToAdd");
                    CopyAlias(parameters, "components_to_remove", "ComponentsToRemove", "componentsToRemove");
                    CopyAlias(parameters, "component_name", "ComponentName", "componentName", "component", "Component", "componentType", "ComponentType");
                    CopyAlias(parameters, "component_properties", "ComponentProperties", "componentProperties");
                    CopyAlias(parameters, "properties", "Properties", "props");
                    CopyAlias(parameters, "search_term", "SearchTerm", "searchTerm", "Query", "query");
                    CopyAlias(parameters, "find_all", "FindAll", "findAll");
                    CopyAlias(parameters, "search_in_children", "SearchInChildren", "searchInChildren");
                    CopyAlias(parameters, "search_inactive", "SearchInactive", "searchInactive", "IncludeInactive", "includeInactive");
                    CopyAlias(parameters, "include_non_public_serialized", "IncludeNonPublicSerialized", "includeNonPublicSerialized");
                    CopyAlias(parameters, "set_active", "SetActive", "setActive", "active", "Active");
                    CopyAlias(parameters, "tag", "Tag");
                    CopyAlias(parameters, "layer", "Layer");
                    CopyAlias(parameters, "save_as_prefab", "SaveAsPrefab", "saveAsPrefab");
                    CopyAlias(parameters, "prefab_path", "PrefabPath", "prefabPath");
                    CopyAlias(parameters, "prefab_folder", "PrefabFolder", "prefabFolder");
                    break;
                case "UniBridge_ManagePrefab":
                    CopyAlias(parameters, "action", "Action");
                    CopyAlias(parameters, "dry_run", "dryRun");
                    CopyAlias(parameters, "include_default_overrides", "includeDefaultOverrides");
                    CopyAlias(parameters, "include_values", "includeValues");
                    CopyAlias(parameters, "include_variant_chain", "includeVariantChain");
                    CopyAlias(parameters, "max_items", "maxItems", "limit");
                    CopyAlias(parameters, "override_id", "overrideId", "id");
                    CopyAlias(parameters, "override_ids", "overrideIds", "ids");
                    CopyAlias(parameters, "override_kind", "overrideKind", "kind");
                    CopyAlias(parameters, "object_path", "objectPath", "path");
                    CopyAlias(parameters, "component_type", "componentType", "type");
                    CopyAlias(parameters, "property_path", "propertyPath");
                    break;
                case "UniBridge_ManageAnimatorController":
                    CopyAlias(parameters, "action", "Action");
                    CopyAlias(parameters, "path", "Path", "controller_path", "controllerPath", "asset_path", "assetPath");
                    CopyAlias(parameters, "query", "Query", "search", "Search", "SearchPattern", "searchPattern", "search_pattern");
                    CopyAlias(parameters, "limit", "Limit");
                    CopyAlias(parameters, "graph", "Graph");
                    CopyAlias(parameters, "create_if_missing", "createIfMissing");
                    CopyAlias(parameters, "dry_run", "dryRun");
                    CopyAlias(parameters, "replace_transitions", "replaceTransitions");
                    CopyAlias(parameters, "remove_missing_parameters", "removeMissingParameters");
                    CopyAlias(parameters, "remove_missing_layers", "removeMissingLayers");
                    CopyAlias(parameters, "remove_missing_states", "removeMissingStates");
                    CopyAlias(parameters, "name", "Name");
                    CopyAlias(parameters, "layer", "Layer", "layer_name", "layerName");
                    CopyAlias(parameters, "layer_index", "layerIndex", "LayerIndex");
                    CopyAlias(parameters, "state_machine", "stateMachine", "StateMachine");
                    CopyAlias(parameters, "parent_state_machine", "parentStateMachine", "ParentStateMachine");
                    CopyAlias(parameters, "state", "State");
                    CopyAlias(parameters, "from_state", "fromState", "FromState");
                    CopyAlias(parameters, "to_state", "toState", "ToState", "destination_state", "destinationState");
                    CopyAlias(parameters, "destination_state_machine", "destinationStateMachine", "to_state_machine", "toStateMachine");
                    CopyAlias(parameters, "motion_path", "motionPath", "clip_path", "clipPath");
                    CopyAlias(parameters, "parameter", "Parameter");
                    CopyAlias(parameters, "type", "Type", "parameter_type", "parameterType");
                    CopyAlias(parameters, "blend_type", "blendType");
                    CopyAlias(parameters, "blend_parameter", "blendParameter");
                    CopyAlias(parameters, "blend_parameter_y", "blendParameterY", "parameter_y", "parameterY");
                    CopyAlias(parameters, "use_automatic_thresholds", "useAutomaticThresholds");
                    CopyAlias(parameters, "min_threshold", "minThreshold");
                    CopyAlias(parameters, "max_threshold", "maxThreshold");
                    CopyAlias(parameters, "replace_existing", "replaceExisting");
                    CopyAlias(parameters, "children", "Children");
                    CopyAlias(parameters, "conditions", "Conditions");
                    CopyAlias(parameters, "avatar_mask", "avatarMask", "avatar_mask_path", "avatarMaskPath");
                    CopyAlias(parameters, "ik_pass", "iKPass", "ikPass");
                    CopyAlias(parameters, "synced_layer_index", "syncedLayerIndex");
                    CopyAlias(parameters, "synced_layer_affects_timing", "syncedLayerAffectsTiming");
                    CopyAlias(parameters, "cycle_offset", "cycleOffset");
                    CopyAlias(parameters, "speed_parameter", "speedParameter");
                    CopyAlias(parameters, "speed_parameter_active", "speedParameterActive");
                    CopyAlias(parameters, "cycle_offset_parameter", "cycleOffsetParameter");
                    CopyAlias(parameters, "cycle_offset_parameter_active", "cycleOffsetParameterActive");
                    CopyAlias(parameters, "mirror_parameter", "mirrorParameter");
                    CopyAlias(parameters, "mirror_parameter_active", "mirrorParameterActive");
                    CopyAlias(parameters, "ik_on_feet", "iKOnFeet", "ikOnFeet");
                    CopyAlias(parameters, "time_parameter", "timeParameter");
                    CopyAlias(parameters, "time_parameter_active", "timeParameterActive");
                    CopyAlias(parameters, "has_fixed_duration", "hasFixedDuration");
                    CopyAlias(parameters, "interruption_source", "interruptionSource");
                    CopyAlias(parameters, "ordered_interruption", "orderedInterruption");
                    CopyAlias(parameters, "can_transition_to_self", "canTransitionToSelf");
                    CopyAlias(parameters, "entry", "entry_transition", "entryTransition");
                    CopyAlias(parameters, "any_state", "anyState");
                    CopyAlias(parameters, "to_exit", "toExit");
                    break;
                case "UniBridge_DomainCatalog":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["overview"] = "Overview",
                        ["list"] = "ListDomains",
                        ["domains"] = "ListDomains",
                        ["inspect"] = "InspectDomain",
                        ["domain"] = "InspectDomain",
                        ["types"] = "ListTypes",
                        ["tools"] = "SuggestTools"
                    });
                    CopyAlias(parameters, "Domain", "domain", "topic", "Topic", "profile", "Profile");
                    CopyAlias(parameters, "Query", "query", "search", "Search");
                    CopyAlias(parameters, "Limit", "limit", "maxResults", "max_results");
                    CopyAlias(parameters, "IncludeTypes", "includeTypes", "include_types");
                    CopyAlias(parameters, "IncludeTools", "includeTools", "include_tools");
                    break;
                case "UniBridge_RuntimeProfiler":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["snapshot"] = "Snapshot",
                        ["state"] = "Snapshot",
                        ["sample"] = "Sample",
                        ["samples"] = "Sample",
                        ["profile"] = "Sample",
                        ["hierarchy"] = "Hierarchy",
                        ["marker_hierarchy"] = "Hierarchy",
                        ["profiler_hierarchy"] = "Hierarchy",
                        ["frame_export"] = "Hierarchy",
                        ["frame_hierarchy"] = "Hierarchy",
                        ["hot_markers"] = "Hierarchy",
                        ["top_markers"] = "Hierarchy",
                        ["metrics"] = "Metrics",
                        ["list"] = "Metrics",
                        ["list_metrics"] = "Metrics"
                    });
                    CopyAlias(parameters, "Action", "action");
                    CopyAlias(parameters, "Name", "name");
                    CopyAlias(parameters, "Metrics", "metrics", "metric", "Metric");
                    CopyAlias(parameters, "ProfilerCategories", "profilerCategories", "profiler_categories", "categories", "Categories");
                    CopyAlias(parameters, "MarkerFilters", "markerFilters", "marker_filters", "filters", "Filters");
                    CopyAlias(parameters, "ExcludeMarkerFilters", "excludeMarkerFilters", "exclude_marker_filters", "excludeFilters", "exclude_filters");
                    CopyAlias(parameters, "SampleFrames", "sampleFrames", "sample_frames", "frames", "Frames");
                    CopyAlias(parameters, "TimeoutMs", "timeoutMs", "timeout_ms", "timeout");
                    CopyAlias(parameters, "RequirePlayMode", "requirePlayMode", "require_play_mode");
                    CopyAlias(parameters, "IncludeSceneSummary", "includeSceneSummary", "include_scene_summary");
                    CopyAlias(parameters, "IncludeMemory", "includeMemory", "include_memory");
                    CopyAlias(parameters, "IncludeBehaviourTypeCounts", "includeBehaviourTypeCounts", "include_behaviour_type_counts", "include_behavior_type_counts");
                    CopyAlias(parameters, "MaxBehaviourTypes", "maxBehaviourTypes", "max_behaviour_types", "max_behavior_types");
                    CopyAlias(parameters, "MainThreadSpikeThresholdMs", "mainThreadSpikeThresholdMs", "main_thread_spike_threshold_ms", "spike_threshold_ms");
                    CopyAlias(parameters, "MaxSpikes", "maxSpikes", "max_spikes");
                    CopyAlias(parameters, "MaxProfilerMarkers", "maxProfilerMarkers", "max_profiler_markers", "maxMarkers", "max_markers");
                    CopyAlias(parameters, "MaxHierarchySamples", "maxHierarchySamples", "max_hierarchy_samples", "top", "limit");
                    CopyAlias(parameters, "MaxHierarchyDepth", "maxHierarchyDepth", "max_hierarchy_depth", "depth");
                    CopyAlias(parameters, "MinHierarchySampleMs", "minHierarchySampleMs", "min_hierarchy_sample_ms", "minTimeMs", "min_time_ms");
                    CopyAlias(parameters, "IncludeCounters", "includeCounters", "include_counters");
                    CopyAlias(parameters, "SaveToFile", "saveToFile", "save_to_file");
                    CopyAlias(parameters, "ReturnSamples", "returnSamples", "return_samples");
                    break;
                case "UniBridge_RuntimeStateProbe":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["snapshot"] = "Snapshot",
                        ["state"] = "Snapshot",
                        ["read"] = "Snapshot",
                        ["sample"] = "Sample",
                        ["samples"] = "Sample",
                        ["watch"] = "Sample",
                        ["probe"] = "Sample",
                        ["assert"] = "Assert",
                        ["assertions"] = "Assert",
                        ["expect"] = "Assert",
                        ["check"] = "Assert",
                        ["watch_assert"] = "Assert",
                        ["watchassert"] = "Assert",
                        ["list"] = "ListMembers",
                        ["members"] = "ListMembers",
                        ["listmembers"] = "ListMembers",
                        ["list_members"] = "ListMembers"
                    });
                    CopyAlias(parameters, "Action", "action");
                    CopyAlias(parameters, "Name", "name");
                    CopyAlias(parameters, "Target", "target", "game_object", "gameObject", "object", "Object", "path", "Path");
                    CopyAlias(parameters, "SearchMethod", "searchMethod", "search_method", "method", "Method");
                    CopyAlias(parameters, "Component", "component", "componentType", "component_type", "type", "Type");
                    CopyAlias(parameters, "Members", "members", "member", "Member", "fields", "Fields", "properties", "Properties");
                    CopyAlias(parameters, "Assertions", "assertions", "rules", "Rules", "watchRules", "watch_rules", "expectations", "Expectations");
                    CopyAlias(parameters, "IncludeInactive", "includeInactive", "include_inactive", "search_inactive", "SearchInactive");
                    CopyAlias(parameters, "IncludePrefabStage", "includePrefabStage", "include_prefab_stage", "prefab_stage");
                    CopyAlias(parameters, "ScenePath", "scenePath", "scene_path", "scene");
                    CopyAlias(parameters, "MaxTargets", "maxTargets", "max_targets", "limit", "Limit");
                    CopyAlias(parameters, "MaxComponents", "maxComponents", "max_components");
                    CopyAlias(parameters, "MaxMembers", "maxMembers", "max_members");
                    CopyAlias(parameters, "IncludeSerializedFields", "includeSerializedFields", "include_serialized_fields");
                    CopyAlias(parameters, "IncludeReadableMembers", "includeReadableMembers", "include_readable_members");
                    CopyAlias(parameters, "IncludeNonPublicFields", "includeNonPublicFields", "include_non_public_fields");
                    CopyAlias(parameters, "SampleFrames", "sampleFrames", "sample_frames", "frames", "Frames");
                    CopyAlias(parameters, "TimeoutMs", "timeoutMs", "timeout_ms", "timeout");
                    CopyAlias(parameters, "RequirePlayMode", "requirePlayMode", "require_play_mode");
                    CopyAlias(parameters, "FailOnFailedAssertions", "failOnFailedAssertions", "fail_on_failed_assertions", "failOnFail", "fail_on_fail");
                    CopyAlias(parameters, "MaxStringLength", "maxStringLength", "max_string_length");
                    CopyAlias(parameters, "MaxCollectionItems", "maxCollectionItems", "max_collection_items");
                    CopyAlias(parameters, "SaveToFile", "saveToFile", "save_to_file");
                    CopyAlias(parameters, "ReturnSamples", "returnSamples", "return_samples");
                    break;
                case "UniBridge_ManagePhysics3D":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["inspect"] = "Inspect",
                        ["applypreset"] = "ApplyPreset",
                        ["apply_preset"] = "ApplyPreset",
                        ["preset"] = "ApplyPreset",
                        ["addrigidbody"] = "AddRigidbody",
                        ["add_rigidbody"] = "AddRigidbody",
                        ["rigidbody"] = "AddRigidbody",
                        ["addcollider"] = "AddCollider",
                        ["add_collider"] = "AddCollider",
                        ["collider"] = "AddCollider",
                        ["addjoint"] = "AddJoint",
                        ["add_joint"] = "AddJoint",
                        ["joint"] = "AddJoint",
                        ["addcharactercontroller"] = "AddCharacterController",
                        ["add_character_controller"] = "AddCharacterController",
                        ["charactercontroller"] = "AddCharacterController",
                        ["creatematerial"] = "CreateMaterial",
                        ["create_material"] = "CreateMaterial",
                        ["material"] = "CreateMaterial"
                    });
                    CopyAlias(parameters, "Target", "target", "gameObject", "game_object");
                    CopyAlias(parameters, "SearchMethod", "searchMethod", "search_method");
                    CopyAlias(parameters, "Preset", "preset");
                    CopyAlias(parameters, "ColliderKind", "colliderKind", "collider_kind", "kind", "Kind");
                    CopyAlias(parameters, "MaterialPath", "materialPath", "material_path");
                    CopyAlias(parameters, "Properties", "properties", "props");
                    break;
                case "UniBridge_ManageNavigation":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["inspect"] = "Inspect",
                        ["applypreset"] = "ApplyPreset",
                        ["preset"] = "ApplyPreset",
                        ["addagent"] = "AddAgent",
                        ["agent"] = "AddAgent",
                        ["addobstacle"] = "AddObstacle",
                        ["obstacle"] = "AddObstacle",
                        ["addoffmeshlink"] = "AddOffMeshLink",
                        ["offmeshlink"] = "AddOffMeshLink",
                        ["addsurface"] = "AddSurface",
                        ["surface"] = "AddSurface",
                        ["addmodifier"] = "AddModifier",
                        ["modifier"] = "AddModifier",
                        ["addmodifiervolume"] = "AddModifierVolume",
                        ["modifiervolume"] = "AddModifierVolume",
                        ["addnavmeshlink"] = "AddNavMeshLink",
                        ["navmeshlink"] = "AddNavMeshLink",
                        ["bake"] = "BakeSurface",
                        ["bakesurface"] = "BakeSurface",
                        ["clear"] = "ClearSurface",
                        ["clearsurface"] = "ClearSurface"
                    });
                    CopyAlias(parameters, "Target", "target", "gameObject", "game_object");
                    CopyAlias(parameters, "SearchMethod", "searchMethod", "search_method");
                    CopyAlias(parameters, "Preset", "preset");
                    CopyAlias(parameters, "StartTarget", "startTarget", "start_target");
                    CopyAlias(parameters, "EndTarget", "endTarget", "end_target");
                    CopyAlias(parameters, "Properties", "properties", "props");
                    break;
                case "UniBridge_ManageRendering":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["inspect"] = "Inspect",
                        ["applypreset"] = "ApplyPreset",
                        ["preset"] = "ApplyPreset",
                        ["createcamera"] = "CreateCamera",
                        ["camera"] = "CreateCamera",
                        ["createlight"] = "CreateLight",
                        ["light"] = "CreateLight",
                        ["createvolume"] = "CreateVolume",
                        ["volume"] = "CreateVolume",
                        ["createrig"] = "CreateRig",
                        ["rig"] = "CreateRig",
                        ["setupscenelighting"] = "SetupSceneLighting",
                        ["rendersettings"] = "SetupSceneLighting",
                        ["addpixelperfectcamera"] = "AddPixelPerfectCamera",
                        ["pixelperfectcamera"] = "AddPixelPerfectCamera",
                        ["pixelperfect"] = "AddPixelPerfectCamera",
                        ["addlight2d"] = "AddLight2D",
                        ["light2d"] = "AddLight2D",
                        ["addshadowcaster2d"] = "AddShadowCaster2D",
                        ["shadowcaster2d"] = "AddShadowCaster2D",
                        ["addspriteshaperenderer"] = "AddSpriteShapeRenderer",
                        ["spriteshaperenderer"] = "AddSpriteShapeRenderer",
                        ["setup2dscene"] = "Setup2DScene",
                        ["setup2drendering"] = "Setup2DScene",
                        ["2drendering"] = "Setup2DScene",
                        ["adddecalprojector"] = "AddDecalProjector",
                        ["decalprojector"] = "AddDecalProjector",
                        ["decal"] = "AddDecalProjector",
                        ["addlensflare"] = "AddLensFlare",
                        ["lensflare"] = "AddLensFlare",
                        ["flare"] = "AddLensFlare",
                        ["addflarelayer"] = "AddFlareLayer",
                        ["flarelayer"] = "AddFlareLayer",
                        ["addwindzone"] = "AddWindZone",
                        ["windzone"] = "AddWindZone",
                        ["wind"] = "AddWindZone",
                        ["addprojector"] = "AddProjector",
                        ["projector"] = "AddProjector",
                        ["addlodgroup"] = "AddLODGroup",
                        ["lodgroup"] = "AddLODGroup",
                        ["lod"] = "AddLODGroup",
                        ["addreflectionprobe"] = "AddReflectionProbe",
                        ["reflectionprobe"] = "AddReflectionProbe",
                        ["reflprobe"] = "AddReflectionProbe",
                        ["addlightprobegroup"] = "AddLightProbeGroup",
                        ["lightprobegroup"] = "AddLightProbeGroup",
                        ["probegrid"] = "AddLightProbeGroup",
                        ["addlightprobeproxyvolume"] = "AddLightProbeProxyVolume",
                        ["lightprobeproxyvolume"] = "AddLightProbeProxyVolume",
                        ["probeproxyvolume"] = "AddLightProbeProxyVolume",
                        ["lppv"] = "AddLightProbeProxyVolume"
                    });
                    CopyAlias(parameters, "Target", "target", "gameObject", "game_object");
                    CopyAlias(parameters, "Name", "name");
                    CopyAlias(parameters, "Parent", "parent");
                    CopyAlias(parameters, "Preset", "preset");
                    CopyAlias(parameters, "Position", "position");
                    CopyAlias(parameters, "Rotation", "rotation", "euler");
                    CopyAlias(parameters, "LightType", "lightType", "light_type", "type", "Type");
                    CopyAlias(parameters, "BackgroundColor", "backgroundColor", "background_color");
                    CopyAlias(parameters, "VolumeProfilePath", "volumeProfilePath", "volume_profile_path");
                    CopyAlias(parameters, "Material", "material", "materialPath", "material_path");
                    CopyAlias(parameters, "RenderingLayerMask", "renderingLayerMask", "rendering_layer_mask");
                    CopyAlias(parameters, "Lods", "lods", "LODs", "LODLevels", "lodLevels", "lod_levels", "Levels", "levels");
                    CopyAlias(parameters, "Renderers", "renderers", "Renderer", "renderer");
                    CopyAlias(parameters, "UseChildRenderers", "useChildRenderers", "use_child_renderers");
                    CopyAlias(parameters, "LODSize", "lodSize", "lod_size", "Size", "size");
                    CopyAlias(parameters, "LocalReferencePoint", "localReferencePoint", "local_reference_point");
                    CopyAlias(parameters, "FadeMode", "fadeMode", "fade_mode");
                    CopyAlias(parameters, "AnimateCrossFading", "animateCrossFading", "animate_cross_fading");
                    CopyAlias(parameters, "LastLODBillboard", "lastLODBillboard", "last_lod_billboard");
                    CopyAlias(parameters, "RecalculateBounds", "recalculateBounds", "recalculate_bounds");
                    CopyAlias(parameters, "ProbePositions", "probePositions", "probe_positions", "Positions", "positions");
                    CopyAlias(parameters, "ProbeLayout", "probeLayout", "probe_layout");
                    CopyAlias(parameters, "Dering", "dering");
                    CopyAlias(parameters, "Tetrahedralize", "tetrahedralize");
                    CopyAlias(parameters, "CullingMask", "cullingMask", "culling_mask");
                    CopyAlias(parameters, "RefreshMode", "refreshMode", "refresh_mode");
                    CopyAlias(parameters, "TimeSlicingMode", "timeSlicingMode", "time_slicing_mode");
                    CopyAlias(parameters, "RenderDynamicObjects", "renderDynamicObjects", "render_dynamic_objects");
                    CopyAlias(parameters, "CustomBakedTexture", "customBakedTexture", "custom_baked_texture", "customBakedTexturePath", "custom_baked_texture_path");
                    CopyAlias(parameters, "Importance", "importance");
                    CopyAlias(parameters, "BoxProjection", "boxProjection", "box_projection");
                    CopyAlias(parameters, "BlendDistance", "blendDistance", "blend_distance");
                    CopyAlias(parameters, "Resolution", "resolution");
                    CopyAlias(parameters, "Hdr", "HDR", "hdr");
                    CopyAlias(parameters, "ShadowDistance", "shadowDistance", "shadow_distance");
                    CopyAlias(parameters, "OcclusionCulling", "occlusionCulling", "occlusion_culling");
                    CopyAlias(parameters, "NearClipPlane", "nearClipPlane", "near_clip_plane", "nearClip", "near_clip");
                    CopyAlias(parameters, "FarClipPlane", "farClipPlane", "far_clip_plane", "farClip", "far_clip");
                    CopyAlias(parameters, "QualityMode", "qualityMode", "quality_mode");
                    CopyAlias(parameters, "DataFormat", "dataFormat", "data_format");
                    CopyAlias(parameters, "BoundingBoxMode", "boundingBoxMode", "bounding_box_mode");
                    CopyAlias(parameters, "SizeCustom", "sizeCustom", "size_custom");
                    CopyAlias(parameters, "OriginCustom", "originCustom", "origin_custom");
                    CopyAlias(parameters, "ResolutionMode", "resolutionMode", "resolution_mode");
                    CopyAlias(parameters, "ProbeDensity", "probeDensity", "probe_density");
                    CopyAlias(parameters, "GridResolutionX", "gridResolutionX", "grid_resolution_x");
                    CopyAlias(parameters, "GridResolutionY", "gridResolutionY", "grid_resolution_y");
                    CopyAlias(parameters, "GridResolutionZ", "gridResolutionZ", "grid_resolution_z");
                    CopyAlias(parameters, "ProbePositionMode", "probePositionMode", "probe_position_mode");
                    CopyAlias(parameters, "Properties", "properties", "props");
                    break;
                case "UniBridge_ManageUIToolkit":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["inspect"] = "Inspect",
                        ["createdocument"] = "CreateDocument",
                        ["createuxml"] = "CreateDocument",
                        ["uxml"] = "CreateDocument",
                        ["createstylesheet"] = "CreateStyleSheet",
                        ["createuss"] = "CreateStyleSheet",
                        ["uss"] = "CreateStyleSheet",
                        ["createpanelsettings"] = "CreatePanelSettings",
                        ["panelsettings"] = "CreatePanelSettings",
                        ["attachdocument"] = "AttachDocument",
                        ["attach"] = "AttachDocument",
                        ["uidocument"] = "AttachDocument",
                        ["addelement"] = "AddElement",
                        ["element"] = "AddElement",
                        ["setclasses"] = "SetClasses",
                        ["classes"] = "SetClasses",
                        ["setinlinestyle"] = "SetInlineStyle",
                        ["style"] = "SetInlineStyle"
                    });
                    CopyAlias(parameters, "Path", "path", "uxmlPath", "uxml_path", "documentPath", "document_path");
                    CopyAlias(parameters, "StyleSheetPath", "styleSheetPath", "stylesheet", "ussPath", "uss_path");
                    CopyAlias(parameters, "PanelSettingsPath", "panelSettingsPath", "panel_settings_path");
                    CopyAlias(parameters, "Target", "target", "gameObject", "game_object");
                    CopyAlias(parameters, "Name", "name");
                    CopyAlias(parameters, "RootName", "rootName", "root_name");
                    CopyAlias(parameters, "RootClass", "rootClass", "root_class");
                    CopyAlias(parameters, "ElementType", "elementType", "element_type", "type", "Type");
                    CopyAlias(parameters, "ElementName", "elementName", "element_name");
                    CopyAlias(parameters, "ParentName", "parentName", "parent_name");
                    CopyAlias(parameters, "Classes", "classes", "class", "Class");
                    CopyAlias(parameters, "Style", "style");
                    break;
                case "UniBridge_ManageUI":
                    ApplyUiTemplateActionAlias(parameters);
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["inspect"] = "Inspect",
                        ["createcanvas"] = "CreateCanvas",
                        ["create_canvas"] = "CreateCanvas",
                        ["canvas"] = "CreateCanvas",
                        ["ensureeventsystem"] = "EnsureEventSystem",
                        ["ensure_event_system"] = "EnsureEventSystem",
                        ["eventsystem"] = "EnsureEventSystem",
                        ["createelement"] = "CreateElement",
                        ["create_element"] = "CreateElement",
                        ["element"] = "CreateElement",
                        ["createtemplate"] = "CreateTemplate",
                        ["create_template"] = "CreateTemplate",
                        ["template"] = "CreateTemplate",
                        ["createpanel"] = "CreateTemplate",
                        ["create_panel"] = "CreateTemplate",
                        ["createmodal"] = "CreateTemplate",
                        ["create_modal"] = "CreateTemplate",
                        ["createtoolbar"] = "CreateTemplate",
                        ["create_toolbar"] = "CreateTemplate",
                        ["createlist"] = "CreateTemplate",
                        ["create_list"] = "CreateTemplate",
                        ["createcardgrid"] = "CreateTemplate",
                        ["create_card_grid"] = "CreateTemplate",
                        ["cardgrid"] = "CreateTemplate",
                        ["card_grid"] = "CreateTemplate",
                        ["createhud"] = "CreateTemplate",
                        ["create_hud"] = "CreateTemplate",
                        ["createscrollview"] = "CreateScrollView",
                        ["create_scroll_view"] = "CreateScrollView",
                        ["scrollview"] = "CreateScrollView",
                        ["scroll_view"] = "CreateScrollView",
                        ["createscrollrect"] = "CreateScrollView",
                        ["create_scroll_rect"] = "CreateScrollView",
                        ["addscrollitem"] = "AddScrollItem",
                        ["add_scroll_item"] = "AddScrollItem",
                        ["addlistitem"] = "AddScrollItem",
                        ["add_list_item"] = "AddScrollItem",
                        ["listitem"] = "AddScrollItem",
                        ["list_item"] = "AddScrollItem",
                        ["setgraphic"] = "SetGraphic",
                        ["set_graphic"] = "SetGraphic",
                        ["graphic"] = "SetGraphic",
                        ["setimage"] = "SetGraphic",
                        ["set_image"] = "SetGraphic",
                        ["seticon"] = "SetGraphic",
                        ["set_icon"] = "SetGraphic",
                        ["setbuttonevent"] = "SetButtonEvent",
                        ["set_button_event"] = "SetButtonEvent",
                        ["button_event"] = "SetButtonEvent",
                        ["onclick"] = "SetButtonEvent",
                        ["on_click"] = "SetButtonEvent",
                        ["clearbuttonevents"] = "ClearButtonEvents",
                        ["clear_button_events"] = "ClearButtonEvents",
                        ["clear_onclick"] = "ClearButtonEvents",
                        ["clear_on_click"] = "ClearButtonEvents",
                        ["setselectabletransition"] = "SetSelectableTransition",
                        ["set_selectable_transition"] = "SetSelectableTransition",
                        ["selectable_transition"] = "SetSelectableTransition",
                        ["settransition"] = "SetSelectableTransition",
                        ["set_transition"] = "SetSelectableTransition",
                        ["setrecttransformlayout"] = "SetRectTransformLayout",
                        ["set_rect_transform_layout"] = "SetRectTransformLayout",
                        ["setlayout"] = "SetRectTransformLayout",
                        ["set_layout"] = "SetRectTransformLayout",
                        ["layout"] = "SetRectTransformLayout",
                        ["setrecttransform"] = "SetRectTransform",
                        ["set_rect_transform"] = "SetRectTransform",
                        ["setrect"] = "SetRectTransform",
                        ["set_rect"] = "SetRectTransform",
                        ["setlayoutgroup"] = "SetLayoutGroup",
                        ["set_layout_group"] = "SetLayoutGroup",
                        ["layoutgroup"] = "SetLayoutGroup",
                        ["layout_group"] = "SetLayoutGroup",
                        ["setcontentsizefitter"] = "SetContentSizeFitter",
                        ["set_content_size_fitter"] = "SetContentSizeFitter",
                        ["contentsizefitter"] = "SetContentSizeFitter",
                        ["content_size_fitter"] = "SetContentSizeFitter",
                        ["setlayoutelement"] = "SetLayoutElement",
                        ["set_layout_element"] = "SetLayoutElement",
                        ["layoutelement"] = "SetLayoutElement",
                        ["layout_element"] = "SetLayoutElement",
                        ["validate"] = "Validate",
                        ["validateui"] = "Validate",
                        ["validate_ui"] = "Validate",
                        ["validation"] = "Validate",
                        ["audit"] = "Audit",
                        ["auditlayout"] = "Audit",
                        ["audit_layout"] = "Audit",
                        ["checkui"] = "Audit",
                        ["check_ui"] = "Audit",
                        ["repairplan"] = "RepairPlan",
                        ["repair_plan"] = "RepairPlan",
                        ["planrepair"] = "RepairPlan",
                        ["plan_repair"] = "RepairPlan",
                        ["repairui"] = "RepairPlan",
                        ["repair_ui"] = "RepairPlan",
                        ["autofix"] = "AutoFix",
                        ["auto_fix"] = "AutoFix",
                        ["fixui"] = "AutoFix",
                        ["fix_ui"] = "AutoFix"
                    });
                    CopyAlias(parameters, "Target", "target", "game_object", "gameObject", "path", "Path");
                    CopyAlias(parameters, "Parent", "parent", "parent_path", "parentPath");
                    CopyAlias(parameters, "Name", "name");
                    CopyAlias(parameters, "TemplateType", "templateType", "template_type", "template", "Template");
                    NormalizeAction(parameters, "TemplateType", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["panel"] = "Panel",
                        ["modal"] = "Modal",
                        ["dialog"] = "Modal",
                        ["toolbar"] = "Toolbar",
                        ["bar"] = "Toolbar",
                        ["list"] = "List",
                        ["scrolllist"] = "List",
                        ["scroll_list"] = "List",
                        ["cardgrid"] = "CardGrid",
                        ["card_grid"] = "CardGrid",
                        ["grid"] = "CardGrid",
                        ["cards"] = "CardGrid",
                        ["hud"] = "HUD",
                        ["heads_up_display"] = "HUD"
                    });
                    CopyAlias(parameters, "ElementType", "elementType", "element_type", "type", "Type");
                    NormalizeAction(parameters, "ElementType", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["empty"] = "Empty",
                        ["panel"] = "Panel",
                        ["image"] = "Image",
                        ["text"] = "Text",
                        ["legacytext"] = "Text",
                        ["legacy_text"] = "Text",
                        ["button"] = "Button",
                        ["tmp"] = "TextMeshProText",
                        ["tmptext"] = "TextMeshProText",
                        ["tmp_text"] = "TextMeshProText",
                        ["textmeshpro"] = "TextMeshProText",
                        ["textmeshprotext"] = "TextMeshProText",
                        ["text_mesh_pro_text"] = "TextMeshProText",
                        ["tmprotext"] = "TextMeshProText",
                        ["tmpro_text"] = "TextMeshProText",
                        ["tmpbutton"] = "TextMeshProButton",
                        ["tmp_button"] = "TextMeshProButton",
                        ["textmeshprobutton"] = "TextMeshProButton",
                        ["text_mesh_pro_button"] = "TextMeshProButton",
                        ["tmprobutton"] = "TextMeshProButton",
                        ["tmpro_button"] = "TextMeshProButton"
                    });
                    CopyAlias(parameters, "RenderMode", "renderMode", "render_mode");
                    CopyAlias(parameters, "Camera", "camera");
                    CopyAlias(parameters, "SortingOrder", "sortingOrder", "sorting_order", "order");
                    CopyAlias(parameters, "OverrideSorting", "overrideSorting", "override_sorting");
                    CopyAlias(parameters, "EnsureEventSystem", "ensureEventSystem", "ensure_event_system");
                    CopyAlias(parameters, "CreateParentCanvas", "createParentCanvas", "create_parent_canvas");
                    CopyAlias(parameters, "LayoutHorizontal", "layoutHorizontal", "layout_horizontal", "horizontal");
                    CopyAlias(parameters, "LayoutVertical", "layoutVertical", "layout_vertical", "vertical");
                    CopyAlias(parameters, "AlsoSetPivot", "alsoSetPivot", "also_set_pivot");
                    CopyAlias(parameters, "AlsoSetPosition", "alsoSetPosition", "also_set_position");
                    CopyAlias(parameters, "AnchorMin", "anchorMin", "anchor_min");
                    CopyAlias(parameters, "AnchorMax", "anchorMax", "anchor_max");
                    CopyAlias(parameters, "Pivot", "pivot");
                    CopyAlias(parameters, "AnchoredPosition", "anchoredPosition", "anchored_position", "position");
                    CopyAlias(parameters, "SizeDelta", "sizeDelta", "size_delta", "size");
                    CopyAlias(parameters, "OffsetMin", "offsetMin", "offset_min");
                    CopyAlias(parameters, "OffsetMax", "offsetMax", "offset_max");
                    CopyAlias(parameters, "LocalScale", "localScale", "local_scale", "scale");
                    CopyAlias(parameters, "MaintainWorldPosition", "maintainWorldPosition", "maintain_world_position");
                    CopyAlias(parameters, "Text", "text", "label", "Label");
                    CopyAlias(parameters, "Title", "title", "heading", "header");
                    CopyAlias(parameters, "Subtitle", "subtitle", "sub_title", "description", "caption");
                    CopyAlias(parameters, "ActionTexts", "actionTexts", "action_texts", "actions", "buttons", "buttonTexts", "button_texts", "commands");
                    CopyAlias(parameters, "FontSize", "fontSize", "font_size");
                    CopyAlias(parameters, "Alignment", "alignment", "textAlignment", "text_alignment");
                    CopyAlias(parameters, "BestFit", "bestFit", "best_fit");
                    CopyAlias(parameters, "MinFontSize", "minFontSize", "min_font_size");
                    CopyAlias(parameters, "MaxFontSize", "maxFontSize", "max_font_size");
                    CopyAlias(parameters, "RichText", "richText", "rich_text");
                    CopyAlias(parameters, "OverflowMode", "overflowMode", "overflow_mode", "tmpOverflowMode", "tmp_overflow_mode");
                    CopyAlias(parameters, "FontAssetPath", "fontAssetPath", "font_asset_path", "tmpFontAssetPath", "tmp_font_asset_path");
                    CopyAlias(parameters, "CreateTmpFontAssetIfMissing", "createTmpFontAssetIfMissing", "create_tmp_font_asset_if_missing", "createFontAssetIfMissing", "create_font_asset_if_missing");
                    CopyAlias(parameters, "SpritePath", "spritePath", "sprite_path", "sprite", "icon", "iconPath", "icon_path");
                    CopyAlias(parameters, "TexturePath", "texturePath", "texture_path", "texture", "rawTexture", "raw_texture");
                    CopyAlias(parameters, "MaterialPath", "materialPath", "material_path", "material", "mat");
                    CopyAlias(parameters, "ImageType", "imageType", "image_type", "imageMode", "image_mode");
                    CopyAlias(parameters, "PreserveAspect", "preserveAspect", "preserve_aspect");
                    CopyAlias(parameters, "RaycastTarget", "raycastTarget", "raycast_target");
                    CopyAlias(parameters, "SetNativeSize", "setNativeSize", "set_native_size", "nativeSize", "native_size");
                    CopyAlias(parameters, "HighlightedSpritePath", "highlightedSpritePath", "highlighted_sprite_path", "hoverSpritePath", "hover_sprite_path");
                    CopyAlias(parameters, "PressedSpritePath", "pressedSpritePath", "pressed_sprite_path", "downSpritePath", "down_sprite_path");
                    CopyAlias(parameters, "SelectedSpritePath", "selectedSpritePath", "selected_sprite_path");
                    CopyAlias(parameters, "DisabledSpritePath", "disabledSpritePath", "disabled_sprite_path");
                    CopyAlias(parameters, "Transition", "transition", "transitionMode", "transition_mode");
                    NormalizeAction(parameters, "Transition", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["none"] = "None",
                        ["off"] = "None",
                        ["colortint"] = "ColorTint",
                        ["color_tint"] = "ColorTint",
                        ["color"] = "ColorTint",
                        ["tint"] = "ColorTint",
                        ["spriteswap"] = "SpriteSwap",
                        ["sprite_swap"] = "SpriteSwap",
                        ["sprite"] = "SpriteSwap",
                        ["animation"] = "Animation",
                        ["anim"] = "Animation"
                    });
                    CopyAlias(parameters, "TargetGraphic", "targetGraphic", "target_graphic", "graphicTarget", "graphic_target");
                    CopyAlias(parameters, "NormalColor", "normalColor", "normal_color");
                    CopyAlias(parameters, "HighlightedColor", "highlightedColor", "highlighted_color", "hoverColor", "hover_color");
                    CopyAlias(parameters, "PressedColor", "pressedColor", "pressed_color", "downColor", "down_color");
                    CopyAlias(parameters, "SelectedColor", "selectedColor", "selected_color");
                    CopyAlias(parameters, "DisabledColor", "disabledColor", "disabled_color");
                    CopyAlias(parameters, "ColorMultiplier", "colorMultiplier", "color_multiplier", "tintMultiplier", "tint_multiplier");
                    CopyAlias(parameters, "FadeDuration", "fadeDuration", "fade_duration");
                    CopyAlias(parameters, "EventTarget", "eventTarget", "event_target", "listenerTarget", "listener_target", "targetObject", "target_object");
                    CopyAlias(parameters, "EventComponent", "eventComponent", "event_component", "listenerComponent", "listener_component", "component");
                    CopyAlias(parameters, "EventMethod", "eventMethod", "event_method", "method", "methodName", "method_name", "onClick", "on_click");
                    CopyAlias(parameters, "EventArgument", "eventArgument", "event_argument", "argument", "arg", "value");
                    CopyAlias(parameters, "EventArgumentType", "eventArgumentType", "event_argument_type", "argumentType", "argument_type", "argType", "arg_type");
                    NormalizeAction(parameters, "EventArgumentType", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["void"] = "Void",
                        ["none"] = "Void",
                        ["string"] = "String",
                        ["str"] = "String",
                        ["int"] = "Int",
                        ["integer"] = "Int",
                        ["float"] = "Float",
                        ["single"] = "Float",
                        ["bool"] = "Bool",
                        ["boolean"] = "Bool"
                    });
                    CopyAlias(parameters, "ClearExistingEvents", "clearExistingEvents", "clear_existing_events", "clearFirst", "clear_first", "replaceEvents", "replace_events");
                    CopyAlias(parameters, "ScrollDirection", "scrollDirection", "scroll_direction", "scrollAxis", "scroll_axis", "direction");
                    NormalizeAction(parameters, "ScrollDirection", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["vertical"] = "Vertical",
                        ["y"] = "Vertical",
                        ["horizontal"] = "Horizontal",
                        ["x"] = "Horizontal",
                        ["both"] = "Both",
                        ["xy"] = "Both",
                        ["x_y"] = "Both",
                        ["twoaxis"] = "Both",
                        ["two_axis"] = "Both"
                    });
                    CopyAlias(parameters, "ViewportName", "viewportName", "viewport_name", "viewport");
                    CopyAlias(parameters, "ContentName", "contentName", "content_name", "content");
                    CopyAlias(parameters, "UseRectMask2D", "useRectMask2D", "use_rect_mask_2d", "rectMask", "rect_mask", "clip", "clipping", "mask");
                    CopyAlias(parameters, "MovementType", "movementType", "movement_type");
                    NormalizeAction(parameters, "MovementType", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["unrestricted"] = "Unrestricted",
                        ["free"] = "Unrestricted",
                        ["elastic"] = "Elastic",
                        ["bounce"] = "Elastic",
                        ["clamped"] = "Clamped",
                        ["clamp"] = "Clamped"
                    });
                    CopyAlias(parameters, "Inertia", "inertia");
                    CopyAlias(parameters, "Elasticity", "elasticity");
                    CopyAlias(parameters, "DecelerationRate", "decelerationRate", "deceleration_rate");
                    CopyAlias(parameters, "ScrollSensitivity", "scrollSensitivity", "scroll_sensitivity", "wheelSensitivity", "wheel_sensitivity");
                    CopyAlias(parameters, "ItemTexts", "itemTexts", "item_texts", "items", "entries", "labels", "rows");
                    CopyAlias(parameters, "ItemSizeDelta", "itemSizeDelta", "item_size_delta", "itemSize", "item_size", "rowSize", "row_size", "cellItemSize", "cell_item_size");
                    CopyAlias(parameters, "Columns", "columns", "columnCount", "column_count");
                    CopyAlias(parameters, "UseTextMeshPro", "useTextMeshPro", "use_text_mesh_pro", "useTMP", "use_tmp", "tmp");
                    CopyAlias(parameters, "ValidateAfterCreate", "validateAfterCreate", "validate_after_create", "validateTemplate", "validate_template");
                    CopyAlias(parameters, "LayoutGroupType", "layoutGroupType", "layout_group_type", "groupType", "group_type");
                    CopyAlias(parameters, "Padding", "padding");
                    CopyAlias(parameters, "Spacing", "spacing", "gap");
                    CopyAlias(parameters, "ChildAlignment", "childAlignment", "child_alignment", "layoutAlignment", "layout_alignment");
                    CopyAlias(parameters, "ChildControlWidth", "childControlWidth", "child_control_width");
                    CopyAlias(parameters, "ChildControlHeight", "childControlHeight", "child_control_height");
                    CopyAlias(parameters, "ChildForceExpandWidth", "childForceExpandWidth", "child_force_expand_width", "expandWidth", "expand_width");
                    CopyAlias(parameters, "ChildForceExpandHeight", "childForceExpandHeight", "child_force_expand_height", "expandHeight", "expand_height");
                    CopyAlias(parameters, "ChildScaleWidth", "childScaleWidth", "child_scale_width");
                    CopyAlias(parameters, "ChildScaleHeight", "childScaleHeight", "child_scale_height");
                    CopyAlias(parameters, "ReverseArrangement", "reverseArrangement", "reverse_arrangement", "reverse");
                    CopyAlias(parameters, "CellSize", "cellSize", "cell_size");
                    CopyAlias(parameters, "StartCorner", "startCorner", "start_corner");
                    CopyAlias(parameters, "StartAxis", "startAxis", "start_axis");
                    CopyAlias(parameters, "Constraint", "constraint");
                    CopyAlias(parameters, "ConstraintCount", "constraintCount", "constraint_count");
                    CopyAlias(parameters, "RemoveExistingLayoutGroups", "removeExistingLayoutGroups", "remove_existing_layout_groups", "replaceLayoutGroup", "replace_layout_group");
                    CopyAlias(parameters, "HorizontalFit", "horizontalFit", "horizontal_fit");
                    CopyAlias(parameters, "VerticalFit", "verticalFit", "vertical_fit");
                    CopyAlias(parameters, "IgnoreLayout", "ignoreLayout", "ignore_layout");
                    CopyAlias(parameters, "MinWidth", "minWidth", "min_width");
                    CopyAlias(parameters, "MinHeight", "minHeight", "min_height");
                    CopyAlias(parameters, "PreferredWidth", "preferredWidth", "preferred_width");
                    CopyAlias(parameters, "PreferredHeight", "preferredHeight", "preferred_height");
                    CopyAlias(parameters, "FlexibleWidth", "flexibleWidth", "flexible_width");
                    CopyAlias(parameters, "FlexibleHeight", "flexibleHeight", "flexible_height");
                    CopyAlias(parameters, "LayoutPriority", "layoutPriority", "layout_priority", "priority");
                    CopyAlias(parameters, "IncludeInactive", "includeInactive", "include_inactive");
                    CopyAlias(parameters, "MaxIssues", "maxIssues", "max_issues", "limit");
                    CopyAlias(parameters, "AuditTolerance", "auditTolerance", "audit_tolerance", "tolerance");
                    CopyAlias(parameters, "FixCodes", "fixCodes", "fix_codes", "codes");
                    CopyAlias(parameters, "MaxFixes", "maxFixes", "max_fixes");
                    CopyAlias(parameters, "AutoFixMode", "autoFixMode", "auto_fix_mode", "fixMode", "fix_mode", "mode");
                    CopyAlias(parameters, "Color", "color", "foreground", "foregroundColor", "foreground_color");
                    CopyAlias(parameters, "BackgroundColor", "backgroundColor", "background_color", "bg", "background");
                    CopyAlias(parameters, "Select", "select");
                    CopyAlias(parameters, "DryRun", "dryRun", "dry_run");
                    break;
                case "UniBridge_ManageUnityEvent":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["inspect"] = "Inspect",
                        ["view"] = "Inspect",
                        ["list"] = "Inspect",
                        ["add"] = "AddPersistentCall",
                        ["addpersistentcall"] = "AddPersistentCall",
                        ["add_persistent_call"] = "AddPersistentCall",
                        ["addlistener"] = "AddPersistentCall",
                        ["add_listener"] = "AddPersistentCall",
                        ["set"] = "SetPersistentCalls",
                        ["replace"] = "SetPersistentCalls",
                        ["setpersistentcalls"] = "SetPersistentCalls",
                        ["set_persistent_calls"] = "SetPersistentCalls",
                        ["setlisteners"] = "SetPersistentCalls",
                        ["set_listeners"] = "SetPersistentCalls",
                        ["clear"] = "ClearPersistentCalls",
                        ["clearpersistentcalls"] = "ClearPersistentCalls",
                        ["clear_persistent_calls"] = "ClearPersistentCalls",
                        ["clearlisteners"] = "ClearPersistentCalls",
                        ["clear_listeners"] = "ClearPersistentCalls"
                    });
                    CopyAlias(parameters, "Target", "target", "game_object", "gameObject", "owner", "Owner", "path", "Path");
                    CopyAlias(parameters, "SearchMethod", "searchMethod", "search_method");
                    CopyAlias(parameters, "Component", "component", "eventComponent", "event_component", "ownerComponent", "owner_component");
                    CopyAlias(parameters, "EventProperty", "eventProperty", "event_property", "property", "event", "eventName", "event_name");
                    CopyAlias(parameters, "PersistentCalls", "persistentCalls", "persistent_calls", "calls", "listeners");
                    CopyAlias(parameters, "PersistentCall", "persistentCall", "persistent_call", "call", "listener");
                    CopyAlias(parameters, "EventTarget", "eventTarget", "event_target", "listenerTarget", "listener_target", "targetObject", "target_object");
                    CopyAlias(parameters, "EventComponent", "eventComponent", "event_component", "listenerComponent", "listener_component");
                    CopyAlias(parameters, "MethodName", "methodName", "method_name", "method", "EventMethod", "eventMethod", "event_method");
                    CopyAlias(parameters, "Argument", "argument", "arg", "value", "EventArgument", "eventArgument", "event_argument");
                    CopyAlias(parameters, "CallState", "callState", "call_state", "state");
                    CopyAlias(parameters, "ClearExisting", "clearExisting", "clear_existing", "clearExistingEvents", "clear_existing_events", "replaceExisting", "replace_existing");
                    CopyAlias(parameters, "IncludeInactive", "includeInactive", "include_inactive");
                    CopyAlias(parameters, "Select", "select");
                    CopyAlias(parameters, "DryRun", "dryRun", "dry_run");
                    break;
                case "UniBridge_ManageAsset":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["import"] = "Import",
                        ["create"] = "Create",
                        ["createorupdate"] = "CreateOrUpdate",
                        ["create_or_update"] = "CreateOrUpdate",
                        ["upsert"] = "CreateOrUpdate",
                        ["modify"] = "Modify",
                        ["delete"] = "Delete",
                        ["duplicate"] = "Duplicate",
                        ["move"] = "Move",
                        ["rename"] = "Rename",
                        ["search"] = "Search",
                        ["getinfo"] = "GetInfo",
                        ["get_info"] = "GetInfo",
                        ["createfolder"] = "CreateFolder",
                        ["create_folder"] = "CreateFolder",
                        ["getcomponents"] = "GetComponents",
                        ["get_components"] = "GetComponents"
                    });
                    CopyAlias(parameters, "Path", "path");
                    CopyAlias(parameters, "AssetType", "assetType", "asset_type");
                    CopyAlias(parameters, "Properties", "properties");
                    CopyAlias(parameters, "Destination", "destination");
                    CopyAlias(parameters, "GeneratePreview", "generatePreview", "generate_preview");
                    CopyAlias(parameters, "SearchPattern", "searchPattern", "search_pattern");
                    CopyAlias(parameters, "FilterType", "filterType", "filter_type");
                    CopyAlias(parameters, "FilterDate", "filterDate", "filter_date");
                    CopyAlias(parameters, "PageSize", "pageSize", "page_size");
                    CopyAlias(parameters, "PageNumber", "pageNumber", "page_number");
                    break;
                case "UniBridge_ManageAssetImporter":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["inspect"] = "Inspect",
                        ["get"] = "Inspect",
                        ["info"] = "Inspect",
                        ["set"] = "SetProperties",
                        ["patch"] = "SetProperties",
                        ["setproperties"] = "SetProperties",
                        ["set_properties"] = "SetProperties",
                        ["applypreset"] = "ApplyPreset",
                        ["apply_preset"] = "ApplyPreset",
                        ["preset"] = "ApplyPreset",
                        ["reimport"] = "Reimport",
                        ["import"] = "Reimport"
                    });
                    NormalizeAction(parameters, "Preset", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["none"] = "None",
                        ["sprite"] = "TextureSprite2D",
                        ["sprite2d"] = "TextureSprite2D",
                        ["sprite_2d"] = "TextureSprite2D",
                        ["texture_sprite"] = "TextureSprite2D",
                        ["texture_sprite_2d"] = "TextureSprite2D",
                        ["ui"] = "TextureUI",
                        ["ui_texture"] = "TextureUI",
                        ["texture_ui"] = "TextureUI",
                        ["readable"] = "TextureReadable",
                        ["texture_readable"] = "TextureReadable",
                        ["normal"] = "TextureNormalMap",
                        ["normalmap"] = "TextureNormalMap",
                        ["normal_map"] = "TextureNormalMap",
                        ["texture_normal"] = "TextureNormalMap",
                        ["static_model"] = "ModelStatic",
                        ["model_static"] = "ModelStatic",
                        ["modelstatic"] = "ModelStatic",
                        ["animated_model"] = "ModelAnimated",
                        ["model_animated"] = "ModelAnimated",
                        ["modelanimated"] = "ModelAnimated",
                        ["audio"] = "Audio2D",
                        ["audio2d"] = "Audio2D",
                        ["audio_2d"] = "Audio2D",
                        ["audio_streaming"] = "AudioStreaming",
                        ["streaming_audio"] = "AudioStreaming",
                        ["audiostreaming"] = "AudioStreaming"
                    });
                    CopyAlias(parameters, "Path", "path", "asset_path", "assetPath");
                    CopyAlias(parameters, "Guid", "guid", "assetGuid", "asset_guid");
                    CopyAlias(parameters, "ImporterType", "importerType", "importer_type", "type");
                    CopyAlias(parameters, "Properties", "properties", "props");
                    CopyAlias(parameters, "Preset", "preset");
                    CopyAlias(parameters, "DryRun", "dryRun", "dry_run");
                    CopyAlias(parameters, "Reimport", "reimport", "saveAndReimport", "save_and_reimport");
                    CopyAlias(parameters, "AllowPackages", "allowPackages", "allow_packages");
                    CopyAlias(parameters, "IncludeSerializedProperties", "includeSerializedProperties", "include_serialized_properties");
                    CopyAlias(parameters, "MaxSerializedProperties", "maxSerializedProperties", "max_serialized_properties", "limit");
                    CopyAlias(parameters, "SpritePixelsPerUnit", "spritePixelsPerUnit", "sprite_pixels_per_unit", "ppu");
                    CopyAlias(parameters, "MaxTextureSize", "maxTextureSize", "max_texture_size");
                    CopyAlias(parameters, "CompressionQuality", "compressionQuality", "compression_quality");
                    CopyAlias(parameters, "GlobalScale", "globalScale", "global_scale");
                    CopyAlias(parameters, "IsReadable", "isReadable", "is_readable", "readable");
                    CopyAlias(parameters, "AudioQuality", "audioQuality", "audio_quality", "quality");
                    break;
                case "UniBridge_ManageMaterial":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["inspect"] = "Inspect",
                        ["get"] = "Inspect",
                        ["info"] = "Inspect",
                        ["validate"] = "Validate",
                        ["check"] = "Validate",
                        ["createorupdate"] = "CreateOrUpdate",
                        ["create_or_update"] = "CreateOrUpdate",
                        ["upsert"] = "CreateOrUpdate",
                        ["create"] = "CreateOrUpdate",
                        ["setshader"] = "SetShader",
                        ["set_shader"] = "SetShader",
                        ["shader"] = "SetShader",
                        ["setproperties"] = "SetProperties",
                        ["set_properties"] = "SetProperties",
                        ["set"] = "SetProperties",
                        ["patch"] = "SetProperties",
                        ["applypreset"] = "ApplyPreset",
                        ["apply_preset"] = "ApplyPreset",
                        ["preset"] = "ApplyPreset"
                    });
                    NormalizeAction(parameters, "Preset", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["none"] = "None",
                        ["urp_lit"] = "URPLit",
                        ["urplit"] = "URPLit",
                        ["lit"] = "URPLit",
                        ["urp_unlit"] = "URPUnlit",
                        ["urpunlit"] = "URPUnlit",
                        ["unlit"] = "URPUnlit",
                        ["standard"] = "Standard",
                        ["builtin_standard"] = "Standard",
                        ["unlit_color"] = "UnlitColor",
                        ["unlitcolor"] = "UnlitColor",
                        ["sprite"] = "SpriteDefault",
                        ["sprite_default"] = "SpriteDefault",
                        ["spritedefault"] = "SpriteDefault",
                        ["ui"] = "UIDefault",
                        ["ui_default"] = "UIDefault",
                        ["uidefault"] = "UIDefault",
                        ["transparent"] = "Transparent",
                        ["alpha"] = "Transparent",
                        ["cutout"] = "Cutout",
                        ["alpha_clip"] = "Cutout",
                        ["alphaclip"] = "Cutout"
                    });
                    CopyAlias(parameters, "Path", "path", "asset_path", "assetPath");
                    CopyAlias(parameters, "Guid", "guid", "assetGuid", "asset_guid");
                    CopyAlias(parameters, "Shader", "shader", "shaderPath", "shader_path", "shaderName", "shader_name");
                    CopyAlias(parameters, "Properties", "properties", "props", "shaderProps", "shader_props");
                    CopyAlias(parameters, "Preset", "preset");
                    CopyAlias(parameters, "DryRun", "dryRun", "dry_run");
                    CopyAlias(parameters, "AllowPackages", "allowPackages", "allow_packages");
                    CopyAlias(parameters, "IncludeShaderProperties", "includeShaderProperties", "include_shader_properties");
                    CopyAlias(parameters, "IncludeValues", "includeValues", "include_values");
                    CopyAlias(parameters, "IncludeHiddenProperties", "includeHiddenProperties", "include_hidden_properties");
                    CopyAlias(parameters, "MaxShaderProperties", "maxShaderProperties", "max_shader_properties", "limit");
                    CopyAlias(parameters, "TexturePath", "texturePath", "texture_path", "texture", "mainTexture", "main_texture");
                    CopyAlias(parameters, "Color", "color", "baseColor", "base_color", "tint");
                    CopyAlias(parameters, "EnableInstancing", "enableInstancing", "enable_instancing", "instancing");
                    CopyAlias(parameters, "DoubleSidedGI", "doubleSidedGI", "double_sided_gi");
                    CopyAlias(parameters, "RenderQueue", "renderQueue", "render_queue");
                    CopyAlias(parameters, "Keywords", "keywords");
                    CopyAlias(parameters, "EnableKeywords", "enableKeywords", "enable_keywords", "enabledKeywords", "enabled_keywords");
                    CopyAlias(parameters, "DisableKeywords", "disableKeywords", "disable_keywords", "disabledKeywords", "disabled_keywords");
                    CopyAlias(parameters, "Select", "select");
                    break;
                case "UniBridge_ManageScriptableObject":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["inspect"] = "Inspect",
                        ["get"] = "Inspect",
                        ["info"] = "Inspect",
                        ["validate"] = "Validate",
                        ["check"] = "Validate",
                        ["createorupdate"] = "CreateOrUpdate",
                        ["create_or_update"] = "CreateOrUpdate",
                        ["upsert"] = "CreateOrUpdate",
                        ["create"] = "CreateOrUpdate",
                        ["setproperties"] = "SetProperties",
                        ["set_properties"] = "SetProperties",
                        ["set"] = "SetProperties",
                        ["patch"] = "SetProperties",
                        ["listtypes"] = "ListTypes",
                        ["list_types"] = "ListTypes",
                        ["types"] = "ListTypes",
                        ["catalog"] = "ListTypes"
                    });
                    CopyAlias(parameters, "Path", "path", "asset_path", "assetPath");
                    CopyAlias(parameters, "Guid", "guid", "assetGuid", "asset_guid");
                    CopyAlias(parameters, "ScriptableObjectType", "scriptableObjectType", "scriptable_object_type", "Type", "type", "scriptClass", "script_class");
                    CopyAlias(parameters, "Properties", "properties", "props");
                    CopyAlias(parameters, "ScriptableObject", "scriptableObject", "scriptable_object");
                    CopyAlias(parameters, "DryRun", "dryRun", "dry_run");
                    CopyAlias(parameters, "AllowPackages", "allowPackages", "allow_packages");
                    CopyAlias(parameters, "IncludeSerializedProperties", "includeSerializedProperties", "include_serialized_properties");
                    CopyAlias(parameters, "MaxSerializedProperties", "maxSerializedProperties", "max_serialized_properties", "limit");
                    CopyAlias(parameters, "Query", "query", "search", "Search");
                    CopyAlias(parameters, "Limit", "limit");
                    CopyAlias(parameters, "Select", "select");
                    break;
                case "UniBridge_TypeSchema":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["inspect"] = "Inspect",
                        ["schema"] = "Inspect",
                        ["get"] = "Inspect",
                        ["info"] = "Inspect",
                        ["listtypes"] = "ListTypes",
                        ["list_types"] = "ListTypes",
                        ["types"] = "ListTypes",
                        ["search"] = "ListTypes",
                        ["catalog"] = "ListTypes",
                        ["typeindex"] = "TypeIndex",
                        ["type_index"] = "TypeIndex",
                        ["index"] = "TypeIndex",
                        ["typemap"] = "TypeIndex",
                        ["type_map"] = "TypeIndex",
                        ["map"] = "TypeIndex",
                        ["typefingerprint"] = "TypeFingerprint",
                        ["type_fingerprint"] = "TypeFingerprint",
                        ["fingerprint"] = "TypeFingerprint",
                        ["inspectshader"] = "InspectShader",
                        ["inspect_shader"] = "InspectShader",
                        ["shader_schema"] = "InspectShader",
                        ["shaderschema"] = "InspectShader",
                        ["inspectasset"] = "InspectAsset",
                        ["inspect_asset"] = "InspectAsset",
                        ["asset_schema"] = "InspectAsset",
                        ["assetschema"] = "InspectAsset",
                        ["importer_schema"] = "InspectAsset",
                        ["inspectgameobject"] = "InspectGameObject",
                        ["inspect_game_object"] = "InspectGameObject",
                        ["gameobject_schema"] = "InspectGameObject",
                        ["game_object_schema"] = "InspectGameObject",
                        ["components"] = "InspectGameObject"
                    });
                    CopyAlias(parameters, "Kind", "kind", "typeKind", "type_kind", "category");
                    NormalizeAction(parameters, "Kind", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["any"] = "Any",
                        ["component"] = "Component",
                        ["components"] = "Component",
                        ["mono"] = "MonoBehaviour",
                        ["monobehaviour"] = "MonoBehaviour",
                        ["mono_behaviour"] = "MonoBehaviour",
                        ["scriptable"] = "ScriptableObject",
                        ["scriptableobject"] = "ScriptableObject",
                        ["scriptable_object"] = "ScriptableObject",
                        ["so"] = "ScriptableObject",
                        ["assetimporter"] = "AssetImporter",
                        ["asset_importer"] = "AssetImporter",
                        ["importer"] = "AssetImporter",
                        ["asset"] = "Asset",
                        ["shader"] = "Shader"
                    });
                    CopyAlias(parameters, "TypeName", "typeName", "type", "component", "componentType", "component_type", "class", "className", "class_name");
                    CopyAlias(parameters, "TypeNames", "typeNames", "types", "componentTypes", "component_types", "classes");
                    CopyAlias(parameters, "Query", "query", "search", "Search", "searchPattern", "search_pattern");
                    CopyAlias(parameters, "Path", "path", "asset_path", "assetPath");
                    CopyAlias(parameters, "Guid", "guid", "assetGuid", "asset_guid");
                    CopyAlias(parameters, "Shader", "shader", "shaderPath", "shader_path", "shaderName", "shader_name");
                    CopyAlias(parameters, "Target", "target", "gameObject", "game_object", "gameObjectPath", "game_object_path");
                    CopyAlias(parameters, "SearchMethod", "searchMethod", "search_method");
                    CopyAlias(parameters, "ComponentTypes", "componentTypes", "component_types");
                    CopyAlias(parameters, "IncludeInherited", "includeInherited", "include_inherited");
                    CopyAlias(parameters, "IncludeProperties", "includeProperties", "include_properties");
                    CopyAlias(parameters, "IncludeFields", "includeFields", "include_fields");
                    CopyAlias(parameters, "IncludePrivateSerialized", "includePrivateSerialized", "include_private_serialized");
                    CopyAlias(parameters, "IncludeReadOnly", "includeReadOnly", "include_read_only");
                    CopyAlias(parameters, "IncludeObsolete", "includeObsolete", "include_obsolete");
                    CopyAlias(parameters, "IncludeSerializedProperties", "includeSerializedProperties", "include_serialized_properties");
                    CopyAlias(parameters, "IncludeValues", "includeValues", "include_values");
                    CopyAlias(parameters, "IncludeAbstract", "includeAbstract", "include_abstract");
                    CopyAlias(parameters, "IncludeNonPublicTypes", "includeNonPublicTypes", "include_non_public_types", "includeNonPublic", "include_non_public");
                    CopyAlias(parameters, "WriteToFile", "writeToFile", "write_to_file", "saveToFile", "save_to_file");
                    CopyAlias(parameters, "Limit", "limit", "maxResults", "max_results");
                    CopyAlias(parameters, "MaxTypeIndexEntries", "maxTypeIndexEntries", "max_type_index_entries", "maxEntries", "max_entries");
                    CopyAlias(parameters, "MaxSerializedProperties", "maxSerializedProperties", "max_serialized_properties");
                    break;
                case "UniBridge_UnitySearch":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["search"] = "Search",
                        ["find"] = "Search",
                        ["lookup"] = "Search",
                        ["resolve"] = "Resolve",
                        ["best"] = "Resolve",
                        ["selection"] = "Selection",
                        ["selected"] = "Selection"
                    });
                    NormalizeAction(parameters, "SortBy", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["relevance"] = "Relevance",
                        ["score"] = "Relevance",
                        ["source"] = "Source",
                        ["name"] = "Name",
                        ["path"] = "Path",
                        ["type"] = "Type"
                    });
                    NormalizeAction(parameters, "Backend", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["unibridge"] = "UniBridge",
                        ["native"] = "NativeSearchService",
                        ["nativesearch"] = "NativeSearchService",
                        ["native_search"] = "NativeSearchService",
                        ["searchservice"] = "NativeSearchService",
                        ["search_service"] = "NativeSearchService",
                        ["nativesearchservice"] = "NativeSearchService",
                        ["native_search_service"] = "NativeSearchService",
                        ["hybrid"] = "Hybrid"
                    });
                    CopyAlias(parameters, "Query", "query", "search", "Search", "searchTerm", "search_term", "SearchPattern", "searchPattern", "search_pattern", "name", "Name");
                    CopyAlias(parameters, "Sources", "sources", "source", "Source", "searchTypes", "search_types");
                    CopyAlias(parameters, "Backend", "backend", "searchBackend", "search_backend");
                    CopyAlias(parameters, "NativeTimeoutMs", "nativeTimeoutMs", "native_timeout_ms");
                    NormalizeAction(parameters, "Backend", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["unibridge"] = "UniBridge",
                        ["native"] = "NativeSearchService",
                        ["nativesearch"] = "NativeSearchService",
                        ["native_search"] = "NativeSearchService",
                        ["searchservice"] = "NativeSearchService",
                        ["search_service"] = "NativeSearchService",
                        ["nativesearchservice"] = "NativeSearchService",
                        ["native_search_service"] = "NativeSearchService",
                        ["hybrid"] = "Hybrid"
                    });
                    CopyAlias(parameters, "Path", "path", "asset_path", "assetPath", "folder", "Folder");
                    CopyAlias(parameters, "Paths", "paths", "folders", "Folders");
                    CopyAlias(parameters, "Guid", "guid", "assetGuid", "asset_guid");
                    CopyAlias(parameters, "Target", "target", "gameObject", "game_object", "gameObjectPath", "game_object_path");
                    CopyAlias(parameters, "Types", "types", "assetTypes", "asset_types", "typeFilters", "type_filters");
                    CopyAlias(parameters, "Extensions", "extensions", "exts");
                    CopyAlias(parameters, "Labels", "labels");
                    CopyAlias(parameters, "IncludePackages", "includePackages", "include_packages");
                    CopyAlias(parameters, "IncludeInactive", "includeInactive", "include_inactive");
                    CopyAlias(parameters, "IncludeComponents", "includeComponents", "include_components");
                    CopyAlias(parameters, "Exact", "exact");
                    CopyAlias(parameters, "Limit", "limit", "maxResults", "max_results");
                    CopyAlias(parameters, "PerSourceLimit", "perSourceLimit", "per_source_limit");
                    CopyAlias(parameters, "MaxScanAssets", "maxScanAssets", "max_scan_assets");
                    CopyAlias(parameters, "MaxSceneObjects", "maxSceneObjects", "max_scene_objects");
                    CopyAlias(parameters, "MaxScanScripts", "maxScanScripts", "max_scan_scripts");
                    CopyAlias(parameters, "SortBy", "sortBy", "sort_by");
                    NormalizeAction(parameters, "SortBy", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["relevance"] = "Relevance",
                        ["score"] = "Relevance",
                        ["source"] = "Source",
                        ["name"] = "Name",
                        ["path"] = "Path",
                        ["type"] = "Type"
                    });
                    break;
                case "UniBridge_AssetIntelligence":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["search"] = "Search",
                        ["inspect"] = "Inspect",
                        ["read"] = "Read",
                        ["dependencies"] = "Dependencies",
                        ["deps"] = "Dependencies",
                        ["dependents"] = "Dependents",
                        ["refs"] = "Dependents",
                        ["stats"] = "Stats",
                        ["types"] = "Types",
                        ["selection"] = "Selection",
                        ["preview"] = "Preview",
                        ["serialize"] = "Serialize",
                        ["snapshot"] = "Snapshot",
                        ["context"] = "Context",
                        ["structure"] = "Structure",
                        ["asset_structure"] = "Structure",
                        ["prefab_structure"] = "Structure",
                        ["scene_asset_structure"] = "Structure",
                        ["structure_search"] = "Structure",
                        ["serialized_asset_search"] = "Structure",
                        ["read_yaml"] = "Structure",
                        ["yaml"] = "Structure",
                        ["referencegraph"] = "ReferenceGraph",
                        ["reference_graph"] = "ReferenceGraph",
                        ["references"] = "ReferenceGraph",
                        ["asset_ref_search"] = "ReferenceGraph",
                        ["asset_reference_search"] = "ReferenceGraph",
                        ["reference_locations"] = "ReferenceGraph",
                        ["impact"] = "Impact",
                        ["resolve_missing"] = "ResolveMissing",
                        ["resolvemissing"] = "ResolveMissing"
                    });
                    NormalizeAction(parameters, "StructureMode", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["list"] = "List",
                        ["tree"] = "List",
                        ["hierarchy"] = "List",
                        ["search"] = "Search",
                        ["find"] = "Search",
                        ["query"] = "Search",
                        ["read"] = "Read",
                        ["inspect"] = "Read",
                        ["object"] = "Read"
                    });
                    CopyAlias(parameters, "Query", "query", "search", "Search", "SearchPattern", "searchPattern", "search_pattern");
                    CopyAlias(parameters, "Path", "path", "uri", "Uri");
                    CopyAlias(parameters, "Guid", "guid", "assetGuid", "asset_guid");
                    CopyAlias(parameters, "Paths", "paths");
                    CopyAlias(parameters, "Types", "types", "assetTypes", "asset_types");
                    CopyAlias(parameters, "Extensions", "extensions");
                    CopyAlias(parameters, "Labels", "labels");
                    CopyAlias(parameters, "IncludePackages", "includePackages", "include_packages");
                    CopyAlias(parameters, "IncludePreview", "includePreview", "include_preview");
                    CopyAlias(parameters, "PreviewMode", "previewMode", "preview_mode");
                    CopyAlias(parameters, "Limit", "limit");
                    CopyAlias(parameters, "Page", "page");
                    CopyAlias(parameters, "SortBy", "sortBy", "sort_by");
                    CopyAlias(parameters, "StructureMode", "structureMode", "structure_mode", "mode", "Mode");
                    CopyAlias(parameters, "ObjectPath", "objectPath", "object_path", "targetPath", "target_path", "indexedPath", "indexed_path");
                    CopyAlias(parameters, "PathPrefix", "pathPrefix", "path_prefix", "prefix", "Prefix");
                    CopyAlias(parameters, "ComponentFilter", "componentFilter", "component_filter", "component", "Component", "type", "Type");
                    CopyAlias(parameters, "MatchFields", "matchFields", "match_fields", "fields", "Fields");
                    CopyAlias(parameters, "MaxStructureDepth", "maxStructureDepth", "max_structure_depth", "maxDepth", "max_depth", "depth", "Depth");
                    CopyAlias(parameters, "MaxStructureItems", "maxStructureItems", "max_structure_items", "maxNodes", "max_nodes", "maxObjects", "max_objects");
                    CopyAlias(parameters, "MaxFieldDepth", "maxFieldDepth", "max_field_depth");
                    CopyAlias(parameters, "MaxArrayItems", "maxArrayItems", "max_array_items");
                    CopyAlias(parameters, "IncludeSerializedProperties", "includeSerializedProperties", "include_serialized_properties", "serializedProperties", "serialized_properties");
                    CopyAlias(parameters, "IncludeReferenceLocations", "includeReferenceLocations", "include_reference_locations", "referenceLocations", "reference_locations");
                    CopyAlias(parameters, "MaxReferenceLocations", "maxReferenceLocations", "max_reference_locations", "maxLocations", "max_locations");
                    CopyAlias(parameters, "RefreshReferenceIndex", "refreshReferenceIndex", "refresh_reference_index");
                    CopyAlias(parameters, "UseReferenceIndex", "useReferenceIndex", "use_reference_index");
                    CopyAlias(parameters, "IncludeReferenceEdges", "includeReferenceEdges", "include_reference_edges");
                    CopyAlias(parameters, "MaxReferenceEdges", "maxReferenceEdges", "max_reference_edges");
                    CopyAlias(parameters, "ImpactOperation", "impactOperation", "impact_operation", "operation", "Operation");
                    break;
                case "UniBridge_ScriptIntelligence":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["catalog"] = "Catalog",
                        ["search"] = "Catalog",
                        ["inspect"] = "Analyze",
                        ["analyze"] = "Analyze",
                        ["readtypes"] = "ReadTypes",
                        ["read_types"] = "ReadTypes",
                        ["references"] = "References",
                        ["refs"] = "References",
                        ["usages"] = "Usages",
                        ["usage"] = "Usages",
                        ["script_usages"] = "Usages",
                        ["asset_script_usages"] = "Usages",
                        ["guid_usages"] = "Usages",
                        ["memberusages"] = "MemberUsages",
                        ["member_usages"] = "MemberUsages",
                        ["serialized_member_usages"] = "MemberUsages",
                        ["serialized_member_search"] = "MemberUsages",
                        ["unity_event_usages"] = "MemberUsages",
                        ["animation_event_usages"] = "MemberUsages",
                        ["serialized_field_usages"] = "MemberUsages",
                        ["codeusages"] = "CodeUsages",
                        ["code_usages"] = "CodeUsages",
                        ["unity_code_usages"] = "CodeUsages",
                        ["caller_scan"] = "CodeUsages",
                        ["callers"] = "CodeUsages",
                        ["member_callers"] = "CodeUsages",
                        ["code_member_usages"] = "CodeUsages",
                        ["changeimpact"] = "ChangeImpact",
                        ["change_impact"] = "ChangeImpact",
                        ["script_change_impact"] = "ChangeImpact",
                        ["script_preflight"] = "ChangeImpact",
                        ["hot_diff"] = "ChangeImpact",
                        ["hotdiff"] = "ChangeImpact",
                        ["reload_risk"] = "ChangeImpact",
                        ["script_reload_risk"] = "ChangeImpact",
                        ["api_change_impact"] = "ChangeImpact",
                        ["hotspots"] = "Hotspots",
                        ["assemblies"] = "Assemblies",
                        ["selection"] = "Selection",
                        ["metrics"] = "Metrics"
                    });
                    CopyAlias(parameters, "Path", "path", "uri", "Uri", "scriptPath", "script_path");
                    CopyAlias(parameters, "Paths", "paths", "scriptPaths", "script_paths");
                    CopyAlias(parameters, "Guid", "guid", "scriptGuid", "script_guid");
                    CopyAlias(parameters, "Query", "query", "search", "Search");
                    CopyAlias(parameters, "TypeName", "typeName", "type_name", "className", "class_name");
                    CopyAlias(parameters, "Member", "member", "method", "Method", "field", "Field", "function", "Function");
                    CopyAlias(parameters, "Pattern", "pattern", "regex");
                    CopyAlias(parameters, "IncludeUsageLocations", "includeUsageLocations", "include_usage_locations", "usageLocations", "usage_locations");
                    CopyAlias(parameters, "IncludePossibleMemberUsages", "includePossibleMemberUsages", "include_possible_member_usages", "includePossibleMatches", "include_possible_matches");
                    CopyAlias(parameters, "IncludeSelfReferences", "includeSelfReferences", "include_self_references", "selfReferences", "self_references");
                    CopyAlias(parameters, "IncludeStringReferences", "includeStringReferences", "include_string_references", "stringReferences", "string_references");
                    CopyAlias(parameters, "ProposedSource", "proposedSource", "proposed_source", "newSource", "new_source", "afterSource", "after_source", "replacementSource", "replacement_source");
                    CopyAlias(parameters, "ProposedPath", "proposedPath", "proposed_path", "newPath", "new_path", "afterPath", "after_path", "replacementPath", "replacement_path");
                    CopyAlias(parameters, "MaxUsageLocations", "maxUsageLocations", "max_usage_locations", "maxLocations", "max_locations");
                    CopyAlias(parameters, "MaxReferences", "maxReferences", "max_references", "maxMatches", "max_matches");
                    CopyAlias(parameters, "IncludeUsages", "includeUsages", "include_usages");
                    CopyAlias(parameters, "IncludeSource", "includeSource", "include_source");
                    CopyAlias(parameters, "IncludeMembers", "includeMembers", "include_members");
                    CopyAlias(parameters, "Limit", "limit");
                    break;
                case "UniBridge_Discover":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ping"] = "Ping",
                        ["status"] = "Status",
                        ["workflows"] = "Workflows",
                        ["workflow"] = "Workflows",
                        ["aliases"] = "Aliases",
                        ["alias"] = "Aliases",
                        ["tools"] = "Tools",
                        ["tool"] = "Tools"
                    });
                    CopyAlias(parameters, "Query", "query", "search", "Search");
                    CopyAlias(parameters, "IncludeTools", "includeTools", "include_tools");
                    CopyAlias(parameters, "Limit", "limit");
                    break;
                case "UniBridge_ValidateAdditiveSceneRegistration":
                    CopyAlias(parameters, "ScenePath", "scenePath", "scene_path", "Path", "path", "Scene", "scene");
                    CopyAlias(parameters, "SceneName", "sceneName", "scene_name", "Name", "name");
                    CopyAlias(parameters, "MetadataAssetPath", "metadataAssetPath", "metadata_asset_path", "MetadataPath", "metadataPath");
                    CopyAlias(parameters, "ScenesManagerPrefabPath", "scenesManagerPrefabPath", "scenes_manager_prefab_path", "ScenesManagerPath", "scenesManagerPath");
                    CopyAlias(parameters, "RequireBuildSettingsEntry", "requireBuildSettingsEntry", "require_build_settings_entry");
                    CopyAlias(parameters, "CheckSceneReferencesMetadata", "checkSceneReferencesMetadata", "check_scene_references_metadata");
                    CopyAlias(parameters, "CheckScenesManager", "checkScenesManager", "check_scenes_manager");
                    CopyAlias(parameters, "CheckBoundaries", "checkBoundaries", "check_boundaries");
                    CopyAlias(parameters, "CheckStaleReferences", "checkStaleReferences", "check_stale_references");
                    CopyAlias(parameters, "TemplateSceneName", "templateSceneName", "template_scene_name");
                    CopyAlias(parameters, "TemplateSceneGuid", "templateSceneGuid", "template_scene_guid");
                    CopyAlias(parameters, "TemplateMetadataGuid", "templateMetadataGuid", "template_metadata_guid");
                    CopyAlias(parameters, "OldSceneName", "oldSceneName", "old_scene_name");
                    CopyAlias(parameters, "NeighborScenePaths", "neighborScenePaths", "neighbor_scene_paths", "NeighborScenes", "neighborScenes");
                    CopyAlias(parameters, "CheckReciprocalNeighbors", "checkReciprocalNeighbors", "check_reciprocal_neighbors");
                    CopyAlias(parameters, "MaxSamples", "maxSamples", "max_samples");
                    break;
                case "UniBridge_ValidateScript":
                    CopyAlias(parameters,
                        "Uri", "uri", "URI",
                        "Path", "path",
                        "AssetPath", "assetPath", "asset_path",
                        "ScriptPath", "scriptPath", "script_path",
                        "File", "file");
                    CopyAlias(parameters, "Level", "level", "ValidationLevel", "validationLevel", "validation_level");
                    CopyAlias(parameters,
                        "IncludeDiagnostics",
                        "includeDiagnostics",
                        "include_diagnostics",
                        "diagnostics",
                        "fullDiagnostics",
                        "full_diagnostics");
                    break;
                case "UniBridge_CaptureAsset":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["capture"] = "Capture",
                        ["render"] = "Capture",
                        ["preview"] = "Capture",
                        ["capturegrid"] = "CaptureGrid",
                        ["capture_grid"] = "CaptureGrid",
                        ["grid"] = "CaptureGrid",
                        ["gridcontactsheet"] = "CaptureGrid",
                        ["grid_contact_sheet"] = "CaptureGrid",
                        ["contactsheet"] = "CaptureContactSheet",
                        ["contact_sheet"] = "CaptureContactSheet",
                        ["capturecontactsheet"] = "CaptureContactSheet",
                        ["capture_contact_sheet"] = "CaptureContactSheet",
                        ["assetcontactsheet"] = "CaptureContactSheet",
                        ["asset_contact_sheet"] = "CaptureContactSheet",
                        ["multiview"] = "CaptureContactSheet",
                        ["multi_view"] = "CaptureContactSheet",
                        ["browse"] = "CaptureGrid"
                    });
                    NormalizeAction(parameters, "View", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["auto"] = "Auto",
                        ["iso"] = "Iso",
                        ["isometric"] = "Iso",
                        ["front"] = "Front",
                        ["back"] = "Back",
                        ["left"] = "Left",
                        ["right"] = "Right",
                        ["top"] = "Top",
                        ["bottom"] = "Bottom"
                    });
                    CopyAlias(parameters, "Path", "path", "asset_path", "assetPath");
                    CopyAlias(parameters, "Guid", "guid", "assetGuid", "asset_guid");
                    CopyAlias(parameters, "Paths", "paths", "asset_paths", "assetPaths", "assets");
                    CopyAlias(parameters, "Guids", "guids", "assetGuids", "asset_guids");
                    CopyAlias(parameters, "Folder", "folder");
                    CopyAlias(parameters, "Folders", "folders");
                    CopyAlias(parameters, "Query", "query", "search", "searchTerm", "search_term");
                    CopyAlias(parameters, "SearchPattern", "searchPattern", "search_pattern");
                    CopyAlias(parameters, "Types", "types", "assetTypes", "asset_types");
                    CopyAlias(parameters, "Width", "width");
                    CopyAlias(parameters, "Height", "height");
                    CopyAlias(parameters, "CellWidth", "cellWidth", "cell_width", "tileWidth", "tile_width");
                    CopyAlias(parameters, "CellHeight", "cellHeight", "cell_height", "tileHeight", "tile_height");
                    CopyAlias(parameters, "MaxResults", "maxResults", "max_results", "limit");
                    CopyAlias(parameters, "Columns", "columns", "cols");
                    CopyAlias(parameters, "IncludeLabels", "includeLabels", "include_labels", "labels");
                    CopyAlias(parameters, "View", "view", "direction", "cameraDirection", "camera_direction");
                    CopyAlias(parameters, "Views", "views", "viewDirections", "view_directions", "directions");
                    CopyAlias(parameters, "SeriesCount", "seriesCount", "series_count", "framesPerView", "frames_per_view");
                    CopyAlias(parameters, "SeriesIntervalSeconds", "seriesIntervalSeconds", "series_interval_seconds", "intervalSeconds", "interval_seconds");
                    CopyAlias(parameters, "Orthographic", "orthographic", "ortho");
                    CopyAlias(parameters, "Padding", "padding", "zoomPadding", "zoom_padding");
                    CopyAlias(parameters, "TransparentBackground", "transparentBackground", "transparent_background", "alpha");
                    CopyAlias(parameters, "BackgroundColor", "backgroundColor", "background_color");
                    CopyAlias(parameters, "OutputDirectory", "outputDirectory", "output_directory");
                    CopyAlias(parameters, "FileName", "fileName", "file_name", "outputName", "output_name");
                    CopyAlias(parameters, "Tag", "tag");
                    CopyAlias(parameters, "Select", "select");
                    CopyAlias(parameters, "AdvanceMs", "advanceMs", "advance_ms");
                    CopyAlias(parameters, "SimulateParticles", "simulateParticles", "simulate_particles", "particles");
                    CopyAlias(parameters, "SampleAnimations", "sampleAnimations", "sample_animations", "animations");
                    break;
                case "UniBridge_CaptureUIToolkit":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["capture"] = "Capture",
                        ["render"] = "Capture",
                        ["preview"] = "Capture",
                        ["inspect"] = "Inspect",
                        ["tree"] = "Inspect",
                        ["list"] = "ListUxml",
                        ["search"] = "ListUxml",
                        ["listuxml"] = "ListUxml",
                        ["list_uxml"] = "ListUxml",
                        ["uxml"] = "ListUxml"
                    });
                    CopyAlias(parameters, "Path", "path", "documentPath", "document_path", "uxml", "Uxml");
                    CopyAlias(parameters, "Guid", "guid", "assetGuid", "asset_guid");
                    CopyAlias(parameters, "Target", "target", "gameObject", "game_object", "uidocument", "uiDocument");
                    CopyAlias(parameters, "Query", "query", "search", "Search", "SearchPattern", "searchPattern", "search_pattern", "name", "Name");
                    CopyAlias(parameters, "Folders", "folders");
                    CopyAlias(parameters, "Width", "width");
                    CopyAlias(parameters, "Height", "height");
                    CopyAlias(parameters, "ReadbackMode", "readbackMode", "readback_mode", "captureBackend", "capture_backend");
                    CopyAlias(parameters, "RenderPasses", "renderPasses", "render_passes", "warmupPasses", "warmup_passes");
                    CopyAlias(parameters, "PanelScale", "panelScale", "panel_scale", "scale");
                    CopyAlias(parameters, "TransparentBackground", "transparentBackground", "transparent_background", "alpha");
                    CopyAlias(parameters, "BackgroundColor", "backgroundColor", "background_color");
                    CopyAlias(parameters, "ThemeStyleSheetPath", "themeStyleSheetPath", "theme_style_sheet_path", "themePath", "theme_path", "theme");
                    CopyAlias(parameters, "IncludeTree", "includeTree", "include_tree", "tree");
                    CopyAlias(parameters, "IncludeIssues", "includeIssues", "include_issues", "issues");
                    CopyAlias(parameters, "MaxTreeDepth", "maxTreeDepth", "max_tree_depth");
                    CopyAlias(parameters, "MaxTreeItems", "maxTreeItems", "max_tree_items");
                    CopyAlias(parameters, "Limit", "limit", "maxResults", "max_results");
                    CopyAlias(parameters, "OutputDirectory", "outputDirectory", "output_directory");
                    CopyAlias(parameters, "FileName", "fileName", "file_name", "outputName", "output_name");
                    CopyAlias(parameters, "Tag", "tag");
                    CopyAlias(parameters, "Select", "select");
                    break;
                case "UniBridge_VisualSceneAudit":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["auditcapture"] = "AuditCapture",
                        ["audit_capture"] = "AuditCapture",
                        ["capture"] = "AuditCapture",
                        ["camera"] = "AuditCapture",
                        ["auditimage"] = "AuditImage",
                        ["audit_image"] = "AuditImage",
                        ["image"] = "AuditImage",
                        ["png"] = "AuditImage",
                        ["auditscene"] = "AuditScene",
                        ["audit_scene"] = "AuditScene",
                        ["scene"] = "AuditScene",
                        ["metadata"] = "AuditScene"
                    });
                    CopyAlias(parameters, "Target", "target", "gameObject", "game_object", "root", "Root");
                    CopyAlias(parameters, "Camera", "camera");
                    CopyAlias(parameters, "SearchMethod", "searchMethod", "search_method");
                    CopyAlias(parameters, "ImagePath", "imagePath", "image_path", "path", "Path");
                    CopyAlias(parameters, "OutputPath", "outputPath", "output_path", "output", "Output");
                    CopyAlias(parameters, "Width", "width");
                    CopyAlias(parameters, "Height", "height");
                    CopyAlias(parameters, "Strict", "strict");
                    CopyAlias(parameters, "IncludeConsole", "includeConsole", "include_console", "console");
                    CopyAlias(parameters, "FailOnIssues", "failOnIssues", "fail_on_issues");
                    CopyAlias(parameters, "MaxMagentaRatio", "maxMagentaRatio", "max_magenta_ratio");
                    CopyAlias(parameters, "MaxSingleColorRatio", "maxSingleColorRatio", "max_single_color_ratio");
                    CopyAlias(parameters, "MaxNearWhiteRatio", "maxNearWhiteRatio", "max_near_white_ratio");
                    CopyAlias(parameters, "MaxDarkRatio", "maxDarkRatio", "max_dark_ratio");
                    CopyAlias(parameters, "MaxBrightRatio", "maxBrightRatio", "max_bright_ratio");
                    CopyAlias(parameters, "MinColorDiversity", "minColorDiversity", "min_color_diversity");
                    CopyAlias(parameters, "MinTargetCoverage", "minTargetCoverage", "min_target_coverage");
                    CopyAlias(parameters, "MaxTargetCoverage", "maxTargetCoverage", "max_target_coverage");
                    break;
                case "UniBridge_SceneObjectView":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["hierarchy"] = "Hierarchy",
                        ["tree"] = "Hierarchy",
                        ["flattened"] = "Hierarchy",
                        ["scene"] = "Hierarchy",
                        ["view"] = "View",
                        ["inspect"] = "View",
                        ["object"] = "View",
                        ["objects"] = "View",
                        ["gameobject"] = "View",
                        ["game_object"] = "View",
                        ["selection"] = "Selection",
                        ["selected"] = "Selection"
                    });
                    NormalizeAction(parameters, "Detail", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["brief"] = "Brief",
                        ["minimal"] = "Brief",
                        ["min"] = "Brief",
                        ["standard"] = "Standard",
                        ["normal"] = "Standard",
                        ["detailed"] = "Detailed",
                        ["detail"] = "Detailed",
                        ["full"] = "Full"
                    });
                    CopyAlias(parameters, "Profile", "profile", "focus", "componentProfile", "component_profile");
                    NormalizeAction(parameters, "Profile", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["auto"] = "Auto",
                        ["basic"] = "Basic",
                        ["rendering"] = "Rendering",
                        ["render"] = "Rendering",
                        ["physics2d"] = "Physics2D",
                        ["physics_2d"] = "Physics2D",
                        ["physics3d"] = "Physics3D",
                        ["physics_3d"] = "Physics3D",
                        ["ui"] = "UI",
                        ["animation"] = "Animation",
                        ["anim"] = "Animation",
                        ["vfx"] = "VFX",
                        ["audio"] = "Audio",
                        ["gameplay"] = "Gameplay",
                        ["navigation"] = "Navigation",
                        ["nav"] = "Navigation",
                        ["input"] = "Input",
                        ["tilemap"] = "Tilemap2D",
                        ["tilemap2d"] = "Tilemap2D",
                        ["tilemap_2d"] = "Tilemap2D",
                        ["lighting"] = "Lighting",
                        ["lights"] = "Lighting",
                        ["video"] = "VideoTimeline",
                        ["timeline"] = "VideoTimeline",
                        ["videotimeline"] = "VideoTimeline",
                        ["video_timeline"] = "VideoTimeline"
                    });
                    NormalizeAction(parameters, "IndexDisplayMode", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["none"] = "None",
                        ["off"] = "None",
                        ["metadata"] = "MetadataColumn",
                        ["metadata_column"] = "MetadataColumn",
                        ["column"] = "MetadataColumn",
                        ["indexes"] = "MetadataColumn",
                        ["indices"] = "MetadataColumn"
                    });
                    CopyAlias(parameters, "Target", "target", "gameObject", "game_object", "path", "Path", "name", "Name");
                    CopyAlias(parameters, "Targets", "targets", "gameObjects", "game_objects", "paths", "Paths", "names", "Names");
                    CopyAlias(parameters, "ScenePath", "scenePath", "scene_path", "sceneName", "scene_name", "scene");
                    CopyAlias(parameters, "Index", "index");
                    CopyAlias(parameters, "SearchMethod", "searchMethod", "search_method", "method");
                    CopyAlias(parameters, "IncludeInactive", "includeInactive", "include_inactive", "searchInactive", "search_inactive");
                    CopyAlias(parameters, "IncludeChildren", "includeChildren", "include_children", "children");
                    CopyAlias(parameters, "IncludeFlattened", "includeFlattened", "include_flattened", "flattened");
                    CopyAlias(parameters, "IncludeStructured", "includeStructured", "include_structured", "structured");
                    CopyAlias(parameters, "IncludeBounds", "includeBounds", "include_bounds", "bounds");
                    CopyAlias(parameters, "IncludePrefab", "includePrefab", "include_prefab", "prefab");
                    CopyAlias(parameters, "IncludeSerializedProperties", "includeSerializedProperties", "include_serialized_properties", "serializedProperties", "serialized_properties", "properties");
                    CopyAlias(parameters, "IncludeComponentProperties", "includeComponentProperties", "include_component_properties", "includeComponents", "include_components", "componentProperties", "component_properties");
                    CopyAlias(parameters, "MaxDepth", "maxDepth", "max_depth", "depth");
                    CopyAlias(parameters, "MaxObjects", "maxObjects", "max_objects", "limit", "maxResults", "max_results");
                    CopyAlias(parameters, "MaxChildren", "maxChildren", "max_children");
                    CopyAlias(parameters, "MaxRoots", "maxRoots", "max_roots");
                    CopyAlias(parameters, "MaxSerializedProperties", "maxSerializedProperties", "max_serialized_properties");
                    CopyAlias(parameters, "Select", "select");
                    break;
                case "UniBridge_ManageScene":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["create"] = "Create",
                        ["load"] = "Load",
                        ["save"] = "Save",
                        ["gethierarchy"] = "GetHierarchy",
                        ["get_hierarchy"] = "GetHierarchy",
                        ["getactive"] = "GetActive",
                        ["get_active"] = "GetActive",
                        ["getbuildsettings"] = "GetBuildSettings",
                        ["get_build_settings"] = "GetBuildSettings"
                    });
                    CopyAlias(parameters, "Name", "name");
                    CopyAlias(parameters, "Path", "path");
                    CopyAlias(parameters, "BuildIndex", "buildIndex", "build_index");
                    CopyAlias(parameters, "Depth", "depth");
                    break;
                case "UniBridge_ManageEditor":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["play"] = "Play",
                        ["requestplaymodenowait"] = "RequestPlayModeNoWait",
                        ["request_play_mode_no_wait"] = "RequestPlayModeNoWait",
                        ["request_play_no_wait"] = "RequestPlayModeNoWait",
                        ["play_no_wait"] = "RequestPlayModeNoWait",
                        ["enter_play_mode_no_wait"] = "RequestPlayModeNoWait",
                        ["waitforplaymode"] = "WaitForPlayMode",
                        ["wait_for_play_mode"] = "WaitForPlayMode",
                        ["waitplay"] = "WaitForPlayMode",
                        ["wait_play"] = "WaitForPlayMode",
                        ["waitforeditmode"] = "WaitForEditMode",
                        ["wait_for_edit_mode"] = "WaitForEditMode",
                        ["waitedit"] = "WaitForEditMode",
                        ["wait_edit"] = "WaitForEditMode",
                        ["pause"] = "Pause",
                        ["stop"] = "Stop",
                        ["exitplaymode"] = "ExitPlayMode",
                        ["exit_play_mode"] = "ExitPlayMode",
                        ["getstate"] = "GetState",
                        ["get_state"] = "GetState",
                        ["getplaymodestate"] = "GetPlayModeState",
                        ["get_play_mode_state"] = "GetPlayModeState",
                        ["getprojectroot"] = "GetProjectRoot",
                        ["get_project_root"] = "GetProjectRoot",
                        ["getwindows"] = "GetWindows",
                        ["get_windows"] = "GetWindows",
                        ["getactivetool"] = "GetActiveTool",
                        ["get_active_tool"] = "GetActiveTool",
                        ["getselection"] = "GetSelection",
                        ["get_selection"] = "GetSelection",
                        ["getprefabstage"] = "GetPrefabStage",
                        ["get_prefab_stage"] = "GetPrefabStage",
                        ["setactivetool"] = "SetActiveTool",
                        ["set_active_tool"] = "SetActiveTool",
                        ["addtag"] = "AddTag",
                        ["add_tag"] = "AddTag",
                        ["removetag"] = "RemoveTag",
                        ["remove_tag"] = "RemoveTag",
                        ["gettags"] = "GetTags",
                        ["get_tags"] = "GetTags",
                        ["addlayer"] = "AddLayer",
                        ["add_layer"] = "AddLayer",
                        ["removelayer"] = "RemoveLayer",
                        ["remove_layer"] = "RemoveLayer",
                        ["getlayers"] = "GetLayers",
                        ["get_layers"] = "GetLayers",
                        ["selectasset"] = "SelectAsset",
                        ["select_asset"] = "SelectAsset",
                        ["selectgameobject"] = "SelectGameObject",
                        ["select_game_object"] = "SelectGameObject",
                        ["clearselection"] = "ClearSelection",
                        ["clear_selection"] = "ClearSelection",
                        ["pingselection"] = "PingSelection",
                        ["ping_selection"] = "PingSelection",
                        ["frameselection"] = "FrameSelection",
                        ["frame_selection"] = "FrameSelection",
                        ["waitforready"] = "WaitForReady",
                        ["wait_for_ready"] = "WaitForReady",
                        ["waitforreadyafterreload"] = "WaitForReadyAfterReload",
                        ["wait_for_ready_after_reload"] = "WaitForReadyAfterReload",
                        ["waitforreconnect"] = "WaitForReadyAfterReload",
                        ["wait_for_reconnect"] = "WaitForReadyAfterReload",
                        ["wait_after_reload"] = "WaitForReadyAfterReload",
                        ["waitidle"] = "WaitIdle",
                        ["wait_idle"] = "WaitIdle",
                        ["refreshassets"] = "RefreshAssets",
                        ["refresh_assets"] = "RefreshAssets",
                        ["requestscriptcompilation"] = "RequestScriptCompilation",
                        ["request_script_compilation"] = "RequestScriptCompilation",
                        ["compile"] = "RequestScriptCompilation",
                        ["requestscriptcompilationnowait"] = "RequestScriptCompilationNoWait",
                        ["request_script_compilation_no_wait"] = "RequestScriptCompilationNoWait",
                        ["compile_no_wait"] = "RequestScriptCompilationNoWait",
                        ["saveall"] = "SaveAll",
                        ["save_all"] = "SaveAll",
                        ["saveassets"] = "SaveAssets",
                        ["save_assets"] = "SaveAssets",
                        ["generatesolutionfiles"] = "GenerateSolutionFiles",
                        ["generate_solution_files"] = "GenerateSolutionFiles",
                        ["generatesolutionfile"] = "GenerateSolutionFile",
                        ["generate_solution_file"] = "GenerateSolutionFile",
                        ["reloadcheckpoint"] = "ReloadCheckpoint",
                        ["reload_checkpoint"] = "ReloadCheckpoint",
                        ["reloadfromcheckpoint"] = "ReloadCheckpoint",
                        ["reload_from_checkpoint"] = "ReloadCheckpoint",
                        ["refreshandreload"] = "ReloadCheckpoint",
                        ["refresh_and_reload"] = "ReloadCheckpoint",
                        ["syncsolution"] = "GenerateSolutionFiles",
                        ["sync_solution"] = "GenerateSolutionFiles"
                    });
                    CopyAlias(parameters, "ToolName", "toolName", "tool_name");
                    CopyAlias(parameters, "TagName", "tagName", "tag_name");
                    CopyAlias(parameters, "LayerName", "layerName", "layer_name");
                    CopyAlias(parameters, "WaitForCompletion", "waitForCompletion", "wait_for_completion");
                    CopyAlias(parameters, "AssetPath", "assetPath", "asset_path", "path");
                    CopyAlias(parameters, "GameObjectPath", "gameObjectPath", "game_object_path", "objectPath", "object_path");
                    CopyAlias(parameters, "Target", "target", "name");
                    CopyAlias(parameters, "InstanceID", "instanceID", "instanceId", "instance_id", "id");
                    CopyAlias(parameters, "PingObject", "pingObject", "ping_object", "ping");
                    CopyAlias(parameters, "Focus", "focus");
                    CopyAlias(parameters, "FrameSceneView", "frameSceneView", "frame_scene_view", "frame");
                    CopyAlias(parameters, "Force", "force");
                    CopyAlias(parameters, "TimeoutMs", "timeoutMs", "timeout_ms", "timeout");
                    CopyAlias(parameters, "PollIntervalMs", "pollIntervalMs", "poll_interval_ms", "poll");
                    CopyAlias(parameters, "RequireNotPlaying", "requireNotPlaying", "require_not_playing");
                    CopyAlias(parameters, "SaveScenes", "saveScenes", "save_scenes");
                    CopyAlias(parameters, "SaveAssets", "saveAssets", "save_assets");
                    CopyAlias(parameters, "ModifiedAssetPaths", "modifiedAssetPaths", "modified_asset_paths", "paths", "Paths");
                    CopyAlias(parameters, "RestoreScenes", "restoreScenes", "restore_scenes");
                    CopyAlias(parameters, "RestorePrefabStage", "restorePrefabStage", "restore_prefab_stage");
                    CopyAlias(parameters, "RepaintEditor", "repaintEditor", "repaint_editor");
                    CopyAlias(parameters, "SaveUnmodifiedScenes", "saveUnmodifiedScenes", "save_unmodified_scenes");
                    CopyAlias(parameters, "AllowDirtySceneReload", "allowDirtySceneReload", "allow_dirty_scene_reload");
                    break;
                case "UniBridge_EditorSnapshot":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["capture"] = "Capture",
                        ["restore"] = "Restore",
                        ["list"] = "List",
                        ["inspect"] = "Inspect",
                        ["delete"] = "Delete",
                        ["clear"] = "Clear"
                    });
                    CopyAlias(parameters, "Name", "name");
                    CopyAlias(parameters, "SnapshotId", "snapshotId", "snapshot_id", "id");
                    CopyAlias(parameters, "SnapshotJson", "snapshotJson", "snapshot_json");
                    CopyAlias(parameters, "Persist", "persist");
                    CopyAlias(parameters, "DryRun", "dryRun", "dry_run");
                    CopyAlias(parameters, "IncludeSceneView", "includeSceneView", "include_scene_view");
                    CopyAlias(parameters, "IncludeSelection", "includeSelection", "include_selection");
                    CopyAlias(parameters, "IncludeWindows", "includeWindows", "include_windows");
                    CopyAlias(parameters, "IncludeDockTabs", "includeDockTabs", "include_dock_tabs", "dockTabs", "dock_tabs");
                    CopyAlias(parameters, "IncludePrefabStage", "includePrefabStage", "include_prefab_stage");
                    CopyAlias(parameters, "IncludePrefabAutoSave", "includePrefabAutoSave", "include_prefab_auto_save", "includeAutoSave", "include_auto_save");
                    CopyAlias(parameters, "RestoreScenes", "restoreScenes", "restore_scenes");
                    CopyAlias(parameters, "RestoreSceneView", "restoreSceneView", "restore_scene_view");
                    CopyAlias(parameters, "RestoreSelection", "restoreSelection", "restore_selection");
                    CopyAlias(parameters, "RestorePrefabStage", "restorePrefabStage", "restore_prefab_stage");
                    CopyAlias(parameters, "RestorePrefabAutoSave", "restorePrefabAutoSave", "restore_prefab_auto_save", "restoreAutoSave", "restore_auto_save");
                    CopyAlias(parameters, "RestoreActiveTool", "restoreActiveTool", "restore_active_tool");
                    CopyAlias(parameters, "RestoreFocusedWindow", "restoreFocusedWindow", "restore_focused_window");
                    CopyAlias(parameters, "RestoreDockTabs", "restoreDockTabs", "restore_dock_tabs");
                    CopyAlias(parameters, "RestoreWindowMaximized", "restoreWindowMaximized", "restore_window_maximized");
                    CopyAlias(parameters, "CloseExtraScenes", "closeExtraScenes", "close_extra_scenes");
                    CopyAlias(parameters, "OpenMissingScenes", "openMissingScenes", "open_missing_scenes");
                    CopyAlias(parameters, "SaveDirtyScenes", "saveDirtyScenes", "save_dirty_scenes");
                    CopyAlias(parameters, "AllowDirtySceneReload", "allowDirtySceneReload", "allow_dirty_scene_reload");
                    CopyAlias(parameters, "Limit", "limit");
                    break;
                case "UniBridge_ManageShader":
                    NormalizeAction(parameters, "Action", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["create"] = "Create",
                        ["read"] = "Read",
                        ["update"] = "Update",
                        ["delete"] = "Delete"
                    });
                    CopyAlias(parameters, "Name", "name");
                    CopyAlias(parameters, "Path", "path");
                    CopyAlias(parameters, "Contents", "contents");
                    CopyAlias(parameters, "ContentsEncoded", "contentsEncoded", "contents_encoded");
                    CopyAlias(parameters, "EncodedContents", "encodedContents", "encoded_contents");
                    break;
            }

            return parameters;
        }

        static void ApplyUiTemplateActionAlias(JObject parameters)
        {
            var token = parameters["Action"] ?? parameters["action"];
            var raw = token?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var normalized = raw.Replace("-", "_").Replace(" ", "_").ToLowerInvariant();
            string templateType = normalized switch
            {
                "createpanel" or "create_panel" => "Panel",
                "createmodal" or "create_modal" or "createdialog" or "create_dialog" => "Modal",
                "createtoolbar" or "create_toolbar" => "Toolbar",
                "createlist" or "create_list" or "createscrolllist" or "create_scroll_list" => "List",
                "createcardgrid" or "create_card_grid" or "cardgrid" or "card_grid" => "CardGrid",
                "createhud" or "create_hud" => "HUD",
                _ => null
            };

            if (templateType == null)
            {
                return;
            }

            parameters["Action"] = "CreateTemplate";
            if (parameters["TemplateType"] == null && parameters["templateType"] == null && parameters["template_type"] == null)
            {
                parameters["TemplateType"] = templateType;
            }
        }

        static void NormalizeAction(JObject parameters, string canonicalName, Dictionary<string, string> map)
        {
            var token = parameters[canonicalName] ?? parameters[canonicalName.ToLowerInvariant()];
            if (token == null && string.Equals(canonicalName, "Action", StringComparison.OrdinalIgnoreCase))
            {
                token = parameters["action"];
            }

            if (token == null || token.Type == JTokenType.Null)
            {
                return;
            }

            var raw = token.ToString();
            if (map.TryGetValue(raw, out var normalized))
            {
                parameters[canonicalName] = normalized;
            }
            else
            {
                parameters[canonicalName] = raw;
            }
        }

        static void CopyAlias(JObject parameters, string canonicalName, params string[] aliases)
        {
            if (parameters[canonicalName] != null)
            {
                return;
            }

            foreach (var alias in aliases)
            {
                var token = parameters[alias];
                if (token != null)
                {
                    parameters[canonicalName] = token.DeepClone();
                    return;
                }
            }
        }

        static string ResolveToolName(string rawTool)
        {
            return BatchActionToolCatalog.ResolveToolName(rawTool);
        }
    }
}
