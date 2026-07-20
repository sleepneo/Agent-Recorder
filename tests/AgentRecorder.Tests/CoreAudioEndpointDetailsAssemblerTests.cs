using System;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Unit tests for <see cref="CoreAudioEndpointDetailsAssembler"/>, the internal
/// boundary that assembles endpoint details from raw state and read-only volume
/// queries. These tests exercise every partial-result branch deterministically
/// without touching real COM objects.
/// </summary>
public class CoreAudioEndpointDetailsAssemblerTests
{
    [Fact]
    public void Assemble_StateActiveMuteTrueVolumeThrows_PreservesStateAndMute()
    {
        var details = CoreAudioEndpointDetailsAssembler.Assemble(
            DeviceState.Active,
            new object(),
            _ => true,
            _ => throw new InvalidOperationException("COM failure"));

        Assert.Equal("active", details.State);
        Assert.True(details.IsMuted);
        Assert.Null(details.VolumePercent);
    }

    [Fact]
    public void Assemble_StateActiveMuteThrowsVolumeSeven_PreservesStateAndVolume()
    {
        var details = CoreAudioEndpointDetailsAssembler.Assemble(
            DeviceState.Active,
            new object(),
            _ => throw new InvalidOperationException("COM failure"),
            _ => 7);

        Assert.Equal("active", details.State);
        Assert.Null(details.IsMuted);
        Assert.Equal(7, details.VolumePercent);
    }

    [Fact]
    public void Assemble_StateFailsMuteFalseVolumeFull_PreservesMuteAndVolume()
    {
        var details = CoreAudioEndpointDetailsAssembler.Assemble(
            null,
            new object(),
            _ => false,
            _ => 100);

        Assert.Null(details.State);
        Assert.False(details.IsMuted);
        Assert.Equal(100, details.VolumePercent);
    }

    [Fact]
    public void Assemble_ActivationFailsStateActive_PreservesStateOnly()
    {
        var details = CoreAudioEndpointDetailsAssembler.Assemble(
            DeviceState.Active,
            null,
            _ => true,
            _ => 50);

        Assert.Equal("active", details.State);
        Assert.Null(details.IsMuted);
        Assert.Null(details.VolumePercent);
    }

    [Fact]
    public void Assemble_AllSuccessfulActiveNotMutedHalfVolume_ReturnsAllFields()
    {
        var details = CoreAudioEndpointDetailsAssembler.Assemble(
            DeviceState.Active,
            new object(),
            _ => false,
            _ => 50);

        Assert.Equal("active", details.State);
        Assert.False(details.IsMuted);
        Assert.Equal(50, details.VolumePercent);
    }

    [Fact]
    public void Assemble_StateDisabled_ReturnsInactiveState()
    {
        var details = CoreAudioEndpointDetailsAssembler.Assemble(
            DeviceState.Disabled,
            new object(),
            _ => true,
            _ => 33);

        Assert.Equal("inactive", details.State);
        Assert.True(details.IsMuted);
        Assert.Equal(33, details.VolumePercent);
    }

    [Fact]
    public void Assemble_MuteReturnsNullButVolumeSucceeds_PreservesVolume()
    {
        var details = CoreAudioEndpointDetailsAssembler.Assemble(
            DeviceState.Active,
            new object(),
            _ => null,
            _ => 42);

        Assert.Equal("active", details.State);
        Assert.Null(details.IsMuted);
        Assert.Equal(42, details.VolumePercent);
    }

    [Fact]
    public void Assemble_VolumeReturnsNullButMuteSucceeds_PreservesMute()
    {
        var details = CoreAudioEndpointDetailsAssembler.Assemble(
            DeviceState.Active,
            new object(),
            _ => true,
            _ => null);

        Assert.Equal("active", details.State);
        Assert.True(details.IsMuted);
        Assert.Null(details.VolumePercent);
    }

    [Fact]
    public void Assemble_MuteAndVolumeBothThrow_StatePreservedOthersNull()
    {
        var details = CoreAudioEndpointDetailsAssembler.Assemble(
            DeviceState.Active,
            new object(),
            _ => throw new InvalidOperationException("mute failed"),
            _ => throw new InvalidOperationException("volume failed"));

        Assert.Equal("active", details.State);
        Assert.Null(details.IsMuted);
        Assert.Null(details.VolumePercent);
    }

    [Fact]
    public void Assemble_StateNullAndActivationFails_AllUnknown()
    {
        var details = CoreAudioEndpointDetailsAssembler.Assemble(
            null,
            null,
            _ => throw new InvalidOperationException("mute failed"),
            _ => throw new InvalidOperationException("volume failed"));

        Assert.Null(details.State);
        Assert.Null(details.IsMuted);
        Assert.Null(details.VolumePercent);
    }
}
