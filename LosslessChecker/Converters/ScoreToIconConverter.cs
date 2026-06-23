using System.Globalization;
using System.Windows.Data;
using LosslessChecker.Models;

namespace LosslessChecker.Converters;

public class ScoreToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AnalysisStatus status)
        {
            return status switch
            {
                AnalysisStatus.Pending => "\u23F3",
                AnalysisStatus.Processing => "\u2699",
                AnalysisStatus.Completed => "\u2705",
                AnalysisStatus.Error => "\u26A0",
                _ => "?"
            };
        }
        return "?";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
