# UniBridge MCP Smoke Regression

`Run-McpSmokeRegression.ps1` is a repeatable development smoke suite that talks
to a live Unity project through the bundled UniBridge relay and real MCP stdio.
The PowerShell file is the Windows entrypoint; it launches the bundled Python
helper that owns the line-delimited JSON-RPC loop.

It is intended for release checks before copying a new package build into a game
project. The suite writes a JSON report and exits with a non-zero code on failed
assertions.

Requirements:

- Unity project open with UniBridge compiled and connected.
- Python available on `PATH`.

## Basic Run

```powershell
powershell -ExecutionPolicy Bypass `
  -File .\com.cidonix.unibridge\Tools~\McpSmokeRegression\Run-McpSmokeRegression.ps1 `
  -ProjectPath H:\Repos\UnityRepos\UniBridge_Test_Project
```

## What It Checks

- `tools/list` includes the core UniBridge MCP tools.
- `RuntimeStateProbe.Assertions` is advertised as an array of objects, and a
  legacy/simplified single assertion object is safely normalized at execution.
- `UniBridge_Discover Action=Ping` returns package/project identity.
- Console clear/read workflows work.
- `WaitForReady`, `RefreshAssets`, `RequestScriptCompilationNoWait`, and
  `WaitForReadyAfterReload` survive reload boundaries.
- `GetCompilationDiagnostics` reports no compile/build-system failures.
- `ValidateScript` can validate a script under `Packages/...`.
- `AssetIntelligence Action=ReadText` works as a text-read alias.
- `ContextSnapshot` and `SceneObjectView` return compact AI-readable context.
- `WorkflowRecipes Execute RunCoreSmokeTest` can create, inspect, and clean up a
  temporary scene object.
- Optional Prefab Stage UI coverage verifies strict path/object-id parent
  resolution, stage-scoped Canvas/template/scroll creation, safe missing and
  ambiguous parent failures, saved prefab structure, and no ordinary-scene
  leakage.

## Optional Coverage

```powershell
# Include Play/Exit Play Mode boundary checks.
powershell -ExecutionPolicy Bypass `
  -File .\com.cidonix.unibridge\Tools~\McpSmokeRegression\Run-McpSmokeRegression.ps1 `
  -ProjectPath H:\Repos\UnityRepos\UniBridge_Test_Project `
  -IncludePlayMode

# Include UI and asset recipe checks.
powershell -ExecutionPolicy Bypass `
  -File .\com.cidonix.unibridge\Tools~\McpSmokeRegression\Run-McpSmokeRegression.ps1 `
  -ProjectPath H:\Repos\UnityRepos\UniBridge_Test_Project `
  -IncludeUiRecipe `
  -IncludeAssetRecipe `
  -AssetFolder Assets/Sprites

# Include isolated Prefab Stage UI creation and parent-resolution checks.
powershell -ExecutionPolicy Bypass `
  -File .\com.cidonix.unibridge\Tools~\McpSmokeRegression\Run-McpSmokeRegression.ps1 `
  -ProjectPath H:\Repos\UnityRepos\UniBridge_Test_Project `
  -IncludePrefabStageUi
```

Use `-SkipRefresh` or `-SkipCompile` only when you need a short connectivity
smoke and do not want to cross Unity reload boundaries.
