using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;

namespace LosslessChecker.Services;

public class FakeStereoDetector
{
    public bool IsFakeStereo(StereoBuffer buffer, double correlation)
    {
        if (!buffer.IsStereo) return false;
        if (correlation < 0.99) return false;

        var left = buffer.Left;
        var right = buffer.Right;
        long n = buffer.Length;

        double crossCorr0 = 0, crossCorr1 = 0;
        for (long i = 0; i < n - 1; i++)
        {
            crossCorr0 += (double)left[i] * right[i];
            crossCorr1 += (double)left[i] * right[i + 1];
        }

        return crossCorr1 <= crossCorr0;
    }

    public bool IsFakeStereoFromPhase(Analyzers.PhaseResult phase, int channels)
    {
        if (channels != 2) return false;
        return phase.Correlation > 0.99;
    }
}
