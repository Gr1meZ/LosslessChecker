using System.IO;
using LosslessChecker.Models;
using NAudio.Wave;

namespace LosslessChecker.Services;

public class AudioDecoder
{
    public static StereoBuffer Decode(string filePath, CancellationToken ct = default)
    {
        using var reader = CreateReader(filePath)
            ?? throw new InvalidOperationException("Unsupported audio format");

        var format = reader.WaveFormat;
        var provider = reader.ToSampleProvider();

        if (format.Channels > 2)
            throw new NotSupportedException("Only mono and stereo files are supported");

        if (format.Channels == 1)
        {
            var buffer = new float[4096];
            int read;
            int totalSamples = (int)(reader.TotalTime.TotalSeconds * format.SampleRate);
            var mono = new List<float>(totalSamples);
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException();
                for (int i = 0; i < read; i++) mono.Add(buffer[i]);
            }
            return new StereoBuffer(mono.ToArray(), Array.Empty<float>(), format.SampleRate);
        }
        else
        {
            // Stereo: decode and mix to mono on the fly
            var buffer = new float[8192];
            int totalSamples = (int)(reader.TotalTime.TotalSeconds * format.SampleRate);
            var mono = new List<float>(totalSamples);
            int stereoRead;
            while ((stereoRead = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException();
                for (int i = 0; i < stereoRead; i += format.Channels)
                {
                    float s = buffer[i];
                    if (i + 1 < stereoRead)
                        s = (s + buffer[i + 1]) * 0.5f;
                    mono.Add(s);
                }
            }
            return new StereoBuffer(mono.ToArray(), Array.Empty<float>(), format.SampleRate);
        }
    }

    public static float[] DecodeMono(string filePath, CancellationToken ct = default)
    {
        return Decode(filePath, ct).Left;
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
