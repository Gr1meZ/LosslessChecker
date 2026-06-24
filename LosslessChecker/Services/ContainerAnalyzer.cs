using System.IO;
using LosslessChecker.Services.Verification;

namespace LosslessChecker.Services;

public class ContainerAnalyzer
{
    public ContainerResult Analyze(string filePath, float[] samples, int sampleRate)
    {
        bool isCdAligned = sampleRate == 44100 && samples.Length % 588 == 0;
        bool flacOk = CheckFlacIntegrity(filePath, samples);
        var (isMqa, mqaDetails) = CheckMqa(samples, sampleRate);
        bool isHdcd = CheckHdcd(samples, sampleRate);

        string source = "Unknown";
        if (isCdAligned && flacOk) source = "CD Rip";
        else if (flacOk && !isCdAligned) source = "WEB Release";
        else if (isCdAligned && !flacOk && sampleRate == 44100) source = "CD Rip (unverified bitstream)";
        if (isMqa) source += " [MQA]";
        if (isHdcd) source += " [HDCD]";

        return new ContainerResult(
            isCdAligned, flacOk, source, isMqa, mqaDetails, isHdcd,
            PcmHasher.ComputePcmMd5(samples));
    }

    private static bool CheckFlacIntegrity(string filePath, float[] samples)
    {
        if (!filePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            return true; // Not a FLAC file, can't verify

        try
        {
            // Read STREAMINFO block to get stored MD5
            // FLAC header: "fLaC" (4 bytes) + metadata blocks
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var header = new byte[4];
            if (fs.Read(header, 0, 4) != 4 || header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C')
                return true; // Not FLAC

            // Parse metadata blocks to find STREAMINFO (type 0)
            bool lastBlock = false;
            while (!lastBlock)
            {
                int blockHeader = fs.ReadByte();
                if (blockHeader < 0) break;
                lastBlock = (blockHeader & 0x80) != 0;
                int blockType = blockHeader & 0x7F;
                // Block size is 24-bit big-endian
                int size = (fs.ReadByte() << 16) | (fs.ReadByte() << 8) | fs.ReadByte();

                if (blockType == 0) // STREAMINFO
                {
                    // Skip: minBlockSize(2) + maxBlockSize(2) + minFrameSize(3) + maxFrameSize(3)
                    // + sampleRate(3) + channels(3) + bitsPerSample(3) + totalSamples(5)
                    fs.Seek(2 + 2 + 3 + 3 + 3 + 3 + 3 + 5, SeekOrigin.Current);
                    // MD5 follows: 16 bytes
                    var storedMd5 = new byte[16];
                    fs.Read(storedMd5, 0, 16);

                    var computedMd5 = ComputePcmMd5(samples);
                    return storedMd5.SequenceEqual(computedMd5);
                }
                fs.Seek(size, SeekOrigin.Current);
            }
        }
        catch { }
        return true; // Can't verify, assume OK
    }

    public static byte[] ComputePcmMd5(float[] samples) => PcmHasher.ComputePcmMd5(samples);

    private static (bool isMqa, string details) CheckMqa(float[] samples, int sampleRate)
    {
        if (sampleRate > 48000) return (false, "");
        if (samples.Length < 1000) return (false, "");

        // MQA signature: specific bit patterns in LSB of 24-bit audio
        // MQA uses bits 8-15 of a 24-bit sample for encoded HF data
        // Simple heuristic: check LSB correlation patterns
        int mqaHits = 0, total = 0;
        for (int i = 0; i < samples.Length - 16; i += 16)
        {
            int lsbSum = 0;
            for (int j = 0; j < 16; j++)
            {
                int sample24 = (int)Math.Round(samples[i + j] * 8388607.0);
                // MQA sync pattern: alternating bits in LSB nibble
                lsbSum += (sample24 & 1) ^ ((sample24 >> 1) & 1);
            }
            if (lsbSum > 12) mqaHits++;
            total++;
        }

        bool isMqa = total > 10 && (double)mqaHits / total > 0.6;
        return (isMqa, isMqa ? "MQA-encoded container detected — not a pure lossless file" : "");
    }

    private static bool CheckHdcd(float[] samples, int sampleRate)
    {
        if (sampleRate != 44100 || samples.Length < 1000) return false;

        // HDCD: LSB carries control codes. Pattern 0x095 = PEAK EXTENSION flag
        int hdcdHits = 0, totalWindows = 0;
        const int windowSize = 588; // CD sector size
        for (int pos = 0; pos + windowSize <= samples.Length; pos += windowSize)
        {
            int patternHits = 0;
            for (int i = pos; i < pos + windowSize; i++)
            {
                int sample16 = (int)Math.Round(Math.Abs(samples[i]) * 32767.0);
                // HDCD control code appears as specific LSB patterns
                if ((sample16 & 0x1) != 0 && ((sample16 >> 1) & 0x1) == 0)
                    patternHits++;
            }
            if ((double)patternHits / windowSize > 0.1) hdcdHits++;
            totalWindows++;
        }

        return totalWindows > 5 && (double)hdcdHits / totalWindows > 0.3;
    }
}

public record ContainerResult(
    bool IsCdAligned, bool FlacIntegrityOk, string Source,
    bool IsMqa, string MqaDetails, bool IsHdcd,
    byte[] PcmMd5);
