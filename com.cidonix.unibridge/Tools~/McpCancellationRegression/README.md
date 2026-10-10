# MCP request cancellation regression

Run from an Administrator Windows PowerShell terminal in the UniBridge repository:

```powershell
$newtonsoft = (Get-ChildItem 'H:\Repos\UnityRepos\UniBridge_Test_Project\Library\PackageCache' -Filter Newtonsoft.Json.dll -Recurse | Select-Object -First 1).FullName
python -B .\com.cidonix.unibridge\Tools~\McpCancellationRegression\run_mcp_cancellation_regression.py --newtonsoft $newtonsoft --report .\Temp\McpCancellation\editor-regression.json
```

Choose a fresh report path for every execution; previous reports are never replaced.
The runner requires the installed .NET 10 SDK and an existing Newtonsoft.Json DLL.

The modern Bridge handshake advertises `requestCancellation` with `version: 1`
and `mode: "transport-scoped"`. A relay sends the one-way `cancel_command` control
only when this capability is advertised. Older and legacy bridges are not qualified
by this suite and do not acquire cancellation support by using a newer relay.

The suite compiles the complete current `ToolExecutionScheduler.cs` and extracts
unchanged `Bridge.CancelRequest`, `Bridge.ReleaseRequestCancellation`,
`McpToolRegistry.ExecuteToolAsync`, and `McpToolRegistry.ExecuteToolCoreAsync` bodies.
It also compiles the current ExecutionStatus producer, observation action schemas,
schema helpers, JSON helper, response helper, and output validator. The serialized
Snapshot/Recent/ReapStale producers are checked against their actual closed schemas
while operations are queued, started, canceled, and deferred. Negative cases reject
wrong cancellation field types and additional undeclared operation fields.
It verifies cancellation through the real registry consumer, queued work admission,
reader/observer cleanup, and preservation of the actual result of a begun mutation.
Transport/request isolation and concurrent cancellation versus disposal are covered.

Tool discovery, output projection, project guards, logging, and Editor yield are
deterministic fixtures. WorkSession review, policy catalogs, and schema branches
outside ExecutionStatus are excluded. This suite does not run a native Unity Editor
or establish runtime behavior in Unity. The MCP smoke suite separately verifies
the installed package in the authorized test Editor. Reports include source hashes
and these qualification boundaries.
