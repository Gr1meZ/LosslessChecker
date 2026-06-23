using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class BitDepthValidator
{
    public (bool isSuspicious, string verdict, double noiseFloorDb) Validate(
        float[] samples, int claimedBitDepth, int sampleRate)
    {
        if (samples.Length < sampleRate)
            return (false, "Too short for noise floor analysis", 0);

        // Take the quietest ~10% of the signal to estimate noise floor
        int blockSize = sampleRate / 10;
        var blockRms = new List<double>();

        for (int pos = 0; pos + blockSize <= samples.Length; pos += blockSize)
        {
            double sumSq = 0;
            for (int i = pos; i < pos + blockSize; i++)
                sumSq += samples[i] * samples[i];
            blockRms.Add(Math.Sqrt(sumSq / blockSize));
        }

        if (blockRms.Count < 5)
            return (false, "Insufficient blocks", 0);

        blockRms.Sort();
        int quietCount = Math.Max(1, blockRms.Count / 10);
        double quietRms = blockRms.Take(quietCount).Average();
        double noiseFloorDb = 20.0 * Math.Log10(Math.Max(quietRms, 1e-10));

        int expectedNoiseFloor = claimedBitDepth * -6;
        double toleranceDb = 16; // Allow 16 dB above theoretical minimum

        if (noiseFloorDb > expectedNoiseFloor + toleranceDb)
        {
            int effectiveBits = (int)Math.Round(-noiseFloorDb / 6.0);
            return (true,
                $"Claimed {claimedBitDepth}-bit but noise floor at {noiseFloorDb:F0} dB = ~{effectiveBits}-bit effective. Padded container.",
                noiseFloorDb);
        }

        return (false, $"{claimedBitDepth}-bit integrity confirmed (noise floor {noiseFloorDb:F0} dB).", noiseFloorDb);
    }

    public bool CheckLsbZeroPadded(float[] samples, int claimedBitDepth)
    {
        if (claimedBitDepth != 24 || samples.Length < 1000)
            return false;

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
            if (maxAbs < sortedBlocks[Math.Min(loudCount - 1, sortedBlocks.Count - 1)])
                continue;

            for (int i = pos; i < pos + blockSize; i++)
            {
                int sample24 = (int)Math.Round(samples[i] * 8388607.0);
                if ((sample24 & 0xFF) == 0)
                    zeroCount++;
                totalCount++;
            }
        }

        return totalCount > 100 && (double)zeroCount / totalCount > 0.95;
    }

    public BitDepthResult ValidateStereo(StereoBuffer buffer, int claimedBitDepth)
    {
        int n = buffer.Length;
        var mono = new float[n];
        for (int i = 0; i < n; i++)
            mono[i] = buffer.IsStereo
                ? (buffer.Left[i] + buffer.Right[i]) * 0.5f
                : buffer.Left[i];

        int blockSize = Math.Max(1, buffer.SampleRate / 10);
        var blockRms = new List<double>();
        for (int pos = 0; pos + blockSize <= n; pos += blockSize)
        {
            double sumSq = 0;
            for (int i = pos; i < pos + blockSize; i++)
                sumSq += mono[i] * mono[i];
            blockRms.Add(Math.Sqrt(sumSq / blockSize));
        }

        if (blockRms.Count < 5)
            return new BitDepthResult(false, "Insufficient blocks", 0, false, claimedBitDepth);

        blockRms.Sort();
        int quietCount = Math.Max(1, blockRms.Count / 10);
        double quietRms = blockRms.Take(quietCount).Average();
        double noiseFloorDb = 20.0 * Math.Log10(Math.Max(quietRms, 1e-10));

        int expectedNoiseFloor = claimedBitDepth * -6;
        double toleranceDb = 16;

        bool lsbZero = CheckLsbZeroPadded(mono, claimedBitDepth);
        bool suspicious = noiseFloorDb > expectedNoiseFloor + toleranceDb || lsbZero;

        int effectiveBits = (int)Math.Round(-noiseFloorDb / 6.0);
        effectiveBits = Math.Min(effectiveBits, claimedBitDepth);

        string verdict;
        if (lsbZero)
            verdict = $"Claimed {claimedBitDepth}-bit but lower 8 bits are zero-padded (effective {effectiveBits}-bit).";
        else if (noiseFloorDb > expectedNoiseFloor + toleranceDb)
            verdict = $"Claimed {claimedBitDepth}-bit but noise floor at {noiseFloorDb:F0} dB = ~{effectiveBits}-bit effective.";
        else
            verdict = $"{claimedBitDepth}-bit integrity confirmed.";

        return new BitDepthResult(suspicious, verdict, Math.Round(noiseFloorDb, 1), lsbZero, effectiveBits);
    }
}

public record BitDepthResult(
    bool IsSuspicious, string Verdict, double NoiseFloorDb,
    bool LsbZeroPadded, int EffectiveBitDepth);
