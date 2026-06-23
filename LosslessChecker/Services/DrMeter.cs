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

        var blockDb = new List<double>();
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
            double rms = Math.Sqrt(sumSq / len);
            double db = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
            blockDb.Add(db);
        }

        double truePeakDb = truePeakLinear > double.MinValue
            ? 20.0 * Math.Log10(Math.Max(truePeakLinear, 1e-10))
            : 0.0;
        double clippingPercent = (double)clippedSamples / samples.Length * 100.0;

        // TT DR Meter: sort dB values, average top 20%, subtract overall dB
        blockDb.Sort((a, b) => b.CompareTo(a));
        int topCount = Math.Max(1, (int)(blockDb.Count * TopPercentile));
        double dbLoud = blockDb.Take(topCount).Average();

        // Overall RMS of entire signal
        double totalSumSq = 0;
        for (int i = 0; i < samples.Length; i++)
            totalSumSq += samples[i] * samples[i];
        double overallRms = Math.Sqrt(totalSumSq / samples.Length);
        double dbOverall = 20.0 * Math.Log10(Math.Max(overallRms, 1e-10));

        double dr = dbLoud - dbOverall;

        return (Math.Round(dr, 1), Math.Round(truePeakDb, 1), Math.Round(clippingPercent, 2));
    }
}
