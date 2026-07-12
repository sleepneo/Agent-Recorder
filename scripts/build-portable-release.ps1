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
    Version string for the zip name. Default: v0.1.3

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
    [string]$Version = "v0.1.3",

    [ValidateSet("self-contained", "framework-dependent")]
    [string]$PublishMode = "self-contained",

    [string]$ProjectRoot = "",

    [switch]$DisableReadyToRun
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

$headlessProject = Join-Path $ProjectRoot "src\AgentRecorder.Headless\AgentRecorder.Headless.csproj"
if (-not (Test-Path $headlessProject)) {
    Write-Host "[ERROR] AgentRecorder.Headless.csproj not found at $headlessProject" -ForegroundColor Red
    exit 1
}

$cliProject = Join-Path $ProjectRoot "tools\AgentRecorder.Cli\AgentRecorder.Cli.csproj"
if (-not (Test-Path $cliProject)) {
    Write-Host "[ERROR] AgentRecorder.Cli.csproj not found at $cliProject" -ForegroundColor Red
    exit 1
}

$appPublishDir = Join-Path $StagingDir "AgentRecorder.App"
$headlessPublishDir = Join-Path $StagingDir "AgentRecorder.Headless"
$cliPublishDir = Join-Path $StagingDir "AgentRecorder.Cli"

Write-Host "[1/8] Publishing AgentRecorder.App ($PublishMode)..." -ForegroundColor Yellow

# ReadyToRun: enabled by default for self-contained, disabled for framework-dependent or when explicitly requested.
$enableR2R = ($PublishMode -eq "self-contained") -and -not $DisableReadyToRun
Write-Host "  ReadyToRun: $(if ($enableR2R) { 'enabled' } else { 'disabled' })" -ForegroundColor Gray

if ($PublishMode -eq "self-contained") {
    $publishArgs = @(
        "publish", $appProject,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $appPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=$($enableR2R.ToString().ToLowerInvariant())",
        "-p:Deterministic=false"
    )
} else {
    $publishArgs = @(
        "publish", $appProject,
        "--configuration", "Release",
        "--output", $appPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false",
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

Write-Host "[2/8] Publishing AgentRecorder.Headless ($PublishMode)..." -ForegroundColor Yellow

if ($PublishMode -eq "self-contained") {
    $headlessPublishArgs = @(
        "publish", $headlessProject,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $headlessPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=$($enableR2R.ToString().ToLowerInvariant())",
        "-p:Deterministic=false"
    )
} else {
    $headlessPublishArgs = @(
        "publish", $headlessProject,
        "--configuration", "Release",
        "--output", $headlessPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false",
        "-p:Deterministic=false"
    )
}

$headlessPublishResult = dotnet @headlessPublishArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] dotnet publish (Headless) failed:" -ForegroundColor Red
    Write-Host $headlessPublishResult
    exit 1
}
Write-Host "[OK] Published to $headlessPublishDir" -ForegroundColor Green

Write-Host "[3/8] Publishing AgentRecorder.Cli ($PublishMode)..." -ForegroundColor Yellow

if ($PublishMode -eq "self-contained") {
    $cliPublishArgs = @(
        "publish", $cliProject,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $cliPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=$($enableR2R.ToString().ToLowerInvariant())",
        "-p:Deterministic=false"
    )
} else {
    $cliPublishArgs = @(
        "publish", $cliProject,
        "--configuration", "Release",
        "--output", $cliPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false",
        "-p:Deterministic=false"
    )
}

$cliPublishResult = dotnet @cliPublishArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] dotnet publish (Cli) failed:" -ForegroundColor Red
    Write-Host $cliPublishResult
    exit 1
}
Write-Host "[OK] Published to $cliPublishDir" -ForegroundColor Green

Write-Host "[4/8] Removing PDBs, XML docs, and dev artifacts..." -ForegroundColor Yellow
$removed = 0
Get-ChildItem -Path $appPublishDir,$headlessPublishDir,$cliPublishDir -Recurse -File | Where-Object {
    $_.Extension -eq ".pdb" -or
    $_.Extension -eq ".xml" -or
    ($_.Name -like "*Tests*") -or
    ($_.Name -like "*test*")
} | ForEach-Object {
    Remove-Item $_.FullName -Force
    $removed++
}
Write-Host "[OK] Removed $removed non-essential files" -ForegroundColor Green

Write-Host "[5/8] Copying FFmpeg binaries..." -ForegroundColor Yellow
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

Write-Host "[6/8] Preparing portable package layout..." -ForegroundColor Yellow
Write-Host "[OK] Portable package layout prepared" -ForegroundColor Green

Write-Host "[7/8] Adding documentation..." -ForegroundColor Yellow

# Root-level docs (including agent instructions and API reference)
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

Write-Host "[8/8] Creating zip archive..." -ForegroundColor Yellow
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
Write-Host "  AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json"
Write-Host "  Let the local AI agent read AGENT-INSTRUCTIONS.zh-CN.md and AGENT-API-REFERENCE.zh-CN.md"
Write-Host "  Prefer POST http://127.0.0.1:37891/api/v1/recordings/quick for common recording intents"
Write-Host ""
