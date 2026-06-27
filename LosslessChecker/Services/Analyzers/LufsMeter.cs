using LosslessChecker.Models;

namespace LosslessChecker.Services.Analyzers;

public class LufsMeter
{
    private const double BlockDuration = 0.4;
    private const double HopDuration = 0.1;
    private const double AbsoluteGate = -70.0;
    private const double RelativeGate = -10.0;
    private const double ChannelWeight = 1.0;

    public LufsResult Analyze(StereoBuffer buffer)
    {
        int sampleRate = buffer.SampleRate;
        int blockSize = (int)(sampleRate * BlockDuration);
        int hopSize = (int)(sampleRate * HopDuration);

        if (blockSize < 1 || buffer.Length < blockSize)
            return new LufsResult(-100, 0);

        var kwL = new KWeightingFilter(sampleRate);
        var kwR = new KWeightingFilter(sampleRate);

        var blockLoudness = new List<double>();
        var shortTermLoudness = new List<double>();

        int stBlockSize = (int)(sampleRate * 3.0);
        int stHopSize = (int)(sampleRate * 1.0);

        int n = buffer.Length;
        var right = buffer.IsStereo ? buffer.Right : buffer.Left;

        for (int pos = 0; pos + blockSize <= n; pos += hopSize)
        {
            double sumSqL = 0, sumSqR = 0;
            int end = pos + blockSize;

            for (int i = pos; i < end; i++)
            {
                double filteredL = kwL.Process(buffer.Left[i]);
                double filteredR = kwR.Process(right[i]);
                sumSqL += filteredL * filteredL;
                sumSqR += filteredR * filteredR;
            }

            double meanSq = (ChannelWeight * sumSqL + ChannelWeight * sumSqR) / blockSize;
            double loudness = -0.691 + 10.0 * Math.Log10(Math.Max(meanSq, 1e-12));
            blockLoudness.Add(loudness);
        }

        if (blockLoudness.Count == 0)
            return new LufsResult(-100, 0);

        for (int pos = 0; pos + stBlockSize <= n; pos += stHopSize)
        {
            double sumSqL = 0, sumSqR = 0;
            int end = pos + stBlockSize;

            for (int i = pos; i < end; i++)
            {
                double filteredL = kwL.Process(buffer.Left[i]);
                double filteredR = kwR.Process(right[i]);
                sumSqL += filteredL * filteredL;
                sumSqR += filteredR * filteredR;
            }

            double meanSq = (ChannelWeight * sumSqL + ChannelWeight * sumSqR) / stBlockSize;
            double stLoudness = -0.691 + 10.0 * Math.Log10(Math.Max(meanSq, 1e-12));
            shortTermLoudness.Add(stLoudness);
        }

        double integratedLufs = ComputeIntegratedLoudness(blockLoudness);
        double lra = ComputeLoudnessRange(shortTermLoudness);

        return new LufsResult(Math.Round(integratedLufs, 1), Math.Round(lra, 1));
    }

    public LufsResult AnalyzeSpan(ReadOnlySpan<float> mono, int sampleRate)
    {
        var buffer = new StereoBuffer(mono.ToArray(), Array.Empty<float>(), sampleRate);
        return Analyze(buffer);
    }

    private static double ComputeIntegratedLoudness(List<double> blockLoudness)
    {
        var absoluteGated = blockLoudness.Where(b => b > AbsoluteGate).ToList();
        if (absoluteGated.Count == 0)
            return -70.0;

        double absoluteMeanLin = absoluteGated.Average(b => Math.Pow(10, b / 10));
        double absoluteLoudness = -0.691 + 10.0 * Math.Log10(absoluteMeanLin);

        double relativeThreshold = absoluteLoudness + RelativeGate;

        var relativeGated = absoluteGated.Where(b => b > relativeThreshold).ToList();
        if (relativeGated.Count == 0)
            return absoluteLoudness;

        double gatedMeanLin = relativeGated.Average(b => Math.Pow(10, b / 10));
        return -0.691 + 10.0 * Math.Log10(gatedMeanLin);
    }

    private static double ComputeLoudnessRange(List<double> shortTermLoudness)
    {
        var gated = shortTermLoudness.Where(b => b > AbsoluteGate).ToList();
        if (gated.Count < 10)
            return 0;

        double meanLin = gated.Average(b => Math.Pow(10, b / 10));
        double meanLoudness = -0.691 + 10.0 * Math.Log10(meanLin);
        double relThreshold = meanLoudness + RelativeGate;

        var relGated = gated.Where(b => b > relThreshold).OrderBy(b => b).ToList();
        if (relGated.Count < 10)
            return 0;

        int lowIdx = (int)Math.Ceiling(relGated.Count * 0.10) - 1;
        int highIdx = (int)Math.Ceiling(relGated.Count * 0.95) - 1;
        lowIdx = Math.Max(0, lowIdx);
        highIdx = Math.Min(relGated.Count - 1, highIdx);

        return relGated[highIdx] - relGated[lowIdx];
    }
}

public record LufsResult(double IntegratedLufs, double LoudnessRange);
