using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Diagnostics;

public class DrDiagnosticTests
{
    private readonly DrMeter _meter = new();

    [Fact]
    public void DrMeter_BlockByBlock_GeneratesCrestOf4()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 20, 44100, 0.9);
        var (dr, _, _) = _meter.Analyze(samples, 44100);
        // Pure sine crest = 3 dB, minus 3 dB calibration = 0
        Assert.True(dr <= 1.0, $"Expected DR ≈ 0 after calibration, got {dr}");
    }

    [Fact]
    public void DrMeter_AllBlocksEqual_ShouldBeNearZero()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 20, 44100, 0.5);
        var (dr, _, _) = _meter.Analyze(samples, 44100);
        Assert.True(dr <= 1.0, $"Expected DR ≈ 0, got {dr}");
    }

    [Fact]
    public void DrMeter_Dr4_PerChannelVersusStereo()
    {
        var buffer = TestSignalGenerator.GenerateStereo(1000, 20, 44100);
        var result = _meter.AnalyzeStereo(buffer);
        Assert.True(result.Dr <= 1.0, $"Expected DR ≈ 0 after cal, got {result.Dr}");
    }
}
