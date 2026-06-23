namespace LosslessChecker.Models;

public record StereoBuffer(float[] Left, float[] Right, int SampleRate)
{
    public int Length => Left.Length;
    public bool IsStereo => Right is { Length: > 0 };
}
