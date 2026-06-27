# Task 4: Bandwidth Mapping + Detected Type Logic

**Files:**
- Modify: `LosslessChecker/Services/CutoffDetector.cs` — add new static method
- Modify: `LosslessChecker/Services/AudioPipeline.cs` — wire into pipeline

## Step 1: Add ClassifyBandwidth static method to CutoffDetector

Add this public static method at the end of `CutoffDetector.cs` (before the closing brace of the class):

```csharp
    public static (string bandwidth, string detectedType) ClassifyBandwidth(
        double cutoffHz, string shelfType, int sampleRate, bool hasArtifacts,
        string artifactLevel, bool hasSpectralHoles, double maxHfDb,
        bool lsbZeroPadded, int effectiveBitDepth, int bitDepth,
        bool isCdAligned, bool isMqa, bool isHdcd,
        bool hasHardClipping, string encoderMatch)
    {
        bool isHiRes = sampleRate >= 88200;
        var nyquist = sampleRate / 2.0;

        // 1a. Fake 24-bit
        if (lsbZeroPadded && bitDepth == 24)
            return ("Fake 24-bit", "FAKE 24bit");

        // 1b. Transcode: brickwall + artifacts in non-MP3/non-AAC container
        if (shelfType == "Brickwall" && (artifactLevel == "Strong" || artifactLevel == "Medium") && hasSpectralHoles)
        {
            if (cutoffHz <= 17000)
                return ("16kHz", "MP3 128");
            if (cutoffHz <= 19500)
                return ("18kHz", "MP3 192");
            if (cutoffHz <= 20500)
                return ("20kHz", "MP3 320");
            return ("Full Range", "UPSCALE (MP3→FLAC)");
        }

        // 2. Hi-Res
        if (isHiRes)
        {
            string bw = $"Hi-Res ({sampleRate / 1000:F0}k)";
            if (maxHfDb < -50)
                return (bw, "UPSCALE (CD→HI-RES)");
            string dt = maxHfDb >= -30
                ? $"HI-RES {sampleRate / 1000:F0}k"
                : "UNCERTAIN";
            return (bw, dt);
        }

        // 3. Lossless (no artifacts, no brickwall at encoder frequencies)
        if (shelfType == "Natural" || (artifactLevel == "None" && shelfType == "Filtered"))
        {
            string bw = cutoffHz >= nyquist * 0.92 ? "Full Range" : $"{cutoffHz / 1000:F0}kHz";
            string dt;
            if (isCdAligned && sampleRate == 44100)
                dt = "LOSSLESS (CD)";
            else if (bitDepth > 16 && !lsbZeroPadded)
                dt = "LOSSLESS 24bit";
            else
                dt = "LOSSLESS (WEB)";

            if (cutoffHz < nyquist * 0.90 && artifactLevel == "None")
                dt = "LOSSLESS (Mastered LPF)";

            return (bw, dt);
        }

        // 4. MP3 / AAC via encoder match
        if (encoderMatch.StartsWith("MP3"))
        {
            string bw = cutoffHz switch
            {
                <= 17000 => "16kHz",
                <= 19500 => "18kHz",
                <= 20500 => "20kHz",
                _ => "Full Range"
            };
            string dt = cutoffHz switch
            {
                <= 17000 => "MP3 128",
                <= 18500 => "MP3 192",
                <= 20000 => "MP3 256",
                <= 20500 => "MP3 320",
                _ => "MP3"
            };
            return (bw, dt);
        }

        if (encoderMatch is "AAC 256" or "AAC 128")
        {
            string bw = cutoffHz >= 19500 ? "Full Range" : $"{cutoffHz / 1000:F0}kHz";
            return (bw, encoderMatch);
        }

        // 5. UNCERTAIN fallback
        return ($"{cutoffHz / 1000:F0}kHz", "UNCERTAIN");
    }
```

## Step 2: Wire into AudioPipeline

In `LosslessChecker/Services/AudioPipeline.cs`, at line 20, after `private readonly VinylDetector _vinyl = new();`, add the private helper:

```csharp
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
```

Then, in the `Analyze()` method, after all detectors complete (after line 205 `ResamplingVerdict = resamplingResult.Verdict,`), add before the result merge:

```csharp
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
```

This must go AFTER the `result = result with { ... SpectrogramDuration ... }` block (around line 216) but BEFORE the scoring section. Insert it right after the `ActualBitrate = actualBitrate` line (line 215).

## Step 3: Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All tests pass.

## Step 4: Commit

```bash
git add LosslessChecker/Services/CutoffDetector.cs LosslessChecker/Services/AudioPipeline.cs
git commit -m "feat: add bandwidth mapping and detected-type logic"
```
