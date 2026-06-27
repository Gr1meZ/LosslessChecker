namespace LosslessChecker.Services.Analysis;

public class ScoringProfile
{
    // Cutoff penalty thresholds (ratio = cutoffHz / nyquist)
    public double[] CutoffRatioThresholds { get; init; } = { 0.65, 0.75, 0.85, 0.90, 0.95 };
    public double[] CutoffPenalties { get; init; } = { 50, 40, 25, 10, 3 };

    // Artifact penalties
    public int ArtifactStrongPenalty { get; init; } = 35;
    public int ArtifactMediumPenalty { get; init; } = 20;
    public int ArtifactWeakPenalty { get; init; } = 8;

    // Shelf penalties
    public int BrickwallPenalty { get; init; } = 15;
    public int FilteredPenalty { get; init; } = 8;

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
    public (double threshold, int penalty)[] LufsThresholds { get; init; } = { (-7, 15), (-10, 8), (-14, 3) };
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

    public static readonly ScoringProfile Default = new();
}
