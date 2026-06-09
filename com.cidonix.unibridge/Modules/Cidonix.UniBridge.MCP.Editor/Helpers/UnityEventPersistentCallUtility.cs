#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    /// <summary>
    /// Shared read/write support for UnityEvent persistent calls. The shape mirrors the
    /// UnityEvent JSON idea, but keeps UniBridge scene and asset reference resolution.
    /// </summary>
    public static class UnityEventPersistentCallUtility
    {
        const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public sealed class EventReference
        {
            public GameObject GameObject { get; set; }
            public Object Owner { get; set; }
            public UnityEventBase UnityEvent { get; set; }
            public string EventName { get; set; }
            public string SourceKind { get; set; }
            public string ComponentType { get; set; }
        }

        public sealed class PersistentCallSpec
        {
            public Object TargetObject { get; set; }
            public MethodInfo Method { get; set; }
            public string MethodName { get; set; }
            public PersistentListenerMode Mode { get; set; }
            public UnityEventCallState CallState { get; set; } = UnityEventCallState.RuntimeOnly;
            public bool HasArgument { get; set; }
            public object Argument { get; set; }
            public Type ArgumentType { get; set; }
        }

        public sealed class ApplyResult
        {
            public int RemovedCount { get; set; }
            public int AddedCount { get; set; }
            public bool DryRun { get; set; }
            public object[] Calls { get; set; }
        }

        public static List<EventReference> FindEvents(
            GameObject gameObject,
            string componentFilter = null,
            string eventProperty = null)
        {
            var results = new List<EventReference>();
            if (gameObject == null)
            {
                return results;
            }

            var owners = new List<Object> { gameObject };
            owners.AddRange(gameObject.GetComponents<Component>().Where(component => component != null));

            foreach (var owner in owners.Where(owner => MatchesComponentFilter(owner, componentFilter)))
            {
                AddPropertyEvents(results, gameObject, owner, eventProperty);
                AddFieldEvents(results, gameObject, owner, eventProperty);
            }

            return results
                .OrderBy(reference => reference.Owner is GameObject ? 0 : 1)
                .ThenBy(reference => reference.ComponentType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(reference => reference.EventName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static object BuildEventReferenceInfo(EventReference reference)
        {
            if (reference == null)
            {
                return null;
            }

            return new
            {
                eventName = reference.EventName,
                sourceKind = reference.SourceKind,
                owner = BuildObjectReferenceInfo(reference.Owner),
                componentType = reference.ComponentType,
                gameObject = BuildObjectReferenceInfo(reference.GameObject),
                unityEvent = BuildEventInfo(reference.UnityEvent)
            };
        }

        public static object BuildEventInfo(UnityEventBase unityEvent)
        {
            if (unityEvent == null)
            {
                return null;
            }

            var count = unityEvent.GetPersistentEventCount();
            return new
            {
                eventType = unityEvent.GetType().FullName,
                persistentListenerCount = count,
                persistentCalls = Enumerable.Range(0, count)
                    .Select(index => BuildPersistentCallInfo(unityEvent, index))
                    .ToArray()
            };
        }

        public static object BuildPersistentCallSpecInfo(PersistentCallSpec spec)
        {
            if (spec == null)
            {
                return null;
            }

            return new
            {
                callState = spec.CallState.ToString(),
                target = BuildObjectReferenceInfo(spec.TargetObject),
                methodName = spec.MethodName,
                mode = spec.Mode.ToString(),
                argument = spec.HasArgument ? SerializeArgument(spec.Argument) : null
            };
        }

        public static bool TryBuildPersistentCallSpec(
            JObject call,
            GameObject defaultTarget,
            string defaultComponent,
            out PersistentCallSpec spec,
            out string error)
        {
            spec = null;
            error = null;

            if (call == null)
            {
                error = "Persistent call must be an object.";
                return false;
            }

            var methodName = GetString(call, "methodName", "method", "EventMethod", "eventMethod", "event_method");
            if (string.IsNullOrWhiteSpace(methodName))
            {
                error = "Persistent call requires methodName/method.";
                return false;
            }

            var targetToken = GetToken(call, "target", "Target", "eventTarget", "EventTarget", "listenerTarget", "listener_target");
            var componentName = GetString(call, "component", "Component", "eventComponent", "EventComponent", "event_component", "listenerComponent", "listener_component")
                ?? defaultComponent;
            var argumentToken = GetToken(call, "argument", "Argument", "eventArgument", "EventArgument", "event_argument", "arg", "value");
            var hasArgument = argumentToken != null && argumentToken.Type != JTokenType.Null;
            var argumentTypeHint = GetString(call, "argumentType", "ArgumentType", "eventArgumentType", "EventArgumentType", "event_argument_type", "mode");
            var callStateText = GetString(call, "callState", "CallState", "call_state", "state");

            if (!TryParseCallState(callStateText, out var callState, out error))
            {
                return false;
            }

            if (!TryParseArgumentTypeHint(argumentTypeHint, out var preferredArgumentType, out error))
            {
                return false;
            }

            var candidateObjects = ResolveCandidateTargets(targetToken, componentName, defaultTarget, out error);
            if (candidateObjects.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "No persistent call target was found.";
                }

                return false;
            }

            foreach (var candidate in candidateObjects)
            {
                if (TryFindMethod(candidate, methodName, argumentToken, hasArgument, preferredArgumentType, out var method, out var mode, out var argument, out var argumentType, out _))
                {
                    spec = new PersistentCallSpec
                    {
                        TargetObject = candidate,
                        Method = method,
                        MethodName = method.Name,
                        Mode = mode,
                        CallState = callState,
                        HasArgument = hasArgument,
                        Argument = argument,
                        ArgumentType = argumentType
                    };
                    return true;
                }
            }

            var candidateTypes = string.Join(", ", candidateObjects.Select(obj => obj.GetType().Name).Distinct());
            error = hasArgument
                ? $"No void method '{methodName}' with a supported static argument was found on target candidate(s): {candidateTypes}."
                : $"No void method '{methodName}()' was found on target candidate(s): {candidateTypes}.";
            return false;
        }

        public static ApplyResult ApplyPersistentCalls(
            UnityEventBase unityEvent,
            IReadOnlyList<PersistentCallSpec> calls,
            bool clearExisting,
            bool dryRun)
        {
            if (unityEvent == null)
            {
                throw new ArgumentNullException(nameof(unityEvent));
            }

            calls ??= Array.Empty<PersistentCallSpec>();
            var result = new ApplyResult
            {
                RemovedCount = clearExisting ? unityEvent.GetPersistentEventCount() : 0,
                AddedCount = calls.Count,
                DryRun = dryRun,
                Calls = calls.Select(BuildPersistentCallSpecInfo).ToArray()
            };

            if (dryRun)
            {
                return result;
            }

            if (clearExisting)
            {
                for (var i = unityEvent.GetPersistentEventCount() - 1; i >= 0; i--)
                {
                    UnityEventTools.RemovePersistentListener(unityEvent, i);
                }
            }

            foreach (var call in calls)
            {
                UnityEventTools.AddPersistentListener(unityEvent);
                var index = unityEvent.GetPersistentEventCount() - 1;
                SetPersistentCall(unityEvent, index, call);
            }

            DirtyPersistentCalls(unityEvent);
            return result;
        }

        public static object BuildObjectReferenceInfo(Object value)
        {
            if (value == null)
            {
                return null;
            }

            var assetPath = AssetDatabase.GetAssetPath(value);
            var gameObject = value as GameObject;
            if (gameObject == null && value is Component component)
            {
                gameObject = component.gameObject;
            }

            return new
            {
                name = value.name,
                type = value.GetType().FullName,
                id = UnityApiAdapter.GetObjectId(value),
                assetPath = string.IsNullOrWhiteSpace(assetPath) ? null : assetPath,
                guid = string.IsNullOrWhiteSpace(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath),
                scenePath = gameObject != null && gameObject.scene.IsValid() ? gameObject.scene.path : null,
                hierarchyPath = gameObject != null ? SceneObjectLocator.GetHierarchyPath(gameObject, leadingSlash: true) : null
            };
        }

        static void AddPropertyEvents(List<EventReference> results, GameObject gameObject, Object owner, string eventProperty)
        {
            foreach (var property in owner.GetType().GetProperties(InstanceFlags)
                         .Where(property => property.GetIndexParameters().Length == 0)
                         .Where(property => typeof(UnityEventBase).IsAssignableFrom(property.PropertyType))
                         .Where(property => MatchesEventName(property.Name, eventProperty)))
            {
                UnityEventBase unityEvent = null;
                try
                {
                    unityEvent = property.GetValue(owner, null) as UnityEventBase;
                }
                catch
                {
                    // Ignore event properties that throw from getters.
                }

                AddUniqueEvent(results, gameObject, owner, unityEvent, property.Name, "property");
            }
        }

        static void AddFieldEvents(List<EventReference> results, GameObject gameObject, Object owner, string eventProperty)
        {
            foreach (var field in owner.GetType().GetFields(InstanceFlags)
                         .Where(field => typeof(UnityEventBase).IsAssignableFrom(field.FieldType))
                         .Where(field => MatchesEventName(field.Name, eventProperty)))
            {
                UnityEventBase unityEvent = null;
                try
                {
                    unityEvent = field.GetValue(owner) as UnityEventBase;
                }
                catch
                {
                    // Ignore event fields that cannot be read.
                }

                AddUniqueEvent(results, gameObject, owner, unityEvent, field.Name, "field");
            }
        }

        static void AddUniqueEvent(
            List<EventReference> results,
            GameObject gameObject,
            Object owner,
            UnityEventBase unityEvent,
            string eventName,
            string sourceKind)
        {
            if (unityEvent == null || results.Any(existing => existing.Owner == owner && ReferenceEquals(existing.UnityEvent, unityEvent)))
            {
                return;
            }

            results.Add(new EventReference
            {
                GameObject = gameObject,
                Owner = owner,
                UnityEvent = unityEvent,
                EventName = eventName,
                SourceKind = sourceKind,
                ComponentType = owner.GetType().FullName
            });
        }

        static object BuildPersistentCallInfo(UnityEventBase unityEvent, int index)
        {
            TryGetPersistentEventArgument(unityEvent, index, out var mode, out var argument);

            return new
            {
                index,
                callState = unityEvent.GetPersistentListenerState(index).ToString(),
                target = BuildObjectReferenceInfo(unityEvent.GetPersistentTarget(index)),
                methodName = unityEvent.GetPersistentMethodName(index),
                mode = mode.ToString(),
                argument = ModeHasStaticArgument(mode) ? SerializeArgument(argument) : null
            };
        }

        static bool TryGetPersistentEventArgument(UnityEventBase unityEvent, int index, out PersistentListenerMode mode, out object value)
        {
            mode = PersistentListenerMode.EventDefined;
            value = null;

            try
            {
                var calls = GetPersistentCallsList(unityEvent);
                if (calls == null || index < 0 || index >= calls.Count)
                {
                    return false;
                }

                var call = calls[index];
                var modeValue = GetFieldValue(call, "m_Mode");
                if (modeValue is not PersistentListenerMode listenerMode)
                {
                    return false;
                }

                mode = listenerMode;
                if (!ModeHasStaticArgument(mode))
                {
                    return true;
                }

                var arguments = GetFieldValue(call, "m_Arguments");
                if (arguments == null)
                {
                    return false;
                }

                value = mode switch
                {
                    PersistentListenerMode.Object => GetFieldValue(arguments, "m_ObjectArgument"),
                    PersistentListenerMode.Int => GetFieldValue(arguments, "m_IntArgument"),
                    PersistentListenerMode.Float => GetFieldValue(arguments, "m_FloatArgument"),
                    PersistentListenerMode.String => GetFieldValue(arguments, "m_StringArgument"),
                    PersistentListenerMode.Bool => GetFieldValue(arguments, "m_BoolArgument"),
                    _ => null
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        static object SerializeArgument(object argument)
        {
            if (argument is Object unityObject)
            {
                return BuildObjectReferenceInfo(unityObject);
            }

            return argument;
        }

        static bool TryFindMethod(
            Object target,
            string methodName,
            JToken argumentToken,
            bool hasArgument,
            Type preferredArgumentType,
            out MethodInfo method,
            out PersistentListenerMode mode,
            out object argument,
            out Type argumentType,
            out string error)
        {
            method = null;
            mode = PersistentListenerMode.Void;
            argument = null;
            argumentType = null;
            error = null;

            if (target == null || string.IsNullOrWhiteSpace(methodName))
            {
                error = "Target and methodName are required.";
                return false;
            }

            var methods = target.GetType()
                .GetMethods(InstanceFlags)
                .Where(candidate => candidate.ReturnType == typeof(void))
                .Where(candidate => string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(candidate => candidate.DeclaringType == target.GetType() ? 0 : 1)
                .ToList();

            foreach (var candidate in methods)
            {
                var parameters = candidate.GetParameters();
                if (!hasArgument && parameters.Length == 0)
                {
                    method = candidate;
                    mode = PersistentListenerMode.Void;
                    return true;
                }

                if (hasArgument && parameters.Length == 1 &&
                    MatchesPreferredArgumentType(parameters[0].ParameterType, preferredArgumentType) &&
                    TryConvertStaticArgument(argumentToken, parameters[0].ParameterType, out var converted, out var listenerMode, out _))
                {
                    method = candidate;
                    mode = listenerMode;
                    argument = converted;
                    argumentType = parameters[0].ParameterType;
                    return true;
                }
            }

            return false;
        }

        static bool MatchesPreferredArgumentType(Type parameterType, Type preferredArgumentType)
        {
            if (preferredArgumentType == null)
            {
                return true;
            }

            if (parameterType == preferredArgumentType)
            {
                return true;
            }

            return preferredArgumentType == typeof(Object) && typeof(Object).IsAssignableFrom(parameterType);
        }

        static bool TryParseArgumentTypeHint(string value, out Type type, out string error)
        {
            type = null;
            error = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            var normalized = value.Trim()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();

            switch (normalized)
            {
                case "void":
                case "none":
                case "eventdefined":
                    return true;
                case "int":
                case "integer":
                    type = typeof(int);
                    return true;
                case "float":
                case "single":
                    type = typeof(float);
                    return true;
                case "string":
                case "str":
                    type = typeof(string);
                    return true;
                case "bool":
                case "boolean":
                    type = typeof(bool);
                    return true;
                case "object":
                case "unityobject":
                    type = typeof(Object);
                    return true;
                default:
                    error = $"UnityEvent argument type hint '{value}' is invalid. Use Void, Int, Float, String, Bool, or Object.";
                    return false;
            }
        }

        static bool TryConvertStaticArgument(
            JToken token,
            Type targetType,
            out object value,
            out PersistentListenerMode mode,
            out string error)
        {
            value = null;
            mode = PersistentListenerMode.EventDefined;
            error = null;

            try
            {
                if (targetType == typeof(int))
                {
                    value = ReadInt(token);
                    mode = PersistentListenerMode.Int;
                    return true;
                }

                if (targetType == typeof(float))
                {
                    value = ReadFloat(token);
                    mode = PersistentListenerMode.Float;
                    return true;
                }

                if (targetType == typeof(string))
                {
                    value = token?.Type == JTokenType.Null ? null : token?.ToString();
                    mode = PersistentListenerMode.String;
                    return true;
                }

                if (targetType == typeof(bool))
                {
                    value = ReadBool(token);
                    mode = PersistentListenerMode.Bool;
                    return true;
                }

                if (typeof(Object).IsAssignableFrom(targetType))
                {
                    value = ResolveObjectReference(token, targetType);
                    mode = PersistentListenerMode.Object;
                    return true;
                }

                error = $"Unity persistent calls do not support static arguments of type {targetType.FullName}.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static List<Object> ResolveCandidateTargets(
            JToken targetToken,
            string componentName,
            GameObject defaultTarget,
            out string error)
        {
            error = null;
            var resolved = ResolveObjectTarget(targetToken, defaultTarget, out error);
            if (resolved == null)
            {
                return new List<Object>();
            }

            if (resolved is GameObject gameObject)
            {
                if (!string.IsNullOrWhiteSpace(componentName) && !IsGameObjectName(componentName))
                {
                    var component = FindComponentByTypeName(gameObject, componentName);
                    if (component == null)
                    {
                        error = $"Component '{componentName}' was not found on '{gameObject.name}'.";
                        return new List<Object>();
                    }

                    return new List<Object> { component };
                }

                var candidates = new List<Object>();
                candidates.Add(gameObject);
                candidates.AddRange(gameObject.GetComponents<Component>().Where(component => component != null));
                return candidates;
            }

            if (resolved is Component componentTarget)
            {
                if (!string.IsNullOrWhiteSpace(componentName) &&
                    !MatchesTypeName(componentTarget.GetType(), componentName) &&
                    !IsGameObjectName(componentName))
                {
                    var component = FindComponentByTypeName(componentTarget.gameObject, componentName);
                    if (component == null)
                    {
                        error = $"Component '{componentName}' was not found on '{componentTarget.gameObject.name}'.";
                        return new List<Object>();
                    }

                    return new List<Object> { component };
                }

                return new List<Object> { componentTarget };
            }

            return new List<Object> { resolved };
        }

        static Object ResolveObjectTarget(JToken targetToken, GameObject defaultTarget, out string error)
        {
            error = null;

            if (targetToken == null || targetToken.Type == JTokenType.Null)
            {
                return defaultTarget;
            }

            if (targetToken.Type == JTokenType.Integer)
            {
                return UnityApiAdapter.GetObjectFromId(targetToken.ToObject<long>());
            }

            if (targetToken.Type == JTokenType.String)
            {
                var text = targetToken.ToString();
                return LooksLikeAssetPath(text)
                    ? LoadAssetReference(text, typeof(Object))
                    : SceneObjectLocator.FindObject(text, "by_id_or_name_or_path", new SceneObjectLocator.Options
                    {
                        IncludeInactive = true,
                        IncludePrefabStage = true
                    });
            }

            if (targetToken is JObject obj)
            {
                var id = GetLong(obj, "id", "objectId", "object_id", "instanceId", "instance_id");
                if (id.HasValue)
                {
                    return UnityApiAdapter.GetObjectFromId(id.Value);
                }

                var assetPath = GetString(obj, "assetPath", "asset_path", "path", "guid");
                if (!string.IsNullOrWhiteSpace(assetPath) && LooksLikeAssetPath(assetPath))
                {
                    return LoadAssetReference(assetPath, typeof(Object));
                }

                var find = GetString(obj, "find", "target", "name", "gameObject", "game_object");
                if (!string.IsNullOrWhiteSpace(find))
                {
                    var method = GetString(obj, "method", "searchMethod", "search_method");
                    return SceneObjectLocator.FindObject(find, method, new SceneObjectLocator.Options
                    {
                        IncludeInactive = true,
                        IncludePrefabStage = true
                    });
                }
            }

            error = "Target reference must be an instance id, scene object path/name, asset path/GUID, or object reference.";
            return null;
        }

        static Object ResolveObjectReference(JToken token, Type expectedType)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            Object value;
            if (token.Type == JTokenType.Integer)
            {
                value = UnityApiAdapter.GetObjectFromId(token.ToObject<long>());
            }
            else if (token.Type == JTokenType.String)
            {
                var text = token.ToString();
                value = LooksLikeAssetPath(text)
                    ? LoadAssetReference(text, expectedType)
                    : SceneObjectLocator.FindObject(text, "by_id_or_name_or_path", new SceneObjectLocator.Options
                    {
                        IncludeInactive = true,
                        IncludePrefabStage = true
                    });
            }
            else if (token is JObject obj)
            {
                value = ResolveObjectTarget(obj, null, out var resolveError);
                if (value == null && !string.IsNullOrWhiteSpace(resolveError))
                {
                    throw new ArgumentException(resolveError);
                }

                var componentName = GetString(obj, "component", "Component", "type", "Type");
                if (!string.IsNullOrWhiteSpace(componentName) && value is GameObject gameObject)
                {
                    value = FindComponentByTypeName(gameObject, componentName);
                }
            }
            else
            {
                throw new ArgumentException("Object argument must be null, id, asset path/GUID, scene object path/name, or object reference.");
            }

            return CastObjectReference(value, expectedType);
        }

        static void SetPersistentCall(UnityEventBase unityEvent, int index, PersistentCallSpec spec)
        {
            var calls = GetPersistentCallsList(unityEvent);
            if (calls == null || index < 0 || index >= calls.Count)
            {
                throw new InvalidOperationException("Persistent call list could not be accessed.");
            }

            var call = calls[index];
            SetFieldValue(call, "m_Target", spec.TargetObject);
            SetFieldValue(call, "m_MethodName", spec.MethodName);
            SetFieldValue(call, "m_Mode", spec.Mode);

            var arguments = GetFieldValue(call, "m_Arguments");
            if (arguments == null)
            {
                throw new InvalidOperationException("Persistent call arguments could not be accessed.");
            }

            ClearArguments(arguments);
            switch (spec.Mode)
            {
                case PersistentListenerMode.Object:
                    SetFieldValue(arguments, "m_ObjectArgument", spec.Argument as Object);
                    SetFieldValue(arguments, "m_ObjectArgumentAssemblyTypeName", spec.ArgumentType?.AssemblyQualifiedName ?? string.Empty);
                    break;
                case PersistentListenerMode.Int:
                    SetFieldValue(arguments, "m_IntArgument", Convert.ToInt32(spec.Argument, CultureInfo.InvariantCulture));
                    break;
                case PersistentListenerMode.Float:
                    SetFieldValue(arguments, "m_FloatArgument", Convert.ToSingle(spec.Argument, CultureInfo.InvariantCulture));
                    break;
                case PersistentListenerMode.String:
                    SetFieldValue(arguments, "m_StringArgument", spec.Argument as string);
                    break;
                case PersistentListenerMode.Bool:
                    SetFieldValue(arguments, "m_BoolArgument", Convert.ToBoolean(spec.Argument, CultureInfo.InvariantCulture));
                    break;
            }

            unityEvent.SetPersistentListenerState(index, spec.CallState);
        }

        static IList GetPersistentCallsList(UnityEventBase unityEvent)
        {
            var persistentCalls = typeof(UnityEventBase)
                .GetField("m_PersistentCalls", InstanceFlags)
                ?.GetValue(unityEvent);
            return persistentCalls?
                .GetType()
                .GetField("m_Calls", InstanceFlags)
                ?.GetValue(persistentCalls) as IList;
        }

        static void ClearArguments(object arguments)
        {
            SetFieldValue(arguments, "m_ObjectArgument", null);
            SetFieldValue(arguments, "m_ObjectArgumentAssemblyTypeName", string.Empty);
            SetFieldValue(arguments, "m_IntArgument", 0);
            SetFieldValue(arguments, "m_FloatArgument", 0f);
            SetFieldValue(arguments, "m_StringArgument", string.Empty);
            SetFieldValue(arguments, "m_BoolArgument", false);
        }

        static object GetFieldValue(object target, string fieldName)
        {
            return target?
                .GetType()
                .GetField(fieldName, InstanceFlags)
                ?.GetValue(target);
        }

        static void SetFieldValue(object target, string fieldName, object value)
        {
            var field = target?
                .GetType()
                .GetField(fieldName, InstanceFlags);
            if (field == null)
            {
                throw new InvalidOperationException($"Field '{fieldName}' was not found on {target?.GetType().FullName ?? "null"}.");
            }

            field.SetValue(target, value);
        }

        static void DirtyPersistentCalls(UnityEventBase unityEvent)
        {
            typeof(UnityEventBase)
                .GetMethod("DirtyPersistentCalls", InstanceFlags)
                ?.Invoke(unityEvent, null);
        }

        static bool MatchesComponentFilter(Object owner, string componentFilter)
        {
            if (string.IsNullOrWhiteSpace(componentFilter))
            {
                return true;
            }

            return owner is GameObject
                ? IsGameObjectName(componentFilter)
                : MatchesTypeName(owner.GetType(), componentFilter);
        }

        static bool MatchesEventName(string candidate, string requested)
        {
            return string.IsNullOrWhiteSpace(requested)
                   || string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(NormalizeName(candidate), NormalizeName(requested), StringComparison.OrdinalIgnoreCase);
        }

        static bool MatchesTypeName(Type type, string requested)
        {
            return type != null &&
                   (string.Equals(type.Name, requested, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.FullName, requested, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeName(type.Name), NormalizeName(requested), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeName(type.FullName), NormalizeName(requested), StringComparison.OrdinalIgnoreCase));
        }

        static bool IsGameObjectName(string value)
        {
            return string.Equals(value, "GameObject", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "UnityEngine.GameObject", StringComparison.OrdinalIgnoreCase);
        }

        static Component FindComponentByTypeName(GameObject gameObject, string componentName)
        {
            if (gameObject == null || string.IsNullOrWhiteSpace(componentName))
            {
                return null;
            }

            return gameObject.GetComponents<Component>()
                .FirstOrDefault(component => component != null && MatchesTypeName(component.GetType(), componentName));
        }

        static bool TryParseCallState(string value, out UnityEventCallState callState, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                callState = UnityEventCallState.RuntimeOnly;
                return true;
            }

            if (Enum.TryParse(value.Replace(" ", string.Empty).Replace("_", string.Empty), ignoreCase: true, out callState))
            {
                return true;
            }

            error = $"UnityEventCallState '{value}' is invalid. Use Off, EditorAndRuntime, or RuntimeOnly.";
            return false;
        }

        static bool ModeHasStaticArgument(PersistentListenerMode mode)
        {
            return mode is PersistentListenerMode.Object
                or PersistentListenerMode.Int
                or PersistentListenerMode.Float
                or PersistentListenerMode.String
                or PersistentListenerMode.Bool;
        }

        static int ReadInt(JToken token)
        {
            if (token.Type == JTokenType.String && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return token.ToObject<int>();
        }

        static float ReadFloat(JToken token)
        {
            if (token.Type == JTokenType.String && float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return token.ToObject<float>();
        }

        static bool ReadBool(JToken token)
        {
            if (token.Type == JTokenType.String && bool.TryParse(token.ToString(), out var parsed))
            {
                return parsed;
            }

            return token.ToObject<bool>();
        }

        static Object LoadAssetReference(string pathOrGuid, Type expectedType)
        {
            var path = NormalizeAssetPath(pathOrGuid);
            var asset = AssetDatabase.LoadAssetAtPath(path, expectedType ?? typeof(Object));
            if (asset == null && expectedType != typeof(Object))
            {
                asset = AssetDatabase.LoadAssetAtPath<Object>(path);
            }

            if (asset == null)
            {
                throw new ArgumentException($"Could not load asset reference '{pathOrGuid}'.");
            }

            return asset;
        }

        static Object CastObjectReference(Object value, Type expectedType)
        {
            if (value == null || expectedType == null || expectedType == typeof(Object))
            {
                return value;
            }

            if (expectedType.IsInstanceOfType(value))
            {
                return value;
            }

            if (value is GameObject gameObject)
            {
                if (expectedType == typeof(GameObject))
                {
                    return gameObject;
                }

                if (typeof(Component).IsAssignableFrom(expectedType))
                {
                    return gameObject.GetComponent(expectedType);
                }
            }

            if (value is Component component)
            {
                if (expectedType == typeof(GameObject))
                {
                    return component.gameObject;
                }

                if (typeof(Component).IsAssignableFrom(expectedType))
                {
                    return component.GetComponent(expectedType);
                }
            }

            throw new ArgumentException($"Object '{value.name}' ({value.GetType().Name}) cannot be used as {expectedType.Name}.");
        }

        static bool LooksLikeAssetPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Replace('\\', '/').Trim();
            return normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                   || normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                   || (normalized.Length == 32 && !normalized.Contains("/"));
        }

        static string NormalizeAssetPath(string pathOrGuid)
        {
            var path = pathOrGuid.Replace('\\', '/').Trim();
            if (path.Length == 32 && !path.Contains("/"))
            {
                path = AssetDatabase.GUIDToAssetPath(path);
            }

            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                path = "Assets/" + path.TrimStart('/');
            }

            return path;
        }

        static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var normalized = name.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace(".", string.Empty);
            if (normalized.StartsWith("m", StringComparison.OrdinalIgnoreCase) && normalized.Length > 1)
            {
                normalized = normalized.Substring(1);
            }

            return normalized.ToLowerInvariant();
        }

        static JToken GetToken(JObject obj, params string[] names)
        {
            foreach (var name in names)
            {
                if (obj.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                {
                    return token;
                }
            }

            return null;
        }

        static string GetString(JObject obj, params string[] names)
        {
            var token = GetToken(obj, names);
            return token == null ? null : token.ToString();
        }

        static long? GetLong(JObject obj, params string[] names)
        {
            var token = GetToken(obj, names);
            if (token == null)
            {
                return null;
            }

            return long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
    }
}
