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

        var frame = new float[FftSize];
        var real = new float[FftSize];
        var imag = new float[FftSize];

        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            Array.Copy(samples, pos, frame, 0, FftSize);

            for (int i = 0; i < FftSize; i++)
                frame[i] *= window[i];

            Array.Copy(frame, real, FftSize);
            Array.Clear(imag, 0, FftSize);

            fft.Direct(real, imag);

            var mags = new double[FftSize / 2];
            for (int i = 0; i < FftSize / 2; i++)
                mags[i] = MathF.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

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

        // If cutoff is near Nyquist (>95%), there's not enough HF spectrum
        // to reliably detect artifacts — skip to avoid false positives
        double cutoffRatio = cutoffFrequency / (sampleRate / 2.0);
        if (cutoffRatio > 0.95)
            return (false, "None", "None");

        // avgSlope is dB/bin. Negative = energy drops above cutoff.
        // Steep drop (avgSlope < -0.12 dB/bin) = brickwall = MP3 artifact
        // Flat (avgSlope > -0.02 dB/bin) = natural rolloff or noise

        bool hasArtifacts;
        string level;
        if (avgFlatness > 0.5 && avgSlope < -0.12)
            (hasArtifacts, level) = (true, "Strong");
        else if (avgFlatness > 0.35 && avgSlope < -0.08)
            (hasArtifacts, level) = (true, "Medium");
        else if (avgFlatness > 0.2 || avgSlope < -0.06)
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

        var sizzleFrame = new float[fftSize];
        var sizzleReal = new float[fftSize];
        var sizzleImag = new float[fftSize];

        for (int pos = 0; pos + fftSize <= samples.Length; pos += 1024)
        {
            Array.Copy(samples, pos, sizzleFrame, 0, fftSize);
            for (int i = 0; i < fftSize; i++) sizzleFrame[i] *= window[i];
            Array.Copy(sizzleFrame, sizzleReal, fftSize);
            Array.Clear(sizzleImag, 0, fftSize);
            fft.Direct(sizzleReal, sizzleImag);

            for (int i = bin15500; i < bin16500 && i < sizzleReal.Length / 2; i++)
                sizzleEnergy += sizzleReal[i] * sizzleReal[i] + sizzleImag[i] * sizzleImag[i];

            for (int i = bin15500; i < cutoffBin && i < sizzleReal.Length / 2; i++)
                totalHfEnergy += sizzleReal[i] * sizzleReal[i] + sizzleImag[i] * sizzleImag[i];

            frames++;
            if (frames >= 20) break;
        }

        if (frames == 0 || totalHfEnergy <= 0) return false;
        double ratio = sizzleEnergy / totalHfEnergy;
        return ratio > 0.4;
    }

    public (bool hasPreEcho, int preEchoCount) DetectPreEcho(float[] samples, int sampleRate)
    {
        if (samples.Length < sampleRate) return (false, 0);
        int windowMs = 5; // 5 ms window — typical MP3 pre-echo duration
        int windowSamples = sampleRate * windowMs / 1000;
        int preEchoCount = 0;
        double prevRms = 0;

        for (int pos = 0; pos + windowSamples * 2 <= samples.Length; pos += windowSamples)
        {
            double rmsBefore = 0, rmsAfter = 0;
            for (int i = pos; i < pos + windowSamples; i++)
                rmsBefore += samples[i] * samples[i];
            for (int i = pos + windowSamples; i < pos + windowSamples * 2; i++)
                rmsAfter += samples[i] * samples[i];
            rmsBefore = Math.Sqrt(rmsBefore / windowSamples);
            rmsAfter = Math.Sqrt(rmsAfter / windowSamples);

            // Pre-echo: noise burst before a transient
            // Transient detected as sudden 4x+ amplitude increase
            if (rmsAfter > prevRms * 4.0 && rmsBefore > rmsAfter * 0.15)
                preEchoCount++;
            prevRms = rmsAfter;
        }

        return (preEchoCount > 3, preEchoCount);
    }

    public bool DetectSpectralHoles(double[] avgSpectrum, double nyquist)
    {
        int bins = avgSpectrum.Length;
        if (bins < 100) return false;

        // MP3 spectral holes: isolated bins with unusually low energy
        // surrounded by higher energy bins (psychoacoustic masking removal)
        int searchStart = bins / 6; // Skip bass, focus on mids/highs
        int holeCount = 0;
        double prevDb = 20.0 * Math.Log10(Math.Max(avgSpectrum[searchStart], 1e-10));

        for (int i = searchStart + 1; i < bins - 1; i++)
        {
            double db = 20.0 * Math.Log10(Math.Max(avgSpectrum[i], 1e-10));
            double nextDb = 20.0 * Math.Log10(Math.Max(avgSpectrum[i + 1], 1e-10));

            // Spectral hole: bin dips > 15 dB below neighbors, then recovers
            if (db < prevDb - 15 && nextDb > db + 10)
                holeCount++;
            prevDb = db;
        }

        return holeCount > bins / 50; // More than ~2% of bins have holes
    }
}
