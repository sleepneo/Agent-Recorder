$displays = Invoke-RestMethod -Uri 'http://127.0.0.1:37891/api/v1/displays' -UseBasicParsing
$displayId = $displays.data.displays[0].id
Write-Host "Using display_id: $displayId"

$body = @{
    source = @{
        type = "display"
        display_id = $displayId
    }
    audio = @{
        microphone = @{
            enabled = $false
        }
    }
    video = @{
        fps = 30
        quality = "medium"
    }
    stop_condition = @{
        type = "duration"
        seconds = 5
    }
} | ConvertTo-Json -Compress

$sw = [Diagnostics.Stopwatch]::StartNew()
$r = Invoke-WebRequest -Uri 'http://127.0.0.1:37891/api/v1/recordings' -Method POST -ContentType 'application/json' -Body $body -UseBasicParsing -TimeoutSec 15
$sw.Stop()
Write-Host "POST /recordings elapsed: $($sw.ElapsedMilliseconds)ms"
$json = $r.Content | ConvertFrom-Json
$json | ConvertTo-Json -Depth 5

$confId = $json.data.confirmation_id
if ($confId) {
    Write-Host ""
    Write-Host "Confirmation ID: $confId"
    Start-Sleep 2
    $confResp = Invoke-WebRequest -Uri "http://127.0.0.1:37891/api/v1/confirmations/$confId" -UseBasicParsing -TimeoutSec 5
    $conf = $confResp.Content | ConvertFrom-Json
    Write-Host ""
    Write-Host "GET /confirmations/$confId"
    $conf | ConvertTo-Json -Depth 3
}