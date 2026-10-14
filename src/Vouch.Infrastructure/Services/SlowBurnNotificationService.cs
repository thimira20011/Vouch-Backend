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
    private readonly Vouch.Infrastructure.Persistence.ApplicationDbContext _db;

    private readonly NotificationWriter _notifications;
    private readonly TimeProvider _clock;
    public SlowBurnNotificationService(RealtimeDelivery delivery, Vouch.Infrastructure.Persistence.ApplicationDbContext db, NotificationWriter notifications, TimeProvider clock)
    {
        _delivery = delivery;
        _db = db;
        _notifications = notifications;
        _clock = clock;
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

    public async Task NotifyTrustScoreAlertToArchitectAsync(Guid userId, double oldScore, double newScore, CancellationToken cancellationToken = default)
    {
        var source = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new Vouch.Application.Common.EligibilityException("Account is unavailable.");
        var eventKey = FormattableString.Invariant($"direct:{userId}:{_clock.GetUtcNow().UtcTicks}:{oldScore:R}:{newScore:R}");
        await _notifications.QueueTrustAsync(source.CampusId, userId, oldScore, newScore, eventKey, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task NotifyInactivityNudgeAsync(Guid userAId, Guid userBId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await Vouch.Infrastructure.Security.ResourceAccess.VisibleConversations(_db, userAId).AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == conversationId && c.UserAId == userAId && c.UserBId == userBId && c.Status == ConversationStatus.Active, cancellationToken);
        if (conversation is null) return;
        var campus = await _db.Users.Where(u => u.Id == userAId).Select(u => u.CampusId).SingleAsync(cancellationToken);
        await _notifications.QueueInactivityAsync(conversation, campus, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }
}