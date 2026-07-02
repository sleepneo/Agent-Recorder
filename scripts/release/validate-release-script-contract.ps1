#Requires -Version 5.1
<#
.SYNOPSIS
    Validate that release scripts use the correct API contract.

.DESCRIPTION
    This script performs static checks on the release scripts to ensure they:
    - Do not use the old `source_type` field
    - Do not use the old `duration_seconds` field at the top level
    - Use the nested `source`, `video`, `stop_condition` objects
    - Unwrap API responses using `.data`
    - Handle the confirmation flow with `confirmation_id`

.PARAMETER ScriptsDir
    Path to the scripts/release directory. Default: ./scripts/release
#>

param(
    [string]$ScriptsDir = $null
)

$ErrorActionPreference = "Continue"

if (-not $ScriptsDir) {
    $ScriptsDir = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "scripts\release"
}

$scripts = @(
    "record-selected-region.ps1"
    "record-nested-regions.ps1"
)

$checks = @{
    "source_type" = @{
        description = "Old flat source_type field"
        expected = $false
    }
    # Check for deprecated flat duration_seconds at top level (not inside stop_condition)
    # Pattern: duration_seconds appearing before a @{} block (not as output.duration_seconds)
    "duration_seconds" = @{
        description = "Old flat duration_seconds field at top level"
        expected = $false
        check_context = $true
    }
    "source = @{" = @{
        description = "Nested source object in JSON body"
        expected = $true
    }
    "video = @{" = @{
        description = "Nested video object in JSON body"
        expected = $true
    }
    "stop_condition = @{" = @{
        description = "Nested stop_condition object in JSON body"
        expected = $true
    }
    '\.data\b' = @{
        description = "API response unwrapping using .data"
        expected = $true
    }
    "confirmation_id" = @{
        description = "Confirmation ID handling for user confirmation flow"
        expected = $true
    }
    "/confirmations/" = @{
        description = "Polling /confirmations/{id} endpoint"
        expected = $true
    }
    "} | ConvertTo-Json" = @{
        description = "Double-JSON encoding: body pre-serialized before Invoke-Api"
        expected = $false
        pattern_type = "double_json"
    }
    "Depth 10" = @{
        description = "Invoke-Api uses ConvertTo-Json -Depth 10 to avoid truncation"
        expected = $true
    }
}

Write-Host "=== Release Script API Contract Validator ===" -ForegroundColor Cyan
Write-Host "Scripts directory: $ScriptsDir"
Write-Host ""

$totalPassed = 0
$totalFailed = 0

foreach ($scriptName in $scripts) {
    $scriptPath = Join-Path $ScriptsDir $scriptName
    if (-not (Test-Path $scriptPath)) {
        Write-Host "[SKIP] $scriptName - not found" -ForegroundColor Gray
        continue
    }

    Write-Host "Checking: $scriptName" -ForegroundColor Yellow
    $content = Get-Content $scriptPath -Raw

    $scriptPassed = 0
    $scriptFailed = 0

    foreach ($check in $checks.Keys) {
        $expected = $checks[$check].expected
        $description = $checks[$check].description

        # Skip array access patterns
        if ($check -match '^\[.*\]$') {
            continue
        }

        # For duration_seconds, check that it's NOT used at top level
        # (it's OK to use $output.duration_seconds, just not duration_seconds = xxx at top level)
        if ($check -eq "duration_seconds" -and $expected -eq $false) {
            # Check for deprecated usage: duration_seconds = xxx (not as .duration_seconds)
            $deprecatedPattern = 'duration_seconds\s*=\s*\$'
            $found = $content -match $deprecatedPattern
        }
        # For double-JSON check: look for hashtable body variables piped to ConvertTo-Json
        # Pattern like: $xxxBody = @{ ... } | ConvertTo-Json
        elseif ($checks[$check].pattern_type -eq "double_json") {
            # Match: variable assignment ending with } | ConvertTo-Json (not inside Invoke-Api function)
            # We look for lines that assign a body variable and pipe to ConvertTo-Json
            $doubleJsonPattern = '(Body|body)\s*=\s*@\{[^}]*\}\s*\|\s*ConvertTo-Json'
            $found = $content -match $doubleJsonPattern

            # Also check for multi-line: } | ConvertTo-Json right after a hashtable assignment
            # More robust: count ConvertTo-Json occurrences outside Invoke-Api
            # Simple approach: count total ConvertTo-Json vs inside Invoke-Api
            if (-not $found) {
                # Look for the pattern where a body variable's last line is } | ConvertTo-Json
                $lines = $content -split "`r?`n"
                $inBodyAssignment = $false
                for ($i = 0; $i -lt $lines.Count; $i++) {
                    $line = $lines[$i]
                    if ($line -match '^\$[a-zA-Z_][a-zA-Z0-9_]*Body\s*=\s*@\{') {
                        $inBodyAssignment = $true
                        continue
                    }
                    if ($inBodyAssignment -and $line -match '^\s*\}\s*\|\s*ConvertTo-Json') {
                        $found = $true
                        break
                    }
                    if ($inBodyAssignment -and $line -match '^\s*\}\s*$') {
                        # End of hashtable without ConvertTo-Json pipe
                        $inBodyAssignment = $false
                    }
                }
            }
        }
        elseif ($check -match '[\.\[\]\(\)\*\+\?\^\$\{\}\|\\]') {
            $found = $content -match $check
        } else {
            $found = $content.Contains($check)
        }

        if ($found -eq $expected) {
            Write-Host "  [OK] $description" -ForegroundColor Green
            $scriptPassed++
        } else {
            if ($expected) {
                Write-Host "  [FAIL] Missing: $description" -ForegroundColor Red
            } else {
                Write-Host "  [FAIL] Found deprecated: $description" -ForegroundColor Red
            }
            $scriptFailed++
        }
    }

    Write-Host "  --> Passed: $scriptPassed, Failed: $scriptFailed" -ForegroundColor $(if ($scriptFailed -eq 0) { "Green" } else { "Red" })
    Write-Host ""

    $totalPassed += $scriptPassed
    $totalFailed += $scriptFailed
}

Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "  Total Passed: $totalPassed"
Write-Host "  Total Failed: $totalFailed"
Write-Host ""

if ($totalFailed -eq 0) {
    Write-Host "All contract checks passed!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "Some contract checks failed. Please review the errors above." -ForegroundColor Red
    exit 1
}
