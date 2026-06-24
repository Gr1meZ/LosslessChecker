using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services.Analyzers;

public class TruePeakDetector : IChunkAccumulator<TruePeakResult>
{
    private const int OversampleFactor = 4;
    private const int ClipRunMin = 3;
    private const int FirTaps = 40;
    private const int PhaseTaps = FirTaps / OversampleFactor;

    private static readonly double[][] PolyphaseFilters = BuildPolyphaseFilters();

    private float _peakL, _peakR, _truePeakL, _truePeakR;
    private int _clippedRunsL, _clippedRunsR;
    private long _totalSamples;
    private int _consecutiveL, _consecutiveR;

    private readonly double[] _delayL = new double[PhaseTaps];
    private readonly double[] _delayR = new double[PhaseTaps];
    private int _delayIdxL, _delayIdxR;

    public void Reset()
    {
        _peakL = _peakR = _truePeakL = _truePeakR = 0;
        _clippedRunsL = _clippedRunsR = 0;
        _consecutiveL = _consecutiveR = 0;
        _totalSamples = 0;
        Array.Clear(_delayL, 0, PhaseTaps);
        Array.Clear(_delayR, 0, PhaseTaps);
        _delayIdxL = _delayIdxR = 0;
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        for (int i = 0; i < mono.Length; i++)
        {
            float s = mono[i];
            float abs = Math.Abs(s);
            if (abs > _peakL) _peakL = abs;
            if (abs > _truePeakL) _truePeakL = abs;

            _consecutiveL = abs >= 1.0f ? _consecutiveL + 1 : 0;
            if (_consecutiveL >= ClipRunMin) { _clippedRunsL++; _consecutiveL = 0; }

            ProcessSampleOversampleL(s);
            _totalSamples++;
        }
    }

    public void AddChunk(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            float l = left[i], r = right[i];
            float absL = Math.Abs(l), absR = Math.Abs(r);

            if (absL > _peakL) _peakL = absL;
            if (absR > _peakR) _peakR = absR;
            if (absL > _truePeakL) _truePeakL = absL;
            if (absR > _truePeakR) _truePeakR = absR;

            _consecutiveL = absL >= 1.0f ? _consecutiveL + 1 : 0;
            if (_consecutiveL >= ClipRunMin) { _clippedRunsL++; _consecutiveL = 0; }
            _consecutiveR = absR >= 1.0f ? _consecutiveR + 1 : 0;
            if (_consecutiveR >= ClipRunMin) { _clippedRunsR++; _consecutiveR = 0; }

            _totalSamples++;

            ProcessSampleOversampleL(l);
            ProcessSampleOversampleR(r);
        }
    }

    private void ProcessSampleOversampleL(double sample)
    {
        _delayL[_delayIdxL] = sample;
        _delayIdxL = (_delayIdxL + 1) % PhaseTaps;

        for (int phase = 0; phase < OversampleFactor; phase++)
        {
            double acc = 0;
            var phaseFilter = PolyphaseFilters[phase];
            int idx = _delayIdxL;
            for (int k = 0; k < PhaseTaps; k++)
            {
                idx = idx == 0 ? PhaseTaps - 1 : idx - 1;
                acc += phaseFilter[k] * _delayL[idx];
            }
            double absAcc = Math.Abs(acc);
            if (absAcc > _truePeakL) _truePeakL = (float)absAcc;
        }
    }

    private void ProcessSampleOversampleR(double sample)
    {
        _delayR[_delayIdxR] = sample;
        _delayIdxR = (_delayIdxR + 1) % PhaseTaps;

        for (int phase = 0; phase < OversampleFactor; phase++)
        {
            double acc = 0;
            var phaseFilter = PolyphaseFilters[phase];
            int idx = _delayIdxR;
            for (int k = 0; k < PhaseTaps; k++)
            {
                idx = idx == 0 ? PhaseTaps - 1 : idx - 1;
                acc += phaseFilter[k] * _delayR[idx];
            }
            double absAcc = Math.Abs(acc);
            if (absAcc > _truePeakR) _truePeakR = (float)absAcc;
        }
    }

    public TruePeakResult GetResult()
    {
        int maxClipRuns = _clippedRunsL > _clippedRunsR ? _clippedRunsL : _clippedRunsR;
        double clipPct = _totalSamples > 0
            ? (double)maxClipRuns / (_totalSamples / (double)ClipRunMin) * 100.0
            : 0;
        bool hasIsp = _truePeakL > 1.0f || _truePeakR > 1.0f;
        return new TruePeakResult(
            Math.Round(ToDb(_peakL), 1), Math.Round(ToDb(_peakR), 1),
            Math.Round(ToDb(_truePeakL), 1), Math.Round(ToDb(_truePeakR), 1),
            Math.Round(clipPct, 2), hasIsp);
    }

    public TruePeakResult Analyze(StereoBuffer buffer)
    {
        Reset();
        if (buffer.IsStereo) AddChunk(buffer.Left, buffer.Right);
        else AddChunk(buffer.Left);
        return GetResult();
    }

    private static double ToDb(float linear) => linear > 0 ? 20.0 * Math.Log10(linear) : -200.0;

    private static double[][] BuildPolyphaseFilters()
    {
        double[] fir = {
            -0.000003, -0.000018, -0.000026,  0.000066,  0.000222,  0.000119,
            -0.000489, -0.000738,  0.000889,  0.002327,  0.001263, -0.003958,
            -0.006047,  0.006114,  0.014570,  0.006536, -0.020278, -0.035127,
             0.035480,  0.108330,  0.108330,  0.035480, -0.035127, -0.020278,
             0.006536,  0.014570,  0.006114, -0.006047, -0.003958,  0.001263,
             0.002327,  0.000889, -0.000738, -0.000489,  0.000119,  0.000222,
             0.000066, -0.000026, -0.000018, -0.000003
        };

        var filters = new double[OversampleFactor][];
        for (int phase = 0; phase < OversampleFactor; phase++)
        {
            filters[phase] = new double[PhaseTaps];
            for (int k = 0; k < PhaseTaps; k++)
                filters[phase][k] = fir[k * OversampleFactor + phase];
        }
        return filters;
    }
}

public record TruePeakResult(double SamplePeakDbL, double SamplePeakDbR, double TruePeakDbL, double TruePeakDbR, double ClippingPercent, bool HasIsp);
