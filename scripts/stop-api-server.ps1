#Requires -Version 5.1
<#
.SYNOPSIS
    Stop the Agent Recorder headless API server.

.DESCRIPTION
    Reads metadata file (headless-api-server.json) to find the per-instance
    shutdown event and PID. Requests a graceful shutdown via the named event,
    then falls back to PID file and process enumeration. Verifies that port
    37891 is no longer LISTENING. Does not attempt to stop legacy
    AgentRecorder.App processes that are not listening on the API port.

.PARAMETER MetadataFile
    Path to the metadata JSON file written by start-api-server.ps1.

.PARAMETER PidFile
    Optional fallback path to the PID file.

.PARAMETER Port
    API port to verify (default: 37891).
#>

param(
    [string]$MetadataFile = "D:\works\python\007-Agent-Recorder\.local-data\headless-api-server.json",
    [string]$PidFile = "D:\works\python\007-Agent-Recorder\.local-data\headless-api-server.pid",
    [int]$Port = 37891
)

$ErrorActionPreference = "Stop"

function Test-PortListening {
    $netstat = netstat -ano | Select-String ":$Port"
    foreach ($line in $netstat) {
        if ($line -match "\s+LISTENING\s+(\d+)") {
            return [int]$matches[1]
        }
    }
    return $null
}

function Request-GracefulShutdown {
    param([string]$EventName)
    if ([string]::IsNullOrWhiteSpace($EventName)) { return $false }
    try {
        $evt = [System.Threading.EventWaitHandle]::OpenExisting($EventName)
        $evt.Set() | Out-Null
        $evt.Dispose()
        return $true
    } catch {
        return $false
    }
}

function Stop-PidIfHeadless {
    param([int]$ProcessId)
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($proc -and $proc.ProcessName -eq "AgentRecorder.Headless") {
        try {
            $proc | Stop-Process -Force -ErrorAction Stop
            Write-Host "Stopped PID=$ProcessId (AgentRecorder.Headless)" -ForegroundColor Green
            return $true
        } catch {
            Write-Host "Warning: could not stop PID=$ProcessId`: $_" -ForegroundColor Yellow
            return $false
        }
    }
    return $false
}

Write-Host "Stopping AgentRecorder.Headless..." -ForegroundColor Cyan

$metadataPid = $null
$metadataEvent = $null

if (Test-Path $MetadataFile) {
    try {
        $metadata = Get-Content $MetadataFile -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ($metadata.pid) { $metadataPid = [int]$metadata.pid }
        if ($metadata.shutdown_event_name) { $metadataEvent = [string]$metadata.shutdown_event_name }
        Write-Host "[INFO] Loaded metadata: pid=$metadataPid, event=$metadataEvent" -ForegroundColor Cyan
    } catch {
        Write-Host "[WARN] Could not parse metadata file: $_" -ForegroundColor Yellow
    }
}

if ($metadataEvent) {
    $gracefulRequested = Request-GracefulShutdown -EventName $metadataEvent
    if ($gracefulRequested) {
        Write-Host "[OK] Graceful shutdown requested via named event: $metadataEvent" -ForegroundColor Green
        Start-Sleep -Seconds 3
    }
} else {
    Write-Host "[INFO] No shutdown event name in metadata; skipping named event request" -ForegroundColor Yellow
}

if ($metadataPid) {
    $proc = Get-Process -Id $metadataPid -ErrorAction SilentlyContinue
    if ($proc -and $proc.ProcessName -eq "AgentRecorder.Headless") {
        Stop-PidIfHeadless -ProcessId $metadataPid | Out-Null
    }
}

if (Test-Path $PidFile) {
    $pidValue = Get-Content $PidFile -Raw -ErrorAction SilentlyContinue
    if ($pidValue -and ($pidValue -match '^\d+$')) {
        $pidInt = [int]$pidValue
        if ($pidInt -ne $metadataPid) {
            Stop-PidIfHeadless -ProcessId $pidInt | Out-Null
        }
    }
}

$headless = @(Get-Process AgentRecorder.Headless -ErrorAction SilentlyContinue)
foreach ($proc in $headless) {
    if ($proc.Id -eq $metadataPid) { continue }
    try {
        $proc | Stop-Process -Force -ErrorAction Stop
        Write-Host "Stopped AgentRecorder.Headless PID=$($proc.Id)" -ForegroundColor Green
    } catch {
        Write-Host "Warning: could not stop AgentRecorder.Headless PID=$($proc.Id)`: $_" -ForegroundColor Yellow
    }
}

Start-Sleep -Seconds 2

$listenerPid = Test-PortListening
if ($null -ne $listenerPid) {
    Write-Host "[WARN] Port $Port still LISTENING on PID=$listenerPid; attempting final stop..." -ForegroundColor Yellow
    $proc = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
    if ($proc -and $proc.ProcessName -eq "AgentRecorder.Headless") {
        try { $proc | Stop-Process -Force -ErrorAction Stop } catch { }
    }
    Start-Sleep -Seconds 2
    $listenerPid = Test-PortListening
    if ($null -ne $listenerPid) {
        Write-Host "[FAIL] Port $Port still LISTENING on PID=$listenerPid" -ForegroundColor Red
        exit 1
    }
}

$remaining = @(Get-Process AgentRecorder.Headless -ErrorAction SilentlyContinue)
if ($remaining.Count -gt 0) {
    Write-Host "[FAIL] AgentRecorder.Headless still running: $($remaining.Id -join ', ')" -ForegroundColor Red
    exit 1
}

if (Test-Path $MetadataFile) {
    Remove-Item $MetadataFile -Force -ErrorAction SilentlyContinue
}
if (Test-Path $PidFile) {
    Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
}

Write-Host "[OK] AgentRecorder.Headless stopped and port $Port is free" -ForegroundColor Green
