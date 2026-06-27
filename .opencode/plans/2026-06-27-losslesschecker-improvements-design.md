# LosslessChecker — Comprehensive Improvements Design (Final)

## Overview

Major rework of LosslessChecker's analysis pipeline, UI, and scoring system. The goal is to make file type detection honest, scoring type-aware, and UI readable/consistent.

---

## 1. Table Columns

### Current
| Icon | Name | Duration | Format | Bitrate | Actual | Cutoff | DR | Authenticity | Quality | Decision |

### New
| Icon | Name | Duration | Format | Bandwidth | MB/min | DR | Claimed Type | Detected Type | Decision |

- **Bandwidth**: derived from cutoff frequency — shows what the spectrum supports (e.g., `16kHz`, `20kHz`, `Full Range`, `Hi-Res`)
- **MB/min**: `fileSize / duration` in MB per minute — industry standard for identifying format:
  - `<5 MB/min` → Lossy
  - `5-12 MB/min` → CD Lossless (16-bit)
  - `12-25 MB/min` → Hi-Res (24-bit/48k-96k)
  - `>25 MB/min` → Ultra Hi-Res
- **DR**: informational only, no impact on score
- **Cutoff**: removed from table (stays in metric detail panel)
- **Bitrate (fact)**: removed (replaced by MB/min — more meaningful)
- **Claimed Type**: from file extension + sample rate
- **Detected Type**: from analysis
- **Decision**: KEEP / INVESTIGATE / REPLACE (unchanged)

---

## 2. Type System: Claimed Type + Detected Type

### Claimed Type (from file properties)
- `MP3` / `AAC` / `FLAC` / `ALAC` / `WAV`
- Sample rate ≥ 88200 → `HI-RES` prefix

### Detected Type (from analysis — determined by artifacts + cutoff)
| Label | Condition |
|---|---|
| `LOSSLESS (CD)` | No lossy artifacts, 44.1kHz, CD-aligned |
| `LOSSLESS (WEB)` | No lossy artifacts, not CD-aligned |
| `LOSSLESS (Mastered LPF)` | No lossy artifacts, cutoff <95% Nyquist (intentional mastering filter) |
| `LOSSLESS 24bit` | No lossy artifacts, >16 bit, LSB genuine |
| `HI-RES 96k` | ≥88.2kHz, HF content above 22kHz present |
| `HI-RES 192k` | ≥176.4kHz, HF content present |
| `MP3 320` | Brickwall cutoff ~20-20.5kHz + spectral holes |
| `MP3 256` | Brickwall cutoff ~19.5-20kHz + spectral holes |
| `MP3 192` | Brickwall cutoff ~18-19kHz + spectral holes |
| `MP3 128` | Brickwall cutoff ~16kHz + spectral holes |
| `AAC 256` | M4A container, soft cutoff ~20kHz+ |
| `AAC 128` | M4A container, soft cutoff ~16kHz |
| `UPSCALE (CD→HI-RES)` | Hi-Res sample rate, no HF content above 22kHz |
| `UPSCALE (MP3→FLAC)` | FLAC, brickwall cutoff <20kHz, spectral holes present |
| `FAKE 24bit` | 24-bit, LSB zero-padded (proven 24-bit stretch) |
| `UNCERTAIN` | Borderline case |

**Authenticity is determined by artifacts, not cutoff frequency.** A file with natural/LPF cutoff at 16kHz but zero compression artifacts → genuine lossless, not a transcode.

**Matching**: Claimed == Detected → green highlight. Mismatch → red.

---

## 3. Scoring per Type

### Key principle: **DR has zero impact on score.** It is informational only.

### MP3 / AAC
- Show only MP3-specific metrics: cutoff vs bitrate match, artifacts, spectral holes
- Hide: "Authenticity TRUE/FALSE", "Hi-Res authenticity", "Bit depth"
- Score: 0-100% encoding quality (existing `Mp3QualityScore`)
- Cutoff-bitrate discrepancy: −30 for 320→128 mismatch, −15 for 256→128

### Lossless (FLAC / WAV / ALAC)
- All current metrics except DR
- Cutoff not penalized unless brickwall + artifacts present together
- Only lossless files get TRUE/FALSE/UNCERTAIN authenticity classification

### Hi-Res (≥88.2kHz)
- HF content check: MaxHfDb ≥ −30 dB → genuine
- HF content < −50 dB → upscale
- Cutoff: any >22kHz OK, otherwise penalize as upscale
- Show "Hi-Res authenticity" metric only for these

---

## 4. DR — Fully Informational

DR removed from `QualityScorer.Score()`. No penalties applied.

### DR metric in detail panel (AudioFileViewModel BuildMetricItems)
Shown with color indicator + genre-relevant tooltip on hover:

```
DR{X} — {status}

Typical DR by genre:
  DR3-4:   EDM, extreme metal, hyperpop
  DR5-7:   Modern metal, alt-rock, post-grunge, pop
  DR8-11:  Rock 80s/90s, indie, symphonic metal
  DR12+:   Jazz, classical, acoustic, vinyl
```

---

## 5. Spectrogram Improvements

### SpectrogramWindow
- Add frequency grid lines at standard markers: `1k, 5k, 10k, 16k, 20k, 22.05k`
- Keep panning support but change trigger: wheel = zoom, middle-click drag or Shift+LMB = pan
- Move frequency labels to left of image (separate Grid column) to avoid overlap
- Higher contrast colors for labels

---

## 6. UI Refactoring

### Color fixes
| Element | Current (broken) | Fixed |
|---------|-----------------|-------|
| ToggleButton filters | `#1a3022` / unnatural backgrounds | Inactive: `#2d2d2d`. Active: green/yellow/red bg, white text |
| Verdict bar (MainWindow.xaml:456-458) | Foreground == Background (invisible) | Text: `#0f0f1a` on colored background |
| Progress bar | Text on bar, unreadable | Height 16px, text below bar, contrasting |
| ScoreToColorConverter | Duplicate threshold logic | Single: ≥70 green, 40-69 amber, <40 red |

### Drag & Drop
- Already supported (`Window_DragOver`, `Window_Drop`) — verify it handles both files and folders

---

## 7. Album Tree — Worst Track % Instead of Average

In `AlbumGroup` and `PopulateArtistGroups`:
- Add `WorstTrackScore`, `WorstTrackDecision` fields
- TreeView node: `AlbumName [72% — 1 Fake Track]`
- Color determined by worst track, not average

---

## 8. Critical Bugs

### LufsMeter KWeightingFilter reset
`LufsMeter.cs:39-40` — `kwL.Reset(); kwR.Reset();` inside loop. **Remove both.** Filter runs once per file.

### TotalFiles in ScanAndAppend
`MainViewModel.cs:521` — counter doesn't update correctly.

### ScoreToColorConverter cleanup
Remove duplicate threshold ranges. Use single system.

---

## 9. Concurrency

```csharp
int concurrency = Math.Max(1, Environment.ProcessorCount / 2);
```

Plus: `SemaphoreSlim` to limit files in memory (max 4 frames loaded simultaneously), preventing OOM on large batches.

---

## 10. Data Model Changes

### AnalysisResult additions
- `ClaimedType` (string) — derived from extension + sample rate
- `DetectedType` (string) — from analysis pipeline
- `Bandwidth` (string) — human-readable bandwidth label

### AudioFileViewModel additions
- `ClaimedType`, `DetectedType` properties
- `Bandwidth` — column display
- `SizePerMinute` (double) — MB/min
- `SizePerMinuteColor` — color for MB/min column
- `DetectedTypeColor` — green if matches Claimed, red if not

### GroupModels additions
- `AlbumGroup.WorstTrackScore` (double)
- `AlbumGroup.WorstTrackDecision` (string)

---

## 11. Caching

Store analyzed results in `cache.json` alongside `settings.json`.

**Key**: `MD5(filePath + fileSize + lastModified)`
**Value**: serialized `AnalysisResult`

On folder re-open, skip files with matching cache keys. On new file or modified file, re-analyze and update cache.

---

## Implementation Order

### Phase 1 — Foundation & Bugs
1. Data model changes (AnalysisResult, AudioFileViewModel, GroupModels)
2. Critical bug: LufsMeter KWeightingFilter reset
3. Critical bug: TotalFiles counter
4. Concurrency: bump to Processors/2 + SemaphoreSlim memory limit

### Phase 2 — Scoring Engine Rework
5. Bandwidth mapping + DetectedType logic
6. Remove DR from scoring (ScoringProfile, QualityScorer)
7. Scoring per type (MP3/Lossless/Hi-Res split)
8. Detected Type vs Claimed Type comparison + coloring

### Phase 3 — UI Changes
9. Table columns reshuffle (add Bandwidth, MB/min, remove Cutoff/Bitrate)
10. Album tree: worst track instead of average
11. Color refactoring (filters, verdict bar, progress bar, converters)
12. DR informational tooltips with genre reference

### Phase 4 — Spectrogram
13. Frequency grid lines + standard markers
14. Pan/zoom mode toggle
15. Label positioning fix

### Phase 5 — Caching & Polish
16. JSON cache implementation
17. Drag & drop verification
18. Unified file picker (optional enhancement)
