# UniBridge Unity 2017 Compatibility Adapter

This adapter connects projects locked to Unity 2017.4 to the current UniBridge
relay and MCP protocol. It is dependency-free and is installed as ordinary
Editor scripts because Unity 2017 cannot load the main Unity 6 UPM package.

## Install

1. Copy `Assets/UniBridgeLegacy` from this directory into the target project's
   `Assets` folder.
2. Open the project in Unity and wait for Editor script compilation to finish.
3. Confirm the Console contains
   `[UniBridge Legacy] MCP bridge started for <project>`.
4. Use `Tools > UniBridge Legacy > Show Status` to read the generated project
   ID, pipe, and discovery path.
5. Add a project-scoped MCP entry that launches the normal UniBridge relay:

```toml
[mcp_servers.unibridge_legacy_project]
command = "C:\\Users\\<user>\\.unibridge\\relay\\unibridge_relay_win.exe"
args = ["--mcp", "--project-id", "<project-id>", "--project-path", "C:\\path\\to\\project", "--name", "unibridge_legacy_project"]
enabled = true
```

6. Restart the AI agent or MCP client itself. Restarting Unity alone does not
   reload the client's MCP server list.

## Tool Surface

The relay exposes `_server_info` plus these Unity tools:

- `UniBridge_Discover`
- `UniBridge_ContextSnapshot`
- `UniBridge_ManageEditor`
- `UniBridge_ReadConsole`
- `UniBridge_ManageScene`
- `UniBridge_SceneObjectView`
- `UniBridge_ManageGameObject`
- `UniBridge_AssetIntelligence`

This is a compatibility profile, not full feature parity. UI authoring,
captures, script intelligence/editing, batches, WorkSessions, runtime probes,
profiling, and other modern tools require the main Unity 6 package.

## Console Scope

Unity 2017 does not expose the same Console APIs as current Editors. The legacy
adapter therefore summarizes messages received after the adapter initialized.
Existing Console entries from before initialization are not part of its
diagnostic snapshot.
