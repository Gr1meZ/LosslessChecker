using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class CutoffDetector
{
    private const int FftSize = 4096;
    private const int HopSize = 2048;

    public (double cutoff, double cutoffSlope, double[] spectrum) DetectFull(
        float[] samples, int sampleRate)
    {
        var nyquist = sampleRate / 2.0;
        if (samples.Length < FftSize)
            return (nyquist, 0, Array.Empty<double>());

        var fft = new Fft(FftSize);
        var window = Window.Hann(FftSize);

        var frame = new float[FftSize];
        var real = new float[FftSize];
        var imag = new float[FftSize];

        int hfStartBin = (int)(10000.0 / nyquist * (FftSize / 2));

        var framePowers = new List<(int pos, double[] power, double hfEnergy)>();

        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            Array.Copy(samples, pos, frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) frame[i] *= window[i];
            Array.Copy(frame, real, FftSize);
            Array.Clear(imag, 0, FftSize);
            fft.Direct(real, imag);

            var power = new double[FftSize / 2];
            double hfEnergy = 0;
            for (int i = 0; i < FftSize / 2; i++)
            {
                power[i] = (double)real[i] * real[i] + (double)imag[i] * imag[i];
                if (i >= hfStartBin) hfEnergy += power[i];
            }
            framePowers.Add((pos, power, hfEnergy));
        }

        if (framePowers.Count == 0)
            return (nyquist, 0, Array.Empty<double>());

        var sortedByHf = framePowers.OrderByDescending(f => f.hfEnergy).ToList();
        int topCount = Math.Max(1, sortedByHf.Count / 6);
        var topFrames = sortedByHf.Take(topCount).ToList();

        var avgMagnitudes = new double[FftSize / 2];
        foreach (var (_, power, _) in topFrames)
            for (int i = 0; i < FftSize / 2; i++)
                avgMagnitudes[i] += power[i];

        for (int i = 0; i < avgMagnitudes.Length; i++)
            avgMagnitudes[i] = Math.Sqrt(avgMagnitudes[i] / topFrames.Count);

        var (cutoff, cutoffSlope) = FindCutoffByDerivative(avgMagnitudes, nyquist);
        return (cutoff, cutoffSlope, avgMagnitudes);
    }

    public double DetectCutoff(float[] samples, int sampleRate)
        => DetectFull(samples, sampleRate).cutoff;

    public (double cutoff, double[] spectrum) DetectWithSpectrum(float[] samples, int sampleRate)
    {
        var r = DetectFull(samples, sampleRate);
        return (r.cutoff, r.spectrum);
    }

    // === NEW: Derivative-based cutoff detection ===
    // Instead of a fixed -60 dB amplitude threshold (which fails on quiet tracks
    // and is fooled by dither noise), we search for the STEEPEST NEGATIVE SLOPE
    // in the dB spectrum. A brickwall encoder creates a 30-40 dB drop over a few
    // bins → large negative derivative. Natural rolloff is gentle (−3 to −8 dB/oct).
    //
    // Algorithm:
    //   1. Convert averaged spectrum to dB (ref=peak in low band)
    //   2. Smooth the dB array (3-bin moving average to reduce FFT ripple)
    //   3. Compute sliding window slope (dB per octave) at each bin
    //   4. Find bin with the most negative slope in upper 2/3 of spectrum
    //   5. If slope < −18 dB/oct → brickwall at that bin → cutoff
    //   6. If slope >= −10 dB/oct → natural rolloff → return Nyquist (no penalizable cutoff)
    //   7. If slope in [−18, −10] → mild filtering, use that bin as cutoff

    private static (double cutoff, double cutoffSlope) FindCutoffByDerivative(
        double[] avgMagnitudes, double nyquist)
    {
        int bins = avgMagnitudes.Length;
        if (bins < 10) return (nyquist, 0);

        // 1. Find peak in low band for dB reference
        double peakMag = 0;
        for (int i = 0; i < bins / 6; i++)
            peakMag = Math.Max(peakMag, avgMagnitudes[i]);
        if (peakMag <= 0) return (nyquist, 0);

        // 2. Convert to dB spectrum
        var spectrumDb = new double[bins];
        for (int i = 0; i < bins; i++)
            spectrumDb[i] = 20.0 * Math.Log10(Math.Max(avgMagnitudes[i], 1e-10) / peakMag);

        // 3. Smooth with 5-bin moving average
        var smoothed = new double[bins];
        for (int i = 0; i < bins; i++)
        {
            double sum = 0; int count = 0;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(bins - 1, i + 2); j++)
            { sum += spectrumDb[j]; count++; }
            smoothed[i] = sum / count;
        }

        // 4. Compute sliding slope in dB/octave across a 30-bin window.
        // Scan left-to-right; weight slopes by normalized frequency so that
        // brickwalls near Nyquist (codec cutoff) are preferred over steeper
        // but lower-frequency internal mix filters (e.g. synth LP at 8 kHz).
        const int windowBins = 30;
        double freqPerBin = nyquist / bins;
        double bestWeightedSlope = 0; // weighted: more negative = better
        int bestBin = bins - 1;
        double bestRawSlope = 0;

        int searchStart = bins / 3;

        for (int center = searchStart; center < bins - windowBins / 2; center++)
        {
            int wStart = Math.Max(1, center - windowBins / 2);
            int wEnd = Math.Min(bins - 1, center + windowBins / 2);
            if (wEnd - wStart < 10) continue;

            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            int n = 0;
            for (int i = wStart; i <= wEnd; i++)
            {
                double freq = i * freqPerBin;
                if (freq < 1) continue;
                double x = Math.Log2(freq / 1000.0);
                double y = smoothed[i];
                sumX += x; sumY += y; sumXY += x * y; sumX2 += x * x; n++;
            }

            if (n < 5) continue;
            double denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-10) continue;
            double slope = (n * sumXY - sumX * sumY) / denom;

            // Weight: slopes at higher frequencies get a bonus (more negative weight).
            // A -20 dB/oct at 20 kHz beats a -25 dB/oct at 8 kHz.
            double freqWeight = (double)center / bins;
            double weightedSlope = slope * (0.5 + 0.5 * freqWeight);

            if (weightedSlope < bestWeightedSlope)
            {
                bestWeightedSlope = weightedSlope;
                bestRawSlope = slope;
                bestBin = center;
            }
        }

        bool isBrickwall = bestRawSlope < -18;
        if (isBrickwall && bestBin > 0 && bestBin < bins - 1)
        {
            int beforeStart = Math.Max(1, bestBin - 40);
            int afterEnd = Math.Min(bins - 1, bestBin + 40);
            double beforeAvg = 0, afterAvg = 0;
            int beforeCount = 0, afterCount = 0;
            for (int i = beforeStart; i < bestBin; i++) { beforeAvg += smoothed[i]; beforeCount++; }
            for (int i = bestBin + 1; i <= afterEnd; i++) { afterAvg += smoothed[i]; afterCount++; }
            beforeAvg = beforeCount > 0 ? beforeAvg / beforeCount : -100;
            afterAvg = afterCount > 0 ? afterAvg / afterCount : -100;

            if (beforeAvg - afterAvg < 12 || afterAvg > beforeAvg - 12)
                isBrickwall = false;
        }

        double cutoffHz;
        if (isBrickwall)
        {
            cutoffHz = SubBinFrequency(bestBin, bins, nyquist, smoothed);
        }
        else if (bestRawSlope < -10)
        {
            if (bestRawSlope < -18) bestRawSlope = -14;
            cutoffHz = SubBinFrequency(bestBin, bins, nyquist, smoothed);
        }
        else
        {
            cutoffHz = nyquist;
            bestRawSlope = 0;
        }

        return (cutoffHz, Math.Round(bestRawSlope, 2));
    }

    private static double SubBinFrequency(int bestBin, int bins, double nyquist, double[] smoothed)
    {
        double binFreq = (double)bestBin / bins * nyquist;
        if (bestBin <= 0 || bestBin >= smoothed.Length - 1)
            return binFreq;

        double y0 = smoothed[bestBin - 1];
        double y1 = smoothed[bestBin];
        double y2 = smoothed[bestBin + 1];

        double denom = y0 - 2 * y1 + y2;
        if (Math.Abs(denom) < 1e-10)
            return binFreq;

        double delta = 0.5 * (y0 - y2) / denom;
        delta = Math.Clamp(delta, -0.5, 0.5);
        return (bestBin + delta) / bins * nyquist;
    }

    public (string encoderMatch, string shelfType) ClassifyCutoff(
        double cutoffHz, double cutoffSlope, int sampleRate)
    {
        var nyquist = sampleRate / 2.0;
        double ratio = nyquist > 0 ? cutoffHz / nyquist : 1.0;
        bool isHiRes = sampleRate >= 88200;

        // Shelf type from slope
        string shelfType = cutoffSlope switch
        {
            < -18 => "Brickwall",
            < -10 => "Filtered",
            _ => "Natural"
        };

        // For Hi-Res files: if cutoff is above CD Nyquist (22.05kHz),
        // this is a legitimate Hi-Res recording, not a codec artifact.
        // Codec encoder labels (MP3 128-192, etc.) only apply below 22kHz.
        string encoderMatch;
        if (isHiRes && cutoffHz > 22100)
        {
            encoderMatch = "None (Hi-Res)";
        }
        else
        {
            encoderMatch = cutoffHz switch
            {
                <= 16500 => "MP3 128-192 kbps",
                <= 18500 => "MP3 192-256 kbps",
                <= 20000 => "MP3 320 / AAC 256 kbps",
                <= 21500 => "Possible LP filter",
                _ => "None"
            };
        }

        // Override: if ratio > 0.95, encoder match is None regardless
        if (!isHiRes && ratio >= 0.95)
            encoderMatch = "None";

        // Override: if cutoff > 90% Nyquist, this is the ADC's anti-aliasing filter,
        // not a lossy codec brickwall. Don't penalize as Brickwall.
        if (ratio >= 0.90 && shelfType == "Brickwall")
            shelfType = "Natural";

        return (encoderMatch, shelfType);
    }

    public bool IsFakeHiRes(double cutoffHz, string shelfType, int sampleRate)
    {
        if (sampleRate < 88200) return false;
        // Fake Hi-Res = brickwall at known upscale frequencies:
        //   16-17 kHz: MP3 128 kbps upscaled to Hi-Res
        //   18-20 kHz: MP3 192-320 / AAC upscaled
        //   20-22.1 kHz: CD (44.1k) upscaled to Hi-Res
        // Real acoustic recordings may have natural rolloff at any frequency,
        // but they won't have a brickwall shelf at these exact points.
        return shelfType == "Brickwall"
            && ((cutoffHz >= 15500 && cutoffHz <= 17000)
                || (cutoffHz >= 18000 && cutoffHz <= 20000)
                || (cutoffHz >= 20000 && cutoffHz <= 22100));
    }
}
