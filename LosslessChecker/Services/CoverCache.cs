using System.IO;
using System.Windows.Media.Imaging;

namespace LosslessChecker.Services;

public class CoverCache
{
    private readonly int _maxEntries;
    private readonly Dictionary<string, LinkedListNode<(string key, BitmapImage image)>> _dict = new();
    private readonly LinkedList<(string key, BitmapImage image)> _list = new();

    public CoverCache(int maxEntries = 5) => _maxEntries = maxEntries;

    public bool TryGet(string key, out BitmapImage? image)
    {
        if (_dict.TryGetValue(key, out var node)) { _list.Remove(node); _list.AddFirst(node); image = node.Value.image; return true; }
        image = null;
        return false;
    }

    public void Store(string key, byte[] coverData, int decodeWidth)
    {
        if (_dict.TryGetValue(key, out var node)) _list.Remove(node);
        else if (_dict.Count >= _maxEntries) { var last = _list.Last!; _dict.Remove(last.Value.key); _list.RemoveLast(); }

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.DecodePixelWidth = decodeWidth;
        bmp.StreamSource = new MemoryStream(coverData);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();

        var newNode = _list.AddFirst((key, bmp));
        _dict[key] = newNode;
    }

    public void Clear() { _dict.Clear(); _list.Clear(); }
}
