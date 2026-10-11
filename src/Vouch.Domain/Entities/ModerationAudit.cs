using Vouch.Domain.Enums;

namespace Vouch.Domain.Entities;

public enum ModerationAction { ReportUpheld = 1, ReportDismissed = 2, AutomaticallySuspended = 3, ManuallySuspended = 4, Reinstated = 5 }

public class ModerationAudit : BaseEntity
{
    public Guid CampusId { get; set; }
    public Guid ArchitectId { get; set; }
    public Guid SubjectUserId { get; set; }
    public User SubjectUser { get; set; } = null!;
    public Guid? ReportId { get; set; }
    public Report? Report { get; set; }
    public ModerationAction Action { get; set; }
    public AccountStatus PreviousStatus { get; set; }
    public AccountStatus ResultingStatus { get; set; }
    public required string Reason { get; set; }
}
