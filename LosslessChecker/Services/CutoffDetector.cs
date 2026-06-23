using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class CutoffDetector
{
    private const int FftSize = 4096;
    private const int HopSize = 2048;
    private const double MagnitudeThresholdDb = -60.0;
    private const double HighFreqSearchStartRatio = 0.4;

    public (double cutoff, double[] spectrum) DetectWithSpectrum(float[] samples, int sampleRate)
    {
        if (samples.Length < FftSize)
            return (sampleRate / 2.0, Array.Empty<double>());

        var nyquist = sampleRate / 2.0;
        var fft = new Fft(FftSize);
        var window = Window.Hann(FftSize);

        var avgMagnitudes = new double[FftSize / 2];
        int frameCount = 0;

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

            for (int i = 0; i < FftSize / 2; i++)
            {
                var mag = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
                avgMagnitudes[i] += mag;
            }

            frameCount++;
        }

        if (frameCount == 0)
            return (nyquist, Array.Empty<double>());

        for (int i = 0; i < avgMagnitudes.Length; i++)
            avgMagnitudes[i] /= frameCount;

        var peakMag = avgMagnitudes.Take(avgMagnitudes.Length / 8).Max();
        if (peakMag <= 0)
            return (nyquist, avgMagnitudes);

        var thresholdMag = peakMag * Math.Pow(10, MagnitudeThresholdDb / 20.0);
        var startBin = (int)(avgMagnitudes.Length * HighFreqSearchStartRatio);

        double cutoff = nyquist;
        for (int bin = avgMagnitudes.Length - 1; bin >= startBin; bin--)
        {
            if (avgMagnitudes[bin] > thresholdMag)
            {
                cutoff = (double)bin / avgMagnitudes.Length * nyquist;
                break;
            }
        }

        return (cutoff, avgMagnitudes);
    }

    public double DetectCutoff(float[] samples, int sampleRate)
    {
        var (cutoff, _) = DetectWithSpectrum(samples, sampleRate);
        return cutoff;
    }
}
