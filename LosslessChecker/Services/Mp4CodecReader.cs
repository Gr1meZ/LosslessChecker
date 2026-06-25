using System.IO;
using System.Text;

namespace LosslessChecker.Services;

public record Mp4CodecInfo(string Codec, int Bitrate, int SampleRate, int Channels, int BitDepth);

public static class Mp4CodecReader
{
    public static Mp4CodecInfo DetectCodec(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return ParseAtoms(fs);
        }
        catch
        {
            return new Mp4CodecInfo("unknown", 0, 0, 0, 0);
        }
    }

    private static Mp4CodecInfo ParseAtoms(FileStream fs)
    {
        if (fs.Length < 8)
            return new Mp4CodecInfo("unknown", 0, 0, 0, 0);

        long moovEnd = FindAtom(fs, 0, "moov");
        if (moovEnd < 0) return new Mp4CodecInfo("unknown", 0, 0, 0, 0);

        // Walk: trak -> mdia -> minf -> stbl -> stsd (first audio track)
        for (int trackIdx = 0; trackIdx < 2; trackIdx++)
        {
            long trakEnd = FindAtom(fs, fs.Position, "trak");
            if (trakEnd < 0) return new Mp4CodecInfo("unknown", 0, 0, 0, 0);

            long mdiaEnd = FindAtom(fs, fs.Position, "mdia");
            if (mdiaEnd < 0) { fs.Seek(trakEnd, SeekOrigin.Begin); continue; }

            long minfEnd = FindAtom(fs, fs.Position, "minf");
            if (minfEnd < 0) { fs.Seek(trakEnd, SeekOrigin.Begin); continue; }

            long stblEnd = FindAtom(fs, fs.Position, "stbl");
            if (stblEnd < 0) { fs.Seek(trakEnd, SeekOrigin.Begin); continue; }

            long stsdEnd = FindAtom(fs, fs.Position, "stsd");
            if (stsdEnd < 0) { fs.Seek(trakEnd, SeekOrigin.Begin); continue; }

            var info = ParseStsd(fs, stsdEnd);
            if (info.Codec != "unknown")
                return info;

            fs.Seek(trakEnd, SeekOrigin.Begin);
        }

        return new Mp4CodecInfo("unknown", 0, 0, 0, 0);
    }

    private static Mp4CodecInfo ParseStsd(FileStream fs, long stsdEnd)
    {
        var verFlags = new byte[4];
        if (fs.Read(verFlags, 0, 4) < 4) return new Mp4CodecInfo("unknown", 0, 0, 0, 0);

        var entryCount = new byte[4];
        if (fs.Read(entryCount, 0, 4) < 4) return new Mp4CodecInfo("unknown", 0, 0, 0, 0);
        int count = (entryCount[0] << 24) | (entryCount[1] << 16) | (entryCount[2] << 8) | entryCount[3];

        if (count < 1) return new Mp4CodecInfo("unknown", 0, 0, 0, 0);

        // Read first entry header
        var entrySize = new byte[4];
        if (fs.Read(entrySize, 0, 4) < 4) return new Mp4CodecInfo("unknown", 0, 0, 0, 0);
        int size = (entrySize[0] << 24) | (entrySize[1] << 16) | (entrySize[2] << 8) | entrySize[3];

        var codec = new byte[4];
        if (fs.Read(codec, 0, 4) < 4) return new Mp4CodecInfo("unknown", 0, 0, 0, 0);
        string codecStr = Encoding.ASCII.GetString(codec);

        long entryStart = fs.Position - 8;

        if (codecStr == "mp4a")
        {
            return ParseAacEntry(fs, entryStart, size);
        }
        else if (codecStr == "alac")
        {
            return ParseAlacEntry(fs, entryStart, size);
        }

        return new Mp4CodecInfo("unknown", 0, 0, 0, 0);
    }

    private static Mp4CodecInfo ParseAacEntry(FileStream fs, long entryStart, int entrySize)
    {
        // After codec type: 6 reserved, 2 dataRefIdx, 2 version, 2 revision, 4 vendor
        // Total skipped: 16 bytes
        fs.Seek(entryStart + 8 + 16, SeekOrigin.Begin);

        var chBuf = new byte[2];
        if (fs.Read(chBuf, 0, 2) < 2) return new Mp4CodecInfo("aac", 0, 44100, 2, 16);
        int channels = (chBuf[0] << 8) | chBuf[1];

        // sampleSize (2 bytes): usually 16
        var sampleSizeBuf = new byte[2];
        fs.Read(sampleSizeBuf, 0, 2);
        int bitDepth = (sampleSizeBuf[0] << 8) | sampleSizeBuf[1];

        // skip: compressionId(2) + reserved(2)
        fs.Seek(4, SeekOrigin.Current);

        // sampleRate: 4 bytes (16.16 fixed-point)
        var srBuf = new byte[4];
        fs.Read(srBuf, 0, 4);
        int sampleRate = (srBuf[0] << 8) | srBuf[1];

        // Find esds atom for bitrate
        int bitrate = 0;
        long savedPos = fs.Position;
        long esdsEnd = FindAtomInRange(fs, entryStart + 8, entryStart + entrySize, "esds");
        if (esdsEnd > 0)
        {
            fs.Seek(4, SeekOrigin.Current); // skip version+flags
            bitrate = ParseEsdsBitrate(fs);
        }

        return new Mp4CodecInfo("aac", bitrate, sampleRate > 0 ? sampleRate : 44100,
            channels > 0 ? channels : 2, bitDepth > 0 ? bitDepth : 16);
    }

    private static int ParseEsdsBitrate(FileStream fs)
    {
        try
        {
            int tag = fs.ReadByte();
            if (tag == 3) // ES_Descriptor
            {
                ReadVarLen(fs);
                fs.Seek(3, SeekOrigin.Current); // ES_ID(2) + flags(1)
                tag = fs.ReadByte();
                if (tag == 4) // DecoderConfigDescriptor
                {
                    ReadVarLen(fs);
                    fs.Seek(2, SeekOrigin.Current); // objectType(1) + streamType(1)
                    // bufferSizeDB(3)
                    var buf = new byte[3];
                    fs.Read(buf, 0, 3);
                    // maxBitrate(4)
                    var br = new byte[4];
                    if (fs.Read(br, 0, 4) < 4) return 0;
                    int maxBr = (br[0] << 24) | (br[1] << 16) | (br[2] << 8) | br[3];
                    // avgBitrate(4)
                    if (fs.Read(br, 0, 4) < 4) return maxBr;
                    int avgBr = (br[0] << 24) | (br[1] << 16) | (br[2] << 8) | br[3];
                    return maxBr > 0 ? maxBr : avgBr;
                }
            }
        }
        catch { }
        return 0;
    }

    private static Mp4CodecInfo ParseAlacEntry(FileStream fs, long entryStart, int entrySize)
    {
        // ALAC atom structure after codec type (32 bytes reserved area):
        // 6 reserved, 2 dataRefIdx, 2 version, 2 revision, 4 vendor = 16 bytes
        // 2 channels, 2 sampleSize, 2 compressionId, 2 reserved = 8 bytes
        // 2 sampleRate (fixed16), 2 reserved = 4 bytes
        // Total fixed: 28 bytes
        const int fixedHeaderSize = 28;

        fs.Seek(entryStart + 8 + fixedHeaderSize, SeekOrigin.Begin);

        // Read ALAC magic cookie: 4 bytes size, 4 bytes 'alac' tag, then ALACSpecificConfig
        var cookieSizeBuf = new byte[4];
        if (fs.Read(cookieSizeBuf, 0, 4) < 4)
            return new Mp4CodecInfo("alac", 0, 44100, 2, 16);

        // ALAC cookie contains: frameLength(4), compatibleVersion(1), bitDepth(1), ...
        long cookieStart = fs.Position;
        fs.Seek(4, SeekOrigin.Current); // skip frameLength
        fs.ReadByte(); // compatibleVersion
        int alacBitDepth = fs.ReadByte(); // bitDepth
        int maxChannels = fs.ReadByte(); // maxChannels
        fs.ReadByte(); // skip

        // maxSampleRate(4 bytes)
        var sr = new byte[4];
        fs.Read(sr, 0, 4);
        int alacSampleRate = (sr[0] << 24) | (sr[1] << 16) | (sr[2] << 8) | sr[3];

        return new Mp4CodecInfo("alac", 0,
            alacSampleRate > 0 ? alacSampleRate : 44100,
            maxChannels > 0 ? maxChannels : 2,
            alacBitDepth > 0 ? alacBitDepth : 16);
    }

    private static long FindAtom(FileStream fs, long searchStart, string atomType)
    {
        fs.Seek(searchStart, SeekOrigin.Begin);
        long parentEnd = searchStart > 0 ? ReadAtomEnd(fs, searchStart) : fs.Length;

        while (fs.Position < Math.Min(parentEnd, fs.Length))
        {
            long atomStart = fs.Position;
            if (atomStart + 8 > fs.Length) break;

            var sizeBuf = new byte[4];
            if (fs.Read(sizeBuf, 0, 4) < 4) break;
            long size = (sizeBuf[0] << 24) | (sizeBuf[1] << 16) | (sizeBuf[2] << 8) | sizeBuf[3];
            if (size < 8) break;

            var typeBuf = new byte[4];
            if (fs.Read(typeBuf, 0, 4) < 4) break;
            string type = Encoding.ASCII.GetString(typeBuf);

            if (type == atomType)
                return atomStart + size;

            if (size == 1)
            {
                // Extended size: next 8 bytes
                var extSize = new byte[8];
                if (fs.Read(extSize, 0, 8) < 8) break;
                long high = (extSize[0] << 24) | (extSize[1] << 16) | (extSize[2] << 8) | extSize[3];
                long low = (extSize[4] << 24) | (extSize[5] << 16) | (extSize[6] << 8) | extSize[7];
                size = (high << 32) | low;
                fs.Seek(atomStart + size, SeekOrigin.Begin);
            }
            else
            {
                fs.Seek(atomStart + size, SeekOrigin.Begin);
            }
        }

        return -1;
    }

    private static long FindAtomInRange(FileStream fs, long rangeStart, long rangeEnd, string atomType)
    {
        fs.Seek(rangeStart, SeekOrigin.Begin);
        while (fs.Position + 8 <= rangeEnd)
        {
            long atomStart = fs.Position;
            var sizeBuf = new byte[4];
            if (fs.Read(sizeBuf, 0, 4) < 4) break;
            long size = (sizeBuf[0] << 24) | (sizeBuf[1] << 16) | (sizeBuf[2] << 8) | sizeBuf[3];
            if (size < 8 || atomStart + size > rangeEnd) { fs.Seek(atomStart + 8, SeekOrigin.Begin); continue; }

            var typeBuf = new byte[4];
            if (fs.Read(typeBuf, 0, 4) < 4) break;
            string type = Encoding.ASCII.GetString(typeBuf);

            if (type == atomType)
                return atomStart + size;

            fs.Seek(atomStart + size, SeekOrigin.Begin);
        }
        return -1;
    }

    private static long ReadAtomEnd(FileStream fs, long atomStart)
    {
        fs.Seek(atomStart, SeekOrigin.Begin);
        var sizeBuf = new byte[4];
        if (fs.Read(sizeBuf, 0, 4) < 4) return atomStart + 8;
        long size = (sizeBuf[0] << 24) | (sizeBuf[1] << 16) | (sizeBuf[2] << 8) | sizeBuf[3];
        return atomStart + (size == 0 ? 8 : size);
    }

    private static int ReadVarLen(FileStream fs)
    {
        int result = 0;
        for (int i = 0; i < 4; i++)
        {
            int b = fs.ReadByte();
            if (b < 0) break;
            result = (result << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return result;
    }
}
