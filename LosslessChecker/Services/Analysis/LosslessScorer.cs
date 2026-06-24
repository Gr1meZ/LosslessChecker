using LosslessChecker.Models;

namespace LosslessChecker.Services.Analysis;

public class LosslessScorer
{
    public double Score(AnalysisResult r)
    {
        double score = 100;
        var nyquist = r.SampleRate / 2.0;
        double ratio = nyquist > 0 ? r.CutoffFrequency / nyquist : 1.0;

        // Cutoff penalty (major factor)
        if (ratio < 0.65) score -= 50;
        else if (ratio < 0.75) score -= 40;
        else if (ratio < 0.85) score -= 25;
        else if (ratio < 0.90) score -= 10;
        else if (ratio < 0.95) score -= 3;

        // Artifacts
        if (r.ArtifactLevel == "Strong") score -= 35;
        else if (r.ArtifactLevel == "Medium") score -= 20;
        else if (r.ArtifactLevel == "Weak") score -= 8;

        // Brickwall shelf
        if (r.ShelfType == "Brickwall" && ratio < 0.95) score -= 15;
        else if (r.ShelfType == "Filtered" && ratio < 0.90) score -= 8;

        // Bit depth fake
        if (r.LsbZeroPadded) score -= 20;
        else if (r.BitDepthSuspicious) score -= 8;

        // Upscale
        if (r.IsUpscale) score -= 25;

        return Math.Max(0, Math.Min(100, score));
    }

    public double ScoreHiRes(AnalysisResult r)
    {
        if (r.SampleRate < 88200) return 0;
        double score = 100;

        if (r.MaxHfDb < -50) score -= 60;
        else if (r.MaxHfDb < -30) score -= 30;
        else if (r.MaxHfDb < -20) score -= 10;

        if (r.CutoffFrequency < 22000) score -= 40;
        if (r.IsUpscale) score -= 40;

        return Math.Max(0, Math.Min(100, score));
    }

    public string Classify(AnalysisResult r)
    {
        var score = Score(r);
        if (score >= 90) return "TRUE LOSSLESS";
        if (score >= 70) return "TRUE LOSSLESS";
        if (score >= 50) return "SUSPICIOUS";
        return "FAKE LOSSLESS";
    }
}
