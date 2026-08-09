using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class WgcEncoderModePolicyTests
{
    [Theory]
    [InlineData(null, WgcEncoderMode.Software)]
    [InlineData("", WgcEncoderMode.Software)]
    [InlineData("   ", WgcEncoderMode.Software)]
    [InlineData("software", WgcEncoderMode.Software)]
    [InlineData(" SoFtWaRe ", WgcEncoderMode.Software)]
    [InlineData("hardware-preferred", WgcEncoderMode.HardwarePreferred)]
    [InlineData(" HARDWARE-PREFERRED ", WgcEncoderMode.HardwarePreferred)]
    public void NormalizeAcceptsOnlyTheTwoSupportedValues(string? raw, WgcEncoderMode expected)
    {
        Assert.Equal(expected, WgcEncoderModePolicy.Normalize(raw));
    }

    [Theory]
    [InlineData("hardware")]
    [InlineData("hardware-preferred-extra")]
    [InlineData("invalid")]
    public void NormalizeRejectsInvalidValues(string raw)
    {
        Assert.Throws<ArgumentException>(() => WgcEncoderModePolicy.Normalize(raw));
    }

    [Fact]
    public void ArgumentValueIsStableAndDoesNotExposeHardwareAsASelectedMode()
    {
        Assert.Equal("software", WgcEncoderModePolicy.ToArgumentValue(WgcEncoderMode.Software));
        Assert.Equal("hardware-preferred", WgcEncoderModePolicy.ToArgumentValue(WgcEncoderMode.HardwarePreferred));
    }
}
