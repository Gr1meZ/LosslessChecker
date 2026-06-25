# UI Overhaul & Codec Detection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Redesign dark theme to minimalist monochrome, fix 3 UI bugs (crash #FF818CF8, spectrogram behind main window, progress bar text), add welcome screen button, implement AAC/ALAC codec detection in M4A container.

**Architecture:** Single-pass UI refresh across 5 XAML/CS files + 1 new service (`Mp4CodecReader`) for MP4 atom parsing. No new dependencies. Theme toggle code removed entirely — Dark.xaml becomes the only theme, loaded unconditionally.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, NAudio, NWaves

---

### Task 1: Crash fix — TreeView Resources `#FF818CF8`

**Files:**
- Modify: `LosslessChecker/Views/MainWindow.xaml:187-194`

- [ ] **Step 1: In `MainWindow.xaml`, replace TreeView.Resources brush definitions**

Replace lines 187-194:
```xml
<SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}"
                 Color="{DynamicResource AccentBrush}"/>
<SolidColorBrush x:Key="{x:Static SystemColors.HighlightTextBrushKey}"
                 Color="{DynamicResource BgPrimaryBrush}"/>
<SolidColorBrush x:Key="{x:Static SystemColors.InactiveSelectionHighlightBrushKey}"
                 Color="{DynamicResource AccentBrush}"/>
<SolidColorBrush x:Key="{x:Static SystemColors.InactiveSelectionHighlightTextBrushKey}"
                 Color="{DynamicResource BgPrimaryBrush}"/>
```

With (hardcoded colors, matching new palette):
```xml
<SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}" Color="#4a9eff"/>
<SolidColorBrush x:Key="{x:Static SystemColors.HighlightTextBrushKey}" Color="#1e1e1e"/>
<SolidColorBrush x:Key="{x:Static SystemColors.InactiveSelectionHighlightBrushKey}" Color="#4a9eff"/>
<SolidColorBrush x:Key="{x:Static SystemColors.InactiveSelectionHighlightTextBrushKey}" Color="#1e1e1e"/>
```

- [ ] **Step 2: Build and run**

```powershell
dotnet build
```
Expected: BUILD SUCCEEDED. No XamlParseException at startup.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Views/MainWindow.xaml
git commit -m "fix: replace DynamicResource AccentBrush with hardcoded Color in TreeView.Resources to fix XamlParseException"
```

---

### Task 2: New Dark.xaml — monochrome palette

**Files:**
- Modify: `LosslessChecker/Themes/Dark.xaml` (entire file)
- Modify: `LosslessChecker/App.xaml:1-12`
- Modify: `LosslessChecker/App.xaml.cs:1-19`

- [ ] **Step 1: Rewrite `Dark.xaml` with new palette**

Replace the entire content of `Dark.xaml` with flat monochrome colors.

- [ ] **Step 2: Simplify `App.xaml` — remove MergedDictionaries**

```xml
<Application x:Class="LosslessChecker.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="Views/MainWindow.xaml">
</Application>
```

- [ ] **Step 3: Simplify `App.xaml.cs`**

```csharp
using System.Windows;

namespace LosslessChecker;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new System.Uri("Themes/Dark.xaml", System.UriKind.Relative)
        });
    }
}
```

- [ ] **Step 4: Build and verify**

```powershell
dotnet build
```
Expected: BUILD SUCCEEDED.

- [ ] **Step 5: Delete `Light.xaml`**

```powershell
Remove-Item -LiteralPath "LosslessChecker\Themes\Light.xaml"
```

- [ ] **Step 6: Commit**

```bash
git add LosslessChecker/Themes/Dark.xaml LosslessChecker/Themes/Light.xaml LosslessChecker/App.xaml LosslessChecker/App.xaml.cs
git commit -m "feat: monochrome dark theme — flat colors, no gradients, remove Light.xaml"
```

---

### Task 3: Remove theme toggle from ViewModel and UI

**Files:**
- Modify: `LosslessChecker/ViewModels/MainViewModel.cs:113-166`
- Modify: `LosslessChecker/Views/MainWindow.xaml:122-156`

- [ ] **Step 1: Remove theme code from `MainViewModel.cs`**

Delete lines 113-166 (`IsDarkTheme` property, `ToggleTheme()`, `ApplyTheme()`, `SaveThemeSetting()`, `LoadThemeSetting()`).

Also remove `_isDarkTheme` initialization from constructor (line 68).

- [ ] **Step 2: Remove theme toggle button from `MainWindow.xaml` toolbar**

Remove the button at Grid.Column="5" and adjust remaining Grid.ColumnDefinitions to 6 columns (0-5). ProgressBar moves to Column="5".

- [ ] **Step 3: Build and verify**

```powershell
dotnet build
```
Expected: BUILD SUCCEEDED.

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/ViewModels/MainViewModel.cs LosslessChecker/Views/MainWindow.xaml
git commit -m "feat: remove theme toggle — dark-only, delete IsDarkTheme/ToggleTheme/ApplyTheme/SaveThemeSetting"
```

---

### Task 4: Theme-aware ScoreToColorConverter

**Files:**
- Modify: `LosslessChecker/Converters/ScoreToColorConverter.cs`

- [ ] **Step 1: Rewrite converter to use dynamic resources**

Replace hardcoded `Color.FromRgb()` with `Application.Current.TryFindResource("LosslessGreenBrush")` etc.

- [ ] **Step 2: Build and verify**

```powershell
dotnet build
```
Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Converters/ScoreToColorConverter.cs
git commit -m "feat: make ScoreToColorConverter theme-aware via TryFindResource"
```

---

### Task 5: Welcome screen — add folder button

**Files:**
- Modify: `LosslessChecker/Views/MainWindow.xaml:522-536`

- [ ] **Step 1: Replace WelcomeContent**

Replace welcome content with button `📁 Выбрать папку` bound to `SelectFolderCommand`.

- [ ] **Step 2: Build and verify**

```powershell
dotnet build
```
Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Views/MainWindow.xaml
git commit -m "feat: add folder select button to welcome screen"
```

---

### Task 6: Spectrogram window always on top

**Files:**
- Modify: `LosslessChecker/Services/DialogService.cs:8-16`

- [ ] **Step 1: Add Owner and Topmost**

```csharp
window.Owner = Application.Current.MainWindow;
window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
window.Topmost = true;
```

- [ ] **Step 2: Build and verify**

```powershell
dotnet build
```
Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/DialogService.cs
git commit -m "fix: spectrogram window — set Owner, Topmost=true, CenterOwner to prevent hiding behind main window"
```

---

### Task 7: Progress bar with text overlay

**Files:**
- Modify: `LosslessChecker/Views/MainWindow.xaml:152-155`
- Modify: `LosslessChecker/ViewModels/MainViewModel.cs:104-108`

- [ ] **Step 1: Replace ProgressBar with Grid overlay in XAML**

Wrap ProgressBar + TextBlock in a Grid at Column="5".

- [ ] **Step 2: Add ProgressText property to MainViewModel**

```csharp
public string ProgressText => IsProcessing
    ? $"{ProcessedFiles}/{TotalFiles} ({Progress:F0}%)"
    : "";
```

Add `OnPropertyChanged(nameof(ProgressText))` calls in progress update blocks.

- [ ] **Step 3: Build and verify**

```powershell
dotnet build
```
Expected: BUILD SUCCEEDED.

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/Views/MainWindow.xaml LosslessChecker/ViewModels/MainViewModel.cs
git commit -m "feat: progress bar text overlay showing 'N/M (X%)'"
```

---

### Task 8: MP4 codec reader — detect AAC vs ALAC in .m4a

**Files:**
- Create: `LosslessChecker/Services/Mp4CodecReader.cs`
- Create: `LosslessChecker.Tests/Services/Mp4CodecReaderTests.cs`

- [ ] **Step 1: Write Mp4CodecReader.cs**

Parse MP4 atoms (moov -> trak -> mdia -> minf -> stbl -> stsd) to detect codec: 'mp4a' = AAC, 'alac' = ALAC. Parse esds atom for AAC bitrate.

- [ ] **Step 2: Write minimal test**

- [ ] **Step 3: Build and verify**

```powershell
dotnet build
```

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/Services/Mp4CodecReader.cs LosslessChecker.Tests/Services/Mp4CodecReaderTests.cs
git commit -m "feat: Mp4CodecReader — parse MP4 atoms to detect AAC vs ALAC codec"
```

---

### Task 9: AudioFormatReader — read M4A format info

**Files:**
- Modify: `LosslessChecker/Services/AudioFormatReader.cs:11-17`

- [ ] **Step 1: Add M4A reading to ReadOriginal()**

Add `.m4a` or `.alac` => `ReadMp4(filePath)` case. Add `ReadMp4()` method.

- [ ] **Step 2: Build and verify**

```powershell
dotnet build
```

- [ ] **Step 3: Commit**

```bash
git add LosslessChecker/Services/AudioFormatReader.cs
git commit -m "feat: AudioFormatReader — read M4A format info via Mp4CodecReader"
```

---

### Task 10: AnalysisResult — add AAC fields

**Files:**
- Modify: `LosslessChecker/Models/AnalysisResult.cs`
- Modify: `LosslessChecker/ViewModels/AudioFileViewModel.cs`

- [ ] **Step 1: Add fields to AnalysisResult record**

Add `AacBitrate` and `IsAac` properties.

- [ ] **Step 2: Update AudioFileViewModel VerdictLabel**

Add AAC case: show "AAC 256" format for verdict.

- [ ] **Step 3: Build and verify**

```powershell
dotnet build
```

- [ ] **Step 4: Commit**

```bash
git add LosslessChecker/Models/AnalysisResult.cs LosslessChecker/ViewModels/AudioFileViewModel.cs
git commit -m "feat: add AacBitrate/IsAac to AnalysisResult and AAC verdict label"
```

---

### Task 11: AudioPipeline — AAC/ALAC/MP3 split logic

**Files:**
- Modify: `LosslessChecker/Services/AudioPipeline.cs`

- [ ] **Step 1: Add AAC codec detection alongside MP3 detection**

- [ ] **Step 2: Add AAC classification in authenticity block**

`Authenticity = "LOSSY (AAC)"`

- [ ] **Step 3: Add AAC quality scoring with ComputeAacQuality()**

- [ ] **Step 4: Update quality blending for AAC**

- [ ] **Step 5: Add bitrate to result**

- [ ] **Step 6: Update format label for ALAC**

- [ ] **Step 7: Build and verify**

```powershell
dotnet build
```

- [ ] **Step 8: Commit**

```bash
git add LosslessChecker/Services/AudioPipeline.cs
git commit -m "feat: AAC/ALAC detection — split lossy AAC from lossless ALAC in M4A, add ComputeAacQuality"
```

---

### Task 12: Final build and test

**Files:** None (verification only)

- [ ] **Step 1: Full build**

```powershell
dotnet build
```
Expected: BUILD SUCCEEDED for both projects.

- [ ] **Step 2: Run all tests**

```powershell
dotnet test
```
Expected: ALL TESTS PASS.

- [ ] **Step 3: Commit (if any cleanup needed)**
