param(
    [Parameter(Mandatory = $true)]
    [string]$ReportPath,
    [string]$NewtonsoftDll,
    [string]$Filter,
    [switch]$IncludeTimeout
)

$ErrorActionPreference = 'Stop'
$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCommand) { throw 'Python is required for the command replay regression suite.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required for the command replay regression suite.' }
$runner = Join-Path $PSScriptRoot 'run_command_replay_regression.py'
$runnerArguments = @($runner, '--report', $ReportPath)
if ($NewtonsoftDll) { $runnerArguments += @('--newtonsoft-dll', $NewtonsoftDll) }
if ($Filter) { $runnerArguments += @('--filter', $Filter) }
if ($IncludeTimeout) { $runnerArguments += '--include-timeout' }
& $pythonCommand.Source @runnerArguments
if ($LASTEXITCODE -ne 0) { throw "Command replay regression failed with exit code $LASTEXITCODE. Report: $ReportPath" }
