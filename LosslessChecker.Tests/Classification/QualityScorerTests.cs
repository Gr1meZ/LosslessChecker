using LosslessChecker.Models;
using LosslessChecker.Services.Analysis;
using Xunit;

namespace LosslessChecker.Tests.Classification;

public class QualityScorerTests
{
    private readonly QualityScorer _scorer = new();

    [Fact]
    public void Perfect_File_GetsMaxScore()
    {
        var r = new AnalysisResult
        {
            Authenticity = "TRUE", DynamicRange = 14, ClippingPercent = 0,
            HasIsp = false, IntegratedLufs = -18,
            DcOffsetL = 0, DcOffsetR = 0, Correlation = 1.0, LsbZeroPadded = false
        };
        var (score, decision) = _scorer.Score(r);
        Assert.Equal(100, score);
        Assert.Equal("KEEP", decision);
    }

    [Fact]
    public void Brickwall_Master_GetsLowQuality()
    {
        var r = new AnalysisResult
        {
            Authenticity = "TRUE", DynamicRange = 4, ClippingPercent = 2.0,
            HasIsp = true, TruePeakDb = 1.5, IntegratedLufs = -6,
            DcOffsetL = 0, DcOffsetR = 0, Correlation = 1.0, LsbZeroPadded = false
        };
        var (score, decision) = _scorer.Score(r);
        Assert.True(score <= 70);
        Assert.StartsWith("KEEP", decision);
    }

    [Fact]
    public void Fake_Lossless_GetsReplace()
    {
        var r = new AnalysisResult
        {
            Authenticity = "FALSE", DynamicRange = 12, ClippingPercent = 0,
            HasIsp = false, IntegratedLufs = -14,
            DcOffsetL = 0, DcOffsetR = 0, Correlation = 1.0, LsbZeroPadded = false
        };
        var (_, decision) = _scorer.Score(r);
        Assert.Equal("REPLACE", decision);
    }

    [Fact]
    public void TrueLossless_PoorMaster_NeverGetsReplace()
    {
        var r = new AnalysisResult
        {
            Authenticity = "TRUE", DynamicRange = 2, ClippingPercent = 10.0,
            HasIsp = true, TruePeakDb = 2.0, IntegratedLufs = -4,
            DcOffsetL = 0.01, Correlation = -0.5, LsbZeroPadded = true
        };
        var (_, decision) = _scorer.Score(r);
        Assert.NotEqual("REPLACE", decision);
        Assert.Contains("KEEP", decision);
    }

    [Fact]
    public void Suspicious_ReturnsInvestigate()
    {
        var r = new AnalysisResult
        {
            Authenticity = "UNCERTAIN", DynamicRange = 12, ClippingPercent = 0,
            HasIsp = false, IntegratedLufs = -14,
            DcOffsetL = 0, DcOffsetR = 0, Correlation = 1.0, LsbZeroPadded = false
        };
        var (score, decision) = _scorer.Score(r);
        Assert.True(score >= 90);
        Assert.Equal("INVESTIGATE", decision);
    }
}
