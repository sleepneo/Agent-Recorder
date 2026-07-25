using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for <see cref="StagingToFinalPublisher"/> using both the production
/// file-system implementation and an injectable failure seam.
/// </summary>
public sealed class StagingToFinalPublisherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _finalDir;

    public StagingToFinalPublisherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTests", $"publisher-{Guid.NewGuid():N}");
        _finalDir = Path.Combine(_tempDir, "final");
        Directory.CreateDirectory(_finalDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex)
        {
            Assert.Fail($"Failed to clean up publisher temp dir {_tempDir}: {ex.Message}");
        }
    }

    private string StagingPath(string name) => Path.Combine(_tempDir, name);
    private string FinalPath(string name) => Path.Combine(_finalDir, name);

    private static void WriteFile(string path, byte[] content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, content);
    }

    private static byte[] ReadFile(string path) => File.ReadAllBytes(path);

    [Fact]
    public async Task PublishAsync_MissingStaging_ReturnsMissingStagingFile()
    {
        var publisher = new StagingToFinalPublisher();
        var result = await publisher.PublishAsync(StagingPath("missing.mp4"), FinalPath("out.mp4"));

        Assert.False(result.Success);
        Assert.Equal("missing_staging_file", result.FailureCategory);
    }

    [Fact]
    public async Task PublishAsync_EmptyStaging_ReturnsEmptyStagingFile()
    {
        WriteFile(StagingPath("empty.mp4"), Array.Empty<byte>());
        var publisher = new StagingToFinalPublisher();
        var result = await publisher.PublishAsync(StagingPath("empty.mp4"), FinalPath("out.mp4"));

        Assert.False(result.Success);
        Assert.Equal("empty_staging_file", result.FailureCategory);
    }

    [Fact]
    public async Task PublishAsync_InvalidFinalDirectory_ReturnsDirectoryCreateFailed()
    {
        byte[] content = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), content);

        var fs = new FailureInjectingFileSystem();
        fs.OnCreateDirectory = _ => throw new IOException("bad dir");
        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");
        var result = await publisher.PublishAsync(StagingPath("valid.mp4"), @"X:\\bad\\out.mp4");

        Assert.False(result.Success);
        Assert.Contains("final_directory_create_failed", result.FailureCategory);
    }

    [Fact]
    public async Task PublishAsync_CopyFailure_DeletesTmp_PreservesExistingFinal()
    {
        byte[] existing = { 9, 8, 7 };
        WriteFile(FinalPath("out.mp4"), existing);

        byte[] staging = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), staging);

        var fs = new FailureInjectingFileSystem();
        fs.OnCopyFile = (_, _, _) => throw new IOException("copy failed");
        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");
        var result = await publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"));

        Assert.False(result.Success);
        Assert.Contains("publish_failed", result.FailureCategory);
        Assert.False(File.Exists(FinalPath("out.publish-tmp-tmp1.mp4")));
        Assert.Equal(existing, ReadFile(FinalPath("out.mp4")));
    }

    [Fact]
    public async Task PublishAsync_SizeReadFailure_DeletesTmp_PreservesExistingFinal()
    {
        byte[] existing = { 9, 8, 7 };
        WriteFile(FinalPath("out.mp4"), existing);

        byte[] staging = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), staging);

        var fs = new FailureInjectingFileSystem();
        int getSizeCalls = 0;
        fs.OnGetFileSize = path =>
        {
            getSizeCalls++;
            if (path.Contains("publish-tmp"))
                throw new IOException("size read failed");
            return new FileInfo(path).Length;
        };
        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");
        var result = await publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"));

        Assert.False(result.Success);
        Assert.Contains("published_size_read_failed", result.FailureCategory);
        Assert.False(File.Exists(FinalPath("out.publish-tmp-tmp1.mp4")));
        Assert.Equal(existing, ReadFile(FinalPath("out.mp4")));
    }

    [Fact]
    public async Task PublishAsync_SizeMismatch_DeletesTmp_PreservesExistingFinal()
    {
        byte[] existing = { 9, 8, 7 };
        WriteFile(FinalPath("out.mp4"), existing);

        byte[] staging = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), staging);

        var fs = new FailureInjectingFileSystem();
        fs.OnGetFileSize = path =>
        {
            if (path.Contains("publish-tmp"))
                return staging.Length - 1; // simulate truncated copy
            return new FileInfo(path).Length;
        };
        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");
        var result = await publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"));

        Assert.False(result.Success);
        Assert.Equal("size_mismatch", result.FailureCategory);
        Assert.False(File.Exists(FinalPath("out.publish-tmp-tmp1.mp4")));
        Assert.Equal(existing, ReadFile(FinalPath("out.mp4")));
    }

    [Fact]
    public async Task PublishAsync_MoveFailure_DeletesTmp_PreservesExistingFinal()
    {
        byte[] existing = { 9, 8, 7 };
        WriteFile(FinalPath("out.mp4"), existing);

        byte[] staging = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), staging);

        var fs = new FailureInjectingFileSystem();
        fs.OnMoveFile = (_, _) => throw new IOException("move failed");
        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");
        var result = await publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"));

        Assert.False(result.Success);
        Assert.Contains("publish_failed", result.FailureCategory);
        Assert.False(File.Exists(FinalPath("out.publish-tmp-tmp1.mp4")));
        Assert.Equal(existing, ReadFile(FinalPath("out.mp4")));
    }

    [Fact]
    public async Task PublishAsync_Success_ReplacesFinal_BytesMatchStaging()
    {
        byte[] existing = { 9, 8, 7 };
        WriteFile(FinalPath("out.mp4"), existing);

        byte[] staging = new byte[4096];
        new Random(42).NextBytes(staging);
        WriteFile(StagingPath("valid.mp4"), staging);

        var publisher = new StagingToFinalPublisher();
        var result = await publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"));

        Assert.True(result.Success);
        Assert.Equal(staging.Length, result.FinalSizeBytes);
        Assert.Equal(staging, ReadFile(FinalPath("out.mp4")));
        Assert.False(File.Exists(FinalPath("out.publish-tmp")));
        Assert.DoesNotContain(Directory.GetFiles(_finalDir), f => Path.GetFileName(f).Contains(".publish-tmp-"));
    }

    [Fact]
    public async Task PublishAsync_CancelledDuringCopy_ReturnsCancelled()
    {
        byte[] staging = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), staging);

        var fs = new FailureInjectingFileSystem();
        fs.OnCopyFile = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
        };
        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"), cts.Token);

        Assert.False(result.Success);
        Assert.Equal("cancelled", result.FailureCategory);
    }

    [Fact]
    public async Task PublishAsync_ProductionFlush_Durable()
    {
        byte[] staging = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), staging);

        bool flushCalled = false;
        var fs = new FailureInjectingFileSystem();
        fs.OnFlushFileToDisk = path =>
        {
            flushCalled = true;
            Assert.Contains(".publish-tmp-", path);
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            fileStream.Flush(flushToDisk: true);
        };
        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");
        var result = await publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"));

        Assert.True(result.Success);
        Assert.True(flushCalled);
        Assert.Equal(staging, ReadFile(FinalPath("out.mp4")));
    }

    [Fact]
    public async Task PublishAsync_ProductionFlushFailure_DeletesTmp_PreservesExistingFinal()
    {
        byte[] existing = { 9, 8, 7 };
        WriteFile(FinalPath("out.mp4"), existing);

        byte[] staging = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), staging);

        var fs = new FailureInjectingFileSystem();
        fs.OnFlushFileToDisk = path =>
        {
            Assert.Contains(".publish-tmp-", path);
            throw new IOException("flush failed");
        };
        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");
        var result = await publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"));

        Assert.False(result.Success);
        Assert.Contains("flush_failed", result.FailureCategory);
        Assert.False(File.Exists(FinalPath("out.publish-tmp-tmp1.mp4")));
        Assert.Equal(existing, ReadFile(FinalPath("out.mp4")));
    }

    [Fact]
    public async Task PublishAsync_CancelledBeforeAtomicMove_DeletesTmp_PreservesExistingFinal()
    {
        byte[] existing = { 9, 8, 7 };
        WriteFile(FinalPath("out.mp4"), existing);

        byte[] staging = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), staging);

        using var cts = new CancellationTokenSource();
        bool cancelled = false;
        var fs = new FailureInjectingFileSystem();
        fs.OnGetFileSize = path =>
        {
            // The second size read is on the published tmp file after copy/flush.
            // Cancelling here exercises the cancellation check immediately before
            // the atomic move.
            if (path.Contains(".publish-tmp-") && !cancelled)
            {
                cancelled = true;
                cts.Cancel();
            }
            return new FileInfo(path).Length;
        };

        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");
        var result = await publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"), cts.Token);

        Assert.False(result.Success);
        Assert.Equal("cancelled", result.FailureCategory);
        Assert.False(File.Exists(FinalPath("out.publish-tmp-tmp1.mp4")));
        Assert.Equal(existing, ReadFile(FinalPath("out.mp4")));
    }

    [Fact]
    public async Task PublishAsync_CommitGateClosedBeforeMove_ReturnsCommitClosed_DeletesTmp_PreservesExistingFinal()
    {
        byte[] existing = { 9, 8, 7 };
        WriteFile(FinalPath("out.mp4"), existing);

        byte[] staging = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), staging);

        var gate = new FileCommitGate();
        var moveBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool waited = false;

        var fs = new FailureInjectingFileSystem();
        fs.OnGetFileSize = path =>
        {
            // The second size read is on the published tmp file after copy/flush.
            // Pause here so Dispose has a chance to close the gate before the
            // atomic move is attempted.
            if (path.Contains(".publish-tmp-") && !waited)
            {
                waited = true;
                moveBarrier.Task.Wait(TimeSpan.FromSeconds(10));
            }
            return new FileInfo(path).Length;
        };

        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");

        var publishTask = publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"), default, gate);

        // Close the gate before releasing the move barrier.
        gate.Close();
        moveBarrier.TrySetResult();

        var result = await publishTask;

        Assert.False(result.Success);
        Assert.Equal("commit_closed", result.FailureCategory);
        Assert.False(File.Exists(FinalPath("out.publish-tmp-tmp1.mp4")));
        Assert.Equal(existing, ReadFile(FinalPath("out.mp4")));
    }

    [Fact]
    public async Task PublishAsync_CommitGateWaitsForInFlightMove_MoveCompletesBeforeCloseReturns()
    {
        byte[] staging = { 1, 2, 3, 4, 5 };
        WriteFile(StagingPath("valid.mp4"), staging);

        var gate = new FileCommitGate();
        var moveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moveBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var fs = new FailureInjectingFileSystem();
        fs.OnMoveFile = (source, dest) =>
        {
            moveStarted.TrySetResult();
            moveBarrier.Task.Wait(TimeSpan.FromSeconds(10));
            File.Move(source, dest, overwrite: true);
        };

        var publisher = new StagingToFinalPublisher(fs, () => "tmp1");
        var publishTask = publisher.PublishAsync(StagingPath("valid.mp4"), FinalPath("out.mp4"), default, gate);

        await moveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Close must block until the in-flight move completes.
        var closeTask = Task.Run(() => gate.Close());
        await Task.Delay(50);
        Assert.False(closeTask.IsCompleted, "Close should wait for the in-flight move.");

        moveBarrier.TrySetResult();
        await closeTask.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await publishTask;

        Assert.True(result.Success);
        Assert.Equal(staging, ReadFile(FinalPath("out.mp4")));
    }

    private sealed class FailureInjectingFileSystem : IFileSystemOperations
    {
        public Action<string>? OnCreateDirectory { get; set; }
        public Func<string, long>? OnGetFileSize { get; set; }
        public Func<string, string, CancellationToken, Task>? OnCopyFile { get; set; }
        public Action<string>? OnFlushFileToDisk { get; set; }
        public Action<string, string>? OnMoveFile { get; set; }
        public Action<string>? OnDeleteFile { get; set; }

        private readonly PhysicalFileSystemOperations _real = new();

        public void CreateDirectory(string path)
        {
            if (OnCreateDirectory != null)
                OnCreateDirectory(path);
            else
                _real.CreateDirectory(path);
        }

        public long GetFileSize(string path)
        {
            if (OnGetFileSize != null)
                return OnGetFileSize(path);
            return _real.GetFileSize(path);
        }

        public async Task CopyFileAsync(string sourcePath, string destPath, CancellationToken cancellationToken)
        {
            if (OnCopyFile != null)
            {
                await OnCopyFile(sourcePath, destPath, cancellationToken);
                return;
            }
            await _real.CopyFileAsync(sourcePath, destPath, cancellationToken);
        }

        public void FlushFileToDisk(string path)
        {
            if (OnFlushFileToDisk != null)
                OnFlushFileToDisk(path);
            else
                _real.FlushFileToDisk(path);
        }

        public void MoveFile(string sourcePath, string destPath)
        {
            if (OnMoveFile != null)
                OnMoveFile(sourcePath, destPath);
            else
                _real.MoveFile(sourcePath, destPath);
        }

        public void DeleteFile(string path)
        {
            if (OnDeleteFile != null)
                OnDeleteFile(path);
            else
                _real.DeleteFile(path);
        }
    }
}
