using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class DrMeterTests
{
    private readonly DrMeter _meter = new();

    [Fact]
    public void Quiet_Sine_HasLowDr()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 3, 44100, 0.1);
        var (dr, _, _) = _meter.Analyze(samples, 44100);
        Assert.True(dr < 20);
    }

    [Fact]
    public void Clipped_Sine_HasClipping()
    {
        var samples = TestSignalGenerator.GenerateClippedSine(1000, 3, 44100, 2.0);
        var (_, _, clip) = _meter.Analyze(samples, 44100);
        Assert.True(clip > 0);
    }

    [Fact]
    public void Stereo_Analysis_Returns_DrResult()
    {
        var buffer = TestSignalGenerator.GenerateStereo(1000, 3, 44100);
        var result = _meter.AnalyzeStereo(buffer);
        Assert.True(result.Dr >= 0);
        Assert.True(result.DrLeft >= 0);
    }
}
