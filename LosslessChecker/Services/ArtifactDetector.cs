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

        // If cutoff is near Nyquist, there's not enough HF spectrum
        // to reliably detect artifacts — skip to avoid false positives.
        // 48kHz guard: MP3 at 48k cuts off ≤20.5 kHz (85%). Cutoff at 88%+
        // Nyquist (21.1 kHz) leaves too little room — any flatness there is noise, not artifacts.
        double cutoffRatio = cutoffFrequency / (sampleRate / 2.0);
        double skipThreshold = sampleRate == 48000 ? 0.88 : 0.95;
        if (cutoffRatio > skipThreshold)
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

        return holeCount > bins / 50;
    }

    public (bool hasSbr, string sbrVerdict) DetectSbr(
        double[] averagedSpectrum, int sampleRate)
    {
        int bins = averagedSpectrum.Length;
        if (bins < 100) return (false, "");

        double nyquist = sampleRate / 2.0;

        int bin12k = (int)(12000.0 / nyquist * bins);
        int bin18k = (int)(18000.0 / nyquist * bins);
        bin12k = Math.Clamp(bin12k, bins / 4, bins - 1);
        bin18k = Math.Clamp(bin18k, bin12k + 10, bins - 1);

        int localCutoffBin = bin18k;
        double steepestDrop = 0;
        for (int i = bin12k + 20; i < bin18k - 5; i++)
        {
            double before = MaxInRange(averagedSpectrum, i - 15, i);
            double after = MaxInRange(averagedSpectrum, i, i + 15);
            double dropDb = 20.0 * Math.Log10(Math.Max(before, 1e-10) / Math.Max(after, 1e-10));
            if (dropDb > steepestDrop) { steepestDrop = dropDb; localCutoffBin = i; }
        }

        int upperStart = localCutoffBin;
        int upperEnd = Math.Min(localCutoffBin + (int)(3000.0 / nyquist * bins), bins - 1);
        int lowerStart = Math.Max(1, localCutoffBin - (int)(3000.0 / nyquist * bins));
        int lowerEnd = localCutoffBin;

        if (upperEnd - upperStart < 10 || lowerEnd - lowerStart < 10)
            return (false, "");

        double upperFlatness = ComputeFlatness(averagedSpectrum, upperStart, upperEnd);
        double lowerFlatness = ComputeFlatness(averagedSpectrum, lowerStart, lowerEnd);

        bool hasTonalPatches = upperFlatness < 0.15 && lowerFlatness > 0.3;

        int envBins = Math.Min(upperEnd - upperStart, lowerEnd - lowerStart);
        double envCorr = ComputeEnvelopeCorrelation(averagedSpectrum, lowerStart, upperStart, envBins);

        double upperEnergy = SumRange(averagedSpectrum, upperStart, upperEnd);
        double lowerEnergy = SumRange(averagedSpectrum, lowerStart, lowerEnd);
        double energyRatioDb = lowerEnergy > 0 ? 20.0 * Math.Log10(upperEnergy / lowerEnergy) : -200;

        bool hasSbr = hasTonalPatches || (envCorr > 0.7 && energyRatioDb > -24 && energyRatioDb < -12);
        string verdict = hasSbr ? "AAC SBR" : "";
        return (hasSbr, verdict);
    }

    private static double MaxInRange(double[] spectrum, int start, int end)
    {
        double max = 0;
        for (int i = Math.Max(0, start); i < Math.Min(spectrum.Length, end); i++)
            max = Math.Max(max, spectrum[i]);
        return max;
    }

    private static double SumRange(double[] spectrum, int start, int end)
    {
        double sum = 0;
        for (int i = Math.Max(0, start); i < Math.Min(spectrum.Length, end); i++)
            sum += spectrum[i];
        return sum;
    }

    private static double ComputeFlatness(double[] spectrum, int start, int end)
    {
        double geomSum = 0, arithSum = 0;
        int count = 0;
        for (int i = start; i < end; i++)
        {
            double v = Math.Max(spectrum[i], 1e-10);
            geomSum += Math.Log(v);
            arithSum += v;
            count++;
        }
        if (count == 0 || arithSum <= 0) return 1.0;
        return Math.Exp(geomSum / count) / (arithSum / count);
    }

    private static double ComputeEnvelopeCorrelation(double[] spectrum, int aStart, int bStart, int count)
    {
        double sumA = 0, sumB = 0, sumAB = 0, sumA2 = 0, sumB2 = 0;
        for (int i = 0; i < count; i++)
        {
            double a = spectrum[aStart + i];
            double b = spectrum[bStart + i];
            sumA += a; sumB += b; sumAB += a * b; sumA2 += a * a; sumB2 += b * b;
        }
        double n = count;
        double denom = Math.Sqrt((n * sumA2 - sumA * sumA) * (n * sumB2 - sumB * sumB));
        return denom > 1e-10 ? (n * sumAB - sumA * sumB) / denom : 0;
    }

    public bool DetectAbruptEdges(float[] samples, int sampleRate)
    {
        int edgeSamples = sampleRate / 2;
        if (samples.Length < edgeSamples * 2) return false;

        double overallSumSq = 0, edgeSumSq = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double s = samples[i];
            double sq = s * s;
            overallSumSq += sq;
            if (i < edgeSamples || i >= samples.Length - edgeSamples)
                edgeSumSq += sq;
        }

        double overallRms = Math.Sqrt(overallSumSq / samples.Length);
        double edgeRms = Math.Sqrt(edgeSumSq / (edgeSamples * 2));

        double overallDb = 20.0 * Math.Log10(Math.Max(overallRms, 1e-10));
        double edgeDb = 20.0 * Math.Log10(Math.Max(edgeRms, 1e-10));

        return edgeDb < -60 && overallDb > -30;
    }
}
