# Task 9: Converter Cleanup

**Files:**
- Modify: `LosslessChecker/Converters/ScoreToColorConverter.cs`
- Modify: `LosslessChecker/Converters/AuthenticityToColorConverter.cs`
- Modify: `LosslessChecker/Converters/DecisionToColorConverter.cs`

## Step 1: Clean ScoreToColorConverter.cs

Replace entire file with:

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

## Step 2: Update AuthenticityToColorConverter.cs

Replace entire file with:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LosslessChecker.Converters;

public class AuthenticityToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is string s
            ? s.StartsWith("LOSSLESS") || s.StartsWith("HI-RES") ? "LosslessGreenBrush"
            : s.StartsWith("MP3") || s.StartsWith("AAC") || s.StartsWith("UPSCALE") || s.StartsWith("FAKE") ? "FakeRedBrush"
            : s.StartsWith("UNCERTAIN") ? "SuspiciousAmberBrush"
            : "NeutralGrayBrush"
            : "NeutralGrayBrush";
        return System.Windows.Application.Current.TryFindResource(key) ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
```

## Step 3: Update DecisionToColorConverter.cs

Replace entire file with:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LosslessChecker.Converters;

public class DecisionToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is string s
            ? s == "LOSSLESS" || s == "HI-RES" ? "LosslessGreenBrush"
            : s == "NOT SURE" ? "SuspiciousAmberBrush"
            : s == "REPLACE" || s.StartsWith("MP3", System.StringComparison.OrdinalIgnoreCase) || s.StartsWith("AAC", System.StringComparison.OrdinalIgnoreCase) ? "FakeRedBrush"
            : "NeutralGrayBrush"
            : "NeutralGrayBrush";
        return System.Windows.Application.Current.TryFindResource(key) ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
```

## Step 4: Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All tests pass.

## Step 5: Commit

```bash
git add LosslessChecker/Converters/ScoreToColorConverter.cs LosslessChecker/Converters/AuthenticityToColorConverter.cs LosslessChecker/Converters/DecisionToColorConverter.cs
git commit -m "refactor: simplify converters, remove duplicate thresholds, update for new type labels"
```
