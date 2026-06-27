namespace LosslessChecker.Services;

public class SpectrogramCache
{
    private readonly int _maxEntries;
    private readonly Dictionary<string, LinkedListNode<(string key, float[] data)>> _dict = new();
    private readonly LinkedList<(string key, float[] data)> _list = new();

    public SpectrogramCache(int maxEntries = 10) => _maxEntries = maxEntries;

    public bool TryGet(string key, out float[] data)
    {
        if (_dict.TryGetValue(key, out var node))
        {
            _list.Remove(node);
            _list.AddFirst(node);
            data = node.Value.data;
            return true;
        }
        data = Array.Empty<float>();
        return false;
    }

    public void Store(string key, float[] data)
    {
        if (_dict.TryGetValue(key, out var node)) _list.Remove(node);
        else if (_dict.Count >= _maxEntries) { var last = _list.Last!; _dict.Remove(last.Value.key); _list.RemoveLast(); }
        var newNode = _list.AddFirst((key, data));
        _dict[key] = newNode;
    }

    public void Clear() { _dict.Clear(); _list.Clear(); }
}
