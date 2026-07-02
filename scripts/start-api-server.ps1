#Requires -Version 5.1
<#
.SYNOPSIS
    Build and start the Agent Recorder headless API server.

.DESCRIPTION
    - Stops any existing AgentRecorder.Headless processes (via metadata/named event)
    - Builds AgentRecorder.sln (Release) including ProcessLauncher
    - Runs tests (Release)
    - Starts the headless API host using ProcessLauncher (CreateProcessW + breakaway)
    - Writes a metadata JSON file with pid, shutdown event name, etc.
    - Verifies the service is still alive after a stabilization period

.PARAMETER Configuration
    Build configuration: Release (default) or Debug

.PARAMETER WindowBackend
    Window capture backend: "" (default, use FFmpeg gdigrab) or "wgc" (prototype stub)
    Leaving it unset means window recordings use FFmpeg gdigrab.

.PARAMETER MetadataFile
    Path where the headless server metadata JSON will be written.

.PARAMETER PidFile
    Optional path where the headless server PID will be written (legacy compatibility).

.PARAMETER StabilizationSeconds
    Seconds to wait after the first health check before verifying the service is
    still alive. This catches processes that exit shortly after startup.

.PARAMETER NoBreakaway
    Do not pass CREATE_BREAKAWAY_FROM_JOB to ProcessLauncher.
    Use only if breakaway fails in your environment.

.EXAMPLE
    .\scripts\start-api-server.ps1 -WindowBackend wgc
    # Enables the WGC prototype stub for window capture (not for production use)

.EXAMPLE
    .\scripts\start-api-server.ps1
    # Default: no WGC flag; window recording uses FFmpeg gdigrab
#>

param(
    [string]$Configuration = "Release",
    [string]$WindowBackend = "",
    [string]$MetadataFile = "D:\works\python\007-Agent-Recorder\.local-data\headless-api-server.json",
    [string]$PidFile = "D:\works\python\007-Agent-Recorder\.local-data\headless-api-server.pid",
    [int]$StabilizationSeconds = 10,
    [switch]$NoBreakaway = $false
)

$ErrorActionPreference = "Stop"
$ProjectRoot = "D:\works\python\007-Agent-Recorder"
$DataDir = "D:\works\python\007-Agent-Recorder\.local-data"
$Port = 37891
Set-Location $ProjectRoot

function Stop-OldAgentRecorderProcesses {
    $oldHeadless = @(Get-Process AgentRecorder.Headless -ErrorAction SilentlyContinue)
    if ($oldHeadless.Count -eq 0) { return $true }

    $failedPids = @()
    foreach ($proc in $oldHeadless) {
        try {
            $proc | Stop-Process -Force -ErrorAction Stop
        } catch {
            $failedPids += $proc.Id
        }
    }

    Start-Sleep -Seconds 2

    $stillRunning = @()
    foreach ($procId in $failedPids) {
        if (Get-Process -Id $procId -ErrorAction SilentlyContinue) {
            $stillRunning += $procId
        }
    }

    if ($stillRunning.Count -gt 0) {
        $portInUse = $false
        $netstat = netstat -ano | Select-String ":$Port"
        foreach ($line in $netstat) {
            if ($line -match "\s+LISTENING\s+(\d+)") {
                $portInUse = $true
                break
            }
        }

        if ($portInUse) {
            Write-Host "Old AgentRecorder.Headless processes could not be stopped and port $Port is still occupied. PIDs: $($stillRunning -join ', ')" -ForegroundColor Red
            return $false
        } else {
            Write-Host "Warning: old AgentRecorder.Headless processes could not be stopped, but port $Port is free. Continuing... PIDs: $($stillRunning -join ', ')" -ForegroundColor Yellow
        }
    }

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

Write-Host "[1/5] Stopping old processes..." -ForegroundColor Cyan
if (-not (Stop-OldAgentRecorderProcesses)) {
    exit 1
}

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
Write-Host ""
Write-Host "[4/5] Starting headless API server (ProcessLauncher)..." -ForegroundColor Cyan

$headlessExe = Join-Path $ProjectRoot "src\AgentRecorder.Headless\bin\$Configuration\net8.0-windows10.0.19041.0\AgentRecorder.Headless.exe"
$launcherExe = Join-Path $ProjectRoot "tools\ProcessLauncher\bin\$Configuration\net8.0-windows10.0.19041.0\AgentRecorder.ProcessLauncher.exe"

if (-not (Test-Path $headlessExe)) {
    Write-Host "[FAIL] Headless executable not found: $headlessExe" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $launcherExe)) {
    Write-Host "[FAIL] ProcessLauncher executable not found: $launcherExe" -ForegroundColor Red
    exit 1
}

$instanceId = [System.Guid]::NewGuid().ToString("N").Substring(0, 12)
$shutdownEventName = "AgentRecorder.Headless.Shutdown.$instanceId"

$headlessArgs = [System.Collections.Generic.List[string]]::new()
$headlessArgs.Add("--data-dir")
$headlessArgs.Add($DataDir)
$headlessArgs.Add("--pid-file")
$headlessArgs.Add($PidFile)
$headlessArgs.Add("--shutdown-event-name")
$headlessArgs.Add($shutdownEventName)

$ffmpeg = Get-Command ffmpeg -EA SilentlyContinue
if ($ffmpeg) {
    $headlessArgs.Add("--ffmpeg-dir")
    $headlessArgs.Add((Split-Path $ffmpeg.Source -Parent))
}

if ($WindowBackend -eq "wgc") {
    $headlessArgs.Add("--window-backend")
    $headlessArgs.Add("wgc")
    Write-Host "[INFO] WGC backend enabled (prototype stub) via -WindowBackend wgc" -ForegroundColor Yellow
} else {
    Write-Host "[INFO] Default window backend: FFmpeg gdigrab" -ForegroundColor Green
}

function Invoke-ProcessLauncher {
    param([bool]$UseBreakaway)
    $args = [System.Collections.Generic.List[string]]::new()
    $args.Add("--exe")
    $args.Add($headlessExe)
    $args.Add("--work-dir")
    $args.Add($ProjectRoot)
    if (-not $UseBreakaway) {
        $args.Add("--no-breakaway")
    }
    $args.Add("--")
    foreach ($a in $headlessArgs) {
        $args.Add($a)
    }

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
        return [pscustomobject]@{ ExitCode = -1; Stdout = ""; Stderr = "Failed to start launcher process"; Pid = $null }
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

    return [pscustomobject]@{
        ExitCode = $proc.ExitCode
        Stdout = $out
        Stderr = $err
        Pid = $pidVal
    }
}

if (Test-Path $PidFile) {
    Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
}
if (Test-Path $MetadataFile) {
    Remove-Item $MetadataFile -Force -ErrorAction SilentlyContinue
}

$useBreakaway = -not $NoBreakaway
$launchResult = $null

if ($useBreakaway) {
    Write-Host "[INFO] Attempting ProcessLauncher with CREATE_BREAKAWAY_FROM_JOB..." -ForegroundColor Cyan
    $launchResult = Invoke-ProcessLauncher -UseBreakaway $true
    if ($launchResult.ExitCode -ne 0) {
        Write-Host "[INFO] Breakaway launch failed (exit=$($launchResult.ExitCode)); retrying without breakaway..." -ForegroundColor Yellow
        $useBreakaway = $false
        $launchResult = $null
    }
}

if (-not $launchResult) {
    $launchResult = Invoke-ProcessLauncher -UseBreakaway $false
}

if ($launchResult.ExitCode -ne 0 -or -not $launchResult.Pid) {
    Write-Host "[FAIL] ProcessLauncher failed with exit code $($launchResult.ExitCode)" -ForegroundColor Red
    Write-Host "--- ProcessLauncher stdout ---"
    Write-Host $launchResult.Stdout
    Write-Host "--- ProcessLauncher stderr ---"
    Write-Host $launchResult.Stderr
    Show-RecentDiagnostics
    exit 1
}

$launchedPid = $launchResult.Pid
$launcherFlags = if ($useBreakaway) { "DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB" } else { "DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW" }

Write-Host "[OK] Headless API server launched via ProcessLauncher: PID=$launchedPid" -ForegroundColor Green
Write-Host "[INFO] Shutdown event: $shutdownEventName" -ForegroundColor Cyan

$healthPid = $null
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $PidFile) {
        $healthPid = Get-Content $PidFile -Raw -ErrorAction SilentlyContinue
        if ($healthPid) { break }
    }
}

if (-not $healthPid) {
    Write-Host "[FAIL] Headless API server did not write PID file" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

if ([int]$healthPid -ne $launchedPid) {
    Write-Host "[WARN] PID file value ($healthPid) differs from launched PID ($launchedPid); using PID file value for checks" -ForegroundColor Yellow
}

if (-not (Test-ApiHealthy -TimeoutSec 5)) {
    Write-Host "[FAIL] Headless API server not responding on initial health check" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}
Write-Host "[OK] Initial health check passed (PID=$healthPid)" -ForegroundColor Green

Write-Host "[INFO] Waiting $StabilizationSeconds`s stabilization period..." -ForegroundColor Cyan
Start-Sleep -Seconds $StabilizationSeconds

$proc = Get-Process -Id $healthPid -ErrorAction SilentlyContinue
if (-not $proc) {
    Write-Host "[FAIL] Headless API server process (PID=$healthPid) exited during stabilization" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

$listenerPid = Get-ListeningPidOnPort -Port $Port
if ($null -eq $listenerPid) {
    Write-Host "[FAIL] Port $Port is not LISTENING after stabilization" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

if ($listenerPid -ne $healthPid) {
    Write-Host "[FAIL] Port $Port is LISTENING on PID=$listenerPid, expected PID=$healthPid" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

if (-not (Test-ApiHealthy -TimeoutSec 5)) {
    Write-Host "[FAIL] Headless API server not responding after stabilization" -ForegroundColor Red
    Show-RecentDiagnostics
    exit 1
}

$metadataDir = Split-Path $MetadataFile -Parent
if (-not (Test-Path $metadataDir)) {
    New-Item -ItemType Directory -Path $metadataDir -Force | Out-Null
}

$metadata = [ordered]@{
    pid = [int]$healthPid
    shutdown_event_name = $shutdownEventName
    started_at = (Get-Date).ToUniversalTime().ToString("o")
    exe = $headlessExe
    arguments = $headlessArgs.ToArray()
    port = $Port
    data_dir = $DataDir
    window_backend = if ($WindowBackend) { $WindowBackend } else { "ffmpeg_gdigrab" }
    launcher_flags = $launcherFlags
    instance_id = $instanceId
}

$metadata | ConvertTo-Json -Depth 5 | Set-Content -Path $MetadataFile -Encoding UTF8 -NoNewline

Write-Host "[OK] Metadata written to $MetadataFile" -ForegroundColor Green
Write-Host "[OK] Headless API server stable and healthy: PID=$healthPid, port=$Port" -ForegroundColor Green
