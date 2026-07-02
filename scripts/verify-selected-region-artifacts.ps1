#Requires -Version 5.1
<#
.SYNOPSIS
    严格验证选区录制 artifact。

.DESCRIPTION
    验证选区录制产生的 manifest JSON。支持两种模式：
    - 默认模式：仅接受 completed 状态，任何非 completed 状态都直接失败。
    - 允许阻断模式 (-AllowBlocked)：按状态语义分别验证。

    状态语义：
    - completed: 验证 ffprobe duration、分辨率、非零大小、本地确认通过
    - no_display / display_enumeration_unavailable: 验证无 MP4、blocker_detail 清晰、cleanup 自洽
    - selection_cancelled / selection_timeout: 验证无 MP4、blocker_detail 清晰
    - confirmation_rejected / confirmation_expired: 可以有 selected_region、recording_id、confirmation_id
    - recording_failed: 可以有 recording_id、confirmation_id

.EXAMPLE
    # 验证成功的选区录制
    .\scripts\verify-selected-region-artifacts.ps1 -ManifestPath "path\to\manifest.json"

.EXAMPLE
    # 验证被阻断的选区录制
    .\scripts\verify-selected-region-artifacts.ps1 -ManifestPath "path\to\manifest.json" -AllowBlocked
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$ManifestPath,
    [int]$ExpectedDurationSeconds = 10,
    [int]$MinDurationSeconds = 8,
    [switch]$AllowBlocked = $false
)

$ErrorActionPreference = "Stop"
$ProjectRoot = "D:\works\python\007-Agent-Recorder"
$ToolsDir = Join-Path $ProjectRoot "tools"
$ffprobeExe = Join-Path $ToolsDir "ffmpeg\bin\ffprobe.exe"

$script:tests = @()
$script:passed = 0
$script:failed = 0

function Add-Test {
    param([string]$Name, [bool]$Result, [string]$Detail = "")
    $script:tests += [pscustomobject]@{
        name = $Name
        result = $Result
        detail = $Detail
    }
    if ($Result) {
        $script:passed++
        Write-Host "[PASS] $Name" -ForegroundColor Green
        if ($Detail) { Write-Host "       $Detail" -ForegroundColor Gray }
    }
    else {
        $script:failed++
        Write-Host "[FAIL] $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "       $Detail" -ForegroundColor Yellow }
    }
}

function Get-PropertyExists {
    param($Object, [string]$Name)
    return ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name)
}

function Test-StringNonEmpty {
    param([string]$Value)
    return (-not [string]::IsNullOrWhiteSpace($Value))
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Selected Region Artifacts Verification" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Manifest: $ManifestPath"
Write-Host "Mode: $(if ($AllowBlocked) { 'ALLOW_BLOCKED' } else { 'STRICT_SUCCESS' })"
Write-Host ""

# ============================================================
# A: Manifest 基本结构
# ============================================================
Write-Host "=== A: Manifest Structure ===" -ForegroundColor Yellow

if (-not (Test-Path $ManifestPath)) {
    Write-Host "[FATAL] Manifest not found: $ManifestPath" -ForegroundColor Red
    exit 1
}

$manifest = $null
try {
    $content = Get-Content $ManifestPath -Raw -Encoding UTF8 -ErrorAction Stop
    $manifest = $content | ConvertFrom-Json -ErrorAction Stop
    Add-Test "A1: Manifest is valid JSON" $true
}
catch {
    Add-Test "A1: Manifest is valid JSON" $false "Parse error: $($_.Exception.Message)"
    Write-Host ""
    Write-Host "RESULT: FAILED" -ForegroundColor Red
    exit 1
}

$status = $null
if (Get-PropertyExists -Object $manifest -Name "status") {
    $status = $manifest.status
    Add-Test "A2: status field exists" $true "status='$status'"
} else {
    Add-Test "A2: status field exists" $false "status field missing"
}

Add-Test "A3: selected_region field exists" (Get-PropertyExists -Object $manifest -Name "selected_region")
Add-Test "A4: recording_id field exists" (Get-PropertyExists -Object $manifest -Name "recording_id")
Add-Test "A5: confirmation_id field exists" (Get-PropertyExists -Object $manifest -Name "confirmation_id")
Add-Test "A6: mp4_path field exists" (Get-PropertyExists -Object $manifest -Name "mp4_path")
Add-Test "A7: duration_requested field exists" (Get-PropertyExists -Object $manifest -Name "duration_requested")
Add-Test "A8: duration_actual field exists" (Get-PropertyExists -Object $manifest -Name "duration_actual")
Add-Test "A9: width field exists" (Get-PropertyExists -Object $manifest -Name "width")
Add-Test "A10: height field exists" (Get-PropertyExists -Object $manifest -Name "height")
Add-Test "A11: cleanup field exists" (Get-PropertyExists -Object $manifest -Name "cleanup")
Add-Test "A12: success_criteria field exists" (Get-PropertyExists -Object $manifest -Name "success_criteria")

$hasEvidence = Get-PropertyExists -Object $manifest -Name "evidence"
Add-Test "A13: evidence field exists" $hasEvidence
if ($hasEvidence) {
    Add-Test "A14: evidence.runner_report field exists" (Get-PropertyExists -Object $manifest.evidence -Name "runner_report")
} else {
    Add-Test "A14: evidence.runner_report field exists" $false "evidence missing"
}

$hasFfmpegArgsDirect = Get-PropertyExists -Object $manifest -Name "ffmpeg_command_args"
$hasRunnerForFfmpeg = ($hasEvidence -and (Get-PropertyExists -Object $manifest.evidence -Name "runner_report"))
Add-Test "A15: ffmpeg_command_args access path exists (direct or via runner_report)" ($hasFfmpegArgsDirect -or $hasRunnerForFfmpeg)

# ============================================================
# B: 状态验证与 AllowBlocked 模式
# ============================================================
Write-Host ""
Write-Host "=== B: Status Validation ===" -ForegroundColor Yellow

$validStatuses = @(
    "completed",
    "no_display",
    "display_enumeration_unavailable",
    "selection_cancelled",
    "selection_timeout",
    "confirmation_rejected",
    "confirmation_expired",
    "recording_failed"
)

$statusRecognized = ($status -in $validStatuses)
Add-Test "B1: Status is recognized" $statusRecognized "Status='$status'"

if (-not $AllowBlocked) {
    $isCompleted = ($status -eq "completed")
    Add-Test "B2: Without -AllowBlocked, status must be completed" $isCompleted `
        "Status='$status' (use -AllowBlocked to validate blocked states)"
    if (-not $isCompleted) {
        Write-Host ""
        Write-Host "============================================================" -ForegroundColor Red
        Write-Host "  RESULT: FAILED - non-completed status without -AllowBlocked" -ForegroundColor Red
        Write-Host "============================================================" -ForegroundColor Red
        exit 1
    }
}
else {
    Add-Test "B2: -AllowBlocked enabled, blocked states permitted" $true
}

if ($status -ne "completed") {
    $hasBlockerType = Get-PropertyExists -Object $manifest -Name "blocker_type"
    Add-Test "B3: non-completed status has blocker_type field" $hasBlockerType
    if ($hasBlockerType) {
        Add-Test "B4: blocker_type is non-empty" (Test-StringNonEmpty $manifest.blocker_type) `
            "blocker_type='$($manifest.blocker_type)'"
    } else {
        Add-Test "B4: blocker_type is non-empty" $false "blocker_type field missing"
    }

    $hasBlockerDetail = Get-PropertyExists -Object $manifest -Name "blocker_detail"
    Add-Test "B5: non-completed status has blocker_detail field" $hasBlockerDetail
    if ($hasBlockerDetail) {
        Add-Test "B6: blocker_detail is non-empty and descriptive" (Test-StringNonEmpty $manifest.blocker_detail) `
            "blocker_detail='$($manifest.blocker_detail)'"
    } else {
        Add-Test "B6: blocker_detail is non-empty and descriptive" $false "blocker_detail field missing"
    }
}

$noDisplayStates = @("no_display", "display_enumeration_unavailable")
$selectionBlockerStates = @("selection_cancelled", "selection_timeout")
$confirmationStates = @("confirmation_rejected", "confirmation_expired")
$recordingFailedStates = @("recording_failed")

# ============================================================
# C: completed 状态专属验证
# ============================================================
if ($status -eq "completed") {
    Write-Host ""
    Write-Host "=== C: Completed Status Validation ===" -ForegroundColor Yellow

    $mp4Path = $manifest.mp4_path
    $hasMp4Path = Test-StringNonEmpty $mp4Path
    Add-Test "C1: mp4_path is set" $hasMp4Path $(if (-not $hasMp4Path) { "mp4_path is empty" } else { "mp4_path='$mp4Path'" })

    $mp4Exists = $hasMp4Path -and (Test-Path $mp4Path)
    Add-Test "C2: MP4 file exists" $mp4Exists $(if (-not $mp4Exists) { "File not found: $mp4Path" } else { "" })

    if ($mp4Exists) {
        $mp4Size = (Get-Item $mp4Path).Length
        $sizeValid = $mp4Size -gt 512
        Add-Test "C3: MP4 size > 512 bytes" $sizeValid "Size=$mp4Size bytes"
    }
    else {
        Add-Test "C3: MP4 size > 512 bytes" $false "MP4 does not exist"
    }

    $ffprobeAvailable = Test-Path $ffprobeExe
    Add-Test "C4: ffprobe.exe available" $ffprobeAvailable `
        $(if (-not $ffprobeAvailable) { "ffprobe not found at: $ffprobeExe" } else { "" })

    if ($ffprobeAvailable -and $mp4Exists) {
        try {
            $fpOutput = & $ffprobeExe -v quiet -print_format json -show_format -show_streams $mp4Path 2>&1
            $fpJson = $fpOutput | ConvertFrom-Json -ErrorAction Stop

            $fpDuration = 0
            if ($fpJson.format.duration) { $fpDuration = [double]$fpJson.format.duration }
            $durationOk = $fpDuration -ge $MinDurationSeconds
            Add-Test "C5: ffprobe duration >= $MinDurationSeconds seconds" $durationOk `
                "Duration=$fpDuration seconds (expected >= $MinDurationSeconds)"

            $videoStream = $fpJson.streams | Where-Object { $_.codec_type -eq "video" } | Select-Object -First 1
            $fpWidth = if ($videoStream.width) { [int]$videoStream.width } else { 0 }
            $fpHeight = if ($videoStream.height) { [int]$videoStream.height } else { 0 }
            $dimensionsOk = ($fpWidth -gt 0) -and ($fpHeight -gt 0)
            Add-Test "C6: ffprobe width/height > 0" $dimensionsOk `
                "Width=$fpWidth, Height=$fpHeight"

            if ($fpWidth -gt 0 -and $fpHeight -gt 0) {
                $manifestWidth = [int]$manifest.width
                $manifestHeight = [int]$manifest.height
                $resMatch = ($fpWidth -eq $manifestWidth -and $fpHeight -eq $manifestHeight)
                Add-Test "C7: ffprobe resolution matches manifest width/height" $resMatch `
                    "Expected: ${manifestWidth}x${manifestHeight}, Got: ${fpWidth}x${fpHeight}"
            }
        }
        catch {
            Add-Test "C5: ffprobe duration >= $MinDurationSeconds seconds" $false "Error: $($_.Exception.Message)"
            Add-Test "C6: ffprobe width/height > 0" $false "ffprobe parse error"
        }
    }
    else {
        Add-Test "C5: ffprobe duration >= $MinDurationSeconds seconds" $false "Skipped (ffprobe or MP4 unavailable)"
        Add-Test "C6: ffprobe width/height > 0" $false "Skipped"
    }

    $hasSelectedRegion = ($null -ne $manifest.selected_region)
    Add-Test "C8: selected_region is present" $hasSelectedRegion
    if ($hasSelectedRegion) {
        $region = $manifest.selected_region
        Add-Test "C9: selected_region has display_id" (Get-PropertyExists -Object $region -Name "display_id")
        Add-Test "C10: selected_region has coordinate_space" (Get-PropertyExists -Object $region -Name "coordinate_space")
        Add-Test "C11: selected_region has bounds" (Get-PropertyExists -Object $region -Name "bounds")
        if (Get-PropertyExists -Object $region -Name "bounds") {
            $bounds = $region.bounds
            Add-Test "C12: bounds.width is even (x264/yuv420p compatible)" ($bounds.width % 2 -eq 0) "width=$($bounds.width)"
            Add-Test "C13: bounds.height is even" ($bounds.height % 2 -eq 0) "height=$($bounds.height)"
            Add-Test "C14: bounds.width is positive" ($bounds.width -gt 0)
            Add-Test "C15: bounds.height is positive" ($bounds.height -gt 0)
        }
    }

    $hasRecordingId = Test-StringNonEmpty $manifest.recording_id
    Add-Test "C16: recording_id is non-empty" $hasRecordingId `
        $(if (-not $hasRecordingId) { "recording_id is empty" } else { "recording_id='$($manifest.recording_id)'" })

    $hasConfirmationId = Test-StringNonEmpty $manifest.confirmation_id
    Add-Test "C17: confirmation_id is non-empty" $hasConfirmationId `
        $(if (-not $hasConfirmationId) { "confirmation_id is empty" } else { "confirmation_id='$($manifest.confirmation_id)'" })

    $hasConfirmationStatus = (Get-PropertyExists -Object $manifest -Name "confirmation_status")
    Add-Test "C18: confirmation_status field exists" $hasConfirmationStatus
    if ($hasConfirmationStatus) {
        $confirmationApproved = ($manifest.confirmation_status -eq "approved")
        Add-Test "C19: confirmation_status is approved (local confirmation passed)" $confirmationApproved `
            "confirmation_status='$($manifest.confirmation_status)'"
    } else {
        Add-Test "C19: confirmation_status is approved (local confirmation passed)" $false "confirmation_status missing"
    }

    $hasDurationRequested = ($null -ne $manifest.duration_requested)
    Add-Test "C20: duration_requested is present" $hasDurationRequested
    if ($hasDurationRequested) {
        $reqMatch = ([int]$manifest.duration_requested -eq $ExpectedDurationSeconds)
        Add-Test "C21: duration_requested == ExpectedDurationSeconds" $reqMatch `
            "Expected=$ExpectedDurationSeconds, Got=$($manifest.duration_requested)"
    }

    $hasDurationActual = ($null -ne $manifest.duration_actual)
    Add-Test "C22: duration_actual is present" $hasDurationActual
}

# ============================================================
# D: no_display / display_enumeration_unavailable 专属验证
# ============================================================
if ($status -in $noDisplayStates) {
    Write-Host ""
    Write-Host "=== D: No Display / Display Enumeration Unavailable ===" -ForegroundColor Yellow

    $noMp4 = ([string]::IsNullOrWhiteSpace($manifest.mp4_path))
    Add-Test "D1: No MP4 for display-blocked state" $noMp4 `
        $(if (-not $noMp4) { "mp4_path='$($manifest.mp4_path)' but status is $status" } else { "MP4 path correctly empty" })

    Add-Test "D2: blocker_detail is descriptive" (Test-StringNonEmpty $manifest.blocker_detail) `
        "blocker_detail='$($manifest.blocker_detail)'"

    if (Get-PropertyExists -Object $manifest -Name "cleanup") {
        $cu = $manifest.cleanup
        $appStopped = (Get-PropertyExists -Object $cu -Name "app_stopped" -and $cu.app_stopped -eq $true)
        $portFree = (Get-PropertyExists -Object $cu -Name "port_free" -and $cu.port_free -eq $true)
        $lockReleased = (Get-PropertyExists -Object $cu -Name "lock_released" -and $cu.lock_released -eq $true)
        Add-Test "D3: cleanup.app_stopped = true" $appStopped
        Add-Test "D4: cleanup.port_free = true" $portFree
        Add-Test "D5: cleanup.lock_released = true" $lockReleased

        $cleanupSelfConsistent = $appStopped -and $portFree -and $lockReleased
        Add-Test "D6: cleanup is self-consistent (all true for display blocker)" $cleanupSelfConsistent
    }
    else {
        Add-Test "D3: cleanup.app_stopped = true" $false "cleanup not present"
        Add-Test "D4: cleanup.port_free = true" $false "cleanup not present"
        Add-Test "D5: cleanup.lock_released = true" $false "cleanup not present"
        Add-Test "D6: cleanup is self-consistent" $false "cleanup not present"
    }
}

# ============================================================
# E: selection_cancelled / selection_timeout 专属验证
# ============================================================
if ($status -in $selectionBlockerStates) {
    Write-Host ""
    Write-Host "=== E: Selection Cancelled / Timeout ===" -ForegroundColor Yellow

    $noMp4 = ([string]::IsNullOrWhiteSpace($manifest.mp4_path))
    Add-Test "E1: No MP4 for selection-blocked state" $noMp4 `
        $(if (-not $noMp4) { "mp4_path='$($manifest.mp4_path)' but status is $status" } else { "MP4 path correctly empty" })

    Add-Test "E2: blocker_detail is descriptive" (Test-StringNonEmpty $manifest.blocker_detail) `
        "blocker_detail='$($manifest.blocker_detail)'"
}

# ============================================================
# F: confirmation_rejected / confirmation_expired 专属验证
# ============================================================
if ($status -in $confirmationStates) {
    Write-Host ""
    Write-Host "=== F: Confirmation Rejected / Expired ===" -ForegroundColor Yellow

    $hasSelectedRegion = ($null -ne $manifest.selected_region)
    Add-Test "F1: selected_region may be present (confirmation stage)" $true `
        "selected_region=$(if ($hasSelectedRegion) { 'present (allowed)' } else { 'absent (allowed)' })"

    $hasRecordingId = Test-StringNonEmpty $manifest.recording_id
    Add-Test "F2: recording_id may be present (confirmation stage)" $true `
        "recording_id=$(if ($hasRecordingId) { 'present (allowed)' } else { 'absent (allowed)' })"

    $hasConfirmationId = Test-StringNonEmpty $manifest.confirmation_id
    Add-Test "F3: confirmation_id may be present (confirmation stage)" $true `
        "confirmation_id=$(if ($hasConfirmationId) { 'present (allowed)' } else { 'absent (allowed)' })"

    $noMp4 = ([string]::IsNullOrWhiteSpace($manifest.mp4_path))
    Add-Test "F4: No MP4 for confirmation-blocked state" $noMp4 `
        $(if (-not $noMp4) { "mp4_path='$($manifest.mp4_path)' but status is $status" } else { "MP4 path correctly empty" })
}

# ============================================================
# G: recording_failed 专属验证
# ============================================================
if ($status -in $recordingFailedStates) {
    Write-Host ""
    Write-Host "=== G: Recording Failed ===" -ForegroundColor Yellow

    $hasRecordingId = Test-StringNonEmpty $manifest.recording_id
    Add-Test "G1: recording_id may be present (recording started then failed)" $true `
        "recording_id=$(if ($hasRecordingId) { 'present (allowed)' } else { 'absent (allowed)' })"

    $hasConfirmationId = Test-StringNonEmpty $manifest.confirmation_id
    Add-Test "G2: confirmation_id may be present (recording started then failed)" $true `
        "confirmation_id=$(if ($hasConfirmationId) { 'present (allowed)' } else { 'absent (allowed)' })"

    $noMp4 = ([string]::IsNullOrWhiteSpace($manifest.mp4_path))
    Add-Test "G3: No MP4 for recording_failed state" $noMp4 `
        $(if (-not $noMp4) { "mp4_path='$($manifest.mp4_path)' but status is recording_failed" } else { "MP4 path correctly empty" })
}

# ============================================================
# H: runner_report 验证
# ============================================================
Write-Host ""
Write-Host "=== H: Runner Report Validation ===" -ForegroundColor Yellow

$runnerReport = $null
if (Get-PropertyExists -Object $manifest -Name "evidence" -and (Get-PropertyExists -Object $manifest.evidence -Name "runner_report")) {
    $runnerReport = $manifest.evidence.runner_report
}

if ($null -eq $runnerReport -or [string]::IsNullOrWhiteSpace($runnerReport)) {
    Add-Test "H1: runner_report is optional (absent is OK)" $true "runner_report is empty/null"
}
else {
    Add-Test "H1: runner_report is present" $true "runner_report='$runnerReport'"

    $rrIsString = ($runnerReport -is [string])
    Add-Test "H2: runner_report is a string path" $rrIsString

    if ($rrIsString) {
        $isRegionRunner = ($runnerReport -match "demo-record-region-.*\.json")
        Add-Test "H3: runner_report matches demo-record-region-*.json pattern" $isRegionRunner `
            "runner_report='$runnerReport'"

        $isNotWindowRunner = ($runnerReport -notmatch "demo-record-window")
        Add-Test "H4: runner_report does NOT reference window runner (demo-record-window)" $isNotWindowRunner `
            $(if (-not $isNotWindowRunner) { "runner_report='$runnerReport' references window runner!" } else { "no window runner reference" })

        $rrExists = Test-Path $runnerReport
        Add-Test "H5: runner_report file exists if specified" $rrExists `
            $(if (-not $rrExists) { "File not found: $runnerReport" } else { "file exists" })
    }
}

# ============================================================
# I: FFmpeg 命令参数验证
# ============================================================
Write-Host ""
Write-Host "=== I: FFmpeg Command Args Validation ===" -ForegroundColor Yellow

$ffmpegArgsStr = $null
$ffmpegArgsSource = $null

$hasDirectArgs = $false
if (Get-PropertyExists -Object $manifest -Name "ffmpeg_command_args") {
    $directVal = $manifest.ffmpeg_command_args
    if ($null -ne $directVal -and $directVal -is [string] -and $directVal.Length -gt 0) {
        $hasDirectArgs = $true
        $ffmpegArgsStr = $directVal
        $ffmpegArgsSource = "manifest"
        Add-Test "I1: ffmpeg_command_args is non-empty (from manifest)" $true
    }
}

if (-not $hasDirectArgs -and $null -ne $runnerReport -and $runnerReport -is [string] -and (Test-Path $runnerReport)) {
    try {
        $rrContent = Get-Content $runnerReport -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $rrHasArgs = $false
        if (Get-PropertyExists -Object $rrContent -Name "recording" -and (Get-PropertyExists -Object $rrContent.recording -Name "command_args")) {
            $rrArgs = $rrContent.recording.command_args
            if ($null -ne $rrArgs -and $rrArgs -is [string] -and $rrArgs.Length -gt 0) {
                $rrHasArgs = $true
                $ffmpegArgsStr = $rrArgs
                $ffmpegArgsSource = "runner_report"
                Add-Test "I1: ffmpeg_command_args is non-empty (from runner report)" $true
            }
        }
        if (-not $rrHasArgs) {
            if ($status -eq "completed") {
                Add-Test "I1: ffmpeg_command_args is non-empty" $false "completed status requires ffmpeg_command_args"
            }
            else {
                Add-Test "I1: ffmpeg_command_args is non-empty" $true "no ffmpeg_command_args (expected for $status)"
            }
        }
    }
    catch {
        if ($status -eq "completed") {
            Add-Test "I1: ffmpeg_command_args is non-empty" $false "Error reading runner report: $($_.Exception.Message)"
        }
        else {
            Add-Test "I1: ffmpeg_command_args is non-empty" $true "runner report unreadable but status is $status"
        }
    }
}
elseif (-not $hasDirectArgs) {
    if ($status -eq "completed") {
        Add-Test "I1: ffmpeg_command_args is non-empty" $false "completed status requires ffmpeg_command_args or runner_report with command_args"
    }
    else {
        Add-Test "I1: ffmpeg_command_args is non-empty" $true "no ffmpeg_command_args (expected for $status)"
    }
}

if ($null -ne $ffmpegArgsStr -and $ffmpegArgsStr.Length -gt 0) {
    $hasOffsetX = ($ffmpegArgsStr -match "(?:^|\s)-offset_x\s+\d+")
    Add-Test "I2: command has -offset_x" $hasOffsetX `
        $(if (-not $hasOffsetX) { "args: $ffmpegArgsStr" } else { "offset_x found (source: $ffmpegArgsSource)" })

    $hasOffsetY = ($ffmpegArgsStr -match "(?:^|\s)-offset_y\s+\d+")
    Add-Test "I3: command has -offset_y" $hasOffsetY `
        $(if (-not $hasOffsetY) { "args: $ffmpegArgsStr" } else { "offset_y found (source: $ffmpegArgsSource)" })

    $hasVideoSize = ($ffmpegArgsStr -match "(?:^|\s)-video_size\s+\d+x\d+")
    Add-Test "I4: command has -video_size" $hasVideoSize `
        $(if (-not $hasVideoSize) { "args: $ffmpegArgsStr" } else { "video_size found (source: $ffmpegArgsSource)" })

    $hasDesktopSource = ($ffmpegArgsStr -match "(?:^|\s)-i\s+desktop")
    Add-Test "I5: command uses -i desktop (region/desktop source)" $hasDesktopSource `
        $(if (-not $hasDesktopSource) { "args: $ffmpegArgsStr" } else { "desktop source found (source: $ffmpegArgsSource)" })

    $noTitleSource = ($ffmpegArgsStr -notmatch "(?:^|\s)-i\s+title=")
    Add-Test "I6: command does NOT use -i title= (not window capture)" $noTitleSource `
        $(if (-not $noTitleSource) { "args: $ffmpegArgsStr" } else { "no title= source (source: $ffmpegArgsSource)" })
}
else {
    # No ffmpeg args available - expected for non-completed status
    Add-Test "I2: command has -offset_x" $true "no ffmpeg args (expected for $status)"
    Add-Test "I3: command has -offset_y" $true "no ffmpeg args (expected for $status)"
    Add-Test "I4: command has -video_size" $true "no ffmpeg args (expected for $status)"
    Add-Test "I5: command uses -i desktop (region/desktop source)" $true "no ffmpeg args (expected for $status)"
    Add-Test "I6: command does NOT use -i title= (not window capture)" $true "no ffmpeg args (expected for $status)"
}

# ============================================================
# J: Cleanup 验证
# ============================================================
Write-Host ""
Write-Host "=== J: Cleanup Validation ===" -ForegroundColor Yellow

if (Get-PropertyExists -Object $manifest -Name "cleanup") {
    $cu = $manifest.cleanup
    Add-Test "J1: cleanup has app_stopped" (Get-PropertyExists -Object $cu -Name "app_stopped")
    Add-Test "J2: cleanup has port_free" (Get-PropertyExists -Object $cu -Name "port_free")
    Add-Test "J3: cleanup has lock_released" (Get-PropertyExists -Object $cu -Name "lock_released")
}
else {
    Add-Test "J1: cleanup has app_stopped" $false "cleanup missing"
    Add-Test "J2: cleanup has port_free" $false "cleanup missing"
    Add-Test "J3: cleanup has lock_released" $false "cleanup missing"
}

# ============================================================
# K: success_criteria 验证
# ============================================================
Write-Host ""
Write-Host "=== K: Success Criteria Validation ===" -ForegroundColor Yellow

if (Get-PropertyExists -Object $manifest -Name "success_criteria") {
    $sc = $manifest.success_criteria
    Add-Test "K1: success_criteria has recording_completed" (Get-PropertyExists -Object $sc -Name "recording_completed")
    Add-Test "K2: success_criteria has mp4_exists" (Get-PropertyExists -Object $sc -Name "mp4_exists")
    Add-Test "K3: success_criteria has size_non_zero" (Get-PropertyExists -Object $sc -Name "size_non_zero")

    if ($status -eq "completed") {
        if (Get-PropertyExists -Object $sc -Name "recording_completed") {
            Add-Test "K4: recording_completed = true for completed status" ($sc.recording_completed -eq $true)
        }
        if (Get-PropertyExists -Object $sc -Name "mp4_exists") {
            Add-Test "K5: mp4_exists = true for completed status" ($sc.mp4_exists -eq $true)
        }
        if (Get-PropertyExists -Object $sc -Name "size_non_zero") {
            Add-Test "K6: size_non_zero = true for completed status" ($sc.size_non_zero -eq $true)
        }
    }
    else {
        if (Get-PropertyExists -Object $sc -Name "recording_completed") {
            Add-Test "K4: recording_completed = false for non-completed status" ($sc.recording_completed -eq $false)
        }
        if (Get-PropertyExists -Object $sc -Name "mp4_exists") {
            Add-Test "K5: mp4_exists = false for non-completed status" ($sc.mp4_exists -eq $false)
        }
    }
}
else {
    Add-Test "K1: success_criteria has recording_completed" $false "success_criteria missing"
    Add-Test "K2: success_criteria has mp4_exists" $false "success_criteria missing"
    Add-Test "K3: success_criteria has size_non_zero" $false "success_criteria missing"
}

# ============================================================
# 结果汇总
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  RESULT: $script:passed PASSED, $script:failed FAILED" -ForegroundColor $(if ($script:failed -eq 0) { "Green" } else { "Red" })
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

$total = $script:passed + $script:failed
if ($script:failed -eq 0) {
    Write-Host "SELECTED REGION ARTIFACTS: ALL PASSED ($total/$total)" -ForegroundColor Green
    exit 0
}
else {
    Write-Host "SELECTED REGION ARTIFACTS: FAILED ($script:passed/$total passed)" -ForegroundColor Red
    Write-Host ""
    Write-Host "Failed tests:" -ForegroundColor Yellow
    foreach ($t in $script:tests) {
        if (-not $t.result) {
            Write-Host "  - $($t.name)" -ForegroundColor Yellow
            if ($t.detail) { Write-Host "    $($t.detail)" -ForegroundColor Gray }
        }
    }
    exit 1
}
