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
        var fi = typeof(AudioFileViewModel).GetField("_lastResult",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var lastResult = fi?.GetValue(vm) as Models.AnalysisResult;

        if (lastResult?.SpectrogramFlat is { Length: > 0 })
        {
            var window = new SpectrogramWindow(
                lastResult.SpectrogramFlat,
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
}
