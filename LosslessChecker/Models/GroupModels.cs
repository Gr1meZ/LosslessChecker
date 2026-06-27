using System.Collections.ObjectModel;

namespace LosslessChecker.Models;

public class ArtistGroup
{
    public string ArtistName { get; set; } = "";
    public ObservableCollection<AlbumGroup> Albums { get; set; } = new();
}

public class AlbumGroup
{
    public string AlbumName { get; set; } = "";
    public string Genre { get; set; } = "";
    public byte[]? CoverData { get; set; }
    public ObservableCollection<ViewModels.AudioFileViewModel> Tracks { get; set; } = new();
    public double AverageLosslessScore { get; set; }
    public double AverageQualityScore { get; set; }
    public double AverageDynamicRange { get; set; }
    public string AlbumVerdict { get; set; } = "";
    public int KeepCount { get; set; }
    public int InvestigateCount { get; set; }
    public int ReplaceCount { get; set; }
    public double WorstTrackScore { get; set; }
    public string WorstTrackDecision { get; set; } = "";
}
