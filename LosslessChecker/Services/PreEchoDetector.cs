using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services;

public class PreEchoDetector : IChunkAccumulator<(bool hasPreEcho, int preEchoCount)>
{
    private const int WindowMs = 2;
    private const int MaxWindows = 500;
    private const double TransientThreshold = 4.0;
    private const int MinPreEchoCount = 3;

    private int _sampleRate, _windowSamples;
    private readonly CircularBuffer<double> _rmsBuffer = new(MaxWindows);
    private int _preEchoCount;
    private bool _initialized;

    public void Init(int sampleRate)
    {
        _sampleRate = sampleRate;
        _windowSamples = sampleRate * WindowMs / 1000;
        if (_windowSamples < 1) _windowSamples = 1;
        _initialized = true;
    }

    public void Reset()
    {
        _preEchoCount = 0;
        _rmsBuffer.Clear();
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        if (!_initialized) throw new InvalidOperationException("Init not called");

        for (int pos = 0; pos + _windowSamples * 2 <= mono.Length; pos += _windowSamples)
        {
            double rmsBefore = ComputeRms(mono, pos, _windowSamples);
            double rmsAfter = ComputeRms(mono, pos + _windowSamples, _windowSamples);

            if (_rmsBuffer.Count > 0)
            {
                double prevRms = _rmsBuffer.Last();
                if (rmsAfter > prevRms * TransientThreshold && rmsBefore > rmsAfter * 0.15)
                    _preEchoCount++;
            }

            _rmsBuffer.Push(rmsAfter);
        }
    }

    public (bool hasPreEcho, int preEchoCount) GetResult()
    {
        return (_preEchoCount > MinPreEchoCount, _preEchoCount);
    }

    private static double ComputeRms(ReadOnlySpan<float> samples, int offset, int count)
    {
        double sumSq = 0;
        int end = Math.Min(offset + count, samples.Length);
        for (int i = offset; i < end; i++)
        {
            double s = samples[i];
            sumSq += s * s;
        }
        int n = end - offset;
        return n > 0 ? Math.Sqrt(sumSq / n) : 0;
    }

    private class CircularBuffer<T>
    {
        private readonly T[] _buf;
        private int _writePos, _count;
        public CircularBuffer(int capacity) => _buf = new T[capacity];
        public int Count => _count;
        public T Last() => _buf[(_writePos - 1 + _buf.Length) % _buf.Length];
        public void Push(T val) { _buf[_writePos] = val; _writePos = (_writePos + 1) % _buf.Length; if (_count < _buf.Length) _count++; }
        public void Clear() { _writePos = _count = 0; Array.Clear(_buf, 0, _buf.Length); }
    }
}
