namespace LosslessChecker.Services;

public class ResamplingDetector
{
    private const int FftSize = 4096;

    public ResamplingResult Detect(byte[] spectrogramFlat, int width, int height, int sampleRate)
    {
        return new ResamplingResult(false, false, "Use DetectFromSpectrum instead");
    }

    public ResamplingResult DetectFromSpectrum(double[] avgSpectrum, int sampleRate)
    {
        int bins = avgSpectrum.Length;
        if (bins < 100)
            return new ResamplingResult(false, false, "Insufficient spectrum data.");

        double nyquist = sampleRate / 2.0;

        // Aliasing: look for isolated narrow peaks in upper spectrum
        double prevDb = 20.0 * Math.Log10(Math.Max(avgSpectrum[0], 1e-10));
        int aliasHits = 0;
        int checkStart = bins / 3;

        for (int i = checkStart + 1; i < bins - 1; i++)
        {
            double db = 20.0 * Math.Log10(Math.Max(avgSpectrum[i], 1e-10));
            double nextDb = 20.0 * Math.Log10(Math.Max(avgSpectrum[i + 1], 1e-10));

            if (db > prevDb + 12 && nextDb < db - 6)
                aliasHits++;
            prevDb = db;
        }

        bool hasAliasing = aliasHits > bins / 40;

        // Ringing: periodic Gibbs-like oscillations in HF
        int ringHits = 0;
        for (int i = bins * 2 / 3; i < bins - 3; i++)
        {
            double db0 = 20.0 * Math.Log10(Math.Max(avgSpectrum[i], 1e-10));
            double db1 = 20.0 * Math.Log10(Math.Max(avgSpectrum[i + 1], 1e-10));
            double db2 = 20.0 * Math.Log10(Math.Max(avgSpectrum[i + 2], 1e-10));
            double db3 = 20.0 * Math.Log10(Math.Max(avgSpectrum[i + 3], 1e-10));

            if (db0 > -60 && db1 < db0 - 3 && db2 > db1 + 2 && db3 < db2 - 2)
                ringHits++;
        }
        bool hasRinging = ringHits > bins / 20;

        string verdict = "";
        if (hasAliasing) verdict += "Aliasing artifacts detected (possible bad resampling). ";
        if (hasRinging) verdict += "Ringing artifacts detected (steep filter). ";
        if (!hasAliasing && !hasRinging) verdict = "Clean — no resampling artifacts detected.";

        return new ResamplingResult(hasAliasing, hasRinging, verdict.Trim());
    }
}

public record ResamplingResult(bool HasAliasing, bool HasRinging, string Verdict);
