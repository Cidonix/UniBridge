# Changelog

All notable UniBridge package changes will be documented in this file.

## 0.2.16

### Added

- Added `UniBridge_RuntimeStateProbe`, a read-only runtime component state
  probe for AI gameplay debugging. It supports `Snapshot`, `Sample`, and
  `ListMembers` actions for live GameObject/component state without arbitrary
  C# execution.
- `Action=Sample` reads selected SerializedProperty paths and reflected
  fields/properties over bounded editor ticks, returns a compact changed-member
  summary, and can save full raw samples under
  `Library/UniBridge/RuntimeStateProbe`.

### Changed

- `UniBridge_Discover`, `UniBridge_ToolGuide`, `UniBridge_DomainCatalog`, and
  `UniBridge_BatchActions` now expose runtime state probe workflows and aliases
  such as `runtime_probe`, `runtime_state`, `state_probe`, `watch_variables`,
  `component_state`, and `runtime_fields`.
- `UniBridge_BatchActions` now normalizes friendly parameter aliases for
  `UniBridge_RuntimeProfiler` and `UniBridge_RuntimeStateProbe`.

## 0.2.15

### Added

- Added `UniBridge_RuntimeProfiler`, a read-only runtime/profiler inspection
  tool for Play Mode debugging. It reports editor/runtime state, loaded-scene
  object counts, Unity profiler memory counters, supported metric aliases, and
  bounded `ProfilerRecorder` samples with avg/p50/p95/max/last and spike
  summaries.
- `Action=Sample` can save full raw samples to
  `Library/UniBridge/RuntimeProfiler` while keeping the MCP response compact.

### Changed

- Added runtime/profiler aliases and workflow entries to `UniBridge_Discover`,
  `UniBridge_ToolGuide`, `UniBridge_DomainCatalog`, and
  `UniBridge_BatchActions`.

## 0.2.14

### Changed

- `UniBridge_ContextSnapshot` now includes an `agentBrief` onboarding block by
  default. It summarizes project shape, likely important folders/systems, active
  WorkSession state, risk flags, guardrails, and recommended next UniBridge
  calls for new agents.
- Added `IncludeAgentBrief` to `UniBridge_ContextSnapshot` so agents can disable
  the onboarding brief when they need a minimal raw snapshot.

## 0.2.13

### Changed

- `UniBridge_WorkSession Action=Begin` now captures an optional compact
  loaded-scene semantic baseline under `Library/UniBridge/WorkSessions`.
- `UniBridge_WorkSession Action=Status|Review` now includes `semanticReview`
  data when the session has a scene semantic baseline.
- Active WorkSession auto-review blocks appended by `UniBridge_BatchActions`
  and returned by `UniBridge_ExecutionStatus` now include semantic scene
  changes in addition to changed-file summaries.

### Added

- Added `IncludeSceneSemantics`, `MaxSemanticObjects`,
  `IncludeSemanticReview`, and `MaxSemanticChanges` controls to
  `UniBridge_WorkSession`.
- Semantic review reports created/deleted/moved/renamed GameObjects,
  component changes, renderer sorting changes, renderer material/enablement
  changes, prefab-info changes, transform changes, and missing-script deltas
  by stable scene object id.

## 0.2.12

### Changed

- `UniBridge_BatchActions` now appends active `UniBridge_WorkSession` review
  data after executing batches by default. If a session is active, agents get a
  compact changed-file summary, risk counts, and sample changed files directly
  in the batch response.
- Added `IncludeWorkSessionReview` and `WorkSessionReviewMaxChanged` to
  `UniBridge_BatchActions` so agents can opt in for dry-runs or tune response
  size.
- `UniBridge_ExecutionStatus Action=Snapshot|Recent` now includes active
  WorkSession review data by default, with `IncludeWorkSession` and
  `WorkSessionMaxChanged` controls.

### Fixed

- Removed an obsolete `Object.GetInstanceID()` call from
  `UniBridge_SceneHierarchyExport` on Unity 6.4+ so compile diagnostics stay
  clean during smoke tests.

## 0.2.11

### Added

- Added `UniBridge_WorkSession`, a project-local AI work-session safety tool.
  Agents can start a checkpoint, review changed files, inspect compact text
  diffs, dry-run selected reverts, and explicitly restore/delete selected files
  from snapshots stored under `Library/UniBridge/WorkSessions`.
- Added WorkSession guidance to `UniBridge_Discover`, `UniBridge_ToolGuide`,
  and `UniBridge_DomainCatalog` so new agents can discover the checkpoint /
  review / diff / revert workflow.

### Changed

- `UniBridge_WorkSession` uses read-only scheduling for `Status`, `Review`,
  `Diff`, and `List`, while `Begin`, `Revert`, and `End` run through the
  exclusive editor execution gate.

## 0.2.10

### Changed

- Bundled relay `1.1.0-build.15` now treats `RefreshAssets` connection loss
  as an expected Unity import/domain-reload boundary. If
  `AssetDatabase.Refresh` closes the bridge, the relay reconnects, waits for
  editor readiness, returns compilation diagnostics, and reports
  `reloadBoundary`, `reconnectRequired`, `reloadSafe`, and
  `nextSuggestedCalls` instead of surfacing a transport-level
  `Unity connection closed` MCP error.

## 0.2.9

### Added

- Added `UniBridge_Discover`, a stable read-only ping/discovery entry point for
  Codex and other MCP clients. Its tool description includes searchable aliases
  for `UniBridge`, `Unity`, `ValidateScript`, `RefreshAssets`,
  `RequestScriptCompilationNoWait`, `WaitForReadyAfterReload`,
  `GetCompilationDiagnostics`, `ReadConsole`, `DiagnosticSummary`,
  `ClearConsole`, `PlayMode`, `WaitForPlayMode`, `WaitForEditMode`, and
  `ValidateAdditiveSceneRegistration`.
- Added `UniBridge_ValidateAdditiveSceneRegistration`, a read-only additive
  scene setup validator. It checks scene/meta GUIDs, metadata asset/meta GUIDs,
  scene-to-metadata references, EditorBuildSettings, scenesManager prefab
  registration, `SceneBoundaries`, `SceneLoadingBoundaries`,
  `ScenePaddingBoundaries`, `ScenePaddingWideScreenExpansion`, stale
  template/old references, and optional neighbor scene sanity.
- Bundled relay `1.1.0-build.14` now gives `_server_info` alias-rich
  discoverability metadata for new Codex sessions before agents know the full
  UniBridge tool list.

### Changed

- Updated bundled Roslyn compiler assemblies
  `Microsoft.CodeAnalysis.dll` and `Microsoft.CodeAnalysis.CSharp.dll` from
  3.11.x to 5.3.0 for newer C# syntax parsing and diagnostics coverage.
- `UniBridge_BatchActions` now allows `UniBridge_Discover` and
  `UniBridge_ValidateAdditiveSceneRegistration` as read-only steps.
- Added batch aliases for additive scene validation:
  `validate_additive_scene`, `additive_scene_validation`,
  `additive_scene_registration`, `scene_registration`, and
  `scene_metadata_validation`.
- `UniBridge_ToolGuide` and `UniBridge_DomainCatalog` now explicitly document
  the compile diagnostics workflow, Play Mode boundary workflow, and additive
  scene registration validation workflow for new agents.

### Fixed

- `UniBridge_ValidateScript` no longer reports the generic
  `String concatenation in Update()` warning just because a file contains both
  an `Update()` method and string/plus tokens elsewhere. The Unity best-practice
  check now scopes string allocation warnings to concrete expressions inside
  `Update()` and includes line/column plus the expression snippet.
- Scoped `ValidateAdditiveSceneRegistration` stale-reference scans to the
  target scene, target metadata, and target `scenesManager` entry. The validator
  no longer reports legal sibling scene entries in the global
  `scenesManager.prefab` as stale `TemplateSceneName` references.

## 0.2.8

### Added

- Added reload-safe Play Mode lifecycle actions:
  `UniBridge_ManageEditor Action=RequestPlayModeNoWait`,
  `UniBridge_ManageEditor Action=WaitForPlayMode`, and
  `UniBridge_ManageEditor Action=WaitForEditMode`.
- Bundled relay `1.1.0-build.13` can recover Play Mode domain reload
  connection loss and return a structured recovered result instead of
  surfacing `Unity not detected` / `Unity connection closed` as a transport
  error.

### Changed

- `Play` and `ExitPlayMode` now queue reload-safe boundary requests instead of
  waiting inline through a domain reload that can recreate the bridge.
- `UniBridge_BatchActions` stops successfully at Play Mode reload boundaries
  and returns `postReconnect.nextSuggestedCalls`, so agents do not continue
  later batch steps against stale pre-reload editor state.
- `ToolGuide` and `DomainCatalog` now describe split-phase Play Mode smoke
  tests: clear/prepare, queue Play, reconnect/wait, then read console.

## 0.2.7

### Added

- Added reload-safe script compilation workflow actions:
  `UniBridge_ManageEditor Action=RequestScriptCompilationNoWait` and
  `UniBridge_ManageEditor Action=WaitForReadyAfterReload`.
- Bundled relay `1.1.0-build.12` can recover script-compilation domain reload
  connection loss and return a structured recovered result with compilation
  diagnostics instead of surfacing `Unity connection closed` as a transport
  error.

### Changed

- `RequestScriptCompilation` with `WaitForCompletion=true` now returns a
  controlled queued compile response. Inline waiting is deferred because Unity
  assembly reload can recreate the MCP bridge before the old call can return.
- `UniBridge_ToolGuide`, `UniBridge_BatchActions` aliases, and scheduler
  policy now guide agents toward:
  validate scripts -> refresh assets -> `RequestScriptCompilationNoWait` ->
  `WaitForReadyAfterReload` -> compilation diagnostics / console.

## 0.2.6

### Added

- Added `UniBridge_ValidateScript` to `UniBridge_BatchActions` as a
  read-only step. Batch workflows can now validate multiple `.cs` files with
  `IncludeDiagnostics=true` before refreshing assets, requesting compilation,
  waiting for the editor, and reading console diagnostics.
- Added batch aliases for script validation:
  `validate_script`, `script_validate`, `validate_cs`, and `cs_validation`.

### Changed

- `UniBridge_BatchActions` now normalizes `Uri`/`Path`/`AssetPath` aliases for
  script validation and reports those script paths as read-only impact
  references rather than mutating touches.
- `UniBridge_ToolGuide` and package documentation now explicitly distinguish
  batch-safe script validation from dedicated SHA/precondition script editing
  tools.

## 0.2.5

### Changed

- Improved `SceneObjectLocator` `ByComponent` matching for scene and Prefab
  Stage objects. It now scans live components by short type name, full type
  name, assembly-qualified name, MonoScript name/path/GUID, and serialized
  `m_EditorClassIdentifier` aliases instead of failing when a type name cannot
  be resolved directly.
- `UniBridge_TypeSchema InspectGameObject` now supports `IncludeInactive` and
  returns component script identity plus namespace-migration diagnostics, such
  as old serialized class ids resolving to new namespaced runtime types.
- `UniBridge_ManageGameObject AddComponent` now returns an actionable UI
  Graphic conflict hint when a target already has a `Graphic` component such as
  `TextMeshProUGUI` before adding `Image`.
- `UniBridge_ContextSnapshot IncludeConsole` now defaults to
  `ConsoleSummaryMode=Compact`, keeping totals and grouped issues while
  omitting long recent-sample/timeline dumps unless detailed mode is requested.
- `UniBridge_ManagePrefab save_stage` and `close_stage` responses now include
  before/current Prefab Stage state and explain when Unity has already closed or
  reloaded the stage.

### Added

- Added `UniBridge_ReadConsole` action aliases `CreateMarker`,
  `ReadSinceMarker`, and `ClearConsole` for clearer post-change console checks.

## 0.2.4

### Changed

- Fixed `UniBridge_ManageGameObject` inactive scene-object lookup by routing
  modify, delete, component, parent, and sibling resolution through the shared
  `SceneObjectLocator` with `IncludeInactive/SearchInactive` support.
- Improved `UniBridge_BatchActions` impact/rollback asset-path normalization so
  project-relative `/Assets`, `/Packages`, `/ProjectSettings`, and `project:/`
  URI forms resolve from the Unity project root before existence checks.
- `UniBridge_ManageScene` and batch scene-step validation now accept full scene
  asset paths in `Path`, including `/Assets/...` and `project:/Assets/...`
  forms, instead of treating them as folders.
- Executing batches now propagate `DryRun=false` to nested dry-run-aware tools
  and warn if a nested tool still reports `dryRun=true`.
- Batch validation now accepts `UniBridge_ManageEditor`
  `GetCompilationDiagnostics`, keeping batch validation aligned with the
  editor lifecycle tool surface.
- Relay discovery now removes stale bridge connection JSON files for dead Unity
  editor PIDs after reporting them.

### Added

- Relay bundle `1.1.0-build.10`.

## 0.2.3

### Changed

- Further reduced `CompareExports` duplicate noise. `left` and `right` now
  carry only object totals and duplicate counts unless `IncludeDuplicateKeys`
  is explicitly enabled.
- Flattened `summary.duplicates` into left/right group and object counters plus
  bounded `sharedTopGroups`, or separate `leftTopGroups` / `rightTopGroups`
  when the duplicate samples differ.
- Added `IncludeDuplicateSummary`, default `true`, so agents can request
  count-only compare responses without top duplicate groups.
- Added `mode` and `changedProject` to `ExecutionStatus` operation records.
  Dry-run calls now show `mode=DryRun` and `changedProject=false` while keeping
  their scheduler policy annotations intact.
- Made `ContextSnapshot Depth=Brief` avoid expanding `registeredPackages` by
  default. It now returns `registeredPackageCount`; full registered package
  roots are returned only for `Depth=Detailed` or
  `IncludePackageDependencies=true`.

## 0.2.2

### Changed

- Reduced `CompareExports` duplicate-summary duplication. Full duplicate group
  detail now lives under `summary.duplicates.left/right`; `left/right` expose
  only `totalObjects`, `duplicateGroupCount`, `duplicateObjectCount`, and
  `duplicateKeys`.
- Added `MaxDuplicateSamples`, default `3`, to limit sample object ids,
  indexed paths, and names inside each duplicate path group.
- Improved `ManageSceneHierarchy Reparent` dry-runs so existing destination
  parents now populate `plannedParentObjectId`, `plannedParentPath`, and
  `plannedParentWillBeCreated=false`.
- Added compact `objectCountValidation` aliases for dry-run and executed
  hierarchy operations: `mode`, `expectedDelta`, `actualDelta`, `plannedDelta`
  where applicable, and `passed`.

## 0.2.1

### Changed

- Added compact `summary` payloads to `UniBridge_SceneHierarchyExport` export
  responses and output files, including scene totals, root/inactive counts,
  missing scripts, renderer counts by type and sorting layer, `Light2D` count,
  prefab instance count, and top duplicate hierarchy path groups.
- Made `CompareExports` less noisy by default: duplicate path/key detail is now
  summarized, verbose `duplicateKeys` rows are opt-in through
  `IncludeDuplicateKeys`, and `MaxDuplicateKeys` bounds the returned list. When
  disabled, verbose `duplicateKeys` rows are omitted from the response.
- Updated export comparison matching to prefer `indexedPath` when available so
  sibling duplicates are handled more reliably than plain hierarchy path keys.
- Clarified `CreateContainer` dry-runs by including
  `plannedParentContainerName`, `plannedParentPath`,
  `plannedParentObjectId=null`, and `plannedParentWillBeCreated=true` on each
  planned move.
- Added explicit object-count validation result fields:
  `validationMode`, `expectedObjectCountDelta`, `actualObjectCountDelta`, and
  `objectCountValidationPassed`.
- Added package author metadata so Unity Package Manager identifies the
  package as authored by Cidonix instead of showing an unknown author.

## 0.2.0

### Added

- Added `UniBridge_SceneHierarchyExport` for complete large-scene hierarchy
  exports with stable depth-first pagination, `objectId`/`parentObjectId`,
  `siblingIndex`, active/tag/layer state, local/world transforms, component
  summaries, missing-script counts, renderer sorting/material data, prefab
  source info, and URP `Light2D` sorting-layer masks.
- Added JSON/JSONL full export output under
  `Library/UniBridge/SceneHierarchyExports` so large scenes do not have to fit
  inside a single MCP response.
- Added export comparison for prototype/main scene audits, including hierarchy,
  prefab source, renderer sorting/material, and `Light2D` mask differences.
- Added `UniBridge_ManageSceneHierarchy` for objectId-based batch reparenting
  and organizational containers with dry-run diffs, world-transform
  preservation, a single Unity Undo group, and object-count validation.
- Added large-scene guidance to `UniBridge_ToolGuide`,
  `UniBridge_DomainCatalog`, batch aliases, and package documentation.

## 0.1.0

Initial Patreon release.

### Added

- Added the local Unity Editor bridge, per-project discovery, local relay
  installation, MCP client configuration helpers, and connection approval flow.
- Added bundled relay binaries for Windows x64, Linux x64, macOS x64, and
  macOS arm64.
- Added agent orientation tools: `UniBridge_ToolGuide`,
  `UniBridge_DomainCatalog`, `UniBridge_ContextSnapshot`,
  `UniBridge_EditorSnapshot`, `UniBridge_SceneObjectView`, and
  `UniBridge_WorkflowRecipes`.
- Added structured editor lifecycle operations through `UniBridge_ManageEditor`,
  including save, refresh, wait idle, request compilation, generate solution
  files, play-mode state, and reload checkpoints.
- Added asset workflows through `UniBridge_AssetIntelligence`,
  `UniBridge_ManageAsset`, `UniBridge_ManageAssetImporter`,
  `UniBridge_CaptureAsset`, material tooling, ScriptableObject tooling, generic
  allowlisted asset authoring, preview grids, import presets, reference
  graphs, impact checks, and fuzzy missing-path recovery.
- Added script workflows for validation, safe edits, source inspection,
  behaviour context, script capability checks, and preview-safe text edit
  operations.
- Added scene and prefab workflows through GameObject, prefab, scoped-edit,
  batch-action, transaction rollback, component schema, serialized-property,
  constraints, and UnityEvent tools.
- Added UI workflows for Canvas/uGUI creation, high-level templates, ScrollView
  authoring, graphics, selectable transitions, Button event wiring, layout
  groups, validation, repair plans, and safe auto-fixes.
- Added UI Toolkit workflows for UXML, USS, PanelSettings, UIDocument wiring,
  element/style edits, tree capture, layout metadata, and visibility hints.
- Added capture and visual verification workflows for Scene View, game-camera,
  selection/object/prefab/overview/around-object captures, contact sheets,
  visual audits, pixel stats, diff heatmaps, and animated/VFX preview advance.
- Added animation tooling for AnimationClip creation, sampled curve editing,
  Animator Controller graph authoring, nested state machines, BlendTrees,
  transitions, and controller validation.
- Added gameplay-domain tools for Physics2D, Physics3D, rendering, navigation,
  tilemaps, input actions, timeline, audio, VFX, shaders, resources, external
  model import, and menu execution.
- Added Unity search and type/schema tooling, including deterministic UniBridge
  search, optional native/hybrid UnityEditor.Search backends, serialized schema
  inspection, shader property metadata, AnimationCurve support, and Gradient
  support.
- Added diagnostics and safety support: console diagnostic summaries, debug log
  filtering, dry-run support on mutating workflows, scoped editor-state restore,
  dirty-scene checks, and batch rollback using Undo plus bounded asset
  snapshots.

### Tested

- Full MCP smoke test passed on the Windows test project with Unity 6000.4.7f1.
- All 60 registered MCP tools were exercised in live Unity through the MCP
  relay.
- Final Unity diagnostic summary reported 0 warnings, 0 errors, and
  0 exceptions after cleanup.

### Known Limitations

- Windows is the primary verified platform for this release.
- Linux and macOS relay binaries are bundled and cross-built, but should be
  validated on target systems.
- Relay binaries are unsigned and may trigger operating-system security prompts.
