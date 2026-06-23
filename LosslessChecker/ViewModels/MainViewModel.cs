using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
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
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private ObservableCollection<AudioFileViewModel> _files = new();

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private int _processedFiles;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _summaryText = "Ready";

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
    }

    [RelayCommand]
    private async Task SelectFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder with audio files",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            await ScanAndAnalyze(dialog.SelectedPath);
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
    }

    private async Task ScanAndAnalyze(string folderPath)
    {
        IsProcessing = true;
        ProcessedFiles = 0;
        ErrorCount = 0;
        KeepCount = 0;
        InvestigateCount = 0;
        ReplaceCount = 0;
        Files.Clear();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var foundFiles = await Task.Run(() => _scanner.ScanFolder(folderPath), ct);
            TotalFiles = foundFiles.Count;

            if (ct.IsCancellationRequested) return;

            var vms = foundFiles.Select(f => new AudioFileViewModel(f)).ToList();
            foreach (var vm in vms)
                Files.Add(vm);

            var queue = new ConcurrentQueue<AudioFileViewModel>(vms);
            int processed = 0;

            var tasks = Enumerable.Range(0, Environment.ProcessorCount).Select(async _ =>
            {
                while (queue.TryDequeue(out var vm) && !ct.IsCancellationRequested)
                {
                    vm.AnalysisStatus = AnalysisStatus.Processing;

                    var fileInfo = new AudioFileInfo(vm.FilePath, vm.FileName, 0);
                    var result = await Task.Run(() => _analyzer.Analyze(fileInfo, ct), ct);

                    vm.ApplyResult(result);

                    int done = Interlocked.Increment(ref processed);
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProcessedFiles = done;
                        if (done % 5 == 0 || done == TotalFiles)
                            Progress = TotalFiles > 0 ? (double)done / TotalFiles * 100.0 : 0;

                        if (result.AnalysisStatus == AnalysisStatus.Error)
                            ErrorCount++;
                        else if (result.Decision.StartsWith("KEEP"))
                            KeepCount++;
                        else if (result.Decision == "INVESTIGATE")
                            InvestigateCount++;
                        else if (result.Decision == "REPLACE")
                            ReplaceCount++;

                        if (done % 5 == 0 || done == TotalFiles)
                            UpdateSummary();
                    });

                    // Force GC every 15 files to reclaim decoded PCM samples (~50 MB each)
                    if (done % 15 == 0)
                        GC.Collect(1, GCCollectionMode.Optimized, false);
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsProcessing = false;
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        SummaryText = $"Ready: {ProcessedFiles}/{TotalFiles} | KEEP: {KeepCount} | INVESTIGATE: {InvestigateCount} | REPLACE: {ReplaceCount} | Errors: {ErrorCount}";
    }
}
