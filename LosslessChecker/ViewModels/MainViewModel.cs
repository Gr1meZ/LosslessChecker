using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LosslessChecker.Models;
using LosslessChecker.Services;

namespace LosslessChecker.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly FileScanner _scanner = new();
    private readonly AudioAnalyzer _analyzer = new();
    private readonly IDialogService _dialogService;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private RangeObservableCollection<AudioFileViewModel> _files = new();

    private HashSet<AudioFileViewModel>? _selectionFilter;

    public ICollectionView FilesView { get; }

    [ObservableProperty]
    private ObservableCollection<ArtistGroup> _artistGroups = new();

    [ObservableProperty]
    private AlbumGroup? _selectedGroup;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _showWelcome = false;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _sortColumn = "FileName";

    [ObservableProperty]
    private bool _sortAscending = true;

    public MainViewModel() : this(new DialogService()) { }

    public MainViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        FilesView = CollectionViewSource.GetDefaultView(_files);
        FilesView.Filter = FilterFile;
    }

    private bool FilterFile(object obj)
    {
        if (obj is not AudioFileViewModel f)
            return false;

        if (_selectionFilter != null && !_selectionFilter.Contains(f))
            return false;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            if (!f.FileName.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) &&
                (f.Artist?.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ?? false) == false &&
                (f.Album?.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ?? false) == false)
                return false;
        }

        return true;
    }

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private int _processedFiles;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _summaryText = "Ready";

    [ObservableProperty]
    private string _currentlyProcessing = "";

    public string ProgressText => IsProcessing
        ? $"{ProcessedFiles}/{TotalFiles} ({Progress:F0}%)"
        : "";

    [ObservableProperty]
    private int _keepCount;

    [ObservableProperty]
    private int _investigateCount;

    [ObservableProperty]
    private int _replaceCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private AudioFileViewModel? _selectedFile;

    [ObservableProperty]
    private double _selectedCutoffFrequency;

    [ObservableProperty]
    private double _selectedNyquist = 22050;

    [ObservableProperty]
    private bool _isSpectrumVisible;

    [ObservableProperty]
    private WriteableBitmap? _selectedSpectrogram;

    [ObservableProperty]
    private string _spectrumTitle = "";

    public void OnSelectionChanged(AudioFileViewModel? selected)
    {
        SelectedFile = selected;
        if (selected == null)
        {
            IsSpectrumVisible = false;
            return;
        }

        SelectedCutoffFrequency = selected.CutoffFrequency;
        SelectedNyquist = selected.SampleRate > 0 ? selected.SampleRate / 2.0 : 22050;
        SpectrumTitle = selected.FileName;
        IsSpectrumVisible = true;
        selected.GetOrBuildSpectrogram();
    }

    [RelayCommand]
    private void OpenSpectrogram()
    {
        if (SelectedFile?.RawSpectrogram is { Length: > 0 })
        {
            _dialogService.ShowSpectrogram(
                SelectedFile.RawSpectrogram,
                SelectedFile.SpectroWidth,
                SelectedFile.SpectroHeight,
                SelectedFile.DurationSeconds,
                SelectedFile.SampleRate,
                SelectedFile.CutoffFrequency,
                SelectedFile.FileName);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedFile?.FilePath is string path)
            System.Diagnostics.Process.Start("explorer", $"/select,\"{path}\"");
    }

    public void OnTreeSelectionChanged(object? selectedItem)
    {
        switch (selectedItem)
        {
            case ArtistGroup artist:
                SelectedGroup = null;
                SelectedFile = null;
                _selectionFilter = new HashSet<AudioFileViewModel>(
                    artist.Albums.SelectMany(a => a.Tracks));
                break;
            case AlbumGroup album:
                SelectedGroup = album;
                SelectedFile = null;
                _selectionFilter = new HashSet<AudioFileViewModel>(album.Tracks);
                break;
            case AudioFileViewModel track:
                SelectedGroup = null;
                _selectionFilter = new HashSet<AudioFileViewModel> { track };
                SelectedFile = track;
                break;
            default:
                SelectedGroup = null;
                _selectionFilter = null;
                break;
        }
        ApplyFilters();
    }

    public void ApplyFilters()
    {
        FilesView.SortDescriptions.Clear();
        var direction = SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        FilesView.SortDescriptions.Add(new SortDescription(SortColumn, direction));
        FilesView.Refresh();
    }

    public void SortFiles(string columnName)
    {
        if (SortColumn == columnName)
            SortAscending = !SortAscending;
        else
            (SortColumn, SortAscending) = (columnName, true);

        ApplyFilters();
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilters();

    [RelayCommand]
    private async Task SelectFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder with audio files",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            folderPath = dialog.SelectedPath;
        }

        ShowWelcome = false;
        await ScanAndAnalyze(folderPath);
    }

    [RelayCommand]
    private async Task SelectFiles()
    {
        var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Multiselect = true,
            Title = "Выберите аудиофайлы",
            Filter = "Audio Files|*.mp3;*.flac;*.wav;*.m4a;*.alac|All Files|*.*"
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        ShowWelcome = false;
        Files.Clear();
        ArtistGroups.Clear();
        _selectionFilter = null;
        FilesView.Refresh();
        SelectedGroup = null;
        ProcessedFiles = 0;
        ErrorCount = 0;
        KeepCount = 0;
        InvestigateCount = 0;
        ReplaceCount = 0;
        CurrentlyProcessing = "";
        UpdateSummary();

        await ScanAndAppend(dialog.FileNames);
    }

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV File|*.csv|JSON File|*.json|HTML Report|*.html",
            Title = "Export results",
            FileName = "lossless_checker_report"
        };
        if (dialog.ShowDialog() != true) return;

        var data = Files.Where(f => f.AnalysisStatus == Models.AnalysisStatus.Completed).ToList();
        if (data.Count == 0) return;

        var ext = System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant();
        if (ext == ".csv") ExportCsv(dialog.FileName, data);
        else if (ext == ".json") ExportJson(dialog.FileName, data);
        else if (ext == ".html") ExportHtml(dialog.FileName, data);
    }

    [RelayCommand]
    private void ClearTable()
    {
        Files.Clear();
        ArtistGroups.Clear();
        _selectionFilter = null;
        FilesView.Refresh();
        SelectedGroup = null;
        SelectedFile = null;
        IsSpectrumVisible = false;
        ProcessedFiles = 0;
        TotalFiles = 0;
        ErrorCount = 0;
        KeepCount = 0;
        InvestigateCount = 0;
        ReplaceCount = 0;
        Progress = 0;
        CurrentlyProcessing = "";
        UpdateSummary();
    }

    [RelayCommand]
    private void ClearCache()
    {
        _analyzer.ClearCache();
    }

    [RelayCommand]
    private void RemoveTrack()
    {
        if (SelectedFile == null) return;
        var toRemove = SelectedFile;
        SelectedFile = null;
        IsSpectrumVisible = false;
        Files.Remove(toRemove);
        _selectionFilter = null;
        PopulateArtistGroups();
        ApplyFilters();
        UpdateSummary();
    }

    private static void ExportCsv(string path, List<AudioFileViewModel> data)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("File,Artist,Album,Format,SampleRate,BitDepth,Duration,Cutoff,DR,TruePeak,Clipping,Authenticity,LosslessScore,HiResScore,Quality,Decision");
        foreach (var f in data)
            sb.AppendLine($"\"{f.FileName}\",\"{f.Artist}\",\"{f.Album}\",{f.Format},{f.SampleRate},{f.BitDepth},{f.DurationSeconds:F1},{f.CutoffFrequency:F0},{f.DynamicRange:F0},{f.TruePeakDb:F1},{f.ClippingPercent:F2},{f.Authenticity},{f.LosslessScorePercent:F0}%,{f.HiResScorePercent:F0}%,{f.QualityScorePercent:F0}%,{f.Decision}");
        System.IO.File.WriteAllText(path, sb.ToString());
    }

    private static void ExportJson(string path, List<AudioFileViewModel> data)
    {
        var json = JsonSerializer.Serialize(data.Select(f => new
        {
            f.FileName, f.Artist, f.Album, f.Format, f.SampleRate, f.BitDepth,
            f.DurationSeconds, f.CutoffFrequency, f.DynamicRange, f.TruePeakDb,
            f.ClippingPercent, f.Authenticity, LosslessScore = f.LosslessScorePercent,
            HiResScore = f.HiResScorePercent, QualityScore = f.QualityScorePercent, f.Decision
        }), new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(path, json);
    }

    private static void ExportHtml(string path, List<AudioFileViewModel> data)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'><title>LosslessChecker Report</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,sans-serif;background:#1e1e2e;color:#cdd6f4;margin:20px}");
        sb.AppendLine("table{border-collapse:collapse;width:100%}th{background:#313244;padding:8px;text-align:left}");
        sb.AppendLine("td{padding:6px 8px;border-bottom:1px solid #45475a}.good{color:#2EA043}.warn{color:#D29922}.bad{color:#CF222E}</style></head><body>");
        sb.AppendLine("<h1>LosslessChecker Report</h1><table><tr><th>File</th><th>Artist</th><th>Format</th><th>Cutoff</th><th>DR</th><th>Auth</th><th>Lossless</th><th>Quality</th><th>Decision</th></tr>");
        foreach (var f in data)
        {
            string cls = f.Decision switch { var d when d.StartsWith("KEEP") => "good", "INVESTIGATE" => "warn", _ => "bad" };
            sb.AppendLine($"<tr class='{cls}'><td>{System.Net.WebUtility.HtmlEncode(f.FileName)}</td><td>{System.Net.WebUtility.HtmlEncode(f.Artist)}</td><td>{f.Format}</td><td>{f.CutoffFrequency:F0} Hz</td><td>DR{f.DynamicRange:F0}</td><td>{f.Authenticity}</td><td>{f.LosslessScorePercent:F0}%</td><td>{f.QualityScorePercent:F0}%</td><td><b>{f.Decision}</b></td></tr>");
        }
        sb.AppendLine("</table></body></html>");
        System.IO.File.WriteAllText(path, sb.ToString());
    }

    private async Task ScanAndAnalyze(string folderPath)
    {
        IsProcessing = true;
        ShowWelcome = false;
        ProcessedFiles = 0;
        ErrorCount = 0;
        KeepCount = 0;
        InvestigateCount = 0;
        ReplaceCount = 0;
        CurrentlyProcessing = "";
        Files.Clear();
        ArtistGroups.Clear();
        _selectionFilter = null;
        FilesView.Refresh();
        SelectedGroup = null;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var foundFiles = await Task.Run(() => _scanner.ScanFolder(folderPath), ct);
            TotalFiles = foundFiles.Count;

            if (ct.IsCancellationRequested) return;

            var vms = foundFiles.Select(f => new AudioFileViewModel(f)).ToList();
            int batchSize = 200;
            for (int i = 0; i < vms.Count; i += batchSize)
            {
                var batch = vms.Skip(i).Take(batchSize).ToList();
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var vm in batch) Files.Add(vm);
                }, System.Windows.Threading.DispatcherPriority.Background);
                await Task.Yield();
            }

            PopulateArtistGroups();
            _selectionFilter = null;
            ApplyFilters();
            UpdateSummary();

            var queue = new ConcurrentQueue<AudioFileViewModel>(vms);
            int processed = 0;

            int concurrency = Math.Max(1, Environment.ProcessorCount / 2);
            using var memoryGate = new SemaphoreSlim(4, 4);
            var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
            {
                while (queue.TryDequeue(out var vm) && !ct.IsCancellationRequested)
                {
                    vm.AnalysisStatus = AnalysisStatus.Processing;

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        CurrentlyProcessing = $"Обработка: {vm.FileName} [{processed + 1}/{TotalFiles}]";
                    });

                    await memoryGate.WaitAsync(ct);
                    try
                    {
                        var fileInfo = new AudioFileInfo(vm.FilePath, vm.FileName, 0);
                        var result = await Task.Run(() => _analyzer.Analyze(fileInfo, ct), ct);

                        vm.ApplyResult(result);

                        int done = Interlocked.Increment(ref processed);
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            ProcessedFiles = done;
                            Progress = TotalFiles > 0 ? (double)done / TotalFiles * 100.0 : 0;

                            if (result.AnalysisStatus == AnalysisStatus.Error)
                                ErrorCount++;
                            else if (result.Decision.StartsWith("KEEP"))
                                KeepCount++;
                            else if (result.Decision == "INVESTIGATE")
                                InvestigateCount++;
                            else if (result.Decision == "REPLACE")
                                ReplaceCount++;

                            var existing = ArtistGroups
                                .SelectMany(a => a.Albums)
                                .FirstOrDefault(al => string.Equals(al.AlbumName,
                                    string.IsNullOrWhiteSpace(result.Album) ? "Unknown Album" : result.Album,
                                    StringComparison.OrdinalIgnoreCase));
                            if (existing != null)
                                existing.Tracks.Add(vm);

                            UpdateSummary();
                        });
                    }
                    finally
                    {
                        memoryGate.Release();
                    }
                }
            });

            await Task.WhenAll(tasks);

            CurrentlyProcessing = "";
            _selectionFilter = null;
            ApplyFilters();
            ShowWelcome = Files.Count == 0;
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsProcessing = false;
            UpdateSummary();
            CurrentlyProcessing = "";
            ShowWelcome = Files.Count == 0;
        }
    }

    [RelayCommand]
    private async Task AddFiles()
    {
        var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Multiselect = true,
            Title = "Add audio files",
            Filter = "Audio Files|*.mp3;*.flac;*.wav;*.m4a;*.alac"
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        await ScanAndAppend(dialog.FileNames);
    }

    public async Task ScanAndAppend(IEnumerable<string> paths)
    {
        var existingPaths = new HashSet<string>(Files.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);
        var newFileInfos = new List<AudioFileInfo>();

        foreach (var path in paths)
        {
            if (System.IO.File.Exists(path))
            {
                var ext = System.IO.Path.GetExtension(path);
                if (".mp3.flac.wav.m4a.alac".Contains(ext.ToLowerInvariant()) && existingPaths.Add(path))
                    newFileInfos.Add(new AudioFileInfo(path, System.IO.Path.GetFileName(path), 0));
            }
            else if (System.IO.Directory.Exists(path))
            {
                var scanned = _scanner.ScanFolder(path);
                foreach (var fi in scanned)
                    if (existingPaths.Add(fi.FilePath))
                        newFileInfos.Add(fi);
            }
        }

        if (newFileInfos.Count == 0) return;

        IsProcessing = true;
        ShowWelcome = false;
        var ct = (_cts = new CancellationTokenSource()).Token;

        try
        {
            var vms = newFileInfos.Select(f => new AudioFileViewModel(f)).ToList();
            foreach (var vm in vms)
                Files.Add(vm);

            PopulateArtistGroups();
            _selectionFilter = null;
            ApplyFilters();
            UpdateSummary();

            var queue = new ConcurrentQueue<AudioFileViewModel>(vms);
            int newTotal = newFileInfos.Count;
            int newProcessed = 0;
            int startTotal = ProcessedFiles;

            int concurrency = Math.Max(1, Environment.ProcessorCount / 2);
            using var memoryGate = new SemaphoreSlim(4, 4);
            var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
            {
                while (queue.TryDequeue(out var vm) && !ct.IsCancellationRequested)
                {
                    int current = newProcessed + 1;
                    vm.AnalysisStatus = AnalysisStatus.Processing;

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        CurrentlyProcessing = $"Обработка: {vm.FileName} [{startTotal + current}/{startTotal + newTotal}]";
                    });

                    await memoryGate.WaitAsync(ct);
                    try
                    {
                        var fileInfo = new AudioFileInfo(vm.FilePath, vm.FileName, 0);
                        var result = await Task.Run(() => _analyzer.Analyze(fileInfo, ct), ct);

                        vm.ApplyResult(result);

                        int done = Interlocked.Increment(ref newProcessed);
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            TotalFiles = startTotal + newTotal;
                            ProcessedFiles = startTotal + newProcessed;
                            Progress = (double)(startTotal + done) / (startTotal + newTotal) * 100.0;
                            if (result.AnalysisStatus == AnalysisStatus.Error) ErrorCount++;
                            else if (result.Decision.StartsWith("KEEP")) KeepCount++;
                            else if (result.Decision == "INVESTIGATE") InvestigateCount++;
                            else if (result.Decision == "REPLACE") ReplaceCount++;
                            UpdateSummary();
                        });
                    }
                    finally
                    {
                        memoryGate.Release();
                    }
                }
            });
            await Task.WhenAll(tasks);

            CurrentlyProcessing = "";
            PopulateArtistGroups();
            _selectionFilter = null;
            ApplyFilters();
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsProcessing = false;
            UpdateSummary();
            CurrentlyProcessing = "";
            ShowWelcome = Files.Count == 0;
        }
    }

    private void PopulateArtistGroups()
    {
        var groups = Files
            .Where(f => f.AnalysisStatus != AnalysisStatus.Error)
            .GroupBy(f => NormalizeArtist(f))
            .OrderBy(g => g.Key)
            .Select(artistGroup =>
            {
                var artist = new ArtistGroup { ArtistName = artistGroup.First().Artist.Length > 0 ? artistGroup.First().Artist : "Unknown Artist" };
                var albums = artistGroup
                    .GroupBy(f => string.IsNullOrWhiteSpace(f.Album) ? "Unknown Album" : f.Album)
                    .OrderBy(g => g.Key)
                    .Select(albumGroup =>
                    {
                        var tracks = albumGroup.OrderBy(f => f.FileName).ToList();
                        var first = tracks.First();
                        var album = new AlbumGroup
                        {
                            AlbumName = albumGroup.Key,
                            Genre = first.Genre,
                            CoverData = first.CoverData,
                            Tracks = new ObservableCollection<AudioFileViewModel>(tracks)
                        };
                        var completed = tracks.Where(t => t.AnalysisStatus == Models.AnalysisStatus.Completed && t.Decision != "SKIPPED").ToList();
                        if (completed.Count > 0)
                        {
                            album.AverageLosslessScore = completed.Average(t => t.LosslessScorePercent);
                            album.AverageQualityScore = completed.Average(t => t.QualityScorePercent);
                            album.AverageDynamicRange = completed.Average(t => t.DynamicRange);
                            album.KeepCount = completed.Count(t => t.Decision.StartsWith("KEEP"));
                            album.InvestigateCount = completed.Count(t => t.Decision == "INVESTIGATE");
                            album.ReplaceCount = completed.Count(t => t.Decision == "REPLACE");
                            album.WorstTrackScore = completed.Count > 0
                                ? completed.Min(t => t.QualityScorePercent)
                                : 0;
                            album.AlbumVerdict = album.ReplaceCount > 0 ? "REPLACE"
                                : album.InvestigateCount > 0 ? "NOT SURE" : "LOSSLESS";

                            // 3.1 Album consistency: ≥80% same Authenticity → mark outliers
                            var authGroups = completed
                                .GroupBy(t => t.Authenticity)
                                .OrderByDescending(g => g.Count())
                                .ToList();
                            if (authGroups.Count >= 2)
                            {
                                var majority = authGroups[0];
                                double majorityPct = (double)majority.Count() / completed.Count;
                                if (majorityPct >= 0.8)
                                {
                                    foreach (var outlier in authGroups.Skip(1).SelectMany(g => g))
                                        outlier.IsAlbumOutlier = true;
                                }
                            }
                        }
                        return album;
                    });
                foreach (var album in albums)
                    artist.Albums.Add(album);
                return artist;
            });

        ArtistGroups = new ObservableCollection<ArtistGroup>(groups);
    }

    private static string NormalizeArtist(AudioFileViewModel f)
    {
        var name = string.IsNullOrWhiteSpace(f.Artist) ? "Unknown Artist" : f.Artist.Trim();
        if (name.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            name = name[4..].Trim();
        return name.ToLowerInvariant();
    }

    private void UpdateSummary()
    {
        SummaryText = $"Ready: {ProcessedFiles}/{TotalFiles} | KEEP: {KeepCount} | INVESTIGATE: {InvestigateCount} | REPLACE: {ReplaceCount} | Errors: {ErrorCount}";
        OnPropertyChanged(nameof(ProgressText));
    }
}
