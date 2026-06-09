# UniBridge AI Feedback from Domovyk

## 2026-06-02 - False-positive script validation warning

Project: `H:\Repos\UnityRepos\Domovyk`
Unity: `6000.4.9f1`
UniBridge: `0.2.4`
AI/client: Codex working on Domovyk gameplay/dialogue systems.

### What I did

I edited Domovyk dialogue scripts and validated them through `UniBridge_ValidateScript`:

- `Assets/_Domovyk/Scripts/Dialogue/DialogueBubble.cs`
- `Assets/_Domovyk/Scripts/Dialogue/DialogueManager.cs`
- `Assets/_Domovyk/Scripts/Dialogue/TalkController.cs`

### What I saw

Validation succeeded for all files. However, `UniBridge_ValidateScript` returned this warning for `DialogueManager.cs` and `TalkController.cs`:

```text
line: 0, col: 0
String concatenation in Update() can cause garbage collection issues
```

For `TalkController.cs`, the current `Update()` method contains no string operations at all; it only updates `Direction` from character flip/mirror state. The warning therefore looks like a false positive or a rule that is not correctly bound to a concrete code location.

### Why this matters

For an AI coding workflow, inaccurate warnings create noise and reduce trust in validation output. Because the diagnostic is reported at `line: 0, col: 0`, I cannot map it back to actionable source code or confidently decide whether it is safe to ignore.

### Suggested improvement

- Only emit the "String concatenation in Update()" warning when actual string concatenation/interpolation/allocation-like string formatting is found inside an `Update()` method body.
- Return the exact source line/column where the issue was detected.
- If the analyzer cannot determine a precise location, mark the diagnostic as low-confidence or include a reason field so agents know it may be heuristic.

## 2026-06-02 - CompilationFinished event wait after RefreshAssets

Project: `H:\Repos\UnityRepos\Domovyk`
Unity: `6000.4.9f1`
UniBridge: `0.2.4`
AI/client: Codex working on Domovyk gameplay/dialogue systems.

### What I did

After editing dialogue scripts, I called `UniBridge_BatchActions` with the editor action `RefreshAssets`. The command succeeded and reported that Unity started compiling.

Then I waited for `CompilationFinished` through `UniBridge_WaitForEvent`.

### What I saw

`UniBridge_WaitForEvent` timed out after 30 seconds, but the returned editor state already had:

```text
editorReady: true
isCompiling: false
isUpdating: false
```

The project console also had no critical errors afterwards.

### Why this matters

For an AI workflow, it is useful to know whether compilation actually finished cleanly or whether a wait timed out because the event was missed. A timeout with `isCompiling: false` is workable, but it forces the agent to infer success from editor state instead of receiving a clear completion signal.

### Suggested improvement

- If `WaitForEvent(CompilationFinished)` starts after compilation has already finished, return success with a `missedButAlreadyComplete` or similar field instead of a timeout.
- Include the last known compilation result summary when possible: errors/exceptions/warnings count and timestamp.
- Consider making `RefreshAssets` optionally wait for import/compilation completion in one call, so agents can use a single reliable operation before validating scenes or scripts.
