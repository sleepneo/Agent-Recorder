# Task 66: Fix for Task 65 review - AutoStartServer observable launch and timeout.
# Verifies that the production API does not advertise or accept WGC continuous
# recording, that no continuous production entry points exist in source, and that
# -AutoStartServer starts the server directly with observable logs, bounded waits,
# and reliable cleanup of only the processes it created.
#
# Must be run while AgentRecorder.App is listening on the configured BaseUrl.
# Compatible with Windows PowerShell 5.1.

param(
    [string]$BaseUrl = "http://127.0.0.1:37891",
    [string]$ApiKey = "",
    [switch]$AutoStartServer = $false
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok([string]$msg) { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Fail([string]$msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red }
function Write-Info([string]$msg) { Write-Host "[INFO] $msg" -ForegroundColor Yellow }

$script:failures = @()
function Record-Failure([string]$msg) {
    $script:failures += $msg
    Write-Fail $msg
}

# ----------------------------------------------------------------------------
# Resolve project root and API key
# ----------------------------------------------------------------------------
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Resolve-Path (Join-Path $ScriptDir "..") | Select-Object -ExpandProperty Path

if ([string]::IsNullOrEmpty($ApiKey)) {
    $KeyFile = Join-Path $ProjectRoot ".local-data\config\api-key.txt"
    if (Test-Path $KeyFile) {
        $ApiKey = (Get-Content -Raw $KeyFile).Trim()
    }
}

if ([string]::IsNullOrEmpty($ApiKey)) {
    Record-Failure "API key not provided and $KeyFile not found"
    exit 1
}

# ----------------------------------------------------------------------------
# Helpers (use curl.exe for reliable response-body capture on Windows)
# ----------------------------------------------------------------------------
function Invoke-ApiGet([string]$path) {
    $uri = ($BaseUrl.TrimEnd('/') + $path)
    $outFile = [System.IO.Path]::GetTempFileName()
    try {
        $httpCode = curl.exe -s -o $outFile -w "%{http_code}" `
            --connect-timeout 2 --max-time 3 `
            -H "X-Agent-Recorder-Key: $ApiKey" $uri
        $body = if (Test-Path $outFile) { Get-Content -Raw $outFile -Encoding UTF8 } else { "" }
        return @{ status = [int]$httpCode; body = $body }
    } catch {
        return @{ status = -1; body = $_.Exception.Message }
    } finally {
        if (Test-Path $outFile) { Remove-Item $outFile -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-ApiPost([string]$path, [object]$payload) {
    $uri = ($BaseUrl.TrimEnd('/') + $path)
    $json = $payload | ConvertTo-Json -Depth 3 -Compress
    $bodyFile = [System.IO.Path]::GetTempFileName()
    $outFile = [System.IO.Path]::GetTempFileName()
    try {
        $json | Set-Content -Path $bodyFile -Encoding UTF8 -NoNewline
        $httpCode = curl.exe -s -o $outFile -w "%{http_code}" `
            --connect-timeout 2 --max-time 3 `
            -H "X-Agent-Recorder-Key: $ApiKey" `
            -H "Content-Type: application/json" `
            -d "@$bodyFile" $uri
        $body = if (Test-Path $outFile) { Get-Content -Raw $outFile -Encoding UTF8 } else { "" }
        return @{ status = [int]$httpCode; body = $body }
    } catch {
        return @{ status = -1; body = $_.Exception.Message }
    } finally {
        if (Test-Path $bodyFile) { Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue }
        if (Test-Path $outFile) { Remove-Item $outFile -Force -ErrorAction SilentlyContinue }
    }
}

function Body-Contains([string]$body, [string]$token) {
    return $body -like "*$token*"
}

function Body-HasCode([string]$body, [string]$code) {
    try {
        $parsed = $body | ConvertFrom-Json
        return $parsed.error.code -eq $code
    } catch { return $false }
}

function Get-RecordingCount {
    $list = Invoke-ApiGet "/api/v1/recordings"
    if ($list.status -ne 200) { return -1 }
    try {
        $parsed = $list.body | ConvertFrom-Json
        return $parsed.data.recordings.Count
    } catch { return -1 }
}

function Get-AgentRecorderPids {
    return @(Get-Process -Name "AgentRecorder.App" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
}

function Get-AgentRecorderHeadlessPids {
    return @(Get-Process -Name "AgentRecorder.Headless" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
}

function Get-AllAgentRecorderPids {
    return @((Get-AgentRecorderPids) + (Get-AgentRecorderHeadlessPids)) | Sort-Object -Unique
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

function Show-ServerDiagnostics {
    Write-Info "--- server process diagnostics ---"
    if ($script:starterProcess) {
        $exited = $false
        try { $exited = $script:starterProcess.HasExited } catch {}
        if ($exited) {
            Write-Info "Server process has exited (exit code $($script:starterProcess.ExitCode))"
        } else {
            Write-Info "Server process is still running (PID=$($script:starterProcess.Id))"
        }
    }

    $auditLog = Join-Path $ProjectRoot ".local-data\logs\audit.jsonl"
    if (Test-Path $auditLog) {
        Write-Info "--- audit log tail (last 20 lines) ---"
        Get-Content $auditLog -Tail 20 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host "(audit log not found)"
    }

    $startupErrLog = Join-Path $ProjectRoot ".local-data\logs\startup-errors.jsonl"
    if (Test-Path $startupErrLog) {
        Write-Info "--- startup error log tail (last 20 lines) ---"
        Get-Content $startupErrLog -Tail 20 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host "(startup error log not found)"
    }
}

# ----------------------------------------------------------------------------
# Server lifecycle helpers
# ----------------------------------------------------------------------------
function Stop-ServerIfStartedByUs {
    if (-not $script:serverStartedByUs) {
        Write-Info "Server was not started by this script; leaving existing server running"
        return
    }

    Write-Info "Stopping server started by this script..."

    # Stop the starter process if it is still around.
    if ($script:starterProcess -and -not $script:starterProcess.HasExited) {
        try {
            $script:starterProcess.Kill()
            $null = $script:starterProcess.WaitForExit(3000)
            Write-Info "Starter process (PID=$($script:starterProcess.Id)) stopped"
        } catch {
            Record-Failure "Failed to stop starter process (PID=$($script:starterProcess.Id)): $_"
        }
    }

    # Wait a moment for process teardown and then recompute the set of
    # AgentRecorder* processes that appeared because of this script run.
    Start-Sleep -Seconds 1

    $currentPids = Get-AllAgentRecorderPids
    $newPids = $currentPids | Where-Object { $script:baselinePids -notcontains $_ }
    Write-Info "Baseline PIDs: $($script:baselinePids -join ', ')"
    Write-Info "Current PIDs: $($currentPids -join ', ')"
    Write-Info "New PIDs to stop: $($newPids -join ', ')"

    $failed = $false
    foreach ($serverPid in $newPids) {
        $proc = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
        if (-not $proc) { continue }
        try {
            $proc | Stop-Process -Force
            Write-Info "Stopped AgentRecorder process PID=$serverPid (name=$($proc.ProcessName))"
        } catch {
            $failed = $true
            Record-Failure "Failed to stop AgentRecorder process PID=$serverPid : $_"
        }
    }

    Start-Sleep -Seconds 1

    # Verify that no new process remains. Pre-existing residuals are ignored.
    $remaining = @()
    $currentAfter = Get-AllAgentRecorderPids
    foreach ($serverPid in $newPids) {
        if ($currentAfter -contains $serverPid) { $remaining += $serverPid }
    }

    if ($remaining.Count -gt 0 -or $failed) {
        Record-Failure "Server cleanup incomplete; remaining new PIDs: $($remaining -join ', ')"
        Show-ServerDiagnostics
    } else {
        Write-Ok "Server started by this script has been stopped"
    }
}

# ----------------------------------------------------------------------------
# Main tests
# ----------------------------------------------------------------------------
try {

# ----------------------------------------------------------------------------
# Test 1: API must be reachable (auto-start server if requested)
# ----------------------------------------------------------------------------
Write-Step "Reachability check"
$ping = Invoke-ApiGet "/api/v1/capabilities"
$script:serverStartedByUs = $false
$script:starterProcess = $null
$script:baselinePids = @()

if ($ping.status -ne 200 -and $AutoStartServer) {
    Write-Host "API not reachable; auto-starting headless API host..." -ForegroundColor Yellow

    # Remember any AgentRecorder* processes (App and Headless) that existed before we started.
    $script:baselinePids = Get-AllAgentRecorderPids

    $serverExe = Join-Path $ProjectRoot "src\AgentRecorder.Headless\bin\Release\net8.0-windows10.0.19041.0\AgentRecorder.Headless.exe"
    if (-not (Test-Path $serverExe)) {
        Record-Failure "Headless API host executable not found at $serverExe. Run 'dotnet build AgentRecorder.sln -c Release' first."
        exit 1
    }

    # Use .NET ProcessStartInfo with UseShellExecute=true + WindowStyle=Hidden so the
    # headless host survives the parent PowerShell script exit. Pass configuration via
    # command-line arguments instead of environment variables, avoiding PowerShell 5.1
    # duplicate Path/PATH issues in the environment provider.
    $arguments = @(
        "--data-dir", (Join-Path $ProjectRoot ".local-data")
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $serverExe
    $psi.Arguments = Format-Arguments -Arguments $arguments
    $psi.WorkingDirectory = $ProjectRoot
    $psi.UseShellExecute = $true
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden

    try {
        $script:starterProcess = [System.Diagnostics.Process]::Start($psi)
    } catch {
        Record-Failure "Failed to start headless API host process: $_"
        Show-ServerDiagnostics
        exit 1
    }

    $script:serverStartedByUs = $true
    Write-Info "Headless API host started by this script (starter PID=$($script:starterProcess.Id))"

    # Wait for the server to become healthy, with bounded total wait and per-request timeout.
    $maxWaitSeconds = 120
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $healthy = $false
    while ($sw.Elapsed.TotalSeconds -lt $maxWaitSeconds) {
        Start-Sleep -Seconds 1
        # Also detect early process exit so we do not wait the full 120s.
        if ($script:starterProcess -and $script:starterProcess.HasExited) {
            break
        }
        $ping = Invoke-ApiGet "/api/v1/capabilities"
        if ($ping.status -eq 200) { $healthy = $true; break }
    }
    $sw.Stop()

    if (-not $healthy) {
        if ($script:starterProcess -and $script:starterProcess.HasExited) {
            Record-Failure "Headless API host process exited before becoming healthy (exit code $($script:starterProcess.ExitCode))."
        } else {
            Record-Failure "Headless API host did not become healthy within ${maxWaitSeconds}s (last status $($ping.status))."
        }
        Show-ServerDiagnostics
        # Cleanup will run in finally; then we exit non-zero.
        exit 1
    }

    Write-Ok "Headless API host became healthy after $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
}

if ($ping.status -ne 200) {
    Record-Failure "API not reachable at $BaseUrl (status $($ping.status)). Start the server manually or re-run with -AutoStartServer."
    exit 1
}
Write-Ok "API reachable"

# ----------------------------------------------------------------------------
# Test 2: Capabilities must not advertise continuous / WGC stream
# ----------------------------------------------------------------------------
Write-Step "Capabilities must not advertise continuous / WGC stream"
$cap = Invoke-ApiGet "/api/v1/capabilities"
$badTokens = @("wgc_continuous", "continuous_recording", "WGC_D3D11_FRAME_STREAM")
$found = @()
foreach ($t in $badTokens) {
    if (Body-Contains $cap.body $t) { $found += $t }
}
if ($found.Count -eq 0) {
    Write-Ok "Capabilities do not advertise continuous features"
} else {
    Record-Failure "Capabilities advertised forbidden tokens: $($found -join ', ')"
}

# ----------------------------------------------------------------------------
# Test 3: POST /recordings with continuous markers must return 400 UNSUPPORTED_FEATURE
# ----------------------------------------------------------------------------
Write-Step "POST /recordings continuous markers rejected"

$beforeCount = Get-RecordingCount
if ($beforeCount -lt 0) {
    Record-Failure "Could not read recording list before continuous marker tests"
}

$continuousCases = @(
    @{ name = "capture_kind=continuous"; field = "capture_kind"; value = "continuous"; body = @{ capture_kind = "continuous"; source = @{ type = "window"; window_id = "window_999999999" } } },
    @{ name = "recording_mode=continuous"; field = "recording_mode"; value = "continuous"; body = @{ recording_mode = "continuous"; source = @{ type = "window"; window_id = "window_999999999" } } },
    @{ name = "capture_method=WGC_D3D11_FRAME_STREAM"; field = "capture_method"; value = "WGC_D3D11_FRAME_STREAM"; body = @{ capture_method = "WGC_D3D11_FRAME_STREAM"; source = @{ type = "window"; window_id = "window_999999999" } } },
    @{ name = "backend=wgc_continuous"; field = "backend"; value = "wgc_continuous"; body = @{ backend = "wgc_continuous"; source = @{ type = "window"; window_id = "window_999999999" } } },
    @{ name = "backend=wgc-continuous"; field = "backend"; value = "wgc-continuous"; body = @{ backend = "wgc-continuous"; source = @{ type = "window"; window_id = "window_999999999" } } }
)

foreach ($case in $continuousCases) {
    $r = Invoke-ApiPost "/api/v1/recordings" $case.body
    $hasCode = Body-HasCode $r.body "UNSUPPORTED_FEATURE"
    $hasField = Body-Contains $r.body $case.field
    $hasValue = Body-Contains $r.body $case.value
    if ($r.status -eq 400 -and $hasCode -and $hasField -and $hasValue) {
        Write-Ok "$($case.name) rejected with 400 UNSUPPORTED_FEATURE"
    } else {
        Record-Failure "$($case.name): expected 400 UNSUPPORTED_FEATURE, got status $($r.status); body=$($r.body)"
    }
}

$afterCount = Get-RecordingCount
if ($afterCount -eq $beforeCount) {
    Write-Ok "Rejected continuous requests did not create recordings"
} else {
    Record-Failure "Recording count changed after continuous marker tests: before=$beforeCount after=$afterCount"
}

# ----------------------------------------------------------------------------
# Test 4: HTTP self-approval still blocked
# ----------------------------------------------------------------------------
Write-Step "POST /confirmations/test-id/approve still blocked"
$approve = Invoke-ApiPost "/api/v1/confirmations/test-id/approve" @{}
if ($approve.status -eq 405) {
    Write-Ok "Self-approval returns 405"
} else {
    Record-Failure "Self-approval expected 405, got $($approve.status)"
}

# ----------------------------------------------------------------------------
# Test 5: No dedicated continuous API routes
# ----------------------------------------------------------------------------
Write-Step "Dedicated continuous API routes must not return 2xx"
$routeCases = @(
    @{ method = "GET"; path = "/api/v1/wgc/continuous" },
    @{ method = "POST"; path = "/api/v1/wgc/continuous" },
    @{ method = "GET"; path = "/api/v1/recordings/test-id/events" }
)
foreach ($rc in $routeCases) {
    $uri = ($BaseUrl.TrimEnd('/') + $rc.path)
    $outFile = [System.IO.Path]::GetTempFileName()
    $bodyFile = $null
    try {
        $curlArgs = @("-s", "-o", $outFile, "-w", "%{http_code}", "-H", "X-Agent-Recorder-Key: $ApiKey", "-X", $rc.method)
        if ($rc.method -eq "POST") {
            $bodyFile = [System.IO.Path]::GetTempFileName()
            "{}" | Set-Content -Path $bodyFile -Encoding UTF8 -NoNewline
            $curlArgs += @("-H", "Content-Type: application/json", "-d", "@$bodyFile")
        }
        $httpCode = curl.exe @curlArgs $uri
        $code = [int]$httpCode
    } catch {
        $code = -1
    } finally {
        if (Test-Path $outFile) { Remove-Item $outFile -Force -ErrorAction SilentlyContinue }
        if ($bodyFile -and (Test-Path $bodyFile)) { Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue }
    }
    if ($code -ge 200 -and $code -lt 300) {
        Record-Failure "$($rc.method) $($rc.path) returned 2xx ($code)"
    } else {
        Write-Ok "$($rc.method) $($rc.path) not 2xx ($code)"
    }
}

# ----------------------------------------------------------------------------
# Test 6: Source-code production boundary static check
# ----------------------------------------------------------------------------
Write-Step "Source-code production boundary static check"

function Check-NoMatch([string]$label, [string]$pattern, [string[]]$paths) {
    $hits = @()
    foreach ($p in $paths) {
        $full = Join-Path $ProjectRoot $p
        if (Test-Path $full) {
            $hit = rg -n -e $pattern $full 2>$null
            if ($hit) { $hits += $hit }
        }
    }
    if ($hits.Count -eq 0) {
        Write-Ok "${label}: no matches"
    } else {
        Record-Failure "${label}: found matches:`n$($hits -join "`n")"
    }
}

Check-NoMatch "No IWgcContinuousCaptureBackend in src" "IWgcContinuousCaptureBackend" @("src\AgentRecorder.Api", "src\AgentRecorder.Core", "src\AgentRecorder.Capture")
Check-NoMatch "No StartContinuous in src" "StartContinuous" @("src\AgentRecorder.Api", "src\AgentRecorder.Core", "src\AgentRecorder.Capture")
Check-NoMatch "No WgcContinuousCaptureBackend in src" "WgcContinuousCaptureBackend" @("src\AgentRecorder.Api", "src\AgentRecorder.Core", "src\AgentRecorder.Capture")
Check-NoMatch "No --capture-session-window in src" "--capture-session-window" @("src\AgentRecorder.Api", "src\AgentRecorder.Core", "src\AgentRecorder.Capture")
Check-NoMatch "No --capture-session-window in helper" "--capture-session-window" @("tools\wgc-native-helper\src")

$helperSrc = Join-Path $ProjectRoot "tools\wgc-native-helper\src\main.cpp"
if (Test-Path $helperSrc) {
    $sample = rg -n -e "--emit-continuous-ipc-v2-sample" $helperSrc 2>$null
    if ($sample) {
        Write-Ok "helper still contains --emit-continuous-ipc-v2-sample sample emitter"
    } else {
        Record-Failure "helper missing --emit-continuous-ipc-v2-sample sample emitter"
    }
} else {
    Record-Failure "helper source not found at $helperSrc"
}

} finally {
    # ----------------------------------------------------------------------------
    # Cleanup: stop the server only if this script started it
    # ----------------------------------------------------------------------------
    Stop-ServerIfStartedByUs
}

# ----------------------------------------------------------------------------
# Summary
# ----------------------------------------------------------------------------
Write-Step "Summary"
if ($script:failures.Count -eq 0) {
    Write-Host "PUBLIC BOUNDARY TEST: ALL PASSED" -ForegroundColor Green
    exit 0
} else {
    Write-Host "PUBLIC BOUNDARY TEST: FAILURES ($($script:failures.Count))" -ForegroundColor Red
    foreach ($f in $script:failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}
