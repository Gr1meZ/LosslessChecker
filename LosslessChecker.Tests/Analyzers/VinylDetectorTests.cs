using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using NWaves.Transforms;
using NWaves.Windows;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class VinylDetectorTests
{
    private readonly VinylDetector _detector = new();
    private const int FftSize = 4096;

    private static double[] ComputeMagnitudeSpectrum(float[] samples)
    {
        if (samples.Length < FftSize)
            return Array.Empty<double>();

        var fft = new Fft(FftSize);
        var window = Window.Hann(FftSize);
        var frame = new float[FftSize];
        var real = new float[FftSize];
        var imag = new float[FftSize];
        var avgSpectrum = new double[FftSize / 2];
        int hopSize = FftSize / 4;
        int frames = 0;

        for (int pos = 0; pos + FftSize <= samples.Length; pos += hopSize)
        {
            Array.Copy(samples, pos, frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) frame[i] *= window[i];
            Array.Copy(frame, real, FftSize);
            Array.Clear(imag, 0, FftSize);
            fft.Direct(real, imag);

            for (int i = 0; i < FftSize / 2; i++)
                avgSpectrum[i] += Math.Sqrt((double)real[i] * real[i] + (double)imag[i] * imag[i]);

            frames++;
        }

        if (frames > 0)
            for (int i = 0; i < avgSpectrum.Length; i++)
                avgSpectrum[i] /= frames;

        return avgSpectrum;
    }

    private static float[] GenerateMultiTone(int sampleRate, double duration, params (double freq, double gain)[] tones)
    {
        int n = (int)(sampleRate * duration);
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            foreach (var (freq, gain) in tones)
                sum += gain * Math.Sin(2 * Math.PI * freq * i / sampleRate);
            samples[i] = (float)Math.Clamp(sum, -1, 1);
        }
        return samples;
    }

    [Fact]
    public void StrongSub40HzRumble_DetectedAsVinyl()
    {
        var samples = GenerateMultiTone(44100, 3.0, (12, 0.8), (55, 0.3), (1000, 0.05));
        var spectrum = ComputeMagnitudeSpectrum(samples);
        var result = _detector.Detect(spectrum, 44100, samples);
        Assert.True(result.IsVinylRip, $"Expected vinyl rip, RumbleRatio={result.RumbleRatio}");
    }

    [Fact]
    public void CleanSine_NotVinyl()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 5.0, 44100, 0.5);
        var spectrum = ComputeMagnitudeSpectrum(samples);
        var result = _detector.Detect(spectrum, 44100, samples);
        Assert.False(result.IsVinylRip);
    }

    [Fact]
    public void CleanSweep_NotVinyl()
    {
        var samples = TestSignalGenerator.GenerateSweep(40, 20000, 5.0, 44100);
        var spectrum = ComputeMagnitudeSpectrum(samples);
        var result = _detector.Detect(spectrum, 44100, samples);
        Assert.False(result.IsVinylRip);
    }

    [Fact]
    public void HighSampleRate_VinylHfSignature_Detected()
    {
        var spectrum = new double[4096];
        spectrum[0] = 10.0;
        spectrum[1] = 10.0;
        for (int i = 42; i <= 170; i++) spectrum[i] = 1.0;
        for (int i = 1881; i < 4096; i++) spectrum[i] = 0.5;
        var result = _detector.Detect(spectrum, 96000, Array.Empty<float>());
        Assert.True(result.IsVinylRip, $"RumbleRatio={result.RumbleRatio}, HfNoiseRatio={result.HfNoiseRatio}");
        Assert.True(result.HfNoiseRatio > 0);
    }

    [Fact]
    public void HighSampleRate_CleanSpectrum_NotVinyl()
    {
        var spectrum = new double[4096];
        for (int i = 42; i <= 170; i++) spectrum[i] = 1.0;
        var result = _detector.Detect(spectrum, 96000, Array.Empty<float>());
        Assert.False(result.IsVinylRip);
    }

    [Fact]
    public void HfNoiseRatio_CalculatedCorrectly()
    {
        var spectrum = new double[4096];
        spectrum[0] = 5.0;
        spectrum[1] = 5.0;
        for (int i = 42; i <= 170; i++) spectrum[i] = 1.0;
        for (int i = 1881; i < 4096; i++) spectrum[i] = 0.7;
        var result = _detector.Detect(spectrum, 96000, Array.Empty<float>());
        Assert.True(result.IsVinylRip, $"RumbleRatio={result.RumbleRatio}, HfNoiseRatio={result.HfNoiseRatio}");
        Assert.True(result.HfNoiseRatio >= 0.69, $"HfNoiseRatio={result.HfNoiseRatio}");
    }

    [Fact]
    public void PowerLineHum_EnablesVinylDetection_AtLowRate()
    {
        var samples = GenerateMultiTone(44100, 3.0, (12, 0.8), (55, 0.5), (1000, 0.05));
        var spectrum = ComputeMagnitudeSpectrum(samples);
        var result = _detector.Detect(spectrum, 44100, samples);
        Assert.True(result.IsVinylRip, $"Expected vinyl with hum, RumbleRatio={result.RumbleRatio}");
    }

    [Fact]
    public void ShortSignal_ReturnsEmptySpectrum_NotVinyl()
    {
        var samples = new float[100];
        var spectrum = ComputeMagnitudeSpectrum(samples);
        Assert.Empty(spectrum);
        var result = _detector.Detect(spectrum, 96000, samples);
        Assert.False(result.IsVinylRip);
    }

    [Fact]
    public void AllZeroSpectrum_NotVinyl()
    {
        var spectrum = new double[2048];
        var result = _detector.Detect(spectrum, 44100, Array.Empty<float>());
        Assert.False(result.IsVinylRip);
    }

    [Fact]
    public void NullSamples_HandledGracefully()
    {
        var spectrum = new double[2048];
        var result = _detector.Detect(spectrum, 44100, null!);
        Assert.False(result.IsVinylRip);
    }

    [Fact]
    public void RumbleRatio_ReportedCorrectly()
    {
        var spectrum = new double[4096];
        spectrum[0] = 6.0;
        spectrum[1] = 6.0;
        for (int i = 42; i <= 170; i++) spectrum[i] = 2.0;
        for (int i = 1881; i < 4096; i++) spectrum[i] = 0.1;
        var result = _detector.Detect(spectrum, 96000, Array.Empty<float>());
        Assert.True(result.IsVinylRip);
        Assert.True(result.RumbleRatio >= 2.9 && result.RumbleRatio <= 3.1,
            $"Expected RumbleRatio ~3.0, got {result.RumbleRatio}");
    }
}
