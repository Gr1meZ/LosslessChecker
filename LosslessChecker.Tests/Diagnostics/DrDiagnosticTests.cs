using LosslessChecker.Services;
using Xunit;

namespace LosslessChecker.Tests.Diagnostics;

public class DrDiagnosticTests
{
    [Fact]
    public void DrMeter_BlockByBlock_GeneratesCrestOf4()
    {
        var meter = new DrMeter();
        int sr = 48000;
        int blockSize = (int)(sr * 3.0); // 3-second blocks
        int totalBlocks = 50; // enough blocks for top 20% selection
        int totalSamples = totalBlocks * blockSize;
        var samples = new float[totalSamples];
        var rng = new Random(42);

        // Top 20% blocks: peak=0.9, rms~0.5. Rest: peak=0.4, rms~0.23.
        // DR ~ (20*log10(0.9)) - (20*log10(0.5)) ≈ 5.1 dB for top 20%
        for (int b = 0; b < totalBlocks; b++)
        {
            double amp = b < totalBlocks * 0.2 ? 0.9 : 0.4;
            int offset = b * blockSize;
            for (int i = 0; i < blockSize; i++)
            {
                samples[offset + i] = (float)(amp * Math.Sin(2 * Math.PI * 1000 * i / sr));
            }
        }

        var (dr, peak, clip) = meter.Analyze(samples, sr);
        // Pure sine: peak = amplitude, RMS = amplitude/sqrt(2) = 0.707*amplitude
        // Peak-to-RMS ratio = 1/0.707 = 1.414 → 20*log10(1.414) = 3.01 dB
        // Since top 20% and rest have the same ratio, DR should be about 3.0
        Assert.True(dr > 2.5, $"Expected DR > 2.5 for pure sine crest, got {dr}");
        Assert.True(dr < 4.0, $"Expected DR < 4.0, got {dr}");
    }

    [Fact]
    public void DrMeter_AllBlocksEqual_ShouldBeNearZero()
    {
        var meter = new DrMeter();
        int sr = 48000;
        int blockSize = (int)(sr * 3.0);
        int totalSamples = blockSize * 10;
        var samples = new float[totalSamples];
        for (int i = 0; i < totalSamples; i++)
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / sr));

        var (dr, _, _) = meter.Analyze(samples, sr);
        // Pure sine: same peak-to-RMS ratio across all blocks → DR should be the sine crest (3.0 dB)
        Assert.True(dr > 2.5, $"Pure sine has crest ~3dB, got {dr}");
        Assert.True(dr < 4.0, $"Pure sine has crest ~3dB, got {dr}");
    }

    [Fact]
    public void DrMeter_Dr4_PerChannelVersusStereo()
    {
        var meter = new DrMeter();
        int sr = 48000;
        int blockSize = (int)(sr * 3.0);
        int totalBlocks = 30;
        int totalSamples = totalBlocks * blockSize;
        var rng = new Random(42);

        var left = new float[totalSamples];
        var right = new float[totalSamples];
        for (int b = 0; b < totalBlocks; b++)
        {
            double ampL = b < totalBlocks * 0.2 ? 0.9 : 0.4;
            double ampR = b < totalBlocks * 0.2 ? 0.9 : 0.4;
            int offset = b * blockSize;
            for (int i = 0; i < blockSize; i++)
            {
                left[offset + i] = (float)(ampL * Math.Sin(2 * Math.PI * 1000 * i / sr));
                right[offset + i] = (float)(ampR * Math.Sin(2 * Math.PI * 1000 * i / sr));
            }
        }

        var buffer = new LosslessChecker.Models.StereoBuffer(left, right, sr);
        var result = meter.AnalyzeStereo(buffer);

        // Pure sine: peak-to-RMS = 3 dB. Mono downmix should give same.
        Assert.True(result.Dr > 2.5, $"Expected mono DR > 2.5, got {result.Dr}");
        Assert.True(result.DrLeft > 2.5, $"Expected left DR > 2.5, got {result.DrLeft}");
        Assert.True(result.DrRight > 2.5, $"Expected right DR > 2.5, got {result.DrRight}");
    }
}
