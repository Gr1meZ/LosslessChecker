using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using NWaves.Transforms;
using NWaves.Windows;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class ArtifactDetectorTests
{
    private readonly ArtifactDetector _detector = new();

    private static double[] ComputeAvgSpectrum(float[] signal)
    {
        const int fftSize = 4096;
        const int hopSize = 2048;
        if (signal.Length < fftSize)
            return [];

        var fft = new Fft(fftSize);
        var window = Window.Hann(fftSize);
        var frame = new float[fftSize];
        var real = new float[fftSize];
        var imag = new float[fftSize];
        var sumMag = new double[fftSize / 2];
        int frameCount = 0;

        for (int pos = 0; pos + fftSize <= signal.Length; pos += hopSize)
        {
            Array.Copy(signal, pos, frame, 0, fftSize);
            for (int i = 0; i < fftSize; i++)
                frame[i] *= window[i];
            Array.Copy(frame, real, fftSize);
            Array.Clear(imag, 0, fftSize);
            fft.Direct(real, imag);

            for (int i = 0; i < fftSize / 2; i++)
                sumMag[i] += Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

            frameCount++;
            if (frameCount >= 16) break;
        }

        for (int i = 0; i < sumMag.Length; i++)
            sumMag[i] /= frameCount;

        return sumMag;
    }

    [Fact]
    public void LongSignalWith16kHzBrickwall_ArtifactsDetected()
    {
        int sampleRate = 44100;
        // Sum 150 sine tones 1k–15.7k = dense spectrum below cutoff, silence above
        int n = sampleRate; // 1 second
        var samples = new float[n];
        var rng = new Random(42);
        double phase = 0;
        for (int tone = 0; tone < 150; tone++)
        {
            double freq = 1000 + rng.NextDouble() * 14700;
            for (int i = 0; i < n; i++)
            {
                phase += 2 * Math.PI * freq / sampleRate;
                samples[i] += (float)(0.006 * Math.Sin(phase));
            }
        }

        var (hasArtifacts, level, type) = _detector.Detect(samples, sampleRate, 16000);

        Assert.True(hasArtifacts);
        Assert.NotEqual("None", level);
    }

    [Fact]
    public void SpectrumWithHoles_SpectralHolesDetected()
    {
        int bins = 1024;
        var spectrum = new double[bins];
        // Fill with moderate baseline (~0.1 = -20 dB)
        for (int i = 0; i < bins; i++)
            spectrum[i] = 0.1;

        // Create spectral holes: bins dipping >15 dB below neighbors
        // Pattern: 0.1, 0.001, 0.1 → -20 dB, -60 dB, -20 dB
        // prevDb=-20, db=-60, nextDb=-20
        // db < prevDb - 15: -60 < -20-15=-35 ✓
        // nextDb > db + 10: -20 > -60+10=-50 ✓
        // need holeCount > bins/50 = 20
        for (int h = bins / 6 + 5; h + 2 < bins - 1; h += 10)
        {
            spectrum[h] = 0.001;
        }

        bool hasHoles = _detector.DetectSpectralHoles(spectrum, 22050);
        Assert.True(hasHoles);
    }

    [Fact]
    public void CleanFullSpectrumSignal_NoArtifacts()
    {
        int sampleRate = 44100;
        // White noise fills the entire spectrum uniformly
        var rng = new Random(123);
        var samples = new float[16384];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(rng.NextDouble() * 2 - 1);

        var (hasArtifacts, level, type) = _detector.Detect(samples, sampleRate, 20000);

        Assert.False(hasArtifacts);
        Assert.Equal("None", level);
        Assert.Equal("None", type);
    }

    [Fact]
    public void SilenceAtBoundaries_AbruptEdgesDetected()
    {
        int sampleRate = 44100;
        int edgeLen = sampleRate / 2; // 0.5 sec
        int totalLen = edgeLen * 4;
        var samples = new float[totalLen];

        // Loud sine in the middle, silence at edges
        int midStart = edgeLen;
        int midEnd = totalLen - edgeLen;
        for (int i = midStart; i < midEnd; i++)
            samples[i] = (float)(0.8 * Math.Sin(2 * Math.PI * 1000 * i / sampleRate));

        bool hasEdges = _detector.DetectAbruptEdges(samples, sampleRate);
        Assert.True(hasEdges);
    }

    [Fact]
    public void SbrLikeSpectrum_SbrDetected()
    {
        int sampleRate = 44100;
        int bins = 1024;
        var spectrum = new double[bins];

        // Lower band (below ~16 kHz): smoothly varying, flatness > 0.3
        // Use only positive values (avoid clamping to 1e-10 which kills flatness)
        int bin16k = (int)(16000.0 / (sampleRate / 2.0) * bins);
        for (int i = 0; i < bin16k; i++)
            spectrum[i] = 0.35 + 0.1 * Math.Sin(i * 0.09) * Math.Sin(i * 0.09);

        // Upper band (above ~16 kHz): sparse tonal patches, flatness < 0.15
        // Fill with near-zero values, then insert a few isolated tonal peaks
        for (int i = bin16k; i < bins; i++)
            spectrum[i] = 1e-8;

        int upperLen = bins - bin16k;
        for (int p = 0; p < upperLen; p += upperLen / 8)
        {
            int idx = bin16k + p;
            if (idx < bins)
                spectrum[idx] = 0.04;
        }

        var (hasSbr, verdict) = _detector.DetectSbr(spectrum, sampleRate);
        Assert.True(hasSbr);
        Assert.Contains("SBR", verdict);
    }
}
