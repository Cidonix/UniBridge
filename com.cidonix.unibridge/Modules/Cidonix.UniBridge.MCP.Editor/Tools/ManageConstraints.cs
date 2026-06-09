#nullable disable
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
using UnityEngine.Animations;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Animation constraint authoring for common transform-follow relationships.
    /// </summary>
    public static class ManageConstraints
    {
        const string ToolName = "UniBridge_ManageConstraints";

        public const string Title = "Manage animation constraints";

        public const string Description = @"Author and inspect Unity animation constraints on scene GameObjects.

Use this when an agent needs ParentConstraint, PositionConstraint, RotationConstraint, ScaleConstraint, AimConstraint, or LookAtConstraint setup with structured source transforms.

Args:
    Action: Inspect, AddConstraint, SetSources, ClearSources, or ApplyPreset.
    Target: GameObject name/path/id that receives the constraint.
    ConstraintType: Parent, Position, Rotation, Scale, Aim, or LookAt.
    Sources: Array of source specs: { Target/Source/Path, Weight }.
    SourceTarget: Single source shortcut.
    Weight, ConstraintActive, Locked: Common constraint controls.
    MaintainOffset: Best-effort initial offset for common constraint types.
    Preset: FollowTransform, FollowPosition, FollowRotation, FollowScale, LookAt, AimAt.
    Properties: Optional extra SerializedProperty/public-property patches.

Returns:
    success, message, and data with target/constraint/source summaries.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new { type = "string", @enum = new[] { "Inspect", "AddConstraint", "SetSources", "ClearSources", "ApplyPreset" } },
                    Target = new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    SearchMethod = new { type = "string" },
                    ConstraintType = new { type = "string", @enum = new[] { "Parent", "Position", "Rotation", "Scale", "Aim", "LookAt" } },
                    Preset = new { type = "string" },
                    Sources = new { type = "array", items = new { type = "object", additionalProperties = true } },
                    SourceTarget = new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    Weight = new { type = "number" },
                    ConstraintActive = new { type = "boolean" },
                    Locked = new { type = "boolean" },
                    MaintainOffset = new { type = "boolean" },
                    Properties = new { type = "object", additionalProperties = true }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "scene", "animation", "constraints" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = Normalize(GetString(parameters, "Action", "action") ?? "Inspect");
            try
            {
                return action switch
                {
                    "inspect" => Inspect(parameters),
                    "addconstraint" or "add" or "create" => AddConstraint(parameters),
                    "setsources" or "sources" => SetSources(parameters),
                    "clearsources" or "clear" => ClearSources(parameters),
                    "applypreset" or "preset" => ApplyPreset(parameters),
                    _ => Response.Error($"Unknown Constraints action '{GetString(parameters, "Action", "action")}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageConstraints] Action '{action}' failed: {ex}");
                return Response.Error($"Constraints action '{action}' failed: {ex.Message}");
            }
        }

        static object Inspect(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: false);
            if (target == null)
            {
                var objects = SceneObjectLocator.GetAllSceneObjects(new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true, IncludeDontDestroyOnLoad = true })
                    .Where(HasConstraint)
                    .Select(BuildObjectSummary)
                    .ToArray();
                return Response.Success("Listed constrained objects.", new { count = objects.Length, objects });
            }

            return Response.Success("Inspected constraints.", BuildObjectSummary(target));
        }

        static object ApplyPreset(JObject parameters)
        {
            var preset = Normalize(GetString(parameters, "Preset", "preset") ?? "FollowTransform");
            parameters["ConstraintType"] ??= preset switch
            {
                "followposition" or "position" => "Position",
                "followrotation" or "rotation" => "Rotation",
                "followscale" or "scale" => "Scale",
                "lookat" => "LookAt",
                "aimat" or "aim" => "Aim",
                _ => "Parent"
            };
            parameters["ConstraintActive"] ??= true;
            parameters["Locked"] ??= true;
            return AddConstraint(parameters);
        }

        static object AddConstraint(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var constraintType = ResolveConstraintType(GetString(parameters, "ConstraintType", "constraintType", "constraint_type", "Type", "type") ?? "Parent");
            var component = target.GetComponent(constraintType);
            if (component == null)
            {
                component = Undo.AddComponent(target, constraintType);
                if (component == null)
                {
                    component = target.AddComponent(constraintType);
                    if (component != null)
                        Undo.RegisterCreatedObjectUndo(component, $"Add {constraintType.Name}");
                }
            }
            if (component == null)
                return Response.Error($"{constraintType.Name} could not be added to '{target.name}'.");

            ConfigureConstraint(component, parameters, replaceSources: GetArray(parameters, "Sources", "sources") != null || GetToken(parameters, "SourceTarget", "sourceTarget", "source_target", "Source", "source") != null);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Constraint added or updated.", new { target = BuildObjectSummary(target), constraint = BuildConstraintSummary(component) });
        }

        static object SetSources(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var component = ResolveConstraintComponent(target, parameters, required: true);
            ConfigureConstraint(component, parameters, replaceSources: true);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Constraint sources updated.", new { target = BuildObjectSummary(target), constraint = BuildConstraintSummary(component) });
        }

        static object ClearSources(JObject parameters)
        {
            var target = ResolveTarget(parameters, required: true);
            var component = ResolveConstraintComponent(target, parameters, required: true);
            if (component is not IConstraint constraint)
                return Response.Error($"{component.GetType().Name} does not implement IConstraint.");

            Undo.RecordObject(component, "Clear Constraint Sources");
            for (var i = constraint.sourceCount - 1; i >= 0; i--)
                constraint.RemoveSource(i);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("Constraint sources cleared.", new { target = BuildObjectSummary(target), constraint = BuildConstraintSummary(component) });
        }

        static void ConfigureConstraint(Component component, JObject parameters, bool replaceSources)
        {
            Undo.RecordObject(component, "Configure Constraint");
            if (component is IConstraint constraint)
            {
                constraint.weight = GetFloat(parameters, constraint.weight, "Weight", "weight");
                constraint.constraintActive = GetBool(parameters, constraint.constraintActive, "ConstraintActive", "constraintActive", "constraint_active", "Active", "active");
                constraint.locked = GetBool(parameters, constraint.locked, "Locked", "locked");

                var sources = BuildSources(parameters).ToList();
                if (replaceSources)
                {
                    while (constraint.sourceCount > 0)
                        constraint.RemoveSource(constraint.sourceCount - 1);
                    foreach (var source in sources)
                        constraint.AddSource(source);
                }

                if (GetBool(parameters, false, "MaintainOffset", "maintainOffset", "maintain_offset") && sources.Count > 0)
                    ApplyMaintainOffset(component, sources[0].sourceTransform);
            }

            ApplyExtraProperties(component, parameters);
        }

        static IEnumerable<ConstraintSource> BuildSources(JObject parameters)
        {
            var array = GetArray(parameters, "Sources", "sources");
            if (array != null)
            {
                foreach (var item in array.OfType<JObject>())
                {
                    var source = BuildSource(item);
                    if (source.sourceTransform != null)
                        yield return source;
                }
                yield break;
            }

            var sourceToken = GetToken(parameters, "SourceTarget", "sourceTarget", "source_target", "Source", "source", "Path", "path");
            if (sourceToken == null)
                yield break;

            var go = ResolveObject(sourceToken.ToString());
            if (go != null)
                yield return new ConstraintSource { sourceTransform = go.transform, weight = GetFloat(parameters, 1f, "SourceWeight", "sourceWeight", "source_weight", "Weight", "weight") };
        }

        static ConstraintSource BuildSource(JObject spec)
        {
            var target = GetString(spec, "Target", "target", "Source", "source", "SourceTarget", "sourceTarget", "Path", "path");
            var go = ResolveObject(target);
            return new ConstraintSource
            {
                sourceTransform = go != null ? go.transform : null,
                weight = GetFloat(spec, 1f, "Weight", "weight")
            };
        }

        static void ApplyMaintainOffset(Component component, Transform source)
        {
            if (component == null || source == null)
                return;

            var target = component.transform;
            TrySetProperty(component, "translationOffset", target.position - source.position);
            TrySetProperty(component, "scaleOffset", new Vector3(
                SafeScaleOffset(target.lossyScale.x, source.lossyScale.x),
                SafeScaleOffset(target.lossyScale.y, source.lossyScale.y),
                SafeScaleOffset(target.lossyScale.z, source.lossyScale.z)));
            TrySetProperty(component, "rotationOffset", (Quaternion.Inverse(source.rotation) * target.rotation).eulerAngles);
        }

        static float SafeScaleOffset(float target, float source) => Mathf.Abs(source) < 0.0001f ? target : target / source;

        static bool TrySetProperty(object target, string propertyName, object value)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite)
                return false;
            try
            {
                property.SetValue(target, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static Component ResolveConstraintComponent(GameObject target, JObject parameters, bool required)
        {
            var constraintTypeName = GetString(parameters, "ConstraintType", "constraintType", "constraint_type", "Type", "type");
            if (!string.IsNullOrWhiteSpace(constraintTypeName))
            {
                var type = ResolveConstraintType(constraintTypeName);
                var component = target.GetComponent(type);
                if (component == null && required)
                    throw new InvalidOperationException($"{type.Name} was not found on '{target.name}'.");
                return component;
            }

            var first = target.GetComponents<Component>().FirstOrDefault(component => component is IConstraint);
            if (first == null && required)
                throw new InvalidOperationException($"No animation constraint was found on '{target.name}'.");
            return first;
        }

        static Type ResolveConstraintType(string typeName)
        {
            return Normalize(typeName) switch
            {
                "parent" or "parentconstraint" => typeof(ParentConstraint),
                "position" or "positionconstraint" => typeof(PositionConstraint),
                "rotation" or "rotationconstraint" => typeof(RotationConstraint),
                "scale" or "scaleconstraint" => typeof(ScaleConstraint),
                "aim" or "aimconstraint" => typeof(AimConstraint),
                "lookat" or "lookatconstraint" => typeof(LookAtConstraint),
                _ => throw new InvalidOperationException($"Unknown constraint type '{typeName}'.")
            };
        }

        static bool HasConstraint(GameObject go)
        {
            return go != null && go.GetComponents<Component>().Any(component => component is IConstraint);
        }

        static object BuildObjectSummary(GameObject target)
        {
            return new
            {
                name = target.name,
                instanceId = UnityApiAdapter.GetObjectId(target),
                path = SceneObjectLocator.GetHierarchyPath(target),
                scene = target.scene.IsValid() ? new { target.scene.name, target.scene.path } : null,
                constraints = target.GetComponents<Component>().Where(component => component is IConstraint).Select(BuildConstraintSummary).ToArray()
            };
        }

        static object BuildConstraintSummary(Component component)
        {
            if (component is not IConstraint constraint)
                return new { type = component.GetType().FullName };

            var sources = new List<object>();
            for (var i = 0; i < constraint.sourceCount; i++)
            {
                var source = constraint.GetSource(i);
                sources.Add(new
                {
                    index = i,
                    weight = source.weight,
                    target = source.sourceTransform != null ? new
                    {
                        name = source.sourceTransform.name,
                        instanceId = UnityApiAdapter.GetObjectId(source.sourceTransform.gameObject),
                        path = SceneObjectLocator.GetHierarchyPath(source.sourceTransform.gameObject)
                    } : null
                });
            }

            return new
            {
                type = component.GetType().FullName,
                constraint.weight,
                constraint.constraintActive,
                constraint.locked,
                sourceCount = constraint.sourceCount,
                sources
            };
        }

        static GameObject ResolveTarget(JObject parameters, bool required)
        {
            var token = GetToken(parameters, "Target", "target", "GameObject", "gameObject", "game_object");
            if (token == null || token.Type == JTokenType.Null || string.IsNullOrWhiteSpace(token.ToString()))
            {
                if (required)
                    throw new InvalidOperationException("Target GameObject is required.");
                return null;
            }

            var go = ResolveObject(token.ToString(), GetString(parameters, "SearchMethod", "searchMethod", "search_method"));
            if (go == null && required)
                throw new InvalidOperationException($"Target GameObject '{token}' was not found.");
            return go;
        }

        static GameObject ResolveObject(string target, string searchMethod = null)
        {
            if (string.IsNullOrWhiteSpace(target))
                return null;

            return SceneObjectLocator.FindObject(target, searchMethod, new SceneObjectLocator.Options
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

            var so = new SerializedObject(component);
            foreach (var property in props.Properties())
            {
                var result = SerializedPropertyPatcher.TryApplyProperty(component, so, property.Name, property.Value, dryRun: false);
                if (!result.Success)
                    TrySetPublicMember(component, property.Name, property.Value);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static bool TrySetPublicMember(UnityEngine.Object target, string name, JToken value)
        {
            var normalized = Normalize(name);
            var property = target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(prop => prop.CanWrite && Normalize(prop.Name) == normalized);
            if (property == null)
                return false;

            try
            {
                object converted;
                if (property.PropertyType == typeof(Vector3))
                    converted = ParseVector3(value) ?? Vector3.zero;
                else if (property.PropertyType.IsEnum)
                    converted = value.Type == JTokenType.Integer ? Enum.ToObject(property.PropertyType, value.ToObject<int>()) : Enum.Parse(property.PropertyType, value.ToString(), true);
                else
                    converted = value.ToObject(property.PropertyType);
                property.SetValue(target, converted);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static Vector3? ParseVector3(JToken token)
        {
            if (token is JArray arr && arr.Count >= 3)
                return new Vector3(ReadFloat(arr, 0), ReadFloat(arr, 1), ReadFloat(arr, 2));
            if (token is JObject obj)
                return new Vector3(ReadFloatMember(obj, "x", 0), ReadFloatMember(obj, "y", 0), ReadFloatMember(obj, "z", 0));
            return null;
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

            return float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        static string Normalize(string value) => (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
    }
}
