using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LosslessChecker.Converters;

public class ScoreToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var score = value switch
        {
            int i => i,
            double d => (int)d,
            _ => -1
        };

        string key = score switch
        {
            >= 70 => "LosslessGreenBrush",
            >= 40 => "SuspiciousAmberBrush",
            >= 7 => "LosslessGreenBrush",
            >= 4 => "SuspiciousAmberBrush",
            >= 1 => "FakeRedBrush",
            _ => "NeutralGrayBrush"
        };

        return System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush
            ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
