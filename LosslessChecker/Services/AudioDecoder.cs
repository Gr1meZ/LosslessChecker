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
        if (estimatedFrames < 1) estimatedFrames = sampleRate * 60;
        int initialCapacity = (int)Math.Min(estimatedFrames + ReadChunkSize, int.MaxValue - ReadChunkSize);

        var left = new float[initialCapacity];
        var right = channels == 2 ? new float[initialCapacity] : Array.Empty<float>();
        int frameCount = 0;

        var readBuffer = new float[ReadChunkSize * channels];
        int read;

        while ((read = provider.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int frames = read / channels;

            if (frameCount + frames > left.Length)
            {
                int newSize = (int)Math.Min((long)left.Length * 2, int.MaxValue);
                if (newSize < frameCount + frames)
                    newSize = frameCount + frames;
                Array.Resize(ref left, newSize);
                if (channels == 2)
                    Array.Resize(ref right, newSize);
            }

            if (channels == 1)
            {
                Array.Copy(readBuffer, 0, left, frameCount, frames);
            }
            else
            {
                for (int i = 0; i < frames; i++)
                {
                    left[frameCount + i] = readBuffer[i * 2];
                    right[frameCount + i] = readBuffer[i * 2 + 1];
                }
            }

            frameCount += frames;
        }

        if (frameCount == 0)
            return new StereoBuffer(Array.Empty<float>(), Array.Empty<float>(), sampleRate);

        if (frameCount < left.Length)
        {
            Array.Resize(ref left, frameCount);
            if (channels == 2)
                Array.Resize(ref right, frameCount);
        }

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
