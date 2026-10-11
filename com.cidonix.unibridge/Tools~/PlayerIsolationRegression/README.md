# Player isolation qualification

Run from an elevated Windows terminal. The runner creates a fresh disposable project, embeds an exact package snapshot and builds a real Windows x64 Mono development Player through the installed Editor. It never opens or changes an authored game project. Use a new --project path for every attempt; previous failures and receipts are retained.

```
python run_player_isolation.py --package <package-root> --project <fresh-owned-project> --editor <Unity.exe> --expect-excluded
```

The default before-change control expects MCP.Runtime and Tracing in the Player compilation graph. With --expect-excluded the test requires all four bridge assemblies in the Editor graph, none in the Player graph and none in the built Player payload. The build must succeed with zero build errors. driver-result.json records actual assembly sets, package version and asmdef hashes. The runner checks administrator token before launching a hidden child Editor. No CoreCLR qualification or package dependency removal is performed.

All package services are internal Editor implementation. Their existing namespace and assembly identity stay compatible with Editor consumers; the Runtime suffix is a historic assembly name and does not promise a Player-facing public API.
