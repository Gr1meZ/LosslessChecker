using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class CutoffDetector
{
    private const int FftSize = 4096;
    private const int HopSize = 2048;
    private const int SpectroFreqBins = 512;
    private const int SpectroMaxFrames = 600;

    public (double cutoff, double cutoffSlope, double[] spectrum, double[][] spectrogram) DetectFull(
        float[] samples, int sampleRate)
    {
        if (samples.Length < FftSize)
            return (sampleRate / 2.0, 0, Array.Empty<double>(), Array.Empty<double[]>());

        var nyquist = sampleRate / 2.0;
        var fft = new Fft(FftSize);
        var window = Window.Hann(FftSize);

        var avgMagnitudes = new double[FftSize / 2];
        int frameCount = 0;

        var spectroFrames = new List<double[]>();
        int spectroStep = Math.Max(1, (samples.Length - FftSize) / HopSize / SpectroMaxFrames);
        int spectroCounter = 0;

        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            var frame = new float[FftSize];
            Array.Copy(samples, pos, frame, 0, FftSize);

            for (int i = 0; i < FftSize; i++)
                frame[i] *= window[i];

            var real = new float[FftSize];
            var imag = new float[FftSize];
            Array.Copy(frame, real, FftSize);

            fft.Direct(real, imag);

            var mags = new double[FftSize / 2];
            for (int i = 0; i < FftSize / 2; i++)
            {
                var mag = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
                avgMagnitudes[i] += mag;
                mags[i] = mag;
            }

            frameCount++;

            spectroCounter++;
            if (spectroCounter % spectroStep == 0 && spectroFrames.Count < SpectroMaxFrames)
            {
                var downsampled = new double[SpectroFreqBins];
                double ratio = (double)mags.Length / SpectroFreqBins;
                for (int j = 0; j < SpectroFreqBins; j++)
                {
                    int srcIdx = (int)(j * ratio);
                    downsampled[j] = mags[Math.Min(srcIdx, mags.Length - 1)];
                }
                spectroFrames.Add(downsampled);
            }
        }

        if (frameCount == 0)
            return (nyquist, 0, Array.Empty<double>(), Array.Empty<double[]>());

        for (int i = 0; i < avgMagnitudes.Length; i++)
            avgMagnitudes[i] /= frameCount;

        var (cutoff, cutoffSlope) = FindCutoff(avgMagnitudes, nyquist, sampleRate);

        return (cutoff, cutoffSlope, avgMagnitudes, spectroFrames.ToArray());
    }

    public double DetectCutoff(float[] samples, int sampleRate)
    {
        var (cutoff, _, _, _) = DetectFull(samples, sampleRate);
        return cutoff;
    }

    public (double cutoff, double[] spectrum) DetectWithSpectrum(float[] samples, int sampleRate)
    {
        var (cutoff, _, spectrum, _) = DetectFull(samples, sampleRate);
        return (cutoff, spectrum);
    }

    private static (double cutoff, double cutoffSlope) FindCutoff(
        double[] avgMagnitudes, double nyquist, int sampleRate)
    {
        // Find reference peak in low frequencies (0-4 kHz)
        int lowBandEnd = avgMagnitudes.Length / 6;
        double peakMag = 0;
        for (int i = 0; i < lowBandEnd; i++)
            peakMag = Math.Max(peakMag, avgMagnitudes[i]);

        if (peakMag <= 0)
            return (nyquist, 0);

        // Fixed relative threshold: signal must be within -60 dB (standard) or -65 dB (Hi-Res) of peak
        double thresholdDb = sampleRate >= 88200 ? -65.0 : -60.0;
        double thresholdMag = peakMag * Math.Pow(10, thresholdDb / 20.0);

        // Search from Nyquist down through upper 2/3 of spectrum
        int startBin = avgMagnitudes.Length / 3;
        int cutoffBin = avgMagnitudes.Length - 1;
        for (int bin = avgMagnitudes.Length - 1; bin >= startBin; bin--)
        {
            if (avgMagnitudes[bin] > thresholdMag)
            {
                cutoffBin = bin;
                break;
            }
        }

        double cutoff = (double)cutoffBin / avgMagnitudes.Length * nyquist;

        // Measure slope around cutoff in dB/octave for forensic classification
        double cutoffSlope = MeasureCutoffSlope(avgMagnitudes, peakMag, cutoffBin, nyquist);

        return (cutoff, cutoffSlope);
    }

    private static double MeasureCutoffSlope(
        double[] avgMagnitudes, double peakMag, int cutoffBin, double nyquist)
    {
        // Measure spectral slope around cutoff: take bins ±15 around cutoff
        int slopeStart = Math.Max(1, cutoffBin - 15);
        int slopeEnd = Math.Min(cutoffBin + 15, avgMagnitudes.Length - 1);

        if (slopeEnd - slopeStart < 5)
            return 0;

        double freqPerBin = nyquist / avgMagnitudes.Length;

        // Linear regression: dB vs log(frequency) → slope in dB/octave
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        int n = 0;
        for (int bin = slopeStart; bin < slopeEnd; bin++)
        {
            double freq = bin * freqPerBin;
            if (freq < 1) continue;
            double x = Math.Log2(freq / 1000.0); // log2 of kHz
            double mag = Math.Max(avgMagnitudes[bin], 1e-10);
            double y = 20.0 * Math.Log10(mag / peakMag);
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
            n++;
        }

        if (n < 3 || Math.Abs(n * sumX2 - sumX * sumX) < 1e-10)
            return 0;

        double slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
        // Negative slope means energy decreases with frequency.
        // -3 to -6 dB/octave = natural rolloff
        // -12 to -24 dB/octave = steep filter
        // < -30 dB/octave = brickwall (lossy codec)
        return slope;
    }
}
