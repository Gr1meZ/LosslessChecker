using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class ArtifactDetector
{
    private const int FftSize = 4096;
    private const int HopSize = 1024;

    public (bool hasArtifacts, string level) Detect(float[] samples, int sampleRate, double cutoffFrequency)
    {
        if (samples.Length < FftSize * 2)
            return (false, "None");

        var nyquist = sampleRate / 2.0;
        var cutoffBin = (int)(cutoffFrequency / nyquist * (FftSize / 2));
        cutoffBin = Math.Max(1, Math.Min(cutoffBin, FftSize / 2 - 1));

        var fft = new Fft(FftSize);
        var window = Window.Hann(FftSize);

        double totalSpectralFlatness = 0;
        double totalTransitionSharpness = 0;
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

            var mags = new double[FftSize / 2];
            for (int i = 0; i < FftSize / 2; i++)
                mags[i] = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

            int aboveStart = cutoffBin;
            int aboveEnd = Math.Min(cutoffBin + 20, mags.Length - 1);
            if (aboveEnd > aboveStart)
            {
                double geomMean = 0, arithMean = 0;
                int count = 0;
                for (int i = aboveStart; i < aboveEnd; i++)
                {
                    var v = Math.Max(mags[i], 1e-10);
                    geomMean += Math.Log(v);
                    arithMean += v;
                    count++;
                }
                if (count > 0 && arithMean > 0)
                {
                    geomMean = Math.Exp(geomMean / count);
                    arithMean /= count;
                    totalSpectralFlatness += geomMean / arithMean;
                }
            }

            if (cutoffBin >= 10 && cutoffBin < mags.Length - 10)
            {
                double before = 0, after = 0;
                for (int i = cutoffBin - 5; i < cutoffBin; i++)
                    before += mags[i];
                for (int i = cutoffBin; i < cutoffBin + 5; i++)
                    after += mags[i];
                before /= 5;
                after /= 5;
                if (before > 0)
                    totalTransitionSharpness += after / before;
            }

            frameCount++;
        }

        if (frameCount == 0)
            return (false, "None");

        double avgFlatness = totalSpectralFlatness / frameCount;
        double avgTransition = totalTransitionSharpness / frameCount;

        if (avgFlatness > 0.5 && avgTransition < 0.3)
            return (true, "Strong");
        if (avgFlatness > 0.3 && avgTransition < 0.5)
            return (true, "Medium");
        if (avgFlatness > 0.15 || avgTransition < 0.7)
            return (true, "Weak");

        return (false, "None");
    }
}
