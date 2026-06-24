using System.Buffers;
using NWaves.Transforms;
using NWaves.Windows;

namespace LosslessChecker.Services;

public record SpectrogramData(float[] DbValues, int Width, int Height, int SampleRate, double Duration);

public class SpectrogramBuilder
{
    private const int FftSize = 4096;
    private const int HopSize = 1024;
    private const int FreqBins = 1024;
    private const int MaxFrames = 1200;
    private const double DbFloor = -96.0;

    private readonly float[] _window = Window.Hann(FftSize);

    public SpectrogramData Build(float[] samples, int sampleRate)
    {
        int height = FreqBins;
        if (samples.Length < FftSize)
            return new SpectrogramData(Array.Empty<float>(), 0, height, sampleRate, 0);

        int totalFrames = (samples.Length - FftSize) / HopSize + 1;
        int step = Math.Max(1, totalFrames / MaxFrames);
        int width = Math.Min(MaxFrames, (totalFrames + step - 1) / step);

        var dbValues = new float[width * height];
        double globalMaxMag = 1e-10;
        var fft = new Fft(FftSize);

        var frame = ArrayPool<float>.Shared.Rent(FftSize);
        var real = ArrayPool<float>.Shared.Rent(FftSize);
        var imag = ArrayPool<float>.Shared.Rent(FftSize);
        var magnitudes = ArrayPool<double>.Shared.Rent(FftSize / 2);

        try
        {
            int framesBuilt = 0;
            int counter = 0;

            for (int pos = 0; pos + FftSize <= samples.Length && framesBuilt < width; pos += HopSize)
            {
                counter++;
                if (counter % step != 0)
                    continue;

                for (int i = 0; i < FftSize; i++)
                {
                    frame[i] = samples[pos + i] * _window[i];
                    real[i] = frame[i];
                    imag[i] = 0;
                }

                fft.Direct(real, imag);

                for (int i = 0; i < FftSize / 2; i++)
                {
                    magnitudes[i] = Math.Sqrt((double)real[i] * real[i] + (double)imag[i] * imag[i]);
                    if (magnitudes[i] > globalMaxMag)
                        globalMaxMag = magnitudes[i];
                }

                int offset = framesBuilt * height;
                double refMag = Math.Max(globalMaxMag, 1e-10);
                double nyquist = sampleRate / 2.0;
                double logMin = Math.Log10(20.0);
                double logMax = Math.Log10(nyquist);
                double logRange = logMax - logMin;
                double binsPerHz = (double)(FftSize / 2) / nyquist;

                for (int j = 0; j < height; j++)
                {
                    double freq = Math.Pow(10, logMin + logRange * j / (height - 1));
                    double binIdx = freq * binsPerHz;
                    int bin0 = (int)binIdx;
                    int bin1 = Math.Min(bin0 + 1, FftSize / 2 - 1);
                    double frac = binIdx - bin0;

                    bin0 = Math.Clamp(bin0, 0, FftSize / 2 - 1);
                    bin1 = Math.Clamp(bin1, 0, FftSize / 2 - 1);

                    double mag = magnitudes[bin0] + (magnitudes[bin1] - magnitudes[bin0]) * frac;
                    double db = 20.0 * Math.Log10(Math.Max(mag, 1e-10) / refMag);
                    dbValues[offset + j] = (float)Math.Clamp((db - DbFloor) / (-DbFloor), 0, 1);
                }

                framesBuilt++;
            }

            if (globalMaxMag <= 1e-10)
                Array.Clear(dbValues, 0, dbValues.Length);

            double duration = (double)samples.Length / sampleRate;
            return new SpectrogramData(dbValues, framesBuilt, height, sampleRate, duration);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(frame);
            ArrayPool<float>.Shared.Return(real);
            ArrayPool<float>.Shared.Return(imag);
            ArrayPool<double>.Shared.Return(magnitudes);
        }
    }
}
