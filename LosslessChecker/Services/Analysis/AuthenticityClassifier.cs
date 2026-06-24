using LosslessChecker.Models;

namespace LosslessChecker.Services.Analysis;

public class AuthenticityClassifier
{
    public string Classify(AnalysisResult result)
    {
        if (result.SampleRate >= 88200 && result.CutoffFrequency < 22000)
            return "FAKE HI-RES";

        if (result.CutoffFrequency <= 16500)
            return "FAKE LOSSLESS";
        if (result.CutoffFrequency <= 18500 && result.HasArtifacts)
            return "FAKE LOSSLESS";
        if (result.CutoffFrequency <= 20000 && result.HasArtifacts && result.ShelfType == "Brickwall")
            return "FAKE LOSSLESS";

        if (result.CutoffFrequency <= 21500 && result.CutoffFrequency > 18500)
            return "SUSPICIOUS";
        if (result.IsUpscale)
            return "SUSPICIOUS";
        if (result.BitDepthSuspicious && result.LsbZeroPadded)
            return "SUSPICIOUS";

        return "TRUE LOSSLESS";
    }
}
