using System.Windows;
using System.Windows.Media.Imaging;

namespace LosslessChecker.Views;

public partial class SpectrogramWindow : Window
{
    public SpectrogramWindow(BitmapSource? bmp, string title = "Spectrogram")
    {
        InitializeComponent();
        SpectrogramImage.Source = bmp;
        DataContext = new { Title = title };
    }
}
