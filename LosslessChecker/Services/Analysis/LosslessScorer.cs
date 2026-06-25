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
        bool isHiRes = r.SampleRate >= 88200;

        // Cutoff penalty: only for non-natural shelves.
        // Natural rolloff (ADC anti-aliasing) is NORMAL — no penalty.
        //
        // For Hi-Res files (>=88.2kHz): real acoustic music rarely exceeds 30-40kHz.
        // ANY cutoff above 22kHz is acceptable — no penalty.
        // Only penalize brickwall at known upscale frequencies:
        //   ~16kHz  = MP3 128kbps upscale to Hi-Res
        //   ~20kHz  = MP3 320 / AAC 256 upscale
        //   ~22kHz  = CD upscale to Hi-Res
        if (r.ShelfType == "Brickwall")
        {
            if (isHiRes)
            {
                if (r.CutoffFrequency <= 17000) score -= 60;       // MP3 128 upscale
                else if (r.CutoffFrequency <= 20000) score -= 50;  // MP3 192-256 upscale
                else if (r.CutoffFrequency <= 22100) score -= 60;  // CD upscale
                // Above 22.1kHz — no penalty, genuine Hi-Res
            }
            else
            {
                if (ratio < 0.65) score -= 60;
                else if (ratio < 0.75) score -= 55;
                else if (ratio < 0.85) score -= 35;
                else if (ratio < 0.90) score -= 20;
                else if (ratio < 0.95) score -= 8;
            }
        }
        else if (r.ShelfType == "Filtered")
        {
            if (isHiRes)
            {
                if (r.CutoffFrequency <= 17000) score -= 35;
                else if (r.CutoffFrequency <= 20000) score -= 20;
                else if (r.CutoffFrequency <= 22100) score -= 30;
                // Above 22.1kHz filtered — gentle mastering LP, no penalty
            }
            else
            {
                if (ratio < 0.75) score -= 40;
                else if (ratio < 0.85) score -= 20;
                else if (ratio < 0.90) score -= 8;
            }
        }

        // Artifact penalty: only significant when paired with brickwall or suspicious cutoff
        bool isSuspiciousCutoff = isHiRes ? r.CutoffFrequency <= 22100 : ratio < 0.85;
        if (r.ArtifactLevel == "Strong" && (r.ShelfType == "Brickwall" || isSuspiciousCutoff)) score -= _p.ArtifactStrongPenalty;
        else if (r.ArtifactLevel == "Medium" && (r.ShelfType == "Brickwall" || isSuspiciousCutoff)) score -= _p.ArtifactMediumPenalty;
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

        // HF content check: is there anything above 22.05kHz?
        if (r.MaxHfDb < _p.HfDbVeryLow) score -= _p.HfVeryLowPenalty;
        else if (r.MaxHfDb < _p.HfDbLow) score -= _p.HfLowPenalty;
        else if (r.MaxHfDb < _p.HfDbMedium) score -= _p.HfMediumPenalty;

        // Cutoff penalty: only at upscale frequencies
        if (r.CutoffFrequency <= 17000) score -= _p.CutoffBelow22kPenalty;         // MP3 128 upscale
        else if (r.CutoffFrequency <= 20000) score -= _p.CutoffBelow22kPenalty / 2; // MP3 256-320 upscale
        else if (r.CutoffFrequency <= 22100) score -= _p.CutoffBelow22kPenalty;     // CD upscale

        if (r.IsUpscale) score -= _p.HiResUpscalePenalty;

        return Math.Max(0, Math.Min(100, score));
    }

    public string Classify(AnalysisResult r)
    {
        var s = Score(r);
        if (s >= _p.TrueLosslessThreshold) return "TRUE";
        if (s >= _p.SuspiciousThreshold) return "UNCERTAIN";
        return "FALSE";
    }
}
