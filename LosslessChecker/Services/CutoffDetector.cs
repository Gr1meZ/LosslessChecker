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

        var frame = new float[FftSize];
        var real = new float[FftSize];
        var imag = new float[FftSize];

        int hfStartBin = (int)(10000.0 / nyquist * (FftSize / 2));

        var hfEnergies = new List<(int pos, double hfEnergy)>();

        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            Array.Copy(samples, pos, frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) frame[i] *= window[i];
            Array.Copy(frame, real, FftSize);
            Array.Clear(imag, 0, FftSize);
            fft.Direct(real, imag);

            double hfEnergy = 0;
            for (int i = hfStartBin; i < FftSize / 2; i++)
                hfEnergy += (double)real[i] * real[i] + (double)imag[i] * imag[i];

            hfEnergies.Add((pos, hfEnergy));
        }

        if (hfEnergies.Count == 0)
            return (nyquist, 0, Array.Empty<double>());

        var sortedByHf = hfEnergies.OrderByDescending(f => f.hfEnergy).ToList();
        int topCount = Math.Max(1, sortedByHf.Count / 6);
        double hfThreshold = sortedByHf[topCount - 1].hfEnergy;
        var topPositions = new HashSet<int>(sortedByHf.Take(topCount).Select(f => f.pos));

        // Second pass: re-run FFT only for top frames to build averaged spectrum
        var avgMagnitudes = new double[FftSize / 2];
        int accumulatedFrames = 0;

        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            if (!topPositions.Contains(pos)) continue;

            Array.Copy(samples, pos, frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) frame[i] *= window[i];
            Array.Copy(frame, real, FftSize);
            Array.Clear(imag, 0, FftSize);
            fft.Direct(real, imag);

            for (int i = 0; i < FftSize / 2; i++)
                avgMagnitudes[i] += Math.Sqrt((double)real[i] * real[i] + (double)imag[i] * imag[i]);

            accumulatedFrames++;
        }

        if (accumulatedFrames > 0)
        {
            for (int i = 0; i < avgMagnitudes.Length; i++)
                avgMagnitudes[i] /= accumulatedFrames;
        }

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

    public static int MapCutoffToBitrate(double cutoffHz, string shelfType, int actualBitrate, int sampleRate)
    {
        var nyquist = sampleRate / 2.0;
        if (cutoffHz >= nyquist * 0.97) return 0;

        if (cutoffHz <= 16500) return 128;
        if (cutoffHz <= 18500) return 192;
        if (cutoffHz <= 20000) return 256;
        if (cutoffHz <= 20500) return 320;
        return 0;
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

        // 4. Compute sliding slope in dB/octave across a 30-bin window.
        // Scan left-to-right; weight slopes by normalized frequency so that
        // brickwalls near Nyquist (codec cutoff) are preferred over steeper
        // but lower-frequency internal mix filters (e.g. synth LP at 8 kHz).
        const int windowBins = 30;
        double freqPerBin = nyquist / bins;
        double bestWeightedSlope = 0; // weighted: more negative = better
        int bestBin = bins - 1;
        double bestRawSlope = 0;

        int searchStart = bins / 3;

        for (int center = searchStart; center < bins - windowBins / 2; center++)
        {
            int wStart = Math.Max(1, center - windowBins / 2);
            int wEnd = Math.Min(bins - 1, center + windowBins / 2);
            if (wEnd - wStart < 10) continue;

            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            int n = 0;
            for (int i = wStart; i <= wEnd; i++)
            {
                double freq = i * freqPerBin;
                if (freq < 1) continue;
                double x = Math.Log2(freq / 1000.0);
                double y = smoothed[i];
                sumX += x; sumY += y; sumXY += x * y; sumX2 += x * x; n++;
            }

            if (n < 5) continue;
            double denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-10) continue;
            double slope = (n * sumXY - sumX * sumY) / denom;

            // Weight: slopes at higher frequencies get a bonus (more negative weight).
            // A -20 dB/oct at 20 kHz beats a -25 dB/oct at 8 kHz.
            double freqWeight = (double)center / bins;
            double weightedSlope = slope * (0.5 + 0.5 * freqWeight);

            if (weightedSlope < bestWeightedSlope)
            {
                bestWeightedSlope = weightedSlope;
                bestRawSlope = slope;
                bestBin = center;
            }
        }

        bool isBrickwall = bestRawSlope < -18;
        if (isBrickwall && bestBin > 0 && bestBin < bins - 1)
        {
            int beforeStart = Math.Max(1, bestBin - 40);
            int afterEnd = Math.Min(bins - 1, bestBin + 40);
            double beforeAvg = 0, afterAvg = 0;
            int beforeCount = 0, afterCount = 0;
            for (int i = beforeStart; i < bestBin; i++) { beforeAvg += smoothed[i]; beforeCount++; }
            for (int i = bestBin + 1; i <= afterEnd; i++) { afterAvg += smoothed[i]; afterCount++; }
            beforeAvg = beforeCount > 0 ? beforeAvg / beforeCount : -100;
            afterAvg = afterCount > 0 ? afterAvg / afterCount : -100;

            if (beforeAvg - afterAvg < 12 || afterAvg > beforeAvg - 12)
                isBrickwall = false;
        }

        double cutoffHz;
        if (isBrickwall)
        {
            cutoffHz = SubBinFrequency(bestBin, bins, nyquist, smoothed);
        }
        else if (bestRawSlope < -10)
        {
            if (bestRawSlope < -18) bestRawSlope = -14;
            cutoffHz = SubBinFrequency(bestBin, bins, nyquist, smoothed);
        }
        else
        {
            cutoffHz = nyquist;
            bestRawSlope = 0;
        }

        return (cutoffHz, Math.Round(bestRawSlope, 2));
    }

    private static double SubBinFrequency(int bestBin, int bins, double nyquist, double[] smoothed)
    {
        double binFreq = (double)bestBin / bins * nyquist;
        if (bestBin <= 0 || bestBin >= smoothed.Length - 1)
            return binFreq;

        double y0 = smoothed[bestBin - 1];
        double y1 = smoothed[bestBin];
        double y2 = smoothed[bestBin + 1];

        double denom = y0 - 2 * y1 + y2;
        if (Math.Abs(denom) < 1e-10)
            return binFreq;

        double delta = 0.5 * (y0 - y2) / denom;
        delta = Math.Clamp(delta, -0.5, 0.5);
        return (bestBin + delta) / bins * nyquist;
    }

    public (string encoderMatch, string shelfType) ClassifyCutoff(
        double cutoffHz, double cutoffSlope, int sampleRate)
    {
        var nyquist = sampleRate / 2.0;
        double ratio = nyquist > 0 ? cutoffHz / nyquist : 1.0;
        bool isHiRes = sampleRate >= 88200;

        // Shelf type from slope
        string shelfType = cutoffSlope switch
        {
            < -18 => "Brickwall",
            < -10 => "Filtered",
            _ => "Natural"
        };

        // For Hi-Res files: if cutoff is above CD Nyquist (22.05kHz),
        // this is a legitimate Hi-Res recording, not a codec artifact.
        // Codec encoder labels (MP3 128-192, etc.) only apply below 22kHz.
        string encoderMatch;
        if (isHiRes && cutoffHz > 22100)
        {
            encoderMatch = "None (Hi-Res)";
        }
        else
        {
            encoderMatch = cutoffHz switch
            {
                <= 16500 => "MP3 128-192 kbps",
                <= 18500 => "MP3 192-256 kbps",
                <= 20500 => "MP3 320 / AAC 256 kbps",
                <= 21500 => "Possible LP filter",
                _ => "None"
            };
        }

        // Override: if ratio > 0.95, encoder match is None regardless
        if (!isHiRes && ratio >= 0.95)
            encoderMatch = "None";

        // 48kHz guard: brickwall at >90% Nyquist (21600 Hz) is a mastering LP filter, not a codec.
        // MP3 at 48kHz cuts off at ≤20.5 kHz (85%). Anything above is too high for a lossy encoder.
        if (sampleRate == 48000 && ratio >= 0.90 && shelfType == "Brickwall")
            shelfType = "Filtered";

        // Override: only reclassify brickwall as natural if ratio >= 0.97
        // (cutoff within 3% of Nyquist). ADC anti-aliasing filters sit right at
        // Nyquist. Below 0.97, even a "brickwall" at 20kHz/44.1kHz (ratio 0.907)
        // is suspicious — it matches MP3 320 / AAC 256 cutoff and should NOT be
        // cleared as "Natural".
        if (ratio >= 0.97 && shelfType == "Brickwall")
            shelfType = "Natural";

        return (encoderMatch, shelfType);
    }

    public (string encoderMatch, string shelfType) ClassifyCutoff(
        double cutoffHz, double cutoffSlope, int sampleRate, bool isVinylRip)
    {
        var (encoderMatch, shelfType) = ClassifyCutoff(cutoffHz, cutoffSlope, sampleRate);
        if (isVinylRip && cutoffHz >= 16000 && cutoffHz <= 18000 && shelfType == "Filtered")
        {
            shelfType = "Vinyl Rolloff";
            encoderMatch = "None (Vinyl Transfer)";
        }
        return (encoderMatch, shelfType);
    }

    public bool IsFakeHiRes(double cutoffHz, string shelfType, int sampleRate)
    {
        if (sampleRate < 88200) return false;
        // Fake Hi-Res = brickwall at known upscale frequencies:
        //   16-17 kHz: MP3 128 kbps upscaled to Hi-Res
        //   18-20 kHz: MP3 192-320 / AAC upscaled
        //   20-22.1 kHz: CD (44.1k) upscaled to Hi-Res
        // Real acoustic recordings may have natural rolloff at any frequency,
        // but they won't have a brickwall shelf at these exact points.
        return shelfType == "Brickwall"
            && ((cutoffHz >= 15500 && cutoffHz <= 17000)
                || (cutoffHz >= 18000 && cutoffHz <= 20000)
                || (cutoffHz >= 20000 && cutoffHz <= 22100));
    }

    public static (string bandwidth, string detectedType) ClassifyBandwidth(
        double cutoffHz, string shelfType, int sampleRate, bool hasArtifacts,
        string artifactLevel, bool hasSpectralHoles, double maxHfDb,
        bool lsbZeroPadded, int effectiveBitDepth, int bitDepth,
        bool isCdAligned, bool isMqa, bool isHdcd,
        bool hasHardClipping, string encoderMatch)
    {
        bool isHiRes = sampleRate >= 88200;
        var nyquist = sampleRate / 2.0;

        // 1a. Fake 24-bit
        if (lsbZeroPadded && bitDepth == 24)
            return ("Fake 24-bit", "FAKE 24bit");

        // 1b. Transcode: brickwall + artifacts in non-MP3/non-AAC container
        if (shelfType == "Brickwall" && (artifactLevel == "Strong" || artifactLevel == "Medium"))
        {
            if (cutoffHz <= 17000)
                return ("16kHz", "MP3 128");
            if (cutoffHz <= 19500)
                return ("18kHz", "MP3 192");
            if (cutoffHz <= 20500)
                return ("20kHz", "MP3 320");
            return ("Full Range", "UPSCALE (MP3->FLAC)");
        }

        // 2. Hi-Res
        if (isHiRes)
        {
            string bw = $"Hi-Res ({sampleRate / 1000:F0}k)";
            if (maxHfDb < -60)
                return (bw, "UPSCALE (CD→HI-RES)");
            if (maxHfDb < -45)
                return (bw, "SUSPICIOUS HI-RES");
            return (bw, $"HI-RES {sampleRate / 1000:F0}k");
        }

        // 3. Lossless — any file without lossy compression artifacts
        if (!isHiRes)
        {
            bool hasLossyArtifacts = shelfType == "Brickwall"
                && (artifactLevel != "None" || cutoffHz < nyquist * 0.90)
                || ((artifactLevel == "Strong" || artifactLevel == "Medium") && hasSpectralHoles);

            bool isTooLowForLossless = cutoffHz < nyquist * 0.80 && artifactLevel != "None";

            if ((!hasLossyArtifacts || shelfType == "Natural") && !isTooLowForLossless)
            {
                string bw = cutoffHz >= nyquist * 0.92 ? "Full Range" : $"{cutoffHz / 1000:F0}kHz";
                string dt;
                if (isCdAligned && sampleRate == 44100)
                    dt = "LOSSLESS (CD)";
                else if (bitDepth > 16 && !lsbZeroPadded)
                    dt = "LOSSLESS 24bit";
                else
                    dt = "LOSSLESS (WEB)";

                if (cutoffHz < nyquist * 0.90 && artifactLevel == "None")
                    dt = "LOSSLESS (Mastered LPF)";

                return (bw, dt);
            }

            // Very low cutoff with non-brickwall shelf and weak artifacts
            // — likely analog tape, vintage recording, or aggressive mastering LPF
            if (artifactLevel == "Weak" && shelfType != "Brickwall" && cutoffHz < nyquist * 0.80)
            {
                string bw = $"{cutoffHz / 1000:F0}kHz";
                string dt = isCdAligned && sampleRate == 44100 ? "LOSSLESS (LEGACY)" : "LOSSLESS (ROLLOFF)";
                return (bw, dt);
            }

            // Has some lossy artifacts but not clearly brickwall — borderline
            if (artifactLevel == "Weak" && shelfType != "Brickwall")
            {
                string bw = cutoffHz >= nyquist * 0.85 ? "Full Range" : $"{cutoffHz / 1000:F0}kHz";
                return (bw, "UNCERTAIN");
            }
        }

        // 4. MP3 / AAC via encoder match
        if (encoderMatch.StartsWith("MP3"))
        {
            string bw = cutoffHz switch
            {
                <= 17000 => "16kHz",
                <= 19500 => "18kHz",
                <= 20500 => "20kHz",
                _ => "Full Range"
            };
            string dt = cutoffHz switch
            {
                <= 17000 => "MP3 128",
                <= 18500 => "MP3 192",
                <= 20000 => "MP3 256",
                <= 20500 => "MP3 320",
                _ => "MP3"
            };
            return (bw, dt);
        }

        // 5. UNCERTAIN fallback — refine based on available evidence
        if (shelfType == "Brickwall" && (artifactLevel == "Strong" || artifactLevel == "Medium"))
            return ($"{cutoffHz / 1000:F0}kHz", "LOSSY (TRANSCODE)");
        if (shelfType == "Filtered" && artifactLevel == "None" && cutoffHz >= nyquist * 0.85)
            return ($"{cutoffHz / 1000:F0}kHz", "LOSSLESS (WEB)");
        if (cutoffHz < nyquist * 0.85 && artifactLevel == "None")
            return ($"{cutoffHz / 1000:F0}kHz", "LOSSLESS (Mastered LPF)");
        return ($"{cutoffHz / 1000:F0}kHz", "UNCERTAIN");
    }

    /// <summary>
    /// Checks whether the spectrum above the cutoff is effectively absolute silence
    /// (lossy codec signature) vs. analog noise or dither (legitimate LPF).
    /// </summary>
    public static bool HasAbsoluteSilence(double[] averagedSpectrum, double cutoffHz, double sampleRate)
    {
        var nyquist = sampleRate / 2.0;
        int cutoffBin = (int)(cutoffHz / nyquist * averagedSpectrum.Length);
        cutoffBin = Math.Max(1, Math.Min(cutoffBin, averagedSpectrum.Length - 1));

        if (cutoffBin >= averagedSpectrum.Length - 1) return false;

        // Look for absolute digital silence above cutoff
        int silenceCount = 0;
        int checkCount = 0;
        for (int i = cutoffBin; i < averagedSpectrum.Length; i++)
        {
            // Values below ~1e-15 are well below any dither floor.
            // Analog noise or shaped dither sits at ~1e-6 to 1e-10.
            if (averagedSpectrum[i] < 1e-15)
                silenceCount++;
            checkCount++;
        }

        // Classify as codec silence only if >95% of bins above cutoff are dead
        return checkCount > 10 && (double)silenceCount / checkCount > 0.95;
    }
}
