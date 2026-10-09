# Restore safety qualification

Run all commands from an **Administrator** terminal. The PowerShell wrapper
and the live Python runners refuse non-elevated execution. Child processes inherit
the elevated terminal token.

```powershell
& .\com.cidonix.unibridge\Tools~\RestoreSafetyRegression\Run-RestoreSafetyRegression.ps1 -Report .\Temp\restore-safety.json -NewtonsoftDll 'C:\Program Files\Unity\Hub\Editor\6000.6.5f1\Editor\Data\Managed\Newtonsoft.Json.dll'
```

The standalone suite compiles the whole unchanged production `WorkSession.cs`,
its non-semantic partials, and `Response.cs`. It extracts the unchanged loaded
dirty-state guard from the semantic partial, and also extracts unchanged
EditorSnapshot restore/planning/scene methods and data models. The only
substituted logic is Unity's Editor substrate, semantic review, and unrelated
Editor UI restoration. Real files, hashes, session JSON, receipts, preview
tokens, capture bytes and read-only write failures exercise production code.

Cases cover owned/unowned file changes, ownership chains and sticky concurrent
conflicts, expected payload hashes, expired/replayed tokens, one-use guarded
plans, capture corruption, incomplete baseline/current scans, metadata
independence, dirty loaded scenes/assets/Prefab Stages, old session refusal,
retained recovery bytes and truthful partial/blocked outcomes.
Snapshot cases cover identical dirty scenes, untitled scenes, failed/still-dirty
saves, safe additive opening, callbacks, false open/close/active results, dirty
Prefab Stages, invalid targets and Editor busy state. This is deterministic
substrate evidence; it does not prove live Unity behavior.

Filesystem fixtures are uniquely created below
`%LOCALAPPDATA%\UniBridgeRegression` with ownership manifests and retained for
inspection. They deliberately avoid a `Temp` path segment because production
scanning excludes such paths. The runner records production source/dependency
SHA-256 values, every response, fixture roots and build/runtime logs. No author
project is accessed or launched. `-SourceRef HEAD -Filter 'unowned added file'`
provides a focused old-source control for the former unsafe ownership behavior.

After root has synchronized the candidate package and confirmed a healthy
authorized Editor, use the live suite:

```powershell
$testProject = '<approved UniBridge_Test_Project path>'
$testProjectId = '<verified project ID>'
$relay = Join-Path $env:USERPROFILE '.unibridge\relay\unibridge_relay_win_1.1.0-build.21.exe'
python -B .\com.cidonix.unibridge\Tools~\RestoreSafetyRegression\run_live_restore_safety_regression.py --project $testProject --project-id $testProjectId --relay $relay --report .\Temp\restore-safety-live.json --expected-version 0.2.60
```

The live suite verifies project identity and version, refuses an active author
WorkSession, creates one unique text-only Assets fixture folder, and qualifies
the published MCP two-phase write/guarded revert contract. It never saves or
changes author scenes or selection. Cleanup removes only the manifest's
unchanged fixture bytes and metadata, preserves any mismatch, and checks final
compilation and author state.

`SnapshotLiveFixture.cs` is excluded from standalone compilation and normally
ignored by Unity under Tools~. Root can install a guarded transient copy in the
test project's UniBridge Editor assembly, compile it, then add
`--snapshot-tool UniBridge_RestoreSnapshotProbe --snapshot-editor-pid <verified PID>`
to the live command. That tool
uses additive uniquely owned scene fixtures and restores original scene handles,
dirty state, active scene and selection. Root must remove the transient tool
after qualification, verify compilation and confirm package parity.
The snapshot fixture and its dialog helper intentionally enforce the approved
test-project root in their guard. Verify that guard before installation; these
fixtures are unsuitable for author projects.

The real denied scene save can display Unity's native "Moving file failed"
dialog. The elevated helper checks the exact Editor PID, project/run-specific
owned source/destination body and Cancel button, then cancels that owned failure
once. It does not handle other dialogs. Its raw audit is saved in the report,
and the combined gate requires all six snapshot cases without skips.

If only the transient snapshot fixture changes after WorkSession has passed,
qualify that phase independently and retain the original run reports:

```powershell
python -B .\com.cidonix.unibridge\Tools~\RestoreSafetyRegression\run_live_snapshot_safety_regression.py --project $testProject --project-id $testProjectId --relay $relay --editor-pid <verified PID> --report .\Temp\restore-safety-snapshot-live-qualified.json
```

This snapshot-only wrapper also refuses an active author WorkSession and checks
the original scene/selection identity, scene disk hashes and final compilation.

Automatic writer integration needs its own live check in addition to the public
two-phase API checks:

```powershell
python -B .\com.cidonix.unibridge\Tools~\RestoreSafetyRegression\run_live_script_ownership_regression.py --project $testProject --project-id $testProjectId --relay $relay --report .\Temp\restore-safety-script-writer-live.json
```

It imports and compiles a uniquely owned plain C# baseline before starting the
session, updates it through `UniBridge_Script`, checks the automatic known-payload
receipt across reload, previews/reverts the exact baseline, verifies compilation,
ends the session and deletes only its unchanged source/metadata fixture. Original
scenes, selection and Console history are preserved.
