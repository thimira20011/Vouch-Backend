using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Auth;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;

namespace Vouch.Infrastructure.Services;

public sealed partial class AccountDeletionProcessor(ApplicationDbContext db, TimeProvider clock,
    ILogger<AccountDeletionProcessor> logger, IRealtimeConnections? realtime = null)
{
    public async Task<int> ProcessDueAsync(int limit = 50, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentException("Deletion batch must be 1–100.");
        await ReconcileAsync(limit, ct);
        var now = clock.GetUtcNow();
        await db.AccountDeletions.Where(d => d.CompletedAt == null && d.DeadlineAt <= now && d.DeadlineMissedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeadlineMissedAt, now), ct);
        // Keep only the minimum restore tombstone during the supported backup window.
        await db.AccountDeletions.Where(d => d.CompletedAt != null && d.DeadlineAt <= now && d.BackupSubjectId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.BackupSubjectId, (string?)null), ct);
        await db.AccountDeletions.Where(d => d.CompletedAt <= now.AddDays(-90)).ExecuteDeleteAsync(ct);
        var count = 0;
        for (; count < limit; count++)
        {
            var lease = Guid.NewGuid();
            var id = await ClaimAsync(lease, ct); if (id is null) break;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                await DeleteAsync(id.Value, lease, timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                db.ChangeTracker.Clear();
                var attempts = await db.AccountDeletions.Where(d => d.Id == id).Select(d => d.Attempts).SingleAsync(ct);
                var next = clock.GetUtcNow().AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(attempts - 1, 6))));
                await db.AccountDeletions.Where(d => d.Id == id && d.LeaseId == lease && d.CompletedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, next).SetProperty(d => d.LeaseId, (Guid?)null)
                        .SetProperty(d => d.LeaseUntil, (DateTimeOffset?)null).SetProperty(d => d.LastErrorCode, "DELETION_FAILED"), ct);
                logger.LogWarning("Account deletion failed; durable retry scheduled. RequestId={RequestId}", id);
            }
        }
        return count;
    }

    private async Task ReconcileAsync(int limit, CancellationToken ct)
    {
        // Covers direct/imported status changes and interrupted legacy queue backfills.
        var missing = await db.Users.AsNoTracking().Where(u => (u.Status == AccountStatus.DeletionRequested || u.DeletionRequestedAt != null) &&
            !db.AccountDeletions.Any(d => d.UserId == u.Id && d.CompletedAt == null))
            .OrderBy(u => u.DeletionRequestedAt ?? u.CreatedAt).ThenBy(u => u.Id).Select(u => u.Id).Take(limit).ToListAsync(ct);
        foreach (var userId in missing)
        {
            try { await AccountDeletionRequests.RequestAsync(db, clock, userId, ct); }
            catch (KeyNotFoundException) { } // Another worker already erased it.
            db.ChangeTracker.Clear();
        }
    }

    private async Task<Guid?> ClaimAsync(Guid lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH due AS (
                SELECT "Id" FROM "AccountDeletions"
                WHERE "CompletedAt" IS NULL AND "NextAttemptAt" <= @now
                  AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= @now)
                ORDER BY "DeadlineAt", "RequestedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE "AccountDeletions" d SET "LeaseId"=@lease, "LeaseUntil"=@until,
                "Attempts"=d."Attempts"+1, "LastAttemptAt"=@now
            FROM due WHERE d."Id"=due."Id" RETURNING d."Id"
            """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)tx.GetDbTransaction());
        command.Parameters.AddWithValue("now", now); command.Parameters.AddWithValue("lease", lease);
        command.Parameters.AddWithValue("until", now.AddMinutes(2));
        var result = await command.ExecuteScalarAsync(ct); await tx.CommitAsync(ct);
        return result is Guid id ? id : null;
    }

    private async Task DeleteAsync(Guid id, Guid lease, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Production is one campus per database. Lock all existing campus rows in a consistent
        // order to also repair imported/cross-campus references without racing eligibility writes.
        var campuses = await db.Campuses.Select(c => c.Id).OrderBy(c => c).ToListAsync(ct);
        foreach (var campus in campuses)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\"={campus} FOR UPDATE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AccountDeletions\" WHERE \"Id\"={id} FOR UPDATE", ct);
        var job = await db.AccountDeletions.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.LeaseId == lease && d.CompletedAt == null, ct);
        if (job is null) return;
        var user = job.UserId.HasValue ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == job.UserId, ct) : null;
        if (user is not null)
        {
            var userId = user.Id;
            if (job.BackupSubjectId is null)
                await db.AccountDeletions.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.BackupSubjectId, DeletionTombstone.Encode(id, userId)), ct);
            var conversations = db.Conversations.Where(c => c.UserAId == userId || c.UserBId == userId).Select(c => c.Id);
            var messages = db.Messages.Where(m => m.SenderId == userId || conversations.Contains(m.ConversationId)).Select(m => m.Id);
            var reports = db.Reports.Where(r => r.ReporterId == userId || r.ReportedUserId == userId ||
                r.RelatedMessageId != null && messages.Contains(r.RelatedMessageId.Value)).Select(r => r.Id);
            await db.ModerationAudits.Where(a => a.ArchitectId == userId || a.SubjectUserId == userId || a.ReportId != null && reports.Contains(a.ReportId.Value)).ExecuteDeleteAsync(ct);
            await db.Reports.Where(r => reports.Contains(r.Id)).ExecuteDeleteAsync(ct);
            await db.Reports.Where(r => r.ResolvedByArchitectId == userId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ResolvedByArchitectId, (Guid?)null).SetProperty(r => r.ArchitectNotes, (string?)null), ct);
            await db.Messages.Where(m => messages.Contains(m.Id)).ExecuteDeleteAsync(ct);
            await db.Conversations.Where(c => conversations.Contains(c.Id)).ExecuteDeleteAsync(ct);
            await db.Vouches.Where(v => v.VoucherUserId == userId || v.TargetUserId == userId).ExecuteDeleteAsync(ct);
            await db.Blocks.Where(b => b.BlockerId == userId || b.BlockedUserId == userId).ExecuteDeleteAsync(ct);
            await db.Matches.Where(m => m.UserAId == userId || m.UserBId == userId).ExecuteDeleteAsync(ct);
            await db.AmbassadorInvites.Where(i => i.IssuedByArchitectId == userId || i.RedeemedByUserId == userId ||
                i.IntendedEmailLookupHash == user.EmailLookupHash).ExecuteDeleteAsync(ct);
            // Retire scalar actor references which are not foreign keys in the historical model.
            await db.Users.Where(u => u.AmbassadorApprovedByArchitectId == userId).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.AmbassadorApprovedByArchitectId, (Guid?)null).SetProperty(u => u.AmbassadorApprovedAt, (DateTimeOffset?)null), ct);
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
            // Owner FK cascades photos, sessions/refreshes, verification, requests,
            // inbox/receipts and subject audits. Conversation/report FKs cascade their notices.
            foreach (var campus in campuses) await db.RecalculateCampusAsync(campus, ct);
        }
        var completed = clock.GetUtcNow();
        await db.AccountDeletions.Where(d => d.Id == id && d.LeaseId == lease).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.UserId, (Guid?)null).SetProperty(d => d.CompletedAt, completed)
            .SetProperty(d => d.BackupSubjectId, d => completed >= d.DeadlineAt ? null : d.BackupSubjectId)
            .SetProperty(d => d.DeadlineMissedAt, d => d.DeadlineMissedAt ?? (completed >= d.DeadlineAt ? completed : (DateTimeOffset?)null))
            .SetProperty(d => d.LeaseId, (Guid?)null).SetProperty(d => d.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(d => d.LastErrorCode, (string?)null), ct);
        await tx.CommitAsync(ct);
        if (user is not null) realtime?.RevokeUser(user.Id);
        logger.LogInformation("Account deletion completed. RequestId={RequestId}", id);
    }

    public async Task<AccountDeletionStatus> GetStatusAsync(Guid campusId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow(); var query = db.AccountDeletions.Where(d => d.CampusId == campusId);
        return new(await query.CountAsync(d => d.CompletedAt == null, ct),
            await query.CountAsync(d => d.CompletedAt == null && d.RequestedAt <= now.AddDays(-1), ct),
            await query.CountAsync(d => d.CompletedAt == null && d.DeadlineAt <= now, ct),
            await query.CountAsync(d => d.CompletedAt != null, ct),
            await query.CountAsync(d => d.DeadlineMissedAt != null, ct));
    }
}
