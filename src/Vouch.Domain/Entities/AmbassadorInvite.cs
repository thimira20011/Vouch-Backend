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

    /// <summary>Cryptographically random token sent to the prospective ambassador.</summary>
    public required string Token { get; set; }

    /// <summary>Email address this invite was intended for (informational only, not enforced).</summary>
    public string? IntendedEmail { get; set; }

    public bool IsUsed { get; set; } = false;
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Invites expire after 7 days by default.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
