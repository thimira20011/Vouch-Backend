using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Persistence;

public static class DeletionTombstone
{
    public static string Encode(Guid requestId, Guid userId) => $"{requestId:N}:{userId:N}";
    public static Guid Decode(string value, Guid requestId)
    {
        if (value.Length != 65 || value[32] != ':' || !Guid.TryParseExact(value[..32], "N", out var saved) || saved != requestId ||
            !Guid.TryParseExact(value[33..], "N", out var userId) || userId == Guid.Empty) throw new ProtectedDataException();
        return userId;
    }
}
