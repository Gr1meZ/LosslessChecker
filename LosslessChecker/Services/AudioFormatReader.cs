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
            ".m4a" or ".alac" => ReadMp4(filePath),
            _ => null
        };
    }

    private static OriginalFormat? ReadMp4(string filePath)
    {
        var info = Mp4CodecReader.DetectCodec(filePath);
        if (info.SampleRate > 0)
            return new OriginalFormat(info.SampleRate, info.BitDepth > 0 ? info.BitDepth : 16, info.Channels > 0 ? info.Channels : 2);
        return null;
    }

    private static OriginalFormat? ReadFlac(string filePath)
    {
        try
        {
            var header = new byte[42];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            if (fs.Read(header, 0, 4) < 4) return null;

            // Skip ID3v2 tag if present (some tools prepend ID3 to FLAC)
            if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
            {
                var id3hdr = new byte[6];
                if (fs.Read(id3hdr, 0, 6) < 6) return null;
                int id3Size = ((id3hdr[2] & 0x7F) << 21) | ((id3hdr[3] & 0x7F) << 14)
                            | ((id3hdr[4] & 0x7F) << 7) | (id3hdr[5] & 0x7F);
                fs.Seek(id3Size, SeekOrigin.Current);
                if (fs.Read(header, 0, 42) < 42) return null;
            }
            else
            {
                // Seek back and read full STREAMINFO
                fs.Seek(0, SeekOrigin.Begin);
                if (fs.Read(header, 0, header.Length) < header.Length) return null;
            }

            if (header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C')
                return null;

            int sampleRate = (header[18] << 12) | (header[19] << 4) | (header[20] >> 4);
            int channels = ((header[20] >> 1) & 0x07) + 1;
            int bitDepth = ((header[20] & 0x01) << 4) | (header[21] >> 4) + 1;

            return new OriginalFormat(sampleRate, bitDepth, channels);
        }
        catch { return null; }
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
