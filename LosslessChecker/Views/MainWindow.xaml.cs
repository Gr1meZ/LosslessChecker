using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LosslessChecker.Models;
using LosslessChecker.ViewModels;

namespace LosslessChecker.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var column = e.Column;
        var path = column.SortMemberPath;
        if (!string.IsNullOrWhiteSpace(path))
            _viewModel.SortFiles(path);
    }

    private void DataGrid_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid &&
            grid.SelectedItem is AudioFileViewModel selected)
        {
            _viewModel.OnSelectionChanged(selected);
            var bmp = selected.GetOrBuildSpectrogram();
            SpectrogramImage.Source = bmp;
        }
        else
        {
            _viewModel.OnSelectionChanged(null);
            SpectrogramImage.Source = null;
        }
    }

    private void TreeView_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        _viewModel.OnTreeSelectionChanged(e.NewValue);
    }

    private void Spectrogram_Click(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedFile == null) return;

        var vm = _viewModel.SelectedFile;
        var lastResult = vm.LastResult;

        if (lastResult?.SpectrogramDb is { Length: > 0 })
        {
            var window = new SpectrogramWindow(
                lastResult.SpectrogramDb,
                lastResult.SpectrogramWidth,
                lastResult.SpectrogramHeight,
                lastResult.DurationSeconds,
                lastResult.SampleRate,
                lastResult.CutoffFrequency,
                vm.FileName);
            window.Owner = this;
            window.Show();
        }
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = System.Windows.DragDropEffects.Link;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            await _viewModel.ScanAndAppend(files);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedFile?.FilePath is string path)
            System.Diagnostics.Process.Start("explorer", $"/select,\"{path}\"");
    }

    private void CopyMetrics_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedFile?.CopyMetricsCommand.Execute(null);
    }

    private void SpectrogramContext_Click(object sender, RoutedEventArgs e)
    {
        Spectrogram_Click(sender, null!);
    }
}
