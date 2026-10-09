using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace Cidonix.UniBridge.Legacy
{
    internal static class UniBridgeLegacyTools
    {
        internal static List<object> BuildDescriptors()
        {
            List<object> tools = new List<object>();
            tools.Add(Descriptor(
                "UniBridge_Discover",
                "UniBridge Legacy discovery and health",
                "Ping or inspect the dependency-free legacy Unity adapter. Actions: Ping, Status."));
            tools.Add(Descriptor(
                "UniBridge_ContextSnapshot",
                "Compact Unity project context",
                "Return project identity, Editor state, active scene summary, and compact console diagnostics."));
            tools.Add(Descriptor(
                "UniBridge_ManageEditor",
                "Manage legacy Unity Editor lifecycle",
                "Inspect Editor state or run basic lifecycle actions. Actions: GetState, RefreshAssets, SaveAssets, SaveAll, Play, ExitPlayMode, Pause, Resume, Undo, Redo, GetCompilationDiagnostics, WaitForReady, WaitIdle, WaitForReadyAfterReload. Legacy readiness actions are instantaneous probes with waitSupported=false: they succeed only when the Editor is ready now, otherwise return failure without blocking the Unity main thread. RequireNotPlaying additionally requires Edit Mode without a pending Play Mode transition."));
            tools.Add(Descriptor(
                "UniBridge_ReadConsole",
                "Read legacy Unity console diagnostics",
                "Read adapter-captured Console messages. Actions: DiagnosticSummary, Get, Clear. Parameters: MaxEntries, IncludeStackTrace."));
            tools.Add(Descriptor(
                "UniBridge_ManageScene",
                "Inspect and manage legacy Unity scenes",
                "Actions: GetOpenScenes, Save, SaveAll, Open, New, SetActive, Close. Prefer SceneId for live scene identity. New defaults to an additive empty scene; Close refuses dirty scenes unless DiscardUnsavedChanges=true."));
            tools.Add(Descriptor(
                "UniBridge_SceneObjectView",
                "Inspect legacy Unity scene hierarchy",
                "Snapshot a complete or bounded hierarchy. Parameters: Target, IncludeInactive, IncludeComponents, MaxObjects, MaxDepth."));
            tools.Add(Descriptor(
                "UniBridge_CaptureView",
                "Capture legacy Unity views to PNG",
                "Actions: Capabilities, CaptureSceneView, CaptureGameCamera, CaptureGameView, GetCaptureStatus, ListCaptures, ListCameras. Scene View and camera captures are immediate; exact Game View capture is queued and polled without blocking the legacy Unity main thread."));
            tools.Add(Descriptor(
                "UniBridge_ManageGameObject",
                "Find and edit legacy Unity scene objects",
                "Actions: Find, Create, Modify, Delete, GetComponents, GetComponent, AddComponent, RemoveComponent, SetComponentProperty. Mutations support DryRun, are blocked in Play Mode unless AllowPlayModeWrite=true, and should target live ObjectId/ComponentObjectId identities."));
            tools.Add(Descriptor(
                "UniBridge_AssetIntelligence",
                "Search and inspect legacy Unity assets",
                "Actions: Search, Inspect, ReadText. Parameters: Query, AssetPath, Folders, MaxResults, MaxCharacters."));
            return tools;
        }

        internal static object Execute(string toolName, Dictionary<string, object> parameters)
        {
            if (String.Equals(toolName, "UniBridge_Discover", StringComparison.OrdinalIgnoreCase))
                return Discover(parameters);
            if (String.Equals(toolName, "UniBridge_ContextSnapshot", StringComparison.OrdinalIgnoreCase))
                return ContextSnapshot(parameters);
            if (String.Equals(toolName, "UniBridge_ManageEditor", StringComparison.OrdinalIgnoreCase))
                return ManageEditor(parameters);
            if (String.Equals(toolName, "UniBridge_ReadConsole", StringComparison.OrdinalIgnoreCase))
                return ReadConsole(parameters);
            if (String.Equals(toolName, "UniBridge_ManageScene", StringComparison.OrdinalIgnoreCase))
                return ManageScene(parameters);
            if (String.Equals(toolName, "UniBridge_SceneObjectView", StringComparison.OrdinalIgnoreCase))
                return SceneObjectView(parameters);
            if (String.Equals(toolName, "UniBridge_CaptureView", StringComparison.OrdinalIgnoreCase))
                return UniBridgeLegacyCapture.Execute(parameters);
            if (String.Equals(toolName, "UniBridge_ManageGameObject", StringComparison.OrdinalIgnoreCase))
                return ManageGameObject(parameters);
            if (String.Equals(toolName, "UniBridge_AssetIntelligence", StringComparison.OrdinalIgnoreCase))
                return AssetIntelligence(parameters);
            throw new InvalidOperationException("Unknown UniBridge legacy tool '" + toolName + "'.");
        }

        private static Dictionary<string, object> Descriptor(string name, string title, string description)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>();
            properties["Action"] = StringSchema("Operation name.");
            properties["Target"] = StringSchema("Hierarchy path, object name, or target selector.");
            properties["ObjectId"] = IntegerSchema("Unity instance ID.");
            properties["AssetPath"] = StringSchema("Unity project-relative asset path.");
            properties["ScenePath"] = StringSchema("Unity project-relative scene path.");
            properties["SceneId"] = IntegerSchema("Adapter-session identity for a loaded Unity Scene.");
            properties["SceneHandle"] = IntegerSchema("Backward-compatible alias for SceneId.");
            properties["Component"] = StringSchema("Component short or fully-qualified type name.");
            properties["ComponentName"] = StringSchema("Alias for Component.");
            properties["ComponentObjectId"] = IntegerSchema("Live Unity Component instance ID.");
            properties["ComponentIndex"] = IntegerSchema("Zero-based index among components matching Component.");
            properties["PropertyPath"] = StringSchema("Exact SerializedProperty path for SetComponentProperty.");
            properties["Value"] = AnySchema("JSON value for PropertyPath.");
            properties["Properties"] = ObjectSchema("Map of exact SerializedProperty paths to JSON values.");
            properties["DryRun"] = BooleanSchema("Validate and describe a mutation without changing Unity state.");
            properties["AllowPlayModeWrite"] = BooleanSchema("Explicitly allow an ephemeral Play Mode mutation. Default false.");
            properties["IncludeSerializedProperties"] = BooleanSchema("Include bounded serialized component-property details.");
            properties["IncludeHidden"] = BooleanSchema("Include hidden serialized properties. Default false.");
            properties["MaxProperties"] = IntegerSchema("Maximum serialized properties returned per component.");
            properties["DiscardUnsavedChanges"] = BooleanSchema("Explicitly allow closing a dirty scene without saving it.");
            properties["CaptureId"] = StringSchema("Opaque adapter-session capture identity returned by CaptureGameView.");
            properties["Width"] = IntegerSchema("PNG width for immediate captures, clamped to 64..4096.");
            properties["Height"] = IntegerSchema("PNG height for immediate captures, clamped to 64..4096.");
            properties["SuperSize"] = IntegerSchema("Exact Game View supersampling multiplier, clamped to 1..4.");
            properties["TimeoutMs"] = IntegerSchema("Exact Game View file timeout, clamped to 1000..30000 milliseconds.");
            properties["RequireNotPlaying"] = BooleanSchema("For legacy readiness probes, also require Edit Mode without a pending Play Mode transition. The adapter does not wait inline or consume TimeoutMs.");
            properties["RestoreFocus"] = BooleanSchema("Restore the previously focused Unity window after exact Game View capture.");
            properties["CreateViewIfMissing"] = BooleanSchema("Allow creation of a missing Scene View or Game View window. Default false.");
            properties["Label"] = StringSchema("Optional human-readable filename label; sanitized by the adapter.");

            Dictionary<string, object> schema = new Dictionary<string, object>();
            schema["type"] = "object";
            schema["properties"] = properties;
            schema["additionalProperties"] = true;

            Dictionary<string, object> annotations = new Dictionary<string, object>();
            annotations["legacyUnityCompatibility"] = true;
            annotations["unityVersion"] = Application.unityVersion;
            annotations["minimumUnityVersion"] = UniBridgeLegacyHost.MinimumUnityVersion;
            annotations["compatibilityProfile"] = UniBridgeLegacyHost.CompatibilityProfile;

            Dictionary<string, object> descriptor = new Dictionary<string, object>();
            descriptor["name"] = name;
            descriptor["title"] = title;
            descriptor["description"] = description;
            descriptor["inputSchema"] = schema;
            descriptor["annotations"] = annotations;
            return descriptor;
        }

        private static Dictionary<string, object> StringSchema(string description)
        {
            Dictionary<string, object> schema = new Dictionary<string, object>();
            schema["type"] = "string";
            schema["description"] = description;
            return schema;
        }

        private static Dictionary<string, object> IntegerSchema(string description)
        {
            Dictionary<string, object> schema = new Dictionary<string, object>();
            schema["type"] = "integer";
            schema["description"] = description;
            return schema;
        }

        private static Dictionary<string, object> BooleanSchema(string description)
        {
            Dictionary<string, object> schema = new Dictionary<string, object>();
            schema["type"] = "boolean";
            schema["description"] = description;
            return schema;
        }

        private static Dictionary<string, object> ObjectSchema(string description)
        {
            Dictionary<string, object> schema = new Dictionary<string, object>();
            schema["type"] = "object";
            schema["description"] = description;
            schema["additionalProperties"] = true;
            return schema;
        }

        private static Dictionary<string, object> AnySchema(string description)
        {
            Dictionary<string, object> schema = new Dictionary<string, object>();
            schema["description"] = description;
            return schema;
        }

        private static Dictionary<string, object> Discover(Dictionary<string, object> parameters)
        {
            string action = Action(parameters, "Ping");
            Dictionary<string, object> result = Result("UniBridge Legacy is online.");
            result["action"] = action;
            result["online"] = true;
            result["protocol"] = UniBridgeLegacyHost.ProtocolVersion;
            result["adapterVersion"] = UniBridgeLegacyHost.AdapterVersion;
            result["compatibilityProfile"] = UniBridgeLegacyHost.CompatibilityProfile;
            result["adapterFamily"] = UniBridgeLegacyHost.AdapterFamily;
            result["minimumUnityVersion"] = UniBridgeLegacyHost.MinimumUnityVersion;
            result["discoveryFile"] = UniBridgeLegacyHost.DiscoveryPath;
            result["editor"] = BuildEditorState();
            return result;
        }

        private static Dictionary<string, object> ContextSnapshot(Dictionary<string, object> parameters)
        {
            Dictionary<string, object> result = Result("Compact legacy Unity project context captured.");
            result["project"] = UniBridgeLegacyHost.BuildProjectContext();
            result["editor"] = BuildEditorState();
            result["scene"] = BuildSceneSummary(UnitySceneManager.GetActiveScene());
            result["console"] = UniBridgeLegacyConsole.BuildSummary();
            return result;
        }

        private static Dictionary<string, object> ManageEditor(Dictionary<string, object> parameters)
        {
            string action = Action(parameters, "GetState");
            if (EqualsAction(action, "RefreshAssets"))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                Dictionary<string, object> refreshed = Result("AssetDatabase refresh requested.");
                refreshed["editor"] = BuildEditorState();
                refreshed["reloadBoundaryPossible"] = true;
                return refreshed;
            }
            if (EqualsAction(action, "SaveAssets"))
            {
                AssetDatabase.SaveAssets();
                return Result("Assets saved.");
            }
            if (EqualsAction(action, "SaveAll"))
            {
                bool scenesSaved = EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();
                Dictionary<string, object> saved = Result("Open scenes and assets saved.");
                saved["scenesSaved"] = scenesSaved;
                return saved;
            }
            if (EqualsAction(action, "Play"))
            {
                EditorApplication.isPlaying = true;
                Dictionary<string, object> play = Result("Play Mode requested.");
                play["reloadBoundaryPossible"] = true;
                return play;
            }
            if (EqualsAction(action, "ExitPlayMode"))
            {
                EditorApplication.isPlaying = false;
                Dictionary<string, object> edit = Result("Exit Play Mode requested.");
                edit["reloadBoundaryPossible"] = true;
                return edit;
            }
            if (EqualsAction(action, "Pause"))
            {
                EditorApplication.isPaused = true;
                return Result("Editor paused.");
            }
            if (EqualsAction(action, "Resume"))
            {
                EditorApplication.isPaused = false;
                return Result("Editor resumed.");
            }
            if (EqualsAction(action, "Undo"))
            {
                if (EditorApplication.isPlaying)
                    throw new InvalidOperationException("Editor Undo through UniBridge is available only in Edit Mode.");
                Undo.PerformUndo();
                Dictionary<string, object> undone = Result("Editor Undo performed.");
                undone["editor"] = BuildEditorState();
                undone["activeScene"] = BuildSceneSummary(UnitySceneManager.GetActiveScene());
                return undone;
            }
            if (EqualsAction(action, "Redo"))
            {
                if (EditorApplication.isPlaying)
                    throw new InvalidOperationException("Editor Redo through UniBridge is available only in Edit Mode.");
                Undo.PerformRedo();
                Dictionary<string, object> redone = Result("Editor Redo performed.");
                redone["editor"] = BuildEditorState();
                redone["activeScene"] = BuildSceneSummary(UnitySceneManager.GetActiveScene());
                return redone;
            }
            if (EqualsAction(action, "GetCompilationDiagnostics"))
            {
                Dictionary<string, object> diagnostics = Result("Legacy Unity compilation diagnostics collected from the live Console buffer.");
                diagnostics["isCompiling"] = EditorApplication.isCompiling;
                diagnostics["isUpdating"] = EditorApplication.isUpdating;
                diagnostics["console"] = UniBridgeLegacyConsole.BuildSummary();
                diagnostics["note"] = "Legacy Unity does not expose the modern CompilationPipeline surface used by the full package; captured Console errors remain authoritative.";
                return diagnostics;
            }
            bool readinessProbe = EqualsAction(action, "WaitForReady") || EqualsAction(action, "WaitIdle") || EqualsAction(action, "WaitForReadyAfterReload");
            if (!EqualsAction(action, "GetState") && !EqualsAction(action, "Status") && !readinessProbe)
                throw new InvalidOperationException("Unsupported ManageEditor action '" + action + "'.");

            bool requireNotPlaying = UniBridgeLegacyValue.GetBool(parameters, "RequireNotPlaying", false);
            Dictionary<string, object> editor = BuildEditorState();
            bool ready = UniBridgeLegacyValue.GetBool(editor, "ready", false) &&
                (!requireNotPlaying || (!UniBridgeLegacyValue.GetBool(editor, "isPlaying", false) &&
                    !UniBridgeLegacyValue.GetBool(editor, "isPlayingOrWillChangePlaymode", false)));
            Dictionary<string, object> readiness = new Dictionary<string, object>(editor);
            readiness["ready"] = ready;
            readiness["isReady"] = ready;
            readiness["requireNotPlaying"] = requireNotPlaying;
            readiness["elapsedMs"] = 0;

            Dictionary<string, object> state = Result(readinessProbe
                ? ready ? "Legacy Unity Editor readiness verified by an instantaneous probe."
                    : "Legacy Unity Editor is not ready. Inline waiting is unsupported; readiness was not verified."
                : "Editor state captured.");
            state["editor"] = editor;
            state["ready"] = ready;
            state["readiness"] = readiness;
            if (readinessProbe)
            {
                state["success"] = ready;
                state["status"] = ready ? "ready" : "not_ready";
                state["completed"] = ready;
                state["waitSucceeded"] = ready;
                state["waitSupported"] = false;
                state["waitedMs"] = 0;
                state["timedOut"] = false;
                state["timeoutConsumed"] = false;
                state["compilationHealthVerified"] = false;
                state["requestedTimeoutMs"] = UniBridgeLegacyValue.Get(parameters, "TimeoutMs");
                state["action"] = action;
                state["note"] = "The legacy adapter checks current readiness without blocking the Unity main thread. TimeoutMs is not consumed; observe or probe again before continuing.";
                if (!ready)
                {
                    List<string> reasons = new List<string>();
                    if (UniBridgeLegacyValue.GetBool(editor, "isCompiling", false)) reasons.Add("compiling");
                    if (UniBridgeLegacyValue.GetBool(editor, "isUpdating", false)) reasons.Add("updating");
                    if (requireNotPlaying && UniBridgeLegacyValue.GetBool(editor, "isPlaying", false)) reasons.Add("playing");
                    if (requireNotPlaying && UniBridgeLegacyValue.GetBool(editor, "isPlayingOrWillChangePlaymode", false)) reasons.Add("play_mode_transition_or_playing");
                    state["notReadyReasons"] = reasons.ToArray();
                    state["nextSuggestedCalls"] = new string[] { "UniBridge_ManageEditor Action=GetState", "UniBridge_ManageEditor Action=" + action + " RequireNotPlaying=" + (requireNotPlaying ? "true" : "false") };
                }
            }
            return state;
        }

        private static Dictionary<string, object> ReadConsole(Dictionary<string, object> parameters)
        {
            string action = Action(parameters, "DiagnosticSummary");
            if (EqualsAction(action, "Clear"))
            {
                UniBridgeLegacyConsole.Clear();
                return Result("Console history cleared.");
            }
            if (EqualsAction(action, "Get") || EqualsAction(action, "Read"))
            {
                int maxEntries = Clamp(UniBridgeLegacyValue.GetInt(parameters, "MaxEntries", 100), 1, 1000);
                bool includeStackTrace = UniBridgeLegacyValue.GetBool(parameters, "IncludeStackTrace", false);
                Dictionary<string, object> entries = Result("Console entries read.");
                entries["summary"] = UniBridgeLegacyConsole.BuildSummary();
                entries["entries"] = UniBridgeLegacyConsole.GetEntries(maxEntries, includeStackTrace);
                return entries;
            }
            if (!EqualsAction(action, "DiagnosticSummary") && !EqualsAction(action, "Summary"))
                throw new InvalidOperationException("Unsupported ReadConsole action '" + action + "'.");

            Dictionary<string, object> summary = Result("Console diagnostic summary captured.");
            summary["diagnostics"] = UniBridgeLegacyConsole.BuildSummary();
            return summary;
        }

        private static Dictionary<string, object> ManageScene(Dictionary<string, object> parameters)
        {
            string action = Action(parameters, "GetOpenScenes");
            if (EqualsAction(action, "GetOpenScenes") || EqualsAction(action, "Inspect"))
            {
                List<object> scenes = new List<object>();
                for (int index = 0; index < UnitySceneManager.sceneCount; index++)
                    scenes.Add(BuildSceneSummary(UnitySceneManager.GetSceneAt(index)));
                Dictionary<string, object> result = Result("Open scenes inspected.");
                result["activeScenePath"] = UnitySceneManager.GetActiveScene().path;
                result["sceneCount"] = scenes.Count;
                result["scenes"] = scenes;
                return result;
            }
            if (EqualsAction(action, "SaveAll"))
            {
                EnsureEditMode("SaveAll scenes");
                bool saved = EditorSceneManager.SaveOpenScenes();
                Dictionary<string, object> result = Result("Open scenes saved.");
                result["saved"] = saved;
                return result;
            }
            if (EqualsAction(action, "Save"))
            {
                EnsureEditMode("Save scene");
                Scene scene = ResolveScene(parameters, true);
                if (!scene.IsValid() || !scene.isLoaded)
                    throw new InvalidOperationException("Scene is not open.");
                bool saved = EditorSceneManager.SaveScene(scene);
                Dictionary<string, object> result = Result("Scene saved.");
                result["scene"] = BuildSceneSummary(scene);
                result["saved"] = saved;
                return result;
            }
            if (EqualsAction(action, "Open"))
            {
                EnsureEditMode("Open scene");
                string scenePath = RequireString(parameters, "ScenePath");
                if (!File.Exists(ToAbsoluteAssetPath(scenePath)))
                    throw new FileNotFoundException("Scene asset does not exist.", scenePath);
                string modeName = UniBridgeLegacyValue.GetString(parameters, "Mode", "Single");
                OpenSceneMode mode = String.Equals(modeName, "Additive", StringComparison.OrdinalIgnoreCase)
                    ? OpenSceneMode.Additive
                    : OpenSceneMode.Single;
                if (mode == OpenSceneMode.Single && HasDirtyOpenScenes())
                    throw new InvalidOperationException("Open Mode=Single is blocked while any open scene has unsaved changes.");
                if (UniBridgeLegacyValue.GetBool(parameters, "DryRun", false))
                {
                    Dictionary<string, object> preview = Result("Scene open validated without changing Editor state.");
                    preview["dryRun"] = true;
                    preview["scenePath"] = scenePath;
                    preview["mode"] = mode.ToString();
                    return preview;
                }
                Scene scene = EditorSceneManager.OpenScene(scenePath, mode);
                Dictionary<string, object> result = Result("Scene opened.");
                result["scene"] = BuildSceneSummary(scene);
                return result;
            }
            if (EqualsAction(action, "New"))
            {
                EnsureEditMode("Create scene");
                string modeName = UniBridgeLegacyValue.GetString(parameters, "Mode", "Additive");
                NewSceneMode mode = String.Equals(modeName, "Single", StringComparison.OrdinalIgnoreCase)
                    ? NewSceneMode.Single
                    : NewSceneMode.Additive;
                if (mode == NewSceneMode.Single && HasDirtyOpenScenes())
                    throw new InvalidOperationException("New Mode=Single is blocked while any open scene has unsaved changes.");
                string setupName = UniBridgeLegacyValue.GetString(parameters, "Setup", "Empty");
                NewSceneSetup setup = String.Equals(setupName, "Default", StringComparison.OrdinalIgnoreCase) ||
                                      String.Equals(setupName, "DefaultGameObjects", StringComparison.OrdinalIgnoreCase)
                    ? NewSceneSetup.DefaultGameObjects
                    : NewSceneSetup.EmptyScene;
                bool dryRun = UniBridgeLegacyValue.GetBool(parameters, "DryRun", false);
                if (dryRun)
                {
                    Dictionary<string, object> preview = Result("New scene validated without changing Editor state.");
                    preview["dryRun"] = true;
                    preview["mode"] = mode.ToString();
                    preview["setup"] = setup.ToString();
                    return preview;
                }

                Scene previous = UnitySceneManager.GetActiveScene();
                Scene created = EditorSceneManager.NewScene(setup, mode);
                UnitySceneManager.SetActiveScene(created);
                Dictionary<string, object> result = Result("New scene created and made active.");
                result["scene"] = BuildSceneSummary(created);
                result["previousActiveScene"] = BuildSceneSummary(previous);
                result["saved"] = false;
                return result;
            }
            if (EqualsAction(action, "SetActive"))
            {
                EnsureEditMode("Set active scene");
                Scene scene = ResolveScene(parameters, false);
                if (!scene.IsValid() || !scene.isLoaded)
                    throw new InvalidOperationException("Scene is not loaded.");
                bool changed = UnitySceneManager.SetActiveScene(scene);
                Dictionary<string, object> result = Result("Active scene selected.");
                result["changed"] = changed;
                result["scene"] = BuildSceneSummary(scene);
                return result;
            }
            if (EqualsAction(action, "Close"))
            {
                EnsureEditMode("Close scene");
                Scene scene = ResolveScene(parameters, false);
                if (!scene.IsValid() || !scene.isLoaded)
                    throw new InvalidOperationException("Scene is not loaded.");
                if (UnitySceneManager.sceneCount <= 1)
                    throw new InvalidOperationException("The last open scene cannot be closed through the legacy bridge.");
                bool discard = UniBridgeLegacyValue.GetBool(parameters, "DiscardUnsavedChanges", false);
                if (scene.isDirty && !discard)
                    throw new InvalidOperationException("Scene has unsaved changes. Save it first or pass DiscardUnsavedChanges=true explicitly.");
                if (UniBridgeLegacyValue.GetBool(parameters, "DryRun", false))
                {
                    Dictionary<string, object> preview = Result("Scene close validated without changing Editor state.");
                    preview["dryRun"] = true;
                    preview["scene"] = BuildSceneSummary(scene);
                    preview["wouldDiscardUnsavedChanges"] = scene.isDirty;
                    return preview;
                }

                Dictionary<string, object> before = BuildSceneSummary(scene);
                if (scene == UnitySceneManager.GetActiveScene())
                {
                    for (int index = 0; index < UnitySceneManager.sceneCount; index++)
                    {
                        Scene candidate = UnitySceneManager.GetSceneAt(index);
                        if (candidate.IsValid() && candidate.isLoaded && candidate != scene)
                        {
                            UnitySceneManager.SetActiveScene(candidate);
                            break;
                        }
                    }
                }
                bool closed = EditorSceneManager.CloseScene(scene, true);
                Dictionary<string, object> result = Result("Scene closed.");
                result["closed"] = closed;
                result["before"] = before;
                result["activeScene"] = BuildSceneSummary(UnitySceneManager.GetActiveScene());
                return result;
            }
            throw new InvalidOperationException("Unsupported ManageScene action '" + action + "'.");
        }

        private static Dictionary<string, object> SceneObjectView(Dictionary<string, object> parameters)
        {
            string action = Action(parameters, "Snapshot");
            if (!EqualsAction(action, "Snapshot") && !EqualsAction(action, "Inspect") && !EqualsAction(action, "Hierarchy"))
                throw new InvalidOperationException("Unsupported SceneObjectView action '" + action + "'.");

            bool includeInactive = UniBridgeLegacyValue.GetBool(parameters, "IncludeInactive", true);
            bool includeComponents = UniBridgeLegacyValue.GetBool(parameters, "IncludeComponents", true);
            int maxObjects = Clamp(UniBridgeLegacyValue.GetInt(parameters, "MaxObjects", 2000), 1, 20000);
            int maxDepth = Clamp(UniBridgeLegacyValue.GetInt(parameters, "MaxDepth", 32), 0, 256);
            string target = UniBridgeLegacyValue.GetString(parameters, "Target", null);

            List<object> objects = new List<object>();
            int totalVisited = 0;
            bool truncated = false;
            if (!String.IsNullOrEmpty(target))
            {
                GameObject root = ResolveOne(parameters, true);
                AddHierarchy(root, 0, includeInactive, includeComponents, maxObjects, maxDepth, objects, ref totalVisited, ref truncated);
            }
            else
            {
                Scene scene = UnitySceneManager.GetActiveScene();
                GameObject[] roots = scene.GetRootGameObjects();
                for (int index = 0; index < roots.Length && !truncated; index++)
                    AddHierarchy(roots[index], 0, includeInactive, includeComponents, maxObjects, maxDepth, objects, ref totalVisited, ref truncated);
            }

            Dictionary<string, object> result = Result("Scene hierarchy captured.");
            result["scene"] = BuildSceneSummary(UnitySceneManager.GetActiveScene());
            result["returnedObjects"] = objects.Count;
            result["visitedObjects"] = totalVisited;
            result["truncated"] = truncated;
            result["objects"] = objects;
            return result;
        }

        private static Dictionary<string, object> ManageGameObject(Dictionary<string, object> parameters)
        {
            string action = Action(parameters, "Find");
            if (EqualsAction(action, "Find") || EqualsAction(action, "Inspect"))
            {
                List<GameObject> matches = ResolveMany(parameters, UniBridgeLegacyValue.GetBool(parameters, "IncludeInactive", true));
                int maxResults = Clamp(UniBridgeLegacyValue.GetInt(parameters, "MaxResults", 100), 1, 1000);
                List<object> objects = new List<object>();
                for (int index = 0; index < matches.Count && index < maxResults; index++)
                    objects.Add(BuildGameObjectSnapshot(matches[index], true));
                Dictionary<string, object> found = Result("Found " + matches.Count + " matching GameObject(s).");
                found["matchCount"] = matches.Count;
                found["returnedCount"] = objects.Count;
                found["objects"] = objects;
                return found;
            }

            GameObject target;
            if (EqualsAction(action, "GetComponents"))
            {
                target = ResolveOne(parameters, true);
                bool includeProperties = UniBridgeLegacyValue.GetBool(parameters, "IncludeSerializedProperties", false);
                bool includeHidden = UniBridgeLegacyValue.GetBool(parameters, "IncludeHidden", false);
                int maxProperties = Clamp(UniBridgeLegacyValue.GetInt(parameters, "MaxProperties", 256), 1, 4096);
                List<object> components = UniBridgeLegacySerialized.BuildComponentDetails(
                    target,
                    includeProperties,
                    includeHidden,
                    maxProperties);
                Dictionary<string, object> result = Result("Components inspected.");
                result["gameObject"] = BuildGameObjectSnapshot(target, false);
                result["componentCount"] = components.Count;
                result["components"] = components;
                return result;
            }

            if (EqualsAction(action, "GetComponent"))
            {
                target = ResolveOne(parameters, true);
                Component component = UniBridgeLegacySerialized.ResolveComponent(target, parameters, true);
                bool includeProperties = UniBridgeLegacyValue.GetBool(parameters, "IncludeSerializedProperties", true);
                bool includeHidden = UniBridgeLegacyValue.GetBool(parameters, "IncludeHidden", false);
                int maxProperties = Clamp(UniBridgeLegacyValue.GetInt(parameters, "MaxProperties", 256), 1, 4096);
                Dictionary<string, object> result = Result("Component inspected.");
                result["gameObject"] = BuildGameObjectSnapshot(target, false);
                result["component"] = UniBridgeLegacySerialized.BuildComponentData(
                    component,
                    includeProperties,
                    includeHidden,
                    maxProperties);
                return result;
            }

            if (EqualsAction(action, "Create"))
            {
                EnsureMutationAllowed(parameters, "Create GameObject");
                string name = RequireString(parameters, "Name");
                GameObject parent = ResolveOptionalParent(parameters);
                ValidateRequestedComponents(parameters);
                bool dryRun = UniBridgeLegacyValue.GetBool(parameters, "DryRun", false);
                if (dryRun)
                {
                    Dictionary<string, object> preview = Result("GameObject creation validated without changing the scene.");
                    preview["dryRun"] = true;
                    preview["name"] = name;
                    preview["parent"] = parent == null ? null : BuildGameObjectSnapshot(parent, false);
                    preview["activeScene"] = BuildSceneSummary(UnitySceneManager.GetActiveScene());
                    return preview;
                }

                int undoGroup = BeginUndoGroup("UniBridge Create GameObject");
                try
                {
                    GameObject created = new GameObject(name);
                    if (!EditorApplication.isPlaying)
                        Undo.RegisterCreatedObjectUndo(created, "UniBridge Create GameObject");
                    if (parent != null)
                        created.transform.SetParent(parent.transform, UniBridgeLegacyValue.GetBool(parameters, "WorldPositionStays", false));
                    ApplyTransform(created.transform, parameters);

                    List<object> componentResults = AddRequestedComponents(created, parameters);
                    MarkSceneDirty(created);
                    CompleteUndoGroup(undoGroup);
                    Dictionary<string, object> result = Result("GameObject '" + name + "' created.");
                    result["gameObject"] = BuildGameObjectSnapshot(created, true);
                    result["componentResults"] = componentResults;
                    result["undoGroup"] = undoGroup < 0 ? (object)null : undoGroup;
                    result["persistent"] = !EditorApplication.isPlaying;
                    return result;
                }
                catch
                {
                    RollbackUndoGroup(undoGroup);
                    throw;
                }
            }

            target = ResolveOne(parameters, true);
            if (EqualsAction(action, "Modify"))
            {
                EnsureMutationAllowed(parameters, "Modify GameObject");
                Dictionary<string, object> before = BuildGameObjectSnapshot(target, true);
                bool dryRun = UniBridgeLegacyValue.GetBool(parameters, "DryRun", false);
                if (dryRun)
                {
                    Dictionary<string, object> preview = Result("GameObject modification validated without changing the scene.");
                    preview["dryRun"] = true;
                    preview["before"] = before;
                    return preview;
                }

                int undoGroup = BeginUndoGroup("UniBridge Modify GameObject");
                try
                {
                    if (!EditorApplication.isPlaying)
                    {
                        Undo.RecordObject(target, "UniBridge Modify GameObject");
                        Undo.RecordObject(target.transform, "UniBridge Modify Transform");
                    }

                    string name = UniBridgeLegacyValue.GetString(parameters, "Name", null);
                    if (!String.IsNullOrEmpty(name)) target.name = name;
                    if (UniBridgeLegacyValue.Get(parameters, "Active") != null)
                        target.SetActive(UniBridgeLegacyValue.GetBool(parameters, "Active", target.activeSelf));
                    if (UniBridgeLegacyValue.Get(parameters, "Layer") != null)
                        target.layer = UniBridgeLegacyValue.GetInt(parameters, "Layer", target.layer);
                    string tag = UniBridgeLegacyValue.GetString(parameters, "Tag", null);
                    if (!String.IsNullOrEmpty(tag)) target.tag = tag;

                    if (UniBridgeLegacyValue.Get(parameters, "Parent") != null || UniBridgeLegacyValue.Get(parameters, "ParentObjectId") != null)
                    {
                        GameObject parent = ResolveOptionalParent(parameters);
                        if (parent == target)
                            throw new InvalidOperationException("A GameObject cannot be parented to itself.");
                        target.transform.SetParent(parent == null ? null : parent.transform, UniBridgeLegacyValue.GetBool(parameters, "WorldPositionStays", true));
                    }
                    ApplyTransform(target.transform, parameters);
                    MarkSceneDirty(target);
                    CompleteUndoGroup(undoGroup);
                    Dictionary<string, object> result = Result("GameObject modified.");
                    result["before"] = before;
                    result["gameObject"] = BuildGameObjectSnapshot(target, true);
                    result["undoGroup"] = undoGroup < 0 ? (object)null : undoGroup;
                    return result;
                }
                catch
                {
                    RollbackUndoGroup(undoGroup);
                    throw;
                }
            }
            if (EqualsAction(action, "Delete"))
            {
                EnsureMutationAllowed(parameters, "Delete GameObject");
                Dictionary<string, object> before = BuildGameObjectSnapshot(target, true);
                if (UniBridgeLegacyValue.GetBool(parameters, "DryRun", false))
                {
                    Dictionary<string, object> preview = Result("GameObject deletion validated without changing the scene.");
                    preview["dryRun"] = true;
                    preview["before"] = before;
                    return preview;
                }
                Scene scene = target.scene;
                int undoGroup = BeginUndoGroup("UniBridge Delete GameObject");
                try
                {
                    if (EditorApplication.isPlaying)
                        UnityEngine.Object.Destroy(target);
                    else
                        Undo.DestroyObjectImmediate(target);
                    if (!EditorApplication.isPlaying && scene.IsValid())
                        EditorSceneManager.MarkSceneDirty(scene);
                    CompleteUndoGroup(undoGroup);
                    Dictionary<string, object> result = Result("GameObject deleted.");
                    result["before"] = before;
                    result["undoGroup"] = undoGroup < 0 ? (object)null : undoGroup;
                    return result;
                }
                catch
                {
                    RollbackUndoGroup(undoGroup);
                    throw;
                }
            }
            if (EqualsAction(action, "AddComponent"))
            {
                EnsureMutationAllowed(parameters, "Add Component");
                string componentName = UniBridgeLegacyValue.GetString(parameters, "Component", null);
                if (String.IsNullOrEmpty(componentName))
                    componentName = UniBridgeLegacyValue.GetString(parameters, "ComponentName", null);
                if (String.IsNullOrEmpty(componentName))
                    throw new InvalidOperationException("Component or ComponentName is required.");
                Type type = ResolveComponentType(componentName);
                if (type == null)
                    throw new InvalidOperationException("Component type was not found: " + componentName);
                if (UniBridgeLegacyValue.GetBool(parameters, "DryRun", false))
                {
                    Dictionary<string, object> preview = Result("Component addition validated without changing the scene.");
                    preview["dryRun"] = true;
                    preview["resolvedType"] = type.FullName;
                    preview["gameObject"] = BuildGameObjectSnapshot(target, false);
                    return preview;
                }
                int undoGroup = BeginUndoGroup("UniBridge Add Component");
                try
                {
                    Component component = EditorApplication.isPlaying ? target.AddComponent(type) : Undo.AddComponent(target, type);
                    MarkSceneDirty(target);
                    CompleteUndoGroup(undoGroup);
                    Dictionary<string, object> result = Result("Component added.");
                    result["component"] = UniBridgeLegacySerialized.BuildComponentData(component, false, false, 1);
                    result["gameObject"] = BuildGameObjectSnapshot(target, true);
                    result["undoGroup"] = undoGroup < 0 ? (object)null : undoGroup;
                    return result;
                }
                catch
                {
                    RollbackUndoGroup(undoGroup);
                    throw;
                }
            }
            if (EqualsAction(action, "RemoveComponent"))
            {
                EnsureMutationAllowed(parameters, "Remove Component");
                Component component = UniBridgeLegacySerialized.ResolveComponent(target, parameters, true);
                if (component is Transform)
                    throw new InvalidOperationException("Transform cannot be removed.");
                Dictionary<string, object> before = UniBridgeLegacySerialized.BuildComponentData(component, false, false, 1);
                if (UniBridgeLegacyValue.GetBool(parameters, "DryRun", false))
                {
                    Dictionary<string, object> preview = Result("Component removal validated without changing the scene.");
                    preview["dryRun"] = true;
                    preview["component"] = before;
                    return preview;
                }
                int undoGroup = BeginUndoGroup("UniBridge Remove Component");
                try
                {
                    if (EditorApplication.isPlaying)
                        UnityEngine.Object.Destroy(component);
                    else
                        Undo.DestroyObjectImmediate(component);
                    MarkSceneDirty(target);
                    CompleteUndoGroup(undoGroup);
                    Dictionary<string, object> result = Result("Component removed.");
                    result["before"] = before;
                    result["undoGroup"] = undoGroup < 0 ? (object)null : undoGroup;
                    return result;
                }
                catch
                {
                    RollbackUndoGroup(undoGroup);
                    throw;
                }
            }
            if (EqualsAction(action, "SetComponentProperty") || EqualsAction(action, "SetComponentProperties"))
            {
                EnsureMutationAllowed(parameters, "Set Component Property");
                Component component = UniBridgeLegacySerialized.ResolveComponent(target, parameters, true);
                Dictionary<string, object> properties = GetSerializedPropertyRequest(parameters);
                bool dryRun = UniBridgeLegacyValue.GetBool(parameters, "DryRun", false);
                if (dryRun)
                {
                    Dictionary<string, object> preview = UniBridgeLegacySerialized.ApplyProperties(component, properties, true);
                    Dictionary<string, object> result = Result("Serialized component-property mutation validated without changing the scene.");
                    result["application"] = preview;
                    return result;
                }

                int undoGroup = BeginUndoGroup("UniBridge Set Component Properties");
                try
                {
                    Dictionary<string, object> application = UniBridgeLegacySerialized.ApplyProperties(component, properties, false);
                    MarkSceneDirty(target);
                    CompleteUndoGroup(undoGroup);
                    Dictionary<string, object> result = Result("Serialized component properties applied and verified.");
                    result["application"] = application;
                    result["gameObject"] = BuildGameObjectSnapshot(target, true);
                    result["undoGroup"] = undoGroup < 0 ? (object)null : undoGroup;
                    return result;
                }
                catch
                {
                    RollbackUndoGroup(undoGroup);
                    throw;
                }
            }
            throw new InvalidOperationException("Unsupported ManageGameObject action '" + action + "'.");
        }

        private static Dictionary<string, object> AssetIntelligence(Dictionary<string, object> parameters)
        {
            string action = Action(parameters, "Search");
            if (EqualsAction(action, "Search") || EqualsAction(action, "Find"))
            {
                string query = UniBridgeLegacyValue.GetString(parameters, "Query", String.Empty);
                int maxResults = Clamp(UniBridgeLegacyValue.GetInt(parameters, "MaxResults", 100), 1, 5000);
                List<object> folderValues = UniBridgeLegacyValue.GetArray(parameters, "Folders");
                List<string> folders = new List<string>();
                if (folderValues != null)
                {
                    for (int index = 0; index < folderValues.Count; index++)
                    {
                        if (folderValues[index] != null)
                            folders.Add(folderValues[index].ToString());
                    }
                }

                string[] guids = folders.Count > 0
                    ? AssetDatabase.FindAssets(query, folders.ToArray())
                    : AssetDatabase.FindAssets(query);
                List<object> assets = new List<object>();
                for (int index = 0; index < guids.Length && assets.Count < maxResults; index++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[index]);
                    assets.Add(BuildAssetSummary(path, guids[index]));
                }
                Dictionary<string, object> result = Result("Asset search completed.");
                result["totalMatches"] = guids.Length;
                result["returnedCount"] = assets.Count;
                result["assets"] = assets;
                return result;
            }
            if (EqualsAction(action, "Inspect"))
            {
                string path = RequireString(parameters, "AssetPath");
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (String.IsNullOrEmpty(guid))
                    throw new FileNotFoundException("Asset was not found.", path);
                Dictionary<string, object> result = Result("Asset inspected.");
                result["asset"] = BuildAssetSummary(path, guid);
                string[] dependencies = AssetDatabase.GetDependencies(path, true);
                result["dependencies"] = dependencies;
                result["dependencyCount"] = dependencies.Length;
                return result;
            }
            if (EqualsAction(action, "ReadText"))
            {
                string path = RequireString(parameters, "AssetPath");
                string absolutePath = ToAbsoluteAssetPath(path);
                if (!File.Exists(absolutePath))
                    throw new FileNotFoundException("Text asset was not found.", path);
                int maxCharacters = Clamp(UniBridgeLegacyValue.GetInt(parameters, "MaxCharacters", 200000), 1, 2000000);
                string text = File.ReadAllText(absolutePath);
                bool truncated = text.Length > maxCharacters;
                if (truncated) text = text.Substring(0, maxCharacters);
                Dictionary<string, object> result = Result("Text asset read.");
                result["assetPath"] = path;
                result["text"] = text;
                result["truncated"] = truncated;
                result["totalCharacters"] = new FileInfo(absolutePath).Length;
                return result;
            }
            throw new InvalidOperationException("Unsupported AssetIntelligence action '" + action + "'.");
        }

        private static Dictionary<string, object> BuildEditorState()
        {
            Dictionary<string, object> state = new Dictionary<string, object>();
            state["isPlaying"] = EditorApplication.isPlaying;
            state["isPlayingOrWillChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode;
            state["isPaused"] = EditorApplication.isPaused;
            state["isCompiling"] = EditorApplication.isCompiling;
            state["isUpdating"] = EditorApplication.isUpdating;
            state["ready"] = !EditorApplication.isCompiling && !EditorApplication.isUpdating;
            state["unityVersion"] = Application.unityVersion;
            state["platform"] = Application.platform.ToString();
            return state;
        }

        private static Dictionary<string, object> BuildSceneSummary(Scene scene)
        {
            Dictionary<string, object> summary = new Dictionary<string, object>();
            summary["sceneId"] = scene.IsValid() ? scene.GetHashCode() : 0;
            summary["name"] = scene.IsValid() ? scene.name : null;
            summary["path"] = scene.IsValid() ? scene.path : null;
            summary["isLoaded"] = scene.IsValid() && scene.isLoaded;
            summary["isDirty"] = scene.IsValid() && scene.isDirty;
            summary["rootCount"] = scene.IsValid() && scene.isLoaded ? scene.rootCount : 0;
            summary["buildIndex"] = scene.IsValid() ? scene.buildIndex : -1;
            return summary;
        }

        private static void AddHierarchy(
            GameObject gameObject,
            int depth,
            bool includeInactive,
            bool includeComponents,
            int maxObjects,
            int maxDepth,
            List<object> results,
            ref int totalVisited,
            ref bool truncated)
        {
            totalVisited++;
            if ((!includeInactive && !gameObject.activeInHierarchy) || depth > maxDepth)
                return;
            if (results.Count >= maxObjects)
            {
                truncated = true;
                return;
            }
            Dictionary<string, object> snapshot = BuildGameObjectSnapshot(gameObject, includeComponents);
            snapshot["depth"] = depth;
            results.Add(snapshot);
            for (int index = 0; index < gameObject.transform.childCount && !truncated; index++)
                AddHierarchy(gameObject.transform.GetChild(index).gameObject, depth + 1, includeInactive, includeComponents, maxObjects, maxDepth, results, ref totalVisited, ref truncated);
        }

        private static Dictionary<string, object> BuildGameObjectSnapshot(GameObject gameObject, bool includeComponents)
        {
            Dictionary<string, object> snapshot = new Dictionary<string, object>();
            snapshot["objectId"] = gameObject.GetInstanceID();
            snapshot["name"] = gameObject.name;
            snapshot["path"] = GetHierarchyPath(gameObject.transform);
            snapshot["parentObjectId"] = gameObject.transform.parent == null ? (object)null : gameObject.transform.parent.gameObject.GetInstanceID();
            snapshot["siblingIndex"] = gameObject.transform.GetSiblingIndex();
            snapshot["activeSelf"] = gameObject.activeSelf;
            snapshot["activeInHierarchy"] = gameObject.activeInHierarchy;
            snapshot["tag"] = gameObject.tag;
            snapshot["layer"] = gameObject.layer;
            snapshot["scenePath"] = gameObject.scene.path;
            snapshot["transform"] = BuildTransformSnapshot(gameObject.transform);

            if (includeComponents)
            {
                Component[] components = gameObject.GetComponents<Component>();
                List<object> componentNames = new List<object>();
                int missingScripts = 0;
                for (int index = 0; index < components.Length; index++)
                {
                    if (components[index] == null)
                        missingScripts++;
                    else
                        componentNames.Add(components[index].GetType().FullName);
                }
                snapshot["components"] = componentNames;
                snapshot["componentDetails"] = UniBridgeLegacySerialized.BuildComponentDetails(
                    gameObject,
                    false,
                    false,
                    1);
                snapshot["missingScripts"] = missingScripts;
            }
            return snapshot;
        }

        private static Dictionary<string, object> BuildTransformSnapshot(Transform transform)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["localPosition"] = Vector(transform.localPosition);
            result["localEulerAngles"] = Vector(transform.localEulerAngles);
            result["localScale"] = Vector(transform.localScale);
            result["worldPosition"] = Vector(transform.position);
            result["worldEulerAngles"] = Vector(transform.eulerAngles);
            return result;
        }

        private static Dictionary<string, object> Vector(Vector3 value)
        {
            Dictionary<string, object> vector = new Dictionary<string, object>();
            vector["x"] = value.x;
            vector["y"] = value.y;
            vector["z"] = value.z;
            return vector;
        }

        private static Dictionary<string, object> BuildAssetSummary(string path, string guid)
        {
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            Dictionary<string, object> summary = new Dictionary<string, object>();
            summary["path"] = path;
            summary["guid"] = guid;
            summary["name"] = asset == null ? Path.GetFileNameWithoutExtension(path) : asset.name;
            summary["type"] = asset == null ? null : asset.GetType().FullName;
            summary["isFolder"] = AssetDatabase.IsValidFolder(path);
            summary["reference"] = UniBridgeLegacySerialized.BuildObjectReferenceInfo(asset);
            return summary;
        }

        private static List<GameObject> ResolveMany(Dictionary<string, object> parameters, bool includeInactive)
        {
            int objectId = UniBridgeLegacyValue.GetInt(parameters, "ObjectId", 0);
            if (objectId == 0)
                objectId = UniBridgeLegacyValue.GetInt(parameters, "InstanceId", 0);
            if (objectId != 0)
            {
                GameObject byId = EditorUtility.InstanceIDToObject(objectId) as GameObject;
                List<GameObject> idResult = new List<GameObject>();
                if (byId != null) idResult.Add(byId);
                return idResult;
            }

            string target = UniBridgeLegacyValue.GetString(parameters, "Target", null);
            string path = UniBridgeLegacyValue.GetString(parameters, "Path", target);
            string name = UniBridgeLegacyValue.GetString(parameters, "Name", null);
            if (String.IsNullOrEmpty(path) && String.IsNullOrEmpty(name))
                throw new InvalidOperationException("Target, Path, Name, or ObjectId is required.");

            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            List<GameObject> results = new List<GameObject>();
            string normalizedPath = NormalizeHierarchyPath(path);
            for (int index = 0; index < all.Length; index++)
            {
                GameObject candidate = all[index];
                if (candidate == null || !candidate.scene.IsValid())
                    continue;
                if (!includeInactive && !candidate.activeInHierarchy)
                    continue;

                bool matches = false;
                if (!String.IsNullOrEmpty(normalizedPath))
                {
                    string candidatePath = NormalizeHierarchyPath(GetHierarchyPath(candidate.transform));
                    matches = String.Equals(candidatePath, normalizedPath, StringComparison.Ordinal) ||
                              String.Equals(candidate.name, path, StringComparison.Ordinal);
                }
                if (!String.IsNullOrEmpty(name))
                    matches = matches || String.Equals(candidate.name, name, StringComparison.Ordinal);
                if (matches)
                    results.Add(candidate);
            }
            results.Sort(delegate(GameObject left, GameObject right)
            {
                return String.CompareOrdinal(GetHierarchyPath(left.transform), GetHierarchyPath(right.transform));
            });
            return results;
        }

        private static GameObject ResolveOne(Dictionary<string, object> parameters, bool includeInactive)
        {
            List<GameObject> matches = ResolveMany(parameters, includeInactive);
            if (matches.Count == 0)
                throw new InvalidOperationException("GameObject was not found.");
            if (matches.Count > 1)
                throw new InvalidOperationException("GameObject selector is ambiguous; " + matches.Count + " objects matched. Use ObjectId or a full hierarchy path.");
            return matches[0];
        }

        private static GameObject ResolveOptionalParent(Dictionary<string, object> parameters)
        {
            object parentIdValue = UniBridgeLegacyValue.Get(parameters, "ParentObjectId");
            string parentPath = UniBridgeLegacyValue.GetString(parameters, "Parent", null);
            if (parentIdValue == null && String.IsNullOrEmpty(parentPath))
                return null;

            Dictionary<string, object> selector = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (parentIdValue != null) selector["ObjectId"] = parentIdValue;
            if (!String.IsNullOrEmpty(parentPath)) selector["Target"] = parentPath;
            return ResolveOne(selector, true);
        }

        private static void ApplyTransform(Transform transform, Dictionary<string, object> parameters)
        {
            Dictionary<string, object> localPosition = UniBridgeLegacyValue.GetObject(parameters, "LocalPosition");
            Dictionary<string, object> worldPosition = UniBridgeLegacyValue.GetObject(parameters, "Position");
            Dictionary<string, object> localEuler = UniBridgeLegacyValue.GetObject(parameters, "LocalEulerAngles");
            Dictionary<string, object> worldEuler = UniBridgeLegacyValue.GetObject(parameters, "EulerAngles");
            Dictionary<string, object> localScale = UniBridgeLegacyValue.GetObject(parameters, "LocalScale");
            if (localPosition != null) transform.localPosition = ReadVector(localPosition, transform.localPosition);
            if (worldPosition != null) transform.position = ReadVector(worldPosition, transform.position);
            if (localEuler != null) transform.localEulerAngles = ReadVector(localEuler, transform.localEulerAngles);
            if (worldEuler != null) transform.eulerAngles = ReadVector(worldEuler, transform.eulerAngles);
            if (localScale != null) transform.localScale = ReadVector(localScale, transform.localScale);
        }

        private static Vector3 ReadVector(Dictionary<string, object> value, Vector3 fallback)
        {
            return new Vector3(
                UniBridgeLegacyValue.GetFloat(value, "x", fallback.x),
                UniBridgeLegacyValue.GetFloat(value, "y", fallback.y),
                UniBridgeLegacyValue.GetFloat(value, "z", fallback.z));
        }

        private static List<object> AddRequestedComponents(GameObject target, Dictionary<string, object> parameters)
        {
            List<object> requested = UniBridgeLegacyValue.GetArray(parameters, "ComponentsToAdd");
            List<object> results = new List<object>();
            if (requested == null)
                return results;
            for (int index = 0; index < requested.Count; index++)
            {
                string componentName = requested[index] == null ? null : requested[index].ToString();
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["requestedType"] = componentName;
                Type type = ResolveComponentType(componentName);
                if (type == null)
                {
                    entry["applied"] = false;
                    entry["error"] = "Component type was not found.";
                }
                else
                {
                    Component component = EditorApplication.isPlaying ? target.AddComponent(type) : Undo.AddComponent(target, type);
                    entry["applied"] = component != null;
                    entry["resolvedType"] = type.FullName;
                }
                results.Add(entry);
            }
            return results;
        }

        private static Type ResolveComponentType(string name)
        {
            if (String.IsNullOrEmpty(name))
                return null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                Type direct = assemblies[assemblyIndex].GetType(name, false, true);
                if (direct != null && typeof(Component).IsAssignableFrom(direct))
                    return direct;

                Type[] types;
                try { types = assemblies[assemblyIndex].GetTypes(); }
                catch (ReflectionTypeLoadException exception) { types = exception.Types; }
                catch { continue; }
                for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    Type type = types[typeIndex];
                    if (type != null && typeof(Component).IsAssignableFrom(type) &&
                        String.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
                        return type;
                }
            }
            return null;
        }

        private static Component FindComponent(GameObject target, string name)
        {
            Component[] components = target.GetComponents<Component>();
            for (int index = 0; index < components.Length; index++)
            {
                Component component = components[index];
                if (component != null &&
                    (String.Equals(component.GetType().FullName, name, StringComparison.OrdinalIgnoreCase) ||
                     String.Equals(component.GetType().Name, name, StringComparison.OrdinalIgnoreCase)))
                    return component;
            }
            return null;
        }

        private static void MarkSceneDirty(GameObject target)
        {
            if (!EditorApplication.isPlaying && target != null && target.scene.IsValid())
            {
                EditorUtility.SetDirty(target);
                EditorSceneManager.MarkSceneDirty(target.scene);
            }
        }

        private static string GetHierarchyPath(Transform transform)
        {
            string path = "/" + transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = "/" + transform.name + path;
            }
            return path;
        }

        private static string NormalizeHierarchyPath(string path)
        {
            if (String.IsNullOrEmpty(path))
                return null;
            string normalized = path.Replace('\\', '/').Trim();
            if (!normalized.StartsWith("/", StringComparison.Ordinal))
                normalized = "/" + normalized;
            while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
            return normalized.TrimEnd('/');
        }

        private static string ToAbsoluteAssetPath(string assetPath)
        {
            string normalized = assetPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(UniBridgeLegacyHost.ProjectRoot, normalized));
        }

        private static string Action(Dictionary<string, object> parameters, string fallback)
        {
            string action = UniBridgeLegacyValue.GetString(parameters, "Action", null);
            if (String.IsNullOrEmpty(action)) action = UniBridgeLegacyValue.GetString(parameters, "action", fallback);
            return action;
        }

        private static Scene ResolveScene(Dictionary<string, object> parameters, bool defaultToActive)
        {
            int sceneId = UniBridgeLegacyValue.GetInt(parameters, "SceneId", 0);
            if (sceneId == 0)
                sceneId = UniBridgeLegacyValue.GetInt(parameters, "SceneHandle", 0);
            if (sceneId != 0)
            {
                for (int index = 0; index < UnitySceneManager.sceneCount; index++)
                {
                    Scene candidate = UnitySceneManager.GetSceneAt(index);
                    if (candidate.GetHashCode() == sceneId)
                        return candidate;
                }
                throw new InvalidOperationException("No loaded scene has SceneId " + sceneId + ".");
            }

            string scenePath = UniBridgeLegacyValue.GetString(parameters, "ScenePath", null);
            if (!String.IsNullOrEmpty(scenePath))
            {
                Scene byPath = UnitySceneManager.GetSceneByPath(scenePath);
                if (byPath.IsValid())
                    return byPath;
                throw new InvalidOperationException("No loaded scene has path: " + scenePath);
            }

            string sceneName = UniBridgeLegacyValue.GetString(parameters, "SceneName", null);
            if (!String.IsNullOrEmpty(sceneName))
            {
                Scene byName = UnitySceneManager.GetSceneByName(sceneName);
                if (byName.IsValid())
                    return byName;
                throw new InvalidOperationException("No loaded scene has name: " + sceneName);
            }

            if (defaultToActive)
                return UnitySceneManager.GetActiveScene();
            throw new InvalidOperationException("SceneId, ScenePath, or SceneName is required.");
        }

        private static bool HasDirtyOpenScenes()
        {
            for (int index = 0; index < UnitySceneManager.sceneCount; index++)
            {
                Scene scene = UnitySceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                    return true;
            }
            return false;
        }

        private static void EnsureEditMode(string operation)
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException(operation + " is blocked while Unity is in Play Mode.");
        }

        private static void EnsureMutationAllowed(Dictionary<string, object> parameters, string operation)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException(operation + " is blocked while Unity is compiling or updating.");
            if (EditorApplication.isPlaying && !UniBridgeLegacyValue.GetBool(parameters, "AllowPlayModeWrite", false))
                throw new InvalidOperationException(
                    operation + " is blocked in Play Mode. Pass AllowPlayModeWrite=true only for an explicitly ephemeral runtime change.");
        }

        private static int BeginUndoGroup(string name)
        {
            if (EditorApplication.isPlaying)
                return -1;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return group;
        }

        private static void CompleteUndoGroup(int group)
        {
            if (group >= 0)
                Undo.CollapseUndoOperations(group);
        }

        private static void RollbackUndoGroup(int group)
        {
            if (group >= 0)
            {
                try { Undo.RevertAllDownToGroup(group); }
                catch { }
            }
        }

        private static void ValidateRequestedComponents(Dictionary<string, object> parameters)
        {
            List<object> requested = UniBridgeLegacyValue.GetArray(parameters, "ComponentsToAdd");
            if (requested == null)
                return;
            for (int index = 0; index < requested.Count; index++)
            {
                string componentName = requested[index] == null ? null : requested[index].ToString();
                if (ResolveComponentType(componentName) == null)
                    throw new InvalidOperationException("Component type was not found: " + componentName);
            }
        }

        private static Dictionary<string, object> GetSerializedPropertyRequest(Dictionary<string, object> parameters)
        {
            Dictionary<string, object> properties = UniBridgeLegacyValue.GetObject(parameters, "Properties");
            if (properties == null)
                properties = UniBridgeLegacyValue.GetObject(parameters, "ComponentProperties");
            if (properties == null)
                properties = UniBridgeLegacyValue.GetObject(parameters, "component_properties");

            string propertyPath = UniBridgeLegacyValue.GetString(parameters, "PropertyPath", null);
            if (!String.IsNullOrEmpty(propertyPath))
            {
                if (!parameters.ContainsKey("Value"))
                    throw new InvalidOperationException("Value is required when PropertyPath is provided. JSON null is allowed for object references.");
                Dictionary<string, object> single = new Dictionary<string, object>();
                single[propertyPath] = UniBridgeLegacyValue.Get(parameters, "Value");
                return single;
            }

            if (properties == null || properties.Count == 0)
                throw new InvalidOperationException("Properties or PropertyPath/Value is required.");

            string componentName = UniBridgeLegacyValue.GetString(parameters, "Component", null);
            if (String.IsNullOrEmpty(componentName))
                componentName = UniBridgeLegacyValue.GetString(parameters, "ComponentName", null);
            if (!String.IsNullOrEmpty(componentName))
            {
                object nested;
                if (properties.TryGetValue(componentName, out nested))
                {
                    Dictionary<string, object> nestedProperties = nested as Dictionary<string, object>;
                    if (nestedProperties == null || nestedProperties.Count == 0)
                        throw new InvalidOperationException("Nested ComponentProperties entry must be a non-empty object.");
                    return nestedProperties;
                }
            }
            return properties;
        }

        private static bool EqualsAction(string actual, string expected)
        {
            return String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string RequireString(Dictionary<string, object> parameters, string key)
        {
            string value = UniBridgeLegacyValue.GetString(parameters, key, null);
            if (String.IsNullOrEmpty(value))
                throw new InvalidOperationException(key + " is required.");
            return value;
        }

        private static Dictionary<string, object> Result(string message)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["success"] = true;
            result["message"] = message;
            result["projectContext"] = UniBridgeLegacyHost.BuildProjectContext();
            return result;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal static class UniBridgeLegacyConsole
    {
        private sealed class Entry
        {
            public string Timestamp;
            public string Message;
            public string StackTrace;
            public LogType Type;
        }

        private static readonly object Sync = new object();
        private static readonly List<Entry> Entries = new List<Entry>();
        private const int MaxHistory = 2000;

        internal static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            lock (Sync)
            {
                Entry entry = new Entry();
                entry.Timestamp = DateTime.UtcNow.ToString("O");
                entry.Message = condition;
                entry.StackTrace = stackTrace;
                entry.Type = type;
                Entries.Add(entry);
                if (Entries.Count > MaxHistory)
                    Entries.RemoveRange(0, Entries.Count - MaxHistory);
            }
        }

        internal static Dictionary<string, object> BuildSummary()
        {
            int logs = 0;
            int warnings = 0;
            int errors = 0;
            int exceptions = 0;
            int asserts = 0;
            lock (Sync)
            {
                for (int index = 0; index < Entries.Count; index++)
                {
                    switch (Entries[index].Type)
                    {
                        case LogType.Warning: warnings++; break;
                        case LogType.Error: errors++; break;
                        case LogType.Exception: exceptions++; break;
                        case LogType.Assert: asserts++; break;
                        default: logs++; break;
                    }
                }
            }

            Dictionary<string, object> summary = new Dictionary<string, object>();
            summary["total"] = logs + warnings + errors + exceptions + asserts;
            summary["logs"] = logs;
            summary["warnings"] = warnings;
            summary["errors"] = errors;
            summary["exceptions"] = exceptions;
            summary["asserts"] = asserts;
            summary["hasCriticalIssues"] = errors > 0 || exceptions > 0 || asserts > 0;
            summary["captureScope"] = "Messages emitted after the legacy adapter initialized.";
            return summary;
        }

        internal static List<object> GetEntries(int maximum, bool includeStackTrace)
        {
            List<object> results = new List<object>();
            lock (Sync)
            {
                int start = Math.Max(0, Entries.Count - maximum);
                for (int index = start; index < Entries.Count; index++)
                {
                    Entry source = Entries[index];
                    Dictionary<string, object> entry = new Dictionary<string, object>();
                    entry["timestamp"] = source.Timestamp;
                    entry["type"] = source.Type.ToString();
                    entry["message"] = source.Message;
                    if (includeStackTrace) entry["stackTrace"] = source.StackTrace;
                    results.Add(entry);
                }
            }
            return results;
        }

        internal static void Clear()
        {
            lock (Sync) Entries.Clear();
            try
            {
                Type logEntries = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
                MethodInfo clear = logEntries == null ? null : logEntries.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (clear != null) clear.Invoke(null, null);
            }
            catch { }
        }
    }
}
