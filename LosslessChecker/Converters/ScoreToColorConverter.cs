using System.Globalization;
using System.Windows.Data;

namespace LosslessChecker.Converters;

public class ScoreToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double score)
        {
            if (score >= 90)
                return new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(46, 160, 67));
            if (score >= 60)
                return new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(210, 153, 34));
            return new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(207, 34, 46));
        }

        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
