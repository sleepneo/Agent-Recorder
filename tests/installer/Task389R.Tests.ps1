#Requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$areas = @('task389r','task389s') | ForEach-Object { Join-Path $repo ".local-data\$_" }
$work = [IO.Path]::GetFullPath($WorkDirectory)
$area = $areas | Where-Object { $work.StartsWith($_+'\',[StringComparison]::OrdinalIgnoreCase) }
if (-not $area -or (Test-Path -LiteralPath $work)) {
    throw 'Use a new installer test directory.'
}
New-Item -ItemType Directory -Path $work | Out-Null
. (Join-Path $repo 'scripts\InstallerCompiler.ps1')

$marker = '   Line 2: AGENT_RECORDER_PREPROCVER=101123072'
$results = [Collections.Generic.List[string]]::new()
$valid = Get-VerifiedInstallerEngine -Probe @{ExitCode=0;TimedOut=$false;Output=$marker} -ExpectedVersion '6.7.4'
if ($valid.Version -cne '6.7.4' -or $valid.PreprocVersion -ne 101123072) { throw 'Incorrect engine decoding.' }
$results.Add('decode-6.7.4')

$minimum = Get-VerifiedInstallerEngine -Probe @{ExitCode=0;TimedOut=$false;Output='Line 2: AGENT_RECORDER_PREPROCVER=100925440'} -ExpectedVersion '6.4.0'
if ($minimum.Version -cne '6.4.0') { throw 'Incorrect minimum supported version.' }
$results.Add('minimum-6.4.0')

$cases = @(
    @{ Name='missing'; Output='Compiler engine version: Inno Setup 6.7.4'; Reason='ISCC_PROBE_INVALID' }
    @{ Name='duplicate'; Output="$marker`n$marker"; Reason='ISCC_PROBE_INVALID' }
    @{ Name='valid-and-malformed'; Output="$marker`nLine 3: AGENT_RECORDER_PREPROCVER=bad"; Reason='ISCC_PROBE_INVALID' }
    @{ Name='malformed'; Output='Line 2: AGENT_RECORDER_PREPROCVER=6.7.4'; Reason='ISCC_PROBE_INVALID' }
    @{ Name='overflow'; Output='Line 2: AGENT_RECORDER_PREPROCVER=4294967296'; Reason='ISCC_PROBE_INVALID' }
    @{ Name='exact-mismatch'; Output=$marker; Expected='6.7.3'; Reason='ISCC_ENGINE_MISMATCH' }
    @{ Name='below-6.4'; Output='Line 2: AGENT_RECORDER_PREPROCVER=100859904'; Expected='6.3.0'; Reason='ISCC_ENGINE_UNSUPPORTED' }
    @{ Name='wrong-major'; Output='Line 2: AGENT_RECORDER_PREPROCVER=117702656'; Expected='7.4.0'; Reason='ISCC_ENGINE_UNSUPPORTED' }
    @{ Name='failed-with-marker'; Output=$marker; ExitCode=2; Reason='ISCC_PROBE_FAILED' }
)
foreach ($case in $cases) {
    $expected = if ($case.Expected) { $case.Expected } else { '6.7.4' }
    $exitCode = if ($case.ExitCode) { $case.ExitCode } else { 0 }
    $failure = $null
    try { Get-VerifiedInstallerEngine -Probe @{ExitCode=$exitCode;TimedOut=$false;Output=$case.Output} -ExpectedVersion $expected | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($failure -notlike "$($case.Reason)*") { throw "Unexpected result for $($case.Name): $failure" }
    $results.Add($case.Name)
}

# Finite, owned subprocess. Exercise the real deadline before passing its result to the gate.
$shell = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $shell)) { $shell = Join-Path $PSHOME 'pwsh.exe' }
$timeout = Invoke-InstallerCompiler -FilePath $shell -Arguments '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 10"' `
    -WorkingDirectory $work -LogPath (Join-Path $work 'timeout.log') -TimeoutMs 500
$failure = $null
try { Get-VerifiedInstallerEngine -Probe $timeout -ExpectedVersion '6.7.4' | Out-Null }
catch { $failure = $_.Exception.Message }
if ($failure -cne 'ISCC_PROBE_TIMEOUT') { throw "Expected timeout rejection, got $failure" }
$results.Add('bounded-process-timeout')

$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'results.json') -Encoding UTF8
Write-Output "PASS $($results.Count) engine version scenarios."
