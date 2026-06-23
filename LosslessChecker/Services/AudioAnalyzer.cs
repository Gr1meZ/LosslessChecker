using System.IO;
using LosslessChecker.Models;
using NAudio.Wave;

namespace LosslessChecker.Services;

public class AudioAnalyzer
{
    private readonly CutoffDetector _cutoffDetector = new();
    private readonly ArtifactDetector _artifactDetector = new();
    private readonly DrMeter _drMeter = new();
    private readonly ScoreCalculator _scoreCalculator = new();

    public AnalysisResult Analyze(AudioFileInfo fileInfo, CancellationToken ct = default)
    {
        var result = new AnalysisResult
        {
            FilePath = fileInfo.FilePath,
            FileName = fileInfo.FileName,
            AnalysisStatus = AnalysisStatus.Processing
        };

        try
        {
            using var reader = CreateReader(fileInfo.FilePath);
            if (reader == null)
            {
                return result with
                {
                    AnalysisStatus = AnalysisStatus.Error,
                    ErrorMessage = "Unsupported format"
                };
            }

            var format = reader.WaveFormat;
            result = result with
            {
                Format = GetFormatLabel(fileInfo.FilePath, format),
                SampleRate = format.SampleRate,
                BitDepth = format.BitsPerSample,
            };

            if (ct.IsCancellationRequested)
                return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

            var samples = DecodeToMono(reader);

            if (ct.IsCancellationRequested)
                return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

            double duration = samples.Length / (double)format.SampleRate;
            result = result with { DurationSeconds = Math.Round(duration, 1) };

            if (duration < 5.0)
            {
                return result with
                {
                    AnalysisStatus = AnalysisStatus.Completed,
                    Status = "Too short",
                    LosslessScore = 50
                };
            }

            if (ct.IsCancellationRequested)
                return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

            var (cutoff, spectrum, spectrogram) = _cutoffDetector.DetectFull(samples, format.SampleRate);

            if (ct.IsCancellationRequested)
                return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

            var (hasArtifacts, artifactLevel) = _artifactDetector.Detect(samples, format.SampleRate, cutoff);

            if (ct.IsCancellationRequested)
                return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

            var (dr, truePeak, clippingPercent) = _drMeter.Analyze(samples, format.SampleRate);

            result = result with
            {
                CutoffFrequency = Math.Round(cutoff, 0),
                HasArtifacts = hasArtifacts,
                ArtifactLevel = artifactLevel,
                DynamicRange = Math.Round(dr, 1),
                TruePeak = Math.Round(truePeak, 1),
                ClippingPercent = Math.Round(clippingPercent, 2),
                AveragedSpectrum = spectrum,
                SpectrogramData = spectrogram
            };

            result = _scoreCalculator.Calculate(result);
            return result with { AnalysisStatus = AnalysisStatus.Completed };
        }
        catch (Exception ex)
        {
            return result with
            {
                AnalysisStatus = AnalysisStatus.Error,
                ErrorMessage = ex.Message
            };
        }
    }

    private static float[] DecodeToMono(WaveStream reader)
    {
        ISampleProvider provider;

        if (reader.WaveFormat.Channels > 1)
        {
            var monoFormat = new WaveFormat(reader.WaveFormat.SampleRate, 16, 1);
            using var resampler = new MediaFoundationResampler(reader, monoFormat);
            provider = resampler.ToSampleProvider();
        }
        else
        {
            provider = reader.ToSampleProvider();
        }

        var samples = new List<float>((int)(reader.TotalTime.TotalSeconds * reader.WaveFormat.SampleRate));
        var buffer = new float[4096];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                samples.Add(buffer[i]);
        }

        return samples.ToArray();
    }

    private static WaveStream? CreateReader(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => new Mp3FileReader(filePath),
            ".wav" => new WaveFileReader(filePath),
            ".flac" => new AudioFileReader(filePath),
            ".m4a" or ".alac" => new AudioFileReader(filePath),
            _ => null
        };
    }

    private static string GetFormatLabel(string filePath, WaveFormat format)
    {
        var ext = Path.GetExtension(filePath).ToUpperInvariant().TrimStart('.');
        return $"{ext} {format.SampleRate / 1000.0:F0}kHz/{format.BitsPerSample}bit";
    }
}
