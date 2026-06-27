using System;
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

    public WriteableBitmap RenderRegion(float[] dbValues, int dataWidth, int dataHeight,
        double startTime, double endTime, double lowFreq, double highFreq,
        double totalDuration, double nyquist, int targetWidth, int targetHeight)
    {
        int srcStartCol = (int)(startTime / totalDuration * dataWidth);
        int srcEndCol = (int)(endTime / totalDuration * dataWidth);
        srcStartCol = Math.Clamp(srcStartCol, 0, dataWidth - 1);
        srcEndCol = Math.Clamp(srcEndCol, 0, dataWidth);

        double logMin = Math.Log10(20.0);
        double logMax = Math.Log10(nyquist);
        double logRange = logMax - logMin;
        int srcTopRow = dataHeight - 1 - (int)((Math.Log10(highFreq) - logMin) / logRange * dataHeight);
        int srcBottomRow = dataHeight - 1 - (int)((Math.Log10(lowFreq) - logMin) / logRange * dataHeight);
        srcTopRow = Math.Clamp(srcTopRow, 0, dataHeight - 1);
        srcBottomRow = Math.Clamp(srcBottomRow, 0, dataHeight - 1);
        if (srcTopRow > srcBottomRow) (srcTopRow, srcBottomRow) = (srcBottomRow, srcTopRow);

        int srcWidth = srcEndCol - srcStartCol;
        int srcHeight = srcBottomRow - srcTopRow + 1;
        if (srcWidth < 1 || srcHeight < 1) return new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);

        int outWidth = Math.Max(1, targetWidth);
        int outHeight = Math.Max(1, targetHeight);

        var bmp = new WriteableBitmap(outWidth, outHeight, 96, 96, PixelFormats.Bgra32, null);
        var pixels = ArrayPool<byte>.Shared.Rent(outWidth * outHeight * 4);
        try
        {
            var span = pixels.AsSpan(0, outWidth * outHeight * 4);
            for (int i = 0; i < span.Length; i += 4)
            {
                span[i] = 0x1B; span[i + 1] = 0x11; span[i + 2] = 0x11; span[i + 3] = 0xFF;
            }

            for (int ox = 0; ox < outWidth; ox++)
            {
                int sx = srcStartCol + (int)((double)ox / outWidth * srcWidth);
                sx = Math.Clamp(sx, 0, dataWidth - 1);
                for (int oy = 0; oy < outHeight; oy++)
                {
                    int sy = srcTopRow + (int)((double)oy / outHeight * srcHeight);
                    sy = Math.Clamp(sy, 0, dataHeight - 1);
                    float t = dbValues[sx * dataHeight + sy];
                    int lutIdx = Math.Clamp((int)(t * 255), 0, 255);
                    int idx = ((outHeight - 1 - oy) * outWidth + ox) * 4;
                    uint color = ColormapLut[lutIdx];
                    span[idx] = (byte)(color & 0xFF);
                    span[idx + 1] = (byte)((color >> 8) & 0xFF);
                    span[idx + 2] = (byte)((color >> 16) & 0xFF);
                    span[idx + 3] = 0xFF;
                }
            }

            bmp.Lock();
            Marshal.Copy(pixels, 0, bmp.BackBuffer, outWidth * outHeight * 4);
            bmp.AddDirtyRect(new Int32Rect(0, 0, outWidth, outHeight));
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
