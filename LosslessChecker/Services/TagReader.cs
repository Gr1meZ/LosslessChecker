using System.IO;
using TagLib;

namespace LosslessChecker.Services;

public static class TagReader
{
    public record AudioTags(
        string Artist, string Album, string Genre, string Title,
        byte[]? CoverData, string CoverMime, uint Year,
        double? ReplayGainTrackDb);

    public static AudioTags? Read(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var tag = file.Tag;

            string artist = string.Join(", ", tag.Performers) ?? "";
            string album = tag.Album ?? "";
            string genre = string.Join(", ", tag.Genres) ?? "";
            string title = tag.Title ?? Path.GetFileNameWithoutExtension(filePath);
            uint year = tag.Year > 0 ? tag.Year : 0;

            byte[]? cover = null;
            string mime = "";
            if (file.Tag.Pictures.Length > 0)
            {
                cover = file.Tag.Pictures[0].Data.Data;
                mime = file.Tag.Pictures[0].MimeType;
            }

            double? replayGain = ReadReplayGain(filePath);

            return new AudioTags(
                string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
                string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album,
                string.IsNullOrWhiteSpace(genre) ? "" : genre,
                string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(filePath) : title,
                cover, mime, year, replayGain);
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadReplayGain(string filePath)
    {
        try
        {
            using var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
            var buf = new byte[65536];
            int len = fs.Read(buf, 0, buf.Length);
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, len);
            int idx = text.IndexOf("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int eqIdx = text.IndexOf('=', idx);
            if (eqIdx < 0) return null;
            int endIdx = text.IndexOfAny(new[] { '\u0000', '\n', '\r' }, eqIdx);
            if (endIdx < 0) endIdx = Math.Min(eqIdx + 15, text.Length);
            string val = text.Substring(eqIdx + 1, endIdx - eqIdx - 1).Trim();
            val = val.Replace(" dB", "").Trim();
            if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double db))
                return db;
        }
        catch { }
        return null;
    }
}
