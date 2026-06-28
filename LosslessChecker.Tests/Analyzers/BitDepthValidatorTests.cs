using LosslessChecker.Models;
using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class BitDepthValidatorTests
{
    private readonly BitDepthValidator _validator = new();

    [Fact]
    public void ZeroPadded_24Bit_DetectedByLsbCheck()
    {
        var samples = TestSignalGenerator.GenerateZeroPadded24Bit(1000, 3, 44100);
        bool isPadded = _validator.CheckLsbZeroPadded(samples, 24);
        Assert.True(isPadded);
    }

    [Fact]
    public void FullScale_Sine_24Bit_NotPadded()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 3, 44100, 1.0);
        bool isPadded = _validator.CheckLsbZeroPadded(samples, 24);
        Assert.False(isPadded);
    }

    [Fact]
    public void Not24Bit_SkipsCheck()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 3, 44100);
        bool isPadded = _validator.CheckLsbZeroPadded(samples, 16);
        Assert.False(isPadded);
    }

    [Fact]
    public void BrickwalledEdmDoesNotTriggerSuspiciousBitDepth()
    {
        var rng = new Random(42);
        int sampleRate = 44100;
        int n = sampleRate * 10;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = (float)(rng.NextDouble() * 2 - 1) * 0.5f;

        var validator = new BitDepthValidator();
        validator.Reset();
        validator.AddChunk(samples);
        var result = validator.GetResult(16);

        Assert.False(result.IsSuspicious);
    }

    [Fact]
    public void TpdfDitherDoesNotTriggerLsbZeroPad()
    {
        var rng = new Random(42);
        int n = 44100 * 2;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            double signal = Math.Sin(2 * Math.PI * 1000 * i / 44100);
            double u1 = rng.NextDouble() * 2 - 1;
            double u2 = rng.NextDouble() * 2 - 1;
            double tpdf = (u1 + u2) * (1.0 / 8388608.0);
            int sample24 = (int)Math.Round((signal + tpdf) * 8388608.0);
            samples[i] = (float)(sample24 / 8388608.0);
        }
        var validator = new BitDepthValidator();
        bool lsbZero = validator.CheckLsbZeroPadded(samples, 24);
        Assert.False(lsbZero, "TPDF dither should not trigger LSB zero-padding");
    }
}
