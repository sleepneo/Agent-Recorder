#Requires -Version 5.1
<#
.SYNOPSIS
    Non-interactive probe to verify API JSON contract is parsed correctly.

.DESCRIPTION
    Verifies that:
    1. POST /region-selections with an unsupported purpose returns 400 INVALID_ARGUMENT
       (not 500 INTERNAL_ERROR — which would indicate double-JSON encoding)
    2. POST /recordings with a structurally valid body but bad display_id returns 404 SOURCE_NOT_FOUND
       (not 400 "source is required" — which would indicate the body wasn't parsed as JSON)

    This probe does NOT open any UI or create pending recordings.

.PARAMETER ApiUrl
    Base API URL. Default: http://127.0.0.1:37891/api/v1
#>

param(
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
$headers = @{ "X-Agent-Recorder-Key" = $apiKey; "X-Agent-Name" = "contract-probe" }

Write-Host "=== Non-Interactive API Contract Probe ===" -ForegroundColor Cyan
Write-Host "API URL: $ApiUrl"
Write-Host ""

$passed = 0
$failed = 0

# --- Helper: Invoke API and return status code + response ---
function Invoke-Probe {
    param(
        [string]$Method,
        [string]$Endpoint,
        [object]$Body
    )
    $url = "$ApiUrl$Endpoint"
    $bodyJson = $Body | ConvertTo-Json -Depth 10
    try {
        # Use Invoke-WebRequest instead of Invoke-RestMethod for better error handling
        $resp = Invoke-WebRequest -Method $Method -Uri $url -Headers $headers `
            -Body $bodyJson -ContentType "application/json" `
            -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        $respObj = $resp.Content | ConvertFrom-Json
        return @{ StatusCode = $resp.StatusCode; Response = $respObj }
    } catch {
        $statusCode = 0
        $respObj = $null

        if ($_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode

            # Read response stream for error details
            try {
                $stream = $_.Exception.Response.GetResponseStream()
                # CRITICAL: Reset stream position before reading!
                if ($stream.CanSeek) {
                    $stream.Position = 0
                }
                $reader = New-Object System.IO.StreamReader($stream)
                $respBody = $reader.ReadToEnd()
                $reader.Close()
                $stream.Dispose()

                if ($respBody) {
                    $respObj = $respBody | ConvertFrom-Json
                }
            } catch {
                # Stream read failed
            }
        }

        return @{ StatusCode = $statusCode; Response = $respObj; Error = $_.Exception.Message }
    }
}

# --- Test 1: region-selections with unsupported purpose ---
Write-Host "[1/2] Testing POST /region-selections with unsupported purpose..." -ForegroundColor Yellow

$regionBody = @{
    purpose = "contract_probe"
    timeout_seconds = 10
}

$result = Invoke-Probe -Method Post -Endpoint "/region-selections" -Body $regionBody
$status = $result.StatusCode
$resp = $result.Response

Write-Host "  Status: $status"

if ($status -eq 400) {
    $errCode = "unknown"
    $errMsg = "unknown"
    if ($resp -and $resp.error) {
        if ($resp.error.code) { $errCode = $resp.error.code }
        if ($resp.error.message) { $errMsg = $resp.error.message }
    }
    Write-Host "  Error code: $errCode"
    Write-Host "  Error message: $errMsg"
    if ($errCode -eq "INVALID_ARGUMENT") {
        Write-Host "  [PASS] Got expected 400 INVALID_ARGUMENT (body parsed correctly)" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  [FAIL] Expected error code INVALID_ARGUMENT, got: $errCode" -ForegroundColor Red
        $failed++
    }
} elseif ($status -eq 500) {
    $errMsg = if ($resp -and $resp.error -and $resp.error.message) { $resp.error.message } else { "unknown" }
    Write-Host "  [FAIL] Got 500 INTERNAL_ERROR — likely double-JSON encoding bug" -ForegroundColor Red
    Write-Host "  Message: $errMsg" -ForegroundColor Red
    $failed++
} else {
    Write-Host "  [FAIL] Unexpected status: $status" -ForegroundColor Red
    $failed++
}

Write-Host ""

# --- Test 2: recordings with invalid display_id (structurally valid body) ---
Write-Host "[2/2] Testing POST /recordings with invalid display_id..." -ForegroundColor Yellow

$recBody = @{
    source = @{
        type = "display"
        display_id = "display_DOES_NOT_EXIST"
    }
    audio = @{
        microphone = @{ enabled = $false }
    }
    video = @{
        fps = 15
        quality = "medium"
    }
    output = @{
        directory = "default"
        filename_template = "contract-probe-{datetime}"
    }
    stop_condition = @{
        type = "duration"
        seconds = 1
    }
    safety = @{
        require_user_confirmation = $true
    }
}

$result = Invoke-Probe -Method Post -Endpoint "/recordings" -Body $recBody
$status = $result.StatusCode
$resp = $result.Response

Write-Host "  Status: $status"

if ($status -eq 404) {
    $errCode = "unknown"
    $errMsg = "unknown"
    if ($resp -and $resp.error) {
        if ($resp.error.code) { $errCode = $resp.error.code }
        if ($resp.error.message) { $errMsg = $resp.error.message }
    }
    Write-Host "  Error code: $errCode"
    Write-Host "  Error message: $errMsg"
    if ($errCode -eq "SOURCE_NOT_FOUND") {
        Write-Host "  [PASS] Got expected 404 SOURCE_NOT_FOUND (nested source object parsed correctly)" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "  [FAIL] Expected error code SOURCE_NOT_FOUND, got: $errCode" -ForegroundColor Red
        $failed++
    }
} elseif ($status -eq 400) {
    $errMsg = "unknown"
    if ($resp -and $resp.error -and $resp.error.message) { $errMsg = $resp.error.message }
    Write-Host "  [FAIL] Got 400 — body may not have been parsed as JSON object" -ForegroundColor Red
    Write-Host "  Message: $errMsg" -ForegroundColor Red
    $failed++
} elseif ($status -eq 500) {
    $errMsg = "unknown"
    if ($resp -and $resp.error -and $resp.error.message) { $errMsg = $resp.error.message }
    Write-Host "  [FAIL] Got 500 INTERNAL_ERROR — likely double-JSON encoding bug" -ForegroundColor Red
    Write-Host "  Message: $errMsg" -ForegroundColor Red
    $failed++
} else {
    Write-Host "  [FAIL] Unexpected status: $status" -ForegroundColor Red
    $failed++
}

Write-Host ""

# --- Summary ---
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "  Passed: $passed"
Write-Host "  Failed: $failed"
Write-Host ""

if ($failed -eq 0) {
    Write-Host "All contract probes passed!" -ForegroundColor Green
    Write-Host "JSON body is being parsed correctly (no double-encoding)." -ForegroundColor Green
    exit 0
} else {
    Write-Host "Some contract probes failed." -ForegroundColor Red
    Write-Host "This usually indicates the request body is not being parsed as a JSON object." -ForegroundColor Yellow
    Write-Host "Common cause: double-JSON encoding (body serialized twice)." -ForegroundColor Yellow
    exit 1
}
