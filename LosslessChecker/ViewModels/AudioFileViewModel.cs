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
    private bool _hasArtifacts;

    [ObservableProperty]
    private string _artifactLevel = "";

    [ObservableProperty]
    private AnalysisStatus _analysisStatus = AnalysisStatus.Pending;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private double[] _averagedSpectrum = Array.Empty<double>();

    [ObservableProperty]
    private WriteableBitmap? _spectrogramBitmap;

    public string FilePath { get; }

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
        DynamicRange = result.DynamicRange;
        TruePeak = result.TruePeak;
        ClippingPercent = result.ClippingPercent;
        LosslessScore = result.LosslessScore;
        StatusMessage = result.Status;
        HasArtifacts = result.HasArtifacts;
        ArtifactLevel = result.ArtifactLevel;
        AnalysisStatus = result.AnalysisStatus;
        ErrorMessage = result.ErrorMessage ?? "";
        AveragedSpectrum = result.AveragedSpectrum;

        if (result.SpectrogramData is { Length: > 0 } frames)
        {
            SpectrogramBitmap = BuildSpectrogramBitmap(frames);
        }
    }

    private static WriteableBitmap BuildSpectrogramBitmap(double[][] frames)
    {
        int width = frames.Length;
        int height = frames[0].Length;
        if (width < 1 || height < 1)
            return null!;

        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];

        // Find global dB range
        var allMags = frames.SelectMany(f => f).Where(v => v > 1e-10).ToList();
        if (allMags.Count == 0) return bmp;
        double maxMag = allMags.Max();
        double minDb = 20.0 * Math.Log10(allMags.Min() / maxMag);
        double dbRange = -minDb;
        if (dbRange < 1) dbRange = 1;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                double mag = frames[x][y];
                double db = 20.0 * Math.Log10(Math.Max(mag, 1e-10) / maxMag);
                double t = Math.Max(0, Math.Min(1, (db - minDb) / dbRange));

                // Inverted: bottom = 0 Hz, top = Nyquist
                int py = height - 1 - y;
                int idx = (py * width + x) * 4;

                // Viridis-like colormap
                (byte r, byte g, byte b) = ViridisColor(t);
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

    private static (byte r, byte g, byte b) ViridisColor(double t)
    {
        // Simplified viridis: dark blue → green → yellow
        if (t < 0.25)
        {
            double s = t / 0.25;
            return ((byte)(72 * s), (byte)(35 + 109 * s), (byte)(143 - 16 * s));
        }
        if (t < 0.5)
        {
            double s = (t - 0.25) / 0.25;
            return ((byte)(72 + 110 * s), (byte)(144 + 52 * s), (byte)(127 - 90 * s));
        }
        if (t < 0.75)
        {
            double s = (t - 0.5) / 0.25;
            return ((byte)(182 + 69 * s), (byte)(196 + 24 * s), (byte)(37 - 10 * s));
        }
        else
        {
            double s = (t - 0.75) / 0.25;
            return ((byte)(251 - 2 * s), (byte)(220 + 11 * s), (byte)(27 - 2 * s));
        }
    }
}
