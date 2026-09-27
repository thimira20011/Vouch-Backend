using System.ComponentModel.DataAnnotations;
using Vouch.Domain.Enums;

namespace Vouch.Domain.Entities;

public class Message : BaseEntity
{
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;

    public Guid SenderId { get; set; }
    public User Sender { get; set; } = null!;

    [MaxLength(2000)]
    public required string Body { get; set; }

    /// <summary>
    /// Step 19 — REQ-16: Type of message.
    /// Only Text messages are permitted before the conversation reaches Full100 clarity.
    /// </summary>
    public MessageType Type { get; set; } = MessageType.Text;

    public bool QualifiesForRevealCounter { get; set; } // true if >= 5 characters
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DeliveredAt { get; set; } = DateTimeOffset.UtcNow;
}
