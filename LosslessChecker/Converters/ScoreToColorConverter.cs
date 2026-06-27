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
