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
