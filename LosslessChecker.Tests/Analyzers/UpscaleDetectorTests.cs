using LosslessChecker.Services;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class UpscaleDetectorTests
{
    private readonly UpscaleDetector _detector = new();

    [Fact]
    public void StandardRate_SkipsCheck()
    {
        var spectrum = new double[2048];
        var (isUpscale, _, _) = _detector.Detect(spectrum, 44100);
        Assert.False(isUpscale);
    }

    [Fact]
    public void HiRes_NoHfContent_ReturnsUpscale()
    {
        var spectrum = new double[4096];
        for (int i = 0; i < 1024; i++)
            spectrum[i] = 1.0;
        var (isUpscale, _, _) = _detector.Detect(spectrum, 96000);
        Assert.True(isUpscale);
    }
}
