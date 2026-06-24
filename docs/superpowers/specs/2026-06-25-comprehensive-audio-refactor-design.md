# Comprehensive Audio Refactor Design

## Overview

Four-phase refactor addressing 20+ critical issues across audio algorithms, spectrogram rendering, memory architecture, and UI/UX.

---

## Phase 1: Audio Algorithms

### 1.1 Stereo Decoder Fix (`AudioDecoder.cs`)

**Problem:** Decoder mixes stereo to mono, returning `StereoBuffer(mono, empty, sr)`. All stereo analyzers receive mono data.

**Solution:** Decode to separate L/R float arrays. For stereo files, read interleaved samples and de-interleave into pre-allocated arrays.

```csharp
public static StereoBuffer Decode(string filePath, CancellationToken ct = default)
{
    using var reader = CreateReader(filePath) ?? throw ...;
    var provider = reader.ToSampleProvider();
    var format = provider.WaveFormat;
    
    if (format.Channels > 2) throw new NotSupportedException(...);
    
    // Pre-allocate based on estimated total samples
    long totalSamples = (long)(reader.TotalTime.TotalSeconds * format.SampleRate);
    int capacity = (int)Math.Min(totalSamples * format.Channels + 4096, int.MaxValue);
    
    var interleaved = new List<float>(capacity);
    var readBuffer = new float[16384];
    int read;
    while ((read = provider.Read(readBuffer, 0, readBuffer.Length)) > 0)
    {
        ct.ThrowIfCancellationRequested();
        interleaved.AddRange(readBuffer[..read]);
    }
    
    if (format.Channels == 1)
        return new StereoBuffer(interleaved.ToArray(), Array.Empty<float>(), format.SampleRate);
    
    // De-interleave stereo
    int n = interleaved.Count / 2;
    var left = new float[n];
    var right = new float[n];
    for (int i = 0; i < n; i++)
    {
        left[i] = interleaved[i * 2];
        right[i] = interleaved[i * 2 + 1];
    }
    return new StereoBuffer(left, right, format.SampleRate);
}
```

**Files:** `Services/AudioDecoder.cs` (rewrite), `Models/StereoBuffer.cs` (unchanged)

### 1.2 DrMeter Stereo Fix (`DrMeter.cs`)

**Problem:** `AddChunk` only has left-channel accumulators. Right channel data leaks into left.

**Solution:** Add `AddChunk(ReadOnlySpan<float> left, ReadOnlySpan<float> right)` overload. Both channels processed independently. Fix clipPct formula to use total sample count.

**Key changes:**
- `AddChunk(left, right)` — parallel accumulation into L/R accumulators
- `AnalyzeStereo` — single pass through both channels
- clipPct: `_clippedRuns / (totalSamples / ClipRunMin) * 100`
- Remove TrimPct (use full top 20% per TT DR spec)

### 1.3 LUFS K-weighting Fix (`LufsMeter.cs`)

**Problem:** 1st-order filters instead of ITU-R BS.1770-4 biquads. No overlap. Mono mixing loses channel weighting.

**Solution:** Implement proper 2nd-order high-pass (stage 1) + 4th-order shelving (stage 2) biquad filters per ITU-R BS.1770-4. Process L and R channels independently with channel weighting. Use 400ms blocks with 75% overlap (100ms hop).

**K-weighting biquad coefficients** (from ITU-R BS.1770-4 Table 1, computed per sample rate):

```
Stage 1 (High-pass, f=38.13547 Hz, Q=0.5003):
  b0, b1, b2, a1, a2 (sample-rate dependent)

Stage 2 (Shelving, f=1500 Hz, Q=0.49099, V=4):
  b0, b1, b2, a1, a2 (sample-rate dependent)
```

Precompute coefficients in a `KWeightingFilter` class. Apply per-channel, per-block.

**LRA:** 3-second sliding window with 66% overlap, minimum statistics per EBU R128.

### 1.4 True Peak 4x Oversampling (`TruePeakDetector.cs`)

**Problem:** Linear interpolation between samples. ITU-R BS.1770-4 requires 4x oversampling with anti-imaging FIR.

**Solution:** Implement 4x oversampling using a polyphase FIR anti-imaging filter (21-tap, per ITU-R BS.1770-4 Annex). Process L and R independently.

**FIR coefficients** (ITU-R BS.1770-4 Table 2):
```
[-0.000003, -0.000018, -0.000026, 0.000066, 0.000222, 0.000119,
 -0.000489, -0.000738, 0.000889, 0.002327, 0.001263, -0.003958,
 -0.006047, 0.006114, 0.014570, 0.006536, -0.020278, -0.035127,
 0.035480, 0.108330, 0.108330, 0.035480, -0.035127, -0.020278,
 0.006536, 0.014570, 0.006114, -0.006047, -0.003958, 0.001263,
 0.002327, 0.000889, -0.000738, -0.000489, 0.000119, 0.000222,
 0.000066, -0.000026, -0.000018, -0.000003]
```

Remove the artificial +3 dB cap. Fix clip-run counting to not reset on each detection (use sliding window instead).

### 1.5 CutoffDetector Improvements (`CutoffDetector.cs`)

- Average power spectrum (magnitude squared) instead of magnitude — reduces peak frame bias
- Remove frequency weight heuristic, use raw slope threshold
- Add sub-bin interpolation for cutoff frequency precision

### 1.6 Hash Verification Interfaces (NEW)

New interfaces for future AccurateRip/CUETools integration:

```csharp
namespace LosslessChecker.Services.Verification;

public interface IAudioHasher
{
    AudioHashResult ComputeHash(string filePath, CancellationToken ct = default);
}

public interface IHashDatabase
{
    Task<HashVerificationResult> VerifyAsync(AudioHashResult hash, CancellationToken ct = default);
}

public record AudioHashResult(
    string Algorithm,        // "AccurateRipV1", "AccurateRipV2", "CUETools"
    string TrackHash,        // hex hash
    int TrackNumber,
    int DiscId);

public record HashVerificationResult(
    bool IsVerified,
    int Confidence,          // number of matching submissions
    string DatabaseName);
```

**New files:**
- `Services/Verification/IAudioHasher.cs`
- `Services/Verification/IHashDatabase.cs`
- `Services/Verification/AccurateRipHasher.cs` (stub implementation)
- `Services/Verification/PcmHasher.cs` (existing MD5 from ContainerAnalyzer, refactored)

**ContainerAnalyzer.cs** — refactor `ComputePcmMd5` into `PcmHasher` implementing `IAudioHasher`. Add `IsCdAligned` + `PcmMd5` to `AnalysisResult` for future verification.

---

## Phase 2: Spectrogram Optimization

### 2.1 Single-Pass FFT with Float Output (`SpectrogramBuilder.cs`)

**Problem:** Double FFT pass, 50% overlap, no bin interpolation, byte quantization.

**Solution:** Complete rewrite:

- **Single pass:** Find globalPeak while building frames, then normalize in post-processing
- **75% overlap:** HopSize = FftSize/4 = 1024
- **Bin interpolation:** Linear interpolation between adjacent FFT bins for log-frequency mapping
- **Float output:** Store dB values as `float[]` (not `byte[]`) — enables dynamic range adjustment and colormap switching without re-FFT
- **ArrayPool:** Rent scratch buffers (`_frame`, `_real`, `_imag`) from `ArrayPool<float>.Shared`

**Output format:** `SpectrogramData` record containing `float[] dbValues`, `width`, `height`, `sampleRate`, `duration`.

```csharp
public record SpectrogramData(float[] DbValues, int Width, int Height, int SampleRate, double Duration);
```

### 2.2 SkiaSharp Renderer (`SpectrogramRenderer.cs`)

**Problem:** LOH allocations per render, new WriteableBitmap each time, double initialization.

**Solution:** Rewrite using `WriteableBitmap` with `ArrayPool`-backed pixel buffer:

- **Cached WriteableBitmap:** Create once, update via `WritePixels` on subsequent renders
- **ArrayPool pixel buffer:** `ArrayPool<byte>.Shared.Rent(width * height * 4)`
- **Colormap LUT:** Pre-compute 256-entry `uint[]` lookup table for hot colormap — eliminates per-pixel computation
- **Bulk copy:** Apply LUT in a tight loop, write to WriteableBitmap backing buffer directly

**Alternative considered:** SkiaSharp (SKImage + SKBitmap). Rejected because:
1. Adds external NuGet dependency (SkiaSharp.Views.Desktop)
2. WriteableBitmap with ArrayPool achieves equivalent performance for this use case
3. SkiaSharp's advantage (GPU acceleration) is only realized with SKGLView, which requires additional plumbing

### 2.3 SpectrogramWindow Fixes (`SpectrogramWindow.xaml.cs`)

- **Debounce resize:** Use `DispatcherTimer` (150ms) to defer `RenderFull` during continuous resize
- **Fix panning:** Accumulate `TranslateTransform` offset properly using a `TransformGroup` with persistent translate
- **Cache brushes:** Static `SolidColorBrush` instances for axes/cutoff lines

### 2.4 AnalysisResult Changes

Replace `byte[] SpectrogramFlat` with `float[] SpectrogramDb`. Update `AudioPipeline`, `AudioFileViewModel`, `SpectrogramWindow`.

---

## Phase 3: Memory & Architecture

### 3.1 Decoder Memory (`AudioDecoder.cs`)

- Replace `List<float>` + `ToArray()` with direct `float[]` allocation
- For known-length formats (WAV, FLAC with totalSamples in header): pre-allocate exact size
- For streaming formats (MP3): use `MemoryStream<float>`-equivalent — grow array in chunks, return final trimmed array

### 3.2 Spectrogram Leak Fix (`AudioFileViewModel.cs`)

- After `GetOrBuildSpectrogram()`, null out `_lastResult.SpectrogramFlat` by creating a copy without it
- Or: change `AnalysisResult` to allow nullable spectrogram data that can be cleared after use

**Approach:** Make `SpectrogramFlat` (now `SpectrogramDb`) a mutable `float[]?` field (not init-only) on a class version of AnalysisResult, or use a wrapper that allows clearing.

**Decision:** Add `ClearSpectrogramData()` method to `AnalysisResult` that nulls the spectrogram array. Call after building bitmap.

### 3.3 CollectionView Filtering (`MainViewModel.cs`)

- Replace `FilteredFiles = new ObservableCollection(...)` with `ICollectionView` via `CollectionViewSource.GetDefaultView(Files)`
- Apply filter predicate instead of rebuilding collection
- Sort via `SortDescription` on the view

### 3.4 Pipeline Parallelization (`AudioPipeline.cs`)

- After decoding, run independent analyzers in parallel using `Task.WhenAll`:
  - Group A: Cutoff + Spectrogram (share FFT) — sequential within group
  - Group B: TruePeak, LUFS, DR, DC, Phase, BitDepth — parallel
- Cancellation token checked between groups
- FFT computed once, shared between Cutoff and Spectrogram via a shared spectrum cache

### 3.5 Disposable Pipeline

- `AudioPipeline` implements `IDisposable`
- All `IDisposable` analyzers disposed properly
- `ArrayPool` buffers returned in `finally` blocks

---

## Phase 4: Premium UI/UX + MVVM

### 4.1 Theme Overhaul (`Themes/Dark.xaml`)

- Add elevation shadow styles: `DropShadowEffect` with blur radius per elevation level (1-5)
- Add acrylic/glassmorphism brushes: semi-transparent backgrounds with blur (simulated via opacity gradients in WPF)
- Add accent gradient brushes for premium buttons
- Add smooth color transition `Storyboard` resources
- Remove all hardcoded hex colors from XAML — centralize in theme

### 4.2 MVVM Cleanup (`MainWindow.xaml.cs`)

Move all code-behind logic to ViewModel:
- **Spectrogram display:** `SelectedFile.SpectrogramBitmap` bound to `Image.Source` in XAML
- **Spectrogram window:** `OpenSpectrogramCommand` in `MainViewModel` using `IDialogService` interface
- **Drag-drop:** `EventToCommand` behavior (from `Microsoft.Xaml.Behaviors.Wpf`)
- **Open folder:** `OpenFolderCommand` in `MainViewModel`
- **Context menu:** `ICommand` bindings instead of click handlers

**New files:**
- `Services/IDialogService.cs` — abstraction for window dialogs
- `Services/DialogService.cs` — implementation

**New NuGet:** `Microsoft.Xaml.Behaviors.Wpf` for `EventToCommand`

### 4.3 SpectrogramWindow ViewModel

New `SpectrogramViewModel`:
- `WriteableBitmap SpectrogramImage` — bound property
- `RelayCommand CopyPngCommand`
- `RelayCommand ResetZoomCommand`
- Zoom/pan logic as `ObservableProperty` transforms applied via XAML `RenderTransform` binding
- Axes drawn via `DrawingVisual` or `Canvas` with bound `ObservableCollection<AxisLabel>`

### 4.4 Premium XAML Styling

- **Panel style:** Rounded corners (8px) + subtle drop shadow + glassmorphism background
- **Button styles:** Hover scale transform (1.02x), accent gradient, smooth color transition (200ms)
- **DataGrid:** Alternating row gradient, hover highlight, rounded selection
- **Progress bar:** Animated gradient shimmer during processing
- **Detail panel:** Card-based layout with shadow elevation
- **Verdict badge:** Gradient background with glow effect
- **Transitions:** `FadeInThemeAnimation` on panel visibility changes

### 4.5 Performance Optimizations

- `VirtualizingStackPanel.VirtualizationMode="Recycling"` on DataGrid
- `EnableColumnVirtualization="True"` on DataGrid
- `ScrollUnit="Pixel"` for smooth scrolling
- `ItemsControl` for metrics: use `UIElementCollection` recycling via `VirtualizingPanel`

---

## Implementation Order

1. **Phase 1** (audio algorithms) — foundation, no UI changes
2. **Phase 2** (spectrograms) — depends on Phase 1 float[] changes
3. **Phase 3** (memory/architecture) — independent of UI
4. **Phase 4** (UI/UX) — final layer, depends on all prior phases

Each phase is independently testable and committable.

## Testing

- Run existing test suite after each phase: `dotnet test`
- Existing tests in `LosslessChecker.Tests/` cover DrMeter, LUFS, TruePeak, Cutoff, Phase, BitDepth, Artifact, Upscale detectors
- Update tests where algorithm changes affect expected values (LUFS K-weighting, True Peak 4x)
- Manual: analyze a known FLAC + MP3 file pair to verify spectrogram and metrics
