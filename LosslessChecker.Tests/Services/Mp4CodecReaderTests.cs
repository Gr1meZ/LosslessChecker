using LosslessChecker.Services;
using Xunit;

namespace LosslessChecker.Tests.Services;

public class Mp4CodecReaderTests
{
    [Fact]
    public void DetectCodec_NullPath_ReturnsUnknown()
    {
        var result = Mp4CodecReader.DetectCodec("nonexistent.m4a");
        Assert.Equal("unknown", result.Codec);
        Assert.Equal(0, result.Bitrate);
    }
}
