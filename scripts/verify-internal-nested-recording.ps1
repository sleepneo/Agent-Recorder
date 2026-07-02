#Requires -Version 5.1
<#
.SYNOPSIS
    Verify internal nested recording manifest and artifacts (v3 - strict).

.DESCRIPTION
    Validates the completeness and consistency of an internal nested recording.
    This version enforces strict product goal alignment:
    - Both layers must be created by Agent Recorder API (not external tools)
    - api_created_both_layers must be true
    - external_recorder_used must be false
    - Minimum duration requirements (outer >= 25s, inner >= 8s)
    - Audit log is REQUIRED (no longer optional)

.PARAMETER ManifestPath
    Path to the internal nested recording manifest JSON.

.PARAMETER RequireAuditLog
    DEPRECATED: Audit log validation is now ALWAYS enforced (no longer optional).

.EXAMPLE
    .\scripts\verify-internal-nested-recording.ps1 -ManifestPath .local-data\demo-runs\internal-nested-recording-xxx.json
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [switch]$RequireAuditLog,  # Kept for backward compatibility but now always enforced

    [switch]$RequireInnerRegion,

    [int]$MinOuterDurationSeconds = 25,

    [int]$MinInnerDurationSeconds = 8
)

$ErrorActionPreference = "Stop"
$ProjectRoot = "D:\works\python\007-Agent-Recorder"
$ToolsDir = Join-Path $ProjectRoot "tools"
$ffprobePath = Join-Path $ToolsDir "ffmpeg\bin\ffprobe.exe"

$Passed = 0
$Failed = 0
$Warnings = 0

function Write-Pass {
    param([string]$Msg)
    $script:Passed++
    Write-Host "[PASS] $Msg" -ForegroundColor Green
}

function Write-Fail {
    param([string]$Msg)
    $script:Failed++
    Write-Host "[FAIL] $Msg" -ForegroundColor Red
}

function Write-Warn {
    param([string]$Msg)
    $script:Warnings++
    Write-Host "[WARN] $Msg" -ForegroundColor Yellow
}

function Write-CheckHeader {
    param([string]$Name)
    Write-Host ""
    Write-Host "--- $Name ---" -ForegroundColor Cyan
}

function Test-AgentRecorderTool {
    param([string]$ToolValue)
    if (-not $ToolValue) { return $false }
    $lower = $ToolValue.ToLowerInvariant()
    return ($lower -eq "agent_recorder" -or $lower -eq "agent recorder")
}

function Normalize-EvenDimension {
    param([int]$Dim)
    if ($Dim % 2 -eq 0) { return $Dim }
    return $Dim - 1
}

function Get-VideoDuration {
    param([string]$VideoPath)
    if (-not (Test-Path $ffprobePath)) { return $null }
    try {
        $probeArgs = @(
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            $VideoPath
        )
        $durationStr = & $ffprobePath $probeArgs 2>&1
        if ($durationStr -and [double]::TryParse($durationStr, [ref]$null)) {
            return [double]$durationStr
        }
        return $null
    } catch {
        return $null
    }
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Internal Nested Recording - Strict Artifact Verification" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# ============================================================
# 1. Manifest validation
# ============================================================
Write-CheckHeader "Manifest validation"

if (-not (Test-Path $ManifestPath)) {
    Write-Fail "Manifest file not found: $ManifestPath"
    Write-Host ""
    Write-Host "RESULT: FAILED (manifest missing)" -ForegroundColor Red
    exit 1
}

try {
    $manifest = Get-Content $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    Write-Pass "Manifest is valid JSON"
} catch {
    Write-Fail "Manifest is not valid JSON: $_"
    exit 1
}

# Extract audit log path early for use in all sections
$auditLogPath = $null
if ($manifest.PSObject.Properties.Name -contains "audit_log_path" -and $manifest.audit_log_path) {
    $auditLogPath = $manifest.audit_log_path
}

if ($manifest.schema_version) {
    Write-Pass "Manifest has schema_version: $($manifest.schema_version)"
} else {
    Write-Fail "Manifest missing schema_version"
}

if ($manifest.task) {
    if ($manifest.task -eq "internal_nested_recording_mvp") {
        Write-Pass "Manifest task: $($manifest.task)"
    } else {
        Write-Fail "Manifest task is '$($manifest.task)', expected 'internal_nested_recording_mvp'"
    }
} else {
    Write-Fail "Manifest missing task field"
}

if ($manifest.status) {
    if ($manifest.status -eq "completed") {
        Write-Pass "Manifest status: $($manifest.status)"
    } else {
        Write-Fail "Manifest status is '$($manifest.status)', expected 'completed'"
    }
} else {
    Write-Fail "Manifest missing status field"
}

# ============================================================
# 2. Product goal contract validation (CRITICAL)
# ============================================================
Write-CheckHeader "Product goal contract validation"

# 2a. api_created_both_layers must be true
if ($manifest.api_created_both_layers -eq $true) {
    Write-Pass "api_created_both_layers = true (both layers created by Agent Recorder API)"
} else {
    Write-Fail "api_created_both_layers is not true ($($manifest.api_created_both_layers)). Internal nested recording requires both layers to be created via Agent Recorder API."
}

# 2b. external_recorder_used must be false
if ($manifest.external_recorder_used -eq $false) {
    Write-Pass "external_recorder_used = false (no external recording tools used)"
} else {
    Write-Fail "external_recorder_used is not false ($($manifest.external_recorder_used)). Product goal: only Agent Recorder API for both layers."
}

# 2c. No blockers should be present
if ($manifest.blockers -and $manifest.blockers.Count -gt 0) {
    foreach ($blocker in $manifest.blockers) {
        Write-Fail "Blocker present: $($blocker.type) - $($blocker.detail)"
    }
} else {
    Write-Pass "No blockers present"
}

# ============================================================
# 3. Outer recording validation
# ============================================================
Write-CheckHeader "Outer recording validation"

$outerDurationSec = $null
$outerRecordingId = $null

if ($manifest.outer_recording) {
    $outer = $manifest.outer_recording
    Write-Pass "Outer recording section present"

    if ($outer.recording_id) {
        $outerRecordingId = $outer.recording_id
        Write-Pass "Outer recording_id: $outerRecordingId"
    } else {
        Write-Fail "Outer recording_id missing"
    }

    if ($outer.status) {
        if ($outer.status -eq "completed") {
            Write-Pass "Outer status: $($outer.status)"
        } else {
            Write-Fail "Outer status is '$($outer.status)', expected 'completed'"
        }
    } else {
        Write-Fail "Outer status missing"
    }

    if ($outer.nested_role) {
        if ($outer.nested_role -eq "outer") {
            Write-Pass "Outer nested_role: $($outer.nested_role)"
        } else {
            Write-Fail "Outer nested_role is '$($outer.nested_role)', expected 'outer'"
        }
    } else {
        Write-Fail "Outer nested_role missing"
    }

    # 3a. tool MUST be Agent Recorder
    if ($outer.PSObject.Properties.Name -contains "tool") {
        if (Test-AgentRecorderTool -ToolValue $outer.tool) {
            Write-Pass "Outer tool is Agent Recorder: $($outer.tool)"
        } else {
            Write-Fail "Outer tool is '$($outer.tool)', expected 'agent_recorder' (must be internal recording)"
        }
    } else {
        Write-Fail "Outer tool field missing (required for product goal verification)"
    }

    if ($outer.video_path -and $outer.video_path -ne "") {
        if (Test-Path $outer.video_path) {
            Write-Pass "Outer video file exists: $($outer.video_path)"
            $outerFile = Get-Item $outer.video_path
            if ($outerFile.Length -gt 0) {
                Write-Pass "Outer video file size: $($outerFile.Length) bytes"
            } else {
                Write-Fail "Outer video file is empty (0 bytes)"
            }

            $outerDurationSec = Get-VideoDuration -VideoPath $outer.video_path
            if ($outerDurationSec -ne $null) {
                if ($outerDurationSec -gt 0) {
                    Write-Pass "Outer video duration (ffprobe): $([Math]::Round($outerDurationSec, 1))s"
                    # Duration requirement: >= MinOuterDurationSeconds
                    if ($outerDurationSec -ge $MinOuterDurationSeconds) {
                        Write-Pass "Outer duration >= ${MinOuterDurationSeconds}s requirement met"
                    } else {
                        Write-Fail "Outer duration ($([Math]::Round($outerDurationSec, 1))s) < ${MinOuterDurationSeconds}s (minimum for nested recording)"
                    }
                } else {
                    Write-Fail "Outer video duration is zero"
                }
            } else {
                # Fallback to manifest duration_actual
                $manifestDuration = $null
                if ($outer.PSObject.Properties.Name -contains "duration_actual") {
                    $manifestDuration = $outer.duration_actual
                } elseif ($outer.PSObject.Properties.Name -contains "duration") {
                    $manifestDuration = $outer.duration
                }
                if ($manifestDuration -and $manifestDuration -gt 0) {
                    $outerDurationSec = $manifestDuration
                    Write-Warn "ffprobe not available, using manifest duration: $($manifestDuration)s"
                    if ($manifestDuration -ge $MinOuterDurationSeconds) {
                        Write-Pass "Outer duration >= ${MinOuterDurationSeconds}s requirement met (from manifest)"
                    } else {
                        Write-Fail "Outer duration ($manifestDuration) < ${MinOuterDurationSeconds}s (minimum for nested recording)"
                    }
                } else {
                    Write-Warn "Could not determine outer video duration"
                }
            }
        } else {
            Write-Fail "Outer video file not found: $($outer.video_path)"
        }
    } else {
        Write-Fail "Outer video_path is missing or empty"
    }
} else {
    Write-Fail "Manifest missing outer_recording section (internal nested recording requires outer recording)"
}

# ============================================================
# 4. Inner recording validation
# ============================================================
Write-CheckHeader "Inner recording validation"

$innerDurationSec = $null

if ($manifest.inner_recording) {
    $inner = $manifest.inner_recording
    Write-Pass "Inner recording section present"

    if ($inner.recording_id) {
        Write-Pass "Inner recording_id: $($inner.recording_id)"
    } else {
        Write-Fail "Inner recording_id missing"
    }

    if ($inner.status) {
        if ($inner.status -eq "completed") {
            Write-Pass "Inner status: $($inner.status)"
        } else {
            Write-Fail "Inner status is '$($inner.status)', expected 'completed'"
        }
    } else {
        Write-Fail "Inner status missing"
    }

    if ($inner.nested_role) {
        if ($inner.nested_role -eq "inner") {
            Write-Pass "Inner nested_role: $($inner.nested_role)"
        } else {
            Write-Fail "Inner nested_role is '$($inner.nested_role)', expected 'inner'"
        }
    } else {
        Write-Fail "Inner nested_role missing"
    }

    if ($inner.parent_recording_id) {
        if ($outerRecordingId -and $inner.parent_recording_id -eq $outerRecordingId) {
            Write-Pass "Inner parent_recording_id matches outer recording_id: $($inner.parent_recording_id)"
        } elseif (-not $outerRecordingId) {
            Write-Warn "Cannot verify parent_recording_id: outer recording_id is missing"
        } else {
            Write-Fail "Inner parent_recording_id '$($inner.parent_recording_id)' does not match outer recording_id '$outerRecordingId'"
        }
    } else {
        Write-Fail "Inner parent_recording_id missing (required for nested recording)"
    }

    # 4a. tool MUST be Agent Recorder
    if ($inner.PSObject.Properties.Name -contains "tool") {
        if (Test-AgentRecorderTool -ToolValue $inner.tool) {
            Write-Pass "Inner tool is Agent Recorder: $($inner.tool)"
        } else {
            Write-Fail "Inner tool is '$($inner.tool)', expected 'agent_recorder' (must be internal recording)"
        }
    } else {
        Write-Fail "Inner tool field missing (required for product goal verification)"
    }

    if ($inner.video_path -and $inner.video_path -ne "") {
        if (Test-Path $inner.video_path) {
            Write-Pass "Inner video file exists: $($inner.video_path)"
            $innerFile = Get-Item $inner.video_path
            if ($innerFile.Length -gt 0) {
                Write-Pass "Inner video file size: $($innerFile.Length) bytes"
            } else {
                Write-Fail "Inner video file is empty (0 bytes)"
            }

            $innerDurationSec = Get-VideoDuration -VideoPath $inner.video_path
            if ($innerDurationSec -ne $null) {
                if ($innerDurationSec -gt 0) {
                    Write-Pass "Inner video duration (ffprobe): $([Math]::Round($innerDurationSec, 1))s"
                    # Duration requirement: >= MinInnerDurationSeconds
                    if ($innerDurationSec -ge $MinInnerDurationSeconds) {
                        Write-Pass "Inner duration >= ${MinInnerDurationSeconds}s requirement met"
                    } else {
                        Write-Fail "Inner duration ($([Math]::Round($innerDurationSec, 1))s) < ${MinInnerDurationSeconds}s (minimum for nested recording)"
                    }
                } else {
                    Write-Fail "Inner video duration is zero"
                }
            } else {
                # Fallback to manifest duration_actual
                $manifestDuration = $null
                if ($inner.PSObject.Properties.Name -contains "duration_actual") {
                    $manifestDuration = $inner.duration_actual
                } elseif ($inner.PSObject.Properties.Name -contains "duration") {
                    $manifestDuration = $inner.duration
                }
                if ($manifestDuration -and $manifestDuration -gt 0) {
                    $innerDurationSec = $manifestDuration
                    Write-Warn "ffprobe not available, using manifest duration: $($manifestDuration)s"
                    if ($manifestDuration -ge $MinInnerDurationSeconds) {
                        Write-Pass "Inner duration >= ${MinInnerDurationSeconds}s requirement met (from manifest)"
                    } else {
                        Write-Fail "Inner duration ($manifestDuration) < ${MinInnerDurationSeconds}s (minimum for nested recording)"
                    }
                } else {
                    Write-Warn "Could not determine inner video duration"
                }
            }
        } else {
            Write-Fail "Inner video file not found: $($inner.video_path)"
        }
    } else {
        Write-Fail "Inner video_path is missing or empty"
    }
} else {
    Write-Fail "Manifest missing inner_recording section (internal nested recording requires inner recording)"
}

# ============================================================
# 4B. Inner region source validation (if RequireInnerRegion)
# Strict: checks selected_region fields, ffprobe dimensions, audit events
# ============================================================
if ($RequireInnerRegion) {
    Write-CheckHeader "Inner region source validation (RequireInnerRegion)"
    if ($manifest.inner_recording) {
        $inner = $manifest.inner_recording

        # Check source_type is region
        if ($inner.PSObject.Properties.Name -contains "source_type") {
            $innerSourceType = $inner.source_type
            if ($innerSourceType -eq "region") {
                Write-Pass "Inner source_type is 'region'"
            } else {
                Write-Fail "Inner source_type is '$innerSourceType', expected 'region' (RequireInnerRegion is set)"
            }
        } else {
            Write-Fail "Inner source_type field is missing from manifest"
        }

        # Check selected_region exists
        if ($inner.PSObject.Properties.Name -contains "selected_region") {
            $selectedRegion = $inner.selected_region
            if ($selectedRegion -ne $null) {
                Write-Pass "Inner selected_region is present"

                # Check display_id
                if ($selectedRegion.PSObject.Properties.Name -contains "display_id") {
                    $selDisplayId = $selectedRegion.display_id
                    if ($selDisplayId -and $selDisplayId -ne "") {
                        Write-Pass "Inner selected_region.display_id is present: $selDisplayId"
                    } else {
                        Write-Fail "Inner selected_region.display_id is empty"
                    }
                } else {
                    Write-Fail "Inner selected_region.display_id field is missing"
                }

                # Check coordinate_space
                if ($selectedRegion.PSObject.Properties.Name -contains "coordinate_space") {
                    $selCoordSpace = $selectedRegion.coordinate_space
                    if ($selCoordSpace -eq "virtual_screen") {
                        Write-Pass "Inner selected_region.coordinate_space is 'virtual_screen'"
                    } else {
                        Write-Fail "Inner selected_region.coordinate_space is '$selCoordSpace', expected 'virtual_screen'"
                    }
                } else {
                    Write-Fail "Inner selected_region.coordinate_space field is missing"
                }

                # Check bounds fields
                if ($selectedRegion.PSObject.Properties.Name -contains "bounds") {
                    $selBounds = $selectedRegion.bounds
                    if ($selBounds -ne $null) {
                        $boundsOk = $true
                        foreach ($field in @("x", "y", "width", "height")) {
                            if ($selBounds.PSObject.Properties.Name -contains $field) {
                                $val = $selBounds.$field
                                if ($val -eq $null -or ($val -is [int] -and $val -lt 0)) {
                                    Write-Fail "Inner selected_region.bounds.$field is invalid: $val"
                                    $boundsOk = $false
                                }
                            } else {
                                Write-Fail "Inner selected_region.bounds.$field field is missing"
                                $boundsOk = $false
                            }
                        }
                        if ($boundsOk) {
                            Write-Pass "Inner selected_region.bounds: x=$($selBounds.x) y=$($selBounds.y) w=$($selBounds.width) h=$($selBounds.height)"
                            # Check width/height are positive
                            if ($selBounds.width -gt 0 -and $selBounds.height -gt 0) {
                                Write-Pass "Inner selected_region bounds width/height are positive"
                                # Show expected normalized dimensions
                                $normW = Normalize-EvenDimension -Dim $selBounds.width
                                $normH = Normalize-EvenDimension -Dim $selBounds.height
                                Write-Pass "Inner selected_region normalized dimensions (expected video): ${normW}x${normH}"
                            } else {
                                Write-Fail "Inner selected_region bounds width/height must be positive"
                            }
                        }
                    } else {
                        Write-Fail "Inner selected_region.bounds is null"
                    }
                } else {
                    Write-Fail "Inner selected_region.bounds field is missing"
                }
            } else {
                Write-Fail "Inner selected_region is null (RequireInnerRegion is set)"
            }
        } else {
            Write-Fail "Inner selected_region field is missing from manifest"
        }

        # Check recording_request_source if present
        if ($inner.PSObject.Properties.Name -contains "recording_request_source") {
            $reqSource = $inner.recording_request_source
            if ($reqSource -ne $null) {
                Write-Pass "Inner recording_request_source is present"
                if ($reqSource.PSObject.Properties.Name -contains "type") {
                    if ($reqSource.type -eq "region") {
                        Write-Pass "Inner recording_request_source.type is 'region'"
                    } else {
                        Write-Fail "Inner recording_request_source.type is '$($reqSource.type)', expected 'region'"
                    }
                }
            }
        }

        # Check audit log for region_selection.selected event (matched by top-level x/y/w/h fields)
        if ($auditLogPath -and (Test-Path $auditLogPath)) {
            $auditContent = Get-Content $auditLogPath -Raw -Encoding UTF8
            if ($auditContent -and $auditContent.Length -gt 0) {
                # Parse selected_region info for matching
                $innerRecId = $inner.recording_id
                $outerRecId = $null
                if ($manifest.outer_recording -and $manifest.outer_recording.recording_id) {
                    $outerRecId = $manifest.outer_recording.recording_id
                }

                $auditLines = $auditContent -split "`n" | Where-Object { $_ -and $_.Trim() -ne "" }

                # Find region_selection.selected event (top-level x/y/w/h/display_id/coordinate_space)
                $regionSelectedEntry = $null
                foreach ($line in $auditLines) {
                    try {
                        $entry = $line | ConvertFrom-Json -ErrorAction SilentlyContinue
                        if ($entry -and $entry.event -eq "region_selection.selected") {
                            $matchDisplay = $entry.display_id -eq $selectedRegion.display_id
                            $matchCoord = $entry.coordinate_space -eq $selectedRegion.coordinate_space
                            $matchX = $entry.x -eq $selectedRegion.bounds.x
                            $matchY = $entry.y -eq $selectedRegion.bounds.y
                            $matchW = $entry.w -eq $selectedRegion.bounds.width
                            $matchH = $entry.h -eq $selectedRegion.bounds.height
                            if ($matchDisplay -and $matchCoord -and $matchX -and $matchY -and $matchW -and $matchH) {
                                $regionSelectedEntry = $entry
                                break
                            }
                        }
                    } catch { }
                }

                if ($regionSelectedEntry) {
                    Write-Pass "Audit log contains 'region_selection.selected' event matching selected_region fields"
                    Write-Pass "  display_id: $($regionSelectedEntry.display_id)"
                    Write-Pass "  coordinate_space: $($regionSelectedEntry.coordinate_space)"
                    Write-Pass "  x/y/w/h: $($regionSelectedEntry.x),$($regionSelectedEntry.y),$($regionSelectedEntry.w),$($regionSelectedEntry.h)"

                    # Timestamp validation: region_selection should be between outer.started and inner.requested
                    $outerStartedTime = $null
                    $innerRequestedTime = $null
                    $regionSelTime = $null
                    try {
                        if ($regionSelectedEntry.PSObject.Properties.Name -contains "time" -and $regionSelectedEntry.time) {
                            $regionSelTime = [DateTime]$regionSelectedEntry.time
                        }
                    } catch { }

                    if ($outerRecId -and $innerRecId) {
                        foreach ($line in $auditLines) {
                            try {
                                $entry = $line | ConvertFrom-Json -ErrorAction SilentlyContinue
                                if ($entry -and $entry.recording_id -eq $outerRecId -and $entry.event -eq "recording.started" -and $entry.time) {
                                    $outerStartedTime = [DateTime]$entry.time
                                }
                                if ($entry -and $entry.recording_id -eq $innerRecId -and $entry.event -eq "recording.requested" -and $entry.time) {
                                    $innerRequestedTime = [DateTime]$entry.time
                                }
                            } catch { }
                        }
                    }

                    if ($regionSelTime -and $outerStartedTime -and $innerRequestedTime) {
                        if ($regionSelTime -ge $outerStartedTime -and $regionSelTime -le $innerRequestedTime) {
                            Write-Pass "region_selection.selected occurred between outer started and inner requested"
                        } else {
                            Write-Warn "region_selection.selected timestamp may be outside expected range (outer_started=$outerStartedTime, region_sel=$regionSelTime, inner_req=$innerRequestedTime)"
                        }
                    } else {
                        Write-Warn "Could not parse timestamps for region_selection timeline validation"
                    }
                } else {
                    Write-Fail "Audit log does not contain 'region_selection.selected' event matching selected_region fields (RequireInnerRegion is set)"
                }

                # Check inner recording.started audit event with ffmpeg_args
                if ($innerRecId) {
                    $innerStartedEntry = $null
                    foreach ($line in $auditLines) {
                        try {
                            $entry = $line | ConvertFrom-Json -ErrorAction SilentlyContinue
                            if ($entry -and $entry.recording_id -eq $innerRecId -and $entry.event -eq "recording.started") {
                                $innerStartedEntry = $entry
                                break
                            }
                        } catch { }
                    }
                    if ($innerStartedEntry) {
                        Write-Pass "Audit log contains 'recording.started' event for inner recording"
                        if ($innerStartedEntry.PSObject.Properties.Name -contains "ffmpeg_args") {
                            $ffmpegArgs = $innerStartedEntry.ffmpeg_args
                            if ($ffmpegArgs -and $ffmpegArgs -ne "") {
                                Write-Pass "Inner ffmpeg_args is non-empty"
                                # Check gdigrab
                                if ($ffmpegArgs -match "-f\s+gdigrab") {
                                    Write-Pass "Inner ffmpeg_args contains '-f gdigrab'"
                                } else {
                                    Write-Fail "Inner ffmpeg_args does not contain '-f gdigrab'"
                                }
                                # Check offset_x
                                if ($ffmpegArgs -match "-offset_x") {
                                    Write-Pass "Inner ffmpeg_args contains '-offset_x'"
                                } else {
                                    Write-Fail "Inner ffmpeg_args does not contain '-offset_x'"
                                }
                                # Check offset_y
                                if ($ffmpegArgs -match "-offset_y") {
                                    Write-Pass "Inner ffmpeg_args contains '-offset_y'"
                                } else {
                                    Write-Fail "Inner ffmpeg_args does not contain '-offset_y'"
                                }
                                # Check video_size
                                if ($ffmpegArgs -match "-video_size") {
                                    Write-Pass "Inner ffmpeg_args contains '-video_size'"
                                } else {
                                    Write-Fail "Inner ffmpeg_args does not contain '-video_size'"
                                }
                                # Check -i desktop
                                if ($ffmpegArgs -match "-i\s+desktop") {
                                    Write-Pass "Inner ffmpeg_args contains '-i desktop'"
                                } else {
                                    Write-Fail "Inner ffmpeg_args does not contain '-i desktop'"
                                }
                                # Check NOT -i title=
                                if ($ffmpegArgs -match "-i\s+title=") {
                                    Write-Fail "Inner ffmpeg_args contains '-i title=' (should use gdigrab, not title)"
                                } else {
                                    Write-Pass "Inner ffmpeg_args does not contain '-i title='"
                                }
                            } else {
                                Write-Fail "Inner ffmpeg_args is empty"
                            }
                        } else {
                            Write-Fail "Inner recording.started event does not contain ffmpeg_args"
                        }
                    } else {
                        Write-Fail "Audit log does not contain 'recording.started' event for inner recording"
                    }
                }
            }
        }

        # Get inner video dimensions via ffprobe
        $innerVideoWidth = 0
        $innerVideoHeight = 0
        if ($inner.video_path -and (Test-Path $inner.video_path)) {
            try {
                $probeArgs = @(
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=width,height",
                    "-of", "csv=s=x:p=0",
                    $inner.video_path
                )
                $dimOutput = & $ffprobePath $probeArgs 2>&1
                if ($dimOutput -and $dimOutput -match "^(\d+)x(\d+)$") {
                    $innerVideoWidth = [int]$matches[1]
                    $innerVideoHeight = [int]$matches[2]
                }
            } catch { }
        }

        # Check ffprobe video dimensions match selected_region bounds (normalized)
        if ($selectedRegion -and $selectedRegion.bounds) {
            $normWidth = Normalize-EvenDimension -Dim $selectedRegion.bounds.width
            $normHeight = Normalize-EvenDimension -Dim $selectedRegion.bounds.height

            if ($innerVideoWidth -gt 0 -and $innerVideoHeight -gt 0) {
                if ($innerVideoWidth -eq $normWidth -and $innerVideoHeight -eq $normHeight) {
                    Write-Pass "Inner MP4 dimensions ($innerVideoWidth x $innerVideoHeight) match normalized selected_region bounds ($normWidth x $normHeight)"
                } else {
                    Write-Fail "Inner MP4 dimensions ($innerVideoWidth x $innerVideoHeight) do not match normalized selected_region bounds ($normWidth x $normHeight)"
                }
            } else {
                Write-Fail "Could not determine inner MP4 video dimensions via ffprobe (RequireInnerRegion requires dimension validation)"
            }
        }
    }
}

# ============================================================
# 5. Timeline validation
# ============================================================
Write-CheckHeader "Timeline validation"

if ($outerDurationSec -ne $null -and $innerDurationSec -ne $null -and $outerDurationSec -gt 0 -and $innerDurationSec -gt 0) {
    if ($innerDurationSec -le $outerDurationSec) {
        Write-Pass "Inner duration ($([Math]::Round($innerDurationSec, 1))s) <= Outer duration ($([Math]::Round($outerDurationSec, 1))s)"
    } else {
        Write-Fail "Inner duration ($([Math]::Round($innerDurationSec, 1))s) > Outer duration ($([Math]::Round($outerDurationSec, 1))s) - inner should be nested within outer"
    }
} else {
    Write-Warn "Cannot compare durations: one or both durations unavailable"
}

if ($manifest.outer_recording -and $manifest.inner_recording) {
    $outerStarted = $null
    $innerStarted = $null
    $outerEnded = $null
    $innerEnded = $null

    if ($outer.PSObject.Properties.Name -contains "started_at" -and $outer.started_at) {
        try {
            $outerStarted = [DateTime]$outer.started_at
            Write-Pass "Outer started_at: $($outer.started_at)"
        } catch {
            Write-Warn "Outer started_at could not be parsed as datetime"
        }
    }
    if ($inner.PSObject.Properties.Name -contains "started_at" -and $inner.started_at) {
        try {
            $innerStarted = [DateTime]$inner.started_at
            Write-Pass "Inner started_at: $($inner.started_at)"
        } catch {
            Write-Warn "Inner started_at could not be parsed as datetime"
        }
    }
    if ($outer.PSObject.Properties.Name -contains "ended_at" -and $outer.ended_at) {
        try {
            $outerEnded = [DateTime]$outer.ended_at
        } catch { }
    }
    if ($inner.PSObject.Properties.Name -contains "ended_at" -and $inner.ended_at) {
        try {
            $innerEnded = [DateTime]$inner.ended_at
        } catch { }
    }

    if ($outerStarted -and $innerStarted) {
        if ($innerStarted -ge $outerStarted) {
            Write-Pass "Inner started after or at same time as outer"
        } else {
            Write-Warn "Inner started before outer (may be expected for delayed inner start)"
        }
    }

    if ($outerEnded -and $innerEnded) {
        if ($innerEnded -le $outerEnded) {
            Write-Pass "Inner ended before or at same time as outer"
        } else {
            Write-Fail "Inner ended after outer - timeline inconsistency"
        }
    }
}

# ============================================================
# 6. Audit log validation (ALWAYS required - no longer optional)
# Strict validation: check event chain by recording_id
# ============================================================
Write-CheckHeader "Audit log validation (REQUIRED - strict)"

$auditLogPath = $null
if ($manifest.PSObject.Properties.Name -contains "audit_log_path" -and $manifest.audit_log_path) {
    $auditLogPath = $manifest.audit_log_path
    if ($auditLogPath -and $auditLogPath -ne "") {
        if (Test-Path $auditLogPath) {
            Write-Pass "Audit log path is specified and exists: $auditLogPath"

            try {
                $auditContent = Get-Content $auditLogPath -Raw -Encoding UTF8
                if ($auditContent -and $auditContent.Length -gt 0) {
                    Write-Pass "Audit log is not empty ($($auditContent.Length) chars)"
                } else {
                    Write-Fail "Audit log is empty (0 bytes)"
                }

                # Get recording IDs from manifest
                $outerRecId = $null
                $innerRecId = $null
                if ($manifest.outer_recording -and $manifest.outer_recording.recording_id) {
                    $outerRecId = $manifest.outer_recording.recording_id
                }
                if ($manifest.inner_recording -and $manifest.inner_recording.recording_id) {
                    $innerRecId = $manifest.inner_recording.recording_id
                }

                if (-not $outerRecId) {
                    Write-Fail "Cannot verify audit: outer recording_id not in manifest"
                }
                if (-not $innerRecId) {
                    Write-Fail "Cannot verify audit: inner recording_id not in manifest"
                }

                if ($outerRecId -and $innerRecId) {
                    # Parse audit lines
                    $auditLines = $auditContent -split "`n" | Where-Object { $_ -and $_.Trim() -ne "" }
                    $outerEvents = @()
                    $innerEvents = @()
                    foreach ($line in $auditLines) {
                        try {
                            $entry = $line | ConvertFrom-Json -ErrorAction SilentlyContinue
                            if ($entry -and $entry.recording_id) {
                                if ($entry.recording_id -eq $outerRecId) {
                                    $outerEvents += $entry
                                } elseif ($entry.recording_id -eq $innerRecId) {
                                    $innerEvents += $entry
                                }
                            }
                        } catch { }
                    }

                    # Event types to check
                    $requiredEvents = @("recording.requested", "confirmation.created", "confirmation.approved", "recording.started", "recording.completed")

                    # Check outer events
                    Write-Host ""
                    Write-Host "  [Outer recording: $outerRecId]" -ForegroundColor Gray
                    foreach ($evt in $requiredEvents) {
                        $found = $outerEvents | Where-Object { $_.event -eq $evt }
                        if ($found) {
                            Write-Pass "  Outer '$evt' event found"
                        } else {
                            Write-Fail "  Outer '$evt' event MISSING (recording_id=$outerRecId)"
                        }
                    }

                    # Check inner events
                    Write-Host ""
                    Write-Host "  [Inner recording: $innerRecId]" -ForegroundColor Gray
                    foreach ($evt in $requiredEvents) {
                        $found = $innerEvents | Where-Object { $_.event -eq $evt }
                        if ($found) {
                            Write-Pass "  Inner '$evt' event found"
                        } else {
                            Write-Fail "  Inner '$evt' event MISSING (recording_id=$innerRecId)"
                        }
                    }

                    # Additional nested_role consistency check
                    $outerRequested = $outerEvents | Where-Object { $_.event -eq "recording.requested" }
                    $innerRequested = $innerEvents | Where-Object { $_.event -eq "recording.requested" }
                    if ($outerRequested -and $outerRequested.nested_role -eq "outer") {
                        Write-Pass "  Outer nested_role='outer' confirmed in audit"
                    } elseif ($outerRequested) {
                        Write-Fail "  Outer nested_role is '$($outerRequested.nested_role)', expected 'outer'"
                    }
                    if ($innerRequested -and $innerRequested.nested_role -eq "inner") {
                        Write-Pass "  Inner nested_role='inner' confirmed in audit"
                    } elseif ($innerRequested) {
                        Write-Fail "  Inner nested_role is '$($innerRequested.nested_role)', expected 'inner'"
                    }

                    # Parent-child relationship check
                    if ($innerRequested -and $innerRequested.parent_recording_id) {
                        if ($innerRequested.parent_recording_id -eq $outerRecId) {
                            Write-Pass "  Inner.parent_recording_id matches outer recording_id"
                        } else {
                            Write-Fail "  Inner.parent_recording_id '$($innerRequested.parent_recording_id)' does not match outer recording_id '$outerRecId'"
                        }
                    } elseif ($innerRequested) {
                        Write-Warn "  Inner parent_recording_id not found in recording.requested event"
                    }
                }
            } catch {
                Write-Fail "Could not read audit log: $_"
            }
        } else {
            Write-Fail "Audit log path is specified but file not found: $auditLogPath"
        }
    } else {
        Write-Fail "audit_log_path is specified but empty string"
    }
} else {
    Write-Fail "audit_log_path field missing from manifest (REQUIRED for internal nested recording verification)"
}

# ============================================================
# Summary
# ============================================================
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Verification Summary" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Passed: $Passed" -ForegroundColor Green
Write-Host "Warnings: $Warnings" -ForegroundColor $(if ($Warnings -gt 0) { "Yellow" } else { "White" })
Write-Host "Failed: $Failed" -ForegroundColor $(if ($Failed -gt 0) { "Red" } else { "White" })
Write-Host ""

if ($Failed -gt 0) {
    Write-Host "RESULT: FAILED" -ForegroundColor Red
    exit 1
}
elseif ($Warnings -gt 0) {
    Write-Host "RESULT: PASSED with warnings" -ForegroundColor Yellow
    exit 0
}
else {
    Write-Host "RESULT: ALL PASSED" -ForegroundColor Green
    exit 0
}
