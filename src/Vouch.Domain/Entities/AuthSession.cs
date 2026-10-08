namespace Vouch.Domain.Entities;

public sealed class AuthSession : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class RefreshToken : BaseEntity
{
    public Guid SessionId { get; set; }
    public AuthSession Session { get; set; } = null!;
    public required string TokenHash { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
