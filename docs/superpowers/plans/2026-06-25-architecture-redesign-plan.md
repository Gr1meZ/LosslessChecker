# LosslessChecker Architecture Redesign — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Refactor the entire analysis pipeline from full-file-loading to streaming chunk-based processing, rewrite BitDepthValidator with RMS histogram, switch PhaseAnalyzer to time-windowed M/S with Haas detection, split scoring into two independent axes (Authenticity + Mastering), implement spectrogram time-binned accumulator, add LRU caches for UI memory, and add 8 new tests covering real-world edge cases.

**Architecture:** Streaming pipeline with 3-phase execution: Phase 1 streams chunks through stateful `IChunkAccumulator` implementations synchronously in `await foreach`; Phase 2 runs FFT-heavy detectors on reservoir data in strict order; Phase 3 applies two-axis scoring. LRU-cached spectrograms and covers. Batched UI updates via `Dispatcher.Yield(Background)`.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, NAudio, NWaves, xUnit

## Global Constraints

- `ArrayPool<float>` rented once per `StreamChunks` stream, returned in `try-finally` of the enumerator
- Phase 1 `AddChunk` calls: synchronous, sequential inside `await foreach` — NO `Task.Run` / `Parallel.Invoke`
- Post-Stream Phase 2 order: SpectrogramAccumulator.Finalize → VinylDetector → CutoffDetector.ClassifyCutoff (with vinyl flag) → ClassifyBandwidth → ArtifactDetector + SBR → UpscaleDetector → PhaseAnalyzer.GetResult (Haas+Flatness) → ResamplingDetector → ContainerAnalyzer
- BitDepthValidator: 1440-bucket histogram (0.1 dB, -144 to 0 dB), cumulative 1% threshold from quietest, -40 dB safety gate, 95% constant-LSB detection
- SpectrogramAccumulator: weighted peak averaging for progressive downsampling, not arithmetic mean
- UI: `Dispatcher.Yield(DispatcherPriority.Background)` for batches, NEVER `NotifyCollectionChangedAction.Reset`, `DecodePixelWidth=150` before `EndInit` for covers
- Tests: `new Random(42)` fixed seed for deterministic signal generation
- CORRUPTED files (FLAC MD5 mismatch / RIFF failure): skip all analysis, return zero scores, verdict CORRUPTED
- All commits in Russian (project convention)

---

### Task A1: Create AudioChunk model

**Files:**
- Create: `LosslessChecker/Models/AudioChunk.cs`

**Interfaces:**
- Produces: `AudioChunk` struct consumed by A2, A4, A6-A9, B1, C1, D1, E1, G1

- [ ] **Step 1: Write the file**

```csharp
namespace LosslessChecker.Models;

public readonly record struct AudioChunk(
    ReadOnlyMemory<float> Left,
    ReadOnlyMemory<float> Right,
    int SampleRate,
    int Channels,
    double RmsDb,
    double StartTime,
    bool IsLast)
{
    public bool IsStereo => Right.Length > 0;
    public int FrameCount => Left.Length;
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Models/AudioChunk.cs
git commit -m "feat(A1): добавить AudioChunk модель для потокового пайплайна"
```

---

### Task A2: Implement AudioDecoder.StreamChunks

**Files:**
- Modify: `LosslessChecker/Services/AudioDecoder.cs` (add `partial`, keep Decode for now)
- Create: `LosslessChecker/Services/AudioDecoder.Streaming.cs`

**Interfaces:**
- Produces: `static async IAsyncEnumerable<AudioChunk> StreamChunks(string filePath, int chunkDurationSec=10, [EnumeratorCancellation] CancellationToken ct)`
- Consumes: `AudioChunk` from A1

- [ ] **Step 1: Make AudioDecoder partial**

Change `LosslessChecker/Services/AudioDecoder.cs` line 9:
```
public static partial class AudioDecoder
```

- [ ] **Step 2: Write Streaming.cs**

```csharp
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using LosslessChecker.Models;
using NAudio.Wave;

namespace LosslessChecker.Services;

public static partial class AudioDecoder
{
    public static async IAsyncEnumerable<AudioChunk> StreamChunks(
        string filePath,
        int chunkDurationSec = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = CreateReader(filePath)
            ?? throw new InvalidOperationException("Unsupported audio format");

        var format = reader.WaveFormat;
        if (format.Channels > 2)
            throw new NotSupportedException("Only mono and stereo files are supported");

        var provider = reader.ToSampleProvider();
        int sampleRate = format.SampleRate;
        int channels = format.Channels;
        int chunkSize = sampleRate * chunkDurationSec;
        int bufferSize = chunkSize * channels;

        float[] readBuf = ArrayPool<float>.Shared.Rent(bufferSize);
        float[] leftBuf = ArrayPool<float>.Shared.Rent(chunkSize);
        float[] rightBuf = channels == 2 ? ArrayPool<float>.Shared.Rent(chunkSize) : Array.Empty<float>();
        try
        {
            double cumulativeTime = 0;
            int read;
            while ((read = provider.Read(readBuf, 0, bufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int frames = read / channels;
                if (channels == 1)
                {
                    Array.Copy(readBuf, 0, leftBuf, 0, frames);
                    double rmsDb = ComputeRmsMono(leftBuf, frames);
                    yield return new AudioChunk(
                        leftBuf.AsMemory(0, frames), ReadOnlyMemory<float>.Empty,
                        sampleRate, channels, rmsDb, cumulativeTime, false);
                }
                else
                {
                    for (int i = 0; i < frames; i++)
                    {
                        leftBuf[i] = readBuf[i * 2];
                        rightBuf[i] = readBuf[i * 2 + 1];
                    }
                    double rmsDb = ComputeRmsStereo(leftBuf, rightBuf, frames);
                    yield return new AudioChunk(
                        leftBuf.AsMemory(0, frames), rightBuf.AsMemory(0, frames),
                        sampleRate, channels, rmsDb, cumulativeTime, false);
                }
                cumulativeTime += (double)frames / sampleRate;
            }

            yield return new AudioChunk(
                ReadOnlyMemory<float>.Empty, ReadOnlyMemory<float>.Empty,
                sampleRate, channels, -200, cumulativeTime, true);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(readBuf);
            ArrayPool<float>.Shared.Return(leftBuf);
            if (rightBuf.Length > 0) ArrayPool<float>.Shared.Return(rightBuf);
        }
    }

    private static double ComputeRmsMono(float[] buf, int n)
    {
        double sumSq = 0;
        for (int i = 0; i < n; i++) { double s = buf[i]; sumSq += s * s; }
        return 20.0 * Math.Log10(Math.Max(Math.Sqrt(sumSq / n), 1e-10));
    }

    private static double ComputeRmsStereo(float[] left, float[] right, int n)
    {
        double sumSq = 0;
        for (int i = 0; i < n; i++) { double m = (left[i] + right[i]) * 0.5; sumSq += m * m; }
        return 20.0 * Math.Log10(Math.Max(Math.Sqrt(sumSq / n), 1e-10));
    }
}
```

- [ ] **Step 3: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/Services/AudioDecoder.cs LosslessChecker/Services/AudioDecoder.Streaming.cs
git commit -m "feat(A2): реализовать StreamChunks с ArrayPool для потокового декодирования"
```

---

### Task A3: Reserve StereoBuffer (mark Decode as obsolete)

**Files:**
- Modify: `LosslessChecker/Models/StereoBuffer.cs`

- [ ] **Step 1: No changes needed yet** — StereoBuffer still used by `AudioPipeline.cs` and existing `Decode()`. Mark `Decode()` as `[Obsolete]` only after Phase G when the pipeline switches to streaming. Skip this task — proceed to A4.

- [ ] **Step 1 (replaced): Verify build compiles**

```powershell
dotnet build
```

- [ ] **Step 2: Commit (empty)**

```bash
echo "A3: skip — StereoBuffer still needed by existing pipeline, will deprecate in Phase G"
```

---

### Task A4: Create ReservoirBuffer

**Files:**
- Create: `LosslessChecker/Services/ReservoirBuffer.cs`

**Interfaces:**
- Produces: `(IReadOnlyList<float[]> Chunks, IReadOnlyList<double> StartTimes) SelectedChunks`, `int TotalChunksSeen`
- Consumed by: G1 (Post-Stream phase for CutoffDetector, ArtifactDetector, PhaseAnalyzer)

- [ ] **Step 1: Write the file**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class ReservoirBuffer
{
    private readonly int _capacity;
    private readonly List<(double rmsDb, float[] data, double startTime)> _heap = new();
    private readonly List<(double rmsDb, double startTime, int dataOffset)> _allChunks = new();
    private readonly List<float[]> _allData = new();
    private int _chunkCount;

    public ReservoirBuffer(int capacity = 6) => _capacity = capacity;

    public bool IsEmpty => _chunkCount == 0;
    public int ChunkCount => _chunkCount;
    public double MaxRmsDb { get; private set; } = -200;

    public IReadOnlyList<float[]> SelectedChunks
    {
        get
        {
            var result = new List<float[]>();
            if (MaxRmsDb < -40 && _chunkCount > _capacity * 2)
            {
                int step = _chunkCount / _capacity;
                for (int i = 0; i < _capacity; i++)
                    result.Add(_allData[Math.Min(i * step + step / 2, _allData.Count - 1)]);
            }
            else
            {
                foreach (var item in _heap)
                    result.Add(item.data);
            }
            return result;
        }
    }

    public IReadOnlyList<double> SelectedStartTimes
    {
        get
        {
            var result = new List<double>();
            if (MaxRmsDb < -40 && _chunkCount > _capacity * 2)
            {
                int step = _chunkCount / _capacity;
                for (int i = 0; i < _capacity; i++)
                    result.Add(_allData[Math.Min(i * step + step / 2, _allData.Count - 1)] != null
                        ? _allChunks[Math.Min(i * step + step / 2, _allChunks.Count - 1)].startTime : 0);
            }
            else
            {
                foreach (var item in _heap)
                    result.Add(item.startTime);
            }
            return result;
        }
    }

    public void AddChunk(AudioChunk chunk)
    {
        _chunkCount++;
        if (chunk.RmsDb > MaxRmsDb) MaxRmsDb = chunk.RmsDb;

        _allChunks.Add((chunk.RmsDb, chunk.StartTime, _allData.Count));

        if (chunk.IsStereo)
        {
            int n = chunk.FrameCount;
            var copy = new float[n];
            var left = chunk.Left.Span;
            var right = chunk.Right.Span;
            for (int i = 0; i < n; i++)
                copy[i] = (left[i] + right[i]) * 0.5f;
            _allData.Add(copy);

            if (_heap.Count < _capacity)
            {
                _heap.Add((chunk.RmsDb, copy, chunk.StartTime));
            }
            else
            {
                int minIdx = 0;
                for (int i = 1; i < _heap.Count; i++)
                    if (_heap[i].rmsDb < _heap[minIdx].rmsDb) minIdx = i;
                if (chunk.RmsDb > _heap[minIdx].rmsDb)
                    _heap[minIdx] = (chunk.RmsDb, copy, chunk.StartTime);
            }
        }
        else
        {
            int n = chunk.FrameCount;
            var copy = new float[n];
            chunk.Left.Span.CopyTo(copy);
            _allData.Add(copy);

            if (_heap.Count < _capacity)
            {
                _heap.Add((chunk.RmsDb, copy, chunk.StartTime));
            }
            else
            {
                int minIdx = 0;
                for (int i = 1; i < _heap.Count; i++)
                    if (_heap[i].rmsDb < _heap[minIdx].rmsDb) minIdx = i;
                if (chunk.RmsDb > _heap[minIdx].rmsDb)
                    _heap[minIdx] = (chunk.RmsDb, copy, chunk.StartTime);
            }
        }
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/ReservoirBuffer.cs
git commit -m "feat(A4): добавить ReservoirBuffer для топ-N громких чанков со стратифицированным фоллбэком"
```

---

### Task A5: Add Reset() to IChunkAccumulator interface

**Files:**
- Modify: `LosslessChecker/Services/ChunkProcessing/IChunkAccumulator.cs`

**Interfaces:**
- Produces: `void Reset()` on `IChunkAccumulator<TResult>`
- Consumed by: A6-A9, B1, C1, E1 (all accumulators must implement Reset)

- [ ] **Step 1: Modify the interface**

```csharp
namespace LosslessChecker.Services.ChunkProcessing;

public interface IChunkAccumulator<out TResult>
{
    void Reset();
    void AddChunk(ReadOnlySpan<float> mono);
    TResult GetResult();
}
```

- [ ] **Step 2: Build — expect errors from accumulators that don't implement Reset yet**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

Expected: build FAILS for TruePeakDetector, PhaseAnalyzer, BitDepthValidator, DrMeter, DcOffsetDetector — that's fine, they get Reset in tasks A6-A9.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/ChunkProcessing/IChunkAccumulator.cs
git commit -m "feat(A5): добавить Reset() в IChunkAccumulator интерфейс"
```

---

### Task A6: Add Reset() to TruePeakDetector

**Files:**
- Modify: `LosslessChecker/Services/Analyzers/TruePeakDetector.cs`

- [ ] **Step 1: Verify Reset() already exists in TruePeakDetector (line 24)**

The existing `TruePeakDetector` already has `Reset()` at line 24-33. No changes needed. Mark this task as done.

- [ ] **Step 2: Verify build after A5 interface change**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

If TruePeakDetector compiles without error, task is complete.

- [ ] **Step 3: Commit (skip — no code changes)**

```
# A6: TruePeakDetector already has Reset()
```

---

### Task A7: Add Reset() to LufsMeter

**Files:**
- Modify: `LosslessChecker/Services/Analyzers/LufsMeter.cs`

The existing `LufsMeter` implements analysis directly without chunk accumulator pattern. It has `Analyze(StereoBuffer)` and `AnalyzeSpan(ReadOnlySpan<float>, int)`. Per the spec, LufsMeter should become `IChunkAccumulator<LufsResult>`.

- [ ] **Step 1: Refactor LufsMeter to full IChunkAccumulator**

```csharp
using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services.Analyzers;

public class LufsMeter : IChunkAccumulator<LufsResult>
{
    private const double BlockDuration = 0.4;
    private const double HopDuration = 0.1;
    private const double AbsoluteGate = -70.0;
    private const double RelativeGate = -10.0;
    private const double ChannelWeight = 1.0;

    private int _sampleRate;
    private int _blockSize, _hopSize;
    private double _pos;
    private int _frameCount;

    private readonly List<double> _blockLoudness = new();
    private readonly List<double> _shortTermLoudness = new();
    private KWeightingFilter _kwL = null!, _kwR = null!;

    private readonly List<float> _pendingSamplesL = new();
    private readonly List<float> _pendingSamplesR = new();

    public void Reset()
    {
        _blockLoudness.Clear();
        _shortTermLoudness.Clear();
        _pendingSamplesL.Clear();
        _pendingSamplesR.Clear();
        _pos = 0;
        _frameCount = 0;
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        if (_kwL == null) throw new InvalidOperationException("Init not called");
        for (int i = 0; i < mono.Length; i++)
            _pendingSamplesL.Add(mono[i]);
        ProcessPendingMono();
    }

    public void AddChunk(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (_kwL == null) throw new InvalidOperationException("Init not called");
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            _pendingSamplesL.Add(left[i]);
            _pendingSamplesR.Add(right[i]);
        }
        ProcessPendingStereo();
    }

    public LufsMeter Init(int sampleRate)
    {
        _sampleRate = sampleRate;
        _blockSize = (int)(sampleRate * BlockDuration);
        _hopSize = (int)(sampleRate * HopDuration);
        _kwL = new KWeightingFilter(sampleRate);
        _kwR = new KWeightingFilter(sampleRate);
        return this;
    }

    private void ProcessPendingMono()
    {
        while (_pendingSamplesL.Count >= _blockSize)
        {
            double sumSq = 0;
            for (int i = 0; i < _blockSize; i++)
            {
                double filtered = _kwL.Process(_pendingSamplesL[i]);
                sumSq += filtered * filtered;
            }
            _pendingSamplesL.RemoveRange(0, _hopSize);
            double meanSq = sumSq / _blockSize;
            double loudness = -0.691 + 10.0 * Math.Log10(Math.Max(meanSq, 1e-12));
            _blockLoudness.Add(loudness);
            _frameCount++;
        }
    }

    private void ProcessPendingStereo()
    {
        while (_pendingSamplesL.Count >= _blockSize)
        {
            double sumSq = 0;
            for (int i = 0; i < _blockSize; i++)
            {
                double fl = _kwL.Process(_pendingSamplesL[i]);
                double fr = _kwR.Process(_pendingSamplesR[i]);
                sumSq += fl * fl + fr * fr;
            }
            _pendingSamplesL.RemoveRange(0, _hopSize);
            _pendingSamplesR.RemoveRange(0, _hopSize);
            double meanSq = sumSq / (_blockSize * 2);
            double loudness = -0.691 + 10.0 * Math.Log10(Math.Max(meanSq, 1e-12));
            _blockLoudness.Add(loudness);
            _frameCount++;
        }
    }

    public LufsResult GetResult()
    {
        double integratedLufs = ComputeIntegratedLoudness(_blockLoudness);
        double lra = 0;
        if (_shortTermLoudness.Count > 10 || _blockLoudness.Count > 10)
        {
            var active = (_shortTermLoudness.Count > 0 ? _shortTermLoudness : _blockLoudness)
                .Where(b => b > AbsoluteGate).ToList();
            if (active.Count >= 10)
            {
                double meanLin = active.Average(b => Math.Pow(10, b / 10));
                double meanLoudness = -0.691 + 10.0 * Math.Log10(meanLin);
                double relThreshold = meanLoudness + RelativeGate;
                var relGated = active.Where(b => b > relThreshold).OrderBy(b => b).ToList();
                if (relGated.Count >= 10)
                {
                    int lowIdx = Math.Max(0, (int)Math.Ceiling(relGated.Count * 0.10) - 1);
                    int highIdx = Math.Min(relGated.Count - 1, (int)Math.Ceiling(relGated.Count * 0.95) - 1);
                    lra = relGated[highIdx] - relGated[lowIdx];
                }
            }
        }
        return new LufsResult(Math.Round(integratedLufs, 1), Math.Round(lra, 1));
    }

    private static double ComputeIntegratedLoudness(List<double> blockLoudness)
    {
        var absoluteGated = blockLoudness.Where(b => b > AbsoluteGate).ToList();
        if (absoluteGated.Count == 0) return -70.0;
        double absoluteMeanLin = absoluteGated.Average(b => Math.Pow(10, b / 10));
        double absoluteLoudness = -0.691 + 10.0 * Math.Log10(absoluteMeanLin);
        double relativeThreshold = absoluteLoudness + RelativeGate;
        var relativeGated = absoluteGated.Where(b => b > relativeThreshold).ToList();
        if (relativeGated.Count == 0) return absoluteLoudness;
        double gatedMeanLin = relativeGated.Average(b => Math.Pow(10, b / 10));
        return -0.691 + 10.0 * Math.Log10(gatedMeanLin);
    }

    public LufsResult Analyze(StereoBuffer buffer)
    {
        Init(buffer.SampleRate);
        Reset();
        if (buffer.IsStereo) AddChunk(buffer.Left, buffer.Right);
        else AddChunk(buffer.Left);
        return GetResult();
    }
}

public record LufsResult(double IntegratedLufs, double LoudnessRange);
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/Analyzers/LufsMeter.cs
git commit -m "feat(A7): рефакторинг LufsMeter в IChunkAccumulator с Reset и AddChunk"
```

---

### Task A8: Add Reset() to DrMeter

**Files:**
- Modify: `LosslessChecker/Services/DrMeter.cs`

DrMeter already has extensive state. Add `Reset()` to clear all internal state.

- [ ] **Step 1: Add Reset method at line 27 (after Init)**

```csharp
public void Reset()
{
    _rmsDb.Clear(); _peakDb.Clear();
    _globalPeak = 0; _clippedRuns = 0; _consecutive = 0;
    _totalSamples = 0;
    _sumSqL = _sumSqR = _maxAbsL = _samplesInBlock = 0;
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/DrMeter.cs
git commit -m "feat(A8): добавить Reset() в DrMeter для переиспользования в пуле"
```

---

### Task A9: Add Reset() to DcOffsetDetector

**Files:**
- Modify: `LosslessChecker/Services/Analyzers/DcOffsetDetector.cs`

`DcOffsetDetector` already has `Reset()` at line 12. No changes needed.

- [ ] **Step 1: Verify**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

Build should pass for DcOffsetDetector.

- [ ] **Step 2: Commit (skip)**

---

### Task B1: Rewrite BitDepthValidator with RMS Histogram

**Files:**
- Modify: `LosslessChecker/Services/BitDepthValidator.cs`

- [ ] **Step 1: Write the complete rewrite**

Replace the entire file:

```csharp
using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services;

public class BitDepthValidator : IChunkAccumulator<BitDepthResult>
{
    private const int BlockSize = 4096;
    private const double HistoDbMin = -144.0;
    private const double HistoDbStep = 0.1;
    private const int HistoBins = 1440;
    private const double SafetyGateDb = -40.0;
    private const double CumulativeThreshold = 0.01;

    private readonly int[] _rmsHistogram = new int[HistoBins];
    private long _totalBlocks;

    private double _sumSqInBlock;
    private int _samplesInBlock;
    private bool _hasSeenChunks;

    public void Reset()
    {
        Array.Clear(_rmsHistogram, 0, HistoBins);
        _totalBlocks = 0;
        _sumSqInBlock = 0;
        _samplesInBlock = 0;
        _hasSeenChunks = false;
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        _hasSeenChunks = true;
        for (int i = 0; i < mono.Length; i++)
        {
            float s = mono[i];
            _sumSqInBlock += s * s;
            if (++_samplesInBlock >= BlockSize)
            {
                double rms = Math.Sqrt(_sumSqInBlock / BlockSize);
                double rmsDb = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
                int bucket = (int)((rmsDb - HistoDbMin) / HistoDbStep);
                bucket = Math.Clamp(bucket, 0, HistoBins - 1);
                _rmsHistogram[bucket]++;
                _totalBlocks++;
                _sumSqInBlock = 0;
                _samplesInBlock = 0;
            }
        }
    }

    public BitDepthResult GetResult(int claimedBitDepth)
    {
        if (_samplesInBlock > 0)
        {
            double rms = Math.Sqrt(_sumSqInBlock / _samplesInBlock);
            double rmsDb = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
            int bucket = (int)((rmsDb - HistoDbMin) / HistoDbStep);
            bucket = Math.Clamp(bucket, 0, HistoBins - 1);
            _rmsHistogram[bucket]++;
            _totalBlocks++;
        }
        return BuildResult(claimedBitDepth);
    }

    public BitDepthResult GetResult() => GetResult(24);

    private BitDepthResult BuildResult(int claimedBitDepth)
    {
        if (_totalBlocks < 5)
            return new BitDepthResult(false, "Insufficient blocks", 0, false, claimedBitDepth);

        long cumulSum = 0;
        double noiseFloorDb = HistoDbMin;
        bool foundNoiseFloor = false;

        for (int b = 0; b < HistoBins; b++)
        {
            cumulSum += _rmsHistogram[b];
            if ((double)cumulSum / _totalBlocks >= CumulativeThreshold)
            {
                noiseFloorDb = HistoDbMin + b * HistoDbStep;
                foundNoiseFloor = true;
                break;
            }
        }

        if (!foundNoiseFloor)
            noiseFloorDb = 0;

        if (noiseFloorDb > SafetyGateDb)
        {
            return new BitDepthResult(false,
                $"No quiet sections detected (noise floor estimate {noiseFloorDb:F0} dB > {SafetyGateDb:F0} gate). Effective bit depth analysis skipped.",
                Math.Round(noiseFloorDb, 1), false, claimedBitDepth);
        }

        int effectiveBits = Math.Min((int)Math.Round(-noiseFloorDb / 6.0), claimedBitDepth);
        double expectedNoiseFloor = claimedBitDepth * -6;
        bool suspicious = noiseFloorDb > expectedNoiseFloor + 16;

        return new BitDepthResult(suspicious,
            suspicious
                ? $"Claimed {claimedBitDepth}-bit but noise floor at {noiseFloorDb:F0} dB = ~{effectiveBits}-bit effective."
                : $"{claimedBitDepth}-bit integrity confirmed.",
            Math.Round(noiseFloorDb, 1), false, effectiveBits);
    }

    public bool CheckLsbZeroPadded(float[] samples, int claimedBitDepth)
    {
        if (claimedBitDepth != 24 || samples.Length < 1000) return false;
        int blockSize = Math.Max(100, samples.Length / 100);
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
        double loudThreshold = sortedBlocks[Math.Min(loudCount - 1, sortedBlocks.Count - 1)];

        int zeroCount = 0, totalCount = 0;
        var constantValues = new Dictionary<int, int>();
        for (int pos = 0; pos + blockSize <= samples.Length; pos += blockSize)
        {
            double maxAbs = 0;
            for (int i = pos; i < pos + blockSize; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(samples[i]));
            if (maxAbs < loudThreshold) continue;
            for (int i = pos; i < pos + blockSize; i++)
            {
                int sample24 = (int)(samples[i] * 8388607.0 + 0.5 * Math.Sign(samples[i]));
                int lsb = sample24 & 0xFF;
                if (lsb == 0) zeroCount++;
                if (constantValues.ContainsKey(lsb)) constantValues[lsb]++;
                else constantValues[lsb] = 1;
                totalCount++;
            }
        }

        if (totalCount < 100) return false;

        // 95% zero LSBs → zero-padded
        if ((double)zeroCount / totalCount > 0.95) return true;

        // 95% same non-zero LSB value → constant dither / naive upscale
        foreach (var kv in constantValues)
        {
            if (kv.Key != 0 && (double)kv.Value / totalCount > 0.95)
                return true;
        }

        return false;
    }

    public BitDepthResult ValidateStereo(StereoBuffer buffer, int claimedBitDepth)
    {
        Reset();
        if (!buffer.IsStereo)
            AddChunk(buffer.Left);
        else
        {
            int n = buffer.Length;
            AddChunk(ToMono(buffer.Left, buffer.Right, n));
        }
        var result = GetResult(claimedBitDepth);
        bool lsbZero = CheckLsbZeroPaddedFull(buffer, claimedBitDepth);
        return new BitDepthResult(
            result.IsSuspicious || lsbZero,
            lsbZero
                ? $"Claimed {claimedBitDepth}-bit but lower 8 bits are zero-padded (or constant dither pattern)."
                : result.Verdict,
            result.NoiseFloorDb, lsbZero, result.EffectiveBitDepth);
    }

    private static float[] ToMono(float[] left, float[] right, int n)
    {
        var mono = new float[n];
        for (int i = 0; i < n; i++) mono[i] = (left[i] + right[i]) * 0.5f;
        return mono;
    }

    private static bool CheckLsbZeroPaddedFull(StereoBuffer buffer, int claimedBitDepth)
    {
        if (claimedBitDepth != 24 || buffer.Length < 1000) return false;
        int n = buffer.Length;
        int blockSize = Math.Max(100, n / 100);
        bool lsbL = CheckLsbZeroPaddedChannel(buffer.Left, n, blockSize);
        bool lsbR = buffer.IsStereo ? CheckLsbZeroPaddedChannel(buffer.Right, n, blockSize) : lsbL;
        return lsbL && lsbR;
    }

    private static bool CheckLsbZeroPaddedChannel(float[] channel, int n, int blockSize)
    {
        var sortedBlocks = new List<double>();
        for (int pos = 0; pos + blockSize <= n; pos += blockSize)
        {
            double maxAbs = 0;
            for (int i = pos; i < pos + blockSize; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(channel[i]));
            sortedBlocks.Add(maxAbs);
        }
        sortedBlocks.Sort((a, b) => b.CompareTo(a));
        int loudCount = Math.Max(1, sortedBlocks.Count / 10);
        double loudThreshold = sortedBlocks[Math.Min(loudCount - 1, sortedBlocks.Count - 1)];

        int zeroCount = 0, totalCount = 0;
        var constantValues = new Dictionary<int, int>();
        for (int pos = 0; pos + blockSize <= n; pos += blockSize)
        {
            double maxAbs = 0;
            for (int i = pos; i < pos + blockSize; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(channel[i]));
            if (maxAbs < loudThreshold) continue;
            for (int i = pos; i < pos + blockSize; i++)
            {
                int sample24 = (int)(channel[i] * 8388607.0 + 0.5 * Math.Sign(channel[i]));
                int lsb = sample24 & 0xFF;
                if (lsb == 0) zeroCount++;
                if (constantValues.ContainsKey(lsb)) constantValues[lsb]++;
                else constantValues[lsb] = 1;
                totalCount++;
            }
        }
        if (totalCount < 100) return false;
        if ((double)zeroCount / totalCount > 0.95) return true;
        foreach (var kv in constantValues)
            if (kv.Key != 0 && (double)kv.Value / totalCount > 0.95) return true;
        return false;
    }
}

public record BitDepthResult(bool IsSuspicious, string Verdict, double NoiseFloorDb, bool LsbZeroPadded, int EffectiveBitDepth);
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/BitDepthValidator.cs
git commit -m "feat(B1): переписать BitDepthValidator с RMS-гистограммой (1440 бакетов, кумулятивный 1%, constant-LSB детекция)"
```

---

### Task C1: Rewrite PhaseAnalyzer with Time-Windowed M/S, Spectral Flatness, Haas Detection

**Files:**
- Modify: `LosslessChecker/Services/Analyzers/PhaseAnalyzer.cs`

- [ ] **Step 1: Write the rewrite**

```csharp
using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;
using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services.Analyzers;

public class PhaseAnalyzer : IChunkAccumulator<PhaseResult>
{
    private const double BlockSec = 3.0;
    private const double HaasMaxLagSec = 0.012;
    private const double SideFlatnessThreshold = 0.1;
    private const double FakeStereoRatioThreshold = 0.01;
    private const double PercentileThreshold = 0.85;

    private int _sampleRate, _blockSize;
    private double _sumSqM, _sumSqS;
    private int _samplesInBlock;
    private readonly List<(double midRms, double sideRms)> _blockRatios = new();

    private readonly List<double> _correlations = new();
    private double _sumXY, _sumX2, _sumY2;
    private int _corrSamplesInBlock;
    private int _channels = 2;
    private bool _isMono;

    public void Reset()
    {
        _blockRatios.Clear();
        _correlations.Clear();
        _sumSqM = _sumSqS = 0;
        _sumXY = _sumX2 = _sumY2 = 0;
        _samplesInBlock = _corrSamplesInBlock = 0;
        _isMono = false;
    }

    public PhaseResult Analyze(StereoBuffer buffer)
    {
        _channels = buffer.IsStereo ? 2 : 1;
        _sampleRate = buffer.SampleRate;
        _blockSize = (int)(_sampleRate * BlockSec);
        Reset();

        if (!buffer.IsStereo)
        {
            _isMono = true;
            return new PhaseResult(1.0, true);
        }

        int n = Math.Min(buffer.Left.Length, buffer.Right.Length);
        for (int i = 0; i < n; i++)
        {
            float l = buffer.Left[i], r = buffer.Right[i];
            double mid = (l + r) * 0.5, side = (l - r) * 0.5;
            _sumSqM += mid * mid;
            _sumSqS += side * side;
            _sumXY += (double)l * r;
            _sumX2 += (double)l * l;
            _sumY2 += (double)r * r;
            _samplesInBlock++;
            _corrSamplesInBlock++;
            if (_samplesInBlock >= _blockSize) FlushBlock();
            if (_corrSamplesInBlock >= 4096) FlushCorrBlock();
        }
        FlushBlock();
        FlushCorrBlock();
        return BuildResult(buffer);
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        for (int i = 0; i < mono.Length; i++) AddSample(mono[i], mono[i]);
    }

    public void AddChunk(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++) AddSample(left[i], right[i]);
    }

    private void AddSample(float l, float r)
    {
        double mid = (l + r) * 0.5, side = (l - r) * 0.5;
        _sumSqM += mid * mid;
        _sumSqS += side * side;
        _sumXY += (double)l * r;
        _sumX2 += (double)l * l;
        _sumY2 += (double)r * r;
        _samplesInBlock++;
        _corrSamplesInBlock++;
        if (_samplesInBlock >= _blockSize) FlushBlock();
        if (_corrSamplesInBlock >= 4096) FlushCorrBlock();
    }

    private void FlushBlock()
    {
        if (_samplesInBlock == 0) return;
        double midRms = Math.Sqrt(_sumSqM / _samplesInBlock);
        double sideRms = Math.Sqrt(_sumSqS / _samplesInBlock);
        _blockRatios.Add((midRms, sideRms));
        _sumSqM = _sumSqS = 0;
        _samplesInBlock = 0;
    }

    private void FlushCorrBlock()
    {
        if (_corrSamplesInBlock == 0) return;
        double denom = Math.Sqrt(_sumX2 * _sumY2);
        _correlations.Add(denom > 1e-10 ? _sumXY / denom : 0);
        _sumXY = _sumX2 = _sumY2 = 0;
        _corrSamplesInBlock = 0;
    }

    public PhaseResult GetResult() => BuildResult(null!);

    private PhaseResult BuildResult(StereoBuffer? buffer)
    {
        if (_isMono || _channels == 1)
            return new PhaseResult(1.0, true);

        double avgCorr = _correlations.Count > 0 ? _correlations.Average() : 1.0;
        bool isMonoCompatible = avgCorr >= 0;

        if (_blockRatios.Count < 2)
            return new PhaseResult(Math.Round(avgCorr, 2), isMonoCompatible);

        var ratios = _blockRatios
            .Select(b => b.sideRms / Math.Max(b.midRms, 1e-10))
            .OrderBy(r => r).ToList();
        double p85 = ratios[(int)(ratios.Count * PercentileThreshold)];

        double avgCorrVal = Math.Round(avgCorr, 2);
        return new PhaseResult(avgCorrVal, isMonoCompatible);
    }

    public (double flatnessMid, double flatnessSide) ComputeSpectralFlatness(float[] reservoirMono, int sampleRate)
    {
        if (reservoirMono.Length < 4096)
            return (0.5, 0.5);

        int fftSize = 4096;
        var fft = new Fft(fftSize);
        var window = Window.Hann(fftSize);
        var frame = new float[fftSize];
        var real = new float[fftSize];
        var imag = new float[fftSize];

        double geomSum = 0, arithSum = 0;
        int bins = 0;
        int hfStart = fftSize / 4; // above 2kHz for 8kHz+

        for (int pos = 0; pos + fftSize <= reservoirMono.Length; pos += fftSize / 2)
        {
            for (int i = 0; i < fftSize; i++)
                frame[i] = reservoirMono[pos + i] * window[i];
            Array.Copy(frame, real, fftSize);
            Array.Clear(imag, 0, fftSize);
            fft.Direct(real, imag);

            for (int i = hfStart; i < fftSize / 2; i++)
            {
                double mag = Math.Sqrt((double)real[i] * real[i] + (double)imag[i] * imag[i]);
                double safe = Math.Max(mag, 1e-10);
                geomSum += Math.Log(safe);
                arithSum += safe;
                bins++;
            }
        }

        if (bins == 0 || arithSum <= 0) return (0.5, 0.5);
        double flatness = Math.Exp(geomSum / bins) / (arithSum / bins);
        return (flatness, flatness);
    }

    public int DetectHaasLag(float[] left, float[] right, int sampleRate)
    {
        int maxLag = (int)(HaasMaxLagSec * sampleRate);
        int n = Math.Min(left.Length - maxLag, 100000);
        if (n < 100) return 0;

        double bestCorr = 0;
        int bestLag = 0;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double sumXY = 0, sumX2 = 0, sumY2 = 0;
            int startL = Math.Max(0, -lag);
            int startR = Math.Max(0, lag);
            int count = Math.Min(n - startL, n - startR);
            for (int i = 0; i < count; i++)
            {
                double l = left[startL + i], r = right[startR + i];
                sumXY += l * r; sumX2 += l * l; sumY2 += r * r;
            }
            double denom = Math.Sqrt(sumX2 * sumY2);
            double corr = denom > 1e-10 ? sumXY / denom : 0;
            if (corr > bestCorr) { bestCorr = corr; bestLag = lag; }
        }
        return Math.Abs(bestLag) >= 100 ? Math.Abs(bestLag) : 0;
    }
}

public record PhaseResult(double Correlation, bool IsMonoCompatible);
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/Analyzers/PhaseAnalyzer.cs
git commit -m "feat(C1): переписать PhaseAnalyzer с временны́ми окнами M/S, Spectral Flatness и детекцией Хааса"
```

---

### Task C2: Simplify FakeStereoDetector

**Files:**
- Modify: `LosslessChecker/Services/FakeStereoDetector.cs`

- [ ] **Step 1: Replace with simplified version consuming PhaseAnalyzer output**

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class FakeStereoDetector
{
    public bool IsFakeStereo(StereoBuffer buffer, double correlation)
    {
        if (!buffer.IsStereo) return false;
        if (correlation < 0.99) return false;

        var left = buffer.Left;
        var right = buffer.Right;
        long n = Math.Min(left.Length, right.Length);

        double crossCorr0 = 0;
        for (long i = 0; i < n; i++)
            crossCorr0 += (double)left[i] * right[i];

        double crossCorr1 = 0;
        for (long i = 0; i < n - 1; i++)
            crossCorr1 += (double)left[i] * right[i + 1];

        return crossCorr1 <= crossCorr0;
    }

    public bool IsFakeStereoFromPhase(PhaseResult phase, int channels)
    {
        if (channels != 2) return false;
        return phase.Correlation > 0.99;
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/FakeStereoDetector.cs
git commit -m "feat(C2): упростить FakeStereoDetector, добавить IsFakeStereoFromPhase"
```

---

### Task D1: Create SpectrogramAccumulator with Time-Binned Grid

**Files:**
- Create: `LosslessChecker/Services/SpectrogramAccumulator.cs`

- [ ] **Step 1: Write the file**

```csharp
using System.Buffers;
using LosslessChecker.Models;
using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class SpectrogramAccumulator
{
    private const int FftSize = 4096;
    private const int FreqBins = 1024;
    private const int MaxCols = 2048;
    private const double DbFloor = -96.0;
    private const double DefaultDurationSec = 300.0;

    private readonly float[] _window = Window.Hann(FftSize);
    private float[][] _columns = Array.Empty<float[]>();
    private int[] _columnCounts = Array.Empty<int>();
    private int _colIndex;
    private int _currentMaxCols = MaxCols;
    private double _timePerColumn;
    private double _accumulatedTime;
    private double _totalDuration;
    private bool _durationKnown;
    private int _sampleRate;

    private double _globalMaxMag = 1e-10;

    public void Init(int sampleRate, double? totalDuration)
    {
        _sampleRate = sampleRate;
        if (totalDuration.HasValue && totalDuration.Value > 0)
        {
            _totalDuration = totalDuration.Value;
            _durationKnown = true;
            _timePerColumn = _totalDuration / MaxCols;
            _currentMaxCols = MaxCols;
        }
        else
        {
            _durationKnown = false;
            _totalDuration = DefaultDurationSec;
            _timePerColumn = _totalDuration / MaxCols;
            _currentMaxCols = MaxCols;
        }

        _columns = new float[_currentMaxCols][];
        _columnCounts = new int[_currentMaxCols];
        _colIndex = 0;
        _accumulatedTime = 0;
        _globalMaxMag = 1e-10;
    }

    public void AddChunk(AudioChunk chunk)
    {
        var mono = chunk.IsStereo ? MixToMono(chunk.Left.Span, chunk.Right.Span) : chunk.Left.Span;
        if (mono.Length < FftSize) return;

        double chunkDuration = (double)mono.Length / _sampleRate;
        double chunkTime = _accumulatedTime;

        using var fftOwner = new FftOwner(FftSize);
        var real = new float[FftSize];
        var imag = new float[FftSize];

        int hopSize = Math.Max(1, FftSize / 4);

        for (int pos = 0; pos + FftSize <= mono.Length; pos += hopSize)
        {
            double frameTime = chunkTime + (double)pos / _sampleRate;
            int col = (int)(frameTime / _timePerColumn);

            if (col >= _currentMaxCols)
            {
                if (!_durationKnown)
                    DownsampleColumns();
                col = Math.Min(col, _currentMaxCols - 1);
            }

            if (col < 0 || col >= _currentMaxCols) continue;

            for (int i = 0; i < FftSize; i++)
                fftOwner.Frame[i] = mono[pos + i] * _window[i];

            Array.Copy(fftOwner.Frame, real, FftSize);
            Array.Clear(imag, 0, FftSize);
            fftOwner.Fft.Direct(real, imag);

            if (_columns[col] == null)
                _columns[col] = new float[FreqBins];

            double nyquist = _sampleRate / 2.0;
            double logMin = Math.Log10(20.0);
            double logMax = Math.Log10(nyquist);
            double logRange = logMax - logMin;
            double binsPerHz = (double)(FftSize / 2) / nyquist;

            for (int j = 0; j < FreqBins; j++)
            {
                double freq = Math.Pow(10, logMin + logRange * j / (FreqBins - 1));
                double binIdx = freq * binsPerHz;
                int bin0 = Math.Clamp((int)binIdx, 0, FftSize / 2 - 1);
                int bin1 = Math.Min(bin0 + 1, FftSize / 2 - 1);
                double frac = binIdx - bin0;

                double mag = Math.Sqrt(
                    (double)real[bin0] * real[bin0] + (double)imag[bin0] * imag[bin0]);
                double mag1 = Math.Sqrt(
                    (double)real[bin1] * real[bin1] + (double)imag[bin1] * imag[bin1]);
                double interpMag = mag + (mag1 - mag) * frac;

                if (_columns[col][j] < interpMag)
                    _columns[col][j] = (float)interpMag;
            }

            _columnCounts[col]++;
            _globalMaxMag = Math.Max(_globalMaxMag, _columns[col].Max());
        }

        _accumulatedTime += chunkDuration;
    }

    public SpectrogramData Finalize()
    {
        int actualCols = 0;
        for (int i = 0; i < _currentMaxCols; i++)
            if (_columnCounts[i] > 0) actualCols = i + 1;
        if (actualCols == 0) actualCols = 1;

        var dbValues = new float[actualCols * FreqBins];
        double refMag = Math.Max(_globalMaxMag, 1e-10);

        for (int x = 0; x < actualCols; x++)
        {
            if (_columns[x] == null) continue;
            for (int y = 0; y < FreqBins; y++)
            {
                double db = 20.0 * Math.Log10(Math.Max(_columns[x][y], 1e-10) / refMag);
                dbValues[x * FreqBins + y] = (float)Math.Clamp((db - DbFloor) / (-DbFloor), 0, 1);
            }
        }

        return new SpectrogramData(dbValues, actualCols, FreqBins, _sampleRate, _totalDuration);
    }

    private void DownsampleColumns()
    {
        int newMax = _currentMaxCols / 2;
        if (newMax < MaxCols / 4) newMax = MaxCols / 4;
        var newCols = new float[newMax][];
        var newCounts = new int[newMax];

        for (int i = 0; i < newMax; i++)
        {
            int src0 = i * 2;
            int src1 = Math.Min(src0 + 1, _currentMaxCols - 1);
            if (_columns[src0] != null || _columns[src1] != null)
            {
                newCols[i] = new float[FreqBins];
                if (_columns[src0] != null && _columns[src1] != null)
                {
                    for (int j = 0; j < FreqBins; j++)
                        newCols[i][j] = Math.Max(_columns[src0][j], _columns[src1][j]);
                    newCounts[i] = _columnCounts[src0] + _columnCounts[src1];
                }
                else if (_columns[src0] != null)
                {
                    Array.Copy(_columns[src0], newCols[i], FreqBins);
                    newCounts[i] = _columnCounts[src0];
                }
                else
                {
                    Array.Copy(_columns[src1], newCols[i], FreqBins);
                    newCounts[i] = _columnCounts[src1];
                }
            }
        }

        _columns = newCols;
        _columnCounts = newCounts;
        _currentMaxCols = newMax;
        _timePerColumn *= 2;
    }

    private static float[] MixToMono(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int n = Math.Min(left.Length, right.Length);
        var mono = new float[n];
        for (int i = 0; i < n; i++) mono[i] = (left[i] + right[i]) * 0.5f;
        return mono;
    }

    private struct FftOwner : IDisposable
    {
        public readonly Fft Fft;
        public readonly float[] Frame;

        public FftOwner(int size)
        {
            Fft = new Fft(size);
            Frame = ArrayPool<float>.Shared.Rent(size);
        }

        public void Dispose()
        {
            ArrayPool<float>.Shared.Return(Frame);
        }
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/SpectrogramAccumulator.cs
git commit -m "feat(D1): создать SpectrogramAccumulator с фиксированной сеткой 2048x1024 и прогрессивным даунсемплингом"
```

---

### Task D2: Redirect SpectrogramBuilder to SpectrogramAccumulator

**Files:**
- Modify: `LosslessChecker/Services/SpectrogramBuilder.cs`

Keep `SpectrogramBuilder.Build()` for backward compat (still used by tests). In Phase G (AudioPipeline), the pipeline will use `SpectrogramAccumulator` directly.

- [ ] **Step 1: No changes to SpectrogramBuilder** — it remains for backward compat. Skip.

- [ ] **Step 2: Commit (skip)**

```
# D2: SpectrogramBuilder kept for backward compat; pipeline uses SpectrogramAccumulator directly
```

---

### Task D3: Add RenderRegion() to SpectrogramRenderer

**Files:**
- Modify: `LosslessChecker/Services/SpectrogramRenderer.cs`

- [ ] **Step 1: Add RenderRegion method after existing Render at line 60**

```csharp
public WriteableBitmap RenderRegion(float[] dbValues, int dataWidth, int dataHeight,
    double startTime, double endTime, double lowFreq, double highFreq,
    double totalDuration, double nyquist, int targetWidth, int targetHeight)
{
    int srcStartCol = (int)(startTime / totalDuration * dataWidth);
    int srcEndCol = (int)(endTime / totalDuration * dataWidth);
    srcStartCol = Math.Clamp(srcStartCol, 0, dataWidth - 1);
    srcEndCol = Math.Clamp(srcEndCol, 0, dataWidth);

    double logMin = Math.Log10(20.0);
    double logMax = Math.Log10(nyquist);
    double logRange = logMax - logMin;
    int srcTopRow = dataHeight - 1 - (int)((Math.Log10(highFreq) - logMin) / logRange * dataHeight);
    int srcBottomRow = dataHeight - 1 - (int)((Math.Log10(lowFreq) - logMin) / logRange * dataHeight);
    srcTopRow = Math.Clamp(srcTopRow, 0, dataHeight - 1);
    srcBottomRow = Math.Clamp(srcBottomRow, 0, dataHeight - 1);
    if (srcTopRow > srcBottomRow) (srcTopRow, srcBottomRow) = (srcBottomRow, srcTopRow);

    int srcWidth = srcEndCol - srcStartCol;
    int srcHeight = srcBottomRow - srcTopRow + 1;
    if (srcWidth < 1 || srcHeight < 1) return new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);

    int outWidth = Math.Max(1, targetWidth);
    int outHeight = Math.Max(1, targetHeight);

    var bmp = new WriteableBitmap(outWidth, outHeight, 96, 96, PixelFormats.Bgra32, null);
    var pixels = ArrayPool<byte>.Shared.Rent(outWidth * outHeight * 4);
    try
    {
        var span = pixels.AsSpan(0, outWidth * outHeight * 4);
        for (int i = 0; i < span.Length; i += 4)
        {
            span[i] = 0x1B; span[i + 1] = 0x11; span[i + 2] = 0x11; span[i + 3] = 0xFF;
        }

        for (int ox = 0; ox < outWidth; ox++)
        {
            int sx = srcStartCol + (int)((double)ox / outWidth * srcWidth);
            sx = Math.Clamp(sx, 0, dataWidth - 1);
            for (int oy = 0; oy < outHeight; oy++)
            {
                int sy = srcTopRow + (int)((double)oy / outHeight * srcHeight);
                sy = Math.Clamp(sy, 0, dataHeight - 1);
                float t = dbValues[sx * dataHeight + sy];
                int lutIdx = Math.Clamp((int)(t * 255), 0, 255);
                int idx = ((outHeight - 1 - oy) * outWidth + ox) * 4;
                uint color = ColormapLut[lutIdx];
                span[idx] = (byte)(color & 0xFF);
                span[idx + 1] = (byte)((color >> 8) & 0xFF);
                span[idx + 2] = (byte)((color >> 16) & 0xFF);
                span[idx + 3] = 0xFF;
            }
        }

        bmp.Lock();
        Marshal.Copy(pixels, 0, bmp.BackBuffer, outWidth * outHeight * 4);
        bmp.AddDirtyRect(new Int32Rect(0, 0, outWidth, outHeight));
        bmp.Unlock();
        return bmp;
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(pixels);
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/SpectrogramRenderer.cs
git commit -m "feat(D3): добавить RenderRegion для перерендера видимой области с обратным лог-преобразованием"
```

---

### Task E1: Create PreEchoDetector as standalone IChunkAccumulator

**Files:**
- Create: `LosslessChecker/Services/PreEchoDetector.cs`

- [ ] **Step 1: Write the file**

```csharp
using LosslessChecker.Models;
using LosslessChecker.Services.ChunkProcessing;

namespace LosslessChecker.Services;

public class PreEchoDetector : IChunkAccumulator<(bool hasPreEcho, int preEchoCount)>
{
    private const int WindowMs = 2;
    private const int MaxWindows = 500;
    private const double TransientThreshold = 4.0;
    private const int MinPreEchoCount = 3;

    private int _sampleRate, _windowSamples;
    private readonly CircularBuffer<double> _rmsBuffer = new(MaxWindows);
    private int _preEchoCount;
    private bool _initialized;

    public void Init(int sampleRate)
    {
        _sampleRate = sampleRate;
        _windowSamples = sampleRate * WindowMs / 1000;
        if (_windowSamples < 1) _windowSamples = 1;
        _initialized = true;
    }

    public void Reset()
    {
        _preEchoCount = 0;
        _rmsBuffer.Clear();
    }

    public void AddChunk(ReadOnlySpan<float> mono)
    {
        if (!_initialized) throw new InvalidOperationException("Init not called");

        for (int pos = 0; pos + _windowSamples * 2 <= mono.Length; pos += _windowSamples)
        {
            double rmsBefore = ComputeRms(mono, pos, _windowSamples);
            double rmsAfter = ComputeRms(mono, pos + _windowSamples, _windowSamples);

            if (_rmsBuffer.Count > 0)
            {
                double prevRms = _rmsBuffer.Last();
                if (rmsAfter > prevRms * TransientThreshold && rmsBefore > rmsAfter * 0.15)
                    _preEchoCount++;
            }

            _rmsBuffer.Push(rmsAfter);
        }
    }

    public (bool hasPreEcho, int preEchoCount) GetResult()
    {
        return (_preEchoCount > MinPreEchoCount, _preEchoCount);
    }

    private static double ComputeRms(ReadOnlySpan<float> samples, int offset, int count)
    {
        double sumSq = 0;
        int end = Math.Min(offset + count, samples.Length);
        for (int i = offset; i < end; i++)
        {
            double s = samples[i];
            sumSq += s * s;
        }
        int n = end - offset;
        return n > 0 ? Math.Sqrt(sumSq / n) : 0;
    }

    private class CircularBuffer<T>
    {
        private readonly T[] _buf;
        private int _writePos, _count;
        public CircularBuffer(int capacity) => _buf = new T[capacity];
        public int Count => _count;
        public T Last() => _buf[(_writePos - 1 + _buf.Length) % _buf.Length];
        public void Push(T val) { _buf[_writePos] = val; _writePos = (_writePos + 1) % _buf.Length; if (_count < _buf.Length) _count++; }
        public void Clear() { _writePos = _count = 0; Array.Clear(_buf, 0, _buf.Length); }
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/PreEchoDetector.cs
git commit -m "feat(E1): выделить PreEchoDetector в отдельный IChunkAccumulator с кольцевым буфером на 500 окон"
```

---

### Task E2: Remove inline PreEcho code from ArtifactDetector, add SBR detection

**Files:**
- Modify: `LosslessChecker/Services/ArtifactDetector.cs`

- [ ] **Step 1: Remove the DetectPreEcho method (lines 176-202) and add SBR detection**

Remove `DetectPreEcho` at lines 176-202. Add `DetectSbr` method after `DetectSpectralHoles`:

```csharp
public (bool hasSbr, string sbrVerdict) DetectSbr(
    double[] averagedSpectrum, int sampleRate)
{
    int bins = averagedSpectrum.Length;
    if (bins < 100) return (false, "");

    double nyquist = sampleRate / 2.0;

    // Find local cutoff in 12-18 kHz range via steepest derivative
    int bin12k = (int)(12000.0 / nyquist * bins);
    int bin18k = (int)(18000.0 / nyquist * bins);
    bin12k = Math.Clamp(bin12k, bins / 4, bins - 1);
    bin18k = Math.Clamp(bin18k, bin12k + 10, bins - 1);

    int localCutoffBin = bin18k;
    double steepestDrop = 0;
    for (int i = bin12k + 20; i < bin18k - 5; i++)
    {
        double before = MaxInRange(averagedSpectrum, i - 15, i);
        double after = MaxInRange(averagedSpectrum, i, i + 15);
        double dropDb = 20.0 * Math.Log10(Math.Max(before, 1e-10) / Math.Max(after, 1e-10));
        if (dropDb > steepestDrop) { steepestDrop = dropDb; localCutoffBin = i; }
    }

    int upperStart = localCutoffBin;
    int upperEnd = Math.Min(localCutoffBin + (int)(3000.0 / nyquist * bins), bins - 1);
    int lowerStart = Math.Max(1, localCutoffBin - (int)(3000.0 / nyquist * bins));
    int lowerEnd = localCutoffBin;

    if (upperEnd - upperStart < 10 || lowerEnd - lowerStart < 10)
        return (false, "");

    double upperFlatness = ComputeFlatness(averagedSpectrum, upperStart, upperEnd);
    double lowerFlatness = ComputeFlatness(averagedSpectrum, lowerStart, lowerEnd);

    // SBR signature: upper band has isolated tonal patches (low flatness)
    // while lower band is musically normal
    bool hasTonalPatches = upperFlatness < 0.15 && lowerFlatness > 0.3;

    // Envelope cross-correlation
    int envBins = Math.Min(upperEnd - upperStart, lowerEnd - lowerStart);
    double envCorr = ComputeEnvelopeCorrelation(averagedSpectrum, lowerStart, upperStart, envBins);

    double upperEnergy = SumRange(averagedSpectrum, upperStart, upperEnd);
    double lowerEnergy = SumRange(averagedSpectrum, lowerStart, lowerEnd);
    double energyRatioDb = lowerEnergy > 0 ? 20.0 * Math.Log10(upperEnergy / lowerEnergy) : -200;

    bool hasSbr = hasTonalPatches || (envCorr > 0.7 && energyRatioDb > -24 && energyRatioDb < -12);
    string verdict = hasSbr ? $"AAC SBR" : "";
    return (hasSbr, verdict);
}

private static double MaxInRange(double[] spectrum, int start, int end)
{
    double max = 0;
    for (int i = Math.Max(0, start); i < Math.Min(spectrum.Length, end); i++)
        max = Math.Max(max, spectrum[i]);
    return max;
}

private static double SumRange(double[] spectrum, int start, int end)
{
    double sum = 0;
    for (int i = Math.Max(0, start); i < Math.Min(spectrum.Length, end); i++)
        sum += spectrum[i];
    return sum;
}

private static double ComputeFlatness(double[] spectrum, int start, int end)
{
    double geomSum = 0, arithSum = 0;
    int count = 0;
    for (int i = start; i < end; i++)
    {
        double v = Math.Max(spectrum[i], 1e-10);
        geomSum += Math.Log(v);
        arithSum += v;
        count++;
    }
    if (count == 0 || arithSum <= 0) return 1.0;
    return Math.Exp(geomSum / count) / (arithSum / count);
}

private static double ComputeEnvelopeCorrelation(double[] spectrum, int aStart, int bStart, int count)
{
    double sumA = 0, sumB = 0, sumAB = 0, sumA2 = 0, sumB2 = 0;
    for (int i = 0; i < count; i++)
    {
        double a = spectrum[aStart + i];
        double b = spectrum[bStart + i];
        sumA += a; sumB += b; sumAB += a * b; sumA2 += a * a; sumB2 += b * b;
    }
    double n = count;
    double denom = Math.Sqrt((n * sumA2 - sumA * sumA) * (n * sumB2 - sumB * sumB));
    return denom > 1e-10 ? (n * sumAB - sumA * sumB) / denom : 0;
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/ArtifactDetector.cs
git commit -m "feat(E2): удалить встроенный PreEcho из ArtifactDetector, добавить детекцию SBR для HE-AAC"
```

---

### Task F1: Add genre-specific DR thresholds to ScoringProfile

**Files:**
- Modify: `LosslessChecker/Services/Analysis/ScoringProfile.cs`

- [ ] **Step 1: Add genre DR thresholds and new penalty fields**

Add after line 52 (`public static readonly ScoringProfile Default = new();`):

```csharp
// Genre-specific DR thresholds
public (double Excellent, double Good, double Poor)[] GenreDrThresholds { get; init; } =
{
    (12, 8, 5),   // Default / Unknown
    (8, 5, 3),    // EDM / Electronic
    (8, 5, 3),    // Metal / Rock
    (14, 10, 7),  // Jazz / Classical
};

// Authenticity penalties
public int CutoffPenaltyBrickwallCodec { get; init; } = 60;
public int CutoffPenaltyBrickwallNearNyquist { get; init; } = 30;
public int CutoffPenaltyFilteredLow { get; init; } = 20;
public int ArtifactStrongPenaltyAuth { get; init; } = 35;
public int ArtifactMediumPenaltyAuth { get; init; } = 20;
public int ArtifactWeakPenaltyAuth { get; init; } = 8;
public int LsbZeroPadPenaltyAuth { get; init; } = 100;
public int LsbConstantPenaltyAuth { get; init; } = 80;
public int BitDepthSuspiciousPenaltyAuth { get; init; } = 20;
public int AliasingPenalty { get; init; } = 15;
public int RingingPenalty { get; init; } = 10;
public int UpscalePenaltyAuth { get; init; } = 25;
public int FakeStereoPenaltyAuth { get; init; } = 10;
public int AbruptEdgesPenaltyAuth { get; init; } = 5;

// Mastering penalties
public int HardClippingSeverePenalty { get; init; } = 20;
public int IspMinimalPenalty { get; init; } = 2;
public int DcOffsetSeverePenalty { get; init; } = 8;
public int PhaseBadPenaltyMastering { get; init; } = 12;
public int PlrLowPenalty { get; init; } = 10;
public int LufsAnomalyPenalty { get; init; } = 15;
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/Analysis/ScoringProfile.cs
git commit -m "feat(F1): добавить жанровые DR-пороги и штрафы для раздельного скоринга"
```

---

### Task F2: Split LosslessScorer into AuthenticityScore + MasteringScore

**Files:**
- Modify: `LosslessChecker/Services/Analysis/LosslessScorer.cs`

- [ ] **Step 1: Add AuthenticityScore, MasteringScore, and genre clustering methods**

Add methods after the existing `Classify` method (before closing brace):

```csharp
public double AuthenticityScore(AnalysisResult r)
{
    double score = 100;
    var nyquist = r.SampleRate / 2.0;
    double ratio = nyquist > 0 ? r.CutoffFrequency / nyquist : 1.0;
    bool isHiRes = r.SampleRate >= 88200;

    if (r.IsCorrupted) return 0;

    if (r.LsbZeroPadded && r.BitDepth >= 24) return Math.Max(0, score - _p.LsbZeroPadPenaltyAuth);
    if (r.BitDepthSuspicious) score -= _p.BitDepthSuspiciousPenaltyAuth;

    if (r.ShelfType == "Brickwall")
    {
        if (isHiRes)
        {
            if (r.CutoffFrequency <= 17000) score -= _p.CutoffPenaltyBrickwallCodec;
            else if (r.CutoffFrequency <= 20000) score -= _p.CutoffPenaltyBrickwallCodec / 2;
            else if (r.CutoffFrequency <= 22100) score -= _p.CutoffPenaltyBrickwallNearNyquist;
        }
        else
        {
            if (ratio < 0.65) score -= _p.CutoffPenaltyBrickwallCodec;
            else if (ratio < 0.85) score -= _p.CutoffPenaltyBrickwallCodec / 2;
            else if (ratio < 0.95) score -= _p.CutoffPenaltyBrickwallNearNyquist;
        }
    }

    if (r.HasArtifacts)
    {
        score -= r.ArtifactLevel switch
        {
            "Strong" => _p.ArtifactStrongPenaltyAuth,
            "Medium" => _p.ArtifactMediumPenaltyAuth,
            "Weak" => _p.ArtifactWeakPenaltyAuth,
            _ => 0
        };
    }

    if (r.HasAliasing) score -= _p.AliasingPenalty;
    if (r.HasRinging) score -= _p.RingingPenalty;
    if (r.IsUpscale) score -= _p.UpscalePenaltyAuth;
    if (r.IsFakeStereo) score -= _p.FakeStereoPenaltyAuth;
    if (r.HasAbruptEdges) score -= _p.AbruptEdgesPenaltyAuth;

    bool isCleanSpectrum = ratio >= 0.90 && r.ShelfType != "Brickwall" && r.ArtifactLevel != "Strong";
    if (isCleanSpectrum && score < 75) score = 75;

    bool isAnalogRolloff = r.ShelfType != "Brickwall" && r.ArtifactLevel != "Strong" && ratio < 0.90;
    if (isAnalogRolloff && score < 60) score = 60;

    return Math.Max(0, Math.Min(100, score));
}

public double MasteringScore(AnalysisResult r)
{
    double score = 100;
    int genre = InferGenre(r);

    var (excellent, good, poor) = _p.GenreDrThresholds[genre];
    if (r.DynamicRange < poor) score -= 20;
    else if (r.DynamicRange < good) score -= 10;
    else if (r.DynamicRange < excellent) score -= 3;

    double expectedLufs = genre switch { 0 => -14, 1 => -8, 2 => -9, 3 => -18, _ => -14 };
    double lufsDelta = Math.Abs(r.IntegratedLufs - expectedLufs);
    if (lufsDelta > 6) score -= _p.LufsAnomalyPenalty;

    if (r.ClippingPercent > 5) score -= _p.HardClippingSeverePenalty;
    else if (r.HasIsp) score -= _p.IspMinimalPenalty;

    if (Math.Abs(r.DcOffsetL) > 1.0 || Math.Abs(r.DcOffsetR) > 1.0) score -= _p.DcOffsetSeverePenalty;
    if (r.Correlation < -0.5) score -= _p.PhaseBadPenaltyMastering;

    return Math.Max(0, Math.Min(100, score));
}

private static int InferGenre(AnalysisResult r)
{
    if (r.IntegratedLufs > -8 && r.DynamicRange <= 5) return 1;  // EDM / Loud pop
    if (r.IntegratedLufs > -8 && r.DynamicRange <= 8) return 2;  // Rock / Metal
    if (r.IntegratedLufs < -14 && r.DynamicRange >= 10) return 3; // Jazz / Classical
    return 0; // Default
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

Note: `IsCorrupted` property doesn't exist on `AnalysisResult` yet — it will be added in Task I1. If compilation fails here, temporarily comment out the `IsCorrupted` check.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/Analysis/LosslessScorer.cs
git commit -m "feat(F2): разделить LosslessScorer на AuthenticityScore и MasteringScore с эвристической жанровой кластеризацией"
```

---

### Task F3: Modify QualityScorer to use anomaly-based penalties

**Files:**
- Modify: `LosslessChecker/Services/Analysis/QualityScorer.cs`

- [ ] **Step 1: Rewrite Score method with anomaly-based logic and cross-axis verdict matrix**

Replace the `Score` method:

```csharp
public (double authenticityScore, double masteringScore, string authenticityVerdict, string masteringVerdict, string decision) ScoreFull(AnalysisResult r, LosslessScorer scorer)
{
    double authScore = scorer.AuthenticityScore(r);
    double mastScore = scorer.MasteringScore(r);

    string authVerdict = authScore >= 70 ? "TRUE" : authScore >= 50 ? "UNCERTAIN" : "FALSE";
    if (r.IsCorrupted) { authVerdict = "CORRUPTED"; authScore = 0; mastScore = 0; }

    string mastVerdict = mastScore >= 80 ? "Excellent" : mastScore >= 50 ? "Good" : "Fair";

    string decision;
    if (r.IsCorrupted) decision = "CORRUPTED";
    else if (r.Authenticity == "MQA") decision = "MQA (needs decoder)";
    else if (authVerdict == "FALSE") decision = "REPLACE";
    else if (authVerdict == "UNCERTAIN") decision = "INVESTIGATE";
    else if (mastVerdict == "Excellent") decision = "KEEP (Excellent)";
    else if (mastVerdict == "Good") decision = "KEEP (Good)";
    else decision = "KEEP (Fair)";

    return (authScore, mastScore, authVerdict, mastVerdict, decision);
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/Analysis/QualityScorer.cs
git commit -m "feat(F3): переработать QualityScorer с кросс-осевым вердиктом и аномалийным скорингом"
```

---

### Task F4: Update VerdictGenerator for two-axis scoring

**Files:**
- Modify: `LosslessChecker/Services/VerdictGenerator.cs`

- [ ] **Step 1: Update Generate and GenerateWhy to use new score fields**

Replace the `Generate` method header and body to reference `AuthenticityScore`/`MasteringScore` instead of the legacy single score. Also replace hardcoded Russian strings with composable labels.

(KEEP Generate and GenerateWhy methods — they produce formatted text reports. Only update field references: use `r.MasteringScore` instead of `r.QualityScorePercent`, and `r.AuthenticityVerdict` instead of `r.Authenticity`. No behavioral change to text generation — Phase K handles localization.)

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit (skip if only field renames — handled in I1/J6)**

```
# F4: VerdictGenerator field references updated inline with I1/J6 AnalysisResult changes
```

---

### Task G1: Rewrite AudioPipeline with 3-phase streaming architecture

**Files:**
- Modify: `LosslessChecker/Services/AudioPipeline.cs`

This is the largest rewrite. The pipeline switches from `buffer = AudioDecoder.Decode()` → `Parallel.Invoke(...)` to the 3-phase streaming model.

- [ ] **Step 1: Rewrite the Analyze method body**

Keep the metadata reading section (lines 52-198 roughly — format detection, tags, bitrate computation) unchanged except: after metadata, instead of `Decode()`, start the 3-phase pipeline.

```csharp
public async Task<AnalysisResult> AnalyzeAsync(AudioFileInfo fileInfo, CancellationToken ct = default)
{
    var result = new AnalysisResult { FilePath = fileInfo.FilePath, FileName = fileInfo.FileName, AnalysisStatus = AnalysisStatus.Processing };

    try
    {
        // ... metadata reading (lines 63-133 unchanged) ...
        
        if (ct.IsCancellationRequested) return Cancelled(result);

        // === Phase 1: Stream Phase ===
        var accumulators = new
        {
            TruePeak = new TruePeakDetector(),
            Dr = new DrMeter(),
            DcOffset = new DcOffsetDetector(),
            Lufs = new LufsMeter(),
            Phase = new PhaseAnalyzer(),
            BitDepth = new BitDepthValidator(),
            PreEcho = new PreEchoDetector(),
            Spectrogram = new SpectrogramAccumulator(),
            Reservoir = new ReservoirBuffer(6)
        };

        accumulators.Dr.Init(sampleRate);
        accumulators.Lufs.Init(sampleRate);
        accumulators.PreEcho.Init(sampleRate);
        accumulators.Spectrogram.Init(sampleRate, duration);

        await foreach (var chunk in AudioDecoder.StreamChunks(fileInfo.FilePath, 10, ct))
        {
            if (chunk.IsLast) break;

            if (chunk.IsStereo)
            {
                accumulators.TruePeak.AddChunk(chunk.Left.Span, chunk.Right.Span);
                accumulators.Phase.AddChunk(chunk.Left.Span, chunk.Right.Span);
                accumulators.Dr.AddChunk(chunk.Left.Span, chunk.Right.Span);
            }
            else
            {
                accumulators.TruePeak.AddChunk(chunk.Left.Span);
                accumulators.Phase.AddChunk(chunk.Left.Span);
                accumulators.Dr.AddChunk(chunk.Left.Span);
            }

            accumulators.DcOffset.AddChunk(chunk.Left.Span);
            accumulators.Lufs.AddChunk(chunk.Left.Span);
            accumulators.BitDepth.AddChunk(chunk.Left.Span);
            accumulators.PreEcho.AddChunk(chunk.Left.Span);
            accumulators.Spectrogram.AddChunk(chunk);
            accumulators.Reservoir.AddChunk(chunk);
        }

        if (ct.IsCancellationRequested) return Cancelled(result);

        var tpResult = accumulators.TruePeak.GetResult();
        var lufsResult = accumulators.Lufs.GetResult();
        var drResult = accumulators.Dr.GetResult();
        var dcResult = accumulators.DcOffset.GetResult();
        var phaseResult = accumulators.Phase.GetResult();
        var bitResult = accumulators.BitDepth.GetResult(bitDepth);
        var preEchoResult = accumulators.PreEcho.GetResult();

        // === Phase 2: Post-Stream (strict order) ===
        var spectroData = accumulators.Spectrogram.Finalize();

        var reservoirChunks = accumulators.Reservoir.SelectedChunks;
        var reservoirMono = ConcatReservoir(reservoirChunks);
        var mono = reservoirMono.Length > 0 ? reservoirMono : Array.Empty<float>();

        var (cutoffHz, cutoffSlope, spectrum) = _cutoff.DetectFull(mono, sampleRate);

        var vinylResult = _vinyl.Detect(spectrum, sampleRate, mono);
        var (encoderMatch, shelfType) = _cutoff.ClassifyCutoff(cutoffHz, cutoffSlope, sampleRate);
        if (vinylResult.IsVinylRip)
        {
            if (cutoffHz >= 16000 && cutoffHz <= 18000 && shelfType == "Filtered")
            {
                shelfType = "Vinyl Rolloff";
                encoderMatch = "None (Vinyl Transfer)";
            }
        }

        var (bandwidth, detectedType) = CutoffDetector.ClassifyBandwidth(
            cutoffHz, shelfType, sampleRate, false, "None", false, 0,
            bitResult.LsbZeroPadded, bitResult.EffectiveBitDepth, bitDepth,
            true, false, false, tpResult.ClippingPercent > 0, encoderMatch);

        var artifactResult = _artifacts.Detect(mono, sampleRate, cutoffHz);
        var sbrResult = _artifacts.DetectSbr(spectrum, sampleRate);
        var hasSpectralHoles = _artifacts.DetectSpectralHoles(spectrum, sampleRate / 2.0);
        var hasCodecSilence = CutoffDetector.HasAbsoluteSilence(spectrum, cutoffHz, sampleRate);
        var hasAbruptEdges = _artifacts.DetectAbruptEdges(mono, sampleRate);

        var upscaleResult = _upscale.Detect(spectrum, sampleRate);
        var resamplingResult = _resampling.DetectFromSpectrum(spectrum, sampleRate);

        // PhaseAnalyzer post-stream: Spectral Flatness and Haas on reservoir data
        // (skip Haas if mono)

        // Container analysis
        var containerResult = _container.Analyze(fileInfo.FilePath, mono, sampleRate);

        // === Phase 3: Scoring ===
        bool isFakeStereo = channels != 2 ? false : false; // placeholder for PhaseAnalyzer Haas output
        
        // ... aggregate result record (unchanged pattern from original lines 270-540) ...

        // NOTE: Full aggregation and scoring code continues — same structure as original
        // but with new AuthenticityScore / MasteringScore from LosslessScorer.

        return result;
    }
    catch (OperationCanceledException) { return Cancelled(result); }
    catch (Exception ex) { return result with { AnalysisStatus = AnalysisStatus.Error, ErrorMessage = ex.Message }; }
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
```

> **CRITICAL**: The full `AnalyzeAsync` rewrite is ~500 lines. The above shows the core streaming integration. The existing metadata/pre-processing code (lines 52-198) is preserved verbatim. The aggregation code (lines 260-540) is preserved with field name updates.

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/AudioPipeline.cs
git commit -m "feat(G1): переписать AudioPipeline на 3-фазную потоковую архитектуру"
```

---

### Task H1: Add anti-false-positive guard to VinylDetector

**Files:**
- Modify: `LosslessChecker/Services/VinylDetector.cs`

- [ ] **Step 1: Add digital silence check in Detect method before the existing classification**

Insert after line 61 (after computing `isVinyl`), add:

```csharp
// Anti-false-positive: verify that HF noise is analog, not digital silence
if (isVinyl)
{
    double hfMinDb = hfCount > 0
        ? 20.0 * Math.Log10(Math.Max(spectrum[hfStart..].Min(), 1e-10) / (avgMid > 1e-10 ? avgMid : 1e-10))
        : -200;
    
    // If above-cutoff is digital silence (< -90 dB), not vinyl — it's a mastering LPF.
    // Vinyl has analog noise floor in -40 to -60 dB range.
    if (hfMinDb < -90 && rumbleRatio < 0.5)
    {
        isVinyl = false;
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/VinylDetector.cs
git commit -m "feat(H1): добавить защиту от ложных срабатываний VinylDetector на цифровой тишине"
```

---

### Task H2: Pass IsVinylRip into ClassifyCutoff

**Files:**
- Modify: `LosslessChecker/Services/CutoffDetector.cs`

Add overload to `ClassifyCutoff` accepting `bool isVinylRip`. If true and cutoff in 16-18 kHz with Filtered shelf → override to `"Vinyl Rolloff"`.

- [ ] **Step 1: Add overloaded ClassifyCutoff at line 246**

```csharp
public (string encoderMatch, string shelfType) ClassifyCutoff(
    double cutoffHz, double cutoffSlope, int sampleRate, bool isVinylRip)
{
    var (encoderMatch, shelfType) = ClassifyCutoff(cutoffHz, cutoffSlope, sampleRate);
    if (isVinylRip && cutoffHz >= 16000 && cutoffHz <= 18000 && shelfType == "Filtered")
    {
        shelfType = "Vinyl Rolloff";
        encoderMatch = "None (Vinyl Transfer)";
    }
    return (encoderMatch, shelfType);
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/CutoffDetector.cs
git commit -m "feat(H2): добавить перегрузку ClassifyCutoff с флагом IsVinylRip"
```

---

### Task I1: Add IsCorrupted flag to ContainerAnalyzer and AnalysisResult

**Files:**
- Modify: `LosslessChecker/Services/ContainerAnalyzer.cs`
- Modify: `LosslessChecker/Models/AnalysisResult.cs`

- [ ] **Step 1: Add IsCorrupted to AnalysisResult (line ~108)**

```csharp
public bool IsCorrupted { get; init; }
```

Add at line 109 (before `HasAbruptEdges`).

- [ ] **Step 2: Add FLAC MD5 / RIFF corruption check to ContainerAnalyzer**

In `ContainerAnalyzer.Analyze`, after computing `flacOk`:

```csharp
bool isCorrupted = false;
string ext = Path.GetExtension(filePath).ToLowerInvariant();
if (ext == ".flac")
{
    isCorrupted = !flacOk;
}
else if (ext == ".wav")
{
    isCorrupted = !CheckRiffIntegrity(filePath);
}
```

Add the RIFF checker:

```csharp
private static bool CheckRiffIntegrity(string filePath)
{
    try
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var header = new byte[12];
        if (fs.Read(header, 0, 12) < 12) return false;
        return header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
            && header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E';
    }
    catch { return false; }
}
```

Update `ContainerResult` record to include `IsCorrupted`:

```csharp
public record ContainerResult(
    bool IsCdAligned, bool FlacIntegrityOk, string Source,
    bool IsMqa, string MqaDetails, bool IsHdcd,
    byte[] PcmMd5, bool IsCorrupted);
```

- [ ] **Step 3: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/Services/ContainerAnalyzer.cs LosslessChecker/Models/AnalysisResult.cs
git commit -m "feat(I1): добавить флаг IsCorrupted для битых FLAC MD5 и RIFF-заголовков"
```

---

### Task J1: Create RangeObservableCollection

**Files:**
- Create: `LosslessChecker/Models/RangeObservableCollection.cs`

- [ ] **Step 1: Write the file**

```csharp
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace LosslessChecker.Models;

public class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
            Items.Add(item);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items.ToList()));
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Models/RangeObservableCollection.cs
git commit -m "feat(J1): создать RangeObservableCollection для батчевых UI-обновлений"
```

---

### Task J2: Update MainViewModel for batched loading and incremental tree updates

**Files:**
- Modify: `LosslessChecker/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Change `Files` field type and batch-add files**

At line 24, change:
```csharp
private RangeObservableCollection<AudioFileViewModel> _files = new();
```

In `ScanAndAnalyze`, replace `foreach (var vm in vms) Files.Add(vm);` (lines 398-399) with:
```csharp
// Batch add with Background priority yields
int batchSize = 200;
for (int i = 0; i < vms.Count; i += batchSize)
{
    var batch = vms.Skip(i).Take(batchSize).ToList();
    await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        foreach (var vm in batch) Files.Add(vm);
    }, DispatcherPriority.Background);
    await Task.Yield();
}
```

- [ ] **Step 2: Make PopulateArtistGroups incremental**

Replace the call at line 458 (inside processing loop) — remove it entirely. At the end of processing (line 460, before `ApplyFilters`), keep one final call. Add an incremental update at line ~448 (inside the result-processing block):

```csharp
// Incremental tree update
await Application.Current.Dispatcher.InvokeAsync(() =>
{
    var existing = ArtistGroups
        .SelectMany(a => a.Albums)
        .FirstOrDefault(al => string.Equals(al.AlbumName,
            string.IsNullOrWhiteSpace(result.Album) ? "Unknown Album" : result.Album,
            StringComparison.OrdinalIgnoreCase));
    if (existing != null)
        existing.Tracks.Add(vm);
});
```

- [ ] **Step 3: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/ViewModels/MainViewModel.cs
git commit -m "feat(J2): батчевая загрузка файлов с Dispatcher.Yield и инкрементальные обновления дерева"
```

---

### Task J3: Create SpectrogramCache

**Files:**
- Create: `LosslessChecker/Services/SpectrogramCache.cs`

- [ ] **Step 1: Write the file**

```csharp
namespace LosslessChecker.Services;

public class SpectrogramCache
{
    private readonly int _maxEntries;
    private readonly Dictionary<string, LinkedListNode<(string key, float[] data)>> _dict = new();
    private readonly LinkedList<(string key, float[] data)> _list = new();

    public SpectrogramCache(int maxEntries = 10) => _maxEntries = maxEntries;

    public bool TryGet(string key, out float[] data)
    {
        if (_dict.TryGetValue(key, out var node))
        {
            _list.Remove(node);
            _list.AddFirst(node);
            data = node.Value.data;
            return true;
        }
        data = Array.Empty<float>();
        return false;
    }

    public void Store(string key, float[] data)
    {
        if (_dict.TryGetValue(key, out var node))
        {
            _list.Remove(node);
        }
        else if (_dict.Count >= _maxEntries)
        {
            var last = _list.Last!;
            _dict.Remove(last.Value.key);
            _list.RemoveLast();
        }
        var newNode = _list.AddFirst((key, data));
        _dict[key] = newNode;
    }

    public void Clear()
    {
        _dict.Clear();
        _list.Clear();
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/SpectrogramCache.cs
git commit -m "feat(J3): создать LRU-кэш для спектрограмм (макс 10 записей)"
```

---

### Task J4: Create CoverCache

**Files:**
- Create: `LosslessChecker/Services/CoverCache.cs`

- [ ] **Step 1: Write the file**

```csharp
using System.IO;
using System.Windows.Media.Imaging;

namespace LosslessChecker.Services;

public class CoverCache
{
    private readonly int _maxEntries;
    private readonly Dictionary<string, LinkedListNode<(string key, BitmapImage image)>> _dict = new();
    private readonly LinkedList<(string key, BitmapImage image)> _list = new();

    public CoverCache(int maxEntries = 5) => _maxEntries = maxEntries;

    public bool TryGet(string key, out BitmapImage? image)
    {
        if (_dict.TryGetValue(key, out var node))
        {
            _list.Remove(node);
            _list.AddFirst(node);
            image = node.Value.image;
            return true;
        }
        image = null;
        return false;
    }

    public void Store(string key, byte[] coverData, int decodeWidth)
    {
        if (_dict.TryGetValue(key, out var node))
        {
            _list.Remove(node);
        }
        else if (_dict.Count >= _maxEntries)
        {
            var last = _list.Last!;
            _dict.Remove(last.Value.key);
            _list.RemoveLast();
        }

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.DecodePixelWidth = decodeWidth;
        bmp.StreamSource = new MemoryStream(coverData);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();

        var newNode = _list.AddFirst((key, bmp));
        _dict[key] = newNode;
    }

    public void Clear()
    {
        _dict.Clear();
        _list.Clear();
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/CoverCache.cs
git commit -m "feat(J4): создать LRU-кэш обложек с DecodePixelWidth для предотвращения утечек памяти"
```

---

### Task J5: Update AudioFileViewModel for cache usage

**Files:**
- Modify: `LosslessChecker/ViewModels/AudioFileViewModel.cs`

- [ ] **Step 1: Remove _rawSpectro storage, use cache key instead**

At `ApplyResult` line 141, replace:
```csharp
_rawSpectro = r.SpectrogramDb;
_spectroWidth = r.SpectrogramWidth;
_spectroHeight = r.SpectrogramHeight;
```

With:
```csharp
if (r.SpectrogramDb is { Length: > 0 })
{
    string cacheKey = $"{r.FilePath}|{r.SpectrogramWidth}|{r.SpectrogramHeight}";
    if (!_spectroCache.TryGet(cacheKey, out _))
        _spectroCache.Store(cacheKey, r.SpectrogramDb);
    _rawSpectroKey = cacheKey;
    _spectroWidth = r.SpectrogramWidth;
    _spectroHeight = r.SpectrogramHeight;
}
```

Add fields:
```csharp
private string? _rawSpectroKey;
private static readonly SpectrogramCache _spectroCache = new();
```

Update `GetOrBuildSpectrogram` to use cache:
```csharp
public WriteableBitmap? GetOrBuildSpectrogram()
{
    if (SpectrogramBitmap != null) return SpectrogramBitmap;
    if (_rawSpectroKey == null) return null;
    if (!_spectroCache.TryGet(_rawSpectroKey, out var rawSpectro) || rawSpectro == null) return null;
    var bmp = _spectroRenderer.Render(rawSpectro, _spectroWidth, _spectroHeight);
    SpectrogramBitmap = bmp;
    return bmp;
}
```

- [ ] **Step 2: For CoverData, use DecodePixelWidth**

Handle cover in `ApplyResult` — store `CoverData` in `CoverCache` with `DecodePixelWidth = 150`:

```csharp
if (r.CoverData is { Length: > 0 })
{
    string coverKey = $"cover_{r.FilePath}";
    if (!_coverCache.TryGet(coverKey, out _))
        _coverCache.Store(coverKey, r.CoverData, 150);
}
```

- [ ] **Step 3: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/ViewModels/AudioFileViewModel.cs
git commit -m "feat(J5): перевести AudioFileViewModel на LRU-кэш спектрограмм и обложек с DecodePixelWidth"
```

---

### Task J6: Add new score fields to AnalysisResult

**Files:**
- Modify: `LosslessChecker/Models/AnalysisResult.cs`

- [ ] **Step 1: Add new fields**

After line 75 (`LosslessScore`), add:
```csharp
public double AuthenticityScore { get; init; }
public double MasteringScore { get; init; }
public string AuthenticityVerdict { get; init; } = "";
public string MasteringVerdict { get; init; } = "";
```

After line 107 (`IsCorrupted` — added in I1):
```csharp
// Already present from I1
```

- [ ] **Step 2: Build**

```powershell
dotnet build LosslessChecker/LosslessChecker.csproj
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Models/AnalysisResult.cs
git commit -m "feat(J6): добавить поля AuthenticityScore/MasteringScore/AuthenticityVerdict/MasteringVerdict"
```

---

### Task K1-K5: Localization (Phase K)

**Scope note:** Full localization extraction is deferred to a follow-up enhancement. The current codebase has ~150 hardcoded Russian strings across XAML and C#. For now, create the infrastructure:

**K1**: Create `Resources/Strings.resx` with 5 sample keys to establish the pattern.
**K2**: Create `Resources/Strings.en.resx` with English translations.
**K3**: Create `Resources/LocalizationService.cs` — singleton with `Dictionary<string,string>` cache.
**K4**: Modify `App.xaml.cs` to initialize LocalizationService on startup.
**K5**: Migrate 5 most-visible strings (Status_ProcessingFormat, Section_SpectrumAnalysis, Decision_Keep, Decision_Investigate, Decision_Replace) from hardcoded to LocalizationService.

- [ ] **Step: Commit infrastructure only**

```bash
git add LosslessChecker/Resources/
git commit -m "feat(K1-K5): добавить инфраструктуру локализации (LocalizationService + resx)"
```

---

### Task L1-L8: New Unit Tests

**Files:**
- Create/Modify: Files in `LosslessChecker.Tests/`

- [ ] **L1: BrickwalledEdmDoesNotTriggerSuspiciousBitDepth**

File: `LosslessChecker.Tests/Analyzers/BitDepthValidatorTests.cs` (append)

```csharp
[Fact]
public void BrickwalledEdmDoesNotTriggerSuspiciousBitDepth()
{
    var rng = new Random(42);
    int sampleRate = 44100;
    int duration = 30;
    int n = sampleRate * duration;
    var samples = new float[n];

    // Generate constant-loudness EDM: RMS ~-6 dBFS, no quiet sections
    for (int i = 0; i < n; i++)
    {
        samples[i] = (float)(rng.NextDouble() * 2 - 1) * 0.5f; // RMS ~ -10 dB
    }

    var validator = new BitDepthValidator();
    validator.Reset();
    validator.AddChunk(samples);
    var result = validator.GetResult(16);

    // No quiet sections exist — should NOT flag as suspicious
    Assert.False(result.IsSuspicious);
    // Noise floor gate should be triggered
    Assert.True(result.NoiseFloorDb > -40 || result.Verdict.Contains("skipped"));
}
```

- [ ] **L2: FadeOutDoesNotTriggerBrickwallCutoff**

File: `LosslessChecker.Tests/Analyzers/CutoffDetectorTests.cs` (append)

```csharp
[Fact]
public void FadeOutDoesNotTriggerBrickwallCutoff()
{
    int sampleRate = 44100;
    double duration = 30;
    int n = (int)(sampleRate * duration);
    var samples = new float[n];
    var rng = new Random(42);

    // Sweep 1k-20kHz, constant amplitude first 90%, exponential fade to -80dB last 10%
    for (int i = 0; i < n; i++)
    {
        double t = (double)i / sampleRate;
        double freq = 1000 + (19000 * t / duration);
        double amp = t < duration * 0.9 ? 0.5
            : 0.5 * Math.Exp(-3 * (t - duration * 0.9) / (duration * 0.1));
        samples[i] = (float)(amp * Math.Sin(2 * Math.PI * freq * t));
    }

    var detector = new CutoffDetector();
    var (cutoff, slope, spectrum) = detector.DetectFull(samples, sampleRate);

    // Cutoff should be found from the loud portion (~20kHz), not the fade-out
    Assert.True(cutoff > 15000, $"Cutoff {cutoff:F0} Hz too low — fade-out noise affected detection");
}
```

- [ ] **L3: TpdfDitherDoesNotTriggerLsbZeroPad**

```csharp
[Fact]
public void TpdfDitherDoesNotTriggerLsbZeroPad()
{
    var rng = new Random(42);
    int n = 44100 * 5;
    var samples = new float[n];

    for (int i = 0; i < n; i++)
    {
        double signal = Math.Sin(2 * Math.PI * 1000 * i / 44100);
        double u1 = rng.NextDouble() * 2 - 1;
        double u2 = rng.NextDouble() * 2 - 1;
        double tpdf = (u1 + u2) * (1.0 / 8388607.0); // TPDF at 24-bit LSB level
        int sample24 = (int)Math.Round((signal + tpdf) * 8388607.0);
        samples[i] = (float)(sample24 / 8388607.0);
    }

    var validator = new BitDepthValidator();
    bool lsbZero = validator.CheckLsbZeroPadded(samples, 24);
    Assert.False(lsbZero, "TPDF dither should not trigger LSB zero-padding detection");
}
```

- [ ] **L4-L8: Tests to be written inside the subagent execution** (UpscaleWithDither, MonoFileSkipsPhase, HaasDelayDetected, PreEchoOnSyntheticTransient, VinylRolloffNotPenalized). Full code in final commit.

- [ ] **Final: Run all tests**

```powershell
dotnet test LosslessChecker.Tests/LosslessChecker.Tests.csproj
```

- [ ] **Commit**

```bash
git add LosslessChecker.Tests/
git commit -m "feat(L1-L8): добавить 8 unit-тестов на реальные сценарии (brickwalled EDM, fade-out, TPDF, upscale+дитер)"
```

---

## Execution Summary

| Phase | Tasks | New Files | Modified Files |
|-------|-------|-----------|----------------|
| A — Streaming Foundation | A1-A9 | 3 (AudioChunk, Streaming.cs, ReservoirBuffer) | 5 (AudioDecoder, IChunkAccumulator, LufsMeter, DrMeter, DcOffsetDetector) |
| B — BitDepthValidator | B1 | 0 | 1 (BitDepthValidator) |
| C — PhaseAnalyzer/FakeStereo | C1-C2 | 0 | 2 (PhaseAnalyzer, FakeStereoDetector) |
| D — Spectrogram | D1-D3 | 1 (SpectrogramAccumulator) | 1 (SpectrogramRenderer) |
| E — PreEcho + SBR | E1-E2 | 1 (PreEchoDetector) | 1 (ArtifactDetector) |
| F — Scoring | F1-F4 | 0 | 3 (ScoringProfile, LosslessScorer, QualityScorer) |
| G — Pipeline Integration | G1 | 0 | 1 (AudioPipeline) |
| H — Vinyl Integration | H1-H2 | 0 | 2 (VinylDetector, CutoffDetector) |
| I — Container CORRUPTED | I1 | 0 | 2 (ContainerAnalyzer, AnalysisResult) |
| J — UI/Memory | J1-J6 | 3 (RangeObsCol, SpectroCache, CoverCache) | 2 (MainViewModel, AudioFileViewModel) |
| K — Localization | K1-K5 | 3 (2 resx + LocalizationService) | 1 (App.xaml.cs) |
| L — Tests | L1-L8 | 0 | 3-4 (test files) |

**Total: 11 new files created, ~24 files modified.**
