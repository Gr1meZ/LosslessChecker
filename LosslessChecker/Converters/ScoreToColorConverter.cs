using System.Globalization;
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

        if (score >= 7)
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 160, 67));     // green
        if (score >= 4)
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 153, 34));     // amber
        if (score >= 1)
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(207, 34, 46));     // red

        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
