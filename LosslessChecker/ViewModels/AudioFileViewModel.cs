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
    private System.Windows.Media.PointCollection? _spectrumPoints;

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

        if (result.AveragedSpectrum.Length > 0)
        {
            var points = new System.Windows.Media.PointCollection();
            for (int i = 0; i < result.AveragedSpectrum.Length; i++)
            {
                points.Add(new System.Windows.Point(i, result.AveragedSpectrum[i]));
            }
            SpectrumPoints = points;
        }
    }
}
