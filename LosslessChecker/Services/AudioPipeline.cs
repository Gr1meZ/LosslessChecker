using System.IO;
using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Services.Analysis;

namespace LosslessChecker.Services;

public class AudioPipeline
{
    private readonly CutoffDetector _cutoff = new();
    private readonly ArtifactDetector _artifacts = new();
    private readonly TruePeakDetector _truePeak = new();
    private readonly LufsMeter _lufs = new();
    private readonly DrMeter _dr = new();
    private readonly DcOffsetDetector _dcOffset = new();
    private readonly PhaseAnalyzer _phase = new();
    private readonly BitDepthValidator _bitDepth = new();
    private readonly UpscaleDetector _upscale = new();
    private readonly SpectrogramBuilder _spectro = new();
    private readonly ResamplingDetector _resampling = new();
    private readonly LosslessScorer _losslessScorer = new();
    private readonly QualityScorer _qualityScorer = new();
    private readonly VerdictGenerator _verdict = new();
    private readonly VinylDetector _vinyl = new();
    private readonly ContainerAnalyzer _container = new();

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

            if (ct.IsCancellationRequested) return Cancelled(result);

            var buffer = AudioDecoder.Decode(fileInfo.FilePath, ct);
            double duration = buffer.Length / (double)buffer.SampleRate;
            result = result with { DurationSeconds = Math.Round(duration, 1) };

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

            // mono is the single shared buffer — Left channel from decoder
            var mono = buffer.Left;

            var (cutoffHz, cutoffSlope, spectrum) = _cutoff.DetectFull(mono, sampleRate);
            var (encoderMatch, shelfType) = _cutoff.ClassifyCutoff(cutoffHz, cutoffSlope, sampleRate);
            bool isFakeHiRes = _cutoff.IsFakeHiRes(cutoffHz, shelfType, sampleRate);

            if (ct.IsCancellationRequested) return Cancelled(result);

            var (hasArtifacts, artifactLevel, artifactType) =
                _artifacts.Detect(mono, sampleRate, cutoffHz);
            if (ct.IsCancellationRequested) return Cancelled(result);

            var tpResult = _truePeak.Analyze(buffer);
            if (ct.IsCancellationRequested) return Cancelled(result);

            var lufsResult = _lufs.Analyze(buffer);
            if (ct.IsCancellationRequested) return Cancelled(result);

            var drResult = _dr.AnalyzeStereo(buffer);
            if (ct.IsCancellationRequested) return Cancelled(result);

            var dcResult = _dcOffset.Analyze(buffer);
            if (ct.IsCancellationRequested) return Cancelled(result);

            var phaseResult = _phase.Analyze(buffer);
            if (ct.IsCancellationRequested) return Cancelled(result);

            var bitResult = _bitDepth.ValidateStereo(buffer, bitDepth);
            if (ct.IsCancellationRequested) return Cancelled(result);

            var (isUpscale, upscaleVerdict, maxHfDb) = _upscale.Detect(spectrum, sampleRate);
            if (ct.IsCancellationRequested) return Cancelled(result);

            var vinylResult = _vinyl.Detect(spectrum, sampleRate, mono);
            var containerResult = _container.Analyze(fileInfo.FilePath, mono, sampleRate);
            var (hasPreEcho, preEchoCount) = _artifacts.DetectPreEcho(mono, sampleRate);
            bool hasSpectralHoles = _artifacts.DetectSpectralHoles(spectrum, sampleRate / 2.0);

            var (spectroData, spectroW, spectroH) = _spectro.Build(mono, sampleRate);

            var resamplingResult = _resampling.DetectFromSpectrum(spectrum, sampleRate);

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
                OverallRmsDb = ComputeOverallRms(mono),
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
                HasAliasing = resamplingResult.HasAliasing,
                HasRinging = resamplingResult.HasRinging,
                ResamplingVerdict = resamplingResult.Verdict,
                SpectrogramFlat = spectroData,
                SpectrogramWidth = spectroW,
                SpectrogramHeight = spectroH
            };

            result = result with { Authenticity = _losslessScorer.Classify(result) };

            var losslessScore = _losslessScorer.Score(result);
            var hiResScore = _losslessScorer.ScoreHiRes(result);
            var (qualityPercent, decision) = _qualityScorer.Score(result);
            result = result with
            {
                LosslessScore = Math.Round(losslessScore, 1),
                HiResScore = Math.Round(hiResScore, 1),
                QualityScorePercent = Math.Round(qualityPercent, 1),
                QualityScore = (int)Math.Round(qualityPercent / 10.0),
                MetricsCoverage = ComputeMetricsCoverage(result),
                Decision = decision,
                StructuredReport = _verdict.Generate(result),
                AnalysisStatus = AnalysisStatus.Completed
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
        return $"{ext} {sampleRate / 1000.0:F0}kHz/{bitDepth}bit";
    }

    private static double ComputeOverallRms(float[] mono)
    {
        double sumSq = 0;
        int n = mono.Length;
        for (int i = 0; i < n; i++)
            sumSq += (double)mono[i] * mono[i];
        double rms = Math.Sqrt(sumSq / n);
        return Math.Round(20.0 * Math.Log10(Math.Max(rms, 1e-10)), 1);
    }
}
