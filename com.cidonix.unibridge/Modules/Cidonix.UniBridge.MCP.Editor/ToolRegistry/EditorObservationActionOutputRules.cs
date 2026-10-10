using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using static Cidonix.UniBridge.MCP.Editor.ToolRegistry.ActionOutputSchema;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    /// <summary>Serialized observation producers, including their nested error and omitted-option branches.</summary>
    internal static class EditorObservationActionOutputRules
    {
        static JObject S() => Nullable(Type("string"));
        static JObject I() => Type("integer");
        static JObject B() => Type("boolean");
        static JObject SS() => Arr(S());
        static JObject Outcome(JObject data) => AnyOf(Success(data), Error());
        static JObject V3() => Obj(("x", Number()), ("y", Number()), ("z", Number()));

        internal static Dictionary<string, JObject> Build(string owner)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            switch (owner)
            {
                case "UniBridge_EditorEvents":
                    foreach (var action in new[] { "Snapshot", "Get", "Clear" }) result[action] = Outcome(EventSnapshot());
                    break;
                case "UniBridge_WaitForEvent":
                    // WaitFor is the selector for this tool, rather than Action.
                    foreach (var condition in new[] { "AnyEvent", "Kind", "SelectionChanged", "HierarchyChanged", "ProjectChanged", "ProjectAssetsChanged", "CompilationStarted", "CompilationFinished", "AssemblyReloadBefore", "AssemblyReloadAfter", "PlayModeState", "PackagesChanged", "ObjectChanges", "EditorReady", "NextEditorUpdate" })
                        result[condition] = AnyOf(Success(WaitResult(condition, false)), Error(WaitResult(condition, true)), Error());
                    break;
                case "UniBridge_ExecutionStatus":
                    result["Snapshot"] = Outcome(Obj(("action", Const("Snapshot")), ("scheduler", Scheduler()), ("workSession", Nullable(RecoveryActionOutputRules.CompactActiveReview()))));
                    result["Recent"] = Outcome(Obj(("action", Const("Recent")), ("recent", Obj(("count", I()), ("maxRecentOperations", I()), ("operations", Arr(Operation())))), ("workSession", Nullable(RecoveryActionOutputRules.CompactActiveReview()))));
                    result["Policies"] = Outcome(Obj(("action", Const("Policies")), ("policies", Obj(("total", I()), ("includeDisabled", B()), ("tools", Arr(Obj(("name", S()), ("title", S()), ("enabled", B()), ("enabledByDefault", B()), ("groups", Nullable(SS())), ("execution", ExecutionAnnotation()))))))));
                    result["ReapStale"] = Outcome(Obj(("action", Const("ReapStale")), ("graceMs", I()), ("forceReadOnly", B()), ("result", Obj(("reaped", Arr(Operation())), ("scheduler", Scheduler())))));
                    result["History"] = result["Recent"]; result["Policy"] = result["Policies"];
                    result["Reap"] = result["ReapStale"]; result["CancelStale"] = result["ReapStale"];
                    break;
                case "UniBridge_BehaviourContext": result["Default"] = Outcome(Behaviour()); break;
                case "UniBridge_ContextSnapshot": result["Default"] = Outcome(Context()); break;
            }
            return result;
        }

        internal static JObject ExecutionAnnotation() => Obj(("policy", S()), ("replaySafe", B()), ("readOnly", B()), ("exclusive", B()), ("timeoutParameters", SS()), ("note", S()));
        static JObject Operation() => Obj(("operationId", I()), ("requestId", S()), ("tool", S()), ("policy", S()), ("mode", S()), ("changedProject", B()), ("queuedUtc", S()), ("startedUtc", S()), ("finishedUtc", S()), ("waitMs", I()), ("durationMs", Nullable(I())), ("timeoutMs", I()), ("outcome", S()), ("cancellationRequested", B()), ("cancellationDeferred", B()), ("forcedRelease", Nullable(Obj(("releasedUtc", S()), ("reason", S())))), ("error", Nullable(Obj(("type", S()), ("message", S())))));
        internal static JObject Scheduler() => Obj(("activeReaders", I()), ("pending", Obj(("readers", I()), ("exclusive", I()))), ("readDepth", I()), ("writeDepth", I()), ("activeExclusive", Nullable(Obj(("leaseId", I()), ("tool", S()), ("policy", S()), ("startedUtc", S()), ("elapsedMs", I())))), ("activeOperations", Arr(Operation())), ("recentOperations", Arr(Operation())), ("totals", Obj(("started", I()), ("completed", I()), ("faulted", I()), ("timedOut", I()), ("canceled", I()), ("reaped", I()))), ("limits", Obj(("defaultTimeoutMs", I()), ("maxTimeoutMs", I()), ("maxRecentOperations", I()))));

        internal static JObject EventRecord() => Obj(("id", I()), ("kind", S()), ("message", S()), ("timeSinceStartup", Number()), ("utc", S()),
            // Event-specific authored data is a JSON value. The event envelope and all query metadata remain closed.
            ("data", JsonValue()));
        internal static JObject Diagnostics() => Obj(("isCompiling", B()), ("currentContext", S()), ("totalRetained", I()), ("returned", I()), ("errors", I()), ("warnings", I()), ("messages", Arr(Obj(("assemblyPath", S()), ("type", S()), ("message", S()), ("file", S()), ("line", I()), ("column", I()), ("utc", S())))));
        static JObject EventObject() => Obj(("name", S()), ("type", S()), ("instanceID", I()), ("isPersistent", B()), ("assetPath", S()), ("path", S()), ("hierarchyPath", S()), ("indexAtPath", Nullable(I())), ("guid", S()), ("scenePath", S()), ("sceneGuid", S()));
        static JObject EventGameObject() => Obj(("name", S()), ("instanceID", I()), ("path", S()), ("indexAtPath", I()), ("scenePath", S()), ("sceneGuid", S()), ("tag", S()), ("layer", I()), ("layerName", S()), ("activeSelf", B()), ("isActiveInHierarchy", B()));
        static JObject EventSelection() => Obj(("activeObject", Nullable(EventObject())), ("activeGameObject", Nullable(EventGameObject())), ("activeTransform", Nullable(EventObject())), ("activeInstanceID", I()), ("count", I()), ("assetGUIDs", SS()), ("activeScene", Obj(("name", S()), ("path", S()), ("guid", S()), ("isDirty", B()), ("isLoaded", B()))), ("objects", Arr(EventObject())), ("gameObjects", Arr(EventGameObject())));
        internal static JObject EventSnapshot() => Obj(("latestId", I()), ("sinceId", I()), ("count", I()), ("truncated", B()), ("events", Arr(EventRecord())), ("selection", Nullable(EventSelection())), ("diagnostics", Nullable(Diagnostics())), ("assetChanges", Nullable(Obj(("latestId", I()), ("sinceId", I()), ("count", I()), ("truncated", B()), ("changes", Arr(Obj(("id", I()), ("kind", S()), ("path", S()), ("oldPath", S()), ("guid", S()), ("utc", S())))), ("referenceGraphHint", S())))));
        static JObject WaitResult(string condition, bool timedOut) => Obj(("waitFor", Const(condition.ToLowerInvariant())), ("timedOut", Const(timedOut)), ("elapsedMs", I()), ("quietMs", I()), ("sinceId", I()), ("startLatestId", I()), ("latestId", I()), ("totalAfterSince", I()), ("matchCount", I()), ("truncated", B()), ("editorReady", B()), ("isCompiling", B()), ("isUpdating", B()), ("isPlaying", B()), ("isPlayingOrWillChangePlaymode", B()), ("events", Arr(EventRecord())));

        static JObject Behaviour()
        {
            var source = AnyOf(Obj(("included", Const(false)), ("reason", S()), ("assetPath", S())), Obj(("included", B()), ("assetPath", S()), ("fullPath", S()), ("length", I()), ("truncated", B()), ("content", S())));
            var field = Obj(("path", S()), ("name", S()), ("displayName", S()), ("type", S()), ("value", JsonValue()));
            var entry = Obj(("index", I()), ("missingScript", B()), ("type", S()), ("assembly", S()), ("scriptPath", S()), ("scriptGuid", S()), ("enabled", Nullable(B())), ("serializedFields", Nullable(Arr(field))), ("source", Nullable(source)));
            return Obj(("gameObject", Obj(("name", S()), ("instanceId", I()), ("path", S()), ("scene", Nullable(Obj(("name", S()), ("path", S())))), ("activeSelf", B()), ("activeInHierarchy", B()))), ("behaviourCount", I()), ("missingScriptCount", I()), ("behaviours", Arr(entry)));
        }

        static JObject Context()
        {
            var dependencies = AnyOf(Obj(("sourceFile", S()), ("count", I()), ("packages", Arr(Obj(("name", S()), ("version", S()), ("source", S()), ("depth", Nullable(I())))))), Obj(("sourceFile", S()), ("error", S())), Obj(("sourceFile", Type("null")), ("count", Const(0)), ("packages", Arr(Type("null"))), ("note", S())));
            var root = Obj(("name", S()), ("assetPath", S()), ("resolvedPath", S()), ("source", S()), ("exists", B()));
            var packageRoot = Obj(("name", S()), ("displayName", S()), ("version", S()), ("assetPath", S()), ("resolvedPath", S()), ("source", S()));
            var roots = OptionalObj(new[] { "roots", "registeredPackageCount" }, ("roots", Arr(root)), ("registeredPackageCount", I()), ("registeredPackages", Arr(packageRoot)));
            var environment = Obj(("companyName", S()), ("productName", S()), ("productGuid", S()), ("colorSpace", S()), ("activeBuildTarget", S()), ("activeBuildTargetGroup", S()), ("renderPipeline", Obj(("name", S()), ("type", S()), ("family", S()))), ("editorDefaults", Obj(("defaultBehaviorMode", S()), ("rendererType", S()))), ("tags", SS()), ("layers", Arr(Obj(("index", I()), ("name", S())))), ("renderingLayers", Arr(Obj(("index", I()), ("name", S()), ("bit", I())))), ("sortingLayers", Arr(Obj(("id", I()), ("name", S()), ("value", I())))));
            var project = Obj(("id", S()), ("name", S()), ("root", S()), ("assetsPath", S()), ("settingsPath", S()), ("unityVersion", S()), ("platform", S()), ("activeBuildTarget", S()), ("isBatchMode", B()), ("roots", Nullable(roots)), ("environment", Nullable(environment)));
            var editor = Obj(("isPlaying", B()), ("isPlayingOrWillChangePlaymode", B()), ("isPaused", B()), ("isCompiling", B()), ("isUpdating", B()), ("isBatchMode", B()), ("timeSinceStartup", Number()), ("selectionCount", I()), ("hasDirtyScenes", B()), ("applicationPath", S()));
            var tool = AnyOf(Obj(("tool", S()), ("pivotMode", S()), ("pivotRotation", S()), ("handlePosition", V3())), Obj(("error", S())));
            var scene = Obj(("valid", B()), ("name", S()), ("path", S()), ("buildIndex", I()), ("isLoaded", B()), ("isDirty", B()), ("rootCount", I()));
            var scenes = Obj(("active", scene), ("loadedCount", I()), ("loaded", Arr(scene)), ("buildSettings", Nullable(Arr(Obj(("index", I()), ("path", S()), ("guid", S()), ("enabled", B()))))));
            var prefab = AnyOf(Obj(("isOpen", B()), ("assetPath", S()), ("prefabRootName", S()), ("rootObjectId", I()), ("mode", S()), ("isDirty", B())), Obj(("isOpen", Const(false)), ("error", S())));
            var obj = Obj(("name", S()), ("type", S()), ("objectId", I()), ("assetPath", S()), ("hierarchyPath", S()), ("gameObjectId", I()));
            var go = Obj(("name", S()), ("objectId", I()), ("hierarchyPath", S()), ("scene", S()), ("components", SS()));
            var selection = Obj(("count", I()), ("activeObject", Nullable(obj)), ("activeGameObject", Nullable(go)), ("objects", Arr(obj)), ("gameObjects", Arr(go)), ("assetGuids", Nullable(SS())));
            var consoleData = Nullable(AnyOf(ReadConsoleActionOutputRules.DiagnosticSummaryData(false), ReadConsoleActionOutputRules.DiagnosticSummaryData(true)));
            var console = AnyOf(Obj(("success", B()), ("message", S()), ("summaryMode", S()), ("data", consoleData), ("error", S())), Obj(("success", Const(false)), ("message", S()), ("error", S())));
            var assets = Obj(("totalAssets", I()), ("countsByKind", Map(I())), ("countsByExtension", Map(I())), ("recentAssets", Arr(Obj(("path", S()), ("guid", S()), ("kind", S()), ("extension", S()), ("lastWriteUtc", S())))), ("recentAssetsLimit", I()), ("note", S()));
            var tools = Obj(("total", I()), ("enabled", I()), ("disabled", I()), ("groups", Arr(Obj(("name", S()), ("total", I()), ("enabled", I())))), ("entries", Arr(Obj(("name", S()), ("title", S()), ("enabled", B()), ("enabledByDefault", B()), ("groups", Nullable(SS()))))), ("truncated", B()));
            var windows = Arr(AnyOf(Obj(("title", S()), ("type", S()), ("isFocused", B()), ("objectId", I()), ("position", Obj(("x", Number()), ("y", Number()), ("width", Number()), ("height", Number())))), Obj(("error", S()))));
            return Obj(("snapshotId", S()), ("createdUtc", S()), ("depth", S()), ("limits", Obj(("hierarchyDepth", I()), ("maxSceneObjects", I()), ("maxAssets", I()), ("maxConsoleIssues", I()), ("consoleSummaryMode", S()), ("maxTools", I()))), ("project", project), ("package", Obj(("name", S()), ("displayName", S()), ("version", S()), ("dependencies", Nullable(dependencies)))), ("agentBrief", Nullable(AgentBrief())), ("editor", editor), ("activeTool", tool), ("scenes", scenes), ("prefabStage", prefab), ("selection", Nullable(selection)), ("hierarchy", Nullable(Hierarchy())), ("console", Nullable(console)), ("assets", Nullable(assets)), ("tools", Nullable(tools)), ("windows", Nullable(windows)), ("hints", SS()));
        }

        static JObject Hierarchy()
        {
            const string key = "ContextSnapshotHierarchyNode";
            var node = Obj(("name", S()), ("objectId", I()), ("path", S()), ("scene", S()), ("activeSelf", B()), ("activeInHierarchy", B()), ("tag", S()), ("layer", I()), ("layerName", S()), ("isStatic", B()), ("transform", Obj(("localPosition", V3()), ("localRotationEuler", V3()), ("localScale", V3()), ("worldPosition", V3()))), ("components", SS()), ("prefab", Nullable(Obj(("instanceStatus", S()), ("assetType", S()), ("assetPath", S())))), ("childCount", I()), ("childrenReturned", I()), ("childrenTruncated", B()), ("children", Nullable(Arr(new JObject { ["$ref"] = "#/$defs/" + key }))));
            var result = Obj(("depth", I()), ("maxObjects", I()), ("returnedObjects", I()), ("truncatedByCount", B()), ("truncatedByDepth", B()), ("scenes", Arr(Obj(("name", S()), ("path", S()), ("rootCount", I()), ("roots", Arr(new JObject { ["$ref"] = "#/$defs/" + key }))))));
            result["$defs"] = new JObject { [key] = node };
            return result;
        }

        static JObject AgentBrief()
        {
            var loadedScene = Obj(("name", S()), ("path", S()), ("rootCount", I()), ("isDirty", B()));
            var shape = Obj(("totalAssets", I()), ("countsByKind", Map(I())), ("loadedSceneCount", I()), ("loadedRootObjectCount", I()), ("dirtySceneCount", I()), ("activeScene", Nullable(loadedScene)), ("loadedScenes", Arr(loadedScene)), ("sceneScale", S()));
            var session = AnyOf(Obj(("active", Const(false)), ("sessionId", Type("null")), ("name", Type("null")), ("startedUtc", Type("null")), ("note", S())), Obj(("active", Const(true)), ("sessionId", S()), ("name", S()), ("startedUtc", S()), ("endedUtc", S()), ("capturedFileCount", Nullable(I())), ("semanticBaselineEnabled", Nullable(B())), ("nextSuggestedCalls", SS())), Obj(("active", Const(false)), ("error", S()), ("note", S())));
            return Obj(("purpose", S()), ("summary", S()), ("projectShape", shape), ("likelyFolders", Obj(("scenes", SS()), ("scripts", SS()), ("gameplay", SS()), ("ui", SS()), ("prefabs", SS()), ("art", SS()), ("audio", SS()), ("data", SS()))), ("likelyImportantSystems", Arr(Obj(("name", S()), ("source", S()), ("detail", S())))), ("activeWorkSession", session), ("riskFlags", Arr(Obj(("severity", S()), ("code", S()), ("message", S()), ("nextCall", S())))), ("guardrails", SS()), ("operatingProtocol", Obj(("readBeforeModify", SS()), ("scopeAwareness", SS()), ("executionSafety", SS()), ("riskSpecificHint", S()))), ("verificationLadder", SS()), ("recommendedNextCalls", SS()));
        }
    }
}
