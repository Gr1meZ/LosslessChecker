# Task J5 Report: AudioFileViewModel cache integration

**Status**: Complete
**Build**: Passed (0 errors, pre-existing warnings only)
**Tests**: 46 passed, 0 failed

## Changes made to `LosslessChecker/ViewModels/AudioFileViewModel.cs`:

1. Removed `_rawSpectro` float[] field. Replaced with `_rawSpectroKey` string and static `SpectrogramCache`/`CoverCache` instances.

2. Updated `ApplyResult`:
   - Spectrogram: stores into `_spectroCache` with key `{FilePath}|{Width}|{Height}` instead of keeping raw float[] in memory.
   - Cover: stores into `_coverCache` with key `cover_{FilePath}` using `DecodePixelWidth=150`.

3. Updated `GetOrBuildSpectrogram`: retrieves from `_spectroCache` by key, falling back to null if not cached or key is null.

4. Updated `ClearSpectrogramData`: nulls `_rawSpectroKey` instead of `_rawSpectro`.

5. Updated `RawSpectrogram` property: retrieves from `_spectroCache` by key (keeps API compatibility with `MainViewModel.OpenSpectrogram`).
