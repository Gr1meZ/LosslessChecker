# Fix Batch: 13 UI/Logic Fixes

Apply ALL the following changes to the LosslessChecker project.

Working directory: `D:\Projects\LosslessChecker\LosslessChecker`

---

## Fix 1: AudioFileViewModel.cs — String comparison bug (line 203)

The pattern `r.ClaimedType is "FLAC"` is C# type pattern matching — it checks if `r.ClaimedType` is of TYPE `string` equal to the constant `"FLAC"`, but this doesn't work correctly for runtime string comparison.

CHANGE line 203 FROM:
```csharp
            || (r.DetectedType.StartsWith("LOSSLESS") && r.ClaimedType is "FLAC" or "ALAC" or "WAV")
```
TO:
```csharp
            || (r.DetectedType.StartsWith("LOSSLESS") && (r.ClaimedType == "FLAC" || r.ClaimedType == "ALAC" || r.ClaimedType == "WAV"))
```

---

## Fix 2: AudioFileViewModel.cs — Quality score % (line 631)

The "Качество мастеринга" metric shows `${r.QualityScorePercent:F0}%` — the % sign is already there. The issue is the `Value` and `Status` colors. Replace lines 625-636 with improved readability:

CHANGE lines 624-636 FROM:
```csharp
        // Quality
        string qualStatus = r.QualityScorePercent >= 70 ? "✓ Отлично" : r.QualityScorePercent >= 40 ? "⚠ Нормально" : "✗ Плохо";
        string qualColor = r.QualityScorePercent >= 70 ? "#2EA043" : r.QualityScorePercent >= 40 ? "#D29922" : "#CF222E";
        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Качество мастеринга",
            Value = $"{r.QualityScorePercent:F0}%",
            Status = qualStatus,
            StatusColor = qualColor,
            Description = "Взвешенная оценка качества мастеринга (0–100%). DR (вес 25), клиппинг (20), True Peak (13), LUFS (15), DC Offset (8), фаза (12), битность (5).",
            Typical = "70–100% — отличный мастеринг\n40–69% — средний мастеринг\n<40% — плохой мастеринг"
        });
```

TO:
```csharp
        // Quality
        string qualStatus = r.QualityScorePercent >= 70 ? "✓ Отлично" : r.QualityScorePercent >= 40 ? "⚠ Нормально" : "✗ Плохо";
        string qualColor = r.QualityScorePercent >= 70 ? "#2EA043" : r.QualityScorePercent >= 40 ? "#D29922" : "#CF222E";
        items.Add(new MetricItem
        {
            Category = "Итог",
            Name = "Качество мастеринга",
            Value = $"{r.QualityScorePercent:F0}%",
            Status = qualStatus,
            StatusColor = qualColor,
            Description = "Взвешенная оценка качества мастеринга (0–100%). Учитывает клиппинг, True Peak, LUFS, DC Offset, фазу.",
            Typical = "70–100% — отличный мастеринг\n40–69% — средний мастеринг\n<40% — плохой мастеринг"
        });
```

---

## Fix 3: AudioPipeline.cs — Fix GetClaimedType for ALAC

The method `GetClaimedType` at the end of AudioPipeline.cs returns "AAC" for all `.m4a` files, but `.m4a` can also contain ALAC.

Find the `GetClaimedType` method and REPLACE it entirely with:

```csharp
    private static string GetClaimedType(string filePath, int sampleRate)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        string type;
        if (ext == ".m4a")
        {
            try
            {
                var info = Mp4CodecReader.DetectCodec(filePath);
                type = info.Codec == "alac" ? "ALAC" : "AAC";
            }
            catch { type = "AAC"; }
        }
        else
        {
            type = ext switch
            {
                ".mp3" => "MP3",
                ".flac" => "FLAC",
                ".wav" => "WAV",
                ".alac" => "ALAC",
                _ => "Unknown"
            };
        }
        if (sampleRate >= 88200)
            type = $"HI-RES {sampleRate / 1000:F0}k";
        return type;
    }
```

---

## Fix 4: CutoffDetector.cs — ClassifyBandwidth for ALAC

In the `ClassifyBandwidth` method, find this block (the lossless detection section):

```csharp
        // 3. Lossless (no artifacts, no brickwall at encoder frequencies)
        if (shelfType == "Natural" || (artifactLevel == "None" && shelfType == "Filtered"))
```

The condition is too strict — it requires Natural shelf or Filtered+NoArtifacts. ALAC files with no compression artifacts should be classified as lossless regardless of shelf type (unless they have brickwall+artifacts, which is caught earlier). Change it to also catch any non-MP3, non-AAC, non-Hi-Res file that has no lossy artifacts:

REPLACE the entire "3. Lossless" block with:

```csharp
        // 3. Lossless — any file without lossy compression artifacts
        // (not MP3, not AAC, not Hi-Res, not fake 24-bit, not transcoded)
        if (!isHiRes)
        {
            bool hasLossyArtifacts = (artifactLevel == "Strong" || artifactLevel == "Medium") 
                && (hasSpectralHoles || shelfType == "Brickwall");
            
            if (!hasLossyArtifacts || shelfType == "Natural")
            {
                string bw = cutoffHz >= nyquist * 0.92 ? "Full Range" : $"{cutoffHz / 1000:F0}kHz";
                string dt;
                if (isCdAligned && sampleRate == 44100)
                    dt = "LOSSLESS (CD)";
                else if (bitDepth > 16 && !lsbZeroPadded)
                    dt = "LOSSLESS 24bit";
                else
                    dt = "LOSSLESS (WEB)";

                if (cutoffHz < nyquist * 0.90 && artifactLevel == "None")
                    dt = "LOSSLESS (Mastered LPF)";

                return (bw, dt);
            }
        }
```

---

## Fix 5: AnalysisCache.cs — Add Clear() method

Add this public method inside the `AnalysisCache` class (before the closing `}`):

```csharp
    public void Clear()
    {
        _cache.Clear();
        try { File.Delete(_cachePath); } catch { }
    }
```

---

## Fix 6: MainWindow.xaml — Remove filter ToggleButtons

DELETE lines 223-234 (the 4 ToggleButton elements: LOSSLESS, NOT SURE, REPLACE, MP3) from MainWindow.xaml.

So the `<StackPanel Grid.Row="0" Orientation="Horizontal" ...>` should only contain the SearchQuery TextBox and nothing else:

```xml
                <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,4">
                    <TextBox Text="{Binding SearchQuery, UpdateSourceTrigger=PropertyChanged}"
                              Width="180" Margin="0,0,8,0"
                              Background="{DynamicResource BgSecondaryBrush}" Foreground="{DynamicResource FgPrimaryBrush}"
                              BorderBrush="{DynamicResource BorderBrush}" FontSize="11"
                              ToolTip="Поиск по названию, артисту, альбому"/>
                </StackPanel>
```

---

## Fix 7: MainWindow.xaml — Fix DataGrid hover/selection colors

CHANGE lines 251-252 FROM:
```xml
                    <SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}" Color="#818cf8"/>
                    <SolidColorBrush x:Key="{x:Static SystemColors.HighlightTextBrushKey}" Color="#0f0f1a"/>
```
TO:
```xml
                    <SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}" Color="#4a6cf7"/>
                    <SolidColorBrush x:Key="{x:Static SystemColors.HighlightTextBrushKey}" Color="White"/>
```

---

## Fix 8: MainWindow.xaml — Fix column fonts and widths + add Quality column

Find the `<DataGrid.Columns>` block. Make these changes:

a) REMOVE FontSize="10" from "МБ/мин" column element style (line 303) — it should inherit the global 12px from DataGridCell style.

b) REMOVE FontSize="10" from "Заявлен" column element style (line 314).

c) REMOVE FontSize="10" from "По анализу" column element style (line 324). Also remove FontWeight="Bold" from it — just leave Foreground binding.

d) Change "Название" Width="180" to Width="*" (stretch to fill remaining space).

e) Change "По анализу" Width="100" to Width="Auto" MinWidth="100".

f) Add a new column for "Качество" (QualityScorePercent) right AFTER the "DR" column. Insert this before the "Заявлен" column:

```xml
                    <DataGridTextColumn Header="Кач-во" Binding="{Binding QualityScorePercent, StringFormat={}{0:F0}%}" Width="55"
                                        SortMemberPath="QualityScorePercent">
                        <DataGridTextColumn.ElementStyle>
                            <Style TargetType="TextBlock">
                                <Setter Property="Foreground" Value="{Binding QualityScorePercent, Converter={StaticResource ScoreToColor}}"/>
                                <Setter Property="FontWeight" Value="Bold"/>
                                <Setter Property="FontSize" Value="12"/>
                            </Style>
                        </DataGridTextColumn.ElementStyle>
                    </DataGridTextColumn>
```

---

## Fix 9: MainWindow.xaml — Fix progress bar

CHANGE the progress bar section (lines 157-166) FROM:
```xml
                <Grid Grid.Column="5" Margin="20,0,0,0">
                    <ProgressBar Value="{Binding Progress}" Minimum="0" Maximum="100"
                                 Height="16" VerticalAlignment="Center"
                                 Foreground="{DynamicResource AccentBrush}"
                                 Background="{DynamicResource BgTertiaryBrush}"/>
                    <TextBlock Text="{Binding ProgressText}"
                               HorizontalAlignment="Center" VerticalAlignment="Bottom"
                               FontSize="10" Foreground="{DynamicResource FgMutedBrush}"
                               Margin="0,0,0,-2"/>
                </Grid>
```
TO:
```xml
                <Grid Grid.Column="5" Margin="20,0,0,0">
                    <ProgressBar Value="{Binding Progress}" Minimum="0" Maximum="100"
                                 Height="18" VerticalAlignment="Center"
                                 Foreground="{DynamicResource LosslessGreenBrush}"
                                 Background="{DynamicResource BgTertiaryBrush}"/>
                    <TextBlock Text="{Binding ProgressText}"
                               HorizontalAlignment="Center" VerticalAlignment="Center"
                               FontSize="11" FontWeight="Bold"
                               Foreground="White"/>
                </Grid>
```

---

## Fix 10: MainWindow.xaml — Add "Clear Cache" and "Clear Table" buttons to toolbar

In the toolbar Grid (lines 132-156), add 2 new ColumnDefinitions and 2 new buttons BEFORE the "📊 Экспорт" button (Grid.Column="4"):

Add 2 more `<ColumnDefinition Width="Auto"/>` to the Grid.ColumnDefinitions (line 139-140 area). Then shift existing buttons by adding the new ones:

```xml
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
```

Add new buttons BEFORE the "📊 Экспорт" button (change Grid.Column numbers accordingly):

```xml
                <Button Grid.Column="4" Content="🗑 Таблица" Command="{Binding ClearTableCommand}"
                        Style="{StaticResource SecondaryButtonStyle}"
                        Width="100" Height="34" Margin="0,0,6,0"/>
                <Button Grid.Column="5" Content="🧹 Кэш" Command="{Binding ClearCacheCommand}"
                        Style="{StaticResource SecondaryButtonStyle}"
                        Width="80" Height="34" Margin="0,0,6,0"/>
```

Then shift the remaining buttons:
- "📊 Экспорт" → Grid.Column="6"
- Progress bar Grid → Grid.Column="7"

---

## Fix 11: MainViewModel.cs — Remove filter properties

DELETE lines 45-55 (ShowKeep, ShowInvestigate, ShowReplace, ShowMp3 properties).

---

## Fix 12: MainViewModel.cs — Simplify FilterFile

In `FilterFile` method, DELETE lines 88-94 (the verdict filtering block). The method should only filter by TreeView selection and SearchQuery. Result:

```csharp
    private bool FilterFile(object obj)
    {
        if (obj is not AudioFileViewModel f)
            return false;

        if (_selectionFilter != null && !_selectionFilter.Contains(f))
            return false;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            if (!f.FileName.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) &&
                (f.Artist?.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ?? false) == false &&
                (f.Album?.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ?? false) == false)
                return false;
        }

        return true;
    }
```

---

## Fix 13: MainViewModel.cs — Remove filter property change handlers + add Clear commands

a) DELETE lines 233-236 (the partial methods for ShowKeep/ShowInvestigate/ShowReplace/ShowMp3 changes):
```csharp
partial void OnShowKeepChanged(bool value) => ApplyFilters();
partial void OnShowInvestigateChanged(bool value) => ApplyFilters();
partial void OnShowReplaceChanged(bool value) => ApplyFilters();
partial void OnShowMp3Changed(bool value) => ApplyFilters();
```

b) Add a Clear() method to AudioAnalyzer.cs that exposes cache clearing:

In `Services/AudioAnalyzer.cs`, add:
```csharp
public void ClearCache() => _cache.Clear();
```

c) Add two new relay commands to MainViewModel.cs (after the Export command, around line 311):

```csharp
[RelayCommand]
private void ClearTable()
{
    Files.Clear();
    ArtistGroups.Clear();
    _selectionFilter = null;
    FilesView.Refresh();
    SelectedGroup = null;
    SelectedFile = null;
    IsSpectrumVisible = false;
    ProcessedFiles = 0;
    TotalFiles = 0;
    ErrorCount = 0;
    KeepCount = 0;
    InvestigateCount = 0;
    ReplaceCount = 0;
    Progress = 0;
    CurrentlyProcessing = "";
    ShowWelcome = true;
    UpdateSummary();
}

[RelayCommand]
private void ClearCache()
{
    _analyzer.ClearCache();
}
```

---

## Fix 14: Dark.xaml — Remove FilterToggleStyle

DELETE the entire `FilterToggleStyle` block from `Themes/Dark.xaml`.

---

## Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All 46 tests pass.

## Commit

```bash
git add -A
git commit -m "fix: 14 UI/logic fixes — filters removed, string comparison fixed, ALAC detection, progress bar, column widths, cache/clear buttons"
```
