using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class ContainerAnalyzerTests
{
    private readonly ContainerAnalyzer _analyzer = new();

    [Fact]
    public void CdAligned_DivisibleBy588_Sr44100_ReturnsTrue()
    {
        var samples = new float[588 * 10];
        var result = _analyzer.Analyze("dummy.mp3", samples, 44100, 5880);
        Assert.True(result.IsCdAligned);
    }

    [Fact]
    public void CdAligned_NotDivisibleBy588_Sr44100_ReturnsFalse()
    {
        var samples = new float[5881];
        var result = _analyzer.Analyze("dummy.mp3", samples, 44100, 5881);
        Assert.False(result.IsCdAligned);
    }

    [Fact]
    public void CdAligned_UsesTotalSamplesOverArrayLength()
    {
        var samples = new float[1000];
        var result = _analyzer.Analyze("dummy.mp3", samples, 44100, 588);
        Assert.True(result.IsCdAligned);
    }

    [Fact]
    public void CdAligned_UsesArrayLengthWhenTotalSamplesZero()
    {
        var samples = new float[588 * 5];
        var result = _analyzer.Analyze("dummy.mp3", samples, 44100);
        Assert.True(result.IsCdAligned);
    }

    [Fact]
    public void CdAligned_Non44100SampleRate_ReturnsFalse()
    {
        var samples = new float[588];
        var result = _analyzer.Analyze("dummy.mp3", samples, 48000, 588);
        Assert.False(result.IsCdAligned);
    }

    [Fact]
    public void MqaDetection_AlternatingLsbPattern_ReturnsTrue()
    {
        var samples = GenerateMqaPatternSamples(16000);
        var result = _analyzer.Analyze("dummy.flac", samples, 44100);
        Assert.True(result.IsMqa);
        Assert.NotEmpty(result.MqaDetails);
    }

    [Fact]
    public void MqaDetection_NormalSine_ReturnsFalse()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 0.5, 44100);
        var result = _analyzer.Analyze("dummy.flac", samples, 44100);
        Assert.False(result.IsMqa);
        Assert.Empty(result.MqaDetails);
    }

    [Fact]
    public void MqaDetection_SampleRateAbove48000_ReturnsFalse()
    {
        var samples = GenerateMqaPatternSamples(16000);
        var result = _analyzer.Analyze("dummy.flac", samples, 96000);
        Assert.False(result.IsMqa);
    }

    [Fact]
    public void MqaDetection_ShortSamples_ReturnsFalse()
    {
        var samples = GenerateMqaPatternSamples(500);
        var result = _analyzer.Analyze("dummy.flac", samples, 44100);
        Assert.False(result.IsMqa);
    }

    [Fact]
    public void HdcdDetection_LsbPattern_ReturnsTrue()
    {
        var samples = GenerateHdcdPatternSamples(5880);
        var result = _analyzer.Analyze("dummy.flac", samples, 44100);
        Assert.True(result.IsHdcd);
    }

    [Fact]
    public void HdcdDetection_NormalSine_ReturnsFalse()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 5880.0 / 44100, 44100);
        var result = _analyzer.Analyze("dummy.flac", samples, 44100);
        Assert.False(result.IsHdcd);
    }

    [Fact]
    public void HdcdDetection_ShortSamples_ReturnsFalse()
    {
        var samples = GenerateHdcdPatternSamples(1000);
        var result = _analyzer.Analyze("dummy.flac", samples, 44100);
        Assert.False(result.IsHdcd);
    }

    [Fact]
    public void HdcdDetection_Non44100SampleRate_ReturnsFalse()
    {
        var samples = GenerateHdcdPatternSamples(5880);
        var result = _analyzer.Analyze("dummy.flac", samples, 48000);
        Assert.False(result.IsHdcd);
    }

    [Fact]
    public void HdcdDetection_NonFlacOrWavExtension_ReturnsFalse()
    {
        var samples = GenerateHdcdPatternSamples(5880);
        var result = _analyzer.Analyze("dummy.mp3", samples, 44100);
        Assert.False(result.IsHdcd);
    }

    [Fact]
    public void EmptySamples_NoFalsePositives()
    {
        var samples = System.Array.Empty<float>();
        var result = _analyzer.Analyze("dummy.flac", samples, 44100);
        Assert.False(result.IsMqa);
        Assert.False(result.IsHdcd);
    }

    private static float[] GenerateMqaPatternSamples(int count)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = 1.0f / 8388607.0f;
        return samples;
    }

    private static float[] GenerateHdcdPatternSamples(int count)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = 1.0f / 32767.0f;
        return samples;
    }
}
