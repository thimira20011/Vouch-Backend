using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Messaging;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;

namespace Vouch.Infrastructure.Services;

public class MessagingService : IMessagingService
{
    private readonly IApplicationDbContext _context;
    private readonly ISlowBurnNotificationService _notificationService;

    public MessagingService(IApplicationDbContext context, ISlowBurnNotificationService notificationService)
    {
        _context = context;
        _notificationService = notificationService;
    }

    public async Task<MessageDto> SendMessageAsync(Guid senderId, SendMessageRequest request, CancellationToken ct = default)
    {
        var conversation = await _context.Conversations
            .Include(c => c.UserA)
            .Include(c => c.UserB)
            .FirstOrDefaultAsync(c => c.Id == request.ConversationId, ct)
            ?? throw new KeyNotFoundException("Conversation not found.");

        if (conversation.UserAId != senderId && conversation.UserBId != senderId)
        {
            throw new UnauthorizedAccessException("You are not a participant in this conversation.");
        }

        if (conversation.Status == ConversationStatus.Paused)
        {
            throw new InvalidOperationException("This conversation is currently paused. Please resume before sending letters.");
        }

        if (conversation.Status == ConversationStatus.Archived)
        {
            throw new InvalidOperationException("This conversation has been archived due to extended inactivity.");
        }

        var recipientId = conversation.UserAId == senderId ? conversation.UserBId : conversation.UserAId;

        // REQ-19: Message quality signals - messages under 5 characters do not increment reveal counter
        var qualifiesForCounter = SlowBurnCalculator.QualifiesForCounter(request.Body);

        var message = new Message
        {
            ConversationId = conversation.Id,
            SenderId = senderId,
            Body = request.Body,
            QualifiesForRevealCounter = qualifiesForCounter,
            DeliveredAt = DateTimeOffset.UtcNow
        };

        _context.Messages.Add(message);

        conversation.LastMessageAt = message.DeliveredAt;
        if (qualifiesForCounter)
        {
            conversation.QualifyingMessageCount += 1;

            // REQ-20: Evaluate reveal clarity stages: 25% at 40%, 60% at 70%, 100% at 100%
            var previousStage = conversation.CurrentClarityStage;
            var updatedStage = SlowBurnCalculator.DetermineClarityStage(
                conversation.QualifyingMessageCount,
                conversation.AdaptiveRevealThreshold);

            if (updatedStage != previousStage)
            {
                conversation.CurrentClarityStage = updatedStage;
                await _notificationService.NotifyClarityStageUpdatedAsync(conversation.Id, updatedStage, ct);
            }
        }

        await _context.SaveChangesAsync(ct);

        // Real-time letter delivery notification via SignalR (REQ-15)
        await _notificationService.NotifyMessageDeliveredAsync(recipientId, message, ct);

        var sender = conversation.UserAId == senderId ? conversation.UserA : conversation.UserB;

        return new MessageDto(
            Id: message.Id,
            SenderId: sender.Id,
            SenderName: sender.FullName,
            Body: message.Body,
            DeliveredAt: message.DeliveredAt,
            QualifiesForReveal: message.QualifiesForRevealCounter
        );
    }

    public async Task<IReadOnlyList<ConversationDto>> GetUserConversationsAsync(Guid userId, CancellationToken ct = default)
    {
        var conversations = await _context.Conversations
            .Include(c => c.UserA)
            .Include(c => c.UserB)
            .Where(c => c.UserAId == userId || c.UserBId == userId)
            .OrderByDescending(c => c.LastMessageAt)
            .ToListAsync(ct);

        return conversations.Select(c =>
        {
            var otherUser = c.UserAId == userId ? c.UserB : c.UserA;
            // Reveal photo depending on stage (REQ-17)
            var photoToDisplay = c.CurrentClarityStage == RevealClarityStage.Full100
                ? otherUser.OriginalPhotoUrl
                : otherUser.OilPaintingAbstractPhotoUrl;

            return new ConversationDto(
                Id: c.Id,
                OtherUserId: otherUser.Id,
                OtherUserName: otherUser.FullName,
                OtherUserPhotoUrl: photoToDisplay,
                ClarityStage: c.CurrentClarityStage,
                ValidMessageCount: c.QualifyingMessageCount,
                AdaptiveThreshold: c.AdaptiveRevealThreshold,
                Status: c.Status,
                IsPausedByMe: c.PausedByUserId == userId,
                LastMessageAt: c.LastMessageAt
            );
        }).ToList();
    }

    public async Task<IReadOnlyList<MessageDto>> GetConversationMessagesAsync(Guid userId, Guid conversationId, CancellationToken ct = default)
    {
        var conversation = await _context.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct)
            ?? throw new KeyNotFoundException("Conversation not found.");

        if (conversation.UserAId != userId && conversation.UserBId != userId)
        {
            throw new UnauthorizedAccessException("Not authorized to view these messages.");
        }

        var messages = await _context.Messages
            .Include(m => m.Sender)
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.SentAt)
            .Select(m => new MessageDto(
                m.Id,
                m.SenderId,
                m.Sender.FullName,
                m.Body,
                m.DeliveredAt,
                m.QualifiesForRevealCounter
            ))
            .ToListAsync(ct);

        return messages;
    }

    public async Task<bool> PauseConversationAsync(Guid userId, Guid conversationId, CancellationToken ct = default)
    {
        var conversation = await _context.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct)
            ?? throw new KeyNotFoundException("Conversation not found.");

        if (conversation.UserAId != userId && conversation.UserBId != userId)
        {
            throw new UnauthorizedAccessException("Not authorized.");
        }

        conversation.Status = ConversationStatus.Paused;
        conversation.PausedByUserId = userId;
        conversation.PausedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync(ct);

        var recipientId = conversation.UserAId == userId ? conversation.UserBId : conversation.UserAId;
        await _notificationService.NotifyConversationPausedAsync(recipientId, conversation.Id, ct);

        return true;
    }

    public async Task<bool> ResumeConversationAsync(Guid userId, Guid conversationId, CancellationToken ct = default)
    {
        var conversation = await _context.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct)
            ?? throw new KeyNotFoundException("Conversation not found.");

        if (conversation.UserAId != userId && conversation.UserBId != userId)
        {
            throw new UnauthorizedAccessException("Not authorized.");
        }

        conversation.Status = ConversationStatus.Active;
        conversation.PausedByUserId = null;
        conversation.PausedAt = null;

        await _context.SaveChangesAsync(ct);

        var recipientId = conversation.UserAId == userId ? conversation.UserBId : conversation.UserAId;
        await _notificationService.NotifyConversationResumedAsync(recipientId, conversation.Id, ct);

        return true;
    }

    public async Task ProcessInactivityChecksAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var twentyOneDaysAgo = now.AddDays(-21);
        var thirtyDaysAgo = now.AddDays(-30);

        // REQ-23: 30 days inactivity with no response -> archive (not delete)
        var toArchive = await _context.Conversations
            .Where(c => c.Status == ConversationStatus.Active && c.LastMessageAt <= thirtyDaysAgo)
            .ToListAsync(ct);

        foreach (var conv in toArchive)
        {
            conv.Status = ConversationStatus.Archived;
            conv.ArchivedAt = now;
        }

        // REQ-23: 21 days inactivity -> send gentle re-engagement nudge
        var toNudge = await _context.Conversations
            .Where(c => c.Status == ConversationStatus.Active &&
                        c.LastMessageAt <= twentyOneDaysAgo &&
                        c.InactivityNudgeSentAt == null)
            .ToListAsync(ct);

        foreach (var conv in toNudge)
        {
            conv.InactivityNudgeSentAt = now;
            // Send nudge notification
        }

        await _context.SaveChangesAsync(ct);
    }
}
