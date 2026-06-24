using LosslessChecker.Models;
using LosslessChecker.Services.Analysis;
using Xunit;

namespace LosslessChecker.Tests.Classification;

public class LosslessScorerTests
{
    private readonly LosslessScorer _scorer = new();

    [Fact]
    public void Score_TrueLossless_HfCutoff_ReturnsHighScore()
    {
        var r = new AnalysisResult { CutoffFrequency = 21800, SampleRate = 44100, ShelfType = "Natural", HasArtifacts = false };
        var score = _scorer.Score(r);
        Assert.True(score >= 70);
    }

    [Fact]
    public void Score_FakeLossless_16kHz_WithArtifacts_ReturnsLowScore()
    {
        var r = new AnalysisResult { CutoffFrequency = 16000, HasArtifacts = true, ShelfType = "Brickwall", SampleRate = 44100 };
        var score = _scorer.Score(r);
        Assert.True(score < 50);
    }

    [Fact]
    public void Classify_TrueLossless_HfCutoff_ReturnsTrue()
    {
        var r = new AnalysisResult { CutoffFrequency = 21800, SampleRate = 44100, ShelfType = "Natural", HasArtifacts = false };
        Assert.Equal("TRUE", _scorer.Classify(r));
    }

    [Fact]
    public void Classify_FakeLossless_16kHz_WithArtifacts_ReturnsFake()
    {
        var r = new AnalysisResult { CutoffFrequency = 16000, HasArtifacts = true, ShelfType = "Brickwall", SampleRate = 44100 };
        Assert.Equal("FALSE", _scorer.Classify(r));
    }

    [Fact]
    public void Classify_Suspicious_20kHz_Cutoff_ReturnsSuspicious()
    {
        var r = new AnalysisResult { CutoffFrequency = 20500, SampleRate = 44100, HasArtifacts = false, ShelfType = "Natural" };
        var result = _scorer.Classify(r);
        Assert.True(result == "UNCERTAIN" || result == "TRUE");
    }

    [Fact]
    public void ScoreHiRes_44kHz_ReturnsZero()
    {
        var r = new AnalysisResult { SampleRate = 44100, MaxHfDb = -10, CutoffFrequency = 22000, IsUpscale = false };
        Assert.Equal(0, _scorer.ScoreHiRes(r));
    }

    [Fact]
    public void ScoreHiRes_96kHz_TrueHiRes_ReturnsHighScore()
    {
        var r = new AnalysisResult { SampleRate = 96000, MaxHfDb = -10, CutoffFrequency = 40000, IsUpscale = false };
        var score = _scorer.ScoreHiRes(r);
        Assert.True(score >= 70);
    }

    [Fact]
    public void ScoreHiRes_96kHz_Upscale_ReturnsLowScore()
    {
        var r = new AnalysisResult { SampleRate = 96000, MaxHfDb = -60, CutoffFrequency = 20000, IsUpscale = true };
        var score = _scorer.ScoreHiRes(r);
        Assert.True(score < 30);
    }
}
