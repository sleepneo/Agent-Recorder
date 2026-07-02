#Requires -Version 5.1
<#
.SYNOPSIS
    Smoke test Agent Recorder API by calling /api/v1/capabilities.
#>

param(
    [string]$ApiUrl = "http://127.0.0.1:37891/api/v1"
)

$ErrorActionPreference = "Stop"

# Bypass system proxy for localhost calls
[System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy($null)

$PackageRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$keyFile = Join-Path $PackageRoot ".local-data\config\api-key.txt"

Write-Host "Agent Recorder API Smoke Test" -ForegroundColor Cyan
Write-Host "  URL: $ApiUrl/capabilities"

# Build headers
$headers = @{}
if (Test-Path $keyFile) {
    $apiKey = (Get-Content $keyFile -Raw).Trim()
    if ($apiKey) {
        $headers["X-Agent-Recorder-Key"] = $apiKey
        Write-Host "  API key: loaded from .local-data\config\api-key.txt"
    }
} else {
    Write-Host "  API key: not found (some endpoints may fail)" -ForegroundColor Yellow
}

try {
    $resp = Invoke-RestMethod "$ApiUrl/capabilities" -UseBasicParsing -TimeoutSec 5 -Headers $headers
    Write-Host ""
    Write-Host "[OK] API is reachable" -ForegroundColor Green
    Write-Host ""

    if (-not (Test-Path $keyFile)) {
        Write-Host "[WARN] API key file not found at: $keyFile" -ForegroundColor Yellow
        Write-Host "  Recording endpoints will fail without a valid API key."
        Write-Host "  Run 'start-agent-recorder.ps1' first to initialize the key."
    }

    Write-Host "Capabilities:" -ForegroundColor Cyan

    $caps = $resp.data

    if ($caps.recording) {
        $r = $caps.recording
        Write-Host "  Sources: $($r.sources -join ', ')"
        Write-Host "  Containers: $($r.containers -join ', ')"
        Write-Host "  Codecs: $($r.codecs -join ', ')"
        Write-Host "  FPS: $($r.fps -join ', ')"
        Write-Host "  Max concurrent: $($r.max_concurrent_recordings)"
    }

    if ($caps.recording -and $caps.recording.nested_recording_mvp) {
        $n = $caps.recording.nested_recording_mvp
        if ($n.supported) {
            Write-Host "  Nested recording: supported (max $($n.max_concurrent), roles: $($n.roles))" -ForegroundColor Green
        }
    }

    if ($caps.safety) {
        Write-Host "  Safety: requires_confirmation=$($caps.safety.requires_confirmation), audit_log=$($caps.safety.audit_log)"
    }

    exit 0
} catch {
    Write-Host "[FAIL] API not reachable: $_" -ForegroundColor Red
    exit 1
}
