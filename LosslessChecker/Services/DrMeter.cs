namespace LosslessChecker.Services;

public class DrMeter
{
    private const double BlockDurationSec = 0.5;
    private const double TopPercentile = 0.2;

    public (double dr, double truePeakDb, double clippingPercent) Analyze(float[] samples, int sampleRate)
    {
        int blockSize = (int)(sampleRate * BlockDurationSec);
        if (blockSize < 1 || samples.Length < blockSize)
            return (0, 0, 0);

        var blockRms = new List<double>();
        double truePeakLinear = double.MinValue;
        int clippedSamples = 0;

        for (int pos = 0; pos < samples.Length; pos += blockSize)
        {
            int len = Math.Min(blockSize, samples.Length - pos);
            double sumSq = 0;
            for (int i = pos; i < pos + len; i++)
            {
                var abs = Math.Abs(samples[i]);
                if (abs > truePeakLinear)
                    truePeakLinear = abs;
                if (abs >= 1.0)
                    clippedSamples++;
                sumSq += samples[i] * samples[i];
            }
            blockRms.Add(Math.Sqrt(sumSq / len));
        }

        double truePeakDb = truePeakLinear > double.MinValue
            ? 20.0 * Math.Log10(Math.Max(truePeakLinear, 1e-10))
            : 0.0;
        double clippingPercent = (double)clippedSamples / samples.Length * 100.0;

        blockRms.Sort((a, b) => b.CompareTo(a));
        int topCount = Math.Max(1, (int)(blockRms.Count * TopPercentile));
        double topAvgRms = blockRms.Take(topCount).Average();
        double overallRms = blockRms.Average();

        double dr = 20.0 * Math.Log10(topAvgRms / Math.Max(overallRms, 1e-10));

        return (dr, truePeakDb, clippingPercent);
    }
}
