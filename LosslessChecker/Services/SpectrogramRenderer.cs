using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LosslessChecker.Services;

public class SpectrogramRenderer
{
    private static readonly (double t, byte r, byte g, byte b)[] DbColormapPoints = new[]
    {
        (0.000, (byte)0,   (byte)0,   (byte)0),
        (0.083, (byte)0,   (byte)0,   (byte)26),
        (0.167, (byte)0,   (byte)0,   (byte)26),
        (0.250, (byte)0,   (byte)0,   (byte)64),
        (0.333, (byte)0,   (byte)0,   (byte)128),
        (0.417, (byte)0,   (byte)0,   (byte)255),
        (0.500, (byte)75,  (byte)0,   (byte)130),
        (0.583, (byte)128, (byte)0,   (byte)128),
        (0.667, (byte)255, (byte)0,   (byte)255),
        (0.750, (byte)255, (byte)0,   (byte)0),
        (0.833, (byte)255, (byte)128, (byte)0),
        (0.917, (byte)255, (byte)255, (byte)0),
        (1.000, (byte)255, (byte)255, (byte)255),
    };

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
                span[i] = 0;
                span[i + 1] = 0;
                span[i + 2] = 0;
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
                    span[idx] = (byte)((color >> 16) & 0xFF);
                    span[idx + 1] = (byte)((color >> 8) & 0xFF);
                    span[idx + 2] = (byte)(color & 0xFF);
                    span[idx + 3] = 0xFF;
                }
            }

            bmp.Lock();
            Marshal.Copy(pixels, 0, bmp.BackBuffer, dataWidth * dataHeight * 4);
            bmp.AddDirtyRect(new Int32Rect(0, 0, dataWidth, dataHeight));
            bmp.Unlock();
            bmp.Freeze();
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

        int srcTopRow = dataHeight - 1 - (int)(highFreq / nyquist * dataHeight);
        int srcBottomRow = dataHeight - 1 - (int)(lowFreq / nyquist * dataHeight);
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
                span[i] = 0; span[i + 1] = 0; span[i + 2] = 0; span[i + 3] = 0xFF;
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
                    span[idx] = (byte)((color >> 16) & 0xFF);
                    span[idx + 1] = (byte)((color >> 8) & 0xFF);
                    span[idx + 2] = (byte)(color & 0xFF);
                    span[idx + 3] = 0xFF;
                }
            }

            bmp.Lock();
            Marshal.Copy(pixels, 0, bmp.BackBuffer, outWidth * outHeight * 4);
            bmp.AddDirtyRect(new Int32Rect(0, 0, outWidth, outHeight));
            bmp.Unlock();
            bmp.Freeze();
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
            var (r, g, b) = DbColormap(t);
            lut[i] = (uint)(r | (g << 8) | (b << 16));
        }
        return lut;
    }

    internal static (byte r, byte g, byte b) DbColormap(double t)
    {
        if (t <= 0) return (DbColormapPoints[0].r, DbColormapPoints[0].g, DbColormapPoints[0].b);
        if (t >= 1) return (DbColormapPoints[^1].r, DbColormapPoints[^1].g, DbColormapPoints[^1].b);

        for (int i = 1; i < DbColormapPoints.Length; i++)
        {
            if (t <= DbColormapPoints[i].t)
            {
                var prev = DbColormapPoints[i - 1];
                var next = DbColormapPoints[i];
                double s = (t - prev.t) / (next.t - prev.t);
                return (
                    (byte)(prev.r + (next.r - prev.r) * s),
                    (byte)(prev.g + (next.g - prev.g) * s),
                    (byte)(prev.b + (next.b - prev.b) * s));
            }
        }

        return (DbColormapPoints[^1].r, DbColormapPoints[^1].g, DbColormapPoints[^1].b);
    }
}
