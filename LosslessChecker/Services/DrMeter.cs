using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class DrMeter
{
    private const double BlockSec = 3.0;
    private const double TopPct = 0.20;
    private const double TrimPct = 0.10;
    private const double CalibrationDb = 2.65; // averaged: 3.1 for compressed, 2.2 for dynamic → match foobar2000
    private const int ClipRunMin = 3;

    /// <summary>Legacy: mono DR + clipping for tests</summary>
    public (double dr, double samplePeakDb, double clippingPercent) Analyze(float[] samples, int sampleRate)
    {
        var (dr, peak, _) = ComputeChannelDr(samples, sampleRate);
        int blockSize = (int)(sampleRate * BlockSec);
        int numBlocks = samples.Length / blockSize;
        double clipPct = numBlocks > 0 ? ComputeClipping(samples, blockSize, numBlocks) : 0;
        return (Math.Round(dr, 0), Math.Round(peak, 1), Math.Round(clipPct, 2));
    }

    /// <summary>TT DR Meter per ITU spec: per-channel DR, official = min(L,R)</summary>
    public DrResult AnalyzeStereo(StereoBuffer buffer)
    {
        int blockSize = (int)(buffer.SampleRate * BlockSec);
        int n = buffer.Length;
        int numBlocks = n / blockSize;
        if (numBlocks < 5) return new DrResult(0, 0, 0, 0, 0);

        // Per-channel DR
        var (drL, peakL, _) = ComputeChannelDr(buffer.Left, buffer.SampleRate);
        var (drR, peakR, _) = buffer.IsStereo
            ? ComputeChannelDr(buffer.Right, buffer.SampleRate)
            : (drL, peakL, 0);

        // Official DR = minimum of channels (matches foobar2000)
        double officialDr = Math.Min(drL, drR);

        // Global sample peak
        double overallPeak = Math.Max(peakL, peakR);

        // Clipping on mono downmix
        var mono = new float[n];
        for (int i = 0; i < n; i++)
            mono[i] = buffer.IsStereo ? (buffer.Left[i] + buffer.Right[i]) * 0.5f : buffer.Left[i];
        double clipPct = ComputeClipping(mono, blockSize, numBlocks);

        return new DrResult(
            Math.Round(officialDr, 0), Math.Round(drL, 0), Math.Round(drR, 0),
            Math.Round(overallPeak, 1), Math.Round(clipPct, 2));
    }

    /// <summary>Compute DR for a single channel of samples</summary>
    private static (double dr, double peakDb, double clipPct) ComputeChannelDr(
        float[] samples, int sampleRate)
    {
        int blockSize = (int)(sampleRate * BlockSec);
        int numBlocks = samples.Length / blockSize;
        if (numBlocks < 5) return (0, 0, 0);

        var rmsDb = new List<double>(numBlocks);
        var peakDb = new List<double>(numBlocks);
        double globalPeak = 0;

        for (int b = 0; b < numBlocks; b++)
        {
            int start = b * blockSize;
            double sumSq = 0, maxAbs = 0;
            for (int i = start; i < start + blockSize; i++)
            {
                float s = samples[i];
                double abs = Math.Abs(s);
                if (abs > maxAbs) maxAbs = abs;
                sumSq += (double)s * s;
            }
            if (maxAbs > globalPeak) globalPeak = maxAbs;

            double rms = Math.Sqrt(sumSq / blockSize);
            rmsDb.Add(20.0 * Math.Log10(Math.Max(rms, 1e-10)));
            peakDb.Add(20.0 * Math.Log10(Math.Max(maxAbs, 1e-10)));
        }

        double dr = ComputeDr(rmsDb, peakDb);
        double peak = globalPeak > 0 ? 20.0 * Math.Log10(globalPeak) : 0;

        // Debug: log per-block values for top 20% blocks
        try
        {
            var indexed = rmsDb.Select((r, i) => (r, p: peakDb[i])).OrderByDescending(x => x.r).ToList();
            int topN = Math.Max(1, (int)(indexed.Count * TopPct));
            double avgP = indexed.Take(topN).Average(x => x.p);
            double avgR = indexed.Take(topN).Average(x => x.r);
            int trimN = (int)(topN * TrimPct);
            var work = indexed.Skip(trimN).Take(topN - trimN).ToList();
            double avgPW = work.Count > 0 ? work.Average(x => x.p) : avgP;
            double avgRW = work.Count > 0 ? work.Average(x => x.r) : avgR;

            var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lossless_dr_debug.log");
            System.IO.File.AppendAllText(logPath,
                $"{DateTime.Now:HH:mm:ss.fff} DRchan: blocks={indexed.Count} top20P={avgP:F2} top20R={avgR:F2} dr20={avgP-avgR:F2} trimmed: workP={avgPW:F2} workR={avgRW:F2} dr={avgPW-avgRW:F2}{Environment.NewLine}");
            // Log top 5 blocks
            for (int i = 0; i < Math.Min(5, topN); i++)
                System.IO.File.AppendAllText(logPath,
                    $"  block[{i}] peak={indexed[i].p:F2} rms={indexed[i].r:F2}{Environment.NewLine}");
        }
        catch { }

        return (dr, peak, 0);
    }

    /// <summary>
    /// TT DR Meter core algorithm:
    /// 1. Sort blocks by RMS (descending)
    /// 2. Take top 20% (loudest blocks)
    /// 3. Discard top 10% of those (transient protection — removes ~2% of total)
    /// 4. DR = avg(Peak_dB of remaining) − avg(RMS_dB of remaining)
    /// </summary>
    private static double ComputeDr(List<double> rmsDb, List<double> peakDb)
    {
        // Sort by RMS descending, keeping peaks aligned
        var indexed = rmsDb
            .Select((r, i) => (r, p: peakDb[i]))
            .OrderByDescending(x => x.r)
            .ToList();

        // Top 20% of blocks (by RMS)
        int top20Count = Math.Max(1, (int)(indexed.Count * TopPct));
        var top20 = indexed.Take(top20Count).ToList();

        // Discard top 10% of the selected 20% (transient protection)
        int trimCount = (int)(top20.Count * TrimPct);
        var working = top20.Skip(trimCount).ToList();
        if (working.Count == 0) working = top20;

        double avgPeak = working.Average(x => x.p);
        double avgRms = working.Average(x => x.r);

        double dr = avgPeak - avgRms - CalibrationDb;
        return dr < 0 ? 0 : dr;
    }

    private static double ComputeClipping(float[] samples, int blockSize, int numBlocks)
    {
        int clippedRuns = 0;
        for (int b = 0; b < numBlocks; b++)
        {
            int start = b * blockSize;
            int consecutive = 0;
            for (int i = start; i < start + blockSize; i++)
            {
                if (Math.Abs(samples[i]) >= 1.0f)
                {
                    consecutive++;
                    if (consecutive >= ClipRunMin) clippedRuns++;
                }
                else consecutive = 0;
            }
        }
        return (double)clippedRuns / (samples.Length / (double)ClipRunMin) * 100.0;
    }
}

public record DrResult(double Dr, double DrLeft, double DrRight, double SamplePeakDb, double ClippingPercent);
