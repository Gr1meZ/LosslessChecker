using LosslessChecker.Views;

namespace LosslessChecker.Services;

public class DialogService : IDialogService
{
    public void ShowSpectrogram(float[] rawData, int width, int height,
        double durationSec, double sampleRate, double cutoffHz, string fileName)
    {
        var window = new SpectrogramWindow(rawData, width, height,
            durationSec, sampleRate, cutoffHz, fileName);
        window.Show();
    }
}
