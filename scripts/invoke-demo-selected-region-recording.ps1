#Requires -Version 5.1
<#
.SYNOPSIS
    Demo script for selected region recording with local user confirmation.

.DESCRIPTION
    - Optionally starts demo app if API not reachable
    - Enumerates displays and validates desktop visibility
    - Triggers region selection via POST /api/v1/region-selections
    - Creates recording with region source, waits for local confirmation
    - Polls completion, verifies output via ffprobe
    - Writes structured demo run report to .local-data/demo-runs/
    - Validates cleanup: lock file, port listening, process state

.PARAMETER DurationSeconds
    Recording duration in seconds (default: 10).

.PARAMETER Fps
    Frames per second (default: 15). Must be 15, 24, 30, or 60.

.PARAMETER Quality
    Video quality: low, medium, high (default: medium).

.PARAMETER SelectionTimeoutSeconds
    Max seconds to wait for region selection (default: 120).

.PARAMETER SkipBuild
    Skip build step when starting demo app.

.PARAMETER KeepServer
    Do not stop the demo app after recording.

.PARAMETER WaitForCompletionSeconds
    Max seconds to wait for recording to complete (default: auto).

.PARAMETER BaseUrl
    API base URL (default: http://127.0.0.1:37891).

.EXAMPLE
    .\scripts\invoke-demo-selected-region-recording.ps1 -DurationSeconds 10
    # Record a user-selected region for 10 seconds

.EXAMPLE
    .\scripts\invoke-demo-selected-region-recording.ps1 -DurationSeconds 30 -SkipBuild
    # 30-second region recording, skip build
#>

param(
    [int]$DurationSeconds = 10,
    [int]$Fps = 15,
    [ValidateSet("low", "medium", "high")]
    [string]$Quality = "medium",
    [int]$SelectionTimeoutSeconds = 120,
    [switch]$SkipBuild = $false,
    [switch]$KeepServer = $false,
    [int]$WaitForCompletionSeconds = 0,
    [string]$BaseUrl = "http://127.0.0.1:37891"
)

if ($WaitForCompletionSeconds -le 0) {
    $WaitForCompletionSeconds = [Math]::Max(120, $DurationSeconds + 30)
}

$ErrorActionPreference = "Stop"
$ProjectRoot = "D:\works\python\007-Agent-Recorder"
$ScriptDir = Join-Path $ProjectRoot "scripts"
$DataDir = "D:\works\python\007-Agent-Recorder\.local-data"
$DemoRunsDir = Join-Path $DataDir "demo-runs"
$VideosDir = Join-Path $DataDir "Videos\demo"
$ToolsDir = Join-Path $ProjectRoot "tools"
$ApiPrefix = "$BaseUrl/api/v1"
$Port = 37891
$StaleAppPid = 49364

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$manifestPath = Join-Path $DemoRunsDir "selected-region-demo-$timestamp.json"
$runnerReportPath = Join-Path $DemoRunsDir "demo-record-region-$timestamp.json"

$script:StartedByScript = $false
$script:ApiKey = $null
$script:FinalStatus = "unhandled_error"
$script:BlockerType = ""
$script:BlockerDetail = ""
$script:SelectedRegion = $null
$script:RecordingId = ""
$script:ConfirmationId = ""
$script:ConfirmationStatus = ""
$script:Mp4Path = $null
$script:DurationActual = $null
$script:Width = 0
$script:Height = 0
$script:FfmpegCommandArgs = ""
$script:RecordingRequest = $null

$script:SuccessCriteria = @{
    recording_completed = $false
    mp4_exists = $false
    size_non_zero = $false
}

# ============ Timing Variables ============
$script:Timings = @{
    total_elapsed_ms = 0
    load_api_key_ms = 0
    ensure_api_reachable_ms = 0
    start_app_ms = 0
    enumerate_displays_ms = 0
    region_selection_wait_ms = 0
    create_recording_http_ms = 0
    confirmation_wait_ms = 0
    recording_start_detect_ms = 0
    recording_duration_ms = 0
    output_probe_ms = 0
    cleanup_ms = 0
}
$script:_t0 = $null  # global start time

# Context info recorded at API reachability check
$script:Context = @{
    api_already_running = $false
    host_mode = ""
    api_pid = $null
    session_id = $null
    # New fields for data directory unification (task 103)
    audit_log_path = ""
    data_dir = $DataDir
    app_start_method = ""  # script_started | existing_project_tray | external_tray
    api_process_path = ""
}

function Get-ElapsedMs {
    param([datetime]$Start)
    return [int]((Get-Date) - $Start).TotalMilliseconds
}

# ============ Helper Functions ============

function Write-Header {
    param([string]$Title)
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

function Write-Step {
    param([string]$Name)
    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
}

function Add-Error {
    param([string]$Msg)
    Write-Host "[ERROR] $Msg" -ForegroundColor Red
}

function Add-Warning {
    param([string]$Msg)
    Write-Host "[WARN] $Msg" -ForegroundColor Yellow
}

function Set-Blocker {
    param([string]$Type, [string]$Detail)
    $script:BlockerType = $Type
    $script:BlockerDetail = $Detail
}

function Get-ApiKey {
    $keyFile = Join-Path $DataDir "config\api-key.txt"
    if (Test-Path $keyFile) {
        $key = Get-Content $keyFile -Raw -ErrorAction SilentlyContinue
        if ($key) { return $key.Trim() }
    }
    return $null
}

function Test-ApiReachable {
    try {
        $r = Invoke-WebRequest "$ApiPrefix/capabilities" -UseBasicParsing -TimeoutSec 3
        return ($r.StatusCode -eq 200)
    } catch {
        return $false
    }
}

function Get-Capabilities {
    try {
        $r = Invoke-WebRequest "$ApiPrefix/capabilities" -UseBasicParsing -TimeoutSec 5
        if ($r.StatusCode -eq 200) {
            return ($r.Content | ConvertFrom-Json).data
        }
        return $null
    } catch {
        return $null
    }
}

function Start-DemoApp {
    Write-Host "[INFO] API not reachable; starting demo app..." -ForegroundColor Cyan
    $startScript = Join-Path $ScriptDir "start-demo-app.ps1"
    if (-not (Test-Path $startScript)) {
        Add-Error "start-demo-app.ps1 not found: $startScript"
        return $false
    }

    $args = @()
    if ($SkipBuild) { $args += "-SkipBuild" }

    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $startScript $args 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        Add-Error "Failed to start demo app (exit=$exitCode)"
        foreach ($line in $output) {
            Write-Host "  $line" -ForegroundColor Gray
        }
        return $false
    }

    $script:StartedByScript = $true
    return $true
}

function Stop-DemoAppIfStarted {
    if (-not $script:StartedByScript -or $KeepServer) { return }
    Write-Host "[INFO] Stopping demo app (started by this script)..." -ForegroundColor Cyan
    $stopScript = Join-Path $ScriptDir "stop-demo-app.ps1"
    if (Test-Path $stopScript) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript 2>&1 | Out-Null
    }
}

function Invoke-Ffprobe {
    param([string]$Mp4Path)

    $ffprobe = Join-Path $ToolsDir "ffmpeg\bin\ffprobe.exe"
    if (-not (Test-Path $ffprobe)) {
        $ffprobeInPath = Get-Command ffprobe -ErrorAction SilentlyContinue
        if ($ffprobeInPath) {
            $ffprobe = $ffprobeInPath.Source
        } else {
            Write-Host "[WARN] ffprobe not found" -ForegroundColor Yellow
            return $null
        }
    }

    try {
        $output = & $ffprobe -v quiet -print_format json -show_format -show_streams $Mp4Path 2>&1
        $json = $output | ConvertFrom-Json -ErrorAction Stop

        $result = @{
            duration = 0
            size = 0
            width = 0
            height = 0
            fps = 0
            codec = ""
            container = ""
        }

        if ($json.format) {
            if ($json.format.duration) { $result.duration = [double]$json.format.duration }
            if ($json.format.size) { $result.size = [long]$json.format.size }
            if ($json.format.format_name) { $result.container = $json.format.format_name }
        }

        $videoStream = $json.streams | Where-Object { $_.codec_type -eq "video" } | Select-Object -First 1
        if ($videoStream) {
            if ($videoStream.width) { $result.width = [int]$videoStream.width }
            if ($videoStream.height) { $result.height = [int]$videoStream.height }
            if ($videoStream.codec_name) { $result.codec = $videoStream.codec_name }
            if ($videoStream.r_frame_rate) {
                $fpsStr = $videoStream.r_frame_rate
                if ($fpsStr -match "^(\d+)/(\d+)$") {
                    $result.fps = [math]::Round([int]$matches[1] / [int]$matches[2], 2)
                } elseif ($fpsStr -match "^(\d+)$") {
                    $result.fps = [int]$fpsStr
                }
            }
        }

        return $result
    } catch {
        Write-Host "[WARN] ffprobe parse error: $($_.Exception.Message)" -ForegroundColor Yellow
        return $null
    }
}

function Get-Displays {
    $headers = @{}
    if ($script:ApiKey) { $headers["X-Agent-Recorder-Key"] = $script:ApiKey }
    $url = "$ApiPrefix/displays"
    try {
        $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 10 -Headers $headers
        $json = $r.Content | ConvertFrom-Json
        if ($json.ok -eq $true -and $json.data.displays) {
            return @($json.data.displays)
        }
        return @()
    } catch {
        Add-Error "Failed to get displays: $($_.Exception.Message)"
        return $null
    }
}

function Get-WindowsCount {
    $headers = @{}
    if ($script:ApiKey) { $headers["X-Agent-Recorder-Key"] = $script:ApiKey }
    $url = "$ApiPrefix/windows?include_minimized=false&include_system_windows=false"
    try {
        $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 10 -Headers $headers
        $json = $r.Content | ConvertFrom-Json
        if ($json.ok -eq $true -and $json.data.windows) {
            return @($json.data.windows).Count
        }
        return 0
    } catch {
        return -1
    }
}

function Get-NetstatInfo {
    $info = @{
        port_listening = $false
        listener_pid = $null
        listener_process_name = $null
        listener_session_id = $null
        current_shell_session_id = $null
    }

    try {
        $info.current_shell_session_id = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    } catch {
        $info.current_shell_session_id = -1
    }

    $netstat = netstat -ano | Select-String ":$Port"
    foreach ($line in $netstat) {
        if ($line -match "\s+LISTENING\s+(\d+)") {
            $info.port_listening = $true
            $info.listener_pid = [int]$matches[1]
            try {
                $proc = Get-Process -Id $info.listener_pid -ErrorAction SilentlyContinue
                if ($proc) {
                    $info.listener_process_name = $proc.ProcessName
                    $info.listener_session_id = $proc.SessionId
                }
            } catch { }
            break
        }
    }

    return $info
}

function Test-PhysicalDisplaysExist {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $screens = [System.Windows.Forms.Screen]::AllScreens
        return ($screens.Count -gt 0)
    } catch {
        return $null
    }
}

function Get-SessionType {
    $isInteractive = [Environment]::UserInteractive
    $sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    return @{
        user_interactive = $isInteractive
        session_id = $sessionId
    }
}

function Invoke-RegionSelection {
    param([int]$TimeoutSeconds)

    $headers = @{ "X-Agent-Recorder-Key" = $script:ApiKey }
    $url = "$ApiPrefix/region-selections"
    $body = @{
        purpose = "recording"
        timeout_seconds = $TimeoutSeconds
    } | ConvertTo-Json -Depth 5

    Write-Host "[INFO] Triggering region selection via API..." -ForegroundColor Cyan
    Write-Host "[INFO] A region selection overlay will appear on screen." -ForegroundColor Yellow
    Write-Host "[INFO] Click and drag to select a region, then confirm." -ForegroundColor Yellow

    try {
        $r = Invoke-WebRequest $url -Method Post -UseBasicParsing -TimeoutSec ($TimeoutSeconds + 30) `
            -Headers $headers -Body $body -ContentType "application/json"
        $json = $r.Content | ConvertFrom-Json
        if ($json.ok -eq $true) {
            return $json.data
        }
        Add-Error "Region selection failed: $($r.Content)"
        return $null
    } catch {
        if ($_.Exception.Response) {
            try {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                $resp = $reader.ReadToEnd()
                Add-Error "Region selection API error: $resp"
            } catch { }
        } else {
            Add-Error "Region selection exception: $($_.Exception.Message)"
        }
        return $null
    }
}

function Start-RegionRecording {
    param($Region)

    $body = [ordered]@{
        source = [ordered]@{
            type = "region"
            display_id = $Region.display_id
            coordinate_space = $Region.coordinate_space
            bounds = [ordered]@{
                x = $Region.bounds.x
                y = $Region.bounds.y
                width = $Region.bounds.width
                height = $Region.bounds.height
            }
        }
        audio = @{
            microphone = @{ enabled = $false }
        }
        video = @{
            fps = $Fps
            quality = $Quality
        }
        stop_condition = @{
            type = "duration"
            seconds = $DurationSeconds
        }
        output = @{
            directory = $VideosDir
            filename_template = "region-{datetime}"
        }
    }

    $bodyJson = $body | ConvertTo-Json -Depth 10
    $script:RecordingRequest = $body

    $headers = @{ "X-Agent-Recorder-Key" = $script:ApiKey }
    $url = "$ApiPrefix/recordings"
    try {
        $r = Invoke-WebRequest $url -Method Post -UseBasicParsing -TimeoutSec 30 `
            -Headers $headers -Body $bodyJson -ContentType "application/json"
        $json = $r.Content | ConvertFrom-Json
        if ($json.ok -eq $true) {
            return $json.data
        }
        Add-Error "Recording creation failed: $($r.Content)"
        return $null
    } catch {
        Add-Error "Recording creation exception: $($_.Exception.Message)"
        return $null
    }
}

function Get-ConfirmationStatus {
    param($ConfirmationId)
    $headers = @{ "X-Agent-Recorder-Key" = $script:ApiKey }
    $url = "$ApiPrefix/confirmations/$ConfirmationId"
    try {
        $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 10 -Headers $headers
        $json = $r.Content | ConvertFrom-Json
        if ($json.ok -eq $true) { return $json.data }
        return $null
    } catch { return $null }
}

function Get-RecordingStatus {
    param($RecordingId)
    $headers = @{ "X-Agent-Recorder-Key" = $script:ApiKey }
    $url = "$ApiPrefix/recordings/$RecordingId"
    try {
        $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 10 -Headers $headers
        $json = $r.Content | ConvertFrom-Json
        if ($json.ok -eq $true) { return $json.data }
        return $null
    } catch { return $null }
}

function Get-RecordingOutput {
    param($RecordingId)
    $headers = @{ "X-Agent-Recorder-Key" = $script:ApiKey }
    $url = "$ApiPrefix/recordings/$RecordingId/output"
    try {
        $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 10 -Headers $headers
        $json = $r.Content | ConvertFrom-Json
        if ($json.ok -eq $true) { return $json.data }
        return $null
    } catch { return $null }
}

function Get-CleanupStatus {
    # Use correct lock path
    $lockDir = Join-Path $DataDir "locks"
    $lockPath = Join-Path $lockDir "agent-recorder.lock"
    $lockExists = Test-Path $lockPath

    $netstat = netstat -ano | Select-String ":$Port" | Select-String "LISTENING"
    $portFree = $null -eq $netstat

    $appRunning = Get-Process AgentRecorder.App -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $StaleAppPid }
    $appStopped = $null -eq $appRunning

    return @{
        app_stopped = $appStopped
        port_free = $portFree
        lock_released = -not $lockExists
    }
}

function Write-Manifest {
    $cleanup = Get-CleanupStatus

    $manifest = [ordered]@{
        status = $script:FinalStatus
        blocker_type = $script:BlockerType
        blocker_detail = $script:BlockerDetail
        selected_region = $script:SelectedRegion
        recording_id = $script:RecordingId
        confirmation_id = $script:ConfirmationId
        confirmation_status = $script:ConfirmationStatus
        mp4_path = $script:Mp4Path
        duration_requested = $DurationSeconds
        duration_actual = $script:DurationActual
        width = $script:Width
        height = $script:Height
        ffmpeg_command_args = $script:FfmpegCommandArgs
        evidence = @{
            runner_report = $runnerReportPath
        }
        cleanup = $cleanup
        success_criteria = $script:SuccessCriteria
        timestamp = (Get-Date).ToUniversalTime().ToString("o")
        parameters = @{
            duration_seconds = $DurationSeconds
            fps = $Fps
            quality = $Quality
            selection_timeout_seconds = $SelectionTimeoutSeconds
        }
    }

    if (-not (Test-Path $DemoRunsDir)) {
        New-Item -ItemType Directory -Path $DemoRunsDir -Force | Out-Null
    }

    $manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $manifestPath -Encoding UTF8
    Write-Host ""
    Write-Host "[INFO] Manifest: $manifestPath" -ForegroundColor Cyan

    return $manifestPath
}

function Write-RunnerReport {
    param([string]$Status, [hashtable]$Extra = @{})

    # Always refresh total_elapsed_ms from the global start time.
    $script:Timings.total_elapsed_ms = [int]((Get-Date) - $script:_t0).TotalMilliseconds

    $report = [ordered]@{
        started_at = $script:_t0.ToUniversalTime().ToString("o")
        mode = "region_record"
        status = $Status
        parameters = @{
            duration_seconds = $DurationSeconds
            fps = $Fps
            quality = $Quality
            selection_timeout_seconds = $SelectionTimeoutSeconds
        }
        recording_request = $script:RecordingRequest
        context = $script:Context
        timings = $script:Timings
    }

    foreach ($key in $Extra.Keys) {
        $report[$key] = $Extra[$key]
    }

    $report.ended_at = (Get-Date).ToUniversalTime().ToString("o")

    if (-not (Test-Path $DemoRunsDir)) {
        New-Item -ItemType Directory -Path $DemoRunsDir -Force | Out-Null
    }

    $report | ConvertTo-Json -Depth 10 | Set-Content -Path $runnerReportPath -Encoding UTF8
    Write-Host "[INFO] Runner report: $runnerReportPath" -ForegroundColor Gray
}

# ============ Main ============

Write-Header "Selected Region Recording Demo"

$script:_t0 = Get-Date
$startedAt = (Get-Date).ToUniversalTime().ToString("o")

try {
    # Step 0: Load API Key
    $stepStart = Get-Date
    Write-Step "Load API Key"
    $script:ApiKey = Get-ApiKey
    if (-not $script:ApiKey) {
        $script:FinalStatus = "preflight_not_ready"
        Set-Blocker -Type "api_key" -Detail "API key not found. Expected at $DataDir\config\api-key.txt"
        Add-Error $script:BlockerDetail
        Write-RunnerReport -Status "api_key_missing"
        Write-Manifest
        exit 1
    }
    $script:Timings.load_api_key_ms = Get-ElapsedMs -Start $stepStart
    Write-Host "[OK] API key loaded (length: $($script:ApiKey.Length))" -ForegroundColor Green

    # Step 1: Ensure API is reachable
    $stepStart = Get-Date
    Write-Step "Ensure API Reachable"
    if (-not (Test-ApiReachable)) {
        if (-not (Start-DemoApp)) {
            $script:FinalStatus = "preflight_not_ready"
            Set-Blocker -Type "api_unavailable" -Detail "Could not start demo app or API not reachable"
            Write-RunnerReport -Status "api_unavailable"
            Write-Manifest
            exit 1
        }
        if (-not (Test-ApiReachable)) {
            $script:FinalStatus = "preflight_not_ready"
            Set-Blocker -Type "api_unavailable" -Detail "Demo app started but API still not reachable"
            Write-RunnerReport -Status "api_unavailable"
            Write-Manifest
            exit 1
        }
        Write-Host "[OK] API became reachable after starting demo app" -ForegroundColor Green
    } else {
        $script:Context.api_already_running = $true
        Write-Host "[OK] API is already reachable" -ForegroundColor Green
    }
    $script:Timings.ensure_api_reachable_ms = Get-ElapsedMs -Start $stepStart

    # Capture context info after API becomes reachable
    $netstatInfo = Get-NetstatInfo
    $script:Context.api_pid = $netstatInfo.listener_pid
    $script:Context.session_id = $netstatInfo.current_shell_session_id

    # Determine app_start_method and capture process path
    $script:Context.api_process_path = ""
    if ($script:StartedByScript) {
        $script:Context.app_start_method = "script_started"
    } elseif ($netstatInfo.listener_pid) {
        try {
            $proc = Get-Process -Id $netstatInfo.listener_pid -ErrorAction SilentlyContinue
            if ($proc -and $proc.Path) {
                $script:Context.api_process_path = $proc.Path
                # Check if process path is within project directory
                $projectBinDir = Join-Path $ProjectRoot "src\AgentRecorder.App\bin"
                $headlessBinDir = Join-Path $ProjectRoot "src\AgentRecorder.Headless\bin"
                if ($proc.Path -like "$projectBinDir*" -or $proc.Path -like "$headlessBinDir*") {
                    $script:Context.app_start_method = "existing_project_tray"
                } else {
                    $script:Context.app_start_method = "external_tray"
                    Add-Warning "API is served by external tray (PID=$($netstatInfo.listener_pid)): $($proc.Path)"
                    Add-Warning "Audit logs may be written to a different data directory."
                    Write-Host "[WARN] Audit logs may be written to default %LocalAppData%\AgentRecorder" -ForegroundColor Yellow
                }
            } else {
                $script:Context.app_start_method = "unknown"
            }
        } catch {
            $script:Context.app_start_method = "unknown"
        }
    } else {
        $script:Context.app_start_method = "none"
    }

    # Set audit_log_path based on data_dir
    $script:Context.audit_log_path = Join-Path $script:Context.data_dir "logs\audit.jsonl"

    # Get host_mode from capabilities
    $caps = Get-Capabilities
    if ($caps -and $caps.host_mode) {
        $script:Context.host_mode = $caps.host_mode
    } elseif ($caps -and $caps.host) {
        $script:Context.host_mode = $caps.host
    }

    # Step 2: Enumerate displays and diagnose
    $stepStart = Get-Date
    Write-Step "Enumerate Displays"
    $displays = Get-Displays

    if ($null -eq $displays) {
        $script:Timings.enumerate_displays_ms = Get-ElapsedMs -Start $stepStart
        $script:FinalStatus = "preflight_not_ready"
        Set-Blocker -Type "api_error" -Detail "Failed to query /api/v1/displays endpoint"
        Write-RunnerReport -Status "displays_query_failed"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
        exit 1
    }

    $displayCount = $displays.Count
    Write-Host "[INFO] Found $displayCount display(s) via API" -ForegroundColor $(if ($displayCount -gt 0) { "Green" } else { "Yellow" })

    if ($displayCount -eq 0) {
        Write-Host ""
        Write-Host "========================================================" -ForegroundColor Yellow
        Write-Host "  DISPLAY ENUMERATION UNAVAILABLE" -ForegroundColor Yellow
        Write-Host "========================================================" -ForegroundColor Yellow
        Write-Host ""

        $netstatInfo = Get-NetstatInfo
        $windowsCount = Get-WindowsCount
        $physicalDisplays = Test-PhysicalDisplaysExist
        $sessionInfo = Get-SessionType

        Write-Host "  Diagnostic Information:" -ForegroundColor Yellow
        Write-Host "    Port listening: $($netstatInfo.port_listening)" -ForegroundColor Yellow
        if ($netstatInfo.listener_pid) {
            Write-Host "    Listener PID: $($netstatInfo.listener_pid)" -ForegroundColor Yellow
            Write-Host "    Listener process: $($netstatInfo.listener_process_name)" -ForegroundColor Yellow
            Write-Host "    Listener SessionId: $($netstatInfo.listener_session_id)" -ForegroundColor Yellow
        }
        Write-Host "    Current shell SessionId: $($netstatInfo.current_shell_session_id)" -ForegroundColor Yellow
        Write-Host "    Windows count (API): $windowsCount" -ForegroundColor Yellow
        Write-Host "    Physical displays (Forms): $physicalDisplays" -ForegroundColor Yellow
        Write-Host "    UserInteractive: $($sessionInfo.user_interactive)" -ForegroundColor Yellow
        Write-Host ""

        $blockerDetails = @()
        $blockerType = "display_enumeration"

        if ($physicalDisplays -eq $false) {
            $blockerDetails += "No physical displays detected via System.Windows.Forms.Screen"
            $blockerType = "no_physical_displays"
        } elseif ($windowsCount -eq 0) {
            $blockerDetails += "API returns 0 windows AND 0 displays - likely headless/sandbox session"
            $blockerType = "headless_session"
        } elseif ($netstatInfo.listener_session_id -ne $netstatInfo.current_shell_session_id) {
            $blockerDetails += "API process SessionId ($($netstatInfo.listener_session_id)) != current shell SessionId ($($netstatInfo.current_shell_session_id))"
            $blockerType = "session_mismatch"
        } else {
            $blockerDetails += "API service process may not be running on interactive desktop"
            $blockerType = "non_interactive_service"
        }

        $blockerDetails += "Port $Port listening: $($netstatInfo.port_listening)"
        if ($netstatInfo.listener_pid) {
            $blockerDetails += "Listener PID: $($netstatInfo.listener_pid), Process: $($netstatInfo.listener_process_name), SessionId: $($netstatInfo.listener_session_id)"
        }
        $blockerDetails += "Current shell SessionId: $($netstatInfo.current_shell_session_id)"
        $blockerDetails += "Windows count via API: $windowsCount"

        $script:FinalStatus = "display_enumeration_unavailable"
        Set-Blocker -Type $blockerType -Detail ($blockerDetails -join "; ")

        $diagnostic = @{
            netstat = $netstatInfo
            windows_count = $windowsCount
            physical_displays_detected = $physicalDisplays
            session_info = $sessionInfo
        }
        Write-RunnerReport -Status "display_enumeration_unavailable" -Extra @{ diagnostic = $diagnostic }
        Write-Manifest

        if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
        exit 1
    }

    foreach ($d in $displays) {
        Write-Host "  - $($d.id): $($d.bounds.width)x$($d.bounds.height) at ($($d.bounds.x),$($d.bounds.y))" -ForegroundColor Gray
    }
    $script:Timings.enumerate_displays_ms = Get-ElapsedMs -Start $stepStart

    # Step 3: Region Selection via API
    $stepStart = Get-Date
    Write-Step "Region Selection"
    Write-Host ""
    Write-Host "========================================================" -ForegroundColor Yellow
    Write-Host "  ACTION REQUIRED: Select region" -ForegroundColor Yellow
    Write-Host "  A region selection overlay will appear." -ForegroundColor Yellow
    Write-Host "  Click and drag to select a region to record." -ForegroundColor Yellow
    Write-Host "  Waiting up to $SelectionTimeoutSeconds seconds..." -ForegroundColor Yellow
    Write-Host "========================================================" -ForegroundColor Yellow
    Write-Host ""

    $selectionResult = Invoke-RegionSelection -TimeoutSeconds $SelectionTimeoutSeconds
    $script:Timings.region_selection_wait_ms = Get-ElapsedMs -Start $stepStart

    if ($null -eq $selectionResult) {
        $script:FinalStatus = "selection_timeout"
        Set-Blocker -Type "selection" -Detail "Region selection API call failed or timed out"
        Write-RunnerReport -Status "selection_failed"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
        exit 1
    }

    $selStatus = $selectionResult.status
    Write-Host "[INFO] Selection status: $selStatus" -ForegroundColor Cyan

    if ($selStatus -eq "selection_cancelled") {
        $script:FinalStatus = "selection_cancelled"
        Set-Blocker -Type "selection" -Detail "User cancelled region selection"
        Write-RunnerReport -Status "selection_cancelled"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
        exit 1
    }

    if ($selStatus -eq "selection_timeout") {
        $script:FinalStatus = "selection_timeout"
        Set-Blocker -Type "selection" -Detail "Region selection timed out after $SelectionTimeoutSeconds seconds"
        Write-RunnerReport -Status "selection_timeout"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
        exit 1
    }

    if ($selStatus -eq "display_unavailable") {
        $script:FinalStatus = "no_display"
        Set-Blocker -Type "display" -Detail "Region selection reported display_unavailable: $($selectionResult.detail)"
        Write-RunnerReport -Status "display_unavailable"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
        exit 1
    }

    if ($selStatus -ne "selected") {
        $script:FinalStatus = "selection_timeout"
        Set-Blocker -Type "selection" -Detail "Unexpected selection status: $selStatus"
        Write-RunnerReport -Status "selection_unexpected_status"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
        exit 1
    }

    $script:SelectedRegion = @{
        display_id = $selectionResult.display_id
        coordinate_space = $selectionResult.coordinate_space
        bounds = @{
            x = $selectionResult.bounds.x
            y = $selectionResult.bounds.y
            width = $selectionResult.bounds.width
            height = $selectionResult.bounds.height
        }
    }

    $script:Width = $selectionResult.bounds.width
    $script:Height = $selectionResult.bounds.height

    Write-Host "[OK] Region selected:" -ForegroundColor Green
    Write-Host "     Display: $($selectionResult.display_id)"
    Write-Host "     Bounds: x=$($selectionResult.bounds.x), y=$($selectionResult.bounds.y), w=$($selectionResult.bounds.width), h=$($selectionResult.bounds.height)"

    # Step 4: Create recording
    $stepStart = Get-Date
    Write-Step "Create Recording"
    $createResult = Start-RegionRecording -Region $selectionResult
    $script:Timings.create_recording_http_ms = Get-ElapsedMs -Start $stepStart

    if (-not $createResult) {
        $script:FinalStatus = "recording_failed"
        Set-Blocker -Type "recording" -Detail "Failed to create recording"
        Write-RunnerReport -Status "recording_create_failed"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
        exit 1
    }

    if ($createResult.status -eq "requires_user_confirmation") {
        $script:ConfirmationId = $createResult.confirmation_id
        $script:ConfirmationStatus = "pending"
        Write-Host "[INFO] Recording requires user confirmation" -ForegroundColor Yellow
        Write-Host "[INFO] Confirmation ID: $($script:ConfirmationId)" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "========================================================" -ForegroundColor Yellow
        Write-Host "  ACTION REQUIRED: Confirm recording" -ForegroundColor Yellow
        Write-Host "  Click the recording confirmation in the system tray" -ForegroundColor Yellow
        Write-Host "  or the pop-up window to approve the recording." -ForegroundColor Yellow
        Write-Host "  Waiting up to 120 seconds..." -ForegroundColor Yellow
        Write-Host "========================================================" -ForegroundColor Yellow
        Write-Host ""

        $confWaitStart = Get-Date
        $deadline = (Get-Date).AddSeconds(120)
        $confFinalStatus = "timeout"

        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 400
            $conf = Get-ConfirmationStatus -ConfirmationId $script:ConfirmationId
            if ($conf) {
                $newStatus = $conf.status
                if ($newStatus -ne $script:ConfirmationStatus) {
                    $script:ConfirmationStatus = $newStatus
                    Write-Host "[INFO] Confirmation status: $newStatus" -ForegroundColor Cyan
                }

                if ($newStatus -eq "approved") {
                    $confFinalStatus = "approved"
                    $script:RecordingId = $conf.recording_id
                    break
                } elseif ($newStatus -eq "rejected") {
                    $confFinalStatus = "rejected"
                    break
                } elseif ($newStatus -eq "expired") {
                    $confFinalStatus = "expired"
                    break
                }
            }
        }

        $script:Timings.confirmation_wait_ms = Get-ElapsedMs -Start $confWaitStart

        if ($confFinalStatus -ne "approved") {
            if ($confFinalStatus -eq "rejected") {
                $script:FinalStatus = "confirmation_rejected"
                Set-Blocker -Type "confirmation" -Detail "Local user rejected recording confirmation"
            } elseif ($confFinalStatus -eq "expired") {
                $script:FinalStatus = "confirmation_expired"
                Set-Blocker -Type "confirmation" -Detail "Recording confirmation expired"
            } else {
                $script:FinalStatus = "confirmation_expired"
                Set-Blocker -Type "confirmation" -Detail "Recording confirmation timed out"
            }
            Write-RunnerReport -Status "confirmation_$confFinalStatus"
            Write-Manifest
            if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
            exit 1
        }

        Write-Host ""
        Write-Host "[OK] Recording approved! Recording ID: $($script:RecordingId)" -ForegroundColor Green
    } elseif ($createResult.status -eq "recording") {
        $script:RecordingId = $createResult.recording_id
        $script:ConfirmationStatus = "not_required"
        Write-Host "[OK] Recording started immediately: $($script:RecordingId)" -ForegroundColor Green
    } else {
        $script:FinalStatus = "recording_failed"
        Set-Blocker -Type "recording" -Detail "Unexpected recording status: $($createResult.status)"
        Write-RunnerReport -Status "unexpected_status"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
        exit 1
    }

    # Step 5: Wait for recording completion
    $stepStart = Get-Date
    Write-Step "Wait for Recording Completion"
    Write-Host "[INFO] Recording ID: $($script:RecordingId)" -ForegroundColor Cyan
    Write-Host "[INFO] Waiting up to $WaitForCompletionSeconds seconds..." -ForegroundColor Cyan

    $deadline = (Get-Date).AddSeconds($WaitForCompletionSeconds)
    $finalRecStatus = "timeout"

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $rec = Get-RecordingStatus -RecordingId $script:RecordingId
        if ($rec) {
            $newStatus = $rec.status

            if ($newStatus -eq "completed") {
                $finalRecStatus = "completed"
                break
            } elseif ($newStatus -eq "failed") {
                $finalRecStatus = "failed"
                if ($rec.error) { Add-Error "Recording failed: $($rec.error)" }
                break
            } elseif ($newStatus -eq "rejected" -or $newStatus -eq "expired" -or $newStatus -eq "cancelled") {
                $finalRecStatus = $newStatus
                break
            }

            if ($rec.elapsed_seconds) {
                Write-Progress -Activity "Recording in progress" -Status "$($rec.elapsed_seconds)s elapsed" -PercentComplete ([Math]::Min(100, [int]($rec.elapsed_seconds / $DurationSeconds * 100)))
            }
        }
    }
    Write-Progress -Activity "Recording" -Completed
    $script:Timings.recording_start_detect_ms = Get-ElapsedMs -Start $stepStart

    if ($finalRecStatus -ne "completed") {
        $script:FinalStatus = "recording_failed"
        Set-Blocker -Type "recording" -Detail "Recording did not complete successfully: $finalRecStatus"
        Write-RunnerReport -Status "recording_$finalRecStatus"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-DemoAppIfStarted }
        exit 1
    }

    Write-Host ""
    Write-Host "[OK] Recording completed!" -ForegroundColor Green

    # Step 6: Verify output
    $stepStart = Get-Date
    Write-Step "Verify Output"

    $output = Get-RecordingOutput -RecordingId $script:RecordingId
    if ($output -and $output.output) {
        $outPath = $output.output.path
        $script:Mp4Path = $outPath

        Write-Host "[INFO] Output file: $outPath" -ForegroundColor Cyan

        if (Test-Path $outPath) {
            $script:SuccessCriteria.mp4_exists = $true
            $fileSize = (Get-Item $outPath).Length
            Write-Host "[INFO] File size: $fileSize bytes"
            if ($fileSize -gt 512) {
                $script:SuccessCriteria.size_non_zero = $true
            }

            if ($output.output.command_args) {
                $script:FfmpegCommandArgs = $output.output.command_args
            }

            $ffprobeResult = Invoke-Ffprobe -Mp4Path $outPath
            if ($null -ne $ffprobeResult) {
                $script:DurationActual = $ffprobeResult.duration
                $script:Width = $ffprobeResult.width
                $script:Height = $ffprobeResult.height

                Write-Host "     Duration: $($ffprobeResult.duration) seconds" -ForegroundColor Gray
                Write-Host "     Resolution: $($ffprobeResult.width) x $($ffprobeResult.height)" -ForegroundColor Gray
                Write-Host "     FPS: $($ffprobeResult.fps)" -ForegroundColor Gray
                Write-Host "     Codec: $($ffprobeResult.codec)" -ForegroundColor Gray
                Write-Host "     Container: $($ffprobeResult.container)" -ForegroundColor Gray
            }

            $script:SuccessCriteria.recording_completed = $true
            $script:FinalStatus = "completed"

            Write-Host ""
            Write-Host "[OK] Recording verified successfully!" -ForegroundColor Green
        } else {
            Add-Error "Output file does not exist: $outPath"
            $script:FinalStatus = "recording_failed"
            Set-Blocker -Type "output" -Detail "MP4 file not found at reported path: $outPath"
        }
    } else {
        Add-Error "Failed to get recording output"
        $script:FinalStatus = "recording_failed"
        Set-Blocker -Type "output" -Detail "Failed to retrieve recording output from API"
    }
    $script:Timings.output_probe_ms = Get-ElapsedMs -Start $stepStart

    Write-RunnerReport -Status $script:FinalStatus -Extra @{
        output = if ($output) { $output.output } else { $null }
        ffprobe = if ($ffprobeResult) { $ffprobeResult } else { $null }
    }

    # Step 7: Cleanup check and write manifest
    $stepStart = Get-Date
    Write-Step "Cleanup Check"

    if ($script:StartedByScript -and -not $KeepServer) {
        Stop-DemoAppIfStarted
        Start-Sleep -Seconds 2
    }

    Write-Manifest
    $script:Timings.cleanup_ms = Get-ElapsedMs -Start $stepStart
    # Note: total_elapsed_ms is always recalculated inside Write-RunnerReport

    # Final summary
    Write-Host ""
    $finalColor = if ($script:FinalStatus -eq "completed") { "Green" } else { "Yellow" }
    Write-Host "============================================================" -ForegroundColor $finalColor
    Write-Host "  SELECTED REGION DEMO: $($script:FinalStatus.ToUpper())" -ForegroundColor $finalColor
    Write-Host "============================================================" -ForegroundColor $finalColor
    Write-Host ""

    if ($script:FinalStatus -eq "completed") {
        Write-Host "  Region: $($script:Width)x$($script:Height)" -ForegroundColor White
        Write-Host "  Duration requested: $DurationSeconds s" -ForegroundColor White
        Write-Host "  Duration actual: $($script:DurationActual) s" -ForegroundColor White
        Write-Host "  MP4: $($script:Mp4Path)" -ForegroundColor White
        Write-Host "  Manifest: $manifestPath" -ForegroundColor White
        Write-Host ""
        exit 0
    } else {
        Write-Host "  Status: $($script:FinalStatus)" -ForegroundColor Yellow
        Write-Host "  Blocker: $($script:BlockerType) - $($script:BlockerDetail)" -ForegroundColor Yellow
        Write-Host ""
        exit 1
    }

} catch {
    $script:FinalStatus = "unhandled_error"
    Set-Blocker -Type "unhandled" -Detail $_.Exception.Message

    try {
        Write-RunnerReport -Status "unhandled_error" -Extra @{
            error = $_.Exception.Message
            stack_trace = $_.ScriptStackTrace
        }
        Write-Manifest
    } catch { }

    if ($script:StartedByScript -and -not $KeepServer) {
        try { Stop-DemoAppIfStarted } catch { }
    }

    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host "  SELECTED REGION DEMO: UNHANDLED ERROR" -ForegroundColor Red
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Stack: $($_.ScriptStackTrace)" -ForegroundColor Gray
    Write-Host ""
    exit 1
}
