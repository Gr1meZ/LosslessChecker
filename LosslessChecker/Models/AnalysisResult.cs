namespace LosslessChecker.Models;

public record AnalysisResult
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Format { get; init; } = "";
    public int SampleRate { get; init; }
    public int Bitrate { get; init; }
    public int BitDepth { get; init; }
    public double DurationSeconds { get; init; }

    public double CutoffFrequency { get; init; }
    public double CutoffSlope { get; init; }
    public bool HasArtifacts { get; init; }
    public string ArtifactLevel { get; init; } = "None";
    public double DynamicRange { get; init; }
    public double TruePeak { get; init; }
    public double ClippingPercent { get; init; }

    public bool BitDepthSuspicious { get; init; }
    public double NoiseFloorDb { get; init; }
    public string BitDepthVerdict { get; init; } = "";

    public bool IsUpscale { get; init; }
    public double MaxHfDb { get; init; }
    public string UpscaleVerdict { get; init; } = "";

    public double LosslessScore { get; init; }
    public string Status { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string? ErrorMessage { get; init; }
    public AnalysisStatus AnalysisStatus { get; init; } = AnalysisStatus.Pending;

    public double[] AveragedSpectrum { get; init; } = Array.Empty<double>();
    public double[][] SpectrogramData { get; init; } = Array.Empty<double[]>();
}
