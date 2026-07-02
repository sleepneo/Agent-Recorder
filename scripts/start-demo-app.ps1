#Requires -Version 5.1
<#
.SYNOPSIS
    Build and start the Agent Recorder demo app (AgentRecorder.App) with tray UI.

.DESCRIPTION
    - Stops any existing demo app from metadata
    - Builds AgentRecorder.sln (Release) including ProcessLauncher
    - Runs tests (Release)
    - Starts the demo app using ProcessLauncher (CreateProcessW + breakaway fallback)
    - Writes a metadata JSON file with pid, port, etc.
    - Verifies the service is still alive after a stabilization period
    - Excludes historical PID 49364 (stale App that does not listen on port)

.PARAMETER Configuration
    Build configuration: Release (default) or Debug

.PARAMETER WindowBackend
    Window capture backend: "" (default, use FFmpeg gdigrab) or "wgc" (prototype stub)

.PARAMETER MetadataFile
    Path where the demo app metadata JSON will be written.

.PARAMETER StabilizationSeconds
    Seconds to wait after the first health check before re-verifying.

.PARAMETER NoBreakaway
    Do not pass CREATE_BREAKAWAY_FROM_JOB to ProcessLauncher.

.EXAMPLE
    .\scripts\start-demo-app.ps1
    # Default: FFmpeg gdigrab backend

.EXAMPLE
    .\scripts\start-demo-app.ps1 -WindowBackend wgc
    # Enables the WGC prototype stub for window capture
#>

param(
    [string]$Configuration = "Release",
    [string]$WindowBackend = "",
    [string]$MetadataFile = "D:\works\python\007-Agent-Recorder\.local-data\demo-app-server.json",
    [int]$StabilizationSeconds = 10,
    [switch]$NoBreakaway = $false,
    [switch]$SkipBuild = $false
)

$ErrorActionPreference = "Stop"
$ProjectRoot = "D:\works\python\007-Agent-Recorder"
$DataDir = "D:\works\python\007-Agent-Recorder\.local-data"
$Port = 37891
$StaleAppPid = 49364
Set-Location $ProjectRoot

function Stop-OldDemoApp {
    $oldPid = $null
    if (Test-Path $MetadataFile) {
        try {
            $md = Get-Content $MetadataFile -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            if ($md.pid) { $oldPid = [int]$md.pid }
        } catch {}
    }

    if ($oldPid) {
        $proc = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
        if ($proc -and $proc.ProcessName -eq "AgentRecorder.App") {
            try {
                $proc | Stop-Process -Force -ErrorAction Stop
                Write-Host "[INFO] Stopped previous demo app PID=$oldPid" -ForegroundColor Cyan
            } catch {
                Write-Host "[WARN] Could not stop previous demo app PID=$oldPid`: $_" -ForegroundColor Yellow
            }
        }
    }

    $listenerPid = $null
    $netstat = netstat -ano | Select-String ":$Port"
    foreach ($line in $netstat) {
        if ($line -match "\s+LISTENING\s+(\d+)") {
            $listenerPid = [int]$matches[1]
            break
        }
    }

    if ($null -ne $listenerPid -and $listenerPid -ne $oldPid) {
        $proc = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
        if ($proc -and ($proc.ProcessName -eq "AgentRecorder.App" -or $proc.ProcessName -eq "AgentRecorder.Headless")) {
            if ($listenerPid -eq $StaleAppPid) {
                Write-Host "[INFO] Port $Port occupied by stale PID=$StaleAppPid (excluded from cleanup); will try to continue" -ForegroundColor Yellow
                return $false
            }
            try {
                $proc | Stop-Process -Force -ErrorAction Stop
                Write-Host "[INFO] Stopped process on port $Port PID=$listenerPid ($($proc.ProcessName))" -ForegroundColor Cyan
            } catch {
                Write-Host "[WARN] Could not stop process on port $Port PID=$listenerPid`: $_" -ForegroundColor Yellow
            }
        }
    }

    Start-Sleep -Seconds 2
    return $true
}

function Format-Arguments {
    param([string[]]$Arguments)
    $quoted = foreach ($a in $Arguments) {
        if ($a -match '\s') {
            '"' + $a.Replace('"', '\"') + '"'
        } else {
            $a
        }
    }
    return $quoted -join " "
}

function Get-ListeningPidOnPort {
    param([int]$Port)
    $netstat = netstat -ano | Select-String ":$Port"
    foreach ($line in $netstat) {
        if ($line -match "\s+LISTENING\s+(\d+)") {
            return [int]$matches[1]
        }
    }
    return $null
}

function Test-ApiHealthy {
    param([int]$TimeoutSec = 5)
    try {
        $r = Invoke-WebRequest "http://127.0.0.1:$Port/api/v1/capabilities" -UseBasicParsing -TimeoutSec $TimeoutSec
        return ($r.StatusCode -eq 200)
    } catch {
        return $false
    }
}

function Test-WindowsEndpoint {
    param([int]$TimeoutSec = 5)
    try {
        $r = Invoke-WebRequest "http://127.0.0.1:$Port/api/v1/windows" -UseBasicParsing -TimeoutSec $TimeoutSec
        return ($r.StatusCode -eq 200)
    } catch {
        return $false
    }
}

function Show-RecentDiagnostics {
    Write-Host "--- Recent audit log ---" -ForegroundColor Cyan
    $auditPath = Join-Path $DataDir "logs\audit.jsonl"
    if (Test-Path $auditPath) {
        Get-Content $auditPath -Tail 10 | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host "(audit log not found)"
    }

    Write-Host "--- Recent startup errors ---" -ForegroundColor Cyan
    $errorPath = Join-Path $DataDir "logs\startup-errors.jsonl"
    if (Test-Path $errorPath) {
        Get-Content $errorPath -Tail 10 | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host "(startup error log not found)"
    }

    Write-Host "--- Current AgentRecorder* processes ---" -ForegroundColor Cyan
    Get-Process AgentRecorder* -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path | Format-Table -AutoSize

    Write-Host "--- Port $Port status ---" -ForegroundColor Cyan
    netstat -ano | Select-String ":$Port" | ForEach-Object { Write-Host $_ }
}

Write-Host "[1/5] Stopping old demo app..." -ForegroundColor Cyan
if (-not (Stop-OldDemoApp)) {
    Write-Host "[WARN] Port $Port may still be occupied; proceeding anyway" -ForegroundColor Yellow
}

if (-not $SkipBuild) {
    Write-Host "[2/5] Building ($Configuration)..." -ForegroundColor Cyan
    $buildOutput = dotnet build AgentRecorder.sln -c $Configuration 2>&1
    $buildExitCode = $LASTEXITCODE
    $buildOutput | Select-Object -Last 10 | ForEach-Object { Write-Host $_ }

    if ($buildExitCode -ne 0) {
        Write-Host ""
        Write-Host "BUILD FAILED with exit code $buildExitCode" -ForegroundColor Red
        exit 1
    }

    Write-Host "[3/5] Running tests..." -ForegroundColor Cyan
    $testOutput = dotnet test AgentRecorder.sln -c $Configuration --no-build 2>&1
    $testExitCode = $LASTEXITCODE
    $testOutput | Select-Object -Last 8 | ForEach-Object { Write-Host $_ }

    if ($testExitCode -ne 0) {
        Write-Host ""
        Write-Host "TEST FAILED with exit code $testExitCode" -ForegroundColor Red
        exit 1
    }

    Write-Host ""
    Write-Host "=== BUILD + TEST: OK ===" -ForegroundColor Green
} else {
    Write-Host "[2/5] Build skipped (-SkipBuild)" -ForegroundColor Cyan
    Write-Host "[3/5] Tests skipped (-SkipBuild)" -ForegroundColor Cyan
}
Write-Host ""
Write-Host "[4/5] Starting demo app (ProcessLauncher)..." -ForegroundColor Cyan

$appExe = Join-Path $ProjectRoot "src\AgentRecorder.App\bin\$Configuration\net8.0-windows10.0.19041.0\AgentRecorder.App.exe"
$launcherExe = Join-Path $ProjectRoot "tools\ProcessLauncher\bin\$Configuration\net8.0-windows10.0.19041.0\AgentRecorder.ProcessLauncher.exe"

if (-not (Test-Path $appExe)) {
    Write-Host "[FAIL] App executable not found: $appExe" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $launcherExe)) {
    Write-Host "[FAIL] ProcessLauncher executable not found: $launcherExe" -ForegroundColor Red
    exit 1
}

$env:AGENT_RECORDER_DATA_DIR = $DataDir

$ffmpeg = Get-Command ffmpeg -EA SilentlyContinue
if ($ffmpeg) {
    $env:AGENT_RECORDER_FFMPEG_DIR = Split-Path $ffmpeg.Source -Parent
    Write-Host "[INFO] FFmpeg directory: $($env:AGENT_RECORDER_FFMPEG_DIR)" -ForegroundColor Cyan
}

if ($WindowBackend -eq "wgc") {
    $env:AGENT_RECORDER_WINDOW_BACKEND = "wgc"
    Write-Host "[INFO] WGC backend enabled (prototype stub) via -WindowBackend wgc" -ForegroundColor Yellow
} else {
    if (Test-Path "env:AGENT_RECORDER_WINDOW_BACKEND") {
        Remove-Item "env:AGENT_RECORDER_WINDOW_BACKEND" -ErrorAction SilentlyContinue
    }
    Write-Host "[INFO] Default window backend: FFmpeg gdigrab" -ForegroundColor Green
}

function Invoke-ProcessLauncher {
    param([string]$Mode = "detached")
    $args = [System.Collections.Generic.List[string]]::new()
    $args.Add("--exe")
    $args.Add($appExe)
    $args.Add("--work-dir")
    $args.Add($ProjectRoot)
    $args.Add("--mode")
    $args.Add($Mode)

    $cmdLine = Format-Arguments -Arguments $args.ToArray()
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $launcherExe
    $psi.Arguments = $cmdLine
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $ProjectRoot

    $proc = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $proc) {
        return [pscustomobject]@{ ExitCode = -1; Stdout = ""; Stderr = "Failed to start launcher process"; Pid = $null; Mode = $Mode }
    }

    $out = $proc.StandardOutput.ReadToEnd()
    $err = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit(10000)

    if (-not $proc.HasExited) {
        try { $proc.Kill() } catch { }
    }

    $pidVal = $null
    if ($out -match '(?m)^PID=(\d+)') {
        $pidVal = [int]$matches[1]
    }

    $flagsVal = "0x0"
    if ($out -match '(?m)^FLAGS=(0x[0-9A-Fa-f]+)') {
        $flagsVal = $matches[1]
    }

    return [pscustomobject]@{
        ExitCode = $proc.ExitCode
        Stdout = $out
        Stderr = $err
        Pid = $pidVal
        Mode = $Mode
        Flags = $flagsVal
    }
}

if (Test-Path $MetadataFile) {
    Remove-Item $MetadataFile -Force -ErrorAction SilentlyContinue
}

$launchModes = @("gui", "gui-no-breakaway", "detached")
if ($NoBreakaway) {
    $launchModes = @("gui-no-breakaway", "detached")
}

$launchResult = $null
$usedMode = ""

foreach ($mode in $launchModes) {
    Write-Host "[INFO] Attempting ProcessLauncher mode: $mode" -ForegroundColor Cyan
    $result = Invoke-ProcessLauncher -Mode $mode
    if ($result.ExitCode -eq 0 -and $result.Pid) {
        $launchResult = $result
        $usedMode = $mode
        Write-Host "[INFO] Launch succeeded with mode=$mode, PID=$($result.Pid)" -ForegroundColor Green
        break
    } else {
        Write-Host "[INFO] Mode $mode failed (exit=$($result.ExitCode)); trying next..." -ForegroundColor Yellow
    }
}

if (-not $launchResult -or -not $launchResult.Pid) {
    Write-Host "[FAIL] All launch modes failed" -ForegroundColor Red
    Write-Host "--- Last attempt stdout ---"
    if ($result) { Write-Host $result.Stdout }
    Write-Host "--- Last attempt stderr ---"
    if ($result) { Write-Host $result.Stderr }
    Show-RecentDiagnostics
    exit 1
}

$launchedPid = $launchResult.Pid
$launcherFlags = $launchResult.Flags
$launchMode = $usedMode

Write-Host "[OK] Demo app launched via ProcessLauncher mode=$launchMode : PID=$launchedPid (flags=$launcherFlags)" -ForegroundColor Green

Write-Host "[INFO] Waiting for API to become healthy..." -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds(20)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if (Test-ApiHealthy -TimeoutSec 3) {
        $healthy = $true
        break
    }
}

if (-not $healthy) {
    Write-Host "[FAIL] Demo app API did not become healthy within 20 seconds" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

$listenerPid = Get-ListeningPidOnPort -Port $Port
if ($null -eq $listenerPid) {
    Write-Host "[FAIL] Port $Port is not LISTENING after health check" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

if ($listenerPid -ne $launchedPid) {
    Write-Host "[WARN] Port $Port LISTENING on PID=$listenerPid, launched PID=$launchedPid" -ForegroundColor Yellow
    $proc = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
    if ($proc -and $proc.ProcessName -ne "AgentRecorder.App" -and $proc.ProcessName -ne "AgentRecorder.Headless") {
        Write-Host "[FAIL] Port $Port listener is not AgentRecorder: $($proc.ProcessName)" -ForegroundColor Red
        exit 1
    }
    if ($listenerPid -eq $StaleAppPid) {
        Write-Host "[FAIL] Port $Port still occupied by stale PID=$StaleAppPid" -ForegroundColor Red
        exit 1
    }
    Write-Host "[INFO] Using listener PID=$listenerPid for metadata" -ForegroundColor Cyan
    $metadataPid = $listenerPid
} else {
    $metadataPid = $launchedPid
}

if (-not (Test-WindowsEndpoint -TimeoutSec 5)) {
    Write-Host "[FAIL] GET /api/v1/windows did not return 200" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

Write-Host "[OK] Initial health check passed (PID=$metadataPid)" -ForegroundColor Green
Write-Host "[OK] GET /capabilities and GET /windows both return 200" -ForegroundColor Green

Write-Host "[INFO] Waiting $StabilizationSeconds`s stabilization period..." -ForegroundColor Cyan
Start-Sleep -Seconds $StabilizationSeconds

$proc = Get-Process -Id $metadataPid -ErrorAction SilentlyContinue
if (-not $proc) {
    Write-Host "[FAIL] Demo app process (PID=$metadataPid) exited during stabilization" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

$listenerPid2 = Get-ListeningPidOnPort -Port $Port
if ($null -eq $listenerPid2 -or $listenerPid2 -ne $metadataPid) {
    Write-Host "[FAIL] Port $Port not LISTENING on expected PID=$metadataPid after stabilization" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

if (-not (Test-ApiHealthy -TimeoutSec 5)) {
    Write-Host "[FAIL] Demo app API not responding after stabilization" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

$metadataDir = Split-Path $MetadataFile -Parent
if (-not (Test-Path $metadataDir)) {
    New-Item -ItemType Directory -Path $metadataDir -Force | Out-Null
}

$metadata = [ordered]@{
    pid = [int]$metadataPid
    started_at = (Get-Date).ToUniversalTime().ToString("o")
    exe = $appExe
    port = $Port
    data_dir = $DataDir
    window_backend = if ($WindowBackend) { $WindowBackend } else { "ffmpeg_gdigrab" }
    launcher_flags = $launcherFlags
    launch_mode = "process_launcher_$launchMode"
}

$metadata | ConvertTo-Json -Depth 5 | Set-Content -Path $MetadataFile -Encoding UTF8 -NoNewline

Write-Host "[OK] Metadata written to $MetadataFile" -ForegroundColor Green
Write-Host "[OK] Demo app stable and healthy: PID=$metadataPid, port=$Port" -ForegroundColor Green
Write-Host "[INFO] Tray icon should be visible in the system tray for recording confirmation" -ForegroundColor Cyan
