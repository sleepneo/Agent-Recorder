using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Contains("--api-fixture")) return await ApiFixture.RunAsync(args);

var signal = args[Array.IndexOf(args, "--signal") + 1];
var receipt = args[Array.IndexOf(args, "--receipt") + 1];
Native.IsProcessInJob(Native.GetCurrentProcess(), IntPtr.Zero, out var inJob);
File.WriteAllText(receipt, JsonSerializer.Serialize(new { pid = Environment.ProcessId, in_job = inJob }));
var deadline = DateTime.UtcNow.AddSeconds(40);
while (!File.Exists(signal) && DateTime.UtcNow < deadline) await Task.Delay(40);
File.WriteAllText(receipt + ".retired", "normal");
return 0;

internal static class Native
{
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
