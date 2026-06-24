using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LosslessChecker.Converters;

public class AuthenticityToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is string s
            ? s.StartsWith("TRUE") ? "LosslessGreenBrush"
            : s.StartsWith("UNCERTAIN") ? "SuspiciousAmberBrush"
            : s.StartsWith("FALSE") ? "FakeRedBrush"
            : s.StartsWith("LOSSY") ? "FakeRedBrush"
            : "NeutralGrayBrush"
            : "NeutralGrayBrush";
        return System.Windows.Application.Current.TryFindResource(key) ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
