#Requires -Version 5.1
<#
.SYNOPSIS
    Start Agent Recorder tray app and wait for API readiness.
#>

param(
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

# Bypass system proxy for localhost calls
[System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy($null)

$PackageRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$AppPath = Join-Path $PackageRoot "AgentRecorder.App\AgentRecorder.App.exe"
$DataDir = Join-Path $PackageRoot ".local-data"
$ApiBase = "http://127.0.0.1:37891/api/v1"
$ApiUrl = "$ApiBase/capabilities"
$keyFile = Join-Path $DataDir "config\api-key.txt"

if (-not (Test-Path $AppPath)) {
    Write-Host "[ERROR] AgentRecorder.App.exe not found at: $AppPath" -ForegroundColor Red
    exit 1
}

$env:AGENT_RECORDER_DATA_DIR = $DataDir

Write-Host "Starting Agent Recorder..." -ForegroundColor Cyan
Write-Host "  App: $AppPath"
Write-Host "  Data dir: $DataDir"

# Check if already running
try {
    $existing = Invoke-RestMethod $ApiUrl -UseBasicParsing -TimeoutSec 2 -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "[OK] Agent Recorder is already running" -ForegroundColor Green
        Write-Host "  API: $ApiUrl"
        exit 0
    }
} catch { }

# Start the app
$proc = Start-Process -FilePath $AppPath -PassThru -WindowStyle Minimized
Write-Host "[OK] Started Agent Recorder (PID: $($proc.Id))" -ForegroundColor Green

# Wait for API readiness
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
Write-Host "Waiting for API to become ready (up to ${TimeoutSeconds}s)..."

$ready = $false
while ((Get-Date) -lt $deadline) {
    try {
        $caps = Invoke-RestMethod $ApiUrl -UseBasicParsing -TimeoutSec 3
        if ($caps -and $caps.ok -and $caps.data -and $caps.data.recording) {
            $ready = $true
            break
        }
    } catch { }
    Start-Sleep -Milliseconds 500
}

if (-not $ready) {
    Write-Host "[ERROR] API did not become ready within ${TimeoutSeconds}s" -ForegroundColor Red
    exit 1
}

Write-Host "[OK] Agent Recorder is ready" -ForegroundColor Green
Write-Host ""

# Trigger API key generation by calling a protected endpoint
Write-Host "Ensuring API key is generated..." -ForegroundColor Cyan
try {
    $keyGenResp = Invoke-WebRequest "$ApiBase/recordings" -Method Get -UseBasicParsing -TimeoutSec 5 -ErrorAction SilentlyContinue
    if ($keyGenResp.StatusCode -eq 200) {
        Write-Host "[OK] API key already exists"
    }
} catch {
    # 401 is expected - key is being generated on first protected call
    if ($_.Exception.Response.StatusCode -eq 401) {
        Write-Host "[OK] API key generation triggered (401 expected on first call)"
    } else {
        Write-Host "[WARN] Unexpected response during key generation: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# Wait up to 5 seconds for the key file to appear
$keyReady = $false
$keyDeadline = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $keyDeadline) {
    if (Test-Path $keyFile) {
        $keyReady = $true
        break
    }
    Start-Sleep -Milliseconds 200
}

if ($keyReady) {
    Write-Host "[OK] API key file ready: $keyFile"
} else {
    Write-Host "[WARN] API key file not yet available: $keyFile" -ForegroundColor Yellow
    Write-Host "  The key will be generated on the first protected API call."
}

Write-Host ""
Write-Host "To stop: .\scripts\release\stop-agent-recorder.ps1"
Write-Host "To smoke test: .\scripts\release\smoke-capabilities.ps1"
