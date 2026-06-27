# Task 13 Report — 14 UI/Logic Fixes

**Status**: Complete
**Build**: Succeeded (0 errors)
**Tests**: 46 passed, 0 failed, 0 skipped

## Changes Applied

### Fix 1: `AudioFileViewModel.cs:203` — String comparison bug
Changed `r.ClaimedType is "FLAC" or "ALAC" or "WAV"` (type pattern matching) to proper `==` comparisons.

### Fix 2: `AudioFileViewModel.cs:634` — Quality score description
Updated description text to remove obsolete weight details (DR weight 25, clipping 20, etc.).

### Fix 3: `AudioPipeline.cs:23-38` — ALAC detection in GetClaimedType
Replaced the simple extension-to-type mapping for `.m4a` with `Mp4CodecReader.DetectCodec()` to distinguish ALAC from AAC.

### Fix 4: `CutoffDetector.cs:318-334` — ClassifyBandwidth for ALAC
Improved lossless detection logic: now catches files without lossy artifacts regardless of shelf type (not just Natural/Filtered).

### Fix 5: `AnalysisCache.cs` — Added Clear() method
Public `Clear()` method that empties the in-memory cache and deletes the cache file.

### Fix 6: `MainWindow.xaml:223-235` — Removed filter ToggleButtons
Deleted LOSSLESS, NOT SURE, REPLACE, MP3 ToggleButtons from the search StackPanel.

### Fix 7: `MainWindow.xaml:251-252` — DataGrid hover/selection colors
Changed HighlightBrush from `#818cf8` to `#4a6cf7`, HighlightTextBrush from `#0f0f1a` to `White`.

### Fix 8: `MainWindow.xaml` DataGrid columns — Fonts, widths, Quality column
- Removed `FontSize="10"` from МБ/мин, Заявлен, По анализу columns (now inherit 12px)
- Removed `FontWeight="Bold"` from По анализу column
- Changed Название Width to `*` (stretch)
- Changed По анализу Width to `Auto` MinWidth="100"
- Added new Кач-во (QualityScorePercent) column after DR column

### Fix 9: `MainWindow.xaml:157-166` — Progress bar
Changed height to 18, foreground to LosslessGreenBrush, text to centered/bold/white.

### Fix 10: `MainWindow.xaml` toolbar — Clear Cache/Table buttons
Added "🗑 Таблица" (ClearTable) and "🧹 Кэш" (ClearCache) buttons before the Export button, shifted column numbers accordingly.

### Fix 11: `MainViewModel.cs:45-55` — Removed filter properties
Deleted `ShowKeep`, `ShowInvestigate`, `ShowReplace`, `ShowMp3` observable properties.

### Fix 12: `MainViewModel.cs:88-94` — Simplified FilterFile
Removed verdict filtering block; FilterFile now only filters by TreeView selection and SearchQuery.

### Fix 13: `MainViewModel.cs` — Removed filter handlers, added Clear commands
- Deleted `OnShowKeepChanged`, `OnShowInvestigateChanged`, `OnShowReplaceChanged`, `OnShowMp3Changed` partial methods
- Added `ClearTable()` and `ClearCache()` relay commands

### Fix 14: `Dark.xaml:121-144` — Removed FilterToggleStyle
Deleted the entire FilterToggleStyle block.

## Files Modified
1. `LosslessChecker/ViewModels/AudioFileViewModel.cs`
2. `LosslessChecker/Services/AudioPipeline.cs`
3. `LosslessChecker/Services/CutoffDetector.cs`
4. `LosslessChecker/Services/AnalysisCache.cs`
5. `LosslessChecker/Services/AudioAnalyzer.cs`
6. `LosslessChecker/Views/MainWindow.xaml`
7. `LosslessChecker/ViewModels/MainViewModel.cs`
8. `LosslessChecker/Themes/Dark.xaml`
