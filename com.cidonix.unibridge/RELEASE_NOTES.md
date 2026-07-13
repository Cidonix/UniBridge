# UniBridge 0.2.39 Release Notes

Release date: 2026-07-13

This hotfix aligns `UniBridge_RuntimeStateProbe` MCP metadata with its C#
parameter contract. `RuntimeStateProbeParams.Assertions` is now published as a
JSON array whose items are objects, matching the documented
`Assertions=[{...}]` request shape.

The shared typed-tool deserializer is also tolerant of callers that send one
assertion as a bare JSON object. It clones and normalizes that value to a
one-item `JArray` before Newtonsoft deserialization, preventing the previous
`JObject is not compatible with expected type JArray` exception.

The MCP smoke regression suite now checks the tool schema from `tools/list` and
executes a live editor-time assertion using the simplified single-object form.

## Previous 0.2.38 Notes

This hotfix makes `UniBridge_WorkSession` review safe for large scenes after
Unity reloads or object-id churn.

Previously, a WorkSession semantic baseline captured across many loaded or
additive scenes could become stale after a reload. When the current loaded-scene
snapshot had no common object ids with the baseline, semantic review treated
almost every object as deleted and recreated. In large scenes this could create
thousands of fake changes, make `UniBridge_ExecutionStatus
IncludeWorkSession=true` slow, and let `UniBridge_BatchActions` spend too much
time in post-action WorkSession review even though the actual batch had already
completed.

`UniBridge_WorkSession` now detects this condition before building the change
list. If both snapshots contain scene objects but `commonObjects=0`, semantic
review returns a compact stale-baseline result:

- `semanticBaselineStale=true`;
- `reviewSkipped=true`;
- `reason=noCommonSceneObjectIds`;
- `suggestedAction=refreshWorkSessionBaseline`;
- `suppressedChangeCount=<baseline objects + current objects>`.

No fake deleted/created scene-object diff is returned in this case. The
recommended recovery is to end the old WorkSession and begin a new one after the
current scene/prefab/additive scene state is stable.

Compact WorkSession review, used by `UniBridge_ExecutionStatus` and batch
post-action review, now uses a lightweight scene identity/count capture. It
does not serialize component lists, renderer state, prefab metadata, or
transform signatures for every loaded object just to determine whether a
semantic baseline is stale. Full semantic diffs are still available through
`UniBridge_WorkSession Action=Review` when an agent explicitly needs detailed
scene-object changes.

Compact changed-file review now uses a metadata-only scan instead of hashing
every tracked file in large projects. Full hash-accurate review remains
available from `UniBridge_WorkSession Action=Review`, while scheduler snapshots
and post-batch summaries stay fast enough for routine agent self-checks.

`UniBridge_BatchActions` also keeps post-action WorkSession review lightweight
by default. Executing batches still append changed-file review data when
`IncludeWorkSessionReview` is true, but loaded-scene semantic review is skipped
unless the caller explicitly passes `IncludeWorkSessionSemanticReview=true`.
This prevents small mutating batches from timing out because of a heavy
post-action semantic review.

`UniBridge_ExecutionStatus` now exposes bounded WorkSession semantic review
controls for diagnostics:

- `IncludeWorkSessionSemanticReview` controls whether semantic review is
  included in Snapshot/Recent WorkSession summaries;
- `WorkSessionMaxSemanticChanges` bounds the returned semantic change samples
  in compact scheduler diagnostics.

## Previous 0.2.37 Notes

This hotfix improves `SetComponentProperty` for the Unity object-reference
cases that matter when an AI agent edits world-space UI and scene wiring.

`UniBridge_ManageGameObject Action=SetComponentProperty` now has a dedicated
TextMeshPro font path. Setting `font`, `FontAssetPath`, `tmpFontAsset`, or
`tmpFontAssetPath` on a TMP component updates the serialized `m_fontAsset`, the
matching `m_sharedMaterial` when one can be resolved from the font asset, the
runtime `font`/`fontSharedMaterial` members, and the attached renderer's shared
material for world-space text. The tool response now includes before/after TMP
font/material evidence so an agent can verify what Unity actually holds.

Renderer material assignment is also stricter and more useful. `sharedMaterial`
and `sharedMaterials` on a `Renderer` accept asset path/GUID references and
subasset payloads such as `{ "guid": "...", "fileID": 123, "type": 2 }`. If a
material cannot be resolved, UniBridge now returns an actionable material
resolution error instead of a misleading "property not found" message.

Custom `UnityEngine.Object` and `Component` fields/properties can now be set
from stable reference payloads:

- `{ "objectIdString": "..." }`;
- `{ "find": "Root/Child/Text", "method": "ByPath", "component": "TMPro.TextMeshPro" }`;
- asset paths or GUIDs;
- `{ "guid": "...", "fileID": 123, "type": 2 }` for subassets.

If a non-null reference payload cannot be resolved, the operation fails
explicitly instead of reporting success while leaving `{fileID: 0}` in the
scene.

`UniBridge_ManageUI Action=SetGraphic` now also supports world-space
`TMPro.TextMeshPro` objects. That gives agents a high-level way to update
font/material/color on 3D text objects that do not have a `RectTransform`.

The script validator also received a small false-positive cleanup: the
`Rigidbody operations in Update()` warning is now based on actual `void
Update()` method bodies instead of a broad file-level string scan.

`UniBridge_BatchActions` can now run read-only `UniBridge_ValidateScript` steps
for package scripts under `Packages/...` as well as project scripts under
`Assets/...`. This keeps package hotfix validation inside the same safe batch
workflow agents use for normal project scripts.

## Previous 0.2.36 Notes

This hotfix makes long-running read-only sampling resilient to client
cancellation, timeouts, and reconnects.

`UniBridge_RuntimeStateProbe` and `UniBridge_RuntimeProfiler` now use
scheduler-provided cancellation while sampling over editor frames. If an MCP
client cancels a request, disconnects, or the scheduler timeout expires, the
operation is marked canceled/timed out and its read slot is released. This
prevents a canceled runtime probe from blocking later captures, Play Mode
cleanup, or mutating tools.

`UniBridge_ExecutionStatus` also adds `Action=ReapStale`, which lets an agent
or maintainer cancel and release stale read-only operations that exceeded their
timeout. Scheduler snapshots now include cancellation evidence and
canceled/timedOut/reaped counters.

The MCP smoke regression suite now includes a targeted
`RuntimeStateProbe` cancel/timeout slot-release check so future changes catch
stuck `activeReaders` regressions automatically.

## Previous 0.2.35 Notes

The previous release added a repeatable MCP smoke regression suite for live
Unity projects.

`Tools~/McpSmokeRegression/Run-McpSmokeRegression.ps1` runs the bundled
UniBridge relay in MCP stdio mode through a Python JSON-RPC helper and checks
the package through the same surface an AI agent uses. It verifies tool
discovery, `UniBridge_Discover`,
console health, editor readiness, package script validation,
`AssetIntelligence Action=ReadText`, compact `ContextSnapshot`,
`SceneObjectView`, workflow recipe execution, refresh/compile reload boundaries,
compilation diagnostics, and final console diagnostics.

The runner writes a JSON report to the target project's `Library/UniBridge`
folder by default and exits with a non-zero code when a step fails. Optional
switches can add Play Mode, UI recipe, and asset recipe smoke coverage.

## Previous 0.2.34 Notes

This polish release keeps the 0.2.33 behavior stable while reducing friction
for AI agents and maintainers.

`UniBridge_AssetIntelligence` now accepts `Action=ReadText` as an alias for
`Action=Read`. The tool already described reading text-like assets, and agents
may naturally request the action as `ReadText`; that call now works instead of
failing enum parsing and writing an avoidable error to the Unity Console.

Internally, the legacy `UniBridge_Script` router has been split so script
validation lives in `ManageScript.Validation.cs`. This does not change the
validation contract used by `UniBridge_ValidateScript`, script edits, or the
legacy route, but it makes the large tool easier to review and safer for future
maintenance.

While testing the split, two old Unity-specific validation checks were tightened:
`FindObjectOfType` and `GameObject.Find` warnings now require a real invocation
inside a parameterless `Update()` method. This avoids false positives when a
script merely contains those phrases in diagnostic strings.

## Previous 0.2.33 Notes

This release adds a shared project path resolver and wires it into the
path-sensitive tools agents use during scripting, asset inspection, semantic
diffs, additive-scene validation, and batch rollback diagnostics.

The resolver gives UniBridge one consistent interpretation for:

- `Assets/...`;
- `Packages/...`;
- `ProjectSettings/...`;
- `unity://path/...`;
- `file://...`;
- absolute paths inside the current Unity project;
- package paths resolved through Unity's package manager.

`UniBridge_ValidateScript` now uses this shared resolver and validates `.cs`
files directly from disk. That means agents can validate package/editor scripts
and absolute project paths, not only scripts under `Assets/`.

The same resolver is now used by:

- `UniBridge_AssetIntelligence`;
- `UniBridge_AssetIntelligence Action=SemanticDiff`;
- `UniBridge_ScriptIntelligence`;
- `UniBridge_UnitySearch`;
- `UniBridge_ValidateAdditiveSceneRegistration`;
- batch rollback/impact path diagnostics.

This should reduce false `exists=false` style diagnostics and make path evidence
more stable across projects, embedded packages, and MCP clients that pass file
URIs or absolute Windows paths.

## Previous 0.2.32 Notes

This release improves stale assembly diagnostics for projects that use asmdefs,
package code, editor assemblies, or multiple generated script assemblies.

`assemblyFreshness` now includes a v2 block based on
`CompilationPipeline.GetAssemblies()`. UniBridge compares each Unity script
assembly output against the newest source file Unity reports for that assembly.
This covers:

- asmdef assemblies;
- package assemblies;
- runtime assemblies;
- editor assemblies;
- the classic `Assembly-CSharp.dll` path.

`GetCompilationDiagnostics` and `WaitForReadyAfterReload` now expose:

- `staleAssemblyCount`;
- `missingOutputAssemblyCount`;
- `v2.summary`;
- `v2.staleAssemblies`;
- `v2.newestSourceAssemblies`.

The old `Assembly-CSharp.dll` fields are still present for compatibility, but
top-level `assemblyFreshness.staleLikely` now reflects the broader v2 assembly
map. This gives AI agents a better chance to detect "Unity says it is ready,
but the relevant script assembly was not rebuilt" situations.

## Previous 0.2.31 Notes

This polish release keeps the 0.2.30 Bee/BuildProgram diagnostics guardrails
but makes reload checkpoint responses easier for AI agents to read.

`UniBridge_ManageEditor Action=WaitForReadyAfterReload` now reports
`compileHealth`, `buildSystemHealth`, and `assemblyFreshness` once at the top
level. Its nested `compilationDiagnostics` block is now scoped to retained
`CompilationPipeline` / editor event diagnostics instead of repeating the same
build-system evidence again.

The standalone `GetCompilationDiagnostics` action still returns the full
diagnostic picture, including `buildSystemHealth`, `assemblyFreshness`, and
`compileHealth`.

`compileHealth.healthy` also considers `assemblyFreshness.staleLikely`, so an
agent can see a stale runtime assembly as a compact health failure even when
Unity's retained compiler diagnostics are empty.

Relay recovery responses for refresh, script compilation, and Play Mode reload
boundaries also strip nested `structuredContent` mirrors from embedded Unity
tool results. The top-level MCP response still keeps structured content, but
the recovery envelope no longer duplicates large payloads inside its nested
`waitResult` / diagnostics blocks.

## Previous 0.2.30 Notes

This hotfix closes a diagnostics gap found in a Unity 6000.5 project where
Unity's Bee/BuildProgram worker failed before producing a fresh runtime
assembly, but `CompilationPipeline` diagnostics still looked clean.

Unity can report lower-level build worker failures as a console `Log` whose
stack trace contains the real failure. UniBridge now classifies those signals
as critical diagnostics when they include fingerprints such as:

- `Internal build system error`;
- `BuildProgram exited with code`;
- `ScriptCompilationBuildProgram`;
- `System.IO.FileLoadException`;
- `Application Control policy has blocked this file`;
- Code Integrity policy text;
- blocked `NiceIO.dll` loads.

`UniBridge_ManageEditor Action=GetCompilationDiagnostics` now returns both the
retained C# compilation diagnostics and `buildSystemHealth`, so agents can see
that Bee/BuildProgram failed even when Unity did not emit a normal compiler
error.

`WaitForReadyAfterReload` also returns `buildSystemHealth` and
`assemblyFreshness`. The freshness block compares
`Library/ScriptAssemblies/Assembly-CSharp.dll` against the latest `Assets/*.cs`
file and marks `staleLikely=true` when source scripts are newer than the
runtime assembly. This is a compact guardrail against falsely trusting
`isCompiling=false` after a failed build worker run.

`UniBridge_ReadConsole Action=Search` also searches stack traces now, so an
agent can find terms such as `Application Control policy` or `NiceIO.dll` even
when Unity's visible message line is only `Internal build system error`.

## Previous 0.2.29 Notes

Release date: 2026-06-17

This hotfix finishes the Unity 6000.5 / Unity 6.5 compatibility pass with a
focus on real MCP client behavior.

Unity 6 EntityId values can exceed JavaScript's safe integer range. UniBridge
now includes string object ID fields in scene/object snapshots and accepts
`ObjectIdString` / `ParentObjectIdString` in `UniBridge_ManageSceneHierarchy`.
Agents can still use numeric IDs in C#-safe contexts, but JSON/MCP clients
should prefer the string fields for duplicate-safe hierarchy edits.

`UniBridge_BatchActions` now accepts step payloads under `arguments` in
addition to `parameters`, `params`, and `args`. This matches common MCP client
request shapes and keeps nested read-only or mutating tool calls from silently
receiving empty parameters.

Play Mode queue/wait handling remains reload-safe, but the smoke-tested path no
longer reports a false early reload boundary while Unity is simply compiling or
waiting for the Editor window to be focused.

Validation:

- live relay/MCP smoke against `UniBridge_Test_Project`;
- Unity 6000.5.0f1;
- 67 UniBridge tools discovered;
- 54 smoke checks passed, 0 failed;
- editor refresh, compilation diagnostics, console diagnostics, scene export,
  export comparison, `ObjectIdString` hierarchy moves, nested batch
  `arguments`, capture, visual audit, Play Mode entry, and Play Mode exit were
  all exercised.

## Previous 0.2.28 Notes

This hotfix updates UniBridge for Unity 6000.5 compatibility.

Unity 6000.5 reports direct `InstanceID` API usage as obsolete compile errors
in package code. UniBridge now routes the remaining `UnitySearch` and
`TypeSchema` object ID lookups through its version-aware Unity API adapter,
which uses `EntityId` APIs on Unity 6000+ and keeps fallback behavior for older
supported editors.

Native Unity Search scene result resolution also keeps compatibility with
provider payloads that still expose legacy instance IDs, using a reflection
fallback instead of a direct obsolete API call.

The package now uses `Microsoft.CodeAnalysis` 4.13.0 with
`System.Collections.Immutable` and `System.Reflection.Metadata` 8.0.0. This
matches Unity 6000.5's reference assemblies and avoids `CS1705` version
conflicts during Editor compilation.

UniBridge still reports and can manage existing `LightProbeProxyVolume`
components for older/Built-in-pipeline projects, but the intentional Unity
6000.5 deprecation warnings for that component are now suppressed in package
compilation.

No tool behavior changed in this release.

## Previous 0.2.27 Notes

This release adds a read-only semantic diff for Unity YAML/text assets, closing
the last useful Locus-inspired asset review idea in UniBridge's own MCP
architecture.

`UniBridge_AssetIntelligence Action=SemanticDiff` compares two text-like Unity
assets such as `.prefab`, `.unity`, `.mat`, `.controller`, `.asset`, or `.meta`
files and returns an AI-readable review instead of only a noisy raw text diff.

SemanticDiff reports:

- YAML document created/deleted/modified counts;
- Unity class/fileID document changes;
- changed serialized properties with before/after values and line numbers;
- GUID reference deltas with resolved asset paths when Unity can resolve them;
- `m_Script` reference changes as a separate high-signal section;
- risk summary for script reference, GUID, component list, hierarchy, transform,
  sorting, and broad document changes;
- bounded line diff hunks when the files are small enough for an exact LCS pass.

Examples:

`UniBridge_AssetIntelligence Action=SemanticDiff Path=Assets/.../Before.prefab OtherPath=Assets/.../After.prefab`

`UniBridge_AssetIntelligence Action=SemanticDiff Paths=[Assets/.../old.unity,Assets/.../new.unity] IncludeLineDiff=true MaxDiffItems=120`

New searchable aliases include:

- `semantic_asset_diff`;
- `asset_semantic_diff`;
- `yaml_semantic_diff`;
- `unity_yaml_diff`;
- `prefab_semantic_diff`;
- `asset_diff`;
- `semantic_diff`.

The previous 0.2.26 release finished the Locus-inspired agent-UX pass by polishing the
guidance that a fresh AI agent sees before it starts editing a Unity project.
It does not add another large tool; it makes the existing guide and snapshot
tools clearer, safer, and more directly actionable.

`UniBridge_ToolGuide Action=Workflow Topic=agent_playbook` now returns a
compact operating protocol:

- read-before-modify rules;
- scene/prefab/editor scope awareness;
- safe execution defaults for WorkSession, BatchActions, ScopedEdit, and
  EditorSnapshot;
- a verification ladder from serialized state checks through console,
  editor-event, visual, runtime, profiler, and WorkSession review checks.

`UniBridge_ContextSnapshot` `agentBrief` now includes:

- `operatingProtocol` for what to inspect before writes;
- `verificationLadder` for what to run after work;
- risk-specific hints for compiling/importing, Prefab Stage, large scenes,
  hierarchy truncation, and existing console diagnostics;
- recommended next calls that point fresh agents at the playbook and full
  hierarchy export when scene scale requires it.

`UniBridge_DomainCatalog` now adds `riskControls` to domain summaries and
details. A new agent inspecting domains such as `Scripts`, `Assets`,
`LargeScenes`, `Rendering`, `UI`, `RuntimeDebug`, `EditorOps`, or `Safety`
can see what to read before editing, how to execute safely, how to verify, and
what red flags to watch.

New searchable aliases include:

- `agent_playbook`;
- `playbook`;
- `read_before_modify`;
- `verification_ladder`;
- `risk_controls`;
- `operating_protocol`.

The previous 0.2.25 release added profiler marker hierarchy export to
`UniBridge_RuntimeProfiler`. Agents can now ask for a bounded one-frame or
short-window view of hot profiler marker paths instead of only seeing counters
such as frame time, GC, batches, or memory.

`UniBridge_RuntimeProfiler Action=Hierarchy` reports:

- selected profiler categories, marker filters, and recorder availability;
- top marker paths by total/max sampled time;
- category summaries;
- a compact synthetic hierarchy built from marker category/name paths;
- optional saved full JSON under `Library/UniBridge/RuntimeProfiler`.

Example:

`UniBridge_RuntimeProfiler Action=Hierarchy SampleFrames=1 MaxHierarchySamples=40 ProfilerCategories=[Internal,Scripts,Render,Physics]`

The workflow is intentionally read-only and based on stable Unity
`ProfilerRecorder` marker handles. It is a marker hierarchy / top-sample view
for AI triage, not the Unity Profiler Window's complete call tree.

Useful filters:

- `MarkerFilters=[Update,Render,Physics]`;
- `ExcludeMarkerFilters=[EditorLoop]`;
- `MaxProfilerMarkers=160`;
- `MaxHierarchyDepth=5`;
- `MinHierarchySampleMs=0.05`;
- `IncludeCounters=true` when non-time counters are also useful.

Discoverability was updated across `BatchActions`, `Discover`, `ToolGuide`, and
`DomainCatalog` with aliases such as `profiler_hierarchy`,
`marker_hierarchy`, `runtime_hierarchy`, `frame_export`, `frame_hierarchy`,
`top_markers`, and `hot_markers`.

The previous 0.2.24 release added script change-impact preflight to
`UniBridge_ScriptIntelligence`. Agents can now compare a current C# script with
`ProposedSource` or `ProposedPath` before applying a larger edit and see what
the change is likely to affect.

`UniBridge_ScriptIntelligence Action=ChangeImpact` reports:

- syntax diagnostics for the proposed source;
- added, removed, changed, and possible-renamed types;
- added, removed, changed, and possible-renamed fields, properties, and
  methods with declaring type context;
- public API risks;
- inspector/serialized field risks;
- Unity callback risks;
- source line/character deltas and expected refresh/compile/domain-reload
  boundaries;
- suggested follow-up calls for `CodeUsages`, `MemberUsages`,
  `ValidateScript`, refresh, compile, reload wait, and diagnostics.

Example:

`UniBridge_ScriptIntelligence Action=ChangeImpact Path=Assets/.../Player.cs ProposedPath=Assets/.../Player.candidate.cs`

The workflow is intentionally read-only. It does not hot reload, patch, or
write source files. Use it before SHA/precondition text edits when the edit
could rename public API, serialized fields, or Unity callbacks.

Discoverability was updated across `BatchActions`, `Discover`, `ToolGuide`, and
`DomainCatalog` with aliases such as `change_impact`,
`script_change_impact`, `script_preflight`, `hot_diff`, `reload_risk`, and
`api_change_impact`.

Use the four script impact scans together:

- `Action=Usages IncludeUsageLocations=true` for prefab/scene YAML references to
  a script GUID;
- `Action=MemberUsages Member=<methodOrField>` for UnityEvent, AnimationEvent,
  and serialized field references in Unity assets;
- `Action=CodeUsages Member=<methodOrField>` for C# caller/type references;
- `Action=ChangeImpact ProposedSource=<candidateSource>` for proposed source
  shape and reload-risk preflight before applying the edit.

The previous 0.2.23 release added C# caller/type impact scanning to
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
