// Transient LIVE regression fixture. Tools~ is ignored by Unity. Install a guarded copy
// inside the test project's UniBridge Editor assembly only while qualifying delivery.
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cidonix.UniBridge.MCP.Editor.CommandReplayQualification
{
    public sealed class ProbeParameters
    {
        public string RunId { get; set; }
        public string CaseId { get; set; }
        public string Mode { get; set; } = "Complete";
        public int DelayMs { get; set; } = 2500;
    }

    public static class LiveProbeFixture
    {
        const string Prefix = "Cidonix.UniBridge.CommandReplayProbe.";
        static readonly string FixtureDomainId = Guid.NewGuid().ToString("N");
        static readonly CancellationTokenSource DomainEndCancellation = new CancellationTokenSource();
        static string activeReloadRun;

        static LiveProbeFixture()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (!string.IsNullOrEmpty(activeReloadRun))
                {
                    var reloadKey = Prefix + activeReloadRun + ".ReloadEvents";
                    SessionState.SetInt(reloadKey, SessionState.GetInt(reloadKey, 0) + 1);
                }
                DomainEndCancellation.Cancel();
            };
        }

        [McpTool("UniBridge_CommandReplayProbe", "Transient qualification fixture: create one task-owned object.",
            ExecutionPolicy = ToolExecutionPolicy.Mutating, EnabledByDefault = true)]
        public static async Task<object> Mutate(ProbeParameters args)
        {
            Validate(args, true);
            var run = args.RunId;
            var key = Prefix + run + "." + args.CaseId;
            var count = SessionState.GetInt(key, 0) + 1;
            var ownedScene = EnsureOwnedScene(run);
            var previousActive = SceneManager.GetActiveScene();
            GameObject created;
            try
            {
                SceneManager.SetActiveScene(ownedScene);
                created = new GameObject("CommandReplayProbe_" + args.CaseId + "_" + count);
            }
            finally
            {
                if (previousActive.IsValid() && previousActive.isLoaded)
                    SceneManager.SetActiveScene(previousActive);
            }
            SessionState.SetInt(key, count);
            var casesKey = Prefix + run + ".Cases";
            var cases = JArray.Parse(SessionState.GetString(casesKey, "[]"));
            if (!Contains(cases, args.CaseId))
            {
                cases.Add(args.CaseId);
                SessionState.SetString(casesKey, cases.ToString(Newtonsoft.Json.Formatting.None));
            }
            var marker = new JObject
            {
                ["runId"] = run, ["caseId"] = args.CaseId, ["count"] = count,
                ["objectId"] = UnityApiAdapter.GetObjectId(created), ["sceneHandle"] = ownedScene.handle.ToString(),
                ["mode"] = args.Mode, ["startedUtc"] = DateTime.UtcNow.ToString("O")
            };
            var directory = MarkerDirectory(run);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, args.CaseId + ".json"), marker.ToString());

            if (string.Equals(args.Mode, "ThrowAfterEffect", StringComparison.Ordinal))
                throw new InvalidOperationException("Intentional regression exception after the side effect.");
            if (string.Equals(args.Mode, "Hold", StringComparison.Ordinal))
                await Task.Delay(Math.Max(500, Math.Min(10000, args.DelayMs)), DomainEndCancellation.Token);
            if (string.Equals(args.Mode, "Reload", StringComparison.Ordinal))
            {
                activeReloadRun = run;
                // Request a real forced compilation directly on the main thread. A
                // delayCall-only RequestScriptReload may be deferred/no-op in a busy
                // Editor, which cannot qualify persistence across an actual reload.
                CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
                // Bridge registers its before-reload hook before this transient fixture;
                // it deactivates the journal before this pending task is canceled.
                await Task.Delay(90000, DomainEndCancellation.Token);
                throw new InvalidOperationException("Expected qualification domain reload did not occur.");
            }
            return Response.Success("One fixture object created.", marker);
        }

        [McpTool("UniBridge_CommandReplayProbeStatus", "Read transient qualification counter and scene state.",
            ExecutionPolicy = ToolExecutionPolicy.Observer, ReplaySafe = true, EnabledByDefault = true)]
        public static object Status(ProbeParameters args)
        {
            Validate(args, false);
            var owned = GetOwnedScene(args.RunId);
            return Response.Success("Qualification fixture status.", new JObject
            {
                ["runId"] = args.RunId, ["caseId"] = args.CaseId,
                ["count"] = string.IsNullOrEmpty(args.CaseId) ? 0 : SessionState.GetInt(Prefix + args.RunId + "." + args.CaseId, 0),
                ["ownedSceneLoaded"] = owned.IsValid() && owned.isLoaded,
                ["ownedSceneRoots"] = owned.IsValid() && owned.isLoaded ? owned.rootCount : 0,
                ["fixtureDomainId"] = FixtureDomainId,
                ["reloadEvents"] = SessionState.GetInt(Prefix + args.RunId + ".ReloadEvents", 0),
                ["snapshot"] = Snapshot(owned)
            });
        }

        [McpTool("UniBridge_CommandReplayUncertifiedRead", "Transient fixture: intentionally unsafe read-only scheduler declaration with one task-owned side effect.",
            ExecutionPolicy = ToolExecutionPolicy.ReadOnly, ReplaySafe = false, EnabledByDefault = true)]
        public static Task<object> UncertifiedRead(ProbeParameters args)
        {
            // Scheduling classification alone is not a replay or durability contract.
            // This intentionally models exporters/custom tools with side effects.
            return Mutate(args);
        }

        [McpTool("UniBridge_CommandReplayProbeCleanup", "Close only the task-owned qualification scene and remove its markers.",
            ExecutionPolicy = ToolExecutionPolicy.Mutating, EnabledByDefault = true)]
        public static object Cleanup(ProbeParameters args)
        {
            Validate(args, false);
            var key = Prefix + args.RunId;
            var owned = GetOwnedScene(args.RunId);
            if (owned.IsValid() && owned.isLoaded)
            {
                if (!string.Equals(owned.name, "UniBridge_CommandReplay_" + args.RunId, StringComparison.Ordinal) || !string.IsNullOrEmpty(owned.path))
                    throw new InvalidOperationException("Fixture ownership no longer matches; refusing scene cleanup.");
                if (!EditorSceneManager.CloseScene(owned, true))
                    throw new InvalidOperationException("Could not close task-owned regression scene.");
            }
            var cases = JArray.Parse(SessionState.GetString(key + ".Cases", "[]"));
            foreach (var item in cases)
                SessionState.EraseInt(key + "." + item.Value<string>());
            SessionState.EraseString(key + ".Scene");
            SessionState.EraseString(key + ".Cases");
            SessionState.EraseInt(key + ".ReloadEvents");
            var directory = MarkerDirectory(args.RunId);
            if (Directory.Exists(directory))
            {
                var parent = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "UniBridge", "CommandReplayProbe"));
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), parent, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Refusing fixture marker cleanup outside its owned directory.");
                Directory.Delete(directory, true);
            }
            return Response.Success("Task-owned qualification scene and markers removed.", new JObject { ["snapshot"] = Snapshot(default(Scene)) });
        }

        static Scene EnsureOwnedScene(string run)
        {
            var key = Prefix + run + ".Scene";
            var scene = GetOwnedScene(run);
            if (scene.IsValid() && scene.isLoaded)
                return scene;
            var originalActive = SceneManager.GetActiveScene();
            var originalSelection = Selection.objects;
            var originalActiveObject = Selection.activeObject;
            try
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                scene.name = "UniBridge_CommandReplay_" + run;
                SessionState.SetString(key, scene.handle.ToString());
                return scene;
            }
            finally
            {
                if (originalActive.IsValid() && originalActive.isLoaded)
                    SceneManager.SetActiveScene(originalActive);
                Selection.objects = originalSelection;
                Selection.activeObject = originalActiveObject;
            }
        }

        static JObject Snapshot(Scene owned)
        {
            var scenes = new JArray();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (owned.IsValid() && scene.handle == owned.handle)
                    continue;
                scenes.Add(new JObject
                {
                    ["handle"] = scene.handle.ToString(), ["name"] = scene.name, ["path"] = scene.path,
                    ["dirty"] = scene.isDirty, ["loaded"] = scene.isLoaded, ["rootCount"] = scene.rootCount
                });
            }
            var selectionIds = new JArray();
            foreach (var selected in Selection.objects)
                selectionIds.Add(UnityApiAdapter.GetObjectId(selected));
            return new JObject
            {
                ["scenes"] = scenes,
                ["activeSceneHandle"] = SceneManager.GetActiveScene().handle.ToString(),
                ["selectionIds"] = selectionIds
            };
        }

        static bool Contains(JArray array, string value)
        {
            foreach (var item in array)
                if (string.Equals(item.Value<string>(), value, StringComparison.Ordinal))
                    return true;
            return false;
        }

        static Scene GetOwnedScene(string run)
        {
            var handle = SessionState.GetString(Prefix + run + ".Scene", string.Empty);
            if (string.IsNullOrEmpty(handle))
                return default(Scene);
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (string.Equals(scene.handle.ToString(), handle, StringComparison.Ordinal))
                    return scene;
            }
            return default(Scene);
        }

        static string MarkerDirectory(string run) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "UniBridge", "CommandReplayProbe", run));

        static void Validate(ProbeParameters args, bool requireCase)
        {
            if (args == null || !Regex.IsMatch(args.RunId ?? string.Empty, "^[a-zA-Z0-9_-]{1,80}$") ||
                ((requireCase || !string.IsNullOrEmpty(args.CaseId)) && !Regex.IsMatch(args.CaseId ?? string.Empty, "^[a-zA-Z0-9_-]{1,80}$")))
                throw new ArgumentException("RunId and CaseId must be task-owned alphanumeric IDs.");
        }
    }
}
