param(
    [Parameter(Mandatory = $true)][string]$Report,
    [Parameter(Mandatory = $true)][string]$NewtonsoftDll,
    [string]$SourceRef,
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'run_editor_wait_regression.py'
$runnerArguments = @($runner, '--report', $Report, '--newtonsoft-dll', $NewtonsoftDll)
if ($SourceRef) { $runnerArguments += @('--source-ref', $SourceRef) }
if ($Filter) { $runnerArguments += @('--filter', $Filter) }
& python @runnerArguments
exit $LASTEXITCODE
