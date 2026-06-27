# Task 6: Separate Scoring per Type

**Files:**
- Modify: `LosslessChecker/Services/AudioPipeline.cs`

## Steps

### Step 1: Read current file to understand scoring section

The scoring section is at lines ~232-286 in AudioPipeline.cs. You'll replace it.

### Step 2: Replace the scoring logic

Replace the entire `// Scoring per type` block. The OLD code starts with:

```csharp
            bool isMp3 = fileInfo.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
```

And continues through the `result = result with { ... AnalysisStatus = AnalysisStatus.Completed, ... }` block.

Replace ALL of it with:

```csharp
            bool isMp3 = fileInfo.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
            string detectedType = result.DetectedType;
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
```

### Step 3: Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All tests pass.

### Step 4: Commit

```bash
git add LosslessChecker/Services/AudioPipeline.cs
git commit -m "feat: separate scoring per type (MP3/AAC differ from Lossless/Hi-Res)"
```
