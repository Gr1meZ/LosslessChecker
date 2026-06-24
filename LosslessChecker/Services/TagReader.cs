using System.IO;
using TagLib;

namespace LosslessChecker.Services;

public static class TagReader
{
    public record AudioTags(
        string Artist, string Album, string Genre, string Title,
        byte[]? CoverData, string CoverMime);

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

            byte[]? cover = null;
            string mime = "";
            if (file.Tag.Pictures.Length > 0)
            {
                cover = file.Tag.Pictures[0].Data.Data;
                mime = file.Tag.Pictures[0].MimeType;
            }

            return new AudioTags(
                string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
                string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album,
                string.IsNullOrWhiteSpace(genre) ? "" : genre,
                string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(filePath) : title,
                cover, mime);
        }
        catch
        {
            return null;
        }
    }
}
