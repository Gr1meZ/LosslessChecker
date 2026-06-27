using System.Buffers;
using LosslessChecker.Models;
using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class SpectrogramAccumulator
{
    private const int FftSize = 4096;
    private const int FreqBins = 1024;
    private const int MaxCols = 2048;
    private const double DbFloor = -96.0;
    private const double DefaultDurationSec = 300.0;

    private readonly float[] _window = Window.Hann(FftSize);
    private float[][] _columns = Array.Empty<float[]>();
    private int[] _columnCounts = Array.Empty<int>();
    private int _colIndex;
    private int _currentMaxCols = MaxCols;
    private double _timePerColumn;
    private double _accumulatedTime;
    private double _totalDuration;
    private bool _durationKnown;
    private int _sampleRate;

    private double _globalMaxMag = 1e-10;

    public void Init(int sampleRate, double? totalDuration)
    {
        _sampleRate = sampleRate;
        if (totalDuration.HasValue && totalDuration.Value > 0)
        {
            _totalDuration = totalDuration.Value;
            _durationKnown = true;
            _timePerColumn = _totalDuration / MaxCols;
            _currentMaxCols = MaxCols;
        }
        else
        {
            _durationKnown = false;
            _totalDuration = DefaultDurationSec;
            _timePerColumn = _totalDuration / MaxCols;
            _currentMaxCols = MaxCols;
        }

        _columns = new float[_currentMaxCols][];
        _columnCounts = new int[_currentMaxCols];
        _colIndex = 0;
        _accumulatedTime = 0;
        _globalMaxMag = 1e-10;
    }

    public void AddChunk(AudioChunk chunk)
    {
        var mono = chunk.IsStereo ? MixToMono(chunk.Left.Span, chunk.Right.Span) : chunk.Left.Span;
        if (mono.Length < FftSize) return;

        double chunkDuration = (double)mono.Length / _sampleRate;
        double chunkTime = _accumulatedTime;

        using var fftOwner = new FftOwner(FftSize);
        var real = new float[FftSize];
        var imag = new float[FftSize];

        int hopSize = Math.Max(1, FftSize / 4);

        for (int pos = 0; pos + FftSize <= mono.Length; pos += hopSize)
        {
            double frameTime = chunkTime + (double)pos / _sampleRate;
            int col = (int)(frameTime / _timePerColumn);

            if (col >= _currentMaxCols)
            {
                if (!_durationKnown)
                    DownsampleColumns();
                col = Math.Min(col, _currentMaxCols - 1);
            }

            if (col < 0 || col >= _currentMaxCols) continue;

            for (int i = 0; i < FftSize; i++)
                fftOwner.Frame[i] = mono[pos + i] * _window[i];

            Array.Copy(fftOwner.Frame, real, FftSize);
            Array.Clear(imag, 0, FftSize);
            fftOwner.Fft.Direct(real, imag);

            if (_columns[col] == null)
                _columns[col] = new float[FreqBins];

            double nyquist = _sampleRate / 2.0;
            double logMin = Math.Log10(20.0);
            double logMax = Math.Log10(nyquist);
            double logRange = logMax - logMin;
            double binsPerHz = (double)(FftSize / 2) / nyquist;

            for (int j = 0; j < FreqBins; j++)
            {
                double freq = Math.Pow(10, logMin + logRange * j / (FreqBins - 1));
                double binIdx = freq * binsPerHz;
                int bin0 = Math.Clamp((int)binIdx, 0, FftSize / 2 - 1);
                int bin1 = Math.Min(bin0 + 1, FftSize / 2 - 1);
                double frac = binIdx - bin0;

                double mag = Math.Sqrt(
                    (double)real[bin0] * real[bin0] + (double)imag[bin0] * imag[bin0]);
                double mag1 = Math.Sqrt(
                    (double)real[bin1] * real[bin1] + (double)imag[bin1] * imag[bin1]);
                double interpMag = mag + (mag1 - mag) * frac;

                if (_columns[col][j] < interpMag)
                    _columns[col][j] = (float)interpMag;
            }

            _columnCounts[col]++;
            _globalMaxMag = Math.Max(_globalMaxMag, _columns[col].Max());
        }

        _accumulatedTime += chunkDuration;
    }

    public SpectrogramData Finalize()
    {
        int actualCols = 0;
        for (int i = 0; i < _currentMaxCols; i++)
            if (_columnCounts[i] > 0) actualCols = i + 1;
        if (actualCols == 0) actualCols = 1;

        var dbValues = new float[actualCols * FreqBins];
        double refMag = Math.Max(_globalMaxMag, 1e-10);

        for (int x = 0; x < actualCols; x++)
        {
            if (_columns[x] == null) continue;
            for (int y = 0; y < FreqBins; y++)
            {
                double db = 20.0 * Math.Log10(Math.Max(_columns[x][y], 1e-10) / refMag);
                dbValues[x * FreqBins + y] = (float)Math.Clamp((db - DbFloor) / (-DbFloor), 0, 1);
            }
        }

        return new SpectrogramData(dbValues, actualCols, FreqBins, _sampleRate, _totalDuration);
    }

    private void DownsampleColumns()
    {
        int newMax = _currentMaxCols / 2;
        if (newMax < MaxCols / 4) newMax = MaxCols / 4;
        var newCols = new float[newMax][];
        var newCounts = new int[newMax];

        for (int i = 0; i < newMax; i++)
        {
            int src0 = i * 2;
            int src1 = Math.Min(src0 + 1, _currentMaxCols - 1);
            if (_columns[src0] != null || _columns[src1] != null)
            {
                newCols[i] = new float[FreqBins];
                if (_columns[src0] != null && _columns[src1] != null)
                {
                    for (int j = 0; j < FreqBins; j++)
                        newCols[i][j] = Math.Max(_columns[src0][j], _columns[src1][j]);
                    newCounts[i] = _columnCounts[src0] + _columnCounts[src1];
                }
                else if (_columns[src0] != null)
                {
                    Array.Copy(_columns[src0], newCols[i], FreqBins);
                    newCounts[i] = _columnCounts[src0];
                }
                else
                {
                    Array.Copy(_columns[src1], newCols[i], FreqBins);
                    newCounts[i] = _columnCounts[src1];
                }
            }
        }

        _columns = newCols;
        _columnCounts = newCounts;
        _currentMaxCols = newMax;
        _timePerColumn *= 2;
    }

    private static float[] MixToMono(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        var mono = new float[n];
        for (int i = 0; i < n; i++) mono[i] = (left[i] + right[i]) * 0.5f;
        return mono;
    }

    private struct FftOwner : IDisposable
    {
        public readonly Fft Fft;
        public readonly float[] Frame;

        public FftOwner(int size)
        {
            Fft = new Fft(size);
            Frame = ArrayPool<float>.Shared.Rent(size);
        }

        public void Dispose()
        {
            ArrayPool<float>.Shared.Return(Frame);
        }
    }
}
