[CmdletBinding()]
param(
    [string]$UnityHubEditorsPath = 'C:\Program Files\Unity\Hub\Editor',
    [string[]]$UnityVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$adapterRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Assets\UniBridgeLegacy\Editor'))
if (-not (Test-Path -LiteralPath $adapterRoot -PathType Container)) {
    throw "Legacy adapter source directory was not found: $adapterRoot"
}

$sourceFiles = @(Get-ChildItem -LiteralPath $adapterRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
if ($sourceFiles.Count -eq 0) {
    throw "No legacy adapter C# sources were found under: $adapterRoot"
}

$supportedVersionPattern = '^(5\.3\.|5\.6\.|2017\.4\.|2018\.4\.)'
if ($UnityVersion -and $UnityVersion.Count -gt 0) {
    $editorDirectories = @($UnityVersion | ForEach-Object {
        $candidate = Join-Path $UnityHubEditorsPath $_
        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
            throw "Requested Unity Editor is not installed: $candidate"
        }
        Get-Item -LiteralPath $candidate
    })
}
else {
    if (-not (Test-Path -LiteralPath $UnityHubEditorsPath -PathType Container)) {
        throw "Unity Hub Editors directory was not found: $UnityHubEditorsPath"
    }
    $editorDirectories = @(Get-ChildItem -LiteralPath $UnityHubEditorsPath -Directory |
        Where-Object { $_.Name -match $supportedVersionPattern } |
        Sort-Object Name)
}

if ($editorDirectories.Count -eq 0) {
    throw 'No installed Unity 5.3.x, 5.6.x, 2017.4.x, or 2018.4.x Editors were found.'
}

$systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testDirectory = [System.IO.Path]::GetFullPath((Join-Path $systemTemp ('UniBridgeLegacyCompile-' + [Guid]::NewGuid().ToString('N'))))
if (-not $testDirectory.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([System.IO.Path]::GetFileName($testDirectory)).StartsWith('UniBridgeLegacyCompile-', [StringComparison]::Ordinal)) {
    throw "Refusing to use an unexpected temporary path: $testDirectory"
}
New-Item -ItemType Directory -Path $testDirectory | Out-Null

$results = New-Object System.Collections.Generic.List[object]
try {
    foreach ($editorDirectory in $editorDirectories) {
        if ($editorDirectory.Name -notmatch $supportedVersionPattern) {
            throw "No legacy compatibility profile is defined for Unity $($editorDirectory.Name)."
        }
        $editorPath = Join-Path $editorDirectory.FullName 'Editor'
        $compilerPath = Join-Path $editorPath 'Data\MonoBleedingEdge\bin\mcs.bat'
        $unityEnginePath = Join-Path $editorPath 'Data\Managed\UnityEngine.dll'
        $unityEditorPath = Join-Path $editorPath 'Data\Managed\UnityEditor.dll'
        foreach ($requiredPath in @($compilerPath, $unityEnginePath, $unityEditorPath)) {
            if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
                throw "Unity $($editorDirectory.Name) is missing a required compile input: $requiredPath"
            }
        }

        $outputPath = Join-Path $testDirectory ('UniBridgeLegacy-' + $editorDirectory.Name + '.dll')
        $versionParts = $editorDirectory.Name.Split('.')
        $defines = @('UNITY_EDITOR', 'UNITY_EDITOR_WIN', ('UNITY_' + $versionParts[0] + '_' + $versionParts[1]))
        # Match Unity's actual conditional-compilation boundary. Compiling without
        # version symbols would silently skip newer serialized-property coverage.
        if ([int]$versionParts[0] -gt 5 -or ([int]$versionParts[0] -eq 5 -and [int]$versionParts[1] -ge 6)) {
            $defines += 'UNITY_5_6_OR_NEWER'
        }
        $compilerArguments = @(
            '-target:library',
            '-langversion:4',
            '-sdk:2',
            ('-define:' + ($defines -join ',')),
            ('-r:' + $unityEnginePath),
            ('-r:' + $unityEditorPath),
            ('-out:' + $outputPath)
        ) + $sourceFiles

        $compilerOutput = @(& $compilerPath @compilerArguments 2>&1)
        $exitCode = $LASTEXITCODE
        if ($compilerOutput.Count -gt 0) {
            $compilerOutput | ForEach-Object { Write-Host $_ }
        }
        if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
            throw "Legacy adapter compilation failed for Unity $($editorDirectory.Name) with exit code $exitCode."
        }

        $assembly = Get-Item -LiteralPath $outputPath
        $results.Add([pscustomobject]@{
            UnityVersion = $editorDirectory.Name
            Compiler = $compilerPath
            LanguageVersion = 'C# 4'
            SdkProfile = '.NET 2.0'
            Defines = $defines -join ','
            AssemblyBytes = $assembly.Length
            Result = 'PASS'
        })
    }
}
finally {
    if (Test-Path -LiteralPath $testDirectory) {
        $resolved = [System.IO.Path]::GetFullPath($testDirectory)
        if ($resolved.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
            ([System.IO.Path]::GetFileName($resolved)).StartsWith('UniBridgeLegacyCompile-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

$results | Format-Table UnityVersion, LanguageVersion, SdkProfile, AssemblyBytes, Result -AutoSize
if (-not ($results | Where-Object { $_.UnityVersion -match '^5\.3\.' })) {
    Write-Warning 'No installed Unity 5.3.x Editor was included; the broader legacy matrix passed, but the primary compatibility floor was not exercised.'
}
