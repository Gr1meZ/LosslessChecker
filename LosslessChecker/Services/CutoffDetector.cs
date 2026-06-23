using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class CutoffDetector
{
    private const int FftSize = 4096;
    private const int HopSize = 2048;
    private const int SpectroFreqBins = 256;
    private const int SpectroMaxFrames = 300;

    public (double cutoff, double cutoffSlope, double[] spectrum, byte[] spectrogram, int sw, int sh) DetectFull(
        float[] samples, int sampleRate)
    {
        int height = SpectroFreqBins;
        if (samples.Length < FftSize)
            return (sampleRate / 2.0, 0, Array.Empty<double>(), Array.Empty<byte>(), 0, height);

        var nyquist = sampleRate / 2.0;
        var fft = new Fft(FftSize);
        var window = Window.Hann(FftSize);
        var avgMagnitudes = new double[FftSize / 2];
        int frameCount = 0;

        int spectroStep = Math.Max(1, (samples.Length - FftSize) / HopSize / SpectroMaxFrames);
        int spectroCounter = 0;
        double globalPeakMag = 0;

        // Pass 1: average spectrum + find global peak
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
            {
                var mag = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
                avgMagnitudes[i] += mag;
            }
            frameCount++;
            spectroCounter++;
            if (spectroCounter % spectroStep == 0)
                for (int j = 0; j < FftSize / 2; j++)
                {
                    double m = Math.Sqrt(real[j] * real[j] + imag[j] * imag[j]);
                    if (m > globalPeakMag) globalPeakMag = m;
                }
        }

        if (frameCount == 0)
            return (nyquist, 0, Array.Empty<double>(), Array.Empty<byte>(), 0, height);

        for (int i = 0; i < avgMagnitudes.Length; i++)
            avgMagnitudes[i] /= frameCount;

        // Pass 2: build spectrogram
        spectroCounter = 0;
        int framesBuilt = 0;
        int width = Math.Min(SpectroMaxFrames, ((samples.Length - FftSize) / HopSize) / spectroStep + 1);
        var flat = new byte[width * height];

        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            var frame = new float[FftSize];
            Array.Copy(samples, pos, frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) frame[i] *= window[i];
            var real = new float[FftSize];
            var imag = new float[FftSize];
            Array.Copy(frame, real, FftSize);
            fft.Direct(real, imag);
            spectroCounter++;
            if (spectroCounter % spectroStep == 0 && framesBuilt < width)
            {
                double ratio = (double)(FftSize / 2) / height;
                double refMag = Math.Max(globalPeakMag, 1e-10);
                int offset = framesBuilt * height;
                for (int j = 0; j < height; j++)
                {
                    int srcIdx = Math.Min((int)(j * ratio), FftSize / 2 - 1);
                    double mag = Math.Sqrt(real[srcIdx] * real[srcIdx] + imag[srcIdx] * imag[srcIdx]);
                    double db = 20.0 * Math.Log10(Math.Max(mag, 1e-10) / refMag);
                    flat[offset + j] = (byte)Math.Max(0, Math.Min(255, (int)((db + 96.0) / 96.0 * 255)));
                }
                framesBuilt++;
            }
        }

        var (cutoff, cutoffSlope) = FindCutoffByDerivative(avgMagnitudes, nyquist);
        return (cutoff, cutoffSlope, avgMagnitudes, flat, framesBuilt, height);
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
}
