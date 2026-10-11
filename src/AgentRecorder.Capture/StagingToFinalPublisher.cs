using AgentRecorder.Infrastructure;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Result of an atomic staging-to-final publish operation.
/// </summary>
internal sealed class PublishResult
{
    /// <summary>True when the final path now contains the complete, verified file.</summary>
    public bool Success { get; set; }

    /// <summary>Size of the published file in bytes, or zero on failure.</summary>
    public long FinalSizeBytes { get; set; }

    internal PublicationReceipt? Receipt { get; init; }
    internal PublicationCleanupOutcome CleanupOutcome { get; set; }

    /// <summary>Stable failure category when <see cref="Success"/> is false.</summary>
    public string? FailureCategory { get; set; }
}

/// <summary>
/// Minimal file-system seam used by <see cref="StagingToFinalPublisher"/> so that
/// copy, flush, size-check and move failures can be injected deterministically in tests.
/// </summary>
internal interface IFileSystemOperations
{
    void CreateDirectory(string path);
    long GetFileSize(string path);
    Task CopyFileAsync(string sourcePath, string destPath, CancellationToken cancellationToken);
    void FlushFileToDisk(string path);
    void MoveFile(string sourcePath, string destPath);
    void DeleteFile(string path);
}

/// <summary>
/// Production file-system implementation.
/// </summary>
internal sealed class PhysicalFileSystemOperations : IFileSystemOperations
{
    public void CreateDirectory(string path)
    {
        if (!string.IsNullOrEmpty(path))
            Directory.CreateDirectory(path);
    }

    public long GetFileSize(string path)
    {
        return new FileInfo(path).Length;
    }

    public async Task CopyFileAsync(string sourcePath, string destPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        await using var dest = new FileStream(
            destPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await source.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
        dest.Flush(flushToDisk: true);
    }

    public void FlushFileToDisk(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: false);
        stream.Flush(flushToDisk: true);
    }

    public void MoveFile(string sourcePath, string destPath)
    {
        File.Move(sourcePath, destPath, overwrite: true);
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

/// <summary>
/// Atomically arbitrates the final file-system move in a staging-to-final publish.
/// </summary>
internal interface IFileCommitGate
{
    /// <summary>True after <see cref="Close"/> has been called.</summary>
    bool IsClosed { get; }

    /// <summary>
    /// Executes <paramref name="move"/> under the gate. Returns false if the gate
    /// has already been closed; the move is not executed.
    /// </summary>
    bool TryCommit(Action move);

    /// <summary>Permanently closes the gate. Subsequent TryCommit calls return false.</summary>
    void Close();
}

/// <summary>
/// Production implementation of <see cref="IFileCommitGate"/>.
/// Close returns only after any in-flight move has completed.
/// </summary>
internal sealed class FileCommitGate : IFileCommitGate
{
    private readonly object _lock = new();
    private bool _closed;

    public bool IsClosed
    {
        get { lock (_lock) return _closed; }
    }

    public bool TryCommit(Action move)
    {
        lock (_lock)
        {
            if (_closed)
                return false;
            move();
            return true;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
        }
    }
}

/// <summary>
/// Publishes a staging file to a final output path without ever exposing a
/// partially-written file at the final location.
/// </summary>
internal interface IStagingToFinalPublisher
{
    /// <summary>
    /// Atomically copy <paramref name="stagingPath"/> to <paramref name="finalPath"/>.
    /// The final path is updated by a same-directory move after the temporary
    /// copy has been flushed and size-verified.
    /// </summary>
    Task<PublishResult> PublishAsync(
        string stagingPath,
        string finalPath,
        CancellationToken cancellationToken = default,
        IFileCommitGate? commitGate = null);
}

/// <summary>
/// Production implementation of <see cref="IStagingToFinalPublisher"/>.
/// </summary>
// Only an opted-in storage gate supplies accounting for this physical copy.
internal interface IPublicationCopyCapacityGate
{
    Task<IPublicationCopyReservation?> BeginCopyAsync(long bytes, CancellationToken token);
}

internal interface IPublicationCopyReservation : IDisposable
{
    Task WriteAsync(FileStream destination, ReadOnlyMemory<byte> bytes, CancellationToken token);
    Task EnsureCapacityAsync(CancellationToken token);
}

internal sealed class StagingToFinalPublisher : IStagingToFinalPublisher
{
    internal static readonly IStagingToFinalPublisher Instance = new StagingToFinalPublisher();

    private readonly IFileSystemOperations _fileSystem;
    private readonly IWindowsFileMetadataApi _identityApi;
    private readonly IPublicationFileOperations _publicationFiles;
    private static readonly object SigningAuthority = new();
    internal static bool IsSigningAuthority(object value) => ReferenceEquals(value, SigningAuthority);
    private readonly Func<string>? _tempPathGenerator;
    internal Action<FileStream>? OriginalCopyCreatedForTests { get; set; }
    internal Action<FileStream>? BeforeOriginalFlushForTests { get; set; }
    internal Action? BeforeOriginalCommitForTests { get; set; }
    internal Action<FileStream, long>? OriginalCopyProgressForTests { get; set; }

    /// <summary>Production constructor.</summary>
    public StagingToFinalPublisher()
        : this(new PhysicalFileSystemOperations(), null)
    {
    }

    /// <summary>Test constructor allowing file-system failure injection and deterministic temp names.</summary>
    internal StagingToFinalPublisher(IFileSystemOperations fileSystem, Func<string>? tempPathGenerator = null, IWindowsFileMetadataApi? identityApi = null, IPublicationFileOperations? publicationFiles = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _identityApi = identityApi ?? WindowsFileMetadataApi.Instance;
        _publicationFiles = publicationFiles ?? WindowsPublicationFileOperations.Instance;
        _tempPathGenerator = tempPathGenerator;
    }

    public async Task<PublishResult> PublishAsync(
        string stagingPath,
        string finalPath,
        CancellationToken cancellationToken = default,
        IFileCommitGate? commitGate = null)
    {
        long stagingSize;
        try
        {
            stagingSize = _fileSystem.GetFileSize(stagingPath);
            if (stagingSize <= 0)
                return Fail("empty_staging_file");
        }
        catch (FileNotFoundException)
        {
            return Fail("missing_staging_file");
        }
        catch (Exception ex)
        {
            return Fail("staging_access_error: " + ex.GetType().Name);
        }

        string? finalDir = null;
        try
        {
            finalDir = Path.GetDirectoryName(Path.GetFullPath(finalPath));
            if (string.IsNullOrEmpty(finalDir))
                return Fail("final_directory_missing");
            _fileSystem.CreateDirectory(finalDir);
        }
        catch (Exception ex)
        {
            return Fail("final_directory_create_failed: " + ex.GetType().Name);
        }

        string tmpPath = Path.Combine(
            finalDir ?? Path.GetTempPath(),
            Path.GetFileName(finalPath) + ".publish-tmp-" + (_tempPathGenerator?.Invoke() ?? Guid.NewGuid().ToString("N")) + ".mp4");

        bool committed = false;
        bool ownsTemp = false;
        FileStream? originalCopy = null;
        IPublicationCopyReservation? copyReservation = null;
        PublicationHandleObservation? before = null;
        PublishResult? returned = null;
        PublishResult Complete(PublishResult value) => returned = value;
        void CommitOriginal()
        {
            BeforeOriginalCommitForTests?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (originalCopy is not null) _publicationFiles.Rename(originalCopy.SafeFileHandle, finalPath);
            else _fileSystem.MoveFile(tmpPath, finalPath); // unanchored legacy simulation only
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_fileSystem is PhysicalFileSystemOperations)
            {
                // Accounting is scoped to the actual native copy, not a path or API input.
                if (commitGate is IPublicationCopyCapacityGate capacity)
                    copyReservation = await capacity.BeginCopyAsync(stagingSize, cancellationToken).ConfigureAwait(false);
                if (copyReservation is not null)
                    await copyReservation.EnsureCapacityAsync(cancellationToken).ConfigureAwait(false);
                // CREATE_NEW grants ownership; retain this original write handle across commit.
                originalCopy = _publicationFiles.CreateNew(tmpPath);
                ownsTemp = true;
                OriginalCopyCreatedForTests?.Invoke(originalCopy);
                await using var source = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                if (copyReservation is null)
                    await source.CopyToAsync(originalCopy, cancellationToken).ConfigureAwait(false);
                else
                {
                    var buffer = new byte[81920];
                    long copied = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        await copyReservation.WriteAsync(originalCopy, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        copied = checked(copied + read);
                        OriginalCopyProgressForTests?.Invoke(originalCopy, copied);
                    }
                }
            }
            else
            {
                // Legacy injected file-system simulations remain explicitly unanchored.
                if (File.Exists(tmpPath)) return Complete(Fail("temporary_path_exists"));
                ownsTemp = true;
                await _fileSystem.CopyFileAsync(stagingPath, tmpPath, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (originalCopy is not null)
                {
                    BeforeOriginalFlushForTests?.Invoke(originalCopy);
                    originalCopy.Flush(flushToDisk: true);
                }
                else _fileSystem.FlushFileToDisk(tmpPath);
            }
            catch (Exception ex)
            {
                return Complete(Fail("flush_failed: " + ex.GetType().Name));
            }

            long tmpSize;
            try
            {
                tmpSize = originalCopy?.Length ?? _fileSystem.GetFileSize(tmpPath);
                if (originalCopy is not null) before = PublicationHandleObservation.Read(_identityApi, originalCopy.SafeFileHandle, tmpPath);
            }
            catch (Exception ex)
            {
                return Complete(Fail("published_size_read_failed: " + ex.GetType().Name));
            }

            if (tmpSize != stagingSize)
                return Complete(Fail("size_mismatch"));

            // Final gate: the atomic move must not happen if cancellation was
            // requested while the copy/flush/size-check was in flight.
            cancellationToken.ThrowIfCancellationRequested();

            if (copyReservation is not null)
                await copyReservation.EnsureCapacityAsync(cancellationToken).ConfigureAwait(false);

            if (commitGate != null)
            {
                committed = commitGate.TryCommit(CommitOriginal);
                if (!committed)
                    return Complete(Fail("commit_closed"));
            }
            else
            {
                CommitOriginal();
                committed = true;
            }

            // Never reopen the final name to manufacture a publication identity.
            var after = originalCopy is null ? null : PublicationHandleObservation.Read(_identityApi, originalCopy.SafeFileHandle, Path.GetFullPath(finalPath));
            PublicationReceipt? receipt = null;
            if (before is not null && after is not null && before.Snapshot == after.Snapshot && after.Snapshot.Length == stagingSize)
                receipt = PublicationReceipt.Sign(SigningAuthority, new(after.Path, after.Snapshot.Length, after.Snapshot.Identity!));
            var finalSize = originalCopy is not null ? tmpSize : stagingSize;
            if (originalCopy is null)
                try { finalSize = _fileSystem.GetFileSize(finalPath); } catch { }
            return Complete(new PublishResult { Success = true, FinalSizeBytes = finalSize, Receipt = receipt });
        }
        catch (OperationCanceledException)
        {
            return Complete(Fail("cancelled"));
        }
        catch (Exception ex)
        {
            return Complete(Fail("publish_failed: " + ex.GetType().Name));
        }
        finally
        {
            try
            {
                if (originalCopy is not null)
                {
                    try
                    {
                        if (!committed && ownsTemp)
                        {
                            PublicationCleanupOutcome cleanup;
                            try { cleanup = _publicationFiles.DeleteOriginal(originalCopy.SafeFileHandle); }
                            catch (ObjectDisposedException) { cleanup = PublicationCleanupOutcome.RetainedHandleUnavailable; }
                            catch { cleanup = PublicationCleanupOutcome.RetainedDeleteFailed; }
                            if (returned is not null) returned.CleanupOutcome = cleanup;
                        }
                    }
                    finally { originalCopy.Dispose(); }
                    // Never delete by the former name, including invalid-handle cleanup.
                }
                else if (!committed && ownsTemp)
                {
                    // Legacy injected simulations have no native object/receipt.
                    try { _fileSystem.DeleteFile(tmpPath); } catch { }
                }
            }
            finally { copyReservation?.Dispose(); }
        }
    }

    private static PublishResult Fail(string category)
    {
        return new PublishResult
        {
            Success = false,
            FailureCategory = category
        };
    }
}
