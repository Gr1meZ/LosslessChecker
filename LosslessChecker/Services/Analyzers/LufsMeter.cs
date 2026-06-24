using LosslessChecker.Models;

namespace LosslessChecker.Services.Analyzers;

public class LufsMeter
{
    private const double BlockDuration = 0.4;
    private const double AbsoluteGate = -70.0;
    private const double RelativeGate = -10.0;

    public LufsResult Analyze(StereoBuffer buffer)
        => AnalyzeMono(ToMono(buffer), buffer.SampleRate);

    public LufsResult AnalyzeSpan(ReadOnlySpan<float> mono, int sampleRate)
        => AnalyzeMono(mono, sampleRate);

    private static LufsResult AnalyzeMono(ReadOnlySpan<float> mono, int sampleRate)
    {
        int blockSize = (int)(sampleRate * BlockDuration);
        if (blockSize < 1 || mono.Length < blockSize)
            return new LufsResult(-100, 0);

        double omegaHp = 2.0 * Math.PI * 38.0 / sampleRate;
        double hpCoeff = Math.Exp(-omegaHp);
        double omegaSh = 2.0 * Math.PI * 1500.0 / sampleRate;
        double shCoeff = 0.5 * (1.0 - Math.Exp(-omegaSh));

        var blockLoudness = new List<double>();
        double hpX1 = 0, hpY1 = 0, shX1 = 0;

        for (int pos = 0; pos + blockSize <= mono.Length; pos += blockSize)
        {
            double sumSq = 0;
            int end = pos + blockSize;
            // Inlined K-weight filter — no per-sample method call overhead
            for (int i = pos; i < end; i++)
            {
                double sample = mono[i];
                double hpOut = hpCoeff * hpY1 + hpCoeff * (sample - hpX1);
                hpX1 = sample;
                hpY1 = hpOut;
                double shOut = hpOut + shCoeff * (hpOut - shX1);
                shX1 = hpOut;
                sumSq += shOut * shOut;
            }
            double rms = Math.Sqrt(sumSq / blockSize);
            double loudness = -0.691 + 10.0 * Math.Log10(Math.Max(rms * rms, 1e-10));
            blockLoudness.Add(loudness);
        }

        if (blockLoudness.Count == 0)
            return new LufsResult(-100, 0);

        // Absolute gate
        double absoluteSum = 0;
        int absoluteCount = 0;
        foreach (var b in blockLoudness)
        {
            if (b > AbsoluteGate)
            {
                absoluteSum += Math.Pow(10, b / 10);
                absoluteCount++;
            }
        }

        double absoluteLoudness = absoluteCount > 0
            ? -0.691 + 10.0 * Math.Log10(absoluteSum / absoluteCount)
            : -70.0;

        double relativeThreshold = absoluteLoudness + RelativeGate;
        double gatedSum = 0;
        int gatedCount = 0;
        foreach (var b in blockLoudness)
        {
            if (b > relativeThreshold)
            {
                gatedSum += Math.Pow(10, b / 10);
                gatedCount++;
            }
        }

        double integratedLufs = gatedCount > 0
            ? -0.691 + 10.0 * Math.Log10(gatedSum / gatedCount)
            : -70.0;

        // LRA
        var loudBlocks = blockLoudness.Where(b => b > AbsoluteGate).OrderBy(b => b).ToList();
        double lra = 0;
        if (loudBlocks.Count >= 10)
        {
            int lowIdx = (int)(loudBlocks.Count * 0.10);
            int highIdx = (int)(loudBlocks.Count * 0.95);
            lra = loudBlocks[highIdx] - loudBlocks[lowIdx];
        }

        return new LufsResult(Math.Round(integratedLufs, 1), Math.Round(lra, 1));
    }

    private static float[] ToMono(StereoBuffer buffer)
    {
        int n = buffer.Length;
        var mono = new float[n];
        if (buffer.IsStereo)
        {
            for (int i = 0; i < n; i++)
                mono[i] = (buffer.Left[i] + buffer.Right[i]) * 0.5f;
        }
        else
        {
            Array.Copy(buffer.Left, mono, n);
        }
        return mono;
    }
}

public record LufsResult(double IntegratedLufs, double LoudnessRange);
