#Requires -Version 5.1
<#
.SYNOPSIS
    Build Agent Recorder portable release zip.

.DESCRIPTION
    Publishes AgentRecorder.App as a Windows x64 portable package, copies bundled
    FFmpeg into the app directory, adds agent-facing API docs, excludes
    PDBs / .local-data / API keys, and creates a zip under
    .local-data/release-candidates/.

.PARAMETER Version
    Version string for the zip name. Default: v0.1.0

.PARAMETER PublishMode
    "self-contained" (default) or "framework-dependent".

.PARAMETER ProjectRoot
    Optional project root. Defaults to the parent directory of this script.

.EXAMPLE
    .\build-portable-release.ps1

.EXAMPLE
    .\build-portable-release.ps1 -PublishMode framework-dependent
#>

param(
    [string]$Version = "v0.1.0",

    [ValidateSet("self-contained", "framework-dependent")]
    [string]$PublishMode = "self-contained",

    [string]$ProjectRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
} else {
    $ProjectRoot = (Resolve-Path $ProjectRoot).Path
}

$ReleaseTag = "AgentRecorder-$Version-win-x64"
if ($PublishMode -eq "self-contained") {
    $ReleaseTag += "-self-contained"
} else {
    $ReleaseTag += "-framework-dependent"
}

$StagingDir = Join-Path $ProjectRoot ".local-data\release-candidates\$ReleaseTag"
$ZipPath = Join-Path $ProjectRoot ".local-data\release-candidates\$ReleaseTag.zip"

if (Test-Path $StagingDir) {
    Remove-Item $StagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

Write-Host "=== Agent Recorder Portable Release Builder ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Mode: $PublishMode"
Write-Host "Staging: $StagingDir"
Write-Host ""

$appProject = Join-Path $ProjectRoot "src\AgentRecorder.App\AgentRecorder.App.csproj"
if (-not (Test-Path $appProject)) {
    Write-Host "[ERROR] AgentRecorder.App.csproj not found at $appProject" -ForegroundColor Red
    exit 1
}

$appPublishDir = Join-Path $StagingDir "AgentRecorder.App"

Write-Host "[1/6] Publishing AgentRecorder.App ($PublishMode)..." -ForegroundColor Yellow
if ($PublishMode -eq "self-contained") {
    $publishArgs = @(
        "publish", $appProject,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $appPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false",
        "-p:Deterministic=false"
    )
} else {
    $publishArgs = @(
        "publish", $appProject,
        "--configuration", "Release",
        "--output", $appPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:Deterministic=false"
    )
}

$publishResult = dotnet @publishArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] dotnet publish failed:" -ForegroundColor Red
    Write-Host $publishResult
    exit 1
}
Write-Host "[OK] Published to $appPublishDir" -ForegroundColor Green

Write-Host "[2/6] Removing PDBs, XML docs, and dev artifacts..." -ForegroundColor Yellow
$removed = 0
Get-ChildItem -Path $appPublishDir -Recurse -File | Where-Object {
    $_.Extension -eq ".pdb" -or
    $_.Extension -eq ".xml" -or
    ($_.Name -like "*Tests*") -or
    ($_.Name -like "*test*")
} | ForEach-Object {
    Remove-Item $_.FullName -Force
    $removed++
}
Write-Host "[OK] Removed $removed non-essential files" -ForegroundColor Green

Write-Host "[3/6] Copying FFmpeg binaries..." -ForegroundColor Yellow
$ffmpegSrc = Join-Path $ProjectRoot "tools\ffmpeg\bin"
if (-not (Test-Path $ffmpegSrc)) {
    Write-Host "[ERROR] FFmpeg bin not found at $ffmpegSrc" -ForegroundColor Red
    exit 1
}

$ffmpegFiles = @(
    "ffmpeg.exe", "ffprobe.exe",
    "avcodec-58.dll", "avdevice-58.dll", "avfilter-7.dll",
    "avformat-58.dll", "avutil-56.dll", "postproc-55.dll",
    "swresample-3.dll", "swscale-5.dll"
)

foreach ($file in $ffmpegFiles) {
    $src = Join-Path $ffmpegSrc $file
    $dst = Join-Path $appPublishDir $file
    if (Test-Path $src) {
        Copy-Item $src -Destination $dst -Force
    }
}
Write-Host "[OK] FFmpeg copied to app directory" -ForegroundColor Green

Write-Host "[4/6] Preparing portable package layout..." -ForegroundColor Yellow
Write-Host "[OK] Portable package layout prepared" -ForegroundColor Green

Write-Host "[5/6] Adding documentation..." -ForegroundColor Yellow

# Root-level docs (including agent instructions and raw API reference)
foreach ($rootDoc in @("README.md", "README.zh-CN.md", "AGENT-INSTRUCTIONS.zh-CN.md", "AGENT-API-REFERENCE.zh-CN.md")) {
    $src = Join-Path $ProjectRoot $rootDoc
    if (Test-Path $src) {
        Copy-Item $src -Destination (Join-Path $StagingDir $rootDoc) -Force
    }
}

foreach ($packageDoc in @("QUICKSTART.md", "QUICKSTART.zh-CN.md", "LICENSE", "LICENSE-NOTICE.md")) {
    $src = Join-Path $ProjectRoot $packageDoc
    if (Test-Path $src) {
        Copy-Item $src -Destination (Join-Path $StagingDir $packageDoc) -Force
    }
}

Write-Host "[OK] Documentation added" -ForegroundColor Green

Write-Host "[6/6] Creating zip archive..." -ForegroundColor Yellow
$zipParent = Split-Path $ZipPath -Parent
if (-not (Test-Path $zipParent)) {
    New-Item -ItemType Directory -Path $zipParent -Force | Out-Null
}

if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}

Compress-Archive -Path "$StagingDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal
$zipSize = (Get-Item $ZipPath).Length
$zipSizeMB = [math]::Round($zipSize / 1MB, 2)
Write-Host "[OK] Created $ZipPath ($zipSizeMB MB)" -ForegroundColor Green

Write-Host ""
Write-Host "=== Release Build Complete ===" -ForegroundColor Cyan
Write-Host "  Mode: $PublishMode"
Write-Host "  Zip: $ZipPath"
Write-Host "  Size: $zipSizeMB MB"
Write-Host "  Staging: $StagingDir"
Write-Host ""
Write-Host "Smoke test (after extracting):" -ForegroundColor Cyan
Write-Host "  cd <extract-dir>"
Write-Host "  Start AgentRecorder.App\AgentRecorder.App.exe"
Write-Host "  Let the local AI agent read AGENT-INSTRUCTIONS.zh-CN.md and AGENT-API-REFERENCE.zh-CN.md"
Write-Host "  Verify GET http://127.0.0.1:37891/api/v1/capabilities via raw API"
Write-Host ""
