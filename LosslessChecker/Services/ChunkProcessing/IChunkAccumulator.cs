namespace LosslessChecker.Services.ChunkProcessing;

public interface IChunkAccumulator<out TResult>
{
    void AddChunk(ReadOnlySpan<float> mono);
    TResult GetResult();
}
