# Package management qualification

`UniBridge_ManagePackages` has exactly six actions. List, Inspect, Search and
ListSamples use read-only scheduling. Add uses CompileReload; ImportSample uses
Mutating. The whole tool is neither read-only nor replay-safe. Add accepts only a
registry identifier plus an exact semantic version. It never retries a native
request. A timeout reports that native work may continue; a successful native Add
still requires a separate Editor readiness observation after registration/reload.

Sample imports refuse existing destinations and destination metadata, interactive
unitypackage samples, source GUID conflicts, path escapes and reparse ancestors.
The source scan is bounded. Import uses the native Sample API without
OverridePreviousImports. Success requires exact copied bytes and AssetDatabase
GUID resolution. There is no Force/Remove/Embed/Unembed operation.

Portable qualification, in an Administrator terminal:

```powershell
python -B .\com.cidonix.unibridge\Tools~\PackageManagementRegression\run_package_management_regression.py --repo H:/Repos/Cidonix/UniBridge --newtonsoft <Unity-Newtonsoft.Json.dll> --report <new-core-report.json>
python -B .\com.cidonix.unibridge\Tools~\PackageManagementRegression\run_package_sdk_regression.py --repo H:/Repos/Cidonix/UniBridge --newtonsoft <Unity-Newtonsoft.Json.dll> --report <new-sdk-report.json>
python -B .\com.cidonix.unibridge\Tools~\PackageManagementRegression\qualify_package_contracts.py --repo H:/Repos/Cidonix/UniBridge --candidate <candidate-root> --core-report <core-report.json> --build <owned-core-build-directory> --validator-path <jsonschema4.25.1-dependencies> --report <new-contract-report.json>
python -B .\com.cidonix.unibridge\Tools~\PackageManagementRegression\local_package_registry.py self-test --work <fresh-owned-work-directory>
```

The core suite links whole production service, guard, DTOs, params, input tool,
scheduler, output rules, ActionOutputSchema, McpJson, mathematical serializer,
OutputContractProvider, CoreActionOutputContracts, ActionSelectorRules and
ActionOutputValidation. Native request/clock/editor/Sample.Import interfaces are
deterministic test adapters. The required projectContext is an explicit test
adapter; unrelated tool-family builders throw if reached. Tests verify the actual
source_action_contract provider and completion validator rather than a fallback.
Filesystem bytes/GUID ownership checks are real. SDK compilation links
the native Client/Request/Sample types from the installed 2020.3, 2022.3 and 6.6
Editors; no Editor is launched. Legacy 5.x/2017/2018 standalone adapters have no
ManagePackages tool, and this work does not claim legacy UPM or Unity CoreCLR
support.

`qualify_package_contracts.py` uses an independent Draft2020-12 validator and
counterproofs, then compiles both complete schedulers to compare existing-tool
policy/annotation rows. An existing Python environment with jsonschema is needed;
the driver does not download dependencies. The core build directory is printed
by the first runner and retains source/fixture hashes.

New-tool policy tests include conflicting Action/action properties in both orders,
case-insensitive keys, ignored nulls and trimmed enum strings. Policy uses the same
last non-null typed Action assignment as Json.NET; malformed selectors remain
exclusive. Existing tools retain their previous scheduling behavior.

Native gate, after the root task promotes and installs the tested package:

1. Create a fresh fixture with `local_package_registry.py create --work <fresh>`,
   then run `serve --work <same> --ready-file <fresh-ready.json> --port 0` as an
   Administrator process. Keep its owned session alive. It binds only 127.0.0.1,
   serves the owned exact-version tarball and has no external dependencies.
2. Preserve the current Test project's manifests and whole author Assets /
   ProjectSettings baseline. Compute fresh SHA256 values for manifest.json and
   packages-lock.json. Call `prepare-manifest --fixture <fixture.json> --project
   H:/Repos/UnityRepos/UniBridge_Test_Project --registry-url <ready.url>
   --expected-manifest-sha <fresh-sha> --expected-lock-sha <fresh-sha> --receipt
   <fresh-receipt.json>`. It adds only the unique fixture's scoped registry.
3. Execute `run_native_package_probe.py --repo H:/Repos/Cidonix/UniBridge --project
   H:/Repos/UnityRepos/UniBridge_Test_Project --fixture <fixture.json> --report
   <fresh-native-report.json>`. It uses ordinary MCP, issues Add and the positive
   ImportSample once, then separately checks readiness, installed exact Registry
   version, actual copied bytes/GUID and a negative repeated import. It never
   launches Unity or prepares/restores manifests itself. A later repeat against
   the same fixture is deliberately blocked because it is already installed.
4. Capture the owned imported sample directory and generated metadata hashes.
   Move only the unchanged owned sample files to quarantine; preserve all original
   author hashes and any unrelated concurrent changes. Call `restore-manifest
   --receipt <receipt.json>`, which refuses anything beyond the known scoped
   registry/dependency/lock fixture delta and restores original bytes exactly.
   Wait again for actual native readiness after the package removal, then stop
   only the owned server session. No author scene save is needed.

The native gate remains necessary; portable/SDK results are not a live Unity
installation or sample import result. Domovyk and Corgi manifest dependencies
outside UniBridge are outside this fixture driver's scope.

The owned loopback fixture is unsigned. Interactive Editors can show native
`Missing Signature` or `Importing a scoped registry` informational windows; a
modal stops Editor updates and MCP readiness replies even after Add completed.
Review the fixture in Package Manager and dismiss informational notices before
the bounded readiness deadline. Do not disable signature validation, change
trust settings, or replay Add/ImportSample to work around a blocked Editor.

The public SDK flags distinguish overriding prior samples from hiding an
interactive unitypackage window. We deliberately omit the overriding option and
reject interactive imports before calling the native API.
[Unity Sample.ImportOptions](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/PackageManager.UI.Sample.ImportOptions.html)
