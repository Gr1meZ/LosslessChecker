namespace LosslessChecker.Models;

public readonly record struct AudioChunk(
    ReadOnlyMemory<float> Left,
    ReadOnlyMemory<float> Right,
    int SampleRate,
    int Channels,
    double RmsDb,
    double StartTime,
    bool IsLast)
{
    public bool IsStereo => Right.Length > 0;
    public int FrameCount => Left.Length;
}
