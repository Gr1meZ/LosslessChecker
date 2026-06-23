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
                double sample = (buffer.Left[i] + buffer.Right[i]) * 0.5;
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

    private double _x1p, _x2p, _y1p, _y2p;
    private double _x1s, _x2s, _y1s, _y2s;

    private void ResetFilters()
    {
        _x1p = _x2p = _y1p = _y2p = 0;
        _x1s = _x2s = _y1s = _y2s = 0;
    }

    private double KWeightFilter(double sample)
    {
        // Pre-filter (high-pass, second-order) — ITU-R BS.1770-4 Table 1 coefficients for 48kHz
        const double a1_p = -1.69065929318241;
        const double a2_p = 0.73248077421585;
        const double b0_p = 1.53512485958697;
        const double b1_p = -2.69169618940638;
        const double b2_p = 1.19839281085285;

        double preOut = b0_p * sample + b1_p * _x1p + b2_p * _x2p
                      - a1_p * _y1p - a2_p * _y2p;
        _x2p = _x1p; _x1p = sample;
        _y2p = _y1p; _y1p = preOut;

        // High-shelf (+4 dB, second-order) — ITU-R BS.1770-4 Table 2 coefficients for 48kHz
        const double a1_s = -1.99004745483398;
        const double a2_s = 0.99007225036621;

        double shelfOut = preOut + _x1s * (-2.0) + _x2s
                        - a1_s * _y1s - a2_s * _y2s;
        _x2s = _x1s; _x1s = preOut;
        _y2s = _y1s; _y1s = shelfOut;

        return shelfOut;
    }
}

public record LufsResult(double IntegratedLufs, double LoudnessRange);
