#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Runs a bounded list of UniBridge actions with validation and dry-run support.
    /// </summary>
    public static partial class BatchActions
    {
        const string ToolName = "UniBridge_BatchActions";
        const int MaxSteps = 50;

        public const string Title = "Run validated batch actions";

        public const string Description = @"Validate and optionally execute a bounded list of UniBridge actions.

Use this when an agent needs to perform several related Unity operations as one planned workflow, for example searching assets, configuring importers/materials/ScriptableObjects, rendering asset previews, creating scene objects, adding components, creating folders/assets, instantiating prefabs, creating Animator Controller states/transitions/BlendTrees, saving a scene, and then capturing or snapshotting the result.

    Safety model:
    DryRun defaults to true. In dry-run mode UniBridge validates each step and reports what would run without changing the project.
    When DryRun is false, UniBridge validates first, executes steps in order, and stops on the first required failure by default.
    RollbackOnFailure defaults to true for executing batches. UniBridge records a Unity Undo group plus a bounded snapshot of referenced asset files, then rolls back if the batch fails.
    Only a curated allow-list of local UniBridge tools can be called from the batch.
    Script text editing tools are intentionally excluded from this batch layer; use their dedicated SHA/precondition workflows directly. Read-only script validation is supported through UniBridge_ValidateScript.
    Reload-safe editor boundaries such as queued Play Mode entry/exit stop the batch successfully and return postReconnect.nextSuggestedCalls; run those follow-up calls after reconnect before continuing.

Args:
    DryRun: true by default. Set false to execute.
    ValidateBeforeExecute: true by default.
    StopOnError: true by default.
    UseUndoGroup: true by default for executing batches.
    RollbackOnFailure: true by default for executing batches.
    RollbackAssets: true by default. Captures/restores referenced Assets/Packages files and deletes newly-created referenced paths on rollback.
    IncludeImpact: true by default. Adds a planned impact block with likely assets/scenes/settings touched.
    IncludeWorkSessionReview: defaults to true for executing batches and false for dry-runs. If a UniBridge_WorkSession is active, appends changed-file review data.
    WorkSessionReviewMaxChanged: maximum changed files to include in the appended WorkSession review.
    IncludeConsoleDelta: false by default. Creates a console marker before the batch and appends a compact DiagnosticSummary for entries emitted during the batch.
    IncludeEditorEventDelta: false by default. Captures the editor event latestId before the batch and appends bounded editor events emitted during the batch.
    Name: Optional human-readable batch name.
    Steps/actions: array of steps.

Step shape:
    {
      ""id"": ""optional-step-id"",
      ""tool"": ""game_object | asset | asset_importer | material | scriptable_object | asset_capture | asset_intelligence | script_intelligence | validate_script | scene | prefab | animator_controller | ui | editor | shader | capture | context | console | exact UniBridge tool name"",
      ""description"": ""optional human note"",
      ""optional"": false,
      ""parameters"": { ... target tool parameters ... }
    }

Convenience form:
    { ""tool"": ""game_object"", ""Action"": ""Create"", ""Name"": ""Probe"", ""ComponentsToAdd"": [""Rigidbody2D""] }

Returns:
    success, message, and data with batch summary, validation findings, per-step dry-run/execution reports, and stop reason.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    DryRun = new { type = "boolean", description = "Validate and report without executing changes. Defaults to true.", @default = true },
                    ValidateBeforeExecute = new { type = "boolean", description = "Run validation before executing each step. Defaults to true.", @default = true },
                    StopOnError = new { type = "boolean", description = "Stop after the first required step validation or execution failure. Defaults to true.", @default = true },
                    UseUndoGroup = new { type = "boolean", description = "Wrap executing steps in a Unity Undo group where possible. Defaults to true.", @default = true },
                    RollbackOnFailure = new { type = "boolean", description = "Rollback the whole executing batch when a required step fails. Defaults to true.", @default = true },
                    RollbackAssets = new { type = "boolean", description = "When rollback is enabled, snapshot referenced Assets/Packages files before execution and restore/delete them on failure. Defaults to true.", @default = true },
                    IncludeImpact = new { type = "boolean", description = "Return a planned impact block with likely asset, scene, and project-setting touches. Defaults to true.", @default = true },
                    IncludeWorkSessionReview = new { type = "boolean", description = "Append active UniBridge_WorkSession review data after the batch. Defaults to true for executing batches and false for dry-runs." },
                    WorkSessionReviewMaxChanged = new { type = "integer", description = "Maximum changed files to include in appended WorkSession review.", @default = 20 },
                    IncludeConsoleDelta = new { type = "boolean", description = "Create a console marker before the batch and append compact DiagnosticSummary entries emitted during this batch. Defaults to false.", @default = false },
                    ConsoleDeltaMarkerLabel = new { type = "string", description = "Optional label for the automatic console marker used by IncludeConsoleDelta." },
                    ConsoleDeltaMaxIssues = new { type = "integer", description = "Maximum critical/warning/spam groups returned in the batch console delta.", @default = 5 },
                    ConsoleDeltaMaxSamples = new { type = "integer", description = "Maximum representative entries returned in the batch console delta.", @default = 5 },
                    IncludeEditorEventDelta = new { type = "boolean", description = "Append a bounded UniBridge_EditorEvents delta captured during this batch. Defaults to false.", @default = false },
                    EditorEventDeltaLimit = new { type = "integer", description = "Maximum editor events returned when IncludeEditorEventDelta is true.", @default = 25 },
                    Name = new { type = "string", description = "Optional batch name used in reports and the Undo group." },
                    Steps = new
                    {
                        type = "array",
                        description = "Batch steps. Each step chooses an allowed UniBridge tool and parameters.",
                        maxItems = MaxSteps,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                id = new { type = "string", description = "Optional stable step id." },
                                tool = new { type = "string", description = "Tool alias or exact UniBridge tool name." },
                                description = new { type = "string", description = "Optional human-readable step description." },
                                optional = new { type = "boolean", description = "If true, a failed step is reported but does not fail the whole batch." },
                                parameters = new { type = "object", description = "Parameters passed to the target tool.", additionalProperties = true }
                            },
                            required = new[] { "tool" },
                            additionalProperties = true
                        }
                    }
                },
                required = new[] { "Steps" }
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "editor", "scene", "assets" }, EnabledByDefault = true)]
        public static async Task<object> HandleCommand(JObject rawParameters)
        {
            rawParameters ??= new JObject();

            var options = BatchOptions.From(rawParameters);
            var parseResult = ParseSteps(rawParameters);
            if (!parseResult.Success)
            {
                return Response.Error(parseResult.Error);
            }

            var steps = parseResult.Steps;
            if (steps.Count == 0)
            {
                return Response.Error("Batch requires at least one step.");
            }

            if (steps.Count > MaxSteps)
            {
                return Response.Error($"Batch contains {steps.Count} steps, but the limit is {MaxSteps}.");
            }

            var toolSettings = McpToolRegistry.GetAllToolsForSettings()
                .ToDictionary(entry => entry.Info.name, entry => entry, StringComparer.Ordinal);
            var reports = new List<object>();
            var summary = new BatchSummary { Total = steps.Count };
            var stopReason = (string)null;
            var impact = options.IncludeImpact ? BatchTransaction.BuildImpact(options, steps) : null;
            var transaction = BatchTransaction.Begin(options, steps);
            var observationStart = BeginPostActionObservation(options);

            foreach (var step in steps)
            {
                var report = await ProcessStep(step, options, toolSettings);
                reports.Add(report.Report);
                summary.Validated++;

                if (report.Skipped)
                {
                    summary.Skipped++;
                }

                if (report.Executed)
                {
                    summary.Executed++;
                }

                if (report.ValidationErrors > 0)
                {
                    summary.ValidationErrors += report.ValidationErrors;
                }

                if (report.ValidationWarnings > 0)
                {
                    summary.ValidationWarnings += report.ValidationWarnings;
                }

                if (report.Success)
                {
                    summary.Succeeded++;
                }
                else
                {
                    summary.Failed++;
                }

                if (report.ShouldStop)
                {
                    stopReason = report.StopReason;
                    break;
                }
            }

            var batchSuccess = summary.Failed == 0 && summary.ValidationErrors == 0;
            if (options.DryRun)
            {
                batchSuccess = summary.ValidationErrors == 0;
            }

            var rollbackTriggered = !options.DryRun && !batchSuccess && options.RollbackOnFailure;
            var rollback = transaction.Finish(batchSuccess, stopReason ?? "Batch did not complete successfully.");

            var message = options.DryRun
                ? batchSuccess ? "Batch dry-run validation passed." : "Batch dry-run found validation issues."
                : batchSuccess && IsReloadBoundaryStop(stopReason) ? "Batch stopped at a reload-safe editor boundary."
                : batchSuccess ? "Batch executed successfully." : rollbackTriggered ? "Batch failed and transaction rollback was attempted." : "Batch finished with failures.";

            var workSessionReview = options.IncludeWorkSessionReview
                ? WorkSession.BuildCompactActiveReview(options.WorkSessionReviewMaxChanged, includeChangedFiles: true)
                : null;
            var postActionDiagnostics = BuildPostActionDiagnostics(options, observationStart);

            var data = new
            {
                name = options.Name,
                dryRun = options.DryRun,
                validateBeforeExecute = options.ValidateBeforeExecute,
                stopOnError = options.StopOnError,
                useUndoGroup = options.UseUndoGroup,
                rollbackOnFailure = options.RollbackOnFailure,
                rollbackAssets = options.RollbackAssets,
                impact,
                rollback,
                workSessionReview,
                postActionDiagnostics,
                summary,
                stopReason,
                steps = reports,
                allowedTools = BatchActionToolCatalog.AllowedTools.OrderBy(tool => tool).ToArray()
            };

            return batchSuccess
                ? Response.Success(message, data)
                : Response.Error(message, data);
        }

        static async Task<ProcessedStep> ProcessStep(
            BatchStep step,
            BatchOptions options,
            Dictionary<string, ToolSettingsEntry> toolSettings)
        {
            var stopwatch = Stopwatch.StartNew();
            var validation = ValidateStep(step, toolSettings);
            var validationObject = validation.ToObject();
            var validationHasErrors = validation.Errors.Count > 0;
            var shouldSkipForValidation = options.ValidateBeforeExecute && validationHasErrors;

            if (options.DryRun || shouldSkipForValidation || step.Skip)
            {
                stopwatch.Stop();
                var success = step.Optional || !validationHasErrors;
                var skipped = !options.DryRun && (shouldSkipForValidation || step.Skip);
                var stop = !success && options.StopOnError;
                return new ProcessedStep
                {
                    Success = success,
                    Skipped = skipped,
                    Executed = false,
                    ValidationErrors = validation.Errors.Count,
                    ValidationWarnings = validation.Warnings.Count,
                    ShouldStop = stop,
                    StopReason = stop ? $"Step {step.Index} validation failed." : null,
                    Report = new
                    {
                        index = step.Index,
                        id = step.Id,
                        description = step.Description,
                        tool = step.ToolName,
                        optional = step.Optional,
                        dryRun = options.DryRun,
                        wouldExecute = !validationHasErrors || step.Optional,
                        skipped,
                        executed = false,
                        success,
                        validation = validationObject,
                        parameters = step.Parameters,
                        durationMs = stopwatch.ElapsedMilliseconds
                    }
                };
            }

            try
            {
                var executionWarnings = new List<string>();
                var executionParameters = PrepareExecutionParameters(step, options, executionWarnings);
                var result = await McpToolRegistry.ExecuteToolInsideCurrentLeaseAsync(step.ToolName, executionParameters);
                stopwatch.Stop();

                var resultJson = ToJObjectSafe(result);
                if (!options.DryRun && ContainsDryRunTrue(resultJson))
                {
                    executionWarnings.Add("Nested tool reported dryRun=true while the batch was executing with DryRun=false. Review this step before trusting it as a mutation.");
                }

                var toolSuccess = resultJson.Value<bool?>("success") ?? true;
                var message = resultJson.Value<string>("message") ?? resultJson.Value<string>("error");
                var reloadBoundary = toolSuccess && IsReloadBoundaryResult(resultJson);
                if (reloadBoundary)
                {
                    executionWarnings.Add("Nested tool requested a reload-safe boundary. BatchActions stopped here so later steps do not run against a stale pre-reload editor state.");
                }

                var stop = reloadBoundary || (!toolSuccess && !step.Optional && options.StopOnError);

                return new ProcessedStep
                {
                    Success = toolSuccess || step.Optional,
                    Executed = true,
                    ValidationErrors = validation.Errors.Count,
                    ValidationWarnings = validation.Warnings.Count,
                    ShouldStop = stop,
                    StopReason = reloadBoundary
                        ? $"Step {step.Index} requested a reload-safe editor boundary. Run the suggested post-reconnect calls before continuing the remaining workflow."
                        : stop ? $"Step {step.Index} execution failed." : null,
                    Report = new
                    {
                        index = step.Index,
                        id = step.Id,
                        description = step.Description,
                        tool = step.ToolName,
                        optional = step.Optional,
                        dryRun = false,
                        skipped = false,
                        executed = true,
                        success = toolSuccess,
                        validation = validationObject,
                        executionWarnings,
                        message,
                        reloadBoundary,
                        postReconnect = reloadBoundary ? ExtractPostReconnectHints(resultJson) : null,
                        result = resultJson,
                        durationMs = stopwatch.ElapsedMilliseconds
                    }
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var stop = !step.Optional && options.StopOnError;
                return new ProcessedStep
                {
                    Success = step.Optional,
                    Executed = true,
                    ValidationErrors = validation.Errors.Count,
                    ValidationWarnings = validation.Warnings.Count,
                    ShouldStop = stop,
                    StopReason = stop ? $"Step {step.Index} threw an exception." : null,
                    Report = new
                    {
                        index = step.Index,
                        id = step.Id,
                        description = step.Description,
                        tool = step.ToolName,
                        optional = step.Optional,
                        dryRun = false,
                        skipped = false,
                        executed = true,
                        success = false,
                        validation = validationObject,
                        executionWarnings = Array.Empty<string>(),
                        error = ex.Message,
                        durationMs = stopwatch.ElapsedMilliseconds
                    }
                };
            }
        }

        static JObject PrepareExecutionParameters(BatchStep step, BatchOptions options, List<string> warnings)
        {
            var parameters = step.Parameters == null ? new JObject() : (JObject)step.Parameters.DeepClone();
            if (options?.DryRun != false || !SupportsNestedDryRun(step.ToolName))
            {
                return parameters;
            }

            if (!HasDryRunParameter(parameters))
            {
                parameters["DryRun"] = false;
                warnings?.Add($"Batch DryRun=false propagated DryRun=false to nested tool '{step.ToolName}'.");
            }

            return parameters;
        }

        static BatchObservationStart BeginPostActionObservation(BatchOptions options)
        {
            var start = new BatchObservationStart();
            if (options == null)
            {
                return start;
            }

            if (options.IncludeEditorEventDelta)
            {
                try
                {
                    start.EditorEventSinceId = EditorEventHistory.LatestId();
                }
                catch (Exception ex)
                {
                    start.Warnings.Add($"Failed to capture editor event start id: {ex.Message}");
                }
            }

            if (options.IncludeConsoleDelta)
            {
                try
                {
                    var markerLabel = string.IsNullOrWhiteSpace(options.ConsoleDeltaMarkerLabel)
                        ? $"{options.Name} {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}"
                        : options.ConsoleDeltaMarkerLabel;

                    var markerResult = ToJObjectSafe(ReadConsole.HandleCommand(new ReadConsoleParams
                    {
                        Action = ConsoleAction.MarkSession,
                        MarkerLabel = markerLabel,
                        IncludeMarker = false
                    }));

                    start.ConsoleMarkerResult = markerResult;
                    start.ConsoleMarkerId = (markerResult["data"] as JObject)?.Value<string>("markerId");
                    var success = markerResult.Value<bool?>("success") ?? false;
                    if (!success || string.IsNullOrWhiteSpace(start.ConsoleMarkerId))
                    {
                        start.Warnings.Add("Console delta marker could not be created; console delta will report marker creation details only.");
                    }
                }
                catch (Exception ex)
                {
                    start.Warnings.Add($"Failed to create console delta marker: {ex.Message}");
                }
            }

            return start;
        }

        static object BuildPostActionDiagnostics(BatchOptions options, BatchObservationStart start)
        {
            if (options == null ||
                (!options.IncludeConsoleDelta && !options.IncludeEditorEventDelta))
            {
                return null;
            }

            start ??= new BatchObservationStart();

            return new
            {
                enabled = true,
                consoleDelta = options.IncludeConsoleDelta ? BuildConsoleDelta(options, start) : null,
                editorEventDelta = options.IncludeEditorEventDelta ? BuildEditorEventDelta(options, start) : null,
                warnings = start.Warnings.ToArray(),
                note = "This self-check is opt-in and observes logs/events emitted between the batch start marker and the final response."
            };
        }

        static object BuildConsoleDelta(BatchOptions options, BatchObservationStart start)
        {
            if (string.IsNullOrWhiteSpace(start.ConsoleMarkerId))
            {
                return new
                {
                    enabled = true,
                    markerCreated = false,
                    markerResult = start.ConsoleMarkerResult,
                    guidance = "Create a marker with UniBridge_ReadConsole Action=MarkSession before the workflow, then read DiagnosticSummary with AfterMarkerId."
                };
            }

            try
            {
                var summaryResult = ToJObjectSafe(ReadConsole.HandleCommand(new ReadConsoleParams
                {
                    Action = ConsoleAction.DiagnosticSummary,
                    AfterMarkerId = start.ConsoleMarkerId,
                    IncludeMarker = false,
                    IncludeStacktrace = false,
                    MaxIssues = options.ConsoleDeltaMaxIssues,
                    MaxSamples = options.ConsoleDeltaMaxSamples,
                    MaxEvents = options.ConsoleDeltaMaxSamples
                }));

                var data = summaryResult["data"] as JObject;
                var summary = data?["summary"] as JObject;
                var compactSummary = summary == null
                    ? null
                    : new JObject
                    {
                        ["dominantIssue"] = summary["dominantIssue"]?.DeepClone(),
                        ["criticalIssues"] = summary["criticalIssues"]?.DeepClone() ?? new JArray(),
                        ["warningIssues"] = summary["warningIssues"]?.DeepClone() ?? new JArray(),
                        ["likelySpam"] = summary["likelySpam"]?.DeepClone() ?? new JArray(),
                        ["recentSamples"] = summary["recentSamples"]?.DeepClone() ?? new JArray()
                    };

                return new
                {
                    enabled = true,
                    markerCreated = true,
                    markerId = start.ConsoleMarkerId,
                    success = summaryResult.Value<bool?>("success") ?? false,
                    totals = data?["totals"]?.DeepClone(),
                    summary = compactSummary,
                    marker = data?["marker"]?.DeepClone(),
                    omitted = new[] { "timelineHighlights" },
                    guidance = "If this delta has new errors or warnings, inspect the returned fingerprint with UniBridge_ReadConsole Action=GroupDetails AfterMarkerId=<markerId>."
                };
            }
            catch (Exception ex)
            {
                start.Warnings.Add($"Failed to build console delta: {ex.Message}");
                return new
                {
                    enabled = true,
                    markerCreated = true,
                    markerId = start.ConsoleMarkerId,
                    success = false,
                    error = ex.Message
                };
            }
        }

        static object BuildEditorEventDelta(BatchOptions options, BatchObservationStart start)
        {
            try
            {
                return new
                {
                    enabled = true,
                    sinceId = start.EditorEventSinceId,
                    result = EditorEventHistory.Snapshot(
                        start.EditorEventSinceId,
                        options.EditorEventDeltaLimit,
                        includeSelection: false,
                        includeDiagnostics: true,
                        includeAssetChanges: true)
                };
            }
            catch (Exception ex)
            {
                start.Warnings.Add($"Failed to build editor event delta: {ex.Message}");
                return new
                {
                    enabled = true,
                    sinceId = start.EditorEventSinceId,
                    success = false,
                    error = ex.Message
                };
            }
        }

        static bool IsReloadBoundaryStop(string stopReason) =>
            !string.IsNullOrWhiteSpace(stopReason) &&
            stopReason.IndexOf("reload-safe editor boundary", StringComparison.OrdinalIgnoreCase) >= 0;

        static bool HasDryRunParameter(JObject parameters)
        {
            return parameters != null &&
                   (parameters.TryGetValue("DryRun", StringComparison.OrdinalIgnoreCase, out _) ||
                    parameters.TryGetValue("dryRun", StringComparison.OrdinalIgnoreCase, out _) ||
                    parameters.TryGetValue("dry_run", StringComparison.OrdinalIgnoreCase, out _));
        }

        static bool SupportsNestedDryRun(string toolName)
        {
            return toolName is
                "UniBridge_EditorSnapshot" or
                "UniBridge_ManageAnimatorController" or
                "UniBridge_ManageAnimationClip" or
                "UniBridge_ManageAsset" or
                "UniBridge_ManageAssetImporter" or
                "UniBridge_ManageAudio" or
                "UniBridge_ManageConstraints" or
                "UniBridge_ManageInputActions" or
                "UniBridge_ManageMaterial" or
                "UniBridge_ManageNavigation" or
                "UniBridge_ManagePhysics2D" or
                "UniBridge_ManagePhysics3D" or
                "UniBridge_ManagePrefab" or
                "UniBridge_ManageRendering" or
                "UniBridge_ManageSceneHierarchy" or
                "UniBridge_ManageScriptableObject" or
                "UniBridge_ManageShader" or
                "UniBridge_ManageTilemap2D" or
                "UniBridge_ManageTimeline" or
                "UniBridge_ManageUI" or
                "UniBridge_ManageUIToolkit" or
                "UniBridge_ManageUnityEvent" or
                "UniBridge_ManageVFX" or
                "UniBridge_ScopedEdit" or
                "UniBridge_WorkflowRecipes";
        }

        static bool ContainsDryRunTrue(JToken token)
        {
            if (token == null)
            {
                return false;
            }

            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    var key = property.Name.Replace("_", string.Empty).ToLowerInvariant();
                    if (key == "dryrun" && TryReadBool(property.Value, out var dryRun) && dryRun)
                    {
                        return true;
                    }

                    if (ContainsDryRunTrue(property.Value))
                    {
                        return true;
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var child in array)
                {
                    if (ContainsDryRunTrue(child))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        static bool IsReloadBoundaryResult(JObject result)
        {
            if (result?["data"] is not JObject data)
            {
                return false;
            }

            return TryReadBool(data["batchBoundary"], out var batchBoundary) && batchBoundary;
        }

        static object ExtractPostReconnectHints(JObject result)
        {
            var data = result?["data"] as JObject;
            if (data == null)
            {
                return null;
            }

            return new
            {
                requestId = data.Value<string>("requestId"),
                status = data.Value<string>("status"),
                reconnectRequired = data.Value<bool?>("reconnectRequired"),
                reloadSafe = data.Value<bool?>("reloadSafe"),
                targetPlaying = data.Value<bool?>("targetPlaying"),
                nextSuggestedCalls = data["nextSuggestedCalls"]?.DeepClone(),
                reason = data.Value<string>("reason")
            };
        }

        static bool TryReadBool(JToken token, out bool value)
        {
            value = false;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }

            return bool.TryParse(token.ToString(), out value);
        }

    }
}
