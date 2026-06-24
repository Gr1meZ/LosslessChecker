using LosslessChecker.Models;

namespace LosslessChecker.Services.Analyzers;

public class LufsMeter
{
    private const double BlockDuration = 0.4;
    private const double AbsoluteGate = -70.0;
    private const double RelativeGate = -10.0;

    public LufsResult Analyze(StereoBuffer buffer)
    {
        int sampleRate = buffer.SampleRate;
        int blockSize = (int)(sampleRate * BlockDuration);
        if (blockSize < 1 || buffer.Length < blockSize)
            return new LufsResult(-100, 0);

        var blockLoudness = new List<double>();
        int totalBlocks = 0;

        ResetFilters();

        for (int pos = 0; pos + blockSize <= buffer.Length; pos += blockSize)
        {
            double sumSq = 0;
            int len = Math.Min(blockSize, buffer.Length - pos);
            for (int i = pos; i < pos + len; i++)
            {
                double sample = buffer.IsStereo
                    ? (buffer.Left[i] + buffer.Right[i]) * 0.5
                    : buffer.Left[i];
                var filtered = KWeightFilter(sample);
                sumSq += filtered * filtered;
            }
            double rms = Math.Sqrt(sumSq / len);
            double loudness = -0.691 + 10.0 * Math.Log10(Math.Max(rms * rms, 1e-10));
            blockLoudness.Add(loudness);
            totalBlocks++;
        }

        if (totalBlocks == 0)
            return new LufsResult(-100, 0);

        // Relative gate: exclude blocks below absolute gate -10
        double absoluteSum = 0;
        int absoluteCount = 0;
        for (int i = 0; i < blockLoudness.Count; i++)
        {
            if (blockLoudness[i] > AbsoluteGate)
            {
                absoluteSum += Math.Pow(10, (blockLoudness[i]) / 10);
                absoluteCount++;
            }
        }

        double absoluteLoudness = absoluteCount > 0
            ? -0.691 + 10.0 * Math.Log10(absoluteSum / absoluteCount)
            : -70.0;

        double relativeThreshold = absoluteLoudness + RelativeGate;
        double gatedSum = 0;
        int gatedCount = 0;
        for (int i = 0; i < blockLoudness.Count; i++)
        {
            if (blockLoudness[i] > relativeThreshold)
            {
                gatedSum += Math.Pow(10, blockLoudness[i] / 10);
                gatedCount++;
            }
        }

        double integratedLufs = gatedCount > 0
            ? -0.691 + 10.0 * Math.Log10(gatedSum / gatedCount)
            : -70.0;

        // LRA (Loudness Range)
        var loudBlocks = blockLoudness
            .Where(b => b > AbsoluteGate)
            .OrderBy(b => b)
            .ToList();

        double lra = 0;
        if (loudBlocks.Count >= 10)
        {
            int lowIdx = (int)(loudBlocks.Count * 0.10);
            int highIdx = (int)(loudBlocks.Count * 0.95);
            lra = loudBlocks[highIdx] - loudBlocks[lowIdx];
        }

        return new LufsResult(
            Math.Round(integratedLufs, 1),
            Math.Round(lra, 1));
    }

    // Simplified stable K-weighting: first-order high-pass + mild high-shelf.
    private double _hpX1, _hpY1;
    private double _shX1, _shY1;

    private void ResetFilters()
    {
        _hpX1 = _hpY1 = 0;
        _shX1 = _shY1 = 0;
    }

    private double KWeightFilter(double sample)
    {
        // First-order high-pass at ~100 Hz
        // H(z) = 0.99 * (1 - z^-1) / (1 - 0.99*z^-1)
        double hpOut = 0.99 * _hpY1 + 0.99 * (sample - _hpX1);
        _hpX1 = sample;
        _hpY1 = hpOut;

        // Mild high-shelf: output = input + 0.25 * (input filtered to preserve highs)
        // Simple approach: y[n] = hpOut + 0.2 * (hpOut - _shX1)
        double shOut = hpOut + 0.2 * (hpOut - _shX1);
        _shX1 = hpOut;

        return shOut;
    }
}

public record LufsResult(double IntegratedLufs, double LoudnessRange);
