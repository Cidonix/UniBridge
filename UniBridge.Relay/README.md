# UniBridge Relay Source

This folder contains the clean MCP-only UniBridge relay rewrite.

The distributable Unity package ships compiled binaries from `com.cidonix.unibridge/RelayApp~`. Relay source lives beside the package in this repository so it can be versioned without being included in Patreon/package archives.

Current scope:

- `--mcp` stdio server for MCP-compatible clients.
- UniBridge discovery under `~/.unibridge/mcp/connections`.
- Stable Unity project targeting with `--project-id <id>` / `UNIBRIDGE_PROJECT_ID`.
- Named pipe transport on Windows.
- Unix domain socket transport on Linux and macOS.

Intentionally removed:

- cloud assistant relay mode;
- WebSocket chat relay;
- remote provider launcher;
- credential and preferences bus;
- Unity cloud analytics posting from the executable.

Build from repo root:

```powershell
dotnet publish .\UniBridge.Relay\UniBridge.Relay.csproj -c Release -r win-x64 --self-contained true
dotnet publish .\UniBridge.Relay\UniBridge.Relay.csproj -c Release -r linux-x64 --self-contained true
dotnet publish .\UniBridge.Relay\UniBridge.Relay.csproj -c Release -r osx-x64 --self-contained true
dotnet publish .\UniBridge.Relay\UniBridge.Relay.csproj -c Release -r osx-arm64 --self-contained true
```

The current project file builds a self-contained single-file executable. NativeAOT is a planned hardening step, but it requires the Visual Studio Desktop Development for C++ workload on Windows.

Output executables:

- `unibridge_relay_win.exe`
- `unibridge_relay_linux`
- `unibridge_relay_mac_x64`
- `unibridge_relay_mac_arm64`

Windows can cross-build the Linux and macOS artifacts. Runtime testing still needs real or virtual target systems.
