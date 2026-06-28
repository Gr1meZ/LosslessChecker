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
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var header = new byte[4];
            if (fs.Read(header, 0, 4) < 4) return null;

            // Skip ID3v2 tag if present (some tools prepend ID3 to FLAC)
            if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
            {
                var id3hdr = new byte[6];
                if (fs.Read(id3hdr, 0, 6) < 6) return null;
                int id3Size = ((id3hdr[2] & 0x7F) << 21) | ((id3hdr[3] & 0x7F) << 14)
                            | ((id3hdr[4] & 0x7F) << 7) | (id3hdr[5] & 0x7F);
                fs.Seek(id3Size, SeekOrigin.Current);
                if (fs.Read(header, 0, 4) < 4) return null;
            }
            else
            {
                fs.Seek(0, SeekOrigin.Begin);
                fs.Read(header, 0, 4);
            }

            if (header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C')
                return null;

            // Walk metadata blocks to find STREAMINFO (type 0)
            bool lastBlock = false;
            while (!lastBlock)
            {
                int blockHeader = fs.ReadByte();
                if (blockHeader < 0) break;
                lastBlock = (blockHeader & 0x80) != 0;
                int blockType = blockHeader & 0x7F;
                int size = (fs.ReadByte() << 16) | (fs.ReadByte() << 8) | fs.ReadByte();

                if (blockType == 0) // STREAMINFO
                {
                    // STREAMINFO layout:
                    // minBlockSize(2) + maxBlockSize(2) + minFrameSize(3) + maxFrameSize(3)
                    var streamInfo = new byte[size];
                    fs.Read(streamInfo, 0, size);

                    // sampleRate: 20 bits starting at bit 80 (byte 10)
                    int sampleRate = (streamInfo[10] << 12) | (streamInfo[11] << 4) | (streamInfo[12] >> 4);
                    // channels: 3 bits at bit 100 (byte 12, after sample rate), +1
                    int channels = ((streamInfo[12] >> 1) & 0x07) + 1;
                    // bitDepth: 5 bits at bit 103 (byte 12-13), +1
                    int bitDepth = ((streamInfo[12] & 0x01) << 4) | (streamInfo[13] >> 4) + 1;

                    return new OriginalFormat(sampleRate, bitDepth, channels);
                }

                fs.Seek(size, SeekOrigin.Current);
            }
        }
        catch { return null; }
        return null;
    }

    private static OriginalFormat? ReadWav(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var riffHeader = new byte[12];
            if (fs.Read(riffHeader, 0, 12) < 12) return null;

            if (riffHeader[0] != 'R' || riffHeader[1] != 'I' || riffHeader[2] != 'F' || riffHeader[3] != 'F')
                return null;
            if (riffHeader[8] != 'W' || riffHeader[9] != 'A' || riffHeader[10] != 'V' || riffHeader[11] != 'E')
                return null;

            long fileEnd = fs.Length;
            int sampleRate = 0, channels = 0, bitDepth = 0;

            // Walk RIFF chunks to find "fmt " and "data"
            while (fs.Position + 8 <= fileEnd)
            {
                var chunkId = new byte[4];
                var chunkSize = new byte[4];
                if (fs.Read(chunkId, 0, 4) < 4) break;
                if (fs.Read(chunkSize, 0, 4) < 4) break;
                int size = BitConverter.ToInt32(chunkSize, 0);
                if (size < 0 || fs.Position + size > fileEnd || size > 1024 * 1024) break;

                string id = System.Text.Encoding.ASCII.GetString(chunkId);

                if (id == "fmt ")
                {
                    var fmtData = new byte[Math.Min(size, 40)];
                    fs.Read(fmtData, 0, fmtData.Length);
                    channels = BitConverter.ToInt16(fmtData, 2);
                    sampleRate = BitConverter.ToInt32(fmtData, 4);
                    bitDepth = size >= 16 ? BitConverter.ToInt16(fmtData, 14) : 16;
                    // Skip remainder of chunk
                    if (size > fmtData.Length) fs.Seek(size - fmtData.Length, SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    // Found data chunk — we have what we need
                    fs.Seek(size, SeekOrigin.Current);
                    if (sampleRate > 0) break;
                }
                else
                {
                    fs.Seek(size, SeekOrigin.Current);
                }
            }

            if (sampleRate <= 0 || channels <= 0 || bitDepth <= 0)
                return null;

            return new OriginalFormat(sampleRate, bitDepth, channels);
        }
        catch
        {
            return null;
        }
    }

    public static long GetFlacMetadataSize(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var header = new byte[4];
            if (fs.Read(header, 0, 4) < 4) return 0;

            if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
            {
                var id3hdr = new byte[6];
                if (fs.Read(id3hdr, 0, 6) < 6) return 0;
                int id3Size = ((id3hdr[2] & 0x7F) << 21) | ((id3hdr[3] & 0x7F) << 14)
                            | ((id3hdr[4] & 0x7F) << 7) | (id3hdr[5] & 0x7F);
                long id3Total = 10 + id3Size;
                fs.Seek(id3Total, SeekOrigin.Begin);
                if (fs.Read(header, 0, 4) < 4) return id3Total;
                long audioStart = id3Total + 4; // + "fLaC" signature

                if (header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C')
                {
                    bool lastBlock = false;
                    while (!lastBlock)
                    {
                        int blockHeader = fs.ReadByte();
                        if (blockHeader < 0) break;
                        lastBlock = (blockHeader & 0x80) != 0;
                        int size = (fs.ReadByte() << 16) | (fs.ReadByte() << 8) | fs.ReadByte();
                        audioStart += 4 + size; // block header + data
                        fs.Seek(size, SeekOrigin.Current);
                    }
                }
                return audioStart;
            }

            if (header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C')
            {
                long audioStart = 4; // "fLaC"
                bool lastBlock = false;
                while (!lastBlock)
                {
                    int blockHeader = fs.ReadByte();
                    if (blockHeader < 0) break;
                    lastBlock = (blockHeader & 0x80) != 0;
                    int size = (fs.ReadByte() << 16) | (fs.ReadByte() << 8) | fs.ReadByte();
                    audioStart += 4 + size;
                    fs.Seek(size, SeekOrigin.Current);
                }
                return audioStart;
            }

            return 0;
        }
        catch { return 0; }
    }
}
