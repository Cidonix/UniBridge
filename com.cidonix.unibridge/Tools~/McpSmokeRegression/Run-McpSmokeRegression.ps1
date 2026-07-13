param(
    [string]$ProjectPath = $env:UNIBRIDGE_PROJECT_PATH,
    [string]$RelayPath,
    [string]$ReportPath,
    [int]$DefaultTimeoutSeconds = 60,
    [int]$ReloadTimeoutSeconds = 180,
    [switch]$SkipRefresh,
    [switch]$SkipCompile,
    [switch]$IncludePlayMode,
    [switch]$IncludeUiRecipe,
    [switch]$IncludePrefabStageUi,
    [switch]$IncludeAssetRecipe,
    [string]$AssetFolder = "Assets/Sprites",
    [int]$MaxSteps = 0,
    [string]$TraceTransportPath
)

$ErrorActionPreference = "Stop"

$python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $python) {
    throw "Python is required to run the UniBridge MCP smoke regression suite."
}

$runner = Join-Path $PSScriptRoot "run_mcp_smoke_regression.py"
$args = @($runner)

if (-not [string]::IsNullOrWhiteSpace($ProjectPath)) {
    $args += @("--project-path", $ProjectPath)
}

if (-not [string]::IsNullOrWhiteSpace($RelayPath)) {
    $args += @("--relay-path", $RelayPath)
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $args += @("--report-path", $ReportPath)
}

$args += @("--default-timeout-seconds", [string]$DefaultTimeoutSeconds)
$args += @("--reload-timeout-seconds", [string]$ReloadTimeoutSeconds)
$args += @("--asset-folder", $AssetFolder)

if ($SkipRefresh) { $args += "--skip-refresh" }
if ($SkipCompile) { $args += "--skip-compile" }
if ($IncludePlayMode) { $args += "--include-play-mode" }
if ($IncludeUiRecipe) { $args += "--include-ui-recipe" }
if ($IncludePrefabStageUi) { $args += "--include-prefab-stage-ui" }
if ($IncludeAssetRecipe) { $args += "--include-asset-recipe" }
if ($MaxSteps -gt 0) { $args += @("--max-steps", [string]$MaxSteps) }
if (-not [string]::IsNullOrWhiteSpace($TraceTransportPath)) {
    $args += @("--trace-transport-path", $TraceTransportPath)
}

& $python.Source @args
exit $LASTEXITCODE
