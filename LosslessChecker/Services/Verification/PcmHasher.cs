using System.Security.Cryptography;

namespace LosslessChecker.Services.Verification;

public sealed class PcmHasher : IAudioHasher
{
    public string Algorithm => "PCM-MD5";

    public AudioHashResult ComputeHash(string filePath, CancellationToken ct = default)
    {
        throw new NotSupportedException("Use ComputeHash(float[], int) for PCM data.");
    }

    public static byte[] ComputePcmMd5(float[] samples)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var chunk = new byte[8192];
        for (int pos = 0; pos < samples.Length; pos += 4096)
        {
            int count = Math.Min(4096, samples.Length - pos);
            int bytesWritten = 0;
            for (int i = 0; i < count; i++)
            {
                short val = (short)Math.Max(-32768, Math.Min(32767, (int)(samples[pos + i] * 32767.0)));
                chunk[bytesWritten++] = (byte)(val & 0xFF);
                chunk[bytesWritten++] = (byte)((val >> 8) & 0xFF);
            }
            md5.AppendData(chunk, 0, bytesWritten);
        }
        return md5.GetHashAndReset();
    }

    public static byte[] ComputePcmMd5(float[] samples, CancellationToken ct = default)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var chunk = new byte[8192];
        for (int pos = 0; pos < samples.Length; pos += 4096)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(4096, samples.Length - pos);
            int bytesWritten = 0;
            for (int i = 0; i < count; i++)
            {
                short val = (short)Math.Max(-32768, Math.Min(32767, (int)(samples[pos + i] * 32767.0)));
                chunk[bytesWritten++] = (byte)(val & 0xFF);
                chunk[bytesWritten++] = (byte)((val >> 8) & 0xFF);
            }
            md5.AppendData(chunk, 0, bytesWritten);
        }
        return md5.GetHashAndReset();
    }

    public static string ToHexString(byte[] hash)
    {
        var sb = new System.Text.StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
