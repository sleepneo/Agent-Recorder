#Requires -Version 5.1
<#
.SYNOPSIS
    直接用 FFmpeg gdigrab 录制，验证 FFmpeg 参数与编码输出。
    不依赖 Agent Recorder 进程，但会将输出写入 .local-data\Videos\
#>

$ErrorActionPreference = "Stop"

# ── 解析项目根 ────────────────────────────────────────────────────────────────
if ($PSScriptRoot) {
    $ProjectRoot = (Get-Item $PSScriptRoot).Parent.FullName
} else {
    $ProjectRoot = "D:\works\python\007-Agent-Recorder"
}

$ffmpeg  = Join-Path $ProjectRoot "tools\ffmpeg\bin\ffmpeg.exe"
$ffprobe = Join-Path $ProjectRoot "tools\ffmpeg\bin\ffprobe.exe"
$videos  = Join-Path $ProjectRoot ".local-data\Videos"

if (-not (Test-Path $ffmpeg)) {
    Write-Host "[ERROR] ffmpeg.exe not found: $ffmpeg" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $videos)) {
    New-Item -ItemType Directory -Force -Path $videos | Out-Null
}

Write-Host "[INFO] ffmpeg : $ffmpeg" -ForegroundColor Cyan
Write-Host "[INFO] ffprobe: $ffprobe" -ForegroundColor Cyan
Write-Host "[INFO] out dir: $videos" -ForegroundColor Cyan
Write-Host ""

# ── Test 1：3840x2160 桌面 → scale 1920x1080 ──────────────────────────────
Write-Host "=== Test 1: Full 3840x2160 gdigrab with scale filter + threads=4 ==="
$out = Join-Path $videos "direct-test-4k-scale-5s.mp4"
$argsList = @(
    '-y', '-f', 'gdigrab', '-framerate', '30',
    '-offset_x', '0', '-offset_y', '0', '-video_size', '3840x2160',
    '-t', '5', '-i', 'desktop',
    '-vf', 'scale=1920:1080:force_original_aspect_ratio=decrease',
    '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '23',
    '-pix_fmt', 'yuv420p', '-threads', '4',
    '-movflags', '+faststart',
    $out
)

& $ffmpeg @argsList 2>&1 | Select-Object -Last 15
Write-Host ""
if (Test-Path $out) {
    $f = Get-Item $out
    Write-Host "Output: $($f.Length) bytes" -ForegroundColor Green
} else { Write-Host "Output MISSING" -ForegroundColor Red }

# ── Test 2：1920x1080 原生 ──────────────────────────────────────────────
Write-Host ""
Write-Host "=== Test 2: 1920x1080 gdigrab directly ==="
$out2 = Join-Path $videos "direct-test-1920x1080-5s.mp4"
$argsList2 = @(
    '-y', '-f', 'gdigrab', '-framerate', '30',
    '-offset_x', '0', '-offset_y', '0', '-video_size', '1920x1080',
    '-t', '5', '-i', 'desktop',
    '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '23',
    '-pix_fmt', 'yuv420p', '-threads', '4',
    '-movflags', '+faststart',
    $out2
)

& $ffmpeg @argsList2 2>&1 | Select-Object -Last 15
Write-Host ""
if (Test-Path $out2) {
    $f2 = Get-Item $out2
    Write-Host "Output: $($f2.Length) bytes" -ForegroundColor Green
} else { Write-Host "Output MISSING" -ForegroundColor Red }

# ── ffprobe 验证 ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== ffprobe results ==="
foreach ($file in @($out, $out2)) {
    if (Test-Path $file) {
        $r = & $ffprobe -v quiet -print_format json -show_format -show_streams $file 2>&1 | Out-String
        $obj = $r | ConvertFrom-Json
        $v = $obj.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
        Write-Host "$file"
        Write-Host ("  duration: " + $obj.format.duration + "s, size: " + $obj.format.size + " bytes")
        if ($v) { Write-Host ("  video: " + $v.width + "x" + $v.height + " " + $v.codec_name) }
    } else { Write-Host "$file : MISSING" }
}
