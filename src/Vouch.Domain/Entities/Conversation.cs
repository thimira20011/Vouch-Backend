using Vouch.Domain.Enums;

namespace Vouch.Domain.Entities;

public class Conversation : BaseEntity
{
    public Guid UserAId { get; set; }
    public User UserA { get; set; } = null!;

    public Guid UserBId { get; set; }
    public User UserB { get; set; } = null!;

    public ConversationStatus Status { get; set; } = ConversationStatus.Active;
    public Guid? PausedByUserId { get; set; }
    public DateTimeOffset? PausedAt { get; set; }

    public int AdaptiveRevealThreshold { get; set; } = 40; // Architect adjustable (20 - 80)
    public int QualifyingMessageCount { get; set; } = 0; // Excludes messages < 5 chars
    public RevealClarityStage CurrentClarityStage { get; set; } = RevealClarityStage.Abstract0;

    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? InactivityNudgeSentAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
