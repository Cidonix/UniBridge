#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Read and write persistent calls on arbitrary UnityEvent fields/properties.
    /// </summary>
    public static class ManageUnityEvent
    {
        public const string Title = "Manage UnityEvent persistent calls";

        public const string Description = @"Inspect, add, replace, or clear persistent UnityEvent listeners on scene objects.

This generalizes the agent-oriented tooling JSONUnityEvent idea for UniBridge. It works beyond Button.onClick: an agent can inspect all UnityEvent members on a component, then set structured persistentCalls on Button, Toggle, Slider, custom MonoBehaviour UnityEvent fields, and other UnityEventBase members.

Args:
    Action: Inspect, AddPersistentCall, SetPersistentCalls, or ClearPersistentCalls.
    Target/SearchMethod: GameObject that owns the event.
    Component: optional component type that owns the UnityEvent.
    EventProperty: UnityEvent member name, e.g. onClick, m_OnClick, onValueChanged, or a custom field.
    PersistentCalls: structured { persistentCalls: [...] } or an array for SetPersistentCalls.
    PersistentCall or EventTarget/EventComponent/MethodName/Argument/CallState: single-call form.
    DryRun: preview changes without editing the scene.

Returns:
    success, message, target/event metadata, before/after persistentCalls, and planned/applied changes.";

        [McpTool("UniBridge_ManageUnityEvent", Description, Title, Groups = new[] { "core", "scene", "ui", "events" }, EnabledByDefault = true)]
        public static object HandleCommand(ManageUnityEventParams parameters)
        {
            parameters ??= new ManageUnityEventParams();

            try
            {
                return parameters.Action switch
                {
                    UnityEventManageAction.Inspect => Inspect(parameters),
                    UnityEventManageAction.AddPersistentCall => AddPersistentCall(parameters),
                    UnityEventManageAction.SetPersistentCalls => SetPersistentCalls(parameters),
                    UnityEventManageAction.ClearPersistentCalls => ClearPersistentCalls(parameters),
                    _ => Response.Error($"Unsupported UnityEvent action '{parameters.Action}'.")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManageUnityEvent] {parameters.Action} failed: {ex}");
                return Response.Error($"ManageUnityEvent action '{parameters.Action}' failed: {ex.Message}");
            }
        }

        static object Inspect(ManageUnityEventParams parameters)
        {
            var target = ResolveTargetGameObject(parameters);
            if (target == null)
            {
                return Response.Error("Inspect requires Target or a selected GameObject.");
            }

            var events = UnityEventPersistentCallUtility.FindEvents(
                target,
                parameters.Component,
                parameters.EventProperty);

            if (events.Count == 0)
            {
                return Response.Error($"No UnityEvent members were found on '{target.name}'.", new
                {
                    target = UnityEventPersistentCallUtility.BuildObjectReferenceInfo(target),
                    component = parameters.Component,
                    eventProperty = parameters.EventProperty
                });
            }

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = target;
            }

            return Response.Success($"Found {events.Count} UnityEvent member(s) on '{target.name}'.", new
            {
                action = parameters.Action.ToString(),
                target = UnityEventPersistentCallUtility.BuildObjectReferenceInfo(target),
                component = parameters.Component,
                eventProperty = parameters.EventProperty,
                events = events.Select(UnityEventPersistentCallUtility.BuildEventReferenceInfo).ToArray()
            });
        }

        static object AddPersistentCall(ManageUnityEventParams parameters)
        {
            return ConfigurePersistentCalls(parameters, replaceExisting: parameters.ClearExisting ?? false, requireCalls: true);
        }

        static object SetPersistentCalls(ManageUnityEventParams parameters)
        {
            return ConfigurePersistentCalls(parameters, replaceExisting: true, requireCalls: false);
        }

        static object ClearPersistentCalls(ManageUnityEventParams parameters)
        {
            var resolved = ResolveSingleEvent(parameters);
            if (!resolved.Success)
            {
                return Response.Error(resolved.Error, resolved.Data);
            }

            var eventRef = resolved.EventReference;
            var before = UnityEventPersistentCallUtility.BuildEventInfo(eventRef.UnityEvent);
            var dryRun = parameters.DryRun ?? false;
            var result = UnityEventPersistentCallUtility.ApplyPersistentCalls(
                eventRef.UnityEvent,
                Array.Empty<UnityEventPersistentCallUtility.PersistentCallSpec>(),
                clearExisting: true,
                dryRun);

            if (!dryRun)
            {
                MarkDirty(eventRef);
            }

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = eventRef.GameObject;
            }

            return Response.Success(
                dryRun
                    ? $"Dry run: would clear {result.RemovedCount} UnityEvent listener(s)."
                    : $"Cleared {result.RemovedCount} UnityEvent listener(s).",
                new
                {
                    action = parameters.Action.ToString(),
                    dryRun,
                    eventRef = UnityEventPersistentCallUtility.BuildEventReferenceInfo(eventRef),
                    removedCount = result.RemovedCount,
                    before,
                    after = dryRun ? before : UnityEventPersistentCallUtility.BuildEventInfo(eventRef.UnityEvent)
                });
        }

        static object ConfigurePersistentCalls(
            ManageUnityEventParams parameters,
            bool replaceExisting,
            bool requireCalls)
        {
            var resolved = ResolveSingleEvent(parameters);
            if (!resolved.Success)
            {
                return Response.Error(resolved.Error, resolved.Data);
            }

            var eventRef = resolved.EventReference;
            var callObjects = ExtractCallObjects(parameters, requireCalls, out var extractError);
            if (extractError != null)
            {
                return Response.Error(extractError);
            }

            var specs = new List<UnityEventPersistentCallUtility.PersistentCallSpec>();
            foreach (var callObject in callObjects)
            {
                if (!UnityEventPersistentCallUtility.TryBuildPersistentCallSpec(
                        callObject,
                        eventRef.GameObject,
                        parameters.EventComponent,
                        out var spec,
                        out var error))
                {
                    return Response.Error(error, new
                    {
                        action = parameters.Action.ToString(),
                        target = UnityEventPersistentCallUtility.BuildObjectReferenceInfo(eventRef.GameObject),
                        eventRef = UnityEventPersistentCallUtility.BuildEventReferenceInfo(eventRef),
                        call = callObject
                    });
                }

                specs.Add(spec);
            }

            var before = UnityEventPersistentCallUtility.BuildEventInfo(eventRef.UnityEvent);
            var dryRun = parameters.DryRun ?? false;
            if (!dryRun)
            {
                Undo.RecordObject(eventRef.Owner, "Configure UniBridge UnityEvent");
            }

            var result = UnityEventPersistentCallUtility.ApplyPersistentCalls(
                eventRef.UnityEvent,
                specs,
                replaceExisting,
                dryRun);

            if (!dryRun)
            {
                MarkDirty(eventRef);
            }

            if (parameters.Select ?? false)
            {
                Selection.activeGameObject = eventRef.GameObject;
            }

            return Response.Success(
                dryRun
                    ? $"Dry run: UnityEvent persistent listener(s) would be updated on '{eventRef.GameObject.name}'."
                    : $"Updated UnityEvent persistent listener(s) on '{eventRef.GameObject.name}'.",
                new
                {
                    action = parameters.Action.ToString(),
                    dryRun,
                    replaceExisting,
                    eventRef = UnityEventPersistentCallUtility.BuildEventReferenceInfo(eventRef),
                    removedCount = result.RemovedCount,
                    addedCount = result.AddedCount,
                    calls = result.Calls,
                    before,
                    after = dryRun ? before : UnityEventPersistentCallUtility.BuildEventInfo(eventRef.UnityEvent)
                });
        }

        static List<JObject> ExtractCallObjects(ManageUnityEventParams parameters, bool requireCalls, out string error)
        {
            error = null;
            var calls = new List<JObject>();

            if (parameters.PersistentCalls != null && parameters.PersistentCalls.Type != JTokenType.Null)
            {
                var token = parameters.PersistentCalls;
                if (token is JObject container && container["persistentCalls"] != null)
                {
                    token = container["persistentCalls"];
                }

                if (token is JArray array)
                {
                    foreach (var item in array)
                    {
                        if (item is not JObject call)
                        {
                            error = "Each PersistentCalls item must be an object.";
                            return calls;
                        }

                        calls.Add(call);
                    }
                }
                else if (token is JObject singleCall)
                {
                    calls.Add(singleCall);
                }
                else
                {
                    error = "PersistentCalls must be an array, a call object, or { persistentCalls: [...] }.";
                    return calls;
                }
            }

            if (parameters.PersistentCall != null)
            {
                calls.Add(parameters.PersistentCall);
            }

            if (!string.IsNullOrWhiteSpace(parameters.MethodName))
            {
                var call = new JObject
                {
                    ["methodName"] = parameters.MethodName
                };

                if (!string.IsNullOrWhiteSpace(parameters.EventTarget))
                {
                    call["target"] = parameters.EventTarget;
                }

                if (!string.IsNullOrWhiteSpace(parameters.EventComponent))
                {
                    call["component"] = parameters.EventComponent;
                }

                if (parameters.Argument != null && parameters.Argument.Type != JTokenType.Null)
                {
                    call["argument"] = parameters.Argument.DeepClone();
                }

                if (!string.IsNullOrWhiteSpace(parameters.CallState))
                {
                    call["callState"] = parameters.CallState;
                }

                calls.Add(call);
            }

            if (requireCalls && calls.Count == 0)
            {
                error = "AddPersistentCall requires PersistentCall/PersistentCalls or MethodName.";
            }

            return calls;
        }

        static SingleEventResolution ResolveSingleEvent(ManageUnityEventParams parameters)
        {
            var target = ResolveTargetGameObject(parameters);
            if (target == null)
            {
                return SingleEventResolution.Failure("UnityEvent action requires Target or a selected GameObject.");
            }

            var events = UnityEventPersistentCallUtility.FindEvents(
                target,
                parameters.Component,
                parameters.EventProperty);

            if (events.Count == 0)
            {
                return SingleEventResolution.Failure($"No UnityEvent member matched on '{target.name}'.", new
                {
                    target = UnityEventPersistentCallUtility.BuildObjectReferenceInfo(target),
                    component = parameters.Component,
                    eventProperty = parameters.EventProperty
                });
            }

            if (events.Count > 1)
            {
                return SingleEventResolution.Failure(
                    "UnityEvent target is ambiguous. Provide Component and EventProperty.",
                    new
                    {
                        target = UnityEventPersistentCallUtility.BuildObjectReferenceInfo(target),
                        candidates = events.Select(UnityEventPersistentCallUtility.BuildEventReferenceInfo).ToArray()
                    });
            }

            return SingleEventResolution.Ok(events[0]);
        }

        static GameObject ResolveTargetGameObject(ManageUnityEventParams parameters)
        {
            if (parameters == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(parameters.Target))
            {
                return Selection.activeGameObject;
            }

            return SceneObjectLocator.FindObject(
                parameters.Target,
                parameters.SearchMethod,
                new SceneObjectLocator.Options
                {
                    IncludeInactive = parameters.IncludeInactive ?? true,
                    IncludePrefabStage = true
                });
        }

        static void MarkDirty(UnityEventPersistentCallUtility.EventReference eventRef)
        {
            if (eventRef == null)
            {
                return;
            }

            if (eventRef.Owner != null)
            {
                EditorUtility.SetDirty(eventRef.Owner);
            }

            if (eventRef.GameObject != null && eventRef.GameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(eventRef.GameObject.scene);
            }

            SceneView.RepaintAll();
        }

        sealed class SingleEventResolution
        {
            public bool Success { get; private set; }
            public string Error { get; private set; }
            public object Data { get; private set; }
            public UnityEventPersistentCallUtility.EventReference EventReference { get; private set; }

            public static SingleEventResolution Ok(UnityEventPersistentCallUtility.EventReference eventReference)
            {
                return new SingleEventResolution
                {
                    Success = true,
                    EventReference = eventReference
                };
            }

            public static SingleEventResolution Failure(string error, object data = null)
            {
                return new SingleEventResolution
                {
                    Success = false,
                    Error = error,
                    Data = data
                };
            }
        }
    }
}
