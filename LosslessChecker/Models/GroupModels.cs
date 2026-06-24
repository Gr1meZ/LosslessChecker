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
}
