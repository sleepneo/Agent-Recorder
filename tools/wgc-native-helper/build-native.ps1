<#
.SYNOPSIS
    Builds the wgc-native-helper native project and tests, then copies the Release exe
    to the location expected by WgcHelperExePathResolver.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [switch]$SkipRunTests,
    [switch]$SkipTests, # deprecated alias for -SkipRunTests
    [string]$OutputExeDir = ""
)

$ErrorActionPreference = "Stop"

# Resolve the project root robustly even if $PSScriptRoot is empty in some invocation contexts.
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($projectRoot)) {
    throw "Unable to determine the script's project root directory."
}
if (-not $PSBoundParameters.ContainsKey('OutputExeDir')) {
    $OutputExeDir = Join-Path $projectRoot "bin"
}

function Find-VsWhere {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) { return $vswhere }

    $vswhere = "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) { return $vswhere }

    throw "vswhere.exe not found. Install Visual Studio Build Tools."
}

function Find-MSBuild {
    $vswhere = Find-VsWhere
    $installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ([string]::IsNullOrWhiteSpace($installPath)) {
        throw "No Visual Studio installation with MSBuild found."
    }

    $msbuild = Join-Path $installPath "MSBuild\Current\Bin\MSBuild.exe"
    if (-not (Test-Path $msbuild)) {
        $msbuild = Join-Path $installPath "MSBuild\15.0\Bin\MSBuild.exe"
    }
    if (-not (Test-Path $msbuild)) {
        throw "MSBuild.exe not found under $installPath"
    }

    return @{ MSBuild = $msbuild; InstallPath = $installPath }
}

function Get-FileSha256 {
    param([string]$Path)
    $maxRetries = 10
    for ($i = 0; $i -lt $maxRetries; $i++) {
        try {
            $hash = Get-FileHash -Path $Path -Algorithm SHA256 -ErrorAction Stop
            return $hash.Hash
        } catch {
            if ($i -eq $maxRetries - 1) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
    throw "Unable to compute SHA-256 for $Path"
}

$toolInfo = Find-MSBuild
$msbuild = $toolInfo.MSBuild
$installPath = $toolInfo.InstallPath

Write-Host "MSBuild: $msbuild"
Write-Host "VS Install: $installPath"
Write-Host "Project Root: $projectRoot"

$mainProject = Join-Path $projectRoot "wgc-native-helper.vcxproj"
$testProject = Join-Path $projectRoot "wgc-native-helper-tests.vcxproj"

if (-not (Test-Path $mainProject)) { throw "Main project not found: $mainProject" }
if (-not (Test-Path $testProject)) { throw "Test project not found: $testProject" }

# Build main project.
Write-Host "`nBuilding wgc-native-helper ($Configuration|$Platform)..."
& $msbuild $mainProject "/p:Configuration=$Configuration;Platform=$Platform" "/m" "/nr:false" "/v:m"
if ($LASTEXITCODE -ne 0) { throw "Main project build failed." }

# Build test project.
Write-Host "`nBuilding wgc-native-helper-tests ($Configuration|$Platform)..."
& $msbuild $testProject "/p:Configuration=$Configuration;Platform=$Platform" "/m" "/nr:false" "/v:m"
if ($LASTEXITCODE -ne 0) { throw "Test project build failed." }

$mainExe = Join-Path $projectRoot "bin\$Platform\$Configuration\wgc-native-helper.exe"
$testExe = Join-Path $projectRoot "bin\$Platform\$Configuration\wgc-native-helper-tests.exe"

if (-not (Test-Path $mainExe)) { throw "Main executable not found: $mainExe" }
if (-not (Test-Path $testExe)) { throw "Test executable not found: $testExe" }

$skipRun = $SkipRunTests -or $SkipTests
if (-not $skipRun) {
    Write-Host "`nRunning native unit tests..."
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "Native unit tests failed." }
}

# Copy Release exe to the resolver's expected location.
if (-not (Test-Path $OutputExeDir)) {
    New-Item -ItemType Directory -Path $OutputExeDir -Force | Out-Null
}

$destExe = Join-Path $OutputExeDir "wgc-native-helper.exe"
Copy-Item -Path $mainExe -Destination $destExe -Force
Write-Host "`nCopied executable to: $destExe"

$size = (Get-Item $destExe).Length
$sha256 = Get-FileSha256 -Path $destExe
Write-Host "Size: $size bytes"
Write-Host "SHA-256: $sha256"

Write-Host "`nBuild completed successfully."
