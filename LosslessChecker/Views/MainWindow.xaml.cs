using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        if (sender is System.Windows.Controls.DataGrid grid && grid.SelectedItem is AudioFileViewModel selected)
        {
            _viewModel.SelectedFile = selected;
            _viewModel.SelectedCutoffFrequency = selected.CutoffFrequency;

            if (selected.AveragedSpectrum is { Length: > 0 } spectrum)
            {
                double canvasWidth = SpectrumCanvas.ActualWidth > 0 ? SpectrumCanvas.ActualWidth : 500;
                double canvasHeight = SpectrumCanvas.ActualHeight > 0 ? SpectrumCanvas.ActualHeight : 190;

                double maxMag = 0;
                for (int i = 0; i < spectrum.Length; i++)
                    maxMag = Math.Max(maxMag, spectrum[i]);

                var points = new System.Windows.Media.PointCollection();
                if (maxMag > 0)
                {
                    for (int i = 0; i < spectrum.Length; i++)
                    {
                        double x = (double)i / spectrum.Length * canvasWidth;
                        double dbMag = 20.0 * Math.Log10(Math.Max(spectrum[i] / maxMag, 1e-10));
                        double y = canvasHeight - ((dbMag + 80.0) / 80.0 * canvasHeight);
                        y = Math.Max(0, Math.Min(canvasHeight, y));
                        points.Add(new System.Windows.Point(x, y));
                    }
                }

                SpectrumLine.Points = points;

                double nyquist = 22050;
                double cutoffX = selected.CutoffFrequency / nyquist * canvasWidth;
                CutoffLine.X1 = cutoffX;
                CutoffLine.X2 = cutoffX;

                _viewModel.IsSpectrumVisible = true;
            }
            else
            {
                _viewModel.IsSpectrumVisible = false;
            }
        }
    }
}
