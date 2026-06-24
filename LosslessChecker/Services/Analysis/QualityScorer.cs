using LosslessChecker.Models;

namespace LosslessChecker.Services.Analysis;

public class QualityScorer
{
    public (int score, string decision) Score(AnalysisResult result)
    {
        int score = 10;

        if (result.DynamicRange < 6) score -= 3;
        else if (result.DynamicRange < 8) score -= 1;

        if (result.ClippingPercent > 0.5) score -= 2;
        else if (result.ClippingPercent > 0) score -= 1;

        if (result.HasIsp)
        {
            score -= 1;
            if (result.TruePeakDb > 1.0) score -= 1;
        }

        if (result.IntegratedLufs > -7) score -= 2;
        else if (result.IntegratedLufs > -10) score -= 1;

        if (Math.Abs(result.DcOffsetL) > 0.01 || Math.Abs(result.DcOffsetR) > 0.01)
            score -= 1;

        if (result.Correlation < 0) score -= 2;

        if (result.LsbZeroPadded) score -= 1;

        score = Math.Max(1, Math.Min(10, score));

        string decision = result.Authenticity switch
        {
            "TRUE LOSSLESS" when score >= 7 => "KEEP",
            "TRUE LOSSLESS" when score >= 4 => "KEEP",
            "TRUE LOSSLESS" => "KEEP (poor master)",
            "SUSPICIOUS" => "INVESTIGATE",
            _ => "REPLACE"
        };

        return (score, decision);
    }
}
