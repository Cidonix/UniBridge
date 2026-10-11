param([Parameter(Mandatory=$true)][string]$Report, [Parameter(Mandatory=$true)][string]$NewtonsoftDll, [string]$SourceRoot, [string]$BaseRoot)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run from an Administrator terminal.' }
if (-not $SourceRoot) { $SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path }
if (-not $BaseRoot) { $BaseRoot = $SourceRoot }
& python -B (Join-Path $PSScriptRoot 'run_input_api_regression.py') --report $Report --source-root $SourceRoot --base-root $BaseRoot --newtonsoft-dll $NewtonsoftDll
exit $LASTEXITCODE
