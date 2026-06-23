using LosslessChecker.Models;

namespace LosslessChecker.Services.Analyzers;

public class DcOffsetDetector
{
    private const double ThresholdPercent = 0.001;

    public DcOffsetResult Analyze(StereoBuffer buffer)
    {
        double meanL = buffer.Left.Average();
        double meanR = buffer.Right.Length > 0 ? buffer.Right.Average() : 0;

        double dcOffsetL = Math.Round(meanL * 100.0, 4);
        double dcOffsetR = Math.Round(meanR * 100.0, 4);

        bool hasDcOffset = Math.Abs(dcOffsetL) > ThresholdPercent
                        || Math.Abs(dcOffsetR) > ThresholdPercent;

        return new DcOffsetResult(dcOffsetL, dcOffsetR, hasDcOffset);
    }
}

public record DcOffsetResult(double DcOffsetL, double DcOffsetR, bool HasDcOffset);
