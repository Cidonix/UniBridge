# Editor wait outcome regression

This isolated suite checks that an unsuccessful Editor readiness wait cannot be
reported as successful completion by a checkpoint or relay recovery wrapper.
It does not connect to a real Unity Editor, edit its discovery files, or replay
project mutations.

Prerequisites: Python 3, .NET 10 SDK, and the `Newtonsoft.Json.dll` supplied by
Unity's `com.unity.nuget.newtonsoft-json` package. Pass the runtime DLL, rather
than its AOT copy.

```powershell
./Run-EditorWaitRegression.ps1 `
    -Report ./editor-wait-results.json `
    -NewtonsoftDll /path/to/Newtonsoft.Json.dll
```

The equivalent Python invocation is:

```text
python run_editor_wait_regression.py --report editor-wait-results.json --newtonsoft-dll /path/to/Newtonsoft.Json.dll
```

Use `--source-ref <git-ref>` / `-SourceRef <git-ref>` to run the same assertions
against the previous production sources and preserve a failing baseline report.
Use `--filter <case-name-substring>` / `-Filter <case-name-substring>` to inspect
one family of cases. A failing case returns a nonzero process exit code. Reports
contain full failure evidence and hashes identifying the production sources.

The suite compiles the production methods `WaitForReady`,
`WaitForReadyAfterReload`, `ReloadCheckpoint`, their readiness predicate and
update poll, and the real `Response` formatter. Only Unity APIs and heavyweight
diagnostic collectors are substituted. Busy Editor scenarios consume the real
100 ms production timeout; positive cases cover an immediately ready Editor,
readiness reached within the budget, and explicitly allowed Play Mode. A failed
wait must retain its error and avoid diagnostic collection. Checkpoint tests
retain the wait result and count the refresh side effect to detect replay.

The exact production `WaitForPlayModeState` method is additionally compiled with
an update seam that can raise `OperationCanceledException`, so a domain reload
boundary can be tested without terminating a real Unity domain. Both entering
and leaving Play Mode must fail when interrupted or timed out and succeed when
their target state is observed.

Legacy tests compile the real `ManageEditor`, state builder, result formatter,
and argument readers with fake Unity APIs. Every supported readiness alias must
fail when compiling, importing, or violating `RequireNotPlaying`, while a busy
`GetState` remains a successful observation. These are instantaneous legacy
probes; they must not claim to consume `TimeoutMs` or wait inline.

Relay tests compile the current relay sources and invoke the real private
compilation, refresh, and Play Mode recovery wrappers against an owned named
pipe. They cover top-level errors, nested failed waits, contradictory structured
content, malformed success flags, missing or false readiness, and unconfirmed
observed Play Mode state. Compatibility fallback is permitted for an explicitly
unknown action; a timeout or handler failure must not trigger it. The fixture
also covers the explicit unsupported-action wording of older legacy adapters.
Readiness booleans must be actual booleans rather than strings. The fixture
audits all commands to ensure recovery sends only waits and state/diagnostic
queries, and checks that an exhausted timeout budget does not receive a fresh
one-second allowance. Three actual 200 ms recovery budgets are exercised with
responses delayed to 600 ms; the relay must return a timeout before those late
responses arrive.

MCP wire cases invoke the real `HandleToolsCallAsync` and inspect its emitted
JSON-RPC result. Failed or contradictory nested wait results must set
`isError=true`, and the top-level text and structured payloads must not claim
success. Healthy waiting retains normal MCP success and sends its command once.
Ordinary legacy transport responses without a tool success boolean remain
compatible. Intentional outer success for a batch with a failed optional step
in its steps array is preserved.

The runner creates a uniquely named build directory and fake discovery endpoint
under the system temporary directory. It removes only those owned directories.
Live Unity compilation, Editor behavior, and client MCP error envelopes require
separate qualification in an explicitly authorized test project.
