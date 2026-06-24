using LosslessChecker.Models;

namespace LosslessChecker.Services.Analysis;

public class QualityScorer
{
    public (double scorePercent, string decision) Score(AnalysisResult r)
    {
        double score = 100;

        // DR: critical weight
        if (r.DynamicRange < 3) score -= 25;
        else if (r.DynamicRange < 5) score -= 15;
        else if (r.DynamicRange < 6) score -= 8;

        // Clipping: high weight
        if (r.ClippingPercent > 5) score -= 20;
        else if (r.ClippingPercent > 2) score -= 12;
        else if (r.ClippingPercent > 0.5) score -= 6;
        else if (r.ClippingPercent > 0) score -= 2;

        // True Peak ISP: high weight
        if (r.HasIsp)
        {
            score -= 8;
            if (r.TruePeakDb > 1.0) score -= 5;
        }

        // LUFS: medium weight
        if (r.IntegratedLufs > -7) score -= 15;
        else if (r.IntegratedLufs > -10) score -= 8;
        else if (r.IntegratedLufs > -14) score -= 3;

        // DC Offset: low weight
        if (Math.Abs(r.DcOffsetL) > 0.05 || Math.Abs(r.DcOffsetR) > 0.05) score -= 8;
        else if (Math.Abs(r.DcOffsetL) > 0.01 || Math.Abs(r.DcOffsetR) > 0.01) score -= 3;

        // Phase: medium weight
        if (r.Correlation < -0.5) score -= 12;
        else if (r.Correlation < 0) score -= 6;

        // LSB zero-pad: low weight
        if (r.LsbZeroPadded) score -= 5;

        score = Math.Max(0, Math.Min(100, score));

        // Decision
        string decision;
        if (r.Authenticity == "TRUE LOSSLESS")
        {
            if (score >= 80) decision = "KEEP";
            else if (score >= 50) decision = "KEEP";
            else decision = "KEEP (poor master)";
        }
        else if (r.Authenticity == "SUSPICIOUS")
            decision = "INVESTIGATE";
        else
            decision = "REPLACE";

        return (score, decision);
    }
}
