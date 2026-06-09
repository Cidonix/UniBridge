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
  tilemaps, input actions, timeline, audio, VFX, and workflow recipes.
- `UniBridge_Discover`, a stable read-only ping/workflow entry point for new
  MCP clients and Codex sessions.
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
