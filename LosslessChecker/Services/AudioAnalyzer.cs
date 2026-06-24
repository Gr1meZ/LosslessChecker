using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class AudioAnalyzer
{
    private readonly AudioPipeline _pipeline = new();

    public AnalysisResult Analyze(AudioFileInfo fileInfo, CancellationToken ct = default)
        => _pipeline.Analyze(fileInfo, ct);
}
