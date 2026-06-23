using LosslessChecker.Models;
using LosslessChecker.Services.Analysis;
using Xunit;

namespace LosslessChecker.Tests.Classification;

public class AuthenticityClassifierTests
{
    private readonly AuthenticityClassifier _classifier = new();

    [Fact]
    public void TrueLossless_HfCutoff_ReturnsTrue()
    {
        var r = new AnalysisResult { CutoffFrequency = 21800, SampleRate = 44100, ShelfType = "Natural", HasArtifacts = false };
        Assert.Equal("TRUE LOSSLESS", _classifier.Classify(r));
    }

    [Fact]
    public void FakeLossless_16kHz_WithArtifacts_ReturnsFake()
    {
        var r = new AnalysisResult { CutoffFrequency = 16000, HasArtifacts = true, ShelfType = "Brickwall", SampleRate = 44100 };
        Assert.Equal("FAKE LOSSLESS", _classifier.Classify(r));
    }

    [Fact]
    public void FakeHiRes_96kHz_CutoffBelow22k_ReturnsFakeHiRes()
    {
        var r = new AnalysisResult { CutoffFrequency = 21999, SampleRate = 96000 };
        Assert.Equal("FAKE HI-RES", _classifier.Classify(r));
    }

    [Fact]
    public void Suspicious_20kHs_Cutoff_ReturnsSuspicious()
    {
        var r = new AnalysisResult { CutoffFrequency = 20500, SampleRate = 44100, HasArtifacts = false, ShelfType = "Natural" };
        Assert.Equal("SUSPICIOUS", _classifier.Classify(r));
    }

    [Fact]
    public void Upscale_ReturnsSuspicious()
    {
        var r = new AnalysisResult { CutoffFrequency = 30000, SampleRate = 96000, IsUpscale = true };
        Assert.Equal("SUSPICIOUS", _classifier.Classify(r));
    }
}
