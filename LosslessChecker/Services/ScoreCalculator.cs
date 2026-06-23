using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class ScoreCalculator
{
    public AnalysisResult Calculate(AnalysisResult input)
    {
        var nyquist = input.SampleRate / 2.0;
        double cutoffRatio = nyquist > 0 ? input.CutoffFrequency / nyquist : 1.0;

        // Cutoff penalty — sample-rate-aware with anti-aliasing filter awareness.
        // Real ADCs have gradual anti-aliasing rolloff before Nyquist.
        // Penalties target known lossy encoder cutoff frequencies:
        //   128 kbps MP3: ~16 kHz,   192 kbps: ~18 kHz,   320 kbps: ~20 kHz
        double cutoffPenalty;
        if (input.SampleRate >= 88200)
        {
            // Hi-Res: expect content above 20 kHz (human hearing limit)
            cutoffPenalty = input.CutoffFrequency switch
            {
                >= 30000 => 0,
                >= 23000 => 2,
                >= 20000 => 5,
                >= 16000 => 20,
                >= 12000 => 35,
                _ => 45
            };
        }
        else if (input.SampleRate >= 44100)
        {
            // Standard rate: ADC anti-aliasing filter starts ~0.85-0.9 × Nyquist.
            // Real lossy codec cutoffs are much lower.
            cutoffPenalty = input.CutoffFrequency switch
            {
                >= 20000 => 0,      // ADC rolloff or full bandwidth — no penalty
                >= 18000 => 5,      // Very mild: could be ADC or 320kbps MP3
                >= 16000 => 18,     // Moderate: 192-256 kbps lossy range
                >= 14000 => 32,     // Heavy: 128-160 kbps MP3 range
                >= 10000 => 45,     // Severe: 96-112 kbps
                _ => 55             // Extreme: sub-96kbps
            };
        }
        else
        {
            // Low sample rates (22.05kHz, etc.)
            double ratio = input.CutoffFrequency / Math.Max(nyquist, 1);
            cutoffPenalty = ratio switch
            {
                >= 0.90 => 0,
                >= 0.80 => 5,
                >= 0.65 => 18,
                >= 0.50 => 32,
                _ => 45
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
        double slopePenalty = 0;
        if (input.CutoffSlope < -18 && input.CutoffFrequency < 20000)
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
