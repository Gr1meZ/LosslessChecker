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
    private readonly UpscaleDetector _upscale = new();
    private readonly ResamplingDetector _resampling = new();
    private readonly LosslessScorer _losslessScorer = new();
    private readonly QualityScorer _qualityScorer = new();
    private readonly VerdictGenerator _verdict = new();
    private readonly VinylDetector _vinyl = new();
    private readonly ContainerAnalyzer _container = new();

    private static string GetClaimedType(string filePath, int sampleRate)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        string type;
        if (ext == ".m4a")
        {
            try
            {
                var info = Mp4CodecReader.DetectCodec(filePath);
                type = info.Codec == "alac" ? "ALAC" : "AAC";
            }
            catch { type = "AAC"; }
        }
        else
        {
            type = ext switch
            {
                ".mp3" => "MP3",
                ".flac" => "FLAC",
                ".wav" => "WAV",
                ".alac" => "ALAC",
                _ => "Unknown"
            };
        }
        if (sampleRate >= 88200)
            type = $"HI-RES {sampleRate / 1000:F0}k";
        return type;
    }

    public AnalysisResult Analyze(AudioFileInfo fileInfo, CancellationToken ct = default)
        => AnalyzeAsync(fileInfo, ct).GetAwaiter().GetResult();

    public async Task<AnalysisResult> AnalyzeAsync(AudioFileInfo fileInfo, CancellationToken ct = default)
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
            double? replayGainTrackDb = null;
            if (tags != null)
            {
                result = result with
                {
                    Artist = tags.Artist,
                    Album = tags.Album,
                    Genre = tags.Genre,
                    FileName = (tags.Artist != null && tags.Title != null)
                        ? $"{tags.Artist} - {tags.Title}"
                        : tags.Title,
                    CoverData = tags.CoverData
                };
                replayGainTrackDb = tags.ReplayGainTrackDb;
            }

            int mp3Bitrate = 0;
            string mp3Encoder = "";
            int mp3VbrFrames = 0;
            if (fileInfo.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var mp3Reader = new NAudio.Wave.Mp3FileReader(fileInfo.FilePath);
                    var xingHeader = mp3Reader.XingHeader;
                    if (xingHeader != null)
                    {
                        mp3Bitrate = xingHeader.Mp3Frame.BitRate / 1000;
                        mp3VbrFrames = xingHeader.Frames;
                        mp3Encoder = xingHeader.Frames > 0 ? "LAME VBR" : $"LAME CBR {mp3Bitrate} kbps";
                    }
                    else
                    {
                        mp3Bitrate = mp3Reader.Mp3WaveFormat.AverageBytesPerSecond * 8 / 1000;
                        mp3Encoder = "Unknown";
                    }
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

            double duration = GetFileDuration(fileInfo.FilePath);
            result = result with { DurationSeconds = Math.Round(duration, 1) };

            int actualBitrate = 0;
            double averageBitrateKbps = 0;
            double compressionRatio = 0;
            double minFrameBitrateKbps = 0;
            double maxFrameBitrateKbps = 0;
            try
            {
                using var fs = new System.IO.FileStream(fileInfo.FilePath, FileMode.Open, FileAccess.Read);
                long fileSize = fs.Length;
                long dataStart = 0;

                bool isFlacFile = fileInfo.FilePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);
                bool isMp3File = fileInfo.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

                if (isMp3File)
                {
                    var tagHeader = new byte[10];
                    if (fs.Read(tagHeader, 0, 10) == 10 &&
                        tagHeader[0] == 'I' && tagHeader[1] == 'D' && tagHeader[2] == '3')
                    {
                        int tagSize = ((tagHeader[6] & 0x7F) << 21) | ((tagHeader[7] & 0x7F) << 14)
                                    | ((tagHeader[8] & 0x7F) << 7) | (tagHeader[9] & 0x7F);
                        dataStart = tagSize + 10;
                    }
                }
                else if (isFlacFile)
                {
                    dataStart = AudioFormatReader.GetFlacMetadataSize(fileInfo.FilePath);
                }

                long dataSize = fileSize - dataStart;
                if (duration > 0)
                {
                    actualBitrate = (int)(dataSize * 8.0 / (duration * 1000.0));
                    averageBitrateKbps = Math.Round(dataSize * 8.0 / (duration * 1000.0), 1);
                }

                long uncompressedSize = (long)(duration * sampleRate * (bitDepth / 8.0) * channels);
                if (uncompressedSize > 0)
                    compressionRatio = Math.Round((double)dataSize / uncompressedSize, 3);

                if (isFlacFile && dataStart > 0 && fileSize > dataStart + 100)
                {
                    var (minBps, maxBps) = AnalyzeFlacFrameBitrates(fileInfo.FilePath, dataStart, fileSize, sampleRate);
                    minFrameBitrateKbps = minBps;
                    maxFrameBitrateKbps = maxBps;
                }
            }
            catch { }

            if (duration < 5.0)
            {
                return result with
                {
                    AnalysisStatus = AnalysisStatus.Completed,
                    Authenticity = "SKIPPED",
                    QualityScore = 0,
                    Decision = "SKIPPED",
                    StructuredReport = "File too short (<5s) for reliable analysis."
                };
            }

            // ========== Phase 1: Stream Phase ==========
            var tp = new TruePeakDetector();
            var dr = new DrMeter();
            var dc = new DcOffsetDetector();
            var lufs = new LufsMeter().Init(sampleRate);
            var phase = new PhaseAnalyzer();
            var bitDepthValidator = new BitDepthValidator();
            var preEcho = new PreEchoDetector();
            var spectrogram = new SpectrogramAccumulator();
            var reservoir = new ReservoirBuffer(6);

            dr.Init(sampleRate);
            preEcho.Init(sampleRate);
            spectrogram.Init(sampleRate, duration);

            double rmsSumSq = 0;
            long rmsCount = 0;

            await foreach (var chunk in AudioDecoder.StreamChunks(fileInfo.FilePath, 10, ct))
            {
                if (chunk.IsLast) break;

                if (chunk.IsStereo)
                {
                    tp.AddChunk(chunk.Left.Span, chunk.Right.Span);
                    phase.AddChunk(chunk.Left.Span, chunk.Right.Span);
                    dr.AddChunk(chunk.Left.Span, chunk.Right.Span);
                    dc.AddChunk(chunk.Left.Span, chunk.Right.Span);
                    lufs.AddChunk(chunk.Left.Span, chunk.Right.Span);
                    for (int i = 0; i < chunk.FrameCount; i++)
                    {
                        double l = chunk.Left.Span[i], r = chunk.Right.Span[i];
                        rmsSumSq += (l * l + r * r) * 0.5;
                    }
                    rmsCount += chunk.FrameCount;
                }
                else
                {
                    tp.AddChunk(chunk.Left.Span);
                    phase.AddChunk(chunk.Left.Span);
                    dr.AddChunk(chunk.Left.Span);
                    dc.AddChunk(chunk.Left.Span);
                    lufs.AddChunk(chunk.Left.Span);
                    for (int i = 0; i < chunk.FrameCount; i++)
                    {
                        double s = chunk.Left.Span[i];
                        rmsSumSq += (double)s * s;
                    }
                    rmsCount += chunk.FrameCount;
                }
                bitDepthValidator.AddChunk(chunk.Left.Span);
                preEcho.AddChunk(chunk.Left.Span);
                spectrogram.AddChunk(chunk);
                reservoir.AddChunk(chunk);
            }

            if (ct.IsCancellationRequested) return Cancelled(result);

            var tpResult = tp.GetResult();
            var lufsResult = lufs.GetResult();
            var drResult = dr.GetResult();
            var dcResult = dc.GetResult();
            var bitResult = bitDepthValidator.GetResult(bitDepth);

            var (lsbZero, lsbConstant) = bitDepthValidator.CheckLsbFlags(bitDepth);
            if (lsbZero || lsbConstant)
            {
                int effectiveBits = bitResult.EffectiveBitDepth;
                bitResult = new BitDepthResult(
                    true,
                    lsbZero
                        ? $"Claimed {bitDepth}-bit but lower 8 bits are zero-padded (effective {effectiveBits}-bit)."
                        : $"Claimed {bitDepth}-bit but lower 8 bits have constant dither pattern (naive upscale).",
                    bitResult.NoiseFloorDb, true, effectiveBits);
            }

            var (hasPreEcho, preEchoCount) = preEcho.GetResult();
            double overallRmsDb = rmsCount > 0
                ? Math.Round(20.0 * Math.Log10(Math.Max(Math.Sqrt(rmsSumSq / rmsCount), 1e-10)), 1)
                : -200;

            // ========== Phase 2: Post-Stream ==========
            var spectroData = spectrogram.Finalize();
            var reservoirChunks = reservoir.SelectedChunks;
            var mono = ConcatReservoir(reservoirChunks);

            var (cutoffHz, cutoffSlope, spectrum) = _cutoff.DetectFull(mono, sampleRate);

            var vinylResult = _vinyl.Detect(spectrum, sampleRate, mono);
            var (encoderMatch, shelfType) = _cutoff.ClassifyCutoff(cutoffHz, cutoffSlope, sampleRate, vinylResult.IsVinylRip);

            long totalSamples = (long)Math.Round(duration * sampleRate);
            var containerResult = _container.Analyze(fileInfo.FilePath, mono, sampleRate, totalSamples);
            bool isFakeHiRes = _cutoff.IsFakeHiRes(cutoffHz, shelfType, sampleRate);

            var (bandwidth, detectedType) = CutoffDetector.ClassifyBandwidth(
                cutoffHz, shelfType, sampleRate, false, "None",
                false, 0, bitResult.LsbZeroPadded,
                bitResult.EffectiveBitDepth, bitDepth, containerResult.IsCdAligned,
                containerResult.IsMqa, containerResult.IsHdcd,
                tpResult.ClippingPercent > 0, encoderMatch);

            var artifactResult = _artifacts.Detect(mono, sampleRate, cutoffHz);
            var hasArtifacts = artifactResult.hasArtifacts;
            var artifactLevel = artifactResult.level;
            var artifactType = artifactResult.artifactType;

            var sbrResult = _artifacts.DetectSbr(spectrum, sampleRate);
            if (sbrResult.hasSbr)
            {
                if (!artifactType.Contains("SBR"))
                    artifactType = string.IsNullOrEmpty(artifactType) || artifactType == "None" ? "AAC SBR" : artifactType + "+SBR";
                if (artifactLevel == "None") { artifactLevel = "Weak"; hasArtifacts = true; }
            }

            var hasSpectralHoles = _artifacts.DetectSpectralHoles(spectrum, sampleRate / 2.0, cutoffHz);
            var hasCodecSilence = CutoffDetector.HasAbsoluteSilence(spectrum, cutoffHz, sampleRate);
            var hasAbruptEdges = _artifacts.DetectAbruptEdges(mono, sampleRate);

            var (isUpscaleDetected, upscaleVerdict, maxHfDb) = _upscale.Detect(spectrum, sampleRate);
            bool isUpscale = isUpscaleDetected || isFakeHiRes
                || (sampleRate == 48000 && shelfType == "Brickwall" && cutoffHz >= 21500 && cutoffHz <= 23000);

            var resamplingResult = _resampling.DetectFromSpectrum(spectrum, sampleRate);

            var phaseResult = phase.GetResult();

            if (ct.IsCancellationRequested) return Cancelled(result);

            // ========== Phase 3: Scoring ==========
            bool isFakeStereo = channels != 2 ? false : phaseResult.Correlation > 0.99;

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
                HasCodecSilence = hasCodecSilence,
                HasAbruptEdges = hasAbruptEdges,
                SamplePeakDb = Math.Max(tpResult.SamplePeakDbL, tpResult.SamplePeakDbR),
                TruePeakDb = Math.Max(tpResult.TruePeakDbL, tpResult.TruePeakDbR),
                ClippingPercent = tpResult.ClippingPercent,
                HasIsp = tpResult.HasIsp,
                HasHardClipping = tpResult.ClippingPercent > 0,
                DynamicRange = drResult.Dr,
                OverallRmsDb = overallRmsDb,
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
                IsFakeStereo = isFakeStereo,
                IsUpscale = isUpscale,
                MaxHfDb = Math.Round(maxHfDb, 1),
                IsVinylRip = vinylResult.IsVinylRip,
                VinylRumbleRatio = vinylResult.RumbleRatio,
                VinylHfNoiseRatio = vinylResult.HfNoiseRatio,
                IsCdAligned = containerResult.IsCdAligned,
                FlacIntegrityOk = containerResult.FlacIntegrityOk,
                ContainerSource = containerResult.Source,
                IsCorrupted = containerResult.IsCorrupted,
                IsMqa = containerResult.IsMqa,
                MqaDetails = containerResult.MqaDetails,
                IsHdcd = containerResult.IsHdcd,
                PcmMd5Hex = PcmHasher.ToHexString(containerResult.PcmMd5),
                ReplayGainTrackDb = replayGainTrackDb ?? 0,
                ReplayGainMismatch = replayGainTrackDb.HasValue
                    && Math.Abs(replayGainTrackDb.Value - lufsResult.IntegratedLufs) > 3,
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
                ActualBitrate = actualBitrate,
                AverageBitrateKbps = averageBitrateKbps,
                CompressionRatio = compressionRatio,
                MinFrameBitrateKbps = minFrameBitrateKbps,
                MaxFrameBitrateKbps = maxFrameBitrateKbps,
                IsSuspiciousBitrate = (!fileInfo.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                                    && !fileInfo.FilePath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
                                    && !fileInfo.FilePath.EndsWith(".aac", StringComparison.OrdinalIgnoreCase)
                                    && averageBitrateKbps < 600 && cutoffHz < 21000 && shelfType == "Brickwall")
                                   || (bitDepth == 24 && bitResult.EffectiveBitDepth <= 16 && bitResult.LsbZeroPadded)
                                   || (compressionRatio > 0.95 && bitDepth == 16)
            };

            result = result with { ClaimedType = GetClaimedType(fileInfo.FilePath, sampleRate) };

            // Recompute ClassifyBandwidth with actual artifact data
            (bandwidth, detectedType) = CutoffDetector.ClassifyBandwidth(
                cutoffHz, shelfType, sampleRate, hasArtifacts, artifactLevel,
                hasSpectralHoles, maxHfDb, bitResult.LsbZeroPadded,
                bitResult.EffectiveBitDepth, bitDepth, containerResult.IsCdAligned,
                containerResult.IsMqa, containerResult.IsHdcd,
                tpResult.ClippingPercent > 0, encoderMatch);

            result = result with { Bandwidth = bandwidth, DetectedType = detectedType };

            // Override detectedType for native lossy formats
            string ext = Path.GetExtension(fileInfo.FilePath).ToLowerInvariant();
            string? nativeLossyAuth = null;
            string? transcodeAuth = null;
            if (ext == ".mp3")
            {
                int cutoffBr = CutoffDetector.MapCutoffToBitrate(cutoffHz, shelfType, actualBitrate, sampleRate);
                if (cutoffBr == 0) cutoffBr = actualBitrate;
                if (cutoffBr == 0 && mp3Bitrate > 0) cutoffBr = mp3Bitrate;
                double mp3Qual = ComputeMp3Quality(cutoffHz, sampleRate, mp3Bitrate > 0 ? mp3Bitrate : actualBitrate,
                    actualBitrate, artifactLevel, hasSpectralHoles);
                result = result with { DetectedType = cutoffBr > 0 ? $"MP3 {cutoffBr}" : "MP3", Mp3QualityScore = mp3Qual };
                nativeLossyAuth = "LOSSY (MP3)";
            }
            else if ((ext == ".m4a" || ext == ".aac") && isAac)
            {
                int cutoffBr = CutoffDetector.MapCutoffToBitrate(cutoffHz, shelfType, actualBitrate, sampleRate);
                if (cutoffBr == 0) cutoffBr = actualBitrate;
                if (cutoffBr == 0 && aacBitrate > 0) cutoffBr = aacBitrate;
                double aacQual = ComputeAacQuality(cutoffHz, sampleRate, aacBitrate > 0 ? aacBitrate : actualBitrate, actualBitrate, artifactLevel, hasSpectralHoles);
                result = result with { DetectedType = cutoffBr > 0 ? $"AAC {cutoffBr}" : "AAC", AacQualityScore = aacQual };
                nativeLossyAuth = "LOSSY (AAC)";
            }

            // Transcode detection: lossless container but ClassifyBandwidth
            // returned MP3/AAC detected type → upscale/transcode from lossy source.
            string? upscaleAuth = null;
            if (nativeLossyAuth == null && detectedType.StartsWith("MP3"))
                transcodeAuth = "LOSSY (TRANSCODE)";

            // Override detectedType for upscaled files
            if (sampleRate >= 88200 && result.IsUpscale)
            {
                string upscaleLabel;
                if (cutoffHz <= 17000 && shelfType == "Brickwall")
                    upscaleLabel = "UPSCALE (MP3 128→HI-RES)";
                else if (cutoffHz <= 20000 && shelfType == "Brickwall")
                    upscaleLabel = "UPSCALE (MP3 320→HI-RES)";
                else if (cutoffHz <= 23500 && shelfType == "Brickwall")
                    upscaleLabel = "UPSCALE (CD→HI-RES)";
                else if (maxHfDb < -50)
                    upscaleLabel = "UPSCALE (NO HF)";
                else
                    upscaleLabel = "UPSCALE (SUSPICIOUS)";
                result = result with { DetectedType = upscaleLabel };
                upscaleAuth = "UPSCALE";
            }
            else if (sampleRate == 48000 && cutoffHz > 21000 && cutoffHz <= 23000 && shelfType == "Brickwall" && maxHfDb < -40)
            {
                result = result with { DetectedType = "UPSCALE (44.1k→48k)" };
                upscaleAuth = "UPSCALE";
            }

            // Refine format label for M4A
            if (result.Format.StartsWith("M4A") && (detectedType.Contains("LOSSLESS") || detectedType.Contains("HI-RES")))
            {
                result = result with { Format = $"ALAC {sampleRate / 1000.0:F0}kHz/{bitDepth}bit" };
            }

            if (Path.GetExtension(fileInfo.FilePath).ToLowerInvariant() == ".m4a"
                && result.ClaimedType == "AAC"
                && (detectedType.Contains("LOSSLESS") || detectedType.Contains("HI-RES")))
            {
                result = result with { ClaimedType = "ALAC" };
            }

            // Refine UNCERTAIN labels
            if (detectedType == "UNCERTAIN")
            {
                string refined;
                if (shelfType == "Brickwall" && hasArtifacts)
                    refined = "LOSSY (TRANSCODE)";
                else if (hasCodecSilence)
                    refined = "LOSSY (TRANSCODE)";
                else if (!hasArtifacts && cutoffHz / (double)(sampleRate / 2) >= 0.90)
                    refined = "LOSSLESS (WEB)";
                else
                    refined = "UNCERTAIN";
                if (refined != "UNCERTAIN")
                {
                    result = result with { DetectedType = refined };
                    detectedType = refined;
                }
            }

            // CORRUPTED early exit
            if (containerResult.IsCorrupted)
            {
                return result with
                {
                    AnalysisStatus = AnalysisStatus.Completed,
                    AuthenticityScore = 0,
                    MasteringScore = 0,
                    Authenticity = "CORRUPTED",
                    Decision = "CORRUPTED",
                    QualityScorePercent = 0,
                    QualityScore = 0,
                    LosslessScore = 0,
                    HiResScore = 0,
                    StructuredReport = _verdict.Generate(result),
                    WhyVerdict = _verdict.GenerateWhy(result),
                    MetricsCoverage = ComputeMetricsCoverage(result)
                };
            }

            // Scoring
            var scorer = new LosslessScorer();
            double authScore = scorer.AuthenticityScore(result);
            double mastScore = scorer.MasteringScore(result);

            var (_, _, authVerdict, mastVerdict, decision) = _qualityScorer.ScoreFull(result, scorer);

            if (transcodeAuth != null || upscaleAuth != null)
            {
                decision = "REPLACE";
                authVerdict = "FALSE";
            }

            result = result with
            {
                AuthenticityScore = Math.Round(authScore, 1),
                MasteringScore = Math.Round(mastScore, 1),
                Authenticity = nativeLossyAuth ?? transcodeAuth ?? upscaleAuth ?? authVerdict,
                AuthenticityVerdict = authVerdict,
                MasteringVerdict = mastVerdict,
                LosslessScore = Math.Round(authScore, 1),
                HiResScore = Math.Round(scorer.ScoreHiRes(result), 1),
                QualityScorePercent = Math.Round(mastScore, 1),
                QualityScore = (int)Math.Round(mastScore / 10.0),
                Decision = decision,
                AnalysisStatus = AnalysisStatus.Completed,
                StructuredReport = _verdict.Generate(result),
                WhyVerdict = _verdict.GenerateWhy(result),
                MetricsCoverage = ComputeMetricsCoverage(result)
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
        if (r.IntegratedLufs <= -5 && r.IntegratedLufs > -70) passed++;
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
        double expectedCutoff = bitrate switch
        {
            >= 320 => 20500,
            >= 256 => 19000,
            >= 192 => 18000,
            >= 128 => 16000,
            _ => 16000
        };

        // Transcode mismatch: cutoff far below expected → clear transcode, score 0
        if (cutoffHz < expectedCutoff * 0.7) return 0;

        // Graded scoring: tiered by cutoff quality
        double score;
        if (cutoffHz >= 20000 && (bitrate >= 320 || bitrate == 0))
            score = 100;
        else if (cutoffHz >= 19000 && bitrate >= 256)
            score = 90;
        else if (cutoffHz >= 18500 && bitrate >= 192)
            score = 80;
        else if (cutoffHz >= 16000)
            score = 50;
        else
            score = 20;

        // Bitrate ratio check (suspicious if different from actual)
        if (actualBitrate > 0 && bitrate > 0)
        {
            double ratio = (double)bitrate / actualBitrate;
            if (ratio > 2.5) score -= 30;
            else if (ratio > 1.5) score -= 15;
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
        double expectedCutoff = bitrate switch
        {
            >= 256 => 20000,
            >= 192 => 18000,
            >= 128 => 16000,
            _ => 15000
        };

        // Transcode mismatch: cutoff far below expected → clear transcode, score 0
        if (cutoffHz < expectedCutoff * 0.7) return 0;

        // Graded scoring: tiered by cutoff quality
        double score;
        if (cutoffHz >= 20000 && (bitrate >= 256 || bitrate == 0))
            score = 100;
        else if (cutoffHz >= 18500 && bitrate >= 192)
            score = 80;
        else if (cutoffHz >= 16000)
            score = 50;
        else
            score = 20;

        // Bitrate ratio check
        if (actualBitrate > 0 && bitrate > 0)
        {
            double ratio = (double)bitrate / actualBitrate;
            if (ratio > 2.5) score -= 30;
            else if (ratio > 1.5) score -= 15;
        }

        if (artifactLevel == "Strong") score -= 25;
        else if (artifactLevel == "Medium") score -= 12;
        else if (artifactLevel == "Weak") score -= 5;

        if (hasSpectralHoles) score -= 15;

        return Math.Max(0, Math.Min(100, score));
    }

    private static (double minKbps, double maxKbps) AnalyzeFlacFrameBitrates(
        string filePath, long audioDataOffset, long fileSize, int sampleRate)
    {
        try
        {
            using var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
            var buf = new byte[16384];
            fs.Seek(audioDataOffset, System.IO.SeekOrigin.Begin);

            var frameOffsets = new List<int>();
            int bytesRead;
            int searchOffset = 0;
            const int minFrameSize = 16;
            const int maxFrameSize = 65535;

            while ((bytesRead = fs.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < bytesRead - 1; i++)
                {
                    int sync = (buf[i] << 8) | buf[i + 1];
                    if ((sync & 0xFFFE) == 0xFFF8)
                    {
                        int offset = searchOffset + i;
                        if (frameOffsets.Count == 0 || offset - frameOffsets[^1] >= minFrameSize)
                            frameOffsets.Add(offset);
                    }
                }
                searchOffset += bytesRead;
            }

            if (frameOffsets.Count < 2)
                return (0, 0);

            var bitrates = new List<double>();
            for (int i = 0; i < frameOffsets.Count - 1; i++)
            {
                int frameSize = frameOffsets[i + 1] - frameOffsets[i];
                if (frameSize >= minFrameSize && frameSize < maxFrameSize)
                {
                    double kbps = frameSize * 8.0 * sampleRate / 4096.0 / 1000.0;
                    if (kbps > 1.0)
                        bitrates.Add(kbps);
                }
            }

            if (bitrates.Count == 0)
                return (0, 0);

            return (Math.Round(bitrates.Min(), 1), Math.Round(bitrates.Max(), 1));
        }
        catch
        {
            return (0, 0);
        }
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

    private static double GetFileDuration(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        using NAudio.Wave.WaveStream reader = ext switch
        {
            ".mp3" => new NAudio.Wave.Mp3FileReader(filePath),
            ".wav" => new NAudio.Wave.WaveFileReader(filePath),
            ".flac" => new NAudio.Wave.AudioFileReader(filePath),
            ".m4a" or ".alac" => new NAudio.Wave.AudioFileReader(filePath),
            _ => throw new InvalidOperationException("Unsupported audio format for duration reading")
        };
        return reader.TotalTime.TotalSeconds;
    }

    private static float[] ConcatReservoir(IReadOnlyList<float[]> chunks)
    {
        if (chunks.Count == 0) return Array.Empty<float>();
        int total = 0;
        foreach (var c in chunks) total += c.Length;
        var result = new float[total];
        int offset = 0;
        foreach (var c in chunks) { Array.Copy(c, 0, result, offset, c.Length); offset += c.Length; }
        return result;
    }
}
