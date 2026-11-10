using Microsoft.Win32.SafeHandles;
using static AgentRecorder.Capture.WindowsDirectoryMetadata;

namespace AgentRecorder.Capture;

internal static partial class WindowsAvStagingInventory
{
    private static IEnumerable<DirectoryEntry> Entries(SafeFileHandle handle, Scan scan)
    {
        var buffer = new byte[1024];
        bool first = true;
        while (true)
        {
            if (scan.Visited == EntryLimit) { scan.Reasons.Add("entry_limit"); yield break; }
            if (scan.DeadlineReached) { scan.Reasons.Add("scan_deadline"); yield break; }
            int status = ReadNext(handle, buffer, out var length);
            if (status == NoMoreFiles || first && status == NoSuchFile) yield break;
            first = false;
            if (status != 0 || !TryReadEntry(buffer, length, out var entry))
            {
                scan.Reasons.Add("enumeration_failed");
                yield break;
            }
            scan.Visited++; // Includes dot entries, nonmatches and rejected entries.
            yield return entry;
        }
    }

}
