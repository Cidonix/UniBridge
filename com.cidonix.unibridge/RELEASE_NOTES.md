# UniBridge 0.2.23 Release Notes

Release date: 2026-06-16

This release adds C# caller/type impact scanning to
`UniBridge_ScriptIntelligence`. Agents can now ask where a script type or
member is referenced in C# source before renaming, deleting, or changing a
public API.

`UniBridge_ScriptIntelligence Action=CodeUsages` accepts a target script or
`TypeName` plus optional `Member=<methodOrField>`. It scans C# scripts and
returns bounded call/type sites with path, GUID, line, column, usage kind,
confidence, symbol, containing code context, note, and preview line.

The scan is intentionally syntax-based and read-only. `Exact` means the site is
qualified by the target type name, `Possible` means the name matches but the
semantic receiver was not resolved, and `RuntimeResolved` covers string-based
callbacks such as `SendMessage("Method")`, `Invoke("Method")`, or
`StartCoroutine("Method")`.

Discoverability was updated across `BatchActions`, `Discover`, `ToolGuide`, and
`DomainCatalog` with aliases such as `code_usages`, `caller_scan`, `callers`,
`member_callers`, and `code_member_usages`. `UniBridge_BatchActions` also
normalizes `IncludeSelfReferences`, `IncludeStringReferences`, and
`MaxReferences` for the new workflow.

Use the three script impact scans together:

- `Action=Usages IncludeUsageLocations=true` for prefab/scene YAML references to
  a script GUID;
- `Action=MemberUsages Member=<methodOrField>` for UnityEvent, AnimationEvent,
  and serialized field references in Unity assets;
- `Action=CodeUsages Member=<methodOrField>` for C# caller/type references.

The previous 0.2.22 release added serialized member usage inspection to
`UniBridge_ScriptIntelligence`. Agents can now ask where a specific script
member is referenced from Unity serialized assets before renaming or removing
callbacks and inspector fields.

`UniBridge_ScriptIntelligence Action=MemberUsages` accepts a target script plus
`Member=<methodOrField>`. It scans bounded Unity asset YAML and reports
`UnityEvent` persistent method bindings, `AnimationEvent` function names, and
serialized field entries on target script components. Results include asset
path, line/column, property path, YAML document info, object path,
duplicate-safe `indexedObjectPath`, component/script type, usage kind,
confidence, note, and preview text.

Discoverability was updated across `BatchActions`, `Discover`, `ToolGuide`,
and `DomainCatalog` with aliases such as `member_usages`,
`serialized_member_usages`, `serialized_member_search`,
`unity_event_usages`, `animation_event_usages`, and
`serialized_field_usages`. `UniBridge_BatchActions` also normalizes
`member`/`method`/`field`/`function` parameter aliases for the new workflow.

The previous 0.2.21 release added location-aware reference inspection to existing
`UniBridge_AssetIntelligence` and `UniBridge_ScriptIntelligence` workflows.
Agents can now ask for exact YAML reference sites instead of only seeing that
one asset depends on another.

`UniBridge_AssetIntelligence Action=ReferenceGraph`, `Dependents`, and
`Impact` accept `IncludeReferenceLocations=true` plus
`MaxReferenceLocations`. Returned locations include the source asset path,
target GUID/path, line, column, inferred YAML property path, YAML document
type/class/fileId, inferred prefab/scene object path, duplicate-safe
`indexedObjectPath`, component/script type, and a short preview line.

`UniBridge_ScriptIntelligence Action=Usages` and
`Analyze IncludeUsages=true` now support `IncludeUsageLocations=true` and
`MaxUsageLocations`, so an agent can find exactly which prefab/scene YAML
objects reference a script GUID before component migration or deletion.

Discoverability was updated across `BatchActions`, `Discover`, `ToolGuide`,
and `DomainCatalog` with aliases such as `asset_ref_search`,
`asset_reference_search`, `asset_usages`, `reference_graph`,
`reference_locations`, `script_usages`, `asset_script_usages`, and
`guid_usages`.

The previous 0.2.20 release added `UniBridge_AssetIntelligence Action=Structure`, a read-only
asset structure workflow for prefab assets and already-loaded scene assets.
Agents can call `StructureMode=List` to get a compact hierarchy map with
duplicate-safe `indexedPath` values, `StructureMode=Search` to find objects by
path/name/component/tag/layer/prefab source and optional serialized fields, and
`StructureMode=Read` to drill into one object with transform, component,
renderer sorting, child, and bounded serialized-property data.

The workflow is intentionally read-only and does not open unloaded scenes
automatically. For scene assets, open/load the scene first, then use
`Action=Structure`; for raw YAML text, keep using `Action=Read`.
`UniBridge_BatchActions`, `Discover`, `ToolGuide`, and `DomainCatalog` expose
aliases such as `asset_structure`, `prefab_structure`,
`scene_asset_structure`, `structure_search`, `serialized_asset_search`, and
`read_yaml`.

`UniBridge_Discover Action=Tools` handles multi-token queries across tool
names, descriptions, and aliases, and `UniBridge_ToolGuide Action=Workflow`
prioritizes exact workflow keys before broader aliases.

The previous 0.2.19 release added cacheable loaded-type maps to `UniBridge_TypeSchema`.
Agents can call `Action=TypeFingerprint` before reusing a cached type map, then
call `Action=TypeIndex` to resolve component, ScriptableObject, AssetImporter,
asset, and shader type names without guessing namespaces or assemblies.
`TypeIndex` keeps the MCP response compact and can write a bounded full JSON
index under `Library/UniBridge/TypeIndex` with `WriteToFile=true`.
`UniBridge_BatchActions` validation and normalization also support
`TypeIndex` / `TypeFingerprint`, so agents can include type resolution inside
larger dry-run or execution workflows.

The previous 0.2.18 release added opt-in post-action diagnostics to `UniBridge_BatchActions`.
Agents can pass `IncludeConsoleDelta=true` to mark the Unity Console before a
batch and receive a compact diagnostic delta for logs emitted during that
batch. They can also pass `IncludeEditorEventDelta=true` to receive bounded
editor event deltas for hierarchy, project/assets, compilation, assembly
reload, play mode, package, and object-change events.

The previous 0.2.17 release added `UniBridge_RuntimeStateProbe Action=Assert`, a read-only
runtime assertion/watch workflow for Play Mode debugging. It lets AI agents
sample selected GameObject/component fields and properties, evaluate simple
pass/fail rules, and stop a batch workflow when a required runtime assumption is
false.

The previous 0.2.16 release added `UniBridge_RuntimeStateProbe`, a read-only
runtime component state probe for Play Mode debugging. It lets AI agents inspect
live GameObject/component fields and properties, list readable members, and
sample selected values over several editor ticks without executing arbitrary C#
in the project.

The previous 0.2.15 release added `UniBridge_RuntimeProfiler`, a read-only
runtime/profiler inspection tool for Play Mode debugging. It gives AI agents a
measured view of runtime state, loaded-scene object counts, Unity profiler
memory counters, and bounded `ProfilerRecorder` samples for frame time, GC
allocation, memory, rendering counters, physics/script markers, and spike
summaries.

The previous 0.2.14 release polished `UniBridge_ContextSnapshot` as the
first-call orientation tool for new AI agents. The snapshot includes an
`agentBrief` block by default: project shape, likely important folders/systems,
active WorkSession state, risk flags, guardrails, and recommended next
UniBridge calls.

The previous 0.2.13 release made `UniBridge_WorkSession` more useful as an
agent self-review tool after scene work. In addition to changed-file summaries,
WorkSession can capture a compact loaded-scene semantic baseline at `Begin` and
report what actually changed in the live Unity scene: created/deleted/moved/
renamed GameObjects, component changes, renderer sorting changes, prefab-info
changes, transform changes, and missing-script deltas.

The previous 0.2.12 release made `UniBridge_WorkSession` easier for agents to
use correctly: executing `UniBridge_BatchActions` appends active WorkSession
review data by default, and `UniBridge_ExecutionStatus` exposes the active
WorkSession summary alongside scheduler state.

The previous 0.2.11 release added `UniBridge_WorkSession`, a project-local
checkpoint and review layer for AI agents. It lets an agent begin a session
before broad Unity work, review changed files afterward, inspect compact text
diffs, dry-run selected reverts, and explicitly restore/delete selected files
from session snapshots stored under `Library/UniBridge/WorkSessions`.

Relay remains `1.1.0-build.15`; this release only adds Unity-side MCP tooling.

The previous 0.2.10 release makes `RefreshAssets WaitForCompletion=true`
reload-safe when Unity import/refresh closes the bridge during a domain reload.
It also includes the 0.2.9 discoverability and additive scene validation
improvements.

The previous 0.2.9 release improves Codex/tool_search discoverability and adds a read-only
additive scene registration validator for large Unity projects. It gives new
agents an obvious `UniBridge_Discover` ping/workflow entry point, makes the
relay `_server_info` metadata searchable by common UniBridge workflow names, and
adds `UniBridge_ValidateAdditiveSceneRegistration` for cloned/additive scene
setup checks without modifying the project.

UniBridge is a local Unity MCP bridge for AI-assisted game development. It lets
AI coding agents work with Unity projects through real Editor tools instead of
guessing from files alone.

## What Is Included

- Unity package: `com.cidonix.unibridge`
- Version: `0.2.23`
- Relay bundle version: `1.1.0-build.15`
- Unity compatibility: Unity Editor 6000.0 or newer
- Local test baseline: Unity 6000.4.10f1 on Windows
- Relay platforms:
  - Windows x64
  - Linux x64
  - macOS x64
  - macOS arm64

## Main Capabilities

- Project orientation: context snapshots, domain catalog, tool guide, workflow
  recipes, scene/object views, editor snapshots, and console diagnostics.
- Runtime debugging: profiler metrics plus read-only component state probes and
  assertion/watch rules for selected SerializedProperty paths and reflected
  fields/properties.
- Work-session safety: begin project-local checkpoints, review changed files,
  inspect compact text diffs, dry-run selected reverts, and restore/delete
  selected files from captured session snapshots.
- Scene and prefab editing: GameObject/component workflows, prefab inspection
  and overrides, scoped scene/prefab edits, batch actions, and rollback support.
- Large-scene hierarchy workflows: complete JSON/JSONL hierarchy exports,
  stable pagination, objectId/parentObjectId/siblingIndex data, export
  comparison, and safe objectId-based batch reparent/container edits with
  dry-run diffs, world-transform preservation, Undo grouping, and object-count
  validation.
- Assets: asset intelligence, import settings, materials, ScriptableObjects,
  generic allowlisted asset creation, dependency/reference graphs, preview
  captures, and read-only prefab/loaded-scene asset structure list/search/read.
- UI: Canvas/uGUI creation, templates, ScrollViews, layout helpers, graphics,
  button events, UI validation, repair plans, and safe auto-fixes.
- UI Toolkit: UXML/USS authoring, PanelSettings, UIDocument wiring, element and
  style edits, and UI Toolkit capture.
- Visual work: Scene View/game-camera captures, object/prefab/overview captures,
  contact sheets, visual audits, pixel stats, diff heatmaps, and animated/VFX
  preview advance.
- Runtime diagnostics: read-only runtime snapshots, loaded-scene object counts,
  memory counters, bounded profiler samples, frame-time/GC/rendering metrics,
  spike summaries, and full raw sample JSON output.
- Gameplay authoring: animation clips, Animator Controllers, Physics2D,
  Physics3D, rendering, navigation, tilemaps, input actions, timeline, audio,
  VFX, shaders, resources, and external model import.
- Script workflows: validation, safe edits, preview mode, source context,
  attached MonoBehaviour context, and script capability inspection.

## 0.2.15 Polish

- Added `UniBridge_RuntimeProfiler Action=Snapshot` for current editor/runtime
  state, loaded-scene summaries, object/component counts, missing-script counts,
  top MonoBehaviour type counts, and Unity profiler memory counters.
- Added `UniBridge_RuntimeProfiler Action=Metrics` to expose supported metric
  aliases such as `main_thread_ms`, `gc_alloc_bytes`, `batches_count`, and
  category/name syntax such as `Internal/Main Thread`.
- Added `UniBridge_RuntimeProfiler Action=Sample` for bounded profiler sampling
  with compact avg/p50/p95/max/last summaries and configurable spike detection.
- Full sample payloads are saved under `Library/UniBridge/RuntimeProfiler` when
  `SaveToFile=true`; raw samples are only returned inline when
  `ReturnSamples=true`.
- Added runtime/profiler discoverability to `UniBridge_Discover`,
  `UniBridge_ToolGuide`, `UniBridge_DomainCatalog`, and batch aliases.

## 0.2.14 Polish

- `UniBridge_ContextSnapshot` returns `agentBrief` by default. It is a compact
  onboarding layer for new agents, not a replacement for the raw `project`,
  `scenes`, `hierarchy`, `assets`, or `console` sections.
- `agentBrief.projectShape` summarizes asset counts, loaded-scene count,
  loaded root-object count, dirty-scene count, active scene, and scene scale.
- `agentBrief.likelyFolders` highlights probable scene/script/gameplay/UI/
  prefab/art/audio/data folders using bounded folder-name heuristics.
- `agentBrief.likelyImportantSystems` highlights obvious package, render
  pipeline, asset-folder, and asmdef signals such as Input System, UGUI,
  UI Toolkit, URP/HDRP, Timeline, TextMesh Pro, Corgi Engine, Spine, and
  project assemblies when present.
- `agentBrief.riskFlags` calls out compile/import/play-mode boundaries, dirty
  scenes, open Prefab Stage, console errors/warnings, hierarchy truncation,
  large loaded scenes, and missing active WorkSession state.
- `agentBrief.guardrails` and `agentBrief.recommendedNextCalls` tell the agent
  which UniBridge calls to make next before broad edits.
- Use `IncludeAgentBrief=false` when an agent wants the old raw snapshot shape
  without the onboarding layer.

## 0.2.13 Polish

- `UniBridge_WorkSession Action=Begin` captures a compact loaded-scene
  semantic baseline by default. Use `IncludeSceneSemantics=false` to disable it,
  or `MaxSemanticObjects` to bound very large scenes.
- `UniBridge_WorkSession Action=Status|Review` includes `semanticReview` when a
  semantic baseline exists. The summary reports live-scene semantic changes by
  stable object id, plus bounded per-object samples and per-scene counts.
- Active WorkSession review blocks appended by `UniBridge_BatchActions` and
  returned by `UniBridge_ExecutionStatus` now include the same semantic review
  data, so agents can notice scene-object changes even when no file has been
  saved yet.
- File revert behavior is unchanged: WorkSession still restores/deletes
  selected files from captured snapshots. Semantic review is a visibility layer
  for loaded scenes, not an automatic live-scene revert engine.

## 0.2.12 Polish

- `UniBridge_BatchActions` appends a `workSessionReview` block after executing
  batches when a WorkSession is active. This includes the session summary,
  changed-file counts, risk counts, bounded changed-file samples, warnings, and
  suggested follow-up calls.
- Added `IncludeWorkSessionReview` and `WorkSessionReviewMaxChanged` to
  `UniBridge_BatchActions`.
- `UniBridge_ExecutionStatus Action=Snapshot|Recent` includes active
  WorkSession review data by default.
- Added `IncludeWorkSession` and `WorkSessionMaxChanged` to
  `UniBridge_ExecutionStatus`.
- Removed an obsolete `Object.GetInstanceID()` call from
  `UniBridge_SceneHierarchyExport` on Unity 6.4+ so compile diagnostics stay
  clean during package smoke tests.

## 0.2.11 Polish

- Added `UniBridge_WorkSession` with actions:
  - `Begin`: capture a baseline snapshot under
    `Library/UniBridge/WorkSessions`;
  - `Status`: summarize active session and current change counts;
  - `Review`: list changed files, risk flags, and restore availability;
  - `Diff`: return compact text diffs for selected changed files;
  - `Revert`: dry-run by default, then restore/delete selected files only when
    `DryRun=false`;
  - `End`: close the active session.
- `UniBridge_Discover`, `UniBridge_ToolGuide`, and
  `UniBridge_DomainCatalog` now surface WorkSession as the recommended
  checkpoint/review layer before and after broad AI edits.

## 0.2.10 Polish

- Relay `1.1.0-build.15` can recover `RefreshAssets` connection loss caused by
  Unity import/domain reload, reconnect, wait for readiness, return compilation
  diagnostics, and surface structured `reloadBoundary`, `reconnectRequired`,
  `reloadSafe`, and `nextSuggestedCalls` data instead of a hard MCP
  `Unity connection closed` failure.

## 0.2.9 Polish

- Added `UniBridge_Discover`, a read-only status/discovery tool for new agents.
  It returns package/version data, core workflow calls, aliases, and optionally
  the enabled tool list.
- `UniBridge_Discover` and relay `_server_info` descriptions now include
  high-signal searchable aliases:
  `UniBridge`, `Unity`, `ValidateScript`, `RefreshAssets`,
  `RequestScriptCompilationNoWait`, `WaitForReadyAfterReload`,
  `GetCompilationDiagnostics`, `ReadConsole`, `DiagnosticSummary`,
  `ClearConsole`, `PlayMode`, `WaitForPlayMode`, `WaitForEditMode`, and
  `ValidateAdditiveSceneRegistration`.
- Added `UniBridge_ValidateAdditiveSceneRegistration` for read-only additive
  scene validation. It checks:
  - `.unity` file existence and scene GUID;
  - metadata `.asset` existence and GUID;
  - scene YAML references to metadata GUID;
  - `ProjectSettings/EditorBuildSettings.asset` scene registration;
  - `scenesManager.prefab` runtime entry;
  - `SceneBoundaries`, `SceneLoadingBoundaries`,
    `ScenePaddingBoundaries`, and `ScenePaddingWideScreenExpansion`;
  - stale supplied template/old scene names and GUIDs;
  - optional neighbor scene existence/metadata/boundary sanity.
- `UniBridge_BatchActions` can run both new tools as read-only steps with
  aliases such as `discover`, `validate_additive_scene`,
  `additive_scene_validation`, and `scene_registration`.
- `UniBridge_ToolGuide` and `UniBridge_DomainCatalog` now expose the compile
  diagnostics workflow, Play Mode boundary workflow, and additive scene
  validation workflow in agent-facing text.

## 0.2.8 Polish

- Added `UniBridge_ManageEditor Action=RequestPlayModeNoWait` for reload-safe
  Play Mode entry requests.
- Added `UniBridge_ManageEditor Action=WaitForPlayMode` and
  `UniBridge_ManageEditor Action=WaitForEditMode` for reconnect-friendly Play
  Mode verification.
- `Play WaitForCompletion=true` and `ExitPlayMode WaitForCompletion=true` now
  return controlled queued boundary responses instead of waiting inline through
  Unity domain reload.
- `UniBridge_BatchActions` now stops successfully at Play Mode reload
  boundaries and returns `postReconnect.nextSuggestedCalls`, so later steps do
  not run against stale pre-reload editor state.
- Relay `1.1.0-build.13` can recover Play Mode domain reload connection loss,
  reconnect, wait for the requested play/edit mode state, and return structured
  recovery data.
- `UniBridge_ToolGuide` and `UniBridge_DomainCatalog` now recommend split-phase
  Play Mode smoke tests:
  clear/prepare -> queue Play -> reconnect/wait -> diagnostics / console.

## 0.2.7 Polish

- Added `UniBridge_ManageEditor Action=RequestScriptCompilationNoWait` for
  reload-safe compile requests.
- Added `UniBridge_ManageEditor Action=WaitForReadyAfterReload`, which waits
  for editor readiness after a compile/reload checkpoint and returns
  compilation diagnostics.
- `RequestScriptCompilation WaitForCompletion=true` now returns a controlled
  queued response instead of waiting inline through Unity assembly reload.
- Relay `1.1.0-build.12` can recover script-compilation domain reload
  connection loss, reconnect, wait for readiness, and return structured
  diagnostics.
- `UniBridge_ToolGuide`, batch aliases, validation, and scheduler policy now
  recommend the stable workflow:
  validate scripts -> refresh assets -> `RequestScriptCompilationNoWait` ->
  `WaitForReadyAfterReload` -> diagnostics / console.

## 0.2.6 Polish

- `UniBridge_BatchActions` now allows `UniBridge_ValidateScript` as a
  read-only step.
- Added script-validation batch aliases:
  `validate_script`, `script_validate`, `validate_cs`, and `cs_validation`.
- Batch step normalization accepts `Uri`, `Path`, `AssetPath`, and
  `ScriptPath` forms for script validation.
- Batch impact reports script validation paths as read-only references and
  explains that rollback/Undo is not required for those steps.
- `UniBridge_ToolGuide` and documentation now describe the batch-safe
  validation workflow while keeping script text edits on their dedicated
  SHA/precondition tools.

## 0.2.5 Polish

- `UniBridge_ManageGameObject Find` / `SearchMethod=ByComponent` now scans
  live components in scenes and Prefab Stage by short type name, full type
  name, assembly-qualified name, MonoScript name/path/GUID, and serialized
  `m_EditorClassIdentifier` aliases. This makes Prefab Mode lookup much more
  reliable after scripts move into namespaces.
- `UniBridge_ManageGameObject GetComponents` and
  `UniBridge_TypeSchema InspectGameObject` now expose script identity metadata
  and namespace-migration diagnostics when an old serialized class id resolves
  to a new runtime type.
- `UniBridge_TypeSchema InspectGameObject` now supports `IncludeInactive` for
  inactive scene and Prefab Stage objects.
- Adding a uGUI `Graphic` component now returns a clear hint when another
  `Graphic` is already present, for example removing `TextMeshProUGUI` before
  adding `Image`.
- `UniBridge_ContextSnapshot IncludeConsole` defaults to compact console
  summaries with totals and grouped critical/warning/spam issues, avoiding long
  timeline dumps unless `ConsoleSummaryMode=Detailed` is requested.
- `UniBridge_ReadConsole` now has clearer aliases:
  `CreateMarker`, `ReadSinceMarker`, and `ClearConsole`.
- `UniBridge_ManagePrefab save_stage` and `close_stage` responses now include
  before/current Prefab Stage state and explain when Unity has already closed
  or reloaded a stage.

## 0.2.4 Polish

- `UniBridge_ManageGameObject` now uses the shared scene-object resolver for
  `Modify`, `Delete`, `AddComponent`, `RemoveComponent`,
  `SetComponentProperty`, component inspection, parent lookup, and sibling
  lookup. Passing `IncludeInactive=true` or `SearchInactive=true` now finds
  inactive scene objects consistently with `UnitySearch` and `SceneObjectView`.
- `UniBridge_BatchActions` impact and rollback diagnostics now normalize
  project-relative `/Assets`, `/Packages`, `/ProjectSettings`, and `project:/`
  path forms before checking file existence from the Unity project root.
- `UniBridge_ManageScene` and batch scene-step validation now accept full scene
  asset paths in `Path`, including `/Assets/...` and `project:/Assets/...`
  forms, so scene diagnostics and execution paths agree.
- Executing batches now propagate `DryRun=false` to nested dry-run-aware tools,
  and step reports include an execution warning if a nested tool still returns
  `dryRun=true`.
- Batch validation accepts `UniBridge_ManageEditor`
  `GetCompilationDiagnostics`, matching the editor lifecycle tool surface.
- The relay now cleans stale Unity discovery JSON files for dead editor PIDs
  during connection discovery.

## 0.2.3 Polish

- `CompareExports` now keeps `left` and `right` count-only by default:
  `totalObjects`, `duplicateGroupCount`, and `duplicateObjectCount`. Verbose
  duplicate keys are returned only when `IncludeDuplicateKeys=true`.
- `summary.duplicates` now uses flattened counters
  `leftGroupCount/rightGroupCount` and `leftObjectCount/rightObjectCount`.
  Matching duplicate samples are returned once as `sharedTopGroups`; otherwise
  the response uses bounded `leftTopGroups` and `rightTopGroups`.
- Added `IncludeDuplicateSummary`, default `true`. Set it to `false` for
  count-only compare output without top duplicate group examples.
- `ExecutionStatus` operation records now include `mode` and `changedProject`.
  Dry-run operations report `mode=DryRun` and `changedProject=false`.
- `ContextSnapshot Depth=Brief` now avoids expanding registered package roots
  by default. It still returns `registeredPackageCount`; the full
  `registeredPackages` list is returned for `Depth=Detailed` or when
  `IncludePackageDependencies=true`.

## 0.2.2 Polish

- `CompareExports` no longer repeats the same duplicate group detail in
  `left`, `right`, and `summary`. Full compact duplicate detail lives under
  `summary.duplicates.left/right`; `left/right` now expose only object totals,
  duplicate group/object counts, and optional `duplicateKeys`.
- `MaxDuplicateSamples` limits sample object ids, indexed paths, and names per
  duplicate path group. The default is `3`, which keeps large UI clone
  hierarchies readable for agents.
- `ManageSceneHierarchy Reparent` dry-runs now populate
  `plannedParentObjectId`, `plannedParentPath`, and
  `plannedParentWillBeCreated=false` when the destination parent already
  exists.
- Dry-run and executed hierarchy operations now include compact
  `objectCountValidation` aliases: `mode`, `expectedDelta`, `actualDelta`,
  `plannedDelta` where applicable, and `passed`.

## 0.2.1 Polish

- `UniBridge_SceneHierarchyExport` now returns a compact `summary` in every
  export response, even when the full scene export is written to JSON or JSONL.
  The summary includes object totals, root/inactive counts, missing scripts,
  renderer breakdowns, `Light2D` count, prefab instance count, and top duplicate
  hierarchy path groups.
- `CompareExports` is quieter by default. Duplicate key rows are opt-in through
  `IncludeDuplicateKeys`, bounded by `MaxDuplicateKeys`, while the summary keeps
  duplicate group/object counts and top duplicate groups visible. When disabled,
  verbose duplicate key rows are omitted from the response.
- Export comparison prefers `indexedPath` when available, which makes sibling
  duplicates such as repeated UI clones safer to compare.
- `ManageSceneHierarchy CreateContainer` dry-runs now explain the planned
  destination container on every planned move before that container exists.
- Object-count validation results now expose the validation mode, expected
  delta, actual delta, and pass/fail flag explicitly for both `Reparent` and
  `CreateContainer`.
- Package metadata now identifies the author as Cidonix in Unity Package
  Manager.

## Installation

1. Extract the release archive.
2. In Unity, open `Window > Package Manager`.
3. Choose `+ > Add package from disk...`.
4. Select `com.cidonix.unibridge/package.json`.
5. Wait for Unity to compile.
6. Open `Project Settings > UniBridge > MCP`.
7. Confirm that the Local Bridge is running.
8. Use the Integrations section to configure your MCP client for this project.
9. Restart the AI agent/MCP client itself so it reloads the new MCP server
   configuration.

For day-to-day work, use the generated MCP configuration that includes
`--project-id`. This keeps each Unity project targeted explicitly.

UniBridge supports multiple open Unity projects at the same time. Configure one
MCP server entry per Unity project, each with its own generated server key and
`--project-id`. When you add another project to the agent configuration,
restart the agent/MCP client, not just Unity.

## Known Limitations

- Windows is the primary live-tested platform in this release.
- Linux and macOS relay binaries are included, but should be verified on target
  systems before relying on them for production work.
- Relay binaries are unsigned. Windows, macOS, or Linux security policy may ask
  for additional confirmation before running them.
- Some tools depend on optional Unity packages. When those packages are absent,
  UniBridge reports the missing capability instead of treating the whole bridge
  as broken.
- UniBridge is an Editor package. It is not intended to be included in runtime
  player builds.

## Verification Summary

The 0.2.3 package was smoke-tested through the MCP relay against the UniBridge
test Unity project.

- `tools/list` reported 62 callable MCP tools.
- `RefreshAssets`, `WaitIdle`, and `GetCompilationDiagnostics` completed with
  0 compile warnings and 0 compile errors.
- `ContextSnapshot Depth=Brief` returned `registeredPackageCount` without
  expanding `registeredPackages`; passing `IncludePackageDependencies=true`
  returned the full registered package roots.
- `SceneHierarchyExport` returned compact summary fields, wrote a JSONL export,
  and bounded duplicate samples with `MaxDuplicateSamples=3`.
- `ManageSceneHierarchy Reparent` dry-run against an existing parent returned
  the correct planned parent metadata, and `ExecutionStatus Recent` reported
  that operation as `policy=Mutating`, `mode=DryRun`,
  `changedProject=false`.
- `CompareExports IncludeDuplicateKeys=false IncludeDuplicateSummary=true`
  returned `left/right` payloads with only totals and duplicate counts. The
  duplicate summary was flattened into group/object counters and
  `sharedTopGroups`.
- `CompareExports IncludeDuplicateSummary=false` returned count-only duplicate
  summary fields without top duplicate groups.
- `CompareExports IncludeDuplicateKeys=true` returned bounded duplicate keys.
- The test scene was restored through `UniBridge_EditorSnapshot` after the
  smoke run.

## Support

For setup issues, include:

- Unity version
- Operating system
- UniBridge version
- MCP client name
- The relevant Unity Console error or UniBridge diagnostic summary
