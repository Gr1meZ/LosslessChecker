using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services;

public class DrMeter : IChunkAccumulator<DrResult>
{
    private const double BlockSec = 3.0;
    private const double TopPct = 0.20;
    private const double TrimPct = 0.10;
    private const double CalibrationDb = 2.65;
    private const int ClipRunMin = 3;

    private readonly List<double> _rmsDbL = new(), _peakDbL = new();
    private readonly List<double> _rmsDbR = new(), _peakDbR = new();
    private double _sumSqL, _sumSqR, _maxAbsL, _maxAbsR;
    private double _globalPeakL, _globalPeakR;
    private int _samplesInBlockL, _samplesInBlockR;
    private int _blockSize, _sampleRate;
    private int _clippedRuns;
    private int _consecutive;
    private bool _initialized; // reserved for future chunked API use

    public void Init(int sampleRate)
    {
        _sampleRate = sampleRate;
        _blockSize = (int)(sampleRate * BlockSec);
        _initialized = true;
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        for (int i = 0; i < mono.Length; i++)
        {
            float s = mono[i];
            double abs = Math.Abs(s);
            if (abs > _maxAbsL) _maxAbsL = abs;
            _sumSqL += (double)s * s;
            if (abs >= 1.0f)
            {
                _consecutive++;
                if (_consecutive == ClipRunMin) _clippedRuns++;
            }
            else _consecutive = 0;
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
        if (_rmsDbR.Count == 0) { _rmsDbR.AddRange(_rmsDbL); _peakDbR.AddRange(_peakDbL); }
        double drL = _rmsDbL.Count >= 5 ? ComputeDr(_rmsDbL, _peakDbL) : 0;
        double drR = _rmsDbR.Count >= 5 ? ComputeDr(_rmsDbR, _peakDbR) : drL;
        double dr = Math.Min(drL, drR);
        double peak = _globalPeakL > 0 ? 20.0 * Math.Log10(_globalPeakL) : 0;
        if (_globalPeakR > _globalPeakL) peak = 20.0 * Math.Log10(_globalPeakR);
        int totalRuns = _samplesInBlockL + _rmsDbL.Count * _blockSize;
        double clipPct = totalRuns > 0 ? (double)_clippedRuns / (totalRuns / (double)ClipRunMin) * 100.0 : 0;
        return new DrResult(Math.Round(dr, 0), Math.Round(drL, 0), Math.Round(drR, 0), Math.Round(peak, 1), Math.Round(clipPct, 2));
    }

    private static double ComputeDr(List<double> rmsDb, List<double> peakDb)
    {
        var indexed = rmsDb.Select((r, i) => (r, p: peakDb[i])).OrderByDescending(x => x.r).ToList();
        int top20 = Math.Max(1, (int)(indexed.Count * TopPct));
        var top = indexed.Take(top20).ToList();
        int trim = (int)(top.Count * TrimPct);
        var work = top.Skip(trim).ToList();
        if (work.Count == 0) work = top;
        double dr = work.Average(x => x.p) - work.Average(x => x.r) - CalibrationDb;
        return dr < 0 ? 0 : dr;
    }

    public DrResult AnalyzeStereo(StereoBuffer buffer)
    {
        Init(buffer.SampleRate);
        _rmsDbL.Clear(); _peakDbL.Clear(); _rmsDbR.Clear(); _peakDbR.Clear();
        _globalPeakL = _globalPeakR = _clippedRuns = _consecutive = 0;
        AddChunk(buffer.Left);
        FlushBlockL();
        if (buffer.IsStereo) { AddChunk(buffer.Right); FlushBlockR(); }
        return GetResult();
    }

    public (double dr, double samplePeakDb, double clippingPercent) Analyze(float[] samples, int sampleRate)
    {
        Init(sampleRate);
        _rmsDbL.Clear(); _peakDbL.Clear(); _globalPeakL = _clippedRuns = _consecutive = 0;
        AddChunk(samples);
        FlushBlockL();
        double dr = _rmsDbL.Count >= 5 ? ComputeDr(_rmsDbL, _peakDbL) : 0;
        double peak = _globalPeakL > 0 ? 20.0 * Math.Log10(_globalPeakL) : 0;
        int totalRuns = samples.Length;
        double clipPct = totalRuns > 0 ? (double)_clippedRuns / (totalRuns / (double)ClipRunMin) * 100.0 : 0;
        return (Math.Round(dr, 0), Math.Round(peak, 1), Math.Round(clipPct, 2));
    }
}

public record DrResult(double Dr, double DrLeft, double DrRight, double SamplePeakDb, double ClippingPercent);
