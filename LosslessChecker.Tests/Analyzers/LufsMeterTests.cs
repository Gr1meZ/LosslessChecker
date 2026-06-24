using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class LufsMeterTests
{
    private readonly LufsMeter _meter = new();

    [Fact]
    public void Sine_Tone_Returns_ReasonableLufs()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 3, 44100, 0.5);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _meter.Analyze(buffer);
        Assert.True(result.IntegratedLufs < -3);
        Assert.True(result.IntegratedLufs > -40);
    }

    [Fact]
    public void Loud_Sine_Has_HigherLufs_Than_Quiet()
    {
        var quiet = TestSignalGenerator.GenerateSine(1000, 3, 44100, 0.1);
        var loud = TestSignalGenerator.GenerateSine(1000, 3, 44100, 0.9);
        var rq = _meter.Analyze(new StereoBuffer(quiet, quiet, 44100));
        var rl = _meter.Analyze(new StereoBuffer(loud, loud, 44100));
        Assert.True(rl.IntegratedLufs > rq.IntegratedLufs);
    }

    [Fact]
    public void Silence_DoesNotCrash_ReturnsLowLufs()
    {
        var samples = new float[44100 * 3];
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _meter.Analyze(buffer);
        Assert.False(double.IsNaN(result.IntegratedLufs));
        Assert.False(double.IsInfinity(result.IntegratedLufs));
        Assert.True(result.IntegratedLufs < -40);
    }

    [Fact]
    public void WhiteNoise_Returns_ReasonableLufs()
    {
        var rng = new System.Random(42);
        var samples = new float[44100 * 3];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(rng.NextDouble() * 0.5 - 0.25);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _meter.Analyze(buffer);
        Assert.False(double.IsNaN(result.IntegratedLufs));
        Assert.False(double.IsInfinity(result.IntegratedLufs));
        Assert.True(result.IntegratedLufs > -40);
    }
}
