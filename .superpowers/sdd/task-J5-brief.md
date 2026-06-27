# Task J5: AudioFileViewModel cache integration

Modify `LosslessChecker/ViewModels/AudioFileViewModel.cs`:

1. Remove `_rawSpectro` float[] field storage. Replace with:
```csharp
private string? _rawSpectroKey;
private static readonly SpectrogramCache _spectroCache = new();
private static readonly CoverCache _coverCache = new();
```

2. In ApplyResult, replace raw spectro storage with cache:
```csharp
if (r.SpectrogramDb is { Length: > 0 })
{
    string cacheKey = $"{r.FilePath}|{r.SpectrogramWidth}|{r.SpectrogramHeight}";
    if (!_spectroCache.TryGet(cacheKey, out _))
        _spectroCache.Store(cacheKey, r.SpectrogramDb);
    _rawSpectroKey = cacheKey;
    _spectroWidth = r.SpectrogramWidth;
    _spectroHeight = r.SpectrogramHeight;
}
```

3. In ApplyResult, for cover data use CoverCache:
```csharp
if (r.CoverData is { Length: > 0 })
{
    string coverKey = $"cover_{r.FilePath}";
    if (!_coverCache.TryGet(coverKey, out _))
        _coverCache.Store(coverKey, r.CoverData, 150);
}
```

4. Update GetOrBuildSpectrogram to use cache:
```csharp
public WriteableBitmap? GetOrBuildSpectrogram()
{
    if (SpectrogramBitmap != null) return SpectrogramBitmap;
    if (_rawSpectroKey == null) return null;
    if (!_spectroCache.TryGet(_rawSpectroKey, out var rawSpectro) || rawSpectro == null) return null;
    var bmp = _spectroRenderer.Render(rawSpectro, _spectroWidth, _spectroHeight);
    SpectrogramBitmap = bmp;
    return bmp;
}
```

Report: `D:\Projects\LosslessChecker\LosslessChecker\.superpowers\sdd\task-J5-report.md`
Commit: `feat(J5): перевести AudioFileViewModel на LRU-кэш спектрограмм и обложек с DecodePixelWidth`
