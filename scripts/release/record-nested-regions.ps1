#Requires -Version 5.1
<#
.SYNOPSIS
    Run a nested recording demo (outer + inner), both using Agent Recorder.

.DESCRIPTION
    1. Starts an outer recording (display) with nested.role=outer
    2. After a short delay, starts an inner recording (region) with nested.role=inner and parent_recording_id
    3. Waits for both to complete
    4. Prints output paths

.PARAMETER OuterDurationSeconds
    Outer recording duration. Default: 60

.PARAMETER InnerDurationSeconds
    Inner recording duration. Default: 20

.PARAMETER InnerStartDelaySeconds
    Seconds to wait after outer starts before starting inner. Default: 8

.PARAMETER Fps
    Frames per second for both recordings. Default: 15

.PARAMETER Quality
    Video quality. Default: medium

.PARAMETER ApiUrl
    Base API URL. Default: http://127.0.0.1:37891/api/v1
#>

param(
    [int]$OuterDurationSeconds = 60,
    [int]$InnerDurationSeconds = 20,
    [int]$InnerStartDelaySeconds = 8,
    [int]$Fps = 15,
    [ValidateSet("low", "medium", "high")]
    [string]$Quality = "medium",
    [string]$ApiUrl = "http://127.0.0.1:37891/api/v1"
)

$ErrorActionPreference = "Stop"

# Bypass system proxy for localhost calls
[System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy($null)

$PackageRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$keyFile = Join-Path $PackageRoot ".local-data\config\api-key.txt"

if (-not (Test-Path $keyFile)) {
    Write-Host "[ERROR] API key file not found: $keyFile" -ForegroundColor Red
    Write-Host "Run 'start-agent-recorder.ps1' first to generate the API key." -ForegroundColor Yellow
    exit 1
}
$apiKey = (Get-Content $keyFile -Raw).Trim()
if (-not $apiKey) {
    Write-Host "[ERROR] API key file is empty: $keyFile" -ForegroundColor Red
    exit 1
}
$headers = @{ "X-Agent-Recorder-Key" = $apiKey; "X-Agent-Name" = "release-demo" }

# --- Helper: Call API and unwrap response ---
function Invoke-Api {
    param(
        [string]$Method = "GET",
        [string]$Endpoint,
        [object]$Body = $null,
        [int]$TimeoutSec = 30
    )
    $url = "$ApiUrl$Endpoint"
    $params = @{
        Uri = $url
        Method = $Method
        Headers = $headers
        ContentType = "application/json"
        UseBasicParsing = $true
        TimeoutSec = $TimeoutSec
    }
    if ($null -ne $Body) {
        if ($Body -is [string]) {
            $params.Body = $Body
        } else {
            $params.Body = $Body | ConvertTo-Json -Depth 10
        }
    }
    try {
        $resp = Invoke-RestMethod @params
        if (-not $resp.ok) {
            $errCode = $resp.error.code
            $errMsg = $resp.error.message
            $reqId = $resp.request_id
            throw "API error: $errCode - $errMsg (request_id: $reqId)"
        }
        return $resp.data
    } catch {
        if ($_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
            Write-Host "[ERROR] HTTP $status calling $Method $Endpoint : $_" -ForegroundColor Red
        } else {
            Write-Host "[ERROR] $Method $Endpoint : $_" -ForegroundColor Red
        }
        throw
    }
}

# --- Helper: Wait for recording to start (confirmation) ---
function Wait-RecordingConfirmation {
    param([string]$ConfirmationId, [int]$TimeoutSeconds = 120)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $confData = Invoke-Api -Endpoint "/confirmations/$ConfirmationId" -TimeoutSec 5
        if ($confData.status -eq "approved") {
            return $confData.recording_id
        }
        if ($confData.status -eq "rejected") {
            throw "Recording was rejected by user"
        }
        if ($confData.status -eq "expired") {
            throw "Recording confirmation expired"
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Recording not confirmed within ${TimeoutSeconds}s"
}

# --- Helper: Wait for recording to complete ---
function Wait-RecordingComplete {
    param([string]$RecordingId, [int]$TimeoutSeconds = 300)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $statusData = Invoke-Api -Endpoint "/recordings/$RecordingId" -TimeoutSec 5
        if ($statusData.status -eq "completed") {
            return $statusData
        }
        if ($statusData.status -eq "failed") {
            throw "Recording $RecordingId failed"
        }
        if ($statusData.status -eq "cancelled" -or $statusData.status -eq "rejected") {
            throw "Recording $RecordingId was $($statusData.status)"
        }
        Start-Sleep -Seconds 1
    }
    throw "Recording $RecordingId did not complete within ${TimeoutSeconds}s"
}

$sessionId = "nested-$(Get-Date -Format 'yyyyMMddHHmmss')"

Write-Host "=== Nested Recording Demo ===" -ForegroundColor Cyan
Write-Host "  Session: $sessionId"
Write-Host "  Outer: ${OuterDurationSeconds}s display"
Write-Host "  Inner: ${InnerDurationSeconds}s region (starts after ${InnerStartDelaySeconds}s)"
Write-Host "  FPS: $Fps, Quality: $Quality"
Write-Host ""

# --- Get display info ---
Write-Host "[0/5] Getting display info..." -ForegroundColor Yellow
$displaysData = Invoke-Api -Endpoint "/displays"
$displays = $displaysData.displays

if ($displays.Count -eq 0) {
    Write-Host "[ERROR] No displays available" -ForegroundColor Red
    exit 1
}

# Find primary display, or use first
$primaryDisplay = $displays | Where-Object { $_.is_primary } | Select-Object -First 1
$display = if ($primaryDisplay) { $primaryDisplay } else { $displays[0] }
$displayId = $display.id

Write-Host "[OK] Using display: $displayId ($($display.name))" -ForegroundColor Green
Write-Host ""

# --- Outer ---
Write-Host "[1/5] Creating outer recording (nested.role=outer)..." -ForegroundColor Yellow

$outerBody = @{
    source = @{
        type = "display"
        display_id = $displayId
    }
    audio = @{
        microphone = @{ enabled = $false }
    }
    video = @{
        fps = $Fps
        quality = $Quality
    }
    output = @{
        directory = "default"
        filename_template = "nested-outer-{datetime}"
    }
    stop_condition = @{
        type = "duration"
        seconds = $OuterDurationSeconds
    }
    nested = @{
        role = "outer"
        session_id = $sessionId
    }
    safety = @{
        require_user_confirmation = $true
    }
}

$outerRecData = Invoke-Api -Method Post -Endpoint "/recordings" -Body $outerBody

if ($outerRecData.status -ne "requires_user_confirmation") {
    Write-Host "[ERROR] Expected requires_user_confirmation, got: $($outerRecData.status)" -ForegroundColor Red
    exit 1
}

$outerConfId = $outerRecData.confirmation_id
Write-Host "[OK] Outer recording pending confirmation: $outerConfId" -ForegroundColor Green
Write-Host "[ACTION] Please confirm the OUTER recording in the system tray." -ForegroundColor Magenta

$outerId = Wait-RecordingConfirmation -ConfirmationId $outerConfId
Write-Host "[OK] Outer recording started: $outerId" -ForegroundColor Green
Write-Host ""

# --- Wait before inner ---
Write-Host "[2/5] Waiting ${InnerStartDelaySeconds}s before inner recording..." -ForegroundColor Yellow
Start-Sleep -Seconds $InnerStartDelaySeconds
Write-Host ""

# --- Inner: Region selection ---
Write-Host "[3/5] Creating inner recording (nested.role=inner, with region)..." -ForegroundColor Yellow

Write-Host "[ACTION] Please draw and confirm the INNER region on screen." -ForegroundColor Magenta

$regionBody = @{
    purpose = "recording"
    timeout_seconds = 300
}

$innerRegionData = Invoke-Api -Method Post -Endpoint "/region-selections" -Body $regionBody -TimeoutSec 320

if ($innerRegionData.status -ne "selected") {
    $reason = if ($innerRegionData.reason) { $innerRegionData.reason } else { "" }
    Write-Host "[ERROR] Inner region selection failed: status=$($innerRegionData.status) reason=$reason" -ForegroundColor Red
    exit 1
}

Write-Host "[OK] Inner region selected: $($innerRegionData.bounds.width)x$($innerRegionData.bounds.height)" -ForegroundColor Green

$innerBody = @{
    source = @{
        type = "region"
        display_id = $innerRegionData.display_id
        coordinate_space = $innerRegionData.coordinate_space
        bounds = @{
            x = $innerRegionData.bounds.x
            y = $innerRegionData.bounds.y
            width = $innerRegionData.bounds.width
            height = $innerRegionData.bounds.height
        }
    }
    audio = @{
        microphone = @{ enabled = $false }
    }
    video = @{
        fps = $Fps
        quality = $Quality
    }
    output = @{
        directory = "default"
        filename_template = "nested-inner-{datetime}"
    }
    stop_condition = @{
        type = "duration"
        seconds = $InnerDurationSeconds
    }
    nested = @{
        role = "inner"
        parent_recording_id = $outerId
        session_id = $sessionId
    }
    safety = @{
        require_user_confirmation = $true
    }
}

$innerRecData = Invoke-Api -Method Post -Endpoint "/recordings" -Body $innerBody

if ($innerRecData.status -ne "requires_user_confirmation") {
    Write-Host "[ERROR] Expected requires_user_confirmation, got: $($innerRecData.status)" -ForegroundColor Red
    exit 1
}

$innerConfId = $innerRecData.confirmation_id
Write-Host "[OK] Inner recording pending confirmation: $innerConfId" -ForegroundColor Green
Write-Host "[ACTION] Please confirm the INNER recording in the system tray." -ForegroundColor Magenta

$innerId = Wait-RecordingConfirmation -ConfirmationId $innerConfId
Write-Host "[OK] Inner recording started: $innerId" -ForegroundColor Green
Write-Host ""

# --- Wait for both ---
Write-Host "[4/5] Waiting for both recordings to complete..." -ForegroundColor Yellow
Write-Host "  Outer: ${OuterDurationSeconds}s, Inner: ${InnerDurationSeconds}s"

$innerFinal = Wait-RecordingComplete -RecordingId $innerId -TimeoutSeconds ($InnerDurationSeconds + 60)
Write-Host "[OK] Inner recording completed: $innerId" -ForegroundColor Green

$outerFinal = Wait-RecordingComplete -RecordingId $outerId -TimeoutSeconds ($OuterDurationSeconds + 60)
Write-Host "[OK] Outer recording completed: $outerId" -ForegroundColor Green
Write-Host ""

# --- Results ---
Write-Host "[5/5] Results:" -ForegroundColor Cyan
Write-Host ""

Write-Host "  Outer Recording:"
Write-Host "    ID: $outerId"
if ($outerFinal.output) {
    $out = $outerFinal.output
    Write-Host "    Output: $($out.path)"
    Write-Host "    Duration: $($out.duration_seconds)s"
    if ($out.width -and $out.height) {
        Write-Host "    Resolution: $($out.width)x$($out.height)"
    }
}
Write-Host ""

Write-Host "  Inner Recording:"
Write-Host "    ID: $innerId"
Write-Host "    Parent: $outerId"
if ($innerFinal.output) {
    $out = $innerFinal.output
    Write-Host "    Output: $($out.path)"
    Write-Host "    Duration: $($out.duration_seconds)s"
    if ($out.width -and $out.height) {
        Write-Host "    Resolution: $($out.width)x$($out.height)"
    }
}
Write-Host ""

Write-Host "Nested recording demo complete." -ForegroundColor Green
