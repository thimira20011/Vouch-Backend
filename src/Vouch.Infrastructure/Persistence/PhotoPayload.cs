using Vouch.Domain.Enums;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Persistence;

public static class PhotoPayload
{
    public const int MaxJpegBytes = 2 * 1024 * 1024;
    public static string Encode(byte[] bytes, Guid owner, Guid generation, RevealClarityStage stage)
        => $"{owner:N}:{generation:N}:{(int)stage}:" + Convert.ToBase64String(bytes);
    public static byte[] Decode(string value, Guid owner, Guid generation, RevealClarityStage stage)
    {
        try
        {
            var parts = value.Split(':', 4);
            if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out var savedOwner) || savedOwner != owner ||
                !Guid.TryParseExact(parts[1], "N", out var savedId) || savedId != generation || parts[2] != ((int)stage).ToString())
                throw new ProtectedDataException();
            var bytes = Convert.FromBase64String(parts[3]);
            if (bytes.Length is < 4 or > MaxJpegBytes || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[2] != 0xff)
                throw new ProtectedDataException();
            return bytes;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { throw new ProtectedDataException(); }
    }
    public static RevealClarityStage ColumnStage(string column) => column switch
    {
        "OriginalJpeg" => RevealClarityStage.Full100, "AbstractJpeg" => RevealClarityStage.Abstract0,
        "QuarterJpeg" => RevealClarityStage.Quarter25, "SixtyJpeg" => RevealClarityStage.Sixty60,
        _ => throw new ArgumentException("Unknown photo column.")
    };
}
