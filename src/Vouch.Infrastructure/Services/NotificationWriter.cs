using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vouch.Domain.Entities;
using Vouch.Infrastructure.Persistence;

namespace Vouch.Infrastructure.Services;

// Adds work to the caller's context. The caller saves and commits it with the domain change.
public sealed class NotificationWriter(ApplicationDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);
    public async Task QueueReportAsync(Report report, Guid campusId, CancellationToken ct)
    {
        if (report.SeverityScore < 4) return;
        var notice = Create(campusId, NotificationKind.HighSeverityReport, $"report:{report.Id}");
        notice.ReportId = report.Id; notice.SubjectUserId = report.ReportedUserId;
        notice.DeadlineAt = report.CreatedAt.AddHours(1);
        notice.Payload = JsonSerializer.Serialize(new { NotificationId = notice.Id, ReportId = report.Id,
            report.Category, report.SeverityScore, SubmittedAt = report.CreatedAt }, PayloadOptions);
        await AddOnceAsync(notice, ct);
    }

    public async Task QueueTrustAsync(Guid campusId, Guid userId, double oldScore, double newScore, string eventKey, CancellationToken ct)
    {
        var notice = Create(campusId, NotificationKind.TrustScoreAnomaly, "trust:" + eventKey);
        notice.SubjectUserId = userId;
        notice.Payload = JsonSerializer.Serialize(new { NotificationId = notice.Id, UserId = userId, OldScore = oldScore,
            NewScore = newScore, Delta = Math.Round(newScore - oldScore, 2), DetectedAt = notice.CreatedAt }, PayloadOptions);
        await AddOnceAsync(notice, ct);
    }

    public async Task QueueInactivityAsync(Conversation conversation, Guid campusId, CancellationToken ct)
    {
        foreach (var recipient in new[] { conversation.UserAId, conversation.UserBId })
        {
            var notice = Create(campusId, NotificationKind.InactivityNudge, $"inactive:{conversation.Id}:{conversation.LastMessageAt.UtcTicks}:{recipient}");
            notice.RecipientUserId = recipient; notice.ConversationId = conversation.Id;
            notice.InactivityLastMessageAt = conversation.LastMessageAt;
            notice.SubjectUserId = recipient == conversation.UserAId ? conversation.UserBId : conversation.UserAId;
            notice.Payload = JsonSerializer.Serialize(new { NotificationId = notice.Id, ConversationId = conversation.Id,
                Message = "It's been a while. No pressure — your conversation is still here whenever you're ready.", SentAt = notice.CreatedAt }, PayloadOptions);
            await AddOnceAsync(notice, ct);
        }
    }

    private Notification Create(Guid campus, NotificationKind kind, string key)
    {
        var now = clock.GetUtcNow();
        return new() { CampusId = campus, Kind = kind, DedupeKey = key, Payload = "{}", CreatedAt = now, NextAttemptAt = now };
    }
    private async Task AddOnceAsync(Notification notice, CancellationToken ct)
    {
        if (!db.Notifications.Local.Any(n => n.DedupeKey == notice.DedupeKey) &&
            !await db.Notifications.AnyAsync(n => n.DedupeKey == notice.DedupeKey, ct)) db.Notifications.Add(notice);
    }
}
