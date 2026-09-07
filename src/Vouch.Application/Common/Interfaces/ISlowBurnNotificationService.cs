using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Application.Common.Interfaces;

public interface ISlowBurnNotificationService
{
    Task NotifyMessageDeliveredAsync(Guid recipientUserId, Message message, CancellationToken cancellationToken = default);
    Task NotifyClarityStageUpdatedAsync(Guid conversationId, RevealClarityStage newStage, CancellationToken cancellationToken = default);
    Task NotifyConversationPausedAsync(Guid recipientUserId, Guid conversationId, CancellationToken cancellationToken = default);
    Task NotifyConversationResumedAsync(Guid recipientUserId, Guid conversationId, CancellationToken cancellationToken = default);
    Task NotifyTrustScoreAlertToArchitectAsync(Guid userId, double oldScore, double newScore, CancellationToken cancellationToken = default);
}
