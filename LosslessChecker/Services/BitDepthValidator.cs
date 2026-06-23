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
}
