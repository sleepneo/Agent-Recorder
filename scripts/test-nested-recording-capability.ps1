#Requires -Version 5.1
<#
.SYNOPSIS
    Probe whether current architecture supports nested (concurrent) recordings.

.DESCRIPTION
    Technical capability probe for nested recording:
    1. Start a single demo app instance
    2. Start outer recording (display or window), wait for human confirmation
    3. Wait inner start delay
    4. Attempt to start inner recording while outer is still recording
    5. Record the API response (success or 409 conflict)
    6. Wait for both to complete (if both started)
    7. Generate capability report JSON + Markdown

    Correct confirmation flow: after POST /recordings returns requires_user_confirmation,
    poll GET /confirmations/{id} until status is approved, then extract recording_id
    from the confirmation body.

.PARAMETER OuterSourceType
    Outer recording source type: display or window.

.PARAMETER OuterDisplayId
    Display ID for outer recording (if OuterSourceType = display).
    Default: primary display.

.PARAMETER OuterWindowTitlePattern
    Window title regex for outer recording (if OuterSourceType = window).

.PARAMETER InnerWindowTitlePattern
    Window title regex for inner recording (always window source).

.PARAMETER OuterDurationSeconds
    Outer recording duration in seconds (default: 60).

.PARAMETER InnerDurationSeconds
    Inner recording duration in seconds (default: 20).

.PARAMETER InnerStartDelaySeconds
    Seconds to wait after outer starts recording before attempting inner (default: 10).

.PARAMETER Fps
    Frames per second (default: 15).

.PARAMETER Quality
    Video quality: low, medium, high (default: medium).

.PARAMETER OutputDir
    Directory for output video files.

.PARAMETER ReportDir
    Directory for capability report files.

.PARAMETER SkipBuild
    Skip build and test steps.

.PARAMETER Configuration
    Build configuration: Release (default) or Debug.

.PARAMETER Port
    API port (default: 37891).

.EXAMPLE
    .\scripts\test-nested-recording-capability.ps1 `
        -OuterSourceType display `
        -InnerWindowTitlePattern "Notepad" `
        -OuterDurationSeconds 60 -InnerDurationSeconds 20 -InnerStartDelaySeconds 10
#>

param(
    [ValidateSet("display", "window")]
    [string]$OuterSourceType = "display",
    [string]$OuterDisplayId = "",
    [string]$OuterWindowTitlePattern = "",
    [string]$InnerWindowTitlePattern = "",
    [int]$OuterDurationSeconds = 60,
    [int]$InnerDurationSeconds = 20,
    [int]$InnerStartDelaySeconds = 10,
    [int]$Fps = 15,
    [ValidateSet("low", "medium", "high")]
    [string]$Quality = "medium",
    [string]$OutputDir = "D:\works\python\007-Agent-Recorder\.local-data\Videos\demo",
    [string]$ReportDir = "D:\works\python\007-Agent-Recorder\.local-data\demo-runs",
    [switch]$SkipBuild = $false,
    [string]$Configuration = "Release",
    [int]$Port = 37891
)

$ErrorActionPreference = "Stop"
$ProjectRoot = "D:\works\python\007-Agent-Recorder"
$ScriptDir = Join-Path $ProjectRoot "scripts"
$DataDir = "D:\works\python\007-Agent-Recorder\.local-data"
$MetadataFile = Join-Path $DataDir "demo-app-server.json"
$LockFile = Join-Path $DataDir "demo-session.lock"

$ApiBase = "http://127.0.0.1:$Port/api/v1"
$AgentName = "nested-capability-probe"
$ApiKey = ""

# Load API key
$apiKeyFile = Join-Path $DataDir "config\api-key.txt"
if (Test-Path $apiKeyFile) {
    $ApiKey = (Get-Content $apiKeyFile -Raw).Trim()
}

$script:AppStarted = $false
$script:LockAcquired = $false
$script:ProbeResult = "failed_unknown"
$script:ProbeDetail = ""
$script:OuterRec = $null
$script:InnerRec = $null
$script:Timeline = @()
$script:ReportFile = $null
$script:PreflightResult = $null
$script:Cleanup = @{
    lock_removed = $false
    port_free = $false
    new_agent_processes = @()
}

# ==================== Helpers ====================

function Add-Timeline {
    param([string]$Event, [string]$Detail = "")
    $ts = Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffZ"
    $script:Timeline += [pscustomobject]@{
        timestamp = $ts
        event = $Event
        detail = $Detail
    }
    Write-Host "[TIMELINE] $ts | $Event | $Detail"
}

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        [string]$Body = "",
        [int]$TimeoutSec = 30
    )
    $url = "$ApiBase$Path"
    $headers = @{
        "X-Agent-Name" = $AgentName
        "X-Agent-Recorder-Key" = $ApiKey
    }

    try {
        if ($Method -eq "GET") {
            $resp = Invoke-RestMethod -Uri $url -Method Get -Headers $headers -TimeoutSec $TimeoutSec
            return @{ status = 200; body = $resp; error = $null }
        }
        else {
            $resp = Invoke-RestMethod -Uri $url -Method Post -Headers $headers `
                -Body $Body -ContentType "application/json" -TimeoutSec $TimeoutSec
            return @{ status = 200; body = $resp; error = $null }
        }
    }
    catch {
        $webErr = $_.Exception
        $statusCode = 0
        $errBody = ""
        if ($webErr.Response) {
            try {
                $statusCode = [int]$webErr.Response.StatusCode
                $reader = New-Object System.IO.StreamReader($webErr.Response.GetResponseStream())
                $errBody = $reader.ReadToEnd()
            } catch {}
        }
        return @{
            status = $statusCode
            body = $null
            error = $webErr.Message
            error_body = $errBody
        }
    }
}

function Resolve-WindowByPattern {
    param([string]$Pattern)
    $r = Invoke-Api -Method GET -Path "/windows?visible_only=true"
    if ($r.status -ne 200) { return $null }
    $wins = $r.body.data.windows
    $matched = @($wins | Where-Object { $_.title -match $Pattern })
    if ($matched.Count -eq 1) { return $matched[0] }
    return $null
}

function Resolve-PrimaryDisplay {
    $r = Invoke-Api -Method GET -Path "/displays"
    if ($r.status -ne 200) { return $null }
    $disps = $r.body.data.displays
    foreach ($d in $disps) {
        if ($d.is_primary) { return $d }
    }
    if ($disps.Count -gt 0) { return $disps[0] }
    return $null
}

function Start-Recording {
    param(
        [string]$SourceType,
        [string]$SourceId,
        [int]$DurationSec,
        [string]$OutputName
    )

    $outPath = Join-Path $OutputDir $OutputName

    if ($SourceType -eq "display") {
        $body = @{
            source = @{
                type = "display"
                display_id = $SourceId
            }
            duration_seconds = $DurationSec
            fps = $Fps
            quality = $Quality
            output_path = $outPath
        } | ConvertTo-Json -Depth 5
    }
    else {
        $body = @{
            source = @{
                type = "window"
                window_id = $SourceId
            }
            duration_seconds = $DurationSec
            fps = $Fps
            quality = $Quality
            output_path = $outPath
        } | ConvertTo-Json -Depth 5
    }

    Add-Timeline "api_call" "POST /recordings ($SourceType, dur=$DurationSec)"
    $r = Invoke-Api -Method POST -Path "/recordings" -Body $body -TimeoutSec 15
    return $r
}

function Get-RecordingStatus {
    param([string]$RecordingId)
    $r = Invoke-Api -Method GET -Path "/recordings/$RecordingId"
    return $r
}

# ==================== CORRECTED: Confirmation polling via GET /confirmations/{id} ====================

function Get-ConfirmationStatus {
    param([string]$ConfirmationId)
    $r = Invoke-Api -Method GET -Path "/confirmations/$ConfirmationId"
    return $r
}

function Wait-ForConfirmation {
    param(
        [string]$ConfirmationId,
        [int]$TimeoutSeconds = 120
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $history = @()

    while ((Get-Date) -lt $deadline) {
        $r = Get-ConfirmationStatus -ConfirmationId $ConfirmationId
        if ($r.status -ne 200) {
            Start-Sleep -Seconds 2
            continue
        }

        $confBody = $r.body.data
        $state = $confBody.status
        $recId = $confBody.recording_id

        # Record history entry
        $history += [pscustomobject]@{
            timestamp = (Get-Date -Format "o")
            status = $state
            recording_id = $recId
        }

        Add-Timeline "confirmation_polled" "conf=$ConfirmationId status=$state rec_id=$recId"

        if ($state -eq "approved") {
            Add-Timeline "confirmation_approved" "$ConfirmationId approved"
            return @{
                result = "approved"
                recording_id = $recId
                history = $history
            }
        }
        if ($state -eq "rejected") {
            Add-Timeline "confirmation_rejected" "$ConfirmationId rejected"
            return @{
                result = "rejected"
                recording_id = $null
                history = $history
            }
        }
        if ($state -eq "expired") {
            Add-Timeline "confirmation_expired" "$ConfirmationId expired"
            return @{
                result = "expired"
                recording_id = $null
                history = $history
            }
        }
        Start-Sleep -Seconds 2
    }

    Add-Timeline "confirmation_timeout" "$ConfirmationId timed out waiting for confirmation"
    return @{
        result = "timeout"
        recording_id = $null
        history = $history
    }
}

function Wait-ForRecordingCompleted {
    param(
        [string]$RecordingId,
        [int]$MaxWaitSeconds = 300
    )

    $deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
    while ((Get-Date) -lt $deadline) {
        $r = Get-RecordingStatus -RecordingId $RecordingId
        if ($r.status -ne 200) {
            Start-Sleep -Seconds 3
            continue
        }
        $state = $r.body.data.status
        if ($state -eq "completed" -or $state -eq "failed") {
            Add-Timeline "recording_$state" "$RecordingId ended with $state"
            return $r.body.data
        }
        Start-Sleep -Seconds 3
    }
    Add-Timeline "recording_timeout" "$RecordingId did not complete within $MaxWaitSeconds s"
    return $null
}

function Get-FFprobe {
    param([string]$FilePath)
    if (-not (Test-Path $FilePath)) {
        return @{ status = "not_found"; reason = "file_not_found" }
    }

    # Try environment variable first
    $ffprobe = $null
    if ($env:AGENT_RECORDER_FFMPEG_DIR) {
        $candidate = Join-Path $env:AGENT_RECORDER_FFMPEG_DIR "ffprobe.exe"
        if (Test-Path $candidate) { $ffprobe = $candidate }
    }

    # Fallback to tools\ffmpeg\bin
    if (-not $ffprobe) {
        $candidate = Join-Path $ProjectRoot "tools\ffmpeg\bin\ffprobe.exe"
        if (Test-Path $candidate) { $ffprobe = $candidate }
    }

    # Last fallback: check PATH
    if (-not $ffprobe) {
        $which = Get-Command ffprobe -EA SilentlyContinue
        if ($which) { $ffprobe = $which.Source }
    }

    if (-not $ffprobe) {
        return @{ status = "not_found"; reason = "ffprobe_exe_not_found" }
    }

    try {
        $json = & $ffprobe -v quiet -print_format json -show_format -show_streams $FilePath 2>$null
        $obj = $json | ConvertFrom-Json
        return @{ status = "ok"; data = $obj }
    }
    catch {
        return @{ status = "error"; reason = $_.Exception.Message }
    }
}

function Build-OuterRecObject {
    param(
        [string]$SourceType, [string]$SourceId, [string]$SourceTitle,
        [int]$DurationRequested,
        [int]$ApiStatusCode, [string]$ApiError,
        [string]$Status, [string]$RecordingId, [string]$ConfirmationId,
        [string]$RecordingIdSource, [object]$ConfirmationHistory,
        [string]$OutputPath
    )
    return [pscustomobject]@{
        source_type = $SourceType
        source_id = $SourceId
        source_title = $SourceTitle
        duration_requested = $DurationRequested
        api_status_code = $ApiStatusCode
        api_error = $ApiError
        status = $Status
        recording_id = $RecordingId
        recording_id_source = $RecordingIdSource
        confirmation_id = $ConfirmationId
        confirmation_status_history = $ConfirmationHistory
        output_path = $OutputPath
        size_bytes = 0
        ffprobe_status = "not_run"
        ffprobe = $null
    }
}

function Build-InnerRecObject {
    param(
        [string]$SourceId, [string]$SourceTitle,
        [int]$DurationRequested,
        [int]$ApiStatusCode, [string]$ApiError, [string]$ApiErrorBody,
        [string]$Status, [string]$RecordingId, [string]$ConfirmationId,
        [string]$RecordingIdSource, [object]$ConfirmationHistory,
        [string]$OutputPath
    )
    return [pscustomobject]@{
        source_type = "window"
        source_id = $SourceId
        source_title = $SourceTitle
        duration_requested = $DurationRequested
        api_status_code = $ApiStatusCode
        api_error = $ApiError
        api_error_body = $ApiErrorBody
        status = $Status
        recording_id = $RecordingId
        recording_id_source = $RecordingIdSource
        confirmation_id = $ConfirmationId
        confirmation_status_history = $ConfirmationHistory
        output_path = $OutputPath
        size_bytes = 0
        ffprobe_status = "not_run"
        ffprobe = $null
    }
}

function Update-RecWithFFprobe {
    param([object]$Rec)
    if (-not (Test-Path $Rec.output_path)) {
        $Rec | Add-Member -NotePropertyName "ffprobe_status" -NotePropertyValue "not_found" -Force
        return
    }
    $Rec.size_bytes = (Get-Item $Rec.output_path).Length
    $fp = Get-FFprobe -FilePath $Rec.output_path
    $Rec.ffprobe_status = $fp.status
    $Rec | Add-Member -NotePropertyName "ffprobe" -NotePropertyValue $fp.data -Force
}

function Save-Report {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $baseName = "nested-capability-$timestamp"

    if (-not (Test-Path $ReportDir)) {
        New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null
    }

    $jsonPath = Join-Path $ReportDir "$baseName.json"
    $mdPath = Join-Path $ReportDir "$baseName.md"

    # Post-process: add ffprobe results
    if ($script:OuterRec) {
        Update-RecWithFFprobe -Rec $script:OuterRec
    }
    if ($script:InnerRec) {
        Update-RecWithFFprobe -Rec $script:InnerRec
    }

    $report = [ordered]@{
        probe_result = $script:ProbeResult
        probe_detail = $script:ProbeDetail
        probe_timestamp = (Get-Date -Format "o")
        outer_source_type = $OuterSourceType
        outer_duration_seconds = $OuterDurationSeconds
        inner_duration_seconds = $InnerDurationSeconds
        inner_start_delay_seconds = $InnerStartDelaySeconds
        fps = $Fps
        quality = $Quality
        preflight = $script:PreflightResult
        outer = $script:OuterRec
        inner = $script:InnerRec
        timeline = $script:Timeline
        cleanup = $script:Cleanup
    }

    $reportJson = $report | ConvertTo-Json -Depth 12
    # Remove empty confirmation_status_history null entries for cleanliness
    $reportJson = $reportJson -replace '"confirmation_status_history": null', '"confirmation_status_history": []'
    $reportJson | Out-File -FilePath $jsonPath -Encoding utf8

    # --- Markdown ---
    $outerMd = "_Not started_"
    if ($script:OuterRec) {
        $o = $script:OuterRec
        $confHistLines = ""
        if ($o.confirmation_status_history -and $o.confirmation_status_history.Count -gt 0) {
            $confHistLines = "`n**Confirmation history:**`n" +
                ($o.confirmation_status_history | ForEach-Object { "- $($_.timestamp) | $($_.status) | rec_id=$($_.recording_id)" } | Out-String)
        }
        $ffprobeMd = ""
        if ($o.ffprobe_status -eq "ok") {
            $ffprobeMd = "`n- **Duration**: $($o.ffprobe.format.duration) s`n- **Resolution**: $($o.ffprobe.streams[0].width)x$($o.ffprobe.streams[0].height)"
        }
        elseif ($o.ffprobe_status -ne "not_run") {
            $ffprobeMd = "`n- **FFprobe status**: $($o.ffprobe_status)"
        }
        $outerMd = @"
- **Status**: $($o.status)
- **Recording ID**: $(if($o.recording_id) { $o.recording_id } else { "_not assigned_" })
- **Recording ID source**: $($o.recording_id_source)
- **Confirmation ID**: $(if($o.confirmation_id) { $o.confirmation_id } else { "_none_" })
- **Output**: $($o.output_path)
- **Size**: $($o.size_bytes) bytes
- **FFprobe status**: $($o.ffprobe_status)$ffprobeMd$confHistLines
"@
    }

    $innerMd = "_Not attempted_"
    if ($script:InnerRec) {
        $i = $script:InnerRec
        $confHistLines = ""
        if ($i.confirmation_status_history -and $i.confirmation_status_history.Count -gt 0) {
            $confHistLines = "`n**Confirmation history:**`n" +
                ($i.confirmation_status_history | ForEach-Object { "- $($_.timestamp) | $($_.status) | rec_id=$($_.recording_id)" } | Out-String)
        }
        $ffprobeMd = ""
        if ($i.ffprobe_status -eq "ok") {
            $ffprobeMd = "`n- **Duration**: $($i.ffprobe.format.duration) s`n- **Resolution**: $($i.ffprobe.streams[0].width)x$($i.ffprobe.streams[0].height)"
        }
        elseif ($i.ffprobe_status -ne "not_run") {
            $ffprobeMd = "`n- **FFprobe status**: $($i.ffprobe_status)"
        }
        $innerMd = @"
- **Status**: $($i.status)
- **Recording ID**: $(if($i.recording_id) { $i.recording_id } else { "_not assigned_" })
- **Recording ID source**: $($i.recording_id_source)
- **Confirmation ID**: $(if($i.confirmation_id) { $i.confirmation_id } else { "_none_" })
- **API status code**: $($i.api_status_code)
- **API error**: $($i.api_error)
- **API error body**: $(if($i.api_error_body) { $i.api_error_body } else { "_none_" })
- **Output**: $($i.output_path)
- **Size**: $($i.size_bytes) bytes
- **FFprobe status**: $($i.ffprobe_status)$ffprobeMd$confHistLines
"@
    }

    $timelineMd = "| Time | Event | Detail |`n|------|-------|--------|"
    foreach ($t in $script:Timeline) {
        $timelineMd += "`n| $($t.timestamp) | $($t.event) | $($t.detail) |"
    }

    $cleanupMd = "| Item | Value |`n|------|-------|`n"
    $cleanupMd += "| lock_removed | $($script:Cleanup.lock_removed) |`n"
    $cleanupMd += "| port_free | $($script:Cleanup.port_free) |`n"
    $cleanupMd += "| new_agent_processes | $($script:Cleanup.new_agent_processes.Count) |`n"

    # Build preflight MD
    $preflightMd = "_Not run_"
    if ($script:PreflightResult) {
        $pf = $script:PreflightResult
        $preflightMd = @"
- **Classification**: ``$($pf.classification)``
- **Demo ready**: $($pf.demo_ready)
- **Report**: $(if($pf.report_path) { $pf.report_path } else { "_none_" })
- **Reason**: $($pf.reason)
"@
    }

    $md = @"
# Nested Recording Capability Probe Report

- **Probe result**: ``$($script:ProbeResult)``
- **Probe detail**: $($script:ProbeDetail)
- **Timestamp**: $(Get-Date -Format "o")

## Preflight

$preflightMd

## Parameters

| Parameter | Value |
|-----------|-------|
| Outer source | $OuterSourceType |
| Outer duration | $OuterDurationSeconds s |
| Inner duration | $InnerDurationSeconds s |
| Inner start delay | $InnerStartDelaySeconds s |
| FPS | $Fps |
| Quality | $Quality |

## Outer Recording

$outerMd

## Inner Recording

$innerMd

## Cleanup

$cleanupMd

## Timeline

$timelineMd
"@

    $md | Out-File -FilePath $mdPath -Encoding utf8

    $script:ReportFile = $jsonPath
    Write-Host ""
    Write-Host "NESTED CAPABILITY PROBE: $($script:ProbeResult)"
    Write-Host "  JSON: $jsonPath"
    Write-Host "  MD:   $mdPath"
}

# ==================== App Lifecycle ====================

function Start-DemoApp {
    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }

    $appExe = Join-Path $ProjectRoot "src\AgentRecorder.App\bin\$Configuration\net8.0-windows10.0.19041.0\AgentRecorder.App.exe"
    $launcherExe = Join-Path $ProjectRoot "tools\ProcessLauncher\bin\$Configuration\net8.0-windows10.0.19041.0\AgentRecorder.ProcessLauncher.exe"

    if (-not (Test-Path $appExe)) {
        Write-Host "[ERROR] App not found at $appExe"
        return $false
    }
    if (-not (Test-Path $launcherExe)) {
        Write-Host "[ERROR] ProcessLauncher not found at $launcherExe"
        return $false
    }

    $env:AGENT_RECORDER_DATA_DIR = $DataDir
    $ffmpegDir = Join-Path $ProjectRoot "tools\ffmpeg\bin"
    if (Test-Path $ffmpegDir) {
        $env:AGENT_RECORDER_FFMPEG_DIR = $ffmpegDir
    }

    Write-Host "[INFO] Starting demo app via ProcessLauncher..."

    $launchModes = @("gui", "gui-no-breakaway", "detached")
    $launchedPid = $null

    foreach ($mode in $launchModes) {
        $argsList = [System.Collections.Generic.List[string]]::new()
        $argsList.Add("--exe")
        $argsList.Add($appExe)
        $argsList.Add("--work-dir")
        $argsList.Add($ProjectRoot)
        $argsList.Add("--mode")
        $argsList.Add($mode)

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $launcherExe
        $psi.Arguments = ($argsList -join " ")
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.WorkingDirectory = $ProjectRoot

        try {
            $proc = [System.Diagnostics.Process]::Start($psi)
            if ($null -eq $proc) { continue }

            $out = $proc.StandardOutput.ReadToEnd()
            $proc.WaitForExit(10000)
            if (-not $proc.HasExited) { try { $proc.Kill() } catch {} }

            if ($proc.ExitCode -eq 0 -and $out -match '(?m)^PID=(\d+)') {
                $launchedPid = [int]$matches[1]
                Write-Host "[OK] Launch succeeded (mode=$mode, PID=$launchedPid)"
                break
            }
        }
        catch {
            Write-Host "[WARN] Mode $mode failed: $($_.Exception.Message)"
        }
    }

    if (-not $launchedPid) {
        Write-Host "[ERROR] All launch modes failed"
        return $false
    }

    $deadline = (Get-Date).AddSeconds(30)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest "$ApiBase/capabilities" -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { $healthy = $true; break }
        } catch {}
        Start-Sleep -Milliseconds 800
    }

    if (-not $healthy) {
        Write-Host "[ERROR] App did not become healthy"
        return $false
    }

    $script:AppStarted = $true
    Write-Host "[OK] Demo app is healthy (PID=$launchedPid)"
    return $true
}

function Stop-DemoApp {
    if (-not $script:AppStarted) { return }
    Get-Process -Name "AgentRecorder.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $script:AppStarted = $false
    Write-Host "[INFO] Demo app stopped"
}

function Get-NewAgentProcesses {
    $knownPid = 49364  # known noise from previous runs
    $current = Get-Process -Name "AgentRecorder.App", "AgentRecorder.Headless" -ErrorAction SilentlyContinue
    return @($current | Where-Object { $_.Id -ne $knownPid } | ForEach-Object { $_.Id })
}

# ==================== Main Probe Flow ====================

function Invoke-Probe {
    Add-Timeline "probe_start" "Nested recording capability probe started"

    # --- Record initial state ---
    $initialProcs = Get-NewAgentProcesses

    # --- Preflight: fresh desktop visibility ---
    Add-Timeline "preflight" "Running fresh desktop visibility diagnostic..."
    $script:PreflightResult = @{
        classification = "unknown"
        demo_ready = $false
        report_path = ""
        reason = ""
    }

    $visScript = Join-Path $ScriptDir "test-interactive-desktop-visibility.ps1"
    if (-not (Test-Path $visScript)) {
        $script:PreflightResult.reason = "Script not found: $visScript"
        $script:ProbeResult = "blocked_preflight_not_ready"
        $script:ProbeDetail = "test-interactive-desktop-visibility.ps1 not found"
        Add-Timeline "preflight_fail" "Script not found"
        return
    }

    $visOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $visScript -StartDemoApp 2>&1
    $visExitCode = $LASTEXITCODE

    # Read the latest desktop visibility report
    $visReport = $null
    $visFiles = @()
    if (Test-Path $ReportDir) {
        $visFiles = @(Get-ChildItem $ReportDir -Filter "desktop-visibility-*.json" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        if ($visFiles.Count -gt 0) { $visReport = $visFiles[0].FullName }
    }
    $script:PreflightResult.report_path = $visReport

    if ($visReport -and (Test-Path $visReport)) {
        try {
            $visJson = Get-Content $visReport -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            $classification = $visJson.classification
            $classificationReason = $visJson.classification_reason
            $script:PreflightResult.classification = $classification

            if ($classification -eq "INTERACTIVE_DESKTOP_VISIBLE") {
                $script:PreflightResult.demo_ready = $true
                $script:PreflightResult.reason = "Fresh diagnostic confirmed: $classification"
                Add-Timeline "preflight_ok" "Fresh diagnostic: $classification"
            }
            else {
                $script:PreflightResult.demo_ready = $false
                $reasonDetail = if ($classificationReason) { " ($classificationReason)" } else { "" }
                $script:PreflightResult.reason = "Classification is $classification$reasonDetail (not INTERACTIVE_DESKTOP_VISIBLE)"
                $script:ProbeResult = "blocked_preflight_not_ready"
                $script:ProbeDetail = "Classification: $classification"
                Add-Timeline "preflight_fail" "Classification=$classification"
                return
            }
        }
        catch {
            $script:PreflightResult.reason = "Error reading report: $($_.Exception.Message)"
            $script:ProbeResult = "blocked_preflight_not_ready"
            $script:ProbeDetail = "Could not parse visibility report"
            Add-Timeline "preflight_fail" "Error reading report"
            return
        }
    }
    else {
        $script:PreflightResult.reason = "No desktop visibility report found"
        $script:ProbeResult = "blocked_preflight_not_ready"
        $script:ProbeDetail = "Visibility diagnostic produced no report"
        Add-Timeline "preflight_fail" "No visibility report"
        return
    }

    # --- Start app ---
    if (-not (Start-DemoApp)) {
        $script:ProbeResult = "blocked_preflight_not_ready"
        $script:ProbeDetail = "Failed to start demo app"
        Add-Timeline "app_start_failed" ""
        return
    }
    Add-Timeline "app_started" "Demo app running"

    # --- Resolve outer source ---
    $outerSourceId = ""
    $outerSourceTitle = ""
    if ($OuterSourceType -eq "display") {
        if ($OuterDisplayId) {
            $outerSourceId = $OuterDisplayId
            $outerSourceTitle = "Display $OuterDisplayId"
        }
        else {
            $d = Resolve-PrimaryDisplay
            if (-not $d) {
                $script:ProbeResult = "blocked_preflight_not_ready"
                $script:ProbeDetail = "No display found"
                Add-Timeline "resolve_outer_fail" "No display"
                return
            }
            $outerSourceId = $d.id
            $outerSourceTitle = $d.name
        }
    }
    else {
        if (-not $OuterWindowTitlePattern) {
            $script:ProbeResult = "blocked_preflight_not_ready"
            $script:ProbeDetail = "OuterWindowTitlePattern required for window source"
            Add-Timeline "resolve_outer_fail" "No pattern"
            return
        }
        $w = Resolve-WindowByPattern -Pattern $OuterWindowTitlePattern
        if (-not $w) {
            $script:ProbeResult = "blocked_preflight_not_ready"
            $script:ProbeDetail = "No unique outer window matching '$OuterWindowTitlePattern'"
            Add-Timeline "resolve_outer_fail" "No unique window"
            return
        }
        $outerSourceId = $w.id
        $outerSourceTitle = $w.title
    }
    Add-Timeline "outer_resolved" "$OuterSourceType : $outerSourceTitle"

    # --- Resolve inner source ---
    if (-not $InnerWindowTitlePattern) {
        $script:ProbeResult = "blocked_missing_parameters"
        $script:ProbeDetail = "InnerWindowTitlePattern required but not provided"
        Add-Timeline "resolve_inner_fail" "No pattern"
        return
    }
    $innerWin = Resolve-WindowByPattern -Pattern $InnerWindowTitlePattern
    if (-not $innerWin) {
        $script:ProbeResult = "blocked_preflight_not_ready"
        $script:ProbeDetail = "No unique inner window matching '$InnerWindowTitlePattern'"
        Add-Timeline "resolve_inner_fail" "No unique window"
        return
    }
    Add-Timeline "inner_resolved" "window: $($innerWin.title)"

    # --- Start outer recording ---
    $outerOutName = "probe-outer-$(Get-Date -Format 'yyyyMMdd-HHmmss').mp4"
    Add-Timeline "outer_request" "Requesting outer recording..."
    $outerResult = Start-Recording -SourceType $OuterSourceType -SourceId $outerSourceId `
        -DurationSec $OuterDurationSeconds -OutputName $outerOutName

    $script:OuterRec = Build-OuterRecObject `
        -SourceType $OuterSourceType -SourceId $outerSourceId -SourceTitle $outerSourceTitle `
        -DurationRequested $OuterDurationSeconds `
        -ApiStatusCode $outerResult.status -ApiError $outerResult.error `
        -Status "unknown" -RecordingId "" -ConfirmationId "" `
        -RecordingIdSource "" -ConfirmationHistory @() `
        -OutputPath (Join-Path $OutputDir $outerOutName)

    if ($outerResult.status -ne 200) {
        $script:ProbeResult = "failed_unknown"
        $script:ProbeDetail = "Outer recording request failed: $($outerResult.error)"
        $script:OuterRec.status = "request_failed"
        Add-Timeline "outer_request_fail" "Status $($outerResult.status)"
        return
    }

    # POST /recordings returns { status: "requires_user_confirmation", confirmation_id: "..." }
    # NO recording_id in this response — must poll confirmation endpoint.
    $outerResultBody = $outerResult.body.data
    if ($outerResultBody.status -eq "requires_user_confirmation") {
        $script:OuterRec.confirmation_id = $outerResultBody.confirmation_id
        $script:OuterRec.recording_id_source = "from_confirmation_after_approval"
        $script:OuterRec.status = "pending_confirmation"
        Add-Timeline "outer_pending_confirmation" "Conf ID: $($script:OuterRec.confirmation_id) (will poll GET /confirmations/{id})"
        Write-Host ""
        Write-Host "[ACTION REQUIRED] Outer recording waiting for your confirmation!"
        Write-Host "  Please click 'Allow' in the system tray popup."
        Write-Host ""

        $confResult = Wait-ForConfirmation -ConfirmationId $script:OuterRec.confirmation_id -TimeoutSeconds 120
        $script:OuterRec.confirmation_status_history = $confResult.history

        if ($confResult.result -ne "approved") {
            $script:ProbeResult = "blocked_no_human_confirmation"
            $script:ProbeDetail = "Outer recording confirmation $($confResult.result)"
            $script:OuterRec.status = $confResult.result
            $script:OuterRec.recording_id = $null
            Add-Timeline "outer_confirmation_$($confResult.result)" "recording_id was never assigned"
            # SECURITY GATE: outer did not enter recording state — do NOT attempt inner request.
            # Inner request without an active outer recording is NOT a valid probe outcome.
            $script:InnerRec = $null
            return
        }
        else {
            # confirmation approved: recording_id comes from the confirmation body
            $script:OuterRec.recording_id = $confResult.recording_id
            $script:OuterRec.status = "recording"
            Add-Timeline "outer_recording_started" "rec_id=$($confResult.recording_id) (from confirmation)"
        }
    }
    elseif ($outerResultBody.status -eq "recording") {
        # No confirmation needed — recording started immediately
        $script:OuterRec.recording_id = $outerResultBody.recording_id
        $script:OuterRec.recording_id_source = "from_post_response"
        $script:OuterRec.status = "recording"
        Add-Timeline "outer_recording_immediate" "Started without confirmation, rec_id=$($script:OuterRec.recording_id)"
    }
    else {
        $script:ProbeResult = "failed_unknown"
        $script:ProbeDetail = "Unexpected outer response status: $($outerResultBody.status)"
        return
    }

    # --- Wait inner start delay (only if outer is recording) ---
    if ($script:OuterRec.status -eq "recording") {
        Add-Timeline "wait_inner_delay" "Waiting $InnerStartDelaySeconds s before inner request..."
        Start-Sleep -Seconds $InnerStartDelaySeconds
    }
    else {
        Add-Timeline "skip_inner_delay" "Outer not recording, skipping delay"
    }

    # --- Attempt inner recording ---
    $innerOutName = "probe-inner-$(Get-Date -Format 'yyyyMMdd-HHmmss').mp4"
    Add-Timeline "inner_request" "Attempting inner recording (outer is $($script:OuterRec.status))..."
    $innerResult = Start-Recording -SourceType "window" -SourceId $innerWin.id `
        -DurationSec $InnerDurationSeconds -OutputName $innerOutName

    $script:InnerRec = Build-InnerRecObject `
        -SourceId $innerWin.id -SourceTitle $innerWin.title `
        -DurationRequested $InnerDurationSeconds `
        -ApiStatusCode $innerResult.status `
        -ApiError $innerResult.error `
        -ApiErrorBody $innerResult.error_body `
        -Status "unknown" -RecordingId "" -ConfirmationId "" `
        -RecordingIdSource "" -ConfirmationHistory @() `
        -OutputPath (Join-Path $OutputDir $innerOutName)

    # 409 → single recording only (our primary finding)
    if ($innerResult.status -eq 409) {
        $script:ProbeResult = "not_supported_single_recording_only"
        $script:ProbeDetail = "API returned 409 RECORDING_ALREADY_RUNNING"
        $script:InnerRec.status = "rejected_409"
        Add-Timeline "inner_rejected_409" "Status=$($innerResult.status) code=RECORDING_ALREADY_RUNNING"

        # Parse error body for structured details
        if ($innerResult.error_body) {
            try {
                $errParsed = $innerResult.error_body | ConvertFrom-Json
                Add-Timeline "inner_409_details" "code=$($errParsed.code) msg=$($errParsed.message)"
            } catch {
                Add-Timeline "inner_409_raw" $innerResult.error_body
            }
        }
        Write-Host "[RESULT] NOT SUPPORTED - API returns 409 RECORDING_ALREADY_RUNNING"
    }
    elseif ($innerResult.status -ne 200) {
        $script:ProbeResult = "failed_unknown"
        $script:ProbeDetail = "Inner recording request failed: status=$($innerResult.status), err=$($innerResult.error)"
        $script:InnerRec.status = "request_failed"
        Add-Timeline "inner_request_fail" "Status $($innerResult.status)"
    }
    else {
        # 200: confirmation required or immediate recording
        $innerResultBody = $innerResult.body.data
        if ($innerResultBody.status -eq "requires_user_confirmation") {
            $script:InnerRec.confirmation_id = $innerResultBody.confirmation_id
            $script:InnerRec.recording_id_source = "from_confirmation_after_approval"
            $script:InnerRec.status = "pending_confirmation"
            Add-Timeline "inner_pending_confirmation" "Conf ID: $($script:InnerRec.confirmation_id)"
            Write-Host ""
            Write-Host "[ACTION REQUIRED] Inner recording waiting for your confirmation!"
            Write-Host "  Please click 'Allow' in the system tray popup."
            Write-Host ""

            $confResult = Wait-ForConfirmation -ConfirmationId $script:InnerRec.confirmation_id -TimeoutSeconds 120
            $script:InnerRec.confirmation_status_history = $confResult.history

            if ($confResult.result -eq "approved") {
                $script:InnerRec.recording_id = $confResult.recording_id
                $script:InnerRec.status = "recording"
                Add-Timeline "inner_recording_started" "rec_id=$($confResult.recording_id)"
            }
            else {
                $script:InnerRec.status = $confResult.result
                Add-Timeline "inner_confirmation_$($confResult.result)" ""
            }
        }
        elseif ($innerResultBody.status -eq "recording") {
            $script:InnerRec.recording_id = $innerResultBody.recording_id
            $script:InnerRec.recording_id_source = "from_post_response"
            $script:InnerRec.status = "recording"
            Add-Timeline "inner_recording_immediate" "Started without confirmation, rec_id=$($script:InnerRec.recording_id)"
        }
    }

    # --- Wait for inner to complete ---
    if ($script:InnerRec.status -eq "recording") {
        Add-Timeline "wait_inner_completion" "Waiting for inner to finish..."
        $finalInner = Wait-ForRecordingCompleted -RecordingId $script:InnerRec.recording_id -MaxWaitSeconds ($InnerDurationSeconds + 30)
        if ($finalInner) {
            $script:InnerRec.status = $finalInner.status
        }
    }

    # --- Wait for outer to complete ---
    if ($script:OuterRec.status -eq "recording") {
        Add-Timeline "wait_outer_completion" "Waiting for outer to finish..."
        $finalOuter = Wait-ForRecordingCompleted -RecordingId $script:OuterRec.recording_id -MaxWaitSeconds ($OuterDurationSeconds + 30)
        if ($finalOuter) {
            $script:OuterRec.status = $finalOuter.status
        }
    }

    # Determine final result
    $outerDone = $script:OuterRec.status -eq "completed"
    $innerDone = $null -ne $script:InnerRec -and $script:InnerRec.status -eq "completed"

    if ($script:ProbeResult -eq "not_supported_single_recording_only") {
        # 409 is the definitive answer — no change needed
        Add-Timeline "probe_conclusion" "409 confirmed: single recording only"
    }
    elseif ($outerDone -and $innerDone) {
        $script:ProbeResult = "supported"
        $script:ProbeDetail = "Both outer and inner recordings completed"
        Add-Timeline "probe_success" "Nested recording is supported"
    }
    elseif ($outerDone -and (($null -ne $script:InnerRec) -and ($script:InnerRec.status -eq "pending_confirmation" -or $script:InnerRec.status -eq "rejected_409"))) {
        # Outer done, inner failed to start — this is a partial success
        $script:ProbeResult = "not_supported_single_recording_only"
        $script:ProbeDetail = "Outer completed but inner was rejected"
        Add-Timeline "probe_partial" "Outer done, inner not started"
    }
    elseif ($script:ProbeResult -eq "blocked_no_human_confirmation") {
        # Already set — inner was not attempted
        Add-Timeline "probe_conclusion" "Blocked by no human confirmation; inner was not attempted"
    }
    elseif ($script:ProbeResult -eq "blocked_preflight_not_ready") {
        # Already set — outer was not attempted
        Add-Timeline "probe_conclusion" "Blocked by preflight; outer was not attempted"
    }
    else {
        $script:ProbeDetail = "Outer=$($script:OuterRec.status), Inner=$(if($null -ne $script:InnerRec){$script:InnerRec.status}else{'null'})"
        Add-Timeline "probe_inconclusive" $script:ProbeDetail
    }
}

# ==================== Entry Point ====================

try {
    if (-not $SkipBuild) {
        Write-Host "[BUILD] Building AgentRecorder.sln..."
        Push-Location $ProjectRoot
        dotnet build AgentRecorder.sln -c Release --nologo 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[ERROR] Build failed"
            exit 1
        }
        Pop-Location
    }

    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }
    if (-not (Test-Path $ReportDir)) {
        New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null
    }

    Invoke-Probe
}
catch {
    $script:ProbeResult = "failed_unknown"
    $script:ProbeDetail = "Exception: $($_.Exception.Message)"
    Add-Timeline "exception" $_.Exception.Message
    Write-Host "[ERROR] $($_.Exception.Message)"
}
finally {
    Stop-DemoApp

    # Cleanup checks
    $script:Cleanup.lock_removed = -not (Test-Path $LockFile)
    $portCheck = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue | Where-Object { $_.State -eq "Listen" }
    $script:Cleanup.port_free = ($null -eq $portCheck)

    $finalProcs = Get-NewAgentProcesses
    $script:Cleanup.new_agent_processes = @($finalProcs | Where-Object { $_ -notin $initialProcs })

    Save-Report
}

exit 0
