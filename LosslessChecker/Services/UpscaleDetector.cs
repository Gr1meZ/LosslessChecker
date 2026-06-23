namespace LosslessChecker.Services;

public class UpscaleDetector
{
    public (bool isUpscale, string verdict, double maxHfDb) Detect(
        double[] averagedSpectrum, int sampleRate)
    {
        int nyquist = sampleRate / 2;
        if (nyquist <= 22050)
            return (false, "Standard sample rate, no upscale check needed.", 0);

        // Find HF content above CD Nyquist (22.05 kHz)
        int binsAbove22k = (int)((nyquist - 22050.0) / nyquist * averagedSpectrum.Length);
        int startBin = averagedSpectrum.Length - binsAbove22k;
        if (startBin >= averagedSpectrum.Length || startBin < 1)
            return (false, "Insufficient bins for HF analysis.", 0);

        double peakBelow = 0;
        for (int i = 0; i < startBin / 2; i++)
            peakBelow = Math.Max(peakBelow, averagedSpectrum[i]);

        double maxHf = 0;
        for (int i = startBin; i < averagedSpectrum.Length; i++)
            maxHf = Math.Max(maxHf, averagedSpectrum[i]);

        if (peakBelow <= 0)
            return (false, "No reference signal.", 0);

        double maxHfDb = 20.0 * Math.Log10(Math.Max(maxHf, 1e-10) / peakBelow);

        if (maxHfDb < -50)
        {
            bool isDither = HasDitherSignature(averagedSpectrum, startBin);
            return (true,
                isDither
                    ? $"Hi-Res ({sampleRate}Hz) but no content above 22kHz (max {maxHfDb:F0} dB). Flat dither noise — upscale from CD."
                    : $"Hi-Res ({sampleRate}Hz) but no content above 22kHz (max {maxHfDb:F0} dB). Likely upscale from 44.1/48kHz source.",
                maxHfDb);
        }

        if (maxHfDb < -30)
        {
            return (true,
                $"Weak HF content above 22kHz ({maxHfDb:F0} dB). Questionable Hi-Res authenticity.",
                maxHfDb);
        }

        return (false, $"Valid Hi-Res content detected above 22kHz ({maxHfDb:F0} dB).", maxHfDb);
    }

    private static bool HasDitherSignature(double[] spectrum, int startBin)
    {
        if (startBin + 20 >= spectrum.Length) return false;

        double sum = 0, sumSq = 0;
        int count = 0;
        for (int i = startBin; i < spectrum.Length; i++)
        {
            double db = 20.0 * Math.Log10(Math.Max(spectrum[i], 1e-10));
            sum += db;
            sumSq += db * db;
            count++;
        }

        if (count < 10) return false;
        double mean = sum / count;
        double variance = sumSq / count - mean * mean;

        return variance < 9.0;
    }
}
