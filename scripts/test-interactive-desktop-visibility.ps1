#Requires -Version 5.1
<#
.SYNOPSIS
    Diagnose interactive desktop and window station visibility.

.DESCRIPTION
    Uses Win32 P/Invoke to enumerate windows in the current PowerShell process,
    compares with API /api/v1/windows results, and collects environment info
    to determine whether the current session has a visible interactive desktop.

    With -StartDemoApp: collects environment snapshots before and after app start.

    Outputs a structured JSON report to .local-data\demo-runs\.

.PARAMETER StartDemoApp
    If specified, start the demo app before running diagnostics and stop it after.
    Collects before/after environment snapshots for comparison.

.PARAMETER Port
    API port (default: 37891).

.PARAMETER OutputDir
    Directory for JSON report output.

.EXAMPLE
    .\scripts\test-interactive-desktop-visibility.ps1
    # Diagnose without starting app (requires app already running on 37891)

.EXAMPLE
    .\scripts\test-interactive-desktop-visibility.ps1 -StartDemoApp
    # Start demo app, diagnose with before/after snapshots, then stop
#>

param(
    [switch]$StartDemoApp = $false,
    [int]$Port = 37891,
    [string]$OutputDir = "D:\works\python\007-Agent-Recorder\.local-data\demo-runs"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = "D:\works\python\007-Agent-Recorder"
$DataDir = "D:\works\python\007-Agent-Recorder\.local-data"
$MetadataFile = "D:\works\python\007-Agent-Recorder\.local-data\demo-app-server.json"
$script:AppStarted = $false

$Win32 = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class Win32Windows {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
"@

Add-Type $Win32 -ErrorAction Stop

$script:AllWindows = New-Object System.Collections.ArrayList
$script:VisibleWindows = New-Object System.Collections.ArrayList
$script:TitledWindows = New-Object System.Collections.ArrayList

function Enum-Callback {
    param([IntPtr]$hWnd, [IntPtr]$lParam)

    $isVisible = [Win32Windows]::IsWindowVisible($hWnd)
    $isMinimized = [Win32Windows]::IsIconic($hWnd)

    $sbTitle = New-Object System.Text.StringBuilder 512
    $titleLen = [Win32Windows]::GetWindowText($hWnd, $sbTitle, 512)
    $title = $sbTitle.ToString()

    $procId = 0
    [void][Win32Windows]::GetWindowThreadProcessId($hWnd, [ref]$procId)

    $procName = ""
    try {
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($proc) { $procName = $proc.ProcessName }
    } catch { }

    $sbClass = New-Object System.Text.StringBuilder 256
    [void][Win32Windows]::GetClassName($hWnd, $sbClass, 256)
    $className = $sbClass.ToString()

    $winInfo = [ordered]@{
        hwnd = "0x$($hWnd.ToString('X8'))"
        title = $title
        process_id = [int]$procId
        process_name = $procName
        visible = $isVisible
        minimized = $isMinimized
        class_name = $className
    }

    [void]$script:AllWindows.Add($winInfo)

    if ($isVisible) {
        [void]$script:VisibleWindows.Add($winInfo)
        if ($title.Length -gt 0) {
            [void]$script:TitledWindows.Add($winInfo)
        }
    }

    return $true
}

function Invoke-Win32Enum {
    $script:AllWindows.Clear()
    $script:VisibleWindows.Clear()
    $script:TitledWindows.Clear()

    Write-Host "[INFO] Enumerating windows via Win32 EnumWindows..." -ForegroundColor Cyan
    $callback = [Win32Windows+EnumWindowsProc]({
        param($hWnd, $lParam)
        return Enum-Callback -hWnd $hWnd -lParam $lParam
    })
    [void][Win32Windows]::EnumWindows($callback, [IntPtr]::Zero)

    Write-Host "[INFO] Win32 EnumWindows total: $($script:AllWindows.Count)" -ForegroundColor Cyan
    Write-Host "[INFO] Visible windows: $($script:VisibleWindows.Count)" -ForegroundColor Cyan
    Write-Host "[INFO] Titled visible windows: $($script:TitledWindows.Count)" -ForegroundColor Cyan

    if ($script:TitledWindows.Count -gt 0) {
        Write-Host ""
        Write-Host "Top 20 titled visible windows:" -ForegroundColor Cyan
        $script:TitledWindows | Select-Object -First 20 | ForEach-Object {
            Write-Host "  $($_.hwnd) [$($_.process_name):$($_.process_id)] $($_.title)"
        }
    }
}

function Get-ForegroundWindowInfo {
    $hWnd = [Win32Windows]::GetForegroundWindow()
    if ($hWnd -eq [IntPtr]::Zero) {
        return [ordered]@{
            hwnd = "0x00000000"
            title = ""
            process_id = 0
            process_name = ""
            note = "GetForegroundWindow returned NULL (no foreground window or invisible desktop)"
        }
    }

    $sbTitle = New-Object System.Text.StringBuilder 512
    [void][Win32Windows]::GetWindowText($hWnd, $sbTitle, 512)

    $procId = 0
    [void][Win32Windows]::GetWindowThreadProcessId($hWnd, [ref]$procId)

    $procName = ""
    try {
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($proc) { $procName = $proc.ProcessName }
    } catch { }

    return [ordered]@{
        hwnd = "0x$($hWnd.ToString('X8'))"
        title = $sbTitle.ToString()
        process_id = [int]$procId
        process_name = $procName
    }
}

function Invoke-ApiGet {
    param([string]$Path)
    try {
        $r = Invoke-WebRequest "http://127.0.0.1:$Port$Path" -UseBasicParsing -TimeoutSec 5
        return [pscustomobject]@{ StatusCode = $r.StatusCode; Content = $r.Content; Success = $true }
    } catch {
        return [pscustomobject]@{ StatusCode = 0; Content = $_.Exception.Message; Success = $false }
    }
}

function Get-ListeningPid {
    $netstat = netstat -ano | Select-String ":$Port"
    foreach ($line in $netstat) {
        if ($line -match "\s+LISTENING\s+(\d+)") {
            return [int]$matches[1]
        }
    }
    return $null
}

function Get-MetadataPid {
    if (Test-Path $MetadataFile) {
        try {
            $md = Get-Content $MetadataFile -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            if ($md.pid) { return [int]$md.pid }
        } catch { }
    }
    return $null
}

function Collect-EnvironmentSnapshot {
    param([string]$Label)

    $whoami = whoami.exe
    $psProc = Get-Process -Id $PID

    $explorerProcs = @(Get-Process explorer -ErrorAction SilentlyContinue)
    $explorerPids = @()
    foreach ($p in $explorerProcs) {
        if ($p.Id -ne $null) { $explorerPids += $p.Id }
    }

    $appProcs = @(Get-Process AgentRecorder.App -ErrorAction SilentlyContinue)
    $appPids = @()
    foreach ($p in $appProcs) {
        if ($p.Id -ne $null) { $appPids += $p.Id }
    }

    $headlessProcs = @(Get-Process AgentRecorder.Headless -ErrorAction SilentlyContinue)
    $headlessPids = @()
    foreach ($p in $headlessProcs) {
        if ($p.Id -ne $null) { $headlessPids += $p.Id }
    }

    $listenerPid = Get-ListeningPid
    $metadataPid = Get-MetadataPid

    $listenerName = $null
    if ($listenerPid) {
        try {
            $listenerProc = Get-Process -Id $listenerPid -ErrorAction SilentlyContinue
            if ($listenerProc) { $listenerName = $listenerProc.ProcessName }
        } catch { }
    }

    return [ordered]@{
        label = $Label
        whoami = $whoami
        powershell_pid = $PID
        powershell_session_id = $psProc.SessionId
        explorer_count = $explorerPids.Count
        explorer_pids = $explorerPids
        agentrecorder_app_count = $appPids.Count
        agentrecorder_app_pids = $appPids
        agentrecorder_headless_count = $headlessPids.Count
        agentrecorder_headless_pids = $headlessPids
        port_37891_listener_pid = $listenerPid
        port_37891_listener_name = $listenerName
        metadata_pid = $metadataPid
    }
}

function Start-App {
    Write-Host ""
    Write-Host "Starting demo app..." -ForegroundColor Cyan
    $startScript = Join-Path $ProjectRoot "scripts\start-demo-app.ps1"
    & $startScript
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[FAIL] Failed to start demo app" -ForegroundColor Red
        return $false
    }
    $script:AppStarted = $true

    Write-Host "[INFO] Waiting for API..." -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $r = Invoke-ApiGet -Path "/api/v1/capabilities"
        if ($r.Success) { break }
    }
    return $true
}

function Stop-App {
    if (-not $script:AppStarted) { return }
    Write-Host ""
    Write-Host "Stopping demo app..." -ForegroundColor Cyan
    $stopScript = Join-Path $ProjectRoot "scripts\stop-demo-app.ps1"
    & $stopScript | Out-Null
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Interactive Desktop Visibility Diagnostic" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

try {
    # Collect environment info (before app start if -StartDemoApp)
    Write-Host "=== Environment Info (before app start) ===" -ForegroundColor Cyan
    $envBefore = Collect-EnvironmentSnapshot -Label "before_start"
    $whoami = $envBefore.whoami
    $psProcSessionId = $envBefore.powershell_session_id

    Write-Host "whoami: $whoami"
    Write-Host "PowerShell PID: $($envBefore.powershell_pid), SessionId: $psProcSessionId"
    Write-Host "Explorer processes: $($envBefore.explorer_count)"
    Write-Host "AgentRecorder.App processes: $($envBefore.agentrecorder_app_count)"
    Write-Host "AgentRecorder.Headless processes: $($envBefore.agentrecorder_headless_count)"
    $listenerPidBefore = $envBefore.port_37891_listener_pid
    $listenerPidDisplay = if ($listenerPidBefore) { "$listenerPidBefore ($($envBefore.port_37891_listener_name))" } else { "none" }
    Write-Host "Port $Port LISTENING PID: $listenerPidDisplay"

    try {
        $queryUser = query user 2>&1
        Write-Host "query user: available"
    } catch {
        Write-Host "query user: not available ($_)"
    }

    Write-Host ""
    Write-Host "=== Foreground Window ===" -ForegroundColor Cyan
    $fg = Get-ForegroundWindowInfo
    Write-Host "HWND: $($fg.hwnd)"
    Write-Host "Title: $($fg.title)"
    Write-Host "Process: $($fg.process_name) (PID=$($fg.process_id))"
    if ($fg.note) { Write-Host "Note: $($fg.note)" }

    Write-Host ""
    Write-Host "=== Win32 EnumWindows (before app start) ===" -ForegroundColor Cyan
    Invoke-Win32Enum
    $win32TitledCount_Before = $script:TitledWindows.Count

    if ($StartDemoApp) {
        if (-not (Start-App)) {
            Write-Host "[FAIL] App start failed" -ForegroundColor Red
            exit 1
        }
    }

    Write-Host ""
    Write-Host "=== API /api/v1/capabilities ===" -ForegroundColor Cyan
    $caps = Invoke-ApiGet -Path "/api/v1/capabilities"
    if ($caps.Success) {
        Write-Host "[OK] 200 OK" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] Not reachable: $($caps.Content)" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "=== API /api/v1/windows (default, exclude minimized/system) ===" -ForegroundColor Cyan
    $winDefault = Invoke-ApiGet -Path "/api/v1/windows?include_minimized=false&include_system_windows=false"
    $apiWindowCount = 0
    if ($winDefault.Success) {
        try {
            $d = $winDefault.Content | ConvertFrom-Json
            $apiWindowCount = if ($d.data -and $d.data.windows) { $d.data.windows.Count } else { 0 }
            Write-Host "[INFO] 200 OK, windows count: $apiWindowCount" -ForegroundColor $(if ($apiWindowCount -gt 0) { "Green" } else { "Yellow" })
        } catch {
            Write-Host "[INFO] 200 OK (could not parse count)" -ForegroundColor Cyan
        }
    } else {
        Write-Host "[FAIL] Not reachable" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "=== API /api/v1/windows (all, include minimized/system) ===" -ForegroundColor Cyan
    $winAll = Invoke-ApiGet -Path "/api/v1/windows?include_minimized=true&include_system_windows=true"
    $apiWindowAllCount = 0
    if ($winAll.Success) {
        try {
            $d = $winAll.Content | ConvertFrom-Json
            $apiWindowAllCount = if ($d.data -and $d.data.windows) { $d.data.windows.Count } else { 0 }
            Write-Host "[INFO] 200 OK, windows count: $apiWindowAllCount" -ForegroundColor Cyan
        } catch {
            Write-Host "[INFO] 200 OK" -ForegroundColor Cyan
        }
    } else {
        Write-Host "[FAIL] Not reachable" -ForegroundColor Red
    }

    # Collect environment after app start (if -StartDemoApp)
    $envAfter = $null
    if ($StartDemoApp) {
        Write-Host ""
        Write-Host "=== Environment Info (after app start) ===" -ForegroundColor Cyan
        $envAfter = Collect-EnvironmentSnapshot -Label "after_start"
        Write-Host "AgentRecorder.App processes: $($envAfter.agentrecorder_app_count) (PIDs: $($envAfter.agentrecorder_app_pids -join ', '))"
        Write-Host "AgentRecorder.Headless processes: $($envAfter.agentrecorder_headless_count) (PIDs: $($envAfter.agentrecorder_headless_pids -join ', '))"
        $listenerPidAfter = $envAfter.port_37891_listener_pid
        $listenerPidDisplayAfter = if ($listenerPidAfter) { "$listenerPidAfter ($($envAfter.port_37891_listener_name))" } else { "none" }
        Write-Host "Port $Port LISTENING PID: $listenerPidDisplayAfter"
    }

    # Classification
    $win32TitledCount = $script:TitledWindows.Count
    $bothZero = ($win32TitledCount -eq 0 -and $apiWindowCount -eq 0)
    $win32ZeroButApiAllHas = ($win32TitledCount -eq 0 -and $apiWindowAllCount -gt 0)
    $apiDefaultZeroButApiAllHas = ($apiWindowCount -eq 0 -and $apiWindowAllCount -gt 0 -and $win32TitledCount -gt 0)

    if ($bothZero -and $apiWindowAllCount -eq 0) {
        $classification = "NON_INTERACTIVE_DESKTOP_OR_INVISIBLE_WINDOWSTATION"
    } elseif ($win32ZeroButApiAllHas) {
        $classification = "NON_INTERACTIVE_OR_HEADLESS_API_HAS_SYSTEM_WINDOWS"
    } elseif ($apiWindowCount -eq 0 -and $win32TitledCount -gt 0) {
        $classification = "API_WINDOWS_EMPTY_BUT_WIN32_HAS_VISIBLE"
    } elseif ($apiWindowCount -gt 0 -and $win32TitledCount -eq 0) {
        $classification = "API_HAS_WINDOWS_BUT_WIN32_NONE"
    } elseif ($apiDefaultZeroButApiAllHas) {
        $classification = "INTERACTIVE_DESKTOP_BUT_NO_DEFAULT_VISIBLE"
    } else {
        $classification = "INTERACTIVE_DESKTOP_VISIBLE"
    }

    Write-Host ""
    Write-Host "=== Classification ===" -ForegroundColor Cyan
    $isDemoReady = ($classification -eq "INTERACTIVE_DESKTOP_VISIBLE")
    Write-Host "Classification: $classification" -ForegroundColor $(if ($isDemoReady) { "Green" } else { "Red" })

    if (-not $isDemoReady) {
        Write-Host ""
        Write-Host "Diagnosis details:" -ForegroundColor Yellow
        Write-Host "  - Win32 titled visible windows: $win32TitledCount" -ForegroundColor Yellow
        Write-Host "  - API default windows (no minimized/system): $apiWindowCount" -ForegroundColor Yellow
        Write-Host "  - API all windows (include minimized/system): $apiWindowAllCount" -ForegroundColor Yellow
        Write-Host "  - Foreground window: $($fg.title) ($($fg.hwnd))" -ForegroundColor Yellow

        if ($classification -eq "NON_INTERACTIVE_DESKTOP_OR_INVISIBLE_WINDOWSTATION") {
            Write-Host ""
            Write-Host "  Both Win32 and API returned 0 windows total." -ForegroundColor Yellow
            Write-Host "  This is a non-interactive or invisible window station." -ForegroundColor Yellow
            Write-Host "  Window recording is NOT possible in this environment." -ForegroundColor Red
        } elseif ($classification -eq "NON_INTERACTIVE_OR_HEADLESS_API_HAS_SYSTEM_WINDOWS") {
            Write-Host ""
            Write-Host "  Win32 found 0 visible windows, but API all count = $apiWindowAllCount." -ForegroundColor Yellow
            Write-Host "  The API may be enumerating system/minimized/invisible windows," -ForegroundColor Yellow
            Write-Host "  but none are actually visible and recordable." -ForegroundColor Yellow
            Write-Host "  This does NOT prove the environment is suitable for demo window recording." -ForegroundColor Red
            Write-Host "  Current assessment: NOT demo-ready." -ForegroundColor Red
        } elseif ($classification -eq "INTERACTIVE_DESKTOP_BUT_NO_DEFAULT_VISIBLE") {
            Write-Host ""
            Write-Host "  Win32 found $win32TitledCount titled visible windows." -ForegroundColor Yellow
            Write-Host "  The desktop IS interactive." -ForegroundColor Yellow
            Write-Host "  However, all visible windows are either minimized or system windows," -ForegroundColor Yellow
            Write-Host "  so the default API filter (exclude minimized/system) returns 0." -ForegroundColor Yellow
            Write-Host "  For demo recording, you need at least one non-minimized, non-system window." -ForegroundColor Yellow
        }

        Write-Host ""
        Write-Host "Suggestions:" -ForegroundColor Yellow
        Write-Host "  1. Run this script from a real interactive user session (not a service/headless agent)." -ForegroundColor Yellow
        Write-Host "  2. Ensure explorer.exe is running in the same session." -ForegroundColor Yellow
        Write-Host "  3. For recording demos, there must be at least one visible, non-minimized, non-system window." -ForegroundColor Yellow
    }

    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }
    $ts = Get-Date -Format "yyyyMMdd-HHmmss"
    $reportPath = Join-Path $OutputDir "desktop-visibility-$ts.json"

    $report = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString("o")
        classification = $classification
        environment_before_start = $envBefore
        environment_after_start = $envAfter
        foreground_window = $fg
        win32_enum = [ordered]@{
            total_windows = $script:AllWindows.Count
            visible_windows = $script:VisibleWindows.Count
            titled_visible_windows = $script:TitledWindows.Count
            top_20_titled = @($script:TitledWindows | Select-Object -First 20)
        }
        api = [ordered]@{
            capabilities_status = $caps.StatusCode
            windows_default_status = $winDefault.StatusCode
            windows_default_count = $apiWindowCount
            windows_all_status = $winAll.StatusCode
            windows_all_count = $apiWindowAllCount
        }
        classification_reason = if ($bothZero -and $apiWindowAllCount -eq 0) {
            "Both Win32 and API returned 0 windows total - non-interactive or invisible window station"
        } elseif ($win32ZeroButApiAllHas) {
            "Win32 has 0 visible windows but API all count > 0 (likely system/minimized windows only, not demo-ready)"
        } elseif ($apiDefaultZeroButApiAllHas) {
            "Win32 has visible windows but API default filter returns 0 (all minimized/system) - desktop is interactive but no default-visible windows"
        } elseif ($apiWindowCount -eq 0 -and $win32TitledCount -gt 0) {
            "Win32 found visible windows but API returned 0 (possible permission or session issue)"
        } elseif ($apiWindowCount -gt 0 -and $win32TitledCount -eq 0) {
            "API returned windows but Win32 found none (possible window station mismatch)"
        } else {
            "Both Win32 and API found visible windows - interactive desktop with visible windows"
        }
    }

    $report | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath -Encoding UTF8

    Write-Host ""
    Write-Host "[INFO] Report written to: $reportPath" -ForegroundColor Cyan

    if ($classification -ne "INTERACTIVE_DESKTOP_VISIBLE") {
        Write-Host ""
        Write-Host "[FAIL] Desktop is not demo-ready: $classification" -ForegroundColor Red
        exit 1
    } else {
        Write-Host ""
        Write-Host "[OK] Interactive desktop visible with recordable windows" -ForegroundColor Green
        exit 0
    }

} catch {
    Write-Host "[FAIL] Diagnostic error: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
} finally {
    if ($script:AppStarted) {
        Stop-App
    }
}
