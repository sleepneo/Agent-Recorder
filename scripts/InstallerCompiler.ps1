function Invoke-InstallerCompiler {
    param(
        [string]$FilePath, [string]$Arguments, [string]$WorkingDirectory,
        [string]$LogPath, [int]$TimeoutMs
    )
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.Arguments = $Arguments
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Compiler process did not start.' }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $exited = $process.WaitForExit($TimeoutMs)
        if (-not $exited) {
            # This is the process started above, never a PID lookup or an installed application.
            try { $process.Kill() } catch [InvalidOperationException] {
                if (-not $process.HasExited) { throw }
            }
            [void]$process.WaitForExit(5000)
        }
        $remaining = [Math]::Max(0, $TimeoutMs - [int]$timer.ElapsedMilliseconds)
        $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), $remaining)
        $output = if ($stdout.IsCompleted -and -not $stdout.IsFaulted) { $stdout.Result } else { '' }
        $errors = if ($stderr.IsCompleted -and -not $stderr.IsFaulted) { $stderr.Result } else { '' }
        $timedOut = -not ($exited -and $drained)
        $exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
        @(
            "Executable: $FilePath"
            "Arguments: $Arguments"
            "Working directory: $WorkingDirectory"
            "Budget (ms): $TimeoutMs"
            "Exit code: $exitCode"
            "Timed out: $timedOut"
            '--- stdout ---'
            $output
            '--- stderr ---'
            $errors
        ) | Set-Content -LiteralPath $LogPath -Encoding UTF8
        return [pscustomobject]@{ ExitCode=$exitCode; TimedOut=$timedOut; Output=$output }
    } finally {
        $process.Dispose()
    }
}

function Get-VerifiedInstallerEngine {
    param($Probe, [string]$ExpectedVersion)
    if ($Probe.TimedOut) { throw 'ISCC_PROBE_TIMEOUT' }
    if ($Probe.ExitCode -ne 0) { throw "ISCC_PROBE_FAILED: exit $($Probe.ExitCode)" }
    $markers = @($Probe.Output -split '\r?\n' | Where-Object { $_ -match 'AGENT_RECORDER_PREPROCVER' })
    if ($markers.Count -ne 1 -or $markers[0] -cnotmatch '^\s*Line [0-9]+: AGENT_RECORDER_PREPROCVER=([1-9][0-9]{0,9})\s*$') {
        throw 'ISCC_PROBE_INVALID: expected exactly one valid PREPROCVER message.'
    }
    $encoded = [uint32]0
    if (-not [uint32]::TryParse($Matches[1], [ref]$encoded)) { throw 'ISCC_PROBE_INVALID: PREPROCVER out of range.' }
    $major = ($encoded -shr 24) -band 255
    $minor = ($encoded -shr 16) -band 255
    $revision = ($encoded -shr 8) -band 255
    $build = $encoded -band 255
    $actual = "$major.$minor.$revision"
    if ($build -ne 0) { $actual += ".$build" }
    if ($actual -cne $ExpectedVersion) { throw "ISCC_ENGINE_MISMATCH: expected $ExpectedVersion, got $actual." }
    if ($major -ne 6 -or $minor -lt 4) { throw "ISCC_ENGINE_UNSUPPORTED: $actual, requires Inno 6.x from 6.4." }
    return [pscustomobject]@{ Version=$actual; PreprocVersion=$encoded }
}
