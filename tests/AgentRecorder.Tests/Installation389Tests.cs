using AgentRecorder.Cli;
using AgentRecorder.Infrastructure;
using Xunit;
using CliProgram = AgentRecorder.Cli.Program;

namespace AgentRecorder.Tests;

public sealed class Installation389Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "installation389-" + Guid.NewGuid().ToString("N"));
    private string Local => Path.Combine(_root, "local");
    private string ProgramRoot => InstallationContext.ExpectedProgramRoot(Local);
    private string Data => Path.Combine(Local, "AgentRecorder");
    private string Marker => Path.Combine(ProgramRoot, InstallationContext.MarkerName);
    public Installation389Tests() => Directory.CreateDirectory(ProgramRoot);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private void Mark() => File.WriteAllText(Marker, InstallationContext.MarkerJson);

    [Fact]
    public void NoMarkerPreservesBothPortableDefaultsAndExistingPriorities()
    {
        var environment = Path.Combine(_root, "environment");
        Assert.Equal(Path.Combine(ProgramRoot, ".local-data"), InstallationContext.ResolveCliDataRoot(ProgramRoot, null, environment, Local));
        Assert.Equal(environment, DataDirResolver.ResolveForContext(ProgramRoot, Local, null, environment));
        Assert.Equal(Data, DataDirResolver.ResolveForContext(ProgramRoot, Local, null, null));
        var explicitPath = Path.Combine(_root, "explicit");
        Assert.Equal(explicitPath, InstallationContext.ResolveCliDataRoot(ProgramRoot, explicitPath, environment, Local));
        Assert.Equal(explicitPath, DataDirResolver.ResolveForContext(ProgramRoot, Local, explicitPath, environment));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InstalledAppAndCliChooseSameRootBeforeAnyDataWrites(bool explicitOverride, bool envOverride)
    {
        Mark();
        var explicitPath = explicitOverride ? Path.Combine(_root, "explicit") : null;
        var environment = envOverride ? Path.Combine(_root, "environment") : null;
        var expected = explicitPath ?? environment ?? Data;
        Assert.Equal(expected, InstallationContext.ResolveCliDataRoot(ProgramRoot, explicitPath, environment, Local));
        Assert.Equal(expected, DataDirResolver.ResolveForContext(ProgramRoot, Local, explicitPath, environment));
        Assert.False(Directory.Exists(expected));
        Assert.False(Directory.Exists(Path.Combine(ProgramRoot, ".local-data")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"format\":1,\"format\":1,\"product\":\"AgentRecorder.PerUser.v1\",\"version\":\"0.1.19\",\"mode\":\"per-user\"}")]
    [InlineData("{\"format\":2,\"product\":\"AgentRecorder.PerUser.v1\",\"version\":\"0.1.19\",\"mode\":\"per-user\"}")]
    [InlineData("{\"format\":1,\"product\":\"Other\",\"version\":\"0.1.19\",\"mode\":\"per-user\"}")]
    [InlineData("{\"format\":1,\"product\":\"AgentRecorder.PerUser.v1\",\"version\":\"0.1.18\",\"mode\":\"per-user\"}")]
    [InlineData("{\"format\":1,\"product\":\"AgentRecorder.PerUser.v1\",\"version\":\"0.1.19\",\"mode\":\"per-user\"}")]
    [InlineData("{\"format\":1,\"product\":\"AgentRecorder.PerUser.v1\",\"version\":\"0.1.21\",\"mode\":\"per-user\"}")]
    [InlineData("{\"format\":1,\"product\":\"AgentRecorder.PerUser.v1\",\"version\":\"0.1.19\",\"mode\":\"per-user\",\"data_dir\":\"C:\\\\other\"}")]
    public void MalformedMarkerNeverFallsBackEvenWithOverride(string contents)
    {
        File.WriteAllText(Marker, contents);
        var explicitPath = Path.Combine(_root, "explicit");
        Assert.ThrowsAny<Exception>(() => InstallationContext.ResolveCliDataRoot(ProgramRoot, explicitPath, null, Local));
        Assert.ThrowsAny<Exception>(() => DataDirResolver.ResolveForContext(ProgramRoot, Local, explicitPath, null));
        Assert.False(Directory.Exists(explicitPath));
    }

    [Fact]
    public void CopiedMarkerDifferentUserOrPackageAndNestedMarkersAreConflicts()
    {
        Mark();
        var copied = Path.Combine(_root, "another-package");
        Directory.CreateDirectory(copied);
        File.Copy(Marker, Path.Combine(copied, InstallationContext.MarkerName));
        Assert.Throws<InvalidDataException>(() => InstallationContext.IsInstalled(copied, Local));
        Assert.Throws<InvalidDataException>(() => InstallationContext.IsInstalled(ProgramRoot, Path.Combine(_root, "another-user")));
        var component = Path.Combine(ProgramRoot, "AgentRecorder.App");
        Directory.CreateDirectory(component);
        File.Copy(Marker, Path.Combine(component, InstallationContext.MarkerName));
        Assert.Throws<InvalidDataException>(() => InstallationContext.IsInstalled(ProgramRoot, Local));
        File.Delete(Marker);
        Assert.Throws<InvalidDataException>(() => InstallationContext.IsInstalled(ProgramRoot, Local));
    }

    [Fact]
    public void InstalledDataCannotBeProgramTreeOrItsParent()
    {
        Mark();
        foreach (var unsafeData in new[] { ProgramRoot, Path.Combine(ProgramRoot, "state"), Local })
        {
            Assert.Throws<InvalidDataException>(() => InstallationContext.ResolveCliDataRoot(ProgramRoot, unsafeData, null, Local));
            Assert.Throws<InvalidDataException>(() => DataDirResolver.ResolveForContext(ProgramRoot, Local, unsafeData, null));
        }
        var sibling = ProgramRoot + "-data";
        Assert.Equal(sibling, InstallationContext.ResolveCliDataRoot(ProgramRoot, sibling, null, Local));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("upgrade")]
    [InlineData("uninstall")]
    public void LifecycleGateFailsClosedAndDoesNotMutateUserData(string operation)
    {
        if (operation != "install") Mark();
        Directory.CreateDirectory(Path.Combine(Data, "runtime"));
        var protectedFiles = new[] { "Videos/recording.mp4", "config/api-key.txt", "config/settings.json", "state/history.db", "logs/audit.jsonl" };
        foreach (var f in protectedFiles)
        {
            var path = Path.Combine(Data, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "synthetic-" + f);
        }
        var original = protectedFiles.ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(Data, f)));
        Assert.True(Gate(operation).Safe);
        Assert.False(Gate(operation, lease: false).Safe);
        Assert.False(Gate(operation, hosts: false).Safe);
        Assert.False(Gate(operation, port: false).Safe);
        Assert.False(Gate(operation, integrations: false).Safe);
        Assert.False(Gate(operation, throwOnProcess: true).Safe);
        File.WriteAllText(Path.Combine(Data, "runtime", "ready.json"), "{invalid}");
        Assert.Equal("INSTALL_READY_INVALID", Gate(operation).Code);
        foreach (var f in protectedFiles) Assert.Equal(original[f], File.ReadAllBytes(Path.Combine(Data, f)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("force")]
    [InlineData("silent")]
    [InlineData("/FORCECLOSEAPPLICATIONS")]
    public void InvalidOperationNeverReadsRuntimeOrWrites(string? operation)
    {
        Assert.False(Gate(operation, throwOnProcess: true).Safe);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ProgramRoot));
    }

    [Fact]
    public void UnknownExistingDirectoryAndMissingOwnershipCannotBeAdopted()
    {
        File.WriteAllText(Path.Combine(ProgramRoot, "user-file.txt"), "keep");
        Assert.Equal("INSTALL_OWNER_UNKNOWN", Gate("install").Code);
        Assert.Equal("INSTALL_OWNER_UNKNOWN", Gate("uninstall").Code);
        Assert.Equal("INSTALL_OWNER_UNKNOWN", Gate("upgrade").Code);
        Assert.False(CliProgram.EvaluateInstallationSafety(_root, "install", Checks()).Safe);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(ProgramRoot, "user-file.txt")));
    }

    [Fact]
    public void PackageComponentResolutionIsSharedAndDoesNotGuessInstallationFromFolderName()
    {
        foreach (var component in new[] { "AgentRecorder.App", "AgentRecorder.Cli", "AgentRecorder.Headless" })
            Assert.Equal(ProgramRoot, InstallationContext.PackageRootFromBase(Path.Combine(ProgramRoot, component)));
        Assert.False(InstallationContext.IsInstalled(ProgramRoot, Local));
    }

    [Fact]
    public void RealDirectoryJunctionBlocksDataAndInstalledTreeWithoutFollowingIt()
    {
        Mark();
        Directory.CreateDirectory(Data);
        var preserved = Path.Combine(Data, "preserved.txt");
        File.WriteAllText(preserved, "synthetic-keep");
        var junction = Path.Combine(ProgramRoot, "redirect");
        var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" +
            junction.Replace("'", "''") + "' -Target '" + Data.Replace("'", "''") + "' | Out-Null";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var command = System.Diagnostics.Process.Start(start)!;
        Assert.True(command.WaitForExit(10000), "Synthetic junction creation did not finish.");
        Assert.Equal(0, command.ExitCode);
        Assert.Throws<InvalidDataException>(() => InstallationContext.RequireNonReparse(Path.Combine(junction, "new-state")));
        Assert.Throws<InvalidDataException>(() => CliProgram.RequireOrdinaryInstallationTree(ProgramRoot));
        Assert.False(Gate("upgrade").Safe);
        Assert.Equal("synthetic-keep", File.ReadAllText(preserved));
        // Remove just the synthetic link before the ordinary fixture cleanup; never follow it.
        Directory.Delete(junction);
        Assert.True(File.Exists(preserved));
    }

    [Fact]
    public void MaintenanceLeaseBlocksAConcurrentOwnerAndIsReleased()
    {
        var name = @"Local\AgentRecorder.Task389." + Guid.NewGuid().ToString("N");
        using (var installer = SingleInstanceGuard.TryAcquire(name))
        {
            Assert.True(installer.IsAcquired);
            using var app = SingleInstanceGuard.TryAcquire(name);
            Assert.False(app.IsAcquired);
        }
        using var after = SingleInstanceGuard.TryAcquire(name);
        Assert.True(after.IsAcquired);
    }

    [Fact]
    public void InstalledHostAndInstallerHoldBothLeasesUntilDisposalAndReleasePartialAcquisition()
    {
        var service = @"Local\AgentRecorder.Task389.Service." + Guid.NewGuid().ToString("N");
        var maintenance = @"Local\AgentRecorder.Task389.Maintenance." + Guid.NewGuid().ToString("N");
        using (var app = SingleInstanceGuard.TryAcquireForInstallation(service, maintenance))
        {
            Assert.True(app.IsAcquired);
            using var blocked = SingleInstanceGuard.TryAcquireForInstallation(service, maintenance);
            Assert.False(blocked.IsAcquired);
        }
        using (var installerLease = SingleInstanceGuard.TryAcquire(maintenance))
        {
            using var blocked = SingleInstanceGuard.TryAcquireForInstallation(service, maintenance);
            Assert.False(blocked.IsAcquired);
            using var originalService = SingleInstanceGuard.TryAcquire(service);
            Assert.True(originalService.IsAcquired); // Failed maintenance acquisition never holds service.
        }
        using (var portable = SingleInstanceGuard.TryAcquire(service))
        {
            using var blocked = SingleInstanceGuard.TryAcquireForInstallation(service, maintenance);
            Assert.False(blocked.IsAcquired);
            using var released = SingleInstanceGuard.TryAcquire(maintenance);
            Assert.True(released.IsAcquired); // Failed service acquisition releases the partial maintenance lease.
        }
    }

    private CliProgram.InstallationSafetyChecks Checks(bool lease = true, bool hosts = true, bool port = true, bool integrations = true, bool throwOnProcess = false) => new()
    {
        LocalRoot = Local,
        MutexesPresent = () => lease,
        NoInstallationRegistration = () => true,
        NoHostProcesses = _ => throwOnProcess ? throw new IOException("identity unavailable") : hosts,
        PortAbsent = () => port,
        NoIntegrations = _ => integrations
    };
    private CliProgram.InstallationSafetyResult Gate(string? operation, bool lease = true, bool hosts = true, bool port = true, bool integrations = true, bool throwOnProcess = false)
        => CliProgram.EvaluateInstallationSafety(ProgramRoot, operation, Checks(lease, hosts, port, integrations, throwOnProcess));

    [Theory]
    [InlineData("same_user", false)]
    [InlineData("other_package", false)]
    [InlineData("other_user_same_image", false)]
    [InlineData("other_user_other_package", true)]
    [InlineData("unknown_owner", false)]
    [InlineData("unknown_path", false)]
    [InlineData("unknown_start", false)]
    [InlineData("exited", true)]
    public void ProcessNamesOrPidsNeverProvideAbsenceProof(string scenario, bool safe)
    {
        var process = new EvidenceProcess { ExecutablePath = Path.Combine(ProgramRoot, "AgentRecorder.App", "AgentRecorder.App.exe") };
        switch (scenario)
        {
            case "other_package": process.ExecutablePath = Path.Combine(_root, "portable", "AgentRecorder.App.exe"); break;
            case "other_user_same_image": process.ProcessUserSid = "other-user"; break;
            case "other_user_other_package": process.ProcessUserSid = "other-user"; process.ExecutablePath = Path.Combine(_root, "other", "AgentRecorder.App.exe"); break;
            case "unknown_owner": process.ProcessUserSid = ""; break;
            case "unknown_path": process.ExecutablePath = ""; break;
            case "unknown_start": process.StartedAtUtcTicks = 0; break;
            case "exited": process.HasExited = true; break;
        }
        Assert.Equal(safe, CliProgram.NoInstallationHostProcesses(new[] { process }, "current-user", ProgramRoot));
        Assert.True(process.Disposed);
    }

    private sealed class EvidenceProcess : CliProgram.IShutdownProcess
    {
        public long StartedAtUtcTicks { get; set; } = 100;
        public string ExecutablePath { get; set; } = "";
        public string ProcessUserSid { get; set; } = "current-user";
        public bool HasExited { get; set; }
        public bool Disposed { get; private set; }
        public Task WaitForExitAsync(CancellationToken token) => throw new InvalidOperationException("The installation gate cannot wait for/stop a process.");
        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData("absent", true)]
    [InlineData("run", false)]
    [InlineData("malformed", false)]
    [InlineData("installed", false)]
    [InlineData("portable", true)]
    public void IntegrationGatePreservesSyntheticRunAndBrokerEntriesWithoutGuessingOwnership(string scenario, bool allowed)
    {
        var run = new FakeRegistryRunKey();
        var registrationPath = Path.Combine(Data, "interactive-launch.json");
        Directory.CreateDirectory(Data);
        if (scenario == "run") run.SetValue(WindowsAutoStartManager.DefaultValueName, "unknown-product-command");
        if (scenario == "malformed") File.WriteAllText(registrationPath, "invalid");
        if (scenario is "installed" or "portable")
        {
            var app = Path.Combine(scenario == "installed" ? ProgramRoot : Path.Combine(_root, "portable"), "AgentRecorder.App", "AgentRecorder.App.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(app)!);
            File.WriteAllText(app, "synthetic-executable-never-run");
            Assert.True(InteractiveLaunchRegistrationStore.TryCreate(app, Data, "S-1-5-21-100-200-300-1001", out var registration, out _, userSidValidator: _ => true));
            Assert.True(InteractiveLaunchRegistrationStore.TrySave(registration!, registrationPath, out _));
        }
        var before = File.Exists(registrationPath) ? File.ReadAllBytes(registrationPath) : null;
        Assert.Equal(allowed, CliProgram.NoInstallationIntegrations(ProgramRoot, run, registrationPath));
        if (before != null) Assert.Equal(before, File.ReadAllBytes(registrationPath));
        if (scenario == "run") Assert.Equal("unknown-product-command", run.GetValue(WindowsAutoStartManager.DefaultValueName));
    }
}
