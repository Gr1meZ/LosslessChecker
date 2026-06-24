using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LosslessChecker.Services;

public class SpectrogramRenderer
{
    public WriteableBitmap Render(byte[] flat, int dataWidth, int dataHeight)
    {
        var bmp = new WriteableBitmap(dataWidth, dataHeight, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[dataWidth * dataHeight * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 0x1B; pixels[i + 1] = 0x11; pixels[i + 2] = 0x11; pixels[i + 3] = 255; }

        for (int x = 0; x < dataWidth; x++)
        {
            for (int y = 0; y < dataHeight; y++)
            {
                byte dbByte = flat[x * dataHeight + y];
                double t = dbByte / 255.0;
                int py = dataHeight - 1 - y;
                int idx = (py * dataWidth + x) * 4;
                var (r, g, b) = HotColormap(t);
                pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
            }
        }

        bmp.Lock();
        bmp.WritePixels(new Int32Rect(0, 0, dataWidth, dataHeight), pixels, dataWidth * 4, 0);
        bmp.Unlock();
        return bmp;
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
