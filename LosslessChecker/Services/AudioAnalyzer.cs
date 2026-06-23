using System.IO;
using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Services.Analysis;

namespace LosslessChecker.Services;

public class AudioAnalyzer
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
    private readonly AuthenticityClassifier _authClassifier = new();
    private readonly QualityScorer _qualityScorer = new();
    private readonly VerdictGenerator _verdict = new();

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

            if (ct.IsCancellationRequested)
                return Cancelled(result);

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

            var mono = new float[buffer.Length];
            for (int i = 0; i < buffer.Length; i++)
                mono[i] = buffer.IsStereo
                    ? (buffer.Left[i] + buffer.Right[i]) * 0.5f
                    : buffer.Left[i];

            var (cutoffHz, cutoffSlope, spectrum) = _cutoff.DetectFull(mono, sampleRate);
            var (encoderMatch, shelfType) = _cutoff.ClassifyCutoff(cutoffHz, cutoffSlope, sampleRate);
            bool isFakeHiRes = _cutoff.IsFakeHiRes(cutoffHz, sampleRate);

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

            var (spectroData, spectroW, spectroH) = _spectro.Build(mono, sampleRate);

            result = result with
            {
                CutoffFrequency = Math.Round(cutoffHz, 0),
                CutoffSlope = Math.Round(cutoffSlope, 2),
                ShelfType = shelfType,
                EncoderMatch = encoderMatch,
                HasArtifacts = hasArtifacts,
                ArtifactLevel = artifactLevel,
                ArtifactType = artifactType,
                SamplePeakDb = tpResult.SamplePeakDbL,
                TruePeakDb = Math.Max(tpResult.TruePeakDbL, tpResult.TruePeakDbR),
                ClippingPercent = tpResult.ClippingPercent,
                HasIsp = tpResult.HasIsp,
                DynamicRange = drResult.Dr,
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
                SpectrogramFlat = spectroData,
                SpectrogramWidth = spectroW,
                SpectrogramHeight = spectroH
            };

            result = result with { Authenticity = _authClassifier.Classify(result) };
            var (qualityScore, decision) = _qualityScorer.Score(result);
            result = result with
            {
                QualityScore = qualityScore,
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

    private static AnalysisResult Cancelled(AnalysisResult r)
        => r with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

    private static string GetFormatLabel(string filePath, int sampleRate, int bitDepth)
    {
        var ext = Path.GetExtension(filePath).ToUpperInvariant().TrimStart('.');
        return $"{ext} {sampleRate / 1000.0:F0}kHz/{bitDepth}bit";
    }
}
