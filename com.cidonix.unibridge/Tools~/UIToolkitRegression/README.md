# UI Toolkit validation and import regression

These checks compile the entire unchanged `ManageUIToolkit.cs`, `UIToolkitValidation.cs`, `UIToolkitAssetWriter.cs`, and response formatter. The standalone suite supplies a controllable Unity API substrate to test failed imports, null or wrong asset types, imported error/warning flags, exceptions, concurrent edits, file locks, and late metadata. It does not emulate Unity's semantic USS importer.

Run all commands from an **Administrator** PowerShell. No Editor process is launched by these runners.

```powershell
& '.\com.cidonix.unibridge\Tools~\UIToolkitRegression\Run-UIToolkitRegression.ps1' `
  -Report '.\Temp\uitoolkit-standalone.json' `
  -NewtonsoftDll '<Unity Editor>\Editor\Data\Managed\Newtonsoft.Json.dll'
```

Standalone filesystem fixtures are new unique fake projects under `%LOCALAPPDATA%\UniBridgeRegression\UIToolkit_<run id>`. Their ownership manifest and recovery bytes are retained for inspection. Production source hashes, dependency hash, and runtime log are included in the report. `-Filter` selects a named scenario for investigation; a filtered result is not a complete qualification. `-ToolSourceRef` selects an older tool only, with the current pure helpers; reports explicitly record that mixed provenance.

The live runner uses actual Unity imports for balanced but invalid selectors and values. It requires the candidate package to be installed and fully compiled in the designated test Editor first. Coordinate project access so no other agent mutates the Editor during this run.

```powershell
python -B '.\com.cidonix.unibridge\Tools~\UIToolkitRegression\run_live_uitoolkit_regression.py' `
  --project '<test project>' `
  --project-id '<verified test project id>' `
  --relay '<UniBridge relay executable>' `
  --expected-version '<installed candidate package version>' `
  --report '.\Temp\uitoolkit-live.json'
```

Live gates include wrong-extension refusal both in preview and execution, invalid XML/DTD/namespace without writes, valid UTF-8 source and actual typed asset import, existing-source preview, missing UXML Style/Template dependencies and undefined Instance, exact restoration after actual import errors, default warning rejection, and explicit warning acceptance with `completed_with_warnings`. Structural validation reports `semanticValidation=not_run`; actual import evidence has `semanticValidationScope=unity_import` and `instantiationValidation=not_run`. Custom-element construction and visual-tree instantiation are separate stages; an unknown custom element is not assumed to be an import-time error. Output-schema coverage is a separate qualification.

The live runner rejects an active author WorkSession. It writes only under the fresh unique `Assets/__UniBridgeUIToolkit_<run id>` folder, with source hashes registered before each write and metadata observed only alongside unchanged fixture source. The ownership manifest resides under `Library/UniBridge/UIToolkitRegression`. Author Assets, ProjectSettings, package manifests, scenes, selection, and Prefab Stage state are checked before and afterward. It never saves or discards an author scene, enters Play mode, or clears Console history.

Cleanup first verifies the complete fixture child set and all paired source/metadata hashes. A negative import candidate's known bytes are authorized in the manifest before the write, alongside its baseline, so an unexpected native acceptance can still clean only known task-owned bytes with the original metadata. An unrecognized or independently changed source/metadata prevents cleanup and remains available for inspection. Removal uses specific declared files and a nonrecursive folder removal. Import recovery copies under `Library/UniBridge/UIToolkitWrites` and reports remain available. Actual importer warnings/errors from negative fixtures remain in Console history; final compilation health is checked independently.
