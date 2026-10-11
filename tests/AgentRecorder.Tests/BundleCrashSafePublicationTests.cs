using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class BundleCrashSafePublicationTests(ITestOutputHelper output)
{
    private static readonly Lazy<Task<string>> Child = new(BuildChild);

    [Theory]
    [InlineData("create", false)] [InlineData("copy", false)] [InlineData("flush", false)]
    [InlineData("before_commit", false)] [InlineData("objects_held", false)]
    [InlineData("namespace_prepared", false)] [InlineData("after_commit", false)]
    [InlineData("create", true)] [InlineData("copy", true)] [InlineData("flush", true)]
    [InlineData("before_commit", true)] [InlineData("objects_held", true)]
    [InlineData("namespace_prepared", true)] [InlineData("after_commit", true)]
    public async Task AbruptChildExitLeavesOriginalPermissionsAndOrdinaryOperations(string stage, bool denyRead)
        => await RunChild(stage, denyRead);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NormalChildCompletionLeavesExactPermissionsAndFiveNativeMatches(bool denyRead)
        => await RunChild("normal", denyRead);

    private async Task RunChild(string stage, bool denyRead)
    {
        Assert.False(Elevated(), "Regression must run under an ordinary non-elevated token.");
        var root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task337r", "crash-" + Guid.NewGuid().ToString("N")));
        var outputs = Path.Combine(root, "outputs"); Directory.CreateDirectory(outputs);
        if (denyRead)
        {
            var acl = new DirectoryInfo(outputs).GetAccessControl(AccessControlSections.Access);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly, AccessControlType.Deny));
            new DirectoryInfo(outputs).SetAccessControl(acl); // Only this new synthetic fixture.
        }
        var parentAcl = Security(outputs);
        var psi = new ProcessStartInfo(await Child.Value) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(root); psi.ArgumentList.Add(stage);
        using var child = Process.Start(psi)!; var pid = child.Id; var started = DateTime.UtcNow;
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        ProbeRecord? record = null; DateTime? killed = null; var assertionsPassed = false;
        var releases = new List<object>();
        try
        {
            if (stage != "normal")
            {
                var barrier = Path.Combine(root, "barrier.json"); var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(barrier) && !child.HasExited && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.True(File.Exists(barrier), "Child missed barrier: " + (child.HasExited ? await stderr : "deadline"));
                record = JsonSerializer.Deserialize<ProbeRecord>(await File.ReadAllTextAsync(barrier))!;
                Assert.Equal(pid, record.Pid); Assert.Equal(stage, record.Stage); Assert.False(record.Elevated);
                Assert.Equal(stage == "after_commit", Directory.Exists(record.Final));
                killed = DateTime.UtcNow; child.Kill(); // Only the process this test started.
            }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(child.HasExited);
            if (stage == "normal")
            {
                Assert.True(child.ExitCode == 0, await stderr);
                record = JsonSerializer.Deserialize<ProbeRecord>(await File.ReadAllTextAsync(Path.Combine(root, "complete.json")))!;
                Assert.True(record.Receipt); Assert.Equal(5, record.NativeMatches);
            }
            else Assert.NotEqual(0, child.ExitCode);
            Assert.NotNull(record); Assert.Equal(pid, record.Pid); Assert.False(record.Elevated);
            Assert.Equal(parentAcl, Security(outputs));
            var committed = stage is "after_commit" or "normal";
            Assert.Equal(committed, Directory.Exists(record.Final));
            if (committed) Assert.Equal(5, record.Files.Length);
            foreach (var file in record.Files)
            {
                var expected = Convert.FromBase64String(file.Dacl);
                if (stage != "normal")
                {
                    Assert.True(File.Exists(file.Path)); releases.Add(await ConfirmHandleRelease(file.Path));
                    Assert.Equal(expected, Security(file.Path));
                    Operate(file.Path, denyRead);
                }
                else Assert.False(File.Exists(file.Path)); // Normal original-handle cleanup.
                if (committed)
                {
                    var alias = Path.Combine(record.Final, file.Name);
                    Assert.True(File.Exists(alias)); releases.Add(await ConfirmHandleRelease(alias));
                    Assert.Equal(expected, Security(alias)); Operate(alias, denyRead);
                }
            }
            if (stage != "normal")
            {
                Assert.True(Directory.Exists(record.Private));
                Assert.Equal(Convert.FromBase64String(record.PrivateDacl), Security(record.Private));
                RenameAndDeleteDirectory(record.Private);
            }
            else Assert.False(Directory.Exists(record.Private));
            if (committed)
            {
                Assert.Equal(Convert.FromBase64String(record.FinalDacl), Security(record.Final));
                RenameAndDeleteDirectory(record.Final);
            }
            assertionsPassed = true;
            output.WriteLine($"stage={stage} deny_read={denyRead} pid={pid} exited={child.HasExited} exit={child.ExitCode} DACL_equal=true write/rename/delete=true native_matches={record.NativeMatches} root={root}");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            await File.WriteAllTextAsync(Path.Combine(root, "process-evidence.json"), JsonSerializer.Serialize(new
            {
                Stage = stage, DenyRead = denyRead, Pid = pid, StartedUtc = started, KillRequestedUtc = killed,
                ExitedUtc = DateTime.UtcNow, ProcessExited = child.HasExited, ExitCode = child.ExitCode,
                AssertionsPassed = assertionsPassed, HandleReleaseProbes = releases, Stdout = await stdout, Stderr = await stderr, Manifest = record
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static async Task<object> ConfirmHandleRelease(string path)
    {
        // A signalled Windows process can precede deferred kernel file cleanup.
        // Wait only for sharing violation, never for denied permissions or repair.
        var watch = Stopwatch.StartNew(); var sharingRetries = 0;
        while (true)
        {
            try
            {
                using var view = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                return new { Path = path, Released = true, ElapsedMs = watch.ElapsedMilliseconds, SharingRetries = sharingRetries };
            }
            catch (IOException error) when ((error.HResult & 0xffff) == 32 && watch.Elapsed < TimeSpan.FromSeconds(3))
            { sharingRetries++; await Task.Delay(20); }
        }
    }
    private static void Operate(string path, bool denyRead)
    {
        if (denyRead) Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(path));
        else File.ReadAllBytes(path);
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read)) { stream.WriteByte(42); stream.Flush(true); }
        if (!denyRead) Assert.Equal(42, File.ReadAllBytes(path)[0]);
        var renamed = path + ".ordinary-renamed"; File.Move(path, renamed); File.Delete(renamed); Assert.False(File.Exists(renamed));
    }
    private static void RenameAndDeleteDirectory(string path)
    { var renamed = path + ".ordinary-renamed"; Directory.Move(path, renamed); Directory.Delete(renamed); Assert.False(Directory.Exists(renamed)); }

    [Theory] [InlineData("objects_held")] [InlineData("namespace_prepared")]
    public async Task CancellationAfterHandleHandoffRollsBackWithoutPermissionResidue(string stage)
    {
        var f = new BundlePublicationIdentityTests.Fixture(); var files = new TrackingFiles(); using var cancel = new CancellationTokenSource();
        var publisher = new BundlePublicationReceipt.Publisher { Files = files, StageForTests = (current, _) => { if (current == stage) cancel.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Publish(publisher, cancel.Token));
        Assert.False(Directory.Exists(f.Final)); Assert.All(files.Created, file => Assert.False(File.Exists(file.Path)));
        Assert.False(Directory.Exists(publisher.PublishingDirectoryForTests));
        var writable = Path.Combine(f.Root, "ordinary-after-cancel"); File.WriteAllText(writable, "ok"); Operate(writable, false);
    }

    [Theory] [InlineData("replace")] [InlineData("hardlink")]
    public async Task MetadataHandoffRejectsForeignObjectsAndPreservesTheirBytesAndAcl(string fault)
    {
        var f = new BundlePublicationIdentityTests.Fixture(); var files = new TrackingFiles(); string? foreign = null; byte[]? before = null;
        var publisher = new BundlePublicationReceipt.Publisher { Files = files, StageForTests = (stage, _) =>
        {
            if (stage != "objects_held") return;
            var original = files.Created[0].Path; foreign = fault == "replace" ? original : original + ".third-party-link";
            if (fault == "replace") { File.Move(original, original + ".original"); File.WriteAllBytes(foreign, Enumerable.Repeat((byte)77, 11).ToArray()); }
            else Assert.True(CreateHardLinkW(foreign, original, IntPtr.Zero));
            before = Security(foreign);
        } };
        await Assert.ThrowsAsync<IOException>(() => f.Publish(publisher)); Assert.False(Directory.Exists(f.Final));
        Assert.NotNull(foreign); Assert.Equal(before, Security(foreign));
        Assert.Equal(fault == "replace" ? Enumerable.Repeat((byte)77, 11).ToArray() : new byte[11], File.ReadAllBytes(foreign));
    }

    [Fact]
    public async Task CommittedDirectoryAndAllAliasesRejectReplacementUntilReceiptThenAllowOrdinaryOperations()
    {
        var f = new BundlePublicationIdentityTests.Fixture(); var files = new TrackingFiles(); var attempts = 0;
        var publisher = new BundlePublicationReceipt.Publisher { Files = files, StageForTests = (stage, _) =>
        {
            if (stage != "after_commit") return;
            Assert.Throws<IOException>(() => Directory.Move(f.Final, f.Final + ".foreign"));
            foreach (var file in files.Created)
            {
                var alias = Path.Combine(f.Final, file.Name);
                Assert.Equal(Convert.FromBase64String(file.Dacl), Security(alias));
                Assert.Throws<IOException>(() => File.Move(alias, alias + ".foreign"));
                Assert.Throws<IOException>(() => File.WriteAllBytes(alias, new byte[new FileInfo(alias).Length]));
                Assert.Throws<IOException>(() => File.Delete(alias)); attempts++;
                Assert.Throws<IOException>(() => File.Move(file.Path, file.Path + ".foreign"));
                Assert.Throws<IOException>(() => File.WriteAllBytes(file.Path, new byte[new FileInfo(alias).Length]));
                Assert.Throws<IOException>(() => File.Delete(file.Path));
            }
        } };
        var ready = await f.Publish(publisher); Assert.Equal(5, attempts); Assert.NotNull(ready.PublicationReceipt);
        Assert.All(ready.PublicationReceipt.Evidence.Anchors, a => Assert.Equal(a.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(a.Path).Before!.Identity));
        foreach (var file in files.Created) Operate(Path.Combine(f.Final, file.Name), false);
        RenameAndDeleteDirectory(f.Final);
    }

    [Fact]
    public async Task ExceptionAfterCommitLeavesCompleteOperableObjectsWithoutSigningPartialEvidence()
    {
        var f = new BundlePublicationIdentityTests.Fixture(); var files = new TrackingFiles();
        var publisher = new BundlePublicationReceipt.Publisher { Files = files, StageForTests = (stage, _) => { if (stage == "after_commit") throw new IOException("owned postcommit fault"); } };
        await Assert.ThrowsAsync<IOException>(() => f.Publish(publisher)); Assert.True(Directory.Exists(f.Final)); Assert.Equal(5, files.Created.Count);
        foreach (var file in files.Created)
        { var alias = Path.Combine(f.Final, file.Name); Assert.Equal(Convert.FromBase64String(file.Dacl), Security(alias)); Operate(alias, false); }
        RenameAndDeleteDirectory(f.Final);
    }

    private sealed record CreatedFile(string Path, string Name, string Dacl);
    private sealed record ProbeRecord(int Pid, string Stage, bool Elevated, string Final, string Private, string PrivateDacl,
        string FinalDacl, CreatedFile[] Files, bool Receipt, int NativeMatches);
    private sealed class TrackingFiles : IPublicationFileOperations
    {
        internal List<CreatedFile> Created { get; } = new();
        public FileStream CreateNew(string path)
        {
            var stream = BundlePublicationFileOperations.Instance.CreateNew(path);
            Created.Add(new(path, path[(path.LastIndexOf('-') + 1)..], Convert.ToBase64String(Security(stream.SafeFileHandle)))); return stream;
        }
        public void Rename(SafeFileHandle h, string p) => BundlePublicationFileOperations.Instance.Rename(h, p);
        public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle h) => BundlePublicationFileOperations.Instance.DeleteOriginal(h);
    }
    private static byte[] Security(string path)
    {
        using var handle = WindowsFileMetadataApi.Instance.Open(@"\\?\" + Path.GetFullPath(path), 0x20000, 7, 3, 0x02200000, out var error);
        Assert.False(handle.IsInvalid, "Read DACL error=" + error); return Security(handle);
    }
    private static byte[] Security(SafeFileHandle handle)
    {
        GetKernelObjectSecurity(handle, 4, null, 0, out var length); var bytes = new byte[length];
        Assert.True(GetKernelObjectSecurity(handle, 4, bytes, length, out _)); return bytes;
    }
    private static bool Elevated()
    {
        Assert.True(OpenProcessToken(Process.GetCurrentProcess().Handle, 8, out var token)); using (token)
        { Assert.True(GetTokenInformation(token, 20, out var elevated, 4, out _)); return elevated != 0; }
    }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint info, byte[]? bytes, uint length, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeFileHandle token, int info, out int value, int length, out int required);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkW(string link, string source, IntPtr security);

    private static async Task<string> BuildChild()
    {
        // A DLL-only subprocess uses this run's actual tested product assemblies.
        // It never rebuilds or replaces them. Source/logs/results are retained.
        var id = Guid.NewGuid().ToString("N"); var root = Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task337r", "child-host-" + id);
        Directory.CreateDirectory(root);
        var references = new[] { typeof(RecordingBundleRequest).Assembly.Location, typeof(WindowsReadOnlyFileMetadataProbe).Assembly.Location };
        var project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows10.0.19041.0</TargetFramework><AssemblyName>AgentRecorder.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>" +
            string.Join("", references.Select(path => $"<Reference Include=\"{Path.GetFileNameWithoutExtension(path)}\"><HintPath>{SecurityElement.Escape(path)}</HintPath></Reference>")) + "</ItemGroup></Project>";
        var csproj = Path.Combine(root, "Probe.csproj"); await File.WriteAllTextAsync(csproj, project); await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), ChildSource);
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "build", csproj, "-c", "Release", "--nologo", "-p:NuGetAudit=false" }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        var area = Path.Combine(TestHelper.ProjectRoot, ".local-data/test-results/task337r"); Directory.CreateDirectory(area);
        await File.WriteAllTextAsync(Path.Combine(area, "crash-helper-" + id + ".log"), await stdout + await stderr);
        await File.WriteAllTextAsync(Path.Combine(area, "crash-helper-" + id + ".exit"), process.ExitCode.ToString());
        Assert.Equal(0, process.ExitCode);
        return Path.Combine(root, "bin/Release/net8.0-windows10.0.19041.0/AgentRecorder.Tests.exe");
    }

    private const string ChildSource = """
        using System.Diagnostics;
        using System.Runtime.InteropServices;
        using System.Text.Json;
        using AgentRecorder.Capture;
        using AgentRecorder.Infrastructure;
        using Microsoft.Win32.SafeHandles;
        var root = args[0]; var pause = args[1]; var final = Path.Combine(root, "outputs", "out.bundle");
        var stagePath = Path.Combine(root, "owned-source"); Directory.CreateDirectory(stagePath);
        var items = new[] { new RecordingBundleContentItem("metadata.json", "application/json", 13),
            new RecordingBundleContentItem("thumbnail.jpg", "image/jpeg", 13), new RecordingBundleContentItem("first_frame.png", "image/png", 13),
            new RecordingBundleContentItem("last_frame.png", "image/png", 13), new RecordingBundleContentItem("marks.json", "application/json", 13) };
        foreach (var item in items) File.WriteAllBytes(Path.Combine(stagePath, item.Name), new byte[13]);
        var request = new RecordingBundleRequest("crash_owned", null, "display", "Synthetic", (0,0,64,64), "virtual_screen",
            DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow, 1, 1, 15, "ffmpeg", "duration_reached", false,
            "not_requested", null, null, null, null, null, null, Path.Combine(root,"outputs","out.mp4"), "mp4", "h264", 64, 64)
            { PublicationOperationId = Guid.NewGuid().ToString("N") };
        var files = new Files(); BundlePublicationReceipt.Publisher? publisher = null;
        string privateDacl = "", finalDacl = ""; var privatePath = "";
        object Record(string stage, bool receipt = false, int matches = 0) => new {
            Pid = Environment.ProcessId, Stage = stage, Elevated = Native.Elevated(), Final = final, Private = privatePath,
            PrivateDacl = privateDacl, FinalDacl = finalDacl, Files = files.Created, Receipt = receipt, NativeMatches = matches };
        publisher = new() { Files = files, StageForTests = (stage, _) => {
            if (privateDacl == "" && publisher!.PublishingDirectoryForTests is {} path) {
                privatePath = path; privateDacl = Convert.ToBase64String(Native.Security(path)); }
            if (stage == "after_commit") finalDacl = Convert.ToBase64String(Native.Security(final));
            if (stage != pause) return;
            File.WriteAllText(Path.Combine(root,"barrier.tmp"), JsonSerializer.Serialize(Record(stage)));
            File.Move(Path.Combine(root,"barrier.tmp"), Path.Combine(root,"barrier.json"));
            new ManualResetEventSlim(false).Wait();
        } };
        var result = await publisher.PublishAsync(request, stagePath, final, new(items), default);
        if (!result.Success || result.PublicationReceipt is null) throw new Exception("Normal control did not issue complete receipt");
        var matches = result.PublicationReceipt.Evidence.Anchors.Count(a => {
            var sample = WindowsReadOnlyFileMetadataProbe.Instance.Observe(a.Path);
            return sample.Outcome == FileObservationOutcome.Sampled && sample.Before!.Identity == a.Identity && sample.Before.Length == a.Length; });
        if (matches != 5) throw new Exception("Original identity did not match all five committed objects");
        File.WriteAllText(Path.Combine(root,"complete.json"), JsonSerializer.Serialize(Record("normal",true,matches)));
        Console.WriteLine("normal complete, receipt=true native_matches=5");
        sealed class Files : IPublicationFileOperations {
            internal List<object> Created {get;} = new();
            public FileStream CreateNew(string path) {
                var stream = BundlePublicationFileOperations.Instance.CreateNew(path);
                Created.Add(new {Path=path,Name=path[(path.LastIndexOf('-')+1)..],Dacl=Convert.ToBase64String(Native.Security(stream.SafeFileHandle))}); return stream; }
            public void Rename(SafeFileHandle h,string p) => BundlePublicationFileOperations.Instance.Rename(h,p);
            public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle h) => BundlePublicationFileOperations.Instance.DeleteOriginal(h);
        }
        static class Native {
            internal static byte[] Security(string path) {
                using var h=WindowsFileMetadataApi.Instance.Open(@"\\?\"+Path.GetFullPath(path),0x20000,7,3,0x02200000,out var error);
                if(h.IsInvalid)throw new Exception("DACL view failed:"+error);return Security(h); }
            internal static byte[] Security(SafeFileHandle h) {
                GetKernelObjectSecurity(h,4,null,0,out var length);var bytes=new byte[length];
                if(!GetKernelObjectSecurity(h,4,bytes,length,out _))throw new Exception("DACL query failed");return bytes; }
            internal static bool Elevated() {
                if(!OpenProcessToken(Process.GetCurrentProcess().Handle,8,out var token))throw new Exception("Token failed");
                using(token) {if(!GetTokenInformation(token,20,out var elevated,4,out _))throw new Exception("Token query failed");return elevated!=0;} }
            [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetKernelObjectSecurity(SafeFileHandle h,uint i,byte[]? b,uint l,out uint r);
            [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr p,uint a,out SafeFileHandle t);
            [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(SafeFileHandle t,int i,out int v,int l,out int r);
        }
        """;
}
