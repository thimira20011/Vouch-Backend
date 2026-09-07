namespace Vouch.Domain.Entities;

public class Block : BaseEntity
{
    public Guid BlockerId { get; set; }
    public User Blocker { get; set; } = null!;

    public Guid BlockedUserId { get; set; }
    public User BlockedUser { get; set; } = null!;
}
