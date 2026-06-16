# UniBridge

UniBridge connects a local Unity Editor project to MCP-compatible AI coding agents such as Codex, Claude Code, Gemini, Cursor, or any client that can launch a local MCP server process.

UniBridge is local-first. The Unity package runs a bridge inside the Editor, installs a local relay executable, and exposes controlled Unity project operations through MCP tools.

For version-specific packaging, verification, and known limitation details, see
`../RELEASE_NOTES.md`.

## Requirements

- Unity Editor 6000.0 or newer.
- A Unity project with the `com.cidonix.unibridge` package installed.
- An MCP-compatible client that can launch a local executable.

The package includes relay binaries for:

- Windows x64: `unibridge_relay_win.exe`
- Linux x64: `unibridge_relay_linux`
- macOS x64: `unibridge_relay_mac_x64`
- macOS arm64: `unibridge_relay_mac_arm64`

## Installation

Install UniBridge through Unity Package Manager from a Git URL or a local package folder.

After Unity recompiles the project, open:

```text
Project Settings > UniBridge > MCP
```

The Local Bridge should start automatically. If it is stopped, use the Start button on the settings page.

## First Connection

1. Open `Project Settings > UniBridge > MCP`.
2. Confirm that Local Bridge status is `Running`.
3. Open the `Integrations` section.
4. Copy or locate the MCP client configuration for the current project.
5. Restart the AI agent/MCP client so it reloads the MCP server configuration
   and launches the UniBridge relay. Restarting Unity alone is not enough.
6. When Unity shows a new MCP connection approval dialog, review the executable identity and choose `Allow` if you trust the client.

After approval, the client can call the enabled UniBridge MCP tools for this Unity project.

## Discoverability And First Ping

When a Codex thread or another MCP client is newly configured, restart the AI
agent/MCP client so it can index the UniBridge tools. Restarting Unity alone is
not enough because most clients load MCP server definitions at client startup.

Use this first call when a new agent needs to verify that UniBridge is visible:

```text
UniBridge_Discover Action=Ping IncludeTools=false
```

Useful follow-up discovery calls:

```text
UniBridge_Discover Action=Workflows
UniBridge_Discover Action=Aliases Query=compile
UniBridge_ToolGuide Action=Workflow Topic=scripts
UniBridge_DomainCatalog Action=SuggestTools Query=PlayMode
```

Recommended reload-safe compile workflow:

```text
UniBridge_ValidateScript IncludeDiagnostics=true Uri=Assets/...
UniBridge_ManageEditor Action=RefreshAssets WaitForCompletion=true
UniBridge_ManageEditor Action=RequestScriptCompilationNoWait Force=true
UniBridge_ManageEditor Action=WaitForReadyAfterReload
UniBridge_ManageEditor Action=GetCompilationDiagnostics
UniBridge_ReadConsole Action=DiagnosticSummary
```

Recommended Play Mode boundary workflow:

```text
UniBridge_ReadConsole Action=ClearConsole
UniBridge_ManageEditor Action=Play WaitForCompletion=true
UniBridge_ManageEditor Action=WaitForPlayMode
UniBridge_ManageEditor Action=WaitForReady RequireNotPlaying=false
UniBridge_ReadConsole Action=DiagnosticSummary
UniBridge_ManageEditor Action=ExitPlayMode WaitForCompletion=true
UniBridge_ManageEditor Action=WaitForEditMode
UniBridge_ManageEditor Action=WaitForReady RequireNotPlaying=true
UniBridge_ReadConsole Action=DiagnosticSummary
```

## Runtime Profiler

`UniBridge_RuntimeProfiler` is a read-only tool for Play Mode runtime triage. It
does not execute arbitrary project code. It uses Unity profiler APIs and
bounded `ProfilerRecorder` sampling so agents can measure symptoms instead of
guessing from scene files alone.

Useful calls:

```text
UniBridge_RuntimeProfiler Action=Snapshot
UniBridge_RuntimeProfiler Action=Metrics
UniBridge_RuntimeProfiler Action=Sample SampleFrames=120 Metrics=[main_thread_ms,gc_alloc_bytes,batches_count]
```

`Action=Sample` requires Play Mode by default. Pass `RequirePlayMode=false` only
when editor-time sampling is intentional.

The response includes compact metric summaries and spike samples. When
`SaveToFile=true`, the full raw sample payload is written under:

```text
Library/UniBridge/RuntimeProfiler
```

## Runtime State Probe

`UniBridge_RuntimeStateProbe` is a read-only tool for inspecting live
GameObject/component state. It is meant for gameplay debugging when the useful
question is "what value did this component have over the next few frames?"
rather than "what did the source file say?"

It does not execute arbitrary project C# code. It reads Unity
`SerializedObject`/`SerializedProperty` values plus reflected fields and
readable properties.

Useful calls:

```text
UniBridge_RuntimeStateProbe Action=ListMembers Component=<ComponentOrMonoBehaviour>
UniBridge_RuntimeStateProbe Action=Snapshot Target=<objectPathOrId> Component=<component> Members=[fieldOrProperty]
UniBridge_RuntimeStateProbe Action=Sample Target=<objectPathOrId> Component=<component> Members=[fieldOrProperty] SampleFrames=30
UniBridge_RuntimeStateProbe Action=Assert Target=<objectPathOrId> Component=<component> Assertions=[{member:'field',operator:'==',value:true}]
```

`Action=Assert` is a read-only watch/assertion workflow. It samples values like
`Action=Sample`, then evaluates simple rules and returns `passed`,
`assertionSummary`, per-rule observed samples, and an optional saved raw payload.
Required failed assertions return `success=false` by default, which makes them a
useful safety gate inside `UniBridge_BatchActions`.

Assertion rule fields:

```text
name: optional label
member/memberPath: SerializedProperty path or reflected field/property
valuePath: optional sub-value path such as x, y, z, width, or height
operator: exists, not_exists, ==, !=, >, >=, <, <=, between, contains, matches, is_null, not_null, changed, stable
value/expected: comparison value
min/max: range values for between
mode: Last, First, Any, All, Changed, or Stable
required: false makes a failed rule informational
tolerance: numeric equality tolerance
```

Example:

```text
UniBridge_RuntimeStateProbe Action=Assert
  Target=/Player
  Component=Transform
  Assertions=[
    {name:'scale_x_is_one',member:'localScale.x',operator:'==',value:1,tolerance:0.001},
    {name:'player_above_floor',member:'position.y',operator:'>',value:-10}
  ]
```

Batch aliases include:

```text
runtime_probe
runtime_state
runtime_state_probe
state_probe
watch_variables
watch_assert
runtime_assert
component_state
monobehaviour_state
runtime_fields
```

`Action=Sample` and `Action=Assert` require Play Mode by default. Pass
`RequirePlayMode=false` only for intentional editor-time smoke tests.

Target lookup uses the shared scene resolver, including inactive scene objects,
Prefab Stage objects, instance IDs, hierarchy paths, component short/full type
names, MonoScript GUIDs, and serialized editor class identifiers.

The response returns compact changed-member summaries. When `SaveToFile=true`,
the full raw sample payload is written under:

```text
Library/UniBridge/RuntimeStateProbe
```

## Additive Scene Registration Validation

`UniBridge_ValidateAdditiveSceneRegistration` is a read-only validator for
cloned/additive scene setup. It checks the scene asset, metadata asset,
scene-to-metadata references, Build Settings, `scenesManager.prefab`, boundary
arrays, stale supplied template references, and optional neighbor scene sanity.

Example:

```text
UniBridge_ValidateAdditiveSceneRegistration ScenePath=Assets/_Domovyk/Scenes/darkness/darkness12.unity
```

Batch-safe example:

```json
{
  "DryRun": true,
  "Steps": [
    {
      "tool": "validate_additive_scene",
      "parameters": {
        "SceneName": "darkness12",
        "RequireBuildSettingsEntry": true
      }
    },
    {
      "tool": "editor",
      "parameters": {
        "Action": "GetCompilationDiagnostics"
      }
    },
    {
      "tool": "console",
      "parameters": {
        "Action": "DiagnosticSummary"
      }
    }
  ]
}
```

## How The Relay Works

The MCP client does not connect to Unity directly. It launches the UniBridge relay as its MCP server process.

The relay:

1. Reads live Unity project discovery files from the user's UniBridge directory.
2. Selects the target Unity project by project ID, project path, editor process ID, or auto-detection.
3. Connects to the Unity Editor bridge.
4. Proxies MCP requests between the AI client and Unity.

On Windows the Editor bridge uses named pipes. On Linux and macOS it uses Unix domain sockets.

The relay is installed under the user's home directory:

```text
~/.unibridge/relay
```

When the package contains a newer relay, or a same-version relay with a different binary hash, UniBridge can refresh the installed relay copy from the package.

## Recommended MCP Configuration

Use the Project Settings integration controls whenever possible. They generate a configuration tied to the current Unity project.

A typical Windows configuration looks like this:

```json
{
  "mcpServers": {
    "unibridge_my_project_0123abcd": {
      "command": "C:\\Users\\USER\\.unibridge\\relay\\unibridge_relay_win.exe",
      "args": ["--mcp", "--project-id", "PROJECT_ID"]
    }
  }
}
```

For Linux:

```bash
~/.unibridge/relay/unibridge_relay_linux --mcp --project-id PROJECT_ID
```

For macOS:

```bash
~/.unibridge/relay/unibridge_relay_mac_x64 --mcp --project-id PROJECT_ID
~/.unibridge/relay/unibridge_relay_mac_arm64 --mcp --project-id PROJECT_ID
```

Use `--project-id` for normal work. Give each Unity project its own MCP server key, for example `unibridge_project_name_0123abcd`. This lets MCP clients expose multiple open Unity projects at the same time instead of replacing one generic `unibridge` entry.

## Multiple Open Unity Projects

UniBridge is designed to work with multiple open Unity projects.

Each project writes discovery metadata with:

- project ID;
- project name;
- project path;
- Unity Editor process ID;
- connection path.

The project ID is stored in:

```text
ProjectSettings/UniBridge/project.json
```

This ID stays with the Unity project. If the project folder is moved, the MCP client can still target the same project by ID after the project is opened again.

If several Unity projects are open, configure one MCP server entry per Unity project and include the corresponding `--project-id`.

After adding another project entry to the agent configuration, restart the AI
agent/MCP client itself so it reloads the list of available MCP servers. Unity
does not need to be restarted for the agent to discover a newly configured
project.

## Project Settings

`Project Settings > UniBridge > MCP` is the main control surface.

It includes:

- Local Bridge status;
- connected MCP clients;
- connection approval records;
- validation level;
- Editor Mode and Batch Mode auto-approval settings;
- enabled/disabled MCP tools;
- tool descriptions and schemas;
- integration snippets;
- installed relay path.

### Validation Level

Validation level controls how strictly UniBridge checks local MCP requests and script operations.

- `basic`: fast syntax and structural checks.
- `standard`: normal recommended checks for everyday work.
- `comprehensive`: broader checks where available.
- `strict`: most cautious mode for sensitive projects.

Use `standard` unless you have a reason to loosen or tighten validation.

### Auto-Approve In Editor Mode

When enabled, new local MCP clients can be approved automatically while Unity is running normally in the Editor.

Use this only when you trust the local tools on your machine. Explicitly rejected clients stay rejected.

### Auto-Approve In Batch Mode

Batch Mode is Unity's headless or automated mode, commonly used by CI, scripts, and command-line runs.

When enabled, UniBridge can approve local MCP clients automatically in Batch Mode where no user can click an approval dialog.

## MCP Tools

The Tools section shows every MCP tool exposed by UniBridge. You can enable or disable tools per project.

Tool descriptions are shown in Project Settings and are also sent to MCP clients as part of the tool schema. These descriptions are meant to help both users and AI agents understand:

- what the tool does;
- when to use it;
- which arguments matter;
- what the result contains;
- any important safety or workflow notes.

For cautious projects, disable tools that are not needed.

## Connection Approval

When a new MCP client connects, Unity shows an approval dialog with process and executable identity details.

Review:

- process name;
- executable path;
- SHA256 hash;
- code signing status;
- requested capabilities.

Choose `Allow` only for clients you trust. Choose `Revoke Access` to reject or remove access.

Unsigned executables can still work. Code signing only affects trust display and operating-system security prompts.

## Useful Workflows

### Choose The Right Tool

Use `UniBridge_ToolGuide` when an agent is new to a project or unsure which UniBridge tool should handle a Unity task. Use `UniBridge_DomainCatalog` when the agent knows the domain (`Rendering`, `Physics3D`, `Navigation`, `UIToolkit`, and so on) but needs the matching tools, aliases, and type hints.

It is read-only and returns:

- the recommended orientation loop;
- workflow topics such as `ui`, `uitoolkit`, `scene_objects`, `scoped_editing`, `behaviour_context`, `tilemap2d`, `input_actions`, `timeline`, `physics2d`, `physics3d`, `navigation`, `rendering`, `assets_import`, `materials`, `scriptable_objects`, `unity_events`, `visual_capture`, `animator`, `scripts`, `batch`, `console`, and `search`;
- first calls, edit calls, and verification calls for the selected workflow;
- batch aliases and allowed tools;
- optional current tool-registry metadata.

Useful calls:

- `Action=Overview`: get the project-workflow map;
- `Action=Workflow Topic=ui`: get the recommended uGUI workflow;
- `Action=Workflow Topic=scoped_editing`: get the safe scene/prefab asset editing workflow;
- `Action=Workflow Topic=tilemap2d`: get the Grid/Tilemap/Tile authoring workflow;
- `Action=Workflow Topic=input_actions`: get the `.inputactions` and PlayerInput workflow;
- `Action=Workflow Topic=timeline`: get the Timeline/PlayableDirector workflow;
- `Action=Workflow Topic=physics2d`: get the Rigidbody2D/Collider2D preset workflow;
- `Action=Workflow Topic=physics3d`: get the Rigidbody/Collider/Joint/PhysicsMaterial workflow;
- `Action=Workflow Topic=navigation`: get the NavMeshAgent/NavMeshObstacle/NavMeshSurface workflow;
- `Action=Workflow Topic=rendering`: get the Camera/Light/Volume/render settings workflow;
- `Action=Workflow Topic=uitoolkit`: get the UXML/USS/UIDocument workflow;
- `UniBridge_DomainCatalog Action=InspectDomain Domain=Rendering`: inspect the domain map directly;
- `Action=Workflow Topic=assets_import`: get the importer workflow;
- `Action=Tool Tool=asset_importer`: resolve an alias and see related workflows.

### Scene And Gameplay Authoring

For scene and gameplay work, prefer high-level tools before falling back to many low-level calls:

- `UniBridge_ScopedEdit`: open a `.unity` scene or `.prefab` asset, run a validated `BatchActions` payload inside that scope, save the scope, and restore editor state.
- `UniBridge_BehaviourContext`: read a target GameObject's attached MonoBehaviour script paths, bounded source text, and JSON-safe serialized field values.
- `UniBridge_ManageTilemap2D`: create Grids, Tilemap layers, Tile assets from sprites, paint/erase cells, inspect occupied cells, and configure tilemap colliders.
- `UniBridge_ManageInputActions`: author `.inputactions` JSON assets, add maps/actions/bindings/control schemes, and wire `PlayerInput` when the Input System package is installed.
- `UniBridge_ManageTimeline`: create Timeline assets, add tracks and default clips, create PlayableDirector components, and bind tracks.
- `UniBridge_ManagePhysics2D`: create PhysicsMaterial2D assets and apply Rigidbody2D, Collider2D, Joint2D, and Effector2D presets.
- `UniBridge_ManagePhysics3D`: create PhysicsMaterial assets and apply Rigidbody, Collider, Joint, and CharacterController presets.
- `UniBridge_ManageNavigation`: author NavMeshAgent, NavMeshObstacle, OffMeshLink, and optional AI Navigation surface/modifier/link components.
- `UniBridge_ManageRendering`: create cameras, lights, Volume assets, render settings, preview/lighting rigs, and named rendering layer masks for lights/renderers.
- `UniBridge_ManageUIToolkit`: create UXML/USS/PanelSettings assets, wire UIDocument scene objects, and patch small UXML element/class/style changes.

Use `DryRun=true` first for scoped or batched edits, then verify with
`UniBridge_ReadConsole Action=DiagnosticSummary` and a domain-specific inspect
or capture tool.

### Run Workflow Recipes

Use `UniBridge_WorkflowRecipes` when a task matches a common Unity workflow and the agent should not manually compose every low-level call.

Recipes expand into `UniBridge_BatchActions` payloads, so an agent can:

- `Action=List`: see available recipes;
- `Action=Describe Recipe=...`: inspect required and optional inputs;
- `Action=BuildBatch Recipe=...`: generate the exact batch without running it;
- `Action=DryRun Recipe=...`: validate the generated workflow safely;
- `Action=Execute Recipe=...`: run it through BatchActions with rollback safety.

Initial recipes include:

- `CreateInventoryScreen`;
- `ImportSpriteFolderAs2D`;
- `CreateSpriteMaterialAndPreview`;
- `CreateHUDFromAssets`;
- `SetupClickableUIButton`;
- `CreateScriptableConfigAndBindToScene`;
- `RunCoreSmokeTest`;
- `RunUISmokeTest`;
- `RunAssetSmokeTest`.

Prefer `DryRun` first for recipes that modify scenes or assets.
For a fresh agent or a newly-opened Unity project, start with
`BuildBatch Recipe=RunCoreSmokeTest`, then `DryRun`, then `Execute` if the
plan is acceptable. The smoke recipes intentionally expand into normal
`UniBridge_BatchActions` steps so their behavior is inspectable.

### Build A Context Snapshot

Use `UniBridge_ContextSnapshot` when an agent needs to quickly understand the current Unity project before planning or editing.

The snapshot returns one structured overview with:

- project identity, project roots, and registered package roots;
- Unity render pipeline, 2D/3D default mode, tags, layers, rendering layers, and sorting layers;
- UniBridge package version and package dependency overview;
- Unity Editor state;
- active and loaded scenes;
- current selection and Prefab Mode state;
- a bounded hierarchy summary;
- console diagnostic summary;
- project asset counts and recent asset paths;
- enabled UniBridge tools;
- `agentBrief`, a compact onboarding layer for new agents.

`agentBrief` is included by default and summarizes:

- project shape: asset counts, loaded-scene count, root-object count,
  dirty-scene count, active scene, and scene scale;
- likely important folders for scenes, scripts, gameplay, UI, prefabs, art,
  audio, and data/config;
- likely important systems detected from packages, render pipeline, asset
  folders, and asmdefs;
- active `UniBridge_WorkSession` state;
- risk flags such as compiling/importing, Play Mode, dirty scenes, open Prefab
  Stage, console issues, hierarchy truncation, large loaded scenes, or missing
  WorkSession;
- guardrails and recommended next UniBridge calls.

Pass `IncludeAgentBrief=false` when an agent wants only the raw snapshot
sections without the onboarding summary.

`Depth=Brief` keeps package-root output compact: it returns
`registeredPackageCount` but does not expand the full `registeredPackages`
list unless `IncludePackageDependencies=true`. `Depth=Detailed` includes the
full registered package roots by default.

Use `Depth` to control how much data is returned:

- `Brief`: small orientation snapshot;
- `Standard`: recommended default for most work;
- `Detailed`: broader hierarchy, asset, tool, window, and build-settings context.

The snapshot is intentionally bounded. Increase `HierarchyDepth`, `MaxSceneObjects`, `MaxAssets`, or `MaxConsoleIssues` only when deeper context is needed. Use `IncludeProjectRoots`, `IncludeProjectSettings`, and `IncludePackageDependencies` to tune the richer project context.

### Preserve Editor Workspace State

Use `UniBridge_EditorSnapshot` when an agent needs to temporarily change Unity Editor context and later put the workspace back.

It can capture:

- loaded scenes and active scene;
- Scene View camera pivot, rotation, size, orthographic mode, and 2D mode;
- current selection;
- active editor tool and pivot settings;
- current Prefab Mode asset;
- Prefab Mode autosave settings;
- focused window, open window metadata, and active dock tabs.

Snapshots are saved under:

```text
<project>/Library/UniBridge/EditorSnapshots
```

Common actions:

- `Capture`: save the current workspace state;
- `List`: list saved snapshots;
- `Inspect`: read one snapshot by `SnapshotId`;
- `Restore`: restore scenes, selection, Scene View, Prefab Mode, Prefab autosave settings, active tool, active dock tabs, and focused window;
- `Delete` / `Clear`: remove saved snapshots.

Restore has safety controls. It refuses to reload or close dirty scenes unless `SaveDirtyScenes` or `AllowDirtySceneReload` is enabled. Use `DryRun=true` before a restore when you want to see exactly what will change.

Window restore is intentionally conservative. UniBridge focuses matching EditorWindow types, can re-show captured active dock tabs, and can optionally restore maximized state, but it does not rewrite Unity layout files.

### Explore Project Assets

Use `UniBridge_AssetIntelligence` when an agent needs to understand the Project window before choosing files or making changes.

It is read-only and supports:

- ranked asset search by query, type, extension, label, and folder scope;
- detailed asset inspection with GUID, type, labels, importer metadata, file size, timestamps, and type-specific details;
- text reading for assets such as `.cs`, `.prefab`, `.unity`, `.mat`, `.asset`, `.json`, `.asmdef`, `.uss`, and `.uxml`;
- dependency and dependent scans;
- cached `ReferenceGraph` queries for dependencies, reverse references, top referenced assets, optional edge samples, and bounded exact YAML reference locations;
- `Impact` reports before modifying, moving, renaming, deleting, or reimporting an asset;
- `ResolveMissing` fuzzy recovery for stale or mistyped asset paths;
- selected Project asset inspection;
- project asset statistics by type, extension, folder, largest files, and recent files;
- PNG previews written under `~/.unibridge/asset-previews/<project>` when Unity can generate them;
- `Serialize` / `Snapshot` output for deeper AI-readable asset context.
- `Context` output for a structured one-call asset context envelope: detail summary, text slice/chunks when applicable, serialized importer/main/sub-asset data when useful, and fuzzy missing-path suggestions.
- `Structure` output for prefab and already-loaded scene assets: compact hierarchy list/search/read with duplicate-safe `indexedPath`, components, active/tag/layer data, missing-script counts, prefab source hints, renderer sorting data, child summaries, and optional serialized field matching.

`Read` supports `StartLine`/`LineCount`, `TailLines`, `HeadBytes`, `Pattern`, and `Chunks` for several precise line ranges in one call.

`Serialize`, `Snapshot`, and `Context` return bounded upload-style envelopes. They are useful when a simple asset summary is not enough:

- prefab assets can include hierarchy, components, transforms, and serialized component fields;
- active scenes can include a bounded hierarchy snapshot;
- scripts include source text plus a compact public interface summary;
- materials, textures, audio clips, importers, TMP fonts, audio mixers, timelines, input actions, UI Toolkit assets, render textures, terrain layers, avatar masks, shader variant collections, sprite atlases, tiles, video clips, VFX assets, meshes, shaders, and compute shaders include compact smart profiles where Unity exposes useful metadata;
- text-like assets return controlled text/YAML slices instead of forcing the agent to read entire files blindly.

Use `Action=Context` when a new agent has an exact path/GUID and needs to understand the asset before deciding what to do. `ContextProfile` controls emphasis:

- `Auto`: summary plus text slices for text-like assets, serialized payload for prefabs/scenes/materials/binary assets;
- `Summary`: metadata/type/importer summary only;
- `Text`: text slices/chunks only, plus metadata;
- `Serialized`: serialized importer/main/sub-asset or prefab/scene hierarchy payload;
- `Deep`: heavier context with sub-assets, dependencies, hierarchy, and serialized properties.

When a requested path is stale, `Context` returns fuzzy suggestions and, by default, a small context payload for the best suggestion.

Use `Action=Structure` when an agent needs a map of a prefab or loaded scene asset before making edits:

- `StructureMode=List` returns bounded hierarchy nodes and summary stats;
- `StructureMode=Search` uses `Query`, `ComponentFilter`, and optional `MatchFields=fields` or `MatchFields=all` to find objects by name/path/component/tag/layer/prefab source and serialized field names or values;
- `StructureMode=Read ObjectPath=<path-or-indexedPath>` drills into one object and returns transform, component details, renderer sorting data, child summaries, and bounded serialized properties;
- if duplicate names make a plain path ambiguous, pass the returned `indexedPath`;
- scene assets must already be loaded in the editor. `Action=Structure` is read-only and does not open unloaded scenes automatically. Use `Action=Read` for raw `.unity` YAML text or `SceneHierarchyExport` for full loaded-scene exports.

Use `IncludeReferenceLocations=true` with `Action=ReferenceGraph`, `Action=Dependents`, or `Action=Impact` when asset-level dependency names are not precise enough. UniBridge scans bounded text/YAML reference sites and returns:

- `line` and `column` for the referenced GUID;
- `propertyPath` inferred from YAML indentation;
- YAML document type/class/fileId;
- inferred `objectPath` and duplicate-safe `indexedObjectPath` for prefab/scene references when available;
- `componentType`, resolved MonoScript type, and a short preview line.

This is especially useful before deleting, renaming, moving, or replacing an asset because the agent can explain exactly which prefab/scene object and property will be affected.

Use `SerializeMode` to control depth:

- `Minimal`: small orientation payload;
- `Standard`: recommended default;
- `Full`: deeper properties/sub-assets, still bounded by `MaxSerializedProperties`, `MaxSerializedDepth`, and `MaxSerializedItems`.

Prefer `UniBridge_AssetIntelligence` for investigation and `UniBridge_ManageAsset` for actual AssetDatabase changes. Before structural asset changes, call `Action=Impact` or `Action=ReferenceGraph RefreshReferenceIndex=true` so the agent can see which assets will be affected.

`UniBridge_ManageAsset Action=CreateOrUpdate` supports a small generic asset-authoring allowlist for common non-Material/non-ScriptableObject assets:

- `PhysicsMaterial` / `PhysicsMaterial2D` with presets such as `bouncy`, `ice`, and `sticky`;
- `RenderTexture` with `width`, `height`, `depth`, `format`, `antiAliasing`, `filterMode`, `wrapMode`, and presets such as `ui` or `camera`;
- `TerrainLayer` with texture paths and tiling fields;
- `AvatarMask` with `preset`, `bodyParts`, and `transformPaths`;
- `ShaderVariantCollection` with explicit shader/pass/keyword variants.

This intentionally stays allowlisted. For Material, ScriptableObject, AnimatorController, importer settings, and prefabs, use their dedicated UniBridge tools.

### Control Editor State

Use `UniBridge_ManageEditor` for editor-level operations that should not require menu-item guessing:

- `GetState` returns play mode, compile/import/update state, readiness, windows, selection, tags/layers, and prefab-stage context depending on the action;
- `SelectAsset` selects and optionally pings a Project asset by `AssetPath`;
- `SelectGameObject` selects and optionally pings/frames a scene object by `GameObjectPath`, `Target`, or `InstanceID`;
- `ClearSelection`, `PingSelection`, and `FrameSelection` help an agent verify exactly what it is looking at before capture or edits;
- `RefreshAssets` wraps `AssetDatabase.Refresh` and can `WaitForCompletion`.
  If import/refresh triggers a Unity domain reload that closes the bridge,
  relay `1.1.0-build.15` reconnects and returns structured reload-boundary
  recovery data instead of a transport-level `Unity connection closed` error;
- `RequestPlayModeNoWait` queues entering Play Mode without waiting through a possible Unity domain reload;
- `WaitForPlayMode` and `WaitForEditMode` are reconnect-friendly Play Mode verification waits;
- `Play WaitForCompletion=true` and `ExitPlayMode WaitForCompletion=true` remain accepted for old callers, but now return controlled queued boundary responses instead of waiting inline through a reload-prone bridge connection;
- `RequestScriptCompilationNoWait` queues script compilation without waiting through Unity assembly reload;
- `WaitForReadyAfterReload` reconnect-friendly waits until Unity is ready after a compile/reload checkpoint and returns compilation diagnostics;
- `RequestScriptCompilation WaitForCompletion=true` remains accepted for old callers, but now returns a controlled queued response instead of waiting inline through a reload-prone bridge connection;
- `WaitForReady` waits until Unity is not compiling or importing, optionally also requiring not entering play mode;
- `SaveAll` saves dirty open scenes, the active prefab stage when possible, and project assets;
- `SaveAssets`, `ExitPlayMode`, `GetPlayModeState`, and `WaitIdle` are explicit aliases for common structured lifecycle operations;
- `GenerateSolutionFiles` / `GenerateSolutionFile` asks Unity to regenerate solution/project files when that editor API is available;
- `ReloadCheckpoint` refreshes externally changed assets and, when a modified scene or prefab-stage asset is involved, safely closes/reopens loaded scenes and Prefab Mode after saving unmodified dirty scenes.

For Play Mode smoke tests, prefer a split-phase workflow: clear/prepare console, queue `RequestPlayModeNoWait` or `Play`, then after reconnect call `WaitForPlayMode`, `WaitForReady RequireNotPlaying=false`, and `ReadConsole DiagnosticSummary`. Do not rely on a single in-process batch to span a Play Mode domain reload.

For script workflows, prefer `RefreshAssets WaitForCompletion=true`, `RequestScriptCompilationNoWait`, `WaitForReadyAfterReload`, then `GetCompilationDiagnostics` / `ReadConsole DiagnosticSummary` instead of interpreting console output while Unity is still compiling/importing. If `RefreshAssets` crosses a reload boundary, treat the returned `nextSuggestedCalls` as the continuation plan.

### Run Batch Actions

Use `UniBridge_BatchActions` when an agent needs to perform several related Unity operations as one planned workflow.

`DryRun` is `true` by default. In dry-run mode UniBridge validates each step and reports what would happen without changing the project.

Set `DryRun` to `false` only after the dry-run report looks correct.

Executing batches are transactional by default. `RollbackOnFailure=true` opens a Unity Undo group and, when `RollbackAssets=true`, captures a bounded snapshot of referenced `Assets/...` and `Packages/...` files before execution. If a required step fails or validation stops the batch after earlier steps already ran, UniBridge reverts the Undo group, restores captured files, deletes newly-created referenced roots, and reports the rollback outcome in `data.rollback`.

If a nested editor action returns a reload-safe boundary such as queued Play Mode entry/exit, `UniBridge_BatchActions` stops successfully at that step and returns `stopReason` plus `postReconnect.nextSuggestedCalls`. Run those follow-up calls after the bridge reconnects, then continue the remaining workflow in a new call.

If a `UniBridge_WorkSession` is active, executing batches (`DryRun=false`) append `data.workSessionReview` by default. This gives agents the current session summary, changed-file counts, risk counts, bounded changed-file samples, warnings, and suggested follow-up calls immediately after the batch. Use `IncludeWorkSessionReview=true` to include the same block in dry-runs, `IncludeWorkSessionReview=false` to suppress it, and `WorkSessionReviewMaxChanged` to tune response size.

For visible scene/UI/material/gameplay edits, pass `IncludeConsoleDelta=true`
to create a console marker before the batch and append
`data.postActionDiagnostics.consoleDelta` after the batch. The returned delta
keeps only compact totals, critical groups, warning groups, likely spam, and
recent representative samples for entries emitted during the batch. Pass
`IncludeEditorEventDelta=true` to also append
`data.postActionDiagnostics.editorEventDelta` with bounded editor events after
the batch start id.

Batch steps can call a curated set of local UniBridge tools:

- `UniBridge_ManageGameObject`;
- `UniBridge_ManageAsset`;
- `UniBridge_ManageAssetImporter`;
- `UniBridge_ManageMaterial`;
- `UniBridge_ManageScriptableObject`;
- `UniBridge_ManageScene`;
- `UniBridge_ManagePrefab`;
- `UniBridge_ManageAnimatorController`;
- `UniBridge_DomainCatalog`;
- `UniBridge_ManagePhysics3D`;
- `UniBridge_ManageNavigation`;
- `UniBridge_ManageRendering`;
- `UniBridge_ManageUIToolkit`;
- `UniBridge_ManageUI`;
- `UniBridge_ManageUnityEvent`;
- `UniBridge_ManageEditor`;
- `UniBridge_ManageShader`;
- `UniBridge_AssetIntelligence`;
- `UniBridge_ScriptIntelligence`;
- `UniBridge_ValidateScript`;
- `UniBridge_CaptureView`;
- `UniBridge_CaptureAsset`;
- `UniBridge_CaptureUIToolkit`;
- `UniBridge_VisualSceneAudit`;
- `UniBridge_SceneObjectView`;
- `UniBridge_SceneHierarchyExport`;
- `UniBridge_ManageSceneHierarchy`;
- `UniBridge_TypeSchema`;
- `UniBridge_UnitySearch`;
- `UniBridge_ContextSnapshot`;
- `UniBridge_EditorSnapshot`;
- `UniBridge_ToolGuide`;
- `UniBridge_ReadConsole`.

For convenience, steps can use aliases such as `game_object`, `asset`, `asset_importer`, `importer_settings`, `material`, `mat`, `material_settings`, `scriptable_object`, `so`, `data_asset`, `config_asset`, `domain`, `physics3d`, `3d_physics`, `navigation`, `navmesh`, `rendering`, `camera`, `lighting`, `uitoolkit`, `uxml_authoring`, `uidocument`, `asset_capture`, `asset_preview`, `render_asset`, `uitoolkit_capture`, `uxml_capture`, `uxml`, `visual_audit`, `self_check`, `scene_view`, `object_view`, `hierarchy_view`, `type_schema`, `component_schema`, `shader_schema`, `importer_schema`, `unity_search`, `unified_search`, `find`, `lookup`, `unity_event`, `persistent_event`, `persistent_call`, `asset_intelligence`, `script_intelligence`, `validate_script`, `script_validate`, `cs_validation`, `scene`, `prefab`, `animator_controller`, `ui`, `editor`, `manage_editor`, `selection`, `ready`, `save_all`, `compile`, `shader`, `capture`, `context`, `editor_snapshot`, `tool_guide`, or `console`.

Example dry-run:

```json
{
  "DryRun": true,
  "Name": "Create a probe object",
  "Steps": [
    {
      "tool": "game_object",
      "action": "create",
      "name": "Probe",
      "components_to_add": ["Rigidbody2D", "CircleCollider2D"]
    },
    {
      "tool": "scene",
      "Action": "Save"
    }
  ]
}
```

Example transactional execution:

```json
{
  "DryRun": false,
  "Name": "Create material and place preview",
  "IncludeConsoleDelta": true,
  "IncludeEditorEventDelta": true,
  "RollbackOnFailure": true,
  "RollbackAssets": true,
  "Steps": [
    {
      "tool": "asset",
      "action": "create_folder",
      "path": "Assets/Generated/Preview"
    },
    {
      "tool": "material",
      "action": "create_or_update",
      "path": "Assets/Generated/Preview/Glow.mat",
      "shader": "Sprites/Default"
    },
    {
      "tool": "game_object",
      "action": "create",
      "name": "Glow Preview"
    }
  ]
}
```

Script text editing tools are intentionally not included in batch actions. Use their dedicated SHA/precondition workflows directly. Read-only script validation is allowed in `UniBridge_BatchActions`, so an agent can validate several `.cs` files, refresh assets, request reload-safe compilation with `RequestScriptCompilationNoWait`, wait with `WaitForReadyAfterReload`, and read console diagnostics in one planned workflow.

### Review AI Work Sessions

Use `UniBridge_WorkSession` as a project-local safety layer around broad AI work. It complements `BatchActions`: `BatchActions` protects one planned execution, while `WorkSession` lets an agent review the whole work window before reporting completion.

Typical flow:

```text
UniBridge_WorkSession Action=Begin Name="Reorganize darkness scene"
...run normal domain-specific UniBridge tools...
UniBridge_WorkSession Action=Review
UniBridge_WorkSession Action=Diff Paths=[Assets/...]
UniBridge_WorkSession Action=Revert DryRun=true Paths=[Assets/...]
UniBridge_WorkSession Action=End
```

Session snapshots are written under `Library/UniBridge/WorkSessions`, outside source control. `Begin` records project files under `Assets`, `ProjectSettings`, and package manifest files by default, captures restorable bytes for text/YAML Unity assets under configurable size limits, captures a compact loaded-scene semantic baseline by default, and marks the session active.

When a semantic baseline exists, `Status`, `Review`, `UniBridge_BatchActions`
auto-review, and `UniBridge_ExecutionStatus` active-session summaries include a
`semanticReview` block. It compares the current loaded scene state against the
session baseline by stable scene object id and reports created/deleted/moved/
renamed GameObjects, component changes, renderer sorting/material changes,
prefab-info changes, transform changes, and missing-script deltas. This is a
visibility/self-check layer for live scene work; file revert behavior remains
strictly file-based.

`UniBridge_ExecutionStatus Action=Snapshot` and `Action=Recent` include active WorkSession review data by default, so an agent can check both tool scheduling state and the current changed-file/semantic-scene summary in one read-only call. Pass `IncludeWorkSession=false` for a scheduler-only response, or `WorkSessionMaxChanged=<n>` to limit changed-file samples.

Actions:

- `Begin`: create a checkpoint and make it active.
- `Status`: return active session metadata plus compact current file and semantic scene change counts.
- `Review`: list changed files with change type, asset kind, risk flags, hashes/sizes, whether UniBridge can revert them from the captured baseline, and semantic scene changes when enabled.
- `Diff`: return compact text diffs for selected changed files.
- `Revert`: defaults to `DryRun=true`; repeat with `DryRun=false` only after reviewing the plan. It restores modified/deleted captured files and deletes files added after the checkpoint.
- `End`: close the active session, optionally deleting session files.

Useful controls:

- `MaxFiles`, `MaxSingleCaptureBytes`, and `MaxTotalCaptureBytes`: bound scan and snapshot size.
- `IncludeSceneSemantics`, `MaxSemanticObjects`, `IncludeSemanticReview`, and `MaxSemanticChanges`: capture and bound loaded-scene semantic review.
- `IncludeProjectSettings`, `IncludePackageManifests`, and `IncludePackageFiles`: tune scope.
- `Paths`: selected project-relative files for `Diff` or `Revert`.
- `RevertAll=true`: revert every detected change from the session, usually after a dry-run review.

### Read Unity Console

Use `UniBridge_ReadConsole` to inspect compile errors, warnings, import issues, and runtime logs. For a quick overview, request errors and warnings without stack traces. For deeper debugging, include stack traces.

### Inspect Scene Objects

Use `UniBridge_SceneObjectView` for read-only structured orientation in loaded scenes. It is the quickest way to get a bounded hierarchy dump, inspect selected objects, or expand a specific GameObject with component details without changing the scene.

Available actions:

- `Hierarchy`: returns a compact flattened scene hierarchy and, when requested, structured root nodes.
- `View`: resolves GameObjects by id, hierarchy path, name, tag, layer, or component type and returns bounded object details.
- `Selection`: returns details for the current Unity selection.

Detail levels:

- `Brief`: names, ids, paths, scene info, child counts, and component names.
- `Standard`: adds tag/layer/static state, transform, bounds, prefab summary, and structured children.
- `Detailed`: adds known component summaries for renderers, cameras, lights, rendering layer masks, colliders, UI, animation, particles/VFX, audio, scripts, and similar common components.
- `Full`: adds bounded `SerializedObject` property output for components, optionally filtered by `IncludeComponentProperties`.

Useful inputs:

- `target` / `targets`: GameObject id, hierarchy path such as `/Canvas/Panel`, or name.
- `profile`: optional focus profile: `Rendering`, `Physics2D`, `Physics3D`, `UI`, `Animation`, `VFX`, `Audio`, `Gameplay`, `Navigation`, `Input`, `Tilemap2D`, `Lighting`, or `VideoTimeline`. Profiles automatically include useful component property filters and enable focused serialized properties by default.
- `search_method`: `Auto`, `ById`, `ByPath`, `ByName`, `ByTag`, `ByLayer`, or `ByComponent`.
- `scene_path`: loaded scene path or name, when multiple scenes are open.
- `index_display_mode=MetadataColumn`: includes duplicate-path indexes in flattened hierarchy output, mirroring the hierarchy metadata column.
- `max_depth`, `max_objects`, `max_children`, `max_roots`, `max_serialized_properties`: response-size controls.

Known summaries include renderer sorting/rendering layer masks, `Animator`, legacy `Animation`, `ParticleSystem`, `ParticleSystemRenderer`, `LineRenderer`, `TrailRenderer`, `AudioSource`, `SortingGroup`, `EventSystem`, `BaseInputModule`, `BaseRaycaster`, `ReflectionProbe`, `LightProbeGroup`, `LightProbeProxyVolume`, and optional reflection-based summaries for navigation, Input System, Tilemap/Grid, URP, Volume, SpriteShape, VideoPlayer, `VisualEffect`, and `PlayableDirector` when those packages are present.

For large scenes, use `UniBridge_SceneHierarchyExport` instead of increasing `SceneObjectView` limits. It performs a stable depth-first traversal with `objectId`, `parentObjectId`, `path`, `indexedPath`, `scenePath`, `siblingIndex`, active/tag/layer state, local/world transforms, missing-script counts, optional component summaries, renderer sorting/material details, prefab source info, and URP `Light2D` sorting-layer masks. It supports `Offset`/`Limit`/`Cursor` pagination and writes large full exports to `Library/UniBridge/SceneHierarchyExports` as JSON or JSONL. Every export response includes a compact `summary` with `totalObjects`, `rootObjects`, `inactiveObjects`, `missingScriptsTotal`, `objectsWithMissingScripts`, `rendererCount`, `rendererCountByType`, `rendererCountBySortingLayer`, `light2DCount`, `prefabInstanceCount`, `duplicatePathGroupCount`, and `topDuplicatePathGroups`. `MaxDuplicateSamples` controls how many sample object ids and indexed paths each duplicate group returns; the default is 3.

Use `UniBridge_ManageSceneHierarchy` for safe objectId-based hierarchy edits:

- `Reparent`: batch reparent or sibling-sort GameObjects by `ObjectId`, with `WorldPositionStays=true`, dry-run, one Unity Undo group, before/after diffs, existing planned parent metadata, and expected object-count delta validation.
- `CreateContainer`: create an empty organizational GameObject and move a set of object ids into it without changing components or active state. Dry-run moves include `plannedParentContainerName`, `plannedParentPath`, `plannedParentObjectId=null`, and `plannedParentWillBeCreated=true`.

Use `UniBridge_SceneHierarchyExport Action=CompareExports` to compare two exported scenes or two exported projects by `indexedPath` when available, then hierarchy path, prefab source, renderer sorting/materials, and `Light2D` layer masks. Duplicate path details are compact by default: `left/right` carry object totals and duplicate counts, `summary.duplicates` exposes flattened left/right group/object counters, and matching duplicate samples are returned once as `sharedTopGroups`. Set `IncludeDuplicateSummary=false` for count-only duplicate summaries, or set `IncludeDuplicateKeys=true`, `MaxDuplicateKeys`, and `MaxDuplicateSamples` when an agent needs bounded verbose duplicate examples. Mutating hierarchy results expose `objectCountValidation` with `mode`, `expectedDelta`, `actualDelta`, `plannedDelta` for dry-runs, and `passed`, plus compatibility fields such as `validationMode`, `expectedObjectCountDelta`, `actualObjectCountDelta`, and `objectCountValidationPassed`. `UniBridge_ExecutionStatus` records dry-runs as `mode=DryRun` and `changedProject=false`.

Use `UniBridge_ManageScene` for scene file operations and simple hierarchy snapshots. Use `UniBridge_ManageGameObject` when you need to create, modify, or delete scene objects and components.
`UniBridge_ManageGameObject` now presents PascalCase parameters as the canonical agent-facing API (`Action`, `Target`, `SearchMethod`, `ComponentsToAdd`, `ComponentName`, `ComponentProperties`). Legacy snake_case aliases are normalized internally.

### Capture Unity Views

Use `UniBridge_CaptureView` when an agent needs to inspect what the project looks like, not only what metadata says.

Available actions:

- `CaptureSceneView`: renders the Scene View camera to a PNG file. It can center and frame a target GameObject by name, hierarchy path, or ID. If the target is not found, the capture fails instead of returning an unrelated view.
- `CaptureGameView`: asks Unity to write the exact post-render Game View screenshot, useful when camera rendering misses overlays, post-processing, or Game tab presentation details.
- `CaptureGameCamera`: renders a Unity Camera to a PNG file. If no camera is specified, UniBridge uses `Main Camera` or the first available camera.
- `CaptureSelection`: frames the currently selected GameObject.
- `CaptureObject`: frames a required target GameObject.
- `CapturePrefabStage`: captures the currently open Prefab Mode stage.
- `CaptureSceneOverview`: frames the active scene or Prefab Stage root as an overview.
- `CaptureAroundObject`: frames a target with extra surrounding context.
- `CaptureSeries`: captures several Scene View or game-camera frames in one request.
- `CaptureContactSheet`: captures several Scene View directions, and optional time slices, then stitches them into one PNG grid with per-cell metadata.
- `CaptureDiff`: compares two PNG captures and writes a heatmap image.
- `ClearCaptures`: deletes old capture PNG files from the project capture folder.
- `ListCameras`: lists available cameras and their IDs/paths.

Captures are written to:

```text
~/.unibridge/captures/<project>
```

The capture uses Unity camera rendering, not desktop pixel capture. Unity does not need to be the foreground window for normal Scene View or camera captures.

Use `UniBridge_VisualSceneAudit` after visible scene, material, camera, lighting, VFX, or UI staging work. It is a deterministic self-check, not an art judge: it renders a camera or reads an existing PNG, analyzes broad pixel failures, inspects scene renderers/materials, checks target camera framing, and includes console diagnostics. This catches common agent mistakes before final reporting: fallback-magenta dominance, huge placeholder blocks, nearly blank/monochrome output, broken shaders, a target outside the camera, or warnings/errors left in the Unity Console.

Available actions:

- `AuditCapture`: render a camera to PNG and audit both the image and scene metadata.
- `AuditImage`: audit an existing PNG path.
- `AuditScene`: audit renderer/material/framing metadata without writing a PNG.

The tool returns `success=true` when the audit completed, with `data.passed=false` when quality gates fail. Set `FailOnIssues=true` when a batch or workflow should stop on audit failure. Batch aliases include `visual_audit`, `scene_audit`, `presentation_audit`, `self_check`, and `visual_qa`.

Use `UniBridge_CaptureAsset` when the target is a Project asset rather than a scene object. It renders supported assets into deterministic PNG previews instead of relying only on Unity thumbnail generation. Sprites and 2D textures use a flat front-facing capture by default, while 3D assets and materials use a temporary preview scene. `Action=CaptureGrid` resolves several assets, renders each preview, stitches them into a PNG contact sheet, and returns a numbered mapping back to asset paths. `Action=CaptureContactSheet` renders one asset across several views/time slices into one PNG, useful for checking a prefab, material, or mesh shape without making several calls.

Supported asset kinds:

- prefab/model `GameObject` assets;
- `Mesh` assets;
- `Material` assets, rendered on a sphere;
- `Sprite` assets, captured as flat 2D previews for `Auto`/`Front`;
- `Texture2D` assets, captured as flat 2D previews for `Auto`/`Front`, or rendered on a textured quad for other views.

Asset captures are written to:

```text
~/.unibridge/asset-captures/<project>
```

Asset capture options:

- `view`: `Auto`, `Iso`, `Front`, `Back`, `Left`, `Right`, `Top`, or `Bottom`;
- `orthographic`: true by default for deterministic previews;
- `transparent_background`: true by default for reusable asset previews;
- `padding`: framing padding around rendered bounds or flat 2D target rect.

CaptureGrid inputs:

- `paths` / `guids`: explicit asset list;
- `folder` / `folders`: folder-scoped visual browsing;
- `query` / `search_pattern`: AssetDatabase search query, such as `cake t:Texture2D`;
- `types`: optional renderable type filters such as `Sprite`, `Texture2D`, `Material`, `Mesh`, `GameObject`, or `Prefab`;
- `max_results`, `cell_width`, `cell_height`, `columns`, `include_labels`: contact-sheet layout controls. The PNG draws numeric badges, while the response metadata maps each index to the full asset path.

Use `UniBridge_CaptureUIToolkit` when the target is a UI Toolkit `VisualTreeAsset`/UXML document or a scene `UIDocument`. It follows the temporary `PanelSettings + UIDocument + RenderTexture` capture pattern, then adds UniBridge metadata that is useful for agent-side UI debugging: bounded VisualElement tree output, render/readback metadata, pixel coverage stats, blank-capture detection, zero-size element hints, invisible-opacity hints, likely text overflow, visible leaf overlap, and elements laid out outside the render target.

Available actions:

- `Capture`: renders a UXML or UIDocument to a deterministic PNG and returns tree/issues metadata.
- `Inspect`: renders offscreen and returns the resolved VisualElement tree/issues without writing a PNG.
- `ListUxml`: searches `VisualTreeAsset` assets by query/folders.

UI Toolkit captures are written to:

```text
~/.unibridge/uitoolkit-captures/<project>
```

Useful inputs:

- `path` / `guid`: exact UXML `VisualTreeAsset`;
- `target`: scene GameObject with a `UIDocument`;
- `query` / `folders`: search and resolve UXML assets when the exact path is unknown;
- `width`, `height`, `panel_scale`, `transparent_background`, `background_color`: render target controls;
- `readback_mode`: `Immediate` or `GpuReadback`; inspect returned `render.readbackMode` to see whether GPU readback or fallback was used;
- `render_passes`: forced UI Toolkit render/update passes before readback, clamped to `1..8`;
- `include_tree`, `include_issues`, `max_tree_depth`, `max_tree_items`: response-size and validation controls;
- `theme_style_sheet_path`: explicit UI Toolkit theme, otherwise UniBridge tries to reuse the target panel theme, the open UI Builder theme, or `UnityDefaultRuntimeTheme.tss`.

Scene/camera capture overlay and diff options:

- `overlay`: draws target and nearby-object bounds, color-coded numeric markers, and a compact legend into the PNG.
- `separate_overlay`: keeps the main capture clean and writes visual hints into a separate transparent PNG layer. UniBridge also writes a composite PNG unless `composite_overlay` is false.
- `annotations`: returned metadata that maps overlay marker numbers to full object names, IDs, paths, bounds, and component summaries.
- `include_nearby_objects`, `nearby_radius`, and `max_objects`: control the scene context metadata returned with captures.
- `transparent_background`: writes alpha into the PNG when explicitly needed. By default captures are opaque so Game Camera output matches Unity Game View.
- `baseline_path` and `compare_path`: inputs for `CaptureDiff`. Use captures with the same dimensions for pixel diff.

### Search Unity

Use `UniBridge_UnitySearch` as the first lookup when the agent does not yet know whether the user means a Project asset, a scene object, a C# script/type, a shader, or an editor menu command. It follows the `SearchAssetsByQuery` idea of returning a single ranked result list with normalized handles, but keeps the result MCP-native and points to the next specialized UniBridge tool.

Available actions:

- `Search`: return ranked matches across selected sources;
- `Resolve`: return the best match plus ambiguity hints when the top scores are close;
- `Selection`: normalize the current Unity selection into the same result shape.

Sources:

- `Assets`: Project assets from `AssetDatabase`;
- `SceneObjects`: open scene hierarchy objects, including inactive objects by default;
- `Scripts`: `MonoScript` assets and compiled class metadata;
- `Types`: loaded Unity/C# types, useful before `UniBridge_TypeSchema`;
- `Shaders`: shader assets by path/name;
- `Menus`: editor menu commands for `UniBridge_ManageMenuItem`.

Each result includes a source, kind, name, path/GUID or scene object id, score, matched fields, and a `suggestedTool` / `suggestedAction` pair for the follow-up step.

Backends:

- `UniBridge`: deterministic AssetDatabase/scene/type scans and the default backend for repeatable automation;
- `NativeSearchService`: UnityEditor.Search `asset`/`scene` providers, useful when an agent wants parity with Unity's own indexed search behavior;
- `Hybrid`: combines NativeSearchService with UniBridge scanning and de-duplicates by stable handles.

Example:

```json
{
  "Action": "Search",
  "Query": "Main Camera",
  "Sources": ["SceneObjects", "Types"],
  "Limit": 10
}
```

### Inspect Type Schemas

Use `UniBridge_TypeSchema` before patching component, ScriptableObject, AssetImporter, asset, material, or shader data. It follows the `GetTypescriptDefinitions` / shader schema idea, but returns MCP-native JSON instead of TypeScript text.

Available actions:

- `ListTypes`: find loaded Unity types by `Kind` and `Query`;
- `TypeFingerprint`: return the loaded-assembly fingerprint and index key for
  deciding whether a cached type index is still valid;
- `TypeIndex`: return a compact loaded Unity type map and, with
  `WriteToFile=true`, write a bounded full JSON index under
  `Library/UniBridge/TypeIndex`;
- `Inspect`: inspect `TypeName` / `TypeNames`, or dispatch to asset, shader, or GameObject inspection when `Path` / `Guid`, `Shader`, or `Target` is provided;
- `InspectShader`: return shader property names, types, default values, ranges, flags, attributes, texture dimensions, and material current values when the source is a material;
- `InspectAsset`: return the main asset schema, importer schema, material shader schema, or shader schema for `Path` / `Guid`;
- `InspectGameObject`: return schemas for components on a scene GameObject, optionally filtered by `ComponentTypes`.

Useful options:

- `Kind`: `Any`, `Component`, `MonoBehaviour`, `ScriptableObject`, `AssetImporter`, `Asset`, or `Shader`;
- `IncludeSerializedProperties`: include exact `SerializedObject` property paths for live objects/assets/importers;
- `IncludeValues`: include current serialized values when cheap and safe to serialize;
- `IncludePrivateSerialized`, `IncludeInherited`, `IncludeReadOnly`, `IncludeObsolete`: control how much reflection data is returned;
- `IncludeNonPublicTypes`: include non-public loaded types in `ListTypes` and
  `TypeIndex` when a focused lookup needs them;
- `WriteToFile` and `MaxTypeIndexEntries`: save a bounded full `TypeIndex`
  JSON file while returning only compact samples through MCP;
- `Limit` and `MaxSerializedProperties`: bound large type catalogs and serialized snapshots.

`TypeIndex` entries include `simpleName`, `fullName`, `assembly`, `kind`,
`domainTags`, `baseType`, ambiguity summaries, and follow-up hints for
`TypeSchema Inspect`, `ManageGameObject AddComponent`, or ScriptableObject
authoring.

`UniBridge_BatchActions` accepts `UniBridge_TypeSchema` steps for
`TypeIndex`, `TypeFingerprint`, and aliases such as `type_index`, `type_map`,
and `fingerprint`. This lets an agent resolve types inside a larger dry-run or
execution workflow before authoring components or assets.

Serialized property schemas include `domainTags` on type summaries and support JSON-safe `AnimationCurve` / `Gradient` values. Those values can be passed back through generic property patchers as:

- `AnimationCurve`: `{ "keys": [{ "time": 0, "value": 0, "inTangent": 0, "outTangent": 0 }], "preWrapMode": "Clamp", "postWrapMode": "Clamp" }`;
- `Gradient`: `{ "mode": "Blend", "colorKeys": [{ "time": 0, "r": 1, "g": 1, "b": 1, "a": 1 }], "alphaKeys": [{ "time": 0, "alpha": 1 }] }`.

Example:

```json
{
  "Action": "Inspect",
  "Kind": "Component",
  "TypeName": "UnityEngine.SpriteRenderer",
  "IncludeSerializedProperties": false
}
```

### Work With Scripts

Use `UniBridge_ScriptIntelligence` when an agent needs to understand C# scripts before editing them.

It is read-only and supports:

- `Catalog`: list scripts and compiled types with path, kind, assembly, and base type. Set `IncludeMembers=true` only for focused catalogs where callbacks and Inspector-facing fields are needed;
- `Analyze`: inspect one script by path, GUID, type name, or query;
- `ReadTypes`: return source and summaries for specific component or ScriptableObject types;
- `References`: search C# files for a type, member, text, or regex pattern;
- `Usages`: find scenes, prefabs, and assets that reference a script asset;
- `MemberUsages`: find Unity serialized references to one script member, including UnityEvent method bindings, AnimationEvent function names, and serialized fields;
- `CodeUsages`: find C# source call sites and type/member references before risky renames, deletes, or signature changes;
- `ChangeImpact`: compare the current script with `ProposedSource` or `ProposedPath` and estimate syntax, API, serialized-field, Unity-callback, and reload risk before applying the edit;
- `Hotspots`: find likely cleanup points such as TODO/FIXME, file/class mismatches, obsolete Unity APIs, large files, or `UnityEditor` references in runtime folders;
- `Assemblies`: summarize Unity compilation assemblies and asmdefs;
- `Selection`: analyze selected script assets;
- `Metrics`: summarize script counts by kind, assembly, folder, and Unity callback.

Prefer this tool for orientation and impact analysis. Use the dedicated edit tools below when it is time to change code.

For script migration or deletion checks, call `Action=Usages IncludeUsageLocations=true`. Usage locations resolve prefab/scene YAML references to the script GUID and include line/column, property path, YAML document context, inferred object path, duplicate-safe indexed object path, and resolved script type where Unity can load the `MonoScript`.

For member rename/delete checks, combine:

- `Action=MemberUsages Path=Assets/.../<script>.cs Member=<methodOrField>` to find serialized UnityEvent, AnimationEvent, and inspector-field references in Unity assets;
- `Action=CodeUsages Path=Assets/.../<script>.cs Member=<methodOrField>` to find C# callers and references.

`CodeUsages` is read-only and syntax-based. `Exact` means the reference is qualified by the target type name, `Possible` means the name matches but the semantic receiver type was not resolved, and `RuntimeResolved` covers string-based callbacks such as `SendMessage("Method")`, `Invoke("Method")`, and `StartCoroutine("Method")`. Use `IncludeSelfReferences=true` when internal references inside the target script matter; by default it focuses on external callers.

For larger source edits, call:

- `Action=ChangeImpact Path=Assets/.../<script>.cs ProposedSource=<candidateSource>` or
  `Action=ChangeImpact Path=Assets/.../<script>.cs ProposedPath=Assets/.../<candidate>.cs`
  before applying the edit.

`ChangeImpact` is read-only. It reports proposed-source syntax diagnostics,
type/member shape diffs, public API risk, inspector serialized-field risk,
Unity callback risk, source line/character deltas, expected refresh/compile/
domain-reload boundaries, and suggested next calls for follow-up scans and
post-edit verification.

Use:

- `UniBridge_ListResources` to find scripts and assets;
- `UniBridge_ReadResource` to read exact file contents;
- `UniBridge_FindInFile` to locate methods, classes, or anchors;
- `UniBridge_GetSha` before edits when you want a file-change guard;
- `UniBridge_ApplyTextEdits` for precise small line/column edits;
- `UniBridge_ScriptApplyEdits` for safer method, class, or anchor-based edits;
- `UniBridge_ValidateScript` after script changes.

### Work With Assets

Use `UniBridge_ManageAsset` for generic AssetDatabase operations such as search, folder creation, asset info, delete, duplicate, move, and rename.

Use `UniBridge_ManageAssetImporter` when the important change is on the asset's importer rather than the asset file itself. It supports `Inspect`, `SetProperties`, `ApplyPreset`, and `Reimport` for `TextureImporter`, `ModelImporter`, `AudioImporter`, and other Unity `AssetImporter` types.

`ApplyPreset` covers common agent workflows: `TextureSprite2D`, `TextureUI`, `TextureReadable`, `TextureNormalMap`, `ModelStatic`, `ModelAnimated`, `Audio2D`, and `AudioStreaming`. `Properties` can still patch public importer properties or serialized property paths directly, and it is applied after a preset so agents can override just the fields they need.

Importer mutations under `Packages/...` are blocked unless `AllowPackages=true`, while `Inspect` remains available for package assets.

Use `UniBridge_ManageMaterial` when the important change is on a `.mat` asset. It supports `Inspect`, `Validate`, `CreateOrUpdate`, `SetShader`, `SetProperties`, and `ApplyPreset`.

Material updates are shader-aware. `Inspect` returns the current shader, material settings, keywords, and bounded shader property metadata with current values. `SetProperties` validates property names against the material shader before applying values, including colors, vectors, floats/ranges, ints, textures, and texture scale/offset properties such as `_BaseMap_ST` or `_MainTex_ST`.

`Properties` can use direct shader property names:

```json
{
  "_BaseColor": { "r": 1, "g": 0.7, "b": 0.2, "a": 1 },
  "_BaseMap": "Assets/Textures/Icon.png",
  "_BaseMap_ST": { "x": 1, "y": 1, "z": 0, "w": 0 }
}
```

It also accepts the structured shape:

```json
{
  "shader": {
    "shaderPath": "Universal Render Pipeline/Lit",
    "props": {
      "_BaseColor": "#ffcc33ff"
    }
  },
  "enableInstancing": true
}
```

`ApplyPreset` covers common workflows: `URPLit`, `URPUnlit`, `Standard`, `UnlitColor`, `SpriteDefault`, `UIDefault`, `Transparent`, and `Cutout`. `TexturePath` and `Color` are convenience inputs for common main texture and color properties, while explicit `Properties` are applied last.

Use `UniBridge_ManageScriptableObject` when the important change is on a `.asset` data/config object that inherits from `UnityEngine.ScriptableObject`. It supports `ListTypes`, `Inspect`, `Validate`, `CreateOrUpdate`, and `SetProperties`.

ScriptableObject updates are `SerializedObject`-aware. Public fields/properties and Unity serialized fields can be patched by field name or serialized property path, including primitive values, strings, enums, colors, vectors, object references, and common arrays. `Inspect` returns bounded serialized property snapshots so the agent can verify exact field names before mutating data assets.

Example:

```json
{
  "Action": "CreateOrUpdate",
  "Path": "Assets/Data/EnemyTuning.asset",
  "ScriptableObjectType": "Game.EnemyTuning",
  "Properties": {
    "displayName": "Scout",
    "health": 45,
    "tint": "#44ccffff"
  }
}
```

It also accepts the structured shape:

```json
{
  "Path": "Assets/Data/EnemyTuning.asset",
  "ScriptableObject": {
    "scriptableObjectType": "Game.EnemyTuning",
    "props": {
      "displayName": "Scout",
      "health": 45
    }
  }
}
```

Use `UniBridge_ImportExternalModel` for importing external FBX models and creating reusable prefabs.

### Work With UI And RectTransforms

Use `UniBridge_ManageUI` for Canvas-based UI and RectTransform layout work.

It supports:

- inspecting UI objects, including RectTransform anchors, pivot, offsets, size, world corners, Canvas, and children;
- creating a Canvas with CanvasScaler and GraphicRaycaster;
- creating or repairing an EventSystem with the project's preferred UI input module;
- creating Empty, Panel, Image, Text, Button, TextMeshProText, and TextMeshProButton UI objects;
- creating higher-level UI templates with `CreateTemplate`: `Panel`, `Modal`, `Toolbar`, `List`, `CardGrid`, and `HUD`;
- creating ScrollRect-based lists with `CreateScrollView` and adding rows with `AddScrollItem`;
- configuring Image, RawImage, Text, and TextMesh Pro `Graphic` state with `SetGraphic`, including color, sprite, texture, material, raycast target, Image type, preserve aspect, and native size;
- configuring Button/Selectable interaction visuals with `SetSelectableTransition`, including `ColorTint`, `SpriteSwap`, `Animation`, target graphic, color block, and sprite state;
- adding or clearing persistent Button `onClick` listeners with `SetButtonEvent` and `ClearButtonEvents`;
- controlling legacy Text/Button and TextMesh Pro label size, alignment, color, rich text, overflow mode, and optional best-fit or auto-sizing behavior;
- applying common RectTransform layout presets: horizontal `Left`, `Center`, `Right`, `Stretch` and vertical `Top`, `Middle`, `Bottom`, `Stretch`;
- directly setting anchors, pivot, anchored position, size, offsets, and scale;
- adding or updating `HorizontalLayoutGroup`, `VerticalLayoutGroup`, and `GridLayoutGroup`;
- adding or updating `ContentSizeFitter` for automatic container sizing;
- adding or updating `LayoutElement` so children inside layout groups keep predictable minimum, preferred, and flexible sizes;
- validating or auditing UI for common quality issues such as legacy/TMP text overflow, missing TMP font assets, sibling overlap, invisible elements, zero scale, tiny rects, disabled Canvas/Graphic components, CanvasGroup alpha hiding, missing CanvasScaler/EventSystem setup, duplicate layout groups, suspicious Canvas sorting, and ScreenSpaceOverlay capture caveats;
- building an AI-readable `RepairPlan` before making changes;
- auto-fixing audit findings in `Safe`, `Layout`, or `Aggressive` modes.

The layout preset action is useful when an agent needs to reproduce Unity's anchor preset behavior without guessing the RectTransform math. `AlsoSetPivot=true` aligns the pivot to the preset. `AlsoSetPosition=false` preserves the current visual placement while changing anchors, while `AlsoSetPosition=true` snaps the object to the new layout.

For most generated UI, prefer layout groups over manual coordinates:

```text
SetLayoutGroup      -> configure a panel, row, column, or grid container
SetLayoutElement    -> define how one child should size inside that container
SetContentSizeFitter -> let a container grow or shrink around its content
```

This makes agent-created UI more robust when text length, screen resolution, or child count changes.

Use `CreateTemplate` when you want a usable screen section instead of placing many RectTransforms by hand. Templates are built from the same primitives as the lower-level actions: Canvas, panels, layout groups, LayoutElement, ScrollRect, TextMesh Pro when available, and post-create validation.

Useful template parameters:

```text
TemplateType: Panel | Modal | Toolbar | List | CardGrid | HUD
Title / Subtitle: header text
ItemTexts: body rows, list entries, HUD stats, or card labels
ActionTexts: button labels for modal actions, toolbars, HUD action bars, or footers
Columns: preferred CardGrid column count
UseTextMeshPro: true by default when TMP is available
ValidateAfterCreate: true by default; includes Validate summary in the response
```

Action aliases such as `create_modal`, `create_toolbar`, `create_list`, `create_card_grid`, and `create_hud` normalize to `Action=CreateTemplate` and set the matching `TemplateType` in batch workflows.

Use `SetGraphic` after creating panels, images, buttons, icons, or raw texture previews when the visual asset should be explicit instead of manually editing Unity components.

Useful Graphic parameters:

```text
SpritePath: Assets/... or asset GUID for Image.sprite
TexturePath: Assets/... or asset GUID for RawImage.texture
MaterialPath: Assets/... or asset GUID for Graphic.material
Color / BackgroundColor: [r,g,b] or [r,g,b,a], 0..1 or 0..255
ImageType: Simple | Sliced | Tiled | Filled
PreserveAspect / RaycastTarget / SetNativeSize
HighlightedSpritePath / PressedSpritePath / SelectedSpritePath / DisabledSpritePath
```

Use `SetSelectableTransition` for buttons and other `Selectable` controls before visual validation. It mirrors Unity's normal Button contract:

```text
Transition: None | ColorTint | SpriteSwap | Animation
TargetGraphic: optional Graphic object path/id; defaults to the current targetGraphic or own Graphic
NormalColor / HighlightedColor / PressedColor / SelectedColor / DisabledColor
ColorMultiplier / FadeDuration
HighlightedSpritePath / PressedSpritePath / SelectedSpritePath / DisabledSpritePath
```

Use `SetButtonEvent` when a generated UI button needs a real persistent listener in the scene:

```text
Target: Button object
EventTarget: GameObject that owns the listener; defaults to the button object
EventComponent: optional component type such as Button, GameObject, or YourController
EventMethod: public void method name
EventArgumentType: Void | String | Int | Float | Bool
EventArgument: argument value for non-void listeners
ClearExistingEvents: true to replace existing listeners
```

`Inspect` returns `graphic`, `selectable`, and `button.onClick` snapshots so an agent can confirm assigned assets, transition settings, and persistent calls without guessing.

Use `UniBridge_ManageUnityEvent` when the event is not only a Button `onClick`, or when the agent needs the full structured `persistentCalls` shape:

```text
Action: Inspect | AddPersistentCall | SetPersistentCalls | ClearPersistentCalls
Target: GameObject path/name/id that owns the event
Component: optional owner component type, such as Button, Toggle, Slider, or a custom MonoBehaviour
EventProperty: onClick, m_OnClick, onValueChanged, or a custom UnityEvent field/property
PersistentCalls: { persistentCalls: [ { target, component, methodName, argument, callState } ] }
```

The single-call convenience form uses `EventTarget`, `EventComponent`, `MethodName`, optional `Argument`, and optional `CallState`. Static persistent arguments support `int`, `float`, `string`, `bool`, and Unity object references. `DryRun=true` previews the exact listeners before changing the scene.

Use `CreateScrollView` for inventory lists, logs, asset lists, settings panels, toolbars, and any UI where content may exceed the visible area. It creates a standard Unity hierarchy:

```text
ScrollView
  Viewport   -> RectMask2D clipping
    Content  -> LayoutGroup + ContentSizeFitter
```

Useful ScrollView parameters:

```text
ScrollDirection: Vertical | Horizontal | Both
SizeDelta: visible ScrollView container size
ItemSizeDelta: row/button/item size inside Content
ItemTexts: initial labels; one item is created for each text
LayoutGroupType: Vertical | Horizontal | Grid
CellSize / Constraint / ConstraintCount: grid content sizing
MovementType: Clamped | Elastic | Unrestricted
ScrollSensitivity / Inertia / Elasticity / DecelerationRate
UseRectMask2D: true by default
```

`SizeDelta` controls the visible viewport. Use `ItemSizeDelta` when you want custom row, toolbar button, or list-item dimensions. For grid content, prefer `CellSize`.

`AddScrollItem` accepts the ScrollView root, its `Viewport`, or its `Content` object as `Target` and resolves the real content container automatically. This lets agents append rows without hard-coding the full hierarchy path.

For modern Unity UI text, prefer `TextMeshProText` and `TextMeshProButton` when TextMesh Pro is available in the project. UniBridge uses the loaded `TMPro.TextMeshProUGUI` type without adding a hard package dependency. If TextMesh Pro has not been initialized in the Unity project yet, import **TMP Essentials** once from Unity's TMP Importer. Examples & Extras are optional and are not required by UniBridge.

Useful TextMesh Pro parameters:

```text
ElementType: TextMeshProText | TextMeshProButton
RichText: true | false
OverflowMode: Overflow | Ellipsis | Truncate | Masking | Linked
FontAssetPath: Assets/.../YourFont.asset
CreateTmpFontAssetIfMissing: true | false
BestFit: true -> enables TMP auto sizing
MinFontSize / MaxFontSize -> auto-sizing bounds
```

When no `FontAssetPath` is provided, UniBridge looks for an existing `TMP_FontAsset` and prefers the main `LiberationSans SDF.asset` installed by TMP Essentials over fallback assets. If no TMP font asset exists and `CreateTmpFontAssetIfMissing=true`, UniBridge attempts to create a small project-local default TMP font asset under `Assets/UniBridgeGenerated/TextMeshPro`.

When a UI element is created directly inside a `HorizontalLayoutGroup`, `VerticalLayoutGroup`, or `GridLayoutGroup`, UniBridge preserves an explicit `SizeDelta` as a `LayoutElement`. This keeps generated buttons and text blocks from collapsing when Unity recalculates the layout.

Use `Validate` or `Audit` after UI changes when you want an AI-readable quality report before taking a screenshot or committing a scene. Both actions are read-only and return issue codes, severity, target object info, focused details, and an `issueCodes` summary grouped by problem type. `Validate` is the clearer action name when you want a pass/fail style check after generated UI work; `Audit` is kept as the same underlying scan for analysis workflows.

Important validation categories:

```text
TEXT_OVERFLOW_RISK
SIBLING_OVERLAP
CHILD_OUTSIDE_PARENT
ZERO_OR_TINY_RECT / ZERO_SCALE
DISABLED_CANVAS / DISABLED_GRAPHIC
INVISIBLE_GRAPHIC_ALPHA / INVISIBLE_CANVAS_RENDERER_ALPHA
UI_HIDDEN_BY_CANVAS_GROUP
MISSING_CANVAS_SCALER / MISSING_GRAPHIC_RAYCASTER / MISSING_EVENT_SYSTEM
LOW_CANVAS_SORTING_ORDER / CANVAS_SORTING_CONFLICT
SCREEN_SPACE_OVERLAY_CAPTURE_CAVEAT
```

Use `RepairPlan` when you want UniBridge to explain what it can fix before it touches the scene. The plan includes the issue code, target, risk level, required `AutoFixMode`, and a ready-to-run dry-run command for each supported finding.

Use `AutoFix` when audit findings are safe to resolve mechanically. Always prefer `DryRun=true` first when working on user scenes.

`AutoFixMode=Safe` only fixes low-risk setup/text issues:

```text
MISSING_CANVAS_SCALER
MISSING_GRAPHIC_RAYCASTER
MISSING_EVENT_SYSTEM
TEXT_OVERFLOW_RISK
```

`AutoFixMode=Layout` also allows local RectTransform repair:

```text
MULTIPLE_LAYOUT_GROUPS
CHILD_OUTSIDE_PARENT
SIBLING_OVERLAP
```

`AutoFixMode=Aggressive` may convert a manual UI container into an inferred `HorizontalLayoutGroup`, `VerticalLayoutGroup`, or `GridLayoutGroup`. When it does this, UniBridge snapshots direct child sizes before adding the layout group and writes `LayoutElement` values back to the children, so controls do not collapse when Unity recalculates the layout.

Use the aggressive mode for generated or test UI first. For hand-authored production UI, inspect the `RepairPlan` and run a dry run before applying it.

`Validate`, `Audit`, and `AutoFix` evaluate child containment and sibling overlap in the parent RectTransform's local space. This keeps camera-space and world-space UI checks reliable, where world-unit tolerances can otherwise hide overlap or outside-parent problems.

When an EventSystem is created or repaired, UniBridge prefers `InputSystemUIInputModule` when the Unity Input System package is available and the project is not explicitly configured as old-input-only. It falls back to `StandaloneInputModule` for classic Input Manager projects.

Use `DryRun=true` to preview UI changes before changing the scene.

For camera-space UI, `CreateCanvas` enables `overrideSorting` by default and uses a sorting order of `100`, so AI-created UI is less likely to be hidden behind scene sprites or meshes. Override `SortingOrder` when a project has its own UI layering rules.

### Work With Prefabs

Use `UniBridge_ManagePrefab` for prefab-specific workflows:

- create a prefab from a scene object;
- create a prefab from a GameObject, model, or prefab asset;
- instantiate a prefab into the active scene, a loaded scene, or the current Prefab Stage;
- inspect prefab status, source asset path, variant/instance state, and override counts;
- inspect detailed prefab instance overrides with `diff_overrides`;
- apply or revert all prefab instance overrides;
- apply or revert selected overrides by `override_id`, kind, object path, component type, or property path;
- remove unused prefab overrides;
- unpack prefab instances;
- create prefab variants;
- open, save, or close Prefab Mode.

`diff_overrides` returns an AI-readable prefab override report grouped by property changes, object overrides, added/removed components, and added/removed GameObjects. Each returned override has a stable `id` for the current prefab state, so follow-up calls can dry-run or execute targeted `apply_override` / `revert_override` operations without applying every override on the instance.

Use `UniBridge_ManageGameObject` for normal scene object and component changes. Use `UniBridge_ManageAsset` for generic asset database operations. Use `UniBridge_ManagePrefab` when the operation depends on Unity's prefab system.

### Work With Animator Controllers

Use `UniBridge_ManageAnimatorController` for Mecanim Animator Controller assets.

It supports:

- finding and inspecting `.controller` assets;
- validating controller structure, duplicate parameters/states, missing default states, and missing condition parameters;
- creating controller assets under `Assets/`;
- adding and removing parameters;
- adding and removing layers, including avatar masks, IK pass, synced layer index, and synced-layer timing;
- adding and removing states and nested state machines;
- assigning `AnimationClip` or `Motion` assets to states;
- creating and configuring BlendTrees;
- adding, replacing, and clearing BlendTree child motions;
- setting a layer's default state;
- adding and removing state transitions, Any State transitions, Entry transitions, Exit transitions, and transitions to destination state machines;
- applying a complete controller graph with one structured `apply_graph` request.

This tool uses Unity's `UnityEditor.Animations` API. It does not edit controller YAML directly.

Use `UniBridge_BatchActions` with `DryRun=true` when planning several Animator Controller changes at once. This lets UniBridge validate the graph operation before changing the asset.

BlendTree actions support `Simple1D`, `SimpleDirectional2D`, `FreeformDirectional2D`, `FreeformCartesian2D`, and `Direct` trees. Child definitions can include `motion_path`, `threshold`, `position`, `time_scale`, `cycle_offset`, `mirror`, and `direct_blend_parameter`. If `motion_path` is omitted, UniBridge creates an empty placeholder child so the graph can be laid out first and connected to clips later.

Use `apply_graph` when an agent already knows the desired controller shape. The graph can describe parameters, layers, nested `state_machines`, states, state motions or BlendTrees, default states, Entry transitions, state-level transitions, layer-level transitions, Any State transitions, Exit transitions, and destination-state-machine transitions. By default it creates or updates what is described and keeps extra existing items. Set `remove_missing_parameters`, `remove_missing_layers`, or `remove_missing_states` only when you intentionally want pruning. Set `dry_run=true` to get the planned change report without modifying the asset.

State and Any State transitions in a graph must have either `has_exit_time=true` or at least one condition. Unity ignores transitions with neither, so UniBridge rejects them during graph validation. Entry transitions are allowed without conditions.

## Troubleshooting

### UniBridge settings do not appear

Check that the package folder is named `com.cidonix.unibridge` and Unity has finished recompiling. Then reopen Project Settings and look for `UniBridge > MCP`.

### MCP client cannot connect

Open `Project Settings > UniBridge > MCP` and check that Local Bridge status is `Running`.

Use the integration snippet from the same Unity project so the client points to the correct relay path and project ID.

If Unity was restarted while the MCP client stayed open, ask the client to reconnect its MCP server. The relay also drops stale pipes automatically on the next failed command and retries once against the current Unity bridge.

### Wrong Unity project is targeted

Add `--project-id PROJECT_ID` to the MCP client configuration. The project ID is stored in `ProjectSettings/UniBridge/project.json`.

### Approval dialog appears repeatedly

Check whether the MCP client executable path, hash, or signing identity changed. UniBridge treats changed executable identity as a new connection identity.

### Relay is unsigned

Unsigned builds can still run, but Unity and the operating system may show stronger warnings. Review the executable path and hash before allowing access.

### macOS blocks the relay

macOS may require manual approval for unsigned or unnotarized executables. Allow the relay only if it came from a trusted UniBridge package.

### Linux or macOS relay cannot start

Check that the relay file exists under `~/.unibridge/relay` and has executable permissions. Reinstall or refresh the relay from Project Settings if needed.
