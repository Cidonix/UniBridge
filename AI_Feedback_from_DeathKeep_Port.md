# AI Feedback From Death Keep Port

Created: 2026-05-17

This file collects observations from the AI agent working on the `Death Keep`
Unity port, so UniBridge feedback can stay close to the UniBridge repository
without polluting the Death Keep project context.

## Source Project

- AI working context: `H:\Repos\UnityRepos\Death Keep`
- Unity project name: `Death Keep`
- Unity version observed through UniBridge: `6000.4.7f1`
- Active build target: `StandaloneWindows64`
- Render pipeline: URP `17.4.0`
- UniBridge package in Death Keep: embedded `com.cidonix.unibridge` `0.1.0`
- Correct MCP endpoint for this work: `unibridge_death_keep`
- Other endpoint also visible during tool discovery: `unibridge_uni_bridge_test_project`

## What The AI Was Doing

The Death Keep project is a fan-porting effort for the old PC game `Death Keep`.
Before UniBridge was added, the AI extracted and cataloged the original CD image:

- mounted/extracted `H:\Projects\Death Keep\DKEEP_V1_0.iso`;
- created a research copy under
  `H:\Repos\UnityRepos\Death Keep\_Research\OriginalMedia\DKEEP_V1_0`;
- identified open data formats:
  - `.LVL` text level data;
  - `.MON` text monster definitions;
  - `.PCX` paletted images;
  - `.PCZ` multi-frame PCX wrapper;
  - `.WAV` audio;
  - `.SMK` Smacker videos;
- wrote a local research/conversion script in the Death Keep project and
  generated CSV reports plus converted PNG/MP4 assets.

After UniBridge was added, the user asked the AI to:

- use direct filesystem operations only for raw file transfer and research files;
- use UniBridge MCP for programming workflows, scene work, Unity objects,
  components, ScriptableObjects, materials, import settings, prefabs, captures,
  and Unity Console diagnostics;
- observe UniBridge behavior while doing the port, because logging is enabled
  and UniBridge itself is under active test.

## Initial UniBridge Check

The AI used `tool_search` and found two UniBridge MCP namespaces:

- `mcp__unibridge_death_keep_85fbdadb__`
- `mcp__unibridge_uni_bridge_test_project_ae4e3233__`

The AI then explicitly called `UniBridge_ContextSnapshot` on the Death Keep
endpoint with:

- `Depth=Standard`
- console included
- hierarchy included
- selection included
- assets included
- tools included
- build settings and project settings included

Observed result:

- call succeeded quickly;
- project identity was correct: `Death Keep`;
- project root was correct: `H:/Repos/UnityRepos/Death Keep`;
- package root listed `com.cidonix.unibridge` as embedded;
- console summary reported `0` logs, warnings, errors, exceptions, and asserts;
- assets summary showed the project was still mostly a fresh Unity template;
- tool registry reported `59` enabled tools;
- snapshot guidance gave useful next-step hints such as using
  `UniBridge_ReadConsole` and `UniBridge_CaptureView`.

## Positive Observations

### ContextSnapshot Is Very Useful

`UniBridge_ContextSnapshot` is a strong first call. It gave the AI enough
orientation to verify project identity, Unity version, render pipeline, package
state, console health, and available tool groups in one request.

Why this matters:

- It reduces accidental edits in the wrong Unity project.
- It gives enough project state to plan Unity-side work without doing many
  smaller discovery calls.
- It makes console health visible before any changes.

### BatchActions Defaults Look Sensible

The exposed safety model is good:

- dry-run defaults to true;
- validation happens before execution;
- rollback and Undo support are available;
- script text editing is excluded from generic batch execution.

Why this matters:

- The AI can plan and validate scene/asset operations before mutating Unity.
- It lowers risk when generating complex porting workflows, such as creating
  import folders, ScriptableObjects, preview scenes, and prefabs.

### Console Tooling Is Well Positioned

`UniBridge_ReadConsole` exposes markers, summaries, groups, timelines, and
details. This matches the user's test goal well because the AI can check
console state before and after UniBridge operations.

Why this matters:

- It creates a repeatable debugging loop for bridge behavior.
- It makes it easier to separate Unity compile/import errors from UniBridge
  tool errors.

## Suggestions

### 1. Make The Active Project Endpoint Harder To Confuse

Observation:

Tool discovery exposed both the Death Keep endpoint and the UniBridge test
project endpoint. The AI correctly chose `unibridge_death_keep`, but this is an
easy place to make a mistake when multiple Unity projects are open.

Suggestion:

- Add a very explicit `active/canonical project` hint in tool discovery metadata
  or in the tool namespace description.
- Consider including the Unity project name, project root, and project id in
  every namespace description shown to the AI.
- If multiple UniBridge endpoints are available, expose a compact
  `UniBridge_ListProjects` or `UniBridge_SelectProject` style read-only helper.

Why:

The current namespace names are descriptive, but still require the AI to infer
which endpoint belongs to the user's current request. A stronger project
identity banner would reduce cross-project edits.

Suggested acceptance check:

- With two Unity projects open, an AI can identify the intended project from
  tool metadata alone before calling any mutating tool.

### 2. Include A Short Non-Truncated Summary Before Large Snapshot Payloads

Observation:

`ContextSnapshot` returned valuable data, but the response is large and can be
truncated in the client display. The essential facts were present, but not all
sections were equally accessible after truncation.

Suggestion:

- Always include a small `summary` object near the top with:
  - project name;
  - project root;
  - Unity version;
  - active scene path;
  - console counts;
  - tool count;
  - package version;
  - whether the payload is truncated;
  - recommended next tool.
- Keep the detailed sections as they are.

Why:

Large context is useful, but the AI needs a stable compact orientation block
that survives truncation and can be quoted or reasoned about reliably.

Suggested acceptance check:

- Even if the detailed payload is truncated, the first 50 lines contain all
  project identity and safety-critical state.

### 3. Add Optional Post-Action Console Delta To BatchActions

Observation:

The user specifically wants UniBridge behavior tested through console logging.
The AI can manually call `MarkSession` and `DiagnosticSummary`, but this is an
extra protocol habit the AI has to remember.

Suggestion:

- Add optional parameters to `UniBridge_BatchActions`, for example:
  - `ConsoleMarkerLabel`
  - `IncludeConsoleDelta`
  - `IncludeEditorEventDelta`
- When enabled, the batch result would include logs/errors/warnings emitted
  during the batch.

Why:

This would make every mutating workflow self-auditing. It would also help catch
cases where a Unity operation reports success but logs warnings, import errors,
or delayed compile issues.

Suggested acceptance check:

- A batch that creates/imports an asset can return operation results plus the
  console entries that occurred during that exact batch.

### 4. Put Project Identity In Mutating Tool Results

Observation:

`ContextSnapshot` confirms project identity well. For future mutating calls, it
would be useful if every result also echoed the project name/root/id.

Suggestion:

- For mutating tools and `BatchActions`, include a compact `project` block:
  - name;
  - id;
  - root;
  - Unity editor instance id if available.

Why:

This provides a safety trail in logs and chat history. If an AI accidentally
uses the wrong endpoint, the mismatch becomes visible immediately in the result.

Suggested acceptance check:

- A scene mutation result can be audited later and tied to the exact Unity
  project instance where it executed.

### 5. Expose A Recommended AI Workflow Preset

Observation:

The AI now follows a manual policy:

1. `ContextSnapshot`
2. `ReadConsole MarkSession`
3. dry-run `BatchActions`
4. execute `BatchActions`
5. `ReadConsole DiagnosticSummary`
6. capture/snapshot if visual changes matter

Suggestion:

- Add a `ToolGuide` workflow topic or a small preset named something like
  `safe_mutating_workflow`.
- It should tell agents exactly which calls to use before and after a mutation.

Why:

The pieces already exist. A named workflow would reduce drift between agents and
make UniBridge easier to evaluate consistently.

Suggested acceptance check:

- A new AI agent can ask `ToolGuide Workflow safe_mutating_workflow` and receive
  a concise, ordered checklist for safe Unity edits.

### 6. Consider A Lightweight Feedback/Observation Tool

Observation:

The user asked the Death Keep AI to write UniBridge observations into this
repository manually.

Suggestion:

- Optional future idea: add a UniBridge developer-facing helper that can append
  structured observations to a configured feedback file, including project id,
  tool call name, result status, console delta, and freeform note.

Why:

This would turn real porting sessions into structured dogfooding data without
requiring the AI to hand-maintain markdown formatting.

Suggested acceptance check:

- From any project using UniBridge, an AI can append a feedback note to the
  UniBridge repository with consistent metadata.

## Current Open Feedback Items

| ID | Priority | Status | Area | Summary |
| --- | --- | --- | --- | --- |
| DK-UB-001 | Medium | Open | Tool discovery | Make active/canonical project endpoint more explicit when multiple UniBridge endpoints are present. |
| DK-UB-002 | Medium | Open | ContextSnapshot | Add compact always-near-top summary that survives truncation. |
| DK-UB-003 | Medium | Open | BatchActions | Add optional console/editor-event delta to mutating batch results. |
| DK-UB-004 | Low | Open | Safety/audit | Echo project identity in mutating tool results. |
| DK-UB-005 | Low | Open | ToolGuide | Add a named safe mutating workflow preset for AI agents. |
| DK-UB-006 | Low | Open | Dogfooding | Consider structured feedback append support. |
| DK-UB-007 | Medium | Open | AssetDatabase refresh | When files are copied outside Unity, make `ManageAsset.Import` validation explain AssetDatabase staleness and suggest `RefreshAssets`. |
| DK-UB-008 | Medium | Open | Editor reload/reconnect | Make `RequestScriptCompilation`/domain reload connection loss clearer or self-healing from the AI perspective. |
| DK-UB-009 | Low | Open | Console markers | Preserve marker entries across reload/console clearing, or expose a stronger session cursor not dependent on console backlog. |
| DK-UB-010 | Low | Open | ScriptIntelligence | Include static/plain C# script assets in `Catalog`, or explain why they are omitted. |
| DK-UB-011 | High | Open | Script editing | `ScriptApplyEdits` appeared to apply changes even with `Preview=true`; clarify/fix preview semantics. |
| DK-UB-012 | Low | Open | Batch impact | Reduce over-inclusion of the active scene in likely asset paths for asset-only batches. |
| DK-UB-013 | Low | Open | JSON safety | Avoid non-standard JSON values such as `Infinity` in context snapshots. |
| DK-UB-014 | Medium | Open | Batch optional steps | Optional step validation errors can still make a `ScopedEdit`/batch roll back; optional failures should not poison batch-level validation totals. |
| DK-UB-015 | Medium | Open | GameObject resolution | Re-enabling an inactive GameObject in the same batch failed with both `SearchInactive=true` and `ById`; inactive-object resolution needs a reliable path. |
| DK-UB-016 | Medium | Open | Component workflow | `SetComponentProperty` did not trigger or wait for `OnValidate` rebuild behavior clearly; add a safe `InvokeComponentMethod` or context-menu method tool. |
| DK-UB-017 | Medium | Open | GameObject parenting | `ManageGameObject.Modify` accepted a target instance id but failed to resolve `Parent` when the parent was passed as an instance id. |
| DK-UB-018 | Medium | Open | Scene workflow | `ScopedEdit`/scene switching can leave extra scenes loaded additively; add an explicit unload/close scene action or clearer scope cleanup control. |
| DK-UB-019 | Medium | Open | Script editing | `ScriptApplyEdits` docs/schema advertise `anchor_replace`, but the tool returned `Unsupported text edit op: anchor_replace`. |
| DK-UB-020 | Low | Open | Script editing | `ApplyTextEdits` with `Options.refresh=immediate` successfully edited a script but returned `scheduledRefresh=false`; the agent still had to drive refresh/compile separately. |
| DK-UB-021 | Medium | Open | Editor reload/reconnect | `RequestScriptCompilation` again lost the MCP connection during domain reload, although Unity recovered and diagnostics were clean. |
| DK-UB-022 | Low | Open | Visual audit console scope | `VisualSceneAudit IncludeConsole=true` is useful, but it cannot currently scope console inspection to a marker such as `AfterMarkerId`. |

## Future Observation Log

Add new entries below this line as the Death Keep port uses UniBridge for real
Unity-side work.

### 2026-05-17 - Initial Death Keep Endpoint Smoke

- `UniBridge_ContextSnapshot` succeeded on `unibridge_death_keep`.
- No console errors/warnings/logs were present at snapshot time.
- The endpoint correctly identified `Death Keep` and the embedded package.
- The main practical risk noticed was cross-project confusion because the test
  project endpoint was also available in the same AI tool environment.

### 2026-05-18 - First Real Death Keep Port Slice

Project/client:

- AI working on `Death Keep`, Unity project root
  `H:\Repos\UnityRepos\Death Keep`.
- Unity version observed through UniBridge: `6000.4.7f1`.
- Endpoint used: `unibridge_death_keep_85fbdadb`.
- Work performed:
  - copied original `.LVL`/`.MON` text data and first `LEVEL00` PNG texture
    slice into `Assets/DeathKeep`;
  - configured texture importers through `UniBridge_BatchActions`;
  - created C# data/parser/runtime scripts through `UniBridge_CreateScript`;
  - created `Assets/DeathKeep/Scenes/DeathKeep_LEVEL00_Preview.unity`;
  - added a preview GameObject/component, generated `1026` level preview
    renderers from `LEVEL00`, and ran visual/console checks.

Observations:

- `ContextSnapshot` was fast and useful, but one earlier snapshot included
  `activeTool.handlePosition` fields as `Infinity`. Strict JSON parsers may
  reject this; consider serializing non-finite floats as `null` plus a warning.
- After files were copied into `Assets/DeathKeep` outside Unity,
  `UniBridge_BatchActions` dry-run for `ManageAsset.Import` reported
  `Asset not found at 'Assets/DeathKeep'`, while the impact block in the same
  result listed `Assets/DeathKeep` as an existing folder. Running
  `UniBridge_ManageEditor RefreshAssets` fixed it. Suggestion: when disk and
  AssetDatabase disagree, return a specific stale-AssetDatabase diagnostic and
  recommended `RefreshAssets` call.
- `RequestScriptCompilation` returned an MCP error:
  `Unity connection lost: Unity connection closed`. Unity itself stayed alive
  and UniBridge reconnected shortly after. This may simply be domain reload, but
  for agents it looks like a hard failure. Suggestion: provide a tool-level
  "domain reload/reconnecting" state, or make compile requests wait/retry through
  reload when possible.
- `ReadConsole MarkSession` worked initially. After compilation/domain reload,
  later `DiagnosticSummary` said the marker was known in session but the marker
  entry was no longer in current Console backlog, so it used fallback behavior.
  Suggestion: provide a persistent console cursor independent of Unity Console
  backlog entries, or document the current fallback in the marker result.
- `ScriptIntelligence Catalog` returned `DeathKeepLevelData`,
  `DeathKeepMonsterData`, and `DeathKeepLevelPreview`, but did not list static
  parser scripts (`DeathKeepLevelParser`, `DeathKeepMonsterParser`). The parser
  files existed as `MonoScript` assets and compiled. Suggestion: include static
  classes as `PlainCSharp` results, or add an omission reason/filter hint.
- I called `UniBridge_ScriptApplyEdits` with `Preview=true` while patching
  `DeathKeepLevelPreview`. It still applied the changes and reported
  `Applied 3 structured edit(s)` although the payload contained 4 edits and the
  anchor insertion was present afterward. Suggestion: make `Preview=true`
  strictly non-mutating, or rename/remove the parameter if it is not meant to
  dry-run. Also make applied edit counts match the input/result.
- `VisualSceneAudit IncludeConsole=true` was very useful. It caught thousands of
  warnings caused by my Unity script creating primitives directly inside
  `OnValidate`. I fixed the script by deferring rebuild through
  `EditorApplication.delayCall`, and a later console check had no errors or
  warnings.
- The warning stack samples attributed the warning file to UniBridge
  `ManageGameObject.cs:801`, because the component was being added through that
  tool. This is understandable, but for generated warnings it would help if the
  report also surfaced the user script type/method involved when Unity provides
  that context in the message.
- `BatchActions` impact often listed `Assets/Scenes/SampleScene.unity` as a
  likely touched asset for asset/importer/folder-only batches. This may be the
  active scene fallback, but it makes asset-only operations look like they touch
  the scene. Suggestion: only include active scene when the tool actually reads
  or mutates scene context, or label it as "active scene context, not expected
  mutation."
- The machine had multiple Unity processes open, including the UniBridge test
  project. The chosen endpoint still addressed the correct Death Keep project.
  Echoing Unity process id/window title/project id in mutating results would make
  this safer and easier to audit.

Outcome:

- UniBridge was able to perform the first real Unity porting pass end-to-end.
- No blocking issues remained after the deferred-rebuild fix.
- The most important issue from this pass is `ScriptApplyEdits Preview=true`
  appearing to mutate files.

### 2026-05-18 - LEVEL00 Mesh Geometry Pass

Project/client:

- AI working on `Death Keep`, Unity project root
  `H:\Repos\UnityRepos\Death Keep`.
- Endpoint used: `unibridge_death_keep_85fbdadb`.
- Work performed:
  - replaced `DeathKeepLevelPreview` with a mesh-based generator using decoded
    `#CUBES` wall bitmasks and directional face texture slots;
  - added `Assets/DeathKeep/Scripts/Editor/DeathKeepLevelPreviewEditorTools.cs`
    with menu item `DeathKeep/Rebuild Level Previews`;
  - rebuilt `Assets/DeathKeep/Scenes/DeathKeep_LEVEL00_Preview.unity`;
  - configured perspective camera, interior point lights, player torch, ambient
    light, and fog;
  - ran visual and console checks.

Observations:

- `UniBridge_Script update` with a SHA precondition worked well for replacing a
  full script and scheduled the asset refresh as expected.
- `RequestScriptCompilation` again reported `Unity connection lost: Unity
  connection closed` during domain reload. `WaitForReady` immediately succeeded
  afterward and console diagnostics were clean. This repeats DK-UB-008.
- `SetComponentProperty` successfully wrote new public fields on
  `DeathKeepLevelPreview`, but the generated scene did not rebuild afterward.
  If this is expected because the tool bypasses normal inspector validation, it
  would help to say so in the result and expose a direct method/context-menu
  invocation tool. I had to add an editor menu item solely to call `Rebuild()`.
- In `ScopedEdit`, two optional `game_object Delete` steps for missing lights
  were marked optional and skipped, but their validation errors still made the
  batch-level result fail and roll back all previous successful steps. Optional
  failures should not contribute to fatal validation totals or rollback triggers
  unless the caller asks for strict optional validation.
- Attempting to disable and then re-enable `DeathKeep_LEVEL00_Preview` in one
  batch failed. The re-enable step could not find the inactive object with
  `SearchInactive=true`, and a follow-up attempt using the instance id with
  `SearchMethod=ById` also failed validation. This makes active-state toggles
  risky for agents unless there is a reliable inactive-object handle.
- `ManageMenuItem Exists` and `Execute` worked cleanly for the custom
  `DeathKeep/Rebuild Level Previews` menu item and became the practical
  workaround for method invocation.
- `VisualSceneAudit` remained useful. It passed the rebuilt scene and reported
  no material or console errors. The remaining low-color-diversity warning is
  plausible for this particular dark, heavily repeated dungeon texture set.

Outcome:

- UniBridge completed the mesh-geometry pass successfully after using the custom
  editor menu workaround.
- New suggested improvements from this pass are DK-UB-014 through DK-UB-016.

### 2026-05-18 - LEVEL01 Ice Cave + Playtest Player Pass

Project/client:

- AI working on `Death Keep`, Unity project root
  `H:\Repos\UnityRepos\Death Keep`.
- Endpoint used: `unibridge_death_keep_85fbdadb`.
- Work performed:
  - imported and configured all `LEVEL01` ice-cave texture tiles;
  - updated `DeathKeepLevelPreview` to generate multi-Y-layer geometry;
  - duplicated/configured `DeathKeep_LEVEL01_IceCave_Preview.unity`;
  - added a temporary `DeathKeepPlayerController` using the new Unity Input
    System directly through `Keyboard.current` and `Mouse.current`;
  - added a `DK Playtest Player`, parented the preview camera and torch under
    it, ran play/capture checks, then cleaned up the loaded scenes.

Observations:

- `ManageGameObject.Modify` accepted numeric instance ids for `Target`, but the
  same operation failed when `Parent` was passed as the numeric instance id of
  `DK Playtest Player`: `Parent specified ('-65968') but not found.` Passing the
  parent by name worked. Suggestion: make all object-reference fields share the
  same resolver, or document that `Parent` is name/path-only.
- A `ScopedEdit`/scene workflow left both `DeathKeep_LEVEL00_Preview.unity` and
  `DeathKeep_LEVEL01_IceCave_Preview.unity` loaded. The active scene was
  correct, but the user noticed the extra scene in the hierarchy. I fixed it by
  saving LEVEL01 and calling `ManageScene Load` for LEVEL01, which reopened it
  as the only loaded scene. Suggestion: expose an explicit `Unload`/`Close`
  scene action in `ManageScene`, and/or have `ScopedEdit` clearly report when it
  leaves extra scenes loaded.
- `ManageScene Load` successfully resolved the situation by loading only the
  requested scene, but the current `ManageScene` tool description does not state
  whether `Load` is single-mode or additive. Suggestion: return/load mode in the
  result and add `LoadMode=Single|Additive` to the schema.

Outcome:

- LEVEL01 is now the active single loaded scene after cleanup.
- The playtest setup compiled and was saved in the LEVEL01 scene.
- New suggested improvements from this pass are DK-UB-017 and DK-UB-018.

### 2026-05-18 - LEVEL01 Ghidra Floor/Drop Geometry Pass

Project/client:

- AI working on `Death Keep`, Unity project root
  `H:\Repos\UnityRepos\Death Keep`.
- Endpoint used: `unibridge_death_keep_85fbdadb`.
- Work performed:
  - used Ghidra findings from `FUN_00433e70` to update
    `DeathKeepLevelPreview` floor geometry;
  - added triangular floor clips for floor types `2..5`;
  - added ramp/sloped floors for floor types `6..21`;
  - changed wall auto-generation so original `#SPACES` entries without floor
    bits do not automatically become fake walls;
  - rebuilt and saved
    `Assets/DeathKeep/Scenes/DeathKeep_LEVEL01_IceCave_Preview.unity`;
  - captured the start view and a temporary probe view near the expected drop.

Observations:

- `UniBridge_ScriptApplyEdits` rejected an edit with
  `Unsupported text edit op: anchor_replace`, even though the exposed tool
  description lists `anchor_replace` among supported operations. I recovered by
  using `UniBridge_ApplyTextEdits`, which worked for the same script.
- `UniBridge_ApplyTextEdits` successfully applied seven coordinated edits and
  produced a SHA, but with `Options.refresh=immediate` it still reported
  `scheduledRefresh=false`. A later explicit editor refresh/compile step was
  needed for confidence.
- `UniBridge_ManageEditor RequestScriptCompilation` again disconnected with
  `Unity connection lost: Unity connection closed` during compilation/domain
  reload. `WaitForReady` immediately succeeded afterward, and compilation
  diagnostics had no errors or warnings. This reinforces DK-UB-008/DK-UB-021.
- `VisualSceneAudit IncludeConsole=true` was again helpful, but it inspected the
  whole console backlog. I tried to pass `AfterMarkerId`, but that is not in the
  tool schema and the audit result showed `marker: null`. Suggestion: either add
  marker filtering to the audit console path or reject unknown console-scope
  parameters so the agent does not think the audit was scoped.
- `ManageRendering CreateCamera`, `CaptureView CaptureGameCamera`, and
  `ManageGameObject Delete` worked cleanly for a temporary probe camera workflow.
  The scene became dirty as expected, and `ManageEditor SaveAll` saved cleanly
  after deleting the temporary camera.

Outcome:

- The pass completed with clean compile diagnostics and no console
  warnings/errors/exceptions after the rebuild marker.
- New suggested improvements from this pass are DK-UB-019 through DK-UB-022.

### 2026-05-18 - LEVEL01 Geometry Regression Correction Pass

Project/client:

- AI working on `Death Keep`, Unity project root
  `H:\Repos\UnityRepos\Death Keep`.
- Endpoint used: `unibridge_death_keep_85fbdadb`.
- Work performed:
  - corrected a bad wall-generation assumption in
    `DeathKeepLevelPreview`;
  - added diagonal wall bands for clipped floor cells;
  - rebuilt and saved
    `Assets/DeathKeep/Scenes/DeathKeep_LEVEL01_IceCave_Preview.unity`;
  - used a temporary probe sphere plus `CaptureAroundObject` to inspect the
    suspected drop area, then deleted the probe and saved the scene.

Observations:

- `UniBridge_ApplyTextEdits` worked well for a coordinated multi-edit patch
  guarded by SHA. However, I requested `Options.refresh=none` and the result
  still reported `scheduledRefresh=true`. Please clarify whether script edits
  always schedule a Unity refresh regardless of this option, or make the result
  explain why the option was overridden.
- `RequestScriptCompilation` again disconnected during domain reload with
  `Unity connection lost: Unity connection closed`. `WaitForReady` immediately
  succeeded afterward and diagnostics were clean. This is the same pattern as
  DK-UB-008/DK-UB-021 and remains the most disruptive workflow issue.
- `ReadConsole MarkSession` created a marker, but after the reload the later
  `DiagnosticSummary` reported `markerEntryFound=false` and fell back to the
  current backlog while still knowing the marker id from session state. The
  fallback is useful, but the result should make it even clearer that the
  summary is not strictly "after marker" anymore.
- `ManageGameObject Create` + `CaptureView CaptureAroundObject` +
  `ManageGameObject Delete` worked cleanly for a temporary probe workflow.
  The scene remained dirty after deletion, which is expected in Unity; `SaveAll`
  then saved cleanly.

Outcome:

- The correction pass completed with clean `ValidateScript`, clean retained
  compilation diagnostics, and no console warnings/errors/exceptions.
- New suggested improvements from this pass are DK-UB-023 through DK-UB-025.
