using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class ArtifactDetector
{
    private const int FftSize = 4096;
    private const int HopSize = 1024;

    public (bool hasArtifacts, string level, string artifactType) Detect(float[] samples, int sampleRate, double cutoffFrequency)
    {
        if (samples.Length < FftSize * 2)
            return (false, "None", "None");

        var nyquist = sampleRate / 2.0;
        var cutoffBin = (int)(cutoffFrequency / nyquist * (FftSize / 2));
        cutoffBin = Math.Max(1, Math.Min(cutoffBin, FftSize / 2 - 1));

        var fft = new Fft(FftSize);
        var window = Window.Hann(FftSize);

        double totalFlatness = 0;
        double totalSlope = 0;
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

            // Wider analysis band: 80 bins above cutoff (~864 Hz)
            int aboveStart = cutoffBin;
            int aboveEnd = Math.Min(cutoffBin + 80, mags.Length - 1);
            if (aboveEnd > aboveStart + 10)
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
                    totalFlatness += geomMean / arithMean;
                }
            }

            // Slope: linear regression over region around cutoff (±40 bins)
            int slopeStart = Math.Max(1, cutoffBin - 40);
            int slopeEnd = Math.Min(cutoffBin + 40, mags.Length - 1);
            if (slopeEnd - slopeStart > 10)
            {
                double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                int n = 0;
                for (int i = slopeStart; i < slopeEnd; i++)
                {
                    double x = i;
                    double y = Math.Max(mags[i], 1e-10);
                    double yDb = 20.0 * Math.Log10(y);
                    sumX += x;
                    sumY += yDb;
                    sumXY += x * yDb;
                    sumX2 += x * x;
                    n++;
                }
                double slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
                totalSlope += slope;
            }

            frameCount++;
        }

        if (frameCount == 0)
            return (false, "None", "None");

        double avgFlatness = totalFlatness / frameCount;
        double avgSlope = totalSlope / frameCount;

        // avgSlope is dB/bin. Negative = energy drops above cutoff.
        // Steep drop (avgSlope < -0.08 dB/bin) = brickwall = MP3 artifact
        // Flat (avgSlope > -0.02 dB/bin) = natural rolloff or noise

        bool hasArtifacts;
        string level;
        if (avgFlatness > 0.4 && avgSlope < -0.1)
            (hasArtifacts, level) = (true, "Strong");
        else if (avgFlatness > 0.25 && avgSlope < -0.06)
            (hasArtifacts, level) = (true, "Medium");
        else if (avgFlatness > 0.1 || avgSlope < -0.04)
            (hasArtifacts, level) = (true, "Weak");
        else
            (hasArtifacts, level) = (false, "None");

        string artifactType = level switch
        {
            "Strong" or "Medium" => DetectMp3Sizzle(samples, sampleRate, cutoffFrequency)
                ? "MP3" : "Unknown",
            "Weak" => "Unknown",
            _ => "None"
        };
        return (hasArtifacts, level, artifactType);
    }

    private static bool DetectMp3Sizzle(float[] samples, int sampleRate, double cutoffFreq)
    {
        if (sampleRate < 44100) return false;

        int fftSize = 4096;
        var fft = new Fft(fftSize);
        var window = Window.Hann(fftSize);
        double sizzleEnergy = 0;
        double totalHfEnergy = 0;
        int frames = 0;

        int bin15500 = (int)(15500.0 / (sampleRate / 2.0) * (fftSize / 2));
        int bin16500 = (int)(16500.0 / (sampleRate / 2.0) * (fftSize / 2));
        int cutoffBin = Math.Min((int)(cutoffFreq / (sampleRate / 2.0) * (fftSize / 2)), fftSize / 2 - 1);

        for (int pos = 0; pos + fftSize <= samples.Length; pos += 1024)
        {
            var frame = new float[fftSize];
            Array.Copy(samples, pos, frame, 0, fftSize);
            for (int i = 0; i < fftSize; i++) frame[i] *= window[i];
            var real = new float[fftSize];
            var imag = new float[fftSize];
            Array.Copy(frame, real, fftSize);
            fft.Direct(real, imag);

            for (int i = bin15500; i < bin16500 && i < real.Length / 2; i++)
                sizzleEnergy += real[i] * real[i] + imag[i] * imag[i];

            for (int i = bin15500; i < cutoffBin && i < real.Length / 2; i++)
                totalHfEnergy += real[i] * real[i] + imag[i] * imag[i];

            frames++;
            if (frames >= 20) break;
        }

        if (frames == 0 || totalHfEnergy <= 0) return false;
        double ratio = sizzleEnergy / totalHfEnergy;
        return ratio > 0.4;
    }
}
