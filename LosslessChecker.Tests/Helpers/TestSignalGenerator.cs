using LosslessChecker.Models;

namespace LosslessChecker.Tests.Helpers;

public static class TestSignalGenerator
{
    private const double TwoPi = 2.0 * Math.PI;

    public static float[] GenerateSweep(double startFreq, double endFreq, double duration, int sampleRate)
    {
        int n = (int)(sampleRate * duration);
        var samples = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sampleRate;
            double freq = startFreq + (endFreq - startFreq) * (t / duration);
            phase += TwoPi * freq / sampleRate;
            samples[i] = MathF.Sin((float)phase);
        }
        return samples;
    }

    public static float[] GenerateSine(double freq, double duration, int sampleRate, double gain = 1.0)
    {
        int n = (int)(sampleRate * duration);
        var samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = (float)(gain * Math.Sin(TwoPi * freq * i / sampleRate));
        return samples;
    }

    public static float[] GenerateClippedSine(double freq, double duration, int sampleRate, double gain = 1.2)
    {
        int n = (int)(sampleRate * duration);
        var samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = (float)Math.Max(-1.0, Math.Min(1.0, gain * Math.Sin(TwoPi * freq * i / sampleRate)));
        return samples;
    }

    public static float[] AddDcOffset(float[] samples, double offsetPercent)
    {
        float offset = (float)(offsetPercent / 100.0);
        var result = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            result[i] = samples[i] + offset;
        return result;
    }

    public static StereoBuffer GenerateStereo(double freq, double duration, int sampleRate,
        bool invertRight = false, double leftGain = 1.0, double rightGain = 1.0)
    {
        var left = GenerateSine(freq, duration, sampleRate, leftGain);
        var right = GenerateSine(freq, duration, sampleRate, rightGain);
        if (invertRight)
            for (int i = 0; i < right.Length; i++) right[i] = -right[i];
        return new StereoBuffer(left, right, sampleRate);
    }

    public static float[] GenerateZeroPadded24Bit(double freq, double duration, int sampleRate)
    {
        int n = (int)(sampleRate * duration);
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            // Generate exact 16-bit audio stored as 24-bit float:
            // quantize to 16-bit integer, then represent as if zero-padded to 24-bit
            double raw = Math.Sin(TwoPi * freq * i / sampleRate);
            int val24 = (int)Math.Round(raw * 8388607.0);
            // Zero out lower 8 bits (simulating 16-bit in 24-bit container)
            val24 &= ~0xFF;
            samples[i] = (float)(val24 / 8388607.0);
        }
        return samples;
    }
}
