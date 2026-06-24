using LosslessChecker.Models;

namespace LosslessChecker.Services.Analysis;

public class QualityScorer
{
    private readonly ScoringProfile _p;

    public QualityScorer(ScoringProfile? profile = null) => _p = profile ?? ScoringProfile.Default;

    public (double scorePercent, string decision) Score(AnalysisResult r)
    {
        double score = 100;

        foreach (var (threshold, penalty) in _p.DrThresholds)
            if (r.DynamicRange < threshold) { score -= penalty; break; }

        foreach (var (threshold, penalty) in _p.ClippingThresholds)
            if (r.ClippingPercent > threshold) { score -= penalty; break; }

        if (r.HasIsp)
        {
            score -= _p.IspBasePenalty;
            if (r.TruePeakDb > 1.0) score -= _p.IspExtraPenalty;
        }

        foreach (var (threshold, penalty) in _p.LufsThresholds)
            if (r.IntegratedLufs > threshold) { score -= penalty; break; }

        if (Math.Abs(r.DcOffsetL) > _p.DcOffsetHighThreshold || Math.Abs(r.DcOffsetR) > _p.DcOffsetHighThreshold) score -= _p.DcOffsetHighPenalty;
        else if (Math.Abs(r.DcOffsetL) > _p.DcOffsetLowThreshold || Math.Abs(r.DcOffsetR) > _p.DcOffsetLowThreshold) score -= _p.DcOffsetLowPenalty;

        if (r.Correlation < _p.PhaseBadThreshold) score -= _p.PhaseBadPenalty;
        else if (r.Correlation < 0) score -= _p.PhaseSuspiciousPenalty;

        if (r.LsbZeroPadded) score -= _p.LsbZeroPadQualityPenalty;

        score = Math.Max(0, Math.Min(100, score));

        string decision;
        if (r.Authenticity == "TRUE")
        {
            if (score >= _p.QualityExcellentThreshold) decision = "KEEP";
            else if (score >= _p.QualityKeepThreshold) decision = "KEEP";
            else decision = "KEEP (poor master)";
        }
        else if (r.Authenticity == "UNCERTAIN")
            decision = "INVESTIGATE";
        else
            decision = "REPLACE";

        return (score, decision);
    }
}
