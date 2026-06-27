using LosslessChecker.Models;

namespace LosslessChecker.Services.Analysis;

public class QualityScorer
{
    private readonly ScoringProfile _p;

    public QualityScorer(ScoringProfile? profile = null) => _p = profile ?? ScoringProfile.Default;

    public (double scorePercent, string decision) Score(AnalysisResult r)
    {
        double score = 100;

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

        if (r.IsFakeStereo) score -= 5;

        if (r.HasAbruptEdges) score -= 3;

        score = Math.Max(0, Math.Min(100, score));

        string decision;
        if (r.Authenticity == "TRUE")
        {
            if (score >= _p.QualityExcellentThreshold) decision = "KEEP (Excellent)";
            else if (score >= _p.QualityKeepThreshold) decision = "KEEP (Good)";
            else decision = "KEEP (Fair)";
        }
        else if (r.Authenticity == "UNCERTAIN")
            decision = "INVESTIGATE";
        else
            decision = "REPLACE";

        return (score, decision);
    }

    public (double authenticityScore, double masteringScore, string authenticityVerdict, string masteringVerdict, string decision) ScoreFull(AnalysisResult r, LosslessScorer scorer)
    {
        double authScore = scorer.AuthenticityScore(r);
        double mastScore = scorer.MasteringScore(r);

        string authVerdict = authScore >= 70 ? "TRUE" : authScore >= 50 ? "UNCERTAIN" : "FALSE";
        if (r.IsCorrupted) { authVerdict = "CORRUPTED"; authScore = 0; mastScore = 0; }

        string mastVerdict = mastScore >= 80 ? "Excellent" : mastScore >= 50 ? "Good" : "Fair";

        string decision;
        if (r.IsCorrupted) decision = "CORRUPTED";
        else if (r.Authenticity == "MQA") decision = "MQA (needs decoder)";
        else if (authVerdict == "FALSE") decision = "REPLACE";
        else if (authVerdict == "UNCERTAIN") decision = "INVESTIGATE";
        else if (mastVerdict == "Excellent") decision = "KEEP (Excellent)";
        else if (mastVerdict == "Good") decision = "KEEP (Good)";
        else decision = "KEEP (Fair)";

        return (authScore, mastScore, authVerdict, mastVerdict, decision);
    }
}
