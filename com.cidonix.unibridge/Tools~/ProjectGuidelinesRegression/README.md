# Project guidelines regression

Run from an elevated terminal on Windows. Use fresh report paths: previous evidence is never overwritten. The source runner requires Python 3 and the .NET 10 SDK. It links the installed package's actual reader, DTOs, serializer, action validator and settings capture; 24 reader, 15 closed-contract and 11 settings cases run without Unity. The settings suite uses an explicit Editor database seam that retains recently deleted GUID paths and typed objects, verifies current type/GUID/physical file identity, and checks truthful unresolved URI output. Assignment/live capture derive GUID from the actual typed object; path reuse cannot rebind it, and a live reference takes precedence over an incorrect generated GUID cache. Only imported main TextAssets are supported: a TextAsset subasset cannot make the reader return its parent asset's file or sibling text, even when its GUID resolves to an actual main TextAsset. The serialization seam omits irrelevant Unity math converters; no native Unity behavior is inferred from these gates.

```powershell
python -B com.cidonix.unibridge/Tools~/ProjectGuidelinesRegression/run_project_guidelines_regression.py --report H:/Evidence/guidelines-source-01.json
```

`--package-root` supports another package checkout. `--source-overlay` explicitly qualifies candidate sources over that package and records every linked file's actual path/hash. Neither source tree is written; owned build directories and fixture files are retained beside the report or in the system temp directory.

For real MCP qualification, use an already open target Editor containing the package. Manually install `Templates/UniBridgeProjectGuidelinesNativeFixture.cs.txt` as a `.cs` file in a unique, owned `Assets/.../Editor/` folder and wait for compilation/readiness. The fixture stages only its own typed TextAsset and in-memory configuration. It never saves project guidelines settings or writes root AGENTS/context documents, restores only its own still-matching state, and retains unexpected concurrent changes.

The native runner requires `jsonschema` (Draft 2020-12). `--validator-path` can name a Python-version-compatible installed dependency directory. It negotiates modern MCP, checks exact project identity, validates registered output schema plus text/structured parity, and exercises 10 positive/negative cases: unavailable defaults, typed source identity/content, metadata-only Status, character/byte caps, invalid budgets, GUID-preserving moves, explicit context opt-in, and deleted GUIDs. Actual Editor execution is required for this gate.

```powershell
python -B com.cidonix.unibridge/Tools~/ProjectGuidelinesRegression/run_native_guidelines_mcp.py --project H:/Repos/UnityRepos/UniBridge_Test_Project --relay H:/Repos/Cidonix/UniBridge/com.cidonix.unibridge/RelayApp~/unibridge_relay_win.exe --report H:/Evidence/guidelines-native-01.json
```

The runner never launches Editor. Relay processes inherit the verified administrator token. Setup, move and deletion calls are never replayed. Require `passed=true`, all 10 cases, `cleanupVerified=true` and saved configuration preservation. Keep the first error and cleanup error distinct. Then remove only the exact owned native fixture and confirm subsequent Editor readiness. A native failure leaves useful evidence and does not qualify the release.
