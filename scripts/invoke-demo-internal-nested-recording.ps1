#Requires -Version 5.1
<#
.SYNOPSIS
    Short rehearsal script for internal nested recording MVP demo.

.DESCRIPTION
    Demonstrates the nested recording MVP feature:
    1. Starts Agent Recorder tray app (if not running)
    2. Validates nested_recording_mvp support via GET /api/v1/capabilities
    3. Lists displays and selects the first one
    4. Creates outer recording (nested.role=outer, 30s, display source)
    5. Waits 5 seconds after user confirmation (auto-simulated)
    6. Creates inner recording (nested.role=inner, parent_recording_id=outer id, 10s, same display)
    7. Waits for both recordings to complete
    8. Generates manifest JSON with schema_version, task, status, outer_recording, inner_recording
    9. Outputs manifest path and results

.PARAMETER OuterDurationSeconds
    Outer recording duration in seconds (default: 30).

.PARAMETER InnerDurationSeconds
    Inner recording duration in seconds (default: 10).

.PARAMETER InnerStartDelaySeconds
    Seconds to wait after outer starts before starting inner (default: 5).

.PARAMETER Fps
    Frames per second (default: 15). Must be 15, 24, 30, or 60.

.PARAMETER Quality
    Video quality: low, medium, high (default: medium).

.PARAMETER SkipBuild
    Skip build step when starting tray app.

.PARAMETER KeepServer
    Do not stop the tray app after recording.

.PARAMETER BaseUrl
    API base URL (default: http://127.0.0.1:37891).

.PARAMETER AgentName
    Value for X-Agent-Name header (default: nested-recording-demo).

.EXAMPLE
    .\scripts\invoke-demo-internal-nested-recording.ps1
    # Run nested recording demo with defaults (outer=30s, inner=10s)

.EXAMPLE
    .\scripts\invoke-demo-internal-nested-recording.ps1 -OuterDurationSeconds 60 -InnerDurationSeconds 20 -SkipBuild
    # Custom durations, skip build
#>

param(
    [int]$OuterDurationSeconds = 30,
    [int]$InnerDurationSeconds = 10,
    [int]$InnerStartDelaySeconds = 5,
    [int]$Fps = 15,
    [ValidateSet("low", "medium", "high")]
    [string]$Quality = "medium",
    [ValidateSet("display", "region")]
    [string]$OuterSourceType = "display",
    [ValidateSet("display", "region")]
    [string]$InnerSourceType = "region",
    [int]$InnerSelectionTimeoutSeconds = 120,
    [ValidateSet("short", "formal", "short-region", "formal-region")]
    [string]$ManifestKind = "short",
    [switch]$SkipBuild = $false,
    [switch]$KeepServer = $false,
    [string]$BaseUrl = "http://127.0.0.1:37891",
    [string]$AgentName = "nested-recording-demo"
)

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
$manifestPath = Join-Path $DemoRunsDir "internal-nested-recording-$timestamp.json"

$script:StartedByScript = $false
$script:ApiKey = $null
$script:FinalStatus = "unhandled_error"
$script:BlockerType = ""
$script:BlockerDetail = ""

$script:OuterRecording = @{
    recording_id = ""
    status = ""
    video_path = ""
    duration = 0
    duration_actual = 0
    started_at = ""
    ended_at = ""
    tool = "agent_recorder"
    nested_role = "outer"
    source_type = $OuterSourceType
    display_id = ""
    confirmation_id = ""
    confirmation_status = ""
}

$script:InnerRecording = @{
    recording_id = ""
    status = ""
    video_path = ""
    duration = 0
    duration_actual = 0
    started_at = ""
    ended_at = ""
    tool = "agent_recorder"
    nested_role = "inner"
    source_type = $InnerSourceType
    display_id = ""
    selected_region = $null  # Will hold {display_id, coordinate_space, bounds}
    recording_request_source = $null  # Will hold the actual source object sent to API
    confirmation_id = ""
    confirmation_status = ""
    parent_recording_id = ""
}

$script:SuccessCriteria = @{
    outer_completed = $false
    inner_completed = $false
    outer_mp4_exists = $false
    inner_mp4_exists = $false
}

$script:Timings = @{
    total_elapsed_ms = 0
    load_api_key_ms = 0
    ensure_api_reachable_ms = 0
    start_app_ms = 0
    check_capabilities_ms = 0
    enumerate_displays_ms = 0
    outer_create_recording_ms = 0
    outer_confirmation_wait_ms = 0
    inner_start_delay_ms = 0
    inner_create_recording_ms = 0
    inner_confirmation_wait_ms = 0
    wait_both_completed_ms = 0
    output_probe_ms = 0
    cleanup_ms = 0
}
$script:_t0 = $null

$script:Context = @{
    api_already_running = $false
    host_mode = ""
    api_pid = $null
    session_id = $null
    audit_log_path = ""
    data_dir = $DataDir
    app_start_method = ""
    api_process_path = ""
}

function Get-ElapsedMs {
    param([datetime]$Start)
    return [int]((Get-Date) - $Start).TotalMilliseconds
}

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
    $envKey = $env:AGENT_RECORDER_API_KEY
    if ($envKey) { return $envKey.Trim() }
    return "test-key"
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

function Start-TrayApp {
    Write-Host "[INFO] API not reachable; starting tray app..." -ForegroundColor Cyan
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
        Add-Error "Failed to start tray app (exit=$exitCode)"
        foreach ($line in $output) {
            Write-Host "  $line" -ForegroundColor Gray
        }
        return $false
    }

    $script:StartedByScript = $true
    return $true
}

function Stop-TrayAppIfStarted {
    if (-not $script:StartedByScript -or $KeepServer) { return }
    Write-Host "[INFO] Stopping tray app (started by this script)..." -ForegroundColor Cyan
    $stopScript = Join-Path $ScriptDir "stop-demo-app.ps1"
    if (Test-Path $stopScript) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript 2>&1 | Out-Null
    }
}

function Get-ApiHeaders {
    $headers = @{
        "X-Agent-Name" = $AgentName
    }
    if ($script:ApiKey) {
        $headers["X-Agent-Recorder-Key"] = $script:ApiKey
    }
    return $headers
}

function Get-Displays {
    $headers = Get-ApiHeaders
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

function Invoke-RegionSelection {
    param([int]$TimeoutSeconds)

    $headers = Get-ApiHeaders
    $url = "$ApiPrefix/region-selections"
    $body = @{
        purpose = "recording"
        timeout_seconds = $TimeoutSeconds
    } | ConvertTo-Json -Depth 5

    Write-Host "[INFO] A region selection overlay will appear on screen." -ForegroundColor Yellow
    Write-Host "[INFO] Click and drag to select a region, then confirm." -ForegroundColor Yellow

    try {
        $r = Invoke-WebRequest $url -Method Post -UseBasicParsing -TimeoutSec ($TimeoutSeconds + 30) `
            -Headers $headers -Body $body -ContentType "application/json"
        $json = $r.Content | ConvertFrom-Json
        if ($json.ok -eq $true) {
            return $json.data  # Contains status, display_id, coordinate_space, bounds directly
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

function New-NestedRecording {
    param(
        [string]$SourceType,
        [string]$DisplayId = "",
        [object]$RegionSelection = $null,  # Contains display_id, coordinate_space, bounds
        [int]$DurationSeconds,
        [string]$NestedRole,
        [string]$ParentRecordingId = ""
    )

    $body = [ordered]@{
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
            filename_template = "nested-$NestedRole-{datetime}"
        }
        nested = [ordered]@{
            role = $NestedRole
        }
    }

    if ($SourceType -eq "display") {
        $body.source = [ordered]@{
            type = "display"
            display_id = $DisplayId
        }
    } elseif ($SourceType -eq "region" -and $RegionSelection) {
        # Region source uses display_id, coordinate_space, and bounds directly
        # (NOT region_selection_id - that requires a separate API implementation)
        $body.source = [ordered]@{
            type = "region"
            display_id = $RegionSelection.display_id
            coordinate_space = $RegionSelection.coordinate_space
            bounds = [ordered]@{
                x = $RegionSelection.bounds.x
                y = $RegionSelection.bounds.y
                width = $RegionSelection.bounds.width
                height = $RegionSelection.bounds.height
            }
        }
    }

    if ($NestedRole -eq "inner" -and $ParentRecordingId) {
        $body.nested["parent_recording_id"] = $ParentRecordingId
    }

    $bodyJson = $body | ConvertTo-Json -Depth 10
    $script:LastRecordingRequest = $body  # Save for manifest
    $headers = Get-ApiHeaders
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
        if ($_.Exception.Response) {
            try {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                $resp = $reader.ReadToEnd()
                Add-Error "Recording API error: $resp"
            } catch { }
        } else {
            Add-Error "Recording creation exception: $($_.Exception.Message)"
        }
        return $null
    }
}

function Get-ConfirmationStatus {
    param($ConfirmationId)
    $headers = Get-ApiHeaders
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
    $headers = Get-ApiHeaders
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
    $headers = Get-ApiHeaders
    $url = "$ApiPrefix/recordings/$RecordingId/output"
    try {
        $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 10 -Headers $headers
        $json = $r.Content | ConvertFrom-Json
        if ($json.ok -eq $true) { return $json.data }
        return $null
    } catch { return $null }
}

function Wait-ForConfirmation {
    param(
        [string]$ConfirmationId,
        [int]$TimeoutSeconds = 120
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $finalStatus = "timeout"
    $recordingId = ""

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $conf = Get-ConfirmationStatus -ConfirmationId $ConfirmationId
        if ($conf) {
            $state = $conf.status
            if ($state -eq "approved") {
                $finalStatus = "approved"
                $recordingId = $conf.recording_id
                break
            } elseif ($state -eq "rejected") {
                $finalStatus = "rejected"
                break
            } elseif ($state -eq "expired") {
                $finalStatus = "expired"
                break
            }
        }
    }

    return @{
        result = $finalStatus
        recording_id = $recordingId
    }
}

function Wait-ForRecordingCompleted {
    param(
        [string]$RecordingId,
        [int]$MaxWaitSeconds = 120
    )

    $deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
    $finalStatus = "timeout"

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $rec = Get-RecordingStatus -RecordingId $RecordingId
        if ($rec) {
            $state = $rec.status
            if ($state -eq "completed") {
                $finalStatus = "completed"
                break
            } elseif ($state -eq "failed") {
                $finalStatus = "failed"
                if ($rec.error) { Add-Error "Recording failed: $($rec.error)" }
                break
            } elseif ($state -eq "cancelled" -or $state -eq "rejected" -or $state -eq "expired") {
                $finalStatus = $state
                break
            }
        }
    }

    return $finalStatus
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

function Write-Manifest {
    $manifest = [ordered]@{
        schema_version = "1.0"
        task = "internal_nested_recording_mvp"
        status = $script:FinalStatus
        api_created_both_layers = $true
        external_recorder_used = $false
        audit_log_path = $script:Context.audit_log_path
        blockers = @()
        outer_recording = [ordered]@{
            tool = $script:OuterRecording.tool
            recording_id = $script:OuterRecording.recording_id
            status = $script:OuterRecording.status
            video_path = $script:OuterRecording.video_path
            duration_actual = $script:OuterRecording.duration_actual
            started_at = $script:OuterRecording.started_at
            ended_at = $script:OuterRecording.ended_at
            nested_role = $script:OuterRecording.nested_role
            source_type = $script:OuterRecording.source_type
            display_id = $script:OuterRecording.display_id
        }
        inner_recording = [ordered]@{
            tool = $script:InnerRecording.tool
            recording_id = $script:InnerRecording.recording_id
            status = $script:InnerRecording.status
            video_path = $script:InnerRecording.video_path
            duration_actual = $script:InnerRecording.duration_actual
            started_at = $script:InnerRecording.started_at
            ended_at = $script:InnerRecording.ended_at
            nested_role = $script:InnerRecording.nested_role
            source_type = $script:InnerRecording.source_type
            selected_region = $script:InnerRecording.selected_region
            recording_request_source = $script:InnerRecording.recording_request_source
            parent_recording_id = $script:InnerRecording.parent_recording_id
        }
        success_criteria = $script:SuccessCriteria
        context = $script:Context
        timings = $script:Timings
        timestamp = (Get-Date).ToUniversalTime().ToString("o")
        parameters = @{
            outer_duration_seconds = $OuterDurationSeconds
            inner_duration_seconds = $InnerDurationSeconds
            inner_start_delay_seconds = $InnerStartDelaySeconds
            fps = $Fps
            quality = $Quality
            outer_source_type = $OuterSourceType
            inner_source_type = $InnerSourceType
            inner_selection_timeout_seconds = $InnerSelectionTimeoutSeconds
            manifest_kind = $ManifestKind
            agent_name = $AgentName
        }
    }

    # Add blocker if present
    if ($script:BlockerType) {
        $manifest.blockers = @(@{
            type = $script:BlockerType
            detail = $script:BlockerDetail
        })
    }

    if (-not (Test-Path $DemoRunsDir)) {
        New-Item -ItemType Directory -Path $DemoRunsDir -Force | Out-Null
    }

    $manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $manifestPath -Encoding UTF8
    Write-Host ""
    Write-Host "[INFO] Manifest: $manifestPath" -ForegroundColor Cyan

    return $manifestPath
}

function Get-CleanupStatus {
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

# ============ Main ============

Write-Header "Internal Nested Recording Demo (Short Rehearsal)"

$script:_t0 = Get-Date
$startedAt = (Get-Date).ToUniversalTime().ToString("o")

try {
    $stepStart = Get-Date
    Write-Step "Load API Key"
    $script:ApiKey = Get-ApiKey
    Write-Host "[OK] API key loaded (length: $($script:ApiKey.Length))" -ForegroundColor Green
    $script:Timings.load_api_key_ms = Get-ElapsedMs -Start $stepStart

    $stepStart = Get-Date
    Write-Step "Ensure API Reachable"
    if (-not (Test-ApiReachable)) {
        if (-not (Start-TrayApp)) {
            $script:FinalStatus = "preflight_not_ready"
            Set-Blocker -Type "api_unavailable" -Detail "Could not start tray app or API not reachable"
            Write-Manifest
            exit 1
        }
        if (-not (Test-ApiReachable)) {
            $script:FinalStatus = "preflight_not_ready"
            Set-Blocker -Type "api_unavailable" -Detail "Tray app started but API still not reachable"
            Write-Manifest
            exit 1
        }
        Write-Host "[OK] API became reachable after starting tray app" -ForegroundColor Green
    } else {
        $script:Context.api_already_running = $true
        Write-Host "[OK] API is already reachable" -ForegroundColor Green
    }
    $script:Timings.ensure_api_reachable_ms = Get-ElapsedMs -Start $stepStart

    $netstatInfo = Get-NetstatInfo
    $script:Context.api_pid = $netstatInfo.listener_pid
    $script:Context.session_id = $netstatInfo.current_shell_session_id

    if ($script:StartedByScript) {
        $script:Context.app_start_method = "script_started"
    } elseif ($netstatInfo.listener_pid) {
        try {
            $proc = Get-Process -Id $netstatInfo.listener_pid -ErrorAction SilentlyContinue
            if ($proc -and $proc.Path) {
                $script:Context.api_process_path = $proc.Path
                $projectBinDir = Join-Path $ProjectRoot "src\AgentRecorder.App\bin"
                $headlessBinDir = Join-Path $ProjectRoot "src\AgentRecorder.Headless\bin"
                if ($proc.Path -like "$projectBinDir*" -or $proc.Path -like "$headlessBinDir*") {
                    $script:Context.app_start_method = "existing_project_tray"
                } else {
                    $script:Context.app_start_method = "external_tray"
                    Add-Warning "API is served by external tray (PID=$($netstatInfo.listener_pid)): $($proc.Path)"
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

    $script:Context.audit_log_path = Join-Path $script:Context.data_dir "logs\audit.jsonl"

    $caps = Get-Capabilities
    if ($caps -and $caps.host_mode) {
        $script:Context.host_mode = $caps.host_mode
    } elseif ($caps -and $caps.host) {
        $script:Context.host_mode = $caps.host
    }

    $stepStart = Get-Date
    Write-Step "Check Nested Recording Capability"
    if (-not $caps) {
        $script:FinalStatus = "preflight_not_ready"
        Set-Blocker -Type "capabilities" -Detail "Failed to retrieve capabilities"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }

    $nestedCap = $caps.recording.nested_recording_mvp
    if (-not $nestedCap -or -not $nestedCap.supported) {
        $script:FinalStatus = "capability_not_supported"
        Set-Blocker -Type "capabilities" -Detail "nested_recording_mvp is not supported"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }

    Write-Host "[OK] nested_recording_mvp is supported" -ForegroundColor Green
    Write-Host "     Max concurrent: $($nestedCap.max_concurrent)" -ForegroundColor Gray
    Write-Host "     Roles: $($nestedCap.roles -join ', ')" -ForegroundColor Gray
    $script:Timings.check_capabilities_ms = Get-ElapsedMs -Start $stepStart

    $stepStart = Get-Date
    Write-Step "Enumerate Displays"
    $displays = Get-Displays

    if ($null -eq $displays) {
        $script:Timings.enumerate_displays_ms = Get-ElapsedMs -Start $stepStart
        $script:FinalStatus = "preflight_not_ready"
        Set-Blocker -Type "api_error" -Detail "Failed to query /api/v1/displays endpoint"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }

    $displayCount = $displays.Count
    Write-Host "[INFO] Found $displayCount display(s) via API" -ForegroundColor $(if ($displayCount -gt 0) { "Green" } else { "Yellow" })

    if ($displayCount -eq 0) {
        $script:FinalStatus = "display_enumeration_unavailable"
        Set-Blocker -Type "display_enumeration" -Detail "No displays found"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }

    foreach ($d in $displays) {
        Write-Host "  - $($d.id): $($d.bounds.width)x$($d.bounds.height) at ($($d.bounds.x),$($d.bounds.y))" -ForegroundColor Gray
    }

    $firstDisplay = $displays[0]
    $displayId = $firstDisplay.id
    $script:OuterRecording.display_id = $displayId
    Write-Host "[OK] Selected display: $displayId" -ForegroundColor Green
    $script:Timings.enumerate_displays_ms = Get-ElapsedMs -Start $stepStart

    $stepStart = Get-Date
    Write-Step "Create Outer Recording (nested.role=outer)"
    Write-Host "[INFO] Duration: $OuterDurationSeconds seconds" -ForegroundColor Cyan
    Write-Host "[INFO] Source type: $OuterSourceType" -ForegroundColor Cyan

    $outerRegionSelection = $null
    if ($OuterSourceType -eq "region") {
        Write-Host "[INFO] Triggering region selection for outer..." -ForegroundColor Cyan
        Write-Host "[ACTION REQUIRED] Please confirm the OUTER recording in the system tray." -ForegroundColor Magenta
        $outerRegionSelection = Invoke-RegionSelection -TimeoutSeconds $InnerSelectionTimeoutSeconds
        if (-not $outerRegionSelection) {
            $script:FinalStatus = "outer_region_selection_failed"
            Set-Blocker -Type "region_selection" -Detail "Failed to create region selection for outer"
            Write-Manifest
            if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
            exit 1
        }
        if ($outerRegionSelection.status -ne "selected") {
            $script:FinalStatus = "outer_region_selection_$($outerRegionSelection.status)"
            Set-Blocker -Type "region_selection" -Detail "Outer region selection status: $($outerRegionSelection.status)"
            Write-Manifest
            if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
            exit 1
        }
        Write-Host "[OK] Outer region selected: $($outerRegionSelection.display_id) [$($outerRegionSelection.coordinate_space)] x=$($outerRegionSelection.bounds.x) y=$($outerRegionSelection.bounds.y) w=$($outerRegionSelection.bounds.width) h=$($outerRegionSelection.bounds.height)" -ForegroundColor Green
    }

    $outerCreateResult = New-NestedRecording -SourceType $OuterSourceType `
        -DisplayId $displayId -RegionSelection $outerRegionSelection `
        -DurationSeconds $OuterDurationSeconds -NestedRole "outer"
    $script:Timings.outer_create_recording_ms = Get-ElapsedMs -Start $stepStart

    if (-not $outerCreateResult) {
        $script:FinalStatus = "outer_recording_failed"
        Set-Blocker -Type "recording" -Detail "Failed to create outer recording"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }

    if ($outerCreateResult.status -eq "requires_user_confirmation") {
        $script:OuterRecording.confirmation_id = $outerCreateResult.confirmation_id
        $script:OuterRecording.confirmation_status = "pending"
        Write-Host "[INFO] Outer recording requires user confirmation" -ForegroundColor Yellow
        Write-Host "[INFO] Confirmation ID: $($script:OuterRecording.confirmation_id)" -ForegroundColor Cyan
        Write-Host ""
        if ($ManifestKind -eq "formal-region") {
            Write-Host "========================================================" -ForegroundColor Magenta
            Write-Host "  STEP 1/3: Confirm OUTER recording within 60 seconds" -ForegroundColor Magenta
            Write-Host "  Click the recording confirmation in the system tray" -ForegroundColor Magenta
            Write-Host "  or the pop-up window to approve the recording." -ForegroundColor Magenta
            Write-Host "  Waiting up to 60 seconds..." -ForegroundColor Magenta
            Write-Host "========================================================" -ForegroundColor Magenta
        } else {
            Write-Host "========================================================" -ForegroundColor Yellow
            Write-Host "  ACTION REQUIRED: Confirm outer recording" -ForegroundColor Yellow
            Write-Host "  Click the recording confirmation in the system tray" -ForegroundColor Yellow
            Write-Host "  or the pop-up window to approve the recording." -ForegroundColor Yellow
            Write-Host "  Waiting up to 60 seconds..." -ForegroundColor Yellow
            Write-Host "========================================================" -ForegroundColor Yellow
        }
        Write-Host ""

        $confWaitStart = Get-Date
        $confResult = Wait-ForConfirmation -ConfirmationId $script:OuterRecording.confirmation_id
        $script:Timings.outer_confirmation_wait_ms = Get-ElapsedMs -Start $confWaitStart

        if ($confResult.result -ne "approved") {
            $script:FinalStatus = "outer_confirmation_$($confResult.result)"
            Set-Blocker -Type "confirmation" -Detail "Outer recording confirmation $($confResult.result)"
            $script:OuterRecording.status = $confResult.result
            Write-Manifest
            if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
            exit 1
        }

        $script:OuterRecording.recording_id = $confResult.recording_id
        $script:OuterRecording.status = "recording"
        # Fetch outer started_at from API immediately after approval
        $outerRecAfterApproval = Get-RecordingStatus -RecordingId $script:OuterRecording.recording_id
        if ($outerRecAfterApproval -and $outerRecAfterApproval.started_at) {
            $script:OuterRecording.started_at = $outerRecAfterApproval.started_at
        }
        Write-Host ""
        Write-Host "[OK] Outer recording approved! Recording ID: $($script:OuterRecording.recording_id)" -ForegroundColor Green
    } elseif ($outerCreateResult.status -eq "recording") {
        $script:OuterRecording.recording_id = $outerCreateResult.recording_id
        $script:OuterRecording.status = "recording"
        $script:OuterRecording.confirmation_status = "not_required"
        Write-Host "[OK] Outer recording started immediately: $($script:OuterRecording.recording_id)" -ForegroundColor Green
    } else {
        $script:FinalStatus = "outer_recording_failed"
        Set-Blocker -Type "recording" -Detail "Unexpected outer recording status: $($outerCreateResult.status)"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }

    $stepStart = Get-Date
    Write-Step "Wait Inner Start Delay ($InnerStartDelaySeconds s)"
    Write-Host "[INFO] Waiting $InnerStartDelaySeconds seconds before starting inner recording..." -ForegroundColor Cyan
    for ($i = 1; $i -le $InnerStartDelaySeconds; $i++) {
        Start-Sleep -Seconds 1
        Write-Progress -Activity "Waiting to start inner recording" -Status "$i / $InnerStartDelaySeconds s" -PercentComplete ([int]($i / $InnerStartDelaySeconds * 100))
    }
    Write-Progress -Activity "Waiting" -Completed
    $script:Timings.inner_start_delay_ms = Get-ElapsedMs -Start $stepStart

    $stepStart = Get-Date
    Write-Step "Create Inner Recording (nested.role=inner)"

    # Check outer remaining time before starting inner
    $outerStartedAt = $script:OuterRecording.started_at
    if ($outerStartedAt -is [string]) { $outerStartedAt = [DateTime]::Parse($outerStartedAt) }
    $outerElapsed = [Math]::Floor((Get-Date).ToUniversalTime().Subtract($outerStartedAt).TotalSeconds)
    $outerRemaining = $OuterDurationSeconds - $outerElapsed
    $minRequired = $InnerDurationSeconds + 20
    if ($outerRemaining -lt $minRequired) {
        $script:FinalStatus = "outer_insufficient_time_for_inner"
        $detail = "Outer has only ${outerRemaining}s remaining, inner needs ${minRequired}s (inner $InnerDurationSeconds + 20s buffer)"
        Set-Blocker -Type "timing" -Detail $detail
        Write-Host "[ERROR] Cannot start inner: $detail" -ForegroundColor Red
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }
    Write-Host "[INFO] Outer remaining: ${outerRemaining}s, inner needs: ${minRequired}s (OK)" -ForegroundColor Cyan

    Write-Host "[INFO] Duration: $InnerDurationSeconds seconds" -ForegroundColor Cyan
    Write-Host "[INFO] Source type: $InnerSourceType" -ForegroundColor Cyan
    Write-Host "[INFO] Parent recording ID: $($script:OuterRecording.recording_id)" -ForegroundColor Cyan

    # Trigger region selection for inner if InnerSourceType=region
    $innerRegionSelection = $null
    if ($InnerSourceType -eq "region") {
        if ($ManifestKind -eq "formal-region") {
            Write-Host "========================================================" -ForegroundColor Magenta
            Write-Host "  STEP 2/3: Draw and confirm INNER region" -ForegroundColor Magenta
            Write-Host "  Please draw and confirm the REGION for inner recording." -ForegroundColor Magenta
            Write-Host "========================================================" -ForegroundColor Magenta
        } else {
            Write-Host "[ACTION REQUIRED] Please draw and confirm the REGION for inner recording." -ForegroundColor Magenta
        }
        $innerRegionSelection = Invoke-RegionSelection -TimeoutSeconds $InnerSelectionTimeoutSeconds
        if (-not $innerRegionSelection) {
            $script:FinalStatus = "inner_region_selection_failed"
            Set-Blocker -Type "region_selection" -Detail "Failed to create region selection for inner"
            Write-Manifest
            if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
            exit 1
        }
        if ($innerRegionSelection.status -ne "selected") {
            $script:FinalStatus = "inner_region_selection_$($innerRegionSelection.status)"
            Set-Blocker -Type "region_selection" -Detail "Inner region selection status: $($innerRegionSelection.status)"
            Write-Manifest
            if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
            exit 1
        }
        Write-Host "[OK] Inner region selected: $($innerRegionSelection.display_id) [$($innerRegionSelection.coordinate_space)] x=$($innerRegionSelection.bounds.x) y=$($innerRegionSelection.bounds.y) w=$($innerRegionSelection.bounds.width) h=$($innerRegionSelection.bounds.height)" -ForegroundColor Green
    }

    $innerCreateResult = New-NestedRecording -SourceType $InnerSourceType `
        -DisplayId $displayId -RegionSelection $innerRegionSelection `
        -DurationSeconds $InnerDurationSeconds -NestedRole "inner" `
        -ParentRecordingId $script:OuterRecording.recording_id
    $script:Timings.inner_create_recording_ms = Get-ElapsedMs -Start $stepStart

    # Save selected_region and recording_request_source for manifest
    if ($innerRegionSelection) {
        $script:InnerRecording.selected_region = @{
            display_id = $innerRegionSelection.display_id
            coordinate_space = $innerRegionSelection.coordinate_space
            bounds = @{
                x = $innerRegionSelection.bounds.x
                y = $innerRegionSelection.bounds.y
                width = $innerRegionSelection.bounds.width
                height = $innerRegionSelection.bounds.height
            }
        }
        $script:InnerRecording.display_id = $innerRegionSelection.display_id
    }
    if ($script:LastRecordingRequest -and $script:LastRecordingRequest.source) {
        $script:InnerRecording.recording_request_source = $script:LastRecordingRequest.source
    }

    if (-not $innerCreateResult) {
        $script:FinalStatus = "inner_recording_failed"
        Set-Blocker -Type "recording" -Detail "Failed to create inner recording"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }

    $script:InnerRecording.parent_recording_id = $script:OuterRecording.recording_id

    if ($innerCreateResult.status -eq "requires_user_confirmation") {
        $script:InnerRecording.confirmation_id = $innerCreateResult.confirmation_id
        $script:InnerRecording.confirmation_status = "pending"
        Write-Host "[INFO] Inner recording requires user confirmation" -ForegroundColor Yellow
        Write-Host "[INFO] Confirmation ID: $($script:InnerRecording.confirmation_id)" -ForegroundColor Cyan
        Write-Host ""
        if ($ManifestKind -eq "formal-region") {
            Write-Host "========================================================" -ForegroundColor Magenta
            Write-Host "  STEP 3/3: Confirm INNER recording within 60 seconds" -ForegroundColor Magenta
            Write-Host "  Click the recording confirmation in the system tray" -ForegroundColor Magenta
            Write-Host "  or the pop-up window to approve the recording." -ForegroundColor Magenta
            Write-Host "  Waiting up to 60 seconds..." -ForegroundColor Magenta
            Write-Host "========================================================" -ForegroundColor Magenta
        } else {
            Write-Host "========================================================" -ForegroundColor Yellow
            Write-Host "  ACTION REQUIRED: Confirm inner recording" -ForegroundColor Yellow
            Write-Host "  Click the recording confirmation in the system tray" -ForegroundColor Yellow
            Write-Host "  or the pop-up window to approve the recording." -ForegroundColor Yellow
            Write-Host "  Waiting up to 60 seconds..." -ForegroundColor Yellow
            Write-Host "========================================================" -ForegroundColor Yellow
        }
        Write-Host ""

        $confWaitStart = Get-Date
        $confResult = Wait-ForConfirmation -ConfirmationId $script:InnerRecording.confirmation_id
        $script:Timings.inner_confirmation_wait_ms = Get-ElapsedMs -Start $confWaitStart

        if ($confResult.result -ne "approved") {
            $script:FinalStatus = "inner_confirmation_$($confResult.result)"
            Set-Blocker -Type "confirmation" -Detail "Inner recording confirmation $($confResult.result)"
            $script:InnerRecording.status = $confResult.result
            Write-Manifest
            if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
            exit 1
        }

        $script:InnerRecording.recording_id = $confResult.recording_id
        $script:InnerRecording.status = "recording"
        Write-Host ""
        Write-Host "[OK] Inner recording approved! Recording ID: $($script:InnerRecording.recording_id)" -ForegroundColor Green
    } elseif ($innerCreateResult.status -eq "recording") {
        $script:InnerRecording.recording_id = $innerCreateResult.recording_id
        $script:InnerRecording.status = "recording"
        $script:InnerRecording.confirmation_status = "not_required"
        Write-Host "[OK] Inner recording started immediately: $($script:InnerRecording.recording_id)" -ForegroundColor Green
    } else {
        $script:FinalStatus = "inner_recording_failed"
        Set-Blocker -Type "recording" -Detail "Unexpected inner recording status: $($innerCreateResult.status)"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }

    $stepStart = Get-Date
    Write-Step "Wait for Both Recordings to Complete"
    Write-Host "[INFO] Outer recording ID: $($script:OuterRecording.recording_id)" -ForegroundColor Cyan
    Write-Host "[INFO] Inner recording ID: $($script:InnerRecording.recording_id)" -ForegroundColor Cyan

    $maxWait = [Math]::Max($OuterDurationSeconds, $InnerDurationSeconds) + 60
    Write-Host "[INFO] Waiting up to $maxWait seconds for both to complete..." -ForegroundColor Cyan

    $outerDone = $false
    $innerDone = $false
    $deadline = (Get-Date).AddSeconds($maxWait)

    while ((Get-Date) -lt $deadline -and (-not $outerDone -or -not $innerDone)) {
        Start-Sleep -Milliseconds 500

        if (-not $outerDone) {
            $outerRec = Get-RecordingStatus -RecordingId $script:OuterRecording.recording_id
            if ($outerRec) {
                if ($outerRec.status -eq "completed") {
                    $outerDone = $true
                    $script:OuterRecording.status = "completed"
                    $script:OuterRecording.started_at = $outerRec.started_at
                    $script:OuterRecording.ended_at = $outerRec.completed_at
                    $script:OuterRecording.duration_actual = $outerRec.elapsed_seconds
                    Write-Host "[OK] Outer recording completed" -ForegroundColor Green
                } elseif ($outerRec.status -eq "failed" -or $outerRec.status -eq "cancelled") {
                    $outerDone = $true
                    $script:OuterRecording.status = $outerRec.status
                    Add-Error "Outer recording ended with status: $($outerRec.status)"
                }
                if ($outerRec.elapsed_seconds) {
                    Write-Progress -Activity "Outer Recording" -Status "$($outerRec.elapsed_seconds)s / $OuterDurationSeconds`s" `
                        -PercentComplete ([Math]::Min(100, [int]($outerRec.elapsed_seconds / $OuterDurationSeconds * 100)))
                }
            }
        }

        if (-not $innerDone) {
            $innerRec = Get-RecordingStatus -RecordingId $script:InnerRecording.recording_id
            if ($innerRec) {
                if ($innerRec.status -eq "completed") {
                    $innerDone = $true
                    $script:InnerRecording.status = "completed"
                    $script:InnerRecording.started_at = $innerRec.started_at
                    $script:InnerRecording.ended_at = $innerRec.completed_at
                    $script:InnerRecording.duration_actual = $innerRec.elapsed_seconds
                    Write-Host "[OK] Inner recording completed" -ForegroundColor Green
                } elseif ($innerRec.status -eq "failed" -or $innerRec.status -eq "cancelled") {
                    $innerDone = $true
                    $script:InnerRecording.status = $innerRec.status
                    Add-Error "Inner recording ended with status: $($innerRec.status)"
                }
            }
        }
    }
    Write-Progress -Activity "Recordings" -Completed
    $script:Timings.wait_both_completed_ms = Get-ElapsedMs -Start $stepStart

    if ($script:OuterRecording.status -ne "completed") {
        $script:FinalStatus = "outer_recording_failed"
        Set-Blocker -Type "recording" -Detail "Outer recording did not complete successfully: $($script:OuterRecording.status)"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }

    if ($script:InnerRecording.status -ne "completed") {
        $script:FinalStatus = "inner_recording_failed"
        Set-Blocker -Type "recording" -Detail "Inner recording did not complete successfully: $($script:InnerRecording.status)"
        Write-Manifest
        if ($script:StartedByScript -and -not $KeepServer) { Stop-TrayAppIfStarted }
        exit 1
    }

    $script:SuccessCriteria.outer_completed = $true
    $script:SuccessCriteria.inner_completed = $true

    $stepStart = Get-Date
    Write-Step "Verify Outputs"

    $outerOutput = Get-RecordingOutput -RecordingId $script:OuterRecording.recording_id
    if ($outerOutput -and $outerOutput.output -and $outerOutput.output.path) {
        $script:OuterRecording.video_path = $outerOutput.output.path
        Write-Host "[INFO] Outer video: $($outerOutput.output.path)" -ForegroundColor Cyan
        if (Test-Path $outerOutput.output.path) {
            $script:SuccessCriteria.outer_mp4_exists = $true
            $fileSize = (Get-Item $outerOutput.output.path).Length
            Write-Host "     Size: $fileSize bytes" -ForegroundColor Gray
            $ffprobeResult = Invoke-Ffprobe -Mp4Path $outerOutput.output.path
            if ($ffprobeResult) {
                $script:OuterRecording.duration_actual = [Math]::Round($ffprobeResult.duration, 1)
                Write-Host "     Duration: $($ffprobeResult.duration) s" -ForegroundColor Gray
                Write-Host "     Resolution: $($ffprobeResult.width)x$($ffprobeResult.height)" -ForegroundColor Gray
            }
        }
    }

    $innerOutput = Get-RecordingOutput -RecordingId $script:InnerRecording.recording_id
    if ($innerOutput -and $innerOutput.output -and $innerOutput.output.path) {
        $script:InnerRecording.video_path = $innerOutput.output.path
        Write-Host "[INFO] Inner video: $($innerOutput.output.path)" -ForegroundColor Cyan
        if (Test-Path $innerOutput.output.path) {
            $script:SuccessCriteria.inner_mp4_exists = $true
            $fileSize = (Get-Item $innerOutput.output.path).Length
            Write-Host "     Size: $fileSize bytes" -ForegroundColor Gray
            $ffprobeResult = Invoke-Ffprobe -Mp4Path $innerOutput.output.path
            if ($ffprobeResult) {
                $script:InnerRecording.duration_actual = [Math]::Round($ffprobeResult.duration, 1)
                Write-Host "     Duration: $($ffprobeResult.duration) s" -ForegroundColor Gray
                Write-Host "     Resolution: $($ffprobeResult.width)x$($ffprobeResult.height)" -ForegroundColor Gray
            }
        }
    }

    $script:Timings.output_probe_ms = Get-ElapsedMs -Start $stepStart

    if ($script:SuccessCriteria.outer_mp4_exists -and $script:SuccessCriteria.inner_mp4_exists) {
        $script:FinalStatus = "completed"
    } else {
        $script:FinalStatus = "output_verification_failed"
        Set-Blocker -Type "output" -Detail "One or both output files not found"
    }

    $stepStart = Get-Date
    Write-Step "Cleanup and Manifest"

    if ($script:StartedByScript -and -not $KeepServer) {
        Stop-TrayAppIfStarted
        Start-Sleep -Seconds 2
    }

    $script:Timings.total_elapsed_ms = [int]((Get-Date) - $script:_t0).TotalMilliseconds
    Write-Manifest
    $script:Timings.cleanup_ms = Get-ElapsedMs -Start $stepStart

    Write-Host ""
    $finalColor = if ($script:FinalStatus -eq "completed") { "Green" } else { "Yellow" }
    Write-Host "============================================================" -ForegroundColor $finalColor
    Write-Host "  INTERNAL NESTED RECORDING DEMO: $($script:FinalStatus.ToUpper())" -ForegroundColor $finalColor
    Write-Host "============================================================" -ForegroundColor $finalColor
    Write-Host ""

    if ($script:FinalStatus -eq "completed") {
        Write-Host "  Outer Recording:" -ForegroundColor White
        Write-Host "    ID: $($script:OuterRecording.recording_id)" -ForegroundColor White
        Write-Host "    Duration: $($script:OuterRecording.duration) s" -ForegroundColor White
        Write-Host "    Video: $($script:OuterRecording.video_path)" -ForegroundColor White
        Write-Host ""
        Write-Host "  Inner Recording:" -ForegroundColor White
        Write-Host "    ID: $($script:InnerRecording.recording_id)" -ForegroundColor White
        Write-Host "    Duration: $($script:InnerRecording.duration) s" -ForegroundColor White
        Write-Host "    Parent: $($script:InnerRecording.parent_recording_id)" -ForegroundColor White
        Write-Host "    Video: $($script:InnerRecording.video_path)" -ForegroundColor White
        Write-Host ""
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
        $script:Timings.total_elapsed_ms = [int]((Get-Date) - $script:_t0).TotalMilliseconds
        Write-Manifest
    } catch { }

    if ($script:StartedByScript -and -not $KeepServer) {
        try { Stop-TrayAppIfStarted } catch { }
    }

    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host "  INTERNAL NESTED RECORDING DEMO: UNHANDLED ERROR" -ForegroundColor Red
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Stack: $($_.ScriptStackTrace)" -ForegroundColor Gray
    Write-Host ""
    exit 1
}
