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
        int totalFrames = (int)(reader.TotalTime.TotalSeconds * format.SampleRate);
        var left = new List<float>(totalFrames);
        var right = new List<float>(totalFrames);

        if (format.Channels > 2)
            throw new NotSupportedException("Only mono and stereo files are supported");

        if (format.Channels == 1)
        {
            var buffer = new float[4096];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException();
                for (int i = 0; i < read; i++) left.Add(buffer[i]);
            }

            // Debug: log overall level
            LogLevel("mono", left, format.SampleRate);

            return new StereoBuffer(left.ToArray(), Array.Empty<float>(), format.SampleRate);
        }

        // Stereo: interleaved L/R
        var stereoBuffer = new float[8192];
        int stereoRead;
        while ((stereoRead = provider.Read(stereoBuffer, 0, stereoBuffer.Length)) > 0)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException();
            for (int i = 0; i < stereoRead; i += format.Channels)
            {
                left.Add(stereoBuffer[i]);
                if (i + 1 < stereoRead)
                    right.Add(stereoBuffer[i + 1]);
            }
        }

        // Debug: log overall level
        LogLevel("L", left, format.SampleRate);
        LogLevel("R", right, format.SampleRate);

        return new StereoBuffer(left.ToArray(), right.ToArray(), format.SampleRate);
    }

    private static void LogLevel(string label, List<float> samples, int sampleRate)
    {
        try
        {
            var arr = samples.ToArray();
            double sumSq = 0, maxAbs = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                double abs = Math.Abs(arr[i]);
                if (abs > maxAbs) maxAbs = abs;
                sumSq += (double)arr[i] * arr[i];
            }
            double rms = Math.Sqrt(sumSq / arr.Length);
            double peakDb = 20.0 * Math.Log10(Math.Max(maxAbs, 1e-10));
            double rmsDb = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lossless_dr_debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} LEVEL[{label}]: peak={peakDb:F2} rms={rmsDb:F2} nsamples={arr.Length} sr={sampleRate} first5=[{arr[0]:F6},{arr[1]:F6},{arr[2]:F6},{arr[3]:F6},{arr[4]:F6}]{Environment.NewLine}");
        }
        catch { }
    }

    public static float[] DecodeMono(string filePath, CancellationToken ct = default)
    {
        var stereo = Decode(filePath, ct);
        int n = stereo.Length;
        var mono = new float[n];
        for (int i = 0; i < n; i++)
            mono[i] = (stereo.Left[i] + stereo.Right[i]) * 0.5f;
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
