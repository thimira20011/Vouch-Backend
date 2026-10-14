using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common;
using Vouch.Application.Features.Notifications;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Services;

public sealed class NotificationService(ApplicationDbContext db, TimeProvider clock) : INotificationService
{
    public static IQueryable<Notification> CurrentSource(ApplicationDbContext db) => db.Notifications.Where(n =>
        n.CancelledAt == null && n.SubjectUser != null && n.SubjectUser.CampusId == n.CampusId && n.SubjectUser.Status != AccountStatus.DeletionRequested &&
        (n.Kind == NotificationKind.HighSeverityReport && n.RecipientUserId == null && n.Report != null && n.Report.Reporter.CampusId == n.CampusId && n.Report.ReportedUser.CampusId == n.CampusId ||
         n.Kind == NotificationKind.TrustScoreAnomaly && n.RecipientUserId == null ||
         n.Kind == NotificationKind.InactivityNudge && n.Conversation != null && n.Conversation.Status == ConversationStatus.Active &&
         n.Conversation.LastMessageAt == n.InactivityLastMessageAt &&
         n.Conversation.UserA.CampusId == n.CampusId && n.Conversation.UserB.CampusId == n.CampusId &&
         n.Conversation.UserA.Status == AccountStatus.Active && n.Conversation.UserB.Status == AccountStatus.Active &&
         n.Conversation.UserA.EmailVerifiedAt != null && n.Conversation.UserA.OnboardingCompletedAt != null &&
         n.Conversation.UserB.EmailVerifiedAt != null && n.Conversation.UserB.OnboardingCompletedAt != null &&
         (n.Conversation.UserAId == n.RecipientUserId || n.Conversation.UserBId == n.RecipientUserId) &&
         !db.Blocks.Any(b => b.BlockerId == n.Conversation.UserAId && b.BlockedUserId == n.Conversation.UserBId ||
                            b.BlockerId == n.Conversation.UserBId && b.BlockedUserId == n.Conversation.UserAId)));

    private async Task<User> RequireAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new EligibilityException("Account is unavailable.");
        if (user.Role == UserRole.Architect) await ResourceAccess.RequireArchitectAsync(db, userId, user.CampusId, ct);
        else MemberEligibility.RequireActive(user);
        return user;
    }
    private IQueryable<Notification> Visible(User user) => CurrentSource(db).Where(n => n.CampusId == user.CampusId &&
        (n.RecipientUserId == user.Id || n.RecipientUserId == null && user.Role == UserRole.Architect));

    public async Task<NotificationPage> GetAsync(Guid userId, int page = 1, int pageSize = 20, bool unreadOnly = false, CancellationToken ct = default)
    {
        var user = await RequireAsync(userId, ct);
        if (page is < 1 or > 10000 || pageSize is < 1 or > 50) throw new ArgumentException("Page must be 1–10000 and pageSize 1–50.");
        var query = Visible(user).AsNoTracking();
        var unread = query.Where(n => !db.NotificationReceipts.Any(r => r.NotificationId == n.Id && r.UserId == userId && r.ReadAt != null));
        var unreadCount = await unread.CountAsync(ct);
        if (unreadOnly) query = unread;
        var total = await query.CountAsync(ct);
        var notices = await query.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = notices.Select(n => n.Id).ToArray();
        var receipts = await db.NotificationReceipts.AsNoTracking().Where(r => ids.Contains(r.NotificationId) && r.UserId == userId).ToListAsync(ct);
        return new(notices.Select(n => new NotificationDto(n.Id, n.Kind, JsonSerializer.Deserialize<JsonElement>(n.Payload), n.CreatedAt,
            receipts.SingleOrDefault(r => r.NotificationId == n.Id)?.ReadAt, n.DeadlineAt)).ToList(), total, unreadCount, page, pageSize);
    }

    public async Task ReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        // Serialize with campus lifecycle writes so an acknowledgement cannot create a cross-campus receipt.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await RequireAsync(userId, ct);
        var campus = user.CampusId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\" = {user.CampusId} FOR UPDATE", ct);
        user = await RequireAsync(userId, ct);
        if (user.CampusId != campus) throw new EligibilityException("Account campus changed; retry the request.");
        if (!await Visible(user).AnyAsync(n => n.Id == notificationId, ct)) throw new EligibilityException("Notification is unavailable.");
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "NotificationReceipts" ("NotificationId", "UserId", "EmailAttempts", "ReadAt") VALUES ({notificationId}, {userId}, 0, {now})
            ON CONFLICT ("NotificationId", "UserId") DO UPDATE SET "ReadAt" = COALESCE("NotificationReceipts"."ReadAt", EXCLUDED."ReadAt")
            """, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<NotificationDeliveryStatus> GetDeliveryStatusAsync(Guid architectId, CancellationToken ct = default)
    {
        var architect = await RequireAsync(architectId, ct);
        await ResourceAccess.RequireArchitectAsync(db, architectId, architect.CampusId, ct);
        var high = CurrentSource(db).Where(n => n.CampusId == architect.CampusId && n.Kind == NotificationKind.HighSeverityReport);
        var pending = high.Where(n => n.EmailCompletedAt == null);
        var hasArchitect = await db.Users.AnyAsync(u => u.CampusId == architect.CampusId && u.Role == UserRole.Architect && u.Status == AccountStatus.Active, ct);
        var count = await pending.CountAsync(ct);
        return new(count, await pending.CountAsync(n => n.DeadlineAt < clock.GetUtcNow(), ct),
            await high.CountAsync(n => n.EmailCompletedAt != null && n.DeadlineMissedAt != null, ct), hasArchitect ? 0 : count,
            await pending.MinAsync(n => n.DeadlineAt, ct));
    }
}
