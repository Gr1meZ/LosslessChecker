using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LosslessChecker.Converters;

public class AuthenticityToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            if (s.StartsWith("TRUE")) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 160, 67));
            if (s.StartsWith("SUSPICIOUS")) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 153, 34));
            if (s.StartsWith("FAKE")) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(207, 34, 46));
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
