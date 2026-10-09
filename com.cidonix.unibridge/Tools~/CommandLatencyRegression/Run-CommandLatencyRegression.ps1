param(
    [Parameter(Mandatory=$true)][switch]$AllowTestProject,
    [Parameter(Mandatory=$true)][string]$Project,
    [Parameter(Mandatory=$true)][string]$ProjectId,
    [Parameter(Mandatory=$true)][string]$UnityExe,
    [Parameter(Mandatory=$true)][string]$UnityVersion,
    [Parameter(Mandatory=$true)][string]$Relay,
    [Parameter(Mandatory=$true)][int]$EditorPid,
    [Parameter(Mandatory=$true)][string]$PackageVersion,
    [Parameter(Mandatory=$true)][ValidateSet('foreground','background','minimized')][string]$SingleState,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,80}$')][string]$ReportName,
    [ValidateSet('baseline','candidate')][string]$Phase = 'baseline',
    [switch]$ExternalWindowReady,
    [ValidateRange(1,5)][int]$WarmRepeats = 3,
    [ValidateRange(1,4)][int]$ShortSessions = 3,
    [ValidateRange(0.5,10)][double]$IdleSeconds = 2,
    [ValidateRange(1,20)][double]$CallTimeout = 8,
    [ValidateRange(1,30)][double]$InitializeTimeout = 15,
    [ValidateRange(30,300)][double]$WholeRunSeconds = 180,
    [double]$SlowThresholdMs = 1000,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
if (-not $AllowTestProject) { throw 'Explicit owner opt-in -AllowTestProject is required.' }
if (-not [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Launch the controller from an Administrator process; relay children inherit that token.'
}
$reportDirectory = Join-Path (Join-Path $Project 'Library/AgentValidation/CommandLatencyRegression') $ReportName
$culture = [Globalization.CultureInfo]::InvariantCulture
$latencyArguments = @(
    (Join-Path $PSScriptRoot 'run_command_latency_regression.py'),
    '--allow-test-project', '--project', $Project, '--project-id', $ProjectId,
    '--unity-exe', $UnityExe, '--unity-version', $UnityVersion,
    '--relay', $Relay, '--editor-pid', [string]$EditorPid,
    '--package-version', $PackageVersion, '--single-state', $SingleState,
    '--report-directory', $reportDirectory, '--phase', $Phase,
    '--warm-repeats', [string]$WarmRepeats, '--short-sessions', [string]$ShortSessions,
    '--idle-seconds', $IdleSeconds.ToString($culture),
    '--call-timeout', $CallTimeout.ToString($culture),
    '--initialize-timeout', $InitializeTimeout.ToString($culture),
    '--whole-run-seconds', [string]$WholeRunSeconds,
    '--slow-threshold-ms', $SlowThresholdMs.ToString($culture)
)
if ($ExternalWindowReady) { $latencyArguments += '--external-window-ready' }
& $Python @latencyArguments
if ($LASTEXITCODE -ne 0) {
    throw "Command latency qualification returned $LASTEXITCODE. Preserve the report at $reportDirectory; a failure must not be silently retried."
}
