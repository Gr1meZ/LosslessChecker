using System.IO;
using System.Security.Cryptography;

namespace LosslessChecker.Services.Verification;

public sealed class AccurateRipHasher : IAudioHasher
{
    private const int CdSectorSamples = 588;

    public string Algorithm => "AccurateRipV2";

    public AudioHashResult ComputeHash(string filePath, CancellationToken ct = default)
    {
        throw new NotSupportedException("Use ComputeHash(float[], int, int) for decoded PCM data.");
    }

    public AudioHashResult ComputeHash(float[] samples, int sampleRate, int trackNumber, CancellationToken ct = default)
    {
        if (sampleRate != 44100)
            return new AudioHashResult(Algorithm, "N/A", trackNumber, 0);

        int skipStart = CdSectorSamples;
        int skipEnd = samples.Length > CdSectorSamples * 2 ? samples.Length - CdSectorSamples : samples.Length;

        if (skipEnd <= skipStart)
            return new AudioHashResult(Algorithm, "N/A", trackNumber, 0);

        long hash1 = 0, hash2 = 0;
        long position = 0;

        for (int i = skipStart; i < skipEnd; i++)
        {
            ct.ThrowIfCancellationRequested();
            int sample16 = (short)Math.Max(-32768, Math.Min(32767, (int)(samples[i] * 32767.0)));

            if (position % 2 == 0)
                hash1 += sample16;
            else
                hash2 += sample16 * (position + 1);

            position++;
        }

        string trackHash = $"{unchecked((uint)hash1):X8}-{unchecked((uint)hash2):X8}";
        int discId = ComputeDiscId(samples);
        return new AudioHashResult(Algorithm, trackHash, trackNumber, discId);
    }

    private static int ComputeDiscId(float[] samples)
    {
        return unchecked((int)(samples.Length / CdSectorSamples));
    }
}

public sealed class CUEToolsHasher : IAudioHasher
{
    public string Algorithm => "CUETools-CTDB";

    public AudioHashResult ComputeHash(string filePath, CancellationToken ct = default)
    {
        throw new NotSupportedException("Use ComputeHash(float[], int, int) for decoded PCM data.");
    }

    public AudioHashResult ComputeHash(float[] samples, int sampleRate, int trackNumber, CancellationToken ct = default)
    {
        if (sampleRate != 44100)
            return new AudioHashResult(Algorithm, "N/A", trackNumber, 0);

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[4096];

        for (int pos = 0; pos < samples.Length; pos += 2048)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(2048, samples.Length - pos);
            int bytesWritten = 0;
            for (int i = 0; i < count; i++)
            {
                short val = (short)Math.Max(-32768, Math.Min(32767, (int)(samples[pos + i] * 32767.0)));
                chunk[bytesWritten++] = (byte)(val & 0xFF);
                chunk[bytesWritten++] = (byte)((val >> 8) & 0xFF);
            }
            sha256.AppendData(chunk, 0, bytesWritten);
        }

        var hash = sha256.GetHashAndReset();
        return new AudioHashResult(Algorithm, PcmHasher.ToHexString(hash), trackNumber, 0, hash);
    }
}
