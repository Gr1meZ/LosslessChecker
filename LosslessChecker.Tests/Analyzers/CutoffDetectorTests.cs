using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class CutoffDetectorTests
{
    private readonly CutoffDetector _detector = new();

    [Fact]
    public void Cutoff_16kHz_Sweep_ReturnsCutoffNear16k()
    {
        var samples = TestSignalGenerator.GenerateSweep(1000, 16000, 5, 44100);
        var (cutoff, _, _) = _detector.DetectFull(samples, 44100);
        Assert.True(cutoff < 17000);
        Assert.True(cutoff > 14000);
    }

    [Fact]
    public void Cutoff_FullSpectrum_ReturnsNearNyquist()
    {
        var samples = TestSignalGenerator.GenerateSweep(1000, 22000, 5, 44100);
        var (cutoff, _, _) = _detector.DetectFull(samples, 44100);
        Assert.True(cutoff > 20000);
    }

    [Fact]
    public void EncoderMatch_16kHz_MapsToMp3_128()
    {
        var samples = TestSignalGenerator.GenerateSweep(1000, 16000, 5, 44100);
        var (cutoff, slope, _) = _detector.DetectFull(samples, 44100);
        var (match, _) = _detector.ClassifyCutoff(cutoff, slope, 44100);
        Assert.Contains("MP3 128", match);
    }

    [Fact]
    public void FakeHiRes_96kHz_BrickwallNearCdNyquist_ReturnsTrue()
    {
        Assert.True(_detector.IsFakeHiRes(22000, "Brickwall", 96000));
    }

    [Fact]
    public void NotFakeHiRes_96kHz_NaturalRolloff_ReturnsFalse()
    {
        Assert.False(_detector.IsFakeHiRes(20000, "Natural", 96000));
    }
}
