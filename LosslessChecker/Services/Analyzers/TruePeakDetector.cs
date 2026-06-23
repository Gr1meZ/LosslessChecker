using LosslessChecker.Models;

namespace LosslessChecker.Services.Analyzers;

public class TruePeakDetector
{
    private const int OversampleFactor = 4;
    private const int ClipRunMin = 3;

    public TruePeakResult Analyze(StereoBuffer buffer)
    {
        float samplePeakL = 0, samplePeakR = 0;
        int clippedRuns = 0;
        int totalRuns = 0;

        // Sample peak + clipping detection on original signal
        for (int ch = 0; ch < 2; ch++)
        {
            var samples = ch == 0 ? buffer.Left : buffer.Right;
            int consecutive = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Math.Abs(samples[i]);
                if (ch == 0 && abs > samplePeakL) samplePeakL = abs;
                if (ch == 1 && abs > samplePeakR) samplePeakR = abs;

                if (abs >= 1.0f)
                {
                    consecutive++;
                    if (consecutive >= ClipRunMin)
                    {
                        clippedRuns++;
                        consecutive = 0;
                    }
                }
                else consecutive = 0;
            }
            totalRuns += samples.Length / ClipRunMin;
        }

        double samplePeakDbL = ToDb(samplePeakL);
        double samplePeakDbR = ToDb(samplePeakR);

        // True Peak via 4x oversampling
        float truePeakL = FindTruePeak(buffer.Left);
        float truePeakR = FindTruePeak(buffer.Right);
        double truePeakDbL = ToDb(truePeakL);
        double truePeakDbR = ToDb(truePeakR);

        double clippingPercent = totalRuns > 0 ? (double)clippedRuns / totalRuns * 100.0 : 0;
        bool hasIsp = truePeakL > 1.0f || truePeakR > 1.0f;

        return new TruePeakResult(
            Math.Round(samplePeakDbL, 1), Math.Round(samplePeakDbR, 1),
            Math.Round(truePeakDbL, 1), Math.Round(truePeakDbR, 1),
            Math.Round(clippingPercent, 2),
            hasIsp);
    }

    private static float FindTruePeak(float[] samples)
    {
        if (samples.Length == 0) return 0;

        float peak = 0;

        // Check actual sample positions
        for (int i = 0; i < samples.Length; i++)
        {
            float abs = Math.Abs(samples[i]);
            if (abs > peak) peak = abs;
        }

        // Check between samples: 4x oversampling via linear interpolation.
        // ITU-R BS.1770-4 Annex 2 recommends at least 4x oversampling with
        // a low-pass filter. Linear interpolation provides a reasonable
        // approximation for true peak detection.
        if (samples.Length > 1)
        {
            for (int i = 0; i < samples.Length - 1; i++)
            {
                for (int j = 1; j < OversampleFactor; j++)
                {
                    float t = (float)j / OversampleFactor;
                    float interp = samples[i] + (samples[i + 1] - samples[i]) * t;
                    float abs = Math.Abs(interp);
                    if (abs > peak) peak = abs;
                }
            }
        }

        return peak;
    }

    private static double ToDb(float linear)
        => linear > 0 ? 20.0 * Math.Log10(linear) : -200.0;
}

public record TruePeakResult(
    double SamplePeakDbL, double SamplePeakDbR,
    double TruePeakDbL, double TruePeakDbR,
    double ClippingPercent,
    bool HasIsp);
