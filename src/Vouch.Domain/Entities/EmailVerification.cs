namespace Vouch.Domain.Entities;

public sealed class EmailVerification : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid CampusId { get; set; }
    public required string EmailLookupHash { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
