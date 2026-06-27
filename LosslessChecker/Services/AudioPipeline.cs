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
                    FileName = tags.Title,
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

            var buffer = AudioDecoder.Decode(fileInfo.FilePath, ct);
            double duration = buffer.Length / (double)buffer.SampleRate;
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

            // mono is the Left channel for spectral analysis
            var mono = buffer.Left;

            var (cutoffHz, cutoffSlope, spectrum) = _cutoff.DetectFull(mono, sampleRate);
            var (encoderMatch, shelfType) = _cutoff.ClassifyCutoff(cutoffHz, cutoffSlope, sampleRate);
            bool isFakeHiRes = _cutoff.IsFakeHiRes(cutoffHz, shelfType, sampleRate);

            if (ct.IsCancellationRequested) return Cancelled(result);

            // ========== Phase 2: Parallel — all independent analyzers ==========
            // Each analyzer reads shared read-only data (buffer, mono, spectrum)
            // and creates its own FFT instances — fully thread-safe.
            (bool hasArtifacts, string artifactLevel, string artifactType) artifactResult = default;
            TruePeakResult tpResult = null!;
            LufsResult lufsResult = null!;
            DrResult drResult = null!;
            DcOffsetResult dcResult = null!;
            PhaseResult phaseResult = null!;
            BitDepthResult bitResult = null!;
            SpectrogramData spectroData = null!;
            bool upscaleDetected = false;
            string upscaleVerdict = "";
            double maxHfDb = 0;
            VinylResult vinylResult = null!;
            ContainerResult containerResult = null!;
            bool hasPreEcho = false;
            int preEchoCount = 0;
            bool hasSpectralHoles = false;
            bool hasCodecSilence = false;
            bool hasAbruptEdges = false;
            ResamplingResult resamplingResult = null!;

            Parallel.Invoke(new ParallelOptions { CancellationToken = ct },
                // buffer-based (full file scan — CPU heavy, memory-bound)
                () => tpResult = new TruePeakDetector().Analyze(buffer),
                () => lufsResult = _lufs.Analyze(buffer),
                () => drResult = new DrMeter().AnalyzeStereo(buffer),
                () => dcResult = new DcOffsetDetector().Analyze(buffer),
                () => phaseResult = new PhaseAnalyzer().Analyze(buffer),
                () => bitResult = new BitDepthValidator().ValidateStereo(buffer, bitDepth),
                () => spectroData = _spectro.Build(mono, sampleRate),

                // mono + cutoffHz-based
                () => artifactResult = _artifacts.Detect(mono, sampleRate, cutoffHz),
                () =>
                {
                    var preEcho = new PreEchoDetector();
                    preEcho.Init(sampleRate);
                    preEcho.AddChunk(mono);
                    var r = preEcho.GetResult();
                    hasPreEcho = r.hasPreEcho;
                    preEchoCount = r.preEchoCount;
                },
                () => hasAbruptEdges = _artifacts.DetectAbruptEdges(mono, sampleRate),
                () => containerResult = _container.Analyze(fileInfo.FilePath, mono, sampleRate),

                // spectrum-based (depends on Phase 1 cutoff)
                () => { var r = _upscale.Detect(spectrum, sampleRate); upscaleDetected = r.isUpscale; upscaleVerdict = r.verdict; maxHfDb = r.maxHfDb; },
                () => vinylResult = _vinyl.Detect(spectrum, sampleRate, mono),
                () => hasSpectralHoles = _artifacts.DetectSpectralHoles(spectrum, sampleRate / 2.0),
                () => hasCodecSilence = CutoffDetector.HasAbsoluteSilence(spectrum, cutoffHz, sampleRate),
                () => resamplingResult = _resampling.DetectFromSpectrum(spectrum, sampleRate)
            );

            if (ct.IsCancellationRequested) return Cancelled(result);

            var hasArtifacts = artifactResult.hasArtifacts;
            var artifactLevel = artifactResult.artifactLevel;
            var artifactType = artifactResult.artifactType;

            // ========== Phase 3: Sequential — lightweight aggregations ==========
            bool isFakeStereo = new FakeStereoDetector().IsFakeStereo(buffer, phaseResult.Correlation);
            bool isUpscale = upscaleDetected || isFakeHiRes
                || (sampleRate == 48000 && shelfType == "Brickwall" && cutoffHz >= 21500 && cutoffHz <= 23000);

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
                HasCodecSilence = hasCodecSilence,
                HasAbruptEdges = hasAbruptEdges,
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
                                   || (bitDepth == 24 && bitResult.EffectiveBitDepth <= 16)
                                   || (compressionRatio > 0.95 && bitDepth == 16)
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

            // Override detectedType for native lossy formats: always show format bitrate from cutoff
            string ext = Path.GetExtension(fileInfo.FilePath).ToLowerInvariant();
            if (ext == ".mp3")
            {
                int cutoffBr = CutoffDetector.MapCutoffToBitrate(cutoffHz, shelfType, actualBitrate, sampleRate);
                if (cutoffBr == 0) cutoffBr = actualBitrate;
                if (cutoffBr == 0 && mp3Bitrate > 0) cutoffBr = mp3Bitrate;
                result = result with { DetectedType = cutoffBr > 0 ? $"MP3 {cutoffBr}" : "MP3" };
            }
            else if ((ext == ".m4a" || ext == ".aac") && isAac)
            {
                int cutoffBr = CutoffDetector.MapCutoffToBitrate(cutoffHz, shelfType, actualBitrate, sampleRate);
                if (cutoffBr == 0) cutoffBr = actualBitrate;
                if (cutoffBr == 0 && aacBitrate > 0) cutoffBr = aacBitrate;
                result = result with { DetectedType = cutoffBr > 0 ? $"AAC {cutoffBr}" : "AAC" };
            }

            // Override detectedType for upscaled files: show source→destination instead of raw format
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
            }
            // Detect 44.1k→48k upscale: brickwall at ~22-24k on 48k sample rate
            else if (sampleRate == 48000 && cutoffHz > 21000 && cutoffHz <= 23000 && shelfType == "Brickwall" && maxHfDb < -40)
            {
                result = result with { DetectedType = "UPSCALE (44.1k→48k)" };
            }

            // Refine format label for M4A: if codec detection failed but audio is lossless → ALAC
            if (result.Format.StartsWith("M4A") && (detectedType.Contains("LOSSLESS") || detectedType.Contains("HI-RES")))
            {
                result = result with
                {
                    Format = $"ALAC {sampleRate / 1000.0:F0}kHz/{bitDepth}bit"
                };
            }

            // Refine ClaimedType for M4A: if detection fell back to AAC but audio is lossless → ALAC
            if (Path.GetExtension(fileInfo.FilePath).ToLowerInvariant() == ".m4a"
                && result.ClaimedType == "AAC"
                && (detectedType.Contains("LOSSLESS") || detectedType.Contains("HI-RES")))
            {
                result = result with { ClaimedType = "ALAC" };
            }

            // Refine remaining UNCERTAIN labels using additional evidence
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
                    refined = "UNCERTAIN"; // genuinely ambiguous, keep it
                if (refined != "UNCERTAIN")
                {
                    result = result with { DetectedType = refined };
                    detectedType = refined;
                }
            }

            bool isMp3 = fileInfo.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
            bool isMp3Detected = detectedType.StartsWith("MP3") || isMp3
                || detectedType == "LOSSY (TRANSCODE)"
                || detectedType == "UPSCALE (MP3->FLAC)";
            bool isAacDetected = detectedType.StartsWith("AAC") || isAac;

            string fileExt = Path.GetExtension(fileInfo.FilePath).ToLowerInvariant();
            bool isTranscode = (isMp3Detected && fileExt != ".mp3") || (isAacDetected && fileExt != ".m4a" && fileExt != ".aac");

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

            if (result.IsMqa)
            {
                result = result with { Authenticity = "MQA" };
            }
            else if (result.IsHdcd)
            {
                result = result with { Authenticity = _losslessScorer.Classify(result) };
            }
            else if (isTranscode)
            {
                result = result with
                {
                    Authenticity = isMp3Detected ? "TRANSCODE (MP3→FLAC)" : "TRANSCODE (AAC→FLAC)"
                };
            }
            else if (isMp3)
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

            var losslessScore = result.IsMqa ? 100.0
                : isTranscode ? 0.0
                : isMp3Detected ? mp3QualityScore
                : isAacDetected ? aacQualityScore
                : _losslessScorer.Score(result);
            var hiResScore = _losslessScorer.ScoreHiRes(result);

            double qualityPercent;
            string decision;

            if (result.IsMqa)
            {
                qualityPercent = 100;
                decision = "MQA (needs decoder)";
            }
            else if (isTranscode)
            {
                decision = "REPLACE";
                qualityPercent = 0;
            }
            else if (isMp3Detected)
            {
                var (masterScore, _) = _qualityScorer.Score(result);
                qualityPercent = mp3QualityScore * 0.6 + masterScore * 0.4;
                decision = mp3QualityScore >= 70 ? "KEEP"
                    : mp3QualityScore >= 40 ? "INVESTIGATE"
                    : "REPLACE";
            }
            else if (isAacDetected)
            {
                var (masterScore, _) = _qualityScorer.Score(result);
                qualityPercent = aacQualityScore * 0.6 + masterScore * 0.4;
                decision = aacQualityScore >= 70 ? "KEEP"
                    : aacQualityScore >= 40 ? "INVESTIGATE"
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
            >= 128 => 16500,
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

            var frameSizes = new List<int>();
            int bytesRead;
            int searchOffset = 0;

            while ((bytesRead = fs.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < bytesRead - 1; i++)
                {
                    int sync = (buf[i] << 8) | buf[i + 1];
                    if ((sync & 0xFFFE) == 0xFFF8)
                    {
                        frameSizes.Add(searchOffset + i);
                    }
                }
                searchOffset += bytesRead;
            }

            if (frameSizes.Count < 2)
                return (0, 0);

            var bitrates = new List<double>();
            for (int i = 0; i < frameSizes.Count - 1; i++)
            {
                int frameSize = frameSizes[i + 1] - frameSizes[i];
                if (frameSize > 0 && frameSize < 65536)
                {
                    double kbps = frameSize * 8.0 * sampleRate / 4096.0 / 1000.0;
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
}
