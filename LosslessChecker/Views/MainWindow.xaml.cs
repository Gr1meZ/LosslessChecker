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

        var bmp = _viewModel.SelectedFile.GetOrBuildSpectrogram();
        if (bmp == null) return;

        var window = new SpectrogramWindow(bmp, _viewModel.SelectedFile.FileName);
        window.Owner = this;
        window.Show();
    }
}
