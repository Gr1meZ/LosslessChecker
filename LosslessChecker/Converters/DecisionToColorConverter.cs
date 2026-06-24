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
            : s == "REPLACE" || s.StartsWith("MP3", System.StringComparison.OrdinalIgnoreCase) ? "FakeRedBrush"
            : "NeutralGrayBrush"
            : "NeutralGrayBrush";
        return System.Windows.Application.Current.TryFindResource(key) ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
