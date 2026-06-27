using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using LosslessChecker.Models;
using NAudio.Wave;

namespace LosslessChecker.Services;

public static partial class AudioDecoder
{
    public static async IAsyncEnumerable<AudioChunk> StreamChunks(
        string filePath,
        int chunkDurationSec = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = CreateReader(filePath)
            ?? throw new InvalidOperationException("Unsupported audio format");

        var format = reader.WaveFormat;
        if (format.Channels > 2)
            throw new NotSupportedException("Only mono and stereo files are supported");

        var provider = reader.ToSampleProvider();
        int sampleRate = format.SampleRate;
        int channels = format.Channels;
        int chunkSize = sampleRate * chunkDurationSec;
        int bufferSize = chunkSize * channels;

        float[] readBuf = ArrayPool<float>.Shared.Rent(bufferSize);
        float[] leftBuf = ArrayPool<float>.Shared.Rent(chunkSize);
        float[] rightBuf = channels == 2 ? ArrayPool<float>.Shared.Rent(chunkSize) : Array.Empty<float>();
        try
        {
            double cumulativeTime = 0;
            int read;
            while ((read = provider.Read(readBuf, 0, bufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int frames = read / channels;
                if (channels == 1)
                {
                    Array.Copy(readBuf, 0, leftBuf, 0, frames);
                    double rmsDb = ComputeRmsMono(leftBuf, frames);
                    yield return new AudioChunk(
                        leftBuf.AsMemory(0, frames), ReadOnlyMemory<float>.Empty,
                        sampleRate, channels, rmsDb, cumulativeTime, false);
                }
                else
                {
                    for (int i = 0; i < frames; i++)
                    {
                        leftBuf[i] = readBuf[i * 2];
                        rightBuf[i] = readBuf[i * 2 + 1];
                    }
                    double rmsDb = ComputeRmsStereo(leftBuf, rightBuf, frames);
                    yield return new AudioChunk(
                        leftBuf.AsMemory(0, frames), rightBuf.AsMemory(0, frames),
                        sampleRate, channels, rmsDb, cumulativeTime, false);
                }
                cumulativeTime += (double)frames / sampleRate;
            }

            yield return new AudioChunk(
                ReadOnlyMemory<float>.Empty, ReadOnlyMemory<float>.Empty,
                sampleRate, channels, -200, cumulativeTime, true);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(readBuf);
            ArrayPool<float>.Shared.Return(leftBuf);
            if (rightBuf.Length > 0) ArrayPool<float>.Shared.Return(rightBuf);
        }
    }

    private static double ComputeRmsMono(float[] buf, int n)
    {
        double sumSq = 0;
        for (int i = 0; i < n; i++) { double s = buf[i]; sumSq += s * s; }
        return 20.0 * Math.Log10(Math.Max(Math.Sqrt(sumSq / n), 1e-10));
    }

    private static double ComputeRmsStereo(float[] left, float[] right, int n)
    {
        double sumSq = 0;
        for (int i = 0; i < n; i++) { double m = (left[i] + right[i]) * 0.5; sumSq += m * m; }
        return 20.0 * Math.Log10(Math.Max(Math.Sqrt(sumSq / n), 1e-10));
    }
}
