# LosslessChecker Architecture Redesign

**Date:** 2026-06-25
**Scope:** Full-project refactoring — streaming pipeline, DSP algorithms, scoring, UI/UX, localization, tests
**Status:** Design approved — awaiting implementation plan

---

## 1. Streaming Pipeline (Critical Priority)

### 1.1 AudioDecoder — StreamChunks

Replace `AudioDecoder.Decode()` (returns full `StereoBuffer` in memory) with streaming.

**New entry point:**

```csharp
public static async IAsyncEnumerable<AudioChunk> StreamChunks(
    string filePath,
    int chunkDurationSec = 10,
    [EnumeratorCancellation] CancellationToken ct = default)
```

**AudioChunk struct:**

```csharp
public readonly record struct AudioChunk(
    ReadOnlyMemory<float> Left,
    ReadOnlyMemory<float> Right,
    int SampleRate,
    int Channels,
    double RmsDb,       // computed on-the-fly per chunk
    double StartTime,   // seconds from start
    bool IsLast)
```

**Memory contract:**
- One `ArrayPool<float>` buffer is rented for the entire stream lifetime
- `Left`/`Right` `ReadOnlyMemory` point into this rented buffer — zero-copy for consumers
- Only `ReservoirBuffer` may deep-copy data (see §1.2)
- Rented buffer is returned to the pool in the enumerator's `finally` block
- Chunk size: `sampleRate * chunkDurationSec * channels` ≈ 960,000 floats per chunk for 10s stereo 48kHz (~3.8 MB)

### 1.2 ReservoirBuffer

Accumulates top-N loudest chunks during streaming for FFT analysis in the Post-Stream phase.

**Algorithm:**
1. Maintain a min-heap of `(RmsDb, copyOfChunkData, startTime)` — fixed size N=6
2. For each incoming chunk, compute RMS; if louder than heap minimum, evict min and insert new chunk (deep copy)
3. **Stratified fallback:** If after stream completion the global max RMS < -40 dBFS (entire track is quiet), discard the heap and instead select chunks evenly spaced across the timeline (divide duration into N segments, pick the middle chunk of each segment). This prevents FFT from analyzing background noise.
4. Expose `IReadOnlyList<(float[] Data, double StartTime)> SelectedChunks` for Post-Stream phase

**Zero-allocation hot path:** RMS computation is O(N) but cheap; heap insertion is O(log N). Only N=6 deep copies ever occur per file.

### 1.3 AudioPipeline — 3-Phase Architecture

**Phase 1 — Stream Phase** (hot path, no FFT, synchronous consumption inside `await foreach`):

All `IChunkAccumulator<T>` implementations receive chunks sequentially:

| Accumulator | Purpose | Thread-safety |
|-------------|---------|---------------|
| `TruePeakDetector` | Polyphase oversampling, sample/true peak, ISP, clipping runs | Stateful, single-thread |
| `LufsMeter` | K-weighting, block loudness, short-term loudness | Stateful, single-thread |
| `DrMeter` | 3-sec block RMS/peak for DR computation | Stateful, single-thread |
| `DcOffsetDetector` | Running DC sum for each channel | Stateful, single-thread |
| `PhaseAnalyzer`* | Time-windowed M/S energy per 3-sec block, list of (midRms, sideRms) | Stateful, single-thread |
| `BitDepthValidator`* | RMS histogram (1440 buckets, 0.1 dB) + 4096-sample block RMS | Stateful, single-thread |
| `PreEchoDetector`* | 500-window circular buffer, transient/pre-echo ratio | Stateful, single-thread |
| `ReservoirBuffer` | Top-6 loud chunk heap | Stateful, single-thread |

(* reworked — see relevant sections)

Each accumulator is a fresh instance per pipeline invocation (no reuse across files, avoiding state bleed).

**Phase 2 — Post-Stream Phase** (sequential, documented order):

1. `SpectrogramAccumulator.Finalize()` — completes time-binned grid
2. `VinylDetector.Detect(spectrum, sampleRate, samples)` — must run before CutoffDetector
3. `CutoffDetector.DetectFull(reservoirData, sampleRate)` — on reservoir chunks
4. `CutoffDetector.ClassifyCutoff(cutoff, slope, sampleRate, vinylResult.IsVinylRip)` — vinyl-aware
5. `CutoffDetector.ClassifyBandwidth(...)` — final bandwidth/type classification
6. `ArtifactDetector.Detect(reservoirData, sampleRate, cutoffHz)` — MP3 sizzle + SBR (new)
7. `ArtifactDetector.DetectSpectralHoles(spectrum, nyquist)` 
8. `UpscaleDetector.Detect(spectrum, sampleRate)` 
9. `PhaseAnalyzer.GetResult()` — computes Spectral Flatness, Haas correlation on reservoir data
10. `ResamplingDetector.DetectFromSpectrum(spectrum, sampleRate)`
11. `ContainerAnalyzer.Analyze(filePath, ...)` — FLAC MD5, RIFF, MQA, HDCD

**Phase 3 — Scoring Phase** (lightweight aggregation):

1. `FakeStereoDetector.IsFakeStereo()` — consumes PhaseAnalyzer output
2. `LosslessScorer.AuthenticityScore(result)` — deductive penalty system, 100 → subtract
3. `LosslessScorer.MasteringScore(result)` — heuristic genre clustering, anomaly-based penalties
4. `VerdictGenerator.Generate(result)` — cross-axis matrix (KEEP/INVESTIGATE/REPLACE/CORRUPTED)

### 1.4 SpectrogramAccumulator — Time-Binned Grid

**Design:**
- Fixed grid: 2048 time columns × 1024 frequency rows
- On `AddChunk(chunk)`: compute FFT of chunk, for each bin, accumulate magnitude into the appropriate time column
- If `TotalDuration` is known from file header: pre-compute `timePerColumn = duration / 2048`
- If `TotalDuration` is unknown: start with assumption of 5 minutes; if stream exceeds grid, progressive downsampling — merge adjacent columns, double `timePerColumn`
- `Finalize()`: normalize accumulated magnitudes by count per column
- Memory: `2048 × 1024 × sizeof(float)` = 8 MB fixed

**Header-first approach:** `AudioPipeline` reads `TotalDuration` from `AudioFormatReader` before streaming begins.

---

## 2. BitDepthValidator — RMS Histogram

### 2.1 Histogram Structure

```csharp
private readonly int[] _rmsHistogram = new int[1440]; // -144.0 dB to 0.0 dB, 0.1 dB buckets
private const double HistoDbMin = -144.0;
private const double HistoDbStep = 0.1;
```

### 2.2 Streaming Accumulation

Each chunk is processed in blocks of 4096 samples. For each block:

```
rmsDb = 20 * log10(sqrt(sumSq / 4096))
bucketIndex = clamp((rmsDb - HistoDbMin) / HistoDbStep, 0, 1439)
_rmsHistogram[bucketIndex]++
```

### 2.3 GetResult() Algorithm

1. **Compute total blocks:** `totalBlocks = sum of all histogram buckets`
2. **Cumulative threshold scan from quiet to loud:**
   - Walk from bucket 0 upward, accumulating count
   - Stop when accumulated ≥ 1% of totalBlocks
   - The bucket where threshold is reached = **true noise floor**
3. **Safety gate:** If true noise floor > -40 dBFS → no quiet sections exist (brickwalled track) → return `EffectiveBitDepth = N/A`, skip noise floor analysis, rely on LSB check only
4. **Bimodal detection:** Compute derivative of cumulative distribution. A significant gap (adjacent buckets with ratio > 10:1) separates noise floor cluster from music content cluster. Use the quieter cluster as noise floor.
5. **Effective bit depth:** `effectiveBits = (-noiseFloorDb) / 6`, clamped to `claimedBitDepth`
6. **Suspect verdict:** `noiseFloorDb > expectedNoiseFloor + 16 && noiseFloorDb > -50`

### 2.4 LSB Zero-Padding Check

- Existing logic at `CheckLsbZeroPaddedChannel` is correct (95% zero threshold)
- **Addition:** Also check for **constant non-zero LSB pattern** (naive upscale): if > 95% of LSB bytes have the same non-zero value → flag as `"Constant dither / naive upscale"`
- TPDF dither detection is **unnecessary** — true TPDF dither has < 1% zero LSBs, so the 95% zero threshold already protects against it

### 2.5 Reset Method

```csharp
public void Reset()
{
    Array.Clear(_rmsHistogram, 0, _rmsHistogram.Length);
    _totalBlocks = 0;
}
```
Required for instance reuse in analyzer pools.

---

## 3. FakeStereoDetector — M/S Analysis

### 3.1 PhaseAnalyzer as IChunkAccumulator

During streaming, `PhaseAnalyzer` computes per 3-second block (synchronized with DrMeter block size):

```
Mid = (L + R) / 2
Side = (L - R) / 2
midRms = sqrt(Σ Mid² / N)
sideRms = sqrt(Σ Side² / N)
```

Stores `List<(double midRms, double sideRms)>` for each block.

### 3.2 Post-Stream Phase — GetResult()

1. **Percentile analysis:**
   - For each block: `sideToMidRatio = sideRms / max(midRms, 1e-10)`
   - Compute 85th percentile of all ratios
   - If > 85% of blocks have `sideToMidRatio < 0.01` → True Mono / Hard Fake Stereo
   - If only 30-40% blocks have low ratio (e.g., centered vocals) but rest have active Side → Genuine Stereo

2. **Spectral Flatness (Wiener Entropy)** on reservoir data:
   - For Side channel FFT above 2 kHz: `flatness = geometricMean(spectrum) / arithmeticMean(spectrum)`
   - If Side flatness < 0.1 while Mid flatness is normal → algorithmic expansion (chorus/micro-pitch shift)
   - This is O(N) — no Shannon entropy logarithm per bin

3. **Haas Effect Detection:**
   - On reservoir chunks, compute sliding cross-correlation between L and R with lag window [-500, +500] samples
   - If max correlation occurs at |lag| = 100-300 samples (2-7 ms at 44.1kHz) and Side spectral energy is low → Fake Stereo (Haas Delay)

4. **Mono skip:** If `Channels == 1`, return immediately: `Correlation = 1.0`, `IsFakeStereo = false`, skip all M/S checks.

### 3.3 Classification Table

| Condition | Verdict |
|-----------|---------|
| Channels == 1 | Genuine Mono (skip all checks) |
| >85% blocks sideToMidRatio < 0.001 | True Mono (in stereo container) |
| >85% blocks sideToMidRatio < 0.01 AND correlation > 0.99 | Fake Stereo (copied) |
| Side Spectral Flatness < 0.1, Mid Flatness normal | Fake Stereo (algorithmic expansion) |
| Haas lag detected (100-300 samples) | Fake Stereo (Haas Delay) |
| Otherwise | Genuine Stereo |

---

## 4. Scoring Architecture — Two Independent Axes

### 4.1 Authenticity Score (0-100%)

**Deductive penalty system** — start at 100, subtract for violations:

| Violation | Penalty |
|-----------|---------|
| Brickwall at codec frequencies (≤ 22.1 kHz for Hi-Res, ratio < 0.90 for standard) | -30 to -60 |
| Filtered shelf below 17 kHz | -12 to -20 |
| Strong artifacts (MP3 sizzle / SBR) | -35 |
| Medium artifacts | -20 |
| Weak artifacts | -8 |
| LSB zero-padded (24-bit claim) | -100 (instant FAKE) |
| LSB constant-value (naive upscale) | -80 |
| Bit depth suspicious (noise floor mismatch) | -20 |
| Aliasing detected | -15 |
| Ringing detected | -10 |
| Upscale detected | -25 |
| Fake Stereo detected | -10 |
| Abrupt Edges detected | -5 |
| FLAC MD5 mismatch / RIFF corruption | -100 (instant CORRUPTED) |

**Classification:**
- ≥ 70 → `TRUE` (genuine lossless)
- 50-69 → `UNCERTAIN`
- < 50 → `FALSE` (replace)

### 4.2 Mastering Score (0-100%)

**Heuristic genre clustering** — auto-detect genre from LUFS + DR correlation, NOT from ID3 tags:

| LUFS Range | DR Range | Inferred Profile |
|------------|----------|-----------------|
| > -8 | DR 3-5 | Loud Modern (EDM/Pop/Hip-Hop) |
| -14 to -8 | DR 5-8 | Standard (Rock/Indie) |
| < -14 | DR 8+ | Dynamic (Jazz/Classical/Acoustic) |

**Anomaly-based penalties** — penalize deviation from genre norm, not absolute values:

| Metric | Penalty Logic |
|--------|--------------|
| LUFS | Penalize if outside genre-expected range by > 6 dB |
| DR | Penalize if below genre minimum (EDM ≥ 3, Rock ≥ 5, Classical ≥ 8) |
| Hard Clipping (3+ consecutive 0 dBFS samples) | -20 (severe) |
| ISP (TruePeak > 0 dBTP) | -2 (minimal — common in modern masters) |
| DC Offset (> 1%) | -8 |
| Phase correlation < -0.5 | -12 |
| PLR < genre threshold | -10 |

**Classification:**
- ≥ 80 → `KEEP (Excellent)`
- 50-79 → `KEEP (Good)`
- 30-49 → `KEEP (Fair)`

### 4.3 Verdict Matrix (Cross-Axis)

```
          Mastering →
          Excellent     Good         Fair
Auth ↓
TRUE      KEEP/Exc      KEEP/Good    KEEP/Fair
UNCERTAIN INVESTIGATE   INVESTIGATE  INVESTIGATE
FALSE     REPLACE       REPLACE      REPLACE
CORRUPTED CORRUPTED     CORRUPTED    CORRUPTED
```

**Rules:**
- If Authenticity < 50 → always REPLACE (beautiful-sounding transcode is still fake)
- If CORRUPTED → always CORRUPTED (spectral analysis is meaningless on damaged bitstream)
- UI labels: `"LOSSLESS (CD) / Loud Master"`, `"FAKE 24bit / Good Mastering"`

### 4.4 Metric Reassignment

Moved from Quality/Mastering to Authenticity:
- **Fake Stereo** — mono-in-stereo is a format defect, not a creative choice
- **Abrupt Edges** — non-zero-crossing cuts indicate bad CD rip, not mastering quality

---

## 5. ArtifactDetector — SBR Detection

### 5.1 HE-AAC Spectral Band Replication

Runs in Post-Stream phase on reservoir data:

1. **Local cutoff detection for SBR:** Find the point of sharp energy drop in 12-16 kHz range (may differ from global CutoffDetector result). Use the same derivative method but restricted to the 12-18 kHz band.
2. **Spectral Flatness comparison:**
   - Upper band (cutoff to cutoff × 1.5): compute Spectral Flatness
   - Lower band (cutoff × 0.5 to cutoff): compute Spectral Flatness
   - If upper band flatness is **anomalously low** (< 0.15) while lower band flatness is normal (> 0.3) → isolated tonal patches = SBR signature
3. **Envelope cross-correlation:**
   - Compute spectral envelope (smoothed magnitude) for both bands
   - Cross-correlate; if correlation > 0.7 AND upper band is 12-24 dB below lower → secondary SBR confirmation
4. **Combined verdict:** If both tonal patch + envelope correlation detected → `ArtifactType = "AAC SBR"`
5. **Existing MP3 sizzle detection** (lines 134-174) unchanged — runs in parallel

---

## 6. CutoffDetector — Vinyl Integration

### 6.1 Execution Order

VinylDetector runs in Post-Stream Phase step 2, **before** `ClassifyCutoff` (step 4).

### 6.2 Classification Override

If `vinylResult.IsVinylRip == true`:
- AND cutoff in 16-18 kHz with Filtered shelf → override shelf type to `"Vinyl Rolloff"`
- AND `encoderMatch = "None (Vinyl Transfer)"`
- Score penalty: **zero** (genuine analog limitation)

### 6.3 Anti-False-Positive Guard

VinylDetector must verify:
1. Energy above cutoff is **not digital silence** (< -90 dBFS would mean LP filter + dither, not vinyl)
2. Energy above cutoff is in -40 to -60 dBFS range (true analog noise floor)
3. Infrasonic rumble (8-15 Hz) is present — already computed as `RumbleRatio`

If rumble absent AND above-cutoff is digital zeros → reject vinyl classification → treat as `"Filtered / Mastered LPF"`.

---

## 7. PreEchoDetector — Standalone IChunkAccumulator

### 7.1 Extraction from ArtifactDetector

Current logic at `ArtifactDetector.cs:176-202` is extracted into `PreEchoDetector.cs`.

### 7.2 Circular Buffer Design

- Fixed-size circular buffer of 500 micro-windows (each 2 ms at current sample rate)
- On each chunk: fill micro-windows, compute RMS, detect transients (4× amplitude jump)
- Check for pre-echo: noise burst in window before transient
- State maintained across chunks via circular buffer
- Returns `(hasPreEcho, preEchoCount)` at `GetResult()`

### 7.3 Wideband Check

Pre-echo is wideband noise before a transient. A legitimate fast transient (snare hit, synth click) is tonal. Check: pre-echo region has high Spectral Flatness (> 0.5) while transient has low Spectral Flatness → confirm pre-echo. This is computed on reservoir data in Post-Stream phase.

---

## 8. UI & Memory Architecture

### 8.1 Batched Collection Updates

**MainViewModel file loading:**
- Build full `List<AudioFileViewModel>` in background
- Assign to `ObservableCollection` via one-time replacement using `Dispatcher.Yield(Background)` between batches of 200-500 items
- Avoid `NotifyCollectionChangedAction.Reset` (destroys virtualization scroll state)

**Incremental tree updates:**
- `PopulateArtistGroups()` is called **exactly once** after batch completes
- During analysis: when a file completes, find existing `ArtistGroup` / `AlbumGroup` and add the file to its internal `ObservableCollection<Tracks>` without rebuilding the tree
- `_artistGroupsDirty` flag with periodic debounce is rejected — too much GC pressure from LINQ `GroupBy` on 10k items

### 8.2 LRU Caches

**SpectrogramCache:**
- Max 10 entries, LRU eviction
- Each entry: `float[2048 * 1024]` = ~8 MB
- `AudioFileViewModel` stores cache key, not raw float array
- `GetOrBuildSpectrogram()` fetches from cache or builds + caches
- On selection loss: no explicit eviction (LRU handles it)

**CoverCache:**
- Uses `BitmapImage.DecodePixelWidth = 150` when loading from `CoverData`
- Without this, WPF decodes a 3000×3000 cover into an uncompressed 36 MB bitmap
- Max 5 entries
- Stored in cache by album key

### 8.3 Spectrogram Zoom Re-rendering

**Debounced async rendering:**
1. On mouse wheel: immediately apply `ScaleTransform` to existing image (smooth, zero latency)
2. Start a `Task.Delay(150ms)` debounce timer
3. After debounce: `Task.Run(() => SpectrogramRenderer.RenderRegion(startTime, endTime, lowFreq, highFreq, width, height))`
4. On completion: swap `WriteableBitmap` on UI thread via `Dispatcher.InvokeAsync`

**Inverse log coordinate mapping for tooltips:**
- Y-axis uses log scale: `freq = 20 * Math.Pow(10, (1 - pixelY / height) * Math.Log10(nyquist / 20))`
- Click handler: invert this formula to get exact frequency and interpolate dB value from grid

---

## 9. Localization (i18n)

### 9.1 Resource Files

- `Resources/Strings.resx` — default (Russian) culture
- `Resources/Strings.en.resx` — English

### 9.2 LocalizationService (Singleton)

```csharp
public class LocalizationService
{
    private readonly Dictionary<string, string> _cache;
    public string Get(string key, params object[] args) => string.Format(Culture, _cache[key], args);
    public CultureInfo Culture { get; set; }
}
```

- All strings cached to `Dictionary<string, string>` at startup — no `ResourceManager.GetString` calls during UI updates
- Formatted strings stored as templates: `"Processing: {0} [{1}/{2}]"` → `string.Format(culture, template, args)`
- XAML bindings via `{x:Static resx:Strings.KeyName}` for static content
- Dynamic content (MetricItems, VerdictGenerator) uses `LocalizationService.Get()`

### 9.3 Key Scope

All strings in `AudioFileViewModel.BuildMetricItems`, `VerdictGenerator`, `MainViewModel`, and XAML DataGrid headers extracted to resx.

---

## 10. Enhanced Test Coverage

### 10.1 New Tests

| Test | Class | What It Validates |
|------|-------|-------------------|
| `BrickwalledEdmDoesNotTriggerSuspiciousBitDepth` | `BitDepthValidatorTests` | RMS histogram on track with no quiet sections; EffectiveBitDepth = N/A, IsSuspicious = false |
| `FadeOutDoesNotTriggerBrickwallCutoff` | `CutoffDetectorTests` | Exponential amplitude sweep to -80 dB; cutoff found from loud portion, not fade-out noise |
| `TpdfDitherDoesNotTriggerLsbZeroPad` | `BitDepthValidatorTests` | 16-bit signal + TPDF dither (sum of two uniform distributions) in 24-bit container; LsbZeroPadded = false |
| `UpscaleWithDitherAbove22kDetected` | `UpscaleDetectorTests` | 44.1k → 96k upscale with pink noise added in 22-48 kHz band; IsUpscale = true |
| `MonoFileSkipsPhaseAndFakeStereoChecks` | `PhaseAnalyzerTests` | Channels=1; returns neutral PhaseResult, no M/S computation |
| `HaasDelayDetectedAsFakeStereo` | `PhaseAnalyzerTests` | 5ms channel delay + chorus; IsFakeStereo = true via Haas detection |
| `PreEchoDetectedOnSyntheticTransient` | `PreEchoDetectorTests` | Synthetic signal with noise burst 2ms before transient; hasPreEcho = true |
| `VinylRolloffNotPenalizedInScoring` | `LosslessScorerTests` | 17kHz vinyl rolloff with rumble; AuthenticityScore ≥ 70 (no codec penalty) |

### 10.2 Test Signal Construction

- **TPDF dither:** Generate two independent uniform random streams U1, U2 in [-1, 1]; add `(U1 + U2) * ditherAmplitude` to 16-bit signal before quantizing to 24-bit
- **Fade-out sweep:** `frequency(t) = 1000 + t/duration * 19000`, `amplitude(t) = exp(-3 * max(0, t - 0.9*duration) / (0.1*duration))` — constant amplitude for first 90%, then exponential decay to -80 dB
- **Pink noise:** Generate via Voss-McCartney algorithm (recursive random generators), bandpass to 22-48 kHz at -50 dB relative to signal
- **Haas delay:** Mono signal → L = original, R = original delayed by 220 samples (5ms at 44.1kHz)

---

## 11. Implementation Order

Files are listed in dependency order — each step builds on the previous.

### Phase A — Streaming Foundation (CRITICAL)

| Step | File | Action |
|------|------|--------|
| A1 | `Models/AudioChunk.cs` | **Create** — `readonly record struct` with RmsDb, StartTime |
| A2 | `Services/AudioDecoder.Streaming.cs` | **Create** — `StreamChunks()` with `ArrayPool` contract |
| A3 | `Models/StereoBuffer.cs` | **Modify** — mark `Decode()` as obsolete, keep for backward compat |
| A4 | `Services/ReservoirBuffer.cs` | **Create** — top-6 heap + stratified fallback |
| A5 | `Services/ChunkProcessing/IChunkAccumulator.cs` | **Modify** — add `Reset()` method to interface |
| A6 | `Services/Analyzers/TruePeakDetector.cs` | **Modify** — add `Reset()` |
| A7 | `Services/Analyzers/LufsMeter.cs` | **Modify** — add `Reset()` |
| A8 | `Services/DrMeter.cs` | **Modify** — add `Reset()` |
| A9 | `Services/Analyzers/DcOffsetDetector.cs` | **Modify** — add `Reset()` |

### Phase B — BitDepthValidator Rework

| Step | File | Action |
|------|------|--------|
| B1 | `Services/BitDepthValidator.cs` | **Rewrite** — RMS histogram (1440 buckets), cumulative 1%, -40dB gate, constant-LSB detection, `Reset()` |

### Phase C — PhaseAnalyzer & FakeStereo Rework

| Step | File | Action |
|------|------|--------|
| C1 | `Services/Analyzers/PhaseAnalyzer.cs` | **Rewrite** — `IChunkAccumulator` with 3-sec time-windowed M/S, Spectral Flatness, Haas detection, mono skip |
| C2 | `Services/FakeStereoDetector.cs` | **Simplify** — consume PhaseAnalyzer output only |

### Phase D — Spectrogram Rework

| Step | File | Action |
|------|------|--------|
| D1 | `Services/SpectrogramAccumulator.cs` | **Create** — Time-Binned Accumulator 2048×1024 with header-first + progressive downsampling |
| D2 | `Services/SpectrogramBuilder.cs` | **Modify** — delegate to SpectrogramAccumulator |
| D3 | `Services/SpectrogramRenderer.cs` | **Modify** — add `RenderRegion()` for zoom |
| D4 | `Views/SpectrogramWindow.xaml.cs` | **Modify** — debounced async re-render, inverse log coordinate mapping |

### Phase E — PreEchoDetector Extraction

| Step | File | Action |
|------|------|--------|
| E1 | `Services/PreEchoDetector.cs` | **Create** — `IChunkAccumulator` with 500-window circular buffer |
| E2 | `Services/ArtifactDetector.cs` | **Modify** — remove inline PreEcho code, add SBR detection |

### Phase F — Scoring Rework

| Step | File | Action |
|------|------|--------|
| F1 | `Services/Analysis/ScoringProfile.cs` | **Modify** — add genre-specific DR thresholds, new penalty values |
| F2 | `Services/Analysis/LosslessScorer.cs` | **Modify** — split into `AuthenticityScore()` (deductive) + `MasteringScore()` (anomaly-based), heuristic genre clustering |
| F3 | `Services/Analysis/QualityScorer.cs` | **Modify** — reduced ISP penalty, anomaly-based LUFS |
| F4 | `Services/VerdictGenerator.cs` | **Modify** — cross-axis verdict matrix, composite labels |

### Phase G — AudioPipeline 3-Phase Integration

| Step | File | Action |
|------|------|--------|
| G1 | `Services/AudioPipeline.cs` | **Rewrite** — 3-phase pipeline, documented Post-Stream ordering |

### Phase H — VinylDetector Integration

| Step | File | Action |
|------|------|--------|
| H1 | `Services/VinylDetector.cs` | **Modify** — anti-false-positive guard (digital silence vs analog noise check) |
| H2 | `Services/CutoffDetector.cs` | **Modify** — vinyl-aware `ClassifyCutoff`, pass `IsVinylRip` |

### Phase I — ContainerAnalyzer CORRUPTED Flag

| Step | File | Action |
|------|------|--------|
| I1 | `Services/ContainerAnalyzer.cs` | **Modify** — add `IsCorrupted` flag (FLAC MD5 + RIFF check) |

### Phase J — UI & Memory Rework

| Step | File | Action |
|------|------|--------|
| J1 | `Models/RangeObservableCollection.cs` | **Create** — batched collection |
| J2 | `ViewModels/MainViewModel.cs` | **Modify** — batched loading, incremental tree updates, one-time PopulateArtistGroups |
| J3 | `Services/SpectrogramCache.cs` | **Create** — LRU cache max 10 |
| J4 | `Services/CoverCache.cs` | **Create** — LRU cache max 5 with DecodePixelWidth |
| J5 | `ViewModels/AudioFileViewModel.cs` | **Modify** — remove raw spectro storage, use cache keys, DecodePixelWidth for covers |
| J6 | `Models/AnalysisResult.cs` | **Modify** — add `IsCorrupted` flag, split score fields |

### Phase K — Localization

| Step | File | Action |
|------|------|--------|
| K1 | `Resources/Strings.resx` | **Create** — Russian default |
| K2 | `Resources/Strings.en.resx` | **Create** — English |
| K3 | `Services/LocalizationService.cs` | **Create** — singleton with cached dictionary |
| K4 | `App.xaml.cs` | **Modify** — initialize LocalizationService |
| K5 | All XAML and CS string files | **Modify** — replace hardcoded strings with resx references |

### Phase L — Tests

| Step | File | Action |
|------|------|--------|
| L1-L8 | Various `*Tests.cs` files | **Create** — 8 new tests as specified in §10 |

---

## 12. What Stays Unchanged

- **TruePeakDetector** polyphase filter math (Kaiser-windowed sinc, proper normalization)
- **LufsMeter** K-weighting biquad filters and EBU R128 relative/absolute gating
- **CutoffDetector** derivative-based slope detection (the "killer feature")
- **Dark.xaml** theme and overall MVVM structure (CommunityToolkit.Mvvm source generators)
- **AnalysisCache** SHA256-based file identity caching
- **FileScanner**, **AudioFormatReader**, **TagReader**, **Mp4CodecReader** — no changes needed
- **ContainerAnalyzer** MQA/HDCD heuristic detection (no changes to existing logic)
