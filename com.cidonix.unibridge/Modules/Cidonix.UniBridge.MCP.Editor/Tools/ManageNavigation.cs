#nullable disable
#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Navigation and NavMesh authoring for agents, obstacles, links, and optional AI Navigation surfaces.
    /// </summary>
    public static class ManageNavigation
    {
        const string ToolName = "UniBridge_ManageNavigation";

        public const string Title = "Manage Navigation presets";

        public const string Description = @"Author Navigation/NavMesh components for 3D gameplay prototypes.

Use this for NavMeshAgent, NavMeshObstacle, OffMeshLink, and optional AI Navigation package components such as NavMeshSurface, NavMeshModifier, NavMeshModifierVolume, and NavMeshLink. Optional package components are resolved reflectively and return a clear unavailable response when absent.

Args:
    Action: Inspect, ApplyPreset, AddAgent, AddObstacle, AddOffMeshLink, AddSurface, AddModifier, AddModifierVolume, AddNavMeshLink, BakeSurface, or ClearSurface.
    Target: GameObject name/path/id for scene operations.
    Preset: HumanoidAgent, FastAgent, StaticObstacle, CarvingObstacle, WalkableSurface, NotWalkableModifier.
    Radius, Height, Speed, Acceleration, AngularSpeed, StoppingDistance, AutoBraking: NavMeshAgent controls.
    Shape, Size, Center, Carving: NavMeshObstacle controls.
    StartTarget, EndTarget, Bidirectional, CostOverride: link controls.
    Properties: Optional extra SerializedProperty/public-property patches for created/updated component.

Returns:
    success, message, and data with created/updated component summaries.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new { type = "string", @enum = new[] { "Inspect", "ApplyPreset", "AddAgent", "AddObstacle", "AddOffMeshLink", "AddSurface", "AddModifier", "AddModifierVolume", "AddNavMeshLink", "BakeSurface", "ClearSurface" } },
                    Target = new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    SearchMethod = new { type = "string" },
                    Preset = new { type = "string" },
                    Radius = new { type = "number" },
                    Height = new { type = "number" },
                    Speed = new { type = "number" },
                    Acceleration = new { type = "number" },
                    AngularSpeed = new { type = "number" },
                    StoppingDistance = new { type = "number" },
                    AutoBraking = new { type = "boolean" },
                    Shape = new { type = "string", @enum = new[] { "Box", "Capsule" } },
                    Size = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    Center = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    Carving = new { type = "boolean" },
                    StartTarget = new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    EndTarget = new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    Bidirectional = new { type = "boolean" },
                    CostOverride = new { type = "number" },
                    Properties = new { type = "object", additionalProperties = true }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "navigation", "ai" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = Normalize(GetString(parameters, "Action", "action") ?? "Inspect");
            try
            {
                return action switch
                {
                    "inspect" => Inspect(parameters),
                    "applypreset" or "preset" => ApplyPreset(parameters),
                    "addagent" or "agent" => AddAgent(parameters),
                    "addobstacle" or "obstacle" => AddObstacle(parameters),
                    "addoffmeshlink" or "offmeshlink" => AddOffMeshLink(parameters),
                    "addsurface" or "surface" => AddOptionalComponent(parameters, "Unity.AI.Navigation.NavMeshSurface", "UnityEngine.AI.NavMeshSurface", "NavMeshSurface"),
                    "addmodifier" or "modifier" => AddOptionalComponent(parameters, "Unity.AI.Navigation.NavMeshModifier", "UnityEngine.AI.NavMeshModifier", "NavMeshModifier"),
                    "addmodifiervolume" or "modifiervolume" => AddOptionalComponent(parameters, "Unity.AI.Navigation.NavMeshModifierVolume", "UnityEngine.AI.NavMeshModifierVolume", "NavMeshModifierVolume"),
                    "addnavmeshlink" or "navmeshlink" => AddOptionalComponent(parameters, "Unity.AI.Navigation.NavMeshLink", "UnityEngine.AI.NavMeshLink", "NavMeshLink"),
                    "bakesurface" or "bake" or "buildnavmesh" => InvokeOptionalSurface(parameters, "BuildNavMesh", "built"),
                    "clearsurface" or "clear" or "removedata" => InvokeOptionalSurface(parameters, "RemoveData", "cleared"),
                    _ => Response.Error($"Unknown Navigation action '{GetString(parameters, "Action", "action")}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageNavigation] Action '{action}' failed: {ex}");
                return Response.Error($"Navigation action '{action}' failed: {ex.Message}");
            }
        }

        static object Inspect(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: false);
            if (target == null)
            {
                var objects = SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true })
                    .Where(HasNavigationComponent)
                    .Select(BuildNavigationSummary)
                    .ToArray();
                return Response.Success("Listed Navigation objects.", new { count = objects.Length, objects });
            }

            return Response.Success("Inspected Navigation object.", BuildNavigationSummary(target));
        }

        static object ApplyPreset(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var preset = Normalize(GetString(parameters, "Preset", "preset") ?? "HumanoidAgent");
            var applied = new List<object>();

            switch (preset)
            {
                case "humanoidagent":
                    if (parameters["Radius"] == null) parameters["Radius"] = 0.35f;
                    if (parameters["Height"] == null) parameters["Height"] = 1.8f;
                    if (parameters["Speed"] == null) parameters["Speed"] = 3.5f;
                    if (parameters["Acceleration"] == null) parameters["Acceleration"] = 8f;
                    applied.Add(EnsureAgent(target, parameters));
                    break;
                case "fastagent":
                    if (parameters["Speed"] == null) parameters["Speed"] = 7f;
                    if (parameters["Acceleration"] == null) parameters["Acceleration"] = 18f;
                    if (parameters["AngularSpeed"] == null) parameters["AngularSpeed"] = 360f;
                    applied.Add(EnsureAgent(target, parameters));
                    break;
                case "staticobstacle":
                    if (parameters["Carving"] == null) parameters["Carving"] = false;
                    if (TryResolveObstacleConflict(target, parameters, out var staticConflict))
                        return staticConflict;
                    applied.Add(EnsureObstacle(target, parameters));
                    break;
                case "carvingobstacle":
                    if (parameters["Carving"] == null) parameters["Carving"] = true;
                    if (TryResolveObstacleConflict(target, parameters, out var carvingConflict))
                        return carvingConflict;
                    applied.Add(EnsureObstacle(target, parameters));
                    break;
                case "walkablesurface":
                    applied.Add(EnsureOptionalComponent(target, parameters, "Unity.AI.Navigation.NavMeshSurface", "UnityEngine.AI.NavMeshSurface", "NavMeshSurface"));
                    break;
                case "notwalkablemodifier":
                    if (parameters["Properties"] == null)
                    {
                        parameters["Properties"] = new JObject
                        {
                            ["overrideArea"] = true,
                            ["area"] = 1
                        };
                    }
                    applied.Add(EnsureOptionalComponent(target, parameters, "Unity.AI.Navigation.NavMeshModifier", "UnityEngine.AI.NavMeshModifier", "NavMeshModifier"));
                    break;
                default:
                    return Response.Error($"Unknown Navigation preset '{GetString(parameters, "Preset", "preset")}'.");
            }

            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Navigation preset applied.", new { target = BuildNavigationSummary(target), applied });
        }

        static object AddAgent(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var component = EnsureAgent(target, parameters);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("NavMeshAgent added or updated.", new { target = BuildNavigationSummary(target), component });
        }

        static object AddObstacle(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            if (TryResolveObstacleConflict(target, parameters, out var conflict))
                return conflict;
            var component = EnsureObstacle(target, parameters);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("NavMeshObstacle added or updated.", new { target = BuildNavigationSummary(target), component });
        }

        static object AddOffMeshLink(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var link = target.GetComponent<OffMeshLink>();
            if (link == null)
                link = Undo.AddComponent<OffMeshLink>(target);

            Undo.RecordObject(link, "Configure OffMeshLink");
            link.activated = GetBool(parameters, link.activated, "Activated", "activated", "enabled");
            link.biDirectional = GetBool(parameters, link.biDirectional, "Bidirectional", "BiDirectional", "bidirectional", "biDirectional");
            link.costOverride = GetFloat(parameters, link.costOverride, "CostOverride", "costOverride", "cost_override");
            link.autoUpdatePositions = GetBool(parameters, link.autoUpdatePositions, "AutoUpdatePositions", "autoUpdatePositions", "auto_update_positions");
            link.startTransform = ResolveTransform(parameters, "StartTarget", "startTarget", "start_target") ?? link.startTransform;
            link.endTransform = ResolveTransform(parameters, "EndTarget", "endTarget", "end_target") ?? link.endTransform;
            ApplyExtraProperties(link, parameters);
            EditorUtility.SetDirty(link);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("OffMeshLink added or updated.", new { target = BuildNavigationSummary(target), component = BuildComponentSummary(link) });
        }

        static object AddOptionalComponent(JObject parameters, params string[] typeNames)
        {
            var target = ResolveTarget(parameters, required: true);
            var component = EnsureOptionalComponent(target, parameters, typeNames);
            if (component == null)
            {
                return Response.Error($"{typeNames.Last()} is unavailable. Install the AI Navigation package or use built-in NavMeshAgent/NavMeshObstacle/OffMeshLink.", new
                {
                    requestedTypes = typeNames
                });
            }

            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success($"{component.GetType().Name} added or updated.", new { target = BuildNavigationSummary(target), component = BuildComponentSummary(component) });
        }

        static object InvokeOptionalSurface(JObject parameters, string methodName, string verb)
        {
            var target = ResolveTarget(parameters, required: true);
            var type = FindType("Unity.AI.Navigation.NavMeshSurface", "UnityEngine.AI.NavMeshSurface", "NavMeshSurface");
            if (type == null)
            {
                return Response.Error("NavMeshSurface is unavailable. Install the AI Navigation package to bake or clear surfaces.");
            }

            var surface = target.GetComponent(type);
            if (surface == null)
            {
                surface = Undo.AddComponent(target, type);
                ApplyExtraProperties(surface, parameters);
            }

            var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return Response.Error($"{type.Name}.{methodName} was not found.");

            method.Invoke(surface, null);
            EditorUtility.SetDirty(surface);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success($"NavMeshSurface {verb}.", new { target = BuildNavigationSummary(target), component = BuildComponentSummary(surface) });
        }

        static object EnsureAgent(GameObject target, JObject parameters)
        {
            var agent = target.GetComponent<NavMeshAgent>();
            if (agent == null)
                agent = Undo.AddComponent<NavMeshAgent>(target);

            Undo.RecordObject(agent, "Configure NavMeshAgent");
            agent.radius = GetFloat(parameters, agent.radius, "Radius", "radius");
            agent.height = GetFloat(parameters, agent.height, "Height", "height");
            agent.baseOffset = GetFloat(parameters, agent.baseOffset, "BaseOffset", "baseOffset", "base_offset");
            agent.speed = GetFloat(parameters, agent.speed, "Speed", "speed");
            agent.acceleration = GetFloat(parameters, agent.acceleration, "Acceleration", "acceleration");
            agent.angularSpeed = GetFloat(parameters, agent.angularSpeed, "AngularSpeed", "angularSpeed", "angular_speed");
            agent.stoppingDistance = GetFloat(parameters, agent.stoppingDistance, "StoppingDistance", "stoppingDistance", "stopping_distance");
            agent.autoBraking = GetBool(parameters, agent.autoBraking, "AutoBraking", "autoBraking", "auto_braking");
            agent.autoRepath = GetBool(parameters, agent.autoRepath, "AutoRepath", "autoRepath", "auto_repath");
            agent.avoidancePriority = GetInt(parameters, agent.avoidancePriority, "AvoidancePriority", "avoidancePriority", "avoidance_priority");
            agent.obstacleAvoidanceType = ParseEnum(GetString(parameters, "ObstacleAvoidanceType", "obstacleAvoidanceType", "obstacle_avoidance_type"), agent.obstacleAvoidanceType);
            ApplyExtraProperties(agent, parameters);
            EditorUtility.SetDirty(agent);
            return BuildComponentSummary(agent);
        }

        static object EnsureObstacle(GameObject target, JObject parameters)
        {
            var obstacle = target.GetComponent<NavMeshObstacle>();
            if (obstacle == null)
                obstacle = Undo.AddComponent<NavMeshObstacle>(target);

            Undo.RecordObject(obstacle, "Configure NavMeshObstacle");
            obstacle.shape = ParseEnum(GetString(parameters, "Shape", "shape"), obstacle.shape);
            obstacle.center = ParseVector3(GetToken(parameters, "Center", "center")) ?? obstacle.center;
            obstacle.size = ParseVector3(GetToken(parameters, "Size", "size")) ?? obstacle.size;
            obstacle.radius = GetFloat(parameters, obstacle.radius, "Radius", "radius");
            obstacle.height = GetFloat(parameters, obstacle.height, "Height", "height");
            obstacle.carving = GetBool(parameters, obstacle.carving, "Carving", "carving");
            obstacle.carveOnlyStationary = GetBool(parameters, obstacle.carveOnlyStationary, "CarveOnlyStationary", "carveOnlyStationary", "carve_only_stationary");
            obstacle.carvingMoveThreshold = GetFloat(parameters, obstacle.carvingMoveThreshold, "CarvingMoveThreshold", "carvingMoveThreshold", "carving_move_threshold");
            obstacle.carvingTimeToStationary = GetFloat(parameters, obstacle.carvingTimeToStationary, "CarvingTimeToStationary", "carvingTimeToStationary", "carving_time_to_stationary");
            ApplyExtraProperties(obstacle, parameters);
            EditorUtility.SetDirty(obstacle);
            return BuildComponentSummary(obstacle);
        }

        static bool TryResolveObstacleConflict(GameObject target, JObject parameters, out object response)
        {
            response = null;
            var agent = target.GetComponent<NavMeshAgent>();
            if (agent == null || !agent.enabled || GetBool(parameters, false, "AllowAgentAndObstacle", "allowAgentAndObstacle", "allow_agent_and_obstacle"))
                return false;

            if (GetBool(parameters, false, "DisableAgent", "disableAgent", "disable_agent"))
            {
                Undo.RecordObject(agent, "Disable NavMeshAgent for NavMeshObstacle");
                agent.enabled = false;
                EditorUtility.SetDirty(agent);
                return false;
            }

            response = Response.Error(
                "Target already has an enabled NavMeshAgent. Unity warns when NavMeshAgent and NavMeshObstacle are active on the same GameObject; use a separate object, pass DisableAgent=true to convert this target, or AllowAgentAndObstacle=true to force it.",
                BuildNavigationSummary(target));
            return true;
        }

        static Component EnsureOptionalComponent(GameObject target, JObject parameters, params string[] typeNames)
        {
            var type = FindType(typeNames);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                return null;

            var component = target.GetComponent(type);
            if (component == null)
                component = Undo.AddComponent(target, type);
            ApplyExtraProperties(component, parameters);
            EditorUtility.SetDirty(component);
            return component;
        }

        static bool HasNavigationComponent(GameObject go)
        {
            return go.GetComponent<NavMeshAgent>() != null ||
                   go.GetComponent<NavMeshObstacle>() != null ||
                   go.GetComponent<OffMeshLink>() != null ||
                   go.GetComponents<Component>().Any(component => component != null && IsOptionalNavigationType(component.GetType()));
        }

        static bool IsOptionalNavigationType(Type type)
        {
            var fullName = type.FullName ?? type.Name;
            return fullName.EndsWith(".NavMeshSurface", StringComparison.Ordinal) ||
                   fullName.EndsWith(".NavMeshModifier", StringComparison.Ordinal) ||
                   fullName.EndsWith(".NavMeshModifierVolume", StringComparison.Ordinal) ||
                   fullName.EndsWith(".NavMeshLink", StringComparison.Ordinal);
        }

        static object BuildNavigationSummary(GameObject target)
        {
            var components = target.GetComponents<Component>()
                .Where(component => component != null)
                .Where(component => component is NavMeshAgent || component is NavMeshObstacle || component is OffMeshLink || IsOptionalNavigationType(component.GetType()))
                .Select(BuildComponentSummary)
                .ToArray();

            return new
            {
                gameObject = new
                {
                    name = target.name,
                    instanceId = UnityApiAdapter.GetObjectId(target),
                    path = SceneObjectLocator.GetHierarchyPath(target),
                    scene = target.scene.IsValid() ? new { name = target.scene.name, path = target.scene.path } : null
                },
                components
            };
        }

        static object BuildComponentSummary(Component component)
        {
            if (component == null)
                return null;

            if (component is NavMeshAgent agent)
            {
                return new { type = agent.GetType().FullName, radius = agent.radius, height = agent.height, speed = agent.speed, acceleration = agent.acceleration, angularSpeed = agent.angularSpeed, stoppingDistance = agent.stoppingDistance, autoBraking = agent.autoBraking, obstacleAvoidanceType = agent.obstacleAvoidanceType.ToString(), enabled = agent.enabled };
            }

            if (component is NavMeshObstacle obstacle)
            {
                return new { type = obstacle.GetType().FullName, shape = obstacle.shape.ToString(), center = SerializeVector3(obstacle.center), size = SerializeVector3(obstacle.size), radius = obstacle.radius, height = obstacle.height, carving = obstacle.carving, enabled = obstacle.enabled };
            }

            if (component is OffMeshLink link)
            {
                return new { type = link.GetType().FullName, activated = link.activated, bidirectional = link.biDirectional, costOverride = link.costOverride, start = link.startTransform != null ? link.startTransform.name : null, end = link.endTransform != null ? link.endTransform.name : null };
            }

            return new
            {
                type = component.GetType().FullName,
                enabled = component is Behaviour behaviour ? behaviour.enabled : (bool?)null,
                optionalPackageComponent = IsOptionalNavigationType(component.GetType())
            };
        }

        static void ApplyExtraProperties(UnityEngine.Object component, JObject parameters)
        {
            var props = parameters["Properties"] as JObject ?? parameters["properties"] as JObject ?? BuildImplicitProperties(parameters);
            if (props == null || !props.Properties().Any())
                return;

            var so = new SerializedObject(component);
            foreach (var property in props.Properties())
            {
                var result = SerializedPropertyPatcher.TryApplyProperty(component, so, property.Name, property.Value, dryRun: false);
                if (!result.Success)
                    TrySetPublicProperty(component, property.Name, property.Value);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static JObject BuildImplicitProperties(JObject parameters)
        {
            var props = new JObject();
            CopyIfPresent(parameters, props, "AgentTypeId", "agentTypeID", "agentTypeId", "agent_type_id");
            CopyIfPresent(parameters, props, "LayerMask", "m_LayerMask", "layerMask", "layer_mask");
            CopyIfPresent(parameters, props, "CollectObjects", "m_CollectObjects", "collectObjects", "collect_objects");
            CopyIfPresent(parameters, props, "UseGeometry", "m_UseGeometry", "useGeometry", "use_geometry");
            CopyIfPresent(parameters, props, "DefaultArea", "m_DefaultArea", "defaultArea", "default_area", "Area", "area");
            CopyIfPresent(parameters, props, "OverrideArea", "m_OverrideArea", "overrideArea", "override_area");
            CopyIfPresent(parameters, props, "Center", "m_Center", "center");
            CopyIfPresent(parameters, props, "Size", "m_Size", "size");
            CopyIfPresent(parameters, props, "Width", "m_Width", "width");
            CopyIfPresent(parameters, props, "CostModifier", "m_CostModifier", "costModifier", "cost_modifier");
            CopyIfPresent(parameters, props, "Bidirectional", "m_Bidirectional", "bidirectional", "biDirectional");
            return props;
        }

        static void CopyIfPresent(JObject source, JObject dest, string canonical, params string[] aliases)
        {
            if (dest[canonical] != null)
                return;
            foreach (var alias in aliases.Concat(new[] { canonical }))
            {
                if (source.TryGetValue(alias, StringComparison.OrdinalIgnoreCase, out var token))
                {
                    dest[canonical] = token.DeepClone();
                    return;
                }
            }
        }

        static bool TrySetPublicProperty(UnityEngine.Object target, string name, JToken value)
        {
            var type = target.GetType();
            var normalized = Normalize(name);
            var property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(prop => prop.CanWrite && Normalize(prop.Name) == normalized);
            if (property == null)
                return false;

            object converted;
            if (!TryConvert(value, property.PropertyType, out converted))
                return false;

            property.SetValue(target, converted);
            return true;
        }

        static bool TryConvert(JToken token, Type type, out object value)
        {
            value = null;
            try
            {
                if (type == typeof(Vector3))
                {
                    value = ParseVector3(token) ?? Vector3.zero;
                    return true;
                }
                if (type.IsEnum)
                {
                    value = token.Type == JTokenType.Integer ? Enum.ToObject(type, token.ToObject<int>()) : Enum.Parse(type, token.ToString(), true);
                    return true;
                }
                value = token.ToObject(type);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static Transform ResolveTransform(JObject parameters, params string[] keys)
        {
            var token = GetToken(parameters, keys);
            if (token == null || token.Type == JTokenType.Null || string.IsNullOrWhiteSpace(token.ToString()))
                return null;
            var go = SceneObjectLocator.FindObject(token.ToString(), GetString(parameters, "SearchMethod", "searchMethod", "search_method"), new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            return go != null ? go.transform : null;
        }

        static GameObject ResolveTarget(JObject parameters, bool required)
        {
            var target = GetToken(parameters, "Target", "target", "GameObject", "gameObject", "game_object");
            if (target == null || target.Type == JTokenType.Null || string.IsNullOrWhiteSpace(target.ToString()))
            {
                if (Selection.activeGameObject != null)
                    return Selection.activeGameObject;
                if (required)
                    throw new InvalidOperationException("Target GameObject is required.");
                return null;
            }

            var go = SceneObjectLocator.FindObject(target.ToString(), GetString(parameters, "SearchMethod", "searchMethod", "search_method"), new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            if (go == null && required)
                throw new InvalidOperationException($"Target GameObject '{target}' was not found.");
            return go;
        }

        static Type FindType(params string[] names)
        {
            foreach (var name in names.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                var direct = Type.GetType(name, false);
                if (direct != null)
                    return direct;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(type => type != null).ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    var match = types.FirstOrDefault(type =>
                        string.Equals(type.FullName, name, StringComparison.Ordinal) ||
                        string.Equals(type.Name, name, StringComparison.Ordinal) ||
                        string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        return match;
                }
            }

            return null;
        }

        static Vector3? ParseVector3(JToken token)
        {
            if (token is JArray arr && arr.Count >= 3)
                return new Vector3(ReadFloat(arr, 0), ReadFloat(arr, 1), ReadFloat(arr, 2));
            if (token is JObject obj)
                return new Vector3(ReadFloatMember(obj, "x", 0), ReadFloatMember(obj, "y", 0), ReadFloatMember(obj, "z", 0));
            return null;
        }

        static object SerializeVector3(Vector3 value) => new { x = value.x, y = value.y, z = value.z };

        static string Normalize(string value) => (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

        static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            return !string.IsNullOrWhiteSpace(value) && Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
        }

        static JToken GetToken(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
                    return token;
            return null;
        }

        static string GetString(JObject obj, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString().Trim();
        }

        static bool GetBool(JObject obj, bool defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && bool.TryParse(token.ToString(), out var value) ? value : defaultValue;
        }

        static int GetInt(JObject obj, int defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : defaultValue;
        }

        static float GetFloat(JObject obj, float defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return TryReadFloat(token, out var value) ? value : defaultValue;
        }

        static float ReadFloat(JArray arr, int index)
        {
            return arr.Count > index && TryReadFloat(arr[index], out var value) ? value : 0f;
        }

        static float ReadFloatMember(JObject obj, string property, float defaultValue)
        {
            return obj.TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var token) &&
                   TryReadFloat(token, out var value)
                ? value
                : defaultValue;
        }

        static bool TryReadFloat(JToken token, out float value)
        {
            value = 0f;
            if (token == null || token.Type == JTokenType.Null)
                return false;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = token.Value<float>();
                return true;
            }
            var text = token.ToString();
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
    }
}
