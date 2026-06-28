using LosslessChecker.Services;
using NWaves.Transforms;
using NWaves.Windows;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class ResamplingDetectorTests
{
    private readonly ResamplingDetector _detector = new();

    private static double[] ComputeMagnitudeSpectrum(float[] signal)
    {
        const int fftSize = 4096;
        if (signal.Length < fftSize)
            return [];

        var fft = new Fft(fftSize);
        var window = Window.Hann(fftSize);
        var frame = new float[fftSize];
        var real = new float[fftSize];
        var imag = new float[fftSize];

        Array.Copy(signal, 0, frame, 0, fftSize);
        for (int i = 0; i < fftSize; i++)
            frame[i] *= window[i];
        Array.Copy(frame, real, fftSize);
        Array.Clear(imag, 0, fftSize);
        fft.Direct(real, imag);

        var mags = new double[fftSize / 2];
        for (int i = 0; i < fftSize / 2; i++)
            mags[i] = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

        return mags;
    }

    [Fact]
    public void CleanWhiteNoise_NoAliasingNoRinging()
    {
        var rng = new Random(42);
        var samples = new float[8192];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(rng.NextDouble() * 2 - 1);

        var spectrum = ComputeMagnitudeSpectrum(samples);
        var result = _detector.DetectFromSpectrum(spectrum, 44100);

        Assert.False(result.HasAliasing);
        Assert.False(result.HasRinging);
        Assert.Contains("Clean", result.Verdict);
    }

    [Fact]
    public void SpectrumWithIsolatedSpikes_AliasingDetected()
    {
        int bins = 1200;
        var spectrum = new double[bins];
        // baseline: -40 dB
        for (int i = 0; i < bins; i++)
            spectrum[i] = 0.01;

        // Insert isolated spikes (1.0 = 0 dB, neighbors 0.01 = -40 dB)
        // Pattern: ... 0.01, 1.0, 0.01, 0.01, 0.01, 1.0, 0.01, ...
        // Spike index i has prev low and next low → triggers db > prevDb+12 && nextDb < db-6
        // need aliasHits > bins/80 = 15
        for (int s = bins / 3 + 10; s + 2 < bins - 1; s += 5)
            spectrum[s] = 1.0;

        var result = _detector.DetectFromSpectrum(spectrum, 44100);

        Assert.True(result.HasAliasing);
        Assert.Contains("Aliasing", result.Verdict);
    }

    [Fact]
    public void AlternatingUpDownPatternInHighFreq_RingingDetected()
    {
        int bins = 1200;
        var spectrum = new double[bins];
        // baseline for low freq region: smooth decay
        for (int i = 0; i < bins * 2 / 3; i++)
            spectrum[i] = 0.5 / (1 + i * 0.001);

        // High freq region (>= bins*2/3): alternating high/low to trigger ringing
        // Ringing condition: db0 > -60, db1 < db0-3, db2 > db1+2, db3 < db2-2
        // 0.1 = -20 dB (> -60), 0.01 = -40 dB
        // db1 < db0-3: -40 < -20-3=-23 ✓
        // db2 > db1+2: -20 > -40+2=-38 ✓
        // db3 < db2-2: -40 < -20-2=-22 ✓
        for (int i = bins * 2 / 3; i < bins; i++)
            spectrum[i] = (i % 2 == 0) ? 0.1 : 0.01;

        var result = _detector.DetectFromSpectrum(spectrum, 44100);

        Assert.True(result.HasRinging);
        Assert.Contains("Ringing", result.Verdict);
    }

    [Fact]
    public void ShortSpectrum_NoFalsePositives()
    {
        var shortSpectrum = new double[50];
        var result = _detector.DetectFromSpectrum(shortSpectrum, 44100);

        Assert.False(result.HasAliasing);
        Assert.False(result.HasRinging);
    }

    [Fact]
    public void FlatDecayingSpectrum_NoArtifacts()
    {
        int bins = 1024;
        var spectrum = new double[bins];
        // Smooth decay, no isolated spikes, no alternating pattern
        for (int i = 0; i < bins; i++)
            spectrum[i] = 1.0 / (1 + i * 0.005);

        var result = _detector.DetectFromSpectrum(spectrum, 44100);

        Assert.False(result.HasAliasing);
        Assert.False(result.HasRinging);
        Assert.Contains("Clean", result.Verdict);
    }
}
