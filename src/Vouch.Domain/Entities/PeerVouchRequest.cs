namespace Vouch.Domain.Entities;

public enum PeerVouchRequestStatus { Pending = 1, Fulfilled = 2, Dismissed = 3, Cancelled = 4, Expired = 5 }

public class PeerVouchRequest : BaseEntity
{
    public Guid RequesterId { get; set; }
    public User Requester { get; set; } = null!;
    public Guid RequestedVoucherId { get; set; }
    public User RequestedVoucher { get; set; } = null!;
    public PeerVouchRequestStatus Status { get; set; } = PeerVouchRequestStatus.Pending;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
