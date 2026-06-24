using System.Globalization;
using System.Windows.Data;

namespace LosslessChecker.Converters;

public class DurationFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double seconds && seconds > 0)
        {
            int totalSecs = (int)Math.Round(seconds);
            int mins = totalSecs / 60;
            int secs = totalSecs % 60;
            return $"{mins}:{secs:D2}";
        }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
