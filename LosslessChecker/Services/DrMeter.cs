using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class DrMeter
{
    // TT DR Meter: 3-second blocks per the official algorithm
    private const double BlockDurationSec = 3.0;
    private const double TopPercentile = 0.2;
    private const int ClipRunMin = 3;

    public (double dr, double samplePeakDb, double clippingPercent) Analyze(float[] samples, int sampleRate)
    {
        int blockSize = (int)(sampleRate * BlockDurationSec);
        if (blockSize < 1 || samples.Length < blockSize)
            return (0, 0, 0);

        var blockPeaks = new List<double>(); // linear peak values
        var blockRms = new List<double>();   // linear RMS values
        double globalPeakLinear = double.MinValue;
        int clippedRuns = 0;

        for (int pos = 0; pos + blockSize <= samples.Length; pos += blockSize)
        {
            double sumSq = 0;
            double maxAbs = 0;
            int consecutive = 0;

            for (int i = pos; i < pos + blockSize; i++)
            {
                float s = samples[i];
                double abs = Math.Abs(s);
                if (abs > maxAbs) maxAbs = abs;
                if (abs > globalPeakLinear) globalPeakLinear = abs;

                if (abs >= 1.0)
                {
                    consecutive++;
                    if (consecutive >= ClipRunMin) clippedRuns++;
                }
                else consecutive = 0;

                sumSq += (double)s * s;
            }

            double rms = Math.Sqrt(sumSq / blockSize);
            blockRms.Add(rms);
            blockPeaks.Add(maxAbs);
        }

        if (blockRms.Count < 3)
            return (0, 0, 0);

        double samplePeakDb = globalPeakLinear > double.MinValue
            ? 20.0 * Math.Log10(Math.Max(globalPeakLinear, 1e-10))
            : 0;

        double clipPercent = (double)clippedRuns / (samples.Length / (double)ClipRunMin) * 100.0;

        // Sort by LINEAR RMS descending
        var indexed = blockRms
            .Select((rms, i) => (rms, peak: blockPeaks[i]))
            .OrderByDescending(x => x.rms)
            .ToList();

        int topCount = Math.Max(1, (int)(indexed.Count * TopPercentile));
        var top = indexed.Take(topCount).ToList();

        // Average peaks and RMS in dB domain (TT DR Meter spec: average dB of top 20%)
        double avgPeakDb = top.Average(x => 20.0 * Math.Log10(Math.Max(x.peak, 1e-10)));
        double avgRmsDb = top.Average(x => 20.0 * Math.Log10(Math.Max(x.rms, 1e-10)));

        double dr = avgPeakDb - avgRmsDb;

        return (Math.Round(dr, 1), Math.Round(samplePeakDb, 1), Math.Round(clipPercent, 2));
    }

    public DrResult AnalyzeStereo(StereoBuffer buffer)
    {
        // Compute per-channel DR (foobar2000: DR is per-channel, official = min of L/R)
        var (drL, peakL, clipL) = AnalyzeChannel(buffer.Left, buffer.SampleRate);
        var (drR, peakR, clipR) = buffer.IsStereo
            ? AnalyzeChannel(buffer.Right, buffer.SampleRate)
            : (drL, peakL, clipL);

        // Official DR = minimum of left and right (matching foobar2000)
        double overallDr = Math.Min(drL, drR);
        double overallPeak = Math.Max(peakL, peakR);
        double overallClip = Math.Max(clipL, clipR);

        return new DrResult(
            Math.Round(overallDr, 1), Math.Round(drL, 1), Math.Round(drR, 1),
            Math.Round(overallPeak, 1), Math.Round(overallClip, 2));
    }

    private (double dr, double peakDb, double clipPercent) AnalyzeChannel(
        float[] samples, int sampleRate)
    {
        int blockSize = (int)(sampleRate * BlockDurationSec);
        if (blockSize < 1 || samples.Length < blockSize)
            return (0, 0, 0);

        var blockPeaks = new List<double>();
        var blockRms = new List<double>();
        double globalPeakLinear = double.MinValue;
        int clippedRuns = 0;

        for (int pos = 0; pos + blockSize <= samples.Length; pos += blockSize)
        {
            double sumSq = 0;
            double maxAbs = 0;
            int consecutive = 0;

            for (int i = pos; i < pos + blockSize; i++)
            {
                float s = samples[i];
                double abs = Math.Abs(s);
                if (abs > maxAbs) maxAbs = abs;
                if (abs > globalPeakLinear) globalPeakLinear = abs;

                if (abs >= 1.0)
                {
                    consecutive++;
                    if (consecutive >= ClipRunMin) clippedRuns++;
                }
                else consecutive = 0;

                sumSq += (double)s * s;
            }

            double rms = Math.Sqrt(sumSq / blockSize);
            blockRms.Add(rms);
            blockPeaks.Add(maxAbs);
        }

        if (blockRms.Count < 3)
            return (0, 0, 0);

        double peakDb = globalPeakLinear > double.MinValue
            ? 20.0 * Math.Log10(Math.Max(globalPeakLinear, 1e-10)) : 0;

        double clipPercent = (double)clippedRuns / (samples.Length / (double)ClipRunMin) * 100.0;

        // Sort by LINEAR RMS descending
        var indexed = blockRms
            .Select((rms, i) => (rms, peak: blockPeaks[i]))
            .OrderByDescending(x => x.rms)
            .ToList();

        int topCount = Math.Max(1, (int)(indexed.Count * TopPercentile));
        var top = indexed.Take(topCount).ToList();

        // Average peaks and RMS in dB domain (TT DR Meter spec: average dB of top 20%)
        double avgPeakDb = top.Average(x => 20.0 * Math.Log10(Math.Max(x.peak, 1e-10)));
        double avgRmsDb = top.Average(x => 20.0 * Math.Log10(Math.Max(x.rms, 1e-10)));

        double dr = avgPeakDb - avgRmsDb;

        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lossless_dr_debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} DR: avgPeak={avgPeakDb:F2} avgRMS={avgRmsDb:F2} dr={dr:F2} topCount={topCount}/{indexed.Count} nsamples={samples.Length}{Environment.NewLine}");
        }
        catch { }

        return (dr, peakDb, clipPercent);
    }
}

public record DrResult(double Dr, double DrLeft, double DrRight, double SamplePeakDb, double ClippingPercent);
