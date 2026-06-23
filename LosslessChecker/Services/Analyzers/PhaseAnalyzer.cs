using LosslessChecker.Models;

namespace LosslessChecker.Services.Analyzers;

public class PhaseAnalyzer
{
    private const int BlockSize = 4096;

    public PhaseResult Analyze(StereoBuffer buffer)
    {
        if (!buffer.IsStereo)
            return new PhaseResult(1.0, true);

        var correlations = new List<double>();
        for (int pos = 0; pos + BlockSize <= buffer.Length; pos += BlockSize)
        {
            double sumXY = 0, sumX2 = 0, sumY2 = 0;
            for (int i = pos; i < pos + BlockSize; i++)
            {
                float x = buffer.Left[i];
                float y = buffer.Right[i];
                sumXY += x * y;
                sumX2 += x * x;
                sumY2 += y * y;
            }

            double denom = Math.Sqrt(sumX2 * sumY2);
            double corr = denom > 1e-10 ? sumXY / denom : 0;
            correlations.Add(corr);
        }

        double avgCorrelation = correlations.Count > 0
            ? Math.Round(correlations.Average(), 2)
            : 1.0;

        bool isMonoCompatible = avgCorrelation >= 0;

        return new PhaseResult(avgCorrelation, isMonoCompatible);
    }
}

public record PhaseResult(double Correlation, bool IsMonoCompatible);
