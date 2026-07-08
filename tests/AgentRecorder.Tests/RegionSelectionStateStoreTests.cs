using System;
using System.IO;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public class RegionSelectionStateStoreTests : IDisposable
{
    private readonly string _originalDataDir;
    private readonly string _testDataDir;
    private readonly bool _hadResolverOverride;

    public RegionSelectionStateStoreTests()
    {
        _originalDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR") ?? "";
        _testDataDir = Path.Combine(Path.GetTempPath(), $"region-state-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataDir);

        // Snapshot whether another test already set a resolver override.
        _hadResolverOverride = DataDirResolver.HasOverride;

        // Prefer resolver override for isolation; also set env var as a safety net.
        DataDirResolver.SetOverride(_testDataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _testDataDir, EnvironmentVariableTarget.Process);
    }

    public void Dispose()
    {
        // Restore resolver override state.
        if (_hadResolverOverride)
        {
            // We cannot recover the previous override value, but tests in this non-parallel
            // collection are expected to manage their own override. Re-set to the current
            // test dir temporarily, then clear it to keep the baseline clean.
            DataDirResolver.SetOverride(_testDataDir);
        }
        DataDirResolver.ClearOverride();

        // Restore environment variable.
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",
            string.IsNullOrEmpty(_originalDataDir) ? null : _originalDataDir,
            EnvironmentVariableTarget.Process);

        // Clean up the temporary directory only after the resolver override is cleared,
        // so no concurrent test can resolve a path inside it.
        try { if (Directory.Exists(_testDataDir)) Directory.Delete(_testDataDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var state = RegionSelectionStateStore.Load();
        Assert.Null(state);
    }

    [Fact]
    public void Load_MalformedJson_ReturnsNull()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegionSelectionStateStore.StatePath)!);
        File.WriteAllText(RegionSelectionStateStore.StatePath, "this is not json");

        var state = RegionSelectionStateStore.Load();
        Assert.Null(state);
    }

    [Fact]
    public void Load_InvalidBounds_ReturnsNull()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegionSelectionStateStore.StatePath)!);
        File.WriteAllText(RegionSelectionStateStore.StatePath,
            "{\"available\":true,\"display_id\":\"d1\",\"coordinate_space\":\"virtual_screen\",\"x\":0,\"y\":0,\"width\":10,\"height\":10,\"updated_at\":\"2026-01-01T00:00:00Z\",\"source\":\"test\"}");

        var state = RegionSelectionStateStore.Load();
        Assert.Null(state);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrip()
    {
        var state = new SelectedRegionState(
            Available: true,
            DisplayId: "display_1",
            CoordinateSpace: "virtual_screen",
            X: 100,
            Y: 150,
            Width: 800,
            Height: 600,
            UpdatedAt: "2026-07-08T10:00:00.000Z",
            Source: "test");

        RegionSelectionStateStore.Save(state);

        var loaded = RegionSelectionStateStore.Load();
        Assert.NotNull(loaded);
        Assert.True(loaded.Available);
        Assert.Equal("display_1", loaded.DisplayId);
        Assert.Equal("virtual_screen", loaded.CoordinateSpace);
        Assert.Equal(100, loaded.X);
        Assert.Equal(150, loaded.Y);
        Assert.Equal(800, loaded.Width);
        Assert.Equal(600, loaded.Height);
        Assert.Equal("2026-07-08T10:00:00.000Z", loaded.UpdatedAt);
        Assert.Equal("test", loaded.Source);
    }

    [Fact]
    public void Save_WritesSnakeCaseJson()
    {
        var state = new SelectedRegionState(
            Available: true,
            DisplayId: "display_1",
            CoordinateSpace: "virtual_screen",
            X: 100,
            Y: 150,
            Width: 800,
            Height: 600,
            UpdatedAt: "2026-07-08T10:00:00.000Z",
            Source: "test");

        RegionSelectionStateStore.Save(state);

        var json = File.ReadAllText(RegionSelectionStateStore.StatePath);
        Assert.Contains("\"display_id\"", json);
        Assert.Contains("\"coordinate_space\"", json);
        Assert.Contains("\"updated_at\"", json);
        Assert.DoesNotContain("\"DisplayId\"", json);
        Assert.DoesNotContain("\"CoordinateSpace\"", json);
        Assert.DoesNotContain("\"UpdatedAt\"", json);
    }

    [Fact]
    public void Save_32x32_ValidState_CanRoundTrip()
    {
        var state = new SelectedRegionState(
            Available: true,
            DisplayId: "display_1",
            CoordinateSpace: "virtual_screen",
            X: 0,
            Y: 0,
            Width: SelectedRegionState.MinRegionSize,
            Height: SelectedRegionState.MinRegionSize,
            UpdatedAt: "2026-01-01T00:00:00Z",
            Source: "test");

        RegionSelectionStateStore.Save(state);

        Assert.True(File.Exists(RegionSelectionStateStore.StatePath));
        var loaded = RegionSelectionStateStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(32, loaded.Width);
        Assert.Equal(32, loaded.Height);
    }

    [Fact]
    public void Save_InvalidState_DoesNotCreateFile()
    {
        var state = new SelectedRegionState(
            Available: true,
            DisplayId: "display_1",
            CoordinateSpace: "virtual_screen",
            X: 0,
            Y: 0,
            Width: 10,
            Height: 10,
            UpdatedAt: "2026-01-01T00:00:00Z",
            Source: "test");

        RegionSelectionStateStore.Save(state);

        Assert.False(File.Exists(RegionSelectionStateStore.StatePath));
    }

    [Fact]
    public void Clear_RemovesFile()
    {
        var state = new SelectedRegionState(
            Available: true,
            DisplayId: "display_1",
            CoordinateSpace: "virtual_screen",
            X: 100,
            Y: 150,
            Width: 800,
            Height: 600,
            UpdatedAt: "2026-07-08T10:00:00.000Z",
            Source: "test");

        RegionSelectionStateStore.Save(state);
        Assert.True(File.Exists(RegionSelectionStateStore.StatePath));

        RegionSelectionStateStore.Clear();
        Assert.False(File.Exists(RegionSelectionStateStore.StatePath));
    }
}
