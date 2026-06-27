using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class PhaseAnalyzerTests
{
    private readonly PhaseAnalyzer _analyzer = new();

    [Fact]
    public void Identical_Channels_Correlation_Near_1()
    {
        var buffer = TestSignalGenerator.GenerateStereo(1000, 2, 44100, invertRight: false);
        var result = _analyzer.Analyze(buffer);
        Assert.True(result.Correlation > 0.9);
    }

    [Fact]
    public void Inverted_Right_Correlation_Near_Minus1()
    {
        var buffer = TestSignalGenerator.GenerateStereo(1000, 2, 44100, invertRight: true);
        var result = _analyzer.Analyze(buffer);
        Assert.True(result.Correlation < -0.9);
    }

    [Fact]
    public void Inverted_Right_NotMonoCompatible()
    {
        var buffer = TestSignalGenerator.GenerateStereo(1000, 2, 44100, invertRight: true);
        var result = _analyzer.Analyze(buffer);
        Assert.False(result.IsMonoCompatible);
    }

    [Fact]
    public void MonoFileSkipsPhaseAndFakeStereoChecks()
    {
        var buffer = new StereoBuffer(
            new float[] { 0.5f, 0.3f, -0.2f },
            Array.Empty<float>(),
            44100);
        var analyzer = new PhaseAnalyzer();
        var result = analyzer.Analyze(buffer);
        Assert.Equal(1.0, result.Correlation);
        Assert.True(result.IsMonoCompatible);
    }
}
