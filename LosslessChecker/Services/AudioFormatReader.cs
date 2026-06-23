using System.IO;

namespace LosslessChecker.Services;

public static class AudioFormatReader
{
    public record OriginalFormat(int SampleRate, int BitDepth, int Channels);

    public static OriginalFormat? ReadOriginal(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".flac" => ReadFlac(filePath),
            ".wav" => ReadWav(filePath),
            _ => null
        };
    }

    private static OriginalFormat? ReadFlac(string filePath)
    {
        try
        {
            var header = new byte[42];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            if (fs.Read(header, 0, header.Length) < header.Length)
                return null;

            if (header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C')
                return null;

            // STREAMINFO starts at byte 8 (after 4-byte magic + 4-byte metadata block header)
            int sampleRate = (header[18] << 12) | (header[19] << 4) | (header[20] >> 4);
            int channels = ((header[20] >> 1) & 0x07) + 1;
            int bitDepth = ((header[20] & 0x01) << 4) | (header[21] >> 4) + 1;

            return new OriginalFormat(sampleRate, bitDepth, channels);
        }
        catch
        {
            return null;
        }
    }

    private static OriginalFormat? ReadWav(string filePath)
    {
        try
        {
            var header = new byte[44];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            if (fs.Read(header, 0, header.Length) < 44)
                return null;

            if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F')
                return null;

            int sampleRate = BitConverter.ToInt32(header, 24);
            int channels = BitConverter.ToInt16(header, 22);
            int bitDepth = BitConverter.ToInt16(header, 34);

            return new OriginalFormat(sampleRate, bitDepth, channels);
        }
        catch
        {
            return null;
        }
    }
}
