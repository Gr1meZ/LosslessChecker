namespace LosslessChecker.Models;

public record AnalysisResult
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Format { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Genre { get; init; } = "";
    public int SampleRate { get; init; }
    public int BitDepth { get; init; }
    public int Channels { get; init; }
    public double DurationSeconds { get; init; }
    public byte[]? CoverData { get; init; }

    public double CutoffFrequency { get; init; }
    public double CutoffSlope { get; init; }
    public string ShelfType { get; init; } = "";
    public string EncoderMatch { get; init; } = "";

    public bool HasArtifacts { get; init; }
    public string ArtifactLevel { get; init; } = "None";
    public string ArtifactType { get; init; } = "None";
    public bool HasPreEcho { get; init; }
    public int PreEchoCount { get; init; }
    public bool HasSpectralHoles { get; init; }

    public double SamplePeakDb { get; init; }
    public double TruePeakDb { get; init; }
    public double ClippingPercent { get; init; }
    public bool HasIsp { get; init; }
    public bool HasHardClipping { get; init; }

    public double DynamicRange { get; init; }
    public double OverallRmsDb { get; init; }
    public double IntegratedLufs { get; init; }
    public double LoudnessRange { get; init; }
    public double Plr { get; init; }

    public bool BitDepthSuspicious { get; init; }
    public bool LsbZeroPadded { get; init; }
    public int EffectiveBitDepth { get; init; }
    public double DcOffsetL { get; init; }
    public double DcOffsetR { get; init; }

    public double Correlation { get; init; }
    public bool IsMonoCompatible { get; init; }

    public bool IsUpscale { get; init; }
    public double MaxHfDb { get; init; }

    public bool IsVinylRip { get; init; }
    public double VinylRumbleRatio { get; init; }
    public double VinylHfNoiseRatio { get; init; }

    public bool IsCdAligned { get; init; }
    public bool FlacIntegrityOk { get; init; }
    public string ContainerSource { get; init; } = "";
    public bool IsMqa { get; init; }
    public string MqaDetails { get; init; } = "";
    public bool IsHdcd { get; init; }

    public bool HasAliasing { get; init; }
    public bool HasRinging { get; init; }
    public string ResamplingVerdict { get; init; } = "";

    public string Authenticity { get; init; } = "";
    public double LosslessScore { get; init; }
    public double HiResScore { get; init; }
    public int QualityScore { get; init; }
    public double QualityScorePercent { get; init; }
    public double MetricsCoverage { get; init; }
    public string Decision { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string StructuredReport { get; init; } = "";

    public int Mp3Bitrate { get; init; }
    public string Mp3Encoder { get; init; } = "";
    public double Mp3QualityScore { get; init; }

    public AnalysisStatus AnalysisStatus { get; init; } = AnalysisStatus.Pending;
    public string? ErrorMessage { get; init; }

    public byte[] SpectrogramFlat { get; init; } = Array.Empty<byte>();
    public int SpectrogramWidth { get; init; }
    public int SpectrogramHeight { get; init; }
}
