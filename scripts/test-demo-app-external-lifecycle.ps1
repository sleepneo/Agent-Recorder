#Requires -Version 5.1
<#
.SYNOPSIS
    Authoritative external lifecycle test for the demo app (AgentRecorder.App).

.DESCRIPTION
    Starts the demo app via start-demo-app.ps1, waits for it to return,
    then waits an additional 30 seconds externally before verifying:
    - PID still running and is AgentRecorder.App
    - Port 37891 LISTENING with matching PID
    - /api/v1/capabilities returns 200
    - /api/v1/windows returns 200

    This is the ONLY authoritative acceptance test for standalone demo app
    external lifecycle. Do NOT trust start-demo-app.ps1 internal stabilization.

.PARAMETER WaitSeconds
    External wait seconds after start-demo-app.ps1 returns (default: 30).

.PARAMETER KeepAlive
    Do not stop the demo app after the test (for manual debugging).

.PARAMETER MetadataFile
    Path to demo app metadata JSON.

.PARAMETER Port
    API port (default: 37891).

.PARAMETER RequireVisibleWindows
    Require at least 1 visible window from /api/v1/windows.
    If 0 windows, fail and suggest running test-interactive-desktop-visibility.ps1.

.EXAMPLE
    .\scripts\test-demo-app-external-lifecycle.ps1
    # Full 30-second external lifecycle test

.EXAMPLE
    .\scripts\test-demo-app-external-lifecycle.ps1 -RequireVisibleWindows
    # Also verify windows are visible (for demo readiness check)
#>

param(
    [int]$WaitSeconds = 30,
    [switch]$KeepAlive = $false,
    [string]$MetadataFile = "D:\works\python\007-Agent-Recorder\.local-data\demo-app-server.json",
    [int]$Port = 37891,
    [switch]$RequireVisibleWindows = $false
)

$ErrorActionPreference = "Stop"
$ProjectRoot = "D:\works\python\007-Agent-Recorder"
$DataDir = "D:\works\python\007-Agent-Recorder\.local-data"
$StaleAppPid = 49364

$script:ExitCode = 1
$script:StepsPassed = 0
$script:StepsFailed = 0

function Write-Pass {
    param([string]$Msg)
    $script:StepsPassed++
    Write-Host "[PASS] $Msg" -ForegroundColor Green
}

function Write-Fail {
    param([string]$Msg)
    $script:StepsFailed++
    Write-Host "[FAIL] $Msg" -ForegroundColor Red
}

function Get-ListeningPidOnPort {
    $netstat = netstat -ano | Select-String ":$Port"
    foreach ($line in $netstat) {
        if ($line -match "\s+LISTENING\s+(\d+)") {
            return [int]$matches[1]
        }
    }
    return $null
}

function Invoke-ApiGet {
    param([string]$Path)
    try {
        $r = Invoke-WebRequest "http://127.0.0.1:$Port$Path" -UseBasicParsing -TimeoutSec 5
        return [pscustomobject]@{ StatusCode = $r.StatusCode; Content = $r.Content }
    } catch {
        return [pscustomobject]@{ StatusCode = 0; Content = $_.Exception.Message }
    }
}

function Show-Diagnostics {
    Write-Host ""
    Write-Host "=== DIAGNOSTICS ===" -ForegroundColor Yellow

    Write-Host ""
    Write-Host "--- Metadata file ---"
    if (Test-Path $MetadataFile) {
        Get-Content $MetadataFile -Raw -Encoding UTF8
    } else {
        Write-Host "(metadata file not found)"
    }

    Write-Host ""
    Write-Host "--- Recent audit log (tail 15) ---"
    $auditPath = Join-Path $DataDir "logs\audit.jsonl"
    if (Test-Path $auditPath) {
        Get-Content $auditPath -Tail 15
    } else {
        Write-Host "(audit log not found)"
    }

    Write-Host ""
    Write-Host "--- Recent startup errors (tail 10) ---"
    $errorPath = Join-Path $DataDir "logs\startup-errors.jsonl"
    if (Test-Path $errorPath) {
        Get-Content $errorPath -Tail 10
    } else {
        Write-Host "(startup error log not found)"
    }

    Write-Host ""
    Write-Host "--- AgentRecorder processes ---"
    Get-Process AgentRecorder.App, AgentRecorder.Headless -ErrorAction SilentlyContinue |
        Select-Object Id, ProcessName, Path, StartTime |
        Format-Table -AutoSize

    Write-Host ""
    Write-Host "--- Port $Port status ---"
    netstat -ano | Select-String ":$Port"
}

function Stop-App {
    Write-Host ""
    Write-Host "Stopping demo app..." -ForegroundColor Cyan
    $stopScript = Join-Path $ProjectRoot "scripts\stop-demo-app.ps1"
    & $stopScript
    $stopExit = $LASTEXITCODE
    if ($stopExit -eq 0) {
        Write-Host "[OK] Demo app stopped successfully" -ForegroundColor Green
    } else {
        Write-Host "[WARN] stop-demo-app.ps1 exit code: $stopExit" -ForegroundColor Yellow
    }
    return $stopExit
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Demo App External Lifecycle Test" -ForegroundColor Cyan
Write-Host "  External wait: $WaitSeconds seconds" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

try {
    Write-Host "Step 1: Clean up old demo app" -ForegroundColor Cyan
    $stopScript = Join-Path $ProjectRoot "scripts\stop-demo-app.ps1"
    & $stopScript
    Write-Host "[INFO] Cleanup exit code: $LASTEXITCODE" -ForegroundColor Cyan
    Start-Sleep -Seconds 2
    Write-Host ""

    Write-Host "Step 2: Start demo app (start-demo-app.ps1)" -ForegroundColor Cyan
    $startScript = Join-Path $ProjectRoot "scripts\start-demo-app.ps1"
    & $startScript
    $startExit = $LASTEXITCODE
    if ($startExit -ne 0) {
        Write-Fail "start-demo-app.ps1 failed with exit code $startExit"
        Show-Diagnostics
        exit 1
    }
    Write-Pass "start-demo-app.ps1 returned exit 0"
    Write-Host ""

    Write-Host "Step 3: Loading metadata" -ForegroundColor Cyan
    $metadataPid = $null
    if (Test-Path $MetadataFile) {
        try {
            $metadata = Get-Content $MetadataFile -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            if ($metadata.pid) {
                $metadataPid = [int]$metadata.pid
                Write-Host "[INFO] Metadata PID: $metadataPid" -ForegroundColor Cyan
                if ($metadata.launch_mode) { Write-Host "[INFO] Launch mode: $($metadata.launch_mode)" -ForegroundColor Cyan }
                if ($metadata.launcher_flags) { Write-Host "[INFO] Launcher flags: $($metadata.launcher_flags)" -ForegroundColor Cyan }
            }
        } catch {
            Write-Fail "Failed to parse metadata: $_"
        }
    } else {
        Write-Fail "Metadata file not found: $MetadataFile"
    }
    Write-Host ""

    Write-Host "Step 4: External wait $WaitSeconds seconds" -ForegroundColor Cyan
    Write-Host "[INFO] start-demo-app.ps1 has returned. Waiting externally..." -ForegroundColor Cyan

    $elapsed = 0
    while ($elapsed -lt $WaitSeconds) {
        Start-Sleep -Seconds 5
        $elapsed += 5
        Write-Host "[INFO] External wait: $elapsed / $WaitSeconds s" -ForegroundColor Gray
    }
    Write-Host "[INFO] External wait complete: $WaitSeconds seconds" -ForegroundColor Green
    Write-Host ""

    Write-Host "Step 5: External verification" -ForegroundColor Cyan
    Write-Host ""

    $proc = $null
    if ($metadataPid) {
        $proc = Get-Process -Id $metadataPid -ErrorAction SilentlyContinue
        if ($proc -and $proc.ProcessName -eq "AgentRecorder.App") {
            Write-Pass "PID $metadataPid still running (AgentRecorder.App)"
        } elseif ($proc) {
            Write-Fail "PID $metadataPid running but is $($proc.ProcessName), not AgentRecorder.App"
        } else {
            Write-Fail "PID $metadataPid is NOT running anymore (process disappeared after script exit)"
        }
    } else {
        Write-Fail "No metadata PID to verify"
    }

    $listenerPid = Get-ListeningPidOnPort
    if ($null -eq $listenerPid) {
        Write-Fail "Port $Port is NOT LISTENING"
    } elseif ($listenerPid -eq $StaleAppPid) {
        Write-Fail "Port $Port LISTENING on stale PID=$StaleAppPid"
    } else {
        $listenerProc = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
        if ($listenerProc) {
            Write-Pass "Port $Port LISTENING on PID=$listenerPid ($($listenerProc.ProcessName))"
        } else {
            Write-Pass "Port $Port LISTENING on PID=$listenerPid"
        }
    }

    if ($metadataPid -and $listenerPid) {
        if ($listenerPid -eq $metadataPid) {
            Write-Pass "Listener PID matches metadata PID ($listenerPid)"
        } elseif ($listenerPid -ne $StaleAppPid) {
            Write-Fail "Listener PID=$listenerPid does not match metadata PID=$metadataPid"
        }
    }

    $caps = Invoke-ApiGet -Path "/api/v1/capabilities"
    if ($caps.StatusCode -eq 200) {
        Write-Pass "GET /api/v1/capabilities → 200"
    } else {
        Write-Fail "GET /api/v1/capabilities → $($caps.StatusCode)"
    }

    $win = Invoke-ApiGet -Path "/api/v1/windows"
    $winCount = 0
    if ($win.StatusCode -eq 200) {
        try {
            $winData = $win.Content | ConvertFrom-Json
            $winCount = if ($winData.data -and $winData.data.windows) { $winData.data.windows.Count } else { 0 }
            Write-Pass "GET /api/v1/windows → 200 ($winCount windows)"
        } catch {
            Write-Pass "GET /api/v1/windows → 200"
        }
    } else {
        Write-Fail "GET /api/v1/windows → $($win.StatusCode)"
    }

    if ($RequireVisibleWindows) {
        Write-Host ""
        Write-Host "RequireVisibleWindows check" -ForegroundColor Cyan
        if ($winCount -gt 0) {
            Write-Pass "RequireVisibleWindows: $winCount visible windows found"
        } else {
            Write-Fail "RequireVisibleWindows: 0 visible windows found"
            Write-Host ""
            Write-Host "  The API returned 0 visible windows. This likely means:" -ForegroundColor Yellow
            Write-Host "  - The current session does not have access to an interactive desktop" -ForegroundColor Yellow
            Write-Host "  - The app is running in an invisible window station" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "  Suggestions:" -ForegroundColor Yellow
            Write-Host "    1. Run scripts\test-interactive-desktop-visibility.ps1 -StartDemoApp for full diagnostics" -ForegroundColor Yellow
            Write-Host "    2. Ensure you are running from a real interactive user session" -ForegroundColor Yellow
            Write-Host "    3. Window recording requires a visible desktop; headless/service sessions cannot enumerate windows" -ForegroundColor Yellow
        }
    }

    Write-Host ""

    if ($script:StepsFailed -eq 0) {
        Write-Host "============================================================" -ForegroundColor Green
        Write-Host "  EXTERNAL LIFECYCLE TEST: ALL PASSED ($script:StepsPassed/$($script:StepsPassed + $script:StepsFailed))" -ForegroundColor Green
        Write-Host "  Demo App survived $WaitSeconds seconds after script exit" -ForegroundColor Green
        Write-Host "============================================================" -ForegroundColor Green
        $script:ExitCode = 0
    } else {
        Write-Host "============================================================" -ForegroundColor Red
        Write-Host "  EXTERNAL LIFECYCLE TEST: FAILED ($script:StepsPassed passed, $script:StepsFailed failed)" -ForegroundColor Red
        Write-Host "============================================================" -ForegroundColor Red
        Show-Diagnostics
        $script:ExitCode = 1
    }

} catch {
    Write-Fail "Unhandled exception: $($_.Exception.Message)"
    Show-Diagnostics
    $script:ExitCode = 1
} finally {
    if (-not $KeepAlive) {
        Stop-App | Out-Null
    } else {
        Write-Host ""
        Write-Host "[INFO] -KeepAlive specified; leaving demo app running" -ForegroundColor Cyan
    }
}

exit $script:ExitCode
