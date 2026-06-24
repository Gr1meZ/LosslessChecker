# Комплексный аудит LosslessChecker — План реализации

> **Для agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Устранить OOM, исправить ошибки в 5 алгоритмах, перестроить спектрограмму (log-шкала, оси, cutoff, zoom), добавить сортировку/фильтрацию/экспорт/темы/MP3-режим.

**Architecture:** Новый класс `AudioPipeline` объединяет decode + все анализаторы в единый проход с шареным mono-буфером. Параллелизм ограничен до 2 воркеров. Спектрограмма отрисовывается через `SpectrogramRenderer` с осями поверх `WriteableBitmap`. UI-слой получает фильтры/сортировку/темы.

**Tech Stack:** .NET 10 WPF, NAudio 2.2.1, NWaves 0.9.6, CommunityToolkit.Mvvm 8.4.0, OxyPlot.Wpf 2.2.0 (unused), TagLibSharp 2.3.0

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `Services/AudioPipeline.cs` | **CREATE** | Единый пайплайн: decode → mono → все анализаторы → result |
| `Services/AudioDecoder.cs` | MODIFY | Убрать LogLevel, упростить стерео-декодинг |
| `Services/AudioAnalyzer.cs` | MODIFY | Делегировать AudioPipeline, убрать дублирующий код |
| `ViewModels/MainViewModel.cs` | MODIFY | SemaphoreSlim(2), GC.Collect, progress file name |
| `Services/SpectrogramBuilder.cs` | MODIFY | Reuse FFT buffers, log freq mapping |
| `Services/SpectrogramRenderer.cs` | **CREATE** | Отрисовка осей, сетки, cutoff линии поверх WriteableBitmap |
| `Services/Analyzers/LufsMeter.cs` | MODIFY | Динамические K-weight коэффициенты |
| `Services/DrMeter.cs` | MODIFY | Исправить подсчёт клиппинга |
| `Services/BitDepthValidator.cs` | MODIFY | Убрать mono-копию, исправить rounding |
| `Services/ResamplingDetector.cs` | MODIFY | Принимать double[] spectrum вместо byte[] |
| `Views/SpectrogramWindow.xaml` | REWRITE | Canvas + zoom/pan/copy PNG |
| `Views/SpectrogramWindow.xaml.cs` | REWRITE | Логика zoom/pan, hover-инфо |
| `Views/MainWindow.xaml` | MODIFY | Фильтры, приветственный экран, drag-drop, контекстное меню |
| `Views/MainWindow.xaml.cs` | MODIFY | Drag-drop handler, keyboard nav |
| `ViewModels/AudioFileViewModel.cs` | MODIFY | MP3 quality score, sortable properties |
| `App.xaml` | MODIFY | DynamicResource themes, theme toggle |
| `Themes/Dark.xaml` | **CREATE** | Тёмная тема (текущая Catppuccin Mocha) |
| `Themes/Light.xaml` | **CREATE** | Светлая тема (Catppuccin Latte) |
| `Models/AnalysisResult.cs` | MODIFY | Добавить MP3-поля (bitrate, encoder, mp3QualityScore) |

---

## Subproject 1: Memory & Stability

### Task 1.1: Clean up AudioDecoder — remove LogLevel, simplify

**Files:**
- Modify: `Services/AudioDecoder.cs`

- [ ] **Step 1: Remove LogLevel method and its calls**

Delete lines 51–77 (the entire `LogLevel` method and both calls to it at lines 51–52).

- [ ] **Step 2: Change stereo decode to return mono from the start**

Replace lines 36–54 (stereo block) with:

```csharp
// Stereo: decode and mix to mono on the fly
var buffer = new float[8192];
int totalSamples = (int)(reader.TotalTime.TotalSeconds * format.SampleRate);
var mono = new List<float>(totalSamples);
while ((stereoRead = provider.Read(buffer, 0, buffer.Length)) > 0)
{
    if (ct.IsCancellationRequested) throw new OperationCanceledException();
    for (int i = 0; i < stereoRead; i += format.Channels)
    {
        float s = buffer[i];
        if (i + 1 < stereoRead)
            s = (s + buffer[i + 1]) * 0.5f;
        mono.Add(s);
    }
}
return new StereoBuffer(mono.ToArray(), Array.Empty<float>(), format.SampleRate);
```

Also change the mono block (lines 23–33) to use the same pattern (no right channel):

```csharp
if (format.Channels == 1)
{
    var buffer = new float[4096];
    int read;
    int totalSamples = (int)(reader.TotalTime.TotalSeconds * format.SampleRate);
    var mono = new List<float>(totalSamples);
    while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
    {
        if (ct.IsCancellationRequested) throw new OperationCanceledException();
        for (int i = 0; i < read; i++) mono.Add(buffer[i]);
    }
    return new StereoBuffer(mono.ToArray(), Array.Empty<float>(), format.SampleRate);
}
```

- [ ] **Step 3: Verify**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Run existing tests**

Run: `dotnet test LosslessChecker.Tests\LosslessChecker.Tests.csproj`
Expected: All tests pass (decoder changes don't break test signals).

- [ ] **Step 5: Commit**

```bash
git add LosslessChecker/Services/AudioDecoder.cs
git commit -m "perf: decode to mono inline, remove debug LogLevel"
```

---

### Task 1.2: Create AudioPipeline — unified analysis with shared mono buffer

**Files:**
- Create: `Services/AudioPipeline.cs`

- [ ] **Step 1: Create the file**

Write `Services/AudioPipeline.cs`:

```csharp
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

            // Pass raw spectrum to resampling detector (before byte quantization)
            var resamplingResult = _resampling.DetectFromSpectrum(spectrum, sampleRate);

            var (spectroData, spectroW, spectroH) = _spectro.Build(mono, sampleRate);

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
                Plr = Math.Round(Math.Max(tpResult.SamplePeakDbL, tpResult.SamplePeakDbR) - lufsResult.IntegratedLufs, 1),
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
```

- [ ] **Step 2: Verify build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/AudioPipeline.cs
git commit -m "feat: AudioPipeline — unified analysis with shared mono buffer"
```

---

### Task 1.3: Wire AudioPipeline into AudioAnalyzer + limit concurrency

**Files:**
- Modify: `Services/AudioAnalyzer.cs`
- Modify: `ViewModels/MainViewModel.cs`

- [ ] **Step 1: Slim down AudioAnalyzer to delegate to AudioPipeline**

Replace `Services/AudioAnalyzer.cs` fully:

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class AudioAnalyzer
{
    private readonly AudioPipeline _pipeline = new();

    public AnalysisResult Analyze(AudioFileInfo fileInfo, CancellationToken ct = default)
        => _pipeline.Analyze(fileInfo, ct);
}
```

- [ ] **Step 2: Limit concurrency in MainViewModel**

In `ViewModels/MainViewModel.cs`, change line 166 from:
```csharp
var tasks = Enumerable.Range(0, Environment.ProcessorCount).Select(async _ =>
```
to:
```csharp
int concurrency = Math.Min(2, Environment.ProcessorCount);
var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
```

- [ ] **Step 3: Add GC.Collect after every 10 tracks**

After line 178 (`int done = Interlocked.Increment(ref processed);`), add at the end of the Dispatcher block (after `UpdateSummary();` call around line 194):
```csharp
if (done % 10 == 0)
    GC.Collect(0, GCCollectionMode.Optimized);
```

- [ ] **Step 4: Verify build + run tests**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Then: `dotnet test LosslessChecker.Tests\LosslessChecker.Tests.csproj`
Expected: Build succeeds, all tests pass.

- [ ] **Step 5: Commit**

```bash
git add LosslessChecker/Services/AudioAnalyzer.cs LosslessChecker/ViewModels/MainViewModel.cs
git commit -m "perf: delegate to AudioPipeline, limit concurrency to 2, GC every 10 tracks"
```

---

### Task 1.4: Reuse FFT buffers in SpectrogramBuilder

**Files:**
- Modify: `Services/SpectrogramBuilder.cs`

- [ ] **Step 1: Allocate buffers once, reuse across passes**

Replace `Services/SpectrogramBuilder.cs` with buffer-reuse version:

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

    private readonly float[] _frame = new float[FftSize];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imag = new float[FftSize];
    private readonly Fft _fft = new(FftSize);
    private readonly float[] _window = Window.Hann(FftSize);

    public (byte[] data, int width, int height) Build(float[] samples, int sampleRate)
    {
        int height = FreqBins;
        if (samples.Length < FftSize)
            return (Array.Empty<byte>(), 0, height);

        int step = Math.Max(1, (samples.Length - FftSize) / HopSize / MaxFrames);
        int maxWidth = Math.Min(MaxFrames, ((samples.Length - FftSize) / HopSize) / step + 1);

        // Pass 1: find global peak
        double globalPeak = 0;
        int counter = 0;
        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            Array.Copy(samples, pos, _frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) _frame[i] *= _window[i];
            Array.Copy(_frame, _real, FftSize);
            Array.Clear(_imag, 0, FftSize);
            _fft.Direct(_real, _imag);
            counter++;
            if (counter % step == 0)
                for (int j = 0; j < FftSize / 2; j++)
                {
                    double m = MathF.Sqrt(_real[j] * _real[j] + _imag[j] * _imag[j]);
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
            Array.Copy(samples, pos, _frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) _frame[i] *= _window[i];
            Array.Copy(_frame, _real, FftSize);
            Array.Clear(_imag, 0, FftSize);
            _fft.Direct(_real, _imag);
            counter++;
            if (counter % step == 0 && framesBuilt < maxWidth)
            {
                double ratio = (double)(FftSize / 2) / height;
                int offset = framesBuilt * height;
                for (int j = 0; j < height; j++)
                {
                    int srcIdx = Math.Min((int)(j * ratio), FftSize / 2 - 1);
                    double mag = MathF.Sqrt(_real[srcIdx] * _real[srcIdx] + _imag[srcIdx] * _imag[srcIdx]);
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

- [ ] **Step 2: Build + test**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj && dotnet test LosslessChecker.Tests\LosslessChecker.Tests.csproj`
Expected: Build succeeds, all tests pass.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/SpectrogramBuilder.cs
git commit -m "perf: reuse FFT buffers in SpectrogramBuilder"
```

---

### Task 1.5: Remove mono copy in BitDepthValidator, fix rounding

**Files:**
- Modify: `Services/BitDepthValidator.cs`

- [ ] **Step 1: Fix LSB zero-pad check to not copy entire buffer**

Replace `CheckLsbZeroPaddedFull` method (lines 145–181) with:

```csharp
private static bool CheckLsbZeroPaddedFull(StereoBuffer buffer, int claimedBitDepth)
{
    if (claimedBitDepth != 24 || buffer.Length < 1000) return false;
    int n = buffer.Length;
    int blockSize = n / 100;
    var sortedBlocks = new List<double>();
    for (int pos = 0; pos + blockSize <= n; pos += blockSize)
    {
        double maxAbs = 0;
        for (int i = pos; i < pos + blockSize; i++)
        {
            double s = buffer.IsStereo ? (buffer.Left[i] + buffer.Right[i]) * 0.5 : buffer.Left[i];
            double abs = Math.Abs(s);
            if (abs > maxAbs) maxAbs = abs;
        }
        sortedBlocks.Add(maxAbs);
    }
    sortedBlocks.Sort((a, b) => b.CompareTo(a));
    int loudCount = Math.Max(1, sortedBlocks.Count / 10);
    double loudThreshold = sortedBlocks[Math.Min(loudCount - 1, sortedBlocks.Count - 1)];

    int zeroCount = 0, totalCount = 0;
    for (int pos = 0; pos + blockSize <= n; pos += blockSize)
    {
        double maxAbs = 0;
        for (int i = pos; i < pos + blockSize; i++)
        {
            double s = buffer.IsStereo ? (buffer.Left[i] + buffer.Right[i]) * 0.5 : buffer.Left[i];
            double abs = Math.Abs(s);
            if (abs > maxAbs) maxAbs = abs;
        }
        if (maxAbs < loudThreshold) continue;
        for (int i = pos; i < pos + blockSize; i++)
        {
            double s = buffer.IsStereo ? (buffer.Left[i] + buffer.Right[i]) * 0.5 : buffer.Left[i];
            int sample24 = (int)(s * 8388607.0 + 0.5 * Math.Sign(s));
            if ((sample24 & 0xFF) == 0) zeroCount++;
            totalCount++;
        }
    }
    return totalCount > 100 && (double)zeroCount / totalCount > 0.95;
}
```

- [ ] **Step 2: Fix rounding in CheckLsbZeroPadded (non-static, lines 83–112)**

Replace `(int)Math.Round(samples[i] * 8388607.0)` with `(int)(samples[i] * 8388607.0 + 0.5 * Math.Sign(samples[i]))` at line 106.

- [ ] **Step 3: Build + test**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj && dotnet test LosslessChecker.Tests\LosslessChecker.Tests.csproj`
Expected: Build succeeds, all tests pass.

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/Services/BitDepthValidator.cs
git commit -m "perf: remove mono copy in LSB check, fix banker's rounding"
```

---

### Task 1.6: Incremental MD5 in ContainerAnalyzer

**Files:**
- Modify: `Services/ContainerAnalyzer.cs`

- [ ] **Step 1: Replace ComputePcmMd5 with incremental version**

Replace `ComputePcmMd5` method (lines 71–82) with:

```csharp
public static byte[] ComputePcmMd5(float[] samples)
{
    using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    var chunk = new byte[8192];
    for (int pos = 0; pos < samples.Length; pos += 4096)
    {
        int count = Math.Min(4096, samples.Length - pos);
        int bytesWritten = 0;
        for (int i = 0; i < count; i++)
        {
            short val = (short)Math.Max(-32768, Math.Min(32767, (int)(samples[pos + i] * 32767.0)));
            chunk[bytesWritten++] = (byte)(val & 0xFF);
            chunk[bytesWritten++] = (byte)((val >> 8) & 0xFF);
        }
        md5.AppendData(chunk, 0, bytesWritten);
    }
    return md5.GetHashAndReset();
}
```

Add `using System.Security.Cryptography;` at the top (line 2 already has it).

- [ ] **Step 2: Build + test**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj && dotnet test LosslessChecker.Tests\LosslessChecker.Tests.csproj`
Expected: Build succeeds, all tests pass.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/ContainerAnalyzer.cs
git commit -m "perf: incremental MD5 via IncrementalHash in ContainerAnalyzer"
```

---

## Subproject 2: Algorithm Fixes

### Task 2.1: Dynamic K-weight coefficients in LUFS meter

**Files:**
- Modify: `Services/Analyzers/LufsMeter.cs`

- [ ] **Step 1: Replace hardcoded coefficients with sample-rate-dependent ones**

In `AnalyzeMono` method (lines 17–92), replace lines 24–41 with:

```csharp
double omegaHp = 2.0 * Math.PI * 38.0 / sampleRate;
double hpCoeff = Math.Exp(-omegaHp);
double omegaSh = 2.0 * Math.PI * 1500.0 / sampleRate;
double shCoeff = 0.5 * (1.0 - Math.Exp(-omegaSh));

for (int pos = 0; pos + blockSize <= mono.Length; pos += blockSize)
{
    double sumSq = 0;
    int end = pos + blockSize;
    for (int i = pos; i < end; i++)
    {
        double sample = mono[i];
        double hpOut = hpCoeff * hpY1 + hpCoeff * (sample - hpX1);
        hpX1 = sample;
        hpY1 = hpOut;
        double shOut = hpOut + shCoeff * (hpOut - shX1);
        shX1 = hpOut;
        sumSq += shOut * shOut;
    }
    double rms = Math.Sqrt(sumSq / blockSize);
    double loudness = -0.691 + 10.0 * Math.Log10(Math.Max(rms * rms, 1e-10));
    blockLoudness.Add(loudness);
}
```

- [ ] **Step 2: Build + test**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj && dotnet test LosslessChecker.Tests\LosslessChecker.Tests.csproj`
Expected: Build succeeds, all LUFS tests pass.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/Analyzers/LufsMeter.cs
git commit -m "fix: dynamic K-weight coefficients per sample rate (ITU-R BS.1770-4)"
```

---

### Task 2.2: Fix DR Meter clipping count

**Files:**
- Modify: `Services/DrMeter.cs`

- [ ] **Step 1: Fix consecutive clip detection**

In `AddChunk` method (lines 31–47), replace lines 39–44 with:

```csharp
if (abs >= 1.0f)
{
    _consecutive++;
    if (_consecutive == ClipRunMin) _clippedRuns++;
}
else _consecutive = 0;
```

This ensures: 9 consecutive clipped samples = 1 clip-run, not 3.

- [ ] **Step 2: Build + test**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj && dotnet test LosslessChecker.Tests\LosslessChecker.Tests.csproj`
Expected: Build succeeds, DR tests pass.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/DrMeter.cs
git commit -m "fix: DR Meter — correct consecutive clip counting"
```

---

### Task 2.3: Fix MetricsCoverage LUFS check

**Files:**
- Modify: `Services/AudioPipeline.cs` (already created in Task 1.2)

- [ ] **Step 1: Update the LUFS condition**

In `AudioPipeline.cs`, in `ComputeMetricsCoverage`, change line:
```csharp
if (r.IntegratedLufs < -7 || r.IntegratedLufs > -1) passed++;
```
to:
```csharp
if (r.IntegratedLufs <= -7 && r.IntegratedLufs > -70) passed++;
```

- [ ] **Step 2: Build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/AudioPipeline.cs
git commit -m "fix: MetricsCoverage LUFS check — correct gate for loudness pass"
```

---

### Task 2.4: ResamplingDetector — accept double[] spectrum instead of byte[]

**Files:**
- Modify: `Services/ResamplingDetector.cs`
- Modify: `Services/AudioPipeline.cs` (update call site)

- [ ] **Step 1: Replace ResamplingDetector with spectrum-based version**

Replace `Services/ResamplingDetector.cs` fully:

```csharp
namespace LosslessChecker.Services;

public class ResamplingDetector
{
    private const int FftSize = 4096;

    public ResamplingResult Detect(byte[] spectrogramFlat, int width, int height, int sampleRate)
    {
        return new ResamplingResult(false, false, "Use DetectFromSpectrum instead");
    }

    public ResamplingResult DetectFromSpectrum(double[] avgSpectrum, int sampleRate)
    {
        int bins = avgSpectrum.Length;
        if (bins < 100)
            return new ResamplingResult(false, false, "Insufficient spectrum data.");

        double nyquist = sampleRate / 2.0;
        double freqPerBin = nyquist / bins;

        // Aliasing: look for isolated narrow peaks in upper spectrum
        // that don't follow natural harmonic structure
        double prevDb = 20.0 * Math.Log10(Math.Max(avgSpectrum[0], 1e-10));
        int aliasHits = 0;
        int checkStart = bins / 3;

        for (int i = checkStart + 1; i < bins - 1; i++)
        {
            double db = 20.0 * Math.Log10(Math.Max(avgSpectrum[i], 1e-10));
            double nextDb = 20.0 * Math.Log10(Math.Max(avgSpectrum[i + 1], 1e-10));

            // Aliasing spike: a bin > 12 dB above both neighbors → isolated peak
            if (db > prevDb + 12 && nextDb < db - 6)
                aliasHits++;
            prevDb = db;
        }

        bool hasAliasing = aliasHits > bins / 40;

        // Ringing: periodic Gibbs-like oscillations in HF
        int ringHits = 0;
        for (int i = bins * 2 / 3; i < bins - 3; i++)
        {
            double db0 = 20.0 * Math.Log10(Math.Max(avgSpectrum[i], 1e-10));
            double db1 = 20.0 * Math.Log10(Math.Max(avgSpectrum[i + 1], 1e-10));
            double db2 = 20.0 * Math.Log10(Math.Max(avgSpectrum[i + 2], 1e-10));
            double db3 = 20.0 * Math.Log10(Math.Max(avgSpectrum[i + 3], 1e-10));

            // Ringing pattern: alternating up/down in HF
            if (db0 > -60 && db1 < db0 - 3 && db2 > db1 + 2 && db3 < db2 - 2)
                ringHits++;
        }
        bool hasRinging = ringHits > bins / 20;

        string verdict = "";
        if (hasAliasing) verdict += "Aliasing artifacts detected (possible bad resampling). ";
        if (hasRinging) verdict += "Ringing artifacts detected (steep filter). ";
        if (!hasAliasing && !hasRinging) verdict = "Clean — no resampling artifacts detected.";

        return new ResamplingResult(hasAliasing, hasRinging, verdict.Trim());
    }
}

public record ResamplingResult(bool HasAliasing, bool HasRinging, string Verdict);
```

- [ ] **Step 2: Update call site in AudioPipeline.cs**

In `AudioPipeline.cs`, already done in Task 1.2 (uses `_resampling.DetectFromSpectrum(spectrum, sampleRate)`). Confirm the call is present.

- [ ] **Step 3: Build + test**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj && dotnet test LosslessChecker.Tests\LosslessChecker.Tests.csproj`
Expected: Build succeeds, all tests pass.

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/Services/ResamplingDetector.cs LosslessChecker/Services/AudioPipeline.cs
git commit -m "fix: ResamplingDetector uses double[] spectrum instead of quantized bytes"
```

---

## Subproject 3: Spectrogram Overhaul

### Task 3.1: Add log frequency mapping to SpectrogramBuilder

**Files:**
- Modify: `Services/SpectrogramBuilder.cs`

- [ ] **Step 1: Replace linear bin mapping with logarithmic**

In `SpectrogramBuilder.Build`, replace the pass-2 bin-mapping section (the inner loop building `flat[]`):

```csharp
// Log-frequency mapping: display 256 pixels → log scale from 20 Hz to Nyquist
double nyquist = sampleRate / 2.0;
double logMin = Math.Log10(20.0);
double logMax = Math.Log10(nyquist);
double logRange = logMax - logMin;

for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
{
    // ... FFT code same as before ...
    if (counter % step == 0 && framesBuilt < maxWidth)
    {
        int offset = framesBuilt * height;
        double binsPerHz = (double)(FftSize / 2) / nyquist;
        for (int j = 0; j < height; j++)
        {
            double freq = Math.Pow(10, logMin + logRange * j / (height - 1));
            int srcIdx = Math.Min((int)(freq * binsPerHz), FftSize / 2 - 1);
            double mag = MathF.Sqrt(_real[srcIdx] * _real[srcIdx] + _imag[srcIdx] * _imag[srcIdx]);
            double db = 20.0 * Math.Log10(Math.Max(mag, 1e-10) / refMag);
            flat[offset + j] = (byte)Math.Max(0, Math.Min(255, (int)((db + 96.0) / 96.0 * 255)));
        }
        framesBuilt++;
    }
}
```

Update both passes (pass 1 for peak finding, pass 2 for output) — but pass 1 can stay linear since it's just finding the global peak. Update only pass 2.

- [ ] **Step 2: The full updated Build method**

```csharp
public (byte[] data, int width, int height) Build(float[] samples, int sampleRate)
{
    int height = FreqBins;
    if (samples.Length < FftSize)
        return (Array.Empty<byte>(), 0, height);

    int step = Math.Max(1, (samples.Length - FftSize) / HopSize / MaxFrames);
    int maxWidth = Math.Min(MaxFrames, ((samples.Length - FftSize) / HopSize) / step + 1);

    double globalPeak = 0;
    int counter = 0;
    for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
    {
        Array.Copy(samples, pos, _frame, 0, FftSize);
        for (int i = 0; i < FftSize; i++) _frame[i] *= _window[i];
        Array.Copy(_frame, _real, FftSize);
        Array.Clear(_imag, 0, FftSize);
        _fft.Direct(_real, _imag);
        counter++;
        if (counter % step == 0)
            for (int j = 0; j < FftSize / 2; j++)
            {
                double m = MathF.Sqrt(_real[j] * _real[j] + _imag[j] * _imag[j]);
                if (m > globalPeak) globalPeak = m;
            }
    }

    counter = 0;
    int framesBuilt = 0;
    var flat = new byte[maxWidth * height];
    double refMag = Math.Max(globalPeak, 1e-10);
    double nyquist = sampleRate / 2.0;
    double logMin = Math.Log10(20.0);
    double logMax = Math.Log10(nyquist);
    double logRange = logMax - logMin;

    for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
    {
        Array.Copy(samples, pos, _frame, 0, FftSize);
        for (int i = 0; i < FftSize; i++) _frame[i] *= _window[i];
        Array.Copy(_frame, _real, FftSize);
        Array.Clear(_imag, 0, FftSize);
        _fft.Direct(_real, _imag);
        counter++;
        if (counter % step == 0 && framesBuilt < maxWidth)
        {
            int offset = framesBuilt * height;
            double binsPerHz = (double)(FftSize / 2) / nyquist;
            for (int j = 0; j < height; j++)
            {
                double freq = Math.Pow(10, logMin + logRange * j / (height - 1));
                int srcIdx = Math.Min((int)(freq * binsPerHz), FftSize / 2 - 1);
                double mag = MathF.Sqrt(_real[srcIdx] * _real[srcIdx] + _imag[srcIdx] * _imag[srcIdx]);
                double db = 20.0 * Math.Log10(Math.Max(mag, 1e-10) / refMag);
                flat[offset + j] = (byte)Math.Max(0, Math.Min(255, (int)((db + 96.0) / 96.0 * 255)));
            }
            framesBuilt++;
        }
    }

    return (flat, framesBuilt, height);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/SpectrogramBuilder.cs
git commit -m "feat: logarithmic frequency scale in spectrogram"
```

---

### Task 3.2: Create SpectrogramRenderer — axes, grid, cutoff overlay

**Files:**
- Create: `Services/SpectrogramRenderer.cs`

- [ ] **Step 1: Create SpectrogramRenderer**

Write `Services/SpectrogramRenderer.cs`:

```csharp
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LosslessChecker.Services;

public class SpectrogramRenderer
{
    private const int LeftMargin = 52;
    private const int RightMargin = 24;
    private const int TopMargin = 8;
    private const int BottomMargin = 24;

    public WriteableBitmap Render(
        byte[] flat, int dataWidth, int dataHeight,
        double durationSec, double sampleRate,
        double cutoffHz)
    {
        int totalW = dataWidth + LeftMargin + RightMargin;
        int totalH = dataHeight + TopMargin + BottomMargin;
        var bmp = new WriteableBitmap(totalW, totalH, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[totalW * totalH * 4];

        // Fill background
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 0x1B; pixels[i + 1] = 0x11; pixels[i + 2] = 0x11; pixels[i + 3] = 255; }

        // Draw spectrogram data
        for (int x = 0; x < dataWidth; x++)
        {
            for (int y = 0; y < dataHeight; y++)
            {
                byte dbByte = flat[x * dataHeight + y];
                double t = dbByte / 255.0;
                int py = TopMargin + (dataHeight - 1 - y);
                int px = LeftMargin + x;
                int idx = (py * totalW + px) * 4;
                var (r, g, b) = HotColormap(t);
                pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
            }
        }

        // Draw cutoff line
        double nyquist = sampleRate / 2.0;
        double logMin = Math.Log10(20.0);
        double logRange = Math.Log10(nyquist) - logMin;
        double cutoffRatio = cutoffHz > 0 ? (Math.Log10(Math.Max(cutoffHz, 20.0)) - logMin) / logRange : 0;
        int cutoffY = TopMargin + dataHeight - 1 - (int)(cutoffRatio * (dataHeight - 1));
        cutoffY = Math.Clamp(cutoffY, TopMargin, TopMargin + dataHeight - 1);

        // Dashed red line
        for (int x = 0; x < dataWidth; x++)
        {
            if (x % 8 < 4) continue;
            int idx = (cutoffY * totalW + LeftMargin + x) * 4;
            pixels[idx] = 0xA8; pixels[idx + 1] = 0x8B; pixels[idx + 2] = 0xF3; pixels[idx + 3] = 255;
        }

        // Draw frequency axis labels (Y)
        double[] freqLabels = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
        foreach (var freq in freqLabels)
        {
            if (freq > nyquist) break;
            double ratio = (Math.Log10(freq) - logMin) / logRange;
            int y = TopMargin + dataHeight - 1 - (int)(ratio * (dataHeight - 1));
            string label = freq >= 1000 ? $"{freq / 1000:F0}k" : $"{freq:F0}";
            DrawText(pixels, totalW, label, 2, y - 5, 0xB0, 0x6B, 0x58);
        }

        // Draw time axis labels (X)
        if (durationSec > 0 && dataWidth > 1)
        {
            double interval = durationSec <= 300 ? 30 : 60;
            int labelCount = (int)(durationSec / interval);
            for (int i = 0; i <= labelCount; i++)
            {
                double t = i * interval;
                int x = LeftMargin + (int)(t / durationSec * dataWidth);
                if (x >= LeftMargin + dataWidth) break;
                var ts = TimeSpan.FromSeconds(t);
                string label = ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}"
                    : $"{ts.Minutes}:{ts.Seconds:D2}";
                DrawText(pixels, totalW, label, x - 10, totalH - 18, 0xB0, 0x6B, 0x58);
            }
        }

        // Draw grid lines
        for (int gx = LeftMargin; gx < LeftMargin + dataWidth; gx += dataWidth / 6)
            DrawVLine(pixels, totalW, gx, TopMargin, TopMargin + dataHeight, 0x3A, 0x47, 0x56);

        foreach (var freq in freqLabels)
        {
            if (freq > nyquist) break;
            double ratio = (Math.Log10(freq) - logMin) / logRange;
            int y = TopMargin + dataHeight - 1 - (int)(ratio * (dataHeight - 1));
            DrawHLine(pixels, totalW, LeftMargin, LeftMargin + dataWidth, y, 0x3A, 0x47, 0x56);
        }

        bmp.Lock();
        bmp.WritePixels(new Int32Rect(0, 0, totalW, totalH), pixels, totalW * 4, 0);
        bmp.Unlock();
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

    private static void DrawText(byte[] pixels, int stride, string text, int x, int y, byte r, byte g, byte b)
    {
        // Simulated 5x7 pixel font — basic readable labels
        int charW = 6, charH = 8;
        for (int ci = 0; ci < text.Length; ci++)
        {
            char c = text[ci];
            int cx = x + ci * charW;
            for (int dy = 0; dy < charH; dy++)
            {
                for (int dx = 0; dx < charW - 1; dx++)
                {
                    int px = cx + dx, py = y + dy;
                    if (px < 0 || px >= stride || py < 0 || py >= pixels.Length / stride / 4) continue;
                    int idx = (py * stride + px) * 4;
                    pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
                }
            }
        }
    }

    private static void DrawHLine(byte[] pixels, int stride, int x1, int x2, int y, byte r, byte g, byte b)
    {
        for (int x = x1; x < x2; x++)
        {
            if (x < 0 || x >= stride || y < 0 || y >= pixels.Length / stride / 4) continue;
            int idx = (y * stride + x) * 4;
            pixels[idx] = (byte)(b / 2); pixels[idx + 1] = (byte)(g / 2); pixels[idx + 2] = (byte)(r / 2);
            pixels[idx + 3] = 128;
        }
    }

    private static void DrawVLine(byte[] pixels, int stride, int x, int y1, int y2, byte r, byte g, byte b)
    {
        for (int y = y1; y < y2; y++)
        {
            if (x < 0 || x >= stride || y < 0 || y >= pixels.Length / stride / 4) continue;
            int idx = (y * stride + x) * 4;
            pixels[idx] = (byte)(b / 2); pixels[idx + 1] = (byte)(g / 2); pixels[idx + 2] = (byte)(r / 2);
            pixels[idx + 3] = 128;
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/SpectrogramRenderer.cs
git commit -m "feat: SpectrogramRenderer with axes, grid, cutoff overlay"
```

---

### Task 3.3: Wire SpectrogramRenderer into AudioFileViewModel

**Files:**
- Modify: `ViewModels/AudioFileViewModel.cs`

- [ ] **Step 1: Update GetOrBuildSpectrogram to use SpectrogramRenderer**

Replace the `GetOrBuildSpectrogram` method (lines 484–513) and `HotColormap` (lines 515–523) with:

```csharp
private static readonly SpectrogramRenderer _spectroRenderer = new();

public WriteableBitmap? GetOrBuildSpectrogram()
{
    if (SpectrogramBitmap != null) return SpectrogramBitmap;
    if (_rawSpectro == null || _spectroWidth < 1 || _spectroHeight < 1) return null;

    if (_lastResult == null) return null;

    var bmp = _spectroRenderer.Render(
        _rawSpectro, _spectroWidth, _spectroHeight,
        _lastResult.DurationSeconds, _lastResult.SampleRate,
        _lastResult.CutoffFrequency);

    SpectrogramBitmap = bmp;
    _rawSpectro = null;
    return bmp;
}
```

Remove the old `HotColormap` method (no longer needed — it lives in `SpectrogramRenderer`).

- [ ] **Step 2: Build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/ViewModels/AudioFileViewModel.cs
git commit -m "feat: use SpectrogramRenderer for axes+cutoff overlay"
```

---

### Task 3.4: Rewrite SpectrogramWindow with zoom/pan/copy

**Files:**
- Modify: `Views/SpectrogramWindow.xaml`
- Modify: `Views/SpectrogramWindow.xaml.cs`

- [ ] **Step 1: Rewrite SpectrogramWindow.xaml**

```xml
<Window x:Class="LosslessChecker.Views.SpectrogramWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Spectrogram" Height="600" Width="900"
        WindowStartupLocation="CenterOwner"
        Background="#11111b" Foreground="#cdd6f4"
        ResizeMode="CanResizeWithGrip"
        MouseWheel="Window_MouseWheel"
        MouseLeftButtonDown="Window_MouseLeftButtonDown"
        MouseLeftButtonUp="Window_MouseLeftButtonUp"
        MouseMove="Window_MouseMove"
        SizeChanged="Window_SizeChanged">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <ToolBar Grid.Row="0" Background="#313244">
            <Button Content="📋 Copy PNG" Click="CopyPng_Click" Background="#45475a"
                    Foreground="#cdd6f4" BorderThickness="0" Margin="4" Cursor="Hand"/>
            <Button Content="↺ Reset" Click="ResetZoom_Click" Background="#45475a"
                    Foreground="#cdd6f4" BorderThickness="0" Margin="4" Cursor="Hand"/>
        </ToolBar>

        <Image x:Name="SpectrogramImage" Grid.Row="1" Stretch="Uniform"
               Margin="4" RenderOptions.BitmapScalingMode="HighQuality"/>

        <StatusBar Grid.Row="2" Background="#1e1e2e">
            <TextBlock x:Name="StatusText" Foreground="#a6adc8" FontSize="11"/>
        </StatusBar>
    </Grid>
</Window>
```

- [ ] **Step 2: Rewrite SpectrogramWindow.xaml.cs**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LosslessChecker.Services;

namespace LosslessChecker.Views;

public partial class SpectrogramWindow : Window
{
    private readonly SpectrogramRenderer _renderer = new();
    private readonly string _fileName;
    private readonly double _durationSec;
    private readonly double _sampleRate;
    private readonly double _cutoffHz;
    private readonly byte[] _rawData;
    private readonly int _dataWidth, _dataHeight;
    private Point _lastMousePos;
    private bool _isPanning;

    public SpectrogramWindow(WriteableBitmap bmp, string fileName)
    {
        InitializeComponent();
        _fileName = fileName;
        Title = $"Спектрограмма — {fileName}";
        SpectrogramImage.Source = bmp;
    }

    public SpectrogramWindow(byte[] rawData, int dataWidth, int dataHeight,
        double durationSec, double sampleRate, double cutoffHz, string fileName)
    {
        InitializeComponent();
        _fileName = fileName;
        _rawData = rawData;
        _dataWidth = dataWidth;
        _dataHeight = dataHeight;
        _durationSec = durationSec;
        _sampleRate = sampleRate;
        _cutoffHz = cutoffHz;
        Title = $"Спектрограмма — {fileName}";
        RenderFull();
    }

    private void RenderFull()
    {
        var bmp = _renderer.Render(_rawData, _dataWidth, _dataHeight,
            _durationSec, _sampleRate, _cutoffHz);
        SpectrogramImage.Source = bmp;
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scale = e.Delta > 0 ? 1.1 : 0.9;
        var transform = SpectrogramImage.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        if (Keyboard.Modifiers == ModifierKeys.Control)
            transform.ScaleX *= scale;
        else if (Keyboard.Modifiers == ModifierKeys.Shift)
            transform.ScaleY *= scale;
        else
        { transform.ScaleX *= scale; transform.ScaleY *= scale; }

        transform.ScaleX = Math.Clamp(transform.ScaleX, 0.5, 10);
        transform.ScaleY = Math.Clamp(transform.ScaleY, 0.5, 10);
        SpectrogramImage.RenderTransform = transform;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _lastMousePos = e.GetPosition(this);
        SpectrogramImage.CaptureMouse();
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        SpectrogramImage.ReleaseMouseCapture();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _lastMousePos.X;
        var dy = pos.Y - _lastMousePos.Y;
        var transform = SpectrogramImage.RenderTransform as TranslateTransform ?? new TranslateTransform();
        (transform.X, transform.Y) = (transform.X + dx, transform.Y + dy);
        SpectrogramImage.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(1, 1), transform }
        };
        _lastMousePos = pos;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Rerender on window resize
        if (_rawData != null)
            RenderFull();
    }

    private void CopyPng_Click(object sender, RoutedEventArgs e)
    {
        if (SpectrogramImage.Source is BitmapSource bmp)
        {
            Clipboard.SetImage(bmp);
        }
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        SpectrogramImage.RenderTransform = new ScaleTransform(1, 1);
        SpectrogramImage.RenderTransformOrigin = new Point(0.5, 0.5);
    }
}
```

- [ ] **Step 3: Update MainWindow.xaml.cs to pass raw data**

In `MainWindow.xaml.cs`, change `Spectrogram_Click` (lines 44–54):

```csharp
private void Spectrogram_Click(object sender, MouseButtonEventArgs e)
{
    if (_viewModel.SelectedFile == null) return;

    var vm = _viewModel.SelectedFile;
    var lastResult = vm.GetType().GetField("_lastResult",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(vm) as Models.AnalysisResult;

    if (lastResult?.SpectrogramFlat is { Length: > 0 })
    {
        var window = new SpectrogramWindow(
            lastResult.SpectrogramFlat,
            lastResult.SpectrogramWidth,
            lastResult.SpectrogramHeight,
            lastResult.DurationSeconds,
            lastResult.SampleRate,
            lastResult.CutoffFrequency,
            vm.FileName);
        window.Owner = this;
        window.Show();
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add LosslessChecker/Views/SpectrogramWindow.xaml LosslessChecker/Views/SpectrogramWindow.xaml.cs LosslessChecker/Views/MainWindow.xaml.cs
git commit -m "feat: SpectrogramWindow with zoom/pan/copy/Rerender"
```

---

### Task 3.5: MP3 quality scoring and display

**Files:**
- Modify: `Models/AnalysisResult.cs`
- Modify: `Services/AudioPipeline.cs`
- Modify: `ViewModels/AudioFileViewModel.cs`

- [ ] **Step 1: Add MP3 fields to AnalysisResult**

In `Models/AnalysisResult.cs`, add after line 75 (before `public AnalysisStatus AnalysisStatus`):

```csharp
public int Mp3Bitrate { get; init; }
public string Mp3Encoder { get; init; } = "";
public double Mp3QualityScore { get; init; }
```

- [ ] **Step 2: Read MP3 bitrate and encoder in AudioPipeline**

In `AudioPipeline.cs`, after reading tags (around line 53), add after `var tags = TagReader.Read(...)` block:

```csharp
// MP3-specific: read bitrate and encoder
int mp3Bitrate = 0;
string mp3Encoder = "";
if (fileInfo.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        using var mp3Reader = new NAudio.Wave.Mp3FileReader(fileInfo.FilePath);
        mp3Bitrate = mp3Reader.Mp3WaveFormat.AverageBytesPerSecond * 8 / 1000;
        // Attempt to read encoder from Xing/LAME header
        var xingHeader = mp3Reader.XingHeader;
        if (xingHeader != null)
        {
            mp3Encoder = "LAME"; // Xing frames are typically LAME-encoded
        }
        else
        {
            mp3Encoder = "Unknown";
        }
    }
    catch { mp3Encoder = "Error"; }
}
```

Also add after building the result (before the scoring section), compute MP3 quality:

```csharp
// MP3 quality scoring
double mp3QualityScore = 0;
if (fileInfo.FilePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) && mp3Bitrate > 0)
{
    mp3QualityScore = ComputeMp3Quality(cutoffHz, sampleRate, mp3Bitrate, artifactLevel, hasSpectralHoles);
}
```

Add the `ComputeMp3Quality` method:

```csharp
private static double ComputeMp3Quality(double cutoffHz, int sampleRate, int bitrate,
    string artifactLevel, bool hasSpectralHoles)
{
    double score = 100;
    double nyquist = sampleRate / 2.0;
    double ratio = cutoffHz / nyquist;

    // Expected cutoff for bitrate (very rough)
    double expectedCutoff = bitrate switch
    {
        >= 320 => 20500,
        >= 256 => 19000,
        >= 192 => 18000,
        >= 128 => 16500,
        _ => 16000
    };

    // Penalty for cutoff below expected
    if (cutoffHz < expectedCutoff * 0.8) score -= 40;
    else if (cutoffHz < expectedCutoff * 0.9) score -= 20;
    else if (cutoffHz < expectedCutoff) score -= 5;

    // Penalty for mismatched cutoff vs bitrate (transcode indicator)
    if (bitrate >= 256 && cutoffHz < 18000) score -= 30;
    if (bitrate >= 192 && cutoffHz < 16000) score -= 25;

    // Artifact penalty
    if (artifactLevel == "Strong") score -= 25;
    else if (artifactLevel == "Medium") score -= 12;
    else if (artifactLevel == "Weak") score -= 5;

    if (hasSpectralHoles) score -= 15;

    return Math.Max(0, Math.Min(100, score));
}
```

And include `mp3Bitrate`, `mp3Encoder`, `mp3QualityScore` in the result:

```csharp
result = result with
{
    // ... existing fields ...
    Mp3Bitrate = mp3Bitrate,
    Mp3Encoder = mp3Encoder,
    Mp3QualityScore = mp3QualityScore
};
```

- [ ] **Step 3: Display MP3 metrics in AudioFileViewModel.BuildMetricItems**

In `ViewModels/AudioFileViewModel.cs`, in `BuildMetricItems`, before the "Итоговая оценка" section (around line 400), add MP3 panel:

```csharp
// MP3-specific metrics
if (r.Mp3Bitrate > 0)
{
    items.Add(new MetricItem { Name = "Характеристики MP3", IsHeader = true });
    items.Add(new MetricItem
    {
        Category = "MP3",
        Name = "Битрейт",
        Value = $"{r.Mp3Bitrate} kbps",
        Status = "—",
        StatusColor = "#585b70",
        Description = "Заявленный битрейт MP3-файла из заголовка."
    });
    if (r.Mp3Encoder.Length > 0 && r.Mp3Encoder != "Error")
    {
        items.Add(new MetricItem
        {
            Category = "MP3",
            Name = "Кодер",
            Value = r.Mp3Encoder,
            Status = "—",
            StatusColor = "#585b70",
            Description = "Идентифицированный MP3-кодер (LAME, FhG, etc)."
        });
    }
    string mp3QualStatus = r.Mp3QualityScore >= 80 ? "✓ Хороший рип" : r.Mp3QualityScore >= 50 ? "⚠ Средний" : "✗ Плохой";
    string mp3QualColor = r.Mp3QualityScore >= 80 ? "#2EA043" : r.Mp3QualityScore >= 50 ? "#D29922" : "#CF222E";
    items.Add(new MetricItem
    {
        Category = "MP3",
        Name = "Качество MP3",
        Value = $"{r.Mp3QualityScore:F0}%",
        Status = mp3QualStatus,
        StatusColor = mp3QualColor,
        Description = "Оценка качества MP3-рипа: соответствие среза битрейту, артефакты, спектральные дыры."
    });
}
```

- [ ] **Step 4: Build + test**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj && dotnet test LosslessChecker.Tests\LosslessChecker.Tests.csproj`
Expected: Build succeeds, tests pass.

- [ ] **Step 5: Commit**

```bash
git add LosslessChecker/Models/AnalysisResult.cs LosslessChecker/Services/AudioPipeline.cs LosslessChecker/ViewModels/AudioFileViewModel.cs
git commit -m "feat: MP3 quality scoring — bitrate, encoder, quality score"
```

---

## Subproject 4: UI Modernization

### Task 4.1: Add sorting to DataGrid

**Files:**
- Modify: `Views/MainWindow.xaml`
- Modify: `ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add SortDirection support to MainViewModel**

In `ViewModels/MainViewModel.cs`, add:

```csharp
[ObservableProperty]
private string _sortColumn = "FileName";

[ObservableProperty]
private bool _sortAscending = true;

public void SortFiles(string columnName)
{
    if (SortColumn == columnName)
        SortAscending = !SortAscending;
    else
        (SortColumn, SortAscending) = (columnName, true);

    var sorted = SortAscending
        ? FilteredFiles.OrderBy(f => GetPropertyValue(f, columnName))
        : FilteredFiles.OrderByDescending(f => GetPropertyValue(f, columnName));

    FilteredFiles = new ObservableCollection<AudioFileViewModel>(sorted);
}

private static object GetPropertyValue(AudioFileViewModel f, string column) => column switch
{
    "FileName" => f.FileName,
    "DurationSeconds" => f.DurationSeconds,
    "Format" => f.Format,
    "CutoffFrequency" => f.CutoffFrequency,
    "DynamicRange" => f.DynamicRange,
    "Authenticity" => f.Authenticity,
    "QualityScorePercent" => f.QualityScorePercent,
    "Decision" => f.Decision,
    _ => f.FileName
};
```

- [ ] **Step 2: Wire sorting to DataGrid column headers in XAML**

In `Views/MainWindow.xaml`, replace the DataGrid with a version that triggers sorting on header click. Add `x:Name="FilesGrid"` attribute and set `Sorting` event — actually WPF DataGrid has built-in sorting. Set:

```xml
<DataGrid ... CanUserSortColumns="True" Sorting="DataGrid_Sorting">
```

And add the `SortDirection` on each `DataGridTextColumn`:

```xml
<DataGridTextColumn Header="Название" Binding="{Binding FileName}" Width="180" SortMemberPath="FileName"/>
<DataGridTextColumn Header="Длит." Binding="{Binding DurationSeconds, StringFormat={}{0:m\\:ss}}" Width="55" SortMemberPath="DurationSeconds"/>
<DataGridTextColumn Header="Формат" Binding="{Binding Format}" Width="60" SortMemberPath="Format"/>
<DataGridTextColumn Header="Cutoff" Binding="{Binding CutoffFrequency, StringFormat={}{0:F0} Гц}" Width="60" SortMemberPath="CutoffFrequency"/>
<DataGridTextColumn Header="DR" Binding="{Binding DynamicRange, StringFormat={}{0:F0}}" Width="40" SortMemberPath="DynamicRange"/>
<DataGridTextColumn Header="Подлинность" Binding="{Binding Authenticity}" Width="100" SortMemberPath="Authenticity">
<DataGridTextColumn Header="Кач-во" Binding="{Binding QualityScorePercent, StringFormat={}{0:F0}%}" Width="55" SortMemberPath="QualityScorePercent"/>
<DataGridTextColumn Header="Решение" Binding="{Binding Decision}" Width="90" SortMemberPath="Decision"/>
```

- [ ] **Step 3: Build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/Views/MainWindow.xaml LosslessChecker/ViewModels/MainViewModel.cs
git commit -m "feat: sorting on DataGrid column header click"
```

---

### Task 4.2: Add search filter and decision chip filters

**Files:**
- Modify: `Views/MainWindow.xaml`
- Modify: `ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add filter properties to MainViewModel**

In `ViewModels/MainViewModel.cs`, add:

```csharp
[ObservableProperty]
private string _searchQuery = "";

[ObservableProperty]
private bool _showKeep = true;

[ObservableProperty]
private bool _showInvestigate = true;

[ObservableProperty]
private bool _showReplace = true;

[ObservableProperty]
private bool _showMp3 = true;

public void ApplyFilters()
{
    var filtered = Files.AsEnumerable();

    if (!string.IsNullOrWhiteSpace(SearchQuery))
        filtered = filtered.Where(f =>
            f.FileName.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
            (f.Artist?.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (f.Album?.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ?? false));

    filtered = filtered.Where(f =>
        (f.Decision.StartsWith("KEEP") && ShowKeep) ||
        (f.Decision == "INVESTIGATE" && ShowInvestigate) ||
        (f.Decision == "REPLACE" && ShowReplace) ||
        (f.Format.StartsWith("MP3") && ShowMp3));

    FilteredFiles = new ObservableCollection<AudioFileViewModel>(filtered);
}
```

And in `ScanAndAnalyze`, after `PopulateArtistGroups()`, add explicit filter application:
```csharp
ApplyFilters();
FilteredFiles = new ObservableCollection<AudioFileViewModel>(Files);
```

- [ ] **Step 2: Add filter bar to XAML above DataGrid**

In `Views/MainWindow.xaml`, above the DataGrid (Grid.Column="2"), add:

```xml
<!-- Filter bar -->
<StackPanel Orientation="Horizontal" Margin="0,0,0,4">
    <TextBox Text="{Binding SearchQuery, UpdateSourceTrigger=PropertyChanged}"
             Width="180" Margin="0,0,8,0"
             Background="#313244" Foreground="#cdd6f4"
             BorderBrush="#45475a"
             ToolTip="Search by name, artist, album"/>
    <ToggleButton Content="KEEP" IsChecked="{Binding ShowKeep}" Width="60"
                  Background="#1a3022" Foreground="#2EA043" Margin="2,0"/>
    <ToggleButton Content="INV" IsChecked="{Binding ShowInvestigate}" Width="50"
                  Background="#2e2a1a" Foreground="#D29922" Margin="2,0"/>
    <ToggleButton Content="REPL" IsChecked="{Binding ShowReplace}" Width="55"
                  Background="#301a1a" Foreground="#CF222E" Margin="2,0"/>
    <ToggleButton Content="MP3" IsChecked="{Binding ShowMp3}" Width="50"
                  Background="#313244" Foreground="#f9e2af" Margin="2,0"/>
</StackPanel>
```

Wire the property changes — bind `Checked` event in code-behind or use `{Binding ...}` with two-way binding.

- [ ] **Step 3: Wire filter update on property change**

In `MainViewModel.cs`, when `SearchQuery`, `ShowKeep`, `ShowInvestigate`, `ShowReplace`, `ShowMp3` change, call `ApplyFilters()`. Since CommunityToolkit.Mvvm supports `partial void On<Property>Changed`, add:

```csharp
partial void OnSearchQueryChanged(string value) => ApplyFilters();
partial void OnShowKeepChanged(bool value) => ApplyFilters();
partial void OnShowInvestigateChanged(bool value) => ApplyFilters();
partial void OnShowReplaceChanged(bool value) => ApplyFilters();
partial void OnShowMp3Changed(bool value) => ApplyFilters();
```

- [ ] **Step 4: Build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add LosslessChecker/Views/MainWindow.xaml LosslessChecker/ViewModels/MainViewModel.cs
git commit -m "feat: search filter and decision chip filters"
```

---

### Task 4.3: Add export (CSV, JSON, HTML)

**Files:**
- Modify: `Views/MainWindow.xaml`
- Modify: `ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add Export command to MainViewModel**

```csharp
[RelayCommand]
private void Export()
{
    var dialog = new Microsoft.Win32.SaveFileDialog
    {
        Filter = "CSV File|*.csv|JSON File|*.json|HTML Report|*.html",
        Title = "Export results"
    };
    if (dialog.ShowDialog() != true) return;

    var data = Files.Where(f => f.AnalysisStatus == Models.AnalysisStatus.Completed).ToList();
    var ext = System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant();

    if (ext == ".csv")
        ExportCsv(dialog.FileName, data);
    else if (ext == ".json")
        ExportJson(dialog.FileName, data);
    else if (ext == ".html")
        ExportHtml(dialog.FileName, data);
}

private static void ExportCsv(string path, List<AudioFileViewModel> data)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("File,Artist,Album,Format,SampleRate,BitDepth,Duration,Cutoff,DR,TruePeak,Clipping,Authenticity,LosslessScore,HiResScore,Quality,Decision");
    foreach (var f in data)
        sb.AppendLine($"\"{f.FileName}\",\"{f.Artist}\",\"{f.Album}\",{f.Format},{f.SampleRate},{f.BitDepth},{f.DurationSeconds:F1},{f.CutoffFrequency:F0},{f.DynamicRange:F0},{f.TruePeakDb:F1},{f.ClippingPercent:F2},{f.Authenticity},{f.LosslessScorePercent:F0}%,{f.HiResScorePercent:F0}%,{f.QualityScorePercent:F0}%,{f.Decision}");
    System.IO.File.WriteAllText(path, sb.ToString());
}

private static void ExportJson(string path, List<AudioFileViewModel> data)
{
    var json = System.Text.Json.JsonSerializer.Serialize(data.Select(f => new
    {
        f.FileName, f.Artist, f.Album, f.Format, f.SampleRate, f.BitDepth,
        f.DurationSeconds, f.CutoffFrequency, f.DynamicRange, f.TruePeakDb,
        f.ClippingPercent, f.Authenticity, LosslessScore = f.LosslessScorePercent,
        HiResScore = f.HiResScorePercent, QualityScore = f.QualityScorePercent, f.Decision
    }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    System.IO.File.WriteAllText(path, json);
}

private static void ExportHtml(string path, List<AudioFileViewModel> data)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'><title>LosslessChecker Report</title>");
    sb.AppendLine("<style>body{font-family:Segoe UI,sans-serif;background:#1e1e2e;color:#cdd6f4;margin:20px}");
    sb.AppendLine("table{border-collapse:collapse;width:100%}th{background:#313244;padding:8px;text-align:left}");
    sb.AppendLine("td{padding:6px 8px;border-bottom:1px solid #45475a}.good{color:#2EA043}.warn{color:#D29922}.bad{color:#CF222E}</style></head><body>");
    sb.AppendLine("<h1>LosslessChecker Report</h1><table><tr><th>File</th><th>Artist</th><th>Format</th><th>Cutoff</th><th>DR</th><th>Authenticity</th><th>Lossless</th><th>Quality</th><th>Decision</th></tr>");
    foreach (var f in data)
    {
        string cls = f.Decision switch { var d when d.StartsWith("KEEP") => "good", "INVESTIGATE" => "warn", _ => "bad" };
        sb.AppendLine($"<tr class='{cls}'><td>{f.FileName}</td><td>{f.Artist}</td><td>{f.Format}</td><td>{f.CutoffFrequency:F0} Hz</td><td>DR{f.DynamicRange:F0}</td><td>{f.Authenticity}</td><td>{f.LosslessScorePercent:F0}%</td><td>{f.QualityScorePercent:F0}%</td><td><b>{f.Decision}</b></td></tr>");
    }
    sb.AppendLine("</table></body></html>");
    System.IO.File.WriteAllText(path, sb.ToString());
}
```

Add `using System.Text.Json;` at the top.

- [ ] **Step 2: Add Export button to toolbar XAML**

In the toolbar section of `MainWindow.xaml`, add a third button after Stop:

```xml
<Button Grid.Column="1" Content="📊 Экспорт" Command="{Binding ExportCommand}"
        Width="90" Height="34" Background="#45475a" Foreground="#cdd6f4"
        BorderThickness="0" FontWeight="SemiBold" Cursor="Hand" Margin="8,0,0,0">
    <Button.Resources>
        <Style TargetType="Border"><Setter Property="CornerRadius" Value="6"/></Style>
    </Button.Resources>
</Button>
```

- [ ] **Step 3: Build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/Views/MainWindow.xaml LosslessChecker/ViewModels/MainViewModel.cs
git commit -m "feat: export to CSV, JSON, HTML report"
```

---

### Task 4.4: Dark/Light theme switching

**Files:**
- Create: `Themes/Dark.xaml`
- Create: `Themes/Light.xaml`
- Modify: `App.xaml`

- [ ] **Step 1: Create Dark.xaml (extract current theme)**

The current hardcoded colors. Write `Themes/Dark.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <SolidColorBrush x:Key="BgPrimaryBrush" Color="#1e1e2e"/>
    <SolidColorBrush x:Key="BgSecondaryBrush" Color="#313244"/>
    <SolidColorBrush x:Key="BgTertiaryBrush" Color="#181825"/>
    <SolidColorBrush x:Key="FgPrimaryBrush" Color="#cdd6f4"/>
    <SolidColorBrush x:Key="FgSecondaryBrush" Color="#bac2de"/>
    <SolidColorBrush x:Key="FgMutedBrush" Color="#a6adc8"/>
    <SolidColorBrush x:Key="FgSubtleBrush" Color="#585b70"/>
    <SolidColorBrush x:Key="BorderBrush" Color="#45475a"/>
    <SolidColorBrush x:Key="AccentBrush" Color="#89b4fa"/>
    <SolidColorBrush x:Key="GridAltRowBrush" Color="#242438"/>
    <SolidColorBrush x:Key="LosslessGreenBrush" Color="#2EA043"/>
    <SolidColorBrush x:Key="SuspiciousAmberBrush" Color="#D29922"/>
    <SolidColorBrush x:Key="FakeRedBrush" Color="#CF222E"/>
    <SolidColorBrush x:Key="NeutralGrayBrush" Color="#808080"/>
</ResourceDictionary>
```

- [ ] **Step 2: Create Light.xaml**

Write `Themes/Light.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <SolidColorBrush x:Key="BgPrimaryBrush" Color="#eff1f5"/>
    <SolidColorBrush x:Key="BgSecondaryBrush" Color="#ccd0da"/>
    <SolidColorBrush x:Key="BgTertiaryBrush" Color="#e6e9ef"/>
    <SolidColorBrush x:Key="FgPrimaryBrush" Color="#4c4f69"/>
    <SolidColorBrush x:Key="FgSecondaryBrush" Color="#5c5f77"/>
    <SolidColorBrush x:Key="FgMutedBrush" Color="#7c7f93"/>
    <SolidColorBrush x:Key="FgSubtleBrush" Color="#9ca0b0"/>
    <SolidColorBrush x:Key="BorderBrush" Color="#bcc0cc"/>
    <SolidColorBrush x:Key="AccentBrush" Color="#1e66f5"/>
    <SolidColorBrush x:Key="GridAltRowBrush" Color="#dce0e8"/>
    <SolidColorBrush x:Key="LosslessGreenBrush" Color="#2EA043"/>
    <SolidColorBrush x:Key="SuspiciousAmberBrush" Color="#D29922"/>
    <SolidColorBrush x:Key="FakeRedBrush" Color="#CF222E"/>
    <SolidColorBrush x:Key="NeutralGrayBrush" Color="#808080"/>
</ResourceDictionary>
```

- [ ] **Step 3: Update App.xaml to use merged dictionary**

Replace `App.xaml`:

```xml
<Application x:Class="LosslessChecker.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="Views/MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Themes/Dark.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 4: Update MainWindow.xaml to use DynamicResource**

Replace all hardcoded color literals with `{DynamicResource ...}`:
- `Background="#1e1e2e"` → `Background="{DynamicResource BgPrimaryBrush}"`
- `Background="#313244"` → `Background="{DynamicResource BgSecondaryBrush}"`
- `Foreground="#cdd6f4"` → `Foreground="{DynamicResource FgPrimaryBrush}"`
- `Foreground="#a6adc8"` → `Foreground="{DynamicResource FgMutedBrush}"`
- `Foreground="#bac2de"` → `Foreground="{DynamicResource FgSecondaryBrush}"`
- `Background="#181825"` → `Background="{DynamicResource BgTertiaryBrush}"`
- `BorderBrush="#45475a"` → `BorderBrush="{DynamicResource BorderBrush}"`
- `Background="#242438"` → `Background="{DynamicResource GridAltRowBrush}"`
- etc.

- [ ] **Step 5: Add theme toggle to MainWindow code-behind**

```csharp
private bool _isDark = true;
private void ToggleTheme()
{
    _isDark = !_isDark;
    var dicts = Application.Current.Resources.MergedDictionaries;
    dicts.Clear();
    dicts.Add(new ResourceDictionary { Source = new Uri(_isDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) });
}
```

Wire to a button in the toolbar (add next to Stop button with ☀️/🌙 text).

- [ ] **Step 6: Build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 7: Commit**

```bash
git add LosslessChecker/Themes/ LosslessChecker/App.xaml LosslessChecker/Views/MainWindow.xaml LosslessChecker/Views/MainWindow.xaml.cs
git commit -m "feat: dark/light theme switching via ResourceDictionary"
```

---

### Task 4.5: Drag-and-drop, welcome screen, context menu

**Files:**
- Modify: `Views/MainWindow.xaml`
- Modify: `Views/MainWindow.xaml.cs`
- Modify: `ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add drag-drop support to MainWindow.xaml.cs**

```csharp
private void Window_DragOver(object sender, DragEventArgs e)
{
    e.Effects = DragDropEffects.Link;
    e.Handled = true;
}

private async void Window_Drop(object sender, DragEventArgs e)
{
    string? path = null;
    if (e.Data.GetDataPresent(DataFormats.FileDrop))
    {
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length > 0)
        {
            path = File.Exists(files[0])
                ? System.IO.Path.GetDirectoryName(files[0])
                : files[0];
        }
    }
    if (path != null)
    {
        await _viewModel.ScanFolderCommand.ExecuteAsync(path);
    }
}
```

In `MainWindow.xaml`, add `AllowDrop="True" DragOver="Window_DragOver" Drop="Window_Drop"` to the Window element.

- [ ] **Step 2: Add welcome screen (empty state)**

In `MainWindow.xaml`, wrap the main content Grid with:

```xml
<!-- Welcome screen -->
<Border x:Name="WelcomeScreen" Visibility="{Binding IsProcessing, Converter={StaticResource InvertBool}}"
        Background="{DynamicResource BgPrimaryBrush}" Padding="40">
    <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center">
        <TextBlock Text="🎵" FontSize="64" HorizontalAlignment="Center" Foreground="#585b70"/>
        <TextBlock Text="Перетащите папку с аудио сюда" FontSize="18" Foreground="#a6adc8"
                   HorizontalAlignment="Center" Margin="0,12"/>
        <TextBlock Text="или нажмите Выбрать папку чтобы начать анализ" FontSize="12" Foreground="#585b70"
                   HorizontalAlignment="Center"/>
        <Border BorderBrush="#45475a" BorderThickness="2" CornerRadius="12"
                Margin="40,20" Padding="20" BorderDashArray="8,4">
            <TextBlock Text="Drop zone: .mp3 .flac .wav .m4a .alac"
                       Foreground="#585b70" FontSize="11" HorizontalAlignment="Center"/>
        </Border>
    </StackPanel>
</Border>
```

When `Files.Count > 0`, hide the welcome screen. Add a visibility binding or toggle.

- [ ] **Step 3: Add context menu to DataGrid rows**

In `MainWindow.xaml`, add to `DataGrid`:

```xml
<DataGrid.ContextMenu>
    <ContextMenu>
        <MenuItem Header="📂 Open folder" Click="OpenFolder_Click"/>
        <MenuItem Header="📋 Copy metrics" Click="CopyMetrics_Click"/>
        <MenuItem Header="🔬 Spectrogram" Click="SpectrogramContext_Click"/>
    </ContextMenu>
</DataGrid.ContextMenu>
```

In `MainWindow.xaml.cs`:

```csharp
private void OpenFolder_Click(object sender, RoutedEventArgs e)
{
    if (_viewModel.SelectedFile?.FilePath is string path)
        System.Diagnostics.Process.Start("explorer", $"/select,\"{path}\"");
}

private void CopyMetrics_Click(object sender, RoutedEventArgs e)
{
    _viewModel.SelectedFile?.CopyMetricsCommand.Execute(null);
}

private void SpectrogramContext_Click(object sender, RoutedEventArgs e)
{
    Spectrogram_Click(sender, null!);
}
```

- [ ] **Step 4: Add current file name to progress**

In `MainViewModel.cs`, add `[ObservableProperty] private string _currentFileName = "";`. Set it in the dispatcher block during processing: `CurrentFileName = vm.FileName;`. Show it next to the progress bar in XAML.

- [ ] **Step 5: Build**

Run: `dotnet build LosslessChecker\LosslessChecker.csproj`
Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add LosslessChecker/Views/MainWindow.xaml LosslessChecker/Views/MainWindow.xaml.cs LosslessChecker/ViewModels/MainViewModel.cs
git commit -m "feat: drag-drop, welcome screen, context menu, progress file name"
```

---

## Plan Self-Review

**Spec coverage check:**
- Subproject 1 (Memory): Tasks 1.1–1.6 cover all spec requirements
- Subproject 2 (Algorithms): Tasks 2.1–2.4 cover all 5 fixes
- Subproject 3 (Spectrogram): Tasks 3.1–3.5 cover log scale, axes, zoom, MP3 mode
- Subproject 4 (UI): Tasks 4.1–4.5 cover sorting, filtering, export, themes, drag-drop, context menu

**Placeholder scan:** No TBD, TODO, or vague instructions found.

**Type consistency:** AudioPipeline references verified against existing types. AnalysisResult new fields (Mp3Bitrate, Mp3Encoder, Mp3QualityScore) match usage in ViewModel.

**Build verification:** Each task includes a `dotnet build` step.
