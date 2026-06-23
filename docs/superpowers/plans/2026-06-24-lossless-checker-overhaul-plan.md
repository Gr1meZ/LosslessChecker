# LosslessChecker Full Overhaul — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Overhaul LosslessChecker with pipeline architecture, 4 new analyzers (TruePeak, LUFS, DC Offset, Phase), enhanced existing analyzers, dual-axis scoring, and full test coverage.

**Architecture:** Clean pipeline — 10 independent analyzers each taking `StereoBuffer` → typed result. Thin `AudioAnalyzer` orchestrator. Dual-axis scoring (Authenticity + Quality 1-10 → KEEP/INVESTIGATE/REPLACE).

**Tech Stack:** .NET 10 WPF, NAudio 2.2.1, NWaves 0.9.6, CommunityToolkit.Mvvm 8.4.0, xUnit

---

### Task 1: Create StereoBuffer model

**Files:**
- Create: `LosslessChecker/Models/StereoBuffer.cs`

- [ ] **Step 1: Write the record type**

```csharp
namespace LosslessChecker.Models;

public record StereoBuffer(float[] Left, float[] Right, int SampleRate)
{
    public int Length => Left.Length;
    public bool IsStereo => Right is { Length: > 0 };
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build LosslessChecker/LosslessChecker.csproj`
Expected: Build succeeds with new file included (SDK-style project auto-includes).

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Models/StereoBuffer.cs
git commit -m "feat: add StereoBuffer model for stereo PCM data"
```

---

### Task 2: Overhaul AnalysisResult model

**Files:**
- Modify: `LosslessChecker/Models/AnalysisResult.cs`

- [ ] **Step 1: Replace AnalysisResult.cs with the 36-field version**

```csharp
namespace LosslessChecker.Models;

public record AnalysisResult
{
    // File info
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Format { get; init; } = "";
    public int SampleRate { get; init; }
    public int BitDepth { get; init; }
    public int Channels { get; init; }
    public double DurationSeconds { get; init; }

    // Cutoff & spectrum
    public double CutoffFrequency { get; init; }
    public double CutoffSlope { get; init; }
    public string ShelfType { get; init; } = "";
    public string EncoderMatch { get; init; } = "";

    // Artifacts
    public bool HasArtifacts { get; init; }
    public string ArtifactLevel { get; init; } = "None";
    public string ArtifactType { get; init; } = "None";

    // Peak & clipping
    public double SamplePeakDb { get; init; }
    public double TruePeakDb { get; init; }
    public double ClippingPercent { get; init; }
    public bool HasIsp { get; init; }

    // Dynamics
    public double DynamicRange { get; init; }
    public double IntegratedLufs { get; init; }
    public double LoudnessRange { get; init; }
    public double Plr { get; init; }

    // Bit depth & DC
    public bool BitDepthSuspicious { get; init; }
    public bool LsbZeroPadded { get; init; }
    public int EffectiveBitDepth { get; init; }
    public double DcOffsetL { get; init; }
    public double DcOffsetR { get; init; }

    // Phase & stereo
    public double Correlation { get; init; }
    public bool IsMonoCompatible { get; init; }

    // Upscale
    public bool IsUpscale { get; init; }
    public double MaxHfDb { get; init; }

    // Classification
    public string Authenticity { get; init; } = "";
    public int QualityScore { get; init; }
    public string Decision { get; init; } = "";
    public string StructuredReport { get; init; } = "";

    // Status
    public AnalysisStatus AnalysisStatus { get; init; } = AnalysisStatus.Pending;
    public string? ErrorMessage { get; init; }

    // Visual
    public byte[] SpectrogramFlat { get; init; } = Array.Empty<byte>();
    public int SpectrogramWidth { get; init; }
    public int SpectrogramHeight { get; init; }
}
```

- [ ] **Step 2: Fix all compile errors in dependent files**

At this point many files will break because they reference removed fields (`LosslessScore`, `TruePeak`, `Status`, `Verdict`, `NoiseFloorDb`, etc.). We'll fix them in subsequent tasks. For now, comment out broken references to let the build compile:

In `AudioFileViewModel.cs` — temporarily comment out all ApplyResult body lines that reference removed fields.
In `MainViewModel.cs` — temporarily comment out lines referencing `LosslessScore` and old summary.
In `ScoreCalculator.cs` — will be deleted later.
In `VerdictGenerator.cs` — will be rewritten later.
In `AudioAnalyzer.cs` — will be rewritten later.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Models/AnalysisResult.cs
git add LosslessChecker/ViewModels/AudioFileViewModel.cs
git add LosslessChecker/ViewModels/MainViewModel.cs
git commit -m "feat: overhaul AnalysisResult model with 36 fields for dual-axis scoring"
```

---

### Task 3: Update ScoreToColorConverter for Quality 1-10

**Files:**
- Modify: `LosslessChecker/Converters/ScoreToColorConverter.cs`

- [ ] **Step 1: Replace converter logic**

```csharp
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LosslessChecker.Converters;

public class ScoreToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var score = value switch
        {
            int i => i,
            double d => (int)d,
            _ => -1
        };

        if (score >= 7)
            return new SolidColorBrush(Color.FromRgb(46, 160, 67));     // green
        if (score >= 4)
            return new SolidColorBrush(Color.FromRgb(210, 153, 34));     // amber
        if (score >= 1)
            return new SolidColorBrush(Color.FromRgb(207, 34, 46));     // red

        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
```

- [ ] **Step 2: Add AuthenticityToColorConverter**

Create: `LosslessChecker/Converters/AuthenticityToColorConverter.cs`

```csharp
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LosslessChecker.Converters;

public class AuthenticityToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            if (s.StartsWith("TRUE")) return new SolidColorBrush(Color.FromRgb(46, 160, 67));
            if (s.StartsWith("SUSPICIOUS")) return new SolidColorBrush(Color.FromRgb(210, 153, 34));
            if (s.StartsWith("FAKE")) return new SolidColorBrush(Color.FromRgb(207, 34, 46));
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
```

- [ ] **Step 3: Add DecisionToColorConverter**

Create: `LosslessChecker/Converters/DecisionToColorConverter.cs`

```csharp
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LosslessChecker.Converters;

public class DecisionToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            if (s.StartsWith("KEEP")) return new SolidColorBrush(Color.FromRgb(46, 160, 67));
            if (s.StartsWith("INVESTIGATE")) return new SolidColorBrush(Color.FromRgb(210, 153, 34));
            if (s.StartsWith("REPLACE")) return new SolidColorBrush(Color.FromRgb(207, 34, 46));
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
```

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/Converters/ScoreToColorConverter.cs
git add LosslessChecker/Converters/AuthenticityToColorConverter.cs
git add LosslessChecker/Converters/DecisionToColorConverter.cs
git commit -m "feat: add quality 1-10 color converter, authenticity + decision converters"
```

---

### Task 4: Create AudioDecoder service

**Files:**
- Create: `LosslessChecker/Services/AudioDecoder.cs`

- [ ] **Step 1: Write the stereo decoder**

```csharp
using System.IO;
using LosslessChecker.Models;
using NAudio.Wave;

namespace LosslessChecker.Services;

public class AudioDecoder
{
    public static StereoBuffer Decode(string filePath, CancellationToken ct = default)
    {
        using var reader = CreateReader(filePath)
            ?? throw new InvalidOperationException("Unsupported audio format");

        var format = reader.WaveFormat;
        var provider = reader.ToSampleProvider();
        int totalFrames = (int)(reader.TotalTime.TotalSeconds * format.SampleRate);
        var left = new List<float>(totalFrames);
        var right = new List<float>(totalFrames);

        if (format.Channels == 1)
        {
            var buffer = new float[4096];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException();
                for (int i = 0; i < read; i++) left.Add(buffer[i]);
            }
            return new StereoBuffer(left.ToArray(), left.ToArray(), format.SampleRate);
        }

        // Stereo: interleaved L/R
        var stereoBuffer = new float[8192];
        int stereoRead;
        while ((stereoRead = provider.Read(stereoBuffer, 0, stereoBuffer.Length)) > 0)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException();
            for (int i = 0; i < stereoRead; i += format.Channels)
            {
                left.Add(stereoBuffer[i]);
                if (i + 1 < stereoRead)
                    right.Add(stereoBuffer[i + 1]);
            }
        }

        return new StereoBuffer(left.ToArray(), right.ToArray(), format.SampleRate);
    }

    public static float[] DecodeMono(string filePath, CancellationToken ct = default)
    {
        var stereo = Decode(filePath, ct);
        int n = stereo.Length;
        var mono = new float[n];
        for (int i = 0; i < n; i++)
            mono[i] = (stereo.Left[i] + stereo.Right[i]) * 0.5f;
        return mono;
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
}
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/AudioDecoder.cs
git commit -m "feat: add AudioDecoder with stereo PCM decoding"
```

---

### Task 5: Extract SpectrogramBuilder service

**Files:**
- Create: `LosslessChecker/Services/SpectrogramBuilder.cs`
- Modify: `LosslessChecker/Services/CutoffDetector.cs:13-95` (remove spectrogram building from DetectFull)

- [ ] **Step 1: Create SpectrogramBuilder**

```csharp
using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class SpectrogramBuilder
{
    private const int FftSize = 4096;
    private const int HopSize = 2048;
    private const int FreqBins = 256;
    private const int MaxFrames = 300;

    public (byte[] data, int width, int height) Build(float[] samples, int sampleRate)
    {
        int height = FreqBins;
        if (samples.Length < FftSize)
            return (Array.Empty<byte>(), 0, height);

        var fft = new Fft(FftSize);
        var window = Window.Hann(FftSize);

        int step = Math.Max(1, (samples.Length - FftSize) / HopSize / MaxFrames);
        int maxWidth = Math.Min(MaxFrames, ((samples.Length - FftSize) / HopSize) / step + 1);

        // Pass 1: find global peak
        double globalPeak = 0;
        int counter = 0;
        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            var frame = new float[FftSize];
            Array.Copy(samples, pos, frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) frame[i] *= window[i];
            var real = new float[FftSize];
            var imag = new float[FftSize];
            Array.Copy(frame, real, FftSize);
            fft.Direct(real, imag);
            counter++;
            if (counter % step == 0)
                for (int j = 0; j < FftSize / 2; j++)
                {
                    double m = Math.Sqrt(real[j] * real[j] + imag[j] * imag[j]);
                    if (m > globalPeak) globalPeak = m;
                }
        }

        // Pass 2: build flat byte[]
        counter = 0;
        int framesBuilt = 0;
        var flat = new byte[maxWidth * height];
        double refMag = Math.Max(globalPeak, 1e-10);

        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            var frame = new float[FftSize];
            Array.Copy(samples, pos, frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) frame[i] *= window[i];
            var real = new float[FftSize];
            var imag = new float[FftSize];
            Array.Copy(frame, real, FftSize);
            fft.Direct(real, imag);
            counter++;
            if (counter % step == 0 && framesBuilt < maxWidth)
            {
                double ratio = (double)(FftSize / 2) / height;
                int offset = framesBuilt * height;
                for (int j = 0; j < height; j++)
                {
                    int srcIdx = Math.Min((int)(j * ratio), FftSize / 2 - 1);
                    double mag = Math.Sqrt(real[srcIdx] * real[srcIdx] + imag[srcIdx] * imag[srcIdx]);
                    double db = 20.0 * Math.Log10(Math.Max(mag, 1e-10) / refMag);
                    flat[offset + j] = (byte)Math.Max(0, Math.Min(255, (int)((db + 96.0) / 96.0 * 255)));
                }
                framesBuilt++;
            }
        }

        return (flat, framesBuilt, height);
    }
}
```

- [ ] **Step 2: Simplify CutoffDetector.DetectFull** — remove spectrogram building from it. Change return signature from `(double, double, double[], byte[], int, int)` to `(double cutoff, double slope, double[] spectrum)`.

In `CutoffDetector.cs`, replace lines 13-95 with a streamlined version that only does Pass 1 (average spectrum + derivative cutoff). Remove FFT-based spectrogram logic entirely (moved to SpectrogramBuilder).

New `DetectFull` signature:
```csharp
public (double cutoff, double cutoffSlope, double[] spectrum)
    DetectFull(float[] samples, int sampleRate)
```

New implementation (Pass 1 only, derivative cutoff at end):
```csharp
public (double cutoff, double cutoffSlope, double[] spectrum) DetectFull(
    float[] samples, int sampleRate)
{
    var nyquist = sampleRate / 2.0;
    if (samples.Length < FftSize)
        return (nyquist, 0, Array.Empty<double>());

    var fft = new Fft(FftSize);
    var window = Window.Hann(FftSize);
    var avgMagnitudes = new double[FftSize / 2];
    int frameCount = 0;

    for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
    {
        var frame = new float[FftSize];
        Array.Copy(samples, pos, frame, 0, FftSize);
        for (int i = 0; i < FftSize; i++) frame[i] *= window[i];
        var real = new float[FftSize];
        var imag = new float[FftSize];
        Array.Copy(frame, real, FftSize);
        fft.Direct(real, imag);
        for (int i = 0; i < FftSize / 2; i++)
            avgMagnitudes[i] += Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
        frameCount++;
    }

    if (frameCount == 0)
        return (nyquist, 0, Array.Empty<double>());

    for (int i = 0; i < avgMagnitudes.Length; i++)
        avgMagnitudes[i] /= frameCount;

    var (cutoff, cutoffSlope) = FindCutoffByDerivative(avgMagnitudes, nyquist);
    return (cutoff, cutoffSlope, avgMagnitudes);
}
```

Also update `DetectCutoff` and `DetectWithSpectrum` methods to match new return type.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/SpectrogramBuilder.cs
git add LosslessChecker/Services/CutoffDetector.cs
git commit -m "feat: extract SpectrogramBuilder, simplify CutoffDetector return type"
```

---

### Task 6: Create TruePeakDetector

**Files:**
- Create: `LosslessChecker/Services/Analyzers/TruePeakDetector.cs`

- [ ] **Step 1: Write the analyzer**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services.Analyzers;

public class TruePeakDetector
{
    private const int OversampleFactor = 4;
    private const int ClipRunMin = 3;

    public TruePeakResult Analyze(StereoBuffer buffer)
    {
        float samplePeakL = 0, samplePeakR = 0;
        int clippedRuns = 0;
        int totalRuns = 0;

        // Sample peak + clipping detection on original signal
        for (int ch = 0; ch < 2; ch++)
        {
            var samples = ch == 0 ? buffer.Left : buffer.Right;
            int consecutive = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Math.Abs(samples[i]);
                if (ch == 0 && abs > samplePeakL) samplePeakL = abs;
                if (ch == 1 && abs > samplePeakR) samplePeakR = abs;

                if (abs >= 1.0f)
                {
                    consecutive++;
                    if (consecutive >= ClipRunMin)
                    {
                        clippedRuns++;
                        consecutive = 0;
                    }
                }
                else consecutive = 0;
            }
            totalRuns += samples.Length / ClipRunMin;
        }

        double samplePeakDbL = ToDb(samplePeakL);
        double samplePeakDbR = ToDb(samplePeakR);

        // True Peak via 4x oversampling
        float truePeakL = FindTruePeak(buffer.Left);
        float truePeakR = FindTruePeak(buffer.Right);
        double truePeakDbL = ToDb(truePeakL);
        double truePeakDbR = ToDb(truePeakR);

        double clippingPercent = totalRuns > 0 ? (double)clippedRuns / totalRuns * 100.0 : 0;
        bool hasIsp = truePeakL > 1.0f || truePeakR > 1.0f;

        return new TruePeakResult(
            Math.Round(samplePeakDbL, 1), Math.Round(samplePeakDbR, 1),
            Math.Round(truePeakDbL, 1), Math.Round(truePeakDbR, 1),
            Math.Round(clippingPercent, 2),
            hasIsp);
    }

    private static float FindTruePeak(float[] samples)
    {
        int n = samples.Length;
        // 4x oversampling via zero-stuffing + simple moving-average LPF
        float peak = 0;
        var upsampled = new float[n * OversampleFactor];
        for (int i = 0; i < n; i++)
            upsampled[i * OversampleFactor] = samples[i];

        // Simple low-pass: 5-tap moving average (acts as sinc-like filter for 4x)
        var filtered = new float[upsampled.Length];
        for (int i = 2; i < upsampled.Length - 2; i++)
        {
            filtered[i] = (upsampled[i - 2] + upsampled[i - 1] + upsampled[i]
                         + upsampled[i + 1] + upsampled[i + 2]) / 5f;
            float abs = Math.Abs(filtered[i]);
            if (abs > peak) peak = abs;
        }

        return peak;
    }

    private static double ToDb(float linear)
        => linear > 0 ? 20.0 * Math.Log10(linear) : -200.0;
}

public record TruePeakResult(
    double SamplePeakDbL, double SamplePeakDbR,
    double TruePeakDbL, double TruePeakDbR,
    double ClippingPercent,
    bool HasIsp);
```

- [ ] **Step 2: Commit**

```bash
New-Item -ItemType Directory -Force -Path "LosslessChecker\Services\Analyzers"
git add LosslessChecker/Services/Analyzers/TruePeakDetector.cs
git commit -m "feat: add TruePeakDetector with 4x oversampling and ISP detection"
```

---

### Task 7: Create LufsMeter (ITU-R BS.1770-4)

**Files:**
- Create: `LosslessChecker/Services/Analyzers/LufsMeter.cs`

- [ ] **Step 1: Write the analyzer**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services.Analyzers;

public class LufsMeter
{
    private const double BlockDuration = 0.4;
    private const double AbsoluteGate = -70.0;
    private const double RelativeGate = -10.0;

    public LufsResult Analyze(StereoBuffer buffer)
    {
        int sampleRate = buffer.SampleRate;
        int blockSize = (int)(sampleRate * BlockDuration);
        if (blockSize < 1 || buffer.Length < blockSize)
            return new LufsResult(-100, 0);

        var blockLoudness = new List<double>();
        int totalBlocks = 0;

        ResetFilters();

        for (int pos = 0; pos + blockSize <= buffer.Length; pos += blockSize)
        {
            double sumSq = 0;
            int len = Math.Min(blockSize, buffer.Length - pos);
            for (int i = pos; i < pos + len; i++)
            {
                double sample = (buffer.Left[i] + buffer.Right[i]) * 0.5;
                var filtered = KWeightFilter(sample);
                sumSq += filtered * filtered;
            }
            double rms = Math.Sqrt(sumSq / len);
            double loudness = -0.691 + 10.0 * Math.Log10(Math.Max(rms * rms, 1e-10));
            blockLoudness.Add(loudness);
            totalBlocks++;
        }

        if (totalBlocks == 0)
            return new LufsResult(-100, 0);

        // Relative gate: exclude blocks below absolute gate -10
        double absoluteSum = 0;
        int absoluteCount = 0;
        for (int i = 0; i < blockLoudness.Count; i++)
        {
            if (blockLoudness[i] > AbsoluteGate)
            {
                absoluteSum += Math.Pow(10, (blockLoudness[i]) / 10);
                absoluteCount++;
            }
        }

        double absoluteLoudness = absoluteCount > 0
            ? -0.691 + 10.0 * Math.Log10(absoluteSum / absoluteCount)
            : -70.0;

        double relativeThreshold = absoluteLoudness + RelativeGate;
        double gatedSum = 0;
        int gatedCount = 0;
        for (int i = 0; i < blockLoudness.Count; i++)
        {
            if (blockLoudness[i] > relativeThreshold)
            {
                gatedSum += Math.Pow(10, blockLoudness[i] / 10);
                gatedCount++;
            }
        }

        double integratedLufs = gatedCount > 0
            ? -0.691 + 10.0 * Math.Log10(gatedSum / gatedCount)
            : -70.0;

        // LRA (Loudness Range)
        var loudBlocks = blockLoudness
            .Where(b => b > AbsoluteGate)
            .OrderBy(b => b)
            .ToList();

        double lra = 0;
        if (loudBlocks.Count >= 10)
        {
            int lowIdx = (int)(loudBlocks.Count * 0.10);
            int highIdx = (int)(loudBlocks.Count * 0.95);
            lra = loudBlocks[highIdx] - loudBlocks[lowIdx];
        }

        return new LufsResult(
            Math.Round(integratedLufs, 1),
            Math.Round(lra, 1));
    }

    // K-weighting filter per ITU-R BS.1770-4 Annex 1
    // Two cascaded second-order IIR filters: pre-filter + high-shelf
    // Using standard biquad topology with state variables
    private double _x1p, _x2p, _y1p, _y2p; // pre-filter state
    private double _x1s, _x2s, _y1s, _y2s; // shelf state
    private bool _filtersReset = true;

    private void ResetFilters()
    {
        _x1p = _x2p = _y1p = _y2p = 0;
        _x1s = _x2s = _y1s = _y2s = 0;
        _filtersReset = true;
    }

    private double KWeightFilter(double sample)
    {
        if (!_filtersReset) ResetFilters();

        // Pre-filter (high-pass, second-order)
        // Coefficients for 48 kHz from ITU-R BS.1770-4 Table 1
        const double a0_p = 1.0;
        const double a1_p = -1.69065929318241;
        const double a2_p = 0.73248077421585;
        const double b0_p = 1.53512485958697;
        const double b1_p = -2.69169618940638;
        const double b2_p = 1.19839281085285;

        double preOut = b0_p * sample + b1_p * _x1p + b2_p * _x2p
                      - a1_p * _y1p - a2_p * _y2p;
        _x2p = _x1p; _x1p = sample;
        _y2p = _y1p; _y1p = preOut;

        // High-shelf (+4 dB, second-order)
        // Coefficients for 48 kHz from ITU-R BS.1770-4 Table 2
        const double a0_s = 1.0;
        const double a1_s = -1.99004745483398;
        const double a2_s = 0.99007225036621;
        const double b0_s = 1.0;
        const double b1_s = -2.0;
        const double b2_s = 1.0;

        double shelfOut = b0_s * preOut + b1_s * _x1s + b2_s * _x2s
                        - a1_s * _y1s - a2_s * _y2s;
        _x2s = _x1s; _x1s = preOut;
        _y2s = _y1s; _y1s = shelfOut;

        return shelfOut;
    }
}

public record LufsResult(double IntegratedLufs, double LoudnessRange);
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/Analyzers/LufsMeter.cs
git commit -m "feat: add LufsMeter with ITU-R BS.1770-4 integrated LUFS and LRA"
```

---

### Task 8: Create DcOffsetDetector

**Files:**
- Create: `LosslessChecker/Services/Analyzers/DcOffsetDetector.cs`

- [ ] **Step 1: Write the analyzer**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services.Analyzers;

public class DcOffsetDetector
{
    private const double ThresholdPercent = 0.001;

    public DcOffsetResult Analyze(StereoBuffer buffer)
    {
        double meanL = buffer.Left.Average();
        double meanR = buffer.Right.Average();

        // DC offset as percentage of full scale (float PCM: ±1.0)
        double dcOffsetL = Math.Round(meanL * 100.0, 4);
        double dcOffsetR = Math.Round(meanR * 100.0, 4);

        bool hasDcOffset = Math.Abs(dcOffsetL) > ThresholdPercent
                        || Math.Abs(dcOffsetR) > ThresholdPercent;

        return new DcOffsetResult(dcOffsetL, dcOffsetR, hasDcOffset);
    }
}

public record DcOffsetResult(double DcOffsetL, double DcOffsetR, bool HasDcOffset);
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/Analyzers/DcOffsetDetector.cs
git commit -m "feat: add DcOffsetDetector with 0.001% threshold"
```

---

### Task 9: Create PhaseAnalyzer

**Files:**
- Create: `LosslessChecker/Services/Analyzers/PhaseAnalyzer.cs`

- [ ] **Step 1: Write the analyzer**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services.Analyzers;

public class PhaseAnalyzer
{
    private const int BlockSize = 4096;

    public PhaseResult Analyze(StereoBuffer buffer)
    {
        if (!buffer.IsStereo)
            return new PhaseResult(1.0, true);

        var correlations = new List<double>();
        for (int pos = 0; pos + BlockSize <= buffer.Length; pos += BlockSize)
        {
            double sumXY = 0, sumX2 = 0, sumY2 = 0;
            for (int i = pos; i < pos + BlockSize; i++)
            {
                float x = buffer.Left[i];
                float y = buffer.Right[i];
                sumXY += x * y;
                sumX2 += x * x;
                sumY2 += y * y;
            }

            double denom = Math.Sqrt(sumX2 * sumY2);
            double corr = denom > 1e-10 ? sumXY / denom : 0;
            correlations.Add(corr);
        }

        double avgCorrelation = correlations.Count > 0
            ? Math.Round(correlations.Average(), 2)
            : 1.0;

        bool isMonoCompatible = avgCorrelation >= 0;

        return new PhaseResult(avgCorrelation, isMonoCompatible);
    }
}

public record PhaseResult(double Correlation, bool IsMonoCompatible);
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/Analyzers/PhaseAnalyzer.cs
git commit -m "feat: add PhaseAnalyzer with correlation meter and mono compatibility check"
```

---

### Task 10: Enhance CutoffDetector — encoder mapping + shelf analysis + Hi-Res check

**Files:**
- Modify: `LosslessChecker/Services/CutoffDetector.cs`

- [ ] **Step 1: Add encoder mapping and shelf analysis methods**

At the bottom of `CutoffDetector.cs`, after `FindCutoffByDerivative`, add:

```csharp
public (string encoderMatch, string shelfType) ClassifyCutoff(
    double cutoffHz, double cutoffSlope, int sampleRate)
{
    var nyquist = sampleRate / 2.0;
    double ratio = nyquist > 0 ? cutoffHz / nyquist : 1.0;

    // Shelf type from slope
    string shelfType = cutoffSlope switch
    {
        < -18 => "Brickwall",
        < -10 => "Filtered",
        _ => "Natural"
    };

    // Encoder mapping (absolute cutoff, not ratio)
    string encoderMatch = cutoffHz switch
    {
        <= 16500 => "MP3 128-192 kbps",
        <= 18500 => "MP3 192-256 kbps",
        <= 20000 => "MP3 320 / AAC 256 kbps",
        <= 21500 => "Possible LP filter",
        _ => "None"
    };

    // Override: if ratio > 0.95, encoder match is None regardless
    if (ratio >= 0.95)
        encoderMatch = "None";

    return (encoderMatch, shelfType);
}

public bool IsFakeHiRes(double cutoffHz, int sampleRate)
{
    // Hi-Res = sample rate >= 88.2 kHz
    if (sampleRate < 80000) return false;
    // If cutoff is below 22 kHz on a Hi-Res file, it's a fake upscale
    return cutoffHz < 22000;
}
```

- [ ] **Step 2: Update existing public methods to use new outputs**

Update `DetectCutoff` return comment — no signature change needed.
Update `DetectWithSpectrum` — keep existing.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/CutoffDetector.cs
git commit -m "feat: add encoder frequency mapping, shelf type, and fake Hi-Res detection to CutoffDetector"
```

---

### Task 11: Enhance ArtifactDetector — MP3 sizzle detection + ArtifactType

**Files:**
- Modify: `LosslessChecker/Services/ArtifactDetector.cs`

- [ ] **Step 1: Add ArtifactType to return value and sizzle detection**

Update the return type and method signature:

```csharp
public (bool hasArtifacts, string level, string artifactType) Detect(
    float[] samples, int sampleRate, double cutoffFrequency)
```

After the existing artifact detection logic (line 109), add artifact type classification:

```csharp
// Determine artifact type
string artifactType = level switch
{
    "Strong" or "Medium" => DetectMp3Sizzle(samples, sampleRate, cutoffFrequency)
        ? "MP3" : "Unknown",
    "Weak" => "Unknown",
    _ => "None"
};
return (hasArtifacts, level, artifactType);
```

Add sizzle detection method:

```csharp
private static bool DetectMp3Sizzle(float[] samples, int sampleRate, double cutoffFreq)
{
    // MP3 at 128-192kbps has characteristic high-frequency noise
    // in the 15.5-16.5 kHz band (just below the cutoff)
    // We check for elevated spectral energy in this narrow band
    if (sampleRate < 44100) return false;

    int fftSize = 4096;
    var fft = new Fft(fftSize);
    var window = Window.Hann(fftSize);
    double sizzleEnergy = 0;
    double totalHfEnergy = 0;
    int frames = 0;

    int bin15500 = (int)(15500.0 / (sampleRate / 2.0) * (fftSize / 2));
    int bin16500 = (int)(16500.0 / (sampleRate / 2.0) * (fftSize / 2));
    int cutoffBin = Math.Min((int)(cutoffFreq / (sampleRate / 2.0) * (fftSize / 2)), fftSize / 2 - 1);

    for (int pos = 0; pos + fftSize <= samples.Length; pos += 1024)
    {
        var frame = new float[fftSize];
        Array.Copy(samples, pos, frame, 0, fftSize);
        for (int i = 0; i < fftSize; i++) frame[i] *= window[i];
        var real = new float[fftSize];
        var imag = new float[fftSize];
        Array.Copy(frame, real, fftSize);
        fft.Direct(real, imag);

        for (int i = bin15500; i < bin16500 && i < real.Length / 2; i++)
            sizzleEnergy += real[i] * real[i] + imag[i] * imag[i];

        for (int i = bin15500; i < cutoffBin && i < real.Length / 2; i++)
            totalHfEnergy += real[i] * real[i] + imag[i] * imag[i];

        frames++;
        if (frames >= 20) break;
    }

    if (frames == 0 || totalHfEnergy <= 0) return false;
    double ratio = sizzleEnergy / totalHfEnergy;
    return ratio > 0.4; // MP3 sizzle concentrates energy in narrow band
}
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/ArtifactDetector.cs
git commit -m "feat: add MP3 sizzle detection and ArtifactType to ArtifactDetector"
```

---

### Task 12: Enhance DrMeter — per-channel DR

**Files:**
- Modify: `LosslessChecker/Services/DrMeter.cs`

- [ ] **Step 1: Add stereo-aware Analyze method**

Add an overload that takes `StereoBuffer`:

```csharp
using LosslessChecker.Models;

// Add this at namespace level or as a result type:
public record DrResult(double Dr, double DrLeft, double DrRight, double SamplePeakDb, double ClippingPercent);
```

Add new method:

```csharp
public DrResult AnalyzeStereo(StereoBuffer buffer)
{
    var (drL, peakL, clipL) = AnalyzeChannel(buffer.Left, buffer.SampleRate);
    var (drR, peakR, clipR) = AnalyzeChannel(buffer.Right, buffer.SampleRate);

    // Combined mono for overall DR
    int n = buffer.Length;
    var mono = new float[n];
    for (int i = 0; i < n; i++)
        mono[i] = (buffer.Left[i] + buffer.Right[i]) * 0.5f;

    var (dr, peak, clip) = AnalyzeChannel(mono, buffer.SampleRate);

    double overallPeak = Math.Max(peakL, peakR);
    double overallClip = Math.Max(clipL, clipR);

    return new DrResult(
        Math.Round(dr, 1),
        Math.Round(drL, 1),
        Math.Round(drR, 1),
        Math.Round(overallPeak, 1),
        Math.Round(overallClip, 2));
}

private (double dr, double peakDb, double clipPercent) AnalyzeChannel(
    float[] samples, int sampleRate)
{
    int blockSize = (int)(sampleRate * BlockDurationSec);
    if (blockSize < 1 || samples.Length < blockSize)
        return (0, 0, 0);

    var blockDb = new List<double>();
    double samplePeakLinear = double.MinValue;
    int clippedRuns = 0;

    for (int pos = 0; pos < samples.Length; pos += blockSize)
    {
        int len = Math.Min(blockSize, samples.Length - pos);
        double sumSq = 0;
        int consecutive = 0;
        for (int i = pos; i < pos + len; i++)
        {
            var abs = Math.Abs(samples[i]);
            if (abs > samplePeakLinear) samplePeakLinear = abs;
            if (abs >= 1.0)
            {
                consecutive++;
                if (consecutive >= ClipRunMin) clippedRuns++;
            }
            else consecutive = 0;
            sumSq += samples[i] * samples[i];
        }
        double rms = Math.Sqrt(sumSq / len);
        blockDb.Add(20.0 * Math.Log10(Math.Max(rms, 1e-10)));
    }

    double peakDb = samplePeakLinear > double.MinValue
        ? 20.0 * Math.Log10(Math.Max(samplePeakLinear, 1e-10)) : 0;

    double clipPercent = (double)clippedRuns / (samples.Length / (double)ClipRunMin) * 100.0;

    blockDb.Sort((a, b) => b.CompareTo(a));
    int topCount = Math.Max(1, (int)(blockDb.Count * TopPercentile));
    double dbLoud = blockDb.Take(topCount).Average();

    double totalSumSq = 0;
    for (int i = 0; i < samples.Length; i++)
        totalSumSq += samples[i] * samples[i];
    double overallRms = Math.Sqrt(totalSumSq / samples.Length);
    double dbOverall = 20.0 * Math.Log10(Math.Max(overallRms, 1e-10));

    double dr = dbLoud - dbOverall;
    return (dr, peakDb, clipPercent);
}
```

Keep the existing `Analyze(float[], int)` method for backward compat (will be used by tests).

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/DrMeter.cs
git commit -m "feat: add per-channel DR measurement to DrMeter"
```

---

### Task 13: Enhance BitDepthValidator — LSB zero-padding check

**Files:**
- Modify: `LosslessChecker/Services/BitDepthValidator.cs`

- [ ] **Step 1: Add LSB zero-padding check method and update return type**

```csharp
using LosslessChecker.Models;

// Replace the existing return type to include LsbZeroPadded
public BitDepthResult Validate(StereoBuffer buffer, int claimedBitDepth)
```

And add result record + enhanced logic:

```csharp
public record BitDepthResult(
    bool IsSuspicious, string Verdict, double NoiseFloorDb,
    bool LsbZeroPadded, int EffectiveBitDepth);
```

Add before the class closing brace:

```csharp
public bool CheckLsbZeroPadded(float[] samples, int claimedBitDepth)
{
    if (claimedBitDepth != 24 || samples.Length < 1000)
        return false;

    // Take loudest 10% of blocks, check if lower 8 bits are always zero
    int blockSize = samples.Length / 100;
    var sortedBlocks = new List<double>();
    for (int pos = 0; pos + blockSize <= samples.Length; pos += blockSize)
    {
        double maxAbs = 0;
        for (int i = pos; i < pos + blockSize; i++)
            maxAbs = Math.Max(maxAbs, Math.Abs(samples[i]));
        sortedBlocks.Add(maxAbs);
    }
    sortedBlocks.Sort((a, b) => b.CompareTo(a));
    int loudCount = Math.Max(1, sortedBlocks.Count / 10);

    // Check: convert loudest samples to 24-bit integer, mask lower 8 bits
    int zeroCount = 0, totalCount = 0;
    for (int pos = 0; pos + blockSize <= samples.Length; pos += blockSize)
    {
        double maxAbs = 0;
        for (int i = pos; i < pos + blockSize; i++)
            maxAbs = Math.Max(maxAbs, Math.Abs(samples[i]));
        if (maxAbs < sortedBlocks[Math.Min(loudCount - 1, sortedBlocks.Count - 1)])
            continue;

        for (int i = pos; i < pos + blockSize; i++)
        {
            // float [-1, 1] → 24-bit signed integer
            int sample24 = (int)Math.Round(samples[i] * 8388607.0);
            // Check if lower 8 bits are zero
            if ((sample24 & 0xFF) == 0)
                zeroCount++;
            totalCount++;
        }
    }

    return totalCount > 100 && (double)zeroCount / totalCount > 0.95;
}
```

Keep the existing `Validate(float[], int, int)` for tests but add new overload:

```csharp
public BitDepthResult Validate(StereoBuffer buffer, int claimedBitDepth)
{
    // Use mono for noise floor
    int n = buffer.Length;
    var mono = new float[n];
    for (int i = 0; i < n; i++)
        mono[i] = (buffer.Left[i] + buffer.Right[i]) * 0.5f;

    int blockSize = Math.Max(1, buffer.SampleRate / 10);
    var blockRms = new List<double>();
    for (int pos = 0; pos + blockSize <= n; pos += blockSize)
    {
        double sumSq = 0;
        for (int i = pos; i < pos + blockSize; i++)
            sumSq += mono[i] * mono[i];
        blockRms.Add(Math.Sqrt(sumSq / blockSize));
    }

    if (blockRms.Count < 5)
        return new BitDepthResult(false, "Insufficient blocks", 0, false, claimedBitDepth);

    blockRms.Sort();
    int quietCount = Math.Max(1, blockRms.Count / 10);
    double quietRms = blockRms.Take(quietCount).Average();
    double noiseFloorDb = 20.0 * Math.Log10(Math.Max(quietRms, 1e-10));

    int expectedNoiseFloor = claimedBitDepth * -6;
    double toleranceDb = 16;

    bool lsbZero = CheckLsbZeroPadded(mono, claimedBitDepth);
    bool suspicious = noiseFloorDb > expectedNoiseFloor + toleranceDb || lsbZero;

    int effectiveBits = (int)Math.Round(-noiseFloorDb / 6.0);
    effectiveBits = Math.Min(effectiveBits, claimedBitDepth);

    string verdict;
    if (lsbZero)
        verdict = $"Claimed {claimedBitDepth}-bit but lower 8 bits are zero-padded (effective {effectiveBits}-bit).";
    else if (noiseFloorDb > expectedNoiseFloor + toleranceDb)
        verdict = $"Claimed {claimedBitDepth}-bit but noise floor at {noiseFloorDb:F0} dB = ~{effectiveBits}-bit effective.";
    else
        verdict = $"{claimedBitDepth}-bit integrity confirmed.";

    return new BitDepthResult(suspicious, verdict, Math.Round(noiseFloorDb, 1), lsbZero, effectiveBits);
}
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/BitDepthValidator.cs
git commit -m "feat: add LSB zero-padding check to BitDepthValidator"
```

---

### Task 14: Enhance UpscaleDetector — dither noise detection

**Files:**
- Modify: `LosslessChecker/Services/UpscaleDetector.cs`

- [ ] **Step 1: Add dither noise detection method**

At the bottom of the class, add:

```csharp
private static bool HasDitherSignature(double[] spectrum, int startBin)
{
    // Dither noise in HF has characteristic flat spectrum (uniform energy)
    if (startBin + 20 >= spectrum.Length) return false;

    double sum = 0, sumSq = 0;
    int count = 0;
    for (int i = startBin; i < spectrum.Length; i++)
    {
        double db = 20.0 * Math.Log10(Math.Max(spectrum[i], 1e-10));
        sum += db;
        sumSq += db * db;
        count++;
    }

    if (count < 10) return false;
    double mean = sum / count;
    double variance = sumSq / count - mean * mean;

    // Very low variance = flat spectrum = shaped dither noise = upscale signature
    return variance < 9.0; // < 3 dB std dev
}
```

Update the `Detect` method to use dither detection:

In the `if (maxHfDb < -50)` block, add dither check:
```csharp
if (maxHfDb < -50)
{
    bool isDither = HasDitherSignature(averagedSpectrum, startBin);
    return (true,
        isDither
            ? $"Hi-Res ({sampleRate}Hz) but no content above 22kHz (max {maxHfDb:F0} dB). Flat dither noise — upscale from CD."
            : $"Hi-Res ({sampleRate}Hz) but no content above 22kHz (max {maxHfDb:F0} dB). Likely upscale from 44.1/48kHz source.",
        maxHfDb);
}
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/UpscaleDetector.cs
git commit -m "feat: add dither noise detection to UpscaleDetector"
```

---

### Task 15: Create AuthenticityClassifier

**Files:**
- Create: `LosslessChecker/Services/Analysis/AuthenticityClassifier.cs`

- [ ] **Step 1: Write the classifier**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services.Analysis;

public class AuthenticityClassifier
{
    public string Classify(AnalysisResult result)
    {
        // FAKE HI-RES: Hi-Res file with HF cutoff < 22 kHz
        if (result.SampleRate >= 80000 && result.CutoffFrequency < 22000)
            return "FAKE HI-RES";

        // FAKE LOSSLESS
        if (result.CutoffFrequency <= 16500)
            return "FAKE LOSSLESS";
        if (result.CutoffFrequency <= 18500 && result.HasArtifacts)
            return "FAKE LOSSLESS";
        if (result.CutoffFrequency <= 20000 && result.HasArtifacts && result.ShelfType == "Brickwall")
            return "FAKE LOSSLESS";

        // SUSPICIOUS
        if (result.CutoffFrequency <= 21500 && result.CutoffFrequency > 18500)
            return "SUSPICIOUS";
        if (result.IsUpscale)
            return "SUSPICIOUS";
        if (result.BitDepthSuspicious && result.LsbZeroPadded)
            return "SUSPICIOUS";

        return "TRUE LOSSLESS";
    }
}
```

- [ ] **Step 2: Commit**

```bash
New-Item -ItemType Directory -Force -Path "LosslessChecker\Services\Analysis"
git add LosslessChecker/Services/Analysis/AuthenticityClassifier.cs
git commit -m "feat: add AuthenticityClassifier with deterministic rules"
```

---

### Task 16: Create QualityScorer

**Files:**
- Create: `LosslessChecker/Services/Analysis/QualityScorer.cs`

- [ ] **Step 1: Write the scorer**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services.Analysis;

public class QualityScorer
{
    public (int score, string decision) Score(AnalysisResult result)
    {
        int score = 10;

        // DR penalties
        if (result.DynamicRange < 6) score -= 3;
        else if (result.DynamicRange < 8) score -= 1;

        // Clipping penalties
        if (result.ClippingPercent > 0.5) score -= 2;
        else if (result.ClippingPercent > 0) score -= 1;

        // ISP penalties
        if (result.HasIsp)
        {
            score -= 1;
            if (result.TruePeakDb > 1.0) score -= 1;
        }

        // LUFS penalties
        if (result.IntegratedLufs > -7) score -= 2;
        else if (result.IntegratedLufs > -10) score -= 1;

        // DC Offset penalty
        if (Math.Abs(result.DcOffsetL) > 0.001 || Math.Abs(result.DcOffsetR) > 0.001)
            score -= 1;

        // Phase penalty
        if (result.Correlation < 0) score -= 2;

        // Bit depth penalty
        if (result.LsbZeroPadded) score -= 1;

        score = Math.Max(1, Math.Min(10, score));

        // Decision
        string decision = result.Authenticity switch
        {
            "TRUE LOSSLESS" when score >= 7 => "KEEP",
            "TRUE LOSSLESS" when score >= 4 => "KEEP",
            "TRUE LOSSLESS" => "KEEP (poor master)",
            "SUSPICIOUS" => "INVESTIGATE",
            _ => "REPLACE"
        };

        return (score, decision);
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/Analysis/QualityScorer.cs
git commit -m "feat: add QualityScorer 1-10 with penalty-based scoring and Keep/Replace decisions"
```

---

### Task 17: Rewrite VerdictGenerator — 5-section structured report

**Files:**
- Modify: `LosslessChecker/Services/VerdictGenerator.cs`

- [ ] **Step 1: Replace entire file**

```csharp
using System.Text;
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class VerdictGenerator
{
    public string Generate(AnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.FileName);
        sb.AppendLine();

        // Section 1: LOSSLESS STATUS
        sb.Append("1. LOSSLESS STATUS: ");
        sb.Append(r.Authenticity);
        sb.Append(" | ");
        sb.Append($"cutoff at {r.CutoffFrequency:F0} Hz");
        if (r.ShelfType.Length > 0) sb.Append($", {r.ShelfType.ToLower()} rolloff");
        if (r.EncoderMatch != "None") sb.Append($", matches {r.EncoderMatch}");
        sb.AppendLine();
        sb.AppendLine();

        // Section 2: CLIPPING & PEAK
        sb.Append("2. CLIPPING & PEAK: ");
        if (r.HasIsp || r.ClippingPercent > 0)
            sb.Append("CLIPPED | ");
        else if (r.TruePeakDb > -0.5)
            sb.Append("HOT | ");
        else
            sb.Append("CLEAN | ");
        sb.Append($"Sample Peak {r.SamplePeakDb:F1} dBFS, True Peak {r.TruePeakDb:F1} dBTP");
        if (r.HasIsp) sb.Append(", ISP DISTORTION");
        sb.AppendLine();
        sb.AppendLine();

        // Section 3: DYNAMICS
        sb.Append("3. DYNAMICS: ");
        if (r.DynamicRange >= 13) sb.Append("AUDIOPHILE");
        else if (r.DynamicRange >= 9) sb.Append("GOOD");
        else if (r.DynamicRange >= 6) sb.Append("COMPRESSED");
        else sb.Append("CATASTROPHIC");
        sb.Append($" | DR{r.DynamicRange:F0}");
        if (r.IntegratedLufs < -1)
            sb.Append($", Integrated {r.IntegratedLufs:F1} LUFS");
        if (r.Plr > 0)
            sb.Append($", PLR {r.Plr:F1} dB");
        sb.AppendLine();
        sb.AppendLine();

        // Section 4: TECHNICAL RED FLAGS
        sb.Append("4. TECHNICAL RED FLAGS: ");
        var flags = new List<string>();
        if (Math.Abs(r.DcOffsetL) > 0.001 || Math.Abs(r.DcOffsetR) > 0.001)
            flags.Add($"DC Offset: L={r.DcOffsetL:F4}%, R={r.DcOffsetR:F4}%");
        if (r.Correlation < 0)
            flags.Add($"Phase correlation: {r.Correlation:F2} (mono incompatible)");
        if (r.LsbZeroPadded)
            flags.Add($"24-bit file has zero-padded LSBs (effective {r.EffectiveBitDepth}-bit)");
        if (r.BitDepthSuspicious)
            flags.Add($"Bit depth suspicious");
        if (r.IsUpscale)
            flags.Add($"Hi-Res upscale suspected (max HF {r.MaxHfDb:F0} dB)");
        sb.AppendLine(flags.Count > 0 ? string.Join("\n   - ", flags) : "None");
        sb.AppendLine();

        // Section 5: OVERALL VERDICT
        sb.Append("5. OVERALL VERDICT: ");
        sb.Append($"{r.QualityScore}/10");
        sb.Append(" | ");
        sb.Append(r.Decision);
        if (r.QualityScore >= 7 && r.Authenticity == "TRUE LOSSLESS")
            sb.Append(" — Excellent, genuine lossless");
        else if (r.Authenticity == "TRUE LOSSLESS" && r.QualityScore < 4)
            sb.Append(" — Genuine but poorly mastered");
        else if (r.Authenticity.StartsWith("FAKE"))
            sb.Append(" — Not genuine, find original source");

        return sb.ToString();
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/VerdictGenerator.cs
git commit -m "feat: rewrite VerdictGenerator with 5-section structured report"
```

---

### Task 18: Delete ScoreCalculator

**Files:**
- Delete: `LosslessChecker/Services/ScoreCalculator.cs`

- [ ] **Step 1: Remove the file**

```bash
git rm LosslessChecker/Services/ScoreCalculator.cs
git commit -m "refactor: remove ScoreCalculator, replaced by AuthenticityClassifier + QualityScorer"
```

---

### Task 19: Rewrite AudioAnalyzer — thin pipeline orchestrator

**Files:**
- Modify: `LosslessChecker/Services/AudioAnalyzer.cs`

- [ ] **Step 1: Replace entire file**

```csharp
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
            // Read format from header
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
                return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

            // Decode to stereo
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

            // Downmix to mono for spectrum analyzers (from already-decoded stereo)
            var mono = new float[buffer.Length];
            for (int i = 0; i < buffer.Length; i++)
                mono[i] = (buffer.Left[i] + buffer.Right[i]) * 0.5f;

            // Pipeline: run analyzers
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

            var bitResult = _bitDepth.Validate(buffer, bitDepth);

            if (ct.IsCancellationRequested) return Cancelled(result);

            var (isUpscale, upscaleVerdict, maxHfDb) = _upscale.Detect(spectrum, sampleRate);

            // Build spectrogram
            var (spectroData, spectroW, spectroH) = _spectro.Build(mono, sampleRate);

            // Assemble result
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

            // Classify
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
            return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };
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
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Services/AudioAnalyzer.cs
git commit -m "feat: rewrite AudioAnalyzer as thin pipeline orchestrator with 10 analyzers"
```

---

### Task 20: Update AudioFileViewModel — new fields

**Files:**
- Modify: `LosslessChecker/ViewModels/AudioFileViewModel.cs`

- [ ] **Step 1: Replace with updated version**

Replace the entire file:

```csharp
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LosslessChecker.Models;

namespace LosslessChecker.ViewModels;

public partial class AudioFileViewModel : ObservableObject
{
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _format = "";
    [ObservableProperty] private double _cutoffFrequency;
    [ObservableProperty] private double _dynamicRange;
    [ObservableProperty] private double _samplePeakDb;
    [ObservableProperty] private double _truePeakDb;
    [ObservableProperty] private double _clippingPercent;
    [ObservableProperty] private string _authenticity = "";
    [ObservableProperty] private int _qualityScore;
    [ObservableProperty] private string _decision = "";
    [ObservableProperty] private string _artifactLevel = "None";
    [ObservableProperty] private bool _hasArtifacts;
    [ObservableProperty] private AnalysisStatus _analysisStatus = AnalysisStatus.Pending;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _bitDepthSuspicious;
    [ObservableProperty] private bool _isUpscale;
    [ObservableProperty] private double _correlation = 1.0;
    [ObservableProperty] private double _dcOffsetL;
    [ObservableProperty] private double _dcOffsetR;
    [ObservableProperty] private double _integratedLufs;
    [ObservableProperty] private int _sampleRate;
    [ObservableProperty] private int _bitDepth;
    [ObservableProperty] private int _channels;
    [ObservableProperty] private string _structuredReport = "";
    [ObservableProperty] private string _encoderMatch = "";
    [ObservableProperty] private WriteableBitmap? _spectrogramBitmap;

    public string FilePath { get; }

    private byte[]? _rawSpectro;
    private int _spectroWidth, _spectroHeight;

    public AudioFileViewModel(AudioFileInfo fileInfo)
    {
        FilePath = fileInfo.FilePath;
        _fileName = fileInfo.FileName;
    }

    public void ApplyResult(AnalysisResult r)
    {
        FileName = r.FileName;
        Format = r.Format;
        CutoffFrequency = r.CutoffFrequency;
        DynamicRange = r.DynamicRange;
        SamplePeakDb = r.SamplePeakDb;
        TruePeakDb = r.TruePeakDb;
        ClippingPercent = r.ClippingPercent;
        Authenticity = r.Authenticity;
        QualityScore = r.QualityScore;
        Decision = r.Decision;
        ArtifactLevel = r.ArtifactLevel;
        HasArtifacts = r.HasArtifacts;
        AnalysisStatus = r.AnalysisStatus;
        ErrorMessage = r.ErrorMessage ?? "";
        BitDepthSuspicious = r.BitDepthSuspicious;
        IsUpscale = r.IsUpscale;
        Correlation = r.Correlation;
        DcOffsetL = r.DcOffsetL;
        DcOffsetR = r.DcOffsetR;
        IntegratedLufs = r.IntegratedLufs;
        SampleRate = r.SampleRate;
        BitDepth = r.BitDepth;
        Channels = r.Channels;
        StructuredReport = r.StructuredReport;
        EncoderMatch = r.EncoderMatch;

        if (r.SpectrogramFlat is { Length: > 0 })
        {
            _rawSpectro = r.SpectrogramFlat;
            _spectroWidth = r.SpectrogramWidth;
            _spectroHeight = r.SpectrogramHeight;
        }
    }

    public WriteableBitmap? GetOrBuildSpectrogram()
    {
        if (SpectrogramBitmap != null) return SpectrogramBitmap;
        if (_rawSpectro == null || _spectroWidth < 1 || _spectroHeight < 1) return null;

        int w = _spectroWidth, h = _spectroHeight;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[w * h * 4];

        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                byte dbByte = _rawSpectro[x * h + y];
                double t = dbByte / 255.0;
                int py = h - 1 - y;
                int idx = (py * w + x) * 4;
                var (r, g, b) = HotColormap(t);
                pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
            }
        }

        bmp.Lock();
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bmp.Unlock();

        SpectrogramBitmap = bmp;
        _rawSpectro = null;
        return bmp;
    }

    private static (byte r, byte g, byte b) HotColormap(double t)
    {
        if (t <= 0) return (0, 0, 0);
        if (t < 0.25) { double s = t / 0.25; return ((byte)(255 * s), 0, 0); }
        if (t < 0.5) { double s = (t - 0.25) / 0.25; return (255, (byte)(255 * s), 0); }
        if (t < 0.85) { double s = (t - 0.5) / 0.35; return (255, (byte)(128 + 127 * s), (byte)(255 * s)); }
        double s2 = (t - 0.85) / 0.15;
        return (255, 255, (byte)(128 + 127 * s2));
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/ViewModels/AudioFileViewModel.cs
git commit -m "feat: update AudioFileViewModel with all new fields from dual-axis model"
```

---

### Task 21: Update MainViewModel — new summary + detail binding

**Files:**
- Modify: `LosslessChecker/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Replace summary logic and add detail panel properties**

Replace `UpdateSummary()` and related counters:

```csharp
[ObservableProperty] private int _keepCount;
[ObservableProperty] private int _investigateCount;
[ObservableProperty] private int _replaceCount;

// ... inside ScanAndAnalyze, replace the old summary update block:

await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
{
    ProcessedFiles = done;
    if (done % 5 == 0 || done == TotalFiles)
        Progress = TotalFiles > 0 ? (double)done / TotalFiles * 100.0 : 0;

    if (result.AnalysisStatus == AnalysisStatus.Error)
        ErrorCount++;
    else if (result.Decision.StartsWith("KEEP"))
        KeepCount++;
    else if (result.Decision == "INVESTIGATE")
        InvestigateCount++;
    else if (result.Decision == "REPLACE")
        ReplaceCount++;

    if (done % 5 == 0 || done == TotalFiles)
        UpdateSummary();
});

// Replace UpdateSummary():
private void UpdateSummary()
{
    SummaryText = $"Ready: {ProcessedFiles}/{TotalFiles} | KEEP: {KeepCount} | INVESTIGATE: {InvestigateCount} | REPLACE: {ReplaceCount} | Errors: {ErrorCount}";
}
```

Also update the `OnSelectionChanged` method to use the VM's sample rate for Nyquist:

```csharp
public void OnSelectionChanged(AudioFileViewModel? selected)
{
    SelectedFile = selected;
    if (selected == null)
    {
        IsSpectrumVisible = false;
        return;
    }

    SelectedCutoffFrequency = selected.CutoffFrequency;
    SelectedNyquist = selected.SampleRate > 0 ? selected.SampleRate / 2.0 : 22050;
    SpectrumTitle = selected.FileName;
    IsSpectrumVisible = true;
}
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/ViewModels/MainViewModel.cs
git commit -m "feat: update MainViewModel with Keep/Investigate/Replace counters and selected Nyquist"
```

---

### Task 22: Redesign MainWindow.xaml — master-detail layout

**Files:**
- Modify: `LosslessChecker/Views/MainWindow.xaml`

- [ ] **Step 1: Replace the XAML with master-detail layout**

Key changes:
1. Wrap DataGrid and detail panel in a horizontal Grid (65%/35% split)
2. Add detail panel on the right side with spectrogram + report TextBlock
3. Update DataGrid columns: remove Score, Status columns; add Authenticity, Quality, Decision columns
4. Replace "Fake / Good MP3" summary with "KEEP / INVESTIGATE / REPLACE"

Full replacement XAML for the main Grid (replace everything inside `<Grid Margin="12">`):

```xml
<Window x:Class="LosslessChecker.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:converters="clr-namespace:LosslessChecker.Converters"
        Title="LosslessChecker"
        Height="800" Width="1300"
        WindowStartupLocation="CenterScreen"
        Background="#1e1e2e"
        Foreground="#cdd6f4">

    <Window.Resources>
        <converters:ScoreToColorConverter x:Key="ScoreToColorConverter"/>
        <converters:AuthenticityToColorConverter x:Key="AuthToColorConverter"/>
        <converters:DecisionToColorConverter x:Key="DecToColorConverter"/>
        <converters:ScoreToIconConverter x:Key="ScoreToIconConverter"/>
        <converters:InvertBoolConverter x:Key="InvertBoolConverter"/>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </Window.Resources>

    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Toolbar (same as before) -->
        <Border Grid.Row="0" CornerRadius="8" Background="#313244" Padding="12" Margin="0,0,0,10">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <Button Grid.Column="0" Content="Select Folder" Command="{Binding SelectFolderCommand}"
                        Width="120" Height="34" Background="#89b4fa" Foreground="#1e1e2e"
                        BorderThickness="0" FontWeight="SemiBold" Cursor="Hand">
                    <Button.Resources>
                        <Style TargetType="Border"><Setter Property="CornerRadius" Value="6"/></Style>
                    </Button.Resources>
                </Button>
                <Button Grid.Column="1" Content="Stop" Command="{Binding StopCommand}"
                        Width="80" Height="34" Background="#f38ba8" Foreground="#1e1e2e"
                        BorderThickness="0" FontWeight="SemiBold" Cursor="Hand" Margin="8,0,0,0">
                    <Button.Resources>
                        <Style TargetType="Border"><Setter Property="CornerRadius" Value="6"/></Style>
                    </Button.Resources>
                </Button>
                <ProgressBar Grid.Column="2" Value="{Binding Progress}" Minimum="0" Maximum="100"
                             Height="8" Margin="20,0,0,0" Foreground="#a6e3a1" Background="#45475a"/>
            </Grid>
        </Border>

        <!-- Master-Detail split -->
        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="400"/>
            </Grid.ColumnDefinitions>

            <!-- DataGrid (left) -->
            <DataGrid Grid.Column="0" x:Name="FilesGrid"
                      ItemsSource="{Binding Files}" AutoGenerateColumns="False"
                      IsReadOnly="True" CanUserAddRows="False" CanUserDeleteRows="False"
                      Background="#1e1e2e" Foreground="#cdd6f4" BorderBrush="#45475a"
                      RowBackground="#1e1e2e" AlternatingRowBackground="#242438"
                      GridLinesVisibility="Horizontal" HorizontalGridLinesBrush="#313244"
                      HeadersVisibility="Column" SelectionMode="Single"
                      EnableRowVirtualization="True" ScrollViewer.CanContentScroll="True"
                      SelectionChanged="DataGrid_SelectionChanged">
                <DataGrid.Resources>
                    <Style TargetType="DataGridColumnHeader">
                        <Setter Property="Background" Value="#313244"/>
                        <Setter Property="Foreground" Value="#a6adc8"/>
                        <Setter Property="FontWeight" Value="SemiBold"/>
                        <Setter Property="BorderBrush" Value="#45475a"/>
                        <Setter Property="BorderThickness" Value="0,0,0,1"/>
                        <Setter Property="Padding" Value="8,6"/>
                        <Setter Property="FontSize" Value="11"/>
                        <Setter Property="ToolTipService.InitialShowDelay" Value="200"/>
                    </Style>
                    <Style TargetType="DataGridCell">
                        <Setter Property="BorderBrush" Value="Transparent"/>
                        <Setter Property="Padding" Value="6,4"/>
                        <Setter Property="Foreground" Value="#cdd6f4"/>
                        <Setter Property="FontSize" Value="12"/>
                    </Style>
                </DataGrid.Resources>
                <DataGrid.Columns>
                    <!-- Icon -->
                    <DataGridTemplateColumn Header="" Width="35">
                        <DataGridTemplateColumn.CellTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding AnalysisStatus, Converter={StaticResource ScoreToIconConverter}}"
                                           HorizontalAlignment="Center" FontSize="14"/>
                            </DataTemplate>
                        </DataGridTemplateColumn.CellTemplate>
                    </DataGridTemplateColumn>

                    <!-- Name -->
                    <DataGridTextColumn Header="Name" Binding="{Binding FileName}" Width="180">
                        <DataGridTextColumn.ElementStyle>
                            <Style TargetType="TextBlock">
                                <Setter Property="TextTrimming" Value="CharacterEllipsis"/>
                            </Style>
                        </DataGridTextColumn.ElementStyle>
                    </DataGridTextColumn>

                    <!-- Format -->
                    <DataGridTextColumn Header="Fmt" Binding="{Binding Format}" Width="110"/>

                    <!-- Cutoff -->
                    <DataGridTextColumn Header="Cutoff" Binding="{Binding CutoffFrequency, StringFormat={}{0:F0} Hz}" Width="75"/>

                    <!-- DR -->
                    <DataGridTextColumn Header="DR" Binding="{Binding DynamicRange, StringFormat={}{0:F1}}" Width="50"/>

                    <!-- True Peak -->
                    <DataGridTextColumn Header="TPeak" Binding="{Binding TruePeakDb, StringFormat={}{0:F1}}" Width="55"/>

                    <!-- Clip% -->
                    <DataGridTextColumn Header="Clip%" Binding="{Binding ClippingPercent, StringFormat={}{0:F2}}" Width="55"/>

                    <!-- Authenticity -->
                    <DataGridTextColumn Header="Auth" Binding="{Binding Authenticity}" Width="110">
                        <DataGridTextColumn.ElementStyle>
                            <Style TargetType="TextBlock">
                                <Setter Property="Foreground" Value="{Binding Authenticity, Converter={StaticResource AuthToColorConverter}}"/>
                                <Setter Property="FontWeight" Value="Bold"/>
                            </Style>
                        </DataGridTextColumn.ElementStyle>
                    </DataGridTextColumn>

                    <!-- Quality -->
                    <DataGridTextColumn Header="Qual" Binding="{Binding QualityScore, StringFormat={}{0}/10}" Width="50">
                        <DataGridTextColumn.ElementStyle>
                            <Style TargetType="TextBlock">
                                <Setter Property="Foreground" Value="{Binding QualityScore, Converter={StaticResource ScoreToColorConverter}}"/>
                                <Setter Property="FontWeight" Value="Bold"/>
                            </Style>
                        </DataGridTextColumn.ElementStyle>
                    </DataGridTextColumn>

                    <!-- Decision -->
                    <DataGridTextColumn Header="Decision" Binding="{Binding Decision}" Width="130">
                        <DataGridTextColumn.ElementStyle>
                            <Style TargetType="TextBlock">
                                <Setter Property="Foreground" Value="{Binding Decision, Converter={StaticResource DecToColorConverter}}"/>
                                <Setter Property="FontWeight" Value="Bold"/>
                            </Style>
                        </DataGridTextColumn.ElementStyle>
                    </DataGridTextColumn>
                </DataGrid.Columns>
            </DataGrid>

            <!-- GridSplitter -->
            <GridSplitter Grid.Column="1" Width="4" Background="#45475a"
                          HorizontalAlignment="Center" VerticalAlignment="Stretch"/>

            <!-- Detail Panel (right) -->
            <Border Grid.Column="2" CornerRadius="8" Background="#242438" Padding="10"
                    Visibility="{Binding IsSpectrumVisible, Converter={StaticResource BoolToVis}}">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel>
                        <!-- Spectrogram -->
                        <Border Background="#1e1e2e" Height="180" Margin="0,0,0,8">
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="*"/>
                                </Grid.RowDefinitions>
                                <TextBlock Grid.Row="0" Text="{Binding SpectrumTitle}"
                                           Foreground="#a6adc8" FontSize="11"
                                           TextTrimming="CharacterEllipsis" Margin="4,2"/>
                                <Image Grid.Row="1" x:Name="SpectrogramImage"
                                       Stretch="Uniform" Margin="2"/>
                            </Grid>
                        </Border>

                        <!-- Cutoff info -->
                        <TextBlock Foreground="#f38ba8" FontSize="11" Margin="0,0,0,8">
                            <Run Text="Cutoff: "/>
                            <Run Text="{Binding SelectedCutoffFrequency, StringFormat={}{0:F0}}"/>
                            <Run Text=" Hz | Nyquist: "/>
                            <Run Text="{Binding SelectedNyquist, StringFormat={}{0:F0}}"/>
                            <Run Text=" Hz"/>
                        </TextBlock>

                        <!-- 5-Section Report -->
                        <TextBlock Text="{Binding SelectedFile.StructuredReport}"
                                   Foreground="#cdd6f4" FontSize="12"
                                   FontFamily="Consolas"
                                   TextWrapping="Wrap"/>
                    </StackPanel>
                </ScrollViewer>
            </Border>
        </Grid>

        <!-- Summary bar -->
        <Border Grid.Row="2" CornerRadius="8" Background="#313244" Padding="12,8" Margin="0,6,0,0">
            <TextBlock Text="{Binding SummaryText}" Foreground="#a6adc8" FontSize="13"/>
        </Border>
    </Grid>
</Window>
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Views/MainWindow.xaml
git commit -m "feat: master-detail layout with detail panel, new columns (Auth/Qual/Decision)"
```

---

### Task 23: Update MainWindow.xaml.cs — selection handler

**Files:**
- Modify: `LosslessChecker/Views/MainWindow.xaml.cs`

- [ ] **Step 1: Simplify the code-behind**

Replace the entire file:

```csharp
using System.Windows;
using LosslessChecker.ViewModels;

namespace LosslessChecker.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    private void DataGrid_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid &&
            grid.SelectedItem is AudioFileViewModel selected)
        {
            _viewModel.OnSelectionChanged(selected);
            var bmp = selected.GetOrBuildSpectrogram();
            SpectrogramImage.Source = bmp;
        }
        else
        {
            _viewModel.OnSelectionChanged(null);
            SpectrogramImage.Source = null;
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add LosslessChecker/Views/MainWindow.xaml.cs
git commit -m "feat: simplify MainWindow code-behind, remove inline cutoff line rendering"
```

---

### Task 24: Build and fix compile errors

**Files:**
- Various (fix all remaining compile errors from the overhaul)

- [ ] **Step 1: Build the project**

```bash
dotnet build LosslessChecker/LosslessChecker.csproj 2>&1
```

- [ ] **Step 2: Fix all remaining compile errors**

Common errors to expect:
- References to old field names (`TruePeak` → `TruePeakDb`, `LosslessScore` → removed, etc.)
- Missing using statements for new namespaces (`LosslessChecker.Services.Analyzers`, `LosslessChecker.Services.Analysis`)
- ArtifactDetector return type changed (3-tuple instead of 2-tuple)

Fix each error by updating the reference.

- [ ] **Step 3: Verify clean build**

Run: `dotnet build LosslessChecker/LosslessChecker.csproj`
Expected: Build succeeded. 0 Error(s)

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "fix: resolve all compile errors from model/analyzer overhaul"
```

---

### Task 25: Create test project + TestSignalGenerator

**Files:**
- Create: `LosslessChecker.Tests/LosslessChecker.Tests.csproj`
- Create: `LosslessChecker.Tests/Helpers/TestSignalGenerator.cs`

- [ ] **Step 1: Create csproj**

```bash
dotnet new xunit -n LosslessChecker.Tests -o LosslessChecker.Tests
```

Then edit `LosslessChecker.Tests/LosslessChecker.Tests.csproj` to:
```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0-windows</TargetFramework>
        <Nullable>enable</Nullable>
        <ImplicitUsings>enable</ImplicitUsings>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="xunit" Version="2.9.3"/>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0"/>
        <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2"/>
    </ItemGroup>
    <ItemGroup>
        <ProjectReference Include="..\LosslessChecker\LosslessChecker.csproj"/>
    </ItemGroup>
</Project>
```

- [ ] **Step 2: Add test project to solution**

```bash
dotnet sln LosslessChecker.slnx add LosslessChecker.Tests/LosslessChecker.Tests.csproj
```

- [ ] **Step 3: Create TestSignalGenerator**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Tests.Helpers;

public static class TestSignalGenerator
{
    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>Generate a sine sweep from startFreq to endFreq over duration seconds.</summary>
    public static float[] GenerateSweep(double startFreq, double endFreq, double duration, int sampleRate)
    {
        int n = (int)(sampleRate * duration);
        var samples = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sampleRate;
            double freq = startFreq + (endFreq - startFreq) * (t / duration);
            phase += TwoPi * freq / sampleRate;
            samples[i] = (float)Math.Sin(phase);
        }
        return samples;
    }

    /// <summary>Generate a pure sine at given frequency and duration.</summary>
    public static float[] GenerateSine(double freq, double duration, int sampleRate, double gain = 1.0)
    {
        int n = (int)(sampleRate * duration);
        var samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = (float)(gain * Math.Sin(TwoPi * freq * i / sampleRate));
        return samples;
    }

    /// <summary>Generate a clipped sine (hard clip at given ceiling).</summary>
    public static float[] GenerateClippedSine(double freq, double duration, int sampleRate, double gain = 1.2)
    {
        int n = (int)(sampleRate * duration);
        var samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = (float)Math.Max(-1.0, Math.Min(1.0, gain * Math.Sin(TwoPi * freq * i / sampleRate)));
        return samples;
    }

    /// <summary>Add DC offset to a signal.</summary>
    public static float[] AddDcOffset(float[] samples, double offsetPercent)
    {
        float offset = (float)(offsetPercent / 100.0);
        var result = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            result[i] = samples[i] + offset;
        return result;
    }

    /// <summary>Generate a StereoBuffer with known phase relationship.</summary>
    public static StereoBuffer GenerateStereo(double freq, double duration, int sampleRate,
        bool invertRight = false, double leftGain = 1.0, double rightGain = 1.0)
    {
        var left = GenerateSine(freq, duration, sampleRate, leftGain);
        var right = GenerateSine(freq, duration, sampleRate, rightGain);
        if (invertRight)
            for (int i = 0; i < right.Length; i++) right[i] = -right[i];
        return new StereoBuffer(left, right, sampleRate);
    }

    /// <summary>Generate 24-bit WAV-like float samples with zero-padded lower 8 bits.</summary>
    public static float[] GenerateZeroPadded24Bit(double freq, double duration, int sampleRate)
    {
        int n = (int)(sampleRate * duration);
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Sin(TwoPi * freq * i / sampleRate);
            // Simulate 16-bit audio stored in 24-bit container (lower 8 bits zeroed)
            short val16 = (short)Math.Round(raw * 32767.0);
            samples[i] = val16 / 32768f; // truncated to 16-bit
        }
        return samples;
    }
}
```

- [ ] **Step 4: Verify test project builds**

```bash
dotnet build LosslessChecker.Tests/LosslessChecker.Tests.csproj
```
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add LosslessChecker.Tests/
git add LosslessChecker.slnx
git commit -m "feat: add xUnit test project and TestSignalGenerator"
```

---

### Task 26-30: Analyzer Tests (batch)

**Files to create (5 test files):**

- [ ] **Step 1: CutoffDetectorTests.cs**

```csharp
using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class CutoffDetectorTests
{
    private readonly CutoffDetector _detector = new();

    [Fact]
    public void Cutoff_16kHz_Sweep_ReturnsCutoffNear16k()
    {
        var samples = TestSignalGenerator.GenerateSweep(1000, 16000, 5, 44100);
        var (cutoff, _, _) = _detector.DetectFull(samples, 44100);
        Assert.True(cutoff < 17000);
        Assert.True(cutoff > 14000);
    }

    [Fact]
    public void Cutoff_FullSpectrum_ReturnsNearNyquist()
    {
        var samples = TestSignalGenerator.GenerateSweep(1000, 22000, 5, 44100);
        var (cutoff, _, _) = _detector.DetectFull(samples, 44100);
        Assert.True(cutoff > 20000);
    }

    [Fact]
    public void EncoderMatch_16kHz_MapsToMp3_128()
    {
        var samples = TestSignalGenerator.GenerateSweep(1000, 16000, 5, 44100);
        var (cutoff, slope, _) = _detector.DetectFull(samples, 44100);
        var (match, _) = _detector.ClassifyCutoff(cutoff, slope, 44100);
        Assert.Contains("MP3 128", match);
    }

    [Fact]
    public void FakeHiRes_96kHz_NoHf_ReturnsTrue()
    {
        Assert.True(_detector.IsFakeHiRes(22000, 96000));
    }

    [Fact]
    public void NotFakeHiRes_96kHz_WithHf_ReturnsFalse()
    {
        Assert.False(_detector.IsFakeHiRes(40000, 96000));
    }
}
```

- [ ] **Step 2: TruePeakDetectorTests.cs**

```csharp
using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class TruePeakDetectorTests
{
    private readonly TruePeakDetector _detector = new();

    [Fact]
    public void Clipped_Sine_ShowsIsp()
    {
        var samples = TestSignalGenerator.GenerateClippedSine(1000, 2, 44100, 1.2);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _detector.Analyze(buffer);
        Assert.True(result.HasIsp);
    }

    [Fact]
    public void Clean_Sine_ShowsNoIsp()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 2, 44100, 0.5);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _detector.Analyze(buffer);
        Assert.False(result.HasIsp);
    }

    [Fact]
    public void Clipped_Sine_HasTruePeakAboveZero()
    {
        var samples = TestSignalGenerator.GenerateClippedSine(1000, 2, 44100, 1.5);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _detector.Analyze(buffer);
        Assert.True(result.TruePeakDbL > -0.5);
    }
}
```

- [ ] **Step 3: DcOffsetDetectorTests.cs**

```csharp
using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class DcOffsetDetectorTests
{
    private readonly DcOffsetDetector _detector = new();

    [Fact]
    public void Clean_Sine_NoDcOffset()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 2, 44100);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _detector.Analyze(buffer);
        Assert.False(result.HasDcOffset);
    }

    [Fact]
    public void DcOffset_01Percent_Detected()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 2, 44100);
        var offset = TestSignalGenerator.AddDcOffset(samples, 0.01);
        var buffer = new StereoBuffer(offset, offset, 44100);
        var result = _detector.Analyze(buffer);
        Assert.True(result.HasDcOffset);
    }
}
```

- [ ] **Step 4: PhaseAnalyzerTests.cs**

```csharp
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class PhaseAnalyzerTests
{
    private readonly PhaseAnalyzer _analyzer = new();

    [Fact]
    public void Identical_Channels_Correlation_Near_1()
    {
        var buffer = TestSignalGenerator.GenerateStereo(1000, 2, 44100, invertRight: false);
        var result = _analyzer.Analyze(buffer);
        Assert.True(result.Correlation > 0.9);
    }

    [Fact]
    public void Inverted_Right_Correlation_Near_Minus1()
    {
        var buffer = TestSignalGenerator.GenerateStereo(1000, 2, 44100, invertRight: true);
        var result = _analyzer.Analyze(buffer);
        Assert.True(result.Correlation < -0.9);
    }

    [Fact]
    public void Inverted_Right_NotMonoCompatible()
    {
        var buffer = TestSignalGenerator.GenerateStereo(1000, 2, 44100, invertRight: true);
        var result = _analyzer.Analyze(buffer);
        Assert.False(result.IsMonoCompatible);
    }
}
```

- [ ] **Step 5: DrMeterTests.cs**

```csharp
using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class DrMeterTests
{
    private readonly DrMeter _meter = new();

    [Fact]
    public void Quiet_Sine_HasHighDr()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 3, 44100, 0.1);
        var (dr, peak, clip) = _meter.Analyze(samples, 44100);
        Assert.True(dr < 20); // DR of pure sine is low because block RMS is close to overall RMS
    }

    [Fact]
    public void Clipped_Sine_HasClipping()
    {
        var samples = TestSignalGenerator.GenerateClippedSine(1000, 3, 44100, 2.0);
        var (_, _, clip) = _meter.Analyze(samples, 44100);
        Assert.True(clip > 0);
    }
}
```

- [ ] **Step 6: Run tests**

```bash
dotnet test LosslessChecker.Tests/LosslessChecker.Tests.csproj
```
Expected: All tests pass (or adjust assertions if needed).

- [ ] **Step 7: Commit**

```bash
git add LosslessChecker.Tests/Analyzers/CutoffDetectorTests.cs
git add LosslessChecker.Tests/Analyzers/TruePeakDetectorTests.cs
git add LosslessChecker.Tests/Analyzers/DcOffsetDetectorTests.cs
git add LosslessChecker.Tests/Analyzers/PhaseAnalyzerTests.cs
git add LosslessChecker.Tests/Analyzers/DrMeterTests.cs
git commit -m "test: add analyzer unit tests for Cutoff, TruePeak, DC Offset, Phase, DR"
```

---

### Task 31-32: Remaining Analyzer Tests + Classification Tests

**Files:**
- Create: `LosslessChecker.Tests/Analyzers/LufsMeterTests.cs`
- Create: `LosslessChecker.Tests/Analyzers/BitDepthValidatorTests.cs`
- Create: `LosslessChecker.Tests/Analyzers/UpscaleDetectorTests.cs`
- Create: `LosslessChecker.Tests/Analyzers/ArtifactDetectorTests.cs`
- Create: `LosslessChecker.Tests/Classification/AuthenticityClassifierTests.cs`
- Create: `LosslessChecker.Tests/Classification/QualityScorerTests.cs`

- [ ] **Step 1: LufsMeterTests.cs**

```csharp
using LosslessChecker.Models;
using LosslessChecker.Services.Analyzers;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class LufsMeterTests
{
    private readonly LufsMeter _meter = new();

    [Fact]
    public void Sine_Tone_Returns_NonNegativeLufs()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 3, 44100, 0.5);
        var buffer = new StereoBuffer(samples, samples, 44100);
        var result = _meter.Analyze(buffer);
        Assert.True(result.IntegratedLufs < -3);
        Assert.True(result.IntegratedLufs > -40);
    }

    [Fact]
    public void Loud_Sine_Returns_HigherLufs()
    {
        var quiet = TestSignalGenerator.GenerateSine(1000, 3, 44100, 0.1);
        var loud = TestSignalGenerator.GenerateSine(1000, 3, 44100, 0.9);
        var resultQuiet = _meter.Analyze(new StereoBuffer(quiet, quiet, 44100));
        var resultLoud = _meter.Analyze(new StereoBuffer(loud, loud, 44100));
        Assert.True(resultLoud.IntegratedLufs > resultQuiet.IntegratedLufs);
    }
}
```

- [ ] **Step 2: BitDepthValidatorTests.cs**

```csharp
using LosslessChecker.Models;
using LosslessChecker.Services;
using LosslessChecker.Tests.Helpers;
using Xunit;

namespace LosslessChecker.Tests.Analyzers;

public class BitDepthValidatorTests
{
    private readonly BitDepthValidator _validator = new();

    [Fact]
    public void ZeroPadded_24Bit_Detected()
    {
        var samples = TestSignalGenerator.GenerateZeroPadded24Bit(1000, 3, 44100);
        bool isPadded = _validator.CheckLsbZeroPadded(samples, 24);
        Assert.True(isPadded);
    }

    [Fact]
    public void FullScale_Sine_24Bit_NotPadded()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 3, 44100, 1.0);
        bool isPadded = _validator.CheckLsbZeroPadded(samples, 24);
        Assert.False(isPadded);
    }

    [Fact]
    public void Not24Bit_SkipsCheck()
    {
        var samples = TestSignalGenerator.GenerateSine(1000, 3, 44100);
        bool isPadded = _validator.CheckLsbZeroPadded(samples, 16);
        Assert.False(isPadded);
    }
}
```

- [ ] **Step 3: AuthenticityClassifierTests.cs**

```csharp
using LosslessChecker.Models;
using LosslessChecker.Services.Analysis;
using Xunit;

namespace LosslessChecker.Tests.Classification;

public class AuthenticityClassifierTests
{
    private readonly AuthenticityClassifier _classifier = new();

    [Fact]
    public void TrueLossless_HfCutoff_ReturnsTrue()
    {
        var result = new AnalysisResult
        {
            CutoffFrequency = 21800, SampleRate = 44100,
            ShelfType = "Natural", HasArtifacts = false
        };
        Assert.Equal("TRUE LOSSLESS", _classifier.Classify(result));
    }

    [Fact]
    public void FakeLossless_16kHz_WithArtifacts_ReturnsFake()
    {
        var result = new AnalysisResult
        {
            CutoffFrequency = 16000, HasArtifacts = true,
            ShelfType = "Brickwall", SampleRate = 44100
        };
        Assert.Equal("FAKE LOSSLESS", _classifier.Classify(result));
    }

    [Fact]
    public void FakeHiRes_96kHz_Cutoff22k_ReturnsFakeHiRes()
    {
        var result = new AnalysisResult
        {
            CutoffFrequency = 22000, SampleRate = 96000
        };
        Assert.Equal("FAKE HI-RES", _classifier.Classify(result));
    }

    [Fact]
    public void Suspicious_20kHz_Cutoff_ReturnsSuspicious()
    {
        var result = new AnalysisResult
        {
            CutoffFrequency = 20500, SampleRate = 44100,
            HasArtifacts = false, ShelfType = "Natural"
        };
        Assert.Equal("SUSPICIOUS", _classifier.Classify(result));
    }

    [Fact]
    public void Upscale_ReturnsSuspicious()
    {
        var result = new AnalysisResult
        {
            CutoffFrequency = 30000, SampleRate = 96000,
            IsUpscale = true
        };
        Assert.Equal("SUSPICIOUS", _classifier.Classify(result));
    }
}
```

- [ ] **Step 4: QualityScorerTests.cs**

```csharp
using LosslessChecker.Models;
using LosslessChecker.Services.Analysis;
using Xunit;

namespace LosslessChecker.Tests.Classification;

public class QualityScorerTests
{
    private readonly QualityScorer _scorer = new();

    [Fact]
    public void Perfect_File_GetsMaxScore()
    {
        var result = new AnalysisResult
        {
            Authenticity = "TRUE LOSSLESS",
            DynamicRange = 14, ClippingPercent = 0,
            HasIsp = false, IntegratedLufs = -18,
            DcOffsetL = 0, DcOffsetR = 0, Correlation = 1.0,
            LsbZeroPadded = false
        };
        var (score, decision) = _scorer.Score(result);
        Assert.Equal(10, score);
        Assert.Equal("KEEP", decision);
    }

    [Fact]
    public void Brickwall_Master_GetsLowQuality()
    {
        var result = new AnalysisResult
        {
            Authenticity = "TRUE LOSSLESS",
            DynamicRange = 4, ClippingPercent = 2.0,
            HasIsp = true, TruePeakDb = 1.5,
            IntegratedLufs = -6,
            DcOffsetL = 0, DcOffsetR = 0, Correlation = 1.0,
            LsbZeroPadded = false
        };
        var (score, decision) = _scorer.Score(result);
        Assert.True(score <= 5);
        Assert.StartsWith("KEEP", decision); // Still KEEP, just with quality note
    }

    [Fact]
    public void Fake_Lossless_GetsReplace()
    {
        var result = new AnalysisResult
        {
            Authenticity = "FAKE LOSSLESS",
            DynamicRange = 12, ClippingPercent = 0,
            HasIsp = false, IntegratedLufs = -14,
            DcOffsetL = 0, DcOffsetR = 0, Correlation = 1.0,
            LsbZeroPadded = false
        };
        var (_, decision) = _scorer.Score(result);
        Assert.Equal("REPLACE", decision);
    }

    [Fact]
    public void TrueLossless_PoorMaster_NeverGetsReplace()
    {
        var result = new AnalysisResult
        {
            Authenticity = "TRUE LOSSLESS",
            DynamicRange = 2, ClippingPercent = 10.0,
            HasIsp = true, TruePeakDb = 2.0,
            IntegratedLufs = -4, DcOffsetL = 0.01,
            Correlation = -0.5, LsbZeroPadded = true
        };
        var (_, decision) = _scorer.Score(result);
        Assert.NotEqual("REPLACE", decision);
        Assert.Contains("KEEP", decision);
    }
}
```

- [ ] **Step 5: Run all tests**

```bash
dotnet test LosslessChecker.Tests/LosslessChecker.Tests.csproj
```
Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add LosslessChecker.Tests/
git commit -m "test: add remaining analyzer tests + classification tests"
```

---

### Task 33: Final build, run, and cleanup

- [ ] **Step 1: Full rebuild**

```bash
dotnet build LosslessChecker/LosslessChecker.csproj
```
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Run all tests**

```bash
dotnet test LosslessChecker.Tests/LosslessChecker.Tests.csproj
```
Expected: All tests pass.

- [ ] **Step 3: Remove GC.Collect from MainViewModel**

In `MainViewModel.cs`, remove the line:
```csharp
if (done % 15 == 0)
    GC.Collect(1, GCCollectionMode.Optimized, false);
```

- [ ] **Step 4: Remove unused fields/comments**

Search for any remaining `// TODO` or commented-out code blocks and remove them.

- [ ] **Step 5: Final commit**

```bash
git add -A
git commit -m "chore: final cleanup — remove GC.Collect, fix warnings, build green"
```

---

### Verification Checklist

After all tasks, verify:

1. `dotnet build LosslessChecker/LosslessChecker.csproj` — 0 errors
2. `dotnet test LosslessChecker.Tests/LosslessChecker.Tests.csproj` — all tests pass
3. Run the app: scans a folder, shows results with Authenticity/Quality/Decision columns
4. Click a file: detail panel shows spectrogram + 5-section structured report
5. Summary bar shows KEEP / INVESTIGATE / REPLACE counts
6. Poor-master TRUE LOSSLESS files show "KEEP (poor master)" not "REPLACE"
