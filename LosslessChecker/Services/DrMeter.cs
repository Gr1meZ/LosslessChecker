using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services;

public class DrMeter : IChunkAccumulator<DrResult>
{
    private const double BlockSec = 3.0;
    private const double TopPct = 0.20;
    private const double CalibrationDb = 2.65;
    private const int ClipRunMin = 3;

    private readonly List<double> _rmsDbL = new(), _peakDbL = new();
    private readonly List<double> _rmsDbR = new(), _peakDbR = new();
    private double _sumSqL, _sumSqR, _maxAbsL, _maxAbsR;
    private double _globalPeakL, _globalPeakR;
    private int _samplesInBlockL, _samplesInBlockR;
    private int _blockSize, _sampleRate;
    private long _totalSamplesL, _totalSamplesR;
    private int _clippedRunsL, _clippedRunsR;
    private int _consecutiveL, _consecutiveR;

    public void Init(int sampleRate)
    {
        _sampleRate = sampleRate;
        _blockSize = (int)(sampleRate * BlockSec);
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        AddChunkLeft(mono);
    }

    public void AddChunk(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            float sl = left[i];
            float absL = Math.Abs(sl);
            if (absL > _maxAbsL) _maxAbsL = absL;
            _sumSqL += (double)sl * sl;
            _consecutiveL = absL >= 1.0f ? _consecutiveL + 1 : 0;
            if (_consecutiveL == ClipRunMin) _clippedRunsL++;
            _totalSamplesL++;
            if (++_samplesInBlockL >= _blockSize) FlushBlockL();

            float sr = right[i];
            float absR = Math.Abs(sr);
            if (absR > _maxAbsR) _maxAbsR = absR;
            _sumSqR += (double)sr * sr;
            _consecutiveR = absR >= 1.0f ? _consecutiveR + 1 : 0;
            if (_consecutiveR == ClipRunMin) _clippedRunsR++;
            _totalSamplesR++;
            if (++_samplesInBlockR >= _blockSize) FlushBlockR();
        }

        if (left.Length > n) AddChunkLeft(left[n..]);
    }

    private void AddChunkLeft(ReadOnlySpan<float> mono)
    {
        for (int i = 0; i < mono.Length; i++)
        {
            float s = mono[i];
            double abs = Math.Abs(s);
            if (abs > _maxAbsL) _maxAbsL = abs;
            _sumSqL += (double)s * s;
            _consecutiveL = abs >= 1.0f ? _consecutiveL + 1 : 0;
            if (_consecutiveL == ClipRunMin) _clippedRunsL++;
            _totalSamplesL++;
            if (++_samplesInBlockL >= _blockSize) FlushBlockL();
        }
    }

    private void FlushBlockL()
    {
        if (_samplesInBlockL == 0) return;
        if (_maxAbsL > _globalPeakL) _globalPeakL = _maxAbsL;
        double rms = Math.Sqrt(_sumSqL / _samplesInBlockL);
        _rmsDbL.Add(20.0 * Math.Log10(Math.Max(rms, 1e-10)));
        _peakDbL.Add(20.0 * Math.Log10(Math.Max(_maxAbsL, 1e-10)));
        _sumSqL = _maxAbsL = 0; _samplesInBlockL = 0;
    }

    private void FlushBlockR()
    {
        if (_samplesInBlockR == 0) return;
        if (_maxAbsR > _globalPeakR) _globalPeakR = _maxAbsR;
        double rms = Math.Sqrt(_sumSqR / _samplesInBlockR);
        _rmsDbR.Add(20.0 * Math.Log10(Math.Max(rms, 1e-10)));
        _peakDbR.Add(20.0 * Math.Log10(Math.Max(_maxAbsR, 1e-10)));
        _sumSqR = _maxAbsR = 0; _samplesInBlockR = 0;
    }

    public DrResult GetResult()
    {
        FlushBlockL(); FlushBlockR();

        if (_rmsDbR.Count == 0)
        {
            _rmsDbR.AddRange(_rmsDbL);
            _peakDbR.AddRange(_peakDbL);
            _globalPeakR = _globalPeakL;
            _clippedRunsR = _clippedRunsL;
            _totalSamplesR = _totalSamplesL;
        }

        double drL = _rmsDbL.Count >= 5 ? ComputeDr(_rmsDbL, _peakDbL) : 0;
        double drR = _rmsDbR.Count >= 5 ? ComputeDr(_rmsDbR, _peakDbR) : drL;
        double dr = Math.Min(drL, drR);

        double peakL = _globalPeakL > 0 ? 20.0 * Math.Log10(_globalPeakL) : 0;
        double peakR = _globalPeakR > 0 ? 20.0 * Math.Log10(_globalPeakR) : 0;
        double peak = Math.Max(peakL, peakR);

        long totalSamples = Math.Max(_totalSamplesL, _totalSamplesR);
        double maxClipRuns = totalSamples / (double)ClipRunMin;
        double clipPct = maxClipRuns > 0
            ? Math.Max(_clippedRunsL, _clippedRunsR) / maxClipRuns * 100.0
            : 0;

        return new DrResult(
            Math.Round(dr, 0), Math.Round(drL, 0), Math.Round(drR, 0),
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
        _rmsDbL.Clear(); _peakDbL.Clear(); _rmsDbR.Clear(); _peakDbR.Clear();
        _globalPeakL = _globalPeakR = 0;
        _clippedRunsL = _clippedRunsR = _consecutiveL = _consecutiveR = 0;
        _totalSamplesL = _totalSamplesR = 0;
        _sumSqL = _sumSqR = _maxAbsL = _maxAbsR = 0;
        _samplesInBlockL = _samplesInBlockR = 0;

        if (buffer.IsStereo)
            AddChunk(buffer.Left, buffer.Right);
        else
            AddChunk(buffer.Left);

        return GetResult();
    }

    public (double dr, double samplePeakDb, double clippingPercent) Analyze(float[] samples, int sampleRate)
    {
        Init(sampleRate);
        _rmsDbL.Clear(); _peakDbL.Clear();
        _globalPeakL = _clippedRunsL = _consecutiveL = 0;
        _totalSamplesL = 0;
        _sumSqL = _maxAbsL = 0; _samplesInBlockL = 0;

        AddChunk(samples);

        double dr = _rmsDbL.Count >= 5 ? ComputeDr(_rmsDbL, _peakDbL) : 0;
        double peak = _globalPeakL > 0 ? 20.0 * Math.Log10(_globalPeakL) : 0;
        double maxClipRuns = _totalSamplesL / (double)ClipRunMin;
        double clipPct = maxClipRuns > 0 ? _clippedRunsL / maxClipRuns * 100.0 : 0;
        return (Math.Round(dr, 0), Math.Round(peak, 1), Math.Round(clipPct, 2));
    }
}

public record DrResult(double Dr, double DrLeft, double DrRight, double SamplePeakDb, double ClippingPercent);
