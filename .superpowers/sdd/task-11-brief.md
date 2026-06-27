# Task 11: JSON Cache Implementation

**Files:**
- Create: `LosslessChecker/Services/AnalysisCache.cs`
- Modify: `LosslessChecker/Services/AudioAnalyzer.cs`

## Step 1: Create AnalysisCache.cs

New file `LosslessChecker/Services/AnalysisCache.cs`:

```csharp
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
```

## Step 2: Wire cache into AudioAnalyzer.cs

Replace the entire `AudioAnalyzer.cs` content:

```csharp
using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class AudioAnalyzer
{
    private readonly AudioPipeline _pipeline = new();
    private readonly AnalysisCache _cache = new();

    public AnalysisResult Analyze(AudioFileInfo fileInfo, CancellationToken ct = default)
    {
        var fileInfo2 = new System.IO.FileInfo(fileInfo.FilePath);
        if (fileInfo2.Exists && _cache.TryGet(fileInfo.FilePath, fileInfo2.Length, fileInfo2.LastWriteTime, out var cached))
            return cached!;

        var result = _pipeline.Analyze(fileInfo, ct);
        if (fileInfo2.Exists && result.AnalysisStatus == AnalysisStatus.Completed)
            _cache.Store(fileInfo.FilePath, fileInfo2.Length, fileInfo2.LastWriteTime, result);

        return result;
    }
}
```

## Step 3: Build and test

Run: `dotnet build`
Expected: Build succeeds.

Run: `dotnet test`
Expected: All tests pass.

## Step 4: Commit

```bash
git add LosslessChecker/Services/AnalysisCache.cs LosslessChecker/Services/AudioAnalyzer.cs
git commit -m "feat: add JSON analysis cache to skip re-analysis of unchanged files"
```
