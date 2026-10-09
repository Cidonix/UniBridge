# Bridge JSON isolation and lifecycle regression

Run these suites from an Administrator Windows terminal. The offline runner
compiles unchanged production files or explicitly listed unchanged method/model
bodies. Its provenance records source hashes and every adapter limitation.

```powershell
python -B .\com.cidonix.unibridge\Tools~\BridgeIsolationRegression\run_bridge_isolation_regression.py --newtonsoft <Unity-Newtonsoft.Json.dll> --goldens .\com.cidonix.unibridge\Tools~\BridgeIsolationRegression\GoldenJson --report <new-report.json>
```

The thirteen pinned golden JSON files were captured before the isolated serializer
migration. Byte equality tests preserve protocol, persisted session/write receipt,
snapshot and parameter contracts. Tests exercise throwing and behavior-changing
global factories, caller-supplied settings, actual tracing/configuration producers,
supplied Unity ID converter serializers, and simultaneous serialization operations.
Lifecycle cases inject deterministic listener/discovery/scheduler faults around the
unchanged Bridge methods. They use real cancellation tokens, tasks and semaphores;
they do not acquire Windows native pipe handles or reproduce a native Mono crash.

The native runner requires explicit opt-in for an owner-authorized test project.
Never run it in an authored host as a substitute for the approved test target.
Install and qualify the candidate first, then pass its immutable package freeze:

```powershell
python -B .\com.cidonix.unibridge\Tools~\BridgeIsolationRegression\run_live_bridge_json_regression.py --allow-test-fixture --project <approved-Test-root> --project-id <approved-Test-id> --relay <installed-relay.exe> --expected-version <qualified-candidate-version> --freeze <package-freeze.json> --report <new-native-report.json>
```

Native qualification imports an exact UUID-owned temporary C# source and metadata
pairs. Menu commands install a throwing global JSON factory for at most 55 seconds
of Editor update time. The fixture restores the exact original delegate explicitly,
before assembly reload, on quit, or at its deadline. A concurrent replacement is
preserved and fails qualification. Ordinary short-lived relay clients then exercise
the actual eager catalog, main-thread context, typed Editor parameters and nested
UI Toolkit validation responses. No additional Bridge instance is constructed.

The runner preserves earlier failed attempts, records mutation dispatches without
replaying them, checks all original author/dependency file hashes and Editor state,
and removes only the unchanged declared temporary source/meta pairs. An unknown or
changed fixture remains for inspection. It never saves author scenes, clears the
Console, enters Play Mode or makes a player build. Global restoration and complete
fixture cleanup must both pass before downstream delivery.
