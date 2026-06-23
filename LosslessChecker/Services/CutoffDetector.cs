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
            {
                for (int j = 0; j < FftSize / 2; j++)
                {
                    double m = Math.Sqrt(real[j] * real[j] + imag[j] * imag[j]);
                    if (m > globalPeakMag) globalPeakMag = m;
                }
            }
        }

        if (frameCount == 0)
            return (nyquist, 0, Array.Empty<double>(), Array.Empty<byte>(), 0, height);

        for (int i = 0; i < avgMagnitudes.Length; i++)
            avgMagnitudes[i] /= frameCount;

        // Pass 2: build flat byte[] spectrogram
        spectroCounter = 0;
        int framesBuilt = 0;
        int width = Math.Min(SpectroMaxFrames,
            ((samples.Length - FftSize) / HopSize) / spectroStep + 1);
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
                    int val = (int)((db + 96.0) / 96.0 * 255);
                    flat[offset + j] = (byte)Math.Max(0, Math.Min(255, val));
                }
                framesBuilt++;
            }
        }

        var (cutoff, cutoffSlope) = FindCutoff(avgMagnitudes, nyquist, sampleRate);
        return (cutoff, cutoffSlope, avgMagnitudes, flat, framesBuilt, height);
    }

    public double DetectCutoff(float[] samples, int sampleRate)
    {
        var (cutoff, _, _, _, _, _) = DetectFull(samples, sampleRate);
        return cutoff;
    }

    public (double cutoff, double[] spectrum) DetectWithSpectrum(float[] samples, int sampleRate)
    {
        var (cutoff, _, spectrum, _, _, _) = DetectFull(samples, sampleRate);
        return (cutoff, spectrum);
    }

    private static (double cutoff, double cutoffSlope) FindCutoff(
        double[] avgMagnitudes, double nyquist, int sampleRate)
    {
        int lowBandEnd = avgMagnitudes.Length / 6;
        double peakMag = 0;
        for (int i = 0; i < lowBandEnd; i++)
            peakMag = Math.Max(peakMag, avgMagnitudes[i]);
        if (peakMag <= 0) return (nyquist, 0);

        double thresholdDb = sampleRate >= 88200 ? -65.0 : -60.0;
        double thresholdMag = peakMag * Math.Pow(10, thresholdDb / 20.0);

        int startBin = avgMagnitudes.Length / 3;
        int cutoffBin = avgMagnitudes.Length - 1;
        for (int bin = avgMagnitudes.Length - 1; bin >= startBin; bin--)
        {
            if (avgMagnitudes[bin] > thresholdMag) { cutoffBin = bin; break; }
        }

        double cutoff = (double)cutoffBin / avgMagnitudes.Length * nyquist;
        double cutoffSlope = MeasureCutoffSlope(avgMagnitudes, peakMag, cutoffBin, nyquist);
        return (cutoff, cutoffSlope);
    }

    private static double MeasureCutoffSlope(
        double[] avgMagnitudes, double peakMag, int cutoffBin, double nyquist)
    {
        int slopeStart = Math.Max(1, cutoffBin - 15);
        int slopeEnd = Math.Min(cutoffBin + 15, avgMagnitudes.Length - 1);
        if (slopeEnd - slopeStart < 5) return 0;

        double freqPerBin = nyquist / avgMagnitudes.Length;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        int n = 0;
        for (int bin = slopeStart; bin < slopeEnd; bin++)
        {
            double freq = bin * freqPerBin;
            if (freq < 1) continue;
            double x = Math.Log2(freq / 1000.0);
            double mag = Math.Max(avgMagnitudes[bin], 1e-10);
            double y = 20.0 * Math.Log10(mag / peakMag);
            sumX += x; sumY += y; sumXY += x * y; sumX2 += x * x; n++;
        }
        if (n < 3 || Math.Abs(n * sumX2 - sumX * sumX) < 1e-10) return 0;
        return (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
    }
}
