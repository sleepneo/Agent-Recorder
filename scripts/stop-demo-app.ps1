#Requires -Version 5.1
<#
.SYNOPSIS
    Stop the Agent Recorder demo app.

.DESCRIPTION
    Reads metadata file (demo-app-server.json) to find the PID of the current
    demo app. Stops only that specific instance. Excludes historical stale
    PID 49364 unless it occupies port 37891.

.PARAMETER MetadataFile
    Path to the metadata JSON file written by start-demo-app.ps1.

.PARAMETER Port
    API port to verify (default: 37891).

.EXAMPLE
    .\scripts\stop-demo-app.ps1
#>

param(
    [string]$MetadataFile = "D:\works\python\007-Agent-Recorder\.local-data\demo-app-server.json",
    [int]$Port = 37891
)

$ErrorActionPreference = "Stop"
$StaleAppPid = 49364

function Test-PortListening {
    $netstat = netstat -ano | Select-String ":$Port"
    foreach ($line in $netstat) {
        if ($line -match "\s+LISTENING\s+(\d+)") {
            return [int]$matches[1]
        }
    }
    return $null
}

Write-Host "Stopping AgentRecorder demo app..." -ForegroundColor Cyan

$metadataPid = $null

if (Test-Path $MetadataFile) {
    try {
        $metadata = Get-Content $MetadataFile -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ($metadata.pid) { $metadataPid = [int]$metadata.pid }
        Write-Host "[INFO] Loaded metadata: pid=$metadataPid" -ForegroundColor Cyan
    } catch {
        Write-Host "[WARN] Could not parse metadata file: $_" -ForegroundColor Yellow
    }
}

$stoppedAny = $false

if ($metadataPid) {
    $proc = Get-Process -Id $metadataPid -ErrorAction SilentlyContinue
    if ($proc -and $proc.ProcessName -eq "AgentRecorder.App") {
        try {
            $proc | Stop-Process -Force -ErrorAction Stop
            Write-Host "[OK] Stopped PID=$metadataPid (AgentRecorder.App)" -ForegroundColor Green
            $stoppedAny = $true
        } catch {
            Write-Host "[WARN] Could not stop PID=$metadataPid`: $_" -ForegroundColor Yellow
        }
    } elseif ($proc) {
        Write-Host "[INFO] PID=$metadataPid is $($proc.ProcessName), not AgentRecorder.App; skipping" -ForegroundColor Cyan
    } else {
        Write-Host "[INFO] PID=$metadataPid not running" -ForegroundColor Cyan
    }
}

$listenerPid = Test-PortListening
if ($null -ne $listenerPid) {
    if ($listenerPid -eq $StaleAppPid) {
        Write-Host "[INFO] Port $Port occupied by stale PID=$StaleAppPid; will not clean up" -ForegroundColor Yellow
    } else {
        $proc = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
        if ($proc -and ($proc.ProcessName -eq "AgentRecorder.App" -or $proc.ProcessName -eq "AgentRecorder.Headless")) {
            if ($listenerPid -ne $metadataPid) {
                try {
                    $proc | Stop-Process -Force -ErrorAction Stop
                    Write-Host "[OK] Stopped $($proc.ProcessName) PID=$listenerPid on port $Port" -ForegroundColor Green
                    $stoppedAny = $true
                } catch {
                    Write-Host "[WARN] Could not stop PID=$listenerPid`: $_" -ForegroundColor Yellow
                }
            }
        }
    }
}

Start-Sleep -Seconds 2

$listenerPid2 = Test-PortListening
if ($null -ne $listenerPid2 -and $listenerPid2 -ne $StaleAppPid) {
    Write-Host "[WARN] Port $Port still LISTENING on PID=$listenerPid2; attempting final stop..." -ForegroundColor Yellow
    $proc = Get-Process -Id $listenerPid2 -ErrorAction SilentlyContinue
    if ($proc -and ($proc.ProcessName -eq "AgentRecorder.App" -or $proc.ProcessName -eq "AgentRecorder.Headless")) {
        try { $proc | Stop-Process -Force -ErrorAction Stop } catch { }
    }
    Start-Sleep -Seconds 2
    $listenerPid2 = Test-PortListening
    if ($null -ne $listenerPid2 -and $listenerPid2 -ne $StaleAppPid) {
        Write-Host "[FAIL] Port $Port still LISTENING on PID=$listenerPid2" -ForegroundColor Red
        exit 1
    }
}

$remainingApp = @(Get-Process AgentRecorder.App -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $StaleAppPid })
if ($remainingApp.Count -gt 0) {
    Write-Host "[WARN] AgentRecorder.App still running (excluding stale $StaleAppPid): $($remainingApp.Id -join ', ')" -ForegroundColor Yellow
} else {
    Write-Host "[OK] No extra AgentRecorder.App processes (stale PID=$StaleAppPid excluded)" -ForegroundColor Green
}

if (Test-Path $MetadataFile) {
    try {
        Remove-Item $MetadataFile -Force -ErrorAction Stop
        Write-Host "[OK] Metadata file removed: $MetadataFile" -ForegroundColor Green
    } catch {
        Write-Host "[WARN] Could not remove metadata file: $_" -ForegroundColor Yellow
    }
}

$finalListener = Test-PortListening
if ($null -eq $finalListener -or $finalListener -eq $StaleAppPid) {
    Write-Host "[OK] Port $Port is free (or occupied only by stale PID=$StaleAppPid)" -ForegroundColor Green
} else {
    Write-Host "[FAIL] Port $Port still LISTENING on PID=$finalListener" -ForegroundColor Red
    exit 1
}

Write-Host "[OK] Demo app stopped successfully" -ForegroundColor Green
