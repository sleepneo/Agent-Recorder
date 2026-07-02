#Requires -Version 5.1
<#
.SYNOPSIS
    Agent Recorder MVP - API Smoke Test

.DESCRIPTION
    Verify Agent Recorder API core endpoints.
    Start server first: .\scripts\start-server.ps1 -Configuration Release

    Environment variables:
    - AGENT_RECORDER_BASE_URL: Custom service URL, default http://127.0.0.1:37891
    - AGENT_RECORDER_API_KEY: API Key (required for sensitive endpoints)

    If API Key is not set, script will try to read from token file:
    - <AGENT_RECORDER_DATA_DIR>/config/api-key.txt
    - Default: .local-data/config/api-key.txt

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-api.ps1
#>

param(
    [string]$BaseUrl = $(if ($env:AGENT_RECORDER_BASE_URL) { $env:AGENT_RECORDER_BASE_URL } else { "http://127.0.0.1:37891" }),
    [string]$ApiKey = $(if ($env:AGENT_RECORDER_API_KEY) { $env:AGENT_RECORDER_API_KEY } else { $null }),
    [string]$ApiPrefix,
    [string]$DataDir = $(if ($env:AGENT_RECORDER_DATA_DIR) { $env:AGENT_RECORDER_DATA_DIR } else { ".local-data" })
)

if (-not $ApiPrefix) { $ApiPrefix = "$BaseUrl/api/v1" }

$ErrorActionPreference = "Continue"
$script:FAILED = 0

# Auto-read token file
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $isAbsolute = $DataDir -match "^([a-zA-Z]:|\\\\)"
    if ($isAbsolute) {
        $absDataDir = $DataDir
    } else {
        $ProjectRoot = if ($PSScriptRoot) { (Get-Item $PSScriptRoot).Parent.FullName } else { "D:\works\python\007-Agent-Recorder" }
        $absDataDir = Join-Path $ProjectRoot $DataDir
    }

    $configDir = Join-Path $absDataDir "config"
    $tokenFile = Join-Path $configDir "api-key.txt"

    if (Test-Path $tokenFile) {
        $ApiKey = Get-Content $tokenFile -Raw | Out-String
        $ApiKey = $ApiKey.Trim()
        Write-Host "[INFO] API Key loaded from: $tokenFile" -ForegroundColor Cyan
    } else {
        Write-Host "[WARN] API Key not set and token file not found: $tokenFile" -ForegroundColor Yellow
        Write-Host "[WARN] Some tests may fail if the endpoint requires authentication" -ForegroundColor Yellow
    }
}

function Test-Endpoint {
    param(
        [string]$Name,
        [string]$Method = "GET",
        [string]$Endpoint,
        [string]$Body = $null,
        [bool]$RequiresAuth = $false
    )

    $url = "$($ApiPrefix.TrimEnd('/'))/$($Endpoint.TrimStart('/'))"
    Write-Host ""
    Write-Host ("-" * 60)
    Write-Host "TEST: $Name"
    Write-Host "URL:  $url"
    Write-Host "AUTH: $(if ($RequiresAuth) { "Required" } else { "Not required" })"

    try {
        $params = @{
            Uri = $url
            UseBasicParsing = $true
            TimeoutSec = 10
            ErrorAction = "Stop"
        }
        if ($Method -ne "GET") { $params["Method"] = $Method }
        if ($Body) {
            $params["Body"] = $Body
            $params["ContentType"] = "application/json"
        }
        if ($RequiresAuth -and $ApiKey) {
            $params["Headers"] = @{ "X-Agent-Recorder-Key" = $ApiKey }
        }

        $resp = Invoke-WebRequest @params
        $status = $resp.StatusCode
        Write-Host "HTTP: $status"

        if ($status -ge 200 -and $status -lt 300) {
            $json = $resp.Content | ConvertFrom-Json
            if ($null -ne $json.ok) {
                if ($json.ok -eq $true) {
                    Write-Host "RESULT: PASS" -ForegroundColor Green
                    $keys = ($json.PSObject.Properties.Name | Where-Object { $_ -ne "ok" })
                    Write-Host "Data keys: $($keys -join ', ')"
                    return $true
                } else {
                    Write-Host "RESULT: FAIL - 'ok' field is false" -ForegroundColor Red
                    $script:FAILED++
                    return $false
                }
            } else {
                Write-Host "RESULT: FAIL - no 'ok' field in response" -ForegroundColor Red
                $script:FAILED++
                return $false
            }
        } else {
            Write-Host "RESULT: FAIL - HTTP $status" -ForegroundColor Red
            $script:FAILED++
            return $false
        }
    }
    catch {
        $msg = $_.Exception.Message
        if ($msg -match "Unable to connect" -or $msg -match "timeout") {
            Write-Host "RESULT: FAIL - Connection error: $msg" -ForegroundColor Red
        } else {
            Write-Host "RESULT: FAIL - $($_.Exception.GetType().Name): $msg" -ForegroundColor Red
        }
        $script:FAILED++
        return $false
    }
}

function Test-AuthNegative {
    param(
        [string]$Name,
        [string]$Method = "GET",
        [string]$Endpoint,
        [string]$BadKey,
        [int]$ExpectedStatus
    )

    $url = "$($ApiPrefix.TrimEnd('/'))/$($Endpoint.TrimStart('/'))"
    Write-Host ""
    Write-Host "TEST: $Name"
    Write-Host "URL:  $url"
    Write-Host "Expected: HTTP $ExpectedStatus"

    try {
        $params = @{
            Uri = $url
            UseBasicParsing = $true
            TimeoutSec = 10
            ErrorAction = "Stop"
            Method = $Method
            Headers = @{ "X-Agent-Recorder-Key" = $BadKey }
        }
        if ($Method -eq "GET") { $params.Remove("Method") }

        $resp = Invoke-WebRequest @params
        $status = $resp.StatusCode

        if ($status -eq $ExpectedStatus) {
            Write-Host "RESULT: PASS - Got expected HTTP $status" -ForegroundColor Green
            return $true
        } else {
            Write-Host "RESULT: FAIL - Expected HTTP $ExpectedStatus but got $status" -ForegroundColor Red
            $script:FAILED++
            return $false
        }
    }
    catch {
        $msg = $_.Exception.Message
        if ($msg -match "403" -and $ExpectedStatus -eq 403) {
            Write-Host "RESULT: PASS - Got expected 403 Forbidden" -ForegroundColor Green
            return $true
        }
        if ($msg -match "401" -and $ExpectedStatus -eq 401) {
            Write-Host "RESULT: PASS - Got expected 401 Unauthorized" -ForegroundColor Green
            return $true
        }
        Write-Host "RESULT: FAIL - Unexpected error: $msg" -ForegroundColor Red
        $script:FAILED++
        return $false
    }
}

Write-Host ""
Write-Host "========================================================"
Write-Host " Agent Recorder - API Smoke Test"
Write-Host " Base URL: $BaseUrl"
Write-Host " API Key:  $(if ($ApiKey) { "Set (length: $($ApiKey.Length))" } else { "Not set" })"
Write-Host "========================================================"

# Use [void] to suppress return value output
[void](Test-Endpoint "GET  /capabilities" -Endpoint "capabilities" -RequiresAuth $false)
[void](Test-Endpoint "GET  /permissions" -Endpoint "permissions" -RequiresAuth $false)
[void](Test-Endpoint "GET  /displays" -Endpoint "displays" -RequiresAuth $false)
[void](Test-Endpoint "GET  /windows" -Endpoint "windows" -RequiresAuth $false)
[void](Test-Endpoint "GET  /windows/active" -Endpoint "windows/active" -RequiresAuth $false)
[void](Test-Endpoint "GET  /audio/devices" -Endpoint "audio/devices" -RequiresAuth $false)
[void](Test-Endpoint "GET  /recordings" -Endpoint "recordings" -RequiresAuth $true)

Write-Host ""
Write-Host ("=" * 60)
if ($script:FAILED -eq 0) {
    Write-Host "SMOKE TEST: ALL PASSED" -ForegroundColor Green
} else {
    Write-Host "SMOKE TEST: $script:FAILED FAILED" -ForegroundColor Red
}

# Authentication negative tests
Write-Host ""
Write-Host ("=" * 60)
Write-Host " Authentication Negative Tests" -ForegroundColor Cyan
Write-Host ("=" * 60)

# Use array-driven tests to avoid manual counting issues
$case1 = @{ Name = "No key - GET /recordings (expect 401)"; Endpoint = "recordings"; BadKey = ""; ExpectedStatus = 401 }
$case2 = @{ Name = "Wrong key - GET /recordings (expect 403)"; Endpoint = "recordings"; BadKey = "invalid-key-12345"; ExpectedStatus = 403 }
$authTestCases = @($case1, $case2)

$authPassed = 0
$authTotal = $authTestCases.Count

foreach ($case in $authTestCases) {
    $result = Test-AuthNegative -Name $case.Name -Endpoint $case.Endpoint -BadKey $case.BadKey -ExpectedStatus $case.ExpectedStatus
    if ($result) { $authPassed++ }
}

Write-Host ""
Write-Host ("=" * 60)
if ($authPassed -eq $authTotal) {
    Write-Host "AUTH TEST: $authPassed/$authTotal PASSED" -ForegroundColor Green
} else {
    Write-Host "AUTH TEST: $authPassed/$authTotal PASSED" -ForegroundColor Red
}
Write-Host ("=" * 60)

if ($script:FAILED -eq 0) {
    exit 0
} else {
    exit 1
}
