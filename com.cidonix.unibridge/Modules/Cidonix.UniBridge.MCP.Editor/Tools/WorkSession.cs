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
    BeginWrite: Capture per-path preconditions before an explicitly owned external write.
    CompleteWrite: Record that write only when expected payload hashes match the files.
    Revert: Preview guarded owned file reverts, then execute the unchanged PlanId once.
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
                        @enum = new[] { "Begin", "Status", "Review", "Diff", "BeginWrite", "CompleteWrite", "Revert", "End", "List" },
                        @default = "Status"
                    },
                    SessionId = new { type = "string", description = "Existing session id. If omitted, the active/latest session is used." },
                    Name = new { type = "string", description = "Human-readable session name for Begin." },
                    Paths = new { type = "array", items = new { type = "string" }, description = "Project-relative paths to diff/revert. Omit for Review; use RevertAll=true for full revert." },
                    RevertAll = new { type = "boolean", description = "For Revert, select owned changed files only; unrelated changes are reported and preserved.", @default = false },
                    DryRun = new { type = "boolean", description = "For Revert, preview without touching files.", @default = true },
                    PlanId = new { type = "string", description = "For executed Revert, the one-use planId from a successful dry-run. Changed files, ownership or selection invalidate the plan." },
                    TokenId = new { type = "string", description = "For CompleteWrite, tokenId from BeginWrite BEFORE the write." },
                    Source = new { type = "string", description = "Description of the explicitly owned write for BeginWrite." },
                    AfterSha256 = new { type = "object", additionalProperties = new { type = new[] { "string", "null" } }, description = "For CompleteWrite, project-relative path to expected SHA256 of the known written bytes; null means an explicitly deleted file. Never derive these from unrelated current changes." },
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
                    "beginwrite" => BeginWriteTracking(parameters),
                    "completewrite" => CompleteWriteTracking(parameters),
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
                Version = 2,
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
                    ScanComplete = scan.Complete,
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
            var skipped = revertAll ? changes.All.Where(change => !state.OwnedWrites.ContainsKey(change.Path)).Select(change => change.Path).ToArray() : Array.Empty<string>();
            var candidates = revertAll ? changes.All.Where(change => state.OwnedWrites.ContainsKey(change.Path)).ToList() : changes.All;
            var targets = SelectRevertTargets(candidates, selected, revertAll, deleteAddedMetaWithAsset);
            var blockers = new List<string>();
            if (state.Version < 2 || state.Baseline?.ScanComplete != true || !changes.ScanComplete)
                blockers.Add("A complete version-2 baseline and current scan are required. Unknown paths from incomplete scans cannot be reverted.");
            if (!revertAll)
            {
                foreach (var path in selected.Where(path => !targets.Any(change => string.Equals(change.Path, path, StringComparison.OrdinalIgnoreCase))))
                    blockers.Add($"Selected path is not a known changed file: {path}");
            }
            if (targets.Count == 0)
                blockers.Add("No owned changed files were selected.");
            var plan = targets.Select(change => BuildRevertPlan(state, change)).ToArray();
            blockers.AddRange(plan.Where(item => !item.CanRevert).Select(item => $"{item.Path}: {item.Reason}"));
            // A metadata file can be independently owned/edited. Refresh can remove orphan
            // metadata, so never delete an asset with an unselected existing companion.
            foreach (var change in targets.Where(change => change.ChangeType == "Added" && !change.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            {
                var meta = change.Path + ".meta";
                if (File.Exists(ToAbsoluteProjectPath(meta)) && !targets.Any(item => item.ChangeType == "Added" && string.Equals(item.Path, meta, StringComparison.OrdinalIgnoreCase)))
                    blockers.Add($"{change.Path}: existing companion metadata is not included as a verified owned addition: {meta}");
            }
            foreach (var change in targets.Where(change => change.ChangeType == "Added" && change.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            {
                var asset = change.Path.Substring(0, change.Path.Length - 5);
                if ((File.Exists(ToAbsoluteProjectPath(asset)) || Directory.Exists(ToAbsoluteProjectPath(asset))) &&
                    !targets.Any(item => item.ChangeType == "Added" && string.Equals(item.Path, asset, StringComparison.OrdinalIgnoreCase)))
                    blockers.Add($"{change.Path}: removing metadata while retaining its asset could regenerate a new GUID. Preserve this metadata.");
            }
            string planId = null;
            if (blockers.Count == 0)
            {
                if (dryRun)
                    planId = SaveRevertPreview(state, plan, selected, revertAll, deleteAddedMetaWithAsset);
                else
                {
                    planId = GetString(parameters, "PlanId", "planId", "plan_id");
                    ValidateAndConsumeRevertPreview(state, plan, selected, revertAll, deleteAddedMetaWithAsset, planId, blockers);
                }
            }
            if (dryRun || blockers.Count > 0)
            {
                var data = new
                {
                    action = "Revert", status = blockers.Count == 0 ? "preview" : "blocked", dryRun,
                    session = ToSessionSummary(state), requested = selected, revertAll, planId,
                    plan = plan.Select(ToRevertPlanDto).ToArray(), canExecute = blockers.Count == 0,
                    invalidCount = blockers.Count, blockers, skippedUnownedPaths = skipped,
                    hint = blockers.Count == 0 ? "Execute the same selection with DryRun=false and this PlanId." : "Preserve blocked files. Start a complete session and track owned writes before modifying files; do not claim existing changes."
                };
                return blockers.Count == 0 ? Response.Success("Built guarded UniBridge revert preview.", data) : Response.Error("WORK_SESSION_REVERT_BLOCKED", data);
            }
            var results = new List<object>();
            var errors = new List<string>();
            var possiblyModified = false;
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addedRecoveryGroups = new List<AddedRevertGroupRecovery>();
            var compensatedPaths = Array.Empty<string>();
            AssetDatabase.DisallowAutoRefresh();
            try
            {
            foreach (var change in targets)
            {
                if (processed.Contains(change.Path)) continue;
                try
                {
                    if (change.ChangeType == "Added")
                    {
                        var group = targets.Where(item => item.ChangeType == "Added" &&
                            (string.Equals(item.Path, change.Path, StringComparison.OrdinalIgnoreCase) ||
                             !change.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && string.Equals(item.Path, change.Path + ".meta", StringComparison.OrdinalIgnoreCase))).ToArray();
                        results.AddRange(ExecuteAddedRevertGroupWithRecovery(state, group, addedRecoveryGroups));
                        foreach (var item in group) processed.Add(item.Path);
                    }
                    else
                    {
                        results.Add(ExecuteRevert(state, change));
                        processed.Add(change.Path);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{change.Path}: {ex.Message}");
                    possiblyModified |= ex is RevertMutationException;
                    break; // Preserve the remaining files after the first failed operation.
                }
            }
            }
            finally
            {
                try
                {
                    try { ValidateAddedRevertGroupsBeforeRefresh(state, addedRecoveryGroups); }
                    catch (Exception ex)
                    {
                        errors.Add("Added asset/metadata refresh guard: " + ex.Message);
                        possiblyModified |= ex is RevertMutationException;
                    }
                    compensatedPaths = addedRecoveryGroups.SelectMany(group => group.CompensatedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (compensatedPaths.Length > 0)
                    {
                        var returnedPaths = new HashSet<string>(compensatedPaths, StringComparer.OrdinalIgnoreCase);
                        results.RemoveAll(result => returnedPaths.Contains(McpJson.ObjectFromObject(result).Value<string>("path")));
                        processed.ExceptWith(returnedPaths);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("Restore result verification: " + ex.Message);
                    possiblyModified = true;
                }
                finally
                {
                    try { AssetDatabase.AllowAutoRefresh(); }
                    catch (Exception ex) { errors.Add("Restore auto-refresh: " + ex.Message); }
                }
            }
            object remaining = null;
            if (results.Count > 0 && errors.Count == 0)
            {
                try { AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate); }
                catch (Exception ex) { errors.Add("Refresh: " + ex.Message); }
                foreach (var path in processed)
                {
                    state.Files.TryGetValue(path, out var baseline);
                    var absolute = ToAbsoluteProjectPath(path);
                    if (File.Exists(absolute) != (baseline != null) ||
                        baseline != null && !string.Equals(TryComputeSha256(absolute), baseline.Sha256, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"{path}: importer/callback changed the post-restore result; preserve it and review before further action.");
                }
            }
            try { remaining = BuildChanges(state, state.Options, DefaultMaxChanged).Summary; }
            catch (Exception ex) { errors.Add("Post-revert review: " + ex.Message); }
            var outcome = new
            {
                action = "Revert", status = errors.Count == 0 ? "completed" : results.Count > 0 || possiblyModified ? "partial" : "blocked",
                dryRun = false, planId, session = ToSessionSummary(state), reverted = results, errors,
                skippedUnownedPaths = skipped, compensatedPaths, remainingChanges = remaining,
                recoveryStorage = ToProjectDisplayPath(Path.Combine(GetSessionDir(state.SessionId), "revert-recovery"))
            };
            return errors.Count == 0 ? Response.Success("Reverted verified owned UniBridge changes.", outcome) : Response.Error("WORK_SESSION_REVERT_INCOMPLETE", outcome);
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
            var complete = true;
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
                        return new ProjectScan { Files = files, Warnings = warnings, Complete = false };
                    }

                    if (ShouldSkipFile(file))
                        continue;

                    AddFile(file);
                }
            }

            return new ProjectScan { Files = files, Warnings = warnings, Complete = complete };

            void AddFile(string absolutePath)
            {
                var relative = ToProjectRelativePath(absolutePath);
                if (string.IsNullOrWhiteSpace(relative) || files.ContainsKey(relative))
                    return;

                try
                {
                    EnsureNoReparsePoints(absolutePath, ProjectRoot);
                    var snapshot = BuildSnapshot(relative, absolutePath, capture, sessionId, computeHashes);
                    files[relative] = snapshot;
                    if (computeHashes && string.IsNullOrWhiteSpace(snapshot.Sha256))
                        throw new IOException("File fingerprint could not be read.");
                }
                catch (Exception ex)
                {
                    complete = false;
                    warnings.Add($"File scan could not verify '{relative}': {ex.Message}");
                }
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
                // Fingerprint and capture the same locked byte stream. A separate hash
                // followed by File.Copy can snapshot a different concurrent revision.
                using var source = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (var destination = new FileStream(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(destination);
                source.Position = 0;
                using var sha = SHA256.Create();
                snapshot.Sha256 = BytesToHex(sha.ComputeHash(source));
                snapshot.SizeBytes = source.Length;
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

                // Missing entries in a bounded scan mean unknown, not added/deleted.
                if ((before == null && state.Baseline?.ScanComplete != true) || (after == null && !current.Complete))
                    continue;
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
                ScanComplete = current.Complete,
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
            var canRevert = state.Version >= 2 && state.Baseline?.ScanComplete == true &&
                state.OwnedWrites != null && state.OwnedWrites.TryGetValue(path, out var owned) && !owned.Conflict &&
                owned.AfterExists == (after != null) && (!owned.AfterExists || string.Equals(owned.AfterSha256, after.Sha256, StringComparison.OrdinalIgnoreCase)) &&
                (changeType == "Added" || (before?.Captured == true && CaptureFileExists(state, before)));
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
                capturePath = plan.CapturePath,
                expectedExists = plan.ExpectedExists,
                expectedSha256 = plan.ExpectedSha256,
                baselineSha256 = plan.BaselineSha256
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
                    if (!path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                        changes.Any(change => change.ChangeType == "Added" && string.Equals(change.Path, path, StringComparison.OrdinalIgnoreCase)))
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
                CanRevert = true,
                ExpectedExists = change.ChangeType != "Deleted",
                ExpectedSha256 = change.AfterSha256,
                BaselineSha256 = change.BeforeSha256
            };

            if (change.ChangeType == "Deleted" || change.ChangeType == "Modified")
            {
                if (state.Files.TryGetValue(change.Path, out var snapshot))
                {
                    plan.Captured = snapshot.Captured;
                    plan.CapturePath = snapshot.CapturePath;
                }
            }

            plan.Reason = GetRevertBlocker(state, change);
            plan.CanRevert = plan.Reason == null;
            return plan;
        }

        static object ExecuteRevert(SessionState state, FileChange change)
        {
            return ExecuteGuardedRevert(state, change);
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
            var root = Path.GetFullPath(GetSessionDir(state.SessionId));
            var full = Path.GetFullPath(Path.Combine(root, snapshot.CapturePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Captured baseline path is outside its session.");
            EnsureNoReparsePoints(full, ProjectRoot);
            return full;
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

            return ReadSessionState(path);
        }

        static void SaveState(SessionState state)
        {
            Directory.CreateDirectory(GetSessionDir(state.SessionId));
            var path = GetSessionFile(state.SessionId);
            EnsureNoReparsePoints(path, ProjectRoot);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, McpJson.SerializeObject(state, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
            return ReadSessionState(GetSessionFile(sessionId));
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
                scanComplete = state.Baseline?.ScanComplete == true,
                guardedRevertAvailable = state.Version >= 2 && state.Baseline?.ScanComplete == true,
                ownedWriteCount = state.OwnedWrites?.Count ?? 0,
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
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId != Path.GetFileName(sessionId) || sessionId.Contains("..") || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("Invalid work session id.");
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
            public Dictionary<string, OwnedWriteReceipt> OwnedWrites = new(StringComparer.OrdinalIgnoreCase);
        }

        sealed class SessionBaseline
        {
            public int FileCount;
            public int CapturedFiles;
            public long CapturedBytes;
            public bool CaptureTruncated;
            public bool ScanComplete;
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
            public bool ExpectedExists;
            public string ExpectedSha256;
            public string BaselineSha256;
        }

        sealed class ChangeSet
        {
            public List<FileChange> All;
            public FileChange[] Items;
            public int TotalChanged;
            public object Summary;
            public string[] Warnings;
            public bool ScanComplete;
        }

        sealed class ProjectScan
        {
            public Dictionary<string, FileSnapshot> Files;
            public List<string> Warnings;
            public bool Complete;
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
