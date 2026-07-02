#Requires -Version 5.1
<#
.SYNOPSIS
    Authoritative external lifecycle test for AgentRecorder.Headless API server.

.DESCRIPTION
    Verifies that the headless API server survives AFTER start-api-server.ps1
    returns. This is the most important acceptance test: it catches the
    "passes inside the script but dies outside" false positive.

    Steps:
    1. Run stop-api-server.ps1 to clean up old instances.
    2. Run start-api-server.ps1 to build + start the service.
    3. Wait $WaitSeconds AFTER start-api-server.ps1 has exited.
    4. From this external script, verify:
       - PID from metadata still exists
       - 127.0.0.1:37891 is LISTENING
       - Listening PID matches metadata PID
       - GET /api/v1/capabilities returns 200
       - smoke-api.ps1 exits 0
    5. By default, runs stop-api-server.ps1 to clean up.
       Use -KeepAlive to leave the service running for manual debugging.

.PARAMETER WaitSeconds
    Seconds to wait after start-api-server.ps1 returns before verifying.
    Default: 30.

.PARAMETER KeepAlive
    If set, do not stop the service after the test; leave it running.

.PARAMETER NoBreakaway
    Pass -NoBreakaway to start-api-server.ps1.

.EXAMPLE
    .\scripts\test-api-service-external-lifecycle.ps1
#>

param(
    [int]$WaitSeconds = 30,
    [switch]$KeepAlive = $false,
    [switch]$NoBreakaway = $false
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Resolve-Path (Join-Path $ScriptDir "..") | Select-Object -ExpandProperty Path
$MetadataFile = Join-Path $ProjectRoot ".local-data\headless-api-server.json"
$PidFile = Join-Path $ProjectRoot ".local-data\headless-api-server.pid"
$Port = 37891

$script:failures = @()

function Write-Step([string]$msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok([string]$msg) { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Fail([string]$msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red }
function Write-Info([string]$msg) { Write-Host "[INFO] $msg" -ForegroundColor Yellow }

function Record-Failure([string]$msg) {
    $script:failures += $msg
    Write-Fail $msg
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

function Show-Diagnostics {
    Write-Host "`n--- DIAGNOSTICS ---" -ForegroundColor Magenta

    Write-Host "`n[AgentRecorder* processes]" -ForegroundColor Cyan
    Get-Process AgentRecorder* -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path | Format-Table -AutoSize

    Write-Host "`n[Port $Port status]" -ForegroundColor Cyan
    netstat -ano | Select-String ":$Port" | ForEach-Object { Write-Host $_ }

    Write-Host "`n[Audit log tail (20 lines)]" -ForegroundColor Cyan
    $auditPath = Join-Path $ProjectRoot ".local-data\logs\audit.jsonl"
    if (Test-Path $auditPath) {
        Get-Content $auditPath -Tail 20 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host "(audit log not found)"
    }

    Write-Host "`n[Startup errors tail (20 lines)]" -ForegroundColor Cyan
    $errPath = Join-Path $ProjectRoot ".local-data\logs\startup-errors.jsonl"
    if (Test-Path $errPath) {
        Get-Content $errPath -Tail 20 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host "(startup errors log not found)"
    }
}

try {
    Write-Step "Step 1: Clean up old headless instances"
    $stopScript = Join-Path $ScriptDir "stop-api-server.ps1"
    & $stopScript -MetadataFile $MetadataFile -PidFile $PidFile -Port $Port
    if ($LASTEXITCODE -ne 0) {
        Write-Info "stop-api-server.ps1 exited $LASTEXITCODE (may be normal if nothing was running)"
    }
    Write-Ok "Cleanup done"

    Write-Step "Step 2: Start service via start-api-server.ps1"
    $startScript = Join-Path $ScriptDir "start-api-server.ps1"
    if ($NoBreakaway) {
        & $startScript -NoBreakaway
    } else {
        & $startScript
    }
    $startExit = $LASTEXITCODE

    if ($startExit -ne 0) {
        Record-Failure "start-api-server.ps1 exited with code $startExit"
        Show-Diagnostics
        exit 1
    }
    Write-Ok "start-api-server.ps1 exited 0"

    Write-Step "Step 3: Waiting $WaitSeconds`s from EXTERNAL perspective"
    Write-Info "This is the critical check: does the service survive AFTER the launcher script returns?"
    Start-Sleep -Seconds $WaitSeconds
    Write-Ok "Wait complete"

    Write-Step "Step 4: External verification"

    $metadataPid = $null
    $metadataEvent = $null
    if (Test-Path $MetadataFile) {
        try {
            $meta = Get-Content $MetadataFile -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            if ($meta.pid) { $metadataPid = [int]$meta.pid }
            if ($meta.shutdown_event_name) { $metadataEvent = [string]$meta.shutdown_event_name }
            Write-Info "Metadata: pid=$metadataPid, event=$metadataEvent"
        } catch {
            Record-Failure "Could not read metadata file: $_"
        }
    } else {
        Record-Failure "Metadata file not found: $MetadataFile"
    }

    if (-not $metadataPid) {
        if (Test-Path $PidFile) {
            $pidVal = Get-Content $PidFile -Raw -ErrorAction SilentlyContinue
            if ($pidVal -and ($pidVal -match '^\d+$')) {
                $metadataPid = [int]$pidVal
                Write-Info "Fell back to PID file: $metadataPid"
            }
        }
    }

    if ($metadataPid) {
        $proc = Get-Process -Id $metadataPid -ErrorAction SilentlyContinue
        if ($proc -and $proc.ProcessName -eq "AgentRecorder.Headless") {
            Write-Ok "Headless process PID=$metadataPid is still running"
        } elseif ($proc) {
            Record-Failure "PID $metadataPid exists but is not AgentRecorder.Headless (it is $($proc.ProcessName))"
        } else {
            Record-Failure "Headless process PID=$metadataPid no longer exists (process died after launcher exited)"
        }
    }

    $listenerPid = Get-ListeningPidOnPort -Port $Port
    if ($null -eq $listenerPid) {
        Record-Failure "Port $Port is not LISTENING after $WaitSeconds`s external wait"
    } else {
        Write-Ok "Port $Port is LISTENING on PID=$listenerPid"
        if ($metadataPid -and $listenerPid -ne $metadataPid) {
            Record-Failure "Listening PID ($listenerPid) does not match metadata PID ($metadataPid)"
        }
    }

    if (Test-ApiHealthy -TimeoutSec 5) {
        Write-Ok "GET /api/v1/capabilities returns HTTP 200"
    } else {
        Record-Failure "GET /api/v1/capabilities failed after $WaitSeconds`s external wait"
    }

    Write-Step "Step 5: Run smoke-api.ps1"
    $smokeScript = Join-Path $ScriptDir "smoke-api.ps1"
    & $smokeScript
    $smokeExit = $LASTEXITCODE
    if ($smokeExit -eq 0) {
        Write-Ok "smoke-api.ps1 passed"
    } else {
        Record-Failure "smoke-api.ps1 exited with code $smokeExit"
    }

    if ($script:failures.Count -gt 0) {
        Show-Diagnostics
        Write-Host "`n[FAIL] External lifecycle test FAILED with $($script:failures.Count) failure(s):" -ForegroundColor Red
        $script:failures | ForEach-Object { Write-Host "  - $_" }
        exit 1
    }

    Write-Host "`n=== EXTERNAL LIFECYCLE TEST: PASSED ===" -ForegroundColor Green
    Write-Info "Service survived $WaitSeconds`s after start-api-server.ps1 exited"
} finally {
    if (-not $KeepAlive) {
        Write-Step "Cleanup: stopping service"
        try {
            $stopScript = Join-Path $ScriptDir "stop-api-server.ps1"
            & $stopScript -MetadataFile $MetadataFile -PidFile $PidFile -Port $Port
        } catch {
            Write-Info "Cleanup error: $_"
        }
    } else {
        Write-Info "-KeepAlive specified; service left running"
    }
}
