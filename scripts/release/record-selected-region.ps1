#Requires -Version 5.1
<#
.SYNOPSIS
    Run a single-layer user-selected-region recording.

.DESCRIPTION
    1. Requests a region selection from the user (POST /api/v1/region-selections)
    2. Creates a region recording with the selected bounds
    3. Handles user confirmation via polling /confirmations/{id}
    4. Polls until recording completes
    5. Prints output path and metadata

.PARAMETER DurationSeconds
    Recording duration in seconds. Default: 30

.PARAMETER Fps
    Frames per second. Default: 15

.PARAMETER Quality
    Video quality (low, medium, high). Default: medium

.PARAMETER ApiUrl
    Base API URL. Default: http://127.0.0.1:37891/api/v1
#>

param(
    [int]$DurationSeconds = 30,
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

# Load API key
if (-not (Test-Path $keyFile)) {
    Write-Host "[ERROR] API key file not found: $keyFile" -ForegroundColor Red
    Write-Host "Run 'start-agent-recorder.ps1' first to generate the API key." -ForegroundColor Yellow
    exit 1
}
$apiKey = (Get-Content $keyFile -Raw).Trim()
if (-not $apiKey) {
    Write-Host "[ERROR] API key file is empty: $keyFile" -ForegroundColor Red
    Write-Host "Run 'start-agent-recorder.ps1' to regenerate the API key." -ForegroundColor Yellow
    exit 1
}
$headers = @{ "X-Agent-Recorder-Key" = $apiKey; "X-Agent-Name" = "release-demo" }

Write-Host "=== Selected-Region Recording Demo ===" -ForegroundColor Cyan
Write-Host "  Duration: ${DurationSeconds}s"
Write-Host "  FPS: $Fps"
Write-Host "  Quality: $Quality"
Write-Host ""

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

# --- Step 1: Region selection ---
Write-Host "[1/4] Requesting region selection..." -ForegroundColor Yellow
Write-Host "[ACTION] Please draw and confirm a region on screen." -ForegroundColor Magenta

$regionBody = @{
    purpose = "recording"
    timeout_seconds = 300
}

$data = Invoke-Api -Method Post -Endpoint "/region-selections" -Body $regionBody -TimeoutSec 320

if ($data.status -ne "selected") {
    $reason = if ($data.reason) { "reason: $($data.reason)" } else { "" }
    Write-Host "[ERROR] Region selection failed: status=$($data.status) $reason" -ForegroundColor Red
    exit 1
}

$displayId = $data.display_id
$coordSpace = $data.coordinate_space
$bounds = $data.bounds

Write-Host "[OK] Region selected:" -ForegroundColor Green
Write-Host "  Display: $displayId"
Write-Host "  Coordinate space: $coordSpace"
Write-Host "  Bounds: x=$($bounds.x), y=$($bounds.y), w=$($bounds.width), h=$($bounds.height)"
Write-Host ""

# --- Step 2: Create recording ---
Write-Host "[2/4] Creating region recording..." -ForegroundColor Yellow

$recBody = @{
    source = @{
        type = "region"
        display_id = $displayId
        coordinate_space = $coordSpace
        bounds = @{
            x = $bounds.x
            y = $bounds.y
            width = $bounds.width
            height = $bounds.height
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
        filename_template = "recording-{datetime}"
    }
    stop_condition = @{
        type = "duration"
        seconds = $DurationSeconds
    }
    safety = @{
        require_user_confirmation = $true
    }
}

$recData = Invoke-Api -Method Post -Endpoint "/recordings" -Body $recBody

# Handle confirmation flow
$recordingId = $null
if ($recData.status -eq "requires_user_confirmation") {
    $confirmationId = $recData.confirmation_id
    Write-Host "[OK] Recording pending confirmation: $confirmationId" -ForegroundColor Green
    Write-Host ""
    Write-Host "[ACTION] Please confirm the recording in the system tray or pop-up window." -ForegroundColor Magenta

    # Poll /confirmations/{id} until approved
    $deadline = (Get-Date).AddSeconds(120)
    $confirmed = $false
    while ((Get-Date) -lt $deadline) {
        $confData = Invoke-Api -Endpoint "/confirmations/$confirmationId" -TimeoutSec 5
        if ($confData.status -eq "approved") {
            $recordingId = $confData.recording_id
            $confirmed = $true
            break
        }
        if ($confData.status -eq "rejected") {
            Write-Host "[ERROR] Recording was rejected by user" -ForegroundColor Red
            exit 1
        }
        if ($confData.status -eq "expired") {
            Write-Host "[ERROR] Recording confirmation expired" -ForegroundColor Red
            exit 1
        }
        Start-Sleep -Milliseconds 500
    }

    if (-not $confirmed) {
        Write-Host "[ERROR] Recording was not confirmed within 120s" -ForegroundColor Red
        exit 1
    }

    Write-Host "[OK] Recording confirmed: $recordingId" -ForegroundColor Green
} elseif ($recData.status -eq "recording" -and $recData.recording_id) {
    $recordingId = $recData.recording_id
    Write-Host "[OK] Recording started directly: $recordingId" -ForegroundColor Green
} else {
    Write-Host "[ERROR] Unexpected recording status: $($recData.status)" -ForegroundColor Red
    exit 1
}

Write-Host ""

# --- Step 3: Wait for completion ---
Write-Host "[3/4] Waiting for recording to complete..." -ForegroundColor Yellow

$deadline = (Get-Date).AddSeconds($DurationSeconds + 60)
$completed = $false
$finalData = $null
while ((Get-Date) -lt $deadline) {
    $statusData = Invoke-Api -Endpoint "/recordings/$recordingId" -TimeoutSec 5
    $finalData = $statusData

    if ($statusData.status -eq "completed") {
        $completed = $true
        break
    }
    if ($statusData.status -eq "failed") {
        $errMsg = if ($statusData.error_message) { $statusData.error_message } else { "unknown" }
        Write-Host "[ERROR] Recording failed: $errMsg" -ForegroundColor Red
        exit 1
    }
    if ($statusData.status -eq "cancelled" -or $statusData.status -eq "rejected") {
        Write-Host "[ERROR] Recording was $($statusData.status)" -ForegroundColor Red
        exit 1
    }
    Start-Sleep -Seconds 1
}

if (-not $completed) {
    Write-Host "[ERROR] Recording did not complete in time" -ForegroundColor Red
    exit 1
}

Write-Host "[OK] Recording completed" -ForegroundColor Green
Write-Host ""

# --- Step 4: Print results ---
Write-Host "[4/4] Results:" -ForegroundColor Cyan
Write-Host "  Recording ID: $recordingId"

if ($finalData.output) {
    $out = $finalData.output
    Write-Host "  Output path: $($out.path)"
    Write-Host "  Duration: $($out.duration_seconds)s"
    if ($out.width -and $out.height) {
        Write-Host "  Resolution: $($out.width)x$($out.height)"
    }
    if ($out.bytes_written) {
        $sizeMB = [math]::Round($out.bytes_written / 1MB, 2)
        Write-Host "  File size: $sizeMB MB"
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
