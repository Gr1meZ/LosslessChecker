# LosslessChecker Improvements — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Rewrite LosslessChecker's table, scoring, type detection, and UI per the approved design spec.

**Architecture:** All changes are within the existing WPF/MVVM pattern. Data model (AnalysisResult, AudioFileViewModel) is extended first. Scoring is split per-file-type and DR removed from evaluation. UI columns and colors are refactored. Spectrogram gets grid lines and fixed pan/zoom.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, NAudio, NWaves, xUnit.

---

## File Structure Map

| Area | Files |
|------|-------|
| **Data Model** | `Models/AnalysisResult.cs`, `Models/GroupModels.cs`, `Models/AudioFileInfo.cs`, `Services/TagReader.cs` |
| **Critical Bugs** | `Services/Analyzers/LufsMeter.cs`, `ViewModels/MainViewModel.cs` |
| **Scoring** | `Services/Analysis/LosslessScorer.cs`, `Services/Analysis/QualityScorer.cs`, `Services/Analysis/ScoringProfile.cs`, `Services/AudioPipeline.cs`, `Services/CutoffDetector.cs` |
| **ViewModel** | `ViewModels/AudioFileViewModel.cs`, `ViewModels/MainViewModel.cs` |
| **UI** | `Views/MainWindow.xaml`, `Views/SpectrogramWindow.xaml`, `Views/SpectrogramWindow.xaml.cs`, `Converters/ScoreToColorConverter.cs`, `Converters/DecisionToColorConverter.cs`, `Converters/AuthenticityToColorConverter.cs` |
| **Caching** | `Services/AnalysisCache.cs` (new), `ViewModels/MainViewModel.cs` |

---

### Phase 1: Foundation & Bugs

### Task 1: Data Model Extensions

**Files:**
- Modify: `Models/AnalysisResult.cs`
- Modify: `Models/GroupModels.cs`
- Modify: `Services/TagReader.cs`

**Interfaces:**
- Produces: `AnalysisResult` gains `ClaimedType`, `DetectedType`, `Bandwidth` (all string). `AlbumGroup` gains `WorstTrackScore` (double), `WorstTrackDecision` (string).

- [ ] **Step 1: Add fields to AnalysisResult**

Add to `Models/AnalysisResult.cs` after line 91 (`ActualBitrate`):
```csharp
public string ClaimedType { get; init; } = "";
public string DetectedType { get; init; } = "";
public string Bandwidth { get; init; } = "";
```

- [ ] **Step 2: Add fields to AlbumGroup**

Add to `Models/GroupModels.cs`:
```csharp
public double WorstTrackScore { get; set; }
public string WorstTrackDecision { get; set; } = "";
```

- [ ] **Step 3: Add Year to TagReader**

In `Services/TagReader.cs`, add to `AudioTags` record:
```csharp
uint Year
```

Set it in `Read()` after line 21:
```csharp
uint year = tag.Year > 0 ? tag.Year : 0;
```

Add to return: `Year = year`.

- [ ] **Step 4: Add Year to AudioFileInfo**

In `Models/AudioFileInfo.cs`, add:
```csharp
uint Year
```

Update constructor/record to include year.

- [ ] **Step 5: Commit**

```bash
git add Models/AnalysisResult.cs Models/GroupModels.cs Services/TagReader.cs Models/AudioFileInfo.cs
git commit -m "feat: add ClaimedType, DetectedType, Bandwidth, Year, WorstTrackScore to data model"
```

---

### Task 2: Fix LufsMeter IIR Filter Reset (Critical Bug)

**Files:**
- Modify: `Services/Analyzers/LufsMeter.cs:39-40`

**Interfaces:**
- Consumes: `StereoBuffer` (unchanged)
- Produces: correct LUFS values per BS.1770-4

- [ ] **Step 1: Remove Reset() calls from loop**

In `Services/Analyzers/LufsMeter.cs`, lines 39-40:
```csharp
kwL.Reset();
kwR.Reset();
```
Delete both lines. The filter state must persist across blocks. The `KWeightingFilter` objects are created once per `Analyze()` call and should not be reset mid-signal.

- [ ] **Step 2: Commit**

```bash
git add Services/Analyzers/LufsMeter.cs
git commit -m "fix: remove KWeightingFilter reset in LUFS loop — IIR state must persist per BS.1770-4"
```

---

### Task 3: Fix TotalFiles Counter + Bump Concurrency + Memory Guard

**Files:**
- Modify: `ViewModels/MainViewModel.cs`

- [ ] **Step 1: Fix TotalFiles in ScanAndAppend**

In `MainViewModel.cs`, the `ScanAndAppend` method at line 521:
```csharp
TotalFiles = startTotal + newProcessed;
```
Change to:
```csharp
TotalFiles = startTotal + newTotal;
```
So that `TotalFiles` immediately reflects the total after appending, not increment progressively.

- [ ] **Step 2: Bump concurrency**

Line 390 (in `ScanAndAnalyze`) and line 502 (in `ScanAndAppend`):
```csharp
int concurrency = Math.Min(2, Environment.ProcessorCount);
```
Change both to:
```csharp
int concurrency = Math.Max(1, Environment.ProcessorCount / 2);
```

- [ ] **Step 3: Add SemaphoreSlim for memory**

Before the task-dispatching loop in both `ScanAndAnalyze` and `ScanAndAppend`, add:
```csharp
using var memoryGate = new SemaphoreSlim(4, 4);
```

Wrap the `_analyzer.Analyze(...)` call inside:
```csharp
await memoryGate.WaitAsync(ct);
try
{
    var result = await Task.Run(() => _analyzer.Analyze(fileInfo, ct), ct);
    vm.ApplyResult(result);
}
finally
{
    memoryGate.Release();
}
```

- [ ] **Step 4: Commit**

```bash
git add ViewModels/MainViewModel.cs
git commit -m "fix: TotalFiles counter in append, bump concurrency to Processors/2, add SemaphoreSlim memory guard"
```

---

### Phase 2: Scoring Engine Rework

### Task 4: Bandwidth Mapping + Detected Type Logic

**Files:**
- Modify: `Services/CutoffDetector.cs` — add new public method
- Modify: `Services/AudioPipeline.cs` — compute Bandwidth + DetectedType after analysis

**Interfaces:**
- Produces: `CutoffDetector.ClassifyBandwidth(cutoffHz, shelfType, sampleRate)` → `(string bandwidth, string detectedType)`

- [ ] **Step 1: Add bandwidth/detected-type helper to CutoffDetector**

Add to `Services/CutoffDetector.cs` a new public static method:

```csharp
public static (string bandwidth, string detectedType) ClassifyBandwidth(
    double cutoffHz, string shelfType, int sampleRate, bool hasArtifacts,
    string artifactLevel, bool hasSpectralHoles, double maxHfDb,
    bool lsbZeroPadded, int effectiveBitDepth, int bitDepth,
    bool isCdAligned, bool isMqa, bool isHdcd,
    bool hasHardClipping, string encoderMatch)
{
    bool isHiRes = sampleRate >= 88200;
    var nyquist = sampleRate / 2.0;

    // 1. Detect transcode / upscale first
    // 1a. Fake 24-bit
    if (lsbZeroPadded && bitDepth == 24)
        return ("Fake 24-bit", "FAKE 24bit");

    // 1b. Upscale: Hi-Res rate but no HF content
    if (isHiRes && maxHfDb < -50)
        return ($"Hi-Res ({sampleRate / 1000:F0}k)", "UPSCALE (CD→HI-RES)");

    // 1c. Transcode: brickwall + artifacts in FLAC container
    if (shelfType == "Brickwall" && (artifactLevel == "Strong" || artifactLevel == "Medium"))
    {
        string bw = cutoffHz switch
        {
            <= 17000 => "16kHz",
            <= 19500 => "18kHz",
            <= 20500 => "20kHz",
            _ => "Full Range"
        };
        string dt = cutoffHz switch
        {
            <= 17000 => "MP3 128",
            <= 18500 => "MP3 192",
            <= 20000 => "MP3 256",
            <= 20500 => "MP3 320",
            _ => "UPSCALE (MP3→FLAC)"
        };
        return (bw, dt);
    }

    // 2. Hi-Res genuine
    if (isHiRes)
    {
        string bw = $"Hi-Res ({sampleRate / 1000:F0}k)";
        string dt = maxHfDb >= -30 ? $"HI-RES {sampleRate / 1000:F0}k" : "UNCERTAIN";
        return (bw, dt);
    }

    // 3. AAC detection (M4A container — soft cutoff, no brickwall)
    if (encoderMatch.Contains("AAC") || isMqa)
    {
        string bw = cutoffHz >= 20000 ? "Full Range" : $"{cutoffHz / 1000:F0}kHz";
        string dt = cutoffHz switch
        {
            >= 20000 => "AAC 256",
            >= 18000 => "AAC 192",
            >= 16000 => "AAC 128",
            _ => "AAC 64"
        };
        return (bw, dt);
    }

    // 4. MP3 detected by brickwall + encoder match
    if (encoderMatch.StartsWith("MP3"))
    {
        string bw = cutoffHz switch
        {
            <= 17000 => "16kHz",
            <= 19500 => "18kHz",
            <= 20500 => "20kHz",
            _ => "Full Range"
        };
        string dt = encoderMatch switch
        {
            "MP3 128-192 kbps" => "MP3 128",
            "MP3 192-256 kbps" => "MP3 192",
            "MP3 320 / AAC 256 kbps" => "MP3 320",
            _ => "MP3"
        };
        return (bw, dt);
    }

    // 5. Lossless
    string bandwidth = cutoffHz >= nyquist * 0.95 ? "Full Range" : $"{cutoffHz / 1000:F0}kHz";
    string detectedType;

    if (isCdAligned && sampleRate == 44100)
        detectedType = "LOSSLESS (CD)";
    else if (bitDepth > 16 && !lsbZeroPadded)
        detectedType = "LOSSLESS 24bit";
    else
        detectedType = "LOSSLESS (WEB)";

    // Check for intentional mastering LPF (natural cutoff, no artifacts)
    if (cutoffHz < nyquist * 0.9 && artifactLevel == "None")
        detectedType = "LOSSLESS (Mastered LPF)";

    return (bandwidth, detectedType);
}
```

- [ ] **Step 2: Wire into AudioPipeline**

In `Services/AudioPipeline.cs`, after all detectors complete and before scoring, add:

```csharp
// Compute ClaimedType
result = result with
{
    ClaimedType = GetClaimedType(fileInfo.FilePath, sampleRate)
};

// Compute Bandwidth + DetectedType
var (bandwidth, detectedType) = CutoffDetector.ClassifyBandwidth(
    cutoffHz, shelfType, sampleRate, hasArtifacts, artifactLevel,
    hasSpectralHoles, maxHfDb, bitResult.LsbZeroPadded,
    bitResult.EffectiveBitDepth, bitDepth, containerResult.IsCdAligned,
    containerResult.IsMqa, containerResult.IsHdcd,
    tpResult.ClippingPercent > 0, encoderMatch);

result = result with { Bandwidth = bandwidth, DetectedType = detectedType };
```

Add helper:
```csharp
private static string GetClaimedType(string filePath, int sampleRate)
{
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    string type = ext switch
    {
        ".mp3" => "MP3",
        ".m4a" => "AAC",
        ".flac" => "FLAC",
        ".wav" => "WAV",
        ".alac" => "ALAC",
        _ => "Unknown"
    };
    if (sampleRate >= 88200)
        type = $"HI-RES {sampleRate / 1000:F0}k";
    return type;
}
```

- [ ] **Step 3: Commit**

```bash
git add Services/CutoffDetector.cs Services/AudioPipeline.cs
git commit -m "feat: add bandwidth mapping and detected-type logic"
```

---

### Task 5: Remove DR from Scoring

**Files:**
- Modify: `Services/Analysis/QualityScorer.cs`
- Modify: `Services/Analysis/ScoringProfile.cs` — remove `DrThresholds`
- Modify: `Services/AudioPipeline.cs` — update the score/decision for non-MP3 files

- [ ] **Step 1: Remove DR from QualityScorer**

In `Services/Analysis/QualityScorer.cs`, remove these lines from the `Score` method:
```csharp
foreach (var (threshold, penalty) in _p.DrThresholds)
    if (r.DynamicRange < threshold) { score -= penalty; break; }
```

- [ ] **Step 2: Clean up ScoringProfile**

In `Services/Analysis/ScoringProfile.cs`, remove the `DrThresholds` property (line 40):
```csharp
public (double threshold, int penalty)[] DrThresholds { get; init; } = { ... };
```

- [ ] **Step 3: Commit**

```bash
git add Services/Analysis/QualityScorer.cs Services/Analysis/ScoringProfile.cs
git commit -m "refactor: remove DR from quality scoring — informational only now"
```

---

### Task 6: Separate Scoring per Type

**Files:**
- Modify: `Services/AudioPipeline.cs` — restructure ComputeMp3Quality / ComputeAacQuality / lossless score

- [ ] **Step 1: Restructure scoring in AudioPipeline.Analyze()**

Replace the current scoring section (lines 232-286) with:

```csharp
// Scoring per type
string detectedType = result.DetectedType;
bool isMp3Detected = detectedType.StartsWith("MP3") || isMp3;
bool isAacDetected = detectedType.StartsWith("AAC") || isAac;

double losslessScore;
double hiResScore = 0;
double qualityPercent;
string decision;

if (isMp3Detected)
{
    losslessScore = ComputeMp3Quality(cutoffHz, sampleRate, mp3Bitrate, actualBitrate, artifactLevel, hasSpectralHoles);
    var (masterScore, _) = _qualityScorer.Score(result);
    qualityPercent = losslessScore * 0.6 + masterScore * 0.4;
    decision = losslessScore >= 80 ? "KEEP"
        : losslessScore >= 50 ? "INVESTIGATE"
        : "REPLACE";
}
else if (isAacDetected)
{
    losslessScore = ComputeAacQuality(cutoffHz, sampleRate, aacBitrate, actualBitrate, artifactLevel, hasSpectralHoles);
    var (masterScore, _) = _qualityScorer.Score(result);
    qualityPercent = losslessScore * 0.6 + masterScore * 0.4;
    decision = losslessScore >= 80 ? "KEEP"
        : losslessScore >= 50 ? "INVESTIGATE"
        : "REPLACE";
}
else
{
    // Lossless or Hi-Res
    losslessScore = _losslessScorer.Score(result);
    hiResScore = _losslessScorer.ScoreHiRes(result);
    (qualityPercent, decision) = _qualityScorer.Score(result);
}
```

- [ ] **Step 2: Add bitrate discrepancy penalty for MP3**

In `ComputeMp3Quality`, after the existing bitrate ratio check (line 353-359), add:
```csharp
// Claimed vs spectral bitrate discrepancy
if (detectedType.StartsWith("MP3") && !detectedType.Contains(bitrate.ToString()))
{
    int spectralBitrate = cutoffHz switch
    {
        <= 17000 => 128,
        <= 19500 => 192,
        <= 20500 => 320,
        _ => 320
    };
    if (bitrate > 0 && spectralBitrate < bitrate * 0.8)
        score -= 30;
}
```

- [ ] **Step 3: Commit**

```bash
git add Services/AudioPipeline.cs
git commit -m "feat: separate scoring per type (MP3/AAC differ from Lossless/Hi-Res)"
```

---

### Phase 3: UI Changes

### Task 7: AudioFileViewModel — New Properties + DR Tooltips

**Files:**
- Modify: `ViewModels/AudioFileViewModel.cs`

- [ ] **Step 1: Add new properties**

Add after line 55 (`ActualBitrate`):
```csharp
[ObservableProperty] private string _claimedType = "";
[ObservableProperty] private string _detectedType = "";
[ObservableProperty] private string _bandwidth = "";
[ObservableProperty] private string _sizePerMinute = "";
[ObservableProperty] private System.Windows.Media.Brush _sizePerMinuteColor =
    System.Windows.Media.Brushes.Gray;
[ObservableProperty] private System.Windows.Media.Brush _detectedTypeColor =
    System.Windows.Media.Brushes.Gray;
```

- [ ] **Step 2: Map them in ApplyResult**

In `ApplyResult()`, after `ActualBitrate = r.ActualBitrate;` (line 175), add:
```csharp
ClaimedType = r.ClaimedType;
DetectedType = r.DetectedType;
Bandwidth = r.Bandwidth;

double mbPerMin = r.DurationSeconds > 0
    ? new System.IO.FileInfo(r.FilePath).Length / (1024.0 * 1024.0) / (r.DurationSeconds / 60.0)
    : 0;
SizePerMinute = $"{mbPerMin:F1}";

// Color by MB/min
if (mbPerMin < 5)
    SizePerMinuteColor = GetBrush("SuspiciousAmberBrush");
else if (mbPerMin < 12)
    SizePerMinuteColor = GetBrush("LosslessGreenBrush");
else if (mbPerMin < 25)
    SizePerMinuteColor = GetBrush("LosslessGreenBrush");
else
    SizePerMinuteColor = GetBrush("AccentBrush");

// DetectedTypeColor: green if matches Claimed, red if not
bool match = string.Equals(r.ClaimedType, r.DetectedType, StringComparison.OrdinalIgnoreCase)
    || (r.DetectedType.StartsWith("LOSSLESS") && r.ClaimedType is "FLAC" or "ALAC" or "WAV")
    || (r.DetectedType.StartsWith("HI-RES") && r.ClaimedType.StartsWith("HI-RES"));
DetectedTypeColor = match ? GetBrush("LosslessGreenBrush") : GetBrush("FakeRedBrush");
```

Add helper at end of file:
```csharp
private static System.Windows.Media.Brush GetBrush(string key)
{
    return System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush
        ?? System.Windows.Media.Brushes.Gray;
}
```

- [ ] **Step 3: Update DR metric tooltips**

In `BuildMetricItems()` where DR is built (around line 320-331), update the `Description` field to include genre-specific info:

```csharp
Description = r.DynamicRange >= 10
    ? "DR12+ — аудиофильский диапазон (джаз, классика, акустика)"
    : r.DynamicRange >= 8
        ? "DR8-11 — золотая середина (рок 80-90х, инди, симфо-метал)"
        : r.DynamicRange >= 5
            ? "DR5-7 — плотный звук (современный метал, альт-рок, поп)"
            : "DR3-4 — кирпичная стена (EDM, экстрим-метал, гиперпоп)"
```

- [ ] **Step 4: Commit**

```bash
git add ViewModels/AudioFileViewModel.cs
git commit -m "feat: add ClaimedType/DetectedType/Bandwidth/MB-per-min to ViewModel + DR tooltips"
```

---

### Task 8: MainView UI — Table Columns + Colors + Filters

**Files:**
- Modify: `Views/MainWindow.xaml`
- Modify: `ViewModels/MainViewModel.cs`

- [ ] **Step 1: Restructure DataGrid columns**

Replace the current DataGrid columns in `Views/MainWindow.xaml` (lines 251-324) with:

```xml
<DataGrid.Columns>
    <!-- Icon -->
    <DataGridTemplateColumn Header="" Width="35">
        <DataGridTemplateColumn.CellTemplate>
            <DataTemplate>
                <TextBlock Text="{Binding AnalysisStatus, Converter={StaticResource IconConverter}}"
                           HorizontalAlignment="Center" FontSize="14"/>
            </DataTemplate>
        </DataGridTemplateColumn.CellTemplate>
    </DataGridTemplateColumn>

    <!-- Name -->
    <DataGridTextColumn Header="Название" Binding="{Binding FileName}" Width="180"
                       SortMemberPath="FileName">
        <DataGridTextColumn.ElementStyle>
            <Style TargetType="TextBlock">
                <Setter Property="TextTrimming" Value="CharacterEllipsis"/>
            </Style>
        </DataGridTextColumn.ElementStyle>
    </DataGridTextColumn>

    <!-- Duration -->
    <DataGridTemplateColumn Header="Длит." Width="55" SortMemberPath="DurationSeconds">
        <DataGridTemplateColumn.CellTemplate>
            <DataTemplate>
                <TextBlock Text="{Binding DurationSeconds, Converter={StaticResource DurationFormat}}"
                           HorizontalAlignment="Right" FontFamily="Consolas" FontSize="11"
                           Foreground="{DynamicResource FgMutedBrush}"/>
            </DataTemplate>
        </DataGridTemplateColumn.CellTemplate>
    </DataGridTemplateColumn>

    <!-- Format -->
    <DataGridTextColumn Header="Формат" Binding="{Binding Format}" Width="60"
                       SortMemberPath="Format"/>

    <!-- Bandwidth -->
    <DataGridTextColumn Header="Полоса" Binding="{Binding Bandwidth}" Width="70"
                       SortMemberPath="Bandwidth"/>

    <!-- MB/min -->
    <DataGridTextColumn Header="МБ/мин" Binding="{Binding SizePerMinute}" Width="60"
                       SortMemberPath="SizePerMinute">
        <DataGridTextColumn.ElementStyle>
            <Style TargetType="TextBlock">
                <Setter Property="Foreground" Value="{Binding SizePerMinuteColor}"/>
                <Setter Property="FontFamily" Value="Consolas"/>
                <Setter Property="HorizontalAlignment" Value="Center"/>
            </Style>
        </DataGridTextColumn.ElementStyle>
    </DataGridTextColumn>

    <!-- DR -->
    <DataGridTextColumn Header="DR" Binding="{Binding DynamicRange, StringFormat={}{0:F0}}" Width="40"
                       SortMemberPath="DynamicRange"/>

    <!-- Claimed Type -->
    <DataGridTextColumn Header="Заявлен" Binding="{Binding ClaimedType}" Width="80"
                       SortMemberPath="ClaimedType"/>

    <!-- Detected Type (colored) -->
    <DataGridTextColumn Header="По анализу" Binding="{Binding DetectedType}" Width="100"
                       SortMemberPath="DetectedType">
        <DataGridTextColumn.ElementStyle>
            <Style TargetType="TextBlock">
                <Setter Property="Foreground" Value="{Binding DetectedTypeColor}"/>
                <Setter Property="FontWeight" Value="Bold"/>
            </Style>
        </DataGridTextColumn.ElementStyle>
    </DataGridTextColumn>

    <!-- Decision -->
    <DataGridTextColumn Header="Решение" Binding="{Binding VerdictLabel}" Width="90"
                       SortMemberPath="VerdictLabel">
        <DataGridTextColumn.ElementStyle>
            <Style TargetType="TextBlock">
                <Setter Property="Foreground" Value="{Binding VerdictLabel, Converter={StaticResource DecToColor}}"/>
                <Setter Property="FontWeight" Value="Bold"/>
            </Style>
        </DataGridTextColumn.ElementStyle>
    </DataGridTextColumn>
</DataGrid.Columns>
```

- [ ] **Step 2: Fix ToggleButton colors**

In `MainWindow.xaml`, replace the ToggleButton section (lines 212-220) with:

```xml
<ToggleButton Content="LOSSLESS" IsChecked="{Binding ShowKeep}" Width="70"
               Style="{StaticResource FilterToggleStyle}"
               Foreground="{DynamicResource LosslessGreenBrush}" Margin="2,0" FontSize="10"/>
<ToggleButton Content="NOT SURE" IsChecked="{Binding ShowInvestigate}" Width="70"
               Style="{StaticResource FilterToggleStyle}"
               Foreground="{DynamicResource SuspiciousAmberBrush}" Margin="2,0" FontSize="10"/>
<ToggleButton Content="REPLACE" IsChecked="{Binding ShowReplace}" Width="65"
               Style="{StaticResource FilterToggleStyle}"
               Foreground="{DynamicResource FakeRedBrush}" Margin="2,0" FontSize="10"/>
<ToggleButton Content="MP3" IsChecked="{Binding ShowMp3}" Width="45"
               Style="{StaticResource FilterToggleStyle}"
               Foreground="#f9e2af" Margin="2,0" FontSize="10"/>
```

Add to `Themes/Dark.xaml`:
```xml
<Style x:Key="FilterToggleStyle" TargetType="ToggleButton">
    <Setter Property="Background" Value="{StaticResource BgSecondaryBrush}"/>
    <Setter Property="Foreground" Value="{StaticResource FgSecondaryBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource BorderBrush}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="4,2"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ToggleButton">
                <Border x:Name="border" Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        CornerRadius="4" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="border" Property="Background" Value="{Binding RelativeSource={RelativeSource Self}, Path=Foreground}"/>
                        <Setter TargetName="border" Property="Opacity" Value="0.85"/>
                    </Trigger>
                    <Trigger Property="IsChecked" Value="False">
                        <Setter Property="Background" Value="{StaticResource BgSecondaryBrush}"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 3: Fix verdict bar text color**

In `MainWindow.xaml`, line 458, change:
```xml
Foreground="{Binding SelectedFile.VerdictLabel, Converter={StaticResource DecToColor}}"
```
To:
```xml
Foreground="#0f0f1a"
```

- [ ] **Step 4: Fix progress bar**

Line 148-154, increase height and move text out:
```xml
<ProgressBar Value="{Binding Progress}" Minimum="0" Maximum="100"
             Height="16" VerticalAlignment="Center"
             Foreground="{DynamicResource AccentBrush}"
             Background="{DynamicResource BgTertiaryBrush}"/>
<TextBlock Text="{Binding ProgressText}"
           HorizontalAlignment="Center" VerticalAlignment="Center"
           FontSize="10" Foreground="{DynamicResource FgMutedBrush}"
           Margin="0,18,0,0"/>
```

- [ ] **Step 5: Update PopulateArtistGroups for worst track**

In `ViewModels/MainViewModel.cs`, modify `PopulateArtistGroups()` to compute worst track:

After computing averages (around line 573-582), add:
```csharp
album.WorstTrackScore = completed.Count > 0
    ? completed.Min(t => t.QualityScorePercent)
    : 0;
var worst = completed.OrderBy(t => t.QualityScorePercent).FirstOrDefault();
album.WorstTrackDecision = worst?.DetectedType ?? "";
```

Update the TreeView template in `MainWindow.xaml` for AlbumGroup to show:
```xml
<StackPanel Orientation="Horizontal">
    <TextBlock Text="{Binding AlbumName}" FontSize="12" Margin="8,0,4,0" VerticalAlignment="Center"/>
    <TextBlock Text="{Binding AverageQualityScore, StringFormat={}{F0}%}" FontSize="10"
               Foreground="{DynamicResource FgMutedBrush}" VerticalAlignment="Center"/>
    <Border CornerRadius="3" Padding="4,1" Margin="4,0,0,0" VerticalAlignment="Center"
            Background="{Binding AlbumVerdict, Converter={StaticResource DecToColor}}">
        <TextBlock Text="{Binding AlbumVerdict}" FontSize="9" Foreground="#0f0f1a" FontWeight="SemiBold"/>
    </Border>
</StackPanel>
```

- [ ] **Step 6: Commit**

```bash
git add Views/MainWindow.xaml ViewModels/MainViewModel.cs Themes/Dark.xaml
git commit -m "feat: restructure table columns, fix filter colors, verdict bar, progress bar, worst track in album tree"
```

---

### Task 9: Converter Cleanup

**Files:**
- Modify: `Converters/ScoreToColorConverter.cs`
- Modify: `Converters/AuthenticityToColorConverter.cs` — update for new type labels

- [ ] **Step 1: Clean ScoreToColorConverter**

Replace entire file:
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LosslessChecker.Converters;

public class ScoreToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double score = value switch
        {
            int i => i,
            double d => d,
            _ => -1
        };

        string key = score switch
        {
            >= 70 => "LosslessGreenBrush",
            >= 40 => "SuspiciousAmberBrush",
            _ => "FakeRedBrush"
        };

        return System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush
            ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
```

- [ ] **Step 2: Update AuthenticityToColorConverter**

Replace the key mapping with simplified version:
```csharp
string key = value is string s
    ? s.StartsWith("LOSSLESS") || s.StartsWith("HI-RES") || s.Contains("MASTERED") ? "LosslessGreenBrush"
    : s.Contains("MP3") || s.Contains("AAC") || s.StartsWith("UPSCALE") || s.StartsWith("FAKE") || s.StartsWith("FALSE") ? "FakeRedBrush"
    : s.StartsWith("UNCERTAIN") ? "SuspiciousAmberBrush"
    : "NeutralGrayBrush"
    : "NeutralGrayBrush";
```

- [ ] **Step 3: Update DecisionToColorConverter**

Add `"MASTERED"` as green variant:
```csharp
string key = value is string s
    ? s == "LOSSLESS" || s == "HI-RES" ? "LosslessGreenBrush"
    : s == "NOT SURE" ? "SuspiciousAmberBrush"
    : s == "REPLACE" || s.StartsWith("MP3") || s.StartsWith("AAC") ? "FakeRedBrush"
    : "NeutralGrayBrush"
    : "NeutralGrayBrush";
```

- [ ] **Step 4: Commit**

```bash
git add Converters/ScoreToColorConverter.cs Converters/AuthenticityToColorConverter.cs Converters/DecisionToColorConverter.cs
git commit -m "refactor: simplify converters, remove duplicate score thresholds, update for new type labels"
```

---

### Phase 4: Spectrogram

### Task 10: Spectrogram — Grid Lines, Pan/Zoom Rework, Label Fix

**Files:**
- Modify: `Views/SpectrogramWindow.xaml.cs`
- Modify: `Views/SpectrogramWindow.xaml`

- [ ] **Step 1: Add frequency grid lines in DrawAxes**

In `SpectrogramWindow.xaml.cs`, `DrawAxes()`, after the existing freqLabels loop, add standard markers:

```csharp
double[] standardMarkers = { 1000, 5000, 10000, 16000, 20000, 22050 };
foreach (var freq in standardMarkers)
{
    if (freq > nyquist) continue;
    double ratio = (Math.Log10(freq) - logMin) / logRange;
    double y = canvasH - ratio * canvasH;

    var line = new Line
    {
        X1 = 0, Y1 = y, X2 = canvasW, Y2 = y,
        Stroke = GridBrush,
        StrokeThickness = 0.8,
        Opacity = 0.5,
        StrokeDashArray = new DoubleCollection { 4, 2 }
    };
    OverlayCanvas.Children.Add(line);
}
```

- [ ] **Step 2: Change pan trigger from LMB to MMB/Shift+LMB**

Replace `Window_MouseLeftButtonDown` handler:
```csharp
private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
{
    if (e.ChangedButton == System.Windows.Input.MouseButton.Middle ||
        (e.ChangedButton == System.Windows.Input.MouseButton.Left &&
         System.Windows.Input.Keyboard.Modifiers == ModifierKeys.Shift))
    {
        _isPanning = true;
        _lastMousePos = e.GetPosition(this);
        SpectrogramImage.CaptureMouse();
    }
}
```

Leave `MouseMove` and `MouseUp` unchanged (they already check `_isPanning`).

- [ ] **Step 3: Fix frequency label positioning**

In `DrawAxes()`, change `Canvas.SetLeft(tb, 0)` to `Canvas.SetLeft(tb, -45)` for frequency labels (negative to push them left of the image margin). Then in `SpectrogramWindow.xaml`, change the Grid margin from `50,10,50,30` to `70,10,50,30` to accommodate the wider left margin.

- [ ] **Step 4: Commit**

```bash
git add Views/SpectrogramWindow.xaml Views/SpectrogramWindow.xaml.cs
git commit -m "feat: add standard frequency grid lines, rework pan to MMB/Shift+LMB, fix label positioning"
```

---

### Phase 5: Caching & Polish

### Task 11: JSON Cache Implementation

**Files:**
- Create: `Services/AnalysisCache.cs`
- Modify: `ViewModels/MainViewModel.cs`

- [ ] **Step 1: Create AnalysisCache service**

New file `Services/AnalysisCache.cs`:

```csharp
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class AnalysisCache
{
    private readonly string _cachePath;
    private Dictionary<string, AnalysisResult> _cache = new();

    public AnalysisCache()
    {
        _cachePath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
            "analysis_cache.json");
        Load();
    }

    public bool TryGet(string filePath, long fileSize, DateTime lastModified, out AnalysisResult? result)
    {
        string key = ComputeKey(filePath, fileSize, lastModified);
        return _cache.TryGetValue(key, out result);
    }

    public void Store(string filePath, long fileSize, DateTime lastModified, AnalysisResult result)
    {
        string key = ComputeKey(filePath, fileSize, lastModified);
        _cache[key] = result;
        Save();
    }

    public void Invalidate(string filePath)
    {
        var keysToRemove = _cache.Keys.Where(k => k.Contains(filePath)).ToList();
        foreach (var key in keysToRemove)
            _cache.Remove(key);
        Save();
    }

    private static string ComputeKey(string filePath, long fileSize, DateTime lastModified)
    {
        string raw = $"{filePath}|{fileSize}|{lastModified.Ticks}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                _cache = JsonSerializer.Deserialize<Dictionary<string, AnalysisResult>>(json) ?? new();
            }
        }
        catch { _cache = new(); }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cachePath, json);
        }
        catch { }
    }
}
```

- [ ] **Step 2: Wire cache into AudioAnalyzer**

In `Services/AudioAnalyzer.cs`:
```csharp
private readonly AudioPipeline _pipeline = new();
private readonly AnalysisCache _cache = new();

public AnalysisResult Analyze(AudioFileInfo fileInfo, CancellationToken ct = default)
{
    var file = new FileInfo(fileInfo.FilePath);
    if (file.Exists && _cache.TryGet(fileInfo.FilePath, file.Length, file.LastWriteTime, out var cached))
        return cached!;

    var result = _pipeline.Analyze(fileInfo, ct);
    if (file.Exists && result.AnalysisStatus == AnalysisStatus.Completed)
        _cache.Store(fileInfo.FilePath, file.Length, file.LastWriteTime, result);

    return result;
}
```

- [ ] **Step 3: Add cache invalidation on new scan**

In `MainViewModel.cs`, `ScanAndAnalyze`, before scanning add:
```csharp
_analyzer.InvalidateCache(folderPath); // optional — invalidate stale entries
```

Or just rely on the file-since timestamp naturally expiring entries.

- [ ] **Step 4: Commit**

```bash
git add Services/AnalysisCache.cs Services/AudioAnalyzer.cs
git commit -m "feat: add JSON-based analysis cache to skip re-analysis of unchanged files"
```

---

### Task 12: SpectrogramWindow frequency label fix for 44.1kHz

**Files:**
- Modify: `Views/SpectrogramWindow.xaml.cs`

- [ ] **Step 1: Add 22.05k label when nyquist = 22050**

In `DrawAxes()`, after the freqLabels loop, add:
```csharp
if (Math.Abs(nyquist - 22050) < 1)
{
    double ratio = (Math.Log10(22050) - logMin) / logRange;
    double y = canvasH - ratio * canvasH;
    var tb = new TextBlock
    {
        Text = "22.05k",
        Foreground = AxisBrush,
        FontSize = 9,
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
    };
    System.Windows.Controls.Canvas.SetLeft(tb, 0);
    System.Windows.Controls.Canvas.SetTop(tb, y - 7);
    OverlayCanvas.Children.Add(tb);
}
```

- [ ] **Step 2: Commit**

```bash
git add Views/SpectrogramWindow.xaml.cs
git commit -m "feat: add 22.05k frequency label for CD-spectrum spectrograms"
```

---

## Self-Review Checklist

- [ ] **Spec coverage**: Every section from the spec has a corresponding task.
  - [x] Table columns (Task 8)
  - [x] Bandwidth mapping (Task 4)
  - [x] MB/min indicator (Task 7 + 8)
  - [x] DR informational (Task 5 + 7)
  - [x] Claimed/Detected type (Task 1, 4, 7, 8)
  - [x] Scoring per type (Task 6)
  - [x] DR removed from scoring (Task 5)
  - [x] Color refactoring (Task 8 + 9)
  - [x] Album worst track (Task 8)
  - [x] Spectrogram grid/labels/pan (Task 10 + 12)
  - [x] LUFS bug fix (Task 2)
  - [x] TotalFiles + concurrency + memory (Task 3)
  - [x] Caching (Task 11)
- [ ] **Placeholder scan**: No "TBD", "TODO", or vague steps.
- [ ] **Type consistency**: All type names, method signatures, and property names match across tasks.
