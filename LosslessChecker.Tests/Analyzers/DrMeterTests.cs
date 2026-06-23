using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class DrMeterTests
{
    private readonly DrMeter _meter = new();

    [Fact]
    public void Quiet_Sine_HasSaneDr()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 10, 44100, 0.1); // 10s for 3s blocks
        var (dr, _, _) = _meter.Analyze(samples, 44100);
        // Pure sine: peak = amp, RMS = amp/sqrt(2), peak-to-RMS = 3.0 dB
        Assert.True(dr > 2.0, $"Expected DR ~3, got {dr}");
        Assert.True(dr < 5.0, $"Expected DR ~3, got {dr}");
    }

    [Fact]
    public void Clipped_Sine_HasClipping()
    {
        var samples = TestSignalGenerator.GenerateClippedSine(1000, 10, 44100, 2.0);
        var (_, _, clip) = _meter.Analyze(samples, 44100);
        Assert.True(clip > 0);
    }

    [Fact]
    public void Stereo_Analysis_Returns_DrResult()
    {
        var buffer = TestSignalGenerator.GenerateStereo(1000, 10, 44100);
        var result = _meter.AnalyzeStereo(buffer);
        Assert.True(result.Dr > 0);
        Assert.True(result.DrLeft > 0);
    }
}
