using System.Globalization;

namespace LosslessChecker.Services;

public class LocalizationService
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());
    public static LocalizationService Instance => _instance.Value;

    private readonly Dictionary<string, string> _cache = new();
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    private LocalizationService()
    {
        var rm = new System.Resources.ResourceManager("LosslessChecker.Resources.Strings", typeof(LocalizationService).Assembly);
        var set = rm.GetResourceSet(Culture, true, true);
        if (set != null)
        {
            foreach (System.Collections.DictionaryEntry entry in set)
                _cache[(string)entry.Key] = (string)entry.Value!;
        }
    }

    public string Get(string key, params object[] args) =>
        string.Format(Culture, _cache.TryGetValue(key, out var val) ? val : key, args);
}
