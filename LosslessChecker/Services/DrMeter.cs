using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services;

public class DrMeter : IChunkAccumulator<DrResult>
{
    private const double BlockSec = 3.0;
    private const double TopPct = 0.20;
    private const double CalibrationDb = 2.65;
    private const int ClipRunMin = 3;

    private readonly List<double> _rmsDb = new(), _peakDb = new();
    private double _sumSqL, _sumSqR, _maxAbsL, _maxAbsR;
    private double _globalPeak;
    private int _samplesInBlock, _blockSize, _sampleRate;
    private long _totalSamples;
    private int _clippedRuns;
    private int _consecutive;
    private bool _isStereo;

    public void Init(int sampleRate)
    {
        _sampleRate = sampleRate;
        _blockSize = (int)(sampleRate * BlockSec);
    }

    public void Reset()
    {
        _rmsDb.Clear(); _peakDb.Clear();
        _globalPeak = 0; _clippedRuns = 0; _consecutive = 0;
        _totalSamples = 0;
        _sumSqL = _sumSqR = _maxAbsL = _maxAbsR = 0; _samplesInBlock = 0;
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        for (int i = 0; i < mono.Length; i++)
        {
            float s = mono[i];
            float abs = Math.Abs(s);
            if (abs > _maxAbsL) _maxAbsL = abs;
            _sumSqL += (double)s * s;
            _consecutive = abs >= 1.0f ? _consecutive + 1 : 0;
            if (_consecutive == ClipRunMin) _clippedRuns++;
            _totalSamples++;
            if (++_samplesInBlock >= _blockSize) FlushBlock();
        }
    }

    public void AddChunk(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            float sl = left[i], sr = right[i];
            float absL = Math.Abs(sl), absR = Math.Abs(sr);

            _sumSqL += (double)sl * sl;
            _sumSqR += (double)sr * sr;

            float maxAbs = absL > absR ? absL : absR;
            if (maxAbs > _maxAbsL) _maxAbsL = maxAbs;

            _consecutive = maxAbs >= 1.0f ? _consecutive + 1 : 0;
            if (_consecutive == ClipRunMin) _clippedRuns++;

            _totalSamples++;
            if (++_samplesInBlock >= _blockSize) FlushBlock();
        }

        if (left.Length > n) AddChunk(left[n..]);
    }

    private void AddChunkLeft(ReadOnlySpan<float> mono)
    {
        for (int i = 0; i < mono.Length; i++)
        {
            float s = mono[i];
            double abs = Math.Abs(s);
            if (abs > _maxAbsL) _maxAbsL = abs;
            _sumSqL += (double)s * s;
            _consecutive = abs >= 1.0f ? _consecutive + 1 : 0;
            if (_consecutive == ClipRunMin) _clippedRuns++;
            _totalSamples++;
            if (++_samplesInBlock >= _blockSize) FlushBlock();
        }
    }

    private void FlushBlock()
    {
        if (_samplesInBlock == 0) return;
        double combinedSq = _sumSqL + _sumSqR;
        int channelCount = _isStereo ? 2 : 1;
        double rms = Math.Sqrt(combinedSq / (_samplesInBlock * channelCount));
        double peak = Math.Max(_maxAbsL, _maxAbsR);
        if (peak > _globalPeak) _globalPeak = peak;
        _rmsDb.Add(20.0 * Math.Log10(Math.Max(rms, 1e-10)));
        _peakDb.Add(20.0 * Math.Log10(Math.Max(peak, 1e-10)));
        _sumSqL = _sumSqR = _maxAbsL = _maxAbsR = 0; _samplesInBlock = 0;
    }

    public DrResult GetResult()
    {
        FlushBlock();

        double dr = _rmsDb.Count >= 5 ? ComputeDr(_rmsDb, _peakDb) : 0;

        double peak = _globalPeak > 0 ? 20.0 * Math.Log10(_globalPeak) : 0;

        double maxClipRuns = _totalSamples / (double)ClipRunMin;
        double clipPct = maxClipRuns > 0
            ? _clippedRuns / maxClipRuns * 100.0
            : 0;

        return new DrResult(
            Math.Round(dr, 0), Math.Round(dr, 0), Math.Round(dr, 0),
            Math.Round(peak, 1), Math.Round(clipPct, 2));
    }

    private static double ComputeDr(List<double> rmsDb, List<double> peakDb)
    {
        var indexed = rmsDb.Select((r, i) => (r, p: peakDb[i])).OrderByDescending(x => x.r).ToList();
        int top20 = Math.Max(1, (int)(indexed.Count * TopPct));
        var top = indexed.Take(top20).ToList();
        double avgPeak = top.Average(x => x.p);
        double avgRms = top.Average(x => x.r);
        double dr = avgPeak - avgRms - CalibrationDb;
        return dr < 0 ? 0 : dr;
    }

    public DrResult AnalyzeStereo(StereoBuffer buffer)
    {
        Init(buffer.SampleRate);
        _rmsDb.Clear(); _peakDb.Clear();
        _globalPeak = 0;
        _clippedRuns = _consecutive = 0;
        _totalSamples = 0;
        _sumSqL = _sumSqR = _maxAbsL = _maxAbsR = 0;
        _samplesInBlock = 0;

        _isStereo = buffer.IsStereo;

        if (buffer.IsStereo)
            AddChunk(buffer.Left, buffer.Right);
        else
            AddChunk(buffer.Left);

        return GetResult();
    }

    public (double dr, double samplePeakDb, double clippingPercent) Analyze(float[] samples, int sampleRate)
    {
        Init(sampleRate);
        _rmsDb.Clear(); _peakDb.Clear();
        _globalPeak = _clippedRuns = _consecutive = 0;
        _totalSamples = 0;
        _sumSqL = _maxAbsL = 0; _samplesInBlock = 0;

        AddChunk(samples);

        double dr = _rmsDb.Count >= 5 ? ComputeDr(_rmsDb, _peakDb) : 0;
        double peak = _globalPeak > 0 ? 20.0 * Math.Log10(_globalPeak) : 0;
        double maxClipRuns = _totalSamples / (double)ClipRunMin;
        double clipPct = maxClipRuns > 0 ? _clippedRuns / maxClipRuns * 100.0 : 0;
        return (Math.Round(dr, 0), Math.Round(peak, 1), Math.Round(clipPct, 2));
    }
}

public record DrResult(double Dr, double DrLeft, double DrRight, double SamplePeakDb, double ClippingPercent);
