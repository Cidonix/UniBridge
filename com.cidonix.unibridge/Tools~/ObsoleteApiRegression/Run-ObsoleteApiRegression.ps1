param(
    [Parameter(Mandatory = $true)][string]$Report,
    [string]$SourceRef,
    [string]$UnityEditorData
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the regression from an Administrator PowerShell terminal.'
}
$runnerArguments = @((Join-Path $PSScriptRoot 'run_obsolete_api_regression.py'), '--report', $Report)
if ($SourceRef) { $runnerArguments += @('--source-ref', $SourceRef) }
if ($UnityEditorData) { $runnerArguments += @('--unity-editor-data', $UnityEditorData) }
& python @runnerArguments
exit $LASTEXITCODE
