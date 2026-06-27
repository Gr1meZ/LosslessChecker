# Task 7: AudioFileViewModel — New Properties + DR Tooltips

**Files:**
- Modify: `LosslessChecker/ViewModels/AudioFileViewModel.cs`

## Step 1: Add new ObservableProperties

After the existing `[ObservableProperty] private int _mp3Bitrate;` line (around line 51), add:

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

## Step 2: Map new fields in ApplyResult

In `ApplyResult()`, after the line `ActualBitrate = r.ActualBitrate;` (around line 175), add:

```csharp
        ClaimedType = r.ClaimedType;
        DetectedType = r.DetectedType;
        Bandwidth = r.Bandwidth;

        double mbPerMin = r.DurationSeconds > 0
            ? new System.IO.FileInfo(r.FilePath).Length / (1024.0 * 1024.0) / (r.DurationSeconds / 60.0)
            : 0;
        SizePerMinute = $"{mbPerMin:F1}";

        if (mbPerMin < 5)
            SizePerMinuteColor = GetBrush("FgMutedBrush");
        else if (mbPerMin < 12)
            SizePerMinuteColor = GetBrush("LosslessGreenBrush");
        else if (mbPerMin < 25)
            SizePerMinuteColor = GetBrush("AccentBrush");
        else
            SizePerMinuteColor = GetBrush("AccentBrush");

        bool match = string.Equals(r.ClaimedType, r.DetectedType, StringComparison.OrdinalIgnoreCase)
            || (r.DetectedType.StartsWith("LOSSLESS") && r.ClaimedType is "FLAC" or "ALAC" or "WAV")
            || (r.DetectedType.StartsWith("HI-RES") && r.ClaimedType.StartsWith("HI-RES"));
        DetectedTypeColor = match ? GetBrush("LosslessGreenBrush") : GetBrush("FakeRedBrush");
```

## Step 3: Add GetBrush helper

At the END of the class (before closing brace), add:

```csharp
    private static System.Windows.Media.Brush GetBrush(string key)
    {
        return System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Gray;
    }
```

## Step 4: Update DR metric tooltip in BuildMetricItems

Find the DR metric creation in `BuildMetricItems()` (around the section with "Динамический диапазон (DR)", approximately lines 320-331). Update the `Description` string to include genre DR reference:

Change the existing Description to:

```csharp
            Description = "Разница между пиковым и средним уровнем громкости (TT DR Meter). Высокий DR — живой, дышащий звук. Низкий DR — не обязательно плохо, зависит от жанра.",
```

And update the `Typical` string to:

```csharp
            Typical = "DR12+ — аудиофил (джаз, классика, акустика, винил)\nDR8-11 — золотая середина (рок 80-90х, инди, симфо-метал)\nDR5-7 — плотный звук (современный метал, альт-рок, пост-гранж, поп)\nDR3-4 — кирпичная стена (EDM, экстрим-метал, гиперпоп)"
```

## Step 5: Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All tests pass.

## Step 6: Commit

```bash
git add LosslessChecker/ViewModels/AudioFileViewModel.cs
git commit -m "feat: add ClaimedType/DetectedType/Bandwidth/MB-per-min to ViewModel + DR genre tooltips"
```
