#Requires -Version 5.1
<#
.SYNOPSIS
    Agent Recorder - API Recording Helper

.DESCRIPTION
    Initiates a recording request via API and waits for user confirmation.
    User must manually click the confirmation dialog to start recording.
    This script does NOT auto-confirm.

    Usage:
        .\record-api-sample.ps1 -SourceType display -Duration 5 -Fps 30 -Quality medium
        .\record-api-sample.ps1 -SourceType window -WindowId window_123 -Duration 5 -Fps 30 -Quality medium

.PARAMETER SourceType
    'display' or 'window'

.PARAMETER DisplayId
    Display ID (e.g., 'display_1') - required if SourceType is 'display'

.PARAMETER WindowId
    Window ID (e.g., 'window_123') - required if SourceType is 'window'

.PARAMETER Duration
    Recording duration in seconds (default: 5)

.PARAMETER Fps
    Frames per second (default: 30)

.PARAMETER Quality
    Video quality: 'low', 'medium', 'high' (default: 'medium')
#>

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('display', 'window')]
    [string]$SourceType,

    [string]$DisplayId = "display_1",

    [string]$WindowId,

    [int]$Duration = 5,

    [int]$Fps = 30,

    [ValidateSet('low', 'medium', 'high')]
    [string]$Quality = "medium"
)

$ErrorActionPreference = "Continue"
$BaseUrl = "http://127.0.0.1:37891"
$ApiPrefix = "$BaseUrl/api/v1"

# Get API Key
$apiKeyFile = ".local-data\config\api-key.txt"
if (Test-Path $apiKeyFile) {
    $apiKey = (Get-Content $apiKeyFile -Raw).Trim()
} else {
    Write-Host "[ERROR] API Key file not found: $apiKeyFile" -ForegroundColor Red
    Write-Host "Please start the server first." -ForegroundColor Yellow
    exit 1
}

# Build request body
$body = @{
    source = @{}
    audio = @{ microphone = @{ enabled = $false } }
    video = @{
        fps = $Fps
        quality = $Quality
    }
    stop_condition = @{
        type = "duration"
        seconds = $Duration
    }
} | ConvertTo-Json -Depth 5

if ($SourceType -eq 'display') {
    $bodyJson = $body | ConvertFrom-Json
    $bodyJson.source | Add-Member -NotePropertyName "type" -NotePropertyValue "display" -Force
    $bodyJson.source | Add-Member -NotePropertyName "display_id" -NotePropertyValue $DisplayId -Force
    $body = $bodyJson | ConvertTo-Json -Depth 5
} else {
    if ([string]::IsNullOrWhiteSpace($WindowId)) {
        Write-Host "[ERROR] WindowId is required for window recording" -ForegroundColor Red
        exit 1
    }
    $bodyJson = $body | ConvertFrom-Json
    $bodyJson.source | Add-Member -NotePropertyName "type" -NotePropertyValue "window" -Force
    $bodyJson.source | Add-Member -NotePropertyName "window_id" -NotePropertyValue $WindowId -Force
    $body = $bodyJson | ConvertTo-Json -Depth 5
}

Write-Host ""
Write-Host "========================================================"
Write-Host " Agent Recorder - API Recording Request"
Write-Host "========================================================"
Write-Host "Source Type: $SourceType"
if ($SourceType -eq 'display') {
    Write-Host "Display ID:  $DisplayId"
} else {
    Write-Host "Window ID:  $WindowId"
}
Write-Host "Duration:    ${Duration}s"
Write-Host "FPS:        $Fps"
Write-Host "Quality:    $Quality"
Write-Host "========================================================"

Write-Host ""
Write-Host "Sending recording request..."

try {
    $response = Invoke-RestMethod -Uri "$ApiPrefix/recordings" `
        -Method POST `
        -Headers @{
            "X-Agent-Recorder-Key" = $apiKey
            "Content-Type" = "application/json"
        } `
        -Body $body `
        -ErrorAction Stop

    if ($response.ok -eq $true) {
        Write-Host "[OK] Recording request accepted" -ForegroundColor Green
        Write-Host ""
        Write-Host "Recording ID:       $($response.data.recording_id)" -ForegroundColor Cyan
        Write-Host "Confirmation ID:    $($response.data.confirmation_id)" -ForegroundColor Cyan
        Write-Host "Output Path:       $($response.data.output_path)" -ForegroundColor White

        Write-Host ""
        Write-Host "========================================================" -ForegroundColor Yellow
        Write-Host " ACTION REQUIRED: Please click CONFIRM in the dialog" -ForegroundColor Yellow
        Write-Host " The recording will start after confirmation" -ForegroundColor Yellow
        Write-Host " Confirmation ID: $($response.data.confirmation_id)" -ForegroundColor Yellow
        Write-Host "========================================================" -ForegroundColor Yellow

        # Return info for caller
        return @{
            RecordingId = $response.data.recording_id
            ConfirmationId = $response.data.confirmation_id
            OutputPath = $response.data.output_path
        }
    } else {
        Write-Host "[ERROR] Recording request failed: $($response.error.code) - $($response.error.message)" -ForegroundColor Red
        exit 1
    }
}
catch {
    $msg = $_.Exception.Message
    Write-Host "[ERROR] Failed to send recording request: $msg" -ForegroundColor Red
    exit 1
}
