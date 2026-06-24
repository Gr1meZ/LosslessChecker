using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LosslessChecker.Services;

public class SpectrogramRenderer
{
    private const int LeftMargin = 52;
    private const int RightMargin = 24;
    private const int TopMargin = 8;
    private const int BottomMargin = 24;

    public WriteableBitmap Render(
        byte[] flat, int dataWidth, int dataHeight,
        double durationSec, double sampleRate,
        double cutoffHz)
    {
        int totalW = dataWidth + LeftMargin + RightMargin;
        int totalH = dataHeight + TopMargin + BottomMargin;
        var bmp = new WriteableBitmap(totalW, totalH, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[totalW * totalH * 4];

        // Fill background
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 0x1B; pixels[i + 1] = 0x11; pixels[i + 2] = 0x11; pixels[i + 3] = 255; }

        // Draw spectrogram data
        for (int x = 0; x < dataWidth; x++)
        {
            for (int y = 0; y < dataHeight; y++)
            {
                byte dbByte = flat[x * dataHeight + y];
                double t = dbByte / 255.0;
                int py = TopMargin + (dataHeight - 1 - y);
                int px = LeftMargin + x;
                int idx = (py * totalW + px) * 4;
                var (r, g, b) = HotColormap(t);
                pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
            }
        }

        // Draw cutoff line
        double nyquist = sampleRate / 2.0;
        double logMin = Math.Log10(20.0);
        double logRange = Math.Log10(nyquist) - logMin;
        double cutoffRatio = cutoffHz > 0 ? (Math.Log10(Math.Max(cutoffHz, 20.0)) - logMin) / logRange : 0;
        int cutoffY = TopMargin + dataHeight - 1 - (int)(cutoffRatio * (dataHeight - 1));
        cutoffY = Math.Clamp(cutoffY, TopMargin, TopMargin + dataHeight - 1);

        // Dashed red line
        for (int x = 0; x < dataWidth; x++)
        {
            if (x % 8 < 4) continue;
            int idx = (cutoffY * totalW + LeftMargin + x) * 4;
            pixels[idx] = 0xA8; pixels[idx + 1] = 0x8B; pixels[idx + 2] = 0xF3; pixels[idx + 3] = 255;
        }

        // Draw frequency axis labels (Y)
        double[] freqLabels = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
        foreach (var freq in freqLabels)
        {
            if (freq > nyquist) break;
            double ratio = (Math.Log10(freq) - logMin) / logRange;
            int y = TopMargin + dataHeight - 1 - (int)(ratio * (dataHeight - 1));
            string label = freq >= 1000 ? $"{freq / 1000:F0}k" : $"{freq:F0}";
            DrawText(pixels, totalW, label, 2, y - 5, 0xB0, 0x6B, 0x58);
        }

        // Draw time axis labels (X)
        if (durationSec > 0 && dataWidth > 1)
        {
            double interval = durationSec <= 300 ? 30 : 60;
            int labelCount = (int)(durationSec / interval);
            for (int i = 0; i <= labelCount; i++)
            {
                double t = i * interval;
                int x = LeftMargin + (int)(t / durationSec * dataWidth);
                if (x >= LeftMargin + dataWidth) break;
                var ts = TimeSpan.FromSeconds(t);
                string label = ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}"
                    : $"{ts.Minutes}:{ts.Seconds:D2}";
                DrawText(pixels, totalW, label, x - 10, totalH - 18, 0xB0, 0x6B, 0x58);
            }
        }

        // Draw grid lines
        for (int gx = LeftMargin; gx < LeftMargin + dataWidth; gx += dataWidth / 6)
            DrawVLine(pixels, totalW, gx, TopMargin, TopMargin + dataHeight, 0x3A, 0x47, 0x56);

        foreach (var freq in freqLabels)
        {
            if (freq > nyquist) break;
            double ratio = (Math.Log10(freq) - logMin) / logRange;
            int y = TopMargin + dataHeight - 1 - (int)(ratio * (dataHeight - 1));
            DrawHLine(pixels, totalW, LeftMargin, LeftMargin + dataWidth, y, 0x3A, 0x47, 0x56);
        }

        bmp.Lock();
        bmp.WritePixels(new Int32Rect(0, 0, totalW, totalH), pixels, totalW * 4, 0);
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

    private static void DrawText(byte[] pixels, int stride, string text, int x, int y, byte r, byte g, byte b)
    {
        int charW = 6, charH = 8;
        for (int ci = 0; ci < text.Length; ci++)
        {
            int cx = x + ci * charW;
            for (int dy = 0; dy < charH; dy++)
            {
                for (int dx = 0; dx < charW - 1; dx++)
                {
                    int px = cx + dx, py = y + dy;
                    if (px < 0 || px >= stride || py < 0 || py >= pixels.Length / stride / 4) continue;
                    int idx = (py * stride + px) * 4;
                    pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
                }
            }
        }
    }

    private static void DrawHLine(byte[] pixels, int stride, int x1, int x2, int y, byte r, byte g, byte b)
    {
        for (int x = x1; x < x2; x++)
        {
            if (x < 0 || x >= stride || y < 0 || y >= pixels.Length / stride / 4) continue;
            int idx = (y * stride + x) * 4;
            pixels[idx] = (byte)(b / 2); pixels[idx + 1] = (byte)(g / 2); pixels[idx + 2] = (byte)(r / 2);
            pixels[idx + 3] = 128;
        }
    }

    private static void DrawVLine(byte[] pixels, int stride, int x, int y1, int y2, byte r, byte g, byte b)
    {
        for (int y = y1; y < y2; y++)
        {
            if (x < 0 || x >= stride || y < 0 || y >= pixels.Length / stride / 4) continue;
            int idx = (y * stride + x) * 4;
            pixels[idx] = (byte)(b / 2); pixels[idx + 1] = (byte)(g / 2); pixels[idx + 2] = (byte)(r / 2);
            pixels[idx + 3] = 128;
        }
    }
}
