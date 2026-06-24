using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services.Analyzers;

public class PhaseAnalyzer : IChunkAccumulator<PhaseResult>
{
    private const int BlockSize = 4096;
    private readonly List<double> _correlations = new();
    private double _sumXY, _sumX2, _sumY2;
    private int _samplesInBlock;

    public PhaseResult Analyze(StereoBuffer buffer)
    {
        if (!buffer.IsStereo) return new PhaseResult(1.0, true);
        _correlations.Clear();
        _sumXY = _sumX2 = _sumY2 = 0;
        _samplesInBlock = 0;
        int maxLen = Math.Min(buffer.Left.Length, buffer.Right.Length);
        for (int i = 0; i < maxLen; i++)
            AddSample(buffer.Left[i], buffer.Right[i]);
        FlushBlock();
        return BuildResult();
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        for (int i = 0; i < mono.Length; i++) AddSample(mono[i], mono[i]);
    }

    public void AddChunk(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++) AddSample(left[i], right[i]);
    }

    private void AddSample(float x, float y)
    {
        _sumXY += x * y; _sumX2 += x * x; _sumY2 += y * y;
        if (++_samplesInBlock >= BlockSize) FlushBlock();
    }

    private void FlushBlock()
    {
        if (_samplesInBlock == 0) return;
        double denom = Math.Sqrt(_sumX2 * _sumY2);
        _correlations.Add(denom > 1e-10 ? _sumXY / denom : 0);
        _sumXY = _sumX2 = _sumY2 = 0;
        _samplesInBlock = 0;
    }

    public PhaseResult GetResult() => BuildResult();

    private PhaseResult BuildResult()
    {
        double avg = _correlations.Count > 0 ? Math.Round(_correlations.Average(), 2) : 1.0;
        return new PhaseResult(avg, avg >= 0);
    }
}

public record PhaseResult(double Correlation, bool IsMonoCompatible);
