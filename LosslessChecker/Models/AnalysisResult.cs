namespace LosslessChecker.Models;

public record AnalysisResult
{
    // File info
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Format { get; init; } = "";
    public int SampleRate { get; init; }
    public int BitDepth { get; init; }
    public int Channels { get; init; }
    public double DurationSeconds { get; init; }

    // Cutoff & spectrum
    public double CutoffFrequency { get; init; }
    public double CutoffSlope { get; init; }
    public string ShelfType { get; init; } = "";
    public string EncoderMatch { get; init; } = "";

    // Artifacts
    public bool HasArtifacts { get; init; }
    public string ArtifactLevel { get; init; } = "None";
    public string ArtifactType { get; init; } = "None";

    // Peak & clipping
    public double SamplePeakDb { get; init; }
    public double TruePeakDb { get; init; }
    public double ClippingPercent { get; init; }
    public bool HasIsp { get; init; }

    // Dynamics
    public double DynamicRange { get; init; }
    public double OverallRmsDb { get; init; }
    public double IntegratedLufs { get; init; }
    public double LoudnessRange { get; init; }
    public double Plr { get; init; }

    // Bit depth & DC
    public bool BitDepthSuspicious { get; init; }
    public bool LsbZeroPadded { get; init; }
    public int EffectiveBitDepth { get; init; }
    public double DcOffsetL { get; init; }
    public double DcOffsetR { get; init; }

    // Phase & stereo
    public double Correlation { get; init; }
    public bool IsMonoCompatible { get; init; }

    // Upscale
    public bool IsUpscale { get; init; }
    public double MaxHfDb { get; init; }

    // Classification
    public string Authenticity { get; init; } = "";
    public int QualityScore { get; init; }
    public string Decision { get; init; } = "";
    public string StructuredReport { get; init; } = "";

    // Status
    public AnalysisStatus AnalysisStatus { get; init; } = AnalysisStatus.Pending;
    public string? ErrorMessage { get; init; }

    // Visual
    public byte[] SpectrogramFlat { get; init; } = Array.Empty<byte>();
    public int SpectrogramWidth { get; init; }
    public int SpectrogramHeight { get; init; }
}
