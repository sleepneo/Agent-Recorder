using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentRecorder.ProcessLauncher;

internal static class Program
{
    private const int DETACHED_PROCESS = 0x00000008;
    private const int CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const int CREATE_NO_WINDOW = 0x08000000;
    private const int CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    private const int ModeDetached = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB;
    private const int ModeGui = CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB;
    private const int ModeGuiNoBreakaway = CREATE_NEW_PROCESS_GROUP;
    private const int ModeNormal = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        [In] ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        string? exePath = null;
        string? workDir = null;
        string mode = "detached";
        int creationFlags = ModeDetached;
        var cmdArgs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--exe":
                    exePath = GetArgValue(args, ref i, "--exe");
                    break;
                case "--work-dir":
                    workDir = GetArgValue(args, ref i, "--work-dir");
                    break;
                case "--mode":
                    mode = GetArgValue(args, ref i, "--mode").ToLowerInvariant();
                    break;
                case "--no-breakaway":
                    creationFlags &= ~CREATE_BREAKAWAY_FROM_JOB;
                    break;
                case "--":
                    for (int j = i + 1; j < args.Length; j++)
                        cmdArgs.Add(args[j]);
                    i = args.Length;
                    break;
                default:
                    Console.Error.WriteLine($"[ERROR] Unknown argument: {args[i]}");
                    PrintHelp();
                    return 1;
            }
        }

        switch (mode)
        {
            case "detached":
                creationFlags = ModeDetached;
                break;
            case "gui":
                creationFlags = ModeGui;
                break;
            case "gui-no-breakaway":
                creationFlags = ModeGuiNoBreakaway;
                break;
            case "normal":
                creationFlags = ModeNormal;
                break;
            default:
                Console.Error.WriteLine($"[ERROR] Unknown mode: {mode}. Valid: detached, gui, gui-no-breakaway, normal");
                return 1;
        }

        if (args.Contains("--no-breakaway"))
        {
            creationFlags &= ~CREATE_BREAKAWAY_FROM_JOB;
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            Console.Error.WriteLine("[ERROR] --exe is required");
            return 1;
        }

        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"[ERROR] Executable not found: {exePath}");
            return 2;
        }

        var commandLine = new StringBuilder();
        AppendArgument(commandLine, exePath);
        foreach (var a in cmdArgs)
            AppendArgument(commandLine, a);

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var pi = new PROCESS_INFORMATION();

        bool success = CreateProcessW(
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            creationFlags,
            IntPtr.Zero,
            workDir,
            ref si,
            out pi);

        if (!success)
        {
            int error = Marshal.GetLastWin32Error();
            var ex = new Win32Exception(error);

            if ((creationFlags & CREATE_BREAKAWAY_FROM_JOB) != 0 && error == 5)
            {
                Console.Error.WriteLine($"[ERROR] CreateProcessW failed with ERROR_ACCESS_DENIED (5). " +
                    $"CREATE_BREAKAWAY_FROM_JOB may not be permitted by current job object. " +
                    $"Try --no-breakaway for a fallback attempt.");
            }
            else
            {
                Console.Error.WriteLine($"[ERROR] CreateProcessW failed: Win32 error {error} - {ex.Message}");
            }

            Console.Error.WriteLine($"[INFO] creationFlags=0x{creationFlags:X8}");
            Console.Error.WriteLine($"[INFO] exe={exePath}");
            return 3;
        }

        Console.WriteLine($"PID={pi.dwProcessId}");
        Console.WriteLine($"HANDLE=0x{pi.hProcess.ToInt64():X16}");
        Console.WriteLine($"FLAGS=0x{creationFlags:X8}");

        try { CloseHandle(pi.hProcess); } catch { }
        try { CloseHandle(pi.hThread); } catch { }

        return 0;
    }

    private static string GetArgValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{name} requires a value.");
        return args[++index];
    }

    private static void AppendArgument(StringBuilder sb, string arg)
    {
        if (sb.Length > 0) sb.Append(' ');
        if (arg.Length == 0 || arg.Contains(' ') || arg.Contains('"') || arg.Contains('\t'))
        {
            sb.Append('"');
            foreach (char c in arg)
            {
                if (c == '"') sb.Append("\\\"");
                else sb.Append(c);
            }
            sb.Append('"');
        }
        else
        {
            sb.Append(arg);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("AgentRecorder.ProcessLauncher");
        Console.WriteLine("Launches a process using Win32 CreateProcessW with configurable creation flags.");
        Console.WriteLine();
        Console.WriteLine("Usage: ProcessLauncher --exe <path> [options] -- [args...]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --exe <path>          Path to the executable to launch.");
        Console.WriteLine("  --work-dir <dir>      Working directory for the new process.");
        Console.WriteLine("  --mode <mode>         Launch mode: detached (default), gui, gui-no-breakaway, normal");
        Console.WriteLine("                          detached: DETACHED_PROCESS | NEW_PROCESS_GROUP | NO_WINDOW | BREAKAWAY");
        Console.WriteLine("                          gui:      NEW_PROCESS_GROUP | BREAKAWAY (for GUI/tray apps)");
        Console.WriteLine("                          gui-no-breakaway: NEW_PROCESS_GROUP only");
        Console.WriteLine("                          normal:   no special flags");
        Console.WriteLine("  --no-breakaway        Remove CREATE_BREAKAWAY_FROM_JOB from current mode.");
        Console.WriteLine("  -- [args...]          Arguments to pass to the target executable.");
        Console.WriteLine("  --help, -h            Show this help.");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0=success, 1=bad args, 2=exe not found, 3=CreateProcessW failed");
    }
}
