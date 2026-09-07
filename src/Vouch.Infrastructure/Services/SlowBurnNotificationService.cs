using Microsoft.AspNetCore.SignalR;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.SignalR;

namespace Vouch.Infrastructure.Services;

public class SlowBurnNotificationService : ISlowBurnNotificationService
{
    private readonly IHubContext<VouchHub> _hubContext;

    public SlowBurnNotificationService(IHubContext<VouchHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyMessageDeliveredAsync(
        Guid recipientUserId,
        Message message,
        CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.Group($"user_{recipientUserId}").SendAsync(
            "ReceiveMessage",
            new
            {
                message.Id,
                message.ConversationId,
                message.SenderId,
                message.Body,
                message.DeliveredAt,
                message.QualifiesForRevealCounter
            },
            cancellationToken);
    }

    public async Task NotifyClarityStageUpdatedAsync(
        Guid conversationId,
        RevealClarityStage newStage,
        CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.Group($"conv_{conversationId}").SendAsync(
            "ClarityStageUpdated",
            new
            {
                ConversationId = conversationId,
                ClarityStage = newStage.ToString(),
                ClarityPercentage = (int)newStage
            },
            cancellationToken);
    }

    public async Task NotifyConversationPausedAsync(
        Guid recipientUserId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.Group($"user_{recipientUserId}").SendAsync(
            "ConversationPaused",
            new
            {
                ConversationId = conversationId,
                Message = "Your connection has chosen to pause this conversation for now. Take your time."
            },
            cancellationToken);
    }

    public async Task NotifyConversationResumedAsync(
        Guid recipientUserId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.Group($"user_{recipientUserId}").SendAsync(
            "ConversationResumed",
            new
            {
                ConversationId = conversationId,
                Message = "The conversation has been resumed."
            },
            cancellationToken);
    }

    public async Task NotifyTrustScoreAlertToArchitectAsync(
        Guid userId,
        double oldScore,
        double newScore,
        CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.Group("architect_alerts").SendAsync(
            "TrustScoreAnomalyDetected",
            new
            {
                UserId = userId,
                OldScore = oldScore,
                NewScore = newScore,
                Delta = Math.Round(newScore - oldScore, 2),
                DetectedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }
}
