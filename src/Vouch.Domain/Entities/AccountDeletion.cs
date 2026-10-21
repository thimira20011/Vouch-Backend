namespace Vouch.Domain.Entities;

// Durable work and completion evidence. The live user FK is cleared on erasure.
public sealed class AccountDeletion : BaseEntity
{
    public Guid CampusId { get; set; }
    public Guid? UserId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset DeadlineAt { get; set; }
    public bool RequestTimeInferred { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? DeadlineMissedAt { get; set; }
    public string? LastErrorCode { get; set; }
    // Encrypted, request-bound restore tombstone, cleared by the original deletion deadline.
    public string? BackupSubjectId { get; set; }
    public string PolicyVersion { get; set; } = "erase-conversations-v1";
}
