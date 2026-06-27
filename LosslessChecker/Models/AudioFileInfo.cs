namespace LosslessChecker.Models;

public record AudioFileInfo(string FilePath, string FileName, long FileSizeBytes, uint Year = 0);
