#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class BatchActions
    {
        sealed class BatchOptions
        {
            public string Name;
            public bool DryRun;
            public bool ValidateBeforeExecute;
            public bool StopOnError;
            public bool UseUndoGroup;
            public bool RollbackOnFailure;
            public bool RollbackAssets;
            public bool IncludeImpact;
            public bool IncludeWorkSessionReview;
            public int WorkSessionReviewMaxChanged;

            public static BatchOptions From(JObject raw)
            {
                var name = GetString(raw, "Name", "name", "BatchName", "batchName");
                var dryRun = GetBool(raw, true, "DryRun", "dryRun", "dry_run", "ValidateOnly", "validateOnly", "validate_only");
                return new BatchOptions
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "UniBridge Batch Actions" : name.Trim(),
                    DryRun = dryRun,
                    ValidateBeforeExecute = GetBool(raw, true, "ValidateBeforeExecute", "validateBeforeExecute", "validate_before_execute"),
                    StopOnError = GetBool(raw, true, "StopOnError", "stopOnError", "stop_on_error"),
                    UseUndoGroup = GetBool(raw, true, "UseUndoGroup", "useUndoGroup", "use_undo_group"),
                    RollbackOnFailure = GetBool(raw, true, "RollbackOnFailure", "rollbackOnFailure", "rollback_on_failure", "Transaction", "transaction", "Transactional", "transactional"),
                    RollbackAssets = GetBool(raw, true, "RollbackAssets", "rollbackAssets", "rollback_assets", "AssetRollback", "assetRollback", "asset_rollback"),
                    IncludeImpact = GetBool(raw, true, "IncludeImpact", "includeImpact", "include_impact", "Impact", "impact", "Plan", "plan"),
                    IncludeWorkSessionReview = GetBool(raw, !dryRun, "IncludeWorkSessionReview", "includeWorkSessionReview", "include_work_session_review", "WorkSessionReview", "workSessionReview", "AutoReview", "autoReview"),
                    WorkSessionReviewMaxChanged = Math.Max(1, GetInt(raw, 20, "WorkSessionReviewMaxChanged", "workSessionReviewMaxChanged", "work_session_review_max_changed"))
                };
            }
        }

        static int GetInt(JObject obj, int defaultValue, params string[] names)
        {
            var raw = GetString(obj, names);
            return int.TryParse(raw, out var value) ? value : defaultValue;
        }

        sealed class BatchStep
        {
            public int Index;
            public string Id;
            public string Description;
            public string ToolName;
            public JObject Parameters;
            public bool Optional;
            public bool Skip;
        }

        sealed class BatchSummary
        {
            public int Total;
            public int Validated;
            public int Executed;
            public int Succeeded;
            public int Failed;
            public int Skipped;
            public int ValidationErrors;
            public int ValidationWarnings;
        }

        sealed class ProcessedStep
        {
            public bool Success;
            public bool Skipped;
            public bool Executed;
            public int ValidationErrors;
            public int ValidationWarnings;
            public bool ShouldStop;
            public string StopReason;
            public object Report;
        }

        sealed class ValidationReport
        {
            public readonly List<string> Errors = new();
            public readonly List<string> Warnings = new();
            public readonly List<string> InfoMessages = new();

            public void Error(string message) => Errors.Add(message);
            public void Warning(string message) => Warnings.Add(message);
            public void Info(string message) => InfoMessages.Add(message);

            public object ToObject() => new
            {
                ok = Errors.Count == 0,
                errors = Errors,
                warnings = Warnings,
                info = InfoMessages
            };
        }

        sealed class ParseResult
        {
            public bool Success;
            public string Error;
            public List<BatchStep> Steps;

            public static ParseResult Ok(List<BatchStep> steps) => new()
            {
                Success = true,
                Steps = steps
            };

            public static ParseResult Fail(string error) => new()
            {
                Success = false,
                Error = error,
                Steps = new List<BatchStep>()
            };
        }
    }
}
