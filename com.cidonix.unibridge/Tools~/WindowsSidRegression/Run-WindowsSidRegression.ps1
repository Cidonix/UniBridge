param(
    [Parameter(Mandatory = $true)]
    [string]$Report,
    [string]$SourceRef,
    [string]$UnityEditor,
    [string]$InspectPipe,
    [int]$Iterations = 2000,
    [switch]$SkipUnityMono
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Python 3 is required.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }
$runnerArguments = @((Join-Path $PSScriptRoot 'run_windows_sid_regression.py'), '--report', $Report, '--iterations', $Iterations)
if ($SourceRef) { $runnerArguments += @('--source-ref', $SourceRef) }
if ($UnityEditor) { $runnerArguments += @('--unity-editor', $UnityEditor) }
if ($InspectPipe) { $runnerArguments += @('--inspect-pipe', $InspectPipe) }
if ($SkipUnityMono) { $runnerArguments += '--skip-unity-mono' }
& python @runnerArguments
if ($LASTEXITCODE -ne 0) { throw "Windows SID regression failed with exit code $LASTEXITCODE. Report: $Report" }
