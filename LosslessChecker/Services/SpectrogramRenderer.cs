using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LosslessChecker.Services;

public class SpectrogramRenderer
{
    private static readonly uint[] ColormapLut = BuildColormapLut();

    public WriteableBitmap Render(float[] dbValues, int dataWidth, int dataHeight)
    {
        if (dataWidth < 1 || dataHeight < 1)
            return new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);

        var bmp = new WriteableBitmap(dataWidth, dataHeight, 96, 96, PixelFormats.Bgra32, null);
        var pixels = ArrayPool<byte>.Shared.Rent(dataWidth * dataHeight * 4);

        try
        {
            var span = pixels.AsSpan(0, dataWidth * dataHeight * 4);

            for (int i = 0; i < span.Length; i += 4)
            {
                span[i] = 0x1B;
                span[i + 1] = 0x11;
                span[i + 2] = 0x11;
                span[i + 3] = 0xFF;
            }

            for (int x = 0; x < dataWidth; x++)
            {
                for (int y = 0; y < dataHeight; y++)
                {
                    float t = dbValues[x * dataHeight + y];
                    int lutIdx = Math.Clamp((int)(t * 255), 0, 255);
                    uint color = ColormapLut[lutIdx];

                    int py = dataHeight - 1 - y;
                    int idx = (py * dataWidth + x) * 4;
                    span[idx] = (byte)(color & 0xFF);
                    span[idx + 1] = (byte)((color >> 8) & 0xFF);
                    span[idx + 2] = (byte)((color >> 16) & 0xFF);
                    span[idx + 3] = 0xFF;
                }
            }

            bmp.Lock();
            Marshal.Copy(pixels, 0, bmp.BackBuffer, dataWidth * dataHeight * 4);
            bmp.AddDirtyRect(new Int32Rect(0, 0, dataWidth, dataHeight));
            bmp.Unlock();
            return bmp;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static uint[] BuildColormapLut()
    {
        var lut = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            var (r, g, b) = HotColormap(t);
            lut[i] = (uint)(r | (g << 8) | (b << 16));
        }
        return lut;
    }

    private static (byte r, byte g, byte b) HotColormap(double t)
    {
        if (t <= 0) return (0, 0, 0);
        if (t < 0.25) { double s = t / 0.25; return ((byte)(255 * s), 0, 0); }
        if (t < 0.5) { double s = (t - 0.25) / 0.25; return (255, (byte)(255 * s), 0); }
        if (t < 0.85) { double s = (t - 0.5) / 0.35; return (255, (byte)(128 + 127 * s), (byte)(255 * s)); }
        double s2 = (t - 0.85) / 0.15;
        return (255, 255, (byte)(128 + 127 * s2));
    }
}
