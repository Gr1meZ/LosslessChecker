# LosslessChecker Design Spec

## Overview

WPF desktop application that scans audio libraries and determines whether each file is genuine lossless or fake (re-encoded from lossy source). Produces a "Lossless Score" (0-100%) per file based on multiple metrics. Also evaluates quality of genuine lossless files (DR, clipping).

## Architecture

```
[User selects folder]
         |
[File Scanner] — recursive scan, filter .mp3 .flac .wav .m4a
         |
[Processing Queue] — parallel processing (N threads, configurable)
         |
[Analyzer per file] — 3 stages:
   1. Decode to PCM (NAudio)
   2. FFT → cutoff frequency, spectrogram artifacts
   3. DR Meter, True Peak, clipping detection
         |
[Result Model] → all metrics
         |
[UI] — real-time table updates, progress bar, spectrogram viewer
```

### Stack
- .NET 10, WPF, MVVM (CommunityToolkit.Mvvm)
- NAudio — audio decoding (MP3, FLAC, WAV, ALAC/M4A)
- NWaves — DSP/FFT operations
- OxyPlot — spectrogram rendering

## Result Model

Per file:
| Field | Description |
|-------|-------------|
| FilePath | Full path |
| Format | Container format + encoding |
| SampleRate | Hz |
| Bitrate | kbps |
| CutoffFrequency | High-frequency cutoff detected (Hz) |
| HasArtifacts | Block-boundary artifacts detected |
| ArtifactLevel | None / Weak / Medium / Strong |
| DynamicRange | DR in dB (TT DR Meter algorithm) |
| TruePeak | dBTP |
| ClippingPercent | % samples at ceiling |
| LosslessScore | 0-100% weighted score |
| Status | Lossless / Good MP3 / Suspicious / Fake / Poor Quality |

## Lossless Score Formula

```
Score = 100
  - cutoff_penalty   (0..40)
  - artifact_penalty (0..30)
  - clipping_penalty (0..20)
  - DR_penalty       (0..10)
```

### Cutoff penalty (ratio-based, adapts to any sample rate)

| cutoff / nyquist | Penalty |
|-------------------|---------|
| >= 0.95 | 0 |
| 0.85-0.94 | -5 |
| 0.75-0.84 | -15 |
| 0.65-0.74 | -25 |
| < 0.65 | -40 |

Where `nyquist = sampleRate / 2`. Works for 44.1kHz through 384kHz+.

### Artifact penalty

| Level | Penalty |
|-------|---------|
| None | 0 |
| Weak | -10 |
| Medium | -20 |
| Strong (block structure) | -30 |

### Clipping penalty

| Clipped samples | Penalty |
|-----------------|---------|
| 0% | 0 |
| <1% | -5 |
| 1-5% | -10 |
| >5% | -20 |

### DR penalty

| DR | Penalty |
|----|---------|
| >=10 | 0 |
| 8-9 | -3 |
| 6-7 | -7 |
| <6 | -10 |

### Interpretation
- 90-100%: Lossless / Good MP3
- 60-89%: Suspicious
- <60%: Fake / Poor quality

## UI Layout

```
┌──────────────────────────────────────────────────┐
│ [Select Folder] [Stop]  ████████░░░ 67%         │ ← Toolbar
├──────────────────────────────────────────────────┤
│ Name | Format | Cutoff | DR | Score | Spectro   │ ← Table (DataGrid)
│ song1│FLAC 96│ 48kHz  │14✅│98%   │ 👁          │
│ song2│FLAC 44│ 18kHz  │6❌ │12%   │ 👁          │
│ ...  │       │        │    │      │            │
├──────────────────────────────────────────────────┤
│ Ready: 3 | Fake: 1 | Good MP3: 1 | Errors: 0   │ ← Summary bar
├──────────────────────────────────────────────────┤
│ [Spectrogram of selected file]                   │ ← Collapsible panel
│   ░░░░▓▓▓▓▓▓░░░░░                               │
│   ░░░░▓▓▓▓▓▓▓▓▓▓░  with cutoff line overlay      │
└──────────────────────────────────────────────────┘
```

- Row colors: green (>=90%), yellow (60-89%), red (<60%)
- Spectrogram panel is a collapsible bottom split
- Virtualizing DataGrid for large libraries

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Corrupted file | Error icon ⚠, skip, continue |
| Too short (<5s) | Warning, skip DR/cutoff |
| DRM protected | Skip with note |
| Naturally low-fi recording | Flagged but not penalized |

If one file fails, processing continues. All errors logged.

## File Support
- FLAC (.flac)
- MP3 (.mp3)
- WAV (.wav)
- ALAC / M4A (.m4a, .alac)

## Edge Cases
- Sample rates above 96kHz: cutoff ratio adapts dynamically
- 24-bit and 32-bit files: handled by NAudio PCM pipeline
- Multi-channel: analyzed on first channel (stereo → left)
- Long files: sampled in segments, results averaged
