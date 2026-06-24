namespace LosslessChecker.Models;

public class AudioAnalysisContext
{
    public int SampleRate { get; }
    public double Nyquist { get; }
    public float[] Samples { get; }

    public AudioAnalysisContext(float[] samples, int sampleRate)
    {
        Samples = samples;
        SampleRate = sampleRate;
        Nyquist = sampleRate / 2.0;
    }
}
