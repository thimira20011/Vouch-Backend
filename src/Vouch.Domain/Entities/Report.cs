using Vouch.Domain.Enums;

namespace Vouch.Domain.Entities;

public class Report : BaseEntity
{
    public Guid ReporterId { get; set; }
    public User Reporter { get; set; } = null!;

    public Guid ReportedUserId { get; set; }
    public User ReportedUser { get; set; } = null!;

    public Guid? RelatedMessageId { get; set; }
    public Message? RelatedMessage { get; set; }

    public ReportCategory Category { get; set; }
    public required string Details { get; set; }
    public int SeverityScore { get; set; } // 1 to 5 (Harassment/Impersonation = 4/5)

    public ReportStatus Status { get; set; } = ReportStatus.Pending;
    public string? ArchitectNotes { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
