namespace LosslessChecker.Services.Analysis;

public class ScoringProfile
{
    // Cutoff penalty thresholds (used by LosslessScorer for Brickwall shelf)
    public double[] CutoffRatioThresholds { get; init; } = { 0.65, 0.75, 0.85, 0.90, 0.95 };
    public double[] CutoffPenalties { get; init; } = { 60, 55, 35, 20, 8 };

    // Artifact penalties
    public int ArtifactStrongPenalty { get; init; } = 35;
    public int ArtifactMediumPenalty { get; init; } = 20;
    public int ArtifactWeakPenalty { get; init; } = 8;

    // Bit depth penalties
    public int LsbZeroPadPenalty { get; init; } = 20;
    public int BitDepthSuspiciousPenalty { get; init; } = 8;

    // Upscale penalty
    public int UpscalePenalty { get; init; } = 25;

    // Authenticity classification thresholds
    public int TrueLosslessThreshold { get; init; } = 70;
    public int SuspiciousThreshold { get; init; } = 50;

    // Hi-Res scoring
    public double HfDbVeryLow { get; init; } = -60;
    public double HfDbLow { get; init; } = -40;
    public double HfDbMedium { get; init; } = -25;
    public int HfVeryLowPenalty { get; init; } = 60;
    public int HfLowPenalty { get; init; } = 30;
    public int HfMediumPenalty { get; init; } = 10;
    public int CutoffBelow22kPenalty { get; init; } = 40;
    public int HiResUpscalePenalty { get; init; } = 40;

    // Quality scoring
    public (double threshold, int penalty)[] ClippingThresholds { get; init; } = { (5, 20), (2, 12), (0.5, 6), (0, 2) };
    public (double threshold, int penalty)[] LufsThresholds { get; init; } = { (-5, 15), (-8, 8), (-14, 3) };
    public int IspBasePenalty { get; init; } = 8;
    public int IspExtraPenalty { get; init; } = 5;
    public double DcOffsetHighThreshold { get; init; } = 1.0;
    public int DcOffsetHighPenalty { get; init; } = 8;
    public double DcOffsetLowThreshold { get; init; } = 0.5;
    public int DcOffsetLowPenalty { get; init; } = 3;
    public double PhaseBadThreshold { get; init; } = -0.5;
    public int PhaseBadPenalty { get; init; } = 12;
    public int PhaseSuspiciousPenalty { get; init; } = 6;
    public int LsbZeroPadQualityPenalty { get; init; } = 5;

    // Quality decision thresholds
    public int QualityKeepThreshold { get; init; } = 50;
    public int QualityExcellentThreshold { get; init; } = 80;

    // Genre-specific DR thresholds: (Excellent, Good, Poor)
    public (double Excellent, double Good, double Poor)[] GenreDrThresholds { get; init; } =
    {
        (12, 8, 5),   // Default
        (8, 5, 3),    // EDM / Electronic
        (8, 5, 3),    // Metal / Rock
        (14, 10, 7),  // Jazz / Classical
    };

    // Authenticity penalties (deductive)
    public int CutoffPenaltyBrickwallCodec { get; init; } = 60;
    public int CutoffPenaltyBrickwallNearNyquist { get; init; } = 30;
    public int CutoffPenaltyFilteredLow { get; init; } = 20;
    public int ArtifactStrongPenaltyAuth { get; init; } = 35;
    public int ArtifactMediumPenaltyAuth { get; init; } = 20;
    public int ArtifactWeakPenaltyAuth { get; init; } = 8;
    public int LsbZeroPadPenaltyAuth { get; init; } = 100;
    public int LsbConstantPenaltyAuth { get; init; } = 80;
    public int BitDepthSuspiciousPenaltyAuth { get; init; } = 20;
    public int AliasingPenalty { get; init; } = 15;
    public int RingingPenalty { get; init; } = 10;
    public int UpscalePenaltyAuth { get; init; } = 25;
    public int FakeStereoPenaltyAuth { get; init; } = 10;
    public int AbruptEdgesPenaltyAuth { get; init; } = 5;

    // Mastering penalties (anomaly-based)
    public int HardClippingSeverePenalty { get; init; } = 20;
    public int IspMinimalPenalty { get; init; } = 2;
    public int DcOffsetSeverePenalty { get; init; } = 8;
    public int PhaseBadPenaltyMastering { get; init; } = 12;
    public int PlrLowPenalty { get; init; } = 10;
    public int LufsAnomalyPenalty { get; init; } = 15;

    public static readonly ScoringProfile Default = new();
}
