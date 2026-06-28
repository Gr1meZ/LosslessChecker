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

        var (cutoff, cutoffSlope) = FindCutoffByNoiseFloor(avgMagnitudes, nyquist);
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

        if (cutoffHz <= 16000) return 128;
        if (cutoffHz <= 18000) return 192;
        if (cutoffHz <= 19000) return 256;
        if (cutoffHz <= 20500) return 320;
        return 0;
    }

    private static (double cutoff, double cutoffSlope) FindCutoffByNoiseFloor(
        double[] avgMagnitudes, double nyquist)
    {
        int bins = avgMagnitudes.Length;
        if (bins < 10) return (nyquist, 0);

        // 1. Divide spectrum into 1-kHz bands and compute average dB per band.
        double hzPerBin = nyquist / bins;
        int bandsPerKhz = (int)Math.Max(1, 1000.0 / hzPerBin);
        int numBands = Math.Max(2, bins / bandsPerKhz);

        var bandDb = new double[numBands];
        double peakDb = double.MinValue;
        for (int band = 0; band < numBands; band++)
        {
            int start = band * bandsPerKhz;
            int end = Math.Min(bins, (band + 1) * bandsPerKhz);
            if (end <= start) { bandDb[band] = -200; continue; }

            double sumMag = 0;
            for (int i = start; i < end; i++)
                sumMag += avgMagnitudes[i];
            double avgMag = sumMag / (end - start);
            double db = 20.0 * Math.Log10(Math.Max(avgMag, 1e-10));
            bandDb[band] = db;
            if (db > peakDb) peakDb = db;
        }

        // Normalize to peak dB
        for (int band = 0; band < numBands; band++)
            bandDb[band] -= peakDb;

        // 2. Scan from Nyquist downward. Find the first significant energy band,
        //    then check for an abrupt drop at the boundary of that band.
        //    A drop of ≥35 dB/oct between adjacent 1-kHz bands indicates
        //    an unnatural cutoff (codec brickwall or resampling artifact).
        double cutoffHz = nyquist;
        double cutoffSlope = 0;

        for (int band = numBands - 1; band >= 2; band--)
        {
            double currentDb = bandDb[band];
            double belowDb = bandDb[band - 1];
            double aboveDb = band < numBands - 1 ? bandDb[band + 1] : -200;

            // Skip bands deep in the noise floor
            if (currentDb < -70 && belowDb < -70) continue;

            // If the band ABOVE is in noise (-70dB) and current band IS
            // in noise, we haven't reached signal yet
            if (belowDb < -70) continue;

            // Found a band with signal (belowDb > -70).
            // Check the drop between belowDb (signal band) and currentDb
            // (this band or the band above).
            double drop = belowDb - currentDb;

            if (drop >= 35 && belowDb > -45)
            {
                // Sharp drop found — unnatural cutoff.
                // Cutoff = top edge of the "below" band = (band) * 1 kHz
                cutoffHz = band * 1000.0;
                cutoffSlope = -drop * 2; // approximate dB/oct for 0.5 oct spans
                break;
            }

            // No sharp drop — this is a natural continuation.
            // Cutoff = top edge of this band (the last band with signal).
            if (belowDb > -45)
            {
                cutoffHz = Math.Min(nyquist, band * 1000.0);
                cutoffSlope = -0.5; // gentle slope → Natural
                break;
            }
        }

        // 3. If cutoff is very close to Nyquist (within 1 kHz), the recording
        //    has full bandwidth — return Nyquist with natural slope.
        if (cutoffHz >= nyquist - 1000)
        {
            cutoffHz = nyquist;
            cutoffSlope = 0;
        }

        return (cutoffHz, Math.Round(cutoffSlope, 2));
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

        string shelfType = cutoffSlope switch
        {
            < -18 => "Brickwall",
            < -10 => "Filtered",
            _ => "Natural"
        };

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

        if (!isHiRes && ratio >= 0.95)
            encoderMatch = "None";

        if (sampleRate == 44100 && ratio >= 0.93 && shelfType == "Brickwall")
            shelfType = "Filtered";
        if (sampleRate == 48000 && ratio >= 0.90 && shelfType == "Brickwall")
            shelfType = "Filtered";

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

        if (lsbZeroPadded && bitDepth == 24)
            return ("Fake 24-bit", "FAKE 24bit");

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

        if (isHiRes)
        {
            string bw = $"Hi-Res ({sampleRate / 1000:F0}k)";
            if (maxHfDb < -60)
                return (bw, "UPSCALE (CD→HI-RES)");
            if (maxHfDb < -45)
                return (bw, "SUSPICIOUS HI-RES");
            return (bw, $"HI-RES {sampleRate / 1000:F0}k");
        }

        if (!isHiRes)
        {
            bool hasLossyArtifacts = shelfType == "Brickwall"
                && (artifactLevel != "None" || cutoffHz < nyquist * 0.90)
                || ((artifactLevel == "Strong" || artifactLevel == "Medium") && hasSpectralHoles);

            bool isTooLowForLossless = cutoffHz < nyquist * 0.80;

            if ((!hasLossyArtifacts || shelfType == "Natural") && !isTooLowForLossless)
            {
                // If encoder match points to a lossy codec and cutoff isn't near Nyquist,
                // this is suspicious — don't call it LOSSLESS.
                if ((encoderMatch.StartsWith("MP3") || encoderMatch.StartsWith("AAC")) && cutoffHz < nyquist * 0.95)
                    return ($"{cutoffHz / 1000:F0}kHz", "UNCERTAIN");

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

            if (artifactLevel == "Weak" && shelfType != "Brickwall" && cutoffHz < nyquist * 0.80)
            {
                string bw = $"{cutoffHz / 1000:F0}kHz";
                string dt = isCdAligned && sampleRate == 44100 ? "LOSSLESS (LEGACY)" : "LOSSLESS (ROLLOFF)";
                return (bw, dt);
            }

            if (artifactLevel == "Weak" && shelfType != "Brickwall")
            {
                string bw = cutoffHz >= nyquist * 0.85 ? "Full Range" : $"{cutoffHz / 1000:F0}kHz";
                return (bw, "UNCERTAIN");
            }
        }

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

        if (shelfType == "Brickwall" && (artifactLevel == "Strong" || artifactLevel == "Medium"))
            return ($"{cutoffHz / 1000:F0}kHz", "LOSSY (TRANSCODE)");
        if (shelfType == "Filtered" && artifactLevel == "None" && cutoffHz >= nyquist * 0.85)
            return ($"{cutoffHz / 1000:F0}kHz", "LOSSLESS (WEB)");
        if (cutoffHz < nyquist * 0.85 && artifactLevel == "None" && cutoffHz >= nyquist * 0.65)
            return ($"{cutoffHz / 1000:F0}kHz", "LOSSLESS (Mastered LPF)");
        return ($"{cutoffHz / 1000:F0}kHz", "UNCERTAIN");
    }

    public static bool HasAbsoluteSilence(double[] averagedSpectrum, double cutoffHz, double sampleRate)
    {
        var nyquist = sampleRate / 2.0;
        int cutoffBin = (int)(cutoffHz / nyquist * averagedSpectrum.Length);
        cutoffBin = Math.Max(1, Math.Min(cutoffBin, averagedSpectrum.Length - 1));

        if (cutoffBin >= averagedSpectrum.Length - 1) return false;

        int silenceCount = 0;
        int checkCount = 0;
        for (int i = cutoffBin; i < averagedSpectrum.Length; i++)
        {
            if (averagedSpectrum[i] < 1e-15)
                silenceCount++;
            checkCount++;
        }

        return checkCount > 10 && (double)silenceCount / checkCount > 0.95;
    }
}
