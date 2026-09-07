namespace Vouch.Domain.Entities;

public class Message : BaseEntity
{
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;

    public Guid SenderId { get; set; }
    public User Sender { get; set; } = null!;

    public required string Body { get; set; }
    public bool QualifiesForRevealCounter { get; set; } // true if >= 5 characters
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DeliveredAt { get; set; } = DateTimeOffset.UtcNow;
}
