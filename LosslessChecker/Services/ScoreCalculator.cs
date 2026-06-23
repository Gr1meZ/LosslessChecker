using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class ScoreCalculator
{
    public AnalysisResult Calculate(AnalysisResult input)
    {
        var nyquist = input.SampleRate / 2.0;
        double cutoffRatio = nyquist > 0 ? input.CutoffFrequency / nyquist : 1.0;

        // Cutoff penalty — based on ratio AND slope
        double cutoffPenalty;
        if (cutoffRatio >= 0.90)
            cutoffPenalty = 0;
        else if (cutoffRatio >= 0.82)
            cutoffPenalty = 3;  // Gentle rolloff — barely penalize
        else if (cutoffRatio >= 0.72)
            cutoffPenalty = 12; // Mild cutoff
        else if (cutoffRatio >= 0.60)
            cutoffPenalty = 25; // Moderate
        else if (cutoffRatio >= 0.45)
            cutoffPenalty = 40; // Heavy
        else
            cutoffPenalty = 50; // Severe

        // If slope is very steep (brickwall), increase penalty
        if (input.CutoffSlope < -20 && cutoffRatio < 0.85)
            cutoffPenalty = Math.Min(50, cutoffPenalty + 10);

        double artifactPenalty = input.ArtifactLevel switch
        {
            "None" => 0,
            "Weak" => 6,
            "Medium" => 15,
            "Strong" => 30,
            _ => 0
        };

        // Clipping penalty — significant for forensic quality
        double clippingPenalty = input.ClippingPercent switch
        {
            <= 0 => 0,
            < 0.5 => 1,
            < 2 => 5,
            < 5 => 10,
            _ => 18
        };

        // DR penalty — SOFTENED. Low DR alone is NOT a file authenticity issue.
        // Only penalize when combined with clipping (loudness war victim).
        double drPenalty = input.DynamicRange switch
        {
            >= 8 => 0,
            >= 6 => input.ClippingPercent > 1 ? 3 : 1,  // Minor if no clipping
            >= 4 => input.ClippingPercent > 1 ? 6 : 2,  // Context-aware
            _ => input.ClippingPercent > 1 ? 10 : 4
        };

        // Upscale penalty
        double upscalePenalty = input.IsUpscale ? 20 : 0;

        // Bit depth padding penalty
        double bitDepthPenalty = input.BitDepthSuspicious ? 10 : 0;

        double score = 100.0
            - cutoffPenalty
            - artifactPenalty
            - clippingPenalty
            - drPenalty
            - upscalePenalty
            - bitDepthPenalty;
        score = Math.Max(0, Math.Min(100, score));

        string status = score switch
        {
            >= 90 => "Lossless / Excellent",
            >= 75 => "Lossless / Good",
            >= 55 => "Suspicious",
            _ => "Fake / Poor"
        };

        return input with { LosslessScore = Math.Round(score, 1), Status = status };
    }
}
