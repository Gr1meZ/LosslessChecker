using System.IO;
using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Services.Analysis;
using LosslessChecker.Services.Verification;

namespace LosslessChecker.Services;

public class AudioPipeline
{
    private readonly CutoffDetector _cutoff = new();
    private readonly ArtifactDetector _artifacts = new();
    private readonly LufsMeter _lufs = new();
    private readonly UpscaleDetector _upscale = new();
    private readonly SpectrogramBuilder _spectro = new();
    private readonly ResamplingDetector _resampling = new();
    private readonly LosslessScorer _losslessScorer = new();
    private readonly QualityScorer _qualityScorer = new();
    private readonly VerdictGenerator _verdict = new();
    private readonly VinylDetector _vinyl = new();
    private readonly ContainerAnalyzer _container = new();

    private static string GetClaimedType(string filePath, int sampleRate)
    {
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        string type = ext switch
        {
            ".mp3" => "MP3",
            ".m4a" => "AAC",
            ".flac" => "FLAC",
            ".wav" => "WAV",
            ".alac" => "ALAC",
            _ => "Unknown"
        };
        if (sampleRate >= 88200)
            type = $"HI-RES {sampleRate / 1000:F0}k";
        return type;
    }

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
            var originalFmt = AudioFormatReader.ReadOriginal(fileInfo.FilePath);
            int bitDepth = originalFmt?.BitDepth ?? 16;
            int sampleRate = originalFmt?.SampleRate ?? 44100;
            int channels = originalFmt?.Channels ?? 2;

            result = result with
            {
                Format = GetFormatLabel(fileInfo.FilePath, sampleRate, bitDepth),
                SampleRate = sampleRate,
                BitDepth = bitDepth,
                Channels = channels
            };

            var tags = TagReader.Read(fileInfo.FilePath);
            if (tags != null)
            {
                result = result with
                {
                    Artist = tags.Artist,
                    Album = tags.Album,
                    Genre = tags.Genre,
                    FileName = tags.Title,
                    CoverData = tags.CoverData
                };
            }

            int mp3Bitrate = 0;
            string mp3Encoder = "";
            if (fileInfo.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var mp3Reader = new NAudio.Wave.Mp3FileReader(fileInfo.FilePath);
                    mp3Bitrate = mp3Reader.Mp3WaveFormat.AverageBytesPerSecond * 8 / 1000;
                    var xingHeader = mp3Reader.XingHeader;
                    mp3Encoder = xingHeader != null ? "LAME" : "Unknown";
                }
                catch { mp3Encoder = "Error"; }
            }

            int aacBitrate = 0;
            bool isAac = false;
            if (fileInfo.FilePath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
                || fileInfo.FilePath.EndsWith(".aac", StringComparison.OrdinalIgnoreCase)
                || fileInfo.FilePath.EndsWith(".alac", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var mp4Info = Mp4CodecReader.DetectCodec(fileInfo.FilePath);
                    if (mp4Info.Codec == "aac")
                    {
                        isAac = true;
                        aacBitrate = mp4Info.Bitrate;
                    }
                }
                catch { }
            }

            if (ct.IsCancellationRequested) return Cancelled(result);

            var buffer = AudioDecoder.Decode(fileInfo.FilePath, ct);
            double duration = buffer.Length / (double)buffer.SampleRate;
            result = result with { DurationSeconds = Math.Round(duration, 1) };

            int actualBitrate = 0;
            try
            {
                long fileSize = new System.IO.FileInfo(fileInfo.FilePath).Length;
                if (duration > 0)
                    actualBitrate = (int)(fileSize * 8.0 / (duration * 1000.0));
            }
            catch { }

            if (duration < 5.0)
            {
                return result with
                {
                    AnalysisStatus = AnalysisStatus.Completed,
                    Authenticity = "SKIPPED",
                    QualityScore = 5,
                    Decision = "INVESTIGATE",
                    StructuredReport = "File too short (<5s) for reliable analysis."
                };
            }

            // mono is the Left channel for spectral analysis
            var mono = buffer.Left;

            var (cutoffHz, cutoffSlope, spectrum) = _cutoff.DetectFull(mono, sampleRate);
            var (encoderMatch, shelfType) = _cutoff.ClassifyCutoff(cutoffHz, cutoffSlope, sampleRate);
            bool isFakeHiRes = _cutoff.IsFakeHiRes(cutoffHz, shelfType, sampleRate);

            if (ct.IsCancellationRequested) return Cancelled(result);

            var artifactTask = Task.Run(() => _artifacts.Detect(mono, sampleRate, cutoffHz), ct);
            var truePeak = new Analyzers.TruePeakDetector();
            var dr = new DrMeter();
            var dcOffset = new Analyzers.DcOffsetDetector();
            var phase = new Analyzers.PhaseAnalyzer();
            var bitDepthValidator = new BitDepthValidator();
            var tpTask = Task.Run(() => truePeak.Analyze(buffer), ct);
            var lufsTask = Task.Run(() => _lufs.Analyze(buffer), ct);
            var drTask = Task.Run(() => dr.AnalyzeStereo(buffer), ct);
            var dcTask = Task.Run(() => dcOffset.Analyze(buffer), ct);
            var phaseTask = Task.Run(() => phase.Analyze(buffer), ct);
            var bitTask = Task.Run(() => bitDepthValidator.ValidateStereo(buffer, bitDepth), ct);
            var spectroTask = Task.Run(() => _spectro.Build(mono, sampleRate), ct);

            var (isUpscale, upscaleVerdict, maxHfDb) = _upscale.Detect(spectrum, sampleRate);
            var vinylResult = _vinyl.Detect(spectrum, sampleRate, mono);
            var containerResult = _container.Analyze(fileInfo.FilePath, mono, sampleRate);
            var (hasPreEcho, preEchoCount) = _artifacts.DetectPreEcho(mono, sampleRate);
            bool hasSpectralHoles = _artifacts.DetectSpectralHoles(spectrum, sampleRate / 2.0);
            var resamplingResult = _resampling.DetectFromSpectrum(spectrum, sampleRate);

            if (ct.IsCancellationRequested) return Cancelled(result);

            var (hasArtifacts, artifactLevel, artifactType) = artifactTask.Result;
            var tpResult = tpTask.Result;
            var lufsResult = lufsTask.Result;
            var drResult = drTask.Result;
            var dcResult = dcTask.Result;
            var phaseResult = phaseTask.Result;
            var bitResult = bitTask.Result;
            var spectroData = spectroTask.Result;

            if (ct.IsCancellationRequested) return Cancelled(result);

            result = result with
            {
                CutoffFrequency = Math.Round(cutoffHz, 0),
                CutoffSlope = Math.Round(cutoffSlope, 2),
                ShelfType = shelfType,
                EncoderMatch = encoderMatch,
                HasArtifacts = hasArtifacts,
                ArtifactLevel = artifactLevel,
                ArtifactType = artifactType,
                HasPreEcho = hasPreEcho,
                PreEchoCount = preEchoCount,
                HasSpectralHoles = hasSpectralHoles,
                SamplePeakDb = tpResult.SamplePeakDbL,
                TruePeakDb = Math.Max(tpResult.TruePeakDbL, tpResult.TruePeakDbR),
                ClippingPercent = tpResult.ClippingPercent,
                HasIsp = tpResult.HasIsp,
                HasHardClipping = tpResult.ClippingPercent > 0,
                DynamicRange = drResult.Dr,
                OverallRmsDb = ComputeOverallRms(buffer),
                IntegratedLufs = lufsResult.IntegratedLufs,
                LoudnessRange = lufsResult.LoudnessRange,
                Plr = Math.Round(Math.Max(tpResult.TruePeakDbL, tpResult.TruePeakDbR) - lufsResult.IntegratedLufs, 1),
                DcOffsetL = dcResult.DcOffsetL,
                DcOffsetR = dcResult.DcOffsetR,
                BitDepthSuspicious = bitResult.IsSuspicious,
                LsbZeroPadded = bitResult.LsbZeroPadded,
                EffectiveBitDepth = bitResult.EffectiveBitDepth,
                Correlation = phaseResult.Correlation,
                IsMonoCompatible = phaseResult.IsMonoCompatible,
                IsUpscale = isUpscale || isFakeHiRes,
                MaxHfDb = Math.Round(maxHfDb, 1),
                IsVinylRip = vinylResult.IsVinylRip,
                VinylRumbleRatio = vinylResult.RumbleRatio,
                VinylHfNoiseRatio = vinylResult.HfNoiseRatio,
                IsCdAligned = containerResult.IsCdAligned,
                FlacIntegrityOk = containerResult.FlacIntegrityOk,
                ContainerSource = containerResult.Source,
                IsMqa = containerResult.IsMqa,
                MqaDetails = containerResult.MqaDetails,
                IsHdcd = containerResult.IsHdcd,
                PcmMd5Hex = PcmHasher.ToHexString(containerResult.PcmMd5),
                HasAliasing = resamplingResult.HasAliasing,
                HasRinging = resamplingResult.HasRinging,
                ResamplingVerdict = resamplingResult.Verdict,
                SpectrogramDb = spectroData.DbValues,
                SpectrogramWidth = spectroData.Width,
                SpectrogramHeight = spectroData.Height,
                SpectrogramSampleRate = spectroData.SampleRate,
                SpectrogramDuration = spectroData.Duration,
                Mp3Bitrate = mp3Bitrate,
                Mp3Encoder = mp3Encoder,
                AacBitrate = aacBitrate,
                IsAac = isAac,
                ActualBitrate = actualBitrate
            };

            result = result with
            {
                ClaimedType = GetClaimedType(fileInfo.FilePath, sampleRate)
            };

            var (bandwidth, detectedType) = CutoffDetector.ClassifyBandwidth(
                cutoffHz, shelfType, sampleRate, hasArtifacts, artifactLevel,
                hasSpectralHoles, maxHfDb, bitResult.LsbZeroPadded,
                bitResult.EffectiveBitDepth, bitDepth, containerResult.IsCdAligned,
                containerResult.IsMqa, containerResult.IsHdcd,
                tpResult.ClippingPercent > 0, encoderMatch);

            result = result with
            {
                Bandwidth = bandwidth,
                DetectedType = detectedType
            };

            bool isMp3 = fileInfo.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
            bool isMp3Detected = detectedType.StartsWith("MP3") || isMp3;
            bool isAacDetected = detectedType.StartsWith("AAC") || isAac;

            double mp3QualityScore = 0;
            double aacQualityScore = 0;

            if (isMp3Detected)
            {
                mp3QualityScore = ComputeMp3Quality(cutoffHz, sampleRate, mp3Bitrate, actualBitrate, artifactLevel, hasSpectralHoles);
            }
            else if (isAacDetected)
            {
                aacQualityScore = ComputeAacQuality(cutoffHz, sampleRate, aacBitrate, actualBitrate, artifactLevel, hasSpectralHoles);
            }

            if (isMp3)
            {
                result = result with { Authenticity = "LOSSY (MP3)" };
            }
            else if (isAac)
            {
                result = result with { Authenticity = "LOSSY (AAC)" };
            }
            else
            {
                result = result with { Authenticity = _losslessScorer.Classify(result) };
            }

            var losslessScore = isMp3Detected ? mp3QualityScore
                : isAacDetected ? aacQualityScore
                : _losslessScorer.Score(result);
            var hiResScore = _losslessScorer.ScoreHiRes(result);

            double qualityPercent;
            string decision;

            if (isMp3Detected)
            {
                var (masterScore, _) = _qualityScorer.Score(result);
                qualityPercent = mp3QualityScore * 0.6 + masterScore * 0.4;
                decision = mp3QualityScore >= 80 ? "KEEP"
                    : mp3QualityScore >= 50 ? "INVESTIGATE"
                    : "REPLACE";
            }
            else if (isAacDetected)
            {
                var (masterScore, _) = _qualityScorer.Score(result);
                qualityPercent = aacQualityScore * 0.6 + masterScore * 0.4;
                decision = aacQualityScore >= 80 ? "KEEP"
                    : aacQualityScore >= 50 ? "INVESTIGATE"
                    : "REPLACE";
            }
            else
            {
                (qualityPercent, decision) = _qualityScorer.Score(result);
            }

            result = result with
            {
                LosslessScore = Math.Round(losslessScore, 1),
                HiResScore = Math.Round(hiResScore, 1),
                QualityScorePercent = Math.Round(qualityPercent, 1),
                QualityScore = (int)Math.Round(qualityPercent / 10.0),
                MetricsCoverage = ComputeMetricsCoverage(result),
                Decision = decision,
                StructuredReport = _verdict.Generate(result),
                WhyVerdict = _verdict.GenerateWhy(result),
                AnalysisStatus = AnalysisStatus.Completed,
                Mp3QualityScore = mp3QualityScore
            };

            return result;
        }
        catch (OperationCanceledException)
        {
            return Cancelled(result);
        }
        catch (Exception ex)
        {
            return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = ex.Message };
        }
    }

    private static double ComputeMetricsCoverage(AnalysisResult r)
    {
        int passed = 0, total = 8;
        if (r.CutoffFrequency / (r.SampleRate / 2.0) >= 0.90) passed++;
        if (r.ArtifactLevel == "None" || r.ArtifactLevel == "Weak") passed++;
        if (r.DynamicRange >= 3) passed++;
        if (r.ClippingPercent < 0.5) passed++;
        if (!r.HasIsp) passed++;
        if (r.IntegratedLufs <= -7 && r.IntegratedLufs > -70) passed++;
        if (r.Correlation >= 0) passed++;
        if (!r.LsbZeroPadded) passed++;
        return Math.Round((double)passed / total * 100, 0);
    }

    private static AnalysisResult Cancelled(AnalysisResult r)
        => r with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

    private static string GetFormatLabel(string filePath, int sampleRate, int bitDepth)
    {
        var ext = Path.GetExtension(filePath).ToUpperInvariant().TrimStart('.');
        if (ext == "M4A" || ext == "ALAC")
        {
            try
            {
                var info = Mp4CodecReader.DetectCodec(filePath);
                ext = info.Codec == "alac" ? "ALAC" : info.Codec == "aac" ? "AAC" : ext;
            }
            catch { }
        }
        return $"{ext} {sampleRate / 1000.0:F0}kHz/{bitDepth}bit";
    }

    private static double ComputeMp3Quality(double cutoffHz, int sampleRate, int bitrate, int actualBitrate,
        string artifactLevel, bool hasSpectralHoles)
    {
        double score = 100;

        double expectedCutoff = bitrate switch
        {
            >= 320 => 20500,
            >= 256 => 19000,
            >= 192 => 18000,
            >= 128 => 16500,
            _ => 16000
        };

        if (cutoffHz < expectedCutoff * 0.8) score -= 40;
        else if (cutoffHz < expectedCutoff * 0.9) score -= 20;
        else if (cutoffHz < expectedCutoff) score -= 5;

        if (bitrate >= 256 && cutoffHz < 18000) score -= 30;
        if (bitrate >= 192 && cutoffHz < 16000) score -= 25;

        if (actualBitrate > 0 && bitrate > 0)
        {
            double ratio = (double)bitrate / actualBitrate;
            if (ratio > 2.5) score -= 40;
            else if (ratio > 1.5) score -= 20;
            else if (ratio > 1.2) score -= 8;
        }

        if (artifactLevel == "Strong") score -= 25;
        else if (artifactLevel == "Medium") score -= 12;
        else if (artifactLevel == "Weak") score -= 5;

        if (hasSpectralHoles) score -= 15;

        return Math.Max(0, Math.Min(100, score));
    }

    private static double ComputeAacQuality(double cutoffHz, int sampleRate, int bitrate, int actualBitrate,
        string artifactLevel, bool hasSpectralHoles)
    {
        double score = 100;

        double expectedCutoff = bitrate switch
        {
            >= 256 => 20000,
            >= 192 => 18000,
            >= 128 => 16000,
            _ => 15000
        };

        if (cutoffHz < expectedCutoff * 0.8) score -= 40;
        else if (cutoffHz < expectedCutoff * 0.9) score -= 20;
        else if (cutoffHz < expectedCutoff) score -= 5;

        if (bitrate >= 192 && cutoffHz < 16000) score -= 25;

        if (actualBitrate > 0 && bitrate > 0)
        {
            double ratio = (double)bitrate / actualBitrate;
            if (ratio > 2.5) score -= 40;
            else if (ratio > 1.5) score -= 20;
            else if (ratio > 1.2) score -= 8;
        }

        if (artifactLevel == "Strong") score -= 25;
        else if (artifactLevel == "Medium") score -= 12;
        else if (artifactLevel == "Weak") score -= 5;

        if (hasSpectralHoles) score -= 15;

        return Math.Max(0, Math.Min(100, score));
    }

    private static double ComputeOverallRms(StereoBuffer buffer)
    {
        double sumSq = 0;
        long n = buffer.Length;
        if (buffer.IsStereo)
        {
            var left = buffer.Left;
            var right = buffer.Right;
            for (int i = 0; i < n; i++)
            {
                double m = (left[i] + right[i]) * 0.5;
                sumSq += m * m;
            }
        }
        else
        {
            var left = buffer.Left;
            for (int i = 0; i < n; i++)
                sumSq += (double)left[i] * left[i];
        }
        double rms = Math.Sqrt(sumSq / n);
        return Math.Round(20.0 * Math.Log10(Math.Max(rms, 1e-10)), 1);
    }
}
