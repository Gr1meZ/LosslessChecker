using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class TruePeakDetectorTests
{
    private readonly TruePeakDetector _detector = new();

    [Fact]
    public void Clipped_Sine_ShowsClipping()
    {
        var samples = TestSignalGenerator.GenerateClippedSine(1000, 2, 44100, 2.0);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _detector.Analyze(buffer);
        Assert.True(result.ClippingPercent > 0);
    }

    [Fact]
    public void Clean_Sine_ShowsNoIsp()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 2, 44100, 0.5);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _detector.Analyze(buffer);
        Assert.False(result.HasIsp);
    }

    [Fact]
    public void Clipped_Sine_HasSamplePeakAt0dB()
    {
        var samples = TestSignalGenerator.GenerateClippedSine(1000, 2, 44100, 2.0);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _detector.Analyze(buffer);
        Assert.True(result.SamplePeakDbL > -0.1);
        Assert.True(result.ClippingPercent > 0);
    }
}
