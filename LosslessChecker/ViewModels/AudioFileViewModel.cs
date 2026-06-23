using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LosslessChecker.Models;

namespace LosslessChecker.ViewModels;

public partial class AudioFileViewModel : ObservableObject
{
    [ObservableProperty]
    private string _fileName = "";

    [ObservableProperty]
    private string _format = "";

    [ObservableProperty]
    private double _cutoffFrequency;

    [ObservableProperty]
    private double _cutoffSlope;

    [ObservableProperty]
    private double _dynamicRange;

    [ObservableProperty]
    private double _truePeak;

    [ObservableProperty]
    private double _clippingPercent;

    [ObservableProperty]
    private double _losslessScore;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _verdict = "";

    [ObservableProperty]
    private bool _hasArtifacts;

    [ObservableProperty]
    private string _artifactLevel = "";

    [ObservableProperty]
    private AnalysisStatus _analysisStatus = AnalysisStatus.Pending;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _bitDepthSuspicious;

    [ObservableProperty]
    private double _noiseFloorDb;

    [ObservableProperty]
    private bool _isUpscale;

    [ObservableProperty]
    private WriteableBitmap? _spectrogramBitmap;

    public string FilePath { get; }

    // Raw spectrogram data stored as byte[] (dB-quantized), built into bitmap lazily
    internal byte[][]? RawSpectrogram { get; private set; }

    public AudioFileViewModel(AudioFileInfo fileInfo)
    {
        FilePath = fileInfo.FilePath;
        _fileName = fileInfo.FileName;
    }

    public void ApplyResult(AnalysisResult result)
    {
        FileName = result.FileName;
        Format = result.Format;
        CutoffFrequency = result.CutoffFrequency;
        CutoffSlope = result.CutoffSlope;
        DynamicRange = result.DynamicRange;
        TruePeak = result.TruePeak;
        ClippingPercent = result.ClippingPercent;
        LosslessScore = result.LosslessScore;
        StatusMessage = result.Status;
        Verdict = result.Verdict;
        HasArtifacts = result.HasArtifacts;
        ArtifactLevel = result.ArtifactLevel;
        AnalysisStatus = result.AnalysisStatus;
        ErrorMessage = result.ErrorMessage ?? "";
        BitDepthSuspicious = result.BitDepthSuspicious;
        NoiseFloorDb = result.NoiseFloorDb;
        IsUpscale = result.IsUpscale;

        // Store raw spectrogram data only — bitmap built on demand
        if (result.SpectrogramData is { Length: > 0 })
            RawSpectrogram = result.SpectrogramData;
    }

    public WriteableBitmap? GetOrBuildSpectrogram()
    {
        if (SpectrogramBitmap != null)
            return SpectrogramBitmap;

        if (RawSpectrogram is not { Length: > 0 } frames || frames[0].Length == 0)
            return null;

        SpectrogramBitmap = BuildBitmap(frames);

        // Free raw data after bitmap is built (user is viewing it)
        RawSpectrogram = null;
        return SpectrogramBitmap;
    }

    private static WriteableBitmap BuildBitmap(byte[][] frames)
    {
        int width = frames.Length;
        int height = frames[0].Length;

        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                byte dbByte = frames[x][y];
                double t = dbByte / 255.0;

                int py = height - 1 - y;
                int idx = (py * width + x) * 4;

                var (r, g, b) = HotColormap(t);
                pixels[idx + 0] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 255;
            }
        }

        bmp.Lock();
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
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
