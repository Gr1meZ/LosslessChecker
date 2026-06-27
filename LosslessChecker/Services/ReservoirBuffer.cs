using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class ReservoirBuffer
{
    private readonly int _capacity;
    private readonly List<(double rmsDb, float[] data, double startTime)> _heap = new();
    private readonly List<(double rmsDb, double startTime, int dataOffset)> _allChunks = new();
    private readonly List<float[]> _allData = new();
    private int _chunkCount;

    public ReservoirBuffer(int capacity = 6) => _capacity = capacity;

    public bool IsEmpty => _chunkCount == 0;
    public int ChunkCount => _chunkCount;
    public double MaxRmsDb { get; private set; } = -200;

    public IReadOnlyList<float[]> SelectedChunks
    {
        get
        {
            var result = new List<float[]>();
            if (MaxRmsDb < -40 && _chunkCount > _capacity * 2)
            {
                int step = _chunkCount / _capacity;
                for (int i = 0; i < _capacity; i++)
                    result.Add(_allData[Math.Min(i * step + step / 2, _allData.Count - 1)]);
            }
            else
            {
                foreach (var item in _heap)
                    result.Add(item.data);
            }
            return result;
        }
    }

    public IReadOnlyList<double> SelectedStartTimes
    {
        get
        {
            var result = new List<double>();
            if (MaxRmsDb < -40 && _chunkCount > _capacity * 2)
            {
                int step = _chunkCount / _capacity;
                for (int i = 0; i < _capacity; i++)
                {
                    int idx = Math.Min(i * step + step / 2, _allChunks.Count - 1);
                    result.Add(idx >= 0 ? _allChunks[idx].startTime : 0);
                }
            }
            else
            {
                foreach (var item in _heap)
                    result.Add(item.startTime);
            }
            return result;
        }
    }

    public void AddChunk(AudioChunk chunk)
    {
        _chunkCount++;
        if (chunk.RmsDb > MaxRmsDb) MaxRmsDb = chunk.RmsDb;
        _allChunks.Add((chunk.RmsDb, chunk.StartTime, _allData.Count));

        if (chunk.IsStereo)
        {
            int n = chunk.FrameCount;
            var copy = new float[n];
            var left = chunk.Left.Span;
            var right = chunk.Right.Span;
            for (int i = 0; i < n; i++)
                copy[i] = (left[i] + right[i]) * 0.5f;
            _allData.Add(copy);
            InsertHeap(chunk.RmsDb, copy, chunk.StartTime);
        }
        else
        {
            int n = chunk.FrameCount;
            var copy = new float[n];
            chunk.Left.Span.CopyTo(copy);
            _allData.Add(copy);
            InsertHeap(chunk.RmsDb, copy, chunk.StartTime);
        }
    }

    private void InsertHeap(double rmsDb, float[] data, double startTime)
    {
        if (_heap.Count < _capacity)
        {
            _heap.Add((rmsDb, data, startTime));
        }
        else
        {
            int minIdx = 0;
            for (int i = 1; i < _heap.Count; i++)
                if (_heap[i].rmsDb < _heap[minIdx].rmsDb) minIdx = i;
            if (rmsDb > _heap[minIdx].rmsDb)
                _heap[minIdx] = (rmsDb, data, startTime);
        }
    }
}
