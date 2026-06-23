using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LosslessChecker.Converters;

public class DecisionToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            if (s.StartsWith("KEEP")) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 160, 67));
            if (s.StartsWith("INVESTIGATE")) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 153, 34));
            if (s.StartsWith("REPLACE")) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(207, 34, 46));
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
