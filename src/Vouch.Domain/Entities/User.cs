using Vouch.Domain.Enums;

namespace Vouch.Domain.Entities;

public class User : BaseEntity
{
    public Guid CampusId { get; set; }
    public Campus Campus { get; set; } = null!;

    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string FullName { get; set; }

    public UserRole Role { get; set; } = UserRole.Seeker;
    public AccountStatus Status { get; set; } = AccountStatus.InIncubation;
    public bool HasFoundingMemberBadge { get; set; } = false;

    // Academic Details
    public required string Faculty { get; set; }
    public required string Department { get; set; }
    public int AcademicYear { get; set; }

    // Onboarding Data (REQ-3)
    public string Bio { get; set; } = string.Empty; // max 280 chars
    public List<string> DeepValues { get; set; } = new();
    public List<IntellectualInterest> IntellectualInterests { get; set; } = new();

    // Slow-Burn Imagery (REQ-4, REQ-17)
    public string? OriginalPhotoUrl { get; set; }
    public string? OilPaintingAbstractPhotoUrl { get; set; }

    // Reputation & Trust
    public double TrustScore { get; set; } = 0.0; // Phase 1 Max Cap = 20.0
    public int ActiveVouchesReceivedCount { get; set; } = 0;
    public DateTimeOffset? IncubationCompletedAt { get; set; }

    // Moderation & Safety (NFR-10 to NFR-14)
    public bool IsSoftHiddenFromMatchmaking { get; set; } = false;
    public int UpheldReportsCount { get; set; } = 0;

    // Privacy & Account Deletion (NFR-6)
    public DateTimeOffset? DeletionRequestedAt { get; set; }

    // Navigation Properties
    public ICollection<VouchRecord> VouchesReceived { get; set; } = new List<VouchRecord>();
    public ICollection<VouchRecord> VouchesGiven { get; set; } = new List<VouchRecord>();
    public ICollection<Report> ReportsSubmitted { get; set; } = new List<Report>();
    public ICollection<Report> ReportsAgainst { get; set; } = new List<Report>();
    public ICollection<Block> BlockedUsers { get; set; } = new List<Block>();
    public ICollection<Block> BlockedByUsers { get; set; } = new List<Block>();
}
