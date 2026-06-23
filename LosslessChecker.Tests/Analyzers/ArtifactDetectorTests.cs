using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class ArtifactDetectorTests
{
    private readonly ArtifactDetector _detector = new();

    [Fact]
    public void VeryShort_Clean_Sine_NoArtifacts()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 0.1, 44100);
        var (hasArtifacts, level, type) = _detector.Detect(samples, 44100, 22050);
        Assert.False(hasArtifacts);
        Assert.Equal("None", level);
        Assert.Equal("None", type);
    }

    [Fact]
    public void Short_Signal_NoArtifacts()
    {
        var samples = new float[100];
        var (hasArtifacts, level, type) = _detector.Detect(samples, 44100, 22050);
        Assert.False(hasArtifacts);
    }
}
