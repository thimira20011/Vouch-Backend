using Vouch.Domain.Enums;

namespace Vouch.Domain.Entities;

public class VouchRecord : BaseEntity
{
    public Guid TargetUserId { get; set; }
    public User TargetUser { get; set; } = null!;

    public Guid VoucherUserId { get; set; }
    public User VoucherUser { get; set; } = null!;

    public CharacterTrait Traits { get; set; }
    public string? Note { get; set; } // Short optional endorsement note

    // Score calculations
    public double BaseWeight { get; set; } = 1.0;
    public double VoucherBonus { get; set; } = 0.0; // +0.2 if voucher has >= 5 vouches
    public double CliqueDampeningMultiplier { get; set; } = 1.0; // 0.5 if clique pattern detected
    public bool IsZeroWeightDueToAccountAge { get; set; } = false; // true if voucher < 14 days old
    public double FinalCalculatedWeight { get; set; } = 0.0;
}
