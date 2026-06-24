namespace LosslessChecker.Services;

public interface IDialogService
{
    void ShowSpectrogram(float[] rawData, int width, int height,
        double durationSec, double sampleRate, double cutoffHz, string fileName);
}
