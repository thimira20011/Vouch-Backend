namespace Vouch.Domain.Entities;

public enum NotificationKind { HighSeverityReport = 1, TrustScoreAnomaly = 2, InactivityNudge = 3 }

// This row is both the durable inbox event and its retryable delivery work item.
public class Notification : BaseEntity
{
    public Guid CampusId { get; set; }
    public Guid? RecipientUserId { get; set; } // Null means the campus's current Architects.
    public Guid? SubjectUserId { get; set; }
    public Guid? ConversationId { get; set; }
    public DateTimeOffset? InactivityLastMessageAt { get; set; }
    public Conversation? Conversation { get; set; }
    public Guid? ReportId { get; set; }
    public Report? Report { get; set; }
    public User? SubjectUser { get; set; }
    public NotificationKind Kind { get; set; }
    public required string DedupeKey { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? FirstPublishedAt { get; set; }
    public DateTimeOffset? EmailCompletedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? DeadlineMissedAt { get; set; }
    public string? LastErrorCode { get; set; }
}

public class NotificationReceipt
{
    public Guid NotificationId { get; set; }
    public Notification Notification { get; set; } = null!;
    public Guid UserId { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public int EmailAttempts { get; set; }
    public DateTimeOffset? EmailSentAt { get; set; }
}
