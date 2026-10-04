namespace Vouch.Domain.Entities;

/// <summary>
/// REQ-A1: Architect-issued single-use invite tokens for ambassador onboarding.
/// Replaces the insecure email-substring hack ("ambassador" in email address).
/// </summary>
public class AmbassadorInvite : BaseEntity
{
    public Guid CampusId { get; set; }
    public Campus Campus { get; set; } = null!;

    /// <summary>The Architect user who issued this invite.</summary>
    public Guid IssuedByArchitectId { get; set; }

    /// <summary>SHA-256 digest; the random bearer token is returned only when issued.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Required for redemption; identity is activated only after mailbox verification.</summary>
    public string? IntendedEmail { get; set; }
    public string? IntendedEmailLookupHash { get; set; }

    public bool IsUsed { get; set; } = false;
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? RedeemedByUserId { get; set; }

    /// <summary>Invites expire after 7 days by default.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
