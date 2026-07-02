#Requires -Version 5.1
<#
.SYNOPSIS
    Run a self-contained API session: start Headless, do work, then stop it.

.DESCRIPTION
    This script starts the AgentRecorder.Headless API server within the same
    PowerShell session lifetime, waits for it to become healthy, runs a
    specified action, and then gracefully stops the service.

    This is the "Plan B" fallback for environments where detached process
    launch (ProcessLauncher with CREATE_BREAKAWAY_FROM_JOB) cannot survive
    after the launching script exits.

.PARAMETER Mode
    Action to run after the API becomes healthy:
    - Smoke: run smoke-api.ps1
    - PublicBoundary: run test-wgc-continuous-public-boundary.ps1
    - DemoRecordWindow: run demo-record-window.ps1 -ResolveOnly (window enumeration only;
      Headless cannot confirm recordings, so actual recording requires demo app)
    - KeepAlive: just start and wait for user to press Enter

.PARAMETER Configuration
    Build configuration: Release (default) or Debug.

.PARAMETER WindowBackend
    Window capture backend: "" (default, FFmpeg gdigrab) or "wgc".

.EXAMPLE
    .\scripts\invoke-api-session.ps1 -Mode Smoke
#>

param(
    [ValidateSet("Smoke", "PublicBoundary", "DemoRecordWindow", "KeepAlive")]
    [string]$Mode = "Smoke",
    [string]$Configuration = "Release",
    [string]$WindowBackend = ""
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Resolve-Path (Join-Path $ScriptDir "..") | Select-Object -ExpandProperty Path
$DataDir = Join-Path $ProjectRoot ".local-data"
$Port = 37891

$script:headlessProc = $null
$script:shutdownEventName = $null
$script:apiKey = $null

function Write-Step([string]$msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok([string]$msg) { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Fail([string]$msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red }
function Write-Info([string]$msg) { Write-Host "[INFO] $msg" -ForegroundColor Yellow }

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

function Test-ApiHealthy {
    param([int]$TimeoutSec = 3)
    try {
        $r = Invoke-WebRequest "http://127.0.0.1:$Port/api/v1/capabilities" -UseBasicParsing -TimeoutSec $TimeoutSec
        return ($r.StatusCode -eq 200)
    } catch {
        return $false
    }
}

function Stop-HeadlessGraceful {
    if ($script:shutdownEventName) {
        try {
            $evt = [System.Threading.EventWaitHandle]::OpenExisting($script:shutdownEventName)
            $evt.Set() | Out-Null
            $evt.Dispose()
            Write-Info "Requested graceful shutdown via event: $script:shutdownEventName"
            Start-Sleep -Seconds 3
        } catch {
            Write-Info "Could not open shutdown event: $_"
        }
    }

    if ($script:headlessProc -and -not $script:headlessProc.HasExited) {
        try {
            $script:headlessProc.Kill()
            $null = $script:headlessProc.WaitForExit(3000)
            Write-Info "Headless process killed as fallback"
        } catch {
            Write-Info "Could not kill headless process: $_"
        }
    }

    Start-Sleep -Milliseconds 500
}

try {
    Write-Step "Resolving API key"
    $keyFile = Join-Path $DataDir "config\api-key.txt"
    if (Test-Path $keyFile) {
        $script:apiKey = (Get-Content -Raw $keyFile).Trim()
    }
    if ([string]::IsNullOrWhiteSpace($script:apiKey)) {
        Write-Fail "API key not found at $keyFile"
        exit 1
    }
    Write-Ok "API key loaded"

    Write-Step "Stopping any existing headless processes"
    Get-Process AgentRecorder.Headless -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_ | Stop-Process -Force -ErrorAction Stop } catch { }
    }
    Start-Sleep -Seconds 1
    Write-Ok "Old processes cleaned up"

    Write-Step "Starting Headless API server (foreground session mode)"
    $headlessExe = Join-Path $ProjectRoot "src\AgentRecorder.Headless\bin\$Configuration\net8.0-windows10.0.19041.0\AgentRecorder.Headless.exe"
    if (-not (Test-Path $headlessExe)) {
        Write-Fail "Headless executable not found: $headlessExe`nBuild first: dotnet build AgentRecorder.sln -c $Configuration"
        exit 1
    }

    $instanceId = [System.Guid]::NewGuid().ToString("N").Substring(0, 12)
    $script:shutdownEventName = "AgentRecorder.Headless.Shutdown.$instanceId"

    $headlessArgs = [System.Collections.Generic.List[string]]::new()
    $headlessArgs.Add("--data-dir")
    $headlessArgs.Add($DataDir)
    $headlessArgs.Add("--shutdown-event-name")
    $headlessArgs.Add($script:shutdownEventName)

    $ffmpeg = Get-Command ffmpeg -EA SilentlyContinue
    if ($ffmpeg) {
        $headlessArgs.Add("--ffmpeg-dir")
        $headlessArgs.Add((Split-Path $ffmpeg.Source -Parent))
    }

    if ($WindowBackend -eq "wgc") {
        $headlessArgs.Add("--window-backend")
        $headlessArgs.Add("wgc")
        Write-Info "WGC backend enabled (prototype stub)"
    } else {
        Write-Info "Default window backend: FFmpeg gdigrab"
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $headlessExe
    $psi.Arguments = Format-Arguments -Arguments $headlessArgs.ToArray()
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $ProjectRoot

    $script:headlessProc = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $script:headlessProc) {
        Write-Fail "Failed to start headless process"
        exit 1
    }

    Write-Info "Headless started: PID=$($script:headlessProc.Id)"

    Write-Step "Waiting for API health"
    $maxWait = 60
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $healthy = $false
    while ($sw.Elapsed.TotalSeconds -lt $maxWait) {
        if ($script:headlessProc.HasExited) {
            Write-Fail "Headless process exited early (exit code $($script:headlessProc.ExitCode))"
            $stdout = $script:headlessProc.StandardOutput.ReadToEnd()
            $stderr = $script:headlessProc.StandardError.ReadToEnd()
            Write-Host "--- stdout ---`n$stdout"
            Write-Host "--- stderr ---`n$stderr"
            exit 1
        }
        if (Test-ApiHealthy -TimeoutSec 3) {
            $healthy = $true
            break
        }
        Start-Sleep -Seconds 1
    }
    $sw.Stop()

    if (-not $healthy) {
        Write-Fail "API did not become healthy within ${maxWait}s"
        exit 1
    }
    Write-Ok "API healthy after $([math]::Round($sw.Elapsed.TotalSeconds,1))s (PID=$($script:headlessProc.Id))"

    Write-Step "Running action: $Mode"
    $actionExit = 0

    switch ($Mode) {
        "Smoke" {
            $smokeScript = Join-Path $ScriptDir "smoke-api.ps1"
            & $smokeScript
            $actionExit = $LASTEXITCODE
        }
        "PublicBoundary" {
            $pbScript = Join-Path $ScriptDir "test-wgc-continuous-public-boundary.ps1"
            & $pbScript
            $actionExit = $LASTEXITCODE
        }
        "DemoRecordWindow" {
            Write-Info "DemoRecordWindow mode: window resolution pre-flight (ResolveOnly)"
            Write-Info "Note: Headless cannot confirm recordings. For actual recording, use start-demo-app.ps1 + demo-record-window.ps1."
            $demoScript = Join-Path $ScriptDir "demo-record-window.ps1"
            if (Test-Path $demoScript) {
                & $demoScript -ResolveOnly -WindowTitlePattern "Notepad|PowerShell|Windows Terminal|Visual Studio Code"
                $actionExit = $LASTEXITCODE
            } else {
                Write-Info "demo-record-window.ps1 not found; skipping"
                $actionExit = 0
            }
        }
        "KeepAlive" {
            Write-Info "KeepAlive mode: service running. Press Enter to stop."
            $null = Read-Host
            $actionExit = 0
        }
    }

    if ($actionExit -ne 0) {
        Write-Fail "Action '$Mode' exited with code $actionExit"
        exit $actionExit
    }

    Write-Ok "Action '$Mode' completed successfully"

    Write-Step "Stopping Headless API server"
    Stop-HeadlessGraceful

    $remaining = @(Get-Process AgentRecorder.Headless -ErrorAction SilentlyContinue)
    if ($remaining.Count -gt 0) {
        Write-Info "Warning: $($remaining.Count) AgentRecorder.Headless process(es) remaining: $($remaining.Id -join ', ')"
    } else {
        Write-Ok "All AgentRecorder.Headless processes stopped"
    }

    Write-Host "`n=== SESSION: COMPLETE ===" -ForegroundColor Green
    exit 0
} finally {
    if ($script:headlessProc -and -not $script:headlessProc.HasExited) {
        Write-Info "Cleaning up headless process in finally block..."
        try { Stop-HeadlessGraceful } catch { }
    }
}
