using System.Runtime.InteropServices;
using System.Text;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Capture;

// A bounded namespace transaction: one new directory and five links, no ACL changes.
// Original copies remain outside it. Aliases are opened and held while still private,
// verified against the continuously held created objects, then become visible together.
internal sealed class BundleDirectoryTransaction : IDisposable
{
    private readonly SafeFileHandle _transaction;
    private readonly SafeFileHandle _directory;
    private readonly List<SafeFileHandle> _aliases = new(5);
    private readonly List<SafeFileHandle> _sources = new(5);
    private readonly List<string> _sourcePaths = new(5);
    private readonly List<(uint Volume, ulong File)> _keys = new(5);
    private bool _committed;
    internal BundleDirectoryTransaction(string path)
    {
        _transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 3000, "AgentRecorder five owned Bundle links");
        if (_transaction.IsInvalid) { var error = Marshal.GetLastWin32Error(); _transaction.Dispose(); throw Failure("transaction_unavailable", error); }
        SafeFileHandle? directory = null;
        try
        {
            if (!CreateDirectoryTransactedW(null, Extended(path), IntPtr.Zero, _transaction)) throw Failure("namespace_create_failed");
            directory = CreateFileTransactedW(Extended(path), 0x10000 | 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero, _transaction, IntPtr.Zero, IntPtr.Zero);
            if (directory.IsInvalid) throw Failure("directory_hold_failed");
            _directory = directory;
        }
        catch
        {
            try { if (!RollbackTransaction(_transaction)) throw Failure("transaction_rollback_failed"); }
            finally { directory?.Dispose(); _transaction.Dispose(); }
            throw;
        }
    }
    internal void Add(string source, string target, SafeFileHandle createdOriginal, long length)
    {
        if (_aliases.Count >= 5) throw new IOException("bundle_alias_bound_exceeded");
        var originalKey = Key(createdOriginal);
        // TxF only works on NTFS; legacy NTFS file indices are unique while held.
        // This structural comparison does not mint optional publication FILE_ID_INFO.
        if (!CreateHardLinkTransactedW(Extended(target), Extended(source), IntPtr.Zero, _transaction)) throw Failure("original_link_failed");
        // NTFS sharing is per name. Hold BOTH known names in the transaction;
        // an outside DELETE handle would conflict with TxF enlistment.
        var sourceGuard = CreateFileTransactedW(Extended(source), 0x10000 | 0x80, 1, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero, _transaction, IntPtr.Zero, IntPtr.Zero);
        if (sourceGuard.IsInvalid) { var error = Marshal.GetLastWin32Error(); sourceGuard.Dispose(); throw Failure("source_hold_failed", error); }
        _sources.Add(sourceGuard); _sourcePaths.Add(source);
        if (Key(sourceGuard) != originalKey) throw new IOException("bundle_source_original_mismatch");
        Validate(sourceGuard, source, length);
        var alias = CreateFileTransactedW(Extended(target), 0x10000 | 0x80, 1, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero, _transaction, IntPtr.Zero, IntPtr.Zero);
        if (alias.IsInvalid) { var error = Marshal.GetLastWin32Error(); alias.Dispose(); throw Failure("alias_hold_failed", error); }
        _aliases.Add(alias);
        if (Key(alias) != originalKey) throw new IOException("bundle_alias_original_mismatch");
        _keys.Add(originalKey);
        Validate(alias, target, length);
    }
    internal void ValidateAll(IReadOnlyList<string> paths, RecordingBundleContentsEvidence contents)
    {
        if (_aliases.Count != 5 || _sources.Count != 5 || _keys.Count != 5 || paths.Count != 5) throw new IOException("bundle_alias_set_incomplete");
        for (var i = 0; i < 5; i++)
        {
            ValidateHeld(_sources[i], _sourcePaths[i], contents.Contents[i].SizeBytes, _keys[i]);
            ValidateHeld(_aliases[i], paths[i], contents.Contents[i].SizeBytes, _keys[i]);
        }
    }
    private void ValidateHeld(SafeFileHandle held, string path, long length, (uint Volume, ulong File) key)
    {
        if (!_committed) Validate(held, path, length);
        else
        {
            // TxF metadata calls stop working after commit. ReOpenFile derives
            // a metadata view from the STILL held name, never from its path.
            // The transaction handle keeps denying mutation throughout receipt.
            using var view = ReOpenFile(held, 0x80, 7, 0);
            if (view.IsInvalid) throw Failure("alias_view_unavailable");
            if (Key(view) != key) throw new IOException("bundle_alias_original_mismatch");
            Validate(view, path, length);
        }
    }
    internal void Commit()
    {
        if (!CommitTransaction(_transaction)) throw Failure("transaction_commit_failed");
        _committed = true; // Complete directory and its five held links become visible together.
    }
    private static (uint Volume, ulong File) Key(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) || (info.IndexHigh == 0 && info.IndexLow == 0)) throw Failure("original_key_unavailable");
        return (info.VolumeSerial, ((ulong)info.IndexHigh << 32) | info.IndexLow);
    }
    private static void Validate(SafeFileHandle handle, string path, long length)
    {
        var api = WindowsFileMetadataApi.Instance; var text = new StringBuilder(32768);
        if (!api.Basic(handle, out var basic, out var basicError)) throw Failure("alias_basic_unavailable", basicError);
        if (!WindowsReadOnlyFileMetadataProbe.ValidAttributes(basic.Attributes) || (basic.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("bundle_alias_attributes_changed");
        if (!api.Standard(handle, out var info, out var standardError)) throw Failure("alias_standard_unavailable", standardError);
        if (info.EndOfFile != length || info.NumberOfLinks != 2 || info.DeletePending != 0 || info.Directory != 0)
            throw new IOException("bundle_alias_structure_changed");
        var count = api.FinalPath(handle, text, text.Capacity, out var pathError);
        if (count == 0) throw Failure("alias_path_unavailable", pathError);
        if (count >= text.Capacity || !string.Equals(text.ToString(), Extended(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("bundle_alias_path_changed");
    }
    public void Dispose()
    {
        try { if (!_committed && !RollbackTransaction(_transaction)) throw Failure("transaction_rollback_failed"); }
        finally
        {
            foreach (var alias in _aliases) alias.Dispose();
            foreach (var source in _sources) source.Dispose();
            _directory.Dispose(); _transaction.Dispose();
        }
    }
    private static string Extended(string path) => @"\\?\" + Path.GetFullPath(path);
    private static IOException Failure(string fact, int? error = null) => new("bundle_" + fact + ":" + (error ?? Marshal.GetLastWin32Error()));
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle ReOpenFile(SafeFileHandle handle, uint access, uint sharing, uint flags);
    [DllImport("KtmW32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateTransaction(IntPtr attributes, IntPtr guid, uint options, uint isolation, uint flags, uint timeout, string description);
    [DllImport("KtmW32.dll", SetLastError = true)] private static extern bool CommitTransaction(SafeFileHandle transaction);
    [DllImport("KtmW32.dll", SetLastError = true)] private static extern bool RollbackTransaction(SafeFileHandle transaction);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateDirectoryTransactedW(string? template, string path, IntPtr security, SafeFileHandle transaction);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkTransactedW(string path, string existing, IntPtr security, SafeFileHandle transaction);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileTransactedW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template, SafeFileHandle transaction, IntPtr miniVersion, IntPtr extended);
}
