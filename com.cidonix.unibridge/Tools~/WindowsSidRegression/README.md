# Windows process-token SID regression

This Windows-only qualification suite compiles the complete, unchanged production
`NamedPipeListener`, `NamedPipeTransport`, and their interfaces. Only Unity logging
is substituted. Native cases use real Windows tokens, pipes and security APIs;
the suite does not connect to an Editor, change account configuration or touch
authored Unity assets.

Requires Python 3 and the .NET 10 SDK. When Unity is installed through Unity Hub,
the suite also compiles against its Mono .NET 4.8 reference assemblies and executes
the same checks using Unity's Mono runtime. Supply `-UnityEditor` to choose an
installation, or `-SkipUnityMono` to run only the standalone .NET checks.

The shipped Mono executables can be x86 even when the Unity Editor embeds an
x64 Mono runtime. The standalone Mono branch qualifies that shipped runtime;
it does not emulate the embedded Editor JIT. Actual Editor reload, MCP and
native pipe checks are recorded separately. .NET native checks use the SDK's
process architecture.

```powershell
./Run-WindowsSidRegression.ps1 -Report ./windows-sid-results.json
```

The equivalent Python command is:

```text
python run_windows_sid_regression.py --report windows-sid-results.json
```

The real native cases verify that the resolved SID equals the independent managed
Windows identity oracle, that anonymous impersonation of the calling thread safely
returns null or the process SID, that the actual created pipe DACL grants full
access only to the process user and SYSTEM, that a same-user client exchanges
binary and UTF-8 messages through the production listener/transport, and that
2,000 SID queries plus 100 pipe create/dispose cycles do not leak native handles.
The impersonation exists only in the harness process and is reverted in `finally`.
Anonymous impersonation can deny `OpenProcessToken`; it does not establish
Microsoft Account compatibility and never causes the test to expand token rights.

The `unmappable-account-name` variant substitutes only a historical
`LookupAccountName` declaration with `ERROR_NONE_MAPPED` (1332), retaining the real
username, token, SID conversion and pipe APIs. It models account-name resolution
failure. Current production has no such lookup declaration, so real token-based
SID retrieval, security inspection and roundtrip must still succeed.

The non-Windows conditional is compiled and must reject native pipe execution.
Unity Mono receives the independent .NET identity oracle because its
`WindowsIdentity.User` implementation is unavailable on some Editor runtimes.
The report retains source hashes, per-case results, compiler output and execution
errors. A failed case returns a nonzero process exit code.

The generated native failure fixture additionally exercises token opening,
buffer sizing/query validation, Unicode decoding, and resource cleanup after
each failed native call or exception. It also checks the existing owner/SYSTEM
fallback and security descriptor/attribute cleanup. Complete production method
bodies are preserved; only native API and allocation boundaries are substituted.

Use `-SourceRef <git-ref>` or `--source-ref <git-ref>` to run the same native
assertions against a previous revision. Generated API failure tests apply only
to the working tree implementation, so a historical baseline can run unchanged.
Passing local-account tests establishes native interoperability and independence
from username lookup; Microsoft Account, CloudAP and Windows Hello coverage still
requires a host using those account types.

An explicitly authorized live Editor can be inspected using
`-InspectPipe '\\.\pipe\the-exact-discovery-pipe'` or `--inspect-pipe`.
This separate mode connects one transient client, reads the actual pipe DACL and
closes only that client. It sends no protocol commands, but the Editor can log
the connection/disconnection. Obtain the exact path from the selected project's
current discovery record; never guess a pipe belonging to another Editor.

The runner creates uniquely named build directories beneath the system temporary
directory and removes only its own directories. Live Editor compilation and MCP
reconnection after a domain reload are qualified separately in the test project.
