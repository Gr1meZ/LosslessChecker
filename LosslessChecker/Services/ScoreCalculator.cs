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
            >= 0.95 => 0,
            >= 0.85 => 5,
            >= 0.75 => 15,
            >= 0.65 => 25,
            _ => 40
        };

        double artifactPenalty = input.ArtifactLevel switch
        {
            "None" => 0,
            "Weak" => 10,
            "Medium" => 20,
            "Strong" => 30,
            _ => 0
        };

        double clippingPenalty = input.ClippingPercent switch
        {
            <= 0 => 0,
            < 1 => 5,
            < 5 => 10,
            _ => 20
        };

        double drPenalty = input.DynamicRange switch
        {
            >= 10 => 0,
            >= 8 => 3,
            >= 6 => 7,
            _ => 10
        };

        double score = 100.0 - cutoffPenalty - artifactPenalty - clippingPenalty - drPenalty;
        score = Math.Max(0, Math.Min(100, score));

        string status = score switch
        {
            >= 90 => "Lossless",
            >= 60 => "Suspicious",
            _ => "Fake / Poor Quality"
        };

        return input with { LosslessScore = Math.Round(score, 1), Status = status };
    }
}
