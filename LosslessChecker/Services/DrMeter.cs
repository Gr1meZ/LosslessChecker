using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class DrMeter
{
    private const double BlockDurationSec = 0.5;
    private const double TopPercentile = 0.2;
    private const int ClipRunMin = 3;

    public (double dr, double samplePeakDb, double clippingPercent) Analyze(float[] samples, int sampleRate)
    {
        int blockSize = (int)(sampleRate * BlockDurationSec);
        if (blockSize < 1 || samples.Length < blockSize)
            return (0, 0, 0);

        var blockDb = new List<double>();
        double samplePeakLinear = double.MinValue;
        int clippedRuns = 0;

        for (int pos = 0; pos < samples.Length; pos += blockSize)
        {
            int len = Math.Min(blockSize, samples.Length - pos);
            double sumSq = 0;
            int consecutive = 0;
            for (int i = pos; i < pos + len; i++)
            {
                var abs = Math.Abs(samples[i]);
                if (abs > samplePeakLinear)
                    samplePeakLinear = abs;

                // Count only runs of 3+ consecutive samples at exactly 0 dBFS
                // (≈1.0 in float PCM). Isolated samples at 0 dBFS are normal after
                // peak normalization, not audible clipping.
                if (abs >= 1.0)
                {
                    consecutive++;
                    if (consecutive >= ClipRunMin)
                        clippedRuns++;
                }
                else
                {
                    consecutive = 0;
                }
                sumSq += samples[i] * samples[i];
            }
            double rms = Math.Sqrt(sumSq / len);
            double db = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
            blockDb.Add(db);
        }

        double samplePeakDb = samplePeakLinear > double.MinValue
            ? 20.0 * Math.Log10(Math.Max(samplePeakLinear, 1e-10))
            : 0.0;

        // ClipRunMin hits per file → approximate % of affected samples
        double clippingPercent = (double)clippedRuns / (samples.Length / (double)ClipRunMin) * 100.0;

        // TT DR Meter
        blockDb.Sort((a, b) => b.CompareTo(a));
        int topCount = Math.Max(1, (int)(blockDb.Count * TopPercentile));
        double dbLoud = blockDb.Take(topCount).Average();

        double totalSumSq = 0;
        for (int i = 0; i < samples.Length; i++)
            totalSumSq += samples[i] * samples[i];
        double overallRms = Math.Sqrt(totalSumSq / samples.Length);
        double dbOverall = 20.0 * Math.Log10(Math.Max(overallRms, 1e-10));

        double dr = dbLoud - dbOverall;

        return (Math.Round(dr, 1), Math.Round(samplePeakDb, 1), Math.Round(clippingPercent, 2));
    }

    public DrResult AnalyzeStereo(StereoBuffer buffer)
    {
        var (drL, peakL, clipL) = AnalyzeChannel(buffer.Left, buffer.SampleRate);
        var (drR, peakR, clipR) = buffer.IsStereo
            ? AnalyzeChannel(buffer.Right, buffer.SampleRate)
            : (0, 0, 0);

        int n = buffer.Length;
        var mono = new float[n];
        for (int i = 0; i < n; i++)
            mono[i] = buffer.IsStereo
                ? (buffer.Left[i] + buffer.Right[i]) * 0.5f
                : buffer.Left[i];

        var (dr, peak, clip) = AnalyzeChannel(mono, buffer.SampleRate);
        double overallPeak = Math.Max(peakL, peakR);
        double overallClip = Math.Max(clipL, clipR);

        return new DrResult(
            Math.Round(dr, 1), Math.Round(drL, 1), Math.Round(drR, 1),
            Math.Round(overallPeak, 1), Math.Round(overallClip, 2));
    }

    private (double dr, double peakDb, double clipPercent) AnalyzeChannel(
        float[] samples, int sampleRate)
    {
        int blockSize = (int)(sampleRate * BlockDurationSec);
        if (blockSize < 1 || samples.Length < blockSize)
            return (0, 0, 0);

        var blockDb = new List<double>();
        double samplePeakLinear = double.MinValue;
        int clippedRuns = 0;

        for (int pos = 0; pos < samples.Length; pos += blockSize)
        {
            int len = Math.Min(blockSize, samples.Length - pos);
            double sumSq = 0;
            int consecutive = 0;
            for (int i = pos; i < pos + len; i++)
            {
                var abs = Math.Abs(samples[i]);
                if (abs > samplePeakLinear) samplePeakLinear = abs;
                if (abs >= 1.0)
                {
                    consecutive++;
                    if (consecutive >= ClipRunMin) clippedRuns++;
                }
                else consecutive = 0;
                sumSq += samples[i] * samples[i];
            }
            double rms = Math.Sqrt(sumSq / len);
            blockDb.Add(20.0 * Math.Log10(Math.Max(rms, 1e-10)));
        }

        double peakDb = samplePeakLinear > double.MinValue
            ? 20.0 * Math.Log10(Math.Max(samplePeakLinear, 1e-10)) : 0;

        double clipPercent = (double)clippedRuns / (samples.Length / (double)ClipRunMin) * 100.0;

        blockDb.Sort((a, b) => b.CompareTo(a));
        int topCount = Math.Max(1, (int)(blockDb.Count * TopPercentile));
        double dbLoud = blockDb.Take(topCount).Average();

        double totalSumSq = 0;
        for (int i = 0; i < samples.Length; i++)
            totalSumSq += samples[i] * samples[i];
        double overallRms = Math.Sqrt(totalSumSq / samples.Length);
        double dbOverall = 20.0 * Math.Log10(Math.Max(overallRms, 1e-10));

        double dr = dbLoud - dbOverall;
        return (dr, peakDb, clipPercent);
    }
}

public record DrResult(double Dr, double DrLeft, double DrRight, double SamplePeakDb, double ClippingPercent);
