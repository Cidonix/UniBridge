# UniBridge Legacy Unity Compatibility Adapter

This dependency-free adapter connects projects on Unity 5.3.2 or later in the
5.3.x line, Unity 5.6.x, Unity 2017.4.x, or Unity 2018.4.x to the current
UniBridge relay and MCP protocol. It is
installed as ordinary Editor scripts because these Editors cannot load the
main Unity 6 UPM package. The `Unity2017` directory name is retained for package
path compatibility; the runtime reports the exact `Unity53Legacy`, `Unity56Legacy`,
`Unity2017Legacy`, or `Unity2018Legacy` profile.

## Install

1. Copy `Assets/UniBridgeLegacy` from this directory into the target project's
   `Assets` folder.
2. Open the project and wait for Editor script compilation to finish.
3. Confirm the Console contains
   `[UniBridge Legacy] MCP bridge started for <project>`.
4. Use `Tools > UniBridge Legacy > Show Status` to read the generated project
   ID, compatibility profile, pipe, and discovery path.
5. Add a project-scoped MCP entry that launches the normal UniBridge relay:

```toml
[mcp_servers.unibridge_legacy_project]
command = "C:\\Users\\<user>\\.unibridge\\relay\\unibridge_relay_win.exe"
args = ["--mcp", "--project-id", "<project-id>", "--project-path", "C:\\path\\to\\project", "--name", "unibridge_legacy_project"]
enabled = true
```

6. Reload the MCP client only when this is a newly added server configuration.
   An already running relay can discover a restarted Unity Editor without
   restarting the client.

Each project receives its own persistent GUID-backed identity under
`ProjectSettings/UniBridge/project.json`. The relay validates the selected
project root on every tool call, so a similarly named project cannot be edited
silently.

Unity 5.3.2f1 uses the same nine tools and guarded write/capture workflow.
`ExposedReference` serialized properties are available only on Unity 5.6 and
newer; ordinary Unity object references work on all supported profiles.
Compatibility with a recovered game's original private Editor revision is a
separate question from bridge API compatibility. Do not upgrade the project or
discard its Library cache to install this adapter.

Close modal Editor dialogs, including `Tools > UniBridge Legacy > Show Status`,
before making MCP calls. Unity pauses its Editor update loop while these
dialogs are open; the pipe can stay connected while main-thread commands wait.

`ManageEditor` actions `WaitForReady`, `WaitIdle`, and `WaitForReadyAfterReload`
are instantaneous readiness probes in the legacy adapter. They explicitly
return `waitSupported=false` and do not consume `TimeoutMs` or block the main
thread. A busy Editor, or Play Mode when `RequireNotPlaying=true`, returns a
failure. A ready probe confirms current readiness and does not prove modern
compilation health. `GetState` remains a successful observation even when busy.

## Tool Surface

The relay exposes `_server_info` plus these Unity tools:

- `UniBridge_Discover`
- `UniBridge_ContextSnapshot`
- `UniBridge_ManageEditor`
- `UniBridge_ReadConsole`
- `UniBridge_ManageScene`
- `UniBridge_SceneObjectView`
- `UniBridge_CaptureView`
- `UniBridge_ManageGameObject`
- `UniBridge_AssetIntelligence`

This is a focused compatibility profile rather than full Unity 6 feature
parity. It supports scene lifecycle, hierarchy/object inspection, asset search,
Console messages captured after startup, guarded scene/component writes, and
visual PNG capture. UI authoring, script intelligence/editing, batches,
WorkSessions, runtime probes, profiling, and other modern tools require the
main package.

## Write Safety

Scene and component mutations are Edit Mode-only by default and fail while the
Editor is compiling or importing. Calls can use `DryRun=true`, record an Undo
group, return before/after data, and verify serialized property readback before
reporting success. `UniBridge_ManageEditor` exposes `Undo` and `Redo` for MCP
verification.

Prefer live `ObjectId`, `ComponentObjectId`, and `SceneId` values returned by
read tools. Serialized Unity object references accept only live object identity
or asset GUID plus local file ID; arbitrary asset-path strings are not accepted
as object references. Ambiguous components fail closed instead of selecting the
first matching type.

Play Mode writes require explicit `AllowPlayModeWrite=true` and remain
ephemeral. Closing a dirty scene requires explicit
`DiscardUnsavedChanges=true`.

## Visual Capture

`UniBridge_CaptureView` supports:

- `CaptureSceneView`: immediate render of the current Scene View camera;
- `CaptureGameCamera`: immediate render from a live Camera selected by
  `ComponentObjectId` or `ObjectId`;
- `CaptureGameView`: exact post-render Game View PNG, queued asynchronously;
- `GetCaptureStatus`: poll an exact Game View `CaptureId` until `complete` or
  `failed`;
- `ListCaptures` and `ListCameras`.

Captures are written below `Library/UniBridge/Captures`, never imported as
assets, and return an absolute path, dimensions, byte count, and SHA-256 when
complete. Scene View renders do not include Unity window chrome or IMGUI
gizmos. Camera renders do not include Screen Space Overlay UI. Full Editor
chrome, Inspector, and Console pixels require a separate OS-level screen
capture; Unity 5.6 has no public cross-platform Editor API for that.

## Transport Safety

On Windows the adapter deliberately bypasses Unity 5.6's old-Mono
`System.IO.Pipes` implementation and owns a Win32 named-pipe handle directly.
This prevents duplex request echo and native heap corruption observed while
Mono released disconnected server streams. The host uses managed command
completion instead of per-call native wait handles, emits diagnostics on the
Unity main thread, accepts up to 32 concurrent relay clients, closes native
listeners during domain unload, and starts again on the first ready Editor
update after a script reload. Relay `1.1.0-build.19` also ignores command echo
frames defensively. Keep these ownership and response-framing boundaries intact
when changing the transport.

## Console Scope

Legacy Unity does not expose the same Console APIs as current Editors. The
adapter therefore summarizes messages received after the adapter initialized.
Existing Console entries from before initialization are not part of its
diagnostic snapshot.

## Compatibility Regression

Run the bundled compiler matrix from PowerShell:

```powershell
& '.\Tools~\Test-LegacyAdapterCompile.ps1'
```

It compiles the exact adapter sources as C# 4 / .NET 2.0 against every installed
Unity 5.3.x, 5.6.x, 2017.4.x, and 2018.4.x Editor. The compiler receives the
matching Unity version defines so both sides of compatibility guards are tested.
You can select exact Editors with `-UnityVersion 5.3.2f1,5.6.7f1,2017.4.6f1`.
