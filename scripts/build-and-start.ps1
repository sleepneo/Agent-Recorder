$ErrorActionPreference = "Stop"
$ProjectRoot = "D:\works\python\007-Agent-Recorder"
Set-Location $ProjectRoot

function Set-ProcessEnvironmentSafely {
    param(
        [Parameter(Mandatory=$true)] [System.Diagnostics.ProcessStartInfo]$psi,
        [Parameter(Mandatory=$true)] [string]$name,
        [Parameter(Mandatory=$true)] [AllowEmptyString()] [string]$value
    )
    if ($null -ne $psi.Environment) {
        try { $psi.Environment[$name] = $value; return } catch {}
    }
    if ($null -ne $psi.EnvironmentVariables) {
        try { $psi.EnvironmentVariables[$name] = $value; return } catch {}
    }
    [System.Environment]::SetEnvironmentVariable($name, $value, "Process")
}

function Stop-OldAgentRecorderProcesses {
    $old = @(Get-Process AgentRecorder* -ErrorAction SilentlyContinue)
    if ($old.Count -eq 0) { return $true }

    $failedPids = @()
    foreach ($proc in $old) {
        try {
            $proc | Stop-Process -Force -ErrorAction Stop
        } catch {
            $failedPids += $proc.Id
        }
    }

    Start-Sleep -Seconds 2

    $stillRunning = @()
    foreach ($procId in $failedPids) {
        if (Get-Process -Id $procId -ErrorAction SilentlyContinue) {
            $stillRunning += $procId
        }
    }

    if ($stillRunning.Count -gt 0) {
        $portInUse = $false
        $netstat = netstat -ano | Select-String ":37891"
        foreach ($line in $netstat) {
            if ($line -match "\s+LISTENING\s+(\d+)") {
                $portInUse = $true
                break
            }
        }

        if ($portInUse) {
            Write-Host "Old AgentRecorder.App processes could not be stopped and port 37891 is still occupied. PIDs: $($stillRunning -join ', ')" -ForegroundColor Red
            return $false
        } else {
            Write-Host "Warning: old AgentRecorder.App processes could not be stopped, but port 37891 is free. Continuing... PIDs: $($stillRunning -join ', ')" -ForegroundColor Yellow
        }
    }

    return $true
}

Write-Host "[1/3] Stopping old processes..." -ForegroundColor Cyan
if (-not (Stop-OldAgentRecorderProcesses)) {
    exit 1
}

Write-Host "[2/3] Building..." -ForegroundColor Cyan
$buildOutput = dotnet build AgentRecorder.sln -c Release 2>&1
$buildExitCode = $LASTEXITCODE
$buildOutput | Select-Object -Last 10 | ForEach-Object { Write-Host $_ }

if ($buildExitCode -ne 0) {
    Write-Host ""
    Write-Host "BUILD FAILED with exit code $buildExitCode" -ForegroundColor Red
    Write-Host ""
    $buildOutput | ForEach-Object { Write-Host $_ }
    exit 1
}
Write-Host ""

Write-Host "[3/3] Running tests..." -ForegroundColor Cyan
$testOutput = dotnet test AgentRecorder.sln -c Release --no-build 2>&1
$testExitCode = $LASTEXITCODE
$testOutput | Select-Object -Last 8 | ForEach-Object { Write-Host $_ }

if ($testExitCode -ne 0) {
    Write-Host ""
    Write-Host "TEST FAILED with exit code $testExitCode" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== BUILD + TEST: OK ===" -ForegroundColor Green
Write-Host ""
Write-Host "Starting server..." -ForegroundColor Cyan
$exe = "D:\works\python\007-Agent-Recorder\src\AgentRecorder.App\bin\Release\net8.0-windows10.0.19041.0\AgentRecorder.App.exe"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

Set-ProcessEnvironmentSafely -psi $psi -name "AGENT_RECORDER_DATA_DIR" -value "D:\works\python\007-Agent-Recorder\.local-data"
$ffmpeg = Get-Command ffmpeg -EA SilentlyContinue
if ($ffmpeg) {
    Set-ProcessEnvironmentSafely -psi $psi -name "AGENT_RECORDER_FFMPEG_DIR" -value (Split-Path $ffmpeg.Source -Parent)
}
# 默认不开启 WGC；如需启用，设置环境变量 AGENT_RECORDER_WINDOW_BACKEND=wgc （prototype stub）。
$started = [System.Diagnostics.Process]::Start($psi)
Write-Host "[OK] Server started: PID=$($started.Id)" -ForegroundColor Green

Start-Sleep -Seconds 4
try {
    $r = Invoke-WebRequest "http://127.0.0.1:37891/api/v1/capabilities" -UseBasicParsing -TimeoutSec 5
    Write-Host "[OK] Server healthy: HTTP $($r.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "[WARN] Server not responding: $($_.Exception.Message)" -ForegroundColor Yellow
}
