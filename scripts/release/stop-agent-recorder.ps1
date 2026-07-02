#Requires -Version 5.1
<#
.SYNOPSIS
    Stop Agent Recorder tray app.
#>

$ErrorActionPreference = "Stop"

# Bypass system proxy for localhost calls
[System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy($null)

$ApiUrl = "http://127.0.0.1:37891/api/v1"

Write-Host "Stopping Agent Recorder..." -ForegroundColor Cyan

# Try graceful shutdown via API first
try {
    $apiKey = $null
    $keyFile = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) ".local-data\config\api-key.txt"
    if (Test-Path $keyFile) {
        $apiKey = (Get-Content $keyFile -Raw).Trim()
    }
    $headers = @{}
    if ($apiKey) { $headers["X-Agent-Recorder-Key"] = $apiKey }

    $r = Invoke-WebRequest "$ApiUrl/server/shutdown" -Method Post -UseBasicParsing -TimeoutSec 5 -Headers $headers -ErrorAction SilentlyContinue
    if ($r.StatusCode -eq 200 -or $r.StatusCode -eq 202) {
        Write-Host "[OK] Shutdown requested via API" -ForegroundColor Green
        Start-Sleep -Seconds 2
    }
} catch { }

# Find and stop AgentRecorder.App processes
$stopped = 0
Get-Process | Where-Object { $_.Name -eq "AgentRecorder.App" } | ForEach-Object {
    try {
        Stop-Process -Id $_.Id -Force -ErrorAction Stop
        Write-Host "[OK] Stopped AgentRecorder.App (PID: $($_.Id))" -ForegroundColor Green
        $stopped++
    } catch {
        Write-Host "[WARN] Could not stop PID $($_.Id): $_" -ForegroundColor Yellow
    }
}

if ($stopped -eq 0) {
    Write-Host "[INFO] No AgentRecorder.App processes found" -ForegroundColor Gray
}

# Check port
$portFree = $true
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect("127.0.0.1", 37891)
    $tcp.Close()
    $portFree = $false
} catch { }

if ($portFree) {
    Write-Host "[OK] Port 37891 is free" -ForegroundColor Green
} else {
    Write-Host "[WARN] Port 37891 still appears occupied" -ForegroundColor Yellow
}
