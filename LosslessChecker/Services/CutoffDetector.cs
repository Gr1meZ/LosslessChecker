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
        var avgMagnitudes = new double[FftSize / 2];
        int frameCount = 0;

        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            var frame = new float[FftSize];
            Array.Copy(samples, pos, frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) frame[i] *= window[i];
            var real = new float[FftSize];
            var imag = new float[FftSize];
            Array.Copy(frame, real, FftSize);
            fft.Direct(real, imag);
            for (int i = 0; i < FftSize / 2; i++)
                avgMagnitudes[i] += Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
            frameCount++;
        }

        if (frameCount == 0)
            return (nyquist, 0, Array.Empty<double>());

        for (int i = 0; i < avgMagnitudes.Length; i++)
            avgMagnitudes[i] /= frameCount;

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

        // 4. Compute sliding slope in dB/octave across a 30-bin window
        // (30 bins at 44.1kHz ≈ 323 Hz — wide enough to catch brickwall, narrow enough for precision)
        const int windowBins = 30;
        double freqPerBin = nyquist / bins;
        double bestSlope = 0;
        int bestBin = bins - 1;

        int searchStart = bins / 3; // Skip lowest 1/3 (not relevant for cutoff)

        for (int center = searchStart; center < bins - windowBins / 2; center++)
        {
            int wStart = Math.Max(1, center - windowBins / 2);
            int wEnd = Math.Min(bins - 1, center + windowBins / 2);
            if (wEnd - wStart < 10) continue;

            // Linear regression: dB vs log2(freq)
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            int n = 0;
            for (int i = wStart; i <= wEnd; i++)
            {
                double freq = i * freqPerBin;
                if (freq < 1) continue;
                double x = Math.Log2(freq / 1000.0); // log2(kHz)
                double y = smoothed[i];
                sumX += x; sumY += y; sumXY += x * y; sumX2 += x * x; n++;
            }

            if (n < 5) continue;
            double denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-10) continue;
            double slope = (n * sumXY - sumX * sumY) / denom;

            // We want the MOST NEGATIVE slope (steepest drop)
            if (slope < bestSlope)
            {
                bestSlope = slope;
                bestBin = center;
            }
        }

        // 5. Classify based on slope
        double cutoffHz;
        if (bestSlope < -18)
        {
            // Brickwall: MP3/AAC encoder hard low-pass
            cutoffHz = (double)bestBin / bins * nyquist;
        }
        else if (bestSlope < -10)
        {
            // Mild filtering: could be gentle anti-aliasing or 320kbps MP3
            cutoffHz = (double)bestBin / bins * nyquist;
        }
        else
        {
            // Natural rolloff: no encoder cutoff detected → return Nyquist
            cutoffHz = nyquist;
            bestSlope = 0; // Don't report slope if no cutoff found
        }

        return (cutoffHz, Math.Round(bestSlope, 2));
    }

    public (string encoderMatch, string shelfType) ClassifyCutoff(
        double cutoffHz, double cutoffSlope, int sampleRate)
    {
        var nyquist = sampleRate / 2.0;
        double ratio = nyquist > 0 ? cutoffHz / nyquist : 1.0;

        // Shelf type from slope
        string shelfType = cutoffSlope switch
        {
            < -18 => "Brickwall",
            < -10 => "Filtered",
            _ => "Natural"
        };

        // Encoder mapping (absolute cutoff, not ratio)
        string encoderMatch = cutoffHz switch
        {
            <= 16500 => "MP3 128-192 kbps",
            <= 18500 => "MP3 192-256 kbps",
            <= 20000 => "MP3 320 / AAC 256 kbps",
            <= 21500 => "Possible LP filter",
            _ => "None"
        };

        // Override: if ratio > 0.95, encoder match is None regardless
        if (ratio >= 0.95)
            encoderMatch = "None";

        return (encoderMatch, shelfType);
    }

    public bool IsFakeHiRes(double cutoffHz, int sampleRate)
    {
        // Hi-Res = sample rate >= 88.2 kHz
        if (sampleRate < 80000) return false;
        // If cutoff is below 22 kHz on a Hi-Res file, it's a fake upscale
        return cutoffHz < 22000;
    }
}
