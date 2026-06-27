using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services;

public class BitDepthValidator : IChunkAccumulator<BitDepthResult>
{
    private readonly List<double> _blockRms = new();
    private double _sumSqInBlock;
    private int _samplesInBlock, _blockSize;
    private int _sampleRate;
    private readonly List<double> _blockMaxAbs = new();
    private double _maxAbsInBlock;
    private bool _initialized;

    public void Reset()
    {
        _blockRms.Clear(); _blockMaxAbs.Clear();
        _sumSqInBlock = _maxAbsInBlock = 0; _samplesInBlock = 0;
    }

    private void Init(int sampleRate)
    {
        _sampleRate = sampleRate;
        _blockSize = Math.Max(1, sampleRate / 10);
        _initialized = true;
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        for (int i = 0; i < mono.Length; i++)
        {
            float s = mono[i];
            _sumSqInBlock += s * s;
            float abs = Math.Abs(s);
            if (abs > _maxAbsInBlock) _maxAbsInBlock = abs;
            if (++_samplesInBlock >= _blockSize) FlushBlock();
        }
    }

    private void FlushBlock()
    {
        if (_samplesInBlock == 0) return;
        _blockRms.Add(Math.Sqrt(_sumSqInBlock / _samplesInBlock));
        _blockMaxAbs.Add(_maxAbsInBlock);
        _sumSqInBlock = 0; _maxAbsInBlock = 0; _samplesInBlock = 0;
    }

    public BitDepthResult GetResult()
    {
        FlushBlock();
        return BuildResult(24); // Default 24-bit for stereo path; overridden below
    }

    public BitDepthResult GetResult(int claimedBitDepth)
    {
        FlushBlock();
        return BuildResult(claimedBitDepth);
    }

    private BitDepthResult BuildResult(int claimedBitDepth)
    {
        if (_blockRms.Count < 5)
            return new BitDepthResult(false, "Insufficient blocks", 0, false, claimedBitDepth);

        var sortedRms = new List<double>(_blockRms); sortedRms.Sort();
        int quietCount = Math.Max(1, sortedRms.Count / 10);
        double quietRms = sortedRms.Take(quietCount).Average();
        double noiseFloorDb = 20.0 * Math.Log10(Math.Max(quietRms, 1e-10));

        int expectedNoiseFloor = claimedBitDepth * -6;
        double toleranceDb = 16;
        bool noiseFloorSuspicious = noiseFloorDb < -50 && noiseFloorDb > expectedNoiseFloor + toleranceDb;
        bool lsbZero = false; // LSB check requires original samples — deferred to ValidateStereo
        bool suspicious = noiseFloorSuspicious || lsbZero;

        int effectiveBits = noiseFloorDb < -50 ? (int)Math.Round(-noiseFloorDb / 6.0) : claimedBitDepth;
        effectiveBits = Math.Min(effectiveBits, claimedBitDepth);

        string verdict = lsbZero
            ? $"Claimed {claimedBitDepth}-bit but lower 8 bits are zero-padded (effective {effectiveBits}-bit)."
            : noiseFloorDb > expectedNoiseFloor + toleranceDb
                ? $"Claimed {claimedBitDepth}-bit but noise floor at {noiseFloorDb:F0} dB = ~{effectiveBits}-bit effective."
                : $"{claimedBitDepth}-bit integrity confirmed.";

        return new BitDepthResult(suspicious, verdict, Math.Round(noiseFloorDb, 1), lsbZero, effectiveBits);
    }

    public bool CheckLsbZeroPadded(float[] samples, int claimedBitDepth)
    {
        if (claimedBitDepth != 24 || samples.Length < 1000) return false;
        int blockSize = samples.Length / 100;
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
        int zeroCount = 0, totalCount = 0;
        for (int pos = 0; pos + blockSize <= samples.Length; pos += blockSize)
        {
            double maxAbs = 0;
            for (int i = pos; i < pos + blockSize; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(samples[i]));
            if (maxAbs < sortedBlocks[Math.Min(loudCount - 1, sortedBlocks.Count - 1)]) continue;
            for (int i = pos; i < pos + blockSize; i++)
            {
                int sample24 = (int)(samples[i] * 8388607.0 + 0.5 * Math.Sign(samples[i]));
                if ((sample24 & 0xFF) == 0) zeroCount++;
                totalCount++;
            }
        }
        return totalCount > 100 && (double)zeroCount / totalCount > 0.95;
    }

    public (bool, string, double) Validate(float[] samples, int claimedBitDepth, int sampleRate)
    {
        if (!_initialized) Init(sampleRate);
        _blockRms.Clear(); _blockMaxAbs.Clear();
        _sumSqInBlock = _maxAbsInBlock = 0; _samplesInBlock = 0;
        AddChunk(samples);
        var result = GetResult(claimedBitDepth);
        return (result.IsSuspicious, result.Verdict, result.NoiseFloorDb);
    }

    public BitDepthResult ValidateStereo(StereoBuffer buffer, int claimedBitDepth)
    {
        if (!_initialized) Init(buffer.SampleRate);
        _blockRms.Clear(); _blockMaxAbs.Clear();
        _sumSqInBlock = _maxAbsInBlock = 0; _samplesInBlock = 0;
        int n = buffer.Length;
        for (int i = 0; i < n; i++)
        {
            float s = buffer.IsStereo ? (buffer.Left[i] + buffer.Right[i]) * 0.5f : buffer.Left[i];
            _sumSqInBlock += s * s;
            float abs = Math.Abs(s);
            if (abs > _maxAbsInBlock) _maxAbsInBlock = abs;
            if (++_samplesInBlock >= _blockSize) FlushBlock();
        }
        var result = GetResult(claimedBitDepth);
        bool lsbZero = CheckLsbZeroPaddedFull(buffer, claimedBitDepth);
        return new BitDepthResult(result.IsSuspicious || lsbZero, lsbZero
            ? $"Claimed {claimedBitDepth}-bit but lower 8 bits are zero-padded (effective {result.EffectiveBitDepth}-bit)."
            : result.Verdict, result.NoiseFloorDb, lsbZero, result.EffectiveBitDepth);
    }

    private static bool CheckLsbZeroPaddedFull(StereoBuffer buffer, int claimedBitDepth)
    {
        if (claimedBitDepth != 24 || buffer.Length < 1000) return false;
        int n = buffer.Length;
        int blockSize = n / 100;

        // Check each channel independently — averaging can mask zero-padding
        bool lsbZeroL = CheckLsbZeroPaddedChannel(buffer.Left, n, blockSize);
        bool lsbZeroR = buffer.IsStereo ? CheckLsbZeroPaddedChannel(buffer.Right, n, blockSize) : lsbZeroL;
        return lsbZeroL && lsbZeroR;
    }

    private static bool CheckLsbZeroPaddedChannel(float[] channel, int n, int blockSize)
    {
        // Find loud blocks threshold from sorted maxAbs
        var sortedBlocks = new List<double>();
        for (int pos = 0; pos + blockSize <= n; pos += blockSize)
        {
            double maxAbs = 0;
            for (int i = pos; i < pos + blockSize; i++)
            {
                double abs = Math.Abs(channel[i]);
                if (abs > maxAbs) maxAbs = abs;
            }
            sortedBlocks.Add(maxAbs);
        }
        sortedBlocks.Sort((a, b) => b.CompareTo(a));
        int loudCount = Math.Max(1, sortedBlocks.Count / 10);
        double loudThreshold = sortedBlocks[Math.Min(loudCount - 1, sortedBlocks.Count - 1)];

        int zeroCount = 0, totalCount = 0;
        for (int pos = 0; pos + blockSize <= n; pos += blockSize)
        {
            double maxAbs = 0;
            for (int i = pos; i < pos + blockSize; i++)
            {
                double abs = Math.Abs(channel[i]);
                if (abs > maxAbs) maxAbs = abs;
            }
            if (maxAbs < loudThreshold) continue;
            for (int i = pos; i < pos + blockSize; i++)
            {
                int sample24 = (int)(channel[i] * 8388607.0 + 0.5 * Math.Sign(channel[i]));
                if ((sample24 & 0xFF) == 0) zeroCount++;
                totalCount++;
            }
        }
        return totalCount > 100 && (double)zeroCount / totalCount > 0.95;
    }
}

public record BitDepthResult(bool IsSuspicious, string Verdict, double NoiseFloorDb, bool LsbZeroPadded, int EffectiveBitDepth);
