using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class CutoffDetector
{
    private const int FftSize = 4096;
    private const int HopSize = 2048;
    private const int SpectroFreqBins = 512;
    private const int SpectroMaxFrames = 600;

    public (double cutoff, double[] spectrumAvg, double[][] spectrogram) DetectFull(
        float[] samples, int sampleRate)
    {
        if (samples.Length < FftSize)
            return (sampleRate / 2.0, Array.Empty<double>(), Array.Empty<double[]>());

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
            return (nyquist, Array.Empty<double>(), Array.Empty<double[]>());

        for (int i = 0; i < avgMagnitudes.Length; i++)
            avgMagnitudes[i] /= frameCount;

        double cutoff = FindCutoff(avgMagnitudes, nyquist);

        return (cutoff, avgMagnitudes, spectroFrames.ToArray());
    }

    public double DetectCutoff(float[] samples, int sampleRate)
    {
        var (cutoff, _, _) = DetectFull(samples, sampleRate);
        return cutoff;
    }

    public (double cutoff, double[] spectrum) DetectWithSpectrum(float[] samples, int sampleRate)
    {
        var (cutoff, spectrum, _) = DetectFull(samples, sampleRate);
        return (cutoff, spectrum);
    }

    private static double FindCutoff(double[] avgMagnitudes, double nyquist)
    {
        // Find adaptive threshold: use signal-to-noise ratio approach
        // Take peak in low frequencies as reference level
        int lowBandEnd = avgMagnitudes.Length / 6;
        var lowBand = avgMagnitudes.AsSpan(0, lowBandEnd);
        double peakMag = 0;
        for (int i = 0; i < lowBand.Length; i++)
            peakMag = Math.Max(peakMag, lowBand[i]);

        if (peakMag <= 0)
            return nyquist;

        // Estimate noise floor from the quietest 20% of high-frequency bins
        int highStart = avgMagnitudes.Length / 3;
        var highBandMags = new List<double>();
        for (int i = highStart; i < avgMagnitudes.Length; i++)
            if (avgMagnitudes[i] > 1e-10)
                highBandMags.Add(avgMagnitudes[i]);

        if (highBandMags.Count < 10)
            return nyquist;

        highBandMags.Sort();
        int noiseCount = Math.Max(1, highBandMags.Count / 5);
        double noiseFloor = 0;
        for (int i = 0; i < noiseCount; i++)
            noiseFloor += highBandMags[i];
        noiseFloor /= noiseCount;

        // Threshold: 12 dB above noise floor, or -48 dB from peak, whichever is higher
        double thresholdDb = Math.Max(-48.0, 20.0 * Math.Log10(Math.Max(noiseFloor, 1e-10) / peakMag) + 12.0);
        double thresholdMag = peakMag * Math.Pow(10, thresholdDb / 20.0);

        // Search from Nyquist downwards
        int startBin = avgMagnitudes.Length / 3;
        for (int bin = avgMagnitudes.Length - 1; bin >= startBin; bin--)
        {
            if (avgMagnitudes[bin] > thresholdMag)
                return (double)bin / avgMagnitudes.Length * nyquist;
        }

        return nyquist;
    }
}
