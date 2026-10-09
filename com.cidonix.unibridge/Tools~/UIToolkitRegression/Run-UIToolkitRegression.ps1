param(
    [Parameter(Mandatory=$true)][string]$Report,
    [Parameter(Mandatory=$true)][string]$NewtonsoftDll,
    [string]$Filter,
    [string]$ToolSourceRef
)
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run UI Toolkit qualification from an Administrator terminal.'
}
$arguments = @('-B', (Join-Path $PSScriptRoot 'run_uitoolkit_regression.py'), '--report', $Report, '--newtonsoft-dll', $NewtonsoftDll)
if ($Filter) { $arguments += @('--filter', $Filter) }
if ($ToolSourceRef) { $arguments += @('--tool-source-ref', $ToolSourceRef) }
& python @arguments
exit $LASTEXITCODE
