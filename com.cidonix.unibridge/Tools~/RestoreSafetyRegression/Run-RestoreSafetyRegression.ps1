param(
    [Parameter(Mandatory = $true)][string]$Report,
    [Parameter(Mandatory = $true)][string]$NewtonsoftDll,
    [string]$SourceRef,
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the regression from an Administrator PowerShell terminal.'
}
$runnerArguments = @((Join-Path $PSScriptRoot 'run_restore_safety_regression.py'), '--report', $Report, '--newtonsoft-dll', $NewtonsoftDll)
if ($SourceRef) { $runnerArguments += @('--source-ref', $SourceRef) }
if ($Filter) { $runnerArguments += @('--filter', $Filter) }
& python @runnerArguments
exit $LASTEXITCODE
