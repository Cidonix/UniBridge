#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class ContextSnapshot
    {
        const int MaxAgentBriefFolders = 1200;
        const int MaxAgentBriefFolderSamples = 6;
        const int MaxAgentBriefSystems = 14;

        static object BuildAgentBrief(SnapshotOptions options, object hierarchy, object console)
        {
            var activeWorkSession = BuildActiveWorkSessionBrief();
            var projectShape = BuildAgentProjectShape();
            var likelyFolders = BuildLikelyFolderMap();
            var likelySystems = BuildLikelyImportantSystems();
            var risks = BuildAgentRiskFlags(options, hierarchy, console, activeWorkSession, projectShape);
            var nextCalls = BuildRecommendedNextCalls(risks, activeWorkSession, projectShape);

            return new
            {
                purpose = "Compact first-call orientation for AI agents before planning or editing this Unity project.",
                summary = BuildAgentOneLineSummary(projectShape, likelySystems, risks),
                projectShape,
                likelyFolders,
                likelyImportantSystems = likelySystems,
                activeWorkSession,
                riskFlags = risks,
                guardrails = BuildAgentGuardrails(activeWorkSession, risks),
                operatingProtocol = BuildAgentOperatingProtocol(risks),
                verificationLadder = BuildAgentVerificationLadder(risks),
                recommendedNextCalls = nextCalls
            };
        }

        static object BuildAgentProjectShape()
        {
            var assetPaths = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                .Where(path => !AssetDatabase.IsValidFolder(path))
                .ToArray();

            var countsByKind = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in assetPaths)
            {
                var kind = DetermineAssetKind(Path.GetExtension(path));
                countsByKind[kind] = countsByKind.TryGetValue(kind, out var current) ? current + 1 : 1;
            }

            var loadedScenes = GetLoadedSceneBriefs();
            var activeScene = SceneManager.GetActiveScene();
            var rootCount = loadedScenes.Sum(scene => scene.RootCount);
            var dirtyCount = loadedScenes.Count(scene => scene.IsDirty);

            return new
            {
                totalAssets = assetPaths.Length,
                countsByKind,
                loadedSceneCount = loadedScenes.Count,
                loadedRootObjectCount = rootCount,
                dirtySceneCount = dirtyCount,
                activeScene = activeScene.IsValid()
                    ? new
                    {
                        name = activeScene.name,
                        path = activeScene.path,
                        rootCount = activeScene.isLoaded ? activeScene.rootCount : 0,
                        isDirty = activeScene.isDirty
                    }
                    : null,
                loadedScenes = loadedScenes.Select(scene => new
                {
                    name = scene.Name,
                    path = scene.Path,
                    rootCount = scene.RootCount,
                    isDirty = scene.IsDirty
                }).ToArray(),
                sceneScale = ClassifySceneScale(rootCount)
            };
        }

        static List<LoadedSceneBrief> GetLoadedSceneBriefs()
        {
            var scenes = new List<LoadedSceneBrief>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                scenes.Add(new LoadedSceneBrief
                {
                    Name = scene.name,
                    Path = scene.path,
                    RootCount = scene.rootCount,
                    IsDirty = scene.isDirty
                });
            }

            return scenes;
        }

        static string ClassifySceneScale(int rootCount)
        {
            if (rootCount >= 150)
            {
                return "large";
            }

            if (rootCount >= 60)
            {
                return "medium";
            }

            return "small";
        }

        static object BuildLikelyFolderMap()
        {
            var folders = EnumerateAssetFolders(MaxAgentBriefFolders)
                .Select(path => new FolderCandidate
                {
                    Path = path,
                    Depth = path.Count(ch => ch == '/'),
                    Score = FolderScore(path)
                })
                .Where(folder => folder.Score > 0)
                .OrderByDescending(folder => folder.Score)
                .ThenBy(folder => folder.Depth)
                .ThenBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new
            {
                scenes = FolderSamples(folders, "scene", "level", "map"),
                scripts = FolderSamples(folders, "script", "code", "source"),
                gameplay = FolderSamples(folders, "gameplay", "system", "manager", "player", "enemy", "character"),
                ui = FolderSamples(folders, "ui", "uxml", "uss", "canvas", "hud", "interface"),
                prefabs = FolderSamples(folders, "prefab"),
                art = FolderSamples(folders, "art", "sprite", "texture", "gfx", "graphic", "material", "model"),
                audio = FolderSamples(folders, "audio", "sound", "music", "sfx"),
                data = FolderSamples(folders, "data", "config", "setting", "resource", "scriptable")
            };
        }

        static IEnumerable<string> EnumerateAssetFolders(int maxFolders)
        {
            var root = NormalizePath(Application.dataPath).TrimEnd('/');
            if (!Directory.Exists(root))
            {
                yield break;
            }

            var stack = new Stack<string>();
            stack.Push(root);
            var count = 0;

            while (stack.Count > 0 && count < maxFolders)
            {
                var current = stack.Pop();
                string[] children;
                try
                {
                    children = Directory.GetDirectories(current);
                }
                catch
                {
                    continue;
                }

                foreach (var child in children.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(child);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var normalized = NormalizePath(child);
                    if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relative = "Assets" + normalized.Substring(root.Length);
                    count++;
                    yield return relative;

                    if (count >= maxFolders)
                    {
                        yield break;
                    }

                    stack.Push(child);
                }
            }
        }

        static int FolderScore(string path)
        {
            var lower = path.ToLowerInvariant();
            var score = 0;
            foreach (var keyword in new[]
            {
                "scene", "level", "map", "script", "code", "source", "gameplay", "system",
                "manager", "player", "enemy", "character", "ui", "uxml", "uss", "canvas",
                "hud", "interface", "prefab", "art", "sprite", "texture", "gfx", "graphic",
                "material", "model", "audio", "sound", "music", "sfx", "data", "config",
                "setting", "resource", "scriptable"
            })
            {
                if (lower.Contains(keyword))
                {
                    score++;
                }
            }

            return score;
        }

        static string[] FolderSamples(List<FolderCandidate> folders, params string[] keywords)
        {
            return folders
                .Where(folder => keywords.Any(keyword => folder.Path.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                .Take(MaxAgentBriefFolderSamples)
                .Select(folder => folder.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static object[] BuildLikelyImportantSystems()
        {
            var systems = new List<SystemSignal>();
            var packageNames = GetRegisteredPackages()
                .Where(package => package != null && !string.IsNullOrEmpty(package.name))
                .Select(package => package.name)
                .ToArray();

            AddPackageSignal(systems, packageNames, "com.unity.inputsystem", "Input System", "package");
            AddPackageSignal(systems, packageNames, "com.unity.cinemachine", "Cinemachine", "package");
            AddPackageSignal(systems, packageNames, "com.unity.timeline", "Timeline", "package");
            AddPackageSignal(systems, packageNames, "com.unity.addressables", "Addressables", "package");
            AddPackageSignal(systems, packageNames, "com.unity.render-pipelines.universal", "URP", "package");
            AddPackageSignal(systems, packageNames, "com.unity.render-pipelines.high-definition", "HDRP", "package");
            AddPackageSignal(systems, packageNames, "com.unity.ugui", "UGUI", "package");
            AddPackageSignal(systems, packageNames, "com.unity.modules.uielements", "UI Toolkit", "package");
            AddPackageSignal(systems, packageNames, "com.unity.2d.tilemap", "2D Tilemap", "package");

            AddAssetFolderSignal(systems, "Assets/TextMesh Pro", "TextMesh Pro", "assetFolder");
            AddAssetFolderSignal(systems, "Assets/Spine", "Spine", "assetFolder");
            AddAssetFolderSignal(systems, "Assets/CorgiEngine", "Corgi Engine", "assetFolder");
            AddAssetFolderSignal(systems, "Assets/MoreMountains", "More Mountains", "assetFolder");

            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null)
            {
                systems.Add(new SystemSignal
                {
                    Name = pipeline.name,
                    Source = "renderPipeline",
                    Detail = pipeline.GetType().FullName
                });
            }

            foreach (var asmdefPath in AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                .Where(path => path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(6))
            {
                systems.Add(new SystemSignal
                {
                    Name = Path.GetFileNameWithoutExtension(asmdefPath),
                    Source = "asmdef",
                    Detail = asmdefPath
                });
            }

            return systems
                .GroupBy(system => system.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(MaxAgentBriefSystems)
                .Select(system => new
                {
                    name = system.Name,
                    source = system.Source,
                    detail = system.Detail
                })
                .Cast<object>()
                .ToArray();
        }

        static void AddPackageSignal(List<SystemSignal> systems, string[] packageNames, string packageName, string displayName, string source)
        {
            if (packageNames.Any(name => string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase)))
            {
                systems.Add(new SystemSignal { Name = displayName, Source = source, Detail = packageName });
            }
        }

        static void AddAssetFolderSignal(List<SystemSignal> systems, string assetFolder, string displayName, string source)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
            {
                systems.Add(new SystemSignal { Name = displayName, Source = source, Detail = assetFolder });
            }
        }

        static object BuildActiveWorkSessionBrief()
        {
            var activePath = Path.Combine(GetProjectRoot(), "Library", "UniBridge", "WorkSessions", "active.txt");
            if (!File.Exists(activePath))
            {
                return new
                {
                    active = false,
                    sessionId = (string)null,
                    name = (string)null,
                    startedUtc = (string)null,
                    note = "No active UniBridge_WorkSession. Start one before mutating project or scene state."
                };
            }

            try
            {
                var sessionId = File.ReadAllText(activePath).Trim();
                var sessionPath = Path.Combine(GetProjectRoot(), "Library", "UniBridge", "WorkSessions", sessionId, "session.json");
                JObject session = null;
                if (File.Exists(sessionPath))
                {
                    session = JObject.Parse(File.ReadAllText(sessionPath));
                }

                return new
                {
                    active = true,
                    sessionId,
                    name = session?.Value<string>("Name"),
                    startedUtc = session?.Value<string>("StartedUtc"),
                    endedUtc = session?.Value<string>("EndedUtc"),
                    capturedFileCount = session?["Baseline"]?.Value<int?>("CapturedFiles"),
                    semanticBaselineEnabled = session?["SemanticBaseline"]?.Value<bool?>("Enabled"),
                    nextSuggestedCalls = new[]
                    {
                        $"UniBridge_WorkSession Action=Review SessionId={sessionId}",
                        "UniBridge_ExecutionStatus Action=Snapshot IncludeWorkSession=true"
                    }
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    active = false,
                    error = ex.Message,
                    note = "UniBridge found an active WorkSession marker but could not read the session metadata."
                };
            }
        }

        static object[] BuildAgentRiskFlags(SnapshotOptions options, object hierarchy, object console, object activeWorkSession, object projectShape)
        {
            var risks = new List<AgentRiskFlag>();
            AddEditorStateRisks(risks);
            AddConsoleRisks(risks, console);
            AddHierarchyRisks(risks, hierarchy);
            AddProjectShapeRisks(risks);
            AddWorkSessionRisk(risks, activeWorkSession);

            return risks.Select(risk => new
            {
                severity = risk.Severity,
                code = risk.Code,
                message = risk.Message,
                nextCall = risk.NextCall
            }).Cast<object>().ToArray();
        }

        static void AddEditorStateRisks(List<AgentRiskFlag> risks)
        {
            if (EditorApplication.isCompiling)
            {
                risks.Add(new AgentRiskFlag("error", "editor_compiling", "Unity is compiling; wait before code-dependent operations.", "UniBridge_ManageEditor Action=WaitForReadyAfterReload"));
            }

            if (EditorApplication.isUpdating)
            {
                risks.Add(new AgentRiskFlag("warning", "asset_importing", "Unity is importing/updating assets; wait before reading diagnostics or mutating assets.", "UniBridge_ManageEditor Action=WaitForReady"));
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                risks.Add(new AgentRiskFlag("warning", "play_mode_boundary", "Editor is in or entering Play Mode; scene edits may be runtime-only.", "UniBridge_ManageEditor Action=WaitForEditMode"));
            }

            var dirtyScenes = GetLoadedSceneBriefs().Where(scene => scene.IsDirty).ToArray();
            if (dirtyScenes.Length > 0)
            {
                risks.Add(new AgentRiskFlag("warning", "dirty_scenes", $"There are {dirtyScenes.Length} dirty loaded scene(s). Save or snapshot before broad changes.", "UniBridge_WorkSession Action=Begin"));
            }

            try
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null)
                {
                    risks.Add(new AgentRiskFlag("warning", "prefab_stage_open", $"Prefab Stage is open for '{stage.assetPath}'. Scene tools may target prefab contents.", "UniBridge_ManagePrefab Action=GetStageState"));
                }
            }
            catch
            {
                // Ignore Prefab Stage inspection failures in orientation.
            }
        }

        static void AddConsoleRisks(List<AgentRiskFlag> risks, object console)
        {
            var totals = TryGetConsoleTotals(console);
            if (totals == null)
            {
                return;
            }

            var errors = totals.Value<int?>("errorCount") ?? 0;
            var exceptions = totals.Value<int?>("exceptionCount") ?? 0;
            var warnings = totals.Value<int?>("warningCount") ?? 0;
            if (errors + exceptions > 0)
            {
                risks.Add(new AgentRiskFlag("error", "console_errors", $"Console has {errors} error(s) and {exceptions} exception(s). Investigate before assuming new changes caused issues.", "UniBridge_ReadConsole Action=DiagnosticSummary"));
            }
            else if (warnings > 0)
            {
                risks.Add(new AgentRiskFlag("warning", "console_warnings", $"Console has {warnings} warning(s). Read compact diagnostics before broad edits.", "UniBridge_ReadConsole Action=DiagnosticSummary"));
            }
        }

        static JObject TryGetConsoleTotals(object console)
        {
            try
            {
                var json = console != null ? JObject.FromObject(console) : null;
                return json?["data"]?["totals"] as JObject;
            }
            catch
            {
                return null;
            }
        }

        static void AddHierarchyRisks(List<AgentRiskFlag> risks, object hierarchy)
        {
            try
            {
                var json = hierarchy != null ? JObject.FromObject(hierarchy) : null;
                if (json == null)
                {
                    return;
                }

                if (json.Value<bool?>("truncatedByCount") == true)
                {
                    risks.Add(new AgentRiskFlag("info", "hierarchy_truncated_by_count", "ContextSnapshot hierarchy hit MaxSceneObjects; use SceneHierarchyExport for complete scene operations.", "UniBridge_SceneHierarchyExport Action=Export Format=jsonl OutputToFile=true"));
                }

                if (json.Value<bool?>("truncatedByDepth") == true)
                {
                    risks.Add(new AgentRiskFlag("info", "hierarchy_truncated_by_depth", "ContextSnapshot hierarchy is depth-limited; expand with SceneObjectView or SceneHierarchyExport before reparenting.", "UniBridge_SceneObjectView Action=Hierarchy MaxDepth=4"));
                }
            }
            catch
            {
                // Hierarchy hints are best-effort only.
            }
        }

        static void AddProjectShapeRisks(List<AgentRiskFlag> risks)
        {
            var rootCount = GetLoadedSceneBriefs().Sum(scene => scene.RootCount);
            if (rootCount >= 120)
            {
                risks.Add(new AgentRiskFlag("info", "large_loaded_scene", $"Loaded scenes contain {rootCount} root objects; prefer full hierarchy export and objectId-based edits.", "UniBridge_SceneHierarchyExport Action=Export OutputToFile=true IncludeRenderers=true IncludePrefabInfo=true"));
            }
        }

        static void AddWorkSessionRisk(List<AgentRiskFlag> risks, object activeWorkSession)
        {
            try
            {
                var json = JObject.FromObject(activeWorkSession);
                if (json.Value<bool?>("active") != true)
                {
                    risks.Add(new AgentRiskFlag("info", "no_active_work_session", "No active WorkSession. Begin one before mutating files, scenes, prefabs, or import settings.", "UniBridge_WorkSession Action=Begin Name=<task>"));
                }
            }
            catch
            {
                risks.Add(new AgentRiskFlag("info", "work_session_unknown", "WorkSession state could not be determined.", "UniBridge_WorkSession Action=Status"));
            }
        }

        static string[] BuildRecommendedNextCalls(object[] risks, object activeWorkSession, object projectShape)
        {
            var calls = new List<string>();
            var hasActiveSession = false;
            try
            {
                hasActiveSession = JObject.FromObject(activeWorkSession).Value<bool?>("active") == true;
            }
            catch
            {
                // Keep default false.
            }

            if (!hasActiveSession)
            {
                calls.Add("UniBridge_WorkSession Action=Begin Name=<task>");
            }

            calls.Add("UniBridge_ToolGuide Action=Overview");
            calls.Add("UniBridge_ToolGuide Action=Workflow Topic=agent_playbook");
            calls.Add("UniBridge_DomainCatalog Action=SuggestTools Query=<task domain>");

            if (HasRisk(risks, "console_errors") || HasRisk(risks, "console_warnings"))
            {
                calls.Add("UniBridge_ReadConsole Action=DiagnosticSummary");
            }

            if (HasRisk(risks, "large_loaded_scene") || HasRisk(risks, "hierarchy_truncated_by_count"))
            {
                calls.Add("UniBridge_SceneHierarchyExport Action=Export OutputToFile=true IncludeInactive=true IncludeRenderers=true IncludePrefabInfo=true");
            }

            calls.Add("UniBridge_SceneObjectView Action=Hierarchy MaxDepth=2");
            calls.Add("UniBridge_UnitySearch Query=<thing to find> IncludeInactive=true");
            calls.Add("UniBridge_CaptureView Action=SceneView");

            return calls.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        }

        static string[] BuildAgentGuardrails(object activeWorkSession, object[] risks)
        {
            var guardrails = new List<string>
            {
                "Read before modifying: resolve exact targets, current state, references, and domain schema before writes.",
                "Before mutating project files, scenes, prefabs, or import settings, start UniBridge_WorkSession and review it after the change.",
                "Use DryRun=true for BatchActions, ManageSceneHierarchy, prefab, importer, and broad asset operations before execution.",
                "For large or truncated scenes, prefer SceneHierarchyExport and objectId/indexedPath-based edits over name-only paths.",
                "After scripts/imports/Play Mode boundaries, use reload-safe editor calls and read compilation/console diagnostics.",
                "For visible work, capture or audit the result before saying it is done."
            };

            if (HasRisk(risks, "prefab_stage_open"))
            {
                guardrails.Add("Prefab Stage is open; be explicit whether the target is prefab contents or the loaded scene.");
            }

            return guardrails.ToArray();
        }

        static object BuildAgentOperatingProtocol(object[] risks)
        {
            return new
            {
                readBeforeModify = new[]
                {
                    "ContextSnapshot/DomainCatalog for project and domain orientation.",
                    "UnitySearch/SceneObjectView/AssetIntelligence/ScriptIntelligence/TypeSchema to resolve exact targets.",
                    "ReferenceGraph/Impact or ScriptIntelligence usages before moves, deletes, renames, callback changes, or serialized API changes."
                },
                scopeAwareness = new[]
                {
                    "Check dirty scenes, Play Mode, Prefab Stage, and active scene before editing.",
                    "Use ScopedEdit for a specific scene/prefab asset and EditorSnapshot for temporary editor context changes.",
                    "Use objectId, GUID, full type name, and indexedPath whenever duplicate names or stale paths are possible."
                },
                executionSafety = new[]
                {
                    "Begin WorkSession for broad work.",
                    "Dry-run BatchActions or hierarchy/prefab/asset operations before execution.",
                    "Keep batches small and request console/editor-event deltas when verifying an execution batch."
                },
                riskSpecificHint = BuildRiskSpecificHint(risks)
            };
        }

        static object BuildAgentVerificationLadder(object[] risks)
        {
            var steps = new List<string>
            {
                "Use the domain-specific inspect/read tool to confirm serialized state.",
                "ReadConsole Action=DiagnosticSummary after meaningful edits.",
                "Use EditorEvents/ExecutionStatus if the edit crossed asset refresh, compile, play-mode, or async editor boundaries.",
                "Use capture/visual audit for UI, rendering, VFX, camera, material, or scene presentation work.",
                "Use RuntimeStateProbe/RuntimeProfiler for Play Mode behavior or performance claims.",
                "Use WorkSession Review/Diff before final reporting when files/assets changed."
            };

            if (HasRisk(risks, "console_errors") || HasRisk(risks, "console_warnings"))
            {
                steps.Insert(0, "Resolve or account for existing console diagnostics before attributing issues to new changes.");
            }

            if (HasRisk(risks, "large_loaded_scene") || HasRisk(risks, "hierarchy_truncated_by_count"))
            {
                steps.Insert(0, "For hierarchy work, re-export or compare the full SceneHierarchyExport after edits.");
            }

            return steps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        static string BuildRiskSpecificHint(object[] risks)
        {
            if (HasRisk(risks, "editor_compiling"))
                return "Unity is compiling; wait for reload-safe readiness before code-dependent work.";

            if (HasRisk(risks, "asset_importing"))
                return "Unity is importing; wait before reading diagnostics or mutating assets.";

            if (HasRisk(risks, "prefab_stage_open"))
                return "Prefab Stage is open; explicitly choose prefab contents versus loaded scene targets.";

            if (HasRisk(risks, "large_loaded_scene") || HasRisk(risks, "hierarchy_truncated_by_count"))
                return "Scene scale/truncation detected; use full hierarchy export and objectId/indexedPath for structural edits.";

            if (HasRisk(risks, "console_errors") || HasRisk(risks, "console_warnings"))
                return "Console diagnostics exist; create/read markers around new work to separate old noise from new issues.";

            return "No special risk override detected; follow read-before-modify, dry-run, and verification ladder.";
        }

        static bool HasRisk(object[] risks, string code)
        {
            if (risks == null || string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            foreach (var risk in risks)
            {
                try
                {
                    var riskCode = JObject.FromObject(risk).Value<string>("code");
                    if (string.Equals(riskCode, code, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore malformed risk entries.
                }
            }

            return false;
        }

        static string BuildAgentOneLineSummary(object projectShape, object[] systems, object[] risks)
        {
            var shape = JObject.FromObject(projectShape);
            var totalAssets = shape.Value<int?>("totalAssets") ?? 0;
            var sceneScale = shape.Value<string>("sceneScale") ?? "unknown";
            var systemNames = systems
                .Select(system => JObject.FromObject(system).Value<string>("name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(4)
                .ToArray();
            var riskCount = risks?.Length ?? 0;
            var systemsText = systemNames.Length > 0 ? string.Join(", ", systemNames) : "no major packages detected";
            return $"Project has {totalAssets} Assets/ files, {sceneScale} loaded-scene scale, key signals: {systemsText}; risk flags: {riskCount}.";
        }

        sealed class LoadedSceneBrief
        {
            public string Name;
            public string Path;
            public int RootCount;
            public bool IsDirty;
        }

        sealed class FolderCandidate
        {
            public string Path;
            public int Depth;
            public int Score;
        }

        sealed class SystemSignal
        {
            public string Name;
            public string Source;
            public string Detail;
        }

        sealed class AgentRiskFlag
        {
            public AgentRiskFlag(string severity, string code, string message, string nextCall)
            {
                Severity = severity;
                Code = code;
                Message = message;
                NextCall = nextCall;
            }

            public string Severity;
            public string Code;
            public string Message;
            public string NextCall;
        }
    }
}
