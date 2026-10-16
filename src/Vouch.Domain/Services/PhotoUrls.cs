using Vouch.Domain.Enums;

namespace Vouch.Domain.Services;

public static class PhotoUrls
{
    public static string Variant(RevealClarityStage stage) => stage switch
    {
        RevealClarityStage.Abstract0 => "abstract", RevealClarityStage.Quarter25 => "quarter",
        RevealClarityStage.Sixty60 => "sixty", RevealClarityStage.Full100 => "original",
        _ => throw new ArgumentException("Unknown photo stage.")
    };
    public static string Create(Guid owner, Guid generation, RevealClarityStage stage)
        => $"/photos/{Variant(stage)}/{owner:N}-{generation:N}.jpg";

    public static bool TryParseFile(string file, out Guid owner, out Guid generation)
    {
        owner = generation = default;
        return file.Length == 69 && file[32] == '-' && file.EndsWith(".jpg", StringComparison.Ordinal) &&
            Guid.TryParseExact(file[..32], "N", out owner) && Guid.TryParseExact(file[33..65], "N", out generation);
    }
    // Legacy/external URLs never reach clients through these projections.
    public static string? ForStage(string? originalUrl, Guid owner, RevealClarityStage stage)
    {
        const string prefix = "/photos/original/";
        return originalUrl is not null && originalUrl.StartsWith(prefix, StringComparison.Ordinal) &&
            TryParseFile(originalUrl[prefix.Length..], out var parsed, out var generation) && parsed == owner
            ? Create(owner, generation, stage) : null;
    }
}
