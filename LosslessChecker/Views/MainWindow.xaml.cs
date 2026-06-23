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

            var bmp = selected.GetOrBuildSpectrogram();
            if (bmp != null)
            {
                SpectrogramImage.Source = bmp;

                double canvasHeight = 220;
                double canvasWidth = 600;
                double nyquist = 22050;
                double cutoffRatio = nyquist > 0 ? selected.CutoffFrequency / nyquist : 1.0;

                double cutoffY = (1.0 - cutoffRatio) * canvasHeight;
                CutoffLine.Y1 = cutoffY;
                CutoffLine.Y2 = cutoffY;
                CutoffLine.X1 = 0;
                CutoffLine.X2 = canvasWidth;

                Canvas.SetTop(CutoffLabel, Math.Min(cutoffY, canvasHeight - 14));
                CutoffLabel.Text = $"{selected.CutoffFrequency:F0} Hz";
                CutoffLabel.Visibility = Visibility.Visible;

                FreqLabelNyq.Text = $"{nyquist / 1000.0:F0}k";
                FreqLabelMid.Text = $"{nyquist / 2000.0:F0}k";
                Canvas.SetTop(FreqLabelNyq, 0);
                Canvas.SetTop(FreqLabelMid, canvasHeight / 2 - 6);
                Canvas.SetTop(FreqLabel0, canvasHeight - 12);
            }
        }
        else
        {
            _viewModel.OnSelectionChanged(null);
            SpectrogramImage.Source = null;
            CutoffLabel.Visibility = Visibility.Collapsed;
        }
    }
}
