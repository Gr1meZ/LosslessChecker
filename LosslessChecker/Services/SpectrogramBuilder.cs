using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public class SpectrogramBuilder
{
    private const int FftSize = 4096;
    private const int HopSize = 2048;
    private const int FreqBins = 256;
    private const int MaxFrames = 300;

    private readonly float[] _frame = new float[FftSize];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imag = new float[FftSize];
    private readonly Fft _fft = new(FftSize);
    private readonly float[] _window = Window.Hann(FftSize);

    public (byte[] data, int width, int height) Build(float[] samples, int sampleRate)
    {
        int height = FreqBins;
        if (samples.Length < FftSize)
            return (Array.Empty<byte>(), 0, height);

        int step = Math.Max(1, (samples.Length - FftSize) / HopSize / MaxFrames);
        int maxWidth = Math.Min(MaxFrames, ((samples.Length - FftSize) / HopSize) / step + 1);

        // Pass 1: find global peak
        double globalPeak = 0;
        int counter = 0;
        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            Array.Copy(samples, pos, _frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) _frame[i] *= _window[i];
            Array.Copy(_frame, _real, FftSize);
            Array.Clear(_imag, 0, FftSize);
            _fft.Direct(_real, _imag);
            counter++;
            if (counter % step == 0)
                for (int j = 0; j < FftSize / 2; j++)
                {
                    double m = MathF.Sqrt(_real[j] * _real[j] + _imag[j] * _imag[j]);
                    if (m > globalPeak) globalPeak = m;
                }
        }

        // Pass 2: build flat byte[]
        counter = 0;
        int framesBuilt = 0;
        var flat = new byte[maxWidth * height];
        double refMag = Math.Max(globalPeak, 1e-10);

        for (int pos = 0; pos + FftSize <= samples.Length; pos += HopSize)
        {
            Array.Copy(samples, pos, _frame, 0, FftSize);
            for (int i = 0; i < FftSize; i++) _frame[i] *= _window[i];
            Array.Copy(_frame, _real, FftSize);
            Array.Clear(_imag, 0, FftSize);
            _fft.Direct(_real, _imag);
            counter++;
            if (counter % step == 0 && framesBuilt < maxWidth)
            {
                double ratio = (double)(FftSize / 2) / height;
                int offset = framesBuilt * height;
                for (int j = 0; j < height; j++)
                {
                    int srcIdx = Math.Min((int)(j * ratio), FftSize / 2 - 1);
                    double mag = MathF.Sqrt(_real[srcIdx] * _real[srcIdx] + _imag[srcIdx] * _imag[srcIdx]);
                    double db = 20.0 * Math.Log10(Math.Max(mag, 1e-10) / refMag);
                    flat[offset + j] = (byte)Math.Max(0, Math.Min(255, (int)((db + 96.0) / 96.0 * 255)));
                }
                framesBuilt++;
            }
        }

        return (flat, framesBuilt, height);
    }
}
