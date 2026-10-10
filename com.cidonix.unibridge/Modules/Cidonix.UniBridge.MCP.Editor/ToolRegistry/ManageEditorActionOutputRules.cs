using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using static Cidonix.UniBridge.MCP.Editor.ToolRegistry.ActionOutputSchema;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    /// <summary>Business response shapes for every ManageEditor dispatch branch.</summary>
    internal static class ManageEditorActionOutputRules
    {
        static JObject B() => Type("boolean");
        static JObject I() => Type("integer");
        static JObject S() => Nullable(Type("string"));
        static JObject Strings() => Arr(S());
        static JObject Outcome(params JObject[] branches) => AnyOf(AnyOf(branches), Error());

        internal static Dictionary<string, JObject> Build()
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var state = PlayState();
            var ready = Readiness();
            var waitData = Obj(("readiness", ready), ("waitedMs", I()));
            var waitError = Obj(("readiness", ready), ("timeoutMs", I()),
                ("requireNotPlaying", B()), ("completed", Const(false)), ("waitSucceeded", Const(false)));
            var wait = Outcome(Success(waitData), Error(waitError));
            var playWait = Outcome(Success(waitData), Error(Obj(("timeoutMs", I()), ("readiness", ready))),
                Error(PlayBoundary()));
            var queuedPlay = Obj(("requestId", S()), ("status", Const("queued")), ("targetPlaying", B()),
                ("waitForCompletionRequested", B()), ("inlineWaitSkipped", B()), ("reconnectRequired", Const(true)),
                ("reloadSafe", Const(true)), ("batchBoundary", Const(true)), ("changedProject", Const(false)),
                ("reason", S()), ("stateBefore", state), ("readinessBefore", ready), ("nextSuggestedCalls", Strings()));
            foreach (var action in new[] { "Play", "RequestPlayModeNoWait", "Stop", "ExitPlayMode" })
                result[action] = Outcome(Success(state), Success(queuedPlay));
            result["WaitForPlayMode"] = playWait;
            result["WaitForEditMode"] = playWait;
            foreach (var action in new[] { "Pause", "SetActiveTool", "AddTag", "RemoveTag", "AddLayer", "RemoveLayer" })
                result[action] = Outcome(Success());
            result["GetState"] = Outcome(Success(DTO(typeof(EditorStateData))));
            result["GetPlayModeState"] = Outcome(Success(Obj(("state", state), ("isPlaying", B()),
                ("isPaused", B()), ("isPlayingOrWillChangePlaymode", B()),
                ("canRequestScriptCompilation", B()), ("readiness", ready))));
            result["GetCompilationDiagnostics"] = Outcome(Success(CompilationDiagnostics(true)));
            result["GetProjectRoot"] = Outcome(Success(Obj(("projectRoot", S()))));
            result["GetWindows"] = Outcome(Success(Arr(DTO(typeof(EditorWindowInfo)))));
            result["GetActiveTool"] = Outcome(Success(DTO(typeof(ActiveToolData))));
            result["GetSelection"] = Outcome(Success(DTO(typeof(SelectionData))));
            result["GetPrefabStage"] = Outcome(Success(DTO(typeof(PrefabStageData))));
            var selection = DTO(typeof(SelectionData));
            var selectAsset = Obj(("selected", Nullable(DTO(typeof(SelectionObjectInfo)))), ("selection", selection));
            result["SelectAsset"] = Outcome(Success(selectAsset));
            result["PingSelection"] = Outcome(Success(selectAsset));
            result["SelectGameObject"] = Outcome(Success(Obj(
                ("selected", Nullable(DTO(typeof(GameObjectSelectionInfo)))), ("selection", selection))));
            foreach (var action in new[] { "ClearSelection", "FrameSelection" })
                result[action] = Outcome(Success(Obj(("selection", selection))));
            result["WaitForReady"] = wait;
            result["WaitIdle"] = wait;
            result["WaitForReadyAfterReload"] = Outcome(
                Success(Obj(("waitResult", Success(waitData)), ("compileHealth", CompileHealth()),
                    ("buildSystemHealth", BuildSystemHealth()), ("assemblyFreshness", AssemblyFreshness()),
                    ("compilationDiagnostics", CompilationDiagnostics(false)), ("readiness", ready))),
                Error(waitError), Error(Obj(("completed", Const(false)), ("waitSucceeded", Const(false)))));
            result["RefreshAssets"] = Outcome(Success(Obj(("forced", B()), ("readiness", ready))),
                Success(waitData), Error(waitError));
            var queuedCompilation = Obj(("requestId", S()), ("status", Const("queued")), ("forced", B()),
                ("waitForCompletionRequested", B()), ("inlineWaitSkipped", B()), ("reconnectRequired", Const(true)),
                ("reloadSafe", Const(true)), ("reason", S()), ("readinessBefore", ready), ("nextSuggestedCalls", Strings()));
            result["RequestScriptCompilation"] = Outcome(Success(queuedCompilation), Success(Obj(
                ("forced", B()), ("status", Const("requested")), ("waitForCompletionRequested", Const(false)),
                ("reconnectRequired", Const(true)), ("readiness", ready))));
            result["RequestScriptCompilationNoWait"] = Outcome(Success(queuedCompilation));
            var savedScenes = Arr(Obj(("name", S()), ("path", S()), ("saved", B())));
            var save = Outcome(Success(Obj(("savedScenes", savedScenes),
                ("skippedScenes", Arr(Obj(("name", S()), ("reason", S())))),
                ("savedPrefabStage", Nullable(Obj(("path", S()), ("saved", B())))),
                ("savedAssets", B()), ("warnings", Strings()), ("readiness", ready))));
            result["SaveAll"] = save;
            result["SaveAssets"] = save;
            var generated = Outcome(Success(Obj(("method", S()), ("readiness", ready))));
            result["GenerateSolutionFiles"] = generated;
            result["GenerateSolutionFile"] = generated;
            var reload = Obj(("completed", B()), ("waitSucceeded", Nullable(B())),
                ("waitResult", AnyOf(Type("null"), Success(waitData), Error(waitError))),
                ("modifiedAssetPaths", Strings()), ("refreshed", Const(true)), ("forced", B()),
                ("reopenedScenes", B()), ("reopenedPrefabStage", B()), ("activeScenePath", S()),
                ("prefabStagePath", S()), ("savedScenes", savedScenes), ("warnings", Strings()), ("readiness", ready));
            result["ReloadCheckpoint"] = Outcome(Success(reload), Error(reload),
                Error(Obj(("modifiedAssetPaths", Strings()), ("blockedDirtyScenes", Strings()))));
            result["GetTags"] = Outcome(Success(Strings()));
            result["GetLayers"] = Outcome(Success(Map(S())));
            return result;
        }

        internal static JObject Readiness() => Obj(("isReady", B()), ("isCompiling", B()),
            ("isUpdating", B()), ("isPlaying", B()), ("isPaused", B()),
            ("isPlayingOrWillChangePlaymode", B()), ("requireNotPlaying", B()),
            ("elapsedMs", I()), ("timeSinceStartup", Number()));

        internal static JObject PlayState() => Obj(("isPlaying", B()), ("isPaused", B()),
            ("isPlayingOrWillChangePlaymode", B()), ("canRequestScriptCompilation", B()));

        static JObject PlayBoundary() => Obj(("status", Enum("play_mode_transition", "edit_mode_transition")),
            ("targetPlaying", B()), ("reloadBoundary", Const(true)), ("reconnectRequired", Const(true)),
            ("reloadSafe", Const(true)), ("changedProject", Const(false)), ("completed", Const(false)),
            ("waitSucceeded", Const(false)), ("requestId", S()), ("queuedAtUtc", S()), ("boundaryReason", S()),
            ("waitedMs", I()), ("state", PlayState()), ("readiness", Readiness()), ("nextSuggestedCalls", Strings()));

        internal static JObject CompileHealth() => Obj(("healthy", B()), ("hasBuildSystemIssues", B()),
            ("staleAssemblyLikely", B()), ("note", S()));

        internal static JObject BuildSystemHealth()
        {
            var group = Obj(("fingerprint", S()), ("type", S()), ("count", I()),
                ("representativeMessage", S()), ("file", S()), ("line", I()),
                ("firstEntryId", I()), ("lastEntryId", I()), ("sampleStackTrace", S()));
            return AnyOf(Obj(("hasCriticalIssues", B()), ("criticalIssueCount", I()),
                    ("criticalIssues", Arr(group)), ("fingerprints", Strings()), ("checkedSignals", Strings())),
                Obj(("hasCriticalIssues", Const(false)), ("criticalIssueCount", Const(0)),
                    ("criticalIssues", Arr(group)), ("warning", S())));
        }

        internal static JObject AssemblyFreshness()
        {
            var assembly = Obj(("name", S()), ("outputPath", S()), ("outputExists", B()),
                ("outputLastWriteTimeUtc", S()), ("newestSourcePath", S()), ("newestSourceLastWriteTimeUtc", S()),
                ("sourceFileCount", I()), ("existingSourceFileCount", I()), ("missingOutput", B()),
                ("staleLikely", B()), ("flags", S()), ("sourceScope", Nullable(Map(I()))));
            var summary = Obj(("assemblyCount", I()), ("assembliesWithSources", I()),
                ("assembliesWithExistingSources", I()), ("staleAssemblyCount", I()), ("missingOutputAssemblyCount", I()),
                ("staleLikely", B()), ("newestSourcePath", S()), ("newestSourceLastWriteTimeUtc", S()),
                ("sourceScopeTotals", Map(I())));
            var v2 = Obj(("mode", Const("CompilationPipeline.GetAssemblies")), ("summary", summary),
                ("staleAssemblies", Arr(assembly)), ("newestSourceAssemblies", Arr(assembly)), ("note", S()));
            return AnyOf(Obj(("available", Const(false)), ("reason", S())),
                Obj(("available", Const(true)), ("version", Const(2)), ("assemblyPath", S()), ("assemblyExists", B()),
                    ("assemblyLastWriteTimeUtc", S()), ("latestAssetScriptPath", S()), ("latestAssetScriptLastWriteTimeUtc", S()),
                    ("assemblyCSharpStaleLikely", B()), ("staleLikely", B()), ("staleAssemblyCount", I()),
                    ("missingOutputAssemblyCount", I()), ("v2", v2), ("note", S())));
        }

        internal static JObject CompilationDiagnostics(bool includeBuildEvidence)
        {
            var diagnostic = Obj(("assemblyPath", S()), ("type", S()), ("message", S()), ("file", S()),
                ("line", I()), ("column", I()), ("utc", S()));
            var diagnostics = Obj(("isCompiling", B()), ("currentContext", S()), ("totalRetained", I()),
                ("returned", I()), ("errors", I()), ("warnings", I()), ("messages", Arr(diagnostic)));
            // EditorEventHistory.Record is a public extension seam taking arbitrary
            // event data. Only that event payload admits the complete JSON value domain.
            var eventRecord = Obj(("id", I()), ("kind", S()), ("message", S()), ("timeSinceStartup", Number()),
                ("utc", S()), ("data", JsonValue()));
            var fields = new List<(string Name, JObject Schema)> {
                ("latestId", I()), ("sinceId", I()), ("count", I()), ("truncated", B()),
                ("events", Arr(eventRecord)), ("selection", Type("null")),
                ("diagnostics", diagnostics), ("assetChanges", Type("null")) };
            if (includeBuildEvidence)
            {
                fields.Add(("buildSystemHealth", BuildSystemHealth()));
                fields.Add(("assemblyFreshness", AssemblyFreshness()));
                fields.Add(("compileHealth", CompileHealth()));
            }
            else fields.Add(("diagnosticScope", Const("retained_compilation_events")));
            return Obj(fields.ToArray());
        }
    }
}
