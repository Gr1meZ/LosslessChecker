using LosslessChecker.Models;
using LosslessChecker.Services;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class AudioPipelineTests
{
    private readonly AudioPipeline _pipeline = new();

    private static string CreateTempWavFile(double frequency, double durationSec, int sampleRate, int bitDepth, int channels, double gain = 0.5)
    {
        string path = Path.GetTempFileName() + ".wav";
        int numSamples = (int)(sampleRate * durationSec);
        int sampleCount = numSamples * channels;
        int byteRate = sampleRate * (bitDepth / 8) * channels;
        int blockAlign = (bitDepth / 8) * channels;
        int dataSize = sampleCount * (bitDepth / 8);
        int fileSize = 36 + dataSize;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // RIFF header
        bw.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        bw.Write(fileSize);
        bw.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

        // fmt chunk
        bw.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        bw.Write(16); // chunk size
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitDepth);

        // data chunk
        bw.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        bw.Write(dataSize);

        // PCM samples
        double maxVal = (1 << (bitDepth - 1)) - 1;
        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double freq = frequency > 0 ? frequency : (20 + (20000 - 20) * t / durationSec);
            short val = (short)(gain * maxVal * Math.Sin(2 * Math.PI * freq * t));
            for (int ch = 0; ch < channels; ch++)
                bw.Write(val);
        }

        return path;
    }

    [Fact]
    public void Analyze_WavFile_FullSpectrumSweep_ReturnsCompletedResult()
    {
        string tempFile = CreateTempWavFile(frequency: 20000, durationSec: 10, sampleRate: 44100, bitDepth: 16, channels: 2);
        try
        {
            var fileInfo = new AudioFileInfo(tempFile, Path.GetFileName(tempFile), new FileInfo(tempFile).Length);
            var result = _pipeline.Analyze(fileInfo);

            Assert.Equal(AnalysisStatus.Completed, result.AnalysisStatus);
            Assert.Null(result.ErrorMessage);
            Assert.Equal(44100, result.SampleRate);
            Assert.Equal(16, result.BitDepth);
            Assert.Equal(2, result.Channels);
            Assert.True(result.DurationSeconds >= 9.5, $"Duration {result.DurationSeconds} too short");
            Assert.StartsWith("WAV", result.Format);
            Assert.NotEmpty(result.ClaimedType);

            // Full spectrum sweep should have cutoff near Nyquist
            Assert.True(result.CutoffFrequency > 19000, $"Cutoff {result.CutoffFrequency} too low for full sweep");

            Assert.NotEmpty(result.Authenticity);
            Assert.True(result.DynamicRange >= 0, $"DynamicRange {result.DynamicRange} should be non-negative");
            Assert.False(double.IsNaN(result.DynamicRange));
            Assert.False(double.IsNaN(result.IntegratedLufs));
            Assert.False(double.IsNaN(result.TruePeakDb));
            Assert.False(double.IsNaN(result.Plr));
            Assert.False(string.IsNullOrEmpty(result.ShelfType));
            Assert.False(string.IsNullOrEmpty(result.Decision));
            Assert.True(result.MetricsCoverage >= 0);
            Assert.NotEmpty(result.StructuredReport);
            Assert.NotNull(result.SpectrogramDb);
            Assert.True(result.SpectrogramWidth > 0);
            Assert.True(result.SpectrogramHeight > 0);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Analyze_ShortFile_ReturnsSkipped()
    {
        string tempFile = CreateTempWavFile(frequency: 1000, durationSec: 3, sampleRate: 44100, bitDepth: 16, channels: 2);
        try
        {
            var fileInfo = new AudioFileInfo(tempFile, Path.GetFileName(tempFile), new FileInfo(tempFile).Length);
            var result = _pipeline.Analyze(fileInfo);

            Assert.Equal(AnalysisStatus.Completed, result.AnalysisStatus);
            Assert.Equal("SKIPPED", result.Authenticity);
            Assert.Equal("SKIPPED", result.Decision);
            Assert.Equal(0, result.QualityScore);
            Assert.Contains("too short", result.StructuredReport);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Analyze_WavFile_1kHzTone_ReturnsSaneValues()
    {
        string tempFile = CreateTempWavFile(frequency: 1000, durationSec: 8, sampleRate: 44100, bitDepth: 16, channels: 2);
        try
        {
            var fileInfo = new AudioFileInfo(tempFile, Path.GetFileName(tempFile), new FileInfo(tempFile).Length);
            var result = _pipeline.Analyze(fileInfo);

            Assert.Equal(AnalysisStatus.Completed, result.AnalysisStatus);
            Assert.Equal(44100, result.SampleRate);
            Assert.Equal(16, result.BitDepth);
            Assert.Equal(2, result.Channels);

            // 1kHz tone should have reasonable RMS (not -infinity)
            Assert.True(result.OverallRmsDb > -60, $"OverallRmsDb {result.OverallRmsDb} too low");

            // Mono tone in both channels → high correlation
            Assert.True(result.Correlation > 0.9, $"Correlation {result.Correlation} should be high for identical L/R");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Analyze_WavFile_Mono_ReturnsSingleChannel()
    {
        string tempFile = CreateTempWavFile(frequency: 440, durationSec: 6, sampleRate: 48000, bitDepth: 24, channels: 1);
        try
        {
            var fileInfo = new AudioFileInfo(tempFile, Path.GetFileName(tempFile), new FileInfo(tempFile).Length);
            var result = _pipeline.Analyze(fileInfo);

            Assert.Equal(AnalysisStatus.Completed, result.AnalysisStatus);
            Assert.Equal(48000, result.SampleRate);
            Assert.Equal(1, result.Channels);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Analyze_WavFile_48kHz_Sweep_ReturnsCorrectSampleRate()
    {
        string tempFile = CreateTempWavFile(frequency: -1, durationSec: 7, sampleRate: 48000, bitDepth: 24, channels: 2);
        try
        {
            var fileInfo = new AudioFileInfo(tempFile, Path.GetFileName(tempFile), new FileInfo(tempFile).Length);
            var result = _pipeline.Analyze(fileInfo);

            Assert.Equal(AnalysisStatus.Completed, result.AnalysisStatus);
            Assert.Equal(48000, result.SampleRate);
            Assert.Equal(24, result.BitDepth);
            Assert.Contains("48kHz", result.Format);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_SupportsCancellation()
    {
        string tempFile = CreateTempWavFile(frequency: 1000, durationSec: 10, sampleRate: 44100, bitDepth: 16, channels: 2);
        try
        {
            var fileInfo = new AudioFileInfo(tempFile, Path.GetFileName(tempFile), new FileInfo(tempFile).Length);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = await _pipeline.AnalyzeAsync(fileInfo, cts.Token);

            Assert.Equal(AnalysisStatus.Error, result.AnalysisStatus);
            Assert.Equal("Cancelled", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
