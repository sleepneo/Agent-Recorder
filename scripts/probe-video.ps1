#Requires -Version 5.1
<#
.SYNOPSIS
    Probe video file and extract metadata

.DESCRIPTION
    Uses ffprobe to extract video metadata from a recording file.

.PARAMETER VideoPath
    Path to the video file

.EXAMPLE
    .\probe-video.ps1 -VideoPath ".local-data\Videos\recording-2026-06-18-003031.mp4"
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$VideoPath
)

$ffprobe = "D:\works\python\007-Agent-Recorder\tools\ffmpeg\bin\ffprobe.exe"

if (-not (Test-Path $VideoPath)) {
    Write-Host "[ERROR] Video file not found: $VideoPath" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $ffprobe)) {
    Write-Host "[ERROR] ffprobe not found: $ffprobe" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "========================================================"
Write-Host " Video Probe"
Write-Host "========================================================"
Write-Host "File: $VideoPath"
Write-Host ""

$fileInfo = Get-Item $VideoPath
Write-Host "File Size: $([math]::Round($fileInfo.Length / 1KB, 2)) KB" -ForegroundColor Cyan

# Get video info via ffprobe
$json = & $ffprobe -v quiet -print_format json -show_format -show_streams $VideoPath 2>&1 | ConvertFrom-Json

$videoStream = $json.streams | Where-Object { $_.codec_type -eq "video" } | Select-Object -First 1

if ($videoStream) {
    Write-Host ""
    Write-Host "Video Stream:" -ForegroundColor Green
    Write-Host "  Codec:       $($videoStream.codec_name) ($($videoStream.profile))" -ForegroundColor White
    Write-Host "  Resolution:  $($videoStream.width)x$($videoStream.height)" -ForegroundColor White
    Write-Host "  Frame Rate:  $($videoStream.avg_frame_rate)" -ForegroundColor White
    Write-Host "  Duration:    $($videoStream.duration)s" -ForegroundColor White
    Write-Host "  Bit Rate:    $($videoStream.bit_rate) bps" -ForegroundColor White
    Write-Host "  Frames:      $($videoStream.nb_frames)" -ForegroundColor White
}

if ($json.format) {
    Write-Host ""
    Write-Host "Format:" -ForegroundColor Green
    Write-Host "  Format:      $($json.format.format_name)" -ForegroundColor White
    Write-Host "  Duration:    $($json.format.duration)s" -ForegroundColor White
    Write-Host "  Size:        $([math]::Round([double]$json.format.size / 1KB, 2)) KB" -ForegroundColor White
    Write-Host "  Bit Rate:    $($json.format.bit_rate) bps" -ForegroundColor White
}

Write-Host "========================================================"

# Return object for piping
$probeResult = @{
    FilePath = $VideoPath
    FileSize = $fileInfo.Length
    Codec = $videoStream.codec_name
    Width = $videoStream.width
    Height = $videoStream.height
    FrameRate = $videoStream.avg_frame_rate
    Duration = $videoStream.duration
    BitRate = $videoStream.bit_rate
    FrameCount = $videoStream.nb_frames
}

return $probeResult
