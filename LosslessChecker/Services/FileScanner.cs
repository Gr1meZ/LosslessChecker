using System.IO;
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class FileScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".alac"
    };

    public List<AudioFileInfo> ScanFolder(string folderPath)
    {
        var files = new List<AudioFileInfo>();

        try
        {
            var allFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories);
            foreach (var filePath in allFiles)
            {
                var ext = Path.GetExtension(filePath);
                if (SupportedExtensions.Contains(ext))
                {
                    var fileInfo = new FileInfo(filePath);
                    files.Add(new AudioFileInfo(filePath, fileInfo.Name, fileInfo.Length));
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return files;
    }
}
