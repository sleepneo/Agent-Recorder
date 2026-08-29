#Requires -Version 5.1
<#
.SYNOPSIS
    Build Agent Recorder portable release zip.

.DESCRIPTION
    Publishes AgentRecorder.App, AgentRecorder.Headless, AgentRecorder.Cli and
    AgentRecorder.AudioHelper as a Windows x64 portable package, copies bundled
    FFmpeg into the app directory, adds agent-facing API docs, excludes
    PDBs / .local-data / API keys, and creates a zip under
    .local-data/release-candidates/.

.PARAMETER Version
    Version string for the zip name. Default: v0.1.11

.PARAMETER PublishMode
    "self-contained" (default) or "framework-dependent".

.PARAMETER ProjectRoot
    Optional project root. Defaults to the parent directory of this script.

.EXAMPLE
    .\build-portable-release.ps1

.EXAMPLE
    .\build-portable-release.ps1 -PublishMode framework-dependent
#>

param(
    [string]$Version = "v0.1.11",

    [ValidateSet("self-contained", "framework-dependent")]
    [string]$PublishMode = "self-contained",

    [string]$ProjectRoot = "",

    [switch]$DisableReadyToRun,

    [switch]$TestArgumentQuoting,

    [switch]$TestProcessTree,

    [switch]$TestFastExitProcess,

    [switch]$TestProcessIdentitySafety,

    [switch]$SimulateTaskkillFailure,

    [switch]$SimulateJobOwnershipFailure,

    [ValidateRange(1000, 600000)]
    [int]$NativeTestTimeoutMs = 600000
)

$ErrorActionPreference = "Stop"

if (-not ("AgentRecorderPortableJob" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

public sealed class AgentRecorderPortableJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectBasicAccountingInformation = 1;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private IntPtr _handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        ref ExtendedLimitInformation information,
        uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr job,
        int informationClass,
        out BasicAccountingInformation information,
        uint informationLength,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public AgentRecorderPortableJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("CreateJobObject failed: " + Marshal.GetLastWin32Error());

        var limits = new ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(
                _handle,
                JobObjectExtendedLimitInformation,
                ref limits,
                (uint)Marshal.SizeOf(typeof(ExtendedLimitInformation))))
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException("SetInformationJobObject failed: " + error);
        }
    }

    public bool AssignHandle(IntPtr processHandle)
    {
        return _handle != IntPtr.Zero && processHandle != IntPtr.Zero && AssignProcessToJobObject(_handle, processHandle);
    }

    public bool Terminate(uint exitCode)
    {
        return _handle != IntPtr.Zero && TerminateJobObject(_handle, exitCode);
    }

    public bool WaitForEmpty(int timeoutMs, out uint activeProcesses, out int queryError)
    {
        activeProcesses = 0;
        queryError = 0;
        if (_handle == IntPtr.Zero || timeoutMs <= 0)
        {
            queryError = 6;
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            BasicAccountingInformation information;
            if (!QueryInformationJobObject(
                    _handle,
                    JobObjectBasicAccountingInformation,
                    out information,
                    (uint)Marshal.SizeOf(typeof(BasicAccountingInformation)),
                    IntPtr.Zero))
            {
                queryError = Marshal.GetLastWin32Error();
                return false;
            }

            activeProcesses = information.ActiveProcesses;
            if (activeProcesses == 0)
            {
                return true;
            }

            long remainingMs = timeoutMs - stopwatch.ElapsedMilliseconds;
            if (remainingMs <= 0)
            {
                return false;
            }
            System.Threading.Thread.Sleep((int)Math.Min(10L, remainingMs));
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

public sealed class AgentRecorderPortableLaunchedProcess : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint Infinite = 0xffffffff;
    private IntPtr _processHandle;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string Reserved;
        public string Desktop;
        public string Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        ref SecurityAttributes attributes,
        int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(
        IntPtr handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    public Process Process { get; private set; }
    public StreamReader StandardOutput { get; private set; }
    public StreamReader StandardError { get; private set; }
    public AgentRecorderPortableJob Job { get; private set; }
    public bool OwnershipEstablishedBeforeResume { get; private set; }

    public int GetExitCode()
    {
        if (_processHandle == IntPtr.Zero)
            throw new InvalidOperationException("process handle is closed");
        uint exitCode;
        if (!GetExitCodeProcess(_processHandle, out exitCode))
            throw new InvalidOperationException("GetExitCodeProcess failed: " + Marshal.GetLastWin32Error());
        return unchecked((int)exitCode);
    }

    private AgentRecorderPortableLaunchedProcess(
        Process process,
        StreamReader standardOutput,
        StreamReader standardError,
        AgentRecorderPortableJob job,
        IntPtr processHandle)
    {
        Process = process;
        StandardOutput = standardOutput;
        StandardError = standardError;
        Job = job;
        _processHandle = processHandle;
        OwnershipEstablishedBeforeResume = true;
    }

    public static AgentRecorderPortableLaunchedProcess Start(
        string applicationName,
        string commandLine,
        bool simulateOwnershipFailure)
    {
        IntPtr stdoutRead = IntPtr.Zero;
        IntPtr stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero;
        IntPtr stderrWrite = IntPtr.Zero;
        ProcessInformation processInformation = new ProcessInformation();
        AgentRecorderPortableJob job = null;
        Process process = null;
        StreamReader standardOutput = null;
        StreamReader standardError = null;
        bool resumed = false;
        bool success = false;
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                InheritHandle = 1,
            };
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref attributes, 0) ||
                !SetHandleInformation(stdoutRead, HandleFlagInherit, 0) ||
                !CreatePipe(out stderrRead, out stderrWrite, ref attributes, 0) ||
                !SetHandleInformation(stderrRead, HandleFlagInherit, 0))
            {
                throw new InvalidOperationException("CreatePipe/SetHandleInformation failed: " + Marshal.GetLastWin32Error());
            }

            var startup = new StartupInfo
            {
                Cb = Marshal.SizeOf(typeof(StartupInfo)),
                Flags = StartfUseStdHandles,
                StdOutput = stdoutWrite,
                StdError = stderrWrite,
                StdInput = IntPtr.Zero,
            };
            var mutableCommandLine = new StringBuilder(commandLine);
            if (!CreateProcess(
                    applicationName,
                    mutableCommandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                    IntPtr.Zero,
                    null,
                    ref startup,
                    out processInformation))
            {
                throw new InvalidOperationException("CreateProcessW failed: " + Marshal.GetLastWin32Error());
            }

            CloseHandle(stdoutWrite);
            stdoutWrite = IntPtr.Zero;
            CloseHandle(stderrWrite);
            stderrWrite = IntPtr.Zero;

            job = new AgentRecorderPortableJob();
            if (simulateOwnershipFailure || !job.AssignHandle(processInformation.Process))
            {
                throw new InvalidOperationException(
                    simulateOwnershipFailure
                        ? "simulated Job Object assignment failure before resume"
                        : "AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
            }

            // The managed wrapper and pipe ownership must exist while the
            // native process is still suspended. Only the resume and the
            // non-throwing ownership handoff remain after this point.
            process = Process.GetProcessById((int)processInformation.ProcessId);
            standardOutput = new StreamReader(
                new FileStream(new SafeFileHandle(stdoutRead, true), FileAccess.Read, 4096, false),
                Encoding.UTF8,
                true);
            stdoutRead = IntPtr.Zero;
            standardError = new StreamReader(
                new FileStream(new SafeFileHandle(stderrRead, true), FileAccess.Read, 4096, false),
                Encoding.UTF8,
                true);
            stderrRead = IntPtr.Zero;

            if (ResumeThread(processInformation.Thread) == Infinite)
            {
                throw new InvalidOperationException("ResumeThread failed: " + Marshal.GetLastWin32Error());
            }
            resumed = true;

            CloseHandle(processInformation.Thread);
            processInformation.Thread = IntPtr.Zero;
            var launched = new AgentRecorderPortableLaunchedProcess(
                process,
                standardOutput,
                standardError,
                job,
                processInformation.Process);
            processInformation.Process = IntPtr.Zero;
            process = null;
            standardOutput = null;
            standardError = null;
            job = null;
            success = true;
            return launched;
        }
        catch (Exception ex)
        {
            if (resumed && job != null)
            {
                job.Terminate(2);
            }
            else if (processInformation.Process != IntPtr.Zero)
            {
                TerminateProcess(processInformation.Process, 2);
            }
            if (processInformation.Process != IntPtr.Zero)
            {
                WaitForSingleObject(processInformation.Process, 5000);
            }
            if (standardOutput != null) standardOutput.Dispose();
            if (standardError != null) standardError.Dispose();
            if (process != null) process.Dispose();
            string phase = resumed ? "after target resume" : "before target resume";
            throw new InvalidOperationException(
                "process-tree ownership setup failed " + phase + ": " + ex.Message,
                ex);
        }
        finally
        {
            if (!success)
            {
                if (job != null) job.Dispose();
                if (processInformation.Thread != IntPtr.Zero) CloseHandle(processInformation.Thread);
                if (processInformation.Process != IntPtr.Zero) CloseHandle(processInformation.Process);
                if (stdoutRead != IntPtr.Zero) CloseHandle(stdoutRead);
                if (stdoutWrite != IntPtr.Zero) CloseHandle(stdoutWrite);
                if (stderrRead != IntPtr.Zero) CloseHandle(stderrRead);
                if (stderrWrite != IntPtr.Zero) CloseHandle(stderrWrite);
            }
        }
    }

    public void Dispose()
    {
        if (StandardOutput != null) StandardOutput.Dispose();
        if (StandardError != null) StandardError.Dispose();
        if (Process != null) Process.Dispose();
        if (Job != null) Job.Dispose();
        if (_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
        StandardOutput = null;
        StandardError = null;
        Process = null;
        Job = null;
    }
}
"@
}

function ConvertTo-WindowsCommandLineArgument {
    param(
        [AllowEmptyString()]
        [string]$Argument
    )

    if ($null -eq $Argument) {
        $Argument = ""
    }

    # Quote according to the CommandLineToArgvW / MS C runtime rules. In
    # particular, backslashes before quotes are doubled and trailing
    # backslashes are doubled before the closing quote.
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            for ($i = 0; $i -lt (2 * $backslashes + 1); $i++) {
                [void]$builder.Append('\')
            }
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }

        for ($i = 0; $i -lt $backslashes; $i++) {
            [void]$builder.Append('\')
        }
        [void]$builder.Append($character)
        $backslashes = 0
    }

    for ($i = 0; $i -lt (2 * $backslashes); $i++) {
        [void]$builder.Append('\')
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Get-BoundedTaskValue {
    param(
        [Parameter(Mandatory = $true)]$Task,
        [int]$TimeoutMs = 5000,
        [string]$Label = "process output"
    )

    if (-not $Task.Wait($TimeoutMs)) {
        throw "$Label drain timed out after $TimeoutMs ms."
    }
    return $Task.GetAwaiter().GetResult()
}

if ($TestArgumentQuoting) {
    $quotingCases = @(
        @{ Value = ""; Expected = '""' },
        @{ Value = "plain"; Expected = '"plain"' },
        @{ Value = "C:\Program Files\Agent\"; Expected = '"C:\Program Files\Agent\\"' },
        @{ Value = 'quote"inside'; Expected = '"quote\"inside"' },
        @{ Value = "C:\path with space\tail"; Expected = '"C:\path with space\tail"' }
    )
    foreach ($case in $quotingCases) {
        $actual = ConvertTo-WindowsCommandLineArgument -Argument $case.Value
        if ($actual -cne $case.Expected) {
            throw "Windows argument quoting mismatch for '$($case.Value)': '$actual' != '$($case.Expected)'."
        }
    }
    Write-Host "Windows command-line quoting tests passed ($($quotingCases.Count) cases)."
    exit 0
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName,

        [string[]]$Arguments = @(),

        [Parameter(Mandatory = $true)]
        [int]$TimeoutMs,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName,

        [string]$ReadyFile = "",

        [ValidateRange(100, 60000)]
        [int]$ReadyTimeoutMs = 5000,

        [switch]$SimulateTaskkillFailure,

        [switch]$SimulateJobOwnershipFailure
    )

    if ($TimeoutMs -le 0) {
        throw "Timeout must be positive for $DisplayName."
    }

    $quotedArguments = @(
        ConvertTo-WindowsCommandLineArgument -Argument $FileName
        foreach ($argument in $Arguments) {
            ConvertTo-WindowsCommandLineArgument -Argument ([string]$argument)
        }
    )
    $commandLine = [string]::Join(' ', $quotedArguments)
    $launched = $null
    $process = $null
    $jobAssigned = $false
    try {
        try {
            $launched = [AgentRecorderPortableLaunchedProcess]::Start(
                $FileName,
                $commandLine,
                [bool]$SimulateJobOwnershipFailure)
        }
        catch {
            $startError = $_.Exception.Message
            if ($startError -match "process-tree ownership setup failed") {
                throw "$DisplayName $startError"
            }
            throw "$DisplayName process-tree ownership setup failed before target resume: $startError"
        }
        $process = $launched.Process
        $jobAssigned = $launched.OwnershipEstablishedBeforeResume

        $stdoutTask = $launched.StandardOutput.ReadToEndAsync()
        $stderrTask = $launched.StandardError.ReadToEndAsync()
        $readyTimedOut = $false
        if (-not [string]::IsNullOrWhiteSpace($ReadyFile)) {
            $readyDeadline = [DateTime]::UtcNow.AddMilliseconds($ReadyTimeoutMs)
            while (-not (Test-Path -LiteralPath $ReadyFile -PathType Leaf) -and
                   [DateTime]::UtcNow -lt $readyDeadline) {
                Start-Sleep -Milliseconds 20
            }
            $readyTimedOut = -not (Test-Path -LiteralPath $ReadyFile -PathType Leaf)
        }

        $processExited = $false
        if (-not $readyTimedOut) {
            $processExited = $process.WaitForExit($TimeoutMs)
        }
        if ($readyTimedOut -or -not $processExited) {
            if ($SimulateTaskkillFailure) {
                $killOutput = @("simulated taskkill failure")
                $killExitCode = 1
            }
            else {
                $killOutput = @(& taskkill.exe /PID $process.Id /T /F 2>&1)
                $killExitCode = $LASTEXITCODE
            }

            $jobTerminated = $false
            if ($killExitCode -ne 0) {
                if ($null -ne $launched.Job) {
                    $jobTerminated = $launched.Job.Terminate(1)
                }
            }

            $rootExited = $process.WaitForExit(5000)
            $stdoutDrained = $true
            $stderrDrained = $true
            try { $null = Get-BoundedTaskValue -Task $stdoutTask -Label "$DisplayName stdout" } catch { $stdoutDrained = $false }
            try { $null = Get-BoundedTaskValue -Task $stderrTask -Label "$DisplayName stderr" } catch { $stderrDrained = $false }

            $jobEmpty = $false
            $jobActiveProcesses = 0
            $jobQueryError = 0
            if ($null -ne $launched.Job) {
                $jobEmpty = $launched.Job.WaitForEmpty(5000, [ref]$jobActiveProcesses, [ref]$jobQueryError)
                if ($jobEmpty) {
                    $jobEmptyStatus = "empty"
                }
                elseif ($jobQueryError -ne 0) {
                    $jobEmptyStatus = "query_failed"
                }
                else {
                    $jobEmptyStatus = "timed_out"
                }
            }
            else {
                $jobEmptyStatus = "missing"
                $jobQueryError = 6
            }

            $treeCleanupProven = $rootExited -and $stdoutDrained -and $stderrDrained -and $jobEmpty -and (
                $killExitCode -eq 0 -or ($jobAssigned -and $jobTerminated))
            if (-not $treeCleanupProven) {
                throw "$DisplayName timeout cleanup incomplete: root_exited=$rootExited ownership_established_before_resume=$jobAssigned taskkill_exit=$killExitCode job_terminated=$jobTerminated job_empty=$jobEmpty job_empty_status=$jobEmptyStatus job_active_processes=$jobActiveProcesses job_query_error=$jobQueryError stdout_drained=$stdoutDrained stderr_drained=$stderrDrained job_empty_timeout_ms=5000."
            }
            if ($readyTimedOut) {
                throw "$DisplayName readiness gate timed out after $ReadyTimeoutMs ms."
            }
            throw "$DisplayName timed out after $TimeoutMs ms: ownership_established_before_resume=$jobAssigned job_terminated=$jobTerminated job_empty=$jobEmpty job_empty_status=$jobEmptyStatus job_active_processes=$jobActiveProcesses job_query_error=$jobQueryError."
        }

        [pscustomobject]@{
            ExitCode = $launched.GetExitCode()
            ProcessId = $process.Id
            StandardOutput = Get-BoundedTaskValue -Task $stdoutTask -Label "$DisplayName stdout"
            StandardError = Get-BoundedTaskValue -Task $stderrTask -Label "$DisplayName stderr"
        }
    }
    finally {
        if ($null -ne $launched) {
            $launched.Dispose()
        }
    }
}

function Invoke-DotNetPublishWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName,

        [int]$MaxAttempts = 3
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $output = dotnet @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0) {
            return [pscustomobject]@{
                ExitCode = 0
                Output = $output
                Attempts = $attempt
            }
        }

        $outputText = ($output | Out-String)
        $isTransientFileLock =
            $outputText -match "being used by another process" -or
            $outputText -match "正由另一进程使用" -or
            $outputText -match "进程无法访问文件"

        if (-not $isTransientFileLock -or $attempt -eq $MaxAttempts) {
            return [pscustomobject]@{
                ExitCode = $exitCode
                Output = $output
                Attempts = $attempt
            }
        }

        $delaySeconds = 2 * $attempt
        Write-Host "[WARN] $DisplayName hit a transient output-file lock; retrying in $delaySeconds seconds ($attempt/$MaxAttempts)..." -ForegroundColor Yellow
        dotnet build-server shutdown 2>&1 | Out-Null
        Start-Sleep -Seconds $delaySeconds
    }
}

function Invoke-FastExitProcessAcceptanceTest {
    $testRoot = Join-Path $env:TEMP ("agent-recorder-fast-exit-" + [guid]::NewGuid().ToString("N"))
    $fixture = Join-Path $testRoot "fast-exit.cmd"
    $cmdExe = (Get-Command cmd.exe -ErrorAction Stop).Source
    $iterationCount = 100
    $expectedStdout = "fast-exit-stdout"
    $expectedStderr = "fast-exit-stderr"
    $testFailure = $null
    $cleanupFailure = $null
    $preCleanupAlivePids = @()
    $fallbackCleanup = "not_needed"
    $testPids = @()
    $identityMarkers = @{}
    try {
        New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
        @(
            "@echo off"
            "echo $expectedStdout"
            "echo $expectedStderr 1>&2"
            "exit /b 7"
        ) | Set-Content -LiteralPath $fixture -Encoding ASCII

        for ($iteration = 1; $iteration -le $iterationCount; $iteration++) {
            try {
                $result = Invoke-BoundedProcess `
                    -FileName $cmdExe `
                    -Arguments @("/d", "/c", $fixture) `
                    -TimeoutMs 10000 `
                    -DisplayName "fast-exit process $iteration"
            }
            catch {
                throw "fast-exit iteration $iteration raised launcher/setup failure: $($_.Exception.Message)"
            }
            $testPids += [int]$result.ProcessId
            $identityMarker = New-TestProcessIdentityMarker -Id $result.ProcessId -Marker $fixture
            if ($null -ne $identityMarker) {
                $identityMarkers[[int]$result.ProcessId] = $identityMarker
            }
            if ($result.ExitCode -ne 7) {
                throw "fast-exit iteration $iteration returned exit code $($result.ExitCode), expected 7."
            }
            if ($result.StandardOutput.Trim() -cne $expectedStdout) {
                throw "fast-exit iteration $iteration stdout mismatch: '$($result.StandardOutput.Trim())'."
            }
            if ($result.StandardError.Trim() -cne $expectedStderr) {
                throw "fast-exit iteration $iteration stderr mismatch: '$($result.StandardError.Trim())'."
            }
        }

        $preCleanupAlivePids = @(
            Get-TestOwnedProcessIds -Ids $testPids -Markers $identityMarkers
        )
        if ($preCleanupAlivePids.Count -ne 0) {
            throw "fast-exit pre-cleanup assertion failed: pre_cleanup_alive_pids=$($preCleanupAlivePids -join ',')"
        }
    }
    catch {
        $testFailure = $_.Exception
    }
    finally {
        try {
            $remaining = @(Get-TestOwnedProcessIds -Ids $testPids -Markers $identityMarkers)
            if ($remaining.Count -ne 0) {
                $fallbackCleanup = "used"
            }
            if ($remaining.Count -ne 0) {
                Stop-TestProcessIds -Ids $remaining -Markers $identityMarkers
            }
            if (Test-Path -LiteralPath $testRoot) {
                Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
            if (Test-Path -LiteralPath $testRoot) {
                throw "fast-exit test temporary directory was not removed: $testRoot"
            }
            $remainingAfterCleanup = @(Get-TestOwnedProcessIds -Ids $testPids -Markers $identityMarkers)
            if ($remainingAfterCleanup.Count -ne 0) {
                throw "fast-exit test-owned processes remain after cleanup: $($remainingAfterCleanup -join ',')"
            }
        }
        catch {
            $cleanupFailure = $_.Exception
        }
    }

    if ($null -ne $testFailure) {
        if ($null -ne $cleanupFailure) {
            throw "fast-exit acceptance failed: $($testFailure.Message); cleanup failed: $($cleanupFailure.Message)"
        }
        throw $testFailure
    }
    if ($null -ne $cleanupFailure) {
        throw $cleanupFailure
    }
    Write-Host "FAST_EXIT_PROCESS_TEST_OK iterations=$iterationCount exit_code=7 stdout=$expectedStdout stderr=$expectedStderr pre_cleanup_alive_pids=$($preCleanupAlivePids -join ',') fallback_cleanup=$fallbackCleanup remaining_processes=0 remaining_temp=0"
}

function Get-TestProcessIds {
    param([string]$PidFile)

    if (-not (Test-Path -LiteralPath $PidFile -PathType Leaf)) {
        return @()
    }
    $seen = @{}
    return @(
        Get-Content -LiteralPath $PidFile -ErrorAction SilentlyContinue |
            ForEach-Object {
                if ($_ -match '^\s*(\d+)\s*$') {
                    $id = [int]$Matches[1]
                    if (-not $seen.ContainsKey($id)) {
                        $seen[$id] = $true
                        $id
                    }
                }
            }
    )
}

function Get-TestProcessIdentity {
    param(
        [int]$Id
    )

    $process = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $null
    }
    try {
        $path = [string]$process.Path
        $startTimeUtc = $process.StartTime.ToUniversalTime()
        if ([string]::IsNullOrEmpty($path)) {
            return $null
        }
        return [pscustomobject]@{
            ProcessId = $Id
            ExecutablePath = $path
            StartTimeUtc = $startTimeUtc
            CommandLine = ""
        }
    }
    catch {
        return $null
    }
    finally {
        $process.Dispose()
    }
}

function Test-TestProcessIdentity {
    param(
        [Parameter(Mandatory = $true)][AllowNull()]$Identity,
        [Parameter(Mandatory = $true)][int]$ExpectedProcessId,
        [Parameter(Mandatory = $true)]$ExpectedMarker
    )

    if ($null -eq $Identity -or [int]$Identity.ProcessId -ne $ExpectedProcessId) {
        return $false
    }
    $expectedPath = [string]$ExpectedMarker.ExecutablePath
    $expectedStartTimeUtc = $ExpectedMarker.StartTimeUtc
    return -not [string]::IsNullOrEmpty([string]$ExpectedMarker.Marker) -and
        -not [string]::IsNullOrEmpty($expectedPath) -and
        $Identity.ExecutablePath -ieq $expectedPath -and
        $Identity.StartTimeUtc -eq $expectedStartTimeUtc
}

function New-TestProcessIdentityMarker {
    param(
        [Parameter(Mandatory = $true)][int]$Id,
        [Parameter(Mandatory = $true)][string]$Marker
    )

    $identity = Get-TestProcessIdentity -Id $Id
    if ($null -eq $identity) {
        return $null
    }
    return [pscustomobject]@{
        Marker = $Marker
        ExecutablePath = $identity.ExecutablePath
        StartTimeUtc = $identity.StartTimeUtc
    }
}

function Get-TestOwnedProcesses {
    param(
        [int[]]$Ids,
        [hashtable]$Markers
    )

    foreach ($id in $Ids) {
        if (-not $Markers.ContainsKey([int]$id)) {
            continue
        }
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            continue
        }
        $identity = Get-TestProcessIdentity -Id $id
        if (Test-TestProcessIdentity -Identity $identity -ExpectedProcessId $id -ExpectedMarker $Markers[[int]$id]) {
            $process
        }
    }
}

function Get-TestOwnedProcessIds {
    param(
        [int[]]$Ids,
        [hashtable]$Markers
    )

    foreach ($process in @(Get-TestOwnedProcesses -Ids $Ids -Markers $Markers)) {
        [int]$process.Id
    }
}

function Stop-TestProcessIds {
    param(
        [int[]]$Ids,
        [hashtable]$Markers
    )

    foreach ($process in @(Get-TestOwnedProcesses -Ids $Ids -Markers $Markers)) {
        try {
            Stop-Process -InputObject $process -Force -ErrorAction Stop
        }
        finally {
            $process.Dispose()
        }
    }
}

function Invoke-ProcessIdentitySafetyTest {
    $testRoot = Join-Path $env:TEMP ("agent-recorder-process-identity-" + [guid]::NewGuid().ToString("N"))
    $markerScript = Join-Path $testRoot "identity-marker.ps1"
    $powershellExe = (Get-Command powershell.exe -ErrorAction Stop).Source
    $process = $null
    $identityMarkers = @{}
    $testFailure = $null
    $cleanupFailure = $null
    $matchingOwned = $false
    $mismatchRejected = $false
    $missingRejected = $false
    $negativeControlAlive = $false
    $markerObserved = $false

    try {
        New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
        "Start-Sleep -Seconds 30" | Set-Content -LiteralPath $markerScript -Encoding Unicode
        $process = Start-Process `
            -FilePath $powershellExe `
            -ArgumentList @("-NoProfile", "-File", $markerScript) `
            -PassThru
        $identityMarkers[[int]$process.Id] = New-TestProcessIdentityMarker -Id $process.Id -Marker $markerScript

        $identity = Get-TestProcessIdentity -Id $process.Id
        $expectedMarker = New-TestProcessIdentityMarker -Id $process.Id -Marker $markerScript
        $markerObserved = ([string]$process.StartInfo.Arguments).IndexOf(
            $markerScript,
            [StringComparison]::OrdinalIgnoreCase) -ge 0
        $matchingOwned = Test-TestProcessIdentity `
            -Identity $identity `
            -ExpectedProcessId $process.Id `
            -ExpectedMarker $expectedMarker

        $mismatchIdentity = [pscustomobject]@{
            ProcessId = $process.Id
            ExecutablePath = $identity.ExecutablePath
            StartTimeUtc = $identity.StartTimeUtc.AddSeconds(-1)
            CommandLine = ""
        }
        $mismatchRejected = -not (Test-TestProcessIdentity `
            -Identity $mismatchIdentity `
            -ExpectedProcessId $process.Id `
            -ExpectedMarker $expectedMarker)
        $missingRejected = -not (Test-TestProcessIdentity `
            -Identity $null `
            -ExpectedProcessId 2147483647 `
            -ExpectedMarker $markerScript)

        if (-not $markerObserved -or -not $matchingOwned -or -not $mismatchRejected -or -not $missingRejected) {
            throw "process identity classifier mismatch: marker_observed=$markerObserved matching_owned=$matchingOwned mismatch_rejected=$mismatchRejected missing_rejected=$missingRejected"
        }

        $negativeControlAlive = $null -ne (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)
        if (-not $negativeControlAlive) {
            throw "process identity negative-control process exited before the mismatch assertion completed: pid=$($process.Id)"
        }
    }
    catch {
        $testFailure = $_.Exception
    }
    finally {
        try {
            if ($null -ne $process) {
                $owned = @(Get-TestOwnedProcessIds -Ids @([int]$process.Id) -Markers $identityMarkers)
                if ($owned.Count -ne 0) {
                    Stop-TestProcessIds -Ids $owned -Markers $identityMarkers
                }
                $remainingOwned = @(Get-TestOwnedProcessIds -Ids @([int]$process.Id) -Markers $identityMarkers)
                if ($remainingOwned.Count -ne 0) {
                    throw "process identity test-owned process remains after cleanup: $($remainingOwned -join ',')"
                }
                $process.Dispose()
                $process = $null
            }
            if (Test-Path -LiteralPath $testRoot) {
                Remove-Item -LiteralPath $testRoot -Recurse -Force
            }
            if (Test-Path -LiteralPath $testRoot) {
                throw "process identity test temporary directory was not removed: $testRoot"
            }
        }
        catch {
            $cleanupFailure = $_.Exception
        }
    }

    if ($null -ne $testFailure) {
        if ($null -ne $cleanupFailure) {
            throw "process identity safety test failed: $($testFailure.Message); cleanup failed: $($cleanupFailure.Message)"
        }
        throw $testFailure
    }
    if ($null -ne $cleanupFailure) {
        throw $cleanupFailure
    }
    Write-Host "PROCESS_IDENTITY_SAFETY_TEST_OK marker=exact_script_path matching=owned mismatch=unrelated missing=gone negative_control_alive=$negativeControlAlive test_owned_cleanup=completed remaining_processes=0 remaining_temp=0"
}

function Invoke-ProcessTreeAcceptanceTest {
    $testRoot = Join-Path $env:TEMP ("agent-recorder-process-tree-" + [guid]::NewGuid().ToString("N"))
    $pidFile = Join-Path $testRoot "pids.txt"
    $parentScriptPath = Join-Path $testRoot "parent.ps1"
    $childScriptPath = Join-Path $testRoot "child.ps1"
    $grandchildScriptPath = Join-Path $testRoot "grandchild.ps1"
    $powershellExe = (Get-Command powershell.exe -ErrorAction Stop).Source
    $identityMarkers = @{}
    try {
        New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
        $childScript = @'
param([string]$PidFile, [string]$GrandchildScript, [string]$PowerShellPath)
$grandchild = Start-Process -FilePath $PowerShellPath -ArgumentList @('-NoProfile', '-File', $GrandchildScript, $PidFile) -PassThru
[System.IO.File]::AppendAllText($PidFile, "$($grandchild.Id)`n")
Start-Sleep -Seconds 60
'@
        $grandchildScript = @'
param([string]$PidFile)
[System.IO.File]::AppendAllText($PidFile, "$PID`n")
Start-Sleep -Seconds 60
'@
        $parentScript = @'
param([string]$PidFile, [string]$ChildScript, [string]$GrandchildScript, [string]$PowerShellPath)
$child = Start-Process -FilePath $PowerShellPath -ArgumentList @('-NoProfile', '-File', $ChildScript, $PidFile, $GrandchildScript, $PowerShellPath) -PassThru
[System.IO.File]::WriteAllText($PidFile, "$PID`n$($child.Id)`n")
while ((Get-Content -LiteralPath $PidFile -ErrorAction SilentlyContinue).Count -lt 3) {
    Start-Sleep -Milliseconds 10
}
Start-Sleep -Seconds 60
'@
        $childScript | Set-Content -LiteralPath $childScriptPath -Encoding Unicode
        $grandchildScript | Set-Content -LiteralPath $grandchildScriptPath -Encoding Unicode
        $parentScript | Set-Content -LiteralPath $parentScriptPath -Encoding Unicode

        $scenarios = @()
        for ($iteration = 1; $iteration -le 20; $iteration++) {
            $scenarios += @{
                Name = "job_cleanup_{0:D2}" -f $iteration
                SimulateTaskkillFailure = $true
                SimulateJobOwnershipFailure = $false
                ExpectIncomplete = $false
                OwnershipEstablishedBeforeResume = $true
            }
        }
        $scenarios += @{
            Name = "ownership_missing"
            SimulateTaskkillFailure = $true
            SimulateJobOwnershipFailure = $true
            ExpectIncomplete = $true
            OwnershipEstablishedBeforeResume = $false
        }
        foreach ($scenario in $scenarios) {
            if (Test-Path -LiteralPath $pidFile) { Remove-Item -LiteralPath $pidFile -Force }
            $invokeArgs = @{
                FileName = $powershellExe
                Arguments = @("-NoProfile", "-File", $parentScriptPath, $pidFile, $childScriptPath, $grandchildScriptPath, $powershellExe)
                TimeoutMs = 1000
                ReadyFile = $pidFile
                ReadyTimeoutMs = 5000
                DisplayName = "process-tree test $($scenario.Name)"
            }
            if ($scenario.SimulateTaskkillFailure) { $invokeArgs.SimulateTaskkillFailure = $true }
            if ($scenario.SimulateJobOwnershipFailure) { $invokeArgs.SimulateJobOwnershipFailure = $true }

            $invoked = $false
            $failureMessage = ""
            try {
                $null = Invoke-BoundedProcess @invokeArgs
                $invoked = $true
            }
            catch {
                $failureMessage = $_.Exception.Message
            }
            if ($invoked) {
                throw "Process-tree test unexpectedly completed without a timeout: $($scenario.Name)"
            }
            $ids = @(Get-TestProcessIds -PidFile $pidFile)
            if ($ids.Count -eq 3) {
                $identityMarkers = @{}
                $identityMarkerPaths = @($parentScriptPath, $childScriptPath, $grandchildScriptPath)
                for ($identityIndex = 0; $identityIndex -lt $ids.Count; $identityIndex++) {
                    $identityMarker = New-TestProcessIdentityMarker `
                        -Id $ids[$identityIndex] `
                        -Marker $identityMarkerPaths[$identityIndex]
                    if ($null -ne $identityMarker) {
                        $identityMarkers[[int]$ids[$identityIndex]] = $identityMarker
                    }
                }
            }
            else {
                $identityMarkers = @{}
            }
            $preCleanupAlive = @(Get-TestOwnedProcessIds -Ids $ids -Markers $identityMarkers)
            $productCleanup = "unknown"
            $fallbackCleanup = "not_run"
            try {
                if ($scenario.ExpectIncomplete) {
                    if ($failureMessage -notmatch "process-tree ownership setup failed") {
                        throw "Ownership failure was not explicit: $failureMessage"
                    }
                    if ($ids.Count -ne 0 -or $preCleanupAlive.Count -ne 0) {
                        throw "Ownership failure released target workload: pre_cleanup_alive_pids=$($preCleanupAlive -join ',')"
                    }
                    $productCleanup = "ownership_setup_failed_before_resume"
                }
                else {
                    if ($failureMessage -match "timeout cleanup incomplete" -or $failureMessage -notmatch "timed out") {
                        throw "Job-owned timeout did not prove cleanup: $failureMessage"
                    }
                    if ($failureMessage -notmatch "job_terminated=True" -or
                        $failureMessage -notmatch "job_empty=True" -or
                        $failureMessage -notmatch "job_empty_status=empty") {
                        throw "Job-owned timeout did not include authoritative empty-Job proof: $failureMessage"
                    }
                    if ($ids.Count -ne 3) {
                        throw "Process-tree test did not publish all three PIDs before timeout: $($scenario.Name)"
                    }
                    if ($preCleanupAlive.Count -gt 0) {
                        throw "Job Object cleanup left pre-cleanup PIDs alive: $($preCleanupAlive -join ',')"
                    }
                    $productCleanup = "job_terminated_and_empty_before_fallback"
                }
            }
            finally {
                $remaining = @(Get-TestProcessIds -PidFile $pidFile)
                $remainingAlive = @(Get-TestOwnedProcessIds -Ids $remaining -Markers $identityMarkers)
                if ($remainingAlive.Count -gt 0) {
                    Stop-TestProcessIds -Ids $remainingAlive -Markers $identityMarkers
                    $fallbackCleanup = "test_owned_fallback_used"
                }
                else {
                    $fallbackCleanup = "not_needed"
                }
            }
            $jobEvidence = if ($scenario.ExpectIncomplete) { "not_applicable" } else { "True" }
            $jobStatusEvidence = if ($scenario.ExpectIncomplete) { "not_applicable" } else { "empty" }
            Write-Host "PROCESS_TREE_TEST_OK scenario=$($scenario.Name) pre_cleanup_alive_pids=$($preCleanupAlive -join ',') ownership_established_before_resume=$($scenario.OwnershipEstablishedBeforeResume) job_terminated=$jobEvidence job_empty=$jobEvidence job_empty_status=$jobStatusEvidence product_cleanup=$productCleanup fallback_cleanup=$fallbackCleanup"
        }
    }
    finally {
        $remaining = @(Get-TestProcessIds -PidFile $pidFile)
        Stop-TestProcessIds -Ids $remaining -Markers $identityMarkers
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
}

if ($TestProcessIdentitySafety) {
    Invoke-ProcessIdentitySafetyTest
    exit 0
}

if ($TestFastExitProcess) {
    Invoke-FastExitProcessAcceptanceTest
    exit 0
}

if ($TestProcessTree) {
    Invoke-ProcessTreeAcceptanceTest
    exit 0
}

function Assert-PortableZipContents {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string[]]$ForbiddenText = @()
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.FullName) })
        $expectedWgc = "AgentRecorder.WgcHelper/wgc-native-helper.exe"
        if (@($entries | Where-Object { $_.FullName.Replace('\', '/') -eq $expectedWgc }).Count -ne 1) {
            throw "Portable zip does not contain exactly one production WGC helper at $expectedWgc."
        }

        $unexpectedWgc = @($entries | Where-Object {
            $normalizedName = $_.FullName.Replace('\', '/')
            $normalizedName.StartsWith("AgentRecorder.WgcHelper/", [System.StringComparison]::OrdinalIgnoreCase) -and
            $normalizedName -ne $expectedWgc
        })
        if ($unexpectedWgc.Count -gt 0) {
            throw "Portable zip contains unexpected WGC helper artifacts: $($unexpectedWgc.FullName -join ', ')."
        }

        $forbidden = @($entries | Where-Object {
            $normalizedName = $_.FullName.Replace('\', '/')
            $normalizedName -match '(^|/)(doc|prompt|report|\.git|\.local-data)(/|$)' -or
            $normalizedName -match '(^|/)api-key\.txt$' -or
            $normalizedName -match '\.(pdb|obj|cpp|h|vcxproj|sln)$'
        })
        if ($forbidden.Count -gt 0) {
            throw "Portable zip contains forbidden artifacts: $($forbidden.FullName -join ', ')."
        }

        $textPathLeaks = @()
        foreach ($entry in $entries) {
            $normalizedName = $entry.FullName.Replace('\', '/')
            if ($normalizedName -notmatch '\.(md|txt|json|xml|config|ps1|cs|csproj)$') {
                continue
            }
            $reader = New-Object System.IO.StreamReader($entry.Open())
            try {
                $text = $reader.ReadToEnd()
            } finally {
                $reader.Dispose()
            }
            foreach ($needle in $ForbiddenText) {
                if (-not [string]::IsNullOrWhiteSpace($needle) -and
                    $text.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $textPathLeaks += $normalizedName
                    break
                }
            }
        }
        if ($textPathLeaks.Count -gt 0) {
            throw "Portable zip text contains forbidden repository paths: $($textPathLeaks -join ', ')."
        }

        return $entries.Count
    }
    finally {
        $archive.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
} else {
    $ProjectRoot = (Resolve-Path $ProjectRoot).Path
}

$ReleaseTag = "AgentRecorder-$Version-win-x64"
if ($PublishMode -eq "self-contained") {
    $ReleaseTag += "-self-contained"
} else {
    $ReleaseTag += "-framework-dependent"
}

$StagingDir = Join-Path $ProjectRoot ".local-data\release-candidates\$ReleaseTag"
$ZipPath = Join-Path $ProjectRoot ".local-data\release-candidates\$ReleaseTag.zip"

if (Test-Path $StagingDir) {
    Remove-Item $StagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

Write-Host "=== Agent Recorder Portable Release Builder ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Mode: $PublishMode"
Write-Host "Staging: $StagingDir"
Write-Host ""

$appProject = Join-Path $ProjectRoot "src\AgentRecorder.App\AgentRecorder.App.csproj"
if (-not (Test-Path $appProject)) {
    Write-Host "[ERROR] AgentRecorder.App.csproj not found at $appProject" -ForegroundColor Red
    exit 1
}

$headlessProject = Join-Path $ProjectRoot "src\AgentRecorder.Headless\AgentRecorder.Headless.csproj"
if (-not (Test-Path $headlessProject)) {
    Write-Host "[ERROR] AgentRecorder.Headless.csproj not found at $headlessProject" -ForegroundColor Red
    exit 1
}

$cliProject = Join-Path $ProjectRoot "tools\AgentRecorder.Cli\AgentRecorder.Cli.csproj"
if (-not (Test-Path $cliProject)) {
    Write-Host "[ERROR] AgentRecorder.Cli.csproj not found at $cliProject" -ForegroundColor Red
    exit 1
}

$audioHelperProject = Join-Path $ProjectRoot "tools\AgentRecorder.AudioHelper\AgentRecorder.AudioHelper.csproj"
if (-not (Test-Path $audioHelperProject)) {
    Write-Host "[ERROR] AgentRecorder.AudioHelper.csproj not found at $audioHelperProject" -ForegroundColor Red
    exit 1
}

$nativeBuildScript = Join-Path $ProjectRoot "tools\wgc-native-helper\build-native.ps1"
if (-not (Test-Path $nativeBuildScript)) {
    Write-Host "[ERROR] Native WGC build script not found at $nativeBuildScript" -ForegroundColor Red
    exit 1
}

$appPublishDir = Join-Path $StagingDir "AgentRecorder.App"
$headlessPublishDir = Join-Path $StagingDir "AgentRecorder.Headless"
$cliPublishDir = Join-Path $StagingDir "AgentRecorder.Cli"
$audioHelperPublishDir = Join-Path $StagingDir "AgentRecorder.AudioHelper"
$wgcHelperPublishDir = Join-Path $StagingDir "AgentRecorder.WgcHelper"
New-Item -ItemType Directory -Path $wgcHelperPublishDir -Force | Out-Null

$nativeBuildHeadroomMs = 120000
$nativeBuildTimeoutMs64 = [int64]$NativeTestTimeoutMs + [int64]$nativeBuildHeadroomMs
if ($nativeBuildTimeoutMs64 -gt [int]::MaxValue) {
    throw "Native build timeout exceeds the supported process wait range."
}
$nativeBuildTimeoutMs = [int]$nativeBuildTimeoutMs64

Write-Host "[1/11] Building and testing native WGC helper (Release|x64)..." -ForegroundColor Yellow
$powershellExe = (Get-Command powershell.exe -ErrorAction Stop).Source
$nativeBuildResult = Invoke-BoundedProcess `
    -FileName $powershellExe `
    -Arguments @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $nativeBuildScript,
        "-Configuration",
        "Release",
        "-Platform",
        "x64",
        "-OutputExeDir",
        $wgcHelperPublishDir,
        "-TestTimeoutMs",
        $NativeTestTimeoutMs.ToString([Globalization.CultureInfo]::InvariantCulture)
    ) `
    -TimeoutMs $nativeBuildTimeoutMs `
    -DisplayName "native WGC build and tests"
if ($nativeBuildResult.StandardOutput) { Write-Host $nativeBuildResult.StandardOutput }
if ($nativeBuildResult.StandardError) { Write-Host $nativeBuildResult.StandardError -ForegroundColor DarkYellow }
if ($nativeBuildResult.ExitCode -ne 0) {
    throw "Native WGC build/tests failed with exit code $($nativeBuildResult.ExitCode)."
}

$wgcHelperExe = Join-Path $wgcHelperPublishDir "wgc-native-helper.exe"
if (-not (Test-Path -LiteralPath $wgcHelperExe -PathType Leaf)) {
    throw "Native WGC build did not produce the production helper: $wgcHelperExe"
}
$wgcHelperInfo = Get-Item -LiteralPath $wgcHelperExe -Force
if (($wgcHelperInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Native WGC production helper is a reparse-point file: $wgcHelperExe"
}

Write-Host "[1/11] Running bounded WGC helper --version smoke..." -ForegroundColor Yellow
$versionResult = Invoke-BoundedProcess `
    -FileName $wgcHelperExe `
    -Arguments @("--version") `
    -TimeoutMs 10000 `
    -DisplayName "WGC helper --version smoke"
$normalizedVersion = $versionResult.StandardOutput.Replace("`r`n", "`n").Replace("`r", "`n")
if ($versionResult.ExitCode -ne 0 -or $normalizedVersion -cne "wgc-native-helper 0.3.0`n") {
    throw "WGC helper --version smoke failed or returned an incompatible contract."
}
Write-Host "[OK] Native tests passed and helper version is wgc-native-helper 0.3.0" -ForegroundColor Green

# ReadyToRun: enabled by default for self-contained, disabled for framework-dependent or when explicitly requested.
$enableR2R = ($PublishMode -eq "self-contained") -and -not $DisableReadyToRun
Write-Host "ReadyToRun: $(if ($enableR2R) { 'enabled' } else { 'disabled' })" -ForegroundColor Gray
Write-Host ""

Write-Host "[2/11] Publishing AgentRecorder.App ($PublishMode)..." -ForegroundColor Yellow

if ($PublishMode -eq "self-contained") {
    $publishArgs = @(
        "publish", $appProject,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $appPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=$($enableR2R.ToString().ToLowerInvariant())",
        "-p:Deterministic=false"
    )
} else {
    $publishArgs = @(
        "publish", $appProject,
        "--configuration", "Release",
        "--output", $appPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false",
        "-p:Deterministic=false"
    )
}

$publishAttempt = Invoke-DotNetPublishWithRetry -Arguments $publishArgs -DisplayName "AgentRecorder.App publish"
if ($publishAttempt.ExitCode -ne 0) {
    Write-Host "[ERROR] dotnet publish failed:" -ForegroundColor Red
    Write-Host $publishAttempt.Output
    exit 1
}
Write-Host "[OK] Published to $appPublishDir" -ForegroundColor Green

Write-Host "[3/11] Publishing AgentRecorder.Headless ($PublishMode)..." -ForegroundColor Yellow

if ($PublishMode -eq "self-contained") {
    $headlessPublishArgs = @(
        "publish", $headlessProject,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $headlessPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=$($enableR2R.ToString().ToLowerInvariant())",
        "-p:Deterministic=false"
    )
} else {
    $headlessPublishArgs = @(
        "publish", $headlessProject,
        "--configuration", "Release",
        "--output", $headlessPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false",
        "-p:Deterministic=false"
    )
}

$headlessPublishAttempt = Invoke-DotNetPublishWithRetry -Arguments $headlessPublishArgs -DisplayName "AgentRecorder.Headless publish"
if ($headlessPublishAttempt.ExitCode -ne 0) {
    Write-Host "[ERROR] dotnet publish (Headless) failed:" -ForegroundColor Red
    Write-Host $headlessPublishAttempt.Output
    exit 1
}
Write-Host "[OK] Published to $headlessPublishDir" -ForegroundColor Green

Write-Host "[4/11] Publishing AgentRecorder.Cli ($PublishMode)..." -ForegroundColor Yellow

if ($PublishMode -eq "self-contained") {
    $cliPublishArgs = @(
        "publish", $cliProject,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $cliPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=$($enableR2R.ToString().ToLowerInvariant())",
        "-p:Deterministic=false"
    )
} else {
    $cliPublishArgs = @(
        "publish", $cliProject,
        "--configuration", "Release",
        "--output", $cliPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false",
        "-p:Deterministic=false"
    )
}

$cliPublishAttempt = Invoke-DotNetPublishWithRetry -Arguments $cliPublishArgs -DisplayName "AgentRecorder.Cli publish"
if ($cliPublishAttempt.ExitCode -ne 0) {
    Write-Host "[ERROR] dotnet publish (Cli) failed:" -ForegroundColor Red
    Write-Host $cliPublishAttempt.Output
    exit 1
}
Write-Host "[OK] Published to $cliPublishDir" -ForegroundColor Green

Write-Host "[5/11] Publishing AgentRecorder.AudioHelper ($PublishMode)..." -ForegroundColor Yellow

if ($PublishMode -eq "self-contained") {
    $audioHelperPublishArgs = @(
        "publish", $audioHelperProject,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $audioHelperPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=$($enableR2R.ToString().ToLowerInvariant())",
        "-p:Deterministic=false"
    )
} else {
    $audioHelperPublishArgs = @(
        "publish", $audioHelperProject,
        "--configuration", "Release",
        "--output", $audioHelperPublishDir,
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false",
        "-p:Deterministic=false"
    )
}

$audioHelperPublishAttempt = Invoke-DotNetPublishWithRetry -Arguments $audioHelperPublishArgs -DisplayName "AgentRecorder.AudioHelper publish"
if ($audioHelperPublishAttempt.ExitCode -ne 0) {
    Write-Host "[ERROR] dotnet publish (AudioHelper) failed:" -ForegroundColor Red
    Write-Host $audioHelperPublishAttempt.Output
    exit 1
}
Write-Host "[OK] Published to $audioHelperPublishDir" -ForegroundColor Green

Write-Host "[6/11] Removing PDBs, XML docs, and dev artifacts..." -ForegroundColor Yellow
$removed = 0
Get-ChildItem -Path $appPublishDir,$headlessPublishDir,$cliPublishDir,$audioHelperPublishDir -Recurse -File | Where-Object {
    $_.Extension -eq ".pdb" -or
    $_.Extension -eq ".xml" -or
    ($_.Name -like "*Tests*") -or
    ($_.Name -like "*test*")
} | ForEach-Object {
    Remove-Item $_.FullName -Force
    $removed++
}
Write-Host "[OK] Removed $removed non-essential files" -ForegroundColor Green

Write-Host "[7/11] Copying FFmpeg binaries..." -ForegroundColor Yellow
$ffmpegSrc = Join-Path $ProjectRoot "tools\ffmpeg\bin"
if (-not (Test-Path $ffmpegSrc)) {
    Write-Host "[ERROR] FFmpeg bin not found at $ffmpegSrc" -ForegroundColor Red
    exit 1
}

$ffmpegFiles = @(
    "ffmpeg.exe", "ffprobe.exe",
    "avcodec-58.dll", "avdevice-58.dll", "avfilter-7.dll",
    "avformat-58.dll", "avutil-56.dll", "postproc-55.dll",
    "swresample-3.dll", "swscale-5.dll"
)

foreach ($file in $ffmpegFiles) {
    $src = Join-Path $ffmpegSrc $file
    $dst = Join-Path $appPublishDir $file
    if (Test-Path $src) {
        Copy-Item $src -Destination $dst -Force
    }
}
Write-Host "[OK] FFmpeg copied to app directory" -ForegroundColor Green

Write-Host "[8/11] Preparing portable package layout..." -ForegroundColor Yellow

$requiredAudioHelperFiles = @(
    "AgentRecorder.AudioHelper.exe",
    "NAudio.Core.dll",
    "NAudio.Wasapi.dll"
)

$helperMissing = $false
foreach ($file in $requiredAudioHelperFiles) {
    $path = Join-Path $audioHelperPublishDir $file
    if (-not (Test-Path $path)) {
        Write-Host "[ERROR] Audio helper dependency missing: $path" -ForegroundColor Red
        $helperMissing = $true
    }
}

if ($helperMissing) {
    exit 1
}

$wgcArtifacts = @(Get-ChildItem -LiteralPath $wgcHelperPublishDir -Force)
if ($wgcArtifacts.Count -ne 1 -or $wgcArtifacts[0].Name -ne "wgc-native-helper.exe" -or $wgcArtifacts[0].PSIsContainer) {
    throw "Portable WGC helper directory must contain only wgc-native-helper.exe."
}

$helperSize = (Get-ChildItem -Path $audioHelperPublishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$wgcHelperSize = (Get-Item -LiteralPath $wgcHelperExe).Length
$helperSizeMB = [math]::Round($helperSize / 1MB, 2)
Write-Host "[OK] Portable package layout prepared (WGC helper: $wgcHelperSize bytes; audio helper: $helperSizeMB MB)" -ForegroundColor Green

Write-Host "[9/11] Adding documentation..." -ForegroundColor Yellow

# Root-level docs (including agent instructions and API reference)
foreach ($rootDoc in @("README.md", "README.zh-CN.md", "AGENT-INSTRUCTIONS.zh-CN.md", "AGENT-API-REFERENCE.zh-CN.md")) {
    $src = Join-Path $ProjectRoot $rootDoc
    if (Test-Path $src) {
        Copy-Item $src -Destination (Join-Path $StagingDir $rootDoc) -Force
    }
}

foreach ($packageDoc in @("QUICKSTART.md", "QUICKSTART.zh-CN.md", "LICENSE", "LICENSE-NOTICE.md")) {
    $src = Join-Path $ProjectRoot $packageDoc
    if (Test-Path $src) {
        Copy-Item $src -Destination (Join-Path $StagingDir $packageDoc) -Force
    }
}

Write-Host "[OK] Documentation added" -ForegroundColor Green

Write-Host "[10/11] Creating zip archive..." -ForegroundColor Yellow
$zipParent = Split-Path $ZipPath -Parent
if (-not (Test-Path $zipParent)) {
    New-Item -ItemType Directory -Path $zipParent -Force | Out-Null
}

if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}

try {
    Compress-Archive -Path "$StagingDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal
    $zipEntryCount = Assert-PortableZipContents -Path $ZipPath -ForbiddenText @(
        $ProjectRoot,
        $ProjectRoot.Replace('\', '/')
    )
    $zipSize = (Get-Item $ZipPath).Length
    $zipSizeMB = [math]::Round($zipSize / 1MB, 2)
} catch {
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }
    throw "Portable zip creation or validation failed; invalid candidate removed. $($_.Exception.Message)"
}
Write-Host "[10/11] Created $ZipPath ($zipSizeMB MB; $zipEntryCount zip entries; WGC helper verified)" -ForegroundColor Green

Write-Host "[11/11] Portable package contract verified." -ForegroundColor Green

Write-Host ""
Write-Host "=== Release Build Complete ===" -ForegroundColor Cyan
Write-Host "  Mode: $PublishMode"
Write-Host "  Zip: $ZipPath"
Write-Host "  Size: $zipSizeMB MB"
Write-Host "  WGC helper: AgentRecorder.WgcHelper\wgc-native-helper.exe ($wgcHelperSize bytes)"
Write-Host "  AudioHelper size: $helperSizeMB MB"
Write-Host "  Staging: $StagingDir"
Write-Host ""
Write-Host "Smoke test (after extracting):" -ForegroundColor Cyan
Write-Host "  cd <extract-dir>"
Write-Host "  AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json"
Write-Host "  Let the local AI agent read AGENT-INSTRUCTIONS.zh-CN.md and AGENT-API-REFERENCE.zh-CN.md"
Write-Host "  Prefer POST http://127.0.0.1:37891/api/v1/recordings/quick for common recording intents"
Write-Host ""
