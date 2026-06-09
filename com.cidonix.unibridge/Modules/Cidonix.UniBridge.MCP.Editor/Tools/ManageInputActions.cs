#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Input Actions asset authoring without a hard Input System compile dependency.
    /// </summary>
    public static class ManageInputActions
    {
        const string ToolName = "UniBridge_ManageInputActions";

        public const string Title = "Manage Input Actions assets";

        public const string Description = @"Create, inspect, and patch .inputactions JSON assets, and wire common Input System scene components when the package is available.

Use this when an agent needs action maps, actions, bindings, control schemes, PlayerInput, player joining, UI input modules, on-screen controls, virtual mouse, or multiplayer event systems. The asset authoring path works through JSON and does not require UniBridge to compile against Input System assemblies.

Args:
    Action: Inspect, Create, CreateOrUpdate, AddMap, AddAction, AddBinding, AddControlScheme, WirePlayerInput, WirePlayerInputManager, WireUIInputModule, AddOnScreenButton, AddOnScreenStick, AddVirtualMouse, or AddMultiplayerEventSystem.
    Path/InputActionsPath: Assets/... .inputactions path.
    Maps: Full map specs for Create/CreateOrUpdate.
    MapName, ActionName, ActionType, ExpectedControlType: AddAction/AddBinding controls.
    Bindings: Binding specs with Path, Action, Groups, Interactions, Processors, Name, IsComposite, IsPartOfComposite.
    ControlSchemes: Control scheme specs.
    Target: GameObject for scene wiring actions.
    ControlPath: On-screen control path such as <Gamepad>/buttonSouth or <Gamepad>/leftStick.
    PlayerPrefab: Optional prefab for PlayerInputManager.
    Properties: Optional extra SerializedProperty/public-property patches for wired components.

Returns:
    success, message, and data with action-map summaries, binding counts, file path, and optional PlayerInput wiring details.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new { type = "string", description = "Input Actions operation.", @enum = new[] { "Inspect", "Create", "CreateOrUpdate", "AddMap", "AddAction", "AddBinding", "AddControlScheme", "WirePlayerInput", "WirePlayerInputManager", "WireUIInputModule", "AddOnScreenButton", "AddOnScreenStick", "AddVirtualMouse", "AddMultiplayerEventSystem" } },
                    Path = new { type = "string", description = "Assets/... .inputactions path." },
                    InputActionsPath = new { type = "string", description = "Alias for Path." },
                    Name = new { type = "string", description = "Asset name. Defaults to file name." },
                    Maps = new { type = "array", description = "Input action map specs.", items = new { type = "object", additionalProperties = true } },
                    MapName = new { type = "string", description = "Action map name." },
                    ActionName = new { type = "string", description = "Action name." },
                    ActionType = new { type = "string", description = "Button, Value, or PassThrough.", @default = "Button" },
                    ExpectedControlType = new { type = "string", description = "Expected control type, e.g. Button, Vector2, Axis." },
                    Bindings = new { type = "array", description = "Binding specs.", items = new { type = "object", additionalProperties = true } },
                    BindingPath = new { type = "string", description = "Single binding path, e.g. <Keyboard>/space." },
                    Groups = new { type = "string", description = "Binding groups." },
                    ControlSchemes = new { type = "array", description = "Control scheme specs.", items = new { type = "object", additionalProperties = true } },
                    Target = new { description = "GameObject target for WirePlayerInput.", anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    DefaultActionMap = new { type = "string", description = "Default action map for PlayerInput." },
                    ControlPath = new { type = "string", description = "On-screen control path." },
                    PlayerPrefab = new { type = "string", description = "Prefab asset path or scene object reference for PlayerInputManager." },
                    Properties = new { type = "object", additionalProperties = true }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "assets", "input", "scene" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = Normalize(GetString(parameters, "Action", "action") ?? "Inspect");
            try
            {
                return action switch
                {
                    "inspect" => Inspect(parameters),
                    "create" or "createorupdate" or "upsert" => CreateOrUpdate(parameters),
                    "addmap" => Patch(parameters, PatchKind.Map),
                    "addaction" => Patch(parameters, PatchKind.Action),
                    "addbinding" => Patch(parameters, PatchKind.Binding),
                    "addcontrolscheme" => Patch(parameters, PatchKind.ControlScheme),
                    "wireplayerinput" or "wire" => WirePlayerInput(parameters),
                    "wireplayerinputmanager" or "playerinputmanager" or "joinmanager" => WirePlayerInputManager(parameters),
                    "wireuiinputmodule" or "uiinputmodule" or "inputsystemui" => WireUIInputModule(parameters),
                    "addonscreenbutton" or "onscreenbutton" => AddOnScreenButton(parameters),
                    "addonscreenstick" or "onscreenstick" => AddOnScreenStick(parameters),
                    "addvirtualmouse" or "virtualmouse" => AddVirtualMouse(parameters),
                    "addmultiplayereventsystem" or "multiplayereventsystem" => AddMultiplayerEventSystem(parameters),
                    _ => Response.Error($"Unknown InputActions action '{GetString(parameters, "Action", "action")}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageInputActions] Action '{action}' failed: {ex}");
                return Response.Error($"InputActions action '{action}' failed: {ex.Message}");
            }
        }

        static object Inspect(JObject parameters)
        {
            var path = ResolveInputActionsPath(parameters, required: true);
            var root = LoadOrCreateRoot(path, create: false, assetName: null);
            return Response.Success("Inspected Input Actions asset.", new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                summary = BuildSummary(root),
                json = GetBool(parameters, false, "IncludeJson", "includeJson", "include_json") ? root : null
            });
        }

        static object CreateOrUpdate(JObject parameters)
        {
            var path = ResolveInputActionsPath(parameters, required: true);
            var name = GetString(parameters, "Name", "name") ?? Path.GetFileNameWithoutExtension(path);
            var root = LoadOrCreateRoot(path, create: true, assetName: name);

            foreach (var mapSpec in GetArray(parameters, "Maps", "maps", "ActionMaps", "actionMaps", "action_maps")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                UpsertMap(root, mapSpec);

            foreach (var schemeSpec in GetArray(parameters, "ControlSchemes", "controlSchemes", "control_schemes")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                UpsertControlScheme(root, schemeSpec);

            SaveRoot(path, root);
            return Response.Success("Input Actions asset created or updated.", new { path, guid = AssetDatabase.AssetPathToGUID(path), summary = BuildSummary(root) });
        }

        static object Patch(JObject parameters, PatchKind kind)
        {
            var path = ResolveInputActionsPath(parameters, required: true);
            var root = LoadOrCreateRoot(path, create: true, assetName: Path.GetFileNameWithoutExtension(path));

            switch (kind)
            {
                case PatchKind.Map:
                    UpsertMap(root, BuildMapSpec(parameters));
                    break;
                case PatchKind.Action:
                    UpsertAction(EnsureMap(root, GetString(parameters, "MapName", "mapName", "map_name") ?? "Player"), BuildActionSpec(parameters));
                    break;
                case PatchKind.Binding:
                    var map = EnsureMap(root, GetString(parameters, "MapName", "mapName", "map_name") ?? "Player");
                    foreach (var binding in BuildBindingSpecs(parameters))
                        UpsertBinding(map, binding);
                    break;
                case PatchKind.ControlScheme:
                    foreach (var scheme in GetArray(parameters, "ControlSchemes", "controlSchemes", "control_schemes")?.OfType<JObject>() ?? new[] { BuildControlSchemeSpec(parameters) })
                        UpsertControlScheme(root, scheme);
                    break;
            }

            SaveRoot(path, root);
            return Response.Success("Input Actions asset patched.", new { path, guid = AssetDatabase.AssetPathToGUID(path), summary = BuildSummary(root) });
        }

        static object WirePlayerInput(JObject parameters)
        {
            var playerInputType = FindType("UnityEngine.InputSystem.PlayerInput");
            if (playerInputType == null)
                return Response.Error("Input System PlayerInput type was not found. Install/enable com.unity.inputsystem in this Unity project to wire PlayerInput.");

            var path = ResolveInputActionsPath(parameters, required: true);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            }

            if (asset == null)
                return Response.Error($"Input Actions asset could not be loaded at '{path}'.");

            var target = ResolveTarget(parameters);
            if (target == null)
                return Response.Error("Target GameObject was not found for PlayerInput wiring.");

            var component = target.GetComponent(playerInputType);
            if (component == null)
                component = Undo.AddComponent(target, playerInputType);
            if (component == null)
                return Response.Error($"PlayerInput component could not be added to '{target.name}'.");
            var serializedObject = new SerializedObject(component);
            SetObjectReference(serializedObject, asset, "m_Actions", "actions");
            var defaultMap = GetString(parameters, "DefaultActionMap", "defaultActionMap", "default_action_map", "MapName", "mapName", "map_name");
            if (!string.IsNullOrWhiteSpace(defaultMap))
                SetString(serializedObject, defaultMap, "m_DefaultActionMap", "defaultActionMap");
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("PlayerInput wired.", new
            {
                target = new { name = target.name, instanceId = UnityApiAdapter.GetObjectId(target), path = SceneObjectLocator.GetHierarchyPath(target) },
                componentType = playerInputType.FullName,
                inputActionsPath = path,
                defaultActionMap = defaultMap
            });
        }

        static object WirePlayerInputManager(JObject parameters)
        {
            return WireInputSystemComponent(
                parameters,
                "PlayerInputManager",
                "Input System PlayerInputManager",
                targetRequired: false,
                fallbackName: "Player Input Manager",
                component =>
                {
                    var serializedObject = new SerializedObject(component);
                    var prefab = ResolveObjectReference(GetString(parameters, "PlayerPrefab", "playerPrefab", "player_prefab"));
                    if (prefab != null)
                        SetObjectReference(serializedObject, prefab, "m_PlayerPrefab", "playerPrefab");

                    SetInteger(serializedObject, parameters, "m_MaxPlayerCount", "maxPlayerCount", "MaxPlayerCount", "max_player_count");
                    SetString(serializedObject, GetString(parameters, "JoinBehavior", "joinBehavior", "join_behavior"), "m_JoinBehavior", "joinBehavior");
                    SetString(serializedObject, GetString(parameters, "NotificationBehavior", "notificationBehavior", "notification_behavior"), "m_NotificationBehavior", "notificationBehavior");
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                },
                "UnityEngine.InputSystem.PlayerInputManager");
        }

        static object WireUIInputModule(JObject parameters)
        {
            return WireInputSystemComponent(
                parameters,
                "InputSystemUIInputModule",
                "Input System UI input module",
                targetRequired: false,
                fallbackName: "EventSystem",
                component =>
                {
                    var eventSystem = component.GetComponent<EventSystem>();
                    if (eventSystem == null)
                        eventSystem = Undo.AddComponent<EventSystem>(component.gameObject);

                    var serializedObject = new SerializedObject(component);
                    var asset = LoadOptionalInputActionsAsset(parameters);
                    if (asset != null)
                        SetObjectReference(serializedObject, asset, "m_ActionsAsset", "actionsAsset", "m_Actions", "actions");
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(eventSystem);
                },
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule");
        }

        static object AddOnScreenButton(JObject parameters)
        {
            return WireInputSystemComponent(
                parameters,
                "OnScreenButton",
                "Input System on-screen button",
                targetRequired: false,
                fallbackName: "On-Screen Button",
                component => ConfigureOnScreenControl(component, parameters, "<Gamepad>/buttonSouth"),
                "UnityEngine.InputSystem.OnScreen.OnScreenButton",
                "UnityEngine.InputSystem.OnScreenButton");
        }

        static object AddOnScreenStick(JObject parameters)
        {
            return WireInputSystemComponent(
                parameters,
                "OnScreenStick",
                "Input System on-screen stick",
                targetRequired: false,
                fallbackName: "On-Screen Stick",
                component =>
                {
                    ConfigureOnScreenControl(component, parameters, "<Gamepad>/leftStick");
                    var serializedObject = new SerializedObject(component);
                    SetFloat(serializedObject, parameters, "m_MovementRange", "MovementRange", "movementRange", "movement_range");
                    SetFloat(serializedObject, parameters, "m_DynamicOriginRange", "DynamicOriginRange", "dynamicOriginRange", "dynamic_origin_range");
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                },
                "UnityEngine.InputSystem.OnScreen.OnScreenStick",
                "UnityEngine.InputSystem.OnScreenStick");
        }

        static object AddVirtualMouse(JObject parameters)
        {
            return WireInputSystemComponent(
                parameters,
                "VirtualMouseInput",
                "Input System virtual mouse",
                targetRequired: false,
                fallbackName: "Virtual Mouse",
                component => { },
                "UnityEngine.InputSystem.UI.VirtualMouseInput");
        }

        static object AddMultiplayerEventSystem(JObject parameters)
        {
            return WireInputSystemComponent(
                parameters,
                "MultiplayerEventSystem",
                "Input System multiplayer event system",
                targetRequired: false,
                fallbackName: "Multiplayer EventSystem",
                component => { },
                "UnityEngine.InputSystem.UI.MultiplayerEventSystem");
        }

        static object WireInputSystemComponent(
            JObject parameters,
            string shortTypeName,
            string label,
            bool targetRequired,
            string fallbackName,
            Action<Component> configure,
            params string[] typeNames)
        {
            var componentType = FindType(typeNames);
            if (componentType == null)
                return Response.Error($"{label} type was not found. Install/enable com.unity.inputsystem in this Unity project to wire this component.");

            var target = ResolveTarget(parameters);
            if (target == null)
            {
                if (targetRequired)
                    return Response.Error($"Target GameObject was not found for {shortTypeName} wiring.");

                target = new GameObject(GetString(parameters, "Name", "name") ?? fallbackName);
                Undo.RegisterCreatedObjectUndo(target, $"Create {shortTypeName}");
            }

            var component = target.GetComponent(componentType);
            if (component == null)
                component = Undo.AddComponent(target, componentType);
            if (component == null)
                return Response.Error($"{shortTypeName} component could not be added to '{target.name}'.");

            Undo.RecordObject(component, $"Configure {shortTypeName}");
            configure?.Invoke(component);
            ApplyExtraProperties(component, parameters);

            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success($"{shortTypeName} wired.", new
            {
                target = new { name = target.name, instanceId = UnityApiAdapter.GetObjectId(target), path = SceneObjectLocator.GetHierarchyPath(target) },
                componentType = componentType.FullName,
                component = SummarizeComponent(component)
            });
        }

        static void ConfigureOnScreenControl(Component component, JObject parameters, string defaultControlPath)
        {
            var controlPath = GetString(parameters, "ControlPath", "controlPath", "control_path", "BindingPath", "bindingPath", "binding_path") ?? defaultControlPath;
            var serializedObject = new SerializedObject(component);
            SetString(serializedObject, controlPath, "m_ControlPath", "controlPath");
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        static UnityEngine.Object LoadOptionalInputActionsAsset(JObject parameters)
        {
            var path = ResolveInputActionsPath(parameters, required: false);
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null)
                return asset;

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        }

        static UnityEngine.Object ResolveObjectReference(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var assetPath = NormalizeAssetPath(value);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null)
                return asset;

            return SceneObjectLocator.FindObject(value, "by_id_or_name_or_path", new SceneObjectLocator.Options
            {
                IncludeInactive = true,
                IncludePrefabStage = true,
                IncludeDontDestroyOnLoad = true
            });
        }

        static void ApplyExtraProperties(UnityEngine.Object component, JObject parameters)
        {
            var props = parameters["Properties"] as JObject ?? parameters["properties"] as JObject;
            if (props == null)
                return;

            var serializedObject = new SerializedObject(component);
            foreach (var property in props.Properties())
            {
                var result = SerializedPropertyPatcher.TryApplyProperty(component, serializedObject, property.Name, property.Value, dryRun: false);
                if (!result.Success)
                    TrySetPublicMember(component, property.Name, property.Value);
            }
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        static bool TrySetPublicMember(UnityEngine.Object target, string name, JToken value)
        {
            var normalized = Normalize(name);
            var type = target.GetType();
            var property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(prop => prop.CanWrite && Normalize(prop.Name) == normalized);
            if (property != null)
            {
                try
                {
                    property.SetValue(target, ConvertValue(value, property.PropertyType));
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            var field = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(item => Normalize(item.Name) == normalized);
            if (field == null)
                return false;

            try
            {
                field.SetValue(target, ConvertValue(value, field.FieldType));
                return true;
            }
            catch
            {
                return false;
            }
        }

        static object ConvertValue(JToken value, Type targetType)
        {
            if (targetType.IsEnum)
                return value.Type == JTokenType.Integer ? Enum.ToObject(targetType, value.ToObject<int>()) : Enum.Parse(targetType, value.ToString(), true);
            if (targetType == typeof(UnityEngine.Object) || typeof(UnityEngine.Object).IsAssignableFrom(targetType))
                return ResolveObjectReference(value.ToString());
            return value.ToObject(targetType);
        }

        static object SummarizeComponent(Component component)
        {
            return new
            {
                type = component.GetType().FullName,
                enabled = component is Behaviour behaviour ? behaviour.enabled : (bool?)null
            };
        }

        static JObject LoadOrCreateRoot(string path, bool create, string assetName)
        {
            if (!File.Exists(ToFullPath(path)))
            {
                if (!create)
                    throw new FileNotFoundException($"Input Actions file was not found at '{path}'.");
                EnsureParentDirectory(path);
                return NewRoot(assetName ?? Path.GetFileNameWithoutExtension(path));
            }

            var text = File.ReadAllText(ToFullPath(path));
            return string.IsNullOrWhiteSpace(text) ? NewRoot(assetName ?? Path.GetFileNameWithoutExtension(path)) : JObject.Parse(text);
        }

        static JObject NewRoot(string name)
        {
            return new JObject
            {
                ["name"] = string.IsNullOrWhiteSpace(name) ? "InputActions" : name,
                ["maps"] = new JArray(),
                ["controlSchemes"] = new JArray()
            };
        }

        static void SaveRoot(string path, JObject root)
        {
            EnsureParentDirectory(path);
            VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true);
            File.WriteAllText(ToFullPath(path), root.ToString(Formatting.Indented));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
        }

        static JObject BuildMapSpec(JObject parameters)
        {
            var map = new JObject
            {
                ["name"] = GetString(parameters, "MapName", "mapName", "map_name", "Name", "name") ?? "Player"
            };
            if (GetArray(parameters, "Actions", "actions") is JArray actions)
                map["actions"] = actions.DeepClone();
            if (GetArray(parameters, "Bindings", "bindings") is JArray bindings)
                map["bindings"] = bindings.DeepClone();
            return map;
        }

        static JObject BuildActionSpec(JObject parameters)
        {
            return new JObject
            {
                ["name"] = GetString(parameters, "ActionName", "actionName", "action_name", "Name", "name") ?? "Action",
                ["type"] = GetString(parameters, "ActionType", "actionType", "action_type", "Type", "type") ?? "Button",
                ["expectedControlType"] = GetString(parameters, "ExpectedControlType", "expectedControlType", "expected_control_type") ?? string.Empty,
                ["processors"] = GetString(parameters, "Processors", "processors") ?? string.Empty,
                ["interactions"] = GetString(parameters, "Interactions", "interactions") ?? string.Empty
            };
        }

        static IEnumerable<JObject> BuildBindingSpecs(JObject parameters)
        {
            var bindings = GetArray(parameters, "Bindings", "bindings");
            if (bindings != null)
                return bindings.OfType<JObject>();

            return new[]
            {
                new JObject
                {
                    ["path"] = GetString(parameters, "BindingPath", "bindingPath", "binding_path", "Path", "path") ?? string.Empty,
                    ["action"] = GetString(parameters, "ActionName", "actionName", "action_name") ?? string.Empty,
                    ["groups"] = GetString(parameters, "Groups", "groups") ?? string.Empty,
                    ["interactions"] = GetString(parameters, "Interactions", "interactions") ?? string.Empty,
                    ["processors"] = GetString(parameters, "Processors", "processors") ?? string.Empty,
                    ["name"] = GetString(parameters, "BindingName", "bindingName", "binding_name", "Name", "name") ?? string.Empty,
                    ["isComposite"] = GetBool(parameters, false, "IsComposite", "isComposite", "is_composite"),
                    ["isPartOfComposite"] = GetBool(parameters, false, "IsPartOfComposite", "isPartOfComposite", "is_part_of_composite")
                }
            };
        }

        static JObject BuildControlSchemeSpec(JObject parameters)
        {
            return new JObject
            {
                ["name"] = GetString(parameters, "ControlSchemeName", "controlSchemeName", "control_scheme_name", "Name", "name") ?? "Keyboard&Mouse",
                ["bindingGroup"] = GetString(parameters, "BindingGroup", "bindingGroup", "binding_group", "Groups", "groups") ?? "Keyboard&Mouse",
                ["devices"] = GetArray(parameters, "Devices", "devices") ?? new JArray()
            };
        }

        static void UpsertMap(JObject root, JObject spec)
        {
            var name = GetString(spec, "name", "Name", "MapName", "mapName", "map_name") ?? "Player";
            var maps = EnsureArray(root, "maps");
            var map = maps.OfType<JObject>().FirstOrDefault(m => string.Equals(m.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase));
            if (map == null)
            {
                map = new JObject { ["name"] = name, ["id"] = Guid.NewGuid().ToString(), ["actions"] = new JArray(), ["bindings"] = new JArray() };
                maps.Add(map);
            }

            foreach (var action in (spec["actions"] as JArray ?? spec["Actions"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                UpsertAction(map, action);
            foreach (var binding in (spec["bindings"] as JArray ?? spec["Bindings"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                UpsertBinding(map, binding);
        }

        static JObject EnsureMap(JObject root, string name)
        {
            var maps = EnsureArray(root, "maps");
            var map = maps.OfType<JObject>().FirstOrDefault(m => string.Equals(m.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase));
            if (map != null)
                return map;

            map = new JObject { ["name"] = name, ["id"] = Guid.NewGuid().ToString(), ["actions"] = new JArray(), ["bindings"] = new JArray() };
            maps.Add(map);
            return map;
        }

        static void UpsertAction(JObject map, JObject spec)
        {
            var name = GetString(spec, "name", "Name", "ActionName", "actionName", "action_name") ?? "Action";
            var actions = EnsureArray(map, "actions");
            var action = actions.OfType<JObject>().FirstOrDefault(a => string.Equals(a.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase));
            if (action == null)
            {
                action = new JObject { ["name"] = name, ["id"] = Guid.NewGuid().ToString() };
                actions.Add(action);
            }

            action["type"] = GetString(spec, "type", "Type", "ActionType", "actionType", "action_type") ?? action.Value<string>("type") ?? "Button";
            action["expectedControlType"] = GetString(spec, "expectedControlType", "ExpectedControlType", "expected_control_type") ?? action.Value<string>("expectedControlType") ?? string.Empty;
            action["processors"] = GetString(spec, "processors", "Processors") ?? action.Value<string>("processors") ?? string.Empty;
            action["interactions"] = GetString(spec, "interactions", "Interactions") ?? action.Value<string>("interactions") ?? string.Empty;
        }

        static void UpsertBinding(JObject map, JObject spec)
        {
            var bindings = EnsureArray(map, "bindings");
            var path = GetString(spec, "path", "Path", "BindingPath", "bindingPath", "binding_path") ?? string.Empty;
            var actionName = GetString(spec, "action", "Action", "ActionName", "actionName", "action_name") ?? string.Empty;
            var binding = bindings.OfType<JObject>().FirstOrDefault(b =>
                string.Equals(b.Value<string>("path"), path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.Value<string>("action"), actionName, StringComparison.OrdinalIgnoreCase));
            if (binding == null)
            {
                binding = new JObject { ["id"] = Guid.NewGuid().ToString() };
                bindings.Add(binding);
            }

            binding["name"] = GetString(spec, "name", "Name", "BindingName", "bindingName", "binding_name") ?? binding.Value<string>("name") ?? string.Empty;
            binding["path"] = path;
            binding["interactions"] = GetString(spec, "interactions", "Interactions") ?? binding.Value<string>("interactions") ?? string.Empty;
            binding["processors"] = GetString(spec, "processors", "Processors") ?? binding.Value<string>("processors") ?? string.Empty;
            binding["groups"] = GetString(spec, "groups", "Groups") ?? binding.Value<string>("groups") ?? string.Empty;
            binding["action"] = actionName;
            binding["isComposite"] = GetBool(spec, binding.Value<bool?>("isComposite") ?? false, "isComposite", "IsComposite", "is_composite");
            binding["isPartOfComposite"] = GetBool(spec, binding.Value<bool?>("isPartOfComposite") ?? false, "isPartOfComposite", "IsPartOfComposite", "is_part_of_composite");
        }

        static void UpsertControlScheme(JObject root, JObject spec)
        {
            var name = GetString(spec, "name", "Name", "ControlSchemeName", "controlSchemeName", "control_scheme_name") ?? "Keyboard&Mouse";
            var schemes = EnsureArray(root, "controlSchemes");
            var scheme = schemes.OfType<JObject>().FirstOrDefault(s => string.Equals(s.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase));
            if (scheme == null)
            {
                scheme = new JObject { ["name"] = name };
                schemes.Add(scheme);
            }

            scheme["bindingGroup"] = GetString(spec, "bindingGroup", "BindingGroup", "binding_group", "groups", "Groups") ?? scheme.Value<string>("bindingGroup") ?? name;
            scheme["devices"] = spec["devices"]?.DeepClone() ?? spec["Devices"]?.DeepClone() ?? scheme["devices"] ?? new JArray();
        }

        static JArray EnsureArray(JObject obj, string key)
        {
            if (obj[key] is not JArray arr)
            {
                arr = new JArray();
                obj[key] = arr;
            }

            return arr;
        }

        static object BuildSummary(JObject root)
        {
            var maps = EnsureArray(root, "maps").OfType<JObject>().Select(map => new
            {
                name = map.Value<string>("name"),
                id = map.Value<string>("id"),
                actionCount = (map["actions"] as JArray)?.Count ?? 0,
                bindingCount = (map["bindings"] as JArray)?.Count ?? 0,
                actions = (map["actions"] as JArray)?.OfType<JObject>().Select(a => new { name = a.Value<string>("name"), type = a.Value<string>("type"), expectedControlType = a.Value<string>("expectedControlType") }).ToArray()
            }).ToArray();

            return new
            {
                name = root.Value<string>("name"),
                mapCount = maps.Length,
                maps,
                controlSchemeCount = (root["controlSchemes"] as JArray)?.Count ?? 0
            };
        }

        static GameObject ResolveTarget(JObject parameters)
        {
            var target = GetToken(parameters, "Target", "target", "GameObject", "gameObject", "game_object");
            if (target == null || target.Type == JTokenType.Null)
                return Selection.activeGameObject;
            return SceneObjectLocator.FindObject(target.ToString(), GetString(parameters, "SearchMethod", "searchMethod", "search_method"), new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true, IncludeDontDestroyOnLoad = true });
        }

        static void SetObjectReference(SerializedObject serializedObject, UnityEngine.Object value, params string[] paths)
        {
            foreach (var path in paths)
            {
                var property = serializedObject.FindProperty(path);
                if (property != null && property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    property.objectReferenceValue = value;
                    return;
                }
            }
        }

        static void SetString(SerializedObject serializedObject, string value, params string[] paths)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            foreach (var path in paths)
            {
                var property = serializedObject.FindProperty(path);
                if (property == null)
                    continue;

                if (property.propertyType == SerializedPropertyType.String)
                {
                    property.stringValue = value;
                    return;
                }

                if (property.propertyType == SerializedPropertyType.Enum)
                {
                    for (var i = 0; i < property.enumNames.Length; i++)
                    {
                        if (string.Equals(property.enumNames[i], value, StringComparison.OrdinalIgnoreCase))
                        {
                            property.enumValueIndex = i;
                            return;
                        }
                    }
                }
            }
        }

        static void SetInteger(SerializedObject serializedObject, JObject parameters, string serializedPath, params string[] keys)
        {
            var token = GetToken(parameters, keys);
            if (token == null || token.Type == JTokenType.Null || !int.TryParse(token.ToString(), out var value))
                return;

            var property = serializedObject.FindProperty(serializedPath);
            if (property == null)
                return;

            if (property.propertyType == SerializedPropertyType.Integer)
                property.intValue = value;
            else if (property.propertyType == SerializedPropertyType.Enum && value >= 0 && value < property.enumNames.Length)
                property.enumValueIndex = value;
        }

        static void SetFloat(SerializedObject serializedObject, JObject parameters, string serializedPath, params string[] keys)
        {
            var token = GetToken(parameters, keys);
            if (token == null || token.Type == JTokenType.Null || !float.TryParse(token.ToString(), out var value))
                return;

            var property = serializedObject.FindProperty(serializedPath);
            if (property != null && property.propertyType == SerializedPropertyType.Float)
                property.floatValue = value;
        }

        static Type FindType(params string[] fullNames)
        {
            foreach (var fullName in fullNames.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type = null;
                    try { type = assembly.GetType(fullName, false); } catch { }
                    if (type != null)
                        return type;

                    try
                    {
                        type = assembly.GetTypes().FirstOrDefault(candidate =>
                            string.Equals(candidate.FullName, fullName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(candidate.Name, fullName, StringComparison.OrdinalIgnoreCase));
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        type = ex.Types.Where(candidate => candidate != null).FirstOrDefault(candidate =>
                            string.Equals(candidate.FullName, fullName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(candidate.Name, fullName, StringComparison.OrdinalIgnoreCase));
                    }
                    catch
                    {
                        type = null;
                    }

                    if (type != null)
                        return type;
                }
            }

            return null;
        }

        static string ResolveInputActionsPath(JObject parameters, bool required)
        {
            var path = NormalizeAssetPath(GetString(parameters, "Path", "path", "InputActionsPath", "inputActionsPath", "input_actions_path", "AssetPath", "assetPath", "asset_path"));
            if (required && string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Path/InputActionsPath is required.");
            if (!string.IsNullOrWhiteSpace(path) && !path.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Input Actions path '{path}' must end with .inputactions.");
            return path;
        }

        static void EnsureParentDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || AssetDatabase.IsValidFolder(directory))
                return;

            var parts = directory.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static string ToFullPath(string assetPath) => Path.GetFullPath(assetPath);

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var normalized = path.Trim().Replace('\\', '/').TrimStart('/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return normalized;
            return "Assets/" + normalized;
        }

        static string Normalize(string value) => (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

        static JToken GetToken(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
                    return token;
            return null;
        }

        static JArray GetArray(JObject obj, params string[] keys)
        {
            return keys.Select(key => obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) ? token as JArray : null).FirstOrDefault(arr => arr != null);
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

        enum PatchKind
        {
            Map,
            Action,
            Binding,
            ControlScheme
        }
    }
}
