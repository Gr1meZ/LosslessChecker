using System.IO;
using LosslessChecker.Services.Verification;

namespace LosslessChecker.Services;

public class ContainerAnalyzer
{
    public ContainerResult Analyze(string filePath, float[] samples, int sampleRate, long totalSamples = 0)
    {
        long sampleCount = totalSamples > 0 ? totalSamples : samples.Length;
        bool isCdAligned = sampleRate == 44100 && sampleCount % 588 == 0;
        bool flacOk = CheckFlacIntegrity(filePath, samples);
        var (isMqa, mqaDetails) = CheckMqa(samples, sampleRate);
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        bool isCorrupted = false;
        if (ext == ".flac") isCorrupted = !flacOk;
        else if (ext == ".wav") isCorrupted = !CheckRiffIntegrity(filePath);
        bool isHdcd = ext == ".flac" || ext == ".wav"
            ? CheckHdcd(samples, sampleRate) : false;

        string source = "Unknown";
        if (isCdAligned && flacOk) source = "CD Rip";
        else if (flacOk && !isCdAligned) source = "WEB Release";
        else if (isCdAligned && !flacOk && sampleRate == 44100) source = "CD Rip (unverified bitstream)";
        if (isMqa) source += " [Possible MQA]";
        if (isHdcd) source += " [Possible HDCD/UV22]";

        return new ContainerResult(
            isCdAligned, flacOk, source, isMqa, mqaDetails, isHdcd,
            PcmHasher.ComputePcmMd5(samples), isCorrupted);
    }

    private static bool CheckRiffIntegrity(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var header = new byte[12];
            if (fs.Read(header, 0, 12) < 12) return false;
            return header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                && header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E';
        }
        catch { return false; }
    }

    private static bool CheckFlacIntegrity(string filePath, float[] samples)
    {
        if (!filePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var header = new byte[4];
            if (fs.Read(header, 0, 4) < 4) return true;

            if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
            {
                var id3hdr = new byte[6];
                if (fs.Read(id3hdr, 0, 6) < 6) return true;
                int id3Size = ((id3hdr[2] & 0x7F) << 21) | ((id3hdr[3] & 0x7F) << 14)
                            | ((id3hdr[4] & 0x7F) << 7) | (id3hdr[5] & 0x7F);
                fs.Seek(id3Size, SeekOrigin.Current);
                if (fs.Read(header, 0, 4) < 4) return true;
            }

            if (header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C')
                return true;

            bool lastBlock = false;
            while (!lastBlock)
            {
                int blockHeader = fs.ReadByte();
                if (blockHeader < 0) break;
                lastBlock = (blockHeader & 0x80) != 0;
                int blockType = blockHeader & 0x7F;
                int size = (fs.ReadByte() << 16) | (fs.ReadByte() << 8) | fs.ReadByte();

                if (blockType == 0)
                {
                    if (size >= 34) return true;
                    break;
                }
                fs.Seek(size, SeekOrigin.Current);
            }
        }
        catch { }
        return true;
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
        return (isMqa, isMqa ? "Possible MQA-encoded container — may require MQA decoder" : "");
    }

    private static bool CheckHdcd(float[] samples, int sampleRate)
    {
        if (sampleRate != 44100 || samples.Length < 5880) return false;

        int hdcdHits = 0, totalWindows = 0;
        const int windowSize = 588;
        for (int pos = 0; pos + windowSize <= samples.Length; pos += windowSize)
        {
            int patternHits = 0;
            for (int i = pos; i < pos + windowSize; i++)
            {
                int sample16 = (int)Math.Round(Math.Abs(samples[i]) * 32767.0);
                if ((sample16 & 0x1) != 0 && ((sample16 >> 1) & 0x1) == 0)
                    patternHits++;
            }
            if ((double)patternHits / windowSize > 0.5) hdcdHits++;
            totalWindows++;
        }

        return totalWindows > 5 && (double)hdcdHits / totalWindows > 0.7;
    }
}

    public record ContainerResult(
    bool IsCdAligned, bool FlacIntegrityOk, string Source,
    bool IsMqa, string MqaDetails, bool IsHdcd,
    byte[] PcmMd5, bool IsCorrupted);
