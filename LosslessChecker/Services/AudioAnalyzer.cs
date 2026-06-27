using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class AudioAnalyzer
{
    private readonly AudioPipeline _pipeline = new();
    private readonly AnalysisCache _cache = new();

    public AnalysisResult Analyze(AudioFileInfo fileInfo, CancellationToken ct = default)
    {
        var fileInfo2 = new System.IO.FileInfo(fileInfo.FilePath);
        if (fileInfo2.Exists && _cache.TryGet(fileInfo.FilePath, fileInfo2.Length, fileInfo2.LastWriteTime, out var cached))
            return cached!;

        var result = _pipeline.Analyze(fileInfo, ct);
        if (fileInfo2.Exists && result.AnalysisStatus == AnalysisStatus.Completed)
            _cache.Store(fileInfo.FilePath, fileInfo2.Length, fileInfo2.LastWriteTime, result);

        return result;
    }

    public void ClearCache() => _cache.Clear();
}
