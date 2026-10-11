using System.Runtime.InteropServices;
using System.Text;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

// CREATE_NEW before FFmpeg; attributes-only handles permit its ordinary write opens.
// Cleanup reopens these original objects, never their potentially replaced names.
internal sealed class OwnedBundleStaging : IDisposable
{
    private readonly OwnedBundleDirectory _directory;
    private readonly List<SafeFileHandle> _originals = new();
    private readonly Dictionary<string, SafeFileHandle> _current = new(StringComparer.OrdinalIgnoreCase);
    private bool _cleaned;
    internal string CleanupFact { get; private set; } = "not_needed";
    internal OwnedBundleStaging(string path) => _directory = OwnedBundleDirectory.Create(path);
    internal void Prepare(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), _directory.Path, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path) is not ("metadata.json" or "thumbnail.jpg" or "first_frame.png" or "last_frame.png" or "marks.json"))
            throw new IOException("staging_output_binding_invalid");
        if (_originals.Count >= 8) throw new IOException("staging_output_bound_exceeded");
        if (_current.TryGetValue(path, out var previous))
        {
            // A fallback may have removed its empty output. Never erase a replacement.
            Delete(previous);
            previous.Dispose();
        }
        var handle = CreateFileW(@"\\?\" + Path.GetFullPath(path), 0x80, 7, IntPtr.Zero, 1, 0x00200080, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("staging_create_failed:" + Marshal.GetLastWin32Error()); }
        _originals.Add(handle); _current[path] = handle;
    }
    internal void Validate(string path)
    {
        if (!_current.TryGetValue(path, out var handle) || handle.IsClosed || handle.IsInvalid)
            throw new IOException("staging_original_unavailable");
        var api = WindowsFileMetadataApi.Instance; var text = new StringBuilder(32768);
        if (!api.Basic(handle, out var basic, out _) || (basic.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            !api.Standard(handle, out var standard, out _) || standard.DeletePending != 0 || standard.NumberOfLinks != 1 ||
            api.FinalPath(handle, text, text.Capacity, out _) == 0 ||
            !string.Equals(text.ToString(), @"\\?\" + Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("staging_original_binding_changed");
    }
    internal FileStream OpenOriginalForWrite(string path)
    {
        Validate(path);
        var handle = ReOpenFile(_current[path], 0x40000000, 7, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("staging_write_handle_unavailable"); }
        try { return new FileStream(handle, FileAccess.Write); }
        catch { handle.Dispose(); throw; }
    }
    private static PublicationCleanupOutcome Delete(SafeFileHandle handle)
    {
        if (handle.IsClosed || handle.IsInvalid) return PublicationCleanupOutcome.NotNeeded;
        // Already removed by a failed extractor: closing the original finishes deletion.
        if (WindowsFileMetadataApi.Instance.Standard(handle, out var standard, out _) && standard.DeletePending != 0)
            return PublicationCleanupOutcome.NotNeeded;
        using var deletion = ReOpenFile(handle, 0x10000 | 0x80, 7, 0);
        return deletion.IsInvalid ? PublicationCleanupOutcome.RetainedHandleUnavailable : WindowsPublicationFileOperations.NoReplace.DeleteOriginal(deletion);
    }
    internal void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true; var retained = false;
        foreach (var handle in _originals)
        {
            try { retained |= Delete(handle) is PublicationCleanupOutcome.RetainedDeleteFailed or PublicationCleanupOutcome.RetainedHandleUnavailable; }
            catch { retained = true; }
            finally { handle.Dispose(); }
        }
        try { retained |= _directory.DeleteEmptyOriginal() != PublicationCleanupOutcome.MarkedForDeletion; }
        catch { retained = true; }
        CleanupFact = retained ? "staging_retained_unproven_cleanup" : "staging_originals_removed";
    }
    public void Dispose() { Cleanup(); _directory.Dispose(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint sharing, uint flags);
}
