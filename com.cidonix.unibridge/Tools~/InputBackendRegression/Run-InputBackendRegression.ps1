param([Parameter(Mandatory=$true)][string]$Report, [string[]]$UnityEditorData)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run from an Administrator terminal.' }
$taskArguments = @('-B', (Join-Path $PSScriptRoot 'run_input_backend_regression.py'), '--report', $Report)
foreach ($taskData in $UnityEditorData) { $taskArguments += @('--unity-editor-data', $taskData) }
& python @taskArguments
exit $LASTEXITCODE
