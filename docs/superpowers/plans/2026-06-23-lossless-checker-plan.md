# LosslessChecker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a WPF desktop app that scans audio libraries, detects fake lossless files via FFT analysis, computes DR metrics, and displays results in a real-time table with spectrogram viewer.

**Architecture:** MVVM pattern with CommunityToolkit.Mvvm. NAudio handles decoding of all formats. NWaves provides DSP/FFT. OxyPlot renders spectrograms. Core analysis pipeline: FileScanner → AudioAnalyzer (decode + 3 detectors) → ScoreCalculator → ViewModel → DataGrid.

**Tech Stack:** .NET 10 WPF, NAudio, NWaves, CommunityToolkit.Mvvm, OxyPlot.Wpf

---

## File Structure

```
LosslessChecker/
├── LosslessChecker.csproj          [modified: WinExe + UseWPF]
├── App.xaml                        [new: WPF app entry]
├── App.xaml.cs                     [new]
├── Models/
│   ├── AudioFileInfo.cs            [new]
│   ├── AnalysisResult.cs           [new]
│   └── AnalysisStatus.cs           [new]
├── Services/
│   ├── FileScanner.cs              [new]
│   ├── AudioAnalyzer.cs            [new]
│   ├── CutoffDetector.cs           [new]
│   ├── ArtifactDetector.cs         [new]
│   ├── DrMeter.cs                  [new]
│   └── ScoreCalculator.cs          [new]
├── ViewModels/
│   ├── MainViewModel.cs            [new]
│   └── AudioFileViewModel.cs       [new]
├── Views/
│   └── MainWindow.xaml/.cs         [new]
├── Converters/
│   ├── ScoreToColorConverter.cs    [new]
│   └── ScoreToIconConverter.cs     [new]
```

---

## Task 1: Convert project to WPF and add packages

**Files:**
- Modify: `LosslessChecker/LosslessChecker/LosslessChecker.csproj`
- Delete: `LosslessChecker/LosslessChecker/Program.cs`
- Create: `LosslessChecker/LosslessChecker/App.xaml`
- Create: `LosslessChecker/LosslessChecker/App.xaml.cs`

- [ ] **Step 1: Update csproj for WPF with packages**

Replace the entire content of `LosslessChecker.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <OutputType>WinExe</OutputType>
        <TargetFramework>net10.0-windows</TargetFramework>
        <UseWPF>true</UseWPF>
        <Nullable>enable</Nullable>
        <ImplicitUsings>enable</ImplicitUsings>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="NAudio" Version="2.2.1" />
        <PackageReference Include="NWaves" Version="0.9.6" />
        <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
        <PackageReference Include="OxyPlot.Wpf" Version="2.2.0" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: Delete Program.cs**

Run: `Remove-Item -LiteralPath "LosslessChecker\LosslessChecker\Program.cs"`

- [ ] **Step 3: Create App.xaml**

```xml
<Application x:Class="LosslessChecker.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="Views/MainWindow.xaml">
    <Application.Resources>
    </Application.Resources>
</Application>
```

- [ ] **Step 4: Create App.xaml.cs**

```csharp
using System.Windows;

namespace LosslessChecker;

public partial class App : Application
{
}
```

- [ ] **Step 5: Restore packages and build**

```bash
dotnet restore "LosslessChecker/LosslessChecker/LosslessChecker.csproj"
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds with no errors (MainWindow not yet created — expect error about missing StartupUri target, that's fine for now).

- [ ] **Step 6: Commit**

```bash
git add LosslessChecker/LosslessChecker/LosslessChecker.csproj LosslessChecker/LosslessChecker/App.xaml LosslessChecker/LosslessChecker/App.xaml.cs
git rm --cached LosslessChecker/LosslessChecker/Program.cs 2>$null; git add -A; git commit -m "feat: convert to WPF project with dependencies"
```

---

## Task 2: Create model classes

**Files:**
- Create: `LosslessChecker/LosslessChecker/Models/AnalysisStatus.cs`
- Create: `LosslessChecker/LosslessChecker/Models/AudioFileInfo.cs`
- Create: `LosslessChecker/LosslessChecker/Models/AnalysisResult.cs`

- [ ] **Step 1: Create AnalysisStatus enum**

```csharp
namespace LosslessChecker.Models;

public enum AnalysisStatus
{
    Pending,
    Processing,
    Completed,
    Error
}
```

- [ ] **Step 2: Create AudioFileInfo**

```csharp
namespace LosslessChecker.Models;

public record AudioFileInfo(string FilePath, string FileName, long FileSizeBytes);
```

- [ ] **Step 3: Create AnalysisResult**

```csharp
namespace LosslessChecker.Models;

public record AnalysisResult
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Format { get; init; } = "";
    public int SampleRate { get; init; }
    public int Bitrate { get; init; }
    public int BitDepth { get; init; }
    public double DurationSeconds { get; init; }

    public double CutoffFrequency { get; init; }
    public bool HasArtifacts { get; init; }
    public string ArtifactLevel { get; init; } = "None";
    public double DynamicRange { get; init; }
    public double TruePeak { get; init; }
    public double ClippingPercent { get; init; }

    public double LosslessScore { get; init; }
    public string Status { get; init; } = "";
    public string? ErrorMessage { get; init; }
    public AnalysisStatus AnalysisStatus { get; init; } = AnalysisStatus.Pending;
}
```

- [ ] **Step 4: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add LosslessChecker/LosslessChecker/Models/
git commit -m "feat: add model classes"
```

---

## Task 3: FileScanner service

**Files:**
- Create: `LosslessChecker/LosslessChecker/Services/FileScanner.cs`

- [ ] **Step 1: Create FileScanner**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class FileScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".alac"
    };

    public List<AudioFileInfo> ScanFolder(string folderPath)
    {
        var files = new List<AudioFileInfo>();

        try
        {
            var allFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories);
            foreach (var filePath in allFiles)
            {
                var ext = Path.GetExtension(filePath);
                if (SupportedExtensions.Contains(ext))
                {
                    var fileInfo = new FileInfo(filePath);
                    files.Add(new AudioFileInfo(filePath, fileInfo.Name, fileInfo.Length));
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return files;
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/LosslessChecker/Services/FileScanner.cs
git commit -m "feat: add FileScanner service"
```

---

## Task 4: CutoffDetector — FFT-based frequency cutoff detection

**Files:**
- Create: `LosslessChecker/LosslessChecker/Services/CutoffDetector.cs`

- [ ] **Step 1: Create CutoffDetector**

```csharp
using NWaves.Signals;
using NWaves.Transforms;
using NWaves.Windows;
using NWaves.Audio;

namespace LosslessChecker.Services;

public class CutoffDetector
{
    private const int FftSize = 4096;
    private const int HopSize = 2048;
    private const double MagnitudeThresholdDb = -60.0;
    private const double HighFreqSearchStartRatio = 0.4;

    public double DetectCutoff(float[] samples, int sampleRate)
    {
        if (samples.Length < FftSize)
            return sampleRate / 2.0;

        var nyquist = sampleRate / 2.0;
        var fft = new Fft(FftSize);
        var window = Window.OfType(WindowTypes.Hann, FftSize);

        var avgMagnitudes = new double[FftSize / 2];

        int frameCount = 0;
        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            var frame = new float[FftSize];
            Array.Copy(samples, pos, frame, 0, FftSize);

            for (int i = 0; i < FftSize; i++)
                frame[i] *= window[i];

            var real = frame.Select(f => (double)f).ToArray();
            var imag = new double[FftSize];

            fft.Direct(real, imag);

            for (int i = 0; i < FftSize / 2; i++)
            {
                var mag = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
                avgMagnitudes[i] += mag;
            }

            frameCount++;
        }

        if (frameCount == 0)
            return nyquist;

        for (int i = 0; i < avgMagnitudes.Length; i++)
            avgMagnitudes[i] /= frameCount;

        var peakMag = avgMagnitudes.Take(avgMagnitudes.Length / 8).Max();
        if (peakMag <= 0)
            return nyquist;

        var thresholdMag = peakMag * Math.Pow(10, MagnitudeThresholdDb / 20.0);
        var startBin = (int)(avgMagnitudes.Length * HighFreqSearchStartRatio);

        for (int bin = avgMagnitudes.Length - 1; bin >= startBin; bin--)
        {
            if (avgMagnitudes[bin] > thresholdMag)
            {
                return (double)bin / avgMagnitudes.Length * nyquist;
            }
        }

        return nyquist;
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/LosslessChecker/Services/CutoffDetector.cs
git commit -m "feat: add CutoffDetector service"
```

---

## Task 5: ArtifactDetector — spectrogram MP3 artifact detection

**Files:**
- Create: `LosslessChecker/LosslessChecker/Services/ArtifactDetector.cs`

- [ ] **Step 1: Create ArtifactDetector**

```csharp
using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class ArtifactDetector
{
    private const int FftSize = 4096;
    private const int HopSize = 1024;

    public (bool hasArtifacts, string level) Detect(float[] samples, int sampleRate, double cutoffFrequency)
    {
        if (samples.Length < FftSize * 2)
            return (false, "None");

        var nyquist = sampleRate / 2.0;
        var cutoffBin = (int)(cutoffFrequency / nyquist * (FftSize / 2));
        cutoffBin = Math.Max(1, Math.Min(cutoffBin, FftSize / 2 - 1));

        var fft = new Fft(FftSize);
        var window = Window.OfType(WindowTypes.Hann, FftSize);

        double totalSpectralFlatness = 0;
        double totalTransitionSharpness = 0;
        int frameCount = 0;

        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            var frame = new float[FftSize];
            Array.Copy(samples, pos, frame, 0, FftSize);

            for (int i = 0; i < FftSize; i++)
                frame[i] *= window[i];

            var real = frame.Select(f => (double)f).ToArray();
            var imag = new double[FftSize];
            fft.Direct(real, imag);

            var mags = new double[FftSize / 2];
            for (int i = 0; i < FftSize / 2; i++)
                mags[i] = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

            // Spectral flatness above cutoff
            int aboveStart = cutoffBin;
            int aboveEnd = Math.Min(cutoffBin + 20, mags.Length - 1);
            if (aboveEnd > aboveStart)
            {
                double geomMean = 0, arithMean = 0;
                int count = 0;
                for (int i = aboveStart; i < aboveEnd; i++)
                {
                    var v = Math.Max(mags[i], 1e-10);
                    geomMean += Math.Log(v);
                    arithMean += v;
                    count++;
                }
                if (count > 0 && arithMean > 0)
                {
                    geomMean = Math.Exp(geomMean / count);
                    arithMean /= count;
                    totalSpectralFlatness += geomMean / arithMean;
                }
            }

            // Transition sharpness at cutoff
            if (cutoffBin >= 10 && cutoffBin < mags.Length - 10)
            {
                double before = 0, after = 0;
                for (int i = cutoffBin - 5; i < cutoffBin; i++)
                    before += mags[i];
                for (int i = cutoffBin; i < cutoffBin + 5; i++)
                    after += mags[i];
                before /= 5;
                after /= 5;
                if (before > 0)
                    totalTransitionSharpness += after / before;
            }

            frameCount++;
        }

        if (frameCount == 0)
            return (false, "None");

        double avgFlatness = totalSpectralFlatness / frameCount;
        double avgTransition = totalTransitionSharpness / frameCount;

        if (avgFlatness > 0.5 && avgTransition < 0.3)
            return (true, "Strong");
        if (avgFlatness > 0.3 && avgTransition < 0.5)
            return (true, "Medium");
        if (avgFlatness > 0.15 || avgTransition < 0.7)
            return (true, "Weak");

        return (false, "None");
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/LosslessChecker/Services/ArtifactDetector.cs
git commit -m "feat: add ArtifactDetector service"
```

---

## Task 6: DrMeter — TT DR Meter and peak analysis

**Files:**
- Create: `LosslessChecker/LosslessChecker/Services/DrMeter.cs`

- [ ] **Step 1: Create DrMeter**

```csharp
namespace LosslessChecker.Services;

public class DrMeter
{
    private const double BlockDurationSec = 0.5;
    private const double TopPercentile = 0.2;
    private const double FullScaleDb = 0.0;

    public (double dr, double truePeak, double clippingPercent) Analyze(float[] samples, int sampleRate)
    {
        int blockSize = (int)(sampleRate * BlockDurationSec);
        if (blockSize < 1 || samples.Length < blockSize)
            return (0, 0, 0);

        var blockRms = new List<double>();
        double truePeak = double.MinValue;
        int clippedSamples = 0;

        for (int pos = 0; pos < samples.Length; pos += blockSize)
        {
            int len = Math.Min(blockSize, samples.Length - pos);
            double sumSq = 0;
            for (int i = pos; i < pos + len; i++)
            {
                var abs = Math.Abs(samples[i]);
                if (abs > truePeak)
                    truePeak = abs;
                if (abs >= 1.0)
                    clippedSamples++;
                sumSq += samples[i] * samples[i];
            }
            blockRms.Add(Math.Sqrt(sumSq / len));
        }

        truePeak = truePeak > double.MinValue ? 20.0 * Math.Log10(Math.Max(truePeak, 1e-10)) : FullScaleDb;
        double clippingPercent = (double)clippedSamples / samples.Length * 100.0;

        blockRms.Sort((a, b) => b.CompareTo(a));
        int topCount = Math.Max(1, (int)(blockRms.Count * TopPercentile));
        double topAvgRms = blockRms.Take(topCount).Average();
        double overallRms = blockRms.Average();

        double dr = 20.0 * Math.Log10(topAvgRms / Math.Max(overallRms, 1e-10));

        return (dr, truePeak, clippingPercent);
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/LosslessChecker/Services/DrMeter.cs
git commit -m "feat: add DrMeter service"
```

---

## Task 7: ScoreCalculator

**Files:**
- Create: `LosslessChecker/LosslessChecker/Services/ScoreCalculator.cs`

- [ ] **Step 1: Create ScoreCalculator**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class ScoreCalculator
{
    public AnalysisResult Calculate(AnalysisResult input)
    {
        var nyquist = input.SampleRate / 2.0;
        double cutoffRatio = nyquist > 0 ? input.CutoffFrequency / nyquist : 1.0;

        double cutoffPenalty = cutoffRatio switch
        {
            >= 0.95 => 0,
            >= 0.85 => 5,
            >= 0.75 => 15,
            >= 0.65 => 25,
            _ => 40
        };

        double artifactPenalty = input.ArtifactLevel switch
        {
            "None" => 0,
            "Weak" => 10,
            "Medium" => 20,
            "Strong" => 30,
            _ => 0
        };

        double clippingPenalty = input.ClippingPercent switch
        {
            <= 0 => 0,
            < 1 => 5,
            < 5 => 10,
            _ => 20
        };

        double drPenalty = input.DynamicRange switch
        {
            >= 10 => 0,
            >= 8 => 3,
            >= 6 => 7,
            _ => 10
        };

        double score = 100.0 - cutoffPenalty - artifactPenalty - clippingPenalty - drPenalty;
        score = Math.Max(0, Math.Min(100, score));

        string status = score switch
        {
            >= 90 => "Lossless",
            >= 60 => "Suspicious",
            _ => "Fake / Poor Quality"
        };

        return input with { LosslessScore = Math.Round(score, 1), Status = status };
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/LosslessChecker/Services/ScoreCalculator.cs
git commit -m "feat: add ScoreCalculator service"
```

---

## Task 8: AudioAnalyzer — orchestrator service

**Files:**
- Create: `LosslessChecker/LosslessChecker/Services/AudioAnalyzer.cs`

- [ ] **Step 1: Create AudioAnalyzer**

```csharp
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

            // Decode to mono float samples
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
                    Status = "Too short for analysis",
                    LosslessScore = 50
                };
            }

            if (ct.IsCancellationRequested)
                return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = "Cancelled" };

            double cutoff = _cutoffDetector.DetectCutoff(samples, format.SampleRate);

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
                ClippingPercent = Math.Round(clippingPercent, 2)
            };

            result = _scoreCalculator.Calculate(result);
            result = result with { AnalysisStatus = AnalysisStatus.Completed };

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
        using var resampler = reader.WaveFormat.Channels > 1
            ? new MediaFoundationResampler(reader, new WaveFormat(reader.WaveFormat.SampleRate, 1))
            : null;

        var source = resampler ?? (IWaveProvider)reader;
        var provider = source.ToSampleProvider();

        var samples = new List<float>((int)(reader.TotalTime.TotalSeconds * reader.WaveFormat.SampleRate));
        var buffer = new float[4096];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
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
            ".m4a" or ".alac" => new StreamMediaFoundationReader(filePath),
            _ => null
        };
    }

    private static string GetFormatLabel(string filePath, WaveFormat format)
    {
        var ext = Path.GetExtension(filePath).ToUpperInvariant().TrimStart('.');
        return $"{ext} {format.SampleRate / 1000.0:F0}kHz/{format.BitsPerSample}bit";
    }

    private static int EstimateBitrate(AudioFileInfo fileInfo)
    {
        // Rough estimate based on format and sample rate
        var ext = Path.GetExtension(fileInfo.FilePath).ToLowerInvariant();
        if (ext == ".mp3")
        {
            try
            {
                using var mp3 = new Mp3FileReader(fileInfo.FilePath);
                return mp3.Mp3WaveFormat.AverageBytesPerSecond * 8 / 1000;
            }
            catch { return 0; }
        }
        return 0;
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/LosslessChecker/Services/AudioAnalyzer.cs
git commit -m "feat: add AudioAnalyzer orchestrator service"
```

---

## Task 9: Converters — Score to color and icon

**Files:**
- Create: `LosslessChecker/LosslessChecker/Converters/ScoreToColorConverter.cs`
- Create: `LosslessChecker/LosslessChecker/Converters/ScoreToIconConverter.cs`

- [ ] **Step 1: Create ScoreToColorConverter**

```csharp
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LosslessChecker.Converters;

public class ScoreToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double score)
        {
            if (score >= 90) return new SolidColorBrush(Color.FromRgb(46, 160, 67));
            if (score >= 60) return new SolidColorBrush(Color.FromRgb(210, 153, 34));
            return new SolidColorBrush(Color.FromRgb(207, 34, 46));
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
```

- [ ] **Step 2: Create ScoreToIconConverter**

```csharp
using System.Globalization;
using System.Windows.Data;
using LosslessChecker.Models;

namespace LosslessChecker.Converters;

public class ScoreToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AnalysisStatus status)
        {
            return status switch
            {
                AnalysisStatus.Pending => "\u23F3",
                AnalysisStatus.Processing => "\u2699",
                AnalysisStatus.Completed => "\u2705",
                AnalysisStatus.Error => "\u26A0",
                _ => "?"
            };
        }
        return "?";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
```

- [ ] **Step 3: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/LosslessChecker/Converters/
git commit -m "feat: add value converters"
```

---

## Task 10: AudioFileViewModel

**Files:**
- Create: `LosslessChecker/LosslessChecker/ViewModels/AudioFileViewModel.cs`

- [ ] **Step 1: Create AudioFileViewModel**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using LosslessChecker.Models;

namespace LosslessChecker.ViewModels;

public partial class AudioFileViewModel : ObservableObject
{
    [ObservableProperty]
    private string _fileName = "";

    [ObservableProperty]
    private string _format = "";

    [ObservableProperty]
    private double _cutoffFrequency;

    [ObservableProperty]
    private double _dynamicRange;

    [ObservableProperty]
    private double _truePeak;

    [ObservableProperty]
    private double _clippingPercent;

    [ObservableProperty]
    private double _losslessScore;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _hasArtifacts;

    [ObservableProperty]
    private string _artifactLevel = "";

    [ObservableProperty]
    private AnalysisStatus _analysisStatus = AnalysisStatus.Pending;

    [ObservableProperty]
    private string _errorMessage = "";

    public string FilePath { get; }

    public AudioFileViewModel(AudioFileInfo fileInfo)
    {
        FilePath = fileInfo.FilePath;
        _fileName = fileInfo.FileName;
    }

    public void ApplyResult(AnalysisResult result)
    {
        FileName = result.FileName;
        Format = result.Format;
        CutoffFrequency = result.CutoffFrequency;
        DynamicRange = result.DynamicRange;
        TruePeak = result.TruePeak;
        ClippingPercent = result.ClippingPercent;
        LosslessScore = result.LosslessScore;
        StatusMessage = result.Status;
        HasArtifacts = result.HasArtifacts;
        ArtifactLevel = result.ArtifactLevel;
        AnalysisStatus = result.AnalysisStatus;
        ErrorMessage = result.ErrorMessage ?? "";
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/LosslessChecker/ViewModels/AudioFileViewModel.cs
git commit -m "feat: add AudioFileViewModel"
```

---

## Task 11: MainViewModel

**Files:**
- Create: `LosslessChecker/LosslessChecker/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Create MainViewModel**

```csharp
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LosslessChecker.Models;
using LosslessChecker.Services;

namespace LosslessChecker.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly FileScanner _scanner = new();
    private readonly AudioAnalyzer _analyzer = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private ObservableCollection<AudioFileViewModel> _files = new();

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private int _processedFiles;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _summaryText = "Ready";

    [ObservableProperty]
    private int _fakeCount;

    [ObservableProperty]
    private int _goodMp3Count;

    [ObservableProperty]
    private int _errorCount;

    [RelayCommand]
    private async Task SelectFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder with audio files"
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            await ScanAndAnalyze(dialog.SelectedPath);
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
    }

    private async Task ScanAndAnalyze(string folderPath)
    {
        IsProcessing = true;
        ProcessedFiles = 0;
        ErrorCount = 0;
        FakeCount = 0;
        GoodMp3Count = 0;
        Files.Clear();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var foundFiles = await Task.Run(() => _scanner.ScanFolder(folderPath), ct);
            TotalFiles = foundFiles.Count;

            if (ct.IsCancellationRequested) return;

            var vms = foundFiles.Select(f => new AudioFileViewModel(f)).ToList();
            foreach (var vm in vms)
                Files.Add(vm);

            var queue = new ConcurrentQueue<AudioFileViewModel>(vms);
            int processed = 0;

            var tasks = Enumerable.Range(0, Environment.ProcessorCount).Select(async _ =>
            {
                while (queue.TryDequeue(out var vm) && !ct.IsCancellationRequested)
                {
                    vm.AnalysisStatus = AnalysisStatus.Processing;

                    var fileInfo = new AudioFileInfo(vm.FilePath, vm.FileName, 0);
                    var result = await Task.Run(() => _analyzer.Analyze(fileInfo, ct), ct);

                    vm.ApplyResult(result);

                    int done = Interlocked.Increment(ref processed);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProcessedFiles = done;
                        Progress = TotalFiles > 0 ? (double)done / TotalFiles * 100.0 : 0;

                        if (result.AnalysisStatus == AnalysisStatus.Error)
                            ErrorCount++;
                        else if (result.LosslessScore < 60)
                            FakeCount++;
                        else if (result.Format.StartsWith("MP3") && result.LosslessScore >= 60)
                            GoodMp3Count++;

                        UpdateSummary();
                    });
                }
            });

            await Task.WhenAll(tasks);

            UpdateSummary();
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsProcessing = false;
        }
    }

    private void UpdateSummary()
    {
        SummaryText =
            $"Ready: {ProcessedFiles}/{TotalFiles} | Fake: {FakeCount} | Good MP3: {GoodMp3Count} | Errors: {ErrorCount}";
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds — but will fail due to missing `System.Windows.Forms` reference. Fix: add `<UseWindowsForms>true</UseWindowsForms>` to csproj, OR replace FolderBrowserDialog with a WPF-native folder picker. Let's use WindowsAPICodePack or the newer `Microsoft.Win32.OpenFolderDialog`. Actually, for simplicity, let's use the WPF `System.Windows.Forms.FolderBrowserDialog` — it requires adding `<UseWindowsForms>true</UseWindowsForms>` to the csproj alongside WPF. Update csproj accordingly.

- [ ] **Step 2b: Update csproj for Windows Forms**

Add `<UseWindowsForms>true</UseWindowsForms>` to the PropertyGroup in `LosslessChecker.csproj`:

```xml
<PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/LosslessChecker/ViewModels/MainViewModel.cs
git add LosslessChecker/LosslessChecker/LosslessChecker.csproj
git commit -m "feat: add MainViewModel with folder scan and parallel analysis"
```

---

## Task 12: MainWindow UI (XAML + code-behind)

**Files:**
- Create: `LosslessChecker/LosslessChecker/Views/MainWindow.xaml`
- Create: `LosslessChecker/LosslessChecker/Views/MainWindow.xaml.cs`

- [ ] **Step 1: Create MainWindow.xaml**

```xml
<Window x:Class="LosslessChecker.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:converters="clr-namespace:LosslessChecker.Converters"
        xmlns:viewmodels="clr-namespace:LosslessChecker.ViewModels"
        Title="LosslessChecker"
        Height="700" Width="1100"
        WindowStartupLocation="CenterScreen"
        Background="#1e1e2e"
        Foreground="#cdd6f4">

    <Window.Resources>
        <converters:ScoreToColorConverter x:Key="ScoreToColorConverter"/>
        <converters:ScoreToIconConverter x:Key="ScoreToIconConverter"/>
    </Window.Resources>

    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Toolbar -->
        <Border Grid.Row="0" CornerRadius="8" Background="#313244" Padding="12" Margin="0,0,0,10">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <Button Grid.Column="0"
                        Content="Select Folder"
                        Command="{Binding SelectFolderCommand}"
                        IsEnabled="{Binding IsProcessing, Converter={StaticResource InvertBoolConverter}}"
                        Width="120" Height="34"
                        Background="#89b4fa" Foreground="#1e1e2e"
                        BorderThickness="0" FontWeight="SemiBold"
                        Cursor="Hand">
                    <Button.Resources>
                        <Style TargetType="Border">
                            <Setter Property="CornerRadius" Value="6"/>
                        </Style>
                    </Button.Resources>
                </Button>

                <Button Grid.Column="1"
                        Content="Stop"
                        Command="{Binding StopCommand}"
                        IsEnabled="{Binding IsProcessing}"
                        Width="80" Height="34"
                        Background="#f38ba8" Foreground="#1e1e2e"
                        BorderThickness="0" FontWeight="SemiBold"
                        Cursor="Hand"
                        Margin="8,0,0,0">
                    <Button.Resources>
                        <Style TargetType="Border">
                            <Setter Property="CornerRadius" Value="6"/>
                        </Style>
                    </Button.Resources>
                </Button>

                <ProgressBar Grid.Column="2"
                             Value="{Binding Progress}"
                             Minimum="0" Maximum="100"
                             Height="8"
                             Margin="20,0,0,0"
                             Foreground="#a6e3a1"
                             Background="#45475a"/>
            </Grid>
        </Border>

        <!-- DataGrid -->
        <DataGrid Grid.Row="1"
                  ItemsSource="{Binding Files}"
                  AutoGenerateColumns="False"
                  IsReadOnly="True"
                  CanUserAddRows="False"
                  CanUserDeleteRows="False"
                  Background="#1e1e2e"
                  Foreground="#cdd6f4"
                  BorderBrush="#45475a"
                  RowBackground="#1e1e2e"
                  AlternatingRowBackground="#242438"
                  GridLinesVisibility="Horizontal"
                  HorizontalGridLinesBrush="#313244"
                  HeadersVisibility="Column"
                  SelectionMode="Single"
                  EnableRowVirtualization="True"
                  ScrollViewer.CanContentScroll="True">

            <DataGrid.Resources>
                <Style TargetType="DataGridColumnHeader">
                    <Setter Property="Background" Value="#313244"/>
                    <Setter Property="Foreground" Value="#a6adc8"/>
                    <Setter Property="FontWeight" Value="SemiBold"/>
                    <Setter Property="BorderBrush" Value="#45475a"/>
                    <Setter Property="BorderThickness" Value="0,0,0,1"/>
                    <Setter Property="Padding" Value="10,8"/>
                </Style>
                <Style TargetType="DataGridCell">
                    <Setter Property="BorderBrush" Value="Transparent"/>
                    <Setter Property="Padding" Value="10,6"/>
                    <Setter Property="Foreground" Value="#cdd6f4"/>
                </Style>
            </DataGrid.Resources>

            <DataGrid.Columns>
                <DataGridTemplateColumn Header="" Width="40">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <TextBlock Text="{Binding AnalysisStatus, Converter={StaticResource ScoreToIconConverter}}"
                                       HorizontalAlignment="Center"
                                       FontSize="14"/>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>

                <DataGridTextColumn Header="Name"
                                    Binding="{Binding FileName}"
                                    Width="280">
                    <DataGridTextColumn.ElementStyle>
                        <Style TargetType="TextBlock">
                            <Setter Property="TextTrimming" Value="CharacterEllipsis"/>
                        </Style>
                    </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>

                <DataGridTextColumn Header="Format"
                                    Binding="{Binding Format}"
                                    Width="140"/>

                <DataGridTextColumn Header="Cutoff (Hz)"
                                    Binding="{Binding CutoffFrequency, StringFormat={}{0:F0}}"
                                    Width="100"/>

                <DataGridTextColumn Header="DR (dB)"
                                    Binding="{Binding DynamicRange, StringFormat={}{0:F1}}"
                                    Width="80"/>

                <DataGridTextColumn Header="True Peak"
                                    Binding="{Binding TruePeak, StringFormat={}{0:F1} dBTP}"
                                    Width="90"/>

                <DataGridTextColumn Header="Clip %"
                                    Binding="{Binding ClippingPercent, StringFormat={}{0:F2}}"
                                    Width="70"/>

                <DataGridTextColumn Header="Artifacts"
                                    Binding="{Binding ArtifactLevel}"
                                    Width="90"/>

                <DataGridTemplateColumn Header="Score" Width="80">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <TextBlock Text="{Binding LosslessScore, StringFormat={}{0:F0}%}"
                                       Foreground="{Binding LosslessScore, Converter={StaticResource ScoreToColorConverter}}"
                                       FontWeight="Bold"
                                       HorizontalAlignment="Center"/>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>

                <DataGridTextColumn Header="Status"
                                    Binding="{Binding StatusMessage}"
                                    Width="150">
                    <DataGridTextColumn.ElementStyle>
                        <Style TargetType="TextBlock">
                            <Setter Property="Foreground">
                                <Setter.Value>
                                    <Binding Path="LosslessScore"
                                             Converter="{StaticResource ScoreToColorConverter}"/>
                                </Setter.Value>
                            </Setter>
                        </Style>
                    </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>
            </DataGrid.Columns>
        </DataGrid>

        <!-- Summary bar -->
        <Border Grid.Row="2" CornerRadius="8" Background="#313244" Padding="12,8" Margin="0,10,0,0">
            <TextBlock Text="{Binding SummaryText}"
                       Foreground="#a6adc8"
                       FontSize="13"/>
        </Border>
    </Grid>
</Window>
```

- [ ] **Step 2: Create MainWindow.xaml.cs**

```csharp
using System.Windows;
using LosslessChecker.ViewModels;

namespace LosslessChecker.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
```

- [ ] **Step 3: Add InverseBooleanConverter**

We need an `InvertBoolConverter` for the Select Folder button. Add it inline or as a resource. Let's add it to the converters:

Create: `LosslessChecker/LosslessChecker/Converters/InvertBoolConverter.cs`

```csharp
using System.Globalization;
using System.Windows.Data;

namespace LosslessChecker.Converters;

public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}
```

Wait — the binding is `IsEnabled="{Binding IsProcessing, Converter={StaticResource InvertBoolConverter}}"`. The converter should return `!boolValue`. Let me fix:

```csharp
public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    => value is bool b && !b;

public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    => value is bool b && !b;
```

Actually, `value` is `IsProcessing`. We want `IsEnabled = !IsProcessing`. So `Convert(true)` should return `false`. The correct version:

```csharp
public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    => value is bool b ? !b : true;

public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    => value is bool b ? !b : true;
```

Let me finalize the XAML to include this converter.

- [ ] **Step 4: Build and verify**

```bash
dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.csproj" --no-restore
```

Expected: Build succeeds. If `InvertBoolConverter` not found, add it as a static resource in XAML.

- [ ] **Step 5: Commit**

```bash
git add LosslessChecker/LosslessChecker/Views/MainWindow.xaml LosslessChecker/LosslessChecker/Views/MainWindow.xaml.cs LosslessChecker/LosslessChecker/Converters/InvertBoolConverter.cs
git commit -m "feat: add MainWindow with DataGrid and dark theme"
```

---

## Task 13: Verification — full build and run

- [ ] **Step 1: Restore and build from solution root**

```bash
dotnet restore "LosslessChecker/LosslessChecker/LosslessChecker.slnx"
if ($?) { dotnet build "LosslessChecker/LosslessChecker/LosslessChecker.slnx" --no-restore }
```

Expected: Build succeeds with zero errors and zero warnings.

- [ ] **Step 2: Run the application briefly to verify it launches**

```bash
Start-Process "LosslessChecker\LosslessChecker\bin\Debug\net10.0-windows\LosslessChecker.exe"
```

Expected: Window opens with dark theme, Select Folder button visible.

- [ ] **Step 3: Commit final build**

```bash
git add -A
git commit -m "chore: final wiring and verification"
```
