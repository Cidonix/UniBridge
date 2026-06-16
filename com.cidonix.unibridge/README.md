# UniBridge

UniBridge is a local Unity MCP bridge for AI-assisted game development.

It gives MCP-compatible AI coding agents practical tools for working inside
real Unity Editor projects: inspect scenes and assets, create and validate UI,
edit scripts safely, work with prefabs, capture previews, author animation,
physics, input actions, timeline content, and more.

UniBridge is local-first. The package runs a bridge inside the Unity Editor,
installs a local relay executable, and exposes controlled per-project MCP tools
through an approval-based connection flow.

## Package Contents

- Unity Editor MCP bridge and Project Settings UI.
- Local relay binaries for Windows x64, Linux x64, macOS x64, and macOS arm64.
- Agent-facing tools for project context, search, assets, scripts, scenes,
  prefabs, UI, capture, validation, animation, rendering, physics, navigation,
  tilemaps, input actions, timeline, audio, VFX, runtime profiling, runtime
  state probing, and workflow recipes.
- Read-only asset structure workflows for prefab and already-loaded scene
  assets, including compact hierarchy list/search/read with duplicate-safe
  indexed paths and optional serialized field matching.
- Location-aware asset/script reference workflows that report exact YAML
  line/property/object context before risky rename, move, delete, or script
  migration work.
- Read-only C# source-change preflight that compares current scripts with
  proposed source and reports API, serialized-field, Unity-callback, syntax,
  and reload risk before applying edits.
- `UniBridge_Discover`, a stable read-only ping/workflow entry point for new
  MCP clients and Codex sessions.
- `UniBridge_WorkSession`, a project-local checkpoint/review/revert layer that
  lets agents summarize changed files, review loaded-scene semantic changes,
  inspect compact text diffs, and dry-run selected file reverts after broad AI
  work.
- Large-scene hierarchy export, JSON/JSONL file output, export comparison, and
  safe objectId-based batch reparent/container workflows.
- Read-only additive scene registration validation for cloned/additive scene
  workflows: scene/meta GUIDs, metadata, Build Settings, scenesManager entries,
  and boundary arrays.
- Documentation, changelog, license, and third-party notices.

## Requirements

This version is compatible with Unity Editor 6000.0 and later. It has been
smoke-tested locally on Unity 6000.4.10f1 on Windows. Linux and macOS relay
artifacts are bundled and cross-built, but should be verified on target systems
before production use.

UniBridge requires an MCP-compatible client that can launch a local executable,
such as Codex, Claude Code, Gemini, Cursor, or another compatible client.

## Installation From A Release Archive

1. Download the versioned UniBridge release archive.
2. Extract the archive.
3. Copy or keep the extracted `com.cidonix.unibridge` folder in a stable
   location.
4. In Unity, open `Window > Package Manager`.
5. Choose `+ > Add package from disk...`.
6. Select `com.cidonix.unibridge/package.json`.
7. Wait for Unity to compile.
8. Open `Project Settings > UniBridge > MCP`.
9. Confirm that the Local Bridge is running.
10. Use the Integrations section to configure your MCP client for this project.

## First Connection

After adding the UniBridge MCP entry to your AI agent or MCP client
configuration, restart the AI agent/MCP client itself. Restarting Unity is not
enough, because most MCP clients only read server configuration when the client
starts.

When the restarted client launches the UniBridge relay, Unity shows a
connection approval dialog. Review the client executable path and identity, then
allow the connection if you trust that local client.

## Multiple Open Unity Projects

Use one MCP server entry per Unity project and prefer the generated
`--project-id` configuration. This avoids accidentally targeting the wrong open
Unity project.

UniBridge can expose two or more open Unity projects to the same agent at the
same time when each project has its own MCP server entry and its own
`--project-id`. After adding, removing, or renaming these entries, restart the
AI agent/MCP client so it reloads the available Unity projects.

## Documentation

Full documentation lives in `Documentation~/unibridge.md`.

Version-specific notes live in `RELEASE_NOTES.md`, and package history lives in
`CHANGELOG.md`.

## Known 0.2.24 Notes

- `UniBridge_ScriptIntelligence Action=ChangeImpact` can compare the current
  `.cs` file with `ProposedSource` or `ProposedPath` before an agent applies a
  larger source edit.
- It reports syntax diagnostics, type/member shape changes, public API risk,
  inspector serialized-field risk, Unity callback risk, source deltas, and
  expected refresh/compile/domain-reload boundaries.
- It returns suggested follow-up calls for `CodeUsages`, `MemberUsages`,
  `ValidateScript`, refresh, compile, reload wait, and console / compilation
  diagnostics.
- Use it before risky script edits:
  `Action=ChangeImpact Path=Assets/.../<script>.cs ProposedSource=<candidate>`.
- `BatchActions`, `Discover`, `ToolGuide`, and `DomainCatalog` expose aliases
  such as `change_impact`, `script_preflight`, `hot_diff`, `reload_risk`, and
  `api_change_impact`.

## Known 0.2.23 Notes

- `UniBridge_ScriptIntelligence Action=CodeUsages` can find C# source call
  sites and target type/member references before risky renames, deletes, or
  signature changes.
- It reports method invocations, member access, conditional access,
  `nameof(...)`, possible identifier matches, and string-based callbacks such
  as `SendMessage`, `Invoke`, and `StartCoroutine`.
- Results include line/column, usage kind, confidence (`Exact`, `Possible`, or
  `RuntimeResolved`), code context, symbol, note, and preview line.
- Use it together with `Usages` and `MemberUsages`:
  `Action=CodeUsages Path=Assets/.../<script>.cs Member=<methodOrField>`.
- `BatchActions`, `Discover`, `ToolGuide`, and `DomainCatalog` expose aliases
  such as `code_usages`, `caller_scan`, `callers`, `member_callers`, and
  `code_member_usages`.

## Known 0.2.22 Notes

- `UniBridge_ScriptIntelligence Action=MemberUsages` can find serialized
  references to one script member in Unity assets.
- It reports `UnityEvent` persistent method bindings, `AnimationEvent`
  function names, and serialized field entries with bounded YAML
  line/property/object context.
- Use it before renaming or removing callback methods and inspector fields:
  `Action=MemberUsages Path=Assets/.../<script>.cs Member=<methodOrField>`.
- `BatchActions`, `Discover`, `ToolGuide`, and `DomainCatalog` expose aliases
  such as `member_usages`, `serialized_member_usages`,
  `unity_event_usages`, `animation_event_usages`, and
  `serialized_field_usages`.

## Known 0.2.21 Notes

- `UniBridge_AssetIntelligence Action=ReferenceGraph`, `Dependents`, and
  `Impact` can now return bounded exact reference locations with
  `IncludeReferenceLocations=true`.
- Reference locations include `assetPath`, `line`, `column`, `propertyPath`,
  YAML document type/fileId, inferred `objectPath`, duplicate-safe
  `indexedObjectPath`, component/script type, and a short preview.
- `UniBridge_ScriptIntelligence Action=Usages` can return exact prefab/scene
  YAML locations for script GUID references with `IncludeUsageLocations=true`.
- `UniBridge_BatchActions`, `Discover`, `ToolGuide`, and `DomainCatalog`
  expose aliases such as `asset_ref_search`, `reference_locations`,
  `script_usages`, `asset_script_usages`, and `guid_usages`.

## Known 0.2.20 Notes

- `UniBridge_AssetIntelligence Action=Structure` adds read-only
  prefab/loaded-scene asset structure inspection.
- Use `StructureMode=List` for a compact hierarchy map with `path`,
  duplicate-safe `indexedPath`, active/tag/layer data, component names,
  missing-script counts, prefab source hints, and summary statistics.
- Use `StructureMode=Search` with `Query`, `ComponentFilter`, and optional
  `MatchFields=fields|all` to find objects without reading the whole asset.
- Use `StructureMode=Read ObjectPath=<indexedPath>` to inspect one object with
  transform, component, renderer sorting, child, and bounded serialized
  property data.
- The workflow is read-only. It does not open unloaded scenes automatically;
  load/open the scene first or use `Action=Read` for raw YAML text.
- `UniBridge_BatchActions` accepts aliases such as `asset_structure`,
  `prefab_structure`, `scene_asset_structure`, `structure_search`,
  `serialized_asset_search`, and `read_yaml`.

## Known 0.2.19 Notes

- `UniBridge_TypeSchema` now includes `Action=TypeIndex` for compact loaded
  Unity type lookup.
- Pass `WriteToFile=true` to write a bounded full type index under
  `Library/UniBridge/TypeIndex` while keeping the MCP response compact.
- `Action=TypeFingerprint` returns a stable loaded-assembly fingerprint and
  index key so an agent can decide whether a cached type index is still valid.
- Type index entries include simple/full names, assembly, kind, domain tags,
  base type, ambiguity hints, and inspect/add-component/create-asset hints.
- `UniBridge_BatchActions` validates and normalizes `TypeIndex` /
  `TypeFingerprint` steps, so agents can include type resolution in larger
  workflows.

## Known 0.2.18 Notes

- `UniBridge_BatchActions` can append opt-in post-action diagnostics.
- Pass `IncludeConsoleDelta=true` to create a console marker before the batch
  and receive a compact console diagnostic delta for entries emitted during the
  batch.
- Pass `IncludeEditorEventDelta=true` to receive a bounded editor event delta
  for hierarchy/project/compilation/play-mode changes emitted during the batch.

## Known 0.2.17 Notes

- `UniBridge_RuntimeStateProbe Action=Assert` adds read-only runtime
  watch/assertion rules on top of the state probe.
- Assertions can check sampled component values with equals/not-equals,
  numeric comparisons, ranges, contains/not-contains, regex matches, null
  checks, `changed`, and `stable` modes.
- Required failed assertions return `success=false` by default, which lets
  `UniBridge_BatchActions` stop a workflow before an agent continues from an
  invalid runtime assumption.

## Known 0.2.16 Notes

- `UniBridge_RuntimeStateProbe` is a read-only runtime state probe for
  GameObjects and components. It supports `Snapshot`, `Sample`, `Assert`, and
  `ListMembers` without executing arbitrary C# in the project.
- Target lookup uses the shared scene resolver, including inactive objects,
  Prefab Stage objects, instance IDs, hierarchy paths, component short/full
  names, MonoScript GUIDs, and serialized editor class identifiers.
- `Action=Sample` requires Play Mode by default, returns compact
  changed-member summaries, and writes full raw payloads under
  `Library/UniBridge/RuntimeStateProbe` when `SaveToFile=true`.

## Known 0.2.15 Notes

- `UniBridge_RuntimeProfiler` is a read-only runtime/profiler tool for Play
  Mode debugging. It can return current runtime state, loaded-scene object
  counts, Unity profiler memory counters, and supported metric aliases.
- `Action=Sample` captures bounded `ProfilerRecorder` samples for frame time,
  GC allocation, memory, rendering counters, physics/script markers, and spike
  summaries. It requires Play Mode by default.
- Full raw sample payloads are saved under
  `Library/UniBridge/RuntimeProfiler` when `SaveToFile=true`; MCP responses
  stay compact unless `ReturnSamples=true`.

## Known 0.2.14 Notes

- `UniBridge_WorkSession Action=Begin` captures a compact loaded-scene semantic
  baseline by default. Use `IncludeSceneSemantics=false` to disable it, or
  `MaxSemanticObjects=<n>` to bound very large scenes.
- `Action=Status` and `Action=Review` include `semanticReview` with
  created/deleted/moved/renamed GameObjects, component changes, renderer
  sorting changes, prefab-info changes, transform changes, and missing-script
  deltas by stable object id.
- Semantic review is a visibility/self-check layer for loaded scenes. File
  revert behavior is unchanged: `Action=Revert` restores/deletes selected files
  from the WorkSession snapshot, not arbitrary live scene objects.

## Known 0.2.12 Notes

- If a WorkSession is active, `UniBridge_BatchActions DryRun=false` appends a
  `workSessionReview` block by default. This gives agents an immediate
  changed-file and semantic scene summary after a mutating batch.
- Use `IncludeWorkSessionReview=true` to include that block in dry-runs, or
  `WorkSessionReviewMaxChanged=<n>` to tune response size.
- `UniBridge_ExecutionStatus Action=Snapshot` and `Action=Recent` include the
  active WorkSession summary by default. Pass `IncludeWorkSession=false` for a
  scheduler-only response.

## Known 0.2.11 Notes

- Use `UniBridge_WorkSession Action=Begin Name=<task>` before broad scene,
  asset, script, UI, or prefab work.
- After edits, call `UniBridge_WorkSession Action=Review` and, when needed,
  `Action=Diff Paths=[...]` to see exactly what changed.
- `Action=Revert` defaults to `DryRun=true`. Repeat with `DryRun=false` only
  after reviewing the revert plan. Session snapshots are stored under
  `Library/UniBridge/WorkSessions`.

## Known 0.2.10 Notes

- `RefreshAssets WaitForCompletion=true` can cross a Unity import/domain-reload
  boundary in real projects. Bundled relay `1.1.0-build.15` recovers that
  connection loss, waits for editor readiness, and returns structured
  `reloadBoundary` / `nextSuggestedCalls` data instead of an MCP transport
  failure.

## Known 0.2.9 Notes

- If `tool_search` returns zero UniBridge tools in Codex after adding or
  updating the MCP server config, restart the AI agent/MCP client itself so it
  reloads and indexes the UniBridge server. Restarting Unity alone is not
  enough.
- Use `UniBridge_Discover Action=Ping` as the first call in a new Unity
  session to verify that the package is reachable and to see the recommended
  compile diagnostics, Play Mode boundary, and additive scene validation
  workflows.
- Use `UniBridge_ValidateAdditiveSceneRegistration` before Play Mode tests for
  cloned/additive scenes such as `darkness12` or `darkness13`.

## Known 0.2.8 Notes

- Play Mode entry/exit can reload Unity domain in real projects. Use the
  reload-safe flow:
  `UniBridge_ManageEditor Action=RequestPlayModeNoWait`, then
  `UniBridge_ManageEditor Action=WaitForPlayMode`, then
  `WaitForReady RequireNotPlaying=false` / `ReadConsole DiagnosticSummary`.
- `Play WaitForCompletion=true` and `ExitPlayMode WaitForCompletion=true` are
  accepted for old callers, but they now return queued boundary responses
  instead of waiting inline through a domain reload.
- `UniBridge_BatchActions` stops at Play Mode reload boundaries and returns
  `postReconnect.nextSuggestedCalls`; run those follow-up calls after the bridge
  reconnects instead of placing all Play Mode verification steps in one batch.
- Bundled relay `1.1.0-build.13` can recover expected Unity domain-reload
  reconnects during Play Mode transitions and report structured recovery data
  rather than a transport-level `Unity not detected` / `Unity connection closed`
  failure.

## Known 0.2.7 Notes

- Script compilation should use the reload-safe flow:
  `UniBridge_ManageEditor Action=RequestScriptCompilationNoWait`, then
  `UniBridge_ManageEditor Action=WaitForReadyAfterReload`, then
  `GetCompilationDiagnostics` / `ReadConsole`.
- `RequestScriptCompilation WaitForCompletion=true` is accepted for old
  callers, but it now returns a queued compile response instead of waiting
  inline through Unity assembly reload.
- Bundled relay `1.1.0-build.12` can recover expected Unity domain-reload
  reconnects during compilation and report structured recovery data rather
  than a transport-level `Unity connection closed` failure.

## Known 0.2.6 Notes

- The relay binaries are unsigned. Operating systems may show additional
  warnings, especially on macOS.
- Linux and macOS binaries are included but have not yet received the same
  live-project verification as Windows.
- UniBridge is an Editor-only package. It is not intended to be included in
  player builds.
- Some tools use optional Unity packages through reflection. If an optional
  package is missing, those specific operations report the missing dependency
  instead of hard-failing the whole bridge.
- Large-scene hierarchy exports can write full JSON/JSONL files to
  `Library/UniBridge/SceneHierarchyExports`; use the compact MCP response
  `summary` for quick scene orientation before reading the full file.
- `ContextSnapshot` includes `agentBrief` by default. Use it as the first
  new-agent onboarding layer: project shape, likely folders/systems, risk
  flags, guardrails, active WorkSession state, and recommended next calls.
  Pass `IncludeAgentBrief=false` only when you want the older raw snapshot
  shape.
- `CompareExports` keeps duplicate examples compact by default. Use
  `IncludeDuplicateKeys=true` or `IncludeDuplicateSummary=false` only when an
  agent specifically needs verbose keys or a count-only response.
- `Find` / `SearchMethod=ByComponent` can match Prefab Stage components by
  runtime type names, MonoScript GUID, and serialized class identifiers. Use
  `IncludeInactive=true` for inactive prefab or scene objects.
- `ContextSnapshot IncludeConsole` uses `ConsoleSummaryMode=Compact` by
  default. Request `ConsoleSummaryMode=Detailed` only when timeline/sample
  detail is needed.
- `UniBridge_BatchActions` can run read-only `UniBridge_ValidateScript`
  steps. Use this for workflows such as validate several `.cs` files, refresh
  assets, request compilation, wait for the editor, and read console
  diagnostics.
