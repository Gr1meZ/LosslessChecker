using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services.Analyzers;

public class DcOffsetDetector : IChunkAccumulator<DcOffsetResult>
{
    private const double ThresholdPercent = 0.5;
    private double _sumL, _sumR;
    private long _countL, _countR;

    public void Reset() { _sumL = _sumR = 0; _countL = _countR = 0; }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        for (int i = 0; i < mono.Length; i++) { _sumL += mono[i]; _countL++; }
    }

    public void AddChunk(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++) { _sumL += left[i]; _sumR += right[i]; }
        _countL += n; _countR += n;
    }

    public DcOffsetResult GetResult()
    {
        double meanL = _countL > 0 ? _sumL / _countL : 0;
        double meanR = _countR > 0 ? _sumR / _countR : 0;
        double dcOffsetL = Math.Round(meanL * 100.0, 4);
        double dcOffsetR = Math.Round(meanR * 100.0, 4);
        bool hasDcOffset = Math.Abs(dcOffsetL) > ThresholdPercent || Math.Abs(dcOffsetR) > ThresholdPercent;
        return new DcOffsetResult(dcOffsetL, dcOffsetR, hasDcOffset);
    }

    public DcOffsetResult Analyze(StereoBuffer buffer)
    {
        Reset();
        if (buffer.IsStereo) AddChunk(buffer.Left, buffer.Right);
        else { AddChunk(buffer.Left); _sumR = _sumL; _countR = _countL; }
        return GetResult();
    }
}

public record DcOffsetResult(double DcOffsetL, double DcOffsetR, bool HasDcOffset);
