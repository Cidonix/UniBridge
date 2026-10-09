# Command latency regression

Measure actual `UniBridge_ContextSnapshot` and `UniBridge_ManageEditor` / `GetState`
round-trip time in a live, explicitly approved test project. This is a development
runner under `Tools~`, excluded from production Unity assemblies.

The Windows controller observes one externally prepared window state per run.
It reads process/window identity, focus, placement and input state. It contains
no native window mutation bindings and does not activate, minimize or restore
windows. Prepare the exact Editor state through the supported desktop UI before
starting, or use the optional acknowledgment barrier described below.

## Run

Use an elevated PowerShell with Python 3 available. Open the test project with
the same frozen package sources as this runner's package. The current runner
requires the target package to be embedded at `Packages/com.cidonix.unibridge`;
other package layouts fail closed. Confirm the exact live Editor PID, project
identity from `ProjectSettings/UniBridge/project.json`, Unity image/version and
package version. Every path argument must be absolute. The identity file must
already be valid; the runner never creates or repairs it.

```powershell
# Fill these values from the explicitly approved test target and current build.
& '<package-root>/Tools~/CommandLatencyRegression/Run-CommandLatencyRegression.ps1' `
  -AllowTestProject `
  -Project '<absolute approved test project root>' `
  -ProjectId '<project identity GUID>' `
  -UnityExe '<absolute matching Unity Editor executable>' `
  -UnityVersion '<exact ProjectVersion.txt Editor version>' `
  -Relay '<absolute qualified relay executable>' `
  -EditorPid <actual matching Editor PID> `
  -PackageVersion '<installed package version>' `
  -SingleState foreground `
  -ReportName baseline-foreground-01
```

Run `background` and `minimized` separately after preparing those states, each
with a fresh report name. A minimized target must also be in the background.
Keep the desktop and project idle during measurement; input, unexpected focus
or placement changes, modal dialogs and compilation stop the run. The runner
preserves outside steering without attempting correction.

Reports are confined to the approved target's
`Library/AgentValidation/CommandLatencyRegression/<fresh-report-name>/`.
An existing report directory is refused. No reports are written under `Tools~`.
The report includes every attempt, request/response, relay output, failure,
startup timing and preservation audit. Retain failed runs; there are no automatic
retries or selective reruns. A nonzero exit means failed or incomplete evidence.

## Measurement and preservation

Defaults are three warm repetitions and three fresh short-lived relay sessions
in the selected state, yielding twelve measured requests. One warm relay is
retained within that state only. Each session separately records process spawn,
`initialize`, actual `tools/list` startup and connected `_server_info` identity.
Those startup/cached responses do not count as Unity handler execution.

Each measured pair follows a two-second idle interval. The minimal snapshot
excludes console, hierarchy, assets and other optional expensive sections.
Actual handler evidence requires a successful matched project context, unique
snapshot ID, recent creation time, advancing Editor clock and ready state.
Typed `GetState` fields retain their actual PascalCase wire names.

RTT runs from immediately before the request write to receipt of its matching
stdout response, excluding controller checkpoint writes. It includes transport,
Editor pumping, scheduling, handler work and serialization. Scheduler metadata
starts after pump dispatch; this runner does not isolate wake-up time or prove
an exclusive cause. A single-state run needs separately qualified foreground
controls before a background comparison. The default comparison threshold is
1000 ms; a timeout is retained as a failed attempt, not fabricated success.

Before and after, the runner hashes all files under `Assets`, `Packages` and
`ProjectSettings`, and hashes both shipping and installed command-pump sources.
It compares loaded/active scenes, dirty flags, Prefab Stage and full selection.
Reparse points, inaccessible files, source mismatch or changing files are
refused. A healthy run performs final snapshot/selection checks. After a failed
or pending request, it makes no further Unity calls and reports incomplete
author-state evidence. It closes or, if necessary, terminates only its own relay
children; it never terminates the Editor. It never saves scenes, clears Console,
enters Play Mode, reloads scripts or changes authored files.

Defaults: request timeout 8 seconds, initialize/catalog timeout 15 seconds and
whole-run budget 180 seconds. Explicit bounded options support warm repeats
1–5, short sessions 1–4, idle 0.5–10 seconds, request timeout 1–20 seconds,
initialize timeout 1–30 seconds and total budget 30–300 seconds. Sleeps are at
most 50 ms. Large file inventories consume the overall budget; a deadline failure
must be retained rather than hidden by adjusting a running test.

## Optional externally prepared window barrier

`-ExternalWindowReady` / `--external-window-ready` is an explicit owner opt-in.
After the author-file inventory the runner atomically publishes `window-ready.json`
in its fresh report directory, including a per-run token, requested state,
Editor PID and target root/ID. An authorized external controller prepares that
exact Editor window, then atomically publishes `window-go.json` with **exactly**:

```json
{"token":"<ready token>","state":"<ready state>","editorPid":123}
```

Use the actual PID from `window-ready.json`; `123` is only an example. Write the
complete JSON to a sibling temporary file, then rename it to `window-go.json`.
The runner waits at most 60 seconds and also respects the total budget. Wrong,
partial or stale acknowledgments fail. The barrier grants no UI control to the
runner; it observes the externally prepared state before any relay launch.

## Offline recorded qualification

`run_recorded_latency_regression.py` validates retained successful report
envelopes through this runner's pure parsing/identity/freshness/summary functions.
It imports the module without entering `main`, and never creates a native target
or launches a relay. Supply each recorded report explicitly; timestamps are
checked against its matching recorded stdout receive time.

```powershell
python '<package-root>/Tools~/CommandLatencyRegression/run_recorded_latency_regression.py' `
  --recorded-report '<absolute retained foreground performance-report.json>' `
  --recorded-report '<absolute retained background performance-report.json>' `
  --recorded-report '<absolute retained minimized performance-report.json>' `
  --output '<absolute new offline qualification evidence JSON>'
```

Offline replay qualifies compatibility with recorded evidence. It does not claim
a new live run or replace actual measurements after changing the installed
package. Machine-specific paths, IDs, versions and recorded project payloads are
not shipped with this runner.
