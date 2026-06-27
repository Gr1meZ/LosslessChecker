using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services;

public class BitDepthValidator : IChunkAccumulator<BitDepthResult>
{
    private const int BlockSize = 4096;
    private const double HistoDbMin = -144.0;
    private const double HistoDbStep = 0.1;
    private const int HistoBins = 1440;
    private const double SafetyGateDb = -40.0;
    private const double CumulativeThreshold = 0.01;

    private readonly int[] _rmsHistogram = new int[HistoBins];
    private long _totalBlocks;

    private double _sumSqInBlock;
    private int _samplesInBlock;
    private bool _hasSeenChunks;

    private int _lsbZeroCount;
    private int _lsbTotalCount;
    private readonly Dictionary<int, int> _lsbConstantMap = new();

    public void Reset()
    {
        Array.Clear(_rmsHistogram, 0, HistoBins);
        _totalBlocks = 0;
        _sumSqInBlock = 0;
        _samplesInBlock = 0;
        _hasSeenChunks = false;
        _lsbZeroCount = _lsbTotalCount = 0;
        _lsbConstantMap.Clear();
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        _hasSeenChunks = true;
        for (int i = 0; i < mono.Length; i++)
        {
            float s = mono[i];
            _sumSqInBlock += s * s;

            if ((i & 0xF) == 0)
            {
                int sample24 = (int)(s * 8388607.0 + 0.5 * Math.Sign(s));
                int lsb = sample24 & 0xFF;
                if (lsb == 0) _lsbZeroCount++;
                if (_lsbConstantMap.ContainsKey(lsb)) _lsbConstantMap[lsb]++;
                else _lsbConstantMap[lsb] = 1;
                _lsbTotalCount++;
            }

            if (++_samplesInBlock >= BlockSize)
            {
                double rms = Math.Sqrt(_sumSqInBlock / BlockSize);
                double rmsDb = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
                int bucket = (int)((rmsDb - HistoDbMin) / HistoDbStep);
                bucket = Math.Clamp(bucket, 0, HistoBins - 1);
                _rmsHistogram[bucket]++;
                _totalBlocks++;
                _sumSqInBlock = 0;
                _samplesInBlock = 0;
            }
        }
    }

    public BitDepthResult GetResult(int claimedBitDepth)
    {
        if (_samplesInBlock > 0)
        {
            double rms = Math.Sqrt(_sumSqInBlock / _samplesInBlock);
            double rmsDb = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
            int bucket = (int)((rmsDb - HistoDbMin) / HistoDbStep);
            bucket = Math.Clamp(bucket, 0, HistoBins - 1);
            _rmsHistogram[bucket]++;
            _totalBlocks++;
        }
        return BuildResult(claimedBitDepth);
    }

    public BitDepthResult GetResult() => GetResult(24);

    public (bool lsbZero, bool lsbConstant) CheckLsbFlags(int claimedBitDepth)
    {
        if (claimedBitDepth != 24 || _lsbTotalCount < 100)
            return (false, false);
        bool lsbZero = (double)_lsbZeroCount / _lsbTotalCount > 0.95;
        bool lsbConstant = false;
        foreach (var kv in _lsbConstantMap)
            if (kv.Key != 0 && (double)kv.Value / _lsbTotalCount > 0.95)
                lsbConstant = true;
        return (lsbZero, lsbConstant);
    }

    private BitDepthResult BuildResult(int claimedBitDepth)
    {
        if (_totalBlocks < 5)
            return new BitDepthResult(false, "Insufficient blocks", 0, false, claimedBitDepth);

        long cumulSum = 0;
        double noiseFloorDb = HistoDbMin;
        bool foundNoiseFloor = false;

        for (int b = 0; b < HistoBins; b++)
        {
            cumulSum += _rmsHistogram[b];
            if ((double)cumulSum / _totalBlocks >= CumulativeThreshold)
            {
                noiseFloorDb = HistoDbMin + b * HistoDbStep;
                foundNoiseFloor = true;
                break;
            }
        }

        if (!foundNoiseFloor)
            noiseFloorDb = 0;

        if (noiseFloorDb > SafetyGateDb)
        {
            return new BitDepthResult(false,
                $"No quiet sections detected (noise floor estimate {noiseFloorDb:F0} dB > {SafetyGateDb:F0} gate). Effective bit depth analysis skipped.",
                Math.Round(noiseFloorDb, 1), false, claimedBitDepth);
        }

        int effectiveBits = Math.Min((int)Math.Round(-noiseFloorDb / 6.0), claimedBitDepth);
        double expectedNoiseFloor = claimedBitDepth * -6;
        bool suspicious = noiseFloorDb > expectedNoiseFloor + 16;

        return new BitDepthResult(suspicious,
            suspicious
                ? $"Claimed {claimedBitDepth}-bit but noise floor at {noiseFloorDb:F0} dB = ~{effectiveBits}-bit effective."
                : $"{claimedBitDepth}-bit integrity confirmed.",
            Math.Round(noiseFloorDb, 1), false, effectiveBits);
    }

    public bool CheckLsbZeroPadded(float[] samples, int claimedBitDepth)
    {
        if (claimedBitDepth != 24 || samples.Length < 1000) return false;
        int blockSize = Math.Max(100, samples.Length / 100);
        var sortedBlocks = new List<double>();
        for (int pos = 0; pos + blockSize <= samples.Length; pos += blockSize)
        {
            double maxAbs = 0;
            for (int i = pos; i < pos + blockSize; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(samples[i]));
            sortedBlocks.Add(maxAbs);
        }
        sortedBlocks.Sort((a, b) => b.CompareTo(a));
        int loudCount = Math.Max(1, sortedBlocks.Count / 10);
        double loudThreshold = sortedBlocks[Math.Min(loudCount - 1, sortedBlocks.Count - 1)];

        int zeroCount = 0, totalCount = 0;
        var constantValues = new Dictionary<int, int>();
        for (int pos = 0; pos + blockSize <= samples.Length; pos += blockSize)
        {
            double maxAbs = 0;
            for (int i = pos; i < pos + blockSize; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(samples[i]));
            if (maxAbs < loudThreshold) continue;
            for (int i = pos; i < pos + blockSize; i++)
            {
                int sample24 = (int)(samples[i] * 8388607.0 + 0.5 * Math.Sign(samples[i]));
                int lsb = sample24 & 0xFF;
                if (lsb == 0) zeroCount++;
                if (constantValues.ContainsKey(lsb)) constantValues[lsb]++;
                else constantValues[lsb] = 1;
                totalCount++;
            }
        }

        if (totalCount < 100) return false;

        // 95% zero LSBs => zero-padded
        if ((double)zeroCount / totalCount > 0.95) return true;

        // 95% same non-zero LSB value => constant dither / naive upscale
        foreach (var kv in constantValues)
        {
            if (kv.Key != 0 && (double)kv.Value / totalCount > 0.95)
                return true;
        }

        return false;
    }

    public BitDepthResult ValidateStereo(StereoBuffer buffer, int claimedBitDepth)
    {
        Reset();
        if (!buffer.IsStereo)
            AddChunk(buffer.Left);
        else
        {
            int n = buffer.Length;
            AddChunk(ToMono(buffer.Left, buffer.Right, n));
        }
        var result = GetResult(claimedBitDepth);
        bool lsbZero = CheckLsbZeroPaddedFull(buffer, claimedBitDepth);
        return new BitDepthResult(
            result.IsSuspicious || lsbZero,
            lsbZero
                ? $"Claimed {claimedBitDepth}-bit but lower 8 bits are zero-padded (or constant dither pattern)."
                : result.Verdict,
            result.NoiseFloorDb, lsbZero, result.EffectiveBitDepth);
    }

    private static float[] ToMono(float[] left, float[] right, int n)
    {
        var mono = new float[n];
        for (int i = 0; i < n; i++) mono[i] = (left[i] + right[i]) * 0.5f;
        return mono;
    }

    private static bool CheckLsbZeroPaddedFull(StereoBuffer buffer, int claimedBitDepth)
    {
        if (claimedBitDepth != 24 || buffer.Length < 1000) return false;
        int n = buffer.Length;
        int blockSize = Math.Max(100, n / 100);
        bool lsbL = CheckLsbZeroPaddedChannel(buffer.Left, n, blockSize);
        bool lsbR = buffer.IsStereo ? CheckLsbZeroPaddedChannel(buffer.Right, n, blockSize) : lsbL;
        return lsbL && lsbR;
    }

    private static bool CheckLsbZeroPaddedChannel(float[] channel, int n, int blockSize)
    {
        var sortedBlocks = new List<double>();
        for (int pos = 0; pos + blockSize <= n; pos += blockSize)
        {
            double maxAbs = 0;
            for (int i = pos; i < pos + blockSize; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(channel[i]));
            sortedBlocks.Add(maxAbs);
        }
        sortedBlocks.Sort((a, b) => b.CompareTo(a));
        int loudCount = Math.Max(1, sortedBlocks.Count / 10);
        double loudThreshold = sortedBlocks[Math.Min(loudCount - 1, sortedBlocks.Count - 1)];

        int zeroCount = 0, totalCount = 0;
        var constantValues = new Dictionary<int, int>();
        for (int pos = 0; pos + blockSize <= n; pos += blockSize)
        {
            double maxAbs = 0;
            for (int i = pos; i < pos + blockSize; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(channel[i]));
            if (maxAbs < loudThreshold) continue;
            for (int i = pos; i < pos + blockSize; i++)
            {
                int sample24 = (int)(channel[i] * 8388607.0 + 0.5 * Math.Sign(channel[i]));
                int lsb = sample24 & 0xFF;
                if (lsb == 0) zeroCount++;
                if (constantValues.ContainsKey(lsb)) constantValues[lsb]++;
                else constantValues[lsb] = 1;
                totalCount++;
            }
        }
        if (totalCount < 100) return false;
        if ((double)zeroCount / totalCount > 0.95) return true;
        foreach (var kv in constantValues)
            if (kv.Key != 0 && (double)kv.Value / totalCount > 0.95) return true;
        return false;
    }
}

public record BitDepthResult(bool IsSuspicious, string Verdict, double NoiseFloorDb, bool LsbZeroPadded, int EffectiveBitDepth);
