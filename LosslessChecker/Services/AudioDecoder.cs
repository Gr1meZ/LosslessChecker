using System.IO;
using System.Runtime.InteropServices;
using LosslessChecker.Models;
using NAudio.Wave;

namespace LosslessChecker.Services;

public class AudioDecoder
{
    private const int ReadChunkSize = 16384;

    public static StereoBuffer Decode(string filePath, CancellationToken ct = default)
    {
        using var reader = CreateReader(filePath)
            ?? throw new InvalidOperationException("Unsupported audio format");

        var format = reader.WaveFormat;
        if (format.Channels > 2)
            throw new NotSupportedException("Only mono and stereo files are supported");

        var provider = reader.ToSampleProvider();
        int sampleRate = format.SampleRate;
        int channels = format.Channels;

        long estimatedFrames = (long)(reader.TotalTime.TotalSeconds * sampleRate);
        int initialCapacity = (int)Math.Min(estimatedFrames * channels + ReadChunkSize, int.MaxValue);

        var interleaved = new List<float>(initialCapacity);
        var readBuffer = new float[ReadChunkSize];
        int read;

        while ((read = provider.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            interleaved.AddRange(readBuffer.AsSpan(0, read));
        }

        if (channels == 1)
        {
            var mono = interleaved.ToArray();
            interleaved.Clear();
            return new StereoBuffer(mono, Array.Empty<float>(), sampleRate);
        }

        int frameCount = interleaved.Count / channels;
        var left = new float[frameCount];
        var right = new float[frameCount];

        var span = CollectionsMarshal.AsSpan(interleaved);
        for (int i = 0; i < frameCount; i++)
        {
            left[i] = span[i * channels];
            right[i] = span[i * channels + 1];
        }

        interleaved.Clear();
        return new StereoBuffer(left, right, sampleRate);
    }

    public static float[] DecodeMono(string filePath, CancellationToken ct = default)
    {
        var buffer = Decode(filePath, ct);
        if (!buffer.IsStereo)
            return buffer.Left;

        var mono = new float[buffer.Length];
        var left = buffer.Left;
        var right = buffer.Right;
        for (int i = 0; i < mono.Length; i++)
            mono[i] = (left[i] + right[i]) * 0.5f;
        return mono;
    }

    private static WaveStream? CreateReader(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => new Mp3FileReader(filePath),
            ".wav" => new WaveFileReader(filePath),
            ".flac" => new AudioFileReader(filePath),
            ".m4a" or ".alac" => new AudioFileReader(filePath),
            _ => null
        };
    }
}
