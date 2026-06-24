using LosslessChecker.Models;

namespace LosslessChecker.Services.Analysis;

public class LosslessScorer
{
    private readonly ScoringProfile _p;

    public LosslessScorer(ScoringProfile? profile = null) => _p = profile ?? ScoringProfile.Default;

    public double Score(AnalysisResult r)
    {
        double score = 100;
        var nyquist = r.SampleRate / 2.0;
        double ratio = nyquist > 0 ? r.CutoffFrequency / nyquist : 1.0;

        // Cutoff penalty: only for non-natural shelves.
        // Natural rolloff (ADC anti-aliasing) is NORMAL — no penalty.
        if (r.ShelfType == "Brickwall")
        {
            if (ratio < 0.65) score -= 60;
            else if (ratio < 0.75) score -= 55;
            else if (ratio < 0.85) score -= 35;
            else if (ratio < 0.90) score -= 20;
            else if (ratio < 0.95) score -= 8;
        }
        else if (r.ShelfType == "Filtered")
        {
            if (ratio < 0.75) score -= 40;
            else if (ratio < 0.85) score -= 20;
            else if (ratio < 0.90) score -= 8;
        }

        // Artifact penalty: only significant when paired with brickwall or suspicious cutoff
        if (r.ArtifactLevel == "Strong" && (r.ShelfType == "Brickwall" || ratio < 0.85)) score -= _p.ArtifactStrongPenalty;
        else if (r.ArtifactLevel == "Medium" && (r.ShelfType == "Brickwall" || ratio < 0.85)) score -= _p.ArtifactMediumPenalty;
        else if (r.ArtifactLevel == "Weak") score -= _p.ArtifactWeakPenalty;

        if (r.LsbZeroPadded) score -= _p.LsbZeroPadPenalty;
        else if (r.BitDepthSuspicious) score -= _p.BitDepthSuspiciousPenalty;

        if (r.IsUpscale) score -= _p.UpscalePenalty;

        return Math.Max(0, Math.Min(100, score));
    }

    public double ScoreHiRes(AnalysisResult r)
    {
        if (r.SampleRate < 88200) return 0;
        double score = 100;

        if (r.MaxHfDb < _p.HfDbVeryLow) score -= _p.HfVeryLowPenalty;
        else if (r.MaxHfDb < _p.HfDbLow) score -= _p.HfLowPenalty;
        else if (r.MaxHfDb < _p.HfDbMedium) score -= _p.HfMediumPenalty;

        if (r.CutoffFrequency < 22000) score -= _p.CutoffBelow22kPenalty;
        if (r.IsUpscale) score -= _p.HiResUpscalePenalty;

        return Math.Max(0, Math.Min(100, score));
    }

    public string Classify(AnalysisResult r)
    {
        var s = Score(r);
        if (s >= 90) return "TRUE LOSSLESS";
        if (s >= _p.TrueLosslessThreshold) return "TRUE LOSSLESS";
        if (s >= _p.SuspiciousThreshold) return "SUSPICIOUS";
        return "FAKE LOSSLESS";
    }
}
