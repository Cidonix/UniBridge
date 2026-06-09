#nullable disable
using System;
using System.Collections.Generic;
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
    /// High-level 2D physics authoring presets and component setup.
    /// </summary>
    public static class ManagePhysics2D
    {
        const string ToolName = "UniBridge_ManagePhysics2D";

        public const string Title = "Manage Physics2D presets";

        public const string Description = @"Apply common Physics2D presets and author Rigidbody2D, Collider2D, Joint2D, Effector2D, and PhysicsMaterial2D assets.

Use this when quickly prototyping 2D gameplay objects: static solids, dynamic bodies, trigger sensors, one-way platforms, simple joints, and reusable PhysicsMaterial2D assets.

Args:
    Action: Inspect, ApplyPreset, AddRigidbody, AddCollider, AddJoint, AddEffector, or CreateMaterial.
    Target: GameObject name/path/id for scene operations.
    Preset: StaticSolid, DynamicBody, TriggerSensor, KinematicMover, OneWayPlatform, BouncyDynamic, SlipperySurface.
    ColliderKind: Box, Circle, Capsule, Polygon, Edge.
    Size, Offset, Radius, IsTrigger, MaterialPath: Collider controls.
    BodyType, GravityScale, Mass, LinearDrag, AngularDrag, FreezeRotation: Rigidbody controls.
    JointType: Distance, Fixed, Hinge, Spring, Slider, Target, Wheel, Relative, Friction.
    ConnectedBodyTarget: Optional GameObject with Rigidbody2D for joint.
    EffectorType: Platform, Surface, Point, Area, Buoyancy.
    Path, Friction, Bounciness: PhysicsMaterial2D asset controls.

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
                    Action = new { type = "string", description = "Physics2D operation.", @enum = new[] { "Inspect", "ApplyPreset", "AddRigidbody", "AddCollider", "AddJoint", "AddEffector", "CreateMaterial" } },
                    Target = new { description = "GameObject target.", anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    SearchMethod = new { type = "string", description = "Target search method." },
                    Preset = new { type = "string", description = "Physics2D preset.", @enum = new[] { "StaticSolid", "DynamicBody", "TriggerSensor", "KinematicMover", "OneWayPlatform", "BouncyDynamic", "SlipperySurface" } },
                    ColliderKind = new { type = "string", description = "Collider kind.", @enum = new[] { "Box", "Circle", "Capsule", "Polygon", "Edge" } },
                    Size = new { type = "array", description = "Collider size [x,y].", items = new { type = "number" }, minItems = 2, maxItems = 2 },
                    Offset = new { type = "array", description = "Collider offset [x,y].", items = new { type = "number" }, minItems = 2, maxItems = 2 },
                    Radius = new { type = "number", description = "Circle/Capsule radius." },
                    IsTrigger = new { type = "boolean", description = "Collider trigger flag." },
                    MaterialPath = new { type = "string", description = "PhysicsMaterial2D asset path." },
                    BodyType = new { type = "string", description = "Rigidbody2D body type.", @enum = new[] { "Dynamic", "Kinematic", "Static" } },
                    GravityScale = new { type = "number", description = "Rigidbody2D gravity scale." },
                    Mass = new { type = "number", description = "Rigidbody2D mass." },
                    FreezeRotation = new { type = "boolean", description = "Freeze Rigidbody2D rotation." },
                    JointType = new { type = "string", description = "Joint2D kind." },
                    ConnectedBodyTarget = new { description = "Connected body target for joints.", anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    EffectorType = new { type = "string", description = "Effector2D kind." },
                    Path = new { type = "string", description = "PhysicsMaterial2D asset path." },
                    Friction = new { type = "number", description = "PhysicsMaterial2D friction." },
                    Bounciness = new { type = "number", description = "PhysicsMaterial2D bounciness." },
                    Properties = new { type = "object", description = "Optional extra SerializedProperty patches for the created/updated component.", additionalProperties = true }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "2d", "physics" }, EnabledByDefault = true)]
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
                    "addeffector" or "effector" => AddEffector(parameters),
                    "creatematerial" or "createphysicsmaterial" or "material" => CreateMaterial(parameters),
                    _ => Response.Error($"Unknown Physics2D action '{GetString(parameters, "Action", "action")}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagePhysics2D] Action '{action}' failed: {ex}");
                return Response.Error($"Physics2D action '{action}' failed: {ex.Message}");
            }
        }

        static object Inspect(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: false);
            if (target == null)
            {
                var objects = SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true })
                    .Where(go => go.GetComponent<Rigidbody2D>() != null || go.GetComponents<Collider2D>().Length > 0 || go.GetComponent<Joint2D>() != null || go.GetComponent<Effector2D>() != null)
                    .Select(BuildPhysicsSummary)
                    .ToArray();
                return Response.Success("Listed Physics2D objects.", new { count = objects.Length, objects });
            }

            return Response.Success("Inspected Physics2D object.", BuildPhysicsSummary(target));
        }

        static object ApplyPreset(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var preset = Normalize(GetString(parameters, "Preset", "preset") ?? "DynamicBody");
            var applied = new List<object>();

            switch (preset)
            {
                case "staticsolid":
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Box", parameters, isTrigger: false));
                    break;
                case "dynamicbody":
                    applied.Add(EnsureRigidbody(target, parameters, RigidbodyType2D.Dynamic));
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Box", parameters, isTrigger: false));
                    break;
                case "kinematicmover":
                    applied.Add(EnsureRigidbody(target, parameters, RigidbodyType2D.Kinematic));
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Box", parameters, isTrigger: false));
                    break;
                case "triggersensor":
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Circle", parameters, isTrigger: true));
                    break;
                case "onewayplatform":
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Box", parameters, isTrigger: false, usedByEffector: true));
                    applied.Add(EnsureEffector(target, "Platform", parameters));
                    break;
                case "bouncydynamic":
                    if (parameters["Bounciness"] == null) parameters["Bounciness"] = 0.8f;
                    if (parameters["Friction"] == null) parameters["Friction"] = 0.1f;
                    applied.Add(EnsureRigidbody(target, parameters, RigidbodyType2D.Dynamic));
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Circle", parameters, isTrigger: false));
                    break;
                case "slipperysurface":
                    if (parameters["Friction"] == null) parameters["Friction"] = 0f;
                    if (parameters["Bounciness"] == null) parameters["Bounciness"] = 0f;
                    applied.Add(EnsureCollider(target, GetString(parameters, "ColliderKind", "colliderKind", "collider_kind") ?? "Box", parameters, isTrigger: false));
                    break;
                default:
                    return Response.Error($"Unknown Physics2D preset '{GetString(parameters, "Preset", "preset")}'.");
            }

            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Physics2D preset applied.", new { target = BuildPhysicsSummary(target), applied });
        }

        static object AddRigidbody(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var bodyType = ParseEnum(GetString(parameters, "BodyType", "bodyType", "body_type"), RigidbodyType2D.Dynamic);
            var result = EnsureRigidbody(target, parameters, bodyType);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Rigidbody2D added or updated.", new { target = BuildPhysicsSummary(target), component = result });
        }

        static object AddCollider(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var kind = GetString(parameters, "ColliderKind", "colliderKind", "collider_kind", "Kind", "kind") ?? "Box";
            var result = EnsureCollider(target, kind, parameters, GetBool(parameters, false, "IsTrigger", "isTrigger", "is_trigger"));
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Collider2D added or updated.", new { target = BuildPhysicsSummary(target), component = result });
        }

        static object AddJoint(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var jointType = ResolveJointType(GetString(parameters, "JointType", "jointType", "joint_type") ?? "Distance");
            var joint = target.GetComponent(jointType) as Joint2D;
            if (joint == null)
                joint = Undo.AddComponent(target, jointType) as Joint2D;
            if (joint == null)
                return Response.Error($"{jointType.Name} component could not be added to '{target.name}'.");
            Undo.RecordObject(joint, "Configure Joint2D");

            var connectedTarget = GetToken(parameters, "ConnectedBodyTarget", "connectedBodyTarget", "connected_body_target");
            if (connectedTarget != null)
            {
                var connectedGo = SceneObjectLocator.FindObject(connectedTarget.ToString(), "by_id_or_name_or_path", new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
                var rb = connectedGo != null ? connectedGo.GetComponent<Rigidbody2D>() : null;
                if (rb != null)
                    joint.connectedBody = rb;
            }

            ApplyExtraProperties(joint, parameters);
            EditorUtility.SetDirty(joint);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Joint2D added or updated.", new { target = BuildPhysicsSummary(target), component = BuildComponentSummary(joint) });
        }

        static object AddEffector(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var result = EnsureEffector(target, GetString(parameters, "EffectorType", "effectorType", "effector_type") ?? "Platform", parameters);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Effector2D added or updated.", new { target = BuildPhysicsSummary(target), component = result });
        }

        static object CreateMaterial(JObject parameters)
        {
            var path = NormalizeAssetPath(GetString(parameters, "Path", "path", "MaterialPath", "materialPath", "material_path") ?? "Assets/Physics2D/NewPhysicsMaterial2D.physicsMaterial2D");
            EnsureParentDirectory(path);
            var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(path);
            var created = false;
            if (material == null)
            {
                material = new PhysicsMaterial2D(Path.GetFileNameWithoutExtension(path));
                AssetDatabase.CreateAsset(material, path);
                created = true;
            }
            else
            {
                VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true);
            }

            Undo.RecordObject(material, "Configure PhysicsMaterial2D");
            material.friction = GetFloat(parameters, material.friction, "Friction", "friction");
            material.bounciness = GetFloat(parameters, material.bounciness, "Bounciness", "bounciness", "Bounce", "bounce");
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return Response.Success(created ? "PhysicsMaterial2D created." : "PhysicsMaterial2D updated.", BuildMaterialSummary(path, material));
        }

        static object EnsureRigidbody(GameObject target, JObject parameters, RigidbodyType2D defaultBodyType)
        {
            var rb = target.GetComponent<Rigidbody2D>();
            if (rb == null)
                rb = Undo.AddComponent<Rigidbody2D>(target);
            if (rb == null)
                throw new InvalidOperationException($"Rigidbody2D component could not be added to '{target.name}'.");
            Undo.RecordObject(rb, "Configure Rigidbody2D");
            rb.bodyType = ParseEnum(GetString(parameters, "BodyType", "bodyType", "body_type"), defaultBodyType);
            rb.gravityScale = GetFloat(parameters, rb.gravityScale, "GravityScale", "gravityScale", "gravity_scale");
            rb.mass = GetFloat(parameters, rb.mass, "Mass", "mass");
            rb.linearDamping = GetFloat(parameters, rb.linearDamping, "LinearDrag", "linearDrag", "linear_drag", "LinearDamping", "linearDamping", "linear_damping");
            rb.angularDamping = GetFloat(parameters, rb.angularDamping, "AngularDrag", "angularDrag", "angular_drag", "AngularDamping", "angularDamping", "angular_damping");
            if (GetBool(parameters, false, "FreezeRotation", "freezeRotation", "freeze_rotation"))
                rb.constraints |= RigidbodyConstraints2D.FreezeRotation;
            ApplyExtraProperties(rb, parameters);
            EditorUtility.SetDirty(rb);
            return BuildComponentSummary(rb);
        }

        static object EnsureCollider(GameObject target, string kind, JObject parameters, bool isTrigger, bool usedByEffector = false)
        {
            var type = ResolveColliderType(kind);
            var collider = target.GetComponent(type) as Collider2D;
            if (collider == null)
                collider = Undo.AddComponent(target, type) as Collider2D;
            if (collider == null)
                throw new InvalidOperationException($"{type.Name} component could not be added to '{target.name}'.");
            Undo.RecordObject(collider, "Configure Collider2D");
            collider.isTrigger = GetBool(parameters, isTrigger, "IsTrigger", "isTrigger", "is_trigger");
            collider.offset = ParseVector2(GetToken(parameters, "Offset", "offset")) ?? collider.offset;

            if (collider is BoxCollider2D box)
                box.size = ParseVector2(GetToken(parameters, "Size", "size")) ?? box.size;
            else if (collider is CircleCollider2D circle)
                circle.radius = GetFloat(parameters, circle.radius, "Radius", "radius");
            else if (collider is CapsuleCollider2D capsule)
                capsule.size = ParseVector2(GetToken(parameters, "Size", "size")) ?? capsule.size;
            else if (collider is EdgeCollider2D edge && GetArray(parameters, "Points", "points") is JArray points)
                edge.points = points.Select(ParseVector2Required).ToArray();
            else if (collider is PolygonCollider2D polygon && GetArray(parameters, "Points", "points") is JArray polygonPoints)
                polygon.SetPath(0, polygonPoints.Select(ParseVector2Required).ToArray());

            var materialPath = NormalizeAssetPath(GetString(parameters, "MaterialPath", "materialPath", "material_path"));
            if (!string.IsNullOrWhiteSpace(materialPath))
                collider.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(materialPath);
            else if (parameters["Friction"] != null || parameters["Bounciness"] != null)
                collider.sharedMaterial = EnsureInlineMaterial(parameters);

            if (usedByEffector)
                collider.usedByEffector = true;

            ApplyExtraProperties(collider, parameters);
            EditorUtility.SetDirty(collider);
            return BuildComponentSummary(collider);
        }

        static object EnsureEffector(GameObject target, string kind, JObject parameters)
        {
            var type = ResolveEffectorType(kind);
            var effector = target.GetComponent(type) as Effector2D;
            if (effector == null)
                effector = Undo.AddComponent(target, type) as Effector2D;
            if (effector == null)
                throw new InvalidOperationException($"{type.Name} component could not be added to '{target.name}'.");
            Undo.RecordObject(effector, "Configure Effector2D");
            ApplyExtraProperties(effector, parameters);
            EditorUtility.SetDirty(effector);
            return BuildComponentSummary(effector);
        }

        static PhysicsMaterial2D EnsureInlineMaterial(JObject parameters)
        {
            var path = NormalizeAssetPath(GetString(parameters, "MaterialPath", "materialPath", "material_path") ?? "Assets/Physics2D/GeneratedPhysicsMaterial2D.physicsMaterial2D");
            EnsureParentDirectory(path);
            var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(path);
            if (material == null)
            {
                material = new PhysicsMaterial2D(Path.GetFileNameWithoutExtension(path));
                AssetDatabase.CreateAsset(material, path);
            }

            material.friction = GetFloat(parameters, material.friction, "Friction", "friction");
            material.bounciness = GetFloat(parameters, material.bounciness, "Bounciness", "bounciness");
            EditorUtility.SetDirty(material);
            return material;
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
                rigidbody = target.GetComponent<Rigidbody2D>() != null ? BuildComponentSummary(target.GetComponent<Rigidbody2D>()) : null,
                colliders = target.GetComponents<Collider2D>().Select(BuildComponentSummary).ToArray(),
                joints = target.GetComponents<Joint2D>().Select(BuildComponentSummary).ToArray(),
                effectors = target.GetComponents<Effector2D>().Select(BuildComponentSummary).ToArray()
            };
        }

        static object BuildComponentSummary(Component component)
        {
            if (component == null)
                return null;

            return component switch
            {
                Rigidbody2D rb => new { type = rb.GetType().FullName, bodyType = rb.bodyType.ToString(), mass = rb.mass, gravityScale = rb.gravityScale, linearDamping = rb.linearDamping, angularDamping = rb.angularDamping, constraints = rb.constraints.ToString() },
                Collider2D c => new { type = c.GetType().FullName, isTrigger = c.isTrigger, offset = SerializeVector2(c.offset), material = c.sharedMaterial != null ? AssetDatabase.GetAssetPath(c.sharedMaterial) : null, bounds = SerializeBounds(c.bounds) },
                Joint2D j => new { type = j.GetType().FullName, connectedBody = j.connectedBody != null ? j.connectedBody.name : null, enableCollision = j.enableCollision, breakForce = j.breakForce, breakTorque = j.breakTorque },
                Effector2D e => new { type = e.GetType().FullName, useColliderMask = e.useColliderMask, colliderMask = e.colliderMask },
                _ => new { type = component.GetType().FullName }
            };
        }

        static object SerializeVector2(Vector2 value) => new { x = value.x, y = value.y };

        static object SerializeVector3(Vector3 value) => new { x = value.x, y = value.y, z = value.z };

        static object SerializeBounds(Bounds value) => new { center = SerializeVector3(value.center), size = SerializeVector3(value.size), min = SerializeVector3(value.min), max = SerializeVector3(value.max) };

        static object BuildMaterialSummary(string path, PhysicsMaterial2D material)
        {
            return new { path, guid = AssetDatabase.AssetPathToGUID(path), friction = material.friction, bounciness = material.bounciness };
        }

        static Type ResolveColliderType(string kind)
        {
            return Normalize(kind) switch
            {
                "box" or "boxcollider2d" => typeof(BoxCollider2D),
                "circle" or "circlecollider2d" => typeof(CircleCollider2D),
                "capsule" or "capsulecollider2d" => typeof(CapsuleCollider2D),
                "polygon" or "polygoncollider2d" => typeof(PolygonCollider2D),
                "edge" or "edgecollider2d" => typeof(EdgeCollider2D),
                _ => typeof(BoxCollider2D)
            };
        }

        static Type ResolveJointType(string kind)
        {
            return Normalize(kind) switch
            {
                "distance" or "distancejoint2d" => typeof(DistanceJoint2D),
                "fixed" or "fixedjoint2d" => typeof(FixedJoint2D),
                "hinge" or "hingejoint2d" => typeof(HingeJoint2D),
                "spring" or "springjoint2d" => typeof(SpringJoint2D),
                "slider" or "sliderjoint2d" => typeof(SliderJoint2D),
                "target" or "targetjoint2d" => typeof(TargetJoint2D),
                "wheel" or "wheeljoint2d" => typeof(WheelJoint2D),
                "relative" or "relativejoint2d" => typeof(RelativeJoint2D),
                "friction" or "frictionjoint2d" => typeof(FrictionJoint2D),
                _ => typeof(DistanceJoint2D)
            };
        }

        static Type ResolveEffectorType(string kind)
        {
            return Normalize(kind) switch
            {
                "platform" or "platformeffector2d" => typeof(PlatformEffector2D),
                "surface" or "surfaceeffector2d" => typeof(SurfaceEffector2D),
                "point" or "pointeffector2d" => typeof(PointEffector2D),
                "area" or "areaeffector2d" => typeof(AreaEffector2D),
                "buoyancy" or "buoyancyeffector2d" => typeof(BuoyancyEffector2D),
                _ => typeof(PlatformEffector2D)
            };
        }

        static Vector2? ParseVector2(JToken token)
        {
            return token is JArray arr && arr.Count >= 2 ? new Vector2(ReadFloat(arr, 0), ReadFloat(arr, 1)) : null;
        }

        static Vector2 ParseVector2Required(JToken token)
        {
            return ParseVector2(token) ?? Vector2.zero;
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

        static float GetFloat(JObject obj, float defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && float.TryParse(token.ToString(), out var value) ? value : defaultValue;
        }

        static float ReadFloat(JArray arr, int index)
        {
            return arr.Count > index && float.TryParse(arr[index]?.ToString(), out var value) ? value : 0f;
        }
    }
}
