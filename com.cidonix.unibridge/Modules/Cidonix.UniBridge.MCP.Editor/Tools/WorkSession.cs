#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Project work-session checkpoint/review/revert safety layer for AI agents.
    /// </summary>
    public static partial class WorkSession
    {
        const string ToolName = "UniBridge_WorkSession";
        const int DefaultMaxFiles = 12000;
        const int DefaultMaxChanged = 200;
        const int DefaultMaxSemanticObjects = 30000;
        const int DefaultMaxSemanticChanges = 120;
        const int DefaultMaxDiffLines = 220;
        const long DefaultMaxSingleCaptureBytes = 2L * 1024L * 1024L;
        const long DefaultMaxTotalCaptureBytes = 150L * 1024L * 1024L;

        static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".asmdef", ".asset", ".cginc", ".compute", ".controller", ".cs", ".css", ".editorconfig",
            ".hlsl", ".inputactions", ".json", ".mat", ".md", ".meta", ".overridecontroller",
            ".playable", ".prefab", ".shader", ".shadergraph", ".txt", ".unity", ".uss", ".uxml", ".xml", ".yaml", ".yml"
        };

        static readonly HashSet<string> CapturableExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".asmdef", ".asset", ".controller", ".cs", ".inputactions", ".json", ".mat", ".md",
            ".meta", ".overridecontroller", ".playable", ".prefab", ".shader", ".shadergraph",
            ".txt", ".unity", ".uss", ".uxml", ".xml", ".yaml", ".yml"
        };

        static readonly HashSet<string> HighRiskExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".meta", ".unity", ".prefab", ".controller", ".overridecontroller", ".playable"
        };

        public const string Title = "Track and review a UniBridge AI work session";

        public const string Description = @"Create project-local work-session checkpoints so AI agents can review and optionally revert their Unity project changes.

Search aliases: UniBridge WorkSession work session checkpoint review changed files diff revert rollback safety agent changes semantic scene prefab script meta renderer sorting components.

Actions:
    Begin: Capture a file baseline and optional loaded-scene semantic baseline under Library/UniBridge/WorkSessions and make it active.
    Status: Return active session metadata plus current file and scene semantic change summaries.
    Review: Return changed files, semantic scene changes, risk flags, and restore availability.
    Diff: Return compact text diffs for selected changed files.
    Revert: Dry-run or execute selected file reverts from the session snapshot.
    End: Mark the active session complete.
    List: List recent work sessions.

The tool writes only session metadata/snapshots under Library unless Revert is executed with DryRun=false. It is intended as a safety/review layer above normal typed UniBridge tools.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new
                    {
                        type = "string",
                        @enum = new[] { "Begin", "Status", "Review", "Diff", "Revert", "End", "List" },
                        @default = "Status"
                    },
                    SessionId = new { type = "string", description = "Existing session id. If omitted, the active/latest session is used." },
                    Name = new { type = "string", description = "Human-readable session name for Begin." },
                    Paths = new { type = "array", items = new { type = "string" }, description = "Project-relative paths to diff/revert. Omit for Review; use RevertAll=true for full revert." },
                    RevertAll = new { type = "boolean", description = "For Revert, select every changed file from this session.", @default = false },
                    DryRun = new { type = "boolean", description = "For Revert, preview without touching files.", @default = true },
                    IncludeProjectSettings = new { type = "boolean", @default = true },
                    IncludePackageManifests = new { type = "boolean", @default = true },
                    IncludePackageFiles = new { type = "boolean", description = "Also scan Packages content. Usually false to avoid package-cache noise.", @default = false },
                    MaxFiles = new { type = "integer", @default = DefaultMaxFiles },
                    MaxChanged = new { type = "integer", @default = DefaultMaxChanged },
                    MaxDiffLines = new { type = "integer", @default = DefaultMaxDiffLines },
                    MaxSingleCaptureBytes = new { type = "integer", @default = DefaultMaxSingleCaptureBytes },
                    MaxTotalCaptureBytes = new { type = "integer", @default = DefaultMaxTotalCaptureBytes },
                    IncludeSceneSemantics = new { type = "boolean", description = "For Begin, capture compact loaded-scene semantic baselines so Review can report created/deleted/moved objects, component changes, renderer sorting changes, transforms, and missing scripts.", @default = true },
                    IncludeSemanticReview = new { type = "boolean", description = "For Status/Review, include live loaded-scene semantic change summary when the session has a semantic baseline.", @default = true },
                    MaxSemanticObjects = new { type = "integer", description = "Maximum loaded scene objects captured in the semantic baseline.", @default = DefaultMaxSemanticObjects },
                    MaxSemanticChanges = new { type = "integer", description = "Maximum semantic scene changes returned in Review/Status.", @default = DefaultMaxSemanticChanges },
                    DeleteAddedMetaWithAsset = new { type = "boolean", @default = true },
                    DeleteSessionFiles = new { type = "boolean", description = "For End, remove Library session files after marking ended.", @default = false }
                },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "safety", "diagnostics", "editor" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = Normalize(GetString(parameters, "Action", "action") ?? "Status");

            try
            {
                return action switch
                {
                    "begin" or "start" or "checkpoint" => Begin(parameters),
                    "review" or "changes" or "changedfiles" => Review(parameters),
                    "diff" => Diff(parameters),
                    "revert" or "rollback" => Revert(parameters),
                    "end" or "finish" or "close" => End(parameters),
                    "list" or "sessions" => List(parameters),
                    _ => Status(parameters)
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WorkSession] Action '{action}' failed: {ex}");
                return Response.Error($"WorkSession action '{action}' failed: {ex.Message}");
            }
        }

        static object Begin(JObject parameters)
        {
            EnsureSessionRoot();

            var options = ScanOptions.From(parameters);
            var sessionId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + ShortHash(Guid.NewGuid().ToString("N"));
            var sessionDir = GetSessionDir(sessionId);
            Directory.CreateDirectory(sessionDir);
            Directory.CreateDirectory(GetCaptureDir(sessionId));

            var capture = new CaptureBudget(options.MaxSingleCaptureBytes, options.MaxTotalCaptureBytes);
            var scan = ScanProject(options, capture, sessionId);
            var semanticBaseline = CaptureSemanticBaseline(options, sessionId, out var semanticWarnings);
            var state = new SessionState
            {
                Version = 1,
                SessionId = sessionId,
                Name = GetString(parameters, "Name", "name") ?? "UniBridge work session",
                ProjectRoot = ProjectRoot,
                UnityVersion = Application.unityVersion,
                StartedUtc = DateTime.UtcNow.ToString("o"),
                Options = options,
                Files = scan.Files,
                Baseline = new SessionBaseline
                {
                    FileCount = scan.Files.Count,
                    CapturedFiles = capture.CapturedFiles,
                    CapturedBytes = capture.CapturedBytes,
                    CaptureTruncated = capture.Truncated,
                    Warnings = scan.Warnings.Concat(capture.Warnings).Concat(semanticWarnings).Distinct().ToList()
                },
                SemanticBaseline = semanticBaseline
            };

            SaveState(state);
            File.WriteAllText(GetActiveSessionPath(), sessionId);

            return Response.Success("Started UniBridge work session.", new
            {
                action = "Begin",
                session = ToSessionSummary(state),
                baseline = state.Baseline,
                semanticBaseline = state.SemanticBaseline,
                storage = new
                {
                    sessionDir = ToProjectDisplayPath(sessionDir),
                    sessionFile = ToProjectDisplayPath(GetSessionFile(sessionId)),
                    note = "Snapshots are stored under Library and are not intended for version control."
                }
            });
        }

        static object Status(JObject parameters)
        {
            var state = LoadRequestedState(parameters, required: false);
            if (state == null)
            {
                return Response.Success("No UniBridge work session is active.", new
                {
                    action = "Status",
                    active = false,
                    sessions = ListSessionSummaries(8)
                });
            }

            var changes = BuildChanges(state, state.Options, DefaultMaxChanged);
            var semanticReview = BuildSemanticReview(
                state,
                GetBool(parameters, true, "IncludeSemanticReview", "includeSemanticReview", "include_semantic_review"),
                GetInt(parameters, DefaultMaxSemanticChanges, "MaxSemanticChanges", "maxSemanticChanges", "semanticLimit", "SemanticLimit"));
            return Response.Success("Built UniBridge work session status.", new
            {
                action = "Status",
                active = string.Equals(GetActiveSessionId(), state.SessionId, StringComparison.OrdinalIgnoreCase),
                session = ToSessionSummary(state),
                baseline = state.Baseline,
                changes = changes.Summary,
                semanticReview,
                storage = ToStorageSummary(state)
            });
        }

        static object Review(JObject parameters)
        {
            var state = LoadRequestedState(parameters, required: true);
            var maxChanged = GetInt(parameters, DefaultMaxChanged, "MaxChanged", "maxChanged", "limit", "Limit");
            var changes = BuildChanges(state, state.Options, maxChanged);
            var semanticReview = BuildSemanticReview(
                state,
                GetBool(parameters, true, "IncludeSemanticReview", "includeSemanticReview", "include_semantic_review"),
                GetInt(parameters, DefaultMaxSemanticChanges, "MaxSemanticChanges", "maxSemanticChanges", "semanticLimit", "SemanticLimit"));

            return Response.Success("Reviewed UniBridge work session changes.", new
            {
                action = "Review",
                session = ToSessionSummary(state),
                summary = changes.Summary,
                semanticReview,
                changedFiles = changes.Items.Select(ToFileChangeDto).ToArray(),
                warnings = changes.Warnings,
                note = changes.TotalChanged > changes.Items.Length
                    ? $"Only the first {changes.Items.Length} of {changes.TotalChanged} changed files are returned. Increase MaxChanged or pass Paths to Diff/Revert."
                    : null
            });
        }

        public static object BuildCompactActiveReview(
            int maxChanged = 20,
            bool includeChangedFiles = true,
            bool includeSemanticReview = true,
            int maxSemanticChanges = 10)
        {
            try
            {
                var sessionId = GetActiveSessionId();
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return new
                    {
                        active = false,
                        reviewAvailable = false,
                        message = "No active UniBridge work session.",
                        nextSuggestedCalls = new[]
                        {
                            "UniBridge_WorkSession Action=Begin"
                        }
                    };
                }

                var state = LoadStateById(sessionId);
                var limit = Math.Max(1, maxChanged);
                var semanticLimit = Math.Max(1, Math.Min(maxSemanticChanges, 10));
                var changes = BuildChanges(state, state.Options, limit, computeHashes: false);
                var semanticReview = BuildSemanticReview(state, includeSemanticReview, semanticLimit, lightweight: true);
                return new
                {
                    active = true,
                    reviewAvailable = true,
                    reviewMode = "Compact",
                    bounded = true,
                    fileReviewMode = "MetadataOnly",
                    semanticReviewIncluded = includeSemanticReview,
                    semanticReviewMaxChanges = includeSemanticReview ? semanticLimit : 0,
                    session = ToSessionSummary(state),
                    summary = changes.Summary,
                    semanticReview,
                    changedFiles = includeChangedFiles ? changes.Items.Select(ToFileChangeDto).ToArray() : null,
                    truncated = changes.TotalChanged > changes.Items.Length,
                    warnings = changes.Warnings,
                    nextSuggestedCalls = new[]
                    {
                        $"UniBridge_WorkSession Action=Review SessionId={state.SessionId}",
                        $"UniBridge_WorkSession Action=Diff SessionId={state.SessionId} Paths=[...]"
                    }
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    active = false,
                    reviewAvailable = false,
                    error = ex.Message,
                    message = "Failed to build active UniBridge work-session review."
                };
            }
        }

        static object Diff(JObject parameters)
        {
            var state = LoadRequestedState(parameters, required: true);
            var selected = ReadPaths(parameters);
            if (selected.Length == 0)
            {
                return Response.Error("Diff requires Paths with one or more project-relative files.", new
                {
                    hint = "Call Action=Review first, then pass changed file paths to Action=Diff."
                });
            }

            var maxDiffLines = GetInt(parameters, DefaultMaxDiffLines, "MaxDiffLines", "maxDiffLines");
            var changes = BuildChanges(state, state.Options, DefaultMaxChanged);
            var byPath = changes.All.ToDictionary(change => change.Path, StringComparer.OrdinalIgnoreCase);
            var diffs = selected.Select(path => BuildDiff(state, NormalizeProjectRelativePath(path), byPath, maxDiffLines)).ToArray();

            return Response.Success("Built UniBridge work session diffs.", new
            {
                action = "Diff",
                session = ToSessionSummary(state),
                count = diffs.Length,
                diffs
            });
        }

        static object Revert(JObject parameters)
        {
            var state = LoadRequestedState(parameters, required: true);
            var dryRun = GetBool(parameters, true, "DryRun", "dryRun", "dry_run");
            var revertAll = GetBool(parameters, false, "RevertAll", "revertAll", "revert_all");
            var deleteAddedMetaWithAsset = GetBool(parameters, true, "DeleteAddedMetaWithAsset", "deleteAddedMetaWithAsset", "delete_added_meta_with_asset");
            var selected = ReadPaths(parameters);

            var changes = BuildChanges(state, state.Options, int.MaxValue);
            var targets = SelectRevertTargets(changes.All, selected, revertAll, deleteAddedMetaWithAsset);
            if (targets.Count == 0)
            {
                return Response.Error("Revert found no selected changed files.", new
                {
                    hint = "Pass Paths from Action=Review, or set RevertAll=true.",
                    changedCount = changes.TotalChanged
                });
            }

            var plan = targets.Select(change => BuildRevertPlan(state, change)).ToArray();
            var invalid = plan.Where(item => !item.CanRevert).ToArray();
            if (dryRun || invalid.Length > 0)
            {
                return Response.Success(dryRun
                    ? "Built UniBridge work session revert dry-run."
                    : "Revert was not executed because one or more selected files cannot be restored.", new
                {
                    action = "Revert",
                    dryRun = true,
                    session = ToSessionSummary(state),
                    requested = selected,
                    revertAll,
                    plan = plan.Select(ToRevertPlanDto).ToArray(),
                    canExecute = invalid.Length == 0,
                    invalidCount = invalid.Length,
                    hint = invalid.Length == 0 ? "Repeat with DryRun=false to execute this revert." : "Remove non-restorable paths or revert them manually."
                });
            }

            var results = new List<object>();
            var errors = new List<string>();
            foreach (var change in targets)
            {
                try
                {
                    results.Add(ExecuteRevert(state, change));
                }
                catch (Exception ex)
                {
                    errors.Add($"{change.Path}: {ex.Message}");
                }
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            var after = BuildChanges(state, state.Options, DefaultMaxChanged);

            return Response.Success(errors.Count == 0
                ? "Reverted selected UniBridge work session changes."
                : "Revert completed with errors.", new
            {
                action = "Revert",
                dryRun = false,
                session = ToSessionSummary(state),
                reverted = results,
                errors,
                remainingChanges = after.Summary
            });
        }

        static object End(JObject parameters)
        {
            var state = LoadRequestedState(parameters, required: true);
            state.EndedUtc = DateTime.UtcNow.ToString("o");
            SaveState(state);

            var active = GetActiveSessionId();
            if (string.Equals(active, state.SessionId, StringComparison.OrdinalIgnoreCase) && File.Exists(GetActiveSessionPath()))
            {
                File.Delete(GetActiveSessionPath());
            }

            var deleteFiles = GetBool(parameters, false, "DeleteSessionFiles", "deleteSessionFiles", "delete_session_files");
            var deleted = false;
            if (deleteFiles)
            {
                var dir = GetSessionDir(state.SessionId);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                    deleted = true;
                }
            }

            return Response.Success("Ended UniBridge work session.", new
            {
                action = "End",
                session = ToSessionSummary(state),
                deletedSessionFiles = deleted
            });
        }

        static object List(JObject parameters)
        {
            var limit = GetInt(parameters, 20, "Limit", "limit", "MaxSessions", "maxSessions");
            return Response.Success("Listed UniBridge work sessions.", new
            {
                action = "List",
                activeSessionId = GetActiveSessionId(),
                sessions = ListSessionSummaries(limit)
            });
        }

        static ProjectScan ScanProject(ScanOptions options, CaptureBudget capture, string sessionId, bool computeHashes = true)
        {
            var warnings = new List<string>();
            var files = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
            var roots = BuildScanRoots(options);
            foreach (var root in roots)
            {
                if (!Directory.Exists(root.AbsolutePath))
                {
                    if (File.Exists(root.AbsolutePath))
                    {
                        AddFile(root.AbsolutePath);
                    }
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(root.AbsolutePath, "*", SearchOption.AllDirectories))
                {
                    if (files.Count >= options.MaxFiles)
                    {
                        warnings.Add($"File scan reached MaxFiles={options.MaxFiles}; baseline is truncated.");
                        return new ProjectScan { Files = files, Warnings = warnings };
                    }

                    if (ShouldSkipFile(file))
                        continue;

                    AddFile(file);
                }
            }

            return new ProjectScan { Files = files, Warnings = warnings };

            void AddFile(string absolutePath)
            {
                var relative = ToProjectRelativePath(absolutePath);
                if (string.IsNullOrWhiteSpace(relative) || files.ContainsKey(relative))
                    return;

                var snapshot = BuildSnapshot(relative, absolutePath, capture, sessionId, computeHashes);
                files[relative] = snapshot;
            }
        }

        static List<ScanRoot> BuildScanRoots(ScanOptions options)
        {
            var roots = new List<ScanRoot>
            {
                new ScanRoot("Assets", Path.Combine(ProjectRoot, "Assets"))
            };

            if (options.IncludeProjectSettings)
                roots.Add(new ScanRoot("ProjectSettings", Path.Combine(ProjectRoot, "ProjectSettings")));

            if (options.IncludePackageManifests)
            {
                roots.Add(new ScanRoot("Packages/manifest.json", Path.Combine(ProjectRoot, "Packages", "manifest.json")));
                roots.Add(new ScanRoot("Packages/packages-lock.json", Path.Combine(ProjectRoot, "Packages", "packages-lock.json")));
            }

            if (options.IncludePackageFiles)
                roots.Add(new ScanRoot("Packages", Path.Combine(ProjectRoot, "Packages")));

            return roots;
        }

        static FileSnapshot BuildSnapshot(string relativePath, string absolutePath, CaptureBudget capture, string sessionId, bool computeHash)
        {
            var info = new FileInfo(absolutePath);
            var extension = Path.GetExtension(relativePath);
            var snapshot = new FileSnapshot
            {
                Path = relativePath,
                Exists = true,
                SizeBytes = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc.ToString("o"),
                Sha256 = computeHash ? TryComputeSha256(absolutePath) : null,
                Kind = ClassifyPath(relativePath),
                TextLike = IsTextLike(relativePath)
            };

            if (IsCapturable(relativePath) && capture.TryReserve(info.Length, relativePath))
            {
                var capturePath = GetCapturedFilePath(sessionId, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath));
                File.Copy(absolutePath, capturePath, overwrite: true);
                snapshot.Captured = true;
                snapshot.CapturePath = ToSessionRelativePath(sessionId, capturePath);
            }

            return snapshot;
        }

        static ChangeSet BuildChanges(SessionState state, ScanOptions options, int maxChanged, bool computeHashes = true)
        {
            var current = ScanProject(options ?? state.Options ?? new ScanOptions(), new CaptureBudget(0, 0), state.SessionId, computeHashes);
            var items = new List<FileChange>();
            var allPaths = new HashSet<string>(state.Files.Keys, StringComparer.OrdinalIgnoreCase);
            allPaths.UnionWith(current.Files.Keys);

            foreach (var path in allPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                state.Files.TryGetValue(path, out var before);
                current.Files.TryGetValue(path, out var after);

                var changeType = GetChangeType(before, after);
                if (changeType == "Unchanged")
                    continue;

                items.Add(ToFileChange(state, changeType, before, after, path));
            }

            var totalChanged = items.Count;
            var limited = items.Take(Math.Max(1, maxChanged)).ToArray();
            return new ChangeSet
            {
                All = items,
                Items = limited,
                TotalChanged = totalChanged,
                Warnings = current.Warnings.ToArray(),
                Summary = BuildChangeSummary(items, totalChanged, limited.Length)
            };
        }

        static string GetChangeType(FileSnapshot before, FileSnapshot after)
        {
            if (before == null && after != null)
                return "Added";
            if (before != null && after == null)
                return "Deleted";
            if (before == null)
                return "Unchanged";

            if (!string.IsNullOrWhiteSpace(before.Sha256) &&
                !string.IsNullOrWhiteSpace(after.Sha256) &&
                !string.Equals(before.Sha256, after.Sha256, StringComparison.OrdinalIgnoreCase))
                return "Modified";

            if (!string.IsNullOrWhiteSpace(before.Sha256) &&
                !string.IsNullOrWhiteSpace(after.Sha256) &&
                string.Equals(before.Sha256, after.Sha256, StringComparison.OrdinalIgnoreCase))
                return "Unchanged";

            if (before.SizeBytes != after.SizeBytes ||
                !string.Equals(before.LastWriteUtc, after.LastWriteUtc, StringComparison.Ordinal))
                return "Modified";

            return "Unchanged";
        }

        static FileChange ToFileChange(SessionState state, string changeType, FileSnapshot before, FileSnapshot after, string path)
        {
            var effective = after ?? before;
            var canRevert = changeType == "Added" || (before?.Captured == true && CaptureFileExists(state, before));
            var risks = BuildRiskFlags(changeType, path);
            return new FileChange
            {
                Path = path,
                ChangeType = changeType,
                Kind = effective?.Kind ?? ClassifyPath(path),
                Extension = Path.GetExtension(path),
                BeforeSizeBytes = before?.SizeBytes,
                AfterSizeBytes = after?.SizeBytes,
                BeforeSha256 = before?.Sha256,
                AfterSha256 = after?.Sha256,
                Captured = before?.Captured == true,
                CanRevert = canRevert,
                Risk = risks.Risk,
                RiskFlags = risks.Flags,
                TextLike = effective?.TextLike == true || IsTextLike(path)
            };
        }

        static object BuildChangeSummary(List<FileChange> items, int totalChanged, int returned)
        {
            var byType = items.GroupBy(item => item.ChangeType).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var byKind = items.GroupBy(item => item.Kind ?? "unknown").ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var highRisk = items.Where(item => string.Equals(item.Risk, "High", StringComparison.OrdinalIgnoreCase)).ToArray();
            return new
            {
                totalChanged,
                returned,
                added = byType.TryGetValue("Added", out var added) ? added : 0,
                modified = byType.TryGetValue("Modified", out var modified) ? modified : 0,
                deleted = byType.TryGetValue("Deleted", out var deleted) ? deleted : 0,
                byKind,
                highRiskCount = highRisk.Length,
                highRiskPaths = highRisk.Take(20).Select(item => item.Path).ToArray(),
                restorableCount = items.Count(item => item.CanRevert),
                nonRestorableCount = items.Count(item => !item.CanRevert)
            };
        }

        static object BuildDiff(SessionState state, string path, Dictionary<string, FileChange> changesByPath, int maxDiffLines)
        {
            if (!changesByPath.TryGetValue(path, out var change))
            {
                return new { path, found = false, message = "Path is not changed in this session." };
            }

            if (!change.TextLike)
            {
                return new { path, found = true, changeType = change.ChangeType, textDiffAvailable = false, message = "File is not text-like; only metadata is available.", change = ToFileChangeDto(change) };
            }

            var beforeText = ReadBaselineText(state, path);
            var afterText = ReadCurrentText(path);
            var diff = BuildCompactDiffLines(beforeText, afterText, maxDiffLines);

            return new
            {
                path,
                found = true,
                changeType = change.ChangeType,
                textDiffAvailable = diff.Available,
                truncated = diff.Truncated,
                lineCount = diff.Lines.Length,
                lines = diff.Lines,
                change = ToFileChangeDto(change)
            };
        }

        static object ToFileChangeDto(FileChange change)
        {
            return new
            {
                path = change.Path,
                changeType = change.ChangeType,
                kind = change.Kind,
                extension = change.Extension,
                beforeSizeBytes = change.BeforeSizeBytes,
                afterSizeBytes = change.AfterSizeBytes,
                beforeSha256 = change.BeforeSha256,
                afterSha256 = change.AfterSha256,
                captured = change.Captured,
                canRevert = change.CanRevert,
                risk = change.Risk,
                riskFlags = change.RiskFlags,
                textLike = change.TextLike
            };
        }

        static object ToRevertPlanDto(RevertPlan plan)
        {
            return new
            {
                path = plan.Path,
                changeType = plan.ChangeType,
                operation = plan.Operation,
                canRevert = plan.CanRevert,
                reason = plan.Reason,
                captured = plan.Captured,
                capturePath = plan.CapturePath
            };
        }

        static CompactDiff BuildCompactDiffLines(string beforeText, string afterText, int maxDiffLines)
        {
            if (beforeText == null && afterText == null)
                return new CompactDiff { Available = false, Lines = Array.Empty<string>() };

            var before = SplitLines(beforeText);
            var after = SplitLines(afterText);
            if (before.SequenceEqual(after))
                return new CompactDiff { Available = true, Lines = new[] { " no textual changes" } };

            var prefix = 0;
            while (prefix < before.Length && prefix < after.Length && string.Equals(before[prefix], after[prefix], StringComparison.Ordinal))
                prefix++;

            var suffix = 0;
            while (suffix + prefix < before.Length &&
                   suffix + prefix < after.Length &&
                   string.Equals(before[before.Length - 1 - suffix], after[after.Length - 1 - suffix], StringComparison.Ordinal))
            {
                suffix++;
            }

            var lines = new List<string>();
            var contextStart = Math.Max(0, prefix - 3);
            for (var i = contextStart; i < prefix; i++)
                lines.Add(" " + before[i]);

            var beforeEnd = before.Length - suffix;
            for (var i = prefix; i < beforeEnd; i++)
                lines.Add("-" + before[i]);

            var afterEnd = after.Length - suffix;
            for (var i = prefix; i < afterEnd; i++)
                lines.Add("+" + after[i]);

            var contextAfterEnd = Math.Min(before.Length, beforeEnd + 3);
            for (var i = beforeEnd; i < contextAfterEnd; i++)
                lines.Add(" " + before[i]);

            var truncated = false;
            if (lines.Count > maxDiffLines)
            {
                lines = lines.Take(Math.Max(1, maxDiffLines)).ToList();
                lines.Add($"... diff truncated to {maxDiffLines} lines");
                truncated = true;
            }

            return new CompactDiff { Available = true, Truncated = truncated, Lines = lines.ToArray() };
        }

        static List<FileChange> SelectRevertTargets(List<FileChange> changes, string[] selected, bool revertAll, bool deleteAddedMetaWithAsset)
        {
            if (revertAll)
                return changes.ToList();

            var wanted = new HashSet<string>(selected.Select(NormalizeProjectRelativePath).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
            if (deleteAddedMetaWithAsset)
            {
                foreach (var path in wanted.ToArray())
                {
                    if (!path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        wanted.Add(path + ".meta");
                }
            }

            return changes.Where(change => wanted.Contains(change.Path)).ToList();
        }

        static RevertPlan BuildRevertPlan(SessionState state, FileChange change)
        {
            var plan = new RevertPlan
            {
                Path = change.Path,
                ChangeType = change.ChangeType,
                Operation = change.ChangeType == "Added" ? "Delete added file" : "Restore baseline file",
                CanRevert = change.CanRevert,
                Reason = change.CanRevert ? null : "Baseline bytes were not captured for this file."
            };

            if (change.ChangeType == "Deleted" || change.ChangeType == "Modified")
            {
                if (state.Files.TryGetValue(change.Path, out var snapshot))
                {
                    plan.Captured = snapshot.Captured;
                    plan.CapturePath = snapshot.CapturePath;
                }
            }

            return plan;
        }

        static object ExecuteRevert(SessionState state, FileChange change)
        {
            var absolute = ToAbsoluteProjectPath(change.Path);
            if (change.ChangeType == "Added")
            {
                if (File.Exists(absolute))
                    File.Delete(absolute);
                return new { path = change.Path, operation = "Deleted added file" };
            }

            if (!state.Files.TryGetValue(change.Path, out var snapshot) ||
                !snapshot.Captured ||
                !CaptureFileExists(state, snapshot))
            {
                throw new InvalidOperationException("No captured baseline bytes are available.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            File.Copy(GetAbsoluteCapturePath(state, snapshot), absolute, overwrite: true);
            return new { path = change.Path, operation = "Restored baseline file", bytes = new FileInfo(absolute).Length };
        }

        static string ReadBaselineText(SessionState state, string path)
        {
            if (!state.Files.TryGetValue(path, out var snapshot))
                return null;
            if (!snapshot.Captured || !CaptureFileExists(state, snapshot))
                return null;
            return TryReadText(GetAbsoluteCapturePath(state, snapshot));
        }

        static string ReadCurrentText(string path)
        {
            var absolute = ToAbsoluteProjectPath(path);
            return File.Exists(absolute) ? TryReadText(absolute) : null;
        }

        static string TryReadText(string absolutePath)
        {
            try
            {
                return File.ReadAllText(absolutePath);
            }
            catch
            {
                return null;
            }
        }

        static string[] SplitLines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        static bool ShouldSkipFile(string absolutePath)
        {
            var normalized = absolutePath.Replace('\\', '/');
            return normalized.Contains("/Library/") ||
                   normalized.Contains("/Temp/") ||
                   normalized.Contains("/obj/") ||
                   normalized.Contains("/Logs/") ||
                   normalized.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        }

        static RiskInfo BuildRiskFlags(string changeType, string path)
        {
            var flags = new List<string>();
            var ext = Path.GetExtension(path);
            var risk = "Low";

            if (string.Equals(changeType, "Deleted", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("deleted");
                risk = "High";
            }

            if (ext.Equals(".meta", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("meta-guid");
                risk = "High";
            }

            if (HighRiskExtensions.Contains(ext))
            {
                flags.Add("unity-serialized-asset");
                if (risk != "High")
                    risk = "Medium";
            }

            if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("script");
                if (risk == "Low")
                    risk = "Medium";
            }

            if (path.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("project-settings");
                risk = "High";
            }

            return new RiskInfo { Risk = risk, Flags = flags.Distinct().OrderBy(value => value).ToArray() };
        }

        static string ClassifyPath(string path)
        {
            if (path.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
                return "projectSettings";

            var ext = Path.GetExtension(path);
            return ext.ToLowerInvariant() switch
            {
                ".cs" => "script",
                ".unity" => "scene",
                ".prefab" => "prefab",
                ".asset" => "asset",
                ".mat" => "material",
                ".controller" or ".overridecontroller" => "animatorController",
                ".anim" => "animationClip",
                ".meta" => "meta",
                ".inputactions" => "inputActions",
                ".json" => "json",
                ".uxml" or ".uss" => "uiToolkit",
                _ => path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ? "package" : "file"
            };
        }

        static bool IsTextLike(string path)
        {
            return TextExtensions.Contains(Path.GetExtension(path));
        }

        static bool IsCapturable(string path)
        {
            return CapturableExtensions.Contains(Path.GetExtension(path));
        }

        static string TryComputeSha256(string absolutePath)
        {
            try
            {
                using var stream = File.OpenRead(absolutePath);
                using var sha = SHA256.Create();
                return BytesToHex(sha.ComputeHash(stream));
            }
            catch
            {
                return null;
            }
        }

        static bool CaptureFileExists(SessionState state, FileSnapshot snapshot)
        {
            return !string.IsNullOrWhiteSpace(snapshot?.CapturePath) && File.Exists(GetAbsoluteCapturePath(state, snapshot));
        }

        static string GetAbsoluteCapturePath(SessionState state, FileSnapshot snapshot)
        {
            return Path.Combine(GetSessionDir(state.SessionId), snapshot.CapturePath.Replace('/', Path.DirectorySeparatorChar));
        }

        static SessionState LoadRequestedState(JObject parameters, bool required)
        {
            var sessionId = GetString(parameters, "SessionId", "sessionId", "session_id");
            if (string.IsNullOrWhiteSpace(sessionId))
                sessionId = GetActiveSessionId() ?? GetLatestSessionId();

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                if (required)
                    throw new InvalidOperationException("No session id was supplied and no active work session exists.");
                return null;
            }

            var path = GetSessionFile(sessionId);
            if (!File.Exists(path))
            {
                if (required)
                    throw new FileNotFoundException($"Work session '{sessionId}' does not exist.", path);
                return null;
            }

            return JsonConvert.DeserializeObject<SessionState>(File.ReadAllText(path));
        }

        static void SaveState(SessionState state)
        {
            Directory.CreateDirectory(GetSessionDir(state.SessionId));
            File.WriteAllText(GetSessionFile(state.SessionId), JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        static object[] ListSessionSummaries(int limit)
        {
            if (!Directory.Exists(SessionRoot))
                return Array.Empty<object>();

            return Directory.GetDirectories(SessionRoot)
                .Select(dir => Path.GetFileName(dir))
                .Where(id => File.Exists(GetSessionFile(id)))
                .Select(id =>
                {
                    try { return LoadStateById(id); }
                    catch { return null; }
                })
                .Where(state => state != null)
                .OrderByDescending(state => state.StartedUtc)
                .Take(Math.Max(1, limit))
                .Select(ToSessionSummary)
                .ToArray();
        }

        static SessionState LoadStateById(string sessionId)
        {
            return JsonConvert.DeserializeObject<SessionState>(File.ReadAllText(GetSessionFile(sessionId)));
        }

        static object ToSessionSummary(SessionState state)
        {
            return new
            {
                sessionId = state.SessionId,
                name = state.Name,
                projectRoot = NormalizeSlashes(state.ProjectRoot),
                unityVersion = state.UnityVersion,
                startedUtc = state.StartedUtc,
                endedUtc = state.EndedUtc,
                fileCount = state.Baseline?.FileCount ?? state.Files?.Count ?? 0,
                capturedFiles = state.Baseline?.CapturedFiles ?? 0,
                captureTruncated = state.Baseline?.CaptureTruncated ?? false,
                sceneSemanticBaseline = state.SemanticBaseline != null ? new
                {
                    enabled = state.SemanticBaseline.Enabled,
                    sceneCount = state.SemanticBaseline.SceneCount,
                    objectCount = state.SemanticBaseline.ObjectCount,
                    truncated = state.SemanticBaseline.Truncated
                } : null
            };
        }

        static object ToStorageSummary(SessionState state)
        {
            return new
            {
                sessionDir = ToProjectDisplayPath(GetSessionDir(state.SessionId)),
                sessionFile = ToProjectDisplayPath(GetSessionFile(state.SessionId))
            };
        }

        static string[] ReadPaths(JObject parameters)
        {
            var token = parameters["Paths"] ?? parameters["paths"] ?? parameters["Path"] ?? parameters["path"];
            if (token == null || token.Type == JTokenType.Null)
                return Array.Empty<string>();

            if (token.Type == JTokenType.Array)
                return token.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalizeProjectRelativePath).ToArray();

            var raw = token.ToString();
            return raw.Split(new[] { '\n', '\r', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeProjectRelativePath)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        static string GetActiveSessionId()
        {
            var path = GetActiveSessionPath();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        static string GetLatestSessionId()
        {
            if (!Directory.Exists(SessionRoot))
                return null;

            return Directory.GetDirectories(SessionRoot)
                .Select(Path.GetFileName)
                .Where(id => File.Exists(GetSessionFile(id)))
                .OrderByDescending(id => id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        static string GetString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) &&
                    token != null &&
                    token.Type != JTokenType.Null)
                    return token.ToString();
            }

            return null;
        }

        static bool GetBool(JObject obj, bool fallback, params string[] keys)
        {
            var raw = GetString(obj, keys);
            return bool.TryParse(raw, out var value) ? value : fallback;
        }

        static int GetInt(JObject obj, int fallback, params string[] keys)
        {
            var raw = GetString(obj, keys);
            return int.TryParse(raw, out var value) ? value : fallback;
        }

        static long GetLong(JObject obj, long fallback, params string[] keys)
        {
            var raw = GetString(obj, keys);
            return long.TryParse(raw, out var value) ? value : fallback;
        }

        static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }

        static string NormalizeProjectRelativePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var path = value.Trim().Trim('"').Replace('\\', '/');
            if (path.StartsWith("project://", StringComparison.OrdinalIgnoreCase))
                path = path.Substring("project://".Length);
            if (path.StartsWith("project:/", StringComparison.OrdinalIgnoreCase))
                path = path.Substring("project:/".Length);
            if (path.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
                path = path.Substring("unity://path/".Length);
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try { path = new Uri(path).LocalPath.Replace('\\', '/'); }
                catch { path = path.Substring("file://".Length); }
            }

            var projectRoot = NormalizeSlashes(ProjectRoot).TrimEnd('/');
            if (Path.IsPathRooted(path))
            {
                var full = NormalizeSlashes(Path.GetFullPath(path));
                if (full.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                    path = full.Substring(projectRoot.Length + 1);
            }

            while (path.StartsWith("./", StringComparison.Ordinal))
                path = path.Substring(2);
            while (path.StartsWith("/", StringComparison.Ordinal))
                path = path.Substring(1);
            while (path.Contains("//"))
                path = path.Replace("//", "/");
            return path.TrimEnd('/');
        }

        static string ToProjectRelativePath(string absolutePath)
        {
            var root = NormalizeSlashes(ProjectRoot).TrimEnd('/');
            var full = NormalizeSlashes(Path.GetFullPath(absolutePath));
            return full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length + 1) : null;
        }

        static string ToAbsoluteProjectPath(string projectRelativePath)
        {
            var normalized = NormalizeProjectRelativePath(projectRelativePath);
            var full = Path.GetFullPath(Path.Combine(ProjectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Path is outside project root: {projectRelativePath}");
            return full;
        }

        static string ToProjectDisplayPath(string absolutePath)
        {
            var relative = ToProjectRelativePath(absolutePath);
            return relative ?? NormalizeSlashes(absolutePath);
        }

        static string ToSessionRelativePath(string sessionId, string absolutePath)
        {
            var root = NormalizeSlashes(GetSessionDir(sessionId)).TrimEnd('/');
            var full = NormalizeSlashes(Path.GetFullPath(absolutePath));
            return full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length + 1) : full;
        }

        static string GetCapturedFilePath(string sessionId, string projectRelativePath)
        {
            var safeHash = ShortHash(projectRelativePath);
            var extension = Path.GetExtension(projectRelativePath);
            return Path.Combine(GetCaptureDir(sessionId), safeHash + extension);
        }

        static void EnsureSessionRoot()
        {
            Directory.CreateDirectory(SessionRoot);
        }

        static string GetActiveSessionPath()
        {
            return Path.Combine(SessionRoot, "active.txt");
        }

        static string GetSessionDir(string sessionId)
        {
            return Path.Combine(SessionRoot, sessionId);
        }

        static string GetSessionFile(string sessionId)
        {
            return Path.Combine(GetSessionDir(sessionId), "session.json");
        }

        static string GetCaptureDir(string sessionId)
        {
            return Path.Combine(GetSessionDir(sessionId), "captures");
        }

        static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        static string SessionRoot => Path.Combine(ProjectRoot, "Library", "UniBridge", "WorkSessions");

        static string ShortHash(string text)
        {
            using var sha = SHA256.Create();
            return BytesToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty))).Substring(0, 12);
        }

        static string BytesToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        static string NormalizeSlashes(string value)
        {
            return value?.Replace('\\', '/');
        }

        sealed class ScanOptions
        {
            public bool IncludeProjectSettings = true;
            public bool IncludePackageManifests = true;
            public bool IncludePackageFiles;
            public int MaxFiles = DefaultMaxFiles;
            public long MaxSingleCaptureBytes = DefaultMaxSingleCaptureBytes;
            public long MaxTotalCaptureBytes = DefaultMaxTotalCaptureBytes;
            public bool IncludeSceneSemantics = true;
            public int MaxSemanticObjects = DefaultMaxSemanticObjects;

            public static ScanOptions From(JObject parameters)
            {
                return new ScanOptions
                {
                    IncludeProjectSettings = GetBool(parameters, true, "IncludeProjectSettings", "includeProjectSettings", "include_project_settings"),
                    IncludePackageManifests = GetBool(parameters, true, "IncludePackageManifests", "includePackageManifests", "include_package_manifests"),
                    IncludePackageFiles = GetBool(parameters, false, "IncludePackageFiles", "includePackageFiles", "include_package_files"),
                    MaxFiles = Math.Max(100, GetInt(parameters, DefaultMaxFiles, "MaxFiles", "maxFiles")),
                    MaxSingleCaptureBytes = Math.Max(0, GetLong(parameters, DefaultMaxSingleCaptureBytes, "MaxSingleCaptureBytes", "maxSingleCaptureBytes")),
                    MaxTotalCaptureBytes = Math.Max(0, GetLong(parameters, DefaultMaxTotalCaptureBytes, "MaxTotalCaptureBytes", "maxTotalCaptureBytes")),
                    IncludeSceneSemantics = GetBool(parameters, true, "IncludeSceneSemantics", "includeSceneSemantics", "include_scene_semantics"),
                    MaxSemanticObjects = Math.Max(0, GetInt(parameters, DefaultMaxSemanticObjects, "MaxSemanticObjects", "maxSemanticObjects", "max_semantic_objects"))
                };
            }
        }

        sealed class SessionState
        {
            public int Version;
            public string SessionId;
            public string Name;
            public string ProjectRoot;
            public string UnityVersion;
            public string StartedUtc;
            public string EndedUtc;
            public ScanOptions Options;
            public SessionBaseline Baseline;
            public SessionSemanticBaseline SemanticBaseline;
            public Dictionary<string, FileSnapshot> Files = new(StringComparer.OrdinalIgnoreCase);
        }

        sealed class SessionBaseline
        {
            public int FileCount;
            public int CapturedFiles;
            public long CapturedBytes;
            public bool CaptureTruncated;
            public List<string> Warnings = new();
        }

        sealed class FileSnapshot
        {
            public string Path;
            public bool Exists;
            public long SizeBytes;
            public string LastWriteUtc;
            public string Sha256;
            public string Kind;
            public bool TextLike;
            public bool Captured;
            public string CapturePath;
        }

        sealed class FileChange
        {
            public string Path;
            public string ChangeType;
            public string Kind;
            public string Extension;
            public long? BeforeSizeBytes;
            public long? AfterSizeBytes;
            public string BeforeSha256;
            public string AfterSha256;
            public bool Captured;
            public bool CanRevert;
            public string Risk;
            public string[] RiskFlags;
            public bool TextLike;
        }

        sealed class RevertPlan
        {
            public string Path;
            public string ChangeType;
            public string Operation;
            public bool CanRevert;
            public string Reason;
            public bool Captured;
            public string CapturePath;
        }

        sealed class ChangeSet
        {
            public List<FileChange> All;
            public FileChange[] Items;
            public int TotalChanged;
            public object Summary;
            public string[] Warnings;
        }

        sealed class ProjectScan
        {
            public Dictionary<string, FileSnapshot> Files;
            public List<string> Warnings;
        }

        sealed class ScanRoot
        {
            public string DisplayPath;
            public string AbsolutePath;

            public ScanRoot(string displayPath, string absolutePath)
            {
                DisplayPath = displayPath;
                AbsolutePath = absolutePath;
            }
        }

        sealed class CaptureBudget
        {
            readonly long maxSingleBytes;
            readonly long maxTotalBytes;
            public int CapturedFiles;
            public long CapturedBytes;
            public bool Truncated;
            public readonly List<string> Warnings = new();

            public CaptureBudget(long maxSingleBytes, long maxTotalBytes)
            {
                this.maxSingleBytes = maxSingleBytes;
                this.maxTotalBytes = maxTotalBytes;
            }

            public bool TryReserve(long sizeBytes, string path)
            {
                if (maxSingleBytes <= 0 || maxTotalBytes <= 0)
                    return false;

                if (sizeBytes > maxSingleBytes)
                {
                    Warnings.Add($"Skipped baseline capture for large file '{path}' ({sizeBytes} bytes).");
                    return false;
                }

                if (CapturedBytes + sizeBytes > maxTotalBytes)
                {
                    Truncated = true;
                    Warnings.Add($"Baseline capture reached MaxTotalCaptureBytes={maxTotalBytes}; later files may not be restorable.");
                    return false;
                }

                CapturedFiles++;
                CapturedBytes += sizeBytes;
                return true;
            }
        }

        sealed class RiskInfo
        {
            public string Risk;
            public string[] Flags;
        }

        sealed class CompactDiff
        {
            public bool Available;
            public bool Truncated;
            public string[] Lines;
        }
    }
}
