using LosslessChecker.Services;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class PreEchoDetectorTests
{
    [Fact]
    public void PreEchoDetectedOnSyntheticTransient()
    {
        int sampleRate = 44100;
        int windowSamples = sampleRate * 2 / 1000;
        int n = sampleRate * 2;
        var samples = new float[n];
        var rng = new Random(42);
        for (int i = 0; i < n; i++)
            samples[i] = (float)(rng.NextDouble() * 0.01);

        double preAmp = 0.15;
        double transAmp = 0.80;
        int spacing = windowSamples * 60;
        int[] starts = [windowSamples * 10, windowSamples * 10 + spacing, windowSamples * 10 + spacing * 2,
                         windowSamples * 10 + spacing * 3, windowSamples * 10 + spacing * 4];

        foreach (int start in starts)
        {
            int preEnd = start + windowSamples;
            int transEnd = preEnd + windowSamples;
            if (transEnd >= n) break;
            for (int i = start; i < preEnd; i++)
                samples[i] = (float)(preAmp * Math.Sin(2 * Math.PI * 500 * (i - start) / sampleRate));
            for (int i = preEnd; i < transEnd; i++)
                samples[i] = (float)(rng.NextDouble() * 2 * transAmp - transAmp);
        }

        var detector = new PreEchoDetector();
        detector.Init(sampleRate);
        detector.AddChunk(samples);
        var (hasPreEcho, _) = detector.GetResult();
        Assert.True(hasPreEcho);
    }
}
