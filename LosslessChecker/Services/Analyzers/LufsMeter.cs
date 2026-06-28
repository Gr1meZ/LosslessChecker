using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services.Analyzers;

public class LufsMeter : IChunkAccumulator<LufsResult>
{
    private const double BlockDuration = 0.4;
    private const double HopDuration = 0.1;
    private const double AbsoluteGate = -70.0;
    private const double RelativeGate = -10.0;
    private const double ChannelWeight = 1.0;

    private int _sampleRate;
    private int _blockSize, _hopSize;
    private double _pos;
    private int _frameCount;

    private readonly List<double> _blockLoudness = new();
    private readonly List<double> _shortTermLoudness = new();
    private KWeightingFilter _kwL = null!, _kwR = null!;

    private readonly List<float> _pendingSamplesL = new();
    private readonly List<float> _pendingSamplesR = new();

    public void Reset()
    {
        _blockLoudness.Clear();
        _shortTermLoudness.Clear();
        _pendingSamplesL.Clear();
        _pendingSamplesR.Clear();
        _pos = 0;
        _frameCount = 0;
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        if (_kwL == null) throw new InvalidOperationException("Init not called");
        for (int i = 0; i < mono.Length; i++)
            _pendingSamplesL.Add(mono[i]);
        ProcessPendingMono();
    }

    public void AddChunk(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (_kwL == null) throw new InvalidOperationException("Init not called");
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            _pendingSamplesL.Add(left[i]);
            _pendingSamplesR.Add(right[i]);
        }
        ProcessPendingStereo();
    }

    public LufsMeter Init(int sampleRate)
    {
        _sampleRate = sampleRate;
        _blockSize = (int)(sampleRate * BlockDuration);
        _hopSize = (int)(sampleRate * HopDuration);
        _kwL = new KWeightingFilter(sampleRate);
        _kwR = new KWeightingFilter(sampleRate);
        return this;
    }

    private void ProcessPendingMono()
    {
        while (_pendingSamplesL.Count >= _blockSize)
        {
            double sumSq = 0;
            for (int i = 0; i < _blockSize; i++)
            {
                double filtered = _kwL.Process(_pendingSamplesL[i]);
                sumSq += filtered * filtered;
            }
            _pendingSamplesL.RemoveRange(0, _hopSize);
            double meanSq = sumSq / _blockSize;
            double loudness = -0.691 + 10.0 * Math.Log10(Math.Max(meanSq, 1e-12));
            _blockLoudness.Add(loudness);
            _frameCount++;
        }
    }

    private void ProcessPendingStereo()
    {
        while (_pendingSamplesL.Count >= _blockSize)
        {
            double sumSq = 0;
            for (int i = 0; i < _blockSize; i++)
            {
                double fl = _kwL.Process(_pendingSamplesL[i]);
                double fr = _kwR.Process(_pendingSamplesR[i]);
                sumSq += fl * fl + fr * fr;
            }
            _pendingSamplesL.RemoveRange(0, _hopSize);
            _pendingSamplesR.RemoveRange(0, _hopSize);
            double meanSq = sumSq / (_blockSize * 2);
            double loudness = -0.691 + 10.0 * Math.Log10(Math.Max(meanSq, 1e-12));
            _blockLoudness.Add(loudness);
            _frameCount++;
        }
    }

    public LufsResult GetResult()
    {
        double integratedLufs = ComputeIntegratedLoudness(_blockLoudness);
        double lra = 0;
        if (_shortTermLoudness.Count > 10 || _blockLoudness.Count > 10)
        {
            var active = (_shortTermLoudness.Count > 0 ? _shortTermLoudness : _blockLoudness)
                .Where(b => b > AbsoluteGate).ToList();
            if (active.Count >= 10)
            {
                double meanLin = active.Average(b => Math.Pow(10, (b + 0.691) / 10.0));
                double meanLoudness = -0.691 + 10.0 * Math.Log10(meanLin);
                double relThreshold = meanLoudness + RelativeGate;
                var relGated = active.Where(b => b > relThreshold).OrderBy(b => b).ToList();
                if (relGated.Count >= 10)
                {
                    int lowIdx = Math.Max(0, (int)Math.Ceiling(relGated.Count * 0.10) - 1);
                    int highIdx = Math.Min(relGated.Count - 1, (int)Math.Ceiling(relGated.Count * 0.95) - 1);
                    lra = relGated[highIdx] - relGated[lowIdx];
                }
            }
        }
        return new LufsResult(Math.Round(integratedLufs, 1), Math.Round(lra, 1));
    }

    private static double ComputeIntegratedLoudness(List<double> blockLoudness)
    {
        var absoluteGated = blockLoudness.Where(b => b > AbsoluteGate).ToList();
        if (absoluteGated.Count == 0) return -70.0;
        double absoluteMeanLin = absoluteGated.Average(b => Math.Pow(10, (b + 0.691) / 10.0));
        double absoluteLoudness = -0.691 + 10.0 * Math.Log10(absoluteMeanLin);
        double relativeThreshold = absoluteLoudness + RelativeGate;
        var relativeGated = absoluteGated.Where(b => b > relativeThreshold).ToList();
        if (relativeGated.Count == 0) return absoluteLoudness;
        double gatedMeanLin = relativeGated.Average(b => Math.Pow(10, (b + 0.691) / 10.0));
        return -0.691 + 10.0 * Math.Log10(gatedMeanLin);
    }

    public LufsResult Analyze(StereoBuffer buffer)
    {
        Init(buffer.SampleRate);
        Reset();
        if (buffer.IsStereo) AddChunk(buffer.Left, buffer.Right);
        else AddChunk(buffer.Left);
        return GetResult();
    }
}

public record LufsResult(double IntegratedLufs, double LoudnessRange);
