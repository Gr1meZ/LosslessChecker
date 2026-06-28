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
                if (r.CutoffFrequency <= 17000) score -= _p.CutoffPenalties[0];      // MP3 128 upscale
                else if (r.CutoffFrequency <= 20000) score -= _p.CutoffPenalties[1]; // MP3 192-256 upscale
                else if (r.CutoffFrequency <= 22100) score -= _p.CutoffPenalties[0]; // CD upscale
                // Above 22.1kHz — no penalty, genuine Hi-Res
            }
            else
            {
                for (int i = 0; i < _p.CutoffRatioThresholds.Length; i++)
                {
                    if (ratio < _p.CutoffRatioThresholds[i])
                    {
                        score -= _p.CutoffPenalties[i];
                        break;
                    }
                }
            }
        }
        else if (r.ShelfType == "Filtered")
        {
            if (isHiRes)
            {
                if (r.CutoffFrequency <= 17000) score -= _p.CutoffPenalties[1];      // MP3 128 upscale
                else if (r.CutoffFrequency <= 20000) score -= _p.CutoffPenalties[3]; // MP3 192-256 upscale
                else if (r.CutoffFrequency <= 22100) score -= _p.CutoffPenalties[2]; // CD upscale
                // Above 22.1kHz filtered — gentle mastering LP, no penalty
            }
            else
            {
                // Filtered = gentle rolloff (analog tape, mastering LPF), not codec.
                // Penalties are much lighter than brickwall — a gradual slope
                // at low cutoff is natural for vintage/analog recordings.
                if (ratio < _p.CutoffRatioThresholds[1]) score -= 12;
                else if (ratio < _p.CutoffRatioThresholds[2]) score -= 8;
                else if (ratio < _p.CutoffRatioThresholds[3]) score -= 4;
            }
        }

        // Artifact penalty: only significant when paired with brickwall or suspicious cutoff
        bool isSuspiciousCutoff = isHiRes ? r.CutoffFrequency <= 22100 : ratio < 0.85;
        if (r.ArtifactLevel == "Strong" && (r.ShelfType == "Brickwall" || isSuspiciousCutoff)) score -= _p.ArtifactStrongPenalty;
        else if (r.ArtifactLevel == "Medium" && (r.ShelfType == "Brickwall" || isSuspiciousCutoff)) score -= _p.ArtifactMediumPenalty;
        else if (r.ArtifactLevel == "Weak") score -= _p.ArtifactWeakPenalty;

        // 0.4 LSB zero-padding: graduated penalty
        if (r.LsbZeroPadded)
        {
            if (r.BitDepth >= 24) score -= 100;  // Guaranteed FAKE 24-bit
            else score -= 20;                     // Minor flag for 16-bit
        }
        else if (r.BitDepthSuspicious) score -= _p.BitDepthSuspiciousPenalty;

        if (r.IsUpscale) score -= _p.UpscalePenalty;

        // 0.5 Container integrity bonuses
        if (r.FlacIntegrityOk) score += 5;
        if (r.IsCdAligned && r.FlacIntegrityOk) score += 5;

        // Floor guarantee: clean spectrum (>=90% Nyquist, non-brickwall, no/weak artifacts) ≥ 75
        // This ensures master-grade files with LP filters never get FALSE
        bool isCleanSpectrum = ratio >= 0.90
                            && r.ShelfType != "Brickwall"
                            && (r.ArtifactLevel == "None" || r.ArtifactLevel == "Weak");
        if (isCleanSpectrum && score < 75)
            score = 75;

        // Floor for analog/vintage recordings with natural HF rolloff:
        // non-brickwall, weak/none artifacts, low cutoff — likely mastering/tape limitation
        bool isAnalogRolloff = r.ShelfType != "Brickwall"
                            && (r.ArtifactLevel == "None" || r.ArtifactLevel == "Weak")
                            && ratio < 0.90;
        if (isAnalogRolloff && score < 60)
            score = 60;

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

    public double AuthenticityScore(AnalysisResult r)
    {
        double score = 100;
        var nyquist = r.SampleRate / 2.0;
        double ratio = nyquist > 0 ? r.CutoffFrequency / nyquist : 1.0;
        bool isHiRes = r.SampleRate >= 88200;

        if (r.IsCorrupted) return 0;

        if (r.LsbZeroPadded && r.BitDepth >= 24) return Math.Max(0, score - _p.LsbZeroPadPenaltyAuth);
        if (r.BitDepthSuspicious) score -= _p.BitDepthSuspiciousPenaltyAuth;

        // Cutoff penalty for ALL shelf types. A cutoff significantly below
        // Nyquist is inherently suspicious regardless of the rolloff shape.
        if (!isHiRes)
        {
            if (ratio < 0.65) score -= _p.CutoffPenaltyBrickwallCodec;
            else if (ratio < 0.80) score -= _p.CutoffPenaltyBrickwallCodec / 2;
            else if (ratio < 0.90) score -= _p.CutoffPenaltyBrickwallNearNyquist;
        }

        if (r.ShelfType == "Brickwall")
        {
            if (isHiRes)
            {
                if (r.CutoffFrequency <= 17000) score -= _p.CutoffPenaltyBrickwallCodec;
                else if (r.CutoffFrequency <= 20000) score -= _p.CutoffPenaltyBrickwallCodec / 2;
                else if (r.CutoffFrequency <= 22100) score -= _p.CutoffPenaltyBrickwallNearNyquist;
            }
            else
            {
                if (ratio < 0.65) score -= _p.CutoffPenaltyBrickwallCodec;
                else if (ratio < 0.85) score -= _p.CutoffPenaltyBrickwallCodec / 2;
                else if (ratio < 0.95) score -= _p.CutoffPenaltyBrickwallNearNyquist;
            }
        }

        if (r.HasArtifacts)
        {
            score -= r.ArtifactLevel switch
            {
                "Strong" => _p.ArtifactStrongPenaltyAuth,
                "Medium" => _p.ArtifactMediumPenaltyAuth,
                "Weak" => _p.ArtifactWeakPenaltyAuth,
                _ => 0
            };
        }

        if (r.EncoderMatch.StartsWith("MP3") || r.EncoderMatch.StartsWith("AAC"))
            score -= 20;

        if (r.HasAliasing) score -= _p.AliasingPenalty;
        if (r.HasRinging) score -= _p.RingingPenalty;
        if (r.IsUpscale) score -= _p.UpscalePenaltyAuth;
        if (r.IsFakeStereo) score -= _p.FakeStereoPenaltyAuth;
        if (r.HasAbruptEdges) score -= _p.AbruptEdgesPenaltyAuth;

        bool isCleanSpectrum = ratio >= 0.90 && r.ShelfType != "Brickwall" && r.ArtifactLevel != "Strong";
        if (isCleanSpectrum && score < 75) score = 75;

        bool isAnalogRolloff = r.ShelfType != "Brickwall" && r.ArtifactLevel != "Strong" && ratio < 0.90;
        if (isAnalogRolloff && score < 60) score = 60;

        return Math.Max(0, Math.Min(100, score));
    }

    public double MasteringScore(AnalysisResult r)
    {
        double score = 100;
        int genre = InferGenre(r);

        var (excellent, good, poor) = _p.GenreDrThresholds[genre];
        if (r.DynamicRange < poor) score -= 20;
        else if (r.DynamicRange < good) score -= 10;
        else if (r.DynamicRange < excellent) score -= 3;

        double expectedLufs = genre switch { 0 => -14, 1 => -8, 2 => -9, 3 => -18, _ => -14 };
        double lufsDelta = Math.Abs(r.IntegratedLufs - expectedLufs);
        if (lufsDelta > 6) score -= _p.LufsAnomalyPenalty;

        if (r.ClippingPercent > 5) score -= _p.HardClippingSeverePenalty;
        else if (r.HasIsp) score -= _p.IspMinimalPenalty;

        if (Math.Abs(r.DcOffsetL) > 1.0 || Math.Abs(r.DcOffsetR) > 1.0) score -= _p.DcOffsetSeverePenalty;
        if (r.Correlation < -0.5) score -= _p.PhaseBadPenaltyMastering;

        if (r.IsFakeStereo) score -= 5;
        if (r.HasAbruptEdges) score -= 3;

        return Math.Max(0, Math.Min(100, score));
    }

    private static int InferGenre(AnalysisResult r)
    {
        if (r.IntegratedLufs > -8 && r.DynamicRange <= 5) return 1;  // EDM / Loud pop
        if (r.IntegratedLufs > -8 && r.DynamicRange <= 8) return 2;  // Rock / Metal
        if (r.IntegratedLufs < -14 && r.DynamicRange >= 10) return 3; // Jazz / Classical
        return 0; // Default
    }
}
