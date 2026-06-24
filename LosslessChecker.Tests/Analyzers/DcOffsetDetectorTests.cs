using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class DcOffsetDetectorTests
{
    private readonly DcOffsetDetector _detector = new();

    [Fact]
    public void Clean_Sine_NoDcOffset()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 2, 44100);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _detector.Analyze(buffer);
        Assert.False(result.HasDcOffset);
    }

    [Fact]
    public void DcOffset_002Percent_AboveThreshold_Detected()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 2, 44100);
        var offset = TestSignalGenerator.AddDcOffset(samples, 0.02);
        var buffer = new StereoBuffer(offset, offset, 44100);
        var result = _detector.Analyze(buffer);
        Assert.True(result.HasDcOffset);
    }
}
