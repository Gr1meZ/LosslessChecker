using LosslessChecker.Models;
using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class BitDepthValidatorTests
{
    private readonly BitDepthValidator _validator = new();

    [Fact]
    public void ZeroPadded_24Bit_IsNotDetectedAsTrue24Bit()
    {
        var samples = TestSignalGenerator.GenerateZeroPadded24Bit(1000, 3, 44100);
        var (isSuspicious, verdict, _, _, _) = _validator.ValidateStereo(
            new LosslessChecker.Models.StereoBuffer(samples, samples, 44100), 24);
        Assert.True(isSuspicious);
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
}
