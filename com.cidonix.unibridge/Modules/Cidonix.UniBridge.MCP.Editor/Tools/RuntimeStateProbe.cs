#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Cidonix.UniBridge.Toolkit;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read-only runtime state sampling for scene GameObjects and MonoBehaviours.
    /// </summary>
    public static class RuntimeStateProbe
    {
        const string ToolName = "UniBridge_RuntimeStateProbe";
        const int DefaultMaxTargets = 5;
        const int DefaultMaxComponents = 8;
        const int DefaultMaxMembers = 80;
        const int DefaultSampleFrames = 30;
        const int DefaultTimeoutMs = 30000;
        const int DefaultMaxStringLength = 400;
        const int DefaultMaxCollectionItems = 16;
        const int MaxChangedMembers = 100;

        public const string Title = "Sample runtime component state";

        public const string Description = @"Read and sample live scene GameObject/component state for AI debugging without executing arbitrary C#.

Use this after entering Play Mode when a gameplay issue depends on MonoBehaviour flags, counters, references, positions, trigger state, animation state fields, or other component values over several frames.

Actions:
    Snapshot: Resolve target GameObjects and read current component/member values once.
    Sample: In Play Mode by default, read the same values over a bounded number of editor update ticks, save raw samples, and return a compact changed-member summary.
    ListMembers: Show readable SerializedProperty paths and reflected fields/properties for matching components.

The probe is read-only. It uses the shared UniBridge scene resolver, so Target/Component lookup supports inactive objects, Prefab Stage objects, instance IDs, hierarchy paths, short/full type names, MonoScript GUIDs, and serialized editor class identifiers.

Search aliases: UniBridge Unity runtime state probe watch variables component fields MonoBehaviour state frame sampling play mode debugger.";

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "runtime", "debugging", "scene" }, EnabledByDefault = true, ExecutionPolicy = ToolExecutionPolicy.ReadOnly)]
        public static async Task<object> HandleCommand(RuntimeStateProbeParams parameters)
        {
            parameters ??= new RuntimeStateProbeParams();
            var options = ProbeOptions.From(parameters);

            try
            {
                var targets = ResolveTargets(parameters, options);
                if (targets.Count == 0)
                {
                    return Response.Error("No probe targets were found.", new
                    {
                        target = parameters.Target,
                        component = parameters.Component,
                        searchMethod = parameters.SearchMethod,
                        includeInactive = options.IncludeInactive,
                        includePrefabStage = options.IncludePrefabStage,
                        scenePath = parameters.ScenePath,
                        hint = "Provide Target, select a GameObject, or provide Component to find matching scene objects."
                    });
                }

                switch (parameters.Action)
                {
                    case RuntimeStateProbeAction.ListMembers:
                        return Response.Success("Listed runtime probe members.", ListMembers(targets, parameters, options));
                    case RuntimeStateProbeAction.Sample:
                        return await Sample(targets, parameters, options);
                    case RuntimeStateProbeAction.Snapshot:
                    default:
                        return Response.Success("Captured runtime state snapshot.", Snapshot(targets, parameters, options));
                }
            }
            catch (Exception ex)
            {
                return Response.Error($"Runtime state probe action '{parameters.Action}' failed: {ex.Message}");
            }
        }

        static object Snapshot(List<GameObject> targets, RuntimeStateProbeParams parameters, ProbeOptions options)
        {
            var sample = CaptureSample(0, targets, parameters, options);
            return new
            {
                schema = "unibridge.runtimeStateProbe.snapshot.v1",
                capturedUtc = DateTime.UtcNow.ToString("o"),
                name = NormalizeName(parameters.Name, "runtime_state_snapshot"),
                editor = BuildEditorState(),
                targetCount = targets.Count,
                componentFilter = parameters.Component,
                requestedMembers = options.RequestedMembers,
                sample = sample.ToDto()
            };
        }

        static async Task<object> Sample(List<GameObject> targets, RuntimeStateProbeParams parameters, ProbeOptions options)
        {
            var requirePlayMode = parameters.RequirePlayMode ?? true;
            if (requirePlayMode && !Application.isPlaying)
            {
                return Response.Error("PLAY_MODE_REQUIRED", new
                {
                    isPlaying = Application.isPlaying,
                    isPaused = EditorApplication.isPaused,
                    hint = "Enter Play Mode first, or pass RequirePlayMode=false for editor-time sampling."
                });
            }

            var frames = Clamp(parameters.SampleFrames ?? DefaultSampleFrames, 1, 600);
            var timeoutMs = Clamp(parameters.TimeoutMs ?? DefaultTimeoutMs, 1000, 300000);
            var startUtc = DateTime.UtcNow;
            var startEditorTime = EditorApplication.timeSinceStartup;
            var deadline = startEditorTime + timeoutMs / 1000.0;
            var samples = new List<ProbeSample>(frames);

            await EditorTask.Yield();
            while (samples.Count < frames && EditorApplication.timeSinceStartup <= deadline)
            {
                samples.Add(CaptureSample(samples.Count, targets, parameters, options));
                await EditorTask.Yield();
            }

            var elapsedMs = Math.Max(0, (EditorApplication.timeSinceStartup - startEditorTime) * 1000.0);
            var changeSummary = BuildChangeSummary(samples);
            var payload = new
            {
                schema = "unibridge.runtimeStateProbe.sample.v1",
                name = NormalizeName(parameters.Name, "runtime_state_sample"),
                startedUtc = startUtc.ToString("o"),
                endedUtc = DateTime.UtcNow.ToString("o"),
                requestedFrames = frames,
                sampleRows = samples.Count,
                timedOut = samples.Count < frames,
                elapsedMs,
                editor = BuildEditorState(),
                targetCount = targets.Count,
                componentFilter = parameters.Component,
                requestedMembers = options.RequestedMembers,
                changeSummary,
                samples = samples.Select(sample => sample.ToDto()).ToArray()
            };

            string savedPath = null;
            if (parameters.SaveToFile ?? true)
            {
                savedPath = SavePayload(payload, parameters.Name);
            }

            return Response.Success($"Captured runtime state sample with {samples.Count} row(s).", new
            {
                schema = "unibridge.runtimeStateProbe.sample.summary.v1",
                name = payload.name,
                requestedFrames = frames,
                sampleRows = samples.Count,
                timedOut = samples.Count < frames,
                elapsedMs,
                savedPath,
                editor = payload.editor,
                targetCount = targets.Count,
                componentFilter = parameters.Component,
                requestedMembers = options.RequestedMembers,
                changeSummary,
                samples = parameters.ReturnSamples == true ? samples.Select(sample => sample.ToDto()).ToArray() : null
            });
        }

        static object ListMembers(List<GameObject> targets, RuntimeStateProbeParams parameters, ProbeOptions options)
        {
            return new
            {
                schema = "unibridge.runtimeStateProbe.members.v1",
                capturedUtc = DateTime.UtcNow.ToString("o"),
                targetCount = targets.Count,
                componentFilter = parameters.Component,
                targets = targets.Select(target => new
                {
                    gameObject = BuildGameObjectSummary(target),
                    components = ResolveComponents(target, parameters.Component, options)
                        .Select(component => BuildComponentMembers(component, options))
                        .ToArray()
                }).ToArray()
            };
        }

        static ProbeSample CaptureSample(int sampleIndex, List<GameObject> targets, RuntimeStateProbeParams parameters, ProbeOptions options)
        {
            return new ProbeSample
            {
                SampleIndex = sampleIndex,
                CapturedUtc = DateTime.UtcNow.ToString("o"),
                EditorTime = EditorApplication.timeSinceStartup,
                FrameCount = SafeInt(() => Time.frameCount),
                RenderedFrameCount = SafeInt(() => Time.renderedFrameCount),
                Time = SafeFloat(() => Time.time),
                DeltaTime = SafeFloat(() => Time.deltaTime),
                Targets = targets
                    .Select(target => CaptureTarget(target, parameters.Component, options))
                    .ToList()
            };
        }

        static TargetProbe CaptureTarget(GameObject target, string componentFilter, ProbeOptions options)
        {
            var components = ResolveComponents(target, componentFilter, options)
                .Select((component, index) => CaptureComponent(component, index, options))
                .ToList();

            return new TargetProbe
            {
                GameObject = BuildGameObjectSummary(target),
                Components = components
            };
        }

        static ComponentProbe CaptureComponent(Component component, int componentIndex, ProbeOptions options)
        {
            if (component == null)
            {
                return new ComponentProbe
                {
                    Index = componentIndex,
                    MissingScript = true,
                    Type = "<missing component>"
                };
            }

            return new ComponentProbe
            {
                Index = componentIndex,
                MissingScript = false,
                Type = component.GetType().FullName,
                ShortType = component.GetType().Name,
                ScriptIdentity = ComponentIdentity.BuildScriptIdentity(component),
                Enabled = component is Behaviour behaviour ? (bool?)behaviour.enabled : null,
                Values = ReadValues(component, componentIndex, options)
            };
        }

        static List<ValueProbe> ReadValues(Component component, int componentIndex, ProbeOptions options)
        {
            var values = new List<ValueProbe>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (options.RequestedMembers.Length > 0)
            {
                foreach (var member in options.RequestedMembers)
                {
                    if (string.IsNullOrWhiteSpace(member))
                    {
                        continue;
                    }

                    values.Add(ReadRequestedValue(component, componentIndex, member.Trim(), options));
                }

                return values;
            }

            if (options.IncludeSerializedFields)
            {
                foreach (var value in ReadSerializedValues(component, componentIndex, options, null))
                {
                    if (seen.Add(NormalizeMemberKey(value.MemberPath)))
                    {
                        values.Add(value);
                    }

                    if (values.Count >= options.MaxMembers)
                    {
                        return values;
                    }
                }
            }

            if (options.IncludeReadableMembers)
            {
                foreach (var value in ReadReflectionValues(component, componentIndex, options))
                {
                    if (seen.Add(NormalizeMemberKey(value.MemberPath)))
                    {
                        values.Add(value);
                    }

                    if (values.Count >= options.MaxMembers)
                    {
                        return values;
                    }
                }
            }

            return values;
        }

        static ValueProbe ReadRequestedValue(Component component, int componentIndex, string member, ProbeOptions options)
        {
            if (options.IncludeSerializedFields)
            {
                var serialized = ReadSerializedValues(component, componentIndex, options, member).FirstOrDefault();
                if (serialized != null)
                {
                    return serialized;
                }
            }

            var reflected = ReadReflectionPath(component, componentIndex, member, options);
            if (reflected != null)
            {
                return reflected;
            }

            return new ValueProbe
            {
                ComponentIndex = componentIndex,
                Source = "notFound",
                MemberPath = member,
                MemberName = member,
                ValueType = null,
                Error = $"Member or SerializedProperty '{member}' was not found on component '{component.GetType().FullName}'."
            };
        }

        static IEnumerable<ValueProbe> ReadSerializedValues(Component component, int componentIndex, ProbeOptions options, string requested)
        {
            SerializedObject serializedObject;
            string serializedObjectError = null;
            try
            {
                serializedObject = new SerializedObject(component);
                serializedObject.Update();
            }
            catch (Exception ex)
            {
                serializedObject = null;
                serializedObjectError = "SerializedObject could not be created: " + ex.Message;
            }

            if (serializedObject == null)
            {
                yield return new ValueProbe
                {
                    ComponentIndex = componentIndex,
                    Source = "serializedProperty",
                    MemberPath = requested,
                    MemberName = requested,
                    Error = serializedObjectError
                };
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(requested))
            {
                var property = SerializedPropertyPatcher.FindProperty(serializedObject, requested);
                if (property == null)
                {
                    yield break;
                }

                yield return SerializedPropertyToValue(componentIndex, property, options);
                yield break;
            }

            var iterator = serializedObject.GetIterator();
            if (!iterator.NextVisible(true))
            {
                yield break;
            }

            var count = 0;
            do
            {
                if (iterator.propertyPath == "m_Script")
                {
                    continue;
                }

                yield return SerializedPropertyToValue(componentIndex, iterator.Copy(), options);
                count++;
            }
            while (count < options.MaxMembers && iterator.NextVisible(false));
        }

        static ValueProbe SerializedPropertyToValue(int componentIndex, SerializedProperty property, ProbeOptions options)
        {
            try
            {
                return new ValueProbe
                {
                    ComponentIndex = componentIndex,
                    Source = "serializedProperty",
                    MemberPath = property.propertyPath,
                    MemberName = property.name,
                    DisplayName = property.displayName,
                    ValueType = property.propertyType.ToString(),
                    Value = TruncateStrings(SerializedPropertyPatcher.SerializePropertyValue(property), options)
                };
            }
            catch (Exception ex)
            {
                return new ValueProbe
                {
                    ComponentIndex = componentIndex,
                    Source = "serializedProperty",
                    MemberPath = property.propertyPath,
                    MemberName = property.name,
                    DisplayName = property.displayName,
                    ValueType = property.propertyType.ToString(),
                    Error = "SerializedProperty value read failed: " + ex.Message
                };
            }
        }

        static IEnumerable<ValueProbe> ReadReflectionValues(Component component, int componentIndex, ProbeOptions options)
        {
            var type = component.GetType();
            foreach (var field in EnumerateFields(type, options.IncludeNonPublicFields))
            {
                if (field.IsStatic || field.IsDefined(typeof(NonSerializedAttribute), true))
                {
                    continue;
                }

                yield return ReadField(component, componentIndex, field, options);
            }

            foreach (var property in EnumerateProperties(type))
            {
                if (!CanReadProperty(property))
                {
                    continue;
                }

                yield return ReadProperty(component, componentIndex, property, options);
            }
        }

        static ValueProbe ReadReflectionPath(Component component, int componentIndex, string path, ProbeOptions options)
        {
            object current = component;
            var currentType = current.GetType();
            var parts = path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (string.IsNullOrWhiteSpace(part))
                {
                    return null;
                }

                var field = FindField(currentType, part);
                if (field != null && !field.IsStatic)
                {
                    try
                    {
                        current = field.GetValue(current);
                    }
                    catch (Exception ex)
                    {
                        return ReflectionError(componentIndex, path, field.FieldType, "Field read failed: " + ex.Message);
                    }

                    currentType = current?.GetType() ?? field.FieldType;
                    continue;
                }

                var property = FindProperty(currentType, part);
                if (property != null && CanReadProperty(property))
                {
                    try
                    {
                        current = property.GetValue(current, null);
                    }
                    catch (Exception ex)
                    {
                        return ReflectionError(componentIndex, path, property.PropertyType, "Property read failed: " + ex.Message);
                    }

                    currentType = current?.GetType() ?? property.PropertyType;
                    continue;
                }

                return null;
            }

            return new ValueProbe
            {
                ComponentIndex = componentIndex,
                Source = "reflection",
                MemberPath = path,
                MemberName = parts[parts.Length - 1],
                ValueType = currentType?.FullName,
                Value = SerializeRuntimeValue(current, options)
            };
        }

        static ValueProbe ReadField(Component component, int componentIndex, FieldInfo field, ProbeOptions options)
        {
            try
            {
                return new ValueProbe
                {
                    ComponentIndex = componentIndex,
                    Source = "reflectionField",
                    MemberPath = field.Name,
                    MemberName = field.Name,
                    ValueType = field.FieldType.FullName,
                    DeclaringType = field.DeclaringType?.FullName,
                    Value = SerializeRuntimeValue(field.GetValue(component), options)
                };
            }
            catch (Exception ex)
            {
                return ReflectionError(componentIndex, field.Name, field.FieldType, "Field read failed: " + ex.Message);
            }
        }

        static ValueProbe ReadProperty(Component component, int componentIndex, PropertyInfo property, ProbeOptions options)
        {
            try
            {
                return new ValueProbe
                {
                    ComponentIndex = componentIndex,
                    Source = "reflectionProperty",
                    MemberPath = property.Name,
                    MemberName = property.Name,
                    ValueType = property.PropertyType.FullName,
                    DeclaringType = property.DeclaringType?.FullName,
                    Value = SerializeRuntimeValue(property.GetValue(component, null), options)
                };
            }
            catch (Exception ex)
            {
                return ReflectionError(componentIndex, property.Name, property.PropertyType, "Property read failed: " + ex.Message);
            }
        }

        static ValueProbe ReflectionError(int componentIndex, string memberPath, Type valueType, string error)
        {
            return new ValueProbe
            {
                ComponentIndex = componentIndex,
                Source = "reflection",
                MemberPath = memberPath,
                MemberName = memberPath,
                ValueType = valueType?.FullName,
                Error = error
            };
        }

        static object BuildComponentMembers(Component component, ProbeOptions options)
        {
            if (component == null)
            {
                return new
                {
                    missingScript = true,
                    type = "<missing component>"
                };
            }

            var serialized = new List<object>();
            if (options.IncludeSerializedFields)
            {
                foreach (var item in ReadSerializedMemberHints(component, options))
                {
                    serialized.Add(item);
                    if (serialized.Count >= options.MaxMembers)
                    {
                        break;
                    }
                }
            }

            var reflected = new List<object>();
            if (options.IncludeReadableMembers)
            {
                var type = component.GetType();
                foreach (var field in EnumerateFields(type, options.IncludeNonPublicFields))
                {
                    if (field.IsStatic || field.IsDefined(typeof(NonSerializedAttribute), true))
                    {
                        continue;
                    }

                    reflected.Add(new
                    {
                        source = "field",
                        name = field.Name,
                        declaringType = field.DeclaringType?.FullName,
                        type = field.FieldType.FullName,
                        isPublic = field.IsPublic,
                        isPrivate = field.IsPrivate,
                        serializedByUnity = field.IsPublic || field.IsDefined(typeof(SerializeField), true)
                    });
                    if (reflected.Count >= options.MaxMembers)
                    {
                        break;
                    }
                }

                if (reflected.Count < options.MaxMembers)
                {
                    foreach (var property in EnumerateProperties(type))
                    {
                        if (!CanReadProperty(property))
                        {
                            continue;
                        }

                        reflected.Add(new
                        {
                            source = "property",
                            name = property.Name,
                            declaringType = property.DeclaringType?.FullName,
                            type = property.PropertyType.FullName
                        });
                        if (reflected.Count >= options.MaxMembers)
                        {
                            break;
                        }
                    }
                }
            }

            return new
            {
                missingScript = false,
                type = component.GetType().FullName,
                shortType = component.GetType().Name,
                scriptIdentity = ComponentIdentity.BuildScriptIdentity(component),
                enabled = component is Behaviour behaviour ? (bool?)behaviour.enabled : null,
                serializedProperties = serialized.ToArray(),
                reflectedMembers = reflected.ToArray()
            };
        }

        static IEnumerable<object> ReadSerializedMemberHints(Component component, ProbeOptions options)
        {
            SerializedObject serializedObject;
            string serializedObjectError = null;
            try
            {
                serializedObject = new SerializedObject(component);
                serializedObject.Update();
            }
            catch (Exception ex)
            {
                serializedObject = null;
                serializedObjectError = "SerializedObject could not be created: " + ex.Message;
            }

            if (serializedObject == null)
            {
                yield return new { error = serializedObjectError };
                yield break;
            }

            var iterator = serializedObject.GetIterator();
            if (!iterator.NextVisible(true))
            {
                yield break;
            }

            do
            {
                if (iterator.propertyPath == "m_Script")
                {
                    continue;
                }

                yield return new
                {
                    source = "serializedProperty",
                    path = iterator.propertyPath,
                    name = iterator.name,
                    displayName = iterator.displayName,
                    type = iterator.propertyType.ToString(),
                    editable = iterator.editable,
                    isArray = iterator.isArray,
                    arraySize = iterator.isArray ? iterator.arraySize : (int?)null
                };
            }
            while (iterator.NextVisible(false));
        }

        static List<GameObject> ResolveTargets(RuntimeStateProbeParams parameters, ProbeOptions options)
        {
            var searchOptions = new SceneObjectLocator.Options
            {
                IncludeInactive = options.IncludeInactive,
                IncludePrefabStage = options.IncludePrefabStage,
                IncludeDontDestroyOnLoad = Application.isPlaying,
                ScenePath = parameters.ScenePath
            };

            List<GameObject> matches;
            if (!string.IsNullOrWhiteSpace(parameters.Target))
            {
                matches = SceneObjectLocator.FindObjects(parameters.Target, parameters.SearchMethod, searchOptions);
            }
            else if (!string.IsNullOrWhiteSpace(parameters.Component))
            {
                matches = SceneObjectLocator.FindObjects(parameters.Component, "ByComponent", searchOptions);
            }
            else
            {
                matches = Selection.activeGameObject != null
                    ? new List<GameObject> { Selection.activeGameObject }
                    : new List<GameObject>();
            }

            return matches
                .Where(go => go != null)
                .Distinct()
                .Take(options.MaxTargets)
                .ToList();
        }

        static List<Component> ResolveComponents(GameObject target, string componentFilter, ProbeOptions options)
        {
            if (target == null)
            {
                return new List<Component>();
            }

            return target.GetComponents<Component>()
                .Where(component => component != null)
                .Where(component => string.IsNullOrWhiteSpace(componentFilter) || ComponentIdentity.Matches(component, componentFilter))
                .Take(options.MaxComponents)
                .ToList();
        }

        static object BuildGameObjectSummary(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            return new
            {
                name = go.name,
                instanceId = UnityApiAdapter.GetObjectId(go),
                path = SceneObjectLocator.GetHierarchyPath(go, leadingSlash: true),
                scene = go.scene.IsValid() ? new { name = go.scene.name, path = go.scene.path } : null,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                tag = SafeTag(go),
                layer = go.layer,
                layerName = LayerMask.LayerToName(go.layer),
                transform = new
                {
                    localPosition = Vector3Dto(go.transform.localPosition),
                    position = Vector3Dto(go.transform.position),
                    localEulerAngles = Vector3Dto(go.transform.localEulerAngles),
                    eulerAngles = Vector3Dto(go.transform.eulerAngles),
                    localScale = Vector3Dto(go.transform.localScale)
                }
            };
        }

        static object BuildEditorState()
        {
            return new
            {
                unityVersion = Application.unityVersion,
                isPlaying = Application.isPlaying,
                isPaused = EditorApplication.isPaused,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                frameCount = SafeInt(() => Time.frameCount),
                renderedFrameCount = SafeInt(() => Time.renderedFrameCount),
                time = SafeFloat(() => Time.time),
                deltaTime = SafeFloat(() => Time.deltaTime),
                realtimeSinceStartup = SafeFloat(() => Time.realtimeSinceStartup)
            };
        }

        static object BuildChangeSummary(List<ProbeSample> samples)
        {
            var map = new Dictionary<string, ChangeAccumulator>(StringComparer.Ordinal);
            var errors = new List<object>();

            foreach (var sample in samples)
            {
                foreach (var target in sample.Targets)
                {
                    var targetObject = target.GameObject;
                    var objectId = ReadObjectId(targetObject);
                    var objectPath = ReadString(targetObject, "path");
                    foreach (var component in target.Components)
                    {
                        foreach (var value in component.Values)
                        {
                            if (!string.IsNullOrWhiteSpace(value.Error))
                            {
                                errors.Add(new
                                {
                                    sampleIndex = sample.SampleIndex,
                                    objectId,
                                    objectPath,
                                    componentIndex = component.Index,
                                    componentType = component.Type,
                                    member = value.MemberPath,
                                    source = value.Source,
                                    error = value.Error
                                });
                                continue;
                            }

                            var key = objectId + "|" + component.Index.ToString(CultureInfo.InvariantCulture) + "|" + component.Type + "|" + value.Source + "|" + value.MemberPath;
                            if (!map.TryGetValue(key, out var accumulator))
                            {
                                accumulator = new ChangeAccumulator
                                {
                                    ObjectId = objectId,
                                    ObjectPath = objectPath,
                                    ComponentIndex = component.Index,
                                    ComponentType = component.Type,
                                    MemberPath = value.MemberPath,
                                    Source = value.Source
                                };
                                map.Add(key, accumulator);
                            }

                            accumulator.Add(sample.SampleIndex, value.Value);
                        }
                    }
                }
            }

            var changed = map.Values
                .Where(item => item.Changed)
                .OrderByDescending(item => item.ChangeCount)
                .ThenBy(item => item.ObjectPath, StringComparer.Ordinal)
                .ThenBy(item => item.MemberPath, StringComparer.Ordinal)
                .Take(MaxChangedMembers)
                .Select(item => item.ToDto())
                .ToArray();

            return new
            {
                sampleRows = samples.Count,
                trackedMembers = map.Count,
                changedMemberCount = map.Values.Count(item => item.Changed),
                stableMemberCount = map.Values.Count(item => !item.Changed),
                errorCount = errors.Count,
                changedMembers = changed,
                errors = errors.Take(MaxChangedMembers).ToArray()
            };
        }

        static object SerializeRuntimeValue(object value, ProbeOptions options, int depth = 0)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string str)
            {
                return Truncate(str, options.MaxStringLength);
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is decimal)
            {
                return NormalizeNumber(value);
            }

            if (value is Enum)
            {
                return value.ToString();
            }

            if (value is Vector2 v2)
            {
                return new { x = v2.x, y = v2.y };
            }

            if (value is Vector3 v3)
            {
                return Vector3Dto(v3);
            }

            if (value is Vector4 v4)
            {
                return new { x = v4.x, y = v4.y, z = v4.z, w = v4.w };
            }

            if (value is Quaternion q)
            {
                return new { x = q.x, y = q.y, z = q.z, w = q.w, euler = Vector3Dto(q.eulerAngles) };
            }

            if (value is Color color)
            {
                return new { r = color.r, g = color.g, b = color.b, a = color.a };
            }

            if (value is Rect rect)
            {
                return new { x = rect.x, y = rect.y, width = rect.width, height = rect.height };
            }

            if (value is Bounds bounds)
            {
                return new { center = Vector3Dto(bounds.center), size = Vector3Dto(bounds.size), min = Vector3Dto(bounds.min), max = Vector3Dto(bounds.max) };
            }

            if (value is Object unityObject)
            {
                return SerializeUnityObject(unityObject);
            }

            if (depth < 1 && value is IDictionary dictionary)
            {
                var items = new List<object>();
                var count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    items.Add(new
                    {
                        key = SerializeRuntimeValue(entry.Key, options, depth + 1),
                        value = SerializeRuntimeValue(entry.Value, options, depth + 1)
                    });
                    count++;
                    if (count >= options.MaxCollectionItems)
                    {
                        break;
                    }
                }

                return new
                {
                    type = type.FullName,
                    count = SafeCollectionCount(value),
                    truncated = count >= options.MaxCollectionItems,
                    items
                };
            }

            if (depth < 1 && value is IEnumerable enumerable)
            {
                var items = new List<object>();
                var count = 0;
                foreach (var item in enumerable)
                {
                    items.Add(SerializeRuntimeValue(item, options, depth + 1));
                    count++;
                    if (count >= options.MaxCollectionItems)
                    {
                        break;
                    }
                }

                return new
                {
                    type = type.FullName,
                    count = SafeCollectionCount(value),
                    truncated = count >= options.MaxCollectionItems,
                    items
                };
            }

            return new
            {
                type = type.FullName,
                display = Truncate(value.ToString(), options.MaxStringLength)
            };
        }

        static object TruncateStrings(object value, ProbeOptions options)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string str)
            {
                return Truncate(str, options.MaxStringLength);
            }

            return value;
        }

        static object SerializeUnityObject(Object obj)
        {
            if (obj == null)
            {
                return null;
            }

            var path = AssetDatabase.GetAssetPath(obj);
            var go = obj as GameObject;
            var component = obj as Component;

            return new
            {
                name = obj.name,
                type = obj.GetType().FullName,
                instanceId = UnityApiAdapter.GetObjectId(obj),
                assetPath = string.IsNullOrWhiteSpace(path) ? null : path,
                guid = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.AssetPathToGUID(path),
                hierarchyPath = go != null
                    ? SceneObjectLocator.GetHierarchyPath(go, leadingSlash: true)
                    : component != null
                        ? SceneObjectLocator.GetHierarchyPath(component.gameObject, leadingSlash: true)
                        : null
            };
        }

        static IEnumerable<FieldInfo> EnumerateFields(Type type, bool includeNonPublic)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            return type.GetFields(flags)
                .Where(field => includeNonPublic || field.IsPublic || field.IsDefined(typeof(SerializeField), true))
                .OrderBy(field => field.DeclaringType == type ? 0 : 1)
                .ThenBy(field => field.MetadataToken);
        }

        static IEnumerable<PropertyInfo> EnumerateProperties(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy;
            return type.GetProperties(flags)
                .OrderBy(property => property.DeclaringType == type ? 0 : 1)
                .ThenBy(property => property.MetadataToken);
        }

        static FieldInfo FindField(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            return type.GetFields(flags)
                .FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        static PropertyInfo FindProperty(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy;
            return type.GetProperties(flags)
                .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        static bool CanReadProperty(PropertyInfo property)
        {
            return property != null
                   && property.CanRead
                   && property.GetIndexParameters().Length == 0
                   && property.GetMethod != null
                   && property.GetMethod.IsPublic;
        }

        static string SavePayload(object payload, string name)
        {
            var directory = Path.Combine(Directory.GetCurrentDirectory(), "Library", "UniBridge", "RuntimeStateProbe");
            Directory.CreateDirectory(directory);
            var fileName = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)
                           + "-"
                           + SanitizeFileName(NormalizeName(name, "runtime-state-probe"))
                           + ".json";
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, JsonConvert.SerializeObject(payload, Formatting.Indented));
            return path.Replace('\\', '/');
        }

        static string NormalizeName(string name, string fallback)
        {
            return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        }

        static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = NormalizeName(name, "runtime-state-probe")
                .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
                .ToArray();
            var sanitized = new string(chars).Trim('-');
            return string.IsNullOrWhiteSpace(sanitized) ? "runtime-state-probe" : sanitized;
        }

        static string NormalizeMemberKey(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
        }

        static object NormalizeNumber(object value)
        {
            return value switch
            {
                float f when float.IsNaN(f) || float.IsInfinity(f) => f.ToString(CultureInfo.InvariantCulture),
                double d when double.IsNaN(d) || double.IsInfinity(d) => d.ToString(CultureInfo.InvariantCulture),
                _ => value
            };
        }

        static string Truncate(string value, int max)
        {
            if (value == null || max <= 0 || value.Length <= max)
            {
                return value;
            }

            return value.Substring(0, max) + "...";
        }

        static int? SafeCollectionCount(object value)
        {
            return value switch
            {
                ICollection collection => collection.Count,
                _ => null
            };
        }

        static string SafeTag(GameObject go)
        {
            try { return go.tag; }
            catch { return null; }
        }

        static object Vector3Dto(Vector3 value) => new { x = value.x, y = value.y, z = value.z };

        static int SafeInt(Func<int> read)
        {
            try { return read(); }
            catch { return 0; }
        }

        static float SafeFloat(Func<float> read)
        {
            try { return read(); }
            catch { return 0f; }
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        static long ReadObjectId(object gameObjectSummary)
        {
            return ReadLong(gameObjectSummary, "instanceId");
        }

        static long ReadLong(object source, string propertyName)
        {
            if (source == null)
            {
                return 0;
            }

            var property = source.GetType().GetProperty(propertyName);
            if (property == null)
            {
                return 0;
            }

            try
            {
                var value = property.GetValue(source, null);
                return value == null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        static string ReadString(object source, string propertyName)
        {
            if (source == null)
            {
                return null;
            }

            var property = source.GetType().GetProperty(propertyName);
            if (property == null)
            {
                return null;
            }

            try
            {
                return property.GetValue(source, null)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        sealed class ProbeOptions
        {
            public bool IncludeInactive;
            public bool IncludePrefabStage;
            public int MaxTargets;
            public int MaxComponents;
            public int MaxMembers;
            public bool IncludeSerializedFields;
            public bool IncludeReadableMembers;
            public bool IncludeNonPublicFields;
            public int MaxStringLength;
            public int MaxCollectionItems;
            public string[] RequestedMembers;

            public static ProbeOptions From(RuntimeStateProbeParams parameters)
            {
                return new ProbeOptions
                {
                    IncludeInactive = parameters.IncludeInactive ?? true,
                    IncludePrefabStage = parameters.IncludePrefabStage ?? true,
                    MaxTargets = Clamp(parameters.MaxTargets ?? DefaultMaxTargets, 1, 50),
                    MaxComponents = Clamp(parameters.MaxComponents ?? DefaultMaxComponents, 1, 100),
                    MaxMembers = Clamp(parameters.MaxMembers ?? DefaultMaxMembers, 1, 500),
                    IncludeSerializedFields = parameters.IncludeSerializedFields ?? true,
                    IncludeReadableMembers = parameters.IncludeReadableMembers ?? true,
                    IncludeNonPublicFields = parameters.IncludeNonPublicFields ?? false,
                    MaxStringLength = Clamp(parameters.MaxStringLength ?? DefaultMaxStringLength, 40, 10000),
                    MaxCollectionItems = Clamp(parameters.MaxCollectionItems ?? DefaultMaxCollectionItems, 0, 200),
                    RequestedMembers = (parameters.Members ?? Array.Empty<string>())
                        .Where(member => !string.IsNullOrWhiteSpace(member))
                        .Select(member => member.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                };
            }
        }

        sealed class ProbeSample
        {
            public int SampleIndex;
            public string CapturedUtc;
            public double EditorTime;
            public int FrameCount;
            public int RenderedFrameCount;
            public float Time;
            public float DeltaTime;
            public List<TargetProbe> Targets = new();

            public object ToDto()
            {
                return new
                {
                    sampleIndex = SampleIndex,
                    capturedUtc = CapturedUtc,
                    editorTime = EditorTime,
                    frameCount = FrameCount,
                    renderedFrameCount = RenderedFrameCount,
                    time = Time,
                    deltaTime = DeltaTime,
                    targets = Targets.Select(target => target.ToDto()).ToArray()
                };
            }
        }

        sealed class TargetProbe
        {
            public object GameObject;
            public List<ComponentProbe> Components = new();

            public object ToDto()
            {
                return new
                {
                    gameObject = GameObject,
                    components = Components.Select(component => component.ToDto()).ToArray()
                };
            }
        }

        sealed class ComponentProbe
        {
            public int Index;
            public bool MissingScript;
            public string Type;
            public string ShortType;
            public Dictionary<string, object> ScriptIdentity;
            public bool? Enabled;
            public List<ValueProbe> Values = new();

            public object ToDto()
            {
                return new
                {
                    index = Index,
                    missingScript = MissingScript,
                    type = Type,
                    shortType = ShortType,
                    scriptIdentity = ScriptIdentity,
                    enabled = Enabled,
                    values = Values.Select(value => value.ToDto()).ToArray()
                };
            }
        }

        sealed class ValueProbe
        {
            public int ComponentIndex;
            public string Source;
            public string MemberPath;
            public string MemberName;
            public string DisplayName;
            public string ValueType;
            public string DeclaringType;
            public object Value;
            public string Error;

            public object ToDto()
            {
                return new
                {
                    componentIndex = ComponentIndex,
                    source = Source,
                    memberPath = MemberPath,
                    memberName = MemberName,
                    displayName = DisplayName,
                    valueType = ValueType,
                    declaringType = DeclaringType,
                    value = Value,
                    error = Error
                };
            }
        }

        sealed class ChangeAccumulator
        {
            public long ObjectId;
            public string ObjectPath;
            public int ComponentIndex;
            public string ComponentType;
            public string MemberPath;
            public string Source;
            public object FirstValue;
            public object LastValue;
            public string LastToken;
            public int FirstSampleIndex;
            public int LastSampleIndex;
            public int SampleCount;
            public int ChangeCount;

            public bool Changed => ChangeCount > 0;

            public void Add(int sampleIndex, object value)
            {
                var token = JsonConvert.SerializeObject(value, Formatting.None);
                if (SampleCount == 0)
                {
                    FirstValue = value;
                    FirstSampleIndex = sampleIndex;
                    LastToken = token;
                }
                else if (!string.Equals(LastToken, token, StringComparison.Ordinal))
                {
                    ChangeCount++;
                    LastToken = token;
                }

                LastValue = value;
                LastSampleIndex = sampleIndex;
                SampleCount++;
            }

            public object ToDto()
            {
                return new
                {
                    objectId = ObjectId,
                    objectPath = ObjectPath,
                    componentIndex = ComponentIndex,
                    componentType = ComponentType,
                    memberPath = MemberPath,
                    source = Source,
                    sampleCount = SampleCount,
                    firstSampleIndex = FirstSampleIndex,
                    lastSampleIndex = LastSampleIndex,
                    changeCount = ChangeCount,
                    firstValue = FirstValue,
                    lastValue = LastValue
                };
            }
        }
    }
}
