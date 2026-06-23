using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class ScoreCalculator
{
    public AnalysisResult Calculate(AnalysisResult input)
    {
        var nyquist = input.SampleRate / 2.0;
        double cutoffRatio = nyquist > 0 ? input.CutoffFrequency / nyquist : 1.0;

        // Cutoff penalty — sample-rate-aware
        // For Hi-Res (≥88.2kHz): content rarely reaches Nyquist. Use absolute threshold.
        // For standard rates (44.1/48kHz): use ratio-to-Nyquist as before.
        double cutoffPenalty;
        if (input.SampleRate >= 88200)
        {
            // Hi-Res: expect content at least above 20 kHz (human hearing limit)
            // Content above 20 kHz = genuine Hi-Res. Below = suspicious.
            cutoffPenalty = input.CutoffFrequency switch
            {
                >= 30000 => 0,     // Excellent: content well into ultrasonic
                >= 23000 => 2,     // Good: typical Hi-Res recording
                >= 20000 => 5,     // OK: just above audible range
                >= 16000 => 20,    // Suspicious: only CD-quality bandwidth
                >= 12000 => 35,    // Likely upscale or poor source
                _ => 45            // Severe: very limited bandwidth
            };
        }
        else
        {
            // Standard sample rate: use ratio to Nyquist
            cutoffPenalty = cutoffRatio switch
            {
                >= 0.90 => 0,
                >= 0.82 => 3,
                >= 0.72 => 12,
                >= 0.60 => 25,
                >= 0.45 => 40,
                _ => 50
            };
        }

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

        // Brickwall slope: if cutoff slope is extremely steep (< -18 dB/octave),
        // it suggests a hard low-pass filter (encoder artifact), not natural rolloff.
        // Only apply for standard rates where we expect gradual HF decay.
        double slopePenalty = 0;
        if (input.SampleRate < 88200 && input.CutoffSlope < -18 && cutoffRatio < 0.90)
            slopePenalty = 8;
        else if (input.SampleRate >= 88200 && input.CutoffSlope < -18 && input.CutoffFrequency < 20000)
            slopePenalty = 8;

        // Bit depth padding penalty
        double bitDepthPenalty = input.BitDepthSuspicious ? 10 : 0;

        double score = 100.0
            - cutoffPenalty
            - artifactPenalty
            - clippingPenalty
            - drPenalty
            - upscalePenalty
            - slopePenalty
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
