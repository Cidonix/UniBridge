# Changelog

All notable UniBridge package changes will be documented in this file.

## 0.2.48

### Fixed

- `UniBridge_RuntimeStateProbe Action=Sample|Assert` now captures once per
  `EditorApplication.update` through one persistent callback. It no longer
  chains a delay/main-thread continuation for every requested sample.
- Sampling is independent of gameplay time and continues when
  `Time.timeScale == 0`.
- The tool reserves headroom before its scheduler timeout. If the requested
  editor ticks still do not fit, it returns partial sample count,
  `completionReason`, `waitReason`, and sampling-clock diagnostics instead of
  being cut off by a transport-level timeout.
- Completion, cancellation, exceptions, and internal timeout all unsubscribe
  the editor callback, allowing the scheduler read slot to be released.
- MCP regression creates a Play Mode component that pauses gameplay time,
  captures `180/180` samples within `30000ms`, and verifies
  `activeReaders == 0` afterward.

## 0.2.47

### Fixed

- `UniBridge_ManageGameObject Action=Create` now applies top-level
  `ComponentProperties` after every requested component has been added. A
  string entry in `ComponentsToAdd` no longer causes the accompanying property
  map to be silently ignored.
- Component type lookup accepts short and fully-qualified names consistently.
  Private `[SerializeField]` values are patched through `SerializedObject`, so
  changes such as a serialized `bool` from `false` to `true` work in both Edit
  Mode and Play Mode.
- Property mutations perform post-write readback and return structured
  `applied` / `skipped` evidence with requested and actual values. Unknown
  components, missing fields, invalid values, and readback mismatches now fail
  explicitly instead of returning misleading success.
- MCP smoke regression creates a temporary component and verifies short/FQN
  lookup, private serialized bool and float writes, independent runtime
  readback, negative diagnostics, and Edit/Play Mode parity.

## 0.2.46

### Fixed

- Mixed `UniBridge_ScriptApplyEdits` calls now execute every normalized text
  and structured operation sequentially against one in-memory source copy.
  Preview diffs include the complete ordered edit batch instead of showing
  only anchor operations and listing method edits as merely planned.
- Mixed Preview and Apply now share the same edit pipeline. Preview returns
  `currentSha256`, `predictedSha256`, `editsApplied=0`, and
  `scheduledRefresh=false`; applying the same batch produces the predicted
  SHA and writes the file once after final C# validation.
- The combined pipeline supports structured operations together with anchor,
  prepend/append, range, and regex text edits while preserving strict missing
  and ambiguous anchor diagnostics.
- MCP regression now covers both `anchor_insert + replace_method` orders, a
  three-operation `anchor_insert + replace_method + insert_method` batch,
  multiple method edits in one class, stale SHA rejection, Preview no-write,
  Preview/Apply SHA parity, and a following method that starts at column zero.

## 0.2.45

### Fixed

- `UniBridge_ScriptApplyEdits` now aligns inserted and deleted lines before
  rendering structured Preview diffs. Expanding a one-line method into a
  multi-line body no longer makes every following method appear removed and
  re-added merely because its line number shifted.
- Unified diff output is grouped into compact hunks with three context lines
  and a bounded response size. Small and medium scripts use exact line-sequence
  alignment; large scripts use a bounded look-ahead fallback instead of an
  unbounded LCS allocation.
- Method-span behavior is regression-tested when the following method uses
  normal indentation and when it begins at column zero. Both cases preserve
  the following method and produce a scoped Preview diff.
- MCP regression verifies strict Preview no-write/no-refresh behavior, current
  and predicted SHA evidence, real apply on a temporary script, stale SHA
  rejection, and a mixed `replace_method + anchor_insert + insert_method`
  Preview.

## 0.2.44

### Fixed

- `UniBridge_VersionControl` now accepts either `AssetPath` or a non-empty
  `AssetPaths` array for `InspectAsset`, `InspectAssets`, `EnsureEditable`, and
  `Checkout`. Multi-file calls no longer fail with `AssetPath is required`.
- Version-control operations return a structured `assets` entry for every
  requested path plus aggregate existing/missing, editable/blocked, and
  checkout counts.
- Empty arrays, non-string entries, and missing path arguments return explicit
  validation errors. Partially invalid path sets retain both valid and missing
  per-asset results, and `ThrowOnBlocked=true` returns the complete diagnostic
  payload with a failed response.
- Version-control path normalization now uses the shared
  `ProjectPathResolver`, including package, project-relative, file URI, and
  absolute paths.
- Bundled relay `1.1.0-build.17` explicitly uses UTF-8 for MCP stdio. This
  prevents Unicode tool arguments from depending on the host Windows console
  code page.
- The MCP smoke suite now covers the `AssetPaths` schema, single and multiple
  asset calls, `Checkout`, both inspection action aliases, empty arrays, and
  partially invalid sets. The smoke runner also forces UTF-8 diagnostics.

## 0.2.43

### Fixed

- `UniBridge_ScriptApplyEdits Preview=true` is now strictly no-write for
  structured operations (`replace_method`, `insert_method`, `delete_method`,
  `replace_class`, and `delete_class`) instead of forwarding to a structured
  writer that ignored the preview option.
- Structured Preview returns a proposed diff, `currentSha256`,
  `predictedSha256`, `editsPreviewed`, `editsApplied=0`,
  `noChangesApplied=true`, and `scheduledRefresh=false`.
- Structured Preview and actual apply both enforce `PreconditionSha256` before
  processing, returning `stale_file` without changing the script.
- Explicit structured-edit refresh modes `none`, `manual`, and `disabled` no
  longer schedule a delayed AssetDatabase refresh.
- MCP regression now previews three `replace_method` operations in one call,
  verifies unchanged UTF-8 content and SHA, checks the response/diff/predicted
  SHA, applies the same edits separately, and retains the full anchor Preview
  coverage from `0.2.42`.

## 0.2.42

### Fixed

- `UniBridge_ScriptApplyEdits` now implements `anchor_insert`,
  `anchor_delete`, and `anchor_replace` consistently in both text-only and
  mixed text/structured routes. Mixed calls no longer reject the documented
  delete/replace operations as unsupported.
- Mixed `Preview=true` is now strictly no-write. It returns the computed text
  diff and planned structured edits without applying the text portion first.
- `PreconditionSha256` is enforced for text, mixed, and structured edit routes;
  stale calls return `stale_file` with current/expected SHA evidence.
- Missing anchors fail by default, and ambiguous regex anchors return an
  actionable error. Callers can disambiguate with zero-based `matchIndex` or
  an explicit `preferLast=true/false`, or opt into missing-anchor no-op behavior
  with `allowNoop=true`.
- Invalid and excessively expensive anchor regex patterns return controlled
  validation errors instead of silently selecting an arbitrary span.
- The standard MCP smoke suite now creates a temporary C# script and verifies
  Preview plus actual apply for all three anchor operations, mixed Preview
  no-write behavior, C# validation, SHA protection, missing/ambiguous errors,
  and cleanup.

## 0.2.41

### Fixed

- `UniBridge_ManageUI Action=SetGraphic` now applies the requested
  `Text`, `FontSize`, `FontAssetPath`, `Alignment`, `RichText`, `BestFit`,
  font-size bounds, and `OverflowMode` to `TextMeshProUGUI` instead of
  silently updating only its base `Graphic` state.
- TMP values are assigned through the loaded public API and synchronized with
  the serialized backing fields used by prefab YAML. Unicode strings such as
  `Мама` and Ukrainian sentences survive Prefab Stage save/reload.
- Text updates record Undo, dirty the component and prefab contents root, and
  explicitly mark the Prefab Stage scene dirty.
- `SetGraphic` responses now include full legacy/TMP text state in `before`
  and `after`, requested/applied/changed field lists, Prefab Stage state, and
  `noChangesApplied`/`alreadyUpToDate` signals.
- Calls with no graphic settings, text settings on a non-text target, or
  incompatible Image/TMP settings fail explicitly instead of returning a
  misleading success.
- The MCP Prefab Stage smoke creates two TMP labels, writes `Мама` and a
  Ukrainian sentence, saves the prefab, rereads YAML, and verifies serialized
  text and font sizes.

## 0.2.40

### Fixed

- `UniBridge_ManageUI` creation actions now scope explicit `Parent` lookup to
  the current Prefab Stage. `CreateCanvas`, `CreateElement`, `CreateTemplate`,
  and `CreateScrollView` create their complete hierarchy in the prefab-stage
  scene instead of falling back to the active ordinary scene.
- Explicit parents that are missing or ambiguous now fail before creating any
  objects. Responses include the requested scope, duplicate candidates where
  applicable, `noObjectsCreated=true`, and `fallbackSuppressed=true`.
- UI creation accepts `ParentObjectIdString` for duplicate-safe targeting by a
  stringified Unity object/EntityId. It takes precedence over `Parent` and
  `Target`.
- Newly created UI objects are moved into the resolved parent's scene before
  parenting. This keeps nested template, label, button, viewport, content, and
  scroll-item objects inside Prefab Stage as well.
- `CreateCanvas` does not create scene infrastructure while editing prefab
  contents. If `EnsureEventSystem=true`, the response returns an explicit
  `eventSystemSkippedReason` instead of mutating the ordinary scene.
- `UniBridge_BatchActions` UI preflight now applies the same Prefab Stage,
  missing-parent, ambiguity, and object-id validation rules as direct tool
  execution.
- The MCP smoke regression suite adds optional isolated Prefab Stage UI
  coverage for path/object-id parent resolution, Canvas/template/scroll
  creation, safe rejection, saved asset structure, and ordinary-scene leakage.
- The smoke runner now unwraps the UniBridge response envelope centrally and
  requires real compilation/console health evidence. Missing nested diagnostic
  fields can no longer pass as an implicit zero count.
- Expected client cancellation of a scheduled tool is returned as a
  controlled canceled MCP result without being duplicated as red registry and
  bridge errors in the Unity Console.

## 0.2.39

### Fixed

- MCP schema generation now distinguishes `Newtonsoft.Json.Linq.JArray` from
  other `JToken` types. `RuntimeStateProbeParams.Assertions` is advertised as
  `type: array` with object items instead of the incorrect dictionary-like
  object schema.
- Shared typed-tool deserialization now accepts a single JSON object for a
  `JArray`-backed property and safely normalizes it to a one-item array. This
  keeps simplified agent calls compatible without changing the canonical MCP
  schema.
- The MCP smoke regression suite now verifies both the generated Assertions
  schema and a live single-object RuntimeStateProbe assertion call.

## 0.2.38

### Fixed

- `UniBridge_WorkSession` semantic review now detects stale/noisy scene
  baselines when a reload, prefab-stage transition, additive scene reload, or
  object-id churn leaves no common scene object ids between the baseline and
  current loaded-scene snapshot. Instead of returning thousands of fake
  deleted/created object changes, it returns a compact
  `semanticBaselineStale=true`, `reviewSkipped=true`,
  `reason=noCommonSceneObjectIds`, `suggestedAction=refreshWorkSessionBaseline`
  status with the suppressed change count.
- Compact WorkSession review now uses a lightweight scene identity/count
  capture. `UniBridge_ExecutionStatus` and batch post-review can detect stale
  semantic baselines without serializing component lists, renderer state,
  prefab metadata, and transform signatures for every loaded scene object.
- Compact WorkSession changed-file review now uses a metadata-only scan instead
  of hashing every tracked file. Full hash-accurate file review remains
  available through `UniBridge_WorkSession Action=Review`.
- `UniBridge_BatchActions` post-action WorkSession review is now lightweight by
  default. It still appends changed-file review data for executing batches, but
  loaded-scene semantic review only runs when
  `IncludeWorkSessionSemanticReview=true` is passed explicitly.
- `UniBridge_ExecutionStatus` active WorkSession summaries now expose bounded
  semantic review controls: `IncludeWorkSessionSemanticReview` and
  `WorkSessionMaxSemanticChanges`. This keeps scheduler diagnostics quick while
  still letting agents request stale semantic-baseline evidence on demand.

## 0.2.37

### Fixed

- `UniBridge_ManageGameObject Action=SetComponentProperty` now resolves Unity
  object references more reliably, including `objectIdString`, scene
  `{find, method, component}` payloads, asset paths/GUIDs, and
  `{guid,fileID,type}` subasset references.
- TextMeshPro font assignment now uses a high-level path that updates
  `m_fontAsset`, `m_sharedMaterial`, `font`, `fontSharedMaterial`, and the
  attached renderer material when a matching TMP material can be resolved. The
  response includes before/after font and material evidence instead of a bare
  success message.
- `MeshRenderer.sharedMaterial` and `MeshRenderer.sharedMaterials` now accept
  material reference payloads directly and return actionable resolution errors
  instead of misleading property-not-found diagnostics.
- Custom `UnityEngine.Object`/`Component` fields and properties can now be set
  from stable reference payloads, including hierarchy path plus component type,
  object id strings, and asset subasset references. Unresolvable non-null
  payloads fail explicitly instead of reporting a silent success.
- `UniBridge_ManageUI Action=SetGraphic` can update world-space
  `TMPro.TextMeshPro` targets, not only `RectTransform` UI graphics, and reports
  the resulting TMP font/material state.
- `UniBridge_ValidateScript` no longer reports the generic
  `Rigidbody operations in Update()` warning just because a file contains both
  `Rigidbody` references and unrelated `SerializedObject.Update()` calls. The
  warning is now based on actual `void Update()` method bodies.
- `UniBridge_BatchActions` now accepts read-only `UniBridge_ValidateScript`
  steps for scripts under both `Assets/...` and `Packages/...`, so agents can
  validate embedded package scripts through the same batch workflow.

## 0.2.36

### Fixed

- `UniBridge_RuntimeStateProbe` and `UniBridge_RuntimeProfiler` sampling loops
  now honor MCP/client cancellation and scheduler timeouts while yielding across
  editor frames. A canceled or timed-out read-only sample releases its scheduler
  read slot instead of blocking later capture or mutating tools.
- The MCP bridge now propagates transport cancellation into the tool registry
  and scheduler for read-only/observer tools, so client disconnects/cancels are
  visible to long-running probes without canceling reload-safe mutating or
  compile boundary workflows.
- `UniBridge_ExecutionStatus` now exposes `Action=ReapStale` to cancel and
  release stale read-only operations that exceeded their timeout, plus
  `canceled`, `timedOut`, and `reaped` counters in scheduler snapshots.
- The MCP smoke regression suite now includes a targeted
  `RuntimeStateProbe` cancel/timeout slot-release check to catch stuck
  `activeReaders` regressions.

## 0.2.35

### Added

- Added `Tools~/McpSmokeRegression/Run-McpSmokeRegression.ps1`, with a Python
  JSON-RPC helper, as a repeatable MCP stdio smoke regression suite for live
  Unity projects.
- The suite verifies core `tools/list` discoverability, `UniBridge_Discover`,
  console clear/read, editor readiness, `ValidateScript` for package scripts,
  `AssetIntelligence Action=ReadText`, compact `ContextSnapshot`,
  `SceneObjectView`, `WorkflowRecipes Execute RunCoreSmokeTest`,
  refresh/compile reload boundaries, `GetCompilationDiagnostics`, and final
  console health.
- The runner writes a JSON report under the target project by default and exits
  non-zero when any regression assertion fails. Optional switches extend
  coverage to Play Mode, UI recipe, and asset recipe smoke checks.

## 0.2.34

### Improved

- `UniBridge_AssetIntelligence` now accepts `Action=ReadText` as a natural
  alias for `Action=Read`, so agents that ask to read a text asset do not
  trigger enum-deserialization errors in the Unity Console.
- Split the legacy `UniBridge_Script` validation internals into
  `ManageScript.Validation.cs`. The script validation behavior stays the same,
  but the large tool implementation is easier to inspect and maintain.
- Tightened two legacy Unity validation warnings so `FindObjectOfType` and
  `GameObject.Find` warnings only fire for real calls inside `Update()`, not
  for diagnostic text literals inside a script.

## 0.2.33

### Improved

- Added a shared `ProjectPathResolver` helper for consistent resolution of
  `Assets/...`, `Packages/...`, `ProjectSettings/...`, `file://`,
  `unity://path/...`, and absolute project paths.
- `UniBridge_ValidateScript` now validates `.cs` files from package paths and
  absolute project paths directly, instead of routing through the legacy
  Assets-only script tool path.
- Asset intelligence, semantic YAML diff, script intelligence, Unity search,
  additive scene registration validation, and batch rollback diagnostics now
  use the shared resolver for more reliable `exists`, absolute path, package
  path, and project-relative path evidence.

## 0.2.32

### Improved

- `assemblyFreshness` now has a v2 mode based on
  `CompilationPipeline.GetAssemblies()`. UniBridge checks each Unity script
  assembly output against its newest source file, covering asmdef, package,
  runtime, and editor assemblies instead of only `Assembly-CSharp.dll`.
- `GetCompilationDiagnostics` and `WaitForReadyAfterReload` now surface
  `staleAssemblyCount`, `missingOutputAssemblyCount`, `staleAssemblies`, and
  `newestSourceAssemblies` evidence so agents can spot stale runtime/editor
  assemblies after Bee, import, or script compilation failures.
- The old `Assembly-CSharp.dll` fields remain in `assemblyFreshness` for
  compatibility, while top-level `staleLikely` now reflects the broader v2
  assembly map.

## 0.2.31

### Improved

- `UniBridge_ManageEditor Action=WaitForReadyAfterReload` now returns
  `buildSystemHealth` and `assemblyFreshness` once at the top level, while
  nested `compilationDiagnostics` is scoped to retained
  `CompilationPipeline` / editor event diagnostics. This keeps reload
  checkpoint responses easier for AI agents to scan without removing the
  critical build-system evidence added in 0.2.30.
- `compileHealth.healthy` now also considers `assemblyFreshness.staleLikely`,
  so a stale runtime assembly is visible in the compact health summary.
- Reload-safe relay recovery envelopes now strip nested `structuredContent`
  mirrors from embedded Unity tool results. Top-level MCP structured content is
  unchanged, but refresh/compile/play recovery responses are smaller and easier
  for agents to scan.

## 0.2.30

### Fixed

- `UniBridge_ReadConsole Action=DiagnosticSummary` now treats Unity
  Bee/BuildProgram failures as critical issues even when Unity records the
  first console line as a plain `Log`. Fingerprints include internal build
  system errors, `BuildProgram exited with code`, `FileLoadException`, Code
  Integrity blocks, Application Control policy blocks, and blocked
  `NiceIO.dll` loads.
- `UniBridge_ManageEditor Action=GetCompilationDiagnostics` now includes
  `buildSystemHealth` beside retained `CompilationPipeline` diagnostics, so a
  clean C# diagnostics list no longer hides lower-level build worker failures.
- `WaitForReadyAfterReload` now returns the same build-system health plus
  `Assembly-CSharp.dll` freshness evidence, helping agents detect stale runtime
  assemblies after failed imports or script compilation.
- `UniBridge_ReadConsole Action=Search` now matches filter text in the full
  console payload and stack trace, not only the first message line.

## 0.2.29

### Fixed

- Added string object ID fields across scene/object serializers and
  `ObjectIdString` / `ParentObjectIdString` inputs for
  `UniBridge_ManageSceneHierarchy`, so JavaScript/JSON MCP clients can safely
  move Unity 6 scene objects whose EntityId values exceed JavaScript's safe
  integer range.
- `UniBridge_BatchActions` now accepts nested step payloads in an `arguments`
  object as well as `parameters`, `params`, and `args`, matching common MCP
  client request shapes.
- Kept Play Mode transition handling reload-safe while removing an
  over-eager early-boundary path that could make a normal focused Play Mode
  transition look suspicious during smoke tests.

### Tested

- Full live MCP smoke on `UniBridge_Test_Project` under Unity 6000.5.0f1:
  54 passed, 0 failed. The only warnings were expected cleanup attempts for a
  non-existent old smoke root.

## 0.2.28

### Fixed

- Fixed Unity 6000.5 compatibility errors in `UniBridge_UnitySearch` and
  `UniBridge_TypeSchema` by replacing direct obsolete `InstanceID` API usage
  with the existing UniBridge Unity API adapter.
- `UniBridge_UnitySearch` native scene result resolution now accepts both
  EntityId-style IDs and legacy instance-id provider payloads without direct
  obsolete API calls.
- Fixed Roslyn dependency resolution on Unity 6000.5 by using
  `Microsoft.CodeAnalysis` 4.13.0 with `System.Collections.Immutable` and
  `System.Reflection.Metadata` 8.0.0, matching Unity 6000.5 reference
  assemblies.
- Suppressed intentional `LightProbeProxyVolume` deprecation warnings while
  keeping support for existing scenes that still contain that component.

## 0.2.27

### Added

- Added `UniBridge_AssetIntelligence Action=SemanticDiff`, a read-only
  semantic diff for Unity YAML/text assets such as prefabs, scenes, materials,
  controllers, `.asset` files, and `.meta` files.
- `SemanticDiff` reports YAML document created/deleted/modified counts,
  class/fileID changes, changed properties, GUID/script reference deltas,
  risk summary, and bounded line diff samples.
- Added discoverability and batch aliases for `semantic_asset_diff`,
  `asset_semantic_diff`, `yaml_semantic_diff`, `unity_yaml_diff`,
  `prefab_semantic_diff`, `asset_diff`, and `semantic_diff`.

## 0.2.26

### Improved

- Polished agent-facing playbooks for new AI agents working through UniBridge.
- `UniBridge_ToolGuide` now includes `Workflow Topic=agent_playbook`, a
  compact read-before-modify, scope-awareness, safe-execution, and
  verification-ladder protocol.
- `UniBridge_ContextSnapshot` `agentBrief` now returns `operatingProtocol` and
  `verificationLadder` beside project shape, risk flags, guardrails, and
  recommended next calls.
- `UniBridge_DomainCatalog` now returns per-domain `riskControls` with what to
  inspect before editing, how to execute safely, how to verify, and which red
  flags matter for that domain.
- Added discoverability aliases for `agent_playbook`, `playbook`,
  `read_before_modify`, `verification_ladder`, `risk_controls`, and
  `operating_protocol`.

## 0.2.25

### Added

- Added `UniBridge_RuntimeProfiler Action=Hierarchy`, a read-only profiler
  marker hierarchy export for AI performance triage.
- `Hierarchy` samples available Unity `ProfilerRecorder` marker handles for
  one frame or a short bounded window, then returns category summaries, top
  marker paths, and a synthetic hierarchy.
- Added controls for `ProfilerCategories`, `MarkerFilters`,
  `ExcludeMarkerFilters`, `MaxProfilerMarkers`, `MaxHierarchySamples`,
  `MaxHierarchyDepth`, `MinHierarchySampleMs`, and `IncludeCounters`.
- Added discoverability and batch aliases for `profiler_hierarchy`,
  `marker_hierarchy`, `runtime_hierarchy`, `frame_export`, `frame_hierarchy`,
  `top_markers`, and `hot_markers`.

### Notes

- `Hierarchy` is intentionally based on stable profiler marker handles rather
  than Unity Profiler Window internals. It is a bounded marker hierarchy /
  top-sample view, not Unity's full call tree.

## 0.2.24

### Added

- Added `UniBridge_ScriptIntelligence Action=ChangeImpact`, a read-only C#
  source-change preflight for agents before applying larger script edits.
- `ChangeImpact` compares the current script with `ProposedSource` or
  `ProposedPath` and reports syntax diagnostics, type/member shape changes,
  serialized field risk, Unity callback risk, public API risk, source deltas,
  and expected refresh/compile/domain-reload boundaries.
- Results include suggested follow-up calls for `CodeUsages`, `MemberUsages`,
  `ValidateScript`, asset refresh, compilation, reload waiting, and console /
  compilation diagnostics.
- Added discoverability and batch aliases for `change_impact`,
  `script_change_impact`, `script_preflight`, `hot_diff`, `reload_risk`, and
  `api_change_impact`.

### Improved

- `UniBridge_ScriptIntelligence` source-shape parsing now keeps declaring type
  context for parsed fields, properties, and methods, making member diffs and
  risk output easier for agents to read.
- Script preflight workflows now cover four complementary questions:
  script GUID references (`Usages`), serialized Unity member references
  (`MemberUsages`), C# caller/type references (`CodeUsages`), and proposed
  source-change impact (`ChangeImpact`).

## 0.2.23

### Added

- Added `UniBridge_ScriptIntelligence Action=CodeUsages`, a read-only
  syntax-based C# impact scan for risky API renames, deletes, and signature
  changes.
- `CodeUsages` finds target type references and member call sites across C#
  scripts, including method invocations, member access, conditional access,
  `nameof(...)`, possible identifier references, and string-based Unity/runtime
  callbacks such as `SendMessage`, `Invoke`, and `StartCoroutine`.
- Code usage results include script path/GUID/type, line, column, usage kind,
  confidence (`Exact`, `Possible`, or `RuntimeResolved`), symbol, containing
  code context, note, and preview line.
- Added discoverability and batch aliases for `code_usages`, `caller_scan`,
  `callers`, `member_callers`, and `code_member_usages`.

### Improved

- `UniBridge_BatchActions` now normalizes `ScriptIntelligence CodeUsages`
  action aliases plus `IncludeSelfReferences`, `IncludeStringReferences`, and
  `MaxReferences` parameter aliases.
- Script impact workflows now distinguish script asset GUID usage
  (`Action=Usages`), serialized Unity member usage (`Action=MemberUsages`), and
  C# source caller/type usage (`Action=CodeUsages`).

## 0.2.22

### Added

- Added `UniBridge_ScriptIntelligence Action=MemberUsages`, a read-only
  serialized member usage scan for Unity assets.
- `MemberUsages` finds:
  - `UnityEvent` persistent method bindings by `m_MethodName`;
  - `AnimationEvent` function names in animation clips;
  - serialized field entries on target `MonoBehaviour`/script components.
- Member usage results include `assetPath`, `line`, `column`,
  `propertyPath`, YAML document info, inferred object path,
  duplicate-safe `indexedObjectPath`, component/script type, usage kind,
  confidence, note, and preview text.
- Added discoverability and batch aliases for `member_usages`,
  `serialized_member_usages`, `serialized_member_search`,
  `unity_event_usages`, `animation_event_usages`, and
  `serialized_field_usages`.

### Improved

- `UniBridge_BatchActions` now normalizes `ScriptIntelligence`
  `MemberUsages` action aliases plus `member`/`method`/`field`/`function`
  parameter aliases.
- Script reference workflows now cover both script GUID references and
  serialized member-level references, making callback/field rename preflight
  checks safer for agents.

## 0.2.21

### Added

- Added bounded YAML reference locations for
  `UniBridge_AssetIntelligence Action=ReferenceGraph`, `Dependents`, and
  `Impact` via `IncludeReferenceLocations=true`.
- Reference locations include asset path, target GUID/path, line, column,
  property path, YAML document type/fileId, inferred GameObject path,
  duplicate-safe indexed object path, component/script type, and a short
  preview line.
- Added script usage locations for
  `UniBridge_ScriptIntelligence Action=Usages` and
  `Analyze IncludeUsages=true` via `IncludeUsageLocations=true`.
- Added discoverability and batch aliases for `asset_ref_search`,
  `asset_reference_search`, `asset_usages`, `reference_graph`,
  `reference_locations`, `script_usages`, `asset_script_usages`, and
  `guid_usages`.

### Improved

- `UniBridge_BatchActions` now normalizes `AssetIntelligence`
  `ReferenceGraph`/`Impact` aliases and `ScriptIntelligence` usage aliases,
  making reference-location checks easier to compose in batch workflows.

## 0.2.20

### Added

- Added `UniBridge_AssetIntelligence Action=Structure`, a read-only
  prefab/loaded-scene asset structure workflow for agents that need a compact
  hierarchy map before editing.
- `StructureMode=List` returns bounded hierarchy nodes with normal paths,
  duplicate-safe `indexedPath` values, active/tag/layer data, components,
  missing-script counts, prefab source hints, and duplicate/component summary
  data.
- `StructureMode=Search` finds objects by path, name, component, tag, layer,
  prefab source, and optional serialized field names/values with
  `MatchFields=fields` or `MatchFields=all`.
- `StructureMode=Read` drills into one `ObjectPath`/`indexedPath` and returns
  transform, component details, renderer sorting data, child summaries, and
  bounded serialized properties.
- Added `BatchActions` and discoverability aliases such as `asset_structure`,
  `prefab_structure`, `scene_asset_structure`, `structure_search`,
  `serialized_asset_search`, and `read_yaml`.
- Improved `UniBridge_Discover Action=Tools` query matching so multi-token
  searches such as `AssetIntelligence Structure asset_structure` match across
  tool names, descriptions, and aliases.
- Improved `UniBridge_ToolGuide Action=Workflow` matching so exact workflow
  keys win before broader alias matches.

## 0.2.19

### Added

- Added `UniBridge_TypeSchema Action=TypeIndex` for compact loaded Unity type
  lookup before component, ScriptableObject, importer, asset, and shader
  authoring.
- `TypeIndex` can write a bounded full JSON index under
  `Library/UniBridge/TypeIndex` with `WriteToFile=true`, while returning only
  compact samples through MCP.
- Added `UniBridge_TypeSchema Action=TypeFingerprint`, which reports a loaded
  assembly fingerprint plus index key so agents can safely reuse cached type
  maps across reload-sensitive workflows.
- Type index entries include simple/full names, assembly hints, domain tags,
  base type, ambiguity summaries, and direct hints for follow-up
  `TypeSchema Inspect` calls.
- `UniBridge_BatchActions` validation and normalization now accept
  `TypeIndex` / `TypeFingerprint` steps and common aliases such as
  `type_index`, `type_map`, and `fingerprint`.

## 0.2.18

### Added

- Added opt-in `UniBridge_BatchActions` post-action diagnostics.
- `IncludeConsoleDelta=true` creates a console marker before the batch and
  appends a compact `DiagnosticSummary` for console entries emitted during that
  batch.
- `IncludeEditorEventDelta=true` appends a bounded editor event delta for
  hierarchy, project/assets, compilation, assembly reload, play mode, package,
  and object-change events emitted during that batch.

## 0.2.17

### Added

- Added `UniBridge_RuntimeStateProbe Action=Assert`, a read-only runtime
  assertion/watch workflow for AI gameplay debugging.
- Assertions can evaluate sampled component values with equals/not-equals,
  numeric comparisons, ranges, contains/not-contains, regex matches, null
  checks, `changed`, and `stable` rules.
- Required assertion failures return `success=false` by default so
  `UniBridge_BatchActions` can stop safely before an agent continues from a bad
  runtime assumption.

### Changed

- Runtime state probing now exposes `runtime_assert`, `watch_assert`,
  `state_assert`, and `expect_state` aliases through `Discover`, `ToolGuide`,
  `DomainCatalog`, and `BatchActions`.

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
