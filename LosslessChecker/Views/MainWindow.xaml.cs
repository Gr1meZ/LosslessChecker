using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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

    private void DataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid &&
            grid.SelectedItem is AudioFileViewModel selected)
        {
            _viewModel.OnSelectionChanged(selected);

            if (selected.SpectrogramBitmap is WriteableBitmap bmp)
            {
                SpectrogramImage.Source = bmp;

                double canvasWidth = SpectrogramImage.Width > 0 ? SpectrogramImage.Width : 600;
                double nyquist = 22050;
                double cutoffX = selected.CutoffFrequency / nyquist * canvasWidth;
                CutoffLine.X1 = cutoffX;
                CutoffLine.X2 = cutoffX;
            }
        }
        else
        {
            _viewModel.OnSelectionChanged(null);
            SpectrogramImage.Source = null;
        }
    }
}
