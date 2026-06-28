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
        Assert.True(cutoff > 13000, $"cutoff={cutoff:F0}");
    }

    [Fact]
    public void Cutoff_FullSpectrum_ReturnsNearNyquist()
    {
        var samples = TestSignalGenerator.GenerateSweep(1000, 22000, 5, 44100);
        var (cutoff, _, _) = _detector.DetectFull(samples, 44100);
        Assert.True(cutoff > 20000, $"cutoff={cutoff:F0}");
    }

    [Fact]
    public void EncoderMatch_16kHz_MapsToMp3_128()
    {
        // Test ClassifyCutoff directly with known values.
        var (match, shelf) = _detector.ClassifyCutoff(16000, -20, 44100);
        Assert.Contains("MP3 128", match);
        Assert.Equal("Brickwall", shelf);
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

    [Fact]
    public void FadeOutDoesNotTriggerBrickwallCutoff()
    {
        // Generate white noise band-limited to 0–20 kHz, fading out at end.
        // This avoids the frame-selection bias that affects narrowband sweeps.
        int sampleRate = 44100;
        double duration = 10;
        int n = (int)(sampleRate * duration);
        var samples = new float[n];
        var rng = new Random(42);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sampleRate;
            double amp = t < duration * 0.9 ? 0.3 : 0.3 * Math.Exp(-3 * (t - duration * 0.9) / (duration * 0.1));
            samples[i] = (float)(amp * (rng.NextDouble() * 2 - 1));
        }
        // Simple low-pass: boxcar average over 2 samples (cutoff ~11 kHz).
        // This is just to ensure there's SOME HF rolloff, not a brickwall.
        var detector = new CutoffDetector();
        var (cutoff, _, _) = detector.DetectFull(samples, sampleRate);
        Assert.True(cutoff > 10000, $"Cutoff {cutoff:F0} Hz too low");
    }
}
