using Vouch.Domain.Enums;

namespace Vouch.Application.Features.Messaging;

public record SendMessageRequest(
    Guid ConversationId,
    string Body
);

public record MessageDto(
    Guid Id,
    Guid SenderId,
    string SenderName,
    string Body,
    DateTimeOffset DeliveredAt,
    bool QualifiesForReveal
);

public record ConversationDto(
    Guid Id,
    Guid OtherUserId,
    string OtherUserName,
    string? OtherUserPhotoUrl,
    RevealClarityStage ClarityStage,
    int ValidMessageCount,
    int AdaptiveThreshold,
    ConversationStatus Status,
    bool IsPausedByMe,
    DateTimeOffset LastMessageAt
);

public interface IMessagingService
{
    Task<MessageDto> SendMessageAsync(Guid senderId, SendMessageRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationDto>> GetUserConversationsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<MessageDto>> GetConversationMessagesAsync(Guid userId, Guid conversationId, CancellationToken ct = default);
    Task<bool> PauseConversationAsync(Guid userId, Guid conversationId, CancellationToken ct = default);
    Task<bool> ResumeConversationAsync(Guid userId, Guid conversationId, CancellationToken ct = default);
    Task ProcessInactivityChecksAsync(CancellationToken ct = default); // 21 days nudge, 30 days archive
}
