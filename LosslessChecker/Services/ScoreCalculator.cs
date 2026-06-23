using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class ScoreCalculator
{
    public AnalysisResult Calculate(AnalysisResult input)
    {
        var nyquist = input.SampleRate / 2.0;
        double cutoffRatio = nyquist > 0 ? input.CutoffFrequency / nyquist : 1.0;

        double cutoffPenalty = cutoffRatio switch
        {
            >= 0.93 => 0,       // No penalty: cutoff within 7% of Nyquist
            >= 0.85 => 8,       // Mild: possible gentle rolloff
            >= 0.75 => 20,      // Moderate: suspicious for lossless
            >= 0.65 => 35,      // Heavy: likely transcode
            _ => 50             // Severe: very low-bitrate source
        };

        double artifactPenalty = input.ArtifactLevel switch
        {
            "None" => 0,
            "Weak" => 8,
            "Medium" => 18,
            "Strong" => 35,
            _ => 0
        };

        double clippingPenalty = input.ClippingPercent switch
        {
            <= 0 => 0,
            < 0.5 => 2,         // Negligible clipping
            < 2 => 6,           // Minor
            < 5 => 12,          // Noticeable
            _ => 20             // Heavy clipping / brickwalled
        };

        double drPenalty = input.DynamicRange switch
        {
            >= 12 => 0,         // Excellent dynamics
            >= 9 => 2,          // Good
            >= 7 => 5,          // Average (modern mastering)
            >= 5 => 8,          // Below average
            _ => 12             // Poor (loudness war victim)
        };

        double score = 100.0 - cutoffPenalty - artifactPenalty - clippingPenalty - drPenalty;
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
