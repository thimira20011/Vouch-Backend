namespace Vouch.Infrastructure.Persistence;

public sealed class ProtectionState
{
    public int Id { get; set; }
    public required string LookupKeyCheck { get; set; }
    public required string EncryptionCheck { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}
