namespace LosslessChecker.Services.Verification;

public interface IAudioHasher
{
    string Algorithm { get; }
    AudioHashResult ComputeHash(string filePath, CancellationToken ct = default);
}

public interface IHashDatabase
{
    string DatabaseName { get; }
    Task<HashVerificationResult> VerifyAsync(AudioHashResult hash, CancellationToken ct = default);
}

public record AudioHashResult(
    string Algorithm,
    string TrackHash,
    int TrackNumber,
    int DiscId,
    byte[]? RawData = null);

public record HashVerificationResult(
    bool IsVerified,
    int Confidence,
    string DatabaseName,
    string? MatchedPressing = null);
