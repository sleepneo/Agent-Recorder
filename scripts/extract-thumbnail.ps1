#Requires -Version 5.1
<#
.SYNOPSIS
    Extract thumbnail from video file

.DESCRIPTION
    Uses ffmpeg to extract a thumbnail frame from a video file.

.PARAMETER VideoPath
    Path to the video file

.PARAMETER OutputPath
    Output path for the thumbnail (optional, defaults to same directory with _thumb.jpg)

.PARAMETER Timestamp
    Timestamp to extract frame from (default: 00:00:02)

.EXAMPLE
    .\extract-thumbnail.ps1 -VideoPath ".local-data\Videos\recording.mp4"
    .\extract-thumbnail.ps1 -VideoPath "recording.mp4" -OutputPath "thumb.jpg" -Timestamp "00:00:01"
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$VideoPath,

    [string]$OutputPath,

    [string]$Timestamp = "00:00:02"
)

$ffmpeg = "D:\works\python\007-Agent-Recorder\tools\ffmpeg\bin\ffmpeg.exe"
$thumbnailDir = ".local-data\Thumbnails"

if (-not (Test-Path $VideoPath)) {
    Write-Host "[ERROR] Video file not found: $VideoPath" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $ffmpeg)) {
    Write-Host "[ERROR] ffmpeg not found: $ffmpeg" -ForegroundColor Red
    exit 1
}

# Determine output path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $videoName = [System.IO.Path]::GetFileNameWithoutExtension($VideoPath)
    if (-not (Test-Path $thumbnailDir)) {
        New-Item -ItemType Directory -Path $thumbnailDir -Force | Out-Null
    }
    $OutputPath = Join-Path $thumbnailDir "${videoName}_thumb.jpg"
}

Write-Host ""
Write-Host "========================================================"
Write-Host " Thumbnail Extraction"
Write-Host "========================================================"
Write-Host "Video:  $VideoPath"
Write-Host "Output: $OutputPath"
Write-Host "Time:   $Timestamp"
Write-Host ""

# Extract thumbnail
$ErrorActionPreference = "Continue"
$result = & $ffmpeg -y -i $VideoPath -ss $Timestamp -vframes 1 -q:v 2 $OutputPath 2>&1

if ($LASTEXITCODE -eq 0 -and (Test-Path $OutputPath)) {
    $fileInfo = Get-Item $OutputPath
    Write-Host "[OK] Thumbnail extracted successfully" -ForegroundColor Green
    Write-Host "Size: $([math]::Round($fileInfo.Length / 1KB, 2)) KB" -ForegroundColor Cyan
    Write-Host "Path: $OutputPath" -ForegroundColor White
} else {
    Write-Host "[ERROR] Failed to extract thumbnail" -ForegroundColor Red
    Write-Host $result
    exit 1
}

Write-Host "========================================================"

return $OutputPath
