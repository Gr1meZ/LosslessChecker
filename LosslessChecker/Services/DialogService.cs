using System.Windows;
using LosslessChecker.Views;

namespace LosslessChecker.Services;

public class DialogService : IDialogService
{
    public void ShowSpectrogram(float[] rawData, int width, int height,
        double durationSec, double sampleRate, double cutoffHz, string fileName)
    {
        var window = new SpectrogramWindow(rawData, width, height,
            durationSec, sampleRate, cutoffHz, fileName);
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Topmost = true;
        window.Show();
        window.Activate();
    }
}
