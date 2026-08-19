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
    [string]$OutputExeDir = "",
    [switch]$TestSynchronization,
    [ValidateRange(1000, 600000)]
    [int]$TestTimeoutMs = 600000
)

$ErrorActionPreference = "Stop"

# Some hosted Windows shells expose both Path and PATH in the inherited
# environment block. .NET Framework MSBuild treats those names as duplicate
# dictionary keys when it starts CL.exe, so keep one canonical entry.
$normalizedPath = [Environment]::GetEnvironmentVariable("Path")
if ($null -ne $normalizedPath) {
    Remove-Item Env:PATH -ErrorAction SilentlyContinue
    $env:Path = $normalizedPath
}

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
            $stream = $null
            $sha256 = $null
            try {
                $stream = [System.IO.File]::Open(
                    $Path,
                    [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::Read,
                    [System.IO.FileShare]::ReadWrite)
                $sha256 = [System.Security.Cryptography.SHA256]::Create()
                $bytes = $sha256.ComputeHash($stream)
                return ([System.BitConverter]::ToString($bytes)).Replace("-", "")
            }
            finally {
                if ($null -ne $sha256) { $sha256.Dispose() }
                if ($null -ne $stream) { $stream.Dispose() }
            }
        } catch {
            if ($i -eq $maxRetries - 1) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
    throw "Unable to compute SHA-256 for $Path"
}

function Sync-HelperExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $sourceFull = [System.IO.Path]::GetFullPath($SourcePath)
    $destinationFull = [System.IO.Path]::GetFullPath($DestinationPath)
    if (-not (Test-Path -LiteralPath $sourceFull -PathType Leaf)) {
        throw "Helper source is not a regular file: $sourceFull"
    }

    $sourceInfo = Get-Item -LiteralPath $sourceFull -Force
    if (($sourceInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Helper source is a reparse point: $sourceFull"
    }

    $destinationParent = Split-Path -Parent $destinationFull
    if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    }

    $sourceHash = Get-FileSha256 -Path $sourceFull
    $temporaryPath = "$destinationFull.tmp.$([guid]::NewGuid().ToString('N'))"
    $backupPath = "$destinationFull.backup.$([guid]::NewGuid().ToString('N'))"
    try {
        Copy-Item -LiteralPath $sourceFull -Destination $temporaryPath -Force
        $temporaryInfo = Get-Item -LiteralPath $temporaryPath -Force
        $temporaryHash = Get-FileSha256 -Path $temporaryPath
        if ($temporaryInfo.Length -ne $sourceInfo.Length -or $temporaryHash -cne $sourceHash) {
            throw "Temporary helper copy did not match source: $sourceFull -> $destinationFull"
        }

        if (Test-Path -LiteralPath $destinationFull -PathType Leaf) {
            # Windows PowerShell/.NET Framework requires a legal backup path
            # for File.Replace; remove this unique backup only after the
            # destination has been atomically swapped.
            [System.IO.File]::Replace($temporaryPath, $destinationFull, $backupPath)
            if (Test-Path -LiteralPath $backupPath) {
                Remove-Item -LiteralPath $backupPath -Force
            }
        } else {
            [System.IO.File]::Move($temporaryPath, $destinationFull)
        }

        $destinationInfo = Get-Item -LiteralPath $destinationFull -Force
        $destinationHash = Get-FileSha256 -Path $destinationFull
        if ($destinationInfo.Length -ne $sourceInfo.Length -or $destinationHash -cne $sourceHash) {
            throw "Synchronized helper does not match source: $sourceFull -> $destinationFull"
        }

        return [pscustomobject]@{
            Path = $destinationFull
            Length = $destinationInfo.Length
            Sha256 = $destinationHash
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $backupPath) {
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($TestSynchronization) {
    $testRoot = Join-Path $env:TEMP ("agent-recorder-helper-sync-" + [guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
        $sourcePath = Join-Path $testRoot "fresh\wgc-native-helper.exe"
        $canonicalPath = Join-Path $testRoot "canonical\wgc-native-helper.exe"
        $externalPath = Join-Path $testRoot "external\wgc-native-helper.exe"
        New-Item -ItemType Directory -Path (Split-Path -Parent $sourcePath), (Split-Path -Parent $canonicalPath), (Split-Path -Parent $externalPath) -Force | Out-Null
        [System.IO.File]::WriteAllBytes($sourcePath, [byte[]](0..255))
        [System.IO.File]::WriteAllBytes($canonicalPath, [byte[]](255..0))
        [System.IO.File]::WriteAllBytes($externalPath, [byte[]](1..8))

        $canonicalResult = Sync-HelperExecutable -SourcePath $sourcePath -DestinationPath $canonicalPath
        $externalResult = Sync-HelperExecutable -SourcePath $sourcePath -DestinationPath $externalPath
        if ($canonicalResult.Length -ne $externalResult.Length -or $canonicalResult.Sha256 -cne $externalResult.Sha256) {
            throw "Canonical and external helper synchronization diverged."
        }
        Write-Host "Helper synchronization tests passed (external OutputExeDir cannot leave canonical helper stale)."
    }
    finally {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
    exit 0
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
    & $testExe --supervisor-timeout-ms $TestTimeoutMs
    if ($LASTEXITCODE -ne 0) { throw "Native unit tests failed." }
}

# Always synchronize the canonical development helper, even when portable
# packaging requests a separate destination. Each destination is replaced
# through a verified sibling temporary file so a failed copy cannot truncate
# the previous canonical helper.
$canonicalExe = Join-Path $projectRoot "bin\wgc-native-helper.exe"
$requestedExe = Join-Path ([System.IO.Path]::GetFullPath($OutputExeDir)) "wgc-native-helper.exe"
$destinationPaths = @($canonicalExe)
if (-not ([System.IO.Path]::GetFullPath($canonicalExe).Equals(
        [System.IO.Path]::GetFullPath($requestedExe),
        [System.StringComparison]::OrdinalIgnoreCase))) {
    $destinationPaths += $requestedExe
}

$synchronized = @()
foreach ($destinationPath in $destinationPaths) {
    $synchronized += Sync-HelperExecutable -SourcePath $mainExe -DestinationPath $destinationPath
}

$sourceInfo = Get-Item -LiteralPath $mainExe -Force
$sourceSha256 = Get-FileSha256 -Path $mainExe
foreach ($result in $synchronized) {
    if ($result.Length -ne $sourceInfo.Length -or $result.Sha256 -cne $sourceSha256) {
        throw "Synchronized helper verification failed for $($result.Path)."
    }
    Write-Host "`nSynchronized executable to: $($result.Path)"
    Write-Host "Size: $($result.Length) bytes"
    Write-Host "SHA-256: $($result.Sha256)"
}

Write-Host "`nBuild completed successfully."
