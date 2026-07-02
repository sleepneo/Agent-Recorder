$ErrorActionPreference = "Continue"
$apiKey = (Get-Content "D:\works\python\007-Agent-Recorder\.local-data\config\api-key.txt" -Raw).Trim()
$base = "http://127.0.0.1:37891/api/v1"
$headers = @{"X-Agent-Recorder-Key" = $apiKey}

Write-Host "=== Test 1: Display Recording (15fps, 5s) ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Create recording
$body1 = @{
    source = @{ type = "display"; display_id = "display_1" }
    audio = @{ microphone = @{ enabled = $false } }
    video = @{ fps = 15; quality = "medium" }
    stop_condition = @{ type = "duration"; seconds = 5 }
} | ConvertTo-Json -Depth 5

Write-Host "[Step 1] POST /recordings (display, 15fps, 5s)..."
try {
    $resp1 = Invoke-RestMethod -Uri "$base/recordings" -Method POST `
        -Headers $headers -ContentType "application/json" -Body $body1 -TimeoutSec 10
    Write-Host "          OK: status=$($resp1.data.status)" -ForegroundColor Green
    Write-Host "          confirmation_id=$($resp1.data.confirmation_id)" -ForegroundColor White

    # Step 2: Wait for MANUAL user approval (via tray menu or pop-up)
    # Note: API self-approval (POST /confirmations/{id}/approve) is NOT allowed
    # for security reasons. Please click "Approve" in the tray menu or the
    # pop-up confirmation dialog.
    $confId = $resp1.data.confirmation_id
    Write-Host ""
    Write-Host "[Step 2] Waiting for MANUAL user approval..." -ForegroundColor Yellow
    Write-Host "         (Click 'Approve' in the tray menu or pop-up confirmation dialog)" -ForegroundColor Yellow
    Write-Host "         (POST /confirmations/$confId/approve returns HTTP 405: API self-approval is disabled)" -ForegroundColor Yellow
    Write-Host ""

    $recId = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try {
            $status = Invoke-RestMethod -Uri "$base/recordings" -Method GET `
                -Headers $headers -TimeoutSec 5 -ErrorAction SilentlyContinue
            if ($status.data.recordings -and $status.data.recordings.Count -gt 0) {
                $rec = $status.data.recordings[0]
                if ($rec.recording_id) { $recId = $rec.recording_id }
                Write-Host "          [$($i+1)s] state=$($rec.status)" -ForegroundColor White
                if ($rec.status -eq "completed") {
                    Write-Host "          RECORDING COMPLETED!" -ForegroundColor Green
                    break
                }
                if ($rec.status -eq "failed") {
                    Write-Host "          RECORDING FAILED: $($rec.error)" -ForegroundColor Red
                    break
                }
            }
        } catch {}
    }

    # Step 3: Show recording details
    Write-Host ""
    Write-Host "[Step 3] Check recording $recId output..."
    try {
        $out = Invoke-RestMethod -Uri "$base/recordings/$recId/output" -Method GET `
            -Headers $headers -TimeoutSec 5
        Write-Host "          path=$(if ($out.data.output.path) { $out.data.output.path } else { 'n/a' })" -ForegroundColor White
        Write-Host "          size_bytes=$(if ($out.data.output.size_bytes) { $out.data.output.size_bytes } else { 'n/a' })" -ForegroundColor White
        Write-Host "          duration_seconds=$(if ($out.data.output.duration_seconds) { $out.data.output.duration_seconds } else { 'n/a' })" -ForegroundColor White
        if ($out.data.warnings -and $out.data.warnings.Count -gt 0) {
            Write-Host "          warnings=$($out.data.warnings -join '; ')" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "          WARNING: GET output failed: $_" -ForegroundColor Yellow
    }

    # Step 4: Thumbnail check
    $thumbPath = "D:\works\python\007-Agent-Recorder\.local-data\Videos\$recId-thumb.jpg"
    if (Test-Path $thumbPath) {
        Write-Host "          thumbnail: FOUND at $thumbPath" -ForegroundColor Green
    } else {
        $jpgs = Get-ChildItem "D:\works\python\007-Agent-Recorder\.local-data\Videos\" -Filter "*.jpg" -EA SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($jpgs) {
            Write-Host "          thumbnail: LATEST = $($jpgs.FullName)" -ForegroundColor Green
        } else {
            Write-Host "          thumbnail: not found (may be named differently)" -ForegroundColor Yellow
        }
    }

} catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Test 2: Window Recording (default backend = FFmpeg gdigrab) ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Create window recording
$body2 = @{
    source = @{ type = "window"; window_id = "window_123456" }
    audio = @{ microphone = @{ enabled = $false } }
    video = @{ fps = 30; quality = "medium" }
    stop_condition = @{ type = "duration"; seconds = 5 }
} | ConvertTo-Json -Depth 5

Write-Host "[Step 1] POST /recordings (window, HWND=123456)..."
try {
    $resp3 = Invoke-RestMethod -Uri "$base/recordings" -Method POST `
        -Headers $headers -ContentType "application/json" -Body $body2 -TimeoutSec 10
    Write-Host "          OK: status=$($resp3.data.status)" -ForegroundColor Green
    Write-Host "          confirmation_id=$($resp3.data.confirmation_id)" -ForegroundColor White

    # Step 2: Wait for MANUAL user approval
    $confId2 = $resp3.data.confirmation_id
    Write-Host ""
    Write-Host "[Step 2] Waiting for MANUAL user approval..." -ForegroundColor Yellow
    Write-Host "         (Click 'Approve' in the tray menu or pop-up)" -ForegroundColor Yellow
    Write-Host "         (POST /confirmations/$confId2/approve returns HTTP 405)" -ForegroundColor Yellow
    Write-Host ""

    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try {
            $status = Invoke-RestMethod -Uri "$base/recordings" -Method GET `
                -Headers $headers -TimeoutSec 5 -ErrorAction SilentlyContinue
            if ($status.data.recordings -and $status.data.recordings.Count -gt 0) {
                $rec = $status.data.recordings[0]
                Write-Host "          [$($i+1)s] state=$($rec.status)" -ForegroundColor White
                if ($rec.status -eq "failed") {
                    Write-Host "          RECORDING FAILED:" -ForegroundColor Yellow
                    Write-Host "            error=$($rec.error)" -ForegroundColor Yellow
                    break
                }
                if ($rec.status -eq "completed") {
                    Write-Host "          RECORDING COMPLETED!" -ForegroundColor Green
                    break
                }
            }
        } catch {}
    }
} catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Audit Log (last 25 lines) ===" -ForegroundColor Cyan
Get-Content "D:\works\python\007-Agent-Recorder\.local-data\logs\audit.jsonl" -Tail 25 | ForEach-Object { Write-Host $_ }

Write-Host ""
Write-Host "=== Smoke Test: GET /capabilities ===" -ForegroundColor Cyan
try {
    $r = Invoke-WebRequest "$base/capabilities" -UseBasicParsing -TimeoutSec 5 -Headers $headers
    Write-Host "          OK: HTTP $($r.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "          FAILED: $_" -ForegroundColor Red
}
