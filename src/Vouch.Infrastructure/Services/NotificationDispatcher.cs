using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.SignalR;

namespace Vouch.Infrastructure.Services;

public sealed class NotificationDispatcher(ApplicationDbContext db, IEmailService email, RealtimeDelivery realtime,
    TimeProvider clock, ILogger<NotificationDispatcher> logger)
{
    public async Task<int> ProcessDueAsync(int limit = 20, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentException("Delivery batch must be 1–100.");
        var now = clock.GetUtcNow();
        var late = await db.Notifications.Where(n => n.Kind == NotificationKind.HighSeverityReport && n.CancelledAt == null &&
            n.EmailCompletedAt == null && n.DeadlineAt < now && n.DeadlineMissedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.DeadlineMissedAt, now), ct);
        if (late > 0) logger.LogWarning("Notification email deadline missed. NewlyOverdue={Count}", late);
        var count = 0;
        for (; count < limit; count++)
        {
            var lease = Guid.NewGuid();
            var id = await ClaimAsync(lease, ct);
            if (id is null) break;
            try { await DeliverAsync(id.Value, lease, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // Lease expires; a new worker resumes it.
            catch (Exception)
            {
                // Keep provider messages, addresses, encrypted payloads and keys out of logs/status.
                await RetryAsync(id.Value, lease, "DELIVERY_FAILED", ct);
                logger.LogWarning("Notification delivery failed; retry scheduled. NotificationId={NotificationId}", id);
            }
        }
        return count;
    }

    private async Task<Guid?> ClaimAsync(Guid lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH due AS (
                SELECT "Id" FROM "Notifications"
                WHERE "CompletedAt" IS NULL AND "CancelledAt" IS NULL AND "NextAttemptAt" <= @now
                  AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= @now)
                ORDER BY "DeadlineAt" NULLS LAST, "CreatedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE "Notifications" n SET "LeaseId" = @lease, "LeaseUntil" = @until, "Attempts" = n."Attempts" + 1
            FROM due WHERE n."Id" = due."Id" RETURNING n."Id"
            """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)tx.GetDbTransaction());
        command.Parameters.AddWithValue("now", now); command.Parameters.AddWithValue("lease", lease);
        command.Parameters.AddWithValue("until", now.AddMinutes(2));
        var value = await command.ExecuteScalarAsync(ct);
        await tx.CommitAsync(ct); // No external publication is permitted before this/domain commits.
        return value is Guid id ? id : null;
    }

    private async Task RenewAsync(Guid id, Guid lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var changed = await db.Notifications.Where(n => n.Id == id && n.LeaseId == lease && n.LeaseUntil > now && n.CancelledAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.LeaseUntil, now.AddMinutes(2)), ct);
        if (changed != 1) throw new InvalidOperationException("Notification lease is unavailable.");
    }

    private async Task DeliverAsync(Guid id, Guid lease, CancellationToken ct)
    {
        if (!await NotificationService.CurrentSource(db).AnyAsync(n => n.Id == id, ct))
        {
            await db.Notifications.Where(n => n.Id == id && n.LeaseId == lease).ExecuteUpdateAsync(s => s
                .SetProperty(n => n.CancelledAt, clock.GetUtcNow()).SetProperty(n => n.LeaseId, (Guid?)null)
                .SetProperty(n => n.LeaseUntil, (DateTimeOffset?)null).SetProperty(n => n.LastErrorCode, "SOURCE_UNAVAILABLE"), ct);
            return;
        }
        var notice = await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == id && n.LeaseId == lease, ct);
        var payload = JsonSerializer.Deserialize<JsonElement>(notice.Payload);
        string? failure = null;
        if (notice.FirstPublishedAt == null)
        {
            try
            {
                await RenewAsync(id, lease, ct);
                var method = notice.Kind switch
                {
                    NotificationKind.HighSeverityReport => "HighSeverityReportSubmitted",
                    NotificationKind.TrustScoreAnomaly => "TrustScoreAnomalyDetected", _ => "InactivityNudge"
                };
                await realtime.SendAsync(method, payload, userId: notice.RecipientUserId, conversationId: notice.ConversationId,
                    architectCampusId: notice.RecipientUserId == null ? notice.CampusId : null,
                    subscriptionRequired: notice.RecipientUserId == null, ct: ct);
                notice.FirstPublishedAt = clock.GetUtcNow();
                await db.Notifications.Where(n => n.Id == id && n.LeaseId == lease)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.FirstPublishedAt, notice.FirstPublishedAt), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { failure = "REALTIME_FAILED"; }
        }
        if (notice.Kind == NotificationKind.HighSeverityReport && notice.EmailCompletedAt == null)
        {
            var architects = await Architects(notice.CampusId).Select(u => u.Id).ToListAsync(ct);
            if (architects.Count == 0) failure ??= "NO_ACTIVE_ARCHITECT";
            foreach (var architectId in architects)
            {
                await RenewAsync(id, lease, ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "NotificationReceipts" ("NotificationId", "UserId", "EmailAttempts") VALUES ({id}, {architectId}, 0)
                    ON CONFLICT ("NotificationId", "UserId") DO NOTHING
                    """, ct);
                if (await db.NotificationReceipts.AnyAsync(r => r.NotificationId == id && r.UserId == architectId && r.EmailSentAt != null, ct)) continue;
                var recipient = await Architects(notice.CampusId).AsNoTracking().SingleOrDefaultAsync(u => u.Id == architectId, ct);
                if (recipient is null || !await NotificationService.CurrentSource(db).AnyAsync(n => n.Id == id, ct)) continue;
                await db.NotificationReceipts.Where(r => r.NotificationId == id && r.UserId == architectId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.EmailAttempts, r => r.EmailAttempts + 1), ct);
                try
                {
                    // A transport timeout bounds each attempt; renew the lease between recipients.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await email.SendAsync(recipient.Email, "[Vouch Alert] High-severity report awaiting review",
                        $"Review report {notice.ReportId} in your campus Architect dashboard. Notification reference: {id}. No report details are included in email.", timeout.Token);
                    await db.NotificationReceipts.Where(r => r.NotificationId == id && r.UserId == architectId &&
                        db.Notifications.Any(n => n.Id == id && n.LeaseId == lease))
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.EmailSentAt, clock.GetUtcNow()), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { failure ??= "SMTP_FAILED"; }
            }
            var currentArchitects = Architects(notice.CampusId);
            var hasRecipients = await currentArchitects.AnyAsync(ct);
            var outstanding = await currentArchitects.AnyAsync(u => !db.NotificationReceipts.Any(r => r.NotificationId == id && r.UserId == u.Id && r.EmailSentAt != null), ct);
            if (hasRecipients && !outstanding)
            {
                notice.EmailCompletedAt = clock.GetUtcNow();
                var missed = notice.DeadlineMissedAt ?? (notice.EmailCompletedAt > notice.DeadlineAt ? notice.EmailCompletedAt : null);
                await db.Notifications.Where(n => n.Id == id && n.LeaseId == lease).ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.EmailCompletedAt, notice.EmailCompletedAt).SetProperty(n => n.DeadlineMissedAt, missed), ct);
            }
        }
        if (notice.FirstPublishedAt.HasValue && (notice.Kind != NotificationKind.HighSeverityReport || notice.EmailCompletedAt.HasValue))
        {
            await db.Notifications.Where(n => n.Id == id && n.LeaseId == lease).ExecuteUpdateAsync(s => s
                .SetProperty(n => n.CompletedAt, clock.GetUtcNow()).SetProperty(n => n.LeaseId, (Guid?)null)
                .SetProperty(n => n.LeaseUntil, (DateTimeOffset?)null).SetProperty(n => n.LastErrorCode, (string?)null), ct);
        }
        else await RetryAsync(id, lease, failure ?? "DELIVERY_PENDING", ct);
    }

    private IQueryable<User> Architects(Guid campus) => db.Users.Where(u => u.CampusId == campus && u.Role == UserRole.Architect && u.Status == AccountStatus.Active);

    private async Task RetryAsync(Guid id, Guid lease, string error, CancellationToken ct)
    {
        var state = await db.Notifications.AsNoTracking().Where(n => n.Id == id && n.LeaseId == lease)
            .Select(n => new { n.Attempts, n.DeadlineAt }).SingleOrDefaultAsync(ct);
        if (state is null) return;
        var now = clock.GetUtcNow();
        var delay = TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(state.Attempts - 1, 4))));
        if (state.DeadlineAt is { } deadline && deadline - now <= TimeSpan.FromMinutes(10)) delay = TimeSpan.FromMinutes(1);
        var next = now + delay;
        if (state.DeadlineAt > now && state.DeadlineAt < next) next = state.DeadlineAt.Value;
        await db.Notifications.Where(n => n.Id == id && n.LeaseId == lease).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.NextAttemptAt, next).SetProperty(n => n.LeaseId, (Guid?)null)
            .SetProperty(n => n.LeaseUntil, (DateTimeOffset?)null).SetProperty(n => n.LastErrorCode, error), ct);
    }
}
