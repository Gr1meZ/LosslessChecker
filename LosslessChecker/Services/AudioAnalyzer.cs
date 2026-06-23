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
    private readonly BitDepthValidator _bitDepthValidator = new();
    private readonly UpscaleDetector _upscaleDetector = new();
    private readonly VerdictGenerator _verdictGenerator = new();

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

            // Read original format from file header (NAudio decodes to float, losing bit depth info)
            var originalFmt = AudioFormatReader.ReadOriginal(fileInfo.FilePath);
            int actualBitDepth = originalFmt?.BitDepth ?? format.BitsPerSample;
            int actualSampleRate = originalFmt?.SampleRate ?? format.SampleRate;

            result = result with
            {
                Format = GetFormatLabel(fileInfo.FilePath, format, actualBitDepth),
                SampleRate = actualSampleRate,
                BitDepth = actualBitDepth,
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
                    // Status = "Too short",
                    // LosslessScore = 50,
                    // Verdict = "File too short (<5s) for reliable analysis."
                };
            }

            if (ct.IsCancellationRequested)
                return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

            var (cutoff, cutoffSlope, spectrum, spectrogram, spectWidth, spectHeight) =
                _cutoffDetector.DetectFull(samples, format.SampleRate);

            if (ct.IsCancellationRequested)
                return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

            var (hasArtifacts, artifactLevel) = _artifactDetector.Detect(samples, format.SampleRate, cutoff);

            if (ct.IsCancellationRequested)
                return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

            var (dr, truePeak, clippingPercent) = _drMeter.Analyze(samples, format.SampleRate);

            // Bit depth validation
            var (bitSuspicious, bitVerdict, noiseFloor) = _bitDepthValidator.Validate(
                samples, format.BitsPerSample, format.SampleRate);

            // Upscale detection
            var (isUpscale, upscaleVerdict, maxHfDb) = _upscaleDetector.Detect(
                spectrum, format.SampleRate);

            result = result with
            {
                CutoffFrequency = Math.Round(cutoff, 0),
                CutoffSlope = Math.Round(cutoffSlope, 2),
                HasArtifacts = hasArtifacts,
                ArtifactLevel = artifactLevel,
                DynamicRange = Math.Round(dr, 1),
                        //     SamplePeakDb = Math.Round(samplePeak, 1),
                        //     TruePeakDb = Math.Round(truePeak, 1),
                ClippingPercent = Math.Round(clippingPercent, 2),
                // AveragedSpectrum = spectrum,
                SpectrogramFlat = spectrogram,
                SpectrogramWidth = spectWidth,
                SpectrogramHeight = spectHeight,
                BitDepthSuspicious = bitSuspicious,
                // NoiseFloorDb = Math.Round(noiseFloor, 1),
                // BitDepthVerdict = bitVerdict,
                IsUpscale = isUpscale,
                MaxHfDb = Math.Round(maxHfDb, 1),
                // UpscaleVerdict = upscaleVerdict
            };

            // result = _scoreCalculator.Calculate(result);
            result = result with
            {
                // Verdict = _verdictGenerator.Generate(result),
                AnalysisStatus = AnalysisStatus.Completed
            };

            return result;
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

    private static string GetFormatLabel(string filePath, WaveFormat format, int actualBitDepth)
    {
        var ext = Path.GetExtension(filePath).ToUpperInvariant().TrimStart('.');
        return $"{ext} {format.SampleRate / 1000.0:F0}kHz/{actualBitDepth}bit";
    }
}
