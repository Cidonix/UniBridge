# Command replay regression

This focused development suite exercises command delivery and query-only
recovery after a command has already changed state but its response is lost.
It runs the current production relay source against an isolated fake Unity
named pipe. It also runs the production command journal with storage delegates
when a Unity Newtonsoft dependency is supplied.

Requirements: Python and .NET 10 SDK. The journal tests additionally need the
`Newtonsoft.Json.dll` shipped by the test project's installed Unity package.

```powershell
& .\com.cidonix.unibridge\Tools~\CommandReplayRegression\Run-CommandReplayRegression.ps1 `
  -ReportPath C:\Temp\unibridge-command-replay-regression.json `
  -NewtonsoftDll H:\Repos\UnityRepos\UniBridge_Test_Project\Library\PackageCache\com.unity.nuget.newtonsoft-json@4dfd81071c64\Runtime\Newtonsoft.Json.dll
```

Use the actual dependency path from the currently installed package. Omit
`-NewtonsoftDll` to run only the transport scenarios. Use `-Filter` for a
particular case name. Add `-IncludeTimeout` to exercise the relay's real
120-second response timeout without changing its production timeout constant.

The fake peer commits a counted side effect before closing its transport,
then records every original command and recovery query. Assertions cover:

- Legacy or unclassified writes never execute again after a lost response.
- Modern writes recover completed success or error through `recover_command`.
- In-flight work is polled; unknown/conflicting results cannot restart work.
- Only a whole-tool `ReadOnly` or `Observer` policy with the explicit
  `replaySafe=true` certificate permits replay, with the original request ID,
  project, Editor PID, and Editor epoch. Read-only hints and mixed tools such
  as Console `Clear` and EditorEvents `MarkSession` cannot grant permission.
- Simultaneously interrupted commands keep distinct operations and results.
- Partial batches, cancelled calls, failed recovery queries, and response
  timeouts preserve uncertain outcomes and never replay the original write.
- `UniBridge_CommandStatus` only queries the original operation.
- The journal persists admission before execution, rejects mismatched payloads,
  shares in-flight completion, and retains terminal responses across reload.
- A started operation interrupted by reload remains unknown and cannot execute
  again; stale-domain completion cannot overwrite restored evidence.
- Storage failure, corruption, retention, and individual/aggregate response
  bounds preserve honest results and admission evidence.
- Certified reads use in-memory deduplication, do not write SessionState, and
  cannot evict durable write evidence under cache pressure.

Each run compiles an isolated copy of `UniBridge.Relay/*.cs` into a unique
temporary directory. Exactly one asserted discovery-directory line is omitted
from that copy so the suite cannot enumerate or remove the user's real Editor
discovery files. The delivery/recovery code and command journal are otherwise
the current production source. Temporary discovery entries point only to the
suite's fake pipe and are removed after verifying ownership of their paths.

The suite never contacts a real Editor, changes a Unity project or scene, or
clears the user's Console. Its JSON report separates every gate and retains
the recovered/uncertain protocol response as evidence. Real Editor execution,
SessionState persistence through an actual domain reload, and package syncing
are separate live qualification steps.
