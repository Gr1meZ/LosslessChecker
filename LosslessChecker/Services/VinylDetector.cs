namespace LosslessChecker.Services;

public class VinylDetector
{
    public VinylResult Detect(double[] spectrum, int sampleRate, float[] samples)
    {
        int bins = spectrum.Length;
        double nyquist = sampleRate / 2.0;

        // Rumble: 8–15 Hz, characteristic of turntable motor noise
        double rumbleEnergy = 0;
        int bin8Hz = (int)(8.0 / nyquist * bins);
        int bin15Hz = (int)(15.0 / nyquist * bins);
        for (int i = bin8Hz; i <= bin15Hz && i < bins; i++)
            rumbleEnergy += spectrum[i];
        double avgRumble = bin15Hz > bin8Hz ? rumbleEnergy / (bin15Hz - bin8Hz + 1) : 0;

        // Reference: mid-band energy for normalization
        double midEnergy = 0;
        int midStart = (int)(500.0 / nyquist * bins);
        int midEnd = (int)(2000.0 / nyquist * bins);
        for (int i = midStart; i <= midEnd && i < bins; i++)
            midEnergy += spectrum[i];
        double avgMid = midEnd > midStart ? midEnergy / (midEnd - midStart + 1) : 1e-10;

        double rumbleRatio = avgRumble / avgMid;

        // HF noise shelf above 22 kHz — typical of vinyl ultrasonic noise
        double hfEnergy = 0;
        int hfStart = (int)(22050.0 / nyquist * bins);
        int hfEnd = bins - 1;
        int hfCount = 0;
        for (int i = hfStart; i <= hfEnd; i++)
        {
            hfEnergy += spectrum[i];
            hfCount++;
        }
        double avgHf = hfCount > 0 ? hfEnergy / hfCount : 0;
        double hfRatio = avgHf / avgMid;

        // Vinyl signature: elevated rumble + HF noise shelf (not a clean drop)
        // At 44.1kHz, HF detection is unreliable (Nyquist = 22.05kHz). Require
        // stronger rumble ratio and check for 50/60 Hz power line hum.
        bool isVinyl;
        if (sampleRate < 88200)
        {
            double humEnergy = 0;
            int bin50Hz = (int)(50.0 / nyquist * bins);
            int bin60Hz = (int)(60.0 / nyquist * bins);
            int humRange = Math.Max(1, (int)(5.0 / nyquist * bins));
            for (int i = Math.Max(0, bin50Hz - humRange); i <= Math.Min(bins - 1, bin60Hz + humRange); i++)
                humEnergy += spectrum[i];
            double avgHum = humEnergy / (Math.Min(bins - 1, bin60Hz + humRange) - Math.Max(0, bin50Hz - humRange) + 1);
            double humRatio = avgHum / avgMid;

            isVinyl = rumbleRatio > 4.0 && humRatio > 1.5;
        }
        else
        {
            isVinyl = rumbleRatio > 2.0 && hfRatio > 0.01;
        }

        if (isVinyl)
        {
            double hfMinDb = hfCount > 0
                ? 20.0 * Math.Log10(Math.Max(spectrum[hfStart..].Min(), 1e-10) / (avgMid > 1e-10 ? avgMid : 1e-10))
                : -200;
            if (hfMinDb < -90 && rumbleRatio < 0.5)
                isVinyl = false;
        }

        return new VinylResult(isVinyl, Math.Round(rumbleRatio, 2), Math.Round(hfRatio, 4));
    }
}

public record VinylResult(bool IsVinylRip, double RumbleRatio, double HfNoiseRatio);
