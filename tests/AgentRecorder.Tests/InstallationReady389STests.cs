using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Infrastructure;
using Xunit;
using CliProgram = AgentRecorder.Cli.Program;
using PidState = AgentRecorder.Cli.Program.InstallationPidState;

namespace AgentRecorder.Tests;

public sealed class InstallationReady389STests : IDisposable
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const int Port = 37891;
    private readonly string _local = Path.Combine(Path.GetTempPath(), "install-ready-389s-" + Guid.NewGuid().ToString("N"));
    private string ProgramRoot => InstallationContext.ExpectedProgramRoot(_local);
    private string Data => Path.Combine(_local, "AgentRecorder");
    private string ReadyPath => Path.Combine(Data, "runtime", "ready.json");

    public void Dispose()
    {
        if (Directory.Exists(_local)) Directory.Delete(_local, recursive: true);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var operation in new[] { "install", "upgrade", "uninstall" })
        foreach (var (scenario, code) in new[]
        {
            ("missing", "INSTALL_SAFE_WHILE_LEASE_HELD"),
            ("retired", "INSTALL_SAFE_WHILE_LEASE_HELD"),
            ("active", "INSTALL_READY_PID_PRESENT"),
            ("reused", "INSTALL_READY_PID_PRESENT"),
            ("denied", "INSTALL_READY_PID_UNKNOWN"),
            ("bad_json", "INSTALL_READY_INVALID"),
            ("duplicate", "INSTALL_READY_INVALID"),
            ("wrong_data", "INSTALL_READY_INVALID"),
            ("wrong_ready", "INSTALL_READY_INVALID"),
            ("wrong_sid", "INSTALL_READY_INVALID"),
            ("bad_start", "INSTALL_READY_INVALID"),
            ("bad_time", "INSTALL_READY_INVALID"),
            ("zero_pid", "INSTALL_READY_INVALID"),
            ("other_port", "INSTALL_READY_INVALID"),
            ("oversize", "INSTALL_READY_UNTRUSTED"),
            ("directory", "INSTALL_READY_UNTRUSTED"),
            ("occupied", "INSTALL_API_UNKNOWN"),
            ("unleased", "INSTALL_LEASE_MISSING"),
            ("integration", "INSTALL_INTEGRATION_REQUIRES_REVIEW"),
            ("host_scan", "INSTALL_HOST_ACTIVE_OR_UNKNOWN")
        })
            yield return new object[] { operation, scenario, code };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LifecycleDecisionPreservesReadyAndUserData(string operation, string scenario, string code)
    {
        SetUp(operation);
        var record = Record();
        switch (scenario)
        {
            case "active": record.Pid = Environment.ProcessId; break;
            case "reused": record.Pid = Environment.ProcessId; record.ProcessStartedAtUtcTicks -= TimeSpan.TicksPerDay; break;
            case "wrong_data": record.DataDir += "-other"; break;
            case "wrong_ready": record.ReadyFile += ".other"; break;
            case "wrong_sid": record.ProcessUserSid = "S-1-5-18"; break;
            case "bad_start": record.ProcessStartedAtUtcTicks = long.MaxValue; break;
            case "bad_time": record.ReadyAt = "invalid"; break;
            case "zero_pid": record.Pid = 0; break;
            case "other_port": record.Port++; break;
        }
        var content = scenario switch
        {
            "missing" or "directory" => null,
            "bad_json" => "{",
            "duplicate" => Serialize(record).Insert(1, "\"PID\":1,"),
            "oversize" => new string(' ', 16 * 1024) + Serialize(record),
            _ => Serialize(record)
        };
        if (scenario == "directory") Directory.CreateDirectory(ReadyPath);
        if (content != null) File.WriteAllText(ReadyPath, content);
        var before = content == null ? null : File.ReadAllBytes(ReadyPath);
        var probes = 0;
        var result = CliProgram.EvaluateInstallationSafety(ProgramRoot, operation, new()
        {
            LocalRoot = _local,
            CurrentUserSid = () => Sid,
            NoInstallationRegistration = () => true,
            MutexesPresent = () => scenario != "unleased",
            NoHostProcesses = _ => scenario != "host_scan",
            PortAbsent = () => scenario != "occupied",
            NoIntegrations = _ => scenario != "integration",
            ObserveReadyPid = pid =>
            {
                probes++;
                return scenario switch
                {
                    "active" or "reused" => CliProgram.ObserveInstallationReadyPid(pid),
                    "denied" => PidState.Unknown,
                    _ => PidState.Absent
                };
            }
        });
        Assert.Equal(code, result.Code);
        Assert.Equal(code == "INSTALL_SAFE_WHILE_LEASE_HELD", result.Safe);
        if (before != null) Assert.Equal(before, File.ReadAllBytes(ReadyPath));
        else Assert.Equal(scenario == "directory", Directory.Exists(ReadyPath));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Data, "preserve.dat")));
        if (scenario is "missing" or "unleased" or "host_scan" || code is "INSTALL_READY_INVALID" or "INSTALL_READY_UNTRUSTED")
            Assert.Equal(0, probes);
    }

    [Fact]
    public void WindowsConfirmsAbsentPidAndRealPortOccupancyStillBlocks()
    {
        SetUp("install");
        // Use the real absence probe. A just-exited child can still have an openable process object.
        const int pid = 0x7fff_fffc;
        Assert.Equal(PidState.Absent, CliProgram.ObserveInstallationReadyPid(pid));
        var record = Record();
        record.Pid = pid;
        File.WriteAllText(ReadyPath, Serialize(record));
        var before = File.ReadAllBytes(ReadyPath);
        var checks = new CliProgram.InstallationSafetyChecks
        {
            LocalRoot = _local, CurrentUserSid = () => Sid, MutexesPresent = () => true,
            NoInstallationRegistration = () => true,
            NoHostProcesses = _ => true, NoIntegrations = _ => true
        };
        Assert.True(CliProgram.EvaluateInstallationSafety(ProgramRoot, "install", checks).Safe);
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.ExclusiveAddressUse = true;
        listener.Bind(new IPEndPoint(IPAddress.Loopback, Port));
        listener.Listen();
        Assert.Equal("INSTALL_API_UNKNOWN", CliProgram.EvaluateInstallationSafety(ProgramRoot, "install", checks).Code);
        Assert.Equal(before, File.ReadAllBytes(ReadyPath));
    }

    [Fact]
    public void FileAppearanceDuringChecksBlocksAndExistingRecordIsHeldReadOnly()
    {
        SetUp("upgrade");
        var content = Serialize(Record());
        Assert.Equal("INSTALL_READY_CHANGED", Gate(() => File.WriteAllText(ReadyPath, content)).Code);
        var before = File.ReadAllBytes(ReadyPath);
        Assert.True(Gate(() => Assert.Throws<IOException>(() => File.WriteAllText(ReadyPath, "replace"))).Safe);
        Assert.Equal(before, File.ReadAllBytes(ReadyPath));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("upgrade")]
    [InlineData("uninstall")]
    public void ReparseRuntimeDirectoryIsRejectedWithoutReadingItsTarget(string operation)
    {
        SetUp(operation);
        var runtime = Path.GetDirectoryName(ReadyPath)!;
        Directory.Delete(runtime);
        var target = Directory.CreateDirectory(Path.Combine(_local, "redirected")).FullName;
        var targetFile = Path.Combine(target, "ready.json");
        File.WriteAllText(targetFile, Serialize(Record()));
        var before = File.ReadAllBytes(targetFile);
        var script = $"New-Item -ErrorAction Stop -ItemType Junction -Path '{runtime.Replace("'", "''")}' -Target '{target.Replace("'", "''")}'";
        var command = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var child = Process.Start(command)!;
        Assert.True(child.WaitForExit(10000));
        Assert.Equal(0, child.ExitCode);
        try
        {
            Assert.Equal("INSTALL_READY_UNTRUSTED", Gate(() => { }, operation).Code);
            Assert.Equal(before, File.ReadAllBytes(targetFile));
        }
        finally { Directory.Delete(runtime); }
    }

    private CliProgram.InstallationSafetyResult Gate(Action duringChecks, string operation = "upgrade") =>
        CliProgram.EvaluateInstallationSafety(ProgramRoot, operation, new()
        {
            LocalRoot = _local, CurrentUserSid = () => Sid, MutexesPresent = () => true,
            NoInstallationRegistration = () => true,
            NoHostProcesses = _ => true, PortAbsent = () => true, ObserveReadyPid = _ => PidState.Absent,
            NoIntegrations = _ => { duringChecks(); return true; }
        });

    private void SetUp(string operation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReadyPath)!);
        File.WriteAllText(Path.Combine(Data, "preserve.dat"), "preserve");
        if (operation == "install") return;
        Directory.CreateDirectory(ProgramRoot);
        File.WriteAllText(Path.Combine(ProgramRoot, InstallationContext.MarkerName), InstallationContext.MarkerJson);
    }

    private ReadySnapshot Record()
    {
        var started = DateTime.UtcNow.AddMinutes(-2);
        return new()
        {
            Ready = true, Pid = 61536, Port = Port, InstanceId = Guid.NewGuid().ToString("D"),
            Mode = "tray", ProcessUserSid = Sid, ExecutablePath = Path.Combine(_local, "old-package", "AgentRecorder.App.exe"),
            DataDir = Data, ReadyFile = ReadyPath, ApiKeyFile = Path.Combine(Data, "config", "api-key.txt"),
            ProcessStartedAtUtcTicks = started.Ticks,
            StartedAt = started.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ReadyAt = started.AddSeconds(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        };
    }

    private static string Serialize(ReadySnapshot record) =>
        JsonSerializer.Serialize(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    [Theory]
    [InlineData("retired", "INSTALL_SAFE_WHILE_LEASE_HELD")]
    [InlineData("active", "INSTALL_READY_PID_PRESENT")]
    [InlineData("reused", "INSTALL_READY_PID_PRESENT")]
    [InlineData("unknown", "INSTALL_READY_PID_UNKNOWN")]
    [InlineData("port", "INSTALL_API_UNKNOWN")]
    [InlineData("integration", "INSTALL_INTEGRATION_REQUIRES_REVIEW")]
    [InlineData("unleased", "INSTALL_LEASE_MISSING")]
    [InlineData("host", "INSTALL_HOST_ACTIVE_OR_UNKNOWN")]
    [InlineData("registration", "INSTALL_READY_LEGACY_REQUIRES_FIRST_INSTALL")]
    [InlineData("registration_appears", "INSTALL_READY_LEGACY_REQUIRES_FIRST_INSTALL")]
    [InlineData("installed", "INSTALL_READY_LEGACY_REQUIRES_FIRST_INSTALL")]
    [InlineData("occupied", "INSTALL_OWNER_UNKNOWN")]
    [InlineData("upgrade", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("uninstall", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("wrong_data", "INSTALL_READY_INVALID")]
    [InlineData("wrong_ready", "INSTALL_READY_INVALID")]
    [InlineData("wrong_key", "INSTALL_READY_INVALID")]
    [InlineData("wrong_audit", "INSTALL_READY_INVALID")]
    [InlineData("missing_field", "INSTALL_READY_INVALID")]
    [InlineData("partial_identity", "INSTALL_READY_INVALID")]
    [InlineData("unknown_field", "INSTALL_READY_INVALID")]
    [InlineData("invalid_type", "INSTALL_READY_INVALID")]
    [InlineData("invalid_time", "INSTALL_READY_INVALID")]
    public void HistoricalWriterShapeIsOnlyAcceptedForAnUnregisteredFirstInstall(string scenario, string code)
    {
        var operation = scenario is "upgrade" or "uninstall" ? scenario : "install";
        SetUp(scenario == "installed" ? "upgrade" : operation);
        if (scenario == "occupied")
        {
            Directory.CreateDirectory(ProgramRoot);
            File.WriteAllText(Path.Combine(ProgramRoot, "existing.txt"), "preserve");
        }
        var historical = JsonNode.Parse(LegacyReady(scenario is "active" or "reused" ? Environment.ProcessId : 0x7fff_fffc))!.AsObject();
        switch (scenario)
        {
            case "wrong_data": historical["data_dir"] = Data + "-other"; break;
            case "wrong_ready": historical["ready_file"] = ReadyPath + ".other"; break;
            case "wrong_key": historical["api_key_file"] = Path.Combine(Data, "other-key.txt"); break;
            case "wrong_audit": historical["audit_log_path"] = Path.Combine(Data, "other-audit.jsonl"); break;
            case "missing_field": historical.Remove("interactive_desktop_ready"); break;
            case "partial_identity": historical["instance_id"] = ""; break;
            case "unknown_field": historical["unknown"] = 1; break;
            case "invalid_type": historical["session_id"] = "1"; break;
            case "invalid_time": historical["ready_at"] = "2000-01-01T00:00:00Z"; break;
        }
        File.WriteAllText(ReadyPath, scenario == "retired" ? LegacyReady(0x7fff_fffc) : historical.ToJsonString());
        var before = File.ReadAllBytes(ReadyPath);
        var registrations = 0;
        var probes = 0;
        var result = CliProgram.EvaluateInstallationSafety(ProgramRoot, operation, new()
        {
            LocalRoot = _local,
            MutexesPresent = () => scenario != "unleased",
            NoHostProcesses = _ => scenario != "host",
            NoInstallationRegistration = () =>
            {
                registrations++;
                return scenario != "registration" && (scenario != "registration_appears" || registrations == 1);
            },
            ReadInstallationRegistration = (_, _, _) => null,
            PortAbsent = scenario == "retired" ? new CliProgram.InstallationSafetyChecks().PortAbsent : () => scenario != "port",
            NoIntegrations = _ => scenario != "integration",
            CurrentUserSid = () => throw new InvalidOperationException("Legacy has no process SID."),
            ObserveReadyPid = pid =>
            {
                probes++;
                return scenario == "unknown" ? PidState.Unknown : CliProgram.ObserveInstallationReadyPid(pid);
            }
        });
        Assert.Equal(code, result.Code);
        Assert.Equal(scenario == "retired", result.Safe);
        Assert.Equal(before, File.ReadAllBytes(ReadyPath));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Data, "preserve.dat")));
        if (scenario is "retired" or "registration_appears") Assert.Equal(2, registrations);
        if (code is "INSTALL_READY_INVALID" || scenario is "registration" or "installed" or "upgrade" or "uninstall")
            Assert.Equal(0, probes);
    }

    [Fact]
    public void HistoricalRecordCannotBeReplacedDuringMaintenanceChecks()
    {
        SetUp("install");
        File.WriteAllText(ReadyPath, LegacyReady(0x7fff_fffc));
        var before = File.ReadAllBytes(ReadyPath);
        var result = CliProgram.EvaluateInstallationSafety(ProgramRoot, "install", new()
        {
            LocalRoot = _local, MutexesPresent = () => true, NoHostProcesses = _ => true,
            NoInstallationRegistration = () => true, PortAbsent = () => true,
            ObserveReadyPid = _ => PidState.Absent,
            NoIntegrations = _ =>
            {
                Assert.Throws<IOException>(() => File.Move(ReadyPath, ReadyPath + ".new"));
                return true;
            }
        });
        Assert.True(result.Safe);
        Assert.Equal(before, File.ReadAllBytes(ReadyPath));
        Assert.False(File.Exists(ReadyPath + ".new"));
    }

    [Fact]
    public void HistoricalRecordAppearingDuringMaintenanceBlocksTheCurrentAttempt()
    {
        SetUp("install");
        var content = LegacyReady(0x7fff_fffc);
        var result = Gate(() => File.WriteAllText(ReadyPath, content), "install");
        Assert.False(result.Safe);
        Assert.Equal("INSTALL_READY_CHANGED", result.Code);
        Assert.Equal(content, File.ReadAllText(ReadyPath));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Data, "preserve.dat")));
    }

    // 56d720d MarkReady and ReadySnapshot, with synthetic values and the same compact snake_case serializer.
    private string LegacyReady(int pid) => JsonSerializer.Serialize(new
    {
        Ready = true, Pid = pid, Port, ApiVersion = "v1", Mode = "tray",
        StartedAt = "2024-01-01T12:00:00Z", ReadyAt = "2024-01-01T12:00:01Z",
        StartupElapsedMs = 1070L, DataDir = Data, ApiKeyFile = Path.Combine(Data, "config", "api-key.txt"),
        AuditLogPath = Path.Combine(Data, "logs", "audit.jsonl"), ReadyFile = ReadyPath,
        NamedEvent = @"Local\AgentRecorderReady", InteractiveDesktopReady = true, SessionId = 1,
        WindowStation = "WinSta0", Desktop = "Default"
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    [Theory]
    [InlineData("wait-failed", false)]
    [InlineData("time-query-failed", false)]
    [InlineData("zero-exit", false)]
    [InlineData("reversed", false)]
    [InlineData("future", false)]
    [InlineData("not-signaled", true)]
    public void IncompleteOrConflictingProcessEvidenceCannotProveExit(string scenario, bool present)
    {
        var now = DateTime.UtcNow;
        var creation = now.AddMinutes(-2).ToFileTimeUtc();
        var exit = now.AddMinutes(-1).ToFileTimeUtc();
        uint wait = 0;
        switch (scenario)
        {
            case "wait-failed": wait = uint.MaxValue; break;
            case "time-query-failed": creation = exit = 0; break;
            case "zero-exit": exit = 0; break;
            case "reversed": exit = creation - 1; break;
            case "future": exit = now.AddDays(1).ToFileTimeUtc(); break;
            case "not-signaled": wait = 258; break;
        }
        Assert.Equal(present ? PidState.Present : PidState.Unknown, CliProgram.ClassifyInstallationPidWait(wait, creation, exit));
    }

    [Fact]
    public async Task LiveReusedPidBlocksUntilRetainedProcessObjectProvesExit()
    {
        SetUp("install");
        var start = new ProcessStartInfo("cmd.exe", "/d /q /c \"set /p ar_fixture= & exit /b 259\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
        };
        using var child = Process.Start(start)!;
        var retained = child.SafeHandle;
        File.WriteAllText(ReadyPath, LegacyReady(child.Id));
        var before = File.ReadAllBytes(ReadyPath);
        var portFree = true;
        var noIntegrations = true;
        var checks = new CliProgram.InstallationSafetyChecks
        {
            LocalRoot = _local, MutexesPresent = () => true, NoHostProcesses = _ => true,
            NoInstallationRegistration = () => true, PortAbsent = () => portFree,
            NoIntegrations = _ => noIntegrations
        };
        CliProgram.InstallationSafetyResult Gate() => CliProgram.EvaluateInstallationSafety(ProgramRoot, "install", checks);
        try
        {
            Assert.Equal(PidState.Present, CliProgram.ObserveInstallationReadyPid(child.Id));
            Assert.Equal("INSTALL_READY_PID_PRESENT", Gate().Code);
        }
        finally
        {
            child.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync(deadline.Token);
        }
        Assert.False(retained.IsClosed);
        Assert.Equal(259, child.ExitCode);
        Assert.Equal(PidState.Exited, CliProgram.ObserveInstallationReadyPid(child.Id));
        Assert.True(Gate().Safe);
        portFree = false;
        Assert.Equal("INSTALL_API_UNKNOWN", Gate().Code);
        portFree = true;
        noIntegrations = false;
        Assert.Equal("INSTALL_INTEGRATION_REQUIRES_REVIEW", Gate().Code);
        Assert.Equal(before, File.ReadAllBytes(ReadyPath));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Data, "preserve.dat")));
    }

    [Theory]
    [InlineData("upgrade")]
    [InlineData("uninstall")]
    public void RetainedLegacyRecordAllowsOwnedMaintenanceImmediatelyAfterFirstInstall(string operation)
    {
        SetUp("install");
        File.WriteAllText(ReadyPath, LegacyReady(0x7fff_fffc));
        var before = File.ReadAllBytes(ReadyPath);
        var registered = false;
        var productState = CliProgram.InstallationKeyState.Missing;
        var checks = new CliProgram.InstallationSafetyChecks
        {
            LocalRoot = _local, MutexesPresent = () => true, NoHostProcesses = _ => true,
            NoInstallationRegistration = () => CliProgram.FirstInstallRegistrationIsClear((_, key) =>
                key == CliProgram.ProductRegistrationKey ? productState :
                    registered ? CliProgram.InstallationKeyState.Populated : CliProgram.InstallationKeyState.Missing),
            ReadInstallationRegistration = (view, key, _) => registered
                ? OwnedRegistration(operation == "uninstall" ? CliProgram.InstallationRegistry64 : view, key)
                : null,
            PortAbsent = () => true, NoIntegrations = _ => true, ObserveReadyPid = _ => PidState.Exited
        };
        Assert.True(CliProgram.EvaluateInstallationSafety(ProgramRoot, "install", checks).Safe);
        SetUpOwnedInstallation();
        registered = true;
        productState = CliProgram.InstallationKeyState.Populated;
        if (operation == "uninstall")
            Assert.True(CliProgram.EvaluateInstallationSafety(ProgramRoot, "upgrade", checks).Safe);
        Assert.True(CliProgram.EvaluateInstallationSafety(ProgramRoot, operation, checks).Safe);
        if (operation == "uninstall")
        {
            // Model the prior uninstaller leaving only its dedicated, empty product key.
            File.Delete(Path.Combine(ProgramRoot, InstallationContext.MarkerName));
            File.Delete(Path.Combine(ProgramRoot, "unins000.exe"));
            File.Delete(Path.Combine(ProgramRoot, "unins000.dat"));
            registered = false;
            productState = CliProgram.InstallationKeyState.Empty;
            Assert.True(CliProgram.EvaluateInstallationSafety(ProgramRoot, "install", checks).Safe);
        }
        Assert.Equal(before, File.ReadAllBytes(ReadyPath));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Data, "preserve.dat")));
    }

    [Theory]
    [InlineData("missing_product", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("missing_uninstall", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("wrong_product", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("wrong_root", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("wrong_command", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("wrong_quiet", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("conflicting_view", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("missing_dat", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("unreadable", "INSTALL_READY_LEGACY_OWNER_UNKNOWN")]
    [InlineData("changed_registration", "INSTALL_READY_LEGACY_OWNER_CHANGED")]
    [InlineData("changed_marker", "INSTALL_READY_LEGACY_OWNER_CHANGED")]
    [InlineData("changed_uninstaller", "INSTALL_READY_LEGACY_OWNER_CHANGED")]
    [InlineData("active", "INSTALL_READY_PID_PRESENT")]
    [InlineData("unknown_pid", "INSTALL_READY_PID_UNKNOWN")]
    [InlineData("port", "INSTALL_API_UNKNOWN")]
    [InlineData("integration", "INSTALL_INTEGRATION_REQUIRES_REVIEW")]
    [InlineData("lease", "INSTALL_LEASE_MISSING")]
    public void LegacyMaintenanceRejectsMissingConflictingOrChangingEvidence(string scenario, string code)
    {
        SetUp("uninstall");
        SetUpOwnedInstallation();
        if (scenario == "missing_dat") File.Delete(Path.Combine(ProgramRoot, "unins000.dat"));
        File.WriteAllText(ReadyPath, LegacyReady(0x7fff_fffc));
        var before = File.ReadAllBytes(ReadyPath);
        var changed = false;
        var probes = 0;
        var result = CliProgram.EvaluateInstallationSafety(ProgramRoot, "uninstall", new()
        {
            LocalRoot = _local,
            MutexesPresent = () => scenario != "lease", NoHostProcesses = _ => true,
            ReadInstallationRegistration = (view, key, _) =>
            {
                if (scenario == "unreadable") throw new UnauthorizedAccessException();
                if (view == CliProgram.InstallationRegistry32)
                    return scenario == "conflicting_view" && key == CliProgram.ProductRegistrationKey ? ["foreign"] : null;
                var product = key == CliProgram.ProductRegistrationKey;
                var values = OwnedRegistration(view, key, changed && scenario == "changed_uninstaller" ? "001" : "000")!;
                if (product && (scenario == "missing_product" || (changed && scenario == "changed_registration"))) return null;
                if (!product && scenario == "missing_uninstall") return null;
                if (product && scenario == "wrong_product") values[0] = "foreign";
                if (!product && scenario == "wrong_root") values[0] += "other";
                if (!product && scenario == "wrong_command") values[2] += " /FORCE";
                if (!product && scenario == "wrong_quiet") values[3] = "\"C:\\foreign.exe\" /SILENT";
                return values;
            },
            PortAbsent = () => scenario != "port",
            NoIntegrations = _ =>
            {
                changed = true;
                if (scenario == "changed_marker") File.Delete(Path.Combine(ProgramRoot, InstallationContext.MarkerName));
                if (scenario == "changed_uninstaller") WriteUninstallerFiles("001");
                return scenario != "integration";
            },
            ObserveReadyPid = _ =>
            {
                probes++;
                return scenario switch { "active" => PidState.Present, "unknown_pid" => PidState.Unknown, _ => PidState.Absent };
            }
        });
        Assert.False(result.Safe);
        Assert.Equal(code, result.Code);
        Assert.Equal(code is "INSTALL_READY_LEGACY_OWNER_UNKNOWN" or "INSTALL_LEASE_MISSING" ? 0 : 1, probes);
        Assert.Equal(before, File.ReadAllBytes(ReadyPath));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Data, "preserve.dat")));
    }

    [Theory]
    [InlineData("0.1.19", "upgrade", "absent", "INSTALL_SAFE_WHILE_LEASE_HELD")]
    [InlineData("0.1.19", "upgrade", "historical", "INSTALL_SAFE_WHILE_LEASE_HELD")]
    [InlineData("0.1.19", "upgrade", "wrong_owner", "INSTALL_OWNER_UNKNOWN")]
    [InlineData("0.1.19", "upgrade", "wrong_display_version", "INSTALL_OWNER_UNKNOWN")]
    [InlineData("0.1.19", "upgrade", "changed_marker", "INSTALL_OWNER_CHANGED")]
    [InlineData("0.1.19", "upgrade", "changed_registration", "INSTALL_OWNER_CHANGED")]
    [InlineData("0.1.19", "install", "absent", "INSTALL_STATE_UNKNOWN")]
    [InlineData("0.1.19", "uninstall", "absent", "INSTALL_STATE_UNKNOWN")]
    [InlineData("0.1.18", "upgrade", "absent", "INSTALL_STATE_UNKNOWN")]
    [InlineData("0.1.21", "upgrade", "absent", "INSTALL_STATE_UNKNOWN")]
    [InlineData("0.1.19-rc.1", "upgrade", "absent", "INSTALL_STATE_UNKNOWN")]
    public void UpgradeAcceptsOnlyTheOwnedAcceptedPriorVersion(string version, string operation, string scenario, string code)
    {
        SetUp("upgrade");
        SetUpOwnedInstallation();
        var marker = Path.Combine(ProgramRoot, InstallationContext.MarkerName);
        var previous = JsonNode.Parse(InstallationContext.MarkerJson)!.AsObject();
        previous["version"] = version;
        File.WriteAllText(marker, previous.ToJsonString());
        if (scenario == "historical") File.WriteAllText(ReadyPath, LegacyReady(0x7fff_fffc));
        var changed = false;
        var result = CliProgram.EvaluateInstallationSafety(ProgramRoot, operation, new()
        {
            LocalRoot = _local, MutexesPresent = () => true, NoHostProcesses = _ => true,
            PortAbsent = () => true, ObserveReadyPid = _ => PidState.Exited,
            NoIntegrations = _ =>
            {
                changed = true;
                if (scenario == "changed_marker") File.WriteAllText(marker, InstallationContext.MarkerJson);
                return true;
            },
            ReadInstallationRegistration = (view, key, names) =>
            {
                var values = OwnedRegistration(view, key);
                if (values == null) return null;
                if (key == CliProgram.ProductRegistrationKey)
                {
                    if (scenario == "wrong_owner" || (changed && scenario == "changed_registration")) values[0] = "foreign";
                    return values;
                }
                Assert.Equal("DisplayVersion", names[^1]);
                return [.. values, scenario == "wrong_display_version" ? InstallationContext.ProductVersion : "0.1.19"];
            }
        });
        Assert.Equal(code == "INSTALL_SAFE_WHILE_LEASE_HELD", result.Safe);
        Assert.Equal(code, result.Code);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Data, "preserve.dat")));
    }

    private void SetUpOwnedInstallation()
    {
        Directory.CreateDirectory(ProgramRoot);
        File.WriteAllText(Path.Combine(ProgramRoot, InstallationContext.MarkerName), InstallationContext.MarkerJson);
        WriteUninstallerFiles("000");
    }

    private void WriteUninstallerFiles(string number)
    {
        File.WriteAllText(Path.Combine(ProgramRoot, $"unins{number}.exe"), "fixture");
        File.WriteAllText(Path.Combine(ProgramRoot, $"unins{number}.dat"), "fixture");
    }

    private string[]? OwnedRegistration(int view, string key, string number = "000")
    {
        if (view != CliProgram.InstallationRegistry64) return null;
        if (key == CliProgram.ProductRegistrationKey)
            return [InstallationContext.ProductId, ProgramRoot, Path.Combine(ProgramRoot, "AgentRecorder.Cli", "AgentRecorder.Cli.exe"),
                Path.Combine(ProgramRoot, "AGENT-INSTRUCTIONS.zh-CN.md"), Data];
        Assert.Equal(CliProgram.UninstallRegistrationKey, key);
        var command = '"' + Path.Combine(ProgramRoot, $"unins{number}.exe") + '"';
        return [ProgramRoot + "\\", ProgramRoot, command, command + " /SILENT"];
    }

    [Theory]
    [InlineData("empty", true)]
    [InlineData("missing", true)]
    [InlineData("partial", false)]
    [InlineData("unknown_or_link", false)]
    [InlineData("other_view_populated", false)]
    [InlineData("uninstall_empty", false)]
    [InlineData("uninstall_unknown", false)]
    [InlineData("changes_after_check", false)]
    public void FirstInstallRequiresClearRegistrationEvenWithoutReady(string scenario, bool safe)
    {
        SetUp("install");
        var changed = false;
        var result = CliProgram.EvaluateInstallationSafety(ProgramRoot, "install", new()
        {
            LocalRoot = _local, MutexesPresent = () => true, NoHostProcesses = _ => true,
            PortAbsent = () => true, NoIntegrations = _ => { changed = true; return true; },
            NoInstallationRegistration = () => CliProgram.FirstInstallRegistrationIsClear((view, key) =>
            {
                if (key == CliProgram.UninstallRegistrationKey)
                    return scenario switch
                    {
                        "uninstall_empty" => CliProgram.InstallationKeyState.Empty,
                        "uninstall_unknown" => CliProgram.InstallationKeyState.Unknown,
                        _ => CliProgram.InstallationKeyState.Missing
                    };
                return scenario switch
                {
                    "missing" => CliProgram.InstallationKeyState.Missing,
                    "partial" => CliProgram.InstallationKeyState.Populated,
                    "unknown_or_link" => CliProgram.InstallationKeyState.Unknown,
                    "other_view_populated" when view == CliProgram.InstallationRegistry32 => CliProgram.InstallationKeyState.Populated,
                    "changes_after_check" when changed => CliProgram.InstallationKeyState.Populated,
                    _ => CliProgram.InstallationKeyState.Empty
                };
            })
        });
        Assert.Equal(safe, result.Safe);
        Assert.Equal(safe ? "INSTALL_SAFE_WHILE_LEASE_HELD" : "INSTALL_FIRST_INSTALL_OWNER_UNKNOWN", result.Code);
        Assert.False(File.Exists(ReadyPath));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Data, "preserve.dat")));
    }

    [Fact]
    public void WindowsKeyCountsIncludeDefaultValuesAndSubkeys()
    {
        var path = @"Software\AgentRecorder.Task389W." + Guid.NewGuid().ToString("N");
        var user = Microsoft.Win32.Registry.CurrentUser;
        CliProgram.InstallationKeyState Observe() => CliProgram.ObserveInstallationKey(CliProgram.InstallationRegistry64, path);
        Assert.Equal(CliProgram.InstallationKeyState.Missing, Observe());
        try
        {
            using var key = user.CreateSubKey(path);
            Assert.Equal(CliProgram.InstallationKeyState.Empty, Observe());
            key.SetValue("", "");
            Assert.Equal(CliProgram.InstallationKeyState.Populated, Observe());
            key.DeleteValue("");
            using (key.CreateSubKey("Unowned")) { }
            Assert.Equal(CliProgram.InstallationKeyState.Populated, Observe());
            key.DeleteSubKey("Unowned");
            Assert.Equal(CliProgram.InstallationKeyState.Empty, Observe());
        }
        finally { user.DeleteSubKey(path, throwOnMissingSubKey: false); }
    }
}
