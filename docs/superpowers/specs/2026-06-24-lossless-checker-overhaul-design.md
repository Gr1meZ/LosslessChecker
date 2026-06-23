# LosslessChecker Full Overhaul — Design Spec

**Date:** 2026-06-24
**Scope:** Full rewrite of analysis pipeline, scoring system, UI, and test coverage per Expert Audio Forensics algorithm.

---

## 1. Architecture: Clean Pipeline Pattern

Replace the monolithic `AudioAnalyzer` (183 lines) with a thin orchestrator that runs 10 independent analyzers through a pipeline. Each analyzer takes `StereoBuffer` (raw PCM) as input, returns a typed result struct, and has zero dependencies on other analyzers.

```
AudioFile → AudioFormatReader → AudioDecoder → Pipeline → VerdictGenerator → ViewModel
                                     │              │
                               StereoBuffer    AuthenticityClassifier
                               (float[] L,R)   QualityScorer
                                     │
              ┌──────────────────────┼──────────────────────┐
              │           │         │         │            │
         CutoffDetector  Artifact  TruePeak  LufsMeter   DrMeter
                         Detector  Detector
              │           │         │         │            │
         BitDepth        Upscale   DcOffset  Phase       Spectrogram
         Validator       Detector  Detector  Analyzer    Builder
```

### Key changes from current:
- Decode to **stereo** `float[]` L/R arrays (was mono)
- Each analyzer is a self-contained class with `Analyze(StereoBuffer) → T` signature
- `AudioAnalyzer` becomes ~40-line orchestrator
- `ScoreCalculator` split into `AuthenticityClassifier` + `QualityScorer`
- `VerdictGenerator` produces structured 5-section report
- `SpectrogramBuilder` extracted from `AudioAnalyzer` into own service
- No `GC.Collect()` calls — let the GC manage memory naturally

---

## 2. Analyzer Specifications

### New Analyzers

#### TruePeakDetector
- **Algorithm**: 4x oversampling per ITU-R BS.1770-4 (zero-stuff + low-pass filter)
- **Outputs**: `TruePeakL` (dBTP), `TruePeakR` (dBTP), `SamplePeakL` (dBFS), `SamplePeakR` (dBFS), `ClippingPercent`, `HasISP`
- **Threshold**: TruePeak > 0.00 dBTP → ISP DISTORTION
- **Clipping detection**: 3+ consecutive samples at exactly 0 dBFS (same as current)

#### LufsMeter
- **Algorithm**: ITU-R BS.1770-4
  - K-weighting filter (pre-filter + high-shelf)
  - 400ms gating blocks, absolute threshold -70 LUFS, relative threshold -10 LU below absolute
- **Outputs**: `IntegratedLUFS`, `LoudnessRange` (LRA)
- **Thresholds**:
  - > -7 LUFS → EXTREME LOUDNESS
  - -8 to -11 LUFS → COMMERCIAL LOUD
  - -14 LUFS → STREAMING TARGET
  - < -16 LUFS → DYNAMIC

#### DcOffsetDetector
- **Algorithm**: Compute mean of all samples per channel, express as percentage of full scale
- **Outputs**: `DcOffsetL` (%), `DcOffsetR` (%), `HasDcOffset`
- **Threshold**: > 0.001% deviation → flagged

#### PhaseAnalyzer
- **Algorithm**: Cosine correlation coefficient per 4096-sample block, averaged across all blocks
- **Outputs**: `Correlation` (-1.0 to +1.0), `IsMonoCompatible` (Correlation >= 0)
- **Thresholds**:
  - +1.0 → perfect mono
  - 0.0 to +0.9 → normal stereo
  - < 0.0 → phase issues, mono incompatible

### Enhanced Existing Analyzers

#### CutoffDetector
- **Keep**: Derivative-based slope detection (steepest negative dB/octave) from commit `6164854`
- **Add**: Encoder frequency mapping:
  - Cutoff ≤ 16.5 kHz → MP3 128-192 kbps → FAKE
  - Cutoff 16.5-18.5 kHz → MP3 192-256 kbps → FAKE
  - Cutoff 18.5-20.0 kHz → MP3 320 kbps / AAC 256 kbps → SUSPICIOUS
  - Cutoff 20.0-21.5 kHz → Possible LP filter → SUSPICIOUS
  - Cutoff > 21.5 kHz → TRUE LOSSLESS
- **Add**: Shelf/Noise Floor analysis — check if noise above cutoff is flat (brickwall = lossy) or gradual (natural rolloff = analog/digital)
- **Add**: Hi-Res check — if sample rate ≥ 88.2 kHz and HF content absent above 22 kHz → FAKE HI-RES
- **New outputs**: `ShelfType` ("Brickwall"|"Natural"), `EncoderMatch` ("MP3 128"|"MP3 256"|"MP3 320"|"None")

#### ArtifactDetector
- **Keep**: Spectral flatness + spectral slope in HF region
- **Add**: MP3-specific "sizzle" detection (characteristic HF noise pattern in 15.5-16.5 kHz band)
- **New output**: `ArtifactType` ("MP3"|"AAC"|"Unknown"|"None")

#### DrMeter
- **Keep**: TT DR Meter (0.5s blocks, top 20% percentile)
- **Add**: Per-channel DR measurement, RMS peak
- **Thresholds**: DR 2-5 catastrophic, DR 6-8 heavy compression, DR 9-12 good, DR 13+ audiophile

#### BitDepthValidator
- **Keep**: Noise floor estimation on quietest 10% of blocks
- **Add**: Direct LSB zero-padding check — for 24-bit files, mask lower 8 bits, verify if all zero in loudest 10% of blocks
- **New output**: `LsbZeroPadded` (bool)

#### UpscaleDetector
- **Keep**: HF content check above 22 kHz relative to 1-5 kHz reference band
- **Add**: Dither noise detection — if HF spectrum is flat (characteristic of shaped dither) → upscale
- **Thresholds**: > -30 dB = valid Hi-Res, -30 to -50 dB = questionable, < -50 dB = likely upscale

#### SpectrogramBuilder
- **Extracted** from AudioAnalyzer into independent service
- **Same logic**: FFT with 4096-point, 2048-hop, 256 freq bins, max 300 frames, flat byte[] with dB values
- Takes `StereoBuffer`, returns `(byte[] data, int width, int height)`

---

## 3. Dual-Axis Scoring & Decision Engine

### Axis 1: Authenticity Classification (deterministic rules)

```
FAKE HI-RES     ← Hi-Res file with HF cutoff < 22 kHz
FAKE LOSSLESS   ← cutoff ≤ 16.5 kHz
FAKE LOSSLESS   ← cutoff ≤ 18.5 kHz AND has artifacts
FAKE LOSSLESS   ← cutoff ≤ 20.0 kHz AND has artifacts AND shelf = Brickwall
SUSPICIOUS      ← cutoff 18.5-20.0 kHz (could be MP3 320 or LP filter)
SUSPICIOUS      ← cutoff 20.0-21.5 kHz (possible LP filter)
SUSPICIOUS      ← isUpscale = true
SUSPICIOUS      ← bitDepthSuspicious AND lsbZeroPadded
TRUE LOSSLESS   ← cutoff > 21.5 kHz, no artifacts, no red flags
```

### Axis 2: Quality Score (1-10, start at 10, subtract)

| Defect | Penalty |
|--------|---------|
| DR < 6 | -3 |
| DR 6-8 | -1 |
| Clipping > 0.5% | -2 |
| Clipping > 0% | -1 |
| True Peak > 0 dBTP | -1 |
| True Peak > +1 dBTP | -1 more |
| LUFS > -7 (extreme loud) | -2 |
| LUFS > -10 (commercial loud) | -1 |
| DC Offset > 0.001% | -1 |
| Correlation < 0 | -2 |
| LSB zero-padded | -1 |

Floor at 1. Minimum possible = 1, Maximum = 10.

### Decision Matrix

| Authenticity | Quality | Decision |
|-------------|---------|----------|
| TRUE LOSSLESS | 7-10 | **KEEP** |
| TRUE LOSSLESS | 4-6 | **KEEP** |
| TRUE LOSSLESS | 1-3 | **KEEP (poor master)** |
| SUSPICIOUS | any | **INVESTIGATE** |
| FAKE LOSSLESS | any | **REPLACE** |
| FAKE HI-RES | any | **REPLACE** |

**Critical invariant**: A TRUE LOSSLESS file is NEVER classified as REPLACE, even with terrible mastering (DR2, clipped, etc.). This prevents false negatives and endless search loops.

### PLR (Peak to Loudness Ratio)
`PLR = TruePeak_dBTP - IntegratedLUFS`
- PLR < 6-7 dB → macro-dynamics destroyed (informational only, no penalty)

---

## 4. 5-Section Structured Report

```
[FILENAME]
1. LOSSLESS STATUS: TRUE LOSSLESS | cutoff at 21.8 kHz, natural rolloff, no encoder signature
2. CLIPPING & PEAK: CLEAN | Sample Peak -0.3 dBFS, True Peak -0.1 dBTP
3. DYNAMICS: AUDIOPHILE | DR14, Integrated -18.2 LUFS, PLR 18.1 dB
4. TECHNICAL RED FLAGS: None
5. OVERALL VERDICT: 9/10 | Excellent mastering, genuine lossless — KEEP
```

For files with issues:
```
4. TECHNICAL RED FLAGS:
   - DC Offset detected: L=0.012%, R=0.008%
   - Phase correlation: -0.34 (mono incompatible)
   - 24-bit file has zero-padded LSBs (effective 16-bit)
```

---

## 5. Data Model

### AnalysisResult (overhauled — 36 fields)

Removed: `LosslessScore`, `Verdict`, `Status`, `NoiseFloorDb`, `BitDepthVerdict`, `UpscaleVerdict`, `AveragedSpectrum`, `Bitrate`

Renamed: `TruePeak` → `SamplePeakDb` (was actually sample peak)

New fields:
- `Channels` (int) — from file header
- `ShelfType` (string) — "Brickwall" | "Natural"
- `EncoderMatch` (string) — "MP3 128" | "MP3 256" | "MP3 320" | "None"
- `ArtifactType` (string) — "MP3" | "AAC" | "Unknown" | "None"
- `TruePeakDb` (double) — actual dBTP via 4x oversampling
- `HasIsp` (bool) — TruePeak > 0 dBTP
- `IntegratedLufs` (double) — ITU 1770-4
- `LoudnessRange` (double) — LRA
- `Plr` (double) — Peak-to-Loudness Ratio
- `LsbZeroPadded` (bool) — direct mask check
- `DcOffsetL` (double), `DcOffsetR` (double) — percentage
- `Correlation` (double) — -1.0 to +1.0
- `IsMonoCompatible` (bool)
- `Authenticity` (string) — "TRUE LOSSLESS" | "SUSPICIOUS" | "FAKE LOSSLESS" | "FAKE HI-RES"
- `QualityScore` (int) — 1-10
- `Decision` (string) — "KEEP" | "KEEP (poor master)" | "INVESTIGATE" | "REPLACE"
- `StructuredReport` (string) — 5-section formatted text

### OriginalFormat record (extended)
Add `Channels` field to the record returned by `AudioFormatReader`.

### StereoBuffer (new type)
```csharp
public record StereoBuffer(float[] Left, float[] Right, int SampleRate)
{
    public int Length => Left.Length;
    public bool IsStereo => Right != null && Right.Length > 0;
}
```

---

## 6. UI Changes

### Layout: Master-Detail

```
┌──────────────────────────────────────────────────────────┐
│ [Select Folder] [Stop]  ████████████░░░░  Progress       │
├───────────────────────────┬──────────────────────────────┤
│  DataGrid (~65% width)    │  Detail Panel (~35%)         │
│                           │                              │
│  Core columns (visible):  │  ┌──────────────────────┐    │
│  Icon│Name│Fmt│Cutoff│DR  │  │ Spectrogram (260px)  │    │
│  │TPeak│Clip│Auth│Qual│Dec│  │ with cutoff line     │    │
│                           │  └──────────────────────┘    │
│  Detail columns (scroll): │                              │
│  SR│Bits│Ch│LUFS│Phase    │  ┌──────────────────────┐    │
│  │DC Off│                 │  │ 5-Section Report     │    │
│                           │  │ (scrollable text)    │    │
│                           │  └──────────────────────┘    │
├───────────────────────────┴──────────────────────────────┤
│ Ready: 0 │ KEEP: 5 │ INVESTIGATE: 2 │ REPLACE: 3 │ Err:0│
└──────────────────────────────────────────────────────────┘
```

### Color Coding
- **Authenticity column**: TRUE LOSSLESS=green (#2EA043), SUSPICIOUS=amber (#D29922), FAKE/FAKE HI-RES=red (#CF222E)
- **Decision column**: KEEP=green, INVESTIGATE=amber, REPLACE=red
- **Quality column**: 7-10=green, 4-6=amber, 1-3=red
- **Phase column**: negative values = red warning text
- **DC Offset column**: non-zero values = red warning text

### Summary Bar
Replaces old "Ready / Fake / Good MP3 / Errors" with:
`Ready: N | KEEP: N | INVESTIGATE: N | REPLACE: N | Errors: N`

### Detail Panel
Spectrogram at top (existing heatmap + cutoff line, unchanged logic), 5-section structured report below in a `TextBlock` with monospace font.

---

## 7. Testing Strategy

### Framework
xUnit (.NET 10), new `LosslessChecker.Tests` project referencing the main project.

### Structure
```
LosslessChecker.Tests/
├── Analyzers/
│   ├── CutoffDetectorTests.cs
│   ├── ArtifactDetectorTests.cs
│   ├── TruePeakDetectorTests.cs
│   ├── LufsMeterTests.cs
│   ├── DrMeterTests.cs
│   ├── DcOffsetDetectorTests.cs
│   ├── PhaseAnalyzerTests.cs
│   ├── BitDepthValidatorTests.cs
│   └── UpscaleDetectorTests.cs
├── Classification/
│   ├── AuthenticityClassifierTests.cs
│   └── QualityScorerTests.cs
├── Integration/
│   └── AudioAnalyzerPipelineTests.cs
└── Helpers/
    └── TestSignalGenerator.cs
```

### Key Test Cases

| Test | Input | Expected |
|------|-------|----------|
| `Cutoff_16kHz_MP3_128` | Sine sweep 0→16kHz @ 44.1kHz | cutoff≈16kHz, EncoderMatch="MP3 128" |
| `Cutoff_18kHz_MP3_256` | Sine sweep 0→18kHz @ 44.1kHz | cutoff≈18kHz, EncoderMatch="MP3 256" |
| `Cutoff_FullSpectrum_True` | Sine sweep 0→22kHz @ 44.1kHz | cutoff>21.5kHz, TRUE LOSSLESS |
| `TruePeak_Clipped_Sine` | 1kHz sine at 0 dBFS (clipped) | TruePeak > +0.5 dBTP, HasIsp=true |
| `TruePeak_Clean_Sine` | 1kHz sine at -1 dBFS | TruePeak ≤ -0.9 dBTP, HasIsp=false |
| `DcOffset_001_Percent` | Sine + 0.001% DC | HasDcOffset=true |
| `DcOffset_Clean` | Pure sine centered at zero | HasDcOffset=false |
| `Phase_Inverted_Right` | L=sine, R=-sine | Correlation≈-1.0, IsMonoCompatible=false |
| `Phase_Identical` | L=sine, R=sine | Correlation≈+1.0, IsMonoCompatible=true |
| `BitDepth_LsbZeros_24bit` | 24-bit with zero lower 8 bits | LsbZeroPadded=true |
| `BitDepth_Valid_24bit` | 24-bit with real LSB data | LsbZeroPadded=false |
| `Lufs_Minus14` | -14 LUFS reference signal | IntegratedLUFS ≈ -14 ± 0.5 |
| `Quality_Brickwall_DR4` | DR4, clipped, LUFS -6 | QualityScore ≤ 3 |
| `Quality_Audiophile_DR14` | DR14, clean, LUFS -18 | QualityScore ≥ 9 |
| `Decision_PoorMaster_Kept` | TRUE LOSSLESS, Quality=2 | Decision="KEEP (poor master)" — NOT REPLACE |
| `Decision_Fake_Replaced` | cutoff=16kHz, artifacts | Decision="REPLACE" |
| `Authenticity_FakeHiRes` | 96kHz file, HF cutoff@22kHz | Authenticity="FAKE HI-RES" |

### Test Signal Generation
`TestSignalGenerator` uses NWaves to create WAV data in-memory — no fixture files needed:
- `GenerateSweep(startFreq, endFreq, duration, sampleRate)` — sine sweep
- `GenerateClipped(freq, duration, sampleRate, gain)` — clipped sine
- `GenerateWithDcOffset(freq, duration, sampleRate, offsetPercent)` — DC-offset signal
- `GenerateStereo(leftGain, rightGain, phaseInvert)` — phase test signals
- `GeneratePadded24Bit(samples)` — 24-bit samples with zero LSBs

---

## 8. Project Structure (after overhaul)

```
LosslessChecker/
├── LosslessChecker.slnx
├── LosslessChecker/                      (main WPF project)
│   ├── LosslessChecker.csproj
│   ├── App.xaml / App.xaml.cs
│   ├── Converters/                       (InvertBool, ScoreToColor, ScoreToIcon)
│   ├── Models/
│   │   ├── AnalysisResult.cs             (36 fields, overhauled)
│   │   ├── AnalysisStatus.cs             (unchanged)
│   │   ├── AudioFileInfo.cs              (unchanged)
│   │   ├── OriginalFormat.cs             (extended with Channels)
│   │   └── StereoBuffer.cs               (NEW)
│   ├── Services/
│   │   ├── Analysis/
│   │   │   ├── AuthenticityClassifier.cs (NEW — split from ScoreCalculator)
│   │   │   ├── QualityScorer.cs          (NEW — split from ScoreCalculator)
│   │   │   └── VerdictGenerator.cs       (rewritten for 5-section report)
│   │   ├── Analyzers/
│   │   │   ├── CutoffDetector.cs         (enhanced: encoder mapping, shelf analysis)
│   │   │   ├── ArtifactDetector.cs       (enhanced: MP3 sizzle, ArtifactType)
│   │   │   ├── TruePeakDetector.cs       (NEW — 4x oversampling)
│   │   │   ├── LufsMeter.cs              (NEW — ITU 1770-4)
│   │   │   ├── DrMeter.cs                (enhanced: per-channel DR)
│   │   │   ├── DcOffsetDetector.cs       (NEW)
│   │   │   ├── PhaseAnalyzer.cs          (NEW)
│   │   │   ├── BitDepthValidator.cs      (enhanced: LSB zero-pad check)
│   │   │   └── UpscaleDetector.cs        (enhanced: dither noise detection)
│   │   ├── AudioAnalyzer.cs              (thin orchestrator ~40 lines)
│   │   ├── AudioDecoder.cs               (NEW — stereo PCM decoding)
│   │   ├── AudioFormatReader.cs          (extended: reads Channels)
│   │   ├── FileScanner.cs                (unchanged)
│   │   └── SpectrogramBuilder.cs         (NEW — extracted from AudioAnalyzer)
│   ├── ViewModels/
│   │   ├── AudioFileViewModel.cs         (extended with new [ObservableProperty] fields)
│   │   └── MainViewModel.cs              (updated summary counts, detail panel binding)
│   └── Views/
│       ├── MainWindow.xaml               (master-detail layout, new columns, detail panel)
│       └── MainWindow.xaml.cs            (updated selection handler)
├── LosslessChecker.Tests/                (NEW)
│   ├── LosslessChecker.Tests.csproj
│   ├── Analyzers/                        (9 test files)
│   ├── Classification/                   (2 test files)
│   ├── Integration/                      (1 test file)
│   └── Helpers/TestSignalGenerator.cs
└── docs/superpowers/
    ├── specs/2026-06-23-lossless-checker-design.md     (original design)
    ├── specs/2026-06-24-lossless-checker-overhaul-design.md  (this file)
    └── plans/2026-06-23-lossless-checker-plan.md       (original plan)
```

---

## 9. Risk & Edge Cases

| Risk | Mitigation |
|------|-----------|
| Stereo decoding doubles memory (2x float arrays) | Already handling ~50MB mono; stereo ≈ 100MB per file — acceptable on modern machines |
| 4x oversampling for TruePeak adds CPU cost | Only runs on peak-finding pass, not full signal; O(n) with small constant factor |
| LUFS calculation is computationally expensive | Can be optional/skippable if needed; ITU 1770-4 is well-optimized with existing DSP libraries |
| DSD support was requested but descoped | User confirmed: MP3, FLAC, WAV, ALAC only |
| Large directories (1000+ files) — memory pressure | Current batching (Environment.ProcessorCount parallelism, progress every 10 files) is sufficient |
| Files < 5 seconds | Skip analysis (existing behavior, unchanged) |
| Mono files | PhaseAnalyzer returns Correlation=1.0, IsMonoCompatible=true (trivially) |
| Corrupt/unreadable files | Caught by AudioDecoder, set AnalysisStatus=Error with ErrorMessage |

---

## 10. Non-Goals (explicitly excluded)

- Batch CSV/HTML export (user rejected)
- DSD (.dsf/.dff) support (user rejected)
- AIFF support (user rejected)
- Plugin system / IAnalyzer interface (over-engineering rejected in favor of pipeline)
- Online database lookup / AcoustID integration
- Audio playback
- Editing/repair features
