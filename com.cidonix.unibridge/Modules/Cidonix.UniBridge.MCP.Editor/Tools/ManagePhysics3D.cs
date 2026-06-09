#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// High-level 3D physics authoring presets and component setup.
    /// </summary>
    public static class ManagePhysics3D
    {
        const string ToolName = "UniBridge_ManagePhysics3D";

        public const string Title = "Manage Physics3D presets";

        public const string Description = @"Apply common Physics3D presets and author Rigidbody, Collider, Joint, CharacterController, and PhysicsMaterial assets.

Use this when quickly prototyping 3D gameplay objects: static colliders, dynamic rigidbodies, kinematic movers, trigger volumes, character controllers, joints, and reusable PhysicsMaterial assets.

Args:
    Action: Inspect, ApplyPreset, AddRigidbody, AddCollider, AddJoint, AddCharacterController, or CreateMaterial.
    Target: GameObject name/path/id for scene operations.
    Preset: StaticCollider, DynamicBody, KinematicBody, TriggerVolume, BouncyDynamic, HeavyCrate, CharacterController.
    ColliderKind: Box, Sphere, Capsule, Mesh, Wheel.
    Size, Center, Radius, Height, Direction, Convex, IsTrigger, MaterialPath: Collider controls.
    UseGravity, IsKinematic, Mass, LinearDamping, AngularDamping, FreezePosition, FreezeRotation: Rigidbody controls.
    JointType: Fixed, Hinge, Spring, Character, Configurable.
    ConnectedBodyTarget: Optional GameObject with Rigidbody for joint.
    Path, DynamicFriction, StaticFriction, Bounciness, FrictionCombine, BounceCombine: PhysicsMaterial asset controls.

Returns:
    success, message, and data with created/updated component summaries and material paths.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new { type = "string", @enum = new[] { "Inspect", "ApplyPreset", "AddRigidbody", "AddCollider", "AddJoint", "AddCharacterController", "CreateMaterial" } },
                    Target = new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    SearchMethod = new { type = "string" },
                    Preset = new { type = "string", @enum = new[] { "StaticCollider", "DynamicBody", "KinematicBody", "TriggerVolume", "BouncyDynamic", "HeavyCrate", "CharacterController" } },
                    ColliderKind = new { type = "string", @enum = new[] { "Box", "Sphere", "Capsule", "Mesh", "Wheel" } },
                    Size = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    Center = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                    Radius = new { type = "number" },
                    Height = new { type = "number" },
                    Direction = new { type = "string", @enum = new[] { "X", "Y", "Z" } },
                    Convex = new { type = "boolean" },
                    IsTrigger = new { type = "boolean" },
                    MaterialPath = new { type = "string" },
                    UseGravity = new { type = "boolean" },
                    IsKinematic = new { type = "boolean" },
                    Mass = new { type = "number" },
                    LinearDamping = new { type = "number" },
                    AngularDamping = new { type = "number" },
                    FreezePosition = new { type = "boolean" },
                    FreezeRotation = new { type = "boolean" },
                    JointType = new { type = "string" },
                    ConnectedBodyTarget = new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    Path = new { type = "string" },
                    DynamicFriction = new { type = "number" },
                    StaticFriction = new { type = "number" },
                    Bounciness = new { type = "number" },
                    FrictionCombine = new { type = "string" },
                    BounceCombine = new { type = "string" },
                    Properties = new { type = "object", additionalProperties = true }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "3d", "physics" }, EnabledByDefault = true)]
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
                    "addrigidbody" or "rigidbody" => AddRigidbody(parameters),
                    "addcollider" or "collider" => AddCollider(parameters),
                    "addjoint" or "joint" => AddJoint(parameters),
                    "addcharactercontroller" or "charactercontroller" or "controller" => AddCharacterController(parameters),
                    "creatematerial" or "createphysicmaterial" or "material" => CreateMaterial(parameters),
                    _ => Response.Error($"Unknown Physics3D action '{GetString(parameters, "Action", "action")}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagePhysics3D] Action '{action}' failed: {ex}");
                return Response.Error($"Physics3D action '{action}' failed: {ex.Message}");
            }
        }

        static object Inspect(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: false);
            if (target == null)
            {
                var objects = SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true })
                    .Where(go => go.GetComponent<Rigidbody>() != null || go.GetComponents<Collider>().Length > 0 || go.GetComponent<Joint>() != null || go.GetComponent<CharacterController>() != null)
                    .Select(BuildPhysicsSummary)
                    .ToArray();
                return Response.Success("Listed Physics3D objects.", new { count = objects.Length, objects });
            }

            return Response.Success("Inspected Physics3D object.", BuildPhysicsSummary(target));
        }

        static object ApplyPreset(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var preset = Normalize(GetString(parameters, "Preset", "preset") ?? "DynamicBody");
            var applied = new List<object>();

            switch (preset)
            {
                case "staticcollider":
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Box", parameters, false));
                    break;
                case "dynamicbody":
                    applied.Add(EnsureRigidbody(target, parameters, isKinematic: false, useGravity: true));
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Box", parameters, false));
                    break;
                case "kinematicbody":
                    applied.Add(EnsureRigidbody(target, parameters, isKinematic: true, useGravity: false));
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Box", parameters, false));
                    break;
                case "triggervolume":
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Box", parameters, true));
                    break;
                case "bouncydynamic":
                    if (parameters["Bounciness"] == null) parameters["Bounciness"] = 0.85f;
                    if (parameters["DynamicFriction"] == null) parameters["DynamicFriction"] = 0.1f;
                    if (parameters["StaticFriction"] == null) parameters["StaticFriction"] = 0.1f;
                    applied.Add(EnsureRigidbody(target, parameters, isKinematic: false, useGravity: true));
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Sphere", parameters, false));
                    break;
                case "heavycrate":
                    if (parameters["Mass"] == null) parameters["Mass"] = 12f;
                    if (parameters["LinearDamping"] == null) parameters["LinearDamping"] = 0.05f;
                    applied.Add(EnsureRigidbody(target, parameters, isKinematic: false, useGravity: true));
                    applied.Add(EnsureCollider(target, "Box", parameters, false));
                    break;
                case "charactercontroller":
                    applied.Add(EnsureCharacterController(target, parameters));
                    break;
                default:
                    return Response.Error($"Unknown Physics3D preset '{GetString(parameters, "Preset", "preset")}'.");
            }

            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Physics3D preset applied.", new { target = BuildPhysicsSummary(target), applied });
        }

        static object AddRigidbody(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var result = EnsureRigidbody(
                target,
                parameters,
                GetBool(parameters, false, "IsKinematic", "isKinematic", "is_kinematic"),
                GetBool(parameters, true, "UseGravity", "useGravity", "use_gravity"));
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Rigidbody added or updated.", new { target = BuildPhysicsSummary(target), component = result });
        }

        static object AddCollider(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var result = EnsureCollider(
                target,
                GetString(parameters, "ColliderKind", "colliderKind", "collider_kind", "Kind", "kind") ?? "Box",
                parameters,
                GetBool(parameters, false, "IsTrigger", "isTrigger", "is_trigger"));
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Collider added or updated.", new { target = BuildPhysicsSummary(target), component = result });
        }

        static object AddJoint(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var jointType = ResolveJointType(GetString(parameters, "JointType", "jointType", "joint_type") ?? "Fixed");
            var joint = target.GetComponent(jointType) as Joint;
            if (joint == null)
                joint = Undo.AddComponent(target, jointType) as Joint;
            if (joint == null)
                return Response.Error($"{jointType.Name} component could not be added to '{target.name}'.");

            Undo.RecordObject(joint, "Configure Joint");
            var connectedToken = GetToken(parameters, "ConnectedBodyTarget", "connectedBodyTarget", "connected_body_target");
            if (connectedToken != null)
            {
                var connectedGo = SceneObjectLocator.FindObject(connectedToken.ToString(), "by_id_or_name_or_path", new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
                var rb = connectedGo != null ? connectedGo.GetComponent<Rigidbody>() : null;
                if (rb != null)
                    joint.connectedBody = rb;
            }

            ApplyExtraProperties(joint, parameters);
            EditorUtility.SetDirty(joint);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Joint added or updated.", new { target = BuildPhysicsSummary(target), component = BuildComponentSummary(joint) });
        }

        static object AddCharacterController(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var result = EnsureCharacterController(target, parameters);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("CharacterController added or updated.", new { target = BuildPhysicsSummary(target), component = result });
        }

        static object CreateMaterial(JObject parameters)
        {
            var path = NormalizeAssetPath(GetString(parameters, "Path", "path", "MaterialPath", "materialPath", "material_path") ?? "Assets/Physics3D/NewPhysicMaterial.physicMaterial");
            EnsureParentDirectory(path);
            var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            var created = false;
            if (material == null)
            {
                material = new PhysicsMaterial(Path.GetFileNameWithoutExtension(path));
                AssetDatabase.CreateAsset(material, path);
                created = true;
            }
            else
            {
                VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true);
            }

            Undo.RecordObject(material, "Configure PhysicsMaterial");
            ApplyMaterialValues(material, parameters);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return Response.Success(created ? "PhysicsMaterial created." : "PhysicsMaterial updated.", BuildMaterialSummary(path, material));
        }

        static object EnsureRigidbody(GameObject target, JObject parameters, bool isKinematic, bool useGravity)
        {
            var rb = target.GetComponent<Rigidbody>();
            if (rb == null)
                rb = Undo.AddComponent<Rigidbody>(target);
            if (rb == null)
                throw new InvalidOperationException($"Rigidbody component could not be added to '{target.name}'.");

            Undo.RecordObject(rb, "Configure Rigidbody");
            rb.mass = GetFloat(parameters, rb.mass, "Mass", "mass");
            rb.useGravity = GetBool(parameters, useGravity, "UseGravity", "useGravity", "use_gravity");
            rb.isKinematic = GetBool(parameters, isKinematic, "IsKinematic", "isKinematic", "is_kinematic");
            rb.linearDamping = GetFloat(parameters, rb.linearDamping, "LinearDamping", "linearDamping", "linear_damping", "LinearDrag", "linearDrag", "linear_drag");
            rb.angularDamping = GetFloat(parameters, rb.angularDamping, "AngularDamping", "angularDamping", "angular_damping", "AngularDrag", "angularDrag", "angular_drag");

            if (GetBool(parameters, false, "FreezePosition", "freezePosition", "freeze_position"))
                rb.constraints |= RigidbodyConstraints.FreezePosition;
            if (GetBool(parameters, false, "FreezeRotation", "freezeRotation", "freeze_rotation"))
                rb.constraints |= RigidbodyConstraints.FreezeRotation;

            ApplyExtraProperties(rb, parameters);
            EditorUtility.SetDirty(rb);
            return BuildComponentSummary(rb);
        }

        static object EnsureCollider(GameObject target, string kind, JObject parameters, bool defaultTrigger)
        {
            var type = ResolveColliderType(kind);
            var collider = target.GetComponent(type) as Collider;
            if (collider == null)
                collider = Undo.AddComponent(target, type) as Collider;
            if (collider == null)
                throw new InvalidOperationException($"{type.Name} component could not be added to '{target.name}'.");

            Undo.RecordObject(collider, "Configure Collider");
            collider.isTrigger = GetBool(parameters, defaultTrigger, "IsTrigger", "isTrigger", "is_trigger");

            var center = ParseVector3(GetToken(parameters, "Center", "center", "Offset", "offset"));
            if (collider is BoxCollider box)
            {
                box.center = center ?? box.center;
                box.size = ParseVector3(GetToken(parameters, "Size", "size")) ?? box.size;
            }
            else if (collider is SphereCollider sphere)
            {
                sphere.center = center ?? sphere.center;
                sphere.radius = GetFloat(parameters, sphere.radius, "Radius", "radius");
            }
            else if (collider is CapsuleCollider capsule)
            {
                capsule.center = center ?? capsule.center;
                capsule.radius = GetFloat(parameters, capsule.radius, "Radius", "radius");
                capsule.height = GetFloat(parameters, capsule.height, "Height", "height");
                capsule.direction = ParseCapsuleDirection(GetString(parameters, "Direction", "direction"), capsule.direction);
            }
            else if (collider is MeshCollider mesh)
            {
                mesh.convex = GetBool(parameters, mesh.convex, "Convex", "convex");
            }
            else if (collider is WheelCollider wheel)
            {
                wheel.center = center ?? wheel.center;
                wheel.radius = GetFloat(parameters, wheel.radius, "Radius", "radius");
            }

            var materialPath = NormalizeAssetPath(GetString(parameters, "MaterialPath", "materialPath", "material_path"));
            if (!string.IsNullOrWhiteSpace(materialPath))
                collider.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(materialPath);
            else if (parameters["Bounciness"] != null || parameters["DynamicFriction"] != null || parameters["StaticFriction"] != null)
                collider.sharedMaterial = EnsureInlineMaterial(parameters);

            ApplyExtraProperties(collider, parameters);
            EditorUtility.SetDirty(collider);
            return BuildComponentSummary(collider);
        }

        static object EnsureCharacterController(GameObject target, JObject parameters)
        {
            var controller = target.GetComponent<CharacterController>();
            if (controller == null)
                controller = Undo.AddComponent<CharacterController>(target);
            if (controller == null)
                throw new InvalidOperationException($"CharacterController component could not be added to '{target.name}'.");

            Undo.RecordObject(controller, "Configure CharacterController");
            controller.center = ParseVector3(GetToken(parameters, "Center", "center", "Offset", "offset")) ?? controller.center;
            controller.radius = GetFloat(parameters, controller.radius, "Radius", "radius");
            controller.height = GetFloat(parameters, controller.height, "Height", "height");
            controller.slopeLimit = GetFloat(parameters, controller.slopeLimit, "SlopeLimit", "slopeLimit", "slope_limit");
            controller.stepOffset = GetFloat(parameters, controller.stepOffset, "StepOffset", "stepOffset", "step_offset");
            controller.skinWidth = GetFloat(parameters, controller.skinWidth, "SkinWidth", "skinWidth", "skin_width");
            controller.minMoveDistance = GetFloat(parameters, controller.minMoveDistance, "MinMoveDistance", "minMoveDistance", "min_move_distance");
            ApplyExtraProperties(controller, parameters);
            EditorUtility.SetDirty(controller);
            return BuildComponentSummary(controller);
        }

        static PhysicsMaterial EnsureInlineMaterial(JObject parameters)
        {
            var path = NormalizeAssetPath(GetString(parameters, "MaterialPath", "materialPath", "material_path") ?? "Assets/Physics3D/GeneratedPhysicMaterial.physicMaterial");
            EnsureParentDirectory(path);
            var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (material == null)
            {
                material = new PhysicsMaterial(Path.GetFileNameWithoutExtension(path));
                AssetDatabase.CreateAsset(material, path);
            }

            ApplyMaterialValues(material, parameters);
            EditorUtility.SetDirty(material);
            return material;
        }

        static void ApplyMaterialValues(PhysicsMaterial material, JObject parameters)
        {
            material.dynamicFriction = GetFloat(parameters, material.dynamicFriction, "DynamicFriction", "dynamicFriction", "dynamic_friction", "Friction", "friction");
            material.staticFriction = GetFloat(parameters, material.staticFriction, "StaticFriction", "staticFriction", "static_friction");
            material.bounciness = GetFloat(parameters, material.bounciness, "Bounciness", "bounciness", "Bounce", "bounce");
            material.frictionCombine = ParseEnum(GetString(parameters, "FrictionCombine", "frictionCombine", "friction_combine"), material.frictionCombine);
            material.bounceCombine = ParseEnum(GetString(parameters, "BounceCombine", "bounceCombine", "bounce_combine"), material.bounceCombine);
        }

        static void ApplyExtraProperties(UnityEngine.Object component, JObject parameters)
        {
            var props = parameters["Properties"] as JObject ?? parameters["properties"] as JObject;
            if (props == null)
                return;

            var so = new SerializedObject(component);
            foreach (var property in props.Properties())
                SerializedPropertyPatcher.TryApplyProperty(component, so, property.Name, property.Value, dryRun: false);
            so.ApplyModifiedPropertiesWithoutUndo();
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

        static object BuildPhysicsSummary(GameObject target)
        {
            return new
            {
                gameObject = new
                {
                    name = target.name,
                    instanceId = UnityApiAdapter.GetObjectId(target),
                    path = SceneObjectLocator.GetHierarchyPath(target),
                    scene = target.scene.IsValid() ? new { name = target.scene.name, path = target.scene.path } : null
                },
                rigidbody = target.GetComponent<Rigidbody>() != null ? BuildComponentSummary(target.GetComponent<Rigidbody>()) : null,
                characterController = target.GetComponent<CharacterController>() != null ? BuildComponentSummary(target.GetComponent<CharacterController>()) : null,
                colliders = target.GetComponents<Collider>().Select(BuildComponentSummary).ToArray(),
                joints = target.GetComponents<Joint>().Select(BuildComponentSummary).ToArray()
            };
        }

        static object BuildComponentSummary(Component component)
        {
            if (component == null)
                return null;

            return component switch
            {
                Rigidbody rb => new { type = rb.GetType().FullName, mass = rb.mass, useGravity = rb.useGravity, isKinematic = rb.isKinematic, linearDamping = rb.linearDamping, angularDamping = rb.angularDamping, constraints = rb.constraints.ToString() },
                CharacterController cc => new { type = cc.GetType().FullName, center = SerializeVector3(cc.center), radius = cc.radius, height = cc.height, slopeLimit = cc.slopeLimit, stepOffset = cc.stepOffset, skinWidth = cc.skinWidth },
                Collider c => new { type = c.GetType().FullName, enabled = c.enabled, isTrigger = c.isTrigger, material = c.sharedMaterial != null ? AssetDatabase.GetAssetPath(c.sharedMaterial) : null, bounds = SerializeBounds(c.bounds) },
                Joint j => new { type = j.GetType().FullName, connectedBody = j.connectedBody != null ? j.connectedBody.name : null, enableCollision = j.enableCollision, breakForce = j.breakForce, breakTorque = j.breakTorque },
                _ => new { type = component.GetType().FullName }
            };
        }

        static object BuildMaterialSummary(string path, PhysicsMaterial material)
        {
            return new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                material.dynamicFriction,
                material.staticFriction,
                material.bounciness,
                frictionCombine = material.frictionCombine.ToString(),
                bounceCombine = material.bounceCombine.ToString()
            };
        }

        static Type ResolveColliderType(string kind)
        {
            return Normalize(kind) switch
            {
                "box" or "boxcollider" => typeof(BoxCollider),
                "sphere" or "spherecollider" => typeof(SphereCollider),
                "capsule" or "capsulecollider" => typeof(CapsuleCollider),
                "mesh" or "meshcollider" => typeof(MeshCollider),
                "wheel" or "wheelcollider" => typeof(WheelCollider),
                _ => typeof(BoxCollider)
            };
        }

        static Type ResolveJointType(string kind)
        {
            return Normalize(kind) switch
            {
                "fixed" or "fixedjoint" => typeof(FixedJoint),
                "hinge" or "hingejoint" => typeof(HingeJoint),
                "spring" or "springjoint" => typeof(SpringJoint),
                "character" or "characterjoint" => typeof(CharacterJoint),
                "configurable" or "configurablejoint" => typeof(ConfigurableJoint),
                _ => typeof(FixedJoint)
            };
        }

        static int ParseCapsuleDirection(string value, int fallback)
        {
            return Normalize(value) switch
            {
                "x" or "axisx" => 0,
                "y" or "axisy" => 1,
                "z" or "axisz" => 2,
                _ => fallback
            };
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

        static object SerializeBounds(Bounds value) => new { center = SerializeVector3(value.center), size = SerializeVector3(value.size), min = SerializeVector3(value.min), max = SerializeVector3(value.max) };

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
