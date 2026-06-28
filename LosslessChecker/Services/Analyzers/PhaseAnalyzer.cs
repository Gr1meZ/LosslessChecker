using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;
using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services.Analyzers;

public class PhaseAnalyzer : IChunkAccumulator<PhaseResult>
{
    private const double BlockSec = 3.0;
    private const double HaasMaxLagSec = 0.012;
    private const double SideFlatnessThreshold = 0.1;
    private const double FakeStereoRatioThreshold = 0.01;
    private const double PercentileThreshold = 0.85;

    private int _sampleRate, _blockSize;
    private double _sumSqM, _sumSqS;
    private int _samplesInBlock;

    private readonly List<double> _correlations = new();
    private double _sumXY, _sumX2, _sumY2;
    private int _corrSamplesInBlock;
    private int _channels = 2;
    private bool _isMono;

    public void Reset()
    {
        _correlations.Clear();
        _sumSqM = _sumSqS = 0;
        _sumXY = _sumX2 = _sumY2 = 0;
        _samplesInBlock = _corrSamplesInBlock = 0;
        _isMono = false;
    }

    public PhaseResult Analyze(StereoBuffer buffer)
    {
        _channels = buffer.IsStereo ? 2 : 1;
        _sampleRate = buffer.SampleRate;
        _blockSize = (int)(_sampleRate * BlockSec);
        Reset();

        if (!buffer.IsStereo)
        {
            _isMono = true;
            return new PhaseResult(1.0, true);
        }

        int n = Math.Min(buffer.Left.Length, buffer.Right.Length);
        for (int i = 0; i < n; i++)
        {
            float l = buffer.Left[i], r = buffer.Right[i];
            double mid = (l + r) * 0.5, side = (l - r) * 0.5;
            _sumSqM += mid * mid;
            _sumSqS += side * side;
            _sumXY += (double)l * r;
            _sumX2 += (double)l * l;
            _sumY2 += (double)r * r;
            _samplesInBlock++;
            _corrSamplesInBlock++;
            if (_samplesInBlock >= _blockSize) FlushBlock();
            if (_corrSamplesInBlock >= 4096) FlushCorrBlock();
        }
        FlushBlock();
        FlushCorrBlock();
        return BuildResult(buffer);
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

    private void AddSample(float l, float r)
    {
        double mid = (l + r) * 0.5, side = (l - r) * 0.5;
        _sumSqM += mid * mid;
        _sumSqS += side * side;
        _sumXY += (double)l * r;
        _sumX2 += (double)l * l;
        _sumY2 += (double)r * r;
        _samplesInBlock++;
        _corrSamplesInBlock++;
        if (_samplesInBlock >= _blockSize) FlushBlock();
        if (_corrSamplesInBlock >= 4096) FlushCorrBlock();
    }

    private void FlushBlock()
    {
        _sumSqM = _sumSqS = 0;
        _samplesInBlock = 0;
    }

    private void FlushCorrBlock()
    {
        if (_corrSamplesInBlock == 0) return;
        double denom = Math.Sqrt(_sumX2 * _sumY2);
        _correlations.Add(denom > 1e-10 ? _sumXY / denom : 0);
        _sumXY = _sumX2 = _sumY2 = 0;
        _corrSamplesInBlock = 0;
    }

    public PhaseResult GetResult() => BuildResult(null!);

    private PhaseResult BuildResult(StereoBuffer? buffer)
    {
        if (_isMono || _channels == 1)
            return new PhaseResult(1.0, true);

        double avgCorr = _correlations.Count > 0 ? _correlations.Average() : 1.0;
        bool isMonoCompatible = avgCorr >= 0;

        double avgCorrVal = Math.Round(avgCorr, 2);
        return new PhaseResult(avgCorrVal, isMonoCompatible);
    }

    public int DetectHaasLag(float[] left, float[] right, int sampleRate)
    {
        int maxLag = (int)(HaasMaxLagSec * sampleRate);
        int n = Math.Min(left.Length - maxLag, 100000);
        if (n < 100) return 0;

        double bestCorr = 0;
        int bestLag = 0;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double sumXY = 0, sumX2 = 0, sumY2 = 0;
            int startL = Math.Max(0, -lag);
            int startR = Math.Max(0, lag);
            int count = Math.Min(n - startL, n - startR);
            for (int i = 0; i < count; i++)
            {
                double l = left[startL + i], r = right[startR + i];
                sumXY += l * r; sumX2 += l * l; sumY2 += r * r;
            }
            double denom = Math.Sqrt(sumX2 * sumY2);
            double corr = denom > 1e-10 ? sumXY / denom : 0;
            if (corr > bestCorr) { bestCorr = corr; bestLag = lag; }
        }
        return Math.Abs(bestLag) >= 100 ? Math.Abs(bestLag) : 0;
    }
}

public record PhaseResult(double Correlation, bool IsMonoCompatible);
