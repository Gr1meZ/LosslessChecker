using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class AnalysisCache
{
    private readonly string _cachePath;
    private Dictionary<string, AnalysisResult> _cache = new();

    public AnalysisCache()
    {
        _cachePath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
            "analysis_cache.json");
        Load();
    }

    public bool TryGet(string filePath, long fileSize, DateTime lastModified, out AnalysisResult? result)
    {
        string key = ComputeKey(filePath, fileSize, lastModified);
        return _cache.TryGetValue(key, out result);
    }

    public void Store(string filePath, long fileSize, DateTime lastModified, AnalysisResult result)
    {
        string key = ComputeKey(filePath, fileSize, lastModified);
        _cache[key] = result;
        Save();
    }

    private static string ComputeKey(string filePath, long fileSize, DateTime lastModified)
    {
        string raw = $"{filePath}|{fileSize}|{lastModified.Ticks}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                _cache = JsonSerializer.Deserialize<Dictionary<string, AnalysisResult>>(json) ?? new();
            }
        }
        catch { _cache = new(); }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cachePath, json);
        }
        catch { }
    }
}
