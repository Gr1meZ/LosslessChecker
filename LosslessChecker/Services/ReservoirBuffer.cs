using LosslessChecker.Models;

namespace LosslessChecker.Services;

public class ReservoirBuffer
{
    private readonly int _capacity;
    private readonly List<(double rmsDb, float[] data, double startTime)> _heap = new();
    private int _chunkCount;
    private double _maxRmsSeen = -200;

    public ReservoirBuffer(int capacity = 6) => _capacity = capacity;

    public double MaxRmsDb => _maxRmsSeen;
    public int ChunkCount => _chunkCount;

    public IReadOnlyList<float[]> SelectedChunks
    {
        get
        {
            var result = new List<float[]>(_heap.Count);
            foreach (var item in _heap)
                result.Add(item.data);
            return result;
        }
    }

    public void AddChunk(AudioChunk chunk)
    {
        _chunkCount++;
        if (chunk.RmsDb > _maxRmsSeen) _maxRmsSeen = chunk.RmsDb;

        float[] copy;
        if (chunk.IsStereo)
        {
            int n = chunk.FrameCount;
            copy = new float[n];
            var left = chunk.Left.Span;
            var right = chunk.Right.Span;
            for (int i = 0; i < n; i++)
                copy[i] = (left[i] + right[i]) * 0.5f;
        }
        else
        {
            copy = new float[chunk.FrameCount];
            chunk.Left.Span.CopyTo(copy);
        }

        if (_heap.Count < _capacity)
        {
            _heap.Add((chunk.RmsDb, copy, chunk.StartTime));
        }
        else
        {
            int minIdx = 0;
            for (int i = 1; i < _heap.Count; i++)
                if (_heap[i].rmsDb < _heap[minIdx].rmsDb) minIdx = i;
            if (chunk.RmsDb > _heap[minIdx].rmsDb)
                _heap[minIdx] = (chunk.RmsDb, copy, chunk.StartTime);
        }
    }
}
