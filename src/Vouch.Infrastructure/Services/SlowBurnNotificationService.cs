using Microsoft.AspNetCore.SignalR;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Vouch.Infrastructure.Services;

public class SlowBurnNotificationService : ISlowBurnNotificationService
{
    private readonly RealtimeDelivery _delivery;
    private readonly IApplicationDbContext _db;

    public SlowBurnNotificationService(RealtimeDelivery delivery, IApplicationDbContext db)
    {
        _delivery = delivery;
        _db = db;
    }

    public async Task NotifyMessageDeliveredAsync(
        Guid recipientUserId,
        Message message,
        CancellationToken cancellationToken = default)
    {
        await _delivery.SendAsync(
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
            userId: recipientUserId, conversationId: message.ConversationId, ct: cancellationToken);
    }

    public async Task NotifyClarityStageUpdatedAsync(
        Guid conversationId,
        RevealClarityStage newStage,
        CancellationToken cancellationToken = default)
    {
        await _delivery.SendAsync(
            "ClarityStageUpdated",
            new
            {
                ConversationId = conversationId,
                ClarityStage = newStage.ToString(),
                ClarityPercentage = (int)newStage
            },
            conversationId: conversationId, subscriptionRequired: true, ct: cancellationToken);
    }

    public async Task NotifyConversationPausedAsync(
        Guid recipientUserId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await _delivery.SendAsync(
            "ConversationPaused",
            new
            {
                ConversationId = conversationId,
                Message = "Your connection has chosen to pause this conversation for now. Take your time."
            },
            userId: recipientUserId, conversationId: conversationId, ct: cancellationToken);
    }

    public async Task NotifyConversationResumedAsync(
        Guid recipientUserId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await _delivery.SendAsync(
            "ConversationResumed",
            new
            {
                ConversationId = conversationId,
                Message = "The conversation has been resumed."
            },
            userId: recipientUserId, conversationId: conversationId, ct: cancellationToken);
    }

    public async Task NotifyTrustScoreAlertToArchitectAsync(
        Guid userId,
        double oldScore,
        double newScore,
        CancellationToken cancellationToken = default)
    {
        var campusId = await _db.Users.Where(u => u.Id == userId).Select(u => u.CampusId).SingleOrDefaultAsync(cancellationToken);
        await _delivery.SendAsync(
            "TrustScoreAnomalyDetected",
            new
            {
                UserId = userId,
                OldScore = oldScore,
                NewScore = newScore,
                Delta = Math.Round(newScore - oldScore, 2),
                DetectedAt = DateTimeOffset.UtcNow
            },
            architectCampusId: campusId, ct: cancellationToken);
    }

    /// <summary>
    /// REQ-23: Sends a gentle re-engagement nudge to both participants.
    /// Fired when a conversation has been silent for 21 days.
    /// </summary>
    public async Task NotifyInactivityNudgeAsync(
        Guid userAId,
        Guid userBId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            ConversationId = conversationId,
            Message = "It's been a while. No pressure — your conversation is still here whenever you're ready.",
            SentAt = DateTimeOffset.UtcNow
        };

        // Notify both participants independently via their personal user group
        await _delivery.SendAsync("InactivityNudge", payload, userId: userAId, conversationId: conversationId, ct: cancellationToken);

        await _delivery.SendAsync("InactivityNudge", payload, userId: userBId, conversationId: conversationId, ct: cancellationToken);
    }
}
